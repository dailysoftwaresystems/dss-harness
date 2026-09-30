using System.ComponentModel;
using System.Text.Json.Serialization;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// A multi-phase procedure, such as a corpus build-and-test or a benchmark.
/// Runners exist so that procedures specific to one repository live in
/// configuration rather than in the tool.
/// </summary>
/// <remarks>
/// What <c>help runners</c> says of each key here, and of each section under it, is that key's
/// <see cref="DescriptionAttribute"/>, listed through <see cref="ConfigKeys"/>.
/// </remarks>
public sealed class RunnerConfig
{
    /// <summary>What this runner is for, shown in help and in the ledger.</summary>
    [Description("what the runner is for, shown in help and in the ledger")]
    public string? Description { get; init; }

    /// <summary>
    /// Legs this runner executes against when <c>--legs</c> names none, or <see langword="null"/> for every
    /// declared leg. Given empty in the file, it is refused when the file is read, rather than read as every leg;
    /// a list a caller builds empty reads as left out.
    /// </summary>
    [Description("the legs it runs when --legs names none; absent, every leg")]
    public List<string>? Legs { get; init; }

    /// <summary>Phases executed in order; a failing phase ends that leg.</summary>
    /// <remarks>
    /// A runner declares <see cref="Phases"/> or <see cref="Action"/>, never both. Two descriptions
    /// of what a runner does would eventually disagree, and nothing could say which one ran.
    /// </remarks>
    [Description("what it runs, in order, when it names no action")]
    public List<RunnerPhase> Phases { get; init; } = [];

    /// <summary>
    /// The action file under <c>.harness-config/runner/actions</c> holding this runner's steps, in
    /// place of <see cref="Phases"/>. Every field a phase carries is a key on a step, so nothing the
    /// verdict contract depends on is lost by declaring one instead of the other.
    /// </summary>
    [Description("its action file, '<name>/<name>.yml', in place of phases")]
    public string? Action { get; init; }

    /// <summary>
    /// The steps of <see cref="Action"/> this runner runs, manual or not, each with the steps it needs;
    /// or <see langword="null"/> for every step that is not manual.
    /// </summary>
    /// <remarks>
    /// So that work belonging to one action - a benchmark beside the build and test it shares its
    /// modules with - can be a runner of its own, with legs of its own, that a gate or CI names like any
    /// other. Checked against the action when it is read, before any leg's run has begun.
    /// </remarks>
    [Description("the steps of its action it runs; absent, all but the manual")]
    public List<string>? Steps { get; init; }

    /// <summary>
    /// Whether this runner needs the repository built before it runs. A runner that calls a program
    /// the build produces otherwise runs against whatever was left there.
    /// </summary>
    /// <remarks>
    /// This gates the build alone, never the sync. A leg on an ssh host or a WSL distribution runs
    /// from that host's own copy of the tree — the host reads <c>config.json</c> and this runner's
    /// action file from it — so the tree is put there whether or not anything is compiled. Use
    /// <c>--use-staged</c> to run against a copy already known to be current.
    /// </remarks>
    [Description("build the leg before it runs")]
    public bool RequireBuild { get; init; }

    /// <summary>
    /// Whether each leg of this runner is heavy - taking one of its machine's heavy-leg slots before it starts, where
    /// that machine declares admission - or <see langword="null"/> to be heavy only where it builds, as a runner that
    /// requires the build is. Given false beside <see cref="RequireBuild"/> it is refused: its build is heavy.
    /// </summary>
    [Description("its legs take a heavy-leg slot ('help admission'); absent, only where it builds")]
    public bool? Heavy { get; init; }

    /// <summary>
    /// Seconds without output after which a phase of this runner is treated as hung, replacing
    /// <c>defaults.stallSeconds</c>. A stall bound rather than a time budget: output cadence stays
    /// stable even when total duration does not.
    /// </summary>
    [Description("the stall bound of its phases and steps, in seconds")]
    public int? StallSeconds { get; init; }

    /// <summary>
    /// Failures this runner is allowed to produce, each with the outcome to report instead of an
    /// unexplained failure, and each gated on the checks that confirm it.
    /// </summary>
    [Description("failures it may produce, and what to report instead")]
    public List<ExpectedException> ExpectedExceptions { get; init; } = [];

    /// <summary>
    /// Directories under the leg's work directory to wipe before every run. Use for
    /// run and scratch directories, whose contents must never carry across runs.
    /// Build directories are deliberately not listed: they stay incremental.
    /// </summary>
    [Description("directories in the leg's tree deleted before every run")]
    public List<string> CleanDirectories { get; init; } = [];

    /// <summary>Environment applied to every phase.</summary>
    [Description("variables every phase and step gets")]
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One phase of a runner.</summary>
/// <remarks>
/// A record so that a phase derived from another is written as a copy with the one field changed,
/// rather than rebuilt member by member. Rebuilt, every field added later has to be remembered at
/// every rebuild site, and the one that is forgotten is silently false: this shipped once, and what
/// it dropped was <see cref="WatchContention"/> and <see cref="RequireInputsUnmoved"/> — two
/// guards, off, in exactly the action files that declared inputs.
/// </remarks>
public sealed record RunnerPhase
{
    /// <summary>Name, used in progress output and to name this phase's log file.</summary>
    [Description("names its progress lines and its log")]
    public required string Name { get; init; }

    /// <summary>Command and arguments, one element per argument; never a shell string.</summary>
    [Description("the program and its arguments, one each; no shell")]
    public required List<string> Command { get; init; }

    /// <summary>Working directory, relative to the leg's work directory.</summary>
    [Description("where it runs, relative to the leg's tree; absent, the tree")]
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment for this phase.</summary>
    [Description("its own variables ('help config' gives the order)")]
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pattern proving the phase ran, checked in addition to its exit code.</summary>
    [Description("what a line of its output must match, besides exiting 0")]
    public string? SuccessPattern { get; init; }

    /// <summary>Seconds without output after which this phase is treated as hung.</summary>
    [Description("its own stall bound, in seconds")]
    public int? StallSeconds { get; init; }

    /// <summary>Whether a failure here ends the leg or is recorded and passed over.</summary>
    [Description("a failure here is recorded, and the leg goes on")]
    public bool ContinueOnError { get; init; }

    /// <summary>
    /// Whether the process table is watched while this runs, so another run building in the same
    /// directory is reported rather than silently shared with.
    /// </summary>
    /// <remarks>
    /// Needs a build directory to watch, which is the leg's. A run reaching no leg has none, and
    /// that is refused when the action runs rather than passed over: a guard that watched nothing
    /// would report a clean directory without having looked at one.
    /// </remarks>
    [Description("report another run building in the leg's build directory")]
    public bool WatchContention { get; init; }

    /// <summary>
    /// Whether the tracked files are fingerprinted before, during and after this, so a tree edited
    /// while it ran is reported rather than producing a result that describes no tree that existed.
    /// </summary>
    [Description("report a tree edited while it ran")]
    public bool RequireInputsUnmoved { get; init; }

    /// <summary>
    /// The step this phase is reported under in <c>ranSteps</c> and, for a step of an action file, the
    /// directory its work goes in. Empty, the phase is its own step; a runner's own phase may name one,
    /// and owns no action directory either way.
    /// </summary>
    [Description("the step it is reported under in ranSteps; absent, its name")]
    public string StepName { get; init; } = string.Empty;

    /// <summary>What this phase must have produced, relative to its step's own build directory.</summary>
    /// <remarks>
    /// Never read from <c>config.json</c>: outputs are checked in the step's directory under its
    /// action's build, and kept in that action's artifacts, and a runner of phases owns neither.
    /// Accepted there, it was a check nobody made: the phase passed without it. The file's reader
    /// refuses it on a phase, and <see cref="MisplacedKeys"/> says a step of an action file takes it.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>Whether this step's outputs survive the run.</summary>
    /// <remarks>Never read from <c>config.json</c>, for the reason <see cref="Outputs"/> is not.</remarks>
    [JsonIgnore]
    public bool Persist { get; init; }
}

/// <summary>A named command invokable through <c>DssHarness exec</c>.</summary>
public sealed class ExecConfig
{
    /// <summary>Executable to run.</summary>
    public required string Command { get; init; }

    /// <summary>Arguments; any given on the command line are appended to these.</summary>
    public List<string> Args { get; init; } = [];

    /// <summary>Working directory, relative to the tree root.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment for the command.</summary>
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the command does, shown in help.</summary>
    public string? Description { get; init; }
}
