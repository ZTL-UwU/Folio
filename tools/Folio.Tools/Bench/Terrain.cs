using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Folio.Tools.Bench;

/// <summary>
/// Made-up landscape for the heavy benchmark document: shaded relief images and contour lines. The colors keep
/// clear of magenta, so nothing but the marker band can match it.
/// </summary>
internal sealed class Terrain(int seed)
{
    /// <summary>Elevation from 0 to 1 at a point, in units of roughly one hill.</summary>
    public double Height(double x, double y)
    {
        double sum = 0, amplitude = 0.5, frequency = 1;
        for (int octave = 0; octave < 7; octave++)
        {
            sum += amplitude * Noise(x * frequency, y * frequency, seed + octave);
            amplitude *= 0.5;
            frequency *= 2.03;
        }
        return Math.Clamp((sum - 0.5) * 2.4 + 0.5, 0, 1);
    }

    private static double Noise(double x, double y, int seed)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double fx = x - xi, fy = y - yi;
        double u = fx * fx * (3 - 2 * fx), v = fy * fy * (3 - 2 * fy);
        double top = Lerp(Lattice(xi, yi, seed), Lattice(xi + 1, yi, seed), u);
        double bottom = Lerp(Lattice(xi, yi + 1, seed), Lattice(xi + 1, yi + 1, seed), u);
        return Lerp(top, bottom, v);
    }

    private static double Lattice(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 982451653);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return h / (double)uint.MaxValue;
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private const double SeaLevel = 0.3;

    // Elevation -> color: water, beach, lowland green, forest, rock, snow.
    private static readonly (double Height, double R, double G, double B)[] Ramp =
    [
        (0.00, 22, 62, 120), (SeaLevel, 70, 130, 180), (SeaLevel + 0.01, 214, 204, 160), (0.38, 120, 168, 92),
        (0.55, 52, 108, 64), (0.72, 128, 108, 84), (0.86, 168, 164, 160), (1.00, 250, 250, 250),
    ];

    /// <summary>
    /// A shaded relief image of the area from (<paramref name="x"/>, <paramref name="y"/>) spanning
    /// <paramref name="span"/> units across, as a JPEG.
    /// </summary>
    public async Task<byte[]> ReliefJpegAsync(int width, int height, double x, double y, double span)
    {
        double step = span / width;
        var heights = new float[(width + 1) * (height + 1)];
        Parallel.For(0, height + 1, row =>
        {
            for (int col = 0; col <= width; col++)
                heights[row * (width + 1) + col] = (float)Height(x + col * step, y + row * step);
        });

        var pixels = new byte[width * height * 4];
        Parallel.For(0, height, row =>
        {
            for (int col = 0; col < width; col++)
            {
                double h = heights[row * (width + 1) + col];
                // Light from the north-west, on slopes exaggerated so hills read at any zoom.
                double dx = (heights[row * (width + 1) + col + 1] - h) / step * 0.12;
                double dy = (heights[(row + 1) * (width + 1) + col] - h) / step * 0.12;
                double shade = h < SeaLevel ? 1 : Math.Clamp(0.82 - 0.9 * (dx + dy) / Math.Sqrt(1 + dx * dx + dy * dy), 0.35, 1.15);
                var (r, g, b) = Color(h);
                int i = (row * width + col) * 4;
                pixels[i] = (byte)Math.Clamp(b * shade, 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(g * shade, 0, 255);
                pixels[i + 2] = (byte)Math.Clamp(r * shade, 0, 255);
                pixels[i + 3] = 255;
            }
        });

        using var stream = new InMemoryRandomAccessStream();
        var settings = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.85f, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, settings);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        using var jpeg = new MemoryStream();
        stream.Seek(0);
        await stream.AsStreamForRead().CopyToAsync(jpeg);
        return jpeg.ToArray();
    }

    private static (double R, double G, double B) Color(double h)
    {
        int i = 1;
        while (i < Ramp.Length - 1 && Ramp[i].Height < h) i++;
        var (h0, r0, g0, b0) = Ramp[i - 1];
        var (h1, r1, g1, b1) = Ramp[i];
        double t = Math.Clamp((h - h0) / (h1 - h0), 0, 1);
        return (Lerp(r0, r1, t), Lerp(g0, g1, t), Lerp(b0, b1, t));
    }

    /// <summary>
    /// Contour lines as PDF path operators for a <paramref name="width"/> x <paramref name="height"/> pt box at the
    /// origin, showing the area from (<paramref name="x"/>, <paramref name="y"/>) spanning <paramref name="span"/>
    /// units across, traced on a grid of <paramref name="cell"/> pt. Returns the content and the segment count.
    /// </summary>
    public (string Content, int Segments) Contours(double width, double height, double x, double y, double span, double cell)
    {
        int cols = (int)(width / cell), rows = (int)(height / cell);
        double scale = span / width;
        var grid = new double[(rows + 1) * (cols + 1)];
        Parallel.For(0, rows + 1, row =>
        {
            for (int col = 0; col <= cols; col++)
                grid[row * (cols + 1) + col] = Height(x + col * cell * scale, y + row * cell * scale);
        });
        // The image has row 0 at the top; PDF's y axis points up.
        double Px(double col) => col * cell;
        double Py(double row) => height - row * cell;

        var content = new StringBuilder();
        int segments = 0;
        Span<(double X, double Y)> points = stackalloc (double, double)[4];
        for (int level = 1; level < 25; level++)
        {
            double value = SeaLevel + level * 0.028;
            bool index = level % 5 == 0;
            content.Append(index ? "0.6 w 0.36 0.24 0.12 RG\n" : "0.3 w 0.45 0.32 0.18 RG\n");
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    // Marching squares: where the level crosses each edge of the cell.
                    double a = grid[row * (cols + 1) + col], b = grid[row * (cols + 1) + col + 1];
                    double c = grid[(row + 1) * (cols + 1) + col + 1], d = grid[(row + 1) * (cols + 1) + col];
                    int n = 0;
                    if ((a < value) != (b < value)) points[n++] = (col + (value - a) / (b - a), row);
                    if ((b < value) != (c < value)) points[n++] = (col + 1, row + (value - b) / (c - b));
                    if ((d < value) != (c < value)) points[n++] = (col + (value - d) / (c - d), row + 1);
                    if ((a < value) != (d < value)) points[n++] = (col, row + (value - a) / (d - a));
                    for (int k = 0; k + 1 < n; k += 2)
                    {
                        content.Append(PdfWriter.N(Px(points[k].X))).Append(' ').Append(PdfWriter.N(Py(points[k].Y))).Append(" m ")
                            .Append(PdfWriter.N(Px(points[k + 1].X))).Append(' ').Append(PdfWriter.N(Py(points[k + 1].Y))).Append(" l\n");
                        segments++;
                    }
                }
            }
            content.Append("S\n");
        }
        return (content.ToString(), segments);
    }
}
