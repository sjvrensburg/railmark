using RailMark.Services;
using RailReader.Core.Models;
using RailReader.Core.Services;
using SkiaSharp;

namespace RailMark.Tests;

/// <summary>
/// Geometry tests for ScreenshotService. These need no PDF and no pdfium — the parts of the
/// service that decide *which region to render* are pure functions, and that is where the bugs
/// live (issue #22 was a double-applied Y flip that shipped unnoticed).
/// </summary>
public class ScreenshotServiceTests
{
    private const double PageW = 612, PageH = 792;
    private const double Scale = 300.0 / 72.0;
    private const double PaddingPt = 40 / Scale;

    // The fake page is red across its top eighth, white below, which makes the vertical placement
    // of a crop checkable by sampling one pixel.
    private const double RedBandPt = PageH / 8;

    private static FreehandAnnotation Stroke(params (float x, float y)[] points)
        => new() { Color = "#F00", Points = [.. points.Select(p => new PointF(p.x, p.y))] };

    /// <summary>The fake page's colour at a page-point y.</summary>
    private static byte[] PixelAt(double yPt) => yPt < RedBandPt ? [255, 0, 0] : [255, 255, 255];

    /// <summary>
    /// A backend without region rendering: only <see cref="IPdfService.RenderPagePixmap(int, int)"/>,
    /// so Core's <see cref="PdfRegionRendering"/> renders the page and crops.
    /// </summary>
    private class FakePdfService : IPdfService
    {
        public byte[] PdfBytes => [];
        public int PageCount => 1;
        public List<OutlineEntry> Outline => [];
        public (double Width, double Height) GetPageSize(int pageIndex) => (PageW, PageH);

        public IRenderedPage RenderPage(int pageIndex, int dpi = 200) => throw new NotSupportedException();
        public IRenderedPage RenderThumbnail(int pageIndex) => throw new NotSupportedException();

        public virtual (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
        {
            double scale = targetSize / Math.Max(PageW, PageH);
            int w = (int)(PageW * scale), h = (int)(PageH * scale);
            var rgb = new byte[w * h * 3];
            for (int row = 0; row < h; row++)
            {
                var px = PixelAt(row / scale);
                for (int col = 0; col < w; col++) px.CopyTo(rgb, (row * w + col) * 3);
            }
            return (rgb, w, h);
        }
    }

    /// <summary>
    /// A backend with region rendering, as <c>SkiaPdfService</c> has: paints only the requested
    /// region, records each request, and refuses whole-page renders.
    /// </summary>
    private sealed class FakeRegionPdfService : FakePdfService, IPdfRegionRenderService
    {
        public List<(BBox Region, double Scale)> Requests { get; } = [];

        public override (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
            => throw new NotSupportedException("whole-page render");

        public (byte[] RgbBytes, int Width, int Height) RenderRegionPixmap(int pageIndex, BBox region,
            double scale, int viewRotation = 0, CancellationToken ct = default)
        {
            Requests.Add((region, scale));
            int x0 = (int)Math.Floor(region.X * scale), y0 = (int)Math.Floor(region.Y * scale);
            int w = (int)Math.Ceiling((region.X + region.W) * scale) - x0;
            int h = (int)Math.Ceiling((region.Y + region.H) * scale) - y0;
            var rgb = new byte[w * h * 3];
            for (int row = 0; row < h; row++)
            {
                var px = PixelAt((y0 + row) / scale);
                for (int col = 0; col < w; col++) px.CopyTo(rgb, (row * w + col) * 3);
            }
            return (rgb, w, h);
        }
    }

    private static AnnotationFile FileWith(params Annotation[] annotations)
    {
        var file = new AnnotationFile { SourcePdf = "test.pdf" };
        file.Pages[0] = [.. annotations];
        return file;
    }

    // --- ToRegion: coordinate mapping ---

    [Fact]
    public void ToRegion_Keeps_A_Top_Of_Page_Annotation_At_The_Top()
    {
        // Annotation coordinates arrive top-down: CompositeAnnotationStore has already converted
        // from the PDF's bottom-up space. y=62 means 62pt below the top edge, and the region
        // renderer takes the same space, so there is no flip (issue #22 was a double flip).
        var region = ScreenshotService.ToRegion(72, 62, 228, 40, PageW, PageH);

        Assert.NotNull(region);
        Assert.Equal(62 - PaddingPt, region!.Value.Y, 3);
    }

    [Fact]
    public void ToRegion_Preserves_Vertical_Ordering()
    {
        var upper = ScreenshotService.ToRegion(72, 100, 100, 20, PageW, PageH);
        var lower = ScreenshotService.ToRegion(72, 500, 100, 20, PageW, PageH);

        Assert.NotNull(upper);
        Assert.NotNull(lower);
        Assert.True(upper!.Value.Y < lower!.Value.Y);
    }

    [Fact]
    public void ToRegion_Applies_Padding_On_Every_Side()
    {
        var region = ScreenshotService.ToRegion(200, 200, 100, 50, PageW, PageH);

        Assert.NotNull(region);
        Assert.Equal(200 - PaddingPt, region!.Value.X, 3);
        Assert.Equal(200 - PaddingPt, region.Value.Y, 3);
        Assert.Equal(100 + 2 * PaddingPt, region.Value.W, 3);
        Assert.Equal(50 + 2 * PaddingPt, region.Value.H, 3);
    }

    // --- ToRegion: clamping and degenerate input ---

    [Fact]
    public void ToRegion_Clamps_At_The_Top_Left_Corner()
    {
        var region = ScreenshotService.ToRegion(0, 0, 50, 50, PageW, PageH);

        Assert.NotNull(region);
        Assert.Equal(0, region!.Value.X);
        Assert.Equal(0, region.Value.Y);
    }

    [Fact]
    public void ToRegion_Clamps_At_The_Bottom_Right_Corner()
    {
        var region = ScreenshotService.ToRegion(562, 742, 50, 50, PageW, PageH);

        Assert.NotNull(region);
        Assert.Equal(PageW, region!.Value.X + region.Value.W, 3);
        Assert.Equal(PageH, region.Value.Y + region.Value.H, 3);
    }

    [Fact]
    public void ToRegion_Clamps_A_Rect_Larger_Than_The_Page()
    {
        var region = ScreenshotService.ToRegion(0, 0, 5000, 5000, PageW, PageH);

        Assert.Equal(new BBox(0, 0, (float)PageW, (float)PageH), region);
    }

    [Fact]
    public void ToRegion_Returns_Null_For_A_Rect_Off_The_Page()
    {
        Assert.Null(ScreenshotService.ToRegion(5000, 5000, 10, 10, PageW, PageH));
    }

    // --- Freehand grouping (union-find, MergeDistancePt = 50) ---

    [Fact]
    public void GroupFreehand_Merges_Nearby_Strokes()
    {
        List<Annotation> annotations = [
            Stroke((100, 100), (110, 110)),
            Stroke((120, 120), (130, 130)),   // ~14pt away
        ];

        var groups = ScreenshotService.GroupFreehandAnnotations(annotations, [0, 1]);

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Count);
    }

    [Fact]
    public void GroupFreehand_Keeps_Distant_Strokes_Apart()
    {
        List<Annotation> annotations = [
            Stroke((100, 100), (110, 110)),
            Stroke((400, 400), (410, 410)),   // far beyond 50pt
        ];

        var groups = ScreenshotService.GroupFreehandAnnotations(annotations, [0, 1]);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void GroupFreehand_Merges_Transitively()
    {
        // A–B is 40pt and B–C is 40pt, so all three merge even though A–C is 80pt.
        List<Annotation> annotations = [
            Stroke((100, 100)),
            Stroke((140, 100)),
            Stroke((180, 100)),
        ];

        var groups = ScreenshotService.GroupFreehandAnnotations(annotations, [0, 1, 2]);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Count);
    }

    [Fact]
    public void GroupFreehand_Never_Merges_A_Stroke_With_No_Points()
    {
        // A stroke with no points has no bounds, so it must not be swept into a neighbour.
        List<Annotation> annotations = [
            Stroke((100, 100), (110, 110)),
            Stroke(),
        ];

        var groups = ScreenshotService.GroupFreehandAnnotations(annotations, [0, 1]);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g));
    }

    [Fact]
    public void GroupFreehand_Handles_No_Strokes()
    {
        Assert.Empty(ScreenshotService.GroupFreehandAnnotations([], []));
    }

    // --- BBoxDistance ---

    [Fact]
    public void BBoxDistance_Is_Zero_For_Overlapping_Boxes()
    {
        Assert.Equal(0, ScreenshotService.BBoxDistance((0, 0, 100, 100), (50, 50, 150, 150)));
    }

    [Fact]
    public void BBoxDistance_Measures_The_Gap_Between_Separated_Boxes()
    {
        // 30pt horizontal gap, aligned vertically.
        Assert.Equal(30, ScreenshotService.BBoxDistance((0, 0, 10, 10), (40, 0, 50, 10)), 3);
        // 3-4-5 triangle diagonally.
        Assert.Equal(5, ScreenshotService.BBoxDistance((0, 0, 10, 10), (13, 14, 20, 20)), 3);
    }

    // --- GetGroupBounds ---

    [Fact]
    public void GetGroupBounds_Unions_Every_Stroke_In_The_Group()
    {
        List<Annotation> annotations = [
            Stroke((100, 100), (150, 120)),
            Stroke((80, 130), (200, 90)),
        ];

        var bounds = ScreenshotService.GetGroupBounds(annotations, [0, 1]);

        Assert.NotNull(bounds);
        Assert.Equal(80, bounds!.Value.x);
        Assert.Equal(90, bounds.Value.y);
        Assert.Equal(120, bounds.Value.w);   // 200 - 80
        Assert.Equal(40, bounds.Value.h);    // 130 - 90
    }

    [Fact]
    public void GetGroupBounds_Returns_Null_When_No_Stroke_Has_Points()
    {
        Assert.Null(ScreenshotService.GetGroupBounds([Stroke()], [0]));
    }

    // --- SavePng ---

    [Fact]
    public void SavePng_Writes_Packed_Rgb_Pixels_In_Place()
    {
        // 2x1: a red pixel then a blue one, so a channel swap or a stride error shows.
        var path = Path.Combine(Path.GetTempPath(), $"railmark-png-{Guid.NewGuid():N}.png");
        try
        {
            ScreenshotService.SavePng([255, 0, 0, 0, 0, 255], 2, 1, path);

            using var written = SKBitmap.Decode(path);
            Assert.Equal(2, written.Width);
            Assert.Equal(1, written.Height);
            Assert.Equal(SKColors.Red, written.GetPixel(0, 0));
            Assert.Equal(SKColors.Blue, written.GetPixel(1, 0));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- CropAnnotationsAsync (Tier 3: whole pipeline, fake renderer, no pdfium) ---

    private static string NewImageDir()
        => Path.Combine(Path.GetTempPath(), $"railmark-imgs-{Guid.NewGuid():N}");

    [Fact]
    public async Task CropAnnotations_Crops_A_Top_Of_Page_Rect_From_The_Top_Of_The_Page()
    {
        // The fake page is red across its top eighth (0..99pt of 792pt), so an annotation at
        // y=20 must yield a red crop. Before the issue #22 fix this cropped the white bottom.
        var pdf = new FakeRegionPdfService();
        var file = FileWith(new RectAnnotation { Color = "#00F", X = 100, Y = 20, W = 200, H = 40 });
        var dir = NewImageDir();

        try
        {
            var images = await ScreenshotService.CropAnnotationsAsync(pdf, file, dir);

            Assert.Single(images);
            using var cropped = SKBitmap.Decode(images[(0, 0)]);
            Assert.Equal(SKColors.Red, cropped.GetPixel(cropped.Width / 2, cropped.Height / 2));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CropAnnotations_Crops_A_Bottom_Of_Page_Rect_From_The_Bottom_Of_The_Page()
    {
        var pdf = new FakeRegionPdfService();
        var file = FileWith(new RectAnnotation { Color = "#00F", X = 100, Y = 700, W = 200, H = 40 });
        var dir = NewImageDir();

        try
        {
            var images = await ScreenshotService.CropAnnotationsAsync(pdf, file, dir);

            using var cropped = SKBitmap.Decode(images[(0, 0)]);
            Assert.Equal(SKColors.White, cropped.GetPixel(cropped.Width / 2, cropped.Height / 2));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CropAnnotations_Gives_Merged_Freehand_Strokes_One_Shared_Image()
    {
        var pdf = new FakeRegionPdfService();
        var file = FileWith(
            Stroke((100, 100), (110, 110)),   // these two are ~14pt apart
            Stroke((120, 120), (130, 130)),
            Stroke((400, 400), (410, 410)));  // this one is far away
        var dir = NewImageDir();

        try
        {
            var images = await ScreenshotService.CropAnnotationsAsync(pdf, file, dir);

            // Three annotations, but only two distinct files: the near pair shares one.
            Assert.Equal(3, images.Count);
            Assert.Equal(images[(0, 0)], images[(0, 1)]);
            Assert.NotEqual(images[(0, 0)], images[(0, 2)]);
            Assert.Equal(2, Directory.GetFiles(dir, "*.png").Length);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CropAnnotations_Renders_Only_The_Padded_Annotation_Region_At_300_Dpi()
    {
        // The fake refuses whole-page renders, so getting an image at all proves none was made.
        var pdf = new FakeRegionPdfService();
        var file = FileWith(new RectAnnotation { Color = "#00F", X = 100, Y = 300, W = 200, H = 40 });
        var dir = NewImageDir();

        try
        {
            var images = await ScreenshotService.CropAnnotationsAsync(pdf, file, dir);

            Assert.Single(images);
            var (region, scale) = Assert.Single(pdf.Requests);
            Assert.Equal(Scale, scale, 6);
            Assert.Equal(ScreenshotService.ToRegion(100, 300, 200, 40, PageW, PageH), region);

            using var cropped = SKBitmap.Decode(images[(0, 0)]);
            // Region-sized, not page-sized (the page is 2550 px wide at this scale).
            var expectedWidth = (int)Math.Ceiling((200 + 2 * PaddingPt) * Scale);
            Assert.InRange(cropped.Width, expectedWidth - 1, expectedWidth + 1);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CropAnnotations_Works_On_A_Backend_Without_Region_Rendering()
    {
        // Core's fallback renders the page and crops; the crop must still come from the right place.
        var pdf = new FakePdfService();
        var file = FileWith(new RectAnnotation { Color = "#00F", X = 100, Y = 20, W = 200, H = 40 });
        var dir = NewImageDir();

        try
        {
            var images = await ScreenshotService.CropAnnotationsAsync(pdf, file, dir);

            using var cropped = SKBitmap.Decode(Assert.Single(images).Value);
            Assert.Equal(SKColors.Red, cropped.GetPixel(cropped.Width / 2, cropped.Height / 2));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CropAnnotations_Ignores_Annotations_With_No_Screenshot()
    {
        // Highlights and notes are rendered as text, not images.
        var pdf = new FakeRegionPdfService();
        var file = FileWith(
            new HighlightAnnotation { Color = "#FF0", Rects = [new(50, 50, 100, 10)] },
            new TextNoteAnnotation { Color = "#FF0", X = 10, Y = 10, Text = "note" });
        var dir = NewImageDir();

        try
        {
            var images = await ScreenshotService.CropAnnotationsAsync(pdf, file, dir);
            Assert.Empty(images);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
