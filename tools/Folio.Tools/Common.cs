using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Folio.Tools;

/// <summary>An error to report without a stack trace.</summary>
internal sealed class ToolException(string message) : Exception(message);

/// <summary>Command line options: <c>--name value</c>, <c>--flag</c>, and positional arguments.</summary>
internal sealed class Options
{
    private static readonly Dictionary<string, string[]> Known = new()
    {
        ["bench"] = ["apps", "runs", "warmup", "settle", "timeout", "cooldown", "fraction", "folio", "thumbnail-size", "out", "save-frames", "close-running"],
        ["chart"] = ["apps", "out"],
        ["icon"] = [],
        ["thumbnail-host"] = [],
    };

    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public Options(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].StartsWith("--"))
            {
                Positional.Add(list[i]);
                continue;
            }
            string name = list[i][2..];
            _named[name] = i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : null;
        }
    }

    /// <summary>Rejects options the command doesn't take, before a long run starts with a typo.</summary>
    public void CheckKnown(string command)
    {
        var unknown = _named.Keys.FirstOrDefault(name => !Known[command].Contains(name, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null) throw new ToolException($"{command} has no option --{unknown}.");
    }

    public bool Flag(string name)
    {
        if (!_named.TryGetValue(name, out var value)) return false;
        // A flag followed by a positional argument would have swallowed it.
        if (value is not null) throw new ToolException($"--{name} doesn't take a value.");
        return true;
    }

    public string? String(string name)
    {
        if (!_named.TryGetValue(name, out var value)) return null;
        return value ?? throw new ToolException($"--{name} needs a value.");
    }

    public double Number(string name, double fallback)
    {
        var text = String(name);
        if (text is null) return fallback;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : throw new ToolException($"--{name} needs a number, not '{text}'.");
    }

    public string[]? List(string name) =>
        String(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class Repo
{
    /// <summary>The checkout this tool was built from: the folder above its output that has Folio.sln.</summary>
    public static string Root { get; } = FindRoot();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Folio.sln"))) return dir.FullName;
        }
        throw new ToolException("Can't find the Folio checkout; run the tools with dotnet run --project tools/Folio.Tools.");
    }

    /// <summary>The checked-out commit, with "-dirty" if there are uncommitted changes.</summary>
    public static string? Commit()
    {
        try
        {
            var (code, output) = Run("git", ["describe", "--always", "--dirty", "--abbrev=7"], echo: false);
            return code == 0 ? output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // git isn't on PATH.
        }
    }

    /// <summary>Runs a program in the checkout and waits for it, returning its exit code and output.</summary>
    public static (int ExitCode, string Output) Run(string file, IEnumerable<string> args, bool echo = true,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) info.Environment[key] = value;
        using var process = Process.Start(info)!;
        var output = new System.Text.StringBuilder();
        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            if (echo) Console.WriteLine(e.Data);
        }
        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return (process.ExitCode, output.ToString());
    }
}

internal static class DotNet
{
    /// <summary>Publishes a project of this checkout in Release, with Native AOT where the project uses it.</summary>
    public static bool Publish(string project, string output, IEnumerable<string> args)
    {
        // Native AOT finds the linker through vcvarsall.bat, which fails unless vswhere.exe is on PATH.
        var environment = new Dictionary<string, string>();
        string installer = Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer");
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (Directory.Exists(installer) && !path.Contains(installer, StringComparison.OrdinalIgnoreCase)) environment["PATH"] = $"{path};{installer}";
        string[] publish = ["publish", project, "-c", "Release", "-o", output, "--nologo", "-v", "quiet", .. args];
        return Repo.Run("dotnet", publish, environment: environment).ExitCode == 0;
    }
}

/// <summary>Finds installed programs.</summary>
internal static class Apps
{
    /// <summary>Looks an executable up in App Paths, where installers register it for Start > Run.</summary>
    public static string? FromAppPaths(string exe)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string value && File.Exists(value.Trim('"'))) return value.Trim('"');
        }
        return null;
    }

    /// <summary>The first of <paramref name="paths"/> (environment variables expanded) that exists.</summary>
    public static string? FirstExisting(params string[] paths) =>
        paths.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);

    public static string? Edge() => FromAppPaths("msedge.exe") ?? FirstExisting(
        @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
        @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe");
}

/// <summary>Renders HTML to PNG with headless Microsoft Edge, which every Windows 11 machine has.</summary>
internal static class HtmlRenderer
{
    /// <summary>
    /// Renders <paramref name="html"/> in a <paramref name="width"/> x <paramref name="height"/> CSS pixel
    /// viewport at <paramref name="scale"/> device pixels per CSS pixel, on a transparent background.
    /// </summary>
    public static void Render(string html, string png, int width, int height, double scale = 1)
    {
        string edge = Apps.Edge() ?? throw new ToolException("Microsoft Edge isn't installed; the tools render images with it.");
        // A profile of its own, so an Edge that's already running doesn't take the job.
        string scratch = Path.Combine(Path.GetTempPath(), "folio-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string page = Path.Combine(scratch, "page.html");
        string shot = Path.Combine(scratch, "page.png");
        try
        {
            File.WriteAllText(page, html);
            var info = new ProcessStartInfo(edge)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "--headless=new", "--disable-gpu", "--hide-scrollbars", "--no-first-run",
                    "--user-data-dir=" + Path.Combine(scratch, "profile"),
                    "--default-background-color=00000000",
                    "--force-device-scale-factor=" + scale.ToString(CultureInfo.InvariantCulture),
                    $"--window-size={width},{height}",
                    "--screenshot=" + shot,
                    new Uri(page).AbsoluteUri,
                },
            };
            using var process = Process.Start(info)!;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                throw new ToolException("Edge didn't finish rendering within a minute.");
            }
            if (!File.Exists(shot)) throw new ToolException($"Edge didn't render the image (exit code {process.ExitCode}).");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(png))!);
            File.Copy(shot, png, overwrite: true);
        }
        finally
        {
            Files.DeleteDirectory(scratch);
        }
    }
}

internal static class Files
{
    /// <summary>Deletes a folder, retrying while a process that just exited still holds files in it.</summary>
    public static void DeleteDirectory(string path)
    {
        for (int attempt = 0; attempt < 10 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
