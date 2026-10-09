using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>The commands the command line waits for after Ctrl+C, rather than cut off part way.</summary>
public sealed class PointOfNoReturnTests
{
    /// <summary>
    /// Every command that finishes what it began once it has written - a removal, a hand-over, a move of an agent's base, a
    /// fold, and a host agent, which may be running one of them for another machine - is waited for after Ctrl+C as long as
    /// a deletion expects; a command that leaves nothing half written when it stops is not.
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
    [InlineData(WorktreeService.ListCommand, false)]
    [InlineData(OrchestratorService.ListCommand, false)]
    [InlineData("build", false)]
    public void ACommandThatFinishesWhatItBegan_IsWaitedForAfterCtrlC_NeverCutOffPartWay(string command, bool waited)
        => Assert.Equal(waited, PointOfNoReturn.Commands.Contains(command));
}
