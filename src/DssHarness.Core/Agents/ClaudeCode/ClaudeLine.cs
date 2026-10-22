using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// One line of a launch's output, read as what it is in Claude Code's stream-json: the launch's own start, an assistant
/// message, a user message it took or a call's result, a piece of a message being written, a turn's result, a control
/// message, or something else the stream holds that the client passes by. Each is read from the probe's recordings,
/// which pin every kind on every version taken.
/// </summary>
/// <param name="Node">The line as it was read, whole.</param>
public abstract record ClaudeLine(JsonObject Node)
{
    /// <summary><c>system/init</c>: what the launch runs as, said again at every turn.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Model">The model it runs on.</param>
    /// <param name="Tools">The tools it has, as it names them.</param>
    /// <param name="ApiKeySource">Where its credential came from, as Claude Code names it.</param>
    /// <param name="Version">Claude Code's version.</param>
    public sealed record Init(JsonObject Node, string? Model, IReadOnlyList<string> Tools, string? ApiKeySource, string? Version) : ClaudeLine(Node);

    /// <summary>An assistant message, whole: its text, its thinking, each call it made, and the error Claude Code wrote it for.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Model">The model that wrote it; <see cref="Synthetic"/> for one Claude Code wrote itself.</param>
    /// <param name="Text">Its text, its blocks joined.</param>
    /// <param name="Thinking">Its thinking as far as the provider shows it; null where it has none.</param>
    /// <param name="Calls">Each call it made, with its input.</param>
    /// <param name="Error">The failure Claude Code wrote it for (<c>authentication_failed</c>, <c>rate_limit</c>, ...); null for the model's own message.</param>
    /// <param name="Usage">What it used, where it says.</param>
    public sealed record Assistant(
        JsonObject Node,
        string? Model,
        string Text,
        string? Thinking,
        IReadOnlyList<Call> Calls,
        string? Error,
        AgentUsage? Usage) : ClaudeLine(Node);

    /// <summary>A user message in the stream: one the launch took from its input, echoed, or the results of the calls it ran.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Replay">Whether it is the echo of a message the launch took from its input.</param>
    /// <param name="Text">Its text, where it holds text.</param>
    /// <param name="Results">The results of calls it carries.</param>
    public sealed record User(JsonObject Node, bool Replay, string Text, IReadOnlyList<CallResult> Results) : ClaudeLine(Node);

    /// <summary>A piece of a message being written (<c>stream_event</c>), as the deltas a front end shows.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Kind">What it is: <c>text</c>, <c>thinking</c>, <c>call</c> (a call's start), <c>input</c> (a piece of a call's input), <c>start</c> (a message's start), or the event's own type for any other.</param>
    /// <param name="Index">The content block it is of, where it names one.</param>
    /// <param name="Text">What it adds: the text, the thinking, the input's piece; the tool's name for a call's start; the model for a message's start.</param>
    /// <param name="CallId">The call's id, for a call's start.</param>
    public sealed record Piece(JsonObject Node, string Kind, int? Index, string? Text, string? CallId) : ClaudeLine(Node);

    /// <summary>A turn's end (<c>result</c>), which ends the turn and not the process.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Subtype">Its subtype: <c>success</c>, or the error it ended on.</param>
    /// <param name="IsError">Whether it is an error, whatever the subtype says.</param>
    /// <param name="Text">The answer as text, where it carries one.</param>
    /// <param name="Structured">The answer held to the launch's schema, where it carries one.</param>
    /// <param name="Errors">The error lines it carries.</param>
    /// <param name="TerminalReason">Why the turn ended, as Claude Code says it.</param>
    /// <param name="StopReason">The last message's stop reason.</param>
    /// <param name="CostUsd">What the launch has cost so far, as Claude Code reports it.</param>
    /// <param name="Usage">What the turn used.</param>
    /// <param name="Turns">How many requests the turn made, as Claude Code counts them.</param>
    public sealed record Result(
        JsonObject Node,
        string Subtype,
        bool IsError,
        string? Text,
        JsonNode? Structured,
        IReadOnlyList<string> Errors,
        string? TerminalReason,
        string? StopReason,
        decimal? CostUsd,
        AgentUsage Usage,
        int Turns) : ClaudeLine(Node);

    /// <summary>A control request the launch makes of its host: a call it asks about, among others.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="RequestId">The request's id, which the answer names.</param>
    /// <param name="Subtype">What it asks: <c>can_use_tool</c> for a call.</param>
    /// <param name="Tool">The tool a call is of.</param>
    /// <param name="Input">The call's input.</param>
    /// <param name="CallId">The call's id.</param>
    public sealed record ControlRequest(JsonObject Node, string RequestId, string Subtype, string? Tool, JsonNode? Input, string? CallId) : ClaudeLine(Node);

    /// <summary>The launch's answer to a control request of the host's: the initialize, an interrupt.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="RequestId">The request it answers.</param>
    /// <param name="Succeeded">Whether it says success.</param>
    /// <param name="Error">What it says went wrong, where it did.</param>
    public sealed record ControlResponse(JsonObject Node, string RequestId, bool Succeeded, string? Error) : ClaudeLine(Node);

    /// <summary>The launch withdraws a control request it made: no answer is wanted any more.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="RequestId">The request withdrawn.</param>
    public sealed record ControlCancel(JsonObject Node, string RequestId) : ClaudeLine(Node);

    /// <summary><c>system/api_retry</c>: Claude Code waits to ask the provider again.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Attempt">Which try comes next.</param>
    /// <param name="Of">Of how many.</param>
    /// <param name="Delay">How long it waits.</param>
    /// <param name="Cause">Why, as it says it.</param>
    public sealed record Retry(JsonObject Node, int Attempt, int Of, TimeSpan Delay, string Cause) : ClaudeLine(Node);

    /// <summary><c>system/permission_denied</c>: Claude Code refused a call by itself, without asking.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Tool">The tool.</param>
    /// <param name="CallId">The call's id.</param>
    /// <param name="Message">What it said.</param>
    public sealed record Refusal(JsonObject Node, string? Tool, string? CallId, string Message) : ClaudeLine(Node);

    /// <summary>Anything else the stream holds - a status, a rate limit's state, the thinking's count - which the client passes by.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Type">Its type.</param>
    /// <param name="Subtype">Its subtype, where it has one.</param>
    public sealed record Other(JsonObject Node, string Type, string? Subtype) : ClaudeLine(Node);

    /// <summary>The model a message Claude Code wrote itself names: no model wrote it.</summary>
    public const string Synthetic = "<synthetic>";

    /// <summary>One call an assistant message made.</summary>
    /// <param name="Id">The call's id.</param>
    /// <param name="Tool">The tool.</param>
    /// <param name="Input">Its input, as the model wrote it.</param>
    public sealed record Call(string Id, string Tool, JsonNode? Input);

    /// <summary>The result of one call, as the launch gave it back to the model.</summary>
    /// <param name="CallId">The call's id.</param>
    /// <param name="Ok">Whether it ran: false for a refusal and for a failure alike.</param>
    /// <param name="Content">What it returned, as text.</param>
    public sealed record CallResult(string CallId, bool Ok, string Content);

    /// <summary>Reads one line; null where it is no JSON object, which the stream never writes and a client passes by.</summary>
    /// <param name="line">The line as the launch wrote it.</param>
    public static ClaudeLine? Read(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        JsonObject? node;

        try
        {
            node = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is null)
        {
            return null;
        }

        var type = TextOf(node["type"]) ?? string.Empty;
        var subtype = TextOf(node["subtype"]);

        return (type, subtype) switch
        {
            ("system", "init") => new Init(node, TextOf(node["model"]), TextsOf(node["tools"]), TextOf(node["apiKeySource"]), TextOf(node["claude_code_version"])),
            ("system", "api_retry") => RetryOf(node),
            ("system", "permission_denied") => new Refusal(node, TextOf(node["tool_name"]), TextOf(node["tool_use_id"]), TextOf(node["message"]) ?? string.Empty),
            ("assistant", _) => AssistantOf(node),
            ("user", _) => UserOf(node),
            ("stream_event", _) => PieceOf(node),
            ("result", _) => ResultOf(node),
            ("control_request", _) => new ControlRequest(
                node,
                TextOf(node["request_id"]) ?? string.Empty,
                TextOf(node["request"]?["subtype"]) ?? string.Empty,
                TextOf(node["request"]?["tool_name"]),
                node["request"]?["input"],
                TextOf(node["request"]?["tool_use_id"])),
            ("control_response", _) => new ControlResponse(
                node,
                TextOf(node["response"]?["request_id"]) ?? string.Empty,
                TextOf(node["response"]?["subtype"]) == "success",
                TextOf(node["response"]?["error"])),
            ("control_cancel_request", _) => new ControlCancel(node, TextOf(node["request_id"]) ?? string.Empty),
            _ => new Other(node, type, subtype),
        };
    }

    /// <summary>What a turn or a message used, as Claude Code reports it: the hour's cache writes apart from the rest.</summary>
    /// <param name="usage">The <c>usage</c> member.</param>
    public static AgentUsage UsageOf(JsonNode? usage)
    {
        if (usage is not JsonObject held)
        {
            return AgentUsage.None;
        }

        var written = Number(held["cache_creation_input_tokens"]);
        var hour = Number(held["cache_creation"]?["ephemeral_1h_input_tokens"]);

        return new AgentUsage(
            Number(held["input_tokens"]),
            Number(held["output_tokens"]),
            Number(held["cache_read_input_tokens"]),
            Math.Max(0, written - hour),
            hour,
            Number(held["server_tool_use"]?["web_search_requests"]));
    }

    private static Assistant AssistantOf(JsonObject node)
    {
        var message = node["message"] as JsonObject;
        var text = new List<string>();
        var thinking = new List<string>();
        var calls = new List<Call>();

        foreach (var block in (message?["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            switch (TextOf(block["type"]))
            {
                case "text":
                    text.Add(TextOf(block["text"]) ?? string.Empty);
                    break;
                case "thinking":
                    thinking.Add(TextOf(block["thinking"]) ?? string.Empty);
                    break;
                case "tool_use":
                    calls.Add(new Call(TextOf(block["id"]) ?? string.Empty, TextOf(block["name"]) ?? string.Empty, block["input"]?.DeepClone()));
                    break;
            }
        }

        return new Assistant(
            node,
            TextOf(message?["model"]),
            string.Concat(text),
            thinking.Count == 0 ? null : string.Concat(thinking),
            calls,
            TextOf(node["error"]),
            message?["usage"] is JsonObject usage ? UsageOf(usage) : null);
    }

    private static User UserOf(JsonObject node)
    {
        var content = node["message"]?["content"];
        var text = TextOf(content);
        var results = new List<CallResult>();

        if (content is JsonArray blocks)
        {
            var parts = new List<string>();

            foreach (var block in blocks.OfType<JsonObject>())
            {
                switch (TextOf(block["type"]))
                {
                    case "text":
                        parts.Add(TextOf(block["text"]) ?? string.Empty);
                        break;
                    case "tool_result":
                        results.Add(new CallResult(
                            TextOf(block["tool_use_id"]) ?? string.Empty,
                            block["is_error"] is not JsonValue error || !error.TryGetValue<bool>(out var failed) || !failed,
                            ResultText(block["content"])));
                        break;
                }
            }

            text = string.Concat(parts);
        }

        return new User(node, node["isReplay"] is JsonValue replay && replay.TryGetValue<bool>(out var echoed) && echoed, text ?? string.Empty, results);
    }

    private static Piece PieceOf(JsonObject node)
    {
        var @event = node["event"];
        var kind = TextOf(@event?["type"]) ?? string.Empty;
        int? index = @event?["index"] is JsonValue at && at.TryGetValue<int>(out var number) ? number : null;

        return kind switch
        {
            "content_block_delta" => TextOf(@event?["delta"]?["type"]) switch
            {
                "text_delta" => new Piece(node, "text", index, TextOf(@event?["delta"]?["text"]), null),
                "thinking_delta" => new Piece(node, "thinking", index, TextOf(@event?["delta"]?["thinking"]), null),
                "input_json_delta" => new Piece(node, "input", index, TextOf(@event?["delta"]?["partial_json"]), null),
                var other => new Piece(node, other ?? kind, index, null, null),
            },
            "content_block_start" when TextOf(@event?["content_block"]?["type"]) == "tool_use"
                => new Piece(node, "call", index, TextOf(@event?["content_block"]?["name"]), TextOf(@event?["content_block"]?["id"])),
            "message_start" => new Piece(node, "start", index, TextOf(@event?["message"]?["model"]), null),
            _ => new Piece(node, kind, index, null, null),
        };
    }

    private static Result ResultOf(JsonObject node)
        => new(
            node,
            TextOf(node["subtype"]) ?? string.Empty,
            node["is_error"] is JsonValue error && error.TryGetValue<bool>(out var failed) && failed,
            TextOf(node["result"]),
            node["structured_output"]?.DeepClone(),
            TextsOf(node["errors"]),
            TextOf(node["terminal_reason"]),
            TextOf(node["stop_reason"]),
            node["total_cost_usd"] is JsonValue cost && cost.TryGetValue<decimal>(out var usd) ? usd : null,
            UsageOf(node["usage"]),
            (int)Number(node["num_turns"]));

    private static Retry RetryOf(JsonObject node)
        => new(
            node,
            (int)Number(node["attempt"]),
            (int)Number(node["max_retries"]),
            TimeSpan.FromMilliseconds(Number(node["retry_delay_ms"])),
            TextOf(node["error"]) ?? TextOf(node["error_status"]) ?? (node["error_status"] is JsonValue status ? status.ToJsonString() : "a failure Claude Code tries again"));

    /// <summary>A call's result as text: a string as it is, a list of blocks as their texts joined.</summary>
    private static string ResultText(JsonNode? content)
        => content switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonArray blocks => string.Concat(blocks.OfType<JsonObject>().Select(block => TextOf(block["text"]) ?? string.Empty)),
            null => string.Empty,
            var other => other.ToJsonString(),
        };

    private static string? TextOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static List<string> TextsOf(JsonNode? node)
        => node is JsonArray items ? [.. items.Select(TextOf).OfType<string>()] : [];

    private static long Number(JsonNode? node)
        => node is JsonValue value
            ? value.TryGetValue<long>(out var whole) ? whole
            : value.TryGetValue<double>(out var real) ? (long)real
            : value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
            : 0
            : 0;
}
