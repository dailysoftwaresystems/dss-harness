using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeApi;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Configuration;

namespace DssHarness.Tests;

/// <summary>
/// The Claude API's side of the loop over replayed answers: the one request every model is sent, the stream read into
/// the loop's parts and the message put back together block by block, every stop reason, and every failure classed.
/// </summary>
public sealed class ClaudeApiProviderTests : IDisposable
{
    private const string Api = "https://api.anthropic.com";
    private const string Model = "claude-opus-5-5";

    private readonly ApiEndpoint _endpoint = new();
    private readonly ApiConnection _connection;

    public ClaudeApiProviderTests() => _connection = new ApiConnection(TimeProvider.System, _endpoint);

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// An answer is put back together from its stream, however its bytes were cut - a character split across two reads: its
    /// thinking with its signature, its text, its usage by kind, the message the conversation stores.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task AnAnswer_IsPutBackTogether_HoweverItsStreamIsCut(int chunk)
    {
        _endpoint.Then(ApiReply.Fixture("answer.sse", chunk));

        var parts = await Sent(Request());

        Assert.Equal(
            [
                new ChatPart.Began(Model),
                new ChatPart.Thinking("The step asks for a summary."),
                new ChatPart.Text("Café crème: "),
                new ChatPart.Text("done."),
            ],
            parts.SkipLast(1));

        var turn = Assert.IsType<ChatPart.Answered>(parts[^1]).Turn;

        Assert.Equal((ChatStop.Done, Model, "Café crème: done.", "The step asks for a summary.", "end_turn"), (turn.Stop, turn.Model, turn.Text, turn.Thinking, turn.StopReason));
        Assert.Equal(new AgentUsage(1200, 42, 0, 3000, 0, 0), turn.Usage);
        Assert.Empty(turn.Calls);
        Assert.Equal(
            """{"role":"assistant","content":[{"type":"thinking","thinking":"The step asks for a summary.","signature":"EqQBCkYIBxgCKkAsignature000000000000000000000000000001"},{"type":"text","text":"Café crème: done."}]}""",
            turn.Message);
        Assert.Null(turn.Credit);
    }

    /// <summary>A call is put back together with its input as the API wrote it, its pieces streamed as they came and what its block began with kept.</summary>
    [Fact]
    public async Task ACall_IsPutBackTogether_WithItsInputAsWritten()
    {
        _endpoint.Then(ApiReply.Fixture("tool-use.sse"));

        var parts = await Sent(Request());
        var turn = Assert.IsType<ChatPart.Answered>(parts[^1]).Turn;

        Assert.Contains(new ChatPart.CallBegan("toolu_000000000000000000000001", "view_file"), parts);
        Assert.Equal(["{\"pa", "th\": \"a.txt\"}"], parts.OfType<ChatPart.CallInput>().Select(part => part.Fragment));
        Assert.Equal((ChatStop.Tools, "Reading it."), (turn.Stop, turn.Text));
        Assert.Equal([new ChatCall("toolu_000000000000000000000001", "view_file", "{\"path\": \"a.txt\"}")], turn.Calls);
        Assert.Equal(new AgentUsage(900, 61, 3000, 0, 0, 0), turn.Usage);
        Assert.Equal(
            """{"type":"tool_use","id":"toolu_000000000000000000000001","name":"view_file","input":{"path":"a.txt"},"caller":{"type":"direct"}}""",
            JsonNode.Parse(turn.Message)!["content"]![2]!.ToJsonString());
    }

    /// <summary>Each way an answer ends is the loop's: cut off, paused, declined - with its category and credit - or out of context.</summary>
    [Theory]
    [InlineData("cut-off.sse", ChatStop.CutOff, "The first half", null, false)]
    [InlineData("pause-turn.sse", ChatStop.Paused, "Still working", null, false)]
    [InlineData("refusal.sse", ChatStop.Declined, "", "cyber", true)]
    [InlineData("refusal-part-way.sse", ChatStop.Declined, "To begin with", null, false)]
    [InlineData("context-full.sse", ChatStop.ContextFull, "Near the end", null, false)]
    public async Task EachWayAnAnswerEnds_IsTheLoops(string fixture, ChatStop stop, string text, string? category, bool credited)
    {
        _endpoint.Then(ApiReply.Fixture(fixture));

        var turn = Assert.IsType<ChatPart.Answered>((await Sent(Request()))[^1]).Turn;

        Assert.Equal((stop, text, category), (turn.Stop, turn.Text, turn.Category));
        Assert.Equal(credited ? new AgentFallbackCredit("fct_000000000000000000000001", PrefillClaim: false) : null, turn.Credit);
    }

    /// <summary>A call cut off part way is in the turn as the API wrote it, and its message holds the input its block began with: the loop never runs nor stores it.</summary>
    [Fact]
    public async Task ACallCutOffPartWay_IsInTheTurnAsWritten()
    {
        _endpoint.Then(ApiReply.Fixture("cut-off-call.sse"));

        var turn = Assert.IsType<ChatPart.Answered>((await Sent(Request()))[^1]).Turn;

        Assert.Equal(ChatStop.CutOff, turn.Stop);
        Assert.Equal([new ChatCall("toolu_000000000000000000000002", "view_file", "{\"path\": \"a.")], turn.Calls);
        Assert.Equal("""{"type":"tool_use","id":"toolu_000000000000000000000002","name":"view_file","input":{}}""", JsonNode.Parse(turn.Message)!["content"]![0]!.ToJsonString());
    }

    /// <summary>A call whose input is no JSON object, in an answer that calls tools, is the answer failing part way: asked again, never run.</summary>
    [Fact]
    public async Task ACallWhoseInputIsNoObject_FailsTheAnswerForNow()
    {
        _endpoint.Then(ApiReply.Stream(ClaudeApiFixtures.Events(
            [
                ClaudeApiFixtures.Start(),
                ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_000000000000000000000003","name":"view_file","input":{}}}"""),
                ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"[1]"}}"""),
                .. ClaudeApiFixtures.End("tool_use"),
            ])));

        var failure = Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure;

        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the answer's call toolu_000000000000000000000003 of 'view_file' came with an input that is no JSON object"), failure);
    }

    /// <summary>A redacted thinking block is kept whole; an answer with no thinking block shows none.</summary>
    [Fact]
    public async Task ARedactedThinkingBlock_IsKeptWhole()
    {
        _endpoint.Then(ApiReply.Fixture("redacted-thinking.sse"));

        var turn = Assert.IsType<ChatPart.Answered>((await Sent(Request()))[^1]).Turn;

        Assert.Null(turn.Thinking);
        Assert.Equal(
            """{"role":"assistant","content":[{"type":"redacted_thinking","data":"EmwKAhgBEgy3va3pzix/LafPsn4aDFIT2Xlxh0L5L8rLVyIwxtE3rAFBa8cr3qpPkNRj2YfWXGmKDxH4mPnZ5sQ7vB5URj"},{"type":"text","text":"Answered."}]}""",
            turn.Message);
    }

    /// <summary>What the server cleared of the context is said, each edit as the API wrote it, and changes nothing of the turn.</summary>
    [Fact]
    public async Task WhatTheServerCleared_IsSaid()
    {
        _endpoint.Then(ApiReply.Fixture("context-edits.sse"));

        var parts = await Sent(Request());

        Assert.Equal(
            new ChatPart.Notice("""The provider edited the conversation's context for this request: {"type":"clear_tool_uses_20250919","cleared_tool_uses":8,"cleared_input_tokens":50000}"""),
            Assert.Single(parts.OfType<ChatPart.Notice>()));
        Assert.Equal("Done.", Assert.IsType<ChatPart.Answered>(parts[^1]).Turn.Text);
    }

    /// <summary>
    /// A block or a delta of a type this tool does not know fails the answer naming it - never dropped, never stored in
    /// part - and a block of a tool of the API's own is one, for no request declares one.
    /// </summary>
    [Theory]
    [InlineData("server-tool.sse", "the answer holds a block of type 'server_tool_use', which this tool does not know and cannot put back together; a block is never dropped or stored in part")]
    [InlineData("unknown-delta.sse", "the answer holds a delta of type 'citations_delta' in a block of type 'text', which this tool does not know and cannot put back together")]
    public async Task ATypeThisToolDoesNotKnow_FailsTheAnswer(string fixture, string said)
    {
        _endpoint.Then(ApiReply.Fixture(fixture));

        Assert.Equal(new ChatFailure(ChatFailureKind.Fatal, said), Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure);
    }

    /// <summary>
    /// A stream that fails part way - an error sent in it, its connection reset, its end before its message's - is the
    /// service failing for now; an event this tool does not know is passed over.
    /// </summary>
    [Theory]
    [InlineData("error", "the API's stream failed: overloaded_error: Overloaded (request req_000000000000000000000001)", 900)]
    [InlineData("reset", "the answer's stream was cut: the connection was reset", 10)]
    [InlineData("short", "the answer's stream ended before its message did", 10)]
    public async Task AStreamThatFailsPartWay_IsTheServiceFailingForNow_HavingSaidWhatItUsed(string how, string said, long input)
    {
        var begun = ClaudeApiFixtures.Events(
            ClaudeApiFixtures.Start(),
            ("future_event", """{"type":"future_event"}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Half"}}"""));

        _endpoint.Then(how switch
        {
            "error" => ApiReply.Fixture("stream-error.sse"),
            "reset" => ApiReply.Cut(begun),
            _ => ApiReply.Stream(begun),
        });

        var parts = await Sent(Request());

        Assert.Equal([new ChatPart.Began(Model), new ChatPart.Text("Half")], parts.SkipLast(1));
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, said), Assert.IsType<ChatPart.Failed>(parts[^1]).Failure);
        Assert.Equal(new AgentUsage(input, 1, 0, 0, 0, 0), Assert.Single((await Sent(Request(), all: true, reply: how is "error" ? ApiReply.Fixture("stream-error.sse") : ApiReply.Stream(begun))).OfType<ChatPart.Used>()).SoFar);
    }

    /// <summary>
    /// What the request has used is said as the stream says it - when the answer begins, and again at its end - so a
    /// request that ends without its turn is counted all the same; the turn carries the last of it.
    /// </summary>
    [Fact]
    public async Task WhatARequestUsed_IsSaidAsTheStreamSaysIt()
    {
        _endpoint.Then(ApiReply.Fixture("answer.sse"));

        var parts = await Sent(Request(), all: true);

        Assert.Equal([new AgentUsage(1200, 2, 0, 3000, 0, 0), new AgentUsage(1200, 42, 0, 3000, 0, 0)], parts.OfType<ChatPart.Used>().Select(used => used.SoFar));
        Assert.Equal(new AgentUsage(1200, 42, 0, 3000, 0, 0), Assert.IsType<ChatPart.Answered>(parts[^1]).Turn.Usage);
    }

    /// <summary>
    /// What comes of an answer that is none of its parts - the API's keep-alive, a block's beginning or end, the message's
    /// delta, an event this tool does not know - is heard all the same, so a long silence of the model's own is no
    /// stream gone silent.
    /// </summary>
    [Fact]
    public async Task WhatIsNoneOfTheAnswersParts_IsHeardAllTheSame()
    {
        _endpoint.Then(ApiReply.Stream(ClaudeApiFixtures.Events(
            [
                ClaudeApiFixtures.Start(),
                ("ping", """{"type":"ping"}"""),
                ("future_event", """{"type":"future_event"}"""),
                ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
                ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}"""),
                ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                .. ClaudeApiFixtures.End("end_turn"),
            ])));

        var parts = await Sent(Request(), all: true);

        Assert.Equal(
            [
                new ChatPart.Used(new AgentUsage(10, 1, 0, 0, 0, 0)), new ChatPart.Began(Model), new ChatPart.Heard(), new ChatPart.Heard(), new ChatPart.Heard(),
                new ChatPart.Text("Hi"), new ChatPart.Heard(), new ChatPart.Used(new AgentUsage(10, 5, 0, 0, 0, 0)),
            ],
            parts.SkipLast(1));
        Assert.Equal("Hi", Assert.IsType<ChatPart.Answered>(parts[^1]).Turn.Text);
    }

    /// <summary>A stream gone silent is left when its request is: the read ends at once, never waiting on the stream.</summary>
    [Fact]
    public async Task ASilentStream_IsLeftWhenItsRequestIs()
    {
        _endpoint.Then(ApiReply.Silent(ClaudeApiFixtures.Events(ClaudeApiFixtures.Start())));
        using var leave = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var parts = new List<ChatPart>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var part in Provider().SendAsync(Request(), leave.Token))
            {
                parts.Add(part);
                leave.CancelAfter(TimeSpan.FromMilliseconds(100));
            }
        });

        Assert.Equal([new ChatPart.Used(new AgentUsage(10, 1, 0, 0, 0, 0)), new ChatPart.Began(Model)], parts);
    }

    /// <summary>
    /// A stream is read whatever place the API gives what it says: blocks numbered out of their order, or a delta of no
    /// block, fail the answer for now; counts said again at the message's end stand for it; why it ended and what the
    /// server cleared, beside the delta or within it; a thinking block given no signature is kept as it began.
    /// </summary>
    [Fact]
    public async Task AStream_IsReadWhereverTheApiPutsWhatItSays()
    {
        _endpoint
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events(
                ClaudeApiFixtures.Start(),
                ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""))))
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events(
                ClaudeApiFixtures.Start(),
                ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}}"""))))
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events(
                ClaudeApiFixtures.Start(),
                ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
                ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Hm."}}"""),
                ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null,"context_management":{"applied_edits":[{"type":"clear_tool_uses_20250919"}]}},"stop_details":{"type":"refusal","category":"bio","fallback_credit_token":"fct_000000000000000000000002","fallback_has_prefill_claim":true},"usage":{"input_tokens":12,"cache_read_input_tokens":7,"output_tokens":30,"server_tool_use":{"web_search_requests":2}}}"""),
                ("message_stop", """{"type":"message_stop"}"""))));

        Assert.Equal(
            new ChatFailure(ChatFailureKind.Transient, "the answer's stream began block 1 where block 0 stood next"),
            Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure);
        Assert.Equal(
            new ChatFailure(ChatFailureKind.Transient, "the answer's stream sent a delta of no block it began"),
            Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure);

        var parts = await Sent(Request());
        var turn = Assert.IsType<ChatPart.Answered>(parts[^1]).Turn;

        Assert.Equal((ChatStop.Declined, "bio", new AgentFallbackCredit("fct_000000000000000000000002", true)), (turn.Stop, turn.Category, turn.Credit));
        Assert.Equal(new AgentUsage(12, 30, 7, 0, 0, 2), turn.Usage);
        Assert.Equal("""{"role":"assistant","content":[{"type":"thinking","thinking":"Hm.","signature":""}]}""", turn.Message);
        Assert.Single(parts.OfType<ChatPart.Notice>());
    }

    /// <summary>A member requestExtras gives as null is sent as null, at the body's top as within one of its objects.</summary>
    [Fact]
    public async Task ANullInRequestExtras_IsSentAsNull()
    {
        using var extras = JsonDocument.Parse("""{"metadata":null,"output_config":{"task_budget":null}}""");
        _endpoint.Then(ApiReply.Fixture("answer.sse"));

        await Sent(Request(), new ClaudeApiSettings("5m", extras.RootElement.Clone(), []));

        Assert.Contains("\"metadata\":null", _endpoint.Asked[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"output_config\":{\"task_budget\":null}", _endpoint.Asked[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A connection over the machine's own network sends through a handler that follows no redirect - which would carry
    /// the key's header wherever it points - keeps no cookie, connects within its bound and decompresses nothing, and gives
    /// a request no time of its own.
    /// </summary>
    [Fact]
    public void AConnection_FollowsNoRedirect_KeepsNoCookie_AndGivesARequestNoTimeOfItsOwn()
    {
        using var connection = new ApiConnection(TimeProvider.System);
        var handler = Assert.IsType<SocketsHttpHandler>(connection.Sending);

        Assert.Equal((false, false, ApiConnection.ConnectBound), (handler.AllowAutoRedirect, handler.UseCookies, handler.ConnectTimeout));
        Assert.Equal((TimeSpan.FromMinutes(5), System.Net.DecompressionMethods.None, Timeout.InfiniteTimeSpan), (handler.PooledConnectionLifetime, handler.AutomaticDecompression, connection.Bound));
    }

    /// <summary>
    /// A wait the API names by its date is read on the connection's clock - what is left of it, and none where it has
    /// passed - and a refusal's body is read up to its bound, no further.
    /// </summary>
    [Fact]
    public async Task AWaitNamedByItsDate_IsWhatIsLeftOfIt_AndARefusalIsReadToItsBound()
    {
        var clock = new ManualClock();
        using var connection = new ApiConnection(clock, _endpoint);
        _endpoint
            .Then(ApiReply.Status(429, "{}", retryAt: clock.GetUtcNow().AddSeconds(90)))
            .Then(ApiReply.Status(429, "{}", retryAt: clock.GetUtcNow().AddSeconds(-30)))
            .Then(ApiReply.Status(502, new string('x', ApiConnection.MostRefusalBytes + 4096), type: "text/html"));

        Assert.Equal(TimeSpan.FromSeconds(90), (await Refusal(connection)).RetryAfter);
        Assert.Equal(TimeSpan.Zero, (await Refusal(connection)).RetryAfter);
        Assert.Equal((502, ApiConnection.MostRefusalBytes, null), await Refusal(connection) is var page ? (page.Status, page.Body.Length, page.RetryAfter) : default);
    }

    /// <summary>
    /// Each refusal of the API is the loop's, said in the API's words with the request it names: a key refused, the key's
    /// limit, a limit of the model on the key waited out as named, the context full, a model the account is not given -
    /// a first request's 400 among them, with what to do - a history changed under a thinking block said as this tool's
    /// defect, the service failing for now, and every other refusal at once.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EachRefusal_IsTheLoops(int status, string type, string message, bool first, ChatFailure expected)
    {
        _endpoint.Then(ApiReply.Error(status, type, message, status == 429 ? TimeSpan.FromSeconds(30) : null));

        var failure = Assert.IsType<ChatPart.Failed>(Assert.Single(await Sent(Request(first ? 1 : 3)))).Failure;

        Assert.Equal(expected, failure);
    }

    public static TheoryData<int, string, string, bool, ChatFailure> Refusals()
    {
        const string Request = " (request req_000000000000000000000009)";

        return new()
        {
            { 401, "authentication_error", "invalid x-api-key", false, new(ChatFailureKind.KeyRefused, $"the API answered 401 authentication_error: invalid x-api-key{Request}") },
            { 402, "billing_error", "credit balance too low", false, new(ChatFailureKind.AccountLimit, $"the API answered 402 billing_error: credit balance too low{Request}", BalanceSpent: true) },
            { 429, "rate_limit_error", "slow down", false, new(ChatFailureKind.RateLimited, $"the API answered 429 rate_limit_error: slow down{Request}", RetryAfter: TimeSpan.FromSeconds(30)) },
            { 413, "request_too_large", "too large", false, new(ChatFailureKind.ContextFull, $"the API answered 413 request_too_large: too large{Request}") },
            { 400, "invalid_request_error", "prompt is too long: 1000001 tokens > 1000000 maximum", false, new(ChatFailureKind.ContextFull, $"the API answered 400 invalid_request_error: prompt is too long: 1000001 tokens > 1000000 maximum{Request}") },
            {
                400, "invalid_request_error", "Invalid `signature` in `thinking` block. The block is bound to a different conversation.", false,
                new(ChatFailureKind.Fatal, $"the API answered 400 invalid_request_error: Invalid `signature` in `thinking` block. The block is bound to a different conversation.{Request} - this tool changed the conversation's history under a thinking block, which is a defect of its own: the request is not sent again, nor its thinking dropped")
            },
            {
                400, "invalid_request_error", "In order to access this model, your organization must have data retention enabled.", true,
                new(ChatFailureKind.Unserved, $"the API answered 400 invalid_request_error: In order to access this model, your organization must have data retention enabled.{Request}; if the request itself is not at fault, the organization this key belongs to is not given '{Model}' - a model that needs 30-day data retention is refused this way where the organization keeps less - and another model can be put first under its tier in tierConfig, or named on the step")
            },
            { 400, "invalid_request_error", "messages: something", false, new(ChatFailureKind.Fatal, $"the API answered 400 invalid_request_error: messages: something{Request}") },
            { 403, "permission_error", "not allowed", false, new(ChatFailureKind.Unserved, $"the API answered 403 permission_error: not allowed{Request}") },
            { 404, "not_found_error", "model: claude-opus-5-5", false, new(ChatFailureKind.Unserved, $"the API answered 404 not_found_error: model: claude-opus-5-5{Request}") },
            { 529, "overloaded_error", "Overloaded", false, new(ChatFailureKind.Transient, $"the API answered 529 overloaded_error: Overloaded{Request}") },
            { 500, "api_error", "Internal error", false, new(ChatFailureKind.Transient, $"the API answered 500 api_error: Internal error{Request}") },
            { 408, "timeout_error", "timed out", false, new(ChatFailureKind.Transient, $"the API answered 408 timeout_error: timed out{Request}") },
            { 422, "unprocessable_entity_error", "no", false, new(ChatFailureKind.Fatal, $"the API answered 422 unprocessable_entity_error: no{Request}") },
            { 307, "redirect", "moved", false, new(ChatFailureKind.Fatal, $"the API answered 307 redirect: moved{Request}") },
        };
    }

    /// <summary>
    /// A spend limit reached is the key's limit at once, whatever its status - the organization's monthly cap, a 429 its
    /// error's details name and no wait mends, and one the organization or a workspace set, a 400 never taken for a model
    /// not given - lifting when the API says access resumes, or at no time it names.
    /// </summary>
    [Fact]
    public async Task ASpendLimitReached_IsTheKeysLimitAtOnce_LiftingWhenAccessResumes()
    {
        const string Cap = "You have reached your API usage limits: your organization has crossed its monthly API usage threshold, set based on your organization's API tier. You will regain access on 2026-09-01 at 00:00 UTC.";
        const string Set = "You have reached your specified workspace API usage limits. You will regain access on 2026-11-01 at 13:45 UTC.";
        _endpoint
            .Then(ApiReply.Status(429, $$$"""{"type":"error","error":{"type":"rate_limit_error","message":"{{{Cap}}}","details":{"error_code":"enforced_spend_limit_reached"}},"request_id":"req_000000000000000000000009"}"""))
            .Then(ApiReply.Error(400, "invalid_request_error", Set))
            .Then(ApiReply.Error(400, "invalid_request_error", "You have reached your specified API usage limits."))
            .Then(ApiReply.Error(400, "invalid_request_error", "Your API usage limits are near."))
            .Then(ApiReply.Error(400, "invalid_request_error", "You have reached your specified quota."));

        Assert.Equal(
            new ChatFailure(ChatFailureKind.AccountLimit, $"the API answered 429 rate_limit_error: {Cap} (request req_000000000000000000000009)", Lifts: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
            Assert.IsType<ChatPart.Failed>(Assert.Single(await Sent(Request()))).Failure);
        Assert.Equal(
            new ChatFailure(ChatFailureKind.AccountLimit, $"the API answered 400 invalid_request_error: {Set} (request req_000000000000000000000009)", Lifts: new DateTimeOffset(2026, 11, 1, 13, 45, 0, TimeSpan.Zero)),
            Assert.IsType<ChatPart.Failed>(Assert.Single(await Sent(Request()))).Failure);
        Assert.Equal(
            (ChatFailureKind.AccountLimit, null),
            Assert.IsType<ChatPart.Failed>(Assert.Single(await Sent(Request(3)))).Failure is var failure ? (failure.Kind, failure.Lifts) : default);

        // A sentence of usage limits that is no limit reached, or a limit reached that is no usage limit, is classed as any other.
        Assert.Equal(ChatFailureKind.Fatal, Assert.IsType<ChatPart.Failed>(Assert.Single(await Sent(Request(3)))).Failure.Kind);
        Assert.Equal(ChatFailureKind.Fatal, Assert.IsType<ChatPart.Failed>(Assert.Single(await Sent(Request(3)))).Failure.Kind);
    }

    /// <summary>
    /// A body that is no error the API wrote is said by its start on one line, a gateway's page among them; an answer with
    /// no stream is a refusal; a request that reaches nothing is the network failing for now.
    /// </summary>
    [Fact]
    public async Task WhatIsNoErrorOfTheApis_IsSaidAsItCame()
    {
        var page = $"<html>\n<body>{new string('x', 400)}</body>\n</html>";
        _endpoint
            .Then(ApiReply.Status(502, page, type: "text/html"))
            .Then(ApiReply.Status(200, """{"id":"msg_1"}"""))
            .Then(ApiReply.Status(503, string.Empty))
            .Then(ApiReply.Unreachable("No such host is known."));

        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, $"the API answered 502: <html> <body>{new string('x', 287)}..."), Failure(await Sent(Request())));
        Assert.Equal(new ChatFailure(ChatFailureKind.Fatal, """the API answered 200: {"id":"msg_1"}"""), Failure(await Sent(Request())));
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the API answered 503 with nothing said"), Failure(await Sent(Request())));
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the API could not be reached: No such host is known."), Failure(await Sent(Request())));

        static ChatFailure Failure(IReadOnlyList<ChatPart> parts) => Assert.IsType<ChatPart.Failed>(Assert.Single(parts)).Failure;
    }

    /// <summary>
    /// Every request is posted to the provider's messages, signed with its key, on the version, under the context
    /// management beta - the credit's too where the step goes on - and the client's betas once each.
    /// </summary>
    [Fact]
    public async Task ARequest_IsPostedSignedAndVersioned()
    {
        _endpoint.Then(ApiReply.Fixture("answer.sse")).Then(ApiReply.Fixture("answer.sse"));

        await Sent(Request(), ClaudeApiSettings.Of(null), "https://gateway.example.test/anthropic/");
        await Sent(Request() with { Credit = true }, ClaudeApiSettings.Of(new AgentClientConfig { Betas = ["thinking-binding-controls-2026-08-01", ClaudeApiRequest.ContextManagementBeta] }));

        var (plain, credited) = (_endpoint.Asked[0], _endpoint.Asked[1]);

        Assert.Equal(new Uri("https://gateway.example.test/anthropic/v1/messages"), plain.Address);
        Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), credited.Address);
        Assert.Equal(("sk-1", "2026-10-21", "context-management-2025-06-27"), (plain.Headers["x-api-key"], plain.Headers["anthropic-version"], plain.Headers["anthropic-beta"]));
        Assert.Equal("context-management-2025-06-27,fallback-credit-2026-07-01,thinking-binding-controls-2026-08-01", credited.Headers["anthropic-beta"]);
        Assert.Equal(("application/json", "text/event-stream"), (plain.Headers["Content-Type"], plain.Headers["Accept"]));
        Assert.DoesNotContain(_endpoint.Asked, sent => sent.Headers["anthropic-beta"].Contains(ClaudeApiRequest.ServerSideFallbackPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every request has the one shape every model is sent: its model and most tokens, streamed, the system prompt with its
    /// cache mark, the tools and every message as stored - byte for byte - a thinking the model decides and shows
    /// summarized, the step's effort, old tool uses cleared by the server, and the conversation's own cache mark; no
    /// sampling, no forced tool choice, no fallbacks.
    /// </summary>
    [Fact]
    public async Task ARequest_HasTheOneShapeEveryModelIsSent()
    {
        var provider = Provider();
        var tools = provider.Tools([new AgentToolDefinition("view_file", AgentCapability.Read, Barrier: false, "Views a file.", """{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string"}}}""")]);
        string[] messages = [provider.UserMessage("Summarize é \"this\"."), """{"role":"assistant","content":[{"type":"text","text":"Oké"}]}""", provider.UserMessage("Go on.")];
        _endpoint.Then(ApiReply.Fixture("answer.sse"));

        await Sent(Request(messages.Length) with { Tools = tools, Messages = messages, Effort = "high" });

        var body = _endpoint.Asked[0].Body;

        Assert.Equal(
            $$$"""{"model":"claude-opus-5-5","max_tokens":16000,"stream":true,"system":[{"type":"text","text":"You are a careful agent.","cache_control":{"type":"ephemeral","ttl":"5m"}}],"tools":{{{tools}}},"messages":[{{{string.Join(",", messages)}}}],"thinking":{"type":"adaptive","display":"summarized"},"output_config":{"effort":"high"},"context_management":{"edits":[{"type":"clear_tool_uses_20250919"}]},"cache_control":{"type":"ephemeral","ttl":"5m"}}""",
            body);
        Assert.Equal(
            """[{"name":"view_file","description":"Views a file.","input_schema":{"type":"object","additionalProperties":false,"required":["path"],"properties":{"path":{"type":"string"}}},"eager_input_streaming":true}]""",
            tools);
    }

    /// <summary>A conversation with no tools sends none; a step with no effort sends none; a client's cache of an hour marks both cache marks so.</summary>
    [Fact]
    public async Task WhatARequestLeavesOut_AndTheHourLongCache()
    {
        _endpoint.Then(ApiReply.Fixture("answer.sse"));

        await Sent(Request() with { Tools = "[]", Effort = null }, ClaudeApiSettings.Of(new AgentClientConfig { CacheTtl = "1h" }));

        var body = JsonNode.Parse(_endpoint.Asked[0].Body)!.AsObject();

        Assert.False(body.ContainsKey("tools"));
        Assert.False(body.ContainsKey("output_config"));
        Assert.Equal(("1h", "1h"), (body["system"]![0]!["cache_control"]!["ttl"]!.GetValue<string>(), body["cache_control"]!["ttl"]!.GetValue<string>()));
    }

    /// <summary>
    /// A client's requestExtras are merged into every body: a new member added, an object's members merged into one the
    /// tool sends - its own kept - and nothing the tool sends replaced.
    /// </summary>
    [Fact]
    public async Task RequestExtras_AreMergedIntoEveryBody()
    {
        using var extras = JsonDocument.Parse("""{"thinking":{"block_binding":{"prefix_mismatch_behavior":"error"}},"metadata":{"user_id":"run-1"}}""");
        _endpoint.Then(ApiReply.Fixture("answer.sse"));

        await Sent(Request(), ClaudeApiSettings.Of(new AgentClientConfig { RequestExtras = extras.RootElement.Clone() }));

        var body = JsonNode.Parse(_endpoint.Asked[0].Body)!.AsObject();

        Assert.Equal("""{"type":"adaptive","display":"summarized","block_binding":{"prefix_mismatch_behavior":"error"}}""", body["thinking"]!.ToJsonString());
        Assert.Equal("""{"user_id":"run-1"}""", body["metadata"]!.ToJsonString());
        Assert.Equal(["model", "max_tokens", "stream", "system", "messages", "thinking", "context_management", "cache_control", "metadata"], body.Select(member => member.Key));
    }

    /// <summary>
    /// A client's requestExtras set nothing the tool sends itself or never sends - fallbacks among those - and are an object,
    /// with an object wherever the tool sends one.
    /// </summary>
    [Theory]
    [InlineData("""{"metadata":{"user_id":"x"},"thinking":{"block_binding":{}}}""", new string[0])]
    [InlineData("[1]", new[] { "x requestExtras is a list, and the fields merged into every request's body are an object's members" })]
    [InlineData("""{"model":"other","stream":false}""", new[] { "x requestExtras.model is refused: the tool sends it itself", "x requestExtras.stream is refused: the tool sends it itself" })]
    [InlineData("""{"fallbacks":"default"}""", new[] { "x requestExtras.fallbacks is refused: no request carries fallbacks, for the engine's fallback is the only one a step has" })]
    [InlineData("""{"thinking":{"type":"disabled"},"output_config":{"format":{}}}""", new[] { "x requestExtras.thinking.type is refused: the tool sends it itself", "x requestExtras.output_config.format is refused: the tool sends it itself" })]
    [InlineData("""{"thinking":true}""", new[] { "x requestExtras.thinking is true or false, and the tool sends an object there, which its members are merged into" })]
    public void RequestExtras_SetNothingTheToolSends(string extras, string[] problems)
    {
        using var document = JsonDocument.Parse(extras);

        Assert.Equal(problems, ClaudeApiRequest.ExtrasProblems(document.RootElement, "x requestExtras"));
    }

    /// <summary>
    /// A conversation's messages are the API's own: the engine's a user message of text, a turn's calls answered by one user
    /// message holding their results in order - a refusal said as an error, an empty answer holding no content - and then
    /// each message of the engine's after them.
    /// </summary>
    [Fact]
    public void AConversationsMessages_AreTheApisOwn()
    {
        var provider = Provider();
        var turn = new ChatTurn(ChatStop.Tools, "{}", Model, string.Empty, null, [new ChatCall("c1", "view_file", "{}"), new ChatCall("c2", "view_file", "{}")], AgentUsage.None, "tool_use");

        Assert.Equal("""{"role":"user","content":[{"type":"text","text":"Go \"on\"."}]}""", provider.UserMessage("Go \"on\"."));
        Assert.Equal(
            ["""{"role":"user","content":[{"type":"tool_result","tool_use_id":"c1","content":"alpha"},{"type":"tool_result","tool_use_id":"c2","is_error":true},{"type":"text","text":"the plan changed"}]}"""],
            provider.Answers(turn, [new ChatAnswer("c1", true, "alpha"), new ChatAnswer("c2", false, string.Empty)], ["the plan changed"]));
        Assert.Equal(["""{"role":"user","content":[{"type":"text","text":"the plan changed"}]}"""], provider.Answers(turn with { Calls = [] }, [], ["the plan changed"]));
        Assert.Empty(provider.Answers(turn with { Calls = [] }, [], []));
    }

    /// <summary>Every request is billed by its input, cache read and output, and by the cache write of the client's lifetime.</summary>
    [Fact]
    public void EveryRequest_IsBilledByTheCacheWriteOfItsLifetime()
    {
        Assert.Equal(new AgentUsage(1, 1, 1, 1, 0, 0), Provider().Uses);
        Assert.Equal(new AgentUsage(1, 1, 1, 0, 1, 0), Provider(ClaudeApiSettings.Of(new AgentClientConfig { CacheTtl = "1h" })).Uses);
    }

    /// <summary>The cache a request wrote, where the API says it whole alone, is counted by the lifetime the request asked for.</summary>
    [Theory]
    [InlineData("5m", 3000, 0)]
    [InlineData("1h", 0, 3000)]
    public async Task ACacheWriteSaidWhole_IsCountedByItsLifetime(string ttl, long fiveMinutes, long anHour)
    {
        _endpoint.Then(ApiReply.Stream(ClaudeApiFixtures.Events(
            [
                ("message_start", """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"usage":{"input_tokens":10,"cache_creation_input_tokens":3000,"cache_read_input_tokens":5,"output_tokens":1}}}"""),
                .. ClaudeApiFixtures.End("end_turn"),
            ])));

        var turn = Assert.IsType<ChatPart.Answered>((await Sent(Request(), ClaudeApiSettings.Of(new AgentClientConfig { CacheTtl = ttl })))[^1]).Turn;

        Assert.Equal(new AgentUsage(10, 5, 5, fiveMinutes, anHour, 0), turn.Usage);
    }

    /// <summary>
    /// The answers slice 24b's live smoke met, curated beside the written ones, are read as those are, for all they hold
    /// that the written ones do not: each event's data padded with spaces after it, a keep-alive written with a space,
    /// the members the API adds - the container, the diagnostics, the service tier, the inference's region, the speed, a
    /// null stop's details, the thinking among the output's tokens - passed over, and the counts the message's delta says
    /// again counted once; the conversation sent on to another model answered by that model.
    /// </summary>
    [Fact]
    public async Task TheAnswersALiveRunMet_AreReadAsTheWrittenOnesAre()
    {
        _endpoint.Then(ApiReply.Fixture("live-tool-use.sse")).Then(ApiReply.Fixture("live-thinking.sse")).Then(ApiReply.Fixture("live-sent-on.sse"));

        var calling = await Sent(Request());
        var call = Assert.IsType<ChatPart.Answered>(calling[^1]).Turn;
        var thought = Assert.IsType<ChatPart.Answered>((await Sent(Request()))[^1]).Turn;
        var sentOn = Assert.IsType<ChatPart.Answered>((await Sent(Request()))[^1]).Turn;

        Assert.Equal(["{\"path\": \"a.txt", "\"}"], calling.OfType<ChatPart.CallInput>().Select(part => part.Fragment));
        Assert.Equal((ChatStop.Tools, "claude-haiku-5-5", new AgentUsage(4, 50, 1209, 123, 0, 0)), (call.Stop, call.Model, call.Usage));
        Assert.Equal([new ChatCall("toolu_000000000000000000000001", "view_file", "{\"path\": \"a.txt\"}")], call.Calls);
        Assert.Equal(
            """{"type":"tool_use","id":"toolu_000000000000000000000001","name":"view_file","input":{"path":"a.txt"},"caller":{"type":"direct"}}""",
            JsonNode.Parse(call.Message)!["content"]![0]!.ToJsonString());

        Assert.Equal((ChatStop.Done, "The word is **alpha**, and it contains **2** vowels (the two a's)."), (thought.Stop, thought.Text));
        Assert.Equal("Checking \"alpha\": a-l-p-h-a, so the vowels are the two a's, giving 2.\n\n", thought.Thinking);
        Assert.Equal(new AgentUsage(2, 133, 1332, 62, 0, 0), thought.Usage);
        Assert.Equal("<elided: the API's signature, 1112 characters>", JsonNode.Parse(thought.Message)!["content"]![0]!["signature"]!.GetValue<string>());

        Assert.Equal((ChatStop.Done, "ahpla", "claude-sonnet-5-5", new AgentUsage(4, 6, 1209, 342, 0, 0)), (sentOn.Stop, sentOn.Text, sentOn.Model, sentOn.Usage));
    }

    /// <summary>Every stored answer the tests replay, by its name.</summary>
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();

        foreach (var fixture in Directory.GetFiles(ClaudeApiFixtures.Folder, "*.sse").Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            data.Add(fixture!);
        }

        return data;
    }

    /// <summary>
    /// The stored answers are the public repository's, so each keeps nothing of the account or the machine that made it
    /// - the rules the Claude Code recordings are held to - and a live one no signature the API gave its thinking: an
    /// answer curated without them is refused here.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AStoredAnswer_KeepsNothingOfTheAccountThatMadeIt(string fixture)
    {
        var text = ClaudeApiFixtures.Read(fixture);

        CuratedRecordings.KeepNothingOfTheMachineOrTheAccount(text);

        if (fixture.StartsWith("live-", StringComparison.Ordinal))
        {
            Assert.All(
                System.Text.RegularExpressions.Regex.Matches(text, "\"signature\":\"([^\"]+)\"").Select(found => found.Groups[1].Value),
                signature => Assert.Matches("^<elided: the API's signature, [0-9]+ characters>$", signature));
        }
    }

    /// <summary>
    /// What a stream says is read only as the API means it: an event that is no JSON is the answer failing for now; a
    /// delta of one block's type sent to a block of another fails it, naming both; a call cut off part way keeps the
    /// input its block began with; an answer that met a stop sequence is done; a message ended that was never begun is
    /// the stream failing for now.
    /// </summary>
    [Fact]
    public async Task AStream_IsReadOnlyAsTheApiMeansIt()
    {
        _endpoint
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events(ClaudeApiFixtures.Start(), ("content_block_delta", "{not json"))))
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events(
                ClaudeApiFixtures.Start(),
                ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
                ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}}"""))))
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events(
                [
                    ClaudeApiFixtures.Start(),
                    ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_000000000000000000000004","name":"view_file","input":{"path":"a.txt"}}}"""),
                    ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"pa"}}"""),
                    .. ClaudeApiFixtures.End("max_tokens"),
                ])))
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events([ClaudeApiFixtures.Start(), .. ClaudeApiFixtures.End("stop_sequence")])))
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events(ClaudeApiFixtures.End("end_turn"))));

        var notJson = Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure;

        Assert.Equal(ChatFailureKind.Transient, notJson.Kind);
        Assert.StartsWith("the answer's stream sent a 'content_block_delta' event that is no JSON: ", notJson.Message, StringComparison.Ordinal);
        Assert.Equal(
            new ChatFailure(ChatFailureKind.Fatal, "the answer holds a delta of type 'text_delta' in a block of type 'thinking', which this tool does not know and cannot put back together"),
            Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure);

        var cut = Assert.IsType<ChatPart.Answered>((await Sent(Request()))[^1]).Turn;

        Assert.Equal("""{"type":"tool_use","id":"toolu_000000000000000000000004","name":"view_file","input":{"path":"a.txt"}}""", JsonNode.Parse(cut.Message)!["content"]![0]!.ToJsonString());
        Assert.Equal((ChatStop.Done, "stop_sequence"), Assert.IsType<ChatPart.Answered>((await Sent(Request()))[^1]).Turn is var done ? (done.Stop, done.StopReason) : default);
        Assert.Equal(
            new ChatFailure(ChatFailureKind.Transient, "the answer's stream ended a message it never began"),
            Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure);
    }

    /// <summary>
    /// The conversation's tools and messages go out as they were stored, byte for byte - a character JSON may write
    /// escaped, or not, written as it stands.
    /// </summary>
    [Fact]
    public async Task StoredToolsAndMessages_GoOutByteForByte()
    {
        const string Tools = """[{"name":"view_file","description":"Views a file's text - café, naïve, <b>","input_schema":{"type":"object"}}]""";
        const string Message = """{"role":"user","content":[{"type":"text","text":"It's <café> & \u00e9"}]}""";
        _endpoint.Then(ApiReply.Fixture("answer.sse"));

        await Sent(Request() with { Tools = Tools, Messages = [Message] });

        Assert.Contains($"\"tools\":{Tools},", _endpoint.Asked[0].Body, StringComparison.Ordinal);
        Assert.Contains($"\"messages\":[{Message}]", _endpoint.Asked[0].Body, StringComparison.Ordinal);
    }

    /// <summary>An answer that ends for a reason this tool does not know fails, naming it; a stream that begins naming no model is the service failing for now.</summary>
    [Fact]
    public async Task AnUnknownEndingOrAnUnnamedModel_Fails()
    {
        _endpoint
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events([ClaudeApiFixtures.Start(), .. ClaudeApiFixtures.End("future_reason")])))
            .Then(ApiReply.Stream(ClaudeApiFixtures.Events(("message_start", """{"type":"message_start","message":{"id":"msg_1"}}"""))));

        Assert.Equal(new ChatFailure(ChatFailureKind.Fatal, "the answer ended for 'future_reason', which this tool does not know"), Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure);
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the answer's stream began with no model named"), Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure);
    }

    private ClaudeApiProvider Provider(ClaudeApiSettings? settings = null, string api = Api) => new(_connection, api, settings ?? ClaudeApiSettings.Of(null));

    /// <summary>
    /// The parts <paramref name="request"/> was answered with - by <paramref name="reply"/> where one is given - what is heard
    /// of no part and what is said of its usage left out unless <paramref name="all"/>.
    /// </summary>
    private async Task<IReadOnlyList<ChatPart>> Sent(ChatRequest request, ClaudeApiSettings? settings = null, string api = Api, bool all = false, ApiReply? reply = null)
    {
        var parts = new List<ChatPart>();

        if (reply is not null)
        {
            _endpoint.Then(reply);
        }

        await foreach (var part in Provider(settings, api).SendAsync(request, TestContext.Current.CancellationToken))
        {
            if (all || part is not (ChatPart.Heard or ChatPart.Used))
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    /// <summary>The refusal a post over <paramref name="connection"/> is answered with, alone.</summary>
    private static async Task<ApiEvent.Refused> Refusal(ApiConnection connection)
    {
        var events = new List<ApiEvent>();

        await foreach (var happened in connection.PostAsync(new ApiPost(ClaudeApiRequest.Endpoint(Api), [], "{}"u8.ToArray()), TestContext.Current.CancellationToken))
        {
            events.Add(happened);
        }

        return Assert.IsType<ApiEvent.Refused>(Assert.Single(events));
    }

    /// <summary>A request of a conversation <paramref name="messages"/> long: its first where it is one.</summary>
    private static ChatRequest Request(int messages = 1)
        => new(
            Model,
            null,
            "You are a careful agent.",
            "[]",
            [.. Enumerable.Range(0, messages).Select(at => at % 2 == 0 ? """{"role":"user","content":[{"type":"text","text":"Go."}]}""" : """{"role":"assistant","content":[{"type":"text","text":"Ok."}]}""")],
            16000,
            new AgentKey("WORK_KEY", "sk-1"),
            null);
}
