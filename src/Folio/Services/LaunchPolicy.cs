using System.Globalization;
using System.Text;

namespace Folio.Services;

public enum AttachmentRisk
{
    /// <summary>A PDF: opened in Folio itself.</summary>
    Pdf,
    /// <summary>Opened with its default app after the user confirms.</summary>
    Ask,
    /// <summary>Programs, scripts, shortcuts and installers: never launched, only saved.</summary>
    Blocked,
}

/// <summary>Decides what content from a document may hand to the shell. Kept free of UI so it can be tested.</summary>
public static class LaunchPolicy
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    // Types that run code or change the system when opened: Windows' own high-risk list plus
    // the Outlook "Level 1" attachment list.
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ade", ".adp", ".app", ".appcontent-ms", ".appinstaller", ".application", ".appref-ms", ".appx", ".appxbundle", ".asp", ".aspx", ".asx",
        ".bas", ".bat", ".bgi", ".cab", ".cer", ".chm", ".cmd", ".cnt", ".com", ".cpl", ".crt", ".csh", ".der",
        ".desktopthemepackfile", ".diagcab", ".diagcfg", ".diagpkg", ".dll", ".exe", ".fxp", ".gadget", ".grp", ".hlp", ".hpj",
        ".hta", ".htc", ".img", ".inf", ".ins", ".iqy", ".iso", ".isp", ".its", ".jar", ".jnlp", ".js", ".jse", ".ksh", ".library-ms",
        ".lnk", ".mad", ".maf", ".mag", ".mam", ".maq", ".mar", ".mas", ".mat", ".mau", ".mav", ".maw", ".mcf", ".mda",
        ".mdb", ".mde", ".mdt", ".mdw", ".mdz", ".msc", ".msh", ".msh1", ".msh1xml", ".msh2", ".msh2xml", ".mshxml", ".msi",
        ".msix", ".msixbundle", ".msp", ".mst", ".msu", ".ocx", ".ops", ".osd", ".pcd", ".pif", ".pl", ".plg", ".prf", ".prg",
        ".printerexport", ".ps1", ".ps1xml", ".ps2", ".ps2xml", ".psc1", ".psc2", ".psd1", ".psm1", ".pssc", ".pst", ".py",
        ".pyc", ".pyo", ".pyw", ".pyz", ".pyzw", ".rdp", ".reg", ".scf", ".scr", ".sct", ".search-ms", ".searchconnector-ms",
        ".settingcontent-ms", ".shb", ".shs", ".slk", ".sys", ".theme", ".themepack", ".tmp", ".udl", ".url", ".vb", ".vbe", ".vbp",
        ".vbs", ".vhd", ".vhdx", ".vsix", ".vsmacros", ".vsto", ".vsw", ".wcx", ".webpnp", ".website", ".ws", ".wsb", ".wsc",
        ".wsf", ".wsh", ".xbap", ".xll", ".xnk",
    };

    // Types Explorer reads as soon as it shows the folder they're in (icons, search connectors,
    // themes). Those can reach out to a remote host, so they're never saved under their own name.
    private static readonly HashSet<string> ShellReadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".desktopthemepackfile", ".library-ms", ".lnk", ".scf", ".search-ms", ".searchconnector-ms", ".theme", ".themepack",
        ".url", ".website",
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$",
    };

    /// <summary>
    /// Parses a link target from a document. Returns null unless it's an http, https or mailto
    /// URI. Bare host names ("example.com/page") are treated as https. Web links with a user name
    /// ("https://trusted.example@evil.example/") are refused: their only use is to disguise the host.
    /// </summary>
    public static Uri? ParseExternalLink(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var text = uri.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
        {
            // Only something that looks like a host name gets the https:// treatment, not paths.
            if (text.Contains(':') || text.StartsWith('\\') || text.StartsWith('/')) return null;
            if (!Uri.TryCreate("https://" + text, UriKind.Absolute, out parsed)) return null;
        }
        if (!AllowedSchemes.Contains(parsed.Scheme)) return null;
        if (parsed.Scheme != Uri.UriSchemeMailto && parsed.UserInfo.Length > 0) return null;
        return parsed;
    }

    /// <summary>
    /// How a link from <see cref="ParseExternalLink"/> is shown before it's opened. The host comes
    /// right after the scheme and in its ASCII (punycode) form, so a look-alike name can't pass
    /// for the real one and a long address can't push it out of view.
    /// </summary>
    public static string DescribeLink(Uri link)
    {
        if (link.Scheme == Uri.UriSchemeMailto) return link.AbsoluteUri;
        string host = link.HostNameType == UriHostNameType.IPv6 ? link.Host : link.IdnHost;
        string port = link.IsDefaultPort ? "" : ":" + link.Port.ToString(CultureInfo.InvariantCulture);
        string rest = link.PathAndQuery == "/" && link.Fragment.Length == 0 ? "" : link.PathAndQuery + link.Fragment;
        return $"{link.Scheme}://{host}{port}{rest}";
    }

    /// <summary>
    /// Removes the bidirectional embedding, override and isolate controls from text that comes
    /// from a document. They're invisible, and a right-to-left override makes a string such as
    /// "notes‮txt.html" read as a different name and reverses the text around it.
    /// </summary>
    public static string StripBidiControls(string text)
    {
        var span = text.AsSpan();
        if (!span.ContainsAnyInRange('‪', '‮') && !span.ContainsAnyInRange('⁦', '⁩')) return text;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is not ((>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Turns an attachment name from a document into a plain file name. Windows drops trailing
    /// dots and spaces when it creates a file, so they're removed here first; otherwise
    /// "setup.exe." would pass the extension check and still land on disk as "setup.exe".
    /// Invisible format characters (bidi overrides, zero-width joiners) are dropped as well, so the
    /// name the user sees is the name whose extension decides what happens.
    /// </summary>
    public static string SafeFileName(string? name)
    {
        name = string.Concat((name ?? "").Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format));
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return "attachment";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name).TrimEnd(' '))) name = "_" + name;
        if (name.Length > 120)
        {
            string ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = "";
            name = name[..(120 - ext.Length)].TrimEnd('.', ' ') + ext;
        }
        return name;
    }

    /// <summary>Classifies an attachment by the name it will be written under (see <see cref="SafeFileName"/>).</summary>
    public static AttachmentRisk Classify(string safeFileName)
    {
        string ext = Path.GetExtension(safeFileName);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return AttachmentRisk.Pdf;
        return BlockedExtensions.Contains(ext) ? AttachmentRisk.Blocked : AttachmentRisk.Ask;
    }

    /// <summary>
    /// The name an attachment is saved under. Shortcuts, desktop.ini and other files Explorer reads
    /// just by showing their folder get ".txt" added, so they stay inert until the user renames them.
    /// </summary>
    public static string SaveFileName(string safeFileName) =>
        ShellReadExtensions.Contains(Path.GetExtension(safeFileName)) || safeFileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            ? safeFileName + ".txt"
            : safeFileName;

    /// <summary>
    /// Tags a file extracted from a document as coming from the internet (Mark of the Web), so
    /// SmartScreen, Office Protected View and similar checks treat it as untrusted. Returns false
    /// if the mark couldn't be written: not every file system supports alternate data streams
    /// (FAT, some network shares).
    /// </summary>
    public static bool MarkAsUntrusted(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
