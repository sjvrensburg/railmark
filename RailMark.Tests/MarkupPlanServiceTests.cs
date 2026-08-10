using System.Text.Json;
using System.Text.Json.Serialization;
using RailMark.Models;
using RailMark.Services;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailMark.Tests;

public class MarkupPlanServiceTests
{
    // --- Helpers ---

    private sealed class FakePdfService(int pageCount, (double Width, double Height) pageSize) : IPdfService
    {
        public byte[] PdfBytes => [];
        public int PageCount => pageCount;
        public List<OutlineEntry> Outline => [];
        public (double Width, double Height) GetPageSize(int pageIndex) => pageSize;
        public IRenderedPage RenderPage(int pageIndex, int dpi = 200) => throw new NotSupportedException();
        public IRenderedPage RenderThumbnail(int pageIndex) => throw new NotSupportedException();
        public (byte[] RgbBytes, int Width, int Height) RenderPagePixmap(int pageIndex, int targetSize) => throw new NotSupportedException();
    }

    private sealed class FakePdfTextService(Dictionary<int, string> pageTexts) : IPdfTextService
    {
        public PageText ExtractPageText(byte[] pdfBytes, int pageIndex, string? password = null)
            => new(pageTexts.GetValueOrDefault(pageIndex, ""), []);

        public List<List<RectF>> GetTextRangeRects(byte[] pdfBytes, int pageIndex, List<(int CharStart, int CharLength)> ranges, string? password = null)
            => ranges.Select(r => new List<RectF> { new(10f, 20f + r.CharStart, 10f + r.CharLength, 30f + r.CharStart) }).ToList();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // --- TextLocator.ResolveQuote ---

    [Fact]
    public void ResolveQuote_Exact_Match()
    {
        var range = TextLocator.ResolveQuote("The quick brown fox jumps.", "brown fox");
        Assert.NotNull(range);
        Assert.Equal("brown fox", "The quick brown fox jumps."[range!.Value.CharStart..(range.Value.CharStart + range.Value.CharLength)]);
    }

    [Fact]
    public void ResolveQuote_Whitespace_Normalized_Match()
    {
        var range = TextLocator.ResolveQuote("word one word  two word three", "word two");
        Assert.NotNull(range);
    }

    [Fact]
    public void ResolveQuote_Case_Insensitive()
    {
        var range = TextLocator.ResolveQuote("The Null Hypothesis", "the null hypothesis");
        Assert.NotNull(range);
    }

    [Fact]
    public void ResolveQuote_Not_Found_Returns_Null()
    {
        var range = TextLocator.ResolveQuote("The quick brown fox.", "a phrase that is not present");
        Assert.Null(range);
    }

    // --- MarkupPlan JSON ---

    [Fact]
    public void MarkupPlan_Deserializes_From_Example_Json()
    {
        const string json = """
            {
              "entries": [
                { "page": 3, "quote": "the null hypothesis is rejected", "type": "highlight",
                  "comment": "Verify against Table 2." },
                { "page": 5, "quote": "heteroscedasticity", "type": "underline" }
              ]
            }
            """;

        var plan = JsonSerializer.Deserialize<MarkupPlan>(json, JsonOptions);

        Assert.NotNull(plan);
        Assert.Equal(2, plan!.Entries.Count);
        Assert.Equal(MarkupType.Highlight, plan.Entries[0].Type);
        Assert.Equal("Verify against Table 2.", plan.Entries[0].Comment);
        Assert.Equal(MarkupType.Underline, plan.Entries[1].Type);
        Assert.Null(plan.Entries[1].Comment);
        Assert.Null(plan.Entries[1].Color);
        Assert.Null(plan.Entries[1].Author);
    }

    // --- MarkupPlanService.Resolve ---

    [Fact]
    public void Resolve_Converts_OneBased_Page_To_ZeroBased_Index()
    {
        var plan = new MarkupPlan { Entries = [new() { Page = 1, Quote = "brown fox", Type = MarkupType.Highlight }] };
        var pdf = new FakePdfService(pageCount: 3, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps." });

        var (file, result) = MarkupPlanService.Resolve(plan, pdf, text);

        Assert.True(result.AllSucceeded);
        Assert.True(file.Pages.ContainsKey(0));
        Assert.False(file.Pages.ContainsKey(1));
    }

    [Fact]
    public void Resolve_Reports_Failure_For_Missing_Quote_Without_Aborting_Plan()
    {
        var plan = new MarkupPlan
        {
            Entries =
            [
                new() { Page = 1, Quote = "not present anywhere", Type = MarkupType.Highlight },
                new() { Page = 1, Quote = "brown fox", Type = MarkupType.Highlight },
            ]
        };
        var pdf = new FakePdfService(pageCount: 1, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps." });

        var (file, result) = MarkupPlanService.Resolve(plan, pdf, text);

        Assert.False(result.AllSucceeded);
        Assert.False(result.Entries[0].Success);
        Assert.True(result.Entries[1].Success);
        Assert.Single(file.Pages[0]);
    }

    [Fact]
    public void Resolve_Preserves_Plan_Order_In_Report_Across_Interleaved_Pages()
    {
        var plan = new MarkupPlan
        {
            Entries =
            [
                new() { Page = 1, Quote = "quick brown", Type = MarkupType.Highlight },
                new() { Page = 2, Quote = "second page", Type = MarkupType.Highlight },
                new() { Page = 1, Quote = "lazy dog", Type = MarkupType.Highlight },
            ]
        };
        var pdf = new FakePdfService(pageCount: 2, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string>
        {
            [0] = "The quick brown fox jumps over the lazy dog.",
            [1] = "This is the second page of text.",
        });

        var (_, result) = MarkupPlanService.Resolve(plan, pdf, text);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal("quick brown", result.Entries[0].Quote);
        Assert.Equal("second page", result.Entries[1].Quote);
        Assert.Equal("lazy dog", result.Entries[2].Quote);
        Assert.All(result.Entries, e => Assert.True(e.Success));
    }

    [Fact]
    public void Resolve_Reports_SpanCount_On_Success_And_Zero_On_Failure()
    {
        var plan = new MarkupPlan
        {
            Entries =
            [
                new() { Page = 1, Quote = "brown fox", Type = MarkupType.Highlight },
                new() { Page = 1, Quote = "not in the text", Type = MarkupType.Highlight },
            ],
        };
        var pdf = new FakePdfService(pageCount: 1, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps." });

        var (_, result) = MarkupPlanService.Resolve(plan, pdf, text);

        Assert.True(result.Entries[0].Success);
        Assert.Equal(1, result.Entries[0].SpanCount);
        Assert.False(result.Entries[1].Success);
        Assert.Equal(0, result.Entries[1].SpanCount);
    }

    [Fact]
    public void Resolve_Reports_Failure_For_OutOfRange_Page()
    {
        var plan = new MarkupPlan { Entries = [new() { Page = 5, Quote = "anything", Type = MarkupType.Highlight }] };
        var pdf = new FakePdfService(pageCount: 2, pageSize: (600, 800));
        var text = new FakePdfTextService([]);

        var (_, result) = MarkupPlanService.Resolve(plan, pdf, text);

        Assert.False(result.AllSucceeded);
        Assert.Contains("out of range", result.Entries[0].Error);
    }

    [Theory]
    [InlineData(MarkupType.Highlight, typeof(HighlightAnnotation))]
    [InlineData(MarkupType.Underline, typeof(UnderlineAnnotation))]
    [InlineData(MarkupType.Strikeout, typeof(StrikeOutAnnotation))]
    [InlineData(MarkupType.Squiggly, typeof(SquigglyAnnotation))]
    public void Resolve_Builds_Correct_Annotation_Subtype(MarkupType type, Type expected)
    {
        var plan = new MarkupPlan { Entries = [new() { Page = 1, Quote = "brown fox", Type = type, Comment = "note", Color = "#123456", Author = "Reviewer" }] };
        var pdf = new FakePdfService(pageCount: 1, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps." });

        var (file, _) = MarkupPlanService.Resolve(plan, pdf, text);

        var annotation = Assert.Single(file.Pages[0]);
        Assert.IsType(expected, annotation);
        Assert.Equal("note", annotation.Contents);
        Assert.Equal("#123456", annotation.Color);
        Assert.Equal("Reviewer", annotation.Author);
        Assert.Equal(AnnotationSource.RailReader, annotation.Source);
        var markup = Assert.IsAssignableFrom<TextMarkupAnnotation>(annotation);
        Assert.NotEmpty(markup.Rects);
    }

    [Theory]
    [InlineData(MarkupType.Underline)]
    [InlineData(MarkupType.Squiggly)]
    public void Resolve_Draws_Underline_And_Squiggly_As_Thin_Band_Near_Baseline(MarkupType type)
    {
        var plan = new MarkupPlan { Entries = [new() { Page = 1, Quote = "brown fox", Type = type }] };
        var pdf = new FakePdfService(pageCount: 1, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps." });

        var (file, _) = MarkupPlanService.Resolve(plan, pdf, text);

        var markup = Assert.IsAssignableFrom<TextMarkupAnnotation>(Assert.Single(file.Pages[0]));
        var rect = Assert.Single(markup.Rects);
        // Full glyph box from FakePdfTextService is Y=20..30 (H=10); the band must be a thin
        // sliver sitting at the bottom of the box, not the full ascender-to-descender height.
        Assert.True(rect.H < 10f, $"expected a thin baseline band, got H={rect.H}");
        Assert.Equal(40f, rect.Y + rect.H, precision: 3);
    }

    [Fact]
    public void Resolve_Applies_Default_Color_When_Omitted()
    {
        var plan = new MarkupPlan { Entries = [new() { Page = 1, Quote = "brown fox", Type = MarkupType.Strikeout }] };
        var pdf = new FakePdfService(pageCount: 1, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps." });

        var (file, _) = MarkupPlanService.Resolve(plan, pdf, text);

        Assert.Equal("#FF0000", file.Pages[0][0].Color);
    }

    [Fact]
    public void Resolve_Places_Note_At_Right_Margin_Of_Last_Matched_Rect()
    {
        var plan = new MarkupPlan { Entries = [new() { Page = 1, Quote = "brown fox", Type = MarkupType.Note, Comment = "Check this." }] };
        var pdf = new FakePdfService(pageCount: 1, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps." });

        var (file, _) = MarkupPlanService.Resolve(plan, pdf, text);

        var note = Assert.IsType<TextNoteAnnotation>(Assert.Single(file.Pages[0]));
        Assert.Equal(600f - 24f, note.X);
        Assert.Equal("Check this.", note.Text);
    }

    [Fact]
    public void Resolve_Merges_Into_Existing_AnnotationFile()
    {
        var existing = new AnnotationFile { SourcePdf = "test.pdf" };
        existing.Pages[0] = [new HighlightAnnotation { Contents = "human note" }];

        var plan = new MarkupPlan { Entries = [new() { Page = 1, Quote = "brown fox", Type = MarkupType.Underline }] };
        var pdf = new FakePdfService(pageCount: 1, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps." });

        var (file, _) = MarkupPlanService.Resolve(plan, pdf, text, existing);

        Assert.Equal(2, file.Pages[0].Count);
        Assert.Contains(file.Pages[0], a => a.Contents == "human note");
        Assert.Contains(file.Pages[0], a => a is UnderlineAnnotation);
    }
}
