using DssHarness.Core.Anchors;

namespace DssHarness.Tests;

/// <summary>
/// What a cell would store broken - a check every anchor write is held to - and what replacing a cell does to its stored
/// text, which a batch holds its changes to: each on the text a consumer's own checks were measured against.
/// </summary>
public sealed class AnchorCellChecksTests
{
    private static readonly AnchorIdScanner Scanner = new(new AnchorIdRules("D", 3));

    private static readonly HashSet<string> Rows = new(["D-AREA-TOPIC-ONE", "D-AREA-TOPIC-TWO", "D-PROBE-EXISTING-ROW-ONE"], AnchorIdMatch.Comparer);

    private static readonly AnchorCellCuts Cuts = new(Scanner, ["tests", "src", "docs"]);

    /// <summary>
    /// A value the door would store cut is refused, naming the cut: an id cut where a line ends - at a hyphen, just before
    /// one, or inside a segment, where the two lines join into a row's id - an id with a space after one of its hyphens,
    /// and a path broken after its '/', wrapped there or stored with a space where it starts at a directory the tree has.
    /// </summary>
    [Theory]
    [InlineData("cites D-PROBE-EXISTING-\nROW-ONE across a line break after a hyphen", "an anchor id cut where a line ends ('D-PROBE-EXISTING-')")]
    [InlineData("cites D-PROBE-EXIS\r\nTING-ROW-ONE", "an anchor id cut where a line ends ('D-PROBE-EXIS')")]
    [InlineData("see D-AREA\n-TOPIC-ONE for the case", "an anchor id cut where a line ends ('D-AREA')")]
    [InlineData("cites D-PROBE-EXISTING- ROW-ONE, joined already", "an anchor id with a space after one of its hyphens ('D-PROBE-EXISTING- ROW-ONE')")]
    [InlineData("see tests/hir/\ntest_x.cpp for the case", @"a path broken across a line after a '/' ('tests/hir/\ntest_x.cpp')")]
    [InlineData("see src/\n.clang-format", @"a path broken across a line after a '/' ('src/\n.clang-format')")]
    [InlineData("see src/\n.clang-format\n", @"a path broken across a line after a '/' ('src/\n.clang-format')")]
    [InlineData("see tests/hir/ test_x.cpp for the case", "a path with a space after a '/' ('tests/hir/ test_x.cpp', as it would be stored)")]
    public void AValueTheDoorWouldStoreCut_IsRefused_NamingTheCut(string text, string refusal)
    {
        var problem = Assert.Single(Cuts.Of(text, "Trigger", stored: null, Rows));

        Assert.StartsWith("The Trigger holds ", problem);
        Assert.Contains(refusal, problem);
    }

    /// <summary>
    /// Prose that only looks cut is left alone: a directory before a word that is no file's name, an id before a figure or
    /// before the next id of a list, on one line or two, a prefix in prose, a sentence ending in a word, a blank line
    /// between two paragraphs, and a path with a space after it that starts at no directory the tree has.
    /// </summary>
    [Theory]
    [InlineData("touches no src/hir/ or src/mir/ at all")]
    [InlineData("D-AREA-TOPIC-ONE MF-4 was measured on one line")]
    [InlineData("cites D-AREA-TOPIC-ONE\nMF-4 was measured")]
    [InlineData("cites D-AREA-TOPIC-ONE\nand the test that proves it")]
    [InlineData("D-AREA-TOPIC-ONE\nD-AREA-TOPIC-TWO\n")]
    [InlineData("D-AREA-TOPIC-ONE\r\nD-AREA-TOPIC-TWO\r\n")]
    [InlineData("ids open with the `D-` prefix")]
    [InlineData("ids open with the D- prefix, and grade D- students pass")]
    [InlineData("planned for D-\nday one")]
    [InlineData("vendor/hir/ test_x.cpp is outside the tree")]
    [InlineData("one and/\nor the other")]
    [InlineData("each is read/\nwrite.")]
    [InlineData("see src/hir/\nfor the case")]
    [InlineData("see src/hir/\ne.g. the lowering")]
    [InlineData("see lib/hir/\n\nFollow-up: the lowering")]
    [InlineData("tests/ etc. are left as they are")]
    public void ProseThatOnlyLooksCut_IsLeftAlone(string text)
    {
        Assert.Empty(Cuts.Of(text, "Trigger", stored: null, Rows));
    }

    /// <summary>A cut the cell held already is history: a changed cell is refused only for a cut it did not hold.</summary>
    [Fact]
    public void ACutTheCellHeldAlready_IsHistory()
    {
        Assert.Empty(Cuts.Of("see docs/ README_x.md, and the rule it states", "Trigger", stored: "see docs/ README_x.md", Rows));
        Assert.Single(Cuts.Of("see docs/ README_x.md, and the rule it states", "Trigger", stored: "the rule", Rows));
    }

    /// <summary>
    /// What writing a cell does to its stored text: nothing, a change of spacing only, an addendum that keeps it word for
    /// word, a fill of an empty cell - or a loss, emptying the cell included, and a word a comma was put after.
    /// </summary>
    [Theory]
    [InlineData("the fix", "the fix", AnchorCellFate.Same)]
    [InlineData("the  fix", "the fix", AnchorCellFate.Respaced)]
    [InlineData("the fix", "the fix and the test that proves it", AnchorCellFate.Kept)]
    [InlineData("", "the fix", AnchorCellFate.Filled)]
    [InlineData("the fix", "a better fix", AnchorCellFate.Lost)]
    [InlineData("the fix", "", AnchorCellFate.Lost)]
    [InlineData("the fix", "the fix, made better", AnchorCellFate.Lost)]
    [InlineData("see tests/hir/ test_x.cpp", "see tests/hir/test_x.cpp", AnchorCellFate.Lost)]
    public void Fate_SaysWhatWritingACellDoesToItsStoredText(string stored, string written, AnchorCellFate fate)
    {
        Assert.Equal(fate, AnchorCellComparison.Fate(stored, written));
    }

    /// <summary>
    /// A word diff names what a cell loses and what replaces it, every unchanged word quoted as it stands, at its start,
    /// between two changes, and at its end: what a reader is asked to accept losing is never shown in part.
    /// </summary>
    [Theory]
    [InlineData(
        "one two three four five six seven eight nine ten",
        "one two three four five SIX seven eight nine ten",
        "one two three four five [-six-] {+SIX+} seven eight nine ten")]
    [InlineData("a b c d e f g h", "a b c d e f g X", "a b c d e f g [-h-] {+X+}")]
    [InlineData("a b c d e f g h i j k l m", "X b c d e f g h i j k l Y", "[-a-] {+X+} b c d e f g h i j k l [-m-] {+Y+}")]
    [InlineData(
        "see tests/hir/ test_x.cpp for the case",
        "see tests/hir/test_x.cpp for the case",
        "see [-tests/hir/ test_x.cpp-] {+tests/hir/test_x.cpp+} for the case")]
    [InlineData("the fix", "a better fix", "[-the-] {+a better+} fix")]
    [InlineData("the fix", "", "[-the fix-]")]
    public void WordDiff_NamesWhatIsLost_AndWhatReplacesIt(string stored, string written, string diff)
    {
        Assert.Equal(diff, AnchorCellComparison.WordDiff(stored, written));
    }

    /// <summary>
    /// A cell of two hundred thousand words is compared exactly, word by word, never shown as the one removed and the
    /// other written: one word changed in its middle is the one change said, every other word quoted as it stands, and a
    /// cell held whole inside a longer one is still kept. Red with the four million comparisons put back.
    /// </summary>
    [Fact]
    public void ATwoHundredThousandWordCell_IsComparedExactly()
    {
        var words = Enumerable.Range(0, 200_000).Select(index => $"w{index}").ToArray();
        var changed = (string[])words.Clone();
        changed[100_000] = "CHANGED";
        var stored = string.Join(' ', words);
        var written = string.Join(' ', changed);

        var diff = AnchorCellComparison.WordDiff(stored, written);

        Assert.Equal(
            $"{string.Join(' ', words[..100_000])} [-w100000-] {{+CHANGED+}} {string.Join(' ', words[100_001..])}",
            diff);
        Assert.Equal(AnchorCellFate.Lost, AnchorCellComparison.Fate(stored, written));
        Assert.Equal(AnchorCellFate.Kept, AnchorCellComparison.Fate(stored, $"{stored} and the test that proves it"));
    }

    /// <summary>
    /// A cell is named <c>ID:cell</c>, split at its last colon: the cell one of a row's five, and the id one an anchor
    /// could have - not empty, and holding no space and no colon. Only the three that hold prose are prose, the cells a
    /// loss of stored text is judged in; built by hand, a name no cell has is refused.
    /// </summary>
    [Theory]
    [InlineData("D-AREA-TOPIC-ONE:closing", "D-AREA-TOPIC-ONE", "closing", true)]
    [InlineData("D-AREA-TOPIC-ONE:cross-refs", "D-AREA-TOPIC-ONE", "cross-refs", true)]
    [InlineData("D-AREA-TOPIC-ONE:trigger", "D-AREA-TOPIC-ONE", "trigger", true)]
    [InlineData("D-AREA-TOPIC-ONE:status", "D-AREA-TOPIC-ONE", "status", false)]
    [InlineData("D-AREA-TOPIC-ONE:priority", "D-AREA-TOPIC-ONE", "priority", false)]
    [InlineData("D-AREA-TOPIC-ONE:notes", null, null, false)]
    [InlineData("closing", null, null, false)]
    [InlineData(":closing", null, null, false)]
    [InlineData(" D-AREA-TOPIC-ONE:closing", null, null, false)]
    [InlineData("D-AREA:TOPIC:trigger", null, null, false)]
    public void ARowCell_IsReadAsIdColonCell(string text, string? id, string? cell, bool prose)
    {
        var read = AnchorRowCell.Parse(text);

        Assert.Equal(id, read?.Id);
        Assert.Equal(cell, read?.Cell);
        Assert.Equal(prose, read?.IsProse ?? false);
        Assert.Throws<ArgumentException>(() => new AnchorRowCell("D-AREA-TOPIC-ONE", "notes"));
    }
}
