using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Mutations;

/// <summary>
/// The files of a worker's copy an arm mutates: read, written dated forward of everything the worker's last build left,
/// and put back as they were - never through a link below the worker's root, a place the worker's builds and runs write
/// (<see cref="UnfollowedFiles"/>).
/// </summary>
internal interface IWorkerSite
{
    /// <summary>
    /// The bytes of <paramref name="path"/>, or <see langword="null"/> where nothing is there that a link leads from.
    /// </summary>
    /// <param name="worker">The worker's copy, which <paramref name="path"/> is below.</param>
    /// <param name="path">The file.</param>
    /// <exception cref="IOException">A link stands on the way to it or at it, or it could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read it.</exception>
    byte[]? Read(string worker, string path);

    /// <summary>
    /// When a file of the worker written now must be dated, so the build that follows sees it changed: past the newest
    /// file the worker's last build left in <paramref name="buildDirectory"/>, and past now - or past now alone, where
    /// that file is dated further ahead of the clock than a build waits for it: the clock went back from it, and the
    /// build's own rule then starts from clean.
    /// </summary>
    /// <param name="buildDirectory">The worker's build directory.</param>
    DateTime Stamp(string buildDirectory);

    /// <summary>Writes <paramref name="contents"/> to <paramref name="path"/>, dated <paramref name="stamp"/>.</summary>
    /// <param name="worker">The worker's copy, which <paramref name="path"/> is below.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">What it holds after.</param>
    /// <param name="stamp">When it is dated, as <see cref="Stamp"/> gave it.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <exception cref="IOException">A link stands on the way to it, or it could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write it.</exception>
    Task WriteAsync(string worker, string path, byte[] contents, DateTime stamp, CancellationToken cancellationToken);

    /// <summary>
    /// Waits until the clock is past <paramref name="stamp"/>, so nothing a build writes after it is dated before the file
    /// dated there.
    /// </summary>
    /// <param name="stamp">When the file was dated.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    Task UntilPastAsync(DateTime stamp, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWorkerSite"/>
/// <remarks>
/// <para>
/// A site is dated two seconds past the later of now and the newest file the worker's last build recorded leaving, and
/// the build waits until the clock is past that: two seconds, since a filesystem that dates coarsely - FAT to two seconds -
/// dates a file written just after another as written with it. Dated by the clock alone, a site written on a machine whose
/// clock stepped back since its last build would be older than the objects built from it, and the build would not see it
/// changed, which is a mutation measured on a binary that never held it.
/// </para>
/// <para>
/// A newest file dated more than <see cref="LongestWait"/> ahead of the clock is one the clock went back from: waiting it
/// out could take hours, so the site is dated by the clock, and the build's own rule answers what that leaves - an input
/// changed and dated no later than what the last build left starts the build from clean, saying so.
/// </para>
/// </remarks>
internal sealed class WorkerSite(IFileSystem fileSystem, TimeProvider clock) : IWorkerSite
{
    /// <summary>How far past the later of the clock and the last build's newest file a site is dated.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);

    /// <summary>The longest a build waits for the clock to pass the newest file the worker's last build left.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(1);

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc/>
    public byte[]? Read(string worker, string path)
    {
        // A link is no file of the worker's, wherever it leads: refused naming it, never read as one, nor taken for none.
        if (!_fileSystem.FileExists(path) && UnfollowedFiles.LinkOnTheWay(_fileSystem, worker, path, itself: true) is null)
        {
            return null;
        }

        using var read = UnfollowedFiles.OpenRead(_fileSystem, worker, path);
        using var bytes = new MemoryStream();

        read.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <inheritdoc/>
    public DateTime Stamp(string buildDirectory)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var newest = BuildRecord.NewestIn(_fileSystem, buildDirectory);
        var from = newest is { } dated && dated > now && dated - now <= LongestWait ? dated : now;

        return from + Margin;
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string worker, string path, byte[] contents, DateTime stamp, CancellationToken cancellationToken)
    {
        await UnfollowedFiles.WriteAsync(_fileSystem, worker, path, contents, cancellationToken).ConfigureAwait(false);
        _fileSystem.SetLastWriteTimeUtc(path, stamp);
    }

    /// <inheritdoc/>
    public async Task UntilPastAsync(DateTime stamp, CancellationToken cancellationToken)
    {
        var started = _clock.GetTimestamp();

        // Read again after each wait: the clock is past the stamp when it says so, never when the wait it was given ran
        // out. Never longer than a stamp is ever ahead of the clock: one that stepped back while it waited is the build's
        // to answer, whose phases say they spanned a step, and whose next build starts from clean for it.
        for (var left = stamp - _clock.GetUtcNow().UtcDateTime;
             left >= TimeSpan.Zero && _clock.GetElapsedTime(started) <= LongestWait + Margin;
             left = stamp - _clock.GetUtcNow().UtcDateTime)
        {
            await Task.Delay(Waits.Shorter(LongestWait, left) + TimeSpan.FromMilliseconds(10), _clock, cancellationToken).ConfigureAwait(false);
        }
    }
}
