using System.Globalization;
using DssHarness.Core.Output;

namespace DssHarness.Tests;

/// <summary>
/// A one-line message names every item of a list that fits its room, in order, and says how many more there are and where
/// the whole list is - never a few named and the rest dropped.
/// </summary>
public sealed class NamesTests
{
    /// <summary>Where the whole list is, as the tests' lines say it.</summary>
    private const string Whole = "'runs/held.json'";

    /// <summary>
    /// A thousand paths of nine characters fill the line's room of 4,096 with 372 of them - nine for the first and eleven
    /// for each after it, a separator with each, 4,090 in all - and the line counts the other 628 and says where all of
    /// them are.
    /// </summary>
    [Fact]
    public void EveryItemThatFits_IsNamed_AndTheRestSaysWhereTheWholeIs()
    {
        var paths = Enumerable.Range(0, 1000).Select(index => string.Create(CultureInfo.InvariantCulture, $"path-{index:0000}")).ToList();

        Assert.Equal($"{string.Join(", ", paths.Take(372))} and 628 more (all in {Whole})", Names.Line(paths, Whole));
    }

    /// <summary>A list that fits is named whole, however many it holds, with no word of where it is kept.</summary>
    [Fact]
    public void AListThatFits_IsNamedWhole_WithNoWordOfWhereItIs()
        => Assert.Equal("a, b, c, d, e", Names.Line(["a", "b", "c", "d", "e"], Whole, room: 100));

    /// <summary>A name fits where it ends on the room's last character, and the next one does not where it would end past it.</summary>
    [Fact]
    public void AName_FitsWhereItEndsOnTheRoomsLastCharacter()
    {
        Assert.Equal($"alpha, beta and 1 more (all in {Whole})", Names.Line(["alpha", "beta", "gamma"], Whole, room: 11));
        Assert.Equal($"alpha and 2 more (all in {Whole})", Names.Line(["alpha", "beta", "gamma"], Whole, room: 10));
    }

    /// <summary>The first item is named whole however wide it is, so a line always names one and never cuts a name.</summary>
    [Fact]
    public void TheFirstItem_IsNamedWholeHoweverWide()
    {
        var wide = new string('x', 50);

        Assert.Equal($"{wide} and 1 more (all in {Whole})", Names.Line([wide, "b"], Whole, room: 10));
        Assert.Equal(wide, Names.Line([wide], Whole, room: 10));
    }

    /// <summary>A list of one is that one, and a list of none is no text.</summary>
    [Fact]
    public void OneItem_IsItself_AndNone_IsNoText()
    {
        Assert.Equal("a", Names.Line(["a"], Whole));
        Assert.Equal(string.Empty, Names.Line([], Whole));
    }

    /// <summary>
    /// An item that holds a control character is named quoted and escaped, as every line names one, and is weighed against
    /// the room as it is written: <c>"a\nb"</c> is six characters, not three.
    /// </summary>
    [Fact]
    public void AnItemHoldingAControlCharacter_IsQuoted_AndWeighedAsQuoted()
    {
        Assert.Equal("\"a\\nb\", c", Names.Line(["a\nb", "c"], Whole, room: 9));
        Assert.Equal($"\"a\\nb\" and 1 more (all in {Whole})", Names.Line(["a\nb", "c"], Whole, room: 8));
    }

    /// <summary>
    /// Where the whole list is must always be said, since no caller knows beforehand what will fit, and a room is never
    /// less than nothing.
    /// </summary>
    [Fact]
    public void WhereTheWholeIs_IsAlwaysGiven()
    {
        Assert.Throws<ArgumentNullException>("whole", () => Names.Line(["a"], null!));
        Assert.Throws<ArgumentException>("whole", () => Names.Line(["a"], string.Empty));
        Assert.Throws<ArgumentException>("whole", () => Names.Line(["a"], " "));
        Assert.Throws<ArgumentOutOfRangeException>("room", () => Names.Line(["a"], Whole, room: -1));
    }
}
