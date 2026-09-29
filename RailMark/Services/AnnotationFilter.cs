using RailReader.Core.Models;

namespace RailMark.Services;

/// <summary>Narrows an <see cref="AnnotationFile"/> to the annotations annotation mode reports.</summary>
internal static class AnnotationFilter
{
    /// <summary>
    /// Keeps the annotations matching <paramref name="predicate"/>, dropping pages left empty.
    /// </summary>
    public static AnnotationFile Where(AnnotationFile src, Func<Annotation, bool> predicate)
    {
        var dst = new AnnotationFile
        {
            Version = src.Version, SourcePdf = src.SourcePdf,
            SourcePdfPath = src.SourcePdfPath, Bookmarks = src.Bookmarks,
        };
        foreach (var (k, v) in src.Pages)
        {
            var filtered = v.Where(predicate).ToList();
            if (filtered.Count > 0) dst.Pages[k] = filtered;
        }
        return dst;
    }

    /// <summary>
    /// Keeps annotations by any of <paramref name="include"/> (when given) and none of
    /// <paramref name="exclude"/>. Names compare case-insensitively: this only narrows what is
    /// reported. An annotation with no author never matches an include list, and is never excluded.
    /// </summary>
    public static AnnotationFile ByAuthor(AnnotationFile src, IEnumerable<string>? include, IEnumerable<string>? exclude)
    {
        var included = include is null ? null : new HashSet<string>(include, StringComparer.OrdinalIgnoreCase);
        var excluded = exclude is null ? null : new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        return Where(src, a =>
            (included is null || (a.Author != null && included.Contains(a.Author)))
            && (excluded is null || a.Author is null || !excluded.Contains(a.Author)));
    }

    /// <summary>
    /// Keeps annotations changed on or after <paramref name="since"/> — last modified, or created
    /// when there is no modification date. An annotation with neither can't be shown to be recent,
    /// so it is dropped and counted in <paramref name="undated"/>.
    /// </summary>
    public static AnnotationFile Since(AnnotationFile src, DateTimeOffset since, out int undated)
    {
        undated = src.Pages.Values.Sum(p => p.Count(a => LastChanged(a) is null));
        return Where(src, a => LastChanged(a) >= since);
    }

    private static DateTimeOffset? LastChanged(Annotation a) => a.ModifiedUtc ?? a.CreatedUtc;
}
