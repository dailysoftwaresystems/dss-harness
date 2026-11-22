using System.Globalization;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using DssHarness.Core.Platform;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.FileSystem;

/// <inheritdoc cref="IFileSystem"/>
/// <param name="filePermissions">How this platform expresses a file's permissions.</param>
/// <param name="told">
/// Told each wait a rename makes while another program holds a file it needs, as it begins and while it lasts; nothing
/// is told where none is given.
/// </param>
public sealed class PhysicalFileSystem(IFilePermissions filePermissions, Action<WaitTold>? told = null) : IFileSystem
{
    /// <summary>
    /// The first wait before a refused rename is tried again, doubled each wait after it up to
    /// <see cref="Waits.SaidEvery"/>: a holder of a file just written - a virus scanner, an indexer - mostly lets go in
    /// moments, and one that holds longer is waited for as long as it holds.
    /// </summary>
    internal static readonly TimeSpan FirstReplaceWait = TimeSpan.FromMilliseconds(10);

    private readonly IFilePermissions _filePermissions = filePermissions;
    private readonly Action<WaitTold> _told = told ?? (static _ => { });

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ResolveLinks(string path) => LinkPaths.Resolve(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException) when ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
        {
            // Windows refuses to delete a read-only file, as git marks the ones under .git/objects; cleared only once
            // the delete refuses, as DeleteDirectory clears them.
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    public PathKind KindOf(string path)
    {
        FileAttributes attributes;

        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathKind.None;
        }

        // A reparse point that is no link - a file a cloud or deduplication service keeps - is what it looks like.
        return (attributes & FileAttributes.ReparsePoint) != 0 && IsLink(path) ? PathKind.Link
            : (attributes & FileAttributes.Directory) != 0 ? PathKind.Directory
            : PathKind.File;
    }

    public string CopyToTemporaryFile(string path)
    {
        var copy = Path.Combine(Path.GetTempPath(), "dssharness-" + Guid.NewGuid().ToString("N"));

        try
        {
            File.Copy(path, copy);
        }
        catch
        {
            // A copy that failed part way must not stay behind as a partial file.
            TryDelete(copy);
            throw;
        }

        return copy;
    }

    public void CopyFile(string source, string destination, bool overwrite = false)
        => File.Copy(source, destination, overwrite);

    public void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        DeleteMarked(path);
    }

    /// <summary>
    /// Removes the directory at <paramref name="path"/> and all in it, the files marked read-only among them - git marks
    /// those under <c>.git/objects</c> so - each entry relative to the directory it is in, held from the directory's own
    /// parent (<see cref="HeldDirectory"/>): a link or a junction anywhere in it removed as itself and never entered, a
    /// volume mounted on a directory in it refused and never unmounted, and a link a process still writing there swaps in
    /// while the removal runs reaching nothing. The directory itself, where it is a link, is removed as the link.
    /// </summary>
    /// <param name="path">The directory, which is there.</param>
    /// <exception cref="JunctionRemovalException">A volume is mounted on a directory in it; what was removed before stays removed.</exception>
    /// <exception cref="IOException">An entry could not be removed; what was removed before it stays removed.</exception>
    internal static void DeleteMarked(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(full) ?? throw new IOException($"'{path}' is a volume's own root, which is never removed");

        using var held = HeldDirectory.Hold(parent, parent, make: false);

        held.DeleteDirectory(Path.GetFileName(full));
    }

    public void EmptyDirectory(string path, IReadOnlyCollection<string> keeping, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keeping);

        if (!Directory.Exists(path))
        {
            return;
        }

        // Held from its parent, as DeleteMarked holds a directory it removes: a link at the directory itself is refused
        // there, never entered, and everything below it is reached relative to a handle, however long its path.
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(full) ?? throw new IOException($"'{path}' is a volume's own root, which is never emptied");

        using var held = HeldDirectory.Hold(parent, full, make: false);

        held.Empty(keeping, cancellationToken);
    }

    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public void ReplaceFile(string source, string destination) => ReplaceWith(source, destination);

    public bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    public long DirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var total = 0L;

        // A directory this user cannot read counts as nothing, rather than ending the count: a leftover of a
        // build run as another user would otherwise stop every clean of the directory it is in.
        foreach (var length in Walk(path, recursive: true, (ref FileSystemEntry entry) => !entry.IsDirectory, (ref FileSystemEntry entry) => entry.Length, ignoreInaccessible: true))
        {
            total += length;
        }

        return total;
    }

    public DiskSpace SpaceAt(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var existing = MeasuredAt(path);

        try
        {
            // Windows measures a volume through its root, and refuses a longer path; elsewhere the call
            // answers for any path on the filesystem.
            var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(existing) ?? existing : existing);

            return new DiskSpace(drive.AvailableFreeSpace, drive.TotalSize, MountOf(existing));
        }
        catch (ArgumentException ex)
        {
            throw new IOException($"The room on the filesystem of '{path}' cannot be asked: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The directory whose filesystem <see cref="SpaceAt"/> measures <paramref name="path"/> by: where the path leads,
    /// through every link along it, then the nearest directory there that exists.
    /// </summary>
    /// <remarks>
    /// Followed through <see cref="LinkPaths.Resolve"/>, the one walk of links, before any directory along it is asked
    /// whether it exists, so a chain of links however long is followed by that walk and never by the system's, whose bound
    /// on a chain would leave the path as written. A build directory linked onto another disk is on that disk, and
    /// measured through the path as written it was the room of the disk the link sits on. A directory not made yet will be
    /// made on the filesystem of the nearest one above it that exists, which is the room it has: a copy a first sync has
    /// still to create, the build directory of a leg that never built. A link nobody may read, or links that lead back to
    /// themselves, leave the path measured where it is written: it is still somewhere.
    /// </remarks>
    /// <param name="path">The path asked about.</param>
    internal static string MeasuredAt(string path)
    {
        var existing = Path.GetFullPath(path);

        try
        {
            existing = LinkPaths.Resolve(existing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Measured where it is written.
        }

        while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
        {
            existing = parent;
        }

        return existing;
    }

    /// <summary>
    /// Where the filesystem <paramref name="path"/> is on is mounted, as a reader would look it up: its
    /// drive on Windows, and elsewhere the deepest mount point above it.
    /// </summary>
    private static string MountOf(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // In upper case, as Windows spells a drive: as a path spelled it - 'c:\' where somebody typed it so - one drive
            // would be two filesystems to everything that keys room by its name.
            return (Path.GetPathRoot(path) ?? path).ToUpperInvariant();
        }

        try
        {
            return DriveInfo.GetDrives()
                .Select(drive => Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName))
                .Where(root => root == "/" || path == root || path.StartsWith(root + "/", StringComparison.Ordinal))
                .OrderByDescending(root => root.Length)
                .FirstOrDefault() ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Named by the path itself where the mounts cannot be listed: the room is still measured.
            return path;
        }
    }

    public IEnumerable<string> EnumerateFiles(string path, bool recursive)
        => Walk(path, recursive, (ref FileSystemEntry entry) => !entry.IsDirectory, (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath());

    public IEnumerable<string> EnumerateReachableFiles(string path, Action<string, Exception> unread)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(unread);

        // A directory at a time, so one that cannot be read is passed by alone where one walk would stop at it.
        var waiting = new Stack<string>([path]);

        while (waiting.TryPop(out var directory))
        {
            List<string> files;
            List<string> beneath;

            try
            {
                files = [.. Walk(directory, recursive: false, (ref FileSystemEntry entry) => !entry.IsDirectory, (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath())];
                beneath = [.. Walk(directory, recursive: false, (ref FileSystemEntry entry) => entry.IsDirectory && !IsLink(ref entry), (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath())];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !ReferenceEquals(directory, path))
            {
                unread(directory, ex);
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var next in Enumerable.Reverse(beneath))
            {
                waiting.Push(next);
            }
        }
    }

    public IEnumerable<WrittenFile> EnumerateWrittenFiles(string path)
        => Dated(Walk(
            path,
            recursive: true,
            (ref FileSystemEntry entry) => !entry.IsDirectory,
            (ref FileSystemEntry entry) => new WrittenFile(entry.ToSpecifiedFullPath(), entry.LastWriteTimeUtc.UtcDateTime) { Length = entry.Length }));

    /// <summary>
    /// <paramref name="walked"/>, each dated by the walk's own reading of its time where the walk had one,
    /// and by asking it again directly where it had none; a file gone since the walk listed it is left out.
    /// </summary>
    /// <param name="walked">The files a walk listed, with the times it read for them.</param>
    /// <remarks>
    /// The walk's own reading costs nothing more on Windows. Where the runtime could not stat an entry it
    /// answers 1601, and the file is asked again directly. On Linux and macOS a walk lists a directory
    /// before it stats what is in it, so a file something else removes in between - a compiler's
    /// temporary, a test's scratch file - is one it could not stat, and asked again it is not there. It is
    /// no longer in the directory at all, and the walk describes what is: raised, one file deleted at the
    /// wrong moment left a build nothing to date its changes against. One that is there, and whose time
    /// still cannot be read, raises what kept it.
    /// </remarks>
    internal IEnumerable<WrittenFile> Dated(IEnumerable<WrittenFile> walked)
    {
        ArgumentNullException.ThrowIfNull(walked);

        foreach (var file in walked)
        {
            if (file.LastWriteTimeUtc != Unstatted)
            {
                yield return file;
                continue;
            }

            DateTime written;
            long length;

            try
            {
                written = LastWriteTimeUtc(file.Path);
                length = new FileInfo(file.Path).Length;
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            yield return file with { LastWriteTimeUtc = written, Length = length };
        }
    }

    public IEnumerable<string> EnumerateDirectoryLinks(string path)
        => Walk(path, recursive: true, (ref FileSystemEntry entry) => entry.IsDirectory && IsLink(ref entry), (ref FileSystemEntry entry) => entry.ToSpecifiedFullPath());

    public IReadOnlyList<string> RemoveJunctions(string path)
    {
        // A root that is itself a link is not walked: listing it would list what it leads to.
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path) || IsLink(path))
        {
            return [];
        }

        // Every junction is found before any is removed, so the walk never meets a directory it just changed.
        var removed = new List<string>();

        foreach (var link in EnumerateDirectoryLinks(path).ToList())
        {
            try
            {
                switch (WindowsJunctions.KindOf(link))
                {
                    case JunctionKind.Junction:
                        WindowsJunctions.Remove(link);
                        removed.Add(link);
                        break;

                    case JunctionKind.MountedVolume:
                        throw new JunctionRemovalException(link, HeldDirectory.MountedThere, [.. removed]);
                }
            }
            catch (IOException ex) when (ex is not JunctionRemovalException)
            {
                throw new JunctionRemovalException(link, ex.Message, [.. removed]);
            }
        }

        return removed;
    }

    public IReadOnlyList<HeldEntry> FindHeld(string path, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return [];
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var held = new List<HeldEntry>();

        // The directory itself first, then everything under it, as a deletion reaches each. The directory is looked
        // at before the walk is made, which opens it to list it: an interruption then leaves nothing open.
        Look(root, isDirectory: true);

        foreach (var (entryPath, isDirectory) in Walk(root, recursive: true, static (ref FileSystemEntry _) => true, (ref FileSystemEntry entry) => (entry.ToSpecifiedFullPath(), entry.IsDirectory)))
        {
            Look(entryPath, isDirectory);
        }

        return held;

        [SupportedOSPlatform("windows")]
        void Look(string entryPath, bool isDirectory)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (WindowsHolds.Held(entryPath, isDirectory) is { } found)
            {
                held.Add(found);
            }
        }
    }

    /// <summary>The time the runtime gives an entry it could not stat.</summary>
    private static readonly DateTime Unstatted = DateTime.FromFileTimeUtc(0);

    /// <summary>
    /// The entries under <paramref name="path"/> that <paramref name="include"/> accepts, as
    /// <paramref name="transform"/> reads each, never walking a directory reached through a link.
    /// </summary>
    /// <remarks>
    /// What the plain overload did - nothing skipped, and a directory that cannot be read said so -
    /// with one difference: a directory reached through a link or a junction is never walked. A link
    /// inside a tree leads either out of it, to files that tree does not contain, or back into it,
    /// which a walk follows until the stack goes. One walk for the files and for the links it passed
    /// over, so the links a caller is told about are exactly the directories it did not see.
    /// </remarks>
    private static FileSystemEnumerable<T> Walk<T>(
        string path,
        bool recursive,
        FileSystemEnumerable<T>.FindPredicate include,
        FileSystemEnumerable<T>.FindTransform transform,
        bool ignoreInaccessible = false)
        => new(
            path,
            transform,
            new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                AttributesToSkip = 0,
                IgnoreInaccessible = ignoreInaccessible,
                MatchType = MatchType.Win32,
            })
        {
            ShouldIncludePredicate = include,
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsLink(ref entry),
        };

    private static bool IsLink(ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0;

    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);

    public void WriteAtomic(string path, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);

        var temporary = TemporaryBeside(path);

        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                write(stream);
            }

            _filePermissions.CopyMode(path, temporary);
            ReplaceWith(temporary, path);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public DateTime LastWriteTimeUtc(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);

        // Refreshed, so it answers for now; Refresh() itself raises nothing. A path with nothing at it, a
        // directory, and a file the runtime could not look up all read as no file there. Asked for its
        // time, the runtime then raises what kept it from looking the last up - measured on Linux, a file
        // in a directory that can be listed but not searched raised UnauthorizedAccessException - and
        // answers for the others: 1601-01-01 for nothing, which reads as a real time, and a directory's
        // own time, which is no file's. Those two are raised here as not found.
        info.Refresh();

        if (info.Exists)
        {
            return info.LastWriteTimeUtc;
        }

        _ = info.LastWriteTimeUtc;

        throw new FileNotFoundException($"'{path}' is not there to be asked when it was last written.", path);
    }

    public FileStanding? StandingOf(string path) => FileStanding.Of(path);

    public DateTime CreationTimeUtc(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var info = new FileInfo(path);

        // Asked as LastWriteTimeUtc asks, so it raises what that raises rather than answering 1601.
        info.Refresh();

        if (info.Exists)
        {
            return info.CreationTimeUtc;
        }

        _ = info.CreationTimeUtc;

        throw new FileNotFoundException($"'{path}' is not there to be asked when it was created.", path);
    }

    public void SetLastWriteTimeUtc(string path, DateTime writtenUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // The runtime would date a directory as readily, on Linux and macOS, and on Windows raise that access was denied:
        // either way not what a caller dating a file it wrote should be told about a path holding none.
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"'{path}' is not there to be dated.", path);
        }

        File.SetLastWriteTimeUtc(path, writtenUtc);
    }

    public string FinalPathOf(Stream opened)
    {
        ArgumentNullException.ThrowIfNull(opened);

        return opened is FileStream { SafeFileHandle: var handle }
            ? OpenedFiles.FinalPath(handle)
            : throw new NotSupportedException($"a {opened.GetType().Name} is open on no file this file system opened, so where it stands cannot be named");
    }

    public string FinalPathOfDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        return OperatingSystem.IsWindows() ? OpenedFiles.FinalDirectoryPath(directory) : LinkPaths.Resolve(directory);
    }

    public Stream OpenRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // A pipe, a socket or a device opens as a file does, and a pipe's open waits for a writer past any stop: opened never
        // to wait, then refused by what the descriptor is open on before a byte is read - so one put at the path after a
        // look is met here too, never waited on.
        if (!OperatingSystem.IsWindows())
        {
            return new FileStream(UnixFiles.OpenRegular(path), FileAccess.Read, 4096);
        }

        // Sequential, and sharing read access: a tree sync walks every file in a repository while
        // other tools legitimately hold some of them open for reading.
        return new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
            });
    }

    public HeldDirectory Hold(string anchor, string directory, bool make, string? asked = null)
        => HeldDirectory.Hold(anchor, directory, make, asked);

    public IAppendFile OpenForAppend(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Read and written by its one writer, and read beside it: a live step's record is read while it grows, and a
        // second writer would interleave its lines with the first's - or cut one the first is still writing.
        return PhysicalAppendFile.Open(path);
    }

    public async Task WriteAllBytesAtomicAsync(
        string path,
        byte[] contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var temporary = TemporaryBeside(path);

        try
        {
            await File.WriteAllBytesAsync(temporary, contents, cancellationToken).ConfigureAwait(false);
            _filePermissions.CopyMode(path, temporary);
            await ReplaceWithAsync(temporary, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public void ProtectSecretFile(string path) => _filePermissions.ProtectSecret(path);

    /// <summary>
    /// A temporary file's name beside <paramref name="path"/>, its directory made where it is not there: an atomic write
    /// renames the one over the other, which only one volume can do.
    /// </summary>
    private static string TemporaryBeside(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="destination"/> in a single
    /// operation that replaces any existing file.
    /// </summary>
    /// <remarks>
    /// Checking whether the destination exists and then choosing between a replace and
    /// a move races: two writers can both see it absent, and the second move throws.
    /// One overwriting rename has no such window. Windows can still refuse the rename
    /// while another program holds the destination or the file just written - a concurrent
    /// writer, a virus scanner, an indexer - so a refusal is tried again while it can change
    /// (<see cref="ReplaceWithAsync(string, string, Action{string, string}, Action{WaitTold}, TimeProvider, CancellationToken)"/>):
    /// the one wait every replace takes, which the rename itself never adds to.
    /// </remarks>
    private void ReplaceWith(string source, string destination)
        => ReplaceWithAsync(source, destination, Replace, _told, TimeProvider.System, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// <see cref="ReplaceWith(string, string)"/> without blocking the thread between tries, and stopping when the caller
    /// does.
    /// </summary>
    /// <remarks>
    /// The synchronous form blocks its thread for as long as a holder holds the file. A tree sync writes thousands of
    /// files, and the parallel legs above it are sharing the thread pool: blocking in it makes a hold on one file look like
    /// a stall in every other leg.
    /// </remarks>
    private Task ReplaceWithAsync(string source, string destination, CancellationToken cancellationToken)
        => ReplaceWithAsync(source, destination, Replace, _told, TimeProvider.System, cancellationToken);

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="destination"/> through <paramref name="replace"/>, trying
    /// again while the refusal can change (<see cref="Retrying"/>): for as long as another program holds the file there or
    /// the file just written - a holder Windows reports by a sharing or a lock violation, or one a look at either file
    /// finds - each wait told, the held file named; and once more after any other refusal, which is raised where it meets
    /// the same state again. A directory standing at the destination is refused at once: no wait moves it. No count of
    /// tries ends it - an indexer holding a file for two minutes is waited out - and the last refusal is raised as it was
    /// raised where the tries end.
    /// </summary>
    /// <param name="source">The file to put in place.</param>
    /// <param name="destination">Where to put it.</param>
    /// <param name="replace">Renames the one over the other once (<see cref="Replace"/>): a test's where it stands in for the rename.</param>
    /// <param name="told">Told each wait, as it begins and while it lasts.</param>
    /// <param name="clock">Times the waits: a test's where it stands in for the clock.</param>
    /// <param name="cancellationToken">Stops it, in a wait.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> stopped it.</exception>
    internal static async Task ReplaceWithAsync(
        string source,
        string destination,
        Action<string, string> replace,
        Action<WaitTold> told,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replace);
        ArgumentNullException.ThrowIfNull(told);
        ArgumentNullException.ThrowIfNull(clock);

        Exception? refusal = null;
        var end = await Retrying.RunAsync(
            (_, _) => Task.FromResult(Tried()),
            new RetryWatch($"the rename over '{destination}'", told, FirstReplaceWait, Bound: null, clock),
            cancellationToken).ConfigureAwait(false);

        if (end.Kind == RetryEndKind.Done)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ExceptionDispatchInfo.Throw(refusal ?? throw new InvalidOperationException($"the rename over '{destination}' ended {end.Kind} without a refusal"));

        RetryTry<bool> Tried()
        {
            try
            {
                replace(source, destination);
                return new RetryTry<bool>.Done(true);
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                refusal = refused;

                if (Directory.Exists(destination))
                {
                    return new RetryTry<bool>.Final(refused.Message);
                }

                return HolderOf(refused, source, destination) is { } held
                    ? new RetryTry<bool>.Passing($"another program holds '{held.Path}' ({held.Reason.TrimEnd('.')})", After: null)
                    : new RetryTry<bool>.Failed(refused.Message, $"{refused.GetType().Name} {refused.HResult}: {refused.Message} - {StateOf(source)} - {StateOf(destination)}");
            }
        }
    }

    /// <summary>
    /// The file another program holds that <paramref name="refused"/> - a refused rename of <paramref name="source"/> over
    /// <paramref name="destination"/> - was refused for, where one is: on Windows, the one a look finds held, or, where
    /// Windows said a sharing or a lock violation and the holder let go before the look, the destination in Windows' own
    /// words. None elsewhere, where a rename is never refused for another program's hold.
    /// </summary>
    private static HeldEntry? HolderOf(Exception refused, string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var full = (Source: Path.GetFullPath(source), Destination: Path.GetFullPath(destination));
        var found = WindowsHolds.Held(full.Source, isDirectory: false) ?? WindowsHolds.Held(full.Destination, isDirectory: false);

        return found ?? (refused is IOException io && WindowsHolds.HeldBy(io) ? new HeldEntry(full.Destination, refused.Message) : null);
    }

    /// <summary>
    /// What a refused rename met at <paramref name="path"/>, which tells one refusal's state from another's: its length and
    /// its last write where a file stands there, else that none does.
    /// </summary>
    private static string StateOf(string path)
    {
        try
        {
            var file = new FileInfo(path);

            return file.Exists
                ? $"{file.Length.ToString(CultureInfo.InvariantCulture)} bytes written at {file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}"
                : "no file";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"unread ({ex.Message})";
        }
    }

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="destination"/> once: on Windows as POSIX does, so a file a
    /// reader holds open is replaced all the same and the reader goes on reading the old one, as on every other system -
    /// a record is streamed while it is read, and a writer may replace it meanwhile - and Windows' own way where the volume
    /// cannot (<see cref="Win32Files.TryReplace(string, string)"/>). A refusal is raised at once; the wait is the caller's.
    /// </summary>
    internal static void Replace(string source, string destination)
    {
        if (OperatingSystem.IsWindows() && Win32Files.TryReplace(Path.GetFullPath(source), Path.GetFullPath(destination)))
        {
            return;
        }

        File.Move(source, destination, overwrite: true);
    }

    /// <summary>
    /// Removes a temporary file. A cleanup failure must never replace the failure that
    /// caused it: losing "there is not enough space on the disk" behind "cannot access
    /// the temporary file" sends the reader after the wrong problem entirely.
    /// </summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

}
