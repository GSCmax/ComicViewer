namespace ComicViewer;

internal sealed record CachedMedia(
    string EntryKey,
    int PageIndex,
    ComicMediaType Type,
    ArraySegment<byte>? ImageData,
    ArraySegment<byte>? VideoData,
    ArraySegment<byte>? CoverImageData,
    long EstimatedBytes);

internal sealed record KnownPasswordResult(string Password, ArchiveSession Session);

internal sealed record MediaLoadRequest(
    int PageIndex,
    ComicPage Page,
    long ReservedBytes);
