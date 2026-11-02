namespace DssHarness.Core.FileSystem;

/// <summary>
/// The one way the tool reads, writes, copies or removes a file of its own inside a place an isolated process writes - a
/// leg's tree or its copy, the outputs a runner's steps leave in it, the build directory a test leaves evidence in: never
/// through a link. Every entry on the way below the place's root is a directory, never a link or a junction, and an open
/// file is held to the place its path names, as the system names the open file (<see cref="IFileSystem.FinalPathOf"/>), so
/// a link put on the way in the instant after the look reaches nothing either. A link such a process plants there would
/// otherwise have the tool, outside every isolation, write what the process chose wherever the link leads - over a file
/// of the user's - or carry what a hidden place holds to where the agents read it.
/// </summary>
/// <remarks>
/// The root itself is the one place taken as it is: no process inside an isolation moves a root it writes, and whatever
/// links lead to it were there before. A rename that puts a written file in place, and a removal, still go by the path,
/// the one step a link put on the way in the instant after the look reaches; the rename replaces a link at the file's own
/// place, and a removal removes such a link itself, and neither goes through one.
/// </remarks>
public static class UnfollowedFiles
{
    /// <summary>
    /// The first entry on the way to <paramref name="path"/> from <paramref name="root"/> down that is a link - a
    /// symbolic link or a junction alike - <paramref name="path"/> itself among them where <paramref name="itself"/>; null
    /// where none is.
    /// </summary>
    /// <param name="fileSystem">Reads what each entry is.</param>
    /// <param name="root">The place's root, which is never asked about itself.</param>
    /// <param name="path">A path below it.</param>
    /// <param name="itself">Whether <paramref name="path"/> itself is asked about.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not below <paramref name="root"/>.</exception>
    public static string? LinkOnTheWay(IFileSystem fileSystem, string root, string path, bool itself)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var parts = Parts(root, path);
        var place = root;

        for (var at = 0; at < parts.Count - (itself ? 0 : 1); at++)
        {
            place = Path.Combine(place, parts[at]);

            switch (fileSystem.KindOf(place))
            {
                case PathKind.Link:
                    return place;

                case PathKind.None:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens the file at <paramref name="path"/>, below <paramref name="root"/>, for reading - refused where a link stands
    /// on its way or at it, or where the file opened stands anywhere but at its path.
    /// </summary>
    /// <param name="fileSystem">Opens and names the file.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="path">The file.</param>
    /// <returns>The open file, which the caller disposes.</returns>
    /// <exception cref="IOException">A link stands on the way, or the file could not be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read it.</exception>
    public static Stream OpenRead(IFileSystem fileSystem, string root, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        Refuse(LinkOnTheWay(fileSystem, root, path, itself: true), path);

        var stream = fileSystem.OpenRead(path);

        try
        {
            Held(fileSystem, root, path, fileSystem.FinalPathOf(stream));
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Writes the file at <paramref name="path"/>, below <paramref name="root"/>, whole and atomically
    /// (<see cref="IFileSystem.WriteAtomic"/>) - refused, nothing written, where a link stands on the way to it, or where
    /// the file begun stands anywhere but in its directory; a link at its own place is replaced, never written through.
    /// </summary>
    /// <param name="fileSystem">Writes and names the file.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="path">The file.</param>
    /// <param name="write">Writes what it is to hold.</param>
    /// <exception cref="IOException">A link stands on the way, or the file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write it.</exception>
    public static void Write(IFileSystem fileSystem, string root, string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(write);

        Refuse(LinkOnTheWay(fileSystem, root, path, itself: false), path);

        IOException? refused = null;

        try
        {
            fileSystem.WriteAtomic(path, stream =>
            {
                var begun = fileSystem.FinalPathOf(stream);

                try
                {
                    Held(fileSystem, root, path, Path.Join(Path.GetDirectoryName(begun), Path.GetFileName(path)));
                }
                catch (IOException away)
                {
                    // Called off before a byte is written: the atomic write removes the file it began, and the target stays
                    // as it was.
                    refused = away;
                    throw new OperationCanceledException(away.Message);
                }

                write(stream);
            });
        }
        catch (OperationCanceledException) when (refused is not null)
        {
            throw refused;
        }
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to the file at <paramref name="path"/>, below <paramref name="root"/>, whole and
    /// atomically (<see cref="IFileSystem.WriteAllBytesAtomicAsync"/>) - refused, nothing written, where a link stands on
    /// the way to it, or where its directory stands anywhere but where its path says, as the system names it; a link at
    /// its own place is replaced, never written through.
    /// </summary>
    /// <param name="fileSystem">Writes the file and names its directory.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">What it is to hold.</param>
    /// <param name="cancellationToken">Stops the write, leaving the file as it was.</param>
    /// <exception cref="IOException">A link stands on the way, or the file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write it.</exception>
    public static async Task WriteAsync(IFileSystem fileSystem, string root, string path, byte[] contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(contents);

        Refuse(LinkOnTheWay(fileSystem, root, path, itself: false), path);

        // A directory the write makes on its way is made after the look, which found nothing there to follow.
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } directory && fileSystem.DirectoryExists(directory))
        {
            Held(fileSystem, root, path, Path.Join(fileSystem.FinalPathOfDirectory(directory), Path.GetFileName(path)));
        }

        await fileSystem.WriteAllBytesAtomicAsync(path, contents, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the file at <paramref name="source"/>, below <paramref name="sourceRoot"/>, to <paramref name="destination"/>,
    /// below <paramref name="destinationRoot"/>: read as <see cref="OpenRead"/> reads, written as <see cref="Write"/> writes.
    /// </summary>
    /// <param name="fileSystem">Reads, writes and names the files.</param>
    /// <param name="sourceRoot">The root of the place the file is read from.</param>
    /// <param name="source">The file read.</param>
    /// <param name="destinationRoot">The root of the place it is written in.</param>
    /// <param name="destination">The file written.</param>
    /// <exception cref="IOException">A link stands on either's way, or a file could not be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read the one or write the other.</exception>
    public static void Copy(IFileSystem fileSystem, string sourceRoot, string source, string destinationRoot, string destination)
    {
        using var read = OpenRead(fileSystem, sourceRoot, source);

        Write(fileSystem, destinationRoot, destination, write => read.CopyTo(write));
    }

    /// <summary>
    /// Removes the file at <paramref name="path"/>, below <paramref name="root"/> - refused where a link stands on the way
    /// to it; a link at its own place is removed itself, never what it leads to.
    /// </summary>
    /// <param name="fileSystem">Removes the file.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="path">The file.</param>
    /// <exception cref="IOException">A link stands on the way, or the file could not be removed.</exception>
    public static void Delete(IFileSystem fileSystem, string root, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        Refuse(LinkOnTheWay(fileSystem, root, path, itself: false), path);
        fileSystem.DeleteFile(path);
    }

    /// <summary>
    /// Makes the directory <paramref name="directory"/>, below <paramref name="root"/>, with every one on the way to it -
    /// refused where a link stands on the way or at it.
    /// </summary>
    /// <param name="fileSystem">Makes the directories.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="directory">The directory.</param>
    /// <exception cref="IOException">A link stands on the way, or a directory could not be made.</exception>
    public static void CreateDirectory(IFileSystem fileSystem, string root, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        Refuse(LinkOnTheWay(fileSystem, root, directory, itself: true), directory);
        fileSystem.CreateDirectory(directory);
    }

    /// <summary>The names on the way from <paramref name="root"/> to <paramref name="path"/>, in order.</summary>
    private static List<string> Parts(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var relative = Path.GetRelativePath(root, path);

        return relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? throw new ArgumentException($"'{path}' is not below '{root}'.", nameof(path))
            : [.. relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>Refuses a path a link stands on the way to.</summary>
    private static void Refuse(string? link, string path)
    {
        if (link is not null)
        {
            throw new IOException($"'{path}' is reached through the link '{link}', which the tool never follows in a place an isolated process writes");
        }
    }

    /// <summary>
    /// Refuses a file the system names at <paramref name="opened"/> where its path, <paramref name="path"/>, does not
    /// stand there: <paramref name="root"/> as the system names it, then the names below it as the path spells them. Told
    /// apart whatever the letter case, as a link leads elsewhere, never to another case of its own name.
    /// </summary>
    private static void Held(IFileSystem fileSystem, string root, string path, string opened)
    {
        var expected = Path.Join(fileSystem.FinalPathOfDirectory(root), Path.GetRelativePath(root, path));

        if (!string.Equals(Path.GetFullPath(expected), Path.GetFullPath(opened), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"'{path}' was opened as '{opened}', where a link put on its way leads, which the tool never follows in a place an isolated process writes");
        }
    }
}
