using DssHarness.Core.Configuration;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The source of a remote workflow, as a step's <c>use:</c> names it: a workflow file in a GitHub repository, at a
/// ref - <c>owner/repo/path/workflow.yml@ref</c>, or the same under a GitHub Enterprise host the configuration names.
/// The one owner of the spelling, and of the name a resolved remote workflow goes by inside the run.
/// </summary>
/// <param name="Host">The repository's host: <c>github.com</c>, or a GitHub Enterprise host the configuration names.</param>
/// <param name="Owner">The repository's owner.</param>
/// <param name="Repo">The repository's name.</param>
/// <param name="File">The workflow file's path in the repository, forward slashes, ending in the workflow extension.</param>
/// <param name="Ref">The ref the file is read at: a full 40-hex commit SHA, a tag, or a branch where the configuration allows one.</param>
public sealed record WorkflowRemoteSource(string Host, string Owner, string Repo, string File, string Ref)
{
    /// <summary>The workflow's directory within its repository - the files its paths name are read under it.</summary>
    public string Directory => File[..File.LastIndexOf('/')];

    /// <summary>The repository the source names, as a refusal or a line says it.</summary>
    public string Repository => $"{Host}/{Owner}/{Repo}";

    /// <summary>The source as it was written, the file's path and its ref apart: github.com's without its host, which none wrote.</summary>
    public string Spelled => Host.Equals(WorkflowSourceRules.GitHub, StringComparison.OrdinalIgnoreCase)
        ? $"{Owner}/{Repo}/{File}@{Ref}"
        : $"{Host}/{Owner}/{Repo}/{File}@{Ref}";

    /// <summary>Whether <paramref name="ref"/> is a full commit SHA, which is its own commit and resolves nowhere.</summary>
    /// <param name="ref">A ref as written after <c>@</c>.</param>
    public static bool IsCommit(string @ref)
        => @ref.Length == 40 && @ref.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

/// <summary>
/// The one owner of what a remote workflow's name is inside a run: the reserved place under <c>remote/</c> that its
/// files are read by, carried on every step of it as a local workflow's directory path is. A local use that resolves
/// under <c>remote/</c> is refused, so no local workflow can pass as a cached one.
/// </summary>
public static class WorkflowRemote
{
    /// <summary>The first segment of every remote workflow's name, reserved to the cache's own place.</summary>
    public const string Prefix = "remote";

    /// <summary>
    /// The remote source <paramref name="source"/> names, or null where it names none: it carries an <c>@</c>, and at
    /// least an owner, a repository and a workflow file's path before it - each segment named, the file's path
    /// relative and inside the repository, and a host only where the first of four or more segments is dotted, for a
    /// GitHub owner is never dotted and a host always is.
    /// </summary>
    /// <param name="source">The value of a step's <c>use:</c>.</param>
    public static WorkflowRemoteSource? Source(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (source.Contains('\\', StringComparison.Ordinal) || source.Contains(':', StringComparison.Ordinal)
            || source.StartsWith('/') || source.StartsWith("./", StringComparison.Ordinal) || source.StartsWith("../", StringComparison.Ordinal))
        {
            return null;
        }

        var at = source.LastIndexOf('@');

        if (at < 1 || at == source.Length - 1 || source.IndexOf('@') != at)
        {
            return null;
        }

        var parts = source[..at].Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3)
        {
            return null;
        }

        var host = WorkflowSourceRules.GitHub;
        var first = 0;

        if (parts[0].Contains('.', StringComparison.Ordinal))
        {
            host = parts[0];
            first = 1;

            if (parts.Length < 4)
            {
                return null;
            }
        }

        var (owner, repo) = (parts[first], parts[first + 1]);
        var file = string.Join("/", parts[(first + 2)..]);

        if (owner.Contains('.', StringComparison.Ordinal) || repo.Contains('.', StringComparison.Ordinal)
            || parts.Any(part => part.Length == 0 || part is "." or ".." || part == "*")
            || !file.EndsWith(HarnessLayout.WorkflowFileExtension, StringComparison.Ordinal)
            || file.Split('/').Length < 2)
        {
            return null;
        }

        return new WorkflowRemoteSource(host, owner, repo, file, source[(at + 1)..]);
    }

    /// <summary>
    /// The name a remote workflow at <paramref name="commit"/> goes by inside a run: the reserved <c>remote/</c>
    /// place, the repository and the commit under it, and the workflow's directory in the repository under them - one
    /// name for one workflow at one commit, carried on its steps and never colliding with a local workflow's own.
    /// </summary>
    /// <param name="source">The remote source as parsed.</param>
    /// <param name="commit">The commit the source resolved to, 40 hex characters.</param>
    public static string Name(WorkflowRemoteSource source, string commit)
    {
        ArgumentNullException.ThrowIfNull(source);

        return string.Join("/", Prefix, source.Host, source.Owner, source.Repo, commit, source.Directory);
    }

    /// <summary>Whether <paramref name="workflow"/> is the name of a workflow resolved from a remote source.</summary>
    /// <param name="workflow">A workflow's name, as a step's files are read by.</param>
    public static bool Is(string workflow)
        => workflow.StartsWith($"{Prefix}/", StringComparison.Ordinal);

    /// <summary>
    /// The parts a remote workflow's name carries - its host, owner, repository, commit and directory in the
    /// repository - or null where <paramref name="workflow"/> is not a remote workflow's name.
    /// </summary>
    /// <param name="workflow">A remote workflow's name, as <see cref="Name(WorkflowRemoteSource, string)"/> wrote it.</param>
    public static (string Host, string Owner, string Repo, string Commit, string Directory)? PartsOf(string workflow)
    {
        if (!Is(workflow))
        {
            return null;
        }

        var parts = workflow.Split('/');

        return parts.Length >= 6 && WorkflowRemoteSource.IsCommit(parts[4])
            ? (parts[1], parts[2], parts[3], parts[4], string.Join("/", parts[5..]))
            : null;
    }
}
