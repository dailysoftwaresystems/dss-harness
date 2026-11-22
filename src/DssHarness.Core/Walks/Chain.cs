namespace DssHarness.Core.Walks;

/// <summary>
/// Follows a chain - a link to the next, and that link's to the next - to its end: the chain's own end, where a link
/// says it has no next, or its first revisit, where a link leads back to one followed before. No count ends it: every
/// member it has followed is kept by its key, so a loop ends where it closes and nowhere else, and a chain however long
/// is followed whole, in time and memory linear in its members.
/// </summary>
public static class Chain
{
    /// <summary>The chain from <paramref name="start"/>, followed through <paramref name="next"/> to its end or its first revisit.</summary>
    /// <typeparam name="T">The chain's members.</typeparam>
    /// <typeparam name="TKey">What tells two members apart.</typeparam>
    /// <param name="start">The first member.</param>
    /// <param name="next">What a member leads to: the next member, or the chain's end.</param>
    /// <param name="keyOf">A member's key.</param>
    /// <param name="comparer">What makes two keys one; the key's own equality where none is given.</param>
    public static ChainEnd<T> Follow<T, TKey>(T start, Func<T, ChainNext<T>> next, Func<T, TKey> keyOf, IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(keyOf);

        var followed = new ChainFollowed<T, TKey>(start, keyOf, comparer);

        while (true)
        {
            if (followed.Take(next(followed.Last)) is { } end)
            {
                return end;
            }
        }
    }

    /// <summary>
    /// The chain from <paramref name="start"/>, followed as <see cref="Follow"/> follows one, through a
    /// <paramref name="next"/> that waits - a redirect fetched, a parent read - and told to <paramref name="followed"/> as
    /// it grows.
    /// </summary>
    /// <typeparam name="T">The chain's members.</typeparam>
    /// <typeparam name="TKey">What tells two members apart, by its own equality.</typeparam>
    /// <param name="start">The first member.</param>
    /// <param name="next">What a member leads to: the next member, or the chain's end.</param>
    /// <param name="keyOf">A member's key.</param>
    /// <param name="followed">
    /// Told how many members the chain holds each time one joins it, the start's 1 first, so a long chain can say its
    /// progress; <see langword="null"/> where nothing is told.
    /// </param>
    /// <param name="cancellationToken">Stops the following before the next member is asked for, and is handed to <paramref name="next"/>.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the chain ended.</exception>
    public static async Task<ChainEnd<T>> FollowAsync<T, TKey>(T start, Func<T, CancellationToken, Task<ChainNext<T>>> next, Func<T, TKey> keyOf, Action<int>? followed, CancellationToken cancellationToken)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(keyOf);

        var chain = new ChainFollowed<T, TKey>(start, keyOf, null);
        followed?.Invoke(chain.Count);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chain.Take(await next(chain.Last, cancellationToken).ConfigureAwait(false)) is { } end)
            {
                return end;
            }

            followed?.Invoke(chain.Count);
        }
    }
}

/// <summary>
/// What a chain's member leads to (<see cref="Chain"/>): the next member (<see cref="To"/>), or the chain's end
/// (<see cref="End"/>, also the type's default).
/// </summary>
/// <typeparam name="T">The chain's members.</typeparam>
public readonly record struct ChainNext<T>
{
    /// <summary>The next member is <paramref name="link"/>.</summary>
    private ChainNext(T link)
    {
        Link = link;
        GoesOn = true;
    }

    /// <summary>The chain ends here: the member leads to none.</summary>
    public static ChainNext<T> End => default;

    /// <summary>Whether the chain goes on, to <see cref="Link"/>; false at its end.</summary>
    public bool GoesOn { get; }

    /// <summary>The next member, where the chain goes on; the type's default at its end.</summary>
    public T? Link { get; }

    /// <summary>The chain goes on to <paramref name="link"/>.</summary>
    /// <param name="link">The next member.</param>
    public static ChainNext<T> To(T link) => new(link);
}

/// <summary>How a chain ended (<see cref="Chain"/>): its members, and where it closed on itself if it did.</summary>
/// <param name="Members">Every member followed, the start first, each once.</param>
/// <param name="RepeatedAt">
/// The link that led back to a member followed before, where the chain closed on itself; the type's default where it
/// ended.
/// </param>
/// <param name="Cycled">Whether the chain closed on itself rather than ended.</param>
public sealed record ChainEnd<T>(IReadOnlyList<T> Members, T? RepeatedAt, bool Cycled);

/// <summary>
/// A chain as far as it is followed (<see cref="Chain"/>): its members in order and the key of each - the one place the
/// rule of a chain's end is kept, for the following that waits and the one that does not.
/// </summary>
/// <typeparam name="T">The chain's members.</typeparam>
/// <typeparam name="TKey">What tells two members apart.</typeparam>
internal sealed class ChainFollowed<T, TKey>
    where TKey : notnull
{
    private readonly List<T> _members;
    private readonly HashSet<TKey> _keys;
    private readonly Func<T, TKey> _keyOf;

    /// <summary>A chain of <paramref name="start"/> alone.</summary>
    public ChainFollowed(T start, Func<T, TKey> keyOf, IEqualityComparer<TKey>? comparer)
    {
        _keyOf = keyOf;
        _members = [start];
        _keys = new HashSet<TKey>(comparer) { keyOf(start) };
    }

    /// <summary>The member followed last, whose next one is asked for.</summary>
    public T Last => _members[^1];

    /// <summary>How many members the chain holds.</summary>
    public int Count => _members.Count;

    /// <summary>
    /// Takes the step <see cref="Last"/> leads to: the chain's end where it ends there or leads back to a member followed
    /// before, and <see langword="null"/> where its next member joins it.
    /// </summary>
    public ChainEnd<T>? Take(ChainNext<T> step)
    {
        if (!step.GoesOn)
        {
            return new ChainEnd<T>(_members, default, Cycled: false);
        }

        var link = step.Link!;

        if (!_keys.Add(_keyOf(link)))
        {
            return new ChainEnd<T>(_members, link, Cycled: true);
        }

        _members.Add(link);
        return null;
    }
}
