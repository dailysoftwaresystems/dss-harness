using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// The one inotify instance a process watches through on Linux, over calls a test answers: what it adds and removes,
/// what it tells each span and when it tells nothing, and what it says where a limit is met - on every system, with
/// nothing taken from the machine's own instances or watches.
/// </summary>
public sealed class InotifyHubTests
{
    private const string A = "/tree/a";
    private const string B = "/tree/b";
    private const string C = "/tree/c";

    private static readonly InotifyHolder[] Holding =
    [
        new(4242, "dssharness", 1, 211),
        new(99, "node", 3, 5800),
    ];

    /// <summary>
    /// Two spans naming one directory share the one descriptor the kernel gives it, and each is told of it; the
    /// descriptor stays while either watches it, and is removed when the second lets go.
    /// </summary>
    [Fact]
    public async Task TwoSpansNamingOneDirectory_ShareItsDescriptor_RemovedWhenTheSecondLetsGo()
    {
        using var calls = new ScriptedInotifyCalls();
        var hub = Hub(calls);
        var first = new RecordingWatchSink();
        var second = new RecordingWatchSink();

        var firstSpan = hub.Watch([A], first);
        var secondSpan = hub.Watch([A, B], second);

        Assert.Equal([A, A, B], calls.Added.Select(added => added.Path));

        calls.Give(new InotifyEvent(calls.DescriptorOf(A), InotifyBits.Create, "x.cpp"));
        await first.WaitForAsync(RecordingWatchSink.WasNamed, Path.Combine(A, "x.cpp"), TestContext.Current.CancellationToken);
        await second.WaitForAsync(RecordingWatchSink.WasNamed, Path.Combine(A, "x.cpp"), TestContext.Current.CancellationToken);

        var a = calls.DescriptorOf(A);
        var b = calls.DescriptorOf(B);

        firstSpan.Dispose();
        Assert.Empty(calls.Removed);

        secondSpan.Dispose();
        Assert.Equal([a, b], calls.Removed);
    }

    /// <summary>A span let go of is told nothing after, though the instance goes on telling of its directory.</summary>
    [Fact]
    public async Task ASpanLetGoOf_IsToldNothingAfter()
    {
        using var calls = new ScriptedInotifyCalls();
        var hub = Hub(calls);
        var gone = new RecordingWatchSink();
        var staying = new RecordingWatchSink();

        var goneSpan = hub.Watch([A], gone);
        using var stayingSpan = hub.Watch([A, B], staying);
        goneSpan.Dispose();

        calls.Give(new InotifyEvent(calls.DescriptorOf(A), InotifyBits.Modify, "x.cpp"));
        calls.Give(new InotifyEvent(calls.DescriptorOf(B), InotifyBits.Modify, "y.cpp"));
        await staying.WaitForAsync(RecordingWatchSink.WasWritten, Path.Combine(B, "y.cpp"), TestContext.Current.CancellationToken);

        Assert.Contains((RecordingWatchSink.WasWritten, Path.Combine(A, "x.cpp")), staying.Heard);
        Assert.Empty(gone.Heard);
    }

    /// <summary>
    /// This user's watches running out on the third directory removes the two the span had added - but never one
    /// another span still watches - and says the limit, how many directories the work needs, and who holds the watches.
    /// </summary>
    [Fact]
    public void WatchesRunningOutOnTheThirdDirectory_RemoveTheSpansFirstTwo_ButNoneAnotherSpanWatches()
    {
        using var calls = new ScriptedInotifyCalls { AddError = path => path == C ? InotifyErrors.WatchesRanOut : 0 };
        var hub = Hub(calls);
        using var other = hub.Watch([A], new RecordingWatchSink());
        var sink = new RecordingWatchSink();

        var refused = Assert.Throws<InputsUnwatchedException>(() => hub.Watch([A, B, C], sink));

        Assert.Equal(
            "the inputs could not be watched, so none of this work was run: this user's inotify watches ran out - "
            + "fs.inotify.max_user_watches is 8192; this work's inputs lie in 3 directory(ies), one watch each, and /proc "
            + "shows 6011 held: 5800 by pid 99 (node), 211 by pid 4242 (dssharness).",
            refused.Message);
        // A is 1, the other span's as well; B is 2, the refused span's alone.
        Assert.Equal([2], calls.Removed);
        Assert.Equal(1, calls.DescriptorOf(A));
        Assert.Empty(sink.Heard);
    }

    /// <summary>
    /// This user's instances all taken leaves no instance and nothing read, says the limit and who holds them - and is
    /// no reason to stop asking: the next watch makes the instance once one is to be had.
    /// </summary>
    [Fact]
    public void InstancesAllTaken_LeaveNoInstanceAndNothingRead_AndTheNextWatchMakesOne()
    {
        using var calls = new ScriptedInotifyCalls { InitError = InotifyErrors.InstancesTaken };
        var hub = Hub(calls);

        var refused = Assert.Throws<InputsUnwatchedException>(() => hub.Watch([A], new RecordingWatchSink()));

        Assert.Equal(
            "the inputs could not be watched, so none of this work was run: this user's inotify instances are all taken - "
            + "fs.inotify.max_user_instances is 128, and /proc shows 4 held: 3 by pid 99 (node), 1 by pid 4242 "
            + "(dssharness). A leg needs one.",
            refused.Message);
        Assert.Equal(-1, hub.Instance);
        Assert.Empty(calls.Added);
        Assert.Equal(0, calls.Reads);

        calls.InitError = 0;

        using var span = hub.Watch([A], new RecordingWatchSink());

        Assert.Equal(2, calls.InitFlags.Count);
        Assert.NotEqual(-1, hub.Instance);
    }

    /// <summary>Any other refusal is said in the system's own words, naming what it refused.</summary>
    [Fact]
    public void AnyOtherRefusal_IsSaidInTheSystemsWords_NamingTheDirectory()
    {
        const int noSuchDirectory = 2;
        using var calls = new ScriptedInotifyCalls { AddError = path => path == B ? noSuchDirectory : 0 };

        var refused = Assert.Throws<InputsUnwatchedException>(() => Hub(calls).Watch([A, B], new RecordingWatchSink()));

        Assert.Equal($"'{B}': {InotifyErrors.Said(noSuchDirectory)}", refused.Why);
        Assert.Equal([1], calls.Removed);
    }

    /// <summary>
    /// The instance is made close-on-exec, once, so no program a leg starts holds it; every directory is added with the
    /// one mask, which hears the directory itself go or move.
    /// </summary>
    [Fact]
    public void TheInstanceIsMadeOnceCloseOnExec_AndEveryDirectoryAddedWithTheOneMask()
    {
        using var calls = new ScriptedInotifyCalls();
        var hub = Hub(calls);

        using var first = hub.Watch([A, B], new RecordingWatchSink());
        using var second = hub.Watch([C], new RecordingWatchSink());

        Assert.Equal([InotifyBits.CloseOnExec], calls.InitFlags);
        Assert.All(calls.Added, added => Assert.Equal(InotifyBits.Watched, added.Mask));
        Assert.True((InotifyBits.Watched & InotifyBits.DeleteSelf) != 0 && (InotifyBits.Watched & InotifyBits.MoveSelf) != 0);
    }

    /// <summary>
    /// An entry made, removed or moved, by either name of its move, is told by name - a directory as a file is - and an
    /// entry written to, or whose attributes changed, as written.
    /// </summary>
    [Theory]
    [InlineData(InotifyBits.Create, RecordingWatchSink.WasNamed)]
    [InlineData(InotifyBits.Delete, RecordingWatchSink.WasNamed)]
    [InlineData(InotifyBits.MovedFrom, RecordingWatchSink.WasNamed)]
    [InlineData(InotifyBits.MovedTo, RecordingWatchSink.WasNamed)]
    [InlineData(InotifyBits.Create | 0x4000_0000u, RecordingWatchSink.WasNamed)]
    [InlineData(InotifyBits.MovedFrom | 0x4000_0000u, RecordingWatchSink.WasNamed)]
    [InlineData(InotifyBits.Modify, RecordingWatchSink.WasWritten)]
    [InlineData(InotifyBits.Attrib, RecordingWatchSink.WasWritten)]
    public async Task AnEntryMadeRemovedOrMoved_IsNamed_AndOneWrittenTo_IsWritten(uint mask, string told)
    {
        using var calls = new ScriptedInotifyCalls();
        var sink = new RecordingWatchSink();
        using var span = Hub(calls).Watch([A], sink);

        calls.Give(new InotifyEvent(calls.DescriptorOf(A), mask, "entry"));
        await sink.WaitForAsync(told, Path.Combine(A, "entry"), TestContext.Current.CancellationToken);

        Assert.Equal([(told, Path.Combine(A, "entry"))], sink.Heard);
    }

    /// <summary>A rename is told by both its names, each as its own event, with nothing to pair.</summary>
    [Fact]
    public async Task ARename_IsToldByBothItsNames()
    {
        using var calls = new ScriptedInotifyCalls();
        var sink = new RecordingWatchSink();
        using var span = Hub(calls).Watch([A, B], sink);

        calls.Give(
            new InotifyEvent(calls.DescriptorOf(A), InotifyBits.MovedFrom, "old.json"),
            new InotifyEvent(calls.DescriptorOf(B), InotifyBits.MovedTo, "new.json"));
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, Path.Combine(B, "new.json"), TestContext.Current.CancellationToken);

        Assert.Equal(
            [(RecordingWatchSink.WasNamed, Path.Combine(A, "old.json")), (RecordingWatchSink.WasNamed, Path.Combine(B, "new.json"))],
            sink.Heard);
    }

    /// <summary>
    /// The watched directory itself removed, moved, or its watch gone unasked is told by the directory's name - which
    /// takes its inputs with it - and its descriptor is let go: what is said of it after is dropped. A directory moved is
    /// still the instance's, and is removed from it.
    /// </summary>
    [Theory]
    [InlineData(InotifyBits.DeleteSelf, false)]
    [InlineData(InotifyBits.MoveSelf, true)]
    [InlineData(InotifyBits.Ignored, false)]
    public async Task TheWatchedDirectoryItselfGone_IsToldByItsName_AndItsDescriptorLetGo(uint mask, bool removed)
    {
        using var calls = new ScriptedInotifyCalls();
        var sink = new RecordingWatchSink();
        using var span = Hub(calls).Watch([A, B], sink);
        var a = calls.DescriptorOf(A);

        calls.Give(new InotifyEvent(a, mask, string.Empty));
        calls.Give(new InotifyEvent(a, InotifyBits.Create, "after.txt"), new InotifyEvent(calls.DescriptorOf(B), InotifyBits.Create, "sentinel"));
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, Path.Combine(B, "sentinel"), TestContext.Current.CancellationToken);

        Assert.Equal([(RecordingWatchSink.WasNamed, A), (RecordingWatchSink.WasNamed, Path.Combine(B, "sentinel"))], sink.Heard);
        Assert.Equal(removed ? [a] : [], calls.Removed);
    }

    /// <summary>
    /// What is said of a descriptor the hub let go of - the removal it asked for, word that was on its way - or never
    /// had, is dropped: never told, and never taken for events lost.
    /// </summary>
    [Fact]
    public async Task AnEventOfADescriptorTheHubLetGoOfOrNeverHad_IsDropped_NeverLost()
    {
        using var calls = new ScriptedInotifyCalls();
        var hub = Hub(calls);
        var gone = hub.Watch([A], new RecordingWatchSink());
        var sink = new RecordingWatchSink();
        using var span = hub.Watch([B], sink);
        var a = calls.DescriptorOf(A);

        gone.Dispose();
        calls.Give(
            new InotifyEvent(a, InotifyBits.Modify, "late.cpp"),
            new InotifyEvent(a, InotifyBits.Ignored, string.Empty),
            new InotifyEvent(4711, InotifyBits.Create, "never.txt"),
            new InotifyEvent(calls.DescriptorOf(B), InotifyBits.Create, "sentinel"));
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, Path.Combine(B, "sentinel"), TestContext.Current.CancellationToken);

        Assert.Equal([(RecordingWatchSink.WasNamed, Path.Combine(B, "sentinel"))], sink.Heard);
    }

    /// <summary>The instance's queue overflowing loses every span: what any of them was not told is not known.</summary>
    [Fact]
    public async Task AnOverflow_LosesEverySpan()
    {
        using var calls = new ScriptedInotifyCalls();
        var hub = Hub(calls);
        var first = new RecordingWatchSink();
        var second = new RecordingWatchSink();
        using var firstSpan = hub.Watch([A], first);
        using var secondSpan = hub.Watch([B], second);

        calls.Give(new InotifyEvent(-1, InotifyBits.QueueOverflow, string.Empty));

        await WaitLostAsync(first);
        await WaitLostAsync(second);
        Assert.Contains(first.Heard, heard => heard.What == RecordingWatchSink.WasLost && heard.Path.Contains("overflowed", StringComparison.Ordinal));
    }

    /// <summary>A record cut short loses every span, after each whole record before it is told.</summary>
    [Fact]
    public async Task ARecordCutShort_LosesEverySpan_AfterTheWholeRecordsBeforeIt()
    {
        using var calls = new ScriptedInotifyCalls();
        var hub = Hub(calls);
        var first = new RecordingWatchSink();
        var second = new RecordingWatchSink();
        using var firstSpan = hub.Watch([A], first);
        using var secondSpan = hub.Watch([B], second);

        calls.GiveBytes([
            .. InotifyRecord.Of(new InotifyEvent(calls.DescriptorOf(A), InotifyBits.Create, "whole.txt")),
            .. InotifyRecord.Of(new InotifyEvent(calls.DescriptorOf(B), InotifyBits.Create, "cut.txt"))[..20],
        ]);

        await WaitLostAsync(first);
        await WaitLostAsync(second);
        Assert.Equal((RecordingWatchSink.WasNamed, Path.Combine(A, "whole.txt")), first.Heard[0]);
        Assert.DoesNotContain(second.Heard, heard => heard.What == RecordingWatchSink.WasNamed);
    }

    /// <summary>A name that is no UTF-8 loses the spans of its directory, and no other.</summary>
    [Fact]
    public async Task ANameThatIsNoUtf8_LosesTheSpansOfItsDirectoryAlone()
    {
        using var calls = new ScriptedInotifyCalls();
        var hub = Hub(calls);
        var first = new RecordingWatchSink();
        var second = new RecordingWatchSink();
        using var firstSpan = hub.Watch([A], first);
        using var secondSpan = hub.Watch([B], second);

        calls.GiveBytes([
            .. InotifyRecord.One(calls.DescriptorOf(A), InotifyBits.Create, [0x61, 0xff], pad: 16),
            .. InotifyRecord.Of(new InotifyEvent(calls.DescriptorOf(B), InotifyBits.Create, "sentinel")),
        ]);
        await second.WaitForAsync(RecordingWatchSink.WasNamed, Path.Combine(B, "sentinel"), TestContext.Current.CancellationToken);

        Assert.Equal(RecordingWatchSink.WasLost, Assert.Single(first.Heard).What);
        Assert.DoesNotContain(second.Heard, heard => heard.What == RecordingWatchSink.WasLost);
    }

    /// <summary>A read a signal interrupted is made again, and nothing is lost by it.</summary>
    [Fact]
    public async Task AReadASignalInterrupted_IsMadeAgain_AndLosesNothing()
    {
        using var calls = new ScriptedInotifyCalls();
        var sink = new RecordingWatchSink();
        using var span = Hub(calls).Watch([A], sink);

        calls.FailRead(InotifyErrors.Interrupted);
        calls.Give(new InotifyEvent(calls.DescriptorOf(A), InotifyBits.Create, "after.txt"));
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, Path.Combine(A, "after.txt"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(sink.Heard, heard => heard.What == RecordingWatchSink.WasLost);
        Assert.Empty(calls.InstancesClosed);
    }

    /// <summary>
    /// A read that fails otherwise loses every span watching through the instance, and the instance is closed: the next
    /// watch makes another, and is told of its own directories.
    /// </summary>
    [Fact]
    public async Task AReadThatFails_LosesEverySpan_AndTheNextWatchMakesAnotherInstance()
    {
        const int ioError = 5;
        using var calls = new ScriptedInotifyCalls();
        var hub = Hub(calls);
        var lost = new RecordingWatchSink();
        using var lostSpan = hub.Watch([A], lost);
        var first = hub.Instance;

        calls.FailRead(ioError);
        await WaitLostAsync(lost);
        await WaitUntilAsync(() => calls.InstancesClosed.Count == 1);

        Assert.Equal([first], calls.InstancesClosed);

        var sink = new RecordingWatchSink();
        using var span = hub.Watch([B], sink);

        Assert.Equal(2, calls.InitFlags.Count);
        Assert.NotEqual(first, hub.Instance);

        calls.Give(new InotifyEvent(calls.DescriptorOf(B), InotifyBits.Create, "made.txt"));
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, Path.Combine(B, "made.txt"), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Through the hub every directory that holds an input is watched for itself, however many there are, and none all
    /// the way down: a watch of 65 such directories adds 65.
    /// </summary>
    [Fact]
    public void AWatchThroughTheHub_AddsEveryDirectoryOfInputs_NoneAllTheWayDown()
    {
        using var temp = new TempDirectory();
        string[] inputs = [.. Enumerable.Range(0, 65).Select(index => $"src/d{index:00}/x.cpp")];

        foreach (var input in inputs)
        {
            temp.WriteFile(input, "x");
        }

        using var calls = new ScriptedInotifyCalls();
        var kind = new InotifyWatchKind(Hub(calls));

        using var watch = new InputWatch(temp.Path, inputs, StringComparer.Ordinal, kind);

        Assert.Null(kind.MostDirectories);
        Assert.Equal(65, calls.Added.Count);
        Assert.All(watch.Watched, watched => Assert.False(watched.Recursive));
    }

    /// <summary>Asked to watch a directory all the way down, which no watch with no bound asks, the kind refuses.</summary>
    [Fact]
    public void AskedToWatchADirectoryAllTheWayDown_TheKindRefuses()
    {
        using var calls = new ScriptedInotifyCalls();

        Assert.Throws<ArgumentException>(() => new InotifyWatchKind(Hub(calls)).Start([(A, false), (B, true)], new RecordingWatchSink()));
        Assert.Empty(calls.Added);
    }

    private static InotifyHub Hub(ScriptedInotifyCalls calls) => new(calls, () => InotifyUses.Of(Holding));

    private static Task WaitLostAsync(RecordingWatchSink sink)
        => WaitUntilAsync(() => sink.Heard.Any(heard => heard.What == RecordingWatchSink.WasLost));

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();

        while (!done())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "the hub never got there");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
