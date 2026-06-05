using System.Text;
using Rr2Annotate.Models;

namespace Rr2Annotate.Services;

public class MarkdownBuilder
{
    private readonly AnnotationExport _export;
    private readonly Dictionary<(int page, int annotIdx), string>? _images;
    private readonly string? _imageRelDir;
    private readonly Dictionary<int, string>? _pageContext;
    private readonly List<FigureReference>? _extractedFigures;

    public MarkdownBuilder(
        AnnotationExport export,
        Dictionary<(int page, int annotIdx), string>? images = null,
        string? imageRelDir = null,
        Dictionary<int, string>? pageContext = null,
        List<FigureReference>? extractedFigures = null)
    {
        _export = export;
        _images = images;
        _imageRelDir = imageRelDir;
        _pageContext = pageContext;
        _extractedFigures = extractedFigures;
    }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Annotations: {_export.Source}");
        sb.AppendLine();

        // Build heading hierarchy lookup
        var headingOrder = new List<(string key, string title, int depth)>();
        BuildHeadingIndex(_export.Outline, 0, headingOrder);

        // Precompute valid heading keys for matching
        var validKeys = new HashSet<string>();
        foreach (var (key, _, _) in headingOrder)
            validKeys.Add(key);

        // Collect all annotations keyed by heading
        var grouped = new Dictionary<string, List<(int page, int annotIdx, Annotation annotation)>>();

        foreach (var page in _export.Pages)
        {
            for (int i = 0; i < page.Annotations.Count; i++)
            {
                var annot = page.Annotations[i];
                string headingKey;
                if (annot.NearestHeading == null)
                {
                    headingKey = "__no_heading__";
                }
                else
                {
                    // Try exact title+page match first, then fall back to title-only
                    // (matches outline entries with a null page number)
                    var exactKey = HeadingKey(annot.NearestHeading.Title, annot.NearestHeading.Page);
                    headingKey = validKeys.Contains(exactKey)
                        ? exactKey
                        : HeadingKey(annot.NearestHeading.Title, (int?)null);
                }

                if (!grouped.ContainsKey(headingKey))
                    grouped[headingKey] = [];

                grouped[headingKey].Add((page.Page, i, annot));
            }
        }

        // Sort each group by reading order
        foreach (var annotations in grouped.Values)
        {
            annotations.Sort((a, b) =>
            {
                var pageCmp = a.page.CompareTo(b.page);
                return pageCmp != 0 ? pageCmp : a.annotation.SortY.CompareTo(b.annotation.SortY);
            });
        }

        // Emit summary header
        EmitSummary(sb, headingOrder, grouped);

        // Emit annotations per heading in outline order
        foreach (var (key, title, depth) in headingOrder)
        {
            if (!grouped.TryGetValue(key, out var annotations))
                continue;

            var level = Math.Min(depth + 2, 4);
            sb.AppendLine($"{new string('#', level)} {title}");
            sb.AppendLine();

            EmitAnnotationGroup(sb, annotations);
        }

        // Annotations without a matching heading
        if (grouped.TryGetValue("__no_heading__", out var ungrouped))
        {
            sb.AppendLine("## Other Annotations");
            sb.AppendLine();
            EmitAnnotationGroup(sb, ungrouped);
        }

        // Extracted figures from RailReader2 export
        if (_extractedFigures is { Count: > 0 })
        {
            sb.AppendLine("## Extracted Figures");
            sb.AppendLine();
            foreach (var fig in _extractedFigures)
            {
                sb.AppendLine($"![{fig.Description}]({fig.RelativePath})");
                sb.AppendLine($"*(p. {fig.Page + 1})*");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private void EmitSummary(
        StringBuilder sb,
        List<(string key, string title, int depth)> headingOrder,
        Dictionary<string, List<(int page, int annotIdx, Annotation annotation)>> grouped)
    {
        sb.AppendLine("## Summary");
        sb.AppendLine();

        var totalAnnotations = grouped.Values.Sum(g => g.Count);
        var totalPages = grouped.Values.SelectMany(g => g).Select(a => a.page).Distinct().Count();
        sb.AppendLine($"**{totalAnnotations} annotations** across **{totalPages} pages**");
        sb.AppendLine();

        sb.AppendLine("| Section | Highlights | Notes | Rectangles | Freehand | Other |");
        sb.AppendLine("|---------|-----------|-------|------------|----------|-------|");

        foreach (var (key, title, _) in headingOrder)
        {
            if (!grouped.TryGetValue(key, out var annotations))
                continue;

            var highlights = annotations.Count(a => a.annotation is HighlightAnnotation);
            var notes = annotations.Count(a => a.annotation is TextNoteAnnotation);
            var rects = annotations.Count(a => a.annotation is RectAnnotation);
            var freehand = annotations.Count(a => a.annotation is FreehandAnnotation);
            var other = annotations.Count(a => IsOtherType(a.annotation));

            sb.AppendLine($"| {title} | {highlights} | {notes} | {rects} | {freehand} | {other} |");
        }

        if (grouped.ContainsKey("__no_heading__"))
        {
            var ug = grouped["__no_heading__"];
            sb.AppendLine($"| *(Other)* | {ug.Count(a => a.annotation is HighlightAnnotation)} | {ug.Count(a => a.annotation is TextNoteAnnotation)} | {ug.Count(a => a.annotation is RectAnnotation)} | {ug.Count(a => a.annotation is FreehandAnnotation)} | {ug.Count(a => IsOtherType(a.annotation))} |");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Emits a group of annotations, deduplicating block text when consecutive
    /// annotations share the same overlapping blocks, and deduplicating images
    /// when grouped freehand annotations share the same screenshot.
    /// </summary>
    private void EmitAnnotationGroup(
        StringBuilder sb,
        List<(int page, int annotIdx, Annotation annotation)> annotations)
    {
        string? lastBlockTextKey = null;
        var emittedImages = new HashSet<string>();

        foreach (var (page, annotIdx, annotation) in annotations)
        {
            // Unknown annotations produce no output — skip them entirely so they
            // don't poison the block-text dedup state for the next real annotation.
            if (annotation is UnknownAnnotation)
                continue;

            var blockText = GetBlockText(annotation.OverlappingBlocks);
            var blockTextKey = string.IsNullOrWhiteSpace(blockText) ? null : blockText;

            bool suppressContext = blockTextKey != null && blockTextKey == lastBlockTextKey;

            // Check if this annotation's image has already been emitted (grouped freehand)
            bool suppressImage = false;
            if (TryGetImagePath(page, annotIdx, out var imgPath) && !emittedImages.Add(imgPath))
                suppressImage = true;

            EmitAnnotation(sb, page, annotIdx, annotation, suppressContext, suppressImage,
                suppressContext ? null : blockText);
            lastBlockTextKey = blockTextKey;
        }
    }

    private void EmitAnnotation(StringBuilder sb, int page, int annotIdx, Annotation annotation,
        bool suppressContext, bool suppressImage, string? blockText)
    {
        switch (annotation)
        {
            case HighlightAnnotation highlight:
                EmitHighlight(sb, page, highlight, suppressContext, blockText);
                break;
            case TextNoteAnnotation note:
                EmitTextNote(sb, page, note, suppressContext, blockText);
                break;
            case RectAnnotation rect:
                EmitRect(sb, page, annotIdx, rect, suppressContext, blockText);
                break;
            case FreehandAnnotation freehand:
                EmitFreehand(sb, page, annotIdx, freehand, suppressContext, suppressImage, blockText);
                break;
            case CaretAnnotation caret:
                EmitCaret(sb, page, caret, suppressContext, blockText);
                break;
            case FreeTextAnnotation freeText:
                EmitFreeText(sb, page, freeText, suppressContext, blockText);
                break;
            case UnknownAnnotation:
                // Silently skip unrecognised annotation types
                break;
        }
    }

    private void EmitHighlight(StringBuilder sb, int page, HighlightAnnotation highlight,
        bool suppressContext, string? blockText)
    {
        var highlightedText = CleanText(highlight.Text ?? "");

        if (!string.IsNullOrWhiteSpace(blockText) && !string.IsNullOrWhiteSpace(highlightedText))
        {
            var bolded = BoldHighlightInContext(blockText, highlightedText, page);
            sb.AppendLine($"> {bolded}");
        }
        else if (!string.IsNullOrWhiteSpace(highlightedText))
        {
            sb.AppendLine($"> **{highlightedText}**");
        }
        else if (!string.IsNullOrWhiteSpace(blockText))
        {
            sb.AppendLine($"> {blockText}");
        }

        // Show reviewer comment (e.g. "rephrase", "what does this mean?")
        if (!string.IsNullOrWhiteSpace(highlight.Contents))
        {
            sb.AppendLine($">");
            sb.AppendLine($"> **Comment:** {highlight.Contents}");
        }

        sb.AppendLine($">");
        sb.AppendLine($"> {GetEnrichedLabel(page, highlight, highlight.Type)}");
        sb.AppendLine();
    }

    private void EmitTextNote(StringBuilder sb, int page, TextNoteAnnotation note,
        bool suppressContext, string? blockText)
    {
        EmitContextBlock(sb, suppressContext, blockText);

        // Use Contents as fallback when NoteText is empty (Adobe Acrobat stores
        // reviewer comments in the /Contents PDF field, not in the popup window).
        var noteText = !string.IsNullOrWhiteSpace(note.NoteText) ? note.NoteText : note.Contents;
        sb.AppendLine($"> **Note:** {noteText}");
        sb.AppendLine($">");
        sb.AppendLine($"> {GetEnrichedLabel(page, note, "note")}");
        sb.AppendLine();
    }

    private void EmitRect(StringBuilder sb, int page, int annotIdx, RectAnnotation rect,
        bool suppressContext, string? blockText)
    {
        var hasImage = TryGetImagePath(page, annotIdx, out var imagePath);

        if (hasImage)
        {
            sb.AppendLine($"![Rectangle annotation, p. {page + 1}]({imagePath})");
            sb.AppendLine();
        }

        if (!suppressContext)
        {
            var rectText = CleanText(rect.Text ?? "");

            if (!string.IsNullOrWhiteSpace(blockText))
            {
                sb.AppendLine($"> {blockText}");
            }
            else if (!string.IsNullOrWhiteSpace(rectText))
            {
                sb.AppendLine($"> {rectText}");
            }
        }

        sb.AppendLine($">");
        sb.AppendLine($"> {GetEnrichedLabel(page, rect, "rectangle")}");
        sb.AppendLine();
    }

    private void EmitFreehand(StringBuilder sb, int page, int annotIdx, FreehandAnnotation freehand,
        bool suppressContext, bool suppressImage, string? blockText)
    {
        var hasImage = TryGetImagePath(page, annotIdx, out var imagePath);

        if (hasImage && !suppressImage)
        {
            sb.AppendLine($"![Freehand annotation, p. {page + 1}]({imagePath})");
            sb.AppendLine();
        }

        EmitContextBlock(sb, suppressContext, blockText);

        if (!hasImage)
        {
            sb.AppendLine($"> *[Freehand drawing — use `--images` to include a screenshot]*");
            sb.AppendLine($">");
        }

        // Suppress the per-stroke label for grouped freehand annotations
        if (!suppressImage)
        {
            sb.AppendLine($"> {GetEnrichedLabel(page, freehand, "freehand")}");
            sb.AppendLine();
        }
    }

    private void EmitCaret(StringBuilder sb, int page, CaretAnnotation caret,
        bool suppressContext, string? blockText)
    {
        EmitContextBlock(sb, suppressContext, blockText);

        if (!string.IsNullOrWhiteSpace(caret.Contents))
        {
            sb.AppendLine($"> **Comment:** {caret.Contents}");
            sb.AppendLine($">");
        }

        sb.AppendLine($"> {GetEnrichedLabel(page, caret, "caret")}");
        sb.AppendLine();
    }

    private void EmitFreeText(StringBuilder sb, int page, FreeTextAnnotation freeText,
        bool suppressContext, string? blockText)
    {
        EmitContextBlock(sb, suppressContext, blockText);

        var content = CleanText(freeText.Contents ?? "");
        if (!string.IsNullOrWhiteSpace(content))
        {
            sb.AppendLine($"> **Free text:** {content}");
            sb.AppendLine(">");
        }

        sb.AppendLine($"> {GetEnrichedLabel(page, freeText, "free text")}");
        sb.AppendLine();
    }

    private bool TryGetImagePath(int page, int annotIdx, out string relativePath)
    {
        relativePath = "";
        if (_images == null || _imageRelDir == null)
            return false;

        if (!_images.TryGetValue((page, annotIdx), out var absPath))
            return false;

        relativePath = Path.Combine(_imageRelDir, Path.GetFileName(absPath));
        return true;
    }

    /// <summary>
    /// Whether the annotation is counted in the summary table's "Other" column.
    /// </summary>
    private static bool IsOtherType(Annotation a) => a is CaretAnnotation or FreeTextAnnotation or UnknownAnnotation;

    /// <summary>
    /// Emits the overlapping-block context text as a blockquote, when not suppressed.
    /// Shared by emit methods that show context above the annotation label.
    /// </summary>
    private static void EmitContextBlock(StringBuilder sb, bool suppressContext, string? blockText)
    {
        if (!suppressContext && !string.IsNullOrWhiteSpace(blockText))
        {
            sb.AppendLine($"> {blockText}");
            sb.AppendLine(">");
        }
    }

    private static string GetBlockText(List<LayoutBlock> blocks)
    {
        if (blocks.Count == 0) return "";

        var ordered = blocks
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .OrderBy(b => b.ReadingOrder ?? 0);

        var combined = string.Join("\n\n", ordered.Select(b => CleanText(b.Text!)));
        return combined;
    }

    /// <summary>
    /// Strips PDF artifacts and collapses whitespace in a single pass.
    /// Returns the cleaned text and a map from normalised positions back to
    /// original string indices.
    /// </summary>
    private static (string text, int[] map) NormalizeWithMap(string original)
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

        // Trim trailing whitespace
        while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
            sb.Length--;
        map[sb.Length] = original.Length;

        return (sb.ToString(), map);
    }

    /// <summary>
    /// Cleans extracted PDF text: removes control characters, soft hyphens,
    /// normalises whitespace, and trims.
    /// </summary>
    internal static string CleanText(string text)
    {
        var (result, _) = NormalizeWithMap(text);
        return result;
    }

    private static string GetEnrichedLabel(int page, Annotation annotation, string baseLabel)
    {
        if (annotation.OverlappingBlocks.Count > 0)
        {
            var blockClass = annotation.OverlappingBlocks[0].Class;
            var qualifier = blockClass switch
            {
                "equation" => $"{baseLabel} {blockClass}",
                "table" => $"{baseLabel} {blockClass}",
                "figure" => $"{baseLabel} {blockClass}",
                _ => null as string
            };
            if (qualifier != null)
                return $"*(p. {page + 1}, {qualifier})*";
        }
        return $"*(p. {page + 1}, {baseLabel})*";
    }

    private string BoldHighlightInContext(string blockText, string highlightText, int page)
    {
        // Tier 1: exact match in block text
        var idx = blockText.IndexOf(highlightText, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return blockText[..idx]
                + "**" + blockText.Substring(idx, highlightText.Length) + "**"
                + blockText[(idx + highlightText.Length)..];
        }

        // Tier 2: fuzzy match (normalized whitespace) in block text
        var (normBlock, blockMap) = NormalizeWithMap(blockText);
        var normHighlight = CleanText(highlightText);

        var normIdx = normBlock.IndexOf(normHighlight, StringComparison.OrdinalIgnoreCase);
        if (normIdx >= 0)
        {
            var origStart = blockMap[normIdx];
            var origEnd = blockMap[normIdx + normHighlight.Length];

            return blockText[..origStart]
                + "**" + blockText[origStart..origEnd] + "**"
                + blockText[origEnd..];
        }

        // Tier 3: try matching against full page context text
        if (_pageContext != null && _pageContext.TryGetValue(page, out var pageText)
            && !string.IsNullOrWhiteSpace(pageText))
        {
            var (normPage, pageMap) = NormalizeWithMap(pageText);
            var pageMatchIdx = normPage.IndexOf(normHighlight, StringComparison.OrdinalIgnoreCase);
            if (pageMatchIdx >= 0 && pageMatchIdx + normHighlight.Length < pageMap.Length)
            {
                var origStart = pageMap[pageMatchIdx];
                var origEnd = pageMap[pageMatchIdx + normHighlight.Length];
                return pageText[..origStart]
                    + "**" + pageText[origStart..origEnd] + "**"
                    + pageText[origEnd..];
            }
        }

        // Last resort
        return $"{blockText}\n>\n> **Highlighted:** {highlightText}";
    }

    private static string HeadingKey(string title, int? page) => $"{title}||{page}";

    private static void BuildHeadingIndex(
        List<OutlineEntry> entries, int depth,
        List<(string key, string title, int depth)> order)
    {
        foreach (var entry in entries)
        {
            var key = HeadingKey(entry.Title, entry.Page);
            order.Add((key, entry.Title, depth));
            BuildHeadingIndex(entry.Children, depth + 1, order);
        }
    }
}
