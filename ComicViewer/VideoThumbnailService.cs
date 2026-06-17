using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ComicViewer;

internal sealed class VideoThumbnailService
{
    private readonly IVideoThumbnailProvider _provider;

    public VideoThumbnailService(IVideoThumbnailProvider provider)
    {
        _provider = provider;
    }

    public Task<VideoThumbnailImage?> GenerateAsync(
        ArraySegment<byte> videoData,
        Func<bool> isRequestCurrent,
        CancellationToken cancellationToken)
    {
        return _provider.GenerateAsync(videoData, isRequestCurrent, cancellationToken);
    }
}

internal interface IVideoThumbnailProvider
{
    Task<VideoThumbnailImage?> GenerateAsync(
        ArraySegment<byte> videoData,
        Func<bool> isRequestCurrent,
        CancellationToken cancellationToken);
}

internal sealed class HeadlessMpvVideoThumbnailProvider : IVideoThumbnailProvider
{
    private const string StreamUri = "comicthumb://media";
    private const int TimeoutMilliseconds = 3000;
    private const int ThumbnailWidth = 480;

    public Task<VideoThumbnailImage?> GenerateAsync(
        ArraySegment<byte> videoData,
        Func<bool> isRequestCurrent,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => GenerateCore(videoData, isRequestCurrent, cancellationToken), cancellationToken);
    }

    private static VideoThumbnailImage? GenerateCore(
        ArraySegment<byte> videoData,
        Func<bool> isRequestCurrent,
        CancellationToken cancellationToken)
    {
        if (videoData.Array is null)
        {
            throw new ArgumentException("Video data must reference a byte array.", nameof(videoData));
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "ComicViewer", "thumbnails", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        using var session = new HeadlessMpvSession(videoData, tempDirectory);
        try
        {
            session.Load();
            var deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!isRequestCurrent())
                {
                    return null;
                }

                session.PumpEvents();
                var pngPath = Directory.EnumerateFiles(tempDirectory, "*.png").FirstOrDefault();
                if (pngPath is not null && TryReadPngFile(pngPath, out var image))
                {
                    return image;
                }

                if (session.HasEnded && pngPath is null)
                {
                    return null;
                }

                Thread.Sleep(15);
            }

            return null;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static bool TryReadPngFile(string path, out VideoThumbnailImage? image)
    {
        image = null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var stream = new MemoryStream();
            file.CopyTo(stream);
            var bytes = stream.ToArray();
            if (!IsCompletePng(bytes))
            {
                return false;
            }

            var (width, height) = ReadPngSize(bytes);
            image = new VideoThumbnailImage(new ArraySegment<byte>(bytes), width, height, width, height);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsCompletePng(byte[] bytes)
    {
        return bytes.Length >= 24
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47
            && bytes[^8] == 0x49
            && bytes[^7] == 0x45
            && bytes[^6] == 0x4E
            && bytes[^5] == 0x44;
    }

    private static (int Width, int Height) ReadPngSize(byte[] bytes)
    {
        if (bytes.Length < 24
            || bytes[0] != 0x89
            || bytes[1] != 0x50
            || bytes[2] != 0x4E
            || bytes[3] != 0x47)
        {
            return (0, 0);
        }

        return (
            ReadBigEndianInt32(bytes, 16),
            ReadBigEndianInt32(bytes, 20));
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24)
            | (bytes[offset + 1] << 16)
            | (bytes[offset + 2] << 8)
            | bytes[offset + 3];
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class HeadlessMpvSession : IDisposable
    {
        private readonly GCHandle _streamUserDataHandle;
        private readonly string _outputDirectory;
        private readonly object _sourceLock = new();
        private readonly ArraySegment<byte> _videoData;
        private IntPtr _mpv;
        private bool _isDisposed;

        public HeadlessMpvSession(ArraySegment<byte> videoData, string outputDirectory)
        {
            _videoData = videoData;
            _outputDirectory = outputDirectory;
            _streamUserDataHandle = GCHandle.Alloc(this);
            _mpv = HeadlessMpvNative.Create();
            HeadlessMpvNative.Check(_mpv != IntPtr.Zero ? 0 : -1, "创建 mpv 缩略图实例失败。");
            Initialize();
        }

        public bool HasEnded { get; private set; }

        private ArraySegment<byte> VideoData
        {
            get
            {
                lock (_sourceLock)
                {
                    return _videoData;
                }
            }
        }

        public void Load()
        {
            HeadlessMpvNative.Check(HeadlessMpvNative.CommandString(_mpv, $"loadfile {StreamUri} replace"), "mpv 无法载入缩略图视频流。");
        }

        public void PumpEvents()
        {
            while (true)
            {
                var eventPtr = HeadlessMpvNative.WaitEvent(_mpv, 0);
                if (eventPtr == IntPtr.Zero)
                {
                    return;
                }

                var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPtr);
                if (mpvEvent.EventId == MpvEventId.None)
                {
                    return;
                }

                if (mpvEvent.EventId is MpvEventId.EndFile or MpvEventId.Shutdown)
                {
                    HasEnded = true;
                }
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            if (_mpv != IntPtr.Zero)
            {
                HeadlessMpvNative.TerminateDestroy(_mpv);
                _mpv = IntPtr.Zero;
            }

            if (_streamUserDataHandle.IsAllocated)
            {
                _streamUserDataHandle.Free();
            }
        }

        private void Initialize()
        {
            HeadlessMpvNative.Check(HeadlessMpvNative.SetOptionString(_mpv, "config", "no"), "mpv 缩略图选项设置失败。");
            HeadlessMpvNative.Check(HeadlessMpvNative.SetOptionString(_mpv, "terminal", "no"), "mpv 缩略图选项设置失败。");
            HeadlessMpvNative.Check(HeadlessMpvNative.SetOptionString(_mpv, "osc", "no"), "mpv 缩略图选项设置失败。");
            HeadlessMpvNative.Check(HeadlessMpvNative.SetOptionString(_mpv, "audio", "no"), "mpv 缩略图选项设置失败。");
            HeadlessMpvNative.Check(HeadlessMpvNative.SetOptionString(_mpv, "vo", "image"), "mpv 缩略图视频输出设置失败。");
            _ = HeadlessMpvNative.SetOptionString(_mpv, "vf", $"scale={ThumbnailWidth}:-2");
            _ = HeadlessMpvNative.SetOptionString(_mpv, "vo-image-format", "png");
            _ = HeadlessMpvNative.SetOptionString(_mpv, "vo-image-outdir", _outputDirectory);
            _ = HeadlessMpvNative.SetOptionString(_mpv, "frames", "1");
            _ = HeadlessMpvNative.SetOptionString(_mpv, "cache", "no");
            _ = HeadlessMpvNative.SetOptionString(_mpv, "hwdec", "no");
            HeadlessMpvNative.Check(HeadlessMpvNative.Initialize(_mpv), "mpv 缩略图初始化失败。");
            HeadlessMpvNative.Check(
                HeadlessMpvNative.StreamCbAddRo(_mpv, "comicthumb", GCHandle.ToIntPtr(_streamUserDataHandle), HeadlessMpvNative.OpenStreamCallback),
                "mpv 缩略图内存流注册失败。");
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
                var streamSource = GCHandle.FromIntPtr(userData).Target;
                if (streamSource is not HeadlessMpvSession session)
                {
                    return -1;
                }

                var cookie = new MpvStreamCookie(session.VideoData);
                var cookieHandle = GCHandle.Alloc(cookie);
                var streamInfo = Marshal.PtrToStructure<MpvStreamCbInfo>(info);
                streamInfo.Cookie = GCHandle.ToIntPtr(cookieHandle);
                streamInfo.Read = HeadlessMpvNative.ReadStreamCallbackPtr;
                streamInfo.Seek = HeadlessMpvNative.SeekStreamCallbackPtr;
                streamInfo.Size = HeadlessMpvNative.SizeStreamCallbackPtr;
                streamInfo.Close = HeadlessMpvNative.CloseStreamCallbackPtr;
                streamInfo.Cancel = HeadlessMpvNative.CancelStreamCallbackPtr;
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

        private static class HeadlessMpvNative
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
}

internal sealed class WpfMpvVideoThumbnailFrameSource : IVideoThumbnailProvider
{
    private const double CaptureWidth = 480d;

    private readonly MpvVideoPlayerControl _player;
    private readonly ContentControl _renderHost;

    public WpfMpvVideoThumbnailFrameSource(
        MpvVideoPlayerControl player,
        ContentControl renderHost)
    {
        _player = player;
        _renderHost = renderHost;
    }

    public async Task<VideoThumbnailImage?> GenerateAsync(
        ArraySegment<byte> videoData,
        Func<bool> isRequestCurrent,
        CancellationToken cancellationToken)
    {
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long videoWidth = 0;
        long videoHeight = 0;

        void OnFirstFrameRendered() => firstFrame.TrySetResult();
        void OnVideoSizeChanged(long width, long height)
        {
            videoWidth = width;
            videoHeight = height;
        }

        try
        {
            _renderHost.Content = _player;
            _renderHost.UpdateLayout();
            _player.SetSource(videoData);
            await _player.WaitForHostReadyAsync(TimeSpan.FromSeconds(2), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!isRequestCurrent())
            {
                return null;
            }

            _player.FirstFrameRendered += OnFirstFrameRendered;
            _player.VideoSizeChanged += OnVideoSizeChanged;
            _player.PrepareFirstFrame();
            await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await ResizeRenderHostAsync(videoWidth, videoHeight, cancellationToken);

            var frame = isRequestCurrent()
                ? _player.CaptureCurrentFrame()
                : null;
            if (frame is null)
            {
                return null;
            }

            var imageData = await Task.Run(() => EncodeBitmapAsPng(frame), cancellationToken);
            return new VideoThumbnailImage(
                imageData,
                videoWidth,
                videoHeight,
                frame.PixelWidth,
                frame.PixelHeight);
        }
        finally
        {
            _player.FirstFrameRendered -= OnFirstFrameRendered;
            _player.VideoSizeChanged -= OnVideoSizeChanged;
            if (isRequestCurrent())
            {
                _player.Stop();
            }
        }
    }

    private static async Task ResizeRenderHostAsync(
        MpvVideoPlayerControl player,
        ContentControl renderHost,
        long videoWidth,
        long videoHeight,
        CancellationToken cancellationToken)
    {
        if (videoWidth <= 0 || videoHeight <= 0)
        {
            return;
        }

        var captureHeight = Math.Max(1d, Math.Round(CaptureWidth * videoHeight / videoWidth));
        if (Math.Abs(renderHost.Width - CaptureWidth) > 0.1
            || Math.Abs(renderHost.Height - captureHeight) > 0.1)
        {
            renderHost.Width = CaptureWidth;
            renderHost.Height = captureHeight;
            renderHost.UpdateLayout();
        }

        player.RequestRender();
        await renderHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(30, cancellationToken);
    }

    private Task ResizeRenderHostAsync(
        long videoWidth,
        long videoHeight,
        CancellationToken cancellationToken)
    {
        return ResizeRenderHostAsync(_player, _renderHost, videoWidth, videoHeight, cancellationToken);
    }

    private static ArraySegment<byte> EncodeBitmapAsPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new ArraySegment<byte>(stream.ToArray());
    }
}

internal sealed record VideoThumbnailImage(
    ArraySegment<byte> ImageData,
    long VideoWidth,
    long VideoHeight,
    int PixelWidth,
    int PixelHeight);
