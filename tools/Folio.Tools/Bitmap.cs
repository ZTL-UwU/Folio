using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Folio.Tools;

/// <summary>An RGBA image with premultiplied alpha, as floats from 0 to 1, for resampling and compositing.</summary>
internal sealed class Bitmap(int width, int height)
{
    private readonly float[] _pixels = new float[width * height * 4];

    public int Width { get; } = width;
    public int Height { get; } = height;

    public static async Task<Bitmap> LoadAsync(string path)
    {
        using var file = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(file.AsRandomAccessStream());
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] bytes = data.DetachPixelData();
        var bitmap = new Bitmap((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        for (int i = 0; i < bytes.Length; i += 4)
        {
            float alpha = bytes[i + 3] / 255f;
            for (int c = 0; c < 3; c++) bitmap._pixels[i + c] = bytes[i + c] / 255f * alpha;
            bitmap._pixels[i + 3] = alpha;
        }
        return bitmap;
    }

    public async Task SavePngAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, await EncodePngAsync());
    }

    public async Task<byte[]> EncodePngAsync()
    {
        var bytes = new byte[_pixels.Length];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            float alpha = _pixels[i + 3];
            for (int c = 0; c < 3; c++) bytes[i + c] = alpha > 0 ? ToByte(_pixels[i + c] / alpha) : (byte)0;
            bytes[i + 3] = ToByte(alpha);
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight, (uint)Width, (uint)Height, 96, 96, bytes);
        await encoder.FlushAsync();
        using var png = new MemoryStream();
        stream.Seek(0);
        await stream.AsStreamForRead().CopyToAsync(png);
        return png.ToArray();
    }

    private static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    /// <summary>A solid color, from straight (not premultiplied) components.</summary>
    public static Bitmap Solid(int width, int height, float r, float g, float b, float a = 1)
    {
        var bitmap = new Bitmap(width, height);
        for (int i = 0; i < bitmap._pixels.Length; i += 4)
        {
            bitmap._pixels[i] = r * a;
            bitmap._pixels[i + 1] = g * a;
            bitmap._pixels[i + 2] = b * a;
            bitmap._pixels[i + 3] = a;
        }
        return bitmap;
    }

    public Bitmap Crop(int x, int y, int width, int height)
    {
        var crop = new Bitmap(width, height);
        for (int row = 0; row < height; row++)
            Array.Copy(_pixels, ((y + row) * Width + x) * 4, crop._pixels, row * width * 4, width * 4);
        return crop;
    }

    /// <summary>Draws <paramref name="image"/> over this one with its top left corner at (x, y).</summary>
    public void Draw(Bitmap image, int x, int y)
    {
        for (int row = 0; row < image.Height; row++)
        {
            for (int col = 0; col < image.Width; col++)
            {
                int s = (row * image.Width + col) * 4, d = ((y + row) * Width + x + col) * 4;
                float cover = 1 - image._pixels[s + 3];
                for (int c = 0; c < 4; c++) _pixels[d + c] = image._pixels[s + c] + _pixels[d + c] * cover;
            }
        }
    }

    /// <summary>Resamples to the given size with a Lanczos (a = 3) filter.</summary>
    public Bitmap Resize(int width, int height)
    {
        // Along rows, then along columns.
        var wide = new Bitmap(width, Height);
        var columns = Weights(Width, width);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < width; x++)
                Sample(_pixels, (y * Width + columns[x].Start) * 4, 4, columns[x].Weights, wide._pixels, (y * width + x) * 4);

        var result = new Bitmap(width, height);
        var rows = Weights(Height, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Sample(wide._pixels, (rows[y].Start * width + x) * 4, width * 4, rows[y].Weights, result._pixels, (y * width + x) * 4);
        return result;
    }

    /// <summary>Sums the pixels from <paramref name="first"/>, <paramref name="step"/> floats apart, by weight.</summary>
    private static void Sample(float[] source, int first, int step, float[] weights, float[] target, int at)
    {
        float r = 0, g = 0, b = 0, a = 0;
        for (int k = 0, i = first; k < weights.Length; k++, i += step)
        {
            float w = weights[k];
            r += source[i] * w;
            g += source[i + 1] * w;
            b += source[i + 2] * w;
            a += source[i + 3] * w;
        }
        // The filter's negative lobes can overshoot at sharp edges.
        a = Math.Clamp(a, 0, 1);
        target[at] = Math.Clamp(r, 0, a);
        target[at + 1] = Math.Clamp(g, 0, a);
        target[at + 2] = Math.Clamp(b, 0, a);
        target[at + 3] = a;
    }

    /// <summary>For each target pixel, the first source pixel it samples and the weights of the ones from there.</summary>
    private static (int Start, float[] Weights)[] Weights(int source, int target)
    {
        double scale = (double)source / target;
        double stretch = Math.Max(scale, 1); // widens the filter when shrinking, so it averages all the pixels it covers
        double support = 3 * stretch;
        var taps = new (int, float[])[target];
        for (int i = 0; i < target; i++)
        {
            double center = (i + 0.5) * scale;
            int start = Math.Max(0, (int)Math.Floor(center - support));
            int end = Math.Min(source, (int)Math.Ceiling(center + support));
            var weights = new double[end - start];
            for (int j = start; j < end; j++) weights[j - start] = Lanczos((j + 0.5 - center) / stretch);
            double sum = weights.Sum();
            taps[i] = (start, weights.Select(w => (float)(w / sum)).ToArray());
        }
        return taps;
    }

    private static double Lanczos(double x) => Math.Abs(x) < 3 ? Sinc(x) * Sinc(x / 3) : 0;

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
}
