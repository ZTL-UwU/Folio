using System.Text;

namespace Folio.Pdf;

[Flags]
public enum SearchOptions
{
    None = 0,
    MatchCase = 1,
    WholeWords = 2,
    /// <summary>Straight and curly quotes match each other: don't finds the typeset form of it.</summary>
    AnyQuote = 4,
    /// <summary>Hyphens, dashes and the minus sign match each other: 1990-2000 finds it with an en dash.</summary>
    AnyDash = 8,
    /// <summary>A word hyphenated at the end of a line matches whole; otherwise the hyphen is matched as one.</summary>
    JoinHyphenated = 16,
}

/// <summary>
/// Finds a query in a page's text as <see cref="PdfDocument.GetSearchTextAsync"/> reads it: one character per
/// PDFium character index, so hits are in the indexes highlighting uses. Spaces in the query match any run of
/// spaces and line breaks. Accents count.
/// </summary>
internal static class TextSearch
{
    /// <summary>Most hits kept per page.</summary>
    public const int MaxHitsPerPage = 10000;

    public static List<SearchHit> Find(string text, int pageIndex, string query, SearchOptions options)
    {
        var hits = new List<SearchHit>();
        var (foldedQuery, queryMap) = Fold(query, options);
        int lead = 0, trail = foldedQuery.Length;
        while (lead < trail && foldedQuery[lead] == ' ') lead++;
        while (trail > lead && foldedQuery[trail - 1] == ' ') trail--;
        if (lead == trail) return hits;
        string pattern = foldedQuery[lead..trail];
        var (folded, map) = Fold(text, options);
        bool wholeWords = options.HasFlag(SearchOptions.WholeWords), joined = options.HasFlag(SearchOptions.JoinHyphenated);
        // Word boundaries are where double-click selection finds them, in the characters as they were: folding makes
        // an en dash a hyphen and a curly apostrophe a straight one, which count as part of a word.
        bool checkStart = wholeWords && PageText.IsWordCodePoint(query[queryMap[lead]]);
        bool checkEnd = wholeWords && PageText.IsWordCodePoint(query[queryMap[trail - 1]]);
        int at = 0;
        while (hits.Count < MaxHitsPerPage && (at = folded.IndexOf(pattern, at, StringComparison.Ordinal)) >= 0)
        {
            int after = at + pattern.Length;
            if (checkStart && at > 0 && PageText.IsWordCodePoint(text[map[at - 1]]) ||
                checkEnd && after < folded.Length && PageText.IsWordCodePoint(text[map[after]]))
            {
                at++;
                continue;
            }
            int start = map[at], end = map[after - 1] + 1;
            hits.Add(new SearchHit
            {
                PageIndex = pageIndex,
                CharIndex = start,
                Length = end - start,
                Before = Context(text, Math.Max(0, start - 40), start, true, joined),
                Match = Context(text, start, end, false, joined),
                After = Context(text, end, Math.Min(text.Length, end + 60), false, joined),
            });
            at = after;
        }
        return hits;
    }

    /// <summary>
    /// The text as it's compared: hyphen markers and invisible characters dropped, each run of spaces and line
    /// breaks made one space, and quotes, dashes and case folded as <paramref name="options"/> say. <c>Map</c>
    /// holds each folded character's index in <paramref name="text"/>.
    /// </summary>
    private static (string Text, int[] Map) Fold(string text, SearchOptions options)
    {
        bool matchCase = options.HasFlag(SearchOptions.MatchCase);
        bool anyQuote = options.HasFlag(SearchOptions.AnyQuote), anyDash = options.HasFlag(SearchOptions.AnyDash);
        bool joinHyphenated = options.HasFlag(SearchOptions.JoinHyphenated);
        var chars = new char[text.Length];
        var map = new int[text.Length];
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            // PDFium joins a word hyphenated across lines, leaving a marker (0x02 or 0xFFFE) where the hyphen was.
            if (IsHyphenMarker(c))
            {
                if (joinHyphenated)
                {
                    while (i + 1 < text.Length && text[i + 1] is '\r' or '\n') i++;
                    continue;
                }
                c = '-';
            }
            if (c is '\0' or (>= '\u200B' and <= '\u200F') or (>= '\u202A' and <= '\u202E') or (>= '\u2060' and <= '\u2069') or '\uFEFF') continue;
            if (c <= ' ' || char.IsWhiteSpace(c))
            {
                if (n > 0 && chars[n - 1] == ' ') continue;
                c = ' ';
            }
            else
            {
                c = c switch
                {
                    '\u2018' or '\u2019' or '\u201A' or '\u201B' or '\u02BC' or '\uFF07' when anyQuote => '\'',
                    '\u201C' or '\u201D' or '\u201E' or '\u201F' or '\uFF02' when anyQuote => '"',
                    (>= '\u2010' and <= '\u2015') or '\u2212' or '\uFE58' or '\uFE63' or '\uFF0D' when anyDash => '-',
                    _ => matchCase ? c : char.ToLowerInvariant(c),
                };
            }
            chars[n] = c;
            map[n++] = i;
        }
        return (new string(chars, 0, n), map);
    }

    private static bool IsHyphenMarker(char c) => c is '\u0002' or '\uFFFE' or '\u00AD';

    /// <summary>Text around a hit for the results list, on one line, with hyphen markers left out of joined words.</summary>
    private static string Context(string text, int start, int end, bool trimStart, bool joined)
    {
        var sb = new StringBuilder(Math.Max(0, end - start));
        bool lastSpace = false;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (IsHyphenMarker(c))
            {
                if (joined) continue;
                c = '-';
            }
            // Bidi overrides would reorder the rest of the result row, see LaunchPolicy.StripBidiControls.
            if (c is '\0' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')) continue;
            if (c <= ' ' || c == '\u00A0')
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            lastSpace = false;
            // Append surrogates only as complete pairs.
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < end && char.IsLowSurrogate(text[i + 1])) sb.Append(c).Append(text[++i]);
                continue;
            }
            if (!char.IsLowSurrogate(c)) sb.Append(c);
        }
        var s = sb.ToString();
        if (trimStart && start > 0)
        {
            int space = s.IndexOf(' ');
            if (space >= 0 && space < s.Length - 1 && s.Length > 30) s = s[(space + 1)..];
        }
        return s;
    }
}
