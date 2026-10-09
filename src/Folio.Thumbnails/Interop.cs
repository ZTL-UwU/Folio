using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Folio.Thumbnails;

internal static class HResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    /// <summary>HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED).</summary>
    public const int AlreadyInitialized = unchecked((int)0x800704DF);
}

[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, in Guid iid, out nint obj);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

[GeneratedComInterface]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
internal partial interface IInitializeWithStream
{
    [PreserveSig] int Initialize(IStream stream, uint mode);
}

[GeneratedComInterface]
[Guid("e357fccd-a995-4576-b01f-234630154e96")]
internal partial interface IThumbnailProvider
{
    [PreserveSig] int GetThumbnail(uint cx, out nint bitmap, out int alphaType);
}

/// <summary>
/// The start of IStream's vtable, up to <see cref="Seek"/>; the methods after it are never called.
/// </summary>
[GeneratedComInterface]
[Guid("0000000c-0000-0000-C000-000000000046")]
internal unsafe partial interface IStream
{
    // ISequentialStream
    [PreserveSig] int Read(byte* buffer, uint count, uint* read);
    [PreserveSig] int Write(byte* buffer, uint count, uint* written);
    // IStream
    [PreserveSig] int Seek(long offset, int origin, ulong* position);
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint Size;
    public int Width, Height;
    public ushort Planes, BitCount;
    public uint Compression, SizeImage;
    public int XPelsPerMeter, YPelsPerMeter;
    public uint ClrUsed, ClrImportant;
}

internal static unsafe partial class Win32
{
    /// <summary>WTS_ALPHATYPE: the bitmap has transparency.</summary>
    public const int WTSAT_ARGB = 2;

    public const int STREAM_SEEK_SET = 0;
    public const int STREAM_SEEK_END = 2;

    public const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4;
    public const uint GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint obj);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleHandleExW(uint flags, nint address, nint* module);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetModuleFileNameW(nint module, char* filename, uint size);
}
