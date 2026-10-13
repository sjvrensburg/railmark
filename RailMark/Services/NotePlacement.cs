using RailReader.Core.Models;

namespace RailMark.Services;

/// <summary>Where a sticky-note icon's top-left corner goes, in top-down page points.</summary>
internal readonly record struct NoteSpot(float X, float Y);

/// <summary>
/// Chooses where a sticky note sits on a page. A note is a fixed-size icon, so it has to be put
/// somewhere rather than drawn over its text: beside the text column its quote is in (the gutter
/// or the nearest margin), vertically centred on the quoted line, and away from any note that
/// already sits there, so a stack of notes on one line reads top to bottom instead of piling up
/// on one spot. A reply instead takes the spot directly under its thread and pushes whatever is
/// there down, so a thread stays contiguous.
/// </summary>
internal static class NotePlacement
{
    /// <summary>
    /// The icon PDFium draws for a text annotation: a 24pt square whose top-left corner is the
    /// note's X/Y (measured: a note at (500, 100) on a 792pt page is written as
    /// <c>/Rect [500 692 524 668]</c>).
    /// </summary>
    public const float IconSize = 24f;

    /// <summary>Clearance between the icon and the text it sits beside, and between stacked icons.</summary>
    public const float Gap = 4f;

    /// <summary>How close to the page edge an icon may go.</summary>
    public const float EdgeInset = 2f;

    /// <summary>Vertical distance between the tops of two stacked icons.</summary>
    public const float Step = IconSize + Gap;

    /// <summary>
    /// How far a note may be moved down (or up) from its ideal spot to avoid the notes already
    /// there — and so how far a note placed earlier may sit from where a fresh placement of the
    /// same note would ideally go. Six icons' worth.
    /// </summary>
    public const float MaxDrift = 6 * Step;

    /// <summary>Lines above and below the anchor that count when working out which column it is in.</summary>
    private const float ColumnWindowLines = 4f;

    /// <summary>A horizontal gap wider than this many line heights separates two columns.</summary>
    private const float ColumnGapFactor = 1.5f;

    /// <summary>
    /// Where a note on the line <paramref name="anchor"/> ideally goes, before avoiding other
    /// notes: centred on the line, just outside the text column the line belongs to, on whichever
    /// side is nearer (the right, if both are). This is the note's identity for dedupe: the same
    /// quote always yields the same ideal spot, however the page's other notes move the final one.
    /// </summary>
    public static NoteSpot Ideal(RectF anchor, IReadOnlyList<CharBox> chars, (double Width, double Height) page)
    {
        var y = ClampY(CentreOn(anchor), page);
        var column = ColumnAround(anchor, chars);
        return new NoteSpot(BesideColumn(column, y, chars, page), y);
    }

    /// <summary>
    /// The final spot for a note on <paramref name="anchor"/>: the ideal one unless an icon in
    /// <paramref name="occupied"/> is already there, in which case the nearest free spot below it
    /// (then above it, when the page or the drift allowance runs out). With no free spot within
    /// reach, the ideal spot is used regardless.
    /// </summary>
    public static NoteSpot Beside(
        RectF anchor, IReadOnlyList<CharBox> chars, (double Width, double Height) page, IReadOnlyList<RectF> occupied)
    {
        var column = ColumnAround(anchor, chars);
        return FirstFree(CentreOn(anchor), y => BesideColumn(column, y, chars, page), page, occupied);
    }

    /// <summary>
    /// The spot for a note that joins a thread whose icons are <paramref name="thread"/>: directly
    /// beneath the lowest of them, in the same column of icons, so the thread reads top to bottom.
    /// </summary>
    public static NoteSpot UnderThread(IReadOnlyList<RectF> thread, (double Width, double Height) page)
        => new(thread[0].Left, ClampY(thread.Max(t => t.Bottom) + Gap, page));

    /// <summary>
    /// Makes room for an icon at <paramref name="spot"/>: every note it would overlap is pushed
    /// down to just beneath it, and whatever those then overlap is pushed in turn, so a note
    /// inserted into a column of icons shoves the rest of the column down rather than landing
    /// on top of it. Notes in other columns are not touched.
    /// </summary>
    public static void Insert(NoteSpot spot, IEnumerable<TextNoteAnnotation> notes, (double Width, double Height) page)
    {
        var pushers = new List<RectF> { IconRect(spot) };
        foreach (var note in notes.OrderBy(n => n.Y).ThenBy(n => n.X))
        {
            var blocking = pushers.Where(p => Overlaps(p, new NoteSpot(note.X, note.Y))).ToList();
            if (blocking.Count == 0) continue;
            note.Y = ClampY(blocking.Max(p => p.Bottom) + Gap, page);
            pushers.Add(IconRect(new NoteSpot(note.X, note.Y)));
        }
    }

    /// <summary>The square an icon at <paramref name="spot"/> covers.</summary>
    public static RectF IconRect(NoteSpot spot) => new(spot.X, spot.Y, spot.X + IconSize, spot.Y + IconSize);

    private static NoteSpot FirstFree(
        float idealY, Func<float, float> xAt, (double Width, double Height) page, IReadOnlyList<RectF> occupied)
    {
        idealY = ClampY(idealY, page);
        float lowest = (float)page.Height - IconSize - EdgeInset;

        // Walk down past whatever blocks each spot, then up; the walk may not leave the page or
        // go further than a later run would look for the note.
        for (float y = idealY; y <= lowest && y - idealY <= MaxDrift;)
        {
            var spot = new NoteSpot(xAt(y), y);
            var blockers = occupied.Where(o => Overlaps(o, spot)).ToList();
            if (blockers.Count == 0) return spot;
            y = blockers.Max(o => o.Bottom) + Gap;
        }
        for (float y = idealY; y >= EdgeInset && idealY - y <= MaxDrift;)
        {
            var spot = new NoteSpot(xAt(y), y);
            var blockers = occupied.Where(o => Overlaps(o, spot)).ToList();
            if (blockers.Count == 0) return spot;
            y = blockers.Min(o => o.Top) - Gap - IconSize;
        }
        return new NoteSpot(xAt(idealY), idealY);
    }

    private static bool Overlaps(RectF icon, NoteSpot spot)
        => icon.Left < spot.X + IconSize && icon.Right > spot.X
           && icon.Top < spot.Y + IconSize && icon.Bottom > spot.Y;

    /// <summary>The y that centres an icon on the anchor's line.</summary>
    private static float CentreOn(RectF anchor) => (anchor.Top + anchor.Bottom) / 2f - IconSize / 2f;

    private static float ClampY(float y, (double Width, double Height) page)
        => Math.Clamp(y, EdgeInset, MathF.Max(EdgeInset, (float)page.Height - IconSize - EdgeInset));

    private static float ClampX(float x, (double Width, double Height) page)
        => Math.Clamp(x, EdgeInset, MathF.Max(EdgeInset, (float)page.Width - IconSize - EdgeInset));

    /// <summary>
    /// The x of an icon at <paramref name="y"/> beside <paramref name="column"/>: the nearer of the
    /// first text-free spot to its right and to its left, the right winning a tie. When neither
    /// side has room — margins narrower than the icon — the icon sits at the page's right edge
    /// over whatever is there.
    /// </summary>
    private static float BesideColumn(RectF column, float y, IReadOnlyList<CharBox> chars, (double Width, double Height) page)
    {
        var right = FreeRightOf(column.Right + Gap, y, chars, page);
        var left = FreeLeftOf(column.Left - Gap - IconSize, y, chars, page);
        if (right is null && left is null)
            return ClampX(column.Right + Gap, page);
        if (right is null) return left!.Value;
        if (left is null) return right.Value;
        // The left side has to be clearly nearer: with equal margins the two distances differ only
        // by glyph-box noise, and the side must not flip from one line to the next.
        float leftDistance = column.Left - (left.Value + IconSize);
        float rightDistance = right.Value - column.Right;
        return leftDistance + SidePreference < rightDistance ? left.Value : right.Value;
    }

    /// <summary>How much nearer the left side must be than the right before a note goes there.</summary>
    private const float SidePreference = 1f;

    /// <summary>
    /// The first x at or right of <paramref name="start"/> where an icon at <paramref name="y"/>
    /// covers no text; null when it would leave the page.
    /// </summary>
    private static float? FreeRightOf(float start, float y, IReadOnlyList<CharBox> chars, (double Width, double Height) page)
    {
        var x = start;
        while (true)
        {
            if (x + IconSize + EdgeInset > page.Width) return null;
            var blocking = InBand(chars, y).Where(c => c.Left < x + IconSize + Gap && c.Right > x - Gap).ToList();
            if (blocking.Count == 0) return x;
            x = blocking.Max(c => c.Right) + Gap;
        }
    }

    private static float? FreeLeftOf(float start, float y, IReadOnlyList<CharBox> chars, (double Width, double Height) page)
    {
        var x = start;
        while (true)
        {
            if (x < EdgeInset) return null;
            var blocking = InBand(chars, y).Where(c => c.Left < x + IconSize + Gap && c.Right > x - Gap).ToList();
            if (blocking.Count == 0) return x;
            x = blocking.Min(c => c.Left) - Gap - IconSize;
        }
    }

    /// <summary>The characters an icon at <paramref name="y"/> would share a horizontal band with.</summary>
    private static IEnumerable<CharBox> InBand(IReadOnlyList<CharBox> chars, float y)
        => chars.Where(c => c.Top < y + IconSize && c.Bottom > y);

    /// <summary>
    /// The horizontal extent of the text column <paramref name="anchor"/> lies in: the characters
    /// within a few lines of it, split into columns at gaps wider than a word space, taking the
    /// group the anchor overlaps. With no characters to go on, the anchor's own extent stands in,
    /// so the note still lands beside its quote.
    /// </summary>
    internal static RectF ColumnAround(RectF anchor, IReadOnlyList<CharBox> chars)
    {
        float lineHeight = MathF.Max(anchor.Bottom - anchor.Top, 1f);
        float windowTop = anchor.Top - ColumnWindowLines * lineHeight;
        float windowBottom = anchor.Bottom + ColumnWindowLines * lineHeight;
        var nearby = chars
            .Where(c => c.Right > c.Left && c.Bottom > c.Top && c.Top < windowBottom && c.Bottom > windowTop)
            .OrderBy(c => c.Left)
            .ToList();
        if (nearby.Count == 0)
            return anchor;

        float gap = ColumnGapFactor * lineHeight;
        var groups = new List<(float Left, float Right)>();
        float left = nearby[0].Left, right = nearby[0].Right;
        foreach (var c in nearby.Skip(1))
        {
            if (c.Left - right > gap)
            {
                groups.Add((left, right));
                (left, right) = (c.Left, c.Right);
            }
            else
            {
                right = MathF.Max(right, c.Right);
            }
        }
        groups.Add((left, right));

        // The anchor's own column is the group it overlaps; a quote whose geometry sits in no
        // group (odd rotated or zero-width glyphs) takes the column nearest to it.
        var column = groups.FirstOrDefault(g => g.Left < anchor.Right && g.Right > anchor.Left, default);
        if (column == default)
            column = groups.MinBy(g => MathF.Min(MathF.Abs(g.Left - anchor.Right), MathF.Abs(g.Right - anchor.Left)));
        return new RectF(MathF.Min(column.Left, anchor.Left), anchor.Top, MathF.Max(column.Right, anchor.Right), anchor.Bottom);
    }
}
