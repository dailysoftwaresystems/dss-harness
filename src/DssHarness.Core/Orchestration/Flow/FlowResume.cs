using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

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
public sealed record FlowResumeState(
    FlowFile File,
    IReadOnlyList<string> Differing,
    IReadOnlyList<string> Adopted,
    IReadOnlyDictionary<string, string> Handoffs,
    IReadOnlyList<FlowQuestionEntry>? Questions = null,
    IReadOnlyList<PreExistingIssue>? Issues = null);

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
    /// Refuses (13), with a pointer to a new run, a run whose definition the repository's entry no longer matches: the
    /// run keeps the definition it began with, and every record it wrote was written under it. The one owner of the rule.
    /// </summary>
    /// <param name="file">The run's state in one file, which holds the digest it began with.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="definitionDigest">The digest of the repository's entry as it reads now.</param>
    /// <exception cref="HarnessException">The two differ (<see cref="HarnessExit.Refused"/>).</exception>
    public static void RefuseAChangedDefinition(FlowFile file, OrchestratorLayout layout, string definitionDigest)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionDigest);

        if (!string.Equals(definitionDigest, file.DefinitionDigest, StringComparison.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"workflow '{file.Definition}', as the repository's config declares it now, has changed since run '{layout.Name}' began, and a run goes on only under the definition it began with; "
                + $"{FlowService.RunCommand} {file.Definition} starts a new run of it. Nothing of run '{layout.Name}' was changed.");
        }
    }

    /// <summary>
    /// The state the run left, reconciled: every step's own record read beside the index and taken wherever there is
    /// one, what only a step's directory holds adopted at the end, the stopped steps' handoffs read from their own
    /// directories, and the index written again over what the reconciliation found.
    /// </summary>
    /// <param name="definitionDigest">The digest of the repository's entry as the caller reads it now; where the run's own digest differs, the resume is refused with a pointer to a new run (<see cref="RefuseAChangedDefinition"/>).</param>
    /// <exception cref="HarnessException">
    /// The run holds no state, its questions or its issues; a record does not read; a record names a handoff its
    /// directory does not hold; or the definition differs (<see cref="HarnessExit.Refused"/>).
    /// </exception>
    public FlowResumeState Load(string definitionDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionDigest);

        var file = _store.ReadFlow(_layout)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{_layout.FlowFile}' is not there, so run '{_layout.Name}' holds no state to take up");

        RefuseAChangedDefinition(file, _layout, definitionDigest);

        // The run's questions and its issues are made with it and never removed: one missing is something that removed
        // it, and an empty one in its place would leave a step waiting on a question nobody can answer.
        var questions = _store.ReadFlowQuestions(_layout)
            ?? throw Missing(_layout.QuestionsFile, "its questions and answers", "a step waiting on a question would wait for ever, and answer-orchestrator could not answer it");
        var issues = _store.ReadPreExistingIssues(_layout)
            ?? throw Missing(_layout.PreExistingIssuesFile, "its pre-existing issues", "the issues it listed would be lost from its report");

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

        var reconciled = file with { Steps = [.. steps], Next = [] };
        var handoffs = HandoffsOf(reconciled);

        _store.WriteFlow(_layout, reconciled);

        return new FlowResumeState(reconciled, differing, adopted, handoffs, questions.Questions, issues.Issues);
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
    /// <c>Running</c> or <c>Stopped</c>. Who wrote it is read from its first line, as the engine marks its own
    /// (<see cref="FlowHandoffRecord.EngineMark"/>).
    /// </summary>
    private FlowStepRecord WithHandoffTakenBack(FlowStepRecord record, string handoffFile)
    {
        if (record.Handoff is not null
            || record.State is not (nameof(FlowInstanceState.Running) or nameof(FlowInstanceState.Stopped))
            || _fileSystem.KindOf(handoffFile) != PathKind.File)
        {
            return record;
        }

        return record with { Handoff = FlowHandoffRecord.Of(_fileSystem.ReadAllText(handoffFile)) };
    }

    /// <summary>
    /// Each stopped or cut-off step's handoff, read from its own directory: every one whose record names one. A record
    /// that names a handoff its directory does not hold is refused, naming the file - the file is written before the
    /// record that names it, so something removed it, and the engine would otherwise write one of its own in the place
    /// of the agent's account.
    /// </summary>
    private Dictionary<string, string> HandoffsOf(FlowFile file)
    {
        var handoffs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var step in file.Steps.Where(step => step.State is nameof(FlowInstanceState.Stopped) or nameof(FlowInstanceState.Running) && step.Handoff is not null))
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
    private readonly OrchestrationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IProcessIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// The engine that holds the run, where one is live: on this machine while its process runs, and on another until
    /// somebody takes it over - nothing here can ask that machine. Null where none is recorded or the recorded one has
    /// gone.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    public RunOwner? Live(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return _store.ReadOwner(layout) is { } held && Stands(held) ? held : null;
    }

    /// <summary>Whether the engine <paramref name="held"/> names still holds the run.</summary>
    /// <param name="held">The owner recorded.</param>
    public bool Stands(RunOwner held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return _identity.Stands(held.Machine, held.ProcessId, held.Stamp);
    }

    /// <summary>Whether the engine <paramref name="held"/> names runs on this machine.</summary>
    /// <param name="held">The owner recorded.</param>
    public bool IsHere(RunOwner held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return _identity.IsHere(held.Machine);
    }

    /// <summary>The engine <paramref name="held"/> names, as a line names it: its process, and that it runs on another machine where it does - never that machine's name.</summary>
    /// <param name="held">The owner recorded.</param>
    public string Named(RunOwner held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return $"process {held.ProcessId.ToString(CultureInfo.InvariantCulture)}{(IsHere(held) ? string.Empty : " on another machine")}";
    }

    /// <summary>
    /// Claims the run for this process: refused (13) where a live owner holds it - an engine that paused its run and
    /// is still recording the machine steps the pause left running is a live owner like any other - and a dead one is
    /// reclaimed and said. Called inside the repository's one section, so two sessions can never both claim one run.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    /// <returns>What is said of a dead owner's record reclaimed; null where none was recorded.</returns>
    /// <exception cref="HarnessException">A live owner holds the run (<see cref="HarnessExit.Refused"/>).</exception>
    public string? Claim(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var held = _store.ReadOwner(layout);

        if (held is not null && Stands(held))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"run '{layout.Name}' is owned by a live engine ({Named(held)}); stop-orchestrator {layout.Name} ends it, or wait for the engine to leave it. Nothing was changed.");
        }

        _store.WriteOwner(layout, new RunOwner(_identity.CurrentMachine, _identity.CurrentId, _clock.GetUtcNow(), _identity.Current));

        return held is null
            ? null
            : $"the owner recorded for run '{layout.Name}' ({Named(held)}) has gone; it was reclaimed";
    }

    /// <summary>
    /// Releases the run where this process holds it: its owner removed, so the next session claims it afresh. An owner
    /// that names another process - one that reclaimed the run after this one was taken for gone - is left as it is.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    public void Release(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (_store.ReadOwner(layout) is { } held && IsHere(held) && held.ProcessId == _identity.CurrentId)
        {
            _store.RemoveOwner(layout);
        }
    }

    /// <summary>Removes an owner record whose engine has gone, or was ended: what the deletion of a run and <c>stop-orchestrator --now</c> leave of it.</summary>
    /// <param name="layout">The run's layout.</param>
    public void RemoveGone(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (_store.ReadOwner(layout) is { } held && !Stands(held))
        {
            _store.RemoveOwner(layout);
        }
    }
}
