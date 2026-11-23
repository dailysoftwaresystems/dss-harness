namespace DssHarness.Core.Walks;

/// <summary>
/// Answers made once each, where making one asks for others - of this store or of any store that shares its
/// <see cref="Makings"/> - as it is made: the dependencies are found by the asking, where a <see cref="Memo{TKey, TAnswer}"/>'s
/// are named before. A key asked again while it is being made is a cycle: the asker is given what is known of the key then -
/// the answer the asker passes for nothing - and the key is never made inside itself.
/// </summary>
/// <remarks>
/// <para>
/// An answer is kept only where no such placeholder reached it. One made while a key further out was met again - read from that
/// key's placeholder, directly or through any answer made in between - was made from nothing where that key's answer stands: it
/// is given where it was asked and held apart, so it is made once in that key's making whoever asks it again there, and forgotten
/// once that making ends, to be made anew from the key's own answer at the next ask. The key met again keeps the answer its making
/// gives, as a memo's does - unless a key further out still was met too, which makes it one such answer itself. So no kept answer
/// was made from a placeholder but the cycle's own outermost key's, and a later ask never reads one.
/// </para>
/// <para>
/// A making is the caller's code, and asks on the caller's stack; a making that throws leaves its key unmade and nothing it held
/// for the throw's sake, so a later ask makes it afresh. One store, and one <see cref="Makings"/>, serve one walker at a time: they
/// are not safe for use from several threads at once.
/// </para>
/// </remarks>
/// <typeparam name="TKey">What is answered.</typeparam>
/// <typeparam name="TAnswer">The answer.</typeparam>
public sealed class MadeOnce<TKey, TAnswer>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TAnswer> _kept;
    private readonly Dictionary<TKey, int> _open;
    private readonly Dictionary<TKey, (TAnswer Answer, int Leans)> _held;

    /// <summary>A store of answers, none made yet.</summary>
    /// <param name="comparer">What makes two keys one; the key's own equality where none is given.</param>
    public MadeOnce(IEqualityComparer<TKey>? comparer = null)
    {
        _kept = new Dictionary<TKey, TAnswer>(comparer);
        _open = new Dictionary<TKey, int>(comparer);
        _held = new Dictionary<TKey, (TAnswer Answer, int Leans)>(comparer);
    }

    /// <summary>Whether <paramref name="key"/>'s answer is kept, and that answer: one no placeholder reached.</summary>
    /// <param name="key">The key.</param>
    /// <param name="answer">Its kept answer, where it has one.</param>
    public bool TryKept(TKey key, out TAnswer answer) => _kept.TryGetValue(key, out answer!);

    /// <summary>
    /// <paramref name="key"/>'s answer: the one kept, else the one held in the making under way, else - where the key is being
    /// made, further out - <paramref name="none"/>, else made now by <paramref name="make"/>, which may ask for any key of any store
    /// <paramref name="makings"/> serves.
    /// </summary>
    /// <param name="makings">The makings under way of every store whose makings ask this one's.</param>
    /// <param name="key">The key.</param>
    /// <param name="none">What is known of the key while it is being made: nothing.</param>
    /// <param name="make">The key's making.</param>
    public TAnswer Of(Makings makings, TKey key, TAnswer none, Func<TAnswer> make)
    {
        ArgumentNullException.ThrowIfNull(makings);
        ArgumentNullException.ThrowIfNull(make);

        if (_kept.TryGetValue(key, out var kept))
        {
            return kept;
        }

        // Held in a making still under way: what asks reads what that answer read.
        if (_held.TryGetValue(key, out var held))
        {
            makings.Read(held.Leans);
            return held.Answer;
        }

        // Asked while it is made, through any chain of makings: a cycle.
        if (_open.TryGetValue(key, out var depth))
        {
            makings.Read(depth);
            return none;
        }

        depth = makings.Depth;
        _open.Add(key, depth);
        makings.Begin();

        TAnswer answer;
        Making making;

        try
        {
            answer = make();
        }
        finally
        {
            making = makings.End();
            _open.Remove(key);
            making.Forget();
        }

        if (making.Leans < depth)
        {
            _held[key] = (answer, making.Leans);
            makings.ForgetWhenEnded(making.Leans, () => _held.Remove(key));
            makings.Read(making.Leans);
        }
        else
        {
            _kept[key] = answer;
        }

        return answer;
    }

    /// <summary>Forgets every answer: what was made while what it was made from was otherwise. Asked between makings, never inside one.</summary>
    public void Clear()
    {
        _kept.Clear();
        _held.Clear();
    }
}

/// <summary>
/// The makings under way of the stores that share it (<see cref="MadeOnce{TKey, TAnswer}"/>), from the outermost in: each with
/// the outermost making it read a placeholder of - its key met again, or an answer held from one - and what to forget once it
/// ends. Shared by every store whose makings ask the others, so a cycle through several stores is told as one through one.
/// </summary>
/// <remarks>Not safe for use from several threads at once, as the stores that share it are not.</remarks>
public sealed class Makings
{
    private readonly List<Making> _open = [];

    /// <summary>How many makings are under way.</summary>
    internal int Depth => _open.Count;

    /// <summary>Begins a making, inside the one under way.</summary>
    internal void Begin() => _open.Add(new Making());

    /// <summary>Ends the innermost making: what it read and what to forget.</summary>
    internal Making End()
    {
        var making = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        return making;
    }

    /// <summary>
    /// The making under way read what the one at <paramref name="depth"/> leaves unknown until it ends - its key's placeholder, or an
    /// answer made from one: its own answer is that one's too.
    /// </summary>
    internal void Read(int depth)
    {
        if (_open.Count > 0 && depth < _open[^1].Leans)
        {
            _open[^1].Leans = depth;
        }
    }

    /// <summary>Forgets what <paramref name="forget"/> forgets once the making at <paramref name="depth"/> ends.</summary>
    internal void ForgetWhenEnded(int depth, Action forget) => _open[depth].Forgets.Add(forget);
}

/// <summary>One making under way (<see cref="Makings"/>): the outermost making whose placeholder it read, and the answers held until it ends.</summary>
internal sealed class Making
{
    /// <summary>The depth of the outermost making whose placeholder it read, directly or through an answer held from one; none where it read none.</summary>
    public int Leans { get; set; } = int.MaxValue;

    /// <summary>What to forget once it ends: the answers made from its placeholder.</summary>
    public List<Action> Forgets { get; } = [];

    /// <summary>Forgets the answers made from its placeholder.</summary>
    public void Forget()
    {
        foreach (var forget in Forgets)
        {
            forget();
        }
    }
}
