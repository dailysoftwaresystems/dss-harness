using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Orchestration;

/// <summary>Creating, deleting and listing orchestrators.</summary>
public interface IOrchestratorService
{
    /// <summary>
    /// Creates an orchestrator: its directory under <c>.orchestrators</c>, its record, its logs, work and plans
    /// directories, and the directory its agents are kept in. Run again for one that exists, it changes only the limit
    /// and the session, and refuses a model other than the one it was created for.
    /// </summary>
    /// <param name="startDirectory">A directory in the repository.</param>
    /// <param name="name">The orchestrator's name, held to the worktree name rule.</param>
    /// <param name="model">The model the orchestrating session runs on.</param>
    /// <param name="parallel">The most agents with a worktree at once; <see cref="OrchestratorRecord.DefaultParallel"/> for a new one where none is given.</param>
    /// <param name="session">The orchestrating session's Claude session id, where it is known.</param>
    /// <param name="cancellationToken">Stops it.</param>
    Task<CommandOutcome> CreateAsync(string startDirectory, string name, string model, int? parallel, string? session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an orchestrator, once none of its agents is live or closed and no worktree is left below its directory
    /// under the worktrees root. The evidence and transcripts its agents kept go with it only with
    /// <paramref name="deleteEvidence"/>.
    /// </summary>
    /// <param name="startDirectory">A directory in the repository.</param>
    /// <param name="name">The orchestrator's name.</param>
    /// <param name="deleteEvidence">Whether the evidence and transcripts its agents kept may go with it.</param>
    /// <param name="cancellationToken">Stops it.</param>
    Task<CommandOutcome> DeleteAsync(string startDirectory, string name, bool deleteEvidence, CancellationToken cancellationToken = default);

    /// <summary>Lists the orchestrators, or the one named, with their agents and where each stands.</summary>
    /// <param name="startDirectory">A directory in the repository.</param>
    /// <param name="name">The orchestrator to list, or null for all of them.</param>
    /// <param name="json">Whether to answer with one JSON document rather than lines.</param>
    /// <param name="cancellationToken">Stops it.</param>
    Task<CommandOutcome> ListAsync(string startDirectory, string? name, bool json, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IOrchestratorService"/>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="gitClient">Asks git whether the orchestrators directory is ignored.</param>
/// <param name="fileSystem">Makes and removes the orchestrator's directories.</param>
/// <param name="platform">How this machine compares paths.</param>
/// <param name="output">Where the worktree inspection says what it saw.</param>
/// <param name="worktrees">The worktrees there are, for the listing.</param>
/// <param name="store">The records.</param>
/// <param name="log">The logs.</param>
/// <param name="clock">When an orchestrator is created.</param>
public sealed class OrchestratorService(
    IHarnessContextLoader contextLoader,
    IGitClient gitClient,
    IFileSystem fileSystem,
    IHostPlatform platform,
    IHarnessOutput output,
    IWorktreeService worktrees,
    OrchestrationStore store,
    OrchestrationLog log,
    TimeProvider clock) : IOrchestratorService
{
    /// <summary>The command that creates one.</summary>
    public const string CreateCommand = "create-orchestrator";

    /// <summary>The command that deletes one.</summary>
    public const string DeleteCommand = "delete-orchestrator";

    /// <summary>The command that lists them.</summary>
    public const string ListCommand = "list-orchestrator";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;
    private readonly IWorktreeService _worktrees = worktrees;
    private readonly OrchestrationStore _store = store;
    private readonly OrchestrationLog _log = log;
    private readonly TimeProvider _clock = clock;

    public async Task<CommandOutcome> CreateAsync(
        string startDirectory,
        string name,
        string model,
        int? parallel,
        string? session,
        CancellationToken cancellationToken = default)
    {
        if ((OrchestrationRules.NameProblem(name)
            ?? OrchestrationRules.ModelProblem(model)
            ?? OrchestrationRules.SessionProblem(session)
            ?? OrchestrationRules.ParallelProblem(parallel)) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;

        if (!WorktreeName.Validate(name, context.Config.Worktrees.MaxNameLength).TryGetName(out _, out var length))
        {
            return CommandOutcome.Usage(length);
        }

        var orchestrator = OrchestratorLayout.Of(layout, name);

        // Everything an orchestrator keeps is machine-local: a record, a log, a transcript committed by `git add -A` would
        // travel to every clone. init keeps the directory in git and everything made in it out.
        var probe = orchestrator.RelativeRecordFile;

        if (!await _gitClient.IsIgnoredAsync(layout.MainCheckoutRoot, probe, cancellationToken).ConfigureAwait(false))
        {
            return CommandOutcome.Refused(
                $"git does not ignore '{probe}', so what orchestrator '{name}' keeps would be committed. Run '{ToolPackage.Command} init', "
                + "which keeps .orchestrators in git and everything made in it out. Nothing was created.");
        }

        var group = WorktreeAddress.Plain(name).PathUnder(layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root));
        var now = _clock.GetUtcNow();

        return _store.Exclusively(orchestrator, () =>
        {
            if (_store.ReadOrchestrator(orchestrator) is { } existing)
            {
                return Change(orchestrator, existing, model, parallel, session);
            }

            // Plain worktrees and orchestrators share the names under the root: the directory named for this one is
            // where its agents' worktrees are made.
            if (_fileSystem.DirectoryExists(group) && (WorktreeInspector.HoldsOwnGit(_fileSystem, group) || !IsEmpty(group)))
            {
                return CommandOutcome.Refused(
                    $"'{group}' is {(WorktreeInspector.HoldsOwnGit(_fileSystem, group) ? "a worktree" : "a directory holding files no orchestrator made")}, and "
                    + $"an orchestrator's agents' worktrees are made there: delete it, or name the orchestrator otherwise. Nothing was created.");
            }

            var record = new OrchestratorRecord
            {
                Kind = OrchestratorRecord.KindName,
                Name = name,
                Model = model,
                CreatedAt = now,
                Parallel = parallel ?? OrchestratorRecord.DefaultParallel,
                Session = session,
            };

            foreach (var directory in new[] { orchestrator.LogsDirectory, orchestrator.WorkRoot, orchestrator.PlansDirectory(name), orchestrator.AgentsDirectory })
            {
                _fileSystem.CreateDirectory(directory);
            }

            _store.WriteOrchestrator(orchestrator, record);

            var outcome = CommandOutcome.Ok(
                $"created orchestrator '{name}'",
                [$"record {orchestrator.RecordFile}", $"plans {orchestrator.PlansDirectory(name)}", $"at most {record.Parallel} agent(s) with a worktree at once"]);

            return _log.Record(orchestrator, name, CreateCommand, outcome);
        });
    }

    /// <summary>What running create-orchestrator again does: changes the limit and the session, and nothing else.</summary>
    private CommandOutcome Change(OrchestratorLayout orchestrator, OrchestratorRecord existing, string model, int? parallel, string? session)
    {
        if (!string.Equals(existing.Model, model, StringComparison.Ordinal))
        {
            return CommandOutcome.Refused(
                $"Orchestrator '{existing.Name}' was created for model '{existing.Model}', not '{model}'. Run again with --model {existing.Model}, "
                + "which changes only its limit and its session. Nothing was changed.");
        }

        var changed = existing with { Parallel = parallel ?? existing.Parallel, Session = session ?? existing.Session };

        if (changed == existing)
        {
            return CommandOutcome.Ok($"orchestrator '{existing.Name}' is already as asked", [$"record {orchestrator.RecordFile}"]);
        }

        _store.WriteOrchestrator(orchestrator, changed);

        var said = new List<string>();

        if (changed.Parallel != existing.Parallel)
        {
            said.Add($"allows at most {changed.Parallel} agent(s) with a worktree at once, where it allowed {existing.Parallel}");
        }

        if (changed.Session != existing.Session)
        {
            said.Add($"records session {changed.Session}");
        }

        return _log.Record(orchestrator, existing.Name, CreateCommand, CommandOutcome.Ok($"orchestrator '{existing.Name}' now {string.Join(", and ", said)}", [$"record {orchestrator.RecordFile}"]));
    }

    public async Task<CommandOutcome> DeleteAsync(
        string startDirectory,
        string name,
        bool deleteEvidence,
        CancellationToken cancellationToken = default)
    {
        if (OrchestrationRules.NameProblem(name) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;
        var orchestrator = OrchestratorLayout.Of(layout, name);
        var worktreesDirectory = layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);
        var group = WorktreeAddress.Plain(name).PathUnder(worktreesDirectory);

        // git's list of worktrees, asked before the orchestrator is held: what is below its directory under the root. A list
        // git cannot give is raised, never read as no worktree below.
        var below = _fileSystem.DirectoryExists(group)
            ? await new WorktreeInspector(_gitClient, _fileSystem, _platform, _output).FindWorktreesBelowAsync(layout.MainCheckoutRoot, worktreesDirectory, group, cancellationToken).ConfigureAwait(false)
            : [];

        // Decided and done with no other command deciding about this orchestrator meanwhile: an agent made beside this
        // deletion would be made into a directory being removed.
        return _store.Exclusively(orchestrator, () =>
        {
            if (_store.ReadOrchestrator(orchestrator) is null)
            {
                return CommandOutcome.Refused(OrchestrationReports.NoOrchestrator(name));
            }

            var agents = _store.Agents(orchestrator);

            if (agents.FirstOrDefault(agent => agent.Record is null) is { } unreadable)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted: what its agent '{unreadable.Name}' is cannot be told - {unreadable.Problem!.TrimEnd('.')}. "
                    + "Nothing was changed.");
            }

            var open = agents.Where(agent => agent.Record!.State != AgentStates.Deleted).Select(agent => agent.Name).ToList();

            if (open.Count > 0)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted: its agent(s) {string.Join(", ", open.Select(agent => $"'{agent}'"))} are not deleted yet. "
                    + $"Delete each with {OrchestrationReports.DeleteAgentLine(name, "<agent>")}. Nothing was changed.");
            }

            if (below.Count > 0)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted: worktrees are left below '{group}' - {ReportText.Listed(below)}. Delete each with "
                    + $"'{ToolPackage.Command} {WorktreeService.DeleteCommand} <address>' first. Nothing was changed.");
            }

            var kept = agents.Select(agent => (agent.Name, Evidence: Files(orchestrator.EvidenceDirectory(agent.Name)), Transcripts: Files(orchestrator.TranscriptsDirectory(agent.Name))))
                .Where(agent => agent.Evidence + agent.Transcripts > 0)
                .ToList();

            if (kept.Count > 0 && !deleteEvidence)
            {
                return CommandOutcome.Refused(
                    $"Orchestrator '{name}' was not deleted, because it keeps {kept.Sum(agent => agent.Evidence)} evidence file(s) and "
                    + $"{kept.Sum(agent => agent.Transcripts)} transcript file(s) of its agents {string.Join(", ", kept.Select(agent => $"'{agent.Name}'"))}. "
                    + "Copy out what you need, or pass --delete-evidence to delete them with it. Nothing was changed.");
            }

            if (Remove(orchestrator) is { } stopped)
            {
                return CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"Orchestrator '{name}' is not deleted yet: {stopped}. Its record is kept, so '{ToolPackage.Command} {DeleteCommand} {name}"
                    + $"{(deleteEvidence ? " --delete-evidence" : string.Empty)}' run again finishes it.");
            }

            if (_fileSystem.DirectoryExists(group) && IsEmpty(group))
            {
                _fileSystem.DeleteDirectory(group);
            }

            return CommandOutcome.Ok(
                $"deleted orchestrator '{name}'",
                [
                    $"removed {orchestrator.Directory}",
                    .. agents.Count == 0 ? [] : new[] { $"with the history of {agents.Count} deleted agent(s)" },
                    .. kept.Count == 0 ? [] : new[] { $"and {kept.Sum(agent => agent.Evidence)} evidence file(s) and {kept.Sum(agent => agent.Transcripts)} transcript file(s)" },
                ]);
        });
    }

    /// <summary>
    /// Removes everything the orchestrator keeps, its record last, so that a removal stopped part way leaves the record,
    /// and the orchestrator is still there to delete again rather than leftovers nothing names. Why it stopped, or null.
    /// </summary>
    private string? Remove(OrchestratorLayout orchestrator)
    {
        try
        {
            foreach (var directory in _fileSystem.EnumerateDirectories(orchestrator.Directory))
            {
                _fileSystem.DeleteDirectory(directory);
            }

            foreach (var file in _fileSystem.EnumerateFiles(orchestrator.Directory, recursive: false).Where(file => !PathContainment.AreSame(file, orchestrator.RecordFile, _platform.PathComparison)))
            {
                _fileSystem.DeleteFile(file);
            }

            _fileSystem.DeleteFile(orchestrator.RecordFile);
            _fileSystem.DeleteDirectory(orchestrator.Directory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    public async Task<CommandOutcome> ListAsync(
        string startDirectory,
        string? name,
        bool json,
        CancellationToken cancellationToken = default)
    {
        if (name is not null && OrchestrationRules.NameProblem(name) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;
        var orchestrators = name is null
            ? _store.Orchestrators(layout)
            : _store.ReadOrchestrator(OrchestratorLayout.Of(layout, name)) is null ? [] : [OrchestratorLayout.Of(layout, name)];

        if (name is not null && orchestrators.Count == 0)
        {
            return CommandOutcome.Refused(OrchestrationReports.NoOrchestrator(name));
        }

        var listed = await _worktrees.ListAsync(layout.MainCheckoutRoot, cancellationToken).ConfigureAwait(false);
        var inventory = new List<OrchestratorInventory>();

        foreach (var orchestrator in orchestrators)
        {
            var record = _store.ReadOrchestrator(orchestrator)!;
            var agents = _store.Agents(orchestrator);

            inventory.Add(new OrchestratorInventory(
                orchestrator,
                record,
                [.. agents.Select(agent => new AgentInventory(
                    agent,
                    listed.FirstOrDefault(worktree => worktree.Name == WorktreeAddress.Nested(orchestrator.Name, agent.Name).Name),
                    WorktreeAddress.Nested(orchestrator.Name, agent.Name).PathUnder(layout.WorktreesDirectoryUnder(agent.Record?.WorktreesRoot ?? context.Config.Worktrees.Root)),
                    Evidence(orchestrator, agent.Name),
                    Files(orchestrator.TranscriptsDirectory(agent.Name))))],
                [.. listed.Where(worktree => WorktreeAddress.AgentOf(orchestrator.Name, worktree.Name) is { } agent && !agents.Any(entry => entry.Name == agent))]));
        }

        return OrchestrationReports.List(inventory, json);
    }

    /// <summary>The keepings of the evidence of <paramref name="agent"/>, each as its records name it.</summary>
    private IReadOnlyList<string> Evidence(OrchestratorLayout orchestrator, string agent)
        => _fileSystem.DirectoryExists(orchestrator.EvidenceDirectory(agent))
            ? [.. _fileSystem.EnumerateDirectories(orchestrator.EvidenceDirectory(agent))
                .Select(directory => OrchestratorLayout.KeptEvidence(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))))
                .Order(StringComparer.Ordinal)]
            : [];

    private int Files(string directory)
        => _fileSystem.DirectoryExists(directory) ? _fileSystem.EnumerateFiles(directory, recursive: true).Count() : 0;

    private bool IsEmpty(string directory)
        => !_fileSystem.EnumerateDirectories(directory).Any() && !_fileSystem.EnumerateFiles(directory, recursive: false).Any();

}

/// <summary>One orchestrator, as list-orchestrator reports it.</summary>
/// <param name="Layout">Where it is kept.</param>
/// <param name="Record">Its record.</param>
/// <param name="Agents">Its agents.</param>
/// <param name="WorktreesWithoutAgent">Worktrees below its directory that no agent of it records.</param>
public sealed record OrchestratorInventory(
    OrchestratorLayout Layout,
    OrchestratorRecord Record,
    IReadOnlyList<AgentInventory> Agents,
    IReadOnlyList<WorktreeListing> WorktreesWithoutAgent);

/// <summary>One agent, as list-orchestrator reports it.</summary>
/// <param name="Entry">Its directory's record, or why it has none that reads.</param>
/// <param name="Worktree">Its worktree, where git records it.</param>
/// <param name="WorktreePath">Where its worktree is, or was.</param>
/// <param name="Evidence">The evidence directories kept for it, relative to its directory.</param>
/// <param name="Transcripts">How many transcript files are kept for it.</param>
public sealed record AgentInventory(
    AgentEntry Entry,
    WorktreeListing? Worktree,
    string WorktreePath,
    IReadOnlyList<string> Evidence,
    int Transcripts);

/// <summary>The context every orchestrator command works in: the main checkout's, whichever tree it was started in.</summary>
internal static class MainTree
{
    /// <summary>
    /// The main checkout's context: orchestrators, their agents' worktrees, and the configuration naming the worktrees
    /// root all belong to it, and an agent's worktree has a configuration of its own, which may say otherwise.
    /// </summary>
    public static async Task<HarnessContext> LoadAsync(IHarnessContextLoader loader, IHostPlatform platform, string startDirectory, CancellationToken cancellationToken)
    {
        var context = await loader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);

        return context.Layout.IsWorktree(platform)
            ? await loader.LoadAsync(context.Layout.MainCheckoutRoot, cancellationToken).ConfigureAwait(false)
            : context;
    }
}
