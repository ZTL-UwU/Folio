using Folio.Pdf;

namespace Folio.Tests;

public sealed class TextSearchTests
{
    private static List<SearchHit> Find(string text, string query, bool matchCase = false, bool wholeWords = false)
        => TextSearch.Find(text, 0, query, matchCase, wholeWords);

    private static (int Start, int End)[] Ranges(List<SearchHit> hits) => [.. hits.Select(h => (h.CharIndex, h.CharIndex + h.Length))];

    [Fact]
    public void IgnoresCase_UnlessAskedTo()
    {
        Assert.Equal([(0, 3), (8, 11)], Ranges(Find("The cat the", "the")));
        Assert.Equal([(8, 11)], Ranges(Find("The cat the", "the", matchCase: true)));
    }

    [Fact]
    public void HitsDoNotOverlap()
    {
        Assert.Equal([(0, 2), (2, 4)], Ranges(Find("aaaaa", "aa")));
    }

    [Fact]
    public void SpacesInTheQuery_MatchLineBreaksAndRunsOfSpaces()
    {
        Assert.Equal([(0, 14)], Ranges(Find("search\r\nengine", "search engine")));
        Assert.Equal([(0, 15)], Ranges(Find("search \t engine", "  search   engine ")));
    }

    [Fact]
    public void FindsWordsHyphenatedAcrossLines()
    {
        // PDFium leaves 0x02 (or 0xFFFE) where it joined a word hyphenated at the end of a line.
        var hit = Assert.Single(Find("an exam\u0002ple of", "example"));
        Assert.Equal((3, 11), (hit.CharIndex, hit.CharIndex + hit.Length));
        Assert.Equal("example", hit.Match);
        Assert.Single(Find("docu￾\r\nment", "document"));
    }

    [Fact]
    public void CurlyQuotesAndPlainOnesMatchEachOther()
    {
        Assert.Single(Find("I don’t know", "don't"));
        Assert.Single(Find("I don't know", "don’t"));
        Assert.Single(Find("a “quoted” word", "\"quoted\""));
    }

    [Fact]
    public void DashesMatchEachOther()
    {
        Assert.Single(Find("from 1990–2000", "1990-2000"));
        Assert.Single(Find("x − y", "x - y"));
        Assert.Single(Find("well-known", "well—known"));
    }

    [Fact]
    public void AccentsCount()
    {
        Assert.Empty(Find("café", "cafe"));
        Assert.Single(Find("café", "CAFÉ"));
    }

    [Fact]
    public void WholeWords_SkipsMatchesInsideWords()
    {
        Assert.Equal([(0, 3)], Ranges(Find("the other theme", "the", wholeWords: true)));
        Assert.Equal([(0, 3), (5, 8), (10, 13)], Ranges(Find("the other theme", "the")));
        // Found later in the text after a match inside a word was skipped.
        Assert.Equal([(7, 10)], Ranges(Find("thecat cat", "cat", wholeWords: true)));
        Assert.Single(Find("(the)", "the", wholeWords: true));
    }

    [Fact]
    public void BlankQuery_FindsNothing()
    {
        Assert.Empty(Find("some text", "   "));
        Assert.Empty(Find("some text", "\u0002"));
    }

    [Fact]
    public void Snippets_AreOneLineWithoutMarkers()
    {
        var hit = Assert.Single(Find("first line\r\nthe exam\u0002ple\r\nnext line", "example"));
        Assert.Equal("first line the ", hit.Before);
        Assert.Equal("example", hit.Match);
        Assert.Equal(" next line", hit.After);
    }
}
