using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.SetUps;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Execution;

/// <summary>
/// The commands that pass a point of no return: once one has written, nothing interrupts it, and what it meets from there
/// on is answered as a change begun and not finished. An interrupted one goes on with what it began to its end, told while
/// it lasts - as every command goes on until its work has stopped, the command line cutting none off
/// (<see cref="CommandInterrupts"/>) - and only a second interruption, the hard stop, ends it. The one list a host agent
/// reads, so a request it serves that holds one is waited for to its end, never ended part way with nothing said.
/// </summary>
public static class PointOfNoReturn
{
    /// <summary>Every such command, by name.</summary>
    public static IReadOnlySet<string> Commands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WorktreeService.DeleteCommand,
        AgentService.CreateCommand,
        AgentService.SeedCommand,
        AgentService.RefreshCommand,
        AgentService.RebaseCommand,
        AgentService.FoldCommand,
        AgentService.DeleteCommand,
        OrchestratorService.DeleteCommand,
        HostAgentProtocol.CommandName,
        ToolUpdateService.CommandName,
    };

    /// <summary>
    /// The commands that pause safely: a first interrupt asks the engine for a safe pause and a second is the hard
    /// stop, so each is waited for as the safe pause needs and never cut off part way - the engine may stay for as
    /// long as a build takes, and a hard stop is bounded (<see cref="HardStopBound"/>). Each counts its own interrupts
    /// (<see cref="CountsItsOwnInterrupts"/>).
    /// </summary>
    public static IReadOnlySet<string> PausesSafely { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Orchestration.Flow.FlowService.RunCommand,
        Orchestration.Flow.FlowService.ResumeCommand,
        Orchestration.Flow.FlowService.AnswerCommand,
    };

    /// <summary>
    /// The commands that hold a conversation with a person and count its interrupts themselves: a side-task, whose first
    /// interrupt cuts the agent's turn and leaves its chat open, and whose second ends the chat - so the command line takes
    /// none of them for it (<see cref="CommandInterrupts"/>), as for a run. No hard stop is bounded for one: a chat ends
    /// when its person leaves it.
    /// </summary>
    public static IReadOnlySet<string> ChatsWithAPerson { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Agents.SideTasks.SideTaskService.RunCommand,
    };

    /// <summary>
    /// The engine's drain of a hard stop: the longest it waits, from applying the stop, for a stopped step's runner to
    /// end - 90 s - before recording the step stopped all the same, saying so.
    /// </summary>
    public static TimeSpan SafePauseDrain { get; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// What a session is given beyond the engine's drain to end in order: the loop applies the stop a moment after it
    /// is asked, and once the drain has passed it still writes the run's end - its state, its status, its report - and
    /// releases its owner.
    /// </summary>
    public static TimeSpan HardStopEndMargin { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest a hard stop of a run can take, from the second interrupt: the engine's drain
    /// (<see cref="SafePauseDrain"/>) and the margin for writing the end (<see cref="HardStopEndMargin"/>) - so the
    /// terminal never cuts off a session the engine is still ending in order, and the engine's own word on a runner that
    /// did not end is what the run records.
    /// </summary>
    public static TimeSpan HardStopBound { get; } = SafePauseDrain + HardStopEndMargin;

    /// <summary>
    /// Whether the command <paramref name="commandLine"/> types passes a point of no return: one of
    /// <see cref="Commands"/>, or a run asked to set up (<see cref="LegSetUps.Option"/>), whose legs bring down what they
    /// brought up however the run ends - a set-up left up runs on after every run that used it.
    /// </summary>
    /// <param name="commandLine">The command's name, then its arguments as typed.</param>
    public static bool Holds(IReadOnlyList<string> commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        if (commandLine.Count == 0)
        {
            return false;
        }

        var command = commandLine[0];

        return Commands.Contains(command)
            || (LegSetUps.Commands.Contains(command)
                && commandLine.Skip(1).TakeWhile(argument => argument != "--").Contains(LegSetUps.Option, StringComparer.Ordinal));
    }

    /// <summary>
    /// Whether the command <paramref name="commandLine"/> types counts its own interrupts - one of <see cref="PausesSafely"/>
    /// or of <see cref="ChatsWithAPerson"/>: the command line takes none of its interrupts for it
    /// (<see cref="CommandInterrupts"/>), since a first interrupt there cancels the command, and a safe pause and a chat
    /// each answer theirs otherwise. It takes Ctrl+C - and off Windows SIGTERM and a hang-up - itself, through the one
    /// registration of them (<see cref="TerminalSignals"/>): a run's session by its terminal
    /// (<see cref="Orchestration.Flow.FlowTerminal"/>), a side-task by its own (<see cref="Agents.SideTasks.SideTaskTerminal"/>).
    /// </summary>
    /// <param name="commandLine">The command's name, then its arguments as typed.</param>
    public static bool CountsItsOwnInterrupts(IReadOnlyList<string> commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        return commandLine.Count > 0 && (PausesSafely.Contains(commandLine[0]) || ChatsWithAPerson.Contains(commandLine[0]));
    }
}
