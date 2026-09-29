using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Orchestration;

/// <summary>Where an agent's Claude transcripts were found.</summary>
/// <param name="Files">Each transcript found, as its path.</param>
/// <param name="Unread">The directories that could not be looked in, each with why: one of them may hold another.</param>
public sealed record TranscriptsFound(IReadOnlyList<string> Files, IReadOnlyList<string> Unread);

/// <summary>What copying an agent's Claude transcripts found and kept.</summary>
/// <param name="Kept">Each copy kept, as its path.</param>
/// <param name="Searched">Where transcripts were looked for.</param>
/// <param name="Unread">The directories that could not be looked in, each with why.</param>
/// <param name="Failed">Why a transcript that was found could not be kept; null where every one found was.</param>
public sealed record TranscriptsKept(IReadOnlyList<string> Kept, string Searched, IReadOnlyList<string> Unread, string? Failed);

/// <summary>
/// Finds and keeps an agent's Claude Code transcripts: a session's is <c>projects/&lt;project&gt;/&lt;session&gt;.jsonl</c> in
/// Claude Code's configuration directory - <c>CLAUDE_CONFIG_DIR</c>, or <c>.claude</c> in the home directory - and a
/// subagent's is <c>projects/&lt;project&gt;/&lt;parent session&gt;/subagents/agent-&lt;id&gt;.jsonl</c>, with its
/// <c>.meta.json</c> beside it.
/// </summary>
/// <remarks>
/// Found by file name alone, and copied as they are: Claude Code documents what the files hold as its own, changing between
/// versions, so nothing in them is read. A transcript not found is said, and stops nothing - the agent's work is in its
/// fold whether or not its transcript is kept - and so is a directory that could not be looked in. A transcript found and
/// not kept is a failure the caller hears, since it is what another session reads to take the agent's work over.
/// </remarks>
/// <param name="fileSystem">Looks for the transcripts, and copies them.</param>
/// <param name="platform">The home directory Claude Code's configuration is in by default.</param>
/// <param name="environment">Reads an environment variable; the process's own where none is given.</param>
public sealed class ClaudeTranscripts(IFileSystem fileSystem, IHostPlatform platform, Func<string, string?>? environment = null)
{
    /// <summary>The variable that moves Claude Code's configuration directory.</summary>
    public const string ConfigDirectoryVariable = "CLAUDE_CONFIG_DIR";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>Where Claude Code keeps its projects' transcripts on this machine, for this user.</summary>
    public string ProjectsDirectory
        => Path.Combine(_environment(ConfigDirectoryVariable) is { Length: > 0 } configured ? configured : Path.Combine(_platform.HomeDirectory, ".claude"), "projects");

    /// <summary>Every transcript of <paramref name="session"/>: a session's, in any project, and a subagent's, under any session.</summary>
    /// <param name="session">A session's id, or a subagent's.</param>
    public TranscriptsFound Find(string session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);

        var found = new List<string>();
        var unread = new List<string>();
        var projects = ProjectsDirectory;

        foreach (var project in Directories(projects, unread))
        {
            found.AddRange(Existing(unread, Path.Combine(project, session + ".jsonl")));

            foreach (var parent in Directories(project, unread))
            {
                var subagents = Path.Combine(parent, "subagents");
                found.AddRange(Existing(unread, Path.Combine(subagents, $"agent-{session}.jsonl"), Path.Combine(subagents, $"agent-{session}.meta.json")));
            }
        }

        return new TranscriptsFound(found, unread);
    }

    /// <summary>
    /// Copies every transcript of <paramref name="session"/> into <paramref name="destination"/>, each under its path in
    /// the projects directory, so two projects' transcripts of one session never meet, and reads each copy back.
    /// </summary>
    /// <param name="session">A session's id, or a subagent's.</param>
    /// <param name="destination">Where the copies go.</param>
    /// <param name="cancellationToken">Stops it before a copy is put in place.</param>
    public async Task<TranscriptsKept> CopyAsync(string session, string destination, CancellationToken cancellationToken)
    {
        var projects = ProjectsDirectory;
        var found = Find(session);
        var kept = new List<string>();

        foreach (var transcript in found.Files)
        {
            var copy = Path.Combine(destination, Path.GetRelativePath(projects, transcript));

            try
            {
                await VerifiedFileCopy.CopyAsync(_fileSystem, transcript, copy, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new TranscriptsKept(kept, projects, found.Unread, $"'{transcript}' could not be kept: {ex.Message.TrimEnd('.')}");
            }

            kept.Add(copy);
        }

        return new TranscriptsKept(kept, projects, found.Unread, null);
    }

    /// <summary>The directories under <paramref name="directory"/>, where it is one; one that cannot be looked in is noted in <paramref name="unread"/>.</summary>
    private IReadOnlyList<string> Directories(string directory, List<string> unread)
    {
        try
        {
            return _fileSystem.KindOf(directory) == PathKind.Directory ? [.. _fileSystem.EnumerateDirectories(directory).Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unread.Add($"'{directory}': {ex.Message.TrimEnd('.')}");
            return [];
        }
    }

    /// <summary>Those of <paramref name="paths"/> that are files; one that cannot be looked at is noted in <paramref name="unread"/>.</summary>
    private IEnumerable<string> Existing(List<string> unread, params string[] paths)
    {
        foreach (var path in paths)
        {
            PathKind kind;

            try
            {
                kind = _fileSystem.KindOf(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unread.Add($"'{path}': {ex.Message.TrimEnd('.')}");
                continue;
            }

            if (kind == PathKind.File)
            {
                yield return path;
            }
        }
    }
}
