namespace DssHarness.Core.Git;

/// <summary>
/// Whether git's own work reaches the user's SSH agent: a push to a remote over ssh, and a commit the repository signs with
/// an ssh key. The one rule of when a commit's or a push's hooks are given the agent's socket inside the operating system's
/// isolation - and of nothing else, so no other hook signs as the user.
/// </summary>
public static class GitSsh
{
    /// <summary>
    /// Whether git reaches <paramref name="url"/> over ssh, as git's own transport reads an address: <c>ssh://</c>,
    /// <c>git+ssh://</c> and <c>ssh+git://</c>, and the scp-like <c>[user@]host:path</c> - an address with no
    /// <c>://</c> whose first <c>:</c> comes before any <c>/</c>, and which is no Windows drive.
    /// </summary>
    /// <param name="url">The address.</param>
    public static bool IsSshAddress(string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var address = url.Trim();

        if (address.IndexOf("://", StringComparison.Ordinal) is var scheme and >= 0)
        {
            return address[..scheme] is "ssh" or "git+ssh" or "ssh+git";
        }

        var colon = address.IndexOf(':');
        var slash = address.IndexOfAny(['/', '\\']);
        var drive = colon == 1 && char.IsAsciiLetter(address[0]) && address.Length > 2 && address[2] is '/' or '\\';

        return colon > 0 && (slash < 0 || colon < slash) && !drive;
    }

    /// <summary>
    /// Whether a push from <paramref name="tree"/> to <paramref name="remote"/> goes over ssh: any address git pushes it to
    /// (<c>git remote get-url --push</c>, every rewrite applied), or the remote itself where it is an address.
    /// </summary>
    /// <param name="git">Asks git.</param>
    /// <param name="tree">The tree the push is from.</param>
    /// <param name="remote">The remote, by its name or its address.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public static async Task<bool> PushesOverSshAsync(IGitClient git, string tree, string remote, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(remote);

        var addresses = await git.RunAsync(tree, ["remote", "get-url", "--push", "--all", remote], cancellationToken: cancellationToken).ConfigureAwait(false);

        return addresses.Succeeded ? addresses.OutputLines.Any(IsSshAddress) : IsSshAddress(remote);
    }

    /// <summary>
    /// Whether a commit in <paramref name="tree"/> is signed with an ssh key, which ssh-keygen signs through the user's SSH
    /// agent: <c>commit.gpgSign</c> true and <c>gpg.format</c> <c>ssh</c>.
    /// </summary>
    /// <param name="git">Asks git.</param>
    /// <param name="tree">The tree the commit is made in.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public static async Task<bool> SignsCommitsWithSshAsync(IGitClient git, string tree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);

        var signs = await git.RunAsync(tree, ["config", "--type=bool", "--get", "commit.gpgsign"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var format = await git.RunAsync(tree, ["config", "--get", "gpg.format"], cancellationToken: cancellationToken).ConfigureAwait(false);

        return signs is { Succeeded: true, OutputLines: ["true", ..] } && format is { Succeeded: true, OutputLines: [var named, ..] } && named.Trim() == "ssh";
    }
}
