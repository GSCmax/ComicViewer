using System.IO;

namespace ComicViewer;

internal static class MediaFileClassifier
{
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

    public static bool IsSupported(string? fileName)
    {
        return IsImage(fileName) || IsVideo(fileName);
    }

    public static ComicMediaType GetMediaType(string fileName)
    {
        return IsVideo(fileName) ? ComicMediaType.Video : ComicMediaType.Image;
    }

    private static bool IsImage(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName) && ImageExtensions.Contains(Path.GetExtension(fileName));
    }

    private static bool IsVideo(string? fileName)
    {
        return !string.IsNullOrWhiteSpace(fileName) && VideoExtensions.Contains(Path.GetExtension(fileName));
    }
}
