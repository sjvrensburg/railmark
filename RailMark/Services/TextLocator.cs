using System.Text;

namespace RailMark.Services;

internal static class TextLocator
{
    internal static (string text, int[] map) NormalizeWithMap(string original)
    {
        var sb = new StringBuilder(original.Length);
        var map = new int[original.Length + 1];
        bool lastWasSpace = true;

        for (int i = 0; i < original.Length; i++)
        {
            char c = original[i];

            if (c == '\u00AD' || c == '\u0002' || c == '\u0003') continue;
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') continue;

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    map[sb.Length] = i;
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                map[sb.Length] = i;
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
            sb.Length--;
        map[sb.Length] = original.Length;

        return (sb.ToString(), map);
    }

    internal static string CleanText(string text)
    {
        var (result, _) = NormalizeWithMap(text);
        return result;
    }

    /// <summary>
    /// Resolves <paramref name="quote"/> to an exact (CharStart, CharLength) range within
    /// <paramref name="pageText"/>. Tier 1: exact case-insensitive match. Tier 2: whitespace-
    /// normalised match, translated back to original offsets. Returns null if neither matches.
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
