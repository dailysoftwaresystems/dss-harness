using System.Globalization;
using System.Runtime.CompilerServices;
using DssHarness.Core.Walks;
using DssHarness.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Tests;

/// <summary>
/// Calls that recurse as deep as their input nests, given the stack they need: our own recursion hopping to a new
/// thread where its stack runs low, a library's given one thread sized to its depth, what either throws thrown on as it
/// was thrown, and a depth no thread can hold said, never run into.
/// </summary>
public sealed class DeepCallsTests
{
    /// <summary>
    /// A recursion a million levels deep, each level calling through <see cref="DeepCalls.Run{T}(Func{T})"/>, runs to its
    /// bottom and back from a stack of a megabyte, which it passes a few thousand levels in: where the stack runs low the
    /// call goes on on a thread of its own, and a deeper call again.
    /// </summary>
    [Fact]
    public async Task ARecursionAMillionDeep_RunsThroughDeepCalls()
    {
        Assert.Equal(new ChildRunAnswer(0, "1000000", ""), await ChildRun.MethodAsync(typeof(DeepCallsTests), nameof(RecursionAMillionDeep), TestContext.Current.CancellationToken));
    }

    /// <summary>The child of <see cref="ARecursionAMillionDeep_RunsThroughDeepCalls"/>: how many levels the recursion counted.</summary>
    internal static string RecursionAMillionDeep() => SmallStack.Run(() => Levels(1_000_000).ToString(CultureInfo.InvariantCulture));

    /// <summary>A recursion <paramref name="left"/> levels deep that counts its levels, each through <see cref="DeepCalls"/>.</summary>
    private static int Levels(int left) => left == 0 ? 0 : DeepCalls.Run(() => Levels(left - 1)) + 1;

    /// <summary>
    /// YamlDotNet's representation model, which recurses once a level and calls through nothing of ours, reads a list a
    /// hundred thousand levels deep from a stack of a megabyte when its call is given a thread sized to that depth.
    /// </summary>
    [Fact]
    public async Task RunSized_GivesALibraryCallItsDepthsStack()
    {
        Assert.Equal(new ChildRunAnswer(0, "100000", ""), await ChildRun.MethodAsync(typeof(DeepCallsTests), nameof(LibraryCallAHundredThousandDeep), TestContext.Current.CancellationToken));
    }

    /// <summary>The child of <see cref="RunSized_GivesALibraryCallItsDepthsStack"/>: how many lists deep the read document goes.</summary>
    internal static string LibraryCallAHundredThousandDeep() => SmallStack.Run(() =>
    {
        var text = $"{string.Concat(Enumerable.Repeat("- ", 100_000))}1\n";
        var stream = DeepCalls.RunSized(
            100_000,
            YamlReading.FrameBytes,
            () =>
            {
                var read = new YamlStream();
                read.Load(new StringReader(text));
                return read;
            });

        var levels = 0;

        for (var node = stream.Documents[0].RootNode; node is YamlSequenceNode sequence; node = sequence.Children[0])
        {
            levels++;
        }

        return levels.ToString(CultureInfo.InvariantCulture);
    });

    /// <summary>
    /// What a call throws on the thread it was given is thrown on in its caller as it was thrown: its type and message,
    /// and its stack trace from the place it was thrown, not from the place it was thrown on again.
    /// </summary>
    [Fact]
    public void AnExceptionOnACallsThread_IsThrownOnWithItsTypeMessageAndTrace()
    {
        var caller = Environment.CurrentManagedThreadId;
        var thrower = caller;

        var thrown = Assert.Throws<InvalidOperationException>(() => DeepCalls.RunSized(
            1_000_000,
            1,
            () =>
            {
                thrower = Environment.CurrentManagedThreadId;
                return Bottom();
            }));

        // A megabyte of need starts a thread of its own, so the exception crossed from one thread to another.
        Assert.NotEqual(caller, thrower);
        Assert.Equal("the bottom of the call", thrown.Message);
        Assert.StartsWith($"   at {typeof(DeepCallsTests).FullName}.{nameof(Bottom)}()", thrown.StackTrace, StringComparison.Ordinal);
    }

    /// <summary>Where the call of <see cref="AnExceptionOnACallsThread_IsThrownOnWithItsTypeMessageAndTrace"/> throws.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Bottom() => throw new InvalidOperationException("the bottom of the call");

    /// <summary>
    /// A need that fits the caller's stack runs where it stands, on the caller's thread; one past
    /// <see cref="DeepCalls.InlineBytes"/> runs on a thread of its own.
    /// </summary>
    [Fact]
    public void ASmallNeed_RunsWhereItStands_AndALargerOneOnAThreadOfItsOwn()
    {
        var caller = Environment.CurrentManagedThreadId;

        Assert.Equal(caller, DeepCalls.RunSized(DeepCalls.InlineBytes / 1024, 1024, () => Environment.CurrentManagedThreadId));
        Assert.NotEqual(caller, DeepCalls.RunSized((DeepCalls.InlineBytes / 1024) + 1, 1024, () => Environment.CurrentManagedThreadId));
    }

    /// <summary>
    /// A depth whose stack passes what a .NET thread can be given - an int count of bytes - is said, before anything is
    /// called, naming the depth, the bytes it needs and the fact; never a crash, and never an answer it did not reach.
    /// </summary>
    [Fact]
    public void ADepthNoThreadCanHold_IsSaidNamingTheFact_AndNothingRuns()
    {
        var ran = false;

        var refused = Assert.Throws<InsufficientExecutionStackException>(() => DeepCalls.RunSized(
            3_000_000,
            1024,
            () =>
            {
                ran = true;
                return 0;
            }));

        Assert.Equal(
            "a call 3000000 levels deep, of 1024 bytes a level, needs 3073048576 bytes of stack, and a .NET thread's stack is given as an int count of bytes, at most 2147483647: no one thread can hold it",
            refused.Message);
        Assert.False(ran);
    }
}
