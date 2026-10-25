namespace DssHarness.Core.Git;

/// <summary>
/// The files of a work tree as a person editing it calls them: those git tracks and those it would add, its ignore rules
/// applied. The one spelling of that listing, which a citation check of the current tree, init's reading of the
/// repository's runner actions and workflows, and an agent's file tools all ask for.
/// </summary>
public static class GitTreeFiles
{
    /// <summary>
    /// The files git tracks and would add under <paramref name="directory"/> - untracked ones its ignore rules leave in -
    /// relative to it, as git lists them; those under <paramref name="under"/> alone, where it is given.
    /// </summary>
    /// <param name="gitClient">Git.</param>
    /// <param name="directory">The directory git lists, inside a work tree.</param>
    /// <param name="under">A path below the directory the listing is held to; null for the whole directory.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="Results.HarnessException">git could not list them.</exception>
    public static Task<IReadOnlyList<GitName>> ListTreeFilesAsync(
        this IGitClient gitClient,
        string directory,
        string? under = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);

        IReadOnlyList<string> arguments = ["ls-files", "-z", "--cached", "--others", "--exclude-standard"];

        return gitClient.ListNamesAsync(directory, under is null ? arguments : [.. arguments, "--", under], cancellationToken);
    }
}
