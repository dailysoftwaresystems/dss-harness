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
    /// The ground of a reason step working in <paramref name="workspace"/>: its agent's worktree, where it starts and which
    /// it writes below, unless its definition says <c>writes: false</c> or the run only reads, its work directory always.
    /// An agent that only reads (<see cref="AgentWorkspace.OnlyReads"/>) writes its tree whatever its definition says - the
    /// tree is its own scratch, the main checkout's HEAD as its step started, where it builds and tests, and nothing of it is
    /// ever folded - and reads the main checkout besides, whose run records and logs it is pointed at; one whose record an
    /// older tool wrote with no tree reads the main checkout alone. Every agent reads the run's inputs
    /// (<see cref="OrchestratorLayout.InputsDirectoryOf"/>), the copies of the files the run's own file inputs named as it
    /// started, which its steps read for those inputs, wherever its own tree was cut from. The tool's own state under each
    /// tree it touches - its worktree, and always the main checkout, whose run records and other worktrees a command reaches
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

        // An agent that only reads reads the main checkout beside its own tree - the run's records and logs are there - and
        // so does a run with no tree; one whose work is folded works in its worktree alone, its view of the main tree coming
        // by the engine.
        var reads = new List<string> { workspace.WorkingDirectory, workspace.WorkDirectory };

        if (workspace.Worktree is null || workspace.OnlyReads)
        {
            reads.Add(workspace.MainCheckout);
        }

        if (handed is not null)
        {
            reads.Add(handed);
        }

        // The files the run's own file inputs named, copied as the run started: every agent of the run reads them, one whose
        // work is folded as well as one that only reads, whichever commit its tree was cut from - a root it reads and never
        // writes, in the run's directory beside what it is handed.
        reads.Add(OrchestratorLayout.InputsDirectoryOf(workspace.RunDirectory));

        // A worktree is written only where the agent writes: a definition's `writes: false`, and a run that only reads,
        // leave it to be read, and the work directory is all the agent writes. The tree of an agent that only reads is its
        // own scratch, written whatever its definition says, since nothing of it is ever folded: only a run that only reads
        // whatever its definition gives it - the advisor in that tree - leaves it to be read.
        var writable = workspace.Worktree is { Length: > 0 } && (definition?.Writes != false || workspace.OnlyReads) && !readsOnly
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
            RunDirectory = workspace.RunDirectory,
        };
    }

    /// <summary>
    /// The ground a run's own file input is decided on (<see cref="FlowInputs.OpenFiles"/>): an agent's read of the main
    /// checkout, for every agent of the run reads the copy - the ground of a run of no tree, definition or step of its own
    /// over the main checkout (<see cref="Of"/>), so every place no agent's read reaches is unreached here as it is there:
    /// every credential slot of the main checkout, every Claude Code directory, every place the user keeps credentials in -
    /// <c>agentPolicy.hiddenPlaces</c> among them, inside the repository or not - and every read the configuration's
    /// <c>agentPolicy.restrictions</c> refuse. Its one root is the main checkout, for a file input names a file inside the
    /// repository - every root an agent's own of the run's directory lies inside it - and nothing is written on it.
    /// </summary>
    /// <param name="config">The configuration the run starts under.</param>
    /// <param name="mainCheckout">The main checkout's root, which each file input is read from.</param>
    /// <param name="claudeDirectories">Every Claude Code directory a client of this run uses, and the tool's own (<see cref="ReasonLaunch.ClaudeDirectories"/>).</param>
    /// <param name="credentialPlaces">Every place the user keeps credentials in on this machine (<see cref="ReasonLaunch.CredentialPlaces"/>).</param>
    public static AgentPolicyGround OfFileInputs(HarnessConfig config, string mainCheckout, IReadOnlyList<string> claudeDirectories, IReadOnlyList<Isolation.HiddenPlace> credentialPlaces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckout);

        var read = Of(
            config,
            definition: null,
            new WorkflowStep { Name = "inputs", Type = "reason" },
            new AgentWorkspace(Worktree: null, WorkDirectory: mainCheckout, MainCheckout: mainCheckout, ExtraRoots: [], WorkingDirectory: mainCheckout, RunDirectory: mainCheckout),
            webSearchGiven: false,
            claudeDirectories,
            credentialPlaces,
            readsOnly: true);

        // A decision and no run: no run's directory, and nothing held of it.
        return read with { ReadRoots = [], WritableRoots = [], RunDirectory = null };
    }

    /// <summary>The paths a writing step may write, as its <c>writes</c> name them; null where it narrows nothing.</summary>
    /// <param name="step">The step.</param>
    private static IReadOnlyList<string>? WritesOf(WorkflowStep step)
        => step.Writes is { Written: { Count: > 0 } written } ? written : null;

    /// <summary>The paths, each once, in the order they were given.</summary>
    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> paths)
        => [.. paths.Distinct(StringComparer.OrdinalIgnoreCase)];
}
