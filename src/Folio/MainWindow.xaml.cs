using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Folio.Controls;
using Folio.Pdf;
using Folio.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using ZoomMode = Folio.Services.ZoomMode;
using COMException = System.Runtime.InteropServices.COMException;

namespace Folio;

public sealed record OutlineEntry(string Title, string Page, PdfDestination? Destination);

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _searchDebounce;
    private readonly DispatcherQueueTimer _reloadDebounce;
    private readonly DispatcherQueueTimer _hintTimer;
    private readonly TaskCompletionSource _loaded = new();
    private readonly ObservableCollection<SearchItem> _searchItems = [];
    private readonly ObservableCollection<AnnotationItem> _annotationItems = [];
    private readonly Dictionary<int, PdfAnnotation[]> _annotations = [];
    private List<ThumbnailItem> _thumbnails = [];
    private readonly EventHandler _recentChanged;
    /// <summary>Folders under %TEMP%\Folio holding attachments this window extracted; deleted when it closes.</summary>
    private readonly List<string> _attachmentFolders = [];
    private PdfDocument? _document;
    /// <summary>The password that unlocked <see cref="_document"/>, kept for reloads during this session.</summary>
    private string? _password;
    private int _opening;
    private RecentDocument? _entry;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _annotationScanCts;
    private FileSystemWatcher? _watcher;
    private DateTime _ignoreWatcherUntil;
    private int _searchIndex = -1;
    private string _searchQuery = "";
    private bool _syncingThumbnails;
    private bool _sidebarOpen;
    private bool _fullscreen;
    private bool _presenting;
    private bool _sidebarBeforeFullscreen;
    private bool _annotationsScanned;
    private bool _forceClose;
    private bool _hasOutline;
    private bool _hasAttachments;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        Title = "Folio";

        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var prefs = AppState.Preferences;
        double scale = GetDpiScale();
        AppWindow.Resize(new SizeInt32((int)(Math.Max(640, prefs.WindowWidth) * scale), (int)(Math.Max(480, prefs.WindowHeight) * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(560 * scale);
            presenter.PreferredMinimumHeight = (int)(420 * scale);
            if (prefs.WindowMaximized) presenter.Maximize();
        }
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) => OnClosed();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) App.LastActiveWindow = this;
            else Viewer.StopMiddleScroll();
        };
        App.OpenWindows.Add(this);

        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(350);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, _) => StartSearch(SearchBox.Text);

        _reloadDebounce = DispatcherQueue.CreateTimer();
        _reloadDebounce.Interval = TimeSpan.FromMilliseconds(600);
        _reloadDebounce.IsRepeating = false;
        _reloadDebounce.Tick += (_, _) => OnFileChangedOnDisk();

        _hintTimer = DispatcherQueue.CreateTimer();
        _hintTimer.Interval = TimeSpan.FromSeconds(2.5);
        _hintTimer.IsRepeating = false;
        _hintTimer.Tick += (_, _) => FullscreenHint.Opacity = 0;

        SearchResultList.ItemsSource = _searchItems;
        AnnotationList.ItemsSource = _annotationItems;
        SidebarGrip.Target = SidebarColumn;
        foreach (var list in new Control[] { ThumbnailList, OutlineTree, AnnotationList, AttachmentList, SearchResultList })
            ScrollBarHideFix.Attach(list);
        SidebarGrip.Resized += (_, width) => AppState.Preferences.SidebarWidthOverride = width;
        SidebarGrip.DoubleTapped += (_, _) =>
        {
            // Back to the automatic width.
            AppState.Preferences.SidebarWidthOverride = null;
            if (_sidebarOpen) SidebarColumn.Width = new GridLength(SidebarWidth());
        };

        Viewer.CurrentPageChanged += (_, _) => OnCurrentPageChanged();
        Viewer.ZoomChanged += (_, _) => UpdateZoomText();
        Viewer.ViewSettingsChanged += (_, _) => OnViewSettingsChanged();
        Viewer.AnnotationsChanged += (_, page) => OnAnnotationsChanged(page);
        Viewer.EditFailed += (_, message) => ShowMessage("Unable to change annotations", message, InfoBarSeverity.Error);
        Viewer.SearchRequested += (_, text) =>
        {
            SearchBox.Text = text;
            StartSearch(text);
        };

        Root.Loaded += (_, _) => _loaded.TrySetResult();
        SearchBox.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnSearchKeyDown), true);
        SetupAccelerators();
        // AppState is static: unsubscribed in OnClosed, or it would keep every closed window alive.
        _recentChanged = (_, _) => DispatcherQueue.TryEnqueue(RefreshRecent);
        AppState.RecentChanged += _recentChanged;
        RefreshRecent();
        UpdateChrome();
        ApplyTheme();
    }

    /// <summary>Applies the theme chosen in Settings.</summary>
    internal void ApplyTheme()
    {
        var theme = AppState.Preferences.Theme;
        Root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // The caption buttons are drawn by the system, outside the XAML content.
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private double GetDpiScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetDpiForWindow(hwnd) / 96.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ================================================================ opening & closing

    public async Task OpenAsync(string path)
    {
        _opening++;
        try
        {
            await _loaded.Task;
            if (!File.Exists(path))
            {
                ShowMessage("File not found", $"“{Path.GetFileName(path)}” could not be found. It may have been moved or deleted.", InfoBarSeverity.Warning);
                return;
            }
            if (IsShowing(path)) return;
            if (!await ConfirmDiscardChangesAsync()) return;

            LoadingRing.IsActive = true;
            (PdfDocument? Document, string? Password) loaded;
            try
            {
                loaded = await LoadAsync(path, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                ShowMessage("Unable to open document", ex.Message, InfoBarSeverity.Error);
                return;
            }
            finally
            {
                LoadingRing.IsActive = false;
            }
            if (loaded.Document is { } document)
                ShowDocument(document, loaded.Password);
        }
        finally
        {
            _opening--;
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/>, asking for a password until it's right. Returns no document
    /// if the user cancelled the password prompt.
    /// </summary>
    private async Task<(PdfDocument? Document, string? Password)> LoadAsync(string path, string? password)
    {
        while (true)
        {
            try
            {
                return (await PdfDocument.OpenAsync(path, password), password);
            }
            catch (PdfPasswordException ex)
            {
                password = await AskPasswordAsync(Path.GetFileName(path), ex.WrongPassword);
                if (password is null) return (null, null);
            }
        }
    }

    /// <summary>Whether this window shows the file at <paramref name="path"/> (a full path).</summary>
    internal bool IsShowing(string path) =>
        _document is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_document.FilePath), StringComparison.OrdinalIgnoreCase);

    /// <summary>On the start page and not about to open anything, so it can take a document opened from outside.</summary>
    internal bool IsIdle => _document is null && _opening == 0;

    internal void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    internal void ReportUnexpectedError()
    {
        if (Root.XamlRoot is null) return;
        ShowMessage("Something went wrong",
            $"Folio ran into an unexpected problem. If something stops working, save your annotations and restart Folio. Details are in {Path.Combine(AppState.Folder, "errors.log")}.",
            InfoBarSeverity.Error);
    }

    private void ShowDocument(PdfDocument document, string? password, bool reloading = false)
    {
        var previous = _document;
        PersistPosition();
        CancelSearch(clearText: !reloading);
        _annotationScanCts?.Cancel();

        _document = document;
        _password = password;
        // Attachments opened from another PDF are temporary copies: don't list them as recent.
        bool remember = AppState.Preferences.RememberRecent && !AttachmentFiles.Contains(document.FilePath);
        var existing = remember ? AppState.Find(document.FilePath) : reloading ? _entry : null;
        _entry = existing ?? new RecentDocument { Path = document.FilePath };
        // The start page shows recent documents without asking for passwords, so nothing from
        // inside an encrypted one (its title or a picture of its first page) is kept there.
        bool encrypted = document.Info.IsEncrypted;
        _entry.Title = encrypted || string.IsNullOrWhiteSpace(document.Info.Title) ? "" : document.Info.Title;
        _entry.LastOpened = DateTimeOffset.Now;
        if (remember && encrypted) AppState.DeleteThumbnail(document.FilePath);
        if (remember) AppState.Touch(_entry);

        StartView.Visibility = Visibility.Collapsed;
        DocumentLayout.Visibility = Visibility.Visible;
        // Size the sidebar for this document's thumbnails before the viewer lays out, so its
        // viewport (and the fit-width zoom the first pages render at) doesn't change afterwards.
        _thumbnailRotation = existing?.Rotation ?? 0;
        SetSidebarOpen(AppState.Preferences.SidebarOpen, persist: false);
        Root.UpdateLayout();

        Viewer.SetDocument(document, existing);
        previous?.Dispose();

        UpdateChrome();
        UpdateTitle();
        UpdateZoomText();
        BuildThumbnails();
        LoadOutline();
        LoadAttachments();
        _annotations.Clear();
        _annotationItems.Clear();
        _annotationsScanned = false;
        SelectSidebarTab(AppState.Preferences.SidebarPage);
        WatchFile(document.FilePath);
        if (remember && !encrypted) _ = SaveRecentThumbnailAsync(document);
        Viewer.Focus(FocusState.Programmatic);
    }

    public async void CloseDocument()
    {
        // Without recent documents the start page has nothing to offer, so the window goes as well.
        if (!AppState.Preferences.RememberRecent)
        {
            await CloseWindowAsync();
            return;
        }
        if (_document is null) return;
        if (!await ConfirmDiscardChangesAsync()) return;
        if (_presenting) SetPresenting(false);
        if (_fullscreen) SetFullscreen(false);
        PersistPosition();
        CancelSearch(clearText: true);
        _annotationScanCts?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        var document = _document;
        _document = null;
        _password = null;
        _entry = null;
        Viewer.SetDocument(null);
        document.Dispose();
        _thumbnails = [];
        ThumbnailList.ItemsSource = null;
        OutlineTree.RootNodes.Clear();
        AttachmentList.ItemsSource = null;
        _annotationItems.Clear();
        DocumentLayout.Visibility = Visibility.Collapsed;
        StartView.Visibility = Visibility.Visible;
        UpdateChrome();
        UpdateTitle();
        RefreshRecent();
    }

    private async Task ReloadAsync()
    {
        if (_document is null) return;
        var current = _document;
        string path = current.FilePath;
        PersistPosition();
        try
        {
            // Encrypted files reopen with the password from this session; if that no longer works, ask again.
            var (document, password) = await LoadAsync(path, _password);
            if (document is null) return;
            if (_document != current)
            {
                // Another document was opened or this one closed while we were loading.
                document.Dispose();
                return;
            }
            ShowDocument(document, password, reloading: true);
            if (!string.IsNullOrEmpty(_searchQuery)) StartSearch(_searchQuery);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowMessage("Unable to reload document", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void PersistPosition()
    {
        if (_entry is null || _document is null) return;
        _entry.Page = Viewer.CurrentPage;
        _entry.ZoomMode = Viewer.ZoomMode;
        _entry.Zoom = Viewer.Zoom;
        _entry.Rotation = Viewer.PageRotation;
        if (!Viewer.IsPresenting)
        {
            _entry.Continuous = Viewer.IsContinuous;
            _entry.Dual = Viewer.IsDual;
        }
        AppState.Save();
    }

    private void WatchFile(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is null) return;
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => DispatcherQueue.TryEnqueue(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); });
            _watcher.Renamed += (_, _) => DispatcherQueue.TryEnqueue(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); });
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
    }

    private void OnFileChangedOnDisk()
    {
        if (_document is null || DateTime.UtcNow < _ignoreWatcherUntil || !File.Exists(_document.FilePath)) return;
        if (!_document.IsModified && !Viewer.HasPendingEdits)
        {
            _ = ReloadAsync();
            return;
        }
        var reload = new Button { Content = "Reload" };
        reload.Click += async (_, _) =>
        {
            Notification.IsOpen = false;
            await ReloadAsync();
        };
        ShowMessage("The document changed on disk", "Reloading will discard your unsaved annotations.", InfoBarSeverity.Warning, reload);
    }

    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        // A note still being typed counts as an unsaved change.
        await Viewer.CommitPendingEditsAsync();
        if (_document is not { IsModified: true }) return true;
        var dialog = Dialogs.Create(Root.XamlRoot);
        dialog.Title = "Save changes?";
        dialog.Content = $"“{Path.GetFileName(_document.FilePath)}” has unsaved annotations. Do you want to save them before closing?";
        dialog.PrimaryButtonText = "Save";
        dialog.SecondaryButtonText = "Don't save";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return false;
        if (result == ContentDialogResult.Primary) return await SaveAsync();
        return true;
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose) return;
        if (_document is { IsModified: true } || Viewer.HasPendingEdits)
        {
            args.Cancel = true;
            await CloseWindowAsync();
            return;
        }
        SaveWindowState();
        PersistPosition();
    }

    /// <summary>Closes the window, offering to save unsaved annotations first.</summary>
    private async Task CloseWindowAsync()
    {
        if (!await ConfirmDiscardChangesAsync()) return;
        // Close() skips OnAppWindowClosing, so remember the window and position now.
        SaveWindowState();
        PersistPosition();
        _forceClose = true;
        Close();
    }

    /// <summary>Releases everything that would otherwise outlive the window.</summary>
    private void OnClosed()
    {
        AppState.RecentChanged -= _recentChanged;
        App.OpenWindows.Remove(this);
        if (App.LastActiveWindow == this) App.LastActiveWindow = null;
        _searchDebounce.Stop();
        _reloadDebounce.Stop();
        _hintTimer.Stop();
        _searchCts?.Cancel();
        _annotationScanCts?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        foreach (var t in _thumbnails) t.Pending?.Cancel();
        _document?.Dispose();
        _document = null;
        _password = null;
        PrintService.Release(this);
        foreach (var dir in _attachmentFolders) AttachmentFiles.Delete(dir);
        _attachmentFolders.Clear();
        // The process may exit with this window; finish writing the position saved while closing.
        AppState.Flush();
    }

    /// <summary>Takes over a folder of extracted attachments, deleting it when this window closes.</summary>
    internal void AdoptAttachmentFolder(string dir) => _attachmentFolders.Add(dir);

    private void SaveWindowState()
    {
        var prefs = AppState.Preferences;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
        {
            prefs.WindowMaximized = true;
        }
        else if (!_fullscreen)
        {
            prefs.WindowMaximized = false;
            double scale = GetDpiScale();
            prefs.WindowWidth = (int)(AppWindow.Size.Width / scale);
            prefs.WindowHeight = (int)(AppWindow.Size.Height / scale);
        }
        AppState.Save();
    }

    private async Task<string?> AskPasswordAsync(string fileName, bool wrong)
    {
        var box = new PasswordBox { PlaceholderText = "Password", Width = 320 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"“{fileName}” is protected. Enter the password to open it.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        if (wrong)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "The password is incorrect. Try again.",
                Style = (Style)Application.Current.Resources["CriticalTextBlockStyle"],
            });
        }
        var dialog = Dialogs.Create(Root.XamlRoot);
        dialog.Title = "Password required";
        dialog.Content = panel;
        dialog.PrimaryButtonText = "Unlock";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                dialog.Hide();
                box.Tag = "submit";
            }
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary || Equals(box.Tag, "submit") ? box.Password : null;
    }

    // ================================================================ chrome

    private void UpdateChrome()
    {
        bool hasDoc = _document is not null;
        var visibility = hasDoc ? Visibility.Visible : Visibility.Collapsed;
        ZoomControls.Visibility = visibility;
        HeaderSeparator.Visibility = visibility;
        AppTitleBar.IsPaneToggleButtonVisible = hasDoc && SearchPanel.Visibility != Visibility.Visible;
        foreach (var item in new MenuFlyoutItemBase[]
                 {
                     DocumentSeparator, SaveItem, SaveAsItem, PrintItem, ViewSeparator, ContinuousItem, DualItem, OddLeftItem,
                     InvertItem, RotateItem, ModeSeparator, FullscreenItem, PresentItem, InfoSeparator, ReloadItem,
                     ShowInFolderItem, PropertiesItem, CloseSeparator, CloseItem,
                 })
        {
            item.Visibility = visibility;
        }
    }

    private void UpdateTitle()
    {
        if (_document is null)
        {
            AppTitleBar.Title = "Folio";
            AppTitleBar.Subtitle = "";
            Title = "Folio";
            return;
        }
        string name = DisplayName(_document);
        AppTitleBar.Title = name;
        AppTitleBar.Subtitle = _document.IsModified ? "Edited" : "";
        AppTitleBar.InvalidateMeasure();
        Title = $"{(_document.IsModified ? "• " : "")}{name} – Folio";
    }

    private static string DisplayName(PdfDocument document)
    {
        var title = document.Info.Title;
        // Many generators write junk titles such as "untitled" or "Microsoft Word - foo.docx".
        if (string.IsNullOrWhiteSpace(title) || title.Length > 120 || title.Equals("untitled", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("Microsoft Word", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(document.FilePath);
        return title;
    }

    private void OnCurrentPageChanged()
    {
        if (_document is null) return;
        SyncThumbnailSelection();
    }

    private void UpdateZoomText()
    {
        ZoomButton.Content = $"{Math.Round(Viewer.Zoom * 100):0}%";
        FitPageItem.IsChecked = Viewer.ZoomMode == ZoomMode.FitPage;
        FitWidthItem.IsChecked = Viewer.ZoomMode == ZoomMode.FitWidth;
    }

    private void OnViewSettingsChanged()
    {
        var prefs = AppState.Preferences;
        if (!Viewer.IsPresenting)
        {
            prefs.Continuous = Viewer.IsContinuous;
            prefs.Dual = Viewer.IsDual;
        }
        prefs.OddPagesLeft = Viewer.OddPagesLeft;
        prefs.Inverted = Viewer.IsInverted;
        if (_thumbnails.Count > 0 && (_thumbnailRotation != Viewer.PageRotation || _thumbnailInverted != Viewer.IsInverted)) BuildThumbnails();
        UpdateZoomText();
    }

    private void OnMainMenuOpening(object? sender, object e)
    {
        ContinuousItem.IsChecked = Viewer.IsContinuous;
        DualItem.IsChecked = Viewer.IsDual;
        OddLeftItem.IsChecked = Viewer.OddPagesLeft;
        OddLeftItem.IsEnabled = Viewer.IsDual;
        InvertItem.IsChecked = Viewer.IsInverted;
        SaveItem.IsEnabled = _document?.IsModified == true;
        CloseItem.Text = AppState.Preferences.RememberRecent ? "Close document" : "Close window";
        FullscreenItem.Text = _fullscreen ? "Exit full screen" : "Full screen";
    }

    private void ShowMessage(string title, string message, InfoBarSeverity severity, ButtonBase? action = null)
    {
        Notification.Title = title;
        Notification.Message = message;
        Notification.Severity = severity;
        Notification.ActionButton = action;
        Notification.IsOpen = true;
    }

    private void ShowHint(string text)
    {
        FullscreenHintText.Text = text;
        FullscreenHint.Opacity = 1;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    // ================================================================ sidebar

    private void OnPaneToggleRequested(TitleBar sender, object args) => SetSidebarOpen(!_sidebarOpen);

    // While searching, the pane toggle becomes a back button that leaves search.
    private void OnTitleBarBackRequested(TitleBar sender, object args)
    {
        CancelSearch(clearText: true);
        Viewer.Focus(FocusState.Programmatic);
    }

    private void SetSidebarOpen(bool open, bool persist = true)
    {
        _sidebarOpen = open;
        Sidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SidebarGrip.Visibility = Sidebar.Visibility;
        SidebarColumn.Width = open ? new GridLength(SidebarWidth()) : new GridLength(0);
        DocumentCard.CornerRadius = open ? new CornerRadius(8, 0, 0, 0) : new CornerRadius(0);
        DocumentCard.BorderThickness = open ? new Thickness(1, 1, 0, 0) : new Thickness(0, 1, 0, 0);
        if (persist) AppState.Preferences.SidebarOpen = open;
        if (open) SyncThumbnailSelection();
    }

    /// <summary>The user's chosen width, or by default just wide enough for the page thumbnails.</summary>
    private double SidebarWidth()
    {
        if (AppState.Preferences.SidebarWidthOverride is { } width) return Math.Clamp(width, 150, 520);
        double thumbnail = _document is { } document ? document.PageSizes.Max(s => ThumbnailSize(s, _thumbnailRotation).Width) : ThumbnailWidth * 0.82;
        // Item padding (10 on each side) plus a roomy gutter on each side of the list.
        return Math.Clamp(thumbnail + 20 + 2 * 28, 150, 520);
    }

    private void SelectSidebarTab(int index)
    {
        var tabs = new[] { ThumbnailsTab, OutlineTab, AnnotationsTab, AttachmentsTab };
        var tab = tabs[Math.Clamp(index, 0, tabs.Length - 1)];
        if (tab.Visibility != Visibility.Visible) tab = ThumbnailsTab;
        SidebarSelector.SelectedItem = tab;
        ShowSidebarPage(tab);
    }

    private void OnSidebarSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is { } item) ShowSidebarPage(item);
    }

    private void ShowSidebarPage(SelectorBarItem tab)
    {
        ThumbnailList.Visibility = tab == ThumbnailsTab ? Visibility.Visible : Visibility.Collapsed;
        OutlineTree.Visibility = tab == OutlineTab ? Visibility.Visible : Visibility.Collapsed;
        AnnotationsPanel.Visibility = tab == AnnotationsTab ? Visibility.Visible : Visibility.Collapsed;
        AttachmentList.Visibility = tab == AttachmentsTab ? Visibility.Visible : Visibility.Collapsed;
        AppState.Preferences.SidebarPage = tab == OutlineTab ? 1 : tab == AnnotationsTab ? 2 : tab == AttachmentsTab ? 3 : 0;
        if (tab == AnnotationsTab) ScanAnnotations();
        if (tab == ThumbnailsTab) SyncThumbnailSelection();
    }

    // ---------------------------------------------------------------- thumbnails

    private const double ThumbnailWidth = 128;
    private int _thumbnailRotation;
    private bool _thumbnailInverted;

    private void BuildThumbnails()
    {
        if (_document is null) return;
        foreach (var t in _thumbnails) t.Pending?.Cancel();
        _thumbnailRotation = Viewer.PageRotation;
        _thumbnailInverted = Viewer.IsInverted;
        var paper = new SolidColorBrush(_thumbnailInverted ? Windows.UI.Color.FromArgb(255, 30, 30, 30) : Colors.White);
        _thumbnails = Enumerable.Range(0, _document.PageCount).Select(i =>
        {
            var (width, height) = ThumbnailSize(_document.PageSizes[i], _thumbnailRotation);
            return new ThumbnailItem { Index = i, Label = _document.GetPageLabel(i), Width = width, Height = height, Paper = paper };
        }).ToList();
        ThumbnailList.ItemsSource = _thumbnails;
        if (_sidebarOpen) SidebarColumn.Width = new GridLength(SidebarWidth());
        SyncThumbnailSelection();
    }

    private static (double Width, double Height) ThumbnailSize(Windows.Foundation.Size size, int rotation)
    {
        bool swap = rotation is 90 or 270;
        double w = swap ? size.Height : size.Width, h = swap ? size.Width : size.Height;
        double width = w >= h ? ThumbnailWidth : Math.Round(ThumbnailWidth * w / h);
        double height = w >= h ? Math.Round(ThumbnailWidth * h / w) : ThumbnailWidth;
        if (w < h)
        {
            // Portrait pages: fix the width, let height follow.
            width = ThumbnailWidth * 0.82;
            height = Math.Round(width * h / w);
        }
        // Extremely long or thin pages get a cropped thumbnail rather than a giant bitmap.
        return (width, Math.Clamp(height, 4, ThumbnailWidth * 3));
    }

    private void OnThumbnailContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not ThumbnailItem item) return;
        if (args.InRecycleQueue)
        {
            item.Pending?.Cancel();
            item.Image = null;
            return;
        }
        if (args.Phase == 0)
        {
            args.RegisterUpdateCallback(1, (_, a) => RenderThumbnail((ThumbnailItem)a.Item));
        }
    }

    private async void RenderThumbnail(ThumbnailItem item)
    {
        if (_document is null || item.Image is not null) return;
        var document = _document;
        item.Pending?.Cancel();
        var cts = item.Pending = new CancellationTokenSource();
        double raster = Root.XamlRoot?.RasterizationScale ?? 1;
        int rotation = _thumbnailRotation;
        var size = document.PageSizes[item.Index];
        double w = rotation is 90 or 270 ? size.Height : size.Width;
        double scale = item.Width * raster / w;
        var region = new RectInt32(0, 0, Math.Max(1, (int)Math.Round(item.Width * raster)), Math.Max(1, (int)Math.Round(item.Height * raster)));
        try
        {
            using var buffer = await document.RenderAsync(item.Index, scale, rotation, region, _thumbnailInverted, WorkPriority.Background, cts.Token);
            if (buffer is null || cts.IsCancellationRequested || document != _document) return;
            item.Image = PageView.ToBitmap(buffer);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
        }
    }

    private void SyncThumbnailSelection()
    {
        if (_document is null || _thumbnails.Count == 0) return;
        int page = Viewer.CurrentPage;
        if (page >= _thumbnails.Count) return;
        _syncingThumbnails = true;
        ThumbnailList.SelectedIndex = page;
        _syncingThumbnails = false;
        foreach (var t in _thumbnails) t.IsCurrent = t.Index == page;
        if (_sidebarOpen && ThumbnailList.Visibility == Visibility.Visible)
            ThumbnailList.ScrollIntoView(_thumbnails[page]);
    }

    private void OnThumbnailClick(object sender, ItemClickEventArgs e)
    {
        if (_syncingThumbnails || e.ClickedItem is not ThumbnailItem item) return;
        Viewer.GoToPage(item.Index);
        Viewer.Focus(FocusState.Programmatic);
    }

    // ---------------------------------------------------------------- outline

    private async void LoadOutline()
    {
        OutlineTree.RootNodes.Clear();
        _hasOutline = false;
        OutlineTab.Visibility = Visibility.Collapsed;
        if (_document is null) return;
        var document = _document;
        try
        {
            var outline = await document.GetOutlineAsync();
            if (document != _document) return;
            int total = 0;
            TreeViewNode Build(OutlineItem item)
            {
                total++;
                string page = item.Destination is { IsInternal: true } d ? document.GetPageLabel(d.PageIndex) : "";
                var node = new TreeViewNode { Content = new OutlineEntry(item.Title, page, item.Destination) };
                foreach (var child in item.Children) node.Children.Add(Build(child));
                return node;
            }
            foreach (var item in outline) OutlineTree.RootNodes.Add(Build(item));
            if (total <= 40)
            {
                foreach (var node in OutlineTree.RootNodes) node.IsExpanded = true;
            }
            _hasOutline = outline.Count > 0;
            OutlineTab.Visibility = _hasOutline ? Visibility.Visible : Visibility.Collapsed;
            if (_hasOutline && AppState.Preferences.SidebarPage == 1) SelectSidebarTab(1);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public static string OutlineTitle(object content) => (content as OutlineEntry)?.Title ?? "";
    public static string OutlinePage(object content) => (content as OutlineEntry)?.Page ?? "";

    private void OnOutlineInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode { Content: OutlineEntry { Destination: { } destination } })
        {
            Viewer.GoToDestination(destination);
        }
    }

    // ---------------------------------------------------------------- annotations

    private async void ScanAnnotations()
    {
        if (_document is null || _annotationsScanned) return;
        _annotationsScanned = true;
        _annotationScanCts?.Cancel();
        var cts = _annotationScanCts = new CancellationTokenSource();
        var document = _document;
        try
        {
            var all = await document.GetAllAnnotationsAsync(cts.Token);
            if (document != _document || cts.IsCancellationRequested) return;
            _annotations.Clear();
            foreach (var group in all.GroupBy(a => a.PageIndex)) _annotations[group.Key] = [.. group];
            RefreshAnnotationList();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async void OnAnnotationsChanged(int page)
    {
        UpdateTitle();
        if (_document is null) return;
        var document = _document;
        try
        {
            var text = await document.GetPageTextAsync(page, WorkPriority.Visible);
            if (document != _document) return;
            _annotations[page] = text.Annotations;
            if (_annotationsScanned) RefreshAnnotationList();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void RefreshAnnotationList()
    {
        if (_document is null) return;
        _annotationItems.Clear();
        foreach (var page in _annotations.Keys.Order())
        {
            foreach (var a in _annotations[page])
            {
                if (a.Kind == AnnotationKind.Attachment) continue;
                _annotationItems.Add(new AnnotationItem(a, _document.GetPageLabel(page)));
            }
        }
        AnnotationsEmpty.Visibility = _annotationItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAnnotationClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AnnotationItem item) Viewer.RevealAnnotation(item.Annotation);
    }

    private void OnAnnotationRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AnnotationItem item } element) return;
        var menu = new MenuFlyout();
        var show = new MenuFlyoutItem { Text = "Show", Icon = new FontIcon { Glyph = "\uE8A7" } };
        show.Click += (_, _) => Viewer.RevealAnnotation(item.Annotation);
        var delete = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "\uE74D" } };
        delete.Click += (_, _) => Viewer.DeleteAnnotation(item.Annotation);
        menu.Items.Add(show);
        menu.Items.Add(delete);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    // ---------------------------------------------------------------- attachments

    private async void LoadAttachments()
    {
        AttachmentList.ItemsSource = null;
        _hasAttachments = false;
        AttachmentsTab.Visibility = Visibility.Collapsed;
        if (_document is null) return;
        var document = _document;
        try
        {
            var attachments = await document.GetAttachmentsAsync();
            if (document != _document) return;
            AttachmentList.ItemsSource = attachments.Select(a => new AttachmentItem(a)).ToList();
            _hasAttachments = attachments.Count > 0;
            AttachmentsTab.Visibility = _hasAttachments ? Visibility.Visible : Visibility.Collapsed;
            if (_hasAttachments && AppState.Preferences.SidebarPage == 3) SelectSidebarTab(3);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnAttachmentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is AttachmentItem item) OpenAttachment(item);
    }

    private void OnAttachmentRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AttachmentItem item } element) return;
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open", Icon = new FontIcon { Glyph = "\uE8E5" } };
        open.Click += (_, _) => OpenAttachment(item);
        var save = new MenuFlyoutItem { Text = "Save as…", Icon = new FontIcon { Glyph = "\uE792" } };
        save.Click += (_, _) => SaveAttachment(item);
        menu.Items.Add(open);
        menu.Items.Add(save);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private async void OpenAttachment(AttachmentItem item)
    {
        if (_document is null) return;
        var document = _document;
        string name = LaunchPolicy.SafeFileName(item.Name);
        var risk = LaunchPolicy.Classify(name);
        if (risk == AttachmentRisk.Blocked)
        {
            var save = new Button { Content = "Save as…" };
            save.Click += (_, _) =>
            {
                Notification.IsOpen = false;
                SaveAttachment(item);
            };
            string saveName = LaunchPolicy.SaveFileName(name);
            ShowMessage("Folio won't open this attachment",
                saveName == name
                    ? $"“{name}” is a program, script or shortcut that could harm your computer. You can save it and check it before opening it."
                    : $"“{name}” is a shortcut that could harm your computer, even unopened. It can be saved as “{saveName}” so Windows ignores it.",
                InfoBarSeverity.Warning, save);
            return;
        }
        if (risk == AttachmentRisk.Ask && !await ConfirmOpenAttachmentAsync(name)) return;
        try
        {
            var data = await document.GetAttachmentDataAsync(item.Attachment);
            string dir = AttachmentFiles.CreateFolder();
            string path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, data);
            // Without the mark, the app that opens it can't tell it came from a document off the internet.
            if (!LaunchPolicy.MarkAsUntrusted(path) && risk != AttachmentRisk.Pdf)
            {
                AttachmentFiles.Delete(dir);
                ShowMessage("Unable to open attachment",
                    $"Windows couldn't mark “{name}” as coming from the internet, so Folio won't open it. Try saving it instead.",
                    InfoBarSeverity.Warning);
                return;
            }
            if (risk == AttachmentRisk.Pdf)
            {
                var window = new MainWindow();
                window.AdoptAttachmentFolder(dir);
                window.Activate();
                await window.OpenAsync(path);
                return;
            }
            _attachmentFolders.Add(dir);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            if (!await Launcher.LaunchFileAsync(file, new LauncherOptions { DisplayApplicationPicker = false }))
                ShowMessage("Unable to open attachment", $"Windows couldn't open “{name}”. Try saving it instead.", InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or COMException)
        {
            ShowMessage("Unable to open attachment", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<bool> ConfirmOpenAttachmentAsync(string name)
    {
        var dialog = Dialogs.Create(Root.XamlRoot);
        dialog.Title = "Open attachment?";
        dialog.Content = new TextBlock
        {
            Text = $"“{name}” will open in the app Windows uses for this type of file. Attachments can contain harmful content, so only open it if you trust where this document came from.",
            TextWrapping = TextWrapping.Wrap,
        };
        dialog.PrimaryButtonText = "Open";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void SaveAttachment(AttachmentItem item)
    {
        if (_document is null) return;
        var document = _document;
        string name = LaunchPolicy.SaveFileName(LaunchPolicy.SafeFileName(item.Name));
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(name),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        string ext = Path.GetExtension(name);
        picker.FileTypeChoices.Add(string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " file", [string.IsNullOrEmpty(ext) ? "." : ext]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;
        try
        {
            await File.WriteAllBytesAsync(result.Path, await document.GetAttachmentDataAsync(item.Attachment));
            if (!LaunchPolicy.MarkAsUntrusted(result.Path))
                ShowMessage("Attachment saved without a security mark",
                    $"This location can't record that “{Path.GetFileName(result.Path)}” came from the internet, so Windows won't warn you when it's opened.",
                    InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            ShowMessage("Unable to save attachment", ex.Message, InfoBarSeverity.Error);
        }
    }

    // ================================================================ search

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchDebounce.Stop();
        if (string.IsNullOrEmpty(sender.Text)) StartSearch("");
        else _searchDebounce.Start();
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchDebounce.Stop();
        if (args.QueryText == _searchQuery && _searchItems.Count > 0) MoveSearch(+1);
        else StartSearch(args.QueryText);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            CancelSearch(clearText: true);
            Viewer.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && IsShiftDown())
        {
            MoveSearch(-1);
            e.Handled = true;
        }
    }

    private static bool IsShiftDown() => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnSearchOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_searchQuery)) StartSearch(_searchQuery);
    }

    private async void StartSearch(string query)
    {
        _searchCts?.Cancel();
        _searchItems.Clear();
        _searchIndex = -1;
        Viewer.ClearSearch();
        _searchQuery = query.Trim();
        if (_document is null) return;
        ShowSearchPanel();
        if (string.IsNullOrEmpty(_searchQuery))
        {
            SearchStatus.Text = "";
            SearchProgress.Visibility = Visibility.Collapsed;
            return;
        }
        var cts = _searchCts = new CancellationTokenSource();
        var document = _document;
        SearchProgress.Visibility = Visibility.Visible;
        SearchProgress.Maximum = document.PageCount;
        SearchProgress.Value = 0;
        SearchStatus.Text = "Searching…";
        bool matchCase = MatchCaseToggle.IsChecked == true, wholeWords = WholeWordToggle.IsChecked == true;
        int start = Viewer.CurrentPage;
        try
        {
            for (int i = 0; i < document.PageCount; i++)
            {
                var hits = await document.SearchPageAsync(i, _searchQuery, matchCase, wholeWords, cts.Token);
                if (cts.IsCancellationRequested) return;
                if (hits.Count > 0)
                {
                    string label = document.GetPageLabel(i);
                    int first = _searchItems.Count;
                    foreach (var hit in hits) _searchItems.Add(new SearchItem(hit, label));
                    Viewer.AddSearchResults(hits);
                    if (_searchIndex < 0 && i >= start) SelectSearchResult(first);
                }
                SearchProgress.Value = i + 1;
                SearchStatus.Text = ResultText(_searchItems.Count) + "…";
            }
            if (_searchIndex < 0 && _searchItems.Count > 0) SelectSearchResult(0);
            SearchStatus.Text = _searchItems.Count == 0 ? "No results found" : ResultText(_searchItems.Count);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (_searchCts == cts) SearchProgress.Visibility = Visibility.Collapsed;
        }
    }

    private const double SearchSidebarWidth = 280;

    /// <summary>Shows the search field and results in place of the sidebar pages.</summary>
    private void ShowSearchPanel()
    {
        if (!_sidebarOpen) SetSidebarOpen(true, persist: false);
        // Results need more room than the thumbnails.
        SidebarColumn.Width = new GridLength(Math.Max(SidebarWidth(), SearchSidebarWidth));
        SearchPanel.Visibility = Visibility.Visible;
        SidebarSelector.Visibility = Visibility.Collapsed;
        AppTitleBar.IsPaneToggleButtonVisible = false;
        AppTitleBar.IsBackButtonVisible = true;
        ThumbnailList.Visibility = OutlineTree.Visibility = AnnotationsPanel.Visibility = AttachmentList.Visibility = Visibility.Collapsed;
    }

    private static string ResultText(int count) => count == 1 ? "1 result" : $"{count:N0} results";

    private void CancelSearch(bool clearText)
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searchDebounce.Stop();
        _searchItems.Clear();
        _searchIndex = -1;
        _searchQuery = "";
        Viewer.ClearSearch();
        if (clearText) SearchBox.Text = "";
        SearchStatus.Text = "";
        SearchPanel.Visibility = Visibility.Collapsed;
        SidebarSelector.Visibility = Visibility.Visible;
        SearchProgress.Visibility = Visibility.Collapsed;
        AppTitleBar.IsBackButtonVisible = false;
        AppTitleBar.IsPaneToggleButtonVisible = _document is not null;
        if (_sidebarOpen) SidebarColumn.Width = new GridLength(SidebarWidth());
        if (SidebarSelector.SelectedItem is { } tab) ShowSidebarPage(tab);
        if (_document is not null && !AppState.Preferences.SidebarOpen && _sidebarOpen) SetSidebarOpen(false, persist: false);
    }

    private void SelectSearchResult(int index)
    {
        if (index < 0 || index >= _searchItems.Count) return;
        _searchIndex = index;
        SearchResultList.SelectedIndex = index;
        SearchResultList.ScrollIntoView(_searchItems[index]);
        Viewer.ShowSearchHit(_searchItems[index].Hit);
    }

    private void MoveSearch(int direction)
    {
        if (_searchItems.Count == 0) return;
        int next = _searchIndex < 0 ? 0 : (_searchIndex + direction + _searchItems.Count) % _searchItems.Count;
        SelectSearchResult(next);
    }

    private void OnSearchResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchItem item) SelectSearchResult(_searchItems.IndexOf(item));
    }

    private void OnNextResult(object sender, RoutedEventArgs e) => MoveSearch(+1);
    private void OnPreviousResult(object sender, RoutedEventArgs e) => MoveSearch(-1);

    private void OnSearchContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not SearchItem item) return;
        if (args.ItemContainer.ContentTemplateRoot is not Grid grid || grid.Children[0] is not TextBlock text) return;
        text.Inlines.Clear();
        var hit = item.Hit;
        text.Inlines.Add(new Run { Text = (hit.Before.Length > 0 ? "…" : "") + hit.Before });
        text.Inlines.Add(new Run { Text = hit.Match, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Inlines.Add(new Run { Text = hit.After });
    }

    // ================================================================ zoom

    private void OnZoomIn(object sender, RoutedEventArgs e) => Viewer.ZoomIn();
    private void OnZoomOut(object sender, RoutedEventArgs e) => Viewer.ZoomOut();
    private void OnFitPage(object sender, RoutedEventArgs e) => Viewer.SetZoomMode(ZoomMode.FitPage);
    private void OnFitWidth(object sender, RoutedEventArgs e) => Viewer.SetZoomMode(ZoomMode.FitWidth);

    private void OnZoomPreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoom))
            Viewer.SetZoom(zoom);
    }

    // ================================================================ menu commands

    private async void OnOpen(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".pdf");
        var result = await picker.PickSingleFileAsync();
        if (result is not null) await OpenAsync(result.Path);
    }

    private void OnNewWindow(object sender, RoutedEventArgs e) => new MainWindow().Activate();

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task<bool> SaveAsync()
    {
        if (_document is null) return false;
        await Viewer.CommitPendingEditsAsync();
        try
        {
            // Our own write shouldn't trigger a reload. The watcher event can arrive after a long
            // save finishes, so the quiet period runs from the end of the save as well.
            _ignoreWatcherUntil = DateTime.MaxValue;
            try
            {
                await _document.SaveAsync(_document.FilePath);
            }
            finally
            {
                _ignoreWatcherUntil = DateTime.UtcNow.AddSeconds(3);
            }
            UpdateTitle();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Unable to save", ex.Message + " Try saving a copy instead.", InfoBarSeverity.Error);
            return false;
        }
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        await Viewer.CommitPendingEditsAsync();
        if (_document is null) return;
        var picker = new FileSavePicker(AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(_document.FilePath) + (_document.IsModified ? " (annotated)" : " (copy)"),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        var result = await picker.PickSaveFileAsync();
        if (result is null) return;
        try
        {
            await _document.SaveAsync(result.Path);
            ShowMessage("Copy saved", $"Saved to “{Path.GetFileName(result.Path)}”.", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Unable to save", ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        try
        {
            await PrintService.PrintAsync(this, _document, DisplayName(_document), Viewer.IsInverted,
                message => DispatcherQueue.TryEnqueue(() => ShowMessage("Printing problem", message, InfoBarSeverity.Warning)));
        }
        catch (Exception ex)
        {
            ShowMessage("Unable to print", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnToggleContinuous(object sender, RoutedEventArgs e) => Viewer.SetContinuous(ContinuousItem.IsChecked);
    private void OnToggleDual(object sender, RoutedEventArgs e) => Viewer.SetDual(DualItem.IsChecked);
    private void OnToggleOddLeft(object sender, RoutedEventArgs e) => Viewer.SetOddPagesLeft(OddLeftItem.IsChecked);
    private void OnToggleInvert(object sender, RoutedEventArgs e) => Viewer.SetInverted(InvertItem.IsChecked);
    private void OnRotateLeft(object sender, RoutedEventArgs e) => Viewer.Rotate(-90);
    private void OnRotateRight(object sender, RoutedEventArgs e) => Viewer.Rotate(90);
    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);
    private void OnPresent(object sender, RoutedEventArgs e) => SetPresenting(true);
    private async void OnReload(object sender, RoutedEventArgs e) => await ReloadIfAllowedAsync();

    private async Task ReloadIfAllowedAsync()
    {
        if (_document is null || !await ConfirmDiscardChangesAsync()) return;
        await ReloadAsync();
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (_document is not null) ShowInFolder(_document.FilePath);
    }

    private static void ShowInFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private void OnCloseDocument(object sender, RoutedEventArgs e) => CloseDocument();

    private async void OnProperties(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        await Dialogs.ShowPropertiesAsync(Root.XamlRoot, _document);
    }

    private async void OnSettings(object sender, RoutedEventArgs e) => await Dialogs.ShowSettingsAsync(Root.XamlRoot);
    private async void OnShortcuts(object sender, RoutedEventArgs e) => await Dialogs.ShowShortcutsAsync(Root.XamlRoot);
    private async void OnAbout(object sender, RoutedEventArgs e) => await Dialogs.ShowAboutAsync(Root.XamlRoot);

    // ================================================================ full screen & presentation

    private void SetFullscreen(bool value)
    {
        if (_fullscreen == value) return;
        _fullscreen = value;
        // An extended title bar keeps the content 1px below the top for the resize border, which would show
        // as a light line along the top of the screen.
        ExtendsContentIntoTitleBar = !value;
        if (!value) SetTitleBar(AppTitleBar);
        AppWindow.SetPresenter(value ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        AppTitleBar.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        if (value)
        {
            _sidebarBeforeFullscreen = _sidebarOpen;
            SetSidebarOpen(false, persist: false);
            DocumentCard.BorderThickness = new Thickness(0);
            if (!_presenting) ShowHint("Press Esc or F11 to exit full screen");
        }
        else
        {
            SetSidebarOpen(_sidebarBeforeFullscreen, persist: false);
            FullscreenHint.Opacity = 0;
        }
    }

    private void SetPresenting(bool value)
    {
        if (_document is null || _presenting == value) return;
        _presenting = value;
        if (value)
        {
            SetFullscreen(true);
            Viewer.SetPresentation(true);
            DocumentSurface.Background = new SolidColorBrush(Colors.Black);
            ShowHint("Press Esc to end the presentation");
        }
        else
        {
            Viewer.SetPresentation(false);
            // Uncovers the card's own background, which follows the theme.
            DocumentSurface.Background = null;
            SetFullscreen(false);
        }
    }

    // ================================================================ recent documents

    private void RefreshRecent()
    {
        var recent = AppState.Preferences.RememberRecent ? AppState.Recent : [];
        var items = recent.Select(r => new RecentItem(r)).ToList();
        RecentGrid.ItemsSource = items;
        bool any = items.Count > 0;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        RecentHeader.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        RecentGrid.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in items) LoadRecentThumbnail(item);
    }

    private static async void LoadRecentThumbnail(RecentItem item)
    {
        try
        {
            string path = item.Entry.ThumbnailPath;
            if (!File.Exists(path)) return;
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            item.Thumbnail = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
        }
    }

    private async Task SaveRecentThumbnailAsync(PdfDocument document)
    {
        try
        {
            string path = AppState.ThumbnailPathFor(document.FilePath);
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= File.GetLastWriteTimeUtc(document.FilePath)) return;
            var size = document.PageSizes[0];
            const int width = 300;
            double scale = width / size.Width;
            // Cropped for very tall pages; the card only shows the top anyway.
            var region = new RectInt32(0, 0, width, (int)Math.Clamp(Math.Round(size.Height * scale), 1, 2 * width));
            using var buffer = await document.RenderAsync(0, scale, 0, region, false, WorkPriority.Background, CancellationToken.None);
            // The list may have been cleared while the page rendered.
            if (buffer is null || AppState.Find(document.FilePath) is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)buffer.Width, (uint)buffer.Height, 96, 96,
                buffer.Data.AsSpan(0, buffer.Length).ToArray());
            await encoder.FlushAsync();
            var bytes = new byte[file.Size];
            file.Seek(0);
            await file.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, Windows.Storage.Streams.InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or COMException)
        {
        }
    }

    private async void OnRecentClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentItem item) await OpenAsync(item.Path);
    }

    private void OnRecentRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecentItem item } element) return;
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open", Icon = new FontIcon { Glyph = "\uE8E5" } };
        open.Click += async (_, _) => await OpenAsync(item.Path);
        var newWindow = new MenuFlyoutItem { Text = "Open in new window", Icon = new FontIcon { Glyph = "\uE8A7" } };
        newWindow.Click += async (_, _) =>
        {
            var window = new MainWindow();
            window.Activate();
            await window.OpenAsync(item.Path);
        };
        var folder = new MenuFlyoutItem { Text = "Show in folder", Icon = new FontIcon { Glyph = "\uE838" } };
        folder.Click += (_, _) => ShowInFolder(item.Path);
        var remove = new MenuFlyoutItem { Text = "Remove from list", Icon = new FontIcon { Glyph = "\uE711" } };
        remove.Click += (_, _) => AppState.Remove(item.Path);
        menu.Items.Add(open);
        menu.Items.Add(newWindow);
        menu.Items.Add(folder);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        menu.ShowAt(element, e.GetPosition(element));
        e.Handled = true;
    }

    private void OnClearRecent(object sender, RoutedEventArgs e) => AppState.ClearRecent();

    // ================================================================ drag and drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Open";
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var files = items.OfType<Windows.Storage.StorageFile>().Where(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0)
        {
            ShowMessage("Unsupported file", "Only PDF documents can be opened.", InfoBarSeverity.Warning);
            return;
        }
        for (int i = 1; i < files.Count; i++)
        {
            var window = new MainWindow();
            window.Activate();
            _ = window.OpenAsync(files[i].Path);
        }
        await OpenAsync(files[0].Path);
    }

    // ================================================================ keyboard

    private void SetupAccelerators()
    {
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers Shift = VirtualKeyModifiers.Shift;
        const VirtualKeyModifiers None = VirtualKeyModifiers.None;

        Add(VirtualKey.O, Ctrl, () => _ = PickAndOpenAsync(), true);
        Add(VirtualKey.N, Ctrl, () => new MainWindow().Activate(), true);
        Add(VirtualKey.W, Ctrl, CloseDocument, true);
        Add(VirtualKey.S, Ctrl, () => { if (_document?.IsModified == true || Viewer.HasPendingEdits) _ = SaveAsync(); }, true);
        Add(VirtualKey.S, Ctrl | Shift, () => OnSaveAs(this, new RoutedEventArgs()), true);
        Add(VirtualKey.P, Ctrl, () => OnPrint(this, new RoutedEventArgs()), true);
        Add(VirtualKey.F, Ctrl, FocusSearch, true);
        Add(VirtualKey.F3, None, () => MoveSearch(+1), true);
        Add(VirtualKey.F3, Shift, () => MoveSearch(-1), true);
        Add(VirtualKey.G, Ctrl, () => MoveSearch(+1), true);
        Add(VirtualKey.G, Ctrl | Shift, () => MoveSearch(-1), true);
        Add(VirtualKey.Add, Ctrl, Viewer.ZoomIn, true);
        Add((VirtualKey)187, Ctrl, Viewer.ZoomIn, true);
        Add((VirtualKey)187, Ctrl | Shift, Viewer.ZoomIn, true);
        Add(VirtualKey.Subtract, Ctrl, Viewer.ZoomOut, true);
        Add((VirtualKey)189, Ctrl, Viewer.ZoomOut, true);
        Add(VirtualKey.Number0, Ctrl, () => Viewer.SetZoom(1), true);
        Add(VirtualKey.NumberPad0, Ctrl, () => Viewer.SetZoom(1), true);
        Add(VirtualKey.F9, None, () => { if (_document is not null) SetSidebarOpen(!_sidebarOpen); }, true);
        Add(VirtualKey.F11, None, () => { if (_presenting) SetPresenting(false); else SetFullscreen(!_fullscreen); }, true);
        Add(VirtualKey.F5, None, () => SetPresenting(!_presenting), true);
        Add(VirtualKey.Left, Ctrl, () => Viewer.Rotate(-90), false);
        Add(VirtualKey.Right, Ctrl, () => Viewer.Rotate(90), false);
        Add(VirtualKey.I, Ctrl, () => Viewer.SetInverted(!Viewer.IsInverted), false);
        Add(VirtualKey.R, Ctrl, () => _ = ReloadIfAllowedAsync(), true);
        Add(VirtualKey.Enter, VirtualKeyModifiers.Menu, () => OnProperties(this, new RoutedEventArgs()), true);
        Add(VirtualKey.H, Ctrl, () => Viewer.HighlightSelection(), false);
        Add((VirtualKey)191, Ctrl | Shift, () => OnShortcuts(this, new RoutedEventArgs()), true);
        Add(VirtualKey.F1, None, () => OnShortcuts(this, new RoutedEventArgs()), true);
        Add((VirtualKey)188, Ctrl, () => OnSettings(this, new RoutedEventArgs()), true);
        Add(VirtualKey.Escape, None, () =>
        {
            if (_presenting) SetPresenting(false);
            else if (_fullscreen) SetFullscreen(false);
        }, false, () => _presenting || _fullscreen);

        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action, bool inTextBoxes, Func<bool>? canExecute = null)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                if (canExecute?.Invoke() == false) return;
                if (!inTextBoxes && Root.XamlRoot is not null && FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or PasswordBox or AutoSuggestBox) return;
                if (_document is null && !WorksWithoutDocument(key)) return;
                args.Handled = true;
                action();
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }

        // Ctrl+W closes the window when there's no start page to go back to.
        static bool WorksWithoutDocument(VirtualKey key) =>
            key is VirtualKey.O or VirtualKey.N or VirtualKey.F1 or (VirtualKey)191 or (VirtualKey)188
            || key == VirtualKey.W && !AppState.Preferences.RememberRecent;
    }

    private void FocusSearch()
    {
        if (_document is null) return;
        ShowSearchPanel();
        SearchBox.UpdateLayout();
        SearchBox.Focus(FocusState.Keyboard);
        if (SearchBox.FindDescendant<TextBox>() is { } box) box.SelectAll();
    }
}

internal static class VisualTreeExtensions
{
    public static T? FindDescendant<T>(this DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (child.FindDescendant<T>() is { } found) return found;
        }
        return null;
    }

    public static T? FindDescendant<T>(this DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            if (child.FindDescendant<T>(name) is { } found) return found;
        }
        return null;
    }
}
