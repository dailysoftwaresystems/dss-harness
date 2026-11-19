using DssHarness.Core.Walks;

namespace DssHarness.Tests;

/// <summary>A recursion run on a stack of its own: each call a frame, its answer read once it has run, at any depth.</summary>
public sealed class FrameTests
{
    /// <summary>How deep the deep recursion goes.</summary>
    private const int Deep = 100_000;

    /// <summary>
    /// A recursion a hundred thousand calls deep answers as the recursion would: each call's answer read by the call that
    /// made it. Run in a child on a small stack, so frames that went on the thread's stack would end that child, never this
    /// run.
    /// </summary>
    [Fact]
    public async Task ARecursionAHundredThousandDeep_RunsOnAStackOfItsOwn()
    {
        var answer = await ChildRun.MethodAsync(typeof(FrameTests), nameof(CountDeep), TestContext.Current.CancellationToken);

        Assert.Equal(new ChildRunAnswer(0, "100000\n", string.Empty), answer);
    }

    /// <summary>
    /// A frame that has its answer already - one kept from an earlier call, or made with it - is not run again where it is
    /// yielded: its body ran once, however many calls read it.
    /// </summary>
    [Fact]
    public void AFrameGivenAlready_IsNotRunAgain()
    {
        var ran = 0;
        var shared = new Frame<int>(self => Giving(self, () => ran++, 7));

        var sum = new Frame<int>(self => Summing(self, [shared, shared, Frame<int>.Of(3)])).Run();

        Assert.Equal(17, sum);
        Assert.Equal(1, ran);
        Assert.Equal(7, shared.Run());
        Assert.Equal(1, ran);
    }

    /// <summary>
    /// What a body throws, however deep, reaches whoever ran the outermost frame as it was thrown, every body still open
    /// ended first, its own <c>finally</c> run - innermost first.
    /// </summary>
    [Fact]
    public void AThrowInADeepBody_EndsEveryOpenBodyFirst()
    {
        var ended = new List<int>();

        var thrown = Assert.Throws<InvalidDataException>(() => Throwing(5, ended).Run());

        Assert.Equal("thrown at the bottom", thrown.Message);
        Assert.Equal([0, 1, 2, 3, 4, 5], ended);
    }

    /// <summary>
    /// A body that yields no frame, or ends without giving its answer, is refused as the fault in its writing it is; so is an
    /// answer given twice, and an answer read before it is given.
    /// </summary>
    [Fact]
    public void AFrameWrittenWrong_IsRefused()
    {
        Assert.Equal(
            "a frame's body yielded no frame: each call it makes is a frame",
            Assert.Throws<InvalidOperationException>(() => new Frame<int>(_ => [null!]).Run()).Message);
        Assert.Equal(
            "a frame's body ended without giving its result",
            Assert.Throws<InvalidOperationException>(() => new Frame<int>(_ => []).Run()).Message);

        var given = Frame<int>.Of(1);

        Assert.Equal("a frame's result is given twice", Assert.Throws<InvalidOperationException>(() => given.Give(2)).Message);
        Assert.Equal(
            "a frame's result is read before its call gave one",
            Assert.Throws<InvalidOperationException>(() => new Frame<int>(_ => []).Result).Message);
    }

    /// <summary><see cref="ARecursionAHundredThousandDeep_RunsOnAStackOfItsOwn"/>'s child.</summary>
    private static string CountDeep() => SmallStack.Run(() => $"{Counted(Deep).Run()}\n");

    /// <summary>How many calls lie below <paramref name="left"/>: zero at the bottom, and one more than the call it makes above.</summary>
    private static Frame<long> Counted(int left)
        => left == 0 ? Frame<long>.Of(0) : new Frame<long>(self => Counting(self, left));

    private static IEnumerable<Frame> Counting(Frame<long> self, int left)
    {
        var below = Counted(left - 1);
        yield return below;
        self.Give(below.Result + 1);
    }

    private static IEnumerable<Frame> Giving(Frame<int> self, Action ran, int answer)
    {
        ran();
        self.Give(answer);
        yield break;
    }

    private static IEnumerable<Frame> Summing(Frame<int> self, IReadOnlyList<Frame<int>> parts)
    {
        var sum = 0;

        foreach (var part in parts)
        {
            yield return part;
            sum += part.Result;
        }

        self.Give(sum);
    }

    /// <summary>A recursion <paramref name="left"/> calls deep whose bottom throws, each body saying in its finally that it ended.</summary>
    private static Frame<int> Throwing(int left, List<int> ended) => new(self => ThrowingBody(self, left, ended));

    private static IEnumerable<Frame> ThrowingBody(Frame<int> self, int left, List<int> ended)
    {
        try
        {
            if (left == 0)
            {
                throw new InvalidDataException("thrown at the bottom");
            }

            var below = Throwing(left - 1, ended);
            yield return below;
            self.Give(below.Result);
        }
        finally
        {
            ended.Add(left);
        }
    }
}
