using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// The runner skeleton: it drives a client through the contract end to end - launch, stream, end - over a fake only,
/// with the stored stream always attached and handed each event first, the events numbered by the one pump a run has
/// and stamped when they were posted, both ends of every run written by the runner, and the run's own record written
/// beside its stream once the stream is whole. Nothing here is live, and no provider is named.
/// </summary>
public sealed class AgentRunnerTests
{
    /// <summary>
    /// End to end: the client is launched with its run - the prompt filled from its template, the places as the step
    /// named them, the step's directory its own - what happens is streamed to the caller in order between the runner's
    /// two ends, and how it ended is answered and written beside the stream.
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

                return Completed(run);
            },
        };

        var seen = new List<AgentEvent>();

        var result = await Runner(client).RunAsync(Step(temp, promptValues: new Dictionary<string, string> { ["item"] = "login form" }), new Collecting(seen), TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("do the login form", client.Run!.UserPrompt);
        Assert.Equal("be terse", client.Run.SystemPrompt);
        Assert.Equal(WorkDirectory(temp), client.Run.Workspace.WorkingDirectory);
        Assert.Equal(StepDirectory(temp), client.Run.StepDirectory);

        Assert.Equal(["runStarted", "sessionStarted", "textDelta", "assistantMessage", "runFinished"], seen.Select(Kind));
        Assert.Equal(["runStarted", "sessionStarted", "assistantMessage", "runFinished"], Lines(temp).Select(LineKind));
        Assert.Equal(new RunStarted(1) { At = seen[0].At, Seq = 1 }, seen[0]);
        Assert.Equal(new RunFinished(AgentRunStatus.Completed) { At = seen[^1].At, Seq = 5 }, seen[^1]);
        Assert.Equal(JsonSerializer.Serialize(result, JsonStateFile.Options) + "\n", File.ReadAllText(ResultFile(temp)));
    }

    /// <summary>
    /// A client that throws leaves a record that says it failed - the run's last line and <c>result.json</c> alike - and
    /// its failure is raised once both are written; where a listener failed too, the run's own failure is the one
    /// answered with.
    /// </summary>
    [Fact]
    public async Task AClientThatThrows_LeavesARecordThatSaysItFailed_AndItsFailureIsRaisedAfter()
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
            () => Runner(client).RunAsync(Step(temp), failing, TestContext.Current.CancellationToken));

        Assert.Equal("the client failed", failed.Message);
        Assert.Equal(["runStarted", "messageSent", "runFinished"], Lines(temp).Select(LineKind));
        Assert.Equal("""{"kind":"runFinished","status":"Failed","error":"the client failed"}""", WithoutWhenAndWhere(Lines(temp)[^1]));

        var recorded = ReadResult(temp);

        Assert.Equal(AgentRunStatus.Failed, recorded.Status);
        Assert.Equal("the client failed", recorded.Error);
        Assert.Equal("model-x", recorded.Model);
    }

    /// <summary>
    /// A hard stop ends the run cancelled: the record's last line and <c>result.json</c> say the run was stopped, and the
    /// stop the client threw is raised after them.
    /// </summary>
    [Fact]
    public async Task AHardStop_EndsTheRunCancelled_RecordedBeforeItIsRaised()
    {
        using var temp = new TempDirectory();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var client = new FakeClient
        {
            Script = async (_, events, token) =>
            {
                await events.EmitAsync(new MessageSent("begun", AgentMessageKind.FirstPrompt));
                await stop.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("never reached");
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Runner(client).RunAsync(Step(temp), events: null, stop.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal(["runStarted", "messageSent", "runFinished"], Lines(temp).Select(LineKind));
        Assert.Equal($$"""{"kind":"runFinished","status":"Cancelled","error":"{{AgentRunner.StoppedError}}"}""", WithoutWhenAndWhere(Lines(temp)[^1]));
        Assert.Equal(AgentRunStatus.Cancelled, ReadResult(temp).Status);
    }

    /// <summary>
    /// The two ends of a run are the runner's own: a client that emits either is refused, and the run ends failed,
    /// saying why - its record holding one end of each kind and no more.
    /// </summary>
    [Fact]
    public async Task AClientThatEmitsAnEndOfTheRun_IsRefused()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient
        {
            Script = async (_, events, _) =>
            {
                await events.EmitAsync(new RunFinished(AgentRunStatus.Completed));
                throw new InvalidOperationException("never reached");
            },
        };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Runner(client).RunAsync(Step(temp), events: null, TestContext.Current.CancellationToken));

        const string Refusal = "'RunFinished' is the runner's own event, which a client never emits: the runner writes each end of every run itself.";

        Assert.Equal(Refusal, refused.Message);
        Assert.Equal(["runStarted", "runFinished"], Lines(temp).Select(LineKind));
        Assert.Equal($$"""{"kind":"runFinished","status":"Failed","error":"{{Refusal}}"}""", WithoutWhenAndWhere(Lines(temp)[^1]));
    }

    /// <summary>
    /// A client is handed no writer of the step's record: nothing in its run is a sink, the stored stream least of all,
    /// so the one way into the record is the events it emits to the pump, numbered and stamped there and written one at
    /// a time.
    /// </summary>
    [Fact]
    public void AClientsRun_HandsItNoWriterOfTheRecord()
        => Assert.Empty(typeof(AgentRun).GetProperties()
            .Where(property => typeof(IAgentEventSink).IsAssignableFrom(property.PropertyType) || typeof(IDisposable).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name));

    /// <summary>
    /// A step run again is the same instance, and its next attempt appends to the same stream: each attempt begins with
    /// its own <see cref="RunStarted"/> and is numbered from one again, so the newest attempt is what follows the last
    /// beginning - after an attempt that ended, and after one a kill cut off with no end and a torn line.
    /// </summary>
    [Fact]
    public async Task TheNextAttempt_AppendsToTheSameStream_BeginningWithItsOwnStart()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };

        await Runner(client).RunAsync(Step(temp), events: null, TestContext.Current.CancellationToken);

        // What a kill leaves of attempt 2: its beginning, a message, no end, and a line begun and never ended.
        File.AppendAllText(StreamFile(temp), """{"kind":"runStarted","attempt":2,"at":"2026-10-18T00:00:00+00:00","seq":1}""" + "\n" + "{\"kind\":\"messageS");

        await Runner(client).RunAsync(Step(temp, attempt: 3), events: null, TestContext.Current.CancellationToken);

        var lines = Lines(temp);

        Assert.Equal(
            ["runStarted", "sessionStarted", "assistantMessage", "runFinished", "runStarted", "runStarted", "sessionStarted", "assistantMessage", "runFinished"],
            lines.Select(LineKind));

        var newest = lines.Skip(lines.FindLastIndex(line => LineKind(line) == "runStarted")).ToList();

        // Numbered from one again: the delta, third, was numbered and not stored.
        Assert.Equal(3, (int)JsonNode.Parse(newest[0])!["attempt"]!);
        Assert.Equal([1L, 2L, 4L, 5L], newest.Select(line => (long)JsonNode.Parse(line)!["seq"]!));
    }

    /// <summary>
    /// An attempt is counted from one: a request for any other is refused before anything is opened or written, for a
    /// record whose attempts cannot be told apart is one no continuation can be written from.
    /// </summary>
    [Fact]
    public async Task AnAttemptBelowOne_IsRefused_BeforeAnythingIsWritten()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient();

        var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Runner(client).RunAsync(Step(temp, attempt: 0), events: null, TestContext.Current.CancellationToken));

        Assert.Equal("step.Attempt", refused.ParamName);
        Assert.Null(client.Run);
        Assert.False(Directory.Exists(StepDirectory(temp)));
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

        await Runner(client).RunAsync(Step(temp), new Split(first, second), TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], first.Seen.Select(@event => @event.Seq));
        Assert.Equal(first.Seen.Select(@event => @event.Seq), second.Seen.Select(@event => @event.Seq));

        // The record keeps each number as the pump gave it: the delta's is the one it does not store.
        Assert.Equal([1L, 2L, 4L, 5L], Lines(temp).Select(line => (long)JsonNode.Parse(line)!["seq"]!));
    }

    /// <summary>
    /// An event is stamped when it was posted, not when a sink got it: under backpressure, an event waits behind a slow
    /// sink, and the time it is given is the time it happened.
    /// </summary>
    [Fact]
    public async Task AnEventIsStampedWhenItWasPosted_NotWhenASinkGotIt()
    {
        var clock = new ManualClock();
        var start = clock.GetUtcNow();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<AgentEvent>();
        var record = new CallbackSink(
            @event =>
            {
                if (@event.Seq == 1)
                {
                    gate.Task.Wait(TimeSpan.FromSeconds(30));
                }

                seen.Add(@event);
            });

        var pump = new AgentEventPump(record, clock, TestContext.Current.CancellationToken);

        await pump.EmitAsync(new Notice("first"));
        clock.Advance(TimeSpan.FromSeconds(5));
        await pump.EmitAsync(new Notice("second"));
        clock.Advance(TimeSpan.FromSeconds(5));

        gate.SetResult();
        await pump.DisposeAsync();

        Assert.Equal([start, start.AddSeconds(5)], seen.Select(@event => @event.At));
    }

    /// <summary>
    /// The stored stream is handed each event before any listener is: whatever a listener does with an event, the
    /// record already holds it.
    /// </summary>
    [Fact]
    public async Task TheRecordHasEachEvent_BeforeAnyListenerIsHandedIt()
    {
        using var temp = new TempDirectory();
        var linesWhenSeen = new List<int>();
        var listener = new CallbackSink(
            @event =>
            {
                if (@event is not TextDelta)
                {
                    linesWhenSeen.Add(ReadBeside(StreamFile(temp)).Length);
                }
            });
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };

        await Runner(client).RunAsync(Step(temp), listener, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3, 4], linesWhenSeen);
    }

    /// <summary>
    /// A listener that hangs holds the run only until the hard stop: then it is let go, the record takes every event
    /// that waited behind it and the run's end, and the run is answered stopped.
    /// </summary>
    [Fact]
    public async Task AHangingListener_IsLetGoAtTheHardStop_AndTheRecordEndsWhole()
    {
        using var temp = new TempDirectory();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new HangingSink(hung.Task, from: 2);
        var client = new FakeClient
        {
            Script = async (_, events, token) =>
            {
                await events.EmitAsync(new MessageSent("one", AgentMessageKind.FirstPrompt));
                await events.EmitAsync(new AssistantMessage("two", []));
                await events.EmitAsync(new AssistantMessage("three", []));
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("never reached");
            },
        };

        var running = Runner(client).RunAsync(Step(temp), listener, stop.Token);

        await listener.Holding.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal(["runStarted", "messageSent", "assistantMessage", "assistantMessage", "runFinished"], Lines(temp).Select(LineKind));
        Assert.Equal(AgentRunStatus.Cancelled, ReadResult(temp).Status);
    }

    /// <summary>
    /// A producer waiting for room behind sinks that hold their ground stops waiting at the hard stop: a sink that hangs
    /// holds no producer past it.
    /// </summary>
    [Fact]
    public async Task AProducerWaitingForRoom_StopsWaitingAtTheHardStop()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var record = new CallbackSink(_ => gate.Task.Wait(TimeSpan.FromSeconds(30)));
        var pump = new AgentEventPump(record, TimeProvider.System, stop.Token);

        var producing = Task.Run(
            async () =>
            {
                for (var count = 0; count < AgentEventPump.Capacity + 10; count++)
                {
                    await pump.EmitAsync(new Notice("n"));
                }
            },
            TestContext.Current.CancellationToken);

        var sprint = await Task.WhenAny(producing, Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));

        Assert.NotSame(producing, sprint);

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        gate.SetResult();
        await pump.DisposeAsync();
    }

    /// <summary>
    /// A listener that throws gets no more events - the others keep theirs, and the stored stream keeps its record whole -
    /// and its exception is raised once the run has ended: what the agent said is not lost to what a listener did.
    /// </summary>
    [Fact]
    public async Task AListenerThatThrows_GetsNoMoreEvents_AndItsFailureRaisesAfterTheRunEnded()
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
            () => Runner(client).RunAsync(Step(temp), failing, TestContext.Current.CancellationToken));

        Assert.Equal("the sink broke", failed.Message);

        // It broke once and was given no more: the run finished around it without asking it again.
        Assert.Equal(1, broken);

        // The record is whole: the stream took every event, the failing listener's among them.
        Assert.Equal(["runStarted", "sessionStarted", "assistantMessage", "runFinished"], Lines(temp).Select(LineKind));
        Assert.Equal(AgentRunStatus.Completed, ReadResult(temp).Status);
    }

    /// <summary>
    /// Where the step's record cannot be written, the run is not one to go on with: the client is stopped through the
    /// token it was handed, the record keeps what it had, and the result - answered, written, and handed to a listener as
    /// the run's end - says the run failed, naming the record and what kept it from being written.
    /// </summary>
    [Fact]
    public async Task WhereTheRecordCannotBeWritten_TheClientIsStopped_AndTheResultSaysTheRunFailedNamingIt()
    {
        using var temp = new TempDirectory();
        var disk = new FullDiskFileSystem(Disk());
        var seen = new List<AgentEvent>();
        var client = new FakeClient
        {
            Script = async (_, events, token) =>
            {
                await events.EmitAsync(new MessageSent("one", AgentMessageKind.FirstPrompt));
                await Until(() => Lines(temp).Count == 2);

                // The record has every line so far; the next is the one the full disk refuses.
                disk.Full = true;
                await events.EmitAsync(new AssistantMessage("two", []));

                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    disk.Full = false;
                }

                throw new InvalidOperationException("never reached");
            },
        };

        var result = await new AgentRunner([client], disk, TimeProvider.System)
            .RunAsync(Step(temp), new Collecting(seen), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var error = $"The step's stored stream '{StreamFile(temp)}' could not be written, so the run was stopped: {FullDiskFileSystem.Why}";

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(error, result.Error);
        Assert.Equal(["runStarted", "messageSent"], Lines(temp).Select(LineKind));
        Assert.Equal(new RunFinished(AgentRunStatus.Failed, error) { At = seen[^1].At, Seq = 4 }, seen[^1]);
        Assert.Equal(AgentRunStatus.Failed, ReadResult(temp).Status);
        Assert.Equal(error, ReadResult(temp).Error);
    }

    /// <summary>
    /// Where the record fails on an event the client emitted just before it returned, the run's end is made only once that
    /// event has been tried: the client said it completed, and the run failed all the same, its end and its result alike.
    /// </summary>
    [Fact]
    public async Task WhereTheRecordFailsAfterTheClientReturned_TheRunStillFails()
    {
        using var temp = new TempDirectory();
        var disk = new SlowToFail(Disk(), failing: 3, after: TimeSpan.FromMilliseconds(500));
        var seen = new List<AgentEvent>();
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };

        var result = await new AgentRunner([client], disk, TimeProvider.System).RunAsync(Step(temp), new Collecting(seen), TestContext.Current.CancellationToken);

        var error = $"The step's stored stream '{StreamFile(temp)}' could not be written, so the run was stopped: {SlowToFail.Why}";

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(error, result.Error);
        Assert.Equal(["runStarted", "sessionStarted"], Lines(temp).Select(LineKind));
        Assert.Equal(new RunFinished(AgentRunStatus.Failed, error) { At = seen[^1].At, Seq = 5 }, seen[^1]);
        Assert.Equal(error, ReadResult(temp).Error);
    }

    /// <summary>
    /// A record that fails on a line and then will not close either is lost once, not twice: the run is answered failed,
    /// naming the first failure, <c>result.json</c> is written, and what the close raised does not escape before it.
    /// </summary>
    [Fact]
    public async Task WhereTheRecordFailsAndThenWillNotClose_TheRunFailsNamingTheFirstFailure()
    {
        using var temp = new TempDirectory();
        var disk = new WillNotClose(Disk(), failing: 3);
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };

        var result = await new AgentRunner([client], disk, TimeProvider.System).RunAsync(Step(temp), events: null, TestContext.Current.CancellationToken);

        var error = $"The step's stored stream '{StreamFile(temp)}' could not be written, so the run was stopped: {WillNotClose.Unwritten}";

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(error, result.Error);
        Assert.Equal(1, disk.Closings);
        Assert.Equal(AgentRunStatus.Failed, ReadResult(temp).Status);
        Assert.Equal(error, ReadResult(temp).Error);
    }

    /// <summary>
    /// A record every line of which was written but that will not close is a record lost all the same: its last lines may
    /// not be on the disk, so the run is answered failed, naming the record and what the close raised, and
    /// <c>result.json</c> says so rather than the close's failure escaping before it is written.
    /// </summary>
    [Fact]
    public async Task WhereTheRecordWillNotClose_TheRunFailsNamingIt()
    {
        using var temp = new TempDirectory();
        var disk = new WillNotClose(Disk(), failing: null);
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };

        var result = await new AgentRunner([client], disk, TimeProvider.System).RunAsync(Step(temp), events: null, TestContext.Current.CancellationToken);

        var error = $"The step's stored stream '{StreamFile(temp)}' could not be closed whole, so its last lines may not be on the disk: {WillNotClose.Unclosed}";

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(error, result.Error);
        Assert.Equal(["runStarted", "sessionStarted", "assistantMessage", "runFinished"], Lines(temp).Select(LineKind));
        Assert.Equal(error, ReadResult(temp).Error);
    }

    /// <summary>
    /// A callback on the stop the runner raised for a lost record that throws - the client's, or whoever holds the token -
    /// is kept as a listener's failure is: the run's end and <c>result.json</c> are written, saying the run failed for
    /// its record, and the callback's failure is raised after them.
    /// </summary>
    [Fact]
    public async Task ACallbackOnTheStopThatThrows_IsRaisedAfterTheResultIsWritten()
    {
        using var temp = new TempDirectory();
        var disk = new SlowToFail(Disk(), failing: 3, after: TimeSpan.Zero);
        var client = new FakeClient
        {
            Script = async (_, events, token) =>
            {
                await events.EmitAsync(new MessageSent("one", AgentMessageKind.FirstPrompt));
                token.Register(static () => throw new InvalidOperationException("the callback broke"));
                await events.EmitAsync(new AssistantMessage("two", []));
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("never reached");
            },
        };

        var raised = await Assert.ThrowsAsync<AggregateException>(
            () => new AgentRunner([client], disk, TimeProvider.System)
                .RunAsync(Step(temp), events: null, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal(["the callback broke"], raised.InnerExceptions.Select(inner => inner.Message));
        Assert.Equal(["runStarted", "messageSent"], Lines(temp).Select(LineKind));
        Assert.Equal(AgentRunStatus.Failed, ReadResult(temp).Status);
        Assert.Equal(
            $"The step's stored stream '{StreamFile(temp)}' could not be written, so the run was stopped: {SlowToFail.Why}",
            ReadResult(temp).Error);
    }

    /// <summary>
    /// <c>result.json</c> is written only once the stream is whole: a listener handed the run's end finds the stream
    /// ending in it, and no result written yet.
    /// </summary>
    [Fact]
    public async Task TheResultIsWritten_OnlyOnceTheStreamIsWhole()
    {
        using var temp = new TempDirectory();
        bool? resultThere = null;
        string? lastLine = null;
        var listener = new CallbackSink(
            @event =>
            {
                if (@event is RunFinished)
                {
                    resultThere = File.Exists(ResultFile(temp));
                    lastLine = ReadBeside(StreamFile(temp))[^1];
                }
            });
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };

        await Runner(client).RunAsync(Step(temp), listener, TestContext.Current.CancellationToken);

        Assert.False(resultThere);
        Assert.Equal("runFinished", LineKind(lastLine!));
        Assert.True(File.Exists(ResultFile(temp)));
    }

    /// <summary>
    /// The run's own record and its stream are written through the file system the runner was given: the stream opened
    /// for appending, and <c>result.json</c> put in place in one step, so one that is there was written whole.
    /// </summary>
    [Fact]
    public async Task TheRecordsAreWritten_ThroughTheFileSystemTheRunnerWasGiven()
    {
        using var temp = new TempDirectory();
        var disk = new RecordingDisk(Disk());
        var client = new FakeClient { Script = async (_, events, _) => await Scripted(events) };

        await new AgentRunner([client], disk, TimeProvider.System).RunAsync(Step(temp), events: null, TestContext.Current.CancellationToken);

        Assert.Equal([StreamFile(temp)], disk.Appended);
        Assert.Equal([ResultFile(temp)], disk.Written);
    }

    /// <summary>
    /// Stream yields what happens as it happens: the first events reach the reader while the client is still at work,
    /// waiting on what the reader does next, and the rest follow until the run has ended.
    /// </summary>
    [Fact]
    public async Task Stream_YieldsWhatHappensAsItHappens()
    {
        using var temp = new TempDirectory();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            Script = async (run, events, _) =>
            {
                await events.EmitAsync(new MessageSent("begun", AgentMessageKind.FirstPrompt));
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(60));
                await events.EmitAsync(new AssistantMessage("done", []));

                return Completed(run);
            },
        };

        var reading = Runner(client).Stream(Step(temp), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var seen = new List<AgentEvent>();

        try
        {
            // The client waits for the gate, which only this reader opens: the two events before it come while it waits.
            for (var count = 0; count < 2; count++)
            {
                Assert.True(await reading.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
                seen.Add(reading.Current);
            }

            gate.SetResult();

            while (await reading.MoveNextAsync())
            {
                seen.Add(reading.Current);
            }
        }
        finally
        {
            await reading.DisposeAsync();
        }

        Assert.Equal(["runStarted", "messageSent", "assistantMessage", "runFinished"], seen.Select(Kind));
        Assert.Equal([1L, 2L, 3L, 4L], seen.Select(@event => @event.Seq));
    }

    /// <summary>
    /// A reader that stops early leaves the run to finish on its own, writing its whole record, and what the run then
    /// raises is seen to: a failure no reader is left to take is never raised unobserved.
    /// </summary>
    [Fact]
    public async Task AReaderThatStopsEarly_LeavesTheRunToFinishAlone_AndItsFailureIsSeenTo()
    {
        using var temp = new TempDirectory();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unobserved = new List<Exception>();
        var client = new FakeClient
        {
            Script = async (_, events, _) =>
            {
                await events.EmitAsync(new MessageSent("begun", AgentMessageKind.FirstPrompt));
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(30));
                throw new InvalidOperationException("the client failed alone");
            },
        };

        void Seen(object? sender, UnobservedTaskExceptionEventArgs raised)
        {
            if (raised.Exception.Flatten().InnerExceptions.Any(inner => inner.Message == "the client failed alone"))
            {
                lock (unobserved)
                {
                    unobserved.Add(raised.Exception);
                }
            }
        }

        TaskScheduler.UnobservedTaskException += Seen;

        try
        {
            Assert.IsType<RunStarted>(await FirstOfAsync(Runner(client), Step(temp)));

            gate.SetResult();

            await Until(() => File.Exists(ResultFile(temp)));
            await Until(() => ReadResult(temp).Status == AgentRunStatus.Failed);

            for (var round = 0; round < 10; round++)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Seen;
        }

        Assert.Equal(["runStarted", "messageSent", "runFinished"], Lines(temp).Select(LineKind));
        Assert.Empty(unobserved);
    }

    /// <summary>
    /// Where no client can be taken, the run never began and the configuration is why: a record of the step exists all
    /// the same, its two ends saying so.
    /// </summary>
    [Fact]
    public async Task WhereNoClientCanBeTaken_TheRunNeverBegan_SayingTheConfiguration()
    {
        using var temp = new TempDirectory();

        var result = await Runner(new FakeClient()).RunAsync(Step(temp, client: "not-there"), events: null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.NotStarted, result.Status);
        Assert.Equal(AgentNotStartedBecause.ConfigurationWrong, result.NotStartedBecause);
        Assert.Equal("No agent client named 'not-there' is registered.", result.Error);
        Assert.Equal(HarnessExit.ConfigInvalid, result.ToOutcome().ExitCode);

        // The lists the plan says are always filled are, even here.
        Assert.Empty(result.Files);
        Assert.Empty(result.Served);
        Assert.Empty(result.Assumptions);

        Assert.Equal(["runStarted", "runFinished"], Lines(temp).Select(LineKind));
        Assert.Equal("""{"kind":"runFinished","status":"NotStarted","error":"No agent client named 'not-there' is registered."}""", WithoutWhenAndWhere(Lines(temp)[^1]));
        Assert.Equal(AgentRunStatus.NotStarted, ReadResult(temp).Status);
    }

    /// <summary>
    /// A step that names no client is worked by the one there is; where there are two, none is taken, and the run never
    /// began, saying which are registered.
    /// </summary>
    [Fact]
    public async Task WhereTwoClientsAreRegisteredAndNoneIsNamed_TheRunNeverBegan_NamingThem()
    {
        using var temp = new TempDirectory();
        var first = new FakeClient();
        var second = new FakeClient { Name = "other" };

        var result = await new AgentRunner([first, second], Disk(), TimeProvider.System).RunAsync(Step(temp), events: null, TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.NotStarted, result.Status);
        Assert.Equal(AgentNotStartedBecause.ConfigurationWrong, result.NotStartedBecause);
        Assert.Equal(
            "The step names no agent client, and 2 are registered ('fake', 'other'): where there is more than one, a step names the one it is worked by.",
            result.Error);
        Assert.Null(first.Run);
        Assert.Null(second.Run);
    }

    /// <summary>
    /// The prompt is filled in one pass: a value that names another value is put in as it is, never filled in its turn.
    /// </summary>
    [Fact]
    public async Task ThePromptIsFilledInOnePass_AValueNamingAnotherIsPutInAsItIs()
    {
        using var temp = new TempDirectory();
        var client = new FakeClient();

        await Runner(client).RunAsync(
            Step(temp, template: "do {a} then {b}, leaving {c}", promptValues: new Dictionary<string, string> { ["a"] = "{b}", ["b"] = "x" }),
            events: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("do {b} then x, leaving {c}", client.Run!.UserPrompt);
    }

    /// <summary>
    /// The agent's bound for a handoff is the step's own where it names one, never the stall's, and three minutes where
    /// it names none.
    /// </summary>
    [Theory]
    [InlineData(42, 42)]
    [InlineData(null, 180)]
    [InlineData(0, 180)]
    public async Task TheHandoffBound_IsTheStepsOwn_ElseThreeMinutes(int? handoffSeconds, int bound)
    {
        using var temp = new TempDirectory();
        var client = new FakeClient();

        await Runner(client).RunAsync(Step(temp, handoffSeconds: handoffSeconds), events: null, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(bound), client.Run!.Handoff.Bound);
        Assert.Equal(AgentLimits.DefaultHandoffBound, TimeSpan.FromSeconds(180));
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

        var pump = new AgentEventPump(slow, TimeProvider.System, TestContext.Current.CancellationToken);

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

        Assert.NotSame(producing, sprint);

        gate.SetResult();

        await producing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await pump.DisposeAsync();

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

    /// <summary>The scripted work: a launch, a message with its delta.</summary>
    private static async Task<AgentRunResult> Scripted(IAgentEventSink events)
    {
        await events.EmitAsync(new SessionStarted(FakeClient.ClientName, "model-x", "hash", [], [], HandedConversation: false));
        await events.EmitAsync(new TextDelta("work"));
        await events.EmitAsync(new AssistantMessage("work done", []));

        return new AgentRunResult(AgentRunStatus.Completed, FinalText: "work done", Turns: 1) { Served = [new AgentServedModel("model-x", 1)] };
    }

    private static AgentRunResult Completed(AgentRun run)
        => new(AgentRunStatus.Completed, FinalText: "work done", Turns: 1) { Served = [new AgentServedModel(run.Model ?? "model-x", 1)] };

    /// <summary>A runner over one client, on the real file system and clock.</summary>
    private static AgentRunner Runner(IAgentClient client) => new([client], Disk(), TimeProvider.System);

    /// <summary>The real file system.</summary>
    private static PhysicalFileSystem Disk() => new(FilePermissionsFactory.Create());

    /// <summary>The first event a stream yields, its reader stopping there: done in a method of its own, so that nothing of the stream outlives it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<AgentEvent> FirstOfAsync(AgentRunner runner, AgentStepRequest step)
    {
        await foreach (var @event in runner.Stream(step, TestContext.Current.CancellationToken))
        {
            return @event;
        }

        throw new InvalidOperationException("The stream yielded nothing.");
    }

    /// <summary>Waits until <paramref name="condition"/> holds, failing the test where it does not within thirty seconds.</summary>
    private static async Task Until(Func<bool> condition)
    {
        var bound = DateTime.UtcNow.AddSeconds(30);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < bound, "the condition never came to hold");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>One step to work, on a repository of a temporary directory.</summary>
    private static AgentStepRequest Step(
        TempDirectory temp,
        string? client = null,
        Dictionary<string, string>? promptValues = null,
        string template = "do the {item}",
        int? handoffSeconds = null,
        int attempt = 1)
        => new(
            "r1",
            "a7",
            "s1",
            attempt,
            AgentDefinition: null,
            Provider: null,
            Client: client,
            Tier: null,
            Model: "model-x",
            Effort: null,
            Fallback: null,
            SystemPrompt: "be terse",
            PromptTemplate: template,
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
            new AgentLimits(MaxTurns: 10, MaxBudgetUsd: null, MaxDuration: null, StallSeconds: 7, HandoffSeconds: handoffSeconds),
            MayAsk: false,
            AskRounds: 0,
            Restrictions: [],
            Mask: null);

    /// <summary>The agent's work directory, the only place it writes.</summary>
    private static string WorkDirectory(TempDirectory temp)
        => Path.Combine(temp.Path, ".orchestrators", "r1", "work", "a7");

    /// <summary>The step's own directory, where the one owner of the paths puts it.</summary>
    private static string StepDirectory(TempDirectory temp)
        => Path.Combine(temp.Path, ".orchestrators", "r1", "agents", "a7", "steps", "s1");

    /// <summary>The step's stored stream.</summary>
    private static string StreamFile(TempDirectory temp) => Path.Combine(StepDirectory(temp), StoredStream.FileName);

    /// <summary>The run's own record, beside its stream.</summary>
    private static string ResultFile(TempDirectory temp) => Path.Combine(StepDirectory(temp), AgentRunner.ResultFileName);

    /// <summary>The stream's lines, read beside its writer, which may still hold it.</summary>
    private static List<string> Lines(TempDirectory temp) => [.. ReadBeside(StreamFile(temp))];

    /// <summary>The stream's lines, read beside its writer while it is open.</summary>
    private static string[] ReadBeside(string path)
    {
        using var beside = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reading = new StreamReader(beside);

        return reading.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary><c>result.json</c>, read back by the options it was written with.</summary>
    private static AgentRunResult ReadResult(TempDirectory temp)
        => JsonSerializer.Deserialize<AgentRunResult>(File.ReadAllText(ResultFile(temp)), JsonStateFile.Options)!;

    /// <summary>One line without when it happened and where it falls, which the test cannot know: what it carries, whole.</summary>
    private static string WithoutWhenAndWhere(string line)
    {
        var node = JsonNode.Parse(line)!.AsObject();
        node.Remove("at");
        node.Remove("seq");
        return node.ToJsonString(JsonStateFile.LineOptions);
    }

    /// <summary>One line's event kind, as its own form names it.</summary>
    private static string LineKind(string line) => (string)JsonNode.Parse(line)!["kind"]!;

    /// <summary>One event's kind, as its own form names it.</summary>
    private static string Kind(AgentEvent @event)
        => (string)JsonNode.Parse(JsonSerializer.Serialize(@event, JsonStateFile.LineOptions))!["kind"]!;

    /// <summary>A client that works whatever its script says, remembering the run it was given.</summary>
    private sealed class FakeClient : IAgentClient
    {
        /// <summary>The fake's own name, as a step names it.</summary>
        public const string ClientName = "fake";

        public string Name { get; init; } = ClientName;

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

    /// <summary>A sink that takes events until the one numbered <paramref name="from"/>, which it holds for good.</summary>
    private sealed class HangingSink(Task never, long from) : IAgentEventSink
    {
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Done once it holds an event.</summary>
        public Task Holding => _holding.Task;

        public ValueTask EmitAsync(AgentEvent @event)
        {
            if (@event.Seq < from)
            {
                return ValueTask.CompletedTask;
            }

            _holding.TrySetResult();
            return new ValueTask(never);
        }
    }

    /// <summary>
    /// The real file system, whose appends to a file opened for appending fail from the one numbered
    /// <paramref name="failing"/> on, each only after <paramref name="after"/>: a disk slow to say it is gone.
    /// </summary>
    private sealed class SlowToFail(IFileSystem inner, int failing, TimeSpan after) : PassThroughFileSystem(inner)
    {
        /// <summary>What the disk says once it fails.</summary>
        public const string Why = "the disk went away";

        public override IAppendFile OpenForAppend(string path) => new Appending(base.OpenForAppend(path), failing, after);

        private sealed class Appending(IAppendFile file, int failing, TimeSpan after) : IAppendFile
        {
            private int _appended;

            public long Length => file.Length;

            public void ReadExactlyAt(long position, Span<byte> buffer) => file.ReadExactlyAt(position, buffer);

            public void TruncateTo(long length) => file.TruncateTo(length);

            public void AppendFlushed(ReadOnlySpan<byte> bytes)
            {
                if (++_appended >= failing)
                {
                    Thread.Sleep(after);
                    throw new IOException(Why);
                }

                file.AppendFlushed(bytes);
            }

            public void Dispose() => file.Dispose();
        }
    }

    /// <summary>
    /// The real file system, whose appends to a file opened for appending fail from the one numbered
    /// <paramref name="failing"/> on, where it names one, and whose file then will not close: a disk that failed a flush,
    /// and fails it again when the file is closed.
    /// </summary>
    private sealed class WillNotClose(IFileSystem inner, int? failing) : PassThroughFileSystem(inner)
    {
        /// <summary>What the disk says to an append.</summary>
        public const string Unwritten = "the flush did not reach the disk";

        /// <summary>What the disk says to the close.</summary>
        public const string Unclosed = "the flush did not reach the disk at the close either";

        /// <summary>How many times a file of it was closed.</summary>
        public int Closings { get; private set; }

        public override IAppendFile OpenForAppend(string path) => new Appending(base.OpenForAppend(path), this);

        private sealed class Appending(IAppendFile file, WillNotClose disk) : IAppendFile
        {
            private int _appended;

            public long Length => file.Length;

            public void ReadExactlyAt(long position, Span<byte> buffer) => file.ReadExactlyAt(position, buffer);

            public void TruncateTo(long length) => file.TruncateTo(length);

            public void AppendFlushed(ReadOnlySpan<byte> bytes)
            {
                if (++_appended >= disk.FailingFrom)
                {
                    throw new IOException(Unwritten);
                }

                file.AppendFlushed(bytes);
            }

            public void Dispose()
            {
                disk.Closings++;
                file.Dispose();
                throw new IOException(Unclosed);
            }
        }

        private int FailingFrom => failing ?? int.MaxValue;
    }

    /// <summary>The real file system, recording what is opened for appending and what is written in one step.</summary>
    private sealed class RecordingDisk(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public List<string> Appended { get; } = [];

        public List<string> Written { get; } = [];

        public override IAppendFile OpenForAppend(string path)
        {
            Appended.Add(path);
            return base.OpenForAppend(path);
        }

        public override void WriteAllTextAtomic(string path, string contents)
        {
            Written.Add(path);
            base.WriteAllTextAtomic(path, contents);
        }
    }
}
