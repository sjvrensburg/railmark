using System.Text.Json.Serialization;

namespace RailMark.Models;

public record MarkupPlan
{
    public List<MarkupEntry> Entries { get; init; } = [];
}

public record MarkupEntry
{
    /// <summary>1-based page number, matching RailMark's CLI convention.</summary>
    public required int Page { get; init; }

    /// <summary>
    /// Exact, verbatim substring expected to appear in the page's extracted text. May be omitted
    /// only by a note that replies to an annotation (<see cref="InReplyTo"/>); the note is then
    /// placed beside that annotation.
    /// </summary>
    public string? Quote { get; init; }

    /// <summary>Required, except for an <see cref="Update"/> entry, which leaves the type alone.</summary>
    public MarkupType? Type { get; init; }

    /// <summary>
    /// The type of a validated entry. Only meaningful when <see cref="Update"/> is not set —
    /// validation guarantees a type in every other case, so reaching this without one is a bug.
    /// </summary>
    [JsonIgnore]
    internal MarkupType Kind => Type
        ?? throw new InvalidOperationException("Entry has no type; only an update entry may omit it.");

    /// <summary>
    /// The id of an existing annotation on <see cref="Page"/> — as reported by annotation mode's
    /// <c>--format json</c> — to change in place. Only <see cref="Comment"/>, <see cref="Color"/>
    /// and <see cref="Author"/> are applied, and the annotation keeps its geometry, id and
    /// creation date; the quote, type and context are not consulted. Works for every annotation
    /// type, including ones RailMark cannot create.
    /// </summary>
    public string? Update { get; init; }

    /// <summary>Maps to Annotation.Contents for markup types, or TextNoteAnnotation.Text for notes.</summary>
    public string? Comment { get; init; }

    /// <summary>Hex color; falls back to a type-specific default when omitted.</summary>
    public string? Color { get; init; }

    /// <summary>
    /// Opacity (<c>/CA</c>), above 0 and at most 1, of a text-markup annotation; ignored for a note.
    /// Falls back to <c>--opacity</c>, then to 0.35 for a highlight (RailReader2's own default,
    /// which keeps text legible under any colour) and 1 for the other types. On an
    /// <see cref="Update"/> entry it may be set on an annotation of any type.
    /// </summary>
    public float? Opacity { get; init; }

    /// <summary>Defaults to "AI Reviewer" when omitted.</summary>
    public string? Author { get; init; }

    /// <summary>
    /// 1-based index of the match to mark when the quote occurs more than once on the page,
    /// counted in reading order among the matches that also satisfy any context given.
    /// </summary>
    public int? Occurrence { get; init; }

    /// <summary>Text that must immediately precede the quote, to pick one of several matches.</summary>
    public string? ContextBefore { get; init; }

    /// <summary>Text that must immediately follow the quote, to pick one of several matches.</summary>
    public string? ContextAfter { get; init; }

    /// <summary>
    /// The id of an existing annotation on the same page — as reported by annotation mode's
    /// <c>--format json</c> — that this entry replies to.
    /// </summary>
    public string? InReplyTo { get; init; }

    /// <summary>The text to put in place of the quote. Required for, and only valid with, <see cref="MarkupType.Replace"/>.</summary>
    public string? Replacement { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<MarkupType>))]
public enum MarkupType
{
    Highlight,
    Underline,
    Strikeout,
    Squiggly,
    Note,
    Replace
}
