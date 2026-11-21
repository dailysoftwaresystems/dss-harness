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
    /// The exit codes topic gives the run verbs' own code - a question waiting is 1, which a wrapper that retries 21 would
    /// otherwise loop on - beside what else a session ends with, from the constants the session exits by.
    /// </summary>
    [Fact]
    public void ExitCodeTopic_SaysARunPausedOnAQuestionExitsOne()
    {
        var result = HelpText.Of("exit-codes");

        Assert.Contains($"  {FlowService.RunCommand}, {FlowService.ResumeCommand}, {FlowService.AnswerCommand}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"      {FlowExit.Paused}  paused: a question waits - a step's, or the budget's at its reserve - for {FlowService.AnswerCommand}", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "A session otherwise ends 0 done, 20 failed, 21 failed where every failure that ended it was incomplete after its retries, and 130 at a safe pause or a stop",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The exit codes topic, and the table the architecture keeps by hand, say that 21 is also an update of a program begun
    /// and not finished - not finished within its bound, or its download not reached - which both updaters exit with: a
    /// code whose words named only a leg, a set-up and an agent's verbs was what each updater answered differently. And
    /// that it is a remote workflow's fetch not finished within its bound and the workflow cache not read, written or
    /// locked, which the cache's commands, validate-workflow and export-workflow exit with.
    /// </summary>
    [Fact]
    public void ExitCodeTopic_SaysAnUpdateStoppedPartWayIsIncomplete()
    {
        Assert.Equal(HarnessExit.Incomplete, WorkflowCache.FailedExit);

        const string Said = "or a deletion, a fold, a hand-over, a move of an agent's base or an update of a program stopped part way (not "
            + "finished within its bound, or its download not reached), or a remote workflow's fetch was not finished within its "
            + "bound or the workflow cache could not be read, written or locked within its window";

        Assert.Contains(Said, Prose.Words(HelpText.Of("exit-codes").StandardOutput), StringComparison.Ordinal);
        Assert.Contains($"| {HarnessExit.Incomplete} | Ran with nothing failing, but a leg reached no verdict or left a set-up it brought up running, {Said};", Prose.Document("docs", "architecture.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCodeTopic_DocumentsEverySharedExitCode()
    {
        var result = HelpText.Of("exit-codes");

        // Generated from the constants, so a new exit code cannot ship undocumented.
        foreach (var description in HarnessExit.All)
        {
            Assert.Contains(description.Name, result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains(
                description.Code.ToString(CultureInfo.InvariantCulture),
                result.StandardOutput,
                StringComparison.Ordinal);
        }
    }

    /// <summary>The overview lists update-tool among the commands a machine is set up with.</summary>
    [Fact]
    public async Task TheOverview_ListsUpdateTool()
    {
        var result = HelpText.Of(null);

        Assert.Contains("  dssharness update-tool             Update this machine's DssHarness, while none runs here", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_ExplainsTheGlobalOptions()
    {
        var result = HelpText.Of(null);

        Assert.Contains("--directory", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--verbose", result.StandardOutput, StringComparison.Ordinal);
    }
}
