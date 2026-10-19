namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// How one instance of a step is named among a run's instances: the step's name, and each group's around it, with
/// what tells the instances apart - a <c>repeat</c>'s iteration (<c>#</c>, from 1), a <c>forEach</c>'s item
/// (<c>[</c>n<c>]</c>, from 0) and a step's visits (<c>+</c>, from 2) - in one path from the run's root.
/// </summary>
/// <remarks>
/// <para>
/// <c>rounds#2.items[2].fix#3.review.correctness</c> is the <c>correctness</c> step of the <c>review</c> block, in
/// the third iteration of <c>fix</c>, of the third item of <c>items</c>, in the second iteration of
/// <c>rounds</c>. A visit is written only from the second, so every id of a step the run was never sent back to is
/// what it would be without visits. On disk the same id names the instance's directory, with an item's brackets
/// written <c>@</c>: <c>rounds#2.items@2.fix#3.review.correctness</c>. Deterministic, so nothing allocates
/// directory names.
/// </para>
/// <para>
/// Pure text rules, nothing more: the scheduler that counts the iterations, items and visits is its own, and this
/// class only owns how one of its instances is said. A segment carries one mark at most; its name is spelt as a
/// step's is (<see cref="FlowScope.IsWellFormedName"/>) and its count as an index is
/// (<see cref="WorkflowExpressions.WholeNumber"/>), each rule written once.
/// </para>
/// </remarks>
public static class FlowInstanceId
{
    /// <summary>Whether <paramref name="id"/> is an instance id as this class spells one.</summary>
    /// <param name="id">An id, as written in a run's records.</param>
    public static bool IsWellFormed(string id) => Problem(id) is null;

    /// <summary>
    /// Why <paramref name="id"/> is not an instance id, or <see langword="null"/> where it is: a segment is not a
    /// step's name, or an iteration, item or visit is written where nothing takes it or counted from nowhere.
    /// </summary>
    /// <param name="id">An id, as written in a run's records.</param>
    public static string? Problem(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "an id is a step's name, with the groups around it";
        }

        if (id.StartsWith('.') || id.EndsWith('.') || id.Contains(".."))
        {
            return "an id is step names joined by '.', each once";
        }

        foreach (var segment in id.Split('.'))
        {
            // One mark at most: an iteration, an item or a visit. A segment that would need two - a repeat's
            // iteration within a visit after its first - is given no spelling here, and is refused, whichever
            // order its marks are written in, rather than read one way or the other.
            var shape = $"'{segment}': a segment of an id is a step's name, with at most one mark after it";
            var name = segment;
            char mark = '\0';
            var written = string.Empty;

            var hash = segment.IndexOf('#');

            if (hash > 0)
            {
                (name, mark, written) = (segment[..hash], '#', segment[(hash + 1)..]);

                // A mark after the count is a second one; a sign before it is a count written another way.
                if (written.IndexOfAny(['#', '[', '+']) > 0)
                {
                    return shape;
                }
            }
            else if (segment.Contains('[') || segment.EndsWith(']'))
            {
                var open = segment.IndexOf('[');
                var close = open < 0 ? -1 : segment.IndexOf(']', open);

                if (close >= 0 && close < segment.Length - 1)
                {
                    return shape;
                }

                if (open <= 0 || !segment.EndsWith(']'))
                {
                    return $"'{segment}': an item is written [n], an index from 0";
                }

                (name, mark, written) = (segment[..open], '[', segment[(open + 1)..^1]);
            }
            else if (segment.IndexOf('+') is var plus && plus > 0)
            {
                (name, mark, written) = (segment[..plus], '+', segment[(plus + 1)..]);
            }

            if (!FlowScope.IsWellFormedName(name))
            {
                return shape;
            }

            if (mark == '\0')
            {
                continue;
            }

            if (WorkflowExpressions.WholeNumber(written) is not { } count)
            {
                return mark switch
                {
                    '#' => $"'{segment}': an iteration is written #n, from 1",
                    '[' => $"'{segment}': an item is written [n], an index from 0",
                    _ => $"'{segment}': a visit is written +n, from 2",
                };
            }

            if (mark == '#' && count < 1)
            {
                return $"'{segment}': an iteration is counted from 1";
            }

            if (mark == '+' && count < 2)
            {
                return $"'{segment}': the first visit of a step is its id without a visit, and the second is written +2";
            }
        }

        return null;
    }

    /// <summary>
    /// The id as it names a directory of the run's store: the same text, with each item's brackets written
    /// <c>@</c>. The id is not checked here; <see cref="Problem"/> owns that refusal.
    /// </summary>
    /// <param name="id">An id, as written in a run's records.</param>
    public static string DirectoryOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return string.Join(
            ".",
            id.Split('.').Select(segment => segment.Contains('[')
                ? segment.Replace("[", "@").Replace("]", string.Empty)
                : segment));
    }

    /// <summary>
    /// The id a directory of the run's store names: the same text, with each item's <c>@</c> written in brackets.
    /// The name is not checked here; <see cref="Problem"/> owns that refusal, over the id it returns.
    /// </summary>
    /// <param name="name">A directory name, as the store spells it.</param>
    public static string IdOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return string.Join(
            ".",
            name.Split('.').Select(segment => segment.Contains('@')
                ? $"{segment.Replace("@", "[")}]"
                : segment));
    }
}
