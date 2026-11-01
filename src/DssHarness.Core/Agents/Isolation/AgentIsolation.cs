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

    /// <summary>What that mechanism left its commands reaching outside on this machine; null where nothing, or where there was none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Leaves { get; init; }

    /// <summary>What holding the directories above its guarded places cost its commands under that mechanism; null where nothing, or where there was none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AnchorCost { get; init; }

    /// <summary>Why its commands ran inside none; null where they ran inside one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? None { get; init; }

    /// <summary>The record as a step's output holds it: each key the record file holds, each left out where it holds none.</summary>
    public System.Text.Json.Nodes.JsonObject ToOutput()
    {
        var output = new System.Text.Json.Nodes.JsonObject { ["mode"] = Mode };

        foreach (var (key, value) in new[] { ("mechanism", Mechanism), ("version", Version), ("leaves", Leaves), ("anchorCost", AnchorCost), ("none", None) })
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
/// two never answer the same mode two ways. Disposed once it has run, it takes its temporary directory away.
/// </summary>
/// <param name="Mode">The configuration's mode (<see cref="AgentIsolationMode"/>).</param>
/// <param name="Mechanism">The mechanism, proved on this machine; null where there is none.</param>
/// <param name="Without">Why it runs under no mechanism, as the opening of a sentence; null where it runs under one.</param>
/// <param name="Remedy">What would give it one, where it has none and needs it; null otherwise.</param>
public abstract record IsolationDecision(string Mode, IIsolationMechanism? Mechanism, string? Without, string? Remedy) : IDisposable
{
    private Action? _release;

    /// <summary>What takes its temporary directory away, once, as it is disposed; nothing where it has none.</summary>
    public Action? Release { init => _release = value; }

    /// <summary>
    /// Whether it may not run: it needs the isolation, there is no mechanism to hold it, and the mode is anything but
    /// <see cref="AgentIsolationMode.Preferred"/> - the one word that lets it.
    /// </summary>
    public bool Refused => Mechanism is null && Remedy is not null && Mode != AgentIsolationMode.Preferred;

    /// <summary>What its records say of it.</summary>
    public AgentIsolationRecord Record => new(Mode)
    {
        Mechanism = Mechanism?.Name,
        Version = Mechanism?.Version,
        Leaves = Mechanism?.Leaves,
        AnchorCost = Mechanism?.AnchorCost,
        None = Mechanism is null ? Without : null,
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        GC.SuppressFinalize(this);
    }

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
/// The isolation one launch of an agent runs under: the mechanism and the profile its commands are started with, or why it
/// runs under none, and, under anything but <see cref="AgentIsolationMode.Preferred"/>, the refusal that keeps it from
/// starting. Disposed with the launch, it takes the launch's temporary directory away.
/// </summary>
/// <param name="Mode">The configuration's mode.</param>
/// <param name="Mechanism">The mechanism, proved on this machine; null where there is none.</param>
/// <param name="Commands">What a command of the launch is denied; null where there is no mechanism.</param>
/// <param name="Without">Why the launch runs under no mechanism, as the opening of a sentence; null where it runs under one.</param>
/// <param name="Remedy">What would give it one, where it has none and needs it; null otherwise.</param>
public sealed record LaunchIsolation(
    string Mode,
    IIsolationMechanism? Mechanism,
    IsolationProfile? Commands,
    string? Without,
    string? Remedy) : IsolationDecision(Mode, Mechanism, Without, Remedy)
{
    /// <summary>Why a launch whose agent may run no command needs no isolation, as its record says it.</summary>
    public const string NoCommands = "the agent may run no command";

    /// <summary>
    /// What a Claude Code launch's whole process is denied where the mechanism takes no command handed to it one by one
    /// (<see cref="IIsolationMechanism.TakesHandedCommands"/>): the commands' profile with the Claude Code directories
    /// read, for it reads its login there. Null where there is no mechanism.
    /// </summary>
    public IsolationProfile? ClaudeCode { get; init; }

    /// <summary>The isolation a command of the launch is started with; null where there is none.</summary>
    public ProcessIsolation? ForCommands => Mechanism is not null && Commands is not null ? new ProcessIsolation(Mechanism, Commands) : null;

    /// <summary>
    /// The isolation a Claude Code launch's whole process is started with, where its mechanism takes no command handed to it
    /// one by one; null where there is none, or the launch hands its commands over and runs outside.
    /// </summary>
    public ProcessIsolation? ForClaudeCode => Mechanism is { TakesHandedCommands: false } && ClaudeCode is not null ? new ProcessIsolation(Mechanism, ClaudeCode) : null;

    /// <summary>What the launch's client runs its processes under, handed it in its run: <see cref="ForCommands"/> and <see cref="ForClaudeCode"/>.</summary>
    public LaunchProcesses Processes => new(ForCommands, ForClaudeCode);

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
    public static LaunchIsolation Unneeded(string mode) => new(mode, null, null, NoCommands, null);
}

/// <summary>
/// What a client runs a launch's processes under, handed it in its run (<see cref="AgentRun.Isolation"/>): the isolation
/// each command it starts is started with, and the one a Claude Code launch's whole process is started with where it runs
/// inside. The decision it comes from (<see cref="LaunchIsolation"/>) stays with the one who asked it, who releases it once
/// the launch has run, so a client is handed nothing it could end.
/// </summary>
/// <param name="Commands">The isolation each command of the launch is started with; null where there is none.</param>
/// <param name="ClaudeCode">The isolation a Claude Code launch's whole process is started with; null where there is none, or it runs outside.</param>
public sealed record LaunchProcesses(ProcessIsolation? Commands, ProcessIsolation? ClaudeCode);

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
/// What an isolation profile is built from, whoever asks: the trees worked over, the places written, read and kept from the
/// child, the Unix sockets it reaches outside, and the launch's own directory - an agent's ground (<see cref="Of(AgentPolicyGround, string)"/>), or an
/// execution of the tool's over what an agent wrote (<see cref="ExecutionGround"/>).
/// </summary>
/// <param name="Trees">Every tree worked over, whose git's code-running files, git's own pointers, configuration directory and state no write reaches.</param>
/// <param name="WritableRoots">The places written - and, for a tree among them, its git directories but their code-running files.</param>
/// <param name="ReadRoots">The places read as they stand, wherever the mechanism gives the child a place of its own in their stead.</param>
/// <param name="SlotDirectories">The credential slots, neither read nor written.</param>
/// <param name="ClaudeCodeDirectories">The Claude Code directories, neither read nor written - but each one's shell snapshots, read.</param>
/// <param name="StateDirectories">The tool's own state under each tree, written by nobody but where a writable root stands in it.</param>
/// <param name="LaunchDirectory">The launch's own directory, which holds its settings and records; null for an execution with none.</param>
/// <param name="Sockets">The Unix sockets the child reaches outside, each alone; none for an agent's own commands.</param>
internal sealed record IsolationGround(
    IReadOnlyList<string> Trees,
    IReadOnlyList<string> WritableRoots,
    IReadOnlyList<string> ReadRoots,
    IReadOnlyList<string> SlotDirectories,
    IReadOnlyList<string> ClaudeCodeDirectories,
    IReadOnlyList<string> StateDirectories,
    string? LaunchDirectory,
    IReadOnlyList<string> Sockets)
{
    /// <summary>The ground of an agent's launch, from its policy's ground.</summary>
    /// <param name="ground">The agent's ground.</param>
    /// <param name="launchDirectory">The launch's own directory.</param>
    public static IsolationGround Of(AgentPolicyGround ground, string launchDirectory)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentException.ThrowIfNullOrWhiteSpace(launchDirectory);

        return new(
            ground.Trees ?? [ground.WorkingDirectory],
            ground.WritableRoots,
            ground.Roots(StringComparison.Ordinal),
            ground.SlotDirectories,
            ground.ClaudeCodeDirectories,
            ground.StateDirectories,
            launchDirectory,
            []);
    }

    /// <summary>
    /// The ground of one of the tool's own executions: its trees and writable roots read as they stand, the tool's own
    /// Claude Code directory hidden, no launch directory - and, for a commit's or a push's hooks, the user's SSH agent's
    /// socket, the one a push over ssh signs with.
    /// </summary>
    /// <param name="ground">The execution's ground.</param>
    /// <param name="claudeCode">The tool's own Claude Code directory.</param>
    /// <param name="agentSocket">The user's SSH agent's socket, whole; null where there is none.</param>
    public static IsolationGround Of(ExecutionGround ground, string claudeCode, string? agentSocket)
    {
        ArgumentNullException.ThrowIfNull(ground);

        return new(
            ground.Trees,
            ground.WritableRoots,
            [.. ground.Trees.Concat(ground.WritableRoots)],
            ground.SlotDirectories,
            [claudeCode],
            ground.StateDirectories,
            null,
            ground.Kind == IsolatedExecution.Hooks && agentSocket is not null ? [agentSocket] : []);
    }
}

/// <summary>Decides the operating system's isolation each launch of an agent, and each execution of the tool's over what an agent wrote, runs under (<see cref="AgentIsolation"/>).</summary>
public interface IAgentIsolation
{
    /// <summary>
    /// The isolation a launch over <paramref name="ground"/> runs under: none needed where its agent may run no command;
    /// else the mechanism and its profile, or why there is none - which the mode turns into a refusal or a notice.
    /// </summary>
    /// <param name="config">The configuration, whose <c>agentPolicy.isolation</c> is the mode.</param>
    /// <param name="ground">The agent's ground.</param>
    /// <param name="mainCheckout">The main checkout: no program on the PATH inside it is the mechanism.</param>
    /// <param name="launchDirectory">The launch's own directory, which holds its settings and records.</param>
    /// <param name="cancellationToken">Stops the probe and the questions of the trees.</param>
    Task<LaunchIsolation> ForLaunchAsync(HarnessConfig config, AgentPolicyGround ground, string mainCheckout, string launchDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// The isolation one of the tool's own executions of an agent's work over <paramref name="ground"/> runs under - the
    /// profile built by the rules every launch's is: the mechanism and its profile, or why there is none - which the mode
    /// turns into a refusal or a notice.
    /// </summary>
    /// <param name="config">The configuration, whose <c>agentPolicy.isolation</c> is the mode.</param>
    /// <param name="ground">The execution's ground (<see cref="ExecutionGrounds"/>).</param>
    /// <param name="mainCheckout">The main checkout: no program on the PATH inside it is the mechanism.</param>
    /// <param name="cancellationToken">Stops the probe and the questions of the trees.</param>
    Task<ExecutionIsolation> ForExecutionAsync(HarnessConfig config, ExecutionGround ground, string mainCheckout, CancellationToken cancellationToken);
}

/// <summary>
/// The owner of the operating system's isolation of what an agent runs, and of what the tool runs of an agent's work: the
/// configuration's mode, this machine's mechanism as its platform proved it once, and each profile built from its ground -
/// the machine read-only; written only the ground's writable roots, a tree's git directories where the tree itself is
/// written, the launch's own temporary directory and the declared tool caches (<see cref="IsolationCaches"/>); neither read
/// nor written the credential slots and the Claude Code directories; reached outside no Unix socket but those the ground
/// names - a push's SSH agent's; and inside what is written, written by nobody git's code-running files and its pointers to
/// its own directories, every configuration file git reads, each tree's configuration directory, the tool's state and the
/// launch's own directory. Every place is ruled at its real spelling; one inside a written place that is not there is made
/// empty first, so no command makes it and has it read or run outside later.
/// </summary>
/// <remarks>
/// A launch or an execution is given no isolation - which the mode turns into a refusal or a notice - where its places
/// cannot be held: git cannot say where a tree keeps its files, a guarded place stands on a file system that ignores letter
/// case and the mechanism holds the one spelling, or a link stands where the child writes on the way to a guarded place,
/// which a command could replace with one leading elsewhere.
/// </remarks>
/// <param name="platform">Finds and proves this machine's mechanism, once per process.</param>
/// <param name="git">Asks git where each tree keeps its own files.</param>
/// <param name="fileSystem">Finds what each place is and where it leads, and makes one that is not there.</param>
/// <param name="host">This machine: its home, and how it compares paths.</param>
/// <param name="environment">Reads this process's environment, for where the user's cache directory, the tool's Claude Code directory and the user's SSH agent are; this process's own where left out.</param>
/// <param name="pins">Keeps each tree's git directories, as git named them, for every later git of this tool's in it; none where left out.</param>
public sealed class AgentIsolation(
    IIsolationPlatform platform,
    IGitClient git,
    IFileSystem fileSystem,
    IHostPlatform host,
    Func<string, string?>? environment = null,
    GitDirectoryPins? pins = null) : IAgentIsolation
{
    /// <summary>The variable the user's SSH agent's socket is named by.</summary>
    public const string SshAgentSocketVariable = "SSH_AUTH_SOCK";

    /// <summary>The directory under a Claude Code directory that holds the shell snapshots its commands source, which stays read.</summary>
    public const string ShellSnapshots = "shell-snapshots";

    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <inheritdoc/>
    public async Task<LaunchIsolation> ForLaunchAsync(HarnessConfig config, AgentPolicyGround ground, string mainCheckout, string launchDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ground);

        if (!ground.Given.Contains(AgentCapability.Run))
        {
            return LaunchIsolation.Unneeded(AgentIsolationMode.Of(config));
        }

        var held = await HeldAsync(config, IsolationGround.Of(ground, launchDirectory), mainCheckout, "make sure git 2.31 or newer answers in the agent's trees", cancellationToken).ConfigureAwait(false);

        return new LaunchIsolation(held.Mode, held.Mechanism, held.Profile, held.Without, held.Remedy) { ClaudeCode = held.ClaudeCode, Release = held.Release };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Its profile is built as an agent's command's is, from the same places: the slots and the tool's own Claude Code
    /// directory hidden, git's code-running files of each tree, every configuration file git reads and the tool's state
    /// written by nobody, the ground's own places written again inside them, the private temporary directory and the
    /// declared caches written - for what it runs is the agents' work however it is started. A commit's or a push's hooks
    /// reach the user's SSH agent's socket alone, so a push over ssh signs as the user's own does.
    /// </remarks>
    public async Task<ExecutionIsolation> ForExecutionAsync(HarnessConfig config, ExecutionGround ground, string mainCheckout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ground);

        var claudeCode = new Orchestration.ClaudeTranscripts(fileSystem, host, _environment).ConfigDirectory;
        var socket = _environment(SshAgentSocketVariable) is { Length: > 0 } named && Path.IsPathFullyQualified(named) && fileSystem.KindOf(named) != PathKind.None ? Real(named) : null;
        var held = await HeldAsync(config, IsolationGround.Of(ground, claudeCode, socket), mainCheckout, "make sure git 2.31 or newer answers in the trees the run works over", cancellationToken).ConfigureAwait(false);

        return new ExecutionIsolation(ground.Kind, held.Mode, held.Mechanism, held.Profile, held.Without, held.Remedy) { Release = held.Release };
    }

    /// <summary>
    /// The decision over <paramref name="ground"/>, whoever asks: the mode; the mechanism and the profile built from the
    /// ground, with what takes its temporary directory away; or why there is none and the remedy.
    /// </summary>
    private async Task<Held> HeldAsync(HarnessConfig config, IsolationGround ground, string mainCheckout, string unreadRemedy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckout);

        var mode = AgentIsolationMode.Of(config);
        var availability = await platform.ProbeAsync(mainCheckout, cancellationToken).ConfigureAwait(false);

        if (availability.Mechanism is not { } mechanism)
        {
            return Held.None(mode, availability.Unavailable, availability.Remedy);
        }

        var gitPlaces = await GitPlacesAsync(ground.Trees, cancellationToken).ConfigureAwait(false);

        if (gitPlaces.Unread is { } unread)
        {
            return Held.None(mode, unread, unreadRemedy);
        }

        var root = IsolationCaches.Root(host.HomeDirectory, host.Current, _environment);
        var caches = Path.Combine(root, IsolationCaches.Directory);

        // A mechanism that gives the child a temporary directory of its own is named none.
        var temp = mechanism.OwnTemp ? null : Path.Combine(root, IsolationCaches.TempDirectory, TempName(ground.LaunchDirectory));
        var shared = temp is not null && fileSystem.DirectoryExists(temp);
        fileSystem.CreateDirectory(caches);

        if (temp is not null)
        {
            fileSystem.CreateDirectory(temp);
        }

        var built = Built(ground, gitPlaces, caches, temp, mechanism);

        if (built.Unheld is { } unheld)
        {
            // Taken away where this launch made it; one an earlier launch of the same directory still works in is left.
            if (temp is not null && !shared)
            {
                Removed(temp);
            }

            return Held.None(mode, unheld.Why, unheld.Remedy);
        }

        // A linked worktree's own git directory is named by its .git file, which says it again to every later git: pinned as
        // git named it now. The main checkout's .git is the directory itself, which nothing redirects.
        foreach (var (tree, gitDirectory, commonDirectory) in gitPlaces.Directories.Where(directory => directory.GitDirectory != directory.CommonDirectory))
        {
            pins?.Pin(tree, gitDirectory, commonDirectory);
        }

        return new Held(mode, mechanism, built.Profile, built.ClaudeCode, null, null, temp is null ? null : () => Removed(temp));
    }

    /// <summary>A decision as the builder makes it, before it is a launch's or an execution's.</summary>
    private sealed record Held(string Mode, IIsolationMechanism? Mechanism, IsolationProfile? Profile, IsolationProfile? ClaudeCode, string? Without, string? Remedy, Action? Release)
    {
        public static Held None(string mode, string? why, string? remedy) => new(mode, null, null, null, why, remedy, null);
    }

    /// <summary>
    /// git's own places of each of <paramref name="trees"/>, as git names them: what a write must not reach - the hooks
    /// directory git runs hooks from (<c>core.hooksPath</c> where it is set), the common directory's <c>config</c>,
    /// <c>config.worktree</c> and <c>info/</c>, each linked worktree's <c>config.worktree</c> and its <c>commondir</c> and
    /// <c>gitdir</c>, which say where its repository is, a tree's own <c>.git</c> where it is such a pointer, every
    /// submodule's <c>hooks/</c> and <c>config</c> under <c>modules/</c>, and every configuration file git reads in the
    /// tree, the user's and what each includes among them - and each tree's own and common git directory, where the tree is
    /// its repository's top. Objects, the
    /// index and the refs are none of them, so <c>git add</c>, <c>fetch</c> and the rest keep working. Or why git could not
    /// say: an answer that is no whole path among it, which git before 2.31 gives.
    /// </summary>
    /// <param name="trees">The trees the agent touches.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public async Task<GitPlaces> GitPlacesAsync(IReadOnlyList<string> trees, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trees);

        var places = new List<IsolatedPath>();
        var directories = new List<(string Tree, string GitDirectory, string CommonDirectory)>();

        foreach (var tree in trees.Distinct(StringComparer.FromComparison(host.PathComparison)))
        {
            var answer = await git.RunAsync(tree, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir", "--git-path", "hooks", "--show-toplevel"], cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!answer.Succeeded || answer.OutputLines is not [var gitLine, var commonLine, var hooksLine, var topLine, ..])
            {
                return GitPlaces.Not($"git could not say where '{tree}' keeps its own files ({answer.FailureMessage})");
            }

            if (Unwhole([gitLine, commonLine, hooksLine, topLine]) is { } partial)
            {
                return GitPlaces.Not($"git answered '{partial}' for where '{tree}' keeps its own files, which is no whole path");
            }

            var gitDirectory = Path.GetFullPath(gitLine.Trim());
            var common = Path.GetFullPath(commonLine.Trim());

            // A tree's git directories are its own only where it is its repository's top: a directory inside another's work
            // tree - a leg's copy that is no repository of its own - is answered by that one, whose git it never writes.
            if (StringComparer.FromComparison(host.PathComparison).Equals(Real(Path.GetFullPath(topLine.Trim())), Real(tree)))
            {
                directories.Add((tree, gitDirectory, common));
            }
            places.Add(new IsolatedPath(Path.GetFullPath(hooksLine.Trim()), Directory: true));
            places.Add(new IsolatedPath(Path.Combine(common, "config"), Directory: false));
            places.Add(new IsolatedPath(Path.Combine(common, "config.worktree"), Directory: false));
            places.Add(new IsolatedPath(Path.Combine(common, "info"), Directory: true));

            var dotGit = Path.Combine(tree, ".git");

            if (fileSystem.FileExists(dotGit))
            {
                places.Add(new IsolatedPath(dotGit, Directory: false));
            }

            var worktrees = Path.Combine(common, "worktrees");

            if (fileSystem.DirectoryExists(worktrees))
            {
                foreach (var linked in fileSystem.EnumerateDirectories(worktrees))
                {
                    places.Add(new IsolatedPath(Path.Combine(linked, "config.worktree"), Directory: false));
                    places.Add(new IsolatedPath(Path.Combine(linked, "commondir"), Directory: false));
                    places.Add(new IsolatedPath(Path.Combine(linked, "gitdir"), Directory: false));
                }
            }

            places.AddRange(Submodules(Path.Combine(common, "modules")));

            var configuration = await git.RunAsync(tree, ["config", "--list", "--show-origin", "--includes", "-z"], cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!configuration.Succeeded)
            {
                return GitPlaces.Not($"git could not say which configuration files it reads in '{tree}' ({configuration.FailureMessage})");
            }

            places.AddRange(ConfigurationFiles(configuration.StandardOutput, tree).Select(file => new IsolatedPath(file, Directory: false)));
        }

        return new GitPlaces([.. places.DistinctBy(place => place.Path, StringComparer.FromComparison(host.PathComparison))], directories, null);
    }

    /// <summary>
    /// The first of <paramref name="lines"/> - git's answer of where a tree keeps its files - that is no whole path, trimmed:
    /// git before 2.31 knows no <c>--path-format=absolute</c> and answers it back as a line of its own, so every place after
    /// it would be read a line late, and relative. Null where every line is whole.
    /// </summary>
    /// <param name="lines">git's lines.</param>
    public static string? Unwhole(IReadOnlyList<string> lines)
        => (lines ?? throw new ArgumentNullException(nameof(lines))).Select(line => line.Trim()).FirstOrDefault(line => !Path.IsPathFullyQualified(line));

    /// <summary>
    /// Every file <c>git config --list --show-origin -z</c> names as a source, whole: each entry's origin before its key,
    /// <c>file:</c> and the path - against <paramref name="tree"/> where git names it relatively.
    /// </summary>
    /// <param name="answered">What git answered.</param>
    /// <param name="tree">The tree git was asked in.</param>
    public static IReadOnlyList<string> ConfigurationFiles(string answered, string tree)
    {
        ArgumentNullException.ThrowIfNull(answered);

        const string FileOrigin = "file:";
        var fields = answered.Split('\0');

        return [.. fields
            .Where((_, at) => at % 2 == 0)
            .Where(origin => origin.StartsWith(FileOrigin, StringComparison.Ordinal) && origin.Length > FileOrigin.Length)
            .Select(origin => Path.GetFullPath(origin[FileOrigin.Length..], tree))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The profile of <paramref name="ground"/> as this machine's mechanism holds it - a command's, and a Claude Code
    /// launch's whole process's, which reads the Claude Code directories it keeps its login in - or why it cannot be held.
    /// </summary>
    private (IsolationProfile Profile, IsolationProfile ClaudeCode, (string Why, string Remedy)? Unheld) Built(IsolationGround ground, GitPlaces gitPlaces, string caches, string? temp, IIsolationMechanism mechanism)
    {
        var comparer = StringComparer.FromComparison(host.PathComparison);

        // What is written: the ground's roots, the git directories of a tree that is itself written - its objects, index
        // and refs, which git add and fetch write - and the tool caches.
        var written = ground.WritableRoots
            .Concat(gitPlaces.Directories
                .Where(directory => ground.WritableRoots.Any(root => comparer.Equals(Real(root), Real(directory.Tree))))
                .SelectMany(directory => new[] { directory.GitDirectory, directory.CommonDirectory }))
            .Append(caches)
            .Select(Real)
            .Distinct(comparer)
            .ToList();

        var open = new IsolationProfile([], [.. written.Select(root => new IsolatedWrite(root, Directory: true, Writable: true))]) { Temp = temp is null ? null : Real(temp) };

        var hidden = ground.SlotDirectories.Concat(ground.ClaudeCodeDirectories).Select(place => (place, directory: true))
            .Concat(ground.ClaudeCodeDirectories
                .Where(directory => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) == ".claude")
                .Select(directory => (place: Path.TrimEndingDirectorySeparator(directory) + ".json", directory: false)))
            .ToList();

        var sealedPlaces = gitPlaces.Places
            .Concat(ground.Trees.Select(tree => new IsolatedPath(Path.Combine(tree, HarnessLayout.DirectoryName), Directory: true)))
            .Concat(ground.StateDirectories.Select(State))
            .Concat(ground.LaunchDirectory is { } launch ? [new IsolatedPath(launch, Directory: true)] : [])
            .ToList();

        // A place outside every written place is read-only already, and one beneath another guarded place is held by it.
        var keptHidden = new List<IsolatedPath>();

        foreach (var (place, directory) in hidden.DistinctBy(entry => entry.place, comparer))
        {
            if (Kept(place, directory, open, stateFile: false) is { } kept)
            {
                keptHidden.Add(kept);
            }
        }

        var keptSealed = new List<IsolatedPath>();

        foreach (var place in sealedPlaces.DistinctBy(place => place.Path, comparer).OrderBy(place => IsolationProfile.Depth(place.Path)))
        {
            var real = Real(place.Path);

            // Written by nobody already: outside every written place, or beneath a guarded directory with no written place between.
            if (!open.Writable(real) || keptSealed.Concat(keptHidden).Any(guarded => Holds(guarded, real, written)))
            {
                continue;
            }

            if (Kept(place.Path, place.Directory, open, stateFile: IsStateFile(place.Path)) is { } kept)
            {
                keptSealed.Add(kept);
            }
        }

        foreach (var guarded in sealedPlaces.Select(place => place.Path).Concat(hidden.Select(entry => entry.place)))
        {
            if (LinkOnTheWay(guarded, open) is { } link)
            {
                return (IsolationProfile.Nothing, IsolationProfile.Nothing, (
                    $"'{link}' is a link inside a place the agent writes, on the way to '{guarded}', which a command could replace with one leading elsewhere",
                    $"replace the link '{link}' with the directory or file it leads to"));
            }
        }

        if (!mechanism.HoldsEveryCase && keptSealed.Concat(keptHidden).FirstOrDefault(FoldsCase) is { } folded)
        {
            return (IsolationProfile.Nothing, IsolationProfile.Nothing, (
                $"'{folded.Path}' stands on a file system that ignores letter case, where {mechanism.Name}'s rules hold the one spelling they are made at",
                "keep the trees and every guarded place on a file system that tells letter case apart - WSL's own, not a Windows drive under /mnt"));
        }

        var profile = new IsolationProfile(
            [.. keptHidden.DistinctBy(place => place.Path, comparer)],
            IsolationProfiles.Ordered(keptSealed, written, comparer))
        {
            Reads = [.. ground.Trees.Concat(ground.ReadRoots).Select(Real).Distinct(comparer)],
            Reopened = [.. ground.ClaudeCodeDirectories.Select(directory => Path.Combine(Real(directory), ShellSnapshots)).Distinct(comparer)],
            Temp = open.Temp,
            Environment = [.. TempVariable(open.Temp), .. IsolationCaches.Environment(Real(caches))],
            Sockets = [.. ground.Sockets.Select(Real).Distinct(comparer)],
        };

        // The whole Claude Code process reads its login in its directories, read alone like the machine: written, they would
        // let a command plant what Claude Code runs outside later - its settings' hooks, its own file's servers.
        var claudeCode = hidden.Where(entry => !ground.ClaudeCodeDirectories.Contains(entry.place, comparer) && !IsClaudeFile(entry.place)).Select(entry => Real(entry.place)).ToHashSet(comparer);

        return (profile, profile with { Hidden = [.. profile.Hidden.Where(place => claudeCode.Contains(place.Path))], Reopened = [] }, null);
    }

    /// <summary>The variable that names a child's temporary directory where the profile has one of its own; none where the mechanism gives it one.</summary>
    private static IEnumerable<KeyValuePair<string, string>> TempVariable(string? temp)
        => temp is null ? [] : [new(IsolationCaches.TempVariable, temp)];

    /// <summary>Whether <paramref name="place"/> is the file beside a Claude Code directory that Claude Code keeps its own state in: <c>~/.claude.json</c>.</summary>
    private static bool IsClaudeFile(string place) => Path.GetFileName(place) == ".claude.json";

    /// <summary>
    /// <paramref name="path"/> as its guarded place, at its real spelling: there as it stands; not there and inside a
    /// written place, made empty first - a directory, or a file of git's - so no command makes it inside and has it read
    /// or run outside later; null where it is not there and no command could make it, and for a state file not there,
    /// which the tool reads as meaning something and so is never made: the words policy alone guards it.
    /// </summary>
    private IsolatedPath? Kept(string path, bool directory, IsolationProfile open, bool stateFile)
    {
        if (fileSystem.DirectoryExists(path))
        {
            return new IsolatedPath(Real(path), Directory: true);
        }

        if (fileSystem.FileExists(path))
        {
            return new IsolatedPath(Real(path), Directory: false);
        }

        if (stateFile || Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is not { Length: > 0 } parent || !open.Writable(Real(parent)))
        {
            return null;
        }

        if (directory)
        {
            fileSystem.CreateDirectory(path);
        }
        else
        {
            fileSystem.CreateDirectory(parent);
            fileSystem.WriteAllTextAtomic(path, string.Empty);
        }

        return new IsolatedPath(Real(path), directory);
    }

    /// <summary>
    /// Whether <paramref name="guarded"/>, a directory, holds <paramref name="place"/> beneath it with no place of
    /// <paramref name="written"/> between them, which would write it again.
    /// </summary>
    private static bool Holds(IsolatedPath guarded, string place, IReadOnlyList<string> written)
        => guarded.Directory
            && guarded.Path != place
            && IsolationProfile.Covers(guarded.Path, place)
            && !written.Any(root => root != guarded.Path && IsolationProfile.Covers(guarded.Path, root) && IsolationProfile.Covers(root, place));

    /// <summary>
    /// The first link on the way to <paramref name="guarded"/> that stands in a directory a command writes - the place
    /// itself, or a directory above it - which a command could remove and put another in the place of; null where none.
    /// </summary>
    private string? LinkOnTheWay(string guarded, IsolationProfile open)
    {
        var at = Path.TrimEndingDirectorySeparator(guarded);

        while (Path.GetDirectoryName(at) is { Length: > 0 } parent && parent != at)
        {
            if (open.Writable(Real(parent)) && (fileSystem.FileExists(at) || fileSystem.DirectoryExists(at)) && fileSystem.IsLink(at))
            {
                return at;
            }

            at = parent;
        }

        return null;
    }

    /// <summary>
    /// Whether the file system <paramref name="place"/> stands on finds it under another case of its name: its name with
    /// every letter's case turned is there too, as on a Windows drive under WSL or a disk of macOS's own.
    /// </summary>
    private bool FoldsCase(IsolatedPath place)
    {
        var name = Path.GetFileName(place.Path);
        var turned = new string([.. name.Select(letter => char.IsUpper(letter) ? char.ToLowerInvariant(letter) : char.ToUpperInvariant(letter))]);

        if (turned == name || Path.GetDirectoryName(place.Path) is not { } parent)
        {
            return false;
        }

        var other = Path.Combine(parent, turned);

        return fileSystem.DirectoryExists(other) || fileSystem.FileExists(other);
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

    /// <summary>
    /// The name of a launch's temporary directory: the one its own directory names, so every launch of one step or one
    /// side-task shares it however often it is set up again, and short, for a Unix socket's path is; a fresh one for an
    /// execution with no directory of its own.
    /// </summary>
    private static string TempName(string? launchDirectory)
        => launchDirectory is null
            ? Guid.NewGuid().ToString("N")[..12]
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(launchDirectory))))[..12];

    /// <summary>A place's real spelling: every link along it followed, as the mechanism meets it.</summary>
    private string Real(string path) => Path.TrimEndingDirectorySeparator(fileSystem.ResolveLinks(path));

    /// <summary>Removes a launch's temporary directory, whatever its commands left in it; one that cannot be removed is left.</summary>
    private void Removed(string temp)
    {
        try
        {
            if (fileSystem.DirectoryExists(temp))
            {
                fileSystem.DeleteDirectory(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary directory left under the isolation's own is harmless, and the next launch makes its own.
        }
    }

    /// <summary>One state path as it stands: a directory, or one of the layout's state files.</summary>
    private static IsolatedPath State(string path) => new(path, Directory: !IsStateFile(path));

    /// <summary>Whether <paramref name="path"/> is one of the layout's state files.</summary>
    private static bool IsStateFile(string path) => HarnessLayout.StateFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal);
}

/// <summary>git's own places of the trees a launch works over (<see cref="AgentIsolation.GitPlacesAsync"/>), or why git could not say them.</summary>
/// <param name="Places">The places no write reaches.</param>
/// <param name="Directories">Each tree's own git directory and the common directory it shares.</param>
/// <param name="Unread">Why git could not say them; null where it did.</param>
public sealed record GitPlaces(IReadOnlyList<IsolatedPath> Places, IReadOnlyList<(string Tree, string GitDirectory, string CommonDirectory)> Directories, string? Unread)
{
    /// <summary>git could not say them, and why.</summary>
    /// <param name="why">Why.</param>
    public static GitPlaces Not(string why) => new([], [], why);
}

/// <summary>The order a profile's write rules are laid in, the one rule of it.</summary>
public static class IsolationProfiles
{
    /// <summary>
    /// The write rules of <paramref name="sealedPlaces"/> and <paramref name="writableRoots"/>, each once, parent first:
    /// each place's rule stands over the rule of every place above it, so a place written by nobody inside a writable root
    /// stays so; a place sealed and writable both is sealed, the tighter rule standing.
    /// </summary>
    /// <param name="sealedPlaces">The places written by nobody.</param>
    /// <param name="writableRoots">The places written.</param>
    /// <param name="comparer">How this machine compares paths.</param>
    public static IReadOnlyList<IsolatedWrite> Ordered(IReadOnlyList<IsolatedPath> sealedPlaces, IReadOnlyList<string> writableRoots, IEqualityComparer<string> comparer)
    {
        ArgumentNullException.ThrowIfNull(sealedPlaces);
        ArgumentNullException.ThrowIfNull(writableRoots);
        ArgumentNullException.ThrowIfNull(comparer);

        var sealedSet = sealedPlaces.Select(place => place.Path).ToHashSet(comparer);

        return
        [
            .. sealedPlaces.DistinctBy(place => place.Path, comparer).Select(place => new IsolatedWrite(place.Path, place.Directory, Writable: false))
                .Concat(writableRoots.Distinct(comparer).Where(root => !sealedSet.Contains(root)).Select(root => new IsolatedWrite(root, Directory: true, Writable: true)))
                .Select((write, at) => (write, at))
                .OrderBy(pair => IsolationProfile.Depth(pair.write.Path))
                .ThenBy(pair => pair.at)
                .Select(pair => pair.write),
        ];
    }
}
