using System.Text.Json.Serialization;
using RailMark.Models;
using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailMark.Services;

public record MarkupEntryResult(
    int Page, string? Quote, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MarkupType? Type,
    bool Success, string? Error, int SpanCount = 0)
{
    /// <summary>The id of the annotation an <c>update</c> entry targeted, echoed from the plan.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Update { get; init; }

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

    /// <summary>Annotations removed up front (by author, for lacking one, or by id).</summary>
    public int Removed { get; set; }

    /// <summary>Ids named for removal that matched no annotation.</summary>
    public List<string> MissingIds { get; } = [];

    public bool AllSucceeded => Entries.All(e => e.Success);
}

public static class MarkupPlanService
{
    /// <summary>
    /// How far apart, in points, two annotations' geometry may be and still count as covering the
    /// same text. Geometry round-trips through the PDF's own coordinate space, so an annotation
    /// read back is not bit-identical to the one written — but a different line of text is a full
    /// line height away.
    /// </summary>
    private const float SameGeometryTolerance = 1.5f;

    /// <summary>
    /// What a highlight's opacity is when neither the entry nor <c>--opacity</c> says: the value
    /// RailReader2's own Highlight tool uses. At 1 a saturated colour hides the text under it.
    /// </summary>
    public const float DefaultHighlightOpacity = 0.35f;

    /// <summary>
    /// How far apart two opacities may be and still match: /CA is stored as an 8-bit alpha, so a
    /// value read back from a PDF is only good to about 0.004.
    /// </summary>
    private const float SameOpacityTolerance = 0.01f;

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
    /// round of markup rather than adding to it. An empty name means the same as
    /// <paramref name="replaceUnauthored"/>.
    /// </param>
    /// <param name="replaceUnauthored">Remove every existing annotation that has no author.</param>
    /// <param name="defaultOpacity">Opacity for text markup whose entry sets none; see <see cref="MarkupEntry.Opacity"/>.</param>
    /// <param name="removeIds">Remove the annotations with these ids (and the tail of any that continue over a page break).</param>
    public static (AnnotationFile file, MarkupPlanApplyResult result) Resolve(
        MarkupPlan plan,
        IPdfService pdf,
        IPdfTextService textService,
        AnnotationFile? existing = null,
        string? password = null,
        bool includeContext = false,
        string? replaceAuthor = null,
        bool replaceUnauthored = false,
        IReadOnlyCollection<string>? removeIds = null,
        float? defaultOpacity = null)
    {
        var file = existing ?? new AnnotationFile();
        var missingIds = new List<string>();
        var removed = Remove(file, replaceAuthor, replaceUnauthored, removeIds, missingIds);
        var removalHint = replaceAuthor != null || replaceUnauthored || removeIds is { Count: > 0 }
            ? " (it may have been removed by --replace-author, --replace-unauthored or --remove-ids)."
            : ".";
        var pages = new Dictionary<int, (NormalizedText Text, IReadOnlyList<CharBox> Chars, bool Blank, bool FromOcr)>();
        (NormalizedText Text, IReadOnlyList<CharBox> Chars, bool Blank, bool FromOcr) Page(int pageIndex)
        {
            if (!pages.TryGetValue(pageIndex, out var page))
            {
                var extracted = textService.ExtractPageText(pdf.PdfBytes, pageIndex, password);
                pages[pageIndex] = page = (NormalizedText.From(extracted.Text), extracted.CharBoxes ?? [],
                    string.IsNullOrWhiteSpace(extracted.Text), extracted.FromOcr);
            }
            return page;
        }
        NormalizedText PageText(int pageIndex) => Page(pageIndex).Text;

        // OCR text can misread a word or box it loosely, so a match against it is worth a look.
        string? OcrWarning(IEnumerable<Placement> placed)
        {
            var ocrPages = placed.Select(p => p.PageIndex).Distinct().Where(p => Page(p).FromOcr).Select(p => p + 1).ToList();
            return ocrPages.Count == 0 ? null
                : $"Matched OCR text on {(ocrPages.Count == 1 ? "page " : "pages ")}{string.Join('–', ocrPages)}: "
                  + "check the markup covers the intended words.";
        }

        // Pass 1 — locate every quote. Results are indexed by original plan position so the
        // report preserves plan order regardless of how geometry lookups are batched.
        var results = new MarkupEntryResult?[plan.Entries.Count];
        var placements = new List<Placement>?[plan.Entries.Count];
        var replyAnchors = new Annotation?[plan.Entries.Count];
        var updateTargets = new Annotation?[plan.Entries.Count];

        string MissingAnnotation(string id, int entryPage, int pageIndex)
        {
            var elsewhere = file.Pages.FirstOrDefault(p => p.Value.Any(a => a.NativeId == id));
            return elsewhere.Value is not null
                ? $"Annotation {id} is on page {elsewhere.Key + 1}, not page {entryPage}."
                : $"No annotation with id {id} on page {entryPage}" + removalHint;
        }
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

            if (entry.Update is { } updateId)
            {
                var target = file.Pages.GetValueOrDefault(pageIndex)?.FirstOrDefault(a => a.NativeId == updateId);
                if (target is null)
                {
                    results[index] = Failure(entry, MissingAnnotation(updateId, entry.Page, pageIndex));
                    continue;
                }

                // Nothing to locate: the annotation is changed where it stands.
                updateTargets[index] = target;
                results[index] = new MarkupEntryResult(entry.Page, null, null, true, null)
                {
                    Update = updateId,
                    Context = includeContext
                        ? $"update the {AnnotationJsonBuilder.TypeName(target)} by {target.Author ?? "unknown author"}"
                        : null,
                };
                continue;
            }

            if (entry.InReplyTo is { } parentId)
            {
                var parent = file.Pages.GetValueOrDefault(pageIndex)?.FirstOrDefault(a => a.NativeId == parentId);
                if (parent is null)
                {
                    results[index] = Failure(entry, MissingAnnotation(parentId, entry.Page, pageIndex));
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
                    Warning = JoinWarnings(match.Value.Warning, OcrWarning(placements[index]!)),
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
                        Warning = OcrWarning(placements[index]!),
                        Context = includeContext
                            ? TextLocator.DescribeContext(page, split.Head) + $" ‖ page {entry.Page + 1}: "
                              + TextLocator.DescribeContext(nextPage, split.Tail)
                            : null,
                    };
                    continue;
                }
            }

            // The commonest cause of a missing quote is a page number off by one: say so. Checked
            // first, so a blank page in a text PDF points at the neighbour rather than at OCR.
            if (failure.MatchCount == 0 && FoundOnNeighbour(entry.Quote!, pageIndex) is { } neighbour)
                failure = failure with { Error = $"Quote not found on page {entry.Page} (it occurs on page {neighbour + 1})." };
            // A scan has no text to search: no plan can fix that, so say what will.
            else if (failure.MatchCount == 0 && Page(pageIndex).Blank)
                failure = failure with
                {
                    Error = $"Page {entry.Page} has no text layer (is it a scan?). Add one with "
                            + "`railreader2 ocr <pdf>` and apply the plan to the copy it writes.",
                };
            else if (failure.MatchCount == 0 && Page(pageIndex).FromOcr)
                failure = failure with
                {
                    Error = $"Quote not found on page {entry.Page}, whose text comes from OCR and may misread "
                            + "words: try a shorter quote, or one without unusual characters.",
                };
            results[index] = failure;
        }

        int? FoundOnNeighbour(string quote, int pageIndex)
        {
            foreach (var candidate in new[] { pageIndex + 1, pageIndex - 1 })
                if (candidate >= 0 && candidate < pdf.PageCount
                    && TextLocator.FindQuoteMatches(PageText(candidate), quote).Count > 0)
                    return candidate;
            return null;
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

        // Pass 3 — build and merge annotations, in plan order. The run state records which entry
        // put each annotation in place during this run, to catch two entries contradicting each
        // other. A note is built at its ideal spot — its identity for dedupe — and only moved to
        // make room for other notes once it is known to be new.
        var run = new RunState();
        for (int index = 0; index < plan.Entries.Count; index++)
        {
            var entry = plan.Entries[index];
            if (updateTargets[index] is { } updateTarget)
            {
                results[index] = ApplyUpdate(entry, updateTarget, file, index, run, results[index]!);
                continue;
            }

            if (replyAnchors[index] is { } anchor)
            {
                var pageIndex = entry.Page - 1;
                var note = BuildReplyNote(entry, anchor);
                var outcome = Merge(file, pageIndex, note, index, run,
                    place: n => PlaceReply(n, anchor, Page(pageIndex).Chars, pdf.GetPageSize(pageIndex), file, pageIndex));
                results[index] = outcome.ConflictsWith is { } other
                    ? ConflictFailure(results[index]!, other)
                    : results[index]! with { Action = outcome.Action };
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
            int partsToBuild = entry.Kind == MarkupType.Note ? 1 : entryPlacements.Count;
            string? headId = null;
            int? conflict = null;
            Annotation? nearDuplicate = null;
            for (int part = 0; part < partsToBuild && conflict is null; part++)
            {
                var pageIndex = entryPlacements[part].PageIndex;
                Annotation annotation;
                Action<TextNoteAnnotation>? place = null;
                if (part == 0)
                {
                    annotation = BuildAnnotation(entry, geometry[index][part], Page(pageIndex).Chars, pdf.GetPageSize(pageIndex), defaultOpacity);
                    if (OpacityIsExplicit(entry, defaultOpacity))
                        run.ExplicitOpacity.Add(annotation);
                    if (annotation is TextNoteAnnotation)
                    {
                        var anchorLine = geometry[index][part][^1];
                        var chars = Page(pageIndex).Chars;
                        var size = pdf.GetPageSize(pageIndex);
                        place = n => Move(n, NotePlacement.Beside(anchorLine, chars, size, OccupiedIcons(file, pageIndex)));
                    }
                    if (entry.InReplyTo is { } parentId)
                        MarkAsReply(annotation, parentId);
                    // A split markup's halves are linked through /NM, so the head needs its id
                    // before it is written rather than the one the PDF writer would mint.
                    if (partsToBuild > 1)
                        annotation.NativeId ??= Guid.NewGuid().ToString();
                }
                else
                {
                    annotation = BuildContinuation(entry, geometry[index][part], headId, defaultOpacity);
                    if (OpacityIsExplicit(entry, defaultOpacity))
                        run.ExplicitOpacity.Add(annotation);
                }

                var outcome = Merge(file, pageIndex, annotation, index, run, place);
                if (outcome.ConflictsWith is { } other)
                {
                    conflict = other;
                    break;
                }
                if (part == 0)
                {
                    headId = outcome.Stored!.NativeId;
                    nearDuplicate = outcome.NearDuplicateOf;
                }
                actions.Add(outcome.Action!);
            }

            if (conflict is { } conflicting)
            {
                results[index] = ConflictFailure(results[index]!, conflicting);
                continue;
            }

            results[index] = results[index]! with
            {
                Warning = JoinWarnings(results[index]!.Warning, nearDuplicate is null ? null : NearDuplicateWarning(nearDuplicate)),
                SpanCount = geometry[index].Sum(rects => rects.Count),
                Action = actions.All(a => a == ActionUnchanged) ? ActionUnchanged
                    : actions.Contains(ActionAdded) ? ActionAdded
                    : ActionUpdated,
            };
        }

        var result = new MarkupPlanApplyResult { Removed = removed };
        result.MissingIds.AddRange(missingIds);
        result.Entries.AddRange(results!);
        return (file, result);
    }

    /// <summary>
    /// Removes the annotations the caller named: every one by <paramref name="author"/> (an empty
    /// name, like <paramref name="unauthored"/>, selects those with no author) and every one with
    /// an id in <paramref name="ids"/>. Emptied pages keep their (now empty) list rather than
    /// losing the key: the PDF writer only reconciles pages present in the file, so a dropped key
    /// would leave the old annotations in the document.
    /// </summary>
    private static int Remove(
        AnnotationFile file, string? author, bool unauthored, IReadOnlyCollection<string>? ids, List<string> missingIds)
    {
        unauthored |= author is { Length: 0 };
        int removed = 0;
        var wanted = ids is null ? null : new HashSet<string>(ids, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var annotations in file.Pages.Values)
        {
            removed += annotations.RemoveAll(a =>
            {
                bool hit = Matches(a);
                if (hit && a.NativeId is { } removedId) gone.Add(removedId);
                return hit;
            });
        }
        // A comment goes with its thread, as in RailReader2 and Acrobat; replies may sit on any
        // page and may themselves be replied to, so repeat until nothing more follows.
        int swept;
        do
        {
            swept = 0;
            foreach (var annotations in file.Pages.Values)
                swept += annotations.RemoveAll(a =>
                {
                    var parent = ReplyMarkup.ParentOf(a);
                    bool follows = (parent is not null && gone.Contains(parent))
                        || (ContinuationMarkup.HeadOf(a) is { } head && gone.Contains(head));
                    if (!follows) return false;
                    if (a.NativeId is { } id) gone.Add(id);
                    return true;
                });
            removed += swept;
        } while (swept > 0);
        if (ids != null)
            missingIds.AddRange(ids.Where(id => !seen.Contains(id)));
        return removed;

        bool Matches(Annotation a)
        {
            // Note the id first, so an annotation also removed by author is not reported missing.
            bool byId = false;
            if (wanted is not null && a.NativeId is { } id)
            {
                // The tail of a markup that runs over a page break goes with its head.
                var head = ContinuationMarkup.HeadOf(a) ?? id;
                if (wanted.Contains(head))
                {
                    seen.Add(head);
                    byId = true;
                }
            }
            return byId
                || (author is { Length: > 0 } && string.Equals(a.Author, author, StringComparison.Ordinal))
                || (unauthored && string.IsNullOrEmpty(a.Author));
        }
    }

    /// <summary>
    /// Changes the comment, colour and author of an existing annotation where it stands. A
    /// replacement strikeout keeps its "Replace with:" text and takes the new comment after it.
    /// </summary>
    private static MarkupEntryResult ApplyUpdate(
        MarkupEntry entry, Annotation target, AnnotationFile file, int entryIndex, RunState run, MarkupEntryResult result)
    {
        string? contents = entry.Comment is null ? null
            : ReplacementMarkup.TryParse(target, out var replacement, out _)
                ? ReplacementMarkup.FormatContents(replacement, entry.Comment)
                : entry.Comment;
        bool changeContents = contents is not null
            && NormalizeContents(target.EffectiveContents) != NormalizeContents(contents);
        bool changeColor = entry.Color is not null
            && !string.Equals(target.Color, entry.Color, StringComparison.OrdinalIgnoreCase);
        bool changeAuthor = entry.Author is not null
            && !string.Equals(target.Author, entry.Author, StringComparison.Ordinal);
        bool changeOpacity = entry.Opacity is { } opacity
            && Math.Abs(target.Opacity - opacity) > SameOpacityTolerance;

        run.Claimed.Add(target);
        if (!changeContents && !changeColor && !changeAuthor && !changeOpacity)
            return result with { Action = ActionUnchanged };

        if (run.Touched.TryGetValue(target, out var earlier) && earlier != entryIndex)
            return result with
            {
                Success = false,
                Error = $"Conflicts with entry {earlier + 1} of this plan, which already changed this annotation. "
                        + "Merge them into one entry.",
            };

        if (changeContents)
        {
            target.Contents = contents;
            if (target is TextNoteAnnotation note)
                note.Text = contents!;
        }
        if (changeColor)
        {
            target.Color = entry.Color!;
            // The reader's faithful colour components would otherwise mask the new hex colour.
            target.ColorComponents = null;
        }
        if (changeAuthor)
            target.Author = entry.Author;
        if (changeOpacity)
            target.Opacity = entry.Opacity!.Value;

        // A markup that runs over a page break is two annotations; the tail carries no comment, but
        // it shares the head's colour, author and opacity, and pairing falls back on the author.
        if ((changeColor || changeAuthor || changeOpacity) && target.NativeId is { } headId)
        {
            foreach (var tail in file.Pages.Values.SelectMany(a => a)
                         .Where(a => ContinuationMarkup.Is(a) && ContinuationMarkup.HeadOf(a) == headId))
            {
                if (changeColor)
                {
                    tail.Color = entry.Color!;
                    tail.ColorComponents = null;
                }
                if (changeAuthor)
                    tail.Author = entry.Author;
                if (changeOpacity)
                    tail.Opacity = entry.Opacity!.Value;
            }
        }

        run.Touched[target] = entryIndex;
        return result with { Action = ActionUpdated };
    }

    /// <summary>What this run has done so far, for telling its own work from what pre-existed.</summary>
    private sealed class RunState
    {
        /// <summary>Annotations built with an opacity the plan or command line asked for (see <see cref="OpacityIsExplicit"/>).</summary>
        public HashSet<Annotation> ExplicitOpacity { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Which entry put each annotation in place during this run.</summary>
        public Dictionary<Annotation, int> Touched { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Every annotation an entry of this run has matched, changed or not.</summary>
        public HashSet<Annotation> Claimed { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Where each note added this run would ideally sit — where a later entry for the same
        /// note will look for it, however far avoidance moved the icon itself.
        /// </summary>
        public Dictionary<Annotation, NoteSpot> Ideals { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>What <see cref="Merge"/> did with an annotation.</summary>
    /// <param name="Stored">The annotation now on the page — the new one, or the twin it matched.</param>
    /// <param name="ConflictsWith">Set instead of an action: the plan index of an earlier entry
    /// that already put different markup on the same text in this run.</param>
    /// <param name="NearDuplicateOf">Set on an added annotation: a pre-existing one of the same kind that
    /// covers much the same text, which the plan may have meant to <c>update</c> instead.</param>
    private readonly record struct MergeOutcome(
        string? Action, Annotation? Stored, int? ConflictsWith = null, Annotation? NearDuplicateOf = null);

    /// <summary>
    /// Adds <paramref name="annotation"/> to its page unless an annotation of the same kind, by the
    /// same author, already covers the same text there. A match whose comment or colour differs is
    /// brought up to date instead of being duplicated — unless an earlier entry of this same plan
    /// put it there, in which case the two entries contradict each other and the later one is
    /// refused rather than silently overwriting the first.
    /// </summary>
    /// <param name="place">
    /// For a note: moves it from its ideal spot to its final one, once it is known to be new. A
    /// note that matched a twin keeps the twin's position.
    /// </param>
    private static MergeOutcome Merge(
        AnnotationFile file, int pageIndex, Annotation annotation, int entryIndex, RunState run,
        Action<TextNoteAnnotation>? place = null)
    {
        if (!file.Pages.TryGetValue(pageIndex, out var pageAnnotations))
            file.Pages[pageIndex] = pageAnnotations = [];

        // Notes of one text may sit near each other (avoidance spreads them out), so of several
        // candidates the one meant is the nearest to where this one would go that no earlier entry
        // has already accounted for.
        var twin = pageAnnotations
            .Where(a => IsSameMarkup(a, annotation, run))
            .MinBy(a => (run.Claimed.Contains(a) ? 1e6f : 0f) + ((a, annotation) is (TextNoteAnnotation na, TextNoteAnnotation nb)
                ? MathF.Abs(na.Y - nb.Y) * 1000f + MathF.Abs(na.X - nb.X)
                : 0f));
        if (twin is null)
        {
            if (annotation is TextNoteAnnotation note && place is not null)
            {
                run.Ideals[note] = new NoteSpot(note.X, note.Y);
                place(note);
            }
            var nearDuplicate = FindNearDuplicate(pageAnnotations, annotation, run);
            pageAnnotations.Add(annotation);
            run.Touched[annotation] = entryIndex;
            run.Claimed.Add(annotation);
            return new MergeOutcome(ActionAdded, annotation, NearDuplicateOf: nearDuplicate);
        }
        run.Claimed.Add(twin);

        bool sameContents = NormalizeContents(twin.EffectiveContents) == NormalizeContents(annotation.EffectiveContents);
        bool sameColor = string.Equals(twin.Color, annotation.Color, StringComparison.OrdinalIgnoreCase);
        bool explicitOpacity = run.ExplicitOpacity.Contains(annotation);
        bool sameOpacity = !explicitOpacity || Math.Abs(twin.Opacity - annotation.Opacity) <= SameOpacityTolerance;
        if (sameContents && sameColor && sameOpacity)
            return new MergeOutcome(ActionUnchanged, twin);

        if (run.Touched.TryGetValue(twin, out var earlier) && earlier != entryIndex)
            return new MergeOutcome(null, null, ConflictsWith: earlier);

        twin.Contents = annotation.EffectiveContents;
        if (twin is TextNoteAnnotation twinNote)
            twinNote.Text = annotation.EffectiveContents;
        twin.Color = annotation.Color;
        // The reader's faithful colour components would otherwise mask the new hex colour.
        twin.ColorComponents = null;
        if (explicitOpacity)
            twin.Opacity = annotation.Opacity;
        run.Touched[twin] = entryIndex;
        return new MergeOutcome(ActionUpdated, twin);
    }

    /// <summary>
    /// Whether <paramref name="existing"/>, already on the page, is the annotation that
    /// <paramref name="candidate"/> (fresh from the plan, a note still at its ideal spot) describes.
    /// </summary>
    private static bool IsSameMarkup(Annotation existing, Annotation candidate, RunState run)
    {
        var (a, b) = (existing, candidate);
        if (a.GetType() != b.GetType()) return false;
        if (!string.Equals(a.Author, b.Author, StringComparison.Ordinal)) return false;

        bool sameText = NormalizeContents(a.EffectiveContents) == NormalizeContents(b.EffectiveContents);

        // A reply answers a particular annotation; replies to different ones are never the same.
        // A reply *note* is identified by what it says, not where it sits: one parent can collect
        // several replies from the same author, and where each is pinned depends on how many
        // came before it. A reply that marks text is still identified by the text it marks.
        var parentA = ReplyMarkup.ParentOf(a);
        var parentB = ReplyMarkup.ParentOf(b);
        if (parentA != parentB) return false;
        if (parentA != null && a is TextNoteAnnotation) return sameText;

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
            // A note is a free-standing icon, so what it says is part of which note it is. One
            // this run placed is the same note only if it was aimed at the same spot; one read
            // back from the PDF may have been moved to make room when it was placed, so it only
            // has to be within reach of where the candidate aims.
            (TextNoteAnnotation na, TextNoteAnnotation nb) => sameText && (
                run.Ideals.TryGetValue(na, out var ideal)
                    ? Near(ideal.X, nb.X) && Near(ideal.Y, nb.Y)
                    : MathF.Abs(na.Y - nb.Y) <= NotePlacement.MaxDrift + SameGeometryTolerance),
            _ => false,
        };
    }

    /// <summary>Intersection over union of two markups' areas at which they count as the same text.</summary>
    private const float NearDuplicateOverlap = 0.5f;

    /// <summary>
    /// A markup already on the page — not one this run added — of the same kind that covers
    /// substantially the same text as <paramref name="added"/>, by any author and however the
    /// lines were split. Dedupe only recognises exact geometry from the same author, so this is
    /// what notices a re-applied record that differs in author or by a line break.
    /// </summary>
    private static Annotation? FindNearDuplicate(List<Annotation> page, Annotation added, RunState run)
    {
        if (added is not TextMarkupAnnotation mb || ReplyMarkup.ParentOf(added) != null || ContinuationMarkup.Is(added))
            return null;
        bool replacement = ReplacementMarkup.TryParse(added, out _, out _);
        return page.OfType<TextMarkupAnnotation>()
            .Where(ma => ma.GetType() == mb.GetType()
                && !run.Touched.ContainsKey(ma)
                && !ContinuationMarkup.Is(ma)
                && ReplyMarkup.ParentOf(ma) == null
                && ReplacementMarkup.TryParse(ma, out _, out _) == replacement)
            .Select(ma => (Annotation: (Annotation)ma, Share: OverlapShare(ma, mb)))
            .Where(c => c.Share >= NearDuplicateOverlap)
            .OrderByDescending(c => c.Share)
            .Select(c => c.Annotation)
            .FirstOrDefault();
    }

    private static float OverlapShare(TextMarkupAnnotation a, TextMarkupAnnotation b)
    {
        float areaA = a.Rects.Sum(r => r.W * r.H), areaB = b.Rects.Sum(r => r.W * r.H);
        if (areaA <= 0 || areaB <= 0) return 0;
        float shared = 0;
        foreach (var ra in a.Rects)
            foreach (var rb in b.Rects)
            {
                float w = MathF.Min(ra.X + ra.W, rb.X + rb.W) - MathF.Max(ra.X, rb.X);
                float h = MathF.Min(ra.Y + ra.H, rb.Y + rb.H) - MathF.Max(ra.Y, rb.Y);
                if (w > 0 && h > 0) shared += w * h;
            }
        // Intersection over union: a short markup nested inside a long one is not a duplicate of it.
        return shared / (areaA + areaB - shared);
    }

    private static string NearDuplicateWarning(Annotation existing)
        => (existing.NativeId is { } id ? $"Near-duplicate of {id}" : "Near-duplicate of an existing annotation without an id")
           + ", which covers much the same text. If the entry meant to change it, use an \"update\" entry with its id instead.";

    private static bool Near(float a, float b) => MathF.Abs(a - b) <= SameGeometryTolerance;

    // Viewers may rewrite line breaks when they save.
    private static string NormalizeContents(string contents) => contents.Replace("\r\n", "\n").Replace('\r', '\n');

    private static MarkupEntryResult ConflictFailure(MarkupEntryResult located, int otherIndex)
        => located with
        {
            Success = false,
            Warning = null,
            Error = $"Conflicts with entry {otherIndex + 1} of this plan, which marks the same text "
                    + "with a different comment or colour. Merge them into one entry.",
        };

    private static string? JoinWarnings(params string?[] warnings)
        => warnings.Any(w => w != null) ? string.Join(' ', warnings.Where(w => w != null)) : null;

    private static MarkupEntryResult Failure(MarkupEntry entry, string error)
        => new(entry.Page, entry.Quote, entry.Type, false, error) { Update = entry.Update };

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
        if (entry.Opacity is { } opacity && !(opacity > 0f && opacity <= 1f))
            return $"opacity must be above 0 and at most 1 (got {opacity}).";

        if (entry.Update != null)
        {
            if (string.IsNullOrWhiteSpace(entry.Update))
                return "update must be the id of an existing annotation.";
            if (entry.InReplyTo != null || entry.Replacement != null)
                return "An update changes only comment, color, author and opacity; inReplyTo and replacement cannot be changed.";
            if (entry.Comment is null && entry.Color is null && entry.Author is null && entry.Opacity is null)
                return "An update needs at least one of comment, color, author or opacity to change.";
            return null;
        }

        if (entry.Type is null)
            return "type is required (only an entry with update may omit it).";

        if (string.IsNullOrWhiteSpace(entry.Quote))
        {
            if (entry.InReplyTo is null)
                return "quote is required (it may be omitted only by a note replying to an annotation via inReplyTo).";
            if (entry.Kind != MarkupType.Note)
                return $"A reply without a quote must be a note (this entry is {entry.Kind.ToString().ToLowerInvariant()}).";
            if (entry.Occurrence != null || entry.ContextBefore != null || entry.ContextAfter != null)
                return "occurrence, contextBefore and contextAfter need a quote to apply to.";
        }

        if (entry.Kind == MarkupType.Replace && string.IsNullOrEmpty(entry.Replacement))
            return "A replace entry needs a non-empty replacement (use strikeout to suggest a deletion).";
        if (entry.Replacement != null && entry.Replacement.Replace("\r\n", "\n").Contains("\n\n"))
            return "replacement must not contain a blank line (it separates the replacement from the comment when read back).";
        if (entry.Kind != MarkupType.Replace && entry.Replacement != null)
            return $"replacement is only valid with type replace (this entry is {entry.Kind.ToString().ToLowerInvariant()}).";
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

    /// <summary>A note answering <paramref name="parent"/>; it is positioned once known to be new.</summary>
    private static TextNoteAnnotation BuildReplyNote(MarkupEntry entry, Annotation parent)
    {
        var note = new TextNoteAnnotation
        {
            Text = entry.Comment ?? "",
            Color = entry.Color ?? DefaultColors[MarkupType.Note],
            Author = entry.Author ?? "AI Reviewer",
            Source = AnnotationSource.RailReader,
        };
        MarkAsReply(note, parent.NativeId!);
        return note;
    }

    /// <summary>
    /// Pins a reply note at the foot of its thread — under its parent's icon and the replies
    /// already there, or, for the first reply to a text markup, beside the markup's last line —
    /// pushing any other note in that spot down, so a thread stays contiguous and reads top to
    /// bottom.
    /// </summary>
    private static void PlaceReply(
        TextNoteAnnotation note, Annotation parent, IReadOnlyList<CharBox> chars,
        (double Width, double Height) page, AnnotationFile file, int pageIndex)
    {
        var notes = file.Pages.GetValueOrDefault(pageIndex)?.OfType<TextNoteAnnotation>().ToList() ?? [];
        var thread = notes
            .Where(n => ReferenceEquals(n, parent) || ReplyMarkup.ParentOf(n) == parent.NativeId)
            .Select(AnchorRect)
            .ToList();
        var spot = thread.Count > 0
            ? NotePlacement.UnderThread(thread, page)
            : NotePlacement.Ideal(AnchorRect(parent, page), chars, page);
        NotePlacement.Insert(spot, notes, page);
        Move(note, spot);
    }

    private static void Move(TextNoteAnnotation note, NoteSpot spot) => (note.X, note.Y) = (spot.X, spot.Y);

    /// <summary>The icons of the notes already on the page, which a new note must not cover.</summary>
    private static List<RectF> OccupiedIcons(AnnotationFile file, int pageIndex)
        => file.Pages.GetValueOrDefault(pageIndex)?.OfType<TextNoteAnnotation>().Select(AnchorRect).ToList() ?? [];

    private static RectF AnchorRect(TextNoteAnnotation note) => NotePlacement.IconRect(new NoteSpot(note.X, note.Y));

    /// <summary>The line (or box) a note beside <paramref name="annotation"/> lines up with.</summary>
    private static RectF AnchorRect(Annotation annotation, (double Width, double Height) page) => annotation switch
    {
        // The last line, where a note on a quote is pinned too.
        TextMarkupAnnotation { Rects.Count: > 0 } m => Box(m.Rects[^1].X, m.Rects[^1].Y, m.Rects[^1].W, m.Rects[^1].H),
        TextNoteAnnotation n => AnchorRect(n),
        RectAnnotation r => Box(r.X, r.Y, r.W, r.H),
        CaretAnnotation c => Box(c.X, c.Y, c.W, c.H),
        FreeTextAnnotation f => Box(f.X, f.Y, f.W, f.H),
        _ => Box(0, HeadingIndex.SortY(annotation), (float)page.Width, 1f),
    };

    private static RectF Box(float x, float y, float w, float h) => new(x, y, x + w, y + h);

    /// <summary>
    /// The part of a markup that carries on over a page break. It is the same kind of markup as the
    /// first part, tagged as a continuation and without the comment (or replacement), which the
    /// first part carries — a strikeout here is the tail of the edit, not a deletion of its own.
    /// </summary>
    private static Annotation BuildContinuation(MarkupEntry entry, List<RectF> rects, string? headId, float? defaultOpacity)
    {
        var type = entry.Kind == MarkupType.Replace ? MarkupType.Strikeout : entry.Kind;
        var annotation = BuildTextMarkup(type, rects);
        annotation.Subject = ContinuationMarkup.Subject;
        if (headId != null)
            annotation.NativeId = ContinuationMarkup.NewId(headId);
        annotation.Color = entry.Color ?? DefaultColors[entry.Kind];
        annotation.Opacity = OpacityFor(entry, defaultOpacity);
        annotation.Author = entry.Author ?? "AI Reviewer";
        annotation.Source = AnnotationSource.RailReader;
        return annotation;
    }

    private static Annotation BuildAnnotation(
        MarkupEntry entry, List<RectF> rects, IReadOnlyList<CharBox> chars, (double Width, double Height) page,
        float? defaultOpacity)
    {
        var color = entry.Color ?? DefaultColors[entry.Kind];
        var author = entry.Author ?? "AI Reviewer";

        if (entry.Kind == MarkupType.Note)
        {
            // Built at its ideal spot, beside the last line of the quote; Merge moves it clear of
            // other notes once it knows the note is new.
            var ideal = NotePlacement.Ideal(rects[^1], chars, page);
            return new TextNoteAnnotation
            {
                X = ideal.X,
                Y = ideal.Y,
                Text = entry.Comment ?? "",
                Color = color,
                Author = author,
                Source = AnnotationSource.RailReader,
            };
        }

        var annotation = BuildTextMarkup(entry.Kind, rects);
        if (entry.Kind == MarkupType.Replace)
        {
            annotation.Subject = ReplacementMarkup.Subject;
            annotation.Contents = ReplacementMarkup.FormatContents(entry.Replacement!, entry.Comment);
        }
        else
        {
            annotation.Contents = entry.Comment;
        }
        annotation.Color = color;
        annotation.Opacity = OpacityFor(entry, defaultOpacity);
        annotation.Author = author;
        annotation.Source = AnnotationSource.RailReader;

        return annotation;
    }

    /// <summary>The opacity a text-markup entry gets: its own, else the run's default, else the type's.</summary>
    private static float OpacityFor(MarkupEntry entry, float? defaultOpacity)
        => entry.Opacity ?? defaultOpacity
           ?? (entry.Kind == MarkupType.Highlight ? DefaultHighlightOpacity : 1f);

    /// <summary>
    /// Whether the plan or the command line asked for this opacity, as opposed to it being the
    /// type's default. Only an asked-for opacity is compared against — and written over — an
    /// annotation already in the PDF, so re-applying an old plan doesn't repaint earlier highlights.
    /// </summary>
    private static bool OpacityIsExplicit(MarkupEntry entry, float? defaultOpacity)
        => entry.Kind != MarkupType.Note && (entry.Opacity ?? defaultOpacity) is not null;

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
