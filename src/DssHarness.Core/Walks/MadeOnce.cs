namespace DssHarness.Core.Walks;

/// <summary>
/// Answers made once each, where making one asks for others - of this store or of any store that shares its
/// <see cref="Makings"/>, a <see cref="Memo{TKey, TAnswer}"/> given them among them - as it is made: the dependencies are found by
/// the asking, and the makings are the graph they make. The answer a key keeps does not depend on which key of it is asked first,
/// and the work is linear in the makings and the reads.
/// </summary>
/// <remarks>
/// <para>
/// A key asked again while it is being made - directly or through any store that shares the makings - is a cycle. Every key of a
/// strongly connected set of makings that holds a cycle answers its placeholder - nothing known, what the store is given as
/// <c>none</c> - with whatever its store keeps beside it of what the whole set read; every read of such a key, while the set is
/// made and after, is given that answer. Every other key keeps the answer its making made. Each key is made once, each set is
/// closed once as its outermost making ends, and nothing kept is ever made again.
/// </para>
/// <para>
/// So every read gives a key's final answer, and each making runs as it would were every final answer known: no kept answer is
/// made from a placeholder where its key's own answer stands. That holds while what a store keeps beside a cyclic key's placeholder
/// - <c>cycled</c> - agrees with the placeholder in all a making reads to decide by: beside the placeholder a store may keep what
/// the set read, never say otherwise of the key than the placeholder does.
/// </para>
/// <para>
/// The sets are found as they close, on the making stack alone (<see cref="Makings"/>): each making is given its place among all the
/// makings ever begun, and keeps the least place of those the makings inside it reached back to, so a making that reached no further
/// out than itself is the outermost of the set of every making begun since it that closed none.
/// </para>
/// <para>
/// A making is the caller's code, and asks on the caller's stack. One that throws leaves its key unmade, and unmade with it every
/// making begun inside it whose set has not closed - a later ask makes each afresh, never from an answer made while the thrower
/// ran - while what closed inside it stays kept, having read nothing of it. A <c>cycled</c> that throws as a set closes unmade
/// the whole set, those its store kept before it with those it had not, and the exception goes on the same way. One store, and
/// one <see cref="Makings"/>, serve one walker at a time: they are not safe for use from several threads at once.
/// </para>
/// </remarks>
/// <typeparam name="TKey">What is answered.</typeparam>
/// <typeparam name="TAnswer">The answer.</typeparam>
public sealed class MadeOnce<TKey, TAnswer>
    where TKey : notnull
{
    private readonly Func<TKey, TAnswer> _none;
    private readonly Func<TKey, TAnswer, Cycle, TAnswer> _cycled;
    private readonly Dictionary<TKey, TAnswer> _kept;
    private readonly Dictionary<TKey, Making<TKey, TAnswer>> _making;

    /// <summary>A store of answers, none made yet.</summary>
    /// <param name="none">What is known of a key while it is being made, and kept for a key on a cycle: nothing.</param>
    /// <param name="cycled">
    /// What a key of a cycle is kept, from the answer its making made and the cycle; the placeholder where none is given. It must
    /// agree with the placeholder in all a making reads to decide by, and is asked once for each member of a set as the set closes.
    /// </param>
    /// <param name="comparer">What makes two keys one; the key's own equality where none is given.</param>
    public MadeOnce(Func<TKey, TAnswer> none, Func<TKey, TAnswer, Cycle, TAnswer>? cycled = null, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(none);

        _none = none;
        _cycled = cycled ?? ((key, _, _) => none(key));
        _kept = new Dictionary<TKey, TAnswer>(comparer);
        _making = new Dictionary<TKey, Making<TKey, TAnswer>>(comparer);
    }

    /// <summary>Whether <paramref name="key"/>'s answer is kept - final, made or a cycle's - and that answer.</summary>
    /// <param name="key">The key.</param>
    /// <param name="answer">Its kept answer, where it has one.</param>
    public bool TryKept(TKey key, out TAnswer answer) => _kept.TryGetValue(key, out answer!);

    /// <summary>
    /// <paramref name="key"/>'s final answer: the one kept, else - where the key is being made, or made and its set not closed
    /// yet - the placeholder, else made now by <paramref name="make"/>, which may ask for any key of any store
    /// <paramref name="makings"/> serves.
    /// </summary>
    /// <param name="makings">The makings under way of every store whose makings ask this one's.</param>
    /// <param name="key">The key.</param>
    /// <param name="make">The key's making.</param>
    public TAnswer Of(Makings makings, TKey key, Func<TAnswer> make)
    {
        ArgumentNullException.ThrowIfNull(makings);
        ArgumentNullException.ThrowIfNull(make);

        if (TryKept(key, out var kept))
        {
            return kept;
        }

        if (Met(makings, key))
        {
            return _none(key);
        }

        var making = Begin(makings, key);
        TAnswer made;

        try
        {
            made = make();
        }
        catch
        {
            Abandon(makings, making);
            throw;
        }

        return End(makings, making, made);
    }

    /// <summary>
    /// Forgets every answer kept, that each be made again at its next ask. Asked between makings only, never inside one: a key
    /// being made is none of a store's to forget.
    /// </summary>
    public void Clear() => _kept.Clear();

    /// <summary>
    /// Whether <paramref name="key"/> is being made, or made and its set not closed yet: a cycle, whose placeholder the making
    /// under way reads.
    /// </summary>
    /// <param name="makings">The makings under way.</param>
    /// <param name="key">The key.</param>
    internal bool Met(Makings makings, TKey key)
    {
        if (!_making.TryGetValue(key, out var making))
        {
            return false;
        }

        makings.Read(making);
        return true;
    }

    /// <summary>Begins <paramref name="key"/>'s making inside the one under way: until it ends, its key is neither kept nor known.</summary>
    /// <param name="makings">The makings under way.</param>
    /// <param name="key">The key.</param>
    internal Making<TKey, TAnswer> Begin(Makings makings, TKey key)
    {
        var making = new Making<TKey, TAnswer>(this, key);
        _making.Add(key, making);
        makings.Begin(making);
        return making;
    }

    /// <summary>
    /// Ends <paramref name="making"/>, which made <paramref name="made"/>: the answer it keeps where its set closed with it - as
    /// made where the set holds no cycle, else what <c>cycled</c> keeps - and, where its set has not closed, the placeholder; the
    /// one given is what the asker reads.
    /// </summary>
    /// <param name="makings">The makings under way.</param>
    /// <param name="making">The making, the one under way.</param>
    /// <param name="made">The answer it made.</param>
    internal TAnswer End(Makings makings, Making<TKey, TAnswer> making, TAnswer made)
    {
        making.Made = made;
        return makings.End(making) ? making.Kept : _none(making.Key);
    }

    /// <summary>Ends <paramref name="making"/> unanswered, it having thrown: its key is neither kept nor being made.</summary>
    /// <param name="makings">The makings under way.</param>
    /// <param name="making">The making that threw, the one under way.</param>
    internal void Abandon(Makings makings, Making<TKey, TAnswer> making) => makings.Abandon(making);

    /// <summary>
    /// Keeps what <paramref name="making"/> made, its set closing: as made where the set is the making alone and holds no
    /// cycle (<see langword="null"/>), else as <c>cycled</c> keeps a member of <paramref name="cycle"/>.
    /// </summary>
    /// <param name="making">The making, ended.</param>
    /// <param name="cycle">The cycle its set holds; null where it holds none.</param>
    internal void Keep(Making<TKey, TAnswer> making, Cycle? cycle)
    {
        making.Kept = cycle is null ? making.Made : _cycled(making.Key, making.Made, cycle);
        _kept[making.Key] = making.Kept;
        _making.Remove(making.Key);
    }

    /// <summary>
    /// Unmakes <paramref name="making"/>: its key is neither kept nor being made, and is made afresh at its next ask. A making
    /// kept as its set closed and unmade with the set, the store having refused one of it, is forgotten the same.
    /// </summary>
    /// <param name="making">The making to unmake.</param>
    internal void Unmake(Making<TKey, TAnswer> making)
    {
        _kept.Remove(making.Key);
        _making.Remove(making.Key);
    }
}

/// <summary>
/// One strongly connected set of makings that holds a cycle (<see cref="MadeOnce{TKey, TAnswer}"/>), given to every member's store
/// as the set closes: what a store keeps beside a member's placeholder may be one thing the members share, made by the first of
/// them to ask for it.
/// </summary>
public sealed class Cycle
{
    private Dictionary<Type, object>? _shared;

    /// <summary>
    /// The one object of <typeparamref name="T"/> the members of this cycle share: <paramref name="make"/>'s at the first ask, the
    /// same at every later one.
    /// </summary>
    /// <param name="make">Makes it, once.</param>
    /// <typeparam name="T">What is shared.</typeparam>
    public T Shared<T>(Func<T> make)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(make);

        _shared ??= [];

        if (!_shared.TryGetValue(typeof(T), out var shared))
        {
            _shared[typeof(T)] = shared = make();
        }

        return (T)shared;
    }
}

/// <summary>
/// The makings under way of the stores that share it (<see cref="MadeOnce{TKey, TAnswer}"/>), from the outermost in: each with
/// its place among all ever begun and the least place it reached back to, which finds the strongly connected sets as they close,
/// on the making stack alone and with no recursion over the makings. Shared by every store whose makings ask the others, so a
/// cycle through several stores is told as one through one.
/// </summary>
/// <remarks>Not safe for use from several threads at once, as the stores that share it are not.</remarks>
public sealed class Makings
{
    private readonly List<Making> _open = [];
    private readonly List<Making> _unclosed = [];
    private int _next;

    /// <summary>Begins <paramref name="making"/> inside the one under way: its place among all ever begun here, on both stacks.</summary>
    /// <param name="making">The making begun.</param>
    internal void Begin(Making making)
    {
        making.Index = _next++;
        _open.Add(making);
        _unclosed.Add(making);
    }

    /// <summary>
    /// The making under way read <paramref name="making"/>, begun before it and not closed: what reaches that one reaches at
    /// least as far out as its place.
    /// </summary>
    /// <param name="making">The making read, open or ended and unclosed.</param>
    internal void Read(Making making)
    {
        var under = _open[^1];

        if (making.Index < under.Low)
        {
            under.Low = making.Index;
        }
    }

    /// <summary>
    /// Ends <paramref name="making"/>. Where it reached back further out than itself, it stays unclosed on the stack of its set
    /// until that set's outermost making ends, and what it reached is the making under way's reaching now. Else it is the
    /// outermost making of its set: every making begun since it that closed none is its set, and each is kept now - as it was
    /// made where the set holds no cycle, as its store's answer for a member of the cycle where it does. Where a store
    /// refuses to keep one of them, throwing, the whole set is unmade - those kept before the one refused with it, that none
    /// stay kept with what part of the set read - and the exception goes on.
    /// </summary>
    /// <param name="making">The making ended, the one under way.</param>
    /// <returns>Whether <paramref name="making"/>'s set closed with it: its answer is kept from now on.</returns>
    internal bool End(Making making)
    {
        _open.RemoveAt(_open.Count - 1);

        if (making.Low < making.Index)
        {
            // It reached back to a making begun before it, so one is still open above it - the one under way, which asked for
            // it - and what it reached is that one's reaching now.
            var asker = _open[^1];

            if (making.Low < asker.Low)
            {
                asker.Low = making.Low;
            }

            return false;
        }

        // Nothing reached back to it and it reached nothing: it is alone, and holds no cycle.
        Cycle? cycle = making.Low == int.MaxValue ? null : new Cycle();

        // The set is closed from its innermost member down to this one, and none is taken off the stack before all are kept:
        // where a store refuses one, the whole set is still on the stack, kept or not, and is unmade together.
        for (var at = _unclosed.Count - 1; ; at--)
        {
            try
            {
                _unclosed[at].Keep(cycle);
            }
            catch
            {
                UnmakeDownTo(making);
                throw;
            }

            if (ReferenceEquals(_unclosed[at], making))
            {
                // Kept whole: off the stack of the unclosed, all at once.
                _unclosed.RemoveRange(at, _unclosed.Count - at);
                return true;
            }
        }
    }

    /// <summary>Unmakes every making still on the stack of the unclosed down to <paramref name="making"/>, it included.</summary>
    /// <param name="making">The last unmade.</param>
    private void UnmakeDownTo(Making making)
    {
        Making unclosed;

        do
        {
            unclosed = _unclosed[^1];
            _unclosed.RemoveAt(_unclosed.Count - 1);
            unclosed.Unmake();
        }
        while (!ReferenceEquals(unclosed, making));
    }

    /// <summary>
    /// Ends <paramref name="making"/> unanswered, its making having thrown: it, and every making still unclosed above it - each
    /// begun inside it - are unmade, to be made afresh at their next ask. What closed inside it stays kept, having read nothing
    /// of it; the makings outside it are as they were.
    /// </summary>
    /// <param name="making">The making that threw, the one under way.</param>
    internal void Abandon(Making making)
    {
        _open.RemoveAt(_open.Count - 1);
        UnmakeDownTo(making);
    }
}

/// <summary>
/// One making (<see cref="Makings"/>): its place among all the makings begun, the least place of those the makings inside it
/// reached back to, and - its store's own, <see cref="Making{TKey, TAnswer}"/> - the key and the answer it made.
/// </summary>
internal abstract class Making
{
    /// <summary>Its place among all the makings ever begun of its <see cref="Makings"/>, in the order they began: one no other making has.</summary>
    public int Index { get; internal set; }

    /// <summary>
    /// The least place among the makings those made inside this one reached back to, each through the ones made inside it in
    /// turn, and this one reached itself: the outermost making its set may hold, and <see cref="int.MaxValue"/> where none was reached.
    /// </summary>
    public int Low { get; set; } = int.MaxValue;

    /// <summary>
    /// Keeps this making's answer, its set closing: as made where it is alone and holds no cycle (<see langword="null"/>), else as
    /// its store's answer for a member of <paramref name="cycle"/>.
    /// </summary>
    /// <param name="cycle">The cycle its set holds; null where the set is this making alone.</param>
    internal abstract void Keep(Cycle? cycle);

    /// <summary>Unmakes this making: its key is neither kept nor being made, and is made afresh at its next ask.</summary>
    internal abstract void Unmake();
}

/// <summary>One making of one store (<see cref="MadeOnce{TKey, TAnswer}"/>): its key, the answer the making made and the one it kept.</summary>
/// <param name="store">The store whose making it is.</param>
/// <param name="key">The key.</param>
internal sealed class Making<TKey, TAnswer>(MadeOnce<TKey, TAnswer> store, TKey key) : Making
    where TKey : notnull
{
    /// <summary>The key.</summary>
    public TKey Key => key;

    /// <summary>The answer its making made, from what it read.</summary>
    public TAnswer Made { get; set; } = default!;

    /// <summary>The answer it kept, once its set closed.</summary>
    public TAnswer Kept { get; internal set; } = default!;

    /// <inheritdoc/>
    internal override void Keep(Cycle? cycle) => store.Keep(this, cycle);

    /// <inheritdoc/>
    internal override void Unmake() => store.Unmake(this);
}
