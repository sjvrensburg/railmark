using System.Text;
using System.Text.RegularExpressions;

namespace RailMark.Services.Docx;

/// <summary>
/// Removes the anchor bookmarks <see cref="DocxReviewPreparer"/> planted from the rendered PDF once
/// they have been read, so the output carries no <c>RR…</c> named destinations. The document's own
/// bookmarks stay.
/// </summary>
/// <remarks>
/// Each entry is blanked with spaces of the same length rather than removed, so every byte offset —
/// and with it the cross-reference table — stays valid without rewriting the file. That needs the
/// destinations in plain text, which LibreOffice writes (a <c>/Dests</c> dictionary off the catalog,
/// no object streams); in a name tree (<c>/Names /Dests</c>) the pair is blanked the same way. One
/// inside a compressed object stream is simply not found and stays. The annotation save that follows
/// rewrites the file, dropping the blanks.
/// </remarks>
internal static class NamedDestinationScrubber
{
    /// <summary>Blanks every destination whose name starts with <paramref name="prefix"/>; returns how many.</summary>
    public static int Scrub(byte[] pdf, string prefix)
    {
        // Latin-1 maps each byte to one char, so string offsets are byte offsets.
        var text = Encoding.Latin1.GetString(pdf);
        var name = Regex.Escape(prefix) + "[A-Za-z0-9]*";
        const string value = @"\s*(?:\[[^\[\]]*\]|\d+\s+\d+\s+R|<<[^<>]*>>)";
        var entry = new Regex($@"(?:/{name}|\({name}\)){value}");
        int count = 0;
        foreach (Match m in entry.Matches(text))
        {
            Array.Fill(pdf, (byte)' ', m.Index, m.Length);
            count++;
        }
        return count;
    }
}
