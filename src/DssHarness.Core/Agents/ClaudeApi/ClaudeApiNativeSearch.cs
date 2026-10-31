using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Execution;
using DssHarness.Core.Web.Search;

namespace DssHarness.Core.Agents.ClaudeApi;

/// <summary>
/// The Claude API's own web search, which a side-task's <c>web_search</c> asks as its <c>native</c> part ("open web
/// search" as the user ruled it, value 395): one request of the harness's own to the API, signed with the side-task's
/// key, whose only tool is Anthropic's web search (the design value 365 had) - no server tool declared in any
/// conversation, for this request is no part of one. Its results are read from the answer's
/// <c>web_search_tool_result</c> blocks and its requests from the answer's usage, priced at the provider's
/// <c>pricePerThousandSearches</c>.
/// </summary>
/// <param name="connection">How the request is posted and its answer read.</param>
/// <param name="api">The provider's address.</param>
/// <param name="model">The model the search runs on, as the side-task resolved it.</param>
/// <param name="key">The key the request is signed with, of the side-task's entries.</param>
public sealed class ClaudeApiNativeSearch(ApiConnection connection, string api, string model, AgentKey key) : IAgentNativeSearch
{
    /// <summary>The server tool the request declares alone, as the API names it.</summary>
    public const string ToolType = "web_search_20250305";

    /// <summary>How the part names its source, beside the configured sources' own names.</summary>
    public const string SourceName = "native";

    private readonly ApiConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly Uri _endpoint = ClaudeApiRequest.Endpoint(api ?? throw new ArgumentNullException(nameof(api)));
    private readonly string _model = model ?? throw new ArgumentNullException(nameof(model));
    private readonly AgentKey _key = key ?? throw new ArgumentNullException(nameof(key));

    /// <inheritdoc/>
    string IAgentNativeSearch.Source => SourceName;

    /// <inheritdoc/>
    public async Task<(WebSearchPart Part, long Requests)> SearchAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 64,
            ["stream"] = true,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = ToolType, ["name"] = "web_search", ["max_uses"] = 1 }),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"Search the web for this, and answer with nothing but what the search returned:\n\n{query}",
            }),
        };

        var post = new ApiPost(
            _endpoint,
            [
                new("x-api-key", _key.Value),
                new("anthropic-version", ClaudeApiRequest.Version),
            ],
            JsonSerializer.SerializeToUtf8Bytes(body, ApiBody.Json));

        var results = new List<WebSearchResult>();
        string? failure = null;
        long requests = 0;

        await foreach (var @event in _connection.PostAsync(post, cancellationToken).ConfigureAwait(false))
        {
            if (@event is not ApiEvent.Item { Type: "content_block_start" or "message_delta" or "error" } item)
            {
                continue;
            }

            JsonObject? read = null;

            try
            {
                read = JsonNode.Parse(item.Data) as JsonObject;
            }
            catch (JsonException)
            {
                // An event this reader does not read: the answer's own events name what it holds.
            }

            if (read is null)
            {
                continue;
            }

            if (item.Type == "content_block_start"
                && read["content_block"]?["type"]?.GetValue<string>() == "web_search_tool_result"
                && read["content_block"]?["content"] is JsonArray found)
            {
                foreach (var one in found.OfType<JsonObject>())
                {
                    var title = one["title"] is JsonValue named ? named.GetValue<string>() : null;
                    var url = one["url"] is JsonValue address ? address.GetValue<string>() : null;
                    var text = one["text"] is JsonValue said ? said.GetValue<string>() : null;

                    if (WebSearchResult.Of(title, url, text, relativeTo: null) is { } result)
                    {
                        results.Add(result);
                    }
                }
            }
            else if (item.Type == "message_delta"
                && read["usage"]?["server_tool_use"]?["web_search"]?["requests"] is JsonValue counted)
            {
                requests = counted.GetValue<long>();
            }
            else if (item.Type == "error")
            {
                failure = read["error"]?["message"] is JsonValue said ? said.GetValue<string>() : "the provider's own search failed";
            }
        }

        return failure is { } why
            ? (new WebSearchPart(SourceName, SourceName, Version: 1, query, WebSearchAnswer.Failed(why), Withheld: 0, Cached: false, KeyMissing: null), requests)
            : (new WebSearchPart(SourceName, SourceName, Version: 1, query, WebSearchAnswer.Read(results), Withheld: 0, Cached: false, KeyMissing: null), requests);
    }
}
