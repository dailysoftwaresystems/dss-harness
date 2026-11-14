using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;

namespace DssHarness.Core.Build;

/// <summary>One unit a build failed to build.</summary>
/// <param name="Source">
/// The source file the unit compiles, relative to the leg's tree with forward slashes where it is in it and absolute
/// where it is not; <see langword="null"/> where the build names no source for it - a step that links or runs a command,
/// or one make names by its target alone.
/// </param>
/// <param name="Step">The step that failed, by its first output as the build names it, relative to the build directory.</param>
public sealed record FailedUnit(string? Source, string Step)
{
    /// <summary>The unit as a line names it: by its source, else by its step.</summary>
    public string Named => Source ?? Step;
}

/// <summary>
/// What a build its tool failed says of the units it failed to build: each by its source, how many, and - where its tool
/// stops at its first failures - how far it got, so a unit it never reached is never read as one that built.
/// </summary>
/// <param name="Tool">The build tool, as a line names it: ninja, samu, make, mingw32-make, MSBuild, dotnet, dart, or a generator.</param>
/// <param name="Units">Each unit the build said failed, in the order it said them; empty where it named none or they were not read.</param>
/// <param name="Unread">
/// Why which units failed is not known, as a clause says it, where the build's output was not read for them;
/// <see langword="null"/> where it was - and <see cref="WhyUnread"/> says why where it was read and named none.
/// </param>
/// <param name="KeptGoing">
/// Whether the build's tool went on past its failing units, as what it was run with says: given its way's arguments
/// (<see cref="KeepGoingWay.GivenIn"/>) - never only asked to (<see cref="KeepGoing.Option"/>) - or going on by itself.
/// </param>
/// <param name="StopsAtFirst">
/// Whether the tool stops at its first failures unless told otherwise - ninja and make - so a build of it not told to go on
/// left units it never reached; MSBuild, dotnet and dart go on by themselves.
/// </param>
/// <param name="StepsRun">How many of its steps the build ran, as ninja's <c>[run/planned]</c> progress last said it; <see langword="null"/> where nothing says.</param>
/// <param name="StepsPlanned">How many steps the build planned to run, as the same progress said it; <see langword="null"/> where nothing says.</param>
/// <remarks>
/// A tool that stops at its first failures hides every unit after them: measured on a consumer's MinGW build under ninja,
/// it stopped with 22 of its 1093 steps run and 3 units failed, and every unit it never reached would have read as clean.
/// So a line of such a build says how far it got and names <see cref="KeepGoing.Option"/>, and where which units failed
/// cannot be read it says so, never that none did: a build its own tool failed failed somewhere, and a reading that found
/// no unit in its lines found none it could read, as GNU make in another language, or a make line written for a signal,
/// was not read before its lines were read for their structure alone.
/// </remarks>
public sealed record FailedUnits(
    string Tool,
    IReadOnlyList<FailedUnit> Units,
    string? Unread,
    bool KeptGoing,
    bool StopsAtFirst,
    int? StepsRun = null,
    int? StepsPlanned = null)
{
    /// <summary>
    /// What a build whose tool's output is not read for its units says: which tool it is, and that its log names each it
    /// failed.
    /// </summary>
    /// <param name="tool">The build tool, as a line names it.</param>
    /// <param name="keptGoing">Whether the build's tool went on past its failing units.</param>
    /// <param name="stopsAtFirst">Whether the tool stops at its first failures unless told otherwise.</param>
    public static FailedUnits NotRead(string tool, bool keptGoing, bool stopsAtFirst)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        return new(tool, [], $"only ninja's and make's output is read for them, and this build ran {tool}: its log names each unit that failed", keptGoing, stopsAtFirst);
    }

    /// <summary>
    /// Why which units failed is not known, as a clause says it: <see cref="Unread"/> where the output was not read, and -
    /// where it was read and named no unit that failed - that none was found in a line read for them, which is never that
    /// none failed; <see langword="null"/> where units were read.
    /// </summary>
    public string? WhyUnread => Unread ?? (Units.Count == 0
        ? $"{Tool} named no unit that failed in any line read for them - its log says what failed"
        : null);

    /// <summary>
    /// What a leg's line says of the units: how many failed, or why which is not known; and, where the tool stops at its
    /// first failures, how far it got and that <see cref="KeepGoing.Option"/> builds past them - or, told to go on, what it
    /// did not run.
    /// </summary>
    public string Describe()
    {
        if (WhyUnread is { } unread)
        {
            return $"which units failed to build was not read: {unread}";
        }

        var failed = $"{ReportText.Counted(Units.Count, "unit")} failed to build";

        if (KeptGoing)
        {
            return StepsRun is { } ran && StepsPlanned is { } planned
                ? ran >= planned
                    ? $"{failed}; {Tool} went on past them and ran all {planned} steps it had to run"
                    : $"{failed}; {Tool} went on past them and ran {ran} of the {planned} steps it had to run: "
                      + (planned - ran == 1 ? "the one it did not run needs one that failed" : $"the {planned - ran} it did not run need one that failed")
                : $"{failed}; {Tool} went on past them";
        }

        if (!StopsAtFirst)
        {
            return failed;
        }

        if (StepsRun is { } reached && StepsPlanned is { } total)
        {
            return reached >= total
                ? $"{failed}; {Tool} ran all {total} steps it had to run"
                : $"{failed}; {Tool} stops at its first failures, and ran {reached} of the {total} steps it had to run: "
                  + (total - reached == 1 ? "the one it never reached was not built, and says" : $"the {total - reached} it never reached were not built, and say")
                  + $" nothing of the code - {KeepGoing.Option} builds past failing units";
        }

        return $"{failed}; {Tool} stops at its first failures, so a unit it never reached was not built, and says nothing of the code - "
            + $"{KeepGoing.Option} builds past failing units";
    }

    /// <summary>
    /// <paramref name="source"/> as a unit names it: relative to <paramref name="treeRoot"/> with forward slashes where it
    /// is in that tree - a source a build names relative to its directory read from there - and as it is otherwise.
    /// </summary>
    /// <param name="source">The source, as the build names it: absolute, or relative to its directory.</param>
    /// <param name="buildDirectory">The build directory, which a relative source is read from.</param>
    /// <param name="treeRoot">The tree the leg builds.</param>
    internal static string SourceIn(string source, string buildDirectory, string treeRoot)
    {
        ArgumentNullException.ThrowIfNull(source);

        var full = Path.IsPathRooted(source) ? source : Path.Combine(buildDirectory, source);
        var relative = Path.GetRelativePath(treeRoot, Path.GetFullPath(full)).Replace('\\', '/');
        var outside = Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal);

        return outside ? source : relative;
    }

    /// <summary>
    /// The units as a document says them - a run's <c>--json</c> on the leg, and a workflow step's red entry for it - each
    /// path shown by <paramref name="show"/>: the count only where which units failed was read (<see cref="WhyUnread"/>),
    /// and the line's own words.
    /// </summary>
    /// <param name="show">How a path is shown: as it is, or a host's spelled as that host spells it.</param>
    public FailedUnitsDocument Document(Func<string, string> show)
    {
        ArgumentNullException.ThrowIfNull(show);

        var unread = WhyUnread;

        return new(
            Tool,
            unread is null ? Units.Count : null,
            [.. Units.Select(unit => new FailedUnitDocument(unit.Source is { } source ? show(source) : null, show(unit.Step)))],
            unread is null ? null : show(unread),
            KeptGoing,
            StopsAtFirst,
            StepsRun,
            StepsPlanned,
            show(Describe()));
    }
}

/// <summary>
/// The units a build failed to build, as a document says them (<see cref="FailedUnits.Document"/>): one shape for the
/// ledger's <c>--json</c> and a workflow step's red entry, each member left out where it holds nothing.
/// </summary>
/// <param name="Tool">The build tool.</param>
/// <param name="Count">How many units failed; <see langword="null"/> where which did was not read.</param>
/// <param name="Units">Each unit, by its source where the build names one and its step always.</param>
/// <param name="Unread">Why which units failed was not read; <see langword="null"/> where it was.</param>
/// <param name="KeptGoing">Whether the build's tool went on past its failing units.</param>
/// <param name="StopsAtFirst">Whether the tool stops at its first failures unless told otherwise.</param>
/// <param name="StepsRun">How many of its steps the build ran, where its tool's own status says it.</param>
/// <param name="StepsPlanned">How many steps it planned to run, where the same status says it.</param>
/// <param name="Said">What the leg's line says of them (<see cref="FailedUnits.Describe"/>).</param>
public sealed record FailedUnitsDocument(
    string Tool,
    int? Count,
    IReadOnlyList<FailedUnitDocument> Units,
    string? Unread,
    bool KeptGoing,
    bool StopsAtFirst,
    int? StepsRun,
    int? StepsPlanned,
    string Said);

/// <summary>One unit as a document says it.</summary>
/// <param name="Source">The source it compiles; <see langword="null"/> where the build names none.</param>
/// <param name="Step">The step that failed, by its first output.</param>
public sealed record FailedUnitDocument(string? Source, string Step);

/// <summary>A build phase its tool failed, as an adapter reads it for the units it failed to build.</summary>
/// <param name="Phase">The phase, which did not pass, exited non-zero and was stopped by no bound of the harness's.</param>
/// <param name="Arguments">
/// What the phase ran its program with, which says whether its tool was given its way to go on past failing units
/// (<see cref="KeepGoingWay.GivenIn"/>) - never what the build was asked alone.
/// </param>
/// <param name="Request">The leg's build.</param>
/// <param name="BuildDirectory">Where it built.</param>
/// <param name="Configured">What CMake's cache says the directory was configured with; <see langword="null"/> where it holds none.</param>
/// <param name="FileSystem">Reads the build's own records.</param>
public sealed record FailedBuild(
    PhaseResult Phase,
    IReadOnlyList<string> Arguments,
    BuildRequest Request,
    string BuildDirectory,
    BuildDirectoryRecord? Configured,
    IFileSystem FileSystem);
