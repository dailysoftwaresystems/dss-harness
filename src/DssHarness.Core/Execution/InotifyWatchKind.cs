namespace DssHarness.Core.Execution;

/// <summary>
/// Watches through this process's one inotify instance, on Linux: every directory that holds an input is watched for
/// its own entries, however many there are, since a watch through the hub is one descriptor and no thread of its own.
/// </summary>
/// <param name="hub">The process's instance.</param>
internal sealed class InotifyWatchKind(InotifyHub hub) : IWatchKind
{
    /// <inheritdoc/>
    /// <remarks>None: a directory more is one watch more, and this user's watches running out is said as such.</remarks>
    public int? MostDirectories => null;

    /// <inheritdoc/>
    public IDisposable Start(IReadOnlyList<WatchTarget> targets, IWatchSink sink)
    {
        ArgumentNullException.ThrowIfNull(targets);

        // A watch with no bound names no directory all the way down; one that did would hear nothing below it here.
        if (targets.FirstOrDefault(target => target.Depth == WatchDepth.AllTheWayDown) is { Directory: { } recursive })
        {
            throw new ArgumentException($"'{recursive}' is to be watched all the way down, which a watch through inotify's hub never is.", nameof(targets));
        }

        return hub.Watch(targets, sink);
    }
}
