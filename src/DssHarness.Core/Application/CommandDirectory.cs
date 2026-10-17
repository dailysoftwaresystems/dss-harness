using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Application;

/// <summary>
/// What resolving the directory a command acts on answered: the directory, absolute and there, or the problem with
/// what was asked - one of the two, never both and never neither.
/// </summary>
/// <param name="Directory">The directory to act on; <see langword="null"/> where the answer is a problem.</param>
/// <param name="Problem">Why the directory cannot be acted on; <see langword="null"/> where it can.</param>
public sealed record ResolvedDirectory(string? Directory, string? Problem)
{
    /// <summary>Whether the directory can be acted on.</summary>
    public bool Holds => Directory is not null;
}

/// <summary>
/// The directory a command acts on: the one it was asked for, or the current directory where it was asked for none.
/// </summary>
public static class CommandDirectory
{
    /// <summary>
    /// Resolves <paramref name="requested"/> to an absolute path and confirms it exists.
    /// </summary>
    /// <param name="requested">The directory as it was asked for; <see langword="null"/> where the command was given none.</param>
    /// <param name="current">
    /// Reads the process's current directory, which stands in where none was asked for and makes a relative one whole:
    /// asked only then, never where the directory asked for is whole already, so a shell standing in a directory since
    /// removed - whose current directory Linux and macOS cannot read - can still name another.
    /// </param>
    /// <param name="fileSystem">Says whether the directory is there.</param>
    /// <remarks>
    /// Done once, before a command's body runs. Without the existence check, the first service to start a child
    /// process in a missing directory fails with an error that names the wrong cause, and a relative path would reach
    /// every message as a fragment rather than the directory the user meant.
    /// </remarks>
    public static ResolvedDirectory Resolve(string? requested, Func<string> current, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fileSystem);

        // A directory asked for blank names none. Made whole against the current directory it would name that one, and
        // the command would act on a tree nobody asked for, where it has always been refused.
        if (requested is not null && string.IsNullOrWhiteSpace(requested))
        {
            return new(null, $"'{requested}' is not a usable directory path: it names no directory.");
        }

        string? here;

        try
        {
            here = requested is not null && Path.IsPathFullyQualified(requested) ? null : current();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A shell standing in a directory since removed: Linux and macOS cannot read it, and the command has nowhere
            // to stand but where it is told.
            return new(null, $"the current directory cannot be read ({ex.Message.TrimEnd('.')}); give a whole path with --directory.");
        }

        var given = requested ?? here!;

        try
        {
            // Relative against the current directory it was handed, not the process's own: the reader of that
            // directory is the seam, and what it answers is what "here" means here.
            var directory = here is null ? Path.GetFullPath(given) : Path.GetFullPath(given, here);

            return fileSystem.DirectoryExists(directory)
                ? new(directory, null)
                : new(null, $"Directory '{directory}' does not exist.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, $"'{given}' is not a usable directory path: {ex.Message}");
        }
    }
}
