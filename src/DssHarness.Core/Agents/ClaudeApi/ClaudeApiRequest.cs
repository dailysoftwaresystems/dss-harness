using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Configuration;
using DssHarness.Core.Schemas;

namespace DssHarness.Core.Agents.ClaudeApi;

/// <summary>
/// What the claude-api client sends with every request: one shape for every model (part 03, "How the two API clients
/// talk to their APIs") - the conversation's system prompt and tools as it began with them and every message as it was
/// stored, a thinking the model decides and shows summarized, the step's effort, the conversation's tool uses cleared by
/// the server as it grows, two cache marks, and the envelope an answer is held to where the step names an output - and the
/// one owner of what a client's <c>requestExtras</c> may add to it, and of the copy of a schema the API is sent.
/// </summary>
/// <remarks>
/// No sampling parameter, no prefill and no forced tool choice is ever sent, no thinking is turned off or given a budget,
/// and no tool of the provider's own is declared: the web is the harness's own (values 365 and 382). No request carries
/// <c>fallbacks</c> or a <c>server-side-fallback-</c> header: the engine's fallback is the only one a step has.
/// </remarks>
public static class ClaudeApiRequest
{
    /// <summary>The API version every request is sent on (<c>anthropic-version</c>; user, 2026-10-26).</summary>
    public const string Version = "2026-10-21";

    /// <summary>The path of the API's messages, under the provider's <c>api</c>.</summary>
    public const string MessagesPath = "v1/messages";

    /// <summary>The beta every request is sent under: the server clears the conversation's old tool uses as it grows.</summary>
    public const string ContextManagementBeta = "context-management-2025-06-27";

    /// <summary>The beta a request of a step with a model after its own is sent under: a declined request leaves that model a credit.</summary>
    public const string FallbackCreditBeta = "fallback-credit-2026-07-01";

    /// <summary>
    /// Whether the API holds every answer to the schema a request sends: structured outputs (<c>output_config.format</c>)
    /// write the answer to the schema's grammar.
    /// </summary>
    public const bool HoldsToSchema = true;

    /// <summary>
    /// What the loop is told when the API refuses a run's envelope as a format too large to compile: its requests go on
    /// with no format, the envelope said in words in the system prompt they send, and the gate holds each answer to it.
    /// </summary>
    public const string FormatInWords = "The Claude API compiles no grammar as large as the step's envelope: its requests are sent no format from now on, and the envelope in words in their system prompt; the harness's own check still holds every answer to it.";

    /// <summary>The most an array's <c>minItems</c> may say in a schema the API is sent: structured outputs take 0 or 1 alone.</summary>
    public const int MostLeastItems = 1;

    /// <summary>The beginning of the betas no request is sent under, and no client's <c>betas</c> may name: the API's own fallback.</summary>
    public const string ServerSideFallbackPrefix = "server-side-fallback-";

    /// <summary>The server's edit of the context every request asks for: old tool uses cleared, as its page sets them.</summary>
    public const string ClearToolUses = "clear_tool_uses_20250919";

    /// <summary>How long a cache mark keeps what it marks where the client says not: five minutes.</summary>
    public const string FiveMinutes = "5m";

    /// <summary>How long a cache mark keeps what it marks where the client says an hour.</summary>
    public const string AnHour = "1h";

    /// <summary>Every member of a request's body the tool sends itself or never sends, each by its path: what no <c>requestExtras</c> may set.</summary>
    public static readonly IReadOnlyList<OwnedMember> Owned =
    [
        .. new[]
        {
            new[] { "model" }, ["max_tokens"], ["stream"], ["system"], ["tools"], ["messages"], ["thinking", "type"], ["thinking", "display"],
            ["output_config", "effort"], ["output_config", "format"], ["context_management", "edits"], ["cache_control"], ["fallback_credit_token"],
        }.Select(path => new OwnedMember(path)),
        new OwnedMember(["fallbacks"], "no request carries fallbacks, for the engine's fallback is the only one a step has"),
    ];

    /// <summary>The cache lifetimes a client may name, in the order help names them.</summary>
    public static IReadOnlyList<string> CacheTtls { get; } = [FiveMinutes, AnHour];

    /// <summary>Where a provider's requests are posted: its <c>api</c>, with the messages' path under it.</summary>
    /// <param name="api">The provider's <c>api</c>, an absolute address.</param>
    public static Uri Endpoint(string api) => ApiPost.Under(api, MessagesPath);

    /// <summary>
    /// What is wrong with a claude-api client's <c>requestExtras</c>, each said in full (<see cref="ApiBody.ExtrasProblems"/>):
    /// an object, whose members set nothing the tool sends itself or never sends (<see cref="Owned"/>).
    /// </summary>
    /// <param name="extras">The client's <c>requestExtras</c>, as the configuration holds it.</param>
    /// <param name="where">Where it is, as a refusal names it: <c>agentClients 'claude-api' requestExtras</c>.</param>
    public static IEnumerable<string> ExtrasProblems(JsonElement extras, string where) => ApiBody.ExtrasProblems(extras, where, Owned);

    /// <summary>
    /// The copy of <paramref name="schema"/> the API is sent (user, 2026-10-18: "check here, strip on send"): each bound of
    /// an array structured outputs do not take - a <c>minItems</c> above <see cref="MostLeastItems"/>, and any
    /// <c>maxItems</c> - moved into that array's description in words, every other word of the schema as it is. The
    /// harness's own check still holds the answer to the whole (<see cref="StructuredOutputGate"/>).
    /// </summary>
    /// <param name="schema">The schema, as JSON, free of the subset's problems.</param>
    public static JsonObject Sent(string schema)
    {
        var sent = JsonNode.Parse(schema) as JsonObject ?? throw new ArgumentException("The schema is no JSON object.", nameof(schema));

        foreach (var each in JsonSchemaSubset.SchemasIn(sent))
        {
            var least = JsonSchemaSubset.Count(each["minItems"]) is > MostLeastItems and var some ? some : (int?)null;
            var most = JsonSchemaSubset.Count(each["maxItems"]);

            if (least is null && most is null)
            {
                continue;
            }

            if (least is not null)
            {
                each.Remove("minItems");
            }

            each.Remove("maxItems");
            var words = (least, most) switch
            {
                ({ } fewest, { } mostOf) => $"At least {fewest} and at most {mostOf} items.",
                ({ } fewest, null) => $"At least {fewest} items.",
                (null, { } mostOf) => $"At most {mostOf} item{(mostOf == 1 ? string.Empty : "s")}.",
                _ => throw new InvalidOperationException("No bound was moved."),
            };

            each["description"] = each["description"] is JsonValue said && said.GetValue<string>() is { Length: > 0 } text ? $"{text} {words}" : words;
        }

        return sent;
    }

    /// <summary>
    /// <paramref name="request"/> as it is sent where the API refused its envelope as a format too large to compile: no
    /// format, and the envelope in words after its system prompt - never in a message, for the stored ones are sent as
    /// they were stored.
    /// </summary>
    /// <param name="request">The request, holding the envelope.</param>
    public static ChatRequest InWords(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Schema is { } schema ? request with { Schema = null, System = $"{request.System}\n\n{AgentEnvelope.InWords(schema)}" } : request;
    }

    /// <summary>The body of <paramref name="request"/>, as JSON: its stored tools and messages as they were stored, byte for byte.</summary>
    /// <param name="request">The request.</param>
    /// <param name="settings">The client's settings: its cache's lifetime and the fields it adds.</param>
    internal static byte[] Body(ChatRequest request, ClaudeApiSettings settings)
    {
        var cache = new JsonObject { ["type"] = "ephemeral", ["ttl"] = settings.CacheTtl };
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = true,
            ["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.System, ["cache_control"] = cache.DeepClone() }),
        };

        // A conversation with no tools sends none; one with tools sends them as it began with them, as stored.
        if (!ApiBody.IsEmptyList(request.Tools))
        {
            body[ApiBody.Tools] = null;
        }

        body[ApiBody.Messages] = null;
        body["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };

        if (request.Effort is { Length: > 0 } effort)
        {
            body["output_config"] = new JsonObject { ["effort"] = effort };
        }

        // Every request of a step that names an output is held to its envelope, the handoff's among them.
        if (request.Schema is { } schema)
        {
            var config = body["output_config"] as JsonObject ?? [];
            config["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = Sent(schema) };
            body["output_config"] = config;
        }

        body["context_management"] = new JsonObject { ["edits"] = new JsonArray(new JsonObject { ["type"] = ClearToolUses }) };
        body["cache_control"] = cache;

        return ApiBody.Write(body, request.Tools, request.Messages, settings.RequestExtras);
    }

    /// <summary>The headers of <paramref name="request"/>: its key, the version, and every beta it is sent under, once each.</summary>
    /// <param name="request">The request.</param>
    /// <param name="settings">The client's settings: the betas it adds.</param>
    internal static IReadOnlyList<KeyValuePair<string, string>> Headers(ChatRequest request, ClaudeApiSettings settings)
    {
        var key = request.Key ?? throw new InvalidOperationException("A request of the claude-api client is signed with a key, and the loop gave it none.");
        IEnumerable<string> betas = [ContextManagementBeta, .. request.Credit ? [FallbackCreditBeta] : Array.Empty<string>(), .. settings.Betas];

        return
        [
            new("x-api-key", key.Value),
            new("anthropic-version", Version),
            new("anthropic-beta", string.Join(",", betas.Distinct(StringComparer.Ordinal))),
        ];
    }
}

/// <summary>What a claude-api client's entry says of its requests: its cache's lifetime, the fields it adds, the betas it adds.</summary>
/// <param name="CacheTtl">How long each cache mark keeps what it marks: <see cref="ClaudeApiRequest.FiveMinutes"/> or <see cref="ClaudeApiRequest.AnHour"/>.</param>
/// <param name="RequestExtras">The fields merged into every request's body; null where it adds none.</param>
/// <param name="Betas">The betas sent beside the tool's own.</param>
public sealed record ClaudeApiSettings(string CacheTtl, JsonElement? RequestExtras, IReadOnlyList<string> Betas)
{
    /// <summary>What a client's entry says, each key's default where it says nothing - or where the configuration declares no entry.</summary>
    /// <param name="entry">The client's entry; null where the configuration declares none.</param>
    public static ClaudeApiSettings Of(AgentClientConfig? entry)
        => new(entry?.CacheTtl ?? ClaudeApiRequest.FiveMinutes, entry?.RequestExtras, entry?.Betas ?? []);
}
