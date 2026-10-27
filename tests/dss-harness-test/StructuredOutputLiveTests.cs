using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeApi;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.ZaiApi;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The envelope live (slice 26), person-run alone, each test skipped unless the person names the key slot of its
/// provider (<see cref="ClaudeApiClientLiveTests.SlotVariable"/>, <see cref="ZaiApiProbeTests.SlotVariable"/>): the copy of
/// each shipped schema's envelope the Claude API is sent is one it takes, and a step held to an envelope completes
/// through each API client, its notes kept. What the wire carried is copied where <see cref="LiveRecords.RecordsVariable"/>
/// names - never a header, so never a key; nothing of it is committed.
/// </summary>
public sealed class StructuredOutputLiveTests
{
    /// <summary>The model the Claude API is asked on: its cheapest.</summary>
    private const string Haiku = "claude-haiku-5-5";

    /// <summary>A step's schema with what structured outputs do not take - a list of at least two - and an enum.</summary>
    private const string Schema = """{"type":"object","additionalProperties":false,"required":["vowels","letters"],"properties":{"vowels":{"type":"integer"},"letters":{"type":"array","minItems":2,"description":"The word's letters, one each.","items":{"type":"string"}},"case":{"enum":["lower","upper"]}}}""";

    private const string Task = "Use the view_file tool to read the file a.txt. Answer with how many vowels the one word it holds has, and its letters.";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Every shipped schema's envelope is answered on the Claude API, as a step that may ask and as one that may not: as
    /// the format in the copy it is sent - where the API compiles it - or else, the schema refused, in words in a message
    /// after the conversation's, as the loop goes on (2026-10-26: the plan's, with questions offered, is more than
    /// structured outputs compile). None fails; the plan's that offers no questions is taken as the format, its work item
    /// one definition. Each answer is held to a few tokens, for only the taking is asked.
    /// </summary>
    [Fact]
    public async Task EachShippedEnvelope_IsAnsweredOnTheClaudeApi_AsTheFormatOrInWords()
    {
        var root = Environment.GetEnvironmentVariable(ClaudeApiClientLiveTests.SlotVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{ClaudeApiClientLiveTests.SlotVariable} is not set: the live check runs only where the person said it might.");

        var (anthropic, key) = LiveApiRun.Signed(new HarnessFactory(), root!, "anthropic", ClaudeApiClient.ClientName);
        using var temp = new TempDirectory();
        var wire = Directory.CreateDirectory(temp.Combine("wire")).FullName;
        var failed = new List<string>();
        var inWords = new List<string>();
        var said = new StringBuilder();

        foreach (var file in WorkflowTemplates.Files.Where(file => file.StartsWith("schemas/", StringComparison.Ordinal)))
        {
            foreach (var rounds in new int?[] { null, 1 })
            {
                var name = $"{Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file))}{(rounds is null ? string.Empty : "-asking")}";
                using var connection = new ApiConnection(TimeProvider.System, new WireRecording(ApiConnection.Connecting(), Path.Combine(wire, name)));
                var provider = new ClaudeApiProvider(connection, anthropic.Api, ClaudeApiSettings.Of(null));
                var request = new ChatRequest(
                    Haiku,
                    null,
                    "You are a careful agent.",
                    "[]",
                    [provider.UserMessage("Answer with the shortest answer the schema takes.")],
                    64,
                    key,
                    AgentEnvelope.Schema(new AgentOutput("o", WorkflowTemplates.TextOf(file)!, AskRounds: rounds)));
                var parts = await Parts(provider, request);

                // The schema refused is the loop's to answer: the request sent again with none, the envelope in words after it.
                if (parts[^1] is ChatPart.Failed { Failure.Kind: ChatFailureKind.SchemaRefused })
                {
                    inWords.Add(name);
                    parts = await Parts(provider, request with { Schema = null, Messages = [.. request.Messages, provider.UserMessage(AgentEnvelope.InWords(request.Schema!))] });
                }

                said.AppendLine($"{name}: {parts[^1].GetType().Name}{(inWords.Contains(name) ? ", in words" : string.Empty)}");

                if (parts[^1] is ChatPart.Failed { Failure: var failure })
                {
                    failed.Add($"{name}: {failure.Message}");
                }
            }
        }

        LiveRecords.Copy((wire, "wire"));
        TestContext.Current.SendDiagnosticMessage(said.ToString());

        Assert.Empty(failed);
        Assert.DoesNotContain("plan", inWords);
    }

    /// <summary>
    /// A step held to an envelope completes on the Claude API: the harness ran its read, the answer read against the step's
    /// whole schema - its two letters at least among it - and its notes are kept beside the result.
    /// </summary>
    [Fact]
    public async Task AStepHeldToItsEnvelope_CompletesOnTheClaudeApi()
    {
        var root = Environment.GetEnvironmentVariable(ClaudeApiClientLiveTests.SlotVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{ClaudeApiClientLiveTests.SlotVariable} is not set: the live smoke runs only where the person said it might.");

        using var run = new LiveApiRun(root!, "anthropic", ClaudeApiClient.ClientName, (harness, tools, connection) => new ClaudeApiClient(harness.FileSystem, tools, TimeProvider.System, connection));

        await Completed(run, Haiku);
    }

    /// <summary>
    /// A step held to an envelope completes on Z.AI, told the envelope in words: the harness ran its read, the answer read
    /// against the step's whole schema, and its notes are kept beside the result.
    /// </summary>
    [Fact]
    public async Task AStepHeldToItsEnvelope_CompletesOnZai()
    {
        var root = Environment.GetEnvironmentVariable(ZaiApiProbeTests.SlotVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{ZaiApiProbeTests.SlotVariable} is not set: the live smoke runs only where the person said it might.");

        using var run = new LiveApiRun(root!, "zai", ZaiApiClient.ClientName, (harness, tools, connection) => new ZaiApiClient(harness.FileSystem, tools, TimeProvider.System, connection));

        await Completed(run, run.Provider.Models[0].Code);
    }

    /// <summary>Works the step on <paramref name="model"/> and holds it to having completed, its output read and its notes kept.</summary>
    private static async Task Completed(LiveApiRun run, string model)
    {
        var result = await run.StepAsync("s1", model, "low", Task, null, null, Token, new AgentOutput("o", Schema));
        run.Keep();
        TestContext.Current.SendDiagnosticMessage($"{run.Client} {model}: {run.Requests().Length} requests, {result.Status}; spent {result.Usage?.Cost?.Counted} USD as the template's prices count it");

        Assert.True(result.Status == AgentRunStatus.Completed, $"{result.Status}: {result.Error}");
        Assert.True(result.ToolCalls >= 1, "the step made no call");
        var output = JsonNode.Parse(result.StructuredOutput!)!;
        Assert.Equal(System.Text.Json.JsonValueKind.Number, output["vowels"]!.GetValueKind());
        Assert.Equal(["a", "l", "p", "h", "a"], output["letters"]!.AsArray().Select(letter => letter!.GetValue<string>().ToLowerInvariant()));
        Assert.False(string.IsNullOrWhiteSpace(result.Notes));
        Assert.Equal(result.Notes, File.ReadAllText(Path.Combine(run.StepDirectory("s1"), OrchestratorLayout.StepNotesFileName)));
    }

    /// <summary>Every part <paramref name="provider"/> answers <paramref name="request"/> with, in order.</summary>
    private static async Task<List<ChatPart>> Parts(ClaudeApiProvider provider, ChatRequest request)
    {
        var parts = new List<ChatPart>();

        await foreach (var part in provider.SendAsync(request, Token))
        {
            parts.Add(part);
        }

        return parts;
    }
}
