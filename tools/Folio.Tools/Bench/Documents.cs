using System.Text;
using static Folio.Tools.Bench.PdfWriter;

namespace Folio.Tools.Bench;

/// <summary>A benchmark document.</summary>
internal sealed record BenchDocument(string Id, string Name, int Pages, string Path);

/// <summary>
/// Generates the benchmark documents into out\bench\documents. They're the same every time; page 1 of each has
/// a pure magenta band across its top fifth, drawn after everything else, which the benchmark watches for.
/// </summary>
internal static class Documents
{
    // Bump when the documents change, so copies made by an earlier version are regenerated.
    private const int Version = 1;

    private const double PageWidth = 612, PageHeight = 792; // US Letter
    private const int HeavyPages = 1000;

    private static readonly string Band = $"q 1 0 1 rg 0 {N(PageHeight * 0.8)} {N(PageWidth)} {N(PageHeight * 0.2)} re f Q\n";

    private static readonly string[] Words =
    [
        "the", "of", "and", "to", "in", "is", "that", "for", "with", "as", "on", "by", "this", "from", "at", "which",
        "page", "document", "viewer", "render", "zoom", "scroll", "memory", "cache", "font", "glyph", "stream", "object",
        "outline", "thumbnail", "search", "selection", "annotation", "layout", "surface", "bitmap", "vector", "frame",
        "window", "index", "map", "contour", "survey", "river", "valley", "ridge", "coast", "season", "measure", "report",
    ];

    public static async Task<List<BenchDocument>> PrepareAsync()
    {
        string folder = Repo.Path("out", "bench", "documents", $"v{Version}");
        Directory.CreateDirectory(folder);
        var documents = new List<BenchDocument>
        {
            new("short", "2 pages of text", 2, Path.Combine(folder, "short.pdf")),
            new("heavy", $"{HeavyPages:N0} pages with a map, photos and charts", HeavyPages, Path.Combine(folder, "heavy.pdf")),
        };
        foreach (var document in documents.Where(d => !File.Exists(d.Path)))
        {
            Console.WriteLine($"Generating {document.Path}...");
            string temp = document.Path + ".tmp";
            if (document.Id == "short") WriteShort(temp);
            else await WriteHeavyAsync(temp);
            File.Move(temp, document.Path, overwrite: true);
        }
        return documents;
    }

    /// <summary>Two pages of text in the standard fonts.</summary>
    private static void WriteShort(string path)
    {
        var random = new Random(1);
        using var pdf = new PdfWriter(path);
        int catalog = pdf.Reserve(), pages = pdf.Reserve();
        int serif = pdf.Add(Font("Times-Roman")), bold = pdf.Add(Font("Helvetica-Bold"));
        var kids = new List<int>();
        for (int page = 1; page <= 2; page++)
        {
            // Page 1's heading sits below the band.
            double top = page == 1 ? PageHeight * 0.8 - 40 : PageHeight - 72;
            var content = new StringBuilder();
            content.Append($"BT /F2 18 Tf 72 {N(top)} Td (Benchmark document, page {page} of 2) Tj ET\n");
            TextBlock(content, random, 72, top - 30, 72, 10, 12);
            if (page == 1) content.Append(Band);
            int stream = pdf.AddStream("", Encoding.Latin1.GetBytes(content.ToString()));
            kids.Add(pdf.Add($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                $"/Resources << /Font << /F1 {serif} 0 R /F2 {bold} 0 R >> >> /Contents {stream} 0 R >>"));
        }
        pdf.Write(pages, $"<< /Type /Pages /Kids [{Refs(kids)}] /Count {kids.Count} >>");
        pdf.Write(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Finish(catalog, pdf.Add("<< /Title (Folio benchmark: 2 pages of text) >>"));
    }

    /// <summary>
    /// A long illustrated document. Page 1 is a topographic map, a 15-megapixel shaded relief image under some
    /// 140,000 contour line segments; the other pages have text and, alternately, an aerial photo or a line chart,
    /// with a chapter outline. Each photo and chart is drawn on many pages, which keeps the file around 8 MB.
    /// </summary>
    private static async Task WriteHeavyAsync(string path)
    {
        const int Photos = 12, Charts = 16, PagesPerNode = 25, PagesPerChapter = 25;
        var random = new Random(1);
        using var pdf = new PdfWriter(path);
        int catalog = pdf.Reserve(), root = pdf.Reserve(), outlines = pdf.Reserve();
        int serif = pdf.Add(Font("Times-Roman")), bold = pdf.Add(Font("Helvetica-Bold")), sans = pdf.Add(Font("Helvetica"));
        string fonts = $"/Font << /F1 {serif} 0 R /F2 {bold} 0 R /F3 {sans} 0 R >>";
        var pages = Enumerable.Range(0, HeavyPages).Select(_ => pdf.Reserve()).ToArray();
        var nodes = Enumerable.Range(0, (HeavyPages + PagesPerNode - 1) / PagesPerNode).Select(_ => pdf.Reserve()).ToArray();
        int Parent(int page) => nodes[page / PagesPerNode];
        string mediaBox = $"/MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}]";

        // Page 1: the map, with the area 6 hills across.
        var terrain = new Terrain(seed: 7);
        double span = 6;
        int relief = pdf.AddStream("/Type /XObject /Subtype /Image /Width 3400 /Height 4400 /ColorSpace /DeviceRGB /BitsPerComponent 8",
            await terrain.ReliefJpegAsync(3400, 4400, 0, 0, span), "DCTDecode");
        var (contours, _) = terrain.Contours(PageWidth, PageHeight, 0, 0, span, cell: 1.5);
        var map = new StringBuilder();
        map.Append($"q {N(PageWidth)} 0 0 {N(PageHeight)} 0 0 cm /Relief Do Q\n");
        map.Append("q 1 J 1 j\n").Append(contours).Append("Q\n");
        map.Append("q 1 1 1 rg 36 36 220 46 re f Q\n");
        map.Append("BT 0.1 0.15 0.2 rg /F2 14 Tf 46 62 Td (Benchmark atlas, sheet 1) Tj /F3 9 Tf 0 -16 Td (Contours every 28 m) Tj ET\n");
        map.Append(Band);
        int mapContent = pdf.AddStream("", Encoding.Latin1.GetBytes(map.ToString()));
        pdf.Write(pages[0], $"<< /Type /Page /Parent {Parent(0)} 0 R {mediaBox} " +
            $"/Resources << {fonts} /XObject << /Relief {relief} 0 R >> >> /Contents {mapContent} 0 R >>");

        var photos = new int[Photos];
        for (int i = 0; i < Photos; i++)
        {
            byte[] jpeg = await new Terrain(seed: 100 + i).ReliefJpegAsync(2400, 1600, i * 10, 0, 2.5);
            photos[i] = pdf.AddStream("/Type /XObject /Subtype /Image /Width 2400 /Height 1600 /ColorSpace /DeviceRGB /BitsPerComponent 8", jpeg, "DCTDecode");
        }
        var charts = Enumerable.Range(0, Charts)
            .Select(_ => pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 504 300] /Resources << {fonts} >>", Chart(random)))
            .ToArray();

        for (int page = 1; page < HeavyPages; page++)
        {
            var content = new StringBuilder();
            int chapter = page / PagesPerChapter + 1, section = page % PagesPerChapter + 1;
            content.Append($"BT /F2 15 Tf 54 734 Td ({chapter}.{section}  {Capitalize(Phrase(random, 4))}) Tj ET\n");
            TextBlock(content, random, 54, 708, 448, 10, 12.5);
            string figure;
            if (page % 2 == 1)
            {
                figure = $"/Photo {photos[page / 2 % Photos]} 0 R";
                content.Append("q 504 0 0 336 54 84 cm /Photo Do Q\n");
            }
            else
            {
                figure = $"/Chart {charts[page / 2 % Charts]} 0 R";
                content.Append("q 1 0 0 1 54 110 cm /Chart Do Q\n");
            }
            content.Append($"BT /F3 9 Tf 300 40 Td ({page + 1}) Tj ET\n");
            int stream = pdf.AddStream("", Encoding.Latin1.GetBytes(content.ToString()));
            pdf.Write(pages[page], $"<< /Type /Page /Parent {Parent(page)} 0 R {mediaBox} " +
                $"/Resources << {fonts} /XObject << {figure} >> >> /Contents {stream} 0 R >>");
        }

        // A two-level page tree, as most PDF writers make for long documents.
        for (int n = 0; n < nodes.Length; n++)
        {
            var kids = pages.Skip(n * PagesPerNode).Take(PagesPerNode).ToList();
            pdf.Write(nodes[n], $"<< /Type /Pages /Parent {root} 0 R /Kids [{Refs(kids)}] /Count {kids.Count} >>");
        }
        pdf.Write(root, $"<< /Type /Pages /Kids [{Refs(nodes)}] /Count {HeavyPages} >>");

        var items = Enumerable.Range(0, HeavyPages / PagesPerChapter).Select(_ => pdf.Reserve()).ToArray();
        for (int i = 0; i < items.Length; i++)
        {
            string siblings = (i > 0 ? $" /Prev {items[i - 1]} 0 R" : "") + (i + 1 < items.Length ? $" /Next {items[i + 1]} 0 R" : "");
            pdf.Write(items[i], $"<< /Title (Chapter {i + 1}: {Capitalize(Phrase(random, 3))}) /Parent {outlines} 0 R{siblings} " +
                $"/Dest [{pages[i * PagesPerChapter]} 0 R /Fit] >>");
        }
        pdf.Write(outlines, $"<< /Type /Outlines /First {items[0]} 0 R /Last {items[^1]} 0 R /Count {items.Length} >>");
        pdf.Write(catalog, $"<< /Type /Catalog /Pages {root} 0 R /Outlines {outlines} 0 R >>");
        pdf.Finish(catalog, pdf.Add($"<< /Title (Folio benchmark: {HeavyPages} pages with a map, photos and charts) >>"));
    }

    /// <summary>A 504 x 300 pt line chart with four series of 1,200 points each.</summary>
    private static byte[] Chart(Random random)
    {
        const double Left = 40, Right = 494, Bottom = 40, Top = 280;
        var chart = new StringBuilder();
        chart.Append("q 0.97 0.98 1 rg 0 0 504 300 re f\n0.82 G 0.4 w\n");
        for (int i = 0; i <= 6; i++) chart.Append($"{N(Left)} {N(Bottom + i * 40)} m {N(Right)} {N(Bottom + i * 40)} l\n");
        for (int i = 0; i <= 10; i++) chart.Append($"{N(Left + i * 45.4)} {N(Bottom)} m {N(Left + i * 45.4)} {N(Top)} l\n");
        chart.Append("S\n0.2 G 0.8 w ").Append($"{N(Left)} {N(Top)} m {N(Left)} {N(Bottom)} l {N(Right)} {N(Bottom)} l S\n");
        string[] colors = ["0.16 0.42 0.78", "0.92 0.5 0.12", "0.1 0.6 0.55", "0.45 0.45 0.45"];
        chart.Append("1 j 1 J 0.9 w\n");
        foreach (var color in colors)
        {
            double value = 160 + random.NextDouble() * 60 - 30, trend = random.NextDouble() * 0.2 - 0.1;
            chart.Append(color).Append(" RG\n");
            for (int i = 0; i < 1200; i++)
            {
                value = Math.Clamp(value + trend + (random.NextDouble() - 0.5) * 6, Bottom + 5, Top - 5);
                chart.Append($"{N(Left + i * (Right - Left) / 1199)} {N(value)} {(i == 0 ? "m" : "l")}\n");
            }
            chart.Append("S\n");
        }
        chart.Append("BT 0.2 g /F3 8 Tf");
        for (int i = 0; i <= 6; i++) chart.Append($" 1 0 0 1 {N(Left - 22)} {N(Bottom + i * 40 - 3)} Tm ({i * 50}) Tj");
        chart.Append($" /F2 10 Tf 1 0 0 1 {N(Left)} 288 Tm ({Capitalize(Phrase(random, 5))}) Tj ET Q\n");
        return Encoding.Latin1.GetBytes(chart.ToString());
    }

    /// <summary>Lines of made-up text from <paramref name="top"/> down to <paramref name="bottom"/>.</summary>
    private static void TextBlock(StringBuilder content, Random random, double x, double top, double bottom, double size, double leading)
    {
        content.Append($"BT /F1 {N(size)} Tf {N(leading)} TL {N(x)} {N(top)} Td\n");
        for (double y = top; y >= bottom; y -= leading) content.Append('(').Append(Phrase(random, 15 + random.Next(4))).Append(") Tj T*\n");
        content.Append("ET\n");
    }

    private static string Phrase(Random random, int words) =>
        string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[random.Next(Words.Length)]));

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static string Font(string name) => $"<< /Type /Font /Subtype /Type1 /BaseFont /{name} /Encoding /WinAnsiEncoding >>";

    private static string Refs(IEnumerable<int> objects) => string.Join(' ', objects.Select(o => $"{o} 0 R"));
}
