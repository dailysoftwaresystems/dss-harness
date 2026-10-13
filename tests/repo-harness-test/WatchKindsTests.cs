using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// Which kind of watch a span of work watches through: inotify's hub on Linux wherever its calls bind, and .NET's
/// watcher elsewhere - said once in a process where Linux has to make do with it, and never taken for a limit met.
/// </summary>
public sealed class WatchKindsTests
{
    /// <summary>On Windows and macOS, .NET's watcher watches, with its bound, and inotify is never asked after.</summary>
    [Theory]
    [InlineData(PlatformId.Windows)]
    [InlineData(PlatformId.MacOs)]
    public void OnWindowsAndMacOs_DotNetsWatcherWatches_WithItsBound(PlatformId platform)
    {
        var factory = new HarnessFactory();

        var kind = WatchKinds.For(platform, factory.Output, () => throw new InvalidOperationException("inotify asked after"), new SaidOnce());

        Assert.IsType<FileSystemWatcherKind>(kind);
        Assert.Equal(FileSystemWatcherKind.MostDirectoriesWatched, kind.MostDirectories);
    }

    /// <summary>On Linux, where inotify's calls bind, the hub watches, with no bound, and nothing is said.</summary>
    [Fact]
    public void OnLinux_WhereInotifyBinds_TheHubWatches_WithNoBound()
    {
        using var calls = new ScriptedInotifyCalls();
        var factory = new HarnessFactory();
        var hub = new InotifyHub(calls, () => InotifyUses.Of());

        var kind = WatchKinds.For(PlatformId.Linux, factory.Output, () => (hub, null), new SaidOnce());

        Assert.IsType<InotifyWatchKind>(kind);
        Assert.Null(kind.MostDirectories);
        Assert.Empty(factory.StandardError.ToString());
    }

    /// <summary>
    /// On Linux, where inotify cannot be called directly, .NET's watcher watches, and the process says so once - the
    /// first time anything is watched, whichever watch of it starts first, and never again - with why.
    /// </summary>
    [Fact]
    public void OnLinux_WhereInotifyCannotBeCalledDirectly_DotNetsWatcherWatches_SaidOnceAProcess()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var said = new SaidOnce();
        const string why = "no C library loads as libc.so.6, libc.so or libc";

        var first = WatchKinds.For(PlatformId.Linux, factory.Output, () => (null, why), said);
        var second = WatchKinds.For(PlatformId.Linux, factory.Output, () => (null, why), said);

        Assert.Equal(FileSystemWatcherKind.MostDirectoriesWatched, first.MostDirectories);
        Assert.Empty(factory.StandardError.ToString());

        foreach (var kind in new[] { first, second, first })
        {
            using var watching = Started(kind, temp.Path);
        }

        Assert.Equal(
            $"watch: WARN - inotify could not be called directly on this machine ({why}), so each watched directory takes "
            + "an inotify instance of its own, as .NET's watcher does." + Environment.NewLine,
            factory.StandardError.ToString());
    }

    /// <summary>
    /// A limit met is never a reason to fall back: the hub's refusal leaves as the watch's own, nothing is said of
    /// .NET's watcher, and the next watch asks the hub again.
    /// </summary>
    [Fact]
    public void ALimitMet_IsNeverAReasonToFallBack()
    {
        using var calls = new ScriptedInotifyCalls { InitError = InotifyErrors.InstancesTaken };
        var factory = new HarnessFactory();
        var hub = new InotifyHub(calls, () => InotifyUses.Of());
        var kind = WatchKinds.For(PlatformId.Linux, factory.Output, () => (hub, null), new SaidOnce());

        Assert.Throws<InputsUnwatchedException>(() => kind.Start([("/tree/a", false)], new RecordingWatchSink()));
        Assert.Throws<InputsUnwatchedException>(() => kind.Start([("/tree/a", false)], new RecordingWatchSink()));

        Assert.Equal(2, calls.InitFlags.Count);
        Assert.Empty(factory.StandardError.ToString());
    }

    /// <summary>The kind's watch of <paramref name="directory"/>, or the test skipped where this machine cannot watch.</summary>
    private static IDisposable Started(IWatchKind kind, string directory)
    {
        try
        {
            return kind.Start([(directory, false)], new RecordingWatchSink());
        }
        catch (InputsUnwatchedException ex)
        {
            Assert.Skip($"This machine cannot watch the tree: {ex.Why}");
            throw;
        }
    }
}
