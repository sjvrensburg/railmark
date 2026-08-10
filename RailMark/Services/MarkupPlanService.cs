using RailMark.Models;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailMark.Services;

public record MarkupEntryResult(int Page, string Quote, MarkupType Type, bool Success, string? Error);

public sealed class MarkupPlanApplyResult
{
    public List<MarkupEntryResult> Entries { get; } = [];

    public bool AllSucceeded => Entries.All(e => e.Success);
}

public static class MarkupPlanService
{
    private const float NoteMarginInset = 24f;

    private static readonly Dictionary<MarkupType, string> DefaultColors = new()
    {
        [MarkupType.Highlight] = "#FFFF00",
        [MarkupType.Underline] = "#00AAFF",
        [MarkupType.Strikeout] = "#FF0000",
        [MarkupType.Squiggly] = "#FF8800",
        [MarkupType.Note] = "#FFCC00",
    };

    /// <summary>
    /// Resolves every entry's quote against the PDF's extracted page text, builds the
    /// corresponding Annotation objects, and merges them into an AnnotationFile (0-based
    /// Pages dict). Does not persist anything — the caller decides whether to Save.
    /// </summary>
    public static (AnnotationFile file, MarkupPlanApplyResult result) Resolve(
        MarkupPlan plan,
        IPdfService pdf,
        IPdfTextService textService,
        AnnotationFile? existing = null,
        string? password = null)
    {
        var file = existing ?? new AnnotationFile();
        var result = new MarkupPlanApplyResult();

        foreach (var group in plan.Entries.GroupBy(e => e.Page))
        {
            var pageIndex = group.Key - 1;
            if (pageIndex < 0 || pageIndex >= pdf.PageCount)
            {
                foreach (var entry in group)
                    result.Entries.Add(new MarkupEntryResult(entry.Page, entry.Quote, entry.Type, false,
                        $"Page {entry.Page} is out of range (document has {pdf.PageCount} pages)."));
                continue;
            }

            var pageText = textService.ExtractPageText(pdf.PdfBytes, pageIndex, password);

            foreach (var entry in group)
            {
                var range = TextLocator.ResolveQuote(pageText.Text, entry.Quote);
                if (range is null)
                {
                    result.Entries.Add(new MarkupEntryResult(entry.Page, entry.Quote, entry.Type, false,
                        $"Quote not found on page {entry.Page}."));
                    continue;
                }

                var rectLines = textService.GetTextRangeRects(
                    pdf.PdfBytes, pageIndex, [(range.Value.CharStart, range.Value.CharLength)], password);
                var rects = MergeLineRects(rectLines.Count > 0 ? rectLines[0] : []);
                if (rects.Count == 0)
                {
                    result.Entries.Add(new MarkupEntryResult(entry.Page, entry.Quote, entry.Type, false,
                        $"Quote matched on page {entry.Page} but produced no geometry."));
                    continue;
                }

                var annotation = BuildAnnotation(entry, rects, pdf, pageIndex);
                if (!file.Pages.TryGetValue(pageIndex, out var pageAnnotations))
                    file.Pages[pageIndex] = pageAnnotations = [];
                pageAnnotations.Add(annotation);

                result.Entries.Add(new MarkupEntryResult(entry.Page, entry.Quote, entry.Type, true, null));
            }
        }

        return (file, result);
    }

    /// <summary>
    /// GetTextRangeRects returns one rect per character, and per-glyph bounding boxes vary in
    /// Top/Bottom (ascenders/descenders), so exact-height grouping doesn't merge same-line
    /// characters. Instead, walk rects in reading order and merge consecutive ones that
    /// vertically overlap the running line's bounding box — characters on the same line always
    /// overlap vertically with their neighbours, while a wrapped-to-next-line rect does not.
    /// </summary>
    private static List<RectF> MergeLineRects(List<RectF> rects)
    {
        if (rects.Count == 0) return rects;

        var merged = new List<RectF>();
        var (left, top, right, bottom) = rects[0];

        foreach (var r in rects.Skip(1))
        {
            bool overlapsVertically = r.Top < bottom && r.Bottom > top;
            if (overlapsVertically)
            {
                left = MathF.Min(left, r.Left);
                right = MathF.Max(right, r.Right);
                top = MathF.Min(top, r.Top);
                bottom = MathF.Max(bottom, r.Bottom);
            }
            else
            {
                merged.Add(new RectF(left, top, right, bottom));
                (left, top, right, bottom) = r;
            }
        }
        merged.Add(new RectF(left, top, right, bottom));

        return merged;
    }

    private static Annotation BuildAnnotation(MarkupEntry entry, List<RectF> rects, IPdfService pdf, int pageIndex)
    {
        var color = entry.Color ?? DefaultColors[entry.Type];
        var author = entry.Author ?? "AI Reviewer";

        if (entry.Type == MarkupType.Note)
        {
            var (pageWidth, _) = pdf.GetPageSize(pageIndex);
            var last = rects[^1];
            return new TextNoteAnnotation
            {
                X = (float)pageWidth - NoteMarginInset,
                Y = (last.Top + last.Bottom) / 2f,
                Text = entry.Comment ?? "",
                Color = color,
                Author = author,
                Source = AnnotationSource.RailReader,
            };
        }

        var highlightRects = rects
            .Select(r => new HighlightRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top))
            .ToList();

        TextMarkupAnnotation annotation = entry.Type switch
        {
            MarkupType.Highlight => new HighlightAnnotation(),
            MarkupType.Underline => new UnderlineAnnotation(),
            MarkupType.Strikeout => new StrikeOutAnnotation(),
            MarkupType.Squiggly => new SquigglyAnnotation(),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Type, "Unhandled markup type."),
        };

        annotation.Rects = highlightRects;
        annotation.Contents = entry.Comment;
        annotation.Color = color;
        annotation.Author = author;
        annotation.Source = AnnotationSource.RailReader;

        return annotation;
    }
}
