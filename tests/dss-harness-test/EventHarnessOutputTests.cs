using System.Diagnostics;
using DssHarness.Core.Output;

namespace DssHarness.Tests;

/// <summary>
/// The output of one operation beside the terminal: every event stamped with the operation, the sequence and the time
/// as it is published; a slow sink never holding a writer, but told what it missed; a sink that throws swallowed once,
/// changing no exit code.
/// </summary>
public sealed class EventHarnessOutputTests
{
    /// <summary>Where the manual clocks of these tests start, so each event's time is known before it is published.</summary>
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    /// <summary>How long a test waits for the sink at most: never reached by an output that works, only by one that hangs.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Parallel publishes reach a queued sink once each, in sequence order, stamped with the operation they belong to
    /// and when they happened - the stamp the publisher's, never the maker's.
    /// </summary>
    [Fact]
    public async Task ParallelPublishes_ReachAQueuedSinkOnceEach_InSequenceOrder()
    {
        var sink = new RecordingSink();
        var operation = OperationId.New();
        using (var output = new EventHarnessOutput(sink, operation))
        {
            await ParallelAsync(400, index => PublishAsync(output, index));
        }

        Assert.Equal(400, sink.Seen.Count);
        Assert.Equal(Enumerable.Range(1, 400), sink.Seen.Select(@event => (int)@event.Sequence));
        Assert.All(sink.Seen, @event =>
        {
            Assert.Equal(operation, @event.Operation);
            Assert.Null(@event.Parent);
            Assert.NotEqual(default, @event.At);
        });
    }

    /// <summary>
    /// A sink held still stalls no writer: events queue to their bound, and past it they are dropped and counted, said
    /// to the sink where they fell - after what it was given before them, before what it is given after - stamped with
    /// the operation and the sequence of the last of them, so everything the sink is given stands in sequence order.
    /// </summary>
    [Fact]
    public async Task ASlowSink_StallsNoWriter_AndIsToldWhatItMissed()
    {
        var held = new TaskCompletionSource();
        var sink = new HeldSink(held.Task);
        var operation = OperationId.New();
        using var output = new EventHarnessOutput(sink, operation);

        // The first event the sink is given holds it; the queue takes its bound, and what will not fit is dropped.
        var total = EventHarnessOutput.QueueLimit + 501;

        var writing = await ParallelAsync(total, index => PublishAsync(output, index));

        Assert.True(writing < TimeSpan.FromSeconds(15), $"the writers were held {writing}");

        held.SetResult();
        output.Dispose();

        // Everything published is either delivered or counted as dropped - the sink holds at most one and the queue
        // its bound, so that is what was delivered - and the count reached the sink before the events that followed
        // what it missed.
        var dropped = sink.Seen.OfType<EventsDropped>().Sum(told => told.Count);
        var delivered = sink.Seen.Where(@event => @event is not EventsDropped).ToList();

        Assert.InRange(delivered.Count, EventHarnessOutput.QueueLimit, EventHarnessOutput.QueueLimit + 1);
        Assert.Equal(total, dropped + delivered.Count);

        // Every gap is told where it fell: the event before it is the one just before the first it stands for, and the
        // gap carries the sequence of the last - so the sink is given everything in sequence order, gaps included.
        Assert.Equal(
            sink.Seen.Select(@event => @event.Sequence).Order().ToList(),
            sink.Seen.Select(@event => @event.Sequence).ToList());
        Assert.Equal(sink.Seen.Count, sink.Seen.Select(@event => @event.Sequence).Distinct().Count());

        foreach (var (gap, index) in sink.Seen.Select((@event, index) => (@event, index)).Where(seen => seen.@event is EventsDropped))
        {
            var told = (EventsDropped)gap;

            Assert.Equal(operation, told.Operation);
            Assert.Equal(told.Sequence - told.Count, index == 0 ? 0 : sink.Seen[index - 1].Sequence);
        }

        Assert.Equal(total, sink.Seen[^1].Sequence);
    }

    /// <summary>
    /// Events dropped while a queued sink is held still are told to it once it has room again, exactly where they fell:
    /// their count, stamped with the operation and the sequence and time of the last of them, after the last event the
    /// queue took before them and before the first it takes after - told once, and never again at the close.
    /// </summary>
    [Fact]
    public async Task EventsDroppedWhileASinkIsHeld_AreToldBetweenTheEventsTheyFellBetween()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();
        var queued = EventHarnessOutput.QueueLimit;
        var sink = new GatedSink(release.Task, awaited: queued + 1);
        var operation = OperationId.New();
        var clock = new ManualClock(Start);
        using var output = new EventHarnessOutput(sink, operation, clock: clock);

        // The first event holds the sink and so leaves the queue empty: the next ones fill it to its bound exactly, and
        // the three after them find it full.
        Publish(1);
        await sink.Holding.WaitAsync(Bound, cancellationToken);

        for (var sequence = 2; sequence <= queued + 4; sequence++)
        {
            Publish(sequence);
        }

        // Once the sink has been given everything the queue held there is room again, and the next event is taken with
        // the gap before it.
        release.SetResult();
        await sink.Given.WaitAsync(Bound, cancellationToken);
        Publish(queued + 5);
        output.Dispose();

        RunEvent[] expected =
        [
            .. Enumerable.Range(1, queued + 1).Select(Published),
            new EventsDropped(3) { Operation = operation, Sequence = queued + 4, At = Start.AddSeconds(queued + 4) },
            Published(queued + 5),
        ];

        Assert.Equal(expected, sink.Seen);
        Assert.False(output.Abandoned);

        // Each event is published a second after the one before it, so each is stamped with a time of its own.
        void Publish(int sequence)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            output.Info("leg", $"event {sequence}");
        }

        TextLine Published(int sequence)
            => new(LineKind.Info, "leg", $"event {sequence}") { Operation = operation, Sequence = sequence, At = Start.AddSeconds(sequence) };
    }

    /// <summary>
    /// What is published once the queue is closed, before the sink has been given the rest, is dropped and told last,
    /// stamped where it fell; once the sink has been told the last of it, a publish is refused, never lost without a word.
    /// </summary>
    [Fact]
    public void WhatIsPublishedOnceTheQueueIsClosed_IsToldLast_AndAPublishAfterIsRefused()
    {
        var held = new TaskCompletionSource();
        var sink = new HeldSink(held.Task);
        var operation = OperationId.New();
        var clock = new ManualClock(Start);
        var output = new EventHarnessOutput(sink, operation, clock: clock);

        clock.Advance(TimeSpan.FromSeconds(1));
        output.Info("leg", "before the close");
        output.Complete();
        clock.Advance(TimeSpan.FromSeconds(1));
        output.Info("leg", "after the close");
        clock.Advance(TimeSpan.FromSeconds(1));
        output.Info("leg", "after the close, again");

        // Time passes before the sink is told: the notice is stamped when the last event it stands for was published,
        // not when it is told.
        clock.Advance(TimeSpan.FromSeconds(1));
        held.SetResult();
        output.Dispose();

        RunEvent[] expected =
        [
            new TextLine(LineKind.Info, "leg", "before the close") { Operation = operation, Sequence = 1, At = Start.AddSeconds(1) },
            new EventsDropped(2) { Operation = operation, Sequence = 3, At = Start.AddSeconds(3) },
        ];

        Assert.Equal(expected, sink.Seen);
        Assert.False(output.Abandoned);
        Assert.Throws<ObjectDisposedException>(() => output.Info("leg", "after it was told the last"));
    }

    /// <summary>
    /// A sink still behind when the output is disposed is waited for no longer than its limit, then abandoned, said so,
    /// and called no more: what it had not been given by then never reaches it.
    /// </summary>
    [Fact]
    public async Task ASinkStillBehindWhenDisposed_IsAbandoned_AndCalledNoMore()
    {
        var held = new TaskCompletionSource();
        var sink = new GatedSink(held.Task, awaited: 1);
        var output = new EventHarnessOutput(sink, OperationId.New(), drainLimit: TimeSpan.FromMilliseconds(50));

        output.Info("leg", "one");

        // Behind on the first, for certain: on a busy machine the drain may not have reached it within the limit.
        await sink.Holding.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        output.Info("leg", "two");
        output.Info("leg", "three");
        output.Dispose();

        Assert.True(output.Abandoned);

        held.SetResult();

        // Told the last of it once the drain finds the sink abandoned: a publish then is refused.
        var bound = Stopwatch.GetTimestamp();

        while (!Refused(output))
        {
            Assert.True(Stopwatch.GetElapsedTime(bound) < TimeSpan.FromSeconds(10), "the drain never finished");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["one"], sink.Seen.OfType<TextLine>().Select(line => line.Message));
        Assert.Single(sink.Seen);
    }

    /// <summary>
    /// A sink that throws is swallowed once and never called again, and no writer and no disposal is the worse for it -
    /// but what it threw is kept for its front end to read, never lost.
    /// </summary>
    [Fact]
    public async Task ASinkThatThrows_IsSwallowedOnce_AndNeverCalledAgain()
    {
        var sink = new ThrowingSink();
        using var output = new EventHarnessOutput(sink, OperationId.New());

        await ParallelAsync(20, index => PublishAsync(output, index));
        output.Dispose();

        Assert.Equal(1, sink.Calls);
        Assert.Equal("the front end broke", Assert.IsType<InvalidOperationException>(output.SinkFault).Message);
    }

    /// <summary>Whether a publish to <paramref name="output"/> is refused, as one is once its sink has been told the last of it.</summary>
    private static bool Refused(EventHarnessOutput output)
    {
        try
        {
            output.Info("leg", "is it over");
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static async Task<TimeSpan> ParallelAsync(int count, Func<int, ValueTask> each)
    {
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForAsync(0, count, async (index, _) => await each(index));
        return Stopwatch.GetElapsedTime(started);
    }

    private static ValueTask PublishAsync(EventHarnessOutput output, int index)
    {
        output.Info("leg", new string((char)('a' + (index % 26)), 64));
        return ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : IRunEvents
    {
        public List<RunEvent> Seen { get; } = [];

        public void Emit(RunEvent @event) => Seen.Add(@event);
    }

    private sealed class HeldSink(Task release) : IRunEvents
    {
        private readonly Task _release = release;

        public List<RunEvent> Seen { get; } = [];

        public void Emit(RunEvent @event)
        {
            _release.GetAwaiter().GetResult();
            Seen.Add(@event);
        }
    }

    /// <summary>
    /// A sink held still on the first event it is given until released, which says when it is holding one and when it has
    /// been given the number of events it awaits.
    /// </summary>
    private sealed class GatedSink(Task release, int awaited) : IRunEvents
    {
        private readonly Task _release = release;
        private readonly int _awaited = awaited;
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _given = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<RunEvent> Seen { get; } = [];

        /// <summary>Completes once the sink has been given its first event and is held on it.</summary>
        public Task Holding => _holding.Task;

        /// <summary>Completes once the sink has been given the number of events it awaits.</summary>
        public Task Given => _given.Task;

        public void Emit(RunEvent @event)
        {
            _holding.TrySetResult();
            _release.GetAwaiter().GetResult();
            Seen.Add(@event);

            if (Seen.Count == _awaited)
            {
                _given.TrySetResult();
            }
        }
    }

    private sealed class ThrowingSink : IRunEvents
    {
        public int Calls { get; private set; }

        public void Emit(RunEvent @event)
        {
            Calls++;
            throw new InvalidOperationException("the front end broke");
        }
    }
}
