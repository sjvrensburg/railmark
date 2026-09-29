using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Models;

namespace RailMark.Services;

/// <summary>The document-level JSON written by annotation mode's <c>--format json</c>.</summary>
public sealed record AnnotationExport(string Source, List<AnnotationRecord> Annotations);

/// <summary>
/// One annotation, shaped like a markup-plan entry — <c>page</c> is 1-based and <c>type</c>,
/// <c>quote</c>, <c>comment</c>, <c>replacement</c>, <c>color</c> and <c>author</c> mean what they
/// mean in a plan — so an agent can read a reviewer's markup and answer it without translating.
/// </summary>
public sealed record AnnotationRecord
{
    public required int Page { get; init; }
    /// <summary>Set when the markup runs over a page break: the 1-based page it ends on.</summary>
    public int? EndPage { get; init; }
    public required string Type { get; init; }
    /// <summary>The annotation's /NM — what a plan entry's <c>inReplyTo</c> refers to.</summary>
    public string? Id { get; init; }
    public string? Quote { get; init; }
    /// <summary>Set when the quote occurs more than once on the page: which match was marked.</summary>
    public int? Occurrence { get; init; }
    public string? Comment { get; init; }
    public string? Replacement { get; init; }
    public string? Color { get; init; }
    public string? Author { get; init; }
    public string? Heading { get; init; }
    public string? InReplyTo { get; init; }
    public string? Subject { get; init; }
    public string? State { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? Image { get; init; }
}

/// <summary>
/// Annotation mode's machine-readable output: the same annotations, headings and reading order as
/// <see cref="MarkdownBuilder"/>, as structured records instead of prose.
/// </summary>
public class AnnotationJsonBuilder(
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
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        // Quotes are prose; keep non-ASCII characters and punctuation readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<int, NormalizedText> _normPageCache = [];

    public string Build() => JsonSerializer.Serialize(BuildExport(), Options) + Environment.NewLine;

    public AnnotationExport BuildExport()
    {
        var headings = new HeadingIndex(outline, headingPositions);
        // A markup split over a page break is reported once, as its first half with the whole
        // quote, so the record re-applies as a single plan entry.
        var continuations = ContinuationMarkup.Pair(annotations);
        var folded = continuations.Values.ToHashSet();

        var ordered = annotations.Pages
            .SelectMany(p => p.Value.Select((a, i) => (page: p.Key, annotIdx: i, annotation: a)))
            .OrderBy(t => t.page)
            .ThenBy(t => HeadingIndex.SortY(t.annotation));

        var records = new List<AnnotationRecord>();
        foreach (var (page, annotIdx, annotation) in ordered)
        {
            if (folded.Contains((page, annotIdx))) continue;

            var quote = annotation is TextMarkupAnnotation ? MarkupText(page, annotIdx) : null;
            int? endPage = null;
            if (continuations.TryGetValue((page, annotIdx), out var tail))
            {
                endPage = tail.page + 1;
                if (quote != null && MarkupText(tail.page, tail.annotIdx) is { } tailText)
                    quote = ContinuationMarkup.JoinText(quote, tailText);
            }
            string? comment = annotation.EffectiveContents;
            string? replacement = null;
            if (ReplacementMarkup.TryParse(annotation, out var parsedReplacement, out var parsedComment))
            {
                replacement = parsedReplacement;
                comment = parsedComment;
            }

            records.Add(new AnnotationRecord
            {
                Page = page + 1,
                EndPage = endPage,
                Type = replacement != null ? "replace" : TypeName(annotation),
                Id = NullIfEmpty(annotation.NativeId),
                Quote = quote,
                Occurrence = quote != null && endPage == null ? Occurrence(page, annotIdx, quote) : null,
                Comment = NullIfEmpty(comment),
                Replacement = replacement,
                Color = annotation.Color,
                Author = NullIfEmpty(annotation.Author),
                Heading = headings.TitleOf(headings.KeyFor(page, HeadingIndex.SortY(annotation))),
                InReplyTo = NullIfEmpty(ReplyMarkup.ParentOf(annotation)),
                // The replacement tag is RailMark's own bookkeeping, already expressed by type.
                Subject = replacement != null ? null : NullIfEmpty(annotation.Subject),
                State = annotation.State == ReviewState.None ? null : annotation.State.ToString().ToLowerInvariant(),
                Created = annotation.CreatedUtc,
                Modified = annotation.ModifiedUtc,
                Image = ImagePath(page, annotIdx),
            });
        }

        return new AnnotationExport(sourceName, records);
    }

    /// <summary>The name a markup plan uses for the type, or a descriptive one for the rest.</summary>
    internal static string TypeName(Annotation annotation) => annotation switch
    {
        HighlightAnnotation => "highlight",
        UnderlineAnnotation => "underline",
        StrikeOutAnnotation => "strikeout",
        SquigglyAnnotation => "squiggly",
        TextNoteAnnotation => "note",
        RectAnnotation => "rect",
        FreehandAnnotation => "freehand",
        CaretAnnotation => "caret",
        FreeTextAnnotation => "freetext",
        _ => annotation.GetType().Name,
    };

    private string? MarkupText(int page, int annotIdx)
        => highlightTexts != null && highlightTexts.TryGetValue((page, annotIdx), out var text)
            ? NullIfEmpty(TextLocator.CleanText(text))
            : null;

    /// <summary>
    /// When the marked text occurs more than once on its page, the 1-based index of the match the
    /// annotation covers — so the record can be turned back into an unambiguous plan entry.
    /// </summary>
    private int? Occurrence(int page, int annotIdx, string quote)
    {
        if (pageTexts == null || !pageTexts.TryGetValue(page, out var pageText)) return null;
        if (markupOffsets == null || !markupOffsets.TryGetValue((page, annotIdx), out var offset)) return null;

        if (!_normPageCache.TryGetValue(page, out var normPage))
            _normPageCache[page] = normPage = NormalizedText.From(pageText);

        var matches = TextLocator.FindQuoteMatches(normPage, quote);
        if (matches.Count < 2) return null;

        var nearest = matches.MinBy(m => Math.Abs(m.CharStart - offset));
        return matches.IndexOf(nearest) + 1;
    }

    private string? ImagePath(int page, int annotIdx)
    {
        if (images == null || imageRelDir == null) return null;
        if (!images.TryGetValue((page, annotIdx), out var absPath)) return null;
        return $"{imageRelDir.Replace('\\', '/')}/{Path.GetFileName(absPath)}";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
