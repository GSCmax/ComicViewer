using System.Runtime.InteropServices;

namespace ComicViewer;

internal static class MpvClientNative
{
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_wakeup")]
    public static extern void Wakeup(IntPtr handle);

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_unobserve_property")]
    public static extern int UnobserveProperty(IntPtr handle, ulong replyUserData);

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

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_command_ret")]
    public static extern int CommandRet(IntPtr handle,
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] args, out MpvNode result);

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
}
