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
    /// to the sink before the next one it is given once it moves.
    /// </summary>
    [Fact]
    public async Task ASlowSink_StallsNoWriter_AndIsToldWhatItMissed()
    {
        var held = new TaskCompletionSource();
        var sink = new HeldSink(held.Task);
        using var output = new EventHarnessOutput(sink, OperationId.New());

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
        Assert.Equal(
            delivered.Select(@event => (int)@event.Sequence).OrderBy(sequence => sequence).ToList(),
            delivered.Select(@event => (int)@event.Sequence).ToList());
        Assert.True(
            sink.Seen[0] is EventsDropped || sink.Seen[1] is EventsDropped,
            "the sink was told what it missed before the events that followed it");
    }

    /// <summary>A sink that throws is swallowed once and never called again, and no writer and no disposal is the worse for it.</summary>
    [Fact]
    public async Task ASinkThatThrows_IsSwallowedOnce_AndNeverCalledAgain()
    {
        var sink = new ThrowingSink();
        using var output = new EventHarnessOutput(sink, OperationId.New());

        await ParallelAsync(20, index => PublishAsync(output, index));
        output.Dispose();

        Assert.Equal(1, sink.Calls);
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
