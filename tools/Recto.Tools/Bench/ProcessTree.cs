using System.Runtime.InteropServices;

namespace Recto.Tools.Bench;

/// <summary>
/// The launched process, everything it starts (transitively, even after a parent exits), and any process named
/// in <paramref name="extraImages"/> that started after launch, for viewers that hand work to brokers.
/// </summary>
internal sealed class ProcessTree(int root, long launchTime, IReadOnlyCollection<string> extraImages)
{
    private readonly HashSet<int> _rejected = [];

    public HashSet<int> Pids { get; } = [root];

    public void Update()
    {
        var entries = Snapshot();
        bool added;
        do
        {
            added = false;
            foreach (var (pid, parent, image) in entries)
            {
                if (Pids.Contains(pid) || _rejected.Contains(pid)) continue;
                if (!Pids.Contains(parent) && !extraImages.Contains(image, StringComparer.OrdinalIgnoreCase)) continue;
                // Guards against a reused pid, or a process of the viewer that was already running.
                if (StartTime(pid) >= launchTime)
                {
                    Pids.Add(pid);
                    added = true;
                }
                else
                {
                    _rejected.Add(pid);
                }
            }
        } while (added);
    }

    private static List<(int Pid, int Parent, string Image)> Snapshot()
    {
        var list = new List<(int, int, string)>();
        var snapshot = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snapshot == new IntPtr(-1)) return list;
        try
        {
            var entry = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            for (bool ok = Native.Process32FirstW(snapshot, ref entry); ok; ok = Native.Process32NextW(snapshot, ref entry))
                list.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, Path.GetFileNameWithoutExtension(entry.szExeFile)));
        }
        finally
        {
            Native.CloseHandle(snapshot);
        }
        return list;
    }

    /// <summary>When the process started, as a UTC FILETIME; 0 if it can't be opened.</summary>
    private static long StartTime(int pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return 0;
        try
        {
            return Native.GetProcessTimes(handle, out long created, out _, out _, out _) ? created : 0;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}

internal readonly record struct MemoryTotals(ulong PrivateWorkingSet, ulong WorkingSet, ulong Commit, int Processes);

/// <summary>Sums memory over a process tree and remembers the highest total.</summary>
internal sealed class MemorySampler
{
    public ulong PeakPrivateWorkingSet { get; private set; }

    public MemoryTotals Sample(ProcessTree tree)
    {
        ulong privateWorkingSet = 0, workingSet = 0, commit = 0;
        int processes = 0;
        foreach (int pid in tree.Pids)
        {
            var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_VM_READ, false, pid);
            if (handle == IntPtr.Zero) handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) continue;
            try
            {
                if (Native.GetExitCodeProcess(handle, out uint code) && code != Native.STILL_ACTIVE) continue;
                var counters = new Native.PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX2>() };
                if (!Native.GetProcessMemoryInfo(handle, ref counters, counters.cb)) continue;
                privateWorkingSet += (ulong)counters.PrivateWorkingSetSize;
                workingSet += (ulong)counters.WorkingSetSize;
                commit += (ulong)counters.PrivateUsage;
                processes++;
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }
        PeakPrivateWorkingSet = Math.Max(PeakPrivateWorkingSet, privateWorkingSet);
        return new MemoryTotals(privateWorkingSet, workingSet, commit, processes);
    }
}
