using System.Diagnostics;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// The output of one operation beside the terminal: every event stamped with the operation, the sequence and the time
/// as it is published; a slow sink never holding a writer and given every event in the end, those it lagged behind from
/// the operation's event file; a sink that throws swallowed once, changing no exit code.
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
    /// Ten thousand events published to a sink held still stall no writer and lose none: the first wait in memory, the
    /// rest in the operation's event file, and once the sink is let go it is given every one of them, in sequence order,
    /// each as it was stamped - never a few thousand and a count of the rest, as a queue that dropped past its bound once
    /// gave it.
    /// </summary>
    [Fact]
    public async Task TenThousandEventsToAStalledSink_LoseNone()
    {
        var held = new TaskCompletionSource();
        var sink = new HeldSink(held.Task);
        var operation = OperationId.New();
        using var temp = new TempDirectory();
        var spill = temp.Combine("events");
        using var output = new EventHarnessOutput(sink, operation, spill: () => new EventSpill(Files, spill));

        var writing = await ParallelAsync(10_000, index => PublishAsync(output, index));

        Assert.True(writing < TimeSpan.FromSeconds(60), $"the writers were held {writing}");

        // The sink holds the first it was given and memory its page: the rest wait on disk, in the event file.
        Assert.InRange(output.WaitingOnDisk, 10_000 - EventHarnessOutput.HeldInMemory - 1, 10_000 - EventHarnessOutput.HeldInMemory);
        Assert.True(File.Exists(spill));

        held.SetResult();
        output.Dispose();

        Assert.Equal(Enumerable.Range(1, 10_000), sink.Seen.Select(@event => (int)@event.Sequence));
        Assert.All(sink.Seen, @event => Assert.Equal(operation, Assert.IsType<TextLine>(@event).Operation));
        Assert.Equal(0, output.WaitingOnDisk);
        Assert.False(File.Exists(spill));
    }

    /// <summary>
    /// An event that waits on disk reaches the sink exactly as it was published - its kind, its words, its operation, its
    /// sequence and its time - in its place among the rest; and one that the event file would not read back as it wrote
    /// it, a run's beginning, is held in memory in its place and given as itself.
    /// </summary>
    [Fact]
    public async Task EventsThatWaitOnDisk_AreGivenAsTheyWerePublished_InTheirPlace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource();
        var queued = EventHarnessOutput.HeldInMemory;
        var sink = new GatedSink(release.Task, awaited: queued + 4);
        var operation = OperationId.New();
        var clock = new ManualClock(Start);
        using var temp = new TempDirectory();
        using var output = new EventHarnessOutput(sink, operation, clock: clock, spill: () => new EventSpill(Files, temp.Combine("events")));
        var run = DssHarness.Core.Execution.RunId.New();

        // The first event holds the sink and so leaves memory empty: the next ones fill it to its page exactly, and the
        // three after them wait on disk, a run's beginning among them.
        Publish(1);
        await sink.Holding.WaitAsync(Bound, cancellationToken);

        for (var sequence = 2; sequence <= queued + 1; sequence++)
        {
            Publish(sequence);
        }

        Publish(queued + 2);
        clock.Advance(TimeSpan.FromSeconds(1));
        output.Publish(new DssHarness.Core.Application.RunBegan("run", run));
        Publish(queued + 4);

        Assert.Equal(3, output.WaitingOnDisk);

        release.SetResult();
        await sink.Given.WaitAsync(Bound, cancellationToken);

        Assert.Equal(
            [
                .. Enumerable.Range(1, queued + 2).Select(Published),
                new DssHarness.Core.Application.RunBegan("run", run) { Operation = operation, Sequence = queued + 3, At = Start.AddSeconds(queued + 3) },
                Published(queued + 4),
            ],
            sink.Seen);
        Assert.Same(run, ((DssHarness.Core.Application.RunBegan)sink.Seen[queued + 2]).Run);

        // Each event is published a second after the one before it, so each is stamped with a time of its own.
        void Publish(int sequence)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            output.Info("leg", $"event {sequence}");
        }

        RunEvent Published(int sequence)
            => new TextLine(LineKind.Info, "leg", $"event {sequence}") { Operation = operation, Sequence = sequence, At = Start.AddSeconds(sequence) };
    }

    /// <summary>
    /// What is published once the output is closed, before the sink has been given the rest, is given after it, in its
    /// place; once the sink has been told the last of it, a publish is refused, never lost without a word.
    /// </summary>
    [Fact]
    public void WhatIsPublishedOnceTheOutputIsClosed_IsGivenAfterTheRest_AndAPublishAfterIsRefused()
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

        held.SetResult();
        output.Dispose();

        RunEvent[] expected =
        [
            new TextLine(LineKind.Info, "leg", "before the close") { Operation = operation, Sequence = 1, At = Start.AddSeconds(1) },
            new TextLine(LineKind.Info, "leg", "after the close") { Operation = operation, Sequence = 2, At = Start.AddSeconds(2) },
        ];

        Assert.Equal(expected, sink.Seen);
        Assert.Throws<ObjectDisposedException>(() => output.Info("leg", "after it was told the last"));
    }

    /// <summary>
    /// Disposing waits for a slow sink to be given every event that waits, however long it takes - here six seconds, past
    /// the five a drain was once given before the sink was abandoned and what it had not been given was lost.
    /// </summary>
    [Fact]
    public void Dispose_DrainsASlowSink()
    {
        var sink = new SlowSink(TimeSpan.FromMilliseconds(150));
        var output = new EventHarnessOutput(sink, OperationId.New());

        for (var index = 0; index < 40; index++)
        {
            output.Info("leg", $"event {index}");
        }

        var started = Stopwatch.GetTimestamp();
        output.Dispose();

        Assert.True(Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5), "the sink was given everything before the old limit, so the test proves nothing");
        Assert.Equal(Enumerable.Range(0, 40).Select(index => $"event {index}"), sink.Seen.OfType<TextLine>().Select(line => line.Message));
    }

    /// <summary>
    /// An event file that cannot be read back is said to the sink where its events fell - how many, and why - and every
    /// event held in memory in its place, and every one after it, is still given: a loss is never passed over.
    /// </summary>
    [Fact]
    public void AnEventFileThatCannotBeReadBack_IsSaidWhereItsEventsFell()
    {
        using var temp = new TempDirectory();
        var spill = new EventSpill(new UnreadableAppends(Files), temp.Combine("events"));
        var run = DssHarness.Core.Execution.RunId.New();
        var began = new DssHarness.Core.Application.RunBegan("run", run);

        spill.Add(new TextLine(LineKind.Info, "leg", "one"));
        spill.Add(began);
        spill.Add(new TextLine(LineKind.Info, "leg", "three"));

        var unread = Assert.IsType<EventsUnread>(spill.Take());

        Assert.Equal((2L, $"the event file '{temp.Combine("events")}' could not be read back: {UnreadableAppends.Why}"), (unread.Count, unread.Why));
        Assert.Same(began, spill.Take());
        Assert.Equal(0, spill.Waiting);
        spill.Dispose();
    }

    /// <summary>The real file system, for the event files these tests make.</summary>
    private static readonly PhysicalFileSystem Files = new(FilePermissionsFactory.Create());

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

    /// <summary>A sink that takes a while over each event it is given.</summary>
    private sealed class SlowSink(TimeSpan each) : IRunEvents
    {
        public List<RunEvent> Seen { get; } = [];

        public void Emit(RunEvent @event)
        {
            Thread.Sleep(each);
            Seen.Add(@event);
        }
    }

    /// <summary>A file system whose files of lines are written and never read back.</summary>
    private sealed class UnreadableAppends(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        /// <summary>What a read back says.</summary>
        public const string Why = "the disk would not give it back";

        public override IAppendFile OpenForAppend(string path) => new Unreadable(base.OpenForAppend(path));

        private sealed class Unreadable(IAppendFile inner) : IAppendFile
        {
            public long Length => inner.Length;

            public void ReadExactlyAt(long position, Span<byte> buffer) => throw new IOException(Why);

            public void TruncateTo(long length) => inner.TruncateTo(length);

            public void AppendFlushed(ReadOnlySpan<byte> bytes) => inner.AppendFlushed(bytes);

            public void Dispose() => inner.Dispose();
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
