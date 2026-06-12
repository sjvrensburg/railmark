using RailReader.Core.Models;
using Rr2Annotate.Services;

namespace Rr2Annotate.Tests;

public class MarkdownBuilderTests
{
    // --- Helpers ---

    private static AnnotationFile MakeFile(
        List<(int pageIdx, List<Annotation> annotations)>? pages = null)
    {
        var file = new AnnotationFile { SourcePdf = "test.pdf" };
        foreach (var (idx, anns) in pages ?? [])
            file.Pages[idx] = anns;
        return file;
    }

    private static OutlineEntry H(string title, int? page, List<OutlineEntry>? children = null)
        => new() { Title = title, Page = page, Children = children ?? [] };

    private static MarkdownBuilder Build(
        AnnotationFile file,
        List<OutlineEntry>? outline = null,
        Dictionary<(int, int), string>? highlightTexts = null,
        Dictionary<int, string>? pageTexts = null,
        Dictionary<(int, int), string>? images = null,
        string? imageRelDir = null)
        => new(file, file.SourcePdf, outline ?? [], highlightTexts, pageTexts, images, imageRelDir);

    private static int CountOccurrences(string source, string value)
    {
        int count = 0, idx = 0;
        while ((idx = source.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }
        return count;
    }

    // --- Highlights ---

    [Fact]
    public void Highlight_Bolds_Text_In_Page_Context()
    {
        var file = MakeFile([(0, [new HighlightAnnotation { Color = "#FF0", Rects = [new(50, 50, 100, 10)] }])]);
        var pageTexts = new Dictionary<int, string> { [0] = "The quick brown fox jumps over the lazy dog." };
        var hlTexts = new Dictionary<(int, int), string> { [(0, 0)] = "brown fox jumps" };

        var md = Build(file, [H("Chapter 1", 0)], hlTexts, pageTexts).Build();

        Assert.Contains("**brown fox jumps**", md);
        Assert.Contains("The quick", md);
        Assert.Contains("lazy dog.", md);
    }

    [Fact]
    public void Highlight_Fuzzy_Match_Handles_Whitespace_Differences()
    {
        var file = MakeFile([(0, [new HighlightAnnotation { Color = "#FF0", Rects = [new(50, 50, 100, 10)] }])]);
        var pageTexts = new Dictionary<int, string> { [0] = "word one word two word three" };
        var hlTexts = new Dictionary<(int, int), string> { [(0, 0)] = "word  two" }; // double space

        var md = Build(file, [H("Section A", 0)], hlTexts, pageTexts).Build();

        Assert.Contains("**word two**", md);
        Assert.DoesNotContain("Highlighted:", md);
    }

    [Fact]
    public void Highlight_Without_Page_Text_Shows_Bold_Highlight()
    {
        var file = MakeFile([(0, [new HighlightAnnotation { Color = "#FF0", Rects = [new(50, 50, 100, 10)] }])]);
        var hlTexts = new Dictionary<(int, int), string> { [(0, 0)] = "some highlighted text" };

        var md = Build(file, [H("Chapter 1", 0)], hlTexts).Build();

        Assert.Contains("**some highlighted text**", md);
    }

    [Fact]
    public void Highlight_With_Reviewer_Comment_Shows_Comment()
    {
        var file = MakeFile([(0, [new HighlightAnnotation
        {
            Color = "#FF0", Contents = "Rephrase this",
            Rects = [new(50, 50, 100, 10)]
        }])]);
        var hlTexts = new Dictionary<(int, int), string> { [(0, 0)] = "phrase" };

        var md = Build(file, [H("Chapter 1", 0)], hlTexts).Build();

        Assert.Contains("**Comment:** Rephrase this", md);
    }

    // --- Notes ---

    [Fact]
    public void TextNote_Shows_Note_Text()
    {
        var note = new TextNoteAnnotation { Color = "#FFCC00", X = 500, Y = 300, Text = "Clarify this" };
        var file = MakeFile([(5, [note])]);

        var md = Build(file, [H("Methods", 5)]).Build();

        Assert.Contains("## Methods", md);
        Assert.Contains("**Note:** Clarify this", md);
        Assert.Contains("*(p. 6, note)*", md);
    }

    [Fact]
    public void TextNote_Prefers_Contents_Over_Text()
    {
        var note = new TextNoteAnnotation
        {
            Color = "#FF0", X = 0, Y = 0,
            Text = "Legacy text field",
            Contents = "Contents from PDF /Contents"
        };
        var file = MakeFile([(0, [note])]);

        var md = Build(file).Build();

        Assert.Contains("Contents from PDF /Contents", md);
        Assert.DoesNotContain("Legacy text field", md);
    }

    // --- Rect ---

    [Fact]
    public void Rect_Without_Images_Shows_Label()
    {
        var rect = new RectAnnotation { Color = "#00F", X = 50, Y = 50, W = 400, H = 200 };
        var file = MakeFile([(10, [rect])]);

        var md = Build(file, [H("Results", 10)]).Build();

        Assert.Contains("*(p. 11, rectangle)*", md);
        Assert.DoesNotContain("![", md);
    }

    [Fact]
    public void Rect_With_Image_Includes_Embed()
    {
        var rect = new RectAnnotation { Color = "#00F", X = 50, Y = 50, W = 400, H = 200 };
        var file = MakeFile([(0, [rect])]);
        var images = new Dictionary<(int, int), string> { [(0, 0)] = "/abs/path/imgs/annotation_001.png" };

        var md = Build(file, [H("Ch1", 0)], images: images, imageRelDir: "imgs").Build();

        Assert.Contains("![Rectangle annotation, p. 1](imgs/annotation_001.png)", md);
    }

    // --- Freehand ---

    [Fact]
    public void Freehand_Without_Images_Shows_Placeholder()
    {
        var fh = new FreehandAnnotation { Color = "#F00", Points = [new(100, 200), new(110, 210)] };
        var file = MakeFile([(20, [fh])]);

        var md = Build(file, [H("Discussion", 20)]).Build();

        Assert.Contains("--images", md);
        Assert.Contains("*(p. 21, freehand)*", md);
    }

    [Fact]
    public void Freehand_Merged_Group_Emits_Label_For_Each_Stroke()
    {
        var fh1 = new FreehandAnnotation { Color = "#F00", Points = [new(100, 200), new(110, 210)] };
        var fh2 = new FreehandAnnotation { Color = "#F00", Points = [new(101, 201), new(111, 211)] };
        var file = MakeFile([(5, [fh1, fh2])]);
        // Share the same image path to simulate merged group (both keys point to same file)
        var images = new Dictionary<(int, int), string>
        {
            [(5, 0)] = "/abs/imgs/annotation_001.png",
            [(5, 1)] = "/abs/imgs/annotation_001.png",
        };

        var md = Build(file, [H("Ch", 5)], images: images, imageRelDir: "imgs").Build();

        // Both strokes should have a label in the output
        Assert.Equal(2, CountOccurrences(md, "*(p. 6, freehand)*"));
        // Image embed should appear exactly once (second stroke is suppressed)
        Assert.Equal(1, CountOccurrences(md, "![Freehand annotation"));
    }

    // --- Caret / FreeText ---

    [Fact]
    public void Caret_Shows_Label()
    {
        var caret = new CaretAnnotation { Color = "#FF0", X = 100, Y = 200, W = 10, H = 20 };
        var file = MakeFile([(0, [caret])]);

        var md = Build(file, [H("Chapter 1", 0)]).Build();

        Assert.Contains("*(p. 1, caret)*", md);
    }

    [Fact]
    public void Caret_With_Contents_Shows_Comment()
    {
        var caret = new CaretAnnotation { Color = "#FF0", X = 100, Y = 200, W = 10, H = 20, Contents = "insert citation" };
        var file = MakeFile([(0, [caret])]);

        var md = Build(file, [H("Chapter 1", 0)]).Build();

        Assert.Contains("**Comment:** insert citation", md);
    }

    [Fact]
    public void FreeText_Shows_Contents()
    {
        var ft = new FreeTextAnnotation { Color = "#FF0", X = 100, Y = 200, W = 200, H = 50, Contents = "A margin note" };
        var file = MakeFile([(0, [ft])]);

        var md = Build(file, [H("Chapter 1", 0)]).Build();

        Assert.Contains("**Free text:** A margin note", md);
        Assert.Contains("*(p. 1, free text)*", md);
    }

    [Fact]
    public void FreeText_Without_Contents_Shows_Label_Only()
    {
        var ft = new FreeTextAnnotation { Color = "#FF0", X = 100, Y = 200, W = 200, H = 50 };
        var file = MakeFile([(0, [ft])]);

        var md = Build(file, [H("Chapter 1", 0)]).Build();

        Assert.DoesNotContain("Free text:", md);
        Assert.Contains("*(p. 1, free text)*", md);
    }

    // --- Heading grouping ---

    [Fact]
    public void Annotations_Grouped_By_Heading_In_Outline_Order()
    {
        var file = MakeFile([
            (10, [new TextNoteAnnotation { Color = "#FF0", X = 100, Y = 100, Text = "Result note" }]),
            (0,  [new TextNoteAnnotation { Color = "#FF0", X = 100, Y = 100, Text = "Intro note" }]),
        ]);
        var outline = new List<OutlineEntry> {
            H("Introduction", 0), H("Methods", 5), H("Results", 10)
        };

        var md = Build(file, outline).Build();

        var introIdx = md.IndexOf("## Introduction");
        var resultsIdx = md.IndexOf("## Results");
        Assert.True(introIdx >= 0 && resultsIdx >= 0 && introIdx < resultsIdx);

        Assert.True(md.IndexOf("Intro note") < md.IndexOf("Result note"));
    }

    [Fact]
    public void Nested_Headings_Use_Correct_Levels()
    {
        var outline = new List<OutlineEntry> {
            H("Chapter 1", 0, [
                H("Section 1.1", 1, [
                    H("Subsection 1.1.1", 2)
                ])
            ])
        };
        var file = MakeFile([
            (0, [new TextNoteAnnotation { Color = "#FF0", X = 100, Y = 100, Text = "ch1 note" }]),
            (1, [new TextNoteAnnotation { Color = "#FF0", X = 100, Y = 100, Text = "sec note" }]),
            (2, [new TextNoteAnnotation { Color = "#FF0", X = 100, Y = 100, Text = "subsec note" }]),
        ]);

        var md = Build(file, outline).Build();

        Assert.Contains("## Chapter 1", md);
        Assert.Contains("### Section 1.1", md);
        Assert.Contains("#### Subsection 1.1.1", md);
    }

    [Fact]
    public void Annotations_Without_Heading_Go_To_Other_Section()
    {
        var file = MakeFile([(0, [new TextNoteAnnotation { Color = "#FF0", X = 10, Y = 10, Text = "orphan note" }])]);

        var md = Build(file, outline: []).Build();

        Assert.Contains("## Other Annotations", md);
        Assert.Contains("orphan note", md);
    }

    [Fact]
    public void Duplicate_Outline_Keys_Do_Not_Double_Emit_Section()
    {
        // Two outline entries with the same title+page produce one section in output.
        var outline = new List<OutlineEntry> { H("Appendix", 10), H("Appendix", 10) };
        var file = MakeFile([(10, [new TextNoteAnnotation { Color = "#FF0", X = 0, Y = 0, Text = "note" }])]);

        var md = Build(file, outline).Build();

        Assert.Equal(1, CountOccurrences(md, "## Appendix"));
        Assert.Equal(1, CountOccurrences(md, "**Note:** note"));
    }

    // --- Summary ---

    [Fact]
    public void Summary_Table_Is_Emitted()
    {
        var file = MakeFile([(0, [
            new HighlightAnnotation { Color = "#FF0", Rects = [new(50, 50, 100, 10)] },
            new TextNoteAnnotation { Color = "#FFCC00", X = 100, Y = 200, Text = "a note" },
        ])]);
        var hlTexts = new Dictionary<(int, int), string> { [(0, 0)] = "test" };

        var md = Build(file, [H("Chapter 1", 0)], hlTexts).Build();

        Assert.Contains("## Summary", md);
        Assert.Contains("**2 annotations**", md);
        Assert.Contains("| Chapter 1 | 1 | 1 | 0 | 0 | 0 |", md);
    }

    [Fact]
    public void Summary_Other_Column_Counts_Caret_And_FreeText()
    {
        var file = MakeFile([(0, [
            new HighlightAnnotation { Color = "#FF0", Rects = [new(50, 50, 100, 10)] },
            new CaretAnnotation { Color = "#FF0", X = 100, Y = 200, W = 10, H = 20 },
            new FreeTextAnnotation { Color = "#FF0", X = 100, Y = 200, W = 200, H = 50, Contents = "note" },
        ])]);

        var md = Build(file, [H("Chapter 1", 0)]).Build();

        Assert.Contains("| Chapter 1 | 1 | 0 | 0 | 0 | 2 |", md);
    }

    // --- CleanText ---

    [Fact]
    public void CleanText_Removes_Soft_Hyphens_And_Control_Chars()
    {
        var input = "hyper­parameter optimisation";
        var cleaned = MarkdownBuilder.CleanText(input);
        Assert.Equal("hyperparameter optimisation", cleaned);
    }

    [Fact]
    public void CleanText_Collapses_Whitespace()
    {
        var input = "word one  word   two\r\nword\tthree";
        var cleaned = MarkdownBuilder.CleanText(input);
        Assert.Equal("word one word two word three", cleaned);
    }

    // --- Label ---

    [Fact]
    public void Label_Shows_Page_Number_One_Based()
    {
        var file = MakeFile([(4, [new TextNoteAnnotation { Color = "#FF0", X = 0, Y = 0, Text = "note" }])]);

        var md = Build(file, [H("Ch", 4)]).Build();

        Assert.Contains("*(p. 5, note)*", md);
    }
}
