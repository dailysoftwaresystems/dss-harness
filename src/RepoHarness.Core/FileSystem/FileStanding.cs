namespace RepoHarness.Core.FileSystem;

/// <summary>
/// How a file stood when it was looked at: how many bytes it held, and when it was last written and created - what
/// tells a file still standing as it was read from one written since, without reading its bytes again.
/// </summary>
/// <param name="Length">How many bytes it held.</param>
/// <param name="Written">When it was last written; <see langword="null"/> where that could not be read.</param>
/// <param name="Created">When it was created; <see langword="null"/> where that could not be read.</param>
/// <remarks>
/// The times are compared for equality alone, never ordered: one host this tool serves has a clock that steps
/// forward by about 25 seconds every few seconds, and the steps reach file times. What equality cannot see is a
/// rewrite of the same length that puts the old times back, which only reading the bytes again would.
/// </remarks>
public sealed record FileStanding(long Length, DateTime? Written, DateTime? Created)
{
    /// <summary>
    /// Whether a file standing as <paramref name="now"/> stands as this one did: there, the same length, and written
    /// and created when it was. A standing either of whose times could not be read confirms nothing, and a file not
    /// there stands as nothing it was.
    /// </summary>
    /// <param name="now">How the file stands now, or <see langword="null"/> where it is not there.</param>
    public bool StandsAs(FileStanding? now)
        => now is not null && Written is not null && Created is not null && this == now;

    /// <summary>
    /// How the file at <paramref name="path"/> stands now, read in one look; <see langword="null"/> where no file is
    /// there - nothing, or a directory.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <exception cref="IOException">It could not be looked up.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not look it up.</exception>
    /// <remarks>
    /// One look for all three, where asking each of them alone asks the file system three times: a reading of a tree
    /// looks at every file of it once more after hashing them all.
    /// </remarks>
    public static FileStanding? Of(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);

        // Refreshed, so it answers for now; a path with nothing at it, or a directory, reads as no file there, and its
        // times as the year 1601, which reads as a real time.
        info.Refresh();

        return info.Exists ? new FileStanding(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc) : null;
    }
}
