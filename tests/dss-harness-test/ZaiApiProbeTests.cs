using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.ZaiApi;
using DssHarness.Core.Configuration;

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

    private const string ReadIt = "Use the view_file tool to read the file a.txt, then give as your answer the one word it holds.";

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
        using var probe = Probe.Begun();
        var asked = probe.Asked;

        await asked("answer", probe.Body([System(), User("Give as your answer the word alpha, and nothing else.")]), null);
        var called = Assistant(await asked("tool-use", probe.Body([System(), User(ReadIt)]), null));
        var callId = called["tool_calls"]?[0]?["id"]?.GetValue<string>() ?? "call_none";
        JsonArray Answered() => [System(), User(ReadIt), called.DeepClone(), new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = "alpha\n" }];
        await asked("tool-result", probe.Body(Answered()), null);

        // A call made at a high effort reasons first: its reasoning goes back with its result, kept across turns where asked.
        var reasoned = Assistant(await asked("tool-use-reasoning", probe.Body([System(), User(ReadIt)], "high"), null));
        var reasonedId = reasoned["tool_calls"]?[0]?["id"]?.GetValue<string>() ?? "call_none";
        await asked(
            "tool-result-reasoning-kept",
            probe.Body([System(), User(ReadIt), reasoned, new JsonObject { ["role"] = "tool", ["tool_call_id"] = reasonedId, ["content"] = "alpha\n" }], "high", thinking: new JsonObject { ["type"] = "enabled", ["clear_thinking"] = false }),
            null);
        await asked("two-calls", probe.Body([System(), User("Use the view_file tool to read the files a.txt and b.txt, both in one turn, then give as your answer the two words they hold.")]), null);
        await asked("no-result", probe.Body([System(), User(ReadIt), called.DeepClone(), User("Go on.")]), null);
        await asked("cut-off", probe.Body([System(), User("Write a story of three paragraphs about a lighthouse.")], maxTokens: 24), null);
        await asked("cut-off-call", probe.Body([System(), User("Use the view_file tool to read the file docs/a-rather-long-directory-name/another-long-directory-name/the-file-with-a-long-name.txt.")], maxTokens: 24), null);

        foreach (var effort in new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" })
        {
            await asked($"effort-{effort}", probe.Body([System(), User("Give as your answer the word ok, and nothing else.")], effort, maxTokens: 400, tools: false), null);
        }

        await asked("key-refused", probe.Body([System(), User("Say ok.")], tools: false), "00000000000000000000000000000000.notakey0000000000");
        await asked("model-unknown", probe.Body([System(), User("Say ok.")], tools: false, named: "glm-0"), null);

        var prefix = string.Concat(Enumerable.Repeat("The harness keeps every step's records beside the run, one file a step, in the order the steps ran. ", 160));
        await asked("cached-first", probe.Body([new JsonObject { ["role"] = "system", ["content"] = prefix }, User("Give as your answer the word ok, and nothing else.")], tools: false), null);
        await asked("cached-again", probe.Body([new JsonObject { ["role"] = "system", ["content"] = prefix }, User("Give as your answer the word ok, and nothing else.")], tools: false), null);

        Assert.Equal(20, probe.Kept());
    }

    /// <summary>
    /// The turn the glm client asks an answer that read as no JSON again in (slice 26): with the conversation's calls and
    /// their answers in its history, the tools left out and <c>response_format: json_object</c>, the model answers one
    /// JSON object; and the same with the tools sent, for what Z.AI makes of the two together.
    /// </summary>
    [Fact]
    public async Task AnAnswerAskedInJsonAlone_IsAnsweredAndRecorded()
    {
        using var probe = Probe.Begun();
        var asked = probe.Asked;
        const string Schema = """{"type":"object","additionalProperties":false,"required":["word","vowels"],"properties":{"word":{"type":"string"},"vowels":{"type":"integer"}}}""";
        var task = $"{ReadIt} Answer with one JSON object, and nothing else, that reads against this JSON schema:\n{Schema}";

        var called = Assistant(await asked("json-tool-use", probe.Body([System(), User(task)]), null));
        var callId = called["tool_calls"]?[0]?["id"]?.GetValue<string>() ?? "call_none";
        JsonArray History() =>
        [
            System(), User(task), called.DeepClone(), new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = "alpha\n" },
            new JsonObject { ["role"] = "assistant", ["content"] = "The word is alpha, and it has two vowels." },
            User("Your answer does not read against the schema it was asked for:\n- $: the answer holds no JSON object; answer with one, as the schema says\nAnswer again, whole, as the schema says."),
        ];
        JsonObject InJson(JsonObject body)
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
            return body;
        }

        await asked("json-alone", InJson(probe.Body(History(), tools: false)), null);
        await asked("json-alone-reasoning-kept", InJson(probe.Body(History(), tools: false, thinking: new JsonObject { ["type"] = "enabled", ["clear_thinking"] = false })), null);
        await asked("json-with-tools", InJson(probe.Body(History())), null);

        Assert.Equal(4, probe.Kept());
    }

    private static JsonObject System() => new() { ["role"] = "system", ["content"] = "You are a careful agent. Do exactly what you are asked, and nothing more." };

    private static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };

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

    /// <summary>One probe's requests: signed with the slot's key, each recorded under its scenario, what each ended with said.</summary>
    private sealed class Probe : IDisposable
    {
        private readonly AgentProviderConfig _zai;
        private readonly AgentKey _key;
        private readonly TempDirectory _temp = new();
        private readonly string _wire;
        private readonly StringBuilder _said = new();

        private Probe(AgentProviderConfig zai, AgentKey key)
        {
            (_zai, _key) = (zai, key);
            _wire = Directory.CreateDirectory(_temp.Combine("wire")).FullName;
        }

        /// <summary>The model every scenario asks unless it names another: the template's first of Z.AI's.</summary>
        public string Model => _zai.Models[0].Code;

        /// <summary>The probe, signed with the slot's key; the test is skipped where the person named no slot.</summary>
        public static Probe Begun()
        {
            var root = Environment.GetEnvironmentVariable(SlotVariable);
            Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{SlotVariable} is not set: the live probe runs only where the person said it might.");

            var (zai, key) = LiveApiRun.Signed(new HarnessFactory(), root!, "zai", ZaiApiClient.ClientName);
            return new Probe(zai, key);
        }

        /// <summary>Asks one scenario, signed with the slot's key or with <c>signedWith</c>, and records it.</summary>
        public Func<string, JsonObject, string?, Task<List<ApiEvent>>> Asked => AskedAsync;

        /// <summary>A request's body: the conversation, streamed with its calls, thinking at an effort, the tools unless left out.</summary>
        public JsonObject Body(JsonArray messages, string effort = "low", int? maxTokens = null, bool tools = true, JsonObject? thinking = null, string? named = null)
        {
            var body = new JsonObject
            {
                ["model"] = named ?? Model,
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

        /// <summary>Copies what the scenarios recorded where the person asked, says what each ended with, and counts them.</summary>
        public int Kept()
        {
            LiveRecords.Copy((_wire, "wire"));
            TestContext.Current.SendDiagnosticMessage(_said.ToString());

            return Directory.GetDirectories(_wire).Length;
        }

        public void Dispose() => _temp.Dispose();

        private async Task<List<ApiEvent>> AskedAsync(string scenario, JsonObject body, string? signedWith)
        {
            using var connection = new ApiConnection(TimeProvider.System, new WireRecording(ApiConnection.Connecting(), Path.Combine(_wire, scenario)));
            var events = new List<ApiEvent>();

            await foreach (var happened in connection.PostAsync(
                new ApiPost(ZaiApiRequest.Endpoint(_zai.Api), [new("Authorization", $"Bearer {signedWith ?? _key.Value}")], Encoding.UTF8.GetBytes(body.ToJsonString())),
                Token))
            {
                events.Add(happened);
            }

            _said.AppendLine($"{scenario}: {events.Count} events, the last {events.LastOrDefault() switch { ApiEvent.Refused refused => $"Refused {refused.Status}", var last => last?.GetType().Name }}");
            return events;
        }
    }
}
