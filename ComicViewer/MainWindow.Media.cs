using System.IO;
using System.Runtime;
using System.Windows.Media.Imaging;

namespace ComicViewer;

public partial class MainWindow
{
    private void StartMediaLoading(int currentPageIndex)
    {
        if (_archiveSession is null || Pages.Count == 0)
        {
            return;
        }

        _currentPageIndex = Math.Clamp(currentPageIndex, 0, Pages.Count - 1);

        lock (_cacheLock)
        {
            if (_mediaLoadTask is { IsCompleted: false })
            {
                return;
            }

            var loadCts = new CancellationTokenSource();
            _mediaLoadCts = loadCts;
            var generation = _cacheGeneration;
            _mediaLoadTask = Task.Run(() => LoadMediaUntilCacheIsFullAsync(generation, loadCts));
        }
    }

    private async Task LoadMediaUntilCacheIsFullAsync(int generation, CancellationTokenSource loadCts)
    {
        var cancellationToken = loadCts.Token;
        try
        {
            using var parallelGate = new SemaphoreSlim(MediaLoadParallelism);
            var workers = new List<Task>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var request = await Dispatcher.InvokeAsync(
                    () => CreateNextMediaRequest(generation),
                    System.Windows.Threading.DispatcherPriority.Background);

                if (request is null)
                {
                    break;
                }

                await parallelGate.WaitAsync(cancellationToken);
                workers.Add(Task.Run(async () =>
                {
                    try
                    {
                        await LoadOneMediaAsync(request, generation, cancellationToken);
                    }
                    finally
                    {
                        parallelGate.Release();
                    }
                }, cancellationToken));

                workers.RemoveAll(task => task.IsCompleted);
            }

            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                lock (_cacheLock)
                {
                    if (ReferenceEquals(_mediaLoadCts, loadCts))
                    {
                        _mediaLoadCts = null;
                        _mediaLoadTask = null;
                    }
                }

                loadCts.Dispose();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private async Task LoadOneMediaAsync(MediaLoadRequest request, int generation, CancellationToken cancellationToken)
    {
        try
        {
            var session = _archiveSession;
            var requestedPage = request.Page;
            if (session is null)
            {
                return;
            }

            if (requestedPage.IsImage)
            {
                using var encodedImage = session.CopyEntryToMemory(requestedPage.EntryKey, cancellationToken, MaxMediaCacheBytes);
                var imageData = TakeMemorySegment(encodedImage);
                await Dispatcher.InvokeAsync(
                    () => AcceptImage(request, imageData, generation),
                    System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            await Dispatcher.InvokeAsync(
                () =>
                {
                    if (IsFreshRequest(request, generation))
                    {
                        requestedPage.SetCoverLoadStatus(VideoCoverLoadStatus.Loading);
                    }
                },
                System.Windows.Threading.DispatcherPriority.Background);

            using var encodedMedia = session.CopyEntryToMemory(requestedPage.EntryKey, cancellationToken, MaxMediaCacheBytes);
            var videoData = TakeMemorySegment(encodedMedia);
            await Dispatcher.InvokeAsync(
                () => AcceptVideo(request, videoData, generation),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsFreshRequest(request, generation))
                {
                    FinishStaleRequest(request);
                    return;
                }

                FinishFailedRequest(request);
                if (request.Page.IsVideo)
                {
                    request.Page.SetCoverLoadStatus(VideoCoverLoadStatus.Failed);
                }

                StatusTextBlock.Text = $"{(request.Page.IsImage ? "图片" : "视频")}加载失败: {Path.GetFileName(request.Page.EntryKey)} - {ex.Message}";
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private MediaLoadRequest? CreateNextMediaRequest(int generation)
    {
        if (generation != _cacheGeneration || Pages.Count == 0)
        {
            return null;
        }

        var visiblePages = GetRealizedPageIndexes();
        lock (_cacheLock)
        {
            if (generation != _cacheGeneration)
            {
                return null;
            }

            foreach (var index in EnumeratePagesFrom(_currentPageIndex))
            {
                var page = Pages[index];
                if (_mediaCache.TryGetValue(page.EntryKey, out var cachedMedia))
                {
                    ApplyCachedMedia(page, cachedMedia);
                    continue;
                }

                if (_loadsInFlight.Contains(page.EntryKey) || _failedMedia.Contains(page.EntryKey))
                {
                    continue;
                }

                var isVisibleOrCurrent = index == _currentPageIndex || visiblePages.Contains(index);
                var reservedBytes = EstimateReservation(page);
                if (reservedBytes > MaxMediaCacheBytes)
                {
                    if (page.IsVideo)
                    {
                        page.SetCoverLoadStatus(VideoCoverLoadStatus.Oversized);
                    }

                    _failedMedia.Add(page.EntryKey);
                    continue;
                }

                if (_cacheBytes + _reservedCacheBytes >= MaxMediaCacheBytes && !isVisibleOrCurrent)
                {
                    return null;
                }

                _loadsInFlight.Add(page.EntryKey);
                _reservedCacheBytes += reservedBytes;
                return new MediaLoadRequest(index, page, reservedBytes);
            }
        }

        return null;
    }

    private void AcceptImage(MediaLoadRequest request, ArraySegment<byte> imageData, int generation)
    {
        if (!IsFreshRequest(request, generation))
        {
            FinishStaleRequest(request);
            return;
        }

        var page = Pages[request.PageIndex];
        StoreCachedMedia(new CachedMedia(page.EntryKey, request.PageIndex, page.MediaType, imageData, null, null, imageData.Count), request);
        page.SetEncodedImageData(imageData);
        ScheduleDecodeVisibleImages();
        EvictMediaOverBudget();
        UpdateReadingStatus(GetCurrentPageIndex());
    }

    private void AcceptVideo(MediaLoadRequest request, ArraySegment<byte> videoData, int generation)
    {
        if (!IsFreshRequest(request, generation))
        {
            FinishStaleRequest(request);
            return;
        }

        var page = Pages[request.PageIndex];
        StoreCachedMedia(new CachedMedia(page.EntryKey, request.PageIndex, page.MediaType, null, videoData, null, videoData.Count), request);
        page.SetCoverLoadStatus(VideoCoverLoadStatus.Loading);
        ScheduleVideoCoverGeneration();
        EvictMediaOverBudget();
        UpdateReadingStatus(GetCurrentPageIndex());
    }

    private bool IsFreshRequest(MediaLoadRequest request, int generation)
    {
        return generation == _cacheGeneration
            && request.PageIndex >= 0
            && request.PageIndex < Pages.Count
            && string.Equals(Pages[request.PageIndex].EntryKey, request.Page.EntryKey, StringComparison.Ordinal);
    }

    private void StoreCachedMedia(CachedMedia media, MediaLoadRequest request)
    {
        lock (_cacheLock)
        {
            _loadsInFlight.Remove(request.Page.EntryKey);
            _reservedCacheBytes = Math.Max(0, _reservedCacheBytes - request.ReservedBytes);
            if (_mediaCache.Remove(request.Page.EntryKey, out var oldMedia))
            {
                _cacheBytes -= oldMedia.EstimatedBytes;
            }

            _mediaCache[request.Page.EntryKey] = media;
            _cacheBytes += media.EstimatedBytes;
        }
    }

    private void FinishFailedRequest(MediaLoadRequest request)
    {
        lock (_cacheLock)
        {
            _loadsInFlight.Remove(request.Page.EntryKey);
            _reservedCacheBytes = Math.Max(0, _reservedCacheBytes - request.ReservedBytes);
            _failedMedia.Add(request.Page.EntryKey);
        }
    }

    private void FinishStaleRequest(MediaLoadRequest request)
    {
        lock (_cacheLock)
        {
            _loadsInFlight.Remove(request.Page.EntryKey);
            _reservedCacheBytes = Math.Max(0, _reservedCacheBytes - request.ReservedBytes);
        }
    }

    private void EvictMediaOverBudget()
    {
        var protectedIndexes = GetProtectedCacheIndexes();
        foreach (var page in Pages.Where(page => page.IsVideoPlaying))
        {
            protectedIndexes.Add(page.Index);
        }

        var evictedBytes = 0L;
        while (true)
        {
            CachedMedia? evicted;
            lock (_cacheLock)
            {
                if (_cacheBytes <= MaxMediaCacheBytes)
                {
                    break;
                }

                evicted = _mediaCache.Values
                    .Where(entry => !protectedIndexes.Contains(entry.PageIndex))
                    .OrderByDescending(entry => Math.Abs(entry.PageIndex - _currentPageIndex))
                    .ThenByDescending(entry => entry.EstimatedBytes)
                    .FirstOrDefault();

                if (evicted is null)
                {
                    break;
                }

                _mediaCache.Remove(evicted.EntryKey);
                _cacheBytes -= evicted.EstimatedBytes;
                evictedBytes += evicted.EstimatedBytes;
            }

            ClearPageMedia(evicted);
        }

        MaybeTrimReleasedCacheMemory(evictedBytes);
    }

    private void MaybeTrimReleasedCacheMemory(long evictedBytes)
    {
        if (evictedBytes < 64L * 1024 * 1024)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastCacheTrimUtc < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastCacheTrimUtc = now;
        _ = Task.Run(() =>
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
        });
    }

    private void ClearPageMedia(CachedMedia media)
    {
        if (media.PageIndex < 0 || media.PageIndex >= Pages.Count)
        {
            return;
        }

        var page = Pages[media.PageIndex];
        if (media.Type == ComicMediaType.Image && page.EncodedImageDataEquals(media.ImageData))
        {
            page.ClearEncodedImageData();
        }
    }

    private void ApplyCachedMedia(ComicPage page, CachedMedia cachedMedia)
    {
        if (cachedMedia.Type == ComicMediaType.Image && cachedMedia.ImageData is { } imageData)
        {
            page.SetEncodedImageData(imageData);
        }
        else if (cachedMedia.Type == ComicMediaType.Video && cachedMedia.VideoFrame is not null)
        {
            page.SetVideoFrame(cachedMedia.VideoFrame);
        }
        else if (cachedMedia.Type == ComicMediaType.Video && cachedMedia.VideoData is not null)
        {
            page.SetCoverLoadStatus(VideoCoverLoadStatus.Loading);
            ScheduleVideoCoverGeneration();
        }
    }

    private void ClearDecodedImagesForWiderDecode(int previousDecodePixelWidth, int currentDecodePixelWidth)
    {
        if (currentDecodePixelWidth <= previousDecodePixelWidth)
        {
            return;
        }

        foreach (var page in Pages.Where(page => page.IsImage && page.Image is not null))
        {
            page.ClearImage(_pageWidth);
        }
    }

    private void ClearMediaCache(bool clearPages)
    {
        CancelPendingImageDecode();
        lock (_cacheLock)
        {
            _mediaCache.Clear();
            _loadsInFlight.Clear();
            _decodesInFlight.Clear();
            _failedMedia.Clear();
            _cacheBytes = 0;
            _reservedCacheBytes = 0;
            _cacheGeneration++;
        }

        if (!clearPages)
        {
            return;
        }

        foreach (var page in Pages)
        {
            page.ClearEncodedImageData();
            page.ClearVideoFrame();
        }
    }

    private void StopMediaLoader()
    {
        CancelPendingImageDecode();
        CancellationTokenSource? cts;
        lock (_cacheLock)
        {
            cts = _mediaLoadCts;
            _mediaLoadCts = null;
            _mediaLoadTask = null;
            _loadsInFlight.Clear();
            _decodesInFlight.Clear();
            _reservedCacheBytes = 0;
        }

        cts?.Cancel();
    }

    private long EstimateReservation(ComicPage page)
    {
        if (page.IsVideo)
        {
            return page.EntrySize > 0 ? page.EntrySize : MaxMediaCacheBytes / 4;
        }

        return page.EntrySize > 0 ? page.EntrySize : 2L * 1024 * 1024;
    }

    private HashSet<int> GetDecodedImageIndexes()
    {
        var indexes = GetRealizedPageIndexes();
        if (indexes.Count == 0 && Pages.Count > 0)
        {
            indexes.Add(_currentPageIndex);
        }

        return indexes;
    }

    private HashSet<int> GetProtectedCacheIndexes()
    {
        var indexes = GetDecodedImageIndexes();
        if (_currentPageIndex >= 0 && _currentPageIndex < Pages.Count)
        {
            indexes.Add(_currentPageIndex);
        }

        return indexes;
    }

    private void RefreshDecodedImages()
    {
        ReleaseDecodedImagesOutsideRange();
        ScheduleDecodeVisibleImages();
    }

    private void ScheduleDecodeVisibleImages()
    {
        CancelPendingImageDecode();
        var decodeCts = new CancellationTokenSource();
        _imageDecodeDebounceCts = decodeCts;
        var generation = _cacheGeneration;
        _ = DecodeVisibleImagesAfterDelayAsync(generation, decodeCts);
    }

    private async Task DecodeVisibleImagesAfterDelayAsync(int generation, CancellationTokenSource decodeCts)
    {
        try
        {
            await Task.Delay(ImageDecodeDebounceMilliseconds, decodeCts.Token);
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_imageDecodeDebounceCts, decodeCts)
                    && !decodeCts.IsCancellationRequested
                    && generation == _cacheGeneration)
                {
                    _imageDecodeDebounceCts = null;
                    DecodeVisibleImages();
                }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (decodeCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (!ReferenceEquals(_imageDecodeDebounceCts, decodeCts))
            {
                decodeCts.Dispose();
            }
        }
    }

    private void CancelPendingImageDecode()
    {
        var decodeCts = _imageDecodeDebounceCts;
        _imageDecodeDebounceCts = null;
        decodeCts?.Cancel();
    }

    private void ReleaseDecodedImagesOutsideRange()
    {
        var keepDecoded = GetDecodedImageIndexes();
        foreach (var page in Pages.Where(page => page.IsImage && page.Image is not null && !keepDecoded.Contains(page.Index)))
        {
            page.ClearImage(_pageWidth);
        }
    }

    private static MemoryStream CreateReadOnlyMemoryStream(ArraySegment<byte> data)
    {
        return data.Array is null
            ? new MemoryStream(Array.Empty<byte>(), writable: false)
            : new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
    }

    private void DecodeVisibleImages()
    {
        var decodePixelWidth = GetDecodePixelWidth();
        foreach (var page in GetDecodedImageIndexes().Select(index => Pages[index]).Where(page => page.IsImage && page.Image is null && page.EncodedImageData is not null))
        {
            lock (_cacheLock)
            {
                if (!_decodesInFlight.Add(page.EntryKey))
                {
                    continue;
                }
            }

            var generation = _cacheGeneration;
            var encodedImageData = page.EncodedImageData!.Value;
            _ = Task.Run(() =>
            {
                try
                {
                    using var stream = CreateReadOnlyMemoryStream(encodedImageData);
                    var image = DecodeImage(stream, decodePixelWidth);
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        lock (_cacheLock)
                        {
                            _decodesInFlight.Remove(page.EntryKey);
                        }

                        if (generation == _cacheGeneration
                            && decodePixelWidth == GetDecodePixelWidth()
                            && page.EncodedImageDataEquals(encodedImageData)
                            && GetDecodedImageIndexes().Contains(page.Index))
                        {
                            page.SetImage(image, _pageWidth);
                        }
                        else
                        {
                            ScheduleDecodeVisibleImages();
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
                catch
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        lock (_cacheLock)
                        {
                            _decodesInFlight.Remove(page.EntryKey);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            });
        }
    }

    private static BitmapImage DecodeImage(Stream encodedStream, int decodePixelWidth)
    {
        if (encodedStream.CanSeek)
        {
            encodedStream.Position = 0;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = decodePixelWidth;
        image.StreamSource = encodedStream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
