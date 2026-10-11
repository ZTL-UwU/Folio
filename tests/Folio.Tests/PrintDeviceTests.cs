using System.Runtime.InteropServices;
using Folio.Pdf;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;

namespace Folio.Tests;

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

    private async Task<PdfDocument> OpenHighlightedAsync()
    {
        var document = await PdfDocument.OpenAsync(_temp.Write("doc.pdf", TestPdf.Create()));
        // Yellow over the middle of the 200 x 300 point page.
        await document.AddHighlightAsync(0, [new Rect(50, 100, 100, 100)], Color.FromArgb(255, 255, 221, 51));
        return document;
    }

    private static bool IsYellowAtCentre(uint pixel)
    {
        byte b = (byte)pixel, g = (byte)(pixel >> 8), r = (byte)(pixel >> 16);
        return r > 200 && g > 180 && b < 160;
    }

    /// <summary>Draws page 1 at one pixel per point onto a white bitmap and returns the BGRA pixel at its centre.</summary>
    private static Task<uint> DrawAsync(PdfDocument document, bool annotations, bool asImage, bool grayscale)
        => PdfWorker.Run(() => Draw(document, annotations, asImage, grayscale));

    private static unsafe uint Draw(PdfDocument document, bool annotations, bool asImage, bool grayscale)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        var info = new BITMAPINFOHEADER { Size = sizeof(BITMAPINFOHEADER), Width = Width, Height = -Height, Planes = 1, BitCount = 32 };
        IntPtr bitmap = CreateDIBSection(dc, &info, 0, out void* bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(dc, bitmap);
        try
        {
            new Span<uint>(bits, Width * Height).Fill(0xFFFFFFFF);
            var rect = new RectInt32(0, 0, Width, Height);
            document.DrawToDevice(dc, 0, rect, 0, rect, 72, annotations, grayscale, asImage);
            GdiFlush();
            return ((uint*)bits)[Height / 2 * Width + Width / 2];
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
