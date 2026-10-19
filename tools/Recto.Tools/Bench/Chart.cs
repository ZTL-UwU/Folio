using System.Globalization;
using System.Text;
using static System.Net.WebUtility;

namespace Recto.Tools.Bench;

/// <summary>
/// Draws bench results as the bar chart in the README, one panel under another: time to first page, memory, and the
/// time to a File Explorer thumbnail, each with the documents side by side, and Recto highlighted.
/// </summary>
internal static class Chart
{
    // 36 px padding left and right.
    private const int Width = 1080, PanelWidth = Width - 36 - 36;

    public static void Render(BenchResults results, IReadOnlyList<string> apps, string png)
    {
        var viewers = apps.Where(a => results.Results.Any(r => r.App == a)).ToList();
        var handlers = apps.Where(a => results.Thumbnails?.Any(t => t.App == a) == true).ToList();
        // Room for the longest app name at 13 px, right-aligned against the bars; the same in every panel, so the bars line up.
        int left = Math.Max(70, 20 + 8 * viewers.Concat(handlers).DefaultIfEmpty("").Max(a => a.Length));
        var panels = new List<(string Title, string Svg, int Height)>();

        if (viewers.Count > 0)
        {
            var documents = results.Documents.Where(d => results.Results.Any(r => r.Document == d.Id)).ToList();
            double? Viewer(string app, string document, Func<Summary, double?> metric) =>
                results.Results.FirstOrDefault(r => r.App == app && r.Document == document) is { } s ? metric(s) : null;
            string time = Panel(documents, viewers, (a, d) => Viewer(a, d, s => s.PageMs), "ms", results.Versions, left, out int height);
            panels.Add(("Time to first page <span>· ms, lower is better</span>", time, height));
            string memory = Panel(documents, viewers, (a, d) => Viewer(a, d, s => s.MemoryMB), "MB", results.Versions, left, out height);
            panels.Add(("Memory <span>· MB, lower is better</span>", memory, height));
        }

        if (handlers.Count > 0)
        {
            var documents = results.Documents.Where(d => results.Thumbnails!.Any(t => t.Document == d.Id)).ToList();
            double? Thumbnail(string app, string document) =>
                results.Thumbnails!.FirstOrDefault(t => t.App == app && t.Document == document)?.Ms;
            string thumbnail = Panel(documents, handlers, Thumbnail, "ms", results.Versions, left, out int height);
            panels.Add(($"File Explorer thumbnail <span>· ms at {results.ThumbnailSize} px, lower is better</span>", thumbnail, height));
        }

        string sections = string.Concat(panels.Select(p => $"<section><h2>{p.Title}</h2>{p.Svg}</section>"));
        // 28 px padding top and bottom, and between panels; in each, a 20 px heading, a 6 px gap and the plot.
        int pageHeight = 28 + 28 + 28 * Math.Max(0, panels.Count - 1) + panels.Sum(p => 20 + 6 + p.Height);

        string html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <style>
              * { box-sizing: border-box; margin: 0; }
              html, body { width: {{Width}}px; height: {{pageHeight}}px; background: transparent; }
              /* 12 px at 2x, the same 24 px corner as .github/assets/hero.png. On a wrapper, as on body it would paint the whole page. */
              main { width: 100%; height: 100%; background: #fff; border-radius: 12px; padding: 28px 36px;
                     font-family: "Segoe UI Variable Text", "Segoe UI", sans-serif; color: #1b2a41; }
              h2 { font-size: 15px; line-height: 20px; font-weight: 600; }
              section + section { margin-top: 28px; }
              h2 span { font-weight: 400; color: #74839a; }
              svg { display: block; margin-top: 6px; }
              svg text { font-family: inherit; }
            </style>
            </head>
            <body>
            <main>
            {{sections}}
            </main>
            </body>
            </html>
            """;
        HtmlRenderer.Render(html, png, Width, pageHeight, scale: 2);
    }

    /// <summary>
    /// Bars for each of <paramref name="apps"/>, a column of them for each document side by side, on one axis up to the
    /// largest value so the documents compare. The app names are on the left, once.
    /// </summary>
    private static string Panel(List<DocumentInfo> documents, IReadOnlyList<string> apps, Func<string, string, double?> Value,
        string unit, Dictionary<string, string>? versions, int left, out int height)
    {
        // Room after the bars for the longest value, and between columns.
        const int ValueRoom = 64, ColumnGap = 24, Top = 8, BarHeight = 18, RowHeight = 32, LabelHeight = 24;
        var (axisMax, step) = Axis(documents.SelectMany(d => apps.Select(a => Value(a, d.Id) ?? 0)).DefaultIfEmpty(0).Max());
        double columnWidth = (PanelWidth - left - ColumnGap * (documents.Count - 1)) / (double)Math.Max(1, documents.Count);
        int plotTop = Top + LabelHeight, plotBottom = plotTop + apps.Count * RowHeight;
        height = plotBottom + 24;

        var svg = new StringBuilder();
        var description = new List<string>();
        for (int i = 0; i < apps.Count; i++)
        {
            string app = apps[i];
            double center = plotTop + i * RowHeight + RowHeight / 2.0;
            bool recto = app == "Recto";
            string weight = recto ? "600" : "400", color = recto ? "#1b2a41" : "#4a5a72";
            if (versions?.GetValueOrDefault(app) is { } version)
            {
                // The name nudged up to make room for the version under it, the two centred on the bar together.
                svg.Append($"""<text x="{left - 10}" y="{N(center + 0.5)}" text-anchor="end" font-size="13" font-weight="{weight}" fill="{color}">{HtmlEncode(app)}</text>""");
                svg.Append($"""<text x="{left - 10}" y="{N(center + 11)}" text-anchor="end" font-size="7" fill="#a3afc0">{HtmlEncode(version)}</text>""");
            }
            else
            {
                svg.Append($"""<text x="{left - 10}" y="{N(center + 4.5)}" text-anchor="end" font-size="13" font-weight="{weight}" fill="{color}">{HtmlEncode(app)}</text>""");
            }
        }

        for (int column = 0; column < documents.Count; column++)
        {
            var document = documents[column];
            double start = left + column * (columnWidth + ColumnGap);
            double X(double value) => start + value / axisMax * (columnWidth - ValueRoom);
            svg.Append($"""<text x="{N(start)}" y="{Top + 15}" font-size="12" font-weight="600" fill="#4a5a72">{HtmlEncode(document.Name)}</text>""");
            // Hairline grid beside the bars only, so it never crosses the heading.
            for (double v = 0; v <= axisMax + step / 2; v += step)
            {
                svg.Append($"""<line x1="{N(X(v))}" x2="{N(X(v))}" y1="{plotTop}" y2="{plotBottom}" stroke="{(v == 0 ? "#cfd8e4" : "#e9eef5")}" stroke-width="1" shape-rendering="crispEdges"/>""");
                svg.Append($"""<text x="{N(X(v))}" y="{plotBottom + 17}" text-anchor="middle" font-size="10" fill="#a3afc0">{Format(v)}</text>""");
            }
            var values = new List<string>();
            for (int i = 0; i < apps.Count; i++)
            {
                string app = apps[i];
                double center = plotTop + i * RowHeight + RowHeight / 2.0;
                bool recto = app == "Recto";
                string weight = recto ? "600" : "400", color = recto ? "#1b2a41" : "#4a5a72";
                if (Value(app, document.Id) is { } value)
                {
                    // Rounded at the data end, square at the baseline.
                    double x0 = X(0), x1 = Math.Max(X(value), x0 + 4), r = 4, top = center - BarHeight / 2.0, bottom = center + BarHeight / 2.0;
                    svg.Append($"""<path d="M{N(x0)},{N(top)} H{N(x1 - r)} Q{N(x1)},{N(top)} {N(x1)},{N(top + r)} V{N(bottom - r)} Q{N(x1)},{N(bottom)} {N(x1 - r)},{N(bottom)} H{N(x0)} Z" fill="{(recto ? "#2777da" : "#cdd9e9")}"/>""");
                    svg.Append($"""<text x="{N(x1 + 8)}" y="{N(center + 4.5)}" font-size="12.5" font-weight="{weight}" fill="{color}">{Format(value)} {unit}</text>""");
                    values.Add($"{app} {Format(value)} {unit}");
                }
                else
                {
                    svg.Append($"""<text x="{N(X(0) + 8)}" y="{N(center + 4.5)}" font-size="12.5" font-style="italic" fill="#a3afc0">no result</text>""");
                    values.Add($"{app} no result");
                }
            }
            description.Add($"{document.Name}: {string.Join(", ", values)}");
        }
        string label = HtmlEncode(string.Join("; ", description));
        return $"""<svg width="{PanelWidth}" height="{height}" role="img" aria-label="{label}">{svg}</svg>""";
    }

    /// <summary>An axis end and tick step: about four ticks, each 1, 2, 2.5 or 5 times a power of ten.</summary>
    private static (double Max, double Step) Axis(double max)
    {
        if (max <= 0) return (1, 1);
        double rough = max / 4, magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(s => s >= rough);
        return (Math.Ceiling(max / step) * step, step);
    }

    private static string Format(double value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
