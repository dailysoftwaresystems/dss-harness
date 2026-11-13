using System.ComponentModel;

namespace DssHarness.Core.Configuration;

/// <summary>
/// Where the workflows a repository uses as steps may come from beyond its own directory: the GitHub owners and
/// repositories a <c>use:</c> may name, the GitHub Enterprise hosts it may reach with their credentials, the refs a
/// remote use may name, and what happens where the network is not there. Trust is a person's decision, never a
/// response's: a remote workflow directs agents and machine steps in the calling repository, so it is allowed here
/// or it is refused.
/// </summary>
/// <remarks>
/// Left out, every key holds its default: no source allowed, so a remote <c>use:</c> is refused until the repository
/// allows its owner; a tag or a full commit SHA, but never a branch; and no offline fallback, so a ref that cannot be
/// resolved refuses the run.
/// </remarks>
public sealed class WorkflowSourcesConfig
{
    /// <summary>
    /// The owners and repositories a remote <c>use:</c> may name: <c>owner</c>, <c>owner/*</c> or <c>owner/repo</c>,
    /// and - for a host beyond github.com - the same under <c>host/</c>. Empty by default, so a remote <c>use:</c> is
    /// refused until a repository allows its source.
    /// </summary>
    [Description("the GitHub owners and repositories a remote use may name: owner, owner/* or owner/repo, under host/ for another host; empty allows none")]
    public List<string> Allow { get; init; } = [];

    /// <summary>
    /// The GitHub Enterprise hosts a <c>use:</c> may reach, each with the credential its API is called with; github.com
    /// needs no entry, for the machine's own <c>gh</c> login reaches it.
    /// </summary>
    public Dictionary<string, WorkflowHostConfig> Hosts { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The refs a remote <c>use:</c> may name: <c>tag-or-commit</c> (the default) takes a tag or a full commit SHA,
    /// <c>commit</c> refuses tags too, and <c>any</c> allows a branch, which moves, warned of at every run that uses one.
    /// </summary>
    [Description("the refs a remote use may name: tag-or-commit (the default), commit, or any, which allows a moving branch")]
    public string? Pin { get; init; }

    /// <summary>
    /// What happens where a tag or branch cannot be resolved offline: <c>refuse</c> (the default) refuses the run, and
    /// <c>last-resolved</c> uses the last resolution in the cache, which the run records that it did.
    /// </summary>
    [Description("what happens where a ref cannot be resolved offline: refuse (the default) or last-resolved")]
    public string? Offline { get; init; }
}

/// <summary>One GitHub Enterprise host a remote <c>use:</c> may reach, and the credential its API is called with.</summary>
public sealed class WorkflowHostConfig
{
    /// <summary>How the host is authenticated: an environment variable, named here.</summary>
    [Description("how the host is authenticated: an environment variable, named here")]
    public WorkflowHostCredentialConfig? Credential { get; init; }
}

/// <summary>The credential a GitHub Enterprise host is reached with: an environment variable, named here.</summary>
public sealed class WorkflowHostCredentialConfig
{
    /// <summary>Where the credential is read from: <c>env</c>.</summary>
    [Description("where the credential is read from: env")]
    public string From { get; init; } = "env";

    /// <summary>The environment variable's name.</summary>
    [Description("the environment variable's name")]
    public string? Name { get; init; }
}

/// <summary>The one owner of the rules a <c>workflowSources</c> section is held to, read wherever they are asked.</summary>
public static class WorkflowSourceRules
{
    /// <summary>The <c>pin</c> that takes a tag or a full commit SHA, and refuses a branch; the default.</summary>
    public const string PinTagOrCommit = "tag-or-commit";

    /// <summary>The <c>pin</c> that takes a full commit SHA only, refusing tags too.</summary>
    public const string PinCommit = "commit";

    /// <summary>The <c>pin</c> that allows a branch as well, which moves, warned of at every run that uses one.</summary>
    public const string PinAny = "any";

    /// <summary>Every <c>pin</c>, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Pins { get; } = [PinTagOrCommit, PinCommit, PinAny];

    /// <summary>The <c>offline</c> that refuses a run whose ref cannot be resolved; the default.</summary>
    public const string OfflineRefuse = "refuse";

    /// <summary>The <c>offline</c> that uses the cache's last resolution, which the run records that it did.</summary>
    public const string OfflineLastResolved = "last-resolved";

    /// <summary>Every <c>offline</c>, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Offlines { get; } = [OfflineRefuse, OfflineLastResolved];

    /// <summary>The host every remote source names where it names none: github.com, reached with the machine's own gh login.</summary>
    public const string GitHub = "github.com";

    /// <summary>The <c>pin</c> that holds: the file's, or <see cref="PinTagOrCommit"/> where it says none.</summary>
    /// <param name="sources">The section; null where the file holds none.</param>
    public static string PinOf(WorkflowSourcesConfig? sources) => sources?.Pin ?? PinTagOrCommit;

    /// <summary>The <c>offline</c> that holds: the file's, or <see cref="OfflineRefuse"/> where it says none.</summary>
    /// <param name="sources">The section; null where the file holds none.</param>
    public static string OfflineOf(WorkflowSourcesConfig? sources) => sources?.Offline ?? OfflineRefuse;

    /// <summary>
    /// Whether <paramref name="allowed"/> allows the repository <paramref name="owner"/>/<paramref name="repo"/> on
    /// <paramref name="host"/>: an entry names the owner whole, its every repository with <c>*</c>, or the one
    /// repository; github.com's entries are written without a host (an entry under <c>github.com/</c> reads the same),
    /// and another host's under its own name, so one host's allowance is never another's.
    /// </summary>
    /// <param name="allowed">The <c>allow</c> list.</param>
    /// <param name="host">The repository's host, <c>github.com</c> or a configured one.</param>
    /// <param name="owner">The repository's owner.</param>
    /// <param name="repo">The repository's name.</param>
    public static bool Allows(IReadOnlyList<string> allowed, string host, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var isGitHub = host.Equals(GitHub, StringComparison.OrdinalIgnoreCase);

        foreach (var entry in allowed)
        {
            var pattern = entry.Trim();

            if (isGitHub && pattern.StartsWith($"{GitHub}/", StringComparison.OrdinalIgnoreCase))
            {
                pattern = pattern[(GitHub.Length + 1)..];
            }
            else if (!isGitHub && !pattern.StartsWith($"{host}/", StringComparison.OrdinalIgnoreCase))
            {
                // Another host's entries are written under its own name; a bare owner or owner/repository names
                // github.com's, never another host's.
                continue;
            }
            else if (!isGitHub)
            {
                pattern = pattern[(host.Length + 1)..];
            }

            var parts = pattern.Split('/');

            if (parts is [var one] && one == owner)
            {
                return true;
            }

            if (parts is [var anOwner, var aRepo] && anOwner == owner && (aRepo == "*" || aRepo == repo))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every problem with one <c>allow</c> entry: that it is a whole owner, an owner's every repository or one
    /// repository, each segment named - never empty, and the star only where a repository is - and a host only under
    /// its own name, three segments deep; null where it is one of these.
    /// </summary>
    /// <param name="entry">The entry as written.</param>
    public static string? AllowProblem(string entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);

        var parts = entry.Split('/');

        if (parts.Length is < 1 or > 3 || parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            return $"allow names '{entry}', which is neither an owner, owner/*, owner/repository nor host/owner/repository: each segment is named, and only a repository may be *";
        }

        if (parts[..^1].Any(part => part == "*"))
        {
            return $"allow names '{entry}', which puts * where a name is: the star stands for the repositories of an owner, owner/*";
        }

        if (parts.Length == 3 && !parts[0].Contains('.', StringComparison.Ordinal))
        {
            return $"allow names '{entry}', whose first of three segments is no host: another host's entries are written under its own name, host/owner/repository";
        }

        return null;
    }
}
