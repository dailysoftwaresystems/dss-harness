using System.Text.RegularExpressions;
using DssHarness.Core.Results;

namespace DssHarness.Core.Git;

/// <summary>
/// The branch a remote names its default, as one read of it: what the remote's own HEAD names where it names a branch
/// here, else the first of its conventional branches that resolves.
/// </summary>
/// <param name="Reference">The default branch, under the remote (<c>origin/main</c>); null where none resolves here.</param>
/// <param name="Named">What the remote's own HEAD names, where it names a branch at all - here or not; null where it names none.</param>
public sealed record GitDefaultBranch(string? Reference, string? Named);

/// <summary>The repository on a forge a remote's address names (<see cref="GitRemotes.ForgeAddress"/>).</summary>
/// <param name="Host">The host, as the address names it - with the web address's port, where it gives one.</param>
/// <param name="Owner">Who owns the repository on the forge.</param>
/// <param name="Name">The repository's name, <c>.git</c> left out.</param>
/// <param name="OverSsh">
/// Whether the address reaches the host over ssh, whose own configuration may name the host otherwise - an alias for one
/// of several accounts (<see cref="SshHostNames"/>).
/// </param>
public sealed record GitForgeAddress(string Host, string Owner, string Name, bool OverSsh)
{
    /// <summary>The repository as the forge's tool takes it: <c>host/owner/name</c>.</summary>
    public string Repository => $"{Host}/{Owner}/{Name}";
}

/// <summary>
/// What a repository's remotes say: their names, the one a branch tracks, the branch each names its default, the address
/// each fetches from, and the repository on a forge that address names.
/// </summary>
public static partial class GitRemotes
{
    /// <summary>The branch names tried, in order, where a remote's own HEAD names no branch here.</summary>
    public static IReadOnlyList<string> ConventionalBranches { get; } = ["main", "master"];

    /// <summary>The remote looked for where nothing names one: the one git's own clone names.</summary>
    public const string ConventionalRemote = "origin";

    /// <summary>The address <paramref name="remote"/> fetches from, as git's configuration says it.</summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="remote">The remote's name.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">Git could not read it (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public static async Task<string> UrlAsync(this IGitClient gitClient, string directory, string remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);

        var read = await gitClient.RunAsync(directory, ["remote", "get-url", remote], cancellationToken: cancellationToken).ConfigureAwait(false);

        return read.Succeeded && read.StandardOutput.Trim() is { Length: > 0 } url
            ? url
            : throw new HarnessException(HarnessExit.CommandFailed, $"git could not read the address of remote '{remote}': {read.FailureMessage}");
    }

    /// <summary>
    /// The repository on a forge an address names: its host, owner and name, of <c>https://host/owner/name.git</c>,
    /// <c>ssh://user@host:port/owner/name</c> and <c>user@host:owner/name.git</c> alike - the user left out, and the port
    /// but where the address is the forge's web one, whose port is its own. Null where the address names none: a path on
    /// this machine, a <c>file://</c> address, an absolute path on an ssh server, one of more or fewer parts than an owner
    /// and a name.
    /// </summary>
    /// <param name="url">The remote's address.</param>
    public static GitForgeAddress? ForgeAddress(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        string authority;
        string path;
        bool overSsh;
        bool web;
        var scheme = url.IndexOf("://", StringComparison.Ordinal);

        if (scheme > 0)
        {
            if (url[..scheme] is not ("https" or "http" or "ssh" or "git"))
            {
                return null;
            }

            var rest = url[(scheme + 3)..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);

            if (slash <= 0)
            {
                return null;
            }

            authority = rest[..slash];
            path = rest[(slash + 1)..];
            overSsh = url[..scheme] == "ssh";
            web = url[..scheme] is "https" or "http";
        }
        else
        {
            // The scp-like form, [user@]host:path, as git reads it: a colon before any slash, and no drive letter (C:\x).
            var colon = url.IndexOf(':', StringComparison.Ordinal);

            if (colon <= 1 || url.IndexOf('/', StringComparison.Ordinal) is var slash && slash >= 0 && slash < colon)
            {
                return null;
            }

            authority = url[..colon];
            path = url[(colon + 1)..];
            overSsh = true;
            web = false;

            // A path from the server's root is a repository on that machine, no owner and name on a forge.
            if (path.StartsWith('/'))
            {
                return null;
            }
        }

        var parts = path.Trim('/').Split('/');

        if (Authority().Match(authority) is not { Success: true } reached || parts.Length != 2 || parts.Any(part => part.Length == 0))
        {
            return null;
        }

        var host = reached.Groups["host"].Value;
        var port = reached.Groups["port"].Value;

        var name = parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1];

        return name.Length == 0 ? null : new GitForgeAddress(web && port.Length > 0 ? $"{host}:{port}" : host, parts[0], name, overSsh);
    }

    /// <summary>An address's authority: a user up to its last '@', the host - an IPv6 one in brackets - and a port after a colon.</summary>
    [GeneratedRegex(@"^(?:.*@)?(?<host>\[[^\]]*\]|[^@:\[\]]+)(?::(?<port>[0-9]*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Authority();

    /// <summary>The names of the repository's remotes, as git lists them.</summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">Git could not list them (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public static async Task<IReadOnlyList<string>> ListRemotesAsync(this IGitClient gitClient, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);

        var listed = await gitClient.RunAsync(directory, ["remote"], cancellationToken: cancellationToken).ConfigureAwait(false);

        return listed.Succeeded
            ? listed.OutputLines
            : throw new HarnessException(HarnessExit.CommandFailed, $"git could not list the repository's remotes: {listed.StandardError.Trim()}");
    }

    /// <summary>
    /// The remote <paramref name="branch"/> tracks, or <see langword="null"/> where it tracks none - where git says the
    /// key is not set (its code 1), and only there: a configuration git could not read is never read as tracking none.
    /// </summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="branch">The local branch.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">Git could not read the configuration (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public static async Task<string?> TrackedRemoteAsync(this IGitClient gitClient, string directory, string branch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var tracked = await gitClient.RunAsync(directory, ["config", "--get", $"branch.{branch}.remote"], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!tracked.Succeeded && (tracked.TimedOut || tracked.ExitCode != 1))
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"git could not read the remote branch '{branch}' tracks: {tracked.StandardError.Trim()}");
        }

        var remote = tracked.Succeeded ? tracked.StandardOutput.Trim() : string.Empty;

        return remote.Length > 0 ? remote : null;
    }

    /// <summary>
    /// The branch <paramref name="remote"/> names its default: what its own HEAD names, where that is a branch here, and
    /// failing that its <see cref="ConventionalBranches"/>, each tried until one resolves. A remote's HEAD still naming a
    /// branch since renamed - as after master became main - names none here, and is said in <see cref="GitDefaultBranch.Named"/>.
    /// </summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="remote">The remote's name.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    public static async Task<GitDefaultBranch> DefaultBranchAsync(this IGitClient gitClient, string directory, string remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);

        var declared = await gitClient
            .RunAsync(directory, ["symbolic-ref", "--short", $"refs/remotes/{remote}/HEAD"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var named = declared.Succeeded ? declared.StandardOutput.Trim() : string.Empty;

        if (named.Length > 0 && await gitClient.ResolveCommitAsync(directory, named, cancellationToken).ConfigureAwait(false) is not null)
        {
            return new GitDefaultBranch(named, named);
        }

        foreach (var conventional in ConventionalBranches)
        {
            var candidate = $"{remote}/{conventional}";

            if (await gitClient.ResolveCommitAsync(directory, candidate, cancellationToken).ConfigureAwait(false) is not null)
            {
                return new GitDefaultBranch(candidate, named.Length > 0 ? named : null);
            }
        }

        return new GitDefaultBranch(null, named.Length > 0 ? named : null);
    }
}
