using System.ComponentModel;

namespace DssHarness.Core.Configuration;

/// <summary>
/// The whole of <c>.harness-config/config.json</c>. Every behaviour the harness has
/// is declared here: nothing about a specific repository, language or toolchain is
/// compiled into the tool. A behaviour that cannot be expressed here is a defect.
/// </summary>
/// <remarks>
/// Every section here is read by a command. A section nothing reads is a key a file plainly
/// appears to set and that changes nothing, which is the same defect as a misspelt one — and that
/// is why an unknown key is refused when the file is read rather than passed over.
/// </remarks>
public sealed class HarnessConfig
{
    /// <summary>Schema version, so later changes can be migrated rather than guessed.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Settings used wherever a more specific section does not override them.</summary>
    public HarnessDefaults Defaults { get; init; } = new();

    /// <summary>Compilers, and the environment or cache variables that select them.</summary>
    public Dictionary<string, ToolchainConfig> Toolchains { get; init; } = Map<ToolchainConfig>();

    /// <summary>Instrumentation overlays, composed onto a build like a toolchain.</summary>
    public Dictionary<string, VariantOverlay> Sanitizers { get; init; } = Map<VariantOverlay>();

    /// <summary>Named build configurations: debug, release, o1, o2 and so on.</summary>
    public Dictionary<string, BuildConfiguration> BuildConfigs { get; init; } = Map<BuildConfiguration>();

    /// <summary>Buildable projects in this repository.</summary>
    public List<ProjectConfig> Projects { get; init; } = [];

    /// <summary>
    /// The machines legs run on: this one, and the WSL distributions and ssh hosts declared under
    /// the names the command line selects them by.
    /// </summary>
    public HostsConfig Hosts { get; init; } = new();

    /// <summary>Ways to run programs built for another processor, keyed by name.</summary>
    public Dictionary<string, EmulatorConfig> Emulators { get; init; } = Map<EmulatorConfig>();

    /// <summary>
    /// Environments a toolchain's builds need set up on the host that runs them, such as Visual
    /// Studio's, keyed by name and named by each toolchain that needs one.
    /// </summary>
    public Dictionary<string, DeveloperEnvironmentConfig> DeveloperEnvironments { get; init; } = Map<DeveloperEnvironmentConfig>();

    /// <summary>Units of work that each receive exactly one verdict, keyed by name.</summary>
    public Dictionary<string, LegConfig> Legs { get; init; } = Map<LegConfig>();

    /// <summary>
    /// Named groups of legs, selected with <c>--legs</c> alongside leg names, so a whole gate can be
    /// invoked by one name.
    /// </summary>
    public Dictionary<string, List<string>> LegSets { get; init; } = Map<List<string>>();

    /// <summary>External tools this repository needs, verified and installed by name.</summary>
    public List<ToolConfig> Tools { get; init; } = [];

    /// <summary>
    /// Where a program is looked for when the PATH a command sees does not name it, by platform or
    /// <c>all</c>. A platform's list replaces the built-in one only where it names something that
    /// platform can search.
    /// </summary>
    /// <remarks>
    /// Left out, a host searches the directories this build already knows to look in: see
    /// <see cref="Hosts.ToolSearchDirectories"/>. Declared, it is the whole list for that platform, so a
    /// tool installed somewhere unusual is found by naming its directory here rather than by editing a
    /// shell's startup files on every host. Given empty, it is refused when the file is read, rather
    /// than read as the built-in list; a list a caller builds empty reads as left out.
    /// </remarks>
    public Dictionary<string, List<string>> ToolSearchDirectories { get; init; } = Map<List<string>>();

    /// <summary>
    /// Multi-phase procedures such as a corpus build-and-test or a benchmark, keyed by the name
    /// <c>run</c> selects them by and a run check names them by. Keyed rather than listed because a
    /// check selects one by name, and an unnamed entry could not be selected at all.
    /// </summary>
    public Dictionary<string, RunnerConfig> PredefinedRunners { get; init; } = Map<RunnerConfig>();

    /// <summary>
    /// The action each runner that declares one names, keyed by the runner's name. A runner
    /// declaring <c>phases</c> instead contributes nothing.
    /// </summary>
    public IEnumerable<KeyValuePair<string, string>> ActionsByRunner()
        => PredefinedRunners
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value.Action))
            .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value.Action!));

    /// <summary>
    /// The runners a run of <paramref name="runner"/> has write the tree it works in (<c>writesTree</c>), in order: the
    /// runner itself where it says so, then each runner its run checks name that does - each once, as
    /// <see cref="RunnerConfig.RunCheckRunners"/> names it, for a run check runs within the run's legs and writes where
    /// they work. Empty where none writes, and for a runner nobody declared, which is refused where it is run. The one
    /// answer the definition's check (V24), the run command's placement refusals, a run step's claim and where its legs
    /// work all read.
    /// </summary>
    /// <param name="runner">The runner's name, in any case.</param>
    public IReadOnlyList<(string Runner, bool IsCheck)> WritingRunners(string runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        if (!PredefinedRunners.TryGetValue(runner, out var declared))
        {
            return [];
        }

        return
        [
            .. declared.WritesTree ? [(runner, false)] : Array.Empty<(string, bool)>(),
            .. declared.RunCheckRunners()
                .Where(check => PredefinedRunners.TryGetValue(check, out var checkRunner) && checkRunner.WritesTree)
                .Select(check => (check, true)),
        ];
    }

    /// <summary>
    /// The workflows this repository declares, by name and nothing else: each name is a definition
    /// file of its own, <c>.harness-config/workflows/&lt;name&gt;/&lt;name&gt;.yml</c>, which holds
    /// the whole flow and whose directory holds the files the flow names. Keyed by neither section nor
    /// entry because a workflow's definition never lives in this file.
    /// </summary>
    /// <remarks>
    /// Given empty in the file, it is refused when the file is read, rather than read as no workflow.
    /// </remarks>
    [Description("the workflows this repository runs, each defined by workflows/<name>/<name>.yml; validate-workflow checks them")]
    public List<string>? Workflows { get; init; }

    /// <summary>
    /// Named set-ups, each the command line that brings up what the repository's work needs on this machine and the one
    /// that brings it down: run by <c>up &lt;name&gt;</c> and <c>down &lt;name&gt;</c>, and around a leg's work where
    /// what the leg runs names one and the run is asked to set up.
    /// </summary>
    public Dictionary<string, SetUpConfig> SetUp { get; init; } = Map<SetUpConfig>();

    /// <summary>Commit message templating and policy.</summary>
    public CommitConfig Commit { get; init; } = new();

    /// <summary>What the tree mirror carries, and what it must never carry.</summary>
    public SyncConfig Sync { get; init; } = new();

    /// <summary>Processes outside the harness that can corrupt a leg's result while it runs.</summary>
    public ContentionConfig Contention { get; init; } = new();

    /// <summary>Worktree creation policy.</summary>
    public WorktreeSettings Worktrees { get; init; } = new();

    /// <summary>The anchor registries: where they live, and how a new anchor id is spelled.</summary>
    public AnchorSettings Anchors { get; init; } = new();

    /// <summary>
    /// Mutation testing: the arms registry <c>check-mutations</c> drives, its texts, the worker copies a leg sweeps
    /// with, and how a test binary writes the report each arm is judged by.
    /// </summary>
    public MutationSettings Mutations { get; init; } = new();

    /// <summary>
    /// The directories under <c>.harness-config/sshItems</c> holding each ssh host's connection data,
    /// named here rather than described here: an address, a user and a key are not the repository's
    /// business, and the tracked configuration must never carry them.
    /// </summary>
    public List<string> SshItems { get; init; } = [];

    /// <summary>
    /// The directories under <c>.harness-config/wslDistros</c> holding each distribution's connection
    /// data. Only a Windows machine has any.
    /// </summary>
    public List<string> WslDistros { get; init; } = [];

    /// <summary>
    /// Patterns whose every match on a line is pulled out of a build's output when <c>build --time</c>
    /// runs - a line at a time, the first 10,000 marks of a phase - so timings come from what the build
    /// itself reported rather than from the harness guessing which part of the wall clock was the
    /// build. Configured with none, <c>--time</c> measures the phases itself.
    /// </summary>
    public List<string> BuildTimingRegex { get; init; } = [];

    /// <summary>Patterns whose every match on a line is pulled out of a predefined run's output under <c>--time</c>, as a build's are.</summary>
    public List<string> RunTimingRegex { get; init; } = [];

    /// <summary>Patterns whose every match on a line is pulled out of a test's output under <c>--time</c>, as a build's are.</summary>
    /// <remarks>
    /// Separate from <see cref="BuildTimingRegex"/> because <c>test</c> builds first and then tests:
    /// one pattern list covering both would attribute a build's marks to the test phase, and the
    /// whole point of asking is to tell the two apart.
    /// </remarks>
    public List<string> TestTimingRegex { get; init; } = [];

    /// <summary>The repository's line-ending policy, and what stands outside it.</summary>
    public LineEndingSettings LineEndings { get; init; } = new();

    /// <summary>Continuous integration: the workflows that carry this repository's legs, and their budget.</summary>
    public CiSettings Ci { get; init; } = new();

    /// <summary>
    /// The companies whose models the tool reaches: a model's provider, its API's address, its key's name and its
    /// client, and everything known of the model, are its entry here. Nothing of a model is in the tool's code - a
    /// config that declares none has no model.
    /// </summary>
    public List<AgentProviderConfig> Providers { get; init; } = [];

    /// <summary>
    /// The tiers - a name for a class of model - each with its models in the order they are tried: a step on the tier
    /// starts on the first, and the rest are its fallback. A tier is defined here and nowhere else.
    /// </summary>
    public Dictionary<string, List<string>> TierConfig { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a provider's keys are balanced by what each spent in the last seven days, a step starting on the one
    /// that spent least: off (<c>false</c>) when left out, and <c>init</c> writes none. Refused where it is on and a
    /// provider with two keys or more lists a model with no price, whose spend could not be counted.
    /// </summary>
    public bool? BalanceKeyUsage { get; init; }

    /// <summary>
    /// The configuration's clients, by the kind of client each is: a name the tool builds in, its entry saying how a
    /// model is reached through it. Each key is read by the slice that builds what it says; <c>init</c> writes none,
    /// and a config that declares none uses every client as it is built in.
    /// </summary>
    public Dictionary<string, AgentClientConfig> AgentClients { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What an agent of a run may do beyond the rules built into the tool: restrictions that only ever refuse.
    /// Left out where the repository restricts nothing, which is the default.
    /// </summary>
    public AgentPolicyConfig? AgentPolicy { get; init; }

    /// <summary>
    /// The harness's own web search: the sources an agent's <c>web_search</c> asks, laid over the built-in ones key by
    /// key, what a step that names none searches, and its bounds. Left out, the built-in sources hold; <c>init</c> writes
    /// them, so that they are there to be read and changed.
    /// </summary>
    public AgentWebSearchConfig? AgentWebSearch { get; init; }

    /// <summary>
    /// What an agent is given of the work it goes on with, and of the run around it: the bounds of the continuation
    /// record. Left out, the defaults hold - each bound a repository changes where its agents work better with more,
    /// or its models hold less.
    /// </summary>
    public AgentContextConfig? AgentContext { get; init; }

    private static Dictionary<string, TValue> Map<TValue>() => new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// What an agent is given of the work it goes on with, under <c>agentContext</c>: the continuation record's bounds.
/// How much of earlier work an agent needs is not the same for every repository, so each is a key and not a constant;
/// the rule that nothing is cut in the middle and that whatever is cut is named is not configurable.
/// </summary>
public sealed class AgentContextConfig
{
    /// <summary>The continuation record's bounds. Left out, the defaults hold.</summary>
    public AgentContinuationConfig? Continuation { get; init; }

    /// <summary>The compaction's bounds. Left out, the defaults hold.</summary>
    public AgentCompactionConfig? Compaction { get; init; }
}

/// <summary>The bounds of the continuation record, under <c>agentContext.continuation</c>: each a positive number, each with its default.</summary>
public sealed class AgentContinuationConfig
{
    /// <summary>The most characters of the record's own parts (all but the last iterations): 60,000 where left out.</summary>
    public int? RecordChars { get; init; }

    /// <summary>The most characters of one step's notes and of its output, each its own bound: 16,000 where left out.</summary>
    public int? StepChars { get; init; }

    /// <summary>The most changed paths listed, the count of the rest said: 300 where left out.</summary>
    public int? Paths { get; init; }

    /// <summary>How many of the last iterations are kept whole, whatever their size: 3 where left out.</summary>
    public int? LastIterations { get; init; }

    /// <summary>The most characters of the earlier iterations kept, none cut in the middle of one: 120,000 where left out.</summary>
    public int? IterationChars { get; init; }

    /// <summary>Each bound said, where one is not a positive number; empty where every one is.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        foreach (var (name, value) in new (string Name, int? Value)[]
        {
            ("agentContext.continuation.recordChars", RecordChars),
            ("agentContext.continuation.stepChars", StepChars),
            ("agentContext.continuation.paths", Paths),
            ("agentContext.continuation.lastIterations", LastIterations),
            ("agentContext.continuation.iterationChars", IterationChars),
        })
        {
            if (value is < 1)
            {
                problems.Add($"'{name}' is {value}, and a bound of the continuation record is a positive number");
            }
        }

        return problems;
    }
}

/// <summary>The bounds of the engine's compaction, under <c>agentContext.compaction</c>: each a positive number, each with its default.</summary>
public sealed class AgentCompactionConfig
{
    /// <summary>The most characters of the compaction record's reduced part: 200,000 where left out.</summary>
    public int? ReducedChars { get; init; }

    /// <summary>The most characters of a call's input in a reduced line: 400 where left out.</summary>
    public int? CallInputChars { get; init; }

    /// <summary>Each bound said, where one is not a positive number; empty where every one is.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        foreach (var (name, value) in new (string Name, int? Value)[]
        {
            ("agentContext.compaction.reducedChars", ReducedChars),
            ("agentContext.compaction.callInputChars", CallInputChars),
        })
        {
            if (value is < 1)
            {
                problems.Add($"'{name}' is {value}, and a bound of the compaction is a positive number");
            }
        }

        return problems;
    }
}

/// <summary>Settings used wherever a more specific section does not override them.</summary>
public sealed class HarnessDefaults
{
    /// <summary>Cores a build or a test run uses unless something more specific says otherwise.</summary>
    public const int DefaultCores = 6;

    /// <summary>
    /// Cores a build uses. Deliberately not "every core": a machine entirely claimed by
    /// a build cannot be used for anything else while it runs. A host with a different
    /// core count replaces this with its own <c>buildCores</c>.
    /// </summary>
    public int BuildCores { get; init; } = DefaultCores;

    /// <summary>
    /// Cores a test run uses, for the same reason. Replaced by a host's
    /// <c>testCores</c>, and by a test invocation's own <c>cores</c>.
    /// </summary>
    public int TestCores { get; init; } = DefaultCores;

    /// <summary>
    /// Most legs a command runs at once <em>on any one physical machine</em>, or
    /// <see langword="null"/> for no per-machine cap.
    /// </summary>
    /// <remarks>
    /// Counted per machine, not across the run: a local leg and every WSL leg are one machine's
    /// processors however differently they are named, while an ssh host shares nothing with either.
    /// A cap across every leg had to be set low enough for the busiest machine, which left every
    /// other host idle. Legs are isolated from one another, so this is never needed for correctness;
    /// it keeps one command from asking a machine for more than it has. Separate commands on one
    /// machine are counted by <see cref="Admission"/>.
    /// </remarks>
    public int? MaxParallelLegs { get; init; }

    /// <summary>
    /// Most legs a command runs at once across every machine together, or <see langword="null"/> for
    /// no overall ceiling. Applied on top of <see cref="MaxParallelLegs"/>, for what a fleet shares
    /// even when its machines do not: a license server, a network share, a sync's bandwidth.
    /// </summary>
    public int? MaxParallelLegsTotal { get; init; }

    /// <summary>Project used when a command names none.</summary>
    public string? Project { get; init; }

    /// <summary>
    /// The tier of a step that names neither a tier nor a model. Nothing is built in: <c>init</c> writes
    /// <c>balanced</c>, and where the config names none, such a step is refused when the definition is checked. (An
    /// agent definition's own tier or model stands before it once definitions are read - not yet in this build.)
    /// </summary>
    public string? AgentTier { get; init; }

    /// <summary>
    /// The rule heavy legs are admitted by on a machine whose own section declares nothing, and each field such a
    /// section leaves out; <see langword="null"/> where only machines that declare their own admit heavy legs: see
    /// <see cref="AdmissionSettings"/>.
    /// </summary>
    public AdmissionSettings? Admission { get; init; }

    /// <summary>
    /// Seconds without output after which a phase is treated as hung; zero disables
    /// the check. A stall bound is used rather than a wall clock budget because
    /// output cadence stays stable even when total duration does not.
    /// </summary>
    public int StallSeconds { get; init; }

    /// <summary>
    /// How many times longer than the same phase on a sibling leg of the same kind
    /// a phase may take before the ledger marks its timings suspect; zero disables the
    /// mark. The mark never changes a verdict: a host that slept, or a clock that stepped,
    /// makes a duration meaningless, and whether the code passed is a separate fact.
    /// </summary>
    public double DurationWarningFactor { get; init; } = 3.0;

    /// <summary>
    /// Milliseconds wall-clock time may drift from monotonic time across one phase before the
    /// phase is recorded as spanning a clock step or a host sleep. Durations from such a phase
    /// are suspect, and so is every mtime it stamped, so an incremental build that would trust
    /// those stamps is rebuilt instead.
    /// </summary>
    public int ClockStepToleranceMilliseconds { get; init; } = 2000;

    /// <summary>
    /// Seconds between samples of the process table while a leg runs, in addition to the
    /// samples always taken as it starts and ends. Every sample is kept: a contender that
    /// started and finished between the first and the last would otherwise go unreported.
    /// </summary>
    public int ProcessSampleSeconds { get; init; } = 5;

    /// <summary>
    /// Where this machine runs a leg itself, it works in a copy of its tree kept in the tree's own .legs: a default for
    /// every leg, which a leg's own <see cref="LegConfig.LocalCopy"/> - <see langword="true"/> or <see langword="false"/>
    /// - overrides, and the command's <c>--local-copy</c> or <c>--in-place</c> overrides both. Says nothing of a leg a
    /// host runs, which always works in that host's copy of its tree.
    /// </summary>
    public bool LocalCopy { get; init; }
}
