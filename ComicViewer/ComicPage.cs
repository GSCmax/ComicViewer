using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ComicViewer;

public sealed class ComicPage : INotifyPropertyChanged
{
    private const double DefaultAspectRatio = 1.45;
    private const double DefaultVideoAspectRatio = 9d / 16d;

    private ArraySegment<byte>? _encodedImageData;
    private ImageSource? _displayImage;
    private MpvVideoPlayerControl? _videoPlayer;
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

    public bool IsLoaded => DisplayImage is not null || _videoPlayer?.HasRenderedFirstFrame == true;

    public ArraySegment<byte>? EncodedImageData => _encodedImageData;

    public bool HasEncodedImageData => _encodedImageData is not null;

    public string DisplayNumber => (Index + 1).ToString(CultureInfo.InvariantCulture);

    public MpvVideoPlayerControl? VideoPlayer => _videoPlayer;

    public FrameworkElement? PlaybackElement => _videoPlayer;

    public bool IsPlaybackHostVisible => _videoPlayer is not null
        && (IsVideoPlaying || IsVideoPreparing || IsVideoPaused || _videoPlayer.HasRenderedFirstFrame);

    public double PlaybackHostWidth => DisplayWidth;

    public double PlaybackHostHeight => DisplayHeight;

    public ImageSource? DisplayImage
    {
        get => _displayImage;
        private set
        {
            if (!ReferenceEquals(_displayImage, value))
            {
                _displayImage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public bool HasDisplayImage => DisplayImage is not null;

    public bool IsVideoPlaying => _videoPlayer?.IsPlaying == true;

    public bool IsVideoPreparing => _videoPlayer?.IsPreparing == true;

    public bool IsVideoPaused => _videoPlayer?.IsPaused == true;

    public VideoCoverLoadStatus CoverLoadStatus => _coverLoadStatus;

    public string VideoTimeText => IsVideo
        ? $"{FormatVideoTime(_videoPositionMs)}/{FormatVideoTime(_videoDurationMs)}"
        : "";

    public string VideoOverlayText => !IsVideo
        ? ""
        : !IsVideoPlaying
            ? IsVideoPreparing
                ? "正在准备播放..."
                : _coverLoadStatus switch
                {
                    VideoCoverLoadStatus.Oversized => "视频过大，无法内存播放",
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

    public void SetDisplayImage(ImageSource? image, double pageWidth)
    {
        DisplayImage = image;
        if (image?.Width > 0)
        {
            _aspectRatio = image.Height / image.Width;
        }

        Resize(pageWidth);
    }

    public void ClearDisplayImage(double pageWidth)
    {
        DisplayImage = null;
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
        DisplayImage = null;
    }

    public void SetCoverLoadStatus(VideoCoverLoadStatus status)
    {
        if (_coverLoadStatus != status)
        {
            _coverLoadStatus = status;
            OnPropertyChanged(nameof(VideoOverlayText));
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(CoverLoadStatus));
        }
    }

    public void SetVideoPlayer(MpvVideoPlayerControl? player)
    {
        _videoPlayer = player;
        OnPropertyChanged(nameof(PlaybackElement));
        NotifyPlaybackStateChanged();
    }

    public void PauseVideo()
    {
        if (!IsVideoPlaying || _videoPlayer is null)
        {
            return;
        }

        _videoPlayer.Pause();
        NotifyPlaybackStateChanged();
    }

    public void ResumeVideo()
    {
        if (_videoPlayer is null)
        {
            return;
        }

        _videoPlayer.Resume();
        NotifyPlaybackStateChanged();
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
        _videoPlayer?.Stop();
        DetachVideoPlayer();
    }

    public void DetachVideoPlayer()
    {
        SetVideoPosition(0);
        _videoPlayer = null;
        OnPropertyChanged(nameof(PlaybackElement));
        NotifyPlaybackStateChanged();
    }

    public void Resize(double pageWidth)
    {
        DisplayWidth = Math.Max(1, pageWidth);
        DisplayHeight = DisplayWidth * _aspectRatio;
        OnPropertyChanged(nameof(PlaybackHostWidth));
        OnPropertyChanged(nameof(PlaybackHostHeight));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void NotifyPlaybackStateChanged()
    {
        OnPropertyChanged(nameof(IsVideoPlaying));
        OnPropertyChanged(nameof(IsVideoPreparing));
        OnPropertyChanged(nameof(IsVideoPaused));
        OnPropertyChanged(nameof(IsPlaybackHostVisible));
        OnPropertyChanged(nameof(PlaybackHostWidth));
        OnPropertyChanged(nameof(PlaybackHostHeight));
        OnPropertyChanged(nameof(VideoOverlayText));
        OnPropertyChanged(nameof(IsLoaded));
    }

    private static string FormatVideoTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

}
