using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Agents.ZaiApi;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// The glm client over replayed answers: a step worked through Z.AI by the loop the API clients share, its calls run by
/// the harness's own tools, its conversation stored and sent back byte for byte - the reasoning with each answer - in a
/// step that goes on in it too, and each request's terms its entries'.
/// </summary>
public sealed class ZaiApiClientTests : IDisposable
{
    private const string Model = "glm-5.3";

    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();
    private readonly RecordedEvents _events = new();
    private readonly ApiEndpoint _endpoint = new();
    private readonly List<IDisposable> _opened = [];
    private readonly string _worktree;
    private readonly string _step;
    private readonly string _sessions;

    public ZaiApiClientTests()
    {
        string Real(string name) => _harness.FileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine(name)).FullName);

        _worktree = Real("worktree");
        var agent = Path.Combine(Real("run"), "agents", "a1");
        _step = Path.Combine(agent, "steps", "s1");
        _sessions = Path.Combine(agent, "sessions");
        File.WriteAllText(Path.Combine(_worktree, "a.txt"), "alpha");
    }

    public void Dispose()
    {
        foreach (var opened in _opened)
        {
            opened.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>
    /// A step is worked through Z.AI: its call run by the harness's tools, its answer the step's, every request after the
    /// first sending the earlier ones' system prompt, tools and messages as they were sent, byte for byte, and the call
    /// answered by a tool message; what the conversation stores is the requests' own.
    /// </summary>
    [Fact]
    public async Task AStep_IsWorkedThroughZai_ItsHistorySentBackByteForByte()
    {
        _endpoint.Then(ZaiApiFixtures.Reply("tool-use.sse", chunk: 3)).Then(ZaiApiFixtures.Reply("reasoned.sse", chunk: 3));

        var result = await Ran(Run());

        Assert.Equal((AgentRunStatus.Completed, "alpha", 2, 1), (result.Status, result.FinalText, result.Turns, result.ToolCalls));
        Assert.Equal(new AgentUsage(69, 31, 384, 0, 0, 0), result.Usage!.Total);
        AssertTheConversationHolds(_endpoint.Asked);

        var second = ClaudeApiClientTests.Messages(_endpoint.Asked[1].Body);
        var answered = JsonNode.Parse(second[3])!;

        Assert.Equal(4, second.Count);
        Assert.Equal(("tool", "call_000000000000000000000001"), (answered["role"]!.GetValue<string>(), answered["tool_call_id"]!.GetValue<string>()));
        Assert.Contains("alpha", answered["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Bearer zai-1", _endpoint.Asked[0].Headers["Authorization"]);

        var stored = File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName));
        var answer = Assert.Single(JsonNode.Parse(stored[1])!.AsArray())!;

        Assert.Equal(2, stored.Length);
        Assert.Equal($"[{string.Join(",", second.Skip(1))}]", stored[0]);
        Assert.Equal(("alpha", "The file contains \"alpha\". The answer should be one word: alpha."), (answer["content"]!.GetValue<string>(), answer["reasoning_content"]!.GetValue<string>()));
    }

    /// <summary>
    /// A step that goes on in a stored conversation sends what it began with - its system prompt and tools as stored,
    /// every earlier message as it was sent - and the step's new message after them, the reasoning kept across turns where
    /// the client's entry asks it.
    /// </summary>
    [Fact]
    public async Task AStepThatGoesOn_SendsWhatTheConversationBeganWith_ByteForByte()
    {
        _endpoint.Then(ZaiApiFixtures.Reply("tool-use.sse")).Then(ZaiApiFixtures.Reply("answer.sse")).Then(ZaiApiFixtures.Reply("answer.sse"));

        var first = await Ran(Run());
        var goingOn = await Ran(Run(new AgentClientConfig { PreserveThinking = true }) with
        {
            SystemPrompt = "a system prompt the step composes now",
            UserPrompt = "go on",
            Conversation = new AgentSessionRef(ZaiApiClient.ClientName, "zai", first.Session!),
        });

        Assert.Equal((AgentRunStatus.Completed, first.Session), (goingOn.Status, goingOn.Session));
        AssertTheConversationHolds(_endpoint.Asked);
        Assert.Equal(6, ClaudeApiClientTests.Messages(_endpoint.Asked[2].Body).Count);
        Assert.False(JsonNode.Parse(_endpoint.Asked[1].Body)!["thinking"]!.AsObject().ContainsKey("clear_thinking"));
        Assert.False(JsonNode.Parse(_endpoint.Asked[2].Body)!["thinking"]!["clear_thinking"]!.GetValue<bool>());
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text.Contains("goes on with the system prompt it began with", StringComparison.Ordinal));
    }

    /// <summary>
    /// Once the conversation passes most of its model's context, the loop stores where the provider began sending it
    /// shortened, and every later request - a later step's among them - sends what it did: older answers in their short
    /// form, the calls and the rest whole.
    /// </summary>
    [Fact]
    public async Task AConversationSentShortened_IsSentSoByEveryLaterRequest()
    {
        // tool-use.sse reads 217 and writes 13 - 230 of a context of 320, past 70% - and asks for one call each turn: the
        // eighth request holds seven turns that called tools, and the first turn's answer, message 2, is the one older.
        for (var turn = 0; turn < 8; turn++)
        {
            _endpoint.Then(ZaiApiFixtures.Reply("tool-use.sse"));
        }

        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse")).Then(ZaiApiFixtures.Reply("answer.sse"));
        var small = Entries() with { Model = Glm53(contextTokens: 320) };

        var shortened = await Ran(Run() with { Entries = small, Limits = new AgentLimits(20, null, null, null) });
        var goingOn = await Ran(Run() with { Entries = small, UserPrompt = "go on", Conversation = new AgentSessionRef(ZaiApiClient.ClientName, "zai", shortened.Session!) });

        var directory = Path.Combine(_sessions, shortened.Session!);
        var before = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, StoredConversation.ShortenedFileName)))!["before"]!.GetValue<int>();
        var later = ClaudeApiClientTests.Messages(_endpoint.Asked[^1].Body);

        Assert.Equal((AgentRunStatus.Completed, AgentRunStatus.Completed), (shortened.Status, goingOn.Status));
        Assert.Equal(3, before);
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text.Contains($"is sent shortened before message {before} from now on", StringComparison.Ordinal));
        Assert.Equal(ZaiApiRequest.ShortAnswer, JsonNode.Parse(later[3])!["content"]!.GetValue<string>());
        Assert.Contains("alpha", JsonNode.Parse(later[1 + before + 1])!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(ZaiApiRequest.ShortAnswer, _endpoint.Asked[1].Body, StringComparison.Ordinal);
        Assert.All(File.ReadAllLines(Path.Combine(directory, StoredConversation.ConversationFileName)), line => Assert.DoesNotContain(ZaiApiRequest.ShortAnswer, line, StringComparison.Ordinal));
    }

    /// <summary>The fields the client's entry adds, and its keeping of the reasoning across turns, reach every request of the step.</summary>
    [Fact]
    public async Task TheEntrysExtrasAndReasoning_ReachEveryRequest()
    {
        using var extras = JsonDocument.Parse("""{"user_id":"harness-run"}""");
        _endpoint.Then(ZaiApiFixtures.Reply("tool-use.sse")).Then(ZaiApiFixtures.Reply("answer.sse"));

        await Ran(Run(new AgentClientConfig { RequestExtras = extras.RootElement.Clone(), PreserveThinking = true }));

        Assert.Equal(2, _endpoint.Asked.Count);
        Assert.All(
            _endpoint.Asked.Select(asked => JsonNode.Parse(asked.Body)!),
            body => Assert.Equal(("harness-run", false), (body["user_id"]!.GetValue<string>(), body["thinking"]!["clear_thinking"]!.GetValue<bool>())));
    }

    /// <summary>
    /// Z.AI holds no answer to a schema: the step's envelope is said in words in its first message; an answer that read as
    /// no JSON is asked again in JSON alone - no tool offered, a JSON object asked for - and the request after one that read
    /// as JSON offers the tools again.
    /// </summary>
    [Fact]
    public async Task TheEnvelope_IsSaidInWords_AndAnAnswerOfNoJson_IsAskedAgainInJsonAlone()
    {
        var output = new AgentOutput("o", """{"type":"object","additionalProperties":false,"required":["word"],"properties":{"word":{"type":"string"}}}""");
        _endpoint
            .Then(ZaiApiFixtures.Answer("The word is alpha."))
            .Then(ZaiApiFixtures.Answer(Envelopes.Of(JsonNode.Parse("""{"wrd":"alpha"}""")).ToJsonString()))
            .Then(ZaiApiFixtures.Answer(Envelopes.Of(JsonNode.Parse("""{"word":"alpha"}""")).ToJsonString()));

        var result = await Ran(Run() with { Output = output });

        Assert.Equal((AgentRunStatus.Completed, """{"word":"alpha"}"""), (result.Status, result.StructuredOutput));
        var bodies = _endpoint.Asked.Select(asked => JsonNode.Parse(asked.Body)!.AsObject()).ToList();
        Assert.EndsWith($"\n\n{AgentEnvelope.InWords(AgentEnvelope.Schema(output))}", bodies[0]["messages"]![1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal([true, false, true], bodies.Select(body => body.ContainsKey("tools")));
        Assert.Equal([false, true, false], bodies.Select(body => body.ContainsKey("response_format")));
    }

    /// <summary>A step on this client is worked by the terms of its entries: the client's tries and its longest wait.</summary>
    [Fact]
    public async Task AStep_IsWorkedByItsEntriesTerms()
    {
        _endpoint.Then(Coded(429, "1305", "The service may be temporarily overloaded")).Then(Coded(429, "1305", "The service may be temporarily overloaded"));

        var tried = await Ran(Run(new AgentClientConfig { MaxRetries = 1 }));

        Assert.Equal(AgentRunStatus.Failed, tried.Status);
        Assert.Equal(2, _endpoint.Asked.Count);

        _endpoint.Then(Coded(429, "1302", "Rate limit reached for requests", TimeSpan.FromSeconds(30)));

        var limited = await Ran(Run(new AgentClientConfig { MaxRetryWaitSeconds = 10 }));

        Assert.Equal(AgentRunStatus.RateLimited, limited.Status);
        Assert.Equal(("ZAI_KEY", Model, false), (limited.Limit!.Key, limited.Limit.Model, limited.Limit.BalanceSpent));
        Assert.Equal(3, _endpoint.Asked.Count);
    }

    /// <summary>A key Z.AI does not take is passed over for the run's next, the same request signed with it; an empty balance is the key's limit.</summary>
    [Fact]
    public async Task ARefusedKey_IsPassedOver_AndAnEmptyBalanceIsTheKeysLimit()
    {
        _endpoint.Then(ApiReply.Status(401, ZaiApiFixtures.Read("key-refused.json"))).Then(Coded(429, "1113", "Insufficient balance or no resource package. Please recharge."));

        var result = await Ran(Run() with { Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("ZAI_KEY", "zai-1"), new AgentKey("ZAI_SPARE", "zai-2")]) });

        Assert.Equal(["Bearer zai-1", "Bearer zai-2"], _endpoint.Asked.Select(sent => sent.Headers["Authorization"]));
        Assert.Equal(_endpoint.Asked[0].Body, _endpoint.Asked[1].Body);
        Assert.Equal((AgentRunStatus.RateLimited, true, "ZAI_SPARE", null), (result.Status, result.Limit!.BalanceSpent, result.Limit.Key, result.Limit.Model));
        Assert.Equal(["ZAI_KEY"], result.KeysRefused);
    }

    /// <summary>A model Z.AI does not know, asked before any answer, means the step never began; an answer Z.AI ends as sensitive is the step refused.</summary>
    [Fact]
    public async Task AnUnknownModel_NeverBegins_AndASensitiveAnswerIsRefused()
    {
        _endpoint.Then(ApiReply.Status(400, ZaiApiFixtures.Read("model-unknown.json"))).Then(ZaiApiFixtures.Reply("sensitive.sse"));

        var unserved = await Ran(Run());
        var declined = await Ran(Run());

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ConfigurationWrong), (unserved.Status, unserved.NotStartedBecause));
        Assert.Contains("1211: Unknown Model", unserved.Error, StringComparison.Ordinal);
        Assert.Equal(AgentRunStatus.Refused, declined.Status);
    }

    /// <summary>A step with no entries, or entries of another provider's type, never begins: every request is built from them.</summary>
    [Fact]
    public async Task AStepWithoutItsEntries_NeverBegins()
    {
        var none = await Client().RunAsync(Run() with { Entries = null }, _events, TestContext.Current.CancellationToken);
        var other = await Client().RunAsync(Run() with { Entries = Entries() with { Provider = new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Api = "https://api.anthropic.com" } }, Provider = "anthropic" }, _events, TestContext.Current.CancellationToken);

        Assert.Equal(
            (AgentRunStatus.NotStarted, "client 'glm' was not started: the run was given no entries of its provider, model and client, which every request is built from"),
            (none.Status, none.Error));
        Assert.Equal("client 'glm' was not started: the run's provider 'anthropic' is of type 'anthropic', and glm reaches 'zai' alone", other.Error);
        Assert.Empty(_endpoint.Asked);
    }

    /// <summary>A budget is held where every request's charges can be counted: the model's input, cached input and output prices, and nothing of a cache's writes.</summary>
    [Fact]
    public async Task ABudget_NeedsThePricesZaiBillsBy()
    {
        var unpriced = Entries() with { Model = Glm53(priceInputHit: null) };
        _endpoint.Then(ZaiApiFixtures.Reply("answer.sse"));

        var refused = await Client().RunAsync(Run() with { Entries = unpriced, Limits = new AgentLimits(null, 5m, null, null) }, _events, TestContext.Current.CancellationToken);
        var held = await Ran(Run() with { Limits = new AgentLimits(null, 5m, null, null) });

        Assert.Equal(
            (AgentRunStatus.NotStarted, "client 'glm' was not started: no price for 'glm-5.3' (providers 'zai', model 'glm-5.3': priceInputHit), and a budget is charged with the cost counted from them"),
            (refused.Status, refused.Error));
        Assert.Equal(AgentRunStatus.Completed, held.Status);
        Assert.Single(_endpoint.Asked);
    }

    /// <summary>The client says what it can do: streamed, thinking shown, written to while it works, the stored conversation handed, the harness's tools, the five levels.</summary>
    [Fact]
    public void TheClient_SaysWhatItCanDo()
    {
        var client = Client();
        var can = client.Capabilities;

        Assert.Equal(ZaiApiClient.ClientName, client.Name);
        Assert.Equal(
            (true, true, true, AgentConversationHandling.Handed, false, AgentToolsOwner.Harness, AgentToolsOwner.Harness, false, "zai", "the request's model"),
            (can.Streaming, can.Thinking, can.WritableWhileWorking, can.GoesOnWith, can.SchemaEnforcedOutput, can.FileAndCommandTools, can.Web, can.ReportsCost, can.ProviderType, can.ToldModelBy));
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], can.EffortLevels);
    }

    /// <summary>Every request a run sent kept the conversation whole: each after the first sent the earlier one's tools, and its messages - the system prompt first - as a byte prefix.</summary>
    private static void AssertTheConversationHolds(IReadOnlyList<ApiSent> sent)
    {
        foreach (var (earlier, later) in sent.Zip(sent.Skip(1)))
        {
            Assert.Equal(JsonNode.Parse(earlier.Body)!["tools"]?.ToJsonString(), JsonNode.Parse(later.Body)!["tools"]?.ToJsonString());
            Assert.StartsWith(string.Join(",", ClaudeApiClientTests.Messages(earlier.Body)), string.Join(",", ClaudeApiClientTests.Messages(later.Body)), StringComparison.Ordinal);
        }
    }

    /// <summary>A refusal with Z.AI's code, naming its wait where one is given.</summary>
    private static ApiReply Coded(int status, string code, string message, TimeSpan? retryAfter = null)
        => ApiReply.Status(status, JsonSerializer.Serialize(new { error = new { code, message } }), retryAfter);

    private ZaiApiClient Client()
    {
        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var connection = new ApiConnection(TimeProvider.System, _endpoint);
        var host = new AgentToolHost(_harness.FileSystem, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System);
        _opened.Add(fetch);

        return new ZaiApiClient(_harness.FileSystem, host, TimeProvider.System, connection, (_, _) => Task.CompletedTask, () => 0);
    }

    private async Task<AgentRunResult> Ran(AgentRun run)
    {
        var result = await Client().RunAsync(run, _events, TestContext.Current.CancellationToken);

        Assert.Null(result.Problem());
        return result;
    }

    private static AgentEntries Entries(AgentClientConfig? client = null)
        => new(new AgentProviderConfig { Name = "zai", Type = "zai", Api = "https://api.z.ai/api/paas/v4", Currency = "USD" }, Glm53(), client);

    private static AgentModelConfig Glm53(long contextTokens = 1000000, double? priceInputHit = 0.26)
        => new() { Code = Model, MaxOutputTokens = 131072, ContextTokens = contextTokens, PriceInputMiss = 1.4, PriceInputHit = priceInputHit, PriceOutput = 4.4 };

    private AgentRun Run(AgentClientConfig? client = null)
        => AgentRuns.Of(_step, _worktree, null, PolicyGrounds.Of(_worktree, [_worktree], [_worktree], [], [], [], null)) with
        {
            Provider = "zai",
            Model = Model,
            Handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30)),
            SessionsDirectory = _sessions,
            Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("ZAI_KEY", "zai-1")]),
            Entries = Entries(client),
        };
}
