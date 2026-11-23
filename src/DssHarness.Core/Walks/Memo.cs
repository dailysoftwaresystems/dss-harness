namespace DssHarness.Core.Walks;

/// <summary>
/// Answers a key from the answers of the keys it depends on, each key answered once: a key asked again, by a later
/// question or by another key that depends on it, is given the first answer, so a graph of shared dependencies costs one
/// answer a key and one look an edge, however many ways lead to a key.
/// </summary>
/// <remarks>
/// <para>
/// Worked post-order on a stack of frames of its own - a key, the enumerator of what it depends on, the answers gathered
/// so far - never a recursion, so no chain of dependencies reaches past a thread's stack. A key is answered once every
/// key it depends on is, so dependencies are answered before what depends on them.
/// </para>
/// <para>
/// A key met again while it is still being answered - one that depends on itself, through any chain - is a cycle: the
/// key that met it is given <c>onCycle</c>'s answer for it in that place, asked at each such meeting, and the key is
/// recorded in <see cref="Cycles"/>. Its own answer is still given once its frame closes.
/// </para>
/// <para>
/// Given <see cref="Makings"/> it shares with other stores, a memo answers each key as a making of theirs, and keeps its answers
/// by their rule (<see cref="MadeOnce{TKey, TAnswer}"/>): an answer that read a cycle's placeholder - its own cycle's or one
/// through any of those stores - is held for the making of the outermost key it leans on and answered again after it, never
/// kept, where a memo of its own keeps every answer it gives.
/// </para>
/// <para>
/// One memo serves one walker at a time: it is not safe for use from several threads at once, and a caller that shares
/// one across threads holds a lock around <see cref="Of"/>.
/// </para>
/// </remarks>
/// <typeparam name="TKey">What is answered.</typeparam>
/// <typeparam name="TAnswer">The answer.</typeparam>
public sealed class Memo<TKey, TAnswer>
    where TKey : notnull
{
    private readonly Func<TKey, IEnumerable<TKey>> _dependsOn;
    private readonly Func<TKey, IReadOnlyList<TAnswer>, TAnswer> _answer;
    private readonly Func<TKey, TAnswer> _onCycle;
    private readonly Dictionary<TKey, TAnswer> _answers;
    private readonly HashSet<TKey> _open;
    private readonly HashSet<TKey> _cycled;
    private readonly List<TKey> _cycles = [];
    private readonly Makings? _makings;
    private readonly MadeOnce<TKey, TAnswer>? _made;

    /// <summary>A memo of the answers <paramref name="answer"/> gives.</summary>
    /// <param name="dependsOn">The keys a key depends on, in order: asked once a key, when it is first answered.</param>
    /// <param name="answer">A key's answer, from its dependencies' answers in the order they were named.</param>
    /// <param name="onCycle">The answer given in place of a key's where that key is met again while it is being answered.</param>
    /// <param name="comparer">What makes two keys one; the key's own equality where none is given.</param>
    /// <param name="makings">The makings it shares with other stores, whose rule keeps its answers; none for a memo of its own.</param>
    public Memo(Func<TKey, IEnumerable<TKey>> dependsOn, Func<TKey, IReadOnlyList<TAnswer>, TAnswer> answer, Func<TKey, TAnswer> onCycle, IEqualityComparer<TKey>? comparer = null, Makings? makings = null)
    {
        ArgumentNullException.ThrowIfNull(dependsOn);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(onCycle);

        _dependsOn = dependsOn;
        _answer = answer;
        _onCycle = onCycle;
        _answers = new Dictionary<TKey, TAnswer>(comparer);
        _open = new HashSet<TKey>(comparer);
        _cycled = new HashSet<TKey>(comparer);
        _makings = makings;
        _made = makings is null ? null : new MadeOnce<TKey, TAnswer>(comparer);
    }

    /// <summary>Every key met again while it was being answered, each once, in the order first met so.</summary>
    public IReadOnlyCollection<TKey> Cycles => _cycles;

    /// <summary>
    /// <paramref name="key"/>'s answer: the one given before where it was asked before, else answered now, with every key
    /// it depends on that is not answered yet.
    /// </summary>
    /// <remarks>
    /// Where a dependency or an answer throws, the keys still open are closed unanswered before the exception goes on, so a
    /// later question asks them afresh rather than reading them as cycles.
    /// </remarks>
    /// <param name="key">The key.</param>
    public TAnswer Of(TKey key)
    {
        if (Known(key, out var known))
        {
            return known;
        }

        // Asked from inside an answer for a key still being answered: the key depends on itself, as any cycle does.
        if (Met(key))
        {
            return Cycle(key);
        }

        var frames = new Stack<MemoFrame<TKey, TAnswer>>();

        try
        {
            Open(key, frames);

            while (true)
            {
                var frame = frames.Peek();

                if (frame.Dependencies.MoveNext())
                {
                    var dependency = frame.Dependencies.Current;

                    if (Known(dependency, out var answered))
                    {
                        frame.Gathered.Add(answered);
                    }
                    else if (Met(dependency))
                    {
                        frame.Gathered.Add(Cycle(dependency));
                    }
                    else
                    {
                        Open(dependency, frames);
                    }

                    continue;
                }

                frames.Pop();
                frame.Dependencies.Dispose();

                TAnswer answer;

                try
                {
                    answer = _answer(frame.Key, frame.Gathered);
                }
                catch
                {
                    Close(frame.Key);
                    throw;
                }

                Answered(frame.Key, answer);

                if (!frames.TryPeek(out var dependent))
                {
                    return answer;
                }

                dependent.Gathered.Add(answer);
            }
        }
        finally
        {
            while (frames.TryPop(out var unanswered))
            {
                Close(unanswered.Key);
                unanswered.Dependencies.Dispose();
            }
        }
    }

    /// <summary><paramref name="key"/>, met again while it is being answered: recorded as a cycle, and given <c>onCycle</c>'s answer.</summary>
    private TAnswer Cycle(TKey key)
    {
        if (_cycled.Add(key))
        {
            _cycles.Add(key);
        }

        return _onCycle(key);
    }

    /// <summary>Opens <paramref name="key"/>'s frame: what it depends on asked, and the key held open until it is answered.</summary>
    private void Open(TKey key, Stack<MemoFrame<TKey, TAnswer>> frames)
    {
        var dependencies = _dependsOn(key).GetEnumerator();

        if (_made is null)
        {
            _open.Add(key);
        }
        else
        {
            _made.Begin(_makings!, key);
        }

        frames.Push(new MemoFrame<TKey, TAnswer>(key, dependencies, []));
    }

    /// <summary>Whether <paramref name="key"/> was answered, and its answer: the one given, or - shared makings' - the one kept or held.</summary>
    private bool Known(TKey key, out TAnswer answer)
        => _made is null ? _answers.TryGetValue(key, out answer!) : _made.Known(_makings!, key, out answer);

    /// <summary>Whether <paramref name="key"/> is being answered: a cycle met.</summary>
    private bool Met(TKey key) => _made is null ? _open.Contains(key) : _made.Met(_makings!, key);

    /// <summary>Closes <paramref name="key"/>'s frame with its answer, given to every later question - shared makings' kept or held by their rule.</summary>
    private void Answered(TKey key, TAnswer answer)
    {
        if (_made is null)
        {
            _open.Remove(key);
            _answers.Add(key, answer);
        }
        else
        {
            _made.End(_makings!, key, answer);
        }
    }

    /// <summary>Closes <paramref name="key"/>'s frame unanswered.</summary>
    private void Close(TKey key)
    {
        if (_made is null)
        {
            _open.Remove(key);
        }
        else
        {
            _made.Abandon(_makings!, key);
        }
    }
}

/// <summary>A key being answered (<see cref="Memo{TKey, TAnswer}"/>): what it depends on, read on, and the answers gathered.</summary>
/// <param name="Key">The key.</param>
/// <param name="Dependencies">What it depends on, read so far.</param>
/// <param name="Gathered">The answers of the dependencies read so far, in their order.</param>
internal sealed record MemoFrame<TKey, TAnswer>(TKey Key, IEnumerator<TKey> Dependencies, List<TAnswer> Gathered);
