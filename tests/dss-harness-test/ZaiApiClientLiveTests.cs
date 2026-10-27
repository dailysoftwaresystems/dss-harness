using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ZaiApi;
using DssHarness.Core.Configuration;

namespace DssHarness.Tests;

/// <summary>
/// The glm client's smoke (slice 25): the client itself against Z.AI, signed with a key of the person's own slot - which
/// the suite skips unless the person names that slot, for the key is theirs and each request is billed. It runs two steps
/// in a scratch tree on the template's model: one whose call the harness runs, asked at a high effort so its reasoning is
/// in the conversation, and one that goes on in that step's conversation with the reasoning kept across turns, the
/// conversation sent on as it was sent - byte for byte, reasoning and all.
/// </summary>
/// <remarks>
/// Set <see cref="SlotVariable"/> to a checkout whose key slot (<c>.harness-config/workflows/.env/</c>, kept from every
/// user but its owner) holds the template's Z.AI key, <c>ZAI_API_KEY</c>; the key is read by the key store alone, never
/// from the process's environment (<see cref="LiveApiRun"/>). <see cref="LiveRecords.RecordsVariable"/> names a directory
/// each step's records and every request's body and answer are copied to, for the person to read; nothing of it is
/// committed. A continuous-integration runner never sets the variable: it holds no key and must not.
/// </remarks>
public sealed class ZaiApiClientLiveTests
{
    /// <summary>The variable naming the checkout whose key slot signs the smoke's requests: the probe's.</summary>
    public const string SlotVariable = ZaiApiProbeTests.SlotVariable;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A step on Z.AI completes through the client: its read of a file is run by the harness, and its answer holds what the
    /// file does. A second step goes on in that conversation, its entry keeping the reasoning across turns: its first
    /// request sends every message the first step's last request sent, as sent, and the first step's answer after them -
    /// its text and its reasoning as Z.AI streamed them - and the answer it gets reads the first step's.
    /// </summary>
    [Fact]
    public async Task AConversation_GoesOnAsItIs_ItsReasoningKept()
    {
        var root = Environment.GetEnvironmentVariable(SlotVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(root), $"{SlotVariable} is not set: the live smoke runs only where the person said it might.");

        using var run = new LiveApiRun(root!, "zai", ZaiApiClient.ClientName, (harness, tools, connection) => new ZaiApiClient(harness.FileSystem, tools, TimeProvider.System, connection));
        var model = run.Provider.Models[0].Code;

        var begun = await run.StepAsync("s1", model, "high", "Use the view_file tool to read the file a.txt. Then work out how many of the letters of the one word it holds are vowels, and give as your answer that word and that number.", null, null, Token);
        var sentFirst = run.Requests().Length;
        var goneOn = begun.Session is { } session
            ? await run.StepAsync("s2", model, "low", "Now give as your answer the word you read written backwards, and nothing else.", session, new AgentClientConfig { PreserveThinking = true }, Token)
            : null;

        run.Keep();
        TestContext.Current.SendDiagnosticMessage(
            $"glm {model}: {run.Requests().Length} requests; spent {begun.Usage?.Cost?.Counted} + {goneOn?.Usage?.Cost?.Counted} USD as the template's prices count it");

        Assert.True(begun.Status == AgentRunStatus.Completed, $"{begun.Status}: {begun.Error}");
        Assert.Contains("alpha", begun.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.True(begun.ToolCalls >= 1, "the step made no call");
        Assert.NotNull(goneOn);
        Assert.True(goneOn.Status == AgentRunStatus.Completed, $"{goneOn.Status}: {goneOn.Error}");
        Assert.Contains("ahpla", goneOn.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(begun.Session, goneOn.Session);

        var (lastBody, sentOnBody) = (File.ReadAllText(run.Requests()[sentFirst - 1]), File.ReadAllText(run.Requests()[sentFirst]));
        var (last, sentOn) = (JsonNode.Parse(lastBody)!, JsonNode.Parse(sentOnBody)!);
        var (before, after) = (ClaudeApiClientTests.Messages(lastBody), ClaudeApiClientTests.Messages(sentOnBody));

        Assert.Equal(before, after.Take(before.Count));
        Assert.Equal((false, false), (last["thinking"]!.AsObject().ContainsKey("clear_thinking"), sentOn["thinking"]!["clear_thinking"]!.GetValue<bool>()));

        // The first step's answer goes on as Z.AI streamed it: its text and its reasoning, whole.
        var answered = File.ReadLines(Path.Combine(run.Wire, $"{sentFirst.ToString("D2", CultureInfo.InvariantCulture)}-answer.sse"))
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal) && line.Trim() != "data: [DONE]")
            .Select(line => JsonNode.Parse(line["data:".Length..])!["choices"]![0]!["delta"]!)
            .ToList();
        var sentBack = JsonNode.Parse(after[before.Count])!;
        string Answered(string member) => string.Concat(answered.Select(delta => delta[member]?.GetValue<string>()));

        Assert.Equal(
            ("assistant", Answered("content"), Answered("reasoning_content")),
            (sentBack["role"]!.GetValue<string>(), sentBack["content"]!.GetValue<string>(), sentBack["reasoning_content"]!.GetValue<string>()));
    }
}
