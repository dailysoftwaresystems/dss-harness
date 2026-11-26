using System.Globalization;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The start of a run and of every session of it: one unfinished run at a time, the run's name, its record, its first
/// status and its owner - all decided inside the one repository-wide section, so two starts can never both pass the
/// test, two can never take one name, two sessions can never both claim one run, and <c>--run</c> never takes over a
/// run that has finished or a directory that holds none.
/// </summary>
/// <remarks>
/// <para>
/// The test is one critical section for the whole repository, <see cref="AcrossRuns"/>: <see cref="OrchestrationStore.Exclusively"/>
/// will not do, since it is a section for one orchestrator's record, and two runs - two orchestrators - would take two
/// different ones and both pass. A session is refused (13) while any other run of the repository is unfinished and has
/// work in the tree: a live owner, or - paused, stopped or failed - an agent with a worktree still open, whose work is
/// folded (an agent that only reads folds nothing of its own tree, and holds no work of the tree's), or a fold in
/// its ledger not yet committed - an intent nobody settled, or a path it changed that the main tree still holds
/// otherwise than its HEAD (<see cref="FoldLedger.Uncommitted"/>), so a commit made by hand ends it as the run's own
/// does. Two would fold into one main tree, and that is as true of a run waiting on an answer
/// as of one that is running. What cannot be told of another run - an agent whose record does not read - refuses as
/// work in the tree does, by a sentence that says so: it is never read as nothing.
/// </para>
/// <para>
/// The test is made at the start of every session - a run, a resume and an answer - and leaves out the run the session
/// is for: so a resume or an answer of <c>r2</c> while <c>r1</c> holds work in the tree is refused as a second run is,
/// and no two engines ever fold into one main tree. The owner is claimed in the same section, after the test: a run
/// is never without an owner between the section and the claim, where another session's test would read it as idle.
/// </para>
/// </remarks>
/// <param name="store">Reads and writes the records, strictly.</param>
/// <param name="fileSystem">Reads the runs' directories.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="git">Reads what the main tree holds otherwise than its HEAD, which says whether a run's folds are committed.</param>
/// <param name="platform">The platform, whose spellings of a path a run's own file input is refused as an agent's read is (<see cref="FlowInputs.OpenFiles"/>).</param>
/// <param name="clock">When the run's record, its first status and its owner are written.</param>
public sealed class FlowSessions(OrchestrationStore store, IFileSystem fileSystem, IProcessIdentity identity, IGitClient git, IHostPlatform platform, TimeProvider? clock = null)
{
    private readonly OrchestrationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IProcessIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IHostPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// The one repository-wide section: no other process decides about the repository's runs at once. The scan for
    /// unfinished runs, the choice of a run's name, the creation of its record and the claim of a run's owner are each
    /// made inside it.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="work">The reading, the decision and the writing, all synchronous.</param>
    /// <param name="cancellationToken">Ends a wait for another process inside the section, before the work begins.</param>
    public T AcrossRuns<T>(HarnessLayout layout, Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(work);

        return _store.AcrossRuns(layout, work, cancellationToken);
    }

    /// <summary>
    /// Starts a run: inside the repository's one section, the test for another unfinished run, the name - a
    /// <paramref name="wantedName"/> that is taken refused (13, <see cref="OrchestrationReports.RunExists"/>, naming its
    /// record and the way on with that one), one whose directory is there holding no run's record refused as well, else
    /// one more than the highest <c>r&lt;N&gt;</c> there - the run's record (model <c>flow-engine</c>, parallel the
    /// workflow's open-agent bound), its first status, every step of the definition NotStarted, and its owner: this
    /// process. Before anything of the run is made, each of the run's own inputs of the kind <c>file</c> that is not empty
    /// is held to a file inside the repository an agent may read, decided as an agent's read of it is on
    /// <paramref name="inputGround"/>, and opened (<see cref="FlowInputs.OpenFiles"/>), and once its directory is
    /// made each is copied into the run's inputs (<see cref="OrchestratorLayout.InputsDirectory"/>) before its record is
    /// written; the context keeps the values as they were given. Once the test for another unfinished run - the last look
    /// at a live owner - has passed, and before anything of the run is written, every run of the repository is swept (<paramref name="sweep"/>), and a start where it keeps a
    /// record because its session holds this very process is refused: the run's engine, this process, would be ended with it
    /// by the next command run from outside, which ends what the record names. Swept there, what it keeps is never older
    /// than the start: an owner seen standing before the section and gone since is swept with the rest.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="sweep">Sweeps every run of the repository, as <c>list-orchestrator</c> does, inside the section.</param>
    /// <param name="command">The command starting the run, which a refusal says to run from outside.</param>
    /// <param name="inputGround">
    /// The ground every file input of the run is decided on: an agent's read of the main checkout, as the configuration the
    /// run starts under makes it (<see cref="Agents.Policy.AgentStepGround.OfFileInputs"/>).
    /// </param>
    /// <param name="wantedName">The name <c>--run</c> gave, or null for the next r&lt;N&gt;.</param>
    /// <param name="inputs">The values the run is started with, by input name.</param>
    /// <param name="startCommit">The commit the run starts from; null where it starts beside the tree's history.</param>
    /// <param name="branch">The branch the run starts on.</param>
    /// <param name="refusal">
    /// What the run's name chosen is still held to before anything of it is written - the checkout it starts from, its
    /// pull request's base and its path budget - as a refusal's sentence; null where nothing refuses it.
    /// </param>
    /// <param name="cancellationToken">Stops the read of the main tree, before anything is written.</param>
    /// <returns>The run's layout and what the run is, as its records name it.</returns>
    /// <exception cref="HarnessException">
    /// Another run of the repository is unfinished, the sweep keeps a record whose session holds this very process, the name
    /// is taken, <paramref name="refusal"/> refuses it, a file input names no file inside the repository an agent may read,
    /// or the run's places or a file input's copy could not be made - what the start had made of the run then taken back,
    /// and said (<see cref="HarnessExit.Refused"/>).
    /// </exception>
    public async Task<(OrchestratorLayout Run, FlowRunContext Context)> CreateAsync(
        HarnessLayout layout,
        WorkflowDefinition definition,
        Func<ChildSweep> sweep,
        string command,
        Agents.Policy.AgentPolicyGround inputGround,
        string? wantedName = null,
        IReadOnlyDictionary<string, string>? inputs = null,
        string? startCommit = null,
        string? branch = null,
        Func<string, string?>? refusal = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(inputGround);

        var differing = await DifferingAsync(layout, leavingOut: null, cancellationToken).ConfigureAwait(false);

        return AcrossRuns(layout, () =>
        {
            RefuseAnotherUnfinished(layout, leavingOut: null, differing);

            if (sweep().Refusal($"no run of workflow '{definition.Name}' was started", "a run started inside it would be ended with it by the next command run from outside", command) is { } holding)
            {
                throw new HarnessException(HarnessExit.Refused, holding);
            }

            var name = wantedName is { } given ? New(layout, given) : NextName(layout);

            if (refusal?.Invoke(name) is { } refused)
            {
                throw new HarnessException(HarnessExit.Refused, refused);
            }

            var run = OrchestratorLayout.Of(layout, name);

            // The files the run's own file inputs name, opened before anything of the run is made, each decided as an agent's
            // read of it is - every agent of the run reads the copy: one that names no file inside the repository an agent
            // may read refuses the start, and the bytes the run keeps are those of the very file decided, read as opened.
            using var files = FlowInputs.OpenFiles(_fileSystem, _platform, definition, FlowInputs.Resolve(definition, inputs), inputGround);

            // The run's places come with it: its temp, where every launch's temporary directory is kept for the run's
            // life, its artifacts, kept for the run's life and copied wherever asked, and its inputs, where each file a
            // file input names is copied before the run's record is written, so a run that has a record has its copies -
            // all taken away with the run. A place or a copy that cannot be made takes back what was made before it and
            // refuses the start: no directory is left holding no run's record, which a later start of that name is
            // refused over.
            try
            {
                _fileSystem.CreateDirectory(run.Directory);
                _fileSystem.CreateDirectory(run.TempDirectory);
                _fileSystem.CreateDirectory(run.ArtifactsDirectory);
                _fileSystem.CreateDirectory(run.InputsDirectory);
                files.Keep(_fileSystem, run);
            }
            catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
            {
                var why = ex is HarnessException ? ex.Message : $"the places of run '{name}' could not be made: {ex.Message.TrimEnd('.')}";

                throw new HarnessException(HarnessExit.Refused, $"{why}; {Unmade(run)} No run was started.", ex);
            }

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

            var context = FlowRunContext.Of(name, definition, inputs, startCommit, branch, _clock.GetUtcNow(), inputCopies: true);
            new WorkflowStatusWriter(_store, run, context).Write(
                new FlowSnapshot(
                    FlowRunStatus.Running,
                    null,
                    new FlowBudgetRecord(0, 0, 0),
                    0,
                    null,
                    [],
                    [],
                    []),
                _clock.GetUtcNow());

            // Claimed before the section is left: a run made here is never, for a moment, a run nobody owns.
            _ = new FlowRunOwner(_store, _identity, _clock).Claim(run);

            return (run, context);
        }, cancellationToken);
    }

    /// <summary>
    /// Takes back what a start that did not get as far as its record made of <paramref name="run"/> - its directory, which
    /// the start found not there, and everything in it - and says so, as a refusal's clause after its cause: removed, or
    /// left where it could not be, naming it and what to do.
    /// </summary>
    /// <param name="run">The run the start was making.</param>
    private string Unmade(OrchestratorLayout run)
    {
        try
        {
            _fileSystem.DeleteDirectory(run.Directory);
            return $"what the start had made of run '{run.Name}' was removed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"what the start had made of run '{run.Name}', '{run.Directory}', could not be removed ({ex.Message.TrimEnd('.')}): remove it, for a run of that name is refused while it stands.";
        }
    }

    /// <summary>
    /// The start of every session of a run already made - a resume and an answer alike - inside the repository's one
    /// section, which a deletion decides in too: the run still there - one deleted since the session looked at it
    /// refused by name, with no owner written beside it - the test for another unfinished run, leaving out
    /// <paramref name="run"/>, the run's owner claimed - refused where a live one holds it, a dead one reclaimed and said -
    /// and a request no engine took removed, so it never reaches this session - where it cannot be taken, the claim just
    /// made is let go before the refusal, so no owner is left behind by a session that never began. Once the claim's last
    /// look at the owner has found it gone, or none, and before the claim takes it, every run of the repository is swept
    /// (<paramref name="sweep"/>), which ends what a dead engine left before this session runs beside it, and a session
    /// where it keeps a record because its session holds this very process is refused, the owner file left as it was: a
    /// session of the run inside it would be ended with it by the next command run from outside, which ends what the
    /// record names. Swept there, what it keeps is never older than the claim - an owner seen gone inside the section
    /// cannot come back, and a live one is never claimed - so an owner the command saw standing - one an answer was handed
    /// to and waited on - and gone since is swept with the rest.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="run">The run the session is for.</param>
    /// <param name="sweep">Sweeps every run of the repository, as <c>list-orchestrator</c> does, inside the section.</param>
    /// <param name="command">The command beginning the session, which a refusal says to run from outside.</param>
    /// <param name="cancellationToken">Stops the read of the main tree, before anything is written.</param>
    /// <returns>What is said of a dead owner reclaimed; null where none was recorded.</returns>
    /// <exception cref="HarnessException">
    /// The run is gone, another run of the repository is unfinished, a live engine owns the run, or the sweep keeps a record
    /// whose session holds this very process (<see cref="HarnessExit.Refused"/>); or a request left for an engine that ended
    /// cannot be taken, the claim this process made let go first. The sweep's refusal comes before the claim is made.
    /// </exception>
    public async Task<string?> ClaimAsync(HarnessLayout layout, OrchestratorLayout run, Func<ChildSweep> sweep, string command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var differing = await DifferingAsync(layout, run.Name, cancellationToken).ConfigureAwait(false);

        return AcrossRuns(layout, () =>
        {
            if (_store.ReadOrchestrator(run) is null)
            {
                throw new HarnessException(HarnessExit.Refused, OrchestrationReports.NoOrchestrator(run.Name));
            }

            RefuseAnotherUnfinished(layout, run.Name, differing);

            var owner = new FlowRunOwner(_store, _identity, _clock);
            var reclaimed = owner.Claim(run, taking: () =>
            {
                if (sweep().Refusal($"run '{run.Name}' was not taken", "a session of the run inside it would be ended with it by the next command run from outside", command) is { } holding)
                {
                    throw new HarnessException(HarnessExit.Refused, holding);
                }
            });

            try
            {
                // A pause or a stop asked of an engine that ended before it took it was asked of that engine, and of no
                // session after it.
                _ = _store.TakeStopFile(run);
            }
            catch
            {
                // A claim is never left behind by a session that does not begin: what this process just wrote is let go,
                // so the run stands as no session holds it, and the refusal is the one said.
                owner.Release(run);
                throw;
            }

            return reclaimed;
        }, cancellationToken);
    }

    /// <summary>The name <c>--run</c> gave, where nothing is there by it: a run's record refused as taken, and a directory that holds none refused as well.</summary>
    private string New(HarnessLayout layout, string given)
    {
        var run = OrchestratorLayout.Of(layout, given);

        if (_fileSystem.KindOf(run.RecordFile) == PathKind.File)
        {
            throw new HarnessException(HarnessExit.Refused, OrchestrationReports.RunExists(run));
        }

        if (_fileSystem.KindOf(run.Directory) != PathKind.None)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{run.Directory}' is there and holds no run's record ('{run.RecordFile}'); --run names a new run, and a run is never made over what something else left there. "
                + "Nothing was changed: remove it, or name the run otherwise.");
        }

        return given;
    }

    /// <summary>
    /// Which of the paths a commit owes in the ledgers of every run but <paramref name="leavingOut"/> git does not ignore
    /// and the main tree holds otherwise than its HEAD (<see cref="FoldLedger.DifferingAsync"/>), read before the repository's section, which is
    /// synchronous - and read only where such a ledger holds one, the one question it answers; null where none does, or
    /// where every ledger that might cannot be read, which the section says. A commit made between the read and the
    /// section only counts a fold uncommitted that is committed by then, never the other way round: a run that folds
    /// meanwhile has a live owner, which the section refuses.
    /// </summary>
    private async Task<IReadOnlySet<string>?> DifferingAsync(HarnessLayout layout, string? leavingOut, CancellationToken cancellationToken)
    {
        var owed = new List<string>();

        foreach (var other in _store.Orchestrators(layout).Where(other => !string.Equals(other.Name, leavingOut, StringComparison.Ordinal)))
        {
            try
            {
                owed.AddRange(FoldLedger.OwedPaths(_store.ReadFolds(other)));
            }
            catch (HarnessException)
            {
                // The section reads it again, and says it cannot be read.
            }
        }

        if (owed.Count == 0)
        {
            return null;
        }

        // A path git ignores is one no commit takes, whatever it holds, so it holds no run unfinished.
        var main = layout.MainCheckoutRoot;
        var asked = owed.Distinct(StringComparer.Ordinal).ToList();
        var ignored = await _git.FindIgnoredAsync(main, asked, cancellationToken).ConfigureAwait(false);

        return await FoldLedger.DifferingAsync(_git, _fileSystem, main, [.. asked.Where(path => !ignored.Contains(path))], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses where any run but <paramref name="leavingOut"/> is unfinished and has work in the tree, or where what it
    /// holds cannot be told; <paramref name="differing"/> is what the main tree holds otherwise than its HEAD, null where
    /// it was not read.
    /// </summary>
    private void RefuseAnotherUnfinished(HarnessLayout layout, string? leavingOut, IReadOnlySet<string>? differing)
    {
        foreach (var other in _store.Orchestrators(layout))
        {
            if (string.Equals(other.Name, leavingOut, StringComparison.Ordinal))
            {
                continue;
            }

            if (Unfinished(other, differing) is { } why)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"run '{other.Name}' of this repository is unfinished ({why}): resume-orchestrator {other.Name} or answer-orchestrator {other.Name} goes on with it, delete-orchestrator {other.Name} ends it");
            }
        }
    }

    /// <summary>Why <paramref name="other"/> is unfinished and holds work in the tree, or cannot be told not to; null where it is not the run this test refuses.</summary>
    private string? Unfinished(OrchestratorLayout other, IReadOnlySet<string>? differing)
    {
        if (new FlowRunOwner(_store, _identity, _clock).Live(other) is not null)
        {
            return "running; owned by a live engine";
        }

        var file = _store.ReadFlow(other);

        if (file is null || file.Status is not (nameof(FlowRunStatus.Running) or nameof(FlowRunStatus.Paused) or nameof(FlowRunStatus.Stopped) or nameof(FlowRunStatus.Failed)))
        {
            return null;
        }

        var status = file.Status.ToLower(CultureInfo.InvariantCulture);
        var agents = _store.Agents(other);

        if (agents.FirstOrDefault(entry => entry.Record is null) is { } unreadable)
        {
            return $"{status}, and whether its agent '{unreadable.Name}' holds a worktree cannot be told: {unreadable.Problem!.TrimEnd('.')}";
        }

        // An agent that only reads holds a tree of its own, and nothing in it is ever folded: no work of the tree's is in it.
        var open = agents.Count(entry => entry.Record is { State: AgentStates.Live, Reads: false } record && record.Worktree is not null);
        int uncommitted;

        try
        {
            uncommitted = FoldLedger.Uncommitted(_store.ReadFolds(other), differing).Count;
        }
        catch (HarnessException ex)
        {
            return $"{status}, and whether its ledger holds a fold not yet committed cannot be told: {ex.Message.TrimEnd('.')}";
        }

        List<string> held = [];

        if (open > 0)
        {
            held.Add($"{open.ToString(CultureInfo.InvariantCulture)} agent{(open == 1 ? string.Empty : "s")} with worktree{(open == 1 ? string.Empty : "s")} still open");
        }

        if (uncommitted > 0)
        {
            held.Add($"{uncommitted.ToString(CultureInfo.InvariantCulture)} fold{(uncommitted == 1 ? string.Empty : "s")} in its ledger not yet committed");
        }

        return held.Count > 0 ? $"{status}; {string.Join(", and ", held)}" : null;
    }

    /// <summary>
    /// The next <c>r&lt;N&gt;</c>: one more than the highest under the orchestrators directory, so a run's name is never
    /// one in use and names grow with the runs while they are kept; a deleted run's name is given again only where no
    /// later run was made.
    /// </summary>
    private string NextName(HarnessLayout layout)
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
/// The path budget at a run's start: the longest worktree path the run makes - counted with what the worktree's own
/// check counts below it (<see cref="WorktreeRoom"/>), so a run that passes here never fails on a worktree it needs, and
/// with each <c>.legs/&lt;key&gt;</c> copy its legs work in, with its variant's build below it, and each
/// <c>.m/&lt;key&gt;&lt;mark&gt;-&lt;n&gt;</c> worker its sweeps keep, with its <c>build/&lt;key&gt;</c> below it, as a
/// copy's own check counts each (<see cref="CopyPathBudget"/>) - stays within this machine's path limit, with the margin to spare. A copy whose build
/// passes the limit fails as compile errors in files nobody touched, so it is asked before the run makes one.
/// </summary>
public static class FlowRunPaths
{
    /// <summary>The step types that run legs, each in the tree its step names.</summary>
    private static readonly IReadOnlySet<string> LegSteps = new HashSet<string>(StringComparer.Ordinal) { "build", "test", "run", "check-mutations" };

    /// <summary>
    /// Checks the longest path the run's worktrees and the copies and workers inside them make, against the margin and
    /// the limit the repository's settings declare.
    /// </summary>
    /// <param name="budget">Reckons a path against this machine's limit.</param>
    /// <param name="settings">The repository's worktree settings: the margin, the reserve, and a limit of its own where it declares one.</param>
    /// <param name="worktreesRoot">Where the run's agents' worktrees are made.</param>
    /// <param name="agentName">The longest agent name the run makes.</param>
    /// <param name="copied">Each variant a leg of the run builds in a copy, <c>.legs/&lt;key&gt;</c> below its tree.</param>
    /// <param name="swept">Each variant a sweep of the run keeps workers of, <c>.m/&lt;key&gt;&lt;mark&gt;-&lt;n&gt;</c> below its tree.</param>
    /// <param name="written">
    /// The longest path, relative to the tree, of a file the tree holds - one the repository tracks
    /// (<see cref="WorktreeRoom.ForFilesAsync"/>), or one a work item says it writes - in the tree and in every copy and
    /// worker of it, which hold the tree's files; 0 where none is said.
    /// </param>
    /// <param name="builds">What a build on this machine adds below the tree itself (<see cref="WorktreeRoom.ForBuilds"/>); 0 where none builds there.</param>
    /// <returns>Whether the longest path stays within the budget, with what it needs and what it may have.</returns>
    public static PathBudgetResult Check(
        IPathBudget budget,
        WorktreeSettings settings,
        string worktreesRoot,
        string agentName,
        IReadOnlyList<VariantKey> copied,
        IReadOnlyList<VariantKey> swept,
        int written = 0,
        int builds = 0)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(copied);
        ArgumentNullException.ThrowIfNull(swept);

        // The deepest tree the run works in: the longest agent's worktree.
        var tree = Path.Combine(worktreesRoot, agentName);

        // The longest thing kept inside a tree the run works in, below the tree: a file it holds, the build a leg of this
        // machine makes in the tree itself, or a copy a leg works in or a mutation worker - each with its build directory
        // below it, its variant's own or the worker's build/<key>, and the build below that, or the file, which a copy
        // holds as the tree does.
        var file = written;
        var inside = Math.Max(file, builds);

        foreach (var variant in copied)
        {
            var copy = LegCopies.PathOf(tree, variant);

            inside = Math.Max(inside, Below(tree, copy, variant.DirectoryUnder(copy), settings.PathBudgetReserve, file));
        }

        foreach (var variant in swept)
        {
            var workers = MutationWorkers.Of(tree, variant);

            inside = Math.Max(inside, Below(tree, workers.PathOf(1), workers.BuildDirectoryOf(1), settings.PathBudgetReserve, file));
        }

        return budget.Check(tree, inside, settings.PathBudgetMargin, settings.PathLimit);
    }

    /// <summary>
    /// The path budget of a run of <paramref name="definition"/> at its start: its agents' worktrees under the
    /// repository's worktrees root, the longest name it gives an agent (<see cref="OrchestrationRules.LongestGeneratedAgentName"/>),
    /// and the variants of every leg this machine runs that a leg step of the run may name - every leg a step names
    /// through its <c>legs</c>, leg sets read out, and every declared leg where one names none - copied where the leg
    /// works in a copy there (<see cref="LegTrees.InLocalCopy"/>), and swept where a <c>check-mutations</c> step names them.
    /// </summary>
    /// <param name="budget">Reckons a path against this machine's limit.</param>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="run">The run's name.</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="platformKey">This machine's operating system, as a leg names one.</param>
    /// <param name="written">The longest path, relative to the tree, of a file the tree holds; 0 where none is said (<see cref="Check"/>).</param>
    /// <returns>What a refusal says where the longest path passes the budget; null where it stays within it.</returns>
    public static string? RefusalAtStart(IPathBudget budget, HarnessContext context, string run, WorkflowDefinition definition, string platformKey, int written = 0)
    {
        var (result, tree) = AtStart(budget, context, run, definition, platformKey, written);

        return result.IsWithinBudget
            ? null
            : $"run '{run}' of '{definition.Name}' would work in paths too long for this machine - the deepest of its agents' worktrees with the copies and workers its leg steps keep inside it: "
                + $"{result.Describe(tree)} Nothing was made.";
    }

    /// <summary>The path budget of a run of <paramref name="definition"/> at its start, and the deepest worktree it reckons from; see <see cref="RefusalAtStart"/>.</summary>
    /// <param name="budget">Reckons a path against this machine's limit.</param>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="run">The run's name.</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="platformKey">This machine's operating system, as a leg names one.</param>
    /// <param name="written">The longest path, relative to the tree, of a file the tree holds; 0 where none is said (<see cref="Check"/>).</param>
    /// <returns>Whether the longest path stays within the budget, with what it needs and what it may have, and the worktree it was reckoned from.</returns>
    public static (PathBudgetResult Result, string Tree) AtStart(IPathBudget budget, HarnessContext context, string run, WorkflowDefinition definition, string platformKey, int written = 0)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);

        var config = context.Config;
        var settings = config.Worktrees;
        var digits = OrchestrationRules.LongestGeneratedAgentName(definition.MaxInstances ?? WorkflowDefinition.DefaultMaxInstances) - OrchestrationRules.GeneratedAgentPrefix.Length;
        var agent = OrchestrationRules.GeneratedAgentPrefix + new string('9', digits);
        var copied = new List<VariantKey>();
        var swept = new List<VariantKey>();

        foreach (var step in WorkflowFiles.Steps(definition).Select(entry => entry.Step).Where(step => step.Type is { } type && LegSteps.Contains(type)))
        {
            foreach (var leg in LegsOf(step, config).Where(leg => LegTrees.BuildsHere(leg, platformKey)))
            {
                var variant = VariantKey.For(config, leg, platformKey);

                if (LegTrees.InLocalCopy(context, leg, here: null, LocalTrees.AsConfigured))
                {
                    copied.Add(variant);
                }

                if (step.Type == "check-mutations")
                {
                    swept.Add(variant);
                }
            }
        }

        var root = WorktreeAddress.Plain(run).PathUnder(context.Layout.WorktreesDirectoryUnder(settings.Root));

        return (Check(budget, settings, root, agent, copied, swept, written, WorktreeRoom.ForBuilds(config, platformKey)), Path.Combine(root, agent));
    }

    /// <summary>
    /// What a copy or a worker inside <paramref name="tree"/> adds below it: its own path, and the deeper of its
    /// build directory - its variant's own in a copy, the one named by its variant's key in a worker - with the build
    /// below that, and <paramref name="file"/>, a work item's file in it.
    /// </summary>
    private static int Below(string tree, string copy, string buildDirectory, int reserve, int file)
        => copy.Length - Path.TrimEndingDirectorySeparator(tree).Length + Math.Max(CopyPathBudget.BuildBelow(copy, buildDirectory, reserve), file);

    /// <summary>The legs <paramref name="step"/> may name: those its <c>legs</c> names, leg sets read out; every declared leg where it names none.</summary>
    private static IEnumerable<LegConfig> LegsOf(WorkflowStep step, HarnessConfig config)
    {
        if (step.Legs.Count == 0)
        {
            return config.Legs.Values;
        }

        return step.Legs
            .SelectMany(name => config.LegSets.TryGetValue(name, out var set) ? set : [name])
            .Distinct(StringComparer.Ordinal)
            .Where(config.Legs.ContainsKey)
            .Select(name => config.Legs[name]);
    }
}
