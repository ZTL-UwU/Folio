using System.Diagnostics;

namespace Folio.Tools.Bench;

/// <summary>A PDF viewer the benchmark starts.</summary>
internal sealed class Viewer : IDisposable
{
    public static readonly string[] All = ["Folio", "Edge", "Acrobat", "Sumatra"];

    public required string Name { get; init; }

    public required string Exe { get; init; }

    /// <summary>The command line that opens a file.</summary>
    public required Func<string, string> Arguments { get; init; }

    /// <summary>
    /// Names of the viewer's processes. Ones that start during a run count towards its memory even if the viewer
    /// didn't start them (brokers), and none may be running beforehand, as the viewer would hand them the file.
    /// </summary>
    public string[] Images { get; init; } = [];

    /// <summary>Only a running copy of this same executable takes the file; Folio keeps one instance per copy.</summary>
    public bool OneInstancePerCopy { get; init; }

    /// <summary>A profile or settings folder made for the benchmark, deleted afterwards.</summary>
    public string? Scratch { get; init; }

    /// <summary>The viewer's processes that are running now.</summary>
    public List<Process> Running() => Images.SelectMany(Process.GetProcessesByName)
        .Where(p => !OneInstancePerCopy || string.Equals(PathOf(p), Exe, StringComparison.OrdinalIgnoreCase))
        .ToList();

    public void Dispose()
    {
        if (Scratch is not null) Files.DeleteDirectory(Scratch);
    }

    private static string? PathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The viewer called <paramref name="name"/> (one of <see cref="All"/>), or null if it isn't installed.</summary>
    public static Viewer? Find(string name, string folio)
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"folio-bench-{name.ToLowerInvariant()}-{Environment.ProcessId}");
        switch (name)
        {
            case "Folio":
                return new Viewer { Name = name, Exe = folio, Arguments = Quote, Images = ["Folio"], OneInstancePerCopy = true };

            case "Edge":
                if (Apps.Edge() is not { } edge) return null;
                // A profile of its own keeps it apart from an Edge that's already running, so there's nothing to close.
                string flags = $"--user-data-dir={Quote(scratch)} --no-first-run --no-default-browser-check --hide-crash-restore-bubble --new-window";
                return new Viewer { Name = name, Exe = edge, Arguments = f => $"{flags} {Quote(f)}", Scratch = scratch };

            case "Acrobat":
                if ((Apps.FromAppPaths("Acrobat.exe") ?? Apps.FromAppPaths("AcroRd32.exe")) is not { } acrobat) return null;
                return new Viewer { Name = name, Exe = acrobat, Arguments = Quote, Images = ["Acrobat", "AcroRd32", "AcroCEF", "RdrCEF"] };

            case "Sumatra":
                if ((Apps.FromAppPaths("SumatraPDF.exe") ?? Apps.FirstExisting(
                    @"%LOCALAPPDATA%\SumatraPDF\SumatraPDF.exe",
                    @"%ProgramFiles%\SumatraPDF\SumatraPDF.exe",
                    @"%ProgramFiles(x86)%\SumatraPDF\SumatraPDF.exe")) is not { } sumatra) return null;
                // Settings of its own, so a run neither restores a session nor changes the installed copy's settings.
                // A fresh settings folder would leave the update check and session restore on.
                Directory.CreateDirectory(scratch);
                File.WriteAllLines(Path.Combine(scratch, "SumatraPDF-settings.txt"),
                    ["CheckForUpdates = false", "RestoreSession = false", "ReuseInstance = false", "RememberOpenedFiles = false", "ShowStartPage = false"]);
                string options = $"-appdata {Quote(scratch)} -new-window";
                return new Viewer { Name = name, Exe = sumatra, Arguments = f => $"{options} {Quote(f)}", Images = ["SumatraPDF"], Scratch = scratch };

            default:
                throw new ArgumentException(name);
        }
    }

    private static string Quote(string path) => $"\"{path}\"";
}
