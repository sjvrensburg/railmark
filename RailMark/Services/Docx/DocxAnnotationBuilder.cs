using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailMark.Models;

namespace RailMark.Services.Docx;

/// <summary>Outcome of placing one Word review item in the PDF.</summary>
internal sealed record DocxItemResult
{
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public string? Author { get; init; }
    public bool Success { get; init; }
    /// <summary>1-based page the annotation landed on.</summary>
    public int? Page { get; init; }
    /// <summary>1-based last page, when a range ran over a page break.</summary>
    public int? EndPage { get; init; }
    /// <summary>What was written: highlight, strikeout, replacement, note, underline, reply.</summary>
    public string? Annotation { get; init; }
    public string? Error { get; init; }
    public string? Warning { get; init; }
}

/// <summary>
/// Turns a <see cref="DocxReview"/> into native PDF annotations on the PDF LibreOffice rendered from
/// the prepared document. Every anchor is a named destination; a destination's page position is
/// mapped to the character boundary nearest it, and a range is the characters between its start
/// and end boundaries — so placement is exact and never depends on what the text says.
/// </summary>
/// <remarks>
/// Mapping, in the Acrobat model (the PDF shows the original text):
/// comment → highlight carrying the comment; reply → sticky note threaded under it (/IRT);
/// comment with no range → sticky note; deletion → strikeout; deletion + insertion → RailMark's
/// replacement strikeout (<see cref="ReplacementMarkup"/>); insertion → sticky note at the
/// insertion point (a caret, in Acrobat — PDFium cannot create carets yet); move → strikeout at the
/// source plus a note at the destination; run-formatting change → underline describing it.
/// A range over a page break follows <see cref="ContinuationMarkup"/>.
/// </remarks>
internal static class DocxAnnotationBuilder
{
    private static readonly string[] Palette = AnnotationInteractionHandler.AnnotationColors;
    private static string Yellow => Palette[0];
    private static string Green => Palette[1];
    private static string Red => Palette[2];
    private static string Blue => Palette[3];

    private readonly record struct Anchor(int Page, int Boundary, CharBox Line);

    private sealed class Pages(IPdfService pdf, IPdfTextService text)
    {
        private readonly Dictionary<int, PageText> _text = [];
        public PageText Text(int page)
        {
            if (!_text.TryGetValue(page, out var t)) _text[page] = t = text.ExtractPageText(pdf.PdfBytes, page);
            return t;
        }
        public (double Width, double Height) Size(int page) => pdf.GetPageSize(page);
        public List<RectF> Rects(int page, int start, int length)
        {
            if (length <= 0) return [];
            var lines = text.GetTextRangeRects(pdf.PdfBytes, page, [(start, length)]);
            return MarkupPlanService.MergeLineRects(lines.Count > 0 ? lines[0] : []);
        }
    }

    public static (AnnotationFile File, List<DocxItemResult> Results) Build(
        DocxReview review, IPdfService pdf, IPdfTextService textService, IPdfLinkService linkService)
    {
        var marks = linkService.ExtractNamedDestinations(pdf.PdfBytes)
            .Where(d => d.Name.StartsWith(review.MarkPrefix, StringComparison.Ordinal))
            .GroupBy(d => d.Name)
            .ToDictionary(g => g.Key, g => g.First().Destination);

        var pages = new Pages(pdf, textService);
        var file = new AnnotationFile();
        var results = new List<DocxItemResult>();
        var heads = new Dictionary<string, (Annotation Annotation, int Page)>(StringComparer.Ordinal);
        string idTag = review.MarkPrefix.ToLowerInvariant();

        Anchor? Locate(string? mark)
        {
            if (mark is null || !marks.TryGetValue(mark, out var d) || d.PageX is not { } x || d.PageY is not { } y)
                return null;
            return BoundaryAt(pages.Text(d.PageIndex), x, y) is { } b ? new Anchor(d.PageIndex, b.Boundary, b.Line) : null;
        }

        List<Annotation> On(int page) => file.Pages.TryGetValue(page, out var l) ? l : file.Pages[page] = [];

        foreach (var item in review.Items)
        {
            var kind = item.Kind.ToString().ToLowerInvariant();
            DocxItemResult Fail(string error) => new() { Kind = kind, Id = item.Id, Author = item.Author, Error = error };

            if (item.Kind == DocxItemKind.Reply)
            {
                if (item.ParentId is null || !heads.TryGetValue(item.ParentId, out var parent))
                {
                    results.Add(Fail("the comment it replies to could not be placed"));
                    continue;
                }
                var reply = Note(item, item.Text, null, Yellow);
                reply.InReplyTo = parent.Annotation.NativeId;
                reply.NativeId = ReplyMarkup.NewId(parent.Annotation.NativeId!);
                MarkupPlanService.PlaceReply(reply, parent.Annotation, pages.Text(parent.Page).CharBoxes,
                    pages.Size(parent.Page), file, parent.Page);
                On(parent.Page).Add(reply);
                results.Add(new() { Kind = kind, Id = item.Id, Author = item.Author, Success = true, Page = parent.Page + 1, Annotation = "reply" });
                continue;
            }

            var start = Locate(item.StartMark);
            var end = Locate(item.EndMark);
            var point = Locate(item.PointMark);
            string? warning = null;

            List<(int Page, int Start, int Length)>? pieces = null;
            if (start is { } s && end is { } e)
            {
                pieces = Pieces(pages, s, e);
                if (pieces.Count == 0)
                {
                    pieces = null;
                    point ??= s;
                    if (item.Kind != DocxItemKind.Comment)
                        warning = "the range is empty in the rendered original; written as a note";
                }
            }
            else if (item.StartMark is not null && point is null)
            {
                point = start ?? end;
            }

            Annotation head;
            int headPage;
            int? endPage = null;
            string written;

            if (pieces is not null && item.Kind is not (DocxItemKind.Insertion or DocxItemKind.MoveTo))
            {
                var (type, color, contents, subject) = item.Kind switch
                {
                    DocxItemKind.Comment => (MarkupType.Highlight, Yellow, item.Text, (string?)null),
                    DocxItemKind.Deletion => (MarkupType.Strikeout, Red, null, "Deleted Text"),
                    DocxItemKind.MoveFrom => (MarkupType.Strikeout, Red, "Moved elsewhere", "Moved Text"),
                    DocxItemKind.Replacement => (MarkupType.Replace, Blue,
                        ReplacementMarkup.FormatContents(item.Text.Trim(), null), ReplacementMarkup.Subject),
                    DocxItemKind.Formatting => (MarkupType.Underline, Green, item.Text, "Formatting"),
                    _ => throw new InvalidOperationException(item.Kind.ToString()),
                };

                var built = new List<(int Page, TextMarkupAnnotation Annotation)>();
                for (int i = 0; i < pieces.Count; i++)
                {
                    var (page, from, length) = pieces[i];
                    var rects = pages.Rects(page, from, length);
                    if (review.BodyMargins is { } margins && !item.Part.StartsWith("header") && !item.Part.StartsWith("footer"))
                        rects = WithinBody(rects, margins, pages.Size(page).Height);
                    if (rects.Count == 0) continue;

                    var pieceType = built.Count > 0 && type == MarkupType.Replace ? MarkupType.Strikeout : type;
                    var markup = MarkupPlanService.BuildTextMarkup(pieceType, rects);
                    Stamp(markup, item, color);
                    markup.Opacity = type == MarkupType.Highlight ? MarkupPlanService.DefaultHighlightOpacity : 1f;
                    if (built.Count == 0)
                    {
                        markup.Contents = contents;
                        markup.Subject = subject;
                        markup.NativeId = $"docx-{kind}-{item.Id}-{idTag}";
                    }
                    else
                    {
                        markup.Subject = ContinuationMarkup.Subject;
                        markup.NativeId = ContinuationMarkup.NewId(built[^1].Annotation.NativeId!);
                    }
                    built.Add((page, markup));
                }

                if (built.Count == 0)
                {
                    results.Add(Fail("the range has no measurable text in the PDF"));
                    continue;
                }
                foreach (var (page, markup) in built) On(page).Add(markup);
                head = built[0].Annotation;
                headPage = built[0].Page;
                if (built[^1].Page != headPage) endPage = built[^1].Page + 1;
                if (built.Count > 2) warning = $"runs over {built.Count} pages";
                written = type switch
                {
                    MarkupType.Highlight => "highlight", MarkupType.Replace => "replacement",
                    MarkupType.Underline => "underline", _ => "strikeout",
                };
            }
            else if (point is { } p)
            {
                var (text, subject, color) = item.Kind switch
                {
                    DocxItemKind.Comment => (item.Text, (string?)null, Yellow),
                    DocxItemKind.Insertion => ($"Insert: “{item.Text.Trim()}”", "Inserted Text", Blue),
                    DocxItemKind.MoveTo => ($"Moved here: “{item.Text.Trim()}”", "Moved Text", Blue),
                    DocxItemKind.Replacement => ($"Replace with: “{item.Text.Trim()}”", "Inserted Text", Blue),
                    DocxItemKind.Deletion or DocxItemKind.MoveFrom => ($"Deleted: “{item.Text.Trim()}”", "Deleted Text", Red),
                    _ => (item.Text, "Formatting", Green),
                };
                var note = Note(item, text, subject, color);
                note.NativeId = $"docx-{kind}-{item.Id}-{idTag}";
                var lineRect = new RectF(p.Line.Left, p.Line.Top, p.Line.Right, p.Line.Bottom);
                var spot = NotePlacement.Beside(lineRect, pages.Text(p.Page).CharBoxes, pages.Size(p.Page),
                    MarkupPlanService.OccupiedIcons(file, p.Page));
                (note.X, note.Y) = (spot.X, spot.Y);
                On(p.Page).Add(note);
                head = note;
                headPage = p.Page;
                written = "note";
            }
            else
            {
                results.Add(Fail(item.Part == "document"
                    ? "its anchor is not in the PDF"
                    : $"its anchor is not in the PDF (it is in {item.Part}, which may not export bookmarks)"));
                continue;
            }

            if (item.Kind == DocxItemKind.Comment)
            {
                heads[item.Id] = (head, headPage);
                if (item.Done) head.State = ReviewState.Completed;
            }
            results.Add(new()
            {
                Kind = kind, Id = item.Id, Author = item.Author, Success = true, Page = headPage + 1,
                EndPage = endPage, Annotation = written, Warning = warning,
            });
        }

        return (file, results);
    }

    private static TextNoteAnnotation Note(DocxReviewItem item, string text, string? subject, string color)
    {
        var note = new TextNoteAnnotation { Text = text, Subject = subject };
        Stamp(note, item, color);
        return note;
    }

    private static void Stamp(Annotation annotation, DocxReviewItem item, string color)
    {
        annotation.Color = color;
        annotation.Author = item.Author;
        annotation.CreatedUtc = item.Date;
        annotation.ModifiedUtc = item.Date;
        annotation.Source = AnnotationSource.RailReader;
    }

    /// <summary>
    /// The character ranges between two anchors, one per page, with blanks trimmed from both ends.
    /// Empty when the range holds no visible character.
    /// </summary>
    private static List<(int Page, int Start, int Length)> Pieces(Pages pages, Anchor s, Anchor e)
    {
        var result = new List<(int, int, int)>();
        if (e.Page < s.Page) return result;
        for (int page = s.Page; page <= e.Page; page++)
        {
            var text = pages.Text(page).Text;
            int from = page == s.Page ? s.Boundary : 0;
            int to = page == e.Page ? e.Boundary : text.Length;
            while (from < to && IsBlank(text[from])) from++;
            while (to > from && IsBlank(text[to - 1])) to--;
            if (to > from) result.Add((page, from, to - from));
        }
        return result;
    }

    /// <summary>
    /// Keeps the rects centred between the page's top and bottom margins. A range that runs to or
    /// from a page break covers the page's text up to its edge, and the running header and footer
    /// are part of that text — at the end of it (LibreOffice 24.2) or before the body (26.2), so no
    /// rule based on text order or on gaps can find them. Their place on the page can: Word sets
    /// headers and footers in the margins, and body text never enters them.
    /// </summary>
    internal static List<RectF> WithinBody(List<RectF> rects, (float Top, float Bottom) margins, double pageHeight)
    {
        float top = margins.Top, bottom = (float)pageHeight - margins.Bottom;
        return rects.Where(r => (r.Top + r.Bottom) / 2f is var mid && mid >= top && mid <= bottom).ToList();
    }

    /// <summary>
    /// The character boundary (an index into the page text: the position before that character)
    /// nearest a destination's position, and the glyph box it was judged against. A destination
    /// sits at the top of its line, so it is probed half a line height down. Null on a page with no
    /// visible text.
    /// </summary>
    internal static (int Boundary, CharBox Line)? BoundaryAt(PageText page, float x, float y)
    {
        var boxes = page.CharBoxes
            .Where(b => b.Right > b.Left && b.Bottom > b.Top && b.Index < page.Text.Length && !IsBlank(page.Text[b.Index]))
            .ToList();
        if (boxes.Count == 0) return null;

        var heights = boxes.Select(b => b.Bottom - b.Top).Order().ToList();
        float probe = y + heights[heights.Count / 2] / 2f;

        static float Distance(float v, float lo, float hi) => v < lo ? lo - v : v > hi ? v - hi : 0f;

        // The line is every glyph overlapping the closest one vertically — not every glyph as close
        // as it: x-height letters start lower than ascenders, so a distance test keeps only the
        // tall letters of the line and the boundary snaps to the nearest of those.
        var seed = boxes.MinBy(b => Distance(probe, b.Top, b.Bottom));
        var nearest = boxes
            .Where(b => MathF.Min(b.Bottom, seed.Bottom) - MathF.Max(b.Top, seed.Top)
                        >= 0.5f * MathF.Min(b.Bottom - b.Top, seed.Bottom - seed.Top))
            .OrderBy(b => Distance(x, b.Left, b.Right))
            .ThenBy(b => MathF.Abs(x - (b.Left + b.Right) / 2f))
            .First();
        int boundary = x <= (nearest.Left + nearest.Right) / 2f ? nearest.Index : nearest.Index + 1;
        return (boundary, nearest);
    }

    private static bool IsBlank(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || c == '­';
}
