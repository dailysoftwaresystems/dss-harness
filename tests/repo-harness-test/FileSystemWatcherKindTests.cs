using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// The kind of watch that goes through .NET's own watcher, one for each directory. What it must never do is leave a
/// watcher behind when it could not start them all - nobody is left to dispose of one - and what it tells must say
/// whether an entry was written to or named, a directory's name as well as a file's.
/// </summary>
public sealed class FileSystemWatcherKindTests
{
    private static readonly WatchTarget[] Three = [new("one"), new("two", Recursive: true), new("three", NamesOnly: true)];

    /// <summary>
    /// A start that fails on one directory has let go of every watcher made before it by the time the failure leaves -
    /// the first, a middle one and the last alike - and the failure is the system's own words, as work that was not run
    /// says them.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AStartThatFailsOnOneDirectory_HasLetGoOfEveryWatcherMadeBeforeIt_AsTheFailureLeaves(int failing)
    {
        var watchers = new List<Watcher>();
        var refusal = new IOException(ScriptedWatchKind.SystemsWords);
        var kind = new FileSystemWatcherKind((target, _) =>
        {
            if (watchers.Count + 1 == failing)
            {
                throw refusal;
            }

            watchers.Add(new Watcher(target));
            return watchers[^1];
        });

        var refused = Assert.Throws<InputsUnwatchedException>(() => kind.Start(Three, new RecordingWatchSink()));

        Assert.Equal(ScriptedWatchKind.Refused, refused.Message);
        Assert.Same(refusal, refused.InnerException);
        Assert.Equal(Three.Take(failing - 1), watchers.Select(watcher => watcher.Target));
        Assert.All(watchers, watcher => Assert.Equal(1, watcher.Disposed));
    }

    /// <summary>
    /// Each way the runtime's watcher refuses a directory is a watch that could not start - and a failure nobody named
    /// leaves as itself, the watchers before it let go of all the same.
    /// </summary>
    [Theory]
    [InlineData(typeof(ArgumentException), true)]
    [InlineData(typeof(IOException), true)]
    [InlineData(typeof(FileNotFoundException), true)]
    [InlineData(typeof(UnauthorizedAccessException), true)]
    [InlineData(typeof(PlatformNotSupportedException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    public void WhateverStopsAStart_TheWatchersBeforeItAreLetGo_AndARefusalOfTheSystemsIsSaidAsOne(Type failure, bool refusal)
    {
        var first = new Watcher(Three[0]);
        var raised = (Exception)Activator.CreateInstance(failure, "the system's own words")!;
        var kind = new FileSystemWatcherKind((target, _) => target.Directory == "one" ? first : throw raised);

        var left = Assert.ThrowsAny<Exception>(() => kind.Start(Three, new RecordingWatchSink()));

        Assert.Equal(1, first.Disposed);

        if (refusal)
        {
            Assert.Equal(InputsUnwatchedException.Opening + raised.Message, Assert.IsType<InputsUnwatchedException>(left).Message);
        }
        else
        {
            Assert.Same(raised, left);
        }
    }

    /// <summary>A watch that started holds one watcher for each directory, as it was asked, until it is disposed of - and then none.</summary>
    [Fact]
    public void AStartedWatch_HoldsAWatcherForEachDirectory_UntilItIsDisposedOf()
    {
        var watchers = new List<Watcher>();
        var kind = new FileSystemWatcherKind((target, _) =>
        {
            watchers.Add(new Watcher(target));
            return watchers[^1];
        });

        var watching = kind.Start(Three, new RecordingWatchSink());

        Assert.Equal(Three, watchers.Select(watcher => watcher.Target));
        Assert.All(watchers, watcher => Assert.Equal(0, watcher.Disposed));

        watching.Dispose();

        Assert.All(watchers, watcher => Assert.Equal(1, watcher.Disposed));
    }

    /// <summary>
    /// With the runtime's watcher: a directory that is not there is refused in the system's words, and the directory
    /// before it is no longer watched once the refusal has left - a file made in it is heard by a watch that did start,
    /// and never by the one that did not.
    /// </summary>
    [Fact]
    public async Task ADirectoryThatIsNotThere_IsRefused_AndTheOneBeforeItIsNoLongerWatched()
    {
        using var temp = new TempDirectory();
        var first = Directory.CreateDirectory(temp.Combine("first")).FullName;
        var kind = new FileSystemWatcherKind();
        var refusedSink = new RecordingWatchSink();
        var control = new RecordingWatchSink();

        using var watching = Started(kind, [new(first)], control);

        var refused = Assert.Throws<InputsUnwatchedException>(() => kind.Start([new(first), new(temp.Combine("gone"))], refusedSink));

        Assert.StartsWith(InputsUnwatchedException.Opening, refused.Message, StringComparison.Ordinal);
        Assert.Contains("gone", refused.Why, StringComparison.Ordinal);

        var made = Path.Combine(first, "made.txt");
        await File.WriteAllTextAsync(made, "made", TestContext.Current.CancellationToken);
        await control.WaitForAsync(RecordingWatchSink.WasNamed, made, TestContext.Current.CancellationToken);

        // Word of one write reaches every watcher on the directory together; a little longer, for one that came apart.
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        Assert.Empty(refusedSink.Heard);
    }

    /// <summary>
    /// With the runtime's watcher: a file made, written to, renamed and removed in a watched directory is each told as
    /// what it was - a write as a write, and each of the rest by name, a rename under both its names.
    /// </summary>
    [Fact]
    public async Task AFileMadeWrittenRenamedAndRemoved_IsEachToldAsWhatItWas_ARenameUnderBothItsNames()
    {
        using var temp = new TempDirectory();
        var sink = new RecordingWatchSink();
        using var watching = Started(new FileSystemWatcherKind(), [new(temp.Path)], sink);
        var made = temp.Combine("made.txt");
        var renamed = temp.Combine("renamed.txt");

        await File.WriteAllTextAsync(made, "made", TestContext.Current.CancellationToken);
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, made, TestContext.Current.CancellationToken);

        await File.AppendAllTextAsync(made, " and written again", TestContext.Current.CancellationToken);
        await sink.WaitForAsync(RecordingWatchSink.WasWritten, made, TestContext.Current.CancellationToken);

        Assert.DoesNotContain((RecordingWatchSink.WasNamed, renamed), sink.Heard);
        File.Move(made, renamed);
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, renamed, TestContext.Current.CancellationToken);

        // The old name was told before the move, as the file was made; told again for the move, it is there twice.
        await WaitForAsync(() => sink.Heard.Count(heard => heard == (RecordingWatchSink.WasNamed, made)) >= 2, "the file's old name told again as it was renamed");

        File.Delete(renamed);
        await WaitForAsync(() => sink.Heard.Count(heard => heard == (RecordingWatchSink.WasNamed, renamed)) >= 2, "the file told again as it was removed");

        Assert.DoesNotContain(sink.Heard, heard => heard.What == RecordingWatchSink.WasLost);
    }

    /// <summary>
    /// With the runtime's watcher: a directory made, renamed and removed in a watched directory is told by name as a file
    /// is, under both names of its rename - what the inputs below a directory that went are counted by.
    /// </summary>
    [Fact]
    public async Task ADirectoryMadeRenamedAndRemovedInAWatchedOne_IsToldByName_UnderBothNamesOfItsRename()
    {
        using var temp = new TempDirectory();
        var sink = new RecordingWatchSink();
        using var watching = Started(new FileSystemWatcherKind(), [new(temp.Path)], sink);
        var made = temp.Combine("made");
        var renamed = temp.Combine("renamed");

        Directory.CreateDirectory(made);
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, made, TestContext.Current.CancellationToken);

        Directory.Move(made, renamed);
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, renamed, TestContext.Current.CancellationToken);
        await WaitForAsync(() => sink.Heard.Count(heard => heard == (RecordingWatchSink.WasNamed, made)) >= 2, "the directory's old name told again as it was renamed");

        Directory.Delete(renamed);
        await WaitForAsync(() => sink.Heard.Count(heard => heard == (RecordingWatchSink.WasNamed, renamed)) >= 2, "the directory told again as it was removed");
    }

    /// <summary>
    /// An error a watcher raises while it starts - as .NET raises one, on the starting thread, where the system will not
    /// watch its directory - refuses the start in the system's own words, before any work runs under it: the watcher is
    /// let go, and the watch is told nothing, so it is never taken for one that started and was lost.
    /// </summary>
    [Fact]
    public void AnErrorAWatcherRaisesWhileItStarts_RefusesTheStart_AndTheWatcherIsLetGo()
    {
        using var temp = new TempDirectory();
        var watcher = new RaisingWatcher(temp.Path);
        var sink = new RecordingWatchSink();
        var raised = new IOException("The configured user limit (8192) on the number of inotify watches was reached.");

        var refused = Assert.Throws<InputsUnwatchedException>(
            () => FileSystemWatcherKind.Started(watcher, sink, started => ((RaisingWatcher)started).Fail(raised)));

        Assert.Equal(InputsUnwatchedException.Opening + raised.Message, refused.Message);
        Assert.Same(raised, refused.InnerException);
        Assert.Equal(1, watcher.Disposals);
        Assert.Empty(sink.Heard);
    }

    /// <summary>
    /// An error a watcher raises once it has started loses the span, in the error's own words, and the watcher stays
    /// for whoever started it to let go; and what it tells of entries is handed on as what each was - a write as a
    /// write, and an entry made, removed or renamed by name, a rename under both its names.
    /// </summary>
    [Fact]
    public void OnceAWatcherHasStarted_AnErrorLosesTheSpan_AndWhatItTellsIsHandedOnAsWhatItWas()
    {
        using var temp = new TempDirectory();
        using var watcher = new RaisingWatcher(temp.Path);
        var sink = new RecordingWatchSink();

        Assert.Same(watcher, FileSystemWatcherKind.Started(watcher, sink, _ => { }));

        watcher.Make("made.txt");
        watcher.Write("made.txt");
        watcher.Rename("made.txt", "renamed.txt");
        watcher.Remove("renamed.txt");
        watcher.Fail(new IOException("Too many changes at once in directory."));

        Assert.Equal(
            [
                (RecordingWatchSink.WasNamed, temp.Combine("made.txt")),
                (RecordingWatchSink.WasWritten, temp.Combine("made.txt")),
                (RecordingWatchSink.WasNamed, temp.Combine("made.txt")),
                (RecordingWatchSink.WasNamed, temp.Combine("renamed.txt")),
                (RecordingWatchSink.WasNamed, temp.Combine("renamed.txt")),
                (RecordingWatchSink.WasLost, "Too many changes at once in directory."),
            ],
            sink.Heard);
        Assert.Equal(0, watcher.Disposals);
    }

    /// <summary>
    /// With the runtime's watcher, a directory watched for names alone - one above inputs that holds none - tells of a
    /// directory made in it, and of nothing a file does there.
    /// </summary>
    [Fact]
    public async Task ADirectoryWatchedForNamesAlone_TellsOfADirectoryMadeInIt_AndNothingOfAFile()
    {
        using var temp = new TempDirectory();
        var sink = new RecordingWatchSink();
        using var watching = Started(new FileSystemWatcherKind(), [new(temp.Path, NamesOnly: true)], sink);

        await File.WriteAllTextAsync(temp.Combine("file.txt"), "a file", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(temp.Combine("made"));
        await sink.WaitForAsync(RecordingWatchSink.WasNamed, temp.Combine("made"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(sink.Heard, heard => heard.Path == temp.Combine("file.txt"));
    }

    /// <summary>The watch a test of the runtime's watcher runs under, or the test skipped where this machine cannot watch.</summary>
    private static IDisposable Started(FileSystemWatcherKind kind, IReadOnlyList<WatchTarget> targets, IWatchSink sink)
    {
        try
        {
            return kind.Start(targets, sink);
        }
        catch (InputsUnwatchedException ex)
        {
            Assert.Skip($"This machine cannot watch the tree: {ex.Why}");
            throw;
        }
    }

    private static async Task WaitForAsync(Func<bool> heard, string what)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (heard())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The watch was never told of {what}.");
    }

    /// <summary>What stands for one of the runtime's watchers: it says what it was made for, and how often it was let go.</summary>
    private sealed class Watcher(WatchTarget target) : IDisposable
    {
        public WatchTarget Target { get; } = target;

        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }

    /// <summary>
    /// One of the runtime's own watchers, never enabled, that raises what a test says when it says so - as the runtime
    /// raises it, through the watcher's own events - and counts how often it was let go.
    /// </summary>
    private sealed class RaisingWatcher(string directory) : FileSystemWatcher(directory)
    {
        public int Disposals { get; private set; }

        public void Fail(Exception error) => OnError(new ErrorEventArgs(error));

        public void Make(string name) => OnCreated(new FileSystemEventArgs(WatcherChangeTypes.Created, Path, name));

        public void Remove(string name) => OnDeleted(new FileSystemEventArgs(WatcherChangeTypes.Deleted, Path, name));

        public void Write(string name) => OnChanged(new FileSystemEventArgs(WatcherChangeTypes.Changed, Path, name));

        public void Rename(string from, string to) => OnRenamed(new RenamedEventArgs(WatcherChangeTypes.Renamed, Path, to, from));

        protected override void Dispose(bool disposing)
        {
            Disposals++;
            base.Dispose(disposing);
        }
    }
}
