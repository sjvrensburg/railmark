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
    [InlineData(MarkupType.Replace, "#0066FF")]
    public void Resolve_Applies_RailReader_Palette_Default_When_Color_Omitted(MarkupType type, string expected)
    {
        var plan = new MarkupPlan
        {
            Entries = [new() { Page = 1, Quote = "brown fox", Type = type, Replacement = type == MarkupType.Replace ? "red fox" : null }],
        };
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

    // --- Replace ---

    [Theory]
    [InlineData(null, "Replace with: red fox")]
    [InlineData("", "Replace with: red fox")]
    [InlineData("Colour is wrong.", "Replace with: red fox\n\nColour is wrong.")]
    public void Resolve_Writes_Replace_As_Tagged_Strikeout(string? comment, string expectedContents)
    {
        var (file, result) = ResolveOne(
            new() { Page = 1, Quote = "brown fox", Type = MarkupType.Replace, Replacement = "red fox", Comment = comment },
            "The quick brown fox jumps.");

        Assert.True(result.Entries[0].Success);
        Assert.Equal(MarkupType.Replace, result.Entries[0].Type);
        var strike = Assert.IsType<StrikeOutAnnotation>(Assert.Single(file.Pages[0]));
        Assert.Equal("Replacement", strike.Subject);
        Assert.Equal(expectedContents, strike.Contents);
        // Full glyph box, like a plain strikeout — not the underline's baseline band.
        Assert.Equal(10f, strike.Rects[0].H, precision: 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_Fails_Replace_Without_Replacement(string? replacement)
    {
        var (file, result) = ResolveOne(
            new() { Page = 1, Quote = "brown fox", Type = MarkupType.Replace, Replacement = replacement },
            "The quick brown fox jumps.");

        Assert.False(result.Entries[0].Success);
        Assert.Contains("non-empty replacement", result.Entries[0].Error);
        Assert.Empty(file.Pages);
    }

    [Fact]
    public void Resolve_Fails_Replacement_On_Another_Type()
    {
        var (_, result) = ResolveOne(
            new() { Page = 1, Quote = "brown fox", Type = MarkupType.Strikeout, Replacement = "red fox" },
            "The quick brown fox jumps.");

        Assert.False(result.Entries[0].Success);
        Assert.Contains("only valid with type replace", result.Entries[0].Error);
    }

    [Fact]
    public void MarkupPlan_Deserializes_Replace()
    {
        const string json = """{ "entries": [ { "page": 1, "quote": "teh", "type": "replace", "replacement": "the" } ] }""";

        var entry = JsonSerializer.Deserialize<MarkupPlan>(json, JsonOptions)!.Entries[0];

        Assert.Equal(MarkupType.Replace, entry.Type);
        Assert.Equal("the", entry.Replacement);
    }

    [Theory]
    [InlineData("Replace with: the", "the", null)]
    [InlineData("Replace with: the\n\nTypo.", "the", "Typo.")]
    [InlineData("Replace with: the\r\n\r\nTypo.", "the", "Typo.")]
    [InlineData("Replace with: two\nlines\n\nWhy.", "two\nlines", "Why.")]
    public void ReplacementMarkup_Parses_What_It_Formats(string contents, string replacement, string? comment)
    {
        var strike = new StrikeOutAnnotation { Subject = "Replacement", Contents = contents };

        Assert.True(ReplacementMarkup.TryParse(strike, out var parsedReplacement, out var parsedComment));
        Assert.Equal(replacement, parsedReplacement);
        Assert.Equal(comment, parsedComment);
    }

    [Fact]
    public void ReplacementMarkup_Ignores_Untagged_Or_Other_Annotations()
    {
        Assert.False(ReplacementMarkup.TryParse(new StrikeOutAnnotation { Contents = "Replace with: x" }, out _, out _));
        Assert.False(ReplacementMarkup.TryParse(new StrikeOutAnnotation { Subject = "Replacement", Contents = "x" }, out _, out _));
        Assert.False(ReplacementMarkup.TryParse(new HighlightAnnotation { Subject = "Replacement", Contents = "Replace with: x" }, out _, out _));
    }

    // --- Idempotent apply ---

    private static (AnnotationFile file, MarkupPlanApplyResult result) Apply(
        List<MarkupEntry> entries, AnnotationFile? existing = null, string? replaceAuthor = null)
        => MarkupPlanService.Resolve(
            new MarkupPlan { Entries = entries },
            new FakePdfService(pageCount: 1, pageSize: (600, 800)),
            new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps over the lazy dog." }),
            existing,
            replaceAuthor: replaceAuthor);

    private static List<MarkupEntry> MixedPlan() =>
    [
        new() { Page = 1, Quote = "brown fox", Type = MarkupType.Highlight, Comment = "c1" },
        new() { Page = 1, Quote = "lazy dog", Type = MarkupType.Note, Comment = "c2" },
        new() { Page = 1, Quote = "jumps", Type = MarkupType.Replace, Replacement = "leaps" },
        new() { Page = 1, Quote = "quick", Type = MarkupType.Underline },
    ];

    [Fact]
    public void Resolve_Reports_Added_For_New_Annotations()
    {
        var (file, result) = Apply(MixedPlan());

        Assert.All(result.Entries, e => Assert.Equal("added", e.Action));
        Assert.Equal(4, file.Pages[0].Count);
    }

    [Fact]
    public void Resolve_Twice_Does_Not_Duplicate()
    {
        var (first, _) = Apply(MixedPlan());
        var (second, result) = Apply(MixedPlan(), existing: first);

        Assert.All(result.Entries, e => Assert.Equal("unchanged", e.Action));
        Assert.All(result.Entries, e => Assert.True(e.Success));
        Assert.Equal(4, second.Pages[0].Count);
    }

    [Fact]
    public void Resolve_Collapses_A_Repeated_Entry_Within_One_Plan()
    {
        var entry = new MarkupEntry { Page = 1, Quote = "brown fox", Type = MarkupType.Highlight };
        var (file, result) = Apply([entry, entry]);

        Assert.Equal(["added", "unchanged"], result.Entries.Select(e => e.Action));
        Assert.Single(file.Pages[0]);
    }

    [Fact]
    public void Resolve_Updates_Comment_And_Colour_In_Place()
    {
        var (first, _) = Apply(MixedPlan());
        var existingId = first.Pages[0][0];

        var revised = MixedPlan();
        revised[0] = revised[0] with { Comment = "revised", Color = "#0066FF" };
        revised[1] = revised[1] with { Comment = "revised note" };
        var (second, result) = Apply(revised, existing: first);

        Assert.Equal(["updated", "updated", "unchanged", "unchanged"], result.Entries.Select(e => e.Action));
        Assert.Equal(4, second.Pages[0].Count);
        Assert.Same(existingId, second.Pages[0][0]);
        Assert.Equal("revised", second.Pages[0][0].Contents);
        Assert.Equal("#0066FF", second.Pages[0][0].Color);
        Assert.Equal("revised note", second.Pages[0].OfType<TextNoteAnnotation>().Single().EffectiveContents);
    }

    [Fact]
    public void Resolve_Treats_Different_Author_As_A_New_Annotation()
    {
        var (first, _) = Apply(MixedPlan());
        var (second, result) = Apply([MixedPlan()[0] with { Author = "Second Reviewer" }], existing: first);

        Assert.Equal("added", result.Entries[0].Action);
        Assert.Equal(5, second.Pages[0].Count);
    }

    [Fact]
    public void Resolve_Keeps_Replacement_And_Deletion_Of_The_Same_Words_Apart()
    {
        var (file, result) = Apply(
        [
            new() { Page = 1, Quote = "jumps", Type = MarkupType.Replace, Replacement = "leaps" },
            new() { Page = 1, Quote = "jumps", Type = MarkupType.Strikeout },
        ]);

        Assert.Equal(["added", "added"], result.Entries.Select(e => e.Action));
        Assert.Equal(2, file.Pages[0].Count);
    }

    [Fact]
    public void Resolve_Treats_Existing_Annotation_Within_Tolerance_As_The_Same()
    {
        // "brown fox" starts at 10 → fake rect (10, 30, 9, 10). A copy read back from a PDF is off
        // by a fraction of a point; one on another line is a full line away.
        var existing = new AnnotationFile();
        existing.Pages[0] =
        [
            new HighlightAnnotation { Author = "AI Reviewer", Color = "#FFFF00", Rects = [new(10.4f, 30.6f, 9.2f, 9.7f)] },
        ];

        var (file, result) = Apply([new() { Page = 1, Quote = "brown fox", Type = MarkupType.Highlight }], existing);
        Assert.Equal("unchanged", result.Entries[0].Action);
        Assert.Single(file.Pages[0]);

        existing.Pages[0][0] = new HighlightAnnotation { Author = "AI Reviewer", Color = "#FFFF00", Rects = [new(10f, 48f, 9f, 10f)] };
        (file, result) = Apply([new() { Page = 1, Quote = "brown fox", Type = MarkupType.Highlight }], existing);
        Assert.Equal("added", result.Entries[0].Action);
        Assert.Equal(2, file.Pages[0].Count);
    }

    [Fact]
    public void Resolve_Replace_Author_Removes_Only_That_Authors_Annotations()
    {
        var existing = new AnnotationFile();
        existing.Pages[0] =
        [
            new HighlightAnnotation { Author = "AI Reviewer", Contents = "old round" },
            new HighlightAnnotation { Author = "Human", Contents = "keep me" },
        ];
        existing.Pages[3] = [new TextNoteAnnotation { Author = "AI Reviewer", Text = "old note" }];

        var (file, result) = Apply(
            [new() { Page = 1, Quote = "brown fox", Type = MarkupType.Highlight, Comment = "new round" }],
            existing, replaceAuthor: "AI Reviewer");

        Assert.Equal(2, result.Removed);
        Assert.Equal(["keep me", "new round"], file.Pages[0].Select(a => a.Contents));
        // The emptied page keeps its key, so the writer still visits it and deletes the old note.
        Assert.True(file.Pages.ContainsKey(3));
        Assert.Empty(file.Pages[3]);
    }

    [Fact]
    public void Resolve_Replace_Author_Is_Case_Sensitive_And_Reports_Zero_When_Nothing_Matches()
    {
        var existing = new AnnotationFile();
        existing.Pages[0] = [new HighlightAnnotation { Author = "AI Reviewer" }];

        var (file, result) = Apply([], existing, replaceAuthor: "ai reviewer");

        Assert.Equal(0, result.Removed);
        Assert.Single(file.Pages[0]);
    }

    // --- Across a page break ---

    private const string Page1 = "Intro text. A second run of the experiment repeated the whole\nPage 2";
    private const string Page2 = "Journal header\nmeasurement with a fresh batch of samples, and it agreed.";

    private static (AnnotationFile file, MarkupPlanApplyResult result) ResolveTwoPages(
        MarkupEntry entry, AnnotationFile? existing = null, bool includeContext = false, string page2 = Page2)
        => MarkupPlanService.Resolve(
            new MarkupPlan { Entries = [entry] },
            new FakePdfService(pageCount: 2, pageSize: (600, 800)),
            new FakePdfTextService(new Dictionary<int, string> { [0] = Page1, [1] = page2 }),
            existing,
            includeContext: includeContext);

    [Fact]
    public void FindAcrossPageBreak_Splits_Between_Words_Past_Footer_And_Header()
    {
        var split = TextLocator.FindAcrossPageBreak(
            NormalizedText.From(Page1), NormalizedText.From(Page2), "repeated the whole measurement with a fresh batch");

        Assert.NotNull(split);
        Assert.Equal("repeated the whole", Page1.Substring(split!.Value.Head.CharStart, split.Value.Head.CharLength));
        Assert.Equal("measurement with a fresh batch", Page2.Substring(split.Value.Tail.CharStart, split.Value.Tail.CharLength));
    }

    [Theory]
    [InlineData("the model is inter-")]
    [InlineData("the model is inter-\n")]
    public void FindAcrossPageBreak_Splits_A_Word_Hyphenated_Over_The_Break(string page1)
    {
        var split = TextLocator.FindAcrossPageBreak(
            NormalizedText.From(page1), NormalizedText.From("pretable in practice."), "model is interpretable in practice");

        Assert.NotNull(split);
        Assert.StartsWith("model is inter", page1.Substring(split!.Value.Head.CharStart, split.Value.Head.CharLength));
        Assert.Equal(0, split.Value.Tail.CharStart);
    }

    [Fact]
    public void FindAcrossPageBreak_Rejects_Halves_Far_From_The_Break()
    {
        var farFromEnd = "repeated the whole " + new string('x', 400);

        Assert.Null(TextLocator.FindAcrossPageBreak(
            NormalizedText.From(farFromEnd), NormalizedText.From(Page2), "repeated the whole measurement"));
        Assert.Null(TextLocator.FindAcrossPageBreak(
            NormalizedText.From(Page1), NormalizedText.From(new string('y', 400) + " measurement with"), "the whole measurement with"));
    }

    [Fact]
    public void FindAcrossPageBreak_Returns_Null_When_The_Tail_Is_Missing()
        => Assert.Null(TextLocator.FindAcrossPageBreak(
            NormalizedText.From(Page1), NormalizedText.From(Page2), "repeated the whole thing entirely"));

    [Fact]
    public void Resolve_Splits_A_Quote_Across_The_Page_Break()
    {
        var (file, result) = ResolveTwoPages(
            new() { Page = 1, Quote = "the whole measurement with", Type = MarkupType.Highlight, Comment = "why?" },
            includeContext: true);

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.Success, entry.Error);
        Assert.Equal(1, entry.Page);
        Assert.Equal(2, entry.EndPage);
        Assert.Equal(2, entry.SpanCount);
        Assert.Equal("added", entry.Action);
        Assert.Contains("[[the whole]]", entry.Context);
        Assert.Contains("page 2: ", entry.Context);
        Assert.Contains("[[measurement with]]", entry.Context);

        var head = Assert.IsType<HighlightAnnotation>(Assert.Single(file.Pages[0]));
        var tail = Assert.IsType<HighlightAnnotation>(Assert.Single(file.Pages[1]));
        Assert.Equal("why?", head.Contents);
        Assert.Null(head.Subject);
        Assert.Null(tail.Contents);
        Assert.Equal("Continuation", tail.Subject);
        Assert.Equal(head.Author, tail.Author);
        Assert.Equal(head.Color, tail.Color);
    }

    [Fact]
    public void Resolve_Splits_A_Replacement_With_The_Replacement_On_The_First_Half()
    {
        var (file, _) = ResolveTwoPages(
            new() { Page = 1, Quote = "the whole measurement", Type = MarkupType.Replace, Replacement = "every measurement" });

        var head = Assert.IsType<StrikeOutAnnotation>(Assert.Single(file.Pages[0]));
        var tail = Assert.IsType<StrikeOutAnnotation>(Assert.Single(file.Pages[1]));
        Assert.True(ReplacementMarkup.TryParse(head, out var replacement, out _));
        Assert.Equal("every measurement", replacement);
        Assert.False(ReplacementMarkup.TryParse(tail, out _, out _));
        Assert.True(ContinuationMarkup.Is(tail));
        Assert.Equal("#0066FF", tail.Color);
    }

    [Fact]
    public void Resolve_Places_A_Spanning_Note_Once_On_The_First_Page()
    {
        var (file, result) = ResolveTwoPages(
            new() { Page = 1, Quote = "the whole measurement", Type = MarkupType.Note, Comment = "c" });

        Assert.Equal(2, result.Entries[0].EndPage);
        Assert.IsType<TextNoteAnnotation>(Assert.Single(file.Pages[0]));
        Assert.False(file.Pages.ContainsKey(1));
    }

    [Fact]
    public void Resolve_Reapplying_A_Spanning_Quote_Is_Unchanged()
    {
        var entry = new MarkupEntry { Page = 1, Quote = "the whole measurement with", Type = MarkupType.Underline };
        var (first, _) = ResolveTwoPages(entry);
        var (second, result) = ResolveTwoPages(entry, existing: first);

        Assert.Equal("unchanged", result.Entries[0].Action);
        Assert.Single(second.Pages[0]);
        Assert.Single(second.Pages[1]);
    }

    [Fact]
    public void Resolve_Checks_Context_On_Both_Sides_Of_The_Break()
    {
        var ok = ResolveTwoPages(new() { Page = 1, Quote = "the whole measurement", Type = MarkupType.Highlight, ContextBefore = "repeated", ContextAfter = "with a" });
        var badAfter = ResolveTwoPages(new() { Page = 1, Quote = "the whole measurement", Type = MarkupType.Highlight, ContextAfter = "nope" });

        Assert.True(ok.result.Entries[0].Success);
        Assert.False(badAfter.result.Entries[0].Success);
    }

    [Fact]
    public void Resolve_Does_Not_Look_Past_The_Last_Page_Or_For_A_Later_Occurrence()
    {
        var onLast = MarkupPlanService.Resolve(
            new MarkupPlan { Entries = [new() { Page = 2, Quote = "samples, and it agreed. More", Type = MarkupType.Highlight }] },
            new FakePdfService(pageCount: 2, pageSize: (600, 800)),
            new FakePdfTextService(new Dictionary<int, string> { [0] = Page1, [1] = Page2 }));
        var laterOccurrence = ResolveTwoPages(new() { Page = 1, Quote = "the whole measurement", Type = MarkupType.Highlight, Occurrence = 2 });

        Assert.False(onLast.result.Entries[0].Success);
        Assert.False(laterOccurrence.result.Entries[0].Success);
        Assert.Contains("not found", laterOccurrence.result.Entries[0].Error);
    }

    [Fact]
    public void Resolve_Prefers_A_Whole_Match_On_The_Page_Over_A_Split()
    {
        // The quote is wholly on page 1 as well as splittable over the break: the page wins.
        var page1 = "the whole measurement is here. " + Page1;
        var (file, result) = MarkupPlanService.Resolve(
            new MarkupPlan { Entries = [new() { Page = 1, Quote = "the whole measurement", Type = MarkupType.Highlight }] },
            new FakePdfService(pageCount: 2, pageSize: (600, 800)),
            new FakePdfTextService(new Dictionary<int, string> { [0] = page1, [1] = Page2 }));

        Assert.Null(result.Entries[0].EndPage);
        Assert.False(file.Pages.ContainsKey(1));
    }

    // --- Replies ---

    private static AnnotationFile WithParent(string id = "parent-1", int page = 0)
    {
        var existing = new AnnotationFile();
        existing.Pages[page] =
        [
            new HighlightAnnotation { NativeId = id, Author = "Human", Contents = "Is this right?", Rects = [new(50, 300, 100, 12)] },
        ];
        return existing;
    }

    private static (AnnotationFile file, MarkupPlanApplyResult result) Reply(
        AnnotationFile existing, params MarkupEntry[] entries)
        => MarkupPlanService.Resolve(
            new MarkupPlan { Entries = [.. entries] },
            new FakePdfService(pageCount: 2, pageSize: (600, 800)),
            new FakePdfTextService(new Dictionary<int, string> { [0] = "The quick brown fox jumps.", [1] = "Second page." }),
            existing);

    [Fact]
    public void Reply_Without_Quote_Is_A_Note_Beside_Its_Parent()
    {
        var (file, result) = Reply(WithParent(),
            new MarkupEntry { Page = 1, InReplyTo = "parent-1", Type = MarkupType.Note, Comment = "Yes — see Table 2." });

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.Success, entry.Error);
        Assert.Null(entry.Quote);
        Assert.Equal("parent-1", entry.InReplyTo);
        Assert.Equal("added", entry.Action);

        var note = Assert.IsType<TextNoteAnnotation>(file.Pages[0][1]);
        Assert.Equal("Yes — see Table 2.", note.Text);
        Assert.Equal("parent-1", note.InReplyTo);
        Assert.StartsWith("parent-1#reply-", note.NativeId);
        Assert.Equal(600f - 24f, note.X);
        // Parent's last line is centred at 306; the first reply sits one step below it.
        Assert.Equal(326f, note.Y, precision: 3);
    }

    [Fact]
    public void Successive_Replies_Stack_And_Are_All_Kept()
    {
        var (file, result) = Reply(WithParent(),
            new MarkupEntry { Page = 1, InReplyTo = "parent-1", Type = MarkupType.Note, Comment = "First." },
            new MarkupEntry { Page = 1, InReplyTo = "parent-1", Type = MarkupType.Note, Comment = "Second." });

        Assert.Equal(["added", "added"], result.Entries.Select(e => e.Action));
        var notes = file.Pages[0].OfType<TextNoteAnnotation>().ToList();
        Assert.Equal([326f, 346f], notes.Select(n => n.Y));
    }

    [Fact]
    public void Reapplying_A_Reply_Finds_It_Through_Its_Id_Alone()
    {
        // As read back from a PDF: no /IRT (PDFium can't write it), just the /NM link.
        var existing = WithParent();
        existing.Pages[0].Add(new TextNoteAnnotation
        {
            NativeId = "parent-1#reply-abc", Contents = "Yes.", Author = "AI Reviewer", Color = "#FFFF00", X = 576, Y = 999,
        });

        var (file, result) = Reply(existing,
            new MarkupEntry { Page = 1, InReplyTo = "parent-1", Type = MarkupType.Note, Comment = "Yes." });

        Assert.Equal("unchanged", result.Entries[0].Action);
        Assert.Equal(2, file.Pages[0].Count);
    }

    [Fact]
    public void Reply_With_A_Quote_Marks_The_Text_And_Links_The_Parent()
    {
        var (file, result) = Reply(WithParent(),
            new MarkupEntry { Page = 1, InReplyTo = "parent-1", Quote = "brown fox", Type = MarkupType.Highlight, Comment = "This part." });

        Assert.True(result.Entries[0].Success);
        var highlight = file.Pages[0].OfType<HighlightAnnotation>().Last();
        Assert.Equal("parent-1", highlight.InReplyTo);
        Assert.StartsWith("parent-1#reply-", highlight.NativeId);
    }

    [Fact]
    public void Reply_Fails_When_The_Parent_Is_Missing_Or_On_Another_Page()
    {
        var (_, missing) = Reply(WithParent(), new MarkupEntry { Page = 1, InReplyTo = "nope", Type = MarkupType.Note, Comment = "x" });
        var (_, elsewhere) = Reply(WithParent(page: 1), new MarkupEntry { Page = 1, InReplyTo = "parent-1", Type = MarkupType.Note, Comment = "x" });

        Assert.Contains("No annotation with id nope on page 1", missing.Entries[0].Error);
        Assert.Contains("is on page 2, not page 1", elsewhere.Entries[0].Error);
    }

    [Fact]
    public void Reply_To_A_Parent_Removed_By_Replace_Author_Says_So()
    {
        var existing = WithParent();
        existing.Pages[0][0].Author = "AI Reviewer";

        var (_, result) = MarkupPlanService.Resolve(
            new MarkupPlan { Entries = [new() { Page = 1, InReplyTo = "parent-1", Type = MarkupType.Note, Comment = "x" }] },
            new FakePdfService(1, (600, 800)), new FakePdfTextService([]), existing, replaceAuthor: "AI Reviewer");

        Assert.Contains("--replace-author", result.Entries[0].Error);
    }

    [Theory]
    [InlineData(null, MarkupType.Highlight, "quote is required")]
    [InlineData("parent-1", MarkupType.Highlight, "must be a note")]
    public void Entry_Without_Quote_Must_Be_A_Reply_Note(string? inReplyTo, MarkupType type, string error)
    {
        var (_, result) = Reply(WithParent(), new MarkupEntry { Page = 1, InReplyTo = inReplyTo, Type = type });

        Assert.False(result.Entries[0].Success);
        Assert.Contains(error, result.Entries[0].Error);
    }

    [Fact]
    public void Reply_Without_Quote_Rejects_Occurrence_And_Context()
    {
        var (_, result) = Reply(WithParent(), new MarkupEntry { Page = 1, InReplyTo = "parent-1", Type = MarkupType.Note, Occurrence = 2 });

        Assert.Contains("need a quote", result.Entries[0].Error);
    }

    [Fact]
    public void MarkupPlan_Deserializes_A_Reply_Without_Quote()
    {
        const string json = """{ "entries": [ { "page": 2, "type": "note", "inReplyTo": "abc", "comment": "Agreed." } ] }""";

        var entry = JsonSerializer.Deserialize<MarkupPlan>(json, JsonOptions)!.Entries[0];

        Assert.Null(entry.Quote);
        Assert.Equal("abc", entry.InReplyTo);
    }

    [Theory]
    [InlineData("irt", null, "irt")]
    [InlineData("irt", "p#reply-x", "irt")]
    [InlineData(null, "p#reply-x", "p")]
    [InlineData(null, "p#reply-x#reply-y", "p#reply-x")]
    [InlineData(null, "plain-id", null)]
    [InlineData(null, null, null)]
    public void ReplyMarkup_Prefers_IRT_Then_Reads_The_Id(string? inReplyTo, string? nativeId, string? expected)
        => Assert.Equal(expected, ReplyMarkup.ParentOf(new TextNoteAnnotation { InReplyTo = inReplyTo, NativeId = nativeId }));

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
