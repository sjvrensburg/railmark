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

    /// <summary>
    /// Resolves <paramref name="quote"/> to an exact (CharStart, CharLength) range within
    /// <paramref name="pageText"/>. Tier 1: exact case-insensitive match. Tier 2: normalised
    /// match (whitespace, ligatures, smart quotes, soft hyphens), translated back to original
    /// offsets. Returns null if neither matches.
    /// </summary>
    internal static (int CharStart, int CharLength)? ResolveQuote(string pageText, string quote)
    {
        var idx = pageText.IndexOf(quote, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return (idx, quote.Length);

        var (normPage, pageMap) = NormalizeWithMap(pageText);
        var normQuote = CleanText(quote);

        var normIdx = normPage.IndexOf(normQuote, StringComparison.OrdinalIgnoreCase);
        if (normIdx >= 0 && normIdx + normQuote.Length < pageMap.Length)
        {
            var origStart = pageMap[normIdx];
            var origEnd = pageMap[normIdx + normQuote.Length];
            return (origStart, origEnd - origStart);
        }

        return null;
    }
}
