using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

public sealed partial class HelpTests
{
    /// <summary>
    /// The space topic says a leg's need, where its own tree's copy recorded none, is the most any other copy of the
    /// repository on that host recorded, named in the line - its example the line as a leg says it.
    /// </summary>
    [Fact]
    public async Task TheSpaceTopic_SaysANeedIsTheMostAnyOtherCopyRecorded_NamingWhose()
    {
        var said = Prose.Words((HelpText.Of("space")).StandardOutput);

        Assert.Contains(
            "in this tree's copy, or else the most any other copy of the repository on that host recorded - the main checkout's, a "
            + "worktree's, an agent's or a plain one, or a leg copy of any of them, named in the line - less what the directory already "
            + "holds.",
            said,
            StringComparison.Ordinal);
        Assert.Contains(
            "ssh vps: this leg needs ~11.4 GiB, what worktree o1/xa's copy of the same variant came to there; 3.2 GiB free on '/'",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The space topic says clean --gone-copies removes only the copies of trees gone from this machine, told by the tree and
    /// never by name, driven by the record and not the legs - a line for each host it names, narrowed by --legs - how each
    /// copy goes and what keeps one, what a host that cannot be reached or that nothing declares any more does, what its
    /// dry run does, that every line names the record, and that it is not given with --former-workers.
    /// </summary>
    [Fact]
    public async Task SpaceTopic_SaysGoneCopiesRemovesOnlyTheCopiesOfGoneTrees_ByTheirTree()
    {
        var result = HelpText.Of("space");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "clean --gone-copies removes only the copies hosts keep of worktrees gone from this machine, told by the tree this "
            + "machine's record of host copies names - never by the name a copy is kept under, so a worktree here that bears a "
            + "gone one's name, a live agent's among them, keeps its own copies. It is driven by that record, not by the legs: "
            + "every host the record names such a copy on is asked, one no declared leg runs on any more among them, each on a "
            + "line of its own named as the record spells the host, in place of the legs' lines; --legs narrows it to the hosts "
            + "the selected legs can run on. A tree is gone only where that is seen: its path is whole on this machine, nothing "
            + "is at it, and the directory it was in - for an orchestrator's agent, the one its orchestrator's was in, once that "
            + "is gone too - is there to look in. One that cannot be looked at - a path spelt for another system, a drive or a "
            + "share that is not mounted, a directory this user may not look in - is never taken for gone: its copies stay "
            + $"recorded, each kept, refused-locked ({HarnessExit.Refused}), its line saying why. Each copy goes as deleting its "
            + "worktree would remove it, at the path the record names, under the same lock: a "
            + "copy the harness took over, or one nothing marks as its own, is forgotten and left where it is, one the host no "
            + $"longer keeps is forgotten, and one a run or a sweep holds is kept, refused-locked ({HarnessExit.Refused}), as is "
            + "one whose marker cannot be read. A host that cannot be reached is said on its line, unavailable, its copies kept "
            + $"recorded for a later clean, and the next host is asked all the same ({HarnessExit.Incomplete}); one no "
            + "configuration here declares any more can never be asked, so its copies are forgotten and left on it for you to "
            + "remove, as deleting a worktree forgets them. With --dry-run it says what each holds and what removing it would "
            + "do, measured at that same path - beside the repositoryPath it was kept beside, whatever the host's item or "
            + "config.json name now - and removes and forgets nothing. Every line names the record, and so does the line the clean ends on - one "
            + $"saying it records none where it does. It cannot be given with --former-workers ({HarnessExit.UsageError}).",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The space topic says what clean leaves alone and why it frees a full disk: nothing is written first,
    /// a build of the leg holds it off, and a host behind this machine's build needs room to be updated.
    /// </summary>
    [Fact]
    public async Task SpaceTopic_SaysCleanWritesNothingFirst_AndWhatItLeavesAlone()
    {
        var result = HelpText.Of("space");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("It writes nothing where it removes before it has removed", text, StringComparison.Ordinal);
        Assert.Contains("refused-locked", text, StringComparison.Ordinal);
        Assert.Contains("--dry-run", text, StringComparison.Ordinal);
        Assert.Contains("A build directory that is a link is left alone", text, StringComparison.Ordinal);
        Assert.Contains("both full and behind has to be freed by hand once", text, StringComparison.Ordinal);
        Assert.Contains("A leg is placed only where its host has the room its build still needs", text, StringComparison.Ordinal);
        Assert.Contains("the leg's buildSpaceGiB", text, StringComparison.Ordinal);
        Assert.Contains("Commands that build nothing - sync, clean - need no room.", text, StringComparison.Ordinal);
        Assert.Contains(
            "all of it as each leg's 'space', with its mutation workers' as 'workerBytes' and its copies' as 'copyBytes': what "
                + "was removed of them - 0 where every one there was kept - or, in a dry run, what is there. A leg whose build "
                + "directory was not measured - a link, locked or not removable - carries a space saying its workers and copies "
                + "alone, where any is there.",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "one a live sweep claims is kept, one that cannot be removed fails the leg, which still says what went, and a directory "
                + "under a worker's name that no sync made is said and left",
            text,
            StringComparison.Ordinal);
        Assert.Contains("The mutation workers a sweep of the leg keeps in that tree go with it", text, StringComparison.Ordinal);
        Assert.Contains(
            "The copies of the tree its legs work in, in the tree's .legs slot, go the same way - the leg's own variant's and "
                + "those of a variant no leg builds any more, whatever the configuration says now",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "a leg a run is building, or whose workers a sweep holds, or whose copy a run holds, is refused-locked in the dry run "
                + "too",
            text,
            StringComparison.Ordinal);
        Assert.Contains("A host holding no copy of the tree holds nothing of it to remove.", text, StringComparison.Ordinal);

        Assert.Contains("legs -v' says the room on each host it measured", text, StringComparison.Ordinal);
    }
}
