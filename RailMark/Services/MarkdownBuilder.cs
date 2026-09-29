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
    private readonly Dictionary<int, NormalizedText> _normPageCache = [];
    // Where each text-markup annotation starts in its page's text, when known — used to pick the
    // right occurrence when the marked text appears more than once on the page.
    private readonly Dictionary<(int page, int annotIdx), int>? _markupOffsets;
    // Vertical position of each heading on its own page, keyed by HeadingKey. Only populated for
    // headings whose title could be located in the page text; absent means page-level ordering.
    private readonly Dictionary<string, float>? _headingPositions;
    // Struck-out text an editor marked for replacement, keyed by the caret annotation that
    // carries the replacement — see BuildCaretReplacements.
    private readonly Dictionary<CaretAnnotation, (int page, int annotIdx)> _caretReplacements = [];
    // (page, annotIdx) of annotations rendered as part of another annotation, not on their own.
    private readonly HashSet<(int page, int annotIdx)> _groupChildren = [];
    // Reply notes, keyed by the annotation they answer, in reading order — rendered beneath it.
    private readonly Dictionary<(int page, int annotIdx), List<(int page, int annotIdx)>> _replies = [];
    // Markup split over a page break: head (page, annotIdx) → its continuation on the next page.
    private Dictionary<(int page, int annotIdx), (int page, int annotIdx)> _continuations = [];

    public MarkdownBuilder(
        AnnotationFile annotations,
        string sourceName,
        List<OutlineEntry> outline,
        Dictionary<(int page, int annotIdx), string>? highlightTexts = null,
        Dictionary<int, string>? pageTexts = null,
        Dictionary<(int page, int annotIdx), string>? images = null,
        string? imageRelDir = null,
        Dictionary<string, float>? headingPositions = null,
        Dictionary<(int page, int annotIdx), int>? markupOffsets = null)
    {
        _annotations = annotations;
        _sourceName = sourceName;
        _outline = outline;
        _highlightTexts = highlightTexts;
        _pageTexts = pageTexts;
        _images = images;
        _imageRelDir = imageRelDir;
        _headingPositions = headingPositions;
        _markupOffsets = markupOffsets;
    }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Annotations: {_sourceName}");
        sb.AppendLine();

        var headings = new HeadingIndex(_outline, _headingPositions);
        var headingOrder = headings.Order;

        BuildCaretReplacements();
        _continuations = ContinuationMarkup.Pair(_annotations);
        foreach (var tail in _continuations.Values)
            _groupChildren.Add(tail);
        BuildReplyThreads();

        // Group annotations by heading
        var grouped = new Dictionary<string, List<(int page, int annotIdx, Annotation annotation)>>();

        foreach (var (pageIdx, annotations) in _annotations.Pages)
        {
            for (int i = 0; i < annotations.Count; i++)
            {
                var annotation = annotations[i];
                var headingKey = headings.KeyFor(pageIdx, HeadingIndex.SortY(annotation));

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
                return pageCmp != 0 ? pageCmp : HeadingIndex.SortY(a.annotation).CompareTo(HeadingIndex.SortY(b.annotation));
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
        if (grouped.TryGetValue(HeadingIndex.NoHeadingKey, out var ungrouped))
        {
            sb.AppendLine("## Other Annotations");
            sb.AppendLine();
            EmitAnnotationGroup(sb, ungrouped);
        }

        return sb.ToString();
    }

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

    /// <summary>
    /// Threads reply notes under the annotation they answer. A reply whose parent is not in the
    /// file, or is itself rendered as part of another entry, stays where it is as an ordinary note.
    /// </summary>
    private void BuildReplyThreads()
    {
        var byId = new Dictionary<string, (int page, int annotIdx)>(StringComparer.Ordinal);
        foreach (var (pageIdx, annotations) in _annotations.Pages)
            for (int i = 0; i < annotations.Count; i++)
                if (!string.IsNullOrEmpty(annotations[i].NativeId))
                    byId.TryAdd(annotations[i].NativeId!, (pageIdx, i));

        var replies = new List<((int page, int annotIdx) reply, (int page, int annotIdx) parent)>();
        foreach (var (pageIdx, annotations) in _annotations.Pages)
        {
            for (int i = 0; i < annotations.Count; i++)
            {
                if (annotations[i] is not TextNoteAnnotation note) continue;
                if (ReplyMarkup.ParentOf(note) is not { } parentId) continue;
                if (!byId.TryGetValue(parentId, out var parent) || _groupChildren.Contains(parent)) continue;
                replies.Add(((pageIdx, i), parent));
            }
        }

        foreach (var (reply, parent) in replies
                     .OrderBy(r => r.reply.page)
                     .ThenBy(r => HeadingIndex.SortY(_annotations.Pages[r.reply.page][r.reply.annotIdx])))
        {
            if (!_replies.TryGetValue(parent, out var list))
                _replies[parent] = list = [];
            list.Add(reply);
            _groupChildren.Add(reply);
        }
    }

    private void EmitReplies(StringBuilder sb, (int page, int annotIdx) parent, int depth = 1)
    {
        if (!_replies.TryGetValue(parent, out var replies)) return;

        var quote = string.Concat(Enumerable.Repeat("> ", depth + 1)).TrimEnd();
        foreach (var reply in replies)
        {
            var note = _annotations.Pages[reply.page][reply.annotIdx];
            var author = string.IsNullOrWhiteSpace(note.Author) ? "" : $" — {note.Author}";
            sb.AppendLine($"{quote} **Reply{author}:** {note.EffectiveContents}");
            sb.AppendLine();
            EmitReplies(sb, reply, depth + 1);
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

        if (grouped.TryGetValue(HeadingIndex.NoHeadingKey, out var ug))
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
            EmitReplies(sb, (page, annotIdx));
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
    {
        var text = CleanText(_highlightTexts != null && _highlightTexts.TryGetValue((page, annotIdx), out var t) ? t : "");
        // A markup that runs over a page break reads as one, with its continuation's text.
        if (_continuations.TryGetValue((page, annotIdx), out var tail))
            text = ContinuationMarkup.JoinText(text, GetMarkupText(tail.page, tail.annotIdx));
        return text;
    }

    /// <summary>"p. 3" — or "pp. 3–4" for a markup that runs over a page break.</summary>
    private string PageSpan(int page, int annotIdx)
        => _continuations.TryGetValue((page, annotIdx), out var tail)
            ? $"pp. {page + 1}–{tail.page + 1}"
            : $"p. {page + 1}";

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
            // A highlight continued on the next page doesn't sit within one page's text.
            var pageText = _continuations.ContainsKey((page, annotIdx)) ? null : _pageTexts?.GetValueOrDefault(page);
            if (pageText != null)
            {
                // The context is page text emitted verbatim, so it still carries the extractor's
                // invisible de-hyphenation markers. Strip them after the offsets have been used.
                var bolded = TextLocator.StripInvisibleMarkers(
                    BoldHighlightInContext(page, annotIdx, pageText, highlightedText));
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
        sb.AppendLine($"> *({PageSpan(page, annotIdx)}, highlight)*");
        sb.AppendLine();
    }

    /// <summary>
    /// Underline, strikeout and squiggly markup. These carry an editorial intent the label spells
    /// out, following the vocabulary pdfannots uses for the same annotation types.
    /// </summary>
    private void EmitTextMarkup(StringBuilder sb, int page, int annotIdx, TextMarkupAnnotation markup)
    {
        var markedText = GetMarkupText(page, annotIdx);

        if (ReplacementMarkup.TryParse(markup, out var replacement, out var replacementComment))
        {
            sb.AppendLine(!string.IsNullOrWhiteSpace(markedText)
                ? $"> ~~{markedText}~~ → **{CleanText(replacement)}**"
                : $"> Replace with **{CleanText(replacement)}**");
            if (!string.IsNullOrWhiteSpace(replacementComment))
            {
                sb.AppendLine(">");
                sb.AppendLine($"> **Comment:** {replacementComment}");
            }
            sb.AppendLine(">");
            sb.AppendLine($"> *({PageSpan(page, annotIdx)}, suggested replacement)*");
            sb.AppendLine();
            return;
        }

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
        sb.AppendLine($"> *({PageSpan(page, annotIdx)}, {label})*");
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
        // Markdown link targets are URLs, so the separator is always '/'. Path.Combine would
        // emit a backslash on Windows, producing a link no renderer resolves.
        relativePath = $"{_imageRelDir.Replace('\\', '/')}/{Path.GetFileName(absPath)}";
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

    private string BoldHighlightInContext(int page, int annotIdx, string pageText, string highlightText)
    {
        // Normalised matching covers the verbatim case too; cache per page to avoid
        // O(highlights × pageLen) normalisation.
        if (!_normPageCache.TryGetValue(page, out var normPage))
            _normPageCache[page] = normPage = NormalizedText.From(pageText);

        var matches = TextLocator.FindQuoteMatches(normPage, highlightText);
        if (matches.Count > 0)
        {
            // The same words can appear several times on a page; bold the occurrence the
            // annotation actually covers when its position is known.
            var match = _markupOffsets != null && _markupOffsets.TryGetValue((page, annotIdx), out var offset)
                ? matches.MinBy(m => Math.Abs(m.CharStart - offset))
                : matches[0];
            var end = match.CharStart + match.CharLength;
            return pageText[..match.CharStart]
                + "**" + pageText[match.CharStart..end] + "**"
                + pageText[end..];
        }

        // Fall back to just the highlighted text bolded
        return $"**{highlightText}**";
    }

    /// <summary>Stable identity for an outline entry — see <see cref="HeadingIndex.Key"/>.</summary>
    internal static string HeadingKey(string title, int? page) => HeadingIndex.Key(title, page);

    internal static string CleanText(string text) => TextLocator.CleanText(text);
}
