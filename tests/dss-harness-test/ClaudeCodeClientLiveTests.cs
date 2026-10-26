using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Output;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// The Claude Code client's smoke (slice 19b): the client itself, with every check it holds a launch to, against the
/// machine's own <c>claude</c> - which the suite skips unless the machine says it may run it, for it signs in with the
/// person's login, runs a live program and spends what its launches cost. It runs four steps in a scratch tree on a
/// cheap model: one whose call is allowed and whose answer is held to a schema, one whose call the engine refuses, one
/// asked for its handoff before the launch took its prompt, and one that fetches a page through the engine's tool server.
/// </summary>
/// <remarks>
/// Set <see cref="ProgramVariable"/> to the program npm installed (<c>&lt;npm root -g&gt;/@anthropic-ai/claude-code/bin/claude[.exe]</c>)
/// to run it; <see cref="ModelVariable"/> names the model (<c>claude-haiku-5-5</c> where it is not set), and
/// <see cref="RecordsVariable"/> a directory each step's records are copied to - its stored stream, the control messages
/// both ways, the launch's settings - for the person to read; nothing of it is committed. A continuous-integration runner
/// never sets the variable: it has no login and must not.
/// </remarks>
public sealed class ClaudeCodeClientLiveTests
{
    /// <summary>The variable naming the program the smoke starts; the smoke runs only where it is set.</summary>
    public const string ProgramVariable = "DSSHARNESS_TESTS_LIVE_CLAUDE_CLIENT";

    /// <summary>The variable naming the model the smoke runs on.</summary>
    public const string ModelVariable = "DSSHARNESS_TESTS_LIVE_CLAUDE_MODEL";

    /// <summary>The variable naming the directory the smoke's records are copied to.</summary>
    public const string RecordsVariable = "DSSHARNESS_TESTS_LIVE_RECORDS";

    private const string Schema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A step on the machine's claude completes through the client: the launch answers the host's initialize, its init
    /// offers the tools it was given and the credential the login gives, the read it asks for is allowed and runs, its
    /// answer reads against the schema - and the launch's start names the user's settings and says the hooks are off.
    /// A second step's read of a credential slot is refused, and the step still completes. A third, asked for its handoff
    /// before the launch took its prompt, has the prompt dropped by the interrupt - named in its receipt by the id the
    /// engine gave it - and its one turn is the handoff's. A fourth, with the web, fetches a page by the engine's tool
    /// server: connected before its init, the call asked of the engine and run by the harness's own fetch.
    /// </summary>
    [Fact]
    public async Task AStep_RunsThroughTheClient_OnTheMachinesClaude()
    {
        var program = Environment.GetEnvironmentVariable(ProgramVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(program), $"{ProgramVariable} is not set: the live smoke runs only where the person said it might.");

        var model = Environment.GetEnvironmentVariable(ModelVariable) is { Length: > 0 } named ? named : "claude-haiku-5-5";
        var harness = new HarnessFactory();
        using var temp = new TempDirectory();
        var tree = Directory.CreateDirectory(temp.Combine("tree")).FullName;
        var work = Directory.CreateDirectory(temp.Combine("work")).FullName;
        var slot = temp.WriteFile(Path.Combine("slot", "keys.env"), "NOT_FOR_THE_AGENT=1\n");
        File.WriteAllText(Path.Combine(tree, "a.txt"), "alpha\n");
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider();
        var client = new ClaudeCodeClient(harness.ProcessRunner, harness.FileSystem, harness.Platform, harness.ChildSessions, TimeProvider.System, tools: provider.GetRequiredService<AgentToolHost>());
        var runner = new AgentRunner([client], harness.FileSystem, harness.Platform, TimeProvider.System);

        var allowed = new Sink();
        var read = await runner.RunAsync(
            Step(temp.Path, tree, work, slot, program!, model, "s1", "Use the Read tool to read the file a.txt, then give as your answer the one word it holds."),
            allowed,
            Token);

        var refused = new Sink();
        var denied = await runner.RunAsync(
            Step(temp.Path, tree, work, slot, program!, model, "s2", $"Use the Read tool to read the file {slot}. If you cannot, give as your answer the word refused."),
            refused,
            Token);

        var pausing = new AgentHandoffSignal(TimeSpan.FromSeconds(120));
        pausing.Ask("the run is pausing");
        var paused = new Sink();
        var handed = await runner.RunAsync(
            Step(temp.Path, tree, work, slot, program!, model, "s3", "Use the Read tool to read the file a.txt, then give as your answer the one word it holds.") with { Handoff = pausing },
            paused,
            Token);

        var fetching = new Sink();
        var fetched = await runner.RunAsync(
            Step(temp.Path, tree, work, slot, program!, model, "s4", "Use the tool mcp__dssharness__web_fetch to fetch https://example.com/, then give as your answer the page's main heading.") with
            {
                Ground = PolicyGrounds.Of(tree, [tree, work], [tree, work], [Path.GetDirectoryName(slot)!], [], [], capabilities: [AgentCapability.Read, AgentCapability.Web]),
            },
            fetching,
            Token);

        Record(temp.Path);

        Assert.True(fetched.Status == AgentRunStatus.Completed, $"{fetched.Status}: {fetched.Error}");
        Assert.Contains("Example Domain", fetched.StructuredOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fetching.Events.OfType<ToolCallStarted>(), call => call.Tool == "mcp__dssharness__web_fetch" && call.Runner == AgentToolRunner.Harness);
        Assert.True(fetched.WebRequests >= 1, "nothing was asked of the web");

        Assert.True(read.Status == AgentRunStatus.Completed, $"{read.Status}: {read.Error}");
        Assert.Contains("alpha", read.StructuredOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(read.ToolCalls >= 1, "the step made no call");

        var started = Assert.Single(allowed.Events.OfType<SessionStarted>());
        Assert.Equal(ClaudeUserSettings.HooksOff, started.Hooks);
        Assert.Contains(started.ApiKeySource, ClaudeLaunch.ApiKeySources(AgentAuth.Login));
        Assert.Contains(ClaudeCodeTools.StructuredOutput, started.Tools);

        Assert.True(denied.Status == AgentRunStatus.Completed, $"{denied.Status}: {denied.Error}");
        Assert.True(denied.Denials >= 1, "nothing was refused");
        Assert.DoesNotContain(refused.Events.OfType<ToolCallFinished>(), call => call.Ok);

        Assert.True(handed.Status == AgentRunStatus.HandedOff, $"{handed.Status}: {handed.Error}");
        Assert.False(string.IsNullOrWhiteSpace(handed.Handoff?.Answer), handed.Handoff?.WhyNone);
        Assert.Equal(1, handed.Turns);
        Assert.Equal([AgentMessageKind.FirstPrompt, AgentMessageKind.HandoffQuestion], paused.Events.OfType<MessageSent>().Select(message => message.Which));

        TestContext.Current.SendDiagnosticMessage(
            $"claude {started.Tools.Count} tools, settings '{started.SettingsFile}' ({started.SettingsDigest}); handoff '{handed.Handoff?.Answer}'; spent {read.Usage?.Cost?.Reported} + {denied.Usage?.Cost?.Reported} + {handed.Usage?.Cost?.Reported} + {fetched.Usage?.Cost?.Reported} USD as Claude Code reports it");
    }

    private static AgentStepRequest Step(string root, string tree, string work, string slot, string program, string model, string step, string prompt)
        => new(
            "r1",
            "a1",
            step,
            1,
            null,
            "anthropic",
            ClaudeCodeClient.ClientName,
            null,
            model,
            "low",
            null,
            "You are a careful agent. Do exactly what you are asked, and nothing more.",
            prompt,
            new Dictionary<string, string>(),
            null,
            new AgentWorkspace(tree, work, root, [], tree, Path.Combine(root, ".orchestrators", "r1")),
            new AgentOutput("o", Schema),
            null,
            new AgentLimits(8, 0.5m, TimeSpan.FromMinutes(5), 120),
            MayAsk: false,
            AskRounds: 0,
            null)
        {
            Ground = PolicyGrounds.Of(tree, [tree, work], [tree, work], [Path.GetDirectoryName(slot)!], [], [], capabilities: [AgentCapability.Read]),
            Program = program,
        };

    /// <summary>Copies what the smoke's steps left to the directory the person named, where they named one.</summary>
    private static void Record(string root)
    {
        if (Environment.GetEnvironmentVariable(RecordsVariable) is not { Length: > 0 } records)
        {
            return;
        }

        var steps = Path.Combine(root, ".orchestrators");
        var into = Path.Combine(records, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));

        foreach (var file in Directory.Exists(steps) ? Directory.GetFiles(steps, "*", SearchOption.AllDirectories) : [])
        {
            var target = Path.Combine(into, Path.GetRelativePath(steps, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Every event a step emitted, from any number of writers - a call of the engine's tool server's among them.</summary>
    private sealed class Sink : IAgentEventSink
    {
        private readonly Lock _gate = new();
        private readonly List<AgentEvent> _events = [];

        public List<AgentEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask EmitAsync(AgentEvent @event)
        {
            lock (_gate)
            {
                _events.Add(@event);
            }

            return ValueTask.CompletedTask;
        }
    }
}
