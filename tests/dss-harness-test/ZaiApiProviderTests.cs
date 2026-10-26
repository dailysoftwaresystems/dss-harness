using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Agents.ZaiApi;
using DssHarness.Core.Configuration;

namespace DssHarness.Tests;

/// <summary>
/// Z.AI's side of the loop over the answers its probe met live: the one request every model is sent, the chunks read
/// into the loop's parts and the message put back together - its text, its reasoning, each call's arguments by its index
/// - every finish reason, every failure classed, and the one rewrite of what is sent that keeps a conversation within its
/// model's context.
/// </summary>
public sealed class ZaiApiProviderTests : IDisposable
{
    private const string Api = "https://api.z.ai/api/paas/v4";
    private const string Model = "glm-5.3";
    private const string FirstCall = "call_000000000000000000000001";

    private readonly ApiEndpoint _endpoint = new();
    private readonly ApiConnection _connection;

    public ZaiApiProviderTests() => _connection = new ApiConnection(TimeProvider.System, _endpoint);

    public void Dispose() => _connection.Dispose();

    /// <summary>
    /// An answer is put back together from its chunks, however its bytes were cut: its reasoning, its text, its usage - the
    /// prompt's cached part apart - and the message the conversation stores, its reasoning always beside its text.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task AnAnswer_IsPutBackTogether_HoweverItsStreamIsCut(int chunk)
    {
        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse", chunk));

        var parts = await Sent(Request());

        Assert.Equal(
            [
                new ChatPart.Began(Model),
                new ChatPart.Thinking("Just"),
                new ChatPart.Thinking(" answer"),
                new ChatPart.Thinking(" \""),
                new ChatPart.Thinking("alpha\"."),
                new ChatPart.Text("alpha"),
            ],
            parts.SkipLast(1));

        var turn = Assert.IsType<ChatPart.Answered>(parts[^1]).Turn;

        Assert.Equal((ChatStop.Done, Model, "alpha", "Just answer \"alpha\".", "stop"), (turn.Stop, turn.Model, turn.Text, turn.Thinking, turn.StopReason));
        Assert.Equal(new AgentUsage(206, 8, 0, 0, 0, 0), turn.Usage);
        Assert.Empty(turn.Calls);
        Assert.Equal("""{"role":"assistant","content":"alpha","reasoning_content":"Just answer \"alpha\"."}""", turn.Message);
    }

    /// <summary>
    /// A call is put back together by its index - its id and tool as it began, its arguments as each piece added to them -
    /// and two calls of one turn each by its own, in the order of their indexes; the message holds them as Z.AI reads them.
    /// </summary>
    [Fact]
    public async Task Calls_ArePutBackTogether_EachByItsIndex()
    {
        _endpoint.Then(ZaiApiFixtures.Reply("tool-use.sse")).Then(ZaiApiFixtures.Reply("two-calls.sse"));

        var parts = await Sent(Request());
        var turn = Assert.IsType<ChatPart.Answered>(parts[^1]).Turn;

        Assert.Equal(new ChatPart.CallBegan(FirstCall, "view_file"), parts[1]);
        Assert.Equal(["{\"", "path", "\":\"", "a.txt\"", "}"], parts.OfType<ChatPart.CallInput>().Select(part => part.Fragment));
        Assert.All(parts.OfType<ChatPart.CallInput>(), part => Assert.Equal(FirstCall, part.Id));
        Assert.Equal((ChatStop.Tools, "tool_calls", null), (turn.Stop, turn.StopReason, turn.Thinking));
        Assert.Equal([new ChatCall(FirstCall, "view_file", """{"path":"a.txt"}""")], turn.Calls);
        Assert.Equal(new AgentUsage(25, 13, 192, 0, 0, 0), turn.Usage);
        Assert.Equal(
            """{"role":"assistant","content":"","reasoning_content":"","tool_calls":[{"id":"call_000000000000000000000001","type":"function","function":{"name":"view_file","arguments":"{\"path\":\"a.txt\"}"}}]}""",
            turn.Message);

        var both = Assert.IsType<ChatPart.Answered>((await Sent(Request()))[^1]).Turn;

        Assert.Equal(
            [new ChatCall(FirstCall, "view_file", """{"path":"a.txt"}"""), new ChatCall("call_000000000000000000000002", "view_file", """{"path":"b.txt"}""")],
            both.Calls);
    }

    /// <summary>
    /// The answers the probe met each end as the loop acts on them: a reasoning answer after a call's answer done, text cut
    /// off at its length, an answer cut off while it reasoned, a call cut off part way kept as it stands for the loop to
    /// throw away, and a prompt the cache served most of counted apart.
    /// </summary>
    [Fact]
    public async Task TheAnswersTheProbeMet_EndAsTheLoopActsOnThem()
    {
        _endpoint
            .Then(ZaiApiFixtures.Reply("reasoned.sse"))
            .Then(ZaiApiFixtures.Reply("cut-off.sse"))
            .Then(ZaiApiFixtures.Reply("cut-off-thinking.sse"))
            .Then(ZaiApiFixtures.Reply("cut-off-call.sse"))
            .Then(ZaiApiFixtures.Reply("cached.sse"));

        var reasoned = await Turn();
        var cut = await Turn();
        var thought = await Turn();
        var call = await Turn();
        var cached = await Turn();

        Assert.Equal((ChatStop.Done, "alpha", "The file contains \"alpha\". The answer should be one word: alpha."), (reasoned.Stop, reasoned.Text, reasoned.Thinking));
        Assert.Equal(new AgentUsage(44, 18, 192, 0, 0, 0), reasoned.Usage);
        Assert.Equal((ChatStop.CutOff, "length"), (cut.Stop, cut.StopReason));
        Assert.StartsWith("The lighthouse at Cape Merrow had stood for over a century", cut.Text, StringComparison.Ordinal);
        Assert.Equal((ChatStop.CutOff, string.Empty), (thought.Stop, thought.Text));
        Assert.NotNull(thought.Thinking);
        Assert.Equal(ChatStop.CutOff, call.Stop);
        Assert.Equal("{\"path\":\"docs/a-rather-long-directory-name/another-long-directory-name/the-file-with-a", Assert.Single(call.Calls).Input);
        Assert.Equal(new AgentUsage(58, 3, 3648, 0, 0, 0), cached.Usage);
    }

    /// <summary>
    /// Each way Z.AI ends an answer is the loop's: a sensitive answer declined, a context that could hold no more full, a
    /// network error failing for now; an ending this tool does not know fails, naming it; an answer that says it ended to
    /// call tools and called none is asked again, and one that says it stopped while it holds calls ended to call them.
    /// </summary>
    [Fact]
    public async Task EachWayAnAnswerEnds_IsTheLoops()
    {
        _endpoint
            .Then(ZaiApiFixtures.Reply("sensitive.sse"))
            .Then(ZaiApiFixtures.Reply("context-full.sse"))
            .Then(ZaiApiFixtures.Reply("network-error.sse"))
            .Then(ApiReply.Stream(Chunks(Ending("future_reason"))))
            .Then(ApiReply.Stream(Chunks(Ending("tool_calls"))))
            .Then(ApiReply.Stream(Chunks(Call(0, FirstCall, "view_file", """{"path":"a.txt"}"""), Ending("stop"))))
            .Then(ApiReply.Stream(Chunks(Call(0, FirstCall, "view_file", """{"path":"""), Ending("tool_calls"))));

        Assert.Equal((ChatStop.Declined, "To begin"), await Turn() is var declined ? (declined.Stop, declined.Text) : default);
        Assert.Equal(ChatStop.ContextFull, (await Turn()).Stop);
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the API ended the answer for a network error"), await Failure());
        Assert.Equal(new ChatFailure(ChatFailureKind.Fatal, "the answer ended for 'future_reason', which this tool does not know"), await Failure());
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the answer ended to call tools and called none"), await Failure());
        Assert.Equal((ChatStop.Tools, "stop"), await Turn() is var stopped ? (stopped.Stop, stopped.StopReason) : default);
        Assert.Equal(
            new ChatFailure(ChatFailureKind.Transient, $"the answer's call {FirstCall} of 'view_file' came with an input that is no JSON object"),
            await Failure());
    }

    /// <summary>
    /// A stream is read only as Z.AI means it: one that ends without its <c>[DONE]</c>, or ends a message no chunk began, a
    /// chunk that is no JSON and an error chunk part way fail it for now - the error in Z.AI's words; a piece of a call
    /// with no index, or a call begun with no id, is asked again; a call of a type no request declares fails it; and a
    /// member a chunk adds is passed over.
    /// </summary>
    [Fact]
    public async Task AStream_IsReadOnlyAsZaiMeansIt()
    {
        _endpoint
            .Then(ApiReply.Stream(Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "Part of" })))
            .Then(ApiReply.Stream("data: [DONE]\n\n"))
            .Then(ApiReply.Stream("data: {not json\n\n"))
            .Then(ZaiApiFixtures.Reply("stream-error.sse"))
            .Then(ApiReply.Stream(Chunks(Chunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = FirstCall, ["function"] = new JsonObject { ["name"] = "view_file" } }) }))))
            .Then(ApiReply.Stream(Chunks(Chunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["type"] = "function", ["function"] = new JsonObject { ["arguments"] = "{" } }) }))))
            .Then(ApiReply.Stream(Chunks(Chunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = FirstCall, ["type"] = "web_search", ["web_search"] = new JsonObject() }) }))))
            .Then(ApiReply.Stream(Chunks(Chunk(new JsonObject { ["content"] = "ok", ["audio"] = new JsonObject() }, extra: true), Ending("stop"))));

        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the answer's stream ended before its message did"), await Failure());
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the answer's stream ended a message it never began"), await Failure());
        Assert.StartsWith("the answer's stream sent a chunk that is no JSON: ", (await Failure()).Message, StringComparison.Ordinal);
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the API's stream failed: 1234: Network error, error id: 0000, please try again later"), await Failure());
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the answer's stream sent a piece of a call with no index"), await Failure());
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the answer's stream began a call with no id or no tool named"), await Failure());
        Assert.Equal(new ChatFailure(ChatFailureKind.Fatal, "the answer holds a call of type 'web_search', which no request declares and this tool cannot put back together"), await Failure());
        Assert.Equal((ChatStop.Done, "ok"), await Turn() is var passed ? (passed.Stop, passed.Text) : default);
    }

    /// <summary>
    /// What a request used is said as it is read - the loop counts it where the answer ends without its turn - and the
    /// ending a chunk names holds through a chunk after it that names none.
    /// </summary>
    [Fact]
    public async Task WhatARequestUsed_IsSaidAsItIsRead_AndItsEndingHolds()
    {
        _endpoint
            .Then(ApiReply.Stream(Chunk(new JsonObject { ["content"] = "Part of" }) + Ending("stop")))
            .Then(ApiReply.Stream(Chunks(Chunk(new JsonObject { ["content"] = "ok" }), Ending("stop"), Chunk(new JsonObject()))));
        var parts = new List<ChatPart>();

        await foreach (var part in Provider().SendAsync(Request(), TestContext.Current.CancellationToken))
        {
            parts.Add(part);
        }

        Assert.Equal(new ChatPart.Used(new AgentUsage(10, 2, 0, 0, 0, 0)), Assert.Single(parts.OfType<ChatPart.Used>()));
        Assert.Equal(new ChatFailure(ChatFailureKind.Transient, "the answer's stream ended before its message did"), Assert.IsType<ChatPart.Failed>(parts[^1]).Failure);
        Assert.Equal((ChatStop.Done, "ok"), await Turn() is var turn ? (turn.Stop, turn.Text) : default);
    }

    /// <summary>Each refusal is the loop's, by Z.AI's code - else by its status, for a code the table does not hold too - said in Z.AI's own words.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EachRefusal_IsTheLoops(int status, string body, ChatFailure expected)
    {
        _endpoint.Then(ApiReply.Status(status, body, expected.RetryAfter));

        Assert.Equal(expected, await Failure());
    }

    public static TheoryData<int, string, ChatFailure> Refusals()
    {
        static string Coded(string code, string message) => JsonSerializer.Serialize(new { error = new { code, message } });
        const string Limit = "Usage limit reached for 5 hours. Your limit will reset at 2026-10-26 18:00:00";

        return new()
        {
            { 401, ZaiApiFixtures.Read("key-refused.json"), new(ChatFailureKind.KeyRefused, "the API answered 401 1000: Authentication Failed") },
            { 400, ZaiApiFixtures.Read("model-unknown.json"), new(ChatFailureKind.Unserved, "the API answered 400 1211: Unknown Model, please check the model code.") },
            { 400, Coded("1000", "Authentication Failed"), new(ChatFailureKind.KeyRefused, "the API answered 400 1000: Authentication Failed") },
            { 401, Coded("1005", "Need Two-Factor Authentication"), new(ChatFailureKind.KeyRefused, "the API answered 401 1005: Need Two-Factor Authentication") },
            { 429, Coded("1113", "Insufficient balance"), new(ChatFailureKind.AccountLimit, "the API answered 429 1113: Insufficient balance", BalanceSpent: true) },
            { 429, Coded("1308", Limit), new(ChatFailureKind.AccountLimit, $"the API answered 429 1308: {Limit}") },
            { 429, Coded("1321", Limit), new(ChatFailureKind.AccountLimit, $"the API answered 429 1321: {Limit}") },
            { 429, Coded("1302", "Rate limit reached for requests"), new(ChatFailureKind.RateLimited, "the API answered 429 1302: Rate limit reached for requests", TimeSpan.FromSeconds(7)) },
            { 429, Coded("1305", "The service may be temporarily overloaded"), new(ChatFailureKind.Transient, "the API answered 429 1305: The service may be temporarily overloaded") },
            { 500, Coded("1234", "Network error"), new(ChatFailureKind.Transient, "the API answered 500 1234: Network error") },
            { 429, Coded("1309", "Your GLM Coding Plan has expired"), new(ChatFailureKind.Fatal, "the API answered 429 1309: Your GLM Coding Plan has expired") },
            { 429, Coded("1311", "Your current subscription plan does not yet include access to glm-5.3"), new(ChatFailureKind.Unserved, "the API answered 429 1311: Your current subscription plan does not yet include access to glm-5.3") },
            { 403, Coded("1220", "You do not have permission to access chat"), new(ChatFailureKind.Unserved, "the API answered 403 1220: You do not have permission to access chat") },
            { 400, Coded("1261", "Prompt too long"), new(ChatFailureKind.ContextFull, "the API answered 400 1261: Prompt too long") },
            { 400, Coded("1301", "Unsafe or sensitive content"), new(ChatFailureKind.Declined, "the API answered 400 1301: Unsafe or sensitive content") },
            { 400, Coded("1214", "Parameter max_tokens is invalid"), new(ChatFailureKind.Fatal, "the API answered 400 1214: Parameter max_tokens is invalid") },
            { 401, Coded("1002", "A code of tomorrow's"), new(ChatFailureKind.KeyRefused, "the API answered 401 1002: A code of tomorrow's") },
            { 429, """{"error":{"code":1302,"message":"Rate limit reached for requests"}}""", new(ChatFailureKind.RateLimited, "the API answered 429 1302: Rate limit reached for requests") },
            { 502, "<html>Bad Gateway</html>", new(ChatFailureKind.Transient, "the API answered 502: <html>Bad Gateway</html>") },
            { 404, string.Empty, new(ChatFailureKind.Unserved, "the API answered 404 with nothing said") },
            { 418, "{}", new(ChatFailureKind.Fatal, "the API answered 418: {}") },
            { 429, "{}", new(ChatFailureKind.RateLimited, "the API answered 429: {}", TimeSpan.FromSeconds(5)) },
            { 413, "{}", new(ChatFailureKind.ContextFull, "the API answered 413: {}") },
            { 503, "{}", new(ChatFailureKind.Transient, "the API answered 503: {}", TimeSpan.FromSeconds(3)) },
        };
    }

    /// <summary>
    /// The one request every model is sent: its key in Z.AI's header, posted to the completions under the provider's
    /// address; the system prompt as the first message and every stored message after it as it was stored, byte for byte;
    /// the stored tools as they were; the stream with each call's arguments; the thinking on - kept across turns where the
    /// client says so - at the step's effort; the most the answer may hold; the client's fields merged in.
    /// </summary>
    [Fact]
    public async Task TheRequest_IsZaisOneShape()
    {
        const string Tools = """[{"type":"function","function":{"name":"view_file","description":"Views a file - café, naïve, <b>","parameters":{"type":"object"}}}]""";
        const string Message = """{"role":"user","content":"It's <café> & é"}""";
        using var extras = JsonDocument.Parse("""{"user_id":"harness-run","thinking":{"budget":"none"}}""");
        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse")).Then(ZaiApiFixtures.Reply("answer.sse"));

        await Sent(Request() with { Tools = Tools, Messages = [Message], Effort = "high" }, new ZaiApiSettings(PreserveThinking: true, extras.RootElement.Clone()));
        await Sent(Request() with { Tools = "[]", Effort = null });

        var asked = _endpoint.Asked[0];
        var body = JsonNode.Parse(asked.Body)!;

        Assert.Equal(new Uri($"{Api}/chat/completions"), asked.Address);
        Assert.Equal(asked.Address, ZaiApiRequest.Endpoint($"{Api}/"));
        Assert.Equal("Bearer zai-key", asked.Headers["Authorization"]);
        Assert.Equal(
            ["model", "messages", "stream", "tool_stream", "tools", "thinking", "reasoning_effort", "max_tokens", "user_id"],
            body.AsObject().Select(member => member.Key));
        Assert.Contains($"\"messages\":[{{\"role\":\"system\",\"content\":\"Be careful.\"}},{Message}]", asked.Body, StringComparison.Ordinal);
        Assert.Contains($"\"tools\":{Tools},", asked.Body, StringComparison.Ordinal);
        Assert.Equal(
            ("glm-5.3", true, true, "high", 4096L),
            (body["model"]!.GetValue<string>(), body["stream"]!.GetValue<bool>(), body["tool_stream"]!.GetValue<bool>(), body["reasoning_effort"]!.GetValue<string>(), body["max_tokens"]!.GetValue<long>()));
        Assert.Equal("""{"type":"enabled","clear_thinking":false,"budget":"none"}""", body["thinking"]!.ToJsonString());

        var plain = JsonNode.Parse(_endpoint.Asked[1].Body)!.AsObject();

        Assert.Equal(["model", "messages", "stream", "tool_stream", "thinking", "max_tokens"], plain.Select(member => member.Key));
        Assert.Equal("""{"type":"enabled"}""", plain["thinking"]!.ToJsonString());
    }

    /// <summary>
    /// What a client's requestExtras may not set is every member the tool sends itself, each refused by its path; a member
    /// under the thinking the tool sends is merged into it, and one it does not send is added.
    /// </summary>
    [Fact]
    public void RequestExtras_SetNothingTheToolSendsItself()
    {
        using var refused = JsonDocument.Parse("""{"model":"x","messages":[],"stream":false,"tool_stream":false,"tools":[],"reasoning_effort":"max","max_tokens":1,"thinking":{"type":"disabled","clear_thinking":true,"budget":1}}""");
        using var thinking = JsonDocument.Parse("""{"thinking":"off"}""");

        Assert.Equal(
            [
                "x requestExtras.model is refused: the tool sends it itself",
                "x requestExtras.messages is refused: the tool sends it itself",
                "x requestExtras.stream is refused: the tool sends it itself",
                "x requestExtras.tool_stream is refused: the tool sends it itself",
                "x requestExtras.tools is refused: the tool sends it itself",
                "x requestExtras.reasoning_effort is refused: the tool sends it itself",
                "x requestExtras.max_tokens is refused: the tool sends it itself",
                "x requestExtras.thinking.type is refused: the tool sends it itself",
                "x requestExtras.thinking.clear_thinking is refused: the tool sends it itself",
            ],
            ZaiApiRequest.ExtrasProblems(refused.RootElement, "x requestExtras"));
        Assert.Equal(["x requestExtras.thinking is a string, and the tool sends an object there, which its members are merged into"], ZaiApiRequest.ExtrasProblems(thinking.RootElement, "x requestExtras"));
    }

    /// <summary>
    /// The conversation's own form: the tools as Z.AI's functions, the engine's message a user message, and a turn's calls
    /// answered by a tool message each, in their order - a refused one by the text that says why - then each message of
    /// the engine's.
    /// </summary>
    [Fact]
    public void TheConversation_IsInZaisOwnForm()
    {
        var provider = Provider();
        var turn = new ChatTurn(ChatStop.Tools, "{}", Model, string.Empty, null, [], AgentUsage.None, "tool_calls");

        Assert.Equal(
            """[{"type":"function","function":{"name":"view_file","description":"Views a file.","parameters":{"type":"object"}}}]""",
            provider.Tools([new AgentToolDefinition("view_file", AgentCapability.Read, false, "Views a file.", """{"type":"object"}""")]));
        Assert.Equal("""{"role":"user","content":"Go on - café."}""", provider.UserMessage("Go on - café."));
        Assert.Equal(
            [
                """{"role":"tool","tool_call_id":"call_1","content":"alpha"}""",
                """{"role":"tool","tool_call_id":"call_2","content":"refused: the path is protected"}""",
                """{"role":"user","content":"The plan changed."}""",
            ],
            provider.Answers(turn, [new ChatAnswer("call_1", true, "alpha"), new ChatAnswer("call_2", false, "refused: the path is protected")], ["The plan changed."]));
        Assert.Empty(provider.Answers(turn, [], []));
    }

    /// <summary>
    /// Once what the model read and wrote passes 70% of its context, the next request says it begins sending the
    /// conversation shortened - before the last six turns that called tools - and sends every answer to a call before
    /// them in its short form, the calls and every other message as stored; a later request given that point sends the
    /// same; past 90% after that, the context is full and nothing is sent.
    /// </summary>
    [Fact]
    public async Task AConversation_IsSentShortened_PastMostOfItsContext()
    {
        // answer.sse reads 206 and writes 8: 214 of a context of 300 is past 70%, and of 230 past 90%.
        var messages = Calling(8);
        var provider = Provider(contextTokens: 300);
        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse")).Then(ZaiApiFixtures.Reply("answer.sse")).Then(ZaiApiFixtures.Reply("answer.sse"));

        await Sent(Request() with { Messages = messages }, provider: provider);
        var shortening = await Sent(Request() with { Messages = messages }, provider: provider);
        var sent = Messages(_endpoint.Asked[1]);

        // The eight turns begin at 1, 3, ... 15: the last six at 5.
        Assert.Equal(new ChatPart.Shortened(5), shortening[0]);
        Assert.Equal(ZaiApiRequest.ShortAnswer, sent[3]!["content"]!.GetValue<string>());
        Assert.Equal(ZaiApiRequest.ShortAnswer, sent[5]!["content"]!.GetValue<string>());
        Assert.Equal("answer 3", sent[7]!["content"]!.GetValue<string>());
        Assert.Equal(messages[3], sent[4]!.ToJsonString(ApiBody.Json));
        Assert.Equal(messages.Count + 1, sent.Count);

        var again = await Sent(Request() with { Messages = messages, ShortenedBefore = 5 }, provider: provider);

        Assert.DoesNotContain(again, part => part is ChatPart.Shortened);
        Assert.Equal(sent.ToJsonString(), Messages(_endpoint.Asked[2]).ToJsonString());

        var full = Provider(contextTokens: 230);
        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse"));
        await Sent(Request() with { Messages = messages, ShortenedBefore = 5 }, provider: full);

        Assert.Equal(
            new ChatFailure(ChatFailureKind.ContextFull, "the conversation holds 214 tokens, past 90% of the 230 the context of 'glm-5.3' holds, and is sent shortened already"),
            Assert.IsType<ChatPart.Failed>(Assert.Single(await Sent(Request() with { Messages = messages, ShortenedBefore = 5 }, provider: full))).Failure);
        Assert.Equal(4, _endpoint.Asked.Count);
    }

    /// <summary>
    /// A conversation is sent whole below 70% of its model's context, and past it where it holds no more than six turns
    /// that called tools - nothing is older than them; and past 90% where it was never shortened, it is sent all the same,
    /// shortened from then on: the API says when it is full.
    /// </summary>
    [Fact]
    public async Task AConversation_IsSentWhole_WhereNothingIsShortened()
    {
        var below = Provider(contextTokens: 400);
        var few = Provider(contextTokens: 300);
        var never = Provider(contextTokens: 230);
        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse")).Then(ZaiApiFixtures.Reply("answer.sse"));
        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse")).Then(ZaiApiFixtures.Reply("answer.sse"));
        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse")).Then(ZaiApiFixtures.Reply("answer.sse"));

        await Sent(Request() with { Messages = Calling(8) }, provider: below);
        Assert.DoesNotContain(await Sent(Request() with { Messages = Calling(8) }, provider: below), part => part is ChatPart.Shortened);
        await Sent(Request() with { Messages = Calling(6) }, provider: few);
        Assert.DoesNotContain(await Sent(Request() with { Messages = Calling(6) }, provider: few), part => part is ChatPart.Shortened);
        await Sent(Request() with { Messages = Calling(8) }, provider: never);
        Assert.Equal(new ChatPart.Shortened(5), (await Sent(Request() with { Messages = Calling(8) }, provider: never))[0]);

        Assert.All(new[] { _endpoint.Asked[1], _endpoint.Asked[3] }, asked => Assert.DoesNotContain(ZaiApiRequest.ShortAnswer, asked.Body, StringComparison.Ordinal));
        Assert.Equal(6, _endpoint.Asked.Count);
    }

    /// <summary>
    /// Only what the next request holds is weighed against the context: an answer cut off at its length may be thrown
    /// away, so what it wrote is not counted; and past 90% the context is full only as measured on a request sent
    /// shortened - the request asked again after the one that began shortening the conversation failed is sent, never
    /// refused by the measure of the conversation whole.
    /// </summary>
    [Fact]
    public async Task OnlyWhatTheNextRequestHolds_IsWeighedAgainstTheContext()
    {
        // cut-off.sse reads 205 and writes 24: 205 of a context of 300 is below 70%, 229 past it.
        var messages = Calling(8);
        var cut = Provider(contextTokens: 300);
        _endpoint.Then(ZaiApiFixtures.Reply("cut-off.sse")).Then(ZaiApiFixtures.Reply("answer.sse"));

        await Sent(Request() with { Messages = messages }, provider: cut);

        Assert.DoesNotContain(await Sent(Request() with { Messages = messages }, provider: cut), part => part is ChatPart.Shortened);

        // answer.sse reads 206 and writes 8: 214 of a context of 230 is past 90%, measured on the conversation whole.
        var retried = Provider(contextTokens: 230);
        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse")).Then(ApiReply.Status(529, string.Empty)).Then(ZaiApiFixtures.Reply("answer.sse"));

        await Sent(Request() with { Messages = messages }, provider: retried);
        var failed = await Sent(Request() with { Messages = messages }, provider: retried);
        var again = await Sent(Request() with { Messages = messages, ShortenedBefore = 5 }, provider: retried);

        Assert.Equal(new ChatPart.Shortened(5), failed[0]);
        Assert.Equal(ChatFailureKind.Transient, Assert.IsType<ChatPart.Failed>(failed[^1]).Failure.Kind);
        Assert.IsType<ChatPart.Answered>(again[^1]);
        Assert.Contains(ZaiApiRequest.ShortAnswer, _endpoint.Asked[^1].Body, StringComparison.Ordinal);
        Assert.Equal(5, _endpoint.Asked.Count);
    }

    /// <summary>Every stored answer the tests replay, by its name.</summary>
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();

        foreach (var fixture in Directory.GetFiles(ZaiApiFixtures.Folder).Select(Path.GetFileName).Where(name => name != "README.md").Order(StringComparer.Ordinal))
        {
            data.Add(fixture!);
        }

        return data;
    }

    /// <summary>
    /// The stored answers are the public repository's, so each keeps nothing of the account or the machine that made it -
    /// the rules the Claude Code recordings are held to, each call's id among them - and each chunk's id is a stand-in: its
    /// date and counting digits, never the server's own.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void AStoredAnswer_KeepsNothingOfTheAccountThatMadeIt(string fixture)
    {
        var text = ZaiApiFixtures.Read(fixture);

        CuratedRecordings.KeepNothingOfTheMachineOrTheAccount(text);
        Assert.All(System.Text.RegularExpressions.Regex.Matches(text, "\"id\":\"(\\d{8}[0-9a-f]+)\"").Select(found => found.Groups[1].Value), id => Assert.Matches("^[0-9]{30}$", id));
    }

    private static ChatRequest Request()
        => new(Model, "low", "Be careful.", """[{"type":"function","function":{"name":"view_file","description":"Views a file.","parameters":{"type":"object"}}}]""", ["""{"role":"user","content":"Read a.txt."}"""], 4096, new AgentKey("ZAI_API_KEY", "zai-key"), null);

    /// <summary>A conversation of <paramref name="turns"/> turns that each called a tool and were answered, after the step's first message.</summary>
    private static List<string> Calling(int turns)
    {
        List<string> messages = ["""{"role":"user","content":"Read the files."}"""];

        for (var turn = 1; turn <= turns; turn++)
        {
            messages.Add($$$"""{"role":"assistant","content":"","reasoning_content":"thinking {{{turn}}}","tool_calls":[{"id":"call_{{{turn}}}","type":"function","function":{"name":"view_file","arguments":"{}"}}]}""");
            messages.Add($$"""{"role":"tool","tool_call_id":"call_{{turn}}","content":"answer {{turn}}"}""");
        }

        return messages;
    }

    /// <summary>The messages a request sent, the system prompt first.</summary>
    private static JsonArray Messages(ApiSent sent) => JsonNode.Parse(sent.Body)!["messages"]!.AsArray();

    /// <summary>One chunk of an answer of the model's: <paramref name="delta"/>, and members a chunk adds where <paramref name="extra"/>.</summary>
    private static string Chunk(JsonObject delta, bool extra = false)
    {
        var data = new JsonObject { ["id"] = "202610260000000000000000000001", ["model"] = Model, ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta }) };

        if (extra)
        {
            data["system_fingerprint"] = "fp";
        }

        return $"data: {data.ToJsonString(ApiBody.Json)}\n\n";
    }

    /// <summary>A piece of a call: its index, id and tool as it begins, and its arguments.</summary>
    private static string Call(int index, string id, string name, string arguments)
        => Chunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = id, ["index"] = index, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } }) });

    /// <summary>An answer's last chunk for <paramref name="finish"/>, with its usage.</summary>
    private static string Ending(string finish)
        => $"data: {{\"id\":\"202610260000000000000000000001\",\"model\":\"{Model}\",\"choices\":[{{\"index\":0,\"finish_reason\":\"{finish}\",\"delta\":{{\"role\":\"assistant\",\"content\":\"\"}}}}],\"usage\":{{\"prompt_tokens\":10,\"completion_tokens\":2}}}}\n\n";

    /// <summary>A stream of <paramref name="chunks"/>, then its end.</summary>
    private static string Chunks(params string[] chunks) => string.Concat(chunks) + "data: [DONE]\n\n";

    private ZaiApiProvider Provider(ZaiApiSettings? settings = null, long contextTokens = 1_000_000)
        => new(_connection, Api, settings ?? ZaiApiSettings.Of(null), contextTokens);

    /// <summary>The turn the next request was answered with.</summary>
    private async Task<ChatTurn> Turn() => Assert.IsType<ChatPart.Answered>((await Sent(Request()))[^1]).Turn;

    /// <summary>The failure the next request was answered with.</summary>
    private async Task<ChatFailure> Failure() => Assert.IsType<ChatPart.Failed>((await Sent(Request()))[^1]).Failure;

    /// <summary>The parts <paramref name="request"/> was answered with, what is heard of no part and what is said of its usage left out.</summary>
    private async Task<IReadOnlyList<ChatPart>> Sent(ChatRequest request, ZaiApiSettings? settings = null, ZaiApiProvider? provider = null)
    {
        var parts = new List<ChatPart>();

        await foreach (var part in (provider ?? Provider(settings)).SendAsync(request, TestContext.Current.CancellationToken))
        {
            if (part is not (ChatPart.Heard or ChatPart.Used))
            {
                parts.Add(part);
            }
        }

        return parts;
    }
}
