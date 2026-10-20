using System.Globalization;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Legs;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The start of a run and of every session of it: one unfinished run at a time, the run's name, its record and its
/// first status - all decided inside the one repository-wide section, so two starts can never both pass the test, two
/// can never take one name, and <c>--run</c> never takes over a run that has finished.
/// </summary>
/// <remarks>
/// <para>
/// The test is one critical section for the whole repository, <see cref="AcrossRuns"/>: <see cref="OrchestrationStore.Exclusively"/>
/// will not do, since it is a section for one orchestrator's record, and two runs - two orchestrators - would take two
/// different ones and both pass. A session is refused (13) while any other run of the repository is unfinished and has
/// work in the tree: a live owner, or - paused, stopped or failed - an agent with a worktree still open or a fold in
/// its ledger not yet committed. Two would fold into one main tree, and that is as true of a run waiting on an answer
/// as of one that is running.
/// </para>
/// <para>
/// The test is made at the start of every session - a run, a resume and an answer - and leaves out the run the session
/// is for: so a resume or an answer of <c>r2</c> while <c>r1</c> holds work in the tree is refused as a second run is,
/// and no two engines ever fold into one main tree.
/// </para>
/// </remarks>
/// <param name="store">Reads and writes the records, strictly.</param>
/// <param name="fileSystem">Reads the runs' directories.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="clock">When the run's record and its first status are written.</param>
public sealed class FlowSessions(OrchestrationStore store, IFileSystem fileSystem, IProcessIdentity identity, TimeProvider? clock = null)
{
    private readonly OrchestrationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IProcessIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// The one repository-wide section: no other process decides about the repository's runs at once. The scan for
    /// unfinished runs, the choice of a run's name and the creation of its record are each made inside it.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="work">The reading, the decision and the writing, all synchronous.</param>
    public T AcrossRuns<T>(HarnessLayout layout, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(work);

        return _store.AcrossRuns(layout, work);
    }

    /// <summary>
    /// Starts a run: inside the repository's one section, the test for another unfinished run, the name - a
    /// <paramref name="wantedName"/> that is taken refused (13, as <c>MustBeNew</c> refuses it: naming its record and
    /// the way on with that one), else the first free <c>r&lt;N&gt;</c> - the run's record (model <c>flow-engine</c>,
    /// parallel the workflow's open-agent bound), and its first status, every step of the definition NotStarted.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="wantedName">The name <c>--run</c> gave, or null for the first free r&lt;N&gt;.</param>
    /// <param name="inputs">The values the run is started with, by input name.</param>
    /// <param name="startCommit">The commit the run starts from; null where it starts beside the tree's history.</param>
    /// <param name="branch">The branch the run starts on.</param>
    /// <returns>The run's layout and what the run is, as its records name it.</returns>
    /// <exception cref="HarnessException">Another run of the repository is unfinished, or the name is taken (<see cref="HarnessExit.Refused"/>).</exception>
    public (OrchestratorLayout Run, FlowRunContext Context) Create(
        HarnessLayout layout,
        WorkflowDefinition definition,
        string? wantedName = null,
        IReadOnlyDictionary<string, string>? inputs = null,
        string? startCommit = null,
        string? branch = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(definition);

        return AcrossRuns(layout, () =>
        {
            RefuseAnotherUnfinished(layout, leavingOut: null);

            var name = wantedName is { } given
                ? _fileSystem.KindOf(OrchestratorLayout.Of(layout, given).RecordFile) == PathKind.File
                    ? throw new HarnessException(
                        HarnessExit.Refused,
                        $"run '{given}' exists; --run names a new run, and resume-orchestrator {given} goes on with that one")
                    : given
                : FirstFree(layout);

            var run = OrchestratorLayout.Of(layout, name);

            _fileSystem.CreateDirectory(run.Directory);
            _store.WriteOrchestrator(
                run,
                new OrchestratorRecord
                {
                    Kind = OrchestratorRecord.KindName,
                    Name = name,
                    Model = "flow-engine",
                    Parallel = definition.MaxOpenAgents ?? definition.MaxReasoningAgents ?? 2,
                    CreatedAt = _clock.GetUtcNow(),
                });

            var context = FlowRunContext.Of(name, definition, inputs, startCommit, branch, _clock.GetUtcNow());
            new WorkflowStatusWriter(_store, run, context).Write(
                new FlowSnapshot(
                    FlowRunStatus.Running,
                    null,
                    new FlowBudgetSpent(),
                    0,
                    null,
                    [],
                    [],
                    []),
                _clock.GetUtcNow());

            return (run, context);
        });
    }

    /// <summary>
    /// The test at the start of every session - a run, a resume and an answer alike - leaving out the run the session
    /// is for: refused (13) where any other run of the repository is unfinished and has work in the tree, naming it and
    /// the ways on with it.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="run">The run the session is for, left out of the test.</param>
    /// <exception cref="HarnessException">Another run of the repository is unfinished (<see cref="HarnessExit.Refused"/>).</exception>
    public void Guard(HarnessLayout layout, string run)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);

        _ = AcrossRuns(layout, () =>
        {
            RefuseAnotherUnfinished(layout, run);
            return false;
        });
    }

    /// <summary>Refuses where any run but <paramref name="leavingOut"/> is unfinished and has work in the tree: a live owner, or a paused, stopped or failed run whose agents still hold worktrees.</summary>
    private void RefuseAnotherUnfinished(HarnessLayout layout, string? leavingOut)
    {
        foreach (var other in _store.Orchestrators(layout))
        {
            if (string.Equals(other.Name, leavingOut, StringComparison.Ordinal))
            {
                continue;
            }

            if (Unfinished(other) is { } why)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"run '{other.Name}' of this repository is unfinished ({why}): resume-orchestrator {other.Name} or answer-orchestrator {other.Name} goes on with it, delete-orchestrator {other.Name} ends it");
            }
        }
    }

    /// <summary>Why <paramref name="other"/> is unfinished and holds work in the tree; null where it is not the run this test refuses.</summary>
    private string? Unfinished(OrchestratorLayout other)
    {
        if (_store.ReadOwner(other) is { } held)
        {
            if (_identity.Stands(held.Machine, held.ProcessId, held.Stamp))
            {
                return "running; owned by a live engine";
            }
        }

        var file = _store.ReadFlow(other);

        if (file is null || file.Status is not ("Running" or "Paused" or "Stopped" or "Failed"))
        {
            return null;
        }

        var open = _store.Agents(other)
            .Count(entry => entry.Record is { State: AgentStates.Live } record && record.Worktree is not null);

        return open > 0
            ? $"{file.Status.ToLower(CultureInfo.InvariantCulture)}; {open.ToString(CultureInfo.InvariantCulture)} agent{(open == 1 ? string.Empty : "s")} with worktree{(open == 1 ? string.Empty : "s")} still open"
            : null;
    }

    /// <summary>The first free <c>r&lt;N&gt;</c>: one more than the highest taken, so a deleted run's name is never reused and the count never runs back.</summary>
    private string FirstFree(HarnessLayout layout)
    {
        var highest = 0;

        if (_fileSystem.KindOf(layout.OrchestratorsDirectory) != PathKind.None)
        {
            foreach (var directory in _fileSystem.EnumerateDirectories(layout.OrchestratorsDirectory))
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

                if (name.Length > 1 && name[0] == 'r' && int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var taken))
                {
                    highest = Math.Max(highest, taken);
                }
            }
        }

        return $"r{(highest + 1).ToString(CultureInfo.InvariantCulture)}";
    }
}

/// <summary>
/// The path budget at a run's start: the longest worktree path the run makes - counted with each
/// <c>.legs/&lt;key&gt;</c> copy its legs work in and each <c>.mutations/&lt;key&gt;&lt;mark&gt;-&lt;n&gt;</c> worker its
/// sweeps keep, as a copy's own check counts the build below it - stays within this machine's path limit, with the
/// margin to spare. A copy whose build passes the limit fails as compile errors in files nobody touched, so it is
/// asked before the run makes one.
/// </summary>
public static class FlowRunPaths
{
    /// <summary>
    /// Checks the longest path the run's worktrees and the copies and workers inside them make, against the margin and
    /// the limit the repository's settings declare.
    /// </summary>
    /// <param name="budget">Reckons a path against this machine's limit.</param>
    /// <param name="settings">The repository's worktree settings: the margin, and a limit of its own where it declares one.</param>
    /// <param name="worktreesRoot">Where the run's agents' worktrees are made.</param>
    /// <param name="agentName">The longest agent name the run makes.</param>
    /// <param name="legKeys">Each leg key a leg of the run works under, <c>.legs/&lt;key&gt;</c> below its tree.</param>
    /// <param name="mutationKeys">Each mutation key a sweep of the run keeps a worker under, with its mark and count.</param>
    /// <param name="reserve">The longest path a build of the project makes below its build directory.</param>
    /// <returns>Whether the longest path stays within the budget, with what it needs and what it may have.</returns>
    public static PathBudgetResult Check(
        IPathBudget budget,
        WorktreeSettings settings,
        string worktreesRoot,
        string agentName,
        IReadOnlyList<string> legKeys,
        IReadOnlyList<string> mutationKeys,
        int reserve)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // The deepest tree the run works in: the longest agent's worktree.
        var tree = Path.Combine(worktreesRoot, agentName);

        // The longest thing kept inside a tree the run works in: a copy a leg works in, or a mutation worker - each
        // below its tree, each with a variant's build directory below it and the build below that.
        var inside = 0;

        foreach (var key in legKeys)
        {
            var copy = Path.Combine(tree, HarnessLayout.LegsDirectoryName, key);
            inside = Math.Max(inside, copy.Length + 1 + reserve);
        }

        foreach (var key in mutationKeys)
        {
            var worker = Path.Combine(tree, HarnessLayout.MutationsDirectoryName, key);
            inside = Math.Max(inside, worker.Length + 1 + reserve);
        }

        return budget.Check(tree, inside, settings.PathBudgetMargin, settings.PathLimit);
    }
}
