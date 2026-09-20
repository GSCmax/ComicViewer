using System.IO;
using System.Windows.Media.Imaging;

namespace ComicViewer;

internal static class ImageDecoder
{
    // A global bound also covers overlapping archive shutdown and startup.
    private static readonly SemaphoreSlim Gate = new(2, 2);
    public const long MaxDecodedImageBytes = 128L * 1024 * 1024;

    public static double? ReadAspectRatio(ArraySegment<byte> data)
    {
        try
        {
            using var stream = Open(data);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return frame.PixelWidth > 0 ? (double)frame.PixelHeight / frame.PixelWidth : null;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return null; }
    }

    public static async Task<BitmapSource> DecodeAsync(MediaContent content, int width, CancellationToken token, long pixelBudget = MaxDecodedImageBytes)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var ratio = content.AspectRatio ?? 1.45;
                width = Math.Min(width, Math.Max(1, (int)Math.Sqrt(pixelBudget / (4d * ratio))));
                using var stream = Open(content.Data);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = width;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                token.ThrowIfCancellationRequested();
                return (BitmapSource)image;
            }, token).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private static MemoryStream Open(ArraySegment<byte> data) => new(data.Array!, data.Offset, data.Count, writable: false);
}
