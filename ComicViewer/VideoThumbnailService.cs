using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ComicViewer;

// One serialized owner handles initialization, capture, stop and disposal.
internal sealed class VideoThumbnailService : IDisposable, IMpvMemoryStreamSource
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sourceLock = new();
    private GCHandle _streamHandle;
    private IntPtr _mpv;
    private ArraySegment<byte> _source;
    private string? _uri;
    private bool _opened;
    private long _loadId;
    private int _disposed;

    public async Task<BitmapSource?> GenerateAsync(ArraySegment<byte> videoData, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return await Task.Run(() => Capture(videoData, token), token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public bool TryGetStreamData(string? uri, out ArraySegment<byte> data)
    {
        lock (_sourceLock)
        {
            data = _uri is not null && uri == _uri ? _source : default;
            _opened |= data.Array is not null;
            return data.Array is not null;
        }
    }

    private BitmapSource? Capture(ArraySegment<byte> data, CancellationToken token)
    {
        Initialize();
        var uri = "comicthumb://media/" + ++_loadId;
        lock (_sourceLock) { _source = data; _uri = uri; _opened = false; }
        try
        {
            MpvClientNative.Check(MpvClientNative.CommandString(_mpv, $"loadfile {uri} replace"), "视频封面载入失败。");
            var timer = Stopwatch.StartNew();
            var ready = false;
            while (timer.ElapsedMilliseconds < 3000)
            {
                token.ThrowIfCancellationRequested();
                var evt = ReadEvent(0.015);
                bool opened;
                lock (_sourceLock) opened = _opened;
                if (!opened) continue;
                if (evt.EventId is MpvEventId.EndFile or MpvEventId.Shutdown) return null;
                ready |= evt.EventId is MpvEventId.FileLoaded or MpvEventId.VideoReconfig or MpvEventId.PlaybackRestart;
                if (ready && CaptureBitmap() is { } bitmap) return bitmap;
            }
            return null;
        }
        finally
        {
            lock (_sourceLock) { _source = default; _uri = null; _opened = false; }
            _ = MpvClientNative.CommandString(_mpv, "stop");
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 200)
                if (ReadEvent(0.02).EventId is MpvEventId.None or MpvEventId.EndFile or MpvEventId.Idle or MpvEventId.Shutdown) break;
        }
    }

    private void Initialize()
    {
        if (_mpv != IntPtr.Zero) return;
        _streamHandle = GCHandle.Alloc(this);
        try
        {
            _mpv = MpvClientNative.Create();
            MpvClientNative.Check(_mpv == IntPtr.Zero ? -1 : 0, "创建视频封面实例失败。");
            foreach (var (name, value) in new[] { ("config", "no"), ("terminal", "no"), ("osc", "no"),
                ("audio", "no"), ("vo", "null"), ("pause", "yes"), ("vf", "scale=480:-2"), ("cache", "no"), ("hwdec", "no") })
                MpvClientNative.Check(MpvClientNative.SetOptionString(_mpv, name, value), "视频封面选项设置失败。");
            MpvClientNative.Check(MpvClientNative.Initialize(_mpv), "视频封面初始化失败。");
            MpvClientNative.Check(MpvClientNative.StreamCbAddRo(_mpv, "comicthumb", GCHandle.ToIntPtr(_streamHandle),
                MpvMemoryStream.OpenStreamCallback), "视频封面内存流注册失败。");
        }
        catch { Destroy(); throw; }
    }

    private MpvEvent ReadEvent(double timeout) => Marshal.PtrToStructure<MpvEvent>(MpvClientNative.WaitEvent(_mpv, timeout));

    private BitmapSource? CaptureBitmap()
    {
        // mpv accepts a null-terminated string array; no manually allocated command tree.
        if (MpvClientNative.CommandRet(_mpv, ["screenshot-raw", "video", null], out var result) < 0) return null;
        try
        {
            if (result.Format != MpvFormat.NodeMap || result.U.List == IntPtr.Zero) return null;
            var map = Marshal.PtrToStructure<MpvNodeList>(result.U.List);
            int width = 0, height = 0, stride = 0;
            string? format = null;
            var pixels = new MpvByteArray();
            for (var i = 0; i < map.Num; i++)
            {
                var key = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(map.Keys, i * IntPtr.Size));
                var value = Marshal.PtrToStructure<MpvNode>(IntPtr.Add(map.Values, i * Marshal.SizeOf<MpvNode>()));
                switch (key)
                {
                    case "w" when value.Format == MpvFormat.Int64: width = checked((int)value.U.Int64); break;
                    case "h" when value.Format == MpvFormat.Int64: height = checked((int)value.U.Int64); break;
                    case "stride" when value.Format == MpvFormat.Int64: stride = checked((int)value.U.Int64); break;
                    case "format" when value.Format == MpvFormat.String: format = Marshal.PtrToStringAnsi(value.U.String); break;
                    case "data" when value.Format == MpvFormat.ByteArray && value.U.ByteArray != IntPtr.Zero:
                        pixels = Marshal.PtrToStructure<MpvByteArray>(value.U.ByteArray); break;
                }
            }
            var pixelFormat = format switch
            {
                "bgr0" => PixelFormats.Bgr32,
                "bgra" or "rgba" => PixelFormats.Bgra32,
                "bgr24" => PixelFormats.Bgr24,
                "rgb24" => PixelFormats.Rgb24,
                _ => PixelFormats.Default
            };
            if (width <= 0 || height <= 0 || stride <= 0 || pixels.Data == IntPtr.Zero || pixelFormat == PixelFormats.Default) return null;
            var size = checked((int)pixels.Size);
            BitmapSource bitmap;
            if (format == "rgba")
            {
                var converted = new byte[size];
                Marshal.Copy(pixels.Data, converted, 0, size);
                for (var i = 0; i + 3 < size; i += 4) (converted[i], converted[i + 2]) = (converted[i + 2], converted[i]);
                bitmap = BitmapSource.Create(width, height, 96, 96, pixelFormat, null, converted, stride);
            }
            else
                bitmap = BitmapSource.Create(width, height, 96, 96, pixelFormat, null, pixels.Data, size, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally { MpvClientNative.FreeNodeContents(ref result); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gate.Wait();
        try { Destroy(); }
        finally { _gate.Release(); }
    }

    private void Destroy()
    {
        if (_mpv != IntPtr.Zero) MpvClientNative.TerminateDestroy(_mpv);
        _mpv = IntPtr.Zero;
        if (_streamHandle.IsAllocated) _streamHandle.Free();
        lock (_sourceLock) { _source = default; _uri = null; }
    }
}
