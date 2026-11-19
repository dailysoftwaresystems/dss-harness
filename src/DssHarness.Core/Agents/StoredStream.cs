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

    /// <summary>What the id of a message of the stream begins with (<see cref="MessageId"/>).</summary>
    public const string MessagePrefix = "message-";

    /// <summary>
    /// The id of a message of the agent's the stream holds, as a record that gives it by reference names it and
    /// <c>read_result</c> reads it by: <see cref="MessagePrefix"/> and the full digest of its text, so the id names the
    /// message whatever window of the stream its record was made from, and two messages are one id only where they are one
    /// text.
    /// </summary>
    /// <param name="text">The message's text, as the agent wrote it.</param>
    public static string MessageId(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return MessagePrefix + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Whether <paramref name="id"/> is spelt as the id of a message of the stream (<see cref="MessageId"/>).</summary>
    /// <param name="id">An id.</param>
    public static bool NamesAMessage(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return id.Length == MessagePrefix.Length + 64
            && id.StartsWith(MessagePrefix, StringComparison.Ordinal)
            && id.AsSpan(MessagePrefix.Length).ContainsAnyExcept("0123456789abcdef") is false;
    }

    /// <summary>
    /// The text of the message of the agent's in <paramref name="stream"/> that <paramref name="id"/> names
    /// (<see cref="MessageId"/>), whole as the agent wrote it; null where the stream holds no such message.
    /// </summary>
    /// <param name="stream">The step's stored stream, every attempt's events in order.</param>
    /// <param name="id">The message's id.</param>
    public static string? MessageText(IReadOnlyList<AgentEvent> stream, string id)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(id);

        return NamesAMessage(id)
            ? stream.OfType<AssistantMessage>().FirstOrDefault(message => string.Equals(MessageId(message.Text), id, StringComparison.Ordinal))?.Text
            : null;
    }

    /// <summary>
    /// The events of the stored stream in <paramref name="stepDirectory"/>, every attempt's in order - a torn last line a
    /// killed writer left set aside - or none where it keeps none.
    /// </summary>
    /// <param name="fileSystem">The file system the step's records are kept on.</param>
    /// <param name="stepDirectory">The step's own directory.</param>
    /// <exception cref="InvalidDataException">A whole line does not read as an event, named by its number.</exception>
    public static IReadOnlyList<AgentEvent> Read(IFileSystem fileSystem, string stepDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepDirectory);

        var path = Path.Combine(stepDirectory, FileName);

        return JsonStateFile.ReadLog<AgentEvent>(
            fileSystem,
            path,
            (_, _) => null,
            (line, why) => new InvalidDataException($"'{path}' line {line.ToString(System.Globalization.CultureInfo.InvariantCulture)} is no event: {why}")).Records;
    }

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
/// steps: what it began with, sent with every request of it, and its messages as they were sent and received, appended
/// through the same seam the stored stream hangs off - one line for each point where the history is whole again, so a
/// line torn by a kill takes back a whole turn and never half of one.
/// </summary>
/// <remarks>
/// <para>
/// Several steps append to one conversation, and one step can have had two. A Claude Code launch has none: a
/// conversation cannot be handed to it, and its record is the step's stored stream.
/// </para>
/// <para>
/// A line is a JSON array of the provider's own messages: an agent's message that called tools with the answers to
/// them, an agent's answer, the engine's message and the turn it began. Each message is kept as its provider made it,
/// byte for byte, and sent back so with every later request: nothing rewrites, re-serializes or masks a stored message.
/// </para>
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
    private readonly List<string> _messages;
    private readonly IFileSystem _fileSystem;

    private StoredConversation(IFileSystem fileSystem, AppendLog log, string directory, string systemPrompt, string tools, List<string> messages)
    {
        _fileSystem = fileSystem;
        _log = log;
        SessionDirectory = directory;
        SystemPrompt = systemPrompt;
        Tools = tools;
        _messages = messages;
    }

    /// <summary>
    /// Opens the stored conversation kept in <paramref name="directory"/>, making it, writing what it began with where the
    /// conversation is new, taking back any torn line a killed writer left, and reading every message it holds.
    /// </summary>
    /// <remarks>
    /// What it began with is written once, and whole: each file is put in place in one step, so a file that is there was
    /// written to its end - a torn one, kept because it is there, would be sent with every later request of the
    /// conversation.
    /// </remarks>
    /// <param name="fileSystem">The file system the agent's records are kept on.</param>
    /// <param name="directory">The session's directory: <c>sessions/&lt;id&gt;/</c> of the agent's (<see cref="OrchestratorLayout.SessionDirectoryIn"/>).</param>
    /// <param name="systemPrompt">The system prompt the conversation began with, written where it is new.</param>
    /// <param name="tools">The tools as first sent, as JSON text, written where they are new.</param>
    /// <returns>The conversation, at the end of its last whole turn.</returns>
    /// <exception cref="InvalidDataException">
    /// A line of the conversation is no JSON array of messages: it was written by no writer of this tool's.
    /// </exception>
    public static StoredConversation Open(IFileSystem fileSystem, string directory, string systemPrompt, string tools)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(tools);

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

        var log = AppendLog.Open(fileSystem, Path.Combine(directory, ConversationFileName));

        try
        {
            var messages = Messages(log);

            return new StoredConversation(log: log, fileSystem: fileSystem, directory: directory, systemPrompt: fileSystem.ReadAllText(system), tools: fileSystem.ReadAllText(given), messages: messages);
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    /// <summary>The conversation's directory.</summary>
    public string SessionDirectory { get; }

    /// <summary>The system prompt the conversation began with, as it was written: what every request of it sends.</summary>
    public string SystemPrompt { get; }

    /// <summary>The tools the conversation began with, as they were written: what every request of it sends.</summary>
    public string Tools { get; }

    /// <summary>Every message the conversation holds, in order, each as its provider made it.</summary>
    public IReadOnlyList<string> Messages() => [.. _messages];

    /// <summary>
    /// Appends <paramref name="messages"/> as one line - the point where the history is whole again - each kept as it is,
    /// byte for byte, whole and on the disk before the next line is taken.
    /// </summary>
    /// <param name="messages">The messages, in order, each one JSON value in its provider's own form holding no line break.</param>
    /// <exception cref="ArgumentException">There is no message, one is no JSON value of its own, or one holds a line break; nothing was written.</exception>
    public void Append(IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException("A line of a conversation holds one message at least.", nameof(messages));
        }

        var line = "[" + string.Join(",", messages) + "]";

        // Read back as it would be: a message that is no JSON value of its own would be split otherwise than it was given,
        // or the line not read at all.
        if (!SplitsInto(line, messages))
        {
            throw new ArgumentException("A message of the line is no JSON value of its own, and would be read back otherwise than it was given; nothing was written.", nameof(messages));
        }

        _log.Append(line);
        _messages.AddRange(messages);
    }

    /// <summary>Closes the conversation. Its last line is whole, and the next open begins after it.</summary>
    public void Dispose() => _log.Dispose();

    /// <summary>Every message the log holds, line by line, each as it was written.</summary>
    private static List<string> Messages(AppendLog log)
    {
        var messages = new List<string>();

        foreach (var line in log.ReadAll().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            messages.AddRange(Split(line));
        }

        return messages;
    }

    /// <summary>Whether <paramref name="line"/> reads back as <paramref name="messages"/>, each byte for byte.</summary>
    private static bool SplitsInto(string line, IReadOnlyList<string> messages)
    {
        try
        {
            return Split(line).SequenceEqual(messages, StringComparer.Ordinal);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>The messages of one line, each as the line holds it, byte for byte.</summary>
    /// <exception cref="InvalidDataException">The line is no JSON array.</exception>
    private static List<string> Split(string line)
    {
        try
        {
            using var document = Json.JsonDepth.ParseDocument(line);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"A line of a stored conversation is a JSON {document.RootElement.ValueKind}, and each is an array of messages.");
            }

            return [.. document.RootElement.EnumerateArray().Select(message => message.GetRawText())];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"A line of a stored conversation does not read as JSON: {ex.Message}", ex);
        }
    }
}
