using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>The commands that finish what they began after Ctrl+C, rather than stop part way, and those that count their own interrupts.</summary>
public sealed class PointOfNoReturnTests
{
    /// <summary>
    /// Every command that finishes what it began once it has written - a removal, a hand-over, a move of an agent's base, a
    /// fold, a host agent, which may be running one of them for another machine, and an update of the installed tool,
    /// which is read back once it has begun - is waited for after Ctrl+C to the end of what it began, only the second
    /// interrupt, the hard stop, ending it; a command that leaves nothing half written when it stops is not.
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
    /// A run asked to set up is waited for after Ctrl+C too, for its legs to bring down what they brought up, each down
    /// line to its end - and only where the option is the command's own, never a word after '--', and only for the
    /// commands that take it; any command on the list is waited for whatever it was given.
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

    /// <summary>The run verbs that pause safely are on that list and no other list: a run's sessions are never cut off part way, and they pass no point of no return of their own.</summary>
    [Theory]
    [InlineData(FlowService.RunCommand, true)]
    [InlineData(FlowService.ResumeCommand, true)]
    [InlineData(FlowService.AnswerCommand, true)]
    [InlineData(FlowService.PauseCommand, false)]
    [InlineData(FlowService.StopCommand, false)]
    public void TheRunVerbsThatPauseSafely_AreOnThatListAndNoOther(string command, bool pauses)
        => Assert.Equal(pauses, PointOfNoReturn.PausesSafely.Contains(command));

    /// <summary>
    /// The run verbs that pause safely count their own interrupts, and so does a side-task - its first cutting the agent's
    /// turn, a second ending its chat - so the command line takes none of theirs, while for a deletion, the listing of
    /// side-tasks and every other command it takes them: the first cancelling the command, the second the hard stop.
    /// </summary>
    [Theory]
    [InlineData(new[] { FlowService.RunCommand, "implementation" }, true)]
    [InlineData(new[] { FlowService.ResumeCommand, "r1" }, true)]
    [InlineData(new[] { FlowService.AnswerCommand, "r1", "more" }, true)]
    [InlineData(new[] { FlowService.PauseCommand, "r1" }, false)]
    [InlineData(new[] { FlowService.StopCommand, "r1" }, false)]
    [InlineData(new[] { DssHarness.Core.Agents.SideTasks.SideTaskService.RunCommand }, true)]
    [InlineData(new[] { DssHarness.Core.Agents.SideTasks.SideTaskService.RunCommand, "--prompt", "x" }, true)]
    [InlineData(new[] { DssHarness.Core.Agents.SideTasks.SideTaskService.ListCommand }, false)]
    [InlineData(new[] { WorktreeService.DeleteCommand, "w" }, false)]
    [InlineData(new[] { "build" }, false)]
    [InlineData(new string[0], false)]
    public void TheRunVerbsThatPauseSafely_AndASideTask_CountTheirOwnInterrupts(string[] commandLine, bool counted)
        => Assert.Equal(counted, PointOfNoReturn.CountsItsOwnInterrupts(commandLine));
}
