using System.Globalization;
using System.Text;
using DssHarness.Core.Application;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Worktrees;

/// <summary>Creating, listing and removing development worktrees.</summary>
public interface IWorktreeService : IUseCase
{
    /// <summary>Creates a worktree, generating its name when <paramref name="useRandomName"/> is set.</summary>
    /// <exception cref="HarnessException">
    /// The repository is not initialised, or git could not be reached.
    /// </exception>
    Task<WorktreeOutcome> CreateAsync(
        string startDirectory,
        string? name,
        bool useRandomName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the worktree at <paramref name="address"/>: a plain one, or an orchestrator's agent's, below the
    /// directory named for its orchestrator. Each name is held to worktrees.maxNameLength where the repository sets it,
    /// and the whole path to the path budget, as create-worktree holds a plain one.
    /// </summary>
    /// <exception cref="HarnessException">As for <see cref="CreateAsync"/>.</exception>
    Task<WorktreeOutcome> CreateAtAsync(
        string startDirectory,
        WorktreeAddress address,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a worktree, everything under it and git's record of it. Unless
    /// <paramref name="force"/> is set, it is refused and left untouched when deleting it would lose
    /// uncommitted changes, edits status cannot see, commits on no branch, tag, remote-tracking ref,
    /// newest stash or other worktree's HEAD, or a submodule repository's unpushed commits or stash;
    /// when it is locked or was moved by hand; and when git does not see it as a worktree of this
    /// repository. Ignored files, and ignored directories with everything in them, are deleted
    /// unchecked, except the evidence roots the configuration declares: one of those holding
    /// anything is refused unless <paramref name="deleteEvidence"/> is set, because a worktree's
    /// measurements are ignored precisely because they are not source, and losing them is silent.
    /// <paramref name="discardUncommitted"/> waives the uncommitted changes alone, which are then
    /// deleted with the worktree and counted in the outcome; every other check still runs. Once
    /// every check has passed, a worktree something holds part of is refused too, on Windows,
    /// where its removal would stop part way on it.
    /// <paramref name="force"/> skips every check, and that look, and overrides a lock.
    /// The mutation workers kept in the worktree go first, and those kept in each host's copy of it: one a sweep still
    /// running holds keeps the worktree, nothing of it removed, unless <paramref name="force"/> deletes it with that
    /// worker in it, as far as either can be removed.
    /// </summary>
    /// <remarks>
    /// <paramref name="name"/> is an address: a plain worktree's name, or <c>orchestrator/agent</c>. A plain address with
    /// no .git of its own that holds worktrees below it - the directory named for an orchestrator, holding its agents' -
    /// is refused even with <paramref name="force"/>, since deleting it would delete each of them; one whose worktrees
    /// git cannot list is not deleted either. A worktree's own submodules and nested repositories never count.
    /// An agent's repository of its own is weighed the same way but for its commits - those made in it that no ref of the
    /// main checkout's reaches - and has no lock or record of git's; once its checks pass, git's status and its commits are
    /// looked at once more, a change or a commit come since refusing it with nothing removed, and it is deleted as a
    /// directory, its own git directory last. The records of the commit a worktree stood on (<see cref="WorktreeKeepalive"/>)
    /// are forgotten once its directory is gone.
    /// <para>
    /// A checked removal of one of git's linked worktrees is reversible until its files are gone: once every check has
    /// passed, git's status and the lock are looked at once more, then everything in it but its .git file is deleted
    /// (<see cref="IFileSystem.EmptyDirectory"/>, which reaches a path past 260 characters), and only then is git asked to
    /// remove it, with its record the one thing left for git to clear. Stopped part way, it is still a worktree git knows,
    /// its .git file and git's record of it there.
    /// </para>
    /// <para>
    /// The worktree of an orchestrator's live agent - one whose record names it at this address and is neither closed nor
    /// deleted - is refused unless <paramref name="force"/> is set, naming delete-agent, which deletes it with the agent:
    /// deleted here, the agent would be left naming a worktree that is gone. delete-agent itself asks with
    /// <paramref name="asItsAgent"/>.
    /// </para>
    /// </remarks>
    /// <param name="startDirectory">A directory in the repository.</param>
    /// <param name="name">The worktree's address.</param>
    /// <param name="force">Whether every check is skipped.</param>
    /// <param name="deleteEvidence">Whether the evidence roots it holds are deleted with it.</param>
    /// <param name="discardUncommitted">Whether its uncommitted changes are deleted with it.</param>
    /// <param name="asItsAgent">Whether delete-agent asks, deleting the worktree as part of deleting its agent.</param>
    /// <param name="cancellationToken">Stops the deletion before its point of no return.</param>
    /// <exception cref="HarnessException">
    /// As for <see cref="CreateAsync"/>, or the path resolved outside the worktrees directory.
    /// </exception>
    Task<WorktreeOutcome> DeleteAsync(
        string startDirectory,
        string name,
        bool force,
        bool deleteEvidence,
        bool discardUncommitted = false,
        bool asItsAgent = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a copy of <paramref name="commit"/> at <paramref name="path"/> to work on beside the main checkout at
    /// <paramref name="mainCheckoutRoot"/>, one no fold of the main tree moves: a detached worktree of git's, outside the
    /// worktrees root, nobody's to keep. A copy an earlier attempt left at the same path - a stop, a pause or a crash
    /// before it was taken away - is taken away first, as <see cref="RemoveCopyAsync"/> takes one, so making it again
    /// never meets what is there. What was made is looked for, never taken on git's word.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, whose worktree the copy is.</param>
    /// <param name="path">Where the copy goes.</param>
    /// <param name="commit">The commit it is a copy of.</param>
    /// <param name="cancellationToken">Stops the making before git is asked to make it; a copy half made is taken back by the next attempt's making.</param>
    Task<WorktreeOutcome> CreateCopyAsync(string mainCheckoutRoot, string path, string commit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes away the copy at <paramref name="path"/> a <see cref="CreateCopyAsync"/> made, whatever it holds: its
    /// directory and git's record of it, forced, the record cleared where git cannot remove it, and looked for after -
    /// never interrupted, for a copy taken away part way is one the next making meets. What could not be taken away is
    /// said, with how to finish.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, whose worktree the copy is.</param>
    /// <param name="path">The copy's directory.</param>
    Task<WorktreeOutcome> RemoveCopyAsync(string mainCheckoutRoot, string path);

    /// <summary>
    /// Takes away, as <see cref="RemoveCopyAsync"/> takes one, every copy git records strictly inside
    /// <paramref name="directory"/>: what a run that was cut off before it took its copies away left, so deleting the run
    /// leaves no record of git's naming a directory that is gone. Never interrupted. Each copy that could not be taken
    /// away is said, one line each; none where every one was, or there was none.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, whose worktrees the copies are.</param>
    /// <param name="directory">The directory the copies were made in.</param>
    Task<IReadOnlyList<string>> RemoveCopiesUnderAsync(string mainCheckoutRoot, string directory);

    /// <summary>Lists existing worktrees with the commit each was made from.</summary>
    /// <exception cref="HarnessException">The repository is not initialised.</exception>
    Task<IReadOnlyList<WorktreeListing>> ListAsync(
        string startDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What removing the copies kept in the worktree at <paramref name="path"/> - the mutation workers its sweeps made,
    /// and the copies of it its legs work in - would leave, each line in its family's own words and with what it is to
    /// the removal - a copy a run or a sweep holds, what the removal cannot take as it stands, or what is only said -
    /// asked only to measure: nothing is removed, written or reclaimed. What its deletion asks before it removes anything,
    /// asked by a dry run of the deletion.
    /// </summary>
    /// <param name="name">The worktree's address, for the command that removes what was left.</param>
    /// <param name="layout">The repository, whose run lock says whether a run holds a leg copy.</param>
    /// <param name="path">The worktree's directory.</param>
    /// <param name="cancellationToken">Stops the asking between copies.</param>
    Task<IReadOnlyList<KeptCopyLine>> CopiesHeldAsync(string name, HarnessLayout layout, string path, CancellationToken cancellationToken = default);
}

/// <summary>One existing worktree.</summary>
/// <param name="Name">
/// The worktree's address: its directory's name under the worktrees root, or, for an orchestrator's agent,
/// <c>orchestrator/agent</c>.
/// </param>
/// <param name="BaseCommit">
/// The commit it was made from, or <see langword="null"/> when none was recorded — a worktree made
/// before the record existed, or one whose record could not be written. Reported so the tree a
/// worktree began from can be reproduced from git rather than from the moment it happened to be
/// made: the worktree's own
/// HEAD moves with every commit in it and stops answering that question after the first one.
/// </param>
public sealed record WorktreeListing(string Name, string? BaseCommit)
{
    /// <summary>What <see cref="Kind"/> holds for one of git's linked worktrees, which git records in the main checkout.</summary>
    public const string WorktreeKind = "worktree";

    /// <summary>What <see cref="Kind"/> holds for an agent's repository, a repository of its own that git records nowhere.</summary>
    public const string RepositoryKind = "repository";

    /// <summary>Where it is, links resolved, as git records it and as a sync records the tree a host's copy is of.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>What kind of tree it is: <see cref="WorktreeKind"/> or <see cref="RepositoryKind"/>.</summary>
    public string Kind { get; init; } = WorktreeKind;

    /// <summary>The line <c>list-worktree</c> prints for this worktree.</summary>
    public override string ToString()
        => BaseCommit is null ? Name : $"{Name}  base {ReportText.Commit(BaseCommit)}";
}

/// <summary>
/// What a worktree command did. The name is returned as its own value rather than
/// packed into a message, so a caller never has to parse it back out.
/// </summary>
/// <param name="Outcome">Exit code, and the message to report.</param>
/// <param name="Name">The worktree acted on. Empty when the command failed.</param>
/// <param name="Path">Its full path. Empty when the command failed.</param>
public sealed record WorktreeOutcome(CommandOutcome Outcome, string Name, string Path)
{
    /// <summary>Whether the command succeeded.</summary>
    public bool Succeeded => Outcome.Succeeded;

    /// <summary>
    /// The commit a worktree just created was made from, as read from it - recorded under <c>refs/harness/worktree-base/</c>
    /// or not: <see langword="null"/> when it could not be read, and for anything but a creation.
    /// </summary>
    public string? BaseCommit { get; init; }

    /// <summary>A failure carrying no worktree.</summary>
    public static WorktreeOutcome Failed(CommandOutcome outcome)
        => new(outcome, string.Empty, string.Empty);
}

/// <inheritdoc cref="IWorktreeService"/>
public sealed class WorktreeService(
    IHarnessContextLoader contextLoader,
    IGitClient gitClient,
    IFileSystem fileSystem,
    IPathBudget pathBudget,
    IHostPlatform platform,
    IHarnessOutput output,
    IHostCopyRemover hostCopies,
    LocalSyncTransport local,
    RunLock runLock,
    IProcessContext processContext) : IWorktreeService
{
    /// <summary>The command a deletion reports under, and the one spelling of its name.</summary>
    public const string DeleteCommand = "delete-worktree";

    /// <summary>The command a creation reports under, and the one spelling of its name.</summary>
    public const string CreateCommand = "create-worktree";

    /// <summary>The command a listing reports under, and the one spelling of its name.</summary>
    public const string ListCommand = "list-worktree";

    /// <summary>
    /// What a worktree's .git file, and an agent's repository's own git directory, are called: what its removal leaves for
    /// last, so one stopped part way is still a worktree, or a repository, until its files are gone.
    /// </summary>
    private const string GitDirectoryName = ".git";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IPathBudget _pathBudget = pathBudget;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;
    private readonly IHostCopyRemover _hostCopies = hostCopies;

    /// <summary>
    /// This machine's own transport, which removes the copies kept in a worktree here: its mutation workers, and the
    /// copies of it its legs work in.
    /// </summary>
    private readonly LocalSyncTransport _local = local;

    /// <summary>
    /// The run lock of the repositories this machine holds, which says whether a run still holds a leg's copy kept in a
    /// worktree being deleted.
    /// </summary>
    private readonly RunLock _runLock = runLock;

    /// <summary>The process's current directory, read per call: what "this command's own directory" means here.</summary>
    private readonly IProcessContext _processContext = processContext;

    /// <summary>
    /// The clock a deletion past its point of no return is timed on as it goes on to its end, told every
    /// <see cref="Waits.SaidEvery"/>; tests give one of their own.
    /// </summary>
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    public async Task<WorktreeOutcome> CreateAsync(
        string startDirectory,
        string? name,
        bool useRandomName,
        CancellationToken cancellationToken = default)
    {
        // Argument shape is checked before the repository is inspected. The reverse
        // order makes these checks unreachable in an uninitialised repository, reporting
        // a usage mistake as a missing prerequisite. Only the length waits for the
        // configuration, because only the length is configured.
        if (useRandomName && !string.IsNullOrWhiteSpace(name))
        {
            return Usage("Give a name or --random, not both.");
        }

        if (!useRandomName)
        {
            // An agent's worktree is made with its records by create-agent, never on its own: one made here would be an
            // agent no orchestrator knows.
            if (WorktreeAddress.TryParse(name, out var address, out _) && address!.IsNested)
            {
                return Usage($"'{name}' names an orchestrator's agent, whose worktree create-agent makes with its records; create-worktree makes a plain one.");
            }

            if (!WorktreeName.ValidateFormat(name).TryGetName(out _, out var formatError))
            {
                return Usage(formatError);
            }
        }

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;
        var settings = context.Config.Worktrees;

        string worktreeName;

        if (useRandomName)
        {
            var length = settings.MaxNameLength is { } most ? Math.Min(WorktreeName.RandomLength, most) : WorktreeName.RandomLength;
            string? generated;

            try
            {
                generated = WorktreeName.Generate(length, TakenNames(layout, settings.Root));
            }
            catch (HarnessException ex)
            {
                return WorktreeOutcome.Failed(CommandOutcome.Failed(ex.ExitCode, ex.Message));
            }

            if (generated is null)
            {
                // The caller's arguments were fine; no name of that length is left, which is a refusal rather than a
                // usage error.
                return WorktreeOutcome.Failed(CommandOutcome.Refused(
                    $"Every name of {length} characters is taken by a worktree or an orchestrator, so no random name is left; "
                    + "give a name, or raise worktrees.maxNameLength."));
            }

            worktreeName = generated;
        }
        else
        {
            if (!WorktreeName.Validate(name, settings.MaxNameLength).TryGetName(out var accepted, out var lengthError))
            {
                return Usage(lengthError);
            }

            worktreeName = accepted;
        }

        // Plain worktrees and orchestrators share the names under the root: an orchestrator's agents' worktrees are made
        // in the directory named for it there. Any directory of that name among the orchestrators' takes it, record or
        // not: one a deletion stopped part way through is still an orchestrator's.
        if (IsOrchestrators(layout, worktreeName))
        {
            return WorktreeOutcome.Failed(CommandOutcome.Refused(
                $"'{worktreeName}' is an orchestrator's name, and its agents' worktrees are made under "
                + $"'{ReportText.Printable(WorktreeAddress.Plain(worktreeName).PathUnder(layout.WorktreesDirectoryUnder(settings.Root)))}'; choose another name."));
        }

        return await CreateAtCoreAsync(context, WorktreeAddress.Plain(worktreeName), cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorktreeOutcome> CreateAtAsync(
        string startDirectory,
        WorktreeAddress address,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);

        foreach (var segment in address.Segments)
        {
            if (!WorktreeName.Validate(segment, context.Config.Worktrees.MaxNameLength).TryGetName(out _, out var error))
            {
                return Usage(error);
            }
        }

        return await CreateAtCoreAsync(context, address, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the worktree at <paramref name="address"/>, whose names were checked already.</summary>
    private async Task<WorktreeOutcome> CreateAtCoreAsync(
        HarnessContext context,
        WorktreeAddress address,
        CancellationToken cancellationToken)
    {
        var layout = context.Layout;
        var settings = context.Config.Worktrees;
        var worktreesDirectory = layout.WorktreesDirectoryUnder(settings.Root);
        var worktreeName = address.Name;
        var path = address.PathUnder(worktreesDirectory);

        if (_fileSystem.DirectoryExists(path))
        {
            return WorktreeOutcome.Failed(
                CommandOutcome.Refused($"A worktree named '{worktreeName}' already exists."));
        }

        // An agent's worktree sits in the directory named for its orchestrator, which is no worktree of its own: one
        // made inside a worktree would be taken for part of it, by git there and by every tool that asks git.
        var parent = Path.GetDirectoryName(path)!;

        if (address.IsNested
            && WorktreeInspector.HoldsOwnGit(_fileSystem, parent))
        {
            return WorktreeOutcome.Failed(CommandOutcome.Refused(
                $"'{ReportText.Printable(parent)}' is a worktree, and an orchestrator's agents' worktrees cannot be made inside one; delete it, or name the orchestrator otherwise."));
        }

        // What the worktree holds below it - the deeper of the files the repository tracks at the main checkout's HEAD,
        // where every worktree is made, and the build a leg of this machine makes in it - reckoned by the one owner a
        // run's start check asks too, so a run that passed its start never fails here on a worktree it needs. Where no
        // limit holds a path, nothing is listed for one.
        int files;

        try
        {
            files = _pathBudget.Bounds(settings.PathLimit)
                && await _gitClient.ResolveCommitAsync(layout.MainCheckoutRoot, "HEAD", cancellationToken).ConfigureAwait(false) is { } head
                ? await WorktreeRoom.ForFilesAsync(_gitClient, layout.MainCheckoutRoot, head, cancellationToken).ConfigureAwait(false)
                : 0;
        }
        catch (HarnessException ex)
        {
            return WorktreeOutcome.Failed(CommandOutcome.Failed(ex.ExitCode, ex.Message));
        }

        var budget = _pathBudget.Check(
            path,
            Math.Max(files, WorktreeRoom.ForBuilds(context.Config, _platform.PlatformKey)),
            settings.PathBudgetMargin,
            settings.PathLimit);

        if (!budget.IsWithinBudget)
        {
            return WorktreeOutcome.Failed(CommandOutcome.Refused(budget.Describe(path)));
        }

        _fileSystem.CreateDirectory(parent);

        string? baseCommit;

        if (address.IsNested)
        {
            // An orchestrator's agent's worktree is a repository of its own (AgentRepositories), never one of git's linked
            // worktrees, which share the main checkout's refs and objects with whatever runs in them. Its making keeps the
            // commit it is made at alive first, and takes back whatever it made where it fails.
            var made = await new AgentRepositories(_gitClient, _fileSystem, _platform)
                .MakeAsync(layout.MainCheckoutRoot, address, path, cancellationToken)
                .ConfigureAwait(false);

            if (made.Failure is { } failure)
            {
                return WorktreeOutcome.Failed(failure);
            }

            baseCommit = made.BaseCommit;
        }
        else
        {
            // Worktrees are always created from the main checkout, so running this from
            // inside a worktree adds a sibling rather than nesting one.
            string[] arguments = settings.Detach
                ? ["worktree", "add", "--detach", path]
                : ["worktree", "add", path];

            var result = await _gitClient
                .RunAsync(layout.MainCheckoutRoot, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                return WorktreeOutcome.Failed(CommandOutcome.Failed(
                    HarnessExit.CommandFailed,
                    $"git refused to create the worktree: {result.FailureMessage}"));
            }

            // git's exit code is not taken as proof the tree exists. A hook, a filter
            // driver or a scanner can remove it between git's success and this line, and
            // reporting a path that is not there sends the user somewhere unrelated.
            if (!_fileSystem.DirectoryExists(path))
            {
                return WorktreeOutcome.Failed(CommandOutcome.Failed(
                    HarnessExit.CommandFailed,
                    $"git reported success but '{path}' does not exist."));
            }

            baseCommit = await RecordBaseCommitAsync(layout, worktreeName, path, cancellationToken)
                .ConfigureAwait(false);
        }

        var details = baseCommit is null ? new List<string> { path } : [path, $"base {ReportText.Commit(baseCommit)}"];

        return new WorktreeOutcome(
            CommandOutcome.Ok($"created worktree '{worktreeName}'", details),
            worktreeName,
            path)
        {
            BaseCommit = baseCommit,
        };
    }

    /// <summary>
    /// Records the commit a worktree was made from (<see cref="WorktreeKeepalive"/>), and returns it.
    /// </summary>
    /// <remarks>
    /// A worktree's HEAD moves as work is committed in it, so after the first commit nothing says
    /// what tree it started from any more, and it can only be reproduced from the moment it
    /// happened to be made. For a linked worktree a failure to record is reported and not fatal: the
    /// worktree exists, its own HEAD keeps its commit alive, and refusing here would leave one behind
    /// that the caller was not told about.
    /// </remarks>
    private async Task<string?> RecordBaseCommitAsync(
        HarnessLayout layout,
        string worktreeName,
        string path,
        CancellationToken cancellationToken)
    {
        var head = await _gitClient
            .RunAsync(path, ["rev-parse", "HEAD"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!head.Succeeded)
        {
            _output.Detail(CreateCommand, $"could not read the new worktree's commit: {head.FailureMessage}");
            return null;
        }

        var commit = head.StandardOutput.Trim();

        if (commit.Length == 0)
        {
            return null;
        }

        // The commit is what the worktree was made from whether or not the record took: said, and still returned.
        if (await WorktreeKeepalive.WriteBaseAsync(_gitClient, layout.MainCheckoutRoot, worktreeName, commit, cancellationToken).ConfigureAwait(false) is { } failure)
        {
            _output.Detail(CreateCommand, $"could not record the base commit: {failure}");
        }

        return commit;
    }

    /// <summary>Removes the records of the commit a worktree that no longer exists stood on (<see cref="WorktreeKeepalive.ForgetAsync"/>).</summary>
    private async Task ForgetBaseCommitAsync(
        HarnessLayout layout,
        string worktreeName,
        CancellationToken cancellationToken)
    {
        foreach (var (reference, why) in await WorktreeKeepalive.ForgetAsync(_gitClient, layout.MainCheckoutRoot, worktreeName, cancellationToken).ConfigureAwait(false))
        {
            // Said rather than swallowed. The deletion itself succeeded, so this is not a failure of
            // the command; what is left is a stale record that will answer for the next worktree of
            // this name, and nobody would otherwise know to remove it.
            _output.Warn(
                DeleteCommand,
                $"'{worktreeName}' was deleted, and the commit it stood on is still recorded at "
                + $"'{reference}': {why.TrimEnd('.')}. Remove it with "
                + $"'git update-ref -d {reference}', or the next worktree of this name "
                + "inherits it.");
        }
    }

    public async Task<WorktreeOutcome> DeleteAsync(
        string startDirectory,
        string name,
        bool force,
        bool deleteEvidence,
        bool discardUncommitted = false,
        bool asItsAgent = false,
        CancellationToken cancellationToken = default)
    {
        // Only the shape is checked, not the length: a worktree created under a longer
        // limit must stay deletable after the limit is lowered.
        if (!WorktreeAddress.TryParse(name, out var address, out var error))
        {
            return Usage(error);
        }

        var worktreeName = address!.Name;
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;
        var settings = context.Config.Worktrees;
        var worktreesDirectory = layout.WorktreesDirectoryUnder(settings.Root);
        var path = address.PathUnder(worktreesDirectory);

        // Guards the one recursive delete this command performs, and comes before anything is
        // touched, so this refusal can never follow a deletion.
        if (!PathContainment.IsStrictlyInside(worktreesDirectory, path, _platform.PathComparison))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"Refusing to delete '{path}': it is not inside '{worktreesDirectory}'.");
        }

        // A live agent's worktree is deleted with the agent, by delete-agent, which keeps its evidence, its transcripts and
        // its records: deleted here, the agent would be left naming a worktree that is gone. Asked before anything else, so
        // nothing of it is ever looked through or removed for want of that one command.
        if (address.IsNested && !force && !asItsAgent && RefuseALiveAgentsWorktree(layout, address, path) is { } agents)
        {
            return agents;
        }

        var inspector = new WorktreeInspector(_gitClient, _fileSystem, _platform, _output);

        // A directory holding worktrees below it - an orchestrator's, holding its agents' - is never deleted as one,
        // --force or not: deleting it would delete each of them, with everything they hold and git's records left
        // naming directories that are gone. Only a plain address with no .git of its own can be one - an agent's
        // worktree holds none, and a worktree's submodules and nested repositories, a husk's included, are its own
        // contents, which the checks below weigh: below it count the worktrees git records, and, in the directory named
        // for an orchestrator, every one holding a .git of its own. Where that cannot be told, nothing is deleted.
        if (!address.IsNested && _fileSystem.DirectoryExists(path) && !WorktreeInspector.HoldsOwnGit(_fileSystem, path))
        {
            WorktreesBelow below;

            try
            {
                below = await inspector.FindWorktreesBelowAsync(layout.MainCheckoutRoot, worktreesDirectory, path, IsOrchestrators(layout, worktreeName), cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
            {
                return Failed($"{ex.Message} Nothing was deleted: whether '{ReportText.Printable(path)}' holds worktrees below it could not be told.");
            }

            if (below.Worktrees.Count > 0)
            {
                return Refused(
                    $"'{ReportText.Printable(path)}' is not deleted, --force or not: it holds worktrees below it - {Names.All(below.Worktrees)} - and deleting it would "
                    + $"delete each of them. Delete each on its own, with '{ToolPackage.Command} {DeleteCommand} <address>' or, for an orchestrator's agent, delete-agent."
                    + (below.Moved.Count > 0 ? $" And {below.SaidMoved(layout.MainCheckoutRoot)}." : string.Empty)
                    + (below.Unrecorded.Count > 0 ? $" And {WorktreeInspector.SaidUnrecorded(below.Unrecorded)}." : string.Empty));
            }

            if (below.Moved.Count > 0)
            {
                return Refused(
                    $"'{ReportText.Printable(path)}' is not deleted, --force or not: {below.SaidMoved(layout.MainCheckoutRoot)}. Deleting the directory would delete each with it."
                    + (below.Unrecorded.Count > 0 ? $" And {WorktreeInspector.SaidUnrecorded(below.Unrecorded)}." : string.Empty));
            }

            if (below.Unrecorded.Count > 0)
            {
                return Refused(
                    $"'{ReportText.Printable(path)}' is not deleted, --force or not: {WorktreeInspector.SaidUnrecorded(below.Unrecorded)}, "
                    + "and deleting the directory would delete each with it.");
            }
        }

        if (!_fileSystem.DirectoryExists(path))
        {
            var cleared = await ClearRecordAsync(layout, worktreeName, path, force, inspector, cancellationToken)
                .ConfigureAwait(false);

            if (cleared is { Succeeded: false })
            {
                return cleared;
            }

            // Nothing of the worktree is left for its keepalive to keep - an agent's repository deleted by hand leaves no
            // record of git's at all - so the commit it stood on is forgotten either way.
            await ForgetBaseCommitAsync(layout, worktreeName, CancellationToken.None).ConfigureAwait(false);

            // Nothing here, and nothing in git's record any more: deleted already, cleared just now, or never made. Copies
            // of it on hosts that could not be asked to remove them when it was deleted can still be there, and dealing
            // with them is what deleting it again is for. Its mutation workers went with it: they were kept in it.
            return await AndItsHostCopiesAsync(context, address, path, treeConfig: null, cleared, cancellationToken).ConfigureAwait(false);
        }

        // Before git is asked anything, because it is about files git was never told about. Asked
        // afterwards, a git that cannot answer reports "unchecked" and this refusal — the one with
        // the --delete-evidence remedy attached — is never the one the reader sees.
        if (!force && !deleteEvidence && DescribeEvidence(path, settings.EvidenceRoots) is { } holdsEvidence)
        {
            return Refused(
                $"Worktree '{worktreeName}' was not deleted, because {holdsEvidence}. "
                + "Copy what you need out first, or pass --delete-evidence to delete it with the worktree "
                + "(--force does too, and skips every other check as well).");
        }

        WorktreeIdentity identity;

        try
        {
            identity = await inspector.IdentifyAsync(layout.MainCheckoutRoot, address, path, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed && force)
        {
            // Forced, the deletion does not wait on git's answer; only finding git's record does.
            identity = new WorktreeIdentity(WorktreeMembership.NotAWorktree, null);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return Unchecked(worktreeName, ex.Message);
        }

        // The copies kept in it are asked about before anything of it is looked through for what another program holds:
        // a mutation worker a sweep still running holds, or a leg copy a run does, is said as that, with what holds it,
        // never as files held. Asked about before any goes, so a deletion refused for one has removed nothing, a copy no
        // run or sweep holds included.
        if (!force && Kept(await RemoveCopiesAsync(worktreeName, layout, path, measureOnly: true, cancellationToken).ConfigureAwait(false), "Nothing was removed") is { } kept)
        {
            return kept;
        }

        IReadOnlyList<WorktreeChange> discarded = [];

        // Whether git counts the worktree as holding submodules, whose files a checkout of its index alone never puts back.
        var holdsSubmodules = false;

        if (!force)
        {
            switch (identity.Membership)
            {
                case WorktreeMembership.NotAWorktree:
                    return await RefuseNotAWorktreeAsync(layout, worktreeName, path, inspector, cancellationToken).ConfigureAwait(false);

                case WorktreeMembership.OfAnotherRepository:
                    return Refused(
                        $"'{worktreeName}' is not a worktree of this repository: '{ReportText.Printable(path)}' belongs to another repository, whose history would be deleted with it. "
                        + "Move it elsewhere, or pass --force to delete it anyway.");
            }

            WorktreeFindings findings;

            try
            {
                findings = await inspector
                    .FindAsync(layout.MainCheckoutRoot, path, identity, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
            {
                return Unchecked(worktreeName, ex.Message);
            }

            holdsSubmodules = findings.HoldsSubmodules;

            // Waived, the uncommitted changes are left out of what is weighed, and nothing else is:
            // every other finding still refuses, and a refusal never names what was to be discarded.
            if (discardUncommitted)
            {
                discarded = findings.Changes;
                findings = findings with { Changes = [] };
            }

            if (findings.StopsDeletion)
            {
                return Refused(Describe(worktreeName, path, layout.MainCheckoutRoot, findings));
            }
        }

        // Read while the worktree is still there: its branch may declare a host the configuration
        // this command runs in does not, and its copy there is reached through it.
        var treeConfig = await OwnConfigurationAsync(layout, path, cancellationToken).ConfigureAwait(false);

        // On Windows, the removal stops at the first entry another program holds, with some of the worktree's files
        // already gone. So each entry is opened first as the removal will open it, and a worktree anything holds is
        // refused whole, with nothing removed. Forced, nothing is asked: the removal goes as far as it can, and says
        // what it left.
        if (!force)
        {
            IReadOnlyList<HeldEntry> held;

            try
            {
                held = _fileSystem.FindHeld(path, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unchecked(worktreeName, $"'{ReportText.Printable(path)}' could not be looked through for what another program holds in it: {ex.Message.TrimEnd('.')}.");
            }

            if (held.Count > 0)
            {
                return Refused(DescribeHeld(worktreeName, held));
            }
        }

        // The copies kept in it go first, each as a copy is removed, by its marker and its claim: one still in use - a
        // sweep still running holds a worker, a run a leg copy - says the tree is, and keeps it; forced, the worktree goes
        // with what of it can be removed, that copy's files among them, and what is left is said.
        var workers = await RemoveCopiesAsync(worktreeName, layout, path, measureOnly: false, cancellationToken).ConfigureAwait(false);

        // The last moment an interruption can stop this cleanly: the worktree itself is whole. One that came as its
        // copies went, or just after, is said with those that went - a copy gone is gone whoever is told, and nothing
        // else would ever say so - where one that found none to say is the command's own. From here the deletion goes
        // on after an interruption, which is reported at once; it can still be left partly done, and --force then
        // finishes it.
        if (workers.Interrupted || cancellationToken.IsCancellationRequested)
        {
            return workers.Lines.Count == 0
                ? throw new OperationCanceledException(cancellationToken)
                : WorktreeOutcome.Failed(CommandOutcome.Failed(
                    HarnessExit.Cancelled,
                    $"Deleting worktree '{worktreeName}' was interrupted as the copies kept in it were being removed. Nothing else of the "
                    + $"worktree was removed: run '{ToolPackage.Command} {DeleteCommand} {worktreeName}' again to delete it.",
                    workers.Lines));
        }

        // A sweep or a run that took one between the asking and the removal keeps the worktree as well, and so does one
        // that could not be removed: the others are gone by then, which what is said of it says.
        if (!force && Kept(workers, "Nothing of the worktree was removed") is { } late)
        {
            return late;
        }

        // What keeps the worktree, of what became of the copies kept in it - one a sweep or a run holds, one that could
        // not be removed or told, or none that could be looked for - or null. Each is said as what it is, in the words a
        // host's copy kept says it in (KeptCopyLine.Keeping): only one a run still holds ends by waiting, and that one
        // alone says so to a caller that can wait (OutcomeCause.LockHeld).
        WorktreeOutcome? Kept(WorkersGone gone, string nothing)
            => gone.Kept is { } kept
                ? WorktreeOutcome.Failed(CommandOutcome.Failed(
                    gone.ExitCode!.Value,
                    $"Worktree '{worktreeName}' was not deleted: {KeptCopyLine.Keeping(kept, gone.KeptMember)}. "
                        + (kept.Verdict == LegVerdict.RefusedLocked ? "Wait for it to end" : "Put that right and try again")
                        + $", or pass --force to delete the worktree with that copy in it. {nothing}.",
                    gone.Lines) with
                {
                    Cause = kept.Verdict == LegVerdict.RefusedLocked ? OutcomeCause.LockHeld : OutcomeCause.None,
                })
                : gone.Failure is { } failure
                    ? WorktreeOutcome.Failed(CommandOutcome.Failed(
                        gone.ExitCode!.Value,
                        $"The copies kept in worktree '{worktreeName}' could not be removed: {failure.TrimEnd('.')}. Nothing of the worktree "
                        + "was removed: put that right and try again, or pass --force to delete the worktree with them in it."))
                    : null;

        WorktreeOutcome outcome;

        // Past its point of no return: an interruption from here on is said, and the deletion goes on to its end, told
        // while it lasts, since a deletion stopped part way is a worktree nobody can check again. Only a second
        // interruption, the hard stop, ends it - the process with it.
        using (WarnIfInterrupted(worktreeName, cancellationToken))
        {
            var removal = new TreeRemoval(layout, worktreeName, path, identity, discarded, holdsSubmodules, inspector);

            outcome = await FinishedAsync(
                    worktreeName,
                    () => identity.Membership == WorktreeMembership.AgentRepository
                        ? RemoveRepositoryAsync(removal, checksRan: !force)
                        : force
                        ? RemoveForcedAsync(layout.MainCheckoutRoot, worktreeName, path, identity.AdministrativeDirectory, inspector, $"run '{ToolPackage.Command} {DeleteCommand} {worktreeName} --force'")
                        : RemoveCheckedAsync(removal))
                .ConfigureAwait(false);
        }

        // What became of its workers is said whichever way the worktree's own removal ended: they went before it, and
        // an outcome naming none of them would leave nobody knowing those copies are gone. One left in it went with it
        // where the worktree is gone, and is said so; where it is not, it is still there.
        WorktreeOutcome WithWorkers(WorktreeOutcome said)
        {
            IReadOnlyList<string> lines = said.Succeeded ? workers.WentWithIt : workers.Lines;

            return lines.Count == 0 ? said : said with { Outcome = said.Outcome with { Details = [.. said.Outcome.Details ?? [], .. lines] } };
        }

        if (!outcome.Succeeded)
        {
            return WithWorkers(outcome);
        }

        if (discarded.Count > 0)
        {
            outcome = outcome with
            {
                Outcome = outcome.Outcome with
                {
                    Details = [.. outcome.Outcome.Details ?? [], $"discarded {ReportText.Counted(discarded.Count, "uncommitted change")}: {Names.All([.. discarded.Select(change => change.Shown)])}"],
                },
            };
        }

        // A worker left in it went with it - by --force, which chose to, or as a directory nothing says the harness made,
        // which goes as everything else in the worktree does: said, and never what fails the deletion asked for.
        outcome = WithWorkers(outcome);

        // Only once the worktree is really gone. Forgetting it earlier would lose the record of a
        // worktree a failed removal left in place. Not cancelled: the removal is past its point of
        // no return, and a record left behind would answer for a later worktree of the same name.
        await ForgetBaseCommitAsync(layout, worktreeName, CancellationToken.None).ConfigureAwait(false);
        return await AndItsHostCopiesAsync(context, address, path, treeConfig, outcome, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What removing the copies kept in a worktree did, as its deletion says it.</summary>
    /// <param name="Told">
    /// What was removed, and each copy left, with why, each line with what it is to the removal: said where the worktree
    /// is still there.
    /// </param>
    /// <param name="WentWithIt">
    /// The same, said once the worktree is gone: each copy left in it - held, not removed, nobody's the harness made,
    /// or not looked for at all - went with it.
    /// </param>
    /// <param name="Removal">What became of them, or <see langword="null"/> where they could not be looked for.</param>
    /// <param name="Failure">Why they could not be looked for at all, or <see langword="null"/>.</param>
    private sealed record WorkersGone(IReadOnlyList<KeptCopyLine> Told, IReadOnlyList<string> WentWithIt, WorkersRemoval? Removal, string? Failure)
    {
        /// <summary>The lines of <see cref="Told"/>, as the deletion says them.</summary>
        public IReadOnlyList<string> Lines => [.. Told.Select(told => told.Line)];

        /// <summary>The copy that keeps the worktree, or <see langword="null"/> where none does (<see cref="WorkersRemoval.Kept"/>).</summary>
        public WorkerLeft? Kept => Removal?.Kept;

        /// <summary>What a line calls one copy of the family of the copy that keeps the worktree, in the singular: <c>mutation worker</c>, <c>leg copy</c>.</summary>
        public string KeptMember { get; init; } = MutationWorkers.Member;

        /// <summary>Whether the removal was stopped before every copy had been dealt with.</summary>
        public bool Interrupted => Removal is { Interrupted: true };

        /// <summary>
        /// The exit code a copy not yet removed leaves a deletion with - its removal's verdict's: refused where a sweep or
        /// a run holds one, which ends by waiting, failed where one could not be removed - and failed where they could not
        /// be looked for; or <see langword="null"/> where none is left that asking again would remove.
        /// </summary>
        public int? ExitCode
            => Failure is not null ? HarnessExit.CommandFailed
                : Removal is { Kept: not null } ? Verdicts.ExitCodeFor(Removal.Verdict)
                : null;
    }

    /// <summary>
    /// What removing the copies kept in the worktree at <paramref name="path"/> - the mutation workers its sweeps made,
    /// and the copies of it its legs work in - would leave, each line in its family's own words and with what it is to
    /// the removal - a copy a run or a sweep holds, what the removal cannot take as it stands, or what is only said -
    /// asked only to measure: nothing is removed, written or reclaimed. What its deletion asks before it removes anything,
    /// asked by a dry run of the deletion.
    /// </summary>
    /// <param name="name">The worktree's address, for the command that removes what was left.</param>
    /// <param name="layout">The repository, whose run lock says whether a run holds a leg copy.</param>
    /// <param name="path">The worktree's directory.</param>
    /// <param name="cancellationToken">Stops the asking between copies.</param>
    public async Task<IReadOnlyList<KeptCopyLine>> CopiesHeldAsync(string name, HarnessLayout layout, string path, CancellationToken cancellationToken = default)
        => (await RemoveCopiesAsync(name, layout, path, measureOnly: true, cancellationToken).ConfigureAwait(false)).Told;

    /// <summary>
    /// Removes the copies kept in the worktree at <paramref name="path"/> - the mutation workers its sweeps made, and the
    /// copies of it its legs work in, each of whichever variant - and says what it did: what was removed, and each left,
    /// with why. Asked only to measure, it removes none, and says each that would be left.
    /// </summary>
    /// <param name="name">The worktree's address, for the command that removes what was left.</param>
    /// <param name="layout">The repository, whose run lock says whether a run holds a leg copy.</param>
    /// <param name="path">The worktree's directory.</param>
    /// <param name="measureOnly">Whether to say what would be left, and remove nothing.</param>
    /// <param name="cancellationToken">Stops the removal between copies.</param>
    private async Task<WorkersGone> RemoveCopiesAsync(string name, HarnessLayout layout, string path, bool measureOnly, CancellationToken cancellationToken)
    {
        // The workers first: a sweep's are asked about as they always were, through the transport - which on a host
        // serving a removal is that host's own. The leg copies never leave this machine, so they are asked of the local
        // slot directly, by the run lock that holds them - a sweep in one holding it whole - and each goes whole, with
        // whatever it keeps: so the workers asked about here are the worktree's own, and a leg copy that cannot be looked
        // for is said as the leg copies.
        var workers = await RemoveWorkersAsync(name, path, measureOnly, cancellationToken).ConfigureAwait(false);

        if (workers.Failure is not null)
        {
            return workers;
        }

        WorkersRemoval copies;

        try
        {
            copies = await _local.RemoveInSlotAsync(path, HarnessLayout.LegsDirectoryName, LegCopies.Claims(_runLock, layout, DeleteCommand), measureOnly, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            return workers with
            {
                Told = [.. workers.Told, KeptCopyLine.Stopping($"the leg copies kept in it could not be removed: {ex.Message.TrimEnd('.')}")],
                WentWithIt = [.. workers.WentWithIt, $"the leg copies kept in it could not be looked for, and went with it: {ex.Message.TrimEnd('.')}"],
                Failure = ex.Message,
            };
        }

        // Asked only to measure, nothing went: an interruption then has nothing to say first, and is the command's own.
        if (measureOnly && copies.Interrupted)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var again = $"'{ToolPackage.Command} {DeleteCommand} {name}'";
        IReadOnlyList<KeptCopyLine> went = !measureOnly && copies.Removed.Count > 0
            ? [KeptCopyLine.Said($"removed {ReportText.Counted(copies.Removed.Count, LegCopies.Member)} kept in it, {DiskSpace.Size(copies.Bytes)}: "
                + string.Join(", ", copies.Removed.Select(copy => $"'{copy.Path}'")))]
            : [];

        // A leg copy a run holds is held by the lock, which is asked for its holder as its removal asks it.
        var left = copies.Left.Select(copy => KeptCopyLine.Left(copy, LegCopies.Member, again, LegCopies.Whole(copy.Path, DeleteCommand))).ToList();
        var said = new WorkersGone(
            [.. went, .. left.Select(copy => copy.Told)],
            [.. went.Select(line => line.Line), .. left.Select(copy => copy.WentWithIt)],
            copies,
            Failure: null);

        return workers with
        {
            Told = [.. workers.Told, .. said.Told],
            WentWithIt = [.. workers.WentWithIt, .. said.WentWithIt],

            // Whichever copy keeps the worktree keeps it: one a run holds ends by waiting, as one a sweep holds does.
            Removal = workers.Kept is not null ? workers.Removal
                : copies.Kept is not null ? copies
                : Merge(workers.Removal, copies),
            KeptMember = workers.Kept is not null ? workers.KeptMember
                : copies.Kept is not null ? LegCopies.Member
                : workers.KeptMember,
        };

        // Neither keeps the worktree: the two removals' verdicts apart, their counts and what went merge, so what the
        // deletion says covers both families.
        static WorkersRemoval? Merge(WorkersRemoval? workers, WorkersRemoval copies) => workers is null ? copies : workers.And(copies);
    }

    /// <summary>
    /// Removes the mutation workers kept in the worktree at <paramref name="path"/> - each a copy of it a sweep made, of
    /// whichever variant - and says what it did: what was removed, and each left, with why. Asked only to measure, it
    /// removes none, and says each that would be left.
    /// </summary>
    /// <param name="name">The worktree's address, for the command that removes what was left.</param>
    /// <param name="path">The worktree's directory.</param>
    /// <param name="measureOnly">Whether to say what would be left, and remove nothing.</param>
    /// <param name="cancellationToken">Stops the removal between workers.</param>
    private async Task<WorkersGone> RemoveWorkersAsync(string name, string path, bool measureOnly, CancellationToken cancellationToken)
    {
        var again = $"'{ToolPackage.Command} {DeleteCommand} {name}'";
        WorkersRemoval removal;

        try
        {
            removal = await _local.RemoveWorkersInTreeAsync(path, measureOnly, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            return new WorkersGone(
                [KeptCopyLine.Stopping($"the mutation workers kept in it could not be removed: {ex.Message.TrimEnd('.')}; {again} removes them once that is put right")],
                [$"the mutation workers kept in it could not be looked for, and went with it: {ex.Message.TrimEnd('.')}"],
                Removal: null,
                ex.Message);
        }

        // Asked only to measure, nothing went: an interruption then has nothing to say first, and is the command's own.
        if (measureOnly && removal.Interrupted)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        IReadOnlyList<KeptCopyLine> went = !measureOnly && removal.Removed.Count > 0
            ? [KeptCopyLine.Said($"removed {ReportText.Counted(removal.Removed.Count, "mutation worker")} kept in it, {DiskSpace.Size(removal.Bytes)}: "
                + string.Join(", ", removal.Removed.Select(worker => $"'{worker.Path}'")))]
            : [];

        // A worker a sweep holds is held by that sweep's claim, which names its holder on the line.
        var left = removal.Left.Select(worker => KeptCopyLine.Left(worker, MutationWorkers.Member, again)).ToList();

        return new WorkersGone(
            [.. went, .. left.Select(worker => worker.Told)],
            [.. went.Select(line => line.Line), .. left.Select(worker => worker.WentWithIt)],
            removal,
            Failure: null);
    }

    public async Task<IReadOnlyList<WorktreeListing>> ListAsync(
        string startDirectory,
        CancellationToken cancellationToken = default)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var worktreesDirectory = context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);

        if (!_fileSystem.DirectoryExists(worktreesDirectory))
        {
            return [];
        }

        // Only what git records as a worktree is one. Every directory under the root was counted, so
        // a data directory a script keeps there read as a sixth worktree beside git's five, and
        // a listing that disagrees with git's own record is one nobody can act on. Read from git's
        // record rather than guessed from a directory's contents, and matched by resolved path, the
        // way git lists them: if the record cannot be read, the command fails rather than listing
        // directories nobody checked.
        var registered = (await _gitClient.ListWorktreesAsync(context.Layout.MainCheckoutRoot, cancellationToken).ConfigureAwait(false))
            .Where(worktree => !worktree.IsMain)
            .ToList();

        var names = new List<(string Name, string Path, string Kind)>();
        var inspector = new WorktreeInspector(_gitClient, _fileSystem, _platform, _output);
        var repositories = new AgentRepositories(_gitClient, _fileSystem, _platform);

        foreach (var directory in Directories(worktreesDirectory, "the worktrees root"))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

            if (string.IsNullOrEmpty(name) || await TakeListedAsync(directory, name, orchestrator: null).ConfigureAwait(false))
            {
                continue;
            }

            // A directory that is no worktree may be an orchestrator's, holding its agents' worktrees, each listed by
            // both names; one holding none is said to be no worktree, as any other directory there is. One that cannot
            // be looked in is said too, and listed as nothing: what it holds is never guessed at.
            var agents = 0;
            IReadOnlyList<string> children;

            try
            {
                children = Directories(directory, $"'{name}' under the worktrees root");
            }
            catch (HarnessException ex)
            {
                _output.Warn(ListCommand, $"{ex.Message.TrimEnd('.')}, so no worktree below it is listed.");
                continue;
            }

            foreach (var child in children)
            {
                var agent = Path.GetFileName(Path.TrimEndingDirectorySeparator(child));

                if (!string.IsNullOrEmpty(agent) && await TakeListedAsync(child, agent, orchestrator: name).ConfigureAwait(false))
                {
                    agents++;
                }
            }

            if (agents == 0)
            {
                _output.Detail(
                    ListCommand,
                    $"'{name}' is under the worktrees root and is not a worktree git records, so it is not listed");
            }
        }

        // Whether the directory is a worktree git records, or an agent's repository, listed under its address; one holding
        // a .git entry that is neither is said, and taken as dealt with, since it is nobody's orchestrator.
        async Task<bool> TakeListedAsync(string directory, string name, string? orchestrator)
        {
            var address = orchestrator is null ? WorktreeAddress.Plain(name) : WorktreeAddress.Nested(orchestrator, name);
            var resolved = _fileSystem.ResolveLinks(directory);

            if (inspector.RecordAt(registered, resolved) is not null)
            {
                names.Add((address.Name, resolved, WorktreeListing.WorktreeKind));
                return true;
            }

            if (await repositories.IsAtAsync(context.Layout.MainCheckoutRoot, address, directory, cancellationToken).ConfigureAwait(false))
            {
                names.Add((address.Name, resolved, WorktreeListing.RepositoryKind));
                return true;
            }

            if (await inspector.MovedFromAsync(context.Layout.MainCheckoutRoot, directory, cancellationToken).ConfigureAwait(false) is { } recorded)
            {
                // Said without being asked, and never as a directory to delete: a worktree whose checkout was moved by hand
                // is still one, its work all in it, and git's record of it names where it was.
                _output.Warn(
                    ListCommand,
                    $"'{address.Name}' is not listed: {WorktreeInspector.SaidMoved(context.Layout.MainCheckoutRoot, directory, recorded)}.");
                return true;
            }

            if (WorktreeInspector.HoldsOwnGit(_fileSystem, directory))
            {
                // Said without being asked. A directory holding a .git entry that git no longer
                // records is what a removal leaves when git's record went and a file in use did not,
                // and its name stays taken: dropped from the listing silently, it looks free.
                _output.Warn(
                    ListCommand,
                    $"'{address.Name}' holds a .git entry, and git records no worktree there: {WorktreeInspector.UnrecordedWhy}. "
                    + "It is not listed, and no worktree can be made at its path until it is gone; look inside for "
                    + $"anything wanted, then delete '{directory}'.");
                return true;
            }

            return false;
        }

        var listings = new List<WorktreeListing>();

        foreach (var (name, path, kind) in names.OrderBy(worktree => worktree.Name, StringComparer.Ordinal))
        {
            listings.Add(new WorktreeListing(
                name,
                await WorktreeKeepalive.ReadBaseAsync(_gitClient, context.Layout.MainCheckoutRoot, name, cancellationToken).ConfigureAwait(false))
            {
                Path = path,
                Kind = kind,
            });
        }

        return listings;
    }

    /// <summary>The directories directly in <paramref name="directory"/>; one that cannot be looked in raises, named as <paramref name="what"/>.</summary>
    /// <exception cref="HarnessException">It could not be looked in (<see cref="HarnessExit.CommandFailed"/>).</exception>
    private IReadOnlyList<string> Directories(string directory, string what)
    {
        try
        {
            return [.. _fileSystem.EnumerateDirectories(directory)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"{what}, '{ReportText.Printable(directory)}', could not be looked in: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>Whether <paramref name="name"/> is an orchestrator's: a directory of that name is kept among the orchestrators'.</summary>
    private bool IsOrchestrators(HarnessLayout layout, string name)
        => _fileSystem.DirectoryExists(Orchestration.OrchestratorLayout.Of(layout, name).Directory);

    /// <summary>
    /// The refusal of an address that is a live agent's worktree, or that may be; <see langword="null"/> where it is no
    /// live agent's. Live is as the agent's own record says it, read by the one reader of orchestrators' records
    /// (<see cref="Orchestration.OrchestrationStore"/>): an agent of the orchestrator the address names, of the name it
    /// names, neither closed nor deleted (<see cref="Orchestration.AgentStates.Live"/>), working in a worktree of its own
    /// whose path is this one - under the worktrees root its record names, whatever the configuration says now. A
    /// closed agent's worktree is not refused, since its removal is what delete-agent's own remedies name; nor is a
    /// directory at the address of an agent made under another root. A record that cannot be read refuses as well: whether
    /// the worktree is a live agent's cannot be told, and "cannot tell" never reads as "it is not".
    /// </summary>
    /// <param name="layout">The repository.</param>
    /// <param name="address">The address, an agent's: <c>orchestrator/agent</c>.</param>
    /// <param name="path">The worktree's directory at that address.</param>
    private WorktreeOutcome? RefuseALiveAgentsWorktree(HarnessLayout layout, WorktreeAddress address, string path)
    {
        var (orchestrator, agent) = (address.Segments[0], address.Segments[1]);
        Orchestration.AgentRecord? record;

        try
        {
            record = new Orchestration.OrchestrationStore(_fileSystem, _output).ReadAgent(Orchestration.OrchestratorLayout.Of(layout, orchestrator), agent);
        }
        catch (HarnessException ex)
        {
            return Refused(
                $"Whether worktree '{address.Name}' is the worktree of a live agent of orchestrator '{orchestrator}' cannot be told: {ex.Message.TrimEnd('.')}. "
                + $"Nothing was deleted: a live agent's worktree is deleted with the agent, by {Orchestration.OrchestrationReports.DeleteAgentLine(orchestrator, agent, string.Empty)}; "
                + "put its record right, or pass --force to delete the worktree anyway.");
        }

        return record is { State: Orchestration.AgentStates.Live, Reads: false } && record.WorktreePath(layout) is { } at && PathsEqual(at, path)
            ? Refused(
                $"Worktree '{address.Name}' is the worktree of agent '{agent}' of orchestrator '{orchestrator}', which is live, and is deleted with the agent: "
                + $"{Orchestration.OrchestrationReports.DeleteAgentLine(orchestrator, agent, string.Empty)} says what deleting it keeps and removes, and with --apply "
                + "deletes it, its evidence and transcripts kept. Nothing was deleted; --force deletes the worktree anyway, leaving the agent's record naming a "
                + "worktree that is gone.")
            : null;
    }

    /// <summary>
    /// The refusal for a directory under the root that is not a worktree git can find, which names the way back only
    /// where there is one: <c>git worktree repair</c> rebuilds a lost .git file or a moved worktree from git's record,
    /// and has nothing to rebuild from once the record is gone too - which is what a removal that stopped part way
    /// leaves, git having deleted both first.
    /// </summary>
    private async Task<WorktreeOutcome> RefuseNotAWorktreeAsync(
        HarnessLayout layout,
        string name,
        string path,
        WorktreeInspector inspector,
        CancellationToken cancellationToken)
    {
        GitWorktree? record;

        try
        {
            record = await inspector.FindRecordAsync(layout.MainCheckoutRoot, path, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return Unchecked(name, ex.Message);
        }

        if (record is not null)
        {
            return Refused(
                $"'{ReportText.Printable(path)}' is not a worktree git can find, so what it holds cannot be checked, though git still records a worktree there. "
                + $"Run 'git -C {ReportText.Printable(layout.MainCheckoutRoot)} worktree repair', which rebuilds its .git file from that record, as after it was moved or its .git file was lost; "
                + "or pass --force to delete it anyway.");
        }

        string? moved;

        try
        {
            moved = await inspector.MovedFromAsync(layout.MainCheckoutRoot, path, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return Unchecked(name, ex.Message);
        }

        if (moved is not null)
        {
            return Refused(
                $"{WorktreeInspector.SaidMoved(layout.MainCheckoutRoot, path, moved)}, after which its work is checked as any worktree's is; "
                + "or pass --force to delete it anyway.");
        }

        var what = WorktreeInspector.HoldsOwnGit(_fileSystem, path)
            ? "its .git names no worktree git records"
            : "it holds no .git of its own, and git records no worktree there";

        return Refused(
            $"'{ReportText.Printable(path)}' is not a worktree, so what it holds cannot be checked: {what}. That is what a removal that stopped part way leaves, "
            + "and what a directory made there by hand looks like, and git worktree repair has no record to rebuild it from. "
            + $"Look at what it holds, and once nothing in it is needed, run '{ToolPackage.Command} {DeleteCommand} {name} --force' to delete it.");
    }

    private static WorktreeOutcome Usage(string message)
        => WorktreeOutcome.Failed(CommandOutcome.Usage(message));

    private static WorktreeOutcome Refused(string message)
        => WorktreeOutcome.Failed(CommandOutcome.Refused(message));

    private static WorktreeOutcome Failed(string message)
        => WorktreeOutcome.Failed(CommandOutcome.Failed(HarnessExit.CommandFailed, message));

    private static WorktreeOutcome Removed(string name, string path)
        => new(CommandOutcome.Ok($"removed worktree '{name}'", [path]), name, path);

    /// <summary>
    /// The failure for a worktree that could not be checked, without --force. Nothing was deleted,
    /// and --force is not offered as the way forward, because it would destroy exactly what could
    /// not be seen.
    /// </summary>
    private static WorktreeOutcome Unchecked(string name, string reason)
        => Failed(
            $"{reason} Nothing was deleted: what worktree '{name}' holds could not be checked. "
            + "Fix the cause and try again; --force would delete it unchecked.");

    /// <summary>
    /// Once past the point of no return, says the moment an interruption arrives that the deletion goes on to its end,
    /// and what a second interruption, which ends it at once, would leave to finish.
    /// </summary>
    private CancellationTokenRegistration WarnIfInterrupted(string name, CancellationToken cancellationToken)
        => cancellationToken.Register(() => _output.Warn(
            DeleteCommand,
            $"Deleting worktree '{name}' is past its point of no return and goes on to its end; a second interruption ends it "
            + $"at once and may leave it half done - if it does, run '{ToolPackage.Command} {DeleteCommand} {name} --force' to finish it."));

    /// <summary>
    /// The deletion of worktree <paramref name="name"/> past its point of no return, run to its end whatever interrupts the
    /// command - the work is given no token that an interruption cancels - and told every <see cref="Waits.SaidEvery"/>
    /// while it lasts, so a removal of a large tree is seen going on rather than hung.
    /// </summary>
    /// <param name="name">The worktree's address.</param>
    /// <param name="removal">The removal.</param>
    private async Task<WorktreeOutcome> FinishedAsync(string name, Func<Task<WorktreeOutcome>> removal)
    {
        var ended = await Waits.ForAsync(
                _ => removal(),
                new WaitWatch($"deleting worktree '{name}' to end", told => _output.Info(DeleteCommand, told.Line()), Bound: null, Clock),
                CancellationToken.None)
            .ConfigureAwait(false);

        // Neither the user's bound nor a token can end it, so it ends with the removal's own answer.
        return ended.Value!;
    }

    /// <summary>
    /// What is said, after what a removal that failed or was stopped left, of the directory junctions it had removed from
    /// the worktree first: each went as a link, and neither a checkout nor anything else of git's puts one back. Nothing
    /// where none was removed.
    /// </summary>
    /// <param name="unlinked">The junctions removed, in the order they were.</param>
    private static string Unlinked(IReadOnlyList<string> unlinked)
        => unlinked.Count == 0
            ? string.Empty
            : $" Its {ReportText.Counted(unlinked.Count, "directory junction")} had been removed first, as links: {Names.All(unlinked)}.";

    /// <summary>What a removal works from once its checks have passed, or once --force skipped them.</summary>
    /// <param name="Layout">The repository.</param>
    /// <param name="Name">The worktree's address.</param>
    /// <param name="Path">Its directory.</param>
    /// <param name="Identity">What git made of it: one of its linked worktrees, with the git directory that holds its record, or an agent's repository.</param>
    /// <param name="Discarded">The uncommitted changes being discarded, which the second look lets through by their paths.</param>
    /// <param name="HoldsSubmodules">Whether git counts it as holding submodules, whose files a checkout of its index never puts back.</param>
    /// <param name="Inspector">Asks git.</param>
    private sealed record TreeRemoval(
        HarnessLayout Layout,
        string Name,
        string Path,
        WorktreeIdentity Identity,
        IReadOnlyList<WorktreeChange> Discarded,
        bool HoldsSubmodules,
        WorktreeInspector Inspector);

    /// <summary>
    /// The look a checked removal takes just before any file of the worktree goes (<see cref="WorktreeInspector.LookAgainAsync"/>):
    /// what came since the checks that would be lost - a change, beyond those being discarded, told by its path as git's
    /// status gives it, never by how a line names it; a lock, for one of git's linked worktrees; a commit made in it, for an
    /// agent's repository of its own - refuses the removal with nothing of the worktree removed, as does a git that cannot
    /// answer. Null where nothing came since.
    /// </summary>
    /// <param name="removal">The worktree, and what its checks let through.</param>
    private async Task<WorktreeOutcome?> RefusedSinceTheChecksAsync(TreeRemoval removal)
    {
        WorktreeFindings late;

        try
        {
            late = await removal.Inspector.LookAgainAsync(removal.Path, removal.Identity, CancellationToken.None).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return Failed(
                $"{ex.Message} Nothing of worktree '{removal.Name}' was removed: whether anything changed in it since it was checked could not be told. "
                + "Fix the cause and try again; --force would delete it unchecked.");
        }

        // What was being discarded was weighed and counted already: only what came since stops it.
        late = late with
        {
            Changes = [.. late.Changes.Where(change => !removal.Discarded.Any(discarded => string.Equals(discarded.Path, change.Path, StringComparison.Ordinal)))],
        };

        return late.StopsDeletion
            ? Refused($"{Describe(removal.Name, removal.Path, removal.Layout.MainCheckoutRoot, late)} It changed since it was checked, and nothing of it was removed.")
            : null;
    }

    /// <summary>
    /// Removes an agent's repository: its directory and everything in it, its own git directory and the objects only it
    /// holds among them, which no record of git's in the main checkout names. Where its checks ran, git is asked once more
    /// what changed in it and what was committed in it since (<see cref="RefusedSinceTheChecksAsync"/>) - a file an agent
    /// still running wrote meanwhile refuses it, nothing removed - as a linked worktree's removal asks. git has no removal
    /// of its own for one, so the directory is deleted - everything in it but its own git directory first, then the rest,
    /// so one stopped part way is still a repository, every commit made in it still there; its read-only object files with
    /// it, a link or junction in it as the link it is, never what it leads to, however far past 260 characters a path in it
    /// runs - and then looked for. No interruption stops it part way: it is past its point of no return.
    /// </summary>
    /// <param name="removal">The repository, and what its checks let through.</param>
    /// <param name="checksRan">Whether every check passed before it began, which makes finishing it with --force safe.</param>
    private async Task<WorktreeOutcome> RemoveRepositoryAsync(TreeRemoval removal, bool checksRan)
    {
        var (name, path) = (removal.Name, removal.Path);

        if (checksRan && await RefusedSinceTheChecksAsync(removal).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        var finish = $"{(checksRan ? "Every check passed before removal began, so run" : "Run")} '{ToolPackage.Command} {DeleteCommand} {name} --force' to finish deleting it";
        var files = true;

        try
        {
            _fileSystem.EmptyDirectory(path, [GitDirectoryName]);
            files = false;
            _fileSystem.DeleteDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(
                $"Worktree '{name}', an agent's repository, was not deleted whole: {ex.Message.TrimEnd('.')}. "
                + (files
                    ? "Some of its files may already be gone, and its own git directory, every commit made in it with it, is still there. "
                    : "Its files are gone, and part of its own git directory may be. ")
                + $"Close whatever holds its files open, then {char.ToLowerInvariant(finish[0])}{finish[1..]}.");
        }

        return _fileSystem.DirectoryExists(path)
            ? Failed($"Worktree '{name}', an agent's repository, was deleted, but '{ReportText.Printable(path)}' still exists. {finish}.")
            : Removed(name, path);
    }

    /// <summary>
    /// Removes a worktree whose checks passed, every step reversible until its files are gone. git's own removal deletes
    /// the worktree's .git file and git's record of it as soon as anything, and then stops at the first file it cannot
    /// delete - one another program holds, or, on Windows, one past 260 characters - leaving a directory no check can run
    /// on again. So the removal is the harness's, in this order: the look git's removal took before it deleted anything,
    /// taken again here (<see cref="RefusedSinceTheChecksAsync"/>) - a file changed or added since the checks, beyond the
    /// changes being discarded, or a lock - refuses with nothing of the worktree removed; each directory junction goes as
    /// the link it is; then everything in it but its .git file, through the file system's own deletion, which reaches any
    /// path, however long; and only then is git asked to remove the worktree, with its record the one thing left for it to
    /// clear, forced once, since the files it would weigh are gone - a lock still stops it. Failing anywhere before git
    /// clears the record, it is still a worktree git knows, its .git file and record there, and what is said of it says
    /// so, the junctions that went before naming among it. No interruption stops it part way: it is past its point of no
    /// return.
    /// </summary>
    /// <param name="removal">The worktree, one of git's linked worktrees, and what its checks let through.</param>
    private async Task<WorktreeOutcome> RemoveCheckedAsync(TreeRemoval removal)
    {
        var (layout, name, path) = (removal.Layout, removal.Name, removal.Path);
        var administrativeDirectory = removal.Identity.AdministrativeDirectory!;

        // Nothing puts a junction back, so each failure after they went names them.
        IReadOnlyList<string> unlinked = [];

        return await RemoveAsync().ConfigureAwait(false);

        async Task<WorktreeOutcome> RemoveAsync()
        {
            if (await RefusedSinceTheChecksAsync(removal).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            // Each junction goes first, as the link it is, never what it leads to: one that cannot be removed, or a volume
            // mounted in the worktree, stops the deletion here, before any file of it has gone. Not interrupted part way:
            // the links go together.
            try
            {
                unlinked = _fileSystem.RemoveJunctions(path);
            }
            catch (JunctionRemovalException ex)
            {
                var before = ex.Removed.Count == 0
                    ? "Nothing of it was removed"
                    : $"Only the {ReportText.Counted(ex.Removed.Count, "junction")} before it were removed, as links, and what each led to is untouched: {Names.All(ex.Removed)}";

                return Failed(
                    $"Worktree '{name}' was not deleted: its directory junction {ex.Message.TrimEnd('.')}. {before}; git was not run. "
                    + $"Deal with that junction, then run '{ToolPackage.Command} {DeleteCommand} {name}' again.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(
                    $"Worktree '{name}' was not deleted: '{ReportText.Printable(path)}' could not be looked through for its directory junctions, "
                    + $"which go first, as links: {ex.Message.TrimEnd('.')}. Nothing of it was removed; git was not run.");
            }

            try
            {
                _fileSystem.EmptyDirectory(path, [GitDirectoryName], CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(
                    $"Worktree '{name}' was not deleted whole: {ex.Message.TrimEnd('.')}; git was not run. "
                    + DescribeWhatRemains(name, path, administrativeDirectory, checksRan: true, filesBegun: true, removal.HoldsSubmodules)
                    + Unlinked(unlinked));
            }

            // Its files are gone; git is left its record to clear, and the .git file naming it, which goes with the directory.
            var gitRemoval = await _gitClient
                .RunAsync(layout.MainCheckoutRoot, ["worktree", "remove", "--force", path], cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            if (!gitRemoval.Succeeded)
            {
                return Failed(
                    $"git could not remove worktree '{name}': {gitRemoval.FailureMessage.TrimEnd('.')}. "
                    + DescribeWhatRemains(name, path, administrativeDirectory, checksRan: true, filesBegun: true, removal.HoldsSubmodules)
                    + Unlinked(unlinked));
            }

            if (_fileSystem.DirectoryExists(path))
            {
                return Failed(
                    $"git reported worktree '{name}' removed, but '{path}' still exists. "
                    + DescribeWhatRemains(name, path, administrativeDirectory, checksRan: true, filesBegun: true, removal.HoldsSubmodules)
                    + Unlinked(unlinked));
            }

            var verified = Verified(name, path, administrativeDirectory, gitFailure: null);

            return unlinked.Count == 0 || !verified.Succeeded
                ? verified
                : verified with
                {
                    Outcome = verified.Outcome with
                    {
                        Details = [.. verified.Outcome.Details ?? [], $"removed {ReportText.Counted(unlinked.Count, "directory junction")} first, as links, never what they lead to: {Names.All(unlinked)}"],
                    },
                };
        }
    }

    /// <summary>
    /// What a removal that failed, or was stopped, left behind, and how to finish - as it is found, never as it was meant
    /// to go. Where the harness began deleting the worktree's files itself (<paramref name="filesBegun"/>), its .git file
    /// and git's record of it were left for last, and while both are there it is still a worktree: one whose files are
    /// gone, or may be, is said with the checkout that puts back what its index holds - and, where it holds submodules,
    /// whose directories and .git files a checkout never makes again, the submodule update that does - beside the --force
    /// that finishes it. Where git deleted it (a forced removal), git deletes the .git file and the record even when it
    /// fails on a file under it, and can delete files before failing at all, so a removal once started is never reported
    /// as having deleted nothing. Finishing with --force is only called safe where the checks ran, since a forced removal
    /// never made any.
    /// </summary>
    /// <param name="name">The worktree's address.</param>
    /// <param name="path">Its directory.</param>
    /// <param name="administrativeDirectory">Its record's directory, where git named one.</param>
    /// <param name="checksRan">Whether every check passed before removal began.</param>
    /// <param name="filesBegun">Whether the harness had begun deleting its files, before git was asked to clear its record.</param>
    /// <param name="holdsSubmodules">Whether git counts it as holding submodules.</param>
    private string DescribeWhatRemains(string name, string path, string? administrativeDirectory, bool checksRan, bool filesBegun, bool holdsSubmodules = false)
    {
        var finish = $"'{ToolPackage.Command} {DeleteCommand} {name} --force'";
        var safe = checksRan ? "Every check passed before removal began, so run" : "Run";
        var directory = _fileSystem.DirectoryExists(path);
        var gitFile = directory && WorktreeInspector.HoldsOwnGit(_fileSystem, path);
        var record = administrativeDirectory is not null && _fileSystem.DirectoryExists(administrativeDirectory);
        var gone = new List<string>();

        if (!directory)
        {
            gone.Add("its directory");
        }
        else
        {
            if (filesBegun && HoldsNothingBut(path, GitDirectoryName))
            {
                gone.Add("its files");
            }

            if (!gitFile)
            {
                gone.Add("its .git file");
            }
        }

        if (administrativeDirectory is not null && !record)
        {
            gone.Add("git's record of it");
        }

        // The .git file and the record both there: still a worktree, whose files git can put back - those its index holds by a
        // checkout, and its submodules' by their update, since a checkout never makes a submodule's directory or .git again.
        var stillAWorktree = filesBegun && gitFile && record
            ? $", and its .git file and git's record of it are still there, so it is still a worktree: 'git -C {ReportText.Printable(path)} checkout -- .' "
                + "puts back the files its index holds"
                + (holdsSubmodules ? $", and 'git -C {ReportText.Printable(path)} submodule update --init --recursive' those of its submodules" : string.Empty)
            : string.Empty;

        if (gone.Count > 0)
        {
            var listed = gone.Count == 1 ? gone[0] : $"{string.Join(", ", gone[..^1])} and {gone[^1]}";

            return $"It stopped part way: {listed} {(gone is ["its files"] || gone.Count > 1 ? "are" : "is")} already gone{stillAWorktree}. {safe} {finish} to finish deleting it.";
        }

        if (stillAWorktree.Length > 0)
        {
            return $"Some of its files may already be gone{stillAWorktree}. Close whatever holds its files open; {char.ToLowerInvariant(safe[0])}{safe[1..]} {finish} "
                + "to finish deleting it.";
        }

        // Stopped before any file of it went, as the last look was asked: what the deletion removed is its copies alone.
        if (checksRan && !filesBegun && directory && gitFile && (administrativeDirectory is null || record))
        {
            return $"No file of it was removed: run '{ToolPackage.Command} {DeleteCommand} {name}' again to delete it.";
        }

        var remaining = administrativeDirectory is null
            ? "Its directory and its .git file are"
            : "Its directory, its .git file and git's record of it are";

        return $"{remaining} still there, but git may already have deleted some files, which would now stop a checked delete as changes. "
            + $"Close whatever holds its files open, look at what is left with 'git -C {ReportText.Printable(path)} status', then run {finish} to finish deleting it.";
    }

    /// <summary>
    /// Whether <paramref name="directory"/> holds no entry but the one named <paramref name="kept"/>; false where it holds
    /// another, or cannot be looked in - what cannot be seen is never said to be gone.
    /// </summary>
    private bool HoldsNothingBut(string directory, string kept)
    {
        try
        {
            return _fileSystem.EnumerateDirectories(directory).Concat(_fileSystem.EnumerateFiles(directory, recursive: false))
                .All(entry => string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(entry)), kept, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a worktree without checking it: its directory, and git's record of it, even when it
    /// is locked, its .git file is gone, or it belongs to another repository.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, whose worktree it is.</param>
    /// <param name="name">What the worktree is called where its removal is said.</param>
    /// <param name="path">Its directory.</param>
    /// <param name="administrativeDirectory">Its record's directory, where git named one; null where git's list is to be asked.</param>
    /// <param name="inspector">Asks git's list for its record.</param>
    /// <param name="finish">What finishes a removal that could not delete its directory, said after closing what holds it.</param>
    private async Task<WorktreeOutcome> RemoveForcedAsync(
        string mainCheckoutRoot,
        string name,
        string path,
        string? administrativeDirectory,
        WorktreeInspector inspector,
        string finish)
    {
        // Forced twice, git removes a locked worktree as well.
        var removal = await _gitClient
            .RunAsync(mainCheckoutRoot, ["worktree", "remove", "--force", "--force", path], cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);

        // git leaves the directory when it cannot treat it as a worktree at all, and can report
        // success while leaving part of it behind, so what remains is deleted here.
        if (_fileSystem.DirectoryExists(path))
        {
            try
            {
                _fileSystem.DeleteDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(
                    $"Could not finish deleting '{path}'; part of it may already be gone: {ex.Message.TrimEnd('.')}. "
                    + $"Close whatever is using it, then {finish}.");
            }
        }

        if (_fileSystem.DirectoryExists(path))
        {
            return Failed(removal.Succeeded
                ? $"'{path}' still exists after it was deleted."
                : $"'{path}' still exists after removal: {removal.FailureMessage}");
        }

        string? clearingFailure = null;

        if (!removal.Succeeded)
        {
            // With the directory gone, git clears the record it could not remove before, locked
            // or not. Its answer is not taken on trust: the record is looked for below.
            var clearing = await _gitClient
                .RunAsync(mainCheckoutRoot, ["worktree", "remove", "--force", "--force", path], cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            clearingFailure = clearing.Succeeded ? null : clearing.FailureMessage;
        }

        return administrativeDirectory is null
            ? await VerifiedByListAsync(mainCheckoutRoot, name, path, clearingFailure, inspector).ConfigureAwait(false)
            : Verified(name, path, administrativeDirectory, clearingFailure);
    }

    public async Task<WorktreeOutcome> CreateCopyAsync(string mainCheckoutRoot, string path, string commit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckoutRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var inspector = new WorktreeInspector(_gitClient, _fileSystem, _platform, _output);

        // A copy an earlier attempt left - its directory, or git's record of it with the directory gone - is taken away
        // first: git refuses to make a worktree where a directory is, whatever --force says, and where its record names a
        // directory that is gone.
        GitWorktree? recorded;

        try
        {
            recorded = await inspector.FindRecordAsync(mainCheckoutRoot, path, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return Failed($"The copy of commit {ReportText.Commit(commit)} at '{ReportText.Printable(path)}' was not made: whether git records one there already could not be told: {ex.Message.TrimEnd('.')}. Nothing was made.");
        }

        if (recorded is not null || _fileSystem.DirectoryExists(path))
        {
            var left = await RemoveCopyAsync(mainCheckoutRoot, path).ConfigureAwait(false);

            if (!left.Succeeded)
            {
                return Failed(
                    $"The copy of commit {ReportText.Commit(commit)} at '{ReportText.Printable(path)}' was not made: the copy an earlier attempt left there "
                    + $"could not be taken away: {left.Outcome.Message.TrimEnd('.')}. Nothing was made.");
            }
        }

        _fileSystem.CreateDirectory(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!);

        var made = await _gitClient
            .RunAsync(mainCheckoutRoot, ["worktree", "add", "--detach", path, commit], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!made.Succeeded)
        {
            return Failed($"git could not make the copy of commit {ReportText.Commit(commit)} at '{ReportText.Printable(path)}': {made.FailureMessage.TrimEnd('.')}.");
        }

        // git's exit code is not taken as proof the copy exists: a hook, a filter or a scanner can take it away between
        // git's success and here.
        return _fileSystem.DirectoryExists(path)
            ? new WorktreeOutcome(CommandOutcome.Ok($"made a copy of commit {ReportText.Commit(commit)}", [path]), name, path) { BaseCommit = commit }
            : Failed($"git reported the copy of commit {ReportText.Commit(commit)} made, but '{ReportText.Printable(path)}' does not exist.");
    }

    public Task<WorktreeOutcome> RemoveCopyAsync(string mainCheckoutRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckoutRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Never interrupted, as a worktree's forced removal is not: a copy taken away part way is one the next making meets.
        return RemoveForcedAsync(
            mainCheckoutRoot,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            path,
            administrativeDirectory: null,
            new WorktreeInspector(_gitClient, _fileSystem, _platform, _output),
            $"make the copy again, which takes it away first, or delete '{ReportText.Printable(path)}' and run 'git worktree prune'");
    }

    public async Task<IReadOnlyList<string>> RemoveCopiesUnderAsync(string mainCheckoutRoot, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckoutRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var inspector = new WorktreeInspector(_gitClient, _fileSystem, _platform, _output);
        IReadOnlyList<GitWorktree> listed;
        string resolved;

        try
        {
            resolved = inspector.ResolveLinks(directory);
            listed = await _gitClient.ListWorktreesAsync(mainCheckoutRoot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return [$"the copies git records inside '{ReportText.Printable(directory)}' could not be looked for, and any is left: {ex.Message.TrimEnd('.')}"];
        }

        var left = new List<string>();

        foreach (var copy in listed.Where(worktree => !worktree.IsMain && PathContainment.IsStrictlyInside(resolved, worktree.Path, _platform.PathComparison)))
        {
            var removed = await RemoveCopyAsync(mainCheckoutRoot, copy.Path).ConfigureAwait(false);

            if (!removed.Succeeded)
            {
                left.Add($"the copy '{ReportText.Printable(copy.Path)}' is left: {removed.Outcome.Message.TrimEnd('.')}");
            }
        }

        return left;
    }

    /// <summary>
    /// Clears git's record of a worktree whose directory is already gone, as an interrupted delete
    /// or a directory removed by hand leaves it. The record alone keeps the name from ever being
    /// created again, and its git directory can still hold the only copy of some work.
    /// </summary>
    /// <returns>What clearing it did, or <see langword="null"/> when git holds no record of it either.</returns>
    private async Task<WorktreeOutcome?> ClearRecordAsync(
        HarnessLayout layout,
        string name,
        string path,
        bool force,
        WorktreeInspector inspector,
        CancellationToken cancellationToken)
    {
        GitWorktree? record;
        WorktreeFindings? findings = null;

        try
        {
            // With no directory to ask in, git's list is the only place the record can be found.
            record = await inspector.FindRecordAsync(layout.MainCheckoutRoot, path, cancellationToken).ConfigureAwait(false);

            if (record is not null && !force)
            {
                findings = await inspector.FindForRecordAsync(layout.MainCheckoutRoot, record, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return force
                ? Failed($"{ex.Message} Nothing was deleted: git's record of worktree '{name}' could not be looked up.")
                : Unchecked(name, ex.Message);
        }

        if (record is null)
        {
            return null;
        }

        if (findings is { StopsDeletion: true })
        {
            return Refused(Describe(name, path, layout.MainCheckoutRoot, findings));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Past its point of no return as a deletion is: an interruption is said, and the clearing goes on to its end.
        using var interruption = WarnIfInterrupted(name, cancellationToken);

        return await FinishedAsync(name, async () =>
            {
                string[] arguments = force
                    ? ["worktree", "remove", "--force", "--force", path]
                    : ["worktree", "remove", path];

                var removal = await _gitClient
                    .RunAsync(layout.MainCheckoutRoot, arguments, cancellationToken: CancellationToken.None)
                    .ConfigureAwait(false);

                if (!removal.Succeeded)
                {
                    return Failed($"git could not clear its record of worktree '{name}', whose directory is gone: {removal.FailureMessage}");
                }

                return await VerifiedByListAsync(layout.MainCheckoutRoot, name, path, gitFailure: null, inspector).ConfigureAwait(false);
            })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asks each host holding a copy of the worktree at <paramref name="address"/> to remove it, as the last part of
    /// deleting it, and adds what they did to <paramref name="deleted"/>.
    /// </summary>
    /// <param name="context">The repository.</param>
    /// <param name="address">The worktree, whose copies are kept under its <see cref="WorktreeAddress.CopyName"/>.</param>
    /// <param name="path">The worktree's directory, whose copies these are.</param>
    /// <param name="treeConfig">The configuration it ran on, read before it was deleted, where it could be.</param>
    /// <param name="deleted">What deleting it here did, or <see langword="null"/> when it was gone already.</param>
    /// <param name="cancellationToken">Stops the asking; a copy not yet asked about stays recorded.</param>
    /// <remarks>
    /// The worktree stays deleted whatever the hosts answer. A copy not dealt with - its host cannot be asked, a run
    /// holds it, or removing it failed - stays recorded, and the command fails, naming it, so that whoever deleted
    /// the worktree learns there is still something of it on a host; deleting the worktree again asks once more.
    /// </remarks>
    private async Task<WorktreeOutcome> AndItsHostCopiesAsync(
        HarnessContext context,
        WorktreeAddress address,
        string path,
        HarnessConfig? treeConfig,
        WorktreeOutcome? deleted,
        CancellationToken cancellationToken)
    {
        var name = address.Name;
        var done = deleted is null ? $"Worktree '{name}' is gone already" : $"Worktree '{name}' was deleted";
        var again = $"'{ToolPackage.Command} {DeleteCommand} {name}'";
        IReadOnlyList<string> before = [.. deleted?.Outcome.Details ?? []];
        HostCopyRemoval removal;

        try
        {
            removal = await _hostCopies.RemoveAsync(context, address.CopyName, path, treeConfig, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            // With no record to read there is no telling whether a worktree of this name ever had a copy on a
            // host, so one that is gone already is not said to have been deleted: it may never have existed.
            return Deleted(CommandOutcome.Failed(
                ex.ExitCode,
                deleted is null
                    ? $"No worktree named '{name}' is here or in git's record, and whether a host holds a copy of one cannot be told: {ex.Message}"
                    : $"{done}, and none of its copies on hosts was removed: {ex.Message}",
                before));
        }

        if (removal.Lines.Count == 0 && !removal.Interrupted)
        {
            return deleted ?? Refused(removal.LeftFor is [var other, ..]
                ? $"No worktree named '{name}' is here or in git's record. The copies kept under that name on hosts are the "
                    + $"worktree's at '{other}', which still exists, so they were left for it."
                : $"No worktree named '{name}'.");
        }

        IReadOnlyList<string> details = [.. before, .. removal.Lines];

        return Deleted(removal switch
        {
            { Interrupted: true } => CommandOutcome.Failed(
                HarnessExit.Cancelled,
                $"{done}, and the interruption came before every host holding a copy of it had been asked to remove it: "
                + $"run {again} to ask the rest.",
                details),
            { Unfinished.Count: > 0 } => CommandOutcome.Failed(
                removal.ExitCode,
                $"{done}, and {removal.Unfinished.Count} of its copies on hosts are not yet dealt with: run {again} once "
                + "what keeps each is gone.",
                details),
            _ when deleted is null => CommandOutcome.Ok($"{done}; each copy of it left on a host is dealt with.", details),
            _ => deleted.Outcome with { Details = details },
        });

        WorktreeOutcome Deleted(CommandOutcome outcome) => new(outcome, name, deleted?.Path ?? string.Empty);
    }

    /// <summary>Confirms git's record is gone, by the administrative directory the record lives in.</summary>
    private WorktreeOutcome Verified(string name, string path, string administrativeDirectory, string? gitFailure)
        => _fileSystem.DirectoryExists(administrativeDirectory)
            ? StillRegistered(name, path, gitFailure)
            : Removed(name, path);

    /// <summary>
    /// Confirms git's record is gone by looking for it in git's list. Used only where git could not
    /// name the record's directory beforehand.
    /// </summary>
    private async Task<WorktreeOutcome> VerifiedByListAsync(
        string mainCheckoutRoot,
        string name,
        string path,
        string? gitFailure,
        WorktreeInspector inspector)
    {
        GitWorktree? remaining;

        try
        {
            remaining = await inspector.FindRecordAsync(mainCheckoutRoot, path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return Failed($"Worktree '{name}' is deleted, but whether git still has it registered could not be confirmed: {ex.Message}");
        }

        return remaining is not null
            ? StillRegistered(name, path, gitFailure)
            : Removed(name, path);
    }

    private static WorktreeOutcome StillRegistered(string name, string path, string? gitFailure)
        => Failed(gitFailure is null
            ? $"Worktree '{name}' is deleted, but git still has it registered, which keeps the name from being used again; "
                + $"run 'git worktree remove --force --force {ReportText.Printable(path)}' to clear it."
            : $"Worktree '{name}' is deleted, but git still has it registered, which keeps the name from being used again, "
                + $"and could not clear it: {gitFailure}");

    /// <summary>The one-line refusal naming everything that stopped the deletion, each with its remedy.</summary>
    private static string Describe(string name, string path, string mainCheckoutRoot, WorktreeFindings findings)
    {
        var reasons = new List<string>();

        // An agent's repository is deleted with its own store: a branch or a stash made in it goes with it, so what keeps its
        // work is the main tree, by a fold, or a fetch into the main checkout.
        if (findings.Changes.Count > 0)
        {
            reasons.Add(
                $"it has {ReportText.Counted(findings.Changes.Count, "uncommitted change")} that would be lost: {Names.All([.. findings.Changes.Select(change => change.Shown)])} "
                + (findings.OwnRepository
                    ? "(fold-agent brings them into the main tree, or pass --discard-uncommitted to delete them with the worktree)"
                    : "(commit them to a branch, run 'git stash -u', or pass --discard-uncommitted to delete them with the worktree)"));
        }

        if (findings.Commits > 0 && findings.Head is { } head)
        {
            var shortHead = ReportText.Commit(head);

            reasons.Add(findings.OwnRepository
                ? $"{ReportText.Counted(findings.Commits, "commit")} up to {shortHead} were made in its own repository, and no ref of the main checkout's reaches them "
                    + $"(run 'git -C {ReportText.Printable(mainCheckoutRoot)} fetch {ReportText.Printable(path)} HEAD' and branch FETCH_HEAD there, or push them)"
                : $"{ReportText.Counted(findings.Commits, "commit")} up to {shortHead} are on no branch, tag, remote-tracking ref, newest stash or other worktree's HEAD "
                    + $"(run 'git branch <name> {shortHead}', or push them)");
        }

        foreach (var submodule in findings.Submodules)
        {
            var held = (submodule.Commits > 0, submodule.HasStash) switch
            {
                (true, true) => $"{ReportText.Counted(submodule.Commits, "commit")} no remote-tracking ref or tag contains, and a stash",
                (true, false) => $"{ReportText.Counted(submodule.Commits, "commit")} no remote-tracking ref or tag contains",
                _ => "a stash",
            };

            reasons.Add(
                $"submodule '{ReportText.Printable(submodule.Path)}' holds {held}, in a repository deleted with the worktree "
                + "(push them, or keep them elsewhere)");
        }

        if (findings.LockReason is { } reason)
        {
            var because = reason.Length == 0 ? string.Empty : $": {ReportText.Printable(reason)}";
            reasons.Add($"it is locked{because} (run 'git worktree unlock {ReportText.Printable(path)}')");
        }

        if (findings.MovedFrom is { } recorded)
        {
            var repair = $"run 'git -C {ReportText.Printable(mainCheckoutRoot)} worktree repair {ReportText.Printable(path)}' to point the record here";

            reasons.Add(recorded.Length == 0
                ? $"git's record of it names no directory ({repair})"
                : $"git's record of it names '{ReportText.Printable(recorded)}', as after moving it by hand ({repair})");
        }

        return $"Worktree '{name}' was not deleted, because {string.Join("; ", reasons)}; fix that, or pass --force to delete it anyway.";
    }

    /// <summary>
    /// Why a worktree something holds part of was refused before anything of it was removed: what is held, why that
    /// would stop the removal part way, and what to do. Run from inside the worktree, this command holds it itself, which
    /// nothing another program closes lets go, so that is said, with running it from outside.
    /// </summary>
    private string DescribeHeld(string name, IReadOnlyList<HeldEntry> held)
    {
        var again = $"'{ToolPackage.Command} {DeleteCommand} {name}'";
        var own = held.Any(entry => PathsEqual(entry.Path, _processContext.CurrentDirectory));
        var remedy = !own ? $"Close what holds it, then run {again} again."
            : held.Count == 1 ? $"It is this command's own current directory: run {again} again from outside the worktree."
            : $"One is this command's own current directory: close what holds the rest, then run {again} again from outside the worktree.";

        return $"Worktree '{name}' was not deleted, and nothing of it was removed: something holds "
            + $"{Names.All([.. held.Select(entry => $"'{ReportText.Printable(entry.Path)}'")])} - a process whose current directory is in "
            + "the worktree, a program with a file of it open, or a program running from it. Windows would not let what "
            + "is held be deleted, so the removal would stop part way, with some of the worktree's files "
            + $"already gone. {remedy} "
            + $"Windows said: {string.Join(" ", held.Select(entry => entry.Reason).Distinct(StringComparer.Ordinal))}";
    }

    /// <summary>
    /// Describes the declared evidence roots that hold anything, or <see langword="null"/> when none
    /// does. Phrased to follow "because", like every other reason a deletion gives.
    /// </summary>
    /// <remarks>
    /// Found as <see cref="WorktreeEvidence"/> finds them, the one reading delete-agent keeps them by. A root that cannot
    /// be read counts as holding something: an unreadable directory is not an empty one, and every measurement here is
    /// biased toward keeping work rather than losing it. A root that is, or is reached through, a link holds nothing the
    /// removal would take, and is not counted.
    /// </remarks>
    private string? DescribeEvidence(string worktreePath, IReadOnlyList<string> evidenceRoots)
    {
        var found = WorktreeEvidence.Find(_fileSystem, _platform.PathComparison, worktreePath, evidenceRoots);

        // Files anywhere beneath a root, not only directly in it: a measurement written into a subdirectory is still the
        // only copy of it.
        List<string> occupied =
        [
            .. found.Files.Keys,
            .. found.Unreadable.Select(root => $"{root.Root} (which could not be read: {root.Why})"),
        ];

        if (occupied.Count == 0)
        {
            return null;
        }

        return occupied.Count == 1
            ? $"1 declared evidence directory holds measurements that only exist there: {Names.All(occupied)}"
            : $"{occupied.Count} declared evidence directories hold measurements that only exist there: {Names.All(occupied)}";
    }

    /// <summary>
    /// Every name a worktree or an orchestrator already uses: each directory under the worktrees root, and each kept among
    /// the orchestrators', compared as this machine's file system compares names - so a random name is drawn from outside
    /// them (<see cref="WorktreeName.Generate(int, IReadOnlySet{string})"/>), never tried against them a set number of times.
    /// </summary>
    /// <exception cref="HarnessException">Either directory could not be listed (<see cref="HarnessExit.CommandFailed"/>).</exception>
    private HashSet<string> TakenNames(HarnessLayout layout, string root)
    {
        var taken = new HashSet<string>(StringComparer.FromComparison(_platform.PathComparison));

        foreach (var directory in new[] { layout.WorktreesDirectoryUnder(root), layout.OrchestratorsDirectory })
        {
            try
            {
                if (!_fileSystem.DirectoryExists(directory))
                {
                    continue;
                }

                foreach (var entry in _fileSystem.EnumerateDirectories(directory))
                {
                    taken.Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(entry)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"'{ReportText.Printable(directory)}' could not be listed for the names already taken, so no random name was drawn: {ex.Message.TrimEnd('.')}.");
            }
        }

        return taken;
    }

    /// <summary>
    /// The configuration the worktree at <paramref name="path"/> runs on, or <see langword="null"/> where it cannot be
    /// read, or is another repository's.
    /// </summary>
    /// <remarks>
    /// Read only to reach the hosts that hold its copies, so one that cannot be read deletes nothing less: its copies
    /// are reached through the configuration the command runs in, and a host only it declared is said to be declared
    /// by none. A directory another repository holds, which --force deletes all the same, runs on that repository's
    /// configuration, and nothing of this one's is reached through it.
    /// </remarks>
    private async Task<HarnessConfig?> OwnConfigurationAsync(HarnessLayout layout, string path, CancellationToken cancellationToken)
    {
        try
        {
            var own = await _contextLoader.LoadAsync(path, cancellationToken).ConfigureAwait(false);

            return PathsEqual(own.Layout.MainCheckoutRoot, layout.MainCheckoutRoot) ? own.Config : null;
        }
        catch (Exception ex) when (ex is HarnessException or ConfigException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool PathsEqual(string left, string right) => PathContainment.AreSame(left, right, _platform.PathComparison);
}
