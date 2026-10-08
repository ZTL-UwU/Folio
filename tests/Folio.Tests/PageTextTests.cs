using Folio.Pdf;
using Windows.Foundation;

namespace Folio.Tests;

public sealed class PageTextTests
{
    // PDFium reports UTF-16 code units, so 𝑎 (U+1D44E) arrives as two entries.
    private static PageText Create(string text)
    {
        int[] units = [.. text.Select(c => (int)c)];
        return new PageText
        {
            PageIndex = 0,
            CodePoints = units,
            Boxes = new Rect[units.Length],
            Links = [],
            Annotations = [],
            Transform = default,
        };
    }

    [Fact]
    public void GetText_KeepsCharactersOutsideTheBmp()
    {
        var text = Create("𝑎 = 𝑟̈");
        Assert.Equal("𝑎 = 𝑟̈", text.GetText(0, text.Count));
    }

    [Fact]
    public void GetText_RangeSplittingAPair_TakesTheWholeCharacter()
    {
        var text = Create("x𝑎y");
        Assert.Equal("x𝑎", text.GetText(0, 2));
        Assert.Equal("𝑎y", text.GetText(2, 4));
    }

    [Fact]
    public void GetText_DropsLoneSurrogates()
    {
        var text = Create("a\uDC00b\uD835");
        Assert.Equal("ab", text.GetText(0, text.Count));
    }
}
