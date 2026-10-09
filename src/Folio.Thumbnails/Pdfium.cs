using System.Runtime.InteropServices;

namespace Folio.Thumbnails;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FPDF_FILEACCESS
{
    /// <summary>A C <c>unsigned long</c>, which is 32 bits on Windows.</summary>
    public uint FileLen;
    public delegate* unmanaged<void*, uint, byte*, uint, int> GetBlock;
    public void* Param;
}

/// <summary>
/// The PDFium entry points the thumbnailer uses, bound to the pdfium.dll next to this DLL. Binding by
/// path rather than by name means another pdfium.dll already loaded in the host process can't be picked
/// up instead. PDFium isn't thread-safe; callers hold <see cref="Gate"/>.
/// </summary>
internal static unsafe class Pdfium
{
    public const int FPDF_ANNOT = 0x01;
    public const int FPDFBitmap_BGRA = 4;

    public static readonly object Gate = new();

    public static delegate* unmanaged<FPDF_FILEACCESS*, byte*, nint> FPDF_LoadCustomDocument;
    public static delegate* unmanaged<nint, void> FPDF_CloseDocument;
    public static delegate* unmanaged<nint, int, nint> FPDF_LoadPage;
    public static delegate* unmanaged<nint, void> FPDF_ClosePage;
    public static delegate* unmanaged<nint, float> FPDF_GetPageWidthF;
    public static delegate* unmanaged<nint, float> FPDF_GetPageHeightF;
    public static delegate* unmanaged<int, int, int, void*, int, nint> FPDFBitmap_CreateEx;
    public static delegate* unmanaged<nint, int, int, int, int, uint, int> FPDFBitmap_FillRect;
    public static delegate* unmanaged<nint, void> FPDFBitmap_Destroy;
    public static delegate* unmanaged<nint, nint, int, int, int, int, int, int, void> FPDF_RenderPageBitmap;

    private static bool _loaded;

    /// <summary>Loads and initializes PDFium on first use. Call while holding <see cref="Gate"/>.</summary>
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        nint lib = NativeLibrary.Load(Path.Combine(ModuleDirectory(), "pdfium.dll"));
        FPDF_LoadCustomDocument = (delegate* unmanaged<FPDF_FILEACCESS*, byte*, nint>)NativeLibrary.GetExport(lib, nameof(FPDF_LoadCustomDocument));
        FPDF_CloseDocument = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(lib, nameof(FPDF_CloseDocument));
        FPDF_LoadPage = (delegate* unmanaged<nint, int, nint>)NativeLibrary.GetExport(lib, nameof(FPDF_LoadPage));
        FPDF_ClosePage = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(lib, nameof(FPDF_ClosePage));
        FPDF_GetPageWidthF = (delegate* unmanaged<nint, float>)NativeLibrary.GetExport(lib, nameof(FPDF_GetPageWidthF));
        FPDF_GetPageHeightF = (delegate* unmanaged<nint, float>)NativeLibrary.GetExport(lib, nameof(FPDF_GetPageHeightF));
        FPDFBitmap_CreateEx = (delegate* unmanaged<int, int, int, void*, int, nint>)NativeLibrary.GetExport(lib, nameof(FPDFBitmap_CreateEx));
        FPDFBitmap_FillRect = (delegate* unmanaged<nint, int, int, int, int, uint, int>)NativeLibrary.GetExport(lib, nameof(FPDFBitmap_FillRect));
        FPDFBitmap_Destroy = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(lib, nameof(FPDFBitmap_Destroy));
        FPDF_RenderPageBitmap = (delegate* unmanaged<nint, nint, int, int, int, int, int, int, void>)NativeLibrary.GetExport(lib, nameof(FPDF_RenderPageBitmap));
        ((delegate* unmanaged<void>)NativeLibrary.GetExport(lib, "FPDF_InitLibrary"))();
        _loaded = true;
    }

    /// <summary>The folder of this DLL, which in the package is also Folio's.</summary>
    private static string ModuleDirectory()
    {
        nint module;
        const uint Flags = Win32.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | Win32.GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT;
        if (Win32.GetModuleHandleExW(Flags, (nint)(delegate*<void>)&EnsureLoaded, &module))
        {
            const int Capacity = 1024;
            char* path = stackalloc char[Capacity];
            uint length = Win32.GetModuleFileNameW(module, path, Capacity);
            if (length > 0 && length < Capacity) return Path.GetDirectoryName(new string(path, 0, (int)length))!;
        }
        // Not inside a native module: the tests run this code JIT-compiled, with pdfium.dll next to them.
        return AppContext.BaseDirectory;
    }
}
