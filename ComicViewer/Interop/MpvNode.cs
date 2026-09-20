using System.Runtime.InteropServices;

namespace ComicViewer;

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
