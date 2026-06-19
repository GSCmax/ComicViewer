using System.Runtime.InteropServices;

namespace ComicViewer;

internal interface IMpvMemoryStreamSource
{
    bool TryGetStreamData(string? uri, out ArraySegment<byte> data);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvStreamCbInfo
{
    public IntPtr Cookie;
    public IntPtr Read;
    public IntPtr Seek;
    public IntPtr Size;
    public IntPtr Close;
    public IntPtr Cancel;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int OpenStreamDelegate(IntPtr userData, IntPtr uri, IntPtr info);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long ReadStreamDelegate(IntPtr cookie, IntPtr buffer, ulong nbytes);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long SeekStreamDelegate(IntPtr cookie, long offset);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate long SizeStreamDelegate(IntPtr cookie);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void CloseStreamDelegate(IntPtr cookie);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void CancelStreamDelegate(IntPtr cookie);

internal static class MpvMemoryStream
{
    public static readonly OpenStreamDelegate OpenStreamCallback = OpenStream;

    private static readonly ReadStreamDelegate ReadStreamCallback = ReadStream;
    private static readonly SeekStreamDelegate SeekStreamCallback = SeekStream;
    private static readonly SizeStreamDelegate SizeStreamCallback = SizeStream;
    private static readonly CloseStreamDelegate CloseStreamCallback = CloseStream;
    private static readonly CancelStreamDelegate CancelStreamCallback = CancelStream;

    private static readonly IntPtr ReadStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(ReadStreamCallback);
    private static readonly IntPtr SeekStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(SeekStreamCallback);
    private static readonly IntPtr SizeStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(SizeStreamCallback);
    private static readonly IntPtr CloseStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(CloseStreamCallback);
    private static readonly IntPtr CancelStreamCallbackPtr = Marshal.GetFunctionPointerForDelegate(CancelStreamCallback);

    private static int OpenStream(IntPtr userData, IntPtr uri, IntPtr info)
    {
        try
        {
            var streamSource = GCHandle.FromIntPtr(userData).Target;
            if (streamSource is not IMpvMemoryStreamSource source)
            {
                return -1;
            }

            var streamUri = Marshal.PtrToStringAnsi(uri);
            if (!source.TryGetStreamData(streamUri, out var data))
            {
                return -1;
            }

            var cookie = new Cookie(data);
            var cookieHandle = GCHandle.Alloc(cookie);
            var streamInfo = Marshal.PtrToStructure<MpvStreamCbInfo>(info);
            streamInfo.Cookie = GCHandle.ToIntPtr(cookieHandle);
            streamInfo.Read = ReadStreamCallbackPtr;
            streamInfo.Seek = SeekStreamCallbackPtr;
            streamInfo.Size = SizeStreamCallbackPtr;
            streamInfo.Close = CloseStreamCallbackPtr;
            streamInfo.Cancel = CancelStreamCallbackPtr;
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
        return GCHandle.FromIntPtr(cookieHandle).Target is Cookie cookie
            ? cookie.Read(buffer, requestedBytes)
            : -1;
    }

    private static long SeekStream(IntPtr cookieHandle, long offset)
    {
        return GCHandle.FromIntPtr(cookieHandle).Target is Cookie cookie
            ? cookie.Seek(offset)
            : -1;
    }

    private static long SizeStream(IntPtr cookieHandle)
    {
        return GCHandle.FromIntPtr(cookieHandle).Target is Cookie cookie
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
        if (GCHandle.FromIntPtr(cookieHandle).Target is Cookie cookie)
        {
            cookie.Cancel();
        }
    }

    private sealed class Cookie : IDisposable
    {
        private readonly ArraySegment<byte> _data;
        private readonly object _lock = new();
        private long _position;
        private bool _isCanceled;

        public Cookie(ArraySegment<byte> data)
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
}
