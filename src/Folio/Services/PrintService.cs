using Folio.Controls;
using Folio.Pdf;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics;
using Windows.Graphics.Printing;

namespace Folio.Services;

/// <summary>Prints through the modern Windows print dialog.</summary>
internal static class PrintService
{
    private static readonly Dictionary<IntPtr, Windows.Foundation.TypedEventHandler<PrintManager, PrintTaskRequestedEventArgs>> Handlers = [];

    /// <summary>
    /// Shows the print dialog. <paramref name="reportProblem"/> is called (on any thread) when
    /// pages couldn't be rendered after the dialog has closed.
    /// </summary>
    public static async Task PrintAsync(Window window, PdfDocument document, string title, bool inverted, Action<string> reportProblem)
    {
        if (document.PageCount == 0) throw new InvalidOperationException("The document has no pages to print.");
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var manager = PrintManagerInterop.GetForWindow(hwnd);
        var printDocument = new PrintDocument();
        var source = printDocument.DocumentSource;
        PrintTaskOptions? options = null;

        printDocument.Paginate += (_, e) =>
        {
            options = e.PrintTaskOptions;
            printDocument.SetPreviewPageCount(document.PageCount, PreviewPageCountType.Final);
        };
        printDocument.GetPreviewPage += async (_, e) =>
        {
            // The dialog waits for every preview page it asks for, so always answer.
            UIElement? page = null;
            try
            {
                page = await BuildPageAsync(document, e.PageNumber - 1, e.PageNumber, options, 96, inverted);
            }
            catch (Exception ex) when (ex is InvalidDataException or ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
            }
            printDocument.SetPreviewPage(e.PageNumber, page ?? BlankPage(options, e.PageNumber));
        };
        printDocument.AddPages += async (_, e) =>
        {
            int failed = 0;
            try
            {
                var pages = SelectPages(e.PrintTaskOptions.CustomPageRanges.Select(r => (r.FirstPageNumber, r.LastPageNumber)), document.PageCount);
                double dpi = pages.Count <= 40 ? 200 : pages.Count <= 150 ? 150 : 110;
                for (int n = 0; n < pages.Count; n++)
                {
                    try
                    {
                        var page = await BuildPageAsync(document, pages[n], n + 1, e.PrintTaskOptions, dpi, inverted: false);
                        if (page is null) failed++;
                        else printDocument.AddPage(page);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                    {
                        failed++;
                    }
                }
            }
            finally
            {
                // Without this the print job never finishes.
                printDocument.AddPagesComplete();
            }
            if (failed > 0) reportProblem(failed == 1 ? "One page couldn't be printed and was left out." : $"{failed} pages couldn't be printed and were left out.");
        };

        if (Handlers.Remove(hwnd, out var previous)) manager.PrintTaskRequested -= previous;

        void OnRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            var task = args.Request.CreatePrintTask(title, request => request.SetSource(source));
            task.Options.DisplayedOptions.Clear();
            task.Options.DisplayedOptions.Add(StandardPrintTaskOptions.Copies);
            task.Options.DisplayedOptions.Add(StandardPrintTaskOptions.CustomPageRanges);
            task.Options.DisplayedOptions.Add(StandardPrintTaskOptions.Orientation);
            task.Options.DisplayedOptions.Add(StandardPrintTaskOptions.MediaSize);
            task.Options.DisplayedOptions.Add(StandardPrintTaskOptions.ColorMode);
            task.Options.DisplayedOptions.Add(StandardPrintTaskOptions.Duplex);
            task.Options.PageRangeOptions.AllowAllPages = true;
            task.Options.PageRangeOptions.AllowCustomSetOfPages = true;
            task.Options.Orientation = document.PageSizes[0].Width > document.PageSizes[0].Height ? PrintOrientation.Landscape : PrintOrientation.Portrait;
        }

        Windows.Foundation.TypedEventHandler<PrintManager, PrintTaskRequestedEventArgs> handler = OnRequested;
        Handlers[hwnd] = handler;
        manager.PrintTaskRequested += handler;
        await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
    }

    /// <summary>Drops the print handler for a window that's closing, so it doesn't keep the document alive.</summary>
    public static void Release(Window window)
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (Handlers.Remove(hwnd, out var handler)) PrintManagerInterop.GetForWindow(hwnd).PrintTaskRequested -= handler;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Zero-based page indexes for the ranges from the print dialog (one-based, inclusive), clamped
    /// to the document. No ranges means every page.
    /// </summary>
    internal static List<int> SelectPages(IEnumerable<(int First, int Last)> ranges, int pageCount)
    {
        var pages = new List<int>();
        bool any = false;
        foreach (var (first, last) in ranges)
        {
            any = true;
            int from = Math.Max(1, first), to = Math.Min(pageCount, last < first ? first : last);
            for (int p = from; p <= to; p++) pages.Add(p - 1);
        }
        return any ? pages : [.. Enumerable.Range(0, pageCount)];
    }

    private static (double Width, double Height, Windows.Foundation.Rect Imageable) Paper(PrintTaskOptions? options, int jobPage)
    {
        var paper = options?.GetPageDescription((uint)jobPage);
        double width = paper?.PageSize.Width ?? 816, height = paper?.PageSize.Height ?? 1056;
        return (width, height, paper?.ImageableRect ?? new Windows.Foundation.Rect(0, 0, width, height));
    }

    private static Grid BlankPage(PrintTaskOptions? options, int jobPage)
    {
        var (width, height, _) = Paper(options, jobPage);
        return new Grid { Width = width, Height = height, Background = new SolidColorBrush(Colors.White) };
    }

    private static async Task<UIElement?> BuildPageAsync(PdfDocument document, int index, int jobPage, PrintTaskOptions? options, double dpi, bool inverted)
    {
        if (index < 0 || index >= document.PageCount) return null;
        var (paperWidth, paperHeight, imageable) = Paper(options, jobPage);

        var size = document.PageSizes[index];
        // Rotate landscape pages onto portrait paper (and vice versa) so they fill the sheet.
        bool rotate = (size.Width > size.Height) != (imageable.Width > imageable.Height);
        double w = rotate ? size.Height : size.Width, h = rotate ? size.Width : size.Height;
        double fit = Math.Min(imageable.Width / (w * 96 / 72), imageable.Height / (h * 96 / 72));
        double scale = dpi / 72 * Math.Min(1, fit);
        var region = new RectInt32(0, 0, Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
        using var buffer = await document.RenderAsync(index, scale, rotate ? 90 : 0, region, inverted, WorkPriority.Visible, CancellationToken.None);
        if (buffer is null) return null;

        var image = new Image
        {
            Source = PageView.ToBitmap(buffer),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var root = new Grid
        {
            Width = paperWidth,
            Height = paperHeight,
            Background = new SolidColorBrush(Colors.White),
        };
        var area = new Grid
        {
            Margin = new Thickness(imageable.X, imageable.Y, Math.Max(0, paperWidth - imageable.Right), Math.Max(0, paperHeight - imageable.Bottom)),
        };
        area.Children.Add(image);
        root.Children.Add(area);
        return root;
    }
}
