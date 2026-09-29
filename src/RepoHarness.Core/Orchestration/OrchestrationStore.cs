using System.Text.Json;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Orchestration;

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
public sealed class OrchestrationStore(IFileSystem fileSystem)
{
    /// <summary>How long one read-decide-write waits for another process's.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>The orchestrators the repository has: every directory under its orchestrators directory holding a record.</summary>
    /// <param name="layout">The repository.</param>
    public IReadOnlyList<OrchestratorLayout> Orchestrators(HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return _fileSystem.DirectoryExists(layout.OrchestratorsDirectory)
            ? [.. _fileSystem.EnumerateDirectories(layout.OrchestratorsDirectory)
                .Select(directory => OrchestratorLayout.Of(layout, Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))))
                .Where(orchestrator => Kind(orchestrator.RecordFile, "an orchestrator's record") == PathKind.File)
                .OrderBy(orchestrator => orchestrator.Name, StringComparer.Ordinal)]
            : [];
    }

    /// <summary>The orchestrator's record, or null where it has none.</summary>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <exception cref="HarnessException">The record cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public OrchestratorRecord? ReadOrchestrator(OrchestratorLayout orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        return Read<OrchestratorRecord>(orchestrator.RecordFile, "an orchestrator's record", record => record.Problem(orchestrator.Name));
    }

    /// <summary>Writes the orchestrator's record.</summary>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <param name="record">The record, which must be one.</param>
    public void WriteOrchestrator(OrchestratorLayout orchestrator, OrchestratorRecord record)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(record);
        Write(orchestrator.RecordFile, record, record.Problem(orchestrator.Name));
    }

    /// <summary>The agent's record, or null where it has none.</summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="agent">The agent's name.</param>
    /// <exception cref="HarnessException">The record cannot be read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public AgentRecord? ReadAgent(OrchestratorLayout orchestrator, string agent)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        return Read<AgentRecord>(orchestrator.AgentRecordFile(agent), "an agent's record", record => record.Problem(orchestrator.Name, agent));
    }

    /// <summary>Writes the agent's record.</summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="record">The record, which must be one.</param>
    public void WriteAgent(OrchestratorLayout orchestrator, AgentRecord record)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(record);
        Write(orchestrator.AgentRecordFile(record.Name), record, record.Problem(orchestrator.Name, record.Name));
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
        return Read<SeedRecord>(orchestrator.SeedFile(agent), "an agent's seed", seed => seed.Problem());
    }

    /// <summary>Writes the agent's seed.</summary>
    /// <param name="orchestrator">Its orchestrator.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="seed">The seed, which must be one.</param>
    public void WriteSeed(OrchestratorLayout orchestrator, string agent, SeedRecord seed)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(seed);
        Write(orchestrator.SeedFile(agent), seed, seed.Problem());
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

    private void Write<T>(string path, T record, string? problem)
    {
        if (problem is not null)
        {
            throw new InvalidOperationException($"Refusing to write '{path}', which would not read back: {problem}.");
        }

        _fileSystem.WriteAllTextAtomic(path, JsonSerializer.Serialize(record, JsonStateFile.Options) + "\n");
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
