using System.Globalization;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Tests;

/// <summary>
/// The scanned readers' walks on a stack of their own: a walk begun while one is read is read once the reading that began
/// it has answered and before the walk it was begun in goes on, in the order the text writes it, held state set around it,
/// and no nesting reaches past the reader's stack.
/// </summary>
public sealed class ScanStackTests
{
    /// <summary>
    /// Walks begun while one is read are read in the order the text writes them - each before the walk it was begun in
    /// goes on, two begun by one reading in the order they were begun - with each one's begin as it is reached and its end
    /// once it is read, its end's walks next.
    /// </summary>
    [Fact]
    public void NestedWalks_AreReadInTheOrderTheTextWritesThem()
    {
        // ( a ( b c ) d ( e ) ) f, as a reader of brackets reads it: a bracket's inside is a walk of its own.
        var text = "(a(bc)d(e))f";
        var stack = new ScanStack();
        var read = new List<string>();

        int Read(int at)
        {
            if (text[at] != '(')
            {
                read.Add(text[at].ToString());
                return at + 1;
            }

            var close = Close(text, at);
            stack.Walk(at + 1, close, Read, begin: () => read.Add($"<{at}"), end: () => read.Add($"{at}>"));
            read.Add($"after {at}");
            return close + 1;
        }

        stack.Walk(0, text.Length, Read);

        Assert.Equal(["after 0", "<0", "a", "after 2", "<2", "b", "c", "2>", "d", "after 7", "<7", "e", "7>", "0>", "f"], read);
    }

    /// <summary>Two walks one reading begins are read in the order it begins them, and a walk an end begins is read next.</summary>
    [Fact]
    public void WalksOneReadingBegins_AreReadInOrder_AndAnEndsWalkNext()
    {
        var stack = new ScanStack();
        var read = new List<string>();

        stack.Walk(0, 1, _ =>
        {
            stack.Walk(0, 2, at => Said(read, $"first {at}", at), end: () => stack.Walk(0, 1, at => Said(read, $"after first {at}", at)));
            stack.Walk(0, 1, at => Said(read, $"second {at}", at));
            return 1;
        });

        Assert.Equal(["first 0", "first 1", "after first 0", "second 0"], read);
    }

    /// <summary>
    /// State a reading holds is set while it reads, and again around each walk begun while it was held - however late
    /// that walk is read - and put back after; a walk begun once it is released is read without it.
    /// </summary>
    [Fact]
    public void HeldState_IsSetAroundEachWalkBegunWhileHeld()
    {
        var stack = new ScanStack();
        var held = new HashSet<string>(StringComparer.Ordinal);
        var read = new List<string>();

        stack.Walk(0, 1, _ =>
        {
            using (stack.Holding(() => held.Add("T"), () => held.Remove("T")))
            {
                read.Add($"reading holds {string.Join(',', held)}");
                stack.Walk(0, 1, at => Said(read, $"held walk sees {string.Join(',', held)}", at));
            }

            stack.Walk(0, 1, at => Said(read, $"later walk sees {string.Join(',', held)}", at));
            read.Add($"released, holds {held.Count}");
            return 1;
        });

        Assert.Equal(["reading holds T", "released, holds 0", "held walk sees T", "later walk sees "], read);
        Assert.Empty(held);
    }

    /// <summary>
    /// A walk whose end is asked before each step walks what grows as it is walked - a body that holds more bodies - to
    /// its end.
    /// </summary>
    [Fact]
    public void AWalkOfWhatGrows_IsWalkedToItsEnd()
    {
        var stack = new ScanStack();
        var bodies = new List<int> { 3 };
        var read = new List<int>();

        stack.Walk(0, () => bodies.Count, at =>
        {
            read.Add(bodies[at]);

            if (bodies[at] > 0)
            {
                bodies.Add(bodies[at] - 1);
            }

            return at + 1;
        });

        Assert.Equal([3, 2, 1, 0], read);
    }

    /// <summary>
    /// Brackets nested a hundred thousand deep are read whole, each opener's walk on the stack and none a call into the
    /// reading, in a child given a small stack: walks read by recursion would overflow it, and end the child.
    /// </summary>
    [Fact]
    public async Task AWalkNestedAHundredThousandDeep_IsRead()
        => Assert.Equal(new ChildRunAnswer(0, "100000 100000", ""), await ChildRun.MethodAsync(typeof(ScanStackTests), nameof(HundredThousandDeep), TestContext.Current.CancellationToken));

    /// <summary>The child of <see cref="AWalkNestedAHundredThousandDeep_IsRead"/>: how many openers and how many ends were read.</summary>
    internal static string HundredThousandDeep() => SmallStack.Run(() =>
    {
        var text = new string('(', 100_000) + new string(')', 100_000);
        var stack = new ScanStack();
        var (opened, ended) = (0, 0);

        int Read(int at)
        {
            if (text[at] != '(')
            {
                return at + 1;
            }

            opened++;
            var close = text.Length - 1 - at;
            stack.Walk(at + 1, close, Read, end: () => ended++);
            return close + 1;
        }

        stack.Walk(0, text.Length, Read);

        return string.Create(CultureInfo.InvariantCulture, $"{opened} {ended}");
    });

    /// <summary>Says <paramref name="what"/> into <paramref name="read"/> at the step <paramref name="at"/>; answers the next step.</summary>
    private static int Said(List<string> read, string what, int at)
    {
        read.Add(what);
        return at + 1;
    }

    /// <summary>Where the bracket <paramref name="open"/> opens closes in <paramref name="text"/>.</summary>
    private static int Close(string text, int open)
    {
        var depth = 0;

        for (var at = open; ; at++)
        {
            depth += text[at] switch { '(' => 1, ')' => -1, _ => 0 };

            if (depth == 0)
            {
                return at;
            }
        }
    }
}
