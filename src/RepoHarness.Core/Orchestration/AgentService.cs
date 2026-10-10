using System.Text.Json;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Orchestration;

/// <summary>Creating, seeding, refreshing, rebasing, folding and deleting an orchestrator's agents.</summary>
public interface IAgentService
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
    /// <paramref name="apply"/>.
    /// </summary>
    Task<CommandOutcome> RefreshAsync(string startDirectory, string orchestrator, string agent, RefreshRequest request, bool apply, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a live agent's base to the main tree's HEAD: what the main tree committed since comes into its worktree as git
    /// holds it, and its own changes and what it was handed stay; refused, moving nothing, where it changed a path the main
    /// tree committed a change to since, unless that path is one of <paramref name="settled"/>, reconciled by hand. A dry
    /// run until <paramref name="apply"/>; run again after one that stopped part way, it finishes it.
    /// </summary>
    Task<CommandOutcome> RebaseAsync(string startDirectory, string orchestrator, string agent, IReadOnlyList<string> settled, bool apply, CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds a live agent's work into the main tree, then applies the anchor rows it declared anew: every path it changed
    /// or shares with the main tree, and every row, is measured first, and nothing is written while any is refused. A dry
    /// run until <paramref name="apply"/>. Its worktree is never removed, and what the fold wrote is recorded as shared: a
    /// review can still send it back. <paramref name="allowances"/> is what the person folding it lets through that the
    /// fold otherwise refuses.
    /// </summary>
    Task<CommandOutcome> FoldAsync(string startDirectory, string orchestrator, string agent, FoldAllowances allowances, bool apply, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an agent: folds what is left of its work and applies its rows - or, where
    /// <paramref name="discardUncommitted"/>, abandons it with nothing folded - keeps its Claude transcripts in its
    /// orchestrator's logs directory and its evidence in its own directory, each proved, closes it, and removes its
    /// worktree and the copies hosts keep of it, never forced; its directory stays as its history. Run again for a closed
    /// agent, it folds nothing and finishes the removal. A dry run until <paramref name="apply"/>. <paramref name="allowances"/>
    /// is what the person deleting it lets its fold through that it otherwise refuses.
    /// </summary>
    Task<CommandOutcome> DeleteAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        FoldAllowances allowances,
        bool apply,
        bool discardUncommitted,
        CancellationToken cancellationToken = default);
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
/// <param name="runLock">Holds the main tree, and an agent's worktree, while they are written.</param>
/// <param name="anchorLocator">Where the anchor registries are, which refresh-agent refreshes by default.</param>
/// <param name="anchors">Applies an agent's rows.</param>
/// <param name="transcripts">Finds and keeps an agent's Claude transcripts.</param>
/// <param name="store">The records.</param>
/// <param name="log">The logs.</param>
/// <param name="clock">When things happen.</param>
/// <param name="currentDirectory">This process's working directory; the process's own where none is given.</param>
public sealed class AgentService(
    IHarnessContextLoader contextLoader,
    IGitClient gitClient,
    IFileSystem fileSystem,
    IHostPlatform platform,
    IHarnessOutput output,
    IFilePermissions permissions,
    IWorktreeService worktrees,
    RunLock runLock,
    IAnchorRegistryLocator anchorLocator,
    IAnchorRegistryService anchors,
    ClaudeTranscripts transcripts,
    OrchestrationStore store,
    OrchestrationLog log,
    TimeProvider clock,
    Func<string>? currentDirectory = null) : IAgentService
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
    private readonly IWorktreeService _worktrees = worktrees;
    private readonly RunLock _runLock = runLock;
    private readonly IAnchorRegistryLocator _anchorLocator = anchorLocator;
    private readonly IAnchorRegistryService _anchors = anchors;
    private readonly ClaudeTranscripts _transcripts = transcripts;
    private readonly OrchestrationStore _store = store;
    private readonly OrchestrationLog _log = log;
    private readonly TimeProvider _clock = clock;
    private readonly Func<string> _currentDirectory = currentDirectory ?? Directory.GetCurrentDirectory;
    private readonly AgentFold _fold = new(gitClient, fileSystem, permissions, platform);
    private readonly AgentEvidence _evidence = new(fileSystem, platform);

    public async Task<CommandOutcome> CreateAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        string model,
        bool empty,
        string? session,
        CancellationToken cancellationToken = default)
    {
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
            return Again(layout, existing, model, empty, session);
        }

        var main = context.Layout.MainCheckoutRoot;
        var address = WorktreeAddress.Nested(orchestrator, agent);
        var listed = await _worktrees.ListAsync(main, cancellationToken).ConfigureAwait(false);
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

            var open = OrchestrationRules.OpenAgents(orchestrator, _store.Agents(layout), listed.Select(worktree => worktree.Name));

            if (open.Count >= current.Parallel)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{orchestrator}' already has {open.Count} open agent(s), its limit: {string.Join(", ", open)}. "
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

        // Until its worktree is made, the agent's directory holds only its place, which every way out before then gives
        // back - a refusal, an interruption, or a hand-over refused - and none after, when the worktree is there.
        Hold hold;
        Handable handable;

        try
        {
            // Seeding reads the main tree, which a fold writes: held while the worktree is made and handed what it is
            // handed, so the seed is one moment of it.
            hold = await HoldAsync(context.Layout, CreateCommand, cancellationToken, main).ConfigureAwait(false);

            if (hold.Refusal is { } held)
            {
                return _log.Record(layout, orchestrator, CreateCommand, GiveBack(layout, agent, held));
            }

            try
            {
                // What it is to be handed is checked before its worktree is made: a hand-over refused leaves nothing behind.
                handable = empty ? Handable.None : await _fold.HandableAsync(main, Floor(context), within: null, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await hold.Handles.DisposeAsync().ConfigureAwait(false);
                throw;
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

            return _log.Record(layout, agent, CreateCommand, await FinishMakingAsync(context, layout, agent, created, handable, empty).ConfigureAwait(false));
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
        bool empty)
    {
        var orchestrator = layout.Name;
        var what = $"Agent '{agent}' of '{orchestrator}' was created at '{created.Path}'";
        var records = new List<string> { $"record {layout.AgentRecordFile(agent)}" };
        var based = false;

        try
        {
            if (created.BaseCommit is not { } baseCommit)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"{what}, and the commit its worktree was made from cannot be read, so nothing of it can be measured. "
                    + $"{OrchestrationReports.DeleteAgentLine(orchestrator, agent, "--apply --discard-uncommitted")} drops it.",
                    records);
            }

            _store.UpdateAgent(layout, agent, current => current with { Base = baseCommit });
            based = true;
            _fileSystem.CreateDirectory(layout.WorkDirectory(agent));
            _fileSystem.CreateDirectory(layout.PlansDirectory(agent));
            _fileSystem.CreateDirectory(layout.RowsDirectory(agent));

            var start = new SeedRecord { SeededAt = _clock.GetUtcNow(), Empty = empty, Paths = [] };
            var (seed, handed, stopped) = await _fold.HandAsync(context.Layout.MainCheckoutRoot, created.Path, handable, start, CancellationToken.None).ConfigureAwait(false);
            _store.WriteSeed(layout, agent, seed);
            records.Add($"seed {layout.SeedFile(agent)}");

            if (stopped is not null)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"{what}, and handing it the main tree's uncommitted state stopped after {handed} of {handable.Count} path(s): {stopped}. "
                    + $"Its seed records the {handed} it was handed. {Remedy(orchestrator, agent, based)}",
                    records);
            }

            return CommandOutcome.Ok(
                $"created agent '{agent}' of orchestrator '{orchestrator}'",
                [
                    created.Path,
                    $"base {OrchestrationReports.Base(baseCommit)}",
                    seed.Empty ? "handed nothing, as asked" : $"handed {handed} path(s) of the main tree's uncommitted state",
                    .. NotHanded(handable),
                    .. records,
                    $"plans {layout.PlansDirectory(agent)}",
                    $"work {layout.WorkDirectory(agent)}",
                    $"rows {layout.RowsDirectory(agent)}",
                ]);
        }
        catch (Exception ex) when (Unfinished(ex))
        {
            return CommandOutcome.Failed(
                HarnessExit.Incomplete,
                $"{what}, and making it did not finish: {ex.Message.TrimEnd('.')}. {Remedy(orchestrator, agent, based)}",
                records);
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
    private CommandOutcome Again(OrchestratorLayout layout, AgentRecord existing, string model, bool empty, string? session)
    {
        if (existing.State == AgentStates.Deleted)
        {
            return CommandOutcome.Refused(
                $"{Agent(existing)} was deleted at {OrchestrationReports.Moment(existing.DeletedAt!.Value)}, and its directory is kept as its history: "
                + "an agent's name is never used twice. Choose another name.");
        }

        if (!string.Equals(existing.Model, model, StringComparison.Ordinal) || empty)
        {
            return CommandOutcome.Refused(
                $"{Agent(existing)} exists, created for model '{existing.Model}'. Run again, create-agent records only a session (--session); "
                + $"{OrchestrationReports.Line(SeedCommand, existing.Orchestrator, existing.Name)} seeds it again. Nothing was changed.");
        }

        // An agent whose making stopped part way is said to be one, never "already as asked".
        if (existing.State == AgentStates.Live && MakingUnfinished(existing, _store.ReadSeed(layout, existing.Name)) is { } unfinished)
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
        var seedLine = $"seed {layout.SeedFile(record.Name)}";
        var previous = _store.ReadSeed(layout, record.Name);
        var unseeded = new SeedRecord { SeededAt = _clock.GetUtcNow(), Empty = empty, Paths = [] };
        Handable handable;

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
                        $"{Agent(record)} already holds {own.Count} changed path(s) of its own - {ReportText.Listed(own)} - and seeding it now would overwrite "
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandOutcome.Failed(HarnessExit.CommandFailed, $"{Agent(record)} was not seeded: what it or the main tree holds cannot be read - {ex.Message.TrimEnd('.')}. Nothing was written.");
        }

        // The agent's worktree is held too: a leg building in it would be building what is being replaced.
        var hold = await HoldAsync(context.Layout, SeedCommand, cancellationToken, main, _fileSystem.ResolveLinks(path)).ConfigureAwait(false);

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

            try
            {
                SeedRecord seed;
                (seed, handed, stopped) = await _fold.HandAsync(main, path, handable, start, CancellationToken.None).ConfigureAwait(false);
                _store.WriteSeed(layout, record.Name, seed);
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Seeding {Lower(Agent(record))} copied what it could and could not record it: {ex.Message.TrimEnd('.')}. Its worktree may hold "
                    + $"copies its seed does not name; run {OrchestrationReports.Line(SeedCommand, record.Orchestrator, record.Name, "--force")} again once that is dealt with.",
                    [seedLine]);
            }

            if (stopped is not null)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Seeding {Lower(Agent(record))} stopped after {handed} of {handable.Count} path(s): {stopped}. Its seed records what it was handed; "
                    + $"run {OrchestrationReports.Line(SeedCommand, record.Orchestrator, record.Name, "--force")} again once that is dealt with.",
                    [seedLine]);
            }

            if (empty)
            {
                return CommandOutcome.Ok(
                    previous is { } before && before.Weighed.Any()
                        ? $"seeded {Lower(Agent(record))} with nothing more, as asked: what it was handed before stays recorded"
                        : $"seeded {Lower(Agent(record))} with nothing, as asked",
                    [.. await BehindAsync(main, record).ConfigureAwait(false), seedLine]);
            }

            return CommandOutcome.Ok(
                $"seeded {Lower(Agent(record))} with {handed} path(s)",
                [.. NotHanded(handable), .. await BehindAsync(main, record).ConfigureAwait(false), seedLine]);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if ((Shape(orchestrator, agent) ?? request.Problem()) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var at = await LiveAgentAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (at.Refusal is { } refusal)
        {
            return refusal;
        }

        var outcome = await RefreshLiveAsync(at, request, apply, cancellationToken).ConfigureAwait(false);
        return apply ? _log.Record(at.Layout!, agent, RefreshCommand, outcome) : outcome;
    }

    /// <summary>Refreshing a live agent, once it is found.</summary>
    private async Task<CommandOutcome> RefreshLiveAsync(AgentAt at, RefreshRequest request, bool apply, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (at.Context!, at.Layout!, at.Record!, at.Path!);
        var floor = Floor(context);
        var main = context.Layout.MainCheckoutRoot;
        var seedLine = $"seed {layout.SeedFile(record.Name)}";

        if (_store.ReadSeed(layout, record.Name) is not { } seed)
        {
            return CommandOutcome.Refused($"{Agent(record)} is not whole - making it stopped part way: {MakingUnfinished(record, seed: null)}.");
        }

        // What it is handed is weighed against its base: never while a move of that base stands part way, or with its HEAD
        // elsewhere.
        if (await OffBaseAsync(main, record, path, cancellationToken).ConfigureAwait(false) is { } off)
        {
            return off.Refusal;
        }

        // Every path the main tree moved, off the floor, where all are asked for: no prefix names them.
        IReadOnlyList<string>? prefixes = request.All
            ? null
            : request.Paths.Count > 0 ? [.. request.Paths.Select(PathPatterns.Normalize)] : await RegistryDirectoriesAsync(context, cancellationToken).ConfigureAwait(false);

        if (prefixes?.FirstOrDefault(prefix => TreeFloor.Covers(floor, prefix)) is { } floored)
        {
            return CommandOutcome.Usage($"'{floored}' is never handed to an agent: {string.Join(", ", floor)} stay in their own tree.");
        }

        var under = prefixes is null ? string.Empty : $" under {string.Join(", ", prefixes)}";
        var except = request.Except.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal);
        Handable handable;
        Handable moved;
        IReadOnlyList<string> changed;
        IReadOnlyList<string> left;
        IReadOnlyList<string> inTheWay;

        try
        {
            // Every path the main tree holds otherwise than the agent shares it, committed or not: a copy it was handed goes
            // stale as soon as the main tree moves it, back to its HEAD's bytes included.
            handable = await _fold.MovedAsync(main, record.Base!, seed, floor, prefixes, everyUncommitted: false, cancellationToken).ConfigureAwait(false);
            moved = handable with
            {
                Files = await OtherAsync(handable.Files).ConfigureAwait(false),
                Deletions = await OtherAsync(handable.Deletions).ConfigureAwait(false),
            };

            // Never over a change of the agent's own - an edit, or a deletion - handed to it or not, nor over or through
            // anything of its own where what it is handed needs room. One left as it is by name is handed nothing, and is in
            // the way of what is handed like anything else of the agent's.
            var edited = await _fold.EditedAsync(path, record.Base!, seed, [.. moved.Files, .. moved.Deletions], cancellationToken).ConfigureAwait(false);
            left = [.. edited.Where(except.Contains)];
            changed = [.. edited.Where(relative => !except.Contains(relative))];
            moved = moved with
            {
                Files = [.. moved.Files.Where(relative => !except.Contains(relative))],
                Deletions = [.. moved.Deletions.Where(relative => !except.Contains(relative))],
            };
            inTheWay = _fold.InTheWayOfHanding(path, moved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CommandOutcome.Failed(HarnessExit.CommandFailed, $"{Agent(record)} was not refreshed: what it or the main tree holds cannot be read - {ex.Message.TrimEnd('.')}. Nothing was copied.");
        }

        // Never silent, and never a guess: a name that leaves nothing out is the typo it usually is.
        if (except.Where(relative => !left.Contains(relative, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } stray)
        {
            return CommandOutcome.Refused(
                $"{RefreshRequest.ExceptOption} {ReportText.Listed([.. stray.Select(relative => $"'{relative}'")])} names no path to refresh{under} that "
                + $"{Lower(Agent(record))} changed, so it leaves nothing out: check its spelling. Nothing was copied.");
        }

        if (changed.Count > 0)
        {
            return CommandOutcome.Refused(
                $"{Agent(record)} changed {changed.Count} of the path(s) to refresh - {ReportText.Listed(changed)} - and refreshing would undo those "
                + "changes. Nothing was copied.",
                [
                    .. changed.Select(relative => $"  {relative}"),
                    $"To hand it every other path and leave these as it changed them, run again with {RefreshRequest.ExceptOption} <path> for each:",
                    $"  {RefreshRequest.ExceptArguments(left.Concat(changed).Order(StringComparer.Ordinal))}",
                    $"{RefreshRequest.ExceptOption} says the path stays the agent's change, for its fold to weigh against what the main tree holds; it is not a --force.",
                ]);
        }

        if (inTheWay.Count > 0)
        {
            return InTheWay("Refreshing", record, inTheWay);
        }

        var all = moved.Files.Concat(moved.Deletions).Order(StringComparer.Ordinal).ToList();
        var behind = await BehindAsync(main, record).ConfigureAwait(false);

        // The line that asks for this refresh again, with everything it was asked: its paths or --all, and each path left
        // as the agent changed it. Said bare, it asked for another refresh - of the registries' directory alone, and
        // refused over the very changes this one was told to leave.
        var again = OrchestrationReports.Line(RefreshCommand, record.Orchestrator, record.Name, request.Arguments(), "--apply");

        // Never silent: a path left out without a word is how a change of the main tree's goes missing while the refresh succeeds.
        IReadOnlyList<string> leftLines = left.Count == 0
            ? []
            : [$"{left.Count} path(s) it changed, named with {RefreshRequest.ExceptOption}, {(apply ? "left" : "to leave")} as it changed them:", .. left.Select(relative => $"  {relative}")];

        if (all.Count == 0)
        {
            return CommandOutcome.Ok(
                $"{Lower(Agent(record))} holds the main tree's copy of every {(left.Count > 0 ? "other " : string.Empty)}changed path{under}",
                [.. leftLines, .. NotHanded(handable), .. behind]);
        }

        if (!apply)
        {
            return CommandOutcome.Ok(
                $"dry run: {all.Count} path(s) would be refreshed into {Lower(Agent(record))}; pass --apply to hand them over",
                [.. all.Select(relative => $"  {relative}"), .. leftLines, .. NotHanded(handable), .. behind]);
        }

        // The agent's worktree is held too: a leg building in it would be building what is being replaced.
        var hold = await HoldAsync(context.Layout, RefreshCommand, cancellationToken, main, _fileSystem.ResolveLinks(path)).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            int handed;
            string? stopped;

            try
            {
                SeedRecord updated;
                (updated, handed, stopped) = await _fold.HandAsync(main, path, moved, seed, CancellationToken.None).ConfigureAwait(false);
                _store.WriteSeed(layout, record.Name, updated);
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Refreshing {Lower(Agent(record))} handed over what it could and could not record it: {ex.Message.TrimEnd('.')}. Run "
                    + $"{again} again once that is dealt with.",
                    [seedLine]);
            }

            return stopped is not null
                ? CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Refreshing {Lower(Agent(record))} stopped after {handed} of {all.Count} path(s): {stopped}. Its seed records the {handed} handed; "
                    + $"run {again} again once that is dealt with.",
                    [seedLine])
                : CommandOutcome.Ok(
                    $"refreshed {all.Count} path(s) into {Lower(Agent(record))}, recorded as handed to it, so its fold leaves them out",
                    [.. all.Select(relative => $"  {relative}"), .. leftLines, .. NotHanded(handable), .. behind, seedLine]);
        }

        // The paths whose main-tree copy the agent does not hold already.
        async Task<IReadOnlyList<string>> OtherAsync(IReadOnlyList<string> relatives)
        {
            var other = new List<string>();

            foreach (var relative in relatives)
            {
                if (!await _fold.SameAsync(Path.Combine(main, relative), Path.Combine(path, relative), cancellationToken).ConfigureAwait(false))
                {
                    other.Add(relative);
                }
            }

            return other;
        }
    }

    public async Task<CommandOutcome> RebaseAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        IReadOnlyList<string> settled,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settled);

        if ((Shape(orchestrator, agent) ?? OrchestrationRules.PathsProblem(settled, FoldAllowances.SettledOption)) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var at = await LiveAgentAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (at.Refusal is { } refusal)
        {
            return refusal;
        }

        var outcome = await RebaseLiveAsync(at, settled, apply, cancellationToken).ConfigureAwait(false);
        return apply ? _log.Record(at.Layout!, agent, RebaseCommand, outcome) : outcome;
    }

    /// <summary>Moving a live agent's base, once it is found.</summary>
    private async Task<CommandOutcome> RebaseLiveAsync(AgentAt at, IReadOnlyList<string> settled, bool apply, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (at.Context!, at.Layout!, at.Record!, at.Path!);
        var main = context.Layout.MainCheckoutRoot;
        var recordLine = $"record {layout.AgentRecordFile(record.Name)}";
        var from = record.Base!;

        if (_store.ReadSeed(layout, record.Name) is not { } seed)
        {
            return CommandOutcome.Refused($"{Agent(record)} is not whole - making it stopped part way: {MakingUnfinished(record, seed: null)}.");
        }

        if (await _gitClient.ResolveCommitAsync(main, "HEAD", cancellationToken).ConfigureAwait(false) is not { } head)
        {
            return CommandOutcome.Refused($"The main tree has no commit for the base of {Lower(Agent(record))} to move to. Nothing was changed.");
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
            return off.Refusal;
        }

        var finishing = record.Moving is not null;
        var moving = $"from {OrchestrationReports.Base(from)} to {(to == head ? "the main tree's HEAD " : "the main tree's commit ")}{OrchestrationReports.Base(to)}";
        var (plan, stop) = await MeasuredAsync(cancellationToken).ConfigureAwait(false);

        if (stop is not null)
        {
            return stop;
        }

        if (!apply)
        {
            return CommandOutcome.Ok($"dry run: the base of {Lower(Agent(record))} would move {moving}; pass --apply to move it", [.. RebaseLines(plan!, applied: false)]);
        }

        // The agent's worktree is held: a leg building in it would be building what is being replaced. Measured again under
        // the hold, so what is written is what was weighed.
        var hold = await HoldAsync(context.Layout, RebaseCommand, cancellationToken, _fileSystem.ResolveLinks(path)).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            (plan, stop) = await MeasuredAsync(cancellationToken).ConfigureAwait(false);

            if (stop is not null)
            {
                return stop;
            }

            // Never interrupted from here. Where the move goes is recorded first, then what the new base brings is written,
            // then HEAD and the index move, then the record names the new base: stopped anywhere, its record says where it
            // was going and its worktree stands on one of the two commits, so running it again finishes it there - and every
            // other command refuses it until then, since what was written would read as the agent's work.
            try
            {
                if (!finishing)
                {
                    _store.UpdateAgent(layout, record.Name, current => current with { Moving = to });
                }

                await _gitClient.CheckOutAtAsync(path, to, plan!.Written, CancellationToken.None).ConfigureAwait(false);
                await _gitClient.CheckOutAtAsync(path, to, plan.WrittenLast, CancellationToken.None).ConfigureAwait(false);
                await _gitClient.ResetToAsync(path, to, CancellationToken.None).ConfigureAwait(false);
                _store.UpdateAgent(layout, record.Name, current => current with { Base = to, Moving = null });
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Moving the base of {Lower(Agent(record))} {moving} stopped part way: {ex.Message.TrimEnd('.')}. Its worktree may hold part of what the new "
                    + $"base brings; run {OrchestrationReports.Line(RebaseCommand, record.Orchestrator, record.Name, "--apply")} again once that is dealt with, "
                    + "and it finishes it.",
                    [recordLine]);
            }

            var details = new List<string>(RebaseLines(plan, applied: true));

            if (await WorktreeService.WriteBaseCommitAsync(_gitClient, main, record.Worktree, to, CancellationToken.None).ConfigureAwait(false) is { } unrecorded)
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
                measured = await _fold.MeasureRebaseAsync(path, from, to, seed, settled, finishing, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, CommandOutcome.Failed(
                    HarnessExit.CommandFailed,
                    $"The base of {Lower(Agent(record))} was not moved: what it holds cannot be read - {ex.Message.TrimEnd('.')}. Nothing was changed."));
            }

            return measured.Refusals.Count == 0
                ? (measured, null)
                : (measured, CommandOutcome.Refused(
                    $"The base of {Lower(Agent(record))} cannot move {moving}: {measured.Refusals.Count} problem(s), and nothing was changed.",
                    [.. RebaseRefusalLines(measured)]));
        }
    }

    /// <summary>What moving an agent's base brings in, keeps and leaves alone, every path the two commits hold differently in exactly one list.</summary>
    private static IEnumerable<string> RebaseLines(RebasePlan plan, bool applied)
    {
        if (plan.Settled.Count > 0)
        {
            // Never silent: a path kept without a word is how a change of the main tree's goes missing while the move succeeds.
            yield return $"{plan.Settled.Count} path(s) it changed, declared settled by hand, {(applied ? "kept" : "to keep")} as its own change on the new base:";

            foreach (var path in plan.Settled)
            {
                yield return $"  {path}";
            }
        }

        yield return $"{plan.Taken.Count} path(s) the main tree committed since {(applied ? "came" : "come")} into its worktree as git holds them:";

        foreach (var path in plan.Taken)
        {
            yield return $"  {path}";
        }

        if (plan.Kept.Count > 0)
        {
            yield return $"and {plan.Kept.Count} path(s) it shares with the main tree, handed to it or folded, {(applied ? "stayed" : "stay")} as its seed "
                + "records them; refresh-agent hands it any the main tree moved since:";

            foreach (var path in plan.Kept)
            {
                yield return $"  {path}";
            }
        }

        if (plan.AlreadyThere.Count > 0)
        {
            yield return $"and {plan.AlreadyThere.Count} path(s) it holds as the new base does already, with nothing to write:";

            foreach (var path in plan.AlreadyThere)
            {
                yield return $"  {path}";
            }
        }
    }

    /// <summary>Why an agent's base cannot be moved, each path with its reason, and how a path reconciled by hand is let through.</summary>
    private static IEnumerable<string> RebaseRefusalLines(RebasePlan plan)
    {
        foreach (var refusal in plan.Refusals)
        {
            yield return $"  {refusal}";
        }

        yield return "A path the agent changed that the main tree committed a change to since is two changes of one file. Reconcile the agent's "
            + "copy with what the main tree committed by hand, then run again naming each path you reconciled with --settled <path>, which keeps its "
            + "copy as its own change on the new base. --settled says you reconciled the path yourself; it is not a --force.";
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
                    + $"and its worktree may hold part of what the new base brings: {OrchestrationReports.Line(RebaseCommand, record.Orchestrator, record.Name, "--apply")} "
                    + "finishes it."));
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowances);

        if ((Shape(orchestrator, agent) ?? allowances.Problem()) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var at = await LiveAgentAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (at.Refusal is { } refusal)
        {
            return refusal;
        }

        var outcome = await FoldLiveAsync(at, allowances, apply, cancellationToken).ConfigureAwait(false);
        return apply ? _log.Record(at.Layout!, agent, FoldCommand, outcome) : outcome;
    }

    /// <summary>Folding a live agent, once it is found.</summary>
    private async Task<CommandOutcome> FoldLiveAsync(AgentAt at, FoldAllowances allowances, bool apply, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (at.Context!, at.Layout!, at.Record!, at.Path!);
        var (unfoldable, seed) = await UnfoldableAsync(context.Layout.MainCheckoutRoot, layout, record, path, cancellationToken).ConfigureAwait(false);

        if (unfoldable is not null)
        {
            return unfoldable;
        }

        if (!apply)
        {
            var measured = await MeasureAsync(context, layout, record, path, seed!, allowances, deleting: false, AnchorBatchMode.Plan, cancellationToken).ConfigureAwait(false);

            if (measured.Refusal is { } refused)
            {
                return refused;
            }

            var (tail, line) = Ending(FoldCommand, record, allowances, measured.Rows.Batch.Unaccepted, "write them");

            return CommandOutcome.Ok(
                $"dry run: folding {Lower(Agent(record))} writes {measured.Plan!.Written.Count} path(s) into the main tree, removes "
                + $"{measured.Plan.Deleted.Count} and applies {measured.Rows.Planned} row(s); {tail}",
                [.. OrchestrationReports.FoldAndRowLines(measured.Plan, measured.Rows, layout.RowsDirectory(record.Name)), .. line]);
        }

        // The agent's worktree is held too: a leg building in it would be writing what is being folded.
        var hold = await HoldAsync(context.Layout, FoldCommand, cancellationToken, context.Layout.MainCheckoutRoot, _fileSystem.ResolveLinks(path)).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            var measured = await MeasureAsync(context, layout, record, path, seed!, allowances, deleting: false, AnchorBatchMode.Check, cancellationToken).ConfigureAwait(false);

            if (measured.Refusal is { } refused)
            {
                return refused;
            }

            var written = await WriteAsync(context, layout, record, path, seed!, measured).ConfigureAwait(false);

            return written.Failure ?? CommandOutcome.Ok(
                $"folded {Lower(Agent(record))}: wrote {measured.Plan!.Written.Count} path(s) into the main tree, removed "
                + $"{measured.Plan.Deleted.Count} and applied {written.Rows.Planned} row(s); its worktree is kept, and what the fold wrote is "
                + "recorded as shared, so a fold after a review weighs the agent's change of it",
                [.. OrchestrationReports.FoldAndRowLines(measured.Plan, written.Rows, layout.RowsDirectory(record.Name)), .. Records(layout, record)]);
        }
    }

    public async Task<CommandOutcome> DeleteAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        FoldAllowances allowances,
        bool apply,
        bool discardUncommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allowances);

        if ((Shape(orchestrator, agent) ?? allowances.Problem()) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        if (discardUncommitted && allowances.Given.Count > 0)
        {
            return CommandOutcome.Usage(
                $"{Letting(allowances)}, and --discard-uncommitted folds nothing: give one or the other.");
        }

        var found = await FindAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (found.Refusal is { } refusal)
        {
            return refusal;
        }

        var outcome = await DeleteFoundAsync(found.Context!, found.Layout!, found.Record!, allowances, apply, discardUncommitted, cancellationToken).ConfigureAwait(false);
        return apply ? _log.Record(found.Layout!, agent, DeleteCommand, outcome) : outcome;
    }

    /// <summary>Deleting an agent, once its record is found.</summary>
    private async Task<CommandOutcome> DeleteFoundAsync(
        HarnessContext context,
        OrchestratorLayout layout,
        AgentRecord record,
        FoldAllowances allowances,
        bool apply,
        bool discardUncommitted,
        CancellationToken cancellationToken)
    {
        if (record.State == AgentStates.Deleted)
        {
            return CommandOutcome.Refused(
                $"{Agent(record)} was deleted at {OrchestrationReports.Moment(record.DeletedAt!.Value)}: nothing of it is left to delete, and its "
                + "directory is kept as its history.");
        }

        var (path, moved) = WorktreeOf(context, record);

        if (moved is not null)
        {
            return CommandOutcome.Refused(moved);
        }

        // A removal cannot take a directory a process stands in: Windows refuses it, naming this process, and elsewhere the
        // process is left standing in a directory that is gone.
        if (apply && StandsIn(path) is { } here)
        {
            return CommandOutcome.Refused(
                $"This process's working directory '{here}' is inside the worktree of {Lower(Agent(record))}, '{path}', which deleting the agent "
                + "removes. Nothing was written or removed: run it from the main tree.");
        }

        var standing = await StandingAsync(context, path, cancellationToken).ConfigureAwait(false);
        var target = new Target(context, layout, record, WorktreeAddress.Nested(record.Orchestrator, record.Name), path, standing);

        return record.State == AgentStates.Closed
            ? await FinishAsync(target, allowances, apply, cancellationToken).ConfigureAwait(false)
            : await CloseAsync(target, allowances, apply, discardUncommitted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deleting a live agent: its fold and rows - or nothing, abandoning it - then its transcripts and evidence kept, its
    /// closing recorded, and its worktree's removal asked for.
    /// </summary>
    private async Task<CommandOutcome> CloseAsync(Target target, FoldAllowances allowances, bool apply, bool discard, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (target.Context, target.Layout, target.Record, target.Path);

        switch (target.Standing.Kind)
        {
            case Standing.Gone when discard:
                return await AbandonGoneAsync(target, apply, cancellationToken).ConfigureAwait(false);

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
                return unfoldable;
            }

            seed = found;
        }

        if (!apply)
        {
            var dry = await MeasureClosingAsync(target, seed, allowances, discard, AnchorBatchMode.Plan, cancellationToken).ConfigureAwait(false);

            if (dry.Refusal is { } refusal)
            {
                return refusal;
            }

            var evidence = dry.Evidence.Files.Count == 0
                ? "its evidence roots hold nothing to keep"
                : $"{dry.Evidence.Files.Count} evidence file(s), to be kept in a directory named for the run in '{layout.EvidenceDirectory(record.Name)}'";
            var (tail, line) = Ending(DeleteCommand, record, allowances, dry.Fold?.Rows.Batch.Unaccepted ?? [], "do it");

            return CommandOutcome.Ok(
                $"dry run: deleting {Lower(Agent(record))} "
                + (discard ? $"discards {dry.Discarded.Count} changed path(s), folding nothing" : $"folds {dry.Fold!.Plan!.Written.Count} path(s), removes {dry.Fold.Plan.Deleted.Count} and applies {dry.Fold.Rows.Planned} row(s)")
                + $", keeps its transcripts and {dry.Evidence.Files.Count} evidence file(s), then removes its worktree and its copies on hosts; {tail}",
                [
                    .. discard
                        ? [$"{dry.Discarded.Count} changed path(s) discarded:", .. dry.Discarded.Select(relative => $"  {relative}")]
                        : OrchestrationReports.FoldAndRowLines(dry.Fold!.Plan, dry.Fold.Rows, layout.RowsDirectory(record.Name)),
                    evidence,
                    TranscriptsPlan(record),
                    .. line,
                ]);
        }

        var hold = await HoldAsync(context.Layout, DeleteCommand, cancellationToken, context.Layout.MainCheckoutRoot, _fileSystem.ResolveLinks(path)).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            var measured = await MeasureClosingAsync(target, seed, allowances, discard, AnchorBatchMode.Check, cancellationToken).ConfigureAwait(false);

            if (measured.Refusal is { } refused)
            {
                return refused;
            }

            // Which git worktree this is, read before anything is written: its closing records it, and a worktree whose
            // identity cannot be read is never closed.
            string stamp;

            try
            {
                stamp = WorktreeStamp.Of(_fileSystem, target.Standing.AdministrativeDirectory!) ?? throw new IOException($"git names no '{WorktreeStamp.FileName}' in its git directory");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return CommandOutcome.Failed(
                    HarnessExit.CommandFailed,
                    $"{Agent(record)} was not deleted: which git worktree '{path}' is cannot be read - {ex.Message.TrimEnd('.')} - so its closing could "
                    + "not record it. Nothing was written or removed.");
            }

            var lines = new List<string>();
            var what = discard ? "Nothing of it was folded" : "Its fold and rows are in the main tree";
            var again = OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, discard ? "--apply --discard-uncommitted" : "--apply");

            if (!discard)
            {
                var fold = measured.Fold!;
                var written = await WriteAsync(context, layout, record, path, seed!, fold).ConfigureAwait(false);

                if (written.Failure is { } failure)
                {
                    return failure with { Message = $"{failure.Message} Nothing was removed." };
                }

                seed = written.Seed;
                lines.AddRange(OrchestrationReports.FoldAndRowLines(fold.Plan, written.Rows, layout.RowsDirectory(record.Name)));
            }

            // Until the closing is recorded, the agent is live: anything that stops it here says what is in, and a run again
            // measures it afresh.
            var stopped = discard ? HarnessExit.CommandFailed : HarnessExit.Incomplete;
            EvidenceKept kept;

            try
            {
                if (!discard)
                {
                    // Nothing may be left to fold: the removal discards the worktree's uncommitted work on this measurement alone.
                    var after = await _fold.MeasureAsync(context.Layout.MainCheckoutRoot, path, record.Base!, seed!, allowances.Settled, Floor(context), CancellationToken.None).ConfigureAwait(false);

                    if (!after.NothingLeft)
                    {
                        return CommandOutcome.Failed(
                            HarnessExit.Incomplete,
                            $"{Agent(record)} was not deleted: after its fold was written, it still differs from the main tree - "
                            + $"{ReportText.Listed([.. after.Written, .. after.Deleted, .. after.Refusals])}. {what}, and its worktree is kept; nothing was removed.",
                            [.. lines, .. Records(layout, record)]);
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
                        [.. lines, .. Records(layout, record)]);
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
                        [.. lines, .. Records(layout, record)]);
                }

                lines.Add(EvidenceLine(kept, destination));
                var holding = await _fold.HeldAsync(path, seed, [], Floor(context), CancellationToken.None).ConfigureAwait(false);
                target = target with { Record = Close(target, stamp, discard, kept.Kept.Count > 0 ? keeping : string.Empty, holding) };
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    stopped,
                    $"{Agent(record)} was not deleted: {ex.Message.TrimEnd('.')}. {what}; its worktree is kept, and nothing was removed. Run "
                    + $"{again} again once that is dealt with.",
                    [.. lines, .. Records(layout, record)]);
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
                lines.Add($"{deleted.Left.Count} evidence file(s) were left: {ReportText.Listed(deleted.Left)}");
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
    private async Task<CommandOutcome> FinishAsync(Target target, FoldAllowances allowances, bool apply, CancellationToken cancellationToken)
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
                        $"{was}, and its worktree holds {changed.Count} file(s) its closing did not record, changed or new since: work done in an agent "
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
            return CommandOutcome.Ok(
                $"dry run: {Lower(was)}; pass --apply to "
                + target.Standing.Kind switch
                {
                    Standing.Husk => "keep its evidence again - nothing here removes a directory with no .git of its own",
                    Standing.Gone => "finish the removal",
                    _ => "keep its evidence again and finish the removal",
                });
        }

        var hold = await HoldAsync(context.Layout, DeleteCommand, cancellationToken, _fileSystem.ResolveLinks(path)).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
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
                    return CommandOutcome.Failed(
                        HarnessExit.Incomplete,
                        $"{was}. '{path}' is no longer a git worktree - it holds no .git of its own - which is what a removal that stopped part way "
                        + "leaves, or a directory made there since. Look at what else it holds; once nothing in it is needed, "
                        + $"{OrchestrationReports.Line(WorktreeService.DeleteCommand, target.Address.Name, "--force")} removes it - nothing here forces it - "
                        + $"and {again} then finishes this agent.",
                        [.. lines, .. Records(layout, record)]);
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
    private async Task<CommandOutcome> AbandonGoneAsync(Target target, bool apply, CancellationToken cancellationToken)
    {
        var (layout, record) = (target.Layout, target.Record);
        var again = OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, "--apply --discard-uncommitted");

        if (!apply)
        {
            return CommandOutcome.Ok(
                $"dry run: {Lower(Agent(record))} has no worktree at '{target.Path}'; pass --apply to delete it with nothing folded, keeping its "
                + "transcripts, once git's record of its worktree and every copy of it on a host are gone",
                [TranscriptsPlan(record)]);
        }

        var hold = await HoldAsync(target.Context.Layout, DeleteCommand, cancellationToken, _fileSystem.ResolveLinks(target.Path)).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
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
        // A mutation worker a sweep left beside where the worktree was is something left for it, which it removes as it
        // removes one beside a worktree still there - or leaves, saying what holds it, which keeps the agent undeleted.
        if (target.Standing.Kind != Standing.Gone || WorkersLeft(target) || (await LeftAsync(target).ConfigureAwait(false)).Count > 0)
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
    /// Whether a mutation worker is kept beside where the agent's worktree is, or was: told by its name, as deleting its
    /// orchestrator tells one. One may be where the directory cannot be looked in - nothing else of a worktree that is
    /// gone looks there - so delete-worktree is asked, and it is the one that says it could not look.
    /// </summary>
    private bool WorkersLeft(Target target)
    {
        var path = Path.TrimEndingDirectorySeparator(target.Path);

        try
        {
            return Path.GetDirectoryName(path) is { Length: > 0 } beside
                && MutationWorkers.TreesWithWorkersIn(_fileSystem, beside).Contains(Path.GetFileName(path), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
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
                left.Add($"the record of host copies still holds {copies.Count} copy(ies) of it: {string.Join(", ", copies.Select(copy => $"{copy.Host}: {copy.Path}"))}");
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
        FoldPlan plan;

        try
        {
            plan = await _fold.MeasureAsync(main, path, record.Base!, seed, allowances.Settled, Floor(context), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Measured(null, null, [], new AgentRowsPlan(new AnchorBatch([], []), []), CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"{Agent(record)} was not {verb}: what it changed cannot be read - {ex.Message.TrimEnd('.')}. {nothing}"));
        }

        // A registry the agent changed as a file would be written over by the fold and then changed again by its rows, which
        // were weighed against the registry before the fold wrote it: an agent's rows go in through its rows directory only.
        var registries = new[] { context.Config.Anchors.PendingAnchorsPath, context.Config.Anchors.DoneAnchorsPath }
            .Select(AnchorRegistryLocator.Normalize)
            .ToHashSet(StringComparer.Ordinal);
        var filedAsFiles = plan.Written.Concat(plan.Deleted).Where(registries.Contains).Order(StringComparer.Ordinal)
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
            what.Add($"{rowsPlan.Batch.Problems.Count} problem(s) with its rows");
        }

        return new Measured(plan, request, all, rowsPlan, CommandOutcome.Refused(
            $"{Agent(record)} was not {verb}: {string.Join(", and ", what)}. {nothing}",
            [.. OrchestrationReports.RefusalLines(plan, rowsPlan, layout.RowsDirectory(record.Name))]));
    }

    /// <summary>
    /// The agent's rows as a fold weighs them: those it declares anew - or never had applied - planned or checked as
    /// applying them would write them (<paramref name="mode"/>), making only the rows named new and losing no stored text
    /// nobody accepted; those an earlier fold applied and it declares as it did then, never applied again; and a row it
    /// declares anew over one an earlier fold applied refused where the registries changed that row since, as a file the
    /// main tree changed since is refused.
    /// </summary>
    /// <remarks>
    /// --new and --accept-lost name rows the agent filed; one naming a row it did not file is refused. A row an earlier fold
    /// of the agent applied is an existing row now - made or changed by that fold - so a --new naming it is let stand, as is
    /// an --accept-lost naming a row the agent declares as that fold applied it: the command line of that fold runs again.
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
        var fresh = declared.Where(row => !applied.TryGetValue(row.Id, out var last) || last != row).ToList();
        var freshIds = fresh.Select(row => row.Id).ToHashSet(AnchorIdMatch.Comparer);
        var unchanged = declared.Where(row => !freshIds.Contains(row.Id)).Select(row => row.Id).ToList();
        var filed = declared.Select(row => row.Id).ToHashSet(AnchorIdMatch.Comparer);
        var named = allowances.New.Distinct(AnchorIdMatch.Comparer).ToList();
        var accepted = allowances.LostCells;

        var unfiled = named.Where(id => !filed.Contains(id))
            .Select(id => $"{AnchorBatchRequest.NewOption} {id} names no row filed in '{directory}': drop it, or correct the id.")
            .Concat(accepted.Where(cell => !filed.Contains(cell.Id))
                .Select(cell => $"{AnchorBatchRequest.AcceptLostOption} {cell} names no row filed in '{directory}': drop it, or correct the id."))
            .Order(StringComparer.Ordinal)
            .ToList();

        var request = new AnchorBatchRequest(fresh)
        {
            New = [.. named.Where(id => freshIds.Contains(id) && !applied.ContainsKey(id))],
            AcceptLost = [.. accepted.Where(cell => freshIds.Contains(cell.Id))],
            Roots = roots,
        };

        var batch = await _anchors.ApplyAsync(main, request, mode, cancellationToken).ConfigureAwait(false);

        // Every row declared anew over one an earlier fold applied - planned, or refused for something else - but for one the
        // registries hold as it is declared now.
        var standing = batch.Rows.Where(outcome => outcome.Action == AnchorRowAction.AlreadyIn).Select(outcome => outcome.Id).ToHashSet(AnchorIdMatch.Comparer);
        var redeclared = fresh.Where(row => applied.ContainsKey(row.Id) && !standing.Contains(row.Id)).Select(row => applied[row.Id]).ToList();

        // What an earlier fold applied is compared with the registries as they are, and held to nothing a write is held to:
        // a row applied before a rule it breaks is still the row that was applied.
        var moved = (await _anchors.DifferencesAsync(main, redeclared, cancellationToken).ConfigureAwait(false))
            .Select(difference => $"{difference.Id}: the registries changed it after an earlier fold of this agent applied it - {difference.How} - so "
                + "applying what it declares now would lose that change. Set the row by hand as it should be; a row already as declared is recorded, not written again")
            .ToList();

        return (new AgentRowsPlan(batch with { Problems = [.. unfiled, .. batch.Problems, .. moved] }, unchanged), request, declared);
    }

    /// <summary>That the options <paramref name="allowances"/> gives let a fold through what it otherwise refuses, for a refusal of them.</summary>
    private static string Letting(FoldAllowances allowances)
        => $"{ReportText.Listed(allowances.Given)} {(allowances.Given.Count == 1 ? "lets" : "let")} a fold through what it otherwise refuses";

    /// <summary>
    /// How the summary of a dry run of a fold ends, and the line it points at: where its rows lose stored text nobody
    /// accepted, that --apply refuses the fold, writing nothing, until each such cell is named with --accept-lost - and the
    /// line opening "to <paramref name="what"/>:" with the command that does it: what the dry run was let through, and an
    /// --accept-lost for each. The summary names that line by its opening words rather than by where it stands: it is the
    /// last of the details, and every report prints its summary after them. Otherwise, that --apply does
    /// <paramref name="what"/>. Like every command line a report names, it runs from where the dry run ran.
    /// </summary>
    private static (string Tail, IReadOnlyList<string> Line) Ending(string command, AgentRecord record, FoldAllowances allowances, IReadOnlyList<AnchorRowCell> unaccepted, string what)
    {
        if (unaccepted.Count == 0)
        {
            return ($"pass --apply to {what}", []);
        }

        var accepting = allowances with { AcceptLost = [.. allowances.AcceptLost, .. unaccepted.Select(cell => cell.ToString())] };
        var opening = $"to {what}:";

        return (
            $"{unaccepted.Count} cell(s) of its rows do not keep their stored text word for word, and --apply refuses it, writing nothing, "
                + $"until each is named with {AnchorBatchRequest.AcceptLostOption}, once its word diff above is read: the line '{opening}' above "
                + "is the command that does it",
            [$"{opening} {OrchestrationReports.Line(command, [record.Orchestrator, record.Name, "--apply", .. accepting.Arguments()])}"]);
    }

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
        var records = Records(layout, record);
        var progress = $"after writing {applied.Written.Count} of {plan.Written.Count} path(s) and removing {applied.Deleted.Count} of {plan.Deleted.Count}";
        SeedRecord folded;

        try
        {
            folded = _fold.Folded(seed, plan, applied, path);
            _store.WriteSeed(layout, record.Name, folded);
        }
        catch (Exception ex) when (Unfinished(ex))
        {
            return new Written(
                CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Folding {Lower(Agent(record))} wrote into the main tree {progress}, and its seed could not record what it wrote: "
                    + $"{ex.Message.TrimEnd('.')}. Its rows were not applied; run it again once that is dealt with - a fold run again finds what it "
                    + "wrote already in, records it, and measures the rest afresh.",
                    records),
                measured.Rows,
                seed);
        }

        if (applied.Stopped is { } why)
        {
            return new Written(
                CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Folding {Lower(Agent(record))} stopped part way, {progress}: {why.TrimEnd('.')}. What it wrote is in the main tree and recorded "
                    + "as shared, and its rows were not applied; run it again once that is dealt with - a fold run again measures the rest afresh.",
                    records),
                measured.Rows,
                folded);
        }

        if (measured.Request is not { Rows.Count: > 0 } request)
        {
            return new Written(null, measured.Rows, folded);
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
                folded);
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
                folded);
        }

        return new Written(null, rows, folded);
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
        => record.Base is null
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
    /// since delete-worktree looks for it under the root the configuration names. Why not, where it does not.
    /// </summary>
    private static (string Path, string? Moved) WorktreeOf(HarnessContext context, AgentRecord record)
    {
        var configured = PathPatterns.Normalize(context.Config.Worktrees.Root);
        var path = record.WorktreePath(context.Layout);

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
    /// Takes each of <paramref name="trees"/> on this machine whole, as a sync takes a copy: no leg builds in it, and no other
    /// fold, seed or deletion writes it, meanwhile. All of them, or none; the run's id names what it keeps.
    /// </summary>
    private async Task<Hold> HoldAsync(HarnessLayout layout, string command, CancellationToken cancellationToken, params string[] trees)
    {
        var runId = RunId.New();
        var handles = new List<RunLockHandle>();

        foreach (var tree in trees)
        {
            var attempt = await _runLock
                .TryAcquireAsync(
                    layout,
                    new LockRequest
                    {
                        Host = HostId.Local.ToString(),
                        Tree = tree,
                        Scope = LockScope.TreeExclusive,
                        RunId = runId,
                        Command = command,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (attempt.Handle is not { } handle)
            {
                await new Handles(handles).DisposeAsync().ConfigureAwait(false);
                return new Hold(new Handles([]), CommandOutcome.Refused($"{attempt.HeldBy} Nothing was changed; run it again once that is done."), runId);
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
                : $"kept {kept.Kept.Count} transcript file(s) of session {record.Session} in '{layout.TranscriptsDirectory(record.Name)}', each read back",
        };

        lines.AddRange(kept.Unread.Select(unread => $"a directory transcripts may be in could not be looked in: {unread}"));

        return (lines, kept.Failed);
    }

    private static string TranscriptsPlan(AgentRecord record)
        => record.Session is null
            ? $"no Claude session is recorded for it, so no transcript is looked for; {OrchestrationReports.Line(CreateCommand, record.Orchestrator, record.Name, "--model", record.Model, "--session", "<id>")} records one"
            : $"transcripts: session {record.Session}'s, where Claude Code keeps them";

    private static string EvidenceLine(EvidenceKept kept, string destination)
        => (kept.Kept.Count == 0 ? "its evidence roots held nothing to keep" : $"kept {kept.Kept.Count} evidence file(s) in '{destination}', each proved")
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
            $"{doing} {Lower(Agent(record))} would write over or through {own.Count} path(s) of its own - {ReportText.Listed(own)} - where the main tree "
            + "holds a directory, or a file in place of the directory they are in: move them aside, and run again. Nothing was written.");

    /// <summary>Where the agent's records are, as every exit after one is written names them: each that is there.</summary>
    private string[] Records(OrchestratorLayout layout, AgentRecord record)
        => [
            $"record {layout.AgentRecordFile(record.Name)}",
            .. _fileSystem.FileExists(layout.SeedFile(record.Name)) ? [$"seed {layout.SeedFile(record.Name)}"] : Array.Empty<string>(),
            .. _fileSystem.FileExists(layout.AppliedRowsFile(record.Name)) ? [$"applied rows {layout.AppliedRowsFile(record.Name)}"] : Array.Empty<string>(),
        ];

    private string? StandsIn(string path)
    {
        var here = _currentDirectory();

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

    /// <summary>What writing a fold did: why it stopped where it did, the rows as applied, and the seed as recorded.</summary>
    private sealed record Written(CommandOutcome? Failure, AgentRowsPlan Rows, SeedRecord Seed);

    /// <summary>What deleting a live agent measured before writing anything, and why it cannot go on where it cannot.</summary>
    private sealed record ClosingMeasure(Measured? Fold, IReadOnlyList<string> Discarded, EvidenceFound Evidence, CommandOutcome? Refusal);

    /// <summary>The trees held, or why they could not be, and the run's id.</summary>
    private sealed record Hold(Handles Handles, CommandOutcome? Refusal, RunId Run);

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
