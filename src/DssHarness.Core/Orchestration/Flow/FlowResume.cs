using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The state a killed or stopped run left, as a resume takes it up: the run's state in one file, reconciled against
/// every step's own record - a step's own record is written first, so after a crash the two can differ by the step
/// whose record was written last, and the step's own is the truth - with the handoff of every stopped step read from
/// its own directory.
/// </summary>
/// <param name="File">The run's state, reconciled.</param>
/// <param name="Differing">Each instance whose own record the index did not agree with, in the index's order.</param>
/// <param name="Adopted">Each instance a step directory holds that the index did not name, in the order they were found.</param>
/// <param name="Handoffs">Each stopped step's handoff, by instance id, read from its own directory where it holds one.</param>
/// <param name="Questions">Every question the run raised and every answer it was given, the open ones among them, as the run's one file of them holds it.</param>
/// <param name="Issues">Every pre-existing issue the run listed, proven, as the run's own file of them holds it.</param>
/// <param name="Refused">Every answer handed over through the inbox that the run refused, as its file of questions holds them; null where none was.</param>
/// <param name="AffectedTests">Each key's test units the run kept, as its own record of them holds them; null where the state was read from no record.</param>
/// <param name="NextRound">Every issue the run owes its next round, as its own file of them holds it; none where the run's directory holds no file of them.</param>
/// <param name="LastRecordAt">
/// When the session before last wrote a record - its last whole event's time, every state change writing one - the
/// time the run is taken to have stood still from: what was open then is closed there, and the time from there to the
/// resume is its pause. Null where the run's events hold none.
/// </param>
public sealed record FlowResumeState(
    FlowFile File,
    IReadOnlyList<string> Differing,
    IReadOnlyList<string> Adopted,
    IReadOnlyDictionary<string, string> Handoffs,
    IReadOnlyList<FlowQuestionEntry>? Questions = null,
    IReadOnlyList<PreExistingIssue>? Issues = null,
    IReadOnlyList<FlowRefusedAnswer>? Refused = null,
    IReadOnlyList<FlowAffectedTests>? AffectedTests = null,
    IReadOnlyList<FlowNextIssue>? NextRound = null,
    DateTimeOffset? LastRecordAt = null);

/// <summary>
/// Reads what a killed or stopped run left and hands it to a resume: the run's state in one file checked against the
/// step records - the run's (<c>steps/*/step.json</c>) and each agent's (<c>agents/*/steps/*/step.json</c>) - taking
/// the step's own record wherever there is one and adopting what only a step's directory names, then writing the index
/// again in the order the run made its steps. A record that cannot be read, or that the run should hold and does not, is
/// refused, named: a resume is never built on a record guessed at, nor on an empty one put in a missing one's place.
/// </summary>
/// <param name="store">Reads the records, strictly.</param>
/// <param name="layout">The run's layout.</param>
/// <param name="fileSystem">Reads the step directories and writes the index again.</param>
public sealed class FlowResume(OrchestrationStore store, OrchestratorLayout layout, IFileSystem fileSystem)
{
    private readonly OrchestrationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly OrchestratorLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>
    /// Refuses (13), with a pointer to a new run, a run whose definition the repository's entry no longer says: the run
    /// keeps the definition it began with, and every record it wrote was written under it. A definition is changed only
    /// by what it says, so what each says is compared (<see cref="Said"/>), both as this build reads them - the entry as
    /// the definition's check made it now, and the run's <c>definition.json</c> as this build reads a frozen one
    /// (<see cref="FrozenDefinition"/>) - and both written by this build's one serializer: never against the digest
    /// <c>flow.json</c> holds, the record of what the run began with, which the build that began it made of the same words
    /// written its own way. The one owner of the rule.
    /// </summary>
    /// <param name="file">The run's state in one file, which names the workflow it runs.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="frozen">The definition the run began with, as this build reads its <c>definition.json</c>.</param>
    /// <param name="entry">The repository's entry for the workflow, as the definition's check reads it now.</param>
    /// <exception cref="HarnessException">The two say different things (<see cref="HarnessExit.Refused"/>).</exception>
    public static void RefuseAChangedDefinition(FlowFile file, OrchestratorLayout layout, WorkflowDefinition frozen, WorkflowDefinition entry)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.Equals(Said(entry), Said(frozen), StringComparison.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"workflow '{file.Definition}', as the repository's config declares it now, has changed since run '{layout.Name}' began, and a run goes on only under the definition it began with; "
                + $"{FlowService.RunCommand} {file.Definition} starts a new run of it. Nothing of run '{layout.Name}' was changed.");
        }
    }

    /// <summary>
    /// The digest of what <paramref name="definition"/> says, as this build writes it (<see cref="FlowRunContext.DigestOf(WorkflowDefinition)"/>),
    /// each use's fingerprint left out. The use it stands for is in the definition whole - the called workflow's steps,
    /// its inputs, params, outputs and recovery, the settings of its own it carries, its texts and the keys it declares
    /// that nothing applies - and is compared there, as every other step is: the one owner of whether what a used
    /// workflow brings the run changed. A local use's fingerprint is a digest the build that began the run made of the
    /// called file, which no frozen definition holds to make again; a remote one's commit is in its name.
    /// </summary>
    /// <param name="definition">The definition, as this build reads it.</param>
    internal static string Said(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return FlowRunContext.DigestOf(definition with
        {
            Steps = Compared(definition.Steps),
            Recover = Compared(definition.Recover),
        });
    }

    /// <summary>Each of <paramref name="steps"/> as <see cref="Said"/> compares it, at every depth: each use's fingerprint left out.</summary>
    private static IReadOnlyList<WorkflowStep> Compared(IReadOnlyList<WorkflowStep> steps)
        => [.. steps.Select(step => step with
        {
            Steps = Compared(step.Steps),
            Use = step.Use is { } use ? use with { Fingerprint = string.Empty, Recover = Compared(use.Recover) } : null,
        })];

    /// <summary>A recovery as <see cref="Said"/> compares it, its steps' uses' fingerprints left out; null where there is none.</summary>
    private static FlowRecover? Compared(FlowRecover? recover)
        => recover is null ? null : recover with { Steps = Compared(recover.Steps) };

    /// <summary>
    /// The state the run left, reconciled: every step's own record read beside the index and taken wherever there is
    /// one, what only a step's directory holds adopted at the end, the stopped steps' handoffs read from their own
    /// directories, and the index written again over what the reconciliation found.
    /// </summary>
    /// <param name="entry">The repository's entry for the workflow, as the caller's definition check reads it now; where what it says differs from what the run's frozen definition says, the resume is refused with a pointer to a new run (<see cref="RefuseAChangedDefinition"/>).</param>
    /// <exception cref="HarnessException">
    /// The run holds no state, no definition, its questions, its issues or its affected tests; a record does not read; a record names a handoff its
    /// directory does not hold; or the definition differs (<see cref="HarnessExit.Refused"/>).
    /// </exception>
    public FlowResumeState Load(WorkflowDefinition entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var file = _store.ReadFlow(_layout)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{_layout.FlowFile}' is not there, so run '{_layout.Name}' holds no state to take up");
        var frozen = _store.ReadDefinition(_layout)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{_layout.DefinitionFile}' is not there, so run '{_layout.Name}' holds no definition to go on with");

        RefuseAChangedDefinition(file, _layout, frozen, entry);

        // The run's questions, its issues and its affected tests are made with it and never removed: one missing is
        // something that removed it, and an empty one in its place would leave a step waiting on a question nobody can
        // answer, or a gate running none of the tests the run's work affects.
        var questions = _store.ReadFlowQuestions(_layout)
            ?? throw Missing(_layout.QuestionsFile, "its questions and answers", "a step waiting on a question would wait for ever, and answer-orchestrator could not answer it");
        var issues = _store.ReadPreExistingIssues(_layout)
            ?? throw Missing(_layout.PreExistingIssuesFile, "its pre-existing issues", "the issues it listed would be lost from its report");
        var affected = _store.ReadAffectedTests(_layout)
            ?? throw Missing(_layout.AffectedTestsFile, "its affected tests", "the tests its finished work affects would be lost from every step that reads run.affectedTests");

        // The next round's issues are read as none where the run's directory holds no file of them: a run an older
        // build began never had one, and an empty round in its place loses nothing it owed.
        var next = _store.ReadNextRound(_layout)?.Issues ?? [];

        var records = StepRecords();
        var steps = new List<FlowStepRecord>();
        var differing = new List<string>();
        var adopted = new List<string>();

        // The index's order is the order the run made its steps - each parent before its children - and it is kept.
        foreach (var step in file.Steps)
        {
            if (records.Remove(step.Instance, out var own))
            {
                // The step's own record is written first, so it is the truth wherever there is one; where it says
                // anything the index does not - its state, its handoff, its handlers, its answers - the index was
                // behind it, and the instance is said.
                if (!Same(step, own))
                {
                    differing.Add(step.Instance);
                }

                steps.Add(own);
            }
            else
            {
                steps.Add(step);
            }
        }

        // A step directory the index does not name: the crash's own, between a step's first write and the index's. Each
        // goes after every step the index named, in the order found - the run's, then each agent's.
        foreach (var (instance, own) in records)
        {
            adopted.Add(instance);
            steps.Add(own);
        }

        // What the session before left the state saying of its engine - a stop it ended with, the pause it was left under -
        // stands as written: the claim that began this session marked the run starting (FlowFile.Starting), which no reader
        // takes any of it for this engine's, and this session's own first write says what its engine is doing.
        var reconciled = file with { Steps = [.. steps], Next = [] };
        var handoffs = HandoffsOf(reconciled);

        _store.WriteFlow(_layout, reconciled);

        // Where the session before stood still: its events' last whole line, every state change writing one after the
        // records that tell of it.
        var events = _fileSystem.FileExists(_layout.EventsFile) ? FlowEventLog.Read(_fileSystem, _layout.EventsFile).Events : [];

        return new FlowResumeState(reconciled, differing, adopted, handoffs, questions.Questions, issues.Issues, questions.Refused, affected.Entries, next, events.Count > 0 ? events[^1].At : null);
    }

    /// <summary>Every step record the run's directories hold - the run's, then each agent's - by instance id, each with any handoff its record does not name yet taken back.</summary>
    private OrderedRecords StepRecords()
    {
        var records = new OrderedRecords();
        ReadStepsUnder(_layout.StepsDirectory, records, agent: null);

        if (_fileSystem.DirectoryExists(_layout.AgentsDirectory))
        {
            foreach (var agentDirectory in _fileSystem.EnumerateDirectories(_layout.AgentsDirectory).Order(StringComparer.Ordinal))
            {
                ReadStepsUnder(Path.Combine(agentDirectory, OrchestratorLayout.StepsDirectoryName), records, Path.GetFileName(Path.TrimEndingDirectorySeparator(agentDirectory)));
            }
        }

        return records;
    }

    /// <summary>Reads each step directory under <paramref name="root"/>, in name order.</summary>
    private void ReadStepsUnder(string root, OrderedRecords records, string? agent)
    {
        if (!_fileSystem.DirectoryExists(root))
        {
            return;
        }

        foreach (var directory in _fileSystem.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var recordFile = Path.Combine(directory, OrchestratorLayout.StepRecordFileName);

            if (_fileSystem.KindOf(recordFile) != PathKind.File)
            {
                continue;
            }

            var record = _store.ReadStepRecord(recordFile)
                ?? throw new HarnessException(HarnessExit.Refused, $"'{recordFile}' is not there, so step '{FlowInstanceId.IdOf(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)))}' of '{_layout.Name}' holds no record to read");

            records.Add(record.Instance, WithHandoffTakenBack(record, Path.Combine(directory, OrchestratorLayout.HandoffFileName)));
        }
    }

    /// <summary>
    /// A step's record with the handoff its directory holds taken back where the record does not name it yet: a handoff's
    /// file is written before the record that names it, so a crash between the two leaves the file beside a record still
    /// <c>Running</c>, <c>Stopped</c> or <c>Paused</c> - a step paused on a question holds one for the attempt its
    /// answer goes on with. Who wrote it is read from its first line, as the engine marks its own
    /// (<see cref="FlowHandoffRecord.EngineMark"/>).
    /// </summary>
    private FlowStepRecord WithHandoffTakenBack(FlowStepRecord record, string handoffFile)
    {
        if (record.Handoff is not null
            || record.State is not (nameof(FlowInstanceState.Running) or nameof(FlowInstanceState.Stopped) or nameof(FlowInstanceState.Paused))
            || _fileSystem.KindOf(handoffFile) != PathKind.File)
        {
            return record;
        }

        return record with { Handoff = FlowHandoffRecord.Of(_fileSystem.ReadAllText(handoffFile)) };
    }

    /// <summary>
    /// Each stopped, cut-off or paused step's handoff, read from its own directory: every one whose record names one. A record
    /// that names a handoff its directory does not hold is refused, naming the file - the file is written before the
    /// record that names it, so something removed it, and the engine would otherwise write one of its own in the place
    /// of the agent's account.
    /// </summary>
    private Dictionary<string, string> HandoffsOf(FlowFile file)
    {
        var handoffs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var step in file.Steps.Where(step => step.State is nameof(FlowInstanceState.Stopped) or nameof(FlowInstanceState.Running) or nameof(FlowInstanceState.Paused) && step.Handoff is not null))
        {
            var handoffFile = _layout.InstanceHandoffFile(step.Instance, step.Agent);

            if (_fileSystem.KindOf(handoffFile) != PathKind.File)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"step '{step.Instance}' of run '{_layout.Name}' holds a handoff written by the {step.Handoff!.By}, and '{handoffFile}' is not there. "
                    + "Nothing was changed: a handoff is never written again in the place of one that was lost; put the file back to go on with the run, "
                    + $"or delete-orchestrator {_layout.Name} ends it.");
            }

            // The text as it was handed: the one line end the records write after it taken off, and nothing of its own.
            var written = _fileSystem.ReadAllText(handoffFile);
            handoffs[step.Instance] = written.EndsWith('\n') ? written[..^1] : written;
        }

        return handoffs;
    }

    /// <summary>Whether the index and the step's own record say the same thing of the instance, whole.</summary>
    private static bool Same(FlowStepRecord index, FlowStepRecord own)
        => string.Equals(JsonSerializer.Serialize(index, JsonStateFile.Options), JsonSerializer.Serialize(own, JsonStateFile.Options), StringComparison.Ordinal);

    /// <summary>The refusal of a record the run is made with that is not there.</summary>
    private HarnessException Missing(string path, string what, string consequence)
        => new(
            HarnessExit.Refused,
            $"'{path}' is not there, so run '{_layout.Name}' holds no record of {what}, which is made with the run and never removed; "
            + $"nothing was changed, for a resume over it would go on without it: {consequence}. Put the file back to go on with the run, or delete-orchestrator {_layout.Name} ends it.");

    /// <summary>Step records by instance id, kept in the order they were found.</summary>
    private sealed class OrderedRecords : IEnumerable<KeyValuePair<string, FlowStepRecord>>
    {
        private readonly Dictionary<string, FlowStepRecord> _byId = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];

        public void Add(string instance, FlowStepRecord record)
        {
            if (_byId.TryAdd(instance, record))
            {
                _order.Add(instance);
            }
            else
            {
                _byId[instance] = record;
            }
        }

        public bool Remove(string instance, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FlowStepRecord? record)
        {
            if (_byId.Remove(instance, out record))
            {
                _ = _order.Remove(instance);
                return true;
            }

            return false;
        }

        public IEnumerator<KeyValuePair<string, FlowStepRecord>> GetEnumerator()
            => _order.Select(instance => new KeyValuePair<string, FlowStepRecord>(instance, _byId[instance])).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>
/// The run's owner, claimed at the start of every session - a run, a resume and an answer alike, each inside the
/// repository's one section (<see cref="FlowSessions"/>) - and released wherever the session ends, however it ends:
/// a live owner refuses a session, for the engine that holds the run is recording what a pause left running; a dead one
/// is reclaimed and said, and what its steps started is ended at the resume before anything is continued. The one
/// owner of whether a run's engine is live: every command that asks - a session, a pause, a stop, an answer, the
/// deletion of a run - asks it here.
/// </summary>
/// <param name="store">Writes the record, strictly.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="clock">When the owner took the run.</param>
public sealed class FlowRunOwner(OrchestrationStore store, IProcessIdentity identity, TimeProvider? clock = null)
{
    private readonly ProcessOwner _owner = new(store ?? throw new ArgumentNullException(nameof(store)), identity ?? throw new ArgumentNullException(nameof(identity)), clock);

    private readonly OrchestrationStore _store = store;

    private readonly IProcessIdentity _identity = identity;

    /// <summary>
    /// The engine that holds the run, as a wait on it names it (<see cref="Waits.WhileHeldAsync"/>): its process, the stamp
    /// that tells it from a later one, and its name as a line says it; null where none is live (<see cref="Live"/>).
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    public WaitHolder? Holder(OrchestratorLayout layout)
        => Live(layout) is { } held ? new WaitHolder(held.ProcessId, held.Stamp, $"its engine, {Named(held)}") : null;

    /// <summary>
    /// This process's identity as a wait on the run's engine asks it (<see cref="Waits.WhileHeldAsync"/>): a holder lives
    /// while the run's recorded owner is still that process and live as this owner judges one (<see cref="Live"/>) - an
    /// engine of another machine among them, which no process here can tell ended - and every other question is this
    /// process's own.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    public IProcessIdentity Liveness(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new OwnerLiveness(this, layout, _identity);
    }

    /// <summary>
    /// The engine that holds the run, where one is live: on this machine while its process runs, and on another until
    /// somebody takes it over - nothing here can ask that machine. Null where none is recorded or the recorded one has
    /// gone.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    public RunOwner? Live(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return _owner.Live(layout.OwnerFile, OrchestrationStore.OwnerKind);
    }

    /// <summary>Whether the engine <paramref name="held"/> names still holds the run.</summary>
    /// <param name="held">The owner recorded.</param>
    public bool Stands(RunOwner held) => _owner.Stands(held);

    /// <summary>Whether this process is the run's recorded owner: a claim of its own, never another session's.</summary>
    /// <param name="layout">The run's layout.</param>
    public bool Holds(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return _owner.Holds(layout.OwnerFile, OrchestrationStore.OwnerKind);
    }

    /// <summary>Whether the engine <paramref name="held"/> names runs on this machine.</summary>
    /// <param name="held">The owner recorded.</param>
    public bool IsHere(RunOwner held) => _owner.IsHere(held);

    /// <summary>The engine <paramref name="held"/> names, as a line names it: its process, and that it runs on another machine where it does - never that machine's name.</summary>
    /// <param name="held">The owner recorded.</param>
    public string Named(RunOwner held) => _owner.Named(held);

    /// <summary>A process's liveness as the run's owner is judged, for a wait on the run's engine: see <see cref="Liveness"/>.</summary>
    /// <param name="owner">The run's owner.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="identity">This process.</param>
    private sealed class OwnerLiveness(FlowRunOwner owner, OrchestratorLayout layout, IProcessIdentity identity) : IProcessIdentity
    {
        public int CurrentId => identity.CurrentId;

        public string CurrentMachine => identity.CurrentMachine;

        public string? Current => identity.Current;

        public bool IsAlive(int processId, string? stamp)
            => owner.Live(layout) is { } held && held.ProcessId == processId && string.Equals(held.Stamp, stamp, StringComparison.Ordinal);

        public string? StampOf(int processId) => identity.StampOf(processId);

        public bool IsSame(int processId, string stamp) => identity.IsSame(processId, stamp);
    }

    /// <summary>
    /// Claims the run for this process: refused (13) where a live owner holds it - an engine that paused its run and
    /// is still recording the machine steps the pause left running is a live owner like any other - and a dead one is
    /// reclaimed and said. Called inside the repository's one section, so two sessions can never both claim one run.
    /// Once the owner is found gone, or none is recorded, and before this process takes the run, <paramref name="taking"/>
    /// decides what the claim still waits on - a refusal it throws leaves the owner file as it was. Then, before the owner
    /// is written, the run's state is marked as this engine's start (<see cref="FlowFile.Starting"/>): the one owner of when
    /// what the session before left the state saying of its engine - a stop it ended with, the pause it was left under -
    /// stops being read as the live owner's, so a reader that finds the owner live - and reads the state after it, as every
    /// reader does (<see cref="OrchestratorService.EngineOf(OrchestrationStore, FlowRunOwner, OrchestratorLayout)"/>) - reads
    /// the engine as starting until the start's own writes say otherwise. Everything else the state says stands as written,
    /// and a run whose state is not there yet - one no session has written - is left so; a state that does not read refuses
    /// the claim, the owner file left as it was.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    /// <param name="taking">What is decided at the claim itself, once no live owner holds the run; null where nothing is.</param>
    /// <returns>What is said of a dead owner's record reclaimed; null where none was recorded.</returns>
    /// <exception cref="HarnessException">A live owner holds the run, <paramref name="taking"/> refuses it (<see cref="HarnessExit.Refused"/>), or the run's state does not read.</exception>
    public string? Claim(OrchestratorLayout layout, Action? taking = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return _owner.Claim(
            layout.OwnerFile,
            OrchestrationStore.OwnerKind,
            $"run '{layout.Name}'",
            held => $"run '{layout.Name}' is owned by a live engine ({held}); stop-orchestrator {layout.Name} ends it, or wait for the engine to leave it. Nothing was changed.",
            () =>
            {
                taking?.Invoke();

                if (_store.ReadFlow(layout) is { Starting: not true } left)
                {
                    _store.WriteFlow(layout, left with { Starting = true });
                }
            });
    }

    /// <summary>
    /// Releases the run where this process holds it: its owner removed, so the next session claims it afresh - and then the
    /// mark of its start taken back where the run's state still holds it (<see cref="FlowFile.Starting"/>), so a session
    /// that ended before its own writes said what its engine did - refused at its start, or let go at its claim - leaves the
    /// state as it found it. The owner goes first, the claim's mirror: a reader that finds the owner gone reads no engine
    /// whatever the state says, and one that read it live before it went reads it gone once more after the state
    /// (<see cref="OrchestratorService.EngineOf(OrchestrationStore, FlowRunOwner, OrchestratorLayout)"/>), so the past
    /// session's state is never read as this engine's. The mark is taken back inside the repository's one section, where a
    /// claim marks it, and only where no owner has claimed the run since: a new claim's mark is that claim's. A state that
    /// cannot be read or written now keeps the mark, which says nothing beside no live owner. An owner that names another
    /// process - one that reclaimed the run after this one was taken for gone - is left as it is, with its mark.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    public void Release(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (!Holds(layout))
        {
            return;
        }

        _owner.Release(layout.OwnerFile, OrchestrationStore.OwnerKind);

        try
        {
            _ = _store.AcrossRuns(layout, () =>
            {
                if (_store.ReadOwner(layout) is null && _store.ReadFlow(layout) is { Starting: true } marked)
                {
                    _store.WriteFlow(layout, marked with { Starting = null });
                }

                return true;
            });
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
        {
            // The mark stays: read only beside a live owner, it says nothing once this one is let go.
        }
    }

    /// <summary>Removes an owner record whose engine has gone, or was ended: what the deletion of a run and <c>stop-orchestrator --now</c> leave of it.</summary>
    /// <param name="layout">The run's layout.</param>
    public void RemoveGone(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _owner.RemoveGone(layout.OwnerFile, OrchestrationStore.OwnerKind);
    }
}