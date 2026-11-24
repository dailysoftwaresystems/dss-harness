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
/// Each frame is a making of the one rule of a store (<see cref="MadeOnce{TKey, TAnswer}"/>), on the <see cref="Makings"/>
/// given - shared with other stores whose makings ask this one's - or, where none is, on a <see cref="Makings"/> of the
/// memo's own. A key asked again while it is being answered - directly or through any store that shares the makings - is
/// a cycle. Every key of a strongly connected set of makings that holds a cycle answers its placeholder - nothing known,
/// <c>onCycle</c>'s answer - with whatever its store keeps beside it of what the whole set read, <c>cycled</c>'s; every read
/// of such a key, while the set is made and after, is given that answer. Every other key keeps the answer its making made.
/// Each key is answered once, each set is closed once as its outermost making ends, and nothing kept is ever answered again:
/// the work is linear in the answers and the reads, whatever key is asked first and however deep the cycles nest. Every
/// key met again is recorded in <see cref="Cycles"/>.
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
    private readonly HashSet<TKey> _inCycles;
    private readonly List<TKey> _cycles = [];
    private readonly Makings _makings;
    private readonly MadeOnce<TKey, TAnswer> _made;

    /// <summary>A memo of the answers <paramref name="answer"/> gives.</summary>
    /// <param name="dependsOn">The keys a key depends on, in order: asked once a key, when it is first answered.</param>
    /// <param name="answer">A key's answer, from its dependencies' answers in the order they were named.</param>
    /// <param name="onCycle">
    /// What is known of a key while it is being answered, and kept for every key of a cycle: nothing. A function of the key
    /// alone, as the answer a making reads for a key of a cycle must be the one that key keeps.
    /// </param>
    /// <param name="comparer">What makes two keys one; the key's own equality where none is given.</param>
    /// <param name="makings">The makings it shares with other stores, whose rule keeps its answers; none for a memo on makings of its own.</param>
    /// <param name="cycled">
    /// What a key of a cycle is kept, from the answer its making made and the cycle; the placeholder where none is given.
    /// </param>
    public Memo(Func<TKey, IEnumerable<TKey>> dependsOn, Func<TKey, IReadOnlyList<TAnswer>, TAnswer> answer, Func<TKey, TAnswer> onCycle, IEqualityComparer<TKey>? comparer = null, Makings? makings = null, Func<TKey, TAnswer, Cycle, TAnswer>? cycled = null)
    {
        ArgumentNullException.ThrowIfNull(dependsOn);
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(onCycle);

        _dependsOn = dependsOn;
        _answer = answer;
        _onCycle = onCycle;
        _inCycles = new HashSet<TKey>(comparer);
        _makings = makings ?? new Makings();
        _made = new MadeOnce<TKey, TAnswer>(onCycle, cycled, comparer);
    }

    /// <summary>
    /// Every key met again before its set closed - while it was being answered, or answered and of a cycle not closed yet -
    /// each once, in the order first met so.
    /// </summary>
    public IReadOnlyCollection<TKey> Cycles => _cycles;

    /// <summary>
    /// <paramref name="key"/>'s final answer: the one kept, else - where the key is being answered, or answered and its set
    /// not closed yet - <c>onCycle</c>'s, else answered now, with every key it depends on that is not answered yet.
    /// </summary>
    /// <remarks>
    /// Where a dependency or an answer throws, the keys still open are closed unanswered before the exception goes on, so a
    /// later question asks them afresh rather than reading them as cycles.
    /// </remarks>
    /// <param name="key">The key.</param>
    public TAnswer Of(TKey key)
    {
        if (_made.TryKept(key, out var known))
        {
            return known;
        }

        // Asked from inside an answer for a key still being answered: the key depends on itself, as any cycle does.
        if (_made.Met(_makings, key))
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

                    if (_made.TryKept(dependency, out var answered))
                    {
                        frame.Gathered.Add(answered);
                    }
                    else if (_made.Met(_makings, dependency))
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

                TAnswer made;

                try
                {
                    made = _answer(frame.Key, frame.Gathered);
                }
                catch
                {
                    _made.Abandon(_makings, frame.Making);
                    throw;
                }

                // A dependency whose set has not closed is of a cycle: what depends on it reads its placeholder, never an answer
                // made from one.
                var given = _made.End(_makings, frame.Making, made);

                if (!frames.TryPeek(out var dependent))
                {
                    return given;
                }

                dependent.Gathered.Add(given);
            }
        }
        finally
        {
            while (frames.TryPop(out var unanswered))
            {
                _made.Abandon(_makings, unanswered.Making);
                unanswered.Dependencies.Dispose();
            }
        }
    }

    /// <summary><paramref name="key"/>, met again while its set is not closed: recorded as a cycle, and given <c>onCycle</c>'s answer.</summary>
    /// <param name="key">The key met again.</param>
    private TAnswer Cycle(TKey key)
    {
        if (_inCycles.Add(key))
        {
            _cycles.Add(key);
        }

        return _onCycle(key);
    }

    /// <summary>
    /// Opens <paramref name="key"/>'s frame: its making begun first, so that whatever is asked for what the key depends on is
    /// read as its own, and the key held open until it is answered.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="frames">The frames of the answer under way.</param>
    private void Open(TKey key, Stack<MemoFrame<TKey, TAnswer>> frames)
    {
        var making = _made.Begin(_makings, key);
        IEnumerator<TKey> dependencies;

        try
        {
            dependencies = _dependsOn(key).GetEnumerator();
        }
        catch
        {
            _made.Abandon(_makings, making);
            throw;
        }

        frames.Push(new MemoFrame<TKey, TAnswer>(key, making, dependencies, []));
    }
}

/// <summary>
/// A key being answered (<see cref="Memo{TKey, TAnswer}"/>): its making, what it depends on, read on, and the answers
/// gathered.
/// </summary>
/// <param name="Key">The key.</param>
/// <param name="Making">Its making, on the memo's makings.</param>
/// <param name="Dependencies">What it depends on, read so far.</param>
/// <param name="Gathered">The answers of the dependencies read so far, in their order.</param>
internal sealed record MemoFrame<TKey, TAnswer>(TKey Key, Making<TKey, TAnswer> Making, IEnumerator<TKey> Dependencies, List<TAnswer> Gathered)
    where TKey : notnull;
