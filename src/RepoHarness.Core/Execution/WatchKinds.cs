using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Execution;

/// <summary>Which kind of watch a span of work on this machine watches through: chosen in one place.</summary>
/// <remarks>
/// On Linux, the process's one inotify instance, wherever its calls can be bound; elsewhere, and on a Linux whose C
/// library they could not be found in, .NET's watcher - said once in a process, since there each watched directory
/// takes an instance of its own. A limit met is never a reason to fall back: it is said, and the work is not run.
/// </remarks>
internal static class WatchKinds
{
    /// <summary>The command a line about how this process watches is written under.</summary>
    public const string Command = "watch";

    /// <summary>The hub this process watches through on Linux, bound at most once; or why there is none.</summary>
    private static readonly Lazy<(InotifyHub? Hub, string? Why)> Linux = new(() =>
        LibcInotifyCalls.TryBind(out var why) is { } calls
            ? (new InotifyHub(calls, () => new InotifyHolders().Read()), null)
            : (null, why));

    /// <summary>Whether this process has said it watches through .NET's watcher on Linux.</summary>
    private static readonly SaidOnce FallbackSaid = new();

    /// <summary>The kind this process watches through on <paramref name="platform"/>.</summary>
    /// <param name="platform">This machine.</param>
    /// <param name="output">Where the fallback is said, the first time it watches.</param>
    public static IWatchKind For(IHostPlatform platform, IHarnessOutput output)
    {
        ArgumentNullException.ThrowIfNull(platform);

        return For(platform.Current, output, () => Linux.Value, FallbackSaid);
    }

    /// <summary>The kind for <paramref name="platform"/>, given what binding inotify's calls came to.</summary>
    /// <param name="platform">The system.</param>
    /// <param name="output">Where the fallback is said.</param>
    /// <param name="linux">The hub on Linux, or why there is none: asked only there.</param>
    /// <param name="said">Whether the fallback was said, for every kind that says it.</param>
    internal static IWatchKind For(PlatformId platform, IHarnessOutput output, Func<(InotifyHub? Hub, string? Why)> linux, SaidOnce said)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(linux);
        ArgumentNullException.ThrowIfNull(said);

        if (platform != PlatformId.Linux)
        {
            return new FileSystemWatcherKind();
        }

        var (hub, why) = linux();

        return hub is not null
            ? new InotifyWatchKind(hub)
            : new Saying(
                new FileSystemWatcherKind(),
                () => said.Once(() => output.Warn(
                    Command,
                    $"inotify could not be called directly on this machine ({why}), so each watched directory takes an "
                    + "inotify instance of its own, as .NET's watcher does.")));
    }

    /// <summary>A kind that says something before the first time it watches, and then watches as another does.</summary>
    private sealed class Saying(IWatchKind kind, Action say) : IWatchKind
    {
        public int? MostDirectories => kind.MostDirectories;

        public IDisposable Start(IReadOnlyList<(string Directory, bool Recursive)> targets, IWatchSink sink)
        {
            say();
            return kind.Start(targets, sink);
        }
    }
}

/// <summary>Whether something has been said, so that it is said once however many ask.</summary>
internal sealed class SaidOnce
{
    private int _said;

    /// <summary>Does <paramref name="say"/> the first time this is asked, and never again.</summary>
    public void Once(Action say)
    {
        ArgumentNullException.ThrowIfNull(say);

        if (Interlocked.Exchange(ref _said, 1) == 0)
        {
            say();
        }
    }
}
