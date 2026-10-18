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
/// class only owns how one of its instances is said.
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
            var name = segment;
            char mark = '\0';
            var written = string.Empty;

            var hash = segment.IndexOf('#');

            if (hash > 0)
            {
                (name, mark, written) = (segment[..hash], '#', segment[(hash + 1)..]);
            }
            else if (segment.Contains('[') || segment.EndsWith(']'))
            {
                var open = segment.IndexOf('[');

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

            if (!StepNameIs(name))
            {
                return $"'{segment}': a segment of an id is a step's name, with at most one mark after it";
            }

            if (mark == '\0')
            {
                continue;
            }

            if (!int.TryParse(written, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                || count < 0 || written.StartsWith('0') && written.Length > 1)
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

    /// <summary>Whether a name is a step's: the model's own shape, a lower-case word with dashes and digits.</summary>
    private static bool StepNameIs(string name)
        => name.Length > 0
            && name[0] is >= 'a' and <= 'z'
            && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
