using System.Runtime.InteropServices;
using Windows.Storage;

namespace Folio.Services;

/// <summary>
/// Where Folio keeps its files. The MSIX build redirects writes under %LOCALAPPDATA% (and so
/// %TEMP%) to a private copy that other apps can't see, so when packaged it uses the package's
/// own folders, whose paths are real.
/// </summary>
internal static class AppPackage
{
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>True when running from the MSIX package.</summary>
    public static readonly bool IsPackaged = IsRunningPackaged();

    /// <summary>
    /// Settings, recent files, thumbnails and the error log. FOLIO_DATA_FOLDER puts them elsewhere, as the
    /// benchmark does so that a page left open in the user's Folio doesn't get restored, and its runs aren't recorded.
    /// </summary>
    public static string DataFolder => Environment.GetEnvironmentVariable("FOLIO_DATA_FOLDER") is { Length: > 0 } folder
        ? Path.GetFullPath(folder)
        : IsPackaged
        ? ApplicationData.Current.LocalFolder.Path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Folio");

    /// <summary>Scratch space, including attachments handed to other apps.</summary>
    public static string TempFolder => IsPackaged
        ? ApplicationData.Current.TemporaryFolder.Path
        : Path.Combine(Path.GetTempPath(), "Folio");

    private static bool IsRunningPackaged()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
