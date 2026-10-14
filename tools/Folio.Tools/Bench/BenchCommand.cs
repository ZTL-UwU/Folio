using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Folio.Tools.Bench;

/// <summary>
/// Compares how fast PDF viewers show a document and how much memory they use, and how fast File Explorer
/// thumbnail handlers for PDFs make a thumbnail of it.
/// </summary>
/// <remarks>
/// Each run starts a viewer on a benchmark document (see <see cref="Documents"/>) and watches its windows until
/// page 1's magenta band covers --fraction of one (<see cref="WindowCapture"/>), keeping them in front meanwhile. The
/// machine can be used during a run, though that adds noise. Viewers are closed between runs, and every
/// document/viewer pair gets --warmup runs that aren't recorded first, so the numbers are warm starts (files in the
/// OS cache). Viewers take turns, to spread out noise. The thumbnail handlers are measured after the viewers
/// (see <see cref="Thumbnails"/>).
/// </remarks>
internal static class BenchCommand
{
    /// <summary>What --apps can name: viewers, and thumbnail handlers. Folio is both.</summary>
    public static readonly string[] All = [.. Viewer.All.Union(Handler.All)];

    public static async Task RunAsync(Options options)
    {
        var settings = new BenchSettings(
            Runs: (int)options.Number("runs", 5),
            Warmup: (int)options.Number("warmup", 1),
            SettleSeconds: options.Number("settle", 5),
            TimeoutSeconds: options.Number("timeout", 60),
            CooldownSeconds: options.Number("cooldown", 0.5),
            // Low enough for a page at fit-page zoom, high enough that a thumbnail in a sidebar doesn't count.
            MarkerFraction: options.Number("fraction", 0.01));
        if (settings.Runs < 1) throw new ToolException("--runs must be at least 1.");
        // Explorer's Large icons view.
        int thumbnailSize = (int)options.Number("thumbnail-size", 256);
        if (thumbnailSize is < 1 or > 10000) throw new ToolException("--thumbnail-size must be from 1 to 10000 pixels.");
        var names = (options.List("apps") ?? All)
            .Select(n => All.FirstOrDefault(a => a.Equals(n, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"Unknown app '{n}'; pick from {string.Join(", ", All)}."))
            .Distinct().ToList();
        string? folioOption = options.String("folio");
        string folio = folioOption is not null ? Path.GetFullPath(folioOption) : Repo.Path("out", "bench", "folio", "Folio.exe");
        if (folioOption is not null && !File.Exists(folio)) throw new ToolException($"{folio} doesn't exist.");
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string outDir = Path.GetFullPath(options.String("out") ?? Repo.Path("out", "bench", stamp));
        bool saveFrames = options.Flag("save-frames"), closeRunning = options.Flag("close-running");

        var viewers = new List<Viewer>();
        try
        {
            foreach (var name in names.Where(n => Viewer.All.Contains(n)))
            {
                if (Viewer.Find(name, folio) is { } viewer) viewers.Add(viewer);
                else Console.WriteLine($"{name} isn't installed; skipping it.");
            }
            CloseRunning(viewers, closeRunning);
            if (folioOption is null && names.Contains("Folio")) PublishFolio(Path.GetDirectoryName(folio)!);
            // A given Folio build is measured with the handler next to it, as the package has it.
            string? folioHandler = !names.Contains("Folio") ? null
                : folioOption is null ? Handler.PublishFolio()
                : Path.Combine(Path.GetDirectoryName(folio)!, Handler.FolioDll);
            var handlers = new List<Handler>();
            foreach (var name in names.Where(n => Handler.All.Contains(n)))
            {
                if (Handler.Find(name, folioHandler) is { } handler) handlers.Add(handler);
                else Console.WriteLine(name == "Folio" ? $"There's no {Handler.FolioDll} next to {folio}; skipping Folio's thumbnails." : $"{name} isn't installed; skipping it.");
            }
            if (viewers.Count == 0 && handlers.Count == 0) throw new ToolException("None of the apps are installed.");

            var documents = await Documents.PrepareAsync();
            Directory.CreateDirectory(outDir);
            var runs = viewers.Count > 0 ? await MeasureAsync(viewers, documents, settings, outDir, saveFrames) : [];
            var (thumbnails, coldThumbnails) = handlers.Count > 0 ? Thumbnails.Measure(handlers, documents, settings, thumbnailSize, outDir) : ([], []);

            var versions = viewers.Where(v => v.Version is not null).ToDictionary(v => v.Name, v => v.Version!);
            foreach (var handler in handlers.Where(h => h.Version is not null)) versions.TryAdd(handler.Name, handler.Version!);
            var results = new BenchResults
            {
                Date = DateTimeOffset.Now,
                Machine = BenchResults.DescribeMachine(),
                Windows = BenchResults.DescribeWindows(),
                Commit = Repo.Commit(),
                Runs = settings.Runs,
                Warmup = settings.Warmup,
                SettleSeconds = settings.SettleSeconds,
                Documents = documents.Select(d => new DocumentInfo(d.Id, d.Name, d.Pages)).ToList(),
                Results = documents.SelectMany(d => viewers.Select(v => Summary.Of(v.Name, d.Id, runs[(v.Name, d.Id)]))).ToList(),
                Versions = versions,
                ThumbnailSize = handlers.Count > 0 ? thumbnailSize : null,
                Thumbnails = handlers.Count > 0 ? documents.SelectMany(d => handlers.Select(h => ThumbnailSummary.Of(h.Name, d.Id, thumbnails[(h.Name, d.Id)]))).ToList() : null,
                ThumbnailColdMs = handlers.Count > 0 ? coldThumbnails : null,
            };
            string json = Path.Combine(outDir, "results.json");
            results.Save(json);
            PrintSummary(results);
            string chart = Path.Combine(outDir, "chart.png");
            Chart.Render(results, names, chart);
            Console.WriteLine();
            if (viewers.Count > 0) Console.WriteLine($"Every run:  {Path.Combine(outDir, "runs.csv")}");
            if (handlers.Count > 0) Console.WriteLine($"Thumbnails: {Path.Combine(outDir, "thumbnails.csv")}, and each handler's in {Path.Combine(outDir, "thumbnails")}");
            Console.WriteLine($"Medians:    {json}");
            Console.WriteLine($"Chart:      {chart}");
        }
        finally
        {
            foreach (var viewer in viewers) viewer.Dispose();
        }
    }

    /// <summary>Viewers that are already running would take the file instead of a new process starting.</summary>
    private static void CloseRunning(List<Viewer> viewers, bool close)
    {
        var running = viewers.SelectMany(v => v.Running()).ToList();
        if (running.Count == 0) return;
        string names = string.Join(", ", running.Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase));
        if (!close) throw new ToolException($"Close {names} first, or pass --close-running to stop them (unsaved work is lost).");
        Console.WriteLine($"Closing {names}...");
        foreach (var process in running)
        {
            try
            {
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(15));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    /// <summary>Publishes this checkout's Folio as CI does (Release, Native AOT).</summary>
    private static void PublishFolio(string output)
    {
        Console.WriteLine($"Publishing Folio to {output}...");
        string[] args = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? ["-p:Platform=ARM64"] : [];
        if (!DotNet.Publish(Repo.Path("src", "Folio", "Folio.csproj"), output, args))
            throw new ToolException("Publishing Folio failed; see above. Or pass --folio <exe> to measure another build.");
    }

    private static async Task<Dictionary<(string App, string Document), List<Measurement>>> MeasureAsync(
        List<Viewer> viewers, List<BenchDocument> documents, BenchSettings settings, string outDir, bool saveFrames)
    {
        var runs = viewers.SelectMany(v => documents.Select(d => (v.Name, d.Id))).ToDictionary(k => k, _ => new List<Measurement>());
        string csv = Path.Combine(outDir, "runs.csv");
        File.WriteAllText(csv, "App,Document,Run,WindowMs,PageMs,DetectedMs,MemoryMB,PeakMemoryMB,WorkingSetMB,CommitMB,Processes,Error\n");
        int width = viewers.Max(v => v.Name.Length);
        bool warnedLocked = false;

        try
        {
            // Lets the capture leave out the yellow border; harmless if refused, as the border isn't in the frames.
            await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
        }
        catch (Exception)
        {
        }
        Native.timeBeginPeriod(1);
        try
        {
            using var device = new CaptureDevice();
            for (int run = 1 - settings.Warmup; run <= settings.Runs; run++)
            {
                string label = run <= 0 ? "warmup" : $"run {run}/{settings.Runs}";
                foreach (var document in documents)
                {
                    foreach (var viewer in viewers)
                    {
                        string? frame = saveFrames ? Path.Combine(outDir, "frames", $"{viewer.Name}-{document.Id}-{label.Replace('/', '-').Replace(' ', '-')}.png") : null;
                        if (!warnedLocked && Native.IsSessionLocked())
                        {
                            Console.WriteLine("The screen is locked: viewers start and draw more slowly, so these numbers will be higher than usual.");
                            warnedLocked = true;
                        }
                        var started = DateTime.Now.AddSeconds(-1);
                        // Warm-up runs only load the files into the OS cache; their memory isn't recorded.
                        var result = await Measurement.RunAsync(viewer, document.Path, settings, device, frame, timeOnly: run <= 0);
                        result.Stop(viewer, started);

                        string line = $"{viewer.Name.PadRight(width)}  {document.Id,-6}  {label,-9}  ";
                        if (result.Error is not null)
                        {
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine(line + result.Error);
                            Console.ResetColor();
                        }
                        else
                        {
                            string memory = result.MemoryMB is null ? "" : $"   memory {result.MemoryMB,6:N0} MB   peak {result.PeakMemoryMB,6:N0} MB   {result.Processes} processes";
                            Console.WriteLine(line + $"window {result.WindowMs,6:N0} ms   page {result.PageMs,6:N0} ms{memory}");
                        }
                        if (run > 0)
                        {
                            runs[(viewer.Name, document.Id)].Add(result);
                            File.AppendAllText(csv, string.Join(',', viewer.Name, document.Id, run, R(result.WindowMs), R(result.PageMs),
                                R(result.DetectedMs), R(result.MemoryMB), R(result.PeakMemoryMB), R(result.WorkingSetMB), R(result.CommitMB),
                                result.Processes, result.Error is null ? "" : $"\"{result.Error}\"") + "\n");
                        }
                        Thread.Sleep(TimeSpan.FromSeconds(settings.CooldownSeconds));
                    }
                }
            }
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
        return runs;

        static string R(double? value) => value?.ToString("0.#", CultureInfo.InvariantCulture) ?? "";
    }

    private static void PrintSummary(BenchResults results)
    {
        if (results.Results.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Medians of {results.Runs} runs. Memory is the private working set of all the viewer's processes once it settled (at most {results.SettleSeconds:0.#} s after page 1 showed).");
            int width = Math.Max(3, results.Results.Max(r => r.App.Length));
            foreach (var document in results.Documents)
            {
                Console.WriteLine();
                Console.WriteLine(document.Name);
                Console.WriteLine($"  {"App".PadRight(width)}  {"OK",5}  {"Window",8}  {"Page",8}  {"Page range",13}  {"Memory",8}  {"Peak",8}  {"Commit",8}  Processes");
                foreach (var s in results.Results.Where(r => r.Document == document.Id))
                {
                    string range = s.PageMinMs is null ? "" : $"{s.PageMinMs:N0}-{s.PageMaxMs:N0}";
                    Console.WriteLine($"  {s.App.PadRight(width)}  {$"{s.Succeeded}/{s.Runs}",5}  {Ms(s.WindowMs),8}  {Ms(s.PageMs),8}  {range,13}  " +
                        $"{Mb(s.MemoryMB),8}  {Mb(s.PeakMemoryMB),8}  {Mb(s.CommitMB),8}  {s.Processes:0}");
                }
            }
        }

        if (results.Thumbnails is { Count: > 0 } thumbnails)
        {
            Console.WriteLine();
            Console.WriteLine($"File Explorer thumbnails at {results.ThumbnailSize} px, medians of {results.Runs} runs. Time is from creating the handler to getting the bitmap.");
            Console.WriteLine("CPU is the host's and that of processes it started, which Windows counts in scheduler ticks (about 16 ms).");
            int width = Math.Max(7, thumbnails.Max(t => t.App.Length));
            foreach (var document in results.Documents)
            {
                Console.WriteLine();
                Console.WriteLine(document.Name);
                Console.WriteLine($"  {"Handler".PadRight(width)}  {"OK",5}  {"Time",10}  {"Range",15}  {"CPU",8}  Processes  Stream calls  {"Read",7}  Bitmap");
                foreach (var t in thumbnails.Where(t => t.Document == document.Id))
                {
                    string range = t.MinMs is null ? "" : $"{t.MinMs:N1}-{t.MaxMs:N1}";
                    string read = t.ReadMB is null ? "-" : $"{t.ReadMB:N1} MB";
                    Console.WriteLine($"  {t.App.PadRight(width)}  {$"{t.Succeeded}/{t.Runs}",5}  {(t.Ms is null ? "-" : $"{t.Ms:N1} ms"),10}  {range,15}  {Ms(t.CpuMs),8}  " +
                        $"{t.Processes,9:0}  {t.StreamCalls,12:N0}  {read,7}  {(t.Width is null ? "-" : $"{t.Width}x{t.Height}")}");
                }
            }
            if (results.ThumbnailColdMs is { Count: > 0 } cold)
            {
                Console.WriteLine();
                Console.WriteLine("Left out of the medians, thumbnails from a host that had just started, which also loaded the handler: " +
                    string.Join("; ", cold.Select(c => $"{c.Key} {string.Join(", ", c.Value.Select(ms => $"{ms:N0} ms"))}")) + ".");
            }
        }

        static string Ms(double? value) => value is null ? "-" : $"{value:N0} ms";
        static string Mb(double? value) => value is null ? "-" : $"{value:N0} MB";
    }
}

/// <summary>Redraws the chart of an earlier bench run, for example with fewer apps for the README.</summary>
internal static class ChartCommand
{
    public static Task RunAsync(Options options)
    {
        if (options.Positional.Count != 1) throw new ToolException("Usage: chart <results.json> [--apps Folio,Edge,Acrobat] [--out <png>]");
        string json = Path.GetFullPath(options.Positional[0]);
        var results = BenchResults.Load(json);
        var available = results.Results.Select(r => r.App).Concat(results.Thumbnails?.Select(t => t.App) ?? []).Distinct().ToList();
        var apps = options.List("apps")?
            .Select(a => available.FirstOrDefault(x => x.Equals(a, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"{json} has no results for {a}; it has {string.Join(", ", available)}."))
            .ToList() ?? available;
        string png = Path.GetFullPath(options.String("out") ?? Path.Combine(Path.GetDirectoryName(json)!, "chart.png"));
        Chart.Render(results, apps, png);
        Console.WriteLine($"Chart: {png}");
        return Task.CompletedTask;
    }
}
