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
    /// <param name="McpServers">Each MCP server the launch has, by its name, with its status: <c>connected</c> for one it took.</param>
    public sealed record Init(JsonObject Node, string? Model, IReadOnlyList<string> Tools, string? ApiKeySource, string? Version, IReadOnlyList<(string Name, string? Status)> McpServers) : ClaudeLine(Node);

    /// <summary>An assistant message, whole: its text, its thinking, each call it made, and the error Claude Code wrote it for.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Model">The model that wrote it; <see cref="Synthetic"/> for one Claude Code wrote itself.</param>
    /// <param name="Text">Its text, its blocks joined.</param>
    /// <param name="Thinking">Its thinking as far as the provider shows it; null where it has none.</param>
    /// <param name="Calls">Each call it made, with its input.</param>
    /// <param name="Error">The failure Claude Code wrote it for (<c>authentication_failed</c>, <c>rate_limit</c>, ...); null for the model's own message.</param>
    /// <param name="Usage">What it used, where it says.</param>
    /// <param name="StopReason">Why the model stopped writing it, where it says: <c>refusal</c> for a message it declined.</param>
    /// <param name="Category">What a decline was of, where the provider names it.</param>
    public sealed record Assistant(
        JsonObject Node,
        string? Model,
        string Text,
        string? Thinking,
        IReadOnlyList<Call> Calls,
        string? Error,
        AgentUsage? Usage,
        string? StopReason = null,
        string? Category = null) : ClaudeLine(Node);

    /// <summary>A user message in the stream: one the launch took from its input, echoed, or the results of the calls it ran.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Replay">Whether it is the echo of a message the launch took from its input.</param>
    /// <param name="Text">Its text, where it holds text.</param>
    /// <param name="Results">The results of calls it carries.</param>
    public sealed record User(JsonObject Node, bool Replay, string Text, IReadOnlyList<CallResult> Results) : ClaudeLine(Node);

    /// <summary>A piece of a message being written (<c>stream_event</c>), as the deltas a front end shows.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="Kind">What it is: <c>text</c>, <c>thinking</c>, <c>call</c> (a call's start), <c>input</c> (a piece of a call's input), <c>start</c> (a message's start), <c>stop</c> (why a message stopped), or the event's own type for any other.</param>
    /// <param name="Index">The content block it is of, where it names one.</param>
    /// <param name="Text">What it adds: the text, the thinking, the input's piece; the tool's name for a call's start; the model for a message's start; the stop reason for a message's stop, its category after it where the provider names one (<c>refusal:cyber</c>).</param>
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

    /// <summary>A control request the launch makes of its host: a call it asks about, a message of an MCP server the host serves, among others.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="RequestId">The request's id, which the answer names.</param>
    /// <param name="Subtype">What it asks: <c>can_use_tool</c> for a call, <c>mcp_message</c> for a message of a server of the host's.</param>
    /// <param name="Tool">The tool a call is of.</param>
    /// <param name="Input">The call's input.</param>
    /// <param name="CallId">The call's id.</param>
    /// <param name="Server">The server an MCP message is for.</param>
    /// <param name="Message">The MCP message, as JSON-RPC writes it.</param>
    public sealed record ControlRequest(JsonObject Node, string RequestId, string Subtype, string? Tool, JsonNode? Input, string? CallId, string? Server = null, JsonNode? Message = null) : ClaudeLine(Node);

    /// <summary>The launch's answer to a control request of the host's: the initialize, an interrupt.</summary>
    /// <param name="Node">The line.</param>
    /// <param name="RequestId">The request it answers.</param>
    /// <param name="Succeeded">Whether it says success.</param>
    /// <param name="Error">What it says went wrong, where it did.</param>
    /// <param name="Cancelled">
    /// The ids of the waiting messages an interrupt's receipt says it dropped: none of them is taken. A message is named
    /// by the id the host gave it, and one given none is never named.
    /// </param>
    public sealed record ControlResponse(JsonObject Node, string RequestId, bool Succeeded, string? Error, IReadOnlyList<string>? Cancelled = null) : ClaudeLine(Node);

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

    /// <summary>
    /// <c>rate_limit_event</c>: where the account stands with its allowances. <c>rejected</c> is an account at its limit -
    /// an allowance used up, or with <c>errorCode: credits_required</c> its included usage spent.
    /// </summary>
    /// <param name="Node">The line.</param>
    /// <param name="Status">Where it stands: <c>allowed</c>, <c>allowed_warning</c>, <c>rejected</c>.</param>
    /// <param name="ResetsAt">When the allowance resets, where the event says.</param>
    /// <param name="Window">Which allowance it is of (<c>five_hour</c>, <c>seven_day</c>, ...), where it says.</param>
    /// <param name="ErrorCode">Why it was rejected, where it says: <c>credits_required</c> for included usage spent.</param>
    public sealed record RateLimit(JsonObject Node, string Status, DateTimeOffset? ResetsAt, string? Window, string? ErrorCode) : ClaudeLine(Node);

    /// <summary>Anything else the stream holds - a status, the thinking's count - which the client passes by.</summary>
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
    /// <param name="Ok">Whether it succeeded: false for a refusal and for a failure alike.</param>
    /// <param name="Content">What it returned, as text.</param>
    /// <param name="Ran">
    /// Whether the tool ran, failed or not. Every version taken stamps a result whose tool did not run with its
    /// <c>non_execution_kind</c> (a refusal; a call the interrupt cut while it was asked, <c>user-rejected</c>, as 2.1.296
    /// was seen to on 2026-10-22) - but a file tool's call its own deny rules refuse as it reads the input
    /// (<paramref name="RefusedByRule"/>); 2.1.303 also stamps a permission decision on every call it decides, asked of the
    /// host or refused by those rules, run or not - and one it rejected did not run. A call that failed before that - its
    /// input did not read, or a deny rule refused it on 2.1.287 - carries Claude Code's <see cref="InputErrorMark"/>, and did
    /// not run; any other ran. Before 2.1.303 a call that ran and failed with that mark reads as one refused on its input:
    /// nothing beside the result tells them apart.
    /// </param>
    /// <param name="RefusedByRule">
    /// Whether Claude Code's own deny rules refused it as it read the call's input, the host never asked: a file tool's call
    /// whose path a rule denies, told by its result alone - <see cref="DeniedByRuleResult"/>, the same words on every version
    /// taken, a permission decision <c>reject</c> by a <c>rule</c> beside it from 2.1.308 and nothing on 2.1.287 - where a
    /// search's or a command's is told by a <c>system/permission_denied</c> line (the probe's <c>deny-rules</c> launch,
    /// 2026-10-26).
    /// </param>
    public sealed record CallResult(string CallId, bool Ok, string Content, bool Ran = true, bool RefusedByRule = false);

    /// <summary>What a result Claude Code wrote for a call refused before it ran - an input that did not read - begins with.</summary>
    public const string InputErrorMark = "<tool_use_error>";

    /// <summary>
    /// The result Claude Code writes for a file tool's call its own deny rules refuse - the only sign of that refusal on a
    /// version that stamps no permission decision (2.1.287), and the same on every later version taken.
    /// </summary>
    public const string DeniedByRuleResult = InputErrorMark + "File is in a directory that is denied by your permission settings.</tool_use_error>";

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
            ("system", "init") => new Init(
                node,
                TextOf(node["model"]),
                TextsOf(node["tools"]),
                TextOf(node["apiKeySource"]),
                TextOf(node["claude_code_version"]),
                [.. (node["mcp_servers"] as JsonArray ?? []).Select(server => (TextOf(server?["name"]) ?? string.Empty, TextOf(server?["status"])))]),
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
                TextOf(node["request"]?["tool_use_id"]),
                TextOf(node["request"]?["server_name"]),
                node["request"]?["message"]),
            ("control_response", _) => new ControlResponse(
                node,
                TextOf(node["response"]?["request_id"]) ?? string.Empty,
                TextOf(node["response"]?["subtype"]) == "success",
                TextOf(node["response"]?["error"]),
                [.. (node["response"]?["response"]?["cancelled"] as JsonArray ?? []).Select(TextOf).OfType<string>()]),
            ("control_cancel_request", _) => new ControlCancel(node, TextOf(node["request_id"]) ?? string.Empty),
            ("rate_limit_event", _) => RateLimitOf(node),
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
            message?["usage"] is JsonObject usage ? UsageOf(usage) : null,
            TextOf(message?["stop_reason"]),
            TextOf(message?["stop_details"]?["category"]));
    }

    private static RateLimit RateLimitOf(JsonObject node)
    {
        var info = node["rate_limit_info"];

        return new RateLimit(
            node,
            TextOf(info?["status"]) ?? string.Empty,
            info?["resetsAt"] is JsonValue resets && resets.TryGetValue<long>(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null,
            TextOf(info?["rateLimitType"]),
            TextOf(info?["errorCode"]));
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
                        var id = TextOf(block["tool_use_id"]) ?? string.Empty;
                        var ok = block["is_error"] is not JsonValue error || !error.TryGetValue<bool>(out var failed) || !failed;
                        var meta = (node["tool_result_meta"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(entry => TextOf(entry["id"]) == id);
                        var decided = meta?["permission_decision"];
                        var rejected = TextOf(decided?["decision"]) == "reject";
                        var said = ResultText(block["content"]);
                        var ran = meta?["non_execution_kind"] is null && !rejected && (ok || decided is not null || !said.StartsWith(InputErrorMark, StringComparison.Ordinal));

                        // A call that ran says what it says: its output in the refusal's words is no refusal.
                        results.Add(new CallResult(id, ok, said, ran, !ran && said == DeniedByRuleResult));
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
            "message_delta" when TextOf(@event?["delta"]?["stop_reason"]) is { } stop
                => new Piece(node, "stop", index, TextOf(@event?["delta"]?["stop_details"]?["category"]) is { } category ? $"{stop}:{category}" : stop, null),
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
