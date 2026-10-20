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
/// <param name="Differing">Each instance whose own record the index did not agree with, in the order they were found.</param>
/// <param name="Adopted">Each instance a step directory holds that the index did not name, in the order they were found.</param>
/// <param name="Handoffs">Each stopped step's handoff, by instance id, read from its own directory where it holds one.</param>
public sealed record FlowResumeState(
    FlowFile File,
    IReadOnlyList<string> Differing,
    IReadOnlyList<string> Adopted,
    IReadOnlyDictionary<string, string> Handoffs);

/// <summary>
/// Reads what a killed or stopped run left and hands it to a resume: the run's state in one file checked against the
/// step records - the run's (<c>steps/*/step.json</c>) and each agent's (<c>agents/*/steps/*/step.json</c>) - taking
/// the step's own record where the two differ and adopting what only a step's directory names, then writing the index
/// again. A record that cannot be read is refused, named: a resume is never built on a record guessed at.
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
    /// The state the run left, reconciled: every step's own record read beside the index, the step's own taken where
    /// the two differ, what only a step's directory holds adopted, the stopped steps' handoffs read from their own
    /// directories, and the index written again over what the reconciliation found.
    /// </summary>
    /// <param name="definitionDigest">The digest of the definition as the caller reads it now; where the run's own digest differs, the resume is refused with a pointer to a new run.</param>
    /// <exception cref="HarnessException">The run holds no state, a record does not read, or the definition differs (<see cref="HarnessExit.Refused"/>).</exception>
    public FlowResumeState Load(string? definitionDigest = null)
    {
        var file = _store.ReadFlow(_layout)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{_layout.FlowFile}' is not there, so run '{_layout.Name}' holds no state to take up");

        if (definitionDigest is { Length: > 0 } && !string.Equals(definitionDigest, file.DefinitionDigest, StringComparison.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"the entry '{file.Definition}' of '{_layout.Name}' has changed since run '{file.Run}' began; start a new run of it");
        }

        var records = StepRecords();
        var steps = new List<FlowStepRecord>();
        var differing = new List<string>();
        var adopted = new List<string>();

        foreach (var step in file.Steps)
        {
            if (records.Remove(step.Instance, out var own) && !Same(step, own))
            {
                // The step's own record is written first, so it is the truth where the two differ.
                differing.Add(step.Instance);
                steps.Add(own);
            }
            else
            {
                steps.Add(step);
            }
        }

        foreach (var (instance, own) in records)
        {
            // A step directory the index does not name: the crash's own, between a step's first write and the index's.
            adopted.Add(instance);
            steps.Add(own);
        }

        var reconciled = file with
        {
            Steps = [.. steps.OrderBy(step => step.Instance.Count(character => character == '.') + 1).ThenBy(step => step.Instance, StringComparer.Ordinal)],
            Next = [],
        };

        _store.WriteFlow(_layout, reconciled);

        return new FlowResumeState(reconciled, differing, adopted, HandoffsOf(reconciled));
    }

    /// <summary>Every step record the run's directories hold: the run's and each agent's, by instance id.</summary>
    private Dictionary<string, FlowStepRecord> StepRecords()
    {
        var records = new Dictionary<string, FlowStepRecord>(StringComparer.Ordinal);
        ReadStepsUnder(_layout.StepsDirectory, records, null);
        ReadStepsUnder(_layout.AgentsDirectory, records, agent: null, throughAgents: true);
        return records;
    }

    /// <summary>Reads each step directory under <paramref name="root"/>, or under each agent's steps where <paramref name="throughAgents"/>.</summary>
    private void ReadStepsUnder(string root, Dictionary<string, FlowStepRecord> records, string? agent, bool throughAgents = false)
    {
        if (!_fileSystem.DirectoryExists(root))
        {
            return;
        }

        foreach (var directory in _fileSystem.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if (throughAgents)
            {
                ReadStepsUnder(Path.Combine(directory, "steps"), records, Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));
                continue;
            }

            var recordFile = Path.Combine(directory, "step.json");

            if (_fileSystem.KindOf(recordFile) != PathKind.File)
            {
                continue;
            }

            var record = _store.ReadStepRecord(recordFile)
                ?? throw new HarnessException(HarnessExit.Refused, $"'{recordFile}' is not there, so step '{FlowInstanceId.IdOf(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)))}' of '{_layout.Name}' holds no record to read");

            records[record.Instance] = record;
        }
    }

    /// <summary>Each stopped step's handoff, read from its own directory where it holds one: an agent's account, or one the engine wrote.</summary>
    private Dictionary<string, string> HandoffsOf(FlowFile file)
    {
        var handoffs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var step in file.Steps.Where(step => step.State is "Stopped" or "Running" && step.Handoff is not null))
        {
            var directory = step.Agent is { } agent
                ? _layout.AgentStepDirectory(agent, FlowInstanceId.DirectoryOf(step.Instance))
                : _layout.StepDirectory(FlowInstanceId.DirectoryOf(step.Instance));
            var handoffFile = Path.Combine(directory, "handoff.md");

            if (_fileSystem.KindOf(handoffFile) == PathKind.File)
            {
                handoffs[step.Instance] = _fileSystem.ReadAllText(handoffFile).TrimEnd('\n', '\r');
            }
        }

        return handoffs;
    }

    /// <summary>Whether the index and the step's own record say the same thing of the instance.</summary>
    private static bool Same(FlowStepRecord index, FlowStepRecord own)
        => index.State == own.State
            && index.Attempt == own.Attempt
            && index.Exit == own.Exit
            && index.Question == own.Question
            && (index.Output?.ToJsonString() ?? string.Empty) == (own.Output?.ToJsonString() ?? string.Empty);
}

/// <summary>
/// The run's owner, claimed at the start of every session - a run, a resume and an answer alike - and released where
/// the session leaves nothing of it holds: a live owner refuses a session, for the engine that holds the run is
/// recording what a pause left running; a dead one is reclaimed and said, and what its steps started is ended at the
/// resume before anything is continued.
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
    /// Claims the run for this process: refused (13) where a live owner holds it - an engine that paused its run and
    /// is still recording the machine steps the pause left running is a live owner like any other - and a dead one is
    /// reclaimed and said.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    /// <returns>Whether a dead owner's record was reclaimed; null where none was recorded.</returns>
    /// <exception cref="HarnessException">A live owner holds the run (<see cref="HarnessExit.Refused"/>).</exception>
    public string? Claim(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (_store.ReadOwner(layout) is { } held)
        {
            if (_identity.Stands(held.Machine, held.ProcessId, held.Stamp))
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"run '{layout.Name}' is owned by a live engine (process {held.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)} of '{held.Machine}'); stop-orchestrator ends it, or wait for the engine to leave it");
            }

            _store.WriteOwner(
                layout,
                new RunOwner(_identity.CurrentMachine, _identity.CurrentId, _clock.GetUtcNow(), _identity.Current));

            return $"the owner recorded for run '{layout.Name}' (process {held.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}) has gone; it was reclaimed";
        }

        _store.WriteOwner(layout, new RunOwner(_identity.CurrentMachine, _identity.CurrentId, _clock.GetUtcNow(), _identity.Current));
        return null;
    }

    /// <summary>Releases the run: its owner removed, so the next session claims it afresh.</summary>
    /// <param name="layout">The run's layout.</param>
    public void Release(OrchestratorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _store.RemoveOwner(layout);
    }
}
