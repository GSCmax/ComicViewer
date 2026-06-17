using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ComicViewer;

internal static class VideoThumbnailService
{
    private const double CaptureWidth = 480d;

    public static async Task<VideoThumbnailImage?> GenerateAsync(
        MpvVideoPlayerControl player,
        ContentControl renderHost,
        ArraySegment<byte> videoData,
        Func<bool> isRequestCurrent,
        CancellationToken cancellationToken)
    {
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
            renderHost.Content = player;
            renderHost.UpdateLayout();
            player.SetSource(videoData);
            await player.WaitForHostReadyAsync(TimeSpan.FromSeconds(2), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isRequestCurrent())
            {
                return null;
            }

            player.FirstFrameRendered += OnFirstFrameRendered;
            player.VideoSizeChanged += OnVideoSizeChanged;
            player.PrepareFirstFrame();
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ResizeRenderHostAsync(player, renderHost, videoWidth, videoHeight, cancellationToken);

            var frame = isRequestCurrent()
                ? player.CaptureCurrentFrame()
                : null;
            if (frame is null)
            {
                return null;
            }

            var imageData = await Task.Run(() => EncodeBitmapAsPng(frame), cancellationToken);
            return new VideoThumbnailImage(
                imageData,
                videoWidth,
                videoHeight,
                frame.PixelWidth,
                frame.PixelHeight);
        }
        finally
        {
            player.FirstFrameRendered -= OnFirstFrameRendered;
            player.VideoSizeChanged -= OnVideoSizeChanged;
            if (isRequestCurrent())
            {
                player.Stop();
            }
        }
    }

    private static async Task ResizeRenderHostAsync(
        MpvVideoPlayerControl player,
        ContentControl renderHost,
        long videoWidth,
        long videoHeight,
        CancellationToken cancellationToken)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
        {
            return;
        }

        var captureHeight = Math.Max(1d, Math.Round(CaptureWidth * videoHeight / videoWidth));
        if (Math.Abs(renderHost.Width - CaptureWidth) > 0.1
            || Math.Abs(renderHost.Height - captureHeight) > 0.1)
        {
            renderHost.Width = CaptureWidth;
            renderHost.Height = captureHeight;
            renderHost.UpdateLayout();
        }

        player.RequestRender();
        await renderHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(30, cancellationToken);
    }

    private static ArraySegment<byte> EncodeBitmapAsPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new ArraySegment<byte>(stream.ToArray());
    }
}

internal sealed record VideoThumbnailImage(
    ArraySegment<byte> ImageData,
    long VideoWidth,
    long VideoHeight,
    int PixelWidth,
    int PixelHeight);
