using RailReader.Core.Models;
using RailMark.Services;

namespace RailMark.Tests;

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
        string? imageRelDir = null,
        Dictionary<string, float>? headingPositions = null)
        => new(file, file.SourcePdf, outline ?? [], highlightTexts, pageTexts, images, imageRelDir, headingPositions);

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

        Assert.Contains("*(p. 1, suggested insertion)*", md);
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

    // --- Text markup subtypes ---

    [Fact]
    public void StrikeOut_Is_Emitted_As_Suggested_Deletion()
    {
        var file = MakeFile([(0, [new StrikeOutAnnotation { Color = "#F00", Rects = [new(50, 50, 100, 10)] }])]);
        var texts = new Dictionary<(int, int), string> { [(0, 0)] = "redundant clause" };

        var md = Build(file, [H("Ch1", 0)], texts).Build();

        Assert.Contains("> ~~redundant clause~~", md);
        Assert.Contains("*(p. 1, suggested deletion)*", md);
    }

    [Fact]
    public void Underline_And_Squiggly_Are_Emitted()
    {
        var file = MakeFile([(0, [
            new UnderlineAnnotation { Color = "#00F", Rects = [new(50, 50, 100, 10)] },
            new SquigglyAnnotation { Color = "#F80", Rects = [new(50, 70, 100, 10)] },
        ])]);
        var texts = new Dictionary<(int, int), string>
        {
            [(0, 0)] = "underlined phrase",
            [(0, 1)] = "questionable phrase",
        };

        var md = Build(file, [H("Ch1", 0)], texts).Build();

        Assert.Contains("underlined phrase", md);
        Assert.Contains("*(p. 1, underline)*", md);
        Assert.Contains("questionable phrase", md);
        Assert.Contains("*(p. 1, squiggly)*", md);
    }

    [Fact]
    public void Summary_Counts_All_Text_Markup_Subtypes()
    {
        var file = MakeFile([(0, [
            new HighlightAnnotation { Color = "#FF0", Rects = [new(50, 50, 100, 10)] },
            new StrikeOutAnnotation { Color = "#F00", Rects = [new(50, 60, 100, 10)] },
            new UnderlineAnnotation { Color = "#00F", Rects = [new(50, 70, 100, 10)] },
        ])]);

        var md = Build(file, [H("Ch1", 0)]).Build();

        Assert.Contains("| Text markup |", md);
        Assert.Contains("| Ch1 | 3 | 0 | 0 | 0 | 0 |", md);
    }

    // --- Caret + StrikeOut replacement grouping ---

    [Fact]
    public void Caret_With_StrikeOut_Child_Renders_As_Single_Replacement()
    {
        var caret = new CaretAnnotation
        {
            Color = "#FF0", X = 100, Y = 200, W = 10, H = 20,
            NativeId = "caret-1", Contents = "concise wording",
        };
        var strikeOut = new StrikeOutAnnotation
        {
            Color = "#F00", Rects = [new(50, 200, 100, 10)], InReplyTo = "caret-1",
        };
        var file = MakeFile([(0, [caret, strikeOut])]);
        var texts = new Dictionary<(int, int), string> { [(0, 1)] = "unnecessarily verbose wording" };

        var md = Build(file, [H("Ch1", 0)], texts).Build();

        Assert.Contains("> ~~unnecessarily verbose wording~~ → **concise wording**", md);
        Assert.Contains("*(p. 1, suggested replacement)*", md);
        // The strikeout is folded into the caret, not also emitted on its own.
        Assert.DoesNotContain("suggested deletion", md);
        Assert.Equal(1, CountOccurrences(md, "unnecessarily verbose wording"));
    }

    [Fact]
    public void StrikeOut_Replying_To_Unknown_Id_Stays_Standalone()
    {
        var strikeOut = new StrikeOutAnnotation
        {
            Color = "#F00", Rects = [new(50, 200, 100, 10)], InReplyTo = "no-such-annotation",
        };
        var file = MakeFile([(0, [strikeOut])]);
        var texts = new Dictionary<(int, int), string> { [(0, 0)] = "cut this" };

        var md = Build(file, [H("Ch1", 0)], texts).Build();

        Assert.Contains("> ~~cut this~~", md);
        Assert.Contains("*(p. 1, suggested deletion)*", md);
    }

    // --- Duplicated /Contents suppression ---

    [Fact]
    public void Highlight_Contents_Echoing_Marked_Text_Is_Suppressed()
    {
        // Skim pre-fills /Contents with a copy of the selected text.
        var file = MakeFile([(0, [new HighlightAnnotation
        {
            Color = "#FF0", Contents = "the selected sentence",
            Rects = [new(50, 50, 100, 10)],
        }])]);
        var texts = new Dictionary<(int, int), string> { [(0, 0)] = "the selected sentence" };

        var md = Build(file, [H("Ch1", 0)], texts).Build();

        Assert.DoesNotContain("**Comment:**", md);
        Assert.Equal(1, CountOccurrences(md, "the selected sentence"));
    }

    [Fact]
    public void Highlight_Contents_Differing_From_Marked_Text_Is_Kept()
    {
        var file = MakeFile([(0, [new HighlightAnnotation
        {
            Color = "#FF0", Contents = "needs a citation",
            Rects = [new(50, 50, 100, 10)],
        }])]);
        var texts = new Dictionary<(int, int), string> { [(0, 0)] = "the selected sentence" };

        var md = Build(file, [H("Ch1", 0)], texts).Build();

        Assert.Contains("**Comment:** needs a citation", md);
    }

    // --- Heading positions ---

    [Fact]
    public void Two_Headings_On_One_Page_Split_Annotations_By_Position()
    {
        var outline = new List<OutlineEntry> { H("Methods", 3), H("Results", 3) };
        var file = MakeFile([(3, [
            new TextNoteAnnotation { Color = "#FF0", X = 10, Y = 150, Text = "methods note" },
            new TextNoteAnnotation { Color = "#FF0", X = 10, Y = 550, Text = "results note" },
        ])]);
        var positions = new Dictionary<string, float>
        {
            [MarkdownBuilder.HeadingKey("Methods", 3)] = 100f,
            [MarkdownBuilder.HeadingKey("Results", 3)] = 500f,
        };

        var md = Build(file, outline, headingPositions: positions).Build();

        var methodsIdx = md.IndexOf("## Methods", StringComparison.Ordinal);
        var resultsIdx = md.IndexOf("## Results", StringComparison.Ordinal);
        var methodsNoteIdx = md.IndexOf("methods note", StringComparison.Ordinal);
        var resultsNoteIdx = md.IndexOf("results note", StringComparison.Ordinal);

        // Each note falls under the heading above it, not both under the first.
        Assert.True(methodsIdx < methodsNoteIdx && methodsNoteIdx < resultsIdx);
        Assert.True(resultsIdx < resultsNoteIdx);
    }

    [Fact]
    public void Unknown_Heading_Positions_Fall_Back_To_Page_Ordering()
    {
        var outline = new List<OutlineEntry> { H("Introduction", 0), H("Results", 10) };
        var file = MakeFile([(10, [new TextNoteAnnotation { Color = "#FF0", X = 10, Y = 400, Text = "note" }])]);

        var md = Build(file, outline).Build();

        var resultsIdx = md.IndexOf("## Results", StringComparison.Ordinal);
        Assert.True(resultsIdx >= 0 && resultsIdx < md.IndexOf("**Note:** note", StringComparison.Ordinal));
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

    [Fact]
    public void CleanText_Rejoins_Words_Split_By_A_Wrap_Hyphen()
    {
        Assert.Equal("compileable source", MarkdownBuilder.CleanText("com-\npileable source"));
        Assert.Equal("compileable source", MarkdownBuilder.CleanText("com-\r\n   pileable source"));
    }

    [Fact]
    public void CleanText_Keeps_Real_Hyphens_At_A_Line_End()
    {
        // Uppercase or digit before the hyphen means it is part of the word, not a wrap artefact.
        Assert.Equal("COVID-19 cases", MarkdownBuilder.CleanText("COVID-\n19 cases"));
        Assert.Equal("H-1 visas", MarkdownBuilder.CleanText("H-\n1 visas"));
    }

    [Fact]
    public void CleanText_Keeps_Hyphens_Not_At_A_Line_End()
    {
        Assert.Equal("well-known result", MarkdownBuilder.CleanText("well-known result"));
        Assert.Equal("well- known result", MarkdownBuilder.CleanText("well- known result"));
    }

    [Fact]
    public void CleanText_Expands_Ligatures_And_Normalises_Quotes()
    {
        Assert.Equal("the first difficulty", MarkdownBuilder.CleanText("the ﬁrst diﬃculty"));
        Assert.Equal("\"quoted\" and 'quoted'", MarkdownBuilder.CleanText("“quoted” and ‘quoted’"));
        Assert.Equal("and so on...", MarkdownBuilder.CleanText("and so on…"));
    }

    // --- Quote resolution ---

    [Fact]
    public void ResolveQuote_Finds_A_Quote_Written_Without_Ligatures()
    {
        var pageText = "We report the ﬁrst results of the study.";

        var range = TextLocator.ResolveQuote(pageText, "the first results");

        Assert.NotNull(range);
        var matched = pageText.Substring(range!.Value.CharStart, range.Value.CharLength);
        Assert.Equal("the ﬁrst results", matched);
    }

    [Fact]
    public void ResolveQuote_Finds_A_Quote_Spanning_A_Wrap_Hyphen()
    {
        var pageText = "The model is inter-\npretable in practice.";

        var range = TextLocator.ResolveQuote(pageText, "is interpretable in practice");

        Assert.NotNull(range);
        // The matched span covers both sides of the break so the geometry covers both lines.
        var matched = pageText.Substring(range!.Value.CharStart, range.Value.CharLength);
        Assert.Equal("is inter-\npretable in practice", matched);
    }

    [Fact]
    public void ResolveQuote_Finds_A_Quote_Written_With_Straight_Quotes()
    {
        var pageText = "He called it “the best option” at the time.";

        var range = TextLocator.ResolveQuote(pageText, "\"the best option\"");

        Assert.NotNull(range);
        var matched = pageText.Substring(range!.Value.CharStart, range.Value.CharLength);
        Assert.Equal("“the best option”", matched);
    }

    [Fact]
    public void ResolveQuote_Returns_Null_When_Absent()
    {
        Assert.Null(TextLocator.ResolveQuote("some page text", "not on this page"));
    }

    // --- Rebuilding multi-line markup text ---

    [Fact]
    public void SpanCoveringParts_Rejoins_A_Word_Split_Across_Lines()
    {
        // PdfTextService de-hyphenates as it extracts, leaving U+0002 where the hyphen was, so
        // the two line rects yield "inter" and "pretable in practice".
        var pageText = "The model is interpretable in practice, as shown.";

        var span = TextLocator.SpanCoveringParts(pageText, ["inter", "pretable in practice"]);

        Assert.Equal("interpretable in practice", span);
        // CleanText drops the marker, so the word comes back whole rather than "inter pretable".
        Assert.Equal("interpretable in practice", MarkdownBuilder.CleanText(span!));
    }

    [Fact]
    public void SpanCoveringParts_Keeps_A_Space_For_An_Ordinary_Line_Wrap()
    {
        var pageText = "the quick brown fox\njumps over the lazy dog";

        var span = TextLocator.SpanCoveringParts(pageText, ["brown fox", "jumps over"]);

        Assert.Equal("brown fox\njumps over", span);
        Assert.Equal("brown fox jumps over", MarkdownBuilder.CleanText(span!));
    }

    [Fact]
    public void SpanCoveringParts_Matches_Parts_In_Order()
    {
        // "the" appears three times; the span must run from the first part's match onwards
        // rather than latching onto an earlier occurrence.
        var pageText = "the alpha the beta the gamma";

        var span = TextLocator.SpanCoveringParts(pageText, ["beta", "the gamma"]);

        Assert.Equal("beta the gamma", span);
    }

    [Fact]
    public void SpanCoveringParts_Returns_Null_When_A_Part_Is_Not_Found()
    {
        Assert.Null(TextLocator.SpanCoveringParts("the quick brown fox", ["quick", "elephant"]));
    }

    [Fact]
    public void SpanCoveringParts_Handles_A_Single_Part()
    {
        Assert.Equal("brown fox", TextLocator.SpanCoveringParts("the quick brown fox", ["brown fox"]));
    }

    [Fact]
    public void SpanCoveringParts_Returns_Null_For_No_Usable_Parts()
    {
        Assert.Null(TextLocator.SpanCoveringParts("the quick brown fox", []));
        Assert.Null(TextLocator.SpanCoveringParts("the quick brown fox", ["   "]));
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
