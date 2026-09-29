using System.Globalization;
using System.Text.Json;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Orchestration;

/// <summary>Creating, seeding, refreshing, folding and deleting an orchestrator's agents.</summary>
public interface IAgentService
{
    /// <summary>
    /// Creates an agent of an orchestrator: its record, its worktree below the directory named for the orchestrator,
    /// and its seed - the main tree's uncommitted state copied into the worktree, each path's digest recorded - unless
    /// <paramref name="empty"/>. Refused past the orchestrator's limit of agents with a worktree. Run again for an agent
    /// that exists, it records only the session.
    /// </summary>
    Task<CommandOutcome> CreateAsync(string startDirectory, string orchestrator, string agent, string model, bool empty, string? session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seeds a live agent again: refused where its worktree holds changes of its own, unless <paramref name="force"/>;
    /// <paramref name="empty"/> records an empty seed and copies nothing.
    /// </summary>
    Task<CommandOutcome> SeedAsync(string startDirectory, string orchestrator, string agent, bool empty, bool force, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies into a live agent the main tree's changed files under <paramref name="paths"/> - the anchor registries'
    /// directory where none are given - that it has not changed, and records them as handed to it, so its fold leaves
    /// them out. A dry run until <paramref name="apply"/>.
    /// </summary>
    Task<CommandOutcome> RefreshAsync(string startDirectory, string orchestrator, string agent, IReadOnlyList<string> paths, bool apply, CancellationToken cancellationToken = default);

    /// <summary>
    /// Folds a live agent's work into the main tree, then applies the anchor rows it filed: every path it changed or was
    /// handed is measured first, and nothing is written while any is refused. A dry run until <paramref name="apply"/>.
    /// Its worktree is never removed: a review can still send it back.
    /// </summary>
    Task<CommandOutcome> FoldAsync(string startDirectory, string orchestrator, string agent, IReadOnlyList<string> settled, bool apply, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an agent: folds what is left of its work and applies its rows - or, where
    /// <paramref name="discardUncommitted"/>, abandons it with nothing folded - keeps its evidence and its Claude transcripts
    /// in its directory, each read back, closes it, and removes its worktree and the copies hosts keep of it, never
    /// forced; its directory stays as its history. Run again for a closed agent, it folds nothing and finishes the
    /// removal. A dry run until <paramref name="apply"/>.
    /// </summary>
    Task<CommandOutcome> DeleteAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        IReadOnlyList<string> settled,
        bool apply,
        bool discardUncommitted,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAgentService"/>
/// <remarks>
/// Once a command has written anything - the main tree, or an agent's record - it is no longer interrupted, and whatever it
/// meets from there on is answered as a change begun and not finished (<see cref="HarnessExit.Incomplete"/>), saying what is
/// in and how running it again goes on: never as a refusal, which says nothing changed.
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

        var (context, layout, record) = await OrchestratorAsync(startDirectory, orchestrator, cancellationToken).ConfigureAwait(false);

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

        // Counted and taken in one step, under the orchestrator's own lock, so two agents made together can never both
        // take its last place.
        var refused = _store.Exclusively(layout, () =>
        {
            if (_fileSystem.DirectoryExists(layout.AgentDirectory(agent)))
            {
                return CommandOutcome.Refused($"'{layout.AgentDirectory(agent)}' is there already, and no record in it names an agent. Nothing was created.");
            }

            var open = _store.Agents(layout)
                .Where(entry => entry.Record is not { State: AgentStates.Deleted })
                .Select(entry => entry.Name)
                .Concat(listed.Select(worktree => WorktreeAddress.AgentOf(orchestrator, worktree.Name)).OfType<string>())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (open.Count >= record.Parallel)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{orchestrator}' already has {open.Count} agent(s) with a worktree, its limit: {string.Join(", ", open)}. "
                    + $"Delete one with {OrchestrationReports.DeleteAgentLine(orchestrator, "<agent>")}, or raise the limit with "
                    + $"'{ToolPackage.Command} {OrchestratorService.CreateCommand} {orchestrator} --model {record.Model} --parallel <n>'. Nothing was created.");
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
            return refused;
        }

        // Until its worktree is made, the agent's directory holds only its place, which every way out before then gives back.
        var placeOnly = true;

        try
        {
            // Seeding reads the main tree, which a fold writes: held while the worktree is made and handed what it is
            // handed, so the seed is one moment of it.
            var hold = await HoldAsync(context.Layout, CreateCommand, cancellationToken, main).ConfigureAwait(false);

            if (hold.Refusal is { } held)
            {
                return held;
            }

            await using (hold.Handles)
            {
                // What it is to be handed is checked before its worktree is made: a hand-over refused leaves nothing behind.
                var handable = empty ? [] : await _fold.HandableAsync(main, Floor(context), within: null, cancellationToken).ConfigureAwait(false);
                var created = await _worktrees.CreateAtAsync(main, address, cancellationToken).ConfigureAwait(false);

                if (!created.Succeeded)
                {
                    return created.Outcome with { Message = $"{created.Outcome.Message.TrimEnd('.')}. No agent was created." };
                }

                placeOnly = false;

                return _log.Record(layout, agent, CreateCommand, await FinishMakingAsync(context, layout, agent, created, handable, empty).ConfigureAwait(false));
            }
        }
        finally
        {
            if (placeOnly)
            {
                _fileSystem.DeleteDirectory(layout.AgentDirectory(agent));
            }
        }
    }

    /// <summary>
    /// What makes an agent whose worktree now exists whole: its base, its directories and its seed - never interrupted, and
    /// never stopping part way without saying what it made and how to finish.
    /// </summary>
    private async Task<CommandOutcome> FinishMakingAsync(
        HarnessContext context,
        OrchestratorLayout layout,
        string agent,
        WorktreeOutcome created,
        IReadOnlyList<string> handable,
        bool empty)
    {
        var orchestrator = layout.Name;
        var remedy = $"'{ToolPackage.Command} {SeedCommand} {orchestrator} {agent} --force' finishes it once that is dealt with, and "
            + $"{OrchestrationReports.DeleteAgentLine(orchestrator, agent, "--apply --discard-uncommitted")} drops it.";

        try
        {
            var baseCommit = created.BaseCommit ?? await _gitClient.ResolveCommitAsync(created.Path, "HEAD", CancellationToken.None).ConfigureAwait(false);

            if (baseCommit is null)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Agent '{agent}' of '{orchestrator}' was created at '{created.Path}', and the commit its worktree was made from cannot be read, so "
                    + $"nothing of it can be measured. {OrchestrationReports.DeleteAgentLine(orchestrator, agent, "--apply --discard-uncommitted")} drops it.");
            }

            _store.UpdateAgent(layout, agent, current => current with { Base = baseCommit });
            _fileSystem.CreateDirectory(layout.WorkDirectory(agent));
            _fileSystem.CreateDirectory(layout.PlansDirectory(agent));

            var start = new SeedRecord { SeededAt = _clock.GetUtcNow(), Empty = empty, Paths = [] };
            var (seed, handed, stopped) = await _fold.HandAsync(context.Layout.MainCheckoutRoot, created.Path, handable, start, CancellationToken.None).ConfigureAwait(false);
            _store.WriteSeed(layout, agent, seed);

            if (stopped is not null)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Agent '{agent}' of '{orchestrator}' was created at '{created.Path}', and handing it the main tree's uncommitted state stopped after "
                    + $"{handed} of {handable.Count} path(s): {stopped}. Its seed records the {handed} it was handed. {remedy}");
            }

            return CommandOutcome.Ok(
                $"created agent '{agent}' of orchestrator '{orchestrator}'",
                [
                    created.Path,
                    $"base {OrchestrationReports.Base(baseCommit)}",
                    seed.Empty ? "handed nothing, as asked" : $"handed {seed.Paths.Count} path(s) of the main tree's uncommitted state",
                    $"record {layout.AgentRecordFile(agent)}",
                    $"plans {layout.PlansDirectory(agent)}",
                    $"work {layout.WorkDirectory(agent)}",
                    $"rows {layout.RowsDirectory(agent)}",
                ]);
        }
        catch (Exception ex) when (Unfinished(ex))
        {
            return CommandOutcome.Failed(
                HarnessExit.Incomplete,
                $"Agent '{agent}' of '{orchestrator}' was created at '{created.Path}', and making it did not finish: {ex.Message.TrimEnd('.')}. {remedy}");
        }
    }

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
                + $"'{ToolPackage.Command} {SeedCommand}' seeds it again. Nothing was changed.");
        }

        // An agent whose making stopped part way is said to be one, never "already as asked".
        if (existing.State == AgentStates.Live && (existing.Base is null || _store.ReadSeed(layout, existing.Name) is null))
        {
            return CommandOutcome.Refused(
                $"{Agent(existing)} exists, and making it did not finish: "
                + (existing.Base is null
                    ? $"it records no commit its worktree was made from. {OrchestrationReports.DeleteAgentLine(existing.Orchestrator, existing.Name, "--apply --discard-uncommitted")} drops it."
                    : $"it was never seeded. '{ToolPackage.Command} {SeedCommand} {existing.Orchestrator} {existing.Name}' seeds it, with --empty where it is to be handed nothing."));
        }

        if (session is null || session == existing.Session)
        {
            return CommandOutcome.Ok($"agent '{existing.Name}' of '{existing.Orchestrator}' is already as asked", [$"record {layout.AgentRecordFile(existing.Name)}"]);
        }

        _store.UpdateAgent(layout, existing.Name, current => current with { Session = session });

        return _log.Record(layout, existing.Name, CreateCommand, CommandOutcome.Ok(
            $"agent '{existing.Name}' of '{existing.Orchestrator}' now records session {session}",
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

        // Seeding overwrites by path, and an agent never reads again a file it believes it owns: seeded while it works, it
        // would go on over files replaced under it. So a worktree with changes of its own is refused, unless forced; an
        // empty seed copies nothing, and replaces nothing.
        if (!empty && !force)
        {
            var own = await _fold.ChangedAsync(path, Floor(context), "what the agent changed cannot be told", within: null, cancellationToken).ConfigureAwait(false);

            if (own.Count > 0)
            {
                return CommandOutcome.Refused(
                    $"{Agent(record)} already holds {own.Count} changed path(s) of its own - {ReportText.Listed(own)} - and seeding it now would overwrite "
                    + "them. Seed an agent before it starts; --empty records that it was handed nothing, and --force seeds it anyway.");
            }
        }

        var hold = await HoldAsync(context.Layout, SeedCommand, cancellationToken, main).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            if (empty)
            {
                _store.WriteSeed(layout, record.Name, new SeedRecord { SeededAt = _clock.GetUtcNow(), Empty = true, Paths = [] });
                return CommandOutcome.Ok($"seeded agent '{record.Name}' of '{record.Orchestrator}' with nothing, as asked", [$"seed {layout.SeedFile(record.Name)}"]);
            }

            // Every path is checked before any is copied; what it was handed before stays recorded until it is handed again.
            var handable = await _fold.HandableAsync(main, Floor(context), within: null, cancellationToken).ConfigureAwait(false);
            var previous = _store.ReadSeed(layout, record.Name);
            var start = (previous ?? new SeedRecord { SeededAt = _clock.GetUtcNow(), Empty = false, Paths = [] }) with { SeededAt = _clock.GetUtcNow(), Empty = false };
            var (seed, handed, stopped) = await _fold.HandAsync(main, path, handable, start, CancellationToken.None).ConfigureAwait(false);
            _store.WriteSeed(layout, record.Name, seed);

            if (stopped is not null)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Seeding {Lower(Agent(record))} stopped after {handed} of {handable.Count} path(s): {stopped}. Its seed records what it was handed; "
                    + $"run '{ToolPackage.Command} {SeedCommand} {record.Orchestrator} {record.Name} --force' again once that is dealt with.",
                    [$"seed {layout.SeedFile(record.Name)}"]);
            }

            var details = new List<string> { $"seed {layout.SeedFile(record.Name)}" };
            var head = await _gitClient.ResolveCommitAsync(main, "HEAD", CancellationToken.None).ConfigureAwait(false);

            // A path committed between the agent's base and the main tree's HEAD is in neither the seed nor the agent's base.
            if (handed > 0 && head is not null && head != record.Base)
            {
                details.Add(
                    $"its base {OrchestrationReports.Base(record.Base)} is not the main tree's HEAD {OrchestrationReports.Base(head)}: a path committed "
                    + "between the two is not handed to it, and a fold refuses any such path it changes");
            }

            return CommandOutcome.Ok($"seeded agent '{record.Name}' of '{record.Orchestrator}' with {handed} path(s)", details);
        }
    }

    public async Task<CommandOutcome> RefreshAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        IReadOnlyList<string> paths,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if ((Shape(orchestrator, agent) ?? PathsProblem(paths, "a path to refresh")) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var at = await LiveAgentAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (at.Refusal is { } refusal)
        {
            return refusal;
        }

        var outcome = await RefreshLiveAsync(at, paths, apply, cancellationToken).ConfigureAwait(false);
        return apply ? _log.Record(at.Layout!, agent, RefreshCommand, outcome) : outcome;
    }

    /// <summary>Refreshing a live agent, once it is found.</summary>
    private async Task<CommandOutcome> RefreshLiveAsync(AgentAt at, IReadOnlyList<string> paths, bool apply, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (at.Context!, at.Layout!, at.Record!, at.Path!);
        var floor = Floor(context);
        var main = context.Layout.MainCheckoutRoot;

        if (_store.ReadSeed(layout, record.Name) is not { } seed)
        {
            return CommandOutcome.Refused($"{Agent(record)} was never seeded, so nothing it was handed can be refreshed.");
        }

        var prefixes = paths.Count > 0 ? [.. paths.Select(PathPatterns.Normalize)] : await RegistryDirectoriesAsync(context, cancellationToken).ConfigureAwait(false);

        if (prefixes.FirstOrDefault(prefix => TreeFloor.Covers(floor, prefix)) is { } floored)
        {
            return CommandOutcome.Usage($"'{floored}' is never handed to an agent: {string.Join(", ", floor)} stay in their own tree.");
        }

        var moved = new List<string>();

        foreach (var relative in await _fold.HandableAsync(main, floor, prefixes, cancellationToken).ConfigureAwait(false))
        {
            if (!await _fold.SameAsync(Path.Combine(main, relative), Path.Combine(path, relative), cancellationToken).ConfigureAwait(false))
            {
                moved.Add(relative);
            }
        }

        // Never over a change of the agent's own - an edit, or a deletion - handed to it or not.
        var changed = await _fold.EditedAsync(path, record.Base!, seed, moved, cancellationToken).ConfigureAwait(false);

        if (changed.Count > 0)
        {
            return CommandOutcome.Refused(
                $"{Agent(record)} changed {changed.Count} of the path(s) to refresh - {ReportText.Listed(changed)} - and refreshing would undo those "
                + "changes. Nothing was copied.");
        }

        if (moved.Count == 0)
        {
            return CommandOutcome.Ok($"agent '{record.Name}' of '{record.Orchestrator}' holds the main tree's copy of every changed path under {string.Join(", ", prefixes)}");
        }

        if (!apply)
        {
            return CommandOutcome.Ok(
                $"dry run: {moved.Count} path(s) would be refreshed into agent '{record.Name}' of '{record.Orchestrator}'; pass --apply to copy them",
                [.. moved.Select(relative => $"  {relative}")]);
        }

        var hold = await HoldAsync(context.Layout, RefreshCommand, cancellationToken, main).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            var (updated, handed, stopped) = await _fold.HandAsync(main, path, moved, seed, CancellationToken.None).ConfigureAwait(false);
            _store.WriteSeed(layout, record.Name, updated);

            return stopped is not null
                ? CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Refreshing {Lower(Agent(record))} stopped after {handed} of {moved.Count} path(s): {stopped}. Its seed records the {handed} copied; "
                    + $"run '{ToolPackage.Command} {RefreshCommand} {record.Orchestrator} {record.Name} --apply' again once that is dealt with.",
                    [$"seed {layout.SeedFile(record.Name)}"])
                : CommandOutcome.Ok(
                    $"refreshed {moved.Count} path(s) into agent '{record.Name}' of '{record.Orchestrator}', recorded as handed to it, so its fold leaves them out",
                    [.. moved.Select(relative => $"  {relative}"), $"seed {layout.SeedFile(record.Name)}"]);
        }
    }

    public async Task<CommandOutcome> FoldAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        IReadOnlyList<string> settled,
        bool apply,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settled);

        if ((Shape(orchestrator, agent) ?? PathsProblem(settled, "--settled")) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var at = await LiveAgentAsync(startDirectory, orchestrator, agent, cancellationToken).ConfigureAwait(false);

        if (at.Refusal is { } refusal)
        {
            return refusal;
        }

        var outcome = await FoldLiveAsync(at, settled, apply, cancellationToken).ConfigureAwait(false);
        return apply ? _log.Record(at.Layout!, agent, FoldCommand, outcome) : outcome;
    }

    /// <summary>Folding a live agent, once it is found.</summary>
    private async Task<CommandOutcome> FoldLiveAsync(AgentAt at, IReadOnlyList<string> settled, bool apply, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (at.Context!, at.Layout!, at.Record!, at.Path!);

        if (await UnfoldableAsync(layout, record, path, cancellationToken).ConfigureAwait(false) is { } unfoldable)
        {
            return unfoldable;
        }

        var seed = _store.ReadSeed(layout, record.Name)!;

        if (!apply)
        {
            var measured = await MeasureAsync(context, layout, record, path, seed, settled, "folded", cancellationToken).ConfigureAwait(false);

            return measured.Refusal ?? CommandOutcome.Ok(
                $"dry run: folding agent '{record.Name}' of '{record.Orchestrator}' writes {measured.Plan!.Written.Count} path(s) into the main tree, removes "
                + $"{measured.Plan.Deleted.Count} and applies {Planned(measured.Rows)} row(s); pass --apply to write them",
                [.. OrchestrationReports.FoldLines(measured.Plan), .. RowsHeading(measured.Rows, layout, record.Name), .. OrchestrationReports.RowLines(measured.Rows)]);
        }

        // The agent's worktree is held too: a leg building in it would be writing what is being folded.
        var hold = await HoldAsync(context.Layout, FoldCommand, cancellationToken, context.Layout.MainCheckoutRoot, _fileSystem.ResolveLinks(path)).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            var measured = await MeasureAsync(context, layout, record, path, seed, settled, "folded", cancellationToken).ConfigureAwait(false);

            if (measured.Refusal is { } refused)
            {
                return refused;
            }

            var (failure, batch) = await WriteAsync(context, record, path, measured).ConfigureAwait(false);

            return failure ?? CommandOutcome.Ok(
                $"folded agent '{record.Name}' of '{record.Orchestrator}': wrote {measured.Plan!.Written.Count} path(s) into the main tree, removed "
                + $"{measured.Plan.Deleted.Count} and applied {Planned(batch)} row(s); its worktree is kept",
                [.. OrchestrationReports.FoldLines(measured.Plan), .. RowsHeading(batch, layout, record.Name), .. OrchestrationReports.RowLines(batch)]);
        }
    }

    public async Task<CommandOutcome> DeleteAsync(
        string startDirectory,
        string orchestrator,
        string agent,
        IReadOnlyList<string> settled,
        bool apply,
        bool discardUncommitted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settled);

        if ((Shape(orchestrator, agent) ?? PathsProblem(settled, "--settled")) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        if (discardUncommitted && settled.Count > 0)
        {
            return CommandOutcome.Usage("--settled leaves paths out of a fold, and --discard-uncommitted folds nothing: give one or the other.");
        }

        var (context, layout, _) = await OrchestratorAsync(startDirectory, orchestrator, cancellationToken).ConfigureAwait(false);

        if (_store.ReadAgent(layout, agent) is not { } record)
        {
            return CommandOutcome.Refused($"Orchestrator '{orchestrator}' has no agent named '{agent}'.");
        }

        var outcome = await DeleteFoundAsync(context, layout, record, settled, apply, discardUncommitted, cancellationToken).ConfigureAwait(false);
        return apply ? _log.Record(layout, agent, DeleteCommand, outcome) : outcome;
    }

    /// <summary>Deleting an agent, once its record is found.</summary>
    private async Task<CommandOutcome> DeleteFoundAsync(
        HarnessContext context,
        OrchestratorLayout layout,
        AgentRecord record,
        IReadOnlyList<string> settled,
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
            ? await FinishAsync(target, settled, apply, cancellationToken).ConfigureAwait(false)
            : await CloseAsync(target, settled, apply, discardUncommitted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deleting a live agent: its fold and rows - or nothing, abandoning it - then its transcripts and evidence kept, its
    /// closing recorded, and its worktree's removal asked for.
    /// </summary>
    private async Task<CommandOutcome> CloseAsync(Target target, IReadOnlyList<string> settled, bool apply, bool discard, CancellationToken cancellationToken)
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

        SeedRecord? seed = null;

        if (!discard)
        {
            if (await UnfoldableAsync(layout, record, path, cancellationToken).ConfigureAwait(false) is { } unfoldable)
            {
                return unfoldable;
            }

            seed = _store.ReadSeed(layout, record.Name)!;
        }

        if (!apply)
        {
            var dry = await MeasureClosingAsync(target, seed, settled, discard, cancellationToken).ConfigureAwait(false);

            if (dry.Refusal is { } refusal)
            {
                return refusal;
            }

            var evidence = dry.Evidence.Files.Count == 0
                ? "its evidence roots hold nothing to keep"
                : $"{dry.Evidence.Files.Count} evidence file(s), to be kept in a directory named for the moment in '{layout.EvidenceDirectory(record.Name)}'";

            return CommandOutcome.Ok(
                $"dry run: deleting {Lower(Agent(record))} "
                + (discard ? $"discards {dry.Discarded.Count} changed path(s), folding nothing" : $"folds {dry.Fold!.Plan!.Written.Count} path(s), removes {dry.Fold.Plan.Deleted.Count} and applies {Planned(dry.Fold.Rows)} row(s)")
                + $", keeps its transcripts and {dry.Evidence.Files.Count} evidence file(s), then removes its worktree and its copies on hosts; pass --apply to do it",
                [
                    .. discard
                        ? [$"{dry.Discarded.Count} changed path(s) discarded:", .. dry.Discarded.Select(relative => $"  {relative}")]
                        : OrchestrationReports.FoldLines(dry.Fold!.Plan!).Concat(RowsHeading(dry.Fold.Rows, layout, record.Name)).Concat(OrchestrationReports.RowLines(dry.Fold.Rows)),
                    evidence,
                    TranscriptsPlan(record),
                ]);
        }

        var hold = await HoldAsync(context.Layout, DeleteCommand, cancellationToken, context.Layout.MainCheckoutRoot, _fileSystem.ResolveLinks(path)).ConfigureAwait(false);

        if (hold.Refusal is { } held)
        {
            return held;
        }

        await using (hold.Handles)
        {
            var measured = await MeasureClosingAsync(target, seed, settled, discard, cancellationToken).ConfigureAwait(false);

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

            if (!discard)
            {
                var fold = measured.Fold!;
                var (failure, batch) = await WriteAsync(context, record, path, fold).ConfigureAwait(false);

                if (failure is not null)
                {
                    return failure with { Message = $"{failure.Message} Nothing was removed." };
                }

                lines.AddRange(OrchestrationReports.FoldLines(fold.Plan!));
                lines.AddRange(RowsHeading(batch, layout, record.Name));
                lines.AddRange(OrchestrationReports.RowLines(batch));
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
                    var after = await _fold.MeasureAsync(context.Layout.MainCheckoutRoot, path, record.Base!, seed!, settled, Floor(context), CancellationToken.None).ConfigureAwait(false);

                    if (!after.NothingLeft)
                    {
                        return CommandOutcome.Failed(
                            HarnessExit.Incomplete,
                            $"{Agent(record)} was not deleted: after its fold was written, it still differs from the main tree - "
                            + $"{ReportText.Listed([.. after.Written, .. after.Deleted, .. after.Refusals])}. {what}, and its worktree is kept; nothing was removed.",
                            lines);
                    }
                }

                var transcripts = await TranscriptsAsync(layout, record).ConfigureAwait(false);
                lines.AddRange(transcripts.Lines);

                if (transcripts.Failed is { } lost)
                {
                    return CommandOutcome.Failed(
                        stopped,
                        $"{Agent(record)} was not deleted, because its transcripts could not be kept: {lost}. {what}; its worktree is kept, and nothing "
                        + $"was removed. Run {OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, discard ? "--apply --discard-uncommitted" : "--apply")} again once that is dealt with.",
                        lines);
                }

                // The evidence roots named before the fold and after it: a fold can bring a change of worktrees.evidenceRoots into
                // the main tree's configuration, and a root it dropped would go unkept.
                var reloaded = await _contextLoader.LoadAsync(context.Layout.MainCheckoutRoot, CancellationToken.None).ConfigureAwait(false);
                var roots = context.Config.Worktrees.EvidenceRoots.Concat(reloaded.Config.Worktrees.EvidenceRoots).Distinct(StringComparer.Ordinal).ToList();
                var (destination, keeping) = FreshEvidence(layout, record.Name);
                kept = await _evidence.KeepAsync(path, roots, measured.Evidence.Files, destination, CancellationToken.None).ConfigureAwait(false);

                if (kept.Problem is { } problem)
                {
                    return CommandOutcome.Failed(
                        stopped,
                        $"{Agent(record)} was not deleted, because its evidence could not be kept: {problem}. {what}; its worktree and its evidence are "
                        + "kept, and nothing was removed.",
                        lines);
                }

                lines.Add(EvidenceLine(kept, destination, measured.Evidence.LinkedRoots));
                var holding = await _fold.HeldAsync(path, seed?.Paths.Keys ?? Enumerable.Empty<string>(), Floor(context), CancellationToken.None).ConfigureAwait(false);
                target = target with { Record = Close(target, stamp, discard, kept.Kept.Count > 0 ? keeping : string.Empty, holding) };
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return CommandOutcome.Failed(
                    stopped,
                    $"{Agent(record)} was not deleted: {ex.Message.TrimEnd('.')}. {what}; its worktree is kept, and nothing was removed. Run "
                    + $"{OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, discard ? "--apply --discard-uncommitted" : "--apply")} again once that is dealt with.",
                    lines);
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
            Closing = new AgentClosing { At = _clock.GetUtcNow(), Abandoned = abandoned, Evidence = evidence, Stamp = stamp, Held = held },
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
            return Closed(target.Record, ex.Message.TrimEnd('.'), lines);
        }
    }

    /// <summary>
    /// Deleting a closed agent again: nothing is folded; its worktree is compared with what closing it recorded, never with
    /// the main tree, its evidence and transcripts are kept again, and its removal is asked for again.
    /// </summary>
    private async Task<CommandOutcome> FinishAsync(Target target, IReadOnlyList<string> settled, bool apply, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (target.Context, target.Layout, target.Record, target.Path);
        var closing = record.Closing!;

        if (settled.Count > 0)
        {
            return CommandOutcome.Usage(
                $"--settled leaves paths out of a fold, and {Lower(Agent(record))}, closed at {OrchestrationReports.Moment(closing.At)}, is never folded "
                + "again. Nothing was read, written or removed.");
        }

        var was = $"{Agent(record)} was closed at {OrchestrationReports.Moment(closing.At)} - "
            + (closing.Abandoned ? "abandoned, with nothing folded" : "its fold and rows are in the main tree")
            + (closing.Evidence.Length > 0 ? $", its evidence kept in '{layout.KeptEvidence(record.Name, closing.Evidence)}'" : string.Empty)
            + " - and its worktree's removal did not finish";
        var removal = $"'{ToolPackage.Command} {WorktreeService.DeleteCommand} {target.Address.Name}";
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
                        + $"--discard-uncommitted --delete-evidence' removes it, and {again} then finishes this agent.");
                }

                if (stamp != closing.Stamp)
                {
                    return CommandOutcome.Refused(
                        $"{was}. '{path}' is not the worktree closed then: git knows it as another, made there since. Nothing of it was read or removed, "
                        + $"and it is not this agent's: {removal}' removes it once what it holds is kept, and {again} then finishes this agent.");
                }

                var changed = await _fold
                    .ChangedSinceAsync(context.Layout.MainCheckoutRoot, path, closing, _store.ReadSeed(layout, record.Name), Floor(context), cancellationToken)
                    .ConfigureAwait(false);

                if (changed.Count > 0)
                {
                    return CommandOutcome.Failed(
                        HarnessExit.Incomplete,
                        $"{was}, and its worktree holds {changed.Count} file(s) its closing did not record, changed or new since: work done in an agent "
                        + $"already closed, which nothing folds again and nothing discards unseen. Copy it out, then remove the worktree with {removal} "
                        + $"--discard-uncommitted --delete-evidence', and {again} finishes this agent.",
                        [.. changed.Select(relative => $"  {relative}")]);
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
                    return Closed(record, $"its transcripts could not be kept: {lost}", lines);
                }

                if (target.Standing.Kind == Standing.Gone)
                {
                    return await RemoveAsync(target, lines).ConfigureAwait(false);
                }

                // Kept again, into a directory of its own: a run that held a file open when the removal stopped may have gone
                // on writing it, and one directory for both would refuse every run after the first as a clash.
                var (destination, _) = FreshEvidence(layout, record.Name);
                var found = await _evidence.FindAsync(path, context.Config.Worktrees.EvidenceRoots, CancellationToken.None).ConfigureAwait(false);
                var kept = await _evidence.KeepAsync(path, context.Config.Worktrees.EvidenceRoots, found.Files, destination, CancellationToken.None).ConfigureAwait(false);

                if (kept.Problem is { } problem)
                {
                    return Closed(record, $"its evidence could not be kept again: {problem}", lines);
                }

                lines.Add(EvidenceLine(kept, destination, found.LinkedRoots));

                if (target.Standing.Kind == Standing.Husk)
                {
                    // Never forced from here: --force checks nothing, and a directory with no .git of its own is what a removal
                    // that stopped part way leaves - or a directory made there since, which nothing here can tell apart.
                    return CommandOutcome.Failed(
                        HarnessExit.Incomplete,
                        $"{was}. '{path}' is no longer a git worktree - it holds no .git of its own - which is what a removal that stopped part way "
                        + "leaves, or a directory made there since. Look at what else it holds; once nothing in it is needed, "
                        + $"{removal} --force' removes it - nothing here forces it - and {again} then finishes this agent.",
                        lines);
                }

                return await AfterClosingAsync(target, kept.Kept, lines).ConfigureAwait(false);
            }
            catch (Exception ex) when (Unfinished(ex))
            {
                return Closed(record, ex.Message.TrimEnd('.'), lines);
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
                    [.. transcripts.Lines]);
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
                    [.. transcripts.Lines]);
            }
        }
    }

    /// <summary>
    /// Removes an agent's worktree and the copies hosts keep of it through delete-worktree - never forced, its evidence
    /// check kept - then proves it: the directory gone, git's record of it gone, and no copy of it recorded on a host. Only
    /// then is the agent deleted; anything left makes the deletion incomplete, naming what is left.
    /// </summary>
    private async Task<CommandOutcome> RemoveAsync(Target target, List<string> lines)
    {
        var record = target.Record;
        var abandoned = record.Closing?.Abandoned ?? true;
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
                ? Closed(record, string.Join("; ", left), lines)
                : CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"{Agent(record)} is not deleted yet: {string.Join("; ", left)}. Deal with what is named, then run "
                    + $"{OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, "--apply --discard-uncommitted")} again.",
                    lines);
        }

        _store.UpdateAgent(target.Layout, record.Name, current => current with { State = AgentStates.Deleted, Closing = null, DeletedAt = _clock.GetUtcNow(), Abandoned = abandoned });

        return CommandOutcome.Ok(
            $"deleted agent '{record.Name}' of '{record.Orchestrator}': {(abandoned ? "abandoned, with nothing folded" : "its fold and rows are in the main tree")}, "
            + "and its worktree, git's record of it and every copy of it recorded on a host are gone; its directory is kept as its history",
            [.. lines, $"record {target.Layout.AgentRecordFile(record.Name)}"]);
    }

    /// <summary>What is said of a closed agent whose removal stopped: what stopped it, what is in, and that running delete-agent again finishes it.</summary>
    private static CommandOutcome Closed(AgentRecord record, string why, IReadOnlyList<string> lines)
        => CommandOutcome.Failed(
            HarnessExit.Incomplete,
            $"{Agent(record)} is closed and not deleted yet: {why}. "
            + (record.Closing?.Abandoned ?? false ? "Nothing of it was folded" : "Its fold and rows are in the main tree")
            + $", and it is closed, so nothing folds it again. Deal with what is named, then run {OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name)} "
            + "again: it folds nothing, and finishes the removal or names what stops it.",
            lines);

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
    private async Task<ClosingMeasure> MeasureClosingAsync(Target target, SeedRecord? seed, IReadOnlyList<string> settled, bool discard, CancellationToken cancellationToken)
    {
        var (context, layout, record, path) = (target.Context, target.Layout, target.Record, target.Path);
        var none = new EvidenceFound(new Dictionary<string, string>(), []);
        Measured? fold = null;
        IReadOnlyList<string> discarded = [];

        if (discard)
        {
            discarded = await _fold.ChangedAsync(path, Floor(context), "what the agent changed cannot be told", within: null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            fold = await MeasureAsync(context, layout, record, path, seed!, settled, "deleted", cancellationToken).ConfigureAwait(false);

            if (fold.Refusal is { } refusal)
            {
                return new ClosingMeasure(fold, discarded, none, refusal);
            }
        }

        try
        {
            return new ClosingMeasure(fold, discarded, await _evidence.FindAsync(path, context.Config.Worktrees.EvidenceRoots, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClosingMeasure(fold, discarded, none, CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"{Agent(record)} was not deleted: {ex.Message.TrimEnd('.')}, and an unread evidence root is not an empty one. Nothing was written or removed."));
        }
    }

    /// <summary>An agent's fold and rows, measured and checked: nothing is written, and every refusal of either is collected.</summary>
    private async Task<Measured> MeasureAsync(
        HarnessContext context,
        OrchestratorLayout layout,
        AgentRecord record,
        string path,
        SeedRecord seed,
        IReadOnlyList<string> settled,
        string verb,
        CancellationToken cancellationToken)
    {
        var main = context.Layout.MainCheckoutRoot;
        var nothing = verb == "deleted" ? "Nothing was written or removed." : "Nothing was written.";
        FoldPlan plan;

        try
        {
            plan = await _fold.MeasureAsync(main, path, record.Base!, seed, settled, Floor(context), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Measured(null, [], new AnchorBatch([], [], Written: false), CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"{Agent(record)} was not {verb}: what it changed cannot be read - {ex.Message.TrimEnd('.')}. {nothing}"));
        }

        var (rows, problems) = AgentRows.Read(_fileSystem, layout.RowsDirectory(record.Name));
        var batch = problems.Count > 0 ? new AnchorBatch([], problems, Written: false) : await _anchors.ApplyAsync(main, rows, dryRun: true, cancellationToken).ConfigureAwait(false);

        if (plan.Refusals.Count == 0 && batch.Succeeded)
        {
            return new Measured(plan, rows, batch, null);
        }

        var what = new List<string>();
        var lines = new List<string>();

        if (plan.Refusals.Count > 0)
        {
            what.Add($"{plan.Refusals.Count} of its paths cannot be folded");
            lines.AddRange(OrchestrationReports.FoldRefusalLines(plan));
        }
        else
        {
            lines.AddRange(OrchestrationReports.FoldLines(plan));
        }

        if (!batch.Succeeded)
        {
            what.Add($"{batch.Problems.Count} problem(s) with its rows");
            lines.AddRange(RowsHeading(batch, layout, record.Name));
            lines.AddRange(OrchestrationReports.RowLines(batch));
        }

        return new Measured(plan, rows, batch, CommandOutcome.Refused($"{Agent(record)} was not {verb}: {string.Join(", and ", what)}. {nothing}", lines));
    }

    /// <summary>
    /// Writes a measured fold into the main tree, then the agent's rows into its registries, never interrupted: the rows as
    /// written, or - once anything is written - what stopped it, as a change begun and not finished.
    /// </summary>
    private async Task<(CommandOutcome? Failure, AnchorBatch Batch)> WriteAsync(HarnessContext context, AgentRecord record, string path, Measured measured)
    {
        var main = context.Layout.MainCheckoutRoot;
        var plan = measured.Plan!;
        var applied = await _fold.ApplyAsync(main, path, plan).ConfigureAwait(false);

        if (applied.Stopped is { } why)
        {
            return (CommandOutcome.Failed(
                HarnessExit.Incomplete,
                $"Folding {Lower(Agent(record))} stopped part way, after writing {applied.Written} of {plan.Written.Count} path(s) and removing "
                + $"{applied.Deleted} of {plan.Deleted.Count}: {why.TrimEnd('.')}. What it wrote is in the main tree, and its rows were not applied; run it "
                + "again once that is dealt with - a fold run again finds what it wrote already in, and measures the rest afresh."), measured.Rows);
        }

        if (measured.Declared.Count == 0)
        {
            return (null, measured.Rows);
        }

        AnchorBatch batch;

        try
        {
            batch = await _anchors.ApplyAsync(main, measured.Declared, dryRun: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (Unfinished(ex))
        {
            batch = new AnchorBatch(measured.Rows.Rows, [], Written: false) { Failure = ex.Message.TrimEnd('.') };
        }

        return batch.Succeeded
            ? (null, batch)
            : (CommandOutcome.Failed(
                HarnessExit.Incomplete,
                $"{Agent(record)} is folded into the main tree, and applying its rows failed{(batch.Failure is { } failure ? $": {failure}" : string.Empty)}. Its "
                + "worktree is kept. Correct the cause and run again: the fold finds its own writes already in, and a row already as declared is not "
                + "written again.",
                [.. OrchestrationReports.RowLines(batch)]), batch);
    }

    /// <summary>Why a live agent's work cannot be folded as it stands: no base, never seeded, or a commit made inside it.</summary>
    private async Task<CommandOutcome?> UnfoldableAsync(OrchestratorLayout layout, AgentRecord record, string path, CancellationToken cancellationToken)
    {
        if (record.Base is null)
        {
            return CommandOutcome.Refused(
                $"{Agent(record)} records no commit its worktree was made from, as when making it stopped part way, so nothing of it can be measured; "
                + $"{OrchestrationReports.DeleteAgentLine(record.Orchestrator, record.Name, "--apply --discard-uncommitted")} deletes it with nothing folded.");
        }

        if (_store.ReadSeed(layout, record.Name) is null)
        {
            return CommandOutcome.Refused(
                $"{Agent(record)} was never seeded, and its seed is what tells its work from what it was handed: '{ToolPackage.Command} {SeedCommand} "
                + $"{record.Orchestrator} {record.Name}' seeds it, with --empty where it was handed nothing.");
        }

        var head = await _gitClient.ResolveCommitAsync(path, "HEAD", cancellationToken).ConfigureAwait(false);

        return head == record.Base
            ? null
            : CommandOutcome.Refused(
                $"{Agent(record)}'s HEAD is {OrchestrationReports.Base(head)}, and its base is {OrchestrationReports.Base(record.Base)}: a commit made inside "
                + "it hides its changes from git status, the only list of paths a fold reads, so folding it now would take part of its work and silently "
                + $"drop the rest. Agents never commit; 'git -C \"{path}\" reset --soft {record.Base}' turns its commits back into uncommitted changes a "
                + "fold reads, where that is what is wanted.");
    }

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

    /// <summary>A live agent whose worktree git records as its own: what seeding, refreshing and folding work on.</summary>
    private async Task<AgentAt> LiveAgentAsync(string startDirectory, string orchestrator, string agent, CancellationToken cancellationToken)
    {
        var (context, layout, _) = await OrchestratorAsync(startDirectory, orchestrator, cancellationToken).ConfigureAwait(false);

        if (_store.ReadAgent(layout, agent) is not { } record)
        {
            return new AgentAt(CommandOutcome.Refused($"Orchestrator '{orchestrator}' has no agent named '{agent}'."));
        }

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

        if (record.Base is null)
        {
            return new AgentAt(CommandOutcome.Refused(
                $"{Agent(record)} records no commit its worktree was made from, as when making it stopped part way; "
                + $"{OrchestrationReports.DeleteAgentLine(orchestrator, agent, "--apply --discard-uncommitted")} deletes it with nothing folded."));
        }

        return new AgentAt(null, context, layout, record, path);
    }

    /// <summary>
    /// Where an agent's worktree is: under the worktrees root it was made under, which the configuration must still name,
    /// since delete-worktree looks for it under the root the configuration names. Why not, where it does not.
    /// </summary>
    private static (string Path, string? Moved) WorktreeOf(HarnessContext context, AgentRecord record)
    {
        var configured = PathPatterns.Normalize(context.Config.Worktrees.Root);
        var path = WorktreeAddress.Nested(record.Orchestrator, record.Name).PathUnder(context.Layout.WorktreesDirectoryUnder(record.WorktreesRoot));

        return string.Equals(configured, record.WorktreesRoot, StringComparison.Ordinal)
            ? (path, null)
            : (path, $"{Agent(record)}'s worktree was made under worktrees root '{record.WorktreesRoot}', and the configuration names '{configured}' now, "
                + $"where delete-worktree looks for it. Nothing was read, written or removed: name '{record.WorktreesRoot}' as worktrees.root again while "
                + "the agent's worktree is there.");
    }

    /// <summary>What the directory at an agent's worktree path is to git.</summary>
    private async Task<WorktreeStanding> StandingAsync(HarnessContext context, string path, CancellationToken cancellationToken)
    {
        if (!_fileSystem.DirectoryExists(path))
        {
            return new WorktreeStanding(Standing.Gone, null, null);
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
            + $"'{ToolPackage.Command} {WorktreeService.DeleteCommand} {WorktreeAddress.Nested(record.Orchestrator, record.Name).Name} --force' once what it holds is kept.",
        Standing.Foreign => $"{Agent(record)}: '{path}' is not a worktree of this repository - {standing.Why}. Nothing was read, written or removed.",
        _ => $"Whether '{path}', the worktree of {Lower(Agent(record))}, is a worktree of this repository cannot be told: {standing.Why?.TrimEnd('.')}. Nothing was read, written or removed.",
    };

    /// <summary>
    /// Takes each of <paramref name="trees"/> on this machine whole, as a sync takes a copy: no leg builds in it, and no other
    /// fold, seed or deletion writes it, meanwhile. All of them, or none.
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
                return new Hold(new Handles([]), CommandOutcome.Refused($"{attempt.HeldBy} Nothing was changed; run it again once that is done."));
            }

            handles.Add(handle);
        }

        return new Hold(new Handles(handles), null);
    }

    /// <summary>
    /// Copies the agent's Claude transcripts into its logs directory: the lines that say what was kept and where, and why a
    /// transcript found could not be kept. None found, or a directory that could not be looked in, is said and stops nothing.
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
            ? $"no Claude session is recorded for it, so no transcript is looked for; '{ToolPackage.Command} {CreateCommand} {record.Orchestrator} {record.Name} --model {record.Model} --session <id>' records one"
            : $"transcripts: session {record.Session}'s, where Claude Code keeps them";

    private static string EvidenceLine(EvidenceKept kept, string destination, IReadOnlyList<string> linked)
        => (kept.Kept.Count == 0 ? "its evidence roots held nothing to keep" : $"kept {kept.Kept.Count} evidence file(s) in '{destination}', each read back")
            + (linked.Count == 0 ? string.Empty : $"; {string.Join(", ", linked)} {(linked.Count == 1 ? "is a link" : "are links")}, and what a link leads to stays where it is");

    private static IEnumerable<string> RowsHeading(AnchorBatch batch, OrchestratorLayout layout, string agent)
        => batch.Rows.Count == 0 && batch.Problems.Count == 0 ? [] : [$"rows filed in '{layout.RowsDirectory(agent)}':"];

    private static int Planned(AnchorBatch batch) => batch.Rows.Count(row => row.Action != AnchorRowAction.AlreadyIn);

    /// <summary>A keeping of the agent's evidence that does not exist yet, named for this moment: its directory, and its name as records give it.</summary>
    private (string Directory, string Keeping) FreshEvidence(OrchestratorLayout layout, string agent)
    {
        var stamp = _clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var name = stamp;

        for (var suffix = 2; _fileSystem.DirectoryExists(layout.KeptEvidence(agent, OrchestratorLayout.KeptEvidence(name))); suffix++)
        {
            name = $"{stamp}-{suffix}";
        }

        var keeping = OrchestratorLayout.KeptEvidence(name);
        return (layout.KeptEvidence(agent, keeping), keeping);
    }

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
    private static string? PathsProblem(IReadOnlyList<string> paths, string what)
        => paths.Select(path => OrchestrationRules.RelativePathProblem(path.TrimEnd('/', '\\'), what)).FirstOrDefault(problem => problem is not null);

    /// <summary>Whether <paramref name="exception"/> is one a step after a first write can meet and must answer, rather than let escape.</summary>
    private static bool Unfinished(Exception exception)
        => exception is IOException or UnauthorizedAccessException or HarnessException or JsonException;

    private static string Agent(AgentRecord record) => $"Agent '{record.Name}' of '{record.Orchestrator}'";

    /// <summary>A sentence's opening words, to follow others.</summary>
    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

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

    /// <summary>A live agent to work on, or why it cannot be.</summary>
    private sealed record AgentAt(
        CommandOutcome? Refusal,
        HarnessContext? Context = null,
        OrchestratorLayout? Layout = null,
        AgentRecord? Record = null,
        string? Path = null);

    /// <summary>An agent being deleted, and what its worktree is.</summary>
    private sealed record Target(HarnessContext Context, OrchestratorLayout Layout, AgentRecord Record, WorktreeAddress Address, string Path, WorktreeStanding Standing);

    /// <summary>An agent's fold and rows, measured, and why they cannot be written where they cannot.</summary>
    private sealed record Measured(FoldPlan? Plan, IReadOnlyList<AnchorRowDeclaration> Declared, AnchorBatch Rows, CommandOutcome? Refusal);

    /// <summary>What deleting a live agent measured before writing anything, and why it cannot go on where it cannot.</summary>
    private sealed record ClosingMeasure(Measured? Fold, IReadOnlyList<string> Discarded, EvidenceFound Evidence, CommandOutcome? Refusal);

    /// <summary>The trees held, or why they could not be.</summary>
    private sealed record Hold(Handles Handles, CommandOutcome? Refusal);

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
