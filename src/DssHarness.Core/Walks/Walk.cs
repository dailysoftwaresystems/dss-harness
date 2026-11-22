namespace DssHarness.Core.Walks;

/// <summary>The order a walk meets the nodes it reaches in (<see cref="Walk.Visit"/>).</summary>
public enum WalkOrder
{
    /// <summary>
    /// Depth first, in the order a recursive walk would meet them before it went down: a node, then everything its first
    /// next node reaches, then everything its second reaches that the first did not.
    /// </summary>
    Depth,

    /// <summary>Breadth first, by levels: the starts, then every node one step from them, then every node two steps away.</summary>
    Breadth,
}

/// <summary>
/// The one walk of a graph from its starts: each node it reaches met once, however many ways lead to it and whatever
/// cycles the graph holds, on a stack or a queue of its own - never a recursion, so no depth of the graph reaches past a
/// thread's stack. Its time and memory are linear in the nodes it reaches and the ways between them.
/// </summary>
public static class Walk
{
    /// <summary>
    /// Every node reached from <paramref name="starts"/> through <paramref name="next"/>, each once, in
    /// <paramref name="order"/>: lazily, so a node is asked its next ones only once it has been given and the walk is
    /// asked on, and a caller that stops early asks nothing more.
    /// </summary>
    /// <remarks>
    /// A node is known by its key (<paramref name="keyOf"/>), compared through <paramref name="comparer"/>: the walk keeps
    /// every key it has met, and a node whose key it holds is never met again - what makes a shared node one visit and a
    /// cycle an end. A start met before is passed over as any node is.
    /// </remarks>
    /// <typeparam name="TNode">The nodes.</typeparam>
    /// <typeparam name="TKey">What tells two nodes apart.</typeparam>
    /// <param name="starts">Where the walk begins, in order.</param>
    /// <param name="keyOf">A node's key.</param>
    /// <param name="next">The nodes a node leads to, in order.</param>
    /// <param name="order">Whether the walk goes deep first or by levels.</param>
    /// <param name="comparer">What makes two keys one; the key's own equality where none is given.</param>
    public static IEnumerable<TNode> Visit<TNode, TKey>(
        IEnumerable<TNode> starts,
        Func<TNode, TKey> keyOf,
        Func<TNode, IEnumerable<TNode>> next,
        WalkOrder order = WalkOrder.Depth,
        IEqualityComparer<TKey>? comparer = null)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(keyOf);
        ArgumentNullException.ThrowIfNull(next);

        return order switch
        {
            WalkOrder.Depth => DepthFirst(starts, keyOf, next, comparer),
            WalkOrder.Breadth => BreadthFirst(starts, keyOf, next, comparer),
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, "A walk goes deep first or by levels."),
        };
    }

    /// <summary>
    /// The nodes depth first, on a stack: a node's next ones are pushed last first, so the first is met next, and a node
    /// is met where it is taken off the stack unless its key was met before - the order a recursive walk would meet them
    /// in, which marks a node where it reaches it.
    /// </summary>
    private static IEnumerable<TNode> DepthFirst<TNode, TKey>(IEnumerable<TNode> starts, Func<TNode, TKey> keyOf, Func<TNode, IEnumerable<TNode>> next, IEqualityComparer<TKey>? comparer)
        where TKey : notnull
    {
        var met = new HashSet<TKey>(comparer);
        var waiting = new Stack<TNode>();

        Push(waiting, starts);

        while (waiting.TryPop(out var node))
        {
            if (!met.Add(keyOf(node)))
            {
                continue;
            }

            yield return node;

            Push(waiting, next(node));
        }
    }

    /// <summary>Pushes <paramref name="nodes"/> on <paramref name="waiting"/> last first, so the first of them is taken off next.</summary>
    private static void Push<TNode>(Stack<TNode> waiting, IEnumerable<TNode> nodes)
    {
        var listed = nodes as IReadOnlyList<TNode> ?? [.. nodes];

        for (var index = listed.Count - 1; index >= 0; index--)
        {
            waiting.Push(listed[index]);
        }
    }

    /// <summary>
    /// The nodes by levels, on a queue: a node's key is kept where the node joins the queue, so it joins once, at the
    /// first level that reaches it.
    /// </summary>
    private static IEnumerable<TNode> BreadthFirst<TNode, TKey>(IEnumerable<TNode> starts, Func<TNode, TKey> keyOf, Func<TNode, IEnumerable<TNode>> next, IEqualityComparer<TKey>? comparer)
        where TKey : notnull
    {
        var met = new HashSet<TKey>(comparer);
        var waiting = new Queue<TNode>();

        foreach (var start in starts)
        {
            if (met.Add(keyOf(start)))
            {
                waiting.Enqueue(start);
            }
        }

        while (waiting.TryDequeue(out var node))
        {
            yield return node;

            foreach (var reached in next(node))
            {
                if (met.Add(keyOf(reached)))
                {
                    waiting.Enqueue(reached);
                }
            }
        }
    }
}
