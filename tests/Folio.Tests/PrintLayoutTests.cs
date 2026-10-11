using Folio.Printing;
using Windows.Foundation;

namespace Folio.Tests;

public sealed class PrintLayoutTests
{
    private static readonly PaperMetrics Letter = new(612, 792, new Rect(18, 18, 576, 756));
    private static readonly Size Portrait = new(612, 792);
    private static readonly Size Landscape = new(792, 612);

    [Theory]
    [InlineData("1-3, 5", new[] { 0, 1, 2, 4 })]
    [InlineData(" 2 ; 4 ", new[] { 1, 3 })]
    [InlineData("8-", new[] { 7, 8, 9 })]
    [InlineData("-2", new[] { 0, 1 })]
    [InlineData("3-1", new[] { 2, 1, 0 })]
    [InlineData("1–2,", new[] { 0, 1 })]
    public void TryParseRanges_AcceptsRanges(string text, int[] expected)
    {
        Assert.True(PrintLayout.TryParseRanges(text, 10, out var pages, out var error), error);
        Assert.Equal(expected, pages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("2-11")]
    [InlineData("a")]
    [InlineData("-")]
    [InlineData("1.5")]
    public void TryParseRanges_RejectsInvalid(string text)
    {
        Assert.False(PrintLayout.TryParseRanges(text, 10, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Filter_UsesPageNumbersAndReverses()
    {
        int[] pages = [0, 1, 2, 3, 4];
        Assert.Equal([0, 2, 4], PrintLayout.Filter(pages, PageSubset.Odd, reverse: false));
        Assert.Equal([3, 1], PrintLayout.Filter(pages, PageSubset.Even, reverse: true));
    }

    [Fact]
    public void Layout_FitsPageToPrintableArea()
    {
        var sheet = Assert.Single(PrintLayout.Layout([0], [Portrait], Letter, new(SheetOrientation.Automatic, PrintScaling.Fit, 1, 1)));
        var placement = Assert.Single(sheet.Pages);
        Assert.False(sheet.Paper.IsLandscape);
        // Limited by the width: 576 / 612, and centred in the printable area.
        Assert.Equal(576, placement.Bounds.Width, 3);
        Assert.Equal(792 * 576 / 612.0, placement.Bounds.Height, 3);
        Assert.Equal(306, placement.Bounds.X + placement.Bounds.Width / 2, 3);
        Assert.Equal(396, placement.Bounds.Y + placement.Bounds.Height / 2, 3);
    }

    [Fact]
    public void Layout_ActualSizeIsCentredOnThePaperAndClippedToThePrintableArea()
    {
        var sheet = Assert.Single(PrintLayout.Layout([0], [Portrait], Letter, new(SheetOrientation.Portrait, PrintScaling.ActualSize, 1, 1)));
        var placement = Assert.Single(sheet.Pages);
        Assert.Equal(new Rect(0, 0, 612, 792), placement.Bounds);
        Assert.Equal(Letter.Printable, placement.Clip);
    }

    [Fact]
    public void Layout_ShrinkOnlyScalesDown()
    {
        var options = new LayoutOptions(SheetOrientation.Portrait, PrintScaling.ShrinkOversized, 1, 1);
        var small = PrintLayout.Layout([0], [new Size(200, 300)], Letter, options)[0].Pages[0];
        Assert.Equal(200, small.Bounds.Width, 3);
        var big = PrintLayout.Layout([0], [new Size(1200, 1600)], Letter, options)[0].Pages[0];
        Assert.True(big.Bounds.Width <= 576 && big.Bounds.Height <= 756);
    }

    [Fact]
    public void Layout_AutomaticTurnsSheetsForLandscapePages()
    {
        var sheets = PrintLayout.Layout([0, 1], [Portrait, Landscape], Letter, new(SheetOrientation.Automatic, PrintScaling.Fit, 1, 1));
        Assert.False(sheets[0].Paper.IsLandscape);
        Assert.True(sheets[1].Paper.IsLandscape);
        // The margins turn with the paper.
        Assert.Equal(new Rect(18, 18, 756, 576), sheets[1].Paper.Printable);
    }

    [Fact]
    public void Layout_TwoPortraitPagesPerSheetGoSideBySideOnLandscape()
    {
        var sheets = PrintLayout.Layout([0, 1, 2], [Portrait, Portrait, Portrait], Letter, new(SheetOrientation.Automatic, PrintScaling.Fit, 1, 2));
        Assert.Equal(2, sheets.Count);
        Assert.True(sheets[0].Paper.IsLandscape);
        Assert.Equal(2, sheets[0].Pages.Count);
        Assert.True(sheets[0].Pages[0].Bounds.Right <= sheets[0].Pages[1].Bounds.Left);
        Assert.Single(sheets[1].Pages);
    }

    [Theory]
    [InlineData(4, false, 2, 2)]
    [InlineData(6, false, 2, 3)]
    [InlineData(6, true, 3, 2)]
    [InlineData(16, true, 4, 4)]
    public void Grid_PutsMorePagesAlongTheLongSide(int perSheet, bool landscape, int columns, int rows)
        => Assert.Equal((columns, rows), PrintLayout.Grid(perSheet, landscape));

    [Fact]
    public void ToPaper_IsTheInverseOfOrient()
    {
        var sheet = PrintLayout.Orient(new PaperMetrics(612, 792, new Rect(10, 20, 580, 740)), landscape: true);
        // The printable area, mapped back onto the paper, is where it started.
        Assert.Equal(new Rect(10, 20, 580, 740), PrintLayout.ToPaper(sheet.Printable, sheet.Width));
    }

    [Fact]
    public void Sequence_CollatedTwoSidedCopiesStartOnANewSheet()
    {
        Assert.Equal([0, 1, 2, null, 0, 1, 2], PrintLayout.Sequence(3, 2, collate: true, twoSided: true));
        Assert.Equal([0, 1, 2, 0, 1, 2], PrintLayout.Sequence(3, 2, collate: true, twoSided: false));
    }

    [Fact]
    public void Sequence_UncollatedRepeatsSheetsOfPaper()
    {
        Assert.Equal([0, 0, 1, 1], PrintLayout.Sequence(2, 2, collate: false, twoSided: false));
        Assert.Equal([0, 1, 0, 1, 2, null, 2], PrintLayout.Sequence(3, 2, collate: false, twoSided: true));
        Assert.Equal(4, PrintLayout.PaperCount(PrintLayout.Sequence(3, 2, collate: false, twoSided: true), twoSided: true));
    }
}
