using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DssHarness.Tests;

/// <summary>What a request to an API carried, as it was sent.</summary>
/// <param name="Address">Where it was posted.</param>
/// <param name="Headers">Its headers and its content's, by name in any case, each one's values joined.</param>
/// <param name="Body">Its body.</param>
internal sealed record ApiSent(Uri Address, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>How an API answers one request, as a test scripts it.</summary>
internal abstract record ApiReply
{
    /// <summary>A stream of <paramref name="events"/>, read <paramref name="chunk"/> bytes at a time - a character split across two reads where one falls so.</summary>
    public static ApiReply Stream(string events, int chunk = 5) => new Streamed(events, chunk, After.Ends);

    /// <summary>The stored answer <paramref name="name"/> (<c>fixtures/claude-api</c>), streamed <paramref name="chunk"/> bytes at a time.</summary>
    public static ApiReply Fixture(string name, int chunk = 5) => Stream(ClaudeApiFixtures.Read(name), chunk);

    /// <summary>A stream of <paramref name="events"/> whose connection is then reset.</summary>
    public static ApiReply Cut(string events) => new Streamed(events, 5, After.Resets);

    /// <summary>A stream of <paramref name="events"/> that then says nothing more until the request is left.</summary>
    public static ApiReply Silent(string events) => new Streamed(events, 5, After.Hangs);

    /// <summary>A refusal: <paramref name="status"/> with <paramref name="body"/>, naming its wait by <paramref name="retryAfter"/> or the date <paramref name="retryAt"/> where one is given.</summary>
    public static ApiReply Status(int status, string body, TimeSpan? retryAfter = null, string type = "application/json", DateTimeOffset? retryAt = null)
        => new Refusing(status, body, retryAfter, type, retryAt);

    /// <summary>The API's own error body: its type and message, and the request it names.</summary>
    public static ApiReply Error(int status, string type, string message, TimeSpan? retryAfter = null)
        => Status(status, $$"""{"type":"error","error":{"type":"{{type}}","message":"{{message}}"},"request_id":"req_000000000000000000000009"}""", retryAfter);

    /// <summary>No answer at all, the network failing with <paramref name="why"/>.</summary>
    public static ApiReply Unreachable(string why) => new Unreached(why);

    /// <summary>What a streamed body does after its events.</summary>
    public enum After
    {
        /// <summary>It ends.</summary>
        Ends,

        /// <summary>Its connection is reset.</summary>
        Resets,

        /// <summary>It says nothing more until the request is left.</summary>
        Hangs,
    }

    /// <summary>A streamed answer.</summary>
    public sealed record Streamed(string Events, int Chunk, After Then) : ApiReply;

    /// <summary>A refused request.</summary>
    public sealed record Refusing(int Code, string Body, TimeSpan? RetryAfter, string Type, DateTimeOffset? RetryAt) : ApiReply;

    /// <summary>A request that reaches nothing.</summary>
    public sealed record Unreached(string Why) : ApiReply;
}

/// <summary>An API a test answers in place of the network: each request answered by the next reply scripted, and every request kept as it was sent.</summary>
internal sealed class ApiEndpoint : HttpMessageHandler
{
    private readonly Queue<ApiReply> _replies = new();
    private readonly List<ApiSent> _asked = [];
    private readonly Lock _gate = new();

    /// <summary>Every request, in the order sent.</summary>
    public IReadOnlyList<ApiSent> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    /// <summary>Answers the next request with <paramref name="reply"/>.</summary>
    public ApiEndpoint Then(ApiReply reply)
    {
        lock (_gate)
        {
            _replies.Enqueue(reply);
        }

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ApiReply reply;

        lock (_gate)
        {
            _asked.Add(new ApiSent(
                request.RequestUri!,
                request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                    .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? string.Empty : Encoding.UTF8.GetString(request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult())));
            reply = _replies.Count > 0 ? _replies.Dequeue() : throw new InvalidOperationException($"request {_asked.Count} was sent, and no reply to it was scripted");
        }

        await Task.Yield();

        switch (reply)
        {
            case ApiReply.Unreached unreached:
                throw new HttpRequestException(unreached.Why);
            case ApiReply.Refusing refusing:
                var refused = new HttpResponseMessage((HttpStatusCode)refusing.Code) { Content = new StringContent(refusing.Body, Encoding.UTF8, refusing.Type), RequestMessage = request };

                if (refusing.RetryAfter is { } wait)
                {
                    refused.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
                }
                else if (refusing.RetryAt is { } at)
                {
                    refused.Headers.RetryAfter = new RetryConditionHeaderValue(at);
                }

                return refused;
            case ApiReply.Streamed streamed:
                var content = new StreamContent(new Chunked(Encoding.UTF8.GetBytes(streamed.Events), streamed.Chunk, streamed.Then));
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            default:
                throw new InvalidOperationException($"'{reply.GetType().Name}' is no reply an endpoint gives.");
        }
    }

    /// <summary>A body read a few bytes at a time, then ended, reset or silent.</summary>
    private sealed class Chunked(byte[] bytes, int chunk, ApiReply.After then) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_at < bytes.Length)
            {
                var count = Math.Min(Math.Min(chunk, buffer.Length), bytes.Length - _at);
                bytes.AsMemory(_at, count).CopyTo(buffer);
                _at += count;
                return count;
            }

            switch (then)
            {
                case ApiReply.After.Resets:
                    throw new IOException("the connection was reset");
                case ApiReply.After.Hangs:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return 0;
                default:
                    return 0;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>The Claude API's stored answers (<c>fixtures/claude-api</c>), and the events a test writes beside them.</summary>
internal static class ClaudeApiFixtures
{
    /// <summary>The folder the stored answers are in.</summary>
    public static string Folder => Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test", "fixtures", "claude-api");

    /// <summary>The stored answer <paramref name="name"/>.</summary>
    public static string Read(string name) => File.ReadAllText(Path.Combine(Folder, name));

    /// <summary>A stream of <paramref name="events"/>, each its type and its data, as the API writes one.</summary>
    public static string Events(params (string Type, string Data)[] events)
        => string.Concat(events.Select(each => $"event: {each.Type}\ndata: {each.Data}\n\n"));

    /// <summary>The start of a message written by <paramref name="model"/>.</summary>
    public static (string, string) Start(string model = "claude-opus-5-5")
        => ("message_start", """{"type":"message_start","message":{"id":"msg_000000000000000000000099","type":"message","role":"assistant","model":""" + JsonSerializer.Serialize(model) + ""","content":[],"stop_reason":null,"usage":{"input_tokens":10,"output_tokens":1}}}""");

    /// <summary>A message's end for <paramref name="reason"/>: its delta, then its stop.</summary>
    public static (string, string)[] End(string reason)
        =>
        [
            ("message_delta", $$$"""{"type":"message_delta","delta":{"stop_reason":"{{{reason}}}","stop_sequence":null},"usage":{"output_tokens":5}}"""),
            ("message_stop", """{"type":"message_stop"}"""),
        ];

    /// <summary>A message that answers <paramref name="text"/> and ends its turn.</summary>
    public static ApiReply Answer(string text)
        => ApiReply.Stream(Events(
        [
            Start(),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""" + JsonSerializer.Serialize(text) + "}}"),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            .. End("end_turn"),
        ]));
}

/// <summary>Z.AI's stored answers (<c>fixtures/zai-api</c>): what the probe met live, and what it cannot ask for, written in its shape.</summary>
internal static class ZaiApiFixtures
{
    /// <summary>The folder the stored answers are in.</summary>
    public static string Folder => Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test", "fixtures", "zai-api");

    /// <summary>The stored answer or body <paramref name="name"/>.</summary>
    public static string Read(string name) => File.ReadAllText(Path.Combine(Folder, name));

    /// <summary>The stored answer <paramref name="name"/>, streamed <paramref name="chunk"/> bytes at a time.</summary>
    public static ApiReply Reply(string name, int chunk = 5) => ApiReply.Stream(Read(name), chunk);

    /// <summary>A message that answers <paramref name="text"/> and stops, in the chunks Z.AI writes one in.</summary>
    public static ApiReply Answer(string text)
        => ApiReply.Stream(
            "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) + "}}]}\n\n"
            + "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":8,\"total_tokens\":28,\"prompt_tokens_details\":{\"cached_tokens\":0}}}\n\n"
            + "data: [DONE]\n\n");
}
