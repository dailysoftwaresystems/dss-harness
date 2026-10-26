using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeApi;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Configuration;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;
using Microsoft.Extensions.DependencyInjection;

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
/// alone, never from the process's environment. <see cref="LiveRecords.RecordsVariable"/> names a
/// directory each step's records and every request's body and answer, as the wire carried them, are copied to - never a
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

        var harness = new HarnessFactory();
        var (tier, providers, tiers) = AgentProviderTemplates.Read();
        var (resolved, problems) = AgentProviders.Resolve(new HarnessConfig
        {
            Defaults = new HarnessDefaults { AgentTier = tier },
            Providers = [.. providers],
            TierConfig = new Dictionary<string, List<string>>(tiers, StringComparer.OrdinalIgnoreCase),
        });
        Assert.Empty(problems);
        var anthropic = providers.Single(provider => provider.Type == "anthropic");
        var named = resolved!.KeysRead(anthropic, ClaudeApiClient.ClientName)[0];
        var key = new AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform).Value(new HarnessLayout(root!, root!), resolved.Appointed(), named);
        Assert.True(key.Value is not null, key.Problem);

        using var temp = new TempDirectory();
        var tree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var work = Directory.CreateDirectory(temp.Combine("work")).FullName;
        var wire = Directory.CreateDirectory(temp.Combine("wire")).FullName;
        File.WriteAllText(Path.Combine(tree, "a.txt"), "alpha\n");
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider();
        var client = new ClaudeApiClient(
            harness.FileSystem,
            provider.GetRequiredService<AgentToolHost>(),
            TimeProvider.System,
            new ApiConnection(TimeProvider.System, new Recording(ApiConnection.Connecting(), wire)));
        var runner = new AgentRunner([client], harness.FileSystem, harness.Platform, TimeProvider.System);
        AgentStepRequest Step(string step, string model, string effort, string prompt, AgentSessionRef? conversation) => new AgentStepRequest(
            "r1",
            "a1",
            step,
            1,
            null,
            anthropic.Name,
            ClaudeApiClient.ClientName,
            null,
            model,
            effort,
            null,
            "You are a careful agent. Do exactly what you are asked, and nothing more.",
            prompt,
            new Dictionary<string, string>(),
            null,
            new AgentWorkspace(tree, work, temp.Path, [], tree, Path.Combine(temp.Path, ".orchestrators", "r1")),
            null,
            conversation,
            new AgentLimits(8, 0.5m, TimeSpan.FromMinutes(5), 120),
            MayAsk: false,
            AskRounds: 0,
            new Masking(key.Value))
        {
            Ground = PolicyGrounds.Of(tree, [tree, work], [tree, work], [], [], [], capabilities: [AgentCapability.Read]),
            Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey(named, key.Value!)]),
            Entries = new AgentEntries(anthropic, anthropic.Models.Single(each => each.Code == model), null),
        };

        var begun = await runner.RunAsync(
            Step("s1", First, "high", "Use the view_file tool to read the file a.txt. Then work out how many of the letters of the one word it holds are vowels, and give as your answer that word and that number.", null),
            new RecordedEvents(),
            Token);
        var sentFirst = Directory.GetFiles(wire, "*-request.json").Length;
        var goneOn = begun.Session is { } session
            ? await runner.RunAsync(Step("s2", Next, "low", "Now give as your answer the word you read written backwards, and nothing else.", new AgentSessionRef(ClaudeApiClient.ClientName, anthropic.Name, session)), new RecordedEvents(), Token)
            : null;

        LiveRecords.Copy((Path.Combine(temp.Path, ".orchestrators"), "steps"), (wire, "wire"));
        var thought = Requests(wire).Length > sentFirst ? ClaudeApiClientTests.Messages(File.ReadAllText(Requests(wire)[sentFirst])).Sum(message => JsonNode.Parse(message)!["content"] is JsonArray blocks ? blocks.Count(block => block?["type"]?.GetValue<string>() is "thinking" or "redacted_thinking") : 0) : 0;
        TestContext.Current.SendDiagnosticMessage(
            $"claude-api {First} then {Next}: {Requests(wire).Length} requests, {thought} thinking blocks sent on; spent {begun.Usage?.Cost?.Counted} + {goneOn?.Usage?.Cost?.Counted} USD as the template's prices count it");

        Assert.True(begun.Status == AgentRunStatus.Completed, $"{begun.Status}: {begun.Error}");
        Assert.Contains("alpha", begun.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.True(begun.ToolCalls >= 1, "the step made no call");
        Assert.NotNull(goneOn);
        Assert.True(goneOn.Status == AgentRunStatus.Completed, $"{goneOn.Status}: {goneOn.Error}");
        Assert.Contains("ahpla", goneOn.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(begun.Session, goneOn.Session);

        var (last, sentOn) = (File.ReadAllText(Requests(wire)[sentFirst - 1]), File.ReadAllText(Requests(wire)[sentFirst]));
        var (before, after) = (ClaudeApiClientTests.Messages(last), ClaudeApiClientTests.Messages(sentOn));

        Assert.Equal((First, Next), (JsonNode.Parse(last)!["model"]!.GetValue<string>(), JsonNode.Parse(sentOn)!["model"]!.GetValue<string>()));
        Assert.Equal(before, after.Take(before.Count));

        // The first step's answer goes on as the API gave it: its text, and its thinking, where it thought, whole with its signature.
        var answered = File.ReadLines(Path.Combine(wire, $"{sentFirst.ToString("D2", CultureInfo.InvariantCulture)}-answer.sse"))
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

    /// <summary>Each request's body the wire carried, in the order sent.</summary>
    private static string[] Requests(string wire) => [.. Directory.GetFiles(wire, "*-request.json").Order(StringComparer.Ordinal)];

    /// <summary>
    /// Writes each request's body and its answer as the wire carried them into <paramref name="directory"/> - the
    /// answer as it is read, so the stream stays one - and never a header, so never the key.
    /// </summary>
    private sealed class Recording(HttpMessageHandler inner, string directory) : DelegatingHandler(inner)
    {
        private int _sent;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var at = Path.Combine(directory, Interlocked.Increment(ref _sent).ToString("D2", CultureInfo.InvariantCulture));
            await File.WriteAllBytesAsync($"{at}-request.json", await request.Content!.ReadAsByteArrayAsync(cancellationToken), cancellationToken);

            var response = await base.SendAsync(request, cancellationToken);
            var answer = new StreamContent(new Tee(await response.Content.ReadAsStreamAsync(cancellationToken), File.Create($"{at}-answer.{(response.IsSuccessStatusCode ? "sse" : "json")}")));

            foreach (var header in response.Content.Headers)
            {
                answer.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = answer;
            return response;
        }
    }

    /// <summary>A stream read as its source is, every byte read written to a copy as it comes.</summary>
    private sealed class Tee(Stream source, Stream copy) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            copy.Write(buffer, offset, read);
            copy.Flush();
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            await copy.WriteAsync(buffer[..read], cancellationToken);
            await copy.FlushAsync(cancellationToken);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
                copy.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
