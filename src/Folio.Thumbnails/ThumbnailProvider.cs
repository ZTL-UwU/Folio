using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Folio.Thumbnails;

/// <summary>The COM class File Explorer creates for each PDF it wants a thumbnail of.</summary>
[GeneratedComClass]
internal sealed partial class ThumbnailProvider : IInitializeWithStream, IThumbnailProvider
{
    /// <summary>Must match the Clsid in src/Folio/Package.appxmanifest.</summary>
    public static readonly Guid Clsid = new("54e57302-4f85-47eb-8be3-76ee7e671857");

    private IStream? _stream;

    public int Initialize(IStream stream, uint mode)
    {
        if (_stream is not null) return HResult.AlreadyInitialized;
        _stream = stream;
        return HResult.S_OK;
    }

    public int GetThumbnail(uint cx, out nint bitmap, out int alphaType)
    {
        bitmap = 0;
        alphaType = Win32.WTSAT_ARGB;
        if (_stream is null) return HResult.E_UNEXPECTED;
        try
        {
            bitmap = PdfThumbnail.Render(_stream, (int)Math.Min(cx, int.MaxValue));
        }
        catch
        {
            // Explorer shows the file icon instead.
        }
        finally
        {
            // Done with the file. Left to the GC, the wrapper could keep it open in the long-lived
            // surrogate, so it couldn't be deleted or renamed until a collection happened to run.
            if ((object)_stream is ComObject com) com.FinalRelease();
            _stream = null;
        }
        return bitmap != 0 ? HResult.S_OK : HResult.E_FAIL;
    }
}

[GeneratedComClass]
internal sealed partial class ClassFactory : IClassFactory
{
    public int CreateInstance(nint outer, in Guid iid, out nint obj)
    {
        obj = 0;
        if (outer != 0) return HResult.CLASS_E_NOAGGREGATION;
        return Exports.QueryInterface(new ThumbnailProvider(), iid, out obj);
    }

    public int LockServer(bool fLock) => HResult.S_OK;
}

/// <summary>The exports COM calls to get the class factory.</summary>
internal static unsafe class Exports
{
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* clsid, Guid* iid, nint* obj)
    {
        if (obj == null) return HResult.E_POINTER;
        *obj = 0;
        if (*clsid != ThumbnailProvider.Clsid) return HResult.CLASS_E_CLASSNOTAVAILABLE;
        try
        {
            return QueryInterface(new ClassFactory(), *iid, out *obj);
        }
        catch (Exception e)
        {
            // An exception can't cross back into COM; it would end the process.
            return e.HResult;
        }
    }

    /// <summary>Never: the .NET runtime in this DLL can't be shut down and unloaded.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => HResult.S_FALSE;

    public static int QueryInterface(object instance, in Guid iid, out nint obj)
    {
        nint unknown = ComWrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        int hr = Marshal.QueryInterface(unknown, iid, out obj);
        Marshal.Release(unknown);
        return hr;
    }
}
