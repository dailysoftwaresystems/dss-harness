using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The one who records a run on disk: everything the coordinator tells it, in the one order the run's law names - a
/// step's own files first (its output, then its record, then its handoff where one was written), then the run's state
/// in one file, then its status, then its pre-existing issues where they changed, and the event that tells of it last,
/// so a client woken by an event reads the files that tell of it already written.
/// </summary>
public interface IFlowRecords
{
    /// <summary>Records one change of the run, whole.</summary>
    /// <param name="kind">What changed.</param>
    /// <param name="snapshot">The run whole, as the coordinator holds it.</param>
    /// <param name="instance">The instance the change is about, where one is.</param>
    /// <param name="from">The state the instance stood in before it, where it applies.</param>
    /// <param name="to">The state the instance stands in now, where it applies.</param>
    /// <param name="attempt">Which attempt the change tells of, where one is.</param>
    /// <param name="detail">What the change says in words, where the kinds alone cannot.</param>
    void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null);

    /// <summary>
    /// Records the run's questions and answers, whole, in the run's own file: written before the step's own record
    /// and before the event, for a client woken by either reads what was asked already on disk.
    /// </summary>
    /// <param name="questions">Every question the run raised, in the order raised, with every answer each was given.</param>
    void Question(IReadOnlyList<FlowQuestionEntry> questions);
}

/// <summary>
/// What a run is, as its records name it: its name, the definition it runs and the digest of the definition as it ran,
/// the values it was started with, the commit and the branch it started from, and when. The digest freezes the
/// definition: a resume runs <c>definition.json</c>, never the file the command read, and a config whose entry now
/// differs is refused with a pointer to a new run.
/// </summary>
/// <param name="Run">The run's name.</param>
/// <param name="Definition">The definition, as it ran.</param>
/// <param name="DefinitionDigest">The SHA-256 of the definition as it ran, frozen in <c>definition.json</c>.</param>
/// <param name="Inputs">The values the run was started with, by input name.</param>
/// <param name="StartCommit">The commit the run started from; null where it started beside the tree's history.</param>
/// <param name="Branch">The branch the run started on.</param>
/// <param name="StartedAt">When the run began.</param>
public sealed record FlowRunContext(
    string Run,
    WorkflowDefinition Definition,
    string DefinitionDigest,
    IReadOnlyDictionary<string, string> Inputs,
    string? StartCommit,
    string? Branch,
    DateTimeOffset StartedAt)
{
    /// <summary>What a run is, from the definition it runs: its digest is of the definition as this build freezes it.</summary>
    /// <param name="run">The run's name.</param>
    /// <param name="definition">The definition, as it was read.</param>
    /// <param name="inputs">The values the run was started with, by input name.</param>
    /// <param name="startCommit">The commit the run started from; null where it started beside the tree's history.</param>
    /// <param name="branch">The branch the run started on.</param>
    /// <param name="startedAt">When the run began.</param>
    public static FlowRunContext Of(string run, WorkflowDefinition definition, IReadOnlyDictionary<string, string>? inputs = null, string? startCommit = null, string? branch = null, DateTimeOffset? startedAt = null)
        => new(
            run,
            definition ?? throw new ArgumentNullException(nameof(definition)),
            DigestOf(JsonSerializer.Serialize(definition, JsonStateFile.Options)),
            inputs ?? new Dictionary<string, string>(),
            startCommit,
            branch,
            startedAt ?? DateTimeOffset.UtcNow);

    /// <summary>The SHA-256 of the definition as this build freezes it, spelt in lower-case hex.</summary>
    /// <param name="frozen">The definition, serialized as <c>definition.json</c> holds it.</param>
    public static string DigestOf(string frozen)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(frozen)));
}

/// <summary>
/// Writes a run's status, <c>workflow.json</c>, alone: atomically, whole, from the run's context and the coordinator's
/// snapshot - every step of the definition from the start (<see cref="FlowInstanceState.NotStarted"/>), its instances,
/// the statuses as the engine's identifiers, UTC <c>yyyy-MM-dd HH:mm:ss</c> times with <see langword="null"/> kept,
/// <c>seconds</c>, <c>progress</c> and <c>waitingOn</c>. Nothing else writes the file, and it holds no prompt, no
/// output, no path outside the run and no secret: what it says is safe to show to anyone who may see the run.
/// </summary>
public sealed class WorkflowStatusWriter
{
    private readonly OrchestrationStore _store;
    private readonly OrchestratorLayout _layout;
    private readonly FlowRunContext _context;

    /// <summary>Writes the status of <paramref name="context"/>'s run, kept as <paramref name="layout"/> spells it.</summary>
    /// <param name="store">Writes the file, atomically.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="context">What the run is.</param>
    public WorkflowStatusWriter(OrchestrationStore store, OrchestratorLayout layout, FlowRunContext context)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(context);

        _store = store;
        _layout = layout;
        _context = context;
    }

    /// <summary>Writes the run's status from <paramref name="snapshot"/>, whole, stamped now.</summary>
    /// <param name="snapshot">The run whole, as the coordinator holds it.</param>
    /// <param name="at">When this status was written.</param>
    public void Write(FlowSnapshot snapshot, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _store.WriteWorkflowStatus(_layout, StatusOf(_context, snapshot, at));
    }

    /// <summary>The run's status, as <paramref name="snapshot"/> says it and <paramref name="context"/> names it.</summary>
    /// <param name="context">What the run is.</param>
    /// <param name="snapshot">The run whole.</param>
    /// <param name="at">When this status was written.</param>
    public static WorkflowStatus StatusOf(FlowRunContext context, FlowSnapshot snapshot, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);

        var steps = new Dictionary<string, WorkflowStepStatus>(StringComparer.Ordinal);

        foreach (var path in FlowScope.Declared(context.Definition).Select(declared => declared.Path))
        {
            steps[path] = StepStatusOf([.. snapshot.Steps.Where(step => string.Equals(step.Step, path, StringComparison.Ordinal))]);
        }

        var waiting = new List<string>(snapshot.WaitingOn);

        if (snapshot.Pause is { } pause && (pause.CompletedAt is null || snapshot.Status is FlowRunStatus.Paused))
        {
            waiting.Insert(0, $"safe pause ({pause.Cause})");
        }

        // The recovery the run is in, where a failure it took is being put right: one instance of the run is at work
        // on it, and the progress says so.
        var recovering = snapshot.Steps.Any(step => step.Instance == FlowScope.Recovery || step.Instance.StartsWith(FlowScope.Recovery + "+", StringComparison.Ordinal))
            && snapshot.Steps.Any(step => (step.Instance == FlowScope.Recovery || step.Instance.StartsWith(FlowScope.Recovery + "+", StringComparison.Ordinal))
                && step.State is FlowInstanceState.NotStarted or FlowInstanceState.Running or FlowInstanceState.Paused);

        return new WorkflowStatus(
            1,
            context.Run,
            context.Definition.Name,
            snapshot.Status.ToString(),
            context.StartedAt,
            at,
            new WorkflowProgress(
                snapshot.Steps.Count,
                snapshot.Steps.Count(step => step.State is FlowInstanceState.NotStarted),
                snapshot.Steps.Count(step => step.State is FlowInstanceState.Running),
                recovering ? 1 : 0,
                snapshot.Steps.Count(step => step.State is FlowInstanceState.Paused),
                snapshot.Steps.Count(step => step.State is FlowInstanceState.Done),
                snapshot.Steps.Count(step => step.State is FlowInstanceState.Failed),
                snapshot.Steps.Count(step => step.State is FlowInstanceState.Skipped),
                snapshot.Steps.Count(step => step.State is FlowInstanceState.Stopped),
                snapshot.Steps.Count(step => step.State is FlowInstanceState.NotRun)),
            steps,
            snapshot.EndedAt,
            waiting.Count == 0 ? null : waiting);
    }

    /// <summary>One step of the definition, as its instances together stand: the first start, the last finish, and the status the run would show for it.</summary>
    private static WorkflowStepStatus StepStatusOf(IReadOnlyList<FlowSnapshotStep> instances)
    {
        var perInstance = new Dictionary<string, WorkflowInstanceStatus>(StringComparer.Ordinal);

        foreach (var instance in instances)
        {
            perInstance[instance.Instance] = new WorkflowInstanceStatus(
                instance.State.ToString(),
                instance.Visit,
                instance.Attempt,
                0,
                instance.StartedAt,
                instance.FinishedAt,
                SecondsOf(instance.StartedAt, instance.FinishedAt));
        }

        return new WorkflowStepStatus(
            instances.Select(instance => instance.State).DefaultIfEmpty(FlowInstanceState.NotStarted).Aggregate(Worst).ToString(),
            perInstance,
            instances.Count == 0 ? null : instances.Min(instance => instance.StartedAt),
            instances.Count == 0 ? null : instances.Max(instance => instance.FinishedAt),
            instances.Count == 0 ? null : instances.Sum(instance => SecondsOf(instance.StartedAt, instance.FinishedAt)));
    }

    /// <summary>What the run would show for a step that holds one of each of two states.</summary>
    private static FlowInstanceState Worst(FlowInstanceState left, FlowInstanceState right) => Rank(left) >= Rank(right) ? left : right;

    /// <summary>The order of what the run would show: what still moves first, what ended worst next, what never ran last.</summary>
    private static int Rank(FlowInstanceState state) => state switch
    {
        FlowInstanceState.Running => 8,
        FlowInstanceState.Paused => 7,
        FlowInstanceState.Stopped => 6,
        FlowInstanceState.Failed => 5,
        FlowInstanceState.Done => 4,
        FlowInstanceState.Skipped => 3,
        FlowInstanceState.NotRun => 2,
        _ => 1,
    };

    /// <summary>What an instance took, in seconds; null until it finished.</summary>
    private static long? SecondsOf(DateTimeOffset? startedAt, DateTimeOffset? finishedAt)
        => startedAt is { } began && finishedAt is { } ended ? (long)Math.Max(0, (ended - began).TotalSeconds) : null;
}

/// <summary>
/// Writes a run's pre-existing issues, <c>pre-existing-issues.json</c>, alone: created empty with the run, an entry
/// added at each triage answer <c>list</c>, always whole and atomically. Nothing else writes the file, and it holds
/// nothing but the issue and its proof - no prompt, no secret - with a quote or an output cut to 4,000 characters, the
/// cut said.
/// </summary>
public sealed class PreExistingIssuesWriter
{
    private readonly OrchestrationStore _store;
    private readonly OrchestratorLayout _layout;
    private readonly string _run;

    /// <summary>Writes the issues of <paramref name="run"/>, kept as <paramref name="layout"/> spells it.</summary>
    /// <param name="store">Writes the file, atomically.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="run">The run's name.</param>
    public PreExistingIssuesWriter(OrchestrationStore store, OrchestratorLayout layout, string run)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);

        _store = store;
        _layout = layout;
        _run = run;
    }

    /// <summary>Writes the run's list as it stands, whole: empty where the run holds none, which is how it is created.</summary>
    /// <param name="issues">Each issue the run found and left.</param>
    /// <param name="at">When this list was written.</param>
    public void Write(IReadOnlyList<PreExistingIssue> issues, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(issues);

        foreach (var issue in issues)
        {
            if (issue.Problem() is { } problem)
            {
                throw new InvalidOperationException($"Refusing to write an issue that does not read back: {problem}.");
            }
        }

        _store.WritePreExistingIssues(_layout, new PreExistingIssuesFile(1, _run, at, issues));
    }

    /// <summary>
    /// Adds one issue the triage answered <c>list</c> for: read whole, the entry appended, written whole - so a crash
    /// between an answer and the file never loses an entry or adds one twice.
    /// </summary>
    /// <param name="title">What it is, in a line.</param>
    /// <param name="description">What it is, in the words the triage gave.</param>
    /// <param name="where">The files it is in, as the repository spells them.</param>
    /// <param name="proof">What the triage read or ran on the base commit.</param>
    /// <param name="why">Which of the three tests it passed none of.</param>
    /// <param name="foundBy">The instance that found it.</param>
    /// <param name="foundAt">When it was found.</param>
    /// <param name="at">When this list was written.</param>
    /// <exception cref="InvalidOperationException">The run's list is not there to add to.</exception>
    public PreExistingIssue Add(
        string title,
        string description,
        IReadOnlyList<string> where,
        PreExistingProof proof,
        string why,
        string foundBy,
        DateTimeOffset foundAt,
        DateTimeOffset at)
    {
        var issues = _store.ReadPreExistingIssues(_layout)
            ?? throw new InvalidOperationException($"'{_layout.PreExistingIssuesFile}' is not there, so the issues of run '{_run}' have no list to add to.");

        var entry = new PreExistingIssue(
            $"pe-{(issues.Issues.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            title,
            description,
            where,
            proof,
            why,
            foundBy,
            foundAt);

        Write([.. issues.Issues, entry], at);
        return entry;
    }

    /// <summary>Cuts a quote or an output to the most it may hold, the cut said.</summary>
    /// <param name="text">What was read or run.</param>
    public static string Cut(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Length <= PreExistingProof.Most ? text : text[..PreExistingProof.Most] + $"… (cut at {PreExistingProof.Most.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters)";
    }
}

/// <summary>
/// The run's records on disk, as the coordinator hands them to it: each step's directory where the rule puts it - a
/// step that names an agent with <c>in</c> or <c>of</c> inside that agent's, every other under the run - the run's
/// state in one file, its status, its issues, and its events, in that order. State files are the truth; the events are
/// history, and they are written last.
/// </summary>
/// <remarks>
/// A reason step that names no agent keeps its directory under the run here: the agent the engine would make for it -
/// a read-only one, named as every agent is - is not built yet, and nothing guesses a name. The rule is the rule; the
/// one step it does not yet place is named in the slice's landed entry.
/// </remarks>
public sealed class FlowRunRecords : IFlowRecords, IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly OrchestrationStore _store;
    private readonly OrchestratorLayout _layout;
    private readonly FlowRunContext _context;
    private readonly TimeProvider _clock;
    private readonly WorkflowStatusWriter _status;
    private readonly PreExistingIssuesWriter _issues;
    private readonly FlowEventLog _events;

    /// <summary>One writer of the run's status, and of nothing else.</summary>
    public WorkflowStatusWriter Status => _status;

    /// <summary>One writer of the run's pre-existing issues, and of nothing else.</summary>
    public PreExistingIssuesWriter Issues => _issues;

    /// <summary>Opens the run's records: the run's directory, its event log once for every change this session appends to, and the definition frozen where it is not there.</summary>
    /// <param name="fileSystem">Writes the files.</param>
    /// <param name="store">Writes the records, strictly.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="context">What the run is.</param>
    /// <param name="clock">When each record is written.</param>
    public FlowRunRecords(IFileSystem fileSystem, OrchestrationStore store, OrchestratorLayout layout, FlowRunContext context, TimeProvider? clock = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _clock = clock ?? TimeProvider.System;
        _status = new WorkflowStatusWriter(store, layout, context);
        _issues = new PreExistingIssuesWriter(store, layout, context.Run);

        _fileSystem.CreateDirectory(_layout.Directory);
        _events = FlowEventLog.Open(_fileSystem, _layout.EventsFile);

        // The definition is what runs: frozen at the run's start, and never the file the command read. A resume runs
        // this one and checks nothing else.
        if (_store.ReadDefinition(_layout) is null)
        {
            _store.WriteDefinition(_layout, _context.Definition);
        }

        // The issues are created empty with the run, before anything can change them.
        if (_store.ReadPreExistingIssues(_layout) is null)
        {
            _issues.Write([], _clock.GetUtcNow());
        }

        // The run's questions and answers are created empty with it too: the one owner of what every question said
        // is there to read from the start, and every entry it ever holds is written whole through this one record.
        if (_store.ReadFlowQuestions(_layout) is null)
        {
            _store.WriteFlowQuestions(_layout, new FlowQuestionsFile(1, _context.Run, _clock.GetUtcNow(), []));
        }
    }

    /// <inheritdoc/>
    void IFlowRecords.Question(IReadOnlyList<FlowQuestionEntry> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        _store.WriteFlowQuestions(_layout, new FlowQuestionsFile(1, _context.Run, _clock.GetUtcNow(), questions));
    }

    /// <inheritdoc/>
    void IFlowRecords.Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance, string? from, string? to, int? attempt, string? detail)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var at = _clock.GetUtcNow();
        var step = instance is null ? null : snapshot.Steps.FirstOrDefault(held => string.Equals(held.Instance, instance, StringComparison.Ordinal));

        if (kind is FlowChangeKind.Step && step is not null)
        {
            // The step's own files first: its output, then its record, in the step's own directory.
            WriteStepFiles(step);
        }

        if (kind is FlowChangeKind.Handoff && step is not null)
        {
            // The handoff is the step's own file too, and its record says who wrote it: both are written before
            // anything says the handoff was.
            WriteStepFiles(step);
            WriteHandoffFile(step);
        }

        // Then the run's state in one file, then its status.
        _store.WriteFlow(_layout, FlowOf(snapshot));
        _status.Write(snapshot, at);

        // Then the event that tells of it, last: a client woken by it reads the files already written.
        _ = _events.Append(
            at,
            kind switch
            {
                FlowChangeKind.SessionBegan => "sessionBegan",
                FlowChangeKind.Resumed => "resumed",
                FlowChangeKind.PauseBegan => "pauseBegan",
                FlowChangeKind.Handoff => "handoff",
                FlowChangeKind.PauseCompleted => "pauseCompleted",
                FlowChangeKind.SessionEnded => "sessionEnded",
                FlowChangeKind.Answer => "answer",
                _ => "step",
            },
            step?.Instance,
            step?.Step,
            from,
            kind is FlowChangeKind.SessionEnded ? snapshot.Status.ToString() : to,
            step?.Attempt ?? attempt,
            detail);
    }

    /// <summary>The run's state in one file, from the coordinator's snapshot.</summary>
    /// <param name="snapshot">The run whole.</param>
    public FlowFile FlowOf(FlowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new FlowFile(
            1,
            _context.Run,
            _context.Definition.Name,
            _context.DefinitionDigest,
            _context.Inputs,
            snapshot.Status.ToString(),
            new FlowBudgetRecord(snapshot.Spent.Usd, snapshot.Spent.Tokens, snapshot.Spent.Minutes),
            snapshot.BudgetGranted,
            [.. snapshot.Steps.Select(StepRecordOf)],
            [.. snapshot.Next],
            _context.StartCommit,
            _context.Branch,
            snapshot.Pause is { } pause
                ? new FlowPauseRecord(pause.Cause, pause.All, pause.BeganAt, [.. pause.Handoffs], pause.CompletedAt)
                : null,
            snapshot.Redirect);
    }

    /// <summary>One step's own record, from the coordinator's snapshot of it.</summary>
    /// <param name="step">The step, whole.</param>
    public FlowStepRecord StepRecordOf(FlowSnapshotStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return new FlowStepRecord(
            step.Instance,
            step.Step,
            step.Group,
            step.State.ToString(),
            step.Attempt,
            step.Visit,
            step.Answers,
            step.WaitsOn,
            step.Type,
            step.StartedAt,
            step.FinishedAt,
            step.Exit,
            step.Error,
            step.SkippedBecause,
            step.NotRunBecause,
            step.PassedOver ? "passedOver" : step.EndedRun ? "endedRun" : null,
            step.EndedByQuestion,
            step.Agent,
            step.Made,
            step.Question,
            step.Handoff is { } handoff ? new FlowHandoffRecord(handoff.By, handoff.Why) : null,
            step.Output?.DeepClone(),
            step.Item.IsMissing ? null : step.Item.ToNode(),
            step.Index,
            step.Iteration,
            step.Handlers,
            step.HandledBy,
            step.Leftover?.DeepClone(),
            step.RaisedBy,
            step.Direction);
    }

    /// <summary>
    /// Where one step's own directory is: inside the agent it names with <c>in</c> or <c>of</c> - for a reason step
    /// too, where it names one - and under the run for every step that names none.
    /// </summary>
    /// <param name="step">The step, whole.</param>
    public string StepDirectoryOf(FlowSnapshotStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var onDisk = FlowInstanceId.DirectoryOf(step.Instance);

        return step.Agent is { } agent
            ? _layout.AgentStepDirectory(agent, onDisk)
            : _layout.StepDirectory(onDisk);
    }

    /// <summary>Closes the run's records: its event log, its last line whole.</summary>
    public void Dispose() => _events.Dispose();

    /// <summary>Writes one step's own files: its output, validated, then its record, in the step's own directory.</summary>
    private void WriteStepFiles(FlowSnapshotStep step)
    {
        var directory = StepDirectoryOf(step);

        _fileSystem.CreateDirectory(directory);

        if (step.Output is not null)
        {
            _fileSystem.WriteAllTextAtomic(Path.Combine(directory, "output.json"), JsonSerializer.Serialize(step.Output, JsonStateFile.Options) + "\n");
        }

        _store.WriteStepRecord(Path.Combine(directory, "step.json"), StepRecordOf(step));

        // The handoff is spent where the record says the step holds none: the file is removed once the attempt it was
        // written for has been given it, and the record is the truth of whether it holds one.
        var handoff = Path.Combine(directory, "handoff.md");

        if (step.Handoff is null && _fileSystem.KindOf(handoff) == PathKind.File)
        {
            _fileSystem.DeleteFile(handoff);
        }
    }

    /// <summary>Writes one step's handoff, in its own directory, from the coordinator's record of it.</summary>
    private void WriteHandoffFile(FlowSnapshotStep step)
    {
        if (step.Handoff is not { } handoff)
        {
            return;
        }

        var directory = StepDirectoryOf(step);

        _fileSystem.CreateDirectory(directory);
        _fileSystem.WriteAllTextAtomic(Path.Combine(directory, "handoff.md"), handoff.Text + "\n");
    }
}
