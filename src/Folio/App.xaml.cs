using Folio.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Folio;

public partial class App : Application
{
    private const long MaxLogSize = 1024 * 1024;

    /// <summary>Open windows, in creation order.</summary>
    internal static List<MainWindow> OpenWindows { get; } = [];
    internal static MainWindow? LastActiveWindow { get; set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogError(e.Exception);
            // Keep the process alive so unsaved annotations aren't lost, but say so: the window
            // that failed may not be fully working any more.
            e.Handled = true;
            (LastActiveWindow ?? OpenWindows.LastOrDefault())?.ReportUnexpectedError();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (Program.IsPrimary)
        {
            // No other Folio is running, so leftovers from earlier sessions are no longer in use by us.
            _ = Task.Run(() => AttachmentFiles.Purge(TimeSpan.FromDays(1)));
            SingleInstance.Listen(paths => dispatcher.TryEnqueue(() => OpenFiles(paths)));
        }
        OpenFiles(Program.StartupFiles);
    }

    /// <summary>
    /// Opens each file in the window that already shows it, an idle start page, or a new window.
    /// With no files, opens a new window.
    /// </summary>
    internal static void OpenFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            var window = new MainWindow();
            window.Activate();
            return;
        }
        foreach (var path in files)
        {
            var window = OpenWindows.FirstOrDefault(w => w.IsShowing(path)) ?? OpenWindows.FirstOrDefault(w => w.IsIdle) ?? new MainWindow();
            window.BringToFront();
            _ = window.OpenAsync(path);
        }
    }

    /// <summary>Applies the theme chosen in Settings to every open window.</summary>
    internal static void ApplyTheme()
    {
        foreach (var window in OpenWindows) window.ApplyTheme();
    }

    private static void LogError(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppState.Folder);
            string log = Path.Combine(AppState.Folder, "errors.log");
            var info = new FileInfo(log);
            if (info.Exists && info.Length > MaxLogSize) File.Move(log, Path.Combine(AppState.Folder, "errors.old.log"), overwrite: true);
            File.AppendAllText(log, $"[{DateTimeOffset.Now:u}] {exception}\n\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
