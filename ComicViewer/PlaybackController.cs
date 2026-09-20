using System.Windows.Controls;

namespace ComicViewer;

internal sealed class PlaybackController : IAsyncDisposable
{
    private readonly ContentControl _parkingHost;
    private readonly Action _updateLayout;
    private MpvVideoPlayerControl? _player;
    private ComicPage? _page;
    private MediaLease? _lease;
    private CancellationTokenSource? _request;
    private Task _operation = Task.CompletedTask;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private bool _disposed;
    public event Action? Changed;
    public event Action<Exception>? Error;

    public PlaybackController(ContentControl parkingHost, Action updateLayout)
    {
        _parkingHost = parkingHost;
        _updateLayout = updateLayout;
    }

    public Task PlayAsync(ReaderSession session, ComicPage page)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_page, page) && _player?.IsPaused == true)
        {
            _player.Resume();
            return Task.CompletedTask;
        }
        _request?.Cancel();
        var request = new CancellationTokenSource();
        _request = request;
        return _operation = PlayCoreAsync(session, page, request);
    }

    private async Task PlayCoreAsync(ReaderSession session, ComicPage page, CancellationTokenSource request)
    {
        var entered = false;
        var started = false;
        MediaLease? lease = null;
        try
        {
            await _transition.WaitAsync(request.Token);
            entered = true;
            await StopPlayerAsync();
            lease = await session.AcquireAsync(page.Index, request.Token);
            request.Token.ThrowIfCancellationRequested();
            var player = EnsurePlayer();
            _parkingHost.Content = null;
            _page = page;
            _lease = lease;
            lease = null;
            page.SetPlaybackHost(player);
            _updateLayout();
            await player.PlayAsync(_lease.Content.Data, request.Token);
            started = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed && !request.IsCancellationRequested) Error?.Invoke(ex); }
        finally
        {
            lease?.Dispose();
            if (entered)
            {
                try { if (!started || request.IsCancellationRequested) await StopPlayerAsync(); }
                catch (Exception ex) { if (!_disposed) Error?.Invoke(ex); }
                finally { _transition.Release(); }
            }
            if (ReferenceEquals(_request, request)) _request = null;
            request.Dispose();
            Changed?.Invoke();
        }
    }

    private MpvVideoPlayerControl EnsurePlayer()
    {
        if (_player is not null) return _player;
        var player = new MpvVideoPlayerControl();
        player.StateChanged += () => _page?.NotifyPlaybackStateChanged();
        player.TimeChanged += time => _page?.SetVideoPosition(time);
        player.DurationChanged += duration => _page?.SetVideoDuration(duration);
        player.VideoSizeChanged += (width, height) =>
        {
            _page?.SetAspectRatio((double)height / width);
            Changed?.Invoke();
        };
        player.EndReached += () => _ = StopAndNotifyAsync();
        player.PlaybackError += () =>
        {
            Error?.Invoke(new InvalidOperationException("播放器无法播放这个视频。"));
            _ = StopAndNotifyAsync();
        };
        return _player = player;
    }

    private async Task StopAndNotifyAsync()
    {
        try { await StopAsync(); }
        catch (Exception ex) { Error?.Invoke(ex); }
        Changed?.Invoke();
    }

    public void Pause() => _player?.Pause();

    public async Task StopAsync()
    {
        _request?.Cancel();
        await _transition.WaitAsync();
        try { await StopPlayerAsync(); }
        finally { _transition.Release(); }
    }

    private async Task StopPlayerAsync()
    {
        _page?.SetPlaybackHost(null);
        _page = null;
        try
        {
            if (_player is not null)
            {
                await _player.StopAsync();
                _parkingHost.Content = _player;
            }
        }
        finally
        {
            _lease?.Dispose();
            _lease = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try { await StopAsync(); await _operation; }
        finally
        {
            try { if (_player is not null) await _player.DisposeAsync(); }
            finally { _parkingHost.Content = null; _transition.Dispose(); }
        }
    }
}
