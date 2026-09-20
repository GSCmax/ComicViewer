using System.Windows.Controls;

namespace ComicViewer;

public partial class MpvVideoPlayerControl : UserControl, IAsyncDisposable
{
    private enum State { Stopped, Preparing, Playing, Paused }
    private MpvVideoPlaybackEngine? _engine;
    private State _state;
    private bool _disposed;
    public MpvVideoPlayerControl() => InitializeComponent();
    public bool IsPlaying => _state == State.Playing;
    public bool IsPreparing => _state == State.Preparing;
    public bool IsPaused => _state == State.Paused;
    public event Action? StateChanged;
    public event Action<long>? DurationChanged;
    public event Action<long>? TimeChanged;
    public event Action<long, long>? VideoSizeChanged;
    public event Action? EndReached;
    public event Action? PlaybackError;

    public async Task PlayAsync(ArraySegment<byte> source, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _state = State.Preparing;
        StateChanged?.Invoke();
        await VideoView.WaitForReadyAsync(TimeSpan.FromSeconds(3), token);
        token.ThrowIfCancellationRequested();
        if (_engine is null)
        {
            _engine = new MpvVideoPlaybackEngine(VideoView);
            _engine.Updated += OnEngineUpdate;
        }
        await _engine.LoadAsync(source);
        token.ThrowIfCancellationRequested();
    }

    private void OnEngineUpdate(PlaybackUpdate update)
    {
        // Both the native event and the dispatched UI callback carry the file generation.
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _engine is null || update.Version != _engine.Version) return;
            switch (update.Kind)
            {
                case PlaybackUpdateKind.Playing: _state = State.Playing; StateChanged?.Invoke(); break;
                case PlaybackUpdateKind.Paused:
                    if (_state != State.Preparing) { _state = State.Paused; StateChanged?.Invoke(); }
                    break;
                case PlaybackUpdateKind.Duration: DurationChanged?.Invoke(update.Value); break;
                case PlaybackUpdateKind.Position: TimeChanged?.Invoke(update.Value); break;
                case PlaybackUpdateKind.Size: VideoSizeChanged?.Invoke(update.Value, update.Other); break;
                case PlaybackUpdateKind.FirstFrame: StateChanged?.Invoke(); break;
                case PlaybackUpdateKind.End: EndReached?.Invoke(); break;
                case PlaybackUpdateKind.Error: PlaybackError?.Invoke(); break;
            }
        });
    }

    public void Pause()
    {
        _state = State.Paused;
        _engine?.Pause();
        StateChanged?.Invoke();
    }

    public void Resume()
    {
        _state = State.Playing;
        _engine?.Resume();
        StateChanged?.Invoke();
    }

    public Task StopAsync()
    {
        _state = State.Stopped;
        StateChanged?.Invoke();
        return _engine?.StopAsync() ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_engine is not null)
            {
                _engine.Updated -= OnEngineUpdate;
                await _engine.DisposeAsync();
            }
        }
        finally
        {
            _engine = null;
            VideoView.Dispose();
        }
    }
}
