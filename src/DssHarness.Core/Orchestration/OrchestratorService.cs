using DssHarness.Core.Application;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Orchestration;

/// <summary>Creating, deleting and listing orchestrators.</summary>
public interface IOrchestratorService : IUseCase
{
    /// <summary>
    /// Creates an orchestrator: its directory under <c>.orchestrators</c>, its record, its logs, work and plans
    /// directories, and the directory its agents are kept in. Run again for one that exists, it changes only the limit
    /// and the session, and refuses a model other than the one it was created for.
    /// </summary>
    /// <param name="startDirectory">A directory in the repository.</param>
    /// <param name="name">The orchestrator's name, held to the worktree name rule.</param>
    /// <param name="model">The model the orchestrating session runs on.</param>
    /// <param name="parallel">The most agents with a worktree at once; <see cref="OrchestratorRecord.DefaultParallel"/> for a new one where none is given.</param>
    /// <param name="session">The orchestrating session's Claude session id, where it is known.</param>
    /// <param name="cancellationToken">Stops it.</param>
    Task<CommandOutcome> CreateAsync(string startDirectory, string name, string model, int? parallel, string? session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an orchestrator, once none of its agents with a worktree is live or closed and no worktree is left below its
    /// directory under the worktrees root; an agent with no worktree holds nothing but records, and goes with it whatever
    /// its state. The evidence, transcripts, steps and sessions its agents kept - each agent's, of either kind, deleted or
    /// not - go with it only with <paramref name="deleteEvidence"/>.
    /// </summary>
    /// <param name="startDirectory">A directory in the repository.</param>
    /// <param name="name">The orchestrator's name.</param>
    /// <param name="deleteEvidence">Whether the evidence, transcripts, steps and sessions its agents kept may go with it.</param>
    /// <param name="cancellationToken">Stops it.</param>
    Task<CommandOutcome> DeleteAsync(string startDirectory, string name, bool deleteEvidence, CancellationToken cancellationToken = default);

    /// <summary>Lists the orchestrators, or the one named, with their agents and where each stands; a run's own lines from <c>flow.json</c> beside them.</summary>
    /// <param name="startDirectory">A directory in the repository.</param>
    /// <param name="name">The orchestrator to list, or null for all of them.</param>
    /// <param name="steps">Whether to list each step instance of a run, with its state, its visit, its attempt, its agent and its own directory.</param>
    /// <param name="questions">Whether to list every question a run raised and what became of it.</param>
    /// <param name="json">Whether to answer with one JSON document rather than lines.</param>
    /// <param name="cancellationToken">Stops it.</param>
    Task<CommandOutcome> ListAsync(string startDirectory, string? name, bool steps, bool questions, bool json, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an orchestrator, as the request says: the answer carries <see cref="OrchestratorCreated"/> facts, and the
    /// request may ask that a record of that name being taken refuses it, which a run's start of its own record does.
    /// </summary>
    Task<UseCaseResult<OrchestratorCreated>> CreateAsync(OrchestratorCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes an orchestrator, as the request says: the answer carries <see cref="OrchestratorDeleted"/> facts.</summary>
    Task<UseCaseResult<OrchestratorDeleted>> DeleteAsync(OrchestratorDeleteRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IOrchestratorService"/>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="gitClient">Asks git whether the orchestrators directory is ignored.</param>
/// <param name="fileSystem">Makes and removes the orchestrator's directories.</param>
/// <param name="platform">How this machine compares paths.</param>
/// <param name="output">Where the worktree inspection says what it saw.</param>
/// <param name="worktrees">The worktrees there are, for the listing.</param>
/// <param name="store">The records.</param>
/// <param name="log">The logs.</param>
/// <param name="sessions">Ends what an orchestrator's steps left running once their starter has gone - a contained child's session, a leg's process group - first in every command over it.</param>
/// <param name="identity">How the liveness of a run's engine is told, through the run's one owner of it (<see cref="Flow.FlowRunOwner"/>).</param>
/// <param name="clock">When an orchestrator is created.</param>
public sealed class OrchestratorService(
    IHarnessContextLoader contextLoader,
    IGitClient gitClient,
    IFileSystem fileSystem,
    IHostPlatform platform,
    IHarnessOutput output,
    IWorktreeService worktrees,
    OrchestrationStore store,
    OrchestrationLog log,
    ChildSessions sessions,
    Execution.IProcessIdentity identity,
    TimeProvider clock) : IOrchestratorService
{
    /// <summary>The command that creates one.</summary>
    public const string CreateCommand = "create-orchestrator";

    /// <summary>The command that deletes one.</summary>
    public const string DeleteCommand = "delete-orchestrator";

    /// <summary>The command that lists them.</summary>
    public const string ListCommand = "list-orchestrator";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;
    private readonly IWorktreeService _worktrees = worktrees;
    private readonly OrchestrationStore _store = store;
    private readonly OrchestrationLog _log = log;
    private readonly ChildSessions _sessions = sessions;
    private readonly Flow.FlowRunOwner _owner = new(store, identity, clock);
    private readonly TimeProvider _clock = clock;

    public Task<CommandOutcome> CreateAsync(
        string startDirectory,
        string name,
        string model,
        int? parallel,
        string? session,
        CancellationToken cancellationToken = default)
        => CreateAsync(startDirectory, name, model, parallel, session, mustBeNew: false, cancellationToken);

    /// <summary>
    /// Creates an orchestrator, or - where one of that name is there and <paramref name="mustBeNew"/> is not asked -
    /// changes its limit and its session; where it is asked, a name that is taken is refused, naming its record, and
    /// nothing is changed.
    /// </summary>
    private async Task<CommandOutcome> CreateAsync(
        string startDirectory,
        string name,
        string model,
        int? parallel,
        string? session,
        bool mustBeNew,
        CancellationToken cancellationToken)
    {
        if ((OrchestrationRules.NameProblem(name)
            ?? OrchestrationRules.ModelProblem(model)
            ?? OrchestrationRules.SessionProblem(session)
            ?? OrchestrationRules.ParallelProblem(parallel)) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;

        if (!WorktreeName.Validate(name, context.Config.Worktrees.MaxNameLength).TryGetName(out _, out var length))
        {
            return CommandOutcome.Usage(length);
        }

        var orchestrator = OrchestratorLayout.Of(layout, name);

        // Before anything else of it is read: what its steps left running once their starter has gone.
        await _sessions.SweepAsync(orchestrator, CreateCommand).ConfigureAwait(false);

        // Everything an orchestrator keeps is machine-local: a record, a log, a transcript committed by `git add -A` would
        // travel to every clone. init keeps the directory in git and everything made in it out.
        if (await SlotIgnored.RefusalAsync(_gitClient, layout.MainCheckoutRoot, orchestrator.RelativeRecordFile, $"what orchestrator '{name}' keeps", cancellationToken).ConfigureAwait(false) is { } unignored)
        {
            return CommandOutcome.Refused($"{unignored} Nothing was created.");
        }

        var group = WorktreeAddress.Plain(name).PathUnder(layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root));
        var now = _clock.GetUtcNow();

        return _store.Exclusively(orchestrator, () =>
        {
            if (_store.ReadOrchestrator(orchestrator) is { } existing)
            {
                // Decided under the same hold as the creation, so no other command can take the name between the two.
                return mustBeNew
                    ? CommandOutcome.Refused(OrchestrationReports.RunExists(orchestrator))
                    : Change(orchestrator, existing, model, parallel, session);
            }

            // Plain worktrees and orchestrators share the names under the root: the directory named for this one is
            // where its agents' worktrees are made.
            if (_fileSystem.DirectoryExists(group) && (WorktreeInspector.HoldsOwnGit(_fileSystem, group) || !_fileSystem.IsEmpty(group)))
            {
                return CommandOutcome.Refused(
                    $"'{group}' is {(WorktreeInspector.HoldsOwnGit(_fileSystem, group) ? "a worktree" : "a directory holding files no orchestrator made")}, and "
                    + $"an orchestrator's agents' worktrees are made there: delete it, or name the orchestrator otherwise. Nothing was created.");
            }

            var record = new OrchestratorRecord
            {
                Kind = OrchestratorRecord.KindName,
                Name = name,
                Model = model,
                CreatedAt = now,
                Parallel = parallel ?? OrchestratorRecord.DefaultParallel,
                Session = session,
            };

            foreach (var directory in new[] { orchestrator.LogsDirectory, orchestrator.WorkRoot, orchestrator.PlansDirectory(name), orchestrator.AgentsDirectory })
            {
                _fileSystem.CreateDirectory(directory);
            }

            _store.WriteOrchestrator(orchestrator, record);

            var outcome = CommandOutcome.Ok(
                $"created orchestrator '{name}'",
                [$"record {orchestrator.RecordFile}", $"plans {orchestrator.PlansDirectory(name)}", $"at most {ReportText.Counted(record.Parallel, "agent")} with a worktree at once"])
                with { Facts = new OrchestratorCreated(name, Existed: false, record.Parallel, session) };

            return _log.Record(orchestrator, name, CreateCommand, outcome);
        });
    }

    /// <summary>What running create-orchestrator again does: changes the limit and the session, and nothing else.</summary>
    private CommandOutcome Change(OrchestratorLayout orchestrator, OrchestratorRecord existing, string model, int? parallel, string? session)
    {
        if (!string.Equals(existing.Model, model, StringComparison.Ordinal))
        {
            return CommandOutcome.Refused(
                $"Orchestrator '{existing.Name}' was created for model '{existing.Model}', not '{model}'. Run again with --model {existing.Model}, "
                + "which changes only its limit and its session. Nothing was changed.");
        }

        var changed = existing with { Parallel = parallel ?? existing.Parallel, Session = session ?? existing.Session };

        if (changed == existing)
        {
            return CommandOutcome.Ok($"orchestrator '{existing.Name}' is already as asked", [$"record {orchestrator.RecordFile}"])
                with { Facts = new OrchestratorCreated(existing.Name, Existed: true, changed.Parallel, changed.Session) };
        }

        _store.WriteOrchestrator(orchestrator, changed);

        var said = new List<string>();

        if (changed.Parallel != existing.Parallel)
        {
            said.Add($"allows at most {ReportText.Counted(changed.Parallel, "agent")} with a worktree at once, where it allowed {existing.Parallel}");
        }

        if (changed.Session != existing.Session)
        {
            said.Add($"records session {changed.Session}");
        }

        return _log.Record(orchestrator, existing.Name, CreateCommand, CommandOutcome.Ok($"orchestrator '{existing.Name}' now {string.Join(", and ", said)}", [$"record {orchestrator.RecordFile}"])
            with { Facts = new OrchestratorCreated(existing.Name, Existed: true, changed.Parallel, changed.Session) });
    }

    public async Task<CommandOutcome> DeleteAsync(
        string startDirectory,
        string name,
        bool deleteEvidence,
        CancellationToken cancellationToken = default)
    {
        if (OrchestrationRules.NameProblem(name) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;
        var orchestrator = OrchestratorLayout.Of(layout, name);

        // Before anything else of it is read, and before its records can go with it: what its steps left running once
        // their starter has gone.
        await _sessions.SweepAsync(orchestrator, DeleteCommand).ConfigureAwait(false);

        var worktreesDirectory = layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);
        var group = WorktreeAddress.Plain(name).PathUnder(worktreesDirectory);

        // git's list of worktrees, asked before the orchestrator is held: what is below its directory under the root. A list
        // git cannot give is raised, never read as no worktree below.
        var below = _fileSystem.DirectoryExists(group)
            ? await new WorktreeInspector(_gitClient, _fileSystem, _platform, _output).FindWorktreesBelowAsync(layout.MainCheckoutRoot, worktreesDirectory, group, orchestrators: true, cancellationToken).ConfigureAwait(false)
            : new WorktreesBelow([], []);

        // Decided and done with no other command deciding about this orchestrator meanwhile: an agent made beside this
        // deletion would be made into a directory being removed. Inside the repository's one section too, taken before
        // the orchestrator's own - as a session's claim of a run takes it - so no claim comes between the look at the
        // run's owner and the run's removal: a session never goes on over records being deleted.
        var outcome = _store.AcrossRuns(layout, () => _store.Exclusively(orchestrator, () =>
        {
            if (_store.ReadOrchestrator(orchestrator) is null)
            {
                return CommandOutcome.Refused(OrchestrationReports.NoOrchestrator(name));
            }

            // A run's engine that is live owns the run: only it records what its steps left running, and the deletion
            // does not take the run from under it. Whether it is live is the run's one owner's to tell; an owner whose
            // engine has gone - a crash, stop-orchestrator --now - goes with the run.
            if (_owner.Live(orchestrator) is { } held)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted: a live engine owns the run ({_owner.Named(held)}). "
                    + $"{OrchestrationReports.Line(Flow.FlowService.PauseCommand, name)} pauses it and "
                    + $"{OrchestrationReports.Line(Flow.FlowService.StopCommand, name)} ends it, and once the engine is gone the run can be deleted. Nothing was changed.");
            }

            var agents = _store.Agents(orchestrator);

            if (agents.FirstOrDefault(agent => agent.Record is null) is { } unreadable)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted: what its agent '{unreadable.Name}' is cannot be told - {unreadable.Problem!.TrimEnd('.')}. "
                    + "Nothing was changed.");
            }

            // An agent that reads the main tree holds nothing but records: it does not stand in the way, and goes with
            // the orchestrator. What it kept of the steps that ran as it and of its sessions is counted below with every
            // agent's, under the guard --delete-evidence gives. What git lists below is refused on its own below, so the
            // open agents are asked of the records alone.
            var open = OrchestrationRules.OpenAgents(name, agents, []);

            if (open.Count > 0)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted: its agent(s) {string.Join(", ", open.Select(agent => $"'{agent}'"))} are not deleted yet. "
                    + $"Delete each with {OrchestrationReports.DeleteAgentLine(name, "<agent>")}. Nothing was changed.");
            }

            if (below.Worktrees.Count > 0)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted: worktrees are left below '{group}' - {ReportText.Listed(below.Worktrees)}. Delete each with "
                    + $"{OrchestrationReports.Line(WorktreeService.DeleteCommand, "<address>")} first. Nothing was changed.");
            }

            // Nothing here can tell what such a directory holds, whatever it is named: it is a person's to look in and delete.
            if (below.Unrecorded.Count > 0)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted: {WorktreeInspector.SaidUnrecorded(below.Unrecorded)}. Nothing was changed.");
            }

            // What an agent kept is its evidence and its Claude transcripts, and the steps that ran as it and its
            // conversations with an API client - a step's stored stream is its transcript - whatever its kind and state.
            var kept = agents.Select(agent => new KeptOf(
                    agent.Name,
                    Files(orchestrator.EvidenceDirectory(agent.Name)),
                    Files(orchestrator.TranscriptsDirectory(agent.Name)),
                    Files(orchestrator.AgentStepsDirectory(agent.Name)),
                    Files(orchestrator.AgentSessionsDirectory(agent.Name))))
                .Where(agent => agent.Evidence + agent.Transcripts + agent.Steps + agent.Sessions > 0)
                .ToList();

            // A run's own records are kept records as its agents' evidence and transcripts are: the steps under the
            // run, the steps and the sessions inside each agent's directory - a deleted agent's included, since its
            // directory stays - and the report. A deleted agent's are counted above where its directory still names
            // it; the run's own steps and its report are counted here.
            if (_store.ReadFlow(orchestrator) is not null)
            {
                var report = (_fileSystem.KindOf(orchestrator.ReportFile) == PathKind.File ? 1 : 0)
                    + (_fileSystem.KindOf(orchestrator.ReportJsonFile) == PathKind.File ? 1 : 0);
                var runKept = new KeptOf(name, 0, 0, Files(orchestrator.StepsDirectory), 0, report);

                if (runKept.Steps > 0 || report > 0)
                {
                    kept.Insert(0, runKept);
                }
            }

            if (kept.Count > 0 && !deleteEvidence)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted, because it keeps {KeptOf.Said(kept)} of its agents {string.Join(", ", kept.Select(agent => $"'{agent.Name}'"))}. "
                    + "Copy out what you need, or pass --delete-evidence to delete them with it. Nothing was changed.");
            }

            if (Remove(orchestrator) is { } stopped)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Orchestrator '{name}' is not deleted yet: {stopped}. Its record is kept, so "
                    + $"{OrchestrationReports.Line(DeleteCommand, name, deleteEvidence ? "--delete-evidence" : string.Empty)} run again finishes it.");
            }

            // Every agent left is deleted, or has no worktree and was not: each is counted as what it is.
            var deleted = agents.Count(agent => agent.Record!.State == AgentStates.Deleted);
            var reading = agents.Count - deleted;
            string[] went =
            [
                .. deleted == 0 ? [] : new[] { ReportText.Counted(deleted, "deleted agent") },
                .. reading == 0 ? [] : new[] { $"{ReportText.Counted(reading, "agent")} with no worktree, not deleted" },
            ];

            return CommandOutcome.Ok(
                $"deleted orchestrator '{name}'",
                [
                    $"removed {orchestrator.Directory}",
                    .. went.Length == 0 ? [] : new[] { $"with the history of {string.Join(" and ", went)}" },
                    .. kept.Count == 0 ? [] : new[] { $"and {KeptOf.Said(kept)}" },
                ]);
        }));

        return outcome.Succeeded
            ? outcome with { Details = [.. outcome.Details ?? [], .. RemoveEmptyGroup(group)], Facts = new OrchestratorDeleted(name, deleteEvidence) }
            : outcome;
    }

    /// <inheritdoc cref="IOrchestratorService.CreateAsync(OrchestratorCreateRequest, CancellationToken)"/>
    /// <remarks>
    /// Where the request's <see cref="OrchestratorCreateRequest.MustBeNew"/> is set, a name that is taken is refused
    /// (<see cref="HarnessExit.Refused"/>), naming its record, and nothing is changed: a run's start creating its own
    /// record never writes over one someone else made.
    /// </remarks>
    public async Task<UseCaseResult<OrchestratorCreated>> CreateAsync(OrchestratorCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UseCaseResult<OrchestratorCreated>.From(
            await CreateAsync(request.StartDirectory, request.Name, request.Model, request.Parallel, request.Session, request.MustBeNew, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc cref="IOrchestratorService.DeleteAsync(OrchestratorDeleteRequest, CancellationToken)"/>
    public async Task<UseCaseResult<OrchestratorDeleted>> DeleteAsync(OrchestratorDeleteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UseCaseResult<OrchestratorDeleted>.From(
            await DeleteAsync(request.StartDirectory, request.Name, request.DeleteEvidence, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Removes everything the orchestrator keeps, every record last - each agent's after everything else of that agent,
    /// the orchestrator's after its agents - so that a removal stopped part way leaves only what still reads: the
    /// orchestrator is still there to delete again, and each agent left is one whose record says what it is, never a
    /// directory nothing names. Why it stopped, or null.
    /// </summary>
    private string? Remove(OrchestratorLayout orchestrator)
    {
        try
        {
            foreach (var directory in _fileSystem.EnumerateDirectories(orchestrator.Directory).Where(directory => !Same(directory, orchestrator.AgentsDirectory)))
            {
                _fileSystem.DeleteDirectory(directory);
            }

            if (_fileSystem.DirectoryExists(orchestrator.AgentsDirectory))
            {
                foreach (var agent in _fileSystem.EnumerateDirectories(orchestrator.AgentsDirectory))
                {
                    RemoveKeepingRecordLast(agent, Path.Combine(agent, OrchestratorLayout.RecordFileName));
                }

                _fileSystem.DeleteDirectory(orchestrator.AgentsDirectory);
            }

            RemoveKeepingRecordLast(orchestrator.Directory, orchestrator.RecordFile);

            // The owner file sits beside the directory, not in it: a run's goes with the run.
            _store.RemoveOwner(orchestrator);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    /// <summary>Removes <paramref name="directory"/> with everything in it, <paramref name="record"/> last.</summary>
    private void RemoveKeepingRecordLast(string directory, string record)
    {
        foreach (var child in _fileSystem.EnumerateDirectories(directory))
        {
            _fileSystem.DeleteDirectory(child);
        }

        foreach (var file in _fileSystem.EnumerateFiles(directory, recursive: false).Where(file => !Same(file, record)))
        {
            _fileSystem.DeleteFile(file);
        }

        _fileSystem.DeleteFile(record);
        _fileSystem.DeleteDirectory(directory);
    }

    /// <summary>
    /// Removes the directory named for a deleted orchestrator under the worktrees root, where it is left empty; one that
    /// cannot be removed, or looked in to tell whether it is empty, is said as which - beside the deletion, never in
    /// place of it: the orchestrator is gone either way.
    /// </summary>
    private IEnumerable<string> RemoveEmptyGroup(string group)
    {
        try
        {
            if (!_fileSystem.DirectoryExists(group) || !_fileSystem.IsEmpty(group))
            {
                return [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"its directory under the worktrees root, '{group}', is left: whether it is empty could not be told - {ex.Message.TrimEnd('.')}"];
        }

        try
        {
            _fileSystem.DeleteDirectory(group);
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"its directory under the worktrees root, '{group}', is empty and could not be removed: {ex.Message.TrimEnd('.')}"];
        }
    }

    private bool Same(string one, string other) => PathContainment.AreSame(one, other, _platform.PathComparison);

    public async Task<CommandOutcome> ListAsync(
        string startDirectory,
        string? name,
        bool steps,
        bool questions,
        bool json,
        CancellationToken cancellationToken = default)
    {
        if (name is not null && OrchestrationRules.NameProblem(name) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;
        var orchestrators = name is null
            ? _store.Orchestrators(layout)
            : _store.ReadOrchestrator(OrchestratorLayout.Of(layout, name)) is null ? [] : [OrchestratorLayout.Of(layout, name)];

        if (name is not null && orchestrators.Count == 0)
        {
            return CommandOutcome.Refused(OrchestrationReports.NoOrchestrator(name));
        }

        // Before anything else of each is read: what its steps left running once their starter has gone.
        foreach (var orchestrator in orchestrators)
        {
            await _sessions.SweepAsync(orchestrator, ListCommand).ConfigureAwait(false);
        }

        var listed = await _worktrees.ListAsync(layout.MainCheckoutRoot, cancellationToken).ConfigureAwait(false);
        var inventory = new List<OrchestratorInventory>();

        foreach (var orchestrator in orchestrators)
        {
            var record = _store.ReadOrchestrator(orchestrator)!;
            var agents = _store.Agents(orchestrator);

            inventory.Add(new OrchestratorInventory(
                orchestrator,
                record,
                [.. agents.Select(agent => Inventory(layout, context.Config.Worktrees.Root, orchestrator, agent))],
                [.. listed.Where(worktree => WorktreeAddress.AgentOf(orchestrator.Name, worktree.Name) is { } agent && !agents.Any(entry => entry.Name == agent))],
                OrchestrationRules.OpenAgents(orchestrator.Name, agents, listed.Select(worktree => worktree.Name)),
                Run: _store.ReadFlow(orchestrator) is { } flow
                    ? new FlowRunInventory(flow, _store.ReadFlowQuestions(orchestrator), EngineOf(orchestrator, flow), _store.ReadFlowUpdates(orchestrator))
                    : null));
        }

        return OrchestrationReports.List(inventory, steps, questions, json);
    }

    /// <summary>
    /// What a run's engine is doing, as its records and its owner say it: none recorded; running; pausing, its handoffs
    /// being written; recording the machine steps a completed pause left running; or gone - an owner recorded whose
    /// process has ended.
    /// </summary>
    private FlowEngineState EngineOf(OrchestratorLayout orchestrator, Flow.FlowFile flow)
        => EngineOf(_store, _owner, orchestrator, flow);

    /// <summary>
    /// The one rule of what a run's engine is doing, as <c>list-orchestrator</c> and a run's own facts read it: no owner
    /// recorded; a live one running the run; pausing, its handoffs being written; recording the machine steps a completed
    /// pause left running; or gone - an owner recorded whose process has ended.
    /// </summary>
    /// <param name="store">Reads the run's owner.</param>
    /// <param name="owner">Tells whether a recorded owner still stands.</param>
    /// <param name="orchestrator">The run.</param>
    /// <param name="flow">The run's state in one file.</param>
    internal static FlowEngineState EngineOf(OrchestrationStore store, Flow.FlowRunOwner owner, OrchestratorLayout orchestrator, Flow.FlowFile flow)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(flow);

        if (store.ReadOwner(orchestrator) is not { } held)
        {
            return new FlowEngineState(FlowEngineState.None, null, []);
        }

        if (!owner.Stands(held))
        {
            return new FlowEngineState(FlowEngineState.Gone, owner.Named(held), []);
        }

        // Once the pause is complete, what its record still holds as running is what the pause left to finish: the
        // machine steps, the only ones a pause lets run on.
        return flow.Pause switch
        {
            { CompletedAt: null } => new FlowEngineState(FlowEngineState.Pausing, owner.Named(held), []),
            { CompletedAt: not null } => new FlowEngineState(FlowEngineState.Recording, owner.Named(held), flow.MachineStepsRunning()),
            _ => new FlowEngineState(FlowEngineState.Running, owner.Named(held), []),
        };
    }

    /// <summary>
    /// One agent as the listing reports it: where its worktree is, from its own record - the worktrees root it was made
    /// under - and whether anything is there, asked of that path; for a record that does not read, the configured root.
    /// An agent that reads the main tree has no worktree, so it names none.
    /// </summary>
    private AgentInventory Inventory(HarnessLayout layout, string configuredRoot, OrchestratorLayout orchestrator, AgentEntry agent)
    {
        var path = agent.Record is { Reads: true }
            ? null
            : agent.Record?.WorktreePath(layout) ?? WorktreeAddress.Nested(orchestrator.Name, agent.Name).PathUnder(layout.WorktreesDirectoryUnder(configuredRoot));

        return new AgentInventory(agent, path is not null && WorktreeExists(path), path, Evidence(orchestrator, agent.Name), Files(orchestrator.TranscriptsDirectory(agent.Name)));
    }

    /// <summary>Whether anything is at an agent's worktree path; a path that cannot be looked at is said to be there, never gone.</summary>
    private bool WorktreeExists(string path)
    {
        try
        {
            return _fileSystem.KindOf(path) != PathKind.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The keepings of the evidence of <paramref name="agent"/>, each as its records name it.</summary>
    private IReadOnlyList<string> Evidence(OrchestratorLayout orchestrator, string agent)
        => _fileSystem.DirectoryExists(orchestrator.EvidenceDirectory(agent))
            ? [.. _fileSystem.EnumerateDirectories(orchestrator.EvidenceDirectory(agent))
                .Select(directory => OrchestratorLayout.KeptEvidence(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))))
                .Order(StringComparer.Ordinal)]
            : [];

    private int Files(string directory)
        => _fileSystem.DirectoryExists(directory) ? _fileSystem.EnumerateFiles(directory, recursive: true).Count() : 0;

    /// <summary>
    /// The files one agent kept that delete-orchestrator deletes only with --delete-evidence, by what they are.
    /// </summary>
    /// <param name="Name">The agent.</param>
    /// <param name="Evidence">Its kept evidence's files.</param>
    /// <param name="Transcripts">Its kept Claude transcripts' files.</param>
    /// <param name="Steps">The files of the steps that ran as it.</param>
    /// <param name="Sessions">The files of its conversations with an API client.</param>
    /// <param name="Report">A run's own report files; always 0 for an agent.</param>
    private sealed record KeptOf(string Name, int Evidence, int Transcripts, int Steps, int Sessions, int Report = 0)
    {
        /// <summary>
        /// What <paramref name="kept"/> hold between them: the one wording of the refusal and the detail. Evidence and
        /// transcripts are always named, as they always were; steps and sessions only where there are any, so an
        /// orchestrator whose agents kept none says what it always said.
        /// </summary>
        /// <param name="kept">The agents that keep any.</param>
        public static string Said(IReadOnlyList<KeptOf> kept)
        {
            var steps = kept.Sum(agent => agent.Steps);
            var sessions = kept.Sum(agent => agent.Sessions);
            var report = kept.Sum(agent => agent.Report);
            string[] said =
            [
                ReportText.Counted(kept.Sum(agent => agent.Evidence), "evidence file"),
                ReportText.Counted(kept.Sum(agent => agent.Transcripts), "transcript file"),
                .. steps == 0 ? [] : new[] { ReportText.Counted(steps, "step file") },
                .. sessions == 0 ? [] : new[] { ReportText.Counted(sessions, "session file") },
                .. report == 0 ? [] : new[] { ReportText.Counted(report, "report file") },
            ];

            return $"{string.Join(", ", said[..^1])} and {said[^1]}";
        }
    }


}

/// <summary>One orchestrator, as list-orchestrator reports it.</summary>
/// <param name="Layout">Where it is kept.</param>
/// <param name="Record">Its record.</param>
/// <param name="Agents">Its agents.</param>
/// <param name="WorktreesWithoutAgent">Worktrees below its directory that no agent of it records.</param>
/// <param name="Open">The agents that count against its limit (<see cref="OrchestrationRules.OpenAgents"/>).</param>
/// <param name="Run">What it is as a run - the flow's own lines from <c>flow.json</c> - where it holds one.</param>
public sealed record OrchestratorInventory(
    OrchestratorLayout Layout,
    OrchestratorRecord Record,
    IReadOnlyList<AgentInventory> Agents,
    IReadOnlyList<WorktreeListing> WorktreesWithoutAgent,
    IReadOnlyList<string> Open,
    FlowRunInventory? Run = null);

/// <summary>
/// One run as list-orchestrator reports it, read from <c>flow.json</c> and worked nothing out again: what the engine
/// wrote there at its last change of state is what runs next, so a person, a resume and a front end see one answer.
/// </summary>
/// <param name="Flow">The run's state in one file.</param>
/// <param name="Questions">The run's questions and answers, where it holds them.</param>
/// <param name="Engine">What the run's engine is doing.</param>
/// <param name="Updates">The run's plan updates, where it holds them.</param>
public sealed record FlowRunInventory(Flow.FlowFile Flow, Flow.FlowQuestionsFile? Questions, FlowEngineState Engine, Flow.FlowUpdatesFile? Updates = null)
{
    /// <summary>The run's status, lower-case, as the listing says it.</summary>
    public string Status => Flow.Status.ToLower(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What a run's engine is doing, as <c>list-orchestrator</c> says it.</summary>
/// <param name="State">One of <see cref="None"/>, <see cref="Running"/>, <see cref="Pausing"/>, <see cref="Recording"/> and <see cref="Gone"/>.</param>
/// <param name="Process">The engine's process as a line names it; null where none is recorded.</param>
/// <param name="LeftRunning">The machine steps a completed pause left running, which the engine stays to record; empty in every other state.</param>
public sealed record FlowEngineState(string State, string? Process, IReadOnlyList<string> LeftRunning)
{
    /// <summary>No engine is recorded: the run is between sessions.</summary>
    public const string None = "none";

    /// <summary>A live engine drives the run.</summary>
    public const string Running = "running";

    /// <summary>A live engine is pausing the run: its handoffs are being written.</summary>
    public const string Pausing = "pausing";

    /// <summary>The pause is complete, and the live engine stays only to record the machine steps it left running.</summary>
    public const string Recording = "recording";

    /// <summary>An engine is recorded whose process has ended: what a crash or <c>stop-orchestrator --now</c> leaves, reclaimed by the next session.</summary>
    public const string Gone = "gone";

    /// <summary>The engine in words, as the listing's line says it.</summary>
    public string Said => State switch
    {
        None => "no engine",
        Gone => $"its engine ({Process}) is gone",
        Recording => $"its engine ({Process}) is recording {LeftRunning.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} machine step{(LeftRunning.Count == 1 ? string.Empty : "s")}{(LeftRunning.Count == 0 ? string.Empty : $": {string.Join(", ", LeftRunning)}")}",
        _ => $"its engine ({Process}) is {State}",
    };
}

/// <summary>One agent, as list-orchestrator reports it.</summary>
/// <param name="Entry">Its directory's record, or why it has none that reads.</param>
/// <param name="WorktreeExists">Whether anything is at its worktree's path.</param>
/// <param name="WorktreePath">Where its worktree is, or was: under the worktrees root it was made under; null where it reads the main tree and has none.</param>
/// <param name="Evidence">The evidence directories kept for it, relative to its directory.</param>
/// <param name="Transcripts">How many transcript files are kept for it.</param>
public sealed record AgentInventory(
    AgentEntry Entry,
    bool WorktreeExists,
    string? WorktreePath,
    IReadOnlyList<string> Evidence,
    int Transcripts);

/// <summary>The context every orchestrator command works in: the main checkout's, whichever tree it was started in.</summary>
internal static class MainTree
{
    /// <summary>
    /// The main checkout's context: orchestrators, their agents' worktrees, and the configuration naming the worktrees
    /// root all belong to it, and an agent's worktree has a configuration of its own, which may say otherwise.
    /// </summary>
    public static async Task<HarnessContext> LoadAsync(IHarnessContextLoader loader, IHostPlatform platform, string startDirectory, CancellationToken cancellationToken)
    {
        var context = await loader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);

        return context.Layout.IsWorktree(platform)
            ? await loader.LoadAsync(context.Layout.MainCheckoutRoot, cancellationToken).ConfigureAwait(false)
            : context;
    }
}
