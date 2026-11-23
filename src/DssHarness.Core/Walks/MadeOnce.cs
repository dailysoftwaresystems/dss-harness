namespace DssHarness.Core.Walks;

/// <summary>
/// Answers made once each, where making one asks for others - of this store or of any store that shares its
/// <see cref="Makings"/>, a <see cref="Memo{TKey, TAnswer}"/> given them among them - as it is made: the dependencies are found by
/// the asking. A key asked again while it is being made is a cycle: the asker is given what is known of the key then - the answer
/// the asker passes for nothing - and the key is never made inside itself.
/// </summary>
/// <remarks>
/// <para>
/// An answer is kept only where no placeholder reached it but its own key's. One made while a key further out was met again - read
/// from that key's placeholder, directly or through any answer made in between - is given where it was asked and held apart for
/// the making of the outermost key it leans on: a making that ends leaning further out still hands what leans on it to the one it
/// leans on, so each key of a cycle is made once in the outermost key's making, however deep the cycles nest and however often
/// asked there. Once that key's making ends, its answer is kept - an answer made from those its cycle's keys gave it, the one
/// answer kept that a placeholder reached - and every answer held for it is forgotten, to be made anew, from that kept answer, at
/// its next ask. So a cycle's keys asked one after another from outside it each make again the keys below them, which the last
/// one's making made and forgot: a cost only a cycle pays.
/// </para>
/// <para>
/// A making is the caller's code, and asks on the caller's stack; a making that throws leaves its key unmade and forgets what was
/// held for it, so a later ask makes it afresh. One store, and one <see cref="Makings"/>, serve one walker at a time: they are not
/// safe for use from several threads at once.
/// </para>
/// </remarks>
/// <typeparam name="TKey">What is answered.</typeparam>
/// <typeparam name="TAnswer">The answer.</typeparam>
public sealed class MadeOnce<TKey, TAnswer>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TAnswer> _kept;
    private readonly Dictionary<TKey, Making> _open;
    private readonly Dictionary<TKey, (TAnswer Answer, Making Leans)> _held;

    /// <summary>A store of answers, none made yet.</summary>
    /// <param name="comparer">What makes two keys one; the key's own equality where none is given.</param>
    public MadeOnce(IEqualityComparer<TKey>? comparer = null)
    {
        _kept = new Dictionary<TKey, TAnswer>(comparer);
        _open = new Dictionary<TKey, Making>(comparer);
        _held = new Dictionary<TKey, (TAnswer Answer, Making Leans)>(comparer);
    }

    /// <summary>Whether <paramref name="key"/>'s answer is kept, and that answer.</summary>
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

        if (Known(makings, key, out var known))
        {
            return known;
        }

        if (Met(makings, key))
        {
            return none;
        }

        Begin(makings, key);

        TAnswer answer;

        try
        {
            answer = make();
        }
        catch
        {
            Abandon(makings, key);
            throw;
        }

        End(makings, key, answer);
        return answer;
    }

    /// <summary>Forgets every answer: what was made while what it was made from was otherwise. Asked between makings, never inside one.</summary>
    public void Clear()
    {
        _kept.Clear();
        _held.Clear();
    }

    /// <summary>
    /// Whether <paramref name="key"/> has an answer the making under way may read, and that answer: the one kept, else the one held
    /// for a making still under way - which the making under way then leans on too.
    /// </summary>
    internal bool Known(Makings makings, TKey key, out TAnswer answer)
    {
        if (_kept.TryGetValue(key, out answer!))
        {
            return true;
        }

        if (_held.TryGetValue(key, out var held))
        {
            makings.Read(held.Leans);
            answer = held.Answer;
            return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="key"/> is being made, further out: a cycle, whose placeholder the making under way reads.</summary>
    internal bool Met(Makings makings, TKey key)
    {
        if (!_open.TryGetValue(key, out var making))
        {
            return false;
        }

        makings.Read(making);
        return true;
    }

    /// <summary>Begins <paramref name="key"/>'s making, inside the one under way.</summary>
    internal void Begin(Makings makings, TKey key) => _open.Add(key, makings.Begin());

    /// <summary>Ends <paramref name="key"/>'s making with <paramref name="answer"/>: kept where it leans on no making further out, else held for the one it leans on.</summary>
    internal void End(Makings makings, TKey key, TAnswer answer)
    {
        _open.Remove(key);

        if (makings.End() is { } leans)
        {
            _held[key] = (answer, leans);
            leans.WhenEnded(() => _held.Remove(key));
        }
        else
        {
            _kept[key] = answer;
        }
    }

    /// <summary>Ends <paramref name="key"/>'s making unanswered, its making having thrown.</summary>
    internal void Abandon(Makings makings, TKey key)
    {
        _open.Remove(key);
        makings.Abandon();
    }
}

/// <summary>
/// The makings under way of the stores that share it (<see cref="MadeOnce{TKey, TAnswer}"/>), from the outermost in: each with
/// the outermost making it read a placeholder of - its key met again, or an answer held for one - and what to forget once it
/// ends kept. Shared by every store whose makings ask the others, so a cycle through several stores is told as one through one.
/// </summary>
/// <remarks>Not safe for use from several threads at once, as the stores that share it are not.</remarks>
public sealed class Makings
{
    private readonly List<Making> _open = [];

    /// <summary>Begins a making, inside the one under way.</summary>
    internal Making Begin()
    {
        var making = new Making(_open.Count);
        _open.Add(making);
        return making;
    }

    /// <summary>
    /// Ends the innermost making: where it leans on a making further out, the one it leans on - which what leaned on it leans on now,
    /// and which the making it is inside leans on too; else none, what leaned on it forgotten.
    /// </summary>
    internal Making? End()
    {
        var making = Pop();

        if (making.Leans < making.Depth)
        {
            var leans = _open[making.Leans];
            leans.Hand(making);
            Read(making.Leans);
            return leans;
        }

        making.Forget();
        return null;
    }

    /// <summary>Ends the innermost making unanswered: what leaned on it forgotten.</summary>
    internal void Abandon() => Pop().Forget();

    /// <summary>
    /// The making under way read what <paramref name="making"/> - or the open making it was handed to, as it ended leaning on one -
    /// leaves unknown until it ends: its own answer leans on that one too.
    /// </summary>
    internal void Read(Making making) => Read(making.Open().Depth);

    /// <summary>The making under way read what the one at <paramref name="depth"/> leaves unknown until it ends.</summary>
    private void Read(int depth)
    {
        if (_open.Count > 0 && depth < _open[^1].Leans)
        {
            _open[^1].Leans = depth;
        }
    }

    private Making Pop()
    {
        var making = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        return making;
    }
}

/// <summary>
/// One making (<see cref="Makings"/>): where it stands among those under way, the outermost one whose placeholder it read, what to
/// forget once it ends kept, and - once it ended leaning further out - the making it was handed to, with those handed to it.
/// </summary>
internal sealed class Making(int depth)
{
    private readonly List<Action> _forgets = [];
    private readonly List<Making> _handed = [];
    private Making? _to;

    /// <summary>How many makings were under way when it began: its place among them.</summary>
    public int Depth { get; } = depth;

    /// <summary>The depth of the outermost making whose placeholder it read, directly or through an answer held for one; none where it read none.</summary>
    public int Leans { get; set; } = int.MaxValue;

    /// <summary>Forgets what <paramref name="forget"/> forgets once this making, or the one it is handed to, ends kept.</summary>
    public void WhenEnded(Action forget) => _forgets.Add(forget);

    /// <summary>Takes <paramref name="ended"/>, which ended leaning on this one: what leaned on it leans on this one now.</summary>
    public void Hand(Making ended)
    {
        ended._to = this;
        _handed.Add(ended);
    }

    /// <summary>The making under way this one stands for: itself while open, else the one it was handed to, followed to one still open.</summary>
    public Making Open()
    {
        var at = this;

        while (at._to is { } to)
        {
            at = to;
        }

        // Each making on the way is handed straight to the open one, so no later look walks the way again.
        for (var on = this; on._to is { } to && !ReferenceEquals(to, at); on = to)
        {
            on._to = at;
        }

        return at;
    }

    /// <summary>Forgets what was held for it and for every making handed to it, each once, on a stack of its own.</summary>
    public void Forget()
    {
        var pending = new Stack<Making>([this]);

        while (pending.TryPop(out var making))
        {
            foreach (var forget in making._forgets)
            {
                forget();
            }

            foreach (var handed in making._handed)
            {
                pending.Push(handed);
            }
        }
    }
}
