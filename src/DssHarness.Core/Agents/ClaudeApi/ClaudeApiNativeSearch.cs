using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Web.Search;

namespace DssHarness.Core.Agents.ClaudeApi;

/// <summary>
/// The Claude API's own web search, which a side-task's <c>web_search</c> asks as its <c>native</c> part: one request of
/// the harness's own to the API, signed with the side-task's key, whose only tool is Anthropic's web search - no server
/// tool declared in any conversation, for this request is no part of one. Its results are read from the answer's
/// <c>web_search_tool_result</c> blocks; what it used - its tokens, the search results billed as input among them, and its
/// searches - from the answer's usage, by the one reader of it (<see cref="ClaudeApiUsage"/>), for the run to price as it
/// prices every request. An answer that is no search's - a refusal, a request that never reached the API, a stream cut,
/// an error, a search the tool itself says failed - is a part that failed, saying why, never one with no results, and
/// the failure it met is given back for the gate to read. Which model and key it asks, the gate's lane it goes through
/// and the part of the run's usage it is counted to are the asking loop's, as the run stands when the agent asks.
/// </summary>
/// <param name="connection">How the request is posted and its answer read.</param>
/// <param name="api">The provider's address.</param>
public sealed class ClaudeApiNativeSearch(ApiConnection connection, string api) : IAgentNativeSearch
{
    /// <summary>The server tool the request declares alone, as the API names it.</summary>
    public const string ToolType = "web_search_20250305";

    /// <summary>How the part names its source, beside the configured sources' own names.</summary>
    public const string SourceName = "native";

    private readonly ApiConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly Uri _endpoint = ClaudeApiRequest.Endpoint(api ?? throw new ArgumentNullException(nameof(api)));

    /// <inheritdoc/>
    string IAgentNativeSearch.Source => SourceName;

    /// <inheritdoc/>
    public long InputTokens(string query, string model) => Codebase.EstimatedTokens.Of(Body(query, model).ToJsonString());

    /// <inheritdoc/>
    public async Task<AgentNativeSearched> SearchAsync(string query, string model, AgentKey key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(key);

        var post = new ApiPost(
            _endpoint,
            [
                new("x-api-key", key.Value),
                new("anthropic-version", ClaudeApiRequest.Version),
            ],
            JsonSerializer.SerializeToUtf8Bytes(Body(query, model), ApiBody.Json));

        var results = new List<WebSearchResult>();
        var used = new ClaudeApiUsage(cacheTtl: null);
        ChatFailure? failure = null;

        await foreach (var happened in _connection.PostAsync(post, cancellationToken).ConfigureAwait(false))
        {
            foreach (var part in ApiAnswers.Parts(happened, item => Read(item, model, results, used), refused => ClaudeApiFailures.Of(refused, first: true, model)))
            {
                if (part is ChatPart.Failed failed)
                {
                    failure ??= failed.Failure;
                }
            }
        }

        var answer = failure is { } why ? WebSearchAnswer.Failed(why.Message) : WebSearchAnswer.Read(results);

        return new AgentNativeSearched(new WebSearchPart(SourceName, SourceName, Version: 1, query, answer, Withheld: 0, Cached: false, KeyMissing: null), used.Used(), failure);
    }

    /// <summary>The request's body: <paramref name="query"/> asked of <paramref name="model"/>, the provider's web search its only tool.</summary>
    private static JsonObject Body(string query, string model)
        => new()
        {
            ["model"] = model,
            ["max_tokens"] = 64,
            ["stream"] = true,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = ToolType, ["name"] = "web_search", ["max_uses"] = 1 }),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"Search the web for this, and answer with nothing but what the search returned:\n\n{query}",
            }),
        };

    /// <summary>
    /// One event of the answer, read for what the search needs: each result of a <c>web_search_tool_result</c> block - or
    /// the failure its content holds where the tool failed - and each usage the answer carries. An error the stream sends
    /// is the part's failure, read as every error of the API's is.
    /// </summary>
    private static IEnumerable<ChatPart> Read(ApiEvent.Item item, string model, List<WebSearchResult> results, ClaudeApiUsage used)
    {
        if (item.Type is not ("message_start" or "content_block_start" or "message_delta" or "error"))
        {
            return [];
        }

        if (item.Type == "error")
        {
            return [new ChatPart.Failed(ClaudeApiFailures.OfStream(item.Data, first: true, model))];
        }

        JsonElement data;

        try
        {
            using var document = JsonDocument.Parse(item.Data);
            data = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return [ApiAnswers.Failed(ChatFailureKind.Transient, $"the answer's stream sent a '{item.Type}' event that is no JSON: {ex.Message}")];
        }

        switch (item.Type)
        {
            case "message_start" when data.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var began):
                used.Take(began);
                break;

            case "message_delta" when data.TryGetProperty("usage", out var usage):
                used.Take(usage);
                break;

            case "content_block_start" when ApiAnswers.Member(data, "content_block") is { } block && ApiAnswers.Text(block, "type") == "web_search_tool_result":
                if (block.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var one in content.EnumerateArray().Where(one => one.ValueKind == JsonValueKind.Object))
                    {
                        if (WebSearchResult.Of(ApiAnswers.Text(one, "title"), ApiAnswers.Text(one, "url"), ApiAnswers.Text(one, "text"), relativeTo: null) is { } result)
                        {
                            results.Add(result);
                        }
                    }
                }
                else if (ApiAnswers.Member(block, "content") is { } error)
                {
                    // The tool's own failure: the search ran and could not answer, its error code said.
                    return [ApiAnswers.Failed(ChatFailureKind.Fatal, $"the provider's own search failed: {ApiAnswers.Text(error, "error_code") ?? ApiAnswers.Text(error, "type") ?? "it gave no reason"}")];
                }

                break;
        }

        return [];
    }
}
