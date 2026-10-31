using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// One turn of a side-task: one message of the person's and the agent's answer to it, as its own record holds it - when
/// it began, the client, provider, model and effort it ran under, the models that served it, what it used and cost,
/// how many turns of the agent's it took, and how it ended.
/// </summary>
/// <param name="At">When the turn began: when the person's message was sent.</param>
/// <param name="Client">The client that served it.</param>
/// <param name="Provider">The provider of the model that served it.</param>
/// <param name="Model">The model it ran on, as the side-task resolved it for this turn.</param>
/// <param name="Effort">The effort it ran at, where one was named.</param>
/// <param name="Served">The models that served it, in order.</param>
/// <param name="Usage">
/// What it used and cost; null where nothing has said or counted it. A turn of a launch held open for a whole chat holds
/// what its own turns were seen to use, and the launch's cost is the side-task's in all.
/// </param>
/// <param name="Turns">How many turns of the agent's it took.</param>
/// <param name="Status">How it ended; null where it ended inside a launch held open for the chat, whose end is the launch's.</param>
/// <param name="Policy">What the agent ran under, as the policy was resolved for it.</param>
public sealed record SideTaskTurn(
    DateTimeOffset At,
    string Client,
    string Provider,
    string Model,
    string? Effort = null,
    IReadOnlyList<AgentServedModel>? Served = null,
    AgentUsageReport? Usage = null,
    int Turns = 0,
    AgentRunStatus? Status = null,
    string? Policy = null)
{
    /// <inheritdoc cref="SideTaskTurn" path="/param[@name='Served']"/>
    public IReadOnlyList<AgentServedModel> Served { get; init => field = value ?? []; } = Served ?? [];

    /// <summary>What is wrong with this record, as a strict read refuses it; null where nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Client) ? "client says nothing"
        : string.IsNullOrWhiteSpace(Provider) ? "provider says nothing"
        : string.IsNullOrWhiteSpace(Model) ? "model says nothing"
        : Turns < 0 ? "turns is below naught"
        : Status is { } status && !Enum.IsDefined(status) ? $"status {(int)status} is no ending a run has"
        : string.IsNullOrWhiteSpace(Policy) ? "policy says nothing"
        : null;
}

/// <summary>How a side-task stands, as its record says it.</summary>
public enum SideTaskStatus
{
    /// <summary>A chat holds it, or its one shot runs.</summary>
    Open,

    /// <summary>It has ended: a person ended its chat, or its one shot is done.</summary>
    Ended,
}

/// <summary>
/// What a side-task runs on, as the person last set it - on its command line, or with <c>/model</c> and <c>/effort</c> in
/// its chat - and what a <c>--resume</c> that names none goes on with: the tier its model comes from, so the tier's later
/// models are its order still, or the model named outright, which has none; the effort asked; and the client.
/// </summary>
/// <param name="Tier">The tier asked; null where none was.</param>
/// <param name="Model">The model named outright; null where none was.</param>
/// <param name="Effort">The one level asked for every model; null where none was.</param>
/// <param name="EffortByModel">The levels asked by model, with <c>all</c> for the rest; null where none were.</param>
/// <param name="Client">The client asked; null where none was.</param>
public sealed record SideTaskRunsOn(
    string? Tier = null,
    string? Model = null,
    string? Effort = null,
    IReadOnlyDictionary<string, string>? EffortByModel = null,
    string? Client = null);

/// <summary>
/// A side-task's own record (<c>task.json</c>): its name, when it began and last spoke, how it stands, what it runs on as
/// the person set it, the conversation it goes on with where one is stored, what it has used and cost in all, and each of
/// its turns as its own record. Written as the side-task is claimed, before its first turn, and after each - the one
/// record of a side-task, read by <c>list-side-tasks</c> and by a <c>--resume</c>.
/// </summary>
/// <param name="Name">Its name, which names its directory.</param>
/// <param name="Began">When it began.</param>
/// <param name="LastSpoke">When its last turn ended; null until one has.</param>
/// <param name="Status">How it stands: <see cref="SideTaskStatus.Open"/> while a chat holds it, <see cref="SideTaskStatus.Ended"/> once it has ended.</param>
/// <param name="RunsOn">What it runs on, as the person last set it.</param>
/// <param name="Conversation">The stored conversation it goes on with, where its client stores one; null where none is stored.</param>
/// <param name="InAll">What every turn of it has used and cost; null until one has.</param>
/// <param name="Turns">Each of its turns, oldest first.</param>
public sealed record SideTaskRecord(
    string Name,
    DateTimeOffset Began,
    DateTimeOffset? LastSpoke = null,
    SideTaskStatus Status = SideTaskStatus.Open,
    SideTaskRunsOn? RunsOn = null,
    AgentSessionRef? Conversation = null,
    AgentUsageReport? InAll = null,
    IReadOnlyList<SideTaskTurn>? Turns = null)
{
    /// <inheritdoc cref="SideTaskRecord" path="/param[@name='Turns']"/>
    public IReadOnlyList<SideTaskTurn> Turns { get; init => field = Listed(value); } = Listed(Turns);

    /// <summary>What is wrong with this record, as a strict read refuses it; null where nothing is.</summary>
    /// <returns>Each problem, one line naming its member; empty where the record holds together.</returns>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("name says nothing");
        }

        if (!Enum.IsDefined(Status))
        {
            problems.Add($"status {(int)Status} is neither '{SideTaskStatus.Open}' nor '{SideTaskStatus.Ended}'");
        }

        problems.AddRange((Turns ?? []).Select(turn => turn.Problem()).OfType<string>().Select(problem => $"turns: {problem}"));

        return [.. problems];
    }

    private static IReadOnlyList<SideTaskTurn> Listed(IReadOnlyList<SideTaskTurn>? turns)
        => turns ?? [];
}

/// <summary>
/// Reads and writes the side-tasks' records: each side-task's <c>task.json</c>, strictly, and the list of every
/// side-task of the repository, newest first. Nothing here removes a record: they are kept until a person removes the
/// directory.
/// </summary>
public sealed class SideTaskStore(IFileSystem fileSystem)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>The side-task's own record, or null where it holds none.</summary>
    /// <param name="layout">The side-task's layout.</param>
    /// <exception cref="HarnessException">The record does not read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public SideTaskRecord? Read(SideTaskLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (_fileSystem.KindOf(layout.TaskFile) != PathKind.File)
        {
            return null;
        }

        try
        {
            var record = JsonStateFile.Read<SideTaskRecord>(_fileSystem, layout.TaskFile);
            var problems = record is null ? new[] { "the file holds null" } : record.Problems();

            if (problems is [_, ..] wrong)
            {
                throw new HarnessException(HarnessExit.Refused, $"'{layout.TaskFile}' does not read as a side-task's record: {string.Join("; ", wrong)}");
            }

            return record;
        }
        catch (JsonException refused)
        {
            throw new HarnessException(HarnessExit.Refused, $"'{layout.TaskFile}' does not read as a side-task's record: {refused.Message}");
        }
    }

    /// <summary>Writes the side-task's own record, whole.</summary>
    /// <param name="layout">The side-task's layout.</param>
    /// <param name="record">The record, which must be one.</param>
    public void Write(SideTaskLayout layout, SideTaskRecord record)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(record);

        if (record.Problems() is [_, ..] problems)
        {
            throw new InvalidOperationException($"Refusing to write a side-task's record that does not read back: {string.Join("; ", problems)}.");
        }

        JsonStateFile.Write(_fileSystem, layout.TaskFile, record);
    }

    /// <summary>
    /// Every side-task of <paramref name="root"/>, newest first: each by its directory that holds a record, an open
    /// owner file alone never one, for an owner file without a record is a claim that never got further.
    /// </summary>
    /// <param name="root">The repository's <c>.side-tasks/</c> directory.</param>
    /// <returns>Each side-task's layout and record, newest began first; empty where the directory holds none.</returns>
    public IReadOnlyList<(SideTaskLayout Layout, SideTaskRecord Record)> List(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (_fileSystem.KindOf(root) != PathKind.Directory)
        {
            return [];
        }

        var listed = new List<(SideTaskLayout Layout, SideTaskRecord Record)>();

        foreach (var directory in _fileSystem.EnumerateDirectories(root))
        {
            var layout = new SideTaskLayout(root, Path.GetFileName(directory));

            if (Read(layout) is { } record)
            {
                listed.Add((layout, record));
            }
        }

        return [.. listed.OrderByDescending(task => task.Record.Began)];
    }
}
