using System.CommandLine.Parsing;

namespace DssHarness.Core.Processes;

/// <summary>
/// One command line written as text - a line of an action file's <c>run</c> block, a set-up's <c>up</c> or
/// <c>down</c> - split into a program and its arguments, with no shell between the harness and the program.
/// </summary>
/// <remarks>
/// The one owner of how such a line is read, so a line means the same program and arguments wherever it is written,
/// and is refused for the same reasons. No shell parses it: no word splitting beyond the one rule below, no globbing,
/// no variables, no <c>&amp;&amp;</c> or pipes - each of those reaches the program as its own argument.
/// </remarks>
public static class CommandLineText
{
    /// <summary>
    /// Whether <paramref name="line"/> can be split without losing arguments.
    /// </summary>
    /// <remarks>
    /// <c>CommandLineParser.SplitCommandLine</c> honours double quotes only and understands no
    /// escape, and an unterminated double quote makes it return everything <em>before</em> the
    /// quote and drop the rest of the line with no error at all. Measured, on System.CommandLine
    /// 2.0.12: <c>dotnet build "unterminated -c Release</c> splits to <c>[dotnet, build]</c>, and
    /// <c>tool --x "a b" "c</c> splits to <c>[tool, --x, a b]</c>. A line that lost its last three
    /// arguments still runs, still exits zero, and still looks like it did the work. So the line is
    /// refused where it is read instead.
    /// </remarks>
    /// <param name="line">One trimmed command line.</param>
    public static bool HasBalancedQuotes(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.Count(character => character == '"') % 2 == 0;
    }

    /// <summary>
    /// The program and arguments <paramref name="line"/> names, or why it names none that can run: the end of a
    /// sentence beginning with the line, such as <c>has an unbalanced double quote. ...</c>.
    /// </summary>
    /// <remarks>
    /// Double quotes group words and are dropped; nothing else is special. Space around the line is not part of it. A
    /// line refused here is never run in part.
    /// </remarks>
    /// <param name="line">One command line.</param>
    /// <param name="arguments">The program first, then its arguments; empty where the line is refused.</param>
    /// <returns>Why the line is refused, or <see langword="null"/> where it names a program.</returns>
    public static string? Split(string line, out IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(line);

        arguments = [];

        if (!HasBalancedQuotes(line))
        {
            return "has an unbalanced double quote. The command splitter drops everything after an unterminated quote "
                + "without a word, so this line would run with its remaining arguments missing.";
        }

        var split = CommandLineParser.SplitCommandLine(line.Trim()).ToArray();

        if (split.Length == 0 || string.IsNullOrEmpty(split[0]))
        {
            return "names no program.";
        }

        arguments = split;

        return null;
    }
}
