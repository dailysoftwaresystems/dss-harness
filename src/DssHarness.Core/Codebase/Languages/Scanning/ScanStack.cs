namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// The walks of one scanned reader's declarations, on a stack of its own: a walk a declaration's reading begins - a
/// block's members, a namespace's body - is never a call back into the reader from inside that reading, but a frame this
/// stack holds, read once the reading that began it has answered and before the walk it was begun in goes on. So a block
/// nested any depth is read in the order the text writes it, and no nesting reaches past the reader's stack: what the
/// text nests is frames here, one a block, and memory alone bounds them.
/// </summary>
/// <remarks>
/// <para>
/// A walk reads its tokens one declaration at a time, as <see cref="ScanFrame.Walk"/> does: its reading of one answers
/// where the next begins - always past it, since one that reads nothing is passed over a token at a time.
/// </para>
/// <para>
/// What a reading does after it begins a walk is done before that walk is read. What must be done before a walk's first
/// declaration is read - state its declarations are read in, set for them alone - is its <c>begin</c>, run as it is
/// reached; what must be done once its last is read and every walk they began is - the bodies a type's members hold, read
/// once every member is declared; state set for it put back - is its <c>end</c>. Either may begin walks of its own, read
/// next. The walks a reading, a <c>begin</c> or an <c>end</c> begins are read in the order they were begun.
/// </para>
/// <para>
/// State a reading sets for all it reads and puts back once it is done - a template's parameters, which the declaration
/// it heads and every member of it see - is held (<see cref="Holding"/>): set now, and set again around each walk begun
/// while it is held, so a walk read later is read in the state it was begun in.
/// </para>
/// </remarks>
internal sealed class ScanStack
{
    private readonly Stack<ScanWalk> _walks = new();

    /// <summary>What is held now, in the order it was taken (<see cref="Holding"/>).</summary>
    private readonly List<(Action Set, Action Reset)> _held = [];

    /// <summary>The walks the step under way has begun, in order; none outside one.</summary>
    private List<ScanWalk>? _begun;

    /// <summary>Whether a walk is being read: one begun now is a frame, read when its turn comes.</summary>
    private bool _running;

    /// <summary>How much of <see cref="_held"/> was held before the walks being read began: what no walk begun now takes.</summary>
    private int _heldBefore;

    /// <summary>
    /// Walks the tokens from <paramref name="from"/> to <paramref name="to"/>, one declaration at a time, each read by
    /// <paramref name="read"/> - at once where no walk is being read, and else once the reading under way has answered.
    /// </summary>
    /// <param name="from">The first index.</param>
    /// <param name="to">The index past the last.</param>
    /// <param name="read">The reading of one declaration, answering where the next begins.</param>
    /// <param name="begin">What is done as the walk is reached, before its first declaration is read; none where nothing is.</param>
    /// <param name="end">What is done once its last declaration is read, and every walk they began; none where nothing is.</param>
    public void Walk(int from, int to, Func<int, int> read, Action? begin = null, Action? end = null)
        => Walk(from, () => to, read, begin, end);

    /// <summary>
    /// Walks from <paramref name="from"/> while the index stands before what <paramref name="to"/> answers - asked again
    /// before each step, for a walk of what grows as it is walked: the bodies a type's members hold, each of which may
    /// hold more - as <see cref="Walk(int, int, Func{int, int}, Action?, Action?)"/> walks tokens.
    /// </summary>
    /// <param name="from">The first index.</param>
    /// <param name="to">The index past the last, asked before each step.</param>
    /// <param name="read">The reading of one step, answering where the next begins.</param>
    /// <param name="begin">What is done as the walk is reached; none where nothing is.</param>
    /// <param name="end">What is done once it is walked, and every walk it began; none where nothing is.</param>
    public void Walk(int from, Func<int> to, Func<int, int> read, Action? begin = null, Action? end = null)
    {
        if (_running)
        {
            (_begun ??= []).Add(new ScanWalk(from, to, read, begin, end, _held.Count > _heldBefore ? [.. _held.Skip(_heldBefore)] : null));
            return;
        }

        _running = true;
        _heldBefore = _held.Count;

        try
        {
            _walks.Push(new ScanWalk(from, to, read, begin, end, null));

            while (_walks.TryPeek(out var top))
            {
                if (!top.Begun)
                {
                    top.Begun = true;
                    Step(top.Held is null && top.Begin is null ? null : () =>
                    {
                        foreach (var (set, _) in top.Held ?? [])
                        {
                            set();
                        }

                        top.Begin?.Invoke();
                    });
                    continue;
                }

                if (top.At >= top.To())
                {
                    _walks.Pop();
                    Step(top.Held is null && top.End is null ? null : () =>
                    {
                        top.End?.Invoke();

                        for (var index = (top.Held?.Length ?? 0) - 1; index >= 0; index--)
                        {
                            top.Held![index].Reset();
                        }
                    });
                    continue;
                }

                var at = top.At;
                var next = at;
                Step(() => next = top.Read(at));
                top.At = next > at ? next : at + 1;
            }
        }
        finally
        {
            _running = false;
            _begun = null;
            _walks.Clear();
        }
    }

    /// <summary>
    /// Holds state the reading under way sets for all it reads - <paramref name="set"/> run now - until what this answers
    /// is disposed, when <paramref name="reset"/> is run: each walk begun while it is held is read in it too, the state set
    /// as the walk is reached and put back once it is read. Held state is put back in the order opposite to its taking.
    /// </summary>
    /// <param name="set">What sets the state.</param>
    /// <param name="reset">What puts it back.</param>
    public IDisposable Holding(Action set, Action reset)
    {
        set();
        _held.Add((set, reset));
        return new Held(this, _held.Count);
    }

    /// <summary>Runs <paramref name="step"/>, then stacks the walks it began - the first on top, read first.</summary>
    private void Step(Action? step)
    {
        if (step is null)
        {
            return;
        }

        _begun = null;
        step();

        if (_begun is { } begun)
        {
            _begun = null;

            for (var index = begun.Count - 1; index >= 0; index--)
            {
                _walks.Push(begun[index]);
            }
        }
    }

    /// <summary>One holding (<see cref="Holding"/>), released by its disposal: its state put back, and it held no more.</summary>
    private sealed class Held(ScanStack stack, int count) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;

            // Held state is released last taken first, as the reading's own scopes close.
            if (stack._held.Count != count)
            {
                throw new InvalidOperationException("held state is released in the order opposite to its taking");
            }

            var (_, reset) = stack._held[count - 1];
            stack._held.RemoveAt(count - 1);
            reset();
        }
    }

    /// <summary>One walk on the stack: where it stands, what reads it, what it was begun holding, and what is done as it is reached and once it is read.</summary>
    private sealed class ScanWalk(int from, Func<int> to, Func<int, int> read, Action? begin, Action? end, (Action Set, Action Reset)[]? held)
    {
        /// <summary>Where its next step begins.</summary>
        public int At { get; set; } = from;

        public Func<int> To { get; } = to;

        public Func<int, int> Read { get; } = read;

        public Action? Begin { get; } = begin;

        public Action? End { get; } = end;

        /// <summary>What was held when it was begun, set again around it.</summary>
        public (Action Set, Action Reset)[]? Held { get; } = held;

        /// <summary>Whether it was reached, and its <see cref="Begin"/> run.</summary>
        public bool Begun { get; set; }
    }
}
