using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;

namespace DssHarness.Core.Agents.ClaudeApi;

/// <summary>
/// The Claude API's side of the loop the two API clients share (<see cref="ToolLoop"/>): the request every model is sent
/// (<see cref="ClaudeApiRequest"/>), its answer's stream read as it comes into the loop's parts, the message it ends with
/// put back together block by block - a thinking block with its signature, a call with its input as the API produced it -
/// and every failure classed (<see cref="ClaudeApiFailures"/>).
/// </summary>
/// <remarks>
/// A block or a delta of a type this tool does not know cannot be put back together, and fails the answer naming it,
/// never dropped and never stored in part; an event of a type it does not know is none of the answer's, and is passed
/// over. A conversation's messages are the API's own: a user message of text blocks, an answer's calls answered by one
/// user message holding their results in order and then each message of the engine's as a text block after them.
/// </remarks>
/// <param name="connection">How the request is posted and its stream read.</param>
/// <param name="api">The provider's <c>api</c>, an absolute address.</param>
/// <param name="settings">The client's settings: its cache's lifetime, the fields and betas it adds.</param>
public sealed class ClaudeApiProvider(ApiConnection connection, string api, ClaudeApiSettings settings) : IChatProvider
{
    private readonly ApiConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly Uri _endpoint = ClaudeApiRequest.Endpoint(api ?? throw new ArgumentNullException(nameof(api)));
    private readonly ClaudeApiSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// What every request is billed by: input, cache read and output, and the cache write of the client's lifetime, for
    /// every request marks what it sends to be cached.
    /// </summary>
    public AgentUsage Uses
        => _settings.CacheTtl == ClaudeApiRequest.AnHour
            ? AgentPrices.EveryRequestUses with { CacheWrite1hTokens = 1 }
            : AgentPrices.EveryRequestUses with { CacheWriteTokens = 1 };

    /// <inheritdoc/>
    public string Tools(IReadOnlyList<AgentToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        // Each call's input streams as it is written; the harness holds every input to its tool's schema itself.
        var list = new JsonArray([.. tools.Select(tool => (JsonNode)new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["input_schema"] = JsonNode.Parse(tool.Schema),
            ["eager_input_streaming"] = true,
        })]);

        return list.ToJsonString(ClaudeApiRequest.Json);
    }

    /// <inheritdoc/>
    public string UserMessage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new JsonObject { ["role"] = "user", ["content"] = new JsonArray(TextBlock(text)) }.ToJsonString(ClaudeApiRequest.Json);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> Answers(ChatTurn turn, IReadOnlyList<ChatAnswer> answers, IReadOnlyList<string> messages)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(messages);

        var content = new JsonArray();

        foreach (var answer in answers)
        {
            var result = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = answer.Id };

            if (answer.Text.Length > 0)
            {
                result["content"] = answer.Text;
            }

            if (!answer.Ok)
            {
                result["is_error"] = true;
            }

            content.Add(result);
        }

        foreach (var message in messages)
        {
            content.Add(TextBlock(message));
        }

        return content.Count == 0 ? [] : [new JsonObject { ["role"] = "user", ["content"] = content }.ToJsonString(ClaudeApiRequest.Json)];
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatPart> SendAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var post = new ApiPost(_endpoint, ClaudeApiRequest.Headers(request, _settings), ClaudeApiRequest.Body(request, _settings));
        var answer = new Answer(request, _settings);

        await foreach (var happened in _connection.PostAsync(post, cancellationToken).ConfigureAwait(false))
        {
            var heard = false;

            foreach (var part in answer.Read(happened))
            {
                heard = true;
                yield return part;

                if (part is ChatPart.Answered or ChatPart.Failed)
                {
                    yield break;
                }
            }

            // An event of no part - a ping, a block's end - is the answer not silent all the same.
            if (!heard)
            {
                yield return new ChatPart.Heard();
            }
        }

        yield return new ChatPart.Failed(new ChatFailure(ChatFailureKind.Transient, "the answer's stream ended before its message did"));
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    /// <summary>One request's answer as it is read: its blocks put back together, its usage, its ending.</summary>
    private sealed class Answer(ChatRequest request, ClaudeApiSettings settings)
    {
        private readonly List<Block> _blocks = [];
        private readonly bool _first = request.Messages.Count == 1;
        private string? _model;
        private string? _stopReason;
        private JsonElement? _stopDetails;
        private long _input;
        private long _output;
        private long _cacheRead;
        private long _cacheWrite;
        private long? _cacheWrite5m;
        private long? _cacheWrite1h;
        private long _searches;

        /// <summary>The parts an event of the answer comes to; one that ends it - a turn or a failure - last.</summary>
        public IEnumerable<ChatPart> Read(ApiEvent happened)
            => happened switch
            {
                ApiEvent.Item item => Item(item),
                ApiEvent.Refused refused => [new ChatPart.Failed(ClaudeApiFailures.Of(refused, _first, request.Model))],
                ApiEvent.Unreached unreached => [new ChatPart.Failed(ClaudeApiFailures.Unreached(unreached.Why))],
                ApiEvent.Cut cut => [new ChatPart.Failed(ClaudeApiFailures.Cut(cut.Why))],
                _ => throw new InvalidOperationException($"'{happened.GetType().Name}' is no event a connection yields."),
            };

        private IEnumerable<ChatPart> Item(ApiEvent.Item item)
        {
            if (item.Type is "ping")
            {
                return [];
            }

            JsonElement data;

            try
            {
                using var document = JsonDocument.Parse(item.Data);
                data = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return [Failed(ChatFailureKind.Transient, $"the answer's stream sent a '{item.Type}' event that is no JSON: {ex.Message}")];
            }

            return item.Type switch
            {
                "message_start" => Started(data),
                "content_block_start" => BlockStarted(data),
                "content_block_delta" => Delta(data),
                "content_block_stop" => [],
                "message_delta" => MessageDelta(data),
                "message_stop" => [Ended()],
                "error" => [new ChatPart.Failed(ClaudeApiFailures.OfStream(item.Data, _first, request.Model))],
                _ => [],
            };
        }

        private IEnumerable<ChatPart> Started(JsonElement data)
        {
            if (!data.TryGetProperty("message", out var message) || Text(message, "model") is not { Length: > 0 } model)
            {
                return [Failed(ChatFailureKind.Transient, "the answer's stream began with no model named")];
            }

            _model = model;

            if (message.TryGetProperty("usage", out var usage))
            {
                Used(usage);
            }

            // What it has read so far first: an answer of another model ends the read at its beginning, and is billed all the same.
            return [new ChatPart.Used(Usage()), new ChatPart.Began(model)];
        }

        private IEnumerable<ChatPart> BlockStarted(JsonElement data)
        {
            if (!data.TryGetProperty("content_block", out var start) || start.ValueKind != JsonValueKind.Object || Text(start, "type") is not { } type)
            {
                return [Failed(ChatFailureKind.Transient, "the answer's stream began a block with no type")];
            }

            if (!Block.Known.Contains(type, StringComparer.Ordinal))
            {
                return [Failed(ChatFailureKind.Fatal, $"the answer holds a block of type '{type}', which this tool does not know and cannot put back together; a block is never dropped or stored in part")];
            }

            // Blocks come in the order they stand, each numbered by its place.
            if (Index(data) != _blocks.Count)
            {
                return [Failed(ChatFailureKind.Transient, $"the answer's stream began block {Index(data)} where block {_blocks.Count} stood next")];
            }

            var block = new Block(type, JsonNode.Parse(start.GetRawText())!.AsObject());

            if (type == Block.ToolUse && (block.Id is not { Length: > 0 } || block.Name is not { Length: > 0 }))
            {
                return [Failed(ChatFailureKind.Transient, "the answer's stream began a call with no id or no tool named")];
            }

            _blocks.Add(block);

            return type == Block.ToolUse ? [new ChatPart.CallBegan(block.Id!, block.Name!)] : [];
        }

        private IEnumerable<ChatPart> Delta(JsonElement data)
        {
            if (Index(data) is not { } at || at < 0 || at >= _blocks.Count || !data.TryGetProperty("delta", out var delta) || Text(delta, "type") is not { } type)
            {
                return [Failed(ChatFailureKind.Transient, "the answer's stream sent a delta of no block it began")];
            }

            var block = _blocks[at];
            var fragment = type switch
            {
                "text_delta" when block.Type == Block.Text => Text(delta, "text"),
                "thinking_delta" when block.Type == Block.Thinking => Text(delta, "thinking"),
                "signature_delta" when block.Type == Block.Thinking => Text(delta, "signature"),
                "input_json_delta" when block.Type == Block.ToolUse => Text(delta, "partial_json"),
                _ => null,
            };

            if (fragment is null)
            {
                return [Failed(ChatFailureKind.Fatal, $"the answer holds a delta of type '{type}' in a block of type '{block.Type}', which this tool does not know and cannot put back together")];
            }

            block.Add(type, fragment);

            return type switch
            {
                "text_delta" when fragment.Length > 0 => [new ChatPart.Text(fragment)],
                "thinking_delta" when fragment.Length > 0 => [new ChatPart.Thinking(fragment)],
                "input_json_delta" when fragment.Length > 0 => [new ChatPart.CallInput(block.Id!, fragment)],
                _ => [],
            };
        }

        private IEnumerable<ChatPart> MessageDelta(JsonElement data)
        {
            var delta = data.TryGetProperty("delta", out var given) && given.ValueKind == JsonValueKind.Object ? given : default;

            _stopReason = Text(delta, "stop_reason") ?? _stopReason;

            // Why a request ended beyond its reason - a declined one's category and credit - and what the server cleared of
            // the conversation's context, wherever the event puts them: beside the reason, or beside the delta.
            if ((Member(delta, "stop_details") ?? Member(data, "stop_details")) is { } details)
            {
                _stopDetails = details.Clone();
            }

            List<ChatPart> parts = [];

            if (data.TryGetProperty("usage", out var usage))
            {
                Used(usage);
                parts.Add(new ChatPart.Used(Usage()));
            }

            // What the server cleared is said, and changes nothing of the stored history.
            if ((Member(data, "context_management") ?? Member(delta, "context_management")) is { } context
                && context.TryGetProperty("applied_edits", out var edits)
                && edits.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(edits.EnumerateArray().Select(edit => new ChatPart.Notice($"The provider edited the conversation's context for this request: {edit.GetRawText()}")));
            }

            return parts;
        }

        private static ChatPart.Failed Failed(ChatFailureKind kind, string message) => new(new ChatFailure(kind, message));

        private ChatPart Ended()
        {
            if (_model is not { } model)
            {
                return Failed(ChatFailureKind.Transient, "the answer's stream ended a message it never began");
            }

            ChatStop? stop = _stopReason switch
            {
                "end_turn" or "stop_sequence" => ChatStop.Done,
                "tool_use" => ChatStop.Tools,
                "max_tokens" => ChatStop.CutOff,
                "pause_turn" => ChatStop.Paused,
                "refusal" => ChatStop.Declined,
                "model_context_window_exceeded" => ChatStop.ContextFull,
                _ => null,
            };

            if (stop is not { } ended)
            {
                return Failed(ChatFailureKind.Fatal, $"the answer ended for '{_stopReason ?? "no reason"}', which this tool does not know");
            }

            var calls = _blocks.Where(block => block.Type == Block.ToolUse).Select(block => new ChatCall(block.Id ?? string.Empty, block.Name ?? string.Empty, block.Input)).ToList();

            // Calls the loop runs are whole: an input the stream left no JSON object is the answer failing part way, asked
            // again - a turn cut off at its length is thrown away by the loop itself, its calls never run.
            if (ended == ChatStop.Tools && calls.FirstOrDefault(call => !IsObject(call.Input)) is { } broken)
            {
                return Failed(ChatFailureKind.Transient, $"the answer's call {broken.Id} of '{broken.Name}' came with an input that is no JSON object");
            }

            var message = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray([.. _blocks.Select(block => (JsonNode)block.Whole())]) };
            var thinking = _blocks.Where(block => block.Type == Block.Thinking).ToList();
            var category = ended == ChatStop.Declined && _stopDetails is { } declined ? Text(declined, "category") : null;

            return new ChatPart.Answered(new ChatTurn(
                ended,
                message.ToJsonString(ClaudeApiRequest.Json),
                model,
                string.Concat(_blocks.Where(block => block.Type == Block.Text).Select(block => block.Written)),
                thinking.Count == 0 ? null : string.Concat(thinking.Select(block => block.Written)),
                calls,
                Usage(),
                _stopReason!,
                category,
                ended == ChatStop.Declined ? Credit() : null));
        }

        /// <summary>The credit a declined answer left, where it names a token.</summary>
        private AgentFallbackCredit? Credit()
            => _stopDetails is { } details && Text(details, "fallback_credit_token") is { Length: > 0 } token
                ? new AgentFallbackCredit(token, details.TryGetProperty("fallback_has_prefill_claim", out var claim) && claim.ValueKind == JsonValueKind.True)
                : null;

        /// <summary>Takes what a usage says, each count it holds standing for the request whole: the last said of each holds.</summary>
        private void Used(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            _input = Count(usage, "input_tokens") ?? _input;
            _output = Count(usage, "output_tokens") ?? _output;
            _cacheRead = Count(usage, "cache_read_input_tokens") ?? _cacheRead;
            _cacheWrite = Count(usage, "cache_creation_input_tokens") ?? _cacheWrite;

            if (usage.TryGetProperty("cache_creation", out var written) && written.ValueKind == JsonValueKind.Object)
            {
                _cacheWrite5m = Count(written, "ephemeral_5m_input_tokens") ?? _cacheWrite5m;
                _cacheWrite1h = Count(written, "ephemeral_1h_input_tokens") ?? _cacheWrite1h;
            }

            if (usage.TryGetProperty("server_tool_use", out var server) && server.ValueKind == JsonValueKind.Object)
            {
                _searches = Count(server, "web_search_requests") ?? _searches;
            }
        }

        /// <summary>
        /// What the request used: the cache it wrote by how long it is kept, as the API says it - or, where it says the
        /// whole alone, by the lifetime the request asked for.
        /// </summary>
        private AgentUsage Usage()
        {
            var (fiveMinutes, anHour) = _cacheWrite5m is not null || _cacheWrite1h is not null
                ? (_cacheWrite5m ?? 0, _cacheWrite1h ?? 0)
                : settings.CacheTtl == ClaudeApiRequest.AnHour ? (0L, _cacheWrite) : (_cacheWrite, 0L);

            return new AgentUsage(_input, _output, _cacheRead, fiveMinutes, anHour, _searches);
        }

        private static long? Count(JsonElement value, string name)
            => value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.Number && member.TryGetInt64(out var count) ? count : null;

        /// <summary>The object <paramref name="value"/> holds as <paramref name="name"/>; null where it holds none.</summary>
        private static JsonElement? Member(JsonElement value, string name)
            => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.Object ? member : null;

        /// <summary>The place of the block an event is of; null where it names none.</summary>
        private static int? Index(JsonElement data)
            => data.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var at) ? at : null;

        private static string? Text(JsonElement value, string name)
            => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

        private static bool IsObject(string json) => Block.ObjectOf(json) is not null;
    }

    /// <summary>
    /// One block of an answer as it is put back together: the block it began as, whole, and what each delta added to it -
    /// a text's text, a thinking's thinking and signature, a call's input.
    /// </summary>
    private sealed class Block(string type, JsonObject start)
    {
        public const string Text = "text";
        public const string Thinking = "thinking";
        public const string RedactedThinking = "redacted_thinking";
        public const string ToolUse = "tool_use";

        /// <summary>Every type of block this tool puts back together: no tool of the provider's own is declared, so none of its blocks comes.</summary>
        public static readonly IReadOnlyList<string> Known = [Text, Thinking, RedactedThinking, ToolUse];

        private readonly StringBuilder _written = new();
        private readonly StringBuilder _signature = new();
        private readonly StringBuilder _input = new();

        public string Type { get; } = type;

        public string? Id => start["id"]?.GetValue<string>();

        public string? Name => start["name"]?.GetValue<string>();

        /// <summary>The text or thinking it wrote, as it began and as each delta added to it.</summary>
        public string Written => $"{Begun(Type == Text ? "text" : "thinking")}{_written}";

        /// <summary>A call's input as the API produced it: what its deltas wrote, else what it began with.</summary>
        public string Input => _input.Length > 0 ? _input.ToString() : start["input"]?.ToJsonString(ClaudeApiRequest.Json) ?? "{}";

        public void Add(string delta, string fragment)
        {
            (delta == "signature_delta" ? _signature : delta == "input_json_delta" ? _input : _written).Append(fragment);
        }

        /// <summary>The block whole, as the conversation stores it and sends it back.</summary>
        public JsonObject Whole()
        {
            var whole = start.DeepClone().AsObject();

            switch (Type)
            {
                case Text:
                    whole["text"] = Written;
                    break;
                case Thinking:
                    whole["thinking"] = Written;
                    whole["signature"] = $"{Begun("signature")}{_signature}";
                    break;
                case ToolUse:
                    whole["input"] = InputNode();
                    break;
            }

            return whole;
        }

        /// <summary>What a member of the block said as it began; empty where it said nothing.</summary>
        private string Begun(string member) => start[member] is JsonValue value && value.TryGetValue<string>(out var said) ? said : string.Empty;

        /// <summary>The JSON object <paramref name="json"/> reads as; null where it reads as none.</summary>
        public static JsonObject? ObjectOf(string json)
        {
            try
            {
                return JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>A call's input as JSON, where what its deltas wrote reads as an object; the input it began with otherwise, which only a call cut off part way has.</summary>
        private JsonNode InputNode()
            => (_input.Length > 0 ? ObjectOf(_input.ToString()) : null) ?? start["input"]?.DeepClone() ?? new JsonObject();
    }
}
