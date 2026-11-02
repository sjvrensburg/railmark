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

    private string MakeDocx(string body, string? comments = null, string? commentsExtended = null, string? footnotes = null)
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
        if (footnotes != null)
            Add("word/footnotes.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:footnotes {Ns}>{footnotes}</w:footnotes>");
        return path;
    }

    private (DocxReview Review, XDocument Document) Prepare(string docx, string part = "word/document.xml")
    {
        var output = Path.Combine(_dir.FullName, $"{Guid.NewGuid():N}-base.docx");
        var review = DocxReviewPreparer.Prepare(docx, output);
        using var zip = ZipFile.OpenRead(output);
        using var s = zip.GetEntry(part)!.Open();
        return (review, XDocument.Load(s, LoadOptions.PreserveWhitespace));
    }

    private static string Ins(int id, string author, string text) => $"<w:ins w:id=\"{id}\" w:author=\"{author}\" {Date}>{R(text)}</w:ins>";
    private static string Del(int id, string author, string text)
        => $"<w:del w:id=\"{id}\" w:author=\"{author}\" {Date}><w:r><w:delText>{text}</w:delText></w:r></w:del>";
    private static string Row(string a, string b, string trPr = "") => $"<w:tr>{trPr}<w:tc>{P(a)}</w:tc><w:tc>{P(b)}</w:tc></w:tr>";
    private static string RowMark(string kind, int id, string author) => $"<w:trPr><w:{kind} w:id=\"{id}\" w:author=\"{author}\" {Date}/></w:trPr>";

    private static bool HasMark(XElement el, string? name)
        => el.Elements(W + "bookmarkStart").Any(b => (string?)b.Attribute(W + "name") == name);

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

    [Fact]
    public void An_Inserted_Table_Row_Is_Removed_And_Is_One_Insertion_At_The_Next_Row()
    {
        var docx = MakeDocx("<w:tbl>"
            + Row(R("Keep A1"), R("Keep B1"))
            + Row(Ins(2, "Rob", "New A"), Ins(3, "Rob", "New B"), RowMark("ins", 1, "Rob"))
            + Row(R("Keep A3"), R("Keep B3")) + "</w:tbl>" + P(R("After.")));

        var (review, doc) = Prepare(docx);

        var item = Assert.Single(review.Items);
        Assert.Equal(DocxItemKind.Insertion, item.Kind);
        Assert.Equal("Rob", item.Author);
        Assert.Equal("Inserted table row: New A | New B", item.Text);
        var rows = doc.Descendants(W + "tr").ToList();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain("New", BodyText(doc));
        // The insertion point is the start of the row that now follows it.
        Assert.True(HasMark(rows[1].Descendants(W + "p").First(), item.PointMark));
    }

    [Fact]
    public void A_Table_Whose_Every_Row_Was_Inserted_Is_Removed_Whole()
    {
        var docx = MakeDocx(P(R("Before."))
            + "<w:tbl>" + Row(Ins(2, "Tia", "T1"), Ins(3, "Tia", "T2"), RowMark("ins", 1, "Tia"))
            + Row(Ins(5, "Tia", "T3"), Ins(6, "Tia", "T4"), RowMark("ins", 4, "Tia")) + "</w:tbl>"
            + P(R("After.")));

        var (review, doc) = Prepare(docx);

        var item = Assert.Single(review.Items);
        Assert.Equal("Inserted table:\nT1 | T2\nT3 | T4", item.Text);
        Assert.Empty(doc.Descendants(W + "tbl"));
        Assert.True(HasMark(doc.Descendants(W + "p").Last(), item.PointMark));
    }

    [Fact]
    public void A_Deleted_Table_Row_Stays_And_Only_Its_Text_Is_Struck()
    {
        var docx = MakeDocx("<w:tbl>"
            + Row(R("Keep A1"), R("Keep B1"))
            + Row(Del(2, "Rob", "Old A"), Del(3, "Rob", "Old B"), RowMark("del", 1, "Rob")) + "</w:tbl>" + P(R("After.")));

        var (review, doc) = Prepare(docx);

        // Two deletions — the row marker itself is not one (it used to be: an empty, unplaceable item).
        Assert.Equal([DocxItemKind.Deletion, DocxItemKind.Deletion], review.Items.Select(i => i.Kind));
        Assert.Equal(["Old A", "Old B"], review.Items.Select(i => i.Text));
        Assert.Equal(2, doc.Descendants(W + "tr").Count());
        Assert.Empty(doc.Descendants(W + "trPr").Elements());
    }

    [Fact]
    public void A_Paragraph_Property_Change_Restores_The_Original_And_Merges_Neighbours()
    {
        string Centred(string text) => $"<w:p><w:pPr><w:jc w:val=\"center\"/><w:rPr><w:b/></w:rPr>"
            + $"<w:pPrChange w:id=\"1\" w:author=\"Pat\" {Date}><w:pPr><w:ind w:left=\"720\"/></w:pPr></w:pPrChange></w:pPr>{R(text)}</w:p>";
        var docx = MakeDocx(Centred("One.") + Centred("Two.") + P(R("Three.")));

        var (review, doc) = Prepare(docx);

        var item = Assert.Single(review.Items);
        Assert.Equal(DocxItemKind.Formatting, item.Kind);
        Assert.Equal("Paragraph formatted: Alignment, Indentation", item.Text);
        var paragraphs = doc.Descendants(W + "p").ToList();
        foreach (var pPr in paragraphs.Take(2).Select(p => p.Element(W + "pPr")!))
        {
            Assert.Null(pPr.Element(W + "jc"));
            Assert.NotNull(pPr.Element(W + "ind"));
            Assert.NotNull(pPr.Element(W + "rPr")); // the paragraph mark's run properties are kept
            Assert.Null(pPr.Element(W + "pPrChange"));
        }
        // One range over both paragraphs: starting in the first, ending in the second.
        Assert.True(HasMark(paragraphs[0], item.StartMark));
        Assert.True(HasMark(paragraphs[1], item.EndMark));
    }

    [Fact]
    public void A_Section_Property_Change_Restores_The_Original_Margins_And_Is_Noted_At_Its_Start()
    {
        var docx = MakeDocx(P(R("First."))
            + $"<w:sectPr><w:headerReference w:type=\"default\" w:id=\"rIdH\"/><w:pgMar w:top=\"720\" w:bottom=\"720\"/>"
            + $"<w:sectPrChange w:id=\"1\" w:author=\"Sam\" {Date}><w:sectPr><w:pgMar w:top=\"1440\" w:bottom=\"1440\"/></w:sectPr></w:sectPrChange></w:sectPr>");

        var (review, doc) = Prepare(docx);

        var item = Assert.Single(review.Items);
        Assert.Equal("Section formatted: Margins", item.Text);
        Assert.NotNull(item.PointMark);
        Assert.True(HasMark(doc.Descendants(W + "p").First(), item.PointMark));
        var sectPr = doc.Descendants(W + "sectPr").Single();
        Assert.NotNull(sectPr.Element(W + "headerReference"));
        Assert.Equal("1440", (string?)sectPr.Element(W + "pgMar")!.Attribute(W + "top"));
        // The body margins are the restored ones.
        Assert.Equal((72f, 72f), review.BodyMargins);
    }

    [Fact]
    public void Table_Property_Changes_Restore_The_Original_And_Are_One_Note_Per_Table()
    {
        var docx = MakeDocx("<w:tbl><w:tblPr><w:tblW w:w=\"5000\"/>"
            + $"<w:tblPrChange w:id=\"1\" w:author=\"Tom\" {Date}><w:tblPr><w:tblW w:w=\"3000\"/></w:tblPr></w:tblPrChange></w:tblPr>"
            + $"<w:tblGrid><w:gridCol w:w=\"2500\"/><w:tblGridChange w:id=\"2\"><w:tblGrid><w:gridCol w:w=\"1500\"/></w:tblGrid></w:tblGridChange></w:tblGrid>"
            + $"<w:tr><w:tc><w:tcPr><w:tcW w:w=\"2500\"/><w:tcPrChange w:id=\"3\" w:author=\"Tom\" {Date}><w:tcPr><w:tcW w:w=\"1500\"/></w:tcPr></w:tcPrChange></w:tcPr>{P(R("Cell"))}</w:tc></w:tr></w:tbl>"
            + P(R("After.")));

        var (review, doc) = Prepare(docx);

        Assert.Empty(review.Unsupported);
        // One note: the grid change carries no author (Word writes none) and joins Tom's.
        var note = Assert.Single(review.Items);
        Assert.Equal(DocxItemKind.Formatting, note.Kind);
        Assert.Equal("Tom", note.Author);
        Assert.Equal("Table formatted: Table properties, Cell properties, Column widths", note.Text);
        Assert.Equal("3000", (string?)doc.Descendants(W + "tblW").Single().Attribute(W + "w"));
        Assert.Equal("1500", (string?)doc.Descendants(W + "gridCol").Single().Attribute(W + "w"));
        Assert.Equal("1500", (string?)doc.Descendants(W + "tcW").Single().Attribute(W + "w"));
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName.EndsWith("Change"));
    }

    [Fact]
    public void An_Anchor_Ending_A_Footnote_Is_Followed_By_A_Word_Joiner()
    {
        var footnotes = $"<w:footnote w:id=\"1\">{P(R("Note text"), Del(5, "Fay", " gone"))}</w:footnote>"
            + $"<w:footnote w:id=\"2\">{P(R("Plain note."))}</w:footnote>";
        var docx = MakeDocx(P(R("Body.")), footnotes: footnotes);

        var (review, notes) = Prepare(docx, "word/footnotes.xml");

        var item = Assert.Single(review.Items);
        Assert.Equal("footnotes", item.Part);
        var paragraphs = notes.Descendants(W + "p").ToList();
        // After the end bookmark: a run holding only U+2060, so the bookmark gets a position on export.
        var last = paragraphs[0].Elements().Last();
        Assert.Equal("\u2060", last.Element(W + "t")?.Value);
        Assert.Contains(last.ElementsBeforeSelf(W + "bookmarkStart"), b => (string?)b.Attribute(W + "name") == item.EndMark);
        // A footnote whose end has no anchor is left alone.
        Assert.False(paragraphs[1].Value.Contains('\u2060'), "a word joiner was added where no anchor ends the footnote");
    }

    [Fact]
    public void A_Picture_Reads_As_Image_In_Revision_Text()
    {
        var picture = "<w:r><w:drawing><wp:inline xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"/></w:drawing></w:r>";
        var docx = MakeDocx(P(R("See "), $"<w:ins w:id=\"1\" w:author=\"Ivo\" {Date}>{picture}</w:ins>", R(" here.")));

        var (review, _) = Prepare(docx);

        Assert.Equal("[image]", Assert.Single(review.Items).Text);
    }

    [Fact]
    public void The_Scrubber_Blanks_Only_The_Anchors_And_Keeps_Every_Offset()
    {
        var pdf = System.Text.Encoding.Latin1.GetBytes(
            "1 0 obj\n<</RRabcdec1s[3 0 R/XYZ 56.7 750.2 0]\n/UserMark[3 0 R/XYZ 1 2 0]\n/RRabcder12e 7 0 R>>\nendobj\n"
            + "2 0 obj\n<</Names[(RRabcdec2p)[3 0 R/Fit](Other)[3 0 R/Fit]]>>\nendobj\nstartxref 42");
        var before = pdf.Length;

        int count = NamedDestinationScrubber.Scrub(pdf, "RRabcde");

        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Equal(3, count);
        Assert.Equal(before, pdf.Length);
        Assert.DoesNotContain("RRabcde", text);
        Assert.Contains("/UserMark[3 0 R/XYZ 1 2 0]", text);
        Assert.Contains("(Other)[3 0 R/Fit]", text);
        Assert.EndsWith("startxref 42", text);
    }

    [Fact]
    public void Soffice_Does_Not_Inherit_The_Bundled_Library_Directory()
    {
        // The tarball/AppImage launcher prepends its bin/ to LD_LIBRARY_PATH.
        Assert.Equal("/usr/lib/extra", LibreOfficeConverter.WithoutOwnBundle("/opt/railmark/bin:/usr/lib/extra", "/opt/railmark/bin/"));
        Assert.Null(LibreOfficeConverter.WithoutOwnBundle("/opt/railmark/bin/", "/opt/railmark/bin"));
        Assert.Null(LibreOfficeConverter.WithoutOwnBundle(null, "/opt/railmark/bin"));
        Assert.Equal("/a:/b", LibreOfficeConverter.WithoutOwnBundle("/a::/b", "/opt/railmark/bin"));
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
    public void A_Caret_Sits_On_The_Baseline_Centred_On_The_Insertion_Point()
    {
        // A line whose glyph boxes span 100..120 (descenders included): baseline ≈ 116.
        var (x, y, w, h) = DocxAnnotationBuilder.CaretBox(200, 100, 120);

        Assert.Equal(10, h, 0.01f);                  // half a line
        Assert.Equal(200, x + w / 2, 0.01f);          // centred on the boundary
        Assert.True(y < 116 && y + h > 116);          // straddles the baseline
        Assert.True(y + h <= 120.5f);                 // and stays within the line
    }

    [Fact]
    public void WithinBody_Drops_Header_And_Footer_Lines_Whatever_Their_Text_Order()
    {
        RectF L(float top) => new(72, top, 500, top + 10);
        // LibreOffice 26.2 writes the header and footer BEFORE the body: no gap rule over text
        // order can drop them, but they sit in the margins (here 72pt top and bottom on 792pt).
        var tail = new List<RectF> { L(36), L(744), L(75), L(87) };

        Assert.Equal([L(75), L(87)], DocxAnnotationBuilder.WithinBody(tail, (72f, 72f), 792));
    }

    [Fact]
    public void Body_Margins_Are_The_Smallest_Over_All_Sections_In_Points()
    {
        var doc = XDocument.Parse(
            $"<w:document {Ns}><w:body>"
            + "<w:p><w:pPr><w:sectPr><w:pgMar w:top=\"1440\" w:bottom=\"2160\"/></w:sectPr></w:pPr></w:p>"
            + "<w:sectPr><w:pgMar w:top=\"-1080\" w:bottom=\"1440\"/></w:sectPr></w:body></w:document>");

        Assert.Equal((54f, 72f), DocxReviewPreparer.ReadBodyMargins(doc));
        Assert.Null(DocxReviewPreparer.ReadBodyMargins(XDocument.Parse($"<w:document {Ns}><w:body/></w:document>")));
    }
}
