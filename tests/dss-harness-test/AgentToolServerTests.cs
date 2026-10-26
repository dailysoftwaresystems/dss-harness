using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Execution;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// The engine's tool server: the part of MCP a server of tools needs, over JSON-RPC - its initialize, its notifications, its
/// tool list and its calls - answered from the runtime the step's launch takes its tools from.
/// </summary>
public sealed class AgentToolServerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();
    private readonly RecordedEvents _events = new();
    private readonly List<IDisposable> _opened = [];
    private readonly string _worktree;
    private readonly string _step;

    public AgentToolServerTests()
    {
        _worktree = _harness.FileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine("worktree")).FullName);
        _step = Path.Combine(_harness.FileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine("run")).FullName), "step");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var opened in Enumerable.Reverse(_opened))
        {
            opened.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>
    /// The initialize is answered with the version of MCP the client asked where the server speaks it, else the newest it
    /// speaks; its capabilities are tools alone, and it names itself and the tool's version.
    /// </summary>
    [Theory]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2031-01-01", "2025-11-25")]
    [InlineData(null, "2025-11-25")]
    public void TheInitialize_IsAnsweredInTheVersionAsked_WhereItIsSpoken(string? asked, string answered)
    {
        var server = Server();
        var message = Message("initialize", 0, asked is null ? new JsonObject() : new JsonObject { ["protocolVersion"] = asked });

        var reply = Answered(server.Read(message));

        Assert.Equal(0, reply["id"]!.GetValue<int>());
        Assert.Equal("2.0", reply["jsonrpc"]!.GetValue<string>());
        Assert.Equal(answered, reply["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("""{"tools":{"listChanged":false}}""", reply["result"]!["capabilities"]!.ToJsonString());
        Assert.Equal("""{"name":"dssharness","version":"1.2.3"}""", reply["result"]!["serverInfo"]!.ToJsonString());
    }

    /// <summary>The tool list is the runtime's: each tool by its name, what the model is told of it, and its input's schema as an object.</summary>
    [Fact]
    public void TheToolList_IsTheRuntimes()
    {
        var server = Server(Search());

        var tools = Answered(server.Read(Message("tools/list", 1))).AsObject()["result"]!["tools"]!.AsArray();

        Assert.Equal([AgentTools.WebFetch, AgentTools.WebSearch], tools.Select(tool => tool!["name"]!.GetValue<string>()));
        Assert.All(tools, tool => Assert.IsType<JsonObject>(tool!["inputSchema"]));
        Assert.Equal("url", tools[0]!["inputSchema"]!["required"]![0]!.GetValue<string>());
        Assert.Contains("alpha", tools[1]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A ping is answered with an empty result; a notification, and a message that is no request, with nothing of their own
    /// - the channel's request still wants its answer; a method the server has not, and a request naming none, are errors.
    /// </summary>
    [Fact]
    public void EveryOtherMessage_IsAnsweredAsJsonRpcSays()
    {
        var server = Server();

        Assert.Equal("""{"jsonrpc":"2.0","id":"p","result":{}}""", Answered(server.Read(Message("ping", "p"))).ToJsonString());
        Assert.Equal("""{"jsonrpc":"2.0","result":{}}""", Answered(server.Read(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" })).ToJsonString());
        Assert.Equal("""{"jsonrpc":"2.0","result":{}}""", Answered(server.Read(new JsonObject { ["jsonrpc"] = "2.0", ["result"] = new JsonObject() })).ToJsonString());
        Assert.Equal("""{"jsonrpc":"2.0","result":{}}""", Answered(server.Read(JsonValue.Create("no object"))).ToJsonString());
        Assert.Equal("""{"jsonrpc":"2.0","result":{}}""", Answered(server.Read(null)).ToJsonString());
        Assert.Equal(
            """{"jsonrpc":"2.0","id":3,"error":{"code":-32601,"message":"the server 'dssharness' has no method 'resources/list'"}}""",
            Json(Answered(server.Read(Message("resources/list", 3)))));
        Assert.Equal(
            """{"jsonrpc":"2.0","id":4,"error":{"code":-32600,"message":"the message names no method"}}""",
            Answered(server.Read(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 4 })).ToJsonString());
    }

    /// <summary>
    /// A message whose params - or a call's <c>_meta</c> - are no object is answered as its kind is, never a fault: an
    /// initialize in the newest version, a cancellation acknowledged, a call naming no tool an error, a call naming no call
    /// read as one that names none.
    /// </summary>
    [Fact]
    public void AMessageWhoseParamsAreNoObject_IsAnsweredAsItsKind()
    {
        var server = Server();

        Assert.Equal(AgentToolServer.ProtocolVersions[^1], Answered(server.Read(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = "2024-11-05" }))["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.IsType<AgentToolServerMessage.Answered>(server.Read(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = 7 }));
        Assert.Equal(-32602, Code(server.Read(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call", ["params"] = new JsonArray(AgentTools.WebFetch) })));

        var call = Assert.IsType<AgentToolServerMessage.Call>(server.Read(Message("tools/call", 3, new JsonObject { ["name"] = AgentTools.WebFetch, ["_meta"] = "toolu_1" })));
        Assert.Null(call.CallId);
    }

    /// <summary>A call's cancellation names the call it cancels by the same key as the call's own id, and is acknowledged at once.</summary>
    [Fact]
    public void ACallsCancellation_NamesTheCall()
    {
        var server = Server();
        var call = Assert.IsType<AgentToolServerMessage.Call>(server.Read(Call(AgentTools.WebFetch, new JsonObject { ["url"] = "https://example.test/" }, "toolu_1", id: 7)));

        var cancelled = Assert.IsType<AgentToolServerMessage.Cancelled>(server.Read(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject { ["requestId"] = 7, ["reason"] = "gone" },
        }));

        Assert.Equal(AgentToolServer.Key(call.Id), cancelled.RequestId);
        Assert.Equal("""{"jsonrpc":"2.0","result":{}}""", cancelled.Reply.ToJsonString());
    }

    /// <summary>
    /// A call is read as its tool among those served, its arguments an object - none given an empty one - and the call it was
    /// asked as from its <c>_meta</c>; one that names no tool, one the server has not, and arguments that are no object are errors.
    /// </summary>
    [Fact]
    public void ACall_IsReadAsItsToolArgumentsAndTheCallItWasAskedAs()
    {
        var server = Server();

        var call = Assert.IsType<AgentToolServerMessage.Call>(server.Read(Call(AgentTools.WebFetch, new JsonObject { ["url"] = "https://example.test/" }, "toolu_1")));
        Assert.Equal((AgentTools.WebFetch, """{"url":"https://example.test/"}""", "toolu_1"), (call.Tool, call.Arguments.ToJsonString(), call.CallId));

        var bare = Assert.IsType<AgentToolServerMessage.Call>(server.Read(Message("tools/call", 2, new JsonObject { ["name"] = AgentTools.WebFetch })));
        Assert.Equal(("{}", null), (bare.Arguments.ToJsonString(), bare.CallId));

        Assert.Equal(-32602, Code(server.Read(Message("tools/call", 3, new JsonObject()))));
        Assert.Equal(-32602, Code(server.Read(Message("tools/call", 4, new JsonObject { ["name"] = "view_file" }))));
        Assert.Equal(-32602, Code(server.Read(Message("tools/call", 5, new JsonObject { ["name"] = AgentTools.WebFetch, ["arguments"] = "url" }))));
        Assert.Contains("has no tool 'view_file': it has web_fetch", Answered(server.Read(Message("tools/call", 4, new JsonObject { ["name"] = "view_file" })))["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A call is run by the runtime and answered with its text: a page in its block, not an error; one the policy refuses,
    /// an error whose text says the rule - recorded and counted as every denial is - and nothing sent.
    /// </summary>
    [Fact]
    public async Task ACall_IsRunByTheRuntime_AndAnsweredWithItsText()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("the page's text", "text/plain"));
        var (server, runtime) = Serve(endpoint: endpoint, restrictions: [new AgentRestriction("Web(refused.test)", AgentPolicySource.Config)]);

        var (page, read) = await server.CallAsync(Assert.IsType<AgentToolServerMessage.Call>(server.Read(Call(AgentTools.WebFetch, new JsonObject { ["url"] = "https://example.test/" }, "toolu_1"))), Token);
        var (refused, denied) = await server.CallAsync(Assert.IsType<AgentToolServerMessage.Call>(server.Read(Call(AgentTools.WebFetch, new JsonObject { ["url"] = "https://refused.test/" }, "toolu_2", id: 3))), Token);

        Assert.False(page["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("the page's text", page["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(("toolu_1", true), (read!.Id, read.Ok));

        Assert.True(refused["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(3, refused["id"]!.GetValue<int>());
        Assert.Contains("Web(refused.test)", refused["result"]!["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(denied!.Ok);

        Assert.Equal("example.test", Assert.Single(endpoint.Asked).Address.Host);
        Assert.Equal((2, 1, 2), (runtime.Tally.Calls, runtime.Tally.Denials, runtime.Tally.WebRequests));
        Assert.Single(_events.All.OfType<PermissionDenied>());
        Assert.Empty(_events.All.OfType<ToolCallFinished>());
    }

    /// <summary>A call its client cancels is answered as cancelled: MCP's code, and no result of the runtime's.</summary>
    [Fact]
    public async Task ACallCancelled_IsAnsweredAsCancelled()
    {
        var (server, _) = Serve(endpoint: new WebEndpoint(_ => WebEndpoint.Page("the page's text", "text/plain")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var (reply, result) = await server.CallAsync(Assert.IsType<AgentToolServerMessage.Call>(server.Read(Call(AgentTools.WebFetch, new JsonObject { ["url"] = "https://example.test/" }, "toolu_1"))), cancelled.Token);

        Assert.Null(result);
        Assert.Equal(
            """{"jsonrpc":"2.0","id":2,"error":{"code":-32800,"message":"the call of 'web_fetch' was cancelled"}}""",
            Json(reply));
    }

    /// <summary>A call the engine never allowed is answered as an error the model reads, with why, and nothing run.</summary>
    [Fact]
    public void ACallRefused_IsAnErrorSayingWhy()
    {
        var server = Server();
        var call = Assert.IsType<AgentToolServerMessage.Call>(server.Read(Call(AgentTools.WebFetch, new JsonObject { ["url"] = "https://example.test/" }, null)));

        Assert.Equal(
            """{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"not allowed"}],"isError":true}}""",
            AgentToolServer.Refused(call, "not allowed").ToJsonString());
    }

    /// <summary>
    /// The runtime a launch is served from writes to the launch's own records and leaves them open when it is closed: the
    /// launch's records are its client's, closed with the launch.
    /// </summary>
    [Fact]
    public async Task AServedRuntime_LeavesTheLaunchsRecordsOpen()
    {
        var (_, runtime) = Serve();
        var records = new AgentCallRecords(_harness.FileSystem, AgentRuns.Of(_step, _worktree), TimeProvider.System, _events);
        _opened.Add(records);
        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        _opened.Add(fetch);
        var host = new AgentToolHost(_harness.FileSystem, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System);
        var served = host.Serve(AgentRuns.Of(_step, _worktree, ground: PolicyGrounds.Of(_worktree, [_worktree], [_worktree], [], [], [])), _events, records, [AgentTools.WebFetch]);

        // A line written while the runtime is open, and one after it is closed: the file the records opened stays open.
        await records.DeniedAsync("web_fetch", "https://example.test/before", new AgentPolicyAnswer.Denied("r", AgentPolicySource.Builtin, "why"));
        served.Dispose();
        await records.DeniedAsync("web_fetch", "https://example.test/after", new AgentPolicyAnswer.Denied("r", AgentPolicySource.Builtin, "why"));

        // Read as a reader beside the records' one writer reads them.
        using (var reader = new StreamReader(new FileStream(Path.Combine(_step, AgentCallRecords.DenialsFileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            Assert.Equal(2, reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        }

        Assert.Equal([AgentTools.WebFetch], served.Tools.Select(tool => tool.Name));
        Assert.Equal([AgentTools.WebFetch], runtime.Tools.Select(tool => tool.Name));
    }

    /// <summary>A server over a runtime of the web's tools, with a source to search where <paramref name="search"/> is given.</summary>
    private AgentToolServer Server(AgentWebSearch? search = null) => Serve(search).Server;

    /// <summary>A server over a runtime of the web's tools, answering from <paramref name="endpoint"/>, held to <paramref name="restrictions"/>.</summary>
    private (AgentToolServer Server, AgentToolRuntime Runtime) Serve(AgentWebSearch? search = null, WebEndpoint? endpoint = null, IReadOnlyList<AgentRestriction>? restrictions = null)
    {
        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, endpoint ?? new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var files = _harness.FileSystem;
        var searching = search is null ? null : new DssHarness.Core.Web.Search.HarnessWebSearch(
            DssHarness.Core.Web.Search.WebSearchKinds.OfThisBuild,
            fetch,
            new DssHarness.Core.Web.Index.WebIndexes(files, new DssHarness.Core.Web.Index.SitemapFill(files, fetch, TimeProvider.System, (_, _) => Task.CompletedTask), TimeProvider.System),
            new DssHarness.Core.Web.Search.WebSearchPace(files, () => Path.Combine(_temp.Path, "pace.json"), TimeProvider.System, (_, _) => Task.CompletedTask),
            files,
            TimeProvider.System);
        var host = new AgentToolHost(files, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, files, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System, search: searching);
        var ground = PolicyGrounds.Of(_worktree, [_worktree], [_worktree], [], [], restrictions ?? []);
        var run = AgentRuns.Of(_step, _worktree, ground: ground, webSearch: search);
        var records = new AgentCallRecords(files, run, TimeProvider.System, _events);
        var runtime = host.Serve(run, _events, records, [AgentTools.WebFetch, AgentTools.WebSearch]);

        _opened.Add(fetch);
        _opened.Add(records);
        _opened.Add(runtime);

        if (searching is not null)
        {
            _opened.Add(searching);
        }

        return (new AgentToolServer(runtime), runtime);
    }

    /// <summary>The step's search of the source <c>alpha</c>.</summary>
    private static AgentWebSearch Search()
        => new(
            [WebSearchFixtures.Source("alpha", """{ "kind": "json-api", "description": "Alpha's answers", "url": "https://alpha.example.test/?q={query}", "items": "$.items", "title": "{@.title}", "link": "{@.link}" }""")],
            DssHarness.Core.Web.Search.WebSearchSources.DefaultResultsPerSource,
            60,
            new Dictionary<string, DssHarness.Core.Web.Search.WebSearchKey>());

    /// <summary>A request of the client's.</summary>
    private static JsonObject Message(string method, JsonNode id, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["method"] = method, ["jsonrpc"] = "2.0", ["id"] = id };

        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return message;
    }

    /// <summary>A call of <paramref name="tool"/>, naming the call it was asked as where one is given, as Claude Code writes one.</summary>
    private static JsonObject Call(string tool, JsonObject arguments, string? callId, int id = 2)
    {
        var parameters = new JsonObject { ["name"] = tool, ["arguments"] = arguments };

        if (callId is not null)
        {
            parameters["_meta"] = new JsonObject { [AgentToolServer.CallIdMeta] = callId, ["progressToken"] = id };
        }

        return Message("tools/call", id, parameters);
    }

    /// <summary>The reply of a message answered at once.</summary>
    private static JsonObject Answered(AgentToolServerMessage message) => Assert.IsType<AgentToolServerMessage.Answered>(message).Reply;

    /// <summary>A reply as JSON, its text unescaped.</summary>
    private static string Json(JsonNode reply) => reply.ToJsonString(DssHarness.Core.FileSystem.JsonStateFile.LineOptions);

    /// <summary>The error code of a message answered at once with one.</summary>
    private static int Code(AgentToolServerMessage message) => Answered(message)["error"]!["code"]!.GetValue<int>();
}
