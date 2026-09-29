using RailReader.Core.Models;

namespace RailMark.Services;

/// <summary>
/// How RailMark links a reply to the annotation it answers.
/// </summary>
/// <remarks>
/// PDF threads a reply to its parent with /IRT, an indirect object reference PDFium can read but
/// not write. RailMark instead chooses the reply's own /NM: the parent's /NM, then
/// <see cref="Marker"/>, then a fresh id. /NM round-trips through PDFium and through viewers
/// untouched, so the link survives the write, and <see cref="ParentOf"/> recovers it on the way
/// back in — preferring a genuine /IRT (a reply written by Acrobat) whenever there is one.
/// </remarks>
internal static class ReplyMarkup
{
    public const string Marker = "#reply-";

    /// <summary>A fresh /NM for a reply to <paramref name="parentId"/>.</summary>
    public static string NewId(string parentId) => $"{parentId}{Marker}{Guid.NewGuid():N}";

    /// <summary>
    /// The /NM of the annotation this one replies to, or null. A reply to a reply carries its
    /// whole ancestry in its id, so the parent is everything before the last marker.
    /// </summary>
    public static string? ParentOf(Annotation annotation)
    {
        if (!string.IsNullOrEmpty(annotation.InReplyTo)) return annotation.InReplyTo;
        if (annotation.NativeId is not { } id) return null;

        var marker = id.LastIndexOf(Marker, StringComparison.Ordinal);
        return marker > 0 ? id[..marker] : null;
    }
}
