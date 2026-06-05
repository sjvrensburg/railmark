using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rr2Annotate.Models;

[JsonConverter(typeof(AnnotationConverter))]
public abstract record Annotation
{
    public required string Type { get; init; }
    public required string Color { get; init; }
    public required double Opacity { get; init; }
    public required List<LayoutBlock> OverlappingBlocks { get; init; }
    public required NearestHeading? NearestHeading { get; init; }
    /// <summary>Reviewer comment (PDF <c>/Contents</c> field). Populated for all types.</summary>
    public string? Contents { get; init; }

    /// <summary>Y-coordinate used for reading-order sorting.</summary>
    public abstract double SortY { get; }
}

public record HighlightAnnotation : Annotation
{
    public required List<HighlightRect> Rects { get; init; }
    public required string? Text { get; init; }
    public override double SortY => Rects.Count > 0 ? Rects[0].Y : 0;
}

public record TextNoteAnnotation : Annotation
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required string NoteText { get; init; }
    public override double SortY => Y;
}

public record RectAnnotation : Annotation
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double W { get; init; }
    public required double H { get; init; }
    public required bool Filled { get; init; }
    public required double StrokeWidth { get; init; }
    public required string? Text { get; init; }
    public override double SortY => Y;
}

public record FreehandAnnotation : Annotation
{
    public required List<FreehandPoint> Points { get; init; }
    public required double StrokeWidth { get; init; }
    public override double SortY => Points.Count > 0 ? Points.Min(p => p.Y) : 0;
}

/// <summary>
/// Caret annotation (read-only PDF annotation, positioned by x/y/w/h).
/// </summary>
public record CaretAnnotation : Annotation
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double W { get; init; }
    public required double H { get; init; }
    public override double SortY => Y;
}

/// <summary>
/// Free-text annotation — visible text lives in the inherited <c>Contents</c> field.
/// </summary>
public record FreeTextAnnotation : Annotation
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double W { get; init; }
    public required double H { get; init; }
    public override double SortY => Y;
}

/// <summary>
/// Fallback for annotation types not yet modelled explicitly.
/// </summary>
public record UnknownAnnotation : Annotation
{
    public override double SortY => 0;
}

public class AnnotationConverter : JsonConverter<Annotation>
{
    public override Annotation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString()!;

        var color = root.GetProperty("color").GetString()!;
        var opacity = root.GetProperty("opacity").GetDouble();
        var blocks = JsonSerializer.Deserialize<List<LayoutBlock>>(
            root.GetProperty("overlapping_blocks").GetRawText(), options) ?? [];
        NearestHeading? heading = root.TryGetProperty("nearest_heading", out var h) && h.ValueKind != JsonValueKind.Null
            ? JsonSerializer.Deserialize<NearestHeading>(h.GetRawText(), options)
            : null;
        var contents = root.TryGetProperty("contents", out var c) ? c.GetString() : null;

        return type switch
        {
            "highlight" or "underline" or "strikeout" or "squiggly" => new HighlightAnnotation
            {
                Type = type, Color = color, Opacity = opacity,
                OverlappingBlocks = blocks, NearestHeading = heading, Contents = contents,
                Rects = JsonSerializer.Deserialize<List<HighlightRect>>(
                    root.GetProperty("rects").GetRawText(), options) ?? [],
                Text = root.TryGetProperty("text", out var t) ? t.GetString() : null
            },
            "text_note" => new TextNoteAnnotation
            {
                Type = type, Color = color, Opacity = opacity,
                OverlappingBlocks = blocks, NearestHeading = heading, Contents = contents,
                X = root.GetProperty("x").GetDouble(),
                Y = root.GetProperty("y").GetDouble(),
                NoteText = root.GetProperty("note_text").GetString()!
            },
            "rect" => new RectAnnotation
            {
                Type = type, Color = color, Opacity = opacity,
                OverlappingBlocks = blocks, NearestHeading = heading, Contents = contents,
                X = root.GetProperty("x").GetDouble(),
                Y = root.GetProperty("y").GetDouble(),
                W = root.GetProperty("w").GetDouble(),
                H = root.GetProperty("h").GetDouble(),
                Filled = root.TryGetProperty("filled", out var f) && f.GetBoolean(),
                StrokeWidth = root.TryGetProperty("stroke_width", out var sw) ? sw.GetDouble() : 0,
                Text = root.TryGetProperty("text", out var rt) ? rt.GetString() : null
            },
            "freehand" => new FreehandAnnotation
            {
                Type = type, Color = color, Opacity = opacity,
                OverlappingBlocks = blocks, NearestHeading = heading, Contents = contents,
                Points = JsonSerializer.Deserialize<List<FreehandPoint>>(
                    root.GetProperty("points").GetRawText(), options) ?? [],
                StrokeWidth = root.TryGetProperty("stroke_width", out var fsw) ? fsw.GetDouble() : 0
            },
            "caret" => new CaretAnnotation
            {
                Type = type, Color = color, Opacity = opacity,
                OverlappingBlocks = blocks, NearestHeading = heading, Contents = contents,
                X = root.GetProperty("x").GetDouble(),
                Y = root.GetProperty("y").GetDouble(),
                W = root.GetProperty("w").GetDouble(),
                H = root.GetProperty("h").GetDouble()
            },
            "free_text" => new FreeTextAnnotation
            {
                Type = type, Color = color, Opacity = opacity,
                OverlappingBlocks = blocks, NearestHeading = heading, Contents = contents,
                X = root.GetProperty("x").GetDouble(),
                Y = root.GetProperty("y").GetDouble(),
                W = root.GetProperty("w").GetDouble(),
                H = root.GetProperty("h").GetDouble()
            },
            _ => new UnknownAnnotation
            {
                Type = type, Color = color, Opacity = opacity,
                OverlappingBlocks = blocks, NearestHeading = heading, Contents = contents
            }
        };
    }

    public override void Write(Utf8JsonWriter writer, Annotation value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}
