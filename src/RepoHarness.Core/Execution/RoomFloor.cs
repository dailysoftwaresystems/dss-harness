namespace RepoHarness.Core.Execution;

/// <summary>
/// The least room a heavy leg's build leaves free on each filesystem it fills, below which it is stopped rather than let
/// fill a disk other legs build and test on: its machine's <c>admission.minFreeGiB</c>.
/// </summary>
/// <param name="Leg">
/// The leg whose builds it holds, as <c>clean --legs</c> names it: the leg's own name, whichever of its builds is stopped -
/// its own, or a sweep's worker's, which its clean removes with it.
/// </param>
/// <param name="Bytes">The least room left free, more than nothing: a floor of nothing stops no build, and is none.</param>
/// <param name="Also">
/// Each filesystem the build fills beside its build directory's, as this machine reaches it, with how a line names it
/// beyond its path: the drive of the machine that sent a WSL distribution's leg, where WSL keeps the distribution's disk.
/// </param>
/// <param name="Unwatched">
/// Why a filesystem the build fills is not watched, said as the build starts; <see langword="null"/> where every one is.
/// </param>
public sealed record RoomFloor(string Leg, long Bytes, IReadOnlyList<(string Path, string Where)> Also, string? Unwatched = null);
