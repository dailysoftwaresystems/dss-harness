using System.IO.Enumeration;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// A directory held open by its handle, reached from an anchor with no link below it, and the one owner of every file
/// operation the tool performs outside the isolation in a place isolated work writes: each open, make, removal, rename and
/// listing is done relative to the directory held - never by a path looked up again - so a link put on the way, or swapped
/// in by a command still running while the operation runs, reaches nothing. Linux and macOS by a descriptor and the C
/// library's <c>*at</c> calls, <c>O_NOFOLLOW</c> on every name (<see cref="UnixHeld"/>); Windows by a handle and
/// <c>NtCreateFile</c> relative to it, every entry opened as itself and a name surrogate refused (<see cref="WindowsHeld"/>).
/// </summary>
/// <remarks>
/// The anchor is the one place taken as it is, its links followed: a writable root of the isolation, or a place above every
/// one, which no process inside moves. Below it a link is never entered, and an entry that is one is removed, renamed or
/// replaced as itself. What a directory holds is named by its handle on Windows and by its descriptor's own link under
/// <c>/proc</c> on Linux; on macOS by the path the system keeps for its descriptor, each name then acted on within the
/// directory held alone, so a listing raced there names other entries, never reaches another place.
/// </remarks>
public sealed class HeldDirectory : IDisposable
{
    /// <summary>This system's way of acting relative to a held directory.</summary>
    private static readonly IHeldNative Native = OperatingSystem.IsWindows() ? new WindowsHeld() : new UnixHeld();

    private readonly SafeFileHandle _handle;

    private HeldDirectory(SafeFileHandle handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    /// <summary>The directory's path, as it was reached: the anchor, then each name below it.</summary>
    public string Path { get; }

    /// <summary>
    /// Holds <paramref name="directory"/>, <paramref name="anchor"/> or below it: the anchor opened as it is, then each name
    /// below it as itself - a link refused (<see cref="LinkOnTheWayException"/>), a missing directory made where
    /// <paramref name="make"/>.
    /// </summary>
    /// <param name="anchor">The anchor, absolute: a place no isolated process moves or replaces.</param>
    /// <param name="directory">The directory, absolute, the anchor itself or below it.</param>
    /// <param name="make">Whether a directory missing on the way is made.</param>
    /// <param name="asked">The path the caller asked about, as a refusal names it; <paramref name="directory"/> where none is given.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is not the anchor or below it.</exception>
    /// <exception cref="LinkOnTheWayException">A link stands on the way, or at the directory.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the way is not there, and was not to be made; or a file stands there.</exception>
    /// <exception cref="IOException">A directory could not be opened or made.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not open or make one.</exception>
    internal static HeldDirectory Hold(string anchor, string directory, bool make, string? asked = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!System.IO.Path.IsPathFullyQualified(anchor))
        {
            throw new ArgumentException($"'{anchor}' is no whole path, and only a whole path anchors a place.", nameof(anchor));
        }

        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(anchor));
        var held = new HeldDirectory(Native.OpenAnchor(root), root);

        try
        {
            foreach (var name in Names(root, System.IO.Path.GetFullPath(directory)))
            {
                var below = held.Below(name, make, asked ?? directory);
                held.Dispose();
                held = below;
            }

            return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The names on the way from <paramref name="anchor"/> to <paramref name="path"/>, in order; none where it is the anchor.
    /// </summary>
    /// <param name="anchor">The anchor, absolute.</param>
    /// <param name="path">A path, absolute, the anchor or below it.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is neither.</exception>
    public static IReadOnlyList<string> Names(string anchor, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchor);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var relative = System.IO.Path.GetRelativePath(anchor, path);

        if (relative == ".")
        {
            return [];
        }

        return relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative)
            ? throw new ArgumentException($"'{path}' is not below '{anchor}'.", nameof(path))
            : [.. relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>
    /// The directory <paramref name="name"/> in this one, held: opened as itself, a link refused, made where it is not there
    /// and <paramref name="make"/>.
    /// </summary>
    /// <param name="name">One name, no separator in it.</param>
    /// <param name="make">Whether it is made where it is not there.</param>
    /// <param name="asked">The path the caller asked about, as a refusal names it.</param>
    /// <exception cref="LinkOnTheWayException">It is a link.</exception>
    /// <exception cref="DirectoryNotFoundException">It is not there and was not to be made, or it is a file.</exception>
    public HeldDirectory Below(string name, bool make, string? asked = null)
    {
        var path = Child(name);
        var opened = Native.OpenDirectory(_handle, name, path);

        if (opened.Handle is { } handle)
        {
            return new HeldDirectory(handle, path);
        }

        if (opened.Kind == PathKind.None && make)
        {
            return new HeldDirectory(Native.MakeDirectory(_handle, name, path), path);
        }

        throw NotADirectory(opened.Kind, path, asked ?? path);
    }

    /// <summary>What the entry <paramref name="name"/> of this directory is, itself: a link is never followed to say.</summary>
    /// <param name="name">One name.</param>
    public PathKind KindOf(string name) => Native.KindOf(_handle, name, Child(name));

    /// <summary>Every entry of this directory, each name with what it is itself, in no order.</summary>
    /// <exception cref="IOException">It could not be listed.</exception>
    public IReadOnlyList<HeldName> Entries() => Native.Entries(_handle, Path);

    /// <summary>
    /// Opens the file <paramref name="name"/> of this directory to read: opened as itself - a link refused, and anything but
    /// a regular file refused before a byte is read, a pipe never waited on.
    /// </summary>
    /// <param name="name">One name.</param>
    /// <exception cref="LinkOnTheWayException">It is a link.</exception>
    /// <exception cref="FileNotFoundException">Nothing is there.</exception>
    /// <exception cref="IOException">It is no regular file, or could not be opened.</exception>
    public Stream OpenRead(string name)
        => new FileStream(Native.OpenFile(_handle, name, Child(name)), FileAccess.Read, 4096);

    /// <summary>
    /// Writes the file <paramref name="name"/> of this directory whole: <paramref name="write"/> writes a file made new
    /// beside it, in this directory, which <paramref name="check"/> then reads back where it is given, and which is then
    /// renamed over it - a link at its place replaced as itself, never written through, and a file replaced keeping its
    /// mode, or given what <paramref name="mode"/> answers where it is given. What <paramref name="write"/> or
    /// <paramref name="check"/> raises leaves the file as it was and the one begun gone.
    /// </summary>
    /// <param name="name">One name.</param>
    /// <param name="write">Writes what the file is to hold; given the file begun, which <see cref="IFileSystem.FinalPathOf"/> names.</param>
    /// <param name="mode">
    /// The mode the file is given on Linux and macOS, from the mode it was made with - a copy's source's, a file made one
    /// to run - in place of the replaced file's; null to keep that.
    /// </param>
    /// <param name="check">Reads the file written back before it is put in place, raising where it is not what was meant; null for none.</param>
    public void Write(string name, Action<Stream> write, Func<UnixFileMode, UnixFileMode>? mode = null, Action<Stream>? check = null)
    {
        ArgumentNullException.ThrowIfNull(write);

        var path = Child(name);
        var temporary = Checked(name) + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        var stream = new FileStream(Native.CreateFile(_handle, temporary, Child(temporary)), FileAccess.Write, 4096);
        var placed = false;

        try
        {
            write(stream);
            stream.Flush();

            if (check is not null)
            {
                using var back = new FileStream(Native.OpenFile(_handle, temporary, Child(temporary)), FileAccess.Read, 4096);
                check(back);
            }

            if (mode is not null && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stream.SafeFileHandle, mode(File.GetUnixFileMode(stream.SafeFileHandle)));
            }
            else
            {
                Native.KeepMode(_handle, name, stream.SafeFileHandle);
            }

            Native.Replace(_handle, temporary, stream.SafeFileHandle, name, path);
            placed = true;
        }
        finally
        {
            if (!placed)
            {
                TakeBack(stream, temporary);
            }

            stream.Dispose();
        }
    }

    /// <summary>Removes the file <paramref name="name"/> of this directory; a link there is removed itself, never what it leads to.</summary>
    /// <param name="name">One name.</param>
    /// <exception cref="FileNotFoundException">Nothing is there.</exception>
    /// <exception cref="HeldIsADirectoryException">A directory is there.</exception>
    public void DeleteFile(string name) => Native.Remove(_handle, Checked(name), directory: false, Child(name));

    /// <summary>
    /// Removes the directory <paramref name="name"/> of this directory and everything in it, each entry relative to the
    /// directory it is in: a link anywhere among them removed itself, never entered; nothing where nothing is there. A link
    /// at <paramref name="name"/> itself is removed as a link.
    /// </summary>
    /// <param name="name">One name.</param>
    /// <exception cref="IOException">An entry could not be removed; what was removed before it stays removed.</exception>
    public void DeleteDirectory(string name)
    {
        switch (KindOf(name))
        {
            case PathKind.None:
                return;
            case PathKind.Link or PathKind.File:
                RemoveEntry(name);
                return;
        }

        HeldDirectory below;

        try
        {
            below = Below(name, make: false);
        }
        catch (Exception ex) when (ex is LinkOnTheWayException or DirectoryNotFoundException)
        {
            // Put in its place since it was looked at: a link or a file there is removed as itself.
            RemoveEntry(name);
            return;
        }

        using (below)
        {
            below.Empty();
        }

        Native.Remove(_handle, Checked(name), directory: true, Child(name));
    }

    /// <summary>Removes everything in this directory, each entry as <see cref="DeleteDirectory"/> removes it, and leaves it standing.</summary>
    public void Empty()
    {
        foreach (var entry in Entries())
        {
            if (entry.Kind == PathKind.Directory)
            {
                DeleteDirectory(entry.Name);
            }
            else
            {
                RemoveEntry(entry.Name);
            }
        }
    }

    /// <summary>Removes the directory <paramref name="name"/> of this directory where it is empty; false where it holds anything, or is no directory.</summary>
    /// <param name="name">One name.</param>
    /// <exception cref="IOException">It could not be removed for any other reason.</exception>
    public bool RemoveIfEmpty(string name)
    {
        try
        {
            Native.Remove(_handle, Checked(name), directory: true, Child(name));
            return true;
        }
        catch (Exception ex) when (ex is HeldNotEmptyException or HeldNotADirectoryException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renames the entry <paramref name="name"/> of this directory to <paramref name="newName"/> in <paramref name="into"/>,
    /// the entry renamed as itself - a link moved as the link - replacing what is there only where <paramref name="replace"/>.
    /// </summary>
    /// <param name="name">One name in this directory.</param>
    /// <param name="into">The directory it goes into; this one for a rename in place.</param>
    /// <param name="newName">Its name there.</param>
    /// <param name="replace">Whether an entry there is replaced; refused where false and one is.</param>
    /// <exception cref="IOException">It could not be renamed, or an entry is there and it was not to be replaced.</exception>
    public void Move(string name, HeldDirectory into, string newName, bool replace)
    {
        ArgumentNullException.ThrowIfNull(into);

        Native.Rename(_handle, Checked(name), into._handle, Checked(newName), replace, Child(name), into.Child(newName));
    }

    /// <summary>Where the directory held stands, as the system names it by its handle (<see cref="OpenedFiles"/>).</summary>
    public string FinalPath() => OpenedFiles.FinalPath(_handle);

    /// <inheritdoc/>
    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// What a name on the way that is no directory raises: a link refused naming it, else a part of the path not found, as
    /// the runtime says of a file where a directory should be.
    /// </summary>
    /// <param name="kind">What is there.</param>
    /// <param name="path">The entry.</param>
    /// <param name="asked">The path the caller asked about.</param>
    internal static Exception NotADirectory(PathKind kind, string path, string asked)
        => kind == PathKind.Link
            ? new LinkOnTheWayException(path, asked)
            : new DirectoryNotFoundException($"Could not find a part of the path '{asked}': '{path}' is {(kind == PathKind.None ? "not there" : "no directory")}.");

    /// <summary>
    /// The entries a listing of <paramref name="listed"/> names, each with what it is itself: where a directory's own handle
    /// cannot be listed, a path that names it.
    /// </summary>
    /// <param name="listed">The path listed.</param>
    /// <param name="path">The directory, as a fault names it.</param>
    internal static IReadOnlyList<HeldName> Listed(string listed, string path)
    {
        try
        {
            return
            [
                .. new FileSystemEnumerable<HeldName>(
                    listed,
                    (ref FileSystemEntry entry) => new HeldName(
                        entry.FileName.ToString(),
                        (entry.Attributes & FileAttributes.ReparsePoint) != 0 ? PathKind.Link : entry.IsDirectory ? PathKind.Directory : PathKind.File),
                    new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false, MatchType = MatchType.Win32 }),
            ];
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new IOException($"'{path}' could not be listed: {ex.Message}", ex);
        }
    }

    /// <summary>Removes an entry that is no directory as itself, or a directory put in its place since, whole.</summary>
    private void RemoveEntry(string name)
    {
        try
        {
            DeleteFile(name);
        }
        catch (HeldIsADirectoryException)
        {
            DeleteDirectory(name);
        }
        catch (FileNotFoundException)
        {
        }
    }

    /// <summary>Removes a file begun and not put in place; one that cannot be is left, and never hides why it was not placed.</summary>
    private void TakeBack(Stream stream, string temporary)
    {
        try
        {
            stream.Dispose();
            Native.Remove(_handle, temporary, directory: false, Child(temporary));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The path of the entry <paramref name="name"/> of this directory.</summary>
    private string Child(string name) => System.IO.Path.Join(Path, Checked(name));

    /// <summary><paramref name="name"/>, where it is one name: no separator, no NUL, and neither <c>.</c> nor <c>..</c>.</summary>
    private static string Checked(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return name is "." or ".." || name.Contains('\0', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal)
            || (OperatingSystem.IsWindows() && name.Contains('\\', StringComparison.Ordinal))
            ? throw new ArgumentException($"'{name}' is no one name of an entry.", nameof(name))
            : name;
    }
}

/// <summary>An entry of a held directory: its name, and what it is itself.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What it is: a file, a directory or a link, never followed.</param>
public sealed record HeldName(string Name, PathKind Kind);

/// <summary>
/// A link on the way to a place the tool reads, writes or removes in, which it never follows: raised naming the link and
/// the path asked about.
/// </summary>
/// <param name="link">The link.</param>
/// <param name="asked">The path asked about.</param>
public sealed class LinkOnTheWayException(string link, string asked)
    : IOException($"'{asked}' is reached through the link '{link}', which the tool never follows in a place an isolated process writes")
{
    /// <summary>The link.</summary>
    public string Link { get; } = link;
}

/// <summary>A directory a removal of a file met.</summary>
/// <param name="path">The directory.</param>
public sealed class HeldIsADirectoryException(string path) : IOException($"'{path}' is a directory");

/// <summary>A file a removal of a directory met.</summary>
/// <param name="path">The file.</param>
public sealed class HeldNotADirectoryException(string path) : IOException($"'{path}' is no directory");

/// <summary>A directory a removal found holding something.</summary>
/// <param name="path">The directory.</param>
public sealed class HeldNotEmptyException(string path) : IOException($"'{path}' is not empty");

/// <summary>A directory opened on the way, or why not: nothing there, or what is there instead.</summary>
/// <param name="Handle">The directory's handle; null where it was not opened.</param>
/// <param name="Kind">What is there where it was not opened: nothing, a link or a file.</param>
internal sealed record HeldOpen(SafeFileHandle? Handle, PathKind Kind)
{
    /// <summary>Nothing is there.</summary>
    public static HeldOpen Missing { get; } = new(null, PathKind.None);

    /// <summary>The directory, held.</summary>
    public static HeldOpen Of(SafeFileHandle handle) => new(handle, PathKind.Directory);

    /// <summary>What is there, which is no directory.</summary>
    public static HeldOpen Not(PathKind kind) => new(null, kind);
}

/// <summary>One system's way of acting relative to a held directory: the calls <see cref="HeldDirectory"/> is made of.</summary>
internal interface IHeldNative
{
    /// <summary>Opens the anchor as it is, its links followed.</summary>
    SafeFileHandle OpenAnchor(string path);

    /// <summary>Opens the directory <paramref name="name"/> of <paramref name="parent"/> as itself; or says what is there instead.</summary>
    HeldOpen OpenDirectory(SafeFileHandle parent, string name, string path);

    /// <summary>Makes the directory <paramref name="name"/> of <paramref name="parent"/> and holds it; one made in the meantime held as any is.</summary>
    SafeFileHandle MakeDirectory(SafeFileHandle parent, string name, string path);

    /// <summary>What the entry <paramref name="name"/> of <paramref name="parent"/> is itself.</summary>
    PathKind KindOf(SafeFileHandle parent, string name, string path);

    /// <summary>Every entry of the directory <paramref name="directory"/> holds.</summary>
    IReadOnlyList<HeldName> Entries(SafeFileHandle directory, string path);

    /// <summary>Opens the regular file <paramref name="name"/> of <paramref name="parent"/> to read, as itself.</summary>
    SafeFileHandle OpenFile(SafeFileHandle parent, string name, string path);

    /// <summary>Makes the file <paramref name="name"/> of <paramref name="parent"/>, new, to write.</summary>
    SafeFileHandle CreateFile(SafeFileHandle parent, string name, string path);

    /// <summary>Gives <paramref name="written"/> the mode of the regular file <paramref name="name"/> of <paramref name="parent"/>, where one is there.</summary>
    void KeepMode(SafeFileHandle parent, string name, SafeFileHandle written);

    /// <summary>Renames the file written, <paramref name="temporary"/>, over <paramref name="name"/> in <paramref name="parent"/>.</summary>
    void Replace(SafeFileHandle parent, string temporary, SafeFileHandle written, string name, string path);

    /// <summary>Renames an entry as itself from one held directory into another, replacing only where asked.</summary>
    void Rename(SafeFileHandle fromParent, string fromName, SafeFileHandle toParent, string toName, bool replace, string from, string to);

    /// <summary>Removes the entry <paramref name="name"/> of <paramref name="parent"/> as itself: an empty directory where <paramref name="directory"/>, else anything else.</summary>
    void Remove(SafeFileHandle parent, string name, bool directory, string path);
}
