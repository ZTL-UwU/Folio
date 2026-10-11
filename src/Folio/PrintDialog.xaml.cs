using System.ComponentModel;
using Folio.Controls;
using Folio.Pdf;
using Folio.Printing;
using Folio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Foundation;
using Windows.Graphics;

namespace Folio;

/// <summary>
/// Folio's own print dialog: a live preview of the sheets that will come out of the printer, next
/// to the settings that matter for documents. Jobs go straight to the printer driver through GDI
/// (see <see cref="PrintJob"/>), so pages print as vector output.
/// </summary>
public sealed partial class PrintDialog : ContentDialog
{
    private readonly Window _window;
    private readonly PdfDocument _document;
    private readonly string _title;
    private readonly int _currentPage;
    private readonly List<SoftwareBitmapSource> _sources = [];
    private string? _defaultPrinter;
    private PrinterDetails? _printer;
    /// <summary>The printer's settings for the job, including ones only its own dialog can change.</summary>
    private DevMode? _settings;
    private PaperMetrics _paper = PaperMetrics.Letter;
    private List<PrintSheet> _sheets = [];
    private int _sheetIndex;
    private int _printerVersion, _paperVersion;
    /// <summary>Set while controls are filled in from code, so their change events don't each lay out again.</summary>
    private bool _updating;
    private CancellationTokenSource? _previewCts, _printCts;
    private bool _printing, _closeRequested;
    /// <summary>Why there's nothing to print, shown in place of the preview.</summary>
    private string? _problem;

    public PrintDialog(Window window, XamlRoot root, PdfDocument document, string title, int currentPage)
    {
        _window = window;
        _document = document;
        _title = title;
        _currentPage = Math.Clamp(currentPage, 0, document.PageCount - 1);
        XamlRoot = root;
        // Dialogs sit above the window's content, so they don't pick up the theme chosen in Settings by themselves.
        if (root.Content is FrameworkElement content) RequestedTheme = content.RequestedTheme;
        // Controls report their initial selections while they're created, before every field is set.
        _updating = true;
        InitializeComponent();

        // In the order of the enums and indexes the rest of the dialog reads them by.
        int count = document.PageCount;
        PagesBox.ItemsSource = new PrintChoice[]
        {
            new("\uE8C8", "All pages", count == 1 ? "1 page" : $"{count:N0} pages"),
            new("\uE7C3", "Current page", $"Page {_currentPage + 1:N0}"),
            new("\uE9D5", "Custom", "Choose the pages to print"),
        };
        OrientationBox.ItemsSource = new PrintChoice[] { new("\uE8B4", "Automatic"), new("\uE7C3", "Portrait"), new("\uE7C3", "Landscape", angle: 90) };
        ColorBox.ItemsSource = new PrintChoice[] { new("\uE790", "Color"), new("\uE7A1", "Black and white") };
        DuplexBox.ItemsSource = new PrintChoice[]
        {
            new("\uE7C3", "Off"), new("\uE8C8", "Flip on long edge"), new("\uE8C8", "Flip on short edge", angle: 90),
        };
        foreach (var box in new[] { PagesBox, OrientationBox, ColorBox, DuplexBox }) box.SelectedIndex = 0;
        foreach (int n in PrintLayout.PagesPerSheetChoices) PerSheetBox.Items.Add(n.ToString());
        PerSheetBox.SelectedIndex = 0;
        _updating = false;

        Closing += OnClosing;
        KeyDown += OnKeyDown;
        Opened += OnOpened;
        Closed += (_, _) =>
        {
            root.Changed -= OnRootChanged;
            _previewCts?.Cancel();
            ReleaseImages();
        };
        root.Changed += OnRootChanged;
        SizeToWindow();
        Relayout();
    }

    private static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    /// <summary>Whether the user asked for the Windows print dialog instead.</summary>
    public bool UseSystemDialog { get; private set; }

    /// <summary>Pages that couldn't be drawn and were left blank in the job that was sent.</summary>
    public int FailedPages { get; private set; }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // Rather than the first control in the dialog, the preview's sheet buttons.
        PrinterBox.Focus(FocusState.Programmatic);
        List<string> names;
        try
        {
            (names, _defaultPrinter) = await Task.Run(Printers.Enumerate);
        }
        catch (Exception ex) when (ex is Win32Exception or System.Runtime.InteropServices.COMException)
        {
            names = [];
        }
        if (names.Count == 0)
        {
            PrinterBox.PlaceholderText = "No printers";
            PrinterStatus.Text = "Add a printer in Windows Settings, or use the system dialog.";
            return;
        }
        string? remembered = AppState.Preferences.Print.Printer;
        PrinterBox.ItemsSource = names;
        PrinterBox.SelectedItem = names.Contains(remembered!) ? remembered : names.Contains(_defaultPrinter!) ? _defaultPrinter : names[0];
    }

    // ================================================================ sizing

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => SizeToWindow();

    /// <summary>Takes most of the window, leaving a margin around the dialog.</summary>
    private void SizeToWindow()
    {
        var size = XamlRoot?.Size ?? new Size(1200, 820);
        double width = Math.Clamp(size.Width - 96, 320, 1180);
        double height = Math.Clamp(size.Height - 96, 320, 860);
        Root.Width = width;
        Root.Height = height;
        // Too narrow for a useful preview: settings only.
        bool preview = width >= 680;
        PreviewPane.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = preview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SettingsColumn.Width = preview ? new GridLength(348) : new GridLength(1, GridUnitType.Star);
    }

    // ================================================================ printer

    private async void OnPrinterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrinterBox.SelectedItem is not string name) return;
        int version = ++_printerVersion;
        _printer = null;
        PrintButton.IsEnabled = false;
        PropertiesButton.IsEnabled = false;
        PrinterStatus.Text = "Connecting…";
        PrinterStatus.Style = AppStyle("SecondaryCaptionTextBlockStyle");
        PrinterDetails details;
        try
        {
            details = await Task.Run(() => Printers.Load(name));
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            if (version != _printerVersion) return;
            PrinterStatus.Text = $"Unavailable. {ex.Message}";
            return;
        }
        if (version != _printerVersion) return;

        _printer = details;
        _settings = details.Defaults.Clone();
        var prefs = AppState.Preferences.Print;
        bool remembered = prefs.Printer == name;

        _updating = true;
        PaperBox.ItemsSource = details.Papers.Select(p => p.Name).ToList();
        PaperBox.Visibility = details.Papers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        short? paper = remembered && details.Papers.Any(p => p.Id == prefs.PaperSize) ? prefs.PaperSize : details.Defaults.PaperSize;
        SelectPaper(paper);
        ColorBox.Visibility = details.IsColor ? Visibility.Visible : Visibility.Collapsed;
        ColorBox.SelectedIndex = (remembered ? prefs.Grayscale : details.Defaults.Grayscale ?? false) ? 1 : 0;
        DuplexBox.Visibility = details.CanDuplex ? Visibility.Visible : Visibility.Collapsed;
        DuplexBox.SelectedIndex = (int)(remembered ? prefs.TwoSided : details.Defaults.Duplex ?? TwoSided.Off);
        _updating = false;

        var status = new List<string>();
        if (name == _defaultPrinter) status.Add("Default");
        status.Add(details.Status);
        if (details.Location.Length > 0) status.Add(details.Location);
        PrinterStatus.Text = string.Join(" · ", status);
        ToolTipService.SetToolTip(PrinterStatus, PrinterStatus.Text);
        if (details.HasProblem) PrinterStatus.Style = AppStyle("CriticalCaptionTextBlockStyle");
        PropertiesButton.IsEnabled = true;

        await RefreshPaperAsync();
        if (version == _printerVersion) UpdateCanPrint();
    }

    private void SelectPaper(short? id)
    {
        if (_printer is null || _printer.Papers.Count == 0) return;
        int index = id is { } v ? _printer.Papers.ToList().FindIndex(p => p.Id == v) : -1;
        PaperBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private PaperSize? SelectedPaper => _printer is { } p && PaperBox.SelectedIndex >= 0 && PaperBox.SelectedIndex < p.Papers.Count ? p.Papers[PaperBox.SelectedIndex] : null;

    private bool Grayscale => _printer is { IsColor: false } || (ColorBox.Visibility == Visibility.Visible && ColorBox.SelectedIndex == 1);

    private TwoSided TwoSidedChoice => _printer is { CanDuplex: true } ? (TwoSided)Math.Max(0, DuplexBox.SelectedIndex) : TwoSided.Off;

    private int Copies => double.IsNaN(CopiesBox.Value) ? 1 : (int)Math.Clamp(CopiesBox.Value, 1, 999);

    /// <summary>The printer's settings with the choices made here.</summary>
    private DevMode JobSettings(DevMode settings)
    {
        var job = settings.Clone();
        // Sheets are turned by the layout instead (see PrintLayout.ToPaper), and copies are sent as
        // separate sheets, so every printer gets the same pages and the preview shows what comes out.
        job.Landscape = false;
        job.Copies = 1;
        job.Collate = false;
        if (SelectedPaper is { } paper) job.PaperSize = paper.Id;
        if (_printer is { IsColor: true }) job.Grayscale = Grayscale;
        if (_printer is { CanDuplex: true }) job.Duplex = TwoSidedChoice;
        return job;
    }

    /// <summary>Asks the printer for the paper it'll use and where it can print on it.</summary>
    private async Task RefreshPaperAsync()
    {
        if (_printer is not { } printer || _settings is not { } settings) return;
        int version = ++_paperVersion;
        var job = JobSettings(settings);
        PaperMetrics? metrics = null;
        try
        {
            metrics = await Task.Run(() => Printers.QueryPaper(printer.Name, job));
        }
        catch (Exception ex) when (ex is Win32Exception or System.Runtime.InteropServices.COMException)
        {
        }
        if (version != _paperVersion || _printer != printer) return;
        if (metrics is null && SelectedPaper is { } paper)
        {
            // Unknown margins: a quarter inch all round.
            metrics = new PaperMetrics(paper.Width, paper.Height, new Rect(18, 18, Math.Max(1, paper.Width - 36), Math.Max(1, paper.Height - 36)));
        }
        _paper = metrics ?? PaperMetrics.Letter;
        Relayout();
    }

    private async void OnPaperChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updating) await RefreshPaperAsync();
    }

    private async void OnPrinterProperties(object sender, RoutedEventArgs e)
    {
        if (_printer is not { } printer || _settings is not { } settings) return;
        var job = JobSettings(settings);
        job.Copies = (short)Copies;
        job.Collate = CollateBox.IsChecked == true;
        DevMode? result;
        try
        {
            result = Printers.ShowProperties(WinRT.Interop.WindowNative.GetWindowHandle(_window), printer.Name, job);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            ShowError($"The printer's settings couldn't be opened. {ex.Message}");
            return;
        }
        if (result is null || _printer != printer) return;

        // Bring the choices here in line with what was picked there.
        _settings = result;
        _updating = true;
        SelectPaper(result.PaperSize);
        if (result.Landscape == true) OrientationBox.SelectedIndex = 2;
        if (printer.IsColor && result.Grayscale is { } gray) ColorBox.SelectedIndex = gray ? 1 : 0;
        if (printer.CanDuplex && result.Duplex is { } duplex) DuplexBox.SelectedIndex = (int)duplex;
        if (result.Copies is { } copies && copies > 0) CopiesBox.Value = copies;
        if (result.Collate is { } collate) CollateBox.IsChecked = collate;
        CollateBox.IsEnabled = Copies > 1;
        _updating = false;
        await RefreshPaperAsync();
    }

    private void OnSystemDialog(object sender, RoutedEventArgs e)
    {
        if (_printing) return;
        UseSystemDialog = true;
        Hide();
    }

    // ================================================================ settings

    private void OnPagesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        bool custom = PagesBox.SelectedIndex == 2;
        RangeBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        Relayout();
    }

    private void OnPagesDropDownClosed(object sender, object e)
    {
        // Picked "Custom" from the list: the range is typed next. Not on SelectionChanged, which also
        // fires for arrow keys on the closed box, and while the list is open it takes focus back.
        if (PagesBox.SelectedIndex == 2) RangeBox.Focus(FocusState.Programmatic);
    }

    private void OnToggleMore(object sender, RoutedEventArgs e)
    {
        bool open = MoreContent.Visibility != Visibility.Visible;
        MoreContent.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        MoreChevron.Glyph = open ? "\uE70E" : "\uE70D";
    }

    private void OnRangeChanged(object sender, TextChangedEventArgs e) => Relayout();
    private void OnLayoutChanged(object sender, RoutedEventArgs e) => Relayout();
    private void OnAppearanceChanged(object sender, RoutedEventArgs e) => RenderPreview();
    private void OnCountChanged(object sender, RoutedEventArgs e) => UpdateSummary();

    private void OnCopiesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating) return;
        if (double.IsNaN(args.NewValue)) sender.Value = 1;
        CollateBox.IsEnabled = Copies > 1;
        UpdateSummary();
    }

    private void OnScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        ScalePercentBox.Visibility = ScaleBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        Relayout();
    }

    private void OnScalePercentChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating) return;
        if (double.IsNaN(args.NewValue)) sender.Value = 100;
        else Relayout();
    }

    private LayoutOptions Layout => new(
        (SheetOrientation)Math.Max(0, OrientationBox.SelectedIndex),
        (PrintScaling)Math.Max(0, ScaleBox.SelectedIndex),
        (double.IsNaN(ScalePercentBox.Value) ? 100 : ScalePercentBox.Value) / 100,
        PrintLayout.PagesPerSheetChoices[Math.Max(0, PerSheetBox.SelectedIndex)]);

    /// <summary>The pages to print, in order, or an explanation of what's wrong with the range.</summary>
    private bool TryGetPages(out List<int> pages, out string? error)
    {
        error = null;
        switch (PagesBox.SelectedIndex)
        {
            case 1:
                pages = [_currentPage];
                break;
            case 2:
                if (!PrintLayout.TryParseRanges(RangeBox.Text, _document.PageCount, out pages, out error)) return false;
                break;
            default:
                pages = [.. Enumerable.Range(0, _document.PageCount)];
                break;
        }
        var subset = (PageSubset)Math.Max(0, SubsetBox.SelectedIndex);
        pages = PrintLayout.Filter(pages, subset, ReverseBox.IsChecked == true);
        if (pages.Count == 0)
        {
            error = subset == PageSubset.Even ? "There are no even pages to print." : "There are no odd pages to print.";
            return false;
        }
        return true;
    }

    private void Relayout()
    {
        if (_updating || RangeError is null) return;
        if (TryGetPages(out var pages, out string? error))
        {
            RangeError.Visibility = Visibility.Collapsed;
            _problem = null;
            _sheets = PrintLayout.Layout(pages, _document.PageSizes, _paper, Layout);
        }
        else
        {
            // An unfinished range while typing: no error until there's something to complain about.
            bool show = PagesBox.SelectedIndex != 2 || RangeBox.Text.Trim().Length > 0;
            RangeError.Text = error;
            RangeError.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _problem = show ? error : "Enter the pages to print.";
            _sheets = [];
        }
        _sheetIndex = Math.Clamp(_sheetIndex, 0, Math.Max(0, _sheets.Count - 1));
        UpdateSummary();
        UpdateCanPrint();
        RenderPreview();
    }

    private void UpdateSummary()
    {
        if (_updating || Summary is null) return;
        if (_sheets.Count == 0)
        {
            Summary.Text = "Nothing to print";
            return;
        }
        bool twoSided = TwoSidedChoice != TwoSided.Off;
        var sequence = PrintLayout.Sequence(_sheets.Count, Copies, CollateBox.IsChecked == true, twoSided);
        int paper = PrintLayout.PaperCount(sequence, twoSided);
        Summary.Text = paper == 1 ? "1 sheet of paper" : $"{paper:N0} sheets of paper";
    }

    private void UpdateCanPrint() => PrintButton.IsEnabled = !_printing && _printer is not null && _sheets.Count > 0;

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    // ================================================================ preview

    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e) => RenderPreview();

    private void OnPreviousSheet(object sender, RoutedEventArgs e) => MoveSheet(-1);
    private void OnNextSheet(object sender, RoutedEventArgs e) => MoveSheet(+1);

    private void OnPreviewWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(PreviewPane).Properties.MouseWheelDelta;
        if (delta != 0) MoveSheet(delta < 0 ? +1 : -1);
        e.Handled = true;
    }

    private void MoveSheet(int step)
    {
        int index = Math.Clamp(_sheetIndex + step, 0, Math.Max(0, _sheets.Count - 1));
        if (index == _sheetIndex) return;
        _sheetIndex = index;
        RenderPreview();
    }

    /// <summary>
    /// Renders the current sheet. The previous one stays up until the new pages are ready, so
    /// changing a setting doesn't flash an empty sheet.
    /// </summary>
    private async void RenderPreview()
    {
        if (_updating || Sheet is null) return;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();

        PreviousSheetButton.IsEnabled = _sheetIndex > 0;
        NextSheetButton.IsEnabled = _sheetIndex < _sheets.Count - 1;
        SheetPosition.Text = _sheets.Count == 0 ? "" : $"{_sheetIndex + 1} of {_sheets.Count:N0}";
        if (_sheets.Count == 0)
        {
            Sheet.Visibility = Visibility.Collapsed;
            PreviewMessage.Text = _problem ?? "Nothing to print.";
            ReleaseImages();
            return;
        }
        double availableWidth = PreviewHost.ActualWidth, availableHeight = PreviewHost.ActualHeight;
        if (availableWidth <= 0 || availableHeight <= 0) return;

        var sheet = _sheets[_sheetIndex];
        double k = Math.Min(availableWidth / sheet.Paper.Width, availableHeight / sheet.Paper.Height);
        double pixels = k * (XamlRoot?.RasterizationScale ?? 1);
        bool gray = Grayscale, annotations = AnnotationsBox.IsChecked == true;

        async Task<(Rect Visible, SoftwareBitmapSource? Source)> RenderAsync(PagePlacement placement)
        {
            var visible = placement.Bounds;
            visible.Intersect(placement.Clip);
            if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return (visible, null);
            var size = _document.PageSizes[placement.Page];
            double scale = pixels * placement.Bounds.Width / size.Width;
            var region = new RectInt32(
                (int)Math.Round((visible.X - placement.Bounds.X) * pixels), (int)Math.Round((visible.Y - placement.Bounds.Y) * pixels),
                Math.Max(1, (int)Math.Round(visible.Width * pixels)), Math.Max(1, (int)Math.Round(visible.Height * pixels)));
            try
            {
                using var buffer = await _document.RenderAsync(placement.Page, scale, 0, region, false, WorkPriority.Visible, cts.Token, printing: true, annotations);
                if (buffer is null || cts.IsCancellationRequested) return (visible, null);
                if (gray) await Task.Run(() => MakeGray(buffer));
                return (visible, await PageView.ToSourceAsync(buffer));
            }
            catch (InvalidDataException)
            {
                return (visible, null);
            }
        }

        (Rect Visible, SoftwareBitmapSource? Source)[] pages;
        try
        {
            pages = await Task.WhenAll(sheet.Pages.Select(RenderAsync));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        if (cts.IsCancellationRequested)
        {
            foreach (var (_, source) in pages) source?.Dispose();
            return;
        }

        ReleaseImages();
        SheetCanvas.Width = sheet.Paper.Width * k;
        SheetCanvas.Height = sheet.Paper.Height * k;
        Sheet.Visibility = Visibility.Visible;
        PreviewMessage.Text = "";
        foreach (var (visible, source) in pages)
        {
            if (source is null) continue;
            _sources.Add(source);
            var image = new Image { Source = source, Stretch = Stretch.Fill, Width = visible.Width * k, Height = visible.Height * k };
            Canvas.SetLeft(image, visible.X * k);
            Canvas.SetTop(image, visible.Y * k);
            SheetCanvas.Children.Add(image);
        }
    }

    private static unsafe void MakeGray(PixelBuffer buffer) => PdfDocument.ToGray(buffer.Pointer, buffer.Length);

    private void ReleaseImages()
    {
        SheetCanvas.Children.Clear();
        foreach (var source in _sources) source.Dispose();
        _sources.Clear();
    }

    // ================================================================ printing

    /// <summary>Enter that no control used, such as after typing pages, prints: what the dialog's own default button did.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !PrintButton.IsEnabled) return;
        e.Handled = true;
        OnPrintClick(PrintButton, e);
    }

    /// <summary>Hides the dialog, or while a job is being sent, cancels it first (see <see cref="OnClosing"/>).</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e) => Hide();

    private async void OnPrintClick(object sender, RoutedEventArgs e)
    {
        // The dialog stays up while the job is sent, with its progress above the buttons.
        if (_printing || _printer is not { } printer || _settings is not { } settings) return;
        if (!TryGetPages(out var pages, out _))
        {
            RangeBox.Focus(FocusState.Programmatic);
            return;
        }
        ErrorBar.IsOpen = false;

        string? output = null;
        if (printer.OutputExtension is { } extension)
        {
            // Printers that make a file would ask for its name themselves, in a dialog that can open behind this window.
            output = await PickOutputAsync(extension);
            if (output is null) return;
        }

        var job = JobSettings(settings);
        bool twoSided = TwoSidedChoice != TwoSided.Off;
        SetPrinting(true);
        ProgressText.Text = "Preparing…";
        PrintProgress.Value = 0;
        var cts = _printCts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (!_printing) return;
            PrintProgress.Value = (double)p.Done / p.Total;
            ProgressText.Text = $"Sending page {p.Done:N0} of {p.Total:N0}…";
        });
        try
        {
            job = await Task.Run(() => Printers.Merge(printer.Name, job));
            var request = new PrintRequest(printer.Name, job, _title, output, pages, Layout, Copies, CollateBox.IsChecked == true, twoSided,
                AnnotationsBox.IsChecked == true, Grayscale, ImageBox.IsChecked == true);
            FailedPages = await PrintJob.RunAsync(_document, request, progress, cts.Token);
            Remember(printer);
            SetPrinting(false);
            Hide();
        }
        catch (OperationCanceledException)
        {
            SetPrinting(false);
            if (_closeRequested) Hide();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ObjectDisposedException or Win32Exception
            or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            SetPrinting(false);
            if (_closeRequested) Hide();
            else ShowError(ex is ObjectDisposedException ? "The document was closed." : ex.Message);
        }
    }

    private async Task<string?> PickOutputAsync(string extension)
    {
        var picker = new FileSavePicker(_window.AppWindow.Id)
        {
            SuggestedFileName = Path.GetFileNameWithoutExtension(_title),
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeChoices.Add(extension == ".pdf" ? "PDF document" : "XPS document", [extension]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    private void SetPrinting(bool printing)
    {
        _printing = printing;
        SettingsScroller.IsEnabled = !printing;
        ProgressPanel.Visibility = printing ? Visibility.Visible : Visibility.Collapsed;
        UpdateCanPrint();
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_printing) return;
        // Cancel (or Esc) while the job is being sent stops it; the dialog closes once it's withdrawn.
        args.Cancel = true;
        _closeRequested = true;
        ProgressText.Text = "Cancelling…";
        _printCts?.Cancel();
    }

    private void Remember(PrinterDetails printer)
    {
        var prefs = AppState.Preferences.Print;
        prefs.Printer = printer.Name;
        prefs.PaperSize = SelectedPaper?.Id;
        if (printer.IsColor) prefs.Grayscale = Grayscale;
        if (printer.CanDuplex) prefs.TwoSided = TwoSidedChoice;
        AppState.Save();
    }
}

/// <summary>An option in the print dialog's main lists: an icon, its name and maybe a second line.</summary>
public sealed class PrintChoice(string glyph, string text, string? detail = null, double angle = 0)
{
    public string Glyph => glyph;
    /// <summary>Turns the icon, for the sideways version of one.</summary>
    public double Angle => angle;
    public string Text => text;
    public string Detail => detail ?? "";
    public bool HasDetail => detail is not null;

    /// <summary>What screen readers announce for the option.</summary>
    public override string ToString() => text;
}
