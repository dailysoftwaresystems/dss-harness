using System.Globalization;
using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Core.Git;

/// <inheritdoc cref="IGitClient"/>
/// <param name="processRunner">Runs git.</param>
/// <param name="output">Where git's own output is echoed, for the commands that echo it.</param>
/// <param name="filePermissions">
/// Says whether a file has an execute bit, for the mode an index entry records; this platform's own
/// where none is given.
/// </param>
/// <param name="localVariables">
/// The variables every git command starts without; asked of git through <paramref name="processRunner"/>
/// where none is given.
/// </param>
/// <param name="fileSystem">
/// Says whether a tree's file system finds a name in any case, which git status is told; this platform's own where none is
/// given.
/// </param>
public sealed partial class GitClient(
    IProcessRunner processRunner,
    IHarnessOutput output,
    IFilePermissions? filePermissions = null,
    GitLocalVariables? localVariables = null,
    IFileSystem? fileSystem = null) : IGitClient
{
    private const string GitExecutable = "git";

    /// <summary>
    /// What the index a copy's staging builds is called, beside the index git reads. Nothing else writes
    /// a file of this name, so one a stopped sync left, and the lock git took on it, are this tool's own.
    /// </summary>
    private const string StagingIndexSuffix = ".harness-sync";

    /// <summary>
    /// How git says a directory is in no repository it can open: in none at all, or below a .git file naming one that is
    /// not there - in its own words since git 2.56, where older git said "not a git repository" for that too.
    /// </summary>
    private static readonly string[] NotARepositoryAnswers = ["not a git repository", "gitfile does not point to a valid repository"];

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IHarnessOutput _output = output;
    private readonly IFilePermissions _filePermissions = filePermissions ?? FilePermissionsFactory.Create();
    private readonly GitLocalVariables _localVariables = localVariables ?? new GitLocalVariables(processRunner);
    private readonly IFileSystem _fileSystem = fileSystem ?? new PhysicalFileSystem(filePermissions ?? FilePermissionsFactory.Create());

    /// <summary>The version the git this client runs names itself by, once read (<see cref="InstalledVersionAsync"/>).</summary>
    private string? _installed;

    public bool IsInstalled() => _processRunner.FindExecutable(GitExecutable) is not null;

    public async Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = await RunQueryAsync(
            directory,
            ["rev-parse", "--is-inside-work-tree"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // A repository git refuses to open is not "not a repository". Answering that
            // sends the user to `git init` inside a repository that already exists, when
            // git's own message (dubious ownership, most often) names the exact remedy.
            EnsureNotAnError(result, directory);
            return false;
        }

        return string.Equals(result.StandardOutput.Trim(), "true", StringComparison.Ordinal);
    }

    public async Task<string?> GetRepositoryRootAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var result = await RunQueryAsync(
            directory,
            ["rev-parse", "--show-toplevel"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            EnsureNotAnError(result, directory);
            return null;
        }

        var root = result.StandardOutput.Trim();
        if (root.Length == 0)
        {
            // Older git answers a bare repository with success and no output.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git found a repository at '{directory}' but reported no working tree for it.");
        }

        return NormalizeDirectory(root);
    }

    public async Task<GitWorktree?> GetMainWorktreeAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        // Taken from git's worktree list rather than derived from --git-common-dir. Asked
        // from the main checkout, that answer is the relative ".git", so the root would be
        // spelled the way the caller typed the directory, while GetRepositoryRootAsync
        // answers with symbolic links resolved. On macOS, where /var links to /private/var,
        // one checkout would carry two spellings and compare unequal to itself. git lists
        // worktrees in the same resolved form as --show-toplevel.
        var result = await RunQueryAsync(
            directory,
            ["worktree", "list", "--porcelain"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            EnsureNotAnError(result, directory);
            return null;
        }

        // git always lists the main worktree first - named for the git directory its worktrees share:
        // that directory's parent, where it is called .git, and the directory itself otherwise, which is
        // no checkout. A submodule's is named so, kept under its superproject's .git/modules, and so is
        // one made apart from its checkout with --separate-git-dir.
        var worktrees = ParseWorktrees(result.StandardOutput);

        return worktrees.FirstOrDefault() is { IsBare: false } main
            && await IsGitDirectoryAsync(main.Path, cancellationToken).ConfigureAwait(false)
            ? await CheckoutOfAsync(directory, main, worktrees, cancellationToken).ConfigureAwait(false)
            : worktrees.FirstOrDefault();
    }

    /// <summary>
    /// Whether <paramref name="path"/>, as git spells a worktree it lists, is a git directory itself: git
    /// asked there names it as its own git directory, where a checkout's is the <c>.git</c> in it.
    /// <see langword="false"/> where git cannot say.
    /// </summary>
    /// <remarks>
    /// Asked where the git directory is, never with <c>--is-inside-git-dir</c>: in a submodule's git
    /// directory, whose <c>core.worktree</c> names its checkout, git 2.43 answers that false and git 2.55
    /// true. <c>--absolute-git-dir</c> resolves the path as the list does, so the two compare as written.
    /// </remarks>
    private async Task<bool> IsGitDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunQueryAsync(path, ["rev-parse", "--absolute-git-dir"], cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            && result.StandardOutput.Trim() is { Length: > 0 } gitDirectory
            && string.Equals(NormalizeDirectory(gitDirectory), path, StringComparison.Ordinal);
    }

    /// <summary>
    /// The main worktree git named <paramref name="listed"/>, its git directory: the checkout that
    /// directory's configuration records, as a submodule's <c>core.worktree</c> does - or, where it
    /// records none, as <c>--separate-git-dir</c> leaves it, the tree at <paramref name="directory"/>
    /// where <paramref name="worktrees"/> lists it as no linked worktree. <see langword="null"/> where
    /// neither says which checkout it is.
    /// </summary>
    private async Task<GitWorktree?> CheckoutOfAsync(
        string directory,
        GitWorktree listed,
        IReadOnlyList<GitWorktree> worktrees,
        CancellationToken cancellationToken)
    {
        var recorded = await RunQueryAsync(listed.Path, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);

        if (recorded.Succeeded && recorded.StandardOutput.Trim() is { Length: > 0 } checkout)
        {
            return listed with { Path = NormalizeDirectory(checkout) };
        }

        // git spells a worktree it lists as it spells the top of one it is asked from.
        var here = await GetRepositoryRootAsync(directory, cancellationToken).ConfigureAwait(false);

        return here is not null && !worktrees.Skip(1).Any(worktree => string.Equals(worktree.Path, here, StringComparison.Ordinal))
            ? listed with { Path = here }
            : null;
    }

    public async Task<bool> IsDirtyAsync(string directory, CancellationToken cancellationToken = default)
    {
        var entries = await GetStatusAsync(directory, cancellationToken).ConfigureAwait(false);
        return entries.Count > 0;
    }

    public Task<IReadOnlyList<GitStatusEntry>> GetStatusAsync(string directory, CancellationToken cancellationToken = default)
        => StatusAsync(directory, "normal", cancellationToken);

    public async Task<bool> IgnoresCaseAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = await RunQueryAsync(directory, ["config", "--bool", "--get", "core.ignorecase"], cancellationToken).ConfigureAwait(false);

        // git answers 1 for a key that is not set: its own default, which heeds case.
        if (!result.TimedOut && result.ExitCode == 1)
        {
            return false;
        }

        Ensure(result, "read whether the repository ignores case");

        return string.Equals(result.StandardOutput.Trim(), "true", StringComparison.Ordinal);
    }

    public Task<IReadOnlyList<GitStatusEntry>> ReadStatusAsync(string directory, CancellationToken cancellationToken = default)
        => StatusAsync(directory, "all", cancellationToken);

    public async Task<IReadOnlySet<string>> ListChangedSinceAsync(string directory, string commit, CancellationToken cancellationToken = default)
        => GitName.PathsOf(await ListNamesChangedSinceAsync(directory, commit, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<GitName>> ListNamesChangedSinceAsync(string directory, string commit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        // git diff against the commit compares the way git status does: the index's record of a file whose stat is clean,
        // and otherwise the file through the clean filters and the line-ending rules that read the index, so a file only
        // checked out with other line endings is no change; and its mode, where core.filemode trusts one. Without git's
        // optional locks, the index it refreshes to answer is never written. Untracked files are not listed.
        var result = await RunForBytesAsync(
            directory,
            ["--no-optional-locks", "diff", "--name-only", "-z", "--no-renames", "--ignore-submodules=none", commit, "--"],
            cancellationToken).ConfigureAwait(false);

        Ensure(result, $"read what the work tree changes since {commit}");

        return [.. Records(result.StandardOutput).Select(GitName.FromBytes)];
    }

    /// <summary>
    /// The one status question, with the untracked files named as <paramref name="untracked"/> says: <c>normal</c> names an
    /// untracked directory as one entry, <c>all</c> names each file in one, git's ignore rules followed - which a walk of
    /// the directory would not follow.
    /// </summary>
    /// <remarks>
    /// Asked with <c>-z</c> and read as bytes, so no path is quoted or mangled and a name that is not UTF-8 is kept as git
    /// holds it. Untracked files and submodules are asked for explicitly, because configuration can hide both: under
    /// status.showUntrackedFiles=no a new file is not listed at all, and the tree reads as clean to a caller about to
    /// discard it. <c>--no-optional-locks</c> keeps the question from writing anything: status otherwise refreshes the
    /// index and writes it back whenever it can take the lock.
    /// <para>
    /// An untracked file that is a tracked one the disk spells otherwise is not listed (<see cref="IndexedInAnotherCaseAsync"/>):
    /// where a directory finds a name in any case and core.ignorecase says the repository does not, git lists such a file
    /// twice, changed under the index's name and untracked under the disk's. Measured: a write put in place over such a
    /// file keeps the old spelling on macOS and takes the new one on Windows, so a fold of the one file wrote it, then
    /// stopped part way at its other name.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<GitStatusEntry>> StatusAsync(string directory, string untracked, CancellationToken cancellationToken)
    {
        var result = await RunForBytesAsync(
            directory,
            ["--no-optional-locks", "status", "--porcelain", "-z", $"--untracked-files={untracked}", "--ignore-submodules=none"],
            cancellationToken).ConfigureAwait(false);

        // An empty list must mean "nothing changed", never "the question failed": a caller reads it as a clean tree and
        // proceeds over uncommitted work.
        Ensure(result, "read the repository status");

        var entries = ParseStatus(result.StandardOutput);
        var indexed = await IndexedInAnotherCaseAsync(
                directory,
                [.. entries.Where(entry => entry is { Index: '?', WorkTree: '?', Path.IsUtf8: true }).Select(entry => entry.Path.Text)],
                cancellationToken)
            .ConfigureAwait(false);

        return indexed.Count == 0
            ? entries
            : [.. entries.Where(entry => entry is not { Index: '?', WorkTree: '?', Path.IsUtf8: true } || !indexed.Contains(entry.Path.Text))];
    }

    public async Task<IReadOnlySet<string>> IndexedInAnotherCaseAsync(
        string directory,
        IReadOnlyCollection<string> untracked,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(untracked);

        var indexed = new HashSet<string>(StringComparer.Ordinal);

        // A directory git names as one untracked entry may hold a new file beside the tracked one, so it stays listed.
        var files = untracked.Where(path => !path.EndsWith('/')).ToList();

        // Where the repository says it ignores case, git already finds an untracked name in the index in any case.
        if (files.Count == 0 || await IgnoresCaseAsync(directory, cancellationToken).ConfigureAwait(false))
        {
            return indexed;
        }

        // The whole index, by the names git keeps from the top of the tree, as status names every path.
        var result = await RunForBytesAsync(directory, ["ls-files", "-z", "--cached", "--full-name", "--", ":/"], cancellationToken).ConfigureAwait(false);

        Ensure(result, "read the index");

        var held = Records(result.StandardOutput)
            .Select(GitName.FromBytes)
            .Where(name => name.IsUtf8)
            .Select(name => name.Text)
            .ToLookup(name => name, StringComparer.OrdinalIgnoreCase);

        if (!files.Any(file => held[file].Any(spelling => spelling != file)))
        {
            return indexed;
        }

        var top = await GetRepositoryRootAsync(directory, cancellationToken).ConfigureAwait(false) ?? directory;

        // Decided file by file, as each directory compares names: a tree may hold a directory that tells names apart
        // below one that does not, and the other way round.
        indexed.UnionWith(files.Where(file => held[file].Any(spelling => spelling != file && SameFileInAnotherCase(_fileSystem, top, file, spelling))));
        return indexed;
    }

    /// <summary>
    /// Whether <paramref name="untracked"/> and <paramref name="indexed"/>, two paths from the top of the tree at
    /// <paramref name="top"/> that differ in case alone, name one entry on disk: at each part where they differ, the
    /// directory holding it lists the untracked spelling and not the index's, finds the untracked name in another case it
    /// does not list - so it does not tell names apart by case, and holds no other spelling of it - and reaches something
    /// by the index's spelling. False where a directory tells names apart, the tracked file and the untracked one two files
    /// of it, or reaches nothing by the index's spelling, a tracked file deleted beside a new one; and where a directory
    /// cannot be listed: the untracked name is kept, never taken for a file the index holds.
    /// </summary>
    /// <param name="fileSystem">The file system to ask.</param>
    /// <param name="top">The top of the tree.</param>
    /// <param name="untracked">The path git status called untracked, as the disk spells it, with forward separators.</param>
    /// <param name="indexed">The path the index holds, with forward separators.</param>
    internal static bool SameFileInAnotherCase(IFileSystem fileSystem, string top, string untracked, string indexed)
    {
        var onDisk = untracked.Split('/');
        var inIndex = indexed.Split('/');

        if (onDisk.Length != inIndex.Length)
        {
            return false;
        }

        try
        {
            var directory = top;

            for (var index = 0; index < onDisk.Length; index++)
            {
                if (onDisk[index] != inIndex[index])
                {
                    var listed = fileSystem.EnumerateDirectories(directory)
                        .Concat(fileSystem.EnumerateFiles(directory, recursive: false))
                        .Select(Path.GetFileName)
                        .ToHashSet(StringComparer.Ordinal);

                    if (!listed.Contains(onDisk[index])
                        || listed.Contains(inIndex[index])
                        || PathCase.OtherCase(onDisk[index]) is not { } probe
                        || listed.Contains(probe)
                        || fileSystem.KindOf(Path.Combine(directory, probe)) == PathKind.None
                        || fileSystem.KindOf(Path.Combine(directory, inIndex[index])) == PathKind.None)
                    {
                        return false;
                    }
                }

                directory = Path.Combine(directory, onDisk[index]);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<IReadOnlyDictionary<string, GitHeld>> HeldAtAsync(
        string directory,
        string commit,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(paths);

        var held = new Dictionary<string, GitHeld>(StringComparer.Ordinal);

        if (paths.Count == 0)
        {
            return held;
        }

        // A line break cannot travel as a line of git's batch input, so such a path is answered from the commit's
        // listing instead, as reading files at a commit does.
        var tree = paths.Any(HoldsLineBreak) ? await ListTreeAsync(directory, commit, cancellationToken).ConfigureAwait(false) : null;
        var asked = paths.Where(path => !HoldsLineBreak(path)).Distinct(StringComparer.Ordinal).ToList();

        var objects = await ObjectsAtAsync(directory, [.. asked.Select(path => (commit, path))], commit, $"which files {commit} holds", "which files it holds", cancellationToken)
            .ConfigureAwait(false);
        var unanswered = new List<string>();

        for (var index = 0; index < asked.Count; index++)
        {
            held[asked[index]] = objects[index] switch
            {
                ("blob", var id) => GitHeld.File(id),
                ("tree", _) => GitHeld.Directory,
                _ => Unanswered(asked[index]),
            };
        }

        // What git did not answer as a file or a directory is a submodule's entry, or a path the commit does not hold. An
        // entry is answered as the commit it names where this repository holds that commit; where it does not, as missing
        // by git 2.43, which Ubuntu 24.04 ships, as the path is, and as a submodule by later versions. The commit's listing
        // tells them apart on every version, and names the commit an entry does.
        if (unanswered.Count > 0)
        {
            tree ??= await ListTreeAsync(directory, commit, cancellationToken).ConfigureAwait(false);
            var unread = unanswered.ToHashSet(StringComparer.Ordinal);

            foreach (var entry in tree.Where(entry => entry.Type == "commit" && entry.Name.IsUtf8 && unread.Contains(entry.Name.Text)))
            {
                held[entry.Name.Text] = GitHeld.Submodule(entry.ObjectId);
            }

            await RefuseListedButUnreadAsync(directory, commit, unanswered, FilesIn(tree), cancellationToken).ConfigureAwait(false);
        }

        foreach (var path in paths.Where(HoldsLineBreak))
        {
            // The listing names files and submodules; a directory is what holds a name below it.
            held[path] = tree!.FirstOrDefault(entry => entry.Name.IsUtf8 && entry.Name.Text == path) is { } entry
                ? entry.IsFile ? GitHeld.File(entry.ObjectId) : GitHeld.Submodule(entry.ObjectId)
                : tree!.Any(entry => entry.Name.IsUtf8 && entry.Name.Text.StartsWith(path + "/", StringComparison.Ordinal)) ? GitHeld.Directory : GitHeld.Nothing;
        }

        return held;

        GitHeld Unanswered(string path)
        {
            unanswered.Add(path);
            return GitHeld.Nothing;
        }
    }

    /// <summary>
    /// The object each commit of <paramref name="pairs"/> holds at its path - its type and id - asked of one git process,
    /// or null where the commit holds none there. Each commit is asked about first, in the same process: git answers
    /// "missing" for a commit it cannot read exactly as it does for a path the commit does not hold, and an unreadable
    /// commit passed off as absent files would let a fold take every file for one the agent added.
    /// </summary>
    /// <param name="directory">Where git is asked.</param>
    /// <param name="pairs">Each commit and path asked about, neither holding a line break.</param>
    /// <param name="about">What was asked about, as a refusal names it: a commit, or how many.</param>
    /// <param name="unknown">What could not be read where git fails, to follow "read".</param>
    /// <param name="untold">What cannot be told where git answers other than it was asked, to end a sentence after <paramref name="about"/>.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <exception cref="HarnessException">git could not answer, answered other than it was asked, or cannot read one of the commits.</exception>
    private async Task<IReadOnlyList<(string Type, string Id)?>> ObjectsAtAsync(
        string directory,
        IReadOnlyList<(string Commit, string Path)> pairs,
        string about,
        string unknown,
        string untold,
        CancellationToken cancellationToken)
    {
        var commits = pairs.Select(pair => pair.Commit).Distinct(StringComparer.Ordinal).ToList();
        var input = new StringBuilder();

        foreach (var commit in commits)
        {
            input.Append(commit).Append("^{commit}\n");
        }

        foreach (var (commit, path) in pairs)
        {
            input.Append(commit).Append(':').Append(path).Append('\n');
        }

        var result = await RunCoreAsync(
                directory,
                ["cat-file", "--batch-check=%(objecttype) %(objectname)"],
                echoOutput: false,
                untranslated: false,
                indexFile: null,
                input.ToString(),
                cancellationToken)
            .ConfigureAwait(false);

        Ensure(result, $"read {unknown}");

        var lines = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToList();

        if (lines.Count != commits.Count + pairs.Count)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git answered {ReportText.Counted(lines.Count, "line")} for the {commits.Count + pairs.Count} it was asked about {about}, so {untold} cannot be told.");
        }

        for (var at = 0; at < commits.Count; at++)
        {
            if (!lines[at].StartsWith("commit ", StringComparison.Ordinal))
            {
                throw new HarnessException(HarnessExit.CommandFailed, $"git cannot read commit {commits[at]}, so {untold} cannot be told: {lines[at]}");
            }
        }

        // An answer is the object's type and id; anything else is git's word that the commit holds nothing there.
        return [.. lines.Skip(commits.Count).Select(line => line.Split(' ') is [var type and ("blob" or "tree" or "commit" or "tag"), var id] ? (type, id) : ((string Type, string Id)?)null)];
    }

    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            directory,
            ["worktree", "list", "--porcelain"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Reporting an empty list would read as "this repository has no worktrees",
        // which is a different answer from "I could not find out".
        Ensure(result, "list the worktrees");

        return ParseWorktrees(result.StandardOutput);
    }

    public async Task<GitLocation?> GetLocationAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        var result = await RunQueryAsync(
            directory,
            ["rev-parse", "--show-prefix", "--absolute-git-dir"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            EnsureNotAnError(result, directory);
            return null;
        }

        // Read by position: at the root of a work tree the prefix is an empty line, and
        // dropping empty lines would take the git directory for the prefix.
        var lines = result.StandardOutput.Split('\n');

        if (lines.Length < 2 || lines[1].TrimEnd('\r').Length == 0)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git did not report where '{directory}' sits in its repository.");
        }

        return new GitLocation(lines[0].TrimEnd('\r'), NormalizeDirectory(lines[1].TrimEnd('\r')));
    }

    public async Task IndexExactlyAsync(
        string directory,
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(paths);

        // Its own index and no other. A directory inside another repository's work tree is part of that
        // repository: staging there puts these files into a history that is not theirs, and takes out
        // whatever that repository tracks below the directory.
        if (await GetLocationAsync(directory, cancellationToken).ConfigureAwait(false) is not { Prefix.Length: 0 })
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{directory}' is not the top of a git repository of its own, so nothing was staged there: git "
                + "finds it inside another repository's work tree, or in none, and staging would write that "
                + "repository's index.");
        }

        var index = await GetIndexFileAsync(directory, cancellationToken).ConfigureAwait(false);
        var staging = index + StagingIndexSuffix;

        Remove(staging);
        Remove(staging + ".lock");

        var wanted = paths
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(path => File.Exists(Path.Combine(directory, path)))
            .ToList();

        // Each file's bytes as they stand, through no filter and no line-ending conversion. Staging a file runs it
        // through the filters and conversions the repository's attributes name, under whatever git configuration the
        // machine has, and a filter that fails fails the whole staging: a host that set Git LFS up where a
        // non-interactive shell cannot find it, or one that refuses a line ending conversion it cannot undo, then
        // failed every sync. Nothing here needs what a filter makes of a file: the index names the files a tree holds,
        // and a build reads each one's bytes itself.
        var ids = await HashAsync(directory, wanted, new Hashing(Written: true, Filtered: false), "read what the tree holds", cancellationToken).ConfigureAwait(false);
        var modes = await ModesAsync(directory, wanted, cancellationToken).ConfigureAwait(false);

        // Built afresh in an index of this tool's own, and put in place whole. Staging into the index git
        // reads took its lock, and a sync stopped part way - a dropped connection kills git where it
        // stands - left that lock behind for good: every sync after it that changed a file failed. What is
        // left of this one is removed above, and what git reads is only ever a finished index.
        var emptied = await RunWithIndexAsync(directory, staging, ["read-tree", "--empty"], null, cancellationToken)
            .ConfigureAwait(false);

        Ensure(emptied, "start the index afresh");

        if (wanted.Count > 0)
        {
            // The paths travel on standard input, NUL-separated, so no list of them outgrows a command
            // line and no name is read as an option or a pathspec.
            var entries = new StringBuilder();

            for (var at = 0; at < wanted.Count; at++)
            {
                entries.Append(modes[at]).Append(' ').Append(ids[at]).Append(" 0\t").Append(wanted[at]).Append('\0');
            }

            var written = await RunWithIndexAsync(
                    directory,
                    staging,
                    ["update-index", "-z", "--index-info"],
                    entries.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);

            Ensure(written, "write the index");
        }

        try
        {
            File.Move(staging, index, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The index built for '{directory}' could not replace '{index}': {ex.Message}");
        }
    }

    public async Task CheckOutAtAsync(string directory, string commit, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return;
        }

        // The paths travel on standard input, NUL-separated, so no list of them outgrows a command line, and each is read
        // literally: a name holding a wildcard or a colon never matches another path than its own.
        var result = await RunCoreAsync(
                directory,
                ["--literal-pathspecs", "checkout", "--no-overlay", "--quiet", commit, "--pathspec-from-file=-", "--pathspec-file-nul"],
                echoOutput: false,
                untranslated: false,
                indexFile: null,
                string.Join('\0', paths),
                cancellationToken)
            .ConfigureAwait(false);

        Ensure(result, $"write what {commit} holds into '{directory}'");
    }

    public async Task<IReadOnlySet<string>> ListHeldAsAtAsync(
        string directory,
        string commit,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(paths);

        var asked = paths.ToHashSet(StringComparer.Ordinal);
        var files = asked.Count == 0
            ? []
            : (await ListTreeAsync(directory, commit, cancellationToken).ConfigureAwait(false))
                .Where(entry => entry.IsFile && entry.Name.IsUtf8 && asked.Contains(entry.Name.Text))
                .ToList();

        if (files.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // An index of this question's own, holding the commit's entry for each file asked about and nothing else: git then
        // compares each with the work tree as it would once the work tree's index held it, whatever that index holds now -
        // nothing, for a file never added. Its own name, so two questions at once never share one.
        var index = Path.Combine(Path.GetTempPath(), $"dssharness-index-{Guid.NewGuid():N}");

        try
        {
            var entries = new StringBuilder();

            foreach (var file in files)
            {
                entries.Append(file.Mode).Append(' ').Append(file.ObjectId).Append(" 0\t").Append(file.Name.Text).Append('\0');
            }

            // --no-split-index writes it whole: a split index would otherwise be written as a new shared index file in the
            // work tree's git directory.
            var written = await RunWithIndexAsync(
                    directory,
                    index,
                    ["update-index", "--no-split-index", "-z", "--index-info"],
                    entries.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);

            Ensure(written, $"write an index to compare the work tree with {commit}");

            // The diff git status makes, not diff-files: an entry with no record of its file's stat is then read and
            // compared, through the clean filters and the line-ending rules, and never listed for its stat alone.
            var differing = await RunCoreAsync(
                    directory,
                    ["--no-optional-locks", "diff", "--name-only", "-z", "--no-renames", "--ignore-submodules=none", "--"],
                    echoOutput: false,
                    untranslated: false,
                    index,
                    standardInput: null,
                    cancellationToken,
                    Encoding.Latin1)
                .ConfigureAwait(false);

            Ensure(differing, $"compare the work tree with {commit}");

            var changed = GitName.PathsOf([.. Records(differing.StandardOutput).Select(GitName.FromBytes)]);

            return files.Select(file => file.Name.Text).Where(path => !changed.Contains(path)).ToHashSet(StringComparer.Ordinal);
        }
        finally
        {
            Discard(index);
            Discard(index + ".lock");
        }
    }

    /// <summary>
    /// Removes <paramref name="path"/>, a file of one question's own in the system's temporary directory, where it is
    /// there and can be: one that cannot is left for the system to clear, and nothing is raised for it.
    /// </summary>
    /// <remarks>
    /// Never <see cref="Remove"/>, which raises: raised from a <see langword="finally"/> it took the place of whatever the
    /// question itself had failed with - git's own words for why a comparison could not be made - and failed a
    /// comparison that had been made, in the words of a staging that stopped part way, which this is not.
    /// </remarks>
    internal static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing reads it again: its name is this question's alone.
        }
    }

    public async Task ResetToAsync(string directory, string commit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        var result = await RunCoreAsync(directory, ["reset", "--mixed", "--quiet", commit, "--"], echoOutput: false, untranslated: false, cancellationToken).ConfigureAwait(false);

        Ensure(result, $"move '{directory}' to {commit}");
    }

    /// <summary>How <c>git hash-object</c> is asked to read files: what differs between its callers.</summary>
    /// <param name="Written">Whether each object is written: into <paramref name="Store"/> where one is given, into the repository otherwise.</param>
    /// <param name="Filtered">
    /// Whether each file is read through the clean filters and the line-ending rules of a path, its own or
    /// <paramref name="AsPath"/>, rather than as its bytes stand.
    /// </param>
    /// <param name="AsPath">The path whose rules apply to every file, for files kept somewhere else; each file's own where null.</param>
    /// <param name="Store">An object directory of the question's own; the repository's where null.</param>
    /// <param name="Told">
    /// What git is told before the command, which changes how it reads a path's rules: a setting, or the tree its
    /// attributes are read from (<see cref="Storing"/>).
    /// </param>
    private sealed record Hashing(bool Written, bool Filtered, string? AsPath = null, ObjectStore? Store = null, IReadOnlyList<string>? Told = null);

    /// <summary>
    /// The object each of <paramref name="files"/> holds, in the same order, read as <paramref name="how"/> says: the one
    /// caller of <c>git hash-object</c>, so the rule for a name that cannot travel on its standard input
    /// (<see cref="NeedsAHashOfItsOwn"/>) is in one place. What git would record for a file is asked through
    /// <see cref="StoredIdsAsync"/>, which says how.
    /// </summary>
    /// <param name="directory">The directory git runs in, which the files are relative to.</param>
    /// <param name="files">The files to read.</param>
    /// <param name="how">Whether each is written, whether it is filtered and as which path, what git is told, and where its object goes.</param>
    /// <param name="what">What was being done, for the failure.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    private async Task<IReadOnlyList<string>> HashAsync(
        string directory,
        IReadOnlyList<string> files,
        Hashing how,
        string what,
        CancellationToken cancellationToken)
    {
        string[] command =
        [
            .. how.Told ?? [],
            "hash-object",
            .. how.Written ? ["-w"] : Array.Empty<string>(),
            .. !how.Filtered ? ["--no-filters"] : how.AsPath is { } asPath ? [$"--path={asPath}"] : Array.Empty<string>(),
        ];

        Task<GitCommandResult> RunAsync(IReadOnlyList<string> arguments, ChildInput? input)
            => RunCoreAsync(directory, [.. command, .. arguments], echoOutput: false, untranslated: false, indexFile: null, input, cancellationToken, objects: how.Store);

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);

        // A path given for them all names their rules, and git reads no list of files on its standard input beside it:
        // they are named as arguments, a handful of this client's own. Otherwise every name that can travel on standard
        // input does, and each that cannot is asked about on its own.
        var alone = how.AsPath is null ? files.Where(NeedsAHashOfItsOwn).ToHashSet(StringComparer.Ordinal) : [];
        var together = files.Where(file => !alone.Contains(file)).ToList();

        if (together.Count > 0)
        {
            var hashed = how.AsPath is null
                ? await RunAsync(["--stdin-paths"], string.Join('\n', together) + '\n').ConfigureAwait(false)
                : await RunAsync(["--", .. together], null).ConfigureAwait(false);

            Ensure(hashed, what);

            var answers = hashed.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .ToList();

            if (answers.Count != together.Count)
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git named {ReportText.Counted(answers.Count, "object")} for the {ReportText.Counted(together.Count, "file")} it was given to read.");
            }

            for (var at = 0; at < together.Count; at++)
            {
                ids[together[at]] = answers[at];
            }
        }

        foreach (var file in alone)
        {
            var hashed = await RunAsync(["--", file], null).ConfigureAwait(false);

            Ensure(hashed, what);
            ids[file] = hashed.StandardOutput.Trim();
        }

        return [.. files.Select(file => ids[file])];
    }

    /// <summary>
    /// Whether <paramref name="path"/> cannot travel as a line of <c>--stdin-paths</c>: one holding a
    /// line break, or starting with the quote that makes git unquote the line.
    /// </summary>
    private static bool NeedsAHashOfItsOwn(string path)
        => path.Contains('\n', StringComparison.Ordinal)
            || path.Contains('\r', StringComparison.Ordinal)
            || path.StartsWith('"');

    /// <summary>The mode git itself would record for each of <paramref name="paths"/>, in the same order.</summary>
    /// <remarks>
    /// Where the repository trusts file modes - <c>core.filemode</c>, which git sets false where the
    /// file system keeps none - a file is executable as its execute bit says. Where it does not, an entry
    /// the index already holds keeps its mode, and a file new to it is not executable.
    /// </remarks>
    private async Task<IReadOnlyList<string>> ModesAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var trusted = await RunQueryAsync(
                directory,
                ["config", "--bool", "--get", "core.filemode"],
                cancellationToken)
            .ConfigureAwait(false);

        // Unset is git's own default, which trusts them.
        var trustsModes = !trusted.Succeeded || !string.Equals(trusted.StandardOutput.Trim(), "false", StringComparison.Ordinal);

        var held = trustsModes
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await ListIndexAsync(directory, cancellationToken).ConfigureAwait(false))
                .Where(entry => entry.Stage == 0 && entry.IsRegularFile)
                .GroupBy(entry => entry.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Mode, StringComparer.Ordinal);

        return
        [
            .. paths.Select(path => trustsModes
                ? _filePermissions.IsExecutable(Path.Combine(directory, path)) ? "100755" : "100644"
                : held.GetValueOrDefault(path, "100644")),
        ];
    }

    /// <summary>Removes <paramref name="path"/> where it is there.</summary>
    private static void Remove(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{path}', left by a staging that stopped part way, could not be removed: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        // -v is what shows the flags that hide an edit from status, and -z keeps every path intact.
        var result = await RunAsync(
            directory,
            ["ls-files", "--stage", "-v", "-z"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Ensure(result, "read the index");

        var entries = new List<GitIndexEntry>();

        foreach (var field in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // The tag, mode, object and stage, separated by spaces, then a tab and the path.
            var tab = field.IndexOf('\t');
            string[] parts = tab < 0 ? [] : field[..tab].Split(' ');

            if (parts.Length != 4
                || parts[0].Length != 1
                || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var stage))
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git listed an index entry in a form this build cannot read: '{field}'");
            }

            entries.Add(new GitIndexEntry(parts[0][0], parts[1], parts[2], stage, field[(tab + 1)..]));
        }

        return entries;
    }

    public Task<string> GetIndexFileAsync(string directory, CancellationToken cancellationToken = default)
        => GitPathAsync(directory, "index", "find the index", cancellationToken);

    public async Task<IReadOnlyList<string>> FindEditedFilesAsync(
        string directory,
        string indexCopy,
        IReadOnlyList<string> assumedUnchanged,
        IReadOnlyList<string> skipWorktree,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assumedUnchanged);
        ArgumentNullException.ThrowIfNull(skipWorktree);

        // Each mark is cleared by a call of its own: given both options, update-index clears only
        // the first. The paths travel on standard input, so no list of them outgrows a command line.
        foreach (var (option, paths) in new[] { ("--no-assume-unchanged", assumedUnchanged), ("--no-skip-worktree", skipWorktree) })
        {
            if (paths.Count == 0)
            {
                continue;
            }

            // --no-split-index writes the copy whole: a split index would otherwise be written as
            // a new shared index file beside the real one, in the worktree's git directory.
            var cleared = await RunWithIndexAsync(
                directory,
                indexCopy,
                ["update-index", "--no-split-index", option, "-z", "--stdin"],
                string.Join('\0', paths) + '\0',
                cancellationToken).ConfigureAwait(false);

            Ensure(cleared, "clear the marks in a copy of the index");
        }

        // Status compares the files the way it compares any other. Hashing one alone would skip
        // git's rule that leaves line endings already stored in the index as they are, and report
        // an untouched file as edited under core.autocrlf.
        var status = await RunCoreAsync(
            directory,
            ["--no-optional-locks", "status", "--porcelain", "-z", "--untracked-files=no", "--ignore-submodules=all"],
            echoOutput: false,
            untranslated: false,
            indexCopy,
            standardInput: null,
            cancellationToken,
            Encoding.Latin1).ConfigureAwait(false);

        Ensure(status, "compare the files marked assume-unchanged or skip-worktree");

        var marked = new HashSet<string>([.. assumedUnchanged, .. skipWorktree], StringComparer.Ordinal);

        return [.. ParseStatus(status.StandardOutput).Select(entry => entry.Path.Text).Where(marked.Contains)];
    }

    public async Task<string?> ResolveGitDirectoryAsync(
        string directory,
        string path,
        CancellationToken cancellationToken = default)
    {
        var result = await RunQueryAsync(
            directory,
            ["rev-parse", "--resolve-git-dir", path],
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return NormalizeDirectory(result.StandardOutput.Trim());
        }

        // "not a gitdir" is git's answer that the path is no repository; any other failure is git
        // unable to look, which must not read as a directory holding nothing.
        if (!result.TimedOut && result.StandardError.Contains("not a gitdir", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        throw new HarnessException(
            HarnessExit.CommandFailed,
            $"git could not tell whether '{path}' is a repository: {result.FailureMessage}");
    }

    public async Task<int> CountCommitsAsync(
        string directory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            directory,
            ["rev-list", "--count", .. revisions],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ReadCount(result);
    }

    public async Task<int> CountRepositoryCommitsAsync(
        string gitDirectory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            gitDirectory,
            [.. RepositoryOnly(gitDirectory), "rev-list", "--count", .. revisions],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ReadCount(result);
    }

    public async Task<bool> HasStashAsync(string gitDirectory, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            gitDirectory,
            [.. RepositoryOnly(gitDirectory), "rev-parse", "--verify", "--quiet", "refs/stash^{commit}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // With --verify --quiet, git answers "no stash" with exit code 1 and nothing else; any other
        // failure is git being unable to look.
        return result.ExitCode switch
        {
            0 when !result.TimedOut => true,
            1 when !result.TimedOut => false,
            _ => throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not look for a stash in '{gitDirectory}': {result.FailureMessage}"),
        };
    }

    public async Task<bool> IsIgnoredAsync(
        string directory,
        string path,
        CancellationToken cancellationToken = default)
    {
        // check-ignore's exit code is three valued: 0 matched, 1 did not match, and
        // anything else is a failure. Folding the third into "not ignored" would
        // report a secret as safe to copy because the question itself errored.
        var result = await RunAsync(
            directory,
            ["check-ignore", "--quiet", "--no-index", "--", Literally(path)],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.ExitCode switch
        {
            0 when !result.TimedOut => true,
            1 when !result.TimedOut => false,
            _ => throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not determine whether '{path}' is ignored: {result.FailureMessage}"),
        };
    }

    public async Task<IReadOnlyList<IgnoreDecision>> ExplainIgnoredAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return [];
        }

        return await DecideEachAsync(
                directory,
                paths,
                consultIndex: false,
                $"which rules decide {ReportText.Counted(paths.Count, "path")} in '{directory}'",
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> FindIgnoredAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // A path git will not answer about is one git add refuses as well, and no rule ignores it.
        return (await DecideEachAsync(
                    directory,
                    paths,
                    consultIndex: true,
                    $"which of {ReportText.Counted(paths.Count, "path")} it ignores in '{directory}'",
                    cancellationToken)
                .ConfigureAwait(false))
            .Where(decision => decision.Source is not null)
            .Select(decision => decision.Path)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Asks git which rule decides each of <paramref name="paths"/>, every path answered that git answers about. One path
    /// git will not answer about - one beyond a symbolic link, which git never looks past, or one below a submodule where
    /// the index is consulted - ends the whole question. git is then asked about the work tree's root, which it answers
    /// about wherever it can answer at all, and where it cannot the question is refused at once; otherwise the question is
    /// halved, and each half that fails halved again, until each part is answered or is one path, which says why it was
    /// not - a few processes for each path git will not answer about, however many are asked.
    /// </summary>
    /// <param name="directory">The work tree's root.</param>
    /// <param name="paths">Paths relative to that root, with forward separators; at least one.</param>
    /// <param name="consultIndex">Whether a tracked file matches nothing, as <c>git add</c> judges it, rather than the rules alone.</param>
    /// <param name="asked">What was asked, as a refusal names it.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">git could not answer in <paramref name="directory"/> at all, or answered in another form.</exception>
    private async Task<IReadOnlyList<IgnoreDecision>> DecideEachAsync(
        string directory,
        IReadOnlyList<string> paths,
        bool consultIndex,
        string asked,
        CancellationToken cancellationToken)
    {
        var result = await CheckIgnoreAsync(directory, paths, cancellationToken, consultIndex).ConfigureAwait(false);

        if (Answered(result))
        {
            return ReadDecisions(result.StandardOutput, paths);
        }

        if (result.TimedOut || !Answered(await CheckIgnoreAsync(directory, ["."], cancellationToken, consultIndex).ConfigureAwait(false)))
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"Could not ask git {asked}: {result.FailureMessage}");
        }

        var decisions = new List<IgnoreDecision>(paths.Count);

        await HalveAsync(paths, result).ConfigureAwait(false);

        return decisions;

        // A part git refused, halved: each half answered is read, in order, and each refused halved again, down to the
        // one path git will not answer about.
        async Task HalveAsync(IReadOnlyList<string> refused, GitCommandResult failed)
        {
            if (refused.Count == 1)
            {
                decisions.Add(new IgnoreDecision(refused[0], null, 0, null) { Unanswered = failed.FailureMessage });
                return;
            }

            foreach (var half in new[] { refused.Take(refused.Count / 2).ToList(), refused.Skip(refused.Count / 2).ToList() })
            {
                var answer = await CheckIgnoreAsync(directory, half, cancellationToken, consultIndex).ConfigureAwait(false);

                if (Answered(answer))
                {
                    decisions.AddRange(ReadDecisions(answer.StandardOutput, half));
                }
                else if (answer.TimedOut)
                {
                    throw new HarnessException(HarnessExit.CommandFailed, $"Could not ask git {asked}: {answer.FailureMessage}");
                }
                else
                {
                    await HalveAsync(half, answer).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Asks git which rule decides each of <paramref name="paths"/>, as <c>check-ignore</c> answers.</summary>
    /// <remarks>
    /// NUL-separated both ways, so a path or a rule may hold anything a line can; and every path
    /// answered, matched or not, so the answer lines up with the question. Without the index unless
    /// <paramref name="consultIndex"/>, so a tracked file is judged by the rules alone; with it, a tracked file
    /// matches nothing, as <c>git add</c> treats it. Each path is asked <see cref="Literally"/>.
    /// </remarks>
    private Task<GitCommandResult> CheckIgnoreAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken, bool consultIndex = false)
        => RunForBytesAsync(
            directory,
            ["check-ignore", "--verbose", "--non-matching", .. consultIndex ? Array.Empty<string>() : ["--no-index"], "-z", "--stdin"],
            cancellationToken,
            string.Concat(paths.Select(path => Literally(path) + '\0')));

    /// <summary>
    /// <paramref name="path"/> as <c>check-ignore</c> reads it literally: after <c>./</c>, so a name beginning with a colon
    /// is never read as pathspec magic, which check-ignore refuses outright - with <c>--literal-pathspecs</c> too, for every
    /// path - and the rules still match it as they match the path itself.
    /// </summary>
    /// <param name="path">A path relative to the work tree's root, with forward separators.</param>
    internal static string Literally(string path) => "./" + path;

    /// <summary>
    /// Whether <c>check-ignore</c> answered: 0 when some path matched a rule and 1 when none did.
    /// Anything else is git unable to answer, and folded into "nothing matched" it would report every
    /// path asked about as unruled.
    /// </summary>
    private static bool Answered(GitCommandResult result) => !result.TimedOut && result.ExitCode is 0 or 1;

    /// <summary>
    /// What <c>check-ignore -v -n -z</c> printed, one decision per path of <paramref name="paths"/>, in order, each asked
    /// <see cref="Literally"/>.
    /// </summary>
    /// <exception cref="HarnessException">git answered in another form, or about other paths.</exception>
    internal static IReadOnlyList<IgnoreDecision> ReadDecisions(string output, IReadOnlyList<string> paths)
    {
        // Four fields to a path - source, line, pattern, path - and nothing after the last NUL.
        var fields = output.Split('\0');

        if (fields.Length != (paths.Count * 4) + 1)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git answered which rules decide {ReportText.Counted(paths.Count, "path")} in a form this build cannot read.");
        }

        var decisions = new List<IgnoreDecision>(paths.Count);

        for (var index = 0; index < paths.Count; index++)
        {
            var source = GitName.FromBytes(fields[index * 4]).Quoted;
            var pattern = GitName.FromBytes(fields[(index * 4) + 2]).Quoted;
            var answered = GitName.FromBytes(fields[(index * 4) + 3]).Text;
            var path = paths[index];

            // git names each path as it was asked, literally.
            if (!string.Equals(answered, Literally(path), StringComparison.Ordinal))
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git answered about '{answered}' where it was asked about '{Literally(path)}'.");
            }

            if (source.Length == 0)
            {
                decisions.Add(new IgnoreDecision(path, null, 0, null));
                continue;
            }

            if (!int.TryParse(fields[(index * 4) + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var line))
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git answered which rule decides '{path}' in a form this build cannot read: "
                    + $"'{fields[(index * 4) + 1]}' is not a line number.");
            }

            decisions.Add(new IgnoreDecision(path, source, line, pattern));
        }

        return decisions;
    }

    public async Task<string?> ResolveCommitAsync(
        string directory,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        // --end-of-options keeps a reference that starts with a dash from being read as an option,
        // and ^{commit} refuses a tag or tree that is not a commit.
        var result = await RunAsync(
            directory,
            ["rev-parse", "--verify", "--quiet", "--end-of-options", reference + "^{commit}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return result.StandardOutput.Trim();
        }

        // With --verify --quiet, git answers "no such commit" with exit code 1 and nothing else;
        // any other failure is git being unable to look.
        if (result.ExitCode == 1 && !result.TimedOut)
        {
            return null;
        }

        throw new HarnessException(
            HarnessExit.CommandFailed,
            $"Could not resolve '{reference}': {result.FailureMessage}");
    }

    public async Task<string?> MergeBaseAsync(
        string directory,
        string first,
        IReadOnlyList<string> others,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(first);
        ArgumentNullException.ThrowIfNull(others);

        if (others.Count == 0 || others.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one commit to measure against, and no empty name.", nameof(others));
        }

        // Each name read as ResolveCommitAsync reads it, a commit: a short id a tree or a blob shares is still the commit's.
        var result = await RunAsync(
            directory,
            ["merge-base", "--end-of-options", .. new[] { first }.Concat(others).Select(name => name + "^{commit}")],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return result.StandardOutput.Trim();
        }

        var named = $"'{first}' and {Merged(others)}";

        // git answers "no common ancestor" with exit code 1 and nothing else; any other failure is git being unable
        // to look, or a name that names no commit. In a shallow clone the answer may mean only that the history here
        // stops first, so it is never read as histories that share nothing - nor is a clone whose history could not be
        // told whole.
        if (result.ExitCode == 1 && !result.TimedOut)
        {
            var (shallow, answer) = await ShallowAnswerAsync(directory, cancellationToken).ConfigureAwait(false);

            switch (shallow)
            {
                case false:
                    return null;

                case true:
                    return await ParentNamedByAnotherAsync(directory, first, others, cancellationToken).ConfigureAwait(false)
                        ?? throw new HarnessException(
                            HarnessExit.CommandFailed,
                            $"Could not find where {named} part: this clone's history is shallow, and may stop before they part "
                            + "- or they share none. Fetch the rest of it (git fetch --unshallow), or clone with the whole history, "
                            + "and run again.");

                default:
                    throw new HarnessException(
                        HarnessExit.CommandFailed,
                        $"git found nothing {named} share, and could not say whether this clone holds their whole history: {answer}");
            }
        }

        throw new HarnessException(
            HarnessExit.CommandFailed,
            $"Could not find where {named} part: {result.FailureMessage}");
    }

    public async Task<IReadOnlyList<string>> ListMergeHeadsAsync(string directory, CancellationToken cancellationToken = default)
    {
        // One line per commit coming in: an octopus merge brings several.
        var path = await GitPathAsync(directory, "MERGE_HEAD", "find where git records a merge in progress", cancellationToken).ConfigureAwait(false);

        try
        {
            return File.Exists(path)
                ? [.. File.ReadAllText(path).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)]
                : [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The merge was finished, or given up, between the look and the read.
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read '{path}', where git records the merge in progress: {ex.Message.TrimEnd('.')}.");
        }
    }

    public async Task<bool?> IsShallowAsync(string directory, CancellationToken cancellationToken = default)
        => (await ShallowAnswerAsync(directory, cancellationToken).ConfigureAwait(false)).Shallow;

    /// <summary>
    /// Whether the clone at <paramref name="directory"/> is shallow, or <see langword="null"/> where git could not say,
    /// with git's answer in words for a message: its failure, or output that is neither answer - the option itself,
    /// which a git older than 2.15 echoes back.
    /// </summary>
    private async Task<(bool? Shallow, string Answer)> ShallowAnswerAsync(string directory, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            directory,
            ["rev-parse", "--is-shallow-repository"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return (null, result.FailureMessage);
        }

        return result.StandardOutput.Trim() switch
        {
            "true" => (true, "true"),
            "false" => (false, "false"),
            var other => (null, $"'{other}'"),
        };
    }

    /// <summary>The commits a merge base is measured against, as a message names them: one, or the merge of several.</summary>
    private static string Merged(IReadOnlyList<string> commits)
        => string.Join(" merged with ", commits.Select(commit => $"'{commit}'"));

    /// <summary>
    /// A commit another's own object names as a parent, where that answers where they part, or <see langword="null"/>:
    /// <paramref name="first"/> where any of <paramref name="others"/> names it, an ancestor of each then and so of their
    /// merge; or, measured against one commit alone, that one where <paramref name="first"/> names it. However little of
    /// the history a shallow clone holds, the commit naming a parent holds it, whatever was cut away behind. A pull
    /// request's merge commit checked out alone names, as its first parent, the tip of the branch it merges into.
    /// </summary>
    private async Task<string?> ParentNamedByAnotherAsync(
        string directory,
        string first,
        IReadOnlyList<string> others,
        CancellationToken cancellationToken)
    {
        if (await ResolveCommitAsync(directory, first, cancellationToken).ConfigureAwait(false) is not { } firstCommit)
        {
            return null;
        }

        var otherCommits = new List<string>();

        foreach (var other in others)
        {
            if (await ResolveCommitAsync(directory, other, cancellationToken).ConfigureAwait(false) is not { } otherCommit)
            {
                return null;
            }

            if ((await ParentsNamedByAsync(directory, otherCommit, cancellationToken).ConfigureAwait(false)).Contains(firstCommit, StringComparer.Ordinal))
            {
                return firstCommit;
            }

            otherCommits.Add(otherCommit);
        }

        // The other way round against one commit alone: measured against a merge, first may share more with a commit
        // merged in than with the parent it names.
        return otherCommits is [var only]
            && (await ParentsNamedByAsync(directory, firstCommit, cancellationToken).ConfigureAwait(false)).Contains(only, StringComparer.Ordinal)
            ? only
            : null;
    }

    /// <summary>
    /// The parents <paramref name="commit"/>'s own object names, read from the object itself: where a shallow clone cut
    /// the history behind it, every view of the history answers that it has none.
    /// </summary>
    private async Task<IReadOnlyList<string>> ParentsNamedByAsync(string directory, string commit, CancellationToken cancellationToken)
    {
        var result = await RunAsync(directory, ["cat-file", "commit", commit], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read commit {commit}: {result.FailureMessage}");
        }

        // The header ends at the first empty line, before the message, which may hold anything.
        return [.. result.StandardOutput
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .TakeWhile(line => line.Length > 0)
            .Where(line => line.StartsWith("parent ", StringComparison.Ordinal))
            .Select(line => line["parent ".Length..].Trim())];
    }

    public async Task<string?> ReadFileAtCommitAsync(
        string directory,
        string commit,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        // Spelled by this machine, so its own separator is turned into git's; nothing else is, as a
        // backslash is an ordinary character in a name on Linux.
        var path = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var read = await ReadFilesAtCommitAsync(directory, commit, [path], cancellationToken).ConfigureAwait(false);

        return read[path];
    }

    public async Task<IReadOnlyDictionary<string, string?>> ReadFilesAtCommitAsync(
        string directory,
        string commit,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        ArgumentNullException.ThrowIfNull(relativePaths);

        // As git spells them, and keyed as given: rewritten, a name holding a backslash - an ordinary
        // character on Linux - named another file, and its answer was filed under a key the caller
        // never asked for.
        var paths = relativePaths.Distinct(StringComparer.Ordinal).ToList();
        var read = paths.ToDictionary(path => path, _ => (string?)null, StringComparer.Ordinal);

        // Each named to git one to a line, as '<commit>:<path>'; a path holding a line break - which
        // git allows and no line can carry - by the object the commit's listing gives it instead.
        var files = paths.Any(HoldsLineBreak)
            ? await FilesAtCommitAsync(directory, commit, cancellationToken).ConfigureAwait(false)
            : null;

        var asked = paths.Where(path => !HoldsLineBreak(path) || files!.ContainsKey(path)).ToList();

        if (asked.Count > 0)
        {
            var objects = asked.Select(path => HoldsLineBreak(path) ? files![path] : $"{commit}:{path}").ToList();
            var answers = await ReadObjectsAsync(directory, commit, objects, cancellationToken).ConfigureAwait(false);

            foreach (var (path, content) in asked.Zip(answers))
            {
                read[path] = content;
            }
        }

        await RefuseListedButUnreadAsync(directory, commit, asked.Where(path => read[path] is null), files, cancellationToken).ConfigureAwait(false);
        return read;
    }

    /// <summary>
    /// Refuses where git answered "missing" for a path <paramref name="commit"/>'s listing holds: git answers that for a file
    /// whose object it cannot read exactly as it does for a path that names no file, and only the listing tells the two
    /// apart. Passed off as absent, a file nobody could read would pass every check that read nothing in it.
    /// </summary>
    /// <param name="directory">The work tree asked in.</param>
    /// <param name="commit">The commit asked about.</param>
    /// <param name="unanswered">The paths git answered "missing" for.</param>
    /// <param name="files">The commit's listing, where it was read already.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">One of them is listed, and could not be read (<see cref="HarnessExit.CommandFailed"/>).</exception>
    private async Task RefuseListedButUnreadAsync(
        string directory,
        string commit,
        IEnumerable<string> unanswered,
        Dictionary<string, string>? files,
        CancellationToken cancellationToken)
    {
        var missing = unanswered.ToList();

        if (missing.Count == 0)
        {
            return;
        }

        files ??= await FilesAtCommitAsync(directory, commit, cancellationToken).ConfigureAwait(false);

        if (missing.FirstOrDefault(files.ContainsKey) is { } unread)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git lists '{unread}' at {commit} but could not read it. Check the repository with 'git fsck'.");
        }
    }

    public async Task<IReadOnlyList<GitName>> ListFilesAtCommitAsync(
        string directory,
        string commit,
        CancellationToken cancellationToken = default)
        => [.. (await ListTreeAsync(directory, commit, cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.IsFile)
            .Select(entry => entry.Name)];

    public async Task<IReadOnlyList<GitName>> ListNamesAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await RunForBytesAsync(directory, arguments, cancellationToken).ConfigureAwait(false);

        Ensure(result, $"run git {string.Join(' ', arguments)}");

        return [.. Records(result.StandardOutput).Select(GitName.FromBytes)];
    }

    /// <summary>
    /// Every entry of <paramref name="commit"/>'s tree, from the repository's root, as
    /// <c>git ls-tree -r</c> lists it.
    /// </summary>
    private async Task<IReadOnlyList<TreeEntry>> ListTreeAsync(
        string directory,
        string commit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        var result = await RunForBytesAsync(directory, ["ls-tree", "-r", "-z", "--full-tree", commit], cancellationToken)
            .ConfigureAwait(false);

        Ensure(result, $"list the files at {commit}");

        return TreeEntries(result.StandardOutput);
    }

    /// <summary>The entries <c>git ls-tree -z</c> listed, read as bytes: one reading of them, whichever listing asked.</summary>
    /// <exception cref="HarnessException">git listed an entry in a form this build cannot read.</exception>
    private static List<TreeEntry> TreeEntries(string output)
    {
        // '<mode> <type> <object>TAB<name>': the name is everything after the first tab, so one
        // holding a tab keeps it.
        return [.. Records(output).Select(entry =>
            entry.IndexOf('\t', StringComparison.Ordinal) is var tab and > 0
                && entry[..tab].Split(' ') is [var mode, var type, var objectId]
                ? new TreeEntry(mode, type, objectId, GitName.FromBytes(entry[(tab + 1)..]))
                : throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git listed a tree entry in a form this build cannot read: '{GitName.FromBytes(entry).Quoted}'"))];
    }

    /// <summary>
    /// Each file at <paramref name="commit"/> whose name is UTF-8 - so one a caller can ask for - with
    /// the object git holds its bytes in.
    /// </summary>
    private async Task<Dictionary<string, string>> FilesAtCommitAsync(
        string directory,
        string commit,
        CancellationToken cancellationToken)
        => FilesIn(await ListTreeAsync(directory, commit, cancellationToken).ConfigureAwait(false));

    /// <summary>Each file <paramref name="tree"/> lists whose name is UTF-8, with the object git holds its bytes in.</summary>
    private static Dictionary<string, string> FilesIn(IReadOnlyList<TreeEntry> tree)
        => tree
            .Where(entry => entry.IsFile && entry.Name.IsUtf8)
            .ToDictionary(entry => entry.Name.Text, entry => entry.ObjectId, StringComparer.Ordinal);

    private static bool HoldsLineBreak(string path) => path.IndexOfAny(['\n', '\r']) >= 0;

    /// <summary>What git separated with NUL - its <c>-z</c> form - one record to an element.</summary>
    private static string[] Records(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Runs git with its output read as Latin-1, which carries each byte as one character: for an
    /// answer that is not all text, or whose names are not all UTF-8.
    /// </summary>
    private Task<GitCommandResult> RunForBytesAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        ChildInput? standardInput = null,
        ObjectStore? objects = null)
        => RunCoreAsync(
            directory,
            arguments,
            echoOutput: false,
            untranslated: false,
            indexFile: null,
            standardInput,
            cancellationToken,
            Encoding.Latin1,
            objects);

    /// <summary>
    /// The objects <paramref name="objects"/> names, in order, read by one <c>git cat-file --batch</c>:
    /// each file's text - its bytes, decoded - or <see langword="null"/> for a name that is not a file git could read.
    /// </summary>
    /// <remarks>
    /// The commit is asked about first, in the same process: git answers "missing" for a commit it
    /// cannot read exactly as it does for a file that was not there, and an unreadable commit passed
    /// off as absent files would pass a check that read nothing. Read as bytes, so the answer is cut
    /// by the byte counts git gives, and each file is then decoded as its own text.
    /// </remarks>
    private async Task<IReadOnlyList<string?>> ReadObjectsAsync(
        string directory,
        string commit,
        IReadOnlyList<string> objects,
        CancellationToken cancellationToken)
    {
        var answers = await ReadBatchAsync(directory, [$"{commit}^{{commit}}", .. objects], $"read {ReportText.Counted(objects.Count, "file")} at {commit}", store: null, cancellationToken)
            .ConfigureAwait(false);

        if (answers[0] is not { Type: "commit" })
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read {ReportText.Counted(objects.Count, "file")} at {commit}: git cannot read that commit.");
        }

        return [.. answers.Skip(1).Select(answer => answer is { Type: "blob" } blob ? Decoded(blob.Content) : null)];
    }

    /// <summary>
    /// What one <c>git cat-file --batch</c> answers for <paramref name="names"/>, in order: each object's type and its
    /// bytes, carried one to a character, or <see langword="null"/> for a name that names nothing git can read.
    /// </summary>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="names">Names git reads as objects, none holding a line break.</param>
    /// <param name="what">What was being read, for the failure.</param>
    /// <param name="store">An object directory of the question's own to read from besides; the repository's alone where null.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    private async Task<IReadOnlyList<(string Type, string Content)?>> ReadBatchAsync(
        string directory,
        IReadOnlyList<string> names,
        string what,
        ObjectStore? store,
        CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return [];
        }

        var result = await RunForBytesAsync(directory, ["cat-file", "--batch"], cancellationToken, string.Join('\n', names) + '\n', store)
            .ConfigureAwait(false);

        Ensure(result, what);

        var answers = new BatchAnswers(result.StandardOutput);

        return [.. names.Select(_ => answers.Next())];
    }

    /// <summary>A file's bytes, carried one to a character, as the text they are.</summary>
    /// <remarks>
    /// Read as a file on disk is read: a byte order mark says which encoding the rest is in - UTF-16,
    /// as a Windows PowerShell 5.1 redirect writes it - and is not part of the text, and UTF-8 is
    /// assumed where there is none. Read as UTF-8 regardless, a UTF-16 file became every other
    /// character a NUL, was taken for binary and skipped, and the commit passed a check that the
    /// same file on disk failed.
    /// </remarks>
    private static string Decoded(string bytes)
    {
        using var reader = new StreamReader(
            new MemoryStream(Encoding.Latin1.GetBytes(bytes)),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads git's batch answers in order: for each object asked about, its header, then as many
    /// bytes as the header names, then a line break; or one line saying it names nothing.
    /// </summary>
    private sealed class BatchAnswers(string output)
    {
        private int _position;

        /// <summary>The next object's type and bytes, or <see langword="null"/> when it names nothing.</summary>
        /// <exception cref="HarnessException">git answered for fewer objects, or with fewer bytes, than it said.</exception>
        public (string Type, string Content)? Next()
        {
            var end = output.IndexOf('\n', _position);

            if (end < 0)
            {
                throw Short();
            }

            var header = output[_position..end].Split(' ');
            _position = end + 1;

            // '<object id> <type> <size>'; anything else - '<name> missing', '<name> ambiguous' -
            // names nothing, and has no content after it.
            if (header is not [var id, var type, var bytes]
                || !id.All(char.IsAsciiHexDigit)
                || !int.TryParse(bytes, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                return null;
            }

            if (_position + size >= output.Length)
            {
                throw Short();
            }

            var content = output.Substring(_position, size);
            _position += size + 1;

            return (type, content);
        }

        private static HarnessException Short()
            => new(HarnessExit.CommandFailed, "git cat-file answered with less than it said it would.");
    }

    /// <summary>One entry of a commit's tree, as <c>git ls-tree</c> lists it.</summary>
    /// <param name="Mode">The octal mode, such as 100644 for a file, 100755 for an executable one or 120000 for a link.</param>
    /// <param name="Type">What the entry is: <c>blob</c>, <c>tree</c>, or <c>commit</c> for a submodule's.</param>
    /// <param name="ObjectId">The object git holds the entry in.</param>
    /// <param name="Name">The entry's path, from the repository's root.</param>
    private sealed record TreeEntry(string Mode, string Type, string ObjectId, GitName Name)
    {
        /// <summary>
        /// Whether the entry is a file - a symbolic link is one, its text the path it points at -
        /// rather than a directory, or a submodule's entry, which names a commit in another repository.
        /// </summary>
        public bool IsFile => Type == "blob";
    }

    public Task<GitCommandResult> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput = false,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(directory, arguments, echoOutput, untranslated: false, cancellationToken);

    /// <summary>
    /// Runs a read-only query whose failure message is inspected, with git's messages
    /// untranslated.
    /// </summary>
    /// <remarks>
    /// "not a git repository" (<see cref="NotARepositoryAnswers"/>) and "not a gitdir" are recognised by their text, and a translated
    /// git answers in another language, so these queries run under <c>LC_ALL=C</c>. The override is kept
    /// to them alone: every other git command, including those that run the user's hooks,
    /// keeps the user's locale, because forcing C onto a hook changes how it handles
    /// anything outside ASCII.
    /// </remarks>
    private Task<GitCommandResult> RunQueryAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => RunCoreAsync(directory, arguments, echoOutput: false, untranslated: true, cancellationToken);

    private Task<GitCommandResult> RunCoreAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput,
        bool untranslated,
        CancellationToken cancellationToken)
        => RunCoreAsync(directory, arguments, echoOutput, untranslated, indexFile: null, standardInput: null, cancellationToken);

    /// <summary>
    /// Runs git against <paramref name="indexFile"/> instead of the work tree's own index: a copy
    /// made to be changed, so the real index is never touched.
    /// </summary>
    private Task<GitCommandResult> RunWithIndexAsync(
        string directory,
        string indexFile,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
        => RunCoreAsync(directory, arguments, echoOutput: false, untranslated: false, indexFile, standardInput, cancellationToken);

    private async Task<GitCommandResult> RunCoreAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput,
        bool untranslated,
        string? indexFile,
        ChildInput? standardInput,
        CancellationToken cancellationToken,
        Encoding? outputEncoding = null,
        ObjectStore? objects = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Without this, an operation that decides to ask for credentials blocks
            // forever with no output. A hang is the one failure this tool cannot
            // report, so git is told up front that nobody is watching.
            ["GIT_TERMINAL_PROMPT"] = "0",
        };

        // Cleared before every question, not only before every change. Each outranks the repository,
        // the working tree or the index that `-C <directory>` would otherwise select, so a hook, or a
        // command started from another checkout, steers every answer git gives: the harness would then
        // read one tree's status and act on another's. A caller that genuinely wants a different index
        // passes it below, after the inherited one is gone.
        await _localVariables.ClearAsync(environment, cancellationToken).ConfigureAwait(false);

        if (untranslated)
        {
            environment["LC_ALL"] = "C";
        }

        // An index of the tool's own is never one git leaves files unexamined in: under core.ignoreStat git marks each entry
        // it writes as unchanged, and every comparison made with it would then read an edited file as its entry's blob.
        if (indexFile is not null)
        {
            environment["GIT_INDEX_FILE"] = indexFile;
            arguments = ["-c", "core.ignoreStat=false", .. arguments];
        }

        // As the index is: an object directory a caller asks for is set once the inherited one is gone. What git writes
        // goes there, and the repository's own objects are still read, beside it.
        if (objects is not null)
        {
            environment["GIT_OBJECT_DIRECTORY"] = objects.Directory;
            environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = objects.AlternateList;
        }

        var request = new ProcessRequest
        {
            FileName = GitExecutable,
            Arguments = arguments,
            WorkingDirectory = directory,
            OnOutputLine = echoOutput ? _output.Raw : null,
            OnErrorLine = echoOutput ? _output.RawError : null,
            Environment = environment,
            StandardInput = standardInput,
            StandardOutputEncoding = outputEncoding,
        };

        var result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);

        return new GitCommandResult(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError,
            result.TimedOut);
    }

    /// <summary>Parses <c>git worktree list --porcelain</c>, whose first entry is the main worktree.</summary>
    private static List<GitWorktree> ParseWorktrees(string output)
    {
        var worktrees = new List<GitWorktree>();
        string? path = null;
        string? commit = null;
        string? branch = null;
        var bare = false;
        string? lockReason = null;

        void Flush()
        {
            if (path is null)
            {
                return;
            }

            worktrees.Add(new GitWorktree(
                NormalizeDirectory(path),
                commit,
                branch,
                IsMain: worktrees.Count == 0,
                IsBare: bare)
            {
                LockReason = lockReason,
            });

            path = null;
            commit = null;
            branch = null;
            bare = false;
            lockReason = null;
        }

        foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.Length == 0)
            {
                Flush();
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                // An unborn HEAD, as on an orphan branch, is listed as the null object id. It names no
                // commit, and handed to git as one it fails every command it reaches.
                var head = line["HEAD ".Length..];
                commit = head.All(character => character == '0') ? null : head;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                // git reports a full ref; callers want the branch name they would type.
                branch = StripRefPrefix(line["branch ".Length..]);
            }
            else if (string.Equals(line, "bare", StringComparison.Ordinal))
            {
                bare = true;
            }
            else if (string.Equals(line, "locked", StringComparison.Ordinal))
            {
                lockReason = string.Empty;
            }
            else if (line.StartsWith("locked ", StringComparison.Ordinal))
            {
                lockReason = line["locked ".Length..];
            }
        }

        Flush();
        return worktrees;
    }

    /// <summary>
    /// The changes <c>git status --porcelain -z</c> printed, read as bytes one to a character: the one reading of its
    /// records, for every status question asked.
    /// </summary>
    /// <param name="output">What git printed.</param>
    /// <exception cref="HarnessException">A record is in a form this build cannot read.</exception>
    private static List<GitStatusEntry> ParseStatus(string output)
    {
        var fields = Records(output);
        var entries = new List<GitStatusEntry>();

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];

            if (field.Length < 4 || field[2] != ' ')
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git named a changed path in a form this build cannot read: '{GitName.FromBytes(field).Quoted}'");
            }

            var entry = new GitStatusEntry(field[0], field[1], GitName.FromBytes(field[3..]), Source: null);

            // A rename or copy is one change encoded as two NUL separated fields: the status with the new path, then the
            // original path. Treating the second field as another entry would report one rename as two changes, and the
            // second would have no status prefix at all. Either status column can mark one: a staged rename is marked in
            // the first, and a rename git finds in the work tree, such as that of an intent-to-add file, in the second.
            if (entry.IsRenameOrCopy)
            {
                if (++index >= fields.Length)
                {
                    throw new HarnessException(HarnessExit.CommandFailed, $"git named a rename of '{entry.Path.Quoted}' with no path it was renamed from.");
                }

                entry = entry with { Source = GitName.FromBytes(fields[index]) };
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Options that aim git at a git directory alone. The work tree is pointed at the git directory
    /// as well, because a submodule's repository names its checkout in core.worktree, and git stops
    /// when that checkout is gone, even for a question that never reads it.
    /// </summary>
    private static string[] RepositoryOnly(string gitDirectory)
        => [$"--git-dir={gitDirectory}", $"--work-tree={gitDirectory}"];

    /// <summary>Reads the count <c>git rev-list --count</c> printed, or throws when git failed.</summary>
    private static int ReadCount(GitCommandResult result)
    {
        Ensure(result, "count commits");

        var text = result.StandardOutput.Trim();

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git counted commits in a form this build cannot read: '{text}'");
    }

    /// <summary>
    /// The version the git this client runs names itself by, as <c>git version</c> gives it - "2.47.1.windows.1" - and the
    /// release its first two numbers make, where they make one, its patch read as 0 - a git built from its own sources
    /// may name itself "2.56.GIT": asked of git once, and the answer kept.
    /// </summary>
    /// <exception cref="HarnessException">git could not say its version.</exception>
    private async Task<(string Named, SemanticVersion? Release)> InstalledVersionAsync(string directory, CancellationToken cancellationToken)
    {
        const string Answer = "git version ";

        if (_installed is null)
        {
            var result = await RunQueryAsync(directory, ["version"], cancellationToken).ConfigureAwait(false);

            Ensure(result, "read which git is installed");

            var line = result.OutputLines.FirstOrDefault() ?? string.Empty;
            _installed = line.StartsWith(Answer, StringComparison.Ordinal) ? line[Answer.Length..].Split(' ')[0] : line.Trim();
        }

        var numbers = _installed.Split('.');

        return (_installed, numbers.Length >= 2 && SemanticVersion.TryParse($"{numbers[0]}.{numbers[1]}.0", out var release) ? release : null);
    }

    /// <summary>
    /// Throws when git failed, carrying git's own message. Used where an empty
    /// result would be indistinguishable from a legitimate empty answer.
    /// </summary>
    private static void Ensure(GitCommandResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not {what}: {result.FailureMessage}");
        }
    }

    /// <summary>
    /// Distinguishes git's "not a repository" answer from a genuine failure to run.
    /// git reports the former on stderr in a recognisable form (<see cref="NotARepositoryAnswers"/>); everything else
    /// (dubious ownership, a corrupt config, an unreadable object store, a timeout) is a
    /// failure whose own message tells the user what to do.
    /// </summary>
    private static void EnsureNotAnError(GitCommandResult result, string directory)
    {
        if (!result.TimedOut
            && NotARepositoryAnswers.Any(answer => result.StandardError.Contains(answer, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new HarnessException(
            HarnessExit.CommandFailed,
            $"git could not inspect '{directory}': {result.FailureMessage}");
    }

    /// <summary>Reduces a full ref to the branch name a person would type.</summary>
    private static string StripRefPrefix(string reference)
        => reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : reference;

    /// <summary>
    /// Renders a directory path in the platform's own separator form with no trailing
    /// separator, so paths from git (which always answers with forward slashes) compare
    /// equal to paths the harness built from them.
    /// </summary>
    private static string NormalizeDirectory(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
