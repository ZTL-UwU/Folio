using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Recto.Tools.Bench;

/// <summary>IStream, all of it, as the stream handed to a handler can be called on any method.</summary>
[GeneratedComInterface]
[Guid("0000000c-0000-0000-C000-000000000046")]
internal unsafe partial interface IStream
{
    // ISequentialStream
    [PreserveSig] int Read(byte* buffer, uint count, uint* read);
    [PreserveSig] int Write(byte* buffer, uint count, uint* written);
    // IStream
    [PreserveSig] int Seek(long offset, int origin, ulong* position);
    [PreserveSig] int SetSize(ulong size);
    [PreserveSig] int CopyTo(nint stream, ulong count, ulong* read, ulong* written);
    [PreserveSig] int Commit(uint flags);
    [PreserveSig] int Revert();
    [PreserveSig] int LockRegion(ulong offset, ulong count, uint type);
    [PreserveSig] int UnlockRegion(ulong offset, ulong count, uint type);
    [PreserveSig] int Stat(STATSTG* stat, uint flags);
    [PreserveSig] int Clone(nint* stream);
}

[StructLayout(LayoutKind.Sequential)]
internal struct STATSTG
{
    public nint Name;
    public uint Type;
    public ulong Size;
    public long Modified, Created, Accessed;
    public uint Mode, LocksSupported;
    public Guid Clsid;
    public uint StateBits, Reserved;
}

/// <summary>
/// A read-only stream over a file that counts the calls made on it. Handed to a handler in another process,
/// each call is a round trip to this one, as calls on the stream Explorer passes are.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class CountingFileStream(string path) : IStream, IDisposable
{
    private const int S_OK = 0, S_FALSE = 1, STG_E_ACCESSDENIED = unchecked((int)0x80030005), STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
    private const int E_NOTIMPL = unchecked((int)0x80004001), E_FAIL = unchecked((int)0x80004005);

    // Unbuffered, so every read goes to the file system as it does through Explorer's stream.
    private readonly FileStream _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 0);
    private long _calls, _bytesRead;

    public long Calls => Interlocked.Read(ref _calls);
    public long BytesRead => Interlocked.Read(ref _bytesRead);

    public int Read(byte* buffer, uint count, uint* read)
    {
        Interlocked.Increment(ref _calls);
        try
        {
            int total = 0;
            var span = new Span<byte>(buffer, (int)count);
            while (total < span.Length)
            {
                int n = _file.Read(span[total..]);
                if (n == 0) break;
                total += n;
            }
            Interlocked.Add(ref _bytesRead, total);
            if (read != null) *read = (uint)total;
            // As file streams do; PowerToys reads until it gets S_FALSE.
            return total < count ? S_FALSE : S_OK;
        }
        catch (Exception e)
        {
            return e.HResult < 0 ? e.HResult : E_FAIL;
        }
    }

    public int Seek(long offset, int origin, ulong* position)
    {
        Interlocked.Increment(ref _calls);
        if (origin is < 0 or > 2) return STG_E_INVALIDFUNCTION;
        try
        {
            long p = _file.Seek(offset, (SeekOrigin)origin);
            if (position != null) *position = (ulong)p;
            return S_OK;
        }
        catch (Exception e)
        {
            return e.HResult < 0 ? e.HResult : E_FAIL;
        }
    }

    public int Stat(STATSTG* stat, uint flags)
    {
        Interlocked.Increment(ref _calls);
        const uint STATFLAG_NONAME = 1, STGTY_STREAM = 2, STGM_READ = 0;
        *stat = new STATSTG { Type = STGTY_STREAM, Size = (ulong)_file.Length, Mode = STGM_READ };
        if ((flags & STATFLAG_NONAME) == 0) stat->Name = Marshal.StringToCoTaskMemUni(Path.GetFileName(path));
        return S_OK;
    }

    public int Write(byte* buffer, uint count, uint* written) => Count(STG_E_ACCESSDENIED);
    public int SetSize(ulong size) => Count(STG_E_ACCESSDENIED);
    public int CopyTo(nint stream, ulong count, ulong* read, ulong* written) => Count(E_NOTIMPL);
    public int Commit(uint flags) => Count(S_OK);
    public int Revert() => Count(S_OK);
    public int LockRegion(ulong offset, ulong count, uint type) => Count(STG_E_INVALIDFUNCTION);
    public int UnlockRegion(ulong offset, ulong count, uint type) => Count(STG_E_INVALIDFUNCTION);
    public int Clone(nint* stream) => Count(E_NOTIMPL);

    private int Count(int hr)
    {
        Interlocked.Increment(ref _calls);
        return hr;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Passing an interface to another process, and calling the handler's.</summary>
internal static unsafe partial class Com
{
    public const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    public const uint COINIT_MULTITHREADED = 0;
    private const uint MSHCTX_LOCAL = 0, MSHLFLAGS_NORMAL = 0;

    public static readonly Guid IID_IClassFactory = new("00000001-0000-0000-C000-000000000046");
    public static readonly Guid IID_IStream = new("0000000c-0000-0000-C000-000000000046");
    public static readonly Guid IID_IInitializeWithStream = new("b824b49d-22ac-4161-ac8a-9916e8fa3f7f");
    public static readonly Guid IID_IThumbnailProvider = new("e357fccd-a995-4576-b01f-234630154e96");
    public static readonly Guid IID_IPersistFile = new("0000010b-0000-0000-C000-000000000046");
    public static readonly Guid IID_IExtractImage = new("bb2e617c-0920-11d1-9a0b-00c04fc2d6c1");

    public const int E_PENDING = unchecked((int)0x8000000A);
    public const uint IEIFLAG_ORIGSIZE = 0x40;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Marshals <paramref name="stream"/> for one unmarshal in another process on this machine.</summary>
    public static byte[] MarshalStream(CountingFileStream stream)
    {
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(stream, CreateComInterfaceFlags.None);
        nint buffer = 0;
        try
        {
            Check(CreateStreamOnHGlobal(0, true, out buffer));
            Guid iid = IID_IStream;
            Check(CoMarshalInterface(buffer, &iid, unknown, MSHCTX_LOCAL, 0, MSHLFLAGS_NORMAL));
            Check(GetHGlobalFromStream(buffer, out nint memory));
            var data = new byte[(int)GlobalSize(memory)];
            Marshal.Copy(GlobalLock(memory), data, 0, data.Length);
            GlobalUnlock(memory);
            return data;
        }
        finally
        {
            if (buffer != 0) Marshal.Release(buffer);
            Marshal.Release(unknown);
        }
    }

    public static int UnmarshalStream(byte[] data, out nint stream)
    {
        stream = 0;
        nint buffer;
        fixed (byte* p = data) buffer = SHCreateMemStream(p, (uint)data.Length);
        if (buffer == 0) return unchecked((int)0x8007000E); // E_OUTOFMEMORY
        Guid iid = IID_IStream;
        nint result;
        int hr = CoUnmarshalInterface(buffer, &iid, &result);
        Marshal.Release(buffer);
        if (hr >= 0) stream = result;
        return hr;
    }

    // The handler's interfaces, called through their vtables.
    public static int CreateInstance(nint factory, Guid iid, out nint instance)
    {
        nint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Method(factory, 3))(factory, 0, &iid, &result);
        instance = result;
        return hr;
    }

    public static int Initialize(nint initializeWithStream, nint stream, uint mode) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Method(initializeWithStream, 3))(initializeWithStream, stream, mode);

    public static int GetThumbnail(nint provider, uint size, out nint bitmap, out int alphaType)
    {
        nint b = 0;
        int a = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int*, int>)Method(provider, 3))(provider, size, &b, &a);
        (bitmap, alphaType) = (b, a);
        return hr;
    }

    /// <summary>IPersistFile::Load, after IPersist::GetClassID and IsDirty.</summary>
    public static int Load(nint persistFile, string path, uint mode)
    {
        fixed (char* p = path) return ((delegate* unmanaged[Stdcall]<nint, char*, uint, int>)Method(persistFile, 5))(persistFile, p, mode);
    }

    /// <summary>IExtractImage::GetLocation, for a 32-bit bitmap fitted in <paramref name="size"/> x <paramref name="size"/>.</summary>
    public static int GetLocation(nint extractImage, uint size, uint flags)
    {
        const int MAX_PATH = 260;
        char* path = stackalloc char[MAX_PATH];
        uint priority = 0;
        int* box = stackalloc int[] { (int)size, (int)size }; // SIZE
        return ((delegate* unmanaged[Stdcall]<nint, char*, uint, uint*, int*, uint, uint*, int>)Method(extractImage, 3))(
            extractImage, path, MAX_PATH, &priority, box, 32, &flags);
    }

    public static int Extract(nint extractImage, out nint bitmap)
    {
        nint b = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(extractImage, 4))(extractImage, &b);
        bitmap = b;
        return hr;
    }

    private static nint Method(nint instance, int slot) => (*(nint**)instance)[slot];

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [DllImport("ole32.dll")] public static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] public static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(nint memory, bool deleteOnRelease, out nint stream);
    [DllImport("ole32.dll")] private static extern int GetHGlobalFromStream(nint stream, out nint memory);
    [DllImport("ole32.dll")] private static extern int CoMarshalInterface(nint stream, Guid* iid, nint unknown, uint destination, nint destinationContext, uint flags);
    [DllImport("ole32.dll")] private static extern int CoUnmarshalInterface(nint stream, Guid* iid, nint* result);
    [DllImport("shlwapi.dll")] private static extern nint SHCreateMemStream(byte* data, uint size);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
}

/// <summary>Reading a thumbnail handler's bitmap.</summary>
internal static unsafe class Gdi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    public static (int Width, int Height) Size(nint bitmap)
    {
        BITMAP info;
        return GetObjectW(bitmap, sizeof(BITMAP), &info) == 0 ? (0, 0) : (info.Width, Math.Abs(info.Height));
    }

    /// <summary>The bitmap's pixels as top-down BGRA rows.</summary>
    public static byte[]? Pixels(nint bitmap, int width, int height)
    {
        var header = new BITMAPINFOHEADER { Size = (uint)sizeof(BITMAPINFOHEADER), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var pixels = new byte[width * height * 4];
        nint dc = CreateCompatibleDC(0);
        try
        {
            fixed (byte* p = pixels) return GetDIBits(dc, bitmap, 0, (uint)height, p, &header, 0) == height ? pixels : null;
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, void* buffer);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BITMAPINFOHEADER* info, uint usage);
}

/// <summary>
/// A job object around a host, which adds up the CPU time of the host and every process it starts, and
/// ends them all if the benchmark does.
/// </summary>
internal sealed unsafe class Job : IDisposable
{
    private const int JobObjectBasicAccountingInformation = 1, JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BASIC_ACCOUNTING
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EXTENDED_LIMIT
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private readonly nint _handle;

    public Job()
    {
        _handle = CreateJobObjectW(0, null);
        if (_handle == 0) throw new ToolException($"Couldn't create a job object (error {Marshal.GetLastWin32Error()}).");
        var limit = new EXTENDED_LIMIT { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
        SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, &limit, (uint)sizeof(EXTENDED_LIMIT));
    }

    public void Add(System.Diagnostics.Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new ToolException($"Couldn't add the host to a job object (error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>CPU time so far, user and kernel, of every process that's been in the job, and how many there were.</summary>
    public (TimeSpan Cpu, int Processes) Totals()
    {
        BASIC_ACCOUNTING info;
        if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, &info, (uint)sizeof(BASIC_ACCOUNTING), null)) return default;
        return (TimeSpan.FromTicks(info.TotalUserTime + info.TotalKernelTime), (int)info.TotalProcesses);
    }

    public void Terminate() => TerminateJobObject(_handle, 1);

    public void Dispose() => CloseHandle(_handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(nint job, int infoClass, void* info, uint size);
    [DllImport("kernel32.dll")] private static extern bool QueryInformationJobObject(nint job, int infoClass, void* info, uint size, uint* returned);
    [DllImport("kernel32.dll")] private static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
