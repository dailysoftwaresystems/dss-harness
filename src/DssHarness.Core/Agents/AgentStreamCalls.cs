using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.ClaudeCode;

namespace DssHarness.Core.Agents;

/// <summary>
/// One tool call a stored stream holds that was answered: the id its client gave it, the tool as the runtime names it, the
/// input the model wrote, the answer it was given and whether that answer says it succeeded.
/// </summary>
/// <param name="Id">The call's id, as the stream holds it: what a closing record's search or measurement cites.</param>
/// <param name="Tool">The tool (<see cref="AgentStreamCalls.ToolOf"/>): a tool of the engine's tool server by the runtime's name, any other by its client's.</param>
/// <param name="Input">Its input, the JSON object the model wrote, as the stream holds it masked.</param>
/// <param name="Result">Its answer, masked and capped as the stream holds it.</param>
/// <param name="Ok">
/// Whether its answer says it succeeded - it went through. A measure call whose cell failed or was not measured answers
/// that it did not, though it was made and keeps its record.
/// </param>
public sealed record AgentStreamCall(string Id, string Tool, JsonObject Input, string Result, bool Ok)
{
    /// <summary>
    /// Every text its input holds - each string value, at any depth, in the order written - unescaped, as the model meant
    /// it: a query is looked for in these, never in the input's JSON text, where a client escapes a quote, a backslash or,
    /// as Claude Code's does, a character such as <c>&lt;</c> or <c>&amp;</c>.
    /// </summary>
    public IReadOnlyList<string> Texts()
    {
        var texts = new List<string>();
        Gather(Input, texts);
        return texts;
    }

    private static void Gather(JsonNode? node, List<string> texts)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var (_, member) in members)
                {
                    Gather(member, texts);
                }

                break;

            case JsonArray items:
                foreach (var item in items)
                {
                    Gather(item, texts);
                }

                break;

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                texts.Add(value.GetValue<string>());
                break;
        }
    }
}

/// <summary>
/// The one reading of the tool calls a stored stream holds, every client's form of them alike: a call paired with its
/// answer by the id both carry - never by the order they came in, for a turn that makes several calls starts them all
/// before any is answered - its tool told by its start or by the message of the model that made it, and named as the
/// runtime names it.
/// </summary>
public static class AgentStreamCalls
{
    /// <summary>
    /// The tool a stream names <paramref name="stored"/>, as the runtime names it: a tool of the engine's tool server Claude
    /// Code called - stored as Claude Code names it, <c>mcp__dssharness__measure</c> - by the runtime's own name,
    /// <c>measure</c>; every other tool, Claude Code's own and every tool the API clients' loop runs, as it is stored.
    /// </summary>
    /// <param name="stored">The tool's name, as the stream stores it.</param>
    public static string ToolOf(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return ClaudeCodeTools.ServedAs(stored) ?? stored;
    }

    /// <summary>
    /// Every call of <paramref name="stream"/> that went through, in the order its answers came: each of
    /// <see cref="Answered"/> whose answer says it succeeded.
    /// </summary>
    /// <param name="stream">A stored stream, every attempt's events in order.</param>
    public static IEnumerable<AgentStreamCall> Of(IEnumerable<AgentEvent> stream) => Answered(stream).Where(call => call.Ok);

    /// <summary>
    /// Every call of <paramref name="stream"/> that was answered, in the order its answers came, whether the answer says it
    /// succeeded or not: each answer paired by its id with the tool the call's start or the model's message named, whose
    /// input reads as a JSON object. A call whose answer never came or names no call the stream started is not among them.
    /// </summary>
    /// <param name="stream">A stored stream, every attempt's events in order.</param>
    public static IEnumerable<AgentStreamCall> Answered(IEnumerable<AgentEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return Read(stream);
    }

    private static IEnumerable<AgentStreamCall> Read(IEnumerable<AgentEvent> stream)
    {
        var tools = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var @event in stream)
        {
            switch (@event)
            {
                case ToolCallStarted started:
                    tools[started.Id] = ToolOf(started.Tool);
                    break;

                case AssistantMessage message:
                    foreach (var made in message.Calls)
                    {
                        tools[made.Id] = ToolOf(made.Name);
                    }

                    break;

                case ToolCallFinished finished when tools.TryGetValue(finished.Id, out var tool) && InputOf(finished.Input) is { } input:
                    yield return new AgentStreamCall(finished.Id, tool, input, finished.Result, finished.Ok);
                    break;
            }
        }
    }

    /// <summary>A call's input as the JSON object it holds; null where it holds none, or does not read.</summary>
    private static JsonObject? InputOf(string input)
    {
        try
        {
            return JsonNode.Parse(input) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
