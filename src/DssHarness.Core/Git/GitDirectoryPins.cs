using System.Collections.Concurrent;

namespace DssHarness.Core.Git;

/// <summary>
/// The git directories of each tree an isolated agent works over, as git named them before the agent's launch: every later
/// git of this tool's own in that tree runs with them given (<c>GIT_DIR</c>, <c>GIT_COMMON_DIR</c>, <c>GIT_WORK_TREE</c>),
/// never with what the tree's own files say then - a worktree's <c>.git</c> file, a linked worktree's <c>commondir</c> and
/// <c>gitdir</c> - so a redirection written into any of them by a command leads nowhere. Held for this process's life;
/// the isolation seals those files too, so a later process reads them as they were.
/// </summary>
public sealed class GitDirectoryPins
{
    private readonly ConcurrentDictionary<string, GitDirectoryPin> _pins = new(StringComparer.Ordinal);

    /// <summary>Pins <paramref name="tree"/>'s git directories as git named them.</summary>
    /// <param name="tree">The tree's root, absolute.</param>
    /// <param name="gitDirectory">Its own git directory.</param>
    /// <param name="commonDirectory">The common directory it shares.</param>
    public void Pin(string tree, string gitDirectory, string commonDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commonDirectory);

        _pins[Key(tree)] = new GitDirectoryPin(Key(tree), gitDirectory, commonDirectory);
    }

    /// <summary>The pin of the tree whose root is <paramref name="directory"/>; null where none is pinned there.</summary>
    /// <param name="directory">The directory git is run in.</param>
    public GitDirectoryPin? Of(string directory)
        => string.IsNullOrWhiteSpace(directory) ? null : _pins.GetValueOrDefault(Key(directory));

    /// <summary>A directory as the pins are kept by: whole, with no separator at its end.</summary>
    private static string Key(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
}

/// <summary>One tree's git directories, pinned (<see cref="GitDirectoryPins"/>).</summary>
/// <param name="Tree">The tree's root.</param>
/// <param name="GitDirectory">Its own git directory.</param>
/// <param name="CommonDirectory">The common directory it shares.</param>
public sealed record GitDirectoryPin(string Tree, string GitDirectory, string CommonDirectory);
