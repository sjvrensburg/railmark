using RailMark.Services;
using RailReader.Core.Models;

namespace RailMark.Tests;

public class AnnotationFilterTests
{
    private static readonly DateTimeOffset Sept1 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static AnnotationFile File(params Annotation[] annotations)
    {
        var file = new AnnotationFile { SourcePdf = "doc.pdf" };
        file.Pages[0] = [.. annotations];
        return file;
    }

    private static IEnumerable<string?> Contents(AnnotationFile file)
        => file.Pages.Values.SelectMany(p => p).Select(a => a.Contents);

    [Fact]
    public void ByAuthor_Includes_Case_Insensitively()
    {
        var file = File(
            new HighlightAnnotation { Author = "AI Reviewer", Contents = "ai" },
            new HighlightAnnotation { Author = "Ann", Contents = "ann" },
            new HighlightAnnotation { Contents = "anonymous" });

        Assert.Equal(["ann"], Contents(AnnotationFilter.ByAuthor(file, ["ann"], null)));
        Assert.Equal(["ai", "ann"], Contents(AnnotationFilter.ByAuthor(file, ["ANN", "ai reviewer"], null)));
    }

    [Fact]
    public void ByAuthor_Exclude_Keeps_Anonymous_Annotations()
    {
        var file = File(
            new HighlightAnnotation { Author = "AI Reviewer", Contents = "ai" },
            new HighlightAnnotation { Author = "Ann", Contents = "ann" },
            new HighlightAnnotation { Contents = "anonymous" });

        Assert.Equal(["ann", "anonymous"], Contents(AnnotationFilter.ByAuthor(file, null, ["ai reviewer"])));
    }

    [Fact]
    public void ByAuthor_Drops_Pages_Left_Empty()
    {
        var file = File(new HighlightAnnotation { Author = "Ann" });
        file.Pages[3] = [new HighlightAnnotation { Author = "Bob" }];

        var filtered = AnnotationFilter.ByAuthor(file, ["Bob"], null);

        Assert.Equal([3], filtered.Pages.Keys);
    }

    [Fact]
    public void Since_Uses_Modified_Then_Created_And_Counts_Undated()
    {
        var file = File(
            new HighlightAnnotation { Contents = "old", CreatedUtc = Sept1.AddDays(-10) },
            new HighlightAnnotation { Contents = "edited", CreatedUtc = Sept1.AddDays(-10), ModifiedUtc = Sept1.AddDays(2) },
            new HighlightAnnotation { Contents = "new", CreatedUtc = Sept1 },
            new HighlightAnnotation { Contents = "stale edit", CreatedUtc = Sept1.AddDays(5), ModifiedUtc = Sept1.AddDays(-1) },
            new HighlightAnnotation { Contents = "undated" });

        var filtered = AnnotationFilter.Since(file, Sept1, out var undated);

        Assert.Equal(["edited", "new"], Contents(filtered));
        Assert.Equal(1, undated);
    }
}
