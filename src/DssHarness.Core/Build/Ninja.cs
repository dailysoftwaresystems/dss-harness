using System.Text.RegularExpressions;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;

namespace DssHarness.Core.Build;

/// <summary>
/// What ninja says as a build ends, where that decides what the harness reports. Whenever ninja itself ends a build that
/// did not succeed, it says why on a line of its own, under its own name - a step that failed, an error of its own, or
/// that it was interrupted - and so does samurai, which CMake runs for a ninja generator where that is what is
/// installed. So a build under either that exited without one was stopped from outside before it finished - killed, as a
/// tool ending its process tree does - which proves nothing about the code.
/// </summary>
internal static class Ninja
{
    /// <summary>What ninja begins the line naming each step that failed with.</summary>
    private const string FailedStep = "FAILED: ";

    /// <summary>The name ninja says everything of its own under, whatever its file is called.</summary>
    private const string OwnName = "ninja";

    /// <summary>
    /// What the program says after its name where it was interrupted, rather than ending the build for a failure:
    /// ninja's line, and samurai's, which names the signal.
    /// </summary>
    private static readonly string[] Interruptions = ["build stopped: interrupted by user", "received signal: "];

    /// <summary>
    /// How a build told to stop at its first failures ends, after the program's name - ninja's <c>build stopped:
    /// subcommand failed.</c>, samurai's <c>subcommand failed</c> - which a build told to go on without a limit never says
    /// of its own: under one, it is a nested build's end, printed as a step's output.
    /// </summary>
    private static readonly string[] NestedEnds = ["subcommand failed", "subcommands failed"];

    /// <summary>
    /// What the program says after its name that is no reason a build ended: a warning, the directory it works in, a
    /// build with nothing to do, and samurai's lines explaining why a step runs.
    /// </summary>
    private static readonly string[] Asides = ["warning: ", "Entering directory", "Leaving directory", "no work to do", "nothing to do", "explain "];

    /// <summary>A terminal's colour codes, which ninja writes around what it says where something makes it colour its output.</summary>
    private static readonly Regex Colour = new(@"\x1B\[[0-9;]*m", RegexOptions.CultureInvariant);

    /// <summary>What ninja 1.13 and later writes between <c>FAILED: </c> and a failed step's outputs: its exit code.</summary>
    private static readonly Regex FailedCode = new(@"\A\[code=-?[0-9]+\] ", RegexOptions.CultureInvariant);

    /// <summary>What samurai says under its name of a step that failed, before the command the step ran.</summary>
    private static readonly Regex JobFailed = new(@"\Ajob failed with status -?[0-9]+: ", RegexOptions.CultureInvariant);

    /// <summary>
    /// What ninja, and samurai, begin a step's line with as its status says it where <c>NINJA_STATUS</c> sets none of its
    /// own: <c>[run/planned] </c> - ninja's <c>[%f/%t] </c> written as each step ends, where its output is no terminal, and
    /// samurai's <c>[%s/%t] </c> as each starts.
    /// </summary>
    private static readonly Regex Status = new(@"\A\[(?<run>[0-9]+)/(?<planned>[0-9]+)\] ", RegexOptions.CultureInvariant);

    /// <summary>
    /// What ninja, and samurai, are given to go on past every step that fails, until nothing more can be built: <c>-k 0</c>,
    /// a limit of failures where 0 is none.
    /// </summary>
    public static IReadOnlyList<string> KeepGoingArguments { get; } = ["-k", "0"];

    /// <summary>Whether a CMake generator, as configuration or CMake's cache names it, is one of ninja's: Ninja, or Ninja Multi-Config.</summary>
    /// <param name="generator">The generator, or <see langword="null"/> where nothing names one.</param>
    public static bool Generates(string? generator) => generator?.StartsWith("Ninja", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The program CMake ran for ninja, as a line names it - <c>ninja</c>, or <c>samu</c> - or ninja where its cache names none.</summary>
    /// <param name="program">The program as CMake's cache records it, or <see langword="null"/>.</param>
    private static string ToolOf(string? program) => Path.GetFileNameWithoutExtension(program) is { Length: > 0 } named ? named : OwnName;

    /// <summary>Whose a failing line of a ninja build's output is, as <see cref="Own"/> reads it.</summary>
    internal enum Failing
    {
        /// <summary>A step of this build's own, which its ninja said failed.</summary>
        Own,

        /// <summary>A step of a build nested in one of this build's steps, printed as that step's output.</summary>
        Nested,

        /// <summary>A failing line that cannot be told this build's own or a nested build's.</summary>
        Unattributed,
    }

    /// <summary>
    /// Every failing line of a ninja build's output, each told this build's own, a nested build's, or neither, in the
    /// order said; and how far the build said it got.
    /// </summary>
    /// <param name="Said">Each failing line, by the step it names - its first output as the manifest canonicalizes it, or as listed - and whose it is.</param>
    /// <param name="Progress">
    /// How many of its steps it ran, and how many it planned, as the last status line it wrote of a step of its own says
    /// them - one more where a step in the console pool ended after it, which ninja writes no status for: one whose
    /// <c>FAILED:</c> line came after it, or whose start it was; <see langword="null"/> where it wrote none, or where a console
    /// step that did not fail started and statuses of its own followed, for whether that step ended before the last of
    /// them is not written.
    /// </param>
    /// <param name="StatusRead">Whether any status line was read as one of the build's own - none is where <c>NINJA_STATUS</c> spells the status otherwise.</param>
    internal sealed record OwnSteps(IReadOnlyList<(string Step, Failing Whose)> Said, (int Run, int Planned)? Progress, bool StatusRead)
    {
        /// <summary>Each step of this build's own its ninja said failed, once each, in the order said.</summary>
        public IReadOnlyList<string> Failed => Of(Failing.Own);

        /// <summary>Each step a failing line named that cannot be told this build's own or a nested build's, once each, and none of its own.</summary>
        public IReadOnlyList<string> Unattributed => [.. Of(Failing.Unattributed).Except(Failed, StringComparer.Ordinal)];

        /// <summary>
        /// Every step that could be this build's own that failed, once each, in the order said: its own and those that
        /// cannot be told apart, so none that could be one of its own is lost - every failing line, where nothing is nested.
        /// </summary>
        public IReadOnlyList<string> NotNested => [.. Said.Where(said => said.Whose != Failing.Nested).Select(said => said.Step).Distinct(StringComparer.Ordinal)];

        private IReadOnlyList<string> Of(Failing whose) => [.. Said.Where(said => said.Whose == whose).Select(said => said.Step).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every failing line of this build's output, told its own ninja's, a nested build's or neither, and how far it said it
    /// got (<see cref="OwnSteps"/>).
    /// </summary>
    /// <param name="lines">The build's output, a line at a time, as its log keeps it.</param>
    /// <param name="program">The program CMake ran for ninja, as its cache records it, or <see langword="null"/>.</param>
    /// <param name="manifest">The build directory's manifest, or <see langword="null"/> where none could be read.</param>
    /// <remarks>
    /// <para>
    /// A nested build's lines reach this build's output as one of its steps' output, shaped as this build's own: a status
    /// line, a <c>FAILED:</c> line naming an object relative to its own directory - one this manifest may name too, where
    /// both projects build a target of one name from a source of one name - and its own end. So a line is this build's own
    /// only where it can be nothing else. A status line is, where what it prints after the count is what one of this
    /// manifest's build lines prints - its description, or its command where it has none - and its count is no lower than
    /// the one this build stands at and no higher than one more: ninja counts each step as it ends, and a step in the
    /// console pool as it starts, at the count it stands at; a regenerated manifest's build starts again at one. A console
    /// step's end has no status, so after its <c>FAILED:</c> line the count may be one more again; and while a console step
    /// runs, ninja holds back every other step's lines, and at its end writes those of each step that failed or printed
    /// something and the status of the last to end, never the others' - so from a console step's start, which no
    /// <c>FAILED:</c> line of its own has yet ended, how far the count goes is not known, and it is held to from below
    /// alone. How many it plans moves either way - up as dynamic dependencies are found, down as a step that restats leaves
    /// others nothing to do - so only the count is held to. A nested build counts from one beside it, so its line passes
    /// both only where it prints what one of this build's prints, at this build's count.
    /// </para>
    /// <para>
    /// A <c>FAILED:</c> line is this build's own where it follows a status line of its own directly, as ninja writes a
    /// failed step's - or, for a step in the console pool, where it names that step and its start was this build's own.
    /// samurai names no outputs, and says which command failed: its line is this build's own where a build line of the
    /// manifest runs that command. A failing line is a nested build's only where it is provably within one: in the output
    /// of a step of this build's own that failed - after that step's <c>FAILED:</c> line, or, in the console pool, after its
    /// start - and before that output's nested build said its own end (<c>build stopped:</c>, or an error, under ninja's
    /// name), where something of this build's followed that end: a line of its own, or another under ninja's name - for this
    /// build's own end is the last, and a console step's own <c>FAILED:</c> line follows all it printed. Every other
    /// failing line is unattributed: it cannot be told this build's own or a nested build's, and is never read as neither -
    /// where NINJA_STATUS spells the status otherwise, no line is this build's own, and every failing line is unattributed.
    /// </para>
    /// <para>
    /// The last status of its own is the build's progress, never the largest count: a build whose dynamic dependencies are
    /// discovered as it goes plans more steps once it has run some. ninja ends a failed build only once every step it
    /// started has ended, so a console step whose <c>FAILED:</c> line, or whose start, came after the last status is one
    /// more run; one that did not fail, followed by statuses of its own, may have ended before or after the last of them,
    /// and how far the build got is not said.
    /// </para>
    /// </remarks>
    public static OwnSteps Own(IEnumerable<string> lines, string? program, NinjaManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var file = Path.GetFileName(program);
        string[] names = string.IsNullOrEmpty(file) ? [OwnName] : [OwnName, file];
        var edges = manifest?.Edges.Where(edge => edge.Rule != NinjaManifest.PhonyRule).ToList() ?? [];
        var printed = new HashSet<string>(edges.SelectMany(edge => new[] { Printed(edge.Description), Printed(edge.Command) }), StringComparer.Ordinal);
        var consoleStarted = new HashSet<NinjaEdge>(ReferenceEqualityComparer.Instance);
        var said = new List<(string Step, Failing Whose)>();
        (int Run, int Planned) at = (0, 0);
        var statusRead = false;
        var afterOwnStatus = false;
        StepOutput? output = null;

        // The console step running - from its own start until its own FAILED line, while ninja holds back every other
        // step's lines - and whether no status of this build's own has followed its start; and whether a console step's
        // FAILED line, its end, came after the last status of its own.
        IReadOnlyList<NinjaEdge>? running = null;
        var runningUnfollowed = false;
        var consoleEnded = false;

        // What a step's output held that may be a nested build's, settled as nested or unattributed once it can be.
        void Settle(Failing whose)
        {
            foreach (var held in output!.Held)
            {
                said[held] = (said[held].Step, whose);
            }

            output = null;
        }

        foreach (var raw in lines)
        {
            var line = Colour.Replace(raw, string.Empty).TrimEnd('\r');
            var ownStatus = afterOwnStatus;

            afterOwnStatus = false;

            if (Status.Match(line) is { Success: true } status
                && int.TryParse(status.Groups["run"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out var run)
                && int.TryParse(status.Groups["planned"].ValueSpan, System.Globalization.CultureInfo.InvariantCulture, out var planned))
            {
                var text = Printed(line[status.Length..]);

                // While a console step runs, how far ninja's count goes by the lines it held back is not known: from below
                // alone; and a console step's end, after its FAILED line, has no status, so the count may be one more again.
                if (!printed.Contains(text) || run < at.Run || running is null && run > at.Run + (consoleEnded ? 2 : 1))
                {
                    continue;
                }

                var started = edges.Where(edge => edge.Console && Printed(edge.Description) == text).ToList();

                at = (run, planned);
                statusRead = true;
                afterOwnStatus = true;
                consoleEnded = false;
                runningUnfollowed = started.Count > 0;
                consoleStarted.UnionWith(started);

                // The console pool runs one step at a time: one that starts is the one running, the last having ended.
                if (started.Count > 0)
                {
                    running = started;
                }

                // A status of this build's own ends a step's output: what a nested build in it said is settled - nested
                // where it said its end before this line, which is something of this build's after it - unless the step
                // is in the console pool, whose output other steps' lines, held while it ran, may follow before its own
                // FAILED line.
                if (output is { Console: false } || output is { Ended: false })
                {
                    Settle(output.Ended ? Failing.Nested : Failing.Unattributed);
                }

                if (started.Count > 0)
                {
                    output = new StepOutput(started[0], Console: true);
                }

                continue;
            }

            if (line.StartsWith(FailedStep, StringComparison.Ordinal))
            {
                var listed = FailedCode.Replace(line[FailedStep.Length..], string.Empty).Trim();
                var edge = EdgeListed(listed, manifest);
                var step = FirstOutput(listed, manifest);

                if (ownStatus || edge is { Console: true } && consoleStarted.Contains(edge))
                {
                    if (output is { } open && (!open.Console || ReferenceEquals(open.Edge, edge)))
                    {
                        Settle(open.Ended ? Failing.Nested : Failing.Unattributed);
                    }

                    said.Add((step, Failing.Own));

                    // A console step's FAILED line is its end, which ninja writes no status for.
                    if (edge is { Console: true } && running?.Contains(edge) == true)
                    {
                        running = null;
                        runningUnfollowed = false;
                        consoleEnded = true;
                    }

                    // Its output follows - unless a console step's still does, whose own FAILED line is still to come.
                    if (edge is not { Console: true } && output is null)
                    {
                        output = new StepOutput(edge, Console: false);
                    }
                }
                else
                {
                    Hold(step);
                }

                continue;
            }

            if (Said(line, names) is not { } ninja || Asides.Any(aside => ninja.StartsWith(aside, StringComparison.Ordinal)))
            {
                continue;
            }

            if (JobFailed.Match(ninja) is { Success: true } job)
            {
                var command = ninja[job.Length..];

                if (CommandEdge(command, manifest) is { } ran)
                {
                    said.Add((NinjaManifest.Normalize(ran.Outputs[0]), Failing.Own));
                }
                else
                {
                    Hold(command);
                }

                continue;
            }

            // Something under ninja's name after a nested build's end is this build's own: the end was the nested one's.
            if (output is { Ended: true, Console: false })
            {
                Settle(Failing.Nested);
            }
            else if (output is { Ended: false } && (ninja.StartsWith("build stopped: ", StringComparison.Ordinal) || ninja.StartsWith("error: ", StringComparison.Ordinal)))
            {
                output.Ended = true;
            }
        }

        if (output is not null)
        {
            Settle(Failing.Unattributed);
        }

        // ninja ends a failed build once every step it started has ended: a console step ended after the last status of
        // its own where its FAILED line, or its start, came after it; one followed by statuses may have ended before them.
        (int Run, int Planned)? progress = !statusRead ? null
            : running is not null ? runningUnfollowed ? (at.Run + 1, at.Planned) : null
            : consoleEnded ? (at.Run + 1, at.Planned) : at;

        return new(said, progress, statusRead);

        // A failing line that is no step of this build's own: held as perhaps a nested build's where it falls in a failed
        // step's output before that output's nested build ended, else unattributed.
        void Hold(string step)
        {
            if (output is { Ended: false } open)
            {
                open.Held.Add(said.Count);
                said.Add((step, Failing.Unattributed));
            }
            else
            {
                said.Add((step, Failing.Unattributed));
            }
        }
    }

    /// <summary>
    /// The output of a step of this build's own that failed, or of a console step that started, as it is read for a build
    /// nested in it: the failing lines it held, and whether a nested build in it said its end.
    /// </summary>
    /// <param name="Edge">The step, where the manifest names it.</param>
    /// <param name="Console">Whether the step is in the console pool, whose own FAILED line follows its output.</param>
    private sealed record StepOutput(NinjaEdge? Edge, bool Console)
    {
        /// <summary>Where in what was said each failing line the output held is.</summary>
        public List<int> Held { get; } = [];

        /// <summary>Whether a nested build in the output said its end.</summary>
        public bool Ended { get; set; }
    }

    /// <summary>
    /// A status line's text, or a build line's description or command, as the two are compared: without the quotes ninja
    /// puts around a path holding a space, which it writes one way on Windows and another elsewhere.
    /// </summary>
    private static string Printed(string text) => text.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>
    /// The units a build ninja failed says it failed to build: each step its own lines say failed (<see cref="Own"/>), a
    /// compile named by the source its build line compiles as the manifest says it, and every other step by its output;
    /// with how many failing lines could not be told its own or a nested build's, how far the build got, and whether its
    /// tool went on past failing steps.
    /// </summary>
    /// <param name="failed">The build phase ninja failed.</param>
    /// <param name="keptGoing">Whether ninja was given its way to go on, as what the phase ran with says.</param>
    /// <remarks>
    /// Read from the build directory's own manifest, never from what a compile's object is called: CMake names the object
    /// of a source outside its directory by a hash of where it is, and only the build line says which source it compiles.
    /// A log that cannot be read back says so, never that no unit failed.
    /// </remarks>
    public static FailedUnits FailedUnitsOf(FailedBuild failed, bool keptGoing)
    {
        ArgumentNullException.ThrowIfNull(failed);

        var program = failed.Configured?.MakeProgram;
        var tool = ToolOf(program);

        try
        {
            var manifest = NinjaManifest.Read(failed.FileSystem, failed.BuildDirectory);
            var own = Own(failed.Phase.Output.Lines(), program, manifest);
            var units = own.Failed
                .Select(output => manifest.EdgeFor(output) is { Source: { } source } edge && CMakeNinjaRule.Compiles(edge)
                    ? new FailedUnit(FailedUnits.SourceIn(source, failed.BuildDirectory, failed.Request.TreeRoot), output)
                    : new FailedUnit(null, output))
                .ToList();

            var unattributed = own.Unattributed.Count;

            // No status line of its own, and failing lines all the same: none can be told its own, which is said, never
            // that no unit failed.
            var unread = !own.StatusRead && units.Count == 0 && unattributed > 0
                ? $"{tool} wrote no status line read as its own - as where NINJA_STATUS spells its status otherwise - so none of the "
                  + $"{ReportText.Counted(unattributed, "failing line")} it wrote could be told its own or a nested build's: its log names each"
                : null;

            return new(tool, units, unread, keptGoing, StopsAtFirst: true, own.Progress?.Run, own.Progress?.Planned, unattributed);
        }
        catch (PhaseOutputUnreadException ex)
        {
            return new(tool, [], ex.Message, keptGoing, StopsAtFirst: true);
        }
    }

    /// <summary>
    /// The verdict of <paramref name="phase"/>, a build ninja ran that did not pass, where ninja did not end it for a
    /// failure: <see cref="LegVerdict.Stopped"/>, naming the exit code and whether ninja said it was interrupted or said
    /// nothing; <see langword="null"/> where ninja said anything of its own that is a reason a build ends - a step that
    /// failed, an error, a failure - or the phase exited 0 or a bound of the harness's stopped it: its own verdict stands
    /// then. And <see cref="LegVerdict.Unmeasured"/> where what the build printed could not be read back from its log, so
    /// nothing tells the two apart.
    /// </summary>
    /// <param name="phase">The build phase.</param>
    /// <param name="program">
    /// The program CMake ran for ninja, as its cache records it, or <see langword="null"/> where it records none. samurai
    /// says what it says under the name it was started by, which is that file's.
    /// </param>
    /// <param name="keptGoing">
    /// Whether ninja was given its way to go on past failing steps, as what the phase ran with says: then a failed step
    /// is no end of the build, and only ninja's last word is (<see cref="StoppedGoingOn"/>).
    /// </param>
    /// <remarks>
    /// Measured on Windows: ninja killed part way - <c>taskkill /F</c>, as a tool ending a process tree does - exits 1, as
    /// a build ninja stopped for a failed step does, and says nothing more, so only what ninja said can tell the two
    /// apart. Read as anything it says under its name that is not known to be no reason, rather than as the lines ninja
    /// prints for a failure: a build whose tool said why in words this does not know is failed, as every build was before
    /// a stopped one was told apart, and never reported as one running again would finish.
    /// </remarks>
    public static ReachedVerdict? Stopped(PhaseResult phase, string? program, bool keptGoing = false)
    {
        ArgumentNullException.ThrowIfNull(phase);

        if (phase.ExitCode == 0 || phase.Stopped != PhaseStop.None)
        {
            return null;
        }

        var file = Path.GetFileName(program);
        string[] names = string.IsNullOrEmpty(file) ? [OwnName] : [OwnName, file];
        var tool = ToolOf(program);
        var interrupted = false;

        if (keptGoing)
        {
            return StoppedGoingOn(phase, names, tool);
        }

        try
        {
            // Read from the log a line at a time, each divided however it divides itself, and no further than the line
            // that decides: a build's output can be larger than any text the harness could hold.
            foreach (var line in phase.Output.Lines().SelectMany(line => TextLines.Split(Colour.Replace(line, string.Empty)).Select(part => part.Text)))
            {
                if (line.StartsWith(FailedStep, StringComparison.Ordinal))
                {
                    return null;
                }

                if (Said(line, names) is not { } said || Asides.Any(aside => said.StartsWith(aside, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!Interruptions.Any(interruption => said.StartsWith(interruption, StringComparison.Ordinal)))
                {
                    return null;
                }

                interrupted = true;
            }
        }
        catch (PhaseOutputUnreadException ex)
        {
            // Decided here, where the build still closes its own records: a log with nothing left in it would read as a
            // build ninja said nothing of, which is one stopped from outside and no failure at all.
            return ReachedVerdict.Of(
                LegVerdict.Unmeasured,
                $"{phase.Phase} exited {phase.ExitCode}, and whether {tool} failed it or something stopped it from outside could not be read: {ex.Message}");
        }

        return ReachedVerdict.Of(
            LegVerdict.Stopped,
            interrupted
                ? $"{phase.Phase} exited {phase.ExitCode}: {tool} says it was interrupted before it finished"
                : $"{phase.Phase} exited {phase.ExitCode} without {tool} saying why, as it says whenever it ends a build itself: "
                  + "something stopped it from outside before it finished");
    }

    /// <summary>
    /// The verdict of a build ninja was told to go on past failing steps with, where ninja did not end it: told to go on,
    /// it builds on long after a step fails, so a failed step says nothing of how the build ended, and ninja's last word
    /// under its name does. Its own end - <c>build stopped: cannot make progress due to previously encountered
    /// errors</c>, or an error of its own - is <see langword="null"/>, its verdict standing; an interruption, or no last
    /// word at all - a ninja killed from outside after a step failed, which exits 1 and says nothing - is
    /// <see cref="LegVerdict.Stopped"/>, so the steps it never ran are never said to need one that failed.
    /// </summary>
    /// <remarks>
    /// A nested build's end - <c>build stopped: subcommand failed</c>, which an external project's ninja says as its step
    /// fails - is no last word of this build's: ninja told to go on without a limit never says it of its own, for it
    /// counts failures against none.
    /// </remarks>
    private static ReachedVerdict? StoppedGoingOn(PhaseResult phase, IReadOnlyList<string> names, string tool)
    {
        string? last = null;
        var failedStep = false;

        try
        {
            foreach (var line in phase.Output.Lines().SelectMany(line => TextLines.Split(Colour.Replace(line, string.Empty)).Select(part => part.Text)))
            {
                if (line.StartsWith(FailedStep, StringComparison.Ordinal))
                {
                    failedStep = true;
                    continue;
                }

                if (Said(line, names) is not { } said
                    || Asides.Any(aside => said.StartsWith(aside, StringComparison.Ordinal))
                    || NestedEnds.Any(end => said.TrimEnd('.').EndsWith(end, StringComparison.Ordinal)))
                {
                    continue;
                }

                last = said;
            }
        }
        catch (PhaseOutputUnreadException ex)
        {
            return ReachedVerdict.Of(
                LegVerdict.Unmeasured,
                $"{phase.Phase} exited {phase.ExitCode}, and whether {tool} failed it or something stopped it from outside could not be read: {ex.Message}");
        }

        if (last is not null && !Interruptions.Any(interruption => last.StartsWith(interruption, StringComparison.Ordinal)))
        {
            return null;
        }

        return ReachedVerdict.Of(
            LegVerdict.Stopped,
            last is not null
                ? $"{phase.Phase} exited {phase.ExitCode}: {tool} says it was interrupted before it finished"
                : failedStep
                    ? $"{phase.Phase} exited {phase.ExitCode}: {tool} was told to go on past failing steps and never said it had ended the build, "
                      + "as it says whenever it ends one itself: something stopped it from outside before it finished"
                    : $"{phase.Phase} exited {phase.ExitCode} without {tool} saying why, as it says whenever it ends a build itself: "
                      + "something stopped it from outside before it finished");
    }

    /// <summary>
    /// The outputs of every step a build's <paramref name="lines"/> say failed that could be the build's own, each as the
    /// manifest canonicalizes the first output of the build line that failed, in the order the build said them - by the
    /// one reading of its failing lines (<see cref="Own"/>): its own, and every one that cannot be told its own or a
    /// nested build's, so none that could be its own is lost; a nested build's alone are left out, and where nothing is
    /// nested, every failing line is here. ninja names a failed step's outputs on its <c>FAILED:</c> line; samurai names
    /// none, and says under its name which command failed, which is matched to the build line that runs it. A failure no
    /// build line of <paramref name="manifest"/> answers to is named as the build said it, so it is never lost for being
    /// unknown.
    /// </summary>
    /// <param name="lines">The build's output, a line at a time, as its log keeps it.</param>
    /// <param name="program">The program CMake ran for ninja, as its cache records it, or <see langword="null"/>.</param>
    /// <param name="manifest">The build directory's manifest, or <see langword="null"/> where none could be read.</param>
    /// <remarks>
    /// Read over lines, never the whole output at once: a build's output is as long as the build, and only these lines
    /// matter. ninja writes every output of the failed step after <c>FAILED: </c>, separated by spaces it does not escape,
    /// so the step is the build line whose outputs, in order, are exactly what follows: a path holding a space is still
    /// one output, and a shorter path that is another step's own is not taken for it.
    /// </remarks>
    public static IReadOnlyList<string> FailedOutputs(IEnumerable<string> lines, string? program, NinjaManifest? manifest)
        => Own(lines, program, manifest).NotNested;

    /// <summary>
    /// The first output of the build line whose outputs are exactly what <paramref name="outputs"/> lists, as the manifest
    /// canonicalizes it; what was listed, canonicalized, where no build line of the manifest has those outputs.
    /// </summary>
    private static string FirstOutput(string outputs, NinjaManifest? manifest)
        => EdgeListed(outputs, manifest) is { } edge ? NinjaManifest.Normalize(edge.Outputs[0]) : NinjaManifest.Normalize(outputs);

    /// <summary>
    /// The build line whose outputs are exactly what <paramref name="outputs"/> lists, as ninja lists them on a
    /// <c>FAILED:</c> line; <see langword="null"/> where no build line of the manifest has those outputs.
    /// </summary>
    private static NinjaEdge? EdgeListed(string outputs, NinjaManifest? manifest)
    {
        var listed = NinjaManifest.Normalize(outputs);

        for (var end = outputs.IndexOf(' ', StringComparison.Ordinal); manifest is not null; end = outputs.IndexOf(' ', end + 1))
        {
            if (manifest.EdgeFor(end < 0 ? outputs : outputs[..end]) is { } edge
                && string.Join(' ', edge.Outputs.Select(NinjaManifest.Normalize)) == listed)
            {
                return edge;
            }

            if (end < 0)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// The build line that runs <paramref name="command"/> - its command alone, or with what its response file holds after
    /// it, as <see cref="NinjaEdge.Command"/> keeps it; <see langword="null"/> where no build line of the manifest runs it.
    /// </summary>
    private static NinjaEdge? CommandEdge(string command, NinjaManifest? manifest)
        => manifest?.Outputs
            .Select(manifest.EdgeFor)
            .OfType<NinjaEdge>()
            .FirstOrDefault(edge => edge.Rule != NinjaManifest.PhonyRule
                && (edge.Command == command || edge.Command.StartsWith(command + " ", StringComparison.Ordinal)));

    /// <summary>
    /// What <paramref name="line"/> says after one of <paramref name="names"/> and a colon, where the program said it of its
    /// own; <see langword="null"/> where it is no line of the program's own.
    /// </summary>
    private static string? Said(string line, IReadOnlyList<string> names)
        => names
            .Where(name => line.StartsWith(name, StringComparison.Ordinal) && line.AsSpan(name.Length).StartsWith(": ", StringComparison.Ordinal))
            .Select(name => line[(name.Length + 2)..])
            .FirstOrDefault();
}
