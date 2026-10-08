using System.Globalization;
using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Worktrees;

/// <summary>Creating, listing and removing development worktrees.</summary>
public interface IWorktreeService
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
    /// directory named for its orchestrator. Each name is held to worktrees.maxNameLength, and the whole path to the
    /// path budget, as create-worktree holds a plain one.
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
    /// where git's removal would stop part way on it.
    /// <paramref name="force"/> skips every check, and that look, and overrides a lock.
    /// The mutation workers kept beside the worktree go first, and beside each host's copy of it: one a sweep still
    /// running holds keeps the worktree, nothing of it removed, unless <paramref name="force"/> deletes it and leaves
    /// that worker - which deleting the worktree again removes, as it removes whatever one already gone left.
    /// </summary>
    /// <remarks>
    /// <paramref name="name"/> is an address: a plain worktree's name, or <c>orchestrator/agent</c>. A plain address with
    /// no .git of its own that holds worktrees below it - the directory named for an orchestrator, holding its agents' -
    /// is refused even with <paramref name="force"/>, since deleting it would delete each of them; one whose worktrees
    /// git cannot list is not deleted either. A worktree's own submodules and nested repositories never count.
    /// </remarks>
    /// <exception cref="HarnessException">
    /// As for <see cref="CreateAsync"/>, or the path resolved outside the worktrees directory.
    /// </exception>
    Task<WorktreeOutcome> DeleteAsync(
        string startDirectory,
        string name,
        bool force,
        bool deleteEvidence,
        bool discardUncommitted = false,
        CancellationToken cancellationToken = default);

    /// <summary>Lists existing worktrees with the commit each was made from.</summary>
    /// <exception cref="HarnessException">The repository is not initialised.</exception>
    Task<IReadOnlyList<WorktreeListing>> ListAsync(
        string startDirectory,
        CancellationToken cancellationToken = default);
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
    /// <summary>Where it is, links resolved, as git records it and as a sync records the tree a host's copy is of.</summary>
    public string Path { get; init; } = string.Empty;

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
    ISyncTransport local) : IWorktreeService
{
    /// <summary>The command a deletion reports under.</summary>
    internal const string DeleteCommand = "delete-worktree";

    /// <summary>The command a creation reports under.</summary>
    internal const string CreateCommand = "create-worktree";

    /// <summary>The command a listing reports under.</summary>
    internal const string ListCommand = "list-worktree";

    /// <summary>
    /// Where the commit a worktree was made from is recorded. Under <c>refs/harness/</c> rather than
    /// under heads, tags or remotes, so the record can never be mistaken for somewhere work is kept.
    /// </summary>
    internal const string BaseCommitRefPrefix = "refs/harness/worktree-base/";

    private const int GenerateAttempts = 10;

    /// <summary>
    /// How long the command line lets a deletion run after an interruption before it ends the process.
    /// Past its point of no return a deletion goes on after an interruption, and git removing a large
    /// tree on Windows can take far longer than the two seconds System.CommandLine otherwise waits.
    /// Two minutes usually lets that finish, and still ends a deletion that has hung; git is stopped
    /// shortly before, so what is left can still be reported.
    /// </summary>
    public static readonly TimeSpan DefaultInterruptionGrace = TimeSpan.FromMinutes(2);

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IPathBudget _pathBudget = pathBudget;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;
    private readonly IHostCopyRemover _hostCopies = hostCopies;

    /// <summary>This machine's own transport, which removes the mutation workers kept beside a worktree here.</summary>
    private readonly ISyncTransport _local = local;

    /// <summary>
    /// How long a deletion may run after an interruption before git is stopped, just before the
    /// command line stops waiting; tests shorten it.
    /// </summary>
    internal TimeSpan InterruptionGrace { get; init; } = DefaultInterruptionGrace;

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
            var generated = GenerateUnusedName(layout, settings.Root, Math.Min(WorktreeName.RandomLength, settings.MaxNameLength));
            if (generated is null)
            {
                // The caller's arguments were fine; the harness could not satisfy them,
                // which is a refusal rather than a usage error.
                return WorktreeOutcome.Failed(CommandOutcome.Refused(
                    $"Could not find an unused random name in {GenerateAttempts} attempts."));
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

        var budget = _pathBudget.Check(
            path,
            BuildDirectoryLength(context.Config) + settings.PathBudgetReserve,
            settings.PathBudgetMargin,
            settings.PathLimit);

        if (!budget.IsWithinBudget)
        {
            return WorktreeOutcome.Failed(CommandOutcome.Refused(budget.Describe(path)));
        }

        _fileSystem.CreateDirectory(parent);

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

        var baseCommit = await RecordBaseCommitAsync(layout, worktreeName, path, cancellationToken)
            .ConfigureAwait(false);

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
    /// Records the commit a worktree was made from, under <c>refs/harness/worktree-base/</c>, and
    /// returns it.
    /// </summary>
    /// <remarks>
    /// A worktree's HEAD moves as work is committed in it, so after the first commit nothing says
    /// what tree it started from any more, and it can only be reproduced from the moment it
    /// happened to be made. A ref is the record because git keeps it, it survives a clone of the
    /// repository, and it is not one of the refs a deletion counts as keeping a commit alive: the
    /// deletion check reads branches, tags, remote-tracking refs, the newest stash and other
    /// worktrees' HEADs, so this record cannot quietly make lost work look safe.
    /// A failure to record is reported and not fatal: the worktree exists, and refusing here would
    /// leave one behind that the caller was not told about.
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

        var recorded = await _gitClient
            .RunAsync(
                layout.MainCheckoutRoot,
                ["update-ref", BaseCommitRef(worktreeName), commit],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // The commit is what the worktree was made from whether or not the record took: said, and still returned.
        if (!recorded.Succeeded)
        {
            _output.Detail(CreateCommand, $"could not record the base commit: {recorded.FailureMessage}");
        }

        return commit;
    }

    /// <summary>Reads the recorded base commit of one worktree, or null when none was recorded.</summary>
    /// <exception cref="HarnessException">
    /// git could not be asked. Distinguished from "no such ref" on purpose: null here is reported as
    /// a worktree made before the record existed, and a reader who sees that stops looking — when
    /// what may have happened is that the record is there and git could not read it.
    /// </exception>
    private Task<string?> ReadBaseCommitAsync(
        HarnessLayout layout,
        string worktreeName,
        CancellationToken cancellationToken)
        => _gitClient.ResolveCommitAsync(layout.MainCheckoutRoot, BaseCommitRef(worktreeName), cancellationToken);

    /// <summary>Removes the base commit record of a worktree that no longer exists.</summary>
    private async Task ForgetBaseCommitAsync(
        HarnessLayout layout,
        string worktreeName,
        CancellationToken cancellationToken)
    {
        // Left behind, the record would answer for a later worktree of the same name with the
        // commit an earlier one started from.
        var removed = await _gitClient
            .RunAsync(
                layout.MainCheckoutRoot,
                ["update-ref", "-d", BaseCommitRef(worktreeName)],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!removed.Succeeded)
        {
            // Said rather than swallowed. The deletion itself succeeded, so this is not a failure of
            // the command; what is left is a stale record that will answer for the next worktree of
            // this name, and nobody would otherwise know to remove it.
            _output.Warn(
                DeleteCommand,
                $"'{worktreeName}' was deleted, and the commit it was made from is still recorded at "
                + $"'{BaseCommitRef(worktreeName)}': {removed.FailureMessage.TrimEnd('.')}. Remove it with "
                + $"'git update-ref -d {BaseCommitRef(worktreeName)}', or the next worktree of this name "
                + "inherits it.");
        }
    }

    private static string BaseCommitRef(string worktreeName) => BaseCommitRefPrefix + worktreeName;

    public async Task<WorktreeOutcome> DeleteAsync(
        string startDirectory,
        string name,
        bool force,
        bool deleteEvidence,
        bool discardUncommitted = false,
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

        var inspector = new WorktreeInspector(_gitClient, _fileSystem, _platform, _output);

        // A directory holding worktrees below it - an orchestrator's, holding its agents' - is never deleted as one,
        // --force or not: deleting it would delete each of them, with everything they hold and git's records left
        // naming directories that are gone. Only a plain address with no .git of its own can be one - an agent's
        // worktree holds none, and a worktree's submodules and nested repositories, a husk's included, are its own
        // contents, which the checks below weigh: below it count the worktrees git records, and, in the directory named
        // for an orchestrator, every one holding a .git of its own. Where that cannot be told, nothing is deleted.
        if (!address.IsNested && _fileSystem.DirectoryExists(path) && !WorktreeInspector.HoldsOwnGit(_fileSystem, path))
        {
            IReadOnlyList<string> below;

            try
            {
                below = await inspector.FindWorktreesBelowAsync(layout.MainCheckoutRoot, worktreesDirectory, path, IsOrchestrators(layout, worktreeName), cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
            {
                return Failed($"{ex.Message} Nothing was deleted: whether '{ReportText.Printable(path)}' holds worktrees below it could not be told.");
            }

            if (below.Count > 0)
            {
                return Refused(
                    $"'{ReportText.Printable(path)}' is not deleted, --force or not: it holds worktrees below it - {ReportText.Listed(below)} - and deleting it would "
                    + $"delete each of them. Delete each on its own, with '{ToolPackage.Command} {DeleteCommand} <address>' or, for an orchestrator's agent, delete-agent.");
            }
        }

        if (!_fileSystem.DirectoryExists(path))
        {
            var cleared = await ClearRecordAsync(layout, worktreeName, path, force, inspector, cancellationToken)
                .ConfigureAwait(false);

            // Nothing here and nothing in git's record: deleted already, or never made. Copies of it can
            // still be on hosts that could not be asked to remove them when it was deleted, and asking
            // them again is what deleting it again is for.
            if (cleared is null)
            {
                var left = await RemoveWorkersAsync(worktreeName, path, measureOnly: false, cancellationToken).ConfigureAwait(false);

                return await AndItsHostCopiesAsync(context, address, path, treeConfig: null, deleted: null, left.Lines, left.ExitCode, cancellationToken).ConfigureAwait(false);
            }

            if (cleared.Succeeded)
            {
                await ForgetBaseCommitAsync(layout, worktreeName, CancellationToken.None).ConfigureAwait(false);

                var left = await RemoveWorkersAsync(worktreeName, path, measureOnly: false, cancellationToken).ConfigureAwait(false);

                return await AndItsHostCopiesAsync(context, address, path, treeConfig: null, cleared, left.Lines, left.ExitCode, cancellationToken).ConfigureAwait(false);
            }

            return cleared;
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
            identity = await inspector.IdentifyAsync(layout.MainCheckoutRoot, path, cancellationToken).ConfigureAwait(false);
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

        var holdsSubmodules = false;
        IReadOnlyList<string> discarded = [];

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
                    .FindAsync(layout.MainCheckoutRoot, path, identity.AdministrativeDirectory!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
            {
                return Unchecked(worktreeName, ex.Message);
            }

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

            holdsSubmodules = findings.HoldsSubmodules;
        }

        // Read while the worktree is still there: its branch may declare a host the configuration
        // this command runs in does not, and its copy there is reached through it.
        var treeConfig = await OwnConfigurationAsync(layout, path, cancellationToken).ConfigureAwait(false);

        // On Windows, git's removal stops at the first entry another program holds, with the worktree's .git
        // file and git's record of it already gone, and only --force finishes it then. So each entry is opened
        // first as the removal will open it, and a worktree anything holds is refused whole, with nothing
        // removed. Forced, nothing is asked: the removal goes as far as it can, and says what it left.
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

        // The mutation workers kept beside it go first: each is a copy of this tree a sweep made, which nothing would
        // ever remove once the tree is gone. One still in use - a sweep still running holds it - says the tree is, and
        // keeps it; forced, the worktree goes and that worker is left, and said. They are asked about before any goes,
        // so a deletion refused for one has removed nothing, a worker no sweep holds included.
        if (!force && Kept(await RemoveWorkersAsync(worktreeName, path, measureOnly: true, cancellationToken).ConfigureAwait(false), "Nothing was removed") is { } kept)
        {
            return kept;
        }

        var workers = await RemoveWorkersAsync(worktreeName, path, measureOnly: false, cancellationToken).ConfigureAwait(false);

        // A sweep that took one between the asking and the removal keeps the worktree as well: the others are gone by then.
        if (!force && Kept(workers, "Nothing of the worktree was removed") is { } late)
        {
            return late;
        }

        // What keeps the worktree, of what became of its workers - one in use, or none that could be looked for - or null.
        WorktreeOutcome? Kept(WorkersGone gone, string nothing)
            => gone.InUse is { } busy
                ? WorktreeOutcome.Failed(CommandOutcome.Refused(
                    $"Worktree '{worktreeName}' was not deleted: a mutation worker kept beside it is in use - '{busy.Path}': {busy.Why.TrimEnd('.')}. "
                    + $"Wait for it to end, or pass --force to delete the worktree and leave that worker. {nothing}.",
                    gone.Lines))
                : gone.Failure is { } failure
                    ? Failed(
                        $"The mutation workers kept beside worktree '{worktreeName}' could not be removed: {failure.TrimEnd('.')}. Nothing of the worktree "
                        + "was removed: put that right and try again, or pass --force to delete the worktree and leave them.")
                    : null;

        // The last moment an interruption can stop this cleanly. From here the deletion goes on
        // after an interruption, which is reported at once; it can still be left partly done, and
        // --force then finishes it.
        cancellationToken.ThrowIfCancellationRequested();

        WorktreeOutcome outcome;

        // The warning an interruption gives, that the deletion may be left half done, is about this
        // part alone: once it is over, the worktree is gone or the outcome says what is left.
        using (var stopping = new CancellationTokenSource())
        using (WarnIfInterrupted(worktreeName, cancellationToken, stopping))
        {
            try
            {
                outcome = force
                    ? await RemoveForcedAsync(layout, worktreeName, path, identity.AdministrativeDirectory, inspector, stopping.Token).ConfigureAwait(false)
                    : await RemoveCheckedAsync(layout, worktreeName, path, identity.AdministrativeDirectory!, holdsSubmodules, discarded.Count > 0, stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return Stopped(worktreeName, path, identity.AdministrativeDirectory, checksRan: !force);
            }
        }

        if (!outcome.Succeeded)
        {
            return outcome;
        }

        if (discarded.Count > 0)
        {
            outcome = outcome with
            {
                Outcome = outcome.Outcome with
                {
                    Details = [.. outcome.Outcome.Details ?? [], $"discarded {discarded.Count} uncommitted change(s): {ReportText.Listed(discarded)}"],
                },
            };
        }

        // A worker left here was left by --force, which chose to: said, and never what fails the deletion it asked for.
        if (workers.Lines.Count > 0)
        {
            outcome = outcome with { Outcome = outcome.Outcome with { Details = [.. outcome.Outcome.Details ?? [], .. workers.Lines] } };
        }

        // Only once the worktree is really gone. Forgetting it earlier would lose the record of a
        // worktree a failed removal left in place. Not cancelled: the removal is past its point of
        // no return, and a record left behind would answer for a later worktree of the same name.
        await ForgetBaseCommitAsync(layout, worktreeName, CancellationToken.None).ConfigureAwait(false);
        return await AndItsHostCopiesAsync(context, address, path, treeConfig, outcome, [], workersLeft: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What removing the mutation workers kept beside a worktree did, as its deletion says it.</summary>
    /// <param name="Lines">What was removed, and each worker left, with why.</param>
    /// <param name="InUse">The first worker left because something is using it, or <see langword="null"/>.</param>
    /// <param name="Failure">Why they could not be looked for or removed at all, or <see langword="null"/>.</param>
    private sealed record WorkersGone(IReadOnlyList<string> Lines, WorkerLeft? InUse, string? Failure)
    {
        /// <summary>
        /// The exit code a worker not yet removed leaves a deletion with - refused where one is in use, failed where they
        /// could not be removed - or <see langword="null"/> where none is left that asking again would remove.
        /// </summary>
        public int? ExitCode => InUse is not null ? HarnessExit.Refused : Failure is not null ? HarnessExit.CommandFailed : null;
    }

    /// <summary>
    /// Removes the mutation workers kept beside the worktree at <paramref name="path"/> - each a copy of it a sweep made,
    /// of whichever variant - and says what it did: what was removed, and each left, with why. Asked only to measure, it
    /// removes none, and says each that would be left.
    /// </summary>
    /// <param name="name">The worktree's address, for the command that removes what was left.</param>
    /// <param name="path">The worktree's directory, there or gone.</param>
    /// <param name="measureOnly">Whether to say what would be left, and remove nothing.</param>
    /// <param name="cancellationToken">Stops the removal between workers.</param>
    private async Task<WorkersGone> RemoveWorkersAsync(string name, string path, bool measureOnly, CancellationToken cancellationToken)
    {
        var again = $"'{ToolPackage.Command} {DeleteCommand} {name}'";
        WorkersRemoval removal;

        try
        {
            removal = await _local.RemoveWorkersAsync(path, measureOnly, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            return new WorkersGone(
                [$"the mutation workers kept beside it could not be removed: {ex.Message.TrimEnd('.')}; {again} removes them once that is put right"],
                null,
                ex.Message);
        }

        var lines = new List<string>();

        if (!measureOnly && removal.Removed.Count > 0)
        {
            lines.Add(
                $"removed {removal.Removed.Count} mutation worker(s) kept beside it, {DiskSpace.Size(removal.Bytes)}: "
                + string.Join(", ", removal.Removed.Select(worker => $"'{worker.Path}'")));
        }

        lines.AddRange(removal.Left.Select(worker => worker.InUse
            ? $"left the mutation worker '{worker.Path}' kept beside it: {worker.Why.TrimEnd('.')}; {again} removes it once nothing holds it"
            : $"left '{worker.Path}', named as a mutation worker kept beside it: {worker.Why.TrimEnd('.')}"));

        return new WorkersGone(lines, removal.InUse.FirstOrDefault(), null);
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

        var names = new List<(string Name, string Path)>();
        var inspector = new WorktreeInspector(_gitClient, _fileSystem, _platform, _output);

        foreach (var directory in Directories(worktreesDirectory, "the worktrees root"))
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

            if (string.IsNullOrEmpty(name) || TakeListed(directory, name, orchestrator: null))
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

                if (!string.IsNullOrEmpty(agent) && TakeListed(child, agent, orchestrator: name))
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

        // Whether the directory is a worktree git records, listed under its address; one holding a .git entry git does
        // not record is said, and taken as dealt with, since it is nobody's orchestrator.
        bool TakeListed(string directory, string name, string? orchestrator)
        {
            var address = orchestrator is null ? name : WorktreeAddress.Nested(orchestrator, name).Name;
            var resolved = _fileSystem.ResolveLinks(directory);

            if (inspector.RecordAt(registered, resolved) is not null)
            {
                names.Add((address, resolved));
                return true;
            }

            // A mutation worker kept beside a worktree: a copy of it with a repository of its own, which git records
            // nowhere and which goes with its worktree. One whose worktree is gone has nothing left to take it with, and
            // is said, with what removes it.
            if (MutationWorkers.TreeNamed(name) is { } copied)
            {
                if (!_fileSystem.DirectoryExists(Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory))!, copied)))
                {
                    var tree = orchestrator is null ? copied : WorktreeAddress.Nested(orchestrator, copied).Name;

                    _output.Warn(
                        ListCommand,
                        $"'{address}' is a mutation worker of the worktree '{tree}', which is gone: '{ToolPackage.Command} {DeleteCommand} {tree}' removes it.");
                }

                return true;
            }

            if (WorktreeInspector.HoldsOwnGit(_fileSystem, directory))
            {
                // Said without being asked. A directory holding a .git entry that git no longer
                // records is what a removal leaves when git's record went and a file in use did not,
                // and its name stays taken: dropped from the listing silently, it looks free.
                _output.Warn(
                    ListCommand,
                    $"'{address}' holds a .git entry, and git records no worktree there: what a removal leaves "
                    + "when git's record goes and something in the directory could not. It is not listed, "
                    + "and a new worktree cannot take its name until it is gone; look inside for anything "
                    + $"wanted, then delete '{directory}'.");
                return true;
            }

            return false;
        }

        var listings = new List<WorktreeListing>();

        foreach (var (name, path) in names.OrderBy(worktree => worktree.Name, StringComparer.Ordinal))
        {
            listings.Add(new WorktreeListing(
                name,
                await ReadBaseCommitAsync(context.Layout, name, cancellationToken).ConfigureAwait(false))
            {
                Path = path,
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
    /// How many characters <c>build/&lt;variant&gt;/</c> adds below a worktree, for the longest
    /// variant among the legs this machine builds; zero when it builds none.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <remarks>
    /// Counted here rather than folded into worktrees.pathBudgetReserve, because the harness knows
    /// every variant's name and the reserve could only guess at one: when build directories came to
    /// be keyed by variant, a reserve measured below the build directory stopped covering the
    /// directory itself, and the check believed it had twenty characters to spare where it had four.
    /// A leg builds in this machine's worktree when it names no host of its own and its operating
    /// system is this machine's; one that names a host builds there, whatever its os says.
    /// </remarks>
    private int BuildDirectoryLength(Configuration.HarnessConfig config)
    {
        var longest = config.Legs.Values
            .Where(leg => leg.Wsl is null && leg.Ssh is null)
            .Where(leg => string.Equals(leg.Os, _platform.PlatformKey, StringComparison.OrdinalIgnoreCase))
            .Select(leg => Build.VariantKey.For(config, leg, _platform.PlatformKey).DirectoryName.Length)
            .DefaultIfEmpty(-1)
            .Max();

        // A separator between 'build' and the variant, and one after the variant. The one before
        // 'build' joins the worktree to what is reserved below it, which the budget counts itself.
        return longest < 0 ? 0 : Build.VariantKey.BuildRootName.Length + longest + 2;
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
    /// Once past the point of no return, says the moment an interruption arrives that the deletion
    /// is under way and how to finish it, and arranges for git to be stopped shortly before the
    /// command line stops waiting, so that what is left can still be reported rather than the
    /// process ending in the middle without a word.
    /// </summary>
    private CancellationTokenRegistration WarnIfInterrupted(
        string name,
        CancellationToken cancellationToken,
        CancellationTokenSource stopping)
        => cancellationToken.Register(() =>
        {
            _output.Warn(
                DeleteCommand,
                $"Deleting worktree '{name}' is under way and may be left half done; "
                + $"if it is, run '{ToolPackage.Command} {DeleteCommand} {name} --force' to finish it.");

            stopping.CancelAfter(InterruptionGrace - TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(10).Ticks, InterruptionGrace.Ticks / 4)));
        });

    /// <summary>A deletion stopped because the command line was about to stop waiting for it.</summary>
    private WorktreeOutcome Stopped(string name, string path, string? administrativeDirectory, bool checksRan)
        => WorktreeOutcome.Failed(CommandOutcome.Failed(
            HarnessExit.Cancelled,
            $"Deleting worktree '{name}' was stopped part way, before the interruption ended the command. "
            + DescribeWhatRemains(name, path, administrativeDirectory, checksRan)));

    /// <summary>
    /// Clearing a record stopped the same way. Its worktree's directory was already gone before the
    /// command began, so nothing of it was lost; only git's record may still be there.
    /// </summary>
    private static WorktreeOutcome StoppedClearing(string name)
        => WorktreeOutcome.Failed(CommandOutcome.Failed(
            HarnessExit.Cancelled,
            $"Clearing git's record of worktree '{name}', whose directory was already gone, was stopped before the interruption "
            + $"ended the command; run '{ToolPackage.Command} {DeleteCommand} {name}' again to finish clearing it."));

    /// <summary>
    /// Removes a worktree whose checks passed. Plain removal runs git's own check as well, which still
    /// catches a file changed since ours, a file added unless status.showUntrackedFiles is no, or a
    /// lock. git refuses every worktree that holds submodules, so for those it is told to go ahead,
    /// skipping that check: their work, and the lock, were checked already. So it is for one whose
    /// uncommitted changes are being discarded, which git's check would refuse; once forced, git still
    /// keeps a lock, which only forcing twice overrides.
    /// </summary>
    private async Task<WorktreeOutcome> RemoveCheckedAsync(
        HarnessLayout layout,
        string name,
        string path,
        string administrativeDirectory,
        bool holdsSubmodules,
        bool discardsChanges,
        CancellationToken stopping)
    {
        // git for Windows leaves every directory junction when it removes a worktree, and every directory above one,
        // reporting the worktree removed with its .git file and its record already gone. So each junction is removed
        // first, as the link it is, never what it leads to: one that cannot be removed stops the deletion here, before
        // git has touched anything. Not interrupted part way: the links go together, before git starts.
        IReadOnlyList<string> unlinked;

        try
        {
            unlinked = _fileSystem.RemoveJunctions(path);
        }
        catch (JunctionRemovalException ex)
        {
            var before = ex.Removed.Count == 0
                ? "Nothing of it was removed"
                : $"Only the {ex.Removed.Count} junction(s) before it were removed, as links, and what each led to is untouched: {ReportText.Listed(ex.Removed)}";

            return Failed(
                $"Worktree '{name}' was not deleted: its directory junction {ex.Message.TrimEnd('.')}. {before}; git was not run. "
                + $"Deal with that junction, then run '{ToolPackage.Command} {DeleteCommand} {name}' again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(
                $"Worktree '{name}' was not deleted: '{ReportText.Printable(path)}' could not be looked through for its directory junctions, "
                + $"which git would leave: {ex.Message.TrimEnd('.')}. Nothing of it was removed; git was not run.");
        }

        string[] arguments = holdsSubmodules || discardsChanges
            ? ["worktree", "remove", "--force", path]
            : ["worktree", "remove", path];

        var removal = await _gitClient
            .RunAsync(layout.MainCheckoutRoot, arguments, cancellationToken: stopping)
            .ConfigureAwait(false);

        var junctions = unlinked.Count == 0
            ? string.Empty
            : $" Its {unlinked.Count} directory junction(s) had been removed first, as links: {ReportText.Listed(unlinked)}.";

        // Nothing more is deleted here when git fails: git's refusal is the second check, and
        // deleting past it would undo the point of running it.
        if (!removal.Succeeded)
        {
            return Failed(
                $"git could not remove worktree '{name}': {removal.FailureMessage.TrimEnd('.')}. "
                + DescribeWhatRemains(name, path, administrativeDirectory, checksRan: true)
                + junctions);
        }

        if (_fileSystem.DirectoryExists(path))
        {
            return Failed(
                $"git reported worktree '{name}' removed, but '{path}' still exists. "
                + DescribeWhatRemains(name, path, administrativeDirectory, checksRan: true)
                + junctions);
        }

        var verified = Verified(name, path, administrativeDirectory, gitFailure: null);

        return unlinked.Count == 0 || !verified.Succeeded
            ? verified
            : verified with
            {
                Outcome = verified.Outcome with
                {
                    Details = [.. verified.Outcome.Details ?? [], $"removed {unlinked.Count} directory junction(s) first, as links, never what they lead to: {ReportText.Listed(unlinked)}"],
                },
            };
    }

    /// <summary>
    /// What a removal git gave up on, or that was stopped, left behind, and how to finish. git deletes
    /// a worktree's .git file and its record even when it fails to delete a file under it, after which
    /// no check can run again, and it can delete files before failing at all, so a removal once
    /// started is never reported as having deleted nothing. Finishing with --force is only called safe
    /// where the checks ran, since a forced removal never made any.
    /// </summary>
    private string DescribeWhatRemains(string name, string path, string? administrativeDirectory, bool checksRan)
    {
        var finish = $"'{ToolPackage.Command} {DeleteCommand} {name} --force'";
        var gone = new List<string>();

        if (!_fileSystem.DirectoryExists(path))
        {
            gone.Add("its directory");
        }
        else if (!WorktreeInspector.HoldsOwnGit(_fileSystem, path))
        {
            gone.Add("its .git file");
        }

        if (administrativeDirectory is not null && !_fileSystem.DirectoryExists(administrativeDirectory))
        {
            gone.Add("git's record of it");
        }

        if (gone.Count > 0)
        {
            var safe = checksRan ? "Every check passed before removal began, so run" : "Run";

            return $"It stopped part way: {string.Join(" and ", gone)} {(gone.Count == 1 ? "is" : "are")} already gone. "
                + $"{safe} {finish} to finish deleting it.";
        }

        var remaining = administrativeDirectory is null
            ? "Its directory and its .git file are"
            : "Its directory, its .git file and git's record of it are";

        return $"{remaining} still there, but git may already have deleted some files, which would now stop a checked delete as changes. "
            + $"Close whatever holds its files open, look at what is left with 'git -C {ReportText.Printable(path)} status', then run {finish} to finish deleting it.";
    }

    /// <summary>
    /// Removes a worktree without checking it: its directory, and git's record of it, even when it
    /// is locked, its .git file is gone, or it belongs to another repository.
    /// </summary>
    private async Task<WorktreeOutcome> RemoveForcedAsync(
        HarnessLayout layout,
        string name,
        string path,
        string? administrativeDirectory,
        WorktreeInspector inspector,
        CancellationToken stopping)
    {
        // Forced twice, git removes a locked worktree as well.
        var removal = await _gitClient
            .RunAsync(layout.MainCheckoutRoot, ["worktree", "remove", "--force", "--force", path], cancellationToken: stopping)
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
                    + $"Close whatever is using it, then run '{ToolPackage.Command} {DeleteCommand} {name} --force'.");
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
                .RunAsync(layout.MainCheckoutRoot, ["worktree", "remove", "--force", "--force", path], cancellationToken: stopping)
                .ConfigureAwait(false);

            clearingFailure = clearing.Succeeded ? null : clearing.FailureMessage;
        }

        return administrativeDirectory is null
            ? await VerifiedByListAsync(layout, name, path, clearingFailure, inspector).ConfigureAwait(false)
            : Verified(name, path, administrativeDirectory, clearingFailure);
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

        using var stopping = new CancellationTokenSource();
        using var interruption = WarnIfInterrupted(name, cancellationToken, stopping);

        try
        {
            string[] arguments = force
                ? ["worktree", "remove", "--force", "--force", path]
                : ["worktree", "remove", path];

            var removal = await _gitClient
                .RunAsync(layout.MainCheckoutRoot, arguments, cancellationToken: stopping.Token)
                .ConfigureAwait(false);

            if (!removal.Succeeded)
            {
                return Failed($"git could not clear its record of worktree '{name}', whose directory is gone: {removal.FailureMessage}");
            }

            return await VerifiedByListAsync(layout, name, path, gitFailure: null, inspector).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return StoppedClearing(name);
        }
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
    /// <param name="workers">
    /// What removing the mutation workers left beside a worktree already gone did, said with what the hosts did; none
    /// where the worktree was deleted here, whose own outcome says it.
    /// </param>
    /// <param name="workersLeft">
    /// The exit code a worker left beside a worktree already gone leaves the command with, since asking again removes
    /// it; <see langword="null"/> where none was, or where deleting the worktree here chose to leave it.
    /// </param>
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
        IReadOnlyList<string> workers,
        int? workersLeft,
        CancellationToken cancellationToken)
    {
        var name = address.Name;
        var done = deleted is null ? $"Worktree '{name}' is gone already" : $"Worktree '{name}' was deleted";
        var again = $"'{ToolPackage.Command} {DeleteCommand} {name}'";
        IReadOnlyList<string> before = [.. deleted?.Outcome.Details ?? [], .. workers];
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

        if (removal.Lines.Count == 0 && !removal.Interrupted && workers.Count == 0)
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
            _ when workersLeft is { } left => CommandOutcome.Failed(
                left,
                $"{done}, and a mutation worker left beside it is not yet removed: run {again} once what keeps it is gone.",
                details),
            _ when deleted is null => CommandOutcome.Ok(
                (removal.Lines.Count > 0, workers.Count > 0) switch
                {
                    (true, true) => $"{done}; each copy of it left on a host, and the mutation workers left beside it, are dealt with.",
                    (false, _) => $"{done}; the mutation workers left beside it are dealt with.",
                    _ => $"{done}; each copy of it left on a host is dealt with.",
                },
                details),
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
    /// <remarks>
    /// This runs whatever the interruption did: it deletes nothing and takes a moment, and stopping
    /// it would report a deletion that finished as one left half done.
    /// </remarks>
    private async Task<WorktreeOutcome> VerifiedByListAsync(
        HarnessLayout layout,
        string name,
        string path,
        string? gitFailure,
        WorktreeInspector inspector)
    {
        GitWorktree? remaining;

        try
        {
            remaining = await inspector.FindRecordAsync(layout.MainCheckoutRoot, path, CancellationToken.None).ConfigureAwait(false);
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

        if (findings.Changes.Count > 0)
        {
            reasons.Add(
                $"it has {findings.Changes.Count} uncommitted change(s) that would be lost: {ReportText.Listed(findings.Changes)} "
                + "(commit them to a branch, run 'git stash -u', or pass --discard-uncommitted to delete them with the worktree)");
        }

        if (findings.Commits > 0 && findings.Head is { } head)
        {
            var shortHead = ReportText.Commit(head);

            reasons.Add(
                $"{findings.Commits} commit(s) up to {shortHead} are on no branch, tag, remote-tracking ref, newest stash or other worktree's HEAD "
                + $"(run 'git branch <name> {shortHead}', or push them)");
        }

        foreach (var submodule in findings.Submodules)
        {
            var held = (submodule.Commits > 0, submodule.HasStash) switch
            {
                (true, true) => $"{submodule.Commits} commit(s) no remote-tracking ref or tag contains, and a stash",
                (true, false) => $"{submodule.Commits} commit(s) no remote-tracking ref or tag contains",
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
    /// would stop git part way, and what to do. Run from inside the worktree, this command holds it itself, which
    /// nothing another program closes lets go, so that is said, with running it from outside.
    /// </summary>
    private string DescribeHeld(string name, IReadOnlyList<HeldEntry> held)
    {
        var again = $"'{ToolPackage.Command} {DeleteCommand} {name}'";
        var own = held.Any(entry => PathsEqual(entry.Path, Environment.CurrentDirectory));
        var remedy = !own ? $"Close what holds it, then run {again} again."
            : held.Count == 1 ? $"It is this command's own current directory: run {again} again from outside the worktree."
            : $"One is this command's own current directory: close what holds the rest, then run {again} again from outside the worktree.";

        return $"Worktree '{name}' was not deleted, and nothing of it was removed: something holds "
            + $"{ReportText.Listed([.. held.Select(entry => $"'{ReportText.Printable(entry.Path)}'")])} - a process whose current directory is in "
            + "the worktree, a program with a file of it open, or a program running from it. Windows would not let git "
            + "delete what is held, so git would stop part way, with the worktree's .git file and git's record of it "
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
            ? $"1 declared evidence directory holds measurements that only exist there: {ReportText.Listed(occupied)}"
            : $"{occupied.Count} declared evidence directories hold measurements that only exist there: {ReportText.Listed(occupied)}";
    }

    /// <summary>
    /// Generates a name no existing worktree or orchestrator already uses, or <see langword="null"/>
    /// when repeated attempts all collided.
    /// </summary>
    private string? GenerateUnusedName(HarnessLayout layout, string root, int length)
    {
        for (var attempt = 0; attempt < GenerateAttempts; attempt++)
        {
            var candidate = WorktreeName.Generate(length);

            if (!_fileSystem.DirectoryExists(layout.WorktreePathUnder(root, candidate))
                && !_fileSystem.DirectoryExists(Path.Combine(layout.OrchestratorsDirectory, candidate)))
            {
                return candidate;
            }
        }

        return null;
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
