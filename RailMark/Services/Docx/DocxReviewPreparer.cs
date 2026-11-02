using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RailMark.Services.Docx;

/// <summary>
/// Reads the comments and tracked changes out of a .docx and writes a copy of it that renders the
/// <b>original</b> text — every change rejected, every comment removed — with an empty bookmark at
/// each place a comment or change begins and ends.
/// </summary>
/// <remarks>
/// <para>The bookmarks are the anchoring mechanism. LibreOffice exports Word bookmarks as PDF named
/// destinations (<c>ExportBookmarksToPDFDestination</c>), each at the exact page position of the
/// bookmark, and an empty bookmark takes no space, so the layout is the one the document would have
/// without them. That gives the annotation builder exact positions without having to search the
/// PDF for quoted text — which needs a page number nobody knows before layout, and is ambiguous
/// wherever a phrase repeats.</para>
/// <para>Rejecting rather than accepting the changes is the Acrobat model: a deletion is a strikeout
/// over text that is on the page, an insertion is a mark at the point where text would go.</para>
/// <para>Plain <see cref="XDocument"/> over the zip — no Open XML SDK dependency. Only the parts
/// that hold body text are rewritten.</para>
/// </remarks>
internal static class DocxReviewPreparer
{
    internal static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    private static readonly XNamespace W15 = "http://schemas.microsoft.com/office/word/2012/wordml";

    private static readonly Regex StoryPart =
        new(@"^word/(document|header\d*|footer\d*|footnotes|endnotes)\.xml$", RegexOptions.IgnoreCase);

    /// <summary>Bookmark ids are scoped per document; start well clear of any Word assigns.</summary>
    private const int FirstBookmarkId = 1_900_000_000;

    private static readonly HashSet<XName> RevisionContainers =
        [W + "ins", W + "del", W + "moveFrom", W + "moveTo"];

    /// <summary>Markers that carry no text: skipped when deciding whether a del and an ins are adjacent.</summary>
    private static readonly HashSet<XName> Markers =
    [
        W + "bookmarkStart", W + "bookmarkEnd", W + "commentRangeStart", W + "commentRangeEnd",
        W + "moveFromRangeStart", W + "moveFromRangeEnd", W + "moveToRangeStart", W + "moveToRangeEnd",
        W + "proofErr", W + "permStart", W + "permEnd",
    ];

    /// <summary>Property revisions that are not annotated; the current value is kept.</summary>
    private static readonly XName[] PropertyChanges = [W + "numberingChange"];

    /// <summary>A table's property revisions, each holding the properties as they were before.</summary>
    private static readonly XName[] TablePropertyChanges =
        [W + "tblPrChange", W + "tblPrExChange", W + "trPrChange", W + "tcPrChange", W + "tblGridChange"];

    /// <summary>Containers whose last paragraph drops a bookmark at its very end on export (see <see cref="KeepTrailingAnchors"/>).</summary>
    private static readonly XName[] Frames = [W + "footnote", W + "endnote", W + "txbxContent"];

    public static DocxReview Prepare(string docxPath, string outputDocxPath)
    {
        var review = new DocxReview { MarkPrefix = "RR" + RandomTag() };
        var state = new State(review);

        using var source = ZipFile.OpenRead(docxPath);
        var comments = ReadComments(source);

        using (var target = ZipFile.Open(outputDocxPath, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                var match = StoryPart.Match(entry.FullName);
                if (!match.Success)
                {
                    using var from = entry.Open();
                    using var to = target.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                    from.CopyTo(to);
                    continue;
                }

                XDocument xml;
                using (var from = entry.Open()) xml = XDocument.Load(from, LoadOptions.PreserveWhitespace);
                state.Part = match.Groups[1].Value.ToLowerInvariant();
                Rewrite(xml, state, comments);
                // After the rewrite: a section change has put the original margins back.
                if (state.Part == "document") review.BodyMargins = ReadBodyMargins(xml);
                using var stream = target.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                xml.Save(stream, SaveOptions.DisableFormatting);
            }
        }

        // Replies are anchored through their parent; put each straight after it.
        var ordered = new List<DocxReviewItem>();
        var replies = comments.Values.Where(c => c.ParentId != null).ToList();
        foreach (var item in state.Items)
        {
            ordered.Add(item);
            if (item.Kind == DocxItemKind.Comment)
                ordered.AddRange(replies.Where(r => r.ParentId == item.Id).OrderBy(r => r.Date));
        }
        review.Items.AddRange(ordered);
        return review;
    }

    // ---------------------------------------------------------------- comments

    private static Dictionary<string, DocxReviewItem> ReadComments(ZipArchive zip)
    {
        var result = new Dictionary<string, DocxReviewItem>(StringComparer.Ordinal);
        var part = zip.GetEntry("word/comments.xml");
        if (part is null) return result;

        XDocument xml;
        using (var s = part.Open()) xml = XDocument.Load(s, LoadOptions.PreserveWhitespace);

        // commentsExtended keys threads and "resolved" by the paraId of a comment's LAST paragraph.
        var byParaId = new Dictionary<string, DocxReviewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in xml.Root!.Elements(W + "comment"))
        {
            var id = (string?)c.Attribute(W + "id");
            if (id is null) continue;
            var item = new DocxReviewItem
            {
                Kind = DocxItemKind.Comment,
                Id = id,
                Author = NullIfEmpty((string?)c.Attribute(W + "author")),
                Date = ParseDate((string?)c.Attribute(W + "date")),
                Text = string.Join("\n", c.Elements(W + "p").Select(TextOf)).Trim(),
            };
            result[id] = item;
            if (c.Elements(W + "p").LastOrDefault()?.Attribute(W14 + "paraId")?.Value is { } paraId)
                byParaId[paraId] = item;
        }

        var extended = zip.GetEntry("word/commentsExtended.xml");
        if (extended is null) return result;
        XDocument ex;
        using (var s = extended.Open()) ex = XDocument.Load(s);
        foreach (var e in ex.Root!.Elements(W15 + "commentEx"))
        {
            if ((string?)e.Attribute(W15 + "paraId") is not { } paraId || !byParaId.TryGetValue(paraId, out var item))
                continue;
            item.Done = (string?)e.Attribute(W15 + "done") is "1" or "true";
            if ((string?)e.Attribute(W15 + "paraIdParent") is { } parentPara
                && byParaId.TryGetValue(parentPara, out var parent) && !ReferenceEquals(parent, item))
                item.ParentId = parent.Id;
        }

        // Word nests replies one level only, but follow a chain to its root regardless.
        foreach (var item in result.Values.Where(i => i.ParentId != null).ToList())
        {
            var root = item;
            var seen = new HashSet<string>();
            while (root.ParentId != null && seen.Add(root.Id) && result.TryGetValue(root.ParentId, out var up))
                root = up;
            if (!ReferenceEquals(root, item))
            {
                item.ParentId = root.Id;
                result[item.Id] = new DocxReviewItem
                {
                    Kind = DocxItemKind.Reply, Id = item.Id, Author = item.Author, Date = item.Date,
                    Text = item.Text, ParentId = root.Id, Done = item.Done,
                };
            }
            else item.ParentId = null;
        }
        return result;
    }

    // ---------------------------------------------------------------- rewrite

    private sealed class State(DocxReview review)
    {
        public DocxReview Review { get; } = review;
        public string Part { get; set; } = "document";
        public List<DocxReviewItem> Items { get; } = [];
        public int NextBookmarkId = FirstBookmarkId;
        public int NextRevision;

        public string Mark(string suffix) => Review.MarkPrefix + suffix;

        public XElement[] Bookmark(string? name)
        {
            ArgumentNullException.ThrowIfNull(name);
            var id = (NextBookmarkId++).ToString(CultureInfo.InvariantCulture);
            return
            [
                new XElement(W + "bookmarkStart", new XAttribute(W + "id", id), new XAttribute(W + "name", name)),
                new XElement(W + "bookmarkEnd", new XAttribute(W + "id", id)),
            ];
        }

        public DocxReviewItem Add(DocxReviewItem item)
        {
            item.Part = Part;
            Items.Add(item);
            return item;
        }
    }

    private static void Rewrite(XDocument xml, State state, Dictionary<string, DocxReviewItem> comments)
    {
        var root = xml.Root!;
        RewriteComments(root, state, comments);
        RewriteInsertedRows(root, state);
        RewriteFormatting(root, state);
        RewriteParagraphProperties(root, state);
        RewriteSectionProperties(root, state);
        RewriteTableProperties(root, state);
        RewriteRevisions(root, state);
        RemoveInsertedParagraphs(root);
        SortPartInDocumentOrder(root, state);

        // Paragraph-mark and table-row revisions: drop the marker (a deleted row stays, its text
        // struck through cell by cell; an inserted one is gone already).
        foreach (var mark in root.Descendants().Where(e =>
                     RevisionContainers.Contains(e.Name) && e.Parent is { } parent && MarkerParents.Contains(parent.Name)).ToList())
            mark.Remove();
        foreach (var mark in root.Descendants().Where(e =>
                     e.Name == W + "cellIns" || e.Name == W + "cellDel" || e.Name == W + "cellMerge").ToList())
        {
            state.Review.CountUnsupported(mark.Name.LocalName);
            mark.Remove();
        }
        foreach (var name in PropertyChanges)
            foreach (var change in root.Descendants(name).ToList())
            {
                state.Review.CountUnsupported(name.LocalName);
                change.Remove();
            }
        foreach (var marker in root.Descendants().Where(e =>
                     e.Name == W + "moveFromRangeStart" || e.Name == W + "moveFromRangeEnd"
                     || e.Name == W + "moveToRangeStart" || e.Name == W + "moveToRangeEnd"
                     || e.Name == W + "customXmlInsRangeStart" || e.Name == W + "customXmlInsRangeEnd"
                     || e.Name == W + "customXmlDelRangeStart" || e.Name == W + "customXmlDelRangeEnd").ToList())
            marker.Remove();
        KeepTrailingAnchors(root, state);
    }

    /// <summary>Where an ins/del is a marker on a paragraph mark or a table row, not a container of text.</summary>
    private static readonly HashSet<XName> MarkerParents = [W + "rPr", W + "trPr"];

    /// <summary>
    /// LibreOffice exports no destination for a bookmark at the very end of the last paragraph of a
    /// footnote, endnote or text box (one anywhere else, even at a paragraph's end, is fine). A word
    /// joiner after it — zero width, no break opportunity, in the same font so the line keeps its
    /// height — gives it a position.
    /// </summary>
    private static void KeepTrailingAnchors(XElement root, State state)
    {
        foreach (var frame in Frames.SelectMany(n => root.Descendants(n)).ToList())
        {
            if (frame.Elements(W + "p").LastOrDefault() is not { } p) continue;
            var lastRun = p.Elements(W + "r").LastOrDefault();
            bool ours = p.Elements(W + "bookmarkStart")
                .Where(b => lastRun is null || b.IsAfter(lastRun))
                .Any(b => ((string?)b.Attribute(W + "name"))?.StartsWith(state.Review.MarkPrefix, StringComparison.Ordinal) == true);
            if (!ours) continue;
            var rPr = lastRun?.Element(W + "rPr");
            p.Add(new XElement(W + "r",
                rPr is null ? null : new XElement(rPr),
                new XElement(W + "t", "\u2060")));
        }
    }

    // ---------------------------------------------------------------- table rows

    private static bool IsInsertedRow(XElement tr) => tr.Element(W + "trPr")?.Element(W + "ins") is not null;

    /// <summary>
    /// An inserted table row (<c>w:trPr/w:ins</c>) is absent from the original, so it is removed; a
    /// run of them by one author becomes one insertion at the place they were, carrying their cells'
    /// text, and a table whose every row was inserted is removed whole. Bookmarks inside are kept.
    /// </summary>
    private static void RewriteInsertedRows(XElement root, State state)
    {
        foreach (var table in root.Descendants(W + "tbl").Where(t => t.Elements(W + "tr").Any(IsInsertedRow)).ToList())
        {
            if (table.Ancestors(W + "tr").Any(IsInsertedRow)) continue;   // goes with the row it is in
            var rows = table.Elements(W + "tr").ToList();
            bool whole = rows.All(IsInsertedRow);

            for (int i = 0; i < rows.Count; i++)
            {
                if (!IsInsertedRow(rows[i])) continue;
                var mark = rows[i].Element(W + "trPr")!.Element(W + "ins")!;
                var author = NullIfEmpty((string?)mark.Attribute(W + "author"));
                int j = i;
                while (j + 1 < rows.Count && IsInsertedRow(rows[j + 1])
                       && NullIfEmpty((string?)rows[j + 1].Element(W + "trPr")!.Element(W + "ins")!.Attribute(W + "author")) == author)
                    j++;
                var run = rows.GetRange(i, j - i + 1);
                var text = string.Join("\n", run.Select(RowText));
                var label = whole ? "Inserted table" : run.Count == 1 ? "Inserted table row" : "Inserted table rows";

                var n = state.NextRevision++;
                var item = state.Add(new DocxReviewItem
                {
                    Kind = DocxItemKind.Insertion, Id = n.ToString(CultureInfo.InvariantCulture), Author = author,
                    Date = ParseDate((string?)mark.Attribute(W + "date")),
                    Text = (run.Count == 1 && !whole ? label + ": " : label + ":\n") + text,
                    PointMark = state.Mark($"r{n}p"),
                });
                var anchor = state.Bookmark(item.PointMark).Concat(run.SelectMany(KeptMarkers)).ToList();
                foreach (var m in anchor.Skip(2)) m.Remove();
                foreach (var r in run) r.Remove();

                if (whole) PlaceAfter(table, anchor);
                else if (j + 1 < rows.Count) AtStartOf(FirstParagraph(rows[j + 1]), anchor);
                else if (rows.Take(i).LastOrDefault(r => !IsInsertedRow(r)) is { } before) AtEndOf(LastParagraph(before), anchor);
                i = j;
            }
            if (whole) table.Remove();
        }
    }

    private static string RowText(XElement tr)
        => string.Join(" | ", tr.Elements(W + "tc").Select(tc => TextOf(tc).Replace('\n', ' ').Trim()));

    private static XElement? FirstParagraph(XElement el) => el.Descendants(W + "p").FirstOrDefault();
    private static XElement? LastParagraph(XElement el) => el.Descendants(W + "p").LastOrDefault();

    private static void AtStartOf(XElement? p, IEnumerable<XElement> nodes)
    {
        if (p is null) return;
        if (p.Element(W + "pPr") is { } pPr) pPr.AddAfterSelf(nodes);
        else p.AddFirst(nodes);
    }

    private static void AtEndOf(XElement? p, IEnumerable<XElement> nodes) => p?.Add(nodes);

    /// <summary>At the start of the paragraph after <paramref name="el"/>, or failing that the end of the one before.</summary>
    private static void PlaceAfter(XElement el, List<XElement> nodes)
    {
        var next = el.ElementsAfterSelf().Select(e => e.Name == W + "p" ? e : FirstParagraph(e)).FirstOrDefault(p => p is not null);
        if (next is not null) { AtStartOf(next, nodes); return; }
        var previous = el.ElementsBeforeSelf().Reverse().Select(e => e.Name == W + "p" ? e : LastParagraph(e)).FirstOrDefault(p => p is not null);
        AtEndOf(previous, nodes);
    }

    // ---------------------------------------------------------------- property revisions

    private static readonly (string Tag, string Name)[] ParagraphPropertyNames =
    [
        ("pStyle", "Style"), ("jc", "Alignment"), ("ind", "Indentation"), ("spacing", "Spacing"),
        ("numPr", "Bullets and numbering"), ("tabs", "Tabs"), ("pBdr", "Borders"), ("shd", "Shading"),
        ("keepNext", "Keep with next"), ("keepLines", "Keep lines together"), ("pageBreakBefore", "Page break before"),
        ("widowControl", "Widow control"), ("outlineLvl", "Outline level"), ("contextualSpacing", "Spacing"),
        ("bidi", "Text direction"), ("textAlignment", "Alignment"), ("framePr", "Frame"),
    ];

    private static readonly (string Tag, string Name)[] SectionPropertyNames =
    [
        ("pgSz", "Page size"), ("pgMar", "Margins"), ("cols", "Columns"), ("type", "Section break"),
        ("pgNumType", "Page numbering"), ("lnNumType", "Line numbering"), ("vAlign", "Vertical alignment"),
        ("titlePg", "Different first page"), ("pgBorders", "Page borders"), ("docGrid", "Document grid"),
        ("textDirection", "Text direction"),
    ];

    /// <summary>Names of the properties that differ between two property elements — Word's balloon wording, roughly.</summary>
    private static List<string> ChangedProperties(XElement? before, XElement now, (string Tag, string Name)[] names, params XName[] ignore)
    {
        static string? Val(XElement? props, string tag)
            => props?.Element(W + tag) is { } e ? e.ToString(SaveOptions.DisableFormatting) : null;
        var parts = new List<string>();
        foreach (var (tag, name) in names)
            if (Val(before, tag) != Val(now, tag) && !parts.Contains(name)) parts.Add(name);
        // Anything else that changed, unnamed.
        var known = names.Select(n => W + n.Tag).Concat(ignore).ToHashSet();
        bool other = (before?.Elements() ?? []).Concat(now.Elements()).Select(e => e.Name).Distinct()
            .Where(n => !known.Contains(n))
            .Any(n => before?.Element(n)?.ToString(SaveOptions.DisableFormatting) != now.Element(n)?.ToString(SaveOptions.DisableFormatting));
        if (other) parts.Add("other properties");
        return parts;
    }

    /// <summary>
    /// A paragraph-property revision (<c>w:pPrChange</c>: alignment, indentation, style, bullets and
    /// numbering…) renders the paragraph as it was and is annotated over its text, like a run-format
    /// change. Neighbouring paragraphs changed alike by one author read as one change.
    /// </summary>
    private static void RewriteParagraphProperties(XElement root, State state)
    {
        DocxReviewItem? last = null;
        XElement? lastParagraph = null, lastEnd = null;
        foreach (var change in root.Descendants(W + "pPrChange").ToList())
        {
            var pPr = change.Parent;
            var p = pPr?.Parent;
            if (pPr is null || p is null || p.Name != W + "p")
            {
                change.Remove();
                continue;
            }

            var original = change.Element(W + "pPr");
            var author = NullIfEmpty((string?)change.Attribute(W + "author"));
            var date = ParseDate((string?)change.Attribute(W + "date"));
            change.Remove();
            var now = new XElement(pPr);
            var parts = ChangedProperties(original, now, ParagraphPropertyNames, W + "rPr", W + "sectPr");
            var description = "Paragraph formatted" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");

            // The paragraph as it was: its old properties, with the mark's run properties and any
            // section break, which pPrChange does not record, kept.
            var kept = pPr.Elements().Where(e => e.Name == W + "rPr" || e.Name == W + "sectPr").ToList();
            pPr.RemoveNodes();
            if (original is not null) pPr.Add(original.Elements());
            pPr.Add(kept);

            if (last is not null && lastEnd is not null && lastParagraph?.ElementsAfterSelf().FirstOrDefault() == p
                && last.Author == author && last.Text == description)
            {
                // Move the end bookmark (start + end element) to this paragraph's end.
                var endPair = new[] { (XElement)lastEnd.PreviousNode!, lastEnd };
                foreach (var m in endPair) m.Remove();
                p.Add(endPair);
                lastParagraph = p;
                continue;
            }

            var n = state.NextRevision++;
            last = state.Add(new DocxReviewItem
            {
                Kind = DocxItemKind.Formatting, Id = n.ToString(CultureInfo.InvariantCulture), Author = author,
                Date = date, Text = description,
                StartMark = state.Mark($"r{n}s"), EndMark = state.Mark($"r{n}e"),
            });
            AtStartOf(p, state.Bookmark(last.StartMark));
            var end = state.Bookmark(last.EndMark);
            p.Add(end);
            (lastParagraph, lastEnd) = (p, end[1]);
        }
    }

    /// <summary>
    /// A section-property revision (<c>w:sectPrChange</c>: page size, margins, columns…) puts the
    /// old properties back, keeping the header and footer references it does not record, and is
    /// noted at the start of the section.
    /// </summary>
    private static void RewriteSectionProperties(XElement root, State state)
    {
        var sections = root.Descendants(W + "sectPr").ToList();
        foreach (var change in root.Descendants(W + "sectPrChange").ToList())
        {
            var sectPr = change.Parent!;
            var original = change.Element(W + "sectPr");
            var author = NullIfEmpty((string?)change.Attribute(W + "author"));
            var date = ParseDate((string?)change.Attribute(W + "date"));
            change.Remove();
            var parts = ChangedProperties(original, new XElement(sectPr), SectionPropertyNames,
                W + "headerReference", W + "footerReference", W + "footnotePr", W + "endnotePr", W + "printerSettings");

            var kept = sectPr.Elements().Where(e => e.Name == W + "headerReference" || e.Name == W + "footerReference").ToList();
            sectPr.RemoveNodes();
            sectPr.Add(kept);
            if (original is not null) sectPr.Add(original.Elements());

            // A section runs from just after the previous section break to the paragraph holding
            // its own (or, for the last, to the end of the body).
            int k = sections.IndexOf(sectPr);
            var previousBreak = k > 0 ? sections[k - 1].Ancestors(W + "p").FirstOrDefault() : null;
            var first = previousBreak is not null
                ? previousBreak.ElementsAfterSelf().Select(e => e.Name == W + "p" ? e : FirstParagraph(e)).FirstOrDefault(p => p is not null)
                : root.Descendants(W + "body").FirstOrDefault() is { } body ? FirstParagraph(body) : null;
            if (first is null) continue;

            var n = state.NextRevision++;
            var item = state.Add(new DocxReviewItem
            {
                Kind = DocxItemKind.Formatting, Id = n.ToString(CultureInfo.InvariantCulture), Author = author, Date = date,
                Text = "Section formatted" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : ""),
                PointMark = state.Mark($"r{n}p"),
            });
            AtStartOf(first, state.Bookmark(item.PointMark));
        }
    }

    /// <summary>
    /// A table's property revisions (table, row, cell, grid) put the old properties back and are
    /// noted once per table and author, at the start of its first cell.
    /// </summary>
    private static void RewriteTableProperties(XElement root, State state)
    {
        var changes = TablePropertyChanges.SelectMany(n => root.Descendants(n)).ToList();
        // Word writes a grid change with no author: it joins the table's first authored change.
        string? AuthorOf(XElement c) => NullIfEmpty((string?)c.Attribute(W + "author"))
            ?? changes.Where(o => o.Ancestors(W + "tbl").First() == c.Ancestors(W + "tbl").First())
                .Select(o => NullIfEmpty((string?)o.Attribute(W + "author"))).FirstOrDefault(a => a is not null);
        foreach (var group in changes
                     .GroupBy(c => (Table: c.Ancestors(W + "tbl").First(), Author: AuthorOf(c)))
                     .ToList())
        {
            var what = new List<string>();
            DateTimeOffset? date = null;
            foreach (var change in group)
            {
                date ??= ParseDate((string?)change.Attribute(W + "date"));
                var props = change.Parent!;
                var original = change.Elements().FirstOrDefault();
                change.Remove();
                // Row and cell property elements also carry revision markers of their own: keep them.
                var kept = props.Elements().Where(e => RevisionContainers.Contains(e.Name)
                    || e.Name == W + "cellIns" || e.Name == W + "cellDel" || e.Name == W + "cellMerge").ToList();
                props.RemoveNodes();
                if (original is not null) props.Add(original.Elements());
                props.Add(kept);
                var name = change.Name.LocalName switch
                {
                    "tblPrChange" or "tblPrExChange" => "Table properties",
                    "trPrChange" => "Row properties",
                    "tcPrChange" => "Cell properties",
                    _ => "Column widths",
                };
                if (!what.Contains(name)) what.Add(name);
            }

            if (FirstParagraph(group.Key.Table) is not { } first) continue;
            var n = state.NextRevision++;
            var item = state.Add(new DocxReviewItem
            {
                Kind = DocxItemKind.Formatting, Id = n.ToString(CultureInfo.InvariantCulture), Author = group.Key.Author,
                Date = date, Text = "Table formatted: " + string.Join(", ", what),
                PointMark = state.Mark($"r{n}p"),
            });
            AtStartOf(first, state.Bookmark(item.PointMark));
        }
    }

    /// <summary>
    /// Items are collected pass by pass (comments, then formatting, then revisions); put this part's
    /// back in the order their first anchor appears in the text.
    /// </summary>
    private static void SortPartInDocumentOrder(XElement root, State state)
    {
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in root.Descendants(W + "bookmarkStart"))
            if ((string?)b.Attribute(W + "name") is { } name && name.StartsWith(state.Review.MarkPrefix, StringComparison.Ordinal))
                position.TryAdd(name, position.Count);

        int Key(DocxReviewItem i)
            => new[] { i.StartMark, i.PointMark }.Select(m => m is not null && position.TryGetValue(m, out var k) ? k : int.MaxValue).Min();

        int first = state.Items.FindIndex(i => i.Part == state.Part);
        if (first < 0) return;
        var part = state.Items.Skip(first).ToList();
        state.Items.RemoveRange(first, part.Count);
        state.Items.AddRange(part.OrderBy(Key));   // OrderBy is stable
    }

    private static void RewriteComments(XElement root, State state, Dictionary<string, DocxReviewItem> comments)
    {
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in root.Descendants().Where(e =>
                     e.Name == W + "commentRangeStart" || e.Name == W + "commentRangeEnd" || e.Name == W + "commentReference")
                     .ToList())
        {
            var id = (string?)el.Attribute(W + "id");
            if (id is null || !comments.TryGetValue(id, out var comment) || comment.ParentId != null)
            {
                RemoveMarker(el);
                continue;
            }

            if (placed.Add(id)) state.Add(comment);
            var safe = Sanitize(id);
            if (el.Name == W + "commentRangeStart")
                el.ReplaceWith(state.Bookmark(comment.StartMark = state.Mark($"c{safe}s")));
            else if (el.Name == W + "commentRangeEnd")
                el.ReplaceWith(state.Bookmark(comment.EndMark = state.Mark($"c{safe}e")));
            else
            {
                // The reference sits in its own run; the point is used only when there is no range.
                var run = el.Parent?.Name == W + "r" ? el.Parent : el;
                run.ReplaceWith(state.Bookmark(comment.PointMark = state.Mark($"c{safe}p")));
            }
        }
    }

    private static void RemoveMarker(XElement el)
    {
        if (el.Name == W + "commentReference" && el.Parent?.Name == W + "r") el.Parent.Remove();
        else el.Remove();
    }

    private static void RewriteFormatting(XElement root, State state)
    {
        DocxReviewItem? last = null;
        XElement? lastEnd = null;
        foreach (var change in root.Descendants(W + "rPrChange").ToList())
        {
            var rPr = change.Parent;
            var run = rPr?.Parent;
            var original = change.Element(W + "rPr");
            if (rPr is null || run is null || run.Name != W + "r")
            {
                change.Remove();   // a paragraph-mark format change: keep it as it is
                continue;
            }

            var description = DescribeFormatting(original, rPr, change);
            var author = NullIfEmpty((string?)change.Attribute(W + "author"));
            change.Remove();
            foreach (var el in rPr.Elements().ToList()) el.Remove();
            if (original is not null) rPr.Add(original.Elements());

            // Consecutive runs changed the same way by the same author read as one change.
            var previous = run.ElementsBeforeSelf().LastOrDefault();
            if (last is not null && lastEnd is not null && ReferenceEquals(previous, lastEnd)
                && last.Author == author && last.Text == description)
            {
                lastEnd.Remove();
                run.AddAfterSelf(lastEnd);
                continue;
            }

            var n = state.NextRevision++;
            last = state.Add(new DocxReviewItem
            {
                Kind = DocxItemKind.Formatting, Id = n.ToString(CultureInfo.InvariantCulture), Author = author,
                Date = ParseDate((string?)change.Attribute(W + "date")), Text = description,
                StartMark = state.Mark($"r{n}s"), EndMark = state.Mark($"r{n}e"),
            });
            run.AddBeforeSelf(state.Bookmark(last.StartMark));
            var end = state.Bookmark(last.EndMark);
            run.AddAfterSelf(end);
            lastEnd = end[1];
        }
    }

    private static void RewriteRevisions(XElement root, State state)
    {
        // Outermost revisions only: an ins wrapping a del (inserted, then deleted by someone else)
        // is wholly absent from the original, and goes with its ins.
        var revisions = root.Descendants()
            .Where(e => RevisionContainers.Contains(e.Name) && !(e.Parent is { } parent && MarkerParents.Contains(parent.Name))
                        && !e.Ancestors().Any(a => RevisionContainers.Contains(a.Name)))
            .ToList();

        // The last revision written, and the last node it left in the tree — a deletion and an
        // insertion by the same author with nothing but markers between them, in either order
        // (Word writes both), are one replacement.
        DocxReviewItem? pendingDeletion = null;
        XElement? pendingDeletionEl = null;
        DocxReviewItem? pendingInsertion = null;
        XElement? pendingInsertionEl = null;

        foreach (var el in revisions)
        {
            var author = NullIfEmpty((string?)el.Attribute(W + "author"));
            var date = ParseDate((string?)el.Attribute(W + "date"));
            var text = TextOf(el);
            bool inserted = el.Name == W + "ins" || el.Name == W + "moveTo";

            if (inserted)
            {
                // A deletion directly followed by an insertion by the same hand is a replacement.
                if (el.Name == W + "ins" && pendingDeletion is { Kind: DocxItemKind.Deletion } del
                    && pendingDeletionEl is not null && del.Author == author
                    && AdjacentAfter(pendingDeletionEl, el))
                {
                    state.Items[state.Items.IndexOf(del)] = new DocxReviewItem
                    {
                        Kind = DocxItemKind.Replacement, Id = del.Id, Author = del.Author, Date = del.Date,
                        Text = text, StartMark = del.StartMark, EndMark = del.EndMark, Part = del.Part,
                    };
                    el.ReplaceWith(KeptMarkers(el));
                    pendingDeletion = null;
                    pendingInsertion = null;
                    continue;
                }

                var n = state.NextRevision++;
                var item = state.Add(new DocxReviewItem
                {
                    Kind = el.Name == W + "moveTo" ? DocxItemKind.MoveTo : DocxItemKind.Insertion,
                    Id = n.ToString(CultureInfo.InvariantCulture), Author = author, Date = date, Text = text,
                    PointMark = state.Mark($"r{n}p"),
                });
                var point = state.Bookmark(item.PointMark);
                el.ReplaceWith(point.Concat<object>(KeptMarkers(el)));
                pendingDeletion = null;
                (pendingInsertion, pendingInsertionEl) = (item, point[1]);
            }
            else
            {
                bool replaces = el.Name == W + "del" && pendingInsertion is { Kind: DocxItemKind.Insertion } ins
                    && pendingInsertionEl is not null && ins.Author == author && AdjacentAfter(pendingInsertionEl, el);
                if (replaces) state.Items.Remove(pendingInsertion!);

                var n = state.NextRevision++;
                var item = state.Add(new DocxReviewItem
                {
                    Kind = replaces ? DocxItemKind.Replacement
                        : el.Name == W + "moveFrom" ? DocxItemKind.MoveFrom : DocxItemKind.Deletion,
                    Id = n.ToString(CultureInfo.InvariantCulture), Author = author, Date = date,
                    Text = replaces ? pendingInsertion!.Text : text,
                    StartMark = state.Mark($"r{n}s"), EndMark = state.Mark($"r{n}e"),
                });
                pendingInsertion = null;
                foreach (var t in el.Descendants(W + "delText").ToList()) t.Name = W + "t";
                foreach (var t in el.Descendants(W + "delInstrText").ToList()) t.Name = W + "instrText";
                var endMark = state.Bookmark(item.EndMark);
                var content = new List<object>();
                content.AddRange(state.Bookmark(item.StartMark));
                content.AddRange(el.Nodes());
                content.AddRange(endMark);
                el.ReplaceWith(content);
                pendingDeletion = item;
                pendingDeletionEl = endMark[1];
            }
        }
    }

    /// <summary>Whether <paramref name="later"/> follows <paramref name="earlier"/> with only markers between.</summary>
    private static bool AdjacentAfter(XElement earlier, XElement later)
    {
        if (earlier.Parent != later.Parent) return false;
        foreach (var node in earlier.ElementsAfterSelf())
        {
            if (ReferenceEquals(node, later)) return true;
            if (!Markers.Contains(node.Name)) return false;
        }
        return false;
    }

    /// <summary>Bookmarks inside removed content are hoisted out so anchors (ours and the document's) survive.</summary>
    private static List<XElement> KeptMarkers(XElement removed)
        => removed.Descendants().Where(e => e.Name == W + "bookmarkStart" || e.Name == W + "bookmarkEnd").ToList();

    /// <summary>
    /// A paragraph whose mark was inserted and whose text was all inserted disappears in the
    /// original. Its bookmarks move to the start of the next paragraph so the insertion point stays.
    /// </summary>
    private static void RemoveInsertedParagraphs(XElement root)
    {
        foreach (var p in root.Descendants(W + "p").ToList())
        {
            if (p.Element(W + "pPr")?.Element(W + "rPr")?.Element(W + "ins") is null) continue;
            bool hasContent = p.Descendants().Any(e =>
                (e.Name == W + "t" && e.Value.Length > 0) || e.Name == W + "drawing" || e.Name == W + "pict"
                || e.Name == W + "object" || e.Name == W + "tab" || e.Name == W + "fldSimple");
            if (hasContent) continue;
            var next = p.ElementsAfterSelf(W + "p").FirstOrDefault();
            if (next is null) continue;   // the last paragraph of a cell or body must stay
            var marks = p.Elements().Where(e => e.Name == W + "bookmarkStart" || e.Name == W + "bookmarkEnd").ToList();
            foreach (var m in marks) m.Remove();
            var anchor = next.Element(W + "pPr");
            if (anchor is not null) anchor.AddAfterSelf(marks);
            else next.AddFirst(marks);
            p.Remove();
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Smallest top and bottom page margin over every section, in points (twips / 20).</summary>
    internal static (float Top, float Bottom)? ReadBodyMargins(XDocument document)
    {
        float? top = null, bottom = null;
        foreach (var mar in document.Descendants(W + "sectPr").Select(s => s.Element(W + "pgMar")).OfType<XElement>())
        {
            // A negative top/bottom margin is "exactly this, even over the header" — the distance still applies.
            if (int.TryParse((string?)mar.Attribute(W + "top"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                top = MathF.Min(top ?? float.MaxValue, Math.Abs(t) / 20f);
            if (int.TryParse((string?)mar.Attribute(W + "bottom"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                bottom = MathF.Min(bottom ?? float.MaxValue, Math.Abs(b) / 20f);
        }
        return top is { } tt && bottom is { } bb ? (tt, bb) : null;
    }

    internal static string TextOf(XElement el)
    {
        var sb = new StringBuilder();
        foreach (var e in el.DescendantsAndSelf())
        {
            if (e.Name == W + "t" || e.Name == W + "delText") sb.Append(e.Value);
            else if (e.Name == W + "tab") sb.Append('\t');
            else if (e.Name == W + "br" || e.Name == W + "cr") sb.Append('\n');
            else if (e.Name == W + "p" && !ReferenceEquals(e, el) && sb.Length > 0) sb.Append('\n');
            else if (e.Name == W + "noBreakHyphen") sb.Append('‑');
            // A picture has no text to quote; a text box inside a drawing has, and is read as text.
            else if ((e.Name == W + "drawing" || e.Name == W + "pict") && !e.Descendants(W + "t").Any()) sb.Append("[image]");
            else if (e.Name == W + "object") sb.Append("[object]");
        }
        return sb.ToString();
    }

    private static readonly (string Tag, string Name)[] FormatNames =
    [
        ("b", "Bold"), ("i", "Italic"), ("u", "Underline"), ("strike", "Strikethrough"),
        ("dstrike", "Double strikethrough"), ("caps", "All caps"), ("smallCaps", "Small caps"),
        ("vertAlign", "Superscript/subscript"), ("color", "Font colour"), ("sz", "Font size"),
        ("rFonts", "Font"), ("highlight", "Highlight"), ("shd", "Shading"), ("rStyle", "Character style"),
    ];

    /// <summary>"Formatted: Bold, not Italic, Font size" — Word's balloon wording, roughly.</summary>
    internal static string DescribeFormatting(XElement? original, XElement current, XElement change)
    {
        static bool On(XElement? rPr, string tag)
        {
            var e = rPr?.Element(W + tag);
            if (e is null) return false;
            var val = (string?)e.Attribute(W + "val");
            return val is null || (val != "0" && val != "false" && val != "none");
        }
        static string? Val(XElement? rPr, string tag)
            => rPr?.Element(W + tag) is { } e ? e.ToString(SaveOptions.DisableFormatting) : null;

        var now = WithoutChange(current, change);
        var parts = new List<string>();
        foreach (var (tag, name) in FormatNames)
        {
            if (tag is "b" or "i" or "u" or "strike" or "dstrike" or "caps" or "smallCaps")
            {
                bool before = On(original, tag), after = On(now, tag);
                if (before != after) parts.Add(after ? name : "not " + name);
            }
            else if (Val(original, tag) != Val(now, tag))
                parts.Add(name);
        }
        return "Formatted" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
    }

    private static XElement WithoutChange(XElement rPr, XElement change)
        => new(rPr.Name, rPr.Elements().Where(e => !ReferenceEquals(e, change)));

    private static DateTimeOffset? ParseDate(string? value)
    {
        // Word writes local time with a 'Z' — there is no way to recover the zone, so take it as written.
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Bookmark names must be letters and digits: LibreOffice escapes anything else (<c>_</c> → <c>5F</c>).</summary>
    private static string Sanitize(string id) => new(id.Where(char.IsAsciiLetterOrDigit).ToArray());

    private static string RandomTag()
    {
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        return new string(Enumerable.Range(0, 5).Select(_ => letters[Random.Shared.Next(letters.Length)]).ToArray());
    }
}
