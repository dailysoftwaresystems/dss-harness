using System.Globalization;
using DssHarness.Core.Walks;

namespace DssHarness.Tests;

/// <summary>
/// The one walk of a graph: each node met once however many ways lead to it, deep first in the order a recursive walk
/// would meet them or by levels, lazily, and on a stack of its own, so no depth of the graph is recursed through.
/// </summary>
public sealed class WalkTests
{
    /// <summary>A diamond: a above b and c, which both lead to d, which leads to e.</summary>
    private static readonly Dictionary<string, string[]> Diamond = new(StringComparer.Ordinal)
    {
        ["a"] = ["b", "c"],
        ["b"] = ["d"],
        ["c"] = ["d"],
        ["d"] = ["e"],
        ["e"] = [],
    };

    /// <summary>
    /// A node two ways lead to is met once, and asked its next ones once: the walk keeps every key it has met, so the
    /// second way to d - and everything below d - is not walked again.
    /// </summary>
    [Fact]
    public void ADiamond_VisitsItsSharedNodeOnce()
    {
        var asked = new List<string>();

        var met = Walk.Visit(
            ["a"],
            node => node,
            node =>
            {
                asked.Add(node);
                return Diamond[node];
            },
            comparer: StringComparer.Ordinal).ToList();

        Assert.Equal(["a", "b", "d", "e", "c"], met);
        Assert.Equal(["a", "b", "d", "e", "c"], asked);
    }

    /// <summary>
    /// Deep first meets the nodes as a recursive walk would - a node, then all its first next node reaches, then its
    /// second's - and by levels meets every node one step from the start before any two steps away; a way back to the
    /// start, a cycle, is met as any node met before is, and ends there.
    /// </summary>
    [Fact]
    public void DeepFirstAndByLevels_EachMeetTheNodesInTheirOrder_AndACycleEnds()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["a"] = ["b", "c"],
            ["b"] = ["d", "e"],
            ["c"] = ["f", "a"],
            ["d"] = ["a"],
            ["e"] = [],
            ["f"] = [],
        };

        // Taken to a hundred, so a walk that went round the cycle fails here rather than running for ever.
        Assert.Equal(["a", "b", "d", "e", "c", "f"], Walk.Visit(["a"], node => node, node => graph[node], WalkOrder.Depth, StringComparer.Ordinal).Take(100));
        Assert.Equal(["a", "b", "c", "d", "e", "f"], Walk.Visit(["a"], node => node, node => graph[node], WalkOrder.Breadth, StringComparer.Ordinal).Take(100));
    }

    /// <summary>
    /// The walk is lazy: a node is asked its next ones only once the walk is asked on past it, so a caller that stops at
    /// the first two nodes asks only the first.
    /// </summary>
    [Fact]
    public void AWalk_AsksANodesNextOnesOnlyOnceItIsWalkedOnPastIt()
    {
        var asked = new List<string>();

        var firstTwo = Walk.Visit(
            ["a"],
            node => node,
            node =>
            {
                asked.Add(node);
                return Diamond[node];
            },
            comparer: StringComparer.Ordinal).Take(2).ToList();

        Assert.Equal(["a", "b"], firstTwo);
        Assert.Equal(["a"], asked);
    }

    /// <summary>
    /// A chain of a million nodes, each leading to the next, is walked to its end on a stack of a megabyte, which a walk
    /// recursing once a node would pass a few thousand nodes in.
    /// </summary>
    [Fact]
    public async Task AChainAMillionDeep_IsWalked()
    {
        Assert.Equal(new ChildRunAnswer(0, "1000000 999999", ""), await ChildRun.MethodAsync(typeof(WalkTests), nameof(ChainAMillionDeep), TestContext.Current.CancellationToken));
    }

    /// <summary>The child of <see cref="AChainAMillionDeep_IsWalked"/>: how many nodes the walk met, and the last.</summary>
    internal static string ChainAMillionDeep() => SmallStack.Run(() =>
    {
        var met = Walk.Visit([0], node => node, node => node < 999_999 ? [node + 1] : []).ToList();

        return string.Create(CultureInfo.InvariantCulture, $"{met.Count} {met[^1]}");
    });
}
