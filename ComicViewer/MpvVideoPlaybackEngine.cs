using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ComicViewer;

internal enum PlaybackUpdateKind { Playing, Paused, Duration, Position, Size, FirstFrame, End, Error }
internal readonly record struct PlaybackUpdate(long Version, PlaybackUpdateKind Kind, long Value = 0, long Other = 0);

/// <summary>A single native event/command owner. The UI never waits on the event polling lock.</summary>
internal sealed class MpvVideoPlaybackEngine : IMpvMemoryStreamSource, IAsyncDisposable
{
    private readonly MpvOpenGlVideoView _view;
    private readonly object _sourceLock = new();
    private readonly object _commandSync = new();
    private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Completion)> _commands = new();
    private readonly GCHandle _streamHandle;
    private IntPtr _mpv;
    private Task _eventLoop = Task.CompletedTask;
    private ArraySegment<byte> _source;
    private string? _uri;
    private long _version;
    private long _openedVersion;
    private long _loadedVersion;
    private long _observedVersion;
    private long _playlistId = -1;
    private long _width;
    private long _height;
    private long _shownVersion = -1;
    private volatile bool _shutdown;
    private bool _disposed;
    private Exception? _eventFailure;

    public long Version => Interlocked.Read(ref _version);
    public event Action<PlaybackUpdate>? Updated;

    public MpvVideoPlaybackEngine(MpvOpenGlVideoView view)
    {
        _view = view;
        _streamHandle = GCHandle.Alloc(this);
        try
        {
            _mpv = MpvClientNative.Create();
            MpvClientNative.Check(_mpv == IntPtr.Zero ? -1 : 0, "创建播放器失败。");
            foreach (var (name, value) in new[] { ("config", "no"), ("terminal", "no"), ("osc", "no"),
                ("input-default-bindings", "no"), ("input-vo-keyboard", "no"), ("vo", "libmpv"),
                ("hwdec", "auto-safe"), ("cache", "no"), ("demuxer-max-bytes", "33554432"), ("demuxer-max-back-bytes", "8388608") })
                MpvClientNative.Check(MpvClientNative.SetOptionString(_mpv, name, value), "播放器选项设置失败。");
            MpvClientNative.Check(MpvClientNative.Initialize(_mpv), "播放器初始化失败。");
            MpvClientNative.Check(MpvClientNative.StreamCbAddRo(_mpv, "comic", GCHandle.ToIntPtr(_streamHandle), MpvMemoryStream.OpenStreamCallback), "内存流注册失败。");
            MpvClientNative.Check(_view.InitializeRenderer(_mpv), "渲染器初始化失败。");
            _view.FrameRendered += OnFirstFrame;
            _eventLoop = Task.Factory.StartNew(EventLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            _view.DisposeRenderer();
            if (_mpv != IntPtr.Zero) MpvClientNative.TerminateDestroy(_mpv);
            _streamHandle.Free();
            throw;
        }
    }

    public bool TryGetStreamData(string? uri, out ArraySegment<byte> data)
    {
        lock (_sourceLock)
        {
            data = default;
            if (_uri is null || _uri != uri) return false;
            _openedVersion = Version;
            data = _source;
            return data.Array is not null;
        }
    }

    public Task LoadAsync(ArraySegment<byte> data)
    {
        var version = Interlocked.Increment(ref _version);
        _view.ResetFrameState();
        return Enqueue(() =>
        {
            if (Version != version) return;
            StopCore();
            // Drop notifications from the previous file before installing this file's observers.
            while (ReadEvent(0).EventId != MpvEventId.None) { }
            if (_observedVersion > 0)
                for (ulong property = 1; property <= 5; property++)
                    MpvClientNative.UnobserveProperty(_mpv, ((ulong)_observedVersion << 8) | property);
            _observedVersion = version;
            _loadedVersion = 0;
            _width = _height = 0;
            _playlistId = -1;
            lock (_sourceLock)
            {
                _source = data;
                _uri = "comic://media/" + version.ToString(CultureInfo.InvariantCulture);
                _openedVersion = 0;
            }
            Observe(version, 1, "time-pos", MpvFormat.Double);
            Observe(version, 2, "duration", MpvFormat.Double);
            Observe(version, 3, "pause", MpvFormat.Flag);
            Observe(version, 4, "dwidth", MpvFormat.Int64);
            Observe(version, 5, "dheight", MpvFormat.Int64);
            Command("set pause yes");
            Command($"loadfile comic://media/{version.ToString(CultureInfo.InvariantCulture)} replace");
        });
    }

    public Task StopAsync()
    {
        Interlocked.Increment(ref _version); // Invalidates queued UI notifications immediately.
        return Enqueue(StopCore);
    }

    public void Pause() => Send("set pause yes");
    public void Resume() => Send("set pause no");

    private void Send(string command)
    {
        var version = Version;
        _ = SendAsync();
        async Task SendAsync()
        {
            try { await Enqueue(() => { if (Version == version) Command(command); }); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                Updated?.Invoke(new(version, PlaybackUpdateKind.Error));
            }
        }
    }

    private void StopCore()
    {
        Command("stop");
        lock (_sourceLock) { _source = default; _uri = null; _openedVersion = 0; }
        _loadedVersion = 0;
    }

    private void Observe(long version, ulong id, string name, MpvFormat format) =>
        MpvClientNative.Check(MpvClientNative.ObserveProperty(_mpv, ((ulong)version << 8) | id, name, format), "无法观察播放属性。");
    private void Command(string command) => MpvClientNative.Check(MpvClientNative.CommandString(_mpv, command), "播放命令失败。");

    private Task Enqueue(Action action)
    {
        lock (_commandSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_eventFailure is { } failure) return Task.FromException(failure);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _commands.Enqueue((action, completion));
            MpvClientNative.Wakeup(_mpv);
            return completion.Task;
        }
    }

    private MpvEvent ReadEvent(double timeout) => Marshal.PtrToStructure<MpvEvent>(MpvClientNative.WaitEvent(_mpv, timeout));

    private void EventLoop()
    {
        try
        {
            while (!_shutdown)
            {
                while (_commands.TryDequeue(out var command))
                {
                    try { command.Action(); command.Completion.TrySetResult(); }
                    catch (Exception ex) { command.Completion.TrySetException(ex); }
                }
                if (_shutdown) break;
                HandleEvent(ReadEvent(-1));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            Updated?.Invoke(new(Version, PlaybackUpdateKind.Error));
            lock (_commandSync)
            {
                _eventFailure = ex;
                while (_commands.TryDequeue(out var command)) command.Completion.TrySetException(ex);
            }
        }
    }

    private void HandleEvent(MpvEvent evt)
    {
        if (evt.EventId == MpvEventId.Shutdown) throw new InvalidOperationException("播放器已关闭。");
        if (evt.EventId == MpvEventId.PropertyChange && evt.Data != IntPtr.Zero)
        {
            var version = (long)(evt.ReplyUserData >> 8);
            if (version != Version) return;
            var prop = Marshal.PtrToStructure<MpvEventProperty>(evt.Data);
            if (prop.Data == IntPtr.Zero) return;
            switch (evt.ReplyUserData & 255)
            {
                case 1 when prop.Format == MpvFormat.Double:
                    Updated?.Invoke(new(version, PlaybackUpdateKind.Position, Milliseconds(prop.Data))); break;
                case 2 when prop.Format == MpvFormat.Double:
                    Updated?.Invoke(new(version, PlaybackUpdateKind.Duration, Milliseconds(prop.Data))); break;
                case 3 when prop.Format == MpvFormat.Flag:
                    if (_loadedVersion == version)
                        Updated?.Invoke(new(version, Marshal.ReadInt32(prop.Data) != 0 ? PlaybackUpdateKind.Paused : PlaybackUpdateKind.Playing));
                    break;
                case 4 when prop.Format == MpvFormat.Int64: _width = Marshal.ReadInt64(prop.Data); NotifySize(version); break;
                case 5 when prop.Format == MpvFormat.Int64: _height = Marshal.ReadInt64(prop.Data); NotifySize(version); break;
            }
        }
        else if (evt.EventId == MpvEventId.StartFile && evt.Data != IntPtr.Zero)
            _playlistId = Marshal.ReadInt64(evt.Data);
        else if (evt.EventId == MpvEventId.FileLoaded)
        {
            lock (_sourceLock) _loadedVersion = _openedVersion;
            _view.RequestRender();
        }
        else if (evt.EventId is MpvEventId.VideoReconfig or MpvEventId.PlaybackRestart)
            _view.RequestRender();
        else if (evt.EventId == MpvEventId.EndFile && evt.Data != IntPtr.Zero)
        {
            var end = Marshal.PtrToStructure<MpvEventEndFile>(evt.Data);
            if (end.PlaylistEntryId != _playlistId) return;
            if (end.Reason == MpvEndFileReason.Error) Updated?.Invoke(new(Version, PlaybackUpdateKind.Error));
            else if (end.Reason == MpvEndFileReason.Eof) Updated?.Invoke(new(Version, PlaybackUpdateKind.End));
        }
    }

    private void NotifySize(long version)
    {
        if (_width > 0 && _height > 0) Updated?.Invoke(new(version, PlaybackUpdateKind.Size, _width, _height));
    }

    private void OnFirstFrame()
    {
        var version = Version;
        if (Interlocked.Read(ref _loadedVersion) != version || _shownVersion == version) return;
        _shownVersion = version;
        Updated?.Invoke(new(version, PlaybackUpdateKind.FirstFrame));
        Resume();
    }

    private static long Milliseconds(IntPtr data)
    {
        var value = Marshal.PtrToStructure<double>(data);
        return double.IsFinite(value) ? (long)(Math.Max(0, value) * 1000) : 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        try { await StopAsync(); }
        finally
        {
            lock (_commandSync) _disposed = true;
            _shutdown = true;
            MpvClientNative.Wakeup(_mpv);
            await _eventLoop;
            _view.FrameRendered -= OnFirstFrame;
            try { _view.DisposeRenderer(); }
            finally
            {
                var handle = _mpv;
                _mpv = IntPtr.Zero;
                try { await Task.Run(() => MpvClientNative.TerminateDestroy(handle)); }
                finally
                {
                    lock (_sourceLock) { _source = default; _uri = null; }
                    _streamHandle.Free();
                }
            }
        }
    }
}
