using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using RailMark.Services.Docx;
using RailReader.Core.Models;

namespace RailMark.Tests;

public class DocxImportTests : IDisposable
{
    private const string Ns =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" "
        + "xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" "
        + "xmlns:w15=\"http://schemas.microsoft.com/office/word/2012/wordml\"";
    private const string Date = "w:date=\"2026-10-30T10:00:00Z\"";
    private static readonly XNamespace W = DocxReviewPreparer.W;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("railmark-docx-test-");
    public void Dispose() => _dir.Delete(recursive: true);

    private static string R(string text, string rPr = "") => $"<w:r>{rPr}<w:t xml:space=\"preserve\">{text}</w:t></w:r>";
    private static string P(params string[] content) => $"<w:p>{string.Concat(content)}</w:p>";

    private string MakeDocx(string body, string? comments = null, string? commentsExtended = null)
    {
        var path = Path.Combine(_dir.FullName, $"{Guid.NewGuid():N}.docx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var s = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            s.Write(content);
        }
        Add("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {Ns}><w:body>{body}</w:body></w:document>");
        if (comments != null)
            Add("word/comments.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:comments {Ns}>{comments}</w:comments>");
        if (commentsExtended != null)
            Add("word/commentsExtended.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w15:commentsEx {Ns}>{commentsExtended}</w15:commentsEx>");
        return path;
    }

    private (DocxReview Review, XDocument Document) Prepare(string docx)
    {
        var output = Path.Combine(_dir.FullName, $"{Guid.NewGuid():N}-base.docx");
        var review = DocxReviewPreparer.Prepare(docx, output);
        using var zip = ZipFile.OpenRead(output);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        return (review, XDocument.Load(s, LoadOptions.PreserveWhitespace));
    }

    private static string BodyText(XDocument doc) => string.Concat(doc.Descendants(W + "t").Select(t => t.Value));

    private static List<string> MarkNames(XDocument doc)
        => doc.Descendants(W + "bookmarkStart").Select(b => (string)b.Attribute(W + "name")!).ToList();

    [Fact]
    public void Renders_The_Original_Text_With_Every_Change_Rejected()
    {
        var docx = MakeDocx(P(
            R("We measured "),
            $"<w:del w:id=\"1\" w:author=\"Carol\" {Date}><w:r><w:delText>three</w:delText></w:r></w:del>",
            $"<w:ins w:id=\"2\" w:author=\"Carol\" {Date}>{R("four")}</w:ins>",
            R(" samples."),
            $"<w:ins w:id=\"3\" w:author=\"Dave\" {Date}>{R(" Added.")}</w:ins>"));

        var (_, doc) = Prepare(docx);

        Assert.Equal("We measured three samples.", BodyText(doc));
        Assert.Empty(doc.Descendants(W + "ins"));
        Assert.Empty(doc.Descendants(W + "del"));
        Assert.Empty(doc.Descendants(W + "delText"));
    }

    [Fact]
    public void A_Deletion_Followed_By_An_Insertion_From_The_Same_Author_Is_One_Replacement()
    {
        var docx = MakeDocx(P(
            R("We measured "),
            $"<w:del w:id=\"1\" w:author=\"Carol\" {Date}><w:r><w:delText>three</w:delText></w:r></w:del>",
            $"<w:ins w:id=\"2\" w:author=\"Carol\" {Date}>{R("four")}</w:ins>",
            R(" samples.")));

        var (review, doc) = Prepare(docx);

        var item = Assert.Single(review.Items);
        Assert.Equal(DocxItemKind.Replacement, item.Kind);
        Assert.Equal("four", item.Text);
        Assert.Equal("Carol", item.Author);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero), item.Date);

        // The range brackets exactly the deleted text.
        var marks = MarkNames(doc);
        var runs = doc.Descendants(W + "p").Single().Elements().ToList();
        int start = runs.FindIndex(e => (string?)e.Attribute(W + "name") == item.StartMark);
        int end = runs.FindIndex(e => (string?)e.Attribute(W + "name") == item.EndMark);
        Assert.True(start >= 0 && end > start);
        Assert.Equal("three", string.Concat(runs.Skip(start).Take(end - start).SelectMany(e => e.Descendants(W + "t")).Select(t => t.Value)));
        Assert.Contains(item.StartMark, marks);
    }

    [Fact]
    public void An_Insertion_Followed_By_A_Deletion_From_The_Same_Author_Is_Also_A_Replacement()
    {
        // Word writes this order too (seen in a real document: "Semester" inserted before "Term" deleted).
        var docx = MakeDocx(P(
            R("Second "),
            $"<w:ins w:id=\"1\" w:author=\"Kath\" {Date}>{R("Semester")}</w:ins>",
            $"<w:del w:id=\"2\" w:author=\"Kath\" {Date}><w:r><w:delText>Term</w:delText></w:r></w:del>",
            R(" test")));

        var (review, doc) = Prepare(docx);

        var item = Assert.Single(review.Items);
        Assert.Equal(DocxItemKind.Replacement, item.Kind);
        Assert.Equal("Semester", item.Text);
        Assert.Equal("Second Term test", BodyText(doc));
    }

    [Fact]
    public void A_Deletion_And_Insertion_By_Different_Authors_Stay_Separate()
    {
        var docx = MakeDocx(P(
            $"<w:del w:id=\"1\" w:author=\"Carol\" {Date}><w:r><w:delText>three</w:delText></w:r></w:del>",
            $"<w:ins w:id=\"2\" w:author=\"Dave\" {Date}>{R("four")}</w:ins>"));

        var (review, _) = Prepare(docx);

        Assert.Equal([DocxItemKind.Deletion, DocxItemKind.Insertion], review.Items.Select(i => i.Kind));
        Assert.NotNull(review.Items[1].PointMark);
    }

    [Fact]
    public void Comments_Become_Ranges_And_Replies_Are_Threaded_Under_Their_Parent()
    {
        var docx = MakeDocx(
            P(R("The quick "), "<w:commentRangeStart w:id=\"0\"/>", R("brown fox"), "<w:commentRangeEnd w:id=\"0\"/>",
              "<w:r><w:commentReference w:id=\"0\"/></w:r>",
              "<w:commentRangeStart w:id=\"1\"/><w:commentRangeEnd w:id=\"1\"/><w:r><w:commentReference w:id=\"1\"/></w:r>",
              R(" jumps.")),
            comments:
                $"<w:comment w:id=\"0\" w:author=\"Alice\" {Date}><w:p w14:paraId=\"0000000A\">{R("Colour?")}</w:p></w:comment>"
                + $"<w:comment w:id=\"1\" w:author=\"Bob\" {Date}><w:p w14:paraId=\"0000000B\">{R("Keep it.")}</w:p></w:comment>",
            commentsExtended:
                "<w15:commentEx w15:paraId=\"0000000A\" w15:done=\"1\"/>"
                + "<w15:commentEx w15:paraId=\"0000000B\" w15:paraIdParent=\"0000000A\" w15:done=\"0\"/>");

        var (review, doc) = Prepare(docx);

        Assert.Equal(2, review.Items.Count);
        var comment = review.Items[0];
        Assert.Equal(DocxItemKind.Comment, comment.Kind);
        Assert.Equal("Colour?", comment.Text);
        Assert.True(comment.Done);
        Assert.NotNull(comment.StartMark);
        Assert.NotNull(comment.EndMark);

        var reply = review.Items[1];
        Assert.Equal(DocxItemKind.Reply, reply.Kind);
        Assert.Equal("0", reply.ParentId);
        Assert.Null(reply.StartMark);

        // No comment machinery survives into the rendered document, and the reply has no anchors.
        Assert.Empty(doc.Descendants(W + "commentReference"));
        Assert.Empty(doc.Descendants(W + "commentRangeStart"));
        Assert.Equal("The quick brown fox jumps.", BodyText(doc));
        Assert.Equal(3, MarkNames(doc).Count(n => n.StartsWith(review.MarkPrefix)));
    }

    [Fact]
    public void Anchor_Names_Are_Letters_And_Digits_Only()
    {
        // LibreOffice rewrites anything else in a destination name ('_' becomes "5F").
        var docx = MakeDocx(P(
            $"<w:del w:id=\"1\" w:author=\"A\" {Date}><w:r><w:delText>x</w:delText></w:r></w:del>",
            "<w:commentRangeStart w:id=\"7\"/>", R("y"), "<w:commentRangeEnd w:id=\"7\"/>"),
            comments: $"<w:comment w:id=\"7\" w:author=\"B\" {Date}><w:p>{R("c")}</w:p></w:comment>");

        var (review, doc) = Prepare(docx);

        Assert.All(MarkNames(doc).Where(n => n.StartsWith(review.MarkPrefix)), n => Assert.True(n.All(char.IsAsciiLetterOrDigit), n));
    }

    [Fact]
    public void Items_Come_Out_In_Document_Order()
    {
        var docx = MakeDocx(
            P($"<w:del w:id=\"1\" w:author=\"A\" {Date}><w:r><w:delText>first</w:delText></w:r></w:del>",
              R(" then "), "<w:commentRangeStart w:id=\"0\"/>", R("second"), "<w:commentRangeEnd w:id=\"0\"/>"),
            comments: $"<w:comment w:id=\"0\" w:author=\"B\" {Date}><w:p>{R("c")}</w:p></w:comment>");

        var (review, _) = Prepare(docx);

        Assert.Equal([DocxItemKind.Deletion, DocxItemKind.Comment], review.Items.Select(i => i.Kind));
    }

    [Fact]
    public void A_Format_Change_Restores_The_Original_Formatting_And_Describes_It()
    {
        var docx = MakeDocx(P(
            R("This is "),
            $"<w:r><w:rPr><w:b/><w:rPrChange w:id=\"5\" w:author=\"Erin\" {Date}><w:rPr><w:i/></w:rPr></w:rPrChange></w:rPr><w:t>key</w:t></w:r>"));

        var (review, doc) = Prepare(docx);

        var item = Assert.Single(review.Items);
        Assert.Equal(DocxItemKind.Formatting, item.Kind);
        Assert.Equal("Formatted: Bold, not Italic", item.Text);
        var rPr = doc.Descendants(W + "rPr").Single();
        Assert.NotNull(rPr.Element(W + "i"));
        Assert.Null(rPr.Element(W + "b"));
        Assert.Empty(doc.Descendants(W + "rPrChange"));
    }

    [Fact]
    public void An_Inserted_Paragraph_Is_Removed_And_Its_Anchor_Moves_To_The_Next()
    {
        var docx = MakeDocx(
            $"<w:p><w:pPr><w:rPr><w:ins w:id=\"9\" w:author=\"Gina\" {Date}/></w:rPr></w:pPr>"
            + $"<w:ins w:id=\"8\" w:author=\"Gina\" {Date}>{R("New paragraph.")}</w:ins></w:p>"
            + P(R("Next.")));

        var (review, doc) = Prepare(docx);

        var item = Assert.Single(review.Items);
        Assert.Equal(DocxItemKind.Insertion, item.Kind);
        var p = Assert.Single(doc.Descendants(W + "p"));
        Assert.Contains(p.Elements(W + "bookmarkStart"), b => (string?)b.Attribute(W + "name") == item.PointMark);
    }

    [Fact]
    public void An_Insertion_Wrapping_A_Later_Deletion_Disappears_Whole()
    {
        var docx = MakeDocx(P(
            R("A "),
            $"<w:ins w:id=\"1\" w:author=\"X\" {Date}>{R("kept ")}<w:del w:id=\"2\" w:author=\"Y\" {Date}><w:r><w:delText>gone</w:delText></w:r></w:del></w:ins>",
            R("B")));

        var (review, doc) = Prepare(docx);

        Assert.Equal("A B", BodyText(doc));
        Assert.Equal(DocxItemKind.Insertion, Assert.Single(review.Items).Kind);
    }

    // ------------------------------------------------------------ geometry

    /// <summary>One line of text as a PageText: 5pt-wide glyphs, x-height letters shorter than ascenders.</summary>
    private static PageText Line(string text, float left, float top)
    {
        var boxes = new List<CharBox>();
        for (int i = 0; i < text.Length; i++)
        {
            float x = left + i * 5f;
            bool tall = "bdfhklt".Contains(text[i]) || char.IsUpper(text[i]);
            boxes.Add(text[i] == ' '
                ? new CharBox(i, x, top + 10, x + 5, top + 10)
                : new CharBox(i, x, top + (tall ? 0 : 3), x + 5, top + 10));
        }
        return new PageText(text, boxes);
    }

    [Fact]
    public void BoundaryAt_Uses_The_Whole_Line_Not_Only_Its_Tall_Letters()
    {
        // "We measured three samples": the end of "three" is between index 17 and 18.
        var page = Line("We measured three samples", 100, 200);
        float endOfThree = 100 + 17 * 5f;

        var at = DocxAnnotationBuilder.BoundaryAt(page, endOfThree + 0.3f, 198f);

        Assert.NotNull(at);
        Assert.Equal(17, at.Value.Boundary);
    }

    [Fact]
    public void BoundaryAt_Picks_The_Line_Below_A_Destination_At_Its_Top()
    {
        var first = Line("aaaa", 100, 100);
        var second = Line("bbbb", 100, 113);
        var page = new PageText(first.Text + second.Text,
            [.. first.CharBoxes, .. second.CharBoxes.Select(b => b with { Index = b.Index + 4 })]);

        var at = DocxAnnotationBuilder.BoundaryAt(page, 110f, 112f);

        Assert.Equal(6, at!.Value.Boundary);
    }

    [Fact]
    public void BoundaryAt_A_Page_Without_Text_Has_No_Boundary()
        => Assert.Null(DocxAnnotationBuilder.BoundaryAt(new PageText("", []), 100, 100));

    [Fact]
    public void TrimRunningText_Drops_A_Footer_After_The_Body_And_A_Header_Before_It()
    {
        RectF L(float top) => new(72, top, 500, top + 10);
        var head = new List<RectF> { L(600), L(613), L(626), L(740) };   // last body lines, then the footer
        var tail = new List<RectF> { L(40), L(80), L(93) };              // header, then the first body lines

        Assert.Equal(3, DocxAnnotationBuilder.TrimRunningText(head, trimEnd: true, trimStart: false).Count);
        var trimmedTail = DocxAnnotationBuilder.TrimRunningText(tail, trimEnd: false, trimStart: true);
        Assert.Equal([L(80), L(93)], trimmedTail);
    }
}
