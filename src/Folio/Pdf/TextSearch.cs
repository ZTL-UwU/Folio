using System.Text;

namespace Folio.Pdf;

/// <summary>
/// Finds a query in a page's text as <see cref="PdfDocument.GetSearchTextAsync"/> reads it: one character per
/// PDFium character index, so hits are in the indexes highlighting uses. Spaces in the query match any run of
/// spaces and line breaks, words split by a hyphen at the end of a line match whole, and curly quotes and dashes
/// match their plain forms. Accents count.
/// </summary>
internal static class TextSearch
{
    /// <summary>Most hits kept per page.</summary>
    public const int MaxHitsPerPage = 10000;

    public static List<SearchHit> Find(string text, int pageIndex, string query, bool matchCase, bool wholeWords)
    {
        var hits = new List<SearchHit>();
        string pattern = Fold(query, matchCase).Text.Trim();
        if (pattern.Length == 0) return hits;
        var (folded, map) = Fold(text, matchCase);
        bool checkStart = wholeWords && PageText.IsWordCodePoint(pattern[0]);
        bool checkEnd = wholeWords && PageText.IsWordCodePoint(pattern[^1]);
        int at = 0;
        while (hits.Count < MaxHitsPerPage && (at = folded.IndexOf(pattern, at, StringComparison.Ordinal)) >= 0)
        {
            int after = at + pattern.Length;
            if (checkStart && at > 0 && PageText.IsWordCodePoint(folded[at - 1]) ||
                checkEnd && after < folded.Length && PageText.IsWordCodePoint(folded[after]))
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
                Before = Context(text, Math.Max(0, start - 40), start, true),
                Match = Context(text, start, end, false),
                After = Context(text, end, Math.Min(text.Length, end + 60), false),
            });
            at = after;
        }
        return hits;
    }

    /// <summary>
    /// The text as it's compared: hyphen markers and invisible characters dropped, each run of spaces and line
    /// breaks made one space, quotes and dashes made plain, and lower-cased unless <paramref name="matchCase"/>.
    /// <c>Map</c> holds each folded character's index in <paramref name="text"/>.
    /// </summary>
    private static (string Text, int[] Map) Fold(string text, bool matchCase)
    {
        var chars = new char[text.Length];
        var map = new int[text.Length];
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            // PDFium joins a word hyphenated across lines, leaving a marker (0x02 or 0xFFFE) where the hyphen was.
            if (c is '\u0002' or '￾' or '­')
            {
                while (i + 1 < text.Length && text[i + 1] is '\r' or '\n') i++;
                continue;
            }
            if (c is '\0' or (>= '​' and <= '‏') or (>= '‪' and <= '‮') or (>= '⁠' and <= '⁩') or '﻿') continue;
            if (c <= ' ' || char.IsWhiteSpace(c))
            {
                if (n > 0 && chars[n - 1] == ' ') continue;
                c = ' ';
            }
            else
            {
                c = c switch
                {
                    '‘' or '’' or '‚' or '‛' or 'ʼ' or '＇' => '\'',
                    '“' or '”' or '„' or '‟' or '＂' => '"',
                    (>= '‐' and <= '―') or '−' or '﹘' or '﹣' or '－' => '-',
                    _ => matchCase ? c : char.ToLowerInvariant(c),
                };
            }
            chars[n] = c;
            map[n++] = i;
        }
        return (new string(chars, 0, n), map);
    }

    /// <summary>Text around a hit for the results list, on one line.</summary>
    private static string Context(string text, int start, int end, bool trimStart)
    {
        var sb = new StringBuilder(Math.Max(0, end - start));
        bool lastSpace = false;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            // Bidi overrides would reorder the rest of the result row, see LaunchPolicy.StripBidiControls.
            if (c is '￾' or '\u0002' or '\0' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')) continue;
            if (c <= ' ' || c == ' ')
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
