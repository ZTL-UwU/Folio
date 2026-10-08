namespace Folio.Services;

/// <summary>Temporary copies of embedded files, written under %TEMP%\Folio (or the package's temp folder) so they can be opened.</summary>
internal static class AttachmentFiles
{
    public static readonly string Folder = AppPackage.TempFolder;

    /// <summary>Creates a fresh folder for one extracted attachment.</summary>
    public static string CreateFolder()
    {
        string dir = Path.Combine(Folder, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static bool Contains(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Folder) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Best effort: files another app still has open are left behind for <see cref="Purge"/>.</summary>
    public static void Delete(string dir)
    {
        try
        {
            if (Contains(dir) && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes folders left behind by earlier sessions.</summary>
    public static void Purge(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            var cutoff = DateTime.UtcNow - olderThan;
            foreach (var dir in Directory.EnumerateDirectories(Folder))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff) Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
