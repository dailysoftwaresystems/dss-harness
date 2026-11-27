using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Repository;

/// <summary>
/// Where an engine session's trees are, as git placed them: each tree's layout - the tree and the main checkout it belongs
/// to (<see cref="RepositoryLocator"/>) - its git directory (<see cref="LocationAsync"/>), the directory its repository
/// keeps its objects in (<see cref="ObjectsAsync"/>) and, for an agent's repository, the mark it carries where its main
/// checkout lends it its objects (<see cref="LentMarkOf"/>), each found once in the session and kept for the rest of it
/// (<see cref="EngineSession"/>), for the run's main checkout and each agent's tree cannot move while the engine holds the
/// run. What a context's every load asks of git - the tree's top, the repository's worktrees, the main checkout's git
/// directory, an agent's repository's mark - and what the worktree services ask of where a tree keeps its things is then
/// asked once per tree, never at every layer a step's work passes through; the configuration is read where it always is
/// (<see cref="HarnessContextLoader"/>), for it is the tree's file.
/// </summary>
/// <remarks>
/// <para>
/// Only a tree's own root is kept: a directory inside a tree may become a tree of its own - a copy made in the tree's
/// <c>.legs</c> - and what was kept for it would answer for the outer tree after.
/// </para>
/// <para>
/// A tree the session makes, deletes or makes again is dropped (<see cref="Forget"/>) by the one owner of the making and
/// the deleting of trees (<see cref="Worktrees.WorktreeService"/>), so the next asking finds it anew.
/// </para>
/// <para>
/// A fact is served only while the root it was kept for still holds its own <c>.git</c>
/// (<see cref="WorktreeInspector.HoldsOwnGit"/>), looked at as it is served: a <c>.git</c> taken away by anything but that
/// owner - a person, another command - leaves a directory git answers for by whatever repository encloses it, and the
/// fact is dropped, so the asking finds what the directory is now, and finds it anew should a <c>.git</c> be put back.
/// </para>
/// </remarks>
public sealed class SessionTrees
{
    private readonly StringComparison _comparison;
    private readonly ConcurrentDictionary<string, HarnessLayout> _layouts;
    private readonly ConcurrentDictionary<string, GitLocation> _locations;
    private readonly ConcurrentDictionary<string, string> _objects;
    private readonly ConcurrentDictionary<string, AgentRepositoryMark> _agents;

    private SessionTrees(StringComparison comparison)
    {
        _comparison = comparison;
        var byRoot = StringComparer.FromComparison(comparison);
        _layouts = new ConcurrentDictionary<string, HarnessLayout>(byRoot);
        _locations = new ConcurrentDictionary<string, GitLocation>(byRoot);
        _objects = new ConcurrentDictionary<string, string>(byRoot);
        _agents = new ConcurrentDictionary<string, AgentRepositoryMark>(byRoot);
    }

    /// <summary>The session's own, the calling work's (<see cref="EngineSession.Of{T}"/>); null outside a session.</summary>
    /// <param name="comparison">How this machine compares paths.</param>
    public static SessionTrees? OfSession(StringComparison comparison)
        => EngineSession.Of(() => new SessionTrees(comparison));

    /// <summary>
    /// Drops everything the calling work's session knows of a tree at <paramref name="path"/> or below it: the tree there is
    /// being made, deleted or made again. Nothing outside a session.
    /// </summary>
    /// <param name="path">The tree's path.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    public static void Forget(string path, StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OfSession(comparison) is not { } known)
        {
            return;
        }

        known.Drop(known._layouts, path);
        known.Drop(known._locations, path);
        known.Drop(known._objects, path);
        known.Drop(known._agents, path);
    }

    /// <summary>
    /// The mark the tree at <paramref name="path"/> carries, where the session found it marked as an agent's repository
    /// and borrowing the objects of the main checkout its mark names (<see cref="KeepLentMark"/>) and the tree still holds
    /// its <c>.git</c>; null where it found none.
    /// </summary>
    /// <param name="path">The tree's root.</param>
    /// <param name="fileSystem">Looks for the tree's <c>.git</c>.</param>
    public AgentRepositoryMark? LentMarkOf(string path, IFileSystem fileSystem)
        => Served(_agents, path, fileSystem, out var mark) ? mark : null;

    /// <summary>
    /// Keeps that the tree at <paramref name="path"/> carries <paramref name="mark"/> and borrows the objects of the main
    /// checkout it names, as git answered the one that found it: the locator (<see cref="RepositoryLocator"/>) or the
    /// telling of an agent's repository (<see cref="Worktrees.AgentRepositories.IsAtAsync"/>), each of which then asks
    /// neither again of the tree.
    /// </summary>
    /// <param name="path">The tree's root.</param>
    /// <param name="mark">Its mark, as its configuration file holds it.</param>
    public void KeepLentMark(string path, AgentRepositoryMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);

        _agents[Whole(path)] = mark;
    }

    /// <summary>
    /// Where <paramref name="directory"/> sits in its repository and its git directory, as <paramref name="ask"/> asks git:
    /// the session's where it asked it of the same tree's root before and the root still holds its <c>.git</c>, else asked
    /// now - and kept where the directory is a tree's root (its prefix empty). Asked each time outside a session.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="fileSystem">Looks for the root's <c>.git</c>.</param>
    /// <param name="ask">Asks git (<see cref="IGitClient.GetLocationAsync"/>).</param>
    public static async Task<GitLocation?> LocationAsync(string directory, StringComparison comparison, IFileSystem fileSystem, Func<Task<GitLocation?>> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);

        var known = OfSession(comparison);

        if (known is not null && known.Served(known._locations, directory, fileSystem, out var held))
        {
            return held;
        }

        var location = await ask().ConfigureAwait(false);

        if (known is not null && location is { Prefix.Length: 0 })
        {
            known._locations[Whole(directory)] = location;
        }

        return location;
    }

    /// <summary>
    /// The object directory of the repository whose checkout is <paramref name="mainCheckout"/>, as <paramref name="ask"/>
    /// asks git: the session's where it asked it before and the checkout still holds its <c>.git</c>, else asked now and
    /// kept. Asked each time outside a session.
    /// </summary>
    /// <param name="mainCheckout">The checkout.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="fileSystem">Looks for the checkout's <c>.git</c>.</param>
    /// <param name="ask">Asks git (<see cref="AgentRepositoryMark.ObjectsOfAsync"/>).</param>
    public static async Task<string> ObjectsAsync(string mainCheckout, StringComparison comparison, IFileSystem fileSystem, Func<Task<string>> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);

        var known = OfSession(comparison);

        if (known is not null && known.Served(known._objects, mainCheckout, fileSystem, out var held))
        {
            return held;
        }

        var objects = await ask().ConfigureAwait(false);

        if (known is not null)
        {
            known._objects[Whole(mainCheckout)] = objects;
        }

        return objects;
    }

    /// <summary>
    /// Keeps <paramref name="objects"/> as the object directory of the repository whose checkout is
    /// <paramref name="mainCheckout"/>, as git spelled it to a question that asked it with others (<see cref="ObjectsAsync"/>).
    /// </summary>
    /// <param name="mainCheckout">The checkout.</param>
    /// <param name="objects">Its object directory, absolute.</param>
    public void KeepObjects(string mainCheckout, string objects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objects);

        _objects[Whole(mainCheckout)] = objects;
    }

    /// <summary>
    /// The layout of the tree whose root is <paramref name="directory"/>, where the session found it and the root still
    /// holds its <c>.git</c>; null where it did not.
    /// </summary>
    /// <param name="directory">The directory a location starts from.</param>
    /// <param name="fileSystem">Looks for the root's <c>.git</c>.</param>
    public HarnessLayout? Find(string directory, IFileSystem fileSystem)
        => Served(_layouts, directory, fileSystem, out var layout) ? layout : null;

    /// <summary>
    /// Whether the session found <paramref name="directory"/> to be a main checkout, and it still holds its <c>.git</c>: a
    /// tree whose own main checkout is itself - so no bare repository, and no agent's repository, which names another.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="fileSystem">Looks for the directory's <c>.git</c>.</param>
    public bool IsMainCheckout(string directory, IFileSystem fileSystem)
        => Find(directory, fileSystem) is { } layout && PathContainment.AreSame(layout.RepositoryRoot, layout.MainCheckoutRoot, _comparison);

    /// <summary>Keeps <paramref name="layout"/>, found from <paramref name="directory"/>, where that directory is the tree's own root.</summary>
    /// <param name="directory">The directory the location started from.</param>
    /// <param name="layout">What git placed there.</param>
    public void Keep(string directory, HarnessLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (PathContainment.AreSame(directory, layout.RepositoryRoot, _comparison))
        {
            _layouts[Whole(directory)] = layout;
        }
    }

    /// <summary>
    /// Whether <paramref name="facts"/> hold one for the root <paramref name="path"/> that can be served: kept, and the root
    /// still holding its own <c>.git</c>. One whose root no longer does is dropped - that one, never one kept for the root
    /// meanwhile - so the root is asked anew from here on.
    /// </summary>
    private bool Served<T>(ConcurrentDictionary<string, T> facts, string path, IFileSystem fileSystem, [MaybeNullWhen(false)] out T fact)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var root = Whole(path);

        if (!facts.TryGetValue(root, out fact))
        {
            return false;
        }

        if (WorktreeInspector.HoldsOwnGit(fileSystem, root))
        {
            return true;
        }

        _ = facts.TryRemove(new KeyValuePair<string, T>(root, fact));
        fact = default;
        return false;
    }

    private void Drop<T>(ConcurrentDictionary<string, T> facts, string path)
    {
        foreach (var root in facts.Keys)
        {
            if (PathContainment.IsInsideOrSame(path, root, _comparison))
            {
                _ = facts.TryRemove(root, out _);
            }
        }
    }

    private static string Whole(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
