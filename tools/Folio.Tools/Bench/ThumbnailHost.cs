using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Folio.Tools.Bench;

/// <summary>One thumbnail: how long it took, and what it cost.</summary>
internal sealed record ThumbnailRun(double? Ms, double CpuMs, int Processes, long StreamCalls, long BytesRead, int Width, int Height, string? Error);

/// <summary>
/// A process of this tool (the hidden <c>thumbnail-host</c> command) that loads one handler and makes thumbnails with
/// it, as Explorer's COM surrogate does. It stays up between thumbnails, as the surrogate does while Explorer
/// keeps asking, and is started again if a thumbnail times out.
/// </summary>
/// <remarks>
/// For each thumbnail it's sent a line, <c>&lt;size&gt; &lt;marshaled stream, hex&gt; &lt;png path or -&gt;</c>, and
/// answers <c>@ok &lt;ms&gt; &lt;width&gt; &lt;height&gt;</c> or <c>@error &lt;HRESULT&gt; &lt;ms&gt;</c>, then <c>@done</c>
/// once it has saved the PNG.
/// </remarks>
internal sealed class ThumbnailHost(Handler handler) : IDisposable
{
    private const int STGM_READ = 0;

    private Process? _process;
    private Job? _job;
    private BlockingCollection<string> _replies = [];
    private bool _served;

    public Handler Handler => handler;

    /// <summary>The first thumbnail the host made, which also loaded the handler, as a surrogate that's just started does.</summary>
    public double? FirstMs { get; private set; }

    public ThumbnailRun Render(string pdf, int size, string? png, TimeSpan timeout)
    {
        Start();
        bool first = !_served;
        _served = true;
        using var stream = new CountingFileStream(pdf);
        string marshaled = Convert.ToHexString(Com.MarshalStream(stream));
        var (cpu, processes) = _job!.Totals();
        _process!.StandardInput.WriteLine($"{size} {marshaled} {png ?? "-"}");
        string? reply = Reply(timeout);
        var (cpuAfter, processesAfter) = _job.Totals();
        if (reply is null)
        {
            Stop();
            return new(null, 0, 0, stream.Calls, stream.BytesRead, 0, 0, $"no thumbnail after {timeout.TotalSeconds:0} s");
        }
        // The PNG is saved after the time is taken, and before the next thumbnail's CPU time is.
        if (Reply(timeout) != "@done") Stop();

        string[] parts = reply.Split(' ');
        double cpuMs = (cpuAfter - cpu).TotalMilliseconds;
        int started = processesAfter - processes;
        if (parts[0] != "@ok") return new(null, cpuMs, started, stream.Calls, stream.BytesRead, 0, 0, $"no thumbnail ({parts[1]})");
        double ms = double.Parse(parts[1], CultureInfo.InvariantCulture);
        if (first) FirstMs = ms;
        return new(ms, cpuMs, started, stream.Calls, stream.BytesRead,
            int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture), null);
    }

    private void Start()
    {
        if (_process is { HasExited: false }) return;
        Stop();
        // dotnet run starts the apphost; `dotnet Folio.Tools.dll` would be dotnet.exe.
        string self = Environment.ProcessPath!;
        var info = new ProcessStartInfo(self)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(ThumbnailHost).Assembly.Location);
        foreach (var arg in (string[])["thumbnail-host", handler.Dll, handler.Clsid.ToString()]) info.ArgumentList.Add(arg);
        var replies = _replies = [];
        _process = Process.Start(info)!;
        _job = new Job();
        _job.Add(_process);
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) replies.CompleteAdding();
            else if (e.Data.StartsWith('@')) replies.Add(e.Data);
            else Console.WriteLine($"  [{handler.Name}] {e.Data}");
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) Console.Error.WriteLine($"  [{handler.Name}] {e.Data}");
        };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private string? Reply(TimeSpan timeout)
    {
        try
        {
            return _replies.TryTake(out var line, timeout) ? line : null;
        }
        catch (InvalidOperationException)
        {
            return null; // The host exited.
        }
    }

    private void Stop()
    {
        if (_process is null) return;
        // Ends the processes the handler started too.
        _job?.Terminate();
        _process.WaitForExit(TimeSpan.FromSeconds(5));
        _process.Dispose();
        _job?.Dispose();
        (_process, _job) = (null, null);
    }

    public void Dispose()
    {
        if (_process is { HasExited: false }) _process.StandardInput.Close();
        _process?.WaitForExit(TimeSpan.FromSeconds(5));
        Stop();
    }

    /// <summary>The <c>thumbnail-host &lt;dll&gt; &lt;clsid&gt;</c> command: the host's side.</summary>
    public static Task ServeAsync(Options options)
    {
        if (options.Positional is not [var dll, var clsid]) throw new ToolException("Usage: thumbnail-host <dll> <clsid>; bench starts it.");
        Exception? error = null;
        // Single-threaded, like the thread the surrogate calls handlers on; PowerToys' starts a process with
        // ShellExecuteEx, which wants one.
        var thread = new Thread(() =>
        {
            try
            {
                Serve(dll, Guid.Parse(clsid));
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new ToolException(error.ToString());
        return Task.CompletedTask;
    }

    private static unsafe void Serve(string dll, Guid clsid)
    {
        nint factory = 0;
        while (Console.ReadLine() is { } line)
        {
            string[] parts = line.Split(' ', 3);
            uint size = uint.Parse(parts[0], CultureInfo.InvariantCulture);
            string? png = parts[2] == "-" ? null : parts[2];
            int hr = Com.UnmarshalStream(Convert.FromHexString(parts[1]), out nint stream);
            nint bitmap = 0;
            var watch = Stopwatch.StartNew();
            if (hr >= 0 && factory == 0) hr = Load(dll, clsid, out factory);
            if (hr >= 0) hr = Render(factory, stream, size, out bitmap);
            watch.Stop();
            if (stream != 0) Marshal.Release(stream);

            string ms = watch.Elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);
            if (bitmap == 0)
            {
                // PowerToys' returns S_OK without a bitmap when it can't write its temporary copy of the file.
                Console.WriteLine($"@error 0x{(hr < 0 ? hr : unchecked((int)0x80004005)):X8} {ms}");
            }
            else
            {
                var (width, height) = Gdi.Size(bitmap);
                Console.WriteLine($"@ok {ms} {width} {height}");
                if (png is not null) SavePng(bitmap, width, height, png);
                Gdi.DeleteObject(bitmap);
            }
            Console.WriteLine("@done");
        }
    }

    /// <summary>Loads the handler's DLL and gets its class factory, as COM does for the first object of a class.</summary>
    private static unsafe int Load(string dll, Guid clsid, out nint factory)
    {
        factory = 0;
        // By path, so the DLL's own folder is searched for what it needs.
        var getClassObject = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(NativeLibrary.Load(dll), "DllGetClassObject");
        Guid iid = Com.IID_IClassFactory;
        nint result;
        int hr = getClassObject(&clsid, &iid, &result);
        if (hr >= 0) factory = result;
        return hr;
    }

    /// <summary>What Explorer does for each file: a new handler, given the stream, asked for the bitmap.</summary>
    private static int Render(nint factory, nint stream, uint size, out nint bitmap)
    {
        bitmap = 0;
        nint init = 0, provider = 0;
        try
        {
            int hr = Com.CreateInstance(factory, Com.IID_IInitializeWithStream, out init);
            if (hr < 0) return hr;
            hr = Com.Initialize(init, stream, STGM_READ);
            if (hr < 0) return hr;
            hr = Marshal.QueryInterface(init, Com.IID_IThumbnailProvider, out provider);
            if (hr < 0) return hr;
            return Com.GetThumbnail(provider, size, out bitmap, out _);
        }
        finally
        {
            if (provider != 0) Marshal.Release(provider);
            if (init != 0) Marshal.Release(init);
        }
    }

    private static void SavePng(nint bitmap, int width, int height, string path)
    {
        var pixels = Gdi.Pixels(bitmap, width, height);
        if (pixels is null) return;
        // LoadImage, which PowerToys' uses, makes a device-dependent bitmap; if that lost the alpha channel, it's opaque.
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;
        if (!hasAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        EncodeAsync().GetAwaiter().GetResult();

        async Task EncodeAsync()
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            stream.Seek(0);
            await stream.AsStreamForRead().CopyToAsync(file);
        }
    }
}
