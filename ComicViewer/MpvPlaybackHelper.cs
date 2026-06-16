using OpenTK.Graphics.OpenGL4;
using OpenTK.Wpf;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace ComicViewer;

public sealed class VideoPlaybackSession : IDisposable
{
    private const string StreamUri = "comic://media";

    private readonly ArraySegment<byte> _videoData;
    private readonly object _mpvLock = new();
    private readonly MpvOpenGlVideoView _view;
    private readonly GCHandle _streamUserDataHandle;
    private IntPtr _mpv;
    private Task? _eventLoopTask;
    private long _displayWidth;
    private long _displayHeight;
    private bool _hasShownVideoSurface;
    private bool _isInitialized;
    private bool _isDisposed;

    private ArraySegment<byte> VideoData => _videoData;

    public VideoPlaybackSession(ArraySegment<byte> videoData)
    {
        if (videoData.Array is null)
        {
            throw new ArgumentException("Video data must reference a byte array.", nameof(videoData));
        }

        _videoData = videoData;
        _view = new MpvOpenGlVideoView();
        _view.FirstFrameRendered += ShowVideoSurface;
        _streamUserDataHandle = GCHandle.Alloc(this);
        _mpv = MpvNative.Create();
        MpvNative.Check(_mpv != IntPtr.Zero ? 0 : -1, "创建 mpv 播放器失败。");
    }

    public event Action? Playing;
    public event Action? Paused;
    public event Action<long>? DurationChanged;
    public event Action<long>? TimeChanged;
    public event Action<long, long>? VideoSizeChanged;
    public event Action? EndReached;
    public event Action? PlaybackError;

    public FrameworkElement View => _view;

    public static Task<ArraySegment<byte>?> TryRenderFirstFramePngAsync(
        ArraySegment<byte> videoData,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => TryRenderFirstFramePng(videoData, timeout, cancellationToken), cancellationToken);
    }

    public Task WaitForHostReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _view.WaitForReadyAsync(timeout, cancellationToken);
    }

    public void Play()
    {
        lock (_mpvLock)
        {
            ThrowIfDisposed();
            EnsureInitialized();
            MpvNative.Check(MpvNative.CommandString(_mpv, "set pause yes"), "mpv 无法准备首帧。");
            MpvNative.Check(MpvNative.CommandString(_mpv, $"loadfile {StreamUri} replace"), "mpv 无法载入内存视频流。");
        }
    }

    public void Pause()
    {
        ExecuteCommand("set pause yes");
    }

    public void Resume()
    {
        ExecuteCommand("set pause no");
    }

    public void Stop()
    {
        ExecuteCommand("stop");
    }

    public void Dispose()
    {
        IntPtr mpv;
        lock (_mpvLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            mpv = _mpv;
            _mpv = IntPtr.Zero;
        }

        DisposeView();

        if (mpv != IntPtr.Zero)
        {
            MpvNative.TerminateDestroy(mpv);
        }

        try
        {
            _eventLoopTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        if (_streamUserDataHandle.IsAllocated)
        {
            _streamUserDataHandle.Free();
        }
    }

    private void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        MpvNative.Check(MpvNative.SetOptionString(_mpv, "config", "no"), "mpv 选项设置失败。");
        MpvNative.Check(MpvNative.SetOptionString(_mpv, "terminal", "no"), "mpv 选项设置失败。");
        MpvNative.Check(MpvNative.SetOptionString(_mpv, "osc", "no"), "mpv 选项设置失败。");
        MpvNative.Check(MpvNative.SetOptionString(_mpv, "input-default-bindings", "no"), "mpv 选项设置失败。");
        MpvNative.Check(MpvNative.SetOptionString(_mpv, "input-vo-keyboard", "no"), "mpv 选项设置失败。");
        MpvNative.Check(MpvNative.SetOptionString(_mpv, "vo", "libmpv"), "mpv 选项设置失败。");
        _ = MpvNative.SetOptionString(_mpv, "hwdec", "auto-safe");
        _ = MpvNative.SetOptionString(_mpv, "video-timing-offset", "0");
        MpvNative.Check(MpvNative.Initialize(_mpv), "mpv 初始化失败。");
        MpvNative.Check(MpvNative.StreamCbAddRo(_mpv, "comic", GCHandle.ToIntPtr(_streamUserDataHandle), MpvNative.OpenStreamCallback), "mpv 内存流注册失败。");
        MpvNative.Check(_view.InitializeRenderer(_mpv), "mpv OpenGL 渲染器初始化失败。");
        MpvNative.ObserveProperty(_mpv, 1, "time-pos", MpvFormat.Double);
        MpvNative.ObserveProperty(_mpv, 2, "duration", MpvFormat.Double);
        MpvNative.ObserveProperty(_mpv, 3, "pause", MpvFormat.Flag);
        MpvNative.ObserveProperty(_mpv, 4, "dwidth", MpvFormat.Int64);
        MpvNative.ObserveProperty(_mpv, 5, "dheight", MpvFormat.Int64);

        _isInitialized = true;
        _eventLoopTask = Task.Run(EventLoop);
    }

    private void ExecuteCommand(string command)
    {
        lock (_mpvLock)
        {
            if (_isDisposed || _mpv == IntPtr.Zero || !_isInitialized)
            {
                return;
            }

            _ = MpvNative.CommandString(_mpv, command);
        }
    }

    private void EventLoop()
    {
        while (true)
        {
            MpvEvent mpvEvent;
            lock (_mpvLock)
            {
                if (_isDisposed || _mpv == IntPtr.Zero)
                {
                    return;
                }

                var eventPtr = MpvNative.WaitEvent(_mpv, 0.1);
                if (eventPtr == IntPtr.Zero)
                {
                    continue;
                }

                mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPtr);
            }

            if (mpvEvent.EventId == MpvEventId.None)
            {
                continue;
            }

            if (mpvEvent.EventId == MpvEventId.Shutdown)
            {
                return;
            }

            HandleEvent(mpvEvent);
        }
    }

    private void HandleEvent(MpvEvent mpvEvent)
    {
        switch (mpvEvent.EventId)
        {
            case MpvEventId.VideoReconfig:
            case MpvEventId.PlaybackRestart:
                _view.RequestRender();
                break;
            case MpvEventId.PropertyChange:
                HandlePropertyChange(mpvEvent.ReplyUserData, mpvEvent.Data);
                break;
            case MpvEventId.EndFile:
                HandleEndFile(mpvEvent.Data);
                break;
        }
    }

    private void HandlePropertyChange(ulong replyUserData, IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        var property = Marshal.PtrToStructure<MpvEventProperty>(data);
        if (property.Data == IntPtr.Zero)
        {
            return;
        }

        switch (replyUserData)
        {
            case 1 when property.Format == MpvFormat.Double:
                TimeChanged?.Invoke(ToMilliseconds(Marshal.PtrToStructure<double>(property.Data)));
                break;
            case 2 when property.Format == MpvFormat.Double:
                DurationChanged?.Invoke(ToMilliseconds(Marshal.PtrToStructure<double>(property.Data)));
                break;
            case 3 when property.Format == MpvFormat.Flag:
                if (Marshal.ReadInt32(property.Data) != 0)
                {
                    Paused?.Invoke();
                }
                else
                {
                    Playing?.Invoke();
                }

                break;
            case 4 when property.Format == MpvFormat.Int64:
                _displayWidth = Math.Max(0, Marshal.ReadInt64(property.Data));
                NotifyVideoSizeIfReady();
                break;
            case 5 when property.Format == MpvFormat.Int64:
                _displayHeight = Math.Max(0, Marshal.ReadInt64(property.Data));
                NotifyVideoSizeIfReady();
                break;
        }
    }

    private void ShowVideoSurface()
    {
        if (_hasShownVideoSurface)
        {
            return;
        }

        _hasShownVideoSurface = true;
        Playing?.Invoke();
        Task.Run(() => ExecuteCommand("set pause no"));
    }

    private void NotifyVideoSizeIfReady()
    {
        if (_displayWidth > 0 && _displayHeight > 0)
        {
            VideoSizeChanged?.Invoke(_displayWidth, _displayHeight);
        }
    }

    private void HandleEndFile(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            EndReached?.Invoke();
            return;
        }

        var endFile = Marshal.PtrToStructure<MpvEventEndFile>(data);
        if (endFile.Reason == MpvEndFileReason.Error)
        {
            PlaybackError?.Invoke();
        }
        else
        {
            EndReached?.Invoke();
        }
    }

    private static long ToMilliseconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return 0;
        }

        return (long)Math.Round(Math.Max(0, seconds) * 1000d);
    }

    private void DisposeView()
    {
        void DisposeCore()
        {
            _view.FirstFrameRendered -= ShowVideoSurface;
            _view.Dispose();
        }

        if (_view.Dispatcher.CheckAccess())
        {
            DisposeCore();
        }
        else
        {
            _view.Dispatcher.Invoke(DisposeCore);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed || _mpv == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(VideoPlaybackSession));
        }
    }

    private sealed class CoverFrameStreamSource
    {
        public CoverFrameStreamSource(ArraySegment<byte> videoData)
        {
            VideoData = videoData;
        }

        public ArraySegment<byte> VideoData { get; }
    }

    private sealed class MpvStreamCookie : IDisposable
    {
        private readonly ArraySegment<byte> _data;
        private readonly object _lock = new();
        private long _position;
        private bool _isCanceled;

        public MpvStreamCookie(ArraySegment<byte> data)
        {
            _data = data;
        }

        public long Size => _data.Count;

        public long Read(IntPtr buffer, ulong requestedBytes)
        {
            lock (_lock)
            {
                if (_isCanceled || _data.Array is null || _position >= _data.Count)
                {
                    return 0;
                }

                var count = (int)Math.Min((long)Math.Min(requestedBytes, int.MaxValue), _data.Count - _position);
                Marshal.Copy(_data.Array, _data.Offset + (int)_position, buffer, count);
                _position += count;
                return count;
            }
        }

        public long Seek(long offset)
        {
            lock (_lock)
            {
                _position = Math.Clamp(offset, 0, _data.Count);
                return _position;
            }
        }

        public void Cancel()
        {
            lock (_lock)
            {
                _isCanceled = true;
            }
        }

        public void Dispose()
        {
            Cancel();
        }
    }

    private static int OpenStream(IntPtr userData, IntPtr uri, IntPtr info)
    {
        try
        {
            ArraySegment<byte> videoData;
            var streamSource = GCHandle.FromIntPtr(userData).Target;
            if (streamSource is VideoPlaybackSession session)
            {
                videoData = session.VideoData;
            }
            else if (streamSource is CoverFrameStreamSource source)
            {
                videoData = source.VideoData;
            }
            else
            {
                return -1;
            }

            var cookie = new MpvStreamCookie(videoData);
            var cookieHandle = GCHandle.Alloc(cookie);
            var streamInfo = Marshal.PtrToStructure<MpvStreamCbInfo>(info);
            streamInfo.Cookie = GCHandle.ToIntPtr(cookieHandle);
            streamInfo.Read = MpvNative.ReadStreamCallbackPtr;
            streamInfo.Seek = MpvNative.SeekStreamCallbackPtr;
            streamInfo.Size = MpvNative.SizeStreamCallbackPtr;
            streamInfo.Close = MpvNative.CloseStreamCallbackPtr;
            streamInfo.Cancel = MpvNative.CancelStreamCallbackPtr;
            Marshal.StructureToPtr(streamInfo, info, false);
            return 0;
        }
        catch
        {
            return -1;
        }
    }

    private static long ReadStream(IntPtr cookieHandle, IntPtr buffer, ulong requestedBytes)
    {
        return GCHandle.FromIntPtr(cookieHandle).Target is MpvStreamCookie cookie
            ? cookie.Read(buffer, requestedBytes)
            : -1;
    }

    private static long SeekStream(IntPtr cookieHandle, long offset)
    {
        return GCHandle.FromIntPtr(cookieHandle).Target is MpvStreamCookie cookie
            ? cookie.Seek(offset)
            : -1;
    }

    private static long SizeStream(IntPtr cookieHandle)
    {
        return GCHandle.FromIntPtr(cookieHandle).Target is MpvStreamCookie cookie
            ? cookie.Size
            : -1;
    }

    private static void CloseStream(IntPtr cookieHandle)
    {
        var handle = GCHandle.FromIntPtr(cookieHandle);
        if (handle.Target is IDisposable disposable)
        {
            disposable.Dispose();
        }

        handle.Free();
    }

    private static void CancelStream(IntPtr cookieHandle)
    {
        if (GCHandle.FromIntPtr(cookieHandle).Target is MpvStreamCookie cookie)
        {
            cookie.Cancel();
        }
    }

    private static ArraySegment<byte>? TryRenderFirstFramePng(
        ArraySegment<byte> videoData,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (videoData.Array is null)
        {
            return null;
        }

        var source = new CoverFrameStreamSource(videoData);
        var sourceHandle = GCHandle.Alloc(source);
        var tempDir = Path.Combine(Path.GetTempPath(), "ComicViewer", "mpv-cover-" + Guid.NewGuid().ToString("N"));
        IntPtr mpv = IntPtr.Zero;
        try
        {
            Directory.CreateDirectory(tempDir);
            mpv = MpvNative.Create();
            if (mpv == IntPtr.Zero)
            {
                return null;
            }

            SetCoverOption(mpv, "config", "no");
            SetCoverOption(mpv, "terminal", "no");
            SetCoverOption(mpv, "osc", "no");
            SetCoverOption(mpv, "audio", "no");
            SetCoverOption(mpv, "vo", "image");
            SetCoverOption(mpv, "vo-image-format", "png");
            SetCoverOption(mpv, "vo-image-outdir", tempDir);
            SetCoverOption(mpv, "frames", "1");

            if (MpvNative.Initialize(mpv) < 0
                || MpvNative.StreamCbAddRo(mpv, "comic", GCHandle.ToIntPtr(sourceHandle), MpvNative.OpenStreamCallback) < 0
                || MpvNative.CommandString(mpv, $"loadfile {StreamUri} replace") < 0)
            {
                return null;
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var eventPtr = MpvNative.WaitEvent(mpv, 0.1);
                if (eventPtr == IntPtr.Zero)
                {
                    continue;
                }

                var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPtr);
                if (mpvEvent.EventId is MpvEventId.EndFile or MpvEventId.Shutdown)
                {
                    break;
                }
            }

            var framePath = Directory
                .EnumerateFiles(tempDir, "*.png", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (framePath is null)
            {
                return default(ArraySegment<byte>?);
            }

            return new ArraySegment<byte>(File.ReadAllBytes(framePath));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (mpv != IntPtr.Zero)
            {
                MpvNative.TerminateDestroy(mpv);
            }

            if (sourceHandle.IsAllocated)
            {
                sourceHandle.Free();
            }

            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void SetCoverOption(IntPtr mpv, string name, string value)
    {
        _ = MpvNative.SetOptionString(mpv, name, value);
    }

    private static class MpvNative
    {
        public static readonly OpenStreamDelegate OpenStreamCallback = OpenStream;
        public static readonly ReadStreamDelegate ReadStreamCallback = ReadStream;
        public static readonly SeekStreamDelegate SeekStreamCallback = SeekStream;
        public static readonly SizeStreamDelegate SizeStreamCallback = SizeStream;
        public static readonly CloseStreamDelegate CloseStreamCallback = CloseStream;
        public static readonly CancelStreamDelegate CancelStreamCallback = CancelStream;

        public static readonly IntPtr ReadStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(ReadStreamCallback);
        public static readonly IntPtr SeekStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(SeekStreamCallback);
        public static readonly IntPtr SizeStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(SizeStreamCallback);
        public static readonly IntPtr CloseStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(CloseStreamCallback);
        public static readonly IntPtr CancelStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(CancelStreamCallback);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_create")]
        public static extern IntPtr Create();

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_initialize")]
        public static extern int Initialize(IntPtr handle);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_terminate_destroy")]
        public static extern void TerminateDestroy(IntPtr handle);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "mpv_set_option_string")]
        public static extern int SetOptionString(IntPtr handle, string name, string value);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "mpv_command_string")]
        public static extern int CommandString(IntPtr handle, string args);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "mpv_observe_property")]
        public static extern int ObserveProperty(IntPtr handle, ulong replyUserData, string name, MpvFormat format);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_wait_event")]
        public static extern IntPtr WaitEvent(IntPtr handle, double timeout);

        [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "mpv_stream_cb_add_ro")]
        public static extern int StreamCbAddRo(IntPtr handle, string protocol, IntPtr userData, OpenStreamDelegate openFn);

        public static void Check(int result, string message)
        {
            if (result < 0)
            {
                throw new InvalidOperationException($"{message} mpv 错误码: {result}");
            }
        }
    }
}

public sealed class MpvOpenGlVideoView : GLWpfControl
{
    private const int MpvRenderUpdateFrame = 1;

    private static readonly MpvOpenGlGetProcAddressDelegate GetProcAddressCallback = GetOpenGlProcAddress;
    private static readonly IntPtr GetProcAddressCallbackPtr = Marshal.GetFunctionPointerForDelegate(GetProcAddressCallback);

    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly MpvRenderUpdateDelegate _updateCallback;
    private readonly IntPtr _framebufferPtr;
    private readonly IntPtr _flipYPtr;
    private readonly MpvRenderParam[] _renderParams;
    private bool _isStarted;
    private bool _isDisposed;
    private bool _isRendering;
    private bool _hasRenderedFrame;
    private bool _forceRender = true;
    private int _renderRequestPending;
    private int _lastFramebuffer;
    private int _lastWidth;
    private int _lastHeight;
    private IntPtr _renderContext;

    public MpvOpenGlVideoView()
    {
        _updateCallback = OnMpvRenderUpdate;
        _framebufferPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlFbo>());
        _flipYPtr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(_flipYPtr, 1);
        _renderParams =
        [
            new MpvRenderParam(MpvRenderParamType.OpenGlFbo, _framebufferPtr),
            new MpvRenderParam(MpvRenderParamType.FlipY, _flipYPtr),
            new MpvRenderParam(MpvRenderParamType.Invalid, IntPtr.Zero)
        ];

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Ready += OnReady;
        Render += OnRender;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Cursor = System.Windows.Input.Cursors.Arrow;
        Focusable = false;
    }

    public event Action? FirstFrameRendered;

    public Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        StartControl();
        return _ready.Task.WaitAsync(timeout, cancellationToken);
    }

    public int InitializeRenderer(IntPtr mpv)
    {
        if (Dispatcher.CheckAccess())
        {
            return InitializeRendererCore(mpv);
        }

        return Dispatcher.Invoke(() => InitializeRendererCore(mpv));
    }

    public void RequestRender()
    {
        _forceRender = true;
        Interlocked.Exchange(ref _renderRequestPending, 1);
    }

    public new void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CompositionTarget.Rendering -= OnCompositionRendering;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        Render -= OnRender;
        Ready -= OnReady;

        if (Dispatcher.CheckAccess())
        {
            DisposeRendererCore();
        }
        else
        {
            Dispatcher.Invoke(DisposeRendererCore);
        }

        Marshal.FreeHGlobal(_framebufferPtr);
        Marshal.FreeHGlobal(_flipYPtr);
        base.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnCompositionRendering;
        CompositionTarget.Rendering += OnCompositionRendering;
        StartControl();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (_isDisposed || Interlocked.CompareExchange(ref _renderRequestPending, 1, 1) == 0)
        {
            return;
        }

        InvalidateVisual();
    }

    private void StartControl()
    {
        if (_isStarted || _isDisposed)
        {
            return;
        }

        _isStarted = true;
        Start(new GLWpfControlSettings
        {
            MajorVersion = 3,
            MinorVersion = 3,
            RenderContinuously = false,
            UseDeviceDpi = true,
            TransparentBackground = true
        });
    }

    private void OnReady()
    {
        if (!TryMakeCurrent())
        {
            return;
        }

        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        _ready.TrySetResult();
    }

    private int InitializeRendererCore(IntPtr mpv)
    {
        if (_renderContext != IntPtr.Zero)
        {
            return 0;
        }

        StartControl();
        if (!_ready.Task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("OpenGL 控件尚未准备好。");
        }

        if (!TryMakeCurrent())
        {
            throw new InvalidOperationException("OpenGL context 尚未准备好。");
        }

        var apiTypePtr = Marshal.StringToHGlobalAnsi("opengl");
        var initParams = new MpvOpenGlInitParams(GetProcAddressCallbackPtr, IntPtr.Zero);
        var initParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvOpenGlInitParams>());
        try
        {
            Marshal.StructureToPtr(initParams, initParamsPtr, false);
            var createParams = new[]
            {
                new MpvRenderParam(MpvRenderParamType.ApiType, apiTypePtr),
                new MpvRenderParam(MpvRenderParamType.OpenGlInitParams, initParamsPtr),
                new MpvRenderParam(MpvRenderParamType.Invalid, IntPtr.Zero)
            };

            var result = MpvRenderNative.RenderContextCreate(out _renderContext, mpv, createParams);
            if (result < 0)
            {
                _renderContext = IntPtr.Zero;
                return result;
            }

            MpvRenderNative.RenderContextSetUpdateCallback(_renderContext, _updateCallback, IntPtr.Zero);
            RequestRender();
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(initParamsPtr);
            Marshal.FreeHGlobal(apiTypePtr);
        }
    }

    private void OnMpvRenderUpdate(IntPtr callbackContext)
    {
        RequestRender();
    }

    private void OnRender(TimeSpan delta)
    {
        if (_isDisposed || _isRendering)
        {
            return;
        }

        _isRendering = true;
        try
        {
            if (!TryMakeCurrent())
            {
                return;
            }

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
            GL.Viewport(0, 0, Math.Max(1, FrameBufferWidth), Math.Max(1, FrameBufferHeight));

            if (_renderContext == IntPtr.Zero)
            {
                GL.Clear(ClearBufferMask.ColorBufferBit);
                return;
            }

            var updateFlags = MpvRenderNative.RenderContextUpdate(_renderContext);
            var framebufferChanged = Framebuffer != _lastFramebuffer
                || FrameBufferWidth != _lastWidth
                || FrameBufferHeight != _lastHeight;
            if ((updateFlags & MpvRenderUpdateFrame) == 0 && !_forceRender && !framebufferChanged)
            {
                return;
            }

            _lastFramebuffer = Framebuffer;
            _lastWidth = FrameBufferWidth;
            _lastHeight = FrameBufferHeight;

            var fbo = new MpvOpenGlFbo(
                Framebuffer,
                Math.Max(1, FrameBufferWidth),
                Math.Max(1, FrameBufferHeight),
                0);
            Marshal.StructureToPtr(fbo, _framebufferPtr, false);

            MpvRenderNative.RenderContextRender(_renderContext, _renderParams);
            MpvRenderNative.RenderContextReportSwap(_renderContext);
            _forceRender = false;

            if (!_hasRenderedFrame)
            {
                _hasRenderedFrame = true;
                FirstFrameRendered?.Invoke();
            }
        }
        finally
        {
            _isRendering = false;
            Interlocked.Exchange(ref _renderRequestPending, 0);
        }
    }

    private void DisposeRendererCore()
    {
        if (_renderContext != IntPtr.Zero)
        {
            if (!TryMakeCurrent())
            {
                return;
            }

            MpvRenderNative.RenderContextSetUpdateCallback(_renderContext, null, IntPtr.Zero);
            MpvRenderNative.RenderContextFree(_renderContext);
            _renderContext = IntPtr.Zero;
        }
    }

    private bool TryMakeCurrent()
    {
        if (Context is null)
        {
            return false;
        }

        Context.MakeCurrent();
        return true;
    }

    private static IntPtr GetOpenGlProcAddress(IntPtr context, IntPtr name)
    {
        var procName = Marshal.PtrToStringAnsi(name);
        if (string.IsNullOrWhiteSpace(procName))
        {
            return IntPtr.Zero;
        }

        var address = WglGetProcAddress(procName);
        if (address != IntPtr.Zero && address.ToInt64() > 3)
        {
            return address;
        }

        var module = GetModuleHandle("opengl32.dll");
        return module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, procName);
    }

    [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr WglGetProcAddress(string name);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);
}

public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9
}

public enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    TracksChanged = 9,
    TrackSwitched = 10,
    Idle = 11,
    Pause = 12,
    Unpause = 13,
    Tick = 14,
    ScriptInputDispatch = 15,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    ChapterChange = 23,
    QueueOverflow = 24,
    Hook = 25
}

public enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5
}

public enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserData;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
public struct MpvStreamCbInfo
{
    public IntPtr Cookie;
    public IntPtr Read;
    public IntPtr Seek;
    public IntPtr Size;
    public IntPtr Close;
    public IntPtr Cancel;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct MpvOpenGlInitParams
{
    public MpvOpenGlInitParams(IntPtr getProcAddress, IntPtr getProcAddressContext)
    {
        GetProcAddress = getProcAddress;
        GetProcAddressContext = getProcAddressContext;
    }

    public readonly IntPtr GetProcAddress;
    public readonly IntPtr GetProcAddressContext;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct MpvOpenGlFbo
{
    public MpvOpenGlFbo(int framebuffer, int width, int height, int internalFormat)
    {
        Framebuffer = framebuffer;
        Width = width;
        Height = height;
        InternalFormat = internalFormat;
    }

    public readonly int Framebuffer;
    public readonly int Width;
    public readonly int Height;
    public readonly int InternalFormat;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct MpvRenderParam
{
    public MpvRenderParam(MpvRenderParamType type, IntPtr data)
    {
        Type = type;
        Data = data;
    }

    public readonly MpvRenderParamType Type;
    public readonly IntPtr Data;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int OpenStreamDelegate(IntPtr userData, IntPtr uri, IntPtr info);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate long ReadStreamDelegate(IntPtr cookie, IntPtr buffer, ulong nbytes);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate long SeekStreamDelegate(IntPtr cookie, long offset);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate long SizeStreamDelegate(IntPtr cookie);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void CloseStreamDelegate(IntPtr cookie);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void CancelStreamDelegate(IntPtr cookie);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate IntPtr MpvOpenGlGetProcAddressDelegate(IntPtr context, IntPtr name);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void MpvRenderUpdateDelegate(IntPtr callbackContext);

public static class MpvRenderNative
{
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_create")]
    public static extern int RenderContextCreate(out IntPtr context, IntPtr handle, [In] MpvRenderParam[] parameters);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_set_update_callback")]
    public static extern void RenderContextSetUpdateCallback(IntPtr context, MpvRenderUpdateDelegate? callback, IntPtr callbackContext);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_update")]
    public static extern ulong RenderContextUpdate(IntPtr context);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_render")]
    public static extern int RenderContextRender(IntPtr context, [In] MpvRenderParam[] parameters);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_report_swap")]
    public static extern void RenderContextReportSwap(IntPtr context);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_render_context_free")]
    public static extern void RenderContextFree(IntPtr context);
}
