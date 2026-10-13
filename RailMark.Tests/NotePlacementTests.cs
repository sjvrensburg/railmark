using RailMark.Services;
using RailReader.Core.Models;

namespace RailMark.Tests;

public class NotePlacementTests
{
    private static readonly (double Width, double Height) Letter = (612, 792);

    /// <summary>A line of 10pt-wide, 10pt-tall glyphs from <paramref name="left"/> to <paramref name="right"/> at <paramref name="top"/>.</summary>
    private static IEnumerable<CharBox> Line(float left, float right, float top, int firstIndex = 0)
    {
        int i = firstIndex;
        for (float x = left; x + 10f <= right + 0.01f; x += 10f)
            yield return new CharBox(i++, x, top, x + 10f, top + 10f, 0);
    }

    private static List<CharBox> Column(float left, float right, float top, int lines)
        => Enumerable.Range(0, lines).SelectMany(l => Line(left, right, top + l * 12f, l * 1000)).ToList();

    [Fact]
    public void Ideal_Centres_The_Icon_On_The_Line_Beside_The_Column()
    {
        var chars = Column(72, 452, 100, 9);
        var anchor = new RectF(200, 148, 260, 158); // the fifth line, a short quote within it

        var spot = NotePlacement.Ideal(anchor, chars, Letter);

        Assert.Equal(452f + NotePlacement.Gap, spot.X);
        Assert.Equal(153f - NotePlacement.IconSize / 2, spot.Y);
    }

    [Fact]
    public void A_Ragged_Last_Line_Still_Uses_The_Column_Edge()
    {
        // The anchor is a short last line; the icon must not sit right after it, over the
        // paragraph below.
        var chars = Column(72, 452, 100, 4).Concat(Line(72, 150, 148)).Concat(Column(72, 452, 160, 4)).ToList();
        var anchor = new RectF(72, 148, 150, 158);

        var spot = NotePlacement.Ideal(anchor, chars, Letter);

        Assert.Equal(452f + NotePlacement.Gap, spot.X);
    }

    [Fact]
    public void Two_Columns_Are_Told_Apart_And_The_Nearer_Margin_Wins()
    {
        var left = Column(72, 292, 100, 9);
        var right = Column(320, 540, 100, 9);
        var chars = left.Concat(right).ToList();

        // Left column, 28pt gutter: the icon plus its clearance (32pt) doesn't fit, so the right
        // side is pushed past the right column; the left margin is nearer.
        var inLeft = NotePlacement.Ideal(new RectF(100, 148, 200, 158), chars, Letter);
        Assert.Equal(72f - NotePlacement.Gap - NotePlacement.IconSize, inLeft.X);

        var inRight = NotePlacement.Ideal(new RectF(400, 148, 500, 158), chars, Letter);
        Assert.Equal(540f + NotePlacement.Gap, inRight.X);
    }

    [Fact]
    public void Without_Room_In_Either_Margin_The_Icon_Stays_On_The_Page()
    {
        var chars = Column(10, 602, 100, 9);

        var spot = NotePlacement.Ideal(new RectF(100, 148, 200, 158), chars, Letter);

        Assert.Equal(612f - NotePlacement.IconSize - NotePlacement.EdgeInset, spot.X);
    }

    [Fact]
    public void With_No_Characters_The_Anchor_Itself_Is_The_Column()
    {
        var spot = NotePlacement.Ideal(new RectF(100, 148, 200, 158), [], Letter);
        Assert.Equal(200f + NotePlacement.Gap, spot.X);
    }

    [Fact]
    public void Beside_Skips_Slots_Other_Icons_Hold_Then_Goes_Above()
    {
        var anchor = new RectF(100, 780, 200, 790); // at the foot of the page
        var ideal = NotePlacement.Ideal(anchor, [], Letter);
        Assert.Equal(792f - NotePlacement.IconSize - NotePlacement.EdgeInset, ideal.Y); // clamped: no room below

        var taken = new List<RectF> { new(ideal.X, ideal.Y, ideal.X + 24, ideal.Y + 24) };
        var spot = NotePlacement.Beside(anchor, [], Letter, taken);

        Assert.Equal(ideal.X, spot.X);
        Assert.Equal(ideal.Y - NotePlacement.Step, spot.Y);
    }

    [Fact]
    public void A_Blocked_Note_Snaps_To_Just_Below_The_Blocking_Icon()
    {
        // An icon 10pt below the ideal spot: the note goes under that icon, not a whole step down
        // from where it aimed, so stacks stay tight whatever the quotes' line spacing.
        var anchor = new RectF(100, 148, 200, 158);
        var ideal = NotePlacement.Ideal(anchor, [], Letter);
        var taken = new List<RectF> { new(ideal.X, ideal.Y + 10, ideal.X + 24, ideal.Y + 34) };

        var spot = NotePlacement.Beside(anchor, [], Letter, taken);

        Assert.Equal(ideal.Y + 34 + NotePlacement.Gap, spot.Y);
    }

    [Fact]
    public void With_Every_Spot_Within_Reach_Taken_The_Ideal_Spot_Is_Used_Anyway()
    {
        var anchor = new RectF(100, 148, 200, 158);
        var ideal = NotePlacement.Ideal(anchor, [], Letter);
        var taken = Enumerable.Range(-7, 15)
            .Select(k => ideal.Y + k * NotePlacement.Step)
            .Select(y => new RectF(ideal.X, y, ideal.X + 24, y + 24))
            .ToList();

        var spot = NotePlacement.Beside(anchor, [], Letter, taken);

        Assert.Equal(ideal, spot);
    }

    [Fact]
    public void A_Moved_Icon_Is_Still_Within_MaxDrift_Of_Its_Ideal()
    {
        var anchor = new RectF(100, 148, 200, 158);
        var ideal = NotePlacement.Ideal(anchor, [], Letter);
        var taken = Enumerable.Range(0, 6)
            .Select(k => ideal.Y + k * NotePlacement.Step)
            .Select(y => new RectF(ideal.X, y, ideal.X + 24, y + 24))
            .ToList();

        var spot = NotePlacement.Beside(anchor, [], Letter, taken);

        Assert.NotEqual(ideal, spot);
        Assert.True(MathF.Abs(spot.Y - ideal.Y) <= NotePlacement.MaxDrift);
    }

    [Fact]
    public void UnderThread_Is_Just_Beneath_The_Lowest_Icon_Of_The_Thread()
    {
        var parent = new RectF(456, 100, 480, 124);
        var reply = new RectF(456, 128, 480, 152);

        Assert.Equal(new NoteSpot(456, 100 + NotePlacement.Step), NotePlacement.UnderThread([parent], Letter));
        Assert.Equal(new NoteSpot(456, 100 + 2 * NotePlacement.Step), NotePlacement.UnderThread([parent, reply], Letter));
    }

    [Fact]
    public void Insert_Pushes_Overlapping_Icons_Down_In_A_Cascade()
    {
        var a = new TextNoteAnnotation { X = 456, Y = 128 };
        var b = new TextNoteAnnotation { X = 456, Y = 156 };
        var c = new TextNoteAnnotation { X = 456, Y = 300 }; // far enough down not to be reached
        var other = new TextNoteAnnotation { X = 40, Y = 128 }; // another column

        NotePlacement.Insert(new NoteSpot(456, 128), [c, b, a, other], Letter);

        Assert.Equal(156f, a.Y);
        Assert.Equal(184f, b.Y);
        Assert.Equal(300f, c.Y);
        Assert.Equal(128f, other.Y);
    }

    [Fact]
    public void Insert_Leaves_An_Icon_That_Merely_Touches_Alone()
    {
        var below = new TextNoteAnnotation { X = 456, Y = 128 };
        NotePlacement.Insert(new NoteSpot(456, 100), [below], Letter); // 100 + 24 + 4 = 128: touching, not overlapping
        Assert.Equal(128f, below.Y);
    }
}
