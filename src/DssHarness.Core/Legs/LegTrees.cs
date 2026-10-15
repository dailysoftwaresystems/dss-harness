using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Hosts;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Core.Legs;

/// <summary>Where the legs this machine runs itself work, for one run.</summary>
public enum LocalTrees
{
    /// <summary>Each as its configuration places it: the leg's own key, else the repository's default, else its tree.</summary>
    AsConfigured,

    /// <summary>In its tree, whatever the configuration says.</summary>
    InPlace,

    /// <summary>In a copy of its tree, whatever the configuration says.</summary>
    Copy,
}

/// <summary>The tree a leg acts on here, and the tree its work happens in on the machine that runs it.</summary>
/// <param name="Here">The tree the leg acts on here: the worktree it names, or the tree the command was typed in.</param>
/// <param name="Work">The tree the work happens in, on the machine it happens on.</param>
/// <param name="LocalCopy">Whether <paramref name="Work"/> is a copy of <paramref name="Here"/> kept in its .legs on this machine.</param>
public sealed record LegTree(string Here, string Work, bool LocalCopy);

/// <summary>The tree a leg acts on here, and the tree its work happens in on the machine that runs it.</summary>
/// <remarks>
/// One answer for placing a leg and for asking a host about it beforehand, so the build directory a host is
/// asked about is the one the leg then builds in.
/// </remarks>
public static class LegTrees
{
    /// <summary>
    /// Whether the configuration has <paramref name="leg"/> work in a copy of its tree where this machine runs it: its own
    /// key, else the repository's default. The one expression of the configuration's half of that order of precedence,
    /// which nothing else repeats.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg.</param>
    private static bool Configured(HarnessConfig config, LegConfig leg) => leg.LocalCopy ?? config.Defaults.LocalCopy;

    /// <summary>
    /// Whether <paramref name="leg"/>, where this machine runs it, works in a copy of its tree kept in the tree's own .legs:
    /// never where that tree is itself a copy, and otherwise as <paramref name="local"/> says, the configuration deciding
    /// where it leaves that open. The one expression of that rule, which placing a leg and refusing a runner that would
    /// write in its copy both ask, so neither can find a copy the other does not.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="here">
    /// The host this machine is to the machine that dispatched the leg here, or <see langword="null"/> where this machine
    /// placed it itself.
    /// </param>
    /// <param name="local">Where this run's own legs work.</param>
    public static bool InLocalCopy(HarnessContext context, LegConfig leg, HostId? here, LocalTrees local)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(leg);

        // Never a copy of a copy: a host running a leg another machine sent it works in the copy it was sent, which is
        // that machine's to replace, and nothing of the harness's is kept beside a tree anyway.
        return HostCopies.KeptAs(here) is null
            && !context.IsSyncedCopy
            && (local == LocalTrees.Copy || local == LocalTrees.AsConfigured && Configured(context.Config, leg));
    }

    /// <summary>
    /// Where <paramref name="leg"/>'s work happens on <paramref name="host"/>: the tree itself, that machine's copy of it,
    /// or - where this machine runs the leg and <paramref name="local"/> puts it in one - the copy of its tree kept in the
    /// tree's own .legs (<see cref="LegCopies.PathOf"/>).
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="variant">The leg's variant, whose key names its copy.</param>
    /// <param name="host">The machine the work runs on.</param>
    /// <param name="here">
    /// The host this machine is to the machine that dispatched the leg here, or <see langword="null"/> where this machine
    /// placed it itself.
    /// </param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="local">Where this run's own legs work.</param>
    public static LegTree For(
        HarnessContext context,
        LegConfig leg,
        VariantKey variant,
        HostId host,
        HostId? here,
        StringComparison comparison,
        LocalTrees local)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(host);

        var tree = Here(context, leg, here);

        if (host.Kind != HostKind.Local)
        {
            return new LegTree(tree, On(context, host, tree, comparison), LocalCopy: false);
        }

        var copy = InLocalCopy(context, leg, here, local);

        return new LegTree(tree, copy ? LegCopies.PathOf(tree, variant) : tree, copy);
    }

    /// <summary>
    /// The tree <paramref name="leg"/> acts on here: the worktree it names, or the tree the command was typed in.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="here">The host this machine is to the machine that sent the leg here, or <see langword="null"/>.</param>
    /// <remarks>
    /// A leg naming a worktree acts on that tree; every path below derives from it, which is what keeps a
    /// worktree's build output out of the main checkout's build directory. On a host a leg was sent to
    /// (<see cref="HostCopies.KeptAs"/>), its tree is the copy it was sent to, which is that worktree's own: the
    /// worktree it names is on the machine that sent it, and nothing in the copy is at that path.
    /// </remarks>
    public static string Here(HarnessContext context, LegConfig leg, HostId? here)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(leg);

        return HostCopies.KeptAs(here) is null && leg.Worktree is { Length: > 0 } worktree
            ? context.Layout.WorktreePathUnder(context.Config.Worktrees.Root, worktree)
            : context.Layout.RepositoryRoot;
    }

    /// <summary>
    /// Where the work on <paramref name="treeRoot"/> happens on <paramref name="host"/>: the tree itself on this
    /// machine, and that machine's copy of it - the main checkout's, or a worktree's own beside it - on any other.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="host">The machine the work runs on.</param>
    /// <param name="treeRoot">The tree here.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <exception cref="HarnessException">The host declares nowhere to keep a copy.</exception>
    public static string On(HarnessContext context, HostId host, string treeRoot, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);

        return host.Kind == HostKind.Local
            ? treeRoot
            : HostCopies.Of(context.Config, host, context.Layout, treeRoot, comparison);
    }
}
