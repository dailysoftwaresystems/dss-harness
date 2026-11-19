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
/// What a reading does after it begins a walk is done before that walk is read: what must be done before a walk's first
/// declaration is read - state its declarations are read in, set for them alone - is its <c>begin</c>, run as it is
/// reached; what must be done once its last is read and every walk they began is - the bodies a type's members hold, read
/// once every member is declared; state set for it put back - is its <c>end</c>. Either may begin walks of its own, read
/// next. The walks a reading or a <c>begin</c> or <c>end</c> begins are read in the order they were begun.
/// </para>
/// </remarks>
internal sealed class ScanStack
{
    private readonly Stack<ScanWalk> _walks = new();

    /// <summary>The walks the reading under way has begun, in order; none outside one.</summary>
    private List<ScanWalk>? _begun;

    /// <summary>Whether a walk is being read: one begun now is a frame, read when its turn comes.</summary>
    private bool _running;

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
    {
        var walk = new ScanWalk(from, to, read, begin, end);

        if (_running)
        {
            (_begun ??= []).Add(walk);
            return;
        }

        _running = true;

        try
        {
            _walks.Push(walk);

            while (_walks.TryPeek(out var top))
            {
                if (!top.Begun)
                {
                    top.Begun = true;
                    Step(top.Begin);
                    continue;
                }

                if (top.At >= top.To)
                {
                    _walks.Pop();
                    Step(top.End);
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

            for (var at = begun.Count - 1; at >= 0; at--)
            {
                _walks.Push(begun[at]);
            }
        }
    }

    /// <summary>One walk on the stack: where it stands, what reads it, and what is done as it is reached and once it is read.</summary>
    private sealed class ScanWalk(int from, int to, Func<int, int> read, Action? begin, Action? end)
    {
        /// <summary>Where its next declaration begins.</summary>
        public int At { get; set; } = from;

        public int To { get; } = to;

        public Func<int, int> Read { get; } = read;

        public Action? Begin { get; } = begin;

        public Action? End { get; } = end;

        /// <summary>Whether it was reached, and its <see cref="Begin"/> run.</summary>
        public bool Begun { get; set; }
    }
}
