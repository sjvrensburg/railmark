using RailReader.Core.Models;

namespace RailMark.Services;

/// <summary>
/// Marks the second half of a text markup that runs over a page break. A PDF annotation belongs
/// to a single page, so a quote spanning two pages becomes two annotations; the second is tagged
/// with a /Subj of <see cref="Subject"/> so it reads as the tail of the first rather than as a
/// separate, uncommented edit.
/// </summary>
internal static class ContinuationMarkup
{
    public const string Subject = "Continuation";

    public static bool Is(Annotation annotation)
        => annotation is TextMarkupAnnotation && annotation.Subject == Subject;

    /// <summary>
    /// Pairs each continuation with the markup it continues: on the previous page, the same kind
    /// of markup (a replacement's tail is a plain strikeout), by the same author — and of those,
    /// the lowest on the page, since a quote runs over the break from the bottom. Keyed by the
    /// head's (page, index); the value is the continuation's.
    /// </summary>
    public static Dictionary<(int page, int annotIdx), (int page, int annotIdx)> Pair(AnnotationFile file)
    {
        var pairs = new Dictionary<(int page, int annotIdx), (int page, int annotIdx)>();
        foreach (var (page, annotations) in file.Pages.OrderBy(p => p.Key))
        {
            if (!file.Pages.TryGetValue(page - 1, out var previous)) continue;

            for (int i = 0; i < annotations.Count; i++)
            {
                if (!Is(annotations[i])) continue;
                var tail = annotations[i];

                var head = previous
                    .Select((a, idx) => (a, idx))
                    .Where(t => t.a.GetType() == tail.GetType()
                                && !Is(t.a)
                                && string.Equals(t.a.Author, tail.Author, StringComparison.Ordinal)
                                && !pairs.ContainsKey((page - 1, t.idx)))
                    .OrderByDescending(t => HeadingIndex.SortY(t.a))
                    .Select(t => (int?)t.idx)
                    .FirstOrDefault();

                if (head is { } headIdx)
                    pairs[(page - 1, headIdx)] = (page, i);
            }
        }
        return pairs;
    }

    /// <summary>
    /// Joins the text of a head and its continuation. The join is a line break, which
    /// <see cref="TextLocator.CleanText"/> turns into a space between words — or removes, with the
    /// hyphen, when a word was hyphenated over the break.
    /// </summary>
    public static string JoinText(string head, string tail) => TextLocator.CleanText(head + "\n" + tail);
}
