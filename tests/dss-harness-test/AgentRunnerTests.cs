using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// The runner skeleton: it drives a client through the contract end to end - launch, stream, end - over a fake only,
/// with the stored stream always attached, the events numbered by the one pump a run has, and the run's own record
/// written beside its stream. Nothing here is live, and no provider is named.
/// </summary>
public sealed class AgentRunnerTests
{
    /// <summary>
    /// End to end: the client is launched with its run - the prompt filled from its template, the places as the step
    /// named them, the stream the step's own - what happens is streamed to the caller in order, and how it ended is
    /// answered and written beside the stream.
    /// </summary>
    [Fact]
    public async Task TheRunnerDrivesAClient_FromLaunchToStreamToEnd()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient
        {
            Script = async (run, events, _) =>
            {
                await events.EmitAsync(new SessionStarted(FakeClient.ClientName, "model-x", "hash", [], [], HandedConversation: false));
                await events.EmitAsync(new TextDelta("work"));
                await events.EmitAsync(new AssistantMessage("work done", []));
                await events.EmitAsync(new RunFinished());

                return Completed(run);
            },
        };

        var runner = new AgentRunner([client]);
        var seen = new List<AgentEvent>();

        var result = await runner.RunAsync(Step(temp, promptValues: new Dictionary<string, string> { ["item"] = "login form" }), new Collecting(seen), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("do the login form", client.Run!.UserPrompt);
        Assert.Equal("be terse", client.Run.SystemPrompt);
        Assert.Equal(WorkDirectory(temp), client.Run.Workspace.WorkingDirectory);

        Assert.Equal(new[] { "sessionStarted", "textDelta", "assistantMessage", "runFinished" }, seen.Select(Kind));

        var lines = File.ReadAllLines(StreamFile(temp));

        Assert.Equal(new[] { "sessionStarted", "assistantMessage", "runFinished" }, lines.Select(LineKind));
        Assert.Contains($"status\": \"{AgentRunStatus.Completed}", File.ReadAllText(ResultFile(temp)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the run itself fails and a listener failed too, the run's failure is the one answered with: what a sink
    /// raised is never raised into the middle of the run's own ending.
    /// </summary>
    [Fact]
    public async Task WhereTheClientFailsAndASinkFailedToo_TheRunsOwnFailureAnswers()
    {
        using var temp = new TempDirectory();
        var failing = new CallbackSink(_ => throw new InvalidOperationException("the sink broke"));
        var client = new FakeClient
        {
            Script = async (_, events, _) =>
            {
                await events.EmitAsync(new MessageSent("begun", AgentMessageKind.FirstPrompt));
                throw new HarnessException(HarnessExit.CommandFailed, "the client failed");
            },
        };

        var failed = await Assert.ThrowsAsync<HarnessException>(
            () => new AgentRunner([client]).RunAsync(Step(temp), failing, TestContext.Current.CancellationToken));

        Assert.Equal("the client failed", failed.Message);
        Assert.Equal(new[] { "messageSent" }, File.ReadAllLines(StreamFile(temp)).Select(LineKind));
    }

    /// <summary>
    /// The events of a run are numbered by the pump, in order, and every sink is called serially with the same numbered
    /// event: no maker of an event numbers it, and no sink sees another order.
    /// </summary>
    [Fact]
    public async Task TheEventsAreNumberedByThePump_AndEverySinkSeesTheSameOrder()
    {
        using var temp = new TempDirectory();
        var first = new Collecting([]);
        var second = new Collecting([]);
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };

        await new AgentRunner([client]).RunAsync(Step(temp), new Split(first, second), TestContext.Current.CancellationToken);

        Assert.Equal(1, first.Seen[0].Seq);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, first.Seen.Select(@event => @event.Seq));
        Assert.Equal(first.Seen.Select(@event => @event.Seq), second.Seen.Select(@event => @event.Seq));
        Assert.All(first.Seen, @event => Assert.NotEqual(default, @event.At));
    }

    /// <summary>
    /// A sink that throws gets no more events - the others keep theirs, and the stored stream keeps its record whole -
    /// and its exception is raised once the run has ended: what the agent said is not lost to what a listener did.
    /// </summary>
    [Fact]
    public async Task ASinkThatThrows_GetsNoMoreEvents_AndItsFailureRaisesAfterTheRunEnded()
    {
        using var temp = new TempDirectory();
        var broken = 0;
        var goneBad = false;
        var failing = new CallbackSink(
            @event =>
            {
                if (@event is AssistantMessage)
                {
                    goneBad = true;
                }

                if (goneBad)
                {
                    broken++;
                    throw new InvalidOperationException("the sink broke");
                }
            });
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AgentRunner([client]).RunAsync(Step(temp), failing, TestContext.Current.CancellationToken));

        Assert.Equal("the sink broke", failed.Message);

        // It broke once and was given no more: the run finished around it without asking it again.
        Assert.Equal(1, broken);

        // The record is whole: the stream took every event, the failing sink among them.
        Assert.Equal(new[] { "sessionStarted", "assistantMessage", "runFinished" }, File.ReadAllLines(StreamFile(temp)).Select(LineKind));
    }

    /// <summary>Stream yields what happens as it happens, in order, until the run has ended.</summary>
    [Fact]
    public async Task Stream_YieldsWhatHappensAsItHappens()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };
        var seen = new List<AgentEvent>();

        await foreach (var @event in new AgentRunner([client]).Stream(Step(temp), TestContext.Current.CancellationToken))
        {
            seen.Add(@event);
        }

        Assert.Equal(new long[] { 1, 2, 3, 4 }, seen.Select(@event => @event.Seq));
        Assert.IsType<RunFinished>(seen[^1]);
        Assert.True(File.Exists(StreamFile(temp)));
    }

    /// <summary>
    /// Where no client can be taken, the run never began and the configuration is why: a record of the step exists all
    /// the same, saying so.
    /// </summary>
    [Fact]
    public async Task WhereNoClientCanBeTaken_TheRunNeverBegan_SayingTheConfiguration()
    {
        using var temp = new TempDirectory();

        var result = await new AgentRunner([new FakeClient()]).RunAsync(
            Step(temp, client: "not-there"), events: null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.NotStarted, result.Status);
        Assert.Equal(AgentNotStartedBecause.ConfigurationWrong, result.NotStartedBecause);
        Assert.Equal(HarnessExit.ConfigInvalid, result.ToOutcome().ExitCode);

        // The lists the plan says are always filled are, even here.
        Assert.Empty(result.Files);
        Assert.Empty(result.Served);
        Assert.Empty(result.Assumptions);

        Assert.Contains($"status\": \"{AgentRunStatus.NotStarted}", File.ReadAllText(ResultFile(temp)), StringComparison.Ordinal);
    }

    /// <summary>
    /// How a run ended is the exit code its table gives: asked is the run command's own code, a hard stop is 130, and
    /// the reasons that can be retried are 21, the ones that cannot 20.
    /// </summary>
    [Theory]
    [InlineData(AgentRunStatus.Completed, 0)]
    [InlineData(AgentRunStatus.Asked, 1)]
    [InlineData(AgentRunStatus.Cancelled, 130)]
    [InlineData(AgentRunStatus.HandedOff, 130)]
    [InlineData(AgentRunStatus.Stalled, 21)]
    [InlineData(AgentRunStatus.TimedOut, 21)]
    [InlineData(AgentRunStatus.RateLimited, 21)]
    [InlineData(AgentRunStatus.MaxTurns, 20)]
    [InlineData(AgentRunStatus.Failed, 20)]
    [InlineData(AgentRunStatus.WrongModel, 20)]
    public void HowARunEnded_IsTheExitCodeItsTableGives(AgentRunStatus status, int exitCode)
        => Assert.Equal(exitCode, new AgentRunResult(status) { Served = [new AgentServedModel("model-x", 1)] }.ToOutcome().ExitCode);

    /// <summary>Where it never began, the exit code says which of the three it was.</summary>
    [Theory]
    [InlineData(AgentNotStartedBecause.ProgramMissing, 14)]
    [InlineData(AgentNotStartedBecause.KeyMissing, 13)]
    [InlineData(AgentNotStartedBecause.ConfigurationWrong, 12)]
    public void WhereItNeverBegan_TheExitCodeSaysWhichOfTheThreeItWas(AgentNotStartedBecause because, int exitCode)
        => Assert.Equal(exitCode, new AgentRunResult(AgentRunStatus.NotStarted, because).ToOutcome().ExitCode);

    /// <summary>
    /// The writer's bounds: a producer that outruns its sinks waits for them - nothing is dropped, nothing faults - and
    /// every event numbered is delivered, past the channel's own capacity and in order.
    /// </summary>
    [Fact]
    public async Task AProducerThatOutrunsItsSinks_WaitsForThem_AndNothingIsDropped()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<long>();
        var slow = new CallbackSink(
            @event =>
            {
                if (@event.Seq == 1)
                {
                    gate.Task.Wait(TimeSpan.FromSeconds(30));
                }

                seen.Add(@event.Seq);
            });

        var pump = new AgentEventPump();
        pump.Attach(slow);

        var posted = AgentEventPump.Capacity + 100;

        var producing = Task.Run(
            async () =>
            {
                for (var count = 0; count < posted; count++)
                {
                    await pump.EmitAsync(new Notice(count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            },
            TestContext.Current.CancellationToken);

        // The sinks hold the first event, so the producer fills the channel and waits: it cannot finish while the
        // gate is closed.
        var sprint = await Task.WhenAny(producing, Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));

        Assert.True(!ReferenceEquals(producing, sprint), "the producer finished with its sinks holding their ground");

        gate.SetResult();

        await producing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await pump.DisposeAsync();

        Assert.Equal(1, seen[0]);
        Assert.Equal(Enumerable.Range(1, posted).Select(count => (long)count), seen);
    }

    /// <summary>The runner is registered as the one way an agent's step is worked, with no client built in yet.</summary>
    [Fact]
    public void TheRunnerIsRegistered_InAddDssHarness()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsType<AgentRunner>(provider.GetRequiredService<IAgentRunner>());
    }

    /// <summary>The scripted work: a launch, a message with its delta, and the end.</summary>
    private static async Task<AgentRunResult> Scripted(IAgentEventSink events)
    {
        await events.EmitAsync(new SessionStarted(FakeClient.ClientName, "model-x", "hash", [], [], HandedConversation: false));
        await events.EmitAsync(new TextDelta("work"));
        await events.EmitAsync(new AssistantMessage("work done", []));
        await events.EmitAsync(new RunFinished());

        return new AgentRunResult(AgentRunStatus.Completed, FinalText: "work done", Turns: 1) { Served = [new AgentServedModel("model-x", 1)] };
    }

    private static AgentRunResult Completed(AgentRun run)
        => new(AgentRunStatus.Completed, FinalText: "work done", Turns: 1) { Served = [new AgentServedModel(run.Model ?? "model-x", 1)] };

    /// <summary>One step to work, on a repository of a temporary directory.</summary>
    private static AgentStepRequest Step(TempDirectory temp, string? client = null, Dictionary<string, string>? promptValues = null)
        => new(
            "r1",
            "a7",
            "s1",
            AgentDefinition: null,
            Provider: null,
            Client: client,
            Tier: null,
            Model: "model-x",
            Effort: null,
            Fallback: null,
            SystemPrompt: "be terse",
            PromptTemplate: "do the {item}",
            promptValues ?? new Dictionary<string, string> { ["item"] = "thing" },
            ResumeTemplate: null,
            new AgentWorkspace(
                Worktree: null,
                WorkDirectory(temp),
                temp.Path,
                ExtraRoots: [],
                WorkingDirectory: WorkDirectory(temp),
                RunDirectory: Path.Combine(temp.Path, ".orchestrators", "r1")),
            Output: null,
            Conversation: null,
            new AgentLimits(MaxTurns: 10, MaxBudgetUsd: null, MaxDuration: null, StallSeconds: null),
            MayAsk: false,
            AskRounds: 0,
            Restrictions: [],
            Mask: null);

    /// <summary>The agent's work directory, the only place it writes.</summary>
    private static string WorkDirectory(TempDirectory temp)
        => Path.Combine(temp.Path, ".orchestrators", "r1", "work", "a7");

    /// <summary>The step's stored stream, where the one owner of the paths puts it.</summary>
    private static string StreamFile(TempDirectory temp)
        => Path.Combine(temp.Path, ".orchestrators", "r1", "agents", "a7", "steps", "s1", StoredStream.FileName);

    /// <summary>The run's own record, beside its stream.</summary>
    private static string ResultFile(TempDirectory temp)
        => Path.Combine(temp.Path, ".orchestrators", "r1", "agents", "a7", "steps", "s1", AgentRunner.ResultFileName);

    /// <summary>One line's event kind, as its own form names it.</summary>
    private static string LineKind(string line) => (string)JsonNode.Parse(line)!["kind"]!;

    /// <summary>One event's kind, as its own form names it.</summary>
    private static string Kind(AgentEvent @event)
        => @event switch
        {
            SessionStarted => "sessionStarted",
            TextDelta => "textDelta",
            AssistantMessage => "assistantMessage",
            RunFinished => "runFinished",
            _ => "other",
        };

    /// <summary>A client that works whatever its script says, remembering the run it was given.</summary>
    private sealed class FakeClient : IAgentClient
    {
        /// <summary>The fake's own name, as a step names it.</summary>
        public const string ClientName = "fake";

        public string Name => ClientName;

        public AgentClientCapabilities Capabilities { get; } = new(
            Streaming: true,
            Thinking: true,
            WritableWhileWorking: true,
            AgentConversationHandling.Rendered,
            SchemaEnforcedOutput: false,
            AgentToolsOwner.Harness,
            AgentToolsOwner.Harness,
            ReportsCost: false,
            ["low", "high"],
            ProviderType: "none",
            ToldModelBy: "the test");

        public AgentRun? Run { get; private set; }

        public Func<AgentRun, IAgentEventSink, CancellationToken, Task<AgentRunResult>> Script { get; init; }
            = (_, events, _) => Scripted(events);

        public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            Run = run;
            return await Script(run, events, cancellationToken);
        }
    }

    /// <summary>A sink that collects what it is given.</summary>
    private sealed class Collecting(List<AgentEvent> seen) : IAgentEventSink
    {
        public List<AgentEvent> Seen => seen;

        public ValueTask EmitAsync(AgentEvent @event)
        {
            seen.Add(@event);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A sink that hands each event to two sinks of its own.</summary>
    private sealed class Split(IAgentEventSink first, IAgentEventSink second) : IAgentEventSink
    {
        public async ValueTask EmitAsync(AgentEvent @event)
        {
            await first.EmitAsync(@event);
            await second.EmitAsync(@event);
        }
    }

    /// <summary>A sink that does something with each event, which may fail.</summary>
    private sealed class CallbackSink(Action<AgentEvent> with) : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent @event)
        {
            with(@event);
            return ValueTask.CompletedTask;
        }
    }
}
