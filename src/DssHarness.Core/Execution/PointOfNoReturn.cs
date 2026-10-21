using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.SetUps;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Execution;

/// <summary>
/// The commands that pass a point of no return: once one has written, nothing interrupts it, and what it meets from there
/// on is answered as a change begun and not finished. An interrupted one is waited for as long as a deletion expects
/// (<see cref="WorktreeService.DefaultInterruptionGrace"/>), never cut off part way - the one list, which the command line
/// reads, so no command that finishes what it began can be left out of the wait.
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
    /// How long, from an interruption, what such a command still does may run before it is stopped: a little short of
    /// <paramref name="grace"/>, the time the command line waits for it - by ten seconds, or by a quarter of a shorter
    /// grace - so what is left is said before the process ends, rather than the process ending in the middle without a
    /// word.
    /// </summary>
    /// <param name="grace">How long the command line waits for the command once interrupted.</param>
    public static TimeSpan StoppedAfter(TimeSpan grace)
        => grace - TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(10).Ticks, grace.Ticks / 4));

    /// <summary>
    /// Whether the command <paramref name="commandLine"/> types counts its own interrupts - one of <see cref="PausesSafely"/>:
    /// the command line's own handling of termination is turned off for it, since that handling cancels the command at
    /// the first interrupt and starts a clock that ends the process, and a safe pause has no such longest. Its session
    /// takes Ctrl+C - and off Windows SIGTERM and a hang-up - itself (<see cref="Orchestration.Flow.FlowTerminal"/>).
    /// </summary>
    /// <param name="commandLine">The command's name, then its arguments as typed.</param>
    public static bool CountsItsOwnInterrupts(IReadOnlyList<string> commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        return commandLine.Count > 0 && PausesSafely.Contains(commandLine[0]);
    }

    /// <summary>
    /// How long the command line waits for the command <paramref name="commandLine"/> types once interrupted: as long
    /// as a deletion expects where it passes a point of no return, as long as a hard stop of a run can take
    /// (<see cref="HardStopBound"/>) - from its second interrupt, the session itself timing it - where it pauses safely,
    /// and this tool's own answer of none
    /// where it does neither - <see langword="null"/>, which leaves the command line's own short wait as it is. A front
    /// end that holds a window open while a command runs holds it by this, for as long as the command asks.
    /// </summary>
    /// <param name="commandLine">The command's name, then its arguments as typed.</param>
    public static TimeSpan? GraceFor(IReadOnlyList<string> commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        if (CountsItsOwnInterrupts(commandLine))
        {
            return HardStopBound;
        }

        return Holds(commandLine) ? WorktreeService.DefaultInterruptionGrace : null;
    }

    /// <summary>
    /// The process termination timeout the command line runs the command <paramref name="commandLine"/> types with - the
    /// one choice of it, which the command line makes by this: <see langword="null"/> for a command that counts its own
    /// interrupts (<see cref="CountsItsOwnInterrupts"/>), which turns the command line's own handling of termination off
    /// - it cancels at the first interrupt and ends the process when its clock runs out, and a safe pause has no such
    /// longest; the grace for one that holds one (<see cref="GraceFor"/>); and the command line's own,
    /// <paramref name="commandLines"/>, as it is, for every other.
    /// </summary>
    /// <param name="commandLine">The command's name, then its arguments as typed.</param>
    /// <param name="commandLines">The command line's own termination timeout, which a command of neither kind keeps.</param>
    public static TimeSpan? TerminationTimeoutFor(IReadOnlyList<string> commandLine, TimeSpan? commandLines)
        => CountsItsOwnInterrupts(commandLine) ? null : GraceFor(commandLine) ?? commandLines;
}
