using System.Text.Json;
using Microsoft.Win32;

namespace Recto.Tools.Bench;

/// <summary>A bench run's medians and what they were measured on, saved as results.json for <see cref="ChartCommand"/>.</summary>
internal sealed record BenchResults
{
    public required DateTimeOffset Date { get; init; }
    public required string Machine { get; init; }
    public required string Windows { get; init; }
    public string? Commit { get; init; }
    public required int Runs { get; init; }
    public required int Warmup { get; init; }
    public required double SettleSeconds { get; init; }
    public required List<DocumentInfo> Documents { get; init; }
    public required List<Summary> Results { get; init; }
    /// <summary>Each viewer's and thumbnail handler's version, such as "26.2" or, for Recto, "1.0 · 48ea997"; missing from results saved before versions were recorded.</summary>
    public Dictionary<string, string>? Versions { get; init; }
    /// <summary>The size File Explorer thumbnails were asked for, in pixels; missing, like the rest, from results saved before they were measured.</summary>
    public int? ThumbnailSize { get; init; }
    public List<ThumbnailSummary>? Thumbnails { get; init; }
    /// <summary>Each handler's thumbnails from a host that had just started, which also loaded the handler: the first, and any after a host was restarted.</summary>
    public Dictionary<string, List<double>>? ThumbnailColdMs { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static BenchResults Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<BenchResults>(File.ReadAllText(path), Json)
                ?? throw new ToolException($"{path} is empty.");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            throw new ToolException($"Can't read {path}: {e.Message}");
        }
    }

    /// <summary>The processor and installed memory, such as "AMD Ryzen 9 8945HS, 32 GB".</summary>
    public static string DescribeMachine()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        string cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown processor";
        // Physical memory less what firmware and devices reserve; rounding up to a power of two recovers what's installed.
        double gb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (double)(1L << 30);
        return $"{cpu}, {Math.Pow(2, Math.Ceiling(Math.Log2(gb))):0} GB";
    }

    /// <summary>The Windows release, such as "Windows 11 25H2 (build 26200)".</summary>
    public static string DescribeWindows()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        int build = Environment.OSVersion.Version.Build;
        string name = build >= 22000 ? "Windows 11" : "Windows 10";
        return key?.GetValue("DisplayVersion") is string release ? $"{name} {release} (build {build})" : $"{name} (build {build})";
    }
}

internal sealed record DocumentInfo(string Id, string Name, int Pages);

/// <summary>Medians over the successful runs of one viewer on one document.</summary>
internal sealed record Summary(string App, string Document, int Succeeded, int Runs,
    double? WindowMs, double? PageMs, double? PageMinMs, double? PageMaxMs,
    double? MemoryMB, double? PeakMemoryMB, double? CommitMB, double? Processes)
{
    public static Summary Of(string app, string document, IReadOnlyList<Measurement> runs)
    {
        var ok = runs.Where(r => r.Error is null).ToList();
        return new Summary(app, document, ok.Count, runs.Count,
            Median(ok.Select(r => r.WindowMs)), Median(ok.Select(r => r.PageMs)),
            ok.Min(r => r.PageMs), ok.Max(r => r.PageMs),
            Median(ok.Select(r => r.MemoryMB)), Median(ok.Select(r => r.PeakMemoryMB)), Median(ok.Select(r => r.CommitMB)),
            Median(ok.Select(r => (double?)r.Processes)));
    }

    public static double? Median(IEnumerable<double?> values)
    {
        var sorted = values.OfType<double>().Order().ToList();
        if (sorted.Count == 0) return null;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
