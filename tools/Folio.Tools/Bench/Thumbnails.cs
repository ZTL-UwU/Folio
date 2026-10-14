using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Folio.Tools.Bench;

/// <summary>A File Explorer thumbnail handler for PDFs.</summary>
internal sealed record Handler(string Name, string Dll, Guid Clsid, string? Version)
{
    public static readonly string[] All = ["Folio", "PowerToys"];

    /// <summary>ThumbnailProvider.Clsid in src/Folio.Thumbnails.</summary>
    private static readonly Guid FolioClsid = new("54e57302-4f85-47eb-8be3-76ee7e671857");

    /// <summary>CLSID_PdfThumbnailProvider in PowerToys' src/modules/previewpane/FileExplorerDllExporter/dllmain.cpp.</summary>
    private static readonly Guid PowerToysClsid = new("d8bb9942-93bd-412d-87e4-33fab214dc1a");

    private const string FolioDll = "Folio.Thumbnails.dll", PowerToysDll = "PowerToys.PdfThumbnailProviderCpp.dll";

    /// <summary>The handler called <paramref name="name"/> (one of <see cref="All"/>), or null if it isn't there.</summary>
    public static Handler? Find(string name, string folio) => name switch
    {
        "Folio" => FindFolio(folio),
        "PowerToys" => FindPowerToys(),
        _ => throw new ArgumentException(name),
    };

    /// <summary>Folio's, next to the Folio being measured, with its pdfium.dll, as in the package.</summary>
    private static Handler? FindFolio(string folio)
    {
        string dll = Path.Combine(Path.GetDirectoryName(folio)!, FolioDll);
        return File.Exists(dll) ? new("Folio", dll, FolioClsid, null) : null;
    }

    /// <summary>PowerToys', from a per-user or machine-wide install. It doesn't have to be turned on, as it's loaded from its DLL.</summary>
    private static Handler? FindPowerToys()
    {
        if (Apps.FirstExisting($@"%LOCALAPPDATA%\PowerToys\{PowerToysDll}", $@"%ProgramFiles%\PowerToys\{PowerToysDll}") is not { } dll) return null;
        var info = FileVersionInfo.GetVersionInfo(dll);
        return new("PowerToys", dll, PowerToysClsid, $"{info.FileMajorPart}.{info.FileMinorPart}");
    }

    /// <summary>Publishes this checkout's handler next to the published Folio, which has the pdfium.dll it loads.</summary>
    public static void PublishFolio(string output)
    {
        Console.WriteLine($"Publishing Folio's thumbnail handler to {output}...");
        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (!DotNet.Publish(Repo.Path("src", "Folio.Thumbnails", "Folio.Thumbnails.csproj"), output, ["-r", rid]))
            throw new ToolException("Publishing the thumbnail handler failed; see above.");
    }
}

/// <summary>Medians over the successful thumbnails of one handler on one document.</summary>
internal sealed record ThumbnailSummary(string App, string Document, int Succeeded, int Runs,
    double? Ms, double? MinMs, double? MaxMs, double? CpuMs, double? Processes, double? StreamCalls, double? ReadMB, int? Width, int? Height)
{
    public static ThumbnailSummary Of(string app, string document, IReadOnlyList<ThumbnailRun> runs)
    {
        var ok = runs.Where(r => r.Error is null).ToList();
        return new ThumbnailSummary(app, document, ok.Count, runs.Count,
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
/// recorded, so files are in the OS cache and the handlers loaded. The first thumbnail of each, which loads
/// it, is reported on its own.
/// </remarks>
internal static class Thumbnails
{
    public static (Dictionary<(string App, string Document), List<ThumbnailRun>> Runs, Dictionary<string, double> FirstMs) Measure(
        List<Handler> handlers, List<BenchDocument> documents, BenchSettings settings, int size, string outDir)
    {
        var runs = handlers.SelectMany(h => documents.Select(d => (h.Name, d.Id))).ToDictionary(k => k, _ => new List<ThumbnailRun>());
        string csv = Path.Combine(outDir, "thumbnails.csv");
        File.WriteAllText(csv, "App,Document,Run,Ms,CpuMs,Processes,StreamCalls,ReadMB,Width,Height,Error\n");
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
                        string line = $"{name.PadRight(width)}  {document.Id,-6}  {label,-9}  thumbnail ";
                        if (result.Error is not null)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine(line + result.Error);
                            Console.ResetColor();
                        }
                        else
                        {
                            Console.WriteLine(line + $"{result.Ms,7:N1} ms   CPU {result.CpuMs,5:N0} ms   {result.Processes} processes   " +
                                $"{result.StreamCalls,5:N0} stream calls   {result.BytesRead / 1e6,4:N1} MB read   {result.Width}x{result.Height}");
                        }
                        if (run <= 0) continue;
                        runs[(name, document.Id)].Add(result);
                        File.AppendAllText(csv, string.Join(',', name, document.Id, run, R(result.Ms), R(result.CpuMs), result.Processes,
                            result.StreamCalls, R(result.BytesRead / 1e6), result.Width, result.Height, result.Error is null ? "" : $"\"{result.Error}\"") + "\n");
                    }
                }
            }
            return (runs, hosts.Where(h => h.FirstMs is not null).ToDictionary(h => h.Handler.Name, h => h.FirstMs!.Value));
        }
        finally
        {
            foreach (var host in hosts) host.Dispose();
            if (init >= 0) Com.CoUninitialize();
        }

        static string R(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    }
}
