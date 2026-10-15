using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>The commands the command line waits for after Ctrl+C, rather than cut off part way.</summary>
public sealed class PointOfNoReturnTests
{
    /// <summary>
    /// What such a command still does after an interruption is stopped a little before the command line stops waiting
    /// for it - ten seconds before, or a quarter of a shorter wait - so what is left is said before the process ends.
    /// </summary>
    [Theory]
    [InlineData(120_000, 110_000)]
    [InlineData(4_000, 3_000)]
    public void WhatComesAfterAnInterruption_IsStopped_ALittleBeforeTheCommandLineStopsWaiting(int waitedMilliseconds, int stoppedMilliseconds)
        => Assert.Equal(TimeSpan.FromMilliseconds(stoppedMilliseconds), PointOfNoReturn.StoppedAfter(TimeSpan.FromMilliseconds(waitedMilliseconds)));

    /// <summary>
    /// Every command that finishes what it began once it has written - a removal, a hand-over, a move of an agent's base, a
    /// fold, a host agent, which may be running one of them for another machine, and an update of the installed tool,
    /// which is read back once it has begun - is waited for after Ctrl+C as long as a deletion expects; a command that
    /// leaves nothing half written when it stops is not.
    /// </summary>
    [Theory]
    [InlineData(WorktreeService.DeleteCommand, true)]
    [InlineData(AgentService.CreateCommand, true)]
    [InlineData(AgentService.SeedCommand, true)]
    [InlineData(AgentService.RefreshCommand, true)]
    [InlineData(AgentService.RebaseCommand, true)]
    [InlineData(AgentService.FoldCommand, true)]
    [InlineData(AgentService.DeleteCommand, true)]
    [InlineData(OrchestratorService.DeleteCommand, true)]
    [InlineData(HostAgentProtocol.CommandName, true)]
    [InlineData(ToolUpdateService.CommandName, true)]
    [InlineData(WorktreeService.ListCommand, false)]
    [InlineData(OrchestratorService.ListCommand, false)]
    [InlineData("build", false)]
    public void ACommandThatFinishesWhatItBegan_IsWaitedForAfterCtrlC_NeverCutOffPartWay(string command, bool waited)
        => Assert.Equal(waited, PointOfNoReturn.Commands.Contains(command));

    /// <summary>
    /// A run asked to set up is waited for after Ctrl+C too, for its legs to bring down what they brought up - and only
    /// where the option is the command's own, never a word after '--', and only for the commands that take it; any
    /// command on the list is waited for whatever it was given.
    /// </summary>
    [Theory]
    [InlineData(new[] { "test", "--set-up" }, true)]
    [InlineData(new[] { "run", "corpus", "--legs", "a", "--set-up" }, true)]
    [InlineData(new[] { "test" }, false)]
    [InlineData(new[] { "run", "corpus", "--", "--set-up" }, false)]
    [InlineData(new[] { "build", "--set-up" }, false)]
    [InlineData(new[] { "test", "--set-upx" }, false)]
    [InlineData(new[] { WorktreeService.DeleteCommand, "w" }, true)]
    [InlineData(new string[0], false)]
    public void ARunAskedToSetUp_IsWaitedForAfterCtrlC_AsWhatIsOnTheListIs(string[] commandLine, bool waited)
        => Assert.Equal(waited, PointOfNoReturn.Holds(commandLine));
}
