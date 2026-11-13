using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A file a workflow's definition names - a prompt, a schema, a pull request's body - held to one rule wherever it is
/// read: spelt as a path inside the workflow's own directory, kept there once every link along it is followed, and there.
/// What a reviewer reads there is what the workflow runs, as an action's files are to its own directory. The definition's
/// check reads each file's problem here, and a run the text of one it needs as it runs. A workflow resolved from a remote
/// source keeps its files in the workflow cache, and they are read there under the same rule, their blobs hashed again
/// as they are.
/// </summary>
/// <param name="fileSystem">Follows links and reads the files.</param>
/// <param name="pathComparison">How this machine compares paths.</param>
/// <param name="cache">
/// The hash cache of remote workflows, where a remote workflow's files are read; null where the reader serves a
/// definition whose workflows are local only.
/// </param>
public sealed class WorkflowNamedFiles(IFileSystem fileSystem, StringComparison pathComparison, WorkflowCache? cache = null)
{
    /// <summary>The workflow cache of remote workflows' files; null where none is read here.</summary>
    private readonly WorkflowCache? _cache = cache;
    /// <summary>The workflow's directory, every link along it followed, as a file it names is held to it.</summary>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="name">The workflow.</param>
    public string Directory(HarnessLayout layout, string name)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return Path.TrimEndingDirectorySeparator(fileSystem.ResolveLinks(Path.GetFullPath(layout.WorkflowDirectory(name))));
    }

    /// <summary>
    /// The one problem with one file the definition names: that it is spelt as a path inside the workflow's own directory,
    /// that it stays there once every link along it is followed, and that it is there; null where it is.
    /// </summary>
    /// <param name="named">The file, as the definition names it.</param>
    /// <param name="directory">The workflow's directory, every link along it followed (<see cref="Directory"/>).</param>
    /// <param name="name">The workflow.</param>
    /// <param name="what">What names the file, as a problem says it.</param>
    /// <param name="path">The file with every link along it followed, where it could be resolved.</param>
    public string? Problem(string named, string directory, string name, string what, out string path)
        => Problem(named, directory, name, what, out path, out _);

    /// <summary>
    /// <see cref="Problem(string, string, string, string, out string)"/>, saying beside it whether the one problem is
    /// that the file is not there - looked for, inside the workflow's directory, and not found - never where it could not
    /// be looked at.
    /// </summary>
    /// <param name="named">The file, as the definition names it.</param>
    /// <param name="directory">The workflow's directory, every link along it followed (<see cref="Directory"/>).</param>
    /// <param name="name">The workflow.</param>
    /// <param name="what">What names the file, as a problem says it.</param>
    /// <param name="path">The file with every link along it followed, where it could be resolved.</param>
    /// <param name="missing">Whether the problem is that the file is not there.</param>
    private string? Problem(string named, string directory, string name, string what, out string path, out bool missing)
    {
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(directory);

        path = string.Empty;
        missing = false;
        var relative = $"{HarnessLayout.WorkflowDirectoryRelative(name)}/";

        // Rooted by every platform's rules, as an action's path is: a drive written on Linux would otherwise read as a
        // directory's name, and a definition would mean two things on two machines.
        if (PlatformPaths.IsRootedOnAnyPlatform(named))
        {
            return $"'{named}', {what}, is an absolute path; every file a workflow names is relative to '{relative}', as an action's are to its own directory";
        }

        if (named.Split('/', '\\').Any(segment => segment is ".." or "." or ""))
        {
            return $"'{named}', {what}, does not name a file inside the workflow's directory; every file a workflow names is relative to '{relative}', without '.', '..' or an empty part, as an action's are to its own directory";
        }

        try
        {
            path = fileSystem.ResolveLinks(Path.GetFullPath(Path.Combine(directory, named)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return $"'{named}', {what}, could not be resolved inside '{directory}': {exception.Message}";
        }

        if (!PathContainment.IsStrictlyInside(directory, path, pathComparison))
        {
            return $"'{named}', {what}, leads to '{path}', which is outside '{directory}'; every file a workflow names is relative to '{relative}', as an action's are to its own directory";
        }

        if (IsFile(path, out var unlooked))
        {
            return null;
        }

        missing = unlooked is null;

        return unlooked is null
            ? $"'{named}', {what}, is not at '{relative}{named.Replace('\\', '/')}'; write it there, beside the definition that names it"
            : $"'{named}', {what}, could not be looked at: {unlooked}";
    }

    /// <summary>
    /// The text of a file the definition names, as a run reads it where it needs it, held to <see cref="Problem(string, string, string, string, out string)"/>'s rule.
    /// </summary>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="name">The workflow.</param>
    /// <param name="named">The file, as the definition names it.</param>
    /// <param name="what">What names the file, as a refusal says it.</param>
    /// <exception cref="HarnessException">It breaks the rule, or could not be read (<see cref="HarnessExit.ConfigInvalid"/>), saying why.</exception>
    public string ReadText(HarnessLayout layout, string name, string named, string what)
        => Read(layout, name, named, what, missingIsNone: false)!;

    /// <summary>
    /// The text of a file the workflow may hold and owes none of, as a run reads it where it needs it: null where it is
    /// not there - looked for inside the workflow's directory and not found - and held to
    /// <see cref="Problem(string, string, string, string, out string)"/>'s rule otherwise, so a file that could not be
    /// looked at or read is never taken for one that is not there.
    /// </summary>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="name">The workflow.</param>
    /// <param name="named">The file, as the workflow's own folder names it.</param>
    /// <param name="what">What names the file, as a refusal says it.</param>
    /// <exception cref="HarnessException">It breaks the rule but for not being there, or could not be read (<see cref="HarnessExit.ConfigInvalid"/>), saying why.</exception>
    public string? ReadTextIfThere(HarnessLayout layout, string name, string named, string what)
        => Read(layout, name, named, what, missingIsNone: true);

    /// <summary>The text of a file the definition names, held to the rule; null where <paramref name="missingIsNone"/> and it is not there.</summary>
    /// <param name="layout">The repository's layout.</param>
    /// <param name="name">The workflow.</param>
    /// <param name="named">The file, as the definition names it.</param>
    /// <param name="what">What names the file, as a refusal says it.</param>
    /// <param name="missingIsNone">Whether a file that is not there answers null rather than a refusal.</param>
    private string? Read(HarnessLayout layout, string name, string named, string what, bool missingIsNone)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (WorkflowRemote.Is(name))
        {
            // A workflow resolved from a remote source: its files are the cache entry's, hashed again as they are
            // read, and no directory of the repository's own is asked for them.
            if (_cache?.FindNamed(name) is not { } entry)
            {
                return missingIsNone ? null : throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"'{named}', {what}, is of the workflow '{name}', which the workflow cache holds no entry for; fetch it again where a use names it");
            }

            try
            {
                return _cache.ReadText(entry, $"{entry.Path}/{named.Replace('\\', '/')}")
                    ?? (missingIsNone ? null : throw new HarnessException(
                        HarnessExit.ConfigInvalid,
                        $"'{named}', {what}, is not in the workflow '{name}': the commit it was fetched at holds no such file in its directory"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new HarnessException(HarnessExit.ConfigInvalid, $"'{named}', {what}, could not be read: {exception.Message}");
            }
        }

        string directory;

        try
        {
            directory = Directory(layout, name);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"'{named}', {what}, could not be read: the workflow's directory '{layout.WorkflowDirectory(name)}' could not be resolved: {exception.Message}");
        }

        if (Problem(named, directory, name, what, out var path, out var missing) is { } problem)
        {
            return missingIsNone && missing ? null : throw new HarnessException(HarnessExit.ConfigInvalid, problem);
        }

        try
        {
            return fileSystem.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"'{named}', {what}, could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// Whether a file is at <paramref name="path"/>; where none is, <paramref name="unlooked"/> says why the file system
    /// would not look, or is <see langword="null"/> where it looked and found nothing - a file that could not be looked
    /// at is never reported as missing, and the person is not sent to write one that may be there.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="unlooked">Why the file system would not look; null where it looked.</param>
    public bool IsFile(string path, out string? unlooked)
    {
        unlooked = null;

        if (fileSystem.FileExists(path))
        {
            return true;
        }

        // FileExists answers false where it could not look; the kind of what is there raises instead.
        try
        {
            _ = fileSystem.KindOf(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            unlooked = exception.Message.TrimEnd('.');
        }

        return false;
    }
}
