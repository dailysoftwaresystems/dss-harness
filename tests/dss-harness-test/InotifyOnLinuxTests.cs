using System.Diagnostics;
using System.Globalization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// The hub over this machine's own inotify, on Linux alone: one instance however many spans, what it hears and what
/// it does not, a directory's own going, and no program a leg starts holding the instance. Few of the machine's watches
/// are taken, for moments, and no limit is ever met here: the limits are the scripted tests' (<see cref="InotifyHubTests"/>).
/// Run alone, so no other test's watcher is counted among this process's instances.
/// </summary>
[Collection(InotifyInstancesCounted.Name)]
public sealed class InotifyOnLinuxTests
{
    /// <summary>
    /// Three spans over 200 directories hold one instance between them - the hub's, holding one watch for each
    /// directory - and so does a fourth opened after the three let go, which the instance's watches were removed for.
    /// </summary>
    [Fact]
    public void ThreeSpansOver200Directories_HoldOneInstance_AndAFourthOpenedAfterThemHoldsTheSame()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify is Linux's.");

        using var temp = new TempDirectory();
        var directories = Enumerable.Range(0, 200).Select(index => Directory.CreateDirectory(temp.Combine($"d{index:000}")).FullName).ToList();
        var hub = RealHub();
        var before = Instances();

        var targets = directories.Select(directory => WatchTarget.Entries(directory)).ToList();
        var spans = Enumerable.Range(0, 3).Select(_ => hub.Watch(targets, new RecordingWatchSink())).ToList();
        var instance = hub.Instance;
        var held = Instances();

        // The hub's instance, made now or by a test before: nothing else of this process's is new.
        Assert.Contains(instance, held);
        Assert.All(held.Except(before), made => Assert.Equal(instance, made));
        Assert.Equal(200, Watches(instance));

        spans.ForEach(span => span.Dispose());

        Assert.Equal(0, Watches(instance));

        using var fourth = hub.Watch(targets[..10], new RecordingWatchSink());

        Assert.Equal(instance, hub.Instance);
        Assert.All(Instances().Except(before), made => Assert.Equal(instance, made));
        Assert.Equal(10, Watches(instance));
    }

    /// <summary>
    /// Every kind this process's watches are given on Linux watches through the one hub: watches from two of them hold
    /// one instance between them.
    /// </summary>
    [Fact]
    public void EveryWatchOfAProcess_WatchesThroughOneInstance()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify is Linux's.");

        using var temp = new TempDirectory();
        temp.WriteFile("a/x.cpp", "x");
        temp.WriteFile("b/y.cpp", "y");
        var factory = new HarnessFactory();
        var before = Instances();

        using var first = new InputWatch(temp.Path, ["a/x.cpp"], StringComparer.Ordinal, WatchKinds.For(factory.Platform, factory.Output));
        using var second = new InputWatch(temp.Path, ["b/y.cpp"], StringComparer.Ordinal, WatchKinds.For(factory.Platform, factory.Output));

        Assert.True(Instances().Except(before).Count() <= 1, "the watches of one process took more than one instance between them");
        Assert.Empty(factory.StandardError.ToString());
    }

    /// <summary>
    /// A file made, written, removed and renamed in a watched directory is each heard, as what it was; a write in an
    /// unwatched directory beside it is not.
    /// </summary>
    [Fact]
    public async Task AFileMadeWrittenRemovedAndRenamed_IsEachHeard_AndAWriteBesideTheWatchIsNot()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify is Linux's.");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var watched = Directory.CreateDirectory(temp.Combine("watched")).FullName;
        var beside = Directory.CreateDirectory(temp.Combine("beside")).FullName;
        var sink = new RecordingWatchSink();
        using var span = RealHub().Watch([WatchTarget.Entries(watched)], sink);
        var made = Path.Combine(watched, "made.txt");
        var renamed = Path.Combine(watched, "renamed.txt");

        await File.WriteAllTextAsync(Path.Combine(beside, "unheard.txt"), "beside", cancellationToken);

        await File.WriteAllTextAsync(made, "made", cancellationToken);
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, made, cancellationToken);

        await File.AppendAllTextAsync(made, " and written", cancellationToken);
        await sink.WaitForAsync(RecordingWatchSink.WasWritten, made, cancellationToken);

        File.Move(made, renamed);
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, renamed, cancellationToken);
        Assert.Equal(2, sink.Heard.Count(heard => heard == (RecordingWatchSink.WasNamed, made)));

        File.Delete(renamed);
        await WaitUntilAsync(() => sink.Heard.Count(heard => heard == (RecordingWatchSink.WasNamed, renamed)) == 2);

        Assert.DoesNotContain(sink.Heard, heard => heard.Path.StartsWith(beside, StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Heard, heard => heard.What == RecordingWatchSink.WasLost);
    }

    /// <summary>
    /// A watched directory removed, one moved aside and put back, and one above inputs that holds none moved aside and
    /// put back, each count every input in or below them - though no input lies above any of them.
    /// </summary>
    [Fact]
    public async Task AWatchedDirectoryRemoved_AndOneMovedAsideAndPutBack_EachCountTheirInputs()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify is Linux's.");

        using var temp = new TempDirectory();
        string[] inputs = ["kept/a/x.cpp", "kept/a/y.cpp", "gone/b/z.cpp", "still/c/w.cpp", "above/d/v.cpp"];

        foreach (var input in inputs)
        {
            temp.WriteFile(input, input);
        }

        var factory = new HarnessFactory();
        var fingerprint = new InputFingerprint(factory.FileSystem, factory.Platform, factory.Output);
        var before = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);
        using var watch = fingerprint.Watch(temp.Path, inputs, before);

        Directory.Move(temp.Combine("kept", "a"), temp.Combine("kept", "a.aside"));
        Directory.Move(temp.Combine("kept", "a.aside"), temp.Combine("kept", "a"));
        Directory.Delete(temp.Combine("gone", "b"), recursive: true);
        Directory.Move(temp.Combine("above"), temp.Combine("above.aside"));
        Directory.Move(temp.Combine("above.aside"), temp.Combine("above"));

        await WaitUntilAsync(() => watch.Changed.Count == 4);

        Assert.Equal(["above/d/v.cpp", "gone/b/z.cpp", "kept/a/x.cpp", "kept/a/y.cpp"], watch.Changed);
        Assert.Null(watch.Failure);
    }

    /// <summary>A program started while a span is open holds no inotify instance: the hub's is close-on-exec.</summary>
    [Fact]
    public async Task AProgramStartedWhileASpanIsOpen_HoldsNoInotifyInstance()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify is Linux's.");

        using var temp = new TempDirectory();
        var hub = RealHub();
        using var span = hub.Watch([WatchTarget.Entries(temp.Path)], new RecordingWatchSink());

        Assert.Contains(hub.Instance, Instances());

        // The shell lists what it was started holding, each descriptor's link read by a program it starts in turn.
        using var shell = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "for f in /proc/$$/fd/*; do readlink \"$f\"; done"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;

        var held = await shell.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await shell.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(held);
        Assert.DoesNotContain(InotifyHolders.InstanceLink, held, StringComparison.Ordinal);
    }

    /// <summary>The C library's inotify calls bind on this machine's Linux, which these tests run on with glibc.</summary>
    [Fact]
    public void TheRealCalls_Bind()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify is Linux's.");

        Assert.NotNull(LibcInotifyCalls.TryBind(out var why));
        Assert.Null(why);
    }

    /// <summary>
    /// A hub of these tests' own over the machine's calls, so what it holds is told from what the process's own holds:
    /// one more instance for the process's life, and no more, however many of these tests run.
    /// </summary>
    private static readonly Lazy<InotifyHub> Real = new(() => new(LibcInotifyCalls.TryBind(out _)!, () => new InotifyHolders().Read()));

    private static InotifyHub RealHub() => Real.Value;

    /// <summary>This process's descriptors that are inotify instances.</summary>
    private static List<int> Instances()
        => [.. Directory.EnumerateFileSystemEntries("/proc/self/fd")
            .Where(descriptor => new FileInfo(descriptor).LinkTarget == InotifyHolders.InstanceLink)
            .Select(descriptor => int.Parse(Path.GetFileName(descriptor), CultureInfo.InvariantCulture))];

    /// <summary>How many watches the instance at <paramref name="instance"/> holds.</summary>
    private static int Watches(int instance)
        => File.ReadAllLines($"/proc/self/fdinfo/{instance}").Count(line => line.StartsWith(InotifyHolders.WatchLine, StringComparison.Ordinal));

    private static async Task WaitUntilAsync(Func<bool> done)
    {
        var deadline = Stopwatch.StartNew();

        while (!done())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), "inotify never told it");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>The tests that count this process's inotify instances, run alone so that no other test's watcher is counted.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InotifyInstancesCounted
{
    /// <summary>The collection's name.</summary>
    public const string Name = "inotify instances counted";
}
