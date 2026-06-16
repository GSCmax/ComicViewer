using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ComicViewer;

public partial class MainWindow
{
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

            if (page.VideoPlayer is not null && page.IsVideoPaused)
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
        MpvVideoPlayerControl? player = null;
        var assignedToPage = false;

        try
        {
            StopOtherVideos(page);
            page.StopVideo();
            StatusTextBlock.Text = $"正在准备播放 {Path.GetFileName(page.EntryKey)} ...";

            videoStream = TryCreateCachedVideoStream(page.EntryKey)
                ?? await Task.Run(() => _archiveSession?.CopyEntryToMemory(page.EntryKey, cancellationToken, MaxInMemoryVideoPlaybackBytes), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (videoStream is null || !IsVideoPlayRequestCurrent(playCts, archivePath, page))
            {
                return;
            }

            var videoData = TakeMemorySegment(videoStream);
            player = CreateVideoPlayer(videoData);
            page.SetVideoPlayer(player);
            assignedToPage = true;
            AttachPlaybackEvents(page, player);
            page.IsVideoPreparing = true;
            page.IsVideoPaused = false;
            ReleaseMouseInputCapture();

            await Dispatcher.InvokeAsync(() => PagesListBox.UpdateLayout());
            await player.WaitForHostReadyAsync(TimeSpan.FromSeconds(2), cancellationToken);
            player.Play();

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
                player?.Dispose();
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

    private static async Task<ImageSource?> TryRenderVideoCoverFrameAsync(
        ArraySegment<byte> videoData,
        CancellationToken cancellationToken)
    {
        var framePng = await MpvVideoPlayerControl.TryRenderFirstFramePngAsync(
            videoData,
            TimeSpan.FromSeconds(2),
            cancellationToken);
        if (framePng is not { } pngData)
        {
            return null;
        }

        using var stream = CreateReadOnlyMemoryStream(pngData);
        return DecodeImage(stream, MinimumDecodePixelWidth);
    }

    private static MpvVideoPlayerControl CreateVideoPlayer(ArraySegment<byte> videoData)
    {
        return new MpvVideoPlayerControl(videoData);
    }

    private void AttachPlaybackEvents(ComicPage page, MpvVideoPlayerControl player)
    {
        player.Playing += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.VideoPlayer == player)
            {
                page.IsVideoPreparing = false;
                page.IsVideoPlaying = true;
                page.IsVideoPaused = false;
                StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 正在播放";
            }
        }));

        player.Paused += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.VideoPlayer == player)
            {
                page.IsVideoPaused = true;
                StatusTextBlock.Text = $"{Path.GetFileName(page.EntryKey)} - 已暂停";
            }
        }));

        player.DurationChanged += durationMs => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.VideoPlayer == player)
            {
                page.SetVideoDuration(durationMs);
            }
        }));

        player.VideoSizeChanged += (width, height) => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.VideoPlayer == player && width > 0)
            {
                page.SetAspectRatio((double)height / width, _pageWidth);
            }
        }));

        player.TimeChanged += positionMs => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.VideoPlayer == player)
            {
                page.SetVideoPosition(positionMs);
            }
        }));

        player.EndReached += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.VideoPlayer == player)
            {
                page.StopVideo();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);

        player.PlaybackError += () => Dispatcher.BeginInvoke((Action)(() =>
        {
            if (page.VideoPlayer != player)
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
}
