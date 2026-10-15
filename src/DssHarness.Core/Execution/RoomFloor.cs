namespace RepoHarness.Core.Execution;

/// <summary>
/// The least room a heavy leg's build leaves free on each filesystem it fills, below which it is stopped rather than let
/// fill a disk other legs build and test on: its machine's <c>admission.minFreeGiB</c>. One of three: on the build
/// directory's filesystem alone (<see cref="Of"/>), on one more the build fills beside it (<see cref="Beside"/>), or on
/// the build directory's alone, where another it fills cannot be watched (<see cref="Unwatching"/>).
/// </summary>
public sealed record RoomFloor
{
    private RoomFloor(string leg, long bytes, IReadOnlyList<(string Path, string Where)> also, string? unwatched)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        Leg = leg;
        Bytes = bytes;
        Also = also;
        Unwatched = unwatched;
    }

    /// <summary>
    /// The leg whose builds it holds, as <c>clean --legs</c> names it: the leg's own name, whichever of its builds is stopped -
    /// its own, or a sweep's worker's, which its clean removes with it.
    /// </summary>
    public string Leg { get; }

    /// <summary>The least room left free, more than nothing: a floor of nothing stops no build, and is none.</summary>
    public long Bytes { get; }

    /// <summary>
    /// Each filesystem the build fills beside its build directory's, as this machine reaches it, with how a line names it
    /// beyond its path: the drive of the machine that sent a WSL distribution's leg, where WSL keeps the distribution's disk.
    /// </summary>
    public IReadOnlyList<(string Path, string Where)> Also { get; }

    /// <summary>
    /// Why a filesystem the build fills is not watched, said as the build starts; <see langword="null"/> where every one is.
    /// </summary>
    public string? Unwatched { get; }

    /// <summary>A floor on the filesystem of the build directory alone, which is all the build fills.</summary>
    /// <param name="leg">The leg whose builds it holds.</param>
    /// <param name="bytes">The least room left free.</param>
    public static RoomFloor Of(string leg, long bytes) => new(leg, bytes, [], null);

    /// <summary>A floor on the build directory's filesystem and on <paramref name="path"/>'s, which the build fills too.</summary>
    /// <param name="leg">The leg whose builds it holds.</param>
    /// <param name="bytes">The least room left free.</param>
    /// <param name="path">A path on the other filesystem, as this machine reaches it.</param>
    /// <param name="where">How a line names that filesystem beyond its path.</param>
    public static RoomFloor Beside(string leg, long bytes, string path, string where)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(where);

        return new(leg, bytes, [(path, where)], null);
    }

    /// <summary>A floor on the build directory's filesystem alone, where the build fills another that cannot be watched.</summary>
    /// <param name="leg">The leg whose builds it holds.</param>
    /// <param name="bytes">The least room left free.</param>
    /// <param name="why">Why the other is not watched, said as the build starts.</param>
    public static RoomFloor Unwatching(string leg, long bytes, string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return new(leg, bytes, [], why);
    }
}
