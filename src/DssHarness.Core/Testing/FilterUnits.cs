using DssHarness.Core.Results;

namespace DssHarness.Core.Testing;

/// <summary>
/// What a runner the harness knows makes of several filter units - a run's <c>test --filter</c> given more than once, a
/// workflow's test step given a list - each given after the invocation's filterArg: ctest, <c>dotnet test</c> and
/// <c>dart test</c>, the runners init seeds. None of them, given its filter option more than once, runs the tests any value
/// names: ctest keeps only the last <c>-R</c> and reads each <c>-L</c> as one more a test must match; <c>dotnet test</c>
/// takes one <c>--filter</c> - measured with the .NET SDK 11.0.100, a second is refused as a command line it cannot read,
/// and the test frameworks of Microsoft.Testing.Platform declare the option taking one value in all; and <c>dart test</c>
/// runs only a test whose name matches every <c>--name</c>, as its own help says. Each reads one value joined by
/// <c>|</c> as the tests any unit names - a regular expression's alternatives, and a filter expression's - save dart's
/// <c>--plain-name</c>, which reads its value as plain text, and so takes several units neither way. The one owner of
/// that rule: the file's reader refuses such an invocation that declares no filterJoin, or another join
/// (<see cref="Misread"/>), and the command line refuses several units it would give apart (<see cref="RefuseApart"/>)
/// and a filter beside the same option its args give (<see cref="RefuseGivenInArgs"/>). A runner it does not know reads
/// its option given twice as its own, and is given each unit after a filterArg of its own where it declares no join.
/// </summary>
internal static class FilterUnits
{
    /// <summary>
    /// What joins several filter units into one value <c>dotnet test</c> and <c>dart test</c> read as the tests any of
    /// them names: the alternatives of a filter expression, and of a regular expression.
    /// </summary>
    public const string Alternatives = "|";

    /// <summary>dotnet's program name.</summary>
    private const string Dotnet = "dotnet";

    /// <summary>dart's program name.</summary>
    private const string Dart = "dart";

    /// <summary>The command of each program that runs its tests.</summary>
    private const string TestCommand = "test";

    /// <summary><c>dotnet test</c>'s filter option.</summary>
    private const string DotnetFilter = "--filter";

    /// <summary>The spellings of <c>dart test</c>'s option that chooses tests by a regular expression their names match.</summary>
    private static readonly string[] DartNames = ["--name", "-n"];

    /// <summary>The spellings of <c>dart test</c>'s option that chooses tests by plain text their names hold.</summary>
    private static readonly string[] DartPlainNames = ["--plain-name", "-N"];

    /// <summary>
    /// How <paramref name="invocation"/>'s runner reads the filter units given after its filterArg, where it is a runner
    /// this knows - ctest by its program's name, <c>dotnet test</c> and <c>dart test</c> by theirs and the first of the args
    /// that is no option - and the filterArg its filter option; null for any other.
    /// </summary>
    /// <param name="invocation">A merged test invocation.</param>
    public static Reading? Of(ResolvedTestInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (Ctest.SelectionOf(invocation.Runner, invocation.FilterArg) is { } selection)
        {
            return new Reading("ctest", selection.Spellings, selection.GivenTwice, Ctest.FilterJoin, "one regular expression");
        }

        if (Runs(invocation, Dotnet) && string.Equals(invocation.FilterArg, DotnetFilter, StringComparison.Ordinal))
        {
            return new Reading("dotnet test", [DotnetFilter], "takes one alone, refusing a command line that gives it twice", Alternatives, "one filter expression");
        }

        if (Runs(invocation, Dart) && DartNames.Contains(invocation.FilterArg, StringComparer.Ordinal))
        {
            return new Reading("dart test", DartNames, "runs only a test whose name matches every one", Alternatives, "one regular expression");
        }

        return Runs(invocation, Dart) && DartPlainNames.Contains(invocation.FilterArg, StringComparer.Ordinal)
            ? new Reading("dart test", DartPlainNames, "runs only a test whose name holds every one", Joins: null, "plain text")
            : null;
    }

    /// <summary>
    /// What is wrong with how <paramref name="invocation"/> would give a runner this knows several filter units, as the
    /// file's reader says it: a filterArg with no filterJoin, which would give each apart; a filterJoin other than the one
    /// its runner reads as alternatives; and dart test's <c>--plain-name</c>, which takes several neither way.
    /// </summary>
    /// <param name="invocation">The invocation, merged for one operating system.</param>
    public static IEnumerable<string> Misread(ResolvedTestInvocation invocation)
    {
        if (Of(invocation) is not { } reading)
        {
            yield break;
        }

        var join = invocation.FilterJoin is { Length: > 0 } declared ? declared : null;

        if (reading.Joins is null)
        {
            yield return $"filterArg '{invocation.FilterArg}' gives {reading.Runner} plain text, so several filter units would reach it "
                + $"neither apart, where it {reading.Twice}, nor joined, where it would look for the join as text; declare "
                + $"'{DartNames[0]}', which reads a regular expression, with \"filterJoin\": \"{Alternatives}\"";
        }
        else if (join is null)
        {
            yield return $"filterArg '{invocation.FilterArg}' would reach {reading.Runner} once for each filter unit, no filterJoin "
                + $"joining them, and of {invocation.FilterArg} it {reading.Twice} - never the tests any unit names; declare "
                + $"\"filterJoin\": \"{reading.Joins}\", so they reach it as {reading.Reads}";
        }
        else if (!string.Equals(join, reading.Joins, StringComparison.Ordinal))
        {
            yield return $"filterJoin '{join}' joins nothing to {reading.Runner}, which reads what it joins as {reading.Reads} "
                + $"holding '{join}', so joined filter units would not choose the tests any of them names; declare "
                + $"\"filterJoin\": \"{reading.Joins}\"";
        }
    }

    /// <summary>
    /// Refuses <paramref name="units"/> filter units where <paramref name="invocation"/>'s runner is one this knows and they
    /// would not reach it as the tests any of them names: given apart, where <paramref name="join"/> is null, or to dart
    /// test's <c>--plain-name</c> at all. One unit, or a runner this does not know, passes.
    /// </summary>
    /// <param name="invocation">The resolved invocation.</param>
    /// <param name="units">How many filter units were given.</param>
    /// <param name="join">The join the invocation declares, or null where it declares none.</param>
    /// <exception cref="HarnessException">The units would not choose what they name (<see cref="HarnessExit.UsageError"/>).</exception>
    public static void RefuseApart(ResolvedTestInvocation invocation, int units, string? join)
    {
        if (units < 2 || Of(invocation) is not { } reading)
        {
            return;
        }

        var count = units.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (reading.Joins is null)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{count} filter units would reach {reading.Runner} after {invocation.FilterArg}, which reads its value as plain text: "
                + $"given apart it {reading.Twice}, and joined it would look for the join as text - never the tests any of them "
                + $"names. Declare filterArg '{DartNames[0]}' with \"filterJoin\": \"{Alternatives}\" in the test settings, so they reach "
                + "it as one regular expression.");
        }

        if (join is null)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"{count} filter units would reach {reading.Runner} apart, each after {invocation.FilterArg}, of which it "
                + $"{reading.Twice} - never the tests any of them names. Declare \"filterJoin\": \"{reading.Joins}\" in the test "
                + $"settings, so they reach it as {reading.Reads}.");
        }
    }

    /// <summary>
    /// Refuses a filter given where <paramref name="invocation"/>'s runner is one this knows and its own args give the
    /// filter option already, in any spelling, after it or after it and '=' (<see cref="Ctest.Values"/>): the runner reads
    /// the two as it reads its option given twice, so the filter would not choose the tests it names among theirs.
    /// </summary>
    /// <param name="invocation">The resolved invocation.</param>
    /// <exception cref="HarnessException">The args give the filter option already (<see cref="HarnessExit.UsageError"/>).</exception>
    public static void RefuseGivenInArgs(ResolvedTestInvocation invocation)
    {
        if (Of(invocation) is { } reading && Ctest.Values(invocation.Args, reading.Spellings).Values.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"A filter was given, and the test settings' args give {invocation.FilterArg} already, of which {reading.Runner} "
                + $"{reading.Twice}: the filter would not choose the tests it names among theirs. Give it in the args, or take "
                + "theirs out.");
        }
    }

    /// <summary>
    /// Whether <paramref name="invocation"/> runs <paramref name="program"/>'s tests: its runner is that program by its
    /// name, however its path is spelled, and the first of its args that is no option is <c>test</c>.
    /// </summary>
    private static bool Runs(ResolvedTestInvocation invocation, string program)
        => string.Equals(Path.GetFileNameWithoutExtension(invocation.Runner), program, StringComparison.OrdinalIgnoreCase)
            && string.Equals(invocation.Args.FirstOrDefault(argument => !argument.StartsWith('-')), TestCommand, StringComparison.Ordinal);

    /// <summary>How a runner this knows reads the filter units given after its filter option.</summary>
    /// <param name="Runner">The runner, as a sentence names it.</param>
    /// <param name="Spellings">Every spelling of its filter option.</param>
    /// <param name="Twice">What it makes of its filter option given more than once, to end a sentence.</param>
    /// <param name="Joins">What joins several units into one value it reads as the tests any of them names; null where nothing does.</param>
    /// <param name="Reads">What it reads one joined value as, to end a sentence.</param>
    internal sealed record Reading(string Runner, IReadOnlyList<string> Spellings, string Twice, string? Joins, string Reads);
}
