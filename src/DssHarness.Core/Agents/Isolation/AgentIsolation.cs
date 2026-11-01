using System.Text.Json.Serialization;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Agents.Isolation;

/// <summary>
/// <c>agentPolicy.isolation</c>: whether an agent may start where this machine has no operating-system isolation of its
/// commands. <see cref="Required"/> where nothing is said - no agent that may run a command starts without it - and
/// <see cref="Preferred"/> runs one on the words policy alone, said at every launch and in its records.
/// </summary>
public static class AgentIsolationMode
{
    /// <summary>No agent that may run a command starts without the isolation.</summary>
    public const string Required = "required";

    /// <summary>An agent starts without the isolation where there is none, said at every launch and in its records.</summary>
    public const string Preferred = "preferred";

    /// <summary>Every value the key takes, in the order help names them.</summary>
    public static IReadOnlyList<string> Modes { get; } = [Required, Preferred];

    /// <summary>The configuration's mode: its own where it names one, <see cref="Required"/> where it names none.</summary>
    /// <param name="config">The configuration.</param>
    public static string Of(HarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.AgentPolicy?.Isolation ?? Required;
    }

    /// <summary>Why the configuration's <c>agentPolicy.isolation</c> does not read, where it does not: a value none of <see cref="Modes"/>, matched exactly.</summary>
    /// <param name="policy">The configuration's <c>agentPolicy</c>; null where it has none.</param>
    public static string? Problem(AgentPolicyConfig? policy)
        => policy?.Isolation is { } given && !Modes.Contains(given, StringComparer.Ordinal)
            ? $"agentPolicy.isolation '{given}' is none of: {string.Join(", ", Modes)}"
            : null;
}

/// <summary>What a launch's records say of the isolation its agent ran under.</summary>
/// <param name="Mode">The configuration's mode (<see cref="AgentIsolationMode"/>).</param>
public sealed record AgentIsolationRecord(string Mode)
{
    /// <summary>The mechanism its commands ran inside; null where none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mechanism { get; init; }

    /// <summary>That mechanism's version; null where none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }

    /// <summary>Why its commands ran inside none; null where they ran inside one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? None { get; init; }

    /// <summary>The record as a step's output holds it: each key the record file holds, each left out where it holds none.</summary>
    public System.Text.Json.Nodes.JsonObject ToOutput()
    {
        var output = new System.Text.Json.Nodes.JsonObject { ["mode"] = Mode };

        foreach (var (key, value) in new[] { ("mechanism", Mechanism), ("version", Version), ("none", None) })
        {
            if (value is not null)
            {
                output[key] = value;
            }
        }

        return output;
    }
}

/// <summary>
/// The decision of the operating system's isolation for one thing that runs what an agent may have written - a launch of
/// an agent, or one of the tool's own executions of an agent's work: the configuration's mode, the mechanism proved on
/// this machine, or why there is none and what would give one. The one place either is refused, said and recorded, so the
/// two never answer the same mode two ways.
/// </summary>
/// <param name="Mode">The configuration's mode (<see cref="AgentIsolationMode"/>).</param>
/// <param name="Mechanism">The mechanism, proved on this machine; null where there is none.</param>
/// <param name="Without">Why it runs under no mechanism, as the opening of a sentence; null where it runs under one.</param>
/// <param name="Remedy">What would give it one, where it has none and needs it; null otherwise.</param>
public abstract record IsolationDecision(string Mode, IIsolationMechanism? Mechanism, string? Without, string? Remedy)
{
    /// <summary>
    /// Whether it may not run: the mode is <see cref="AgentIsolationMode.Required"/>, it needs the isolation, and there
    /// is no mechanism to hold it.
    /// </summary>
    public bool Refused => Mechanism is null && Remedy is not null && Mode == AgentIsolationMode.Required;

    /// <summary>What its records say of it.</summary>
    public AgentIsolationRecord Record => new(Mode) { Mechanism = Mechanism?.Name, Version = Mechanism?.Version, None = Mechanism is null ? Without : null };

    /// <summary>
    /// What is said where it runs with no mechanism though it needs one, under <see cref="AgentIsolationMode.Preferred"/>:
    /// why there is none, <paramref name="so"/>, and what would give one. Null where it runs under one, or needs none.
    /// </summary>
    /// <param name="so">What follows for it, as the sentence's middle says it.</param>
    protected string? NoticeSaying(string so)
        => Mechanism is null && Remedy is not null && !Refused
            ? $"agentPolicy.isolation is \"{AgentIsolationMode.Preferred}\" and {Without}, so {so}; {Remedy}"
            : null;

    /// <summary>
    /// The refusal of <paramref name="what"/> where it may not run (<see cref="Refused"/>), as a sentence: what is refused,
    /// why, <paramref name="so"/>, and what would let it run; null where it may.
    /// </summary>
    /// <param name="what">What is refused, as the refusal names it.</param>
    /// <param name="so">What the mode keeps from running here, as the sentence's middle says it.</param>
    protected string? RefusalSaying(string what, string so)
        => Refused
            ? $"{what} is refused: agentPolicy.isolation is \"{AgentIsolationMode.Required}\" and {Without}, so {so}; {Remedy}"
            : null;
}

/// <summary>
/// The isolation one launch of an agent runs under: the mechanism and the two profiles its children are started with -
/// a command of the API clients' runtime, and the whole Claude Code process, which this tool's mechanism holds with the
/// Claude Code directories open to it - or why it runs under none, and, under <see cref="AgentIsolationMode.Required"/>,
/// the refusal that keeps it from starting.
/// </summary>
/// <param name="Mode">The configuration's mode.</param>
/// <param name="Mechanism">The mechanism, proved on this machine; null where there is none.</param>
/// <param name="Commands">What a command the API clients' runtime runs is denied; null where there is no mechanism.</param>
/// <param name="ClaudeCode">What a Claude Code launch's whole process is denied; null where there is no mechanism.</param>
/// <param name="Without">Why the launch runs under no mechanism, as the opening of a sentence; null where it runs under one.</param>
/// <param name="Remedy">What would give it one, where it has none and needs it; null otherwise.</param>
public sealed record LaunchIsolation(
    string Mode,
    IIsolationMechanism? Mechanism,
    IsolationProfile? Commands,
    IsolationProfile? ClaudeCode,
    string? Without,
    string? Remedy) : IsolationDecision(Mode, Mechanism, Without, Remedy)
{
    /// <summary>Why a launch whose agent may run no command needs no isolation, as its record says it.</summary>
    public const string NoCommands = "the agent may run no command";

    /// <summary>The isolation a command of the API clients' runtime is started with; null where there is none.</summary>
    public ProcessIsolation? ForCommands => Mechanism is not null && Commands is not null ? new ProcessIsolation(Mechanism, Commands) : null;

    /// <summary>The isolation a Claude Code launch's whole process is started with; null where there is none.</summary>
    public ProcessIsolation? ForClaudeCode => Mechanism is not null && ClaudeCode is not null ? new ProcessIsolation(Mechanism, ClaudeCode) : null;

    /// <summary>
    /// What every launch says where it starts on the words policy alone, its agent able to run a command: why there is no
    /// mechanism, and what would give one. Null where it runs under one, or needs none.
    /// </summary>
    public string? Notice => NoticeSaying("the agent's commands run on the words policy alone, with no isolation of the operating system's");

    /// <summary>
    /// The refusal of <paramref name="what"/> where the launch may not start (<see cref="IsolationDecision.Refused"/>), as a
    /// sentence: what is refused, why, and what would let it start; null where it may.
    /// </summary>
    /// <param name="what">What is refused, as the refusal names it: <c>'step'</c>, <c>'run-side-task'</c>.</param>
    public string? Refusal(string what) => RefusalSaying(what, "no agent that may run a command starts here");

    /// <summary>A launch whose agent may run no command: no isolation is needed, and none is asked.</summary>
    /// <param name="mode">The configuration's mode.</param>
    public static LaunchIsolation Unneeded(string mode) => new(mode, null, null, null, NoCommands, null);
}

/// <summary>
/// Which of the tool's own executions of an agent's work an <see cref="ExecutionGround"/> is of: what decides the
/// words its refusal and its notice say.
/// </summary>
public enum IsolatedExecution
{
    /// <summary>The phases of a run's machine step's legs this machine runs itself: what builds, tests and runs the agents' work.</summary>
    Legs,

    /// <summary>A run's commit or push, which runs the repository's hooks over the agents' work.</summary>
    Hooks,
}

/// <summary>
/// The ground of one of the tool's own executions of an agent's work - a leg's phases, or a commit's or a push's git with
/// the repository's hooks: the trees whose git's code-running files nothing it runs writes, the places it writes, and the
/// slots and the tool's state it neither reads nor writes, and writes, as an agent's own commands do not. Built by
/// <see cref="ExecutionGrounds"/>, never by hand.
/// </summary>
/// <param name="Kind">Which execution it is.</param>
/// <param name="Trees">The trees it works over, each asked of git for where it keeps its own files.</param>
/// <param name="WritableRoots">The places it writes, written again wherever they stand inside a place written by nobody.</param>
/// <param name="SlotDirectories">The credential slots of the main checkout, neither read nor written.</param>
/// <param name="StateDirectories">The tool's own state under each tree, written by nobody.</param>
public sealed record ExecutionGround(
    IsolatedExecution Kind,
    IReadOnlyList<string> Trees,
    IReadOnlyList<string> WritableRoots,
    IReadOnlyList<string> SlotDirectories,
    IReadOnlyList<string> StateDirectories);

/// <summary>The builder of every <see cref="ExecutionGround"/>: the one place each of the tool's executions of an agent's work is given its ground.</summary>
public static class ExecutionGrounds
{
    /// <summary>
    /// The ground of a run's machine step's legs on this machine, before any is placed: the tree the step works in - an
    /// agent's worktree, or the main checkout - written, as what its decision is asked of before each leg's own.
    /// </summary>
    /// <param name="config">The configuration, whose worktrees root is among the state.</param>
    /// <param name="mainCheckout">The main checkout, whose slots are hidden.</param>
    /// <param name="tree">The tree the step works in.</param>
    public static ExecutionGround OfLegs(HarnessConfig config, string mainCheckout, string tree)
        => Of(IsolatedExecution.Legs, config, mainCheckout, [tree], [tree]);

    /// <summary>
    /// The ground of one leg's phases on this machine: the tree it works in - the step's tree itself, or the leg's copy of
    /// it - and its records written; the step's tree's git files and state, and the leg's own, written by nobody, but what
    /// the leg writes inside them - its copy, and <paramref name="besides"/>.
    /// </summary>
    /// <param name="config">The configuration, whose worktrees root is among the state.</param>
    /// <param name="mainCheckout">The main checkout, whose slots are hidden.</param>
    /// <param name="tree">The tree the step works in.</param>
    /// <param name="legTree">The tree the leg works in: <paramref name="tree"/>, or its copy.</param>
    /// <param name="runDirectory">Where the run keeps the leg's records, some of which its phases write.</param>
    /// <param name="besides">What else the leg's work writes inside the tool's state: a sweep's workers.</param>
    public static ExecutionGround OfLeg(HarnessConfig config, string mainCheckout, string tree, string legTree, string runDirectory, IReadOnlyList<string> besides)
    {
        ArgumentNullException.ThrowIfNull(besides);

        return Of(IsolatedExecution.Legs, config, mainCheckout, [tree, legTree], [legTree, runDirectory, .. besides]);
    }

    /// <summary>
    /// The ground of a commit's or a push's git in <paramref name="tree"/> with the repository's hooks: the tree written -
    /// its work tree, and what git keeps of it but its code-running files - its slots hidden and its state sealed.
    /// </summary>
    /// <param name="config">The configuration, whose worktrees root is among the state.</param>
    /// <param name="mainCheckout">The main checkout, whose slots are hidden.</param>
    /// <param name="tree">The tree git works in.</param>
    public static ExecutionGround OfHooks(HarnessConfig config, string mainCheckout, string tree)
        => Of(IsolatedExecution.Hooks, config, mainCheckout, [tree], [tree]);

    private static ExecutionGround Of(IsolatedExecution kind, HarnessConfig config, string mainCheckout, IReadOnlyList<string> trees, IReadOnlyList<string> writable)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckout);

        var worktreesRoot = config.Worktrees?.Root ?? WorktreeSettings.DefaultRoot;
        IReadOnlyList<string> over = [.. trees.Prepend(mainCheckout).Distinct(StringComparer.OrdinalIgnoreCase)];
        IReadOnlyList<string> written = [.. writable.Distinct(StringComparer.OrdinalIgnoreCase)];

        // A place of the tool's state the execution writes as a whole - the slot a sweep keeps its workers in - is its own,
        // and is not sealed: the profile keeps a place both sealed and written sealed.
        return new ExecutionGround(
            kind,
            over,
            written,
            HarnessLayout.SecretSlotDirectoriesUnder(mainCheckout),
            [.. over.SelectMany(each => HarnessLayout.StatePathsUnder(each, worktreesRoot))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(state => !written.Contains(state, StringComparer.OrdinalIgnoreCase))]);
    }
}

/// <summary>
/// The isolation one of the tool's own executions of an agent's work runs under (<see cref="ExecutionGround"/>): the
/// mechanism and the profile it is started with, or why it runs under none, and, under
/// <see cref="AgentIsolationMode.Required"/>, the refusal that keeps it from running.
/// </summary>
/// <param name="Kind">Which execution it is.</param>
/// <param name="Mode">The configuration's mode.</param>
/// <param name="Mechanism">The mechanism, proved on this machine; null where there is none.</param>
/// <param name="Profile">What it is denied; null where there is no mechanism.</param>
/// <param name="Without">Why it runs under no mechanism, as the opening of a sentence; null where it runs under one.</param>
/// <param name="Remedy">What would give it one, where it has none; null otherwise.</param>
public sealed record ExecutionIsolation(
    IsolatedExecution Kind,
    string Mode,
    IIsolationMechanism? Mechanism,
    IsolationProfile? Profile,
    string? Without,
    string? Remedy) : IsolationDecision(Mode, Mechanism, Without, Remedy)
{
    /// <summary>The isolation its processes are started with; null where there is none.</summary>
    public ProcessIsolation? ForProcess => Mechanism is not null && Profile is not null ? new ProcessIsolation(Mechanism, Profile) : null;

    /// <summary>
    /// What is said wherever it runs with no mechanism under <see cref="AgentIsolationMode.Preferred"/>: why there is none,
    /// what then runs outside, and what would give one. Null where it runs under one.
    /// </summary>
    public string? Notice => NoticeSaying(Kind == IsolatedExecution.Legs
        ? "what the agents wrote is built, tested and run here with no isolation of the operating system's"
        : "the repository's hooks run over the agents' work with no isolation of the operating system's");

    /// <summary>The refusal of <paramref name="what"/> where it may not run, as a sentence; null where it may.</summary>
    /// <param name="what">What is refused, as the refusal names it: <c>'step'</c>.</param>
    public string? Refusal(string what) => RefusalSaying(what, Kind == IsolatedExecution.Legs
        ? "nothing the agents wrote is built, tested or run here"
        : "no hook of the repository's runs over the agents' work here");
}

/// <summary>
/// The isolation profile of one agent's ground, the one builder of it: what the operating system denies every command the
/// agent's launch runs, built from the places the ground already guards - never a list of its own.
/// </summary>
/// <remarks>
/// <para>
/// Hidden, neither read nor written: every credential slot of the main checkout, and every Claude Code directory of the
/// run's - for a Claude Code launch, whose whole process the mechanism holds and which reads its own login there, those
/// directories stay open, guarded by the words policy alone.
/// </para>
/// <para>
/// Written by nobody: git's code-running files of every tree the agent touches (<see cref="AgentIsolation.GitPlacesAsync"/>)
/// and the user's own git configuration, which this tool's git reads outside the isolation; the tool's own state under
/// each tree; and the launch's own directory, which holds its settings and records. Written again inside those: the
/// agent's own writable roots - its worktree, its work directory. Everything else is as the user.
/// </para>
/// </remarks>
public static class IsolationProfiles
{
    /// <summary>The profile of a ground's launch, from the places its owner found and made ready.</summary>
    /// <param name="hidden">The places neither read nor written: the slots, and the Claude Code directories where <paramref name="claudeCodeDirectories"/> says they are hidden.</param>
    /// <param name="claudeCodeDirectories">The Claude Code directories, each as it stands.</param>
    /// <param name="sealedPlaces">The places written by nobody.</param>
    /// <param name="writableRoots">The agent's writable roots, written again wherever they stand.</param>
    /// <param name="hideClaudeCodeDirectories">Whether the Claude Code directories are hidden: true for a command, false for the whole Claude Code process.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    public static IsolationProfile Of(
        IReadOnlyList<IsolatedPath> hidden,
        IReadOnlyList<IsolatedPath> claudeCodeDirectories,
        IReadOnlyList<IsolatedPath> sealedPlaces,
        IReadOnlyList<string> writableRoots,
        bool hideClaudeCodeDirectories,
        StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentNullException.ThrowIfNull(claudeCodeDirectories);
        ArgumentNullException.ThrowIfNull(sealedPlaces);
        ArgumentNullException.ThrowIfNull(writableRoots);

        var comparer = StringComparer.FromComparison(comparison);
        var sealedSet = sealedPlaces.Select(place => place.Path).ToHashSet(comparer);

        // A place sealed and writable both is sealed: the tighter rule stands. Each place's rule stands over the rule of
        // every place above it, so the rules are laid parent first.
        IReadOnlyList<IsolatedWrite> writes =
        [
            .. sealedPlaces.DistinctBy(place => place.Path, comparer).Select(place => new IsolatedWrite(place.Path, place.Directory, Writable: false))
                .Concat(writableRoots.Distinct(comparer).Where(root => !sealedSet.Contains(root)).Select(root => new IsolatedWrite(root, Directory: true, Writable: true)))
                .Select((write, at) => (write, at))
                .OrderBy(pair => Depth(pair.write.Path))
                .ThenBy(pair => pair.at)
                .Select(pair => pair.write),
        ];

        return new IsolationProfile(
            [.. hidden.Concat(hideClaudeCodeDirectories ? claudeCodeDirectories : []).DistinctBy(place => place.Path, comparer)],
            writes);
    }

    /// <summary>How deep a path stands: its separators, so a place above another is laid before it.</summary>
    private static int Depth(string path) => path.TrimEnd('/', '\\').Count(letter => letter is '/' or '\\');
}

/// <summary>Decides the operating system's isolation each launch of an agent runs under (<see cref="AgentIsolation"/>).</summary>
public interface IAgentIsolation
{
    /// <summary>
    /// The isolation a launch over <paramref name="ground"/> runs under: none needed where its agent may run no command;
    /// else the mechanism and its profiles, or why there is none - which the mode turns into a refusal or a notice.
    /// </summary>
    /// <param name="config">The configuration, whose <c>agentPolicy.isolation</c> is the mode.</param>
    /// <param name="ground">The agent's ground.</param>
    /// <param name="mainCheckout">The main checkout: no program on the PATH inside it is the mechanism.</param>
    /// <param name="launchDirectory">The launch's own directory, which holds its settings and records.</param>
    /// <param name="cancellationToken">Stops the probe and the questions of the trees.</param>
    Task<LaunchIsolation> ForLaunchAsync(HarnessConfig config, AgentPolicyGround ground, string mainCheckout, string launchDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// The isolation one of the tool's own executions of an agent's work over <paramref name="ground"/> runs under: the
    /// mechanism and its profile, or why there is none - which the mode turns into a refusal or a notice.
    /// </summary>
    /// <param name="config">The configuration, whose <c>agentPolicy.isolation</c> is the mode.</param>
    /// <param name="ground">The execution's ground (<see cref="ExecutionGrounds"/>).</param>
    /// <param name="mainCheckout">The main checkout: no program on the PATH inside it is the mechanism.</param>
    /// <param name="cancellationToken">Stops the probe and the questions of the trees.</param>
    Task<ExecutionIsolation> ForExecutionAsync(HarnessConfig config, ExecutionGround ground, string mainCheckout, CancellationToken cancellationToken);
}

/// <summary>
/// The owner of the operating system's isolation of agents' commands: the configuration's mode, this machine's mechanism
/// as its platform proved it once, and each launch's profiles built from its ground - the places it guards found, and each
/// one not there made empty first, so a command cannot make it inside and have it read or run outside later.
/// </summary>
/// <param name="platform">Finds and proves this machine's mechanism, once per process.</param>
/// <param name="git">Asks git where each tree keeps its own files.</param>
/// <param name="fileSystem">Finds what each place is, and makes one that is not there.</param>
/// <param name="host">This machine: its home, and how it compares paths.</param>
/// <param name="environment">Reads this process's environment, for where the user's git configuration is; this process's own where left out.</param>
public sealed class AgentIsolation(IIsolationPlatform platform, IGitClient git, IFileSystem fileSystem, IHostPlatform host, Func<string, string?>? environment = null) : IAgentIsolation
{
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <inheritdoc/>
    public async Task<LaunchIsolation> ForLaunchAsync(HarnessConfig config, AgentPolicyGround ground, string mainCheckout, string launchDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckout);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchDirectory);

        var mode = AgentIsolationMode.Of(config);

        if (!ground.Given.Contains(AgentCapability.Run))
        {
            return LaunchIsolation.Unneeded(mode);
        }

        var availability = await platform.ProbeAsync(mainCheckout, cancellationToken).ConfigureAwait(false);

        if (availability.Mechanism is not { } mechanism)
        {
            return new LaunchIsolation(mode, null, null, null, availability.Unavailable, availability.Remedy);
        }

        var (gitPlaces, unread) = await GitPlacesAsync(ground.Trees ?? [ground.WorkingDirectory], cancellationToken).ConfigureAwait(false);

        if (unread is not null)
        {
            return new LaunchIsolation(mode, null, null, null, unread, "make sure git answers in the agent's trees");
        }

        var (hidden, claude, sealedPlaces) = Places(ground.SlotDirectories, ground.ClaudeCodeDirectories, gitPlaces, ground.StateDirectories, launchDirectory);
        var comparison = host.PathComparison;

        return new LaunchIsolation(
            mode,
            mechanism,
            IsolationProfiles.Of(hidden, claude, sealedPlaces, ground.WritableRoots, hideClaudeCodeDirectories: true, comparison),
            IsolationProfiles.Of(hidden, claude, sealedPlaces, ground.WritableRoots, hideClaudeCodeDirectories: false, comparison),
            null,
            null);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Its profile is an agent's command's from the same places: the slots and the tool's own Claude Code directory hidden,
    /// git's code-running files of each tree, the user's git configuration and the tool's state written by nobody, and
    /// the ground's own places written again inside them. What it runs is the agents' work however it is started, so it is
    /// denied what their own commands are.
    /// </remarks>
    public async Task<ExecutionIsolation> ForExecutionAsync(HarnessConfig config, ExecutionGround ground, string mainCheckout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckout);

        var mode = AgentIsolationMode.Of(config);
        var availability = await platform.ProbeAsync(mainCheckout, cancellationToken).ConfigureAwait(false);

        if (availability.Mechanism is not { } mechanism)
        {
            return new ExecutionIsolation(ground.Kind, mode, null, null, availability.Unavailable, availability.Remedy);
        }

        var (gitPlaces, unread) = await GitPlacesAsync(ground.Trees, cancellationToken).ConfigureAwait(false);

        if (unread is not null)
        {
            return new ExecutionIsolation(ground.Kind, mode, null, null, unread, "make sure git answers in the trees the run works over");
        }

        var toolClaudeCode = new Orchestration.ClaudeTranscripts(fileSystem, host, _environment).ConfigDirectory;
        var (hidden, claude, sealedPlaces) = Places(ground.SlotDirectories, [toolClaudeCode], gitPlaces, ground.StateDirectories, launchDirectory: null);

        return new ExecutionIsolation(
            ground.Kind,
            mode,
            mechanism,
            IsolationProfiles.Of(hidden, claude, sealedPlaces, ground.WritableRoots, hideClaudeCodeDirectories: true, host.PathComparison),
            null,
            null);
    }

    /// <summary>
    /// The places a profile is built from, each as it stands and made first where it is not there: the slots and the
    /// Claude Code directories, hidden; and git's code-running files, the user's git configuration, the tool's state and
    /// the launch's own directory where there is one, written by nobody.
    /// </summary>
    private (List<IsolatedPath> Hidden, List<IsolatedPath> ClaudeCode, List<IsolatedPath> Sealed) Places(
        IReadOnlyList<string> slots,
        IReadOnlyList<string> claudeCodeDirectories,
        IReadOnlyList<IsolatedPath> gitPlaces,
        IReadOnlyList<string> state,
        string? launchDirectory)
    {
        var hidden = slots.Select(slot => Made(slot, directory: true)).OfType<IsolatedPath>().ToList();
        var claude = claudeCodeDirectories.Select(directory => Made(directory, directory: true)).OfType<IsolatedPath>().ToList();

        var sealedPlaces = gitPlaces
            .Concat(UserGitConfiguration())
            .Concat(state.Select(State))
            .Concat(launchDirectory is null ? [] : [new IsolatedPath(launchDirectory, Directory: true)])
            .Select(place => Made(place.Path, place.Directory))
            .OfType<IsolatedPath>()
            .ToList();

        return (hidden, claude, sealedPlaces);
    }

    /// <summary>
    /// git's code-running files of each of <paramref name="trees"/>, as git names them: the hooks directory git runs hooks
    /// from (<c>core.hooksPath</c> where it is set), the common directory's <c>config</c>, <c>config.worktree</c> and
    /// <c>info/</c>, each linked worktree's <c>config.worktree</c>, and every submodule's <c>hooks/</c> and <c>config</c>
    /// under <c>modules/</c>. Objects, the index and the refs are left writable, so <c>git add</c>, <c>fetch</c> and the
    /// rest keep working. Or why git could not say.
    /// </summary>
    /// <param name="trees">The trees the agent touches.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public async Task<(IReadOnlyList<IsolatedPath> Places, string? Unread)> GitPlacesAsync(IReadOnlyList<string> trees, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trees);

        var places = new List<IsolatedPath>();

        foreach (var tree in trees.Distinct(StringComparer.FromComparison(host.PathComparison)))
        {
            var answer = await git.RunAsync(tree, ["rev-parse", "--path-format=absolute", "--git-common-dir", "--git-path", "hooks"], cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!answer.Succeeded || answer.OutputLines is not [var commonLine, var hooksLine, ..])
            {
                return ([], $"git could not say where '{tree}' keeps its own files ({answer.FailureMessage})");
            }

            var common = Path.GetFullPath(commonLine.Trim());
            places.Add(new IsolatedPath(Path.GetFullPath(hooksLine.Trim()), Directory: true));
            places.Add(new IsolatedPath(Path.Combine(common, "config"), Directory: false));
            places.Add(new IsolatedPath(Path.Combine(common, "config.worktree"), Directory: false));
            places.Add(new IsolatedPath(Path.Combine(common, "info"), Directory: true));

            var worktrees = Path.Combine(common, "worktrees");

            if (fileSystem.DirectoryExists(worktrees))
            {
                places.AddRange(fileSystem.EnumerateDirectories(worktrees).Select(linked => new IsolatedPath(Path.Combine(linked, "config.worktree"), Directory: false)));
            }

            places.AddRange(Submodules(Path.Combine(common, "modules")));
        }

        return ([.. places.DistinctBy(place => place.Path, StringComparer.FromComparison(host.PathComparison))], null);
    }

    /// <summary>Every submodule's <c>hooks/</c> and <c>config</c> under <paramref name="modules"/>, at any depth - a git directory told by its <c>HEAD</c>, never walked into but for its own <c>modules/</c>.</summary>
    private IEnumerable<IsolatedPath> Submodules(string modules)
    {
        var waiting = new Queue<string>();

        if (fileSystem.DirectoryExists(modules))
        {
            waiting.Enqueue(modules);
        }

        while (waiting.TryDequeue(out var directory))
        {
            foreach (var child in fileSystem.EnumerateDirectories(directory))
            {
                if (fileSystem.FileExists(Path.Combine(child, "HEAD")))
                {
                    yield return new IsolatedPath(Path.Combine(child, "hooks"), Directory: true);
                    yield return new IsolatedPath(Path.Combine(child, "config"), Directory: false);

                    if (fileSystem.DirectoryExists(Path.Combine(child, "modules")))
                    {
                        waiting.Enqueue(Path.Combine(child, "modules"));
                    }
                }
                else
                {
                    // A submodule's name may hold a separator: modules/libs/one is the git directory of libs/one.
                    waiting.Enqueue(child);
                }
            }
        }
    }

    /// <summary>The user's own git configuration, which this tool's git reads outside the isolation: <c>~/.gitconfig</c>, and <c>git/</c> under <c>XDG_CONFIG_HOME</c> or <c>~/.config</c>.</summary>
    private IEnumerable<IsolatedPath> UserGitConfiguration()
    {
        var home = host.HomeDirectory;
        var configHome = _environment("XDG_CONFIG_HOME") is { Length: > 0 } named && Path.IsPathFullyQualified(named) ? named : Path.Combine(home, ".config");

        yield return new IsolatedPath(Path.Combine(home, ".gitconfig"), Directory: false);
        yield return new IsolatedPath(Path.Combine(configHome, "git"), Directory: true);
    }

    /// <summary>One state path as it stands: a directory, or one of the layout's state files.</summary>
    private static IsolatedPath State(string path)
        => new(path, Directory: !HarnessLayout.StateFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal));

    /// <summary>
    /// <paramref name="path"/> as it stands, made empty first where it is not there - a directory, or a file of git's or the
    /// user's - so no command makes it inside the isolation; null for a state file not there, which the tool reads as
    /// meaning something and so is never made: the words policy alone guards it.
    /// </summary>
    private IsolatedPath? Made(string path, bool directory)
    {
        if (fileSystem.DirectoryExists(path))
        {
            return new IsolatedPath(path, Directory: true);
        }

        if (fileSystem.FileExists(path))
        {
            return new IsolatedPath(path, Directory: false);
        }

        if (directory)
        {
            fileSystem.CreateDirectory(path);
            return new IsolatedPath(path, Directory: true);
        }

        if (HarnessLayout.StateFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal))
        {
            return null;
        }

        if (Path.GetDirectoryName(path) is { Length: > 0 } parent)
        {
            fileSystem.CreateDirectory(parent);
        }

        fileSystem.WriteAllTextAtomic(path, string.Empty);
        return new IsolatedPath(path, Directory: false);
    }
}
