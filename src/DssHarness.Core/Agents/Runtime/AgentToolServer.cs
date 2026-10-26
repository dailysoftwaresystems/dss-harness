using System.Text.Json.Nodes;
using DssHarness.Core.Hosts;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// The engine's tool server (part 03; part 08, "Claude Code: the graph as an MCP server the engine serves"): the runtime's
/// tools a Claude Code launch takes from the engine, served as one MCP server of type <c>sdk</c>, whose every message
/// Claude Code sends its host as an <c>mcp_message</c> control request. It speaks the part of MCP a server of tools needs -
/// JSON-RPC 2.0 over <c>System.Text.Json</c>, no MCP library: <c>initialize</c>, the notifications, <c>tools/list</c>,
/// <c>tools/call</c> and <c>ping</c> - and each call is answered by the runtime, the owner of the tool on every client.
/// </summary>
/// <remarks>
/// It decides nothing of whether a call may run: the client asks the engine about each call first, by its ask rule, and
/// hands the server only a call the engine allowed (<see cref="Read"/> says which a message is). Each call's rules are its
/// tool's, decided as it runs and recorded - each address of the web asked of the policy, as on the API clients.
/// </remarks>
/// <param name="runtime">The runtime whose tools are served: those Claude Code takes from the engine alone.</param>
public sealed class AgentToolServer(AgentToolRuntime runtime)
{
    /// <summary>The server's name, as <c>--mcp-config</c> names it and as Claude Code begins its tools' names (<c>mcp__dssharness__web_search</c>).</summary>
    public const string Name = ToolPackage.Command;

    /// <summary>The member of a call's <c>_meta</c> in which Claude Code names the call it asked the engine about.</summary>
    public const string CallIdMeta = "claudecode/toolUseId";

    /// <summary>
    /// The versions of MCP the server speaks, oldest first: the part it serves - tools, and their text - reads alike in each.
    /// A client asking another is answered with the newest, as the protocol says a server answers one it does not speak.
    /// </summary>
    public static IReadOnlyList<string> ProtocolVersions { get; } = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];

    /// <summary>JSON-RPC's code of a message that is no request.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>JSON-RPC's code of a method the server has not.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>JSON-RPC's code of a request whose parameters do not read.</summary>
    public const int InvalidParams = -32602;

    /// <summary>MCP's code of a request its client cancelled.</summary>
    public const int RequestCancelled = -32800;

    private readonly AgentToolRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>
    /// What a message is to the server: one answered at once - a request of anything but a call, a notification, a message
    /// that does not read - a call to run, or the cancellation of a call running, answered at once too.
    /// </summary>
    /// <param name="message">The JSON-RPC message, as the <c>mcp_message</c> carried it.</param>
    public AgentToolServerMessage Read(JsonNode? message)
    {
        if (message is not JsonObject request || request["method"] is not JsonValue named || !named.TryGetValue<string>(out var method))
        {
            // A message with no method is no request of a client's: one with an id is answered with why, one with none is
            // nothing a server answers, and is acknowledged as a notification is.
            return message is JsonObject { } withId && withId["id"] is { } id
                ? new AgentToolServerMessage.Answered(Error(id, InvalidRequest, "the message names no method"))
                : new AgentToolServerMessage.Answered(Acknowledged());
        }

        var asked = request["id"];

        if (asked is null)
        {
            // A notification is answered with nothing of its own, as the SDK answers one: the channel's request still
            // wants its answer. A call's cancellation names the call it cancels.
            return method == "notifications/cancelled" && (request["params"] as JsonObject)?["requestId"] is { } cancelled
                ? new AgentToolServerMessage.Cancelled(Acknowledged(), Key(cancelled))
                : new AgentToolServerMessage.Answered(Acknowledged());
        }

        return method switch
        {
            "initialize" => new AgentToolServerMessage.Answered(Result(asked, new JsonObject
            {
                ["protocolVersion"] = (request["params"] as JsonObject)?["protocolVersion"] is JsonValue version && version.TryGetValue<string>(out var spoken) && ProtocolVersions.Contains(spoken, StringComparer.Ordinal)
                    ? spoken
                    : ProtocolVersions[^1],
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = _runtime.ToolVersion },
            })),
            "ping" => new AgentToolServerMessage.Answered(Result(asked, new JsonObject())),
            "tools/list" => new AgentToolServerMessage.Answered(Result(asked, new JsonObject
            {
                ["tools"] = new JsonArray([.. _runtime.Tools.Select(tool => (JsonNode)new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = JsonNode.Parse(tool.Schema),
                })]),
            })),
            "tools/call" => CallOf(asked, request["params"] as JsonObject),
            _ => new AgentToolServerMessage.Answered(Error(asked, MethodNotFound, $"the server '{Name}' has no method '{method}'")),
        };
    }

    /// <summary>
    /// Runs a call the engine allowed, by the runtime, and answers it: the tool's text, and whether it is an error - a
    /// refusal or a failure, whose text says why. A call its client cancelled is answered as cancelled.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Cancels the call: its client's cancellation, or the launch's end.</param>
    /// <returns>The reply, and what the runtime answered; null where the call was cancelled.</returns>
    public async Task<(JsonObject Reply, AgentToolCallResult? Result)> CallAsync(AgentToolServerMessage.Call call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        AgentToolCallResult result;

        try
        {
            result = await _runtime.ServeAsync(
                new AgentToolCallRequest(call.CallId ?? throw new ArgumentException("a call the engine allowed names the call it was asked as", nameof(call)), call.Tool, call.Arguments.ToJsonString()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (Error(call.Id, RequestCancelled, $"the call of '{call.Tool}' was cancelled"), null);
        }

        return (Result(call.Id, Text(result.Text, isError: !result.Ok)), result);
    }

    /// <summary>The answer to a call the engine never allowed: an error the model reads, and nothing run.</summary>
    /// <param name="call">The call.</param>
    /// <param name="why">Why it does not run.</param>
    public static JsonObject Refused(AgentToolServerMessage.Call call, string why)
    {
        ArgumentNullException.ThrowIfNull(call);

        return Result(call.Id, Text(why, isError: true));
    }

    /// <summary>A call's parameters read: its tool among those served, its arguments an object, and the call it was asked as.</summary>
    private AgentToolServerMessage CallOf(JsonNode id, JsonObject? parameters)
    {
        if (parameters?["name"] is not JsonValue named || !named.TryGetValue<string>(out var tool))
        {
            return new AgentToolServerMessage.Answered(Error(id, InvalidParams, "the call names no tool"));
        }

        if (!_runtime.Tools.Any(served => served.Name == tool))
        {
            return new AgentToolServerMessage.Answered(Error(id, InvalidParams, $"the server '{Name}' has no tool '{tool}': it has {string.Join(", ", _runtime.Tools.Select(served => served.Name))}"));
        }

        if (parameters["arguments"] is not (null or JsonObject))
        {
            return new AgentToolServerMessage.Answered(Error(id, InvalidParams, $"the call of '{tool}' gives arguments that are no object"));
        }

        var arguments = parameters["arguments"]?.DeepClone() as JsonObject ?? [];
        var callId = (parameters["_meta"] as JsonObject)?[CallIdMeta] is JsonValue meta && meta.TryGetValue<string>(out var said) && said.Length > 0 ? said : null;

        return new AgentToolServerMessage.Call(id.DeepClone(), tool, arguments, callId);
    }

    /// <summary>A tool's answer as MCP carries it: its text, and whether it is an error.</summary>
    private static JsonObject Text(string text, bool isError)
        => new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };

    /// <summary>A request's result.</summary>
    private static JsonObject Result(JsonNode id, JsonObject result)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    /// <summary>A request's error.</summary>
    private static JsonObject Error(JsonNode id, int code, string message)
        => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /// <summary>What a notification, or a message no server answers, is acknowledged with: nothing of its own.</summary>
    private static JsonObject Acknowledged() => new() { ["jsonrpc"] = "2.0", ["result"] = new JsonObject() };

    /// <summary>A request's id as a key, the same for the call and its cancellation: its JSON.</summary>
    /// <param name="id">The id.</param>
    public static string Key(JsonNode id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return id.ToJsonString();
    }
}

/// <summary>What a message of a client's is to the engine's tool server.</summary>
public abstract record AgentToolServerMessage
{
    /// <summary>A message answered at once: a request of anything but a call, a notification, one that does not read.</summary>
    /// <param name="Reply">The reply.</param>
    public sealed record Answered(JsonObject Reply) : AgentToolServerMessage;

    /// <summary>A call of a served tool, to run where the engine allowed it.</summary>
    /// <param name="Id">The request's id, which the reply names.</param>
    /// <param name="Tool">The tool, by the runtime's name.</param>
    /// <param name="Arguments">Its arguments.</param>
    /// <param name="CallId">The call Claude Code asked the engine about, by its id; null where the call names none.</param>
    public sealed record Call(JsonNode Id, string Tool, JsonObject Arguments, string? CallId) : AgentToolServerMessage;

    /// <summary>A call's cancellation, acknowledged at once.</summary>
    /// <param name="Reply">The acknowledgement.</param>
    /// <param name="RequestId">The cancelled call's request, as <see cref="AgentToolServer.Key"/> keys it.</param>
    public sealed record Cancelled(JsonObject Reply, string RequestId) : AgentToolServerMessage;
}
