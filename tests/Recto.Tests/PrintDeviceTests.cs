using System.Runtime.InteropServices;
using Recto.Pdf;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace Recto.Tests;

/// <summary>Draws pages onto a GDI memory device context, the way they go to a printer.</summary>
public sealed partial class PrintDeviceTests : IDisposable
{
    private const int Width = 200, Height = 300;
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrawToDevice_DrawsAnnotations(bool asImage)
    {
        using var document = await OpenHighlightedAsync();
        Assert.True(IsYellowAtCentre(await DrawAsync(document, annotations: true, asImage, grayscale: false)));
        Assert.False(IsYellowAtCentre(await DrawAsync(document, annotations: false, asImage, grayscale: false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrawToDevice_PrintsGrayscale(bool asImage)
    {
        using var document = await OpenHighlightedAsync();
        uint pixel = await DrawAsync(document, annotations: true, asImage, grayscale: true);
        byte b = (byte)pixel, g = (byte)(pixel >> 8), r = (byte)(pixel >> 16);
        Assert.True(Math.Abs(r - b) <= 2 && Math.Abs(g - b) <= 2, $"#{r:X2}{g:X2}{b:X2} isn't grey");
        Assert.True(r < 250, "the highlight is missing");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrawToDevice_TurnsLandscapeSheetsTheWayOnPaperSays(bool asImage)
    {
        // A landscape page with yellow along its top, in the top-left quarter of a landscape sheet
        // that prints on portrait paper, drawn the way PrintJob draws it.
        using var document = await PdfDocument.OpenAsync(_temp.Write("wide.pdf", TestPdf.Create(mediaBox: "0 0 300 200")));
        await document.AddHighlightAsync(0, [new Rect(20, 10, 260, 40)], Color.FromArgb(255, 255, 221, 51));
        var sheet = new Recto.Printing.PrintSheet(new Recto.Printing.PaperMetrics(300, 200, new Rect(0, 0, 300, 200)), []);
        var placement = new Recto.Printing.PagePlacement(0, new Rect(0, 0, 150, 100), new Rect(0, 0, 300, 200));
        var paper = new Recto.Printing.PaperMetrics(Width, Height, new Rect(0, 0, Width, Height));
        var (bounds, clip, quarter) = Recto.Printing.PrintLayout.OnPaper(sheet, placement, paper);

        uint[] pixels = await PdfWorker.Run(() => Draw(document, ToPixels(bounds), quarter, ToPixels(clip), annotations: true, asImage, grayscale: false));

        // The quarter lands bottom-left on the paper, 100 x 150, with the page's top along its left
        // edge: yellow there, nothing on its right edge (where a clockwise turn would put it) or above.
        Assert.True(IsYellow(pixels[225 * Width + 15]), "the top of the page isn't along the paper's left edge");
        Assert.False(IsYellow(pixels[225 * Width + 85]));
        Assert.False(IsYellow(pixels[75 * Width + 15]));
    }

    private static RectInt32 ToPixels(Rect r) => new((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);

    private async Task<PdfDocument> OpenHighlightedAsync()
    {
        var document = await PdfDocument.OpenAsync(_temp.Write("doc.pdf", TestPdf.Create()));
        // Yellow over the middle of the 200 x 300 point page.
        await document.AddHighlightAsync(0, [new Rect(50, 100, 100, 100)], Color.FromArgb(255, 255, 221, 51));
        return document;
    }

    private static bool IsYellowAtCentre(uint pixel) => IsYellow(pixel);

    private static bool IsYellow(uint pixel)
    {
        byte b = (byte)pixel, g = (byte)(pixel >> 8), r = (byte)(pixel >> 16);
        return r > 200 && g > 180 && b < 160;
    }

    /// <summary>Draws page 1 over the whole bitmap, at one pixel per point, and returns the BGRA pixel at its centre.</summary>
    private static Task<uint> DrawAsync(PdfDocument document, bool annotations, bool asImage, bool grayscale)
    {
        var rect = new RectInt32(0, 0, Width, Height);
        return PdfWorker.Run(() => Draw(document, rect, 0, rect, annotations, asImage, grayscale)[Height / 2 * Width + Width / 2]);
    }

    /// <summary>Draws page 1 onto a white <see cref="Width"/> x <see cref="Height"/> bitmap and returns its BGRA pixels. Call on the PDFium thread.</summary>
    private static unsafe uint[] Draw(PdfDocument document, RectInt32 bounds, int quarter, RectInt32 clip, bool annotations, bool asImage, bool grayscale)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        var info = new BITMAPINFOHEADER { Size = sizeof(BITMAPINFOHEADER), Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
        IntPtr bitmap = CreateDIBSection(dc, &info, 0, out void* bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(dc, bitmap);
        try
        {
            new Span<uint>(bits, Width * Height).Fill(0xFFFFFFFF);
            document.DrawToDevice(dc, 0, bounds, quarter, clip, 72, annotations, grayscale, asImage);
            GdiFlush();
            return new Span<uint>(bits, Width * Height).ToArray();
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size;
        public int Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [LibraryImport("gdi32")] private static partial IntPtr CreateCompatibleDC(IntPtr dc);
    [LibraryImport("gdi32")] private static unsafe partial IntPtr CreateDIBSection(IntPtr dc, BITMAPINFOHEADER* info, uint usage, out void* bits, IntPtr section, uint offset);
    [LibraryImport("gdi32")] private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [LibraryImport("gdi32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("gdi32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(IntPtr dc);
    [LibraryImport("gdi32")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GdiFlush();
}
