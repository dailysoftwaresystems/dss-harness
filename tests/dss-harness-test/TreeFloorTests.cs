using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The floors of a tree: what is never moved into another tree, and what an agent's own moves -
/// seed, fold, refresh, rebase - never move on top of that, which the tree's credential slots join.
/// </summary>
public sealed class TreeFloorTests
{
    /// <summary>
    /// The agent moves' floor is the tree's own floor plus every credential slot: a planted
    /// <c>.env</c> folded back could redirect a key, and the ignore block is a rule a repository can
    /// edit away. <see cref="TreeFloor.Of"/> itself stays without them, for the verbs that are not
    /// an agent's moves.
    /// </summary>
    [Fact]
    public void TheFloorOfAnAgentsMoves_HoldsTheTreesOwnFloor_AndEveryCredentialSlot()
    {
        const string worktreesRoot = "wt";

        var ofMoves = TreeFloor.OfAgentMoves(worktreesRoot);

        foreach (var path in TreeFloor.Of(worktreesRoot).Concat(HarnessLayout.SecretSlots))
        {
            Assert.Contains(path, ofMoves);
        }

        // One of each: the slots are distinct paths, whatever the floor already held.
        Assert.Equal(ofMoves.Count, ofMoves.Distinct(StringComparer.Ordinal).Count());

        // The tree's own floor is without them, for the verbs that are not an agent's moves: it
        // feeds what sync withholds, where a slot is already never transferred.
        foreach (var slot in HarnessLayout.SecretSlots)
        {
            Assert.DoesNotContain(slot, TreeFloor.Of(worktreesRoot));
        }

        // Every slot is covered as a floor covers: the path itself and everything beneath it.
        foreach (var slot in HarnessLayout.SecretSlots)
        {
            Assert.True(TreeFloor.Covers(ofMoves, slot), slot);
            Assert.True(TreeFloor.Covers(ofMoves, slot + "/keys.env"), slot);
        }
    }

    /// <summary>The five credential slots are spelled from the layout's own names, rooted under the harness directory.</summary>
    [Fact]
    public void TheCredentialSlots_AreTheFiveTheLayoutNames()
    {
        Assert.Equal(
        [
            ".harness-config/sshItems",
            ".harness-config/wslDistros",
            ".harness-config/runner/.env",
            ".harness-config/runner/.secrets",
            ".harness-config/workflows/.env",
        ],
        HarnessLayout.SecretSlots);
    }

    /// <summary>
    /// The tool's own state is one list from the layout's names: the root slots and what it keeps
    /// under the harness directory, so a place the layout gains joins it the day it is added.
    /// </summary>
    [Fact]
    public void TheStatePaths_AreTheRootSlots_AndTheStateUnderTheHarnessDirectory()
    {
        Assert.Equal(
        [
            .. HarnessLayout.RootSlots("wt"),
            ".harness-config/runs",
            ".harness-config/host-copies",
            ".harness-config/mutation-record",
            ".harness-config/deps",
            ".harness-config/lock.json",
            ".harness-config/synced-copy.json",
        ],
        HarnessLayout.StatePaths("wt"));
    }
}
