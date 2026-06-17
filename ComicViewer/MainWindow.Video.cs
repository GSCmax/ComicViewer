using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ComicViewer;

public partial class MainWindow
{
    private const double VideoCoverCaptureWidth = 480d;

    private async void PlayVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingArchive || sender is not Button { CommandParameter: ComicPage page } button || !page.IsVideo)
        {
            return;
        }

        try
        {
            button.IsEnabled = false;
            if (page.IsVideoPreparing)
            {
                return;
            }

            if (ReferenceEquals(_sharedVideoPage, page) && page.IsVideoPaused)
            {
                _sharedVideoPlayer?.Resume();
                page.NotifyPlaybackStateChanged();
            }
            else
            {
                await PlayVideoAsync(page);
            }
        }
        finally
        {
            ReleaseMouseInputCapture();
            button.IsEnabled = true;
            e.Handled = true;
        }
    }

    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ComicPage page } && page.IsVideoPlaying)
        {
            page.PauseVideo();
            ReleaseMouseInputCapture();
            e.Handled = true;
        }
    }

    private static void ReleaseMouseInputCapture()
    {
        if (Mouse.Captured is UIElement capturedElement)
        {
            capturedElement.ReleaseMouseCapture();
        }

        Mouse.Capture(null);
    }

    private async Task PlayVideoAsync(ComicPage page)
    {
        var archivePath = _archivePath;
        if (archivePath is null)
        {
            return;
        }

        var cachedVideoData = TryGetCachedVideoData(page.EntryKey);
        if (page.EntrySize > MaxInMemoryVideoPlaybackBytes && !cachedVideoData.HasValue)
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

        CancelVideoCoverGeneration();
        var playCts = BeginVideoPlayLoad();
        var cancellationToken = playCts.Token;
        MemoryStream? videoStream = null;
        ArraySegment<byte> videoData;

        try
        {
            StatusTextBlock.Text = $"正在准备播放 {Path.GetFileName(page.EntryKey)} ...";

            if (cachedVideoData.HasValue)
            {
                videoData = cachedVideoData.Value;
            }
            else
            {
                videoStream = await Task.Run(() => _archiveSession?.CopyEntryToMemory(page.EntryKey, cancellationToken, MaxInMemoryVideoPlaybackBytes), cancellationToken);
                if (videoStream is null)
                {
                    return;
                }

                videoData = TakeMemorySegment(videoStream);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVideoPlayRequestCurrent(playCts, archivePath, page))
            {
                return;
            }

            await PrepareSharedVideoPlayerAsync(page, videoData, playCts, archivePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StopSharedVideoPlayer();
            StatusTextBlock.Text = "视频播放失败";
            MessageBox.Show(this, ex.Message, "无法播放视频", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            videoStream?.Dispose();
            FinishVideoPlayLoad(playCts);
        }
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

    private static MpvVideoPlayerControl CreateVideoPlayer()
    {
        return new MpvVideoPlayerControl();
    }

    private async Task PrepareSharedVideoPlayerAsync(
        ComicPage page,
        ArraySegment<byte> videoData,
        CancellationTokenSource playCts,
        string archivePath)
    {
        var player = _sharedVideoPlayer ?? throw new InvalidOperationException("共享播放器尚未初始化。");
        var cancellationToken = playCts.Token;

        StopSharedVideoPlayer(returnToCoverHost: false);
        VideoCoverGeneratorHost.Content = null;
        player.SetSource(videoData);
        _sharedVideoPage = page;
        page.SetVideoPlayer(player);
        player.BeginPreparing(autoPlayAfterFirstFrame: true);
        page.NotifyPlaybackStateChanged();
        ReleaseMouseInputCapture();

        await Dispatcher.InvokeAsync(() => PagesListBox.UpdateLayout());
        await player.WaitForHostReadyAsync(TimeSpan.FromSeconds(2), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsVideoPlayRequestCurrent(playCts, archivePath, page))
        {
            return;
        }

        player.Play();
        StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 使用内存播放";
    }

    private void AttachPlaybackEvents(MpvVideoPlayerControl player)
    {
        player.Playing += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            var page = _sharedVideoPage;
            if (page?.VideoPlayer == player)
            {
                page.NotifyPlaybackStateChanged();
                StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 正在播放";
            }
        }));

        player.Paused += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            var page = _sharedVideoPage;
            if (page?.VideoPlayer == player)
            {
                page.NotifyPlaybackStateChanged();
                StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 已暂停";
            }
        }));

        player.DurationChanged += durationMs => Dispatcher.BeginInvoke((Action)(() =>
        {
            var page = _sharedVideoPage;
            if (page?.VideoPlayer == player)
            {
                page.SetVideoDuration(durationMs);
            }
        }));

        player.VideoSizeChanged += (width, height) => Dispatcher.BeginInvoke((Action)(() =>
        {
            var page = _sharedVideoPage;
            if (page?.VideoPlayer == player && width > 0)
            {
                page.SetAspectRatio((double)height / width, _pageWidth);
            }
        }));

        player.FirstFrameRendered += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            var page = _sharedVideoPage;
            if (page?.VideoPlayer == player)
            {
                page.NotifyPlaybackStateChanged();
            }
        }));

        player.TimeChanged += positionMs => Dispatcher.BeginInvoke((Action)(() =>
        {
            var page = _sharedVideoPage;
            if (page?.VideoPlayer == player)
            {
                page.SetVideoPosition(positionMs);
            }
        }));

        player.EndReached += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_sharedVideoPage?.VideoPlayer == player)
            {
                StopSharedVideoPlayer();
                ScheduleVideoCoverGeneration();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);

        player.PlaybackError += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (_sharedVideoPage?.VideoPlayer != player)
            {
                return;
            }

            StopSharedVideoPlayer();
            ScheduleVideoCoverGeneration();
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

    private void ScheduleVideoCoverGeneration()
    {
        var archivePath = _archivePath;
        if (archivePath is null
            || _videoCoverCts is not null
            || _videoPlayCts is not null
            || _sharedVideoPage is not null)
        {
            return;
        }

        var coverCts = new CancellationTokenSource();
        _videoCoverCts = coverCts;
        _ = GenerateVideoCoversAsync(archivePath, coverCts);
    }

    private void CancelVideoCoverGeneration()
    {
        var cts = _videoCoverCts;
        _videoCoverCts = null;
        cts?.Cancel();
    }

    private async Task GenerateVideoCoversAsync(string archivePath, CancellationTokenSource coverCts)
    {
        var cancellationToken = coverCts.Token;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForVideoCoverIdleAsync(cancellationToken);
                if (!IsVideoCoverRequestCurrent(coverCts, archivePath))
                {
                    return;
                }

                var page = FindNextVideoCoverPage();
                if (page is null)
                {
                    return;
                }

                var videoData = TryGetCachedVideoData(page.EntryKey);
                if (!videoData.HasValue)
                {
                    return;
                }

                page.SetCoverLoadStatus(VideoCoverLoadStatus.Loading);
                VideoCoverFrame? coverFrame;
                try
                {
                    coverFrame = await RenderVideoCoverFrameAsync(page, videoData.Value, coverCts, archivePath);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    coverFrame = null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!IsVideoCoverRequestCurrent(coverCts, archivePath))
                {
                    return;
                }

                if (coverFrame is null)
                {
                    page.SetCoverLoadStatus(VideoCoverLoadStatus.Failed);
                }
                else
                {
                    StoreVideoCoverFrame(page, coverFrame);
                }

                UpdateReadingStatus(GetCurrentPageIndex());
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_videoCoverCts, coverCts))
            {
                _videoCoverCts = null;
                StopSharedVideoPlayer();
            }

            coverCts.Dispose();
        }
    }

    private ComicPage? FindNextVideoCoverPage()
    {
        return Pages
            .Where(page => page.IsVideo
                && !page.HasDisplayImage
                && page.CoverLoadStatus != VideoCoverLoadStatus.Failed
                && page.CoverLoadStatus != VideoCoverLoadStatus.Oversized
                && TryGetCachedVideoData(page.EntryKey).HasValue)
            .OrderBy(page => Math.Abs(page.Index - _currentPageIndex))
            .ThenBy(page => page.Index)
            .FirstOrDefault();
    }

    private async Task WaitForVideoCoverIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsedSinceScroll = DateTime.UtcNow - _lastScrollUtc;
            var remainingDelay = TimeSpan.FromMilliseconds(VideoCoverIdleDelayMilliseconds) - elapsedSinceScroll;
            if (remainingDelay <= TimeSpan.Zero)
            {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                return;
            }

            await Task.Delay(remainingDelay, cancellationToken);
        }
    }

    private async Task<VideoCoverFrame?> RenderVideoCoverFrameAsync(
        ComicPage page,
        ArraySegment<byte> videoData,
        CancellationTokenSource coverCts,
        string archivePath)
    {
        var player = _sharedVideoPlayer ?? throw new InvalidOperationException("共享播放器尚未初始化。");
        var cancellationToken = coverCts.Token;
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long videoWidth = 0;
        long videoHeight = 0;

        void OnFirstFrameRendered() => firstFrame.TrySetResult();
        void OnVideoSizeChanged(long width, long height)
        {
            videoWidth = width;
            videoHeight = height;
        }

        try
        {
            StopSharedVideoPlayer();
            VideoCoverGeneratorHost.Content = player;
            VideoCoverGeneratorHost.UpdateLayout();
            player.SetSource(videoData);
            await player.WaitForHostReadyAsync(TimeSpan.FromSeconds(2), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVideoCoverRequestCurrent(coverCts, archivePath))
            {
                return null;
            }

            player.FirstFrameRendered += OnFirstFrameRendered;
            player.VideoSizeChanged += OnVideoSizeChanged;
            player.PrepareFirstFrame();
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ResizeVideoCoverGeneratorHostAsync(videoWidth, videoHeight, cancellationToken);
            var frame = IsVideoCoverRequestCurrent(coverCts, archivePath)
                ? player.CaptureCurrentFrame()
                : null;
            return frame is null
                ? null
                : new VideoCoverFrame(frame, videoWidth, videoHeight);
        }
        finally
        {
            player.FirstFrameRendered -= OnFirstFrameRendered;
            player.VideoSizeChanged -= OnVideoSizeChanged;
            if (ReferenceEquals(_videoCoverCts, coverCts))
            {
                player.Stop();
            }
        }
    }

    private async Task ResizeVideoCoverGeneratorHostAsync(long videoWidth, long videoHeight, CancellationToken cancellationToken)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
        {
            return;
        }

        var captureHeight = Math.Max(1d, Math.Round(VideoCoverCaptureWidth * videoHeight / videoWidth));
        if (Math.Abs(VideoCoverGeneratorHost.Width - VideoCoverCaptureWidth) > 0.1
            || Math.Abs(VideoCoverGeneratorHost.Height - captureHeight) > 0.1)
        {
            VideoCoverGeneratorHost.Width = VideoCoverCaptureWidth;
            VideoCoverGeneratorHost.Height = captureHeight;
            VideoCoverGeneratorHost.UpdateLayout();
        }

        _sharedVideoPlayer?.RequestRender();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Task.Delay(30, cancellationToken);
    }

    private bool IsVideoCoverRequestCurrent(CancellationTokenSource coverCts, string archivePath)
    {
        return ReferenceEquals(_videoCoverCts, coverCts)
            && !coverCts.IsCancellationRequested
            && _videoPlayCts is null
            && _sharedVideoPage is null
            && string.Equals(_archivePath, archivePath, StringComparison.Ordinal);
    }

    private void StoreVideoCoverFrame(ComicPage page, VideoCoverFrame coverFrame)
    {
        var frame = coverFrame.Frame;
        page.SetDisplayImage(frame, _pageWidth);
        if (coverFrame.VideoWidth > 0)
        {
            page.SetAspectRatio((double)coverFrame.VideoHeight / coverFrame.VideoWidth, _pageWidth);
        }
        else if (frame.Width > 0)
        {
            page.SetAspectRatio(frame.Height / frame.Width, _pageWidth);
        }

        page.SetCoverLoadStatus(VideoCoverLoadStatus.Success);

        lock (_cacheLock)
        {
            if (_mediaCache.TryGetValue(page.EntryKey, out var cachedMedia))
            {
                _mediaCache[page.EntryKey] = cachedMedia with { DisplayImage = frame };
            }
        }
    }

    private sealed record VideoCoverFrame(ImageSource Frame, long VideoWidth, long VideoHeight);

    private ArraySegment<byte>? TryGetCachedVideoData(string entryKey)
    {
        lock (_cacheLock)
        {
            if (_mediaCache.TryGetValue(entryKey, out var cachedMedia) && cachedMedia.VideoData is { } videoData)
            {
                return videoData;
            }

            return null;
        }
    }

    private void StopSharedVideoPlayer(bool returnToCoverHost = true)
    {
        var page = _sharedVideoPage;
        _sharedVideoPage = null;
        _sharedVideoPlayer?.Stop();
        page?.DetachVideoPlayer();
        if (returnToCoverHost && _sharedVideoPlayer is not null && !ReferenceEquals(VideoCoverGeneratorHost.Content, _sharedVideoPlayer))
        {
            VideoCoverGeneratorHost.Content = _sharedVideoPlayer;
        }
    }

    private void DisposeSharedVideoPlayer()
    {
        StopSharedVideoPlayer();
        _sharedVideoPlayer?.Dispose();
        VideoCoverGeneratorHost.Content = null;
        _sharedVideoPlayer = null;
    }

    private void StopAllVideos()
    {
        StopSharedVideoPlayer();
    }
}
