namespace DssHarness.Core.SetUps;

/// <summary>
/// What a set-up's lines may read where the leg's work is held inside the operating system's isolation under
/// <c>required</c>: none of the trees the agents wrote. A set-up's lines run outside the isolation - what an up line
/// brings up outlives every isolation, and a container engine's socket is hidden inside one - so a line that read such a
/// tree - a compose file, a script - would run what the agents wrote outside it. Each line runs from the leg's own
/// records, never in the tree, and a line whose program or any argument names a place in one of the trees is refused,
/// the leg skipped, before any set-up of it is held or comes up.
/// </summary>
/// <param name="Trees">The trees the agents wrote, as this machine spells them: the one the leg works in, the run's, and the main checkout.</param>
public sealed record SetUpConfinement(IReadOnlyList<string> Trees)
{
    /// <summary>
    /// The tree <paramref name="line"/> names a place in - by its program, a path read against the tree it would have run
    /// in, or by one of its arguments, whichever separator spells it - or null where it names none.
    /// </summary>
    /// <param name="line">The line, its inputs filled in.</param>
    public string? Reads(SetUpCommandLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        foreach (var word in line.Arguments.Prepend(line.FileName))
        {
            var spelled = word.Replace('\\', '/');

            if (Trees.FirstOrDefault(tree => Names(spelled, Path.TrimEndingDirectorySeparator(tree).Replace('\\', '/'))) is { } tree)
            {
                return tree;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="word"/> names <paramref name="tree"/> or a place in it: the tree whole, ended by the word's
    /// end or by anything a name of a sibling would not go on with - a separator, a container engine's <c>:</c> - never a
    /// sibling its name begins.
    /// </summary>
    private static bool Names(string word, string tree)
    {
        for (var at = word.IndexOf(tree, StringComparison.OrdinalIgnoreCase); at >= 0; at = word.IndexOf(tree, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var after = at + tree.Length;

            if (after == word.Length || !(char.IsLetterOrDigit(word[after]) || word[after] is '-' or '_' or '.'))
            {
                return true;
            }
        }

        return false;
    }
}
