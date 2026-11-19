using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailMark.Services;

public static class ScreenshotService
{
    private const int RenderDpi = 300;
    private const double PdfPointsPerInch = 72.0;
    private const double Scale = RenderDpi / PdfPointsPerInch;
    // Margin around each annotation: 40 px at 300 dpi, as when crops were cut from a page render.
    private const double PaddingPt = 40 / Scale;

    // PDF-point distance within which freehand strokes are merged into one screenshot.
    private const double MergeDistancePt = 50.0;

    /// <summary>
    /// Renders each annotation's area via <paramref name="pdf"/> — only that area, not the whole
    /// page (<see cref="PdfRegionRendering.RenderRegionPixmap"/>, which falls back to rendering
    /// the page and cropping on a backend without region rendering).
    /// Freehand annotations on the same page are grouped when spatially close,
    /// producing one combined screenshot per group.
    /// Returns a mapping of (0-based pageIndex, annotationIndex) → saved image path.
    /// </summary>
    public static Task<Dictionary<(int page, int annotIdx), string>> CropAnnotationsAsync(
        IPdfService pdf, AnnotationFile annotationFile, string imageDir)
    {
        Directory.CreateDirectory(imageDir);

        var result = new Dictionary<(int, int), string>();
        int imageCounter = 0;

        foreach (var (pageIdx, annotations) in annotationFile.Pages.OrderBy(p => p.Key))
        {
            var rectIndices = new List<int>();
            var freehandIndices = new List<int>();

            for (int i = 0; i < annotations.Count; i++)
            {
                switch (annotations[i])
                {
                    case RectAnnotation:
                        rectIndices.Add(i);
                        break;
                    case FreehandAnnotation:
                        freehandIndices.Add(i);
                        break;
                }
            }

            if (rectIndices.Count == 0 && freehandIndices.Count == 0)
                continue;

            var (pageW, pageH) = pdf.GetPageSize(pageIdx);

            string? RenderCrop(float x, float y, float w, float h)
            {
                var region = ToRegion(x, y, w, h, pageW, pageH);
                if (region == null) return null;

                var (rgb, pxW, pxH) = pdf.RenderRegionPixmap(pageIdx, region.Value, Scale);
                imageCounter++;
                var outPath = Path.Combine(imageDir, $"annotation_{imageCounter:D3}.png");
                SavePng(rgb, pxW, pxH, outPath);
                return outPath;
            }

            // Rect annotations: one crop per annotation
            foreach (var i in rectIndices)
            {
                var rect = (RectAnnotation)annotations[i];
                if (RenderCrop(rect.X, rect.Y, rect.W, rect.H) is { } path)
                    result[(pageIdx, i)] = path;
            }

            // Freehand annotations: group spatially close strokes, one crop per group
            foreach (var group in GroupFreehandAnnotations(annotations, freehandIndices))
            {
                var bounds = GetGroupBounds(annotations, group);
                if (bounds == null) continue;

                if (RenderCrop(bounds.Value.x, bounds.Value.y, bounds.Value.w, bounds.Value.h) is not { } path)
                    continue;
                foreach (var i in group)
                    result[(pageIdx, i)] = path;
            }
        }

        return Task.FromResult(result);
    }

    internal static List<List<int>> GroupFreehandAnnotations(List<Annotation> annotations, List<int> indices)
    {
        if (indices.Count == 0) return [];

        var parent = new int[indices.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }

        // Nullable: zero-point strokes have no bounds and must not be merged with others.
        var bounds = new (float minX, float minY, float maxX, float maxY)?[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            var fh = (FreehandAnnotation)annotations[indices[i]];
            if (fh.Points.Count > 0)
                bounds[i] = (fh.Points.Min(p => p.X), fh.Points.Min(p => p.Y),
                             fh.Points.Max(p => p.X), fh.Points.Max(p => p.Y));
        }

        for (int i = 0; i < indices.Count; i++)
            for (int j = i + 1; j < indices.Count; j++)
            {
                var bi = bounds[i]; var bj = bounds[j];
                if (bi.HasValue && bj.HasValue &&
                    Find(i) != Find(j) && BBoxDistance(bi.Value, bj.Value) <= MergeDistancePt)
                    Union(i, j);
            }

        var groupMap = new Dictionary<int, List<int>>();
        for (int i = 0; i < indices.Count; i++)
        {
            var root = Find(i);
            if (!groupMap.ContainsKey(root)) groupMap[root] = [];
            groupMap[root].Add(indices[i]);
        }

        return [.. groupMap.Values];
    }

    internal static double BBoxDistance(
        (float minX, float minY, float maxX, float maxY) a,
        (float minX, float minY, float maxX, float maxY) b)
    {
        var dx = Math.Max(0, Math.Max(a.minX - b.maxX, b.minX - a.maxX));
        var dy = Math.Max(0, Math.Max(a.minY - b.maxY, b.minY - a.maxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static (float x, float y, float w, float h)? GetGroupBounds(
        List<Annotation> annotations, List<int> indices)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;

        foreach (var i in indices)
        {
            var fh = (FreehandAnnotation)annotations[i];
            foreach (var pt in fh.Points)
            {
                minX = Math.Min(minX, pt.X); minY = Math.Min(minY, pt.Y);
                maxX = Math.Max(maxX, pt.X); maxY = Math.Max(maxY, pt.Y);
                any = true;
            }
        }

        return any ? (minX, minY, maxX - minX, maxY - minY) : null;
    }

    /// <summary>
    /// The page region to render for an annotation box: padded by <see cref="PaddingPt"/> on every
    /// side and clamped to the page, or null when nothing of it lies on the page.
    /// </summary>
    internal static BBox? ToRegion(float x, float y, float w, float h, double pageW, double pageH)
    {
        // Annotation coordinates are top-down: CompositeAnnotationStore converts out of the PDF's
        // bottom-up space when it loads them, which is also the space the region renderer takes,
        // so no flip. ToPixelRect once flipped here as well, which mirrored every crop vertically
        // (issue #22).
        double left = Math.Max(0, x - PaddingPt), top = Math.Max(0, y - PaddingPt);
        double right = Math.Min(pageW, x + w + PaddingPt), bottom = Math.Min(pageH, y + h + PaddingPt);

        return right > left && bottom > top
            ? new BBox((float)left, (float)top, (float)(right - left), (float)(bottom - top))
            : null;
    }

    /// <summary>Writes packed RGB (3 bytes per pixel, as the region renderer returns it) as a PNG.</summary>
    internal static void SavePng(byte[] rgb, int width, int height, string outputPath)
    {
        var rgba = new byte[width * height * 4];
        for (int src = 0, dst = 0; src < width * height * 3; src += 3, dst += 4)
        {
            rgba[dst] = rgb[src];
            rgba[dst + 1] = rgb[src + 1];
            rgba[dst + 2] = rgb[src + 2];
            rgba[dst + 3] = 255;
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixelCopy(info, rgba, width * 4);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }
}
