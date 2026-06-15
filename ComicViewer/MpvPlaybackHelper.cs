using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ComicViewer;

public sealed class VideoPlaybackSession : IDisposable
{
    private const string StreamUri = "comic://media";

    private readonly ArraySegment<byte> _videoData;
    private readonly object _mpvLock = new();
    private readonly MpvVideoHost _view = new();
    private readonly GCHandle _streamUserDataHandle;
    private IntPtr _mpv;
    private Task? _eventLoopTask;
    private bool _isInitialized;
    private bool _isDisposed;

    public VideoPlaybackSession(ArraySegment<byte> videoData)
    {
        if (videoData.Array is null)
        {
            throw new ArgumentException("Video data must reference a byte array.", nameof(videoData));
        }

        _videoData = videoData;
        _streamUserDataHandle = GCHandle.Alloc(this);
        _mpv = MpvPlaybackHelper.Create();
        MpvPlaybackHelper.Check(_mpv != IntPtr.Zero ? 0 : -1, "创建 mpv 播放器失败。");
    }

    public event Action? Playing;
    public event Action? Paused;
    public event Action<long>? DurationChanged;
    public event Action<long>? TimeChanged;
    public event Action? EndReached;
    public event Action? PlaybackError;

    public FrameworkElement View => _view;

    public Task WaitForHostReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _view.WaitForHandleAsync(timeout, cancellationToken);
    }

    public void Play()
    {
        lock (_mpvLock)
        {
            ThrowIfDisposed();
            EnsureInitialized();
            MpvPlaybackHelper.Check(MpvPlaybackHelper.CommandString(_mpv, $"loadfile {StreamUri} replace"), "mpv 无法载入内存视频流。");
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

        if (mpv != IntPtr.Zero)
        {
            MpvPlaybackHelper.TerminateDestroy(mpv);
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

        DisposeView();
    }

    private void EnsureInitialized()
    {
        if (_isInitialized)
        {
            return;
        }

        if (_view.PlayerWindowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("视频窗口尚未准备好。");
        }

        MpvPlaybackHelper.Check(MpvPlaybackHelper.SetOptionString(_mpv, "config", "no"), "mpv 选项设置失败。");
        MpvPlaybackHelper.Check(MpvPlaybackHelper.SetOptionString(_mpv, "terminal", "no"), "mpv 选项设置失败。");
        MpvPlaybackHelper.Check(MpvPlaybackHelper.SetOptionString(_mpv, "osc", "no"), "mpv 选项设置失败。");
        MpvPlaybackHelper.Check(MpvPlaybackHelper.SetOptionString(_mpv, "input-default-bindings", "no"), "mpv 选项设置失败。");
        MpvPlaybackHelper.Check(MpvPlaybackHelper.SetOptionString(_mpv, "input-vo-keyboard", "no"), "mpv 选项设置失败。");
        MpvPlaybackHelper.Check(MpvPlaybackHelper.SetOptionString(_mpv, "wid", _view.PlayerWindowHandle.ToInt64().ToString(CultureInfo.InvariantCulture)), "mpv 选项设置失败。");
        MpvPlaybackHelper.Check(MpvPlaybackHelper.Initialize(_mpv), "mpv 初始化失败。");
        MpvPlaybackHelper.Check(MpvPlaybackHelper.StreamCbAddRo(_mpv, "comic", GCHandle.ToIntPtr(_streamUserDataHandle), MpvPlaybackHelper.OpenStreamCallback), "mpv 内存流注册失败。");
        MpvPlaybackHelper.ObserveProperty(_mpv, 1, "time-pos", MpvFormat.Double);
        MpvPlaybackHelper.ObserveProperty(_mpv, 2, "duration", MpvFormat.Double);
        MpvPlaybackHelper.ObserveProperty(_mpv, 3, "pause", MpvFormat.Flag);

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

            _ = MpvPlaybackHelper.CommandString(_mpv, command);
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

                var eventPtr = MpvPlaybackHelper.WaitEvent(_mpv, 0.1);
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
            case MpvEventId.FileLoaded:
                Playing?.Invoke();
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
        if (_view.Dispatcher.CheckAccess())
        {
            _view.Dispose();
        }
        else
        {
            _view.Dispatcher.BeginInvoke((Action)_view.Dispose);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed || _mpv == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(VideoPlaybackSession));
        }
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
            var session = GCHandle.FromIntPtr(userData).Target as VideoPlaybackSession;
            if (session is null)
            {
                return -1;
            }

            var cookie = new MpvStreamCookie(session._videoData);
            var cookieHandle = GCHandle.Alloc(cookie);
            var streamInfo = Marshal.PtrToStructure<MpvStreamCbInfo>(info);
            streamInfo.Cookie = GCHandle.ToIntPtr(cookieHandle);
            streamInfo.Read = MpvPlaybackHelper.ReadStreamCallbackPtr;
            streamInfo.Seek = MpvPlaybackHelper.SeekStreamCallbackPtr;
            streamInfo.Size = MpvPlaybackHelper.SizeStreamCallbackPtr;
            streamInfo.Close = MpvPlaybackHelper.CloseStreamCallbackPtr;
            streamInfo.Cancel = MpvPlaybackHelper.CancelStreamCallbackPtr;
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

    private static class MpvPlaybackHelper
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

public sealed class MpvVideoHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;

    private readonly TaskCompletionSource _handleReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _hwnd;

    public IntPtr PlayerWindowHandle => _hwnd;

    public Task WaitForHandleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _handleReady.Task.WaitAsync(timeout, cancellationToken);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(
            0,
            "static",
            "",
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0,
            0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        _handleReady.TrySetResult();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }

        _hwnd = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_hwnd != IntPtr.Zero)
        {
            MoveWindow(_hwnd, 0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), true);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int nWidth, int nHeight, bool repaint);
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

