using System.Text.Json;
using Rr2Annotate.Models;

namespace Rr2Annotate.Tests;

public class AnnotationConverterTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Deserializes_Highlight()
    {
        var json = """
        {
            "type": "highlight",
            "color": "#FFB6C1",
            "opacity": 0.35,
            "rects": [{"x": 100, "y": 200, "w": 50, "h": 10}],
            "text": "some highlighted text",
            "overlapping_blocks": [],
            "nearest_heading": {"title": "Abstract", "source": "outline", "page": 1}
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var highlight = Assert.IsType<HighlightAnnotation>(annotation);
        Assert.Equal("highlight", highlight.Type);
        Assert.Equal("#FFB6C1", highlight.Color);
        Assert.Equal("some highlighted text", highlight.Text);
        Assert.Single(highlight.Rects);
        Assert.Equal(100, highlight.Rects[0].X);
        Assert.Equal("Abstract", highlight.NearestHeading!.Title);
    }

    [Fact]
    public void Deserializes_TextNote()
    {
        var json = """
        {
            "type": "text_note",
            "color": "#FFCC00",
            "opacity": 0.9,
            "x": 558.0,
            "y": 390.0,
            "note_text": "Rephrase this",
            "overlapping_blocks": [],
            "nearest_heading": {"title": "Introduction", "source": "outline", "page": 5}
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var note = Assert.IsType<TextNoteAnnotation>(annotation);
        Assert.Equal("Rephrase this", note.NoteText);
        Assert.Equal(558.0, note.X);
        Assert.Equal(390.0, note.Y);
    }

    [Fact]
    public void Deserializes_Rect()
    {
        var json = """
        {
            "type": "rect",
            "color": "#0066FF",
            "opacity": 0.5,
            "stroke_width": 2,
            "x": 56.0,
            "y": 477.0,
            "w": 506.0,
            "h": 253.0,
            "filled": false,
            "text": "content inside rect",
            "overlapping_blocks": [
                {
                    "class": "text",
                    "class_id": 22,
                    "b_box": {"x": 57, "y": 426, "w": 494, "h": 87},
                    "confidence": 0.94,
                    "reading_order": 0,
                    "text": "block text here"
                }
            ],
            "nearest_heading": null
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var rect = Assert.IsType<RectAnnotation>(annotation);
        Assert.Equal(506.0, rect.W);
        Assert.False(rect.Filled);
        Assert.Equal("content inside rect", rect.Text);
        Assert.Single(rect.OverlappingBlocks);
        Assert.Equal("text", rect.OverlappingBlocks[0].Class);
        Assert.Null(rect.NearestHeading);
    }

    [Fact]
    public void Deserializes_Freehand()
    {
        var json = """
        {
            "type": "freehand",
            "color": "#FF0000",
            "opacity": 0.8,
            "stroke_width": 2,
            "points": [{"x": 100, "y": 200}, {"x": 105, "y": 210}],
            "overlapping_blocks": [],
            "nearest_heading": {"title": "Methods", "source": "outline", "page": 10}
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var freehand = Assert.IsType<FreehandAnnotation>(annotation);
        Assert.Equal(2, freehand.Points.Count);
        Assert.Equal(200, freehand.SortY); // min Y
    }

    [Fact]
    public void Deserializes_Underline_As_HighlightAnnotation()
    {
        var json = """
        {
            "type": "underline",
            "color": "#0000FF",
            "opacity": 0.5,
            "rects": [{"x": 50, "y": 100, "w": 200, "h": 8}],
            "text": "underlined text",
            "overlapping_blocks": [],
            "nearest_heading": null
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var highlight = Assert.IsType<HighlightAnnotation>(annotation);
        Assert.Equal("underline", highlight.Type);
        Assert.Equal("underlined text", highlight.Text);
        Assert.Single(highlight.Rects);
        Assert.Equal(50, highlight.Rects[0].X);
    }

    [Fact]
    public void Deserializes_Strikeout_As_HighlightAnnotation()
    {
        var json = """
        {
            "type": "strikeout",
            "color": "#FF0000",
            "opacity": 0.5,
            "rects": [{"x": 50, "y": 100, "w": 200, "h": 8}],
            "text": "struck text",
            "overlapping_blocks": [],
            "nearest_heading": null
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var highlight = Assert.IsType<HighlightAnnotation>(annotation);
        Assert.Equal("strikeout", highlight.Type);
        Assert.Equal("struck text", highlight.Text);
    }

    [Fact]
    public void Deserializes_Squiggly_As_HighlightAnnotation()
    {
        var json = """
        {
            "type": "squiggly",
            "color": "#00FF00",
            "opacity": 0.5,
            "rects": [{"x": 50, "y": 100, "w": 200, "h": 8}],
            "text": "squiggly text",
            "overlapping_blocks": [],
            "nearest_heading": null
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var highlight = Assert.IsType<HighlightAnnotation>(annotation);
        Assert.Equal("squiggly", highlight.Type);
        Assert.Equal("squiggly text", highlight.Text);
    }

    [Fact]
    public void Deserializes_Caret()
    {
        var json = """
        {
            "type": "caret",
            "color": "#FF0000",
            "opacity": 0.8,
            "x": 100.0,
            "y": 200.0,
            "w": 10.0,
            "h": 20.0,
            "overlapping_blocks": [],
            "nearest_heading": null
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var caret = Assert.IsType<CaretAnnotation>(annotation);
        Assert.Equal("caret", caret.Type);
        Assert.Equal(100.0, caret.X);
        Assert.Equal(200.0, caret.Y);
        Assert.Equal(10.0, caret.W);
        Assert.Equal(20.0, caret.H);
        Assert.Equal(200.0, caret.SortY);
    }

    [Fact]
    public void Deserializes_FreeText()
    {
        var json = """
        {
            "type": "free_text",
            "color": "#000000",
            "opacity": 1.0,
            "x": 50.0,
            "y": 300.0,
            "w": 200.0,
            "h": 50.0,
            "contents": "A margin note",
            "overlapping_blocks": [],
            "nearest_heading": null
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var freeText = Assert.IsType<FreeTextAnnotation>(annotation);
        Assert.Equal("free_text", freeText.Type);
        Assert.Equal(50.0, freeText.X);
        Assert.Equal("A margin note", freeText.Contents);
        Assert.Equal(300.0, freeText.SortY);
    }

    [Fact]
    public void Deserializes_Unknown_As_UnknownAnnotation()
    {
        var json = """
        {
            "type": "unknown",
            "color": "#000000",
            "opacity": 0.5,
            "overlapping_blocks": [],
            "nearest_heading": null
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var unknown = Assert.IsType<UnknownAnnotation>(annotation);
        Assert.Equal("unknown", unknown.Type);
        Assert.Equal("#000000", unknown.Color);
    }

    [Fact]
    public void Deserializes_New_Metadata_Fields_Without_Error()
    {
        var json = """
        {
            "type": "highlight",
            "color": "#FFB6C1",
            "opacity": 0.35,
            "rects": [{"x": 100, "y": 200, "w": 50, "h": 10}],
            "text": "some text",
            "overlapping_blocks": [],
            "nearest_heading": null,
            "author": "Reviewer",
            "contents": "Check this",
            "subject": "Review",
            "native_id": "annot-42",
            "created_utc": "2026-06-01T12:00:00Z",
            "modified_utc": "2026-06-02T08:30:00Z",
            "state": "Accepted",
            "source": "pdf",
            "in_reply_to": "annot-41"
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var highlight = Assert.IsType<HighlightAnnotation>(annotation);
        Assert.Equal("some text", highlight.Text);
    }

    [Fact]
    public void Deserializes_Outline_With_Null_Page()
    {
        var json = """
        {
            "source": "test.pdf",
            "exported_at": "2026-01-01T00:00:00Z",
            "page_count": 5,
            "outline": [
                {"title": "TOC Entry", "children": []}
            ],
            "pages": [],
            "bookmarks": []
        }
        """;

        var export = JsonSerializer.Deserialize<AnnotationExport>(json, Options)!;

        Assert.Null(export.Outline[0].Page);
    }

    [Fact]
    public void Deserializes_NearestHeading_With_Null_Page()
    {
        var json = """
        {
            "type": "highlight",
            "color": "#FF0",
            "opacity": 0.5,
            "rects": [],
            "overlapping_blocks": [],
            "nearest_heading": {"title": "Appendix", "source": "outline"}
        }
        """;

        var annotation = JsonSerializer.Deserialize<Annotation>(json, Options);

        var highlight = Assert.IsType<HighlightAnnotation>(annotation);
        Assert.NotNull(highlight.NearestHeading);
        Assert.Equal("Appendix", highlight.NearestHeading.Title);
        Assert.Null(highlight.NearestHeading.Page);
    }

    [Fact]
    public void Deserializes_Full_Export()
    {
        var json = """
        {
            "source": "test.pdf",
            "exported_at": "2026-01-01T00:00:00Z",
            "page_count": 10,
            "outline": [
                {"title": "Chapter 1", "page": 0, "children": [
                    {"title": "Section 1.1", "page": 1, "children": []}
                ]}
            ],
            "pages": [
                {
                    "page": 0,
                    "width": 595.0,
                    "height": 842.0,
                    "annotations": [
                        {
                            "type": "highlight",
                            "color": "#FF0",
                            "opacity": 0.5,
                            "rects": [],
                            "text": "test",
                            "overlapping_blocks": [],
                            "nearest_heading": {"title": "Chapter 1", "source": "outline", "page": 0}
                        }
                    ]
                }
            ],
            "bookmarks": []
        }
        """;

        var export = JsonSerializer.Deserialize<AnnotationExport>(json, Options)!;

        Assert.Equal("test.pdf", export.Source);
        Assert.Equal(10, export.PageCount);
        Assert.Single(export.Outline);
        Assert.Equal("Chapter 1", export.Outline[0].Title);
        Assert.Single(export.Outline[0].Children);
        Assert.Single(export.Pages);
        Assert.IsType<HighlightAnnotation>(export.Pages[0].Annotations[0]);
    }

    [Fact]
    public void Deserializes_Contents_On_All_Types()
    {
        // Contents is the PDF /Contents field — reviewer comments
        var highlightJson = """
        {
            "type": "highlight",
            "color": "#FF0",
            "opacity": 0.5,
            "rects": [],
            "overlapping_blocks": [],
            "nearest_heading": null,
            "contents": "rephrase this"
        }
        """;

        var noteJson = """
        {
            "type": "text_note",
            "color": "#FFD400",
            "opacity": 0.9,
            "x": 100, "y": 200,
            "note_text": "",
            "overlapping_blocks": [],
            "nearest_heading": null,
            "contents": "Needs more context"
        }
        """;

        var highlight = JsonSerializer.Deserialize<Annotation>(highlightJson, Options);
        Assert.Equal("rephrase this", highlight!.Contents);

        var note = JsonSerializer.Deserialize<Annotation>(noteJson, Options);
        Assert.Equal("Needs more context", note!.Contents);
    }
}
