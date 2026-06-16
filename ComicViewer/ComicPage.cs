using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicViewer;

public sealed class ComicPage : INotifyPropertyChanged
{
    private const double DefaultAspectRatio = 1.45;
    private const double DefaultVideoAspectRatio = 9d / 16d;

    private ArraySegment<byte>? _encodedImageData;
    private BitmapImage? _image;
    private MpvVideoPlayerControl? _videoPlayer;
    private ImageSource? _videoFrame;
    private bool _isVideoPaused;
    private bool _isVideoPreparing;
    private bool _isVideoPlaying;
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

    public bool IsImageLoaded => Image is not null;

    public bool IsLoaded => IsImage ? Image is not null : VideoFrame is not null;

    public ArraySegment<byte>? EncodedImageData => _encodedImageData;

    public bool HasEncodedImageData => _encodedImageData is not null;

    public string DisplayNumber => (Index + 1).ToString(CultureInfo.InvariantCulture);

    public MpvVideoPlayerControl? VideoPlayer => _videoPlayer;

    public FrameworkElement? PlaybackElement => _videoPlayer;

    public bool IsPlaybackHostVisible => IsVideoPlaying || IsVideoPreparing;

    public double PlaybackHostWidth => DisplayWidth;

    public double PlaybackHostHeight => DisplayHeight;

    public BitmapImage? Image
    {
        get => _image;
        private set
        {
            if (!ReferenceEquals(_image, value))
            {
                _image = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsImageLoaded));
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public ImageSource? VideoFrame
    {
        get => _videoFrame;
        private set
        {
            if (!ReferenceEquals(_videoFrame, value))
            {
                _videoFrame = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public bool IsVideoPlaying
    {
        get => _isVideoPlaying;
        set
        {
            if (_isVideoPlaying != value)
            {
                _isVideoPlaying = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VideoOverlayText));
                OnPropertyChanged(nameof(IsPlaybackHostVisible));
                OnPropertyChanged(nameof(PlaybackHostWidth));
                OnPropertyChanged(nameof(PlaybackHostHeight));
            }
        }
    }

    public bool IsVideoPreparing
    {
        get => _isVideoPreparing;
        set
        {
            if (_isVideoPreparing != value)
            {
                _isVideoPreparing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsPlaybackHostVisible));
                OnPropertyChanged(nameof(PlaybackHostWidth));
                OnPropertyChanged(nameof(PlaybackHostHeight));
                OnPropertyChanged(nameof(VideoOverlayText));
            }
        }
    }

    public bool IsVideoPaused
    {
        get => _isVideoPaused;
        set
        {
            if (_isVideoPaused != value)
            {
                _isVideoPaused = value;
                OnPropertyChanged();
            }
        }
    }

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
                    VideoCoverLoadStatus.Oversized => "视频过大，点击后加载播放",
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

    public void SetImage(BitmapImage? image, double pageWidth)
    {
        Image = image;
        if (image?.PixelWidth > 0)
        {
            _aspectRatio = (double)image.PixelHeight / image.PixelWidth;
        }

        Resize(pageWidth);
    }

    public void ClearImage(double pageWidth)
    {
        Image = null;
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
        Image = null;
    }

    public void SetVideoFrame(ImageSource frame)
    {
        SetCoverLoadStatus(VideoCoverLoadStatus.Success);
        VideoFrame = frame;
        OnPropertyChanged(nameof(VideoOverlayText));
    }

    public void ClearVideoFrame()
    {
        VideoFrame = null;
        OnPropertyChanged(nameof(VideoOverlayText));
    }

    public void SetCoverLoadStatus(VideoCoverLoadStatus status)
    {
        if (_coverLoadStatus != status)
        {
            _coverLoadStatus = status;
            OnPropertyChanged(nameof(VideoOverlayText));
        }
    }

    public void SetVideoPlayer(MpvVideoPlayerControl player)
    {
        _videoPlayer = player;
        OnPropertyChanged(nameof(PlaybackElement));
    }

    public void PauseVideo()
    {
        if (!IsVideoPlaying || _videoPlayer is null)
        {
            return;
        }

        _videoPlayer.Pause();
        IsVideoPaused = true;
    }

    public void ResumeVideo()
    {
        if (_videoPlayer is null)
        {
            return;
        }

        _videoPlayer.Resume();
        IsVideoPlaying = true;
        IsVideoPaused = false;
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
        IsVideoPlaying = false;
        IsVideoPaused = false;
        SetVideoPosition(0);
        IsVideoPreparing = false;
        DisposePlayerInBackground(_videoPlayer, stopFirst: true);
        _videoPlayer = null;
        OnPropertyChanged(nameof(PlaybackElement));
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

    private static string FormatVideoTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static void DisposePlayerInBackground(MpvVideoPlayerControl? player, bool stopFirst)
    {
        if (player is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (stopFirst)
                {
                    player.Stop();
                }

                player.Dispose();
            }
            catch
            {
            }
        });
    }
}
