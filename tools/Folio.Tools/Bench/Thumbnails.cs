using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Folio.Tools.Bench;

/// <summary>
/// A File Explorer thumbnail handler for PDFs. Most are IThumbnailProviders, given a stream; one that
/// <paramref name="ExtractImage"/> is the older IExtractImage, given the file's path, which Explorer still uses
/// when a file type has no IThumbnailProvider.
/// </summary>
internal sealed record Handler(string Name, string Dll, Guid Clsid, string? Version, bool ExtractImage = false)
{
    public static readonly string[] All = ["Folio", "Acrobat", "Sumatra", "PowerToys"];

    /// <summary>ThumbnailProvider.Clsid in src/Folio.Thumbnails.</summary>
    private static readonly Guid FolioClsid = new("54e57302-4f85-47eb-8be3-76ee7e671857");

    /// <summary>CLSID_PdfThumbnailProvider in PowerToys' src/modules/previewpane/FileExplorerDllExporter/dllmain.cpp.</summary>
    private static readonly Guid PowerToysClsid = new("d8bb9942-93bd-412d-87e4-33fab214dc1a");

    /// <summary>The PDF class of SumatraPDF's PdfPreview.dll, which has one per format it opens.</summary>
    private static readonly Guid SumatraClsid = new("3d3b1846-cc43-42ae-bff9-d914083c2ba3");

    /// <summary>"PDF Shell Extension", Acrobat's IExtractImage for Acrobat.Document.DC.</summary>
    private static readonly Guid AcrobatClsid = new("f9db5320-233e-11d1-9f84-707f02c10627");

    public const string FolioDll = "Folio.Thumbnails.dll";

    /// <summary>
    /// The DLL that serves PowerToys' PDF thumbnails, by release: one for all of them from 0.102, with the same
    /// CLSIDs, and one per handler before.
    /// </summary>
    private static readonly string[] PowerToysDlls = ["PowerToys.FileExplorerDllExporter.dll", "PowerToys.PdfThumbnailProviderCpp.dll"];

    /// <summary>The handler called <paramref name="name"/> (one of <see cref="All"/>), or null if it isn't there.</summary>
    public static Handler? Find(string name, string? folioDll) => name switch
    {
        "Folio" => folioDll is not null && File.Exists(folioDll) ? new("Folio", folioDll, FolioClsid, null) : null,
        "Acrobat" => FindAcrobat(),
        "Sumatra" => FindSumatra(),
        "PowerToys" => FindPowerToys(),
        _ => throw new ArgumentException(name),
    };

    /// <summary>
    /// Acrobat's, which is only installed when its preference to show PDF thumbnails in File Explorer is turned on.
    /// It starts Acrobat to make the thumbnail.
    /// </summary>
    private static Handler? FindAcrobat()
    {
        if (Apps.FirstExisting(@"%CommonProgramFiles%\Adobe\Acrobat\ActiveX\PDFShell64.dll") is not { } dll) return null;
        var info = FileVersionInfo.GetVersionInfo(dll);
        return new("Acrobat", dll, AcrobatClsid, $"{info.FileMajorPart}.{info.FileMinorPart}", ExtractImage: true);
    }

    /// <summary>
    /// SumatraPDF's, next to the viewer. Its installer only registers it when asked to, and doesn't install it with
    /// the portable copy, but it's loaded from its DLL.
    /// </summary>
    private static Handler? FindSumatra()
    {
        if (Apps.Sumatra() is not { } sumatra) return null;
        string dll = Path.Combine(Path.GetDirectoryName(sumatra)!, "PdfPreview.dll");
        if (!File.Exists(dll)) return null;
        var info = FileVersionInfo.GetVersionInfo(dll);
        return new("Sumatra", dll, SumatraClsid, $"{info.FileMajorPart}.{info.FileMinorPart}");
    }

    /// <summary>PowerToys', from a per-user or machine-wide install. It doesn't have to be turned on, as it's loaded from its DLL.</summary>
    private static Handler? FindPowerToys()
    {
        var paths = new[] { @"%LOCALAPPDATA%\PowerToys", @"%ProgramFiles%\PowerToys" }.SelectMany(folder => PowerToysDlls.Select(dll => Path.Combine(folder, dll)));
        if (Apps.FirstExisting([.. paths]) is not { } found) return null;
        var info = FileVersionInfo.GetVersionInfo(found);
        return new("PowerToys", found, PowerToysClsid, $"{info.FileMajorPart}.{info.FileMinorPart}");
    }

    /// <summary>
    /// Publishes this checkout's handler with the pdfium.dll it loads from its folder, as in the package. Both are
    /// built for this process, which the host shares, rather than for Windows as Folio is (an x64 dotnet on ARM64
    /// Windows), so they go in a folder of their own. Returns the handler's path.
    /// </summary>
    public static string PublishFolio()
    {
        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        string output = Repo.Path("out", "bench", "thumbnails", rid);
        Console.WriteLine($"Publishing Folio's thumbnail handler to {output}...");
        if (!DotNet.Publish(Repo.Path("src", "Folio.Thumbnails", "Folio.Thumbnails.csproj"), output, ["-r", rid]))
            throw new ToolException("Publishing the thumbnail handler failed; see above.");
        // This tool references the same PDFium package as Folio, for every architecture.
        File.Copy(Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "pdfium.dll"), Path.Combine(output, "pdfium.dll"), overwrite: true);
        return Path.Combine(output, FolioDll);
    }
}

/// <summary>
/// Medians over the successful thumbnails of one handler on one document. Those from a host that had just started,
/// which also loaded the handler, are left out; they're in <see cref="BenchResults.ThumbnailColdMs"/>.
/// </summary>
internal sealed record ThumbnailSummary(string App, string Document, int Succeeded, int Runs,
    double? Ms, double? MinMs, double? MaxMs, double? CpuMs, double? Processes, double? StreamCalls, double? ReadMB, int? Width, int? Height)
{
    public static ThumbnailSummary Of(string app, string document, IReadOnlyList<ThumbnailRun> runs)
    {
        // A cold thumbnail that failed still counts as a failed run.
        var warm = runs.Where(r => !r.Cold || r.Error is not null).ToList();
        var ok = warm.Where(r => r.Error is null).ToList();
        return new ThumbnailSummary(app, document, ok.Count, warm.Count,
            Summary.Median(ok.Select(r => r.Ms)), ok.Min(r => r.Ms), ok.Max(r => r.Ms), Summary.Median(ok.Select(r => (double?)r.CpuMs)),
            Summary.Median(ok.Select(r => (double?)r.Processes)), Summary.Median(ok.Select(r => (double?)r.StreamCalls)),
            Summary.Median(ok.Select(r => (double?)(r.BytesRead / 1e6))), ok.FirstOrDefault()?.Width, ok.FirstOrDefault()?.Height);
    }
}

/// <summary>
/// Times File Explorer thumbnail handlers making a thumbnail of page 1 of each benchmark document.
/// </summary>
/// <remarks>
/// Each handler is loaded into a host process of its own (<see cref="ThumbnailHost"/>), as Explorer loads them
/// into a COM surrogate, and reads the PDF through a stream served from this process, so each call on it is a
/// round trip between processes, as it is from Explorer. A thumbnail is timed from creating the handler to
/// getting its bitmap, which is what Explorer waits for; also counted are the CPU time of the host and any
/// processes it started, and the calls on the stream. Handlers take turns, and the warm-up rounds aren't
/// recorded, so files are in the OS cache and the handlers loaded. The first thumbnail of each host, which loads
/// the handler, is left out of the medians and reported on its own; a host is started again after a timeout.
/// </remarks>
internal static class Thumbnails
{
    public static (Dictionary<(string App, string Document), List<ThumbnailRun>> Runs, Dictionary<string, List<double>> ColdMs) Measure(
        List<Handler> handlers, List<BenchDocument> documents, BenchSettings settings, int size, string outDir)
    {
        var runs = handlers.SelectMany(h => documents.Select(d => (h.Name, d.Id))).ToDictionary(k => k, _ => new List<ThumbnailRun>());
        string csv = Path.Combine(outDir, "thumbnails.csv");
        File.WriteAllText(csv, "App,Document,Run,Ms,CpuMs,Processes,StreamCalls,ReadMB,Width,Height,Cold,Error\n");
        var cold = handlers.ToDictionary(h => h.Name, _ => new List<double>());
        int width = handlers.Max(h => h.Name.Length);
        var timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);

        // The streams handed to the hosts are called from other processes, which an MTA serves without a message loop.
        int init = Com.CoInitializeEx(0, Com.COINIT_MULTITHREADED);
        if (init == Com.RPC_E_CHANGED_MODE) throw new ToolException("The benchmark thread is single-threaded; it can't serve the thumbnail handlers' streams.");
        var hosts = handlers.Select(h => new ThumbnailHost(h)).ToList();
        try
        {
            for (int run = 1 - settings.Warmup; run <= settings.Runs; run++)
            {
                string label = run <= 0 ? "warmup" : $"run {run}/{settings.Runs}";
                foreach (var document in documents)
                {
                    foreach (var host in hosts)
                    {
                        string name = host.Handler.Name;
                        // Saved once, to check what was measured.
                        string? png = run == 1 ? Path.Combine(outDir, "thumbnails", $"{name}-{document.Id}.png") : null;
                        var result = host.Render(document.Path, size, png, timeout);
                        if (result.Cold && result.Ms is { } coldMs) cold[name].Add(coldMs);
                        string line = $"{name.PadRight(width)}  {document.Id,-6}  {label,-9}  thumbnail ";
                        if (result.Error is not null)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine(line + result.Error);
                            Console.ResetColor();
                        }
                        else
                        {
                            string stream = result.StreamCalls is null ? "" : $"{result.StreamCalls,5:N0} stream calls   {result.BytesRead / 1e6,4:N1} MB read   ";
                            Console.WriteLine(line + $"{result.Ms,7:N1} ms   CPU {result.CpuMs,5:N0} ms   {result.Processes} processes   " +
                                $"{stream}{result.Width}x{result.Height}" +
                                (result.Cold ? "   (cold: loaded the handler)" : ""));
                        }
                        if (run <= 0) continue;
                        runs[(name, document.Id)].Add(result);
                        File.AppendAllText(csv, string.Join(',', name, document.Id, run, R(result.Ms), R(result.CpuMs), result.Processes,
                            result.StreamCalls, R(result.BytesRead / 1e6), result.Width, result.Height, result.Cold ? 1 : 0, result.Error is null ? "" : $"\"{result.Error}\"") + "\n");
                    }
                }
            }
            return (runs, cold.Where(c => c.Value.Count > 0).ToDictionary());
        }
        finally
        {
            foreach (var host in hosts) host.Dispose();
            if (init >= 0) Com.CoUninitialize();
        }

        static string R(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    }
}
