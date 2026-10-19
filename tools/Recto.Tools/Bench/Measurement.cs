using System.Diagnostics;

namespace Recto.Tools.Bench;

internal sealed record BenchSettings(int Runs, int Warmup, double SettleSeconds, double TimeoutSeconds, double CooldownSeconds, double MarkerFraction);

/// <summary>What one launch of a viewer measured. Times are from the launch call, in ms; memory is in MB.</summary>
internal sealed class Measurement
{
    /// <summary>To the viewer's first top-level window being shown.</summary>
    public double? WindowMs { get; set; }

    /// <summary>To DWM composing the first frame that shows page 1's marker band.</summary>
    public double? PageMs { get; set; }

    /// <summary>To this tool finishing the check of that frame; a sanity check on <see cref="PageMs"/>.</summary>
    public double? DetectedMs { get; set; }

    // Totals over the viewer's processes once their memory settled after the page showed.
    public double? MemoryMB { get; set; } // private working set, as Task Manager's Memory column
    public double? WorkingSetMB { get; set; }
    public double? CommitMB { get; set; } // private bytes, including memory paged out or not touched yet
    public int Processes { get; set; }

    /// <summary>The highest private working set total from launch to the end of the settle period.</summary>
    public double? PeakMemoryMB { get; set; }

    public string? Error { get; set; }

    public HashSet<int> Pids { get; set; } = [];

    /// <summary>
    /// Starts <paramref name="viewer"/> on <paramref name="file"/>, waits for page 1 to show, then (unless
    /// <paramref name="timeOnly"/>) for its memory to settle, and reads the memory of the viewer's processes.
    /// Leaves the viewer running; see <see cref="Stop"/>.
    /// </summary>
    public static async Task<Measurement> RunAsync(Viewer viewer, string file, BenchSettings settings, CaptureDevice device,
        string? framePath, bool timeOnly = false)
    {
        var result = new Measurement();
        var existing = WindowCapture.TopLevelWindows().ToHashSet();
        // A second of slack for clock granularity when matching processes by start time.
        long launchTime = DateTime.UtcNow.ToFileTimeUtc() - TimeSpan.TicksPerSecond;

        long launched = Stopwatch.GetTimestamp();
        int pid;
        try
        {
            // Through the shell, as when opening a file from File Explorer.
            using var process = Process.Start(new ProcessStartInfo(viewer.Exe, viewer.Arguments(file)) { UseShellExecute = true })
                ?? throw new InvalidOperationException("no process started");
            pid = process.Id;
        }
        catch (Exception e)
        {
            result.Error = "launch failed: " + e.Message;
            return result;
        }
        double Elapsed() => Stopwatch.GetElapsedTime(launched).TotalMilliseconds;

        var tree = new ProcessTree(pid, launchTime, viewer.Images);
        var memory = new MemorySampler();
        var captures = new Dictionary<IntPtr, WindowCapture?>();
        WindowCapture? hit = null;
        try
        {
            double lastTree = double.MinValue, lastMemory = double.MinValue, lastRaise = double.MinValue;
            while (Elapsed() < settings.TimeoutSeconds * 1000)
            {
                double now = Elapsed();
                if (now - lastTree >= 100)
                {
                    tree.Update();
                    lastTree = now;
                }
                if (now - lastMemory >= 250)
                {
                    memory.Sample(tree);
                    lastMemory = now;
                }

                foreach (var hwnd in WindowCapture.FindWindows(existing, tree))
                {
                    result.WindowMs ??= Elapsed();
                    if (!captures.ContainsKey(hwnd)) captures[hwnd] = WindowCapture.TryStart(hwnd, device, settings.MarkerFraction, launched);
                }
                if (now - lastRaise >= 100)
                {
                    foreach (var hwnd in captures.Keys) WindowCapture.BringToFront(hwnd);
                    lastRaise = now;
                }

                hit = captures.Values.Where(c => c?.MatchTime is not null).MinBy(c => c!.MatchTime);
                if (hit is not null) break;
                Thread.Sleep(2);
            }
            if (hit is not null && framePath is not null) await hit.SaveMatchAsync(framePath);
        }
        finally
        {
            foreach (var capture in captures.Values) capture?.Dispose();
        }

        if (hit is not null)
        {
            result.PageMs = hit.MatchTime;
            result.DetectedMs = hit.DetectedTime;
        }
        else
        {
            result.Error = result.WindowMs is null ? "no window appeared"
                : captures.Values.All(c => c is null) ? "couldn't capture the viewer's windows"
                : "page 1 never showed";
        }

        tree.Update();
        result.Pids = tree.Pids;
        if (timeOnly) return result;

        // Let the viewer finish loading (thumbnails, text, prefetching) before the reading that counts: until its
        // memory has stayed within 2% (or 2 MB) for StableMs, or for at most the settle time.
        const int SampleMs = 250, StableMs = 2000;
        var recent = new Queue<ulong>();
        double settleEnd = Elapsed() + settings.SettleSeconds * 1000;
        MemoryTotals final;
        while (true)
        {
            tree.Update();
            final = memory.Sample(tree);
            recent.Enqueue(final.PrivateWorkingSet);
            if (recent.Count > StableMs / SampleMs + 1) recent.Dequeue();
            bool stable = recent.Count > StableMs / SampleMs && recent.Max() - recent.Min() <= Math.Max(2UL << 20, recent.Max() / 50);
            if (stable || Elapsed() >= settleEnd) break;
            Thread.Sleep(SampleMs);
        }
        const double MB = 1 << 20;
        result.MemoryMB = final.PrivateWorkingSet / MB;
        result.WorkingSetMB = final.WorkingSet / MB;
        result.CommitMB = final.Commit / MB;
        result.PeakMemoryMB = memory.PeakPrivateWorkingSet / MB;
        result.Processes = final.Processes;
        return result;
    }

    /// <summary>Closes what the run started: its process tree and the viewer's processes that started since.</summary>
    public void Stop(Viewer viewer, DateTime started)
    {
        var processes = Pids.Select(TryGetProcess).OfType<Process>()
            .Concat(viewer.Images.SelectMany(Process.GetProcessesByName).Where(p => StartedSince(p, started)))
            .ToList();
        foreach (var process in processes)
        {
            try
            {
                process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to stop.
            }
        }
        foreach (var process in processes)
        {
            try
            {
                process.WaitForExit(TimeSpan.FromSeconds(15));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            process.Dispose();
        }
    }

    private static Process? TryGetProcess(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool StartedSince(Process process, DateTime time)
    {
        try
        {
            return process.StartTime >= time;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
