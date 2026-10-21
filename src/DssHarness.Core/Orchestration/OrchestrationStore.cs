using System.Text.Json;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration;

/// <summary>One agent directory's record, or why it has none that can be read.</summary>
/// <param name="Name">The directory's name.</param>
/// <param name="Record">The record; null where <paramref name="Problem"/> says why.</param>
/// <param name="Problem">Why no record could be read there; null where one was.</param>
public sealed record AgentEntry(string Name, AgentRecord? Record, string? Problem);

/// <summary>
/// Reads and writes orchestrators' and agents' records, strictly: a record that does not read as exactly what it must be
/// is refused, named with why, and never read as absent or guessed at - a record read "as best it can be" is how a fold
/// ends up subtracting the wrong set, and how a closed agent is folded again.
/// </summary>
/// <param name="fileSystem">Reads and writes the records.</param>
/// <param name="output">Tells a front end each record written (<see cref="RecordWritten"/>), what it is and where.</param>
public sealed class OrchestrationStore(IFileSystem fileSystem, IHarnessOutput output)
{
    /// <summary>What an orchestrator's record is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string OrchestratorRecordKind = "an orchestrator's record";

    /// <summary>What an agent's record is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string AgentRecordKind = "an agent's record";

    /// <summary>What an agent's seed is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string SeedKind = "an agent's seed";

    /// <summary>What the record of an agent's applied rows is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string AppliedRowsKind = "the record of an agent's applied rows";

    /// <summary>What a run's state in one file is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string FlowKind = "the run's state in one file";

    /// <summary>What a run's status is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string WorkflowStatusKind = "the run's status";

    /// <summary>What a run's pre-existing issues are, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string PreExistingIssuesKind = "the run's pre-existing issues";

    /// <summary>What a step's own record is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string StepRecordKind = "a step's own record";

    /// <summary>What a run's owner is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string OwnerKind = "the run's owner";

    /// <summary>What a run's frozen definition is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string DefinitionKind = "the orchestrator entry as it ran";

    /// <summary>What a run's questions and answers are, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string QuestionsKind = "the run's questions and answers";

    /// <summary>What another process asked of the running engine is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string StopFileKind = "what another process asked of the running engine";

    /// <summary>What a person gave from another terminal is, as its refusals and <see cref="RecordWritten"/> name it.</summary>
    public const string InboxKind = "what a person gave from another terminal";

    /// <summary>How long one read-decide-write waits for another process's.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>The orchestrators the repository has: every directory under its orchestrators directory holding a record.</summary>
    /// <param name="layout">The repository.</param>
    public IReadOnlyList<OrchestratorLayout> Orchestrators(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return _fileSystem.DirectoryExists(layout.OrchestratorsDirectory)
            ? [.. _fileSystem.EnumerateDirectories(layout.OrchestratorsDirectory)
                .Select(directory => OrchestratorLayout.Of(layout, Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))))
                .Where(orchestrator => Kind(orchestrator.RecordFile, OrchestratorRecordKind) == PathKind.File)
                .OrderBy(orchestrator => orchestrator.Name, StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The orchestrator's record, or null where it has none.</summary>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <exception cref="HarnessException">The record cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public OrchestratorRecord? ReadOrchestrator(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<OrchestratorRecord>(orchestrator.RecordFile, OrchestratorRecordKind, record => record.Problem(orchestrator.Name));
    }

    /// <summary>Writes the orchestrator's record.</summary>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <param name="record">The record, which must be one.</param>
    public void WriteOrchestrator(OrchestratorLayout orchestrator, OrchestratorRecord record)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(record);
        Write(orchestrator.RecordFile, OrchestratorRecordKind, record, record.Problem(orchestrator.Name));
    }

    /// <summary>The agent's record, or null where it has none.</summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="agent">The agent's name.</param>
    /// <exception cref="HarnessException">The record cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public AgentRecord? ReadAgent(OrchestratorLayout orchestrator, string agent)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        return Read<AgentRecord>(orchestrator.AgentRecordFile(agent), AgentRecordKind, record => record.Problem(orchestrator.Name, agent));
    }

    /// <summary>Writes the agent's record.</summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="record">The record, which must be one.</param>
    public void WriteAgent(OrchestratorLayout orchestrator, AgentRecord record)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(record);
        Write(orchestrator.AgentRecordFile(record.Name), AgentRecordKind, record, record.Problem(orchestrator.Name, record.Name));
    }

    /// <summary>Every agent directory of the orchestrator, in name order, each with its record or why it has none that reads.</summary>
    /// <param name="orchestrator">The orchestrator.</param>
    public IReadOnlyList<AgentEntry> Agents(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        if (!_fileSystem.DirectoryExists(orchestrator.AgentsDirectory))
        {
            return [];
        }

        var entries = new List<AgentEntry>();

        foreach (var directory in _fileSystem.EnumerateDirectories(orchestrator.AgentsDirectory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

            try
            {
                entries.Add(ReadAgent(orchestrator, name) is { } record
                    ? new AgentEntry(name, record, null)
                    : new AgentEntry(name, null, $"'{orchestrator.AgentRecordFile(name)}' is not there"));
            }
            catch (HarnessException ex)
            {
                entries.Add(new AgentEntry(name, null, ex.Message));
            }
        }

        return entries;
    }

    /// <summary>The agent's seed, or null where it was never seeded.</summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="agent">The agent's name.</param>
    /// <exception cref="HarnessException">The seed cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public SeedRecord? ReadSeed(OrchestratorLayout orchestrator, string agent)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<SeedRecord>(orchestrator.SeedFile(agent), SeedKind, seed => seed.Problem());
    }

    /// <summary>Writes the agent's seed.</summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="seed">The seed, which must be one.</param>
    public void WriteSeed(OrchestratorLayout orchestrator, string agent, SeedRecord seed)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(seed);
        Write(orchestrator.SeedFile(agent), SeedKind, seed, seed.Problem());
    }

    /// <summary>The rows the agent's folds applied, or null where none ever were.</summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="agent">The agent's name.</param>
    /// <exception cref="HarnessException">The record cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public AppliedRowsRecord? ReadAppliedRows(OrchestratorLayout orchestrator, string agent)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<AppliedRowsRecord>(orchestrator.AppliedRowsFile(agent), AppliedRowsKind, record => record.Problem());
    }

    /// <summary>Writes the rows the agent's folds applied.</summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="record">The record, which must be one.</param>
    public void WriteAppliedRows(OrchestratorLayout orchestrator, string agent, AppliedRowsRecord record)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(record);
        Write(orchestrator.AppliedRowsFile(agent), AppliedRowsKind, record, record.Problem());
    }

    /// <summary>
    /// Changes the agent's record as <paramref name="change"/> says, reading it and writing it with no other process doing
    /// the same to that orchestrator at once, so two commands can never each write over what the other decided - a
    /// session recorded while the agent is being closed, which would put back the live state its closing replaced.
    /// </summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="change">The record as it is now, to the record to write; null leaves it as it is.</param>
    /// <returns>The record as written, or as it is where <paramref name="change"/> left it.</returns>
    /// <exception cref="HarnessException">The agent has no record, or one that does not read (<see cref="HarnessExit.Refused"/>).</exception>
    public AgentRecord UpdateAgent(OrchestratorLayout orchestrator, string agent, Func<AgentRecord, AgentRecord?> change)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(change);

        return Exclusively(orchestrator, () =>
        {
            var current = ReadAgent(orchestrator, agent)
                ?? throw new HarnessException(HarnessExit.Refused, $"'{orchestrator.AgentRecordFile(agent)}' is not there, so agent '{agent}' of '{orchestrator.Name}' has no record to change.");

            if (change(current) is not { } changed)
            {
                return current;
            }

            WriteAgent(orchestrator, changed);
            return changed;
        });
    }

    /// <summary>
    /// Runs <paramref name="work"/> with no other process deciding about the same orchestrator at once: counting its
    /// agents against its limit and taking a place for a new one, so two agents made together can never both take the
    /// last place.
    /// </summary>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <param name="work">The reading, the decision and the writing, all synchronous.</param>
    public T Exclusively<T>(OrchestratorLayout orchestrator, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return MachineWideFile.Update(orchestrator.RecordFile, Window, work);
    }

    /// <summary>
    /// Runs <paramref name="work"/> with no other process deciding about the repository's runs at once: the scan for
    /// unfinished runs, the choice of a run's name and the creation of its record. <see cref="Exclusively{T}"/> will
    /// not do for these, since it is a section for one orchestrator's record, and two runs - two orchestrators -
    /// would take two different ones and both pass.
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="work">The reading, the decision and the writing, all synchronous.</param>
    public T AcrossRuns<T>(HarnessLayout layout, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(work);

        return MachineWideFile.Update(RunsSectionFile(layout), Window, work);
    }

    /// <summary>The file that names the repository's one section across its runs (<see cref="AcrossRuns{T}"/>).</summary>
    /// <param name="layout">The repository.</param>
    public static string RunsSectionFile(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.OrchestratorsDirectory, ".runs");
    }

    /// <summary>The run's state in one file, or null where the run holds none.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <exception cref="HarnessException">The file cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public Flow.FlowFile? ReadFlow(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<Flow.FlowFile>(orchestrator.FlowFile, FlowKind, file => file.Problem());
    }

    /// <summary>Writes the run's state in one file.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="file">The state, which must be one.</param>
    public void WriteFlow(OrchestratorLayout orchestrator, Flow.FlowFile file)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(file);
        Write(orchestrator.FlowFile, FlowKind, file, file.Problem());
    }

    /// <summary>The run's status, or null where the run holds none.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <exception cref="HarnessException">The status cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public WorkflowStatus? ReadWorkflowStatus(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<WorkflowStatus>(orchestrator.WorkflowStatusFile, WorkflowStatusKind, status => status.Problem());
    }

    /// <summary>Writes the run's status.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="status">The status, which must be one.</param>
    public void WriteWorkflowStatus(OrchestratorLayout orchestrator, WorkflowStatus status)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(status);
        Write(orchestrator.WorkflowStatusFile, WorkflowStatusKind, status, status.Problem());
    }

    /// <summary>The run's pre-existing issues, or null where the run holds none.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <exception cref="HarnessException">The file cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public PreExistingIssuesFile? ReadPreExistingIssues(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<PreExistingIssuesFile>(orchestrator.PreExistingIssuesFile, PreExistingIssuesKind, issues => issues.Problem());
    }

    /// <summary>Writes the run's pre-existing issues.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="issues">The issues, which must be one.</param>
    public void WritePreExistingIssues(OrchestratorLayout orchestrator, PreExistingIssuesFile issues)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(issues);
        Write(orchestrator.PreExistingIssuesFile, PreExistingIssuesKind, issues, issues.Problem());
    }

    /// <summary>One step's own record, wherever the run keeps it, or null where the step's directory holds none.</summary>
    /// <param name="recordFile">The record's path, as <see cref="OrchestratorLayout"/> spells it.</param>
    /// <exception cref="HarnessException">The record cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public FlowStepRecord? ReadStepRecord(string recordFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordFile);
        return Read<FlowStepRecord>(recordFile, StepRecordKind, record => record.Problem());
    }

    /// <summary>Writes one step's own record, wherever the run keeps it.</summary>
    /// <param name="recordFile">The record's path, as <see cref="OrchestratorLayout"/> spells it.</param>
    /// <param name="record">The record, which must be one.</param>
    public void WriteStepRecord(string recordFile, FlowStepRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordFile);
        ArgumentNullException.ThrowIfNull(record);
        Write(recordFile, StepRecordKind, record, record.Problem());
    }

    /// <summary>The run's owner, or null where none is recorded.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <exception cref="HarnessException">The owner cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public RunOwner? ReadOwner(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<RunOwner>(orchestrator.OwnerFile, OwnerKind, owner => owner.Problem());
    }

    /// <summary>Writes the run's owner.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="owner">The owner, which must be one.</param>
    public void WriteOwner(OrchestratorLayout orchestrator, RunOwner owner)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(owner);
        Write(orchestrator.OwnerFile, OwnerKind, owner, owner.Problem());
    }

    /// <summary>Removes the run's owner, where the run holds one.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    public void RemoveOwner(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        if (_fileSystem.KindOf(orchestrator.OwnerFile) == PathKind.File)
        {
            _fileSystem.DeleteFile(orchestrator.OwnerFile);
        }
    }

    /// <summary>The orchestrator entry as it ran, frozen, or null where the run holds none.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <exception cref="HarnessException">The entry cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public WorkflowDefinition? ReadDefinition(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<WorkflowDefinition>(orchestrator.DefinitionFile, DefinitionKind, _ => null);
    }

    /// <summary>Writes the orchestrator entry as it ran, frozen.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="definition">The definition, as it ran.</param>
    public void WriteDefinition(OrchestratorLayout orchestrator, WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(definition);
        Write(orchestrator.DefinitionFile, DefinitionKind, definition, null);
    }

    /// <summary>Every question the run raised and every answer it was given, or null where the run holds none.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <exception cref="HarnessException">The file cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public FlowQuestionsFile? ReadFlowQuestions(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<FlowQuestionsFile>(orchestrator.QuestionsFile, QuestionsKind, file => file.Problem());
    }

    /// <summary>Writes the run's questions and answers, whole.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="file">The questions and answers, which must be one.</param>
    public void WriteFlowQuestions(OrchestratorLayout orchestrator, FlowQuestionsFile file)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(file);
        Write(orchestrator.QuestionsFile, QuestionsKind, file, file.Problem());
    }

    /// <summary>What another process asked of the running engine, or null where nothing waits to be taken.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <exception cref="HarnessException">The file cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public FlowStopFile? ReadStopFile(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<FlowStopFile>(orchestrator.StopFile, StopFileKind, file => file.Problem());
    }

    /// <summary>Writes what a process asks of the running engine, inside the run's own section; one request at a time.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="file">The request, which must be one.</param>
    public void WriteStopFile(OrchestratorLayout orchestrator, FlowStopFile file)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(file);

        _ = Exclusively(orchestrator, () =>
        {
            Write(orchestrator.StopFile, StopFileKind, file, file.Problem());
            return true;
        });
    }

    /// <summary>
    /// Takes what another process asked of the running engine, inside the run's own section: the request read, and the
    /// file removed, so it is applied once.
    /// </summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <returns>The request taken, or null where none waited.</returns>
    public FlowStopFile? TakeStopFile(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        return Exclusively(orchestrator, () =>
        {
            var taken = ReadStopFile(orchestrator);

            if (taken is not null)
            {
                _fileSystem.DeleteFile(orchestrator.StopFile);
            }

            return taken;
        });
    }

    /// <summary>What a person gave from another terminal that the engine has not taken yet, or null where none is there.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <exception cref="HarnessException">The file cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public FlowInboxFile? ReadInbox(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<FlowInboxFile>(orchestrator.InboxFile, InboxKind, file => file.Problem());
    }

    /// <summary>Writes the inbox whole: what a person gave that the engine has not taken yet, plus what it has.</summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="file">The inbox, which must be one.</param>
    public void WriteInbox(OrchestratorLayout orchestrator, FlowInboxFile file)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(file);
        Write(orchestrator.InboxFile, InboxKind, file, file.Problem());
    }

    /// <summary>
    /// Adds an answer to the run's inbox, keeping what is there, whole: read and written inside the run's own section, so
    /// two answers handed over at once are both kept.
    /// </summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="answer">The answer on its way to the engine, under an id of its own.</param>
    public void AddToInbox(OrchestratorLayout orchestrator, FlowInboxAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(answer);

        _ = Exclusively(orchestrator, () =>
        {
            var inbox = ReadInbox(orchestrator) ?? new FlowInboxFile(1, []);
            WriteInbox(orchestrator, inbox with { Answers = [.. inbox.Answers, answer] });
            return true;
        });
    }

    /// <summary>
    /// Removes one answer from the run's inbox by its id, once the engine has written it where it belongs - read and
    /// written inside the run's own section, so an answer added meanwhile is kept - and the file itself where the inbox
    /// empties.
    /// </summary>
    /// <param name="orchestrator">The run's orchestrator.</param>
    /// <param name="id">The answer's id, as its entry names it.</param>
    /// <returns>Whether the inbox held the answer.</returns>
    public bool RemoveFromInbox(OrchestratorLayout orchestrator, string id)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return Exclusively(orchestrator, () =>
        {
            if (ReadInbox(orchestrator) is not { } inbox || inbox.Answers.All(answer => !string.Equals(answer.Id, id, StringComparison.Ordinal)))
            {
                return false;
            }

            List<FlowInboxAnswer> left = [.. inbox.Answers.Where(answer => !string.Equals(answer.Id, id, StringComparison.Ordinal))];

            if (left.Count == 0)
            {
                _fileSystem.DeleteFile(orchestrator.InboxFile);
            }
            else
            {
                WriteInbox(orchestrator, inbox with { Answers = left });
            }

            return true;
        });
    }

    private T? Read<T>(string path, string what, Func<T, string?> problemOf)
        where T : class
    {
        // A record this process cannot look for is not a record that is not there.
        if (Kind(path, what) == PathKind.None)
        {
            return null;
        }

        T? record;

        try
        {
            record = JsonSerializer.Deserialize<T>(_fileSystem.ReadAllText(path), JsonStateFile.Options);
        }
        catch (JsonException ex)
        {
            throw Unreadable(path, what, ex.Message.TrimEnd('.'));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"'{path}', {what}, could not be read: {ex.Message.TrimEnd('.')}. Nothing was changed.");
        }

        return record is null ? throw Unreadable(path, what, "it holds null")
            : problemOf(record) is { } problem ? throw Unreadable(path, what, problem)
            : record;
    }

    /// <summary>Writes a record whole, and tells a front end what it wrote and where.</summary>
    private void Write<T>(string path, string what, T record, string? problem)
    {
        if (problem is not null)
        {
            throw new InvalidOperationException($"Refusing to write '{path}', which would not read back: {problem}.");
        }

        _fileSystem.WriteAllTextAtomic(path, JsonSerializer.Serialize(record, JsonStateFile.Options) + "\n");
        _output.Publish(new RecordWritten(what, path));
    }

    /// <summary>What is at a record's path; refused as a failure where that cannot be told.</summary>
    private PathKind Kind(string path, string what)
    {
        try
        {
            return _fileSystem.KindOf(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"Whether '{path}', {what}, is there cannot be told: {ex.Message.TrimEnd('.')}. Nothing was changed.");
        }
    }

    private static HarnessException Unreadable(string path, string what, string why)
        => new(
            HarnessExit.Refused,
            $"'{path}' is {what}, and does not read as one: {why}. Nothing was changed: a record is never guessed at.");
}
