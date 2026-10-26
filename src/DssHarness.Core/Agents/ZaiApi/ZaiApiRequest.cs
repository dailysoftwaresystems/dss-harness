using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Agents.ZaiApi;

/// <summary>
/// What the glm client sends Z.AI with every request (part 03, "How the two API clients talk to their APIs"): one shape -
/// the conversation's system prompt as its first message, every stored message after it as it was stored, its tools as it
/// began with them, the calls' input streamed, the model's thinking on at the step's effort - and the one owner of what
/// a glm client's <c>requestExtras</c> may add to it, and of the short form older answers go out in.
/// </summary>
/// <remarks>
/// The tool sends no sampling parameter and no tool choice of its own - a client's <c>requestExtras</c> may add them -
/// and never asks Z.AI's search or its reader, whose tools no request declares: the web is the harness's own (value 382).
/// Z.AI holds no answer to a schema: the step's is said in its first message, and an answer that read as no JSON is
/// asked again in JSON alone - no tool offered, <c>response_format</c> a JSON object - as the live probe found it answered.
/// </remarks>
public static class ZaiApiRequest
{
    /// <summary>Whether Z.AI holds every answer to a schema: it takes none, only an answer in JSON (<c>response_format</c>).</summary>
    public const bool HoldsToSchema = false;

    /// <summary>The path of the API's chat completions, under the provider's <c>api</c>.</summary>
    public const string CompletionsPath = "chat/completions";

    /// <summary>
    /// What an answer to a call is sent as once the conversation is sent shortened: the call is still there, and what it
    /// answered is not, which the agent reads.
    /// </summary>
    public static readonly string ShortAnswer = $"(This call's answer is no longer sent: the conversation grew past most of the model's context, and only the answers to the calls of its last {ZaiApiProvider.WholeTurns.ToString(CultureInfo.InvariantCulture)} turns that called tools are sent whole.)";

    /// <summary>Every member of a request's body the tool sends itself, each by its path: what no <c>requestExtras</c> may set.</summary>
    public static readonly IReadOnlyList<OwnedMember> Owned =
    [
        .. new[]
        {
            new[] { "model" }, ["messages"], ["stream"], ["tool_stream"], ["tools"], ["thinking", "type"], ["thinking", "clear_thinking"],
            ["reasoning_effort"], ["max_tokens"], ["response_format"],
        }.Select(path => new OwnedMember(path)),
    ];

    /// <summary>Where a provider's requests are posted: its <c>api</c>, with the completions' path under it.</summary>
    /// <param name="api">The provider's <c>api</c>, an absolute address.</param>
    public static Uri Endpoint(string api) => ApiPost.Under(api, CompletionsPath);

    /// <summary>
    /// What is wrong with a glm client's <c>requestExtras</c>, each said in full (<see cref="ApiBody.ExtrasProblems"/>): an
    /// object, whose members set nothing the tool sends itself (<see cref="Owned"/>).
    /// </summary>
    /// <param name="extras">The client's <c>requestExtras</c>, as the configuration holds it.</param>
    /// <param name="where">Where it is, as a refusal names it: <c>agentClients 'glm' requestExtras</c>.</param>
    public static IEnumerable<string> ExtrasProblems(JsonElement extras, string where) => ApiBody.ExtrasProblems(extras, where, Owned);

    /// <summary>
    /// The body of <paramref name="request"/>, as JSON: the system prompt first, then the stored messages as they were
    /// stored, byte for byte - but each answer to a call before <paramref name="shortenedBefore"/>, sent in its short form;
    /// a request that asks for JSON alone offers no tool and asks for a JSON object.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="settings">The client's settings: whether the model's reasoning is kept across turns, and the fields it adds.</param>
    /// <param name="shortenedBefore">The first message sent whole where the conversation is sent shortened; null where it is not.</param>
    internal static byte[] Body(ChatRequest request, ZaiApiSettings settings, int? shortenedBefore)
    {
        var thinking = new JsonObject { ["type"] = "enabled" };

        // Z.AI drops a turn's reasoning from what the model reads later unless asked to keep it; it is stored and sent either way.
        if (settings.PreserveThinking)
        {
            thinking["clear_thinking"] = false;
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            [ApiBody.Messages] = null,
            ["stream"] = true,
        };

        // A request that asks for JSON alone offers no tool: what it answers is the JSON, and no call.
        if (!request.JsonAlone)
        {
            body["tool_stream"] = true;

            // A conversation with no tools sends none; one with tools sends them as it began with them, as stored.
            if (!ApiBody.IsEmptyList(request.Tools))
            {
                body[ApiBody.Tools] = null;
            }
        }

        body["thinking"] = thinking;

        if (request.Effort is { Length: > 0 } effort)
        {
            body["reasoning_effort"] = effort;
        }

        body["max_tokens"] = request.MaxOutputTokens;

        if (request.JsonAlone)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }

        var system = new JsonObject { ["role"] = "system", ["content"] = request.System }.ToJsonString(ApiBody.Json);
        IReadOnlyList<string> messages = [system, .. request.Messages.Select((message, at) => at < shortenedBefore ? Short(message) : message)];

        return ApiBody.Write(body, request.Tools, messages, settings.RequestExtras);
    }

    /// <summary>The headers of <paramref name="request"/>: its key, as Z.AI takes one.</summary>
    /// <param name="request">The request.</param>
    internal static IReadOnlyList<KeyValuePair<string, string>> Headers(ChatRequest request)
    {
        var key = request.Key ?? throw new InvalidOperationException("A request of the glm client is signed with a key, and the loop gave it none.");

        return [new("Authorization", $"Bearer {key.Value}")];
    }

    /// <summary>A message as the conversation sent shortened sends it: an answer to a call in its short form, every other as it is.</summary>
    private static string Short(string message)
    {
        if (JsonNode.Parse(message) is not JsonObject node || node["role"]?.GetValue<string>() != "tool")
        {
            return message;
        }

        node["content"] = ShortAnswer;
        return node.ToJsonString(ApiBody.Json);
    }
}

/// <summary>What a glm client's entry says of its requests: whether the model's reasoning is kept across turns, the fields it adds.</summary>
/// <param name="PreserveThinking">Whether Z.AI is asked to keep the model's reasoning across turns (<c>thinking.clear_thinking: false</c>).</param>
/// <param name="RequestExtras">The fields merged into every request's body; null where it adds none.</param>
public sealed record ZaiApiSettings(bool PreserveThinking, JsonElement? RequestExtras)
{
    /// <summary>What a client's entry says, each key's default where it says nothing - or where the configuration declares no entry.</summary>
    /// <param name="entry">The client's entry; null where the configuration declares none.</param>
    public static ZaiApiSettings Of(AgentClientConfig? entry) => new(entry?.PreserveThinking ?? false, entry?.RequestExtras);
}
