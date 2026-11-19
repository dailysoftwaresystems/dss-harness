using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Json;
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
/// <remarks>
/// The request asks for as much of an answer as the loop's own first request does - a quarter of the most a reply of the
/// model holds (<see cref="ToolLoop.FirstMaxTokens"/>) - and an answer cut off before its search returned anything is
/// asked again with twice as much, up to the most, as the loop asks a turn cut off before it wrote anything; what every
/// request of it used is counted. It names no number of searches: the model searches as often as the query needs, and
/// each search is counted in what the request used, as the provider bills it.
/// </remarks>
/// <param name="connection">How the request is posted and its answer read.</param>
/// <param name="api">The provider's address.</param>
/// <param name="records">Where the body of a request the API refuses is kept whole: the step's records; null where none are kept, and it is held.</param>
/// <param name="mostOutput">
/// Each model the run may ask the search of, by its code as the loop asks it, with the most a reply of it holds, as its
/// entry says it.
/// </param>
public sealed class ClaudeApiNativeSearch(ApiConnection connection, string api, ApiRecords? records, IReadOnlyDictionary<string, long> mostOutput) : IAgentNativeSearch
{
    /// <summary>The server tool the request declares alone, as the API names it.</summary>
    public const string ToolType = "web_search_20250305";

    /// <summary>How the part names its source, beside the configured sources' own names.</summary>
    public const string SourceName = "native";

    /// <summary>What the request asks of the model: the search alone, for its answer's text is never read.</summary>
    public const string Asked = "Search the web for this. Only the search's own results are read, never your answer, so write nothing after searching:";

    private readonly ApiConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly Uri _endpoint = ClaudeApiRequest.Endpoint(api ?? throw new ArgumentNullException(nameof(api)));
    private readonly IReadOnlyDictionary<string, long> _mostOutput = mostOutput ?? throw new ArgumentNullException(nameof(mostOutput));

    /// <inheritdoc/>
    string IAgentNativeSearch.Source => SourceName;

    /// <inheritdoc/>
    public long InputTokens(string query, string model) => Codebase.EstimatedTokens.Of(Body(query, model, ToolLoop.FirstMaxTokens(Most(model))).ToJsonString(ApiBody.Json));

    /// <inheritdoc/>
    public async Task<AgentNativeSearched> SearchAsync(string query, string model, AgentKey key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(key);

        var most = Most(model);
        var asked = ToolLoop.FirstMaxTokens(most);
        var used = AgentUsage.None;

        while (true)
        {
            var answer = new Answer(model);
            var post = new ApiPost(
                _endpoint,
                [
                    new("x-api-key", key.Value),
                    new("anthropic-version", ClaudeApiRequest.Version),
                ],
                JsonSerializer.SerializeToUtf8Bytes(Body(query, model, asked), ApiBody.Json),
                records);

            await foreach (var happened in _connection.PostAsync(post, cancellationToken).ConfigureAwait(false))
            {
                answer.Read(happened);
            }

            used = used.Plus(answer.Used.Used());

            // What a search returned stands, whatever the answer met after it: each result came whole in its own block.
            if (answer.Searched)
            {
                return new AgentNativeSearched(Part(query, WebSearchAnswer.Read(answer.Results)), used, answer.Failure);
            }

            if (answer.Failure is { } why)
            {
                return new AgentNativeSearched(Part(query, WebSearchAnswer.Failed(why.Message)), used, why);
            }

            // Ended before its search returned anything: cut off at its length is asked again with room for more, up to
            // the most; any other end says the search was never made.
            if (answer.Stop != "max_tokens" || asked >= most)
            {
                var ended = answer.Stop == "max_tokens"
                    ? $"the answer was cut off at {asked.ToString("N0", CultureInfo.InvariantCulture)} tokens before its search returned anything, the most a reply of '{model}' holds"
                    : $"the answer ended for '{answer.Stop ?? "no reason"}' before its search returned anything";

                return new AgentNativeSearched(Part(query, WebSearchAnswer.Failed($"the provider's own search was not made: {ended}")), used);
            }

            asked = Math.Min(most, asked * 2);
        }
    }

    /// <summary>The most a reply of <paramref name="model"/> holds, as its entry says it.</summary>
    /// <exception cref="InvalidOperationException">The run may not ask <paramref name="model"/>: the loop asked it of a model none of its entries name.</exception>
    private long Most(string model)
        => _mostOutput.TryGetValue(model, out var most)
            ? most
            : throw new InvalidOperationException($"The native search was asked of '{model}', which none of the run's entries name.");

    /// <summary>The part a search of <paramref name="query"/> came to.</summary>
    private static WebSearchPart Part(string query, WebSearchAnswer answer)
        => new(SourceName, SourceName, Version: 1, query, answer, Withheld: 0, Cached: false, KeyMissing: null);

    /// <summary>The request's body: <paramref name="query"/> asked of <paramref name="model"/>, the provider's web search its only tool, as much of an answer as <paramref name="maxTokens"/> asked.</summary>
    private static JsonObject Body(string query, string model, long maxTokens)
        => new()
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
            ["tools"] = new JsonArray(new JsonObject { ["type"] = ToolType, ["name"] = "web_search" }),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"{Asked}\n\n{query}",
            }),
        };

    /// <summary>
    /// One request's answer, read for what the search needs: each result of a <c>web_search_tool_result</c> block - or the
    /// failure its content holds where the tool failed - each usage the answer carries, and why it ended. An error the
    /// stream sends is the answer's failure, read as every error of the API's is, and so is a refusal, a request that never
    /// reached the API and a stream cut.
    /// </summary>
    private sealed class Answer(string model)
    {
        /// <summary>Each result the search returned, in order.</summary>
        public List<WebSearchResult> Results { get; } = [];

        /// <summary>What the request used.</summary>
        public ClaudeApiUsage Used { get; } = new(cacheTtl: null);

        /// <summary>Whether a search returned: a <c>web_search_tool_result</c> block came, with results or none.</summary>
        public bool Searched { get; private set; }

        /// <summary>The API's word for why the answer ended; null where it said none.</summary>
        public string? Stop { get; private set; }

        /// <summary>The first failure the answer met; null where it met none.</summary>
        public ChatFailure? Failure { get; private set; }

        /// <summary>Takes one event of the answer.</summary>
        public void Read(ApiEvent happened)
        {
            foreach (var part in ApiAnswers.Parts(happened, Item, refused => ClaudeApiFailures.Of(refused, first: true, model)))
            {
                if (part is ChatPart.Failed failed)
                {
                    Failure ??= failed.Failure;
                }
            }
        }

        private IEnumerable<ChatPart> Item(ApiEvent.Item item)
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
                using var document = JsonDepth.ParseDocument(item.Data);
                data = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return [ApiAnswers.Failed(ChatFailureKind.Transient, $"the answer's stream sent a '{item.Type}' event that is no JSON: {ex.Message}")];
            }

            switch (item.Type)
            {
                case "message_start" when data.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var began):
                    Used.Take(began);
                    break;

                case "message_delta":
                    if (data.TryGetProperty("usage", out var usage))
                    {
                        Used.Take(usage);
                    }

                    Stop = ApiAnswers.Member(data, "delta") is { } delta ? ApiAnswers.Text(delta, "stop_reason") ?? Stop : Stop;
                    break;

                case "content_block_start" when ApiAnswers.Member(data, "content_block") is { } block && ApiAnswers.Text(block, "type") == "web_search_tool_result":
                    if (block.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        Searched = true;

                        foreach (var one in content.EnumerateArray().Where(one => one.ValueKind == JsonValueKind.Object))
                        {
                            if (WebSearchResult.Of(ApiAnswers.Text(one, "title"), ApiAnswers.Text(one, "url"), ApiAnswers.Text(one, "text"), relativeTo: null) is { } result)
                            {
                                Results.Add(result);
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
}
