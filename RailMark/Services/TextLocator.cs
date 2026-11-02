using System.Text;

namespace RailMark.Services;

internal static class TextLocator
{
    /// <summary>
    /// Typographic characters a PDF producer emits but an AI agent (or a human) is unlikely to
    /// reproduce verbatim when quoting. Substituting them on both sides of a match is what lets
    /// a quote of "the first fifty" find a page that actually contains "the ﬁrst ﬁfty".
    /// </summary>
    private static readonly Dictionary<char, string> CharacterSubstitutions = new()
    {
        ['ﬀ'] = "ff",
        ['ﬁ'] = "fi",
        ['ﬂ'] = "fl",
        ['ﬃ'] = "ffi",
        ['ﬄ'] = "ffl",
        ['‘'] = "'",
        ['’'] = "'",
        ['‚'] = "'",
        ['‛'] = "'",
        ['“'] = "\"",
        ['”'] = "\"",
        ['„'] = "\"",
        ['‟'] = "\"",
        ['…'] = "...",
        // Unicode hyphen / non-breaking hyphen — the same character as ASCII '-' for matching,
        // and normalising them here is what lets the de-hyphenation rule below see them.
        ['‐'] = "-",
        ['‑'] = "-",
    };

    private static bool IsHyphen(char c) => c == '-' || c == '‐' || c == '‑';

    /// <summary>
    /// Normalises <paramref name="original"/> for matching — collapses whitespace runs to a single
    /// space, drops soft hyphens and control characters, expands ligatures and smart quotes, and
    /// rejoins words split across a line break by a soft (wrap) hyphen. Returns the normalised
    /// text plus a map from each normalised index to the index in <paramref name="original"/> it
    /// came from, with a final sentinel entry for the end of the string.
    /// </summary>
    internal static (string text, int[] map) NormalizeWithMap(string original)
    {
        var sb = new StringBuilder(original.Length);
        // A ligature expands to more characters than it consumes, so the normalised text can be
        // longer than the input — the map has to grow rather than being sized up front.
        var map = new List<int>(original.Length + 1);
        bool lastWasSpace = true;

        for (int i = 0; i < original.Length; i++)
        {
            char c = original[i];

            if (c == '\u00AD' || c == '\u0002' || c == '\u0003') continue;
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') continue;

            // A hyphen ending a line, preceded by a lowercase letter, is a soft hyphen the
            // producer inserted to wrap a word: drop it and the line break so "com-\npileable"
            // matches "compileable". A non-lowercase predecessor means a real hyphen that
            // happens to fall at a line end ("COVID-\n19", "H-\n1") — keep the hyphen, but still
            // swallow the break rather than turning it into a space, so the word stays whole.
            if (IsHyphen(c) && sb.Length > 0 && !char.IsWhiteSpace(sb[^1]))
            {
                int j = i + 1;
                while (j < original.Length && (original[j] == ' ' || original[j] == '\t' || original[j] == '\r'))
                    j++;

                if (j < original.Length && original[j] == '\n')
                {
                    if (!char.IsLower(sb[^1]))
                    {
                        map.Add(i);
                        sb.Append('-');
                    }

                    while (j < original.Length && char.IsWhiteSpace(original[j]))
                        j++;
                    i = j - 1;
                    lastWasSpace = false;
                    continue;
                }
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    map.Add(i);
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else if (CharacterSubstitutions.TryGetValue(c, out var substitution))
            {
                // Every expanded character maps back to the single source character.
                foreach (var sc in substitution)
                {
                    map.Add(i);
                    sb.Append(sc);
                }
                lastWasSpace = false;
            }
            else
            {
                map.Add(i);
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
        {
            sb.Length--;
            map.RemoveAt(map.Count - 1);
        }

        map.Add(original.Length);

        return (sb.ToString(), map.ToArray());
    }

    internal static string CleanText(string text)
    {
        var (result, _) = NormalizeWithMap(text);
        return result;
    }

    /// <summary>How far, in characters, <see cref="SentenceAround"/> reaches either side of the span.</summary>
    internal const int MaxContextChars = 200;

    /// <summary>
    /// The sentence holding <c>[start, end)</c> of <paramref name="pageText"/>, on one line, with the
    /// span bolded \u2014 what annotation mode shows around a highlight, rather than the whole page.
    /// A sentence ends at <c>.</c>/<c>!</c>/<c>?</c>/<c>\u2026</c> (plus any closing quote or bracket)
    /// before whitespace, at a blank line, or at the end of a line much shorter than the page's
    /// typical line (a heading, or a paragraph's last line). Past <see cref="MaxContextChars"/> on
    /// either side it is cut at a word and marked with an ellipsis. Line breaks become spaces, except
    /// after a hyphen, which rejoins the word; the extractor's invisible markers are dropped.
    /// </summary>
    internal static string SentenceAround(string pageText, int start, int end)
    {
        var lines = LineSpans(pageText);
        int shortLine = ShortLineThreshold(pageText, lines);

        int from = start;
        bool cutBefore = false;
        while (from > 0)
        {
            if (start - from >= MaxContextChars) { cutBefore = true; break; }
            char c = pageText[from - 1];
            if (char.IsWhiteSpace(c))
            {
                if (c == '\n' && EndsParagraph(pageText, LineAt(lines, from - 1), shortLine)) break;
                if (EndsSentence(pageText, from - 1)) break;
            }
            from--;
        }

        int to = end;
        bool cutAfter = false;
        while (to < pageText.Length)
        {
            if (to - end >= MaxContextChars) { cutAfter = true; break; }
            char c = pageText[to];
            if (char.IsWhiteSpace(c))
            {
                if (c is '\n' or '\r' && EndsParagraph(pageText, LineAt(lines, to), shortLine)) break;
                if (EndsSentence(pageText, to)) break;
            }
            to++;
        }

        // A cut falls between words, never inside one.
        if (cutBefore)
            while (from < start && !char.IsWhiteSpace(pageText[from])) from++;
        if (cutAfter)
            while (to > end && !char.IsWhiteSpace(pageText[to - 1])) to--;

        return (cutBefore ? "\u2026 " : "")
            + OneLine(pageText[from..start]).TrimStart()
            + "**" + OneLine(pageText[start..end]).Trim() + "**"
            + OneLine(pageText[end..to]).TrimEnd()
            + (cutAfter ? " \u2026" : "");
    }

    /// <summary>
    /// True when the text before the whitespace at <paramref name="ws"/> closes a sentence: terminal
    /// punctuation, optionally followed by closing quotes or brackets.
    /// </summary>
    private static bool EndsSentence(string text, int ws)
    {
        int i = ws - 1;
        while (i >= 0 && IsInvisible(text[i])) i--;
        while (i >= 0 && text[i] is '"' or '\'' or ')' or ']' or '\u201D' or '\u2019' or '\u00BB') i--;
        return i >= 0 && text[i] is '.' or '!' or '?' or '\u2026';
    }

    /// <summary>A line that is blank, or ends well short of the page's typical line, closes its paragraph.</summary>
    private static bool EndsParagraph(string text, LineSpan line, int shortLine)
    {
        int length = VisibleLength(text, line);
        return length == 0 || length < shortLine;
    }

    /// <summary>Text on one line: whitespace runs become one space, and a line break after a hyphen none.</summary>
    private static string OneLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false, joined = false;
        foreach (char c in text)
        {
            if (IsInvisible(c) || (char.IsControl(c) && !char.IsWhiteSpace(c))) continue;
            if (char.IsWhiteSpace(c))
            {
                if (c is '\n' or '\r' && !space && sb.Length > 0 && IsHyphen(sb[^1])) joined = true;
                else if (!joined) space = true;
                continue;
            }
            if (space) sb.Append(' ');
            space = joined = false;
            sb.Append(c);
        }
        if (space) sb.Append(' ');
        return sb.ToString();
    }

    private readonly record struct LineSpan(int Start, int End);

    /// <summary>Each line's <c>[Start, End)</c>, the line break (a CRLF's CR included) excluded.</summary>
    private static List<LineSpan> LineSpans(string text)
    {
        var spans = new List<LineSpan>();
        int s = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            int e = i > s && text[i - 1] == '\r' ? i - 1 : i;
            spans.Add(new LineSpan(s, e));
            s = i + 1;
        }
        return spans;
    }

    /// <summary>The line holding <paramref name="pos"/>; a line break belongs to the line it ends.</summary>
    private static LineSpan LineAt(List<LineSpan> lines, int pos)
    {
        int lo = 0, hi = lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (lines[mid].Start <= pos) lo = mid; else hi = mid - 1;
        }
        return lines[lo];
    }

    private static int VisibleLength(string text, LineSpan line)
    {
        var span = text.AsSpan(line.Start, line.End - line.Start).Trim();
        int n = 0;
        foreach (char c in span)
            if (!IsInvisible(c)) n++;
        return n;
    }

    /// <summary>
    /// Under 60% of the median non-blank line's length. Zero (no line is short) on a page of fewer
    /// than three lines, where a median says nothing.
    /// </summary>
    private static int ShortLineThreshold(string text, List<LineSpan> lines)
    {
        var lengths = lines.Select(l => VisibleLength(text, l)).Where(n => n > 0).Order().ToList();
        return lengths.Count < 3 ? 0 : (int)(lengths[lengths.Count / 2] * 0.6);
    }

    /// <summary>
    /// Rebuilds the page-text span covered by a multi-line text-markup annotation, given the text
    /// extracted from each of its per-line rects.
    /// </summary>
    /// <remarks>
    /// Joining the per-line parts with a space is wrong when the producer split a word across the
    /// break: <c>PdfTextService</c> already de-hyphenates, so the text layer records that join as a
    /// soft-hyphen marker (U+0002) rather than a space, and a space would put the word back
    /// together as "inter pretable". Locating the parts back in the page text recovers whatever
    /// actually sat between them — the marker for a split word, a newline for an ordinary wrap —
    /// and <see cref="CleanText"/> then resolves either correctly.
    /// Returns <c>null</c> when the parts cannot be located verbatim and in order, leaving the
    /// caller to fall back to a plain join.
    /// </remarks>
    internal static string? SpanCoveringParts(string pageText, IReadOnlyList<string> parts)
    {
        int firstStart = -1, lastEnd = -1, cursor = 0;

        foreach (var part in parts)
        {
            var needle = part.Trim();
            if (needle.Length == 0) continue;

            var idx = pageText.IndexOf(needle, cursor, StringComparison.Ordinal);
            if (idx < 0) return null;

            if (firstStart < 0) firstStart = idx;
            lastEnd = idx + needle.Length;
            cursor = lastEnd;
        }

        return firstStart < 0 ? null : pageText[firstStart..lastEnd];
    }

    /// <summary>
    /// Resolves <paramref name="quote"/> to the (CharStart, CharLength) range of its first match
    /// within <paramref name="pageText"/>, or null if it does not occur. See
    /// <see cref="FindQuoteMatches"/> for the matching rules.
    /// </summary>
    internal static (int CharStart, int CharLength)? ResolveQuote(string pageText, string quote)
    {
        var matches = FindQuoteMatches(NormalizedText.From(pageText), quote);
        return matches.Count == 0 ? null : (matches[0].CharStart, matches[0].CharLength);
    }

    /// <summary>
    /// Every non-overlapping place <paramref name="quote"/> occurs in <paramref name="page"/>, in
    /// reading order. Matching is case-insensitive and happens in normalised space (whitespace,
    /// ligatures, smart quotes, wrap hyphens — see <see cref="NormalizeWithMap"/>), which is a
    /// superset of a verbatim match, so a count taken here is the true number of candidates the
    /// quote could refer to.
    /// </summary>
    internal static List<QuoteMatch> FindQuoteMatches(NormalizedText page, string quote)
    {
        var matches = new List<QuoteMatch>();
        var needle = CleanText(quote);
        if (needle.Length == 0) return matches;

        int from = 0;
        while (from <= page.Text.Length - needle.Length)
        {
            var idx = page.Text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            matches.Add(page.MatchAt(idx, needle.Length));
            from = idx + needle.Length;
        }
        return matches;
    }

    /// <summary>
    /// True when the normalised text immediately before <paramref name="match"/> ends with
    /// <paramref name="before"/> and the text immediately after it starts with
    /// <paramref name="after"/>. A null or blank context is not checked. The whitespace between the
    /// quote and its context is ignored, so a context can be given with or without it.
    /// </summary>
    internal static bool HasContext(NormalizedText page, QuoteMatch match, string? before, string? after)
    {
        if (!string.IsNullOrWhiteSpace(before))
        {
            var preceding = page.Text[..match.NormStart].TrimEnd();
            if (!preceding.EndsWith(CleanText(before), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(after))
        {
            var following = page.Text[match.NormEnd..].TrimStart();
            if (!following.StartsWith(CleanText(after), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// How much text, in normalised characters, may sit between the two halves of a quote split by
    /// a page break — the running footer of one page and the running header of the next.
    /// </summary>
    internal const int MaxPageBreakGap = 300;

    /// <summary>The shortest half a split quote may have: a letter or two fits almost anywhere.</summary>
    internal const int MinSplitPart = 3;

    /// <summary>
    /// Finds a quote that runs off the bottom of <paramref name="page"/> and on at the top of
    /// <paramref name="next"/>: a head that ends a line of the first page and a tail that starts a
    /// line of the second, each within <see cref="MaxPageBreakGap"/> of the break, and each at
    /// least <see cref="MinSplitPart"/> characters. The split falls between words, or inside a word
    /// only where the page text actually hyphenates it. When several splits fit, the one with the
    /// least text between its halves wins. Returns null when no split fits.
    /// </summary>
    /// <remarks>
    /// Proximity alone is far too permissive: a one-letter head fits the end of nearly any page, so
    /// a quote given the wrong page number would "resolve" as a stray letter on one page and the
    /// rest on the next. Requiring the break to fall at a line break in the extracted text is what
    /// makes a split mean what it says.
    /// </remarks>
    internal static (QuoteMatch Head, QuoteMatch Tail)? FindAcrossPageBreak(NormalizedText page, NormalizedText next, string quote)
    {
        var needle = CleanText(quote);
        (QuoteMatch Head, QuoteMatch Tail)? best = null;
        int bestGap = int.MaxValue;

        void Try(string head, string tail, bool needsHyphenAfterHead)
        {
            if (head.Length < MinSplitPart || tail.Length < MinSplitPart) return;

            var headAt = page.Text.LastIndexOf(head, StringComparison.OrdinalIgnoreCase);
            if (headAt < 0) return;
            var gapBefore = page.Text.Length - (headAt + head.Length);
            if (gapBefore > MaxPageBreakGap) return;

            var tailAt = next.Text.IndexOf(tail, StringComparison.OrdinalIgnoreCase);
            if (tailAt < 0 || tailAt > MaxPageBreakGap) return;

            var headMatch = page.MatchAt(headAt, head.Length);
            var tailMatch = next.MatchAt(tailAt, tail.Length);
            if (!EndsLine(page.Original, headMatch, needsHyphenAfterHead)) return;
            if (!StartsLine(next.Original, tailMatch)) return;

            if (gapBefore + tailAt >= bestGap) return;
            bestGap = gapBefore + tailAt;
            best = (headMatch, tailMatch);
        }

        for (int k = 1; k < needle.Length; k++)
        {
            if (needle[k] == ' ')
            {
                Try(needle[..k], needle[(k + 1)..], needsHyphenAfterHead: false);
            }
            else if (needle[k - 1] == '-')
            {
                // A real hyphen falling at the line end: "COVID-" | "19".
                Try(needle[..k], needle[k..], needsHyphenAfterHead: false);
            }
            else if (char.IsLetter(needle[k - 1]) && char.IsLetter(needle[k]))
            {
                // A word hyphenated over the break. The wrap hyphen may survive normalisation (it
                // is then part of the head), or be dropped with the line break — in which case it
                // must still be there in the original text.
                Try(needle[..k] + "-", needle[k..], needsHyphenAfterHead: false);
                Try(needle[..k], needle[k..], needsHyphenAfterHead: true);
            }
        }

        return best;
    }

    /// <summary>
    /// True when only whitespace or invisible markers — after an optional hyphen, when required —
    /// follow the match before the end of its line.
    /// </summary>
    private static bool EndsLine(string original, QuoteMatch match, bool needsHyphen)
    {
        int i = match.CharStart + match.CharLength;
        while (i < original.Length && IsInvisible(original[i])) i++;
        if (needsHyphen)
        {
            if (i >= original.Length || !IsHyphen(original[i])) return false;
            i++;
        }
        for (; i < original.Length && original[i] != '\n'; i++)
            if (!char.IsWhiteSpace(original[i]) && !IsInvisible(original[i])) return false;
        return true;
    }

    /// <summary>True when only whitespace precedes the match on its line.</summary>
    private static bool StartsLine(string original, QuoteMatch match)
    {
        for (int i = match.CharStart - 1; i >= 0 && original[i] != '\n'; i--)
            if (!char.IsWhiteSpace(original[i]) && !IsInvisible(original[i])) return false;
        return true;
    }

    private static bool IsInvisible(char c) => c is '\u00AD' or '\u0002' or '\u0003';

    /// <summary>
    /// The normalised text around <paramref name="match"/>, with the match itself bracketed as
    /// <c>[[…]]</c> — enough for an agent to confirm the quote landed where it meant it to.
    /// </summary>
    internal static string DescribeContext(NormalizedText page, QuoteMatch match, int radius = 40)
    {
        var start = Math.Max(0, match.NormStart - radius);
        var end = Math.Min(page.Text.Length, match.NormEnd + radius);
        return (start > 0 ? "…" : "")
            + page.Text[start..match.NormStart]
            + "[[" + page.Text[match.NormStart..match.NormEnd] + "]]"
            + page.Text[match.NormEnd..end]
            + (end < page.Text.Length ? "…" : "");
    }
}

/// <summary>A located quote: its range in the original text and in the normalised text.</summary>
internal readonly record struct QuoteMatch(int CharStart, int CharLength, int NormStart, int NormEnd);

/// <summary>
/// Page text paired with its normalised form and the index map between them, computed once so a
/// page can be searched for many quotes without normalising it again for each one.
/// </summary>
internal sealed class NormalizedText
{
    public string Original { get; }
    public string Text { get; }
    private readonly int[] _map;

    private NormalizedText(string original, string text, int[] map)
    {
        Original = original;
        Text = text;
        _map = map;
    }

    public static NormalizedText From(string original)
    {
        var (text, map) = TextLocator.NormalizeWithMap(original);
        return new NormalizedText(original, text, map);
    }

    /// <summary>
    /// Translates a normalised range back to the original text. The end is taken one past the
    /// last matched character rather than at the start of the next one, so characters the
    /// normaliser dropped after the match (a trailing soft-hyphen marker, say) are not dragged
    /// into the range and given geometry of their own.
    /// </summary>
    public QuoteMatch MatchAt(int normStart, int normLength)
    {
        var origStart = _map[normStart];
        var origEnd = _map[normStart + normLength - 1] + 1;
        return new QuoteMatch(origStart, origEnd - origStart, normStart, normStart + normLength);
    }
}
