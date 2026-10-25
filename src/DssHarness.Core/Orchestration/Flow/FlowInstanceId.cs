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
/// A segment carries at most two marks, in one order: its visit first, then the iteration or the item that visit
/// holds, outer to inner - a <c>repeat</c> run again is <c>rounds+2#1</c>, a <c>forEach</c> expanded anew
/// <c>items+2[0]</c> (<c>items+2@0</c> on disk). The other order (<c>rounds#1+2</c>) is refused naming the right one,
/// and a mark written twice (<c>+2+3</c>, <c>#1#2</c>), or an iteration beside an item, is refused.
/// </para>
/// <para>
/// Pure text rules, nothing more: the scheduler that counts the iterations, items and visits is its own, and this
/// class only owns how one of its instances is said. A segment's name is spelt as a step's is
/// (<see cref="FlowScope.IsWellFormedName"/>) and each count as an index is (<see cref="WorkflowExpressions.WholeNumber"/>),
/// each rule written once.
/// </para>
/// </remarks>
public static class FlowInstanceId
{
    /// <summary>Whether <paramref name="id"/> is an instance id as this class spells one.</summary>
    /// <param name="id">An id, as written in a run's records.</param>
    public static bool IsWellFormed(string id) => Problem(id) is null;

    /// <summary>
    /// Why <paramref name="id"/> is not an instance id, or <see langword="null"/> where it is: a segment is not a
    /// step's name, an iteration, item or visit is written where nothing takes it or counted from nowhere, or a
    /// segment's marks are written twice or with the visit after what it holds.
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
            if (SegmentProblem(segment) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>
    /// Why one segment is not a step's name with the marks it may carry - a visit, then an iteration or an item - or
    /// <see langword="null"/> where it is.
    /// </summary>
    private static string? SegmentProblem(string segment)
    {
        var shape = $"'{segment}': a segment of an id is a step's name, then at most a visit (+n) and an iteration (#n) or an item ([n]), in that order";
        var first = segment.IndexOfAny(Marks);
        var name = first < 0 ? segment : segment[..first];

        if (!FlowScope.IsWellFormedName(name))
        {
            return shape;
        }

        var marks = new List<(char Mark, string Written)>();

        for (var at = first; at >= 0 && at < segment.Length;)
        {
            var mark = segment[at];

            if (mark == '[')
            {
                var close = segment.IndexOf(']', at + 1);

                if (close < 0)
                {
                    return $"'{segment}': an item is written [n], an index from 0";
                }

                marks.Add(('[', segment[(at + 1)..close]));
                at = close + 1;
            }
            else if (mark is '#' or '+')
            {
                // The count runs to the next mark; the character after this one is always the count's, so a sign
                // written there is a count written another way, never a second mark.
                var next = at + 2 > segment.Length ? -1 : segment.IndexOfAny(Marks, at + 2);
                var end = next < 0 ? segment.Length : next;

                marks.Add((mark, segment[(at + 1)..end]));
                at = end;
            }
            else
            {
                // Anything after an item's closing bracket that starts no mark.
                return shape;
            }
        }

        foreach (var (mark, written) in marks)
        {
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

        return marks switch
        {
            [] or [_] or [('+', _), (not '+', _)] => null,

            // A visit holds its iterations or items, so it is written first: the other order is refused, naming the right one.
            [(not '+' and var inner, var held), ('+', var visit)] =>
                $"'{segment}': a visit is written before the iteration or item it holds: '{name}+{visit}{(inner == '#' ? $"#{held}" : $"[{held}]")}'",
            _ => shape,
        };
    }

    /// <summary>The characters that begin a mark: a visit's, an iteration's and an item's.</summary>
    private static readonly char[] Marks = ['+', '#', '['];

    /// <summary>
    /// Which visit of the step named <paramref name="name"/> the well-formed id <paramref name="id"/> holds: the visit
    /// of its own segment, from 1 where none is written.
    /// </summary>
    /// <param name="id">The instance id, well formed.</param>
    /// <param name="name">The step's name, the id's last segment's name.</param>
    public static int VisitOf(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var segment = id[(id.LastIndexOf('.') + 1)..];

        if (!segment.StartsWith(name, StringComparison.Ordinal) || segment.Length == name.Length)
        {
            return 1;
        }

        var marks = segment[name.Length..];

        if (marks[0] != '+')
        {
            return 1;
        }

        var end = marks.IndexOfAny(['+', '#', '['], 1);
        var written = marks[1..(end < 0 ? marks.Length : end)];

        return WorkflowExpressions.WholeNumber(written) is { } visit ? visit : 1;
    }

    /// <summary>
    /// Whether two well-formed ids are visits of one step of one work item: the same, but for each visit and each
    /// iteration written after the innermost item - an iteration or a visit outside it may hold another item at the same
    /// place, so there they must match. <c>rounds#1.items[0].fix#2.revise.rework</c> and
    /// <c>rounds#1.items[0].fix#1.revise.rework</c> are; <c>rounds#2.items[0].fix#1.revise.rework</c> is not of them.
    /// </summary>
    /// <param name="one">An instance id, well formed.</param>
    /// <param name="other">Another.</param>
    public static bool SameStep(string one, string other)
    {
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(Lineage(one), Lineage(other), StringComparison.Ordinal);
    }

    /// <summary>The id with each visit and iteration written after its innermost item taken out: what <see cref="SameStep"/> compares.</summary>
    private static string Lineage(string id)
    {
        var segments = id.Split('.');
        var item = Array.FindLastIndex(segments, segment => segment.Contains('['));

        return string.Join('.', segments.Select((segment, at) => at <= item ? segment : segment[..(segment.IndexOfAny(Marks) is >= 0 and var mark ? mark : segment.Length)]));
    }

    /// <summary>
    /// The id as it names a directory of the run's store: the same text, with each item's brackets written
    /// <c>@</c> - an item is a segment's last mark, so <c>items+2[0]</c> is <c>items+2@0</c>. The id is not checked
    /// here; <see cref="Problem"/> owns that refusal.
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
