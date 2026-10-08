using Windows.Foundation;

namespace Folio.Pdf;

/// <summary>
/// A run of characters drawn on one line. <see cref="Start"/> is its first character with a box
/// and <see cref="End"/> one past its last, so line breaks PDFium generates fall between lines.
/// </summary>
public readonly record struct TextLine(int Start, int End, Rect Bounds)
{
    /// <summary>Horizontal extent of the paragraph or column the line belongs to.</summary>
    public double BlockLeft { get; init; } = Bounds.Left;
    public double BlockRight { get; init; } = Bounds.Right;
}

/// <summary>
/// Groups a page's characters into lines and maps points to text positions the way text editors do:
/// a point picks a line first, then a position on it, so the space beside a short line belongs to it.
/// </summary>
public sealed class TextLayout
{
    private readonly PageText _text;

    public IReadOnlyList<TextLine> Lines { get; }

    public TextLayout(PageText text)
    {
        _text = text;
        Lines = BuildLines(text.CodePoints, text.Boxes, out _);
    }

    /// <param name="blocks">Ranges [first, end) of lines forming a block.</param>
    private static TextLine[] BuildLines(int[] codePoints, Rect[] boxes, out List<(int First, int End)> blocks)
    {
        var lines = new List<TextLine>();
        int start = -1, last = -1;
        Rect bounds = Rect.Empty;
        bool breakSeen = false;
        for (int i = 0; i < codePoints.Length; i++)
        {
            int cp = codePoints[i];
            var box = boxes[i];
            if (cp is '\r' or '\n')
            {
                breakSeen = true;
                continue;
            }
            if (box.IsEmpty) continue;
            if (start >= 0 && (breakSeen || !ContinuesLine(bounds, boxes[last], box)))
            {
                lines.Add(new TextLine(start, last + 1, bounds));
                start = -1;
            }
            if (start < 0)
            {
                start = i;
                bounds = box;
            }
            else
            {
                bounds = Union(bounds, box);
            }
            last = i;
            breakSeen = false;
        }
        if (start >= 0) lines.Add(new TextLine(start, last + 1, bounds));

        // Consecutive lines stacked closely with overlapping extents form a block (a paragraph, or
        // a column of one); a line's hit area spans its block, so the space after a short last line
        // or before an indented first line belongs to that line rather than its neighbour.
        var result = lines.ToArray();
        blocks = [];
        int first = 0;
        for (int i = 1; i <= result.Length; i++)
        {
            if (i < result.Length && ContinuesBlock(result[i - 1].Bounds, result[i].Bounds)) continue;
            double left = double.MaxValue, right = double.MinValue;
            for (int j = first; j < i; j++)
            {
                left = Math.Min(left, result[j].Bounds.Left);
                right = Math.Max(right, result[j].Bounds.Right);
            }
            for (int j = first; j < i; j++) result[j] = result[j] with { BlockLeft = left, BlockRight = right };
            blocks.Add((first, i));
            first = i;
        }
        return result;
    }

    /// <summary>
    /// Puts a page's characters in reading order. PDFium gives them in the order they're drawn, which
    /// often isn't: captions and figure labels tend to come after the body text around them, so
    /// selecting from a caption into the next paragraph would take in everything in between.
    /// Blocks are read top to bottom, and a column before the column to its right.
    /// </summary>
    /// <returns>The reordered characters and, for each original index, its new one; null when the order is unchanged.</returns>
    public static (int[] CodePoints, Rect[] Boxes, int[]? Map) ToReadingOrder(int[] codePoints, Rect[] boxes)
    {
        var lines = BuildLines(codePoints, boxes, out var blocks);
        int n = blocks.Count;
        // The ordering compares every pair of blocks against every other block.
        if (n < 2 || n > 300) return (codePoints, boxes, null);

        var rects = new Rect[n];
        var spans = new (int Start, int End)[n];
        for (int b = 0; b < n; b++)
        {
            var (first, end) = blocks[b];
            rects[b] = lines[first].Bounds;
            for (int l = first + 1; l < end; l++) rects[b] = Union(rects[b], lines[l].Bounds);
            // A block takes the generated characters after it, up to the next block; the first block
            // also takes any before it.
            spans[b] = (b == 0 ? 0 : lines[first].Start, end < lines.Length ? lines[end].Start : codePoints.Length);
        }

        var order = ReadingOrder(rects);
        bool unchanged = true;
        for (int i = 0; i < n && unchanged; i++) unchanged = order[i] == i;
        if (unchanged) return (codePoints, boxes, null);

        var newCodePoints = new List<int>(codePoints.Length + n * 2);
        var newBoxes = new List<Rect>(codePoints.Length + n * 2);
        var map = new int[codePoints.Length];
        for (int k = 0; k < n; k++)
        {
            var (start, end) = spans[order[k]];
            for (int i = start; i < end; i++)
            {
                map[i] = newCodePoints.Count;
                newCodePoints.Add(codePoints[i]);
                newBoxes.Add(boxes[i]);
            }
            // Blocks moved next to each other are still separate lines.
            if (k < n - 1 && end > start && codePoints[end - 1] != '\n')
            {
                newCodePoints.AddRange(['\r', '\n']);
                newBoxes.AddRange([Rect.Empty, Rect.Empty]);
            }
        }
        return ([.. newCodePoints], [.. newBoxes], map);
    }

    /// <summary>
    /// Orders blocks after Breuel, "High performance document layout analysis": a comes before b when
    /// they share a column and a is higher (or level with it and further left), or when a is left of b
    /// and no block between them vertically spans both (as a heading over two columns does).
    /// </summary>
    /// <summary>How much of the narrower of two blocks may overlap the other horizontally with them still side by side.</summary>
    private const double SideBySideOverlap = 0.1;

    private static int[] ReadingOrder(Rect[] rects)
    {
        int n = rects.Length;
        static double Mid(Rect r) => r.Top + r.Height / 2;
        static double Center(Rect r) => r.Left + r.Width / 2;
        // Whether two blocks share a column. Loose character boxes can close a narrow gutter, so
        // blocks that only touch at their edges are still side by side.
        static bool Stacked(Rect a, Rect b) => Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) > Math.Min(a.Width, b.Width) * SideBySideOverlap;

        var after = new List<int>[n];
        var pending = new int[n];
        for (int a = 0; a < n; a++) after[a] = [];
        for (int a = 0; a < n; a++)
        {
            for (int b = 0; b < n; b++)
            {
                if (a == b) continue;
                Rect ra = rects[a], rb = rects[b];
                bool before = false;
                if (Stacked(ra, rb))
                {
                    before = Mid(ra) < Mid(rb) || (Mid(ra) == Mid(rb) && (Center(ra) < Center(rb) || (Center(ra) == Center(rb) && a < b)));
                }
                else if (Center(ra) < Center(rb))
                {
                    before = true;
                    double top = Math.Min(Mid(ra), Mid(rb)), bottom = Math.Max(Mid(ra), Mid(rb));
                    for (int c = 0; c < n && before; c++)
                    {
                        if (c == a || c == b) continue;
                        double mid = Mid(rects[c]);
                        if (mid > top && mid < bottom && Stacked(rects[c], ra) && Stacked(rects[c], rb)) before = false;
                    }
                }
                if (!before) continue;
                after[a].Add(b);
                pending[b]++;
            }
        }

        // Topological sort, taking the highest block among those that may go next. Odd layouts can
        // make a cycle; the highest remaining block goes next then.
        var order = new int[n];
        var done = new bool[n];
        for (int k = 0; k < n; k++)
        {
            int pick = -1;
            for (int pass = 0; pass < 2 && pick < 0; pass++)
            {
                for (int b = 0; b < n; b++)
                {
                    if (done[b] || (pass == 0 && pending[b] > 0)) continue;
                    if (pick < 0 || rects[b].Top < rects[pick].Top || (rects[b].Top == rects[pick].Top && rects[b].Left < rects[pick].Left)) pick = b;
                }
            }
            order[k] = pick;
            done[pick] = true;
            foreach (var b in after[pick]) pending[b]--;
        }
        return order;
    }

    private static bool ContinuesLine(Rect line, Rect previous, Rect box)
    {
        double overlap = Math.Min(line.Bottom, box.Bottom) - Math.Max(line.Top, box.Top);
        // Text that jumps back to the left starts a new line even when it overlaps vertically.
        return overlap > Math.Min(line.Height, box.Height) * 0.5 && box.Right > previous.Left;
    }

    private static bool ContinuesBlock(Rect above, Rect below)
    {
        bool movesDown = below.Top > above.Top + above.Height * 0.5;
        bool close = below.Top - above.Bottom < Math.Max(above.Height, below.Height);
        bool overlaps = Math.Min(above.Right, below.Right) > Math.Max(above.Left, below.Left);
        return movesDown && close && overlaps;
    }

    /// <summary>The line nearest a point (display points), or -1 when the page has no text.</summary>
    public int LineAt(Point p)
    {
        int best = -1;
        double bestScore = double.MaxValue, bestCenter = double.MaxValue;
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var b = line.Bounds;
            double dx = Math.Max(0, Math.Max(line.BlockLeft - p.X, p.X - line.BlockRight));
            double dy = Math.Max(0, Math.Max(b.Top - p.Y, p.Y - b.Bottom));
            // Vertical distance counts more, so a point stays on the row it's on; horizontal distance
            // still matters so a point in one column doesn't pick a line in the next.
            double score = dx + dy * 4;
            // Character boxes of adjacent lines can overlap; the nearer middle wins there.
            double center = Math.Abs(p.Y - (b.Top + b.Height / 2));
            if (score < bestScore - 0.01 || (score < bestScore + 0.01 && center < bestCenter))
            {
                best = i;
                bestScore = score;
                bestCenter = center;
            }
        }
        return best;
    }

    /// <summary>The text position (between characters) nearest a point.</summary>
    public int CaretAt(Point p)
    {
        int line = LineAt(p);
        return line < 0 ? 0 : CaretInLine(Lines[line], p.X);
    }

    /// <summary>The character under a point, allowing <paramref name="tolerance"/> around lines, or -1.</summary>
    public int CharAt(Point p, double tolerance = 0)
    {
        // The same line CaretAt picks, so a double click and a drag agree where line boxes overlap.
        int index = LineAt(p);
        if (index < 0) return -1;
        var line = Lines[index];
        var b = line.Bounds;
        if (p.X < b.Left - tolerance || p.X > b.Right + tolerance || p.Y < b.Top - tolerance || p.Y > b.Bottom + tolerance) return -1;
        return NearestChar(line, p.X);
    }

    /// <summary>The index of the line containing character <paramref name="index"/>, or of the line before it.</summary>
    public int LineOf(int index)
    {
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lines[mid].Start <= index) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return Math.Max(found, Lines.Count > 0 ? 0 : -1);
    }

    /// <summary>One rectangle per line covering the characters in [start, end).</summary>
    public List<Rect> RangeRects(int start, int end)
    {
        var result = new List<Rect>();
        if (Lines.Count == 0 || end <= start) return result;
        for (int l = LineOf(start); l < Lines.Count && Lines[l].Start < end; l++)
        {
            var line = Lines[l];
            var rect = Rect.Empty;
            for (int i = Math.Max(start, line.Start); i < Math.Min(end, line.End); i++)
            {
                var box = _text.Boxes[i];
                if (!box.IsEmpty) rect = rect.IsEmpty ? box : Union(rect, box);
            }
            if (!rect.IsEmpty) result.Add(rect);
        }
        return result;
    }

    /// <summary>Like <see cref="RangeRects"/> for a range of PDFium's character indices (<see cref="PageText.SourceIndexMap"/>).</summary>
    public List<Rect> SourceRangeRects(int start, int end)
    {
        if (_text.SourceIndexMap is not { } map) return RangeRects(start, end);
        start = Math.Clamp(start, 0, map.Length);
        end = Math.Clamp(end, start, map.Length);
        if (end == start) return [];
        // Ranges within a line (as search hits nearly always are) stay together when lines are reordered.
        if (map[end - 1] - map[start] == end - 1 - start) return RangeRects(map[start], map[end - 1] + 1);
        var result = new List<Rect>();
        for (int i = start; i < end; i++) result.AddRange(RangeRects(map[i], map[i] + 1));
        return result;
    }

    private static Rect Union(Rect a, Rect b)
    {
        double left = Math.Min(a.Left, b.Left), top = Math.Min(a.Top, b.Top);
        return new Rect(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    private int CaretInLine(TextLine line, double x)
    {
        if (x <= line.Bounds.Left) return line.Start;
        if (x >= line.Bounds.Right) return line.End;
        int i = NearestChar(line, x);
        var box = _text.Boxes[i];
        return x > box.Left + box.Width / 2 ? i + 1 : i;
    }

    private int NearestChar(TextLine line, double x)
    {
        int best = line.Start;
        double bestDistance = double.MaxValue;
        for (int i = line.Start; i < line.End; i++)
        {
            var box = _text.Boxes[i];
            if (box.IsEmpty) continue;
            double d = Math.Max(0, Math.Max(box.Left - x, x - box.Right));
            if (d < bestDistance)
            {
                best = i;
                bestDistance = d;
                if (d == 0) break;
            }
        }
        return best;
    }
}
