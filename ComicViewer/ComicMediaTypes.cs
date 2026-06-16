namespace ComicViewer;

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
