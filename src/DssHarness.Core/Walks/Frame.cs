namespace DssHarness.Core.Walks;

/// <summary>
/// One call of a recursion that runs on a stack of its own rather than the thread's: its body is an iterator that yields
/// each call it makes, in turn, and reads that call's <see cref="Frame{T}.Result"/> once it goes on. The body is run, and
/// each call it yields is run to its end before the body goes on, on a stack of frames held in memory - so a recursion as
/// deep as its input nests (a query's parentheses, a schema's properties, a document's members) is bounded by memory alone,
/// and the thread's stack, whose overflow no catch stops, is never what ends it.
/// </summary>
/// <remarks>
/// <para>
/// A body is written as the recursion would be, each call <c>var inner = Call(x);</c> made a frame, then
/// <c>yield return inner;</c>, then <c>inner.Result</c> read where the recursion read the call's answer. A body gives its
/// own answer through <see cref="Frame{T}.Give"/> before it ends. A frame yielded that has its result already - one kept
/// from an earlier call, say - is not run again: its body ran once.
/// </para>
/// <para>
/// What a body throws is thrown on to whoever runs the outermost frame, and the bodies still open below it are ended first,
/// each one's <c>finally</c> run. Frames are run on the thread that runs the outermost one, in the order a recursion would
/// run them, so a body may share state with the bodies it calls.
/// </para>
/// </remarks>
public abstract class Frame
{
    /// <summary>A frame of one of this type's own kinds.</summary>
    private protected Frame()
    {
    }

    /// <summary>Whether the call has given its result, and so is not run again.</summary>
    public abstract bool Given { get; }

    /// <summary>The call's body, begun: each call it makes, yielded in turn.</summary>
    private protected abstract IEnumerator<Frame> Begin();

    /// <summary>
    /// Runs <paramref name="first"/>'s body and every call it makes, each to its end before the body that made it goes on,
    /// on a stack of frames of its own.
    /// </summary>
    /// <param name="first">The outermost call.</param>
    /// <exception cref="InvalidOperationException">A body yielded no frame, or ended without giving its result.</exception>
    private protected static void Unwind(Frame first)
    {
        var open = new Stack<(Frame Call, IEnumerator<Frame> Body)>();

        try
        {
            open.Push((first, first.Begin()));

            while (open.TryPeek(out var top))
            {
                if (top.Body.MoveNext())
                {
                    var made = top.Body.Current ?? throw new InvalidOperationException("a frame's body yielded no frame: each call it makes is a frame");

                    if (!made.Given)
                    {
                        open.Push((made, made.Begin()));
                    }

                    continue;
                }

                open.Pop();
                top.Body.Dispose();

                if (!top.Call.Given)
                {
                    throw new InvalidOperationException("a frame's body ended without giving its result");
                }
            }
        }
        finally
        {
            // Left early by a throw: every body still open is ended, its own finally run.
            while (open.TryPop(out var left))
            {
                left.Body.Dispose();
            }
        }
    }
}

/// <summary>A call of a recursion run on a stack of its own (<see cref="Frame"/>), whose answer is a <typeparamref name="T"/>.</summary>
/// <typeparam name="T">What the call answers.</typeparam>
/// <param name="body">
/// The call's body: given the frame itself, to give its result through, it yields each call it makes in turn and reads
/// each one's result once it goes on.
/// </param>
public sealed class Frame<T>(Func<Frame<T>, IEnumerable<Frame>> body) : Frame
{
    private readonly Func<Frame<T>, IEnumerable<Frame>> _body = body ?? throw new ArgumentNullException(nameof(body));
    private T _result = default!;
    private bool _given;

    /// <inheritdoc/>
    public override bool Given => _given;

    /// <summary>What the call answered: read by the body that yielded it, once it goes on.</summary>
    /// <exception cref="InvalidOperationException">The call has not given its result yet.</exception>
    public T Result => _given ? _result : throw new InvalidOperationException("a frame's result is read before its call gave one");

    /// <summary>A frame that has its result already, <paramref name="result"/>: yielded, it is not run.</summary>
    /// <param name="result">The result.</param>
    public static Frame<T> Of(T result)
    {
        var frame = new Frame<T>(static _ => []);
        frame.Give(result);
        return frame;
    }

    /// <summary>Gives the call's result: what its body answers, given once, before the body ends.</summary>
    /// <param name="result">The result.</param>
    /// <exception cref="InvalidOperationException">It was given already.</exception>
    public void Give(T result)
    {
        if (_given)
        {
            throw new InvalidOperationException("a frame's result is given twice");
        }

        _result = result;
        _given = true;
    }

    /// <summary>Runs the call, and every call it makes, on a stack of their own, and answers its result.</summary>
    /// <exception cref="InvalidOperationException">A body yielded no frame, or ended without giving its result.</exception>
    public T Run()
    {
        if (!_given)
        {
            Unwind(this);
        }

        return Result;
    }

    /// <inheritdoc/>
    private protected override IEnumerator<Frame> Begin() => _body(this).GetEnumerator();
}
