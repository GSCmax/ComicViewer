using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

        using var session = new HeadlessMpvSession(videoData);
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
            if (session.CanCaptureFrame && session.TryCaptureThumbnail(out var image))
            {
                return image;
            }

            if (session.HasEnded)
            {
                return null;
            }

            Thread.Sleep(15);
        }

        return null;
    }

    private sealed class HeadlessMpvSession : IDisposable
    {
        private readonly GCHandle _streamUserDataHandle;
        private readonly object _sourceLock = new();
        private readonly ArraySegment<byte> _videoData;
        private IntPtr _mpv;
        private bool _isDisposed;

        public HeadlessMpvSession(ArraySegment<byte> videoData)
        {
            _videoData = videoData;
            _streamUserDataHandle = GCHandle.Alloc(this);
            _mpv = HeadlessMpvNative.Create();
            HeadlessMpvNative.Check(_mpv != IntPtr.Zero ? 0 : -1, "创建 mpv 缩略图实例失败。");
            Initialize();
        }

        public bool HasEnded { get; private set; }

        public bool CanCaptureFrame { get; private set; }

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

                if (mpvEvent.EventId is MpvEventId.FileLoaded or MpvEventId.VideoReconfig or MpvEventId.PlaybackRestart)
                {
                    CanCaptureFrame = true;
                }
            }
        }

        public bool TryCaptureThumbnail(out VideoThumbnailImage? image)
        {
            return HeadlessMpvNative.TryCaptureRawScreenshot(_mpv, out image);
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
            HeadlessMpvNative.Check(HeadlessMpvNative.SetOptionString(_mpv, "vo", "null"), "mpv 缩略图视频输出设置失败。");
            HeadlessMpvNative.Check(HeadlessMpvNative.SetOptionString(_mpv, "pause", "yes"), "mpv 缩略图暂停设置失败。");
            _ = HeadlessMpvNative.SetOptionString(_mpv, "vf", $"scale={ThumbnailWidth}:-2");
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

            [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_command_node")]
            public static extern int CommandNode(IntPtr handle, ref MpvNode args, out MpvNode result);

            [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_free_node_contents")]
            public static extern void FreeNodeContents(ref MpvNode node);

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

            public static bool TryCaptureRawScreenshot(IntPtr handle, out VideoThumbnailImage? image)
            {
                image = null;
                using var command = MpvCommandNode.Create("screenshot-raw", "video");
                var result = new MpvNode();
                var commandResult = CommandNode(handle, ref command.Node, out result);
                try
                {
                    return commandResult >= 0
                        && TryCreateThumbnailFromScreenshotNode(result, out image);
                }
                finally
                {
                    FreeNodeContents(ref result);
                }
            }
        }
    }

    private static bool TryCreateThumbnailFromScreenshotNode(MpvNode node, out VideoThumbnailImage? image)
    {
        image = null;
        if (node.Format != MpvFormat.NodeMap || node.U.List == IntPtr.Zero)
        {
            return false;
        }

        var map = Marshal.PtrToStructure<MpvNodeList>(node.U.List);
        var width = 0;
        var height = 0;
        var stride = 0;
        string? format = null;
        byte[]? pixels = null;

        for (var i = 0; i < map.Num; i++)
        {
            var keyPointer = Marshal.ReadIntPtr(map.Keys, i * IntPtr.Size);
            var key = Marshal.PtrToStringAnsi(keyPointer);
            var valuePointer = IntPtr.Add(map.Values, i * Marshal.SizeOf<MpvNode>());
            var value = Marshal.PtrToStructure<MpvNode>(valuePointer);

            switch (key)
            {
                case "w":
                    width = ReadNodeInt32(value);
                    break;
                case "h":
                    height = ReadNodeInt32(value);
                    break;
                case "stride":
                    stride = ReadNodeInt32(value);
                    break;
                case "format":
                    format = value.Format == MpvFormat.String
                        ? Marshal.PtrToStringAnsi(value.U.String)
                        : null;
                    break;
                case "data":
                    pixels = ReadNodeByteArray(value);
                    break;
            }
        }

        if (width <= 0 || height <= 0 || stride <= 0 || string.IsNullOrWhiteSpace(format) || pixels is null)
        {
            return false;
        }

        if (!TryEncodeRawFrameAsPng(pixels, width, height, stride, format, out var imageData))
        {
            return false;
        }

        image = new VideoThumbnailImage(imageData, width, height, width, height);
        return true;
    }

    private static int ReadNodeInt32(MpvNode node)
    {
        return node.Format == MpvFormat.Int64
            ? (int)Math.Clamp(node.U.Int64, int.MinValue, int.MaxValue)
            : 0;
    }

    private static byte[]? ReadNodeByteArray(MpvNode node)
    {
        if (node.Format != MpvFormat.ByteArray || node.U.ByteArray == IntPtr.Zero)
        {
            return null;
        }

        var byteArray = Marshal.PtrToStructure<MpvByteArray>(node.U.ByteArray);
        var size = checked((int)byteArray.Size);
        var bytes = new byte[size];
        if (size > 0)
        {
            Marshal.Copy(byteArray.Data, bytes, 0, size);
        }

        return bytes;
    }

    private static bool TryEncodeRawFrameAsPng(
        byte[] pixels,
        int width,
        int height,
        int stride,
        string format,
        out ArraySegment<byte> imageData)
    {
        imageData = default;
        var pixelFormat = format switch
        {
            "bgr0" => PixelFormats.Bgr32,
            "bgra" => PixelFormats.Bgra32,
            "bgr24" => PixelFormats.Bgr24,
            "rgb24" => PixelFormats.Rgb24,
            "rgba" => PixelFormats.Bgra32,
            _ => PixelFormats.Default
        };

        if (pixelFormat == PixelFormats.Default)
        {
            return false;
        }

        var encodedPixels = format == "rgba"
            ? ConvertRgbaToBgra(pixels)
            : pixels;

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            pixelFormat,
            null,
            encodedPixels,
            stride);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        imageData = new ArraySegment<byte>(stream.ToArray());
        return true;
    }

    private static byte[] ConvertRgbaToBgra(byte[] pixels)
    {
        var converted = new byte[pixels.Length];
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            converted[i] = pixels[i + 2];
            converted[i + 1] = pixels[i + 1];
            converted[i + 2] = pixels[i];
            converted[i + 3] = pixels[i + 3];
        }

        return converted;
    }

    private sealed class MpvCommandNode : IDisposable
    {
        private readonly IntPtr _listPointer;
        private readonly IntPtr _valuesPointer;
        private readonly List<IntPtr> _stringPointers;

        private MpvCommandNode(IntPtr listPointer, IntPtr valuesPointer, List<IntPtr> stringPointers)
        {
            _listPointer = listPointer;
            _valuesPointer = valuesPointer;
            _stringPointers = stringPointers;
            Node = new MpvNode
            {
                U = new MpvNodeUnion { List = _listPointer },
                Format = MpvFormat.NodeArray
            };
        }

        public MpvNode Node;

        public static MpvCommandNode Create(params string[] args)
        {
            var nodeSize = Marshal.SizeOf<MpvNode>();
            var valuesPointer = Marshal.AllocHGlobal(nodeSize * args.Length);
            var stringPointers = new List<IntPtr>(args.Length);

            for (var i = 0; i < args.Length; i++)
            {
                var stringPointer = Marshal.StringToHGlobalAnsi(args[i]);
                stringPointers.Add(stringPointer);
                var argNode = new MpvNode
                {
                    U = new MpvNodeUnion { String = stringPointer },
                    Format = MpvFormat.String
                };
                Marshal.StructureToPtr(argNode, IntPtr.Add(valuesPointer, i * nodeSize), false);
            }

            var listPointer = Marshal.AllocHGlobal(Marshal.SizeOf<MpvNodeList>());
            Marshal.StructureToPtr(
                new MpvNodeList
                {
                    Num = args.Length,
                    Keys = IntPtr.Zero,
                    Values = valuesPointer
                },
                listPointer,
                false);

            return new MpvCommandNode(listPointer, valuesPointer, stringPointers);
        }

        public void Dispose()
        {
            foreach (var stringPointer in _stringPointers)
            {
                Marshal.FreeHGlobal(stringPointer);
            }

            Marshal.FreeHGlobal(_valuesPointer);
            Marshal.FreeHGlobal(_listPointer);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvNode
{
    public MpvNodeUnion U;
    public MpvFormat Format;
}

[StructLayout(LayoutKind.Explicit)]
internal struct MpvNodeUnion
{
    [FieldOffset(0)]
    public IntPtr String;

    [FieldOffset(0)]
    public long Int64;

    [FieldOffset(0)]
    public double Double;

    [FieldOffset(0)]
    public IntPtr List;

    [FieldOffset(0)]
    public IntPtr ByteArray;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvNodeList
{
    public int Num;
    public IntPtr Values;
    public IntPtr Keys;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvByteArray
{
    public IntPtr Data;
    public nuint Size;
}

internal sealed record VideoThumbnailImage(
    ArraySegment<byte> ImageData,
    long VideoWidth,
    long VideoHeight,
    int PixelWidth,
    int PixelHeight);
