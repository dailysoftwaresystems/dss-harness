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
/// answer kept that a placeholder reached - and every answer held for it that read its placeholder, directly or through an
/// answer, is forgotten, to be made anew, from that kept answer, at its next ask. The rest lean only on keys met inside it:
/// they are set aside under the key met inside it that they lean on - each making handed to it, with all that leans on that
/// making - and taken back by that key's next making, given there as they were, made from the placeholders that making gives
/// them again; so the keys of a cycle are not made again for each of them asked after it, and none of them is kept. A key made
/// anew while set aside voids the set it was set aside in, for what was made from its old answer is no longer made from its
/// answer.
/// </para>
/// <para>
/// A making is the caller's code, and asks on the caller's stack; a making that throws leaves its key unmade, forgets what was
/// held for it and sets the rest aside, so a later ask makes it afresh. One store, and one <see cref="Makings"/>, serve one
/// walker at a time: they are not safe for use from several threads at once.
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
    private readonly Dictionary<TKey, Making> _setAside;

    /// <summary>A store of answers, none made yet.</summary>
    /// <param name="comparer">What makes two keys one; the key's own equality where none is given.</param>
    public MadeOnce(IEqualityComparer<TKey>? comparer = null)
    {
        _kept = new Dictionary<TKey, TAnswer>(comparer);
        _open = new Dictionary<TKey, Making>(comparer);
        _held = new Dictionary<TKey, (TAnswer Answer, Making Leans)>(comparer);
        _setAside = new Dictionary<TKey, Making>(comparer);
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
        _setAside.Clear();
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

        if (_held.TryGetValue(key, out var held) && makings.Read(held.Leans))
        {
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

    /// <summary>
    /// Begins <paramref name="key"/>'s making, inside the one under way: the set its answer held while set aside was in voided, and
    /// what was set aside under it taken back.
    /// </summary>
    internal void Begin(Makings makings, TKey key)
    {
        var making = makings.Begin(set => _setAside[key] = set);

        if (_held.Remove(key, out var stale))
        {
            stale.Leans.Void();
        }

        if (_setAside.Remove(key, out var aside))
        {
            making.TakeBack(aside);
        }

        _open.Add(key, making);
    }

    /// <summary>Ends <paramref name="key"/>'s making with <paramref name="answer"/>: kept where it leans on no making further out, else held for the one it leans on.</summary>
    internal void End(Makings makings, TKey key, TAnswer answer)
    {
        _open.Remove(key);

        if (makings.End() is not { } leans)
        {
            _kept[key] = answer;
            return;
        }

        _held[key] = (answer, leans);
        leans.WhenEnded(() =>
        {
            if (_held.TryGetValue(key, out var held) && ReferenceEquals(held.Leans, leans))
            {
                _held.Remove(key);
            }
        });
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
/// the outermost making it read a placeholder of - its key met again, or an answer held for one - what to forget once it ends
/// kept and what to set aside then. Shared by every store whose makings ask the others, so a cycle through several stores is told
/// as one through one.
/// </summary>
/// <remarks>Not safe for use from several threads at once, as the stores that share it are not.</remarks>
public sealed class Makings
{
    private readonly List<Making> _open = [];

    /// <summary>Begins a making, inside the one under way; <paramref name="setAside"/> sets it aside under its key.</summary>
    internal Making Begin(Action<Making> setAside)
    {
        var making = new Making(_open.Count, setAside);
        _open.Add(making);
        return making;
    }

    /// <summary>
    /// Ends the innermost making: where it leans on a making further out, the one it leans on - which what leaned on it leans on now,
    /// and which the making it is inside leans on too; else none, what read its placeholder forgotten and the rest set aside.
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

        making.Close();
        return null;
    }

    /// <summary>Ends the innermost making unanswered: what read its placeholder forgotten, the rest set aside.</summary>
    internal void Abandon() => Pop().Close();

    /// <summary>
    /// Whether what <paramref name="making"/> leaves unknown until it ends - it, or the making it was handed to or taken back by, as
    /// it ended or was set aside - is under way; the making under way read it then, and its own answer leans on that one too.
    /// </summary>
    internal bool Read(Making making)
    {
        if (making.UnderWay() is not { } open)
        {
            return false;
        }

        Read(open.Depth);
        return true;
    }

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
        making.Ended();
        return making;
    }
}

/// <summary>
/// One making (<see cref="Makings"/>): where it stands among those under way, the outermost one whose placeholder it read and what
/// to forget once it ends kept; once it ended leaning further out, the making it was handed to, with those handed to it and
/// taken back by it - a tree whose root is under way, or set aside, as its root is.
/// </summary>
/// <remarks>
/// Below a making that ended the tree never changes; a root gains makings and loses them. So each making, as it ends, joins the
/// line of the largest making below it - one owner of the line's top for every making on it - and the root of any making is
/// found by a jump to its line's top and a step up, again and again: a step up leaves a line whose makings are at most half its
/// top's, so a root is as many steps away as the tree's size halves, never as many as it is deep.
/// </remarks>
internal sealed class Making(int depth, Action<Making> setAside)
{
    private readonly List<Action> _forgets = [];
    private readonly List<Making> _below = [];
    private Making? _above;
    private Line? _line;
    private bool _open = true;
    private bool _takenBack;
    private bool _void;
    private int _size = 1;

    /// <summary>How many makings were under way when it began: its place among them.</summary>
    public int Depth { get; } = depth;

    /// <summary>The depth of the outermost making whose placeholder it read, directly or through an answer held for one; none where it read none.</summary>
    public int Leans { get; set; } = int.MaxValue;

    /// <summary>Its making is over.</summary>
    public void Ended() => _open = false;

    /// <summary>Forgets what <paramref name="forget"/> forgets once this making, or the one it is handed to, ends kept.</summary>
    public void WhenEnded(Action forget) => _forgets.Add(forget);

    /// <summary>Takes <paramref name="ended"/>, which ended leaning on this one: what leaned on it leans on this one now.</summary>
    public void Hand(Making ended)
    {
        Below(ended);
        ended.Join();
    }

    /// <summary>
    /// Takes back <paramref name="aside"/>, set aside under this making's key: what leans on it leans on this one again, and it ends
    /// with this one - read its placeholder, the same key's.
    /// </summary>
    public void TakeBack(Making aside)
    {
        if (aside._void)
        {
            return;
        }

        aside._takenBack = true;
        Below(aside);
    }

    /// <summary>The making under way this one stands for: itself while under way, else its tree's root while that is; none while set aside.</summary>
    public Making? UnderWay()
    {
        for (var at = this; ;)
        {
            if (at._open)
            {
                return at;
            }

            if (at.Top()._above is not { } above)
            {
                return null;
            }

            at = above;
        }
    }

    /// <summary>Voids the set this making is set aside in: what leans on it is never taken back.</summary>
    public void Void()
    {
        var at = this;

        while (!at._open && at.Top()._above is { } above)
        {
            at = above;
        }

        if (!at._open)
        {
            at.Top()._void = true;
        }
    }

    /// <summary>
    /// Ends it kept, or unmade: forgets what was held for it and for each making taken back by it - the same key's, whose
    /// placeholder those read - and sets every other making handed to them aside under its key, with all that leans on it, on a
    /// stack of its own.
    /// </summary>
    public void Close()
    {
        var closing = new Stack<Making>([this]);

        while (closing.TryPop(out var making))
        {
            foreach (var forget in making._forgets)
            {
                forget();
            }

            foreach (var below in making._below)
            {
                if (below._line is { } line && ReferenceEquals(line, making._line))
                {
                    line.Top = below;
                }

                if (below._takenBack)
                {
                    closing.Push(below);
                    continue;
                }

                below._above = null;
                below.SetAside();
            }

            making._forgets.Clear();
            making._below.Clear();
            making._above = null;
            making._line = null;
        }
    }

    private void SetAside() => setAside(this);

    private void Below(Making making)
    {
        making._above = this;
        _below.Add(making);
    }

    /// <summary>Joins the line of the largest making below it, as its top, or begins one.</summary>
    private void Join()
    {
        Making? largest = null;

        foreach (var below in _below)
        {
            _size += below._size;

            if (largest is null || below._size > largest._size)
            {
                largest = below;
            }
        }

        _line = largest?._line ?? new Line();
        _line.Top = this;
    }

    private Making Top() => _line?.Top ?? this;

    /// <summary>Makings that ended each the largest below the next: the top's tree holds theirs.</summary>
    private sealed class Line
    {
        public Making Top { get; set; } = null!;
    }
}
