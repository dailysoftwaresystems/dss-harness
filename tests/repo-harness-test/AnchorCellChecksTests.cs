using RepoHarness.Core.Anchors;

namespace RepoHarness.Tests;

/// <summary>
/// What a cell would store broken, what it cites, and what replacing it loses: the checks every anchor write is held to,
/// each on the text a consumer's own checks were measured against.
/// </summary>
public sealed class AnchorCellChecksTests
{
    private static readonly AnchorIdRules Rules = new("D", 3);

    /// <summary>
    /// A cell cites each whole token spelt as a new id is, and nothing else: never a family or pattern of ids, a token too
    /// short to be a new id, nor the tail of a longer word.
    /// </summary>
    [Theory]
    [InlineData("see D-AREA-TOPIC-ONE and [[D-AREA-TOPIC-TWO]], then `D-AREA-TOPIC-ONE`.", "D-AREA-TOPIC-ONE D-AREA-TOPIC-TWO")]
    [InlineData("the D-AREA-TOPIC-SUB-* family, D-AREA-TOPIC-SUB* and D-AREA-TOPIC-SUB{1,2}, and D-AREA-TOPIC-ONE- cut", "")]
    [InlineData("tier D-3, and D-AREA-X, name no row", "")]
    [InlineData("XD-AREA-TOPIC-ONE and -D-AREA-TOPIC-TWO", "")]
    public void CitedIds_AreWholeIdsAsLongAsANewOne_NeverAFamilyOrAFragment(string text, string cited)
    {
        Assert.Equal(cited.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal), Rules.CitedIds(text).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A value the door would store cut is refused, naming the cut: an id broken after a hyphen - wrapped there, or already
    /// joined with a space - an id broken inside a segment, and a path broken after a '/', wrapped there or stored with a
    /// space where it starts at a directory the tree holds.
    /// </summary>
    [Theory]
    [InlineData("cites D-PROBE-EXISTING-\nROW-ONE across a line break after a hyphen", "an anchor id broken after a hyphen ('D-PROBE-EXISTING- R'")]
    [InlineData("cites D-PROBE-EXISTING- ROW-ONE, joined already", "an anchor id broken after a hyphen")]
    [InlineData("cites D-PROBE-EXIS\r\nTING-ROW-ONE", @"an anchor id broken across a line ('D-PROBE-EXIS\nTING-R')")]
    [InlineData("see tests/hir/\ntest_x.cpp for the case", @"a path broken across a line after a '/' ('tests/hir/\ntest_x.cpp')")]
    [InlineData("see tests/hir/ test_x.cpp for the case", "a path with a space after a '/' ('tests/hir/ test_x.cpp', as it would be stored)")]
    public void AValueTheDoorWouldStoreCut_IsRefused_NamingTheCut(string text, string refusal)
    {
        var problems = new AnchorCellCuts(Rules, ["tests", "src"]).Of(text, "Trigger");

        Assert.Contains(problems, problem => problem.StartsWith("The Trigger holds ", StringComparison.Ordinal) && problem.Contains(refusal, StringComparison.Ordinal));
    }

    /// <summary>
    /// Prose that only looks cut is left alone: a directory followed by a word that is no file's name, an id and a figure on
    /// one line, a prefix in backticks, and a path with a space after it that starts at no directory the tree holds.
    /// </summary>
    [Theory]
    [InlineData("touches no src/hir/ or src/mir/ at all")]
    [InlineData("D-AREA-TOPIC-ONE MF-4 was measured on one line")]
    [InlineData("ids open with the `D-` prefix")]
    [InlineData("vendor/hir/ test_x.cpp is outside the tree")]
    [InlineData("one and/\nor the other")]
    public void ProseThatOnlyLooksCut_IsLeftAlone(string text)
    {
        Assert.Empty(new AnchorCellCuts(Rules, ["tests", "src"]).Of(text, "Trigger"));
    }

    /// <summary>
    /// What writing a cell does to its stored text: nothing, a change of spacing only, an addendum that keeps it word for
    /// word, a fill of an empty cell - or a loss.
    /// </summary>
    [Theory]
    [InlineData("the fix", "the fix", AnchorCellFate.Same)]
    [InlineData("the  fix", "the fix", AnchorCellFate.Respaced)]
    [InlineData("the fix", "the fix and the test that proves it", AnchorCellFate.Kept)]
    [InlineData("", "the fix", AnchorCellFate.Filled)]
    [InlineData("the fix", "a better fix", AnchorCellFate.Lost)]
    [InlineData("see tests/hir/ test_x.cpp", "see tests/hir/test_x.cpp", AnchorCellFate.Lost)]
    public void Fate_SaysWhatWritingACellDoesToItsStoredText(string stored, string written, AnchorCellFate fate)
    {
        Assert.Equal(fate, AnchorCellComparison.Fate(stored, written));
    }

    /// <summary>
    /// A word diff names what a cell loses and what replaces it, with a few unchanged words either side of each change and
    /// the rest left out.
    /// </summary>
    [Theory]
    [InlineData(
        "one two three four five six seven eight nine ten",
        "one two three four five SIX seven eight nine ten",
        "… two three four five [-six-] {+SIX+} seven eight nine ten")]
    [InlineData(
        "see tests/hir/ test_x.cpp for the case",
        "see tests/hir/test_x.cpp for the case",
        "see [-tests/hir/ test_x.cpp-] {+tests/hir/test_x.cpp+} for the case")]
    [InlineData("the fix", "a better fix", "[-the-] {+a better+} fix")]
    public void WordDiff_NamesWhatIsLost_AndWhatReplacesIt(string stored, string written, string diff)
    {
        Assert.Equal(diff, AnchorCellComparison.WordDiff(stored, written));
    }

    /// <summary>A cell is named <c>ID:cell</c>, the cell one that holds prose; anything else is no cell.</summary>
    [Theory]
    [InlineData("D-AREA-TOPIC-ONE:closing", "D-AREA-TOPIC-ONE", "closing")]
    [InlineData("D-AREA-TOPIC-ONE:cross-refs", "D-AREA-TOPIC-ONE", "cross-refs")]
    [InlineData("D-AREA-TOPIC-ONE:status", null, null)]
    [InlineData("closing", null, null)]
    [InlineData(":closing", null, null)]
    public void ARowCell_IsReadAsIdColonCell(string text, string? id, string? cell)
    {
        var read = AnchorRowCell.Parse(text);

        Assert.Equal(id, read?.Id);
        Assert.Equal(cell, read?.Cell);
    }
}
