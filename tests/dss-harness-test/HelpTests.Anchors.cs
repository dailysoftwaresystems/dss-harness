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
    [Fact]
    public async Task AnchorsTopic_DocumentsEveryStatus_Command_AndDefault()
    {
        var result = HelpText.Of("anchors");


        // Each command as its list gives it, with what it takes: named in prose alone, one was missing
        // from the list a reader scans.
        var expected = AnchorStatus.Cells
            .Concat(["write-anchor", "set-anchor", "read-anchor", "read-anchors", "check-anchor-balance [--base REF]"])
            .Concat(["check-anchor-citations --current-commit|--current-tree|--current-pr", "--anchor-dry-run"])
            .Concat([AnchorSettings.DefaultPendingAnchorsPath, AnchorSettings.DefaultDoneAnchorsPath, AnchorRegistryDocument.TableHeader]);

        foreach (var text in expected)
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }

        // Closing by record: where anchors.closure says 'required', a lane folded by hand closes its classes by record -
        // closure.json judged by the rules the run's class-closure check reads, its searches and measurements read as given.
        Assert.Contains(
            "Closing by record. Where anchors.closure says 'required', a lane folded by hand",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "refused unless its directory holds closure.json, the closing record, read against",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "A manual lane has no records of its calls, so its searches",
            result.StandardOutput,
            StringComparison.Ordinal);

        // An empty record closes nothing, a lane's arm is its worktree's, and a run's own folds are judged by the check.
        Assert.Contains(
            "so; a record that names no class closes no row, and a class's arm is named against the",
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            "registry the lane's worktree holds. A run's own folds are held to no closure.json:",
            result.StandardOutput,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The balance says what it measures from, what it does not credit and what it refuses - a base that moved on, an
    /// unfinished merge, the bookkeeping pair, the open copy of an anchor closed already, an id with two rows, a lost row
    /// and the one it only notes - where the anchors are described.
    /// </summary>
    [Fact]
    public async Task AnchorsTopic_StatesWhatTheBalanceMeasuresFrom_AndDoesNotCredit()
    {
        var result = HelpText.Of("anchors");

        var prose = Prose.Words(result.StandardOutput);

        Assert.Contains("A base that has moved on since HEAD left it is measured from where HEAD left it", prose, StringComparison.Ordinal);
        Assert.Contains(
            $"a closure whose Trigger opens with {AnchorStatus.ClosedMark}{AnchorStatus.BookkeepingMark} records work that already existed",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("an anchor open or closed where the change began is in neither registry now", prose, StringComparison.Ordinal);
        Assert.Contains("a lost one that was open is not credited either", prose, StringComparison.Ordinal);
        Assert.Contains("Nor is it for taking away the open row of an anchor that had a closed row already", prose, StringComparison.Ordinal);
        Assert.Contains("an id has more than one row", prose, StringComparison.Ordinal);
        Assert.Contains("one whose Anchor cell named no id, or more than one, is noted instead", prose, StringComparison.Ordinal);
        Assert.Contains("during an unfinished merge, HEAD is taken merged with what the merge brings in", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The citation check says what it cannot see - a break inside a segment, with no hyphen on either
    /// side - both where its command is described and in the topic that lists it.
    /// </summary>
    [Fact]
    public async Task TheCitationCheck_StatesTheOneWrapItCannotSee()
    {
        // A command's own --help is built by the command line from where it declares the command, so only the tool itself
        // prints it; the topic is the core's, and is asked of its renderer.
        var described = await CliRunner.RunAsync(["check-anchor-citations", "--help"], TestContext.Current.CancellationToken);
        var listed = HelpText.Of("anchors");

        Assert.Equal(HarnessExit.Success, described.ExitCode);
        // Read as prose: a description is wrapped to the terminal, with its continuation indented.
        Assert.Contains("with no hyphen on either side", Prose.Words(described.StandardOutput), StringComparison.Ordinal);
        Assert.Contains("with no hyphen on either side", Prose.Words(listed.StandardOutput), StringComparison.Ordinal);
    }

    /// <summary>
    /// The anchors topic says what has no bound of the code's own: the registries' lock is waited for while its holder
    /// lives, told as it begins and every five minutes; every value is given whole and every same-namespace id named; a
    /// row with no id is told apart by its whole cell; <c>\x</c> takes any count of hex digits; a history form's commit is
    /// spelled as git spells one; and the arms registry's whole history is read.
    /// </summary>
    [Fact]
    public void AnchorsTopic_SaysWhatHasNoBoundOfTheCodesOwn()
    {
        var topic = Prose.Words(HelpText.Of("anchors").StandardOutput);

        foreach (var text in new[]
        {
            "A change or a read that finds the lock held waits for as long as its holder lives - a holder that ends lets it go - "
                + $"saying so as it begins, under '{NamedMutexAnchorRegistryLock.TellsAs}', and every "
                + $"{ReportText.Limit(DssHarness.Core.Waiting.Waits.SaidEvery)} while it lasts; an interruption ends the wait, nothing read or written.",
            "is given whole, never cut short, and a change of a cell is said word by word however long the cell",
            "an id read-anchor does not find names every id of its namespace that a registry holds",
            "A row with no id in its Anchor cell is told from another by the whole of that cell's text.",
            "or \\x41 with as many hex digits as C lets \\x take, which is any count - is a citation",
            "a commit spelled as git spells one, in 4 to 64 hex characters",
            "the registry's whole history read, under every name it had",
        })
        {
            Assert.Contains(text, topic, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("seconds writes nothing", topic, StringComparison.Ordinal);
    }
}
