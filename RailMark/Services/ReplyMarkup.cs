using RailReader.Core.Models;

namespace RailMark.Services;

/// <summary>
/// How RailMark links a reply to the annotation it answers.
/// </summary>
/// <remarks>
/// PDF threads a reply to its parent with /IRT. Core 0.71 writes it (as a post-pass over PDFium's
/// output), but not for signed or read-only files, which go to the sidecar, nor for RC4/AES-128
/// files, and PDFs RailMark wrote earlier have none. RailMark therefore also chooses the reply's
/// own /NM: the parent's /NM, then <see cref="Marker"/>, then a fresh id. /NM round-trips through
/// PDFium and through viewers untouched, and <see cref="ParentOf"/> recovers it on the way back
/// in — preferring a genuine /IRT whenever there is one.
/// </remarks>
internal static class ReplyMarkup
{
    public const string Marker = "#reply-";

    /// <summary>A fresh /NM for a reply to <paramref name="parentId"/>.</summary>
    public static string NewId(string parentId) => $"{parentId}{Marker}{Guid.NewGuid():N}";

    /// <summary>
    /// The /NM of the annotation this one replies to, or null. A reply to a reply carries its
    /// whole ancestry in its id, so the parent is everything before the last marker — provided
    /// that marker begins the id's final segment.
    /// </summary>
    public static string? ParentOf(Annotation annotation)
    {
        if (!string.IsNullOrEmpty(annotation.InReplyTo)) return annotation.InReplyTo;
        return IdSuffix.Prefix(annotation.NativeId, Marker);
    }
}

/// <summary>
/// Reads the ids RailMark composes as <c>&lt;base&gt;&lt;marker&gt;&lt;fresh id&gt;</c>.
/// </summary>
internal static class IdSuffix
{
    /// <summary>
    /// The base of <paramref name="id"/> when its <b>last</b> segment was added with
    /// <paramref name="marker"/>, else null — so the continuation of a reply
    /// (<c>p#reply-x#cont-y</c>) is not itself read as a reply to <c>p</c>.
    /// </summary>
    public static string? Prefix(string? id, string marker)
    {
        if (id is null) return null;
        var at = id.LastIndexOf(marker, StringComparison.Ordinal);
        if (at <= 0) return null;
        return id.IndexOf('#', at + marker.Length) >= 0 ? null : id[..at];
    }
}
