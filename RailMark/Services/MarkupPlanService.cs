using System.Text.Json.Serialization;
using RailMark.Models;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailMark.Services;

public record MarkupEntryResult(int Page, string? Quote, MarkupType Type, bool Success, string? Error, int SpanCount = 0)
{
    /// <summary>How many times the quote occurs on the page, before any context filtering.</summary>
    public int MatchCount { get; init; }

    /// <summary>Set when the entry was applied but may not have landed where intended.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Warning { get; init; }

    /// <summary>
    /// What applying the entry did: <c>added</c> a new annotation, left an identical one
    /// <c>unchanged</c>, or <c>updated</c> the comment/colour of one already covering the same
    /// text. Null for a failed entry.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; init; }

    /// <summary>The id of the annotation the entry replied to, echoed from the plan.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InReplyTo { get; init; }

    /// <summary>
    /// Set when the quote runs over a page break: the 1-based page it ends on. The markup is then
    /// split across the two pages.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EndPage { get; init; }

    /// <summary>The text around the match with the match bracketed; populated on request (dry run).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; init; }
}

public sealed class MarkupPlanApplyResult
{
    public List<MarkupEntryResult> Entries { get; } = [];

    /// <summary>Annotations removed up front because their author was named for replacement.</summary>
    public int Removed { get; set; }

    public bool AllSucceeded => Entries.All(e => e.Success);
}

public static class MarkupPlanService
{
    private const float NoteMarginInset = 24f;

    /// <summary>Vertical step, in points, between successive reply notes beside one annotation.</summary>
    private const float ReplyStep = 20f;

    /// <summary>
    /// How far apart, in points, two annotations' geometry may be and still count as covering the
    /// same text. Geometry round-trips through the PDF's own coordinate space, so an annotation
    /// read back is not bit-identical to the one written — but a different line of text is a full
    /// line height away.
    /// </summary>
    private const float SameGeometryTolerance = 1.5f;

    public const string ActionAdded = "added";
    public const string ActionUnchanged = "unchanged";
    public const string ActionUpdated = "updated";

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
    /// <remarks>
    /// Applying is idempotent: an entry whose annotation already exists — same type, author and
    /// geometry — is not added a second time, so re-running a plan, or a plan that repeats an
    /// entry, does not stack duplicates.
    /// </remarks>
    /// <param name="includeContext">Report the text around each match (for dry runs).</param>
    /// <param name="replaceAuthor">
    /// Remove every existing annotation by this author before applying — for revising an earlier
    /// round of markup rather than adding to it.
    /// </param>
    public static (AnnotationFile file, MarkupPlanApplyResult result) Resolve(
        MarkupPlan plan,
        IPdfService pdf,
        IPdfTextService textService,
        AnnotationFile? existing = null,
        string? password = null,
        bool includeContext = false,
        string? replaceAuthor = null)
    {
        var file = existing ?? new AnnotationFile();
        var removed = replaceAuthor is null ? 0 : RemoveByAuthor(file, replaceAuthor);
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
        var placements = new List<Placement>?[plan.Entries.Count];
        var replyAnchors = new Annotation?[plan.Entries.Count];
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

            if (entry.InReplyTo is { } parentId)
            {
                var parent = file.Pages.GetValueOrDefault(pageIndex)?.FirstOrDefault(a => a.NativeId == parentId);
                if (parent is null)
                {
                    var elsewhere = file.Pages.FirstOrDefault(p => p.Value.Any(a => a.NativeId == parentId));
                    results[index] = Failure(entry, elsewhere.Value is not null
                        ? $"Annotation {parentId} is on page {elsewhere.Key + 1}, not page {entry.Page}."
                        : $"No annotation with id {parentId} on page {entry.Page}"
                          + (replaceAuthor is null ? "." : $" (it may have been removed by --replace-author \"{replaceAuthor}\")."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Quote))
                {
                    // Nothing to locate: the reply is anchored to its parent.
                    replyAnchors[index] = parent;
                    results[index] = new MarkupEntryResult(entry.Page, entry.Quote, entry.Type, true, null)
                    {
                        InReplyTo = parentId,
                        Context = includeContext
                            ? $"reply to the {AnnotationJsonBuilder.TypeName(parent)} by {parent.Author ?? "unknown author"}"
                            : null,
                    };
                    continue;
                }
            }

            var page = PageText(pageIndex);
            var (match, failure) = Locate(entry, page);
            if (match is not null)
            {
                placements[index] = [new Placement(pageIndex, match.Value.Match.CharStart, match.Value.Match.CharLength)];
                results[index] = new MarkupEntryResult(entry.Page, entry.Quote, entry.Type, true, null)
                {
                    InReplyTo = entry.InReplyTo,
                    MatchCount = match.Value.MatchCount,
                    Warning = match.Value.Warning,
                    Context = includeContext ? TextLocator.DescribeContext(page, match.Value.Match) : null,
                };
                continue;
            }

            // Not on the page as a whole: the quote may run on over the page break.
            if (failure!.MatchCount == 0 && entry.Occurrence is null or 1 && pageIndex + 1 < pdf.PageCount)
            {
                var nextPage = PageText(pageIndex + 1);
                if (TextLocator.FindAcrossPageBreak(page, nextPage, entry.Quote!) is { } split
                    && TextLocator.HasContext(page, split.Head, entry.ContextBefore, null)
                    && TextLocator.HasContext(nextPage, split.Tail, null, entry.ContextAfter))
                {
                    placements[index] =
                    [
                        new Placement(pageIndex, split.Head.CharStart, split.Head.CharLength),
                        new Placement(pageIndex + 1, split.Tail.CharStart, split.Tail.CharLength),
                    ];
                    results[index] = new MarkupEntryResult(entry.Page, entry.Quote, entry.Type, true, null)
                    {
                        InReplyTo = entry.InReplyTo,
                        MatchCount = 1,
                        EndPage = entry.Page + 1,
                        Context = includeContext
                            ? TextLocator.DescribeContext(page, split.Head) + $" ‖ page {entry.Page + 1}: "
                              + TextLocator.DescribeContext(nextPage, split.Tail)
                            : null,
                    };
                    continue;
                }
            }

            results[index] = failure;
        }

        // Pass 2 — fetch geometry in a single batched GetTextRangeRects call per page instead of
        // one native call per entry.
        var geometry = new List<RectF>[plan.Entries.Count][];
        for (int index = 0; index < plan.Entries.Count; index++)
            geometry[index] = new List<RectF>[placements[index]?.Count ?? 0];

        var parts = Enumerable.Range(0, plan.Entries.Count)
            .Where(i => placements[i] is not null)
            .SelectMany(i => placements[i]!.Select((placement, part) => (index: i, part, placement)));
        foreach (var group in parts.GroupBy(p => p.placement.PageIndex))
        {
            var onPage = group.ToList();
            var rectLines = textService.GetTextRangeRects(
                pdf.PdfBytes, group.Key,
                onPage.Select(p => (p.placement.CharStart, p.placement.CharLength)).ToList(),
                password);
            for (int k = 0; k < onPage.Count; k++)
                geometry[onPage[k].index][onPage[k].part] = MergeLineRects(k < rectLines.Count ? rectLines[k] : []);
        }

        // Pass 3 — build and merge annotations, in plan order.
        for (int index = 0; index < plan.Entries.Count; index++)
        {
            var entry = plan.Entries[index];
            if (replyAnchors[index] is { } anchor)
            {
                var pageIndex = entry.Page - 1;
                var note = BuildReplyNote(entry, anchor, file.Pages[pageIndex], pdf, pageIndex);
                results[index] = results[index]! with { Action = Merge(file, pageIndex, note) };
                continue;
            }

            if (placements[index] is not { } entryPlacements) continue;
            if (geometry[index].Any(rects => rects.Count == 0))
            {
                results[index] = Failure(entry, $"Quote matched on page {entry.Page} but produced no geometry.")
                    with { MatchCount = results[index]!.MatchCount };
                continue;
            }

            // A note is a single marker, pinned beside where the quote starts; markup covers
            // every part, with the comment carried by the first alone so it isn't repeated.
            var actions = new List<string>();
            int partsToBuild = entry.Type == MarkupType.Note ? 1 : entryPlacements.Count;
            for (int part = 0; part < partsToBuild; part++)
            {
                var pageIndex = entryPlacements[part].PageIndex;
                var annotation = part == 0
                    ? BuildAnnotation(entry, geometry[index][part], pdf, pageIndex)
                    : BuildContinuation(entry, geometry[index][part]);
                if (part == 0 && entry.InReplyTo is { } parentId)
                    MarkAsReply(annotation, parentId);
                actions.Add(Merge(file, pageIndex, annotation));
            }

            results[index] = results[index]! with
            {
                SpanCount = geometry[index].Sum(rects => rects.Count),
                Action = actions.All(a => a == ActionUnchanged) ? ActionUnchanged
                    : actions.Contains(ActionAdded) ? ActionAdded
                    : ActionUpdated,
            };
        }

        var result = new MarkupPlanApplyResult { Removed = removed };
        result.Entries.AddRange(results!);
        return (file, result);
    }

    /// <summary>
    /// Removes every annotation by <paramref name="author"/>. Emptied pages keep their (now empty)
    /// list rather than losing the key: the PDF writer only reconciles pages present in the file,
    /// so a dropped key would leave the old annotations in the document.
    /// </summary>
    private static int RemoveByAuthor(AnnotationFile file, string author)
    {
        int removed = 0;
        foreach (var annotations in file.Pages.Values)
            removed += annotations.RemoveAll(a => string.Equals(a.Author, author, StringComparison.Ordinal));
        return removed;
    }

    /// <summary>
    /// Adds <paramref name="annotation"/> to its page unless an annotation of the same kind, by the
    /// same author, already covers the same text there. A match whose comment or colour differs is
    /// brought up to date instead of being duplicated.
    /// </summary>
    private static string Merge(AnnotationFile file, int pageIndex, Annotation annotation)
    {
        if (!file.Pages.TryGetValue(pageIndex, out var pageAnnotations))
            file.Pages[pageIndex] = pageAnnotations = [];

        var twin = pageAnnotations.FirstOrDefault(a => IsSameMarkup(a, annotation));
        if (twin is null)
        {
            pageAnnotations.Add(annotation);
            return ActionAdded;
        }

        bool sameContents = NormalizeContents(twin.EffectiveContents) == NormalizeContents(annotation.EffectiveContents);
        bool sameColor = string.Equals(twin.Color, annotation.Color, StringComparison.OrdinalIgnoreCase);
        if (sameContents && sameColor)
            return ActionUnchanged;

        twin.Contents = annotation.EffectiveContents;
        if (twin is TextNoteAnnotation note)
            note.Text = annotation.EffectiveContents;
        twin.Color = annotation.Color;
        // The reader's faithful colour components would otherwise mask the new hex colour.
        twin.ColorComponents = null;
        return ActionUpdated;
    }

    private static bool IsSameMarkup(Annotation a, Annotation b)
    {
        if (a.GetType() != b.GetType()) return false;
        if (!string.Equals(a.Author, b.Author, StringComparison.Ordinal)) return false;

        // A reply is identified by what it answers and what it says, not where it sits: one
        // parent can collect several replies from the same author, and where each is pinned
        // depends on how many came before it.
        var parentA = ReplyMarkup.ParentOf(a);
        var parentB = ReplyMarkup.ParentOf(b);
        if (parentA != null || parentB != null)
            return parentA == parentB
                && NormalizeContents(a.EffectiveContents) == NormalizeContents(b.EffectiveContents);

        // A suggested replacement and a plain deletion over the same words are different edits,
        // and so are a markup and the continuation of one that started on the previous page.
        if (ReplacementMarkup.TryParse(a, out _, out _) != ReplacementMarkup.TryParse(b, out _, out _)) return false;
        if (ContinuationMarkup.Is(a) != ContinuationMarkup.Is(b)) return false;

        return (a, b) switch
        {
            (TextMarkupAnnotation ma, TextMarkupAnnotation mb) =>
                ma.Rects.Count == mb.Rects.Count
                && ma.Rects.Zip(mb.Rects).All(p =>
                    Near(p.First.X, p.Second.X) && Near(p.First.Y, p.Second.Y)
                    && Near(p.First.W, p.Second.W) && Near(p.First.H, p.Second.H)),
            (TextNoteAnnotation na, TextNoteAnnotation nb) => Near(na.X, nb.X) && Near(na.Y, nb.Y),
            _ => false,
        };
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) <= SameGeometryTolerance;

    // Viewers may rewrite line breaks when they save.
    private static string NormalizeContents(string contents) => contents.Replace("\r\n", "\n").Replace('\r', '\n');

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

        var all = TextLocator.FindQuoteMatches(page, entry.Quote!);
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
        if (string.IsNullOrWhiteSpace(entry.Quote))
        {
            if (entry.InReplyTo is null)
                return "quote is required (it may be omitted only by a note replying to an annotation via inReplyTo).";
            if (entry.Type != MarkupType.Note)
                return $"A reply without a quote must be a note (this entry is {entry.Type.ToString().ToLowerInvariant()}).";
            if (entry.Occurrence != null || entry.ContextBefore != null || entry.ContextAfter != null)
                return "occurrence, contextBefore and contextAfter need a quote to apply to.";
        }

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

    private static void MarkAsReply(Annotation annotation, string parentId)
    {
        annotation.InReplyTo = parentId;
        // PDFium can't write /IRT, so the link rides in the reply's own /NM — see ReplyMarkup.
        annotation.NativeId = ReplyMarkup.NewId(parentId);
    }

    /// <summary>
    /// A note answering <paramref name="parent"/>, pinned in the right margin beside it and below
    /// any replies it already has, so a thread reads top to bottom.
    /// </summary>
    private static TextNoteAnnotation BuildReplyNote(
        MarkupEntry entry, Annotation parent, List<Annotation> pageAnnotations, IPdfService pdf, int pageIndex)
    {
        var (pageWidth, pageHeight) = pdf.GetPageSize(pageIndex);
        var parentId = parent.NativeId!;
        int earlierReplies = pageAnnotations.Count(a => ReplyMarkup.ParentOf(a) == parentId);
        var y = MathF.Min(AnchorY(parent) + ReplyStep * (earlierReplies + 1), (float)pageHeight - ReplyStep);

        var note = new TextNoteAnnotation
        {
            X = (float)pageWidth - NoteMarginInset,
            Y = y,
            Text = entry.Comment ?? "",
            Color = entry.Color ?? DefaultColors[MarkupType.Note],
            Author = entry.Author ?? "AI Reviewer",
            Source = AnnotationSource.RailReader,
        };
        MarkAsReply(note, parentId);
        return note;
    }

    /// <summary>The vertical position a margin note beside the annotation lines up with.</summary>
    private static float AnchorY(Annotation annotation) => annotation switch
    {
        // The last line, where a note on a quote is pinned too.
        TextMarkupAnnotation { Rects.Count: > 0 } m => m.Rects[^1].Y + m.Rects[^1].H / 2f,
        TextNoteAnnotation n => n.Y,
        RectAnnotation r => r.Y + r.H / 2f,
        CaretAnnotation c => c.Y + c.H / 2f,
        FreeTextAnnotation f => f.Y + f.H / 2f,
        _ => HeadingIndex.SortY(annotation),
    };

    /// <summary>
    /// The part of a markup that carries on over a page break. It is the same kind of markup as the
    /// first part, tagged as a continuation and without the comment (or replacement), which the
    /// first part carries — a strikeout here is the tail of the edit, not a deletion of its own.
    /// </summary>
    private static Annotation BuildContinuation(MarkupEntry entry, List<RectF> rects)
    {
        var type = entry.Type == MarkupType.Replace ? MarkupType.Strikeout : entry.Type;
        var annotation = BuildTextMarkup(type, rects);
        annotation.Subject = ContinuationMarkup.Subject;
        annotation.Color = entry.Color ?? DefaultColors[entry.Type];
        annotation.Author = entry.Author ?? "AI Reviewer";
        annotation.Source = AnnotationSource.RailReader;
        return annotation;
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

        var annotation = BuildTextMarkup(entry.Type, rects);
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

    private static TextMarkupAnnotation BuildTextMarkup(MarkupType type, List<RectF> rects)
    {
        // Highlight/StrikeOut are drawn across the full glyph box, but Underline/Squiggly are a
        // stroke beneath the text, so passing them the same full-height quad draws the line at
        // cap-height and it reads as a strike-through instead of an underline (issue #13).
        bool baselineBand = type is MarkupType.Underline or MarkupType.Squiggly;
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

        TextMarkupAnnotation annotation = type switch
        {
            MarkupType.Highlight => new HighlightAnnotation(),
            MarkupType.Underline => new UnderlineAnnotation(),
            MarkupType.Strikeout or MarkupType.Replace => new StrikeOutAnnotation(),
            MarkupType.Squiggly => new SquigglyAnnotation(),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unhandled markup type."),
        };
        annotation.Rects = markupRects;
        return annotation;
    }
}
