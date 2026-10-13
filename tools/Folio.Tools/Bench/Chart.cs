using System.Globalization;
using System.Text;
using static System.Net.WebUtility;

namespace Folio.Tools.Bench;

/// <summary>
/// Draws bench results as the bar chart in the README: time to first page and memory, side by side, grouped by
/// document, with Folio highlighted.
/// </summary>
internal static class Chart
{
    private const int Width = 1080, PanelWidth = 484;

    public static void Render(BenchResults results, IReadOnlyList<string> apps, string png)
    {
        var documents = results.Documents.Where(d => results.Results.Any(r => r.Document == d.Id)).ToList();
        string time = Panel(results, documents, apps, s => s.PageMs, "ms", out int height);
        string memory = Panel(results, documents, apps, s => s.MemoryMB, "MB", out _);

        // 28 px padding, 20 px headings, 6 px gap, the plots, 28 px padding.
        int pageHeight = 28 + 20 + 6 + height + 28;
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
              .panels { display: grid; grid-template-columns: 1fr 1fr; gap: 40px; }
              h2 { font-size: 15px; line-height: 20px; font-weight: 600; }
              h2 span { font-weight: 400; color: #74839a; }
              svg { display: block; margin-top: 6px; }
              svg text { font-family: inherit; }
            </style>
            </head>
            <body>
            <main>
              <div class="panels">
                <section><h2>Time to first page <span>· ms, lower is better</span></h2>{{time}}</section>
                <section><h2>Memory <span>· MB, lower is better</span></h2>{{memory}}</section>
              </div>
            </main>
            </body>
            </html>
            """;
        HtmlRenderer.Render(html, png, Width, pageHeight, scale: 2);
    }

    private static string Panel(BenchResults results, List<DocumentInfo> documents, IReadOnlyList<string> apps,
        Func<Summary, double?> metric, string unit, out int height)
    {
        const int Right = PanelWidth - 64, Top = 8, BarHeight = 18, RowHeight = 32, GroupGap = 16, LabelHeight = 24;
        // Room for the longest app name at 13 px, right-aligned against the bars.
        int left = Math.Max(70, 20 + 8 * apps.Max(a => a.Length));
        double? Value(string app, string document) => results.Results.FirstOrDefault(r => r.App == app && r.Document == document) is { } s ? metric(s) : null;
        double max = documents.SelectMany(d => apps.Select(a => Value(a, d.Id) ?? 0)).DefaultIfEmpty(0).Max();
        var (axisMax, step) = Axis(max);
        double X(double value) => left + value / axisMax * (Right - left);
        int plotBottom = Top + documents.Count * (LabelHeight + apps.Count * RowHeight) + (documents.Count - 1) * GroupGap;
        height = plotBottom + 24;

        var svg = new StringBuilder();
        var description = new List<string>();
        for (double v = 0; v <= axisMax + step / 2; v += step)
            svg.Append($"""<text x="{N(X(v))}" y="{plotBottom + 17}" text-anchor="middle" font-size="10" fill="#a3afc0">{Format(v)}</text>""");

        int y = Top;
        foreach (var document in documents)
        {
            svg.Append($"""<text x="0" y="{y + 15}" font-size="12" font-weight="600" fill="#4a5a72">{HtmlEncode(document.Name)}</text>""");
            y += LabelHeight;
            // Hairline grid beside the bars only, so it never crosses the group heading.
            for (double v = 0; v <= axisMax + step / 2; v += step)
            {
                svg.Append($"""<line x1="{N(X(v))}" x2="{N(X(v))}" y1="{y}" y2="{y + apps.Count * RowHeight}" stroke="{(v == 0 ? "#cfd8e4" : "#e9eef5")}" stroke-width="1" shape-rendering="crispEdges"/>""");
            }
            var values = new List<string>();
            foreach (var app in apps)
            {
                double center = y + RowHeight / 2.0;
                bool folio = app == "Folio";
                string weight = folio ? "600" : "400", color = folio ? "#1b2a41" : "#4a5a72";
                if (results.Versions?.GetValueOrDefault(app) is { } version)
                {
                    // The name nudged up to make room for the version under it, the two centred on the bar together.
                    svg.Append($"""<text x="{left - 10}" y="{N(center + 0.5)}" text-anchor="end" font-size="13" font-weight="{weight}" fill="{color}">{HtmlEncode(app)}</text>""");
                    svg.Append($"""<text x="{left - 10}" y="{N(center + 11)}" text-anchor="end" font-size="7" fill="#a3afc0">{HtmlEncode(version)}</text>""");
                }
                else
                {
                    svg.Append($"""<text x="{left - 10}" y="{N(center + 4.5)}" text-anchor="end" font-size="13" font-weight="{weight}" fill="{color}">{HtmlEncode(app)}</text>""");
                }
                if (Value(app, document.Id) is { } value)
                {
                    // Rounded at the data end, square at the baseline.
                    double x0 = X(0), x1 = Math.Max(X(value), x0 + 4), r = 4, top = center - BarHeight / 2.0, bottom = center + BarHeight / 2.0;
                    svg.Append($"""<path d="M{N(x0)},{N(top)} H{N(x1 - r)} Q{N(x1)},{N(top)} {N(x1)},{N(top + r)} V{N(bottom - r)} Q{N(x1)},{N(bottom)} {N(x1 - r)},{N(bottom)} H{N(x0)} Z" fill="{(folio ? "#2777da" : "#cdd9e9")}"/>""");
                    svg.Append($"""<text x="{N(x1 + 8)}" y="{N(center + 4.5)}" font-size="12.5" font-weight="{weight}" fill="{color}">{Format(value)} {unit}</text>""");
                    values.Add($"{app} {Format(value)} {unit}");
                }
                else
                {
                    svg.Append($"""<text x="{N(X(0) + 8)}" y="{N(center + 4.5)}" font-size="12.5" font-style="italic" fill="#a3afc0">no result</text>""");
                    values.Add($"{app} no result");
                }
                y += RowHeight;
            }
            description.Add($"{document.Name}: {string.Join(", ", values)}");
            y += GroupGap;
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
