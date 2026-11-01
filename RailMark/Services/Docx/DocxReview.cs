namespace RailMark.Services.Docx;

/// <summary>What a Word review item is, once it has been read out of the document.</summary>
internal enum DocxItemKind
{
    /// <summary>A comment on a range of text (or, with no range, on a point).</summary>
    Comment,
    /// <summary>A reply to a comment (Word 2013+ threads, <c>commentsExtended.xml</c>).</summary>
    Reply,
    /// <summary>Tracked insertion (<c>w:ins</c>). Absent from the rendered original.</summary>
    Insertion,
    /// <summary>Tracked deletion (<c>w:del</c>). Present in the rendered original.</summary>
    Deletion,
    /// <summary>A deletion immediately followed by an insertion by the same author.</summary>
    Replacement,
    /// <summary>Text moved away from here (<c>w:moveFrom</c>). Present in the rendered original.</summary>
    MoveFrom,
    /// <summary>Text moved to here (<c>w:moveTo</c>). Absent from the rendered original.</summary>
    MoveTo,
    /// <summary>Run formatting change (<c>w:rPrChange</c>); the original formatting is rendered.</summary>
    Formatting,
}

/// <summary>
/// One comment or tracked change, with the names of the bookmarks the preparer put around it in
/// the rewritten document. A range has a start and an end bookmark; a point (an insertion, which
/// has no text in the original, or a comment with no range) has only <see cref="PointMark"/>.
/// </summary>
internal sealed class DocxReviewItem
{
    public required DocxItemKind Kind { get; init; }

    /// <summary>The Word id (<c>w:id</c>) for comments; a running number for revisions.</summary>
    public required string Id { get; init; }

    public string? Author { get; init; }
    public DateTimeOffset? Date { get; init; }

    /// <summary>
    /// The comment body; for a revision, the text it inserted or deleted (for a replacement, the
    /// inserted text — the deleted text is what the range covers); for a format change, a
    /// description such as "Bold, not Italic".
    /// </summary>
    public string Text { get; set; } = "";

    public string? StartMark { get; set; }
    public string? EndMark { get; set; }
    public string? PointMark { get; set; }

    /// <summary>For a reply: the <see cref="Id"/> of the comment it answers.</summary>
    public string? ParentId { get; set; }

    /// <summary>The comment was marked resolved (<c>w15:done="1"</c>).</summary>
    public bool Done { get; set; }

    /// <summary>The story the item sits in (<c>document</c>, <c>footnotes</c>, <c>header1</c>…).</summary>
    public string Part { get; set; } = "document";
}

/// <summary>What <see cref="DocxReviewPreparer"/> read and how it rewrote the document.</summary>
internal sealed class DocxReview
{
    /// <summary>Every item in document order; replies follow the comments they answer.</summary>
    public List<DocxReviewItem> Items { get; } = [];

    /// <summary>Prefix of every bookmark the preparer added, unique to this run.</summary>
    public required string MarkPrefix { get; init; }

    /// <summary>Revisions the prototype leaves as they are in the rendered original (counted by kind).</summary>
    public Dictionary<string, int> Unsupported { get; } = new(StringComparer.Ordinal);

    internal void CountUnsupported(string what) => Unsupported[what] = Unsupported.GetValueOrDefault(what) + 1;
}
