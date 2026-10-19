using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// The record of a session a contained child of an orchestrator's step leads while it runs, on Linux and macOS:
/// <c>&lt;step&gt;/children/&lt;session&gt;.json</c> in the step's own directory, a step of the run's or of an agent's alike.
/// </summary>
/// <remarks>
/// Kept where the step's own records are, so a resume of the run reads what the step left running from the place it reads
/// the step from. The child leads its session under its own process id, which is the session's; the stamp beside it tells
/// that process from a later one given the same id, and the owner is the process that started it, whose end - killed, so
/// that it never ended the session itself - is what makes the session one to end.
/// </remarks>
public sealed record ChildSessionRecord
{
    /// <summary>The session's id: the process id of the child, which leads it.</summary>
    public required int Session { get; init; }

    /// <summary>What tells the child from a later process given its id (<see cref="IProcessIdentity.StampOf"/>).</summary>
    public required string Started { get; init; }

    /// <summary>The program the child runs, by its resolved path.</summary>
    public required string Program { get; init; }

    /// <summary>The process that started the child, and ends its session when it ends it.</summary>
    public required ChildSessionOwner Owner { get; init; }

    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => Session < 1 ? $"its session is {Session.ToString(CultureInfo.InvariantCulture)}, and a session is a process's id"
            : string.IsNullOrWhiteSpace(Started) ? "it says nothing of when its child started"
            : string.IsNullOrWhiteSpace(Program) ? "it names no program"
            : Owner.ProcessId < 1 || string.IsNullOrWhiteSpace(Owner.Machine) ? "it names no process as its owner"
            : null;
}

/// <summary>The process that started a contained child, as its session's record names it.</summary>
/// <param name="Machine">The machine it runs on.</param>
/// <param name="ProcessId">Its process id.</param>
/// <param name="Stamp">What tells it from a later process given its id; null where this machine would not say.</param>
public sealed record ChildSessionOwner(string Machine, int ProcessId, string? Stamp = null);

/// <summary>A session a sweep ended, as a command's document names it.</summary>
/// <param name="Orchestrator">The orchestrator whose step left it.</param>
/// <param name="Agent">The agent whose step left it; null for a step of the run that no agent owns.</param>
/// <param name="Step">The step instance that left it.</param>
/// <param name="Session">The session's id: its leader's process id.</param>
/// <param name="Program">The program its leader ran.</param>
/// <param name="Owner">The process id of the process that started it and had gone.</param>
/// <param name="Unreached">What of the session could not be reached, and why; null where everything in it was.</param>
public sealed record EndedChildSession(string Orchestrator, string? Agent, string Step, int Session, string Program, int Owner, string? Unreached);

/// <summary>
/// Keeps the records of the sessions contained children of an orchestrator's steps lead, and ends the ones a killed process
/// left: the one writer and the one reader of <see cref="ChildSessionRecord"/>.
/// </summary>
/// <remarks>
/// <para>
/// A record is written by the process runner, through the recorder <see cref="For"/> hands a request, once the child is
/// placed in its session and before it is said to have started; and taken back once the runner has ended the session, at
/// the child's own end or at a stop. A record is never written by hand.
/// </para>
/// <para>
/// Every command that works an orchestrator sweeps its records first (<see cref="SweepAsync"/>), before it reads anything
/// else of it: a session whose owner still runs is its owner's to end and is left as it is; one whose owner has gone was
/// left running by a process killed before it could end it, and is ended as that process would have ended it - where its
/// leader is still the process recorded, and never where another process has been given its id - and its record removed.
/// An owner on another machine is never taken to have gone, since nothing here can ask that machine. Windows records
/// nothing: a contained child there is held in a job that ends with the process holding it.
/// </para>
/// </remarks>
/// <param name="fileSystem">Reads, writes and removes the records.</param>
/// <param name="identity">This process, the owner of what it records, and how another process's liveness and start are told.</param>
/// <param name="left">Ends a session its owner left running.</param>
/// <param name="output">Says what a sweep ended, and a record it could not read or remove.</param>
public sealed class ChildSessions(IFileSystem fileSystem, IProcessIdentity identity, LeftSessions left, IHarnessOutput output)
{
    /// <summary>What a record is, as a line that cannot read one names it.</summary>
    public const string RecordKind = "the record of a contained child's session";

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    private readonly IProcessIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));

    private readonly LeftSessions _left = left ?? throw new ArgumentNullException(nameof(left));

    private readonly IHarnessOutput _output = output ?? throw new ArgumentNullException(nameof(output));

    /// <summary>Where a contained child of the step kept at <paramref name="stepDirectory"/> records its session, as its request names it.</summary>
    /// <param name="stepDirectory">The step's own directory.</param>
    public IContainedSessionRecord For(string stepDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepDirectory);
        return new Recorder(this, stepDirectory);
    }

    /// <summary>
    /// Ends each session a contained child of the orchestrator's steps leads whose owner has gone, as that owner would have
    /// ended it, and removes its record: said, each, as a line of <paramref name="command"/>.
    /// </summary>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <param name="command">The command sweeping, which its lines name.</param>
    /// <returns>Each session ended, in the order its record was found.</returns>
    public async Task<IReadOnlyList<EndedChildSession>> SweepAsync(OrchestratorLayout orchestrator, string command)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var ended = new List<EndedChildSession>();

        foreach (var (file, agent, step) in RecordFiles(orchestrator))
        {
            if (Read(file, command) is not { } record)
            {
                continue;
            }

            // A session whose owner runs is its owner's to end.
            if (_identity.Stands(record.Owner.Machine, record.Owner.ProcessId, record.Owner.Stamp))
            {
                continue;
            }

            string? unreached = null;
            var leads = _identity.IsSame(record.Session, record.Started);

            if (leads)
            {
                try
                {
                    unreached = await _left.EndAsync(record.Session).ConfigureAwait(false);
                }
                catch (PlatformNotSupportedException ex)
                {
                    _output.Warn(command, $"'{file}' is {RecordKind}, which this system never writes: {ex.Message.TrimEnd('.')}. It was left as it is.");
                    continue;
                }
            }

            // Ended, or led by nothing recorded any more - its leader gone, or its id another process's - the record has
            // nothing left to name.
            Remove(file, command, orchestrator.Name);

            if (leads)
            {
                var session = new EndedChildSession(orchestrator.Name, agent, step, record.Session, record.Program, record.Owner.ProcessId, unreached);

                _output.Info(command, Line(session));
                ended.Add(session);
            }
        }

        return ended;
    }

    /// <summary>The line a command says a session it ended with.</summary>
    /// <param name="session">The session ended.</param>
    public static string Line(EndedChildSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var step = session.Agent is null
            ? $"step '{session.Step}' of '{session.Orchestrator}'"
            : $"step '{session.Step}' of agent '{session.Agent}' of '{session.Orchestrator}'";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{step} left session {session.Session} ('{session.Program}') running, and the process that started it (pid {session.Owner}) has gone; it was ended{(session.Unreached is { } unreached ? $", and {unreached}" : string.Empty)}");
    }

    /// <summary>Writes the record of a session placed in the step at <paramref name="stepDirectory"/>, whole.</summary>
    /// <returns>The record's path; null where the leader's start cannot be read, since it has then ended and no record of it could ever be matched.</returns>
    private string? Write(string stepDirectory, int leader, string program)
    {
        if (_identity.StampOf(leader) is not { Length: > 0 } started)
        {
            return null;
        }

        var record = new ChildSessionRecord
        {
            Session = leader,
            Started = started,
            Program = program,
            Owner = new ChildSessionOwner(_identity.CurrentMachine, _identity.CurrentId, _identity.Current),
        };

        var file = OrchestratorLayout.ChildFile(stepDirectory, leader);

        _fileSystem.CreateDirectory(OrchestratorLayout.ChildrenDirectory(stepDirectory));
        _fileSystem.WriteAllTextAtomic(file, JsonSerializer.Serialize(record, JsonStateFile.Options) + "\n");

        return file;
    }

    /// <summary>
    /// Every record of the orchestrator's steps, the run's and each agent's, with the agent and the step it was found
    /// under, in the order of their paths.
    /// </summary>
    private List<(string File, string? Agent, string Step)> RecordFiles(OrchestratorLayout orchestrator)
    {
        var found = new List<(string File, string? Agent, string Step)>();

        foreach (var step in Directories(orchestrator.StepsDirectory))
        {
            found.AddRange(Files(step).Select(file => (file, (string?)null, Name(step))));
        }

        foreach (var agent in Directories(orchestrator.AgentsDirectory))
        {
            foreach (var step in Directories(orchestrator.AgentStepsDirectory(Name(agent))))
            {
                found.AddRange(Files(step).Select(file => (file, (string?)Name(agent), Name(step))));
            }
        }

        return found;
    }

    /// <summary>The directories within <paramref name="directory"/>, in order; none where it is not there.</summary>
    private List<string> Directories(string directory)
        => _fileSystem.DirectoryExists(directory) ? [.. _fileSystem.EnumerateDirectories(directory).Order(StringComparer.Ordinal)] : [];

    /// <summary>The records of the step kept at <paramref name="stepDirectory"/>, in order.</summary>
    private List<string> Files(string stepDirectory)
    {
        var children = OrchestratorLayout.ChildrenDirectory(stepDirectory);

        return _fileSystem.DirectoryExists(children)
            ? [.. _fileSystem.EnumerateFiles(children, recursive: false)
                .Where(file => file.EndsWith(OrchestratorLayout.ChildExtension, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>
    /// The record at <paramref name="file"/>, strictly; null - and said - where it does not read as one, which is left as
    /// it is: a record is never guessed at, and no process is ended on one.
    /// </summary>
    private ChildSessionRecord? Read(string file, string command)
    {
        string why;

        try
        {
            var record = JsonSerializer.Deserialize<ChildSessionRecord>(_fileSystem.ReadAllText(file), JsonStateFile.Options);

            if (record?.Problem() is null && record is not null)
            {
                return record;
            }

            why = record?.Problem() ?? "it holds null";
        }
        catch (JsonException ex)
        {
            why = ex.Message.TrimEnd('.');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = $"it could not be read: {ex.Message.TrimEnd('.')}";
        }

        _output.Warn(command, $"'{file}' is {RecordKind}, and does not read as one: {why}. It was left as it is, and nothing it names was ended.");
        return null;
    }

    /// <summary>Removes a record; one that cannot be removed is said, and left for the next command's sweep.</summary>
    private void Remove(string file, string command, string orchestrator)
    {
        try
        {
            _fileSystem.DeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.Warn(command, $"'{file}', {RecordKind}, could not be removed: {ex.Message.TrimEnd('.')}. The next command over '{orchestrator}' removes it.");
        }
    }

    /// <summary>A directory's own name.</summary>
    private static string Name(string directory) => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

    /// <summary>The recorder a contained child's request is handed: the record written once it is placed, and taken back once it is ended.</summary>
    /// <param name="sessions">The records.</param>
    /// <param name="stepDirectory">The step's own directory.</param>
    private sealed class Recorder(ChildSessions sessions, string stepDirectory) : IContainedSessionRecord
    {
        private string? _file;

        public void Placed(int leader, string program)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(program);
            _file = sessions.Write(stepDirectory, leader, program);
        }

        public void Ended()
        {
            if (_file is { } file)
            {
                sessions._fileSystem.DeleteFile(file);
                _file = null;
            }
        }
    }
}
