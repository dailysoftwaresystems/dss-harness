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
}
