using System.Text.Json;
using RailMark.Services;
using RailReader.Core.Models;

namespace RailMark.Tests;

public class AnnotationJsonBuilderTests
{
    private static OutlineEntry H(string title, int? page) => new() { Title = title, Page = page, Children = [] };

    private static AnnotationFile MakeFile(params (int page, Annotation annotation)[] items)
    {
        var file = new AnnotationFile { SourcePdf = "test.pdf" };
        foreach (var (page, annotation) in items)
        {
            if (!file.Pages.TryGetValue(page, out var list)) file.Pages[page] = list = [];
            list.Add(annotation);
        }
        return file;
    }

    [Fact]
    public void Records_Carry_Plan_Shaped_Fields()
    {
        var file = MakeFile((2, new HighlightAnnotation
        {
            Rects = [new(10, 100, 50, 10)], Contents = "Check this.", Color = "#FFFF00", Author = "Ann",
            NativeId = "nm-1", State = ReviewState.Accepted,
            CreatedUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        }));
        var texts = new Dictionary<(int, int), string> { [(2, 0)] = "brown  fox" };

        var record = Assert.Single(new AnnotationJsonBuilder(file, "doc.pdf", [], texts).BuildExport().Annotations);

        Assert.Equal(3, record.Page);
        Assert.Equal("highlight", record.Type);
        Assert.Equal("brown fox", record.Quote);
        Assert.Equal("Check this.", record.Comment);
        Assert.Equal("#FFFF00", record.Color);
        Assert.Equal("Ann", record.Author);
        Assert.Equal("nm-1", record.Id);
        Assert.Equal("accepted", record.State);
        Assert.Equal(2026, record.Created!.Value.Year);
    }

    [Fact]
    public void Records_Report_Opacity_Rounded_To_Two_Places()
    {
        // /CA is stored as an 8-bit alpha, so 0.35 reads back as 0.349.
        var file = MakeFile((0, new HighlightAnnotation { Rects = [new(10, 100, 50, 10)], Opacity = 0.349f }));

        var record = Assert.Single(new AnnotationJsonBuilder(file, "doc.pdf", [], new()).BuildExport().Annotations);

        Assert.Equal(0.35f, record.Opacity);
    }

    [Theory]
    [InlineData(typeof(HighlightAnnotation), "highlight")]
    [InlineData(typeof(UnderlineAnnotation), "underline")]
    [InlineData(typeof(StrikeOutAnnotation), "strikeout")]
    [InlineData(typeof(SquigglyAnnotation), "squiggly")]
    [InlineData(typeof(TextNoteAnnotation), "note")]
    [InlineData(typeof(RectAnnotation), "rect")]
    [InlineData(typeof(FreehandAnnotation), "freehand")]
    [InlineData(typeof(CaretAnnotation), "caret")]
    [InlineData(typeof(FreeTextAnnotation), "freetext")]
    public void TypeName_Matches_Plan_Vocabulary(Type type, string expected)
        => Assert.Equal(expected, AnnotationJsonBuilder.TypeName((Annotation)Activator.CreateInstance(type)!));

    [Fact]
    public void Tagged_Strikeout_Becomes_A_Replace_Record()
    {
        var file = MakeFile((0, new StrikeOutAnnotation
        {
            Rects = [new(10, 100, 50, 10)], Subject = "Replacement", Contents = "Replace with: red fox\n\nWrong colour.",
        }));
        var texts = new Dictionary<(int, int), string> { [(0, 0)] = "brown fox" };

        var record = Assert.Single(new AnnotationJsonBuilder(file, "doc.pdf", [], texts).BuildExport().Annotations);

        Assert.Equal("replace", record.Type);
        Assert.Equal("red fox", record.Replacement);
        Assert.Equal("Wrong colour.", record.Comment);
        Assert.Null(record.Subject);
    }

    [Fact]
    public void Note_Body_Comes_From_Text_Or_Contents()
    {
        var file = MakeFile(
            (0, new TextNoteAnnotation { X = 500, Y = 50, Text = "authored body" }),
            (0, new TextNoteAnnotation { X = 500, Y = 90, Contents = "read-back body" }));

        var records = new AnnotationJsonBuilder(file, "doc.pdf", []).BuildExport().Annotations;

        Assert.Equal(["authored body", "read-back body"], records.Select(r => r.Comment));
        Assert.All(records, r => Assert.Null(r.Quote));
    }

    [Fact]
    public void Records_Are_In_Reading_Order_And_Filed_Under_Headings()
    {
        var file = MakeFile(
            (1, new TextNoteAnnotation { Y = 50, Text = "p2" }),
            (0, new TextNoteAnnotation { Y = 400, Text = "p1 low" }),
            (0, new TextNoteAnnotation { Y = 100, Text = "p1 high" }));
        var positions = new Dictionary<string, float> { [HeadingIndex.Key("Intro", 0)] = 80, [HeadingIndex.Key("Methods", 0)] = 300 };

        var records = new AnnotationJsonBuilder(file, "doc.pdf", [H("Intro", 0), H("Methods", 0)], headingPositions: positions)
            .BuildExport().Annotations;

        Assert.Equal(["p1 high", "p1 low", "p2"], records.Select(r => r.Comment));
        Assert.Equal(["Intro", "Methods", "Methods"], records.Select(r => r.Heading));
    }

    [Fact]
    public void Occurrence_Is_Set_Only_When_The_Quote_Repeats_On_The_Page()
    {
        const string page = "Run the test. Then run the test again. Unique words here.";
        var file = MakeFile(
            (0, new HighlightAnnotation { Rects = [new(10, 100, 50, 10)] }),
            (0, new HighlightAnnotation { Rects = [new(10, 200, 50, 10)] }));
        var texts = new Dictionary<(int, int), string> { [(0, 0)] = "the test", [(0, 1)] = "Unique words" };
        var offsets = new Dictionary<(int, int), int>
        {
            [(0, 0)] = page.LastIndexOf("the test", StringComparison.Ordinal),
            [(0, 1)] = page.IndexOf("Unique", StringComparison.Ordinal),
        };

        var records = new AnnotationJsonBuilder(file, "doc.pdf", [], texts,
            pageTexts: new Dictionary<int, string> { [0] = page }, markupOffsets: offsets).BuildExport().Annotations;

        Assert.Equal(2, records[0].Occurrence);
        Assert.Null(records[1].Occurrence);
    }

    [Theory]
    [InlineData("the whole", "measurement with", "the whole measurement with")]
    [InlineData("is inter-", "pretable here", "is interpretable here")]
    public void Continuation_Folds_Into_Its_Head_As_One_Record(string headText, string tailText, string expected)
    {
        var file = MakeFile(
            (0, new HighlightAnnotation { Author = "AI", Rects = [new(10, 100, 50, 10)], Contents = "why?" }),
            (0, new HighlightAnnotation { Author = "AI", Rects = [new(10, 700, 50, 10)], Contents = "head" }),
            (1, new HighlightAnnotation { Author = "AI", Rects = [new(10, 60, 50, 10)], Subject = "Continuation" }));
        var texts = new Dictionary<(int, int), string> { [(0, 0)] = "unrelated", [(0, 1)] = headText, [(1, 0)] = tailText };

        var records = new AnnotationJsonBuilder(file, "doc.pdf", [], texts).BuildExport().Annotations;

        Assert.Equal(2, records.Count);
        var spanning = records[1];
        Assert.Equal(1, spanning.Page);
        Assert.Equal(2, spanning.EndPage);
        Assert.Equal(expected, spanning.Quote);
        Assert.Equal("head", spanning.Comment);
        Assert.Null(records[0].EndPage);
    }

    [Fact]
    public void Continuation_Pairs_By_Its_Id_Link_Over_Position()
    {
        // A lower highlight on the page (a footnote, the other column) must not steal the tail.
        var file = MakeFile(
            (0, new HighlightAnnotation { NativeId = "head", Author = "AI", Rects = [new(300, 600, 50, 10)] }),
            (0, new HighlightAnnotation { NativeId = "footnote", Author = "AI", Rects = [new(10, 750, 50, 10)] }),
            (1, new HighlightAnnotation { NativeId = "head#cont-1", Author = "AI", Rects = [new(10, 60, 50, 10)], Subject = "Continuation" }));

        Assert.Equal((0, 0), Assert.Single(ContinuationMarkup.Pair(file)).Key);
    }

    [Fact]
    public void Continuation_Pairs_Only_With_Same_Kind_And_Author()
    {
        var file = MakeFile(
            (0, new UnderlineAnnotation { Author = "AI", Rects = [new(10, 700, 50, 10)] }),
            (0, new HighlightAnnotation { Author = "Human", Rects = [new(10, 710, 50, 10)] }),
            (0, new HighlightAnnotation { Author = "AI", Rects = [new(10, 500, 50, 10)] }),
            (1, new HighlightAnnotation { Author = "AI", Rects = [new(10, 60, 50, 10)], Subject = "Continuation" }));

        var pairs = ContinuationMarkup.Pair(file);

        Assert.Equal((1, 0), Assert.Single(pairs, p => p.Key == (0, 2)).Value);
        Assert.Single(pairs);
    }

    [Fact]
    public void Reply_Link_Is_Read_From_The_Id_When_There_Is_No_IRT()
    {
        var file = MakeFile(
            (0, new HighlightAnnotation { NativeId = "p1", Rects = [new(10, 100, 50, 10)] }),
            (0, new TextNoteAnnotation { NativeId = "p1#reply-abc", Y = 120, Contents = "Answer." }));

        var records = new AnnotationJsonBuilder(file, "doc.pdf", []).BuildExport().Annotations;

        Assert.Null(records[0].InReplyTo);
        Assert.Equal("p1", records[1].InReplyTo);
        Assert.Equal("p1#reply-abc", records[1].Id);
    }

    [Fact]
    public void Image_Paths_Use_Forward_Slashes()
    {
        var file = MakeFile((0, new RectAnnotation { X = 1, Y = 1, W = 10, H = 10 }));
        var images = new Dictionary<(int, int), string> { [(0, 0)] = Path.Combine("abs", "dir", "annotation_001.png") };

        var record = Assert.Single(new AnnotationJsonBuilder(file, "doc.pdf", [], images: images, imageRelDir: @"out\imgs")
            .BuildExport().Annotations);

        Assert.Equal("out/imgs/annotation_001.png", record.Image);
    }

    [Fact]
    public void Json_Is_CamelCase_Omits_Nulls_And_Keeps_Prose_Readable()
    {
        var file = MakeFile((0, new HighlightAnnotation { Rects = [new(10, 100, 50, 10)], Contents = "“smart” — café" }));

        var json = new AnnotationJsonBuilder(file, "doc.pdf", []).Build();

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("doc.pdf", doc.RootElement.GetProperty("source").GetString());
        var record = doc.RootElement.GetProperty("annotations")[0];
        Assert.Equal(1, record.GetProperty("page").GetInt32());
        Assert.False(record.TryGetProperty("replacement", out _));
        Assert.False(record.TryGetProperty("id", out _));
        Assert.Contains("“smart” — café", json);
    }

    [Fact]
    public void Empty_File_Produces_An_Empty_Array()
    {
        var json = new AnnotationJsonBuilder(new AnnotationFile(), "doc.pdf", []).Build();

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("annotations").GetArrayLength());
    }
}
