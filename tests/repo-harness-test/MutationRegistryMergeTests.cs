using System.Text;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// An arms registry merged by arm: what each of two trees did to each arm since the text both last held, taken where
/// only one of them did it, refused where both did it differently; the main tree's text kept line for line and changed
/// only where the agent contributed; and the result the same bytes however often it is merged again.
/// </summary>
public sealed class MutationRegistryMergeTests
{
    /// <summary>What the lines call the three texts, as a fold calls them.</summary>
    private static readonly MergeSides Sides = new("what both held", "the main tree", "the agent");

    /// <summary>
    /// A row of any kind belongs to the arm its second field names: one of a kind the registry does not read is carried
    /// with its arm, for the validation to refuse in its own words. A row that names no arm belongs to none, and the
    /// text holding it is unreadable.
    /// </summary>
    [Fact]
    public void ARowOfAnyKind_BelongsToTheArmItsSecondFieldNames_AndARowNamingNoneIsUnreadable()
    {
        var held = Text(A("one"), C("one"), A("two"), C("two"));
        var main = Text(A("one"), C("one"), A("two", "two, reworded"), C("two"));
        var agent = Text(A("one"), C("one"), "Z | one | a row of a kind nobody reads", A("two"), C("two"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, agent));

        Assert.Equal(Text(A("one"), C("one"), "Z | one | a row of a kind nobody reads", A("two", "two, reworded"), C("two")), merged.Text);
        Assert.Equal(
            ["line 3: 'Z' is no row this registry reads, which are A, C, G, B, M and S"],
            MutationRegistryParser.Parse(MutationRegistryParser.Lines(merged.Text), texts: null).Problems);

        var unreadable = Assert.IsType<RegistryMerge.Unreadable>(Merge(held, main, Text(A("one"), C("one"), "Z", A("two"), C("two"), "words and no field |")));

        Assert.Equal(
            [UnreadVersion.OfLine(MergeSide.Agent, 3, "a row names no arm"), UnreadVersion.OfLine(MergeSide.Agent, 6, "a row names no arm")],
            unreadable.Versions);
    }

    /// <summary>
    /// A comment or a blank line belongs to the row below it, so it goes where that row goes and leaves when that row
    /// does; and the lines above the first row are the registry's own down to their last blank line, the comments
    /// below that blank line being the first row's.
    /// </summary>
    [Fact]
    public void CommentsAndBlankLines_TravelWithTheRowBelow_AndTheHeaderEndsAtItsLastBlankLine()
    {
        var held = Text(
            "# the arms of a library",
            "# kept in one file",
            "",
            "# about one",
            A("one"),
            C("one"),
            "",
            "# about two",
            A("two"),
            C("two"),
            "# the end");
        var main = Text(
            "# the arms of a library",
            "# kept in one file, by hand",
            "",
            "# about one",
            A("one"),
            C("one"),
            "",
            "# about two",
            A("two"),
            C("two"),
            "",
            "# about three",
            A("three"),
            C("three"),
            "# the end");
        var agent = Text(
            "# the arms of a library",
            "# kept in one file",
            "",
            "# about one, reworded",
            A("one"),
            C("one"),
            "# the end");

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, agent));

        Assert.Equal(
            Text(
                "# the arms of a library",
                "# kept in one file, by hand",
                "",
                "# about one, reworded",
                A("one"),
                C("one"),
                "",
                "# about three",
                A("three"),
                C("three"),
                "# the end"),
            merged.Text);
        Assert.Equal((new MergeCounts(1, 0, 0), new MergeCounts(0, 1, 1), 0, 2), (merged.Main, merged.Agent, merged.Same, merged.Arms));
    }

    /// <summary>
    /// The rows of one arm are one arm wherever they stand: a row of it standing below another arm's is changed with
    /// the rest, and a change to it meets a change to its A row as two changes of one arm.
    /// </summary>
    [Fact]
    public void AnArmWhoseRowsStandApart_IsOneArm()
    {
        var held = Text(A("one"), C("one", "Near"), A("two"), C("two"), C("one", "Far"));
        var agent = Text(A("one"), C("one", "Near"), A("two"), C("two"), C("one", "Farther"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, Text(A("one"), C("one", "Near"), A("two", "two, reworded"), C("two"), C("one", "Far")), agent));

        Assert.Equal(Text(A("one"), C("one", "Near"), A("two", "two, reworded"), C("two"), C("one", "Farther")), merged.Text);
        Assert.Equal((new MergeCounts(0, 0, 1), new MergeCounts(0, 0, 1)), (merged.Main, merged.Agent));

        var conflicted = Assert.IsType<RegistryMerge.Conflicted>(Merge(held, Text(A("one", "one, reworded"), C("one", "Near"), A("two"), C("two"), C("one", "Far")), agent));
        var conflict = Assert.Single(conflicted.Conflicts);

        Assert.Equal(("one", ArmConflictKind.ChangedDifferently), (conflict.Arm, conflict.Kind));
        Assert.Equal([1, 2, 5], conflict.Agent!.Lines.Select(line => line.Line));
    }

    /// <summary>
    /// An arm one side added, removed or changed since the text both held, the other leaving it alone, is that side's -
    /// whichever side it is, and whatever the other did to another arm.
    /// </summary>
    [Theory]
    [InlineData(true, "added")]
    [InlineData(true, "removed")]
    [InlineData(true, "changed")]
    [InlineData(false, "added")]
    [InlineData(false, "removed")]
    [InlineData(false, "changed")]
    public void AnArmOneSideAddedRemovedOrChanged_IsThatSides(bool byMain, string done)
    {
        string[] one = [A("one"), C("one")];
        string[] two = ["", A("two"), C("two")];
        string[] four = ["", A("four"), C("four")];
        string[] three = ["", A("three"), C("three")];
        string[] reworded = ["", A("three", "three, reworded"), C("three")];
        string[] edited = done switch
        {
            "added" => [.. one, .. two, .. four],
            "removed" => [.. one],
            _ => [.. one, "", A("two", "two, reworded"), C("two")],
        };

        var held = Text([.. one, .. two, .. three]);
        var did = Text([.. edited, .. three]);
        var other = Text([.. one, .. two, .. reworded]);

        var merged = Assert.IsType<RegistryMerge.Merged>(byMain ? Merge(held, did, other) : Merge(held, other, did));

        Assert.Equal(Text([.. edited, .. reworded]), merged.Text);

        var counts = done switch
        {
            "added" => new MergeCounts(1, 0, 0),
            "removed" => new MergeCounts(0, 1, 0),
            _ => new MergeCounts(0, 0, 1),
        };

        Assert.Equal(byMain ? (counts, new MergeCounts(0, 0, 1)) : (new MergeCounts(0, 0, 1), counts), (merged.Main, merged.Agent));
        Assert.Equal(0, merged.Same);
    }

    /// <summary>An arm both sides added, removed or changed alike is one, the main tree's own lines, and neither's contribution.</summary>
    [Fact]
    public void AnArmBothSidesChangedAlike_IsOne()
    {
        var held = Text(A("one"), C("one"), "", A("two"), C("two"), "", A("three"), C("three"));
        var both = Text(A("two", "two, reworded"), C("two"), "", A("three"), C("three"), "", A("four"), C("four"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, both, both.Replace("\n", "\r\n", StringComparison.Ordinal)));

        Assert.Equal(both, merged.Text);
        Assert.Equal(Bytes(both), merged.Bytes);
        Assert.Equal((new MergeCounts(0, 0, 0), new MergeCounts(0, 0, 0), 3, 3), (merged.Main, merged.Agent, merged.Same, merged.Arms));
    }

    /// <summary>
    /// An arm both sides changed, each its own way, is a conflict: it names the arm, how the two disagree, and each
    /// side's version with the number each line has in that side's text. Said, it shows the lines the three do not all
    /// hold, each side called what the caller calls it.
    /// </summary>
    [Fact]
    public void AnArmBothSidesChangedDifferently_IsAConflict_NamingBothVersionsAndTheirLines()
    {
        var held = Text("# arms", "", A("one"), C("one"), A("two"), C("two"));
        var main = Text("# arms", "", A("one"), C("one"), A("two", "the main tree's why"), C("two"));
        var agent = Text("# arms", "", A("zero"), C("zero"), A("one"), C("one"), A("two", "the agent's why"), C("two"), C("two", "More"));

        var conflicted = Assert.IsType<RegistryMerge.Conflicted>(Merge(held, main, agent));
        var conflict = Assert.Single(conflicted.Conflicts);

        Assert.Equal(("two", ArmConflictKind.ChangedDifferently, RegistryPart.Arm), (conflict.Arm, conflict.Kind, conflict.Part));
        Assert.Equal([(5, A("two")), (6, C("two"))], conflict.Base!.Lines);
        Assert.Equal([(5, A("two", "the main tree's why")), (6, C("two"))], conflict.Main!.Lines);
        Assert.Equal([(7, A("two", "the agent's why")), (8, C("two")), (9, C("two", "More"))], conflict.Agent!.Lines);
        Assert.Equal((new MergeCounts(0, 0, 0), new MergeCounts(1, 0, 0)), (conflicted.Main, conflicted.Agent));
        Assert.Equal(
            [
                "arm 'two': changed by both, each differently",
                "  what both held, line 5:  " + A("two"),
                "  the main tree, line 5:   " + A("two", "the main tree's why"),
                "  the agent, line 7:       " + A("two", "the agent's why"),
                "  the agent, line 9:       " + C("two", "More"),
            ],
            conflict.Said(Sides));
    }

    /// <summary>An arm one side removed and the other changed is a conflict, either way round, and says which did which.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnArmOneSideRemovedAndTheOtherChanged_IsAConflict(bool removedByMain)
    {
        var held = Text(A("one"), C("one"), A("two"), C("two"));
        var removed = Text(A("one"), C("one"));
        var changed = Text(A("one"), C("one"), A("two", "two, reworded"), C("two"));

        var conflicted = Assert.IsType<RegistryMerge.Conflicted>(removedByMain ? Merge(held, removed, changed) : Merge(held, changed, removed));
        var conflict = Assert.Single(conflicted.Conflicts);

        Assert.Equal(("two", removedByMain ? ArmConflictKind.RemovedByMain : ArmConflictKind.RemovedByAgent), (conflict.Arm, conflict.Kind));
        Assert.Equal(removedByMain, conflict.Main is null);
        Assert.Equal(!removedByMain, conflict.Agent is null);
        Assert.Equal(
            [
                removedByMain ? "arm 'two': removed by the main tree, changed by the agent" : "arm 'two': removed by the agent, changed by the main tree",
                "  what both held, line 3:  " + A("two"),
                (removedByMain ? "  the agent, line 3:       " : "  the main tree, line 3:   ") + A("two", "two, reworded"),
            ],
            conflict.Said(Sides));
    }

    /// <summary>
    /// An arm both sides added under ids that differ only in the case of their letters is one arm - ids are compared
    /// ignoring case - added by both, and a conflict that says so; one whose rows differ besides is added by both,
    /// each differently.
    /// </summary>
    [Fact]
    public void AnArmBothSidesAdded_UnderIdsThatDifferOnlyInCase_IsAConflict()
    {
        var held = Text(A("one"), C("one"));
        var main = Text(A("one"), C("one"), A("two").Replace("| two |", "| Two |", StringComparison.Ordinal), "C | Two | Suite.Case | reddens");
        var agent = Text(A("one"), C("one"), A("two"), C("two"));

        var apart = Assert.Single(Assert.IsType<RegistryMerge.Conflicted>(Merge(held, main, agent)).Conflicts);

        Assert.Equal(("Two", ArmConflictKind.SpelledApart), (apart.Arm, apart.Kind));
        Assert.Null(apart.Base);
        Assert.Equal(("Two", "two"), (apart.Main!.Id, apart.Agent!.Id));

        // A blank line that parts one side's from what stands above it is no difference between them.
        var parted = Assert.Single(Assert.IsType<RegistryMerge.Conflicted>(Merge(held, Text(A("one"), C("one"), "", A("two").Replace("| two |", "| Two |", StringComparison.Ordinal), "C | Two | Suite.Case | reddens"), agent)).Conflicts);

        Assert.Equal(("Two", ArmConflictKind.SpelledApart), (parted.Arm, parted.Kind));
        Assert.Equal(
            [
                "arm 'Two': added by both, under ids that differ only in the case of their letters",
                "  the main tree, line 3:  " + A("two").Replace("| two |", "| Two |", StringComparison.Ordinal),
                "  the main tree, line 4:  C | Two | Suite.Case | reddens",
                "  the agent, line 3:      " + A("two"),
                "  the agent, line 4:      " + C("two"),
            ],
            apart.Said(Sides));

        var differently = Assert.Single(Assert.IsType<RegistryMerge.Conflicted>(Merge(held, main, Text(A("one"), C("one"), A("two", "another why"), C("two")))).Conflicts);

        Assert.Equal(("Two", ArmConflictKind.AddedDifferently), (differently.Arm, differently.Kind));
        Assert.Equal("arm 'Two': added by both, each differently", differently.Said(Sides)[0]);
    }

    /// <summary>
    /// With no text both held, an arm one side has is that side's, an arm both have alike is one, and an arm both have
    /// differently is a conflict. An arm one side removed cannot be told from one the other added, and is kept.
    /// </summary>
    [Fact]
    public void WithNoBase_AnArmOfOneSideIsKept_AndOneBothHoldDifferentlyIsAConflict()
    {
        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(null, Text(A("one"), C("one"), A("two"), C("two")), Text(A("one"), C("one"), A("three"), C("three"))));

        Assert.Equal(Text(A("one"), C("one"), A("two"), C("two"), A("three"), C("three")), merged.Text);
        Assert.Equal((new MergeCounts(1, 0, 0), new MergeCounts(1, 0, 0), 1, 3), (merged.Main, merged.Agent, merged.Same, merged.Arms));

        var conflict = Assert.Single(Assert.IsType<RegistryMerge.Conflicted>(Merge(null, Text(A("one", "the main tree's why"), C("one")), Text(A("one", "the agent's why"), C("one")))).Conflicts);

        Assert.Equal(("one", ArmConflictKind.AddedDifferently), (conflict.Arm, conflict.Kind));
        Assert.Null(conflict.Base);
        Assert.Equal(
            [
                "arm 'one': added by both, each differently",
                "  the main tree, line 1:  " + A("one", "the main tree's why"),
                "  the agent, line 1:      " + A("one", "the agent's why"),
            ],
            conflict.Said(Sides));
    }

    /// <summary>
    /// Two sides each adding a block of arms at one place - the end of the text, or between two arms - are no conflict:
    /// the blocks come out one after the other, the main tree's first, each whole.
    /// </summary>
    [Fact]
    public void TwoSidesAddingBlocksAtOnePlace_AreNoConflict_AndComeOutTheMainTreesFirst()
    {
        string[] opening = ["# the arms", "", A("one"), C("one")];
        string[] two = ["", A("two"), C("two")];
        string[] mains = ["", A("m1"), C("m1"), "", "# the main tree's second", A("m2"), C("m2")];
        string[] agents = ["", A("a1"), C("a1"), "", "# the agent's second", A("a2"), C("a2")];

        var atTheEnd = Assert.IsType<RegistryMerge.Merged>(Merge(Text([.. opening, .. two]), Text([.. opening, .. two, .. mains]), Text([.. opening, .. two, .. agents])));

        Assert.Equal(Text([.. opening, .. two, .. mains, .. agents]), atTheEnd.Text);
        Assert.Equal((new MergeCounts(2, 0, 0), new MergeCounts(2, 0, 0), 0, 6), (atTheEnd.Main, atTheEnd.Agent, atTheEnd.Same, atTheEnd.Arms));

        var between = Assert.IsType<RegistryMerge.Merged>(Merge(Text([.. opening, .. two]), Text([.. opening, .. mains, .. two]), Text([.. opening, .. agents, .. two])));

        Assert.Equal(Text([.. opening, .. mains, .. agents, .. two]), between.Text);
    }

    /// <summary>
    /// The merged text keeps the main tree's order, whatever order the agent's text has, and an arm only the agent has
    /// stands after the arm it follows in the agent's text - before the first where nothing stands above it there, and
    /// after a row only the main tree's version of that arm holds.
    /// </summary>
    [Fact]
    public void TheMergedText_KeepsTheMainTreesOrder_AndPlacesAnAgentsArmWhereTheAgentPlacedIt()
    {
        var held = Text(A("one"), C("one"), A("two"), C("two"), A("three"), C("three"));
        var main = Text(A("three"), C("three"), C("three", "More"), A("one"), C("one"), A("two"), C("two"));
        var agent = Text(
            A("top"),
            C("top"),
            A("one"),
            C("one"),
            A("mid"),
            C("mid"),
            A("two", "two, reworded"),
            C("two"),
            A("three"),
            C("three"),
            A("end"),
            C("end"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, agent));

        Assert.Equal(
            Text(
                A("top"),
                C("top"),
                A("three"),
                C("three"),
                C("three", "More"),
                A("end"),
                C("end"),
                A("one"),
                C("one"),
                A("mid"),
                C("mid"),
                A("two", "two, reworded"),
                C("two")),
            merged.Text);
    }

    /// <summary>
    /// The rows of an arm only the agent has keep their order, whatever order the main tree has the arms between them
    /// in: a row is never placed above an earlier row of its own arm, so the arm's A row stays above its others.
    /// </summary>
    [Fact]
    public void TheRowsOfAnArmOnlyTheAgentHas_KeepTheirOrder_WhateverOrderTheMainTreeHasTheArmsBetweenThem()
    {
        var held = Text(A("one"), C("one"), A("two"), C("two"));
        var main = Text(A("two"), C("two"), A("one"), C("one"), A("three"), C("three"));
        var agent = Text(A("one"), C("one"), A("new"), A("two"), C("two"), C("new"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, agent));

        Assert.Equal(Text(A("two"), C("two"), A("one"), C("one"), A("three"), C("three"), A("new"), C("new")), merged.Text);
        Assert.Empty(MutationRegistryParser.Parse(MutationRegistryParser.Lines(merged.Text), texts: null).Problems);
    }

    /// <summary>
    /// An arm of the agent's that follows, in its text, only arms the result no longer holds has no place to keep, and
    /// goes after the last arm: nothing says it stands first. The main tree's removal of the arm at the top changes
    /// nothing of the arm that then stands first.
    /// </summary>
    [Fact]
    public void AnAgentsArm_FollowingOnlyArmsTheResultDropped_GoesAfterTheLast()
    {
        var held = Text(A("one"), C("one"), "", A("two"), C("two"), "", A("three"), C("three"));
        var main = Text(A("two"), C("two"), "", A("three"), C("three"));
        var agent = Text(A("one"), C("one"), "", A("new"), C("new"), "", A("two"), C("two"), "", A("three"), C("three"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, agent));

        Assert.Equal(Text(A("two"), C("two"), "", A("three"), C("three"), "", A("new"), C("new")), merged.Text);
        Assert.Equal((new MergeCounts(0, 1, 0), new MergeCounts(1, 0, 0)), (merged.Main, merged.Agent));
    }

    /// <summary>
    /// An arm taken from the agent lies over the rows the main tree has of it, one for one in order: where the agent
    /// has more, the rest follow the last of them, and where it has fewer, the main tree's remaining rows go.
    /// </summary>
    [Fact]
    public void AnArmTakenFromTheAgent_LiesOverTheMainTreesRows_WhateverTheirNumber()
    {
        var held = Text(A("one"), C("one", "X"), A("two"), C("two"), C("one", "Y"));
        var main = Text(A("one"), C("one", "X"), A("two", "two, reworded"), C("two"), C("one", "Y"));

        var more = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, Text(A("one", "one, reworded"), C("one", "X"), C("one", "Y"), C("one", "Z"), A("two"), C("two"))));

        Assert.Equal(Text(A("one", "one, reworded"), C("one", "X"), A("two", "two, reworded"), C("two"), C("one", "Y"), C("one", "Z")), more.Text);

        var fewer = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, Text(A("one", "one, reworded"), C("one", "X"), A("two"), C("two"))));

        Assert.Equal(Text(A("one", "one, reworded"), C("one", "X"), A("two", "two, reworded"), C("two")), fewer.Text);
    }

    /// <summary>
    /// The lines above the first row, and those below the last, are each merged as one: taken from the side that
    /// changed them, and a conflict, named for where they stand, where both did differently.
    /// </summary>
    [Fact]
    public void TheLinesAboveTheFirstRow_AndBelowTheLast_AreEachMergedAsOne_AndAConflictThereIsNamed()
    {
        string[] arms = [A("one"), C("one")];
        var held = Text(["# the arms", "", .. arms, "# the end"]);

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, Text(["# the arms of a library", "", .. arms, "# the end"]), Text(["# the arms", "", .. arms, "", "# the end, for now"])));

        Assert.Equal(Text(["# the arms of a library", "", .. arms, "", "# the end, for now"]), merged.Text);
        Assert.Equal((new MergeCounts(0, 0, 0), new MergeCounts(0, 0, 0), 0, 1), (merged.Main, merged.Agent, merged.Same, merged.Arms));

        var added = Assert.IsType<RegistryMerge.Merged>(Merge(Text(arms), Text(["# the arms", "", .. arms]), Text([.. arms, "# the end"])));

        Assert.Equal(Text(["# the arms", "", .. arms, "# the end"]), added.Text);

        var conflicted = Assert.IsType<RegistryMerge.Conflicted>(Merge(
            held,
            Text(["# the arms of a library", "", .. arms, "# the end, for the main tree"]),
            Text(["# every arm", "", .. arms])));

        Assert.Equal(
            [
                (RegistryPart.AboveTheFirstRow, ArmConflictKind.ChangedDifferently, "the lines above the first row"),
                (RegistryPart.BelowTheLastRow, ArmConflictKind.RemovedByAgent, "the lines below the last row"),
            ],
            conflicted.Conflicts.Select(conflict => (conflict.Part, conflict.Kind, conflict.Arm)));
        Assert.Equal(
            [
                "the lines above the first row: changed by both, each differently",
                "  what both held, line 1:  # the arms",
                "  the main tree, line 1:   # the arms of a library",
                "  the agent, line 1:       # every arm",
            ],
            conflicted.Conflicts[0].Said(Sides));
        Assert.Equal(
            [
                "the lines below the last row: removed by the agent, changed by the main tree",
                "  what both held, line 5:  # the end",
                "  the main tree, line 5:   # the end, for the main tree",
            ],
            conflicted.Conflicts[1].Said(Sides));
    }

    /// <summary>
    /// Blank lines above a row only part it from what stands above it, and are no part of its arm: an arm removed or
    /// added at the top, which leaves the arm next to it with a blank line more or less above it, is no change to that
    /// arm, so the arm only the other side changed is that side's - as a merge of lines would have it. The arm that
    /// comes to stand first is written without the blank lines that parted it from what is gone.
    /// </summary>
    [Fact]
    public void AnArmRemovedOrAddedAtTheTop_IsNoChangeToTheArmNextToIt()
    {
        var held = Text(A("one"), C("one"), "", A("two"), C("two"));

        var removed = Assert.IsType<RegistryMerge.Merged>(Merge(held, Text(A("two"), C("two")), Text(A("one"), C("one"), "", A("two"), C("two", "Reworded"))));

        Assert.Equal(Text(A("two"), C("two", "Reworded")), removed.Text);
        Assert.Equal((new MergeCounts(0, 1, 0), new MergeCounts(0, 0, 1)), (removed.Main, removed.Agent));

        var kept = Assert.IsType<RegistryMerge.Merged>(Merge(held, Text("", A("two"), C("two")), Text(A("one"), C("one"), "", A("two"), C("two", "Reworded"))));

        Assert.Equal(Text("", A("two"), C("two", "Reworded")), kept.Text);

        var added = Assert.IsType<RegistryMerge.Merged>(Merge(held, Text(A("one", "reworded"), C("one"), "", A("two"), C("two")), Text(A("new"), C("new"), "", A("one"), C("one"), "", A("two"), C("two"))));

        Assert.Equal(Text(A("new"), C("new"), A("one", "reworded"), C("one"), "", A("two"), C("two")), added.Text);
        Assert.Equal((new MergeCounts(0, 0, 1), new MergeCounts(1, 0, 0)), (added.Main, added.Agent));

        var first = Assert.IsType<RegistryMerge.Merged>(Merge(held, Text(A("two"), C("two")), Text(A("one"), C("one"), "", A("two", "reworded"), C("two"))));

        Assert.Equal(Text(A("two", "reworded"), C("two")), first.Text);

        // The agent's rows lie below the blank lines the main tree parts them from what stands above with - and, where
        // nothing stands above them any more, below none.
        var parted = Assert.IsType<RegistryMerge.Merged>(Merge(held, held, Text(A("one"), C("one"), A("two", "reworded"), C("two"))));

        Assert.Equal(Text(A("one"), C("one"), "", A("two", "reworded"), C("two")), parted.Text);

        var alone = Assert.IsType<RegistryMerge.Merged>(Merge(held, held, Text(A("two", "reworded"), C("two"))));

        Assert.Equal(Text(A("two", "reworded"), C("two")), alone.Text);
    }

    /// <summary>
    /// An arm only the agent added comes out in the agent's order, after the nearest row above it there that the merged
    /// text holds: a row of an arm the main tree's version is kept for, which that version no longer holds, stands
    /// nowhere in it, and the place is sought further up.
    /// </summary>
    [Fact]
    public void AnArmOnlyTheAgentAdded_ComesOutInTheAgentsOrder_ThoughARowItFollowedIsGone()
    {
        var held = Text(A("one"), C("one", "Near"), A("two"), C("two"), C("one", "Far"));
        var main = Text(A("one"), C("one", "Near"), A("two"), C("two"));
        var agent = Text(A("one"), C("one", "Near"), A("s"), C("s"), A("two"), C("two"), C("one", "Far"), A("t"), C("t"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, agent));

        Assert.Equal(Text(A("one"), C("one", "Near"), A("s"), C("s"), A("two"), C("two"), A("t"), C("t")), merged.Text);
    }

    /// <summary>
    /// An arm only the agent added stays after the arm it follows in the agent's text where the main tree reworded every
    /// row of that arm: the main tree's version holds no segment like the agent's, and the agent's stands as the main
    /// tree's in its place - whether the arm stands first or after another.
    /// </summary>
    [Fact]
    public void AnArmOnlyTheAgentAdded_StaysAfterTheArmItFollows_ThoughTheMainTreeRewordedEveryRowOfIt()
    {
        var held = Text(A("zero"), C("zero"), A("one"), C("one"), A("two"), C("two"));
        var main = Text(A("zero"), C("zero"), A("one", "one, reworded"), C("one", "Reworded"), A("two"), C("two"));
        var agent = Text(A("zero"), C("zero"), A("one"), C("one"), A("new"), C("new"), A("two"), C("two"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, agent));

        Assert.Equal(Text(A("zero"), C("zero"), A("one", "one, reworded"), C("one", "Reworded"), A("new"), C("new"), A("two"), C("two")), merged.Text);

        var first = Assert.IsType<RegistryMerge.Merged>(Merge(
            Text(A("one"), C("one"), A("two"), C("two")),
            Text(A("one", "one, reworded"), C("one", "Reworded"), A("two"), C("two")),
            Text(A("one"), C("one"), A("new"), C("new"), A("two"), C("two"))));

        Assert.Equal(Text(A("one", "one, reworded"), C("one", "Reworded"), A("new"), C("new"), A("two"), C("two")), first.Text);
    }

    /// <summary>
    /// An arm only the agent added after the last row of an arm the main tree reworded there, and gave a further row
    /// below, follows that arm's last row - as it would were the row it follows kept as it was - never a row in the middle
    /// of the arm.
    /// </summary>
    [Fact]
    public void AnArmOnlyTheAgentAdded_AfterAnArmsLastRow_FollowsItsLast_WhereTheMainTreeRewordedThatRowAndAddedOne()
    {
        var held = Text(A("one"), C("one", "X"), A("two"), C("two"), C("one", "Y"), A("three"), C("three"));
        var main = Text(A("one"), C("one", "X"), A("two"), C("two"), C("one", "Reworded"), A("three"), C("three"), C("one", "Z"));
        var agent = Text(A("one"), C("one", "X"), A("two"), C("two"), C("one", "Y"), A("new"), C("new"), A("three"), C("three"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, agent));

        Assert.Equal(
            Text(A("one"), C("one", "X"), A("two"), C("two"), C("one", "Reworded"), A("three"), C("three"), C("one", "Z"), A("new"), C("new")),
            merged.Text);
    }

    /// <summary>
    /// An arm only the agent added stays directly after an arm taken from the agent that it follows, as that arm stands in
    /// the merged text - below the blank lines the main tree parts it with - never at the top.
    /// </summary>
    [Fact]
    public void AnArmOnlyTheAgentAdded_StaysAfterAnArmTakenFromTheAgent_ThatItFollows()
    {
        var held = Text(A("one"), C("one"), "", A("two"), C("two"));
        var agent = Text(A("one", "one, reworded"), C("one"), A("new"), C("new"), "", A("two"), C("two"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, held, agent));

        Assert.Equal(Text(A("one", "one, reworded"), C("one"), A("new"), C("new"), "", A("two"), C("two")), merged.Text);
    }

    /// <summary>
    /// With no text both held, the lines above the first row that one side alone has are that side's: a side with none
    /// holds none, which is no version of them.
    /// </summary>
    [Fact]
    public void WithNoBase_TheLinesAboveTheFirstRowThatOneSideAloneHas_AreThatSides()
    {
        var opened = Text("# the arms", "", A("one"), C("one"));
        var bare = Text(A("one"), C("one"));

        Assert.Equal(opened, Assert.IsType<RegistryMerge.Merged>(Merge(null, opened, bare)).Text);
        Assert.Equal(opened, Assert.IsType<RegistryMerge.Merged>(Merge(null, bare, opened)).Text);
    }

    /// <summary>
    /// The agent's lines are ended with a line feed where the main tree's text has no line, or one it does not end, to
    /// take an ending from - whatever the agent's own lines end with.
    /// </summary>
    [Fact]
    public void AnAgentsLines_EndWithALineFeed_WhereTheMainTreesTextEndsNoLineToFollow()
    {
        var agent = Bytes(Text(A("one"), C("one")).Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(Bytes(Text(A("one"), C("one"))), Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(null, Bytes(string.Empty), agent)).Bytes);
        Assert.Equal(
            Bytes(A("zero") + "\n" + A("one") + "\n" + C("one")),
            Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(null, Bytes(A("zero")), Bytes(Text(A("zero"), A("one"), C("one")).Replace("\n", "\r\n", StringComparison.Ordinal)))).Bytes);
    }

    /// <summary>
    /// A merge whose text would not read back as it was merged is a conflict, never a text: where an arm that keeps a
    /// blank line above it would come to stand first, that line and those above it would be read as the registry's own
    /// opening lines, and merging the result again would then give another text.
    /// </summary>
    [Fact]
    public void AMergeThatWouldNotReadBackAsItWasMerged_IsAConflict_NamingTheArmThatWouldStandFirst()
    {
        var held = Text(A("one"), C("one"), "", A("two"), C("two"));
        var main = Text(A("one"), C("one"), "", A("two"), C("two"), "", A("three"), C("three"));
        var agent = Text(A("two"), C("two"), "# moved below", "", A("one"), C("one"));

        var conflict = Assert.Single(Assert.IsType<RegistryMerge.Conflicted>(Merge(held, main, agent)).Conflicts);

        Assert.Equal(("one", ArmConflictKind.WouldOpenTheText, RegistryPart.Arm), (conflict.Arm, conflict.Kind, conflict.Part));
        Assert.Equal(
            [
                "arm 'one': it would stand first, and the lines kept above it down to a blank line would then be read as the lines above the first row",
                "  the agent, line 3:  # moved below",
                "  the agent, line 4:",
            ],
            conflict.Said(Sides));
    }

    /// <summary>
    /// A text that ends its lines another way is not changed by that: no arm of it is. And the merged text ends its
    /// lines as the main tree does - each line the main tree holds as it holds it, each line of the agent's with the
    /// ending of the main tree's first line - and ends with a line ending where the main tree's does.
    /// </summary>
    [Fact]
    public void AChangeOfLineEndingsAlone_IsNoChange_AndTheMergedTextEndsItsLinesAsTheMainTreeDoes()
    {
        var held = Text(A("one"), C("one"), A("two"), C("two"));
        var reworded = Text(A("one"), C("one"), A("two", "two, reworded"), C("two"));
        var added = Text(A("one"), C("one"), A("two"), C("two"), A("three"), C("three"));
        var both = Text(A("one"), C("one"), A("two", "two, reworded"), C("two"), A("three"), C("three"));

        var windows = Assert.IsType<RegistryMerge.Merged>(Merge(held, Windows(reworded), added));

        Assert.Equal(Windows(both), windows.Text);
        Assert.Equal(Bytes(Windows(both)), windows.Bytes);
        Assert.Equal((new MergeCounts(0, 0, 1), new MergeCounts(1, 0, 0), 0), (windows.Main, windows.Agent, windows.Same));

        var unix = Assert.IsType<RegistryMerge.Merged>(Merge(Windows(held), reworded, Windows(added)));

        Assert.Equal(Bytes(both), unix.Bytes);

        // Each of the main tree's lines as it holds it, its last given an ending once another follows it; the agent's
        // with the ending of the main tree's first line; and no ending at the end, as the main tree has none.
        var mixed = Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(
            Bytes(held),
            Bytes(A("one") + "\n" + C("one") + "\r\n" + A("two", "two, reworded") + "\r" + C("two")),
            Bytes(Windows(added))));

        Assert.Equal(
            A("one") + "\n" + C("one") + "\r\n" + A("two", "two, reworded") + "\r" + C("two") + "\n" + A("three") + "\n" + C("three"),
            Encoding.UTF8.GetString(mixed.Bytes));

        static string Windows(string text) => text.Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// The merged text reads back as the lines it was merged from, where the endings alone would lose one: an empty last
    /// line is ended though the main tree's last line is not, since unended it would be no line; and an empty line
    /// written after a line that a carriage return alone ends is ended by a carriage return and a line feed, since a
    /// line feed alone would join the carriage return above it and end one line where there were two.
    /// </summary>
    [Fact]
    public void TheMergedText_ReadsBackAsTheLinesItWasMergedFrom()
    {
        var held = Text(A("one"), C("one"), A("two"), C("two"));

        var closing = Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(
            Bytes(held),
            Bytes(A("one") + "\n" + C("one") + "\n" + A("two", "two, reworded") + "\n" + C("two")),
            Bytes(Text(A("one"), C("one"), A("two"), C("two"), "# the end", ""))));

        Assert.Equal(Text(A("one"), C("one"), A("two", "two, reworded"), C("two"), "# the end", ""), closing.Text);
        Assert.Equal(["# the end", "", ""], MutationRegistryParser.Lines(closing.Text).TakeLast(3));

        var between = Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(
            Bytes(held),
            Bytes(A("one") + "\n" + C("one") + "\r" + A("two", "two, reworded") + "\n" + C("two") + "\n"),
            Bytes(Text(A("one"), C("one"), "", A("new"), C("new"), A("two"), C("two")))));

        Assert.Equal(
            A("one") + "\n" + C("one") + "\r" + "\r\n" + A("new") + "\n" + C("new") + "\n" + A("two", "two, reworded") + "\n" + C("two") + "\n",
            between.Text);
        Assert.Equal([A("one"), C("one"), "", A("new"), C("new"), A("two", "two, reworded"), C("two"), ""], MutationRegistryParser.Lines(between.Text));
    }

    /// <summary>
    /// A text merged with itself is itself, and a merge in which the agent changed nothing - or only how its lines end -
    /// is the main tree's text, byte for byte: a byte order mark, each line's own ending and the absence of one at the
    /// end included.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("# only a comment\n")]
    [InlineData("\n\n# and blank lines\n\n")]
    [InlineData("A | one | s | b | a | TEST-RED | t | r | 1 | d | why\nC | one | S.C | red\n")]
    [InlineData("# arms\r\n\r\nA | one | s | b | a | TEST-RED | t | r | 1 | d | why\r\nC | one | S.C | red\r\n\r\n# end\r\n")]
    [InlineData("# arms\n\nA | one | s | b | a | TEST-RED | t | r | 1 | d | why\r\nC | one | S.C | red\rA | two | s | b | a | TEST-RED | t | r | 1 | d | why\nC | two | S.C | red")]
    [InlineData("\n\nA | one | s | b | a | TEST-RED | t | r | 1 | d | why\n\n\nC | one | S.C | red\n\n")]
    public void MergingATextWithItself_OrWithAnAgentThatChangedNothing_ReturnsTheMainTreesTextByteForByte(string text)
    {
        foreach (var marked in new[] { false, true })
        {
            byte[] bytes = [.. marked ? Encoding.UTF8.Preamble : [], .. Bytes(text)];
            byte[] other = Bytes("# what both held\n\nA | zero | s | b | a | TEST-RED | t | r | 1 | d | why\nC | zero | S.C | red\n# its end\n");

            Assert.Equal(bytes, Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(bytes, bytes, bytes)).Bytes);
            Assert.Equal(bytes, Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(null, bytes, bytes)).Bytes);
            Assert.Equal(bytes, Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(other, bytes, other)).Bytes);
            Assert.Equal(bytes, Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(bytes, bytes, Bytes(text.Replace("\r\n", "\n", StringComparison.Ordinal)))).Bytes);
        }
    }

    /// <summary>
    /// A merge in which the main tree changed nothing is not the agent's text: it holds every arm as the agent holds it,
    /// the arms both hold in the main tree's order and an arm the agent added after the arm it follows in the agent's
    /// text, each line ended as the main tree ends its lines and behind the main tree's byte order mark, or none.
    /// </summary>
    [Fact]
    public void AMergeInWhichTheMainTreeChangedNothing_HoldsTheAgentsArms_InTheMainTreesOrderEndingsAndMark()
    {
        var held = Text(A("one"), C("one"), A("two"), C("two"));
        var agent = Text(A("two", "two, reworded"), C("two"), A("one"), C("one"), A("three"), C("three"));
        byte[] marked = [.. Encoding.UTF8.Preamble, .. Bytes(agent.Replace("\n", "\r\n", StringComparison.Ordinal))];

        var merged = Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(Bytes(held), Bytes(held), marked));

        Assert.Equal(Bytes(Text(A("one"), C("one"), A("three"), C("three"), A("two", "two, reworded"), C("two"))), merged.Bytes);
    }

    /// <summary>
    /// A merge run again with its own result on either side gives that result, byte for byte: with the main tree
    /// already holding it, and with the agent already holding it - with a text both held and with none, whichever
    /// side ends its lines which way, and with a byte order mark on either. A stopped fold, rebase or refresh rests on
    /// it when it is run again.
    /// </summary>
    [Fact]
    public void MergingAResultAgainWithEitherSideThatMadeIt_ReturnsTheResultByteForByte()
    {
        var clean = 0;

        foreach (var (held, main, agent) in Generated())
        {
            foreach (var (mainAs, agentAs) in Shapes)
            {
                ReadOnlyMemory<byte>? @base = held is null ? null : Bytes(held);
                var m = mainAs(main);
                var a = agentAs(agent);

                if (MutationRegistryMerge.Merge(@base, m, a) is not RegistryMerge.Merged merged)
                {
                    continue;
                }

                clean++;

                var mainHoldsIt = MutationRegistryMerge.Merge(@base, merged.Bytes, a);
                var agentHoldsIt = MutationRegistryMerge.Merge(@base, m, merged.Bytes);

                Assert.True(
                    mainHoldsIt is RegistryMerge.Merged again && again.Bytes.AsSpan().SequenceEqual(merged.Bytes),
                    $"With the main tree holding the result, the merge gave {Shown(mainHoldsIt)}\nof what both held:\n{held}\nthe main tree:\n{main}\nthe agent:\n{agent}\nfirst merged as:\n{merged.Text}");
                Assert.True(
                    agentHoldsIt is RegistryMerge.Merged once && once.Bytes.AsSpan().SequenceEqual(merged.Bytes),
                    $"With the agent holding the result, the merge gave {Shown(agentHoldsIt)}\nof what both held:\n{held}\nthe main tree:\n{main}\nthe agent:\n{agent}\nfirst merged as:\n{merged.Text}");
            }
        }

        // The table must hold merges to speak of: most pairs that share a text merge cleanly.
        Assert.True(clean > 1500, $"Only {clean} of the generated merges were clean.");
    }

    /// <summary>
    /// A text that is not UTF-8, or that holds a row naming no arm, is unreadable, and so is the merge: it says which
    /// of the three, where, and why, in the caller's names for them, and offers no text.
    /// </summary>
    [Fact]
    public void ATextThatIsNotUtf8_OrHoldsARowNamingNoArm_IsUnreadable_SayingWhichOfTheThree()
    {
        var sound = Bytes(Text(A("one"), C("one")));
        byte[] latin = [.. sound, 0xE9, 0x0A];
        var stray = Bytes(Text(A("one"), C("one"), "stray words", "Z |  | x"));

        var agents = Assert.IsType<RegistryMerge.Unreadable>(MutationRegistryMerge.Merge(sound, sound, latin));

        Assert.Equal([UnreadVersion.OfText(MergeSide.Agent, $"is not UTF-8: the byte at offset {sound.Length} (0xE9) does not form a UTF-8 character")], agents.Versions);
        Assert.All(
            typeof(UnreadVersion).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            constructor => Assert.False(constructor.IsPublic, $"{constructor} is public"));
        Assert.Equal([$"the text of the agent is not UTF-8: the byte at offset {sound.Length} (0xE9) does not form a UTF-8 character"], agents.Said(Sides));

        var all = Assert.IsType<RegistryMerge.Unreadable>(MutationRegistryMerge.Merge(latin, stray, stray));

        Assert.Equal(
            [
                $"the text of what both held is not UTF-8: the byte at offset {sound.Length} (0xE9) does not form a UTF-8 character",
                "line 3 of the text of the main tree: a row names no arm",
                "line 4 of the text of the main tree: a row names no arm",
                "line 3 of the text of the agent: a row names no arm",
                "line 4 of the text of the agent: a row names no arm",
            ],
            all.Said(Sides));
        Assert.Equal(
            [MergeSide.Base, MergeSide.Main, MergeSide.Main, MergeSide.Agent, MergeSide.Agent],
            all.Versions.Select(version => version.Side));

        // A line opening with a byte order mark - a second behind the text's own, or one on a later line - would be read
        // as the text's own once it stood first, as it does here once the lines above it go: unreadable too, whichever
        // of the three holds it.
        byte[] marked = [.. Encoding.UTF8.Preamble, .. sound];
        byte[] twice = [.. Encoding.UTF8.Preamble, .. marked];
        var later = Bytes("A | x | 1\nA | w | 2\n\uFEFFA | y | 3\n");

        Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(sound, marked, marked));
        Assert.Equal(
            ["line 1 of the text of the agent: opens with a byte order mark, which no line may open with: standing first, it would be read as the text's own"],
            Assert.IsType<RegistryMerge.Unreadable>(MutationRegistryMerge.Merge(sound, sound, twice)).Said(Sides));
        Assert.Equal(
            ["line 3 of the text of what both held: opens with a byte order mark, which no line may open with: standing first, it would be read as the text's own"],
            Assert.IsType<RegistryMerge.Unreadable>(MutationRegistryMerge.Merge(later, Bytes("A | x | 1\n\uFEFFA | y | 3\n"), Bytes("A | w | 2\n\uFEFFA | y | 3\n"))).Said(Sides).Take(1));
        Assert.Equal(
            [MergeSide.Main],
            Assert.IsType<RegistryMerge.Unreadable>(MutationRegistryMerge.Merge(sound, later, sound)).Versions.Select(version => version.Side));

        // With no text both held, none is read: only the two there are.
        Assert.IsType<RegistryMerge.Merged>(MutationRegistryMerge.Merge(null, sound, sound));
    }

    /// <summary>
    /// The counts say what each side contributed since the text both held - the arms it added, removed and changed that
    /// the other left alone - how many both did alike, and how many arms the merged text holds.
    /// </summary>
    [Fact]
    public void TheCounts_SayWhatEachSideContributed()
    {
        var held = Text(A("one"), C("one"), A("two"), C("two"), A("three"), C("three"), A("four"), C("four"), A("five"), C("five"));
        var main = Text(A("one", "one, reworded"), C("one"), A("three"), C("three"), A("four"), C("four"), A("five", "five, by both"), C("five"), A("m1"), C("m1"), A("m2"), C("m2"), A("both"), C("both"));
        var agent = Text(A("one"), C("one"), A("two"), C("two"), A("three", "three, reworded"), C("three"), A("five", "five, by both"), C("five"), A("a1"), C("a1"), A("both"), C("both"));

        var merged = Assert.IsType<RegistryMerge.Merged>(Merge(held, main, agent));

        Assert.Equal(new MergeCounts(Added: 2, Removed: 1, Changed: 1), merged.Main);
        Assert.Equal(new MergeCounts(Added: 1, Removed: 1, Changed: 1), merged.Agent);
        Assert.Equal(2, merged.Same);
        Assert.Equal(7, merged.Arms);
        Assert.Equal(
            Text(A("one", "one, reworded"), C("one"), A("three", "three, reworded"), C("three"), A("five", "five, by both"), C("five"), A("m1"), C("m1"), A("m2"), C("m2"), A("a1"), C("a1"), A("both"), C("both")),
            merged.Text);
        Assert.Equal(
            "merged by arm: 3 from the agent (1 added, 1 changed, 1 removed), 4 from the main tree (2 added, 1 changed, 1 removed), 2 alike in both; 7 arm(s) in all",
            merged.Said(Sides));
    }

    /// <summary>
    /// The sentences call the three sides what the caller calls them, so a command says one thing of its textual merges
    /// and of this one; and a side that contributed nothing is said to have contributed none.
    /// </summary>
    [Fact]
    public void TheSentences_CallTheThreeSidesWhatTheCallerCallsThem()
    {
        var merged = new RegistryMerge.Merged([], string.Empty, new MergeCounts(66, 0, 2), new MergeCounts(30, 0, 0), 0, 307);

        Assert.Equal(
            "merged by arm: 30 from the agent (30 added), 68 from the main tree (66 added, 2 changed); 307 arm(s) in all",
            merged.Said(Sides));
        Assert.Equal(
            "merged by arm: 30 from the worktree (30 added), 68 from the new base (66 added, 2 changed); 307 arm(s) in all",
            merged.Said(new MergeSides("the old base", "the new base", "the worktree")));
        Assert.Equal(
            "merged by arm: 0 from the agent, 1 from the main tree (1 removed); 1 arm(s) in all",
            new RegistryMerge.Merged([], string.Empty, new MergeCounts(0, 1, 0), new MergeCounts(0, 0, 0), 0, 1).Said(Sides));

        var conflict = ArmConflict.OverArm(
            "two",
            ArmConflictKind.RemovedByMain,
            new ArmVersion("two", [(120, "A | two | x")]),
            null,
            new ArmVersion("two", [(124, "A | two | y")]));

        Assert.Equal(
            [
                "arm 'two': removed by the new base, changed by the worktree",
                "  the old base, line 120:  A | two | x",
                "  the worktree, line 124:  A | two | y",
            ],
            conflict.Said(new MergeSides("the old base", "the new base", "the worktree")));
        Assert.Equal(
            "line 14 of the text of the new base: a row names no arm",
            UnreadVersion.OfLine(MergeSide.Main, 14, "a row names no arm").Said(new MergeSides("the old base", "the new base", "the worktree")));
    }

    /// <summary>
    /// A conflict is made one way for each thing it can be over - an arm, the lines above the first row, those below the
    /// last - so what it is over, what it is called and how the sides disagree always agree: the lines above the first row
    /// are never added under ids that differ in case, nor would they stand first.
    /// </summary>
    [Fact]
    public void AConflict_IsMadeOverAnArm_OrTheLinesAboveOrBelow_EachNamedAsWhatItIsOver()
    {
        var lines = new ArmVersion(ArmConflict.AboveTheFirstRow, [(1, "# the arms")]);

        Assert.Equal(
            [
                (RegistryPart.Arm, "one"),
                (RegistryPart.AboveTheFirstRow, ArmConflict.AboveTheFirstRow),
                (RegistryPart.BelowTheLastRow, ArmConflict.BelowTheLastRow),
            ],
            new[]
            {
                ArmConflict.OverArm("one", ArmConflictKind.SpelledApart, null, new ArmVersion("one", [(1, A("one"))]), new ArmVersion("ONE", [(1, A("ONE"))])),
                ArmConflict.OverTheLinesAbove(ArmConflictKind.ChangedDifferently, lines, lines, lines),
                ArmConflict.OverTheLinesBelow(ArmConflictKind.WouldOpenTheText, null, lines, null),
            }.Select(conflict => (conflict.Part, conflict.Arm)));
        Assert.Throws<ArgumentException>(() => ArmConflict.OverTheLinesAbove(ArmConflictKind.WouldOpenTheText, null, lines, null));
        Assert.Throws<ArgumentException>(() => ArmConflict.OverTheLinesBelow(ArmConflictKind.SpelledApart, null, lines, lines));
        Assert.Throws<ArgumentException>(() => ArmConflict.OverArm(" ", ArmConflictKind.ChangedDifferently, lines, lines, lines));
        Assert.All(
            typeof(ArmConflict).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsPrivate, $"{constructor} is not private"));
    }

    /// <summary>
    /// Every merge of two valid registries that conflicts over no arm, and leaves an arm, is a valid registry: each
    /// side keeps every A row above its arm's other rows, and the merge never reorders the rows of one arm. Held over a
    /// table of generated pairs, each result read by the parser. Two sides that between them removed every arm leave
    /// a registry declaring none, which the validation refuses as it refuses any such registry.
    /// </summary>
    [Fact]
    public void EveryMergeOfTwoValidRegistries_ThatConflictsOverNoArm_IsAValidRegistry()
    {
        var clean = 0;

        foreach (var (held, main, agent) in Generated())
        {
            Assert.All(new[] { held, main, agent }.OfType<string>(), text => Assert.Empty(MutationRegistryParser.Parse(MutationRegistryParser.Lines(text), texts: null).Problems));

            if (Merge(held, main, agent) is not RegistryMerge.Merged merged)
            {
                continue;
            }

            var reading = MutationRegistryParser.Parse(MutationRegistryParser.Lines(merged.Text), texts: null);

            if (merged.Arms == 0)
            {
                Assert.Equal(["the registry declares no A row: an empty one would report every arm behaving as declared while holding none"], reading.Problems);
                continue;
            }

            clean++;

            Assert.True(
                reading.Valid,
                $"{string.Join("; ", reading.Problems)}\nof what both held:\n{held}\nthe main tree:\n{main}\nthe agent:\n{agent}\nmerged as:\n{merged.Text}");
            Assert.Equal(merged.Arms, reading.Registry.Arms.Count);
        }

        Assert.True(clean > 300, $"Only {clean} of the generated merges were clean.");
    }

    /// <summary>
    /// Whatever three texts hold - rows of a few arms in any order and either case, rows of a kind nobody reads, comments,
    /// blank lines, any ending on any line and none on the last, a byte order mark, a line opening with one - merging them
    /// throws on none, and a text holding a line that opens with a byte order mark is unreadable; a text merged with
    /// itself is itself, and a merge in which the agent changed nothing is the main tree's text, byte for byte; and every
    /// merge that is offered gives itself again with its result on either side.
    /// </summary>
    [Fact]
    public void WhateverThreeTextsHold_AMergeThatIsOffered_GivesItselfAgain()
    {
        var random = new Random(20261012);
        var offered = 0;

        for (var round = 0; round < Rounds; round++)
        {
            var lines = Enumerable.Range(0, random.Next(0, 10)).Select(_ => AnyLine(random)).ToList();
            var held = Held(lines, random);
            var main = Held(AnyChange(lines, random), random);
            var agent = Held(AnyChange(lines, random), random);

            var (heldRead, mainRead, agentRead) = (Readable(held), Readable(main), Readable(agent));

            if (!mainRead)
            {
                Assert.IsType<RegistryMerge.Unreadable>(MutationRegistryMerge.Merge(null, main, main));
                continue;
            }

            AssertSame(main, MutationRegistryMerge.Merge(main, main, main), held, main, agent, "its own three times over");
            AssertSame(main, MutationRegistryMerge.Merge(null, main, main), held, main, agent, "its own twice, with no text both held");

            if (heldRead)
            {
                AssertSame(main, MutationRegistryMerge.Merge(held, main, held), held, main, agent, "the main tree's, the agent holding what both held");
            }

            foreach (var both in new ReadOnlyMemory<byte>?[] { held, null })
            {
                var merge = MutationRegistryMerge.Merge(both, main, agent);

                if (!agentRead || (both is not null && !heldRead))
                {
                    Assert.IsType<RegistryMerge.Unreadable>(merge);
                    continue;
                }

                if (merge is not RegistryMerge.Merged merged)
                {
                    continue;
                }

                offered++;
                AssertSame(merged.Bytes, MutationRegistryMerge.Merge(both, merged.Bytes, agent), held, main, agent, "the result, the main tree holding it");
                AssertSame(merged.Bytes, MutationRegistryMerge.Merge(both, main, merged.Bytes), held, main, agent, "the result, the agent holding it");
            }
        }

        Assert.True(offered > Rounds / 4, $"Only {offered} merges were offered.");

        static void AssertSame(byte[] expected, RegistryMerge merge, byte[] held, byte[] main, byte[] agent, string wanted)
            => Assert.True(
                merge is RegistryMerge.Merged merged && merged.Bytes.AsSpan().SequenceEqual(expected),
                $"Wanted {wanted}, and the merge gave {Shown(merge)}\nof what both held:\n{Escaped(held)}\nthe main tree:\n{Escaped(main)}\nthe agent:\n{Escaped(agent)}\nwanted:\n{Escaped(expected)}");

        static string Escaped(byte[] bytes) => Encoding.UTF8.GetString(bytes).Replace("\r", "<CR>", StringComparison.Ordinal).Replace("\n", "<LF>\n", StringComparison.Ordinal);

        static bool Readable(byte[] bytes) => RegistryText.TryRead(bytes, out _, out _, out _);
    }

    /// <summary>How many texts <see cref="WhateverThreeTextsHold_AMergeThatIsOffered_GivesItselfAgain"/> merges.</summary>
    private const int Rounds = 20000;

    /// <summary>
    /// A line of any kind a registry's text may hold: blank, a comment, or a row of one of a few arms - now and then one
    /// opening with a byte order mark.
    /// </summary>
    private static string AnyLine(Random random) => random.Next(10) switch
    {
        0 => string.Empty,
        1 => "   ",
        2 or 3 => $"# note {random.Next(4)}",
        _ => $"{(random.Next(20) == 0 ? "\uFEFF" : string.Empty)}{"AACCCZ"[random.Next(6)]} | {new[] { "x", "X", "y", "z", "w" }[random.Next(5)]} | {random.Next(4)}",
    };

    /// <summary>The lines with a few of them added, removed, replaced or exchanged - or none.</summary>
    private static List<string> AnyChange(List<string> lines, Random random)
    {
        var changed = lines.ToList();

        for (var change = random.Next(0, 4); change > 0; change--)
        {
            switch (random.Next(4))
            {
                case 0:
                    changed.Insert(random.Next(changed.Count + 1), AnyLine(random));
                    break;

                case 1 when changed.Count > 0:
                    changed.RemoveAt(random.Next(changed.Count));
                    break;

                case 2 when changed.Count > 0:
                    changed[random.Next(changed.Count)] = AnyLine(random);
                    break;

                case 3 when changed.Count > 1:
                    var (one, other) = (random.Next(changed.Count), random.Next(changed.Count));

                    (changed[one], changed[other]) = (changed[other], changed[one]);
                    break;
            }
        }

        return changed;
    }

    /// <summary>The lines as a tree may hold them: each ended one way or another, the last perhaps not at all, behind a byte order mark or not.</summary>
    private static byte[] Held(List<string> lines, Random random)
    {
        var text = new StringBuilder();

        for (var index = 0; index < lines.Count; index++)
        {
            text.Append(lines[index]);

            if (index < lines.Count - 1 || random.Next(4) != 0)
            {
                text.Append(new[] { "\n", "\n", "\n", "\r\n", "\r" }[random.Next(5)]);
            }
        }

        return [.. random.Next(8) == 0 ? Encoding.UTF8.Preamble : [], .. Bytes(text.ToString())];
    }

    /// <summary>How the main tree's and the agent's bytes are made of a generated text: each way a tree may hold it.</summary>
    private static readonly (Func<string, byte[]> Main, Func<string, byte[]> Agent)[] Shapes =
    [
        (Bytes, Bytes),
        (text => Bytes(Windows(text)), Bytes),
        (Bytes, text => Bytes(Windows(text))),
        (text => [.. Encoding.UTF8.Preamble, .. Bytes(text)], Bytes),
        (Bytes, text => [.. Encoding.UTF8.Preamble, .. Bytes(Windows(text))]),
        (text => Bytes(Mixed(text)), Bytes),
        (text => Bytes(text.TrimEnd('\n')), text => Bytes(Windows(text))),
    ];

    private static string Windows(string text) => text.Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>The text with every third line ended as Windows ends one, and the others as they are.</summary>
    private static string Mixed(string text)
    {
        var lines = text.Split('\n');

        return string.Concat(lines.Select((line, index) => index == lines.Length - 1 ? line : line + (index % 3 == 2 ? "\r\n" : "\n")));
    }

    /// <summary>
    /// A table of registries two sides changed since a text both held, each valid: arms added at the top, in the
    /// middle and at the end, removed, reworded, given another row or a comment, moved now and then, and the opening
    /// and closing lines reworded - each pair once with the text both held and once with none. The same table every
    /// run.
    /// </summary>
    private static IEnumerable<(string? Held, string Main, string Agent)> Generated()
    {
        for (var seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            var held = Document.Of(random);
            var main = held.Edited(random, "m", seed % 2);
            var agent = held.Edited(random, "a", 1 - (seed % 2));

            yield return (held.Render(), main.Render(), agent.Render());
            yield return (null, main.Render(), agent.Render());
        }
    }

    /// <summary>A registry as the generator holds one: its opening lines, its arms each with its own lines, its closing lines.</summary>
    private sealed record Document(IReadOnlyList<string> Opening, IReadOnlyList<(string Id, IReadOnlyList<string> Lines)> Arms, IReadOnlyList<string> Closing, bool Spaced)
    {
        public static Document Of(Random random)
        {
            var arms = Enumerable.Range(0, random.Next(2, 7)).Select(index => Arm($"b{index}", random)).ToList();

            return new Document(
                random.Next(3) == 0 ? [] : ["# the arms", "# of a library", ""],
                arms,
                random.Next(3) == 0 ? ["# the end"] : [],
                random.Next(4) != 0);
        }

        /// <summary>The text: the opening lines, each arm - a blank line between two where the registry is spaced - and the closing lines.</summary>
        public string Render()
            => Text([.. Opening, .. Arms.SelectMany((arm, index) => index > 0 && Spaced ? ["", .. arm.Lines] : arm.Lines), .. Closing]);

        /// <summary>
        /// The registry as one side leaves it: a few changes, most of them to the arms whose place in the text is of
        /// <paramref name="half"/>, so that two sides seldom change one arm.
        /// </summary>
        public Document Edited(Random random, string side, int half)
        {
            var arms = Arms.ToList();
            var opening = Opening;
            var closing = Closing;
            var added = 0;

            int Own() => Enumerable.Range(0, arms.Count).Where(index => index % 2 == half || random.Next(8) == 0).OrderBy(_ => random.Next()).FirstOrDefault(-1);

            for (var change = random.Next(1, 4); change > 0; change--)
            {
                switch (random.Next(12))
                {
                    case 0 or 1 or 2:
                        arms.Insert(random.Next(arms.Count + 1), Arm($"{side}{added++}", random));
                        break;

                    case 3:
                        arms.Add(Arm($"{side}{added++}", random));
                        break;

                    case 4 when arms.Count > 1 && Own() is >= 0 and var removed:
                        arms.RemoveAt(removed);
                        break;

                    case 5 or 6 when Own() is >= 0 and var reworded:
                        arms[reworded] = (arms[reworded].Id, [.. arms[reworded].Lines.Select(line => line.StartsWith("A |", StringComparison.Ordinal) ? line + $", as {side} has it" : line)]);
                        break;

                    case 7 when Own() is >= 0 and var grown:
                        arms[grown] = (arms[grown].Id, [.. arms[grown].Lines, C(arms[grown].Id, $"By{side}{change}")]);
                        break;

                    case 8 when Own() is >= 0 and var noted:
                        arms[noted] = (arms[noted].Id, [$"# a word from {side}", .. arms[noted].Lines]);
                        break;

                    case 9 when random.Next(3) == 0 && arms.Count > 1:
                        var moved = arms[^1];

                        arms.RemoveAt(arms.Count - 1);
                        arms.Insert(random.Next(arms.Count), moved);
                        break;

                    case 10 when random.Next(2) == 0:
                        opening = [$"# the arms, as {side} has them", ""];
                        break;

                    case 11 when random.Next(2) == 0:
                        closing = [$"# the end, as {side} has it"];
                        break;
                }
            }

            return this with { Arms = arms, Opening = opening, Closing = closing };
        }

        private static (string Id, IReadOnlyList<string> Lines) Arm(string id, Random random)
            => (id, [.. random.Next(3) == 0 ? new[] { $"# about {id}" } : [], A(id), C(id), .. random.Next(3) == 0 ? new[] { C(id, "Another") } : []]);
    }

    private static string Shown(RegistryMerge merge) => merge switch
    {
        RegistryMerge.Merged merged => "another text:\n" + merged.Text,
        RegistryMerge.Conflicted conflicted => "a conflict:\n" + string.Join("\n", conflicted.Conflicts.SelectMany(conflict => conflict.Said(Sides))),
        RegistryMerge.Unreadable unreadable => "an unreadable version:\n" + string.Join("\n", unreadable.Said(Sides)),
        _ => merge.ToString(),
    };

    private static string A(string id, string why = "why")
        => $"A | {id} | src/{id}.cpp | texts/{id}.before | texts/{id}.after | TEST-RED | fixture | fixture_tests | 3 | texts/{id}.diag | {why}";

    private static string C(string id, string @case = "Case") => $"C | {id} | Suite.{@case} | reddens";

    private static string Text(params string[] lines) => string.Join("\n", lines) + "\n";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The merge of three texts, each as UTF-8 bytes - or of two, where no text is given as what both held.</summary>
    private static RegistryMerge Merge(string? held, string main, string agent)
        => held is null ? MutationRegistryMerge.Merge(null, Bytes(main), Bytes(agent)) : MutationRegistryMerge.Merge(Bytes(held), Bytes(main), Bytes(agent));
}
