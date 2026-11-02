using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// The policy's ground of one reason step, built as the engine hands the step its agent to run: where the agent works
/// and what it may write, the places no action of any agent reaches, and every restriction that applies to it - the
/// configuration's, its definition's and its step's, all applied together. The one builder of a step's own ground,
/// beside the policy that decides by one.
/// </summary>
public static class AgentStepGround
{
    /// <summary>
    /// The ground of a reason step working in <paramref name="workspace"/>: its agent's worktree where it has one -
    /// which it writes below, unless its definition says <c>writes: false</c> or the run only reads, its work directory
    /// always - or, for an agent with none, the main checkout, which it only reads. The tool's own state under each tree
    /// it touches - its worktree, and always the main checkout, whose run records and other worktrees a command reaches
    /// from the worktree - git's own files beneath both under the worktree rules, and every credential slot of the main
    /// checkout (<see cref="HarnessLayout.SecretSlotDirectoriesUnder"/>), every Claude Code directory and every place the
    /// user keeps credentials in, no action of the agent's reaches.
    /// </summary>
    /// <param name="config">The configuration, whose <c>agentPolicy</c> restricts and whose state paths are guarded.</param>
    /// <param name="definition">The agent definition the step works by, where there is one; null where it names none.</param>
    /// <param name="step">The step, whose <c>restrictions</c> add and whose <c>writes</c> narrow.</param>
    /// <param name="workspace">Where the run works, as the step's agent answers it.</param>
    /// <param name="webSearchGiven">Whether the run is given a web search, which without it the agent has not.</param>
    /// <param name="claudeDirectories">Every Claude Code directory a client of this run uses, and the tool's own.</param>
    /// <param name="credentialPlaces">Every place the user keeps credentials in on this machine (<see cref="Isolation.UserCredentials.Of"/>).</param>
    /// <param name="readsOnly">
    /// Whether the run only reads whatever its definition gives it - the run's advisor, which works where the asking step
    /// works and writes nothing there, and its reporter: its own work directory is all it writes.
    /// </param>
    /// <param name="handed">
    /// Where the step's agent is handed what it reads - its skills, its continuation record's copies
    /// (<see cref="OrchestratorLayout.HandedDirectory"/>) - a root it reads and never writes; null where it is handed none.
    /// </param>
    public static AgentPolicyGround Of(
        HarnessConfig config,
        AgentDefinition? definition,
        WorkflowStep step,
        AgentWorkspace workspace,
        bool webSearchGiven,
        IReadOnlyList<string> claudeDirectories,
        IReadOnlyList<Isolation.HiddenPlace> credentialPlaces,
        bool readsOnly = false,
        string? handed = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(claudeDirectories);
        ArgumentNullException.ThrowIfNull(credentialPlaces);

        var policy = config.AgentPolicy ?? new AgentPolicyConfig();

        // Every tree the agent touches: its worktree where it has one, and always the main checkout it was made from, whose
        // state - the run's records among it - and whose git's own files a command's write reaches from the worktree.
        IReadOnlyList<string> trees = workspace.Worktree is { Length: > 0 } worktree ? Distinct([worktree, workspace.MainCheckout]) : [workspace.MainCheckout];

        // The capabilities its definition names - all four where it names none - less the web where this run is given
        // none: without it every web request is refused, and no client offers the agent another way.
        var may = definition?.May ?? AgentDefinition.AllCapabilities;
        var capabilities = may
            .Where(one => one != "web" || webSearchGiven)
            .Select(one => one switch
            {
                "read" => AgentCapability.Read,
                "write" => AgentCapability.Write,
                "run" => AgentCapability.Run,
                "web" => AgentCapability.Web,
                _ => (AgentCapability?)null,
            })
            .OfType<AgentCapability>()
            .ToList();

        var restrictions = policy.Restrictions.Select(one => new AgentRestriction(one, AgentPolicySource.Config))
            .Concat((definition?.AddedRules ?? []).Select(one => new AgentRestriction(one, AgentPolicySource.Agent)))
            .Concat(step.Restrictions.Select(one => new AgentRestriction(one, AgentPolicySource.Agent)))
            .ToList();

        // An agent with no worktree reads the main tree; one with a worktree works in it alone, its view of the main
        // tree coming by the engine.
        var reads = new List<string> { workspace.WorkingDirectory, workspace.WorkDirectory };

        if (workspace.Worktree is null)
        {
            reads.Add(workspace.MainCheckout);
        }

        if (handed is not null)
        {
            reads.Add(handed);
        }

        // A worktree is written only where the agent writes: a definition's `writes: false`, and a run that only reads,
        // leave it to be read, and the work directory is all the agent writes.
        var writable = workspace.Worktree is { Length: > 0 } && definition?.Writes != false && !readsOnly
            ? Distinct([workspace.Worktree, workspace.WorkDirectory])
            : [workspace.WorkDirectory];

        return new AgentPolicyGround(
            workspace.WorkingDirectory,
            Distinct(reads),
            writable,
            HarnessLayout.SecretSlotDirectoriesUnder(workspace.MainCheckout),
            Distinct([.. claudeDirectories]),
            credentialPlaces,
            restrictions,
            capabilities,
            policy.WebDomains is { Count: > 0 } domains ? [.. domains] : null,
            WritesOf(step),
            ReporterRoot: null,
            WorktreeRules: policy.WorktreeRules ?? true,
            ProtectedPaths: policy.ProtectedPaths ?? true,
            [.. trees.SelectMany(tree => HarnessLayout.StatePathsUnder(tree, config.Worktrees?.Root ?? WorktreeSettings.DefaultRoot)).Distinct(StringComparer.OrdinalIgnoreCase)])
        {
            Trees = trees,
        };
    }

    /// <summary>The paths a writing step may write, as its <c>writes</c> name them; null where it narrows nothing.</summary>
    /// <param name="step">The step.</param>
    private static IReadOnlyList<string>? WritesOf(WorkflowStep step)
        => step.Writes is { Written: { Count: > 0 } written } ? written : null;

    /// <summary>The paths, each once, in the order they were given.</summary>
    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> paths)
        => [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
}
