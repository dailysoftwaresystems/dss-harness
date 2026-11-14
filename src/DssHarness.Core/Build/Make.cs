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
    /// The makefiles CMake drives a build through, by file name: the top <c>Makefile</c>, and <c>CMakeFiles/Makefile2</c>,
    /// which starts one make for each target's own <c>build.make</c>. A failure in one of them is a make it started
    /// failing - or, where CMake re-runs itself, its own check - never a target's own step.
    /// </summary>
    private static readonly string[] Drivers = ["Makefile", "Makefile2"];

    /// <summary>
    /// What make has every make it runs go on past a target that fails, with every target that does not need it:
    /// <c>-k</c>, which POSIX names for every make, and which GNU make carries into the makes CMake's makefiles start for
    /// each target.
    /// </summary>
    public static IReadOnlyList<string> KeepGoingArguments { get; } = ["-k"];

    /// <summary>A terminal's colour codes, which make writes around what it says where something makes it colour its output.</summary>
    private static readonly Regex Colour = new(@"\x1B\[[0-9;]*m", RegexOptions.CultureInvariant);

    /// <summary>
    /// The line GNU make says a target failed on, read by its structure alone: its own name, the depth of the make that
    /// says it where it is not the first, <c>*** </c>, and in brackets the makefile and line of the rule and then the target
    /// - <c>mingw32-make[2]: *** [CMakeFiles\app.dir\build.make:78: CMakeFiles/app.dir/a.c.obj] Error 1</c> - whatever
    /// follows: GNU make translates <c>Error</c> (<c>Fehler 1</c>, <c>Erreur 1</c>) and writes a signal's name in its place
    /// (<c>Killed</c>, <c>Segmentation fault</c>), and neither its <c>*** </c> nor its brackets. GNU make before 4 names the
    /// target alone. A failure make was told to ignore is written without the <c>*** </c>, and is no failure.
    /// </summary>
    private static readonly Regex Failed = new(
        @"\A\S+?(?:\[(?<depth>[0-9]+)\])?: \*\*\* \[(?<rule>.+?)\] \S.*\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The makefile and line GNU make 4 and later writes before the target, in a failure's brackets, parted from it as its
    /// catalogs write the colon: <c>build.make:76: a.o</c> in English and most others, French's <c>build.make:76 : a.o</c>
    /// with a space before it, Simplified Chinese's <c>build.make:76：a.o</c> with a full-width one.
    /// </summary>
    private static readonly Regex RuleAt = new(@"\A(?<makefile>.+?):[0-9]+\s*[:：]\s*", RegexOptions.CultureInvariant);

    /// <summary>
    /// A line number after a colon, which no target's name holds: where a failure's brackets hold one that
    /// <see cref="RuleAt"/> could not part from the target, make named a makefile in a form this does not read.
    /// </summary>
    private static readonly Regex LineNumber = new(@":[0-9]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// The directory of a target's own makefiles in what a target's name or a makefile's path holds - <c>CMakeFiles/app.dir/</c>,
    /// <c>sub/CMakeFiles/app.dir/</c> - which ties a failure of CMake's driving makefiles for a target to the failures of
    /// that target's own steps.
    /// </summary>
    private static readonly Regex TargetDirectory = new(@"\A(?<directory>(?:[^/]*/)*?CMakeFiles/[^/]+\.dir/)", RegexOptions.CultureInvariant);

    /// <summary>The targets of CMake's driving makefiles that stand for the whole build, or for more than one target.</summary>
    private static readonly string[] WholeBuild = ["all", "default_target", "preinstall"];

    /// <summary>
    /// The goals <c>CMakeFiles/Makefile2</c> names for each target - <c>CMakeFiles/app.dir/all</c>, <c>.../rule</c> -
    /// by which a failure of a driving makefile is known where make names no makefile.
    /// </summary>
    private static readonly Regex DriverGoal = new(@"(?:\A|/)CMakeFiles/[^/]+\.dir/(?:all|rule|preinstall|clean)\z", RegexOptions.CultureInvariant);

    /// <summary>Whether a CMake generator, as configuration or CMake's cache names it, is one whose build tool is make.</summary>
    /// <param name="generator">The generator, or <see langword="null"/> where nothing names one.</param>
    public static bool Generates(string? generator)
        => generator is not null && Generators.Contains(generator, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The targets a build's <paramref name="lines"/> say failed of its own, each once, in the order make said them: each
    /// step of a target's own makefile that failed, and each failure of CMake's driving makefiles no failure below it
    /// explains - read along each chain of makes, never over the whole build.
    /// </summary>
    /// <param name="lines">The build's output, a line at a time, as its log keeps it.</param>
    /// <param name="targets">The targets the build was asked for, which CMake's top makefiles name a rule each.</param>
    /// <remarks>
    /// <para>
    /// A make fails a target's rule after every make the rule started has said why it failed, so what explains a failure
    /// was said before it, by a deeper make. CMake's makefiles start one make for the build, one through
    /// <c>CMakeFiles/Makefile2</c>, and from it one for each target's <c>build.make</c>, whose steps are the units; so a
    /// failure of a driving makefile is explained by an earlier, deeper failure of the same target - by the target's
    /// directory, <c>CMakeFiles/app.dir/</c>, in its target and in the deeper one's makefile - or, for a rule standing for
    /// the whole build, by any. One no failure explains is kept, named by its target: a target whose make said no target
    /// failed - make that found no rule for a header, and stopped - is still a target that failed. GNU make before 4 names
    /// no makefile, so a driving makefile's failure is known by its target, and a step's tied to a target by its directory
    /// where its target lies under it, as an object does, else to any target.
    /// </para>
    /// <para>
    /// A build nested in a step - an external project's, whose step runs <c>$(MAKE)</c> - fails within that step, deeper,
    /// before it: so a failure is a nested build's where a step of this build at a shallower depth failed after it - one
    /// known to be a step, by a makefile read that drives nothing, or by its target where make names no makefile at all -
    /// and its units are that build's, relative to its own directory; the step that ran it is this build's unit.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> FailedTargets(IEnumerable<string> lines, IReadOnlyCollection<string>? targets = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var said = new List<Failure>();

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
            var at = RuleAt.Match(rule);
            var makefile = at.Success ? at.Groups["makefile"].Value.Replace('\\', '/') : null;
            var target = rule[at.Length..];
            var whole = WholeBuild.Contains(target, StringComparer.Ordinal) || targets?.Contains(target, StringComparer.Ordinal) == true;
            var driver = makefile is not null
                ? Drivers.Contains(Path.GetFileName(makefile), StringComparer.Ordinal)
                : whole || DriverGoal.IsMatch(target);
            var tie = driver
                ? whole ? null : DirectoryOf(target)
                : DirectoryOf(makefile ?? target);

            said.Add(new Failure(said.Count, depth, driver, target, tie, makefile is not null, !at.Success && LineNumber.IsMatch(rule)));
        }

        // GNU make before 4 names no makefile in any failure, and a step is known by its target; one that names one in any
        // is read by its makefiles alone.
        var named = said.Any(failure => failure.MakefileRead || failure.MakefileUnread);

        return [.. said
            .Where(failure => !Nested(failure, said, named) && !(failure.Driver && Explained(failure, said)))
            .Select(failure => failure.Target)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The units a build make failed says it failed to build: each target of its own (<see cref="FailedTargets"/>), by the
    /// target make names - an object, relative to the build directory, for make says no source - and whether its tool went
    /// on past failing targets. make counts nothing of how far it got.
    /// </summary>
    /// <param name="failed">The build phase make failed.</param>
    /// <param name="keptGoing">Whether make was given its way to go on, as what the phase ran with says.</param>
    public static FailedUnits FailedUnitsOf(FailedBuild failed, bool keptGoing)
    {
        ArgumentNullException.ThrowIfNull(failed);

        var tool = Path.GetFileNameWithoutExtension(failed.Configured?.MakeProgram) is { Length: > 0 } named ? named : "make";

        try
        {
            return new(
                tool,
                [.. FailedTargets(failed.Phase.Output.Lines(), failed.Request.Project.Targets).Select(target => new FailedUnit(null, target))],
                null,
                keptGoing,
                StopsAtFirst: true);
        }
        catch (PhaseOutputUnreadException ex)
        {
            return new(tool, [], ex.Message, keptGoing, StopsAtFirst: true);
        }
    }

    /// <summary>
    /// Whether <paramref name="failure"/> is a nested build's: a step of this build at a shallower depth failed after it,
    /// the step whose make ran the build it is of - one known to be a step: its makefile read and none of CMake's driving
    /// ones, or, where no failure of the build names a makefile, as GNU make before 4 writes them, its target no driving
    /// makefile's goal. A failure whose makefile was named in a form not read proves nothing, nor does one naming no
    /// makefile among failures that name theirs: either could be a driving makefile's, and would leave out the very unit
    /// that failed.
    /// </summary>
    /// <param name="failure">The failure.</param>
    /// <param name="said">Every failure the build said, in order.</param>
    /// <param name="named">Whether any failure of the build names a makefile, read or not.</param>
    private static bool Nested(Failure failure, IReadOnlyList<Failure> said, bool named)
        => said.Any(step => !step.Driver
            && (step.MakefileRead || !named)
            && step.Order > failure.Order
            && step.Depth < failure.Depth);

    /// <summary>
    /// Whether a failure of a driving makefile is explained by an earlier, deeper failure along its chain: of its own
    /// target, by the directory both are tied to, or - for a rule standing for the whole build, or for a step tied to no
    /// target - of any.
    /// </summary>
    private static bool Explained(Failure driver, IReadOnlyList<Failure> said)
        => said.Any(deeper => deeper.Order < driver.Order
            && deeper.Depth > driver.Depth
            && (driver.Tie is null || deeper.Tie is null || SameTie(deeper.Tie, driver.Tie)));

    /// <summary>
    /// Whether two ties name one target's directory: alike, or one absolute and ending in the other - a makefile make was
    /// given by its whole path.
    /// </summary>
    private static bool SameTie(string one, string other)
        => string.Equals(one, other, StringComparison.Ordinal)
            || Path.IsPathRooted(one) && one.EndsWith("/" + other, StringComparison.Ordinal)
            || Path.IsPathRooted(other) && other.EndsWith("/" + one, StringComparison.Ordinal);

    /// <summary>The directory of a target's own makefiles a path lies under, ending in '/'; <see langword="null"/> where it lies under none.</summary>
    private static string? DirectoryOf(string path)
        => TargetDirectory.Match(path.Replace('\\', '/')) is { Success: true } match ? match.Groups["directory"].Value : null;

    /// <summary>One failure make said.</summary>
    /// <param name="Order">Where it was said among the build's failures.</param>
    /// <param name="Depth">The depth of the make that said it: 0 for the first.</param>
    /// <param name="Driver">Whether it is a failure of CMake's driving makefiles, rather than of a target's own step.</param>
    /// <param name="Target">The target that failed, as make names it.</param>
    /// <param name="Tie">The directory of the target's own makefiles it is tied to; <see langword="null"/> where it is tied to none.</param>
    /// <param name="MakefileRead">Whether the makefile and line make wrote before the target were read apart from it.</param>
    /// <param name="MakefileUnread">Whether make wrote a makefile and line in a form <see cref="RuleAt"/> does not read, so the target is the whole bracket.</param>
    private sealed record Failure(int Order, int Depth, bool Driver, string Target, string? Tie, bool MakefileRead, bool MakefileUnread);
}
