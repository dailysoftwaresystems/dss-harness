using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Core.SetUps;

/// <summary>
/// What a set-up's lines may read where the leg's work is held inside the operating system's isolation under
/// <c>required</c>: none of the trees the agents wrote. A set-up's lines run outside the isolation - what an up line
/// brings up outlives every isolation, and a container engine's socket is hidden inside one - so a line that read such a
/// tree - a compose file, a package's scripts, a script - would run what the agents wrote outside it.
/// </summary>
/// <remarks>
/// <para>
/// Each line runs from <see cref="Directory"/>, an empty directory with no tree above it, so a program that finds its
/// project where it runs or above it - Compose's file, npm's package, make's makefile, git's repository - finds none of
/// the agents', and a path a line names relatively is read there.
/// </para>
/// <para>
/// Each line is read before any set-up of the leg is held or comes up, as the policy's own reader reads a command
/// (<see cref="CommandAnalysis"/>): its words as its program is handed them, every command it runs - a shell's script
/// gone into, its wrappers stripped - and every path each word names (<see cref="CommandAnalysis.PathsNamed"/>, each
/// part of a <c>source:target</c> or a search path among them), a home at its start expanded, read against the directory
/// it runs from and each whole directory a command of it moves to; and every value the leg's environment gives it. A line
/// is refused - its leg skipped - where it names a place in one of the trees, or a directory that holds one, which a
/// program handed it may walk; where a word of it names a place by what its shell would expand - a variable, a
/// substitution, a pattern - which cannot be told before it runs; where it moves to a directory that is no whole path;
/// and where it cannot be read whole.
/// </para>
/// </remarks>
/// <param name="trees">The trees the agents wrote, as this machine spells them: the one the leg works in, the run's, and the main checkout.</param>
/// <param name="directory">The empty directory each line runs from, outside every tree.</param>
/// <param name="fileSystem">Follows the links along a place a line names, as the system would.</param>
/// <param name="platform">This machine: its home, and whether its programs split their own command lines.</param>
public sealed class SetUpConfinement(IReadOnlyList<string> trees, string directory, IFileSystem fileSystem, IHostPlatform platform)
{
    /// <summary>The trees the agents wrote, none of which a line reads.</summary>
    public IReadOnlyList<string> Trees { get; } = trees ?? throw new ArgumentNullException(nameof(trees));

    /// <summary>The empty directory each line runs from, with no tree above it.</summary>
    public string Directory { get; } = !string.IsNullOrWhiteSpace(directory) ? directory : throw new ArgumentException("A confinement runs its lines from a directory.", nameof(directory));

    /// <summary>
    /// Why <paramref name="line"/>, given <paramref name="environment"/>, may not run outside the isolation - as a sentence's
    /// middle, after the line - or null where it reads none of the trees.
    /// </summary>
    /// <param name="line">The line, its inputs filled in.</param>
    /// <param name="environment">The values the leg's environment gives every line it runs.</param>
    public string? Refusal(SetUpCommandLine line, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(environment);

        // Where it runs from first: a directory inside a tree would hand it the tree, whatever its words say.
        if (TreeHolding(Directory) is { } holding)
        {
            return $"would run from '{Directory}', which lies in '{holding}', which the agents wrote";
        }

        foreach (var (name, value) in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (Named(value, [Directory], holds: false) is { } named)
            {
                return $"is given {name} by the leg's environment, which {named}";
            }
        }

        IReadOnlyList<AnalyzedCommand> commands;

        try
        {
            commands = CommandAnalysis.Commands([line.FileName, .. line.Arguments], splitByItsProgram: platform.Current == PlatformId.Windows, inputGiven: false);
        }
        catch (NotSupportedException ex)
        {
            return $"could not be read whole: {ex.Message}";
        }

        var places = new List<string> { Directory };

        // The words as its program is handed them - the program among them, and every word of a script one holds - then
        // each command it runs, read where the commands before it moved.
        foreach (var word in new[] { line.FileName }.Concat(line.Arguments))
        {
            if (Named(word, places, holds: true) is { } named)
            {
                return named;
            }
        }

        foreach (var command in commands)
        {
            for (var at = 0; at < command.Words.Count; at++)
            {
                if ((Expanded(command.Words[at], command.QuotedWord(at)) ?? Named(command.Words[at], places, holds: true, quoted: command.QuotedWord(at))) is { } why)
                {
                    return why;
                }
            }

            // A here-document's or a here-string's text is read for the paths its words name; a descriptor duplicated names none.
            foreach (var redirect in command.Redirects.Where(redirect => redirect.Operator != "<&"))
            {
                var text = redirect.Operator == "<<<";

                if (((text ? null : Expanded(redirect.Target, quoted: null)) ?? Named(redirect.Target, places, holds: !text, braces: !text)) is { } why)
                {
                    return why;
                }
            }

            if (Moved(command.Words) is { } moved)
            {
                if (moved.Why is { } why)
                {
                    return why;
                }

                places.AddRange(moved.To);
            }
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="word"/> may not be read outside the isolation: a path it names, read against each of
    /// <paramref name="places"/>, lies in a tree or - where <paramref name="holds"/> and the word gives it whole - holds
    /// one; or the links along it cannot be followed. Null where it names neither. A tilde of a variable the shell does not
    /// expand - a program reads it out of its word, or it is quoted (<paramref name="quoted"/>) - is the text it spells.
    /// </summary>
    private string? Named(string word, IReadOnlyList<string> places, bool holds, bool braces = true, string? quoted = null)
    {
        IReadOnlyList<NamedPath> paths;

        try
        {
            paths = CommandAnalysis.PathsNamed(word, braces, lists: true, quoted);
        }
        catch (NotSupportedException ex)
        {
            return $"could not be read whole: {ex.Message}";
        }

        foreach (var named in paths)
        {
            string expanded;

            try
            {
                expanded = AgentPolicy.HomeExpanded(named.Path, platform.HomeDirectory, named.TildeExpands);
            }
            catch (NotSupportedException ex)
            {
                return $"could not be read whole: {ex.Message}";
            }

            foreach (var place in places)
            {
                string full;

                try
                {
                    full = Path.GetFullPath(expanded, place);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // A word that is no path at all ('--detach') names no place.
                    continue;
                }

                if (TreeHolding(full) is { } tree)
                {
                    return $"names '{full}', a place in '{tree}', which the agents wrote";
                }

                if (holds && named.Whole && Trees.FirstOrDefault(tree => Inside(full, tree)) is { } held)
                {
                    return $"names '{full}', which holds '{held}', which the agents wrote - a program handed it may walk it";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="word"/> names a place this cannot tell before the line runs: by what its shell expands - a
    /// variable, a substitution, a pattern, a brace - but a home at its start; null where it expands nothing else.
    /// </summary>
    private string? Expanded(string word, string? quoted)
    {
        string homeless;

        try
        {
            homeless = AgentPolicy.HomeExpanded(word, platform.HomeDirectory, CommandAnalysis.TildeExpands(word, quoted));
        }
        catch (NotSupportedException ex)
        {
            return $"could not be read whole: {ex.Message}";
        }

        var expansion = homeless == word ? CommandAnalysis.Expansion(word, quoted) : CommandAnalysis.Expansion(homeless);

        return expansion is null ? null : $"names a place by {expansion} in '{word}', which cannot be told before it runs";
    }

    /// <summary>
    /// Where a move among <paramref name="words"/> (<see cref="AgentPolicy.Movers"/>) takes the commands after it - each
    /// whole directory it names, the home where it names none - or why it cannot be told: a directory that is no whole
    /// path, read against where the shell stands or a <c>CDPATH</c> the line cannot be seen to set, or the place the shell
    /// stood before. Null where the words are no move.
    /// </summary>
    private (IReadOnlyList<string> To, string? Why)? Moved(IReadOnlyList<string> words)
    {
        var environment = CommandAnalysis.EnvironmentOf(words);

        if (environment == words.Count || !AgentPolicy.Movers.Contains(CommandAnalysis.Name(words[environment]), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var targets = new List<string>();
        var ended = false;

        foreach (var word in words.Skip(environment + 1))
        {
            if (!ended && word == "--")
            {
                ended = true;
            }
            else if (ended || word == "-" || !word.StartsWith('-'))
            {
                targets.Add(word);
            }
            else if (word.IndexOf(':', StringComparison.Ordinal) is var colon and > 1 && colon + 1 < word.Length)
            {
                targets.Add(word[(colon + 1)..]);
            }
        }

        var to = new List<string>();

        foreach (var target in targets.Count == 0 ? ["~"] : targets)
        {
            string expanded;

            try
            {
                expanded = AgentPolicy.HomeExpanded(target, platform.HomeDirectory);
            }
            catch (NotSupportedException ex)
            {
                return ([], $"could not be read whole: {ex.Message}");
            }

            if (target == "-" || !Path.IsPathFullyQualified(expanded))
            {
                return ([], $"moves to '{target}', which is no whole path, so where it then reads cannot be told before it runs");
            }

            to.Add(Path.GetFullPath(expanded));
        }

        return (to, null);
    }

    /// <summary>The tree <paramref name="place"/> lies in, as spelt or as its links lead; null where none.</summary>
    private string? TreeHolding(string place) => Trees.FirstOrDefault(tree => Inside(tree, place));

    /// <summary>
    /// Whether <paramref name="place"/> is <paramref name="root"/> or lies beneath it, as either is spelt or as their links
    /// lead - ignoring letter case, as every refusal of the policy's does, for a tree may stand on a file system that finds
    /// a name in any case. Links the system cannot follow - a cycle, a name it cannot spell, a directory this user may not
    /// look in - lead nowhere a program of the same user's reaches either.
    /// </summary>
    private bool Inside(string root, string place)
    {
        if (PathContainment.IsInsideOrSame(root, place, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return PathContainment.IsInsideOrSame(fileSystem.ResolveLinks(root), fileSystem.ResolveLinks(place), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
