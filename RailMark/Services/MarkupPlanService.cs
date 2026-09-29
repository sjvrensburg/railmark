using System.Text.Json.Serialization;
using RailMark.Models;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailMark.Services;

public record MarkupEntryResult(int Page, string Quote, MarkupType Type, bool Success, string? Error, int SpanCount = 0)
{
    /// <summary>How many times the quote occurs on the page, before any context filtering.</summary>
    public int MatchCount { get; init; }

    /// <summary>Set when the entry was applied but may not have landed where intended.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Warning { get; init; }

    /// <summary>The text around the match with the match bracketed; populated on request (dry run).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; init; }
}

public sealed class MarkupPlanApplyResult
{
    public List<MarkupEntryResult> Entries { get; } = [];

    public bool AllSucceeded => Entries.All(e => e.Success);
}

public static class MarkupPlanService
{
    private const float NoteMarginInset = 24f;

    // Drawn from RailReader2's own five-colour palette, using the same per-tool default hue, so
    // AI-authored markup is indistinguishable in colour from markup a reader adds by hand — and
    // `--color` picks both up together. Core keeps the per-tool indices private, so they are
    // mirrored here.
    private static readonly Dictionary<MarkupType, string> DefaultColors = new()
    {
        [MarkupType.Highlight] = AnnotationInteractionHandler.AnnotationColors[0], // Yellow
        [MarkupType.Underline] = AnnotationInteractionHandler.AnnotationColors[1], // Green
        [MarkupType.Strikeout] = AnnotationInteractionHandler.AnnotationColors[2], // Red
        [MarkupType.Squiggly] = AnnotationInteractionHandler.AnnotationColors[1],  // Green
        [MarkupType.Note] = AnnotationInteractionHandler.AnnotationColors[0],      // Yellow
        // Not a RailReader2 tool: blue keeps a suggested replacement visibly distinct from a
        // plain (red) deletion.
        [MarkupType.Replace] = AnnotationInteractionHandler.AnnotationColors[3],   // Blue
    };

    /// <summary>Where an entry's quote landed: a character range on a 0-based page.</summary>
    private readonly record struct Placement(int PageIndex, int CharStart, int CharLength);

    /// <summary>
    /// Resolves every entry's quote against the PDF's extracted page text, builds the
    /// corresponding Annotation objects, and merges them into an AnnotationFile (0-based
    /// Pages dict). Does not persist anything — the caller decides whether to Save.
    /// </summary>
    /// <param name="includeContext">Report the text around each match (for dry runs).</param>
    public static (AnnotationFile file, MarkupPlanApplyResult result) Resolve(
        MarkupPlan plan,
        IPdfService pdf,
        IPdfTextService textService,
        AnnotationFile? existing = null,
        string? password = null,
        bool includeContext = false)
    {
        var file = existing ?? new AnnotationFile();
        var pages = new Dictionary<int, NormalizedText>();
        NormalizedText PageText(int pageIndex)
        {
            if (!pages.TryGetValue(pageIndex, out var text))
                pages[pageIndex] = text = NormalizedText.From(
                    textService.ExtractPageText(pdf.PdfBytes, pageIndex, password).Text);
            return text;
        }

        // Pass 1 — locate every quote. Results are indexed by original plan position so the
        // report preserves plan order regardless of how geometry lookups are batched.
        var results = new MarkupEntryResult?[plan.Entries.Count];
        var placements = new Placement?[plan.Entries.Count];
        for (int index = 0; index < plan.Entries.Count; index++)
        {
            var entry = plan.Entries[index];
            var pageIndex = entry.Page - 1;
            if (pageIndex < 0 || pageIndex >= pdf.PageCount)
            {
                results[index] = Failure(entry,
                    $"Page {entry.Page} is out of range (document has {pdf.PageCount} pages).");
                continue;
            }

            if (Validate(entry) is { } invalid)
            {
                results[index] = Failure(entry, invalid);
                continue;
            }

            var page = PageText(pageIndex);
            var (match, failure) = Locate(entry, page);
            if (match is null)
            {
                results[index] = failure;
                continue;
            }

            placements[index] = new Placement(pageIndex, match.Value.Match.CharStart, match.Value.Match.CharLength);
            results[index] = new MarkupEntryResult(entry.Page, entry.Quote, entry.Type, true, null)
            {
                MatchCount = match.Value.MatchCount,
                Warning = match.Value.Warning,
                Context = includeContext ? TextLocator.DescribeContext(page, match.Value.Match) : null,
            };
        }

        // Pass 2 — fetch geometry in a single batched GetTextRangeRects call per page instead of
        // one native call per entry.
        var geometry = new List<RectF>?[plan.Entries.Count];
        foreach (var group in Enumerable.Range(0, plan.Entries.Count)
                     .Where(i => placements[i] is not null)
                     .GroupBy(i => placements[i]!.Value.PageIndex))
        {
            var indices = group.ToList();
            var rectLines = textService.GetTextRangeRects(
                pdf.PdfBytes, group.Key,
                indices.Select(i => (placements[i]!.Value.CharStart, placements[i]!.Value.CharLength)).ToList(),
                password);
            for (int k = 0; k < indices.Count; k++)
                geometry[indices[k]] = MergeLineRects(k < rectLines.Count ? rectLines[k] : []);
        }

        // Pass 3 — build and merge annotations, in plan order.
        for (int index = 0; index < plan.Entries.Count; index++)
        {
            if (placements[index] is not { } placement) continue;
            var entry = plan.Entries[index];
            var rects = geometry[index] ?? [];
            if (rects.Count == 0)
            {
                results[index] = Failure(entry, $"Quote matched on page {entry.Page} but produced no geometry.")
                    with { MatchCount = results[index]!.MatchCount };
                continue;
            }

            var annotation = BuildAnnotation(entry, rects, pdf, placement.PageIndex);
            if (!file.Pages.TryGetValue(placement.PageIndex, out var pageAnnotations))
                file.Pages[placement.PageIndex] = pageAnnotations = [];
            pageAnnotations.Add(annotation);

            results[index] = results[index]! with { SpanCount = rects.Count };
        }

        var result = new MarkupPlanApplyResult();
        result.Entries.AddRange(results!);
        return (file, result);
    }

    private static MarkupEntryResult Failure(MarkupEntry entry, string error)
        => new(entry.Page, entry.Quote, entry.Type, false, error);

    /// <summary>
    /// Picks the match an entry refers to. Context narrows the candidates, <c>occurrence</c> then
    /// indexes into what is left; with neither, the first match wins — but a quote that occurs
    /// more than once is flagged, since the first match is only a guess at what was meant.
    /// </summary>
    private static ((QuoteMatch Match, int MatchCount, string? Warning)? Located, MarkupEntryResult? Failure) Locate(
        MarkupEntry entry, NormalizedText page)
    {
        if (entry.Occurrence is < 1)
            return (null, Failure(entry, $"occurrence must be 1 or greater (got {entry.Occurrence})."));

        var all = TextLocator.FindQuoteMatches(page, entry.Quote);
        if (all.Count == 0)
            return (null, Failure(entry, $"Quote not found on page {entry.Page}."));

        bool hasContext = !string.IsNullOrWhiteSpace(entry.ContextBefore) || !string.IsNullOrWhiteSpace(entry.ContextAfter);
        var candidates = hasContext
            ? all.Where(m => TextLocator.HasContext(page, m, entry.ContextBefore, entry.ContextAfter)).ToList()
            : all;

        if (candidates.Count == 0)
            return (null, Failure(entry,
                $"Quote occurs {Times(all.Count)} on page {entry.Page}, but never with the given contextBefore/contextAfter.")
                with { MatchCount = all.Count });

        if (entry.Occurrence is { } occurrence)
        {
            if (occurrence > candidates.Count)
                return (null, Failure(entry,
                    $"occurrence {occurrence} requested, but the quote occurs only {Times(candidates.Count)} on page {entry.Page}"
                    + (hasContext ? " with the given context." : "."))
                    with { MatchCount = all.Count });
            return ((candidates[occurrence - 1], all.Count, null), null);
        }

        string? warning = candidates.Count > 1
            ? $"Ambiguous: the quote occurs {Times(candidates.Count)} on page {entry.Page}"
              + (hasContext ? " with the given context" : "")
              + "; marked the first. Set occurrence, add contextBefore/contextAfter, or lengthen the quote."
            : null;
        return ((candidates[0], all.Count, warning), null);
    }

    /// <summary>Checks the fields that only make sense for some types; null when the entry is valid.</summary>
    private static string? Validate(MarkupEntry entry)
    {
        if (entry.Type == MarkupType.Replace && string.IsNullOrEmpty(entry.Replacement))
            return "A replace entry needs a non-empty replacement (use strikeout to suggest a deletion).";
        if (entry.Type != MarkupType.Replace && entry.Replacement != null)
            return $"replacement is only valid with type replace (this entry is {entry.Type.ToString().ToLowerInvariant()}).";
        return null;
    }

    private static string Times(int n) => n == 1 ? "once" : $"{n} times";

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

        // Highlight/StrikeOut are drawn across the full glyph box, but Underline/Squiggly are a
        // stroke beneath the text, so passing them the same full-height quad draws the line at
        // cap-height and it reads as a strike-through instead of an underline (issue #13).
        bool baselineBand = entry.Type is MarkupType.Underline or MarkupType.Squiggly;
        var markupRects = rects
            .Select(r =>
            {
                if (!baselineBand)
                    return new HighlightRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

                var height = r.Bottom - r.Top;
                var bandHeight = MathF.Max(2f, height * 0.15f);
                return new HighlightRect(r.Left, r.Bottom - bandHeight, r.Right - r.Left, bandHeight);
            })
            .ToList();

        TextMarkupAnnotation annotation = entry.Type switch
        {
            MarkupType.Highlight => new HighlightAnnotation(),
            MarkupType.Underline => new UnderlineAnnotation(),
            MarkupType.Strikeout or MarkupType.Replace => new StrikeOutAnnotation(),
            MarkupType.Squiggly => new SquigglyAnnotation(),
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Type, "Unhandled markup type."),
        };

        annotation.Rects = markupRects;
        if (entry.Type == MarkupType.Replace)
        {
            annotation.Subject = ReplacementMarkup.Subject;
            annotation.Contents = ReplacementMarkup.FormatContents(entry.Replacement!, entry.Comment);
        }
        else
        {
            annotation.Contents = entry.Comment;
        }
        annotation.Color = color;
        annotation.Author = author;
        annotation.Source = AnnotationSource.RailReader;

        return annotation;
    }
}
