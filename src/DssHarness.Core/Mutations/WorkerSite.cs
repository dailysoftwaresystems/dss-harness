using System.Globalization;
using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Waiting;

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
    /// file the worker's last build left in <paramref name="buildDirectory"/>, and past now, by the granularity the file
    /// system there dates files at - so a file written just after another is never dated with it.
    /// </summary>
    /// <param name="buildDirectory">The worker's build directory.</param>
    /// <exception cref="IOException">The granularity could not be measured: no time could be set on a file there.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write there.</exception>
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
    /// dated there: for as long as that takes - a clock behind the files a build left, as a WSL clock minutes ahead of
    /// this machine's leaves them, is waited out - told as it begins and every <see cref="Waits.SaidEvery"/> while it lasts.
    /// </summary>
    /// <param name="stamp">When the file was dated.</param>
    /// <param name="told">Told where the wait stands.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    Task UntilPastAsync(DateTime stamp, Action<WaitTold> told, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWorkerSite"/>
/// <remarks>
/// <para>
/// A site is dated past the later of now and the newest file the worker's last build recorded leaving, by the granularity
/// the file system dates files at - measured there, once for each build directory, by setting a file's time and reading
/// back what was kept: a tick on NTFS, a second or two where a file system dates coarsely, as FAT does - and the build
/// waits until the clock is past that. Dated by the clock alone, a site written on a machine whose clock stepped back
/// since its last build would be older than the objects built from it, and the build would not see it changed, which is a
/// mutation measured on a binary that never held it.
/// </para>
/// <para>
/// A newest file dated ahead of the clock - one a clock that stepped back left, or a WSL distribution's clock ahead of
/// this machine's - is waited out however far ahead it is, told: the clock is past the stamp when it says so, never when
/// a number of the code's own says it has waited long enough.
/// </para>
/// </remarks>
internal sealed class WorkerSite(IFileSystem fileSystem, TimeProvider clock) : IWorkerSite
{
    /// <summary>
    /// A moment the granularity is measured at, a whole even second - which every file system that dates files keeps -
    /// long past and long before any limit a file system's dates have.
    /// </summary>
    private static readonly DateTime Measured = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The file the granularity is measured on, in a build directory, made and removed at once.</summary>
    private const string ProbeName = ".dssharness-date-probe";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly TimeProvider _clock = clock;

    /// <summary>The granularity measured in each build directory, by its path: measured once there.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeSpan> _granularities = new(StringComparer.Ordinal);

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
        var from = newest is { } dated && dated > now ? dated : now;

        return from + Granularity(buildDirectory);
    }

    /// <summary>
    /// The granularity the file system holding <paramref name="buildDirectory"/> dates files at: a file's time set to just
    /// past a whole second, and to just short of the next but one, each read back - the most either moved, and a tick more,
    /// is a step no two times a file is given there fall within. Measured once for each directory, on a file made there and
    /// removed at once.
    /// </summary>
    internal TimeSpan Granularity(string buildDirectory)
        => _granularities.GetOrAdd(buildDirectory, directory =>
        {
            var probe = Path.Combine(directory, ProbeName);

            _fileSystem.CreateDirectory(directory);
            _fileSystem.WriteAllTextAtomic(probe, string.Empty);

            try
            {
                var moved = TimeSpan.Zero;

                foreach (var asked in new[] { Measured.AddTicks(1), Measured.AddSeconds(2).AddTicks(-1) })
                {
                    _fileSystem.SetLastWriteTimeUtc(probe, asked);

                    var kept = _fileSystem.LastWriteTimeUtc(probe);
                    var by = (kept - asked).Duration();

                    moved = by > moved ? by : moved;
                }

                return moved + TimeSpan.FromTicks(1);
            }
            finally
            {
                _fileSystem.DeleteFile(probe);
            }
        });

    /// <inheritdoc/>
    public async Task WriteAsync(string worker, string path, byte[] contents, DateTime stamp, CancellationToken cancellationToken)
    {
        await UnfollowedFiles.WriteAsync(_fileSystem, worker, path, contents, cancellationToken).ConfigureAwait(false);
        _fileSystem.SetLastWriteTimeUtc(path, stamp);
    }

    /// <inheritdoc/>
    public async Task UntilPastAsync(DateTime stamp, Action<WaitTold> told, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(told);

        var subject = $"the clock to pass {stamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}Z, which its site was dated by";
        var left = stamp - _clock.GetUtcNow().UtcDateTime;

        // Said as it begins where it is a wait a person sees - as long as a wait on a holder takes to look again, or
        // longer - which is a clock behind the files a build left: one the file system's own granularity alone sets is
        // over before it could be read.
        if (left >= Waits.LooksAgainEvery)
        {
            told(new WaitTold(subject, TimeSpan.Zero, null, $"this machine's clock is {LedgerReport.FormatDuration(left)} behind it"));
        }

        var ended = await Waits.UntilAsync(new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc)), new WaitWatch(subject, told, Bound: null, _clock), cancellationToken)
            .ConfigureAwait(false);

        if (ended.Kind != WaitEndKind.Done)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
