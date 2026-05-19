using System.Text.Json.Serialization;

namespace Rr2Annotate.Models;

public record AnnotationExport(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("exported_at")] string? ExportedAt,
    [property: JsonPropertyName("page_count")] int PageCount,
    [property: JsonPropertyName("outline")] List<OutlineEntry> Outline,
    [property: JsonPropertyName("pages")] List<AnnotatedPage> Pages,
    [property: JsonPropertyName("bookmarks")] List<Bookmark>? Bookmarks = null
);

public record OutlineEntry(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("children")] List<OutlineEntry> Children
);

public record Bookmark(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("page")] int Page
);

public record AnnotatedPage(
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("annotations")] List<Annotation> Annotations
);

public record NearestHeading(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("page")] int Page
);

public record BoundingBox(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("w")] double W,
    [property: JsonPropertyName("h")] double H
);

public record LayoutBlock(
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("class_id")] int ClassId,
    [property: JsonPropertyName("b_box")] BoundingBox BBox,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("reading_order")] int? ReadingOrder,
    [property: JsonPropertyName("text")] string? Text
);

public record HighlightRect(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("w")] double W,
    [property: JsonPropertyName("h")] double H
);

public record FreehandPoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y
);
