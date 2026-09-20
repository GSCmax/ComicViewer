using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ComicViewer;

public sealed class ComicPage : INotifyPropertyChanged
{
    private ImageSource? _displayImage;
    private MpvVideoPlayerControl? _player;
    private long _position;
    private long _duration;
    private double _aspectRatio;
    private VideoCoverLoadStatus _coverStatus;

    public ComicPage(int index, ComicArchiveEntry entry)
    {
        Index = index;
        EntryKey = entry.Key;
        MediaType = entry.Type;
        EntrySize = entry.Size;
        _aspectRatio = IsVideo ? 9d / 16d : 1.45;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public int Index { get; }
    public string EntryKey { get; }
    public long EntrySize { get; }
    public ComicMediaType MediaType { get; }
    public bool IsVideo => MediaType == ComicMediaType.Video;
    public string DisplayNumber => (Index + 1).ToString(CultureInfo.InvariantCulture);
    public double AspectRatio => _aspectRatio;
    public ImageSource? DisplayImage => _displayImage;
    public FrameworkElement? PlaybackElement => _player;
    public bool IsVideoPlaying => _player?.IsPlaying == true;
    public bool IsVideoPaused => _player?.IsPaused == true;
    public bool IsVideoPreparing => _player?.IsPreparing == true;
    public bool IsPlaybackHostVisible => _player is not null;
    public string VideoOverlayText => IsVideoPlaying
        ? $"{FormatTime(_position)}/{FormatTime(_duration)}"
        : IsVideoPreparing ? "正在准备播放..."
        : IsVideoPaused ? "点击继续播放"
        : _coverStatus switch
        {
            VideoCoverLoadStatus.Oversized => "视频过大，无法内存播放",
            VideoCoverLoadStatus.Loading => "正在加载封面...",
            VideoCoverLoadStatus.Failed => "封面加载失败，点击播放",
            VideoCoverLoadStatus.Success => "点击播放",
            _ => "等待加载封面..."
        };

    public void SetDisplayImage(ImageSource? image)
    {
        if (ReferenceEquals(_displayImage, image)) return;
        _displayImage = image;
        Notify(nameof(DisplayImage));
    }

    public void SetAspectRatio(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio <= 0 || Math.Abs(_aspectRatio - ratio) < 0.000001) return;
        _aspectRatio = ratio;
        Notify(nameof(AspectRatio));
    }

    public void SetCoverLoadStatus(VideoCoverLoadStatus status)
    {
        if (_coverStatus == status) return;
        _coverStatus = status;
        Notify(nameof(VideoOverlayText));
    }

    public void SetPlaybackHost(MpvVideoPlayerControl? player)
    {
        _player = player;
        if (player is null) _position = _duration = 0;
        Notify(nameof(PlaybackElement));
        NotifyPlaybackStateChanged();
    }

    public void SetVideoPosition(long value)
    {
        // The overlay displays seconds; avoid updating it for every mpv time event.
        if (_position / 1000 == value / 1000) return;
        _position = Math.Max(0, value);
        Notify(nameof(VideoOverlayText));
    }

    public void SetVideoDuration(long value)
    {
        if (_duration == value) return;
        _duration = Math.Max(0, value);
        Notify(nameof(VideoOverlayText));
    }

    public void NotifyPlaybackStateChanged()
    {
        Notify(nameof(IsVideoPlaying));
        Notify(nameof(IsVideoPaused));
        Notify(nameof(IsVideoPreparing));
        Notify(nameof(IsPlaybackHostVisible));
        Notify(nameof(VideoOverlayText));
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}
