using System.Text;
using RailReader.Core.Models;

namespace Rr2Annotate.Services;

public class MarkdownBuilder
{
    private readonly AnnotationFile _annotations;
    private readonly string _sourceName;
    private readonly List<OutlineEntry> _outline;
    // Pre-extracted highlight text keyed by (0-based pageIdx, annotationIndex)
    private readonly Dictionary<(int page, int annotIdx), string>? _highlightTexts;
    // Full page text (raw PDF extraction) for bold-in-context fallback
    private readonly Dictionary<int, string>? _pageTexts;
    private readonly Dictionary<(int page, int annotIdx), string>? _images;
    private readonly string? _imageRelDir;
    private readonly Dictionary<int, (string text, int[] map)> _normPageCache = [];

    public MarkdownBuilder(
        AnnotationFile annotations,
        string sourceName,
        List<OutlineEntry> outline,
        Dictionary<(int page, int annotIdx), string>? highlightTexts = null,
        Dictionary<int, string>? pageTexts = null,
        Dictionary<(int page, int annotIdx), string>? images = null,
        string? imageRelDir = null)
    {
        _annotations = annotations;
        _sourceName = sourceName;
        _outline = outline;
        _highlightTexts = highlightTexts;
        _pageTexts = pageTexts;
        _images = images;
        _imageRelDir = imageRelDir;
    }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Annotations: {_sourceName}");
        sb.AppendLine();

        // Build heading index (DFS order = outline order); deduplicate keys so a PDF
        // with duplicate bookmark entries doesn't emit the same section twice.
        var headingOrder = new List<(string key, string title, int depth)>();
        BuildHeadingIndex(_outline, 0, headingOrder, []);

        // Flatten outline sorted by page for heading assignment
        var sortedHeadings = new List<(string key, int? page)>();
        CollectHeadingsByPage(_outline, sortedHeadings);
        sortedHeadings.Sort((a, b) =>
        {
            // Entries without a page go to the end
            if (a.page == null && b.page == null) return 0;
            if (a.page == null) return 1;
            if (b.page == null) return -1;
            return a.page.Value.CompareTo(b.page.Value);
        });

        var validKeys = new HashSet<string>(headingOrder.Select(h => h.key));

        // Group annotations by heading
        var grouped = new Dictionary<string, List<(int page, int annotIdx, Annotation annotation)>>();

        foreach (var (pageIdx, annotations) in _annotations.Pages)
        {
            for (int i = 0; i < annotations.Count; i++)
            {
                var annotation = annotations[i];
                var headingKey = FindNearestHeadingKey(sortedHeadings, validKeys, pageIdx);

                if (!grouped.ContainsKey(headingKey))
                    grouped[headingKey] = [];

                grouped[headingKey].Add((pageIdx, i, annotation));
            }
        }

        // Sort each group by reading order: page, then Y-position
        foreach (var group in grouped.Values)
        {
            group.Sort((a, b) =>
            {
                var pageCmp = a.page.CompareTo(b.page);
                return pageCmp != 0 ? pageCmp : GetSortY(a.annotation).CompareTo(GetSortY(b.annotation));
            });
        }

        EmitSummary(sb, headingOrder, grouped);

        // Emit in outline order
        foreach (var (key, title, depth) in headingOrder)
        {
            if (!grouped.TryGetValue(key, out var annotations))
                continue;

            var level = Math.Min(depth + 2, 4);
            sb.AppendLine($"{new string('#', level)} {title}");
            sb.AppendLine();

            EmitAnnotationGroup(sb, annotations);
        }

        // Ungrouped annotations
        if (grouped.TryGetValue("__no_heading__", out var ungrouped))
        {
            sb.AppendLine("## Other Annotations");
            sb.AppendLine();
            EmitAnnotationGroup(sb, ungrouped);
        }

        return sb.ToString();
    }

    private static string FindNearestHeadingKey(
        List<(string key, int? page)> sortedHeadings,
        HashSet<string> validKeys,
        int annotPage)
    {
        string? bestKey = null;
        foreach (var (key, page) in sortedHeadings)
        {
            if (page.HasValue && page.Value <= annotPage && validKeys.Contains(key))
                bestKey = key;
            else if (page.HasValue && page.Value > annotPage)
                break;
        }
        return bestKey ?? "__no_heading__";
    }

    private static double GetSortY(Annotation a) => a switch
    {
        HighlightAnnotation h => h.Rects.Count > 0 ? h.Rects[0].Y : 0,
        TextNoteAnnotation n => n.Y,
        RectAnnotation r => r.Y,
        FreehandAnnotation f => f.Points.Count > 0 ? f.Points.Min(p => p.Y) : 0,
        _ => 0
    };

    private void EmitSummary(
        StringBuilder sb,
        List<(string key, string title, int depth)> headingOrder,
        Dictionary<string, List<(int page, int annotIdx, Annotation annotation)>> grouped)
    {
        sb.AppendLine("## Summary");
        sb.AppendLine();

        int total = grouped.Values.Sum(g => g.Count);
        int pageCount = grouped.Values.SelectMany(g => g).Select(a => a.page).Distinct().Count();
        sb.AppendLine($"**{total} annotations** across **{pageCount} pages**");
        sb.AppendLine();

        sb.AppendLine("| Section | Highlights | Notes | Rectangles | Freehand | Other |");
        sb.AppendLine("|---------|-----------|-------|------------|----------|-------|");

        foreach (var (key, title, _) in headingOrder)
        {
            if (!grouped.TryGetValue(key, out var list)) continue;
            sb.AppendLine($"| {title} | {Count<HighlightAnnotation>(list)} | {Count<TextNoteAnnotation>(list)} | {Count<RectAnnotation>(list)} | {Count<FreehandAnnotation>(list)} | {CountOther(list)} |");
        }

        if (grouped.TryGetValue("__no_heading__", out var ug))
            sb.AppendLine($"| *(Other)* | {Count<HighlightAnnotation>(ug)} | {Count<TextNoteAnnotation>(ug)} | {Count<RectAnnotation>(ug)} | {Count<FreehandAnnotation>(ug)} | {CountOther(ug)} |");

        sb.AppendLine();
    }

    private static int Count<T>(List<(int, int, Annotation a)> list) where T : Annotation
        => list.Count(x => x.a is T);

    private static int CountOther(List<(int, int, Annotation a)> list)
        => list.Count(x => x.a is not HighlightAnnotation and not TextNoteAnnotation
            and not RectAnnotation and not FreehandAnnotation);

    private void EmitAnnotationGroup(
        StringBuilder sb,
        List<(int page, int annotIdx, Annotation annotation)> annotations)
    {
        var emittedImages = new HashSet<string>();

        foreach (var (page, annotIdx, annotation) in annotations)
        {
            bool suppressImage = false;
            if (TryGetImagePath(page, annotIdx, out var imgPath) && !emittedImages.Add(imgPath))
                suppressImage = true;

            EmitAnnotation(sb, page, annotIdx, annotation, suppressImage);
        }
    }

    private void EmitAnnotation(StringBuilder sb, int page, int annotIdx, Annotation annotation, bool suppressImage)
    {
        switch (annotation)
        {
            case HighlightAnnotation highlight:
                EmitHighlight(sb, page, annotIdx, highlight);
                break;
            case TextNoteAnnotation note:
                EmitTextNote(sb, page, note);
                break;
            case RectAnnotation rect:
                EmitRect(sb, page, annotIdx, rect);
                break;
            case FreehandAnnotation freehand:
                EmitFreehand(sb, page, annotIdx, freehand, suppressImage);
                break;
            case CaretAnnotation caret:
                EmitCaret(sb, page, caret);
                break;
            case FreeTextAnnotation freeText:
                EmitFreeText(sb, page, freeText);
                break;
        }
    }

    private void EmitHighlight(StringBuilder sb, int page, int annotIdx, HighlightAnnotation highlight)
    {
        var highlightedText = CleanText(
            _highlightTexts != null && _highlightTexts.TryGetValue((page, annotIdx), out var ht) ? ht : "");

        if (!string.IsNullOrWhiteSpace(highlightedText))
        {
            var pageText = _pageTexts?.GetValueOrDefault(page);
            if (pageText != null)
            {
                var bolded = BoldHighlightInContext(page, pageText, highlightedText);
                sb.AppendLine($"> {bolded}");
            }
            else
            {
                sb.AppendLine($"> **{highlightedText}**");
            }
        }

        if (!string.IsNullOrWhiteSpace(highlight.Contents))
        {
            sb.AppendLine(">");
            sb.AppendLine($"> **Comment:** {highlight.Contents}");
        }

        sb.AppendLine(">");
        sb.AppendLine($"> {GetLabel(page, "highlight")}");
        sb.AppendLine();
    }

    private void EmitTextNote(StringBuilder sb, int page, TextNoteAnnotation note)
    {
        var noteText = note.EffectiveContents;
        sb.AppendLine($"> **Note:** {noteText}");
        sb.AppendLine(">");
        sb.AppendLine($"> {GetEnrichedLabel(page, note, "note")}");
        sb.AppendLine();
    }

    private void EmitRect(StringBuilder sb, int page, int annotIdx, RectAnnotation rect)
    {
        if (TryGetImagePath(page, annotIdx, out var imagePath))
        {
            sb.AppendLine($"![Rectangle annotation, p. {page + 1}]({imagePath})");
            sb.AppendLine();
        }

        sb.AppendLine(">");
        sb.AppendLine($"> {GetLabel(page, "rectangle")}");
        sb.AppendLine();
    }

    private void EmitFreehand(StringBuilder sb, int page, int annotIdx, FreehandAnnotation freehand, bool suppressImage)
    {
        var hasImage = TryGetImagePath(page, annotIdx, out var imagePath);

        if (hasImage && !suppressImage)
        {
            sb.AppendLine($"![Freehand annotation, p. {page + 1}]({imagePath})");
            sb.AppendLine();
        }

        if (!hasImage)
        {
            sb.AppendLine("> *[Freehand drawing — use `--images` to include a screenshot]*");
            sb.AppendLine(">");
        }

        // Always emit the label — merged strokes share an image but each still has a page location.
        sb.AppendLine($"> {GetLabel(page, "freehand")}");
        sb.AppendLine();
    }

    private void EmitCaret(StringBuilder sb, int page, CaretAnnotation caret)
    {
        if (!string.IsNullOrWhiteSpace(caret.Contents))
        {
            sb.AppendLine($"> **Comment:** {caret.Contents}");
            sb.AppendLine(">");
        }

        sb.AppendLine($"> {GetLabel(page, "caret")}");
        sb.AppendLine();
    }

    private void EmitFreeText(StringBuilder sb, int page, FreeTextAnnotation freeText)
    {
        var content = CleanText(freeText.Contents ?? "");
        if (!string.IsNullOrWhiteSpace(content))
        {
            sb.AppendLine($"> **Free text:** {content}");
            sb.AppendLine(">");
        }

        sb.AppendLine($"> {GetLabel(page, "free text")}");
        sb.AppendLine();
    }

    private bool TryGetImagePath(int page, int annotIdx, out string relativePath)
    {
        relativePath = "";
        if (_images == null || _imageRelDir == null) return false;
        if (!_images.TryGetValue((page, annotIdx), out var absPath)) return false;
        relativePath = Path.Combine(_imageRelDir, Path.GetFileName(absPath));
        return true;
    }

    private static string GetLabel(int page, string baseLabel)
        => $"*(p. {page + 1}, {baseLabel})*";

    private static string GetEnrichedLabel(int page, Annotation annotation, string baseLabel)
    {
        // Include review state and author when set
        var parts = new List<string> { $"p. {page + 1}", baseLabel };

        if (annotation.State != ReviewState.None)
            parts.Add(annotation.State.ToString());

        if (!string.IsNullOrWhiteSpace(annotation.Author))
            parts.Add($"— {annotation.Author}");

        return $"*({string.Join(", ", parts)})*";
    }

    private string BoldHighlightInContext(int page, string pageText, string highlightText)
    {
        // Tier 1: exact match
        var idx = pageText.IndexOf(highlightText, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return pageText[..idx]
                + "**" + pageText.Substring(idx, highlightText.Length) + "**"
                + pageText[(idx + highlightText.Length)..];
        }

        // Tier 2: fuzzy match (normalised whitespace) — cache per page to avoid O(highlights × pageLen)
        if (!_normPageCache.TryGetValue(page, out var cached))
            _normPageCache[page] = cached = NormalizeWithMap(pageText);
        var (normPage, pageMap) = cached;
        var normHighlight = CleanText(highlightText);

        var normIdx = normPage.IndexOf(normHighlight, StringComparison.OrdinalIgnoreCase);
        if (normIdx >= 0 && normIdx + normHighlight.Length < pageMap.Length)
        {
            var origStart = pageMap[normIdx];
            var origEnd = pageMap[normIdx + normHighlight.Length];
            return pageText[..origStart]
                + "**" + pageText[origStart..origEnd] + "**"
                + pageText[origEnd..];
        }

        // Fall back to just the highlighted text bolded
        return $"**{highlightText}**";
    }

    private static string HeadingKey(string title, int? page) => $"{title}||{page}";

    private static void BuildHeadingIndex(
        List<OutlineEntry> entries, int depth,
        List<(string key, string title, int depth)> order,
        HashSet<string> seen)
    {
        foreach (var entry in entries)
        {
            var key = HeadingKey(entry.Title, entry.Page);
            if (seen.Add(key))
                order.Add((key, entry.Title, depth));
            BuildHeadingIndex(entry.Children, depth + 1, order, seen);
        }
    }

    private static void CollectHeadingsByPage(
        List<OutlineEntry> entries,
        List<(string key, int? page)> result)
    {
        foreach (var entry in entries)
        {
            result.Add((HeadingKey(entry.Title, entry.Page), entry.Page));
            CollectHeadingsByPage(entry.Children, result);
        }
    }

    private static (string text, int[] map) NormalizeWithMap(string original)
    {
        var sb = new StringBuilder(original.Length);
        var map = new int[original.Length + 1];
        bool lastWasSpace = true;

        for (int i = 0; i < original.Length; i++)
        {
            char c = original[i];

            if (c == '­' || c == '' || c == '') continue;
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
}
