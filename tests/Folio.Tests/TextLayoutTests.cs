using Folio.Pdf;
using Windows.Foundation;

namespace Folio.Tests;

public sealed class TextLayoutTests
{
    private const double CharWidth = 5, LineHeight = 10;

    /// <summary>
    /// Lays out lines of fixed-width characters, each starting at (x, y), joined by the empty-boxed
    /// "\r\n" PDFium generates between lines.
    /// </summary>
    private static PageText Create(params (string Text, double X, double Y)[] lines)
    {
        var codePoints = new List<int>();
        var boxes = new List<Rect>();
        foreach (var (text, x, y) in lines)
        {
            if (codePoints.Count > 0)
            {
                codePoints.AddRange(['\r', '\n']);
                boxes.AddRange([Rect.Empty, Rect.Empty]);
            }
            for (int i = 0; i < text.Length; i++)
            {
                codePoints.Add(text[i]);
                boxes.Add(new Rect(x + i * CharWidth, y, CharWidth, LineHeight));
            }
        }
        return new PageText
        {
            PageIndex = 0,
            CodePoints = [.. codePoints],
            Boxes = [.. boxes],
            Links = [],
            Annotations = [],
            Transform = default,
        };
    }

    // Two paragraphs; the first ends with a short line.
    //   0: "aaaaaaaaaa"  y 0..10
    //   1: "bbb"         y 12..22
    //   2: "cccccccccc"  y 40..50
    private static PageText Paragraphs() => Create(("aaaaaaaaaa", 0, 0), ("bbb", 0, 12), ("cccccccccc", 0, 40));

    [Fact]
    public void Lines_SplitAtLineBreaks()
    {
        var text = Paragraphs();
        Assert.Equal(3, text.Layout.Lines.Count);
        Assert.Equal((0, 10), (text.Layout.Lines[0].Start, text.Layout.Lines[0].End));
        Assert.Equal((12, 15), (text.Layout.Lines[1].Start, text.Layout.Lines[1].End));
        Assert.Equal((17, 27), (text.Layout.Lines[2].Start, text.Layout.Lines[2].End));
    }

    [Fact]
    public void CaretAt_RightOfShortLastLine_IsItsEnd()
    {
        var text = Paragraphs();
        // Beside "bbb", under the end of the long line above, and slightly below "bbb".
        Assert.Equal(15, text.Layout.CaretAt(new Point(40, 17)));
        Assert.Equal(15, text.Layout.CaretAt(new Point(40, 24)));
    }

    [Fact]
    public void CaretAt_BetweenParagraphs_PicksTheNearerLine()
    {
        var text = Paragraphs();
        Assert.Equal(15, text.Layout.CaretAt(new Point(40, 26)));
        Assert.Equal(17 + 8, text.Layout.CaretAt(new Point(40, 37)));
    }

    [Fact]
    public void CaretAt_InsideALine_SnapsToTheNearerCharacterEdge()
    {
        var text = Paragraphs();
        Assert.Equal(2, text.Layout.CaretAt(new Point(11, 5)));
        Assert.Equal(3, text.Layout.CaretAt(new Point(13, 5)));
        Assert.Equal(0, text.Layout.CaretAt(new Point(-30, 5)));
        Assert.Equal(10, text.Layout.CaretAt(new Point(90, 5)));
    }

    [Fact]
    public void CaretAt_OverlappingLineBoxes_PicksTheNearerLine()
    {
        // Loose boxes of tightly set lines overlap by 2 points.
        var text = Create(("aaaa", 0, 0), ("bbbb", 0, 8));
        Assert.Equal(2, text.Layout.CaretAt(new Point(10, 8.5)));
        Assert.Equal(6 + 2, text.Layout.CaretAt(new Point(10, 9.5)));
    }

    [Fact]
    public void CaretAt_StaysInTheColumnThePointIsIn()
    {
        // Two columns whose lines aren't aligned: a point between lines of the right column picks
        // one of those, not the left column's line at the same height.
        var text = Create(("llll", 0, 0), ("llll", 0, 14), ("llll", 0, 28), ("rrrr", 100, 7), ("rrrr", 100, 21));
        Assert.Equal(3, text.Layout.LineAt(new Point(110, 17)));
        Assert.Equal(4, text.Layout.LineAt(new Point(110, 20)));
    }

    [Fact]
    public void CharAt_OverlappingLineBoxes_AgreesWithCaretAt()
    {
        var text = Create(("aaaa", 0, 0), ("bbbb", 0, 8));
        var p = new Point(10, 9.5);
        Assert.Equal(7, text.Layout.CharAt(p));
        Assert.Equal(text.Layout.LineAt(p), text.Layout.LineOf(text.Layout.CharAt(p)));
    }

    [Fact]
    public void CharAt_FindsCharactersAndMissesGaps()
    {
        var text = Paragraphs();
        Assert.Equal(1, text.Layout.CharAt(new Point(7, 5)));
        Assert.Equal(-1, text.Layout.CharAt(new Point(40, 17)));
        Assert.Equal(-1, text.Layout.CharAt(new Point(7, 30)));
    }

    [Fact]
    public void RangeRects_GivesOneRectPerLine()
    {
        var text = Paragraphs();
        var rects = text.Layout.RangeRects(5, 20);
        Assert.Equal(3, rects.Count);
        Assert.Equal(new Rect(25, 0, 25, 10), rects[0]);
        Assert.Equal(new Rect(0, 12, 15, 10), rects[1]);
        Assert.Equal(new Rect(0, 40, 15, 10), rects[2]);
    }

    [Fact]
    public void Lines_BreakWhenTextJumpsBackLeft()
    {
        // No generated line break, but the second run starts left of the first on the same height.
        var text = Create(("abc", 50, 0));
        var more = Create(("de", 0, 1));
        var combined = new PageText
        {
            PageIndex = 0,
            CodePoints = [.. text.CodePoints, .. more.CodePoints],
            Boxes = [.. text.Boxes, .. more.Boxes],
            Links = [],
            Annotations = [],
            Transform = default,
        };
        Assert.Equal(2, combined.Layout.Lines.Count);
    }

    private static (string Text, int[]? Map) ReadingOrder(PageText text)
    {
        var (codePoints, boxes, map) = TextLayout.ToReadingOrder(text.CodePoints, text.Boxes);
        var ordered = new PageText
        {
            PageIndex = 0,
            CodePoints = codePoints,
            Boxes = boxes,
            SourceIndexMap = map,
            Links = [],
            Annotations = [],
            Transform = default,
        };
        return (ordered.GetText(0, ordered.Count), map);
    }

    [Fact]
    public void ReadingOrder_PutsACaptionDrawnLaterBeforeTheTextBelowIt()
    {
        // Drawn body first, then the caption above it, like the figures in Word exports.
        var (text, map) = ReadingOrder(Create(("body one", 0, 100), ("body two", 0, 112), ("caption", 0, 50)));
        Assert.Equal("caption\nbody one\nbody two\n", text);
        Assert.NotNull(map);
        Assert.Equal("caption\r\n".Length, map[0]);
    }

    [Fact]
    public void ReadingOrder_KeepsTextAlreadyInOrder()
    {
        var (text, map) = ReadingOrder(Create(("heading", 0, 0), ("left one", 0, 30), ("left two", 0, 42), ("right one", 100, 30), ("right two", 100, 42)));
        Assert.Null(map);
        Assert.Equal("heading\nleft one\nleft two\nright one\nright two", text);
    }

    [Fact]
    public void ReadingOrder_ReadsTheLeftColumnFirst()
    {
        var (text, _) = ReadingOrder(Create(("heading", 0, 0), ("right one", 100, 30), ("right two", 100, 42), ("left one", 0, 30), ("left two", 0, 42)));
        Assert.Equal("heading\nleft one\nleft two\nright one\nright two\n", text);
    }

    [Fact]
    public void ReadingOrder_ColumnsWhoseBoxesTouch_ReadLeftFirst()
    {
        // The right column is drawn first, and its boxes overlap the left column's by a point.
        var (text, _) = ReadingOrder(Create(("right one", 39, 0), ("right two", 39, 12), ("left one", 0, 0), ("left two", 0, 12)));
        Assert.Equal("left one\nleft two\nright one\nright two\n", text);
    }

    [Fact]
    public void ReadingOrder_LevelBlocksSideBySide_ReadLeftFirst()
    {
        // Level with each other and overlapping a lot, so in one column; drawn right to left.
        var (text, _) = ReadingOrder(Create(("right", 10, 0), ("left", 0, 0)));
        Assert.Equal("left\nright\n", text);
    }

    [Fact]
    public void ReadingOrder_FullWidthTextSeparatesColumnsAboveAndBelowIt()
    {
        // Left/right blocks above a full-width paragraph come before it, those below it after.
        var (text, _) = ReadingOrder(Create(
            ("bottom right", 100, 100), ("top right", 100, 0), ("wide paragraph across both", 0, 50), ("top left", 0, 0), ("bottom left", 0, 100)));
        Assert.Equal("top left\ntop right\nwide paragraph across both\nbottom left\nbottom right\n", text);
    }

    [Fact]
    public void SourceRangeRects_FollowsMovedLines()
    {
        var page = Create(("body", 0, 100), ("caption", 0, 50));
        var (codePoints, boxes, map) = TextLayout.ToReadingOrder(page.CodePoints, page.Boxes);
        var ordered = new PageText
        {
            PageIndex = 0,
            CodePoints = codePoints,
            Boxes = boxes,
            SourceIndexMap = map,
            Links = [],
            Annotations = [],
            Transform = default,
        };
        // "caption" is at 6..13 in PDFium's order.
        Assert.Equal(new[] { new Rect(0, 50, 35, 10) }, ordered.Layout.SourceRangeRects(6, 13));
    }
}
