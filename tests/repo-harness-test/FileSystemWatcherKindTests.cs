using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// The kind of watch that goes through .NET's own watcher, one for each directory. What it must never do is leave a
/// watcher behind when it could not start them all - nobody is left to dispose of one - and what it tells must say
/// whether an entry was written to or named, a directory's name as well as a file's.
/// </summary>
public sealed class FileSystemWatcherKindTests
{
    private static readonly (string Directory, bool Recursive)[] Three = [("one", false), ("two", true), ("three", false)];

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
        var kind = new FileSystemWatcherKind((directory, recursive, _) =>
        {
            if (watchers.Count + 1 == failing)
            {
                throw refusal;
            }

            watchers.Add(new Watcher(directory, recursive));
            return watchers[^1];
        });

        var refused = Assert.Throws<InputsUnwatchedException>(() => kind.Start(Three, new RecordingWatchSink()));

        Assert.Equal(ScriptedWatchKind.Refused, refused.Message);
        Assert.Same(refusal, refused.InnerException);
        Assert.Equal(Three.Take(failing - 1), watchers.Select(watcher => (watcher.Directory, watcher.Recursive)));
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
        var first = new Watcher("one", false);
        var raised = (Exception)Activator.CreateInstance(failure, "the system's own words")!;
        var kind = new FileSystemWatcherKind((directory, _, _) => directory == "one" ? first : throw raised);

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
        var kind = new FileSystemWatcherKind((directory, recursive, _) =>
        {
            watchers.Add(new Watcher(directory, recursive));
            return watchers[^1];
        });

        var watching = kind.Start(Three, new RecordingWatchSink());

        Assert.Equal(Three, watchers.Select(watcher => (watcher.Directory, watcher.Recursive)));
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

        using var watching = Started(kind, [(first, false)], control);

        var refused = Assert.Throws<InputsUnwatchedException>(() => kind.Start([(first, false), (temp.Combine("gone"), false)], refusedSink));

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
        using var watching = Started(new FileSystemWatcherKind(), [(temp.Path, false)], sink);
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
        using var watching = Started(new FileSystemWatcherKind(), [(temp.Path, false)], sink);
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

    /// <summary>The watch a test of the runtime's watcher runs under, or the test skipped where this machine cannot watch.</summary>
    private static IDisposable Started(FileSystemWatcherKind kind, IReadOnlyList<(string Directory, bool Recursive)> targets, IWatchSink sink)
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
    private sealed class Watcher(string directory, bool recursive) : IDisposable
    {
        public string Directory { get; } = directory;

        public bool Recursive { get; } = recursive;

        public int Disposed { get; private set; }

        public void Dispose() => Disposed++;
    }
}
