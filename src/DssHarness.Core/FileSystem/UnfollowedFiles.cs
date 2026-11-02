namespace DssHarness.Core.FileSystem;

/// <summary>
/// The one way the tool reads, writes, copies, moves or removes a file or a directory of its own inside a place an isolated
/// process writes - a leg's tree or its copy, the outputs a runner's steps leave in it, the build directory a test leaves
/// evidence in, an agent's worktree and work directory: never through a link. Each acts relative to a directory held open
/// from the place's root with no link on the way (<see cref="IFileSystem.Hold"/>, <see cref="HeldDirectory"/>), so a link
/// put on the way - or swapped in by a command still running while the tool acts - reaches nothing, and a link at the
/// entry's own place is read as nothing, replaced or removed as itself. A link such a process plants there would otherwise
/// have the tool, outside every isolation, write what the process chose wherever the link leads - over a file of the
/// user's - or carry what a hidden place holds to where the agents read it.
/// </summary>
/// <remarks>
/// The root is the one place taken as it is: it is a writable root of the isolation, or a place above every one, which no
/// process inside an isolation moves - and whatever links lead to it were there before. A directory held and then moved by
/// such a process is acted on where it went: inside the places that process writes, never through a link out of them.
/// </remarks>
public static class UnfollowedFiles
{
    /// <summary>
    /// The first entry on the way to <paramref name="path"/> from <paramref name="root"/> down that is a link - a
    /// symbolic link or a junction alike - <paramref name="path"/> itself among them where <paramref name="itself"/>; null
    /// where none is. A look, for a caller deciding what to say or to pass by: what it then does goes through the other
    /// members, which refuse a link put there since.
    /// </summary>
    /// <param name="fileSystem">Reads what each entry is.</param>
    /// <param name="root">The place's root, which is never asked about itself.</param>
    /// <param name="path">A path below it.</param>
    /// <param name="itself">Whether <paramref name="path"/> itself is asked about.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not below <paramref name="root"/>.</exception>
    public static string? LinkOnTheWay(IFileSystem fileSystem, string root, string path, bool itself)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var parts = Below(root, path);
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
    /// on its way or at it, and where it is no regular file, before a byte is read.
    /// </summary>
    /// <param name="fileSystem">Holds the file's directory.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="path">The file.</param>
    /// <returns>The open file, which the caller disposes.</returns>
    /// <exception cref="LinkOnTheWayException">A link stands on the way, or at the file.</exception>
    /// <exception cref="FileNotFoundException">No file is there.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the way is not there.</exception>
    /// <exception cref="IOException">It is no regular file, or could not be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read it.</exception>
    public static Stream OpenRead(IFileSystem fileSystem, string root, string path)
    {
        var (directory, name) = Split(root, path);

        using var held = Held(fileSystem, root, directory, make: false, path);

        return held.OpenRead(name);
    }

    /// <summary>
    /// Writes the file at <paramref name="path"/>, below <paramref name="root"/>, whole and atomically: a file made new in its
    /// directory, held, then renamed over it - refused, nothing written, where a link stands on the way to it; a link at its
    /// own place is replaced, never written through; the directories on the way made.
    /// </summary>
    /// <param name="fileSystem">Holds the file's directory.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="path">The file.</param>
    /// <param name="write">Writes what it is to hold.</param>
    /// <exception cref="LinkOnTheWayException">A link stands on the way.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write it.</exception>
    public static void Write(IFileSystem fileSystem, string root, string path, Action<Stream> write) => Written(fileSystem, root, path, write, mode: null);

    /// <summary>
    /// Writes <paramref name="contents"/> to the file at <paramref name="path"/>, below <paramref name="root"/>, whole and
    /// atomically, as <see cref="Write"/> writes.
    /// </summary>
    /// <param name="fileSystem">Holds the file's directory.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">What it is to hold.</param>
    /// <param name="cancellationToken">Stops the write before it begins, leaving the file as it was.</param>
    /// <exception cref="LinkOnTheWayException">A link stands on the way.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write it.</exception>
    public static Task WriteAsync(IFileSystem fileSystem, string root, string path, byte[] contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);
        cancellationToken.ThrowIfCancellationRequested();

        Write(fileSystem, root, path, stream => stream.Write(contents));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies the file at <paramref name="source"/>, below <paramref name="sourceRoot"/>, to <paramref name="destination"/>,
    /// below <paramref name="destinationRoot"/>: read as <see cref="OpenRead"/> reads, written as <see cref="Write"/> writes,
    /// with the mode the file read has on Linux and macOS, as the platform's own copy keeps it - a script stays one to run.
    /// </summary>
    /// <param name="fileSystem">Holds both files' directories.</param>
    /// <param name="sourceRoot">The root of the place the file is read from.</param>
    /// <param name="source">The file read.</param>
    /// <param name="destinationRoot">The root of the place it is written in.</param>
    /// <param name="destination">The file written.</param>
    /// <exception cref="IOException">A link stands on either's way, or a file could not be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read the one or write the other.</exception>
    public static void Copy(IFileSystem fileSystem, string sourceRoot, string source, string destinationRoot, string destination)
    {
        using var read = OpenRead(fileSystem, sourceRoot, source);

        Written(fileSystem, destinationRoot, destination, write => read.CopyTo(write), ModeOf(read));
    }

    /// <summary>The mode <paramref name="read"/>'s file has, as a copy of it is given it on Linux and macOS; null on Windows, which keeps none.</summary>
    /// <param name="read">A file opened by <see cref="OpenRead"/>.</param>
    internal static Func<UnixFileMode, UnixFileMode>? ModeOf(Stream read)
    {
        if (OperatingSystem.IsWindows() || read is not FileStream { SafeFileHandle: var handle })
        {
            return null;
        }

        var mode = File.GetUnixFileMode(handle);

        return _ => mode;
    }

    /// <summary>
    /// <see cref="Write(IFileSystem, string, string, Action{Stream})"/>, the file given what <paramref name="mode"/> answers
    /// on Linux and macOS where it is given, in place of the mode of the file it replaces, and read back by
    /// <paramref name="check"/> before it is put in place where that is given (<see cref="HeldDirectory.Write"/>).
    /// </summary>
    internal static void Written(IFileSystem fileSystem, string root, string path, Action<Stream> write, Func<UnixFileMode, UnixFileMode>? mode, Action<Stream>? check = null)
    {
        ArgumentNullException.ThrowIfNull(write);

        var (directory, name) = Split(root, path);

        using var held = Held(fileSystem, root, directory, make: true, path);

        held.Write(name, write, mode, check);
    }

    /// <summary>
    /// Removes the file at <paramref name="path"/>, below <paramref name="root"/> - refused where a link stands on the way to
    /// it; a link at its own place is removed itself, never what it leads to; nothing where no file is there.
    /// </summary>
    /// <param name="fileSystem">Holds the file's directory.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="path">The file.</param>
    /// <exception cref="LinkOnTheWayException">A link stands on the way.</exception>
    /// <exception cref="IOException">The file could not be removed.</exception>
    public static void Delete(IFileSystem fileSystem, string root, string path)
    {
        var (directory, name) = Split(root, path);

        try
        {
            using var held = Held(fileSystem, root, directory, make: false, path);

            held.DeleteFile(name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or HeldIsADirectoryException)
        {
            // No file there - nothing, a directory, or no directory it would be in: nothing to remove.
        }
    }

    /// <summary>
    /// Removes the directory at <paramref name="path"/>, below <paramref name="root"/>, and everything in it - refused where a
    /// link stands on the way to it; every entry removed relative to the directory it is in, a link anywhere among them
    /// removed itself and never entered, a link at <paramref name="path"/> itself removed as a link; nothing where nothing
    /// is there.
    /// </summary>
    /// <param name="fileSystem">Holds the directory's parent.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="path">The directory.</param>
    /// <exception cref="LinkOnTheWayException">A link stands on the way.</exception>
    /// <exception cref="IOException">An entry could not be removed; what was removed before it stays removed.</exception>
    public static void DeleteDirectory(IFileSystem fileSystem, string root, string path)
    {
        var (directory, name) = Split(root, path);

        try
        {
            using var held = Held(fileSystem, root, directory, make: false, path);

            held.DeleteDirectory(name);
        }
        catch (DirectoryNotFoundException)
        {
            // No directory it would be in: nothing to remove.
        }
    }

    /// <summary>
    /// Makes the directory <paramref name="directory"/>, below <paramref name="root"/>, with every one on the way to it -
    /// refused where a link stands on the way or at it.
    /// </summary>
    /// <param name="fileSystem">Makes the directories.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="directory">The directory.</param>
    /// <exception cref="LinkOnTheWayException">A link stands on the way, or at the directory.</exception>
    /// <exception cref="IOException">A directory could not be made.</exception>
    public static void CreateDirectory(IFileSystem fileSystem, string root, string directory)
    {
        using var held = Held(fileSystem, root, directory, make: true, directory);
    }

    /// <summary>
    /// Moves the entry at <paramref name="source"/> to <paramref name="destination"/>, both below <paramref name="root"/>: the
    /// entry renamed as itself - a link moved as the link - from its directory, held, into the destination's, held and made
    /// where it is not there, replacing what is there only where <paramref name="replace"/>; refused where a link stands on
    /// either's way.
    /// </summary>
    /// <param name="fileSystem">Holds both directories.</param>
    /// <param name="root">The place's root.</param>
    /// <param name="source">The entry moved.</param>
    /// <param name="destination">Where it goes.</param>
    /// <param name="replace">Whether what is at <paramref name="destination"/> is replaced; refused where false and something is.</param>
    /// <exception cref="LinkOnTheWayException">A link stands on either's way.</exception>
    /// <exception cref="IOException">It could not be moved, or something is at the destination and it was not to be replaced.</exception>
    public static void Move(IFileSystem fileSystem, string root, string source, string destination, bool replace)
    {
        var (from, fromName) = Split(root, source);
        var (to, toName) = Split(root, destination);

        using var fromHeld = Held(fileSystem, root, from, make: false, source);
        using var toHeld = Held(fileSystem, root, to, make: true, destination);

        fromHeld.Move(fromName, toHeld, toName, replace);
    }

    /// <summary>The directory <paramref name="directory"/>, below or at <paramref name="root"/>, held.</summary>
    private static HeldDirectory Held(IFileSystem fileSystem, string root, string directory, bool make, string asked)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        // Where the way is to be made, the root is made first where it is not there: the root is the place taken as it
        // is, above every link the walk refuses, and a write that makes the way makes it as it made the rest - a pull's
        // landing, before its first file, is a root that is not there yet.
        if (make)
        {
            fileSystem.CreateDirectory(root);
        }

        return fileSystem.Hold(root, directory, make, asked);
    }

    /// <summary><paramref name="path"/>'s directory and its own name, where it is below <paramref name="root"/>.</summary>
    private static (string Directory, string Name) Split(string root, string path)
    {
        Below(root, path);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        return (Path.GetDirectoryName(full)!, Path.GetFileName(full));
    }

    /// <summary>The names on the way from <paramref name="root"/> to <paramref name="path"/>, in order; at least one.</summary>
    private static IReadOnlyList<string> Below(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var names = HeldDirectory.Names(root, path);

        return names.Count > 0 ? names : throw new ArgumentException($"'{path}' is not below '{root}'.", nameof(path));
    }
}
