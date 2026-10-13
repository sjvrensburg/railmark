using RailReader.Core.Models;

namespace RailMark.Services;

/// <summary>
/// The PDF outline flattened for filing annotations under headings: the outline order headings
/// are emitted in, and the reading order — (page, y) — used to find the heading that precedes an
/// annotation.
/// </summary>
internal sealed class HeadingIndex
{
    public const string NoHeadingKey = "__no_heading__";

    /// <summary>Headings in outline (DFS) order, with duplicate bookmarks removed.</summary>
    public List<(string key, string title, int depth)> Order { get; } = [];

    private readonly List<(string key, int? page, float y)> _byPosition = [];
    private readonly HashSet<string> _validKeys;
    private readonly Dictionary<string, string> _titles = [];

    /// <param name="headingPositions">
    /// Vertical position of each heading on its own page, keyed by <see cref="Key"/>. Only
    /// populated for headings whose title could be located in the page text; absent means
    /// page-level ordering.
    /// </param>
    public HeadingIndex(List<OutlineEntry> outline, Dictionary<string, float>? headingPositions)
    {
        // Deduplicate keys so a PDF with duplicate bookmark entries doesn't emit the same
        // section twice.
        BuildOrder(outline, 0, []);
        foreach (var (key, title, _) in Order)
            _titles[key] = title;
        _validKeys = new HashSet<string>(Order.Select(h => h.key));

        // Sorted by reading order (page, then vertical position where known). Without the Y
        // component every annotation on a page carrying two headings would be filed under the
        // first of them.
        CollectByPosition(outline, headingPositions);
        _byPosition.Sort((a, b) =>
        {
            // Entries without a page go to the end
            if (a.page == null && b.page == null) return 0;
            if (a.page == null) return 1;
            if (b.page == null) return -1;
            var pageCmp = a.page.Value.CompareTo(b.page.Value);
            return pageCmp != 0 ? pageCmp : a.y.CompareTo(b.y);
        });
    }

    /// <summary>
    /// Stable identity for an outline entry. Callers that resolve heading positions out of band
    /// (they need the PDF text service) key the map they pass in the same way.
    /// </summary>
    public static string Key(string title, int? page) => $"{title}||{page}";

    /// <summary>
    /// Picks the last heading that precedes the annotation in reading order, or
    /// <see cref="NoHeadingKey"/>. Headings whose position on the page is unknown sort to the top
    /// of their page (y == 0), which reproduces page-only behaviour for them.
    /// </summary>
    public string KeyFor(int annotPage, float annotY)
    {
        string? bestKey = null;
        foreach (var (key, page, y) in _byPosition)
        {
            if (!page.HasValue) break;

            bool precedes = page.Value < annotPage || (page.Value == annotPage && y <= annotY);
            if (!precedes) break;

            if (_validKeys.Contains(key))
                bestKey = key;
        }
        return bestKey ?? NoHeadingKey;
    }

    /// <summary>The heading title for a key from <see cref="KeyFor"/>; null for no heading.</summary>
    public string? TitleOf(string key) => _titles.GetValueOrDefault(key);

    /// <summary>Where an annotation starts vertically on its page — its reading-order position.</summary>
    public static float SortY(Annotation a) => a switch
    {
        TextMarkupAnnotation m => m.Rects.Count > 0 ? m.Rects[0].Y : 0,
        TextNoteAnnotation n => n.Y,
        RectAnnotation r => r.Y,
        CaretAnnotation c => c.Y,
        FreeTextAnnotation f => f.Y,
        FreehandAnnotation f => f.Points.Count > 0 ? f.Points.Min(p => p.Y) : 0,
        _ => 0
    };

    private void BuildOrder(List<OutlineEntry> entries, int depth, HashSet<string> seen)
    {
        foreach (var entry in entries)
        {
            var key = Key(entry.Title, entry.Page);
            if (seen.Add(key))
                Order.Add((key, entry.Title, depth));
            BuildOrder(entry.Children, depth + 1, seen);
        }
    }

    private void CollectByPosition(List<OutlineEntry> entries, Dictionary<string, float>? headingPositions)
    {
        foreach (var entry in entries)
        {
            var key = Key(entry.Title, entry.Page);
            var y = headingPositions != null && headingPositions.TryGetValue(key, out var found) ? found : 0f;
            _byPosition.Add((key, entry.Page, y));
            CollectByPosition(entry.Children, headingPositions);
        }
    }
}
