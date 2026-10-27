using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;

namespace DssHarness.Core.Agents.ZaiApi;

/// <summary>
/// Z.AI's side of the loop the two API clients share (<see cref="ToolLoop"/>): the request every model is sent
/// (<see cref="ZaiApiRequest"/>), its answer's chunks read as they come into the loop's parts, the message it ends with
/// put back together - its text, its reasoning and each call's arguments as the API streamed them - every failure classed
/// (<see cref="ZaiApiFailures"/>), and the one rewrite of what is sent that keeps a conversation within its model's
/// context, for Z.AI clears nothing of it itself.
/// </summary>
/// <remarks>
/// <para>
/// The chunk is read as the live probe recorded it (2026-10-26): each a <c>choices[0].delta</c> of the message's role, a
/// piece of its <c>reasoning_content</c>, of its <c>content</c> or of its <c>tool_calls</c> - a call by its index, its id
/// and tool named as it begins - the last naming the <c>finish_reason</c> with the request's <c>usage</c>, and
/// <c>[DONE]</c> after it. A member the chunk adds is passed over; a call of a type other than a function's, which no
/// request declares, fails the answer. A conversation's messages are Z.AI's own: a user message of text, an answer's
/// calls answered by a tool message each, in order, and then each message of the engine's as a user message.
/// </para>
/// <para>
/// Once what the next request holds - what the model last read, and what it wrote where that goes on in the
/// conversation - passes <see cref="ShortenAt"/> of its context, the answers to calls older than the last
/// <see cref="WholeTurns"/> turns that called tools go out in their short form from the next request on - the point
/// stored with the conversation (<see cref="ChatPart.Shortened"/>), so every later request sends the same and the cache
/// misses once - and the reasoning as it was; past <see cref="FullAt"/> as measured on a request sent shortened, the
/// context is full.
/// </para>
/// </remarks>
/// <param name="connection">How the request is posted and its stream read.</param>
/// <param name="api">The provider's <c>api</c>, an absolute address.</param>
/// <param name="settings">The client's settings: whether the reasoning is kept across turns, the fields it adds.</param>
/// <param name="contextTokens">The most the model's context holds, as its entry says it: what the conversation is kept within.</param>
public sealed class ZaiApiProvider(ApiConnection connection, string api, ZaiApiSettings settings, long contextTokens) : IChatProvider
{
    /// <summary>The share of the model's context past which the conversation is sent shortened.</summary>
    public const double ShortenAt = 0.7;

    /// <summary>The share of the model's context past which a conversation sent shortened is full.</summary>
    public const double FullAt = 0.9;

    /// <summary>How many of the last turns that called tools have their answers sent whole once the conversation is sent shortened.</summary>
    public const int WholeTurns = 6;

    private readonly ApiConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly Uri _endpoint = ZaiApiRequest.Endpoint(api ?? throw new ArgumentNullException(nameof(api)));
    private readonly ZaiApiSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    // What the last answer had the model read and write where that goes on in the conversation: what the next request
    // holds at least - and whether it was measured on a request sent shortened, the only measure a full context is told by.
    private long? _held;
    private bool _heldShortened;

    /// <summary>What every request is billed by: its input, the input its cache served and its output - Z.AI caches by itself and bills no write.</summary>
    public AgentUsage Uses => AgentPrices.EveryRequestUses;

    /// <inheritdoc cref="IChatProvider.SendsSchema"/>
    public bool SendsSchema => ZaiApiRequest.HoldsToSchema;

    /// <inheritdoc/>
    public string Tools(IReadOnlyList<AgentToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var list = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.Schema),
            },
        })]);

        return list.ToJsonString(ApiBody.Json);
    }

    /// <inheritdoc/>
    public string UserMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new JsonObject { ["role"] = "user", ["content"] = text }.ToJsonString(ApiBody.Json);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Answers(ChatTurn turn, IReadOnlyList<ChatAnswer> answers, IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(messages);

        // A refused or failed call is answered with the text that says why: a tool message holds no mark of its own for it.
        return
        [
            .. answers.Select(answer => new JsonObject { ["role"] = "tool", ["tool_call_id"] = answer.Id, ["content"] = answer.Text }.ToJsonString(ApiBody.Json)),
            .. messages.Select(UserMessage),
        ];
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatPart> SendAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var shortened = request.ShortenedBefore;

        if (_held is { } held && contextTokens > 0)
        {
            if (_heldShortened && held >= FullAt * contextTokens)
            {
                yield return new ChatPart.Failed(new ChatFailure(
                    ChatFailureKind.ContextFull,
                    $"the conversation holds {held.ToString(CultureInfo.InvariantCulture)} tokens, past {Math.Round(FullAt * 100).ToString(CultureInfo.InvariantCulture)}% of the {contextTokens.ToString(CultureInfo.InvariantCulture)} the context of '{request.Model}' holds, and is sent shortened already"));
                yield break;
            }

            if (shortened is null && held >= ShortenAt * contextTokens && Boundary(request.Messages) is { } before)
            {
                shortened = before;
                yield return new ChatPart.Shortened(before);
            }
        }

        var post = new ApiPost(_endpoint, ZaiApiRequest.Headers(request), ZaiApiRequest.Body(request, _settings, shortened));
        var answer = new Answer();

        await foreach (var part in ApiAnswers.ReadAsync(_connection, post, answer.Read, cancellationToken).ConfigureAwait(false))
        {
            // An answer cut off at its length is weighed by what it read alone: the loop throws one away that called a tool
            // or wrote nothing, and one it keeps is weighed whole by the request that nudges it on.
            if (part is ChatPart.Answered { Turn: { Usage: var usage } turn })
            {
                _held = usage.InputTokens + usage.CacheReadTokens + (turn.Stop == ChatStop.CutOff ? 0 : usage.OutputTokens);
                _heldShortened = shortened is not null;
            }

            yield return part;
        }
    }

    /// <summary>
    /// The first message sent whole once the conversation is sent shortened: the beginning of the last
    /// <see cref="WholeTurns"/> turns that called tools; null where it holds no more turns than that, and nothing is
    /// older than them.
    /// </summary>
    private static int? Boundary(IReadOnlyList<string> messages)
    {
        var calling = messages
            .Select((message, at) => (Calls: JsonNode.Parse(message) is JsonObject node && node["role"]?.GetValue<string>() == "assistant" && node["tool_calls"] is JsonArray, At: at))
            .Where(each => each.Calls)
            .Select(each => each.At)
            .ToList();

        return calling.Count > WholeTurns ? calling[^WholeTurns] : null;
    }

    /// <summary>One request's answer as it is read: its text, reasoning and calls put back together, its usage, its ending.</summary>
    private sealed class Answer
    {
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        private readonly SortedDictionary<int, Call> _calls = new();
        private string? _model;
        private string? _finish;
        private long _prompt;
        private long _completion;
        private long _cached;

        /// <summary>The parts an event of the answer comes to; one that ends it - a turn or a failure - last.</summary>
        public IEnumerable<ChatPart> Read(ApiEvent happened) => ApiAnswers.Parts(happened, Item, ZaiApiFailures.Of);

        private static ChatPart.Failed Failed(ChatFailureKind kind, string message) => ApiAnswers.Failed(kind, message);

        private IEnumerable<ChatPart> Item(ApiEvent.Item item)
        {
            if (item.Data == "[DONE]")
            {
                return [Ended()];
            }

            JsonElement data;

            try
            {
                using var document = JsonDocument.Parse(item.Data);
                data = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return [Failed(ChatFailureKind.Transient, $"the answer's stream sent a chunk that is no JSON: {ex.Message}")];
            }

            if (ApiAnswers.Member(data, "error") is not null)
            {
                return [new ChatPart.Failed(ZaiApiFailures.OfStream(item.Data))];
            }

            List<ChatPart> parts = [];

            if (_model is null && ApiAnswers.Text(data, "model") is { Length: > 0 } model)
            {
                _model = model;
                parts.Add(new ChatPart.Began(model));
            }

            if (data.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];

                if (ApiAnswers.Member(choice, "delta") is { } delta && Delta(delta, parts) is { } failed)
                {
                    return [.. parts, failed];
                }

                _finish = ApiAnswers.Text(choice, "finish_reason") ?? _finish;
            }

            if (ApiAnswers.Member(data, "usage") is { } usage)
            {
                _prompt = ApiAnswers.Count(usage, "prompt_tokens") ?? _prompt;
                _completion = ApiAnswers.Count(usage, "completion_tokens") ?? _completion;
                _cached = ApiAnswers.Member(usage, "prompt_tokens_details") is { } details ? ApiAnswers.Count(details, "cached_tokens") ?? _cached : _cached;
                parts.Add(new ChatPart.Used(Usage()));
            }

            return parts;
        }

        /// <summary>Takes a delta's pieces, adding the parts they come to; a failure where it holds a call this tool cannot put back together.</summary>
        private ChatPart.Failed? Delta(JsonElement delta, List<ChatPart> parts)
        {
            if (ApiAnswers.Text(delta, "reasoning_content") is { Length: > 0 } reasoning)
            {
                _reasoning.Append(reasoning);
                parts.Add(new ChatPart.Thinking(reasoning));
            }

            if (ApiAnswers.Text(delta, "content") is { Length: > 0 } content)
            {
                _text.Append(content);
                parts.Add(new ChatPart.Text(content));
            }

            if (!delta.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var piece in calls.EnumerateArray())
            {
                if (!piece.TryGetProperty("index", out var index) || !index.TryGetInt32(out var at))
                {
                    return Failed(ChatFailureKind.Transient, "the answer's stream sent a piece of a call with no index");
                }

                if (ApiAnswers.Text(piece, "type") is { } type && type != "function")
                {
                    return Failed(ChatFailureKind.Fatal, $"the answer holds a call of type '{type}', which no request declares and this tool cannot put back together");
                }

                var function = ApiAnswers.Member(piece, "function") ?? default;

                if (!_calls.TryGetValue(at, out var call))
                {
                    if (ApiAnswers.Text(piece, "id") is not { Length: > 0 } id || ApiAnswers.Text(function, "name") is not { Length: > 0 } name)
                    {
                        return ApiAnswers.CallUnnamed();
                    }

                    _calls[at] = call = new Call(id, name);
                    parts.Add(new ChatPart.CallBegan(id, name));
                }

                if (ApiAnswers.Text(function, "arguments") is { Length: > 0 } arguments)
                {
                    call.Arguments.Append(arguments);
                    parts.Add(new ChatPart.CallInput(call.Id, arguments));
                }
            }

            return null;
        }

        private ChatPart Ended()
        {
            if (_model is not { } model)
            {
                return ApiAnswers.NeverBegan();
            }

            if (_finish == "network_error")
            {
                return Failed(ChatFailureKind.Transient, "the API ended the answer for a network error");
            }

            var calls = _calls.Values.Select(call => new ChatCall(call.Id, call.Name, call.Arguments.ToString())).ToList();

            // Each finish read as its word says it; a turn whose word and calls disagree - one that stopped holding calls,
            // one that ended to call tools holding none - is settled as every API's is (ApiAnswers.Settled).
            ChatStop? stop = _finish switch
            {
                "tool_calls" => ChatStop.Tools,
                "stop" => ChatStop.Done,
                "length" => ChatStop.CutOff,
                "sensitive" => ChatStop.Declined,
                "model_context_window_exceeded" => ChatStop.ContextFull,
                _ => null,
            };

            if (stop is not { } ended)
            {
                return ApiAnswers.EndedUnknown(_finish);
            }

            // The reasoning goes back with the message always: Z.AI reads it with the answers to the turn's calls.
            var message = new JsonObject { ["role"] = "assistant", ["content"] = _text.ToString(), ["reasoning_content"] = _reasoning.ToString() };

            if (calls.Count > 0)
            {
                message["tool_calls"] = new JsonArray([.. calls.Select(call => (JsonNode)new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Input },
                })]);
            }

            return ApiAnswers.Settled(new ChatTurn(
                ended,
                message.ToJsonString(ApiBody.Json),
                model,
                _text.ToString(),
                _reasoning.Length == 0 ? null : _reasoning.ToString(),
                calls,
                Usage(),
                _finish!));
        }

        /// <summary>What the request used: its prompt, the part of it the cache served apart, and its output - the reasoning among it.</summary>
        private AgentUsage Usage() => new(Math.Max(0, _prompt - _cached), _completion, _cached, 0, 0, 0);
    }

    /// <summary>One call of an answer as it is put back together: its id and tool as it began, its arguments as each piece added to them.</summary>
    private sealed class Call(string id, string name)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public StringBuilder Arguments { get; } = new();
    }
}
