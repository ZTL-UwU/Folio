namespace Folio.Pdf;

public enum WorkPriority
{
    /// <summary>Things the user is looking at right now.</summary>
    Visible = 0,
    /// <summary>Things that are likely to be needed soon (neighbouring pages, text).</summary>
    Normal = 1,
    /// <summary>Thumbnails, search, annotation scans.</summary>
    Background = 2,
}

/// <summary>
/// PDFium is not thread safe, so every call into it is funnelled through one dedicated
/// thread. Work items are executed by priority and skipped when cancelled before they start.
/// </summary>
internal static class PdfWorker
{
    private abstract class WorkItem
    {
        public abstract void Execute();
    }

    private sealed class WorkItem<T>(Func<T> func, CancellationToken token) : WorkItem
    {
        public readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Execute()
        {
            if (token.IsCancellationRequested)
            {
                Completion.TrySetCanceled(token);
                return;
            }
            try
            {
                Completion.TrySetResult(func());
            }
            catch (OperationCanceledException oce)
            {
                Completion.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }
    }

    private static readonly PriorityQueue<WorkItem, (int, long)> Queue = new();
    private static readonly object Gate = new();
    private static long _sequence;

    static PdfWorker()
    {
        new Thread(Loop) { IsBackground = true, Name = "PDFium" }.Start();
    }

    public static Task<T> Run<T>(Func<T> func, WorkPriority priority = WorkPriority.Normal, CancellationToken token = default)
    {
        var item = new WorkItem<T>(func, token);
        if (token.IsCancellationRequested)
        {
            item.Completion.TrySetCanceled(token);
            return item.Completion.Task;
        }
        lock (Gate)
        {
            Queue.Enqueue(item, ((int)priority, _sequence++));
            Monitor.Pulse(Gate);
        }
        return item.Completion.Task;
    }

    public static Task Run(Action action, WorkPriority priority = WorkPriority.Normal, CancellationToken token = default)
        => Run(() => { action(); return true; }, priority, token);

    private static unsafe void Loop()
    {
        var config = new FPDF_LIBRARY_CONFIG { Version = 2 };
        Native.FPDF_InitLibraryWithConfig(&config);

        while (true)
        {
            WorkItem item;
            lock (Gate)
            {
                while (Queue.Count == 0) Monitor.Wait(Gate);
                item = Queue.Dequeue();
            }
            item.Execute();
        }
    }
}
