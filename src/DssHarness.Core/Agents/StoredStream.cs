using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;

namespace DssHarness.Core.Agents;

/// <summary>
/// The step's stored stream: every event but the deltas, one line to <c>stream.jsonl</c> in the step's own directory,
/// masked, appended and on the disk per line. It is the step's transcript and there is no other: four things are
/// written from it and from nothing else - the last iterations a continuation carries, a handoff the engine writes, a
/// compaction, and, with the step's <c>graph.jsonl</c>, its working set.
/// </summary>
/// <remarks>
/// <para>
/// The deltas are what a front end shows while a message is being written; the whole message that follows them holds
/// everything they held, so nothing is lost by leaving them out and the file stays readable. A kill leaves the file
/// ending at the last whole event, which is what the engine writes the next handoff from at the resume.
/// </para>
/// <para>
/// Each line is the event in this tool's own form and no provider's - its kind, when it happened, where it falls, and
/// what it carries - with every secret masked by the mask the run was given. Every attempt of a step appends to the one
/// file, each beginning with its <see cref="RunStarted"/> and ending with its <see cref="RunFinished"/>, so the newest
/// attempt's stream is what follows the last <see cref="RunStarted"/>, whether the attempt before it ended or was killed.
/// </para>
/// </remarks>
public sealed class StoredStream : IAgentEventSink, IDisposable
{
    /// <summary>The stream's own file, in the step's directory.</summary>
    public const string FileName = "stream.jsonl";

    private readonly AppendLog _log;
    private readonly ILineMask _mask;

    private StoredStream(AppendLog log, ILineMask mask)
    {
        _log = log;
        _mask = mask;
    }

    /// <summary>The stream's own file.</summary>
    public string FilePath => _log.FilePath;

    /// <summary>Opens the step's stored stream, making its directory and taking back any torn line a killed writer left.</summary>
    /// <param name="fileSystem">The file system the step's records are kept on.</param>
    /// <param name="stepDirectory">The step's own directory, as <see cref="OrchestratorLayout.AgentStepDirectory(string, string)"/> spells it.</param>
    /// <param name="mask">What masks the secrets the events may carry; never null, masking nothing where the step carries none.</param>
    /// <returns>The stream, at the end of its last whole event.</returns>
    public static StoredStream Open(IFileSystem fileSystem, string stepDirectory, ILineMask mask)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepDirectory);
        ArgumentNullException.ThrowIfNull(mask);

        fileSystem.CreateDirectory(stepDirectory);

        return new StoredStream(AppendLog.Open(fileSystem, Path.Combine(stepDirectory, FileName)), mask);
    }

    /// <summary>Writes one event, unless it is a delta: masked, whole, and on the disk before the next is taken.</summary>
    /// <param name="event">The event, numbered and stamped.</param>
    public ValueTask EmitAsync(AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (@event is TextDelta or ThinkingDelta or ToolCallInputDelta)
        {
            return ValueTask.CompletedTask;
        }

        var node = JsonSerializer.SerializeToNode(@event, typeof(AgentEvent), JsonStateFile.LineOptions);

        _log.Append((Masked(node, _mask) ?? new JsonObject()).ToJsonString(JsonStateFile.LineOptions));

        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the stream. Its last line is whole, and the next open begins after it.</summary>
    public void Dispose() => _log.Dispose();

    /// <summary>
    /// <paramref name="node"/> with every string in it masked, all the way down: a string where it stands, and each member
    /// of an object and each item of a list the same, whatever holds which. A line of the stream is a record, and a record
    /// that held a secret would be a leak kept.
    /// </summary>
    /// <param name="node">One node of an event's form; masked in place where it is an object or a list.</param>
    /// <param name="mask">What masks a secret.</param>
    /// <returns>The node masked: a new value where it was a string, the same node otherwise.</returns>
    internal static JsonNode? Masked(JsonNode? node, ILineMask mask)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(mask.Redact(text));

            case JsonObject members:
                foreach (var name in members.Select(member => member.Key).ToList())
                {
                    var before = members[name];

                    if (Masked(before, mask) is var after && !ReferenceEquals(before, after))
                    {
                        members[name] = after;
                    }
                }

                return members;

            case JsonArray items:
                for (var index = 0; index < items.Count; index++)
                {
                    var before = items[index];

                    if (Masked(before, mask) is var after && !ReferenceEquals(before, after))
                    {
                        items[index] = after;
                    }
                }

                return items;

            default:
                return node;
        }
    }
}

/// <summary>
/// A conversation the engine stores in its provider's own form, under <c>sessions/&lt;id&gt;/</c> beside an agent's
/// steps: what it began with, sent with every request of it, and the turns as they were sent and received, appended
/// one line at a time through the same seam the stored stream hangs off.
/// </summary>
/// <remarks>
/// Several steps append to one conversation, and one step can have had two. A Claude Code launch has none: a
/// conversation cannot be handed to it, and its record is the step's stored stream.
/// </remarks>
public sealed class StoredConversation : IDisposable
{
    /// <summary>The conversation's own file, in its session's directory.</summary>
    public const string ConversationFileName = "conversation.jsonl";

    /// <summary>The system prompt the conversation began with.</summary>
    public const string SystemFileName = "system.md";

    /// <summary>The tools as first sent.</summary>
    public const string ToolsFileName = "tools.json";

    private readonly AppendLog _log;

    private StoredConversation(AppendLog log, string directory)
    {
        _log = log;
        SessionDirectory = directory;
    }

    /// <summary>
    /// Opens a stored conversation, making its directory, writing what it began with where the conversation is new, and
    /// taking back any torn line a killed writer left.
    /// </summary>
    /// <remarks>
    /// What it began with is written once, and whole: each file is put in place in one step, so a file that is there was
    /// written to its end - a torn one, kept because it is there, would be sent with every later request of the
    /// conversation.
    /// </remarks>
    /// <param name="fileSystem">The file system the agent's records are kept on.</param>
    /// <param name="layout">The run, whose agent's sessions hold the conversation.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="id">The conversation's id.</param>
    /// <param name="systemPrompt">The system prompt the conversation began with, written where it is new.</param>
    /// <param name="tools">The tools as first sent, as JSON text, written where they are new.</param>
    /// <returns>The conversation, at the end of its last whole turn.</returns>
    public static StoredConversation Open(IFileSystem fileSystem, OrchestratorLayout layout, string agent, string id, string systemPrompt, string tools)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(tools);

        var directory = layout.AgentSessionDirectory(agent, id);
        fileSystem.CreateDirectory(directory);

        var system = Path.Combine(directory, SystemFileName);
        var given = Path.Combine(directory, ToolsFileName);

        if (!fileSystem.FileExists(system))
        {
            fileSystem.WriteAllTextAtomic(system, systemPrompt);
        }

        if (!fileSystem.FileExists(given))
        {
            fileSystem.WriteAllTextAtomic(given, tools);
        }

        return new StoredConversation(AppendLog.Open(fileSystem, Path.Combine(directory, ConversationFileName)), directory);
    }

    /// <summary>The conversation's directory.</summary>
    public string SessionDirectory { get; }

    /// <summary>Appends one turn, as it was sent or received, whole and on the disk before the next is taken.</summary>
    /// <param name="line">The turn, in the provider's own form, as one line of JSON holding no line break.</param>
    /// <exception cref="ArgumentException">The line holds a line break of its own; nothing was written.</exception>
    public void Append(string line) => _log.Append(line);

    /// <summary>Closes the conversation. Its last line is whole, and the next open begins after it.</summary>
    public void Dispose() => _log.Dispose();
}
