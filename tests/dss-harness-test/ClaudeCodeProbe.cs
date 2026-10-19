using System.Text.Json.Nodes;

namespace DssHarness.Tests;

/// <summary>
/// Reads what a Claude Code probe recorded: the line kinds of its stream, and the facts the
/// probe's answers rest on - what a launch began as, what it asked the host, and how each turn
/// ended.
/// </summary>
/// <remarks>
/// Built from the recordings under <c>fixtures/claude-code/</c>, whose README says how they are
/// made and what is elided from them. Nothing here runs anything, and nothing here reads the
/// network.
/// </remarks>
internal static class ClaudeCodeProbe
{
    /// <summary>One recorded line: its kind - <c>system</c>, <c>user</c>, <c>assistant</c>, <c>stream_event</c>, <c>control_request</c>, <c>control_response</c>, <c>rate_limit_event</c>, or <c>result</c> - and the line whole.</summary>
    /// <param name="Kind">The line's own <c>type</c>; a result's, which carries it inside.</param>
    /// <param name="Node">The line, parsed.</param>
    internal sealed record Line(string Kind, JsonNode Node);

    /// <summary>What one launch began as, as its <c>system/init</c> line says.</summary>
    /// <param name="Tools">The tools the launch was given, as it names them.</param>
    /// <param name="Model">The model it runs on.</param>
    /// <param name="ApiKeySource">Where its credential came from, as it says; never a value.</param>
    /// <param name="Version">The program's own version.</param>
    /// <param name="Capabilities">The control protocol it speaks, as it names the parts.</param>
    internal sealed record Began(IReadOnlyList<string> Tools, string Model, string ApiKeySource, string Version, IReadOnlyList<string> Capabilities);

    /// <summary>One ask the launch put to its host: a tool call held for a decision.</summary>
    /// <param name="Id">The ask's own id, which the host's answer names.</param>
    /// <param name="Tool">The tool it asks about.</param>
    /// <param name="ToolUseId">The tool call the ask holds.</param>
    internal sealed record Ask(string Id, string Tool, string ToolUseId);

    /// <summary>How one turn ended, as its result line says.</summary>
    /// <param name="TerminalReason">Why it ended.</param>
    /// <param name="Denials">How many permissions it was denied.</param>
    /// <param name="IsError">Whether it ended in error.</param>
    /// <param name="Turns">How many turns it took.</param>
    /// <param name="CostUsd">What it cost, as the program counted it.</param>
    /// <param name="Subtype">The result's subtype: <c>success</c>, or the error it ended in.</param>
    /// <param name="Errors">The errors an error subtype carries, in place of a result.</param>
    /// <param name="HasResult">Whether the line carries a <c>result</c> member at all.</param>
    internal sealed record Ended(string TerminalReason, int Denials, bool IsError, int Turns, decimal CostUsd, string Subtype, IReadOnlyList<string> Errors, bool HasResult);

    /// <summary>One ask's reasons, as the child gave them: why the call was put to the host.</summary>
    /// <param name="Tool">The tool it asks about.</param>
    /// <param name="ReasonType">The <c>decision_reason_type</c> - <c>rule</c>, <c>other</c>, <c>safetyCheck</c>, <c>workingDir</c> - or empty where the ask names none.</param>
    /// <param name="Input">The call's input, whole.</param>
    internal sealed record Reason(string Tool, string ReasonType, JsonNode? Input);

    /// <summary>Reads a recording, one line each, in the order they came.</summary>
    /// <param name="path">The recording's own file.</param>
    /// <returns>The lines, their kinds and their wholes.</returns>
    internal static List<Line> Read(string path)
        => [.. File.ReadLines(path).Where(line => line.Length > 0).Select(One)];

    /// <summary>What a launch began as, where the line is its <c>system/init</c>; null where it is not.</summary>
    /// <param name="line">One recorded line.</param>
    internal static Began? BeganOf(Line line)
    {
        if (line.Kind != "system" || line.Node["subtype"]?.GetValue<string>() != "init")
        {
            return null;
        }

        return new Began(
            [.. (line.Node["tools"] as JsonArray ?? []).Select(tool => tool!.GetValue<string>())],
            line.Node["model"]!.GetValue<string>(),
            line.Node["apiKeySource"]!.GetValue<string>(),
            line.Node["claude_code_version"]!.GetValue<string>(),
            [.. (line.Node["capabilities"] as JsonArray ?? []).Select(one => one!.GetValue<string>())]);
    }

    /// <summary>One ask, where the line is a <c>can_use_tool</c> request; null where it is not.</summary>
    /// <param name="line">One recorded line.</param>
    internal static Ask? AskOf(Line line)
    {
        if (line.Kind != "control_request" || line.Node["request"]?["subtype"]?.GetValue<string>() != "can_use_tool")
        {
            return null;
        }

        return new Ask(
            line.Node["request_id"]!.GetValue<string>(),
            line.Node["request"]!["tool_name"]!.GetValue<string>(),
            line.Node["request"]!["tool_use_id"]!.GetValue<string>());
    }

    /// <summary>How the turn ended, where the line is its result; null where it is not.</summary>
    /// <param name="line">One recorded line.</param>
    internal static Ended? EndOf(Line line)
    {
        if (line.Kind != "result")
        {
            return null;
        }

        return new Ended(
            line.Node["terminal_reason"]?.GetValue<string>() ?? "",
            (line.Node["permission_denials"] as JsonArray ?? []).Count,
            line.Node["is_error"]?.GetValue<bool>() ?? false,
            line.Node["num_turns"]?.GetValue<int>() ?? 0,
            line.Node["total_cost_usd"]?.GetValue<decimal>() ?? 0,
            line.Node["subtype"]?.GetValue<string>() ?? "",
            [.. (line.Node["errors"] as JsonArray ?? []).Select(error => error!.GetValue<string>())],
            line.Node.AsObject().ContainsKey("result"));
    }

    /// <summary>Why an ask was put to the host, where the line is a <c>can_use_tool</c> request; null where it is not.</summary>
    /// <param name="line">One recorded line.</param>
    internal static Reason? ReasonOf(Line line)
    {
        if (AskOf(line) is not { } ask)
        {
            return null;
        }

        return new Reason(ask.Tool, line.Node["request"]!["decision_reason_type"]?.GetValue<string>() ?? "", line.Node["request"]!["input"]);
    }

    /// <summary>The text of every tool result the lines hold, in the order they came: a result's own text, or its text blocks joined.</summary>
    /// <param name="lines">Recorded lines.</param>
    internal static List<string> ToolResultsOf(IEnumerable<Line> lines)
        => [.. lines
            .Where(line => line.Kind == "user")
            .SelectMany(line => (line.Node["message"]?["content"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(block => block["type"]?.GetValue<string>() == "tool_result")
            .Select(block => block["content"] switch
            {
                JsonValue text => text.GetValue<string>(),
                JsonArray parts => string.Concat(parts.OfType<JsonObject>().Select(part => part["text"]?.GetValue<string>() ?? "")),
                _ => "",
            })];

    /// <summary>The model every assistant line names, in the order they came.</summary>
    /// <param name="lines">Recorded lines.</param>
    internal static List<string> ModelsOf(IEnumerable<Line> lines)
        => [.. lines.Where(line => line.Kind == "assistant").Select(line => line.Node["message"]!["model"]!.GetValue<string>())];

    /// <summary>The <c>system</c> lines of one subtype, in the order they came.</summary>
    /// <param name="lines">Recorded lines.</param>
    /// <param name="subtype">The subtype, such as <c>permission_denied</c> or <c>model_fallback</c>.</param>
    internal static List<JsonNode> SystemOf(IEnumerable<Line> lines, string subtype)
        => [.. lines.Where(line => line.Kind == "system" && line.Node["subtype"]?.GetValue<string>() == subtype).Select(line => line.Node)];

    /// <summary>What a probe found beside its recordings, one entry a launch, keyed by the launch's name.</summary>
    /// <param name="path">A channel's curated <c>findings.jsonl</c>.</param>
    internal static Dictionary<string, JsonNode> FindingsOf(string path)
        => File.ReadLines(path)
            .Where(line => line.Length > 0)
            .Select(line => JsonNode.Parse(line)!)
            .ToDictionary(node => node["launch"]!.GetValue<string>(), StringComparer.Ordinal);

    /// <summary>One line, parsed: its kind from its own <c>type</c>, a result's from inside, where it carries it.</summary>
    /// <param name="line">The line, whole.</param>
    private static Line One(string line)
    {
        var node = JsonNode.Parse(line)!;

        return new Line(node["type"]?.GetValue<string>() ?? node["subtype"]?.GetValue<string>() switch
        {
            "init" => "system",
            _ => "result",
        }, node);
    }
}
