using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeApi;

namespace DssHarness.Tests;

/// <summary>
/// The claude-api client's smoke (slice 24b): the client itself against the Claude API, signed with a key of the
/// person's own slot - which the suite skips unless the person names that slot, for the key is theirs and each request
/// is billed. It runs two steps in a scratch tree: one on a cheap model whose call the harness runs, asked at a high
/// effort so its thinking is likely in the conversation, and one that goes on in that step's conversation on another
/// model, the conversation sent on as it was sent - byte for byte, thinking and all.
/// </summary>
/// <remarks>
/// Set <see cref="SlotVariable"/> to a checkout whose key slot (<c>.harness-config/workflows/.env/</c>, kept from every
/// user but its owner) holds the template's Anthropic key, <c>ANTHROPIC_API_KEY</c>; the key is read by the key store
/// alone, never from the process's environment (<see cref="LiveApiRun"/>). <see cref="LiveRecords.RecordsVariable"/> names
/// a directory each step's records and every request's body and answer, as the wire carried them, are copied to - never a
/// header, so never the key - for the person to read; nothing of it is committed. A continuous-integration runner never
/// sets the variable: it holds no key and must not.
/// </remarks>
public sealed class ClaudeApiClientLiveTests
{
    /// <summary>The variable naming the checkout whose key slot signs the smoke's requests; the smoke runs only where it is set.</summary>
    public const string SlotVariable = "DSSHARNESS_TESTS_LIVE_CLAUDE_API";

    /// <summary>The model the conversation begins on.</summary>
    private const string First = "claude-haiku-5-5";

    /// <summary>The model the conversation goes on with.</summary>
    private const string Next = "claude-sonnet-5-5";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A step on the API completes through the client: its read of a file is run by the harness, and its answer holds
    /// what the file does. A second step goes on in that conversation on another model: its first request sends every
    /// message the first step's last request sent, as sent, and the first step's answer after them; it names the other
    /// model; and the answer it gets reads the first step's.
    /// </summary>
    [Fact]
    public async Task AConversation_BegunOnOneModel_GoesOnWithAnother_AsItIs()
    {
        var root = Environment.GetEnvironmentVariable(SlotVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{SlotVariable} is not set: the live smoke runs only where the person said it might.");

        using var run = new LiveApiRun(root!, "anthropic", ClaudeApiClient.ClientName, (harness, tools, connection) => new ClaudeApiClient(harness.FileSystem, tools, TimeProvider.System, connection));

        var begun = await run.StepAsync("s1", First, "high", "Use the view_file tool to read the file a.txt. Then work out how many of the letters of the one word it holds are vowels, and give as your answer that word and that number.", null, null, Token);
        var sentFirst = run.Requests().Length;
        var goneOn = begun.Session is { } session
            ? await run.StepAsync("s2", Next, "low", "Now give as your answer the word you read written backwards, and nothing else.", session, null, Token)
            : null;

        run.Keep();
        var thought = run.Requests().Length > sentFirst ? ClaudeApiClientTests.Messages(File.ReadAllText(run.Requests()[sentFirst])).Sum(message => JsonNode.Parse(message)!["content"] is JsonArray blocks ? blocks.Count(block => block?["type"]?.GetValue<string>() is "thinking" or "redacted_thinking") : 0) : 0;
        TestContext.Current.SendDiagnosticMessage(
            $"claude-api {First} then {Next}: {run.Requests().Length} requests, {thought} thinking blocks sent on; spent {begun.Usage?.Cost?.Counted} + {goneOn?.Usage?.Cost?.Counted} USD as the template's prices count it");

        Assert.True(begun.Status == AgentRunStatus.Completed, $"{begun.Status}: {begun.Error}");
        Assert.Contains("alpha", begun.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.True(begun.ToolCalls >= 1, "the step made no call");
        Assert.NotNull(goneOn);
        Assert.True(goneOn.Status == AgentRunStatus.Completed, $"{goneOn.Status}: {goneOn.Error}");
        Assert.Contains("ahpla", goneOn.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(begun.Session, goneOn.Session);

        var (last, sentOn) = (File.ReadAllText(run.Requests()[sentFirst - 1]), File.ReadAllText(run.Requests()[sentFirst]));
        var (before, after) = (ClaudeApiClientTests.Messages(last), ClaudeApiClientTests.Messages(sentOn));

        Assert.Equal((First, Next), (JsonNode.Parse(last)!["model"]!.GetValue<string>(), JsonNode.Parse(sentOn)!["model"]!.GetValue<string>()));
        Assert.Equal(before, after.Take(before.Count));

        // The first step's answer goes on as the API gave it: its text, and its thinking, where it thought, whole with its signature.
        var answered = File.ReadLines(Path.Combine(run.Wire, $"{sentFirst.ToString("D2", CultureInfo.InvariantCulture)}-answer.sse"))
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line["data:".Length..])!)
            .Where(data => data["type"]!.GetValue<string>() == "content_block_delta")
            .Select(data => data["delta"]!)
            .ToList();
        var sentBack = JsonNode.Parse(after[before.Count])!;
        string Answered(string type, string member) => string.Concat(answered.Where(delta => delta["type"]!.GetValue<string>() == type).Select(delta => delta[member]!.GetValue<string>()));
        string[] Blocks(string type, string member) => [.. sentBack["content"]!.AsArray().Where(block => block!["type"]!.GetValue<string>() == type).Select(block => block![member]!.GetValue<string>())];

        Assert.Equal("assistant", sentBack["role"]!.GetValue<string>());
        Assert.Equal(Answered("text_delta", "text"), string.Concat(Blocks("text", "text")));
        Assert.Equal(
            Answered("signature_delta", "signature") is { Length: > 0 } signature ? [(Answered("thinking_delta", "thinking"), signature)] : [],
            Blocks("thinking", "thinking").Zip(Blocks("thinking", "signature")));
    }
}
