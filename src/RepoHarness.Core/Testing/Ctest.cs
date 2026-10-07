using System.Text.RegularExpressions;
using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Testing;

/// <summary>
/// What ctest makes of the options a test invocation gives it, where that decides what the harness
/// must do: the options <c>init</c> seeds for a CMake project, and how ctest combines an option that
/// chooses its tests with another of the same kind - given twice, or set by a test preset.
/// </summary>
internal static class Ctest
{
    /// <summary>ctest's program name.</summary>
    public const string Program = "ctest";

    /// <summary>Chooses the tests to run by name.</summary>
    public const string FilterArg = "-R";

    /// <summary>Leaves out the tests a label matches.</summary>
    public const string ExcludeArg = "-LE";

    /// <summary>Chooses the tests to run by a label they carry.</summary>
    public const string LabelArg = "-L";

    /// <summary>
    /// What joins regular expressions into one that matches what any of them does: several exclusions,
    /// given to ctest as one, each leave their tests out.
    /// </summary>
    public const string ExcludeJoin = "|";

    /// <summary>The option naming a test preset.</summary>
    private const string PresetArg = "--preset";

    /// <summary>
    /// What ctest prints where it has no test to run, given any option at all and not --no-tests=ignore: what chose its
    /// tests chose none, or it found none where it looked.
    /// </summary>
    public const string NoTestsLine = "No tests were found!!!";

    /// <summary>What ctest prints, given no option at all, where it finds no test where it looked.</summary>
    public const string NoConfigurationLine = "No test configuration file found!";

    /// <summary>The spellings of the option that takes the union of the tests -I and -R choose.</summary>
    private static readonly string[] UnionArgs = ["-U", "--union"];

    /// <summary>The line ctest ends a run that ran tests with, which it never prints where it ran none.</summary>
    private static readonly Regex Summary = new(@"^\d+% tests passed, \d+ tests failed out of \d+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// What ctest's patterns read specially - every other character matches itself - and more besides, so a pattern
    /// holding none of them is plain text whatever ctest's regular expressions turn out to read.
    /// </summary>
    private static readonly char[] Special = ['\\', '.', '^', '$', '|', '?', '*', '+', '(', ')', '[', ']', '{', '}'];

    /// <summary>The option that chooses tests by name, beside which --union reads no -E either.</summary>
    private static readonly Selection ByName = new(["-R", "--tests-regex"], Excludes: false, Part.Name, Narrows: false, ReadBesideUnion: false);

    /// <summary>
    /// ctest's options that choose its tests by name or by label. Measured with ctest 4.3.2: given one
    /// of them more than once, in any of its spellings, it keeps only the last where it names tests, and
    /// runs or leaves out only a test every one matches where it names labels; and a test preset that sets
    /// the same filter is combined with it the same way. Beside --union given in its args, whatever its
    /// value, it also runs every test -I picks - every test, where the args give no -I - so none of them
    /// decides which tests run; but -E, given where nothing chooses tests by name - no -R, and no test
    /// preset setting filter.include.name - still leaves its tests out first. Beside -R it narrows only
    /// what -R chooses.
    /// </summary>
    private static readonly Selection[] Selections =
    [
        ByName,
        new(["-E", "--exclude-regex"], Excludes: true, Part.Name, Narrows: false, ReadBesideUnion: true),
        new(["-L", "--label-regex"], Excludes: false, Part.Label, Narrows: true, ReadBesideUnion: false),
        new(["-LE", "--label-exclude"], Excludes: true, Part.Label, Narrows: true, ReadBesideUnion: false),
    ];

    /// <summary>The part of a test one of ctest's options that choose tests matches.</summary>
    public enum Part
    {
        /// <summary>The test's name.</summary>
        Name,

        /// <summary>Any one of the labels the test carries.</summary>
        Label,
    }

    /// <summary>The filter a test preset chooses tests by name with, beside which --union reads no -E.</summary>
    public static string ByNamePresetFilter => ByName.PresetFilter;

    /// <summary>The filters a test preset sets the options that choose tests with: <c>include.name</c> and the like.</summary>
    public static IEnumerable<string> PresetFilters => Selections.Select(selection => selection.PresetFilter);

    /// <summary>
    /// The option <paramref name="argument"/> is to ctest, where <paramref name="runner"/> is ctest and
    /// the argument one of the options that choose its tests; <see langword="null"/> otherwise.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="argument">The argument an invocation introduces a filter, an exclusion or a label with.</param>
    public static Selection? SelectionOf(string runner, string? argument)
        => IsCtest(runner) ? Selections.FirstOrDefault(selection => selection.Spellings.Contains(argument, StringComparer.Ordinal)) : null;

    /// <summary>
    /// Each of ctest's options that choose its tests that <paramref name="args"/> give, with the values they give it,
    /// in every spelling and form, where <paramref name="runner"/> is ctest; none otherwise.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="args">The invocation's own arguments.</param>
    public static IEnumerable<(Selection Option, IReadOnlyList<string> Values)> SelectionsIn(string runner, IReadOnlyList<string> args)
        => IsCtest(runner)
            ? Selections.Select(option => (option, Values(args, option.Spellings).Values)).Where(given => given.Values.Count > 0)
            : [];

    /// <summary>
    /// Whether <paramref name="runner"/> is ctest and <paramref name="args"/> give it -R, in either spelling and any
    /// form, with a value: an empty one it reads as not given.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="args">The invocation's own arguments.</param>
    public static bool ChoosesByName(string runner, IReadOnlyList<string> args)
        => IsCtest(runner) && Values(args, ByName.Spellings).Values.Any(value => value.Length > 0);

    /// <summary>
    /// Whether <paramref name="runner"/> is ctest and <paramref name="output"/> says it had no test to run: a line of
    /// its own saying so, and no summary of tests that ran. Measured with ctest 4.3.2, a test that fails having printed
    /// the same line - one running ctest itself - leaves it in the output beside the summary, which ctest prints only
    /// where tests ran.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="output">What it printed, read a line at a time: a suite's output can be larger than any text the harness could hold.</param>
    public static bool FoundNone(string runner, PhaseOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!IsCtest(runner))
        {
            return false;
        }

        var none = false;

        foreach (var line in output.Lines().SelectMany(line => line.ReplaceLineEndings("\n").Split('\n')).Select(line => line.Trim()))
        {
            if (Summary.IsMatch(line))
            {
                return false;
            }

            none |= line is NoTestsLine or NoConfigurationLine;
        }

        return none;
    }

    /// <summary>
    /// Whether every name or label ctest finds <paramref name="chosen"/> in it finds <paramref name="leftOut"/> in too,
    /// as far as their spelling alone tells: the two are spelled the same, or both are plain text and the first holds
    /// the second, since ctest finds a pattern anywhere in what it reads. Measured with ctest 4.3.2: -L git-state beside
    /// -LE git chooses no test.
    /// </summary>
    /// <param name="chosen">A pattern an option chooses tests by.</param>
    /// <param name="leftOut">A pattern an option leaves tests out by, reading the same part of a test.</param>
    public static bool Covers(string chosen, string leftOut)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(leftOut);

        return string.Equals(chosen, leftOut, StringComparison.Ordinal)
            || (Plain(chosen) && Plain(leftOut) && chosen.Contains(leftOut, StringComparison.Ordinal));
    }

    /// <summary>Whether <paramref name="pattern"/> is text ctest matches as it is spelled, and something to match.</summary>
    private static bool Plain(string pattern) => pattern.Length > 0 && pattern.IndexOfAny(Special) < 0;

    /// <summary>
    /// The test preset <paramref name="args"/> name, where <paramref name="runner"/> is ctest; <see langword="null"/>
    /// otherwise.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="args">The invocation's own arguments.</param>
    /// <remarks>
    /// Given after the option or after '=', as ctest takes it; ctest takes a preset given after a space in
    /// the same argument for no option at all, and of two presets uses the first.
    /// </remarks>
    public static string? PresetIn(string runner, IReadOnlyList<string> args)
    {
        for (var index = 0; IsCtest(runner) && index < args.Count; index++)
        {
            if (string.Equals(args[index], PresetArg, StringComparison.Ordinal) && index + 1 < args.Count)
            {
                return args[index + 1];
            }

            if (args[index].StartsWith(PresetArg + "=", StringComparison.Ordinal))
            {
                return args[index][(PresetArg.Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="runner"/> is ctest and <paramref name="args"/> ask it to rerun the tests that
    /// failed last time: measured with ctest 4.3.2, it then passes over -R, -L and -LE, and -E makes it run
    /// other tests than those.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="args">The invocation's own arguments.</param>
    public static bool RerunsFailed(string runner, IReadOnlyList<string> args)
        => IsCtest(runner) && args.Contains("--rerun-failed", StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="runner"/> is ctest and <paramref name="args"/> give it --union, in either
    /// spelling and any form. Measured with ctest 4.3.2, it takes a value, and given any - OFF and 0 among
    /// them - also runs every test -I picks, every test where the args give no -I, whatever -R, -L, -LE and
    /// -E say: only -E, given where nothing chooses tests by name, still leaves its tests out. Given none, it
    /// refuses to run.
    /// </summary>
    /// <param name="runner">The test runner an invocation starts, by name or path.</param>
    /// <param name="args">The invocation's own arguments.</param>
    public static bool Unites(string runner, IReadOnlyList<string> args)
        => IsCtest(runner)
            && (Values(args, UnionArgs).Values.Count > 0 || args.Any(argument => UnionArgs.Contains(argument, StringComparer.Ordinal)));

    /// <summary>
    /// The values <paramref name="args"/> give an option spelled any of <paramref name="spellings"/> - after it,
    /// after it and '=', or after it and a space in the same argument, each of which ctest takes - and the args
    /// without them.
    /// </summary>
    /// <param name="args">An invocation's own arguments.</param>
    /// <param name="spellings">The option's spellings.</param>
    public static (IReadOnlyList<string> Values, IReadOnlyList<string> Others) Values(IReadOnlyList<string> args, IReadOnlyList<string> spellings)
    {
        var given = Given(args, spellings).ToList();
        var taken = given
            .SelectMany(value => value.Prefix.Length == 0 ? new[] { value.Index - 1, value.Index } : [value.Index])
            .ToHashSet();

        return ([.. given.Select(value => value.Value)], [.. args.Where((_, index) => !taken.Contains(index))]);
    }

    /// <summary>
    /// <paramref name="args"/> with <paramref name="suffix"/> after each value they give an option spelled any of
    /// <paramref name="spellings"/> - or after the last alone - each in the form it was given.
    /// </summary>
    /// <param name="args">An invocation's own arguments.</param>
    /// <param name="spellings">The option's spellings.</param>
    /// <param name="suffix">What to add after each value.</param>
    /// <param name="lastOnly">Whether only the last value takes it.</param>
    public static IReadOnlyList<string> Appended(IReadOnlyList<string> args, IReadOnlyList<string> spellings, string suffix, bool lastOnly)
    {
        var given = Given(args, spellings).ToList();
        var appended = args.ToList();

        foreach (var (index, prefix, value) in lastOnly ? given.TakeLast(1) : given)
        {
            appended[index] = prefix + value + suffix;
        }

        return appended;
    }

    /// <summary>
    /// Each value <paramref name="args"/> give an option spelled any of <paramref name="spellings"/>: the argument
    /// holding it, what comes before it in that argument, and the value.
    /// </summary>
    private static IEnumerable<(int Index, string Prefix, string Value)> Given(IReadOnlyList<string> args, IReadOnlyList<string> spellings)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];

            if (spellings.Contains(argument, StringComparer.Ordinal) && index + 1 < args.Count)
            {
                index++;
                yield return (index, string.Empty, args[index]);
            }
            else if (spellings.FirstOrDefault(spelling => argument.StartsWith(spelling + "=", StringComparison.Ordinal)
                || argument.StartsWith(spelling + " ", StringComparison.Ordinal)) is { } spelling)
            {
                yield return (index, argument[..(spelling.Length + 1)], argument[(spelling.Length + 1)..]);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="runner"/> is ctest, by its program's name, however its path is spelled: ctest, or ctest3,
    /// as the packages carrying CMake 3 beside an older CMake name it.
    /// </summary>
    private static bool IsCtest(string runner)
        => Path.GetFileNameWithoutExtension(runner) is var name
            && (string.Equals(name, Program, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, Program + "3", StringComparison.OrdinalIgnoreCase));

    /// <summary>One of ctest's options that choose its tests.</summary>
    /// <param name="Spellings">Its spellings.</param>
    /// <param name="Excludes">Whether it leaves out the tests it matches, rather than choosing them.</param>
    /// <param name="Reads">The part of a test it matches.</param>
    /// <param name="Narrows">
    /// Whether ctest, given it twice, reads both - running or leaving out only a test every one matches - rather
    /// than keeping the last.
    /// </param>
    /// <param name="ReadBesideUnion">
    /// Whether ctest still leaves out what it matches beside --union given in its args: where nothing chooses tests
    /// by name besides.
    /// </param>
    public sealed record Selection(IReadOnlyList<string> Spellings, bool Excludes, Part Reads, bool Narrows, bool ReadBesideUnion)
    {
        /// <summary>The filter a test preset sets it with, under <c>filter</c>: <c>include.name</c> and the like.</summary>
        public string PresetFilter => $"{(Excludes ? "exclude" : "include")}.{(Reads == Part.Label ? "label" : "name")}";
    }
}
