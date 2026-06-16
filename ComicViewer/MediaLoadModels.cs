using System.Windows.Media;

namespace ComicViewer;

internal sealed record CachedMedia(
    string EntryKey,
    int PageIndex,
    ComicMediaType Type,
    ArraySegment<byte>? ImageData,
    ArraySegment<byte>? VideoData,
    ImageSource? VideoFrame,
    long EstimatedBytes);

internal sealed record KnownPasswordResult(string Password, ArchiveSession Session);

internal sealed record MediaLoadRequest(
    int PageIndex,
    ComicPage Page,
    long ReservedBytes);
