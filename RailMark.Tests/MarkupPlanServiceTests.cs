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

    // --- TextLocator.FindQuoteMatches / HasContext ---

    [Fact]
    public void FindQuoteMatches_Returns_Every_Occurrence_In_Reading_Order()
    {
        const string page = "The model fits. We tune the model. Finally the MODEL is saved.";
        var matches = TextLocator.FindQuoteMatches(NormalizedText.From(page), "the model");

        Assert.Equal(3, matches.Count);
        Assert.Equal([0, 24, 43], matches.Select(m => m.CharStart));
        Assert.All(matches, m => Assert.Equal("the model", page.Substring(m.CharStart, m.CharLength), ignoreCase: true));
    }

    [Fact]
    public void FindQuoteMatches_Counts_Normalised_Occurrences_Alongside_Verbatim_Ones()
    {
        // The first occurrence carries a ligature, so a verbatim search would only see the
        // second — and would report an ambiguous quote as unique.
        const string page = "the \uFB01rst step, then the first step again";
        var matches = TextLocator.FindQuoteMatches(NormalizedText.From(page), "the first step");

        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].CharStart);
        Assert.Equal("the \uFB01rst step", page.Substring(matches[0].CharStart, matches[0].CharLength));
    }

    [Fact]
    public void FindQuoteMatches_Range_Excludes_Markers_Dropped_After_The_Match()
    {
        // "inter" + U+0002 + "pretable": a quote of "inter" must not swallow the marker.
        const string page = "is inter\u0002pretable here";
        var match = Assert.Single(TextLocator.FindQuoteMatches(NormalizedText.From(page), "is inter"));

        Assert.Equal("is inter", page.Substring(match.CharStart, match.CharLength));
    }

    [Fact]
    public void FindQuoteMatches_Spans_A_Wrap_Hyphen()
    {
        const string page = "The model is inter-\npretable in practice.";
        var match = Assert.Single(TextLocator.FindQuoteMatches(NormalizedText.From(page), "interpretable in practice"));

        Assert.Equal("inter-\npretable in practice", page.Substring(match.CharStart, match.CharLength));
    }

    [Theory]
    [InlineData("tune", null, true)]
    [InlineData("tune ", null, true)]
    [InlineData("TUNE", null, true)]
    [InlineData("fits.", null, false)]
    [InlineData(null, ". Finally", true)]
    [InlineData(null, "is saved", false)]
    [InlineData("We tune", ". Finally", true)]
    [InlineData("We tune", "is saved", false)]
    public void HasContext_Checks_Text_Adjacent_To_The_Match(string? before, string? after, bool expected)
    {
        var page = NormalizedText.From("The model fits. We tune the model. Finally the model is saved.");
        var second = TextLocator.FindQuoteMatches(page, "the model")[1];

        Assert.Equal(expected, TextLocator.HasContext(page, second, before, after));
    }

    [Fact]
    public void DescribeContext_Brackets_The_Match_In_Normalised_Text()
    {
        var page = NormalizedText.From("The quick   brown\nfox jumps over the lazy dog.");
        var match = TextLocator.FindQuoteMatches(page, "brown fox")[0];

        Assert.Equal("…ick [[brown fox]] jum…", TextLocator.DescribeContext(page, match, radius: 4));
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

    // Literal hex, not AnnotationInteractionHandler.AnnotationColors: if Core's palette changes,
    // this should fail so the skill's schema doc gets updated along with it.
    [Theory]
    [InlineData(MarkupType.Highlight, "#FFFF00")]
    [InlineData(MarkupType.Underline, "#00A000")]
    [InlineData(MarkupType.Strikeout, "#FF0000")]
    [InlineData(MarkupType.Squiggly, "#00A000")]
    [InlineData(MarkupType.Note, "#FFFF00")]
    public void Resolve_Applies_RailReader_Palette_Default_When_Color_Omitted(MarkupType type, string expected)
    {
        var plan = new MarkupPlan { Entries = [new() { Page = 1, Quote = "brown fox", Type = type }] };
        var pdf = new FakePdfService(pageCount: 1, pageSize: (600, 800));
        var text = new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps." });

        var (file, _) = MarkupPlanService.Resolve(plan, pdf, text);

        Assert.Equal(expected, file.Pages[0][0].Color);
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

    // --- Occurrence / context disambiguation ---

    private const string RepeatedText = "The model fits. We tune the model. Finally the model is saved.";

    // FakePdfTextService places each range's rect at Y = 20 + CharStart, so the rect's Y says
    // which occurrence was marked.
    private static int MarkedCharStart(AnnotationFile file)
        => (int)(((TextMarkupAnnotation)file.Pages[0].Single()).Rects[0].Y - 20f);

    private static (AnnotationFile file, MarkupPlanApplyResult result) ResolveOne(MarkupEntry entry, string pageText = RepeatedText, bool includeContext = false)
        => MarkupPlanService.Resolve(
            new MarkupPlan { Entries = [entry] },
            new FakePdfService(pageCount: 1, pageSize: (600, 800)),
            new FakePdfTextService(new Dictionary<int, string> { [0] = pageText }),
            includeContext: includeContext);

    [Fact]
    public void Resolve_Warns_When_Quote_Is_Ambiguous_And_Marks_The_First()
    {
        var (file, result) = ResolveOne(new() { Page = 1, Quote = "the model", Type = MarkupType.Highlight });

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.Success);
        Assert.Equal(3, entry.MatchCount);
        Assert.Contains("occurs 3 times", entry.Warning);
        Assert.Equal(0, MarkedCharStart(file));
    }

    [Fact]
    public void Resolve_Does_Not_Warn_For_A_Unique_Quote()
    {
        var (_, result) = ResolveOne(new() { Page = 1, Quote = "tune the model", Type = MarkupType.Highlight });

        Assert.Equal(1, result.Entries[0].MatchCount);
        Assert.Null(result.Entries[0].Warning);
    }

    [Fact]
    public void Resolve_Marks_The_Requested_Occurrence_Without_Warning()
    {
        var (file, result) = ResolveOne(new() { Page = 1, Quote = "the model", Type = MarkupType.Highlight, Occurrence = 3 });

        Assert.True(result.Entries[0].Success);
        Assert.Null(result.Entries[0].Warning);
        Assert.Equal(RepeatedText.LastIndexOf("the model", StringComparison.Ordinal), MarkedCharStart(file));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Resolve_Fails_For_An_Occurrence_Out_Of_Range(int occurrence)
    {
        var (file, result) = ResolveOne(new() { Page = 1, Quote = "the model", Type = MarkupType.Highlight, Occurrence = occurrence });

        Assert.False(result.Entries[0].Success);
        Assert.Contains("occurrence", result.Entries[0].Error);
        Assert.Empty(file.Pages);
    }

    [Fact]
    public void Resolve_Uses_Context_To_Pick_The_Match()
    {
        var (file, result) = ResolveOne(new() { Page = 1, Quote = "the model", Type = MarkupType.Highlight, ContextBefore = "We tune" });

        Assert.True(result.Entries[0].Success);
        Assert.Null(result.Entries[0].Warning);
        Assert.Equal(3, result.Entries[0].MatchCount);
        Assert.Equal(RepeatedText.IndexOf("the model.", StringComparison.Ordinal), MarkedCharStart(file));
    }

    [Fact]
    public void Resolve_Applies_Occurrence_Among_Context_Filtered_Matches()
    {
        // "the model" followed by " is"/" fits" — contextAfter narrows to two, occurrence picks one.
        const string text = "the model fits. the model is. the model fits again.";
        var (file, result) = ResolveOne(
            new() { Page = 1, Quote = "the model", Type = MarkupType.Highlight, ContextAfter = "fits", Occurrence = 2 }, text);

        Assert.True(result.Entries[0].Success);
        Assert.Equal(text.LastIndexOf("the model", StringComparison.Ordinal), MarkedCharStart(file));
    }

    [Fact]
    public void Resolve_Still_Warns_When_Context_Leaves_Several_Matches()
    {
        const string text = "the model fits. the model is. the model fits again.";
        var (_, result) = ResolveOne(new() { Page = 1, Quote = "the model", Type = MarkupType.Highlight, ContextAfter = "fits" }, text);

        Assert.Contains("occurs 2 times", result.Entries[0].Warning);
        Assert.Contains("with the given context", result.Entries[0].Warning);
    }

    [Fact]
    public void Resolve_Fails_When_No_Match_Has_The_Given_Context()
    {
        var (_, result) = ResolveOne(new() { Page = 1, Quote = "the model", Type = MarkupType.Highlight, ContextBefore = "nowhere" });

        Assert.False(result.Entries[0].Success);
        Assert.Equal(3, result.Entries[0].MatchCount);
        Assert.Contains("never with the given", result.Entries[0].Error);
    }

    [Fact]
    public void Resolve_Reports_Context_Only_When_Asked()
    {
        var entry = new MarkupEntry { Page = 1, Quote = "tune the model", Type = MarkupType.Highlight };

        Assert.Null(ResolveOne(entry).result.Entries[0].Context);
        Assert.Equal("The model fits. We [[tune the model]]. Finally the model is saved.",
            ResolveOne(entry, includeContext: true).result.Entries[0].Context);
    }

    [Fact]
    public void MarkupEntryResult_Serializes_Optional_Fields_Only_When_Set()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var plain = JsonSerializer.Serialize(new MarkupEntryResult(1, "q", MarkupType.Note, true, null), options);

        Assert.Contains("\"error\":null", plain);
        Assert.Contains("\"matchCount\":0", plain);
        Assert.DoesNotContain("warning", plain);
        Assert.DoesNotContain("context", plain);
    }

    [Fact]
    public void MarkupPlan_Deserializes_Disambiguation_Fields()
    {
        const string json = """
            { "entries": [ { "page": 1, "quote": "the model", "type": "highlight",
                             "occurrence": 2, "contextBefore": "We tune", "contextAfter": "." } ] }
            """;

        var entry = JsonSerializer.Deserialize<MarkupPlan>(json, JsonOptions)!.Entries[0];

        Assert.Equal(2, entry.Occurrence);
        Assert.Equal("We tune", entry.ContextBefore);
        Assert.Equal(".", entry.ContextAfter);
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
