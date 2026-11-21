using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace DssHarness.Core.Walks;

/// <summary>
/// Calls that recurse as deep as their input nests, given the stack they need: no depth is refused and none is cut short,
/// and memory is the only bound. A stack overflow cannot be caught in .NET - it ends the process - so a recursion whose
/// depth the input decides runs through here, never on whatever stack its caller happens to have.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of recursion. Our own (<see cref="Run{T}(Func{T})"/>) calls through here at each level: the call runs where
/// it stands while the stack holds an ordinary call chain, and goes on, on a new thread of
/// <see cref="ThreadStackBytes"/>, where it does not - and a deeper call hops again, so the depth is bounded by memory
/// alone. A library's, which we cannot make call through here (<see cref="RunSized"/>), is given one thread whose stack
/// holds the depth the input nests to, measured before the call.
/// </para>
/// <para>
/// A hop's thread is a background one, started with the caller's execution context, so what flows with it - an
/// <see cref="AsyncLocal{T}"/>, the culture - flows to the call; the caller waits for it, and what the call throws is
/// thrown on in the caller through <see cref="ExceptionDispatchInfo"/>, its type, its message and its stack trace kept.
/// A thread's stack is reserved address space, its pages committed only as the call reaches them. Nothing here asks the
/// operating system: a thread of a given stack is .NET's own, on every platform.
/// </para>
/// </remarks>
public static class DeepCalls
{
    /// <summary>
    /// The stack of a thread our own recursion hops to: the size of one hop, not a limit - a call deeper than it holds
    /// hops again.
    /// </summary>
    public const int ThreadStackBytes = 256 * 1024 * 1024;

    /// <summary>
    /// What a sized call's thread holds beyond its depth's frames (<see cref="RunSized"/>): the thread's own start, the
    /// call's way down to its recursion, the work at the deepest level - a parser's own calls, an exception thrown and
    /// unwound there, a collection the runtime runs there - with room to spare.
    /// </summary>
    public const int SizedBaseBytes = 1024 * 1024;

    /// <summary>
    /// The most a sized call takes of its caller's stack (<see cref="RunSized"/>): a need this small runs where it stands
    /// when the stack holds it and an ordinary call chain beyond, so a file that nests a few levels starts no thread.
    /// Small enough to take where the runtime's check of an ordinary call chain has just passed, on a 32-bit system too.
    /// </summary>
    public const int InlineBytes = 32 * 1024;

    /// <summary>
    /// <paramref name="call"/>'s answer, run where it stands while the stack holds an ordinary call chain and on a thread of
    /// <see cref="ThreadStackBytes"/> where it does not: what each level of our own recursion calls through.
    /// </summary>
    /// <typeparam name="T">The answer.</typeparam>
    /// <param name="call">One level of the recursion.</param>
    public static T Run<T>(Func<T> call)
    {
        ArgumentNullException.ThrowIfNull(call);

        return RuntimeHelpers.TryEnsureSufficientExecutionStack() ? call() : OnThread(call, ThreadStackBytes);
    }

    /// <summary><paramref name="call"/>, run as <see cref="Run{T}(Func{T})"/> runs one: for a level that answers nothing.</summary>
    /// <param name="call">One level of the recursion.</param>
    public static void Run(Action call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            call();
            return;
        }

        OnThread(
            () =>
            {
                call();
                return true;
            },
            ThreadStackBytes);
    }

    /// <summary>
    /// <paramref name="call"/>'s answer, run on a stack that holds <paramref name="depth"/> frames of
    /// <paramref name="frameBytes"/> each and <see cref="SizedBaseBytes"/> beyond: for a library's recursion, whose depth
    /// the input nests to and was measured before the call. A need of at most <see cref="InlineBytes"/> runs where it
    /// stands, when the caller's stack is found to hold it and an ordinary call chain beyond.
    /// </summary>
    /// <remarks>
    /// A .NET thread's stack is given as an <see cref="int"/> count of bytes - the runtime's own fact - so a depth whose
    /// stack would pass <see cref="int.MaxValue"/> bytes cannot be given one thread. It is said, never run into: an
    /// <see cref="InsufficientExecutionStackException"/> whose message names the depth, the bytes it needs and the fact,
    /// thrown before anything is called. A reader turns it into its refusal of the file.
    /// </remarks>
    /// <typeparam name="T">The answer.</typeparam>
    /// <param name="depth">How deep the call recurses: the levels its input nests to.</param>
    /// <param name="frameBytes">The most stack one level of the recursion takes, measured, with a margin.</param>
    /// <param name="call">The call.</param>
    /// <exception cref="InsufficientExecutionStackException">The stack the depth needs passes what one thread can be given.</exception>
    public static T RunSized<T>(long depth, int frameBytes, Func<T> call)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameBytes);
        ArgumentNullException.ThrowIfNull(call);

        if (depth > (int.MaxValue - SizedBaseBytes) / frameBytes)
        {
            throw new InsufficientExecutionStackException(
                $"a call {depth} levels deep, of {frameBytes} bytes a level, needs {(Int128)depth * frameBytes + SizedBaseBytes} bytes of stack, "
                + $"and a .NET thread's stack is given as an int count of bytes, at most {int.MaxValue}: no one thread can hold it");
        }

        var need = (int)(depth * frameBytes);

        return need <= InlineBytes && Holds(need) ? call() : OnThread(call, need + SizedBaseBytes);
    }

    /// <summary>
    /// Whether the stack where this is called holds <paramref name="bytes"/> - at most <see cref="InlineBytes"/> - and an
    /// ordinary call chain beyond: the bytes are taken off the stack, here, only once the runtime's check says the stack
    /// holds an ordinary call chain, which is more than they are; and the check is asked again beyond them.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Holds(int bytes)
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return false;
        }

        if (bytes == 0)
        {
            return true;
        }

        Span<byte> taken = stackalloc byte[bytes];
        taken[^1] = 1;

        return RuntimeHelpers.TryEnsureSufficientExecutionStack() && taken[^1] == 1;
    }

    /// <summary>
    /// <paramref name="call"/>'s answer, run on a new background thread of <paramref name="stackBytes"/> started with the
    /// caller's execution context, the caller waiting for it; what it throws is thrown on here, as it was thrown.
    /// </summary>
    private static T OnThread<T>(Func<T> call, int stackBytes)
    {
        var answer = default(T)!;
        ExceptionDispatchInfo? thrown = null;

        var thread = new Thread(
            () =>
            {
                try
                {
                    answer = call();
                }
                catch (Exception ex)
                {
                    thrown = ExceptionDispatchInfo.Capture(ex);
                }
            },
            stackBytes)
        {
            IsBackground = true,
            Name = "deep call",
        };

        thread.Start();
        thread.Join();
        thrown?.Throw();

        return answer;
    }
}
