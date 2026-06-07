using LibVLCSharp.Shared;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Readers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ComicViewer;

public partial class MainWindow : Window
{
    private const int MinimumDecodePixelWidth = 480;
    private const int MediaLoadParallelism = 2;
    private const long MaxMediaCacheBytes = 512L * 1024 * 1024;
    private const long MaxInMemoryVideoPlaybackBytes = MaxMediaCacheBytes;
    private const double MouseWheelScrollMultiplier = 5d;
    private const double MouseWheelPixelsPerLine = 16d;
    private static readonly TimeSpan CoverFrameTimeout = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp",
        ".tif",
        ".tiff"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mov",
        ".m4v",
        ".avi",
        ".wmv",
        ".mkv",
        ".webm"
    };

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedMedia> _mediaCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loadsInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _decodesInFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedMedia = new(StringComparer.Ordinal);
    private readonly LibVLC _libVlc;

    private ArchiveSession? _archiveSession;
    private CancellationTokenSource? _mediaLoadCts;
    private CancellationTokenSource? _videoPlayCts;
    private Task? _mediaLoadTask;
    private TaskCompletionSource<string?>? _passwordPrompt;
    private ScrollViewer? _pagesScrollViewer;
    private string? _archivePath;
    private double _pageWidth = 800;
    private int _cacheGeneration;
    private int _currentPageIndex;
    private long _cacheBytes;
    private long _reservedCacheBytes;
    private DateTime _lastCacheTrimUtc = DateTime.MinValue;
    private bool _isLoadingArchive;

    public ObservableCollection<ComicPage> Pages { get; } = [];

    public ObservableCollection<string> PasswordHistory { get; } = [];

    public MainWindow()
    {
        Core.Initialize();
        _libVlc = new LibVLC();

        InitializeComponent();
        DataContext = this;
        LoadPasswordHistory();

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            StopMediaLoader();
            CancelVideoPlayLoad();
            StopAllVideos();
            DisposeArchiveSession();
            _libVlc.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _pagesScrollViewer = FindVisualChild<ScrollViewer>(PagesListBox);
        UpdatePageWidth();

        var archivePath = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(archivePath) && File.Exists(archivePath))
        {
            await LoadArchiveWithPasswordRetryAsync(archivePath);
        }
    }

    private async void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择漫画压缩包",
            Filter = "漫画压缩包 (*.zip;*.rar)|*.zip;*.rar|ZIP 文件 (*.zip)|*.zip|RAR 文件 (*.rar)|*.rar|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadArchiveWithPasswordRetryAsync(dialog.FileName);
        }
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeRestoreWindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        MaximizeRestoreWindowButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        Dispatcher.BeginInvoke((Action)(() => UpdatePageWidth()));
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (PasswordOverlay.Visibility == Visibility.Visible || _isLoadingArchive || Pages.Count == 0)
        {
            return;
        }

        var offset = e.Key switch
        {
            Key.PageUp => -1,
            Key.PageDown => 1,
            _ => 0
        };

        if (offset == 0)
        {
            return;
        }

        ScrollPageToTop(Math.Clamp(GetCurrentPageIndex() + offset, 0, Pages.Count - 1));
        e.Handled = true;
    }

    private async Task LoadArchiveWithPasswordRetryAsync(string archivePath, string? initialPassword = null)
    {
        var password = initialPassword;
        var hasTriedKnownPasswords = false;
        var hasTriedAnyKnownPassword = false;

        while (true)
        {
            try
            {
                await LoadArchiveAsync(archivePath, password);
                return;
            }
            catch (Exception ex) when (IsLikelyPasswordProblem(ex))
            {
                ResetReader();
                _isLoadingArchive = false;
                SetLoadingState(false);

                if (!hasTriedKnownPasswords)
                {
                    hasTriedKnownPasswords = true;
                    var knownPasswords = GetKnownPasswordsToTry(password);
                    if (knownPasswords.Count > 0)
                    {
                        hasTriedAnyKnownPassword = true;
                        try
                        {
                            var knownPassword = await TryKnownPasswordsAsync(archivePath, knownPasswords);
                            if (knownPassword is not null)
                            {
                                await LoadArchiveAsync(archivePath, knownPassword);
                                return;
                            }
                        }
                        catch (Exception knownPasswordException)
                        {
                            ResetReader();
                            ShowOpenArchiveError(knownPasswordException);
                            return;
                        }
                    }
                }

                password = await ShowPasswordOverlayAsync(archivePath, hasTriedAnyKnownPassword);
                if (password is null)
                {
                    StatusTextBlock.Text = "已取消打开压缩包";
                    return;
                }
            }
            catch (Exception ex)
            {
                ResetReader();
                ShowOpenArchiveError(ex);
                return;
            }
            finally
            {
                _isLoadingArchive = false;
                SetLoadingState(false);
            }
        }
    }

    private List<string> GetKnownPasswordsToTry(string? skippedPassword)
    {
        return PasswordHistory
            .Where(password => !string.IsNullOrWhiteSpace(password))
            .Distinct(StringComparer.Ordinal)
            .Where(password => !string.Equals(password, skippedPassword, StringComparison.Ordinal))
            .ToList();
    }

    private async Task<string?> TryKnownPasswordsAsync(string archivePath, IReadOnlyList<string> knownPasswords)
    {
        _isLoadingArchive = true;
        SetLoadingState(true, $"正在尝试 {knownPasswords.Count} 个已知密码 ...");

        var nextPasswordIndex = -1;
        var hasResult = 0;
        var maxConcurrency = Math.Min(knownPasswords.Count, Math.Clamp(Environment.ProcessorCount / 2, 2, 4));
        var resultLock = new object();
        using var cancellation = new CancellationTokenSource();
        string? result = null;
        Exception? unexpectedException = null;

        async Task TryPasswordWorkerAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                var passwordIndex = Interlocked.Increment(ref nextPasswordIndex);
                if (passwordIndex >= knownPasswords.Count)
                {
                    return;
                }

                var password = knownPasswords[passwordIndex];
                try
                {
                    await Task.Run(() => ArchiveSession.VerifyPassword(archivePath, password), cancellation.Token);
                    if (Interlocked.CompareExchange(ref hasResult, 1, 0) == 0)
                    {
                        lock (resultLock)
                        {
                            result = password;
                        }

                        await cancellation.CancelAsync();
                    }

                    return;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (IsLikelyPasswordProblem(ex))
                {
                }
                catch (Exception ex)
                {
                    lock (resultLock)
                    {
                        unexpectedException ??= ex;
                    }

                    await cancellation.CancelAsync();
                    return;
                }
            }
        }

        var workers = Enumerable.Range(0, maxConcurrency)
            .Select(_ => TryPasswordWorkerAsync())
            .ToArray();

        await Task.WhenAll(workers);

        if (result is not null)
        {
            return result;
        }

        if (unexpectedException is not null)
        {
            throw unexpectedException;
        }

        return null;
    }

    private async Task LoadArchiveAsync(string archivePath, string? password)
    {
        _isLoadingArchive = true;
        SetLoadingState(true, $"正在读取目录 {Path.GetFileName(archivePath)} ...");
        ResetReader();
        UpdatePageWidth();

        var session = await Task.Run(() => ArchiveSession.Open(archivePath, password));
        ApplyArchiveSession(session, archivePath, password);
    }

    private void ApplyArchiveSession(ArchiveSession session, string archivePath, string? password)
    {
        var pages = session.MediaEntries
            .Select((entry, index) => new ComicPage(index, entry.Key, entry.Type, entry.Size, _pageWidth))
            .ToList();

        _archiveSession = session;
        _archivePath = archivePath;

        Pages.Clear();
        foreach (var page in pages)
        {
            Pages.Add(page);
        }

        GetPagesScrollViewer()?.ScrollToTop();
        if (Pages.Count == 0)
        {
            StatusTextBlock.Text = "压缩包中没有找到支持的图片或视频文件";
            return;
        }

        RememberPassword(password);
        UpdatePageWidth();
        UpdateReadingStatus(0);
        StartMediaLoading(0);
        RefreshDecodedImages();
    }

    private void ShowOpenArchiveError(Exception exception)
    {
        StatusTextBlock.Text = "打开失败";
        MessageBox.Show(this, exception.Message, "无法打开压缩包", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ResetReader()
    {
        _archivePath = null;
        StopMediaLoader();
        CancelVideoPlayLoad();
        StopAllVideos();
        DisposeArchiveSession();
        Pages.Clear();
    }

    private void DisposeArchiveSession()
    {
        ClearMediaCache(clearPages: false);
        var session = _archiveSession;
        _archiveSession = null;
        DisposeArchiveSessionInBackground(session);
    }

    private static void DisposeArchiveSessionInBackground(IDisposable? session)
    {
        if (session is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                session.Dispose();
            }
            catch
            {
            }
        });
    }

    private void SetLoadingState(bool isLoading, string? status = null)
    {
        OpenArchiveButton.IsEnabled = !isLoading;
        if (status is not null)
        {
            StatusTextBlock.Text = status;
        }
    }

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

            _mediaLoadCts = new CancellationTokenSource();
            var generation = _cacheGeneration;
            _mediaLoadTask = Task.Run(() => LoadMediaUntilCacheIsFullAsync(generation, _mediaLoadCts));
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
            if (session is null)
            {
                return;
            }

            if (request.Type == ComicMediaType.Image)
            {
                using var encodedImage = session.CopyEntryToMemory(request.EntryKey, cancellationToken);
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
                        request.Page.SetCoverLoadStatus(VideoCoverLoadStatus.Loading);
                    }
                },
                System.Windows.Threading.DispatcherPriority.Background);

            using var encodedMedia = session.CopyEntryToMemory(request.EntryKey, cancellationToken);
            var videoData = TakeMemorySegment(encodedMedia);
            var coverFrame = await TryRenderVideoCoverFrameAsync(
                request.Page,
                CreateReadOnlyMemoryStream(videoData),
                cancellationToken);
            await Dispatcher.InvokeAsync(
                () => AcceptVideo(request, videoData, coverFrame, generation),
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
                StatusTextBlock.Text = $"{(request.Type == ComicMediaType.Image ? "图片" : "视频")}加载失败: {Path.GetFileName(request.EntryKey)} - {ex.Message}";
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
                if (page.IsVideo && reservedBytes > MaxMediaCacheBytes)
                {
                    page.SetCoverLoadStatus(VideoCoverLoadStatus.Oversized);
                    _failedMedia.Add(page.EntryKey);
                    continue;
                }

                if (_cacheBytes + _reservedCacheBytes >= MaxMediaCacheBytes && !isVisibleOrCurrent)
                {
                    return null;
                }

                _loadsInFlight.Add(page.EntryKey);
                _reservedCacheBytes += reservedBytes;
                return new MediaLoadRequest(index, page, page.EntryKey, page.MediaType, reservedBytes);
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
        StoreCachedMedia(new CachedMedia(request.EntryKey, request.PageIndex, request.Type, imageData, null, null, imageData.Count), request);
        page.SetEncodedImageData(imageData);
        DecodeVisibleImages();
        EvictMediaOverBudget();
        UpdateReadingStatus(GetCurrentPageIndex());
    }

    private void AcceptVideo(MediaLoadRequest request, ArraySegment<byte> videoData, ImageSource? coverFrame, int generation)
    {
        if (!IsFreshRequest(request, generation))
        {
            FinishStaleRequest(request);
            return;
        }

        var page = Pages[request.PageIndex];
        StoreCachedMedia(new CachedMedia(request.EntryKey, request.PageIndex, request.Type, null, videoData, coverFrame, videoData.Count), request);
        if (coverFrame is not null && !page.IsVideoPlaying)
        {
            page.SetVideoFrame(coverFrame);
        }

        page.SetCoverLoadStatus(coverFrame is null ? VideoCoverLoadStatus.Failed : VideoCoverLoadStatus.Success);
        EvictMediaOverBudget();
        UpdateReadingStatus(GetCurrentPageIndex());
    }

    private bool IsFreshRequest(MediaLoadRequest request, int generation)
    {
        return generation == _cacheGeneration
            && request.PageIndex >= 0
            && request.PageIndex < Pages.Count
            && string.Equals(Pages[request.PageIndex].EntryKey, request.EntryKey, StringComparison.Ordinal);
    }

    private void StoreCachedMedia(CachedMedia media, MediaLoadRequest request)
    {
        lock (_cacheLock)
        {
            _loadsInFlight.Remove(request.EntryKey);
            _reservedCacheBytes = Math.Max(0, _reservedCacheBytes - request.ReservedBytes);
            if (_mediaCache.Remove(request.EntryKey, out var oldMedia))
            {
                _cacheBytes -= oldMedia.EstimatedBytes;
            }

            _mediaCache[request.EntryKey] = media;
            _cacheBytes += media.EstimatedBytes;
        }
    }

    private void FinishFailedRequest(MediaLoadRequest request)
    {
        lock (_cacheLock)
        {
            _loadsInFlight.Remove(request.EntryKey);
            _reservedCacheBytes = Math.Max(0, _reservedCacheBytes - request.ReservedBytes);
            _failedMedia.Add(request.EntryKey);
        }
    }

    private void FinishStaleRequest(MediaLoadRequest request)
    {
        lock (_cacheLock)
        {
            _loadsInFlight.Remove(request.EntryKey);
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
        else if (media.Type == ComicMediaType.Video && !page.IsVideoPlaying && ReferenceEquals(page.VideoFrame, media.VideoFrame))
        {
            page.ClearVideoFrame();
        }
    }

    private void ApplyCachedMedia(ComicPage page, CachedMedia cachedMedia)
    {
        if (cachedMedia.Type == ComicMediaType.Image && cachedMedia.ImageData is { } imageData)
        {
            page.SetEncodedImageData(imageData);
        }
        else if (cachedMedia.Type == ComicMediaType.Video && cachedMedia.VideoFrame is not null && page.VideoFrame is null)
        {
            page.SetVideoFrame(cachedMedia.VideoFrame);
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
            if (!page.IsVideoPlaying)
            {
                page.ClearVideoFrame();
            }
        }
    }

    private void StopMediaLoader()
    {
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
        DecodeVisibleImages();
    }

    private void ReleaseDecodedImagesOutsideRange()
    {
        var keepDecoded = GetDecodedImageIndexes();
        foreach (var page in Pages.Where(page => page.IsImage && page.Image is not null && !keepDecoded.Contains(page.Index)))
        {
            page.ClearImage(_pageWidth);
        }
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
                            DecodeVisibleImages();
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

    private async void PlayVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingArchive || sender is not Button { CommandParameter: ComicPage page } button || !page.IsVideo)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;
            if (page.PlaybackSession is not null && page.IsVideoPaused)
            {
                page.ResumeVideo();
            }
            else
            {
                await PlayVideoAsync(page);
            }
        }
        finally
        {
            button.IsEnabled = true;
            e.Handled = true;
        }
    }

    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ComicPage page } && page.IsVideoPlaying)
        {
            page.PauseVideo();
            e.Handled = true;
        }
    }

    private async Task PlayVideoAsync(ComicPage page)
    {
        var archivePath = _archivePath;
        if (archivePath is null)
        {
            return;
        }

        using var cachedPlaybackProbe = TryCreateCachedVideoStream(page.EntryKey);
        if (page.EntrySize > MaxInMemoryVideoPlaybackBytes && cachedPlaybackProbe is null)
        {
            StatusTextBlock.Text = $"视频过大，已阻止整段读入内存: {Path.GetFileName(page.EntryKey)}";
            MessageBox.Show(
                this,
                $"这个视频大小为 {FormatByteSize(page.EntrySize)}，超过当前内存播放上限 {FormatByteSize(MaxInMemoryVideoPlaybackBytes)}。\n\n为避免卡死或内存耗尽，程序不会把它整段读入内存，也不会写临时文件。",
                "视频过大",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var playCts = BeginVideoPlayLoad();
        var cancellationToken = playCts.Token;
        MemoryStream? videoStream = null;
        VideoPlaybackSession? session = null;
        var assignedToPage = false;

        try
        {
            StopOtherVideos(page);
            page.StopVideo();
            StatusTextBlock.Text = $"正在准备播放 {Path.GetFileName(page.EntryKey)} ...";

            videoStream = TryCreateCachedVideoStream(page.EntryKey)
                ?? await Task.Run(() => _archiveSession?.CopyEntryToMemory(page.EntryKey, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (videoStream is null || !IsVideoPlayRequestCurrent(playCts, archivePath, page))
            {
                return;
            }

            session = CreateVideoSession(page, videoStream, disableAudio: false);
            videoStream = null;
            page.SetVideoPlaybackSession(session);
            assignedToPage = true;
            AttachPlaybackEvents(page, session);
            page.IsVideoPlaying = true;
            page.IsVideoPaused = false;
            session.MediaPlayer.Mute = false;
            session.MediaPlayer.Volume = 100;

            if (!session.MediaPlayer.Play())
            {
                throw new InvalidOperationException("播放器没有成功启动视频。");
            }

            StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 使用内存播放";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            page.StopVideo();
            StatusTextBlock.Text = "视频播放失败";
            MessageBox.Show(this, ex.Message, "无法播放视频", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!assignedToPage)
            {
                session?.Dispose();
            }

            videoStream?.Dispose();
            FinishVideoPlayLoad(playCts);
        }
    }

    private MemoryStream? TryCreateCachedVideoStream(string entryKey)
    {
        lock (_cacheLock)
        {
            if (_mediaCache.TryGetValue(entryKey, out var cachedMedia) && cachedMedia.VideoData is { } videoData)
            {
                return CreateReadOnlyMemoryStream(videoData);
            }
        }

        return null;
    }

    private static ArraySegment<byte> TakeMemorySegment(MemoryStream stream)
    {
        if (stream.TryGetBuffer(out var buffer)
            && buffer.Array is not null
            && buffer.Offset == 0
            && buffer.Count == stream.Length)
        {
            return buffer;
        }

        return new ArraySegment<byte>(stream.ToArray());
    }

    private static MemoryStream CreateReadOnlyMemoryStream(ArraySegment<byte> data)
    {
        return data.Array is null
            ? new MemoryStream(Array.Empty<byte>(), writable: false)
            : new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
    }

    private async Task<ImageSource?> TryRenderVideoCoverFrameAsync(
        ComicPage page,
        MemoryStream videoStream,
        CancellationToken cancellationToken)
    {
        VideoPlaybackSession? session = null;
        ImageSource? coverFrame = null;
        var streamOwnedBySession = false;
        try
        {
            session = CreateVideoSession(page, videoStream, disableAudio: true, frame => coverFrame = frame, updatePageFrame: false);
            page.SetCoverSession(session);
            streamOwnedBySession = true;

            if (!session.MediaPlayer.Play())
            {
                return null;
            }

            await session.Renderer.FirstFrameDisplayed.WaitAsync(CoverFrameTimeout, cancellationToken);
            return coverFrame;
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            if (!streamOwnedBySession)
            {
                videoStream.Dispose();
            }

            if (session is not null)
            {
                page.DetachCoverSession(session);
                await Task.Run(() =>
                {
                    try
                    {
                        session.Stop();
                        session.Dispose();
                    }
                    catch
                    {
                    }
                }, CancellationToken.None);
            }
        }
    }

    private VideoPlaybackSession CreateVideoSession(
        ComicPage page,
        MemoryStream videoStream,
        bool disableAudio,
        Action<ImageSource>? onFrame = null,
        bool updatePageFrame = true)
    {
        var input = new StreamMediaInput(videoStream);
        var media = new VlcMedia(_libVlc, input);
        if (disableAudio)
        {
            media.AddOption(":no-audio");
        }

        var mediaPlayer = new VlcMediaPlayer(_libVlc);
        var renderer = new VideoFrameRenderer(
            frame =>
            {
                if (updatePageFrame)
                {
                    page.SetVideoFrame(frame);
                }

                onFrame?.Invoke(frame);
            },
            (width, height) => page.SetAspectRatio((double)height / width, _pageWidth));

        renderer.AttachTo(mediaPlayer);
        mediaPlayer.Media = media;
        return new VideoPlaybackSession(videoStream, input, media, mediaPlayer, renderer);
    }

    private void AttachPlaybackEvents(ComicPage page, VideoPlaybackSession session)
    {
        session.MediaPlayer.Playing += (_, _) => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.IsVideoPaused = false;
                StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 正在播放";
            }
        }));

        session.MediaPlayer.Paused += (_, _) => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.IsVideoPaused = true;
                StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 已暂停";
            }
        }));

        session.MediaPlayer.LengthChanged += (_, args) => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.SetVideoDuration(args.Length);
            }
        }));

        session.MediaPlayer.TimeChanged += (_, args) => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.SetVideoPosition(args.Time);
            }
        }));

        session.MediaPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.PlaybackSession == session)
            {
                page.StopVideo();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);

        session.MediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.PlaybackSession != session)
            {
                return;
            }

            page.StopVideo();
            StatusTextBlock.Text = "视频播放失败";
            MessageBox.Show(this, "播放器无法播放这个视频。", "视频播放失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }));
    }

    private CancellationTokenSource BeginVideoPlayLoad()
    {
        CancelVideoPlayLoad();
        _videoPlayCts = new CancellationTokenSource();
        return _videoPlayCts;
    }

    private void CancelVideoPlayLoad()
    {
        var cts = _videoPlayCts;
        _videoPlayCts = null;
        cts?.Cancel();
    }

    private void FinishVideoPlayLoad(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_videoPlayCts, cts))
        {
            _videoPlayCts = null;
        }

        cts.Dispose();
    }

    private bool IsVideoPlayRequestCurrent(CancellationTokenSource cts, string archivePath, ComicPage page)
    {
        return ReferenceEquals(_videoPlayCts, cts)
            && !cts.IsCancellationRequested
            && string.Equals(_archivePath, archivePath, StringComparison.Ordinal)
            && page.Index >= 0
            && page.Index < Pages.Count
            && ReferenceEquals(Pages[page.Index], page);
    }

    private void StopAllVideos()
    {
        foreach (var page in Pages)
        {
            page.StopVideo();
            page.StopCoverSession();
        }
    }

    private void StopOtherVideos(ComicPage currentPage)
    {
        foreach (var page in Pages)
        {
            if (!ReferenceEquals(page, currentPage))
            {
                page.StopVideo();
            }
        }
    }

    private void ImageScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePageWidth(e.NewSize.Width);
    }

    private void ImageScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isLoadingArchive || Pages.Count == 0)
        {
            return;
        }

        var currentPageIndex = GetCurrentPageIndex();
        _currentPageIndex = currentPageIndex;
        UpdateReadingStatus(currentPageIndex);
        StartMediaLoading(currentPageIndex);
        RefreshDecodedImages();
    }

    private void ImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = GetPagesScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        var wheelLines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
        var deltaSteps = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        var scrollPixels = deltaSteps * wheelLines * MouseWheelPixelsPerLine * MouseWheelScrollMultiplier;
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - scrollPixels);
        e.Handled = true;
    }

    private void UpdatePageWidth(double fallbackWidth = 0)
    {
        var previousDecodePixelWidth = GetDecodePixelWidth();
        var width = ResolvePageWidth(fallbackWidth);
        if (Math.Abs(width - _pageWidth) <= 0.1)
        {
            return;
        }

        _pageWidth = width;
        foreach (var page in Pages)
        {
            page.Resize(width);
        }

        if (Pages.Count > 0)
        {
            ClearDecodedImagesForWiderDecode(previousDecodePixelWidth, GetDecodePixelWidth());
            StartMediaLoading(GetCurrentPageIndex());
            RefreshDecodedImages();
        }
    }

    private double ResolvePageWidth(double fallbackWidth = 0)
    {
        var scrollViewer = GetPagesScrollViewer();
        var width = scrollViewer?.ViewportWidth ?? 0;
        if (double.IsNaN(width) || width <= 1)
        {
            width = fallbackWidth;
        }

        if (double.IsNaN(width) || width <= 1)
        {
            width = PagesListBox.ActualWidth;
        }

        if (double.IsNaN(width) || width <= 1)
        {
            width = ActualWidth;
        }

        return Math.Max(1, width);
    }

    private int GetDecodePixelWidth()
    {
        return Math.Max(MinimumDecodePixelWidth, (int)Math.Ceiling(_pageWidth * VisualTreeHelper.GetDpi(this).DpiScaleX));
    }

    private ScrollViewer? GetPagesScrollViewer()
    {
        if (_pagesScrollViewer is not null)
        {
            return _pagesScrollViewer;
        }

        PagesListBox.ApplyTemplate();
        _pagesScrollViewer = FindVisualChild<ScrollViewer>(PagesListBox);
        return _pagesScrollViewer;
    }

    private int GetCurrentPageIndex()
    {
        return TryGetFirstVisiblePageIndex() ?? GetPageIndexAtOffset(GetPagesScrollViewer()?.VerticalOffset ?? 0);
    }

    private int GetPageIndexAtOffset(double offset)
    {
        var top = 0d;
        for (var i = 0; i < Pages.Count; i++)
        {
            var pageHeight = Math.Max(1, Pages[i].DisplayHeight);
            if (offset < top + pageHeight)
            {
                return i;
            }

            top += pageHeight;
        }

        return Math.Max(0, Pages.Count - 1);
    }

    private void ScrollPageToTop(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
        {
            return;
        }

        PagesListBox.ScrollIntoView(Pages[pageIndex]);
        Dispatcher.BeginInvoke((Action)(() =>
        {
            AlignRealizedPageToTop(pageIndex);
            _currentPageIndex = pageIndex;
            UpdateReadingStatus(pageIndex);
            StartMediaLoading(pageIndex);
            RefreshDecodedImages();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void AlignRealizedPageToTop(int pageIndex)
    {
        var scrollViewer = GetPagesScrollViewer();
        if (scrollViewer is null)
        {
            return;
        }

        PagesListBox.UpdateLayout();
        if (PagesListBox.ItemContainerGenerator.ContainerFromIndex(pageIndex) is not FrameworkElement container)
        {
            return;
        }

        try
        {
            var bounds = container.TransformToAncestor(scrollViewer)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + bounds.Top);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private int? TryGetFirstVisiblePageIndex()
    {
        var scrollViewer = GetPagesScrollViewer();
        if (scrollViewer is null || Pages.Count == 0)
        {
            return null;
        }

        var bestIndex = -1;
        var bestTop = double.PositiveInfinity;
        foreach (var container in FindVisualChildren<ListBoxItem>(PagesListBox))
        {
            var index = PagesListBox.ItemContainerGenerator.IndexFromContainer(container);
            if (index < 0 || index >= Pages.Count || container.ActualHeight <= 0)
            {
                continue;
            }

            Rect bounds;
            try
            {
                bounds = container.TransformToAncestor(scrollViewer)
                    .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (bounds.Bottom <= 0 || bounds.Top >= scrollViewer.ViewportHeight)
            {
                continue;
            }

            if (bounds.Top < bestTop)
            {
                bestTop = bounds.Top;
                bestIndex = index;
            }
        }

        return bestIndex >= 0 ? bestIndex : null;
    }

    private HashSet<int> GetRealizedPageIndexes()
    {
        var indexes = new HashSet<int>();
        foreach (var container in FindVisualChildren<ListBoxItem>(PagesListBox))
        {
            var index = PagesListBox.ItemContainerGenerator.IndexFromContainer(container);
            if (index >= 0 && index < Pages.Count)
            {
                indexes.Add(index);
            }
        }

        return indexes;
    }

    private IEnumerable<int> EnumeratePagesFrom(int currentPageIndex)
    {
        return Enumerable.Range(0, Pages.Count)
            .OrderBy(index => GetReadingDistance(index, currentPageIndex))
            .ThenBy(index => Math.Abs(index - currentPageIndex));
    }

    private static double GetReadingDistance(int pageIndex, int currentPageIndex)
    {
        var offset = pageIndex - currentPageIndex;
        return offset >= 0 ? offset / 2d : -offset;
    }

    private void UpdateReadingStatus(int currentPageIndex)
    {
        if (_archivePath is null || Pages.Count == 0)
        {
            return;
        }

        StatusTextBlock.Text = $"{Path.GetFileName(_archivePath)} - 第 {currentPageIndex + 1}/{Pages.Count} 页，已载入 {GetLoadedRangeText()}";
    }

    private string GetLoadedRangeText()
    {
        var loaded = Pages
            .Where(page => page.IsImage ? page.HasEncodedImageData : page.IsLoaded)
            .Select(page => page.Index + 1)
            .ToList();
        if (loaded.Count == 0)
        {
            return "0";
        }

        return loaded.Count == 1
            ? loaded[0].ToString(CultureInfo.InvariantCulture)
            : $"{loaded.Min()}-{loaded.Max()} ({loaded.Count})";
    }

    private static string FormatByteSize(long bytes)
    {
        return bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024d / 1024d / 1024d:0.0}GB"
            : $"{bytes / 1024d / 1024d:0.0}MB";
    }

    private Task<string?> ShowPasswordOverlayAsync(string archivePath, bool knownPasswordsTried)
    {
        _passwordPrompt?.TrySetResult(null);
        _passwordPrompt = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PasswordPromptTextBlock.Text = knownPasswordsTried
            ? "所有已知密码均无法解锁，请输入正确密码"
            : "请输入压缩包密码";
        PasswordArchiveNameTextBlock.Text = Path.GetFileName(archivePath);
        ArchivePasswordBox.Clear();
        PasswordOpenButton.IsEnabled = false;
        PasswordOverlay.Visibility = Visibility.Visible;
        OpenArchiveButton.IsEnabled = false;
        Dispatcher.BeginInvoke((Action)(() => ArchivePasswordBox.Focus()));
        return _passwordPrompt.Task;
    }

    private void CompletePasswordPrompt(string? password)
    {
        var prompt = _passwordPrompt;
        if (prompt is null)
        {
            return;
        }

        _passwordPrompt = null;
        PasswordOverlay.Visibility = Visibility.Collapsed;
        ArchivePasswordBox.Clear();
        OpenArchiveButton.IsEnabled = true;
        prompt.TrySetResult(password);
    }

    private void PasswordOpenButton_Click(object sender, RoutedEventArgs e)
    {
        CompletePasswordPrompt(ArchivePasswordBox.Password);
    }

    private void PasswordCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CompletePasswordPrompt(null);
    }

    private void ArchivePasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(ArchivePasswordBox.Password))
        {
            CompletePasswordPrompt(ArchivePasswordBox.Password);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CompletePasswordPrompt(null);
            e.Handled = true;
        }
    }

    private void ArchivePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        PasswordOpenButton.IsEnabled = !string.IsNullOrWhiteSpace(ArchivePasswordBox.Password);
    }

    private static string PasswordHistoryFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ComicViewer", "password-history.json");

    private void LoadPasswordHistory()
    {
        try
        {
            if (!File.Exists(PasswordHistoryFilePath))
            {
                return;
            }

            var passwords = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PasswordHistoryFilePath));
            foreach (var password in passwords?.Where(password => !string.IsNullOrWhiteSpace(password)).Distinct() ?? [])
            {
                PasswordHistory.Add(password);
            }
        }
        catch
        {
        }
    }

    private void RememberPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var existing = PasswordHistory.FirstOrDefault(item => string.Equals(item, password, StringComparison.Ordinal));
        if (existing is not null)
        {
            PasswordHistory.Remove(existing);
        }

        PasswordHistory.Insert(0, password);
        while (PasswordHistory.Count > 50)
        {
            PasswordHistory.RemoveAt(PasswordHistory.Count - 1);
        }

        SavePasswordHistory();
    }

    private void SavePasswordHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PasswordHistoryFilePath)!);
            File.WriteAllText(PasswordHistoryFilePath, JsonSerializer.Serialize(PasswordHistory.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
        }
    }

    private static bool IsImageFile(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName) && ImageExtensions.Contains(Path.GetExtension(fileName));
    }

    private static bool IsVideoFile(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName) && VideoExtensions.Contains(Path.GetExtension(fileName));
    }

    private static bool IsSupportedMediaFile(string? fileName)
    {
        return IsImageFile(fileName) || IsVideoFile(fileName);
    }

    private static ComicMediaType GetMediaType(string fileName)
    {
        return IsVideoFile(fileName) ? ComicMediaType.Video : ComicMediaType.Image;
    }

    private static bool IsLikelyPasswordProblem(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("password", StringComparison.OrdinalIgnoreCase)
            || message.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("crypt", StringComparison.OrdinalIgnoreCase)
            || message.Contains("crc", StringComparison.OrdinalIgnoreCase)
            || message.Contains("data error", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nestedChild in FindVisualChildren<T>(child))
            {
                yield return nestedChild;
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
            {
                return typedChild;
            }

            var nestedChild = FindVisualChild<T>(child);
            if (nestedChild is not null)
            {
                return nestedChild;
            }
        }

        return null;
    }

    private sealed record CachedMedia(
        string EntryKey,
        int PageIndex,
        ComicMediaType Type,
        ArraySegment<byte>? ImageData,
        ArraySegment<byte>? VideoData,
        ImageSource? VideoFrame,
        long EstimatedBytes);

    private sealed record MediaLoadRequest(
        int PageIndex,
        ComicPage Page,
        string EntryKey,
        ComicMediaType Type,
        long ReservedBytes);

    private sealed class ArchiveSession : IDisposable
    {
        private readonly IArchive _archive;
        private readonly List<IArchiveEntry> _entries;
        private readonly SemaphoreSlim _archiveLock = new(1, 1);
        private bool _isDisposed;

        private ArchiveSession(IArchive archive, List<IArchiveEntry> entries, IReadOnlyList<ComicArchiveEntry> mediaEntries)
        {
            _archive = archive;
            _entries = entries;
            MediaEntries = mediaEntries;
        }

        public IReadOnlyList<ComicArchiveEntry> MediaEntries { get; }

        public static ArchiveSession Open(string archivePath, string? password)
        {
            var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions
            {
                Password = password
            });

            try
            {
                var entries = archive.Entries
                    .Where(entry => !entry.IsDirectory && entry.Key is not null)
                    .ToList();
                var mediaEntries = entries
                    .Where(entry => IsSupportedMediaFile(entry.Key))
                    .OrderBy(entry => entry.Key, NaturalFileNameComparer.Instance)
                    .Select(entry => new ComicArchiveEntry(entry.Key!, GetMediaType(entry.Key!), entry.Size))
                    .ToList();

                var firstEntry = mediaEntries.FirstOrDefault();
                if (firstEntry is not null)
                {
                    using var stream = entries.First(entry => string.Equals(entry.Key, firstEntry.Key, StringComparison.Ordinal)).OpenEntryStream();
                    _ = stream.ReadByte();
                }

                return new ArchiveSession(archive, entries, mediaEntries);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }

        public static void VerifyPassword(string archivePath, string password)
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions
            {
                Password = password
            });

            var firstMediaEntry = archive.Entries
                .FirstOrDefault(entry => !entry.IsDirectory && entry.Key is not null && IsSupportedMediaFile(entry.Key));
            if (firstMediaEntry is not null)
            {
                using var stream = firstMediaEntry.OpenEntryStream();
                _ = stream.ReadByte();
            }
        }

        public MemoryStream CopyEntryToMemory(string entryKey, CancellationToken cancellationToken)
        {
            _archiveLock.Wait(cancellationToken);
            try
            {
                ThrowIfDisposed();
                var entry = _entries.FirstOrDefault(entry => string.Equals(entry.Key, entryKey, StringComparison.Ordinal))
                    ?? throw new FileNotFoundException("Entry not found in archive.", entryKey);

                using var source = entry.OpenEntryStream();
                var destination = entry.Size is > 0 and <= int.MaxValue
                    ? new MemoryStream(checked((int)entry.Size))
                    : new MemoryStream();
                CopyToMemory(source, destination, cancellationToken);
                destination.Position = 0;
                return destination;
            }
            finally
            {
                _archiveLock.Release();
            }
        }

        private static void CopyToMemory(Stream source, MemoryStream destination, CancellationToken cancellationToken)
        {
            var buffer = new byte[128 * 1024];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytesRead = source.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    return;
                }

                destination.Write(buffer, 0, bytesRead);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(ArchiveSession));
            }
        }

        public void Dispose()
        {
            _archiveLock.Wait();
            try
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                _archive.Dispose();
            }
            finally
            {
                _archiveLock.Release();
                _archiveLock.Dispose();
            }
        }
    }
}

public sealed class ComicPage : INotifyPropertyChanged
{
    private const double DefaultAspectRatio = 1.45;
    private const double DefaultVideoAspectRatio = 9d / 16d;

    private ArraySegment<byte>? _encodedImageData;
    private BitmapImage? _image;
    private VideoPlaybackSession? _videoPlaybackSession;
    private VideoPlaybackSession? _coverSession;
    private ImageSource? _videoFrame;
    private bool _isVideoPaused;
    private bool _isVideoPlaying;
    private long _videoPositionMs;
    private long _videoDurationMs;
    private double _aspectRatio;
    private double _displayWidth;
    private double _displayHeight;
    private VideoCoverLoadStatus _coverLoadStatus;

    public ComicPage(int index, string entryKey, ComicMediaType mediaType, long entrySize, double pageWidth)
    {
        Index = index;
        EntryKey = entryKey;
        MediaType = mediaType;
        EntrySize = entrySize;
        _aspectRatio = IsVideo ? DefaultVideoAspectRatio : DefaultAspectRatio;
        Resize(pageWidth);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public string EntryKey { get; }

    public long EntrySize { get; }

    public ComicMediaType MediaType { get; }

    public bool IsImage => MediaType == ComicMediaType.Image;

    public bool IsVideo => MediaType == ComicMediaType.Video;

    public bool IsImageLoaded => Image is not null;

    public bool IsLoaded => IsImage ? Image is not null : VideoFrame is not null;

    public ArraySegment<byte>? EncodedImageData => _encodedImageData;

    public bool HasEncodedImageData => _encodedImageData is not null;

    public string DisplayNumber => (Index + 1).ToString(CultureInfo.InvariantCulture);

    public VideoPlaybackSession? PlaybackSession => _videoPlaybackSession;

    public BitmapImage? Image
    {
        get => _image;
        private set
        {
            if (!ReferenceEquals(_image, value))
            {
                _image = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsImageLoaded));
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public ImageSource? VideoFrame
    {
        get => _videoFrame;
        private set
        {
            if (!ReferenceEquals(_videoFrame, value))
            {
                _videoFrame = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public bool IsVideoPlaying
    {
        get => _isVideoPlaying;
        set
        {
            if (_isVideoPlaying != value)
            {
                _isVideoPlaying = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VideoOverlayText));
            }
        }
    }

    public bool IsVideoPaused
    {
        get => _isVideoPaused;
        set
        {
            if (_isVideoPaused != value)
            {
                _isVideoPaused = value;
                OnPropertyChanged();
            }
        }
    }

    public string VideoTimeText => IsVideo
        ? $"{FormatVideoTime(_videoPositionMs)}/{FormatVideoTime(_videoDurationMs)}"
        : "";

    public string VideoOverlayText => !IsVideo
        ? ""
        : !IsVideoPlaying
            ? _coverLoadStatus switch
            {
                VideoCoverLoadStatus.Oversized => "视频过大，点击后加载播放",
                VideoCoverLoadStatus.Loading => "正在加载封面...",
                VideoCoverLoadStatus.Failed => "封面加载失败，点击播放",
                VideoCoverLoadStatus.Success => "点击播放",
                _ => "等待加载封面..."
            }
            : VideoTimeText;

    public double DisplayWidth
    {
        get => _displayWidth;
        private set
        {
            if (Math.Abs(_displayWidth - value) > 0.1)
            {
                _displayWidth = value;
                OnPropertyChanged();
            }
        }
    }

    public double DisplayHeight
    {
        get => _displayHeight;
        private set
        {
            if (Math.Abs(_displayHeight - value) > 0.1)
            {
                _displayHeight = value;
                OnPropertyChanged();
            }
        }
    }

    public void SetImage(BitmapImage? image, double pageWidth)
    {
        Image = image;
        if (image?.PixelWidth > 0)
        {
            _aspectRatio = (double)image.PixelHeight / image.PixelWidth;
        }

        Resize(pageWidth);
    }

    public void ClearImage(double pageWidth)
    {
        Image = null;
        Resize(pageWidth);
    }

    public void SetEncodedImageData(ArraySegment<byte> encodedImageData)
    {
        if (!EncodedImageDataEquals(encodedImageData))
        {
            _encodedImageData = encodedImageData;
        }
    }

    public bool EncodedImageDataEquals(ArraySegment<byte>? encodedImageData)
    {
        if (_encodedImageData is not { } current || encodedImageData is not { } other)
        {
            return _encodedImageData is null && encodedImageData is null;
        }

        return ReferenceEquals(current.Array, other.Array)
            && current.Offset == other.Offset
            && current.Count == other.Count;
    }

    public void ClearEncodedImageData()
    {
        _encodedImageData = null;
        Image = null;
    }

    public void SetVideoFrame(ImageSource frame)
    {
        SetCoverLoadStatus(VideoCoverLoadStatus.Success);
        VideoFrame = frame;
        OnPropertyChanged(nameof(VideoOverlayText));
    }

    public void ClearVideoFrame()
    {
        VideoFrame = null;
        OnPropertyChanged(nameof(VideoOverlayText));
    }

    public void SetCoverLoadStatus(VideoCoverLoadStatus status)
    {
        if (_coverLoadStatus != status)
        {
            _coverLoadStatus = status;
            OnPropertyChanged(nameof(VideoOverlayText));
        }
    }

    public void SetVideoPlaybackSession(VideoPlaybackSession session)
    {
        _videoPlaybackSession = session;
    }

    public void SetCoverSession(VideoPlaybackSession session)
    {
        DisposeSessionInBackground(_coverSession, stopFirst: true);
        _coverSession = session;
    }

    public void DetachCoverSession(VideoPlaybackSession session)
    {
        if (ReferenceEquals(_coverSession, session))
        {
            _coverSession = null;
        }
    }

    public void StopCoverSession()
    {
        DisposeSessionInBackground(_coverSession, stopFirst: true);
        _coverSession = null;
    }

    public void PauseVideo()
    {
        if (!IsVideoPlaying || _videoPlaybackSession is null)
        {
            return;
        }

        _videoPlaybackSession.MediaPlayer.SetPause(true);
        IsVideoPaused = true;
    }

    public void ResumeVideo()
    {
        if (_videoPlaybackSession is null)
        {
            return;
        }

        _videoPlaybackSession.MediaPlayer.SetPause(false);
        IsVideoPlaying = true;
        IsVideoPaused = false;
    }

    public void SetVideoPosition(long positionMs)
    {
        positionMs = Math.Max(0, positionMs);
        if (_videoPositionMs != positionMs)
        {
            _videoPositionMs = positionMs;
            OnPropertyChanged(nameof(VideoTimeText));
            OnPropertyChanged(nameof(VideoOverlayText));
        }
    }

    public void SetVideoDuration(long durationMs)
    {
        durationMs = Math.Max(0, durationMs);
        if (_videoDurationMs != durationMs)
        {
            _videoDurationMs = durationMs;
            OnPropertyChanged(nameof(VideoTimeText));
            OnPropertyChanged(nameof(VideoOverlayText));
        }
    }

    public void SetAspectRatio(double aspectRatio, double pageWidth)
    {
        if (aspectRatio > 0)
        {
            _aspectRatio = aspectRatio;
            Resize(pageWidth);
        }
    }

    public void StopVideo()
    {
        IsVideoPlaying = false;
        IsVideoPaused = false;
        SetVideoPosition(0);
        DisposeSessionInBackground(_videoPlaybackSession, stopFirst: true);
        _videoPlaybackSession = null;
    }

    public void Resize(double pageWidth)
    {
        DisplayWidth = Math.Max(1, pageWidth);
        DisplayHeight = DisplayWidth * _aspectRatio;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatVideoTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static void DisposeSessionInBackground(VideoPlaybackSession? session, bool stopFirst)
    {
        if (session is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (stopFirst)
                {
                    session.Stop();
                }

                session.Dispose();
            }
            catch
            {
            }
        });
    }
}

public sealed record ComicArchiveEntry(string Key, ComicMediaType Type, long Size);

public enum ComicMediaType
{
    Image,
    Video
}

public enum VideoCoverLoadStatus
{
    None,
    Loading,
    Success,
    Failed,
    Oversized
}

public sealed class VideoPlaybackSession : IDisposable
{
    private readonly MemoryStream _videoStream;
    private readonly StreamMediaInput _input;
    private readonly VlcMedia _media;
    private readonly VideoFrameRenderer _renderer;
    private bool _isStopped;
    private bool _isDisposed;

    public VideoPlaybackSession(
        MemoryStream videoStream,
        StreamMediaInput input,
        VlcMedia media,
        VlcMediaPlayer mediaPlayer,
        VideoFrameRenderer renderer)
    {
        _videoStream = videoStream;
        _input = input;
        _media = media;
        _renderer = renderer;
        MediaPlayer = mediaPlayer;
    }

    public VlcMediaPlayer MediaPlayer { get; }

    public VideoFrameRenderer Renderer => _renderer;

    public void Stop()
    {
        if (_isStopped || _isDisposed)
        {
            return;
        }

        _isStopped = true;
        if (MediaPlayer.IsPlaying)
        {
            MediaPlayer.Stop();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        MediaPlayer.Dispose();
        _renderer.Dispose();
        _media.Dispose();
        _input.Dispose();
        _videoStream.Dispose();
    }
}

public sealed class VideoFrameRenderer : IDisposable
{
    private readonly Action<ImageSource> _setFrame;
    private readonly Action<uint, uint> _setVideoSize;
    private readonly TaskCompletionSource _firstFrameDisplayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly VlcMediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly VlcMediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
    private readonly VlcMediaPlayer.LibVLCVideoDisplayCb _displayCallback;
    private readonly VlcMediaPlayer.LibVLCVideoFormatCb _formatCallback;
    private readonly VlcMediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;
    private readonly object _frameSync = new();

    private byte[]? _frameBuffer;
    private byte[]? _pendingFrame;
    private GCHandle _frameBufferHandle;
    private WriteableBitmap? _bitmap;
    private uint _width;
    private uint _height;
    private uint _pitch;
    private bool _frameUpdateQueued;
    private bool _isDisposed;

    public VideoFrameRenderer(Action<ImageSource> setFrame, Action<uint, uint> setVideoSize)
    {
        _setFrame = setFrame;
        _setVideoSize = setVideoSize;
        _lockCallback = LockVideo;
        _unlockCallback = UnlockVideo;
        _displayCallback = DisplayVideo;
        _formatCallback = FormatVideo;
        _cleanupCallback = CleanupVideo;
    }

    public Task FirstFrameDisplayed => _firstFrameDisplayed.Task;

    public void AttachTo(VlcMediaPlayer mediaPlayer)
    {
        mediaPlayer.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
        mediaPlayer.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
    }

    private uint FormatVideo(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        _width = width;
        _height = height;
        _pitch = width * 4;
        pitches = _pitch;
        lines = height;

        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        AllocateFrameBuffer((int)(_pitch * _height));

        Application.Current.Dispatcher.Invoke(() =>
        {
            _bitmap = new WriteableBitmap((int)_width, (int)_height, 96, 96, PixelFormats.Bgr32, null);
            _setVideoSize(_width, _height);
        });

        return 1;
    }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        lock (_frameSync)
        {
            if (!_isDisposed && _frameBufferHandle.IsAllocated)
            {
                Marshal.WriteIntPtr(planes, _frameBufferHandle.AddrOfPinnedObject());
            }
        }

        return IntPtr.Zero;
    }

    private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        if (_isDisposed || _frameBuffer is null || _bitmap is null || _width == 0 || _height == 0)
        {
            return;
        }

        var frameSize = (int)(_pitch * _height);
        var shouldQueueRender = false;
        lock (_frameSync)
        {
            if (_isDisposed || _frameBuffer is null)
            {
                return;
            }

            if (_pendingFrame is null || _pendingFrame.Length != frameSize)
            {
                _pendingFrame = new byte[frameSize];
            }

            Buffer.BlockCopy(_frameBuffer, 0, _pendingFrame, 0, frameSize);
            if (!_frameUpdateQueued)
            {
                _frameUpdateQueued = true;
                shouldQueueRender = true;
            }
        }

        if (shouldQueueRender)
        {
            Application.Current.Dispatcher.BeginInvoke((Action)RenderPendingFrame, System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void RenderPendingFrame()
    {
        lock (_frameSync)
        {
            if (_isDisposed || _bitmap is null || _pendingFrame is null || _width == 0 || _height == 0)
            {
                _frameUpdateQueued = false;
                return;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, (int)_width, (int)_height), _pendingFrame, (int)_pitch, 0);
            _setFrame(_bitmap);
            _frameUpdateQueued = false;
            _firstFrameDisplayed.TrySetResult();
        }
    }

    private void CleanupVideo(ref IntPtr opaque)
    {
        ReleaseFrameBuffer();
    }

    private void AllocateFrameBuffer(int size)
    {
        lock (_frameSync)
        {
            ReleaseFrameBufferCore();
            _frameBuffer = new byte[size];
            _frameBufferHandle = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);
        }
    }

    private void ReleaseFrameBuffer()
    {
        lock (_frameSync)
        {
            ReleaseFrameBufferCore();
            _pendingFrame = null;
            _frameUpdateQueued = false;
        }
    }

    private void ReleaseFrameBufferCore()
    {
        if (_frameBufferHandle.IsAllocated)
        {
            _frameBufferHandle.Free();
        }

        _frameBuffer = null;
    }

    public void Dispose()
    {
        lock (_frameSync)
        {
            _isDisposed = true;
            _pendingFrame = null;
            _frameUpdateQueued = false;
        }

        _firstFrameDisplayed.TrySetCanceled();
        ReleaseFrameBuffer();
    }
}

public sealed class MinimumThumbTrack : Track
{
    public static readonly DependencyProperty MinimumThumbLengthProperty =
        DependencyProperty.Register(
            nameof(MinimumThumbLength),
            typeof(double),
            typeof(MinimumThumbTrack),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsArrange));

    public double MinimumThumbLength
    {
        get => (double)GetValue(MinimumThumbLengthProperty);
        set => SetValue(MinimumThumbLengthProperty, value);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = base.ArrangeOverride(arrangeSize);
        if (Thumb is null || MinimumThumbLength <= 0)
        {
            return size;
        }

        if (Orientation == Orientation.Vertical)
        {
            ArrangeVerticalThumb(arrangeSize);
        }
        else
        {
            ArrangeHorizontalThumb(arrangeSize);
        }

        return size;
    }

    private void ArrangeVerticalThumb(Size arrangeSize)
    {
        var targetHeight = Math.Min(MinimumThumbLength, arrangeSize.Height);
        if (Thumb.RenderSize.Height >= targetHeight || targetHeight <= 0)
        {
            return;
        }

        var top = Math.Clamp(
            VisualTreeHelper.GetOffset(Thumb).Y - (targetHeight - Thumb.RenderSize.Height) / 2,
            0,
            Math.Max(0, arrangeSize.Height - targetHeight));
        DecreaseRepeatButton?.Arrange(new Rect(0, 0, arrangeSize.Width, top));
        Thumb.Arrange(new Rect(0, top, arrangeSize.Width, targetHeight));
        IncreaseRepeatButton?.Arrange(new Rect(0, top + targetHeight, arrangeSize.Width, Math.Max(0, arrangeSize.Height - top - targetHeight)));
    }

    private void ArrangeHorizontalThumb(Size arrangeSize)
    {
        var targetWidth = Math.Min(MinimumThumbLength, arrangeSize.Width);
        if (Thumb.RenderSize.Width >= targetWidth || targetWidth <= 0)
        {
            return;
        }

        var left = Math.Clamp(
            VisualTreeHelper.GetOffset(Thumb).X - (targetWidth - Thumb.RenderSize.Width) / 2,
            0,
            Math.Max(0, arrangeSize.Width - targetWidth));
        DecreaseRepeatButton?.Arrange(new Rect(0, 0, left, arrangeSize.Height));
        Thumb.Arrange(new Rect(left, 0, targetWidth, arrangeSize.Height));
        IncreaseRepeatButton?.Arrange(new Rect(left + targetWidth, 0, Math.Max(0, arrangeSize.Width - left - targetWidth), arrangeSize.Height));
    }
}

internal sealed class NaturalFileNameComparer : IComparer<string?>
{
    public static NaturalFileNameComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var ix = 0;
        var iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                var numberComparison = CompareNumberChunks(x, ref ix, y, ref iy);
                if (numberComparison != 0)
                {
                    return numberComparison;
                }
            }
            else
            {
                var charComparison = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                if (charComparison != 0)
                {
                    return charComparison;
                }

                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    private static int CompareNumberChunks(string x, ref int ix, string y, ref int iy)
    {
        var startX = ix;
        var startY = iy;

        while (ix < x.Length && char.IsDigit(x[ix]))
        {
            ix++;
        }

        while (iy < y.Length && char.IsDigit(y[iy]))
        {
            iy++;
        }

        var chunkX = x[startX..ix].TrimStart('0');
        var chunkY = y[startY..iy].TrimStart('0');
        if (chunkX.Length == 0)
        {
            chunkX = "0";
        }

        if (chunkY.Length == 0)
        {
            chunkY = "0";
        }

        var lengthComparison = chunkX.Length.CompareTo(chunkY.Length);
        if (lengthComparison != 0)
        {
            return lengthComparison;
        }

        var valueComparison = string.Compare(chunkX, chunkY, CultureInfo.InvariantCulture, CompareOptions.Ordinal);
        return valueComparison != 0 ? valueComparison : (ix - startX).CompareTo(iy - startY);
    }
}
