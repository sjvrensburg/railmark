using RailMark.Services;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using SkiaSharp;

namespace RailMark.Tests;

/// <summary>
/// Geometry tests for ScreenshotService. These need no PDF and no pdfium — the parts of the
/// service that decide *which pixels to crop* are pure functions, and that is where the bugs
/// live (issue #22 was a double-applied Y flip that shipped unnoticed).
/// </summary>
public class ScreenshotServiceTests
{
    // A 612x792pt page rendered at the service's 300 dpi: 612 * (300/72) x 792 * (300/72).
    private const int PageBmpW = 2550;
    private const int PageBmpH = 3300;
    private const double Scale = 300.0 / 72.0;
    private const int Padding = 40;

    private static FreehandAnnotation Stroke(params (float x, float y)[] points)
        => new() { Color = "#F00", Points = [.. points.Select(p => new PointF(p.x, p.y))] };

    /// <summary>
    /// Serves a synthetic page bitmap, so the whole crop pipeline can be driven without pdfium.
    /// The page is white with a red band across its top eighth, which makes the vertical
    /// placement of a crop checkable by sampling one pixel.
    /// </summary>
    private sealed class FakePdfService : IPdfService, IDisposable
    {
        private readonly List<SKBitmap> _issued = [];

        public byte[] PdfBytes => [];
        public int PageCount => 1;
        public List<OutlineEntry> Outline => [];
        public (double Width, double Height) GetPageSize(int pageIndex) => (612, 792);

        public IRenderedPage RenderPage(int pageIndex, int dpi = 200)
        {
            var bitmap = new SKBitmap(PageBmpW, PageBmpH);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                using var paint = new SKPaint { Color = SKColors.Red };
                canvas.DrawRect(new SKRect(0, 0, PageBmpW, PageBmpH / 8f), paint);
            }
            _issued.Add(bitmap);
            return new SkiaRenderedPage(bitmap);
        }

        public IRenderedPage RenderThumbnail(int pageIndex) => throw new NotSupportedException();
        public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize)
            => throw new NotSupportedException();

        public void Dispose()
        {
            foreach (var b in _issued) b.Dispose();
        }
    }

    private static AnnotationFile FileWith(params Annotation[] annotations)
    {
        var file = new AnnotationFile { SourcePdf = "test.pdf" };
        file.Pages[0] = [.. annotations];
        return file;
    }

    // --- ToPixelRect: coordinate mapping ---

    [Fact]
    public void ToPixelRect_Maps_A_Top_Of_Page_Annotation_To_The_Top_Of_The_Bitmap()
    {
        // Annotation coordinates arrive top-down: CompositeAnnotationStore has already converted
        // from the PDF's bottom-up space. y=62 means 62pt below the top edge.
        var rect = ScreenshotService.ToPixelRect(72, 62, 228, 40, PageBmpW, PageBmpH);

        Assert.NotNull(rect);
        var expectedTop = (int)(62 * Scale) - Padding;
        Assert.Equal(expectedTop, rect!.Value.Top);
        // Sanity: firmly in the upper third, not mirrored into the lower one.
        Assert.True(rect.Value.Top < PageBmpH / 3,
            $"expected a top-of-page crop, got Top={rect.Value.Top} of {PageBmpH}");
    }

    [Fact]
    public void ToPixelRect_Maps_A_Bottom_Of_Page_Annotation_To_The_Bottom_Of_The_Bitmap()
    {
        var rect = ScreenshotService.ToPixelRect(72, 700, 228, 40, PageBmpW, PageBmpH);

        Assert.NotNull(rect);
        Assert.True(rect!.Value.Top > PageBmpH * 2 / 3,
            $"expected a bottom-of-page crop, got Top={rect.Value.Top} of {PageBmpH}");
    }

    [Fact]
    public void ToPixelRect_Preserves_Vertical_Ordering()
    {
        // Whatever the mapping, an annotation higher on the page must crop higher in the bitmap.
        var upper = ScreenshotService.ToPixelRect(72, 100, 100, 20, PageBmpW, PageBmpH);
        var lower = ScreenshotService.ToPixelRect(72, 500, 100, 20, PageBmpW, PageBmpH);

        Assert.NotNull(upper);
        Assert.NotNull(lower);
        Assert.True(upper!.Value.Top < lower!.Value.Top);
    }

    [Fact]
    public void ToPixelRect_Applies_Padding_On_Every_Side()
    {
        var rect = ScreenshotService.ToPixelRect(200, 200, 100, 50, PageBmpW, PageBmpH);

        Assert.NotNull(rect);
        Assert.Equal((int)(200 * Scale) - Padding, rect!.Value.Left);
        Assert.Equal((int)(200 * Scale) - Padding, rect.Value.Top);
        Assert.Equal((int)(100 * Scale) + Padding * 2, rect.Value.Width);
        Assert.Equal((int)(50 * Scale) + Padding * 2, rect.Value.Height);
    }

    // --- ToPixelRect: clamping and degenerate input ---

    [Fact]
    public void ToPixelRect_Clamps_At_The_Top_Left_Corner()
    {
        // Padding would push the origin negative; it must clamp to 0 instead.
        var rect = ScreenshotService.ToPixelRect(0, 0, 50, 50, PageBmpW, PageBmpH);

        Assert.NotNull(rect);
        Assert.Equal(0, rect!.Value.Left);
        Assert.Equal(0, rect.Value.Top);
    }

    [Fact]
    public void ToPixelRect_Clamps_At_The_Bottom_Right_Corner()
    {
        var rect = ScreenshotService.ToPixelRect(562, 742, 50, 50, PageBmpW, PageBmpH);

        Assert.NotNull(rect);
        Assert.True(rect!.Value.Right <= PageBmpW, $"Right={rect.Value.Right} exceeds {PageBmpW}");
        Assert.True(rect.Value.Bottom <= PageBmpH, $"Bottom={rect.Value.Bottom} exceeds {PageBmpH}");
    }

    [Fact]
    public void ToPixelRect_Clamps_A_Rect_Larger_Than_The_Page()
    {
        var rect = ScreenshotService.ToPixelRect(0, 0, 5000, 5000, PageBmpW, PageBmpH);

        Assert.NotNull(rect);
        Assert.True(rect!.Value.Right <= PageBmpW);
        Assert.True(rect.Value.Bottom <= PageBmpH);
    }

    [Fact]
    public void ToPixelRect_Returns_Null_For_A_Rect_Off_The_Page()
    {
        Assert.Null(ScreenshotService.ToPixelRect(5000, 5000, 10, 10, PageBmpW, PageBmpH));
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

    // --- CropAndSave (Tier 2: synthetic bitmap, still no pdfium) ---

    [Fact]
    public void CropAndSave_Writes_The_Requested_Region()
    {
        // Left half white, right half red — so the crop's colour proves which region was taken.
        using var source = new SKBitmap(200, 100);
        using (var canvas = new SKCanvas(source))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(100, 0, 200, 100), paint);
        }

        var path = Path.Combine(Path.GetTempPath(), $"railmark-crop-{Guid.NewGuid():N}.png");
        try
        {
            ScreenshotService.CropAndSave(source, new SKRectI(100, 0, 200, 100), path);

            Assert.True(File.Exists(path));
            using var written = SKBitmap.Decode(path);
            Assert.Equal(100, written.Width);
            Assert.Equal(100, written.Height);
            Assert.Equal(SKColors.Red, written.GetPixel(50, 50));
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
        using var pdf = new FakePdfService();
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
        using var pdf = new FakePdfService();
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
        using var pdf = new FakePdfService();
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
    public async Task CropAnnotations_Ignores_Annotations_With_No_Screenshot()
    {
        // Highlights and notes are rendered as text, not images.
        using var pdf = new FakePdfService();
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
