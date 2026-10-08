using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Folio.Services;

/// <summary>
/// Keeps one Folio process per user session. Later launches hand their files to the running
/// process over a named pipe and exit, so there is a single writer of the persisted state and
/// opening several PDFs from Explorer doesn't start several processes.
/// </summary>
internal static class SingleInstance
{
    private const string Header = "folio-open/1";
    private const string Ack = "ok";
    private const int MaxPaths = 256;

    // The mutex is per session (Local\); the pipe namespace is machine wide, hence the session id.
    // CurrentUserOnly restricts the pipe to this user on both ends.
    private static readonly string Name = $"Folio-{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    private static Mutex? _mutex;

    /// <summary>True if this process is the first one in the session; it should then call <see cref="Listen"/>.</summary>
    public static bool TryBecomePrimary()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    /// <summary>
    /// Sends <paramref name="paths"/> (absolute) to the primary process. Returns false if it
    /// didn't take them, for example because it's shutting down; the caller then runs on its own.
    /// </summary>
    public static bool TryForward(IReadOnlyList<string> paths)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            pipe.Connect(5000);
            // We were started by the user, so we may give the running process the right to come to the front.
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid)) AllowSetForegroundWindow(pid);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { NewLine = "\n" };
            writer.WriteLine(Header);
            foreach (var path in paths.Take(MaxPaths)) writer.WriteLine(path);
            writer.WriteLine();
            writer.Flush();
            var reply = new StreamReader(pipe, Encoding.UTF8).ReadLineAsync();
            return reply.Wait(5000) && reply.Result == Ack;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or AggregateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Serves forwarded launches in the background. <paramref name="open"/> is called on a
    /// thread-pool thread, possibly for several requests at once, and returns whether the request
    /// was accepted.
    /// </summary>
    public static void Listen(Func<IReadOnlyList<string>, bool> open)
    {
        var thread = new Thread(() =>
        {
            // The first instance claims the name, so a process that got there first can't receive our launches.
            var options = PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance;
            while (true)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Someone else holds the name. Later launches will time out and run on their own.
                    return;
                }
                options &= ~PipeOptions.FirstPipeInstance;
                try
                {
                    server.WaitForConnection();
                }
                catch (IOException)
                {
                    server.Dispose();
                    continue;
                }
                // Each request has its own pipe instance and deadline, so a client that connects
                // and then stalls can't hold up the next launch.
                _ = ServeAsync(server, open);
            }
        })
        {
            IsBackground = true,
            Name = "Folio single instance",
        };
        thread.Start();
    }

    private static async Task ServeAsync(NamedPipeServerStream server, Func<IReadOnlyList<string>, bool> open)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using (server)
            {
                var reader = new StreamReader(server, Encoding.UTF8);
                if (await reader.ReadLineAsync(timeout.Token) != Header) return;
                var paths = new List<string>();
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                {
                    if (paths.Count < MaxPaths && line.Length <= 32767 && Path.IsPathFullyQualified(line)) paths.Add(line);
                }
                if (!open(paths)) return;
                var writer = new StreamWriter(server, new UTF8Encoding(false)) { NewLine = "\n" };
                await writer.WriteLineAsync(Ack.AsMemory(), timeout.Token);
                await writer.FlushAsync(timeout.Token);
                // Let the client read the reply and hang up before the pipe is closed.
                var rest = new byte[64];
                while (await server.ReadAsync(rest, timeout.Token) > 0)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // The other side went away or took too long.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
