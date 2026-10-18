using System.Runtime.ExceptionServices;
using System.Text.Json;
using DssHarness.Core.Anchors;
using DssHarness.Core.Application;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Orchestration;

/// <summary>Creating, seeding, refreshing, rebasing, folding and deleting an orchestrator's agents.</summary>
public interface IAgentService : IUseCase
{
    /// <summary>
    /// Creates an agent of an orchestrator: its record, its worktree below the directory named for the orchestrator,
    /// and its seed - the main tree's uncommitted state handed to it, each changed file copied into the worktree and each
    /// deletion made there, every path's digest recorded - unless <paramref name="empty"/>. Refused past the orchestrator's
    /// limit of open agents. Run again for an agent that exists, it records only the session.
    /// </summary>
    Task<CommandOutcome> CreateAsync(string startDirectory, string orchestrator, string agent, string model, bool empty, string? session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds a live agent again with the main tree's uncommitted state, and every path the main tree holds otherwise than
    /// the agent shares it, committed or not: refused where its worktree holds changes of its own, unless
    /// <paramref name="force"/>; <paramref name="empty"/> hands it nothing more, keeping what it was handed before.
    /// </summary>
    Task<CommandOutcome> SeedAsync(string startDirectory, string orchestrator, string agent, bool empty, bool force, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands a live agent every path <paramref name="request"/> weighs - under the paths it names, the anchor registries'
    /// directory where it names none, or anywhere in the tree - that the main tree holds otherwise than the agent shares it,
    /// committed or not, and records them as handed to it, so its fold leaves them out; refused, copying nothing, where the
    /// agent changed or deleted one of them that the request does not leave as it is, by name. A dry run until
    /// <paramref name="apply"/>, which names who holds what it needs. <paramref name="json"/> answers with one document.
    /// </summary>
    Task<CommandOutcome> RefreshAsync(string startDirectory, string orchestrator, string agent, RefreshRequest request, bool apply, CancellationToken cancellationToken = default, bool json = false);

    /// <summary>
    /// Moves a live agent's base to the main tree's HEAD: what the main tree committed since comes into its worktree as git
    /// holds it, and its own changes and what it was handed stay; refused, moving nothing, where it changed a path the main
    /// tree committed a change to since, unless <paramref name="request"/> names that path - to be merged, where a
    /// three-way merge of the two is clean, or as reconciled by hand. A dry run until <paramref name="apply"/>, which names
    /// who holds what it needs; run again after one that stopped part way, it finishes it. <paramref name="json"/> answers
    /// with one document.
    /// </summary>
    Task<CommandOutcome> RebaseAsync(string startDirectory, string orchestrator, string agent, RebaseRequest request, bool apply, CancellationToken cancellationToken = default, bool json = false);

    /// <summary>
    /// Folds a live agent's work into the main tree, then applies the anchor rows it declared anew: every path it changed
    /// or shares with the main tree, and every row, is measured first, and nothing is written while any is refused. A dry
    /// run until <paramref name="apply"/>, which names who holds what it needs. Its worktree is never removed, and what
    /// the fold wrote is recorded as shared: a review can still send it back. <paramref name="allowances"/> is what the
    /// person folding it lets through that the fold otherwise refuses. <paramref name="json"/> answers with one document.
    /// </summary>
    Task<CommandOutcome> FoldAsync(string startDirectory, string orchestrator, string agent, FoldAllowances allowances, bool apply, CancellationToken cancellationToken = default, bool json = false);

    /// <summary>
    /// Deletes an agent: folds what is left of its work and applies its rows - or, where
    /// <paramref name="discardUncommitted"/>, abandons it with nothing folded - keeps its Claude transcripts in its
    /// orchestrator's logs directory and its evidence in its own directory, each proved, closes it, and removes its
    /// worktree and the copies hosts keep of it, never forced; its directory stays as its history. Run again for a closed
    /// agent, it folds nothing and finishes the removal. A dry run until <paramref name="apply"/>, which names who holds
    /// what it needs, a copy of the worktree among it. <paramref name="allowances"/> is what the person deleting it lets
    /// its fold through that it otherwise refuses. <paramref name="json"/> answers with one document.
    /// </summary>
    Task<CommandOutcome> DeleteAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        FoldAllowances allowances,
        bool apply,
        bool discardUncommitted,
        CancellationToken cancellationToken = default,
        bool json = false);

    /// <summary>
    /// Creates an agent, as the request says: the answer carries what its making knows beyond its lines, where its
    /// worktree is and what its seed recorded, as <see cref="AgentCreated"/> facts.
    /// </summary>
    Task<UseCaseResult<AgentCreated>> CreateAsync(AgentCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds a live agent again, as the request says: the answer carries what it was handed, as
    /// <see cref="AgentSeeded"/> facts.
    /// </summary>
    Task<UseCaseResult<AgentSeeded>> SeedAsync(AgentSeedRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands a live agent what the request weighs, as the request says: the answer carries
    /// <see cref="AgentVerbFacts"/>, whatever form it is asked in.
    /// </summary>
    Task<UseCaseResult<AgentVerbFacts>> RefreshAsync(RefreshAgentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a live agent's base, as the request says: the answer carries <see cref="AgentVerbFacts"/>, whatever form
    /// it is asked in.
    /// </summary>
    Task<UseCaseResult<AgentVerbFacts>> RebaseAsync(RebaseAgentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds a live agent's work into the main tree, as the request says: the answer carries
    /// <see cref="AgentVerbFacts"/>, whatever form it is asked in - what the fold wrote among them, where it wrote.
    /// </summary>
    Task<UseCaseResult<AgentVerbFacts>> FoldAsync(FoldAgentRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an agent, as the request says: the answer carries <see cref="AgentVerbFacts"/>, whatever form it is
    /// asked in - what its last fold wrote among them, where it wrote one.
    /// </summary>
    Task<UseCaseResult<AgentVerbFacts>> DeleteAsync(DeleteAgentRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAgentService"/>
/// <remarks>
/// Once a command has written anything - the main tree, an agent's worktree, or an agent's record - it is no longer
/// interrupted, and whatever it meets from there on is answered as a change begun and not finished
/// (<see cref="HarnessExit.Incomplete"/>), saying what is in and how running it again goes on: never as a refusal, which
/// says nothing changed. Each run that reaches an agent's record, or makes one, is a line in its log, refusals included;
/// a dry run is not.
/// </remarks>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="gitClient">Reads both trees' state from git.</param>
/// <param name="fileSystem">Reads and writes both trees and the records.</param>
/// <param name="platform">How paths compare, and whether files have an execute bit.</param>
/// <param name="output">Where the worktree inspection says what it saw.</param>
/// <param name="permissions">Reads a file's execute bit.</param>
/// <param name="worktrees">Makes and removes agents' worktrees.</param>
/// <param name="hostCopies">Removes the copies hosts keep of an agent's worktree, and says which runs hold one.</param>
/// <param name="runLock">Holds the main tree, and an agent's worktree, while they are written.</param>
/// <param name="treeHolders">Says who holds what a dry run's --apply takes, and where each holder's leg stands.</param>
/// <param name="anchorLocator">Where the anchor registries are, which refresh-agent refreshes by default.</param>
/// <param name="anchors">Applies an agent's rows.</param>
/// <param name="sync">Says what a sync of a tree withholds, which the arm registry a command merges into it is judged by.</param>
/// <param name="transcripts">Finds and keeps an agent's Claude transcripts.</param>
/// <param name="store">The records.</param>
/// <param name="log">The logs.</param>
/// <param name="clock">When things happen.</param>
/// <param name="processContext">This process's current directory, read per call: where a verb was typed, when none is named.</param>
public sealed class AgentService(
    IHarnessContextLoader contextLoader,
    IGitClient gitClient,
    IFileSystem fileSystem,
    IHostPlatform platform,
    IHarnessOutput output,
    IFilePermissions permissions,
    IWorktreeService worktrees,
    IHostCopyRemover hostCopies,
    RunLock runLock,
    TreeHolders treeHolders,
    IAnchorRegistryLocator anchorLocator,
    IAnchorRegistryService anchors,
    ISyncService sync,
    ClaudeTranscripts transcripts,
    OrchestrationStore store,
    OrchestrationLog log,
    TimeProvider clock,
    IProcessContext processContext) : IAgentService
{
    /// <summary>The command that creates an agent.</summary>
    public const string CreateCommand = "create-agent";

    /// <summary>The command that seeds one again.</summary>
    public const string SeedCommand = "seed-agent";

    /// <summary>The command that refreshes one.</summary>
    public const string RefreshCommand = "refresh-agent";

    /// <summary>The command that moves one's base to the main tree's HEAD.</summary>
    public const string RebaseCommand = "rebase-agent";

    /// <summary>The command that folds one.</summary>
    public const string FoldCommand = "fold-agent";

    /// <summary>The command that deletes one.</summary>
    public const string DeleteCommand = "delete-agent";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;
    private readonly IFilePermissions _permissions = permissions;
    private readonly IWorktreeService _worktrees = worktrees;
    private readonly IHostCopyRemover _hostCopies = hostCopies;
    private readonly RunLock _runLock = runLock;
    private readonly TreeHolders _treeHolders = treeHolders;
    private readonly IAnchorRegistryLocator _anchorLocator = anchorLocator;
    private readonly IAnchorRegistryService _anchors = anchors;
    private readonly ISyncService _sync = sync;
    private readonly ClaudeTranscripts _transcripts = transcripts;
    private readonly OrchestrationStore _store = store;
    private readonly OrchestrationLog _log = log;
    private readonly TimeProvider _clock = clock;
    private readonly IProcessContext _processContext = processContext;
    private readonly AgentFold _fold = new(gitClient, fileSystem, permissions, platform);
    private readonly AgentEvidence _evidence = new(fileSystem, platform);

    public Task<CommandOutcome> CreateAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        string model,
        bool empty,
        string? session,
        CancellationToken cancellationToken = default)
        => CreateAsync(startDirectory, orchestrator, agent, model, empty, session, noWorktree: false, cancellationToken);

    /// <summary>
    /// Creates an agent, of either kind: with a worktree of its own, or - where <paramref name="noWorktree"/> is set -
    /// reading the main tree, which the engine makes for a read-only reason step and nothing else does.
    /// </summary>
    private async Task<CommandOutcome> CreateAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        string model,
        bool empty,
        string? session,
        bool noWorktree,
        CancellationToken cancellationToken)
    {
        if (noWorktree && empty)
        {
            return CommandOutcome.Usage(
                "--empty hands an agent's worktree nothing of the main tree's uncommitted state, and an agent made with "
                + "--no-worktree has no worktree to hand anything to. Give one or the other. Nothing was created.");
        }

        if ((Shape(orchestrator, agent) ?? OrchestrationRules.ModelProblem(model) ?? OrchestrationRules.SessionProblem(session)) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var (context, layout, _) = await OrchestratorAsync(startDirectory, orchestrator, cancellationToken).ConfigureAwait(false);

        if (!WorktreeName.Validate(agent, context.Config.Worktrees.MaxNameLength).TryGetName(out _, out var length))
        {
            return CommandOutcome.Usage(length);
        }

        if (_store.ReadAgent(layout, agent) is { } existing)
        {
            return Again(layout, existing, model, empty, session, noWorktree);
        }

        var main = context.Layout.MainCheckoutRoot;
        var address = WorktreeAddress.Nested(orchestrator, agent);

        // Nothing of git is asked for an agent with no worktree: it counts against no limit, and no worktree is made.
        IReadOnlyList<WorktreeListing> listed = noWorktree
            ? []
            : await _worktrees.ListAsync(main, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();

        // Counted and taken in one step, under the orchestrator's own lock, from its record as it is then: two agents made
        // together can never both take its last place, and an orchestrator deleted meanwhile is never given an agent.
        var refused = _store.Exclusively(layout, () =>
        {
            if (_store.ReadOrchestrator(layout) is not { } current)
            {
                return CommandOutcome.Refused(OrchestrationReports.NoOrchestrator(orchestrator));
            }

            if (_fileSystem.DirectoryExists(layout.AgentDirectory(agent)))
            {
                return CommandOutcome.Refused($"'{layout.AgentDirectory(agent)}' is there already, and no record in it names an agent. Nothing was created.");
            }

            if (noWorktree)
            {
                // One step under the lock, and the agent is made: its two directories - which are all an agent gets
                // outside a worktree, and nothing a re-run refuses, being not its own directory - and then its record,
                // the one write. Nothing else is made: no worktree, no hold, nothing handed over, and it counts
                // against no limit, the limit being a limit on worktrees.
                _fileSystem.CreateDirectory(layout.WorkDirectory(agent));
                _fileSystem.CreateDirectory(layout.PlansDirectory(agent));

                _store.WriteAgent(layout, new AgentRecord
                {
                    Kind = AgentRecord.KindName,
                    Name = agent,
                    Orchestrator = orchestrator,
                    Model = model,
                    CreatedAt = now,
                    Access = AgentRecord.AccessRead,
                    Session = session,
                    State = AgentStates.Live,
                });

                return null;
            }

            var open = OrchestrationRules.OpenAgents(orchestrator, _store.Agents(layout), listed.Select(worktree => worktree.Name));

            if (open.Count >= current.Parallel)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{orchestrator}' already has {ReportText.Counted(open.Count, "open agent")}, its limit: {string.Join(", ", open)}. "
                    + $"Delete one with {OrchestrationReports.DeleteAgentLine(orchestrator, "<agent>")}, or raise the limit with "
                    + $"{OrchestrationReports.Line(OrchestratorService.CreateCommand, orchestrator, "--model", current.Model, "--parallel", "<n>")}. Nothing was created.");
            }

            _store.WriteAgent(layout, new AgentRecord
            {
                Kind = AgentRecord.KindName,
                Name = agent,
                Orchestrator = orchestrator,
                Model = model,
                CreatedAt = now,
                Worktree = address.Name,
                WorktreesRoot = PathPatterns.Normalize(context.Config.Worktrees.Root),
                Session = session,
                State = AgentStates.Live,
            });

            return null;
        });

        if (refused is not null)
        {
            return _store.ReadOrchestrator(layout) is null ? refused : _log.Record(layout, orchestrator, CreateCommand, refused);
        }

        if (noWorktree)
        {
            // Its record and its two directories are written; the log line is the last of it.
            return _log.Record(layout, agent, CreateCommand, CommandOutcome.Ok(
                $"Agent '{agent}' of '{orchestrator}' was created with no worktree: it reads the main tree, and writes only '{layout.WorkDirectory(agent)}'",
                [$"record {layout.AgentRecordFile(agent)}", layout.WorkDirectory(agent), layout.PlansDirectory(agent)]));
        }

        // Until its worktree is made, the agent's directory holds only its place, which every way out before then gives
        // back - a refusal, an interruption, or a hand-over refused - and none after, when the worktree is there.
        Hold hold;
        Handable handable;

        try
        {
            // Seeding reads the main tree, which a fold writes: held while the worktree is made and handed what it is
            // handed, so the seed is one moment of it.
            hold = await HoldAsync(context.Layout, CreateCommand, met: null, cancellationToken, main).ConfigureAwait(false);

            if (hold.Refusal is { } held)
            {
                return _log.Record(layout, orchestrator, CreateCommand, GiveBack(layout, agent, held));
            }

            IReadOnlyList<SubmodulePath> inSubmodules;

            try
            {
                // What it is to be handed is checked before its worktree is made: a hand-over refused leaves nothing behind -
                // one into a submodule the worktree made from the main tree's HEAD would record among it.
                handable = empty ? Handable.None : await _fold.HandableAsync(main, Floor(context), within: null, cancellationToken).ConfigureAwait(false);
                inSubmodules = await _fold.InSubmodulesAsync(main, [.. handable.Deletions, .. handable.Files], "HEAD", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await hold.Handles.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            if (inSubmodules.Count > 0)
            {
                await hold.Handles.DisposeAsync().ConfigureAwait(false);

                return _log.Record(
                    layout,
                    orchestrator,
                    CreateCommand,
                    GiveBack(layout, agent, InSubmodules("Creating", agent, orchestrator, inSubmodules, "commit them in the main tree first, or create it with --empty", "Nothing was created.")));
            }
        }
        catch
        {
            if (GiveBack(layout, agent) is { } left)
            {
                _output.Warn(CreateCommand, left);
            }

            throw;
        }

        await using (hold.Handles)
        {
            // Never interrupted from here: git killed part way through making a worktree leaves one half made, still
            // registered, and never cleaned up after.
            var created = await _worktrees.CreateAtAsync(main, address, CancellationToken.None).ConfigureAwait(false);

            if (!created.Succeeded)
            {
                return _log.Record(layout, orchestrator, CreateCommand, GiveBack(layout, agent, created.Outcome with { Message = $"{created.Outcome.Message.TrimEnd('.')}. No agent was created." }));
            }

            return _log.Record(layout, agent, CreateCommand, await FinishMakingAsync(context, layout, agent, created, handable, empty, session).ConfigureAwait(false));
        }
    }

    /// <summary>Gives back the place taken for an agent whose worktree was never made; why it could not be, beside <paramref name="outcome"/>.</summary>
    private CommandOutcome GiveBack(OrchestratorLayout layout, string agent, CommandOutcome outcome)
        => GiveBack(layout, agent) is { } left ? outcome with { Details = [.. outcome.Details ?? [], left] } : outcome;

    /// <summary>Gives back the place taken for an agent whose worktree was never made; why it could not be, or null.</summary>
    private string? GiveBack(OrchestratorLayout layout, string agent)
    {
        try
        {
            _fileSystem.DeleteDirectory(layout.AgentDirectory(agent));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the place taken for agent '{agent}', '{layout.AgentDirectory(agent)}', could not be given back: {ex.Message.TrimEnd('.')}. Delete it by hand.";
        }
    }

    /// <summary>
    /// What makes an agent whose worktree now exists whole: its base, its directories and its seed - never interrupted, and
    /// never stopping part way without saying what it made, where its records are, and how to finish.
    /// </summary>
    private async Task<CommandOutcome> FinishMakingAsync(
        HarnessContext context,
        OrchestratorLayout layout,
        string agent,
        WorktreeOutcome created,
        Handable handable,
        bool empty,
        string? session)
    {
        var orchestrator = layout.Name;
        var what = $"Agent '{agent}' of '{orchestrator}' was created at '{created.Path}'";
        var based = false;

        // Each exit names its records as they stand then: the seed and the bytes kept beside it where they were written.
        string[] Named() => [$"record {layout.AgentRecordFile(agent)}", .. SeedRecords(layout, agent)];

        try
        {
            if (created.BaseCommit is not { } baseCommit)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"{what}, and the commit its worktree was made from cannot be read, so nothing of it can be measured. "
                    + $"{OrchestrationReports.DeleteAgentLine(orchestrator, agent, "--apply --discard-uncommitted")} drops it.",
                    Named());
            }

            _store.UpdateAgent(layout, agent, current => current with { Base = baseCommit });
            based = true;
            _fileSystem.CreateDirectory(layout.WorkDirectory(agent));
            _fileSystem.CreateDirectory(layout.PlansDirectory(agent));
            _fileSystem.CreateDirectory(layout.RowsDirectory(agent));

            var ledger = Ledger(layout, agent, new SeedRecord { SeededAt = _clock.GetUtcNow(), Empty = empty, Paths = [] });
            var (handed, stopped) = await _fold.HandAsync(context.Layout.MainCheckoutRoot, created.Path, handable, ledger, held: null, CancellationToken.None).ConfigureAwait(false);
            var (seed, notes) = WriteSeed(layout, agent, ledger);

            if (stopped is not null)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"{what}, and handing it the main tree's uncommitted state stopped after {handed} of {ReportText.Counted(handable.Count, "path")}: {stopped}. "
                    + $"Its seed records the {handed} it was handed. {Remedy(orchestrator, agent, based)}",
                    [.. notes, .. Named()]);
            }

            return CommandOutcome.Ok(
                $"created agent '{agent}' of orchestrator '{orchestrator}'",
                [
                    created.Path,
                    $"base {OrchestrationReports.Base(baseCommit)}",
                    seed.Empty ? "handed nothing, as asked" : $"handed {ReportText.Counted(handed, "path")} of the main tree's uncommitted state",
                    .. NotHanded(handable),
                    .. notes,
                    .. Named(),
                    $"plans {layout.PlansDirectory(agent)}",
                    $"work {layout.WorkDirectory(agent)}",
                    $"rows {layout.RowsDirectory(agent)}",
                ])
                with
            {
                // What the making knows beyond its lines rides it: where the worktree is, what it was made from and handed.
                Facts = new AgentCreated(orchestrator, agent, created.Path, baseCommit, empty, session),
            };
        }
        catch (Exception ex) when (Unfinished(ex))
        {
            return CommandOutcome.Failed(
                HarnessExit.Incomplete,
                $"{what}, and making it did not finish: {ex.Message.TrimEnd('.')}. {Remedy(orchestrator, agent, based)}",
                Named());
        }
    }

    /// <summary>How an agent whose making stopped part way is finished, or dropped: seeding needs the base its worktree records.</summary>
    private static string Remedy(string orchestrator, string agent, bool based)
        => based
            ? $"{OrchestrationReports.Line(SeedCommand, orchestrator, agent, "--force")} finishes it once that is dealt with, and "
                + $"{OrchestrationReports.DeleteAgentLine(orchestrator, agent, "--apply --discard-uncommitted")} drops it."
            : $"{OrchestrationReports.DeleteAgentLine(orchestrator, agent, "--apply --discard-uncommitted")} drops it; nothing else can finish it, "
                + "since its record names no commit its worktree was made from.";

    /// <summary>What running create-agent again does: records the session, and nothing else.</summary>
    private CommandOutcome Again(OrchestratorLayout layout, AgentRecord existing, string model, bool empty, string? session, bool noWorktree)
    {
        if (existing.State == AgentStates.Deleted)
        {
            return CommandOutcome.Refused(
                $"{Agent(existing)} was deleted at {OrchestrationReports.Moment(existing.DeletedAt!.Value)}, and its directory is kept as its history: "
                + "an agent's name is never used twice. Choose another name.");
        }

        // The two kinds of agent are two agents: the option that makes one is never the one that answers for the other.
        if (noWorktree && !existing.Reads)
        {
            return CommandOutcome.Refused($"{Agent(existing)} has a worktree: an agent with no worktree is another agent. Nothing was changed.");
        }

        if (!noWorktree && existing.Reads)
        {
            return CommandOutcome.Refused($"{Agent(existing)} has no worktree: an agent with one is another agent. Nothing was changed.");
        }

        if (!string.Equals(existing.Model, model, StringComparison.Ordinal) || empty)
        {
            return CommandOutcome.Refused(
                $"{Agent(existing)} exists, created for model '{existing.Model}'. Run again, create-agent records only a session (--session); "
                + (existing.Reads
                    ? "Nothing was changed."
                    : $"{OrchestrationReports.Line(SeedCommand, existing.Orchestrator, existing.Name)} seeds it again. Nothing was changed."));
        }

        // An agent whose making stopped part way is said to be one, never "already as asked"; one that reads the main
        // tree has no making that can stop, its record being all there is of it.
        if (!existing.Reads && existing.State == AgentStates.Live && MakingUnfinished(existing, _store.ReadSeed(layout, existing.Name)) is { } unfinished)
        {
            return CommandOutcome.Refused($"{Agent(existing)} exists, and making it did not finish: {unfinished}.");
        }

        if (session is null || session == existing.Session)
        {
            return CommandOutcome.Ok($"{Lower(Agent(existing))} is already as asked", [$"record {layout.AgentRecordFile(existing.Name)}"]);
        }

        _store.UpdateAgent(layout, existing.Name, current => current with { Session = session });

        return _log.Record(layout, existing.Name, CreateCommand, CommandOutcome.Ok(
            $"{Lower(Agent(existing))} now records session {session}",
            [$"record {layout.AgentRecordFile(existing.Name)}"]));
    }

    public async Task<CommandOutcome> SeedAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        bool empty,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (Shape(orchestrator, agent) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var at = await LiveAgentAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        return at.Refusal ?? _log.Record(at.Layout!, agent, SeedCommand, await SeedLiveAsync(at, empty, force, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Seeding a live agent again, once it is found.</summary>
    private async Task<CommandOutcome> SeedLiveAsync(AgentAt at, bool empty, bool force, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (at.Context!, at.Layout!, at.Record!, at.Path!);
        var main = context.Layout.MainCheckoutRoot;
        var previous = _store.ReadSeed(layout, record.Name);
        var unseeded = new SeedRecord { SeededAt = _clock.GetUtcNow(), Empty = empty, Paths = [] };
        Handable handable;
        IReadOnlyDictionary<string, FileIdentity?> holding;

        // What it is handed is weighed against its base: never while a move of that base stands part way, or with its HEAD
        // elsewhere.
        if (record.Base is not null && await OffBaseAsync(main, record, path, cancellationToken).ConfigureAwait(false) is { } off)
        {
            return off.Refusal;
        }

        try
        {
            // Seeding overwrites by path, and an agent never reads again a file it believes it owns: seeded while it works,
            // it would go on over files replaced under it. So a worktree with changes of its own is refused, unless forced;
            // a copy it was handed and left alone is not its own.
            if (!empty && !force)
            {
                var own = await _fold.OwnAsync(path, previous, Floor(context), cancellationToken).ConfigureAwait(false);

                if (own.Count > 0)
                {
                    return CommandOutcome.Refused(
                        $"{Agent(record)} already holds {ReportText.Counted(own.Count, "changed path")} of its own - {ReportText.Listed(own)} - and seeding it now would overwrite "
                        + "them. Seed an agent before it starts; --empty hands it nothing more, and --force seeds it anyway.");
                }
            }

            // Every path is checked before any is copied: the main tree's uncommitted state, and every path it holds otherwise
            // than the agent shares it - committed since its base, or moved since it was handed.
            handable = empty
                ? Handable.None
                : await _fold.MovedAsync(main, record.Base!, previous ?? unseeded, Floor(context), within: null, everyUncommitted: true, cancellationToken).ConfigureAwait(false);

            // Forced or not: what it holds there is either written through, out of its worktree, or stops the hand-over.
            if (_fold.InTheWayOfHanding(path, handable) is { Count: > 0 } inTheWay)
            {
                return InTheWay("Seeding", record, inTheWay);
            }

            if (await _fold.InSubmodulesAsync(path, [.. handable.Deletions, .. handable.Files], commit: null, cancellationToken).ConfigureAwait(false) is { Count: > 0 } inSubmodules)
            {
                return InSubmodules("Seeding", record.Name, record.Orchestrator, inSubmodules, ByRebasing, NothingWritten);
            }

            // Forced or not, what it is written over is what was weighed here: a file its session writes meanwhile stops it.
            holding = await _fold.HoldingAsync(path, [.. handable.Deletions, .. handable.Files], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandOutcome.Failed(HarnessExit.CommandFailed, $"{Agent(record)} was not seeded: what it or the main tree holds cannot be read - {ex.Message.TrimEnd('.')}. Nothing was written.");
        }

        // The agent's worktree is held too: a leg building in it would be building what is being replaced.
        var hold = await HoldAsync(context.Layout, SeedCommand, met: null, cancellationToken, main, path).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            // What it was handed before stays recorded until it is handed again; handed nothing more, it keeps all of it.
            var start = (previous ?? unseeded) with
            {
                SeededAt = _clock.GetUtcNow(),
                Empty = empty && previous?.Weighed.Any() != true,
            };
            int handed;
            string? stopped;
            IReadOnlyList<string> notes;

            try
            {
                var ledger = Ledger(layout, record.Name, start);
                (handed, stopped) = await _fold.HandAsync(main, path, handable, ledger, holding, CancellationToken.None).ConfigureAwait(false);
                (_, notes) = WriteSeed(layout, record.Name, ledger);
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Seeding {Lower(Agent(record))} copied what it could and could not record it: {ex.Message.TrimEnd('.')}. Its worktree may hold "
                    + $"copies its seed does not name; run {OrchestrationReports.Line(SeedCommand, record.Orchestrator, record.Name, "--force")} again once that is dealt with.",
                    SeedRecords(layout, record.Name));
            }

            if (stopped is not null)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Seeding {Lower(Agent(record))} stopped after {handed} of {ReportText.Counted(handable.Count, "path")}: {stopped}. Its seed records what it was handed; "
                    + $"run {OrchestrationReports.Line(SeedCommand, record.Orchestrator, record.Name, "--force")} again once that is dealt with.",
                    [.. notes, .. SeedRecords(layout, record.Name)]);
            }

            if (empty)
            {
                return CommandOutcome.Ok(
                    previous is { } before && before.Weighed.Any()
                        ? $"seeded {Lower(Agent(record))} with nothing more, as asked: what it was handed before stays recorded"
                        : $"seeded {Lower(Agent(record))} with nothing, as asked",
                    [.. await BehindAsync(main, record).ConfigureAwait(false), .. notes, .. SeedRecords(layout, record.Name)])
                    with { Facts = new AgentSeeded(record.Orchestrator, record.Name, Empty: true, Handed: 0) };
            }

            return CommandOutcome.Ok(
                $"seeded {Lower(Agent(record))} with {ReportText.Counted(handed, "path")}",
                [.. NotHanded(handable), .. await BehindAsync(main, record).ConfigureAwait(false), .. notes, .. SeedRecords(layout, record.Name)])
                with { Facts = new AgentSeeded(record.Orchestrator, record.Name, Empty: false, Handed: handed) };
        }
    }

    /// <summary>
    /// Said where the agent's base is not the main tree's HEAD: what the main tree committed since reaches it only as copies
    /// it is handed, until rebase-agent moves its base there. Never a failure where it cannot be told, since what it reports
    /// on is written.
    /// </summary>
    private async Task<IEnumerable<string>> BehindAsync(string main, AgentRecord record)
    {
        try
        {
            var head = await _gitClient.ResolveCommitAsync(main, "HEAD", CancellationToken.None).ConfigureAwait(false);

            return head is null || head == record.Base
                ? []
                : [$"its base {OrchestrationReports.Base(record.Base)} is not the main tree's HEAD {OrchestrationReports.Base(head)}: what the main tree "
                    + $"committed since reaches it as copies it is handed; {OrchestrationReports.Line(RebaseCommand, record.Orchestrator, record.Name, "--apply")} "
                    + "moves its base there"];
        }
        catch (HarnessException ex)
        {
            return [$"whether the main tree's HEAD is still its base cannot be told: {ex.Message.TrimEnd('.')}"];
        }
    }

    public async Task<CommandOutcome> RefreshAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        RefreshRequest request,
        bool apply,
        CancellationToken cancellationToken = default,
        bool json = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        if ((Shape(orchestrator, agent) ?? request.Problem()) is { } shape)
        {
            return Answered(CommandOutcome.Usage(shape), json);
        }

        var at = await LiveAgentAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (at.Refusal is { } refusal)
        {
            return Answered(refusal, json);
        }

        var met = new HoldersMet();
        var outcome = await RefreshLiveAsync(at, request, apply, json, met, cancellationToken).ConfigureAwait(false);
        var answered = apply ? _log.Record(at.Layout!, agent, RefreshCommand, outcome) : outcome;

        // Asked for one document, what the live agent answered brings one: the dry run's own exits bring theirs, and the
        // rest - an --apply's, and a refusal met there before anything was weighed - say what the code alone says, save an
        // --apply refused at its hold, which names the run it met. A usage error or a refusal returned above, before the
        // agent was found, brings the same document, empty, as the command's plumbing answers every exit that brings
        // none - and its facts, which say as much.
        return Answered(answered, apply, json, met);
    }

    /// <summary>Refreshing a live agent, once it is found.</summary>
    private async Task<CommandOutcome> RefreshLiveAsync(AgentAt at, RefreshRequest request, bool apply, bool json, HoldersMet met, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (at.Context!, at.Layout!, at.Record!, at.Path!);
        var floor = Floor(context);
        var main = context.Layout.MainCheckoutRoot;

        // What its plan names, once it is measured: carried by every answer after, its dry run's and its --apply's.
        AgentPlanned? planned = null;

        // Who holds what this command's --apply takes, named on every exit a dry run makes, refused or not: the refusal a
        // dry run is refused with, and the failure it meets, keep their own words and gain the holders' lines.
        CommandOutcome RefusedInDry(CommandOutcome refused)
            => !apply ? Dry(json, context.Layout, RefreshCommand, refused, RequestStanding.Refused, new Typed([], [], planned), main, path) : refused;

        if (_store.ReadSeed(layout, record.Name) is not { } seed)
        {
            return RefusedInDry(CommandOutcome.Refused($"{Agent(record)} is not whole - making it stopped part way: {MakingUnfinished(record, seed: null)}."));
        }

        // What it is handed is weighed against its base: never while a move of that base stands part way, or with its HEAD
        // elsewhere.
        if (await OffBaseAsync(main, record, path, cancellationToken).ConfigureAwait(false) is { } off)
        {
            return RefusedInDry(off.Refusal);
        }

        // Every path the main tree moved, off the floor, where all are asked for: no prefix names them.
        IReadOnlyList<string>? prefixes = request.All
            ? null
            : request.Paths.Count > 0 ? [.. request.Paths.Select(PathPatterns.Normalize)] : await RegistryDirectoriesAsync(context, cancellationToken).ConfigureAwait(false);

        if (prefixes?.FirstOrDefault(prefix => TreeFloor.Covers(floor, prefix)) is { } floored)
        {
            return RefusedInDry(CommandOutcome.Usage($"'{floored}' is never handed to an agent: {string.Join(", ", floor)} stay in their own tree."));
        }

        var under = prefixes is null ? string.Empty : $" under {string.Join(", ", prefixes)}";
        var except = request.Except.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal);
        var stale = request.Stale.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal);
        var policy = TwoChangePolicy.Of(context, request.Merge);

        if (policy.NamedNeverMerged().ToList() is { Count: > 0 } never)
        {
            return RefusedInDry(CommandOutcome.Refused($"{string.Join("; ", never.Select(named => named.Why))}. Nothing was copied."));
        }

        RefreshPlan plan;

        try
        {
            plan = await _fold.MeasureRefreshAsync(
                main,
                path,
                record.Base!,
                seed,
                floor,
                prefixes,
                except,
                stale,
                policy,
                Changes(context, layout, record.Name, keeping: apply),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return !apply
                ? Dry(json, context.Layout, RefreshCommand, CommandOutcome.Failed(HarnessExit.CommandFailed, $"{Agent(record)} was not refreshed: what it or the main tree holds cannot be read - {ex.Message.TrimEnd('.')}. Nothing was copied."), null, main, path)
                : CommandOutcome.Failed(HarnessExit.CommandFailed, $"{Agent(record)} was not refreshed: what it or the main tree holds cannot be read - {ex.Message.TrimEnd('.')}. Nothing was copied.");
        }

        planned = met.Planned = PlannedOf(plan, policy);

        var (moved, left, changed, inTheWay, alike) = (plan.Handed, plan.Left, plan.Changed, plan.InTheWay, plan.Alike);

        // Never silent, and never a guess: a name that leaves nothing out is the typo it usually is.
        if (except.Where(relative => !left.Contains(relative, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } stray)
        {
            return RefusedInDry(CommandOutcome.Refused(
                $"{RefreshRequest.ExceptOption} {ReportText.Listed([.. stray.Select(relative => $"'{relative}'")])} names no path to refresh{under} that "
                + $"{Lower(Agent(record))} changed, so it leaves nothing out: check its spelling. Nothing was copied."));
        }

        if (plan.StaleStray.Count > 0)
        {
            return RefusedInDry(CommandOutcome.Refused(
                $"{RefreshRequest.StaleOption} {ReportText.Listed([.. plan.StaleStray.Select(relative => $"'{relative}'")])} names no path{under} whose copy in "
                + "the agent is not the main tree's, so it hands nothing: check its spelling. Nothing was copied."));
        }

        if (plan.MergeStray.Count > 0)
        {
            return RefusedInDry(CommandOutcome.Refused(
                $"{FoldAllowances.MergeOption} {ReportText.Listed([.. plan.MergeStray.Select(relative => $"'{relative}'")])} names no path{under} the refresh "
                + "weighs, so it merges nothing: check its spelling. Nothing was copied."));
        }

        // Where the search stopped short of the main tree's whole history, a copy older than it reached is not found stale.
        IReadOnlyList<string> bound = plan.Capped
            ? [$"The last {ReportText.Counted(TwoChanges.MostCommits, "commit")} of the main tree that changed these paths were searched, and its history goes "
                + "further back: a copy older than that is not found stale."]
            : [];

        if (plan.StaleRefused.Count > 0)
        {
            return plan.StaleRefused is [var one]
                ? RefusedInDry(CommandOutcome.Refused($"{one} Nothing was copied.", bound))
                : RefusedInDry(CommandOutcome.Refused(
                    $"{ReportText.Counted(plan.StaleRefused.Count, "path")} named with {RefreshRequest.StaleOption} are not proved stale, each said below. Nothing was copied.",
                    [.. plan.StaleRefused.Select(why => $"  {why}"), .. bound]));
        }

        if (changed.Count > 0)
        {
            var staleOnes = changed.Where(change => change.StaleAt is not null).Select(change => change.Path).ToList();
            var mergeable = plan.Mergeable;

            // What each option says of a path, said of those this refusal offers: one, or several, none a --force.
            List<string> says = [$"{RefreshRequest.ExceptOption} says the path stays the agent's change, for its fold to weigh against what the main tree holds"];

            if (staleOnes.Count > 0)
            {
                says.Add($"{RefreshRequest.StaleOption} says its copy is not the agent's work, and is taken only where the main tree's history holds those very bytes at that path");
            }

            if (mergeable.Count > 0)
            {
                says.Add($"{FoldAllowances.MergeOption} writes a clean three-way merge into its worktree as its change, and a clean textual merge proves nothing about the program");
            }

            return RefusedInDry(CommandOutcome.Refused(
                $"{Agent(record)} changed {changed.Count} of the path(s) to refresh - {ReportText.Listed([.. changed.Select(change => change.Path)])} - and "
                + "refreshing would undo those changes. Nothing was copied.",
                [
                    .. changed.Select(change => new[] { change.StaleAt is { } commit ? StaleSaid(commit) : null, change.Merge }.OfType<string>().ToList() is { Count: > 0 } said
                        ? $"  {change.Path}   ({string.Join("; ", said)})"
                        : $"  {change.Path}"),
                    .. plan.Shown,
                    $"To hand it every other path and leave these as it changed them, run again with {RefreshRequest.ExceptOption} <path> for each:",
                    $"  {string.Join(' ', OrchestrationReports.PathArguments(RefreshRequest.ExceptOption, left.Concat(changed.Select(change => change.Path)).Order(StringComparer.Ordinal)))}",
                    .. staleOnes.Count == 0
                        ? Array.Empty<string>()
                        : [
                            $"{staleOnes.Count} of them {(staleOnes.Count == 1 ? "holds" : "hold")} bytes the main tree itself held at that path: copies gone stale, where nobody changed them on purpose. "
                                + "To hand it the main tree's copy of each instead, name them:",
                            $"  {string.Join(' ', OrchestrationReports.PathArguments(RefreshRequest.StaleOption, staleOnes))}",
                        ],
                    .. mergeable.Count == 0
                        ? Array.Empty<string>()
                        : [
                            $"{mergeable.Count} of them {(mergeable.Count == 1 ? "merges" : "merge")} clean with the main tree's copy. To write "
                                + $"{(mergeable.Count == 1 ? "the merge" : "each merge")} into its worktree as its change, name {(mergeable.Count == 1 ? "it" : "them")}:",
                            $"  {string.Join(' ', OrchestrationReports.PathArguments(FoldAllowances.MergeOption, mergeable))}",
                        ],
                    says.Count == 1 ? $"{says[0]}; it is not a --force." : $"{string.Join("; ", says)}. None is a --force.",
                    .. bound,
                ]));
        }

        if (inTheWay.Count > 0)
        {
            return RefusedInDry(InTheWay("Refreshing", record, inTheWay));
        }

        if (plan.InSubmodules.Count > 0)
        {
            return RefusedInDry(InSubmodules("Refreshing", record.Name, record.Orchestrator, plan.InSubmodules, ByRebasing, NothingWritten));
        }

        var all = moved.Files.Concat(moved.Deletions).Order(StringComparer.Ordinal).ToList();
        var behind = await BehindAsync(main, record).ConfigureAwait(false);

        // Handed like any other path, and listed apart, each with the commit its copy is the main tree's own at.
        var staleHanded = plan.Stale.Select(copy => copy.Path).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> handedLines = [.. all.Where(relative => !staleHanded.Contains(relative)).Select(relative => $"  {relative}")];
        IReadOnlyList<string> staleLines = plan.Stale.Count == 0
            ? []
            : [
                $"{ReportText.Counted(plan.Stale.Count, "path")} named with {RefreshRequest.StaleOption}, whose copy is the main tree's own at an earlier commit, "
                    + $"{(apply ? "handed" : "to hand")} the main tree's copy:",
                .. plan.Stale.Select(copy => $"  {copy.Path}   ({StaleSaid(copy.Commit)})"),
            ];

        // Never silent, and never taken for more than it is: git merged the text, and nobody has built it.
        var merges = plan.Merged.Select(merge => merge.Merge).ToList();
        IReadOnlyList<string> mergedLines = merges.Count == 0
            ? []
            : [
                $"{ReportText.Counted(merges.Count, "path")} both trees changed, merged three-way, {(apply ? "written" : "to write")} into its worktree as its own "
                    + $"change, for its fold to write, and {(apply ? "recorded" : "to record")} as handed the main tree's copy - a clean textual merge proves "
                    + "nothing about the program:",
                .. OrchestrationReports.MergedLines(merges),
            ];

        // Recorded as shared, so its record says what it holds: dropped unrecorded, the next move of the main tree reads
        // its copy as its change. Never silent, and written only with --apply.
        IReadOnlyList<string> alikeLines = alike.Count == 0
            ? []
            : [$"{ReportText.Counted(alike.Count, "path")} it already holds as the main tree does, {(apply ? "recorded" : "to record")} as shared:", .. alike.Select(relative => $"  {relative}")];

        // The line that asks for this refresh again, with everything it was asked: its paths or --all, and each path left
        // as the agent changed it. Said bare, it asked for another refresh - of the registries' directory alone, and
        // refused over the very changes this one was told to leave.
        var again = OrchestrationReports.Line(RefreshCommand, record.Orchestrator, record.Name, request.Arguments(), "--apply");

        // Never silent: a path left out without a word is how a change of the main tree's goes missing while the refresh succeeds.
        IReadOnlyList<string> leftLines = left.Count == 0
            ? []
            : [$"{ReportText.Counted(left.Count, "path")} it changed, named with {RefreshRequest.ExceptOption}, {(apply ? "left" : "to leave")} as it changed them:", .. left.Select(relative => $"  {relative}")];

        var holds = $"{Lower(Agent(record))} holds the main tree's copy of every {(left.Count > 0 ? "other " : string.Empty)}changed path{under}";

        if (all.Count == 0 && alike.Count == 0 && merges.Count == 0)
        {
            return CommandOutcome.Ok(holds, [.. leftLines, .. NotHanded(moved), .. behind]);
        }

        // What a summary says of the merges, beside what is handed; or alone, where nothing else is.
        var merging = $"{ReportText.Counted(merges.Count, "path")} {(apply ? "merged" : "would be merged")} into the worktree of {Lower(Agent(record))} as its own "
            + "change, recorded as handed the main tree's copy";

        if (!apply)
        {
            return Dry(
                json,
                context.Layout,
                RefreshCommand,
                CommandOutcome.Ok(
                    (all.Count, merges.Count) switch
                    {
                        (0, 0) => $"{holds}; {alike.Count} of them {(alike.Count == 1 ? "is" : "are")} not recorded as shared yet: pass --apply to record them",
                        (0, _) => $"dry run: {merging}{(alike.Count == 0 ? string.Empty : $", and {alike.Count} recorded as shared")}; pass --apply to write them",
                        _ => $"dry run: {ReportText.Counted(all.Count, "path")} would be refreshed into {Lower(Agent(record))}"
                            + $"{(alike.Count == 0 ? string.Empty : $", and {alike.Count} recorded as shared")}"
                            + $"{(merges.Count == 0 ? string.Empty : $", and {merges.Count} merged into its worktree as its own change")}; pass --apply to hand them over",
                    },
                    [.. handedLines, .. staleLines, .. mergedLines, .. alikeLines, .. leftLines, .. NotHanded(moved), .. behind]),
                RequestStanding.Sound,
                new Typed([], [], planned),
                main,
                path);
        }

        // The agent's worktree is held too: a leg building in it would be building what is being replaced.
        var hold = await HoldAsync(context.Layout, RefreshCommand, met, cancellationToken, main, path).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            int handed;
            var merged = 0;
            string? stopped;
            IReadOnlyList<string> notes;

            try
            {
                var ledger = Ledger(layout, record.Name, seed);
                await _fold.AlikeAsync(path, plan, ledger).ConfigureAwait(false);
                (handed, stopped) = await _fold.HandAsync(main, path, moved, ledger, plan.Held, CancellationToken.None).ConfigureAwait(false);

                // Each merge once everything else is handed: a merge stopped part way leaves the paths handed recorded.
                if (stopped is null)
                {
                    (merged, stopped) = await _fold.MergeRefreshAsync(main, plan, ledger).ConfigureAwait(false);
                }

                (_, notes) = WriteSeed(layout, record.Name, ledger);
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Refreshing {Lower(Agent(record))} handed over what it could and could not record it: {ex.Message.TrimEnd('.')}. Run "
                    + $"{again} again once that is dealt with.",
                    SeedRecords(layout, record.Name));
            }

            var recorded = alike.Count == 0 ? string.Empty : $", and recorded {alike.Count} it already held as the main tree does";

            return stopped is not null
                ? CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Refreshing {Lower(Agent(record))} stopped after {handed} of {ReportText.Counted(all.Count, "path")}"
                    + $"{(merges.Count == 0 ? string.Empty : $" and merging {merged} of {merges.Count}")}: {stopped}. Its seed records the {handed} handed"
                    + $"{(merges.Count == 0 ? string.Empty : $", the {merged} merged")}"
                    + $"{(alike.Count == 0 ? string.Empty : $" and the {alike.Count} it already held as the main tree does")}; run {again} again once that is dealt with.",
                    [.. notes, .. SeedRecords(layout, record.Name)])
                : CommandOutcome.Ok(
                    (all.Count, merges.Count) switch
                    {
                        (0, 0) => $"{holds}, and recorded {alike.Count} of them as shared",
                        (0, _) => $"{merging}{recorded}",
                        _ => $"refreshed {ReportText.Counted(all.Count, "path")} into {Lower(Agent(record))}, recorded as handed to it, so its fold leaves them out"
                            + $"{recorded}{(merges.Count == 0 ? string.Empty : $", and merged {merges.Count} into its worktree as its own change")}",
                    },
                    [.. handedLines, .. staleLines, .. mergedLines, .. alikeLines, .. leftLines, .. NotHanded(moved), .. behind, .. notes, .. SeedRecords(layout, record.Name)]);
        }
    }

    /// <summary>What a stale copy's line says of it, to stand in brackets: the commit of the main tree's history that holds its very bytes.</summary>
    private static string StaleSaid(string commit) => $"its bytes are the main tree's at {ReportText.Commit(commit)}";

    public async Task<CommandOutcome> RebaseAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        RebaseRequest request,
        bool apply,
        CancellationToken cancellationToken = default,
        bool json = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        if ((Shape(orchestrator, agent) ?? request.Problem()) is { } shape)
        {
            return Answered(CommandOutcome.Usage(shape), json);
        }

        var at = await LiveAgentAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (at.Refusal is { } refusal)
        {
            return Answered(refusal, json);
        }

        var met = new HoldersMet();
        var outcome = await RebaseLiveAsync(at, request, apply, json, met, cancellationToken).ConfigureAwait(false);
        var answered = apply ? _log.Record(at.Layout!, agent, RebaseCommand, outcome) : outcome;

        // Asked for one document, what the live agent answered brings one, and what was returned above brings the same
        // document, empty, with its facts, as refresh-agent says.
        return Answered(answered, apply, json, met);
    }

    /// <summary>Moving a live agent's base, once it is found.</summary>
    private async Task<CommandOutcome> RebaseLiveAsync(AgentAt at, RebaseRequest request, bool apply, bool json, HoldersMet met, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (at.Context!, at.Layout!, at.Record!, at.Path!);
        var main = context.Layout.MainCheckoutRoot;
        var recordLine = $"record {layout.AgentRecordFile(record.Name)}";
        var from = record.Base!;

        // A rebase takes the worktree alone, never the main tree: who holds it is named on every exit a dry run makes.
        CommandOutcome RefusedInDry(CommandOutcome refused)
            => !apply ? Dry(json, context.Layout, RebaseCommand, refused, RequestStanding.Refused, path) : refused;

        if (_store.ReadSeed(layout, record.Name) is not { } seed)
        {
            return RefusedInDry(CommandOutcome.Refused($"{Agent(record)} is not whole - making it stopped part way: {MakingUnfinished(record, seed: null)}."));
        }

        if (await _gitClient.ResolveCommitAsync(main, "HEAD", cancellationToken).ConfigureAwait(false) is not { } head)
        {
            return RefusedInDry(CommandOutcome.Refused($"The main tree has no commit for the base of {Lower(Agent(record))} to move to. Nothing was changed."));
        }

        string to;

        if (await OffBaseAsync(main, record, path, cancellationToken).ConfigureAwait(false) is not { } off)
        {
            if (head == from)
            {
                return CommandOutcome.Ok($"the base of {Lower(Agent(record))} is the main tree's HEAD {OrchestrationReports.Base(head)} already", [recordLine]);
            }

            to = head;
        }
        else if (off.StoppedAt is { } stoppedAt)
        {
            // A move its record says stopped part way: finished where it was going, whatever the main tree did since, and
            // what it wrote then held as the new base holds it.
            to = stoppedAt;
        }
        else
        {
            return RefusedInDry(off.Refusal);
        }

        var finishing = record.Moving is not null;
        var moving = $"from {OrchestrationReports.Base(from)} to {(to == head ? "the main tree's HEAD " : "the main tree's commit ")}{OrchestrationReports.Base(to)}";
        var policy = TwoChangePolicy.Of(context, request.Merge);

        // The line that finishes this move, with every path it was told to merge or keep: said bare, it was refused over
        // the very paths this one was let through. Recorded with the move, so whichever command meets it stopped names it.
        List<string> told = [.. request.Arguments()];
        var again = OrchestrationReports.Line(RebaseCommand, [record.Orchestrator, record.Name, "--apply", .. told]);
        var (plan, stop) = await MeasuredAsync(cancellationToken).ConfigureAwait(false);
        var planned = met.Planned = plan is null ? null : PlannedOf(plan, policy);

        if (stop is not null)
        {
            if (!apply)
            {
                return Dry(json, context.Layout, RebaseCommand, stop, DryRuns.StandingOf(stop), new Typed([], RebaseReasons(plan), planned), path);
            }

            met.Reasons = RebaseReasons(plan);
            return stop;
        }

        if (!apply)
        {
            return Dry(
                json,
                context.Layout,
                RebaseCommand,
                CommandOutcome.Ok($"dry run: the base of {Lower(Agent(record))} would move {moving}; pass --apply to move it", [.. RebaseLines(plan!, applied: false)]),
                RequestStanding.Sound,
                new Typed([], [], planned),
                path);
        }

        // The agent's worktree is held: a leg building in it would be building what is being replaced. Measured again under
        // the hold, so what is written is what was weighed.
        var hold = await HoldAsync(context.Layout, RebaseCommand, met, cancellationToken, path).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            (plan, stop) = await MeasuredAsync(cancellationToken).ConfigureAwait(false);

            // What is written is what was weighed under the hold, and what its answer names.
            met.Planned = plan is null ? null : PlannedOf(plan, policy);

            if (stop is not null)
            {
                met.Reasons = RebaseReasons(plan);
                return stop;
            }

            // Never interrupted from here. Where the move goes is recorded first, then what the new base brings is written,
            // then HEAD and the index move, then the record names the new base: stopped anywhere, its record says where it
            // was going and its worktree stands on one of the two commits, so running it again finishes it there - and every
            // other command refuses it until then, since what was written would read as the agent's work.
            try
            {
                if (!finishing || !told.SequenceEqual(record.MovingWith ?? [], StringComparer.Ordinal))
                {
                    _store.UpdateAgent(layout, record.Name, current => current with { Moving = to, MovingWith = told.Count > 0 ? told : null });
                }

                // What git writes over or removes is what was weighed under the hold: a file the agent's session wrote since
                // stops the move here, before git writes anything.
                await _fold.RequireTakenAsync(path, plan!).ConfigureAwait(false);

                // The old names of files renamed in case alone that the first checkout took out of the agent's index are
                // recorded once its git ends, whether it wrote everything or stopped part way: run again, the move takes
                // such a name for its own removal only where so recorded, git leaving the index and the disk alike after it
                // and after a git rm of the agent's session.
                ExceptionDispatchInfo? stoppedAt = null;

                try
                {
                    await _gitClient.CheckOutAtAsync(path, to, plan!.Written, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (Unfinished(ex))
                {
                    stoppedAt = ExceptionDispatchInfo.Capture(ex);
                }

                if (await _fold.RemovedByTheMoveAsync(path, plan!).ConfigureAwait(false) is { Count: > 0 } removed)
                {
                    _store.UpdateAgent(
                        layout,
                        record.Name,
                        current => current with { MovingRemoved = [.. (current.MovingRemoved ?? []).Union(removed, StringComparer.Ordinal)] });
                }

                stoppedAt?.Throw();
                await _gitClient.CheckOutAtAsync(path, to, plan!.WrittenLast, CancellationToken.None).ConfigureAwait(false);

                // Each merge over the agent's file as it was weighed, before HEAD moves: stopped after, a run again finds the
                // path both changed still, and merges it again to the same bytes.
                foreach (var merge in plan.Merged)
                {
                    await _fold.WriteMergeAsync(merge).ConfigureAwait(false);
                }

                await _gitClient.ResetToAsync(path, to, CancellationToken.None).ConfigureAwait(false);
                _store.UpdateAgent(layout, record.Name, current => current with { Base = to, Moving = null, MovingWith = null, MovingRemoved = null });
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Moving the base of {Lower(Agent(record))} {moving} stopped part way: {ex.Message.TrimEnd('.')}. Its worktree may hold part of what the new "
                    + $"base brings; run {again} again once that is dealt with, and it finishes it.",
                    [recordLine]);
            }

            var details = new List<string>(RebaseLines(plan, applied: true));

            if (await WorktreeService.WriteBaseCommitAsync(_gitClient, main, record.Worktree!, to, CancellationToken.None).ConfigureAwait(false) is { } unrecorded)
            {
                details.Add($"its base is recorded in its record, and could not be in git's record of the worktree: {unrecorded.TrimEnd('.')}");
            }

            if (to != head)
            {
                details.Add(
                    $"the main tree's HEAD is {OrchestrationReports.Base(head)} now: run {OrchestrationReports.Line(RebaseCommand, record.Orchestrator, record.Name, "--apply")} "
                    + "again to move its base there");
            }

            details.Add(recordLine);

            return CommandOutcome.Ok($"moved the base of {Lower(Agent(record))} {moving}", details);
        }

        // What moving it brings in and keeps, or why it cannot move: nothing is written either way.
        async Task<(RebasePlan? Plan, CommandOutcome? Stop)> MeasuredAsync(CancellationToken token)
        {
            RebasePlan measured;

            try
            {
                measured = await _fold.MeasureRebaseAsync(
                        path, from, to, seed, request.Settled, policy, Changes(context, layout, record.Name, keeping: false), finishing, record.MovingRemoved ?? [], token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, CommandOutcome.Failed(
                    HarnessExit.CommandFailed,
                    $"The base of {Lower(Agent(record))} was not moved: what it holds cannot be read - {ex.Message.TrimEnd('.')}. Nothing was changed."));
            }

            // The command that names every path whose merge is clean and that nobody asked for, beside what this one was given.
            var merging = measured.Mergeable.Count == 0
                ? null
                : OrchestrationReports.Line(RebaseCommand, [record.Orchestrator, record.Name, .. (request with { Merge = [.. request.Merge, .. measured.Mergeable] }).Arguments()]);

            return measured.Refusals.Count == 0
                ? (measured, null)
                : (measured, CommandOutcome.Refused(
                    $"The base of {Lower(Agent(record))} cannot move {moving}: {ReportText.Counted(measured.Refusals.Count, "problem")}, and nothing was changed.",
                    [.. RebaseRefusalLines(measured, merging)]));
        }
    }

    /// <summary>What moving an agent's base brings in, keeps and leaves alone, every path the two commits hold differently in exactly one list.</summary>
    private static IEnumerable<string> RebaseLines(RebasePlan plan, bool applied)
    {
        if (plan.Settled.Count > 0)
        {
            // Never silent: a path kept without a word is how a change of the main tree's goes missing while the move succeeds.
            yield return $"{ReportText.Counted(plan.Settled.Count, "path")} it changed, declared settled by hand, {(applied ? "kept" : "to keep")} as its own change on the new base:";

            foreach (var path in plan.Settled)
            {
                yield return $"  {path}";
            }
        }

        if (plan.Merged.Count > 0)
        {
            // Never silent, and never taken for more than it is: git merged the text, and nobody has built it.
            yield return $"{ReportText.Counted(plan.Merged.Count, "path")} it changed that the main tree committed a change to, merged three-way, "
                + $"{(applied ? "written" : "to write")} into its worktree as its own change on the new base - a clean textual merge proves nothing about the program:";

            foreach (var line in OrchestrationReports.MergedLines(plan.Merged))
            {
                yield return line;
            }
        }

        yield return $"{ReportText.Counted(plan.Taken.Count, "path")} the main tree committed since {(applied ? "came" : "come")} into its worktree as git holds them:";

        foreach (var path in plan.Taken)
        {
            yield return $"  {path}";
        }

        if (plan.Kept.Count > 0)
        {
            yield return $"and {ReportText.Counted(plan.Kept.Count, "path")} it shares with the main tree, handed to it or folded, {(applied ? "stayed" : "stay")} as its seed "
                + "records them; refresh-agent hands it any the main tree moved since:";

            foreach (var path in plan.Kept)
            {
                yield return $"  {path}";
            }
        }

        if (plan.AlreadyThere.Count > 0)
        {
            yield return $"and {ReportText.Counted(plan.AlreadyThere.Count, "path")} it holds as the new base does already, with nothing to write:";

            foreach (var path in plan.AlreadyThere)
            {
                yield return $"  {path}";
            }
        }
    }

    /// <summary>
    /// Why an agent's base cannot be moved, each path with its reason and what a three-way merge of it gives, the conflicts
    /// of each that conflicts, and how a path both changed is let through: merged where its merge is clean, or reconciled
    /// by hand.
    /// </summary>
    /// <param name="plan">A move with refusals.</param>
    /// <param name="merging">The command that names every path whose merge is clean and was not asked for, quoted; null where there is none.</param>
    private static IEnumerable<string> RebaseRefusalLines(RebasePlan plan, string? merging)
    {
        foreach (var refusal in plan.Refusals)
        {
            yield return $"  {refusal}";
        }

        foreach (var line in plan.Shown)
        {
            yield return line;
        }

        yield return "A path the agent changed that the main tree committed a change to since is two changes of one file. Where a three-way merge "
            + $"of the two is clean, {FoldAllowances.MergeOption} <path> writes it into the agent's worktree as its own change on the new base: it is "
            + "never taken unasked, and a clean textual merge proves nothing about the program. Otherwise reconcile the agent's copy with what the "
            + $"main tree committed by hand, then run again naming the path with {FoldAllowances.SettledOption} <path>, which keeps its copy as its "
            + "own change on the new base. Neither is a --force.";

        if (merging is not null)
        {
            yield return OrchestrationReports.MergingLine(plan.Mergeable.Count, merging);
        }
    }

    /// <summary>
    /// Why an agent cannot be worked with as it stands - a move of its base that its record says stopped part way, which
    /// rebase-agent alone finishes, or a HEAD that is not its base - or <see langword="null"/> where its HEAD is its base and
    /// no move of it is under way. Every command that weighs or writes its worktree asks this first: what a stopped move
    /// wrote reads as the agent's work, and a HEAD off its base weighs that work against the wrong commit.
    /// </summary>
    private async Task<HeadMoved?> OffBaseAsync(string main, AgentRecord record, string path, CancellationToken cancellationToken)
    {
        var head = await _gitClient.ResolveCommitAsync(path, "HEAD", cancellationToken).ConfigureAwait(false);

        // Stopped before HEAD moved, or after and before the record named the new base: where else HEAD is, a hand moved it.
        if (record.Moving is { } moving && (head == record.Base || head == moving))
        {
            return HeadMoved.Stopped(
                moving,
                CommandOutcome.Refused(
                    $"Moving the base of {Lower(Agent(record))} from {OrchestrationReports.Base(record.Base)} to {OrchestrationReports.Base(moving)} stopped part way, "
                    + "and its worktree may hold part of what the new base brings: "
                    + $"{OrchestrationReports.Line(RebaseCommand, [record.Orchestrator, record.Name, "--apply", .. record.MovingWith ?? []])} finishes it."));
        }

        return head == record.Base ? null : await HeadMovedAsync(main, record, path, head, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Why an agent whose HEAD is neither its base nor, where a move of its base stands stopped part way, where that move
    /// goes cannot be worked with as it stands, and what puts it right - each told from git's history, never guessed where
    /// git cannot say: a HEAD naming no commit; one before its base, moved back by hand; one after it that the main tree's
    /// history does not hold, a commit made inside it; and any other, moved there by hand. Only a move its record names is
    /// ever finished: a HEAD moved by hand is never taken for one, which would move its base to wherever the hand put it.
    /// </summary>
    private async Task<HeadMoved> HeadMovedAsync(string main, AgentRecord record, string path, string? head, CancellationToken cancellationToken)
    {
        var based = OrchestrationReports.Base(record.Base);

        if (head is null)
        {
            return HeadMoved.Refusing(CommandOutcome.Refused(
                $"{Agent(record)}'s HEAD names no commit - a branch with none yet was checked out in it - so its work cannot be weighed against its base {based}: "
                + $"'git -C \"{path}\" reset --soft {record.Base}' puts its HEAD back on its base, its files as they are; then run again."));
        }

        var at = OrchestrationReports.Base(head);
        string? parted;
        bool inMain;

        try
        {
            // Where HEAD and its base part says which comes first; a commit after the base that the main tree's history holds
            // is the main tree's, put there by hand, where one it does not hold was made inside the agent.
            parted = await _gitClient.MergeBaseAsync(main, head, [record.Base!], cancellationToken).ConfigureAwait(false);
            inMain = parted == record.Base && await _gitClient.MergeBaseAsync(main, head, ["HEAD"], cancellationToken).ConfigureAwait(false) == head;
        }
        catch (HarnessException ex)
        {
            return HeadMoved.Refusing(CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"{Agent(record)}'s HEAD is {at}, and its base is {based}, and how its HEAD came off its base - moved by hand, or a commit made inside it - "
                + $"cannot be told: {ex.Message.TrimEnd('.')}. Nothing was changed."));
        }

        var putBack = $"Put it back on its base the way it was moved off it - after a checkout, 'git -C \"{path}\" checkout {record.Base}'; after a reset, "
            + $"'git -C \"{path}\" reset --soft {record.Base}' - and run again.";

        return HeadMoved.Refusing(CommandOutcome.Refused(
            parted == head
                ? $"{Agent(record)}'s HEAD is {at}, a commit before its base {based}: it was moved back by hand, which no move of its base does, so folding or "
                    + $"moving it now would weigh its work against the wrong commit. {putBack}"
                : parted == record.Base && !inMain
                    ? $"{Agent(record)}'s HEAD is {at}, and its base is {based}: a commit made inside it hides its changes from git status, the only list of paths "
                        + "a fold reads, so folding it now would take part of its work and silently drop the rest. Agents never commit; "
                        + $"'git -C \"{path}\" reset --soft {record.Base}' turns its commits back into uncommitted changes a fold reads, where that is what is wanted."
                    : $"{Agent(record)}'s HEAD is {at}, "
                        + (parted == record.Base
                            ? $"a commit of the main tree's after its base {based}, "
                                + (record.Moving is { } moving
                                    ? $"while a move of its base to {OrchestrationReports.Base(moving)} stands stopped part way"
                                    : "though no move of its base is under way")
                            : $"a commit neither before nor after its base {based}")
                        + $": it was moved there by hand, so folding or moving it now would weigh its work against the wrong commit. {putBack}"));
    }

    public async Task<CommandOutcome> FoldAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        FoldAllowances allowances,
        bool apply,
        CancellationToken cancellationToken = default,
        bool json = false)
    {
        ArgumentNullException.ThrowIfNull(allowances);

        if ((Shape(orchestrator, agent) ?? allowances.Problem()) is { } shape)
        {
            return Answered(CommandOutcome.Usage(shape), json);
        }

        var at = await LiveAgentAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (at.Refusal is { } refusal)
        {
            return Answered(refusal, json);
        }

        var met = new HoldersMet();
        var outcome = await FoldLiveAsync(at, allowances, apply, json, met, cancellationToken).ConfigureAwait(false);
        var answered = apply ? _log.Record(at.Layout!, agent, FoldCommand, outcome) : outcome;

        // Asked for one document, what the live agent answered brings one, and what was returned above brings the same
        // document, empty, with its facts, as refresh-agent says.
        return Answered(answered, apply, json, met);
    }

    /// <summary>Folding a live agent, once it is found.</summary>
    private async Task<CommandOutcome> FoldLiveAsync(AgentAt at, FoldAllowances allowances, bool apply, bool json, HoldersMet met, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (at.Context!, at.Layout!, at.Record!, at.Path!);
        var (unfoldable, seed) = await UnfoldableAsync(context.Layout.MainCheckoutRoot, layout, record, path, cancellationToken).ConfigureAwait(false);

        // A fold takes the main tree and the agent's worktree: who holds either is named on every exit a dry run makes.
        string[] heldTrees = [context.Layout.MainCheckoutRoot, path];

        if (unfoldable is not null)
        {
            return !apply ? Dry(json, context.Layout, FoldCommand, unfoldable, DryRuns.StandingOf(unfoldable), heldTrees) : unfoldable;
        }

        if (!apply)
        {
            var measured = await MeasureAsync(context, layout, record, path, seed!, allowances, deleting: false, AnchorBatchMode.Plan, cancellationToken).ConfigureAwait(false);

            var (standing, typed) = Consents(measured);

            if (measured.Refusal is { } refused)
            {
                return Dry(json, context.Layout, FoldCommand, refused, standing, typed, heldTrees);
            }

            var (tail, line) = Ending(FoldCommand, record, allowances, measured.Rows.Batch, "write them");

            return Dry(
                json,
                context.Layout,
                FoldCommand,
                CommandOutcome.Ok(
                    $"dry run: folding {Lower(Agent(record))} writes {ReportText.Counted(measured.Plan!.Written.Count, "path")} into the main tree, "
                    + $"{Merging(measured.Plan, "merges")}{Handing(measured.Plan, "hands")}removes {measured.Plan.Deleted.Count} and applies {ReportText.Counted(measured.Rows.Planned, "row")}; {tail}",
                    [.. OrchestrationReports.FoldAndRowLines(measured.Plan, measured.Rows, layout.RowsDirectory(record.Name), applied: false), .. line]),
                standing,
                typed,
                heldTrees);
        }

        // The agent's worktree is held too: a leg building in it would be writing what is being folded.
        var hold = await HoldAsync(context.Layout, FoldCommand, met, cancellationToken, context.Layout.MainCheckoutRoot, path).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            var measured = await MeasureAsync(context, layout, record, path, seed!, allowances, deleting: false, AnchorBatchMode.Check, cancellationToken).ConfigureAwait(false);

            if (measured.Refusal is { } refused)
            {
                // Typed as its dry run's would be, a consent nobody gave besides: a conflict told from every other refusal
                // without reading a line.
                met.Reasons = ApplyReasons(measured);
                return refused;
            }

            var written = await WriteAsync(context, layout, record, path, seed!, measured).ConfigureAwait(false);

            // What the fold wrote rides the answer as its facts, whatever form the answer is asked in - as far as it went,
            // where it stopped part way, since that much is in the main tree now.
            met.Folded = FoldedOf(written, context);

            return written.Failure ?? CommandOutcome.Ok(
                $"folded {Lower(Agent(record))}: wrote {ReportText.Counted(measured.Plan!.Written.Count, "path")} into the main tree, "
                + $"{Merging(measured.Plan, "merged")}{Handing(measured.Plan, "handed")}removed {measured.Plan.Deleted.Count} and applied {ReportText.Counted(written.Rows.Planned, "row")}; its worktree is kept, and what the fold wrote is "
                + "recorded as shared, so a fold after a review weighs the agent's change of it",
                [.. OrchestrationReports.FoldAndRowLines(measured.Plan, written.Rows, layout.RowsDirectory(record.Name), applied: true), .. written.Notes, .. Records(layout, record)]);
        }
    }

    public async Task<CommandOutcome> DeleteAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        FoldAllowances allowances,
        bool apply,
        bool discardUncommitted,
        CancellationToken cancellationToken = default,
        bool json = false)
    {
        ArgumentNullException.ThrowIfNull(allowances);

        if ((Shape(orchestrator, agent) ?? allowances.Problem()) is { } shape)
        {
            return Answered(CommandOutcome.Usage(shape), json);
        }

        if (discardUncommitted && allowances.Given.Count > 0)
        {
            return Answered(
                CommandOutcome.Usage($"{Letting(allowances)}, and --discard-uncommitted folds nothing: give one or the other."),
                json);
        }

        var found = await FindAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (found.Refusal is { } refusal)
        {
            return Answered(refusal, json);
        }

        var met = new HoldersMet();
        var outcome = await DeleteFoundAsync(found.Context!, found.Layout!, found.Record!, allowances, apply, discardUncommitted, json, met, cancellationToken).ConfigureAwait(false);
        var answered = apply ? _log.Record(found.Layout!, agent, DeleteCommand, outcome) : outcome;

        // Asked for one document, what the agent found answered brings one, and what was returned above brings the same
        // document, empty, with its facts, as refresh-agent says.
        return Answered(answered, apply, json, met);
    }

    /// <summary>Deleting an agent, once its record is found.</summary>
    private async Task<CommandOutcome> DeleteFoundAsync(
        HarnessContext context,
        OrchestratorLayout layout,
        AgentRecord record,
        FoldAllowances allowances,
        bool apply,
        bool discardUncommitted,
        bool json,
        HoldersMet met,
        CancellationToken cancellationToken)
    {
        if (record.State == AgentStates.Deleted)
        {
            return CommandOutcome.Refused(
                $"{Agent(record)} was deleted at {OrchestrationReports.Moment(record.DeletedAt!.Value)}: nothing of it is left to delete, and its "
                + "directory is kept as its history.");
        }

        if (record.Reads)
        {
            return DeleteReadingAsync(context, layout, record, allowances, apply, discardUncommitted, json);
        }

        var (path, moved) = WorktreeOf(context, record);

        if (moved is not null)
        {
            return CommandOutcome.Refused(moved);
        }

        // A removal cannot take a directory a process stands in: Windows refuses it, naming this process, and elsewhere the
        // process is left standing in a directory that is gone. A dry run is refused as its --apply is.
        if (StandsIn(path) is { } here)
        {
            return CommandOutcome.Refused(
                $"This process's working directory '{here}' is inside the worktree of {Lower(Agent(record))}, '{path}', which deleting the agent "
                + "removes. Nothing was written or removed: run it from the main tree.");
        }

        var standing = await StandingAsync(context, path, cancellationToken).ConfigureAwait(false);
        var target = new Target(context, layout, record, WorktreeAddress.Nested(record.Orchestrator, record.Name), path, standing);

        return record.State == AgentStates.Closed
            ? await FinishAsync(target, allowances, apply, json, met, cancellationToken).ConfigureAwait(false)
            : await CloseAsync(target, allowances, apply, discardUncommitted, json, met, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deleting an agent that reads the main tree: one write, live to deleted, which is the path the states already have
    /// for an agent whose worktree was never made. Nothing is folded and no worktree is removed, because it has neither;
    /// its directory stays, as every deleted agent's does.
    /// </summary>
    private CommandOutcome DeleteReadingAsync(
        HarnessContext context,
        OrchestratorLayout layout,
        AgentRecord record,
        FoldAllowances allowances,
        bool apply,
        bool discard,
        bool json)
    {
        // --discard-uncommitted and the allowances act on the changes in a worktree and on what a fold would write, and
        // this agent has neither: each is said to have nothing to act on, never silently ignored.
        var nothing = new List<string>();

        if (discard)
        {
            nothing.Add("--discard-uncommitted discards the changes in an agent's worktree, and this agent has none");
        }

        if (allowances.Merge.Count + allowances.Settled.Count + allowances.New.Count + allowances.AcceptLost.Count + allowances.AcceptOver.Count > 0)
        {
            nothing.Add("the allowances let a fold write, and there is nothing of this agent to fold");
        }

        if (!apply)
        {
            var (tail, _) = Ending(DeleteCommand, record, allowances, rows: null, "do it");

            return Dry(
                json,
                context.Layout,
                DeleteCommand,
                CommandOutcome.Ok(
                    $"dry run: deleting {Lower(Agent(record))} folds nothing and removes no worktree - it reads the main tree, and has neither - "
                    + $"and marks it deleted; {tail}",
                    [$"record {layout.AgentRecordFile(record.Name)}", .. nothing]),
                standing: null);
        }

        _store.UpdateAgent(
            layout,
            record.Name,
            current => current with { State = AgentStates.Deleted, DeletedAt = _clock.GetUtcNow(), Abandoned = false });

        // Unlogged here, as every delete path's apply is: the caller logs it once.
        return CommandOutcome.Ok(
            $"deleted {Lower(Agent(record))}, which read the main tree: nothing was folded and no worktree removed, and its directory is kept as its history",
            [$"record {layout.AgentRecordFile(record.Name)}", .. nothing]);
    }

    /// <summary>
    /// Deleting a live agent: its fold and rows - or nothing, abandoning it - then its transcripts and evidence kept, its
    /// closing recorded, and its worktree's removal asked for.
    /// </summary>
    private async Task<CommandOutcome> CloseAsync(Target target, FoldAllowances allowances, bool apply, bool discard, bool json, HoldersMet met, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (target.Context, target.Layout, target.Record, target.Path);

        switch (target.Standing.Kind)
        {
            case Standing.Gone when discard:
                return await AbandonGoneAsync(target, apply, json, met, cancellationToken).ConfigureAwait(false);

            case Standing.Gone:
                return CommandOutcome.Refused(
                    $"{Agent(record)} has no worktree at '{path}', so nothing of it can be folded. --discard-uncommitted deletes it with nothing "
                    + "folded, keeping its transcripts.");

            case not Standing.Registered:
                return CommandOutcome.Refused(Unusable(record, path, target.Standing));
        }

        var seed = discard ? _store.ReadSeed(layout, record.Name) : null;

        if (!discard)
        {
            var (unfoldable, found) = await UnfoldableAsync(context.Layout.MainCheckoutRoot, layout, record, path, cancellationToken).ConfigureAwait(false);

            if (unfoldable is not null)
            {
                // Named on every exit a dry run makes, refused or not: who holds what its --apply takes.
                return !apply
                    ? Dry(json, context.Layout, DeleteCommand, unfoldable, DryRuns.StandingOf(unfoldable), context.Layout.MainCheckoutRoot, path)
                    : unfoldable;
            }

            seed = found;
        }

        if (!apply)
        {
            var dry = await MeasureClosingAsync(target, seed, allowances, discard, AnchorBatchMode.Plan, cancellationToken).ConfigureAwait(false);

            if (dry.Refusal is { } refusal)
            {
                // Weighed as the fold's own only where the refusal is the fold's: a closing whose fold measured clean and
                // then could not read its evidence failed for that, and stands as its failure does.
                var (refusedStanding, refusedTyped) = dry.Fold is { } measuredFold && ReferenceEquals(refusal, measuredFold.Refusal)
                    ? Consents(measuredFold)
                    : (DryRuns.StandingOf(refusal), Typed.None);

                return Dry(json, context.Layout, DeleteCommand, refusal, refusedStanding, refusedTyped, context.Layout.MainCheckoutRoot, path);
            }

            // Which git worktree this is, as its --apply reads it before it writes: one that cannot be read fails both alike.
            if (StampOf(target).Failure is { } unread)
            {
                return Dry(json, context.Layout, DeleteCommand, unread, standing: null, context.Layout.MainCheckoutRoot, path);
            }

            var evidence = dry.Evidence.Files.Count == 0
                ? "its evidence roots hold nothing to keep"
                : $"{ReportText.Counted(dry.Evidence.Files.Count, "evidence file")}, to be kept in a directory named for the run in '{layout.EvidenceDirectory(record.Name)}'";
            var (tail, line) = Ending(DeleteCommand, record, allowances, dry.Fold?.Rows.Batch, "do it");

            var planned = CommandOutcome.Ok(
                $"dry run: deleting {Lower(Agent(record))} "
                + (discard
                    ? $"discards {ReportText.Counted(dry.Discarded.Count, "changed path")}, folding nothing"
                    : $"folds {ReportText.Counted(dry.Fold!.Plan!.Written.Count, "path")}, {Merging(dry.Fold.Plan, "merges")}{Handing(dry.Fold.Plan, "hands")}removes {dry.Fold.Plan.Deleted.Count} and applies "
                        + ReportText.Counted(dry.Fold.Rows.Planned, "row"))
                + $", keeps its transcripts and {ReportText.Counted(dry.Evidence.Files.Count, "evidence file")}, then removes its worktree and its copies on hosts; {tail}",
                [
                    .. discard
                        ? [$"{ReportText.Counted(dry.Discarded.Count, "changed path")} discarded:", .. dry.Discarded.Select(relative => $"  {relative}")]
                        : OrchestrationReports.FoldAndRowLines(dry.Fold!.Plan, dry.Fold.Rows, layout.RowsDirectory(record.Name), applied: false),
                    evidence,
                    TranscriptsPlan(record),
                    .. line,
                ]);

            var (closingStanding, closingTyped) = dry.Fold is { } fold ? Consents(fold) : (RequestStanding.Sound, Typed.None);

            return await DryRemovalAsync(
                json,
                target,
                planned,
                closingStanding,
                closingTyped,
                cancellationToken,
                context.Layout.MainCheckoutRoot,
                path).ConfigureAwait(false);
        }

        var hold = await HoldAsync(context.Layout, DeleteCommand, met, cancellationToken, context.Layout.MainCheckoutRoot, path).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            var measured = await MeasureClosingAsync(target, seed, allowances, discard, AnchorBatchMode.Check, cancellationToken).ConfigureAwait(false);

            if (measured.Refusal is { } refused)
            {
                // Typed as its dry run's would be, a consent nobody gave besides, where the refusal is the fold's own.
                met.Reasons = measured.Fold is { } refusedFold && ReferenceEquals(refused, refusedFold.Refusal) ? ApplyReasons(refusedFold) : [];
                return refused;
            }

            // Which git worktree this is, read before anything is written: its closing records it, and a worktree whose
            // identity cannot be read is never closed.
            var (stamp, unreadable) = StampOf(target);

            if (unreadable is not null)
            {
                return unreadable;
            }

            // Which copies hosts keep of it, read before anything is written, as its dry run reads them: a removal that could
            // not tell them would leave an agent closed and its removal unfinished.
            if (HostCopiesOf(target).Unread is { } copiesUnread)
            {
                return copiesUnread;
            }

            var lines = new List<string>();
            var what = discard ? "Nothing of it was folded" : "Its fold and rows are in the main tree";
            var again = OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, discard ? "--apply --discard-uncommitted" : "--apply");

            // What is said of the copies its fold kept, said only where the agent outlives this command: once it is closed,
            // nothing folds it again, and they go with it.
            IReadOnlyList<string> notes = [];

            if (!discard)
            {
                var fold = measured.Fold!;
                var written = await WriteAsync(context, layout, record, path, seed!, fold).ConfigureAwait(false);

                // The work the delete folded back rides the answer as its facts, whatever form the answer is asked in - as
                // far as it went, where it stopped part way, since that much is in the main tree now.
                met.Folded = FoldedOf(written, context);

                if (written.Failure is { } failure)
                {
                    return failure with { Message = $"{failure.Message} Nothing was removed." };
                }

                seed = written.Seed;
                lines.AddRange(OrchestrationReports.FoldAndRowLines(fold.Plan, written.Rows, layout.RowsDirectory(record.Name), applied: true));
                notes = written.Notes;
            }

            IReadOnlyList<string> Live() => [.. lines, .. notes, .. Records(layout, record)];

            // Until the closing is recorded, the agent is live: anything that stops it here says what is in, and a run again
            // measures it afresh.
            var stopped = discard ? HarnessExit.CommandFailed : HarnessExit.Incomplete;
            EvidenceKept kept;

            try
            {
                if (!discard)
                {
                    // Nothing may be left to fold: the removal discards the worktree's uncommitted work on this measurement alone.
                    var after = await _fold.MeasureAsync(
                        context.Layout.MainCheckoutRoot,
                        path,
                        record.Base!,
                        seed!,
                        allowances.Settled,
                        TwoChangePolicy.Of(context, allowances.Merge),
                        Changes(context, layout, record.Name, keeping: false),
                        Floor(context),
                        CancellationToken.None).ConfigureAwait(false);

                    if (!after.NothingLeft)
                    {
                        return CommandOutcome.Failed(
                            HarnessExit.Incomplete,
                            $"{Agent(record)} was not deleted: after its fold was written, it still differs from the main tree - "
                            + $"{ReportText.Listed([.. after.Written, .. after.Merged.Select(merge => merge.Path), .. after.Deleted, .. after.Handed.Files.Concat(after.Handed.Deletions), .. after.Refusals])}. {what}, and its worktree is kept; nothing was removed.",
                            Live());
                    }
                }

                var transcripts = await TranscriptsAsync(layout, record).ConfigureAwait(false);
                lines.AddRange(transcripts.Lines);

                if (transcripts.Failed is { } lost)
                {
                    return CommandOutcome.Failed(
                        stopped,
                        $"{Agent(record)} was not deleted, because its transcripts could not be kept: {lost}. {what}; its worktree is kept, and nothing "
                        + $"was removed. Run {again} again once that is dealt with.",
                        Live());
                }

                // The evidence roots named before the fold and after it: a fold can bring a change of worktrees.evidenceRoots into
                // the main tree's configuration, and a root it dropped would go unkept.
                var reloaded = await _contextLoader.LoadAsync(context.Layout.MainCheckoutRoot, CancellationToken.None).ConfigureAwait(false);
                var roots = context.Config.Worktrees.EvidenceRoots.Concat(reloaded.Config.Worktrees.EvidenceRoots).Distinct(StringComparer.Ordinal).ToList();
                var keeping = OrchestratorLayout.KeptEvidence(hold.Run.ToString());
                var destination = layout.KeptEvidence(record.Name, keeping);
                kept = await _evidence.KeepAsync(path, roots, measured.Evidence.Files, destination, CancellationToken.None).ConfigureAwait(false);

                if (kept.Problem is { } problem)
                {
                    return CommandOutcome.Failed(
                        stopped,
                        $"{Agent(record)} was not deleted, because its evidence could not be kept: {problem}. {what}; its worktree and its evidence are "
                        + "kept, and nothing was removed.",
                        Live());
                }

                lines.Add(EvidenceLine(kept, destination));
                var holding = await _fold.HeldAsync(path, seed, [], Floor(context), CancellationToken.None).ConfigureAwait(false);
                target = target with { Record = Close(target, stamp!, discard, kept.Kept.Count > 0 ? keeping : string.Empty, holding) };
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    stopped,
                    $"{Agent(record)} was not deleted: {ex.Message.TrimEnd('.')}. {what}; its worktree is kept, and nothing was removed. Run "
                    + $"{again} again once that is dealt with.",
                    Live());
            }

            return await AfterClosingAsync(target, kept.Kept, lines).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records the agent closed, before its worktree's removal is asked for: from here nothing folds it again, and running
    /// delete-agent again finishes the removal, never reading the debris of one that stopped part way as the agent's work.
    /// </summary>
    /// <exception cref="HarnessException">Its record changed since it was read, as by a command run beside this one.</exception>
    private AgentRecord Close(Target target, string stamp, bool abandoned, string evidence, Dictionary<string, string?> held)
    {
        var closed = _store.UpdateAgent(target.Layout, target.Record.Name, current => current.State != AgentStates.Live ? null : current with
        {
            State = AgentStates.Closed,
            Abandoned = abandoned,
            Closing = new AgentClosing { At = _clock.GetUtcNow(), Evidence = evidence, Stamp = stamp, Held = held },
        });

        return closed.State == AgentStates.Closed
            ? closed
            : throw new HarnessException(HarnessExit.Refused, $"its record says it is {closed.State} now, changed since this command read it");
    }

    /// <summary>
    /// What follows the closing: the kept originals deleted and the worktree's removal asked for. The agent is closed, so
    /// anything that stops it says so, and delete-agent run again finishes it.
    /// </summary>
    private async Task<CommandOutcome> AfterClosingAsync(Target target, IReadOnlyDictionary<string, string> kept, List<string> lines)
    {
        try
        {
            // The originals go only where they still hold what was kept; the removal then keeps its evidence check, so a
            // file written since stops it rather than going with it.
            var deleted = await _evidence.DeleteKeptAsync(target.Path, kept, CancellationToken.None).ConfigureAwait(false);

            if (deleted.Left.Count > 0)
            {
                lines.Add($"{ReportText.Counted(deleted.Left.Count, "evidence file")} were left: {ReportText.Listed(deleted.Left)}");
            }

            return await RemoveAsync(target, lines).ConfigureAwait(false);
        }
        catch (Exception ex) when (Unfinished(ex))
        {
            return Closed(target, ex.Message.TrimEnd('.'), lines);
        }
    }

    /// <summary>
    /// Deleting a closed agent again: nothing is folded; its worktree is compared with what closing it recorded, never with
    /// the main tree, its evidence and transcripts are kept again, and its removal is asked for again.
    /// </summary>
    private async Task<CommandOutcome> FinishAsync(Target target, FoldAllowances allowances, bool apply, bool json, HoldersMet met, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (target.Context, target.Layout, target.Record, target.Path);
        var closing = record.Closing!;

        if (allowances.Given.Count > 0)
        {
            return CommandOutcome.Usage(
                $"{Letting(allowances)}, and {Lower(Agent(record))}, closed at "
                + $"{OrchestrationReports.Moment(closing.At)}, is never folded again. Nothing was read, written or removed.");
        }

        var was = $"{Agent(record)} was closed at {OrchestrationReports.Moment(closing.At)} - "
            + (record.Abandoned is true ? "abandoned, with nothing folded" : "its fold and rows are in the main tree")
            + (closing.Evidence.Length > 0 ? $", its evidence kept in '{layout.KeptEvidence(record.Name, closing.Evidence)}'" : string.Empty)
            + " - and its worktree's removal did not finish";
        var removal = OrchestrationReports.Line(WorktreeService.DeleteCommand, target.Address.Name, "--discard-uncommitted", "--delete-evidence");
        var again = OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name);

        switch (target.Standing.Kind)
        {
            case Standing.Registered:
                string? stamp;

                try
                {
                    stamp = WorktreeStamp.Of(_fileSystem, target.Standing.AdministrativeDirectory!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return CommandOutcome.Refused(
                        $"{was}. Which git worktree '{path}' is cannot be read now - {ex.Message.TrimEnd('.')} - so whether it is the one closed then "
                        + $"cannot be told. Nothing of it was read or removed; run {again} again once it can be read.");
                }

                if (stamp is null)
                {
                    return CommandOutcome.Refused(
                        $"{was}. git names no '{WorktreeStamp.FileName}' in the git directory of '{path}', so whether it is the worktree closed then "
                        + $"or one made there since cannot be told. Nothing of it was read or removed: once what it holds is copied out, {removal} "
                        + $"removes it, and {again} then finishes this agent.");
                }

                if (stamp != closing.Stamp)
                {
                    return CommandOutcome.Refused(
                        $"{was}. '{path}' is not the worktree closed then: git knows it as another, made there since. Nothing of it was read or removed, "
                        + $"and it is not this agent's: {OrchestrationReports.Line(WorktreeService.DeleteCommand, target.Address.Name)} removes it once what "
                        + $"it holds is kept, and {again} then finishes this agent.");
                }

                var changed = await _fold
                    .ChangedSinceAsync(context.Layout.MainCheckoutRoot, path, closing, _store.ReadSeed(layout, record.Name), Floor(context), cancellationToken)
                    .ConfigureAwait(false);

                if (changed.Count > 0)
                {
                    return CommandOutcome.Failed(
                        HarnessExit.Incomplete,
                        $"{was}, and its worktree holds {ReportText.Counted(changed.Count, "file")} its closing did not record, changed or new since: work done in an agent "
                        + $"already closed, which nothing folds again and nothing discards unseen. Copy it out, then remove the worktree with {removal}, "
                        + $"and {again} finishes this agent.",
                        [.. changed.Select(relative => $"  {relative}"), .. Records(layout, record)]);
                }

                break;

            case Standing.Husk or Standing.Gone:
                break;

            default:
                return CommandOutcome.Refused(Unusable(record, path, target.Standing));
        }

        if (!apply)
        {
            // A directory that is no longer a git worktree: every --apply keeps its evidence again and leaves the removal
            // unfinished, so its dry run answers as that --apply does, keeping nothing - and asks nothing of the removal,
            // which that --apply stops before it reaches.
            if (target.Standing.Kind == Standing.Husk)
            {
                return Dry(json, context.Layout, DeleteCommand, CommandOutcome.Failed(HarnessExit.Incomplete, Husk(target, was, applied: false)), RequestStanding.Refused, path);
            }

            // Closed or gone, the removal's questions are the same ones a live agent's dry run asks, unqualified by the
            // agent's state: a copy a run holds leaves the --apply's removal unfinished, and the dry run says so.
            return await DryRemovalAsync(
                json,
                target,
                CommandOutcome.Ok($"dry run: {Lower(was)}; pass --apply to {(target.Standing.Kind == Standing.Gone ? "finish the removal" : "keep its evidence again and finish the removal")}"),
                RequestStanding.Sound,
                Typed.None,
                cancellationToken,
                path).ConfigureAwait(false);
        }

        var hold = await HoldAsync(context.Layout, DeleteCommand, met, cancellationToken, path).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            // Which copies hosts keep of it, read before anything is kept again, as its dry run reads them - save for a
            // directory that is no longer a git worktree, whose --apply stops before the removal, and whose dry run asks
            // nothing of it.
            if (target.Standing.Kind != Standing.Husk && HostCopiesOf(target).Unread is { } copiesUnread)
            {
                return copiesUnread;
            }

            var lines = new List<string>();

            try
            {
                var transcripts = await TranscriptsAsync(layout, record).ConfigureAwait(false);
                lines.AddRange(transcripts.Lines);

                if (transcripts.Failed is { } lost)
                {
                    return Closed(target, $"its transcripts could not be kept: {lost}", lines);
                }

                if (target.Standing.Kind == Standing.Gone)
                {
                    return await RemoveAsync(target, lines).ConfigureAwait(false);
                }

                // Kept again, into a directory of its own: a run that held a file open when the removal stopped may have gone
                // on writing it, and one directory for both would refuse every run after the first as a clash. Nothing was
                // measured before, so nothing is compared with a measure.
                var destination = layout.KeptEvidence(record.Name, OrchestratorLayout.KeptEvidence(hold.Run.ToString()));
                var kept = await _evidence.KeepAsync(path, context.Config.Worktrees.EvidenceRoots, measured: null, destination, CancellationToken.None).ConfigureAwait(false);

                if (kept.Problem is { } problem)
                {
                    return Closed(target, $"its evidence could not be kept again: {problem}", lines);
                }

                lines.Add(EvidenceLine(kept, destination));

                if (target.Standing.Kind == Standing.Husk)
                {
                    // Never forced from here: --force checks nothing, and a directory with no .git of its own is what a removal
                    // that stopped part way leaves - or a directory made there since, which nothing here can tell apart.
                    return CommandOutcome.Failed(HarnessExit.Incomplete, Husk(target, was, applied: true), [.. lines, .. Records(layout, record)]);
                }

                return await AfterClosingAsync(target, kept.Kept, lines).ConfigureAwait(false);
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return Closed(target, ex.Message.TrimEnd('.'), lines);
            }
        }
    }

    /// <summary>Deleting a live agent whose worktree is gone, with nothing folded: its transcripts kept, and what is left of the worktree removed.</summary>
    private async Task<CommandOutcome> AbandonGoneAsync(Target target, bool apply, bool json, HoldersMet met, CancellationToken cancellationToken)
    {
        var (layout, record) = (target.Layout, target.Record);
        var again = OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, "--apply --discard-uncommitted");

        if (!apply)
        {
            // The worktree asked about as its --apply asks for it, keyed as the lock keys it, beside the removal's other
            // questions: a run holding what the removal takes is named, and a copy a run holds leaves it unfinished.
            return await DryRemovalAsync(
                json,
                target,
                CommandOutcome.Ok(
                    $"dry run: {Lower(Agent(record))} has no worktree at '{target.Path}'; pass --apply to delete it with nothing folded, keeping its "
                    + "transcripts, once git's record of its worktree and every copy of it on a host are gone",
                    [TranscriptsPlan(record)]),
                RequestStanding.Sound,
                Typed.None,
                cancellationToken,
                target.Path).ConfigureAwait(false);
        }

        var hold = await HoldAsync(target.Context.Layout, DeleteCommand, met, cancellationToken, target.Path).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            // Which copies hosts keep of it, read before anything is kept, as its dry run reads them.
            if (HostCopiesOf(target).Unread is { } copiesUnread)
            {
                return copiesUnread;
            }

            var transcripts = await TranscriptsAsync(layout, record).ConfigureAwait(false);

            if (transcripts.Failed is { } lost)
            {
                return CommandOutcome.Failed(
                    HarnessExit.CommandFailed,
                    $"{Agent(record)} was not deleted, because its transcripts could not be kept: {lost}. Nothing was removed; run {again} again once "
                    + "that is dealt with.",
                    [.. transcripts.Lines, .. Records(layout, record)]);
            }

            try
            {
                return await RemoveAsync(target, [.. transcripts.Lines]).ConfigureAwait(false);
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    HarnessExit.CommandFailed,
                    $"{Agent(record)} was not deleted: {ex.Message.TrimEnd('.')}. Run {again} again once that is dealt with.",
                    [.. transcripts.Lines, .. Records(layout, record)]);
            }
        }
    }

    /// <summary>
    /// Removes an agent's worktree and the copies hosts keep of it through delete-worktree - never forced, its evidence
    /// check kept - then proves it: the directory gone, git's record of it gone, and no copy of it recorded on a host. Only
    /// then is the agent deleted, its closing kept as its history; anything left makes the deletion incomplete, naming
    /// what is left.
    /// </summary>
    private async Task<CommandOutcome> RemoveAsync(Target target, List<string> lines)
    {
        var record = target.Record;

        // Closed, it records whether it was abandoned; still live, its worktree is gone and nothing of it can be folded.
        var abandoned = record.Abandoned ?? true;
        WorktreeOutcome? removal = null;

        // Nothing is asked of delete-worktree where nothing is left for it: it would answer that no such worktree exists.
        if (target.Standing.Kind != Standing.Gone || (await LeftAsync(target).ConfigureAwait(false)).Count > 0)
        {
            removal = await _worktrees
                .DeleteAsync(target.Context.Layout.MainCheckoutRoot, target.Address.Name, force: false, deleteEvidence: false, discardUncommitted: true, CancellationToken.None)
                .ConfigureAwait(false);

            lines.Add($"{WorktreeService.DeleteCommand} {target.Address.Name}: {removal.Outcome.Message}");
            lines.AddRange((removal.Outcome.Details ?? []).Select(detail => $"  {detail}"));
        }

        // The exit code alone is not proof.
        var left = await LeftAsync(target).ConfigureAwait(false);

        if (removal is { Succeeded: false } && left.Count == 0)
        {
            left.Add($"{WorktreeService.DeleteCommand} exited {removal.Outcome.ExitCode} although the worktree, git's record of it and every recorded copy are gone, and its lines say why");
        }

        if (left.Count > 0)
        {
            return record.State == AgentStates.Closed
                ? Closed(target, string.Join("; ", left), lines)
                : CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"{Agent(record)} is not deleted yet: {string.Join("; ", left)}. Deal with what is named, then run "
                    + $"{OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, "--apply --discard-uncommitted")} again.",
                    [.. lines, .. Records(target.Layout, record)]);
        }

        _store.UpdateAgent(target.Layout, record.Name, current => current with { State = AgentStates.Deleted, DeletedAt = _clock.GetUtcNow(), Abandoned = abandoned });

        // Nothing merges from them any more: they go once its record says it is deleted, and one that cannot is said, never
        // a deletion left unfinished.
        if (Copies(target.Layout, record.Name).Remove() is { } stillKept)
        {
            lines.Add(stillKept);
        }

        return CommandOutcome.Ok(
            $"deleted {Lower(Agent(record))}: {(abandoned ? "abandoned, with nothing folded" : "its fold and rows are in the main tree")}, "
            + "and its worktree, git's record of it and every copy of it recorded on a host are gone; its directory is kept as its history",
            [.. lines, .. Records(target.Layout, record)]);
    }

    /// <summary>What is said of a closed agent whose removal stopped: what stopped it, what is in, and that running delete-agent again finishes it.</summary>
    private CommandOutcome Closed(Target target, string why, IReadOnlyList<string> lines)
    {
        var record = target.Record;

        return CommandOutcome.Failed(
            HarnessExit.Incomplete,
            $"{Agent(record)} is closed and not deleted yet: {why}. "
            + (record.Abandoned is true ? "Nothing of it was folded" : "Its fold and rows are in the main tree")
            + $", and it is closed, so nothing folds it again. Deal with what is named, then run {OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name)} "
            + "again: it folds nothing, and finishes the removal or names what stops it.",
            [.. lines, .. Records(target.Layout, record)]);
    }

    /// <summary>
    /// What is left of an agent's worktree: its directory, git's record of it, and copies recorded on hosts. A question that
    /// cannot be answered is named as that, never taken for nothing left.
    /// </summary>
    private async Task<List<string>> LeftAsync(Target target)
    {
        var left = new List<string>();
        var path = target.Path;

        try
        {
            if (_fileSystem.KindOf(path) != PathKind.None)
            {
                left.Add($"'{path}' is still there");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left.Add($"whether '{path}' is still there cannot be told: {ex.Message.TrimEnd('.')}");
        }

        try
        {
            if (await Inspector().FindRecordAsync(target.Context.Layout.MainCheckoutRoot, path, CancellationToken.None).ConfigureAwait(false) is not null)
            {
                left.Add($"git still records '{path}' as a worktree");
            }
        }
        catch (HarnessException ex)
        {
            left.Add($"whether git still records '{path}' as a worktree cannot be told: {ex.Message.TrimEnd('.')}");
        }

        try
        {
            var copies = new HostCopyRecord(_fileSystem, _platform.PathComparison).Of(target.Context.Layout, target.Address.CopyName);

            if (copies.Count > 0)
            {
                left.Add($"the record of host copies still holds {ReportText.Counted(copies.Count, "copy")} of it: {string.Join(", ", copies.Select(copy => $"{copy.Host}: {copy.Path}"))}");
            }
        }
        catch (HarnessException ex)
        {
            left.Add($"the record of host copies cannot be read: {ex.Message.TrimEnd('.')}");
        }

        return left;
    }

    /// <summary>
    /// A live agent's fold and rows, and what its evidence roots hold - or, abandoning it, what it changed - measured
    /// before anything is written: every refusal is collected, and any one leaves everything as it was.
    /// </summary>
    private async Task<ClosingMeasure> MeasureClosingAsync(
        Target target,
        SeedRecord? seed,
        FoldAllowances allowances,
        bool discard,
        AnchorBatchMode mode,
        CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (target.Context, target.Layout, target.Record, target.Path);
        var none = new EvidenceFound(new Dictionary<string, string>(), []);
        Measured? fold = null;
        IReadOnlyList<string> discarded = [];

        try
        {
            if (discard)
            {
                discarded = await _fold.ChangedAsync(path, Floor(context), "what the agent changed cannot be told", within: null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                fold = await MeasureAsync(context, layout, record, path, seed!, allowances, deleting: true, mode, cancellationToken).ConfigureAwait(false);

                if (fold.Refusal is { } refusal)
                {
                    return new ClosingMeasure(fold, discarded, none, refusal);
                }
            }

            return new ClosingMeasure(fold, discarded, await _evidence.FindAsync(path, context.Config.Worktrees.EvidenceRoots, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClosingMeasure(fold, discarded, none, CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"{Agent(record)} was not deleted: {ex.Message.TrimEnd('.')}, and what cannot be read is never taken for nothing there. Nothing was written or removed."));
        }
    }

    /// <summary>
    /// An agent's fold and rows, measured and checked: nothing is written, and every refusal of either is collected.
    /// <paramref name="mode"/> is how far the rows are held to what applying them would refuse: a dry run shows a cell
    /// that would lose stored text, and a run that goes on to write refuses it unless it was accepted.
    /// </summary>
    private async Task<Measured> MeasureAsync(
        HarnessContext context,
        OrchestratorLayout layout,
        AgentRecord record,
        string path,
        SeedRecord seed,
        FoldAllowances allowances,
        bool deleting,
        AnchorBatchMode mode,
        CancellationToken cancellationToken)
    {
        var main = context.Layout.MainCheckoutRoot;
        var verb = deleting ? "deleted" : "folded";
        var nothing = deleting ? "Nothing was written or removed." : "Nothing was written.";
        var policy = TwoChangePolicy.Of(context, allowances.Merge);
        FoldPlan plan;

        try
        {
            // What a search of the main tree's history finds for a seed with no kept copy is kept by a fold that goes on to
            // write - a deletion's among them, which may stop after its fold with the agent live - and never by a dry run,
            // which writes nothing.
            plan = await _fold.MeasureAsync(
                main,
                path,
                record.Base!,
                seed,
                allowances.Settled,
                policy,
                Changes(context, layout, record.Name, keeping: mode != AnchorBatchMode.Plan),
                Floor(context),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Measured(null, null, [], new AgentRowsPlan(new AnchorBatch([], []), []), CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"{Agent(record)} was not {verb}: what it changed cannot be read - {ex.Message.TrimEnd('.')}. {nothing}"));
        }

        // A registry the agent changed as a file would be written over by the fold and then changed again by its rows, which
        // were weighed against the registry before the fold wrote it: an agent's rows go in through its rows directory only.
        // The policy never takes a merge of one, and a merged one would be refused here all the same.
        var filedAsFiles = plan.Written.Concat(plan.Deleted).Concat(plan.Merged.Select(merge => merge.Path)).Where(policy.AnchorRegistries.Contains).Order(StringComparer.Ordinal)
            .Select(changed => $"'{changed}' is an anchor registry, which a fold changes only through the rows the agent files in "
                + $"'{layout.RowsDirectory(record.Name)}', never as a file: file its rows there instead, or reconcile the file by hand")
            .ToList();

        if (filedAsFiles.Count > 0)
        {
            plan = plan with { Refusals = [.. plan.Refusals, .. filedAsFiles] };
        }

        (AgentRowsPlan Plan, AnchorBatchRequest Request, IReadOnlyList<AnchorRowDeclaration> All) rows;

        try
        {
            // The directories at the top of the tree its fold makes count as the tree's, before its files are written and
            // after, so a path a row cites into one of them is judged the same both times.
            var roots = plan.Written.Where(written => written.Contains('/', StringComparison.Ordinal)).Select(written => written[..written.IndexOf('/', StringComparison.Ordinal)]);
            rows = await MeasureRowsAsync(main, layout, record, allowances, mode, [.. roots.Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Measured(plan, null, [], new AgentRowsPlan(new AnchorBatch([], []), []), CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"{Agent(record)} was not {verb}: its rows, or the registries they go in, cannot be read - {ex.Message.TrimEnd('.')}. {nothing}"));
        }

        var (rowsPlan, request, all) = rows;

        if (plan.Refusals.Count == 0 && rowsPlan.Batch.Succeeded)
        {
            return new Measured(plan, request, all, rowsPlan, null);
        }

        var what = new List<string>();

        if (plan.Refusals.Count > 0)
        {
            what.Add($"{plan.Refusals.Count} of its paths cannot be folded");
        }

        if (!rowsPlan.Batch.Succeeded)
        {
            what.Add($"{ReportText.Counted(rowsPlan.Batch.Problems.Count, "problem")} with its rows");
        }

        // The command that names every path whose merge is clean and that nobody asked for, beside what this one was given.
        var merging = plan.Mergeable.Count == 0
            ? null
            : OrchestrationReports.Line(
                deleting ? DeleteCommand : FoldCommand,
                [record.Orchestrator, record.Name, .. (allowances with { Merge = [.. allowances.Merge, .. plan.Mergeable] }).Arguments()]);

        return new Measured(plan, request, all, rowsPlan, CommandOutcome.Refused(
            $"{Agent(record)} was not {verb}: {string.Join(", and ", what)}. {nothing}",
            [.. OrchestrationReports.RefusalLines(plan, rowsPlan, layout.RowsDirectory(record.Name), merging)]));
    }

    /// <summary>How many paths a fold merges, as its summary says it between what it writes and what it removes; nothing where it merges none.</summary>
    /// <param name="plan">The fold measured.</param>
    /// <param name="verb">"merges", of a fold to come; "merged", of one written.</param>
    private static string Merging(FoldPlan plan, string verb) => plan.Merged.Count == 0 ? string.Empty : $"{verb} {plan.Merged.Count}, ";

    /// <summary>What a fold's summary says of the paths settled by hand it hands the agent, where there are any.</summary>
    private static string Handing(FoldPlan plan, string verb) => plan.Handed.Count == 0 ? string.Empty : $"{verb} the agent {plan.Handed.Count} settled by hand, ";

    /// <summary>
    /// The agent's rows as a fold weighs them, every one it declares sent with what an earlier fold of it recorded, planned
    /// or checked as applying them would write them (<paramref name="mode"/>): one never applied, making only the rows named
    /// new and losing no stored text nobody accepted; one an earlier fold applied and it declares as it did then, left
    /// untouched; any other weighed cell by cell, inside the registries' own lock, against what they took since - each cell
    /// both changed otherwise written only where --accept-over names it.
    /// </summary>
    /// <remarks>
    /// --new, --accept-lost and --accept-over name rows the agent filed; one naming a row it did not file is refused. A row
    /// an earlier fold of the agent applied is an existing row now - made or changed by that fold - so a --new naming it is
    /// let stand, as is a consent naming a row the agent declares as that fold applied it: the command line of that fold
    /// runs again.
    /// </remarks>
    private async Task<(AgentRowsPlan Plan, AnchorBatchRequest Request, IReadOnlyList<AnchorRowDeclaration> All)> MeasureRowsAsync(
        string main,
        OrchestratorLayout layout,
        AgentRecord record,
        FoldAllowances allowances,
        AnchorBatchMode mode,
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        var directory = layout.RowsDirectory(record.Name);
        var (declared, problems) = AgentRows.Read(_fileSystem, directory);

        if (problems.Count > 0)
        {
            return (new AgentRowsPlan(new AnchorBatch([], problems), []), new AnchorBatchRequest([]), declared);
        }

        var applied = (_store.ReadAppliedRows(layout, record.Name)?.Rows ?? []).ToDictionary(row => row.Id, AnchorIdMatch.Comparer);
        var filed = declared.Select(row => row.Id).ToHashSet(AnchorIdMatch.Comparer);
        var named = allowances.New.Distinct(AnchorIdMatch.Comparer).ToList();
        var (lost, over) = (allowances.LostCells, allowances.OverCells);

        string Unfiled(string option, string what) => $"{option} {what} names no row filed in '{directory}': drop it, or correct the id.";

        var unfiled = named.Where(id => !filed.Contains(id)).Select(id => Unfiled(AnchorBatchRequest.NewOption, id))
            .Concat(lost.Where(cell => !filed.Contains(cell.Id)).Select(cell => Unfiled(AnchorBatchRequest.AcceptLostOption, cell.ToString())))
            .Concat(over.Where(cell => !filed.Contains(cell.Id)).Select(cell => Unfiled(AnchorBatchRequest.AcceptOverOption, cell.ToString())))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Every row it declares, each with what an earlier fold of it recorded: weighed inside the registries' own lock, a row
        // declared as it was then left as they hold it, any other cell by cell against what they took since.
        var request = new AnchorBatchRequest(declared)
        {
            New = [.. named.Where(id => filed.Contains(id) && !applied.ContainsKey(id))],
            AcceptLost = [.. lost.Where(cell => filed.Contains(cell.Id))],
            AcceptOver = [.. over.Where(cell => filed.Contains(cell.Id))],
            Earlier = applied.Where(row => filed.Contains(row.Key)).ToDictionary(row => row.Key, row => row.Value, AnchorIdMatch.Comparer),
            Roots = roots,
        };

        var batch = await _anchors.ApplyAsync(main, request, mode, cancellationToken).ConfigureAwait(false);
        var untouched = batch.Rows.Where(outcome => outcome.Action == AnchorRowAction.Untouched).Select(outcome => outcome.Id).ToList();

        return (new AgentRowsPlan(batch with { Problems = [.. unfiled, .. batch.Problems] }, untouched), request, declared);
    }

    /// <summary>That the options <paramref name="allowances"/> gives let a fold through what it otherwise refuses, for a refusal of them.</summary>
    private static string Letting(FoldAllowances allowances)
        => $"{ReportText.Listed(allowances.Given)} {(allowances.Given.Count == 1 ? "lets" : "let")} a fold through what it otherwise refuses";

    /// <summary>
    /// How the summary of a dry run of a fold ends, and the line it points at: where its rows have cells that need a
    /// consent nobody gave - stored text a cell does not keep, or a cell the registries and the agent both changed - that
    /// --apply refuses it, writing nothing, until each is named, with --accept-lost or --accept-over; and the line opening
    /// "to <paramref name="what"/>:" with the command that does it: what the dry run was let through, and the consent for
    /// each. The summary names that line by its opening words rather than by where it stands: it is the last of the
    /// details, and every report prints its summary after them. Otherwise, that --apply does <paramref name="what"/>. Like
    /// every command line a report names, it runs from where the dry run ran.
    /// </summary>
    private static (string Tail, IReadOnlyList<string> Line) Ending(string command, AgentRecord record, FoldAllowances allowances, AnchorBatch? rows, string what)
    {
        var (lost, over) = (rows?.Unaccepted ?? [], rows?.UnacceptedOver ?? []);

        if (lost.Count + over.Count == 0)
        {
            return ($"pass --apply to {what}", []);
        }

        var accepting = allowances with
        {
            AcceptLost = [.. allowances.AcceptLost, .. lost.Select(cell => cell.ToString())],
            AcceptOver = [.. allowances.AcceptOver, .. over.Select(cell => cell.ToString())],
        };
        var opening = $"to {what}:";
        var naming = $"the line '{opening}' above is the command that does it";

        // With no cell both changed, the sentence a dry run always said of a loss.
        var tail = over.Count == 0
            ? $"{ReportText.Counted(lost.Count, "cell")} of its rows do not keep their stored text word for word, and --apply refuses it, writing nothing, "
                + $"until each is named with {AnchorBatchRequest.AcceptLostOption}, once its word diff above is read: {naming}"
            : $"{ReportText.Counted(lost.Count + over.Count, "cell")} of its rows need a consent --apply refuses it without, writing nothing - "
                + string.Join(
                    ", ",
                    new[]
                    {
                        lost.Count == 0 ? null : $"{lost.Count} that does not keep its stored text word for word ({AnchorBatchRequest.AcceptLostOption})",
                        $"{over.Count} that the registries and the agent both changed ({AnchorBatchRequest.AcceptOverOption})",
                    }.OfType<string>())
                + $" - once each diff above is read: {naming}";

        return (tail, [$"{opening} {OrchestrationReports.Line(command, [record.Orchestrator, record.Name, "--apply", .. accepting.Arguments()])}"]);
    }

    /// <summary>How a dry run's request stands where its rows were planned: needing a consent where a cell of them waits for one, sound otherwise.</summary>
    private static RequestStanding Consenting(AnchorBatch rows)
        => rows.Unaccepted.Count + rows.UnacceptedOver.Count > 0 ? RequestStanding.NeedsConsent : RequestStanding.Sound;

    /// <summary>
    /// The consents a measured fold or closing lacks, the reasons it is refused for, and how its request stands with them.
    /// The cells a plan shows are the consents they always were; and where the measurement refused the request, rows no
    /// registry holds that nobody named new and clean merges nobody asked for are consents it lacks too - the whole
    /// refusal, where each of its problems is one of those, is the consent it sounds like rather than a fault to put right,
    /// and stands as needing one. Any other refusal - a path that conflicts, a misspelled option, a row that does not read -
    /// is put right, and stands as it did, its reasons typed: a conflict apart from everything else. A measurement that
    /// failed - what it changed, its rows or the registries they go in unread - is no refusal at all, and stands as its
    /// failure does, whatever it counted: nothing it would have counted was read.
    /// </summary>
    /// <param name="measured">What the fold measured, or a closing's fold.</param>
    private static (RequestStanding? Standing, Typed Typed) Consents(Measured measured)
    {
        List<ConsentKind> needs = [];

        if (measured.Rows.Batch.UnnamedNew.Count > 0)
        {
            needs.Add(ConsentKind.FoldNewRows);
        }

        if (measured.Rows.Batch.Unaccepted.Count > 0)
        {
            needs.Add(ConsentKind.FoldLostRows);
        }

        if (measured.Rows.Batch.UnacceptedOver.Count > 0)
        {
            needs.Add(ConsentKind.FoldOverCells);
        }

        if (measured.Plan?.Mergeable.Count > 0)
        {
            needs.Add(ConsentKind.FoldTwoChanges);
        }

        if (measured.Refusal is { } refused)
        {
            var standing = DryRuns.StandingOf(refused);

            if (standing != RequestStanding.Refused)
            {
                return (standing, new Typed(needs, []));
            }

            var onlyConsents = measured.Plan is { } plan
                && plan.Refusals.Count == plan.Mergeable.Count
                && measured.Rows.Batch.Problems.Count == measured.Rows.Batch.UnnamedNew.Count;

            return onlyConsents
                ? (RequestStanding.NeedsConsent, new Typed(needs, []))
                : (standing, new Typed(needs, FoldReasons(measured)));
        }

        return (Consenting(measured.Rows.Batch), new Typed(needs, []));
    }

    /// <summary>
    /// The reasons a refused fold or closing is refused for, by kind: a conflict where a path both trees changed has a
    /// merge that conflicts, and something else where any refusal is neither that nor a consent the request lacks - a
    /// clean merge nobody asked for and a row nobody named new are consents, and type no reason.
    /// </summary>
    /// <param name="measured">What the fold measured, refused.</param>
    private static IReadOnlyList<RefusalKind> FoldReasons(Measured measured)
    {
        if (measured.Plan is not { } plan)
        {
            return [RefusalKind.Other];
        }

        var otherwise = plan.Refusals.Count > plan.Conflicting.Count + plan.Mergeable.Count
            || measured.Rows.Batch.Problems.Count > measured.Rows.Batch.UnnamedNew.Count;

        return
        [
            .. plan.Conflicting.Count > 0 ? [RefusalKind.Conflict] : Array.Empty<RefusalKind>(),
            .. otherwise ? [RefusalKind.Other] : Array.Empty<RefusalKind>(),
        ];
    }

    /// <summary>
    /// The reasons a move of an agent's base is refused for, by kind: a conflict where a path both changed has a merge that
    /// conflicts, and something else where any other path refuses it - a clean merge nobody asked for among them, which a
    /// move, asking no consent, refuses as it refuses the rest. Something else too where it could not be measured.
    /// </summary>
    /// <param name="plan">What the move measured; null where it could not be measured.</param>
    private static IReadOnlyList<RefusalKind> RebaseReasons(RebasePlan? plan)
        => plan is null
            ? [RefusalKind.Other]
            :
            [
                .. plan.Conflicting.Count > 0 ? [RefusalKind.Conflict] : Array.Empty<RefusalKind>(),
                .. plan.Refusals.Count > plan.Conflicting.Count ? [RefusalKind.Other] : Array.Empty<RefusalKind>(),
            ];

    /// <summary>
    /// The reasons a request's facts carry for its standing: those typed where it stands refused, something else where it
    /// stands refused and nothing typed one - a refusal of its state, its arguments, or kept copies its removal cannot
    /// take - and none where it does not stand refused.
    /// </summary>
    /// <param name="standing">How the request stands, as its answer says it.</param>
    /// <param name="typed">The reasons its measure typed, where it typed any.</param>
    private static IReadOnlyList<RefusalKind> Reasons(RequestStanding? standing, IReadOnlyList<RefusalKind> typed)
        => standing != RequestStanding.Refused ? [] : typed.Count > 0 ? typed : [RefusalKind.Other];

    /// <summary>
    /// What a fold wrote, removed, merged, handed and applied, as its answer's facts carry it - as far as it went, where it
    /// stopped part way - with the arm registry, merged by arm, apart from the paths merged as text; or null where it did
    /// none of that, which is the one way an answer says a fold did nothing.
    /// </summary>
    /// <param name="written">What writing the measured fold answered.</param>
    /// <param name="context">The repository and its configuration, which name the arm registry.</param>
    private static AgentFolded? FoldedOf(Written written, HarnessContext context)
    {
        var policy = TwoChangePolicy.Of(context, []);
        var rows = written.RowsApplied ? written.Rows.Batch.Rows : [];
        var folded = new AgentFolded(
            written.Applied.Written,
            written.Applied.Deleted,
            [.. written.Applied.Merged.Where(path => policy.RuleFor(path) != TwoChangeRule.Arms)],
            written.Applied.Merged.FirstOrDefault(path => policy.RuleFor(path) == TwoChangeRule.Arms),
            written.Applied.Handed,
            [.. rows.Select(row => row.Change?.To.Name).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. rows.Where(row => row.Action is AnchorRowAction.New or AnchorRowAction.Changed).Select(row => row.Id)],
            [.. rows.SelectMany(row => row.Over).Where(cell => cell.Accepted).Select(cell => cell.Cell.ToString())]);

        return folded is { Written.Count: 0, Deleted.Count: 0, Merged.Count: 0, ArmRegistry: null, Handed.Count: 0, Registries.Count: 0, Rows.Count: 0, OverCells.Count: 0 }
            ? null
            : folded;
    }

    /// <summary>
    /// What a refresh's plan names, as its answer's facts carry it: what it hands and what it merges, the arm registry
    /// apart from the paths merged as text.
    /// </summary>
    /// <param name="plan">What the refresh measured.</param>
    /// <param name="policy">How each path both trees changed is resolved, which names the arm registry.</param>
    private static AgentPlanned PlannedOf(RefreshPlan plan, TwoChangePolicy policy)
        => Planned([], [], [.. plan.Handed.Files, .. plan.Handed.Deletions], [.. plan.Merged.Select(merge => merge.Merge.Path)], policy);

    /// <summary>
    /// What a move of an agent's base names, as its answer's facts carry it: what it takes, what it keeps and what it
    /// merges, the arm registry apart from the paths merged as text.
    /// </summary>
    /// <param name="plan">What the move measured.</param>
    /// <param name="policy">How each path both changed is resolved, which names the arm registry.</param>
    private static AgentPlanned PlannedOf(RebasePlan plan, TwoChangePolicy policy)
        => Planned(plan.Taken, plan.Kept, [], [.. plan.Merged.Select(merge => merge.Path)], policy);

    private static AgentPlanned Planned(IReadOnlyList<string> taken, IReadOnlyList<string> kept, IReadOnlyList<string> handed, IReadOnlyList<string> merged, TwoChangePolicy policy)
        => new(
            taken,
            kept,
            handed,
            [.. merged.Where(path => policy.RuleFor(path) != TwoChangeRule.Arms)],
            merged.FirstOrDefault(path => policy.RuleFor(path) == TwoChangeRule.Arms));

    /// <summary>
    /// The reasons an <c>--apply</c>'s own measure refused it for, by kind: as its dry run types them, and a consent nobody
    /// gave as something else besides - an <c>--apply</c> refuses what its dry run asks, so a request refused for a
    /// conflicting path and a clean merge nobody asked for is never read as refused for conflicts alone.
    /// </summary>
    /// <param name="measured">What the --apply measured, refused.</param>
    private static IReadOnlyList<RefusalKind> ApplyReasons(Measured measured)
    {
        var (_, typed) = Consents(measured);

        return typed.Needs.Count > 0 && !typed.Reasons.Contains(RefusalKind.Other) ? [.. typed.Reasons, RefusalKind.Other] : typed.Reasons;
    }

    /// <summary>
    /// Which git worktree a live agent's is, read before anything of it is written - its closing records it - or the failure
    /// that ends a deletion where it cannot be read: the same for a dry run as for its --apply.
    /// </summary>
    private (string? Stamp, CommandOutcome? Failure) StampOf(Target target)
    {
        try
        {
            return (WorktreeStamp.Of(_fileSystem, target.Standing.AdministrativeDirectory!) ?? throw new IOException($"git names no '{WorktreeStamp.FileName}' in its git directory"), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"{Agent(target.Record)} was not deleted: which git worktree '{target.Path}' is cannot be read - {ex.Message.TrimEnd('.')} - so its closing "
                + "could not record it. Nothing was written or removed."));
        }
    }

    /// <summary>
    /// What is said of a closed agent whose directory is no longer a git worktree, which every --apply of delete-agent keeps
    /// the evidence of again and cannot remove: the sentence its --apply ends with, and its dry run's, which keeps nothing.
    /// </summary>
    /// <param name="target">The closed agent.</param>
    /// <param name="was">What closing it recorded, as a sentence.</param>
    /// <param name="applied">Whether its evidence was kept again, rather than only weighed.</param>
    private static string Husk(Target target, string was, bool applied)
        => (applied ? $"{was}. " : $"dry run: {Lower(was)}; --apply keeps its evidence again and cannot finish the removal: ")
            + $"'{target.Path}' is no longer a git worktree - it holds no .git of its own - which is what a removal that stopped part way leaves, or a "
            + "directory made there since. Look at what else it holds; once nothing in it is needed, "
            + $"{OrchestrationReports.Line(WorktreeService.DeleteCommand, target.Address.Name, "--force")} removes it - nothing here forces it - and "
            + $"{OrchestrationReports.DeleteAgentLine(target.Record.Orchestrator, target.Record.Name)} then finishes this agent.";

    /// <summary>
    /// Writes a measured fold into the main tree, records what it wrote as shared, then applies the rows the agent declared
    /// anew and records them as applied, never interrupted: the rows as written and the seed as recorded, or - once anything
    /// is written - what stopped it, as a change begun and not finished.
    /// </summary>
    private async Task<Written> WriteAsync(HarnessContext context, OrchestratorLayout layout, AgentRecord record, string path, SeedRecord seed, Measured measured)
    {
        var main = context.Layout.MainCheckoutRoot;
        var plan = measured.Plan!;
        var applied = await _fold.ApplyAsync(main, path, plan).ConfigureAwait(false);
        var progress = $"after writing {applied.Written.Count} of {ReportText.Counted(plan.Written.Count, "path")}"
            + (plan.Merged.Count == 0 ? string.Empty : $", merging {applied.Merged.Count} of {plan.Merged.Count}")
            + $" and removing {applied.Deleted.Count} of {plan.Deleted.Count}"
            + (plan.Handed.Count == 0 ? string.Empty : $", then handing the agent {applied.Handed.Count} of {plan.Handed.Count} settled by hand");
        SeedRecord folded;
        IReadOnlyList<string> notes;

        try
        {
            var ledger = Ledger(layout, record.Name, seed);
            await _fold.FoldedAsync(ledger, plan, applied, main, path).ConfigureAwait(false);
            (folded, notes) = WriteSeed(layout, record.Name, ledger);
        }
        catch (Exception ex) when (Unfinished(ex))
        {
            return new Written(
                CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Folding {Lower(Agent(record))} wrote into the main tree {progress}, and its seed could not record what it wrote: "
                    + $"{ex.Message.TrimEnd('.')}. Its rows were not applied; run it again once that is dealt with - a fold run again finds what it "
                    + "wrote already in, records it, and measures the rest afresh.",
                    Records(layout, record)),
                measured.Rows,
                seed,
                [],
                applied,
                RowsApplied: false);
        }

        // What is said of the kept copies stands beside the records on every way out from here, named as they stand once
        // the seed is written: a fold may have kept the agent's first copies.
        IReadOnlyList<string> records = [.. notes, .. Records(layout, record)];

        if (applied.Stopped is { } why)
        {
            return new Written(
                CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Folding {Lower(Agent(record))} stopped part way, {progress}: {why.TrimEnd('.')}. What it wrote is in the main tree and recorded "
                    + "as shared, and its rows were not applied; run it again once that is dealt with - a fold run again measures the rest afresh.",
                    records),
                measured.Rows,
                folded,
                notes,
                applied,
                RowsApplied: false);
        }

        if (measured.Request is not { Rows.Count: > 0 } request)
        {
            return new Written(null, measured.Rows, folded, notes, applied, RowsApplied: true);
        }

        AnchorBatch batch;

        try
        {
            batch = await _anchors.ApplyAsync(main, request, AnchorBatchMode.Apply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (Unfinished(ex))
        {
            batch = new AnchorBatch(measured.Rows.Batch.Rows, []) { Failure = ex.Message.TrimEnd('.') };
        }

        var rows = measured.Rows with { Batch = batch };

        if (!batch.Succeeded)
        {
            return new Written(
                CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"{Agent(record)} is folded into the main tree, and applying its rows failed{(batch.Failure is { } failure ? $": {failure}" : string.Empty)}. Its "
                    + "worktree is kept. Correct the cause and run again: the fold finds its own writes recorded as shared, and a row already as declared "
                    + "is not written again.",
                    [.. OrchestrationReports.RowLines(batch), .. records]),
                rows,
                folded,
                notes,
                applied,
                RowsApplied: false);
        }

        try
        {
            var kept = (_store.ReadAppliedRows(layout, record.Name)?.Rows ?? []).ToDictionary(row => row.Id, StringComparer.Ordinal);

            foreach (var row in measured.All)
            {
                kept[row.Id] = row;
            }

            _store.WriteAppliedRows(layout, record.Name, new AppliedRowsRecord { Rows = [.. kept.Values.OrderBy(row => row.Id, StringComparer.Ordinal)] });
        }
        catch (Exception ex) when (Unfinished(ex))
        {
            return new Written(
                CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"{Agent(record)} is folded into the main tree and its rows are in the registries, and the record of the rows it applied could not be "
                    + $"written: {ex.Message.TrimEnd('.')}. Run it again once that is dealt with: a row already as declared is recorded, not written again.",
                    [.. OrchestrationReports.RowLines(batch), .. records]),
                rows,
                folded,
                notes,
                applied,
                RowsApplied: true);
        }

        return new Written(null, rows, folded, notes, applied, RowsApplied: true);
    }

    /// <summary>
    /// Why a live agent's work cannot be folded as it stands - not whole, a move of its base that stopped part way, or a
    /// HEAD off its base - or its seed, where it can.
    /// </summary>
    private async Task<(CommandOutcome? Refusal, SeedRecord? Seed)> UnfoldableAsync(string main, OrchestratorLayout layout, AgentRecord record, string path, CancellationToken cancellationToken)
    {
        var seed = _store.ReadSeed(layout, record.Name);

        if (MakingUnfinished(record, seed) is { } unfinished)
        {
            return (CommandOutcome.Refused($"{Agent(record)} is not whole - making it stopped part way: {unfinished}."), null);
        }

        return await OffBaseAsync(main, record, path, cancellationToken).ConfigureAwait(false) is { } off ? (off.Refusal, null) : (null, seed);
    }

    /// <summary>
    /// Why an agent whose making stopped part way is not whole, and what finishes it or drops it; null where it is whole.
    /// The one wording of it, whichever command meets it.
    /// </summary>
    /// <param name="record">The agent's record.</param>
    /// <param name="seed">Its seed, or null where it was never seeded.</param>
    private static string? MakingUnfinished(AgentRecord record, SeedRecord? seed)
        => record.Reads ? null
        : record.Base is null
            ? $"it records no commit its worktree was made from, so nothing of it can be measured; "
                + $"{OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, "--apply --discard-uncommitted")} drops it"
            : seed is null
                ? "it was never seeded, and its seed is what tells its work from what it was handed; "
                    + $"{OrchestrationReports.Line(SeedCommand, record.Orchestrator, record.Name)} seeds it, with --empty where it is to be handed nothing"
                : null;

    /// <summary>The directories the anchor registries are kept in, relative to the tree: what an agent is refreshed with where no path is named.</summary>
    private async Task<IReadOnlyList<string>> RegistryDirectoriesAsync(HarnessContext context, CancellationToken cancellationToken)
    {
        var registries = await _anchorLocator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        return [.. registries.All
            .Select(registry => PathPatterns.Normalize(registry.RelativePath))
            .Select(relative => relative.Contains('/', StringComparison.Ordinal) ? relative[..relative.LastIndexOf('/')] : relative)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The orchestrator named, in the main checkout's context; refused where it has no record.</summary>
    private async Task<(HarnessContext Context, OrchestratorLayout Layout, OrchestratorRecord Record)> OrchestratorAsync(
        string startDirectory,
        string orchestrator,
        CancellationToken cancellationToken)
    {
        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, orchestrator);

        return _store.ReadOrchestrator(layout) is { } record
            ? (context, layout, record)
            : throw new HarnessException(HarnessExit.Refused, OrchestrationReports.NoOrchestrator(orchestrator));
    }

    /// <summary>The agent named, of the orchestrator named, with its record: every command on an existing agent starts here.</summary>
    private async Task<AgentAt> FindAsync(string startDirectory, string orchestrator, string agent, CancellationToken cancellationToken)
    {
        var (context, layout, _) = await OrchestratorAsync(startDirectory, orchestrator, cancellationToken).ConfigureAwait(false);

        return _store.ReadAgent(layout, agent) is { } record
            ? new AgentAt(null, context, layout, record)
            : new AgentAt(CommandOutcome.Refused($"Orchestrator '{orchestrator}' has no agent named '{agent}'."));
    }

    /// <summary>A live agent whose worktree git records as its own, and whose making finished: what seeding, refreshing and folding work on.</summary>
    private async Task<AgentAt> LiveAgentAsync(string startDirectory, string orchestrator, string agent, CancellationToken cancellationToken)
    {
        var found = await FindAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (found.Refusal is not null)
        {
            return found;
        }

        var (context, record) = (found.Context!, found.Record!);

        if (record.State != AgentStates.Live)
        {
            return new AgentAt(CommandOutcome.Refused(
                record.State == AgentStates.Closed
                    ? $"{Agent(record)} is closed: its deletion began, and it is never seeded, refreshed or folded again. "
                        + $"{OrchestrationReports.DeleteAgentLine(orchestrator, agent)} finishes deleting it."
                    : $"{Agent(record)} was deleted at {OrchestrationReports.Moment(record.DeletedAt!.Value)}."));
        }

        // An agent that reads the main tree is never seeded, refreshed, rebased or folded, and nothing of git is asked
        // of it: this one refusal is every verb's.
        if (record.Reads)
        {
            return new AgentAt(CommandOutcome.Refused(
                $"{Agent(record)} has no worktree: it reads the main tree, and there is nothing of it to seed, refresh, rebase or fold."));
        }

        var (path, moved) = WorktreeOf(context, record);

        if (moved is not null)
        {
            return new AgentAt(CommandOutcome.Refused(moved));
        }

        var standing = await StandingAsync(context, path, cancellationToken).ConfigureAwait(false);

        if (standing.Kind != Standing.Registered)
        {
            return new AgentAt(CommandOutcome.Refused(Unusable(record, path, standing)));
        }

        // Seeding is how an agent never seeded is made whole, so only a missing base refuses here.
        if (record.Base is null)
        {
            return new AgentAt(CommandOutcome.Refused($"{Agent(record)} is not whole - making it stopped part way: {MakingUnfinished(record, seed: null)}."));
        }

        return found with { Path = path };
    }

    /// <summary>
    /// Where an agent's worktree is: under the worktrees root it was made under, which the configuration must still name,
    /// since delete-worktree looks for it under the root the configuration names. Why not, where it does not. An agent
    /// that reads the main tree has no worktree, and no caller of this asks for one.
    /// </summary>
    private static (string Path, string? Moved) WorktreeOf(HarnessContext context, AgentRecord record)
    {
        var configured = PathPatterns.Normalize(context.Config.Worktrees.Root);
        var path = record.WorktreePath(context.Layout)!;

        return string.Equals(configured, record.WorktreesRoot, StringComparison.Ordinal)
            ? (path, null)
            : (path, $"{Agent(record)}'s worktree was made under worktrees root '{record.WorktreesRoot}', and the configuration names '{configured}' now, "
                + $"where delete-worktree looks for it. Nothing was read, written or removed: name '{record.WorktreesRoot}' as worktrees.root again while "
                + "the agent's worktree is there.");
    }

    /// <summary>What the directory at an agent's worktree path is to git; a path that cannot be looked at is never taken for one that is gone.</summary>
    private async Task<WorktreeStanding> StandingAsync(HarnessContext context, string path, CancellationToken cancellationToken)
    {
        try
        {
            switch (_fileSystem.KindOf(path))
            {
                case PathKind.None:
                    return new WorktreeStanding(Standing.Gone, null, null);
                case PathKind.File:
                    return new WorktreeStanding(Standing.Foreign, null, "it is a file, not a directory");
                case PathKind.Link:
                    return new WorktreeStanding(Standing.Foreign, null, "it is a link, and a worktree is never reached through one");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorktreeStanding(Standing.Unreadable, null, $"whether anything is there cannot be told: {ex.Message.TrimEnd('.')}");
        }

        WorktreeIdentity identity;

        try
        {
            identity = await Inspector().IdentifyAsync(context.Layout.MainCheckoutRoot, path, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return new WorktreeStanding(Standing.Unreadable, null, ex.Message);
        }

        // A husk is never read: a directory with no .git of its own is answered for by the main checkout that contains it,
        // whose HEAD and uncommitted changes would be read as the agent's.
        return identity.Membership switch
        {
            WorktreeMembership.OfThisRepository => new WorktreeStanding(Standing.Registered, identity.AdministrativeDirectory, null),
            WorktreeMembership.OfAnotherRepository => new WorktreeStanding(Standing.Foreign, null, "it is a repository of its own"),
            _ when WorktreeInspector.HoldsOwnGit(_fileSystem, path) => new WorktreeStanding(Standing.Unreadable, null, "its .git is there, and git cannot read the worktree from it"),
            _ => new WorktreeStanding(Standing.Husk, null, "it holds no .git of its own, and git answers for it with the main checkout"),
        };
    }

    /// <summary>Why an agent's worktree cannot be worked on as it stands.</summary>
    private static string Unusable(AgentRecord record, string path, WorktreeStanding standing) => standing.Kind switch
    {
        Standing.Gone => $"{Agent(record)} has no worktree at '{path}'; {OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, "--apply --discard-uncommitted")} deletes it with nothing folded.",
        Standing.Husk => $"{Agent(record)}: '{path}' is no worktree - {standing.Why} - so every question asked of it would be answered by the main tree. "
            + "Nothing was read, written or removed. What a removal that stopped part way leaves is finished by "
            + $"{OrchestrationReports.Line(WorktreeService.DeleteCommand, WorktreeAddress.Nested(record.Orchestrator, record.Name).Name, "--force")} once what it holds is kept.",
        Standing.Foreign => $"{Agent(record)}: '{path}' is not a worktree of this repository - {standing.Why}. Nothing was read, written or removed.",
        _ => $"Whether '{path}', the worktree of {Lower(Agent(record))}, is a worktree of this repository cannot be told: {standing.Why?.TrimEnd('.')}. Nothing was read, written or removed.",
    };

    /// <summary>
    /// The lock requests <paramref name="command"/> takes: each of <paramref name="trees"/> whole, on this machine. The
    /// one spelling of them, asked by the hold an <c>--apply</c> takes and by its dry run, so the two can never ask
    /// differently: where each leads (<see cref="RunLock.Key"/>), as git names a tree and as the builds before this one
    /// recorded an agent's trees, so a run of one of them asking for the tree as it spells it meets the hold.
    /// </summary>
    /// <param name="command">The command taking them.</param>
    /// <param name="runId">The run taking them.</param>
    /// <param name="trees">The trees, on this machine.</param>
    /// <exception cref="HarnessException">Where one of them leads cannot be told: refused (<see cref="RunLock.Key"/>).</exception>
    private IReadOnlyList<LockRequest> Holds(string command, RunId runId, params string[] trees)
        => [.. trees.Select(tree => new LockRequest
        {
            Host = HostId.Local.ToString(),
            Tree = _runLock.Key(HostId.Local.ToString(), tree),
            Scope = LockScope.TreeExclusive,
            RunId = runId,
            Command = command,
        })];

    /// <summary>
    /// The answer a dry run gives: what the verb composed - its lines named with who holds what its <c>--apply</c> takes,
    /// every holder, whether the dry run is otherwise refused or not - and the code the one rule gives it. As one document
    /// where <paramref name="json"/> asks.
    /// </summary>
    /// <param name="json">Whether to answer with one JSON document.</param>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="command">The command whose --apply takes the trees.</param>
    /// <param name="planned">What the verb composed: its summary and its lines, or the failure or refusal it met.</param>
    /// <param name="standing">How the request stands; null where what it weighs could not be read.</param>
    /// <param name="trees">The trees its --apply takes, by their own paths.</param>
    private CommandOutcome Dry(bool json, HarnessLayout layout, string command, CommandOutcome planned, RequestStanding? standing, params string[] trees)
        => Dry(json, layout, command, planned, standing, Typed.None, trees);

    /// <summary>The answer a dry run gives, with the consents its request lacks and the reasons it is refused for, typed.</summary>
    /// <param name="json">Whether to answer with one JSON document.</param>
    /// <param name="layout">The repository, whose main checkout holds the lock file.</param>
    /// <param name="command">The command whose --apply takes the trees.</param>
    /// <param name="planned">What the verb composed: its summary and its lines, or the failure or refusal it met.</param>
    /// <param name="standing">How the request stands; null where what it weighs could not be read.</param>
    /// <param name="typed">Each consent the request lacks and each reason it is refused for, by kind, as its measure typed them.</param>
    /// <param name="trees">The trees its --apply takes, by their own paths.</param>
    private CommandOutcome Dry(bool json, HarnessLayout layout, string command, CommandOutcome planned, RequestStanding? standing, Typed typed, params string[] trees)
    {
        ArgumentNullException.ThrowIfNull(planned);

        var holders = _treeHolders.Read(layout, Holds(command, RunId.New(), trees), [], []);

        return Dry(json, planned, standing, typed, holders);
    }

    /// <summary>The answer a dry run gives, its holders already read.</summary>
    /// <param name="json">Whether to answer with one JSON document.</param>
    /// <param name="planned">What the verb composed.</param>
    /// <param name="standing">How the request stands; null where what it weighs could not be read.</param>
    /// <param name="typed">Each consent the request lacks and each reason it is refused for, by kind, as its measure typed them.</param>
    /// <param name="holders">Who holds what its --apply takes.</param>
    private static CommandOutcome Dry(bool json, CommandOutcome planned, RequestStanding? standing, Typed typed, TreeHeld holders)
    {
        var answer = DryRuns.Answer(
            planned with { Details = [.. planned.Details ?? [], .. HolderLines(holders)] },
            standing,
            holders);

        // The facts ride the answer whatever form it is asked in: a step of a run will read them typed, and the document
        // is them serialised. The reasons follow the answer's standing, not the measure's: one the one rule made refused -
        // copies its removal cannot take - is refused for something no measure typed.
        var facts = new AgentVerbFacts(
            answer.Outcome.ExitCode,
            answer.Outcome.Message,
            Applied: false,
            answer.Standing,
            holders,
            answer.Outcome.Details ?? [],
            typed.Needs,
            Reasons(answer.Standing, typed.Reasons),
            Folded: null,
            typed.Planned);

        return json ? OrchestrationReports.Document(answer.Outcome, facts) : answer.Outcome with { Facts = facts };
    }

    /// <summary>
    /// The answer a dry run of a deletion gives, asked with the removal's own questions whatever the agent's state: the
    /// copies hosts keep of its worktree - asked for by their own requests, as the removal asks it - what would be left
    /// of the copies kept in the worktree itself, each line in its family's own words, and who holds the trees its
    /// <c>--apply</c> takes, spelled as that --apply spells them. A copy a run or a sweep holds does not refuse the
    /// --apply, which goes as far as the removal and leaves it unfinished; what is kept in the worktree that the removal
    /// cannot take as it stands leaves it unfinished too, until that is put right; a directory there no sync made goes
    /// with the worktree, and is only said. A record of host copies that cannot be read refuses it, as its --apply is
    /// refused reading that record before it writes anything (<see cref="HostCopiesOf"/>), with the lines it weighed and
    /// who holds the trees that --apply takes.
    /// </summary>
    /// <param name="json">Whether to answer with one JSON document.</param>
    /// <param name="target">The agent whose deletion is asked about.</param>
    /// <param name="planned">What the dry run composed: its summary and its lines.</param>
    /// <param name="standing">How the request stands.</param>
    /// <param name="typed">Each consent the request lacks and each reason it is refused for, by kind, as its measure typed them.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    /// <param name="trees">The trees its --apply takes, by their own paths.</param>
    private async Task<CommandOutcome> DryRemovalAsync(bool json, Target target, CommandOutcome planned, RequestStanding? standing, Typed typed, CancellationToken cancellationToken, params string[] trees)
    {
        ArgumentNullException.ThrowIfNull(planned);

        var layout = target.Context.Layout;
        var (copies, unread) = HostCopiesOf(target);

        if (unread is not null)
        {
            return Dry(json, unread with { Details = planned.Details }, RequestStanding.Refused, typed with { Reasons = [] }, _treeHolders.Read(layout, Holds(DeleteCommand, RunId.New(), trees), [], []));
        }

        var leftInIt = await _worktrees.CopiesHeldAsync(target.Address.Name, layout, target.Path, cancellationToken).ConfigureAwait(false);

        // The lock is asked once, for the trees and the hosts' copies alike, so a lock that cannot be read is said by the one
        // rule's row for it; and what is kept in the worktree that the removal cannot take as it stands is weighed by that
        // rule, in its order, with every holder: below a lock that cannot be read and a tree a run holds, either of which
        // refuses the --apply before it goes anywhere near the removal.
        return Dry(json, planned, standing, typed, _treeHolders.Read(layout, Holds(DeleteCommand, RunId.New(), trees), copies, leftInIt));
    }

    /// <summary>
    /// The locks the removal of the copies hosts keep of the agent's worktree takes, read from the record of host copies -
    /// by a dry run of the deletion, and by its --apply before it writes anything; or, where that record cannot be read,
    /// the refusal both give: no removal could tell which copies are the worktree's to remove, and one that went ahead would
    /// leave the agent closed and its removal unfinished.
    /// </summary>
    /// <param name="target">The agent being deleted.</param>
    private (IReadOnlyList<LockRequest> Copies, CommandOutcome? Unread) HostCopiesOf(Target target)
    {
        try
        {
            return (_hostCopies.RemovalLocks(target.Context, target.Address.CopyName, target.Path, RunId.New()), null);
        }
        catch (HarnessException ex)
        {
            return ([], CommandOutcome.Refused($"{Agent(target.Record)} was not deleted: {ex.Message.TrimEnd('.')}. Nothing was removed."));
        }
    }

    /// <summary>
    /// Every holder, as the dry runs' lines say them, and what the families of copies say would be left: a holder of a copy
    /// kept in the worktree is named on its family's line, never on a <c>held now:</c> line besides.
    /// </summary>
    /// <param name="holders">Who holds what the --apply takes.</param>
    private static IReadOnlyList<string> HolderLines(TreeHeld holders)
    {
        List<string> lines =
        [
            .. holders.Holders.Where(holder => !holder.HoldsKeptCopy).Select(holder => $"held now: {holder.Said}"),
            .. holders.KeptCopies.Select(line => line.Line),
        ];

        if (holders.StandingUnread is { } unread)
        {
            lines.Add($"where each run's leg stands cannot be told: {unread}");
        }

        return lines;
    }

    /// <summary>
    /// How a request stands that was answered rather than planned: sound where it went through, refused where something of
    /// it must be put right, and nothing said where what it weighs could not be read or it stopped part way.
    /// </summary>
    /// <param name="outcome">The outcome an --apply met.</param>
    private static RequestStanding? StandingOfApply(CommandOutcome outcome)
        => outcome.Succeeded
            ? RequestStanding.Sound
            : outcome.ExitCode is HarnessExit.Refused or HarnessExit.UsageError ? RequestStanding.Refused : null;

    /// <summary>
    /// What fold-agent, refresh-agent, rebase-agent or delete-agent answers once its outcome is in: one document where
    /// <paramref name="json"/> asks and the outcome brings none - an --apply refused at its hold sound, nothing in it to
    /// correct, the run it met in heldNow, as its dry run at that moment answers; any other as it stands, nobody asked -
    /// and the outcome itself otherwise. Either way the answer carries its facts, composed here: what a dry run already
    /// knew keeps what it knew, and what an --apply wrote rides with it.
    /// </summary>
    /// <param name="answered">The outcome, with its log's line where an --apply wrote one.</param>
    /// <param name="apply">Whether --apply was given.</param>
    /// <param name="json">Whether to answer with one JSON document.</param>
    /// <param name="met">The run the --apply's hold met, where one was in its way, and what it folded, where it folded.</param>
    private static CommandOutcome Answered(CommandOutcome answered, bool apply, bool json, HoldersMet met)
    {
        var had = answered.Facts as AgentVerbFacts;
        var standing = had?.Standing ?? (met.Held is null ? StandingOfApply(answered) : RequestStanding.Sound);
        var facts = new AgentVerbFacts(
            answered.ExitCode,
            answered.Message,
            apply,
            standing,
            had?.Holders ?? met.Held,
            answered.Details ?? [],
            had?.Needs ?? [],
            had?.Refusals ?? Reasons(standing, met.Reasons),
            met.Folded ?? had?.Folded,
            had?.Planned ?? met.Planned);

        return json && answered.Data.Count == 0
            ? OrchestrationReports.Document(answered, facts)
            : answered with { Facts = facts };
    }

    /// <summary>
    /// What fold-agent, refresh-agent, rebase-agent or delete-agent answers where it stopped before anything was weighed -
    /// its arguments a usage error, its agent not found or not one the verb works on: facts that say exactly that -
    /// nothing applied, no standing, nobody asked, no lines, nothing needed and no reason typed - and, where
    /// <paramref name="json"/> asks, the document's stopped form, which says the same as the command's plumbing says it
    /// for every exit that brings no document.
    /// </summary>
    /// <param name="stopped">The usage error or the refusal it stopped with.</param>
    /// <param name="json">Whether to answer with one JSON document.</param>
    private static CommandOutcome Answered(CommandOutcome stopped, bool json)
    {
        var facts = new AgentVerbFacts(stopped.ExitCode, stopped.Message, Applied: false, Standing: null, Holders: null, Lines: [], Needs: [], Refusals: []);

        return json && stopped.Data.Count == 0
            ? stopped with { Data = [OrchestrationReports.Stopped(stopped.ExitCode, stopped.Message)], Quiet = true, Facts = facts }
            : stopped with { Facts = facts };
    }

    /// <summary>
    /// Takes each of <paramref name="trees"/> on this machine whole, as a sync takes a copy: no leg builds in it, and no other
    /// fold, seed or deletion writes it, meanwhile. All of them, or none; the run's id names what it keeps. Where a run is in
    /// the way, <paramref name="met"/> - where given - is told that run, as a dry run at that moment names it.
    /// </summary>
    private async Task<Hold> HoldAsync(HarnessLayout layout, string command, HoldersMet? met, CancellationToken cancellationToken, params string[] trees)
    {
        var runId = RunId.New();
        var handles = new List<RunLockHandle>();

        foreach (var request in Holds(command, runId, trees))
        {
            var attempt = await _runLock.TryAcquireAsync(layout, request, cancellationToken).ConfigureAwait(false);

            if (attempt.Handle is not { } handle)
            {
                await new Handles(handles).DisposeAsync().ConfigureAwait(false);
                var refused = _treeHolders.Refused(request, attempt.Holder!);

                if (met is not null)
                {
                    met.Held = refused.Held;
                }

                return new Hold(new Handles([]), CommandOutcome.Refused($"{refused.Said} Nothing was changed; run it again once that is done.")
                with
                {
                    // A run in the way is waited out, not put right: the caller that can try again knows to.
                    Cause = OutcomeCause.LockHeld,
                }, runId);
            }

            handles.Add(handle);
        }

        return new Hold(new Handles(handles), null, runId);
    }

    /// <summary>
    /// Copies the agent's Claude transcripts into its orchestrator's logs directory: the lines that say what was kept and
    /// where, and why a transcript found could not be kept. None found, or a directory that could not be looked in, is said
    /// and stops nothing.
    /// </summary>
    private async Task<(IReadOnlyList<string> Lines, string? Failed)> TranscriptsAsync(OrchestratorLayout layout, AgentRecord record)
    {
        if (record.Session is null)
        {
            return ([TranscriptsPlan(record)], null);
        }

        var kept = await _transcripts.CopyAsync(record.Session, layout.TranscriptsDirectory(record.Name), CancellationToken.None).ConfigureAwait(false);
        var lines = new List<string>
        {
            kept.Kept.Count == 0 && kept.Failed is null
                ? $"no transcript of session {record.Session} was found under '{kept.Searched}'"
                : $"kept {ReportText.Counted(kept.Kept.Count, "transcript file")} of session {record.Session} in '{layout.TranscriptsDirectory(record.Name)}', each read back",
        };

        lines.AddRange(kept.Unread.Select(unread => $"a directory transcripts may be in could not be looked in: {unread}"));

        return (lines, kept.Failed);
    }

    private static string TranscriptsPlan(AgentRecord record)
        => record.Session is null
            ? $"no Claude session is recorded for it, so no transcript is looked for; {OrchestrationReports.Line(CreateCommand, record.Orchestrator, record.Name, "--model", record.Model, "--session", "<id>")} records one"
            : $"transcripts: session {record.Session}'s, where Claude Code keeps them";

    private static string EvidenceLine(EvidenceKept kept, string destination)
        => (kept.Kept.Count == 0 ? "its evidence roots held nothing to keep" : $"kept {ReportText.Counted(kept.Kept.Count, "evidence file")} in '{destination}', each proved")
            + (kept.Linked.Count == 0 ? string.Empty : $"; {string.Join(", ", kept.Linked)} {(kept.Linked.Count == 1 ? "is a link" : "are links")}, and what a link leads to stays where it is");

    /// <summary>
    /// What a hand-over leaves out, said: an untracked directory git will not look into, and a symbolic link the main tree
    /// committed, are never handed to an agent.
    /// </summary>
    private static IEnumerable<string> NotHanded(Handable handable)
        => [
            .. handable.Directories.Count == 0
                ? Array.Empty<string>()
                : [$"not handed: {ReportText.Listed(handable.Directories)} - a directory git will not look into, a repository of its own, which a fold never moves"],
            .. handable.Links.Count == 0
                ? Array.Empty<string>()
                : [$"not handed: {ReportText.Listed(handable.Links)} - a symbolic link the main tree committed, never handed as the file it leads to; rebase-agent "
                    + "brings it in as git holds it"],
        ];

    /// <summary>
    /// A hand-over refused for what the agent holds of its own in its way, <paramref name="own"/>: written over, or through
    /// a link out of its worktree.
    /// </summary>
    private static CommandOutcome InTheWay(string doing, AgentRecord record, IReadOnlyList<string> own)
        => CommandOutcome.Refused(
            $"{doing} {Lower(Agent(record))} would write over or through {ReportText.Counted(own.Count, "path")} of its own - {ReportText.Listed(own)} - where the main tree "
            + "holds a directory, or a file in place of the directory they are in: move them aside, and run again. Nothing was written.");

    /// <summary>What brings in what a live agent is refused for in a submodule of its worktree, where the main tree committed it.</summary>
    private const string ByRebasing = "where the main tree committed them, rebase-agent brings them in as git holds them";

    /// <summary>What a hand-over refused before anything was written leaves.</summary>
    private const string NothingWritten = "Nothing was written.";

    /// <summary>
    /// A hand-over refused for what it would write into or remove from a submodule of the agent's worktree,
    /// <paramref name="paths"/>: another repository, which no hand-over writes into.
    /// </summary>
    /// <param name="doing">What would hand them over, to open the sentence.</param>
    /// <param name="agent">The agent's name.</param>
    /// <param name="orchestrator">Its orchestrator's name.</param>
    /// <param name="paths">Each path, with the submodule it is or lies in.</param>
    /// <param name="remedy">What brings them in instead.</param>
    /// <param name="nothing">What was left as it was, as a sentence.</param>
    private static CommandOutcome InSubmodules(string doing, string agent, string orchestrator, IReadOnlyList<SubmodulePath> paths, string remedy, string nothing)
        => CommandOutcome.Refused(
            $"{doing} agent '{agent}' of '{orchestrator}' would write into or remove from {ReportText.Counted(paths.Count, "path")} of a submodule of its worktree - "
            + $"{ReportText.Listed([.. paths.Select(path => path.Path == path.Submodule ? $"'{path.Path}'" : $"'{path.Path}' in '{path.Submodule}'")])} - a "
            + $"repository of its own, which a hand-over never writes into or removes from: {remedy}. {nothing}");

    /// <summary>Where the agent's records are, as every exit after one is written names them: each that is there.</summary>
    private string[] Records(OrchestratorLayout layout, AgentRecord record)
        => [
            $"record {layout.AgentRecordFile(record.Name)}",
            .. SeedRecords(layout, record.Name),
            .. _fileSystem.FileExists(layout.AppliedRowsFile(record.Name)) ? [$"applied rows {layout.AppliedRowsFile(record.Name)}"] : Array.Empty<string>(),
        ];

    /// <summary>
    /// Where the agent's seed and the bytes kept beside it are, as every exit of a command that writes them names them: each
    /// that is there.
    /// </summary>
    private string[] SeedRecords(OrchestratorLayout layout, string agent)
        => [
            .. _fileSystem.FileExists(layout.SeedFile(agent)) ? [$"seed {layout.SeedFile(agent)}"] : Array.Empty<string>(),
            .. _fileSystem.DirectoryExists(layout.SharedDirectory(agent)) ? [$"shared {layout.SharedDirectory(agent)}"] : Array.Empty<string>(),
        ];

    private string? StandsIn(string path)
    {
        var here = _processContext.CurrentDirectory;

        try
        {
            return PathContainment.IsInsideOrSame(_fileSystem.ResolveLinks(path), _fileSystem.ResolveLinks(here), _platform.PathComparison) ? here : null;
        }
        catch (IOException)
        {
            return PathContainment.IsInsideOrSame(path, here, _platform.PathComparison) ? here : null;
        }
    }

    private WorktreeInspector Inspector() => new(_gitClient, _fileSystem, _platform, _output);

    /// <summary>
    /// The recorder of what <paramref name="agent"/> shares with the main tree, starting from <paramref name="seed"/>, its
    /// bytes kept beside its seed.
    /// </summary>
    private SharedLedger Ledger(OrchestratorLayout layout, string agent, SeedRecord seed) => new(seed, Copies(layout, agent));

    /// <summary>The bytes <paramref name="agent"/> shares with the main tree, kept beside its seed.</summary>
    private SharedCopies Copies(OrchestratorLayout layout, string agent) => new(_fileSystem, _permissions, layout, agent);

    /// <summary>What measures a three-way merge of a path both trees changed, for one agent and one command.</summary>
    /// <param name="context">The repository, whose main checkout's history is read where no copy of shared bytes was kept.</param>
    /// <param name="layout">The agent's orchestrator.</param>
    /// <param name="agent">The agent, whose kept copies are what both trees held at a shared path.</param>
    /// <param name="keeping">Whether bytes found in that history are kept: true of a command that goes on to write.</param>
    private TwoChanges Changes(HarnessContext context, OrchestratorLayout layout, string agent, bool keeping)
        => new(_gitClient, _fileSystem, _sync, Copies(layout, agent), context.Layout.MainCheckoutRoot, keeping);

    /// <summary>
    /// Writes what <paramref name="ledger"/> recorded as the agent's seed, then prunes the bytes kept beside it to what that
    /// seed names: the last two of a command's writes, after the trees and the kept copies, so that, stopped anywhere, the
    /// seed never names a path the trees do not hold as recorded. Every command that writes a seed writes it here.
    /// </summary>
    /// <returns>
    /// The seed written, and what is said of the kept copies: each path whose bytes could not be kept, and each copy the
    /// seed no longer names that could not be removed.
    /// </returns>
    private (SeedRecord Seed, IReadOnlyList<string> Notes) WriteSeed(OrchestratorLayout layout, string agent, SharedLedger ledger)
    {
        var seed = ledger.Build();
        _store.WriteSeed(layout, agent, seed);

        return (seed, [.. ledger.NotKept, .. ledger.Kept.Prune(seed)]);
    }

    private static IReadOnlyList<string> Floor(HarnessContext context) => TreeFloor.Of(context.Config.Worktrees.Root);

    private static string? Shape(string orchestrator, string agent)
        => OrchestrationRules.NameProblem(orchestrator)
            ?? OrchestrationRules.NameProblem(agent)
            ?? OrchestrationRules.AgentNameProblem(orchestrator, agent);

    /// <summary>What is wrong with paths given on the command line, asked of each as given, before anything tidies it.</summary>

    /// <summary>
    /// Whether <paramref name="exception"/> is one a step after a first write can meet and must answer, rather than let
    /// escape: the file system's, git's and the tool's own refusals, a record that does not read, and a configuration that
    /// no longer loads - a fold can bring a change of it into the main tree.
    /// </summary>
    private static bool Unfinished(Exception exception)
        => exception is IOException or UnauthorizedAccessException or HarnessException or JsonException or ConfigException;

    private static string Agent(AgentRecord record) => $"Agent '{record.Name}' of '{record.Orchestrator}'";

    /// <summary>A sentence's opening words, to follow others.</summary>
    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>
    /// An agent that cannot be worked with as it stands: where a move of its base that its record names stopped part way was
    /// going, which rebase-agent finishes, or none - a HEAD off its base; and why it cannot be folded, deleted, seeded or
    /// refreshed as it stands - nor moved, where no move is under way.
    /// </summary>
    private sealed record HeadMoved
    {
        private HeadMoved(string? stoppedAt, CommandOutcome refusal) => (StoppedAt, Refusal) = (stoppedAt, refusal);

        /// <summary>Where a move of its base that its record says stopped part way was going; null where it is no such move.</summary>
        public string? StoppedAt { get; }

        /// <summary>Why it cannot be worked with as it stands.</summary>
        public CommandOutcome Refusal { get; }

        /// <summary>A move of its base that its record says stopped part way, going to <paramref name="at"/>.</summary>
        public static HeadMoved Stopped(string at, CommandOutcome refusal) => new(at, refusal);

        /// <summary>Anything else: nothing here finishes it.</summary>
        public static HeadMoved Refusing(CommandOutcome refusal) => new(null, refusal);
    }

    /// <summary>What the directory at an agent's worktree path is.</summary>
    private enum Standing
    {
        Registered,
        Gone,
        Husk,
        Foreign,
        Unreadable,
    }

    /// <summary>What the directory at an agent's worktree path is, its git directory where it is a worktree, and why where it is not usable.</summary>
    private sealed record WorktreeStanding(Standing Kind, string? AdministrativeDirectory, string? Why);

    /// <summary>An agent found - or a live one to work on, with its worktree - or why it cannot be.</summary>
    private sealed record AgentAt(
        CommandOutcome? Refusal,
        HarnessContext? Context = null,
        OrchestratorLayout? Layout = null,
        AgentRecord? Record = null,
        string? Path = null);

    /// <summary>An agent being deleted, and what its worktree is.</summary>
    private sealed record Target(HarnessContext Context, OrchestratorLayout Layout, AgentRecord Record, WorktreeAddress Address, string Path, WorktreeStanding Standing);

    /// <summary>
    /// An agent's fold and rows, measured: the rows declared anew with what applying them is allowed - null where they were
    /// never weighed - every row declared, and why they cannot be written where they cannot.
    /// </summary>
    private sealed record Measured(FoldPlan? Plan, AnchorBatchRequest? Request, IReadOnlyList<AnchorRowDeclaration> All, AgentRowsPlan Rows, CommandOutcome? Refusal);

    /// <summary>
    /// What writing a measured fold answered, and what it did, on every exit: why it stopped where it did, its rows as they
    /// were applied, its seed as recorded, the fold's own paths as far as it wrote them, and its notes.
    /// </summary>
    /// <param name="Failure">Why it stopped part way; null where it wrote everything.</param>
    /// <param name="Rows">The rows as applying them left them.</param>
    /// <param name="Seed">The seed as recorded.</param>
    /// <param name="Notes">What is said of the kept copies beside the records.</param>
    /// <param name="Applied">What the fold wrote, merged, removed and handed into the trees, as far as it went - all of it, or part where it stopped part way.</param>
    /// <param name="RowsApplied">Whether its rows are in the registries: false where it stopped before applying them, or applying them failed and was put back.</param>
    private sealed record Written(CommandOutcome? Failure, AgentRowsPlan Rows, SeedRecord Seed, IReadOnlyList<string> Notes, FoldApplied Applied, bool RowsApplied);

    /// <inheritdoc cref="IAgentService.CreateAsync(AgentCreateRequest, CancellationToken)"/>
    public async Task<UseCaseResult<AgentCreated>> CreateAsync(AgentCreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UseCaseResult<AgentCreated>.From(
            await CreateAsync(
                request.StartDirectory,
                request.Orchestrator,
                request.Agent,
                request.Model,
                request.Empty,
                request.Session,
                request.NoWorktree,
                cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc cref="IAgentService.SeedAsync(AgentSeedRequest, CancellationToken)"/>
    public async Task<UseCaseResult<AgentSeeded>> SeedAsync(AgentSeedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UseCaseResult<AgentSeeded>.From(
            await SeedAsync(request.StartDirectory, request.Orchestrator, request.Agent, request.Empty, request.Force, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc cref="IAgentService.RefreshAsync(RefreshAgentRequest, CancellationToken)"/>
    public async Task<UseCaseResult<AgentVerbFacts>> RefreshAsync(RefreshAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UseCaseResult<AgentVerbFacts>.From(
            await RefreshAsync(request.StartDirectory, request.Orchestrator, request.Agent, request.Request, request.Apply, cancellationToken, request.Json).ConfigureAwait(false));
    }

    /// <inheritdoc cref="IAgentService.RebaseAsync(RebaseAgentRequest, CancellationToken)"/>
    public async Task<UseCaseResult<AgentVerbFacts>> RebaseAsync(RebaseAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UseCaseResult<AgentVerbFacts>.From(
            await RebaseAsync(request.StartDirectory, request.Orchestrator, request.Agent, request.Request, request.Apply, cancellationToken, request.Json).ConfigureAwait(false));
    }

    /// <inheritdoc cref="IAgentService.FoldAsync(FoldAgentRequest, CancellationToken)"/>
    public async Task<UseCaseResult<AgentVerbFacts>> FoldAsync(FoldAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UseCaseResult<AgentVerbFacts>.From(
            await FoldAsync(request.StartDirectory, request.Orchestrator, request.Agent, request.Allowances, request.Apply, cancellationToken, request.Json).ConfigureAwait(false));
    }

    /// <inheritdoc cref="IAgentService.DeleteAsync(DeleteAgentRequest, CancellationToken)"/>
    public async Task<UseCaseResult<AgentVerbFacts>> DeleteAsync(DeleteAgentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UseCaseResult<AgentVerbFacts>.From(
            await DeleteAsync(request.StartDirectory, request.Orchestrator, request.Agent, request.Allowances, request.Apply, request.DiscardUncommitted, cancellationToken, request.Json).ConfigureAwait(false));
    }

    /// <summary>What deleting a live agent measured before writing anything, and why it cannot go on where it cannot.</summary>
    private sealed record ClosingMeasure(Measured? Fold, IReadOnlyList<string> Discarded, EvidenceFound Evidence, CommandOutcome? Refusal);

    /// <summary>The trees held, or why they could not be, and the run's id.</summary>
    private sealed record Hold(Handles Handles, CommandOutcome? Refusal, RunId Run);

    /// <summary>
    /// The run an --apply's hold met in its way, told by the hold to the verb that answers with one document, so its heldNow
    /// names the run its refusal names.
    /// </summary>
    private sealed class HoldersMet
    {
        /// <summary>The run met, as a dry run at that moment names it; <see langword="null"/> where no hold was refused.</summary>
        public TreeHeld? Held { get; set; }

        /// <summary>What a fold applied, where this --apply wrote one; <see langword="null"/> where it folded nothing.</summary>
        public AgentFolded? Folded { get; set; }

        /// <summary>What the plan of a refresh or a move of an agent's base names, once it is measured; <see langword="null"/> before.</summary>
        public AgentPlanned? Planned { get; set; }

        /// <summary>The reasons the --apply's own measure was refused for, by kind, where it typed any; empty otherwise.</summary>
        public IReadOnlyList<RefusalKind> Reasons { get; set; } = [];
    }

    /// <summary>
    /// What a request's measure typed: each consent it lacks and each reason it is refused for, by kind, which its standing
    /// is typed by, and what its plan names where it is a refresh's or a move of an agent's base.
    /// </summary>
    /// <param name="Needs">Each consent the request lacks, by kind, and nothing else.</param>
    /// <param name="Reasons">Each reason its measure refused it for, by kind; empty where it typed none.</param>
    /// <param name="Planned">What the plan of a refresh or a move of an agent's base names; <see langword="null"/> where none was measured.</param>
    private sealed record Typed(IReadOnlyList<ConsentKind> Needs, IReadOnlyList<RefusalKind> Reasons, AgentPlanned? Planned = null)
    {
        /// <summary>Nothing lacked and nothing typed.</summary>
        public static Typed None { get; } = new([], []);
    }

    /// <summary>Locks let go together, in the reverse of the order they were taken.</summary>
    private sealed class Handles(IReadOnlyList<RunLockHandle> handles) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var handle in handles.Reverse())
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
