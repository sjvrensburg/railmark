using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace Rr2Annotate.Services;

public static class ScreenshotService
{
    private const int RenderDpi = 300;
    private const double PdfPointsPerInch = 72.0;
    private static readonly double Scale = RenderDpi / PdfPointsPerInch;
    private const int PaddingPx = 40;

    // PDF-point distance within which freehand strokes are merged into one screenshot.
    private const double MergeDistancePt = 50.0;

    /// <summary>
    /// Renders the required pages via <paramref name="pdf"/> and crops annotation areas.
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

            using var rendered = pdf.RenderPage(pageIdx, RenderDpi);
            if (rendered is not SkiaRenderedPage skiaPage) continue;
            var bitmap = skiaPage.Bitmap;

            // Rect annotations: one crop per annotation
            foreach (var i in rectIndices)
            {
                var rect = (RectAnnotation)annotations[i];
                var pxRect = ToPixelRect(rect.X, rect.Y, rect.W, rect.H, bitmap.Width, bitmap.Height);
                if (pxRect == null) continue;

                imageCounter++;
                var outPath = Path.Combine(imageDir, $"annotation_{imageCounter:D3}.png");
                CropAndSave(bitmap, pxRect.Value, outPath);
                result[(pageIdx, i)] = outPath;
            }

            // Freehand annotations: group spatially close strokes, one crop per group
            var groups = GroupFreehandAnnotations(annotations, freehandIndices);

            foreach (var group in groups)
            {
                var bounds = GetGroupBounds(annotations, group);
                if (bounds == null) continue;

                var pxRect = ToPixelRect(bounds.Value.x, bounds.Value.y,
                    bounds.Value.w, bounds.Value.h, bitmap.Width, bitmap.Height);
                if (pxRect == null) continue;

                imageCounter++;
                var outPath = Path.Combine(imageDir, $"annotation_{imageCounter:D3}.png");
                CropAndSave(bitmap, pxRect.Value, outPath);

                foreach (var i in group)
                    result[(pageIdx, i)] = outPath;
            }
        }

        return Task.FromResult(result);
    }

    private static List<List<int>> GroupFreehandAnnotations(List<Annotation> annotations, List<int> indices)
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
                if (bounds[i].HasValue && bounds[j].HasValue &&
                    Find(i) != Find(j) && BBoxDistance(bounds[i].Value, bounds[j].Value) <= MergeDistancePt)
                    Union(i, j);

        var groupMap = new Dictionary<int, List<int>>();
        for (int i = 0; i < indices.Count; i++)
        {
            var root = Find(i);
            if (!groupMap.ContainsKey(root)) groupMap[root] = [];
            groupMap[root].Add(indices[i]);
        }

        return [.. groupMap.Values];
    }

    private static double BBoxDistance(
        (float minX, float minY, float maxX, float maxY) a,
        (float minX, float minY, float maxX, float maxY) b)
    {
        var dx = Math.Max(0, Math.Max(a.minX - b.maxX, b.minX - a.maxX));
        var dy = Math.Max(0, Math.Max(a.minY - b.maxY, b.minY - a.maxY));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static (float x, float y, float w, float h)? GetGroupBounds(
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

    private static SKRectI? ToPixelRect(float x, float y, float w, float h, int bmpW, int bmpH)
    {
        // PDF y-axis has origin at the bottom-left; bitmap y-axis has origin at the top-left.
        // Flip: pixel top = bmpH - (y + h) * Scale
        var px = (int)(x * Scale) - PaddingPx;
        var py = bmpH - (int)((y + h) * Scale) - PaddingPx;
        var pw = (int)(w * Scale) + PaddingPx * 2;
        var ph = (int)(h * Scale) + PaddingPx * 2;

        px = Math.Max(0, px);
        py = Math.Max(0, py);
        pw = Math.Min(pw, bmpW - px);
        ph = Math.Min(ph, bmpH - py);

        return pw > 0 && ph > 0 ? new SKRectI(px, py, px + pw, py + ph) : null;
    }

    private static void CropAndSave(SKBitmap source, SKRectI rect, string outputPath)
    {
        using var cropped = new SKBitmap(rect.Width, rect.Height);
        using var canvas = new SKCanvas(cropped);
        canvas.DrawBitmap(source, rect, new SKRect(0, 0, rect.Width, rect.Height));
        canvas.Flush();

        using var image = SKImage.FromBitmap(cropped);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }
}
