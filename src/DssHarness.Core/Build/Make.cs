using System.Text.RegularExpressions;
using DssHarness.Core.Execution;

namespace DssHarness.Core.Build;

/// <summary>
/// What make is to a build CMake generates for it: which generators are make's, what has it go on past a failing unit,
/// and what its lines say failed.
/// </summary>
internal static class Make
{
    /// <summary>The generators whose build tool is make: GNU make, MinGW's and MSYS's, or any make POSIX describes.</summary>
    private static readonly string[] Generators = ["Unix Makefiles", "MinGW Makefiles", "MSYS Makefiles"];

    /// <summary>
    /// What make has every make it runs go on past a target that fails, with every target that does not need it:
    /// <c>-k</c>, which POSIX names for every make, and which GNU make carries into the makes CMake's makefiles start for
    /// each target.
    /// </summary>
    public static IReadOnlyList<string> KeepGoingArguments { get; } = ["-k"];

    /// <summary>A terminal's colour codes, which make writes around what it says where something makes it colour its output.</summary>
    private static readonly Regex Colour = new(@"\x1B\[[0-9;]*m", RegexOptions.CultureInvariant);

    /// <summary>
    /// The line GNU make says a target failed on: its own name, the depth of the make that says it where it is not the
    /// first, and in brackets the makefile and line of the rule, then the target, before the code its recipe exited with -
    /// <c>mingw32-make[2]: *** [CMakeFiles\app.dir\build.make:78: CMakeFiles/app.dir/a.c.obj] Error 1</c>; GNU make before 4
    /// names the target alone. A failure make was told to ignore is no failure.
    /// </summary>
    private static readonly Regex Failed = new(
        @"\A\S+?(?:\[(?<depth>[0-9]+)\])?: \*\*\* \[(?<rule>.+)\] Error -?[0-9]+\s*\z",
        RegexOptions.CultureInvariant);

    /// <summary>The makefile and line GNU make 4 and later writes before the target, in a failure's brackets.</summary>
    private static readonly Regex RuleAt = new(@"\A.+?:[0-9]+: ", RegexOptions.CultureInvariant);

    /// <summary>Whether a CMake generator, as configuration or CMake's cache names it, is one whose build tool is make.</summary>
    /// <param name="generator">The generator, or <see langword="null"/> where nothing names one.</param>
    public static bool Generates(string? generator)
        => generator is not null && Generators.Contains(generator, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The targets a build's <paramref name="lines"/> say failed, each once, in the order make said them: those the deepest
    /// make saying any failed said - the make that ran the target's own recipe - and none of those above it, each of which
    /// failed only because a make it started did, as CMake's makefiles start one make for every target and another for
    /// the whole build.
    /// </summary>
    /// <param name="lines">The build's output, a line at a time, as its log keeps it.</param>
    public static IReadOnlyList<string> FailedTargets(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var said = new List<(int Depth, string Target)>();

        foreach (var raw in lines)
        {
            if (Failed.Match(Colour.Replace(raw, string.Empty).TrimEnd('\r')) is not { Success: true } failure)
            {
                continue;
            }

            var depth = failure.Groups["depth"].Success
                && int.TryParse(failure.Groups["depth"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out var level)
                    ? level
                    : 0;
            var rule = failure.Groups["rule"].Value;

            said.Add((depth, rule[RuleAt.Match(rule).Length..]));
        }

        if (said.Count == 0)
        {
            return [];
        }

        var deepest = said.Max(failure => failure.Depth);

        return [.. said.Where(failure => failure.Depth == deepest).Select(failure => failure.Target).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The units a build make failed says it failed to build: each target the make that ran it said failed
    /// (<see cref="FailedTargets"/>), by the target make names - an object, relative to the build directory, for make says
    /// no source - and whether it was told to go on. make counts nothing of how far it got.
    /// </summary>
    /// <param name="failed">The build phase make failed.</param>
    public static FailedUnits FailedUnitsOf(FailedBuild failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        var tool = Path.GetFileNameWithoutExtension(failed.Configured?.MakeProgram) is { Length: > 0 } named ? named : "make";

        try
        {
            return new(tool, [.. FailedTargets(failed.Phase.Output.Lines()).Select(target => new FailedUnit(null, target))], null, failed.Request.KeepGoing, StopsAtFirst: true);
        }
        catch (PhaseOutputUnreadException ex)
        {
            return new(tool, [], ex.Message, failed.Request.KeepGoing, StopsAtFirst: true);
        }
    }
}
