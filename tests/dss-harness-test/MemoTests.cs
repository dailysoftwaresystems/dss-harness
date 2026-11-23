using System.Globalization;
using DssHarness.Core.Walks;

namespace DssHarness.Tests;

/// <summary>
/// A memo of answers built from the answers of what each key depends on: each key answered once, its dependencies
/// first, a key met again while it is being answered given the cycle's answer, and no chain of dependencies recursed
/// through.
/// </summary>
public sealed class MemoTests
{
    /// <summary>
    /// A key two others depend on is answered once and asked what it depends on once; a key that depends, through
    /// another, on itself is of a cycle - it is given the cycle's answer where it is met again, it and the key that met
    /// it again keep that answer, and it is recorded as a cycle - while the keys they depend on outside the cycle keep
    /// what they were answered; and a key asked again later is given its first answer, nothing asked anew.
    /// </summary>
    [Fact]
    public void EachKeyIsAnsweredOnce_AndACycleByOnCycle()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["a"] = ["b", "c", "b"],
            ["b"] = ["d"],
            ["c"] = ["d", "a"],
            ["d"] = [],
        };
        var asked = new List<string>();
        var answered = new List<string>();
        var memo = new Memo<string, string>(
            key =>
            {
                asked.Add(key);
                return graph[key];
            },
            (key, answers) =>
            {
                answered.Add(key);
                return $"{key}({string.Join(',', answers)})";
            },
            key => $"cycle:{key}",
            StringComparer.Ordinal);

        Assert.Equal("cycle:a", memo.Of("a"));
        Assert.Equal(("cycle:c", "b(d())", "d()"), (memo.Of("c"), memo.Of("b"), memo.Of("d")));

        Assert.Equal(["d", "b", "c", "a"], answered);
        Assert.Equal(["a", "b", "d", "c"], asked);
        Assert.Equal(["a"], memo.Cycles);
    }

    /// <summary>
    /// A key is answered only once every key it depends on is, so the answers come in the order a post-order walk gives:
    /// dependencies before what depends on them, each dependency's own before it, in the order they are named.
    /// </summary>
    [Fact]
    public void DependenciesAreAnsweredBeforeWhatDependsOnThem()
    {
        var graph = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["top"] = ["x", "y"],
            ["x"] = ["z"],
            ["y"] = ["z", "w"],
            ["z"] = [],
            ["w"] = [],
        };
        var answered = new List<string>();
        var memo = new Memo<string, int>(
            key => graph[key],
            (key, answers) =>
            {
                answered.Add(key);
                return answers.Sum() + 1;
            },
            _ => 0);

        Assert.Equal(6, memo.Of("top"));
        Assert.Equal(["z", "x", "w", "y", "top"], answered);
        Assert.Empty(memo.Cycles);
    }

    /// <summary>
    /// Where answering throws, the keys it left open are closed unanswered, so a later question asks them afresh - none of
    /// them read as a cycle for having been open when the exception passed.
    /// </summary>
    [Fact]
    public void AnAnswerThatThrows_LeavesNoKeyOpen()
    {
        var failing = true;
        var memo = new Memo<string, string>(
            key => key == "top" ? ["below"] : [],
            (key, answers) => failing && key == "below" ? throw new InvalidOperationException("not yet") : $"{key}({string.Join(',', answers)})",
            key => $"cycle:{key}",
            StringComparer.Ordinal);

        Assert.Equal("not yet", Assert.Throws<InvalidOperationException>(() => memo.Of("top")).Message);

        failing = false;

        Assert.Equal("top(below())", memo.Of("top"));
        Assert.Empty(memo.Cycles);
    }

    /// <summary>
    /// A chain of a hundred thousand keys, each depending on the next, is answered on a stack of a megabyte, which a memo
    /// recursing once a dependency would pass a few thousand keys in.
    /// </summary>
    [Fact]
    public async Task AChainOfAHundredThousandDependencies_IsAnswered()
    {
        Assert.Equal(new ChildRunAnswer(0, "100000", ""), await ChildRun.MethodAsync(typeof(MemoTests), nameof(ChainOfAHundredThousandDependencies), TestContext.Current.CancellationToken));
    }

    /// <summary>The child of <see cref="AChainOfAHundredThousandDependencies_IsAnswered"/>: the answer of the chain's first key.</summary>
    internal static string ChainOfAHundredThousandDependencies() => SmallStack.Run(() =>
    {
        var memo = new Memo<int, int>(key => key < 99_999 ? [key + 1] : [], (_, answers) => answers.Sum() + 1, _ => 0);

        return memo.Of(0).ToString(CultureInfo.InvariantCulture);
    });
}
