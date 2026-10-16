using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Tools;

namespace DssHarness.Core.Legs;

/// <summary>Where a leg runs, or why it does not: no host can take it, or the one it went to lacks what it needs.</summary>
/// <param name="Leg">The leg.</param>
/// <param name="Host">The host that runs it, or <see langword="null"/> when it does not run.</param>
/// <param name="Reason">Why it does not run, when it does not.</param>
public sealed record LegPlacement(SelectedLeg Leg, HostReport? Host, string? Reason)
{
    /// <summary>Whether a host can run the leg.</summary>
    public bool Runnable => Host is not null;

    /// <summary>What a run records for the leg when no host can run it.</summary>
    /// <remarks>
    /// Decided with the reason rather than read back out of its wording, because each sends somebody
    /// somewhere different: skipped for a missing tool, to install a program on a host that is right
    /// in every other way; skipped as unavailable, to switch a machine on or reach it; and poisoned,
    /// to report a defect in this tool - a host never asked about a program the leg starts.
    /// </remarks>
    public LegVerdict Verdict { get; init; } = LegVerdict.SkippedUnavailable;

    /// <summary>
    /// What the leg's build still needs where it was placed, where something says; <see langword="null"/> where nothing
    /// does, or the command does not build the leg.
    /// </summary>
    public RoomNeed? Need { get; init; }

    /// <summary>
    /// The hosts that may run <paramref name="leg"/>, in the order they are tried: the one host it names,
    /// or else this machine, then the WSL distributions when the leg runs on Linux, then the ssh hosts, each
    /// in the order the configuration declares them, and each named as the configuration declares it.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg being placed.</param>
    /// <param name="here">
    /// Whether this machine is the only candidate, whatever the leg names. Set when a host is
    /// running a leg the machine that reached it dispatched: the leg names that host, and asking it
    /// to place the leg again would send it looking for connection data it was deliberately never
    /// given, to reach a machine it already is.
    /// </param>
    public static IReadOnlyList<HostId> Candidates(HarnessConfig config, LegConfig leg, bool here = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);

        if (here)
        {
            return [HostId.Local];
        }

        if (Named(config, leg) is { } named)
        {
            return [named];
        }

        // A WSL distribution runs Linux and nothing else. Measuring one for a leg on another operating system
        // would install DssHarness there for a leg it can never run, or stop the check over its version.
        var distributions = Same(leg.Os, PlatformNames.Linux)
            ? config.Hosts.Wsl.Keys.Select(HostId.Wsl)
            : Enumerable.Empty<HostId>();

        return [HostId.Local, .. distributions, .. config.Hosts.Ssh.Keys.Select(HostId.Ssh)];
    }

    /// <summary>
    /// The one host <paramref name="leg"/> names, as the configuration declares it, or <see langword="null"/> where it
    /// names none and may run on any candidate.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg.</param>
    public static HostId? Named(HarnessConfig config, LegConfig leg)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);

        return leg.Wsl is { } wsl ? HostId.Wsl(DeclaredName.In(config.Hosts.Wsl.Keys, wsl) ?? wsl)
            : leg.Ssh is { } ssh ? HostId.Ssh(DeclaredName.In(config.Hosts.Ssh.Keys, ssh) ?? ssh)
            : null;
    }

    /// <summary>
    /// Places a leg on the first of its candidates that can take it - the right machine, reached,
    /// with its emulator working - and turns it away there when that host lacks what
    /// <paramref name="workload"/> starts. A candidate missing from <paramref name="reports"/> was not
    /// measured, and is passed over.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="selected">The leg being placed.</param>
    /// <param name="workload">What the command has the leg do, which says what its host must have.</param>
    /// <param name="reports">What measurement found, by host.</param>
    /// <param name="here">
    /// The host this machine is to the machine that dispatched the leg here, which makes this machine
    /// the only candidate and names the settings it is judged by; <see langword="null"/> where this
    /// machine places the leg itself.
    /// </param>
    /// <remarks>
    /// A program never chooses the host. Chosen by what each command starts, a leg went to one machine
    /// for its build and another for its tests, and a run on what a sync had staged went to a host the
    /// sync never put the tree on - while a leg quietly measured a different machine from the one it
    /// ran on the day before, because somebody had installed a tool there. Every command places a leg
    /// where a copy of its tree goes, and a program that host lacks is said to be missing there. To
    /// run a leg on another host, the leg names that host.
    /// <para>
    /// The machine is asked about before its programs, because a host that is the wrong machine
    /// should say so. Told instead that cmake is missing on a Windows host being considered for a
    /// Linux leg, a reader would go and install cmake on a machine the leg will never run on.
    /// </para>
    /// <para>
    /// A host running a leg for the machine that dispatched it - <paramref name="here"/> - names
    /// no candidate in its reasons: it is the only one, and the name it has for itself is not the
    /// name the machine that reads the reason knows it by.
    /// </para>
    /// </remarks>
    public static LegPlacement Place(
        HarnessConfig config,
        SelectedLeg selected,
        LegWorkload workload,
        IReadOnlyDictionary<HostId, HostReport> reports,
        HostId? here = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(reports);

        var reasons = new List<string>();

        foreach (var candidate in Candidates(config, selected.Leg, here is not null))
        {
            if (!reports.TryGetValue(candidate, out var report))
            {
                continue;
            }

            var at = here is null ? $"{candidate}: " : string.Empty;

            if (PlatformObstacle(selected.Leg, report) is { } passedOver)
            {
                reasons.Add(at + passedOver);
                continue;
            }

            // What the host declares for itself says what it must have - a PATH it sets is where the
            // leg's programs are found - read under the name the reader knows the host by, as the run
            // reads it.
            var settings = config.Hosts.SettingsFor(here ?? candidate);

            var refused = MissingDeveloperEnvironment(LegPrograms.DeveloperEnvironmentOf(config, selected.Leg, workload), report)
                ?? MissingPrograms(LegPrograms.For(config, selected.Leg, workload, settings), report, FloorRemedy(config, selected.Name, selected.Leg, report.Os));

            return refused is { } missing
                ? new LegPlacement(selected, null, at + missing.Reason) { Verdict = missing.Verdict }
                : new LegPlacement(selected, report, null);
        }

        return new LegPlacement(selected, null, reasons.Count == 0 ? "no host was measured for it" : string.Join("; ", reasons));
    }

    /// <summary>
    /// What stops <paramref name="host"/> from taking <paramref name="leg"/> at all, before its
    /// programs are asked about: it is unreachable, the wrong machine, or its emulator does not work
    /// there. <see langword="null"/> when the host can take it: the one test that decides which host
    /// a leg goes to, and which hosts are measured for it.
    /// </summary>
    /// <param name="leg">The leg.</param>
    /// <param name="host">What measuring the host found.</param>
    public static string? PlatformObstacle(LegConfig leg, HostReport host)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(host);

        if (!host.Available)
        {
            return host.Reason;
        }

        if (!Same(host.Os, leg.Os))
        {
            return $"it runs {host.Os}, and the leg needs {leg.Os}";
        }

        if (leg.Emulator is null)
        {
            return Same(host.Processor, leg.Processor)
                ? null
                : $"it is {host.Processor}, and the leg runs natively on {leg.Processor}";
        }

        if (!host.Emulators.TryGetValue(leg.Emulator, out var check))
        {
            return $"emulator '{leg.Emulator}' was not checked there";
        }

        return check.Available ? null : $"emulator '{leg.Emulator}' cannot run there: {check.Reason}";
    }

    /// <summary>
    /// Why the developer environment <paramref name="name"/> cannot be set up on <paramref name="host"/>,
    /// said as one reason with what a run records for it, or <see langword="null"/> where it can, or the
    /// leg needs none.
    /// </summary>
    /// <remarks>
    /// Its own check rather than one of the programs: it is found by Visual Studio's installer, not on
    /// a PATH, and what it would put on the PATH is exactly what no survey can see beforehand. The three
    /// facts a program's check keeps apart are kept apart here: not there is a tool missing; not known,
    /// the host having failed to look, is no claim that it is missing; and never asked is a defect in
    /// this tool, since a host is asked about every environment a leg it might run starts in.
    /// </remarks>
    private static (string Reason, LegVerdict Verdict)? MissingDeveloperEnvironment(string? name, HostReport host)
    {
        if (name is null)
        {
            return null;
        }

        if (!host.DeveloperEnvironments.TryGetValue(name, out var check))
        {
            return (
                $"whether developer environment '{name}' can be set up there was never asked, which is a defect in this "
                + "tool: a host is asked about every developer environment a leg starts in",
                LegVerdict.Poisoned);
        }

        if (check.CanSetUp)
        {
            return null;
        }

        return check.Found == DeveloperEnvironmentFound.Nowhere
            ? ($"developer environment '{name}' cannot be set up there: {check.Reason}", LegVerdict.SkippedToolMissing)
            : ($"whether developer environment '{name}' can be set up there could not be established: {check.Reason ?? "the host named no instance"}", LegVerdict.SkippedUnavailable);
    }

    /// <summary>
    /// The programs of <paramref name="programs"/> that <paramref name="host"/> is not known to have,
    /// said as one reason with what a run records for it, or <see langword="null"/> when it has them all.
    /// </summary>
    /// <remarks>
    /// Read from what the host itself found, with the search a leg there will use. Three different
    /// facts, kept apart: a program the search looked for everywhere and did not find is missing; one
    /// it could not look for everywhere is not known either way, and no refusal may call it missing;
    /// and one the host was never asked about is a defect in this tool, since a host is asked about
    /// every program any leg starts. A survey that had not looked must not read as one that looked
    /// and found nothing, nor as one that found it.
    /// </remarks>
    private static (string Reason, LegVerdict Verdict)? MissingPrograms(
        IReadOnlyList<string> programs,
        HostReport host,
        Func<string, string?> floorRemedy)
        => MissingPrograms(programs, program => host.Programs.GetValueOrDefault(program), "the PATH a command run there sees", floorRemedy);

    /// <summary>
    /// What the leg <paramref name="name"/> is told to do about a tool it starts whose every copy on a host running
    /// <paramref name="os"/> is below the tool's minimum version, or <see langword="null"/> where the tool is not declared
    /// for it: its minimum is another leg's business, and the leg starts whichever copy the host finds - the one raised
    /// for the legs it is declared for, where one was.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="name">The leg's name.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="os">The operating system of the host it runs on.</param>
    /// <remarks>
    /// A host holds every copy of a tool to the tool's minimum on every platform the tool is declared for, because it
    /// answers for all of its legs at once; whether the minimum is this leg's is the tool's whole scope, which only the
    /// leg can say. <c>install-missing-tools</c> is named where the tool declares how to install it there, so that the
    /// remedy said is one that can be run.
    /// </remarks>
    internal static Func<string, string?> FloorRemedy(HarnessConfig config, string name, LegConfig leg, string? os)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(leg);

        return program => config.Tools.FirstOrDefault(tool => string.Equals(tool.Name, program, StringComparison.Ordinal)) is { } tool
            && ToolScope.Covers(tool, config, name, leg)
                ? PlatformScope.Select(tool.Install, os) is null
                    ? FloorCopyWanted
                    : $"run '{ToolPackage.Command} {ToolProvisionService.CommandName} --legs {name}', which installs one as its tools "
                        + $"entry declares, or {FloorCopyWanted}"
                : null;
    }

    /// <summary>Where a copy that meets a tool's minimum version has to be, for a leg to start it.</summary>
    private const string FloorCopyWanted
        = "put a copy of at least that version where a leg there finds programs: on the PATH, or in a directory searched for programs";

    /// <summary>
    /// The programs of <paramref name="programs"/> that <paramref name="located"/> does not find, said
    /// as one reason with what a run records for it, or <see langword="null"/> when it finds them all.
    /// </summary>
    /// <param name="programs">The programs to find.</param>
    /// <param name="located">Where each program is, or <see langword="null"/> where nothing was ever asked about it.</param>
    /// <param name="path">The PATH they were looked for on, as a refusal names it.</param>
    /// <param name="floorRemedy">
    /// What the leg is told to do about a program every copy of which is below the minimum version its tool is
    /// declared with (see <see cref="FloorRemedy"/>), or <see langword="null"/> where that minimum is not the leg's,
    /// and a program held to it counts as found.
    /// </param>
    /// <remarks>
    /// One reading for every look: a host's survey, and a leg checking the PATH its developer
    /// environment set up before anything of it starts. See the overload above for the three facts it
    /// keeps apart. A declared tool held to a minimum version that is the leg's adds three more, kept apart the
    /// same way: below it, with every copy older, is a tool missing, which an install can settle; one whose
    /// copy that meets it a directory raised for another tool shadows is a tool missing too, whose reason says
    /// what to update; and one no copy of which could be shown to meet it is not known either way. Either of
    /// the first two would run the leg's work under a version its configuration says it does not use, to fail,
    /// when it fails, for a reason that names something else.
    /// </remarks>
    internal static (string Reason, LegVerdict Verdict)? MissingPrograms(
        IReadOnlyList<string> programs,
        Func<string, ProgramLocation?> located,
        string path,
        Func<string, string?> floorRemedy)
    {
        ArgumentNullException.ThrowIfNull(floorRemedy);

        var absent = new List<string>();
        var nothingAt = new List<string>();
        var unknown = new List<string>();
        var unasked = new List<string>();
        var below = new List<string>();

        foreach (var program in programs)
        {
            var location = located(program);

            switch (location)
            {
                case { Present: true }:
                    break;

                // Its reason says what each copy said; only every copy being older is one an install settles.
                case { Found: ProgramFound.BelowFloor } when floorRemedy(program) is { } remedy:
                    below.Add($"{location.Reason}; {remedy}");
                    break;
                case { Found: ProgramFound.FloorShadowed } when floorRemedy(program) is not null:
                    below.Add(location.Reason!);
                    break;
                case { Found: ProgramFound.FloorUnknown } when floorRemedy(program) is not null:
                    unknown.Add(location.Reason!);
                    break;
                case { HeldToFloor: true }:
                    // A minimum that is another leg's: the copy is there, and this leg starts it.
                    break;
                case { Found: ProgramFound.Nowhere }:
                    (ProcessRunner.IsPath(program) ? nothingAt : absent).Add(program);
                    break;
                case { Found: ProgramFound.Unreadable }:
                    unknown.Add(location.Unestablished());
                    break;
                default:
                    unasked.Add(program);
                    break;
            }
        }

        var said = new List<string>();

        if (absent.Count > 0)
        {
            said.Add(
                $"{Quoted(absent)} {(absent.Count == 1 ? "is" : "are")} not installed there: neither on {path} "
                + "nor in any directory searched for programs; install "
                + $"{(absent.Count == 1 ? "it" : "them")}, or name the directory under toolSearchDirectories");
        }

        // A program named by its path is looked for at that path and nowhere else, so no directory
        // searched for programs has anything to do with it.
        if (nothingAt.Count > 0)
        {
            said.Add(
                $"nothing is at {Quoted(nothingAt)} there; install {(nothingAt.Count == 1 ? "it" : "them")} "
                + "at that path, or name the program where it is");
        }

        said.AddRange(below);
        said.AddRange(unknown);

        if (unasked.Count > 0)
        {
            said.Add(
                $"whether {Quoted(unasked)} {(unasked.Count == 1 ? "is" : "are")} there was never asked, which is a "
                + "defect in this tool: a host is asked about every program a leg starts");
        }

        if (said.Count == 0)
        {
            return null;
        }

        var verdict = unasked.Count > 0 ? LegVerdict.Poisoned
            : absent.Count + nothingAt.Count + below.Count > 0 ? LegVerdict.SkippedToolMissing
            : LegVerdict.SkippedUnavailable;

        return (string.Join("; ", said), verdict);
    }

    private static string Quoted(IEnumerable<string> programs) => string.Join(", ", programs.Select(program => $"'{program}'"));

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
