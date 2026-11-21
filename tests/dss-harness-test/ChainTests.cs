using System.Globalization;
using DssHarness.Core.Walks;

namespace DssHarness.Tests;

/// <summary>
/// A chain followed to its end: its own end, or its first revisit, which names the link that closed it - never a count
/// of links, so a long chain is followed whole and a loop ends where it closes.
/// </summary>
public sealed class ChainTests
{
    /// <summary>
    /// A chain that loops back ends at the link that leads to a member followed before, naming it, every member kept once:
    /// the members' keys are kept, so the loop is never followed round again - the step throws past a thousand calls,
    /// which only a chain that does not know its members would reach.
    /// </summary>
    [Fact]
    public void ALoopEndsAtItsFirstRevisit_NamingIt()
    {
        var calls = 0;

        var end = Chain.Follow(
            "m0",
            member =>
            {
                if (++calls > 1000)
                {
                    throw new InvalidOperationException("the loop was followed round again");
                }

                return ChainNext<string>.To(member == "m4" ? "m2" : $"m{int.Parse(member[1..], CultureInfo.InvariantCulture) + 1}");
            },
            member => member,
            StringComparer.Ordinal);

        Assert.Equal(["m0", "m1", "m2", "m3", "m4"], end.Members);
        Assert.Equal(("m2", true), (end.RepeatedAt, end.Cycled));
        Assert.Equal(5, calls);
    }

    /// <summary>A chain of a thousand links is followed to its own end, every member kept, and nothing named as repeated.</summary>
    [Fact]
    public void AThousandLinkChain_IsFollowedWhole()
    {
        var end = Chain.Follow(0, member => member < 999 ? ChainNext<int>.To(member + 1) : ChainNext<int>.End, member => member);

        Assert.Equal(Enumerable.Range(0, 1000), end.Members);
        Assert.False(end.Cycled);
        Assert.Equal(0, end.RepeatedAt);
    }

    /// <summary>
    /// A chain followed through a step that waits is told, as each member joins it, how many it holds - the start's 1
    /// first - and ends at its own end, as one that does not wait does; and a loop ends at its first revisit there too.
    /// </summary>
    [Fact]
    public async Task FollowAsync_TellsEachCountAsTheChainGrows_AndEndsWhereFollowDoes()
    {
        var counts = new List<int>();

        var ended = await Chain.FollowAsync(
            "a",
            (member, _) => Task.FromResult(member switch
            {
                "a" => ChainNext<string>.To("b"),
                "b" => ChainNext<string>.To("c"),
                "c" => ChainNext<string>.To("d"),
                _ => ChainNext<string>.End,
            }),
            member => member,
            counts.Add,
            TestContext.Current.CancellationToken);

        var calls = 0;
        var looped = await Chain.FollowAsync(
            "a",
            (member, _) => ++calls > 1000 ? throw new InvalidOperationException("the loop was followed round again") : Task.FromResult(ChainNext<string>.To(member == "a" ? "b" : "a")),
            member => member,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b", "c", "d"], ended.Members);
        Assert.Null(ended.RepeatedAt);
        Assert.False(ended.Cycled);
        Assert.Equal([1, 2, 3, 4], counts);
        Assert.Equal(["a", "b"], looped.Members);
        Assert.Equal(("a", true), (looped.RepeatedAt, looped.Cycled));
        Assert.Equal(2, calls);
    }

    /// <summary>
    /// A chain that never ends is stopped by its token before the next link is asked for: cancelled as the third member
    /// joins, it asks no link after it - the step throws past a thousand calls, which only a following that ignored the
    /// token would reach.
    /// </summary>
    [Fact]
    public async Task FollowAsync_IsCancelledBeforeTheNextLink()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var counts = new List<int>();
        var calls = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => Chain.FollowAsync(
            0,
            (member, _) => ++calls > 1000 ? throw new InvalidOperationException("the token was not looked at") : Task.FromResult(ChainNext<int>.To(member + 1)),
            member => member,
            count =>
            {
                counts.Add(count);

                if (count == 3)
                {
                    cancellation.Cancel();
                }
            },
            cancellation.Token));

        Assert.Equal([1, 2, 3], counts);
        Assert.Equal(2, calls);
    }
}
