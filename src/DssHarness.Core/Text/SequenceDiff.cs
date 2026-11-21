using System.Runtime.InteropServices;

namespace DssHarness.Core.Text;

/// <summary>
/// The one diff of two sequences: the runs, in order, in which a newer sequence keeps an older one's items and in which it
/// changes them. No bound is set on the edits and nothing is taken as changed whole for what finding it would cost; the
/// memory is linear in the two sequences, and no depth of either is recursed through.
/// </summary>
/// <remarks>
/// <para>
/// Every item is hashed once, through the comparer, and stood for by a number its equals share; a null item stands for
/// itself, equal to a null alone, and is never handed to the comparer, whose hash of a null may throw. Everything after
/// compares numbers.
/// </para>
/// <para>
/// Anchors first. A work stack of regions - explicit, never a recursion - begins with the whole of both sides. A region's
/// common prefix and suffix are taken as equal first. In what remains, the items that occur exactly once in its before and
/// exactly once in its after are its anchors: the lines a reader would align two texts by. The longest run of anchors in
/// one order on both sides, their longest increasing subsequence found by patience sorting, splits the region: each
/// anchor is extended to the longest equal run around it, and each region between two of them, with items on both of its
/// sides, is pushed on the stack to be anchored in its turn, where an item repeated in the whole may be once in the part.
/// </para>
/// <para>
/// A region with no anchor is diffed by Myers' linear-space algorithm. The items of either side that its other side does
/// not hold are set aside first, since no common subsequence can hold them; between the rest the middle snake - the
/// forward and backward walks of the fewest edits, run until they meet - splits each part in two, the halves pushed on a
/// stack of its own, never a recursion. Its answer is a longest common subsequence of the region, so no unequal run's two
/// sides share an item, and a diff that meets no anchor is the shortest there is. Where anchors split a region, the diff
/// follows them over a longer common subsequence that would align repeated items instead: a moved block is told moved
/// along its distinct lines, not kept along its braces.
/// </para>
/// <para>
/// The numbering, and each region's prefix, suffix, counts and extensions, take time linear in what they read, and the
/// anchors k log k in their number. Myers takes (N + M) D for a region of N and M items D edits apart. Where regions nest
/// as deep as the input is long, or many edits fall between many recurring items, the time grows as the square of the
/// input: the price of the shortest script, which no bound cuts short. Memory is linear in the input throughout.
/// </para>
/// </remarks>
public static class SequenceDiff
{
    /// <summary>
    /// The runs in which <paramref name="after"/> keeps and changes <paramref name="before"/>'s items: in order, covering
    /// both sides exactly, none empty, no two of a kind side by side. An equal run's items are equal pairwise; an unequal
    /// run's two sides share no item.
    /// </summary>
    /// <typeparam name="T">The items' type.</typeparam>
    /// <param name="before">The older sequence.</param>
    /// <param name="after">The newer sequence.</param>
    /// <param name="comparer">What makes two items equal; the type's default where none is given.</param>
    public static IReadOnlyList<DiffRun> Of<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var numbers = new Dictionary<Item<T>, int>(new ItemComparer<T>(comparer ?? EqualityComparer<T>.Default));
        var kinds = 0;
        var nullKind = -1;

        int Number(T item)
        {
            if (item is null)
            {
                return nullKind >= 0 ? nullKind : nullKind = kinds++;
            }

            ref var kind = ref CollectionsMarshal.GetValueRefOrAddDefault(numbers, new Item<T>(item), out var known);

            if (!known)
            {
                kind = kinds++;
            }

            return kind;
        }

        var older = new int[before.Count];
        var newer = new int[after.Count];

        for (var index = 0; index < older.Length; index++)
        {
            older[index] = Number(before[index]);
        }

        for (var index = 0; index < newer.Length; index++)
        {
            newer[index] = Number(after[index]);
        }

        return new Differ(older, newer, kinds).Runs();
    }

    /// <summary>An item as a key of the numbering, so an item of any type, a nullable one among them, can be one.</summary>
    private readonly struct Item<T>(T value)
    {
        /// <summary>The item.</summary>
        public T Value { get; } = value;
    }

    /// <summary>Two items' equality, and an item's hash, as the comparer given says; never asked of a null.</summary>
    private sealed class ItemComparer<T>(IEqualityComparer<T> comparer) : IEqualityComparer<Item<T>>
    {
        /// <inheritdoc/>
        public bool Equals(Item<T> x, Item<T> y) => comparer.Equals(x.Value, y.Value);

        /// <inheritdoc/>
        public int GetHashCode(Item<T> obj) => comparer.GetHashCode(obj.Value!);
    }

    /// <summary>
    /// A piece of work on a stack: a region of both sides still to diff, or a run of items already found equal, waiting
    /// for what precedes it to be told first.
    /// </summary>
    /// <param name="Region">Whether this is a region to diff; else an equal run.</param>
    /// <param name="Before">The first index on the before side.</param>
    /// <param name="BeforeEnd">The index past the last on the before side.</param>
    /// <param name="After">The first index on the after side.</param>
    /// <param name="AfterEnd">The index past the last on the after side.</param>
    private readonly record struct Work(bool Region, int Before, int BeforeEnd, int After, int AfterEnd)
    {
        /// <summary>A region to diff.</summary>
        public static Work Part(int before, int beforeEnd, int after, int afterEnd) => new(true, before, beforeEnd, after, afterEnd);

        /// <summary>A run of <paramref name="length"/> items found equal.</summary>
        public static Work Same(int before, int after, int length) => new(false, before, before + length, after, after + length);
    }

    /// <summary>
    /// One diff's state: the two sides as numbers and the buffers every region shares, each sized once by the input, so
    /// the memory of a whole diff is linear in it.
    /// </summary>
    private sealed class Differ
    {
        private readonly int[] _before;
        private readonly int[] _after;

        // Per kind, within the region being anchored: how often it occurs on each side, and where on the after side it
        // last does. Set by a region and cleared by it again, so no region pays for another's kinds.
        private readonly int[] _beforeCount;
        private readonly int[] _afterCount;
        private readonly int[] _afterAt;

        // A region's anchors in before's order, and the patience piles their longest increasing subsequence is read from.
        private readonly int[] _anchorBefore;
        private readonly int[] _anchorAfter;
        private readonly int[] _tops;
        private readonly int[] _previous;

        // A region Myers diffs, its items the other side does not hold set aside: where each item kept stands, and its kind.
        private readonly int[] _keptBefore;
        private readonly int[] _keptBeforeKind;
        private readonly int[] _keptAfter;
        private readonly int[] _keptAfterKind;

        // The furthest each walk of the middle snake reaches on each diagonal.
        private readonly int[] _forward;
        private readonly int[] _backward;

        private readonly List<DiffRun> _runs = [];
        private int _nextBefore;
        private int _nextAfter;

        /// <summary>A diff of <paramref name="before"/> and <paramref name="after"/>, whose items are numbers below <paramref name="kinds"/>.</summary>
        public Differ(int[] before, int[] after, int kinds)
        {
            _before = before;
            _after = after;
            _beforeCount = new int[kinds];
            _afterCount = new int[kinds];
            _afterAt = new int[kinds];

            var fewer = Math.Min(before.Length, after.Length);
            _anchorBefore = new int[fewer];
            _anchorAfter = new int[fewer];
            _tops = new int[fewer];
            _previous = new int[fewer];

            _keptBefore = new int[before.Length];
            _keptBeforeKind = new int[before.Length];
            _keptAfter = new int[after.Length];
            _keptAfterKind = new int[after.Length];

            // Every diagonal the walks of the largest region try, and the one beyond either end each reads.
            _forward = new int[before.Length + after.Length + 3];
            _backward = new int[before.Length + after.Length + 3];
        }

        /// <summary>The runs: each region anchored or diffed in the order its items stand, the runs found told as found.</summary>
        public List<DiffRun> Runs()
        {
            var work = new Stack<Work>();
            work.Push(Work.Part(0, _before.Length, 0, _after.Length));

            while (work.TryPop(out var piece))
            {
                if (piece.Region)
                {
                    Anchor(piece, work);
                }
                else
                {
                    TellEqual(piece.Before, piece.After, piece.BeforeEnd - piece.Before);
                }
            }

            if (_nextBefore < _before.Length || _nextAfter < _after.Length)
            {
                _runs.Add(new DiffRun(_nextBefore, _before.Length - _nextBefore, _nextAfter, _after.Length - _nextAfter, false));
            }

            return _runs;
        }

        /// <summary>
        /// Takes <paramref name="region"/>'s common prefix and suffix, then splits what lies between at its anchors, pushing
        /// what is left in reverse so the stack gives it back in order; or, where it has none, has Myers diff it.
        /// </summary>
        private void Anchor(Work region, Stack<Work> work)
        {
            var (before, beforeEnd, after, afterEnd) = (region.Before, region.BeforeEnd, region.After, region.AfterEnd);
            var head = 0;

            while (before + head < beforeEnd && after + head < afterEnd && _before[before + head] == _after[after + head])
            {
                head++;
            }

            TellEqual(before, after, head);
            before += head;
            after += head;

            var tail = 0;

            while (beforeEnd - tail > before && afterEnd - tail > after && _before[beforeEnd - 1 - tail] == _after[afterEnd - 1 - tail])
            {
                tail++;
            }

            beforeEnd -= tail;
            afterEnd -= tail;

            if (tail > 0)
            {
                work.Push(Work.Same(beforeEnd, afterEnd, tail));
            }

            if (before == beforeEnd || after == afterEnd)
            {
                return;
            }

            for (var index = before; index < beforeEnd; index++)
            {
                _beforeCount[_before[index]]++;
            }

            for (var index = after; index < afterEnd; index++)
            {
                _afterCount[_after[index]]++;
                _afterAt[_after[index]] = index;
            }

            var anchors = 0;

            for (var index = before; index < beforeEnd; index++)
            {
                var kind = _before[index];

                if (_beforeCount[kind] == 1 && _afterCount[kind] == 1)
                {
                    _anchorBefore[anchors] = index;
                    _anchorAfter[anchors] = _afterAt[kind];
                    anchors++;
                }
            }

            var keptBefore = 0;
            var keptAfter = 0;

            if (anchors == 0)
            {
                for (var index = before; index < beforeEnd; index++)
                {
                    if (_afterCount[_before[index]] > 0)
                    {
                        _keptBefore[keptBefore] = index;
                        _keptBeforeKind[keptBefore++] = _before[index];
                    }
                }

                for (var index = after; index < afterEnd; index++)
                {
                    if (_beforeCount[_after[index]] > 0)
                    {
                        _keptAfter[keptAfter] = index;
                        _keptAfterKind[keptAfter++] = _after[index];
                    }
                }
            }

            for (var index = before; index < beforeEnd; index++)
            {
                _beforeCount[_before[index]] = 0;
            }

            for (var index = after; index < afterEnd; index++)
            {
                _afterCount[_after[index]] = 0;
            }

            if (anchors > 0)
            {
                Split(anchors, before, beforeEnd, after, afterEnd, work);
            }
            else if (keptBefore > 0 && keptAfter > 0)
            {
                Myers(keptBefore, keptAfter);
            }
        }

        /// <summary>
        /// Splits a region at the longest increasing subsequence of its <paramref name="anchors"/>, read from the last back:
        /// each extended to the longest equal run around it, short of the run after it, and each part between with items on
        /// both sides pushed after the run that follows it, so the stack gives the parts and runs back in order.
        /// </summary>
        private void Split(int anchors, int before, int beforeEnd, int after, int afterEnd, Stack<Work> work)
        {
            // Patience: each anchor is put on the first pile whose top stands later on the after side, and remembers the
            // top of the pile before it; the last pile's top, followed back, is a longest increasing subsequence.
            var piles = 0;

            for (var anchor = 0; anchor < anchors; anchor++)
            {
                var low = 0;
                var high = piles;

                while (low < high)
                {
                    var middle = (low + high) >>> 1;

                    if (_anchorAfter[_tops[middle]] < _anchorAfter[anchor])
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                _previous[anchor] = low > 0 ? _tops[low - 1] : -1;
                _tops[low] = anchor;

                if (low == piles)
                {
                    piles++;
                }
            }

            var nextBefore = beforeEnd;
            var nextAfter = afterEnd;

            for (var anchor = _tops[piles - 1]; anchor >= 0; anchor = _previous[anchor])
            {
                var start = _anchorBefore[anchor];
                var startAfter = _anchorAfter[anchor];

                // Within the run after it, already extended over it: an anchor once on each side can be matched there alone.
                if (start >= nextBefore || startAfter >= nextAfter)
                {
                    continue;
                }

                var end = start + 1;
                var endAfter = startAfter + 1;

                while (end < nextBefore && endAfter < nextAfter && _before[end] == _after[endAfter])
                {
                    end++;
                    endAfter++;
                }

                while (start > before && startAfter > after && _before[start - 1] == _after[startAfter - 1])
                {
                    start--;
                    startAfter--;
                }

                if (end < nextBefore && endAfter < nextAfter)
                {
                    work.Push(Work.Part(end, nextBefore, endAfter, nextAfter));
                }

                work.Push(Work.Same(start, startAfter, end - start));
                nextBefore = start;
                nextAfter = startAfter;
            }

            if (before < nextBefore && after < nextAfter)
            {
                work.Push(Work.Part(before, nextBefore, after, nextAfter));
            }
        }

        /// <summary>
        /// Myers' linear-space diff of the items kept of a region, <paramref name="beforeCount"/> and
        /// <paramref name="afterCount"/> of them: each part's common prefix told, its suffix pushed to follow it, and what
        /// lies between split at the middle snake, the two halves pushed so the stack gives them back in order.
        /// </summary>
        private void Myers(int beforeCount, int afterCount)
        {
            var parts = new Stack<Work>();
            parts.Push(Work.Part(0, beforeCount, 0, afterCount));

            while (parts.TryPop(out var piece))
            {
                var (before, beforeEnd, after, afterEnd) = (piece.Before, piece.BeforeEnd, piece.After, piece.AfterEnd);

                if (!piece.Region)
                {
                    TellKept(before, after, beforeEnd - before);
                    continue;
                }

                var head = 0;

                while (before + head < beforeEnd && after + head < afterEnd && _keptBeforeKind[before + head] == _keptAfterKind[after + head])
                {
                    head++;
                }

                TellKept(before, after, head);
                before += head;
                after += head;

                var tail = 0;

                while (beforeEnd - tail > before && afterEnd - tail > after && _keptBeforeKind[beforeEnd - 1 - tail] == _keptAfterKind[afterEnd - 1 - tail])
                {
                    tail++;
                }

                beforeEnd -= tail;
                afterEnd -= tail;

                if (tail > 0)
                {
                    parts.Push(Work.Same(beforeEnd, afterEnd, tail));
                }

                if (before == beforeEnd || after == afterEnd || MiddleSnake(before, beforeEnd, after, afterEnd) is not { } split)
                {
                    continue;
                }

                parts.Push(Work.Part(split.Before, beforeEnd, split.After, afterEnd));
                parts.Push(Work.Part(before, split.Before, after, split.After));
            }
        }

        /// <summary>
        /// Where a shortest edit script between the kept items of a part - its two sides non-empty, their first items unequal
        /// and their last - passes, as the middle snake finds it: the forward walk from the start and the backward walk from
        /// the end, each taking one more edit a step, until they overlap on a diagonal. Each half of the part on either side
        /// of the point holds at least one edit, so both are smaller than the part. <see langword="null"/> where the two
        /// sides share no item, which the walks prove by never meeting.
        /// </summary>
        private (int Before, int After)? MiddleSnake(int before, int beforeEnd, int after, int afterEnd)
        {
            var count = beforeEnd - before;
            var countAfter = afterEnd - after;
            var most = (count + countAfter + 1) / 2;
            var offset = most;
            var length = (2 * most) + 2;

            Array.Fill(_forward, -1, 0, length);
            Array.Fill(_backward, -1, 0, length);
            _forward[offset + 1] = 0;
            _backward[offset + 1] = 0;

            var delta = count - countAfter;

            // Where the two sides' lengths differ by an odd number, the forward walk is the one that meets the other.
            var forwardMeets = (delta & 1) != 0;

            // How far the diagonals each walk tries are drawn in from either end, once a walk has run off the grid there.
            var forwardStart = 0;
            var forwardEnd = 0;
            var backwardStart = 0;
            var backwardEnd = 0;

            for (var edits = 0; edits < most; edits++)
            {
                for (var diagonal = -edits + forwardStart; diagonal <= edits - forwardEnd; diagonal += 2)
                {
                    var at = offset + diagonal;
                    var x = diagonal == -edits || (diagonal != edits && _forward[at - 1] < _forward[at + 1])
                        ? _forward[at + 1]
                        : _forward[at - 1] + 1;
                    var y = x - diagonal;

                    while (x < count && y < countAfter && _keptBeforeKind[before + x] == _keptAfterKind[after + y])
                    {
                        x++;
                        y++;
                    }

                    _forward[at] = x;

                    if (x > count)
                    {
                        forwardEnd += 2;
                    }
                    else if (y > countAfter)
                    {
                        forwardStart += 2;
                    }
                    else if (forwardMeets)
                    {
                        var mirror = offset + delta - diagonal;

                        if (mirror >= 0 && mirror < length && _backward[mirror] != -1 && x >= count - _backward[mirror])
                        {
                            return (before + x, after + y);
                        }
                    }
                }

                for (var diagonal = -edits + backwardStart; diagonal <= edits - backwardEnd; diagonal += 2)
                {
                    var at = offset + diagonal;
                    var x = diagonal == -edits || (diagonal != edits && _backward[at - 1] < _backward[at + 1])
                        ? _backward[at + 1]
                        : _backward[at - 1] + 1;
                    var y = x - diagonal;

                    while (x < count && y < countAfter && _keptBeforeKind[beforeEnd - 1 - x] == _keptAfterKind[afterEnd - 1 - y])
                    {
                        x++;
                        y++;
                    }

                    _backward[at] = x;

                    if (x > count)
                    {
                        backwardEnd += 2;
                    }
                    else if (y > countAfter)
                    {
                        backwardStart += 2;
                    }
                    else if (!forwardMeets)
                    {
                        var mirror = offset + delta - diagonal;

                        if (mirror >= 0 && mirror < length && _forward[mirror] != -1 && _forward[mirror] >= count - x)
                        {
                            var forwardX = _forward[mirror];

                            return (before + forwardX, after + forwardX - (mirror - offset));
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>Tells <paramref name="length"/> kept items of a Myers region found equal, each where it stands in the whole.</summary>
        private void TellKept(int before, int after, int length)
        {
            for (var index = 0; index < length; index++)
            {
                TellEqual(_keptBefore[before + index], _keptAfter[after + index], 1);
            }
        }

        /// <summary>
        /// Tells <paramref name="length"/> items from <paramref name="before"/> and <paramref name="after"/> found equal, the
        /// next in order: what lies between the last run told and these as one unequal run, and these joined to an equal
        /// run they follow on both sides.
        /// </summary>
        private void TellEqual(int before, int after, int length)
        {
            if (length == 0)
            {
                return;
            }

            if (before > _nextBefore || after > _nextAfter)
            {
                _runs.Add(new DiffRun(_nextBefore, before - _nextBefore, _nextAfter, after - _nextAfter, false));
                _runs.Add(new DiffRun(before, length, after, length, true));
            }
            else if (_runs.Count > 0 && _runs[^1].Equal)
            {
                var last = _runs[^1];
                _runs[^1] = last with { BeforeCount = last.BeforeCount + length, AfterCount = last.AfterCount + length };
            }
            else
            {
                _runs.Add(new DiffRun(before, length, after, length, true));
            }

            _nextBefore = before + length;
            _nextAfter = after + length;
        }
    }
}

/// <summary>
/// A run of a diff: <see cref="BeforeCount"/> items of the older sequence from <see cref="Before"/> against
/// <see cref="AfterCount"/> of the newer from <see cref="After"/>. An equal run's two counts are equal and its items equal
/// pairwise; an unequal run's items were taken out, put in, or both.
/// </summary>
/// <param name="Before">The older sequence's first item of the run, from 0; where the run takes none out, the index of the item it stands before.</param>
/// <param name="BeforeCount">How many of the older sequence's items the run holds; 0 where it only puts items in.</param>
/// <param name="After">The newer sequence's first item of the run, from 0; where the run puts none in, the index of the item it stands before.</param>
/// <param name="AfterCount">How many of the newer sequence's items the run holds; 0 where it only takes items out.</param>
/// <param name="Equal">Whether the run's items are kept, equal pairwise, rather than changed.</param>
public readonly record struct DiffRun(int Before, int BeforeCount, int After, int AfterCount, bool Equal);
