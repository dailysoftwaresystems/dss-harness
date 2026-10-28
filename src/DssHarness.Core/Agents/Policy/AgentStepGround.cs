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
    /// which it writes below, its work directory always - or, for an agent with none, the main checkout, which it only
    /// reads. The tool's own state under each tree it touches, and the slot its keys are read from, no action of the
    /// agent's reaches.
    /// </summary>
    /// <param name="config">The configuration, whose <c>agentPolicy</c> restricts and whose state paths are guarded.</param>
    /// <param name="definition">The step's agent definition, where it names one; null where it names none.</param>
    /// <param name="step">The step, whose <c>restrictions</c> add and whose <c>writes</c> narrow.</param>
    /// <param name="workspace">Where the run works, as the step's agent answers it.</param>
    /// <param name="webSearchGiven">Whether the run is given a web search, which without it the agent has not.</param>
    /// <param name="claudeDirectories">Every Claude Code directory a client of this run uses, and the tool's own.</param>
    /// <param name="slotDirectory">The directory the agents' key slot reads.</param>
    public static AgentPolicyGround Of(
        HarnessConfig config,
        AgentDefinition? definition,
        WorkflowStep step,
        AgentWorkspace workspace,
        bool webSearchGiven,
        IReadOnlyList<string> claudeDirectories,
        string slotDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(claudeDirectories);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotDirectory);

        var policy = config.AgentPolicy ?? new AgentPolicyConfig();
        var trees = new List<string>();

        if (workspace.Worktree is { Length: > 0 } worktree)
        {
            trees.Add(worktree);
        }
        else
        {
            trees.Add(workspace.MainCheckout);
        }

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

        var writable = workspace.Worktree is { Length: > 0 }
            ? Distinct([workspace.Worktree, workspace.WorkDirectory])
            : [workspace.WorkDirectory];

        return new AgentPolicyGround(
            workspace.WorkingDirectory,
            Distinct(reads),
            writable,
            [slotDirectory],
            Distinct([.. claudeDirectories]),
            restrictions,
            capabilities,
            policy.WebDomains is { Count: > 0 } domains ? [.. domains] : null,
            WritesOf(step),
            ReporterRoot: null,
            WorktreeRules: policy.WorktreeRules ?? true,
            ProtectedPaths: policy.ProtectedPaths ?? true,
            [.. trees.SelectMany(HarnessLayout.StatePaths).Distinct(StringComparer.OrdinalIgnoreCase)])
        {
            // The main checkout is read by an agent that has no worktree; written by none of them.
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
