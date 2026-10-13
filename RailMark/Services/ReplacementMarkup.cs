using RailReader.Core.Models;

namespace RailMark.Services;

/// <summary>
/// How RailMark writes a "replace this text with that" edit into a PDF.
/// </summary>
/// <remarks>
/// Acrobat expresses a replacement as a caret carrying the new text, with a strikeout over the
/// old text linked to it by /IRT. PDFium can neither create a caret nor write /IRT, so RailMark
/// writes a single strikeout instead, tagged with a /Subj of <see cref="Subject"/> and with the
/// replacement spelled out at the head of /Contents — legible as-is in any viewer, and
/// recognisable again when RailMark extracts the annotations.
/// </remarks>
internal static class ReplacementMarkup
{
    public const string Subject = "Replacement";
    private const string Prefix = "Replace with: ";
    private const string CommentSeparator = "\n\n";

    public static string FormatContents(string replacement, string? comment)
        => string.IsNullOrWhiteSpace(comment)
            ? Prefix + replacement
            : Prefix + replacement + CommentSeparator + comment;

    /// <summary>
    /// Recognises a strikeout written by <see cref="FormatContents"/> and splits its /Contents
    /// back into the replacement text and the optional reviewer comment.
    /// </summary>
    public static bool TryParse(Annotation annotation, out string replacement, out string? comment)
    {
        replacement = "";
        comment = null;
        if (annotation is not StrikeOutAnnotation
            || annotation.Subject != Subject
            || annotation.Contents is not { } contents
            || !contents.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        // Viewers may round-trip the line breaks as \r\n or \r.
        var body = contents[Prefix.Length..].Replace("\r\n", "\n").Replace('\r', '\n');
        var split = body.IndexOf(CommentSeparator, StringComparison.Ordinal);
        if (split < 0)
        {
            replacement = body;
        }
        else
        {
            replacement = body[..split];
            comment = body[(split + CommentSeparator.Length)..];
        }
        return true;
    }
}
