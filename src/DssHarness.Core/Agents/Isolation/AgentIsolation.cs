using System.Text.Json.Serialization;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

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
/// two never answer the same mode two ways; and, once it has run, the one question of whether anything goes on over what
/// it ran over (<see cref="AfterwardAsync"/>). Disposed once it has run, it takes its temporary directory away.
/// </summary>
/// <param name="Mode">The configuration's mode (<see cref="AgentIsolationMode"/>).</param>
/// <param name="Mechanism">The mechanism, proved on this machine; null where there is none.</param>
/// <param name="Without">Why it runs under no mechanism, as the opening of a sentence; null where it runs under one.</param>
/// <param name="Remedy">What would give it one, where it has none and needs it; null otherwise.</param>
public abstract record IsolationDecision(string Mode, IIsolationMechanism? Mechanism, string? Without, string? Remedy) : IDisposable
{
    private Action? _release;
    private readonly Func<CancellationToken, Task<string?>>? _after;

    /// <summary>What takes its temporary directory away, once, as it is disposed; nothing where it has none.</summary>
    public Action? Release { init => _release = value; }

    /// <summary>What looks, once it has run, at what it left in git's own directories (<see cref="AfterwardAsync"/>); nothing where it ran under no mechanism.</summary>
    public Func<CancellationToken, Task<string?>>? After { init => _after = value; }

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

    /// <summary>
    /// Asked once what it isolated has run, however it ended, before anything of the tool's goes on over its trees: why
    /// nothing may - what it left in a git directory it wrote that git started outside would go on with as it lies, and the
    /// isolation could not hold: the state of an operation under way, made or changed while it ran, whose continue runs
    /// what its todo says; or a replacement made or moved in a repository whose refs it wrote, which puts other content in
    /// place of an object for every git that reads it - or that git could not say what is there - as a sentence with the
    /// remedy; null where nothing keeps it from going on.
    /// </summary>
    /// <param name="cancellationToken">Stops the look, which is bounded besides (<see cref="AgentIsolation.AfterwardLimit"/>).</param>
    public Task<string?> AfterwardAsync(CancellationToken cancellationToken) => _after?.Invoke(cancellationToken) ?? Task.FromResult<string?>(null);

    /// <inheritdoc/>
    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// What is said where it runs with no mechanism though it needs one, under <see cref="AgentIsolationMode.Preferred"/>:
    /// why there is none, <paramref name="so"/>, and what would give one; and where it runs under a mechanism that leaves
    /// something reached (<see cref="IIsolationMechanism.Leaves"/>) - bubblewrap on a kernel with no Landlock scope -
    /// that <paramref name="runs"/> inside it and what it leaves. Null where it runs under one that leaves nothing, or needs
    /// none.
    /// </summary>
    /// <param name="so">What follows for it with no mechanism, as the sentence's middle says it.</param>
    /// <param name="runs">What runs inside a mechanism, as the sentence's opening says it.</param>
    protected string? NoticeSaying(string so, string runs)
        => Mechanism is null && Remedy is not null && !Refused
            ? $"agentPolicy.isolation is \"{AgentIsolationMode.Preferred}\" and {Without}, so {so}; {Remedy}"
            : Mechanism is { Leaves: { } leaves }
                ? $"{runs} inside {Mechanism.Name}, which leaves {leaves} reached"
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
    /// What a Claude Code launch's whole process is denied: where the mechanism takes no command handed to it one by one
    /// (<see cref="IIsolationMechanism.TakesHandedCommands"/>), the commands' profile with the Claude Code directories read,
    /// for it reads its login there; where it takes them and starts again inside a layer of its own
    /// (<see cref="IIsolationMechanism.Unnested"/>), the outer layer its commands are each handed into a layer of their own
    /// from (<see cref="IsolationProfile.Outer"/>). Null where there is no mechanism, or Claude Code's own process runs
    /// outside every layer.
    /// </summary>
    public IsolationProfile? ClaudeCode { get; init; }

    /// <summary>The isolation a command of the launch is started with; null where there is none.</summary>
    public ProcessIsolation? ForCommands => Mechanism is not null && Commands is not null ? new ProcessIsolation(Mechanism, Commands) : null;

    /// <summary>
    /// The isolation a Claude Code launch's whole process is started with - whole inside, or in the outer layer it hands each
    /// command in from; null where there is none, or the launch hands its commands over from outside every layer.
    /// </summary>
    public ProcessIsolation? ForClaudeCode => Mechanism is not null && ClaudeCode is not null ? new ProcessIsolation(Mechanism, ClaudeCode) : null;

    /// <summary>
    /// Why a Claude Code launch's own process runs outside every layer, handing each command in from there - the mechanism
    /// takes handed commands and starts none inside a layer of its own; null where it runs inside, or there is no mechanism.
    /// </summary>
    public string? ClaudeCodeOutside => Mechanism is { TakesHandedCommands: true } && ClaudeCode is null ? Mechanism.Unnested : null;

    /// <summary>
    /// What every launch whose ground writes a linked worktree - an agent an earlier version of this tool made - says as it
    /// starts, each tree one saying: inside the isolation the work writes the tree's own git directory alone, so git add and
    /// git fetch fail there, and the remedy names the verbs that give the agent a repository of its own. Empty where no tree
    /// the ground writes is a linked worktree, and where it runs under no mechanism, whose denial nothing fails.
    /// </summary>
    public IReadOnlyList<string> LegacyWorktrees { get; init; } = [];

    /// <summary>What the launch's client runs its processes under, handed it in its run: <see cref="ForCommands"/>, <see cref="ForClaudeCode"/> and <see cref="ClaudeCodeOutside"/>.</summary>
    public LaunchProcesses Processes => new(ForCommands, ForClaudeCode) { ClaudeCodeOutside = ClaudeCodeOutside };

    /// <summary>
    /// What every launch says where it starts on the words policy alone, its agent able to run a command: why there is no
    /// mechanism, and what would give one; or, under a mechanism that leaves something reached, what. Null where it runs
    /// under one that leaves nothing, or needs none.
    /// </summary>
    public string? Notice => NoticeSaying("the agent's commands run on the words policy alone, with no isolation of the operating system's", "the agent's commands run");

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
/// inside one - whole, or in an outer layer. The decision it comes from (<see cref="LaunchIsolation"/>) stays with the one
/// who asked it, who releases it once the launch has run, so a client is handed nothing it could end.
/// </summary>
/// <param name="Commands">The isolation each command of the launch is started with; null where there is none.</param>
/// <param name="ClaudeCode">The isolation a Claude Code launch's whole process is started with; null where there is none, or it runs outside.</param>
public sealed record LaunchProcesses(ProcessIsolation? Commands, ProcessIsolation? ClaudeCode)
{
    /// <summary>Why a Claude Code launch's own process runs outside every layer, handing each command in (<see cref="LaunchIsolation.ClaudeCodeOutside"/>); null where it does not.</summary>
    public string? ClaudeCodeOutside { get; init; }
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
    IReadOnlyList<string> StateDirectories)
{
    /// <summary>
    /// Whether what it runs reaches the user's SSH agent: a push to a remote over ssh, or a commit the repository signs with
    /// an ssh key (<see cref="GitSsh"/>) - and nothing else, so no other hook, and no leg's work, signs as the user.
    /// </summary>
    public bool ReachesSshAgent { get; init; }
}

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
    /// it - written; the step's tree's git files, configuration directory and state, and the leg's own, written by nobody,
    /// but what the leg writes inside them - its copy, and <paramref name="besides"/>. The run's records are none of it:
    /// every log, the ledger, the run's claim and its stop are the tool's own, written from what each phase prints, so
    /// nothing a phase runs plants a link the tool would then write through, or read a hidden place by.
    /// </summary>
    /// <param name="config">The configuration, whose worktrees root is among the state.</param>
    /// <param name="mainCheckout">The main checkout, whose slots are hidden.</param>
    /// <param name="tree">The tree the step works in.</param>
    /// <param name="legTree">The tree the leg works in: <paramref name="tree"/>, or its copy.</param>
    /// <param name="besides">
    /// What else the leg's work writes inside the tool's own places: a sweep's workers in the state, and each action's
    /// build directory for the run in the configuration directory (<see cref="Legs.LegWorkload.WritesIn"/>), where its
    /// steps write what they declare.
    /// </param>
    public static ExecutionGround OfLeg(HarnessConfig config, string mainCheckout, string tree, string legTree, IReadOnlyList<string> besides)
    {
        ArgumentNullException.ThrowIfNull(besides);

        return Of(IsolatedExecution.Legs, config, mainCheckout, [tree, legTree], [legTree, .. besides]);
    }

    /// <summary>
    /// The ground of a commit's or a push's git in <paramref name="tree"/> with the repository's hooks: the tree written -
    /// its work tree, and what git keeps of it but its code-running files - its slots hidden and its state sealed; the
    /// user's SSH agent reached where git's own work needs it (<see cref="GitSsh"/>).
    /// </summary>
    /// <param name="config">The configuration, whose worktrees root is among the state.</param>
    /// <param name="mainCheckout">The main checkout, whose slots are hidden.</param>
    /// <param name="tree">The tree git works in.</param>
    /// <param name="sshAgent">Whether git's own work signs or pushes through the user's SSH agent: a push over ssh, a commit signed with an ssh key.</param>
    public static ExecutionGround OfHooks(HarnessConfig config, string mainCheckout, string tree, bool sshAgent)
        => Of(IsolatedExecution.Hooks, config, mainCheckout, [tree], [tree]) with { ReachesSshAgent = sshAgent };

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
    /// An empty directory of the execution's own under the isolation's own (<see cref="IsolationCaches.OutsideDirectory"/>),
    /// with no tree above it and no place any isolated child writes: where what the tool runs outside the isolation for a
    /// leg's work starts - a set-up's lines - so nothing it finds where it runs, or above it, is the agents'. Taken away
    /// with the decision; null where there is no mechanism, and for a commit's or a push's hooks, which start nothing
    /// outside.
    /// </summary>
    public string? Outside { get; init; }

    /// <summary>
    /// What an execution whose ground writes a linked worktree - a leg pinned to a plain worktree, an agent an earlier
    /// version of this tool made - says on the leg's line as its work starts, each tree one saying: inside the isolation
    /// the work writes the tree's own git directory alone, so git add and git fetch fail there, and the remedy names the
    /// verbs that give the agent a repository of its own. Empty where no tree the ground writes is a linked worktree, and
    /// where it runs under no mechanism, whose denial nothing fails.
    /// </summary>
    public IReadOnlyList<string> LegacyWorktrees { get; init; } = [];

    /// <summary>
    /// What is said wherever it runs with no mechanism under <see cref="AgentIsolationMode.Preferred"/>: why there is none,
    /// what then runs outside, and what would give one; or, under a mechanism that leaves something reached, what. Null
    /// where it runs under one that leaves nothing.
    /// </summary>
    public string? Notice => Kind == IsolatedExecution.Legs
        ? NoticeSaying("what the agents wrote is built, tested and run here with no isolation of the operating system's", "what the agents wrote is built, tested and run")
        : NoticeSaying("the repository's hooks run over the agents' work with no isolation of the operating system's", "the repository's hooks run");

    /// <summary>The refusal of <paramref name="what"/> where it may not run, as a sentence; null where it may.</summary>
    /// <param name="what">What is refused, as the refusal names it: <c>'step'</c>.</param>
    public string? Refusal(string what) => RefusalSaying(what, Kind == IsolatedExecution.Legs
        ? "nothing the agents wrote is built, tested or run here"
        : "no hook of the repository's runs over the agents' work here");

    /// <summary>
    /// The isolation of the work of a leg placed in the WSL distribution <paramref name="distribution"/> under
    /// <paramref name="mode"/>: none. The distribution runs on this machine outside its isolation, and reaches this
    /// machine's drives under <c>/mnt</c> as the user, written - the credential slots and every tree's git files among
    /// them - so required refuses the leg, and preferred runs it there saying so.
    /// </summary>
    /// <remarks>
    /// A host reached over ssh is decided by nothing here: it is taken to be another machine, which reaches none of this
    /// one's files. One that is this machine, or a WSL distribution, a virtual machine or a container reached over ssh that
    /// shares this machine's drives, would run the agents' work outside the isolation as a distribution does, but cannot be
    /// told from another machine before it is reached: ssh's own configuration may point the name anywhere - a
    /// <c>HostName</c>, a <c>ProxyJump</c>, a <c>ProxyCommand</c> - a loopback address may be a port forwarded into a
    /// distribution, a virtual machine or a container, and a network address may be a virtual machine's that mounts this
    /// machine's drives; and asked once reached, the host answers with what the agents' work there may already have
    /// written. A check that held for some such hosts would read as a guarantee for all, so none is made, and help says to
    /// name no such host where the agents' work must be held.
    /// </remarks>
    /// <param name="mode">The configuration's mode.</param>
    /// <param name="distribution">The distribution, as the leg's host names it.</param>
    public static ExecutionIsolation InDistribution(string mode, string distribution)
        => new(
            IsolatedExecution.Legs,
            mode,
            null,
            null,
            $"the leg runs in the WSL distribution '{distribution}', which this machine's isolation does not hold and which reaches this machine's drives under /mnt",
            "place the leg on this machine, or on another machine reached over ssh");
}


/// <summary>
/// What an isolation profile is built from, whoever asks: the trees worked over, the places written, read and kept from the
/// child, the user's SSH agent it reaches outside, and the launch's own directory - an agent's ground (<see cref="Of(AgentPolicyGround, string)"/>), or an
/// execution of the tool's over what an agent wrote (<see cref="ExecutionGround"/>).
/// </summary>
/// <param name="Trees">Every tree worked over, whose git's code-running files, git's own pointers, configuration directory and state no write reaches.</param>
/// <param name="WritableRoots">The places written - and, for a tree among them, its git directories but their code-running files.</param>
/// <param name="ReadRoots">The places read as they stand, wherever the mechanism gives the child a place of its own in their stead.</param>
/// <param name="SlotDirectories">The credential slots, neither read nor written.</param>
/// <param name="ClaudeCodeDirectories">The Claude Code directories, neither read nor written - but each one's shell snapshots, read.</param>
/// <param name="CredentialPlaces">The places the user keeps credentials in, neither read nor written - but the files each leaves read.</param>
/// <param name="StateDirectories">The tool's own state under each tree, written by nobody but where a writable root stands in it.</param>
/// <param name="LaunchDirectory">The launch's own directory, which holds its settings and records; null for an execution with none.</param>
/// <param name="AgentSocket">The user's SSH agent's socket, which the child reaches outside alone and is named at its real spelling; null for an agent's own commands, and for an execution that signs and pushes through none.</param>
internal sealed record IsolationGround(
    IReadOnlyList<string> Trees,
    IReadOnlyList<string> WritableRoots,
    IReadOnlyList<string> ReadRoots,
    IReadOnlyList<string> SlotDirectories,
    IReadOnlyList<string> ClaudeCodeDirectories,
    IReadOnlyList<HiddenPlace> CredentialPlaces,
    IReadOnlyList<string> StateDirectories,
    string? LaunchDirectory,
    string? AgentSocket)
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
            ground.CredentialPlaces,
            ground.StateDirectories,
            launchDirectory,
            null);
    }

    /// <summary>
    /// The ground of one of the tool's own executions: its trees and writable roots read as they stand, every Claude Code
    /// directory and every place the user keeps credentials in hidden as from an agent's own launch, no launch directory -
    /// and, where git's own work signs or pushes through it, the user's SSH agent's socket.
    /// </summary>
    /// <param name="ground">The execution's ground.</param>
    /// <param name="claudeCode">Every Claude Code directory on this machine (<see cref="ClaudeCodeDirectories"/>).</param>
    /// <param name="credentials">Every place the user keeps credentials in on this machine (<see cref="UserCredentials.Of"/>).</param>
    /// <param name="agentSocket">The user's SSH agent's socket, whole; null where there is none.</param>
    public static IsolationGround Of(ExecutionGround ground, IReadOnlyList<string> claudeCode, IReadOnlyList<HiddenPlace> credentials, string? agentSocket)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(claudeCode);
        ArgumentNullException.ThrowIfNull(credentials);

        return new(
            ground.Trees,
            ground.WritableRoots,
            [.. ground.Trees.Concat(ground.WritableRoots)],
            ground.SlotDirectories,
            claudeCode,
            credentials,
            ground.StateDirectories,
            null,
            ground.ReachesSshAgent ? agentSocket : null);
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

    /// <summary>
    /// The places the isolation hides from every isolated command over the repository whose main checkout is
    /// <paramref name="mainCheckout"/> (<see cref="IsolationHidden.Places"/>): its credential slots, every Claude Code
    /// directory there is for <paramref name="config"/>, and the file Claude Code keeps beside one - what a read of the
    /// tool's that follows a link never reaches.
    /// </summary>
    /// <param name="config">The configuration, whose providers name the Claude Code directory a slot sets.</param>
    /// <param name="mainCheckout">The main checkout, whose slots are hidden.</param>
    IReadOnlyList<IsolatedPath> Hidden(HarnessConfig config, string mainCheckout);
}

/// <summary>
/// The owner of the operating system's isolation of what an agent runs, and of what the tool runs of an agent's work: the
/// configuration's mode, this machine's mechanism as its platform proved it once, and each profile built from its ground -
/// the machine read-only; written only the ground's writable roots, what git writes of a tree that is itself written -
/// its own git directory (<see cref="GitWritten"/>) - the launch's own temporary directory and the declared tool caches of its write
/// scope's own (<see cref="IsolationCaches"/>), which no child of another reach shares; neither read nor written the
/// credential slots, every Claude Code directory (<see cref="ClaudeCodeDirectories"/>) and every place the user keeps
/// credentials in (<see cref="UserCredentials"/>), but the files each leaves read; reached outside no Unix socket but the
/// one the ground names - the user's SSH
/// agent's, for a push over ssh or a commit signed with an ssh key, named to the child at its real spelling, which no
/// hidden place stands on the way to; and inside what is written, written by nobody git's
/// code-running files and its pointers to its own directories, every git directory's submodules, its replacements and
/// its alternates, every configuration file git reads and every file one includes, each tree's configuration directory,
/// the tool's state and the launch's own directory. Every place is ruled at its real spelling; one inside a written place
/// that is not there is made first, so no command makes it and has it read or run outside later - but the
/// <see cref="CommonDirectoryFile"/> of a git directory the work writes that keeps its refs in a reftable is never made,
/// for any file there breaks every commit git makes on a branch: the path is held absent where the mechanism holds a path
/// that is not there, and where none can, the ground is refused. Every git directory it
/// names is pinned for every later git of the tool's in its tree, and what the work leaves in those it writes that no
/// rule holds - an operation's state, a replacement - is looked at once it has run (<see cref="IsolationDecision.AfterwardAsync"/>).
/// </summary>
/// <remarks>
/// A launch or an execution is given no isolation - which the mode turns into a refusal or a notice - where its places
/// cannot be held: git cannot say where a tree keeps its files or what replacements its repository holds, a main
/// checkout's git directory holds a <c>commondir</c> leading to another repository's own, or one at all - or none, where
/// the mechanism holds no path that is not there - in a repository that keeps its refs in a reftable, in which any
/// <c>commondir</c> file in a git directory the work writes breaks every commit git makes on a branch, a file git's
/// configuration includes is not there inside a written place, a guarded place stands on a file system that ignores
/// letter case and the mechanism holds the one spelling, a link stands where the child writes on the way to a guarded
/// place, which a command could replace with one leading elsewhere, or a place the user keeps credentials in holds a
/// tree or a place written, which hiding it would hide too.
/// </remarks>
/// <param name="platform">Finds and proves this machine's mechanism, once per process.</param>
/// <param name="git">Asks git where each tree keeps its own files, its worktrees and its replacements.</param>
/// <param name="fileSystem">Finds what each place is and where it leads, and makes one that is not there.</param>
/// <param name="host">This machine: its home, and how it compares paths.</param>
/// <param name="environment">Reads this process's environment, for where the user's cache directory, the places the user keeps credentials in and the user's SSH agent are; this process's own where left out.</param>
/// <param name="pins">Keeps each tree's git directories, as git named them, for every later git of this tool's in it; none where left out.</param>
/// <param name="claudeCode">Every Claude Code directory on this machine, which an execution hides as a launch does; this machine's own alone, the slot unread, where left out.</param>
public sealed class AgentIsolation(
    IIsolationPlatform platform,
    IGitClient git,
    IFileSystem fileSystem,
    IHostPlatform host,
    Func<string, string?>? environment = null,
    GitDirectoryPins? pins = null,
    ClaudeCodeDirectories? claudeCode = null) : IAgentIsolation
{
    /// <summary>The variable the user's SSH agent's socket is named by.</summary>
    public const string SshAgentSocketVariable = "SSH_AUTH_SOCK";

    /// <summary>The directory under a Claude Code directory that holds the shell snapshots its commands source, which stays read.</summary>
    public const string ShellSnapshots = "shell-snapshots";

    /// <summary>
    /// The file git reads in a git directory as the pointer to the common directory it takes the repository's
    /// configuration, hooks, objects and refs from: a linked worktree's leads to its repository's, and one in a main
    /// checkout's git directory would lead every git started there to another repository's own files.
    /// </summary>
    public const string CommonDirectoryFile = "commondir";

    /// <summary>
    /// What a <see cref="CommonDirectoryFile"/> the isolation makes in a main checkout's git directory holds: that directory
    /// itself, which git takes as no pointer at all - it refuses to read an empty one - so the file can be held as every
    /// other guarded place is, and no command makes one leading elsewhere. Made only where the repository keeps its refs
    /// as loose files or a <c>packed-refs</c>: where it keeps them in a reftable, no file at all may stand there, for any
    /// breaks every commit git makes on a branch, and the path is held absent instead.
    /// </summary>
    public const string ItsOwnDirectory = ".";

    /// <summary>
    /// What an operation under way keeps in a tree's own git directory, which git's next continue - the user's, an
    /// editor's - goes on with as it lies: an interactive rebase's todo, whose <c>exec</c> lines it runs, an am's or a
    /// rebase's patches, a cherry-pick's or a revert's commits. None can be made empty first - git reads an empty one as an
    /// operation under way - so what the work leaves of them is looked at once it has run.
    /// </summary>
    public static IReadOnlyList<string> OperationState { get; } = ["rebase-merge", "rebase-apply", "sequencer"];

    /// <summary>How long the look once the work has run may take: what it asks git, a list of a repository's replacements, is answered at once where nothing holds git up.</summary>
    public static readonly TimeSpan AfterwardLimit = TimeSpan.FromMinutes(1);

    /// <summary>How much of an operation's state is read for what it holds before it is taken as changed whatever it holds.</summary>
    private const long OperationStateBound = 64L * 1024 * 1024;

    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;
    private readonly ClaudeCodeDirectories _claudeCode = claudeCode ?? new ClaudeCodeDirectories(fileSystem, host, keys: null, environment);

    /// <inheritdoc/>
    public IReadOnlyList<IsolatedPath> Hidden(HarnessConfig config, string mainCheckout)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckout);

        return IsolationHidden.Places(HarnessLayout.SecretSlotDirectoriesUnder(mainCheckout), _claudeCode.Of(config, mainCheckout), UserCredentials.Of(config, host.HomeDirectory, host.Current, _environment));
    }

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

        return new LaunchIsolation(held.Mode, held.Mechanism, held.Profile, held.Without, held.Remedy)
        {
            ClaudeCode = held.ClaudeCode,
            Release = held.Release,
            After = held.After,
            LegacyWorktrees = held.Legacy,
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Its profile is built as an agent's command's is, from the same places: the slots, every Claude Code directory and
    /// every place the user keeps credentials in hidden, git's code-running files of each tree, every configuration file
    /// git reads and the tool's state written by nobody, the ground's own places written again inside them, the private
    /// temporary directory and the declared caches
    /// of its write scope written - for what it runs is the agents' work however it is started. Where git's own work signs
    /// or pushes through it - a push over ssh, a commit signed with an ssh key - the user's SSH agent's socket is reached
    /// alone. A leg's work is given an empty directory outside every tree for what the tool starts outside it
    /// (<see cref="ExecutionIsolation.Outside"/>).
    /// </remarks>
    public async Task<ExecutionIsolation> ForExecutionAsync(HarnessConfig config, ExecutionGround ground, string mainCheckout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ground);

        var socket = _environment(SshAgentSocketVariable) is { Length: > 0 } named && Path.IsPathFullyQualified(named) && fileSystem.KindOf(named) != PathKind.None ? Real(named) : null;
        var credentials = UserCredentials.Of(config, host.HomeDirectory, host.Current, _environment);
        var held = await HeldAsync(config, IsolationGround.Of(ground, _claudeCode.Of(config, mainCheckout), credentials, socket), mainCheckout, "make sure git 2.31 or newer answers in the trees the run works over", cancellationToken).ConfigureAwait(false);

        // A leg's set-ups start outside the isolation, in a directory of the execution's own beside its caches and temporary
        // directories, which no child writes.
        var outside = ground.Kind == IsolatedExecution.Legs && held.Mechanism is not null
            ? Path.Combine(IsolationCaches.Root(host.HomeDirectory, host.Current, _environment), IsolationCaches.OutsideDirectory, Guid.NewGuid().ToString("N")[..12])
            : null;

        if (outside is not null)
        {
            fileSystem.CreateDirectory(outside);
        }

        var release = held.Release;

        return new ExecutionIsolation(ground.Kind, held.Mode, held.Mechanism, held.Profile, held.Without, held.Remedy)
        {
            Release = outside is null ? release : () =>
            {
                release?.Invoke();
                Removed(outside);
            },
            After = held.After,
            Outside = outside,
            LegacyWorktrees = held.Legacy,
        };
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
            return Held.None(mode, unread, gitPlaces.Remedy ?? unreadRemedy);
        }

        // What the work may leave in the git directories it writes that no rule holds is looked at once it has run, against
        // what is there before it starts.
        var writtenTrees = Written(ground, gitPlaces);
        var before = await LeftAsync(writtenTrees, cancellationToken).ConfigureAwait(false);

        if (before.Unread is { } unreadBefore)
        {
            return Held.None(mode, unreadBefore, "look at what keeps git from listing the repository's refs, and remove it");
        }

        var root = IsolationCaches.Root(host.HomeDirectory, host.Current, _environment);
        var cachesRoot = Path.Combine(root, IsolationCaches.Directory);
        var comparer = StringComparer.FromComparison(host.PathComparison);

        // A tree the ground writes that is one of git's linked worktrees - an agent an earlier version of this tool made -
        // is said at the launch: the work writes its own git directory alone there, so git add and git fetch fail inside the
        // isolation, and the remedy names the verbs that give the agent a repository of its own.
        // A tree the ground writes that is one of git's linked worktrees - an agent an earlier version of this tool made -
        // is said at the launch: the work writes its own git directory alone there, so git add and git fetch fail inside the
        // isolation, and the remedy names the verbs that give the agent a repository of its own.
        var legacy = gitPlaces.Directories
            .Where(directory => ground.WritableRoots.Any(writable => comparer.Equals(Real(writable), Real(directory.Tree)))
                && !comparer.Equals(Real(directory.GitDirectory), Real(directory.CommonDirectory)))
            .Select(directory => $"'{directory.Tree}' is a linked worktree an earlier version made: git add and fetch fail there; delete-agent and a new agent give it a repository of its own")
            .ToList();

        // What is written: the ground's roots, and what git writes of a tree that is itself written - its own git directory,
        // whatever the tree's kind. Its caches are its write scope's own, those of every scope gone swept.
        var written = ground.WritableRoots
            .Concat(writtenTrees.SelectMany(GitWritten))
            .Select(Real)
            .Distinct(comparer)
            .ToList();
        IsolationCaches.Sweep(fileSystem, cachesRoot);
        var caches = IsolationCaches.Prepared(fileSystem, cachesRoot, written, ground.AgentSocket is { } reached ? [Real(reached)] : []);

        // A mechanism that gives the child a temporary directory of its own is named none.
        var temp = mechanism.OwnTemp ? null : Path.Combine(root, IsolationCaches.TempDirectory, TempName(ground.LaunchDirectory));
        var shared = temp is not null && fileSystem.DirectoryExists(temp);

        if (temp is not null)
        {
            fileSystem.CreateDirectory(temp);
        }

        var built = Built(ground, gitPlaces, [.. written, Real(caches)], caches, temp, mechanism);

        if (built.Unheld is { } unheld)
        {
            // Taken away where this launch made it; one an earlier launch of the same directory still works in is left.
            if (temp is not null && !shared)
            {
                Removed(temp);
            }

            return Held.None(mode, unheld.Why, unheld.Remedy);
        }

        // Each tree's git directories, as git named them now, for every later git of the tool's in it: a linked worktree's
        // .git file, and a commondir a main checkout's .git came to hold, would lead it elsewhere.
        foreach (var (tree, gitDirectory, commonDirectory, _) in gitPlaces.Directories)
        {
            pins?.Pin(tree, gitDirectory, commonDirectory);
        }

        return new Held(mode, mechanism, built.Profile, built.ClaudeCode, null, null, legacy, temp is null ? null : () => Removed(temp), async token => LeftSince(before, await LeftAsync(writtenTrees, token).ConfigureAwait(false)));
    }

    /// <summary>A decision as the builder makes it, before it is a launch's or an execution's.</summary>
    private sealed record Held(string Mode, IIsolationMechanism? Mechanism, IsolationProfile? Profile, IsolationProfile? ClaudeCode, string? Without, string? Remedy, IReadOnlyList<string> Legacy, Action? Release, Func<CancellationToken, Task<string?>>? After = null)
    {
        public static Held None(string mode, string? why, string? remedy) => new(mode, null, null, null, why, remedy, [], null);
    }

    /// <summary>
    /// What the work may leave in the git directories it writes that no rule of the isolation holds, and git started outside
    /// goes on with as it lies.
    /// </summary>
    /// <param name="Operations">Each operation's state there is (<see cref="OperationState"/>), by its path, with what it holds.</param>
    /// <param name="Replacements">Every replacement of each repository whose refs are written: its common directory, its ref and the object it names.</param>
    /// <param name="Unread">Why git could not say what replacements there are; null where it did.</param>
    private sealed record Left(IReadOnlyDictionary<string, string> Operations, IReadOnlySet<(string Common, string Ref, string Object)> Replacements, string? Unread);

    /// <summary>
    /// Each tree of <paramref name="gitPlaces"/> that is one of <paramref name="ground"/>'s writable roots, with its git
    /// directories: those whose git the work writes - a linked worktree's own git directory alone, a main checkout's and an
    /// agent repository's whole (<see cref="GitWritten"/>).
    /// </summary>
    private IReadOnlyList<(string Tree, string GitDirectory, string CommonDirectory, bool Reftable)> Written(IsolationGround ground, GitPlaces gitPlaces)
    {
        var comparer = StringComparer.FromComparison(host.PathComparison);

        return [.. gitPlaces.Directories.Where(directory => ground.WritableRoots.Any(root => comparer.Equals(Real(root), Real(directory.Tree))))];
    }

    /// <summary>
    /// What there is in the git directories of <paramref name="written"/> that git started outside would go on with: each
    /// operation's state in each tree's own git directory, with what it holds; and every replacement of each repository,
    /// listed by git - whatever keeps its refs, loose files, <c>packed-refs</c> or a reftable - so nothing the work wrote is
    /// read by the tool itself, and a ref store that holds git up is answered as unread within <see cref="AfterwardLimit"/>.
    /// </summary>
    private async Task<Left> LeftAsync(IReadOnlyList<(string Tree, string GitDirectory, string CommonDirectory, bool Reftable)> written, CancellationToken cancellationToken)
    {
        var comparer = StringComparer.FromComparison(host.PathComparison);
        var operations = new Dictionary<string, string>(comparer);
        var replacements = new HashSet<(string Common, string Ref, string Object)>();

        foreach (var gitDirectory in written.Select(directory => directory.GitDirectory).Distinct(comparer))
        {
            foreach (var place in OperationState.Select(name => Path.Combine(gitDirectory, name)))
            {
                if (Holding(gitDirectory, place) is { } holds)
                {
                    operations[place] = holds;
                }
            }
        }

        // The repositories whose refs the work writes are those whose git directory it writes whole - a main checkout's, an
        // agent repository's own. A linked worktree's work writes no ref of its repository's: its common directory is
        // read-only to it, so its replacements are not the work's to make or move.
        // The repositories whose refs the work writes are those whose git directory it writes whole - a main checkout's, an
        // agent repository's own. A linked worktree's work writes no ref of its repository's: its common directory is
        // read-only to it, so its replacements are not the work's to make or move.
        foreach (var (tree, _, common, _) in written
                     .Where(directory => comparer.Equals(Real(directory.GitDirectory), Real(directory.CommonDirectory)))
                     .DistinctBy(directory => directory.CommonDirectory, comparer))
        {
            var listed = await git.RunWithinAsync(tree, ["for-each-ref", "--format=%(objectname) %(refname)", ReplaceRefs], AfterwardLimit, cancellationToken).ConfigureAwait(false);

            if (listed.TimedOut || !listed.Succeeded)
            {
                return new Left(operations, replacements, $"git could not list the replacements of '{common}' ({(listed.TimedOut ? $"it did not answer within {ReportText.Limit(AfterwardLimit)}" : listed.FailureMessage)})");
            }

            foreach (var line in listed.OutputLines)
            {
                if (line.Trim().Split(' ', 2) is [var named, var reference])
                {
                    replacements.Add((common, reference, named));
                }
            }
        }

        return new Left(operations, replacements, null);
    }

    /// <summary>Where git keeps the refs that put one object's content in place of another's.</summary>
    private const string ReplaceRefs = "refs/replace/";

    /// <summary>
    /// What the operation's state at <paramref name="place"/>, in <paramref name="gitDirectory"/>, holds, as one text that
    /// differs wherever it does - each file beneath it by its path and its content's hash, read never through a link and
    /// never past <see cref="OperationStateBound"/> - and a text no other equals where it cannot be read whole: a link, a
    /// pipe or a device, a file that went while it was read, or more than the bound. Null where nothing is there.
    /// </summary>
    private string? Holding(string gitDirectory, string place)
    {
        try
        {
            var kind = fileSystem.KindOf(place);
            List<string>? files = kind switch
            {
                PathKind.None => null,
                PathKind.File => [place],
                PathKind.Directory => fileSystem.EnumerateFiles(place, recursive: true).Order(StringComparer.Ordinal).ToList(),
                _ => throw new IOException($"'{place}' is a link"),
            };

            if (files is null)
            {
                return null;
            }

            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            var read = 0L;

            foreach (var file in files)
            {
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes($"{Path.GetRelativePath(gitDirectory, file)}\0"));
                using var stream = UnfollowedFiles.OpenRead(fileSystem, gitDirectory, file);

                while (stream.Read(buffer) is var count and > 0)
                {
                    read += count;

                    if (read > OperationStateBound)
                    {
                        throw new IOException($"'{place}' holds more than {OperationStateBound} bytes");
                    }

                    hash.AppendData(buffer, 0, count);
                }

                hash.AppendData([0]);
            }

            return $"{kind} {Convert.ToHexStringLower(hash.GetHashAndReset())}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"unread {Guid.NewGuid():N}";
        }
    }

    /// <summary>
    /// Why nothing of the tool's may go on over the trees once the work has run, from what was there before it,
    /// <paramref name="before"/>, and what is there now, <paramref name="now"/>: git could not say what is there; the
    /// first operation's state made or changed; else the first replacement made or moved - as a sentence with the remedy;
    /// null where none.
    /// </summary>
    private static string? LeftSince(Left before, Left now)
    {
        if (now.Unread is { } unread)
        {
            return $"{unread}, and whether the work put other content in place of an object cannot be told - so nothing goes on over the trees; look at what keeps git from listing the repository's refs, and remove it";
        }

        foreach (var (place, holds) in now.Operations.OrderBy(operation => operation.Key, StringComparer.Ordinal))
        {
            var was = before.Operations.GetValueOrDefault(place);

            if (!string.Equals(was, holds, StringComparison.Ordinal))
            {
                return $"'{place}' {(was is null ? "was made" : "changed")} while it ran: the state of an operation under way, which git's next continue outside - the user's, an editor's - goes on with as it lies, running each exec line its todo holds - so nothing goes on over the trees; look at what it holds, and remove it - never continue the operation, nor abort it, which puts the branch where the state says";
            }
        }

        return now.Replacements.Where(replacement => !before.Replacements.Contains(replacement)).OrderBy(replacement => replacement.Ref, StringComparer.Ordinal).FirstOrDefault() is ({ } common, { } name, _)
            ? $"'{common}' came to hold {name} while it ran: a replacement, which puts other content in place of an object for every git started outside that reads it - so nothing goes on over the trees; look at what it puts in place, and remove it with git update-ref -d {name}"
            : null;
    }

    /// <summary>
    /// git's own places of each of <paramref name="trees"/>, as git names them: what a write must not reach - the hooks
    /// directory git runs hooks from (<c>core.hooksPath</c> where it is set) and the common directory's own <c>hooks/</c>
    /// beside it, which git runs from again once the setting is gone; the common directory's <c>config</c>,
    /// <c>config.worktree</c> and <c>info/</c>; its <c>refs/replace/</c>, whose refs put other content in place of an
    /// object every checkout reads, and <c>objects/info/</c>, whose alternates lend it another store's; its own
    /// <c>commondir</c>, which leads git to another repository's own files, and its <c>worktrees/</c>, which holds every
    /// linked worktree's git directory; each linked worktree's <c>config.worktree</c> and its <c>commondir</c> and
    /// <c>gitdir</c>, which say where its repository is, and its <c>.git</c> file, which says where its git directory is -
    /// the tree's own and every other's git lists; every git directory's <c>modules/</c> - the common one's and each
    /// linked worktree's - whole, for it holds the submodules' own git directories,
    /// their configuration and hooks; and every configuration file git reads in the tree, the user's and what each includes
    /// among them - and each tree's own and common git directory, where the tree is its repository's top, with every file a
    /// configuration file names to include, whether its condition holds now or not (<see cref="GitPlaces.Includes"/>), and
    /// whether the tree's own git directory holds a reftable, the repository keeping its refs there. Or why git could not
    /// say - an answer that is no whole path among it, which git before 2.31 gives - or why what it says cannot be
    /// trusted: a main checkout's <c>.git</c> holding a <c>commondir</c> that leads anywhere but to itself.
    /// </summary>
    /// <param name="trees">The trees the agent touches.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public async Task<GitPlaces> GitPlacesAsync(IReadOnlyList<string> trees, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trees);

        var comparer = StringComparer.FromComparison(host.PathComparison);
        var places = new List<IsolatedPath>();
        var directories = new List<(string Tree, string GitDirectory, string CommonDirectory, bool Reftable)>();
        var listed = new HashSet<string>(comparer);
        var read = new HashSet<string>(comparer);
        var includes = new List<string>();

        foreach (var tree in trees.Distinct(comparer))
        {
            var dotGit = Path.Combine(tree, ".git");

            // A main checkout's .git holding a commondir that leads anywhere but to itself leads git - this tool's own
            // unpinned among it - to another repository's own files: git's answers here would be that one's.
            if (fileSystem.DirectoryExists(dotGit) && !LeadsToItself(dotGit))
            {
                var pointer = Path.Combine(dotGit, CommonDirectoryFile);

                return GitPlaces.Not(
                    $"'{pointer}' leads git to another repository's own files, where a main checkout's git directory holds none but one naming itself - something made it",
                    $"look at where '{pointer}' leads, and remove it");
            }

            var answer = await git.RunAsync(tree, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir", "--git-path", "hooks", "--git-path", "reftable", "--show-toplevel"], cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!answer.Succeeded || answer.OutputLines is not [var gitLine, var commonLine, var hooksLine, var reftableLine, var topLine, ..])
            {
                return GitPlaces.Not($"git could not say where '{tree}' keeps its own files ({answer.FailureMessage})");
            }

            if (Unwhole([gitLine, commonLine, hooksLine, reftableLine, topLine]) is { } partial)
            {
                return GitPlaces.Not($"git answered '{partial}' for where '{tree}' keeps its own files, which is no whole path");
            }

            var gitDirectory = Path.GetFullPath(gitLine.Trim());
            var common = Path.GetFullPath(commonLine.Trim());
            var top = Path.GetFullPath(topLine.Trim());

            // A tree's git directories are its own only where it is its repository's top: a directory inside another's work
            // tree - a leg's copy that is no repository of its own - is answered by that one, whose git it never writes.
            // Whether its own git directory holds a reftable says where the repository keeps its refs only for a tree at
            // its repository's top, for a linked worktree's own directory holds none: its repository's reftable lives in
            // the common directory.
            if (comparer.Equals(Real(top), Real(tree)))
            {
                directories.Add((tree, gitDirectory, common, fileSystem.DirectoryExists(Path.GetFullPath(reftableLine.Trim()))));
            }

            places.Add(new IsolatedPath(Path.GetFullPath(hooksLine.Trim()), Directory: true));
            places.Add(new IsolatedPath(Path.Combine(common, "hooks"), Directory: true));
            places.Add(new IsolatedPath(Path.Combine(common, "config"), Directory: false));
            places.Add(new IsolatedPath(Path.Combine(common, "config.worktree"), Directory: false));
            places.Add(new IsolatedPath(Path.Combine(common, "info"), Directory: true));
            places.Add(new IsolatedPath(Path.Combine(common, "refs", "replace"), Directory: true));
            places.Add(new IsolatedPath(Path.Combine(common, "objects", "info"), Directory: true));
            places.Add(new IsolatedPath(Path.Combine(common, "modules"), Directory: true));

            // The common directory's own commondir, which a main checkout's git directory, written whole, would otherwise let
            // a command make; and its worktrees/, so the work makes no worktree's git directory there and writes no other's.
            places.Add(new IsolatedPath(Path.Combine(common, CommonDirectoryFile), Directory: false));
            places.Add(new IsolatedPath(Path.Combine(common, "worktrees"), Directory: true));

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
                    places.Add(new IsolatedPath(Path.Combine(linked, CommonDirectoryFile), Directory: false));
                    places.Add(new IsolatedPath(Path.Combine(linked, "gitdir"), Directory: false));
                    places.Add(new IsolatedPath(Path.Combine(linked, "modules"), Directory: true));
                }
            }

            // Every other linked worktree's .git file, which a ground writing the directory it stands in - a worktree a person
            // or another tool made inside the main checkout - would otherwise let a command point at a repository of its own.
            if (listed.Add(common))
            {
                IReadOnlyList<GitWorktree> linkedTrees;

                try
                {
                    linkedTrees = await git.ListWorktreesAsync(tree, cancellationToken).ConfigureAwait(false);
                }
                catch (HarnessException ex)
                {
                    return GitPlaces.Not($"git could not list the worktrees of '{tree}' ({ex.Message})");
                }

                places.AddRange(linkedTrees
                    .Where(worktree => !worktree.IsMain && !worktree.IsBare && fileSystem.FileExists(Path.Combine(worktree.Path, ".git")))
                    .Select(worktree => new IsolatedPath(Path.GetFullPath(Path.Combine(worktree.Path, ".git")), Directory: false)));
            }

            var configuration = await git.RunAsync(tree, ["config", "--list", "--show-origin", "--includes", "-z"], cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!configuration.Succeeded)
            {
                return GitPlaces.Not($"git could not say which configuration files it reads in '{tree}' ({configuration.FailureMessage})");
            }

            // git moves to the top of the work tree before it reads, so a file it names relatively is named from there.
            var files = ConfigurationFiles(configuration.StandardOutput, top);
            read.UnionWith(files);
            places.AddRange(files.Select(file => new IsolatedPath(file, Directory: false)));
            includes.AddRange(IncludeTargets(configuration.StandardOutput, top, host.HomeDirectory));
        }

        // A file an include names that git reads nothing from now - its condition does not hold, or it is empty - is read
        // for the files it would include in turn, each once.
        var waiting = new Queue<string>(includes.Where(target => !read.Contains(target)));
        var named = new HashSet<string>(includes, comparer);

        while (trees.Count > 0 && waiting.TryDequeue(out var target))
        {
            if (!read.Add(target) || !fileSystem.FileExists(target))
            {
                continue;
            }

            var own = await git.RunAsync(trees[0], ["config", "--file", target, "--list", "--show-origin", "-z"], cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var more in own.Succeeded ? IncludeTargets(own.StandardOutput, trees[0], host.HomeDirectory) : [])
            {
                if (named.Add(more))
                {
                    waiting.Enqueue(more);
                }
            }
        }

        return new GitPlaces([.. places.DistinctBy(place => place.Path, comparer)], directories, null) { Includes = [.. named] };
    }

    /// <summary>
    /// Whether the <see cref="CommonDirectoryFile"/> in <paramref name="gitDirectory"/> leads git nowhere but to that
    /// directory itself - none there, or one naming it (<see cref="ItsOwnDirectory"/>) - read as git reads it, its line
    /// ends taken off and a relative path against the directory; never through a link, never more than a line's worth, and
    /// not where it is a link, a pipe or a device, or could not be read, which leads nowhere it can be told.
    /// </summary>
    private bool LeadsToItself(string gitDirectory)
    {
        var file = Path.Combine(gitDirectory, CommonDirectoryFile);

        try
        {
            if (fileSystem.KindOf(file) == PathKind.None)
            {
                return true;
            }

            using var stream = UnfollowedFiles.OpenRead(fileSystem, gitDirectory, file);
            var buffer = new byte[4096];
            var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var named = System.Text.Encoding.UTF8.GetString(buffer, 0, count).TrimEnd('\r', '\n');

            return count < buffer.Length
                && named.Length > 0
                && StringComparer.FromComparison(host.PathComparison).Equals(Real(Path.GetFullPath(named, gitDirectory)), Real(gitDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
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
    /// Every file an <c>include.path</c> or <c>includeIf.&lt;condition&gt;.path</c> of <c>git config --list --show-origin -z</c>
    /// names, whole, as git takes it whether its condition holds or not: <c>~/</c> in the user's home, a relative path
    /// against the directory of the file that names it - itself against <paramref name="top"/> where git names it
    /// relatively. One under another user's home, under git's own install, or named relatively by no file is none of them.
    /// </summary>
    /// <param name="answered">What git answered.</param>
    /// <param name="top">
    /// The top of the work tree git was asked in, which it moves to before it reads anything: a directory below it is
    /// answered with the repository's own configuration named from there, as <c>.git/config</c>.
    /// </param>
    /// <param name="home">The user's home.</param>
    public static IReadOnlyList<string> IncludeTargets(string answered, string top, string home)
    {
        ArgumentNullException.ThrowIfNull(answered);

        const string FileOrigin = "file:";
        var fields = answered.Split('\0');
        var targets = new List<string>();

        for (var at = 0; at + 1 < fields.Length; at += 2)
        {
            var (origin, entry) = (fields[at], fields[at + 1]);

            if (entry.IndexOf('\n') is var split and > 0
                && entry[..split] is var key
                && (key == "include.path" || (key.StartsWith("includeif.", StringComparison.Ordinal) && key.EndsWith(".path", StringComparison.Ordinal)))
                && entry[(split + 1)..] is { Length: > 0 } value)
            {
                var from = origin.StartsWith(FileOrigin, StringComparison.Ordinal) && origin.Length > FileOrigin.Length ? Path.GetFullPath(origin[FileOrigin.Length..], top) : null;

                string? target = value switch
                {
                    "~" => home,
                    _ when value.StartsWith("~/", StringComparison.Ordinal) => Path.GetFullPath(Path.Combine(home, value[2..])),
                    _ when value.StartsWith('~') || value.StartsWith("%(prefix)/", StringComparison.Ordinal) => null,
                    _ when Path.IsPathFullyQualified(value) => Path.GetFullPath(value),
                    _ => from is not null && Path.GetDirectoryName(from) is { } directory ? Path.GetFullPath(Path.Combine(directory, value)) : null,
                };

                if (target is not null)
                {
                    targets.Add(target);
                }
            }
        }

        return [.. targets.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every file <c>git config --list --show-origin -z</c> names as a source, whole: each entry's origin before its key,
    /// <c>file:</c> and the path - against <paramref name="top"/> where git names it relatively.
    /// </summary>
    /// <param name="answered">What git answered.</param>
    /// <param name="top">
    /// The top of the work tree git was asked in, which it moves to before it reads anything: a directory below it is
    /// answered with the repository's own configuration named from there, as <c>.git/config</c>.
    /// </param>
    public static IReadOnlyList<string> ConfigurationFiles(string answered, string top)
    {
        ArgumentNullException.ThrowIfNull(answered);

        const string FileOrigin = "file:";
        var fields = answered.Split('\0');

        return [.. fields
            .Where((_, at) => at % 2 == 0)
            .Where(origin => origin.StartsWith(FileOrigin, StringComparison.Ordinal) && origin.Length > FileOrigin.Length)
            .Select(origin => Path.GetFullPath(origin[FileOrigin.Length..], top))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The profile of <paramref name="ground"/> as this machine's mechanism holds it - a command's, and a Claude Code
    /// launch's whole process's where the mechanism holds one (<see cref="ClaudeCodeOf"/>) - or why it cannot be held:
    /// <paramref name="written"/> written, its write scope's <paramref name="caches"/> among them.
    /// </summary>
    private (IsolationProfile Profile, IsolationProfile? ClaudeCode, (string Why, string Remedy)? Unheld) Built(IsolationGround ground, GitPlaces gitPlaces, IReadOnlyList<string> written, string caches, string? temp, IIsolationMechanism mechanism)
    {
        var comparer = StringComparer.FromComparison(host.PathComparison);
        var open = new IsolationProfile([], [.. written.Select(root => new IsolatedWrite(root, Directory: true, Writable: true))]) { Temp = temp is null ? null : Real(temp) };

        // A git directory the work writes whole - a main checkout's, an agent repository's own - that keeps its refs in a
        // reftable holds no commondir of its own, whatever the file holds: git reads one there and splits the repository's
        // refs over two stores, and every commit git makes on a branch fails ('fatal: cannot lock references'). Where the
        // file is there the ground is refused, for the work's own commits inside would fail whatever it holds; where it is
        // not, it is never made, the path held against every command's making one - which only a mechanism that holds a
        // path that is not there can do, and where none can, the ground is refused with the remedy.
        var heldAbsent = new HashSet<string>(comparer);

        foreach (var directory in gitPlaces.Directories)
        {
            if (!directory.Reftable
                || !comparer.Equals(Real(directory.GitDirectory), Real(directory.CommonDirectory))
                || !open.Writable(Real(directory.GitDirectory)))
            {
                continue;
            }

            var pointer = Path.Combine(directory.GitDirectory, CommonDirectoryFile);

            if (fileSystem.FileExists(pointer))
            {
                return (IsolationProfile.Nothing, IsolationProfile.Nothing, (
                    $"'{pointer}' is there, and the repository keeps its refs in a reftable, where any commondir file in a git directory the work writes breaks every commit git makes on a branch, whatever it holds - something made it",
                    $"remove '{pointer}', which nothing makes in a repository that keeps its refs in a reftable"));
            }

            if (!mechanism.HoldsAnAbsentFile)
            {
                return (IsolationProfile.Nothing, IsolationProfile.Nothing, (
                    $"'{pointer}' cannot be held, for the repository keeps its refs in a reftable, where no commondir file may stand in a git directory the work writes, and {mechanism.Name} holds no path that is not there - whatever it mounted over one would put a file of its own there",
                    "put the repository's refs in the files format (git refs migrate --ref-format=files), where a main checkout's commondir naming itself breaks nothing"));
            }

            heldAbsent.Add(pointer);
        }

        // A place the user keeps credentials in that holds a tree or a place written would hide it with them.
        foreach (var credential in ground.CredentialPlaces)
        {
            var real = Real(credential.Path);

            if (ground.Trees.Select(Real).Concat(written).Concat(open.Temp is { } own ? [own] : []).FirstOrDefault(root => IsolationProfile.Covers(real, root)) is { } held)
            {
                return (IsolationProfile.Nothing, IsolationProfile.Nothing, (
                    $"'{credential.Path}' is hidden as a place the user keeps credentials in, and holds '{held}', which the isolated work needs, so hiding it would hide that too",
                    "keep the trees and the isolation's own directory outside it - a place agentPolicy.hiddenPlaces adds may be taken out of it"));
            }
        }

        var hidden = IsolationHidden.Places(ground.SlotDirectories, ground.ClaudeCodeDirectories, ground.CredentialPlaces).Select(place => (place: place.Path, directory: place.Directory)).ToList();

        // A file git's configuration includes is held as it stands; one not there, where a command could make it, cannot be.
        var included = new List<IsolatedPath>();

        foreach (var target in gitPlaces.Includes)
        {
            if (fileSystem.FileExists(target) || fileSystem.DirectoryExists(target))
            {
                included.Add(new IsolatedPath(target, Directory: fileSystem.DirectoryExists(target)));
            }
            else if (Path.GetDirectoryName(target) is { Length: > 0 } holder && open.Writable(Real(holder)))
            {
                return (IsolationProfile.Nothing, IsolationProfile.Nothing, (
                    $"git's configuration includes '{target}', which is not there, inside a place the agent writes: a command could make it, and every git started outside would read it",
                    $"make '{target}' - an empty file includes nothing - or take its include out of git's configuration"));
            }
        }

        var sealedPlaces = gitPlaces.Places
            .Concat(included)
            .Concat(ground.Trees.Select(tree => new IsolatedPath(Path.Combine(tree, HarnessLayout.DirectoryName), Directory: true)))
            .Concat(ground.StateDirectories.Select(State))
            .Concat(ground.LaunchDirectory is { } launch ? [new IsolatedPath(launch, Directory: true)] : [])
            .ToList();

        // A place outside every written place is read-only already, and one beneath another guarded place is held by it.
        var keptHidden = new List<IsolatedPath>();

        foreach (var (place, directory) in hidden.DistinctBy(entry => entry.place, comparer))
        {
            if (Kept(place, directory, open, stateFile: false, absent: false) is { } kept)
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

            if (Kept(place.Path, place.Directory, open, stateFile: IsStateFile(place.Path), absent: heldAbsent.Contains(place.Path)) is { } kept)
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

        // What a program reads by itself inside a hidden directory of the user's credentials - ssh's known_hosts and config -
        // is read again there, at the hidden directory's real spelling, whatever the file itself leads to.
        var hiddenDirectories = keptHidden.Where(place => place.Directory).Select(place => place.Path).ToHashSet(comparer);
        IReadOnlyList<string> readable =
        [
            .. ground.CredentialPlaces
                .Select(credential => (credential, real: Real(credential.Path)))
                .Where(pair => hiddenDirectories.Contains(pair.real))
                .SelectMany(pair => pair.credential.Readable.Select(file => Path.Combine(pair.real, Path.GetRelativePath(pair.credential.Path, file))))
                .Distinct(comparer),
        ];

        var socket = ground.AgentSocket is { } agent ? Real(agent) : null;
        var profile = new IsolationProfile(
            [.. keptHidden.DistinctBy(place => place.Path, comparer)],
            IsolationProfiles.Ordered(keptSealed, written, comparer))
        {
            Reads = [.. ground.Trees.Concat(ground.ReadRoots).Select(Real).Distinct(comparer)],
            Reopened = [.. ground.ClaudeCodeDirectories.Select(directory => Path.Combine(Real(directory), ShellSnapshots)).Distinct(comparer), .. readable],
            Temp = open.Temp,
            Environment = [.. TempVariable(open.Temp), .. IsolationCaches.Environment(Real(caches)), .. IsolationCaches.Installed(host.HomeDirectory, host.Current, _environment, fileSystem.DirectoryExists), .. AgentSocketVariable(socket)],
            Sockets = socket is null ? [] : [socket],
        };

        return (profile, ClaudeCodeOf(profile, ground, hidden, readable, mechanism, comparer), null);
    }

    /// <summary>
    /// What a Claude Code launch's whole process is denied, from its commands' <paramref name="profile"/>, as the mechanism
    /// takes the launch: where it takes no command handed to it (MXC's container), the commands' profile with the Claude
    /// Code directories and <c>~/.claude.json</c> read alone - written, they would let a command plant what Claude Code runs
    /// outside later, its settings' hooks, its own file's servers, but the files a program reads by itself inside a hidden
    /// directory of the user's credentials still read (<paramref name="readable"/>); where it takes handed commands and
    /// starts again inside a layer of its own (bubblewrap where its probe proved that), an outer layer: the commands'
    /// profile with Claude Code's own places written - those directories and that file, which Claude Code fails every shell
    /// command without writing, and the receipts' directory and its own temporary directory in the launch directory,
    /// sealed but for them - so a command Claude Code ran without its prefix stays inside it, the slots hidden, the machine
    /// read-only, git's code-running files and the tool's state sealed; null where it takes handed commands and starts none
    /// inside (macOS's sandbox), and Claude Code's own process runs outside, or for an execution, which has no launch.
    /// </summary>
    private IsolationProfile? ClaudeCodeOf(IsolationProfile profile, IsolationGround ground, IReadOnlyList<(string place, bool directory)> hidden, IReadOnlyList<string> readable, IIsolationMechanism mechanism, IEqualityComparer<string> comparer)
    {
        var own = hidden.Where(entry => ground.ClaudeCodeDirectories.Contains(entry.place, comparer) || IsolationHidden.IsClaudeFile(entry.place)).ToList();
        var slots = hidden.Except(own).Select(entry => Real(entry.place)).ToHashSet(comparer);
        var apart = profile with { Hidden = [.. profile.Hidden.Where(place => slots.Contains(place.Path))], Reopened = [.. readable] };

        if (!mechanism.TakesHandedCommands)
        {
            return apart;
        }

        if (mechanism.Unnested is not null || ground.LaunchDirectory is not { } launch)
        {
            return null;
        }

        IReadOnlyList<IsolatedWrite> written =
        [
            .. profile.Writes,
            .. own.DistinctBy(entry => entry.place, comparer).Select(entry => new IsolatedWrite(Real(entry.place), entry.directory, Writable: true)),
            .. ClaudeCode.ClaudeShell.PlacesIn(launch).Select(place => new IsolatedWrite(Real(place), Directory: true, Writable: true)),
        ];

        return apart with { Writes = IsolationProfiles.Ordered(written, comparer), Outer = true };
    }

    /// <summary>
    /// The variable that names the user's SSH agent's socket to a child that reaches it, at its real spelling: the name the
    /// user's environment gives it may be a link inside a hidden place - ssh's own directory among them - which the child
    /// cannot follow. None where it reaches no agent.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> AgentSocketVariable(string? socket)
        => socket is null ? [] : [new(SshAgentSocketVariable, socket)];

    /// <summary>
    /// What git writes of a tree that is itself written: its own git directory alone. A main checkout's is written whole,
    /// and an agent's repository's, which is its repository's own common directory, for git makes its lock files directly
    /// in it, which no rule at a path grants one name at a time - but a main checkout's <c>commondir</c>, made naming
    /// itself, and never made where the repository keeps its refs in a reftable, the path held absent instead, and its
    /// <c>worktrees/</c>, hold no write (<see cref="GitPlacesAsync"/>); a linked worktree's is its
    /// directory under the common directory's <c>worktrees/</c>, and the common directory itself - its <c>objects/</c>,
    /// its <c>refs/</c> and its <c>reftable/</c> wherever the repository keeps them, its <c>logs/</c> and Git LFS's
    /// <c>lfs/</c> - is read-only to the work, so git add and git fetch fail inside the isolation there, an agent an
    /// earlier version made and a leg pinned to a plain worktree among them. What is left in a git directory it wrote
    /// that no rule holds is looked at once the work has run
    /// (<see cref="IsolationDecision.AfterwardAsync"/>).
    /// </summary>
    private static IEnumerable<string> GitWritten((string Tree, string GitDirectory, string CommonDirectory, bool Reftable) directory)
        => [directory.GitDirectory];

    /// <summary>The variable that names a child's temporary directory where the profile has one of its own; none where the mechanism gives it one.</summary>
    private static IEnumerable<KeyValuePair<string, string>> TempVariable(string? temp)
        => temp is null ? [] : [new(IsolationCaches.TempVariable, temp)];

    /// <summary>
    /// <paramref name="path"/> as its guarded place, at its real spelling: there as it stands; not there and inside a
    /// written place, made first - a directory, or a file of git's, empty but a <see cref="CommonDirectoryFile"/>, made
    /// naming its own directory (<see cref="ItsOwnDirectory"/>), for git refuses an empty one - so no command makes it
    /// inside and has it read or run outside later; held absent instead, made by nobody, where <paramref name="absent"/>
    /// says so, which only a mechanism that holds a path that is not there serves
    /// (<see cref="IIsolationMechanism.HoldsAnAbsentFile"/>); null where it is not there and no command could make it, and
    /// for a state file not there, which the tool reads as meaning something and so is never made: the words policy alone
    /// guards it.
    /// </summary>
    private IsolatedPath? Kept(string path, bool directory, IsolationProfile open, bool stateFile, bool absent)
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

        if (!absent)
        {
            if (directory)
            {
                fileSystem.CreateDirectory(path);
            }
            else
            {
                fileSystem.CreateDirectory(parent);
                fileSystem.WriteAllTextAtomic(path, Path.GetFileName(path) == CommonDirectoryFile ? ItsOwnDirectory : string.Empty);
            }
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

    /// <summary>
    /// Removes a directory of a launch's or an execution's own under the isolation's own - its temporary directory, the
    /// one its set-ups ran from - whatever was left in it; one that cannot be removed is left.
    /// </summary>
    private void Removed(string directory)
    {
        try
        {
            if (fileSystem.DirectoryExists(directory))
            {
                fileSystem.DeleteDirectory(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory left under the isolation's own is harmless, and the next launch or execution makes its own.
        }
    }

    /// <summary>One state path as it stands: a directory, or one of the layout's state files.</summary>
    private static IsolatedPath State(string path) => new(path, Directory: !IsStateFile(path));

    /// <summary>Whether <paramref name="path"/> is one of the layout's state files.</summary>
    private static bool IsStateFile(string path) => HarnessLayout.StateFileNames.Contains(Path.GetFileName(path), StringComparer.Ordinal);
}

/// <summary>git's own places of the trees a launch works over (<see cref="AgentIsolation.GitPlacesAsync"/>), or why git could not say them.</summary>
/// <param name="Places">The places no write reaches.</param>
/// <param name="Directories">Each tree's own git directory and the common directory it shares, and whether that git directory holds a reftable.</param>
/// <param name="Unread">Why git could not say them; null where it did.</param>
public sealed record GitPlaces(IReadOnlyList<IsolatedPath> Places, IReadOnlyList<(string Tree, string GitDirectory, string CommonDirectory, bool Reftable)> Directories, string? Unread)
{
    /// <summary>Every file git's configuration names to include, whether its condition holds now or not, there or not.</summary>
    public IReadOnlyList<string> Includes { get; init; } = [];

    /// <summary>What would let git's places be read, where they could not be for a reason of their own; null for the asker's.</summary>
    public string? Remedy { get; init; }

    /// <summary>git could not say them, and why.</summary>
    /// <param name="why">Why.</param>
    /// <param name="remedy">What would let them be read, where it is not the asker's own.</param>
    public static GitPlaces Not(string why, string? remedy = null) => new([], [], why) { Remedy = remedy };
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

        return Ordered(
            [
                .. sealedPlaces.Select(place => new IsolatedWrite(place.Path, place.Directory, Writable: false)),
                .. writableRoots.Select(root => new IsolatedWrite(root, Directory: true, Writable: true)),
            ],
            comparer);
    }

    /// <summary>
    /// <paramref name="writes"/>, each place once, parent first: the places written by nobody before the places written,
    /// a place both standing sealed, then every place's rule laid over the rule of every place above it.
    /// </summary>
    /// <param name="writes">The write rules, a file written alone among them.</param>
    /// <param name="comparer">How this machine compares paths.</param>
    public static IReadOnlyList<IsolatedWrite> Ordered(IReadOnlyList<IsolatedWrite> writes, IEqualityComparer<string> comparer)
    {
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(comparer);

        var sealedSet = writes.Where(write => !write.Writable).Select(write => write.Path).ToHashSet(comparer);

        return
        [
            .. writes.Where(write => !write.Writable).DistinctBy(write => write.Path, comparer)
                .Concat(writes.Where(write => write.Writable && !sealedSet.Contains(write.Path)).DistinctBy(write => write.Path, comparer))
                .Select((write, at) => (write, at))
                .OrderBy(pair => IsolationProfile.Depth(pair.write.Path))
                .ThenBy(pair => pair.at)
                .Select(pair => pair.write),
        ];
    }
}
