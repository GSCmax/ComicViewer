using System.Runtime.InteropServices;

namespace ComicViewer;

internal enum MpvFormat
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

internal enum MpvEventId
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

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5
}

internal enum MpvRenderParamType
{
    Invalid = 0,
    ApiType = 1,
    OpenGlInitParams = 2,
    OpenGlFbo = 3,
    FlipY = 4
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserData;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct MpvOpenGlInitParams
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
internal readonly struct MpvOpenGlFbo
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
internal readonly struct MpvRenderParam
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
internal delegate IntPtr MpvOpenGlGetProcAddressDelegate(IntPtr context, IntPtr name);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MpvRenderUpdateDelegate(IntPtr callbackContext);

internal static class MpvRenderNative
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
