using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;

namespace DssHarness.Core.Agents;

/// <summary>
/// The step's stored stream: every event but the deltas, one line to <c>stream.jsonl</c> in the step's own directory,
/// masked, appended and flushed per line. It is the step's transcript and there is no other: three things are written
/// from it and from nothing else - the last iterations a continuation carries, a handoff the engine writes, and a
/// compaction.
/// </summary>
/// <remarks>
/// <para>
/// The deltas are what a front end shows while a message is being written; the whole message that follows them holds
/// everything they held, so nothing is lost by leaving them out and the file stays readable. A kill leaves the file
/// ending at the last whole event, which is what the engine writes the next handoff from at the resume.
/// </para>
/// <para>
/// Each line is the event in this tool's own form and no provider's - its kind, when it happened, where it falls, and
/// what it carries - with every secret masked by the mask the run was given.
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

    /// <summary>Opens the step's stored stream, making its directory and taking back any torn line a killed writer left.</summary>
    /// <param name="stepDirectory">The step's own directory, as <see cref="OrchestratorLayout.AgentStepDirectory(string, string)"/> spells it.</param>
    /// <param name="mask">What masks the secrets the events may carry; never null, masking nothing where the step carries none.</param>
    /// <returns>The stream, at the end of its last whole event.</returns>
    public static StoredStream Open(string stepDirectory, ILineMask mask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepDirectory);
        ArgumentNullException.ThrowIfNull(mask);

        Directory.CreateDirectory(stepDirectory);

        return new StoredStream(AppendLog.Open(Path.Combine(stepDirectory, FileName)), mask);
    }

    /// <summary>Writes one event, unless it is a delta: masked, whole, and flushed before the next is taken.</summary>
    /// <param name="event">The event, numbered and stamped.</param>
    public ValueTask EmitAsync(AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (@event is TextDelta or ThinkingDelta or ToolCallInputDelta)
        {
            return ValueTask.CompletedTask;
        }

        _log.Append(Masked(@event).ToJsonString(JsonStateFile.LineOptions));

        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the stream. Its last line is whole, and the next open begins after it.</summary>
    public void Dispose() => _log.Dispose();

    /// <summary>
    /// The event as one line reads it: this tool's own form, with every string it carries masked - a line of the stream
    /// is a record, and a record that held a secret would be a leak kept.
    /// </summary>
    /// <param name="event">The event.</param>
    /// <returns>The event, as a node of JSON.</returns>
    private JsonNode Masked(AgentEvent @event)
    {
        var node = JsonSerializer.SerializeToNode(@event, typeof(AgentEvent), JsonStateFile.LineOptions) ?? new JsonObject();

        if (node is JsonObject masked)
        {
            Mask(masked);
        }

        return node;
    }

    /// <summary>Every string in <paramref name="node"/>, masked; deeper nodes the same, all the way down.</summary>
    /// <param name="node">One object of the event's form.</param>
    private void Mask(JsonObject node)
    {
        foreach (var (name, value) in node)
        {
            if (value is JsonValue text && text.TryGetValue<string>(out var plain))
            {
                node[name] = _mask.Redact(plain);
            }
            else if (value is JsonObject deeper)
            {
                Mask(deeper);
            }
            else if (value is JsonArray array)
            {
                Mask(array);
            }
        }
    }

    /// <summary>Every string in <paramref name="array"/>, masked.</summary>
    /// <param name="array">One array of the event's form.</param>
    private void Mask(JsonArray array)
    {
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is JsonValue text && text.TryGetValue<string>(out var plain))
            {
                array[index] = _mask.Redact(plain);
            }
            else if (array[index] is JsonObject deeper)
            {
                Mask(deeper);
            }
            else if (array[index] is JsonArray inner)
            {
                Mask(inner);
            }
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
    /// <param name="layout">The run, whose agent's sessions hold the conversation.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="id">The conversation's id.</param>
    /// <param name="systemPrompt">The system prompt the conversation began with, written where it is new.</param>
    /// <param name="tools">The tools as first sent, as JSON text, written where they are new.</param>
    /// <returns>The conversation, at the end of its last whole turn.</returns>
    public static StoredConversation Open(OrchestratorLayout layout, string agent, string id, string systemPrompt, string tools)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(tools);

        var directory = layout.AgentSessionDirectory(agent, id);
        System.IO.Directory.CreateDirectory(directory);

        var system = Path.Combine(directory, SystemFileName);
        var given = Path.Combine(directory, ToolsFileName);

        if (!File.Exists(system))
        {
            File.WriteAllText(system, systemPrompt);
        }

        if (!File.Exists(given))
        {
            File.WriteAllText(given, tools);
        }

        return new StoredConversation(AppendLog.Open(Path.Combine(directory, ConversationFileName)), directory);
    }

    /// <summary>The conversation's directory.</summary>
    public string SessionDirectory { get; }

    /// <summary>Appends one turn, as it was sent or received, whole and flushed before the next is taken.</summary>
    /// <param name="line">The turn, in the provider's own form, as one line of JSON.</param>
    public void Append(string line) => _log.Append(line);

    /// <summary>Closes the conversation. Its last line is whole, and the next open begins after it.</summary>
    public void Dispose() => _log.Dispose();
}
