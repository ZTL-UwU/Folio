using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Folio.Pdf;
using Folio.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;

namespace Folio.Controls;

/// <summary>
/// Displays a single page: a full-page bitmap, optional high resolution tiles for the visible
/// region when zoomed in, and an overlay for selection / search highlights.
/// </summary>
internal sealed class PageView : Canvas
{
    private const int TileSize = 512;
    private const double MaxFullPagePixels = 6_000_000;
    private const double LowResPixels = 1_500_000;
    private const double MaxBitmapSide = 8192;
    /// <summary>Most tiles rendered by one PDFium pass: 64 tiles of 512 px is 16 MP, about two 4K screens.</summary>
    private const int MaxTilesPerBatch = 64;

    private readonly PdfDocument _document;
    private readonly Image _base = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Canvas _tiles = new() { IsHitTestVisible = false };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _frameHost = new() { IsHitTestVisible = false };
    private readonly Border _paper = new() { IsHitTestVisible = false };
    private readonly Dictionary<(int Col, int Row), Image> _tileImages = [];
    private readonly Dictionary<(int Col, int Row), TileBatch> _pendingTiles = [];
    private CancellationTokenSource? _baseCts;
    private double _baseScale;
    private double _tileScale;
    private int _renderedRotation = -1;
    private bool _renderedInverted;
    private int _version;

    public PageView(PdfDocument document, int pageIndex, bool framed)
    {
        _document = document;
        PageIndex = pageIndex;
        _paper.Background = new SolidColorBrush(Colors.White);
        if (framed) Children.Add(_frameHost);
        Children.Add(_paper);
        Children.Add(_base);
        Children.Add(_tiles);
        Children.Add(_overlay);
        if (framed) AttachFrame();
    }

    public int PageIndex { get; }
    /// <summary>Page size in display points, before user rotation.</summary>
    public Size PagePoints => _document.PageSizes[PageIndex];
    public int PageRotation { get; private set; }
    /// <summary>Layout DIPs per point.</summary>
    public double LayoutScale { get; private set; } = 1;

    // Windows 11 card look: rounded corners and an outside hairline in CardStrokeColorDefault, no
    // shadow. Sizes are in screen DIPs and are rescaled to cancel out zoom.
    private const double FrameRadius = 4;
    private static readonly Windows.UI.Color StrokeLight = Windows.UI.Color.FromArgb(0x0F, 0, 0, 0);
    private static readonly Windows.UI.Color StrokeDark = Windows.UI.Color.FromArgb(0x19, 0, 0, 0);

    private ShapeVisual? _outline;
    private CompositionRoundedRectangleGeometry? _outlineGeometry, _clipGeometry;
    private CompositionColorBrush? _outlineBrush;
    /// <summary>Device pixels per layout DIP the frame was last sized for; 0 before the first render.</summary>
    private double _framePixelScale;

    private void AttachFrame()
    {
        var compositor = ElementCompositionPreview.GetElementVisual(_frameHost).Compositor;
        // A rounded rect a hairline larger than the page, behind it.
        _outlineGeometry = compositor.CreateRoundedRectangleGeometry();
        _outlineBrush = compositor.CreateColorBrush();
        var shape = compositor.CreateSpriteShape(_outlineGeometry);
        shape.FillBrush = _outlineBrush;
        _outline = compositor.CreateShapeVisual();
        _outline.Shapes.Add(shape);
        ElementCompositionPreview.SetElementChildVisual(_frameHost, _outline);

        _clipGeometry = compositor.CreateRoundedRectangleGeometry();
        foreach (var element in new UIElement[] { _paper, _base, _tiles, _overlay })
            ElementCompositionPreview.GetElementVisual(element).Clip = compositor.CreateGeometricClip(_clipGeometry);
        ActualThemeChanged += (_, _) => UpdateFrame();
    }

    private void UpdateFrame()
    {
        if (_outline is null || _outlineGeometry is null || _outlineBrush is null || _clipGeometry is null) return;
        if (double.IsNaN(Width) || double.IsNaN(Height)) return;
        double raster = XamlRoot?.RasterizationScale ?? 1;
        double pixel = _framePixelScale > 0 ? _framePixelScale : raster;
        // Whole device pixels, so the clip lines up with the pixel-snapped bitmap instead of cutting into its last row.
        var size = new Vector2((float)(Math.Round(Width * pixel) / pixel), (float)(Math.Round(Height * pixel) / pixel));
        float hairline = (float)(Math.Max(1, Math.Round(raster)) / pixel);
        bool rounded = AppState.Preferences.RoundedPageCorners;
        float radius = rounded ? (float)(FrameRadius * raster / pixel) : 0;
        _clipGeometry.Size = size;
        _clipGeometry.CornerRadius = new Vector2(radius);
        _outline.Offset = new Vector3(-hairline, -hairline, 0);
        _outline.Size = size + new Vector2(2 * hairline);
        _outlineGeometry.Size = _outline.Size;
        _outlineGeometry.CornerRadius = new Vector2(rounded ? radius + hairline : 0);
        _outlineBrush.Color = ActualTheme == ElementTheme.Dark ? StrokeDark : StrokeLight;
    }

    /// <summary>Reapplies the frame after the page corner preference changes.</summary>
    public void RefreshFrame() => UpdateFrame();

    public void SetPaperColor(Windows.UI.Color color) => ((SolidColorBrush)_paper.Background).Color = color;

    public void Arrange(Rect rect, double layoutScale, int rotation)
    {
        SetLeft(this, rect.X);
        SetTop(this, rect.Y);
        Width = rect.Width;
        Height = rect.Height;
        _paper.Width = rect.Width;
        _paper.Height = rect.Height;
        UpdateFrame();
        if (rotation != PageRotation || _base.Source is null)
        {
            _base.Width = rect.Width;
            _base.Height = rect.Height;
        }
        if (rotation != PageRotation)
        {
            // Old bitmaps have the wrong orientation; drop them.
            _base.Source = null;
            ClearTiles();
            _baseScale = 0;
        }
        LayoutScale = layoutScale;
        PageRotation = rotation;
    }

    /// <summary>Converts a rect in display points to page-local layout DIPs, applying rotation.</summary>
    public Rect ToView(Rect r)
    {
        var p1 = ToView(new Point(r.Left, r.Top));
        var p2 = ToView(new Point(r.Right, r.Bottom));
        return new Rect(p1, p2);
    }

    public Point ToView(Point p)
    {
        double w = PagePoints.Width, h = PagePoints.Height;
        var q = PageRotation switch
        {
            90 => new Point(h - p.Y, p.X),
            180 => new Point(w - p.X, h - p.Y),
            270 => new Point(p.Y, w - p.X),
            _ => p,
        };
        return new Point(q.X * LayoutScale, q.Y * LayoutScale);
    }

    /// <summary>Converts a page-local layout DIP point to display points.</summary>
    public Point FromView(Point v)
    {
        double w = PagePoints.Width, h = PagePoints.Height;
        double x = v.X / LayoutScale, y = v.Y / LayoutScale;
        return PageRotation switch
        {
            90 => new Point(y, h - x),
            180 => new Point(w - x, h - y),
            270 => new Point(w - y, x),
            _ => new Point(x, y),
        };
    }

    /// <summary>A rectangle of tiles rendered in one pass: every pass walks the whole page, so fewer passes are much cheaper.</summary>
    private sealed class TileBatch(int c0, int r0, int c1, int r1)
    {
        public readonly CancellationTokenSource Cts = new();
        public int C0 { get; } = c0;
        public int R0 { get; } = r0;
        public int C1 { get; } = c1;
        public int R1 { get; } = r1;

        public IEnumerable<(int Col, int Row)> Keys
        {
            get
            {
                for (int r = R0; r <= R1; r++)
                    for (int c = C0; c <= C1; c++) yield return (c, r);
            }
        }

        public bool Overlaps(int c0, int r0, int c1, int r1) => C0 <= c1 && C1 >= c0 && R0 <= r1 && R1 >= r0;
    }

    public void Invalidate()
    {
        _version++;
        _baseScale = 0;
        CancelPendingTiles();
        _tileScale = 0;
    }

    private void CancelPendingTiles()
    {
        foreach (var batch in _pendingTiles.Values) batch.Cts.Cancel();
        _pendingTiles.Clear();
    }

    /// <summary>
    /// Ensures the bitmaps match the current zoom. <paramref name="pixelScale"/> is device pixels per
    /// layout DIP; <paramref name="visible"/> is the visible part of the page in page-local layout DIPs.
    /// </summary>
    public void Render(double pixelScale, Rect visible, bool inverted, bool isVisible)
    {
        if (Math.Abs(pixelScale - _framePixelScale) > 1e-6)
        {
            _framePixelScale = pixelScale;
            UpdateFrame();
        }
        double pxPerPoint = LayoutScale * pixelScale;
        double pw = Width * pixelScale, ph = Height * pixelScale;
        double area = pw * ph, side = Math.Max(pw, ph);
        // Very long, thin pages stay under the pixel budget but can exceed the GPU's texture size.
        bool tiled = area > MaxFullPagePixels || side > MaxBitmapSide;
        double baseScale = tiled ? pxPerPoint * Math.Min(Math.Sqrt(LowResPixels / area), MaxBitmapSide / side) : pxPerPoint;

        if (inverted != _renderedInverted || PageRotation != _renderedRotation)
        {
            Invalidate();
        }

        if (_baseScale <= 0 || Math.Abs(baseScale - _baseScale) / baseScale > 0.02)
        {
            RenderBase(baseScale, inverted, isVisible);
        }

        if (!tiled)
        {
            ClearTiles();
            return;
        }
        if (Math.Abs(_tileScale - pxPerPoint) / pxPerPoint > 0.001)
        {
            ClearTiles();
            _tileScale = pxPerPoint;
        }
        if (!isVisible || visible.IsEmpty) return;

        int maxW = (int)Math.Ceiling(pw), maxH = (int)Math.Ceiling(ph);
        int c0 = Math.Max(0, (int)(visible.Left * pixelScale) / TileSize);
        int r0 = Math.Max(0, (int)(visible.Top * pixelScale) / TileSize);
        int c1 = Math.Min((maxW - 1) / TileSize, (int)(visible.Right * pixelScale) / TileSize);
        int r1 = Math.Min((maxH - 1) / TileSize, (int)(visible.Bottom * pixelScale) / TileSize);

        foreach (var key in _tileImages.Keys.ToList())
        {
            if (key.Col < c0 - 1 || key.Col > c1 + 1 || key.Row < r0 - 1 || key.Row > r1 + 1)
            {
                _tiles.Children.Remove(_tileImages[key]);
                _tileImages.Remove(key);
            }
        }
        // Batches still partly in view finish; ones that scrolled away entirely stop.
        foreach (var batch in _pendingTiles.Values.Distinct().ToList())
        {
            if (batch.Overlaps(c0, r0, c1, r1)) continue;
            batch.Cts.Cancel();
            foreach (var key in batch.Keys) _pendingTiles.Remove(key);
        }

        foreach (var (bc0, br0, bc1, br1) in MissingTileRects(c0, r0, c1, r1))
        {
            var batch = new TileBatch(bc0, br0, bc1, br1);
            foreach (var key in batch.Keys) _pendingTiles[key] = batch;
            RenderTiles(batch, pxPerPoint, pixelScale, inverted, maxW, maxH);
        }
    }

    private bool IsMissing(int c, int r) => !_tileImages.ContainsKey((c, r)) && !_pendingTiles.ContainsKey((c, r));

    /// <summary>
    /// Covers the tiles in [c0, c1] x [r0, r1] that are neither shown nor pending with few
    /// rectangles: runs of missing tiles in a row, extended down while the rows below have the same run.
    /// </summary>
    private List<(int C0, int R0, int C1, int R1)> MissingTileRects(int c0, int r0, int c1, int r1)
    {
        var done = new List<(int, int, int, int)>();
        // Runs still growing downwards: (first column, last column) -> top row.
        var open = new Dictionary<(int C0, int C1), int>();
        for (int r = r0; r <= r1 + 1; r++)
        {
            var next = new Dictionary<(int C0, int C1), int>();
            for (int c = c0; r <= r1 && c <= c1; c++)
            {
                if (!IsMissing(c, r)) continue;
                int start = c;
                while (c < c1 && IsMissing(c + 1, r)) c++;
                var run = (start, c);
                if (open.Remove(run, out int top))
                {
                    if ((r - top + 1) * (c - start + 1) <= MaxTilesPerBatch)
                    {
                        next[run] = top;
                        continue;
                    }
                    done.Add((start, top, c, r - 1));
                }
                next[run] = r;
            }
            foreach (var ((oc0, oc1), top) in open) done.Add((oc0, top, oc1, r - 1));
            open = next;
        }
        return done;
    }

    private async void RenderBase(double scale, bool inverted, bool isVisible)
    {
        _baseCts?.Cancel();
        var cts = _baseCts = new CancellationTokenSource();
        _baseScale = scale;
        _renderedInverted = inverted;
        _renderedRotation = PageRotation;
        int version = _version;
        var size = RotatedPoints();
        var region = new RectInt32(0, 0, Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
        try
        {
            using var buffer = await _document.RenderAsync(PageIndex, scale, PageRotation, region, inverted,
                isVisible ? WorkPriority.Visible : WorkPriority.Normal, cts.Token);
            if (buffer is null || cts.IsCancellationRequested || version != _version) return;
            // Size the image to its exact pixel dimensions so it maps 1:1 onto the screen.
            _base.Width = buffer.Width * LayoutScale / scale;
            _base.Height = buffer.Height * LayoutScale / scale;
            _base.Source = ToBitmap(buffer);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidDataException)
        {
        }
    }

    private async void RenderTiles(TileBatch batch, double pxPerPoint, double pixelScale, bool inverted, int maxW, int maxH)
    {
        int version = _version;
        int x0 = batch.C0 * TileSize, y0 = batch.R0 * TileSize;
        var region = new RectInt32(x0, y0, Math.Min((batch.C1 + 1) * TileSize, maxW) - x0, Math.Min((batch.R1 + 1) * TileSize, maxH) - y0);
        try
        {
            using var buffer = await _document.RenderAsync(PageIndex, pxPerPoint, PageRotation, region, inverted, WorkPriority.Visible, batch.Cts.Token);
            foreach (var key in batch.Keys)
            {
                if (_pendingTiles.TryGetValue(key, out var current) && current == batch) _pendingTiles.Remove(key);
            }
            if (buffer is null || batch.Cts.IsCancellationRequested || version != _version || Math.Abs(_tileScale - pxPerPoint) > 1e-9) return;
            // Cut the pass into tiles, which are what's kept and dropped as the view moves.
            foreach (var key in batch.Keys)
            {
                var tile = new RectInt32(key.Col * TileSize, key.Row * TileSize, Math.Min(TileSize, maxW - key.Col * TileSize), Math.Min(TileSize, maxH - key.Row * TileSize));
                var image = new Image
                {
                    Source = ToBitmap(buffer, tile.X - region.X, tile.Y - region.Y, tile.Width, tile.Height),
                    Stretch = Stretch.Fill,
                    Width = tile.Width / pixelScale,
                    Height = tile.Height / pixelScale,
                };
                SetLeft(image, tile.X / pixelScale);
                SetTop(image, tile.Y / pixelScale);
                if (_tileImages.Remove(key, out var old)) _tiles.Children.Remove(old);
                _tileImages[key] = image;
                _tiles.Children.Add(image);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidDataException)
        {
        }
    }

    /// <summary>Offsets the page by a sub-pixel amount so its bitmaps land on whole device pixels.</summary>
    public void SnapToPixels(Point devicePosition, double pixelScale)
    {
        double fx = devicePosition.X - Math.Round(devicePosition.X);
        double fy = devicePosition.Y - Math.Round(devicePosition.Y);
        if (RenderTransform is not TranslateTransform t) RenderTransform = t = new TranslateTransform();
        t.X = -fx / pixelScale;
        t.Y = -fy / pixelScale;
    }

    private Size RotatedPoints() => PageRotation is 90 or 270 ? new Size(PagePoints.Height, PagePoints.Width) : PagePoints;

    private void ClearTiles()
    {
        CancelPendingTiles();
        _tileImages.Clear();
        _tiles.Children.Clear();
    }

    public void Release()
    {
        _baseCts?.Cancel();
        ClearTiles();
        _base.Source = null;
    }

    internal static WriteableBitmap ToBitmap(PixelBuffer buffer) => ToBitmap(buffer, 0, 0, buffer.Width, buffer.Height);

    /// <summary>Copies the <paramref name="width"/> x <paramref name="height"/> pixels at (<paramref name="x"/>, <paramref name="y"/>) into a bitmap.</summary>
    internal static WriteableBitmap ToBitmap(PixelBuffer buffer, int x, int y, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height);
        int stride = buffer.Width * 4;
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            if (x == 0 && width == buffer.Width)
                stream.Write(buffer.Data, y * stride, height * stride);
            else
                for (int row = 0; row < height; row++) stream.Write(buffer.Data, (y + row) * stride + x * 4, width * 4);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    // ---------------------------------------------------------------- overlay

    /// <summary>
    /// Shows highlight rectangles (display points). Each brush becomes one Path whose geometry is
    /// parsed from path markup in a single call, instead of a Rectangle element per highlight:
    /// pages with hundreds of search hits used to take tens of milliseconds to update.
    /// </summary>
    public void SetOverlay(IEnumerable<(Rect Rect, Brush Fill)> shapes)
    {
        var paths = new Dictionary<Brush, StringBuilder>();
        foreach (var (rect, fill) in shapes)
        {
            var view = ToView(rect);
            if (view.IsEmpty || view.Width <= 0 || view.Height <= 0) continue;
            // F1: nonzero fill, so overlapping rectangles don't cut holes in each other.
            if (!paths.TryGetValue(fill, out var markup)) paths[fill] = markup = new StringBuilder("F1");
            AppendRoundedRect(markup, view, 1.5);
        }
        _overlay.Children.Clear();
        foreach (var (fill, markup) in paths)
        {
            var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), markup.ToString());
            _overlay.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Fill = fill });
        }
    }

    private static void AppendRoundedRect(StringBuilder sb, Rect r, double radius)
    {
        double k = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        sb.Append(CultureInfo.InvariantCulture,
            $" M{l + k:0.###},{t:0.###} H{rt - k:0.###} A{k:0.###},{k:0.###} 0 0 1 {rt:0.###},{t + k:0.###} V{b - k:0.###} A{k:0.###},{k:0.###} 0 0 1 {rt - k:0.###},{b:0.###} H{l + k:0.###} A{k:0.###},{k:0.###} 0 0 1 {l:0.###},{b - k:0.###} V{t + k:0.###} A{k:0.###},{k:0.###} 0 0 1 {l + k:0.###},{t:0.###} Z");
    }
}
