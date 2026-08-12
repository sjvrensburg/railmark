using System.Text;
using RailReader.Core.Models;

namespace RailMark.Services;

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
    // Vertical position of each heading on its own page, keyed by HeadingKey. Only populated for
    // headings whose title could be located in the page text; absent means page-level ordering.
    private readonly Dictionary<string, float>? _headingPositions;
    // Struck-out text an editor marked for replacement, keyed by the caret annotation that
    // carries the replacement — see BuildCaretReplacements.
    private readonly Dictionary<CaretAnnotation, (int page, int annotIdx)> _caretReplacements = [];
    // (page, annotIdx) of annotations rendered as part of another annotation, not on their own.
    private readonly HashSet<(int page, int annotIdx)> _groupChildren = [];

    public MarkdownBuilder(
        AnnotationFile annotations,
        string sourceName,
        List<OutlineEntry> outline,
        Dictionary<(int page, int annotIdx), string>? highlightTexts = null,
        Dictionary<int, string>? pageTexts = null,
        Dictionary<(int page, int annotIdx), string>? images = null,
        string? imageRelDir = null,
        Dictionary<string, float>? headingPositions = null)
    {
        _annotations = annotations;
        _sourceName = sourceName;
        _outline = outline;
        _highlightTexts = highlightTexts;
        _pageTexts = pageTexts;
        _images = images;
        _imageRelDir = imageRelDir;
        _headingPositions = headingPositions;
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

        // Flatten outline sorted by reading order (page, then vertical position where known) for
        // heading assignment. Without the Y component every annotation on a page carrying two
        // headings would be filed under the first of them.
        var sortedHeadings = new List<(string key, int? page, float y)>();
        CollectHeadingsByPage(_outline, sortedHeadings, _headingPositions);
        sortedHeadings.Sort((a, b) =>
        {
            // Entries without a page go to the end
            if (a.page == null && b.page == null) return 0;
            if (a.page == null) return 1;
            if (b.page == null) return -1;
            var pageCmp = a.page.Value.CompareTo(b.page.Value);
            return pageCmp != 0 ? pageCmp : a.y.CompareTo(b.y);
        });

        var validKeys = new HashSet<string>(headingOrder.Select(h => h.key));

        BuildCaretReplacements();

        // Group annotations by heading
        var grouped = new Dictionary<string, List<(int page, int annotIdx, Annotation annotation)>>();

        foreach (var (pageIdx, annotations) in _annotations.Pages)
        {
            for (int i = 0; i < annotations.Count; i++)
            {
                var annotation = annotations[i];
                var headingKey = FindNearestHeadingKey(sortedHeadings, validKeys, pageIdx, GetSortY(annotation));

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

    /// <summary>
    /// Picks the last heading that precedes the annotation in reading order. Headings whose
    /// position on the page is unknown sort to the top of their page (y == 0), which reproduces
    /// the previous page-only behaviour for them.
    /// </summary>
    private static string FindNearestHeadingKey(
        List<(string key, int? page, float y)> sortedHeadings,
        HashSet<string> validKeys,
        int annotPage,
        float annotY)
    {
        string? bestKey = null;
        foreach (var (key, page, y) in sortedHeadings)
        {
            if (!page.HasValue) break;

            bool precedes = page.Value < annotPage || (page.Value == annotPage && y <= annotY);
            if (!precedes) break;

            if (validKeys.Contains(key))
                bestKey = key;
        }
        return bestKey ?? "__no_heading__";
    }

    private static float GetSortY(Annotation a) => a switch
    {
        TextMarkupAnnotation m => m.Rects.Count > 0 ? m.Rects[0].Y : 0,
        TextNoteAnnotation n => n.Y,
        RectAnnotation r => r.Y,
        CaretAnnotation c => c.Y,
        FreeTextAnnotation f => f.Y,
        FreehandAnnotation f => f.Points.Count > 0 ? f.Points.Min(p => p.Y) : 0,
        _ => 0
    };

    /// <summary>
    /// A "replace this text with that text" edit is written as a caret carrying the replacement
    /// text with a strikeout over the text to remove, linked to it by /IRT. Pair them up so the
    /// edit renders as one entry instead of two unrelated ones.
    /// </summary>
    private void BuildCaretReplacements()
    {
        var caretsById = new Dictionary<string, CaretAnnotation>(StringComparer.Ordinal);
        foreach (var (_, annotations) in _annotations.Pages)
            foreach (var annotation in annotations)
                if (annotation is CaretAnnotation caret && !string.IsNullOrEmpty(caret.NativeId))
                    caretsById[caret.NativeId] = caret;

        if (caretsById.Count == 0) return;

        foreach (var (pageIdx, annotations) in _annotations.Pages)
        {
            for (int i = 0; i < annotations.Count; i++)
            {
                if (annotations[i] is not StrikeOutAnnotation strikeOut) continue;
                if (string.IsNullOrEmpty(strikeOut.InReplyTo)) continue;
                if (!caretsById.TryGetValue(strikeOut.InReplyTo, out var caret)) continue;
                // A caret can only stand in for one strikeout; leave any others standalone.
                if (!_caretReplacements.TryAdd(caret, (pageIdx, i))) continue;

                _groupChildren.Add((pageIdx, i));
            }
        }
    }

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

        sb.AppendLine("| Section | Text markup | Notes | Rectangles | Freehand | Other |");
        sb.AppendLine("|---------|-------------|-------|------------|----------|-------|");

        foreach (var (key, title, _) in headingOrder)
        {
            if (!grouped.TryGetValue(key, out var list)) continue;
            sb.AppendLine($"| {title} | {Count<TextMarkupAnnotation>(list)} | {Count<TextNoteAnnotation>(list)} | {Count<RectAnnotation>(list)} | {Count<FreehandAnnotation>(list)} | {CountOther(list)} |");
        }

        if (grouped.TryGetValue("__no_heading__", out var ug))
            sb.AppendLine($"| *(Other)* | {Count<TextMarkupAnnotation>(ug)} | {Count<TextNoteAnnotation>(ug)} | {Count<RectAnnotation>(ug)} | {Count<FreehandAnnotation>(ug)} | {CountOther(ug)} |");

        sb.AppendLine();
    }

    private static int Count<T>(List<(int, int, Annotation a)> list) where T : Annotation
        => list.Count(x => x.a is T);

    private static int CountOther(List<(int, int, Annotation a)> list)
        => list.Count(x => x.a is not TextMarkupAnnotation and not TextNoteAnnotation
            and not RectAnnotation and not FreehandAnnotation);

    private void EmitAnnotationGroup(
        StringBuilder sb,
        List<(int page, int annotIdx, Annotation annotation)> annotations)
    {
        var emittedImages = new HashSet<string>();

        foreach (var (page, annotIdx, annotation) in annotations)
        {
            // Rendered inline by its parent (e.g. the strikeout half of a replacement edit).
            if (_groupChildren.Contains((page, annotIdx)))
                continue;

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
            case TextMarkupAnnotation markup:
                EmitTextMarkup(sb, page, annotIdx, markup);
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

    private string GetMarkupText(int page, int annotIdx)
        => CleanText(_highlightTexts != null && _highlightTexts.TryGetValue((page, annotIdx), out var t) ? t : "");

    /// <summary>
    /// Some readers (Skim, notably) pre-fill an annotation's /Contents with a copy of the text it
    /// covers. Printing that as a reviewer comment just repeats the quote back, so drop it.
    /// </summary>
    private static bool IsEchoOfMarkedText(string? contents, string markedText)
        => !string.IsNullOrWhiteSpace(contents)
           && !string.IsNullOrWhiteSpace(markedText)
           && string.Equals(CleanText(contents), markedText.Trim(), StringComparison.Ordinal);

    private void EmitHighlight(StringBuilder sb, int page, int annotIdx, HighlightAnnotation highlight)
    {
        var highlightedText = GetMarkupText(page, annotIdx);

        if (!string.IsNullOrWhiteSpace(highlightedText))
        {
            var pageText = _pageTexts?.GetValueOrDefault(page);
            if (pageText != null)
            {
                // The context is page text emitted verbatim, so it still carries the extractor's
                // invisible de-hyphenation markers. Strip them after the offsets have been used.
                var bolded = TextLocator.StripInvisibleMarkers(
                    BoldHighlightInContext(page, pageText, highlightedText));
                sb.AppendLine($"> {bolded}");
            }
            else
            {
                sb.AppendLine($"> **{highlightedText}**");
            }
        }

        if (!string.IsNullOrWhiteSpace(highlight.Contents) && !IsEchoOfMarkedText(highlight.Contents, highlightedText))
        {
            sb.AppendLine(">");
            sb.AppendLine($"> **Comment:** {highlight.Contents}");
        }

        sb.AppendLine(">");
        sb.AppendLine($"> {GetLabel(page, "highlight")}");
        sb.AppendLine();
    }

    /// <summary>
    /// Underline, strikeout and squiggly markup. These carry an editorial intent the label spells
    /// out, following the vocabulary pdfannots uses for the same annotation types.
    /// </summary>
    private void EmitTextMarkup(StringBuilder sb, int page, int annotIdx, TextMarkupAnnotation markup)
    {
        var markedText = GetMarkupText(page, annotIdx);

        var (label, rendered) = markup switch
        {
            StrikeOutAnnotation => ("suggested deletion", $"~~{markedText}~~"),
            UnderlineAnnotation => ("underline", $"<u>{markedText}</u>"),
            SquigglyAnnotation => ("squiggly", $"*{markedText}*"),
            _ => ("text markup", markedText),
        };

        if (!string.IsNullOrWhiteSpace(markedText))
            sb.AppendLine($"> {rendered}");

        if (!string.IsNullOrWhiteSpace(markup.Contents) && !IsEchoOfMarkedText(markup.Contents, markedText))
        {
            sb.AppendLine(">");
            sb.AppendLine($"> **Comment:** {markup.Contents}");
        }

        sb.AppendLine(">");
        sb.AppendLine($"> {GetLabel(page, label)}");
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
        // A caret paired with a strikeout replaces the struck text; on its own it inserts.
        if (_caretReplacements.TryGetValue(caret, out var child))
        {
            var struckText = GetMarkupText(child.page, child.annotIdx);
            var replacement = CleanText(caret.Contents ?? "");

            if (!string.IsNullOrWhiteSpace(struckText))
                sb.AppendLine($"> ~~{struckText}~~ → **{replacement}**");
            else
                sb.AppendLine($"> Replace with **{replacement}**");

            sb.AppendLine(">");
            sb.AppendLine($"> {GetLabel(page, "suggested replacement")}");
            sb.AppendLine();
            return;
        }

        if (!string.IsNullOrWhiteSpace(caret.Contents))
        {
            sb.AppendLine($"> **Comment:** {caret.Contents}");
            sb.AppendLine(">");
        }

        sb.AppendLine($"> {GetLabel(page, "suggested insertion")}");
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
            _normPageCache[page] = cached = TextLocator.NormalizeWithMap(pageText);
        var (normPage, pageMap) = cached;
        var normHighlight = TextLocator.CleanText(highlightText);

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

    /// <summary>
    /// Stable identity for an outline entry. Public so callers that resolve heading positions
    /// out of band (they need the PDF text service, which this class has no access to) can key
    /// the map they pass as <c>headingPositions</c> the same way.
    /// </summary>
    internal static string HeadingKey(string title, int? page) => $"{title}||{page}";

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
        List<(string key, int? page, float y)> result,
        Dictionary<string, float>? headingPositions)
    {
        foreach (var entry in entries)
        {
            var key = HeadingKey(entry.Title, entry.Page);
            var y = headingPositions != null && headingPositions.TryGetValue(key, out var found) ? found : 0f;
            result.Add((key, entry.Page, y));
            CollectHeadingsByPage(entry.Children, result, headingPositions);
        }
    }

    internal static string CleanText(string text) => TextLocator.CleanText(text);
}
