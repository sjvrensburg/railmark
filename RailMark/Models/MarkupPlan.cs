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

    /// <summary>Exact, verbatim substring expected to appear in the page's extracted text.</summary>
    public required string Quote { get; init; }

    public required MarkupType Type { get; init; }

    /// <summary>Maps to Annotation.Contents for markup types, or TextNoteAnnotation.Text for notes.</summary>
    public string? Comment { get; init; }

    /// <summary>Hex color; falls back to a type-specific default when omitted.</summary>
    public string? Color { get; init; }

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
}

[JsonConverter(typeof(JsonStringEnumConverter<MarkupType>))]
public enum MarkupType
{
    Highlight,
    Underline,
    Strikeout,
    Squiggly,
    Note
}
