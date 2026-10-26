using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeApi;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// The claude-api client over replayed answers: a step worked through the API by the loop the API clients share, its
/// calls run by the harness's own tools, its conversation stored and sent back byte for byte - in a step that goes on
/// in it too - each request's terms its entries', and nothing of the API's own fallback ever asked.
/// </summary>
public sealed class ClaudeApiClientTests : IDisposable
{
    private const string Model = "claude-opus-5-5";

    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();
    private readonly RecordedEvents _events = new();
    private readonly ApiEndpoint _endpoint = new();
    private readonly List<IDisposable> _opened = [];
    private readonly List<TimeSpan> _waits = [];
    private readonly string _worktree;
    private readonly string _step;
    private readonly string _sessions;

    public ClaudeApiClientTests()
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
    /// A step is worked through the API: its call run by the harness's tools, its answer the step's, and every request
    /// after the first sending the earlier ones' system prompt, tools and messages as they were sent, byte for byte -
    /// the API's own check of a conversation - the call's result in the API's own form.
    /// </summary>
    [Fact]
    public async Task AStep_IsWorkedThroughTheApi_ItsHistorySentBackByteForByte()
    {
        _endpoint.Then(ApiReply.Fixture("tool-use.sse", chunk: 3)).Then(ApiReply.Fixture("answer.sse", chunk: 3));

        var result = await Ran(Run());

        Assert.Equal((AgentRunStatus.Completed, "Café crème: done.", 2, 1), (result.Status, result.FinalText, result.Turns, result.ToolCalls));
        Assert.Equal(new AgentUsage(2100, 103, 3000, 3000, 0, 0), result.Usage!.Total);
        AssertTheConversationHolds(_endpoint.Asked);

        var second = Messages(_endpoint.Asked[1]);
        var answered = JsonNode.Parse(second[2])!;
        var called = Assert.Single(answered["content"]!.AsArray())!;

        Assert.Equal(3, second.Count);
        Assert.Equal(("user", "tool_result", "toolu_000000000000000000000001", null), (answered["role"]!.GetValue<string>(), called["type"]!.GetValue<string>(), called["tool_use_id"]!.GetValue<string>(), called["is_error"]));
        Assert.Contains("alpha", called["content"]!.GetValue<string>(), StringComparison.Ordinal);

        var stored = File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName));
        var answer = Assert.Single(JsonNode.Parse(stored[1])!.AsArray())!;

        Assert.Equal(2, stored.Length);
        Assert.Equal($"[{string.Join(",", second)}]", stored[0]);
        Assert.Equal("assistant", answer["role"]!.GetValue<string>());
        Assert.Equal(["thinking", "text"], answer["content"]!.AsArray().Select(block => block!["type"]!.GetValue<string>()));
    }

    /// <summary>
    /// A step that goes on in a stored conversation sends what it began with - its system prompt and tools as stored,
    /// every earlier message as it was sent - and the step's new message after them: the earlier requests a byte prefix.
    /// </summary>
    [Fact]
    public async Task AStepThatGoesOn_SendsWhatTheConversationBeganWith_ByteForByte()
    {
        _endpoint.Then(ApiReply.Fixture("tool-use.sse")).Then(ApiReply.Fixture("answer.sse")).Then(ApiReply.Fixture("answer.sse"));

        var first = await Ran(Run());
        var goingOn = await Ran(Run() with
        {
            SystemPrompt = "a system prompt the step composes now",
            UserPrompt = "go on",
            Conversation = new AgentSessionRef(ClaudeApiClient.ClientName, "anthropic", first.Session!),
        });

        Assert.Equal((AgentRunStatus.Completed, first.Session), (goingOn.Status, goingOn.Session));
        AssertTheConversationHolds(_endpoint.Asked);
        Assert.Equal(5, Messages(_endpoint.Asked[2]).Count);
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text.Contains("goes on with the system prompt it began with", StringComparison.Ordinal));
    }

    /// <summary>A step on this client is worked by the terms of its entries: the client's tries and its longest wait.</summary>
    [Fact]
    public async Task AStep_IsWorkedByItsEntriesTerms()
    {
        _endpoint.Then(ApiReply.Error(529, "overloaded_error", "Overloaded")).Then(ApiReply.Error(529, "overloaded_error", "Overloaded"));

        var tried = await Ran(Run(new AgentClientConfig { MaxRetries = 1 }));

        Assert.Equal(AgentRunStatus.Failed, tried.Status);
        Assert.Equal(2, _endpoint.Asked.Count);

        _endpoint.Then(ApiReply.Error(429, "rate_limit_error", "slow down", TimeSpan.FromSeconds(30)));

        var limited = await Ran(Run(new AgentClientConfig { MaxRetryWaitSeconds = 10 }));

        Assert.Equal(AgentRunStatus.RateLimited, limited.Status);
        Assert.Equal(("WORK_KEY", Model, false), (limited.Limit!.Key, limited.Limit.Model, limited.Limit.BalanceSpent));
        Assert.Equal(3, _endpoint.Asked.Count);
    }

    /// <summary>A key the API refuses is passed over for the run's next, the same request signed with it; a spent balance is the key's limit.</summary>
    [Fact]
    public async Task ARefusedKey_IsPassedOver_AndASpentBalanceIsTheKeysLimit()
    {
        _endpoint
            .Then(ApiReply.Error(401, "authentication_error", "invalid x-api-key"))
            .Then(ApiReply.Error(402, "billing_error", "credit balance too low"));

        var result = await Ran(Run() with { Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1"), new AgentKey("SPARE_KEY", "sk-2")]) });

        Assert.Equal(["sk-1", "sk-2"], _endpoint.Asked.Select(sent => sent.Headers["x-api-key"]));
        Assert.Equal(_endpoint.Asked[0].Body, _endpoint.Asked[1].Body);
        Assert.Equal((AgentRunStatus.RateLimited, true, "SPARE_KEY", null), (result.Status, result.Limit!.BalanceSpent, result.Limit.Key, result.Limit.Model));
        Assert.Equal(["WORK_KEY"], result.KeysRefused);
        Assert.Equal(("WORK_KEY", "SPARE_KEY"), (Assert.Single(_events.All.OfType<FallbackStarted>()).FromKey, _events.All.OfType<FallbackStarted>().Single().ToKey));
    }

    /// <summary>
    /// A first request the API refuses as a model the key's organization is not given means the step never began, saying
    /// what to do; a declined request ends the step refused with its category and the credit it left the next model.
    /// </summary>
    [Fact]
    public async Task AModelTheOrganizationIsNotGiven_NeverBegins_AndADeclineLeavesItsCredit()
    {
        _endpoint
            .Then(ApiReply.Error(400, "invalid_request_error", "In order to access this model, your organization must have data retention enabled."))
            .Then(ApiReply.Fixture("refusal.sse"));

        var unserved = await Ran(Run());
        var declined = await Ran(Run() with { Fallback = [new AgentFallbackModel("claude-opus-5", "anthropic", ClaudeApiClient.ClientName, null, UsableHere: true)] });

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ConfigurationWrong), (unserved.Status, unserved.NotStartedBecause));
        Assert.Contains("is not given 'claude-opus-5-5'", unserved.Error, StringComparison.Ordinal);
        Assert.Equal((AgentRunStatus.Refused, "cyber", new AgentFallbackCredit("fct_000000000000000000000001", false)), (declined.Status, declined.DeclinedFor, declined.Credit));
        Assert.Equal("context-management-2025-06-27,fallback-credit-2026-07-01", _endpoint.Asked[1].Headers["anthropic-beta"]);
    }

    /// <summary>A step with no entries, or entries of another provider's type, never begins: every request is built from them.</summary>
    [Fact]
    public async Task AStepWithoutItsEntries_NeverBegins()
    {
        var none = await Client().RunAsync(Run() with { Entries = null }, _events, TestContext.Current.CancellationToken);
        var other = await Client().RunAsync(Run() with { Entries = Entries() with { Provider = new AgentProviderConfig { Name = "zai", Type = "zai", Api = "https://api.z.ai" } }, Provider = "zai" }, _events, TestContext.Current.CancellationToken);

        Assert.Equal(
            (AgentRunStatus.NotStarted, "client 'claude-api' was not started: the run was given no entries of its provider, model and client, which every request is built from"),
            (none.Status, none.Error));
        Assert.Equal("client 'claude-api' was not started: the run's provider 'zai' is of type 'zai', and claude-api reaches 'anthropic' alone", other.Error);

        var named = await Client().RunAsync(Run() with { Provider = "work" }, _events, TestContext.Current.CancellationToken);
        var relative = await Client().RunAsync(Run() with { Entries = Entries() with { Provider = new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Api = "api.anthropic.com" } } }, _events, TestContext.Current.CancellationToken);

        Assert.Equal("client 'claude-api' was not started: the run is of provider 'work', and the entries it was given are provider 'anthropic''s", named.Error);
        Assert.Equal("client 'claude-api' was not started: provider 'anthropic' api 'api.anthropic.com' is no absolute address", relative.Error);
        Assert.Empty(_endpoint.Asked);
    }

    /// <summary>
    /// What a step's client entry says of its requests reaches every one of them - the cache's lifetime, the fields merged
    /// in, the betas - and a budget on an hour-long cache needs the model's price of that cache's writes.
    /// </summary>
    [Fact]
    public async Task TheEntrysCacheExtrasAndBetas_ReachEveryRequest()
    {
        using var extras = JsonDocument.Parse("""{"metadata":{"user_id":"u1"}}""");
        var entry = new AgentClientConfig { CacheTtl = "1h", RequestExtras = extras.RootElement.Clone(), Betas = ["b-2026-01-01"] };
        _endpoint.Then(ApiReply.Fixture("answer.sse"));

        await Ran(Run(entry));

        var body = JsonNode.Parse(_endpoint.Asked[0].Body)!;

        Assert.Equal(
            ("1h", "1h", "u1"),
            (body["cache_control"]!["ttl"]!.GetValue<string>(), body["system"]![0]!["cache_control"]!["ttl"]!.GetValue<string>(), body["metadata"]!["user_id"]!.GetValue<string>()));
        Assert.Equal("context-management-2025-06-27,b-2026-01-01", _endpoint.Asked[0].Headers["anthropic-beta"]);

        var budgeted = await Client().RunAsync(Run(entry) with { Limits = new AgentLimits(null, 5m, null, null) }, _events, TestContext.Current.CancellationToken);

        Assert.Equal(
            (AgentRunStatus.NotStarted, "client 'claude-api' was not started: no price for 'claude-opus-5-5' (providers 'anthropic', model 'claude-opus-5-5': priceCacheWrite1h), and a budget is charged with the cost counted from them"),
            (budgeted.Status, budgeted.Error));
        Assert.Single(_endpoint.Asked);
    }

    /// <summary>The client says what it can do: streamed, thinking shown, written to while it works, the stored conversation handed, the harness's tools.</summary>
    [Fact]
    public void TheClient_SaysWhatItCanDo()
    {
        var client = Client();
        var can = client.Capabilities;

        Assert.Equal(ClaudeApiClient.ClientName, client.Name);
        Assert.Equal(
            (true, true, true, AgentConversationHandling.Handed, false, AgentToolsOwner.Harness, AgentToolsOwner.Harness, false, "anthropic", "the request's model"),
            (can.Streaming, can.Thinking, can.WritableWhileWorking, can.GoesOnWith, can.SchemaEnforcedOutput, can.FileAndCommandTools, can.Web, can.ReportsCost, can.ProviderType, can.ToldModelBy));
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], can.EffortLevels);
    }

    /// <summary>Every request a run sent kept the conversation whole: each after the first sent the earlier one's system prompt and tools, and its messages as a byte prefix.</summary>
    private static void AssertTheConversationHolds(IReadOnlyList<ApiSent> sent)
    {
        Assert.DoesNotContain(sent, request => JsonNode.Parse(request.Body)!.AsObject().ContainsKey("fallbacks"));
        Assert.DoesNotContain(sent, request => request.Headers["anthropic-beta"].Contains(ClaudeApiRequest.ServerSideFallbackPrefix, StringComparison.Ordinal));

        foreach (var (earlier, later) in sent.Zip(sent.Skip(1)))
        {
            var (before, after) = (JsonNode.Parse(earlier.Body)!, JsonNode.Parse(later.Body)!);

            Assert.Equal(before["system"]!.ToJsonString(), after["system"]!.ToJsonString());
            Assert.Equal(before["tools"]?.ToJsonString(), after["tools"]?.ToJsonString());

            var (prefix, whole) = (string.Join(",", Messages(earlier)), string.Join(",", Messages(later)));

            Assert.StartsWith(prefix, whole, StringComparison.Ordinal);
        }
    }

    /// <summary>The messages a request sent, each as its body holds it: byte for byte.</summary>
    private static List<string> Messages(ApiSent sent)
    {
        var body = sent.Body;
        var start = body.IndexOf("\"messages\":[", StringComparison.Ordinal) + "\"messages\":[".Length;
        var messages = new List<string>();

        for (var at = start; body[at] != ']';)
        {
            var end = At(body, at);
            messages.Add(body[at..end]);
            at = body[end] == ',' ? end + 1 : end;
        }

        return messages;

        // The end of the JSON value that begins at <paramref name="at"/>, read by its brackets outside its strings.
        static int At(string text, int at)
        {
            var (depth, quoted, escaped) = (0, false, false);

            for (var index = at; ; index++)
            {
                var character = text[index];

                if (quoted)
                {
                    (escaped, quoted) = (!escaped && character == '\\', escaped || character != '"');
                    continue;
                }

                depth += character switch { '{' or '[' => 1, '}' or ']' => -1, _ => 0 };
                quoted = character == '"';

                if (depth == 0)
                {
                    return index + 1;
                }
            }
        }
    }

    private ClaudeApiClient Client()
    {
        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var connection = new ApiConnection(TimeProvider.System, _endpoint);
        var host = new AgentToolHost(_harness.FileSystem, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System);
        _opened.Add(fetch);

        return new ClaudeApiClient(
            _harness.FileSystem,
            host,
            TimeProvider.System,
            connection,
            (delay, _) =>
            {
                lock (_waits)
                {
                    _waits.Add(delay);
                }

                return Task.CompletedTask;
            },
            () => 0);
    }

    private async Task<AgentRunResult> Ran(AgentRun run)
    {
        var result = await Client().RunAsync(run, _events, TestContext.Current.CancellationToken);

        Assert.Null(result.Problem());
        return result;
    }

    private static AgentEntries Entries(AgentClientConfig? client = null)
        => new(
            new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Api = "https://api.anthropic.com", Currency = "USD" },
            new AgentModelConfig { Code = Model, MaxOutputTokens = 64000, ContextTokens = 1000000, PriceInputMiss = 4, PriceInputHit = 0.2, PriceOutput = 20, PriceCacheWrite = 5 },
            client);

    private AgentRun Run(AgentClientConfig? client = null)
        => AgentRuns.Of(_step, _worktree, null, PolicyGrounds.Of(_worktree, [_worktree], [_worktree], [], [], [], null)) with
        {
            Model = Model,
            Handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30)),
            SessionsDirectory = _sessions,
            Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1")]),
            Entries = Entries(client),
        };
}
