using RepoHarness.Core.Hosts;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Execution;

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
    };
}
