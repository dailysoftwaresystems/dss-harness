using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.ZaiApi;

namespace DssHarness.Tests;

/// <summary>
/// The Z.AI probe (slice 25): what Z.AI's chat completions say that its reference leaves out - the streamed chunk, a
/// call's arguments as they stream, the reasoning, where the usage and the finish reason come, each refusal's body - asked
/// live once, so the fixtures the glm client's tests replay are what the API sends. Person-run alone: skipped unless
/// <see cref="SlotVariable"/> names a checkout whose key slot holds the template's Z.AI key, read by the key store
/// under the name the glm client reads. Each scenario's requests and answers are recorded as the wire carried them -
/// never a header, so never the key - into the directory <see cref="LiveRecords.RecordsVariable"/> names, for the
/// fixtures to be curated from; nothing raw is committed. It holds each scenario only to having been answered.
/// </summary>
public sealed class ZaiApiProbeTests
{
    /// <summary>The variable naming the checkout whose key slot signs the probe's requests; the probe runs only where it is set.</summary>
    public const string SlotVariable = "DSSHARNESS_TESTS_LIVE_ZAI";

    private const string Tools = """[{"type":"function","function":{"name":"view_file","description":"Reads a file of the worktree and gives its text.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"The file's path, relative to the worktree."}},"required":["path"]}}}]""";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Every scenario of the probe list is asked and answered: an answer, a call, the call's result sent back, a call
    /// that reasoned first and its result sent back with the reasoning kept across turns, two calls in one turn, a call no
    /// result follows, an answer and a call cut off at their length, each reasoning effort the reference names, a key
    /// refused, a model unknown, and a long prefix asked twice for what the cache reads.
    /// </summary>
    [Fact]
    public async Task EachScenario_IsAnsweredAndRecorded()
    {
        var root = Environment.GetEnvironmentVariable(SlotVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{SlotVariable} is not set: the live probe runs only where the person said it might.");

        var (zai, key) = LiveApiRun.Signed(new HarnessFactory(), root!, "zai", ZaiApiClient.ClientName);
        var model = zai.Models[0].Code;

        using var temp = new TempDirectory();
        var wire = Directory.CreateDirectory(temp.Combine("wire")).FullName;
        var said = new StringBuilder();

        async Task<List<ApiEvent>> Asked(string scenario, JsonObject body, string? signedWith = null)
        {
            using var connection = new ApiConnection(TimeProvider.System, new WireRecording(ApiConnection.Connecting(), Path.Combine(wire, scenario)));
            var events = new List<ApiEvent>();

            await foreach (var happened in connection.PostAsync(
                new ApiPost(ZaiApiRequest.Endpoint(zai.Api), [new("Authorization", $"Bearer {signedWith ?? key.Value}")], Encoding.UTF8.GetBytes(body.ToJsonString())),
                Token))
            {
                events.Add(happened);
            }

            said.AppendLine($"{scenario}: {events.Count} events, the last {events.LastOrDefault() switch { ApiEvent.Refused refused => $"Refused {refused.Status}", var last => last?.GetType().Name }}");
            return events;
        }

        JsonObject Body(JsonArray messages, string effort = "low", int? maxTokens = null, bool tools = true, JsonObject? thinking = null, string? named = null)
        {
            var body = new JsonObject
            {
                ["model"] = named ?? model,
                ["messages"] = messages,
                ["stream"] = true,
                ["tool_stream"] = true,
                ["thinking"] = thinking ?? new JsonObject { ["type"] = "enabled" },
                ["reasoning_effort"] = effort,
            };

            if (tools)
            {
                body["tools"] = JsonNode.Parse(Tools);
            }

            if (maxTokens is { } most)
            {
                body["max_tokens"] = most;
            }

            return body;
        }

        JsonObject System() => new() { ["role"] = "system", ["content"] = "You are a careful agent. Do exactly what you are asked, and nothing more." };
        JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };

        const string ReadIt = "Use the view_file tool to read the file a.txt, then give as your answer the one word it holds.";

        await Asked("answer", Body([System(), User("Give as your answer the word alpha, and nothing else.")]));
        var called = Assistant(await Asked("tool-use", Body([System(), User(ReadIt)])));
        var callId = called["tool_calls"]?[0]?["id"]?.GetValue<string>() ?? "call_none";
        JsonArray Answered() => [System(), User(ReadIt), called.DeepClone(), new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = "alpha\n" }];
        await Asked("tool-result", Body(Answered()));

        // A call made at a high effort reasons first: its reasoning goes back with its result, kept across turns where asked.
        var reasoned = Assistant(await Asked("tool-use-reasoning", Body([System(), User(ReadIt)], "high")));
        var reasonedId = reasoned["tool_calls"]?[0]?["id"]?.GetValue<string>() ?? "call_none";
        await Asked(
            "tool-result-reasoning-kept",
            Body([System(), User(ReadIt), reasoned, new JsonObject { ["role"] = "tool", ["tool_call_id"] = reasonedId, ["content"] = "alpha\n" }], "high", thinking: new JsonObject { ["type"] = "enabled", ["clear_thinking"] = false }));
        await Asked("two-calls", Body([System(), User("Use the view_file tool to read the files a.txt and b.txt, both in one turn, then give as your answer the two words they hold.")]));
        await Asked("no-result", Body([System(), User(ReadIt), called.DeepClone(), User("Go on.")]));
        await Asked("cut-off", Body([System(), User("Write a story of three paragraphs about a lighthouse.")], maxTokens: 24));
        await Asked("cut-off-call", Body([System(), User("Use the view_file tool to read the file docs/a-rather-long-directory-name/another-long-directory-name/the-file-with-a-long-name.txt.")], maxTokens: 24));

        foreach (var effort in new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" })
        {
            await Asked($"effort-{effort}", Body([System(), User("Give as your answer the word ok, and nothing else.")], effort, maxTokens: 400, tools: false));
        }

        await Asked("key-refused", Body([System(), User("Say ok.")], tools: false), signedWith: "00000000000000000000000000000000.notakey0000000000");
        await Asked("model-unknown", Body([System(), User("Say ok.")], tools: false, named: "glm-0"));

        var prefix = string.Concat(Enumerable.Repeat("The harness keeps every step's records beside the run, one file a step, in the order the steps ran. ", 160));
        await Asked("cached-first", Body([new JsonObject { ["role"] = "system", ["content"] = prefix }, User("Give as your answer the word ok, and nothing else.")], tools: false));
        await Asked("cached-again", Body([new JsonObject { ["role"] = "system", ["content"] = prefix }, User("Give as your answer the word ok, and nothing else.")], tools: false));

        LiveRecords.Copy((wire, "wire"));
        TestContext.Current.SendDiagnosticMessage(said.ToString());

        Assert.Equal(20, Directory.GetDirectories(wire).Length);
    }

    /// <summary>The assistant's message a streamed answer holds, put together the way the reference's own sample does: each call's arguments by its index.</summary>
    private static JsonObject Assistant(List<ApiEvent> events)
    {
        var (content, reasoning) = (new StringBuilder(), new StringBuilder());
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Arguments)>();

        foreach (var data in events.OfType<ApiEvent.Item>().Where(item => item.Data != "[DONE]").Select(item => JsonNode.Parse(item.Data)))
        {
            if (data?["choices"]?[0]?["delta"] is not JsonObject delta)
            {
                continue;
            }

            content.Append(delta["content"]?.GetValue<string>());
            reasoning.Append(delta["reasoning_content"]?.GetValue<string>());

            foreach (var call in delta["tool_calls"]?.AsArray() ?? [])
            {
                var at = call!["index"]?.GetValue<int>() ?? 0;
                var (id, name, arguments) = calls.TryGetValue(at, out var known) ? known : (null, null, new StringBuilder());
                arguments.Append(call["function"]?["arguments"]?.GetValue<string>());
                calls[at] = (id ?? call["id"]?.GetValue<string>(), name ?? call["function"]?["name"]?.GetValue<string>(), arguments);
            }
        }

        var message = new JsonObject { ["role"] = "assistant", ["content"] = content.ToString(), ["reasoning_content"] = reasoning.ToString() };

        if (calls.Count > 0)
        {
            message["tool_calls"] = new JsonArray([.. calls.Values.Select(call => (JsonNode)new JsonObject
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments.ToString() },
            })]);
        }

        return message;
    }
}
