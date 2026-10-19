namespace Recto.Tools;

/// <summary>
/// Builds src/Recto/Assets/AppIcon.png and .ico and the MSIX logos in Assets/Package from the SVGs in
/// tools/Recto.Tools/AppIcon. The icon has three levels of detail, each drawn at 256 x 256 units: full,
/// medium (thicker text lines) for 32-48 px, and small (no text lines) for 24 px and below, where the
/// full artwork would blur into a smudge.
/// </summary>
internal static class IconCommand
{
    private const int RenderSize = 1024;
    private static readonly string[] Variants = ["full", "medium", "small"];
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    public static async Task RunAsync(Options options)
    {
        var art = await RenderAsync();
        Bitmap Icon(int size) => art[size <= 24 ? "small" : size <= 48 ? "medium" : "full"].Resize(size, size);

        string assets = Repo.Path("src", "Recto", "Assets");
        await Icon(256).SavePngAsync(Path.Combine(assets, "AppIcon.png"));
        var frames = IcoSizes.Select(Icon).ToList();
        await WriteIcoAsync(Path.Combine(assets, "AppIcon.ico"), frames);
        await WritePackageLogosAsync(Path.Combine(assets, "Package"), Icon);
        Console.WriteLine($"Wrote AppIcon.png, AppIcon.ico and Package\\*.png in {assets}");

        string preview = Repo.Path("out", "icon-preview.png");
        await WritePreviewAsync(preview, frames);
        Console.WriteLine($"Every .ico size on light and dark: {preview}");
    }

    /// <summary>Renders each variant at <see cref="RenderSize"/> px, from which every size is scaled down.</summary>
    private static async Task<Dictionary<string, Bitmap>> RenderAsync()
    {
        var images = Variants.Select(v =>
        {
            string svg = File.ReadAllText(Repo.Path("tools", "Recto.Tools", "AppIcon", $"AppIcon-{v}.svg"));
            string uri = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
            return $"""<img src="{uri}" width="{RenderSize}" height="{RenderSize}">""";
        });
        string html = $"""<!DOCTYPE html><body style="margin: 0; display: flex">{string.Concat(images)}</body>""";
        string png = Path.Combine(Path.GetTempPath(), $"recto-icon-{Guid.NewGuid():N}.png");
        try
        {
            HtmlRenderer.Render(html, png, RenderSize * Variants.Length, RenderSize);
            var strip = await Bitmap.LoadAsync(png);
            return Variants.Select((v, i) => (v, strip.Crop(i * RenderSize, 0, RenderSize, RenderSize))).ToDictionary();
        }
        finally
        {
            File.Delete(png);
        }
    }

    /// <summary>An .ico with a PNG-compressed image per size, which Windows supports since Vista.</summary>
    private static async Task WriteIcoAsync(string path, List<Bitmap> frames)
    {
        var images = new List<byte[]>();
        foreach (var frame in frames) images.Add(await frame.EncodePngAsync());
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // icon
        writer.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            writer.Write((byte)(frames[i].Width % 256)); // 0 means 256
            writer.Write((byte)(frames[i].Height % 256));
            writer.Write((byte)0); // no palette
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) writer.Write(image);
    }

    /// <summary>The logos Package.appxmanifest refers to, at the sizes and scales Windows asks for.</summary>
    private static async Task WritePackageLogosAsync(string folder, Func<int, Bitmap> icon)
    {
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder, "*.png")) File.Delete(old);

        // Start, the taskbar and File Explorer pick a size by pixels; "unplated" ones go without a colored plate behind them.
        foreach (int size in new[] { 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256 })
        {
            var image = icon(size);
            foreach (var suffix in new[] { "", "_altform-unplated", "_altform-lightunplated" })
                await image.SavePngAsync(Path.Combine(folder, $"Square44x44Logo.targetsize-{size}{suffix}.png"));
        }
        foreach (int scale in new[] { 100, 125, 150, 200, 400 })
        {
            await icon(44 * scale / 100).SavePngAsync(Path.Combine(folder, $"Square44x44Logo.scale-{scale}.png"));
            await icon(50 * scale / 100).SavePngAsync(Path.Combine(folder, $"StoreLogo.scale-{scale}.png"));
        }
        foreach (int scale in new[] { 100, 125, 150, 200 })
        {
            int side = 150 * scale / 100;
            await Centered(side, side, side * 2 / 3).SavePngAsync(Path.Combine(folder, $"Square150x150Logo.scale-{scale}.png"));
            await Centered(620 * scale / 100, 300 * scale / 100, 160 * scale / 100).SavePngAsync(Path.Combine(folder, $"SplashScreen.scale-{scale}.png"));
        }

        Bitmap Centered(int width, int height, int size)
        {
            var canvas = new Bitmap(width, height);
            canvas.Draw(icon(size), (width - size) / 2, (height - size) / 2);
            return canvas;
        }
    }

    /// <summary>Every .ico frame at its actual size, on a light and a dark background.</summary>
    private static async Task WritePreviewAsync(string path, List<Bitmap> frames)
    {
        const int Gap = 12, Row = 256 + 2 * Gap;
        var preview = new Bitmap(frames.Sum(f => f.Width + Gap) + Gap, 2 * Row);
        foreach (var (row, shade) in new[] { (0, 243 / 255f), (1, 32 / 255f) })
        {
            preview.Draw(Bitmap.Solid(preview.Width, Row, shade, shade, shade), 0, row * Row);
            int x = Gap;
            foreach (var frame in frames)
            {
                preview.Draw(frame, x, row * Row + Gap + 256 - frame.Height);
                x += frame.Width + Gap;
            }
        }
        await preview.SavePngAsync(path);
    }
}
