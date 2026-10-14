using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Orchestration;

/// <summary>
/// What folding an agent does, measured: every path its worktree changes and every path it shares with the main tree,
/// each in exactly one list.
/// </summary>
public sealed record FoldPlan
{
    /// <summary>The agent's own work: written into the main tree.</summary>
    public required IReadOnlyList<string> Written { get; init; }

    /// <summary>The paths it deleted: removed from the main tree.</summary>
    public required IReadOnlyList<string> Deleted { get; init; }

    /// <summary>The paths it shares with the main tree and left as they were: not its work, and never written back.</summary>
    public required IReadOnlyList<string> Inherited { get; init; }

    /// <summary>The paths the main tree already holds as the agent does, absent from both included: nothing to write.</summary>
    public required IReadOnlyList<string> AlreadyIn { get; init; }

    /// <summary>
    /// The paths declared settled by hand where the main tree holds other than what the agent last shared with it - the
    /// reconciliation: what the main tree holds there handed to the agent once everything else is written - each deletion
    /// made, then each file copied - and recorded as shared, so its next fold starts from it.
    /// </summary>
    public required Handable Handed { get; init; }

    /// <summary>
    /// The paths declared settled by hand that a fold never reads or writes - reached through a link, or a directory git
    /// will not look into, in either tree - each with why: left out, and not handed.
    /// </summary>
    public required IReadOnlyList<SettledOut> LeftOut { get; init; }

    /// <summary>
    /// The paths both trees changed whose three-way merge is clean and was asked for: the merged bytes are written into
    /// both trees, and recorded as what they share.
    /// </summary>
    public required IReadOnlyList<MergedPath> Merged { get; init; }

    /// <summary>Why the fold cannot be applied, a line for each path; while there is one, nothing is written.</summary>
    public required IReadOnlyList<string> Refusals { get; init; }

    /// <summary>The conflicts of each refused path whose merge conflicts, as a block printed under the refusals.</summary>
    public required IReadOnlyList<string> Shown { get; init; }

    /// <summary>The refused paths whose three-way merge is clean and was not asked for: what naming each to be merged would write.</summary>
    public required IReadOnlyList<string> Mergeable { get; init; }

    /// <summary>The paths the agent shares with the main tree: handed to it, or written or removed by an earlier fold.</summary>
    public required IReadOnlySet<string> Seeded { get; init; }

    /// <summary>
    /// What each written path, each merged, each already in the main tree and each handed held in the agent's worktree when
    /// it was measured - the file it was, its digest and whether it could be run, or null where it held no file: what is
    /// written, what a merge or a hand-over is written over or removed, and what both trees are recorded as sharing once a
    /// written path is. A handed path where the agent holds anything else is not in it (<see cref="AgentFold.HoldingAsync"/>).
    /// </summary>
    public required IReadOnlyDictionary<string, FileIdentity?> AgentContent { get; init; }

    /// <summary>
    /// What each written, each merged and each deleted path held in the main tree when it was measured - its digest, or
    /// null where it held no file: what a write replaces and a removal removes, and nothing else.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> MainContent { get; init; }

    /// <summary>Whether nothing is left to write, merge, delete, hand or refuse: what deleting an agent requires once its fold is in.</summary>
    public bool NothingLeft => Written.Count == 0 && Merged.Count == 0 && Deleted.Count == 0 && Handed.Count == 0 && Refusals.Count == 0;
}

/// <summary>A path declared settled by hand that a fold leaves out, and why.</summary>
/// <param name="Path">The path, as git names it.</param>
/// <param name="Why">What it is that a fold never reads or writes, to stand in brackets.</param>
public sealed record SettledOut(string Path, string Why);

/// <summary>A path a hand-over would write into or remove from a submodule of the agent's worktree, and that submodule.</summary>
/// <param name="Path">The path, as git names it.</param>
/// <param name="Submodule">The submodule's path: the path itself, or the one it lies in.</param>
public sealed record SubmodulePath(string Path, string Submodule);

/// <summary>A path handed to an agent, and the file it was handed.</summary>
/// <param name="Path">The path, as git names it.</param>
/// <param name="Digest">The SHA-256 of the file copied; null for a deletion.</param>
public sealed record HandedPath(string Path, string? Digest);

/// <summary>What applying a fold did.</summary>
/// <param name="Written">The paths it wrote into the main tree, in order.</param>
/// <param name="Deleted">The paths it removed from it, in order.</param>
/// <param name="Merged">The paths it merged, in order: each once both trees hold the merge.</param>
/// <param name="Handed">The paths settled by hand it handed to the agent, in order.</param>
/// <param name="Stopped">Why it stopped part way; null where it wrote everything.</param>
public sealed record FoldApplied(IReadOnlyList<string> Written, IReadOnlyList<string> Deleted, IReadOnlyList<string> Merged, IReadOnlyList<HandedPath> Handed, string? Stopped);

/// <summary>
/// What moving an agent's base from one commit to another does to its worktree, measured; nothing written. Every path the
/// two commits hold differently is in exactly one list, or refused.
/// </summary>
public sealed record RebasePlan
{
    /// <summary>The paths the agent holds as its base does, which come into its worktree as the new base holds them.</summary>
    public required IReadOnlyList<string> Taken { get; init; }

    /// <summary>The paths it shares with the main tree - handed to it, or folded - which stay as they are: its seed still says what they are.</summary>
    public required IReadOnlyList<string> Kept { get; init; }

    /// <summary>The paths it holds as the new base does already.</summary>
    public required IReadOnlyList<string> AlreadyThere { get; init; }

    /// <summary>The paths it changed that were declared settled by hand: its copy stays, its own change on the new base.</summary>
    public required IReadOnlyList<string> Settled { get; init; }

    /// <summary>
    /// The paths it changed that the main tree committed a change to, whose three-way merge is clean and was asked for:
    /// written into its worktree as its own change on the new base, and recorded nowhere - its fold weighs them.
    /// </summary>
    public required IReadOnlyList<MergedPath> Merged { get; init; }

    /// <summary>Why its base cannot be moved, path by path; empty where it can.</summary>
    public required IReadOnlyList<string> Refusals { get; init; }

    /// <summary>The conflicts of each refused path whose merge conflicts, as a block printed under the refusals.</summary>
    public required IReadOnlyList<string> Shown { get; init; }

    /// <summary>The refused paths whose three-way merge is clean and was not asked for: what naming each to be merged would write.</summary>
    public required IReadOnlyList<string> Mergeable { get; init; }

    /// <summary>
    /// What git is asked to write first as the new base holds it: every taken path but those in <see cref="WrittenLast"/>,
    /// what was below them among it, which git removes with the directories that leaves empty.
    /// </summary>
    public required IReadOnlyList<string> Written { get; init; }

    /// <summary>
    /// What git is asked to write once <see cref="Written"/> is: each taken path the new base holds as a file or a
    /// submodule's entry where the old base held a directory. Asked for at once with what was below it, git finds that gone
    /// and stops; a submodule's entry written first leaves what was below it in place, hidden from git.
    /// </summary>
    public required IReadOnlyList<string> WrittenLast { get; init; }
}

/// <summary>A path whose copy in the agent is the main tree's own at an earlier commit, with that commit.</summary>
/// <param name="Path">The path, as git names it.</param>
/// <param name="Commit">The newest commit of the main tree's history that holds the agent's very bytes there.</param>
public sealed record StaleCopy(string Path, string Commit);

/// <summary>
/// A path the agent changed that a refresh would hand: where its copy is the main tree's own at an earlier commit, that
/// commit; and where both trees hold a file there, what a three-way merge of the two gives.
/// </summary>
/// <param name="Path">The path, as git names it.</param>
/// <param name="StaleAt">The newest commit of the main tree's history that holds its very bytes there; null where none does, or it holds no file.</param>
/// <param name="Merge">What a three-way merge of the agent's copy and the main tree's gives, as a clause; null where none was weighed.</param>
public sealed record RefreshChange(string Path, string? StaleAt, string? Merge);

/// <summary>A merge a refresh writes into the agent's worktree, and what the main tree held there: what the path is recorded as shared at.</summary>
/// <param name="Merge">The merge, and the agent's copy it is written over.</param>
/// <param name="Main">What the main tree held at the path when it was weighed.</param>
public sealed record RefreshMerge(MergedPath Merge, FileIdentity Main);

/// <summary>
/// What refreshing an agent does, measured; nothing written. Every path under the paths asked for that the main tree holds
/// otherwise than the agent shares it is in exactly one list: handed - a stale copy named with
/// <see cref="RefreshRequest.StaleOption"/> among them - already held as the main tree does, left by name as the agent
/// changed it, merged where both changed it and the merge is taken, or changed by the agent - which refuses the refresh.
/// </summary>
public sealed record RefreshPlan
{
    /// <summary>
    /// What is handed: each file the main tree holds there copied, each deletion made, and what is named and not handed -
    /// directories git will not look into, and links the main tree committed.
    /// </summary>
    public required Handable Handed { get; init; }

    /// <summary>
    /// The paths the agent already holds as the main tree does - the same file, or absent from both: recorded as shared, so
    /// its record says what it holds, and nothing copied.
    /// </summary>
    public required IReadOnlyList<string> Alike { get; init; }

    /// <summary>The paths it changed that were named to be left as it changed them: handed nothing.</summary>
    public required IReadOnlyList<string> Left { get; init; }

    /// <summary>
    /// The paths it changed that nothing named: handing the main tree's over them would undo its work, so the refresh is
    /// refused - each with the commit its copy is the main tree's own at, where it is one, and what a merge of it gives.
    /// </summary>
    public required IReadOnlyList<RefreshChange> Changed { get; init; }

    /// <summary>
    /// The paths both trees changed whose three-way merge is clean and is taken - named with
    /// <see cref="FoldAllowances.MergeOption"/>, or the arm registry, merged by arm unasked where it reads as a registry of
    /// the worktree the refresh leaves: the merge written into the agent's worktree as its change, and the path recorded as
    /// shared at the main tree's bytes, which its copy descends from.
    /// </summary>
    public required IReadOnlyList<RefreshMerge> Merged { get; init; }

    /// <summary>The conflicts of each changed path whose merge conflicts, as a block printed under them.</summary>
    public required IReadOnlyList<string> Shown { get; init; }

    /// <summary>The changed paths whose three-way merge is clean and was not asked for: what naming each to be merged would write.</summary>
    public required IReadOnlyList<string> Mergeable { get; init; }

    /// <summary>The paths named with <see cref="FoldAllowances.MergeOption"/> outside the paths weighed: a misspelling, most likely.</summary>
    public required IReadOnlyList<string> MergeStray { get; init; }

    /// <summary>
    /// The paths named with <see cref="RefreshRequest.StaleOption"/> whose copy is proved the main tree's own at an earlier
    /// commit: handed with the rest, in <see cref="Handed"/>, and listed apart.
    /// </summary>
    public required IReadOnlyList<StaleCopy> Stale { get; init; }

    /// <summary>Why each path named with <see cref="RefreshRequest.StaleOption"/> that is not proved stale is refused, each a sentence.</summary>
    public required IReadOnlyList<string> StaleRefused { get; init; }

    /// <summary>The paths named with <see cref="RefreshRequest.StaleOption"/> that are not under the paths weighed, or never handed: a misspelling, most likely.</summary>
    public required IReadOnlyList<string> StaleStray { get; init; }

    /// <summary>Whether the search of the main tree's history stopped at its bound with more history left: a copy older than that is not found stale.</summary>
    public required bool Capped { get; init; }

    /// <summary>What of the agent's own handing <see cref="Handed"/> would write over or through, which refuses the refresh.</summary>
    public required IReadOnlyList<string> InTheWay { get; init; }

    /// <summary>
    /// Each path handed, or already alike and to be recorded, that is or lies in a submodule the agent's worktree records -
    /// another repository, which a hand-over never writes into or removes from - which refuses the refresh.
    /// </summary>
    public required IReadOnlyList<SubmodulePath> InSubmodules { get; init; }

    /// <summary>
    /// What the agent's worktree held at each path handed, and at each it already holds as the main tree does, when it was
    /// measured - the file whole, its digest and its bit, or null where it held none: what a hand-over may write over or
    /// remove, and nothing else (<see cref="AgentFold.HandAsync"/>); and what is recorded as shared for a path already
    /// alike (<see cref="AgentFold.AlikeAsync"/>).
    /// </summary>
    public required IReadOnlyDictionary<string, FileIdentity?> Held { get; init; }
}

/// <summary>What the main tree hands an agent: each file copied, each deletion made, and what it cannot hand.</summary>
/// <param name="Files">The changed files, each copied into the agent's worktree.</param>
/// <param name="Deletions">
/// The paths the main tree deleted, or turned from a file into a directory, each removed from the agent's worktree before
/// any file is copied - what such a directory holds is handed on paths of its own.
/// </param>
/// <param name="Directories">
/// The directories git will not look into - repositories of their own, and submodules - which a fold never moves: named,
/// and not handed.
/// </param>
/// <param name="Links">
/// The symbolic links the main tree committed since the agent's base, which are never handed as the files they lead to:
/// named, and not handed; moving the agent's base brings each in as git holds it. One not committed refuses the hand-over.
/// </param>
public sealed record Handable(IReadOnlyList<string> Files, IReadOnlyList<string> Deletions, IReadOnlyList<string> Directories, IReadOnlyList<string> Links)
{
    /// <summary>A hand-over of nothing.</summary>
    public static Handable None { get; } = new([], [], [], []);

    /// <summary>How many paths are handed over: files and deletions.</summary>
    public int Count => Files.Count + Deletions.Count;
}

/// <summary>
/// What an agent shares with the main tree, as one command adds to it: the one recorder of a shared path, whichever
/// command shares it - handed, folded, or found already in. Each file recorded has its bytes kept beside the seed
/// (<see cref="SharedCopies"/>), so the seed and the copies it names are written from one value - by a command that
/// deletes the agent too, which may stop after its fold and leave the agent live, with paths to merge from them.
/// </summary>
/// <remarks>
/// A command writes in one order: the trees, the kept copies - made here, as each path is recorded - the seed, and then
/// the prune of the copies to what that seed names. Stopped anywhere, the seed never names a path the trees do not hold
/// as recorded, and a copy the seed does not name is pruned at the next write.
/// </remarks>
/// <param name="seed">What the agent shared before.</param>
/// <param name="kept">Where the bytes of what it shares are kept.</param>
internal sealed class SharedLedger(SeedRecord seed, SharedCopies kept)
{
    private readonly Dictionary<string, string> _shared = new(seed.Paths, StringComparer.Ordinal);
    private readonly HashSet<string> _executable = (seed.Executable ?? []).ToHashSet(StringComparer.Ordinal);
    private readonly HashSet<string> _absent = (seed.Absent ?? []).ToHashSet(StringComparer.Ordinal);
    private readonly List<(string Path, string Why)> _notKept = [];

    /// <summary>Where the bytes of what the agent shares are kept, pruned to the seed once it is written.</summary>
    public SharedCopies Kept => kept;

    /// <summary>
    /// What is said of the paths recorded whose bytes could not be kept: never a failure of the command that recorded them,
    /// and one line however many there are - which paths, as many as a line names, how many more, and why - since a full
    /// disk would otherwise say it for every file a command records.
    /// </summary>
    public IReadOnlyList<string> NotKept => ReportText.FailedInOneLine(
        _notKept,
        "path",
        (path, why) => $"'{ReportText.Printable(path)}' is recorded as shared, and its bytes were not kept in '{kept.Directory}': {why}",
        (counted, paths, why) => $"{counted} are recorded as shared, and their bytes were not kept in '{kept.Directory}' - {paths}: {why}");

    /// <summary>
    /// Records <paramref name="path"/> as a file both trees hold, and keeps its bytes. Never interrupted: a path the
    /// trees already hold as recorded is recorded.
    /// </summary>
    /// <param name="path">The path, relative to both trees.</param>
    /// <param name="digest">The SHA-256 of the file both hold.</param>
    /// <param name="executable">Whether it can be run, where the platform has such a bit.</param>
    /// <param name="holding">One tree's copy of it, which the bytes are kept from.</param>
    public async Task FileAsync(string path, string digest, bool executable, string holding)
    {
        _shared[path] = digest;
        _absent.Remove(path);

        if (executable)
        {
            _executable.Add(path);
        }
        else
        {
            _executable.Remove(path);
        }

        if (await kept.KeepAsync(digest, holding, CancellationToken.None).ConfigureAwait(false) is { } why)
        {
            _notKept.Add((path, why));
        }
    }

    /// <summary>Records <paramref name="path"/> as absent from both trees.</summary>
    /// <param name="path">The path, relative to both trees.</param>
    public void Absent(string path)
    {
        _shared.Remove(path);
        _executable.Remove(path);
        _absent.Add(path);
    }

    /// <summary>The seed with everything recorded: handed nothing only where it still names nothing.</summary>
    public SeedRecord Build()
        => seed with
        {
            Paths = new Dictionary<string, string>(_shared, StringComparer.Ordinal),
            Empty = seed.Empty && _shared.Count == 0 && _absent.Count == 0,
            Executable = _executable.Count > 0 ? [.. _executable.Order(StringComparer.Ordinal)] : null,
            Absent = _absent.Count > 0 ? [.. _absent.Order(StringComparer.Ordinal)] : null,
        };
}

/// <summary>
/// Measures and applies an agent's fold: what the agent changed, told apart from what it shares with the main tree and
/// from what the main tree did meanwhile, and written into the main tree only where nothing can be lost.
/// </summary>
/// <remarks>
/// An agent's contribution is a measurement, never an assumption: its worktree's status and every path it shares with the
/// main tree, less the shared paths it left as they were. A path it shares - handed to it, or written or removed by an
/// earlier fold - is compared with what both trees held then; any other with the blob at the agent's own base, never the
/// main tree's HEAD, which a sibling's committed fold moves, and as git status compares, so a line-ending conversion is
/// not taken for a change. A file is its content and, where the platform has one, its execute bit. Every refusal is
/// collected before anything is written, and any one refuses the whole fold: a fold that wrote nine files and refused the
/// tenth would leave a tree nobody can reason about.
/// </remarks>
/// <param name="gitClient">Reads the worktree's status and the blobs to measure against.</param>
/// <param name="fileSystem">Reads both trees, and writes the main one.</param>
/// <param name="permissions">Reads a file's execute bit.</param>
/// <param name="platform">Whether files have an execute bit here, and how paths compare.</param>
internal sealed class AgentFold(IGitClient gitClient, IFileSystem fileSystem, IFilePermissions permissions, IHostPlatform platform)
{
    /// <summary>What cannot be done with a path the main tree changes that no record can keep, to end a sentence.</summary>
    private const string HandingConsequence = "it cannot be handed to an agent";

    /// <summary>The main tree, as a write that stopped names the tree whose file is not what was measured.</summary>
    private const string MainTreeName = "the main tree";

    /// <summary>The agent's worktree, as such a stop names it.</summary>
    private const string AgentTreeName = "the agent's worktree";

    /// <summary>A directory in the main tree that a fold never reads or writes, as a refusal and a left-out name it.</summary>
    private const string MainDirectory = "a directory in the main tree - a submodule, or a repository of its own";

    /// <summary>A directory in the agent's worktree that a fold never reads or writes, as a refusal and a left-out name it.</summary>
    private const string AgentDirectory = "a directory in the agent's worktree - a submodule, or a repository of its own";

    /// <summary>What writes a merged arm registry into the main tree, as a refusal of the merged text names it.</summary>
    private const string FoldWriter = "the fold";

    /// <summary>What writes one into the agent's worktree on a move of its base.</summary>
    private const string RebaseWriter = "the rebase";

    /// <summary>What writes one into the agent's worktree as a refresh hands it what the main tree moved.</summary>
    private const string RefreshWriter = "the refresh";

    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFilePermissions _permissions = permissions;
    private readonly IHostPlatform _platform = platform;

    /// <summary>Measures folding the agent whose worktree is <paramref name="worktree"/> into <paramref name="main"/>; writes nothing.</summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree, which must be its own registered worktree.</param>
    /// <param name="baseCommit">The commit its worktree was made from, which its HEAD must still be.</param>
    /// <param name="seed">What it shares with the main tree.</param>
    /// <param name="settled">The paths declared settled by hand: reconciled in the main tree, whose state there the agent is handed.</param>
    /// <param name="policy">How a path both trees changed is resolved, the paths named to be merged among it.</param>
    /// <param name="changes">Measures what a three-way merge of such a path gives.</param>
    /// <param name="floor">The paths never moved between trees (<see cref="TreeFloor"/>).</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <exception cref="HarnessException">git could not answer, or a changed path is not one a record can keep.</exception>
    public async Task<FoldPlan> MeasureAsync(
        string main,
        string worktree,
        string baseCommit,
        SeedRecord seed,
        IReadOnlyCollection<string> settled,
        TwoChangePolicy policy,
        TwoChanges changes,
        IReadOnlyList<string> floor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(changes);

        var candidates = await CandidatesAsync(worktree, seed, floor, "what the agent did with it cannot be measured", cancellationToken).ConfigureAwait(false);
        var settledPaths = settled.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal);
        var shared = new Baselines(seed);
        var roots = new Roots(main, _fileSystem.ResolveLinks(main), worktree, _fileSystem.ResolveLinks(worktree));
        var looks = new List<Look>(candidates.Count);

        foreach (var path in candidates)
        {
            looks.Add(await LookAsync(path, roots, cancellationToken).ConfigureAwait(false));
        }

        // Every path the agent shares nothing for is weighed against its base: the blob there, one git process for them
        // all, and whether the main tree moved from it since - asked of git once, compared as git status compares.
        var unseeded = looks.Where(look => look.Link is null && shared.Of(look.Path) is null).ToList();
        var atBase = await _gitClient.HeldAtAsync(worktree, baseCommit, [.. unseeded.Select(look => look.Path)], cancellationToken).ConfigureAwait(false);
        var baseBlobs = atBase.ToDictionary(held => held.Key, held => held.Value.Blob, StringComparer.Ordinal);
        var answers = new MainTreeAnswers(_gitClient, main, baseCommit, cancellationToken);

        // A submodule of the main tree's is another repository: nothing a fold writes there, or removes, can be committed in
        // the main tree, and the submodule's own commands lose it. Asked of git once, for every path the fold weighs.
        var inSubmodules = await answers.SubmodulesAsync([.. looks.Select(look => look.Path)]).ConfigureAwait(false);
        IReadOnlySet<string> mainMoved = unseeded.Any(look => baseBlobs[look.Path] is not null)
            ? await answers.MovedFromBaseAsync().ConfigureAwait(false)
            : new HashSet<string>(StringComparer.Ordinal);

        var written = new List<string>();
        var deleted = new List<string>();
        var agentContent = new Dictionary<string, FileIdentity?>(StringComparer.Ordinal);
        var mainContent = new Dictionary<string, string?>(StringComparer.Ordinal);
        var inherited = new List<string>();
        var alreadyIn = new List<string>();
        var settling = new List<string>();
        var leftOut = new List<SettledOut>();
        var refused = new List<(string Path, string Why)>();
        var sinceBase = new List<(Look Look, Since Change)>();
        var twice = new List<(Look Look, string Why, TwoChangeAncestor? Ancestor)>();
        var at = OrchestrationReports.Base(baseCommit);

        foreach (var look in looks)
        {
            var path = look.Path;
            var isSettled = settledPaths.Contains(path);

            // Asked before --settled, which never reaches outside the main tree.
            if (look.Escapes)
            {
                refused.Add((path, $"'{path}' leads out of the main tree, through {look.Link}"));
                continue;
            }

            // What a fold never reads or writes, settled by hand, is left out and not handed: --settled stays the one way out
            // of its refusal.
            if (isSettled && Unread(look) is { } unread)
            {
                leftOut.Add(new SettledOut(path, unread));
                continue;
            }

            if (look.Link is { } link)
            {
                refused.Add((path, $"'{path}' is reached through a symbolic link or junction in {link}, and a fold never reads or writes through one"));
                continue;
            }

            if (look.Agent.Kind == Kind.Directory)
            {
                refused.Add((path, $"'{path}' is {AgentDirectory} - and a fold moves files only"));
                continue;
            }

            var baseline = shared.Of(path);

            // What it shares and still holds as both trees held it is not its work, whatever the main tree did since - but
            // settled by hand, what the main tree did since is the reconciliation, handed below; where the main tree holds it
            // as both did, it is let stand, so the line that ran once runs again.
            if (baseline is { } kept && look.Agent == kept && (!isSettled || look.Main == kept))
            {
                inherited.Add(path);
                continue;
            }

            // Anything else of a submodule's is never written, removed or recorded: refused, or left out where settled by hand.
            if (inSubmodules.TryGetValue(path, out var inSubmodule))
            {
                if (isSettled)
                {
                    leftOut.Add(new SettledOut(path, InMainSubmodule(path, inSubmodule.Gitlink)));
                }
                else
                {
                    refused.Add((path, $"'{path}' {(path == inSubmodule.Gitlink ? "is" : $"lies in '{inSubmodule.Gitlink}',")} a submodule of the main tree's "
                        + $"{(inSubmodule.In == Submodule.Committed ? "made by a commit" : "staged by an uncommitted edit")} - a repository of its own, which a fold "
                        + "never writes into or removes from"));
                }

                continue;
            }

            // What the main tree already holds as the agent does is neither written nor refused: writing it changes
            // nothing, and refusing it would refuse every fold run again after one that stopped part way. Settled by hand
            // too, it is recorded, and nothing is copied.
            if (look.Main == look.Agent)
            {
                alreadyIn.Add(path);
                agentContent[path] = look.Agent.File;
                continue;
            }

            if (isSettled)
            {
                // The main tree's copy is handed where it is not what the agent last shared - or, for a path it shares
                // nothing for, its base's: there is the reconciliation. Where it still is, only the agent changed the path,
                // nothing was reconciled, and handing the main tree's would undo the agent's work.
                var reconciled = baseline is { } ancestor
                    ? look.Main != ancestor
                    : Since.Of(baseBlobs[path], HoldsAt(main, look), mainMoved.Contains(path)) is not null;

                if (reconciled)
                {
                    settling.Add(path);
                }
                else
                {
                    refused.Add((path, NothingReconciled(look, shared: baseline is not null, at)));
                }

                continue;
            }

            if (look.Main.Kind == Kind.Directory)
            {
                refused.Add((path, look.Agent.Kind == Kind.Absent
                    ? $"the agent removed '{path}', which is a directory in the main tree, and a fold removes files only"
                    : $"'{path}' is a directory in the main tree, and the agent holds a file there"));
                continue;
            }

            if (baseline is { } before)
            {
                // A shared path goes in only where the main tree still holds what both trees held: anything else is a change
                // of the main tree's that writing or removing the agent's would lose. Said of when it was last shared - handed
                // to the agent or written by a fold - since the seed records either alike.
                if (look.Main != before)
                {
                    var why = (before.Kind, look.Main.Kind) switch
                    {
                        (Kind.File, Kind.Absent) => $"'{path}' was in both trees when the agent was last handed it or folded, and the main tree no longer holds it",
                        (Kind.Absent, _) => $"'{path}' was absent from both trees when the agent was last handed it or folded, and the main tree holds it now",
                        _ => $"'{path}': the main tree changed it after the agent was last handed it or folded, {Lose(look.Agent.Kind == Kind.Absent, "that change")}",
                    };

                    // Two changes of one file, where both trees hold one: what a merge of them gives is measured, from what
                    // both held - the bytes the seed records, where it records a file.
                    if (BothHoldAFile(look))
                    {
                        twice.Add((look, why, before.File is { } held ? new TwoChangeAncestor.Shared(held.Digest, held.Executable) : null));
                    }
                    else
                    {
                        refused.Add((path, why));
                    }

                    continue;
                }
            }
            else if (baseBlobs[path] is null && look.Agent.Kind == Kind.Absent)
            {
                refused.Add((path, $"the agent deleted '{path}', which its base {at} does not hold, so nothing proves removing it from the main tree safe"));
                continue;
            }
            else if (Since.Of(baseBlobs[path], HoldsAt(main, look), mainMoved.Contains(path)) is { } since)
            {
                // What the main tree did there since the agent's base is weighed below, with how it came to.
                sinceBase.Add((look, since));
                continue;
            }

            mainContent[path] = look.Main.File?.Digest;

            if (look.Agent.Kind == Kind.Absent)
            {
                deleted.Add(path);
            }
            else
            {
                written.Add(path);
                agentContent[path] = look.Agent.File;
            }
        }

        // How the main tree came to hold otherwise than the agent's base at a path it shares nothing for - changed it, made it,
        // made a directory of it, or removed it - because the two are reconciled differently: a commit once a sibling's fold
        // was committed, an edit while it is not, and both where a commit moved it and an edit moved it again.
        if (sinceBase.Count > 0)
        {
            // What the base held there is also what a merge is measured from: a link, which is no file to merge, and whether
            // it could be run, never read where the platform keeps no such bit.
            var came = await CameAsync(answers, worktree, baseCommit, [.. sinceBase.Select(since => (since.Look.Path, since.Change.Now))], cancellationToken).ConfigureAwait(false);

            foreach (var (look, change) in sinceBase)
            {
                var path = look.Path;
                var entry = came[path].Based;
                var why = change.Said(path, at, came[path], agentRemoved: look.Agent.Kind == Kind.Absent);

                switch (change)
                {
                    // Both trees made it: nothing either held says what a merge would start from.
                    case Since.Made:
                        twice.Add((look, why, null));
                        break;
                    case Since.OfABaseFile { BaseBlob: var baseBlob } when BothHoldAFile(look):
                        twice.Add((look, why, new TwoChangeAncestor.Committed(baseBlob, Bit(entry), entry is { IsLink: true })));
                        break;
                    case Since.OfABaseFile:
                        refused.Add((path, why));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(sinceBase), change, "Not something the main tree did since the agent's base.");
                }
            }
        }

        // Every path both trees changed is weighed in full before anything is written, asked for or not.
        var weighed = new List<(TwoChangeOffer Offer, string Why, IReadOnlyList<MergeTarget> Into)>();

        foreach (var (look, why, ancestor) in twice.OrderBy(two => two.Look.Path, StringComparer.Ordinal))
        {
            var path = look.Path;
            var offer = await changes.OfferAsync(
                policy,
                new TwoChangeAsk(
                    path,
                    ancestor,

                    // Neither tree's copy is a link, nor reached through one: a fold refuses such a path whole, above.
                    new TwoChangeSide(new GitContent.File(Path.Combine(main, path)), look.Main.File!.Value.Executable, Link: false),
                    new TwoChangeSide(new GitContent.File(Path.Combine(worktree, path)), look.Agent.File!.Value.Executable, Link: false),
                    main),
                cancellationToken).ConfigureAwait(false);

            weighed.Add((offer, why, [new MergeTarget(Path.Combine(main, path), MainTreeName, look.Main.File!.Value), new MergeTarget(Path.Combine(worktree, path), AgentTreeName, look.Agent.File!.Value)]));
        }

        // The arm registry merged by arm is judged as a registry of the main tree as the fold leaves it: what it removes
        // gone, and what it writes and merges there - each read only where the registry cites it.
        await JudgeAsync(changes, policy, weighed, main, FoldWriter, merges => TreeAfter.Copied(_fileSystem, worktree, deleted, written, merges), cancellationToken).ConfigureAwait(false);

        // A merge that is clean and was asked for - or the arm registry's, which reads as one - is taken, and any other ends
        // the path's refusal with what a merge of it gives.
        var merged = new List<MergedPath>();
        var shown = new List<string>();
        var mergeableUnasked = new List<string>();

        foreach (var (offer, why, into) in weighed)
        {
            var path = offer.Path;

            if (TwoChanges.Taken(policy, offer))
            {
                merged.Add(new MergedPath(path, offer.Working!, offer.Digest!, offer.Executable, offer.Listed, into));
                mainContent[path] = into[0].Weighed.Digest;
                agentContent[path] = into[1].Weighed;
                continue;
            }

            refused.Add((path, offer.After(why)));
            shown.AddRange(offer.Shown);

            if (offer.IsClean)
            {
                mergeableUnasked.Add(path);
            }
        }

        // What the agent's files are written over in the main tree is only what the same fold removes: a file where one of
        // them needs a directory, or files in a directory where one of them stands in its place, the fold does not remove.
        var ways = InTheWay(main, [.. written.Select(path => (path, true))], deleted.ToHashSet(StringComparer.Ordinal));

        // Each file of the main tree's in the way is weighed against the agent's base as any path is (Since.Of) - made where
        // the base holds no file there, changed where it holds one the main tree moved from, and neither where the main tree
        // holds the base's - and said as it came to be.
        var inTheWay = ways.Count > 0
            ? await CameAsync(answers, worktree, baseCommit, [.. ways.Select(way => (way.Own, Holds.File))], cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, Came>(StringComparer.Ordinal);
        var movedInTheWay = inTheWay.Values.Any(came => came.Based is { IsBlob: true })
            ? await answers.MovedFromBaseAsync().ConfigureAwait(false)
            : new HashSet<string>(StringComparer.Ordinal);

        Since? SinceInTheWay(string own)
            => Since.Of(inTheWay[own].Based is { IsBlob: true } based ? based.ObjectId : null, Holds.File, movedInTheWay.Contains(own));

        foreach (var way in ways.Where(way => !way.Below).DistinctBy(way => way.Written))
        {
            refused.Add((way.Written, $"'{way.Written}' needs a directory at '{way.Own}', where the main tree holds a file the fold does not remove"
                + (SinceInTheWay(way.Own) is { } since ? $", {since.Short($"since the agent's base {at}", inTheWay[way.Own])}" : string.Empty)));
        }

        foreach (var file in ways.Where(way => way.Below).GroupBy(way => way.Written, StringComparer.Ordinal))
        {
            // The agent's base is named once, by the first file said to have come to be since it, and the rest since then.
            var named = false;
            var listed = new List<string>();

            foreach (var way in file)
            {
                if (SinceInTheWay(way.Own) is not { } since)
                {
                    listed.Add(way.Own);
                    continue;
                }

                var came = inTheWay[way.Own];
                listed.Add($"{way.Own} ({since.Short(named ? "since then" : $"since the agent's base {at}", came)})");
                named |= !came.Ignored;
            }

            refused.Add((file.Key, $"'{file.Key}': the main tree holds a directory there with files the fold does not remove - "
                + $"{ReportText.Listed(listed)} - which writing it would remove"));
        }

        // What is settled by hand is sorted as any hand-over sorts what the main tree holds, and is handed over nothing of
        // the agent's own; a directory git will not look into is left out, as one reached through a link is.
        var sorted = await ClassifyAsync(main, settling, uncommitted: null, cancellationToken).ConfigureAwait(false);
        var handed = sorted with { Directories = [], Links = [] };
        leftOut.AddRange(sorted.Directories.Select(directory => new SettledOut(directory, MainDirectory)));

        var inTheWayOfHanding = WaysOfHanding(worktree, handed);

        foreach (var way in inTheWayOfHanding.Where(way => !way.Below).DistinctBy(way => way.Written))
        {
            refused.Add((way.Written, $"{FoldAllowances.SettledOption} '{way.Written}': handing the agent the main tree's copy needs a directory at '{way.Own}', "
                + "where the agent holds a file or a link of its own"));
        }

        foreach (var file in inTheWayOfHanding.Where(way => way.Below).GroupBy(way => way.Written, StringComparer.Ordinal))
        {
            refused.Add((file.Key, $"{FoldAllowances.SettledOption} '{file.Key}': the agent holds a directory there with files of its own - "
                + $"{ReportText.Listed([.. file.Select(way => way.Own)])} - which handing it the main tree's copy would remove"));
        }

        foreach (var (path, held) in await HoldingAsync(worktree, [.. handed.Deletions, .. handed.Files], cancellationToken).ConfigureAwait(false))
        {
            agentContent[path] = held;
        }

        foreach (var stray in settledPaths.Where(path => !candidates.Contains(path, StringComparer.Ordinal)).Order(StringComparer.Ordinal))
        {
            refused.Add((stray, $"--settled '{stray}' names no path this fold weighs, so it settles nothing: check its spelling"));
        }

        // A path named to be merged that the fold weighs and has nothing to merge at is let stand, so the command line of a
        // fold that stopped part way runs again; one it does not weigh is the misspelling it most likely is.
        foreach (var stray in policy.NamedAsText().Where(path => !candidates.Contains(path, StringComparer.Ordinal)))
        {
            refused.Add((stray, $"{FoldAllowances.MergeOption} '{stray}' names no path this fold weighs, so it merges nothing: check its spelling"));
        }

        refused.AddRange(policy.NamedNeverMerged());

        return new FoldPlan
        {
            Written = written,
            Deleted = deleted,
            Inherited = inherited,
            AlreadyIn = alreadyIn,
            Handed = handed,
            LeftOut = [.. leftOut.OrderBy(path => path.Path, StringComparer.Ordinal)],
            Merged = merged,
            Refusals = [.. refused.OrderBy(refusal => refusal.Path, StringComparer.Ordinal).Select(refusal => refusal.Why)],
            Shown = shown,
            Mergeable = mergeableUnasked,
            Seeded = seed.Weighed.ToHashSet(StringComparer.Ordinal),
            AgentContent = agentContent,
            MainContent = mainContent,
        };
    }

    /// <summary>
    /// Judges the arm registry merged by arm among <paramref name="weighed"/>, where it is, as a registry of
    /// <paramref name="tree"/> as the command leaves it - changed as <paramref name="changed"/> says, with every merge that
    /// would be taken written there, the registry's own among them - and puts the judged offer in its place.
    /// </summary>
    /// <param name="changes">Judges it.</param>
    /// <param name="policy">What decides which merges are taken.</param>
    /// <param name="weighed">Every path both trees changed, with what a merge of it gives.</param>
    /// <param name="tree">The tree the merged registry is written into.</param>
    /// <param name="writer">What writes it, as a refusal names it.</param>
    /// <param name="changed">What the command changes in that tree, given the merges it writes there.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    private static async Task JudgeAsync(
        TwoChanges changes,
        TwoChangePolicy policy,
        List<(TwoChangeOffer Offer, string Why, IReadOnlyList<MergeTarget> Into)> weighed,
        string tree,
        string writer,
        Func<IEnumerable<(string Path, byte[] Bytes)>, IEnumerable<(string Path, Func<CancellationToken, Task<byte[]>>? Read)>> changed,
        CancellationToken cancellationToken)
    {
        if (weighed.FindIndex(one => one.Offer.Registry is not null) is var at && at < 0)
        {
            return;
        }

        var merges = weighed.Where(one => TwoChanges.Taken(policy, one.Offer)).Select(one => (one.Offer.Path, one.Offer.Working!)).ToList();

        weighed[at] = weighed[at] with
        {
            Offer = await changes.JudgedAsync(policy, weighed[at].Offer, tree, writer, changed(merges), cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Writes a measured fold into <paramref name="main"/>: each deleted path removed, with the directories that leaves
    /// empty up to - never including - the main tree's root, then each written path copied and proved, so a file the agent
    /// turned into a directory, or a directory it turned into a file, makes room for what replaced it; then each merged
    /// path's bytes written into both trees, the main tree's first, once both copies are asked to be what was weighed;
    /// then, last, what the main tree holds at each path settled by hand handed to the agent, as any hand-over hands it.
    /// Never cancelled part way. Only what was measured is written or removed: an agent's file that changed since, or a
    /// main-tree file that did - one about to be written over or removed - stops it there, with what it did so far said.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="plan">A plan with no refusal.</param>
    public async Task<FoldApplied> ApplyAsync(string main, string worktree, FoldPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Refusals.Count > 0)
        {
            throw new InvalidOperationException("A fold with refusals is never applied.");
        }

        var written = new List<string>();
        var deleted = new List<string>();
        var merged = new List<string>();
        var handed = new List<HandedPath>();

        try
        {
            foreach (var path in plan.Deleted)
            {
                var file = Path.Combine(main, path);
                await RequireMeasuredAsync(file, path, plan.MainContent[path], MainTreeName, "removed").ConfigureAwait(false);
                _fileSystem.DeleteFile(file);
                deleted.Add(path);
                _fileSystem.RemoveEmptiedDirectories(main, Path.GetDirectoryName(file), _platform.PathComparison);
            }

            foreach (var path in plan.Written)
            {
                var destination = Path.Combine(main, path);
                await RequireMeasuredAsync(destination, path, plan.MainContent[path], MainTreeName, "written over").ConfigureAwait(false);
                await VerifiedFileCopy.CopyAsync(_fileSystem, Path.Combine(worktree, path), destination, plan.AgentContent[path]?.Digest, CancellationToken.None).ConfigureAwait(false);
                written.Add(path);
            }

            foreach (var merge in plan.Merged)
            {
                // The main tree first, and recorded only once both hold it: stopped between the two, a fold run again merges
                // what the main tree then holds with the agent's copy, and comes to the same bytes.
                await WriteMergeAsync(merge).ConfigureAwait(false);
                merged.Add(merge.Path);
            }

            // Last, what was settled by hand: the main tree's copy handed to the agent as any hand-over hands it, over only
            // what was weighed in the agent's worktree.
            await HandEachAsync(
                main,
                worktree,
                plan.Handed,
                plan.AgentContent,
                (path, digest) =>
                {
                    handed.Add(new HandedPath(path, digest));
                    return Task.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FoldApplied(written, deleted, merged, handed, ex.Message);
        }

        return new FoldApplied(written, deleted, merged, handed, null);
    }

    /// <summary>
    /// Writes a merge over every copy it names, in order, each asked first to be what was weighed - its content and its bit:
    /// the merge is of the files that were weighed, and one written over a file changed since would lose what nobody merged.
    /// Every copy is asked before any is written. A copy that holds what the merge gives already is not written again.
    /// Never cancelled part way, and the one way a fold and a move of an agent's base write a merge.
    /// </summary>
    /// <param name="merge">The merge, and the copies it is written over.</param>
    /// <exception cref="IOException">A copy is not what was weighed, or could not be written; what was written before it stays.</exception>
    public async Task WriteMergeAsync(MergedPath merge)
    {
        ArgumentNullException.ThrowIfNull(merge);

        foreach (var into in merge.Into)
        {
            await RequireWeighedAsync(into.File, merge.Path, into.Weighed, into.Tree, "written over").ConfigureAwait(false);
        }

        foreach (var into in merge.Into)
        {
            await WriteMergedAsync(into.File, merge).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a merged path's bytes to <paramref name="file"/>, with the bit the merge gave it, unless the file is that
    /// already: a tree that holds what the merge gives is not written again.
    /// </summary>
    /// <param name="file">One tree's copy of the path.</param>
    /// <param name="merge">The merge.</param>
    private async Task WriteMergedAsync(string file, MergedPath merge)
    {
        if (await IdentityAsync(file, CancellationToken.None).ConfigureAwait(false) != new FileIdentity(merge.Digest, merge.Executable))
        {
            await VerifiedFileCopy.WriteAsync(_fileSystem, _permissions, merge.Working, file, merge.Executable, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records in <paramref name="ledger"/> what the agent shares once a fold is applied, as far as it went: each path it
    /// wrote, and each the main tree already held as the agent does, shared as the file both trees now hold, each with the
    /// bit it was measured with - or as absent from both - each it merged as the merged bytes both now hold, with the bit
    /// they were written with, each it removed as absent from both, and each settled path it handed as a hand-over records
    /// it. A later fold weighs those paths against that, never against the agent's base: a review that sends the agent
    /// back, and the agent putting a path back as it was, is then its change to fold like any other.
    /// </summary>
    /// <param name="ledger">What the agent shares with the main tree, to add them to.</param>
    /// <param name="plan">The fold measured.</param>
    /// <param name="applied">What applying it did.</param>
    /// <param name="main">The main checkout's root, whose copies - the fold's own writes among them - the shared bytes are kept from.</param>
    /// <param name="worktree">The agent's worktree, whose copies say which files could be run.</param>
    public async Task FoldedAsync(SharedLedger ledger, FoldPlan plan, FoldApplied applied, string main, string worktree)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(applied);

        foreach (var path in applied.Written.Concat(plan.AlreadyIn))
        {
            if (plan.AgentContent[path] is { } identity)
            {
                await ledger.FileAsync(path, identity.Digest, identity.Executable, Path.Combine(main, path)).ConfigureAwait(false);
            }
            else
            {
                ledger.Absent(path);
            }
        }

        foreach (var merge in plan.Merged.Where(merge => applied.Merged.Contains(merge.Path, StringComparer.Ordinal)))
        {
            await ledger.FileAsync(merge.Path, merge.Digest, merge.Executable, Path.Combine(main, merge.Path)).ConfigureAwait(false);
        }

        foreach (var path in applied.Deleted)
        {
            ledger.Absent(path);
        }

        foreach (var handed in applied.Handed)
        {
            await HandedAsync(ledger, worktree, handed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What the worktree holds at every path a fold of it weighs and every one of <paramref name="also"/>: each file's
    /// digest, or null where it holds none. What closing an agent records, and what finishing its deletion compares the
    /// worktree with - never the main tree, which later agents go on changing.
    /// </summary>
    /// <param name="worktree">The agent's worktree, which must be its own registered worktree.</param>
    /// <param name="seed">What it shares with the main tree, where it was ever seeded.</param>
    /// <param name="also">The paths to weigh besides.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="IOException">A path could not be looked at: never read as holding no file.</exception>
    public async Task<Dictionary<string, string?>> HeldAsync(
        string worktree,
        SeedRecord? seed,
        IEnumerable<string> also,
        IReadOnlyList<string> floor,
        CancellationToken cancellationToken)
    {
        var held = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var weighed = await CandidatesAsync(worktree, seed, floor, "what the agent holds cannot be recorded", cancellationToken).ConfigureAwait(false);

        foreach (var path in weighed.Concat(also.Where(path => !TreeFloor.Covers(floor, path))).Distinct(StringComparer.Ordinal))
        {
            held[path] = await DigestAsync(Path.Combine(worktree, path), cancellationToken).ConfigureAwait(false);
        }

        return new Dictionary<string, string?>(held, StringComparer.Ordinal);
    }

    /// <summary>
    /// The paths the worktree holds now with content its closing did not record: a file whose digest is not the recorded
    /// one, or one where none was recorded. A file gone since is not listed - a removal only deletes, so that is the debris
    /// of one that stopped part way - and nor is a path the main tree ignores, which no fold would write: asked of the main
    /// tree, since a removal that stopped part way can have deleted the worktree's own ignore rules first.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree, which must be its own registered worktree.</param>
    /// <param name="closing">What closing it recorded.</param>
    /// <param name="seed">What it shared with the main tree, where it was ever seeded.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<IReadOnlyList<string>> ChangedSinceAsync(
        string main,
        string worktree,
        AgentClosing closing,
        SeedRecord? seed,
        IReadOnlyList<string> floor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(closing);

        var now = await HeldAsync(worktree, seed, closing.Held.Keys, floor, cancellationToken).ConfigureAwait(false);
        var changed = now
            .Where(pair => pair.Value is not null && (!closing.Held.TryGetValue(pair.Key, out var recorded) || recorded != pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        if (changed.Count == 0)
        {
            return [];
        }

        var ignored = await _gitClient.FindIgnoredAsync(main, changed, cancellationToken).ConfigureAwait(false);
        return [.. changed.Where(path => !ignored.Contains(path))];
    }

    /// <summary>
    /// Every path the work tree at <paramref name="directory"/> changes, off <paramref name="floor"/>, as git names it: an
    /// untracked directory git will not look into - a repository of its own - named as the directory. Each is one a record
    /// can keep: named in UTF-8, and relative to the tree on every platform.
    /// </summary>
    /// <param name="directory">A work tree's root.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="consequence">What cannot be done with a path no record can keep, to end a sentence.</param>
    /// <param name="within">Only the paths these cover (<see cref="PathPatterns"/>); every path where null.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">
    /// git could not read the status, or a path is not named in UTF-8, or is one no record can keep - rooted, or with an
    /// empty, <c>.</c> or <c>..</c> part, on some platform - refused before anything is written.
    /// </exception>
    public async Task<IReadOnlyList<string>> ChangedAsync(
        string directory,
        IReadOnlyList<string> floor,
        string consequence,
        IReadOnlyList<string>? within,
        CancellationToken cancellationToken)
    {
        var names = (await _gitClient.ReadStatusAsync(directory, cancellationToken).ConfigureAwait(false)).SelectMany(entry => entry.Paths);

        return [.. Weighed(directory, names, floor, within, consequence).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The paths <paramref name="names"/> name that are weighed - off <paramref name="floor"/>, and under
    /// <paramref name="within"/> where it is given - each one a record can keep: a name that is not UTF-8, or no record can
    /// keep, is refused, never passed over.
    /// </summary>
    /// <param name="directory">The work tree they are in.</param>
    /// <param name="names">The names, as git holds them; a directory's with its trailing slash.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="within">Only the paths these cover; every path where null.</param>
    /// <param name="consequence">What cannot be done with a path no record can keep, to end a sentence.</param>
    private static IEnumerable<string> Weighed(string directory, IEnumerable<GitName> names, IReadOnlyList<string> floor, IReadOnlyList<string>? within, string consequence)
    {
        foreach (var name in names)
        {
            var path = name.Text.TrimEnd('/');

            if (!Covered(path, floor, within))
            {
                continue;
            }

            if (!name.IsUtf8)
            {
                throw name.Unreadable(consequence);
            }

            RequireKeepable(directory, path, consequence);

            yield return path;
        }
    }

    /// <summary>Whether <paramref name="path"/> is weighed: off <paramref name="floor"/>, and under <paramref name="within"/> where it is given.</summary>
    private static bool Covered(string path, IReadOnlyList<string> floor, IReadOnlyList<string>? within)
        => !TreeFloor.Covers(floor, path) && (within is null || PathPatterns.Matches(within, path));

    /// <summary>
    /// Refuses <paramref name="path"/>, which the tree at <paramref name="directory"/> changes, where no record of an agent
    /// can keep it: rooted, or with an empty, <c>.</c> or <c>..</c> part, on some platform.
    /// </summary>
    /// <param name="directory">A work tree's root.</param>
    /// <param name="path">A path it changes, as git names it.</param>
    /// <param name="consequence">What cannot be done with a path no record can keep, to end a sentence.</param>
    private static void RequireKeepable(string directory, string path, string consequence)
    {
        if (OrchestrationRules.RelativePathProblem(path, $"'{ReportText.Printable(directory)}' changes a path that") is { } problem)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{problem}, and no record of an agent can keep a path another platform would read as somewhere else, so {consequence}. "
                + "Rename or remove it first. Nothing was written.");
        }
    }

    /// <summary>
    /// What the main tree's uncommitted state under <paramref name="within"/> - all of it where null - hands an agent, off
    /// the floor: each changed file, each deletion, and each untracked directory git will not look into, named and not
    /// handed. Every path is checked before any is copied, so a hand-over refused is refused before anything is written.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="within">Only the paths these cover; every path where null.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">
    /// A path is a symbolic link, which is never handed over as the file it leads to, or is one no record can keep
    /// (<see cref="HarnessExit.Refused"/>).
    /// </exception>
    public async Task<Handable> HandableAsync(string main, IReadOnlyList<string> floor, IReadOnlyList<string>? within, CancellationToken cancellationToken)
        => await ClassifyAsync(main, await ChangedAsync(main, floor, HandingConsequence, within, cancellationToken).ConfigureAwait(false), uncommitted: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// What the main tree under <paramref name="within"/> - all of it where null - holds differently from what the agent
    /// shares with it, off the floor: each path whose main-tree copy is not what the agent's seed records both trees held
    /// when it was last handed it or folded, or, for a path it shares nothing for, not what its base holds, as git status
    /// compares - committed or not. A copy the agent was handed is stale once the main tree moves it, whether the main tree
    /// moved it back to its HEAD or committed it since; one it was never handed is stale once the main tree's copy is not
    /// its base's. Each such file, each such deletion, each untracked directory git will not look into and each link the
    /// main tree committed, named and not handed; one it has not committed refuses it. Every path is checked before any is
    /// copied.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="baseCommit">The commit the agent's worktree was made from.</param>
    /// <param name="seed">What the agent shares with the main tree.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="within">Only the paths these cover; every path where null.</param>
    /// <param name="everyUncommitted">
    /// Whether every path of the main tree's uncommitted state is handed besides, whatever the agent shares: what seeding
    /// again hands, over changes of the agent's own where it is forced to.
    /// </param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">
    /// A path the main tree has not committed is a symbolic link, or one is a path no record can keep
    /// (<see cref="HarnessExit.Refused"/>), or git could not answer.
    /// </exception>
    public async Task<Handable> MovedAsync(
        string main,
        string baseCommit,
        SeedRecord seed,
        IReadOnlyList<string> floor,
        IReadOnlyList<string>? within,
        bool everyUncommitted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var uncommitted = await ChangedAsync(main, floor, HandingConsequence, within, cancellationToken).ConfigureAwait(false);
        var sinceNamed = await _gitClient.ListNamesChangedSinceAsync(main, baseCommit, cancellationToken).ConfigureAwait(false);
        var sinceBase = GitName.PathsOf(sinceNamed);

        var candidates = uncommitted
            .Concat(Weighed(main, sinceNamed, floor, within, HandingConsequence))
            .Concat(seed.Weighed.Where(path => Covered(path, floor, within)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // A link is never read as the file it leads to, so it is weighed by what git says of it alone: changed since the
        // agent's base, committed or not.
        var links = candidates.Where(path => _fileSystem.KindOf(Path.Combine(main, path)) == PathKind.Link).ToHashSet(StringComparer.Ordinal);
        var moved = await DiffersAsync(main, baseCommit, seed, [.. candidates.Where(path => !links.Contains(path))], sinceBase, cancellationToken).ConfigureAwait(false);

        var handed = moved
            .Concat(links)
            .Concat(everyUncommitted ? uncommitted : [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        return await ClassifyAsync(main, [.. handed], uncommitted.ToHashSet(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="paths"/> sorted by what the main tree holds at each: a file to copy, a deletion to make, a directory
    /// git will not look into, or a link - refused where the main tree has not committed it, named and not handed where
    /// it has. Any other directory stands where the agent's base or seed holds a file, which the main tree turned into a
    /// directory: that file is a deletion to make, and what the directory holds comes on paths of its own.
    /// <paramref name="uncommitted"/> is the main tree's uncommitted state; every path is in it where null.
    /// </summary>
    private async Task<Handable> ClassifyAsync(string main, IReadOnlyList<string> paths, IReadOnlySet<string>? uncommitted, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var deletions = new List<string>();
        var directories = new List<string>();
        var links = new List<string>();
        var committedLinks = new List<string>();

        foreach (var path in paths)
        {
            switch (_fileSystem.KindOf(Path.Combine(main, path)))
            {
                case PathKind.File:
                    files.Add(path);
                    break;
                case PathKind.None:
                    deletions.Add(path);
                    break;
                case PathKind.Directory:
                    directories.Add(path);
                    break;
                default:
                    (uncommitted is null || uncommitted.Contains(path) ? links : committedLinks).Add(path);
                    break;
            }
        }

        // git will not look into a repository of its own - one holding its own .git - nor into a submodule, whose entry may
        // stand for an empty directory nothing has checked out.
        if (directories.Any(directory => _fileSystem.KindOf(Path.Combine(main, directory, ".git")) == PathKind.None))
        {
            var submodules = (await _gitClient.ListIndexAsync(main, cancellationToken).ConfigureAwait(false))
                .Where(entry => entry.IsSubmodule)
                .Select(entry => entry.Path)
                .ToHashSet(StringComparer.Ordinal);
            var turned = directories
                .Where(directory => _fileSystem.KindOf(Path.Combine(main, directory, ".git")) == PathKind.None && !submodules.Contains(directory))
                .ToList();

            directories = [.. directories.Except(turned, StringComparer.Ordinal)];
            deletions = [.. deletions.Concat(turned).Order(StringComparer.Ordinal)];
        }

        return links.Count switch
        {
            0 => new Handable(files, deletions, directories, committedLinks),
            1 => throw new HarnessException(
                HarnessExit.Refused,
                $"'{ReportText.Printable(links[0])}' is a symbolic link, which is never handed to an agent as the file it leads to: commit it, or remove "
                + "it, first. Nothing was handed over."),
            _ => throw new HarnessException(
                HarnessExit.Refused,
                $"{ReportText.Listed(links)} are symbolic links, which are never handed to an agent as the files they lead to: commit them, or remove "
                + "them, first. Nothing was handed over."),
        };
    }

    /// <summary>
    /// Measures refreshing the agent whose worktree is <paramref name="worktree"/> under <paramref name="within"/> - all of
    /// it where null; writes nothing. Each path the main tree holds otherwise than the agent shares it
    /// (<see cref="MovedAsync"/>) is, in this order: held by the agent as the main tree holds it already, and so recorded
    /// as shared; changed by the agent (<see cref="EditedAsync"/>) and named in <paramref name="except"/>, left as it is;
    /// changed by the agent, named in <paramref name="stale"/> and proved the main tree's own copy at an earlier commit,
    /// handed; changed by the agent, each tree holding a file there, whose three-way merge is clean and taken, merged into
    /// the worktree; changed by the agent otherwise, which refuses the refresh; or handed. A path named stale that the main tree
    /// did not move is weighed too, where the agent's copy is not the main tree's. What the agent's worktree holds at each
    /// path handed is measured with it, so the hand-over writes over nothing it did not weigh.
    /// </summary>
    /// <remarks>
    /// The proof is git's, asked once for every path a refusal or a <see cref="RefreshRequest.StaleOption"/> names: the
    /// agent's copy as git would store it in its worktree (<see cref="IGitClient.ListStagedIdsAsync"/>), so a copy that
    /// differs from a commit's file only by a line-ending conversion is that commit's file, against the blobs the main
    /// tree's history held at the path (<see cref="IGitClient.ListHeldInHistoryAsync"/>, among the last
    /// <see cref="TwoChanges.MostCommits"/> commits that changed it, merges' other sides included). Bytes that history
    /// holds are the main tree's own there, so handing its copy over them loses nothing the history does not keep.
    /// </remarks>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="baseCommit">The commit its worktree was made from.</param>
    /// <param name="seed">What it shares with the main tree.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="within">Only the paths these cover; every path where null.</param>
    /// <param name="except">The paths named to be left as the agent changed them.</param>
    /// <param name="stale">The paths named as stale copies, to be handed the main tree's where the proof holds.</param>
    /// <param name="policy">How a path both trees changed is resolved, the paths named to be merged among it.</param>
    /// <param name="changes">Measures what a three-way merge of such a path gives.</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <exception cref="HarnessException">A path is one a hand-over refuses (<see cref="MovedAsync"/>), or git could not answer.</exception>
    /// <exception cref="IOException">A path could not be looked at.</exception>
    public async Task<RefreshPlan> MeasureRefreshAsync(
        string main,
        string worktree,
        string baseCommit,
        SeedRecord seed,
        IReadOnlyList<string> floor,
        IReadOnlyList<string>? within,
        IReadOnlySet<string> except,
        IReadOnlySet<string> stale,
        TwoChangePolicy policy,
        TwoChanges changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(except);
        ArgumentNullException.ThrowIfNull(stale);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(changes);

        // Every path the main tree holds otherwise than the agent shares it, committed or not: a copy it was handed goes
        // stale as soon as the main tree moves it, back to its HEAD's bytes included.
        var moved = await MovedAsync(main, baseCommit, seed, floor, within, everyUncommitted: false, cancellationToken).ConfigureAwait(false);
        var movedPaths = moved.Files.Concat(moved.Deletions).ToHashSet(StringComparer.Ordinal);
        var alike = new List<string>();
        var other = new List<string>();

        foreach (var path in movedPaths.Order(StringComparer.Ordinal))
        {
            (await HoldsAlikeAsync(main, worktree, path, cancellationToken).ConfigureAwait(false) ? alike : other).Add(path);
        }

        // A stale copy the main tree did not move is weighed too - one put back by hand to an older version, which the
        // agent's fold would otherwise write into the main tree as its change - where it is not the main tree's copy. One
        // that is, is let stand, so a line that ran once runs again; one outside what is weighed names nothing.
        var strays = new List<string>();
        var unmoved = new List<string>();

        foreach (var path in stale.Where(path => !movedPaths.Contains(path)).Order(StringComparer.Ordinal))
        {
            if (!Covered(path, floor, within))
            {
                strays.Add(path);
            }
            else if (_fileSystem.KindOf(Path.Combine(main, path)) is not (PathKind.File or PathKind.None))
            {
                // The main tree holds a link or a directory there - never a file to hand, so no copy of the main tree's to
                // prove stale either: named as itself among what is not handed, and weighed no further, as MovedAsync
                // keeps links and directories from its own question.
            }
            else if (!await HoldsAlikeAsync(main, worktree, path, cancellationToken).ConfigureAwait(false))
            {
                unmoved.Add(path);
            }
        }

        // Never over a change of the agent's own - an edit, or a deletion - handed to it or not. One left as it is by name is
        // handed nothing, and is in the way of what is handed like anything else of the agent's.
        var edited = await EditedAsync(worktree, baseCommit, seed, other, cancellationToken).ConfigureAwait(false);
        var proved = await StaleAtAsync(main, worktree, [.. edited.Where(path => !except.Contains(path)), .. unmoved], cancellationToken).ConfigureAwait(false);
        var staleCopies = new List<StaleCopy>();
        var refused = new List<string>();

        foreach (var path in edited.Where(stale.Contains).Concat(unmoved).Order(StringComparer.Ordinal))
        {
            if (proved.At.TryGetValue(path, out var commit))
            {
                staleCopies.Add(new StaleCopy(path, commit));
            }
            else
            {
                refused.Add(_fileSystem.KindOf(Path.Combine(worktree, path)) == PathKind.File
                    ? $"{RefreshRequest.StaleOption} '{path}': its copy is no file the main tree held at that path in the last "
                        + $"{ReportText.Counted(TwoChanges.MostCommits, "commit")} that changed it, so nothing proves it stale. "
                        + $"{RefreshRequest.ExceptOption} leaves it as the agent's change, for its fold to weigh."
                    : $"{RefreshRequest.StaleOption} '{path}': the agent holds no file there, and a deletion is its change. {RefreshRequest.ExceptOption} leaves it.");
            }
        }

        // Handed: what is neither held as the main tree holds it already, nor the agent's change, nor named to be left -
        // and each stale copy proved, the main tree's own at an earlier commit.
        var handing = staleCopies.Select(copy => copy.Path).ToHashSet(StringComparer.Ordinal);
        var unhanded = alike.Concat(edited).Concat(except).Where(path => !handing.Contains(path)).ToHashSet(StringComparer.Ordinal);
        var unmovedHanded = await ClassifyAsync(main, [.. unmoved.Where(handing.Contains)], uncommitted: null, cancellationToken).ConfigureAwait(false);
        var handed = moved with
        {
            Files = [.. moved.Files.Where(path => !unhanded.Contains(path)).Concat(unmovedHanded.Files).Order(StringComparer.Ordinal)],
            Deletions = [.. moved.Deletions.Where(path => !unhanded.Contains(path)).Concat(unmovedHanded.Deletions).Order(StringComparer.Ordinal)],
            Directories = [.. moved.Directories.Concat(unmovedHanded.Directories).Order(StringComparer.Ordinal)],
            Links = [.. moved.Links.Concat(unmovedHanded.Links).Order(StringComparer.Ordinal)],
        };

        // What the agent changed that the main tree moved too, each tree holding a file there, is weighed in full, asked for
        // or not: a clean merge named to be taken - or the arm registry's, judged as a registry of the worktree the refresh
        // leaves, each path it hands read from the main tree - is written; any other is said beside the path it refuses.
        var weighed = await WeighChangedAsync(main, worktree, baseCommit, seed, policy, changes, [.. edited.Where(path => !except.Contains(path) && !handing.Contains(path))], cancellationToken)
            .ConfigureAwait(false);
        var twice = weighed.Select(one => (one.Offer, Why: string.Empty, one.Into)).ToList();

        await JudgeAsync(changes, policy, twice, worktree, RefreshWriter, merges => TreeAfter.Copied(_fileSystem, main, handed.Deletions, handed.Files, merges), cancellationToken)
            .ConfigureAwait(false);

        var mains = weighed.ToDictionary(one => one.Offer.Path, one => one.Main, StringComparer.Ordinal);
        var merged = new List<RefreshMerge>();
        var gives = new Dictionary<string, string>(StringComparer.Ordinal);
        var shown = new List<string>();
        var mergeable = new List<string>();

        foreach (var (offer, _, into) in twice)
        {
            if (TwoChanges.Taken(policy, offer))
            {
                merged.Add(new RefreshMerge(new MergedPath(offer.Path, offer.Working!, offer.Digest!, offer.Executable, offer.Listed, into), mains[offer.Path]));
                continue;
            }

            if (offer.Gives.Length > 0)
            {
                gives[offer.Path] = offer.Gives;
            }

            shown.AddRange(offer.Shown);

            if (offer.IsClean)
            {
                mergeable.Add(offer.Path);
            }
        }

        var merging = merged.Select(merge => merge.Merge.Path).ToHashSet(StringComparer.Ordinal);

        return new RefreshPlan
        {
            Handed = handed,
            Alike = [.. alike.Order(StringComparer.Ordinal)],
            Left = [.. edited.Where(except.Contains)],
            Changed = [.. edited
                .Where(path => !except.Contains(path) && !handing.Contains(path) && !merging.Contains(path))
                .Select(path => new RefreshChange(path, proved.At.GetValueOrDefault(path), gives.GetValueOrDefault(path)))],
            Merged = merged,
            Shown = shown,
            Mergeable = mergeable,

            // A path named to be merged under the paths weighed that has nothing to merge is let stand, so a line that ran
            // once runs again; one outside them names nothing.
            MergeStray = [.. policy.NamedAsText().Where(path => !Covered(path, floor, within))],
            Stale = staleCopies,
            StaleRefused = refused,
            StaleStray = strays,
            Capped = proved.Capped,
            InTheWay = InTheWayOfHanding(worktree, handed),
            InSubmodules = await InSubmodulesAsync(worktree, [.. handed.Deletions, .. handed.Files, .. alike], cancellationToken).ConfigureAwait(false),
            Held = await HoldingAsync(worktree, [.. handed.Deletions, .. handed.Files, .. alike], cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// What a three-way merge of each of <paramref name="paths"/> gives where both trees hold a file there - the main
    /// tree's the side merged into, the agent's the side merged in, from what both last held: the bytes its seed records,
    /// or for a path it shares nothing for, the blob at its base - each with the agent's copy a merge is written over and
    /// what the main tree held. A copy that is a link or reached through one is offered no merge, and is written over by
    /// none.
    /// </summary>
    private async Task<List<(TwoChangeOffer Offer, IReadOnlyList<MergeTarget> Into, FileIdentity Main)>> WeighChangedAsync(
        string main,
        string worktree,
        string baseCommit,
        SeedRecord seed,
        TwoChangePolicy policy,
        TwoChanges changes,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var both = paths.Where(path => _fileSystem.KindOf(Path.Combine(main, path)) == PathKind.File && _fileSystem.KindOf(Path.Combine(worktree, path)) == PathKind.File).ToList();
        var weighed = new List<(TwoChangeOffer Offer, IReadOnlyList<MergeTarget> Into, FileIdentity Main)>();

        if (both.Count == 0)
        {
            return weighed;
        }

        var shared = new Baselines(seed);
        var unseeded = both.Where(path => shared.Of(path) is null).ToList();
        var entries = unseeded.Count > 0
            ? await _gitClient.ListEntriesAtAsync(worktree, baseCommit, unseeded, cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, GitEntry>(StringComparer.Ordinal);
        var reachedMain = _fileSystem.ResolveLinks(main);
        var reachedAgent = _fileSystem.ResolveLinks(worktree);

        foreach (var path in both)
        {
            TwoChangeAncestor? ancestor = shared.Of(path) is { } baseline
                ? baseline.File is { } held ? new TwoChangeAncestor.Shared(held.Digest, held.Executable) : null
                : entries.GetValueOrDefault(path) is { IsBlob: true } entry ? new TwoChangeAncestor.Committed(entry.ObjectId, Bit(entry), entry.IsLink) : null;
            var mainFile = Path.Combine(main, path);
            var agentFile = Path.Combine(worktree, path);
            var mainLinked = ThroughLink(main, reachedMain, path);
            var agentLinked = ThroughLink(worktree, reachedAgent, path);
            var ours = mainLinked ? default : await IdentityAsync(mainFile, cancellationToken).ConfigureAwait(false);
            var theirs = agentLinked ? default : await IdentityAsync(agentFile, cancellationToken).ConfigureAwait(false);
            var offer = await changes.OfferAsync(
                policy,
                new TwoChangeAsk(
                    path,
                    ancestor,
                    new TwoChangeSide(new GitContent.File(mainFile), ours.Executable, mainLinked),
                    new TwoChangeSide(new GitContent.File(agentFile), theirs.Executable, agentLinked),
                    worktree),
                cancellationToken).ConfigureAwait(false);

            weighed.Add((offer, mainLinked || agentLinked ? [] : [new MergeTarget(agentFile, AgentTreeName, theirs)], ours));
        }

        return weighed;
    }

    /// <summary>
    /// Writes each merge <paramref name="plan"/> takes into the agent's worktree - the copy asked first to be what was
    /// weighed, its content and its bit - and records each path in <paramref name="ledger"/> as it goes, as shared at what
    /// the main tree held, its bytes kept from the main tree's file: the merge is the agent's change over that, for its
    /// fold to write, and what its copy descends from. Never cancelled part way.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="plan">The refresh measured.</param>
    /// <param name="ledger">What the agent shares with the main tree, to add them to.</param>
    /// <returns>How many were merged, and why it stopped where it did not merge them all.</returns>
    public async Task<(int Merged, string? Stopped)> MergeRefreshAsync(string main, RefreshPlan plan, SharedLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ledger);

        var count = 0;

        try
        {
            foreach (var (merge, held) in plan.Merged)
            {
                await WriteMergeAsync(merge).ConfigureAwait(false);
                await ledger.FileAsync(merge.Path, held.Digest, held.Executable, Path.Combine(main, merge.Path)).ConfigureAwait(false);
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (count, ex.Message.TrimEnd('.'));
        }

        return (count, null);
    }

    /// <summary>
    /// The newest commit of the main tree's history that holds, at each of <paramref name="paths"/> the agent holds a file
    /// at, the very bytes the agent's copy is as git would store it; and whether the search stopped at its bound with more
    /// history left. A path the agent holds no file at, or whose bytes that history does not hold, has none.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string> At, bool Capped)> StaleAtAsync(
        string main,
        string worktree,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var files = paths.Distinct(StringComparer.Ordinal).Where(path => _fileSystem.KindOf(Path.Combine(worktree, path)) == PathKind.File).ToList();
        var at = new Dictionary<string, string>(StringComparer.Ordinal);

        if (files.Count == 0)
        {
            return (at, false);
        }

        var staged = await _gitClient.ListStagedIdsAsync(worktree, files, cancellationToken).ConfigureAwait(false);
        var history = await _gitClient.ListHeldInHistoryAsync(main, "HEAD", files, TwoChanges.MostCommits, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < files.Count; index++)
        {
            if (history.Held.GetValueOrDefault(files[index], []).FirstOrDefault(version => version.Blob == staged[index]) is { } version)
            {
                at[files[index]] = version.Commit;
            }
        }

        return (at, history.Cut);
    }

    /// <summary>
    /// Records in <paramref name="ledger"/> each path of <paramref name="plan"/> the agent already holds as the main tree
    /// does, as what it was when it was measured - the file both trees held, its digest and its bit, or absent from both -
    /// its bytes kept from the agent's copy: nothing is copied, and its record then says what it holds.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="plan">The refresh measured.</param>
    /// <param name="ledger">What the agent shares with the main tree, to add them to.</param>
    public async Task AlikeAsync(string worktree, RefreshPlan plan, SharedLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ledger);

        foreach (var path in plan.Alike)
        {
            var copy = Path.Combine(worktree, path);

            if (plan.Held[path] is { } measured)
            {
                await ledger.FileAsync(path, measured.Digest, measured.Executable, copy).ConfigureAwait(false);
            }
            else
            {
                ledger.Absent(path);
            }
        }
    }

    /// <summary>
    /// Each of <paramref name="paths"/> that is or lies in a submodule the agent's worktree records - a gitlink in its index
    /// at or above the path, compared as its <c>core.ignorecase</c> says - with that submodule: another repository, which a
    /// hand-over never writes into or removes from. One git process for them all, none where there are none.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="paths">The paths it is to be handed, or recorded as sharing.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">git could not answer.</exception>
    public async Task<IReadOnlyList<SubmodulePath>> InSubmodulesAsync(string worktree, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var found = await new Submodules(_gitClient, worktree, () => _gitClient.IgnoresCaseAsync(worktree, cancellationToken), cancellationToken)
            .OfAsync(paths)
            .ConfigureAwait(false);

        return [.. found.OrderBy(path => path.Key, StringComparer.Ordinal).Select(path => new SubmodulePath(path.Key, path.Value.Gitlink))];
    }

    /// <summary>
    /// What the agent's worktree holds at each of <paramref name="paths"/> - the file it is, its digest and whether it
    /// could be run, or null where it holds no file - measured so a hand-over writes over or removes only what was weighed,
    /// the file whole. A path where it holds anything else - a directory, a link - is not in it: a hand-over meets that as
    /// it always has.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="paths">The paths it is to be handed.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="IOException">A path could not be looked at.</exception>
    public async Task<IReadOnlyDictionary<string, FileIdentity?>> HoldingAsync(string worktree, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var held = new Dictionary<string, FileIdentity?>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var copy = Path.Combine(worktree, path);

            switch (_fileSystem.KindOf(copy))
            {
                case PathKind.File:
                    held[path] = await IdentityAsync(copy, cancellationToken).ConfigureAwait(false);
                    break;
                case PathKind.None:
                    held[path] = null;
                    break;
            }
        }

        return held;
    }

    /// <summary>
    /// Hands the agent what <paramref name="handable"/> names - each deletion made in its worktree, with the directories that
    /// leaves empty, then each file copied from the main tree there and proved, so a file the main tree turned into a
    /// directory, or a directory it turned into a file, makes room for what replaced it - and records in
    /// <paramref name="ledger"/> what it was handed as it goes: a hand-over that stops part way leaves a record of exactly
    /// what was handed, never one that takes a copied file for the agent's own work. Each path <paramref name="held"/>
    /// names is first asked to hold what was measured there: a file the agent's session wrote since is never written over
    /// or removed, and the hand-over stops there.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="handable">What to hand it, as <see cref="HandableAsync"/> gives it.</param>
    /// <param name="ledger">What the agent shares with the main tree, to add them to.</param>
    /// <param name="held">
    /// What the worktree held at each path when it was measured (<see cref="HoldingAsync"/>); null for a worktree just made,
    /// which holds nothing anybody wrote.
    /// </param>
    /// <param name="cancellationToken">Stops it between one path and the next.</param>
    /// <returns>How many paths were handed, and why it stopped where it did not hand everything.</returns>
    public async Task<(int Handed, string? Stopped)> HandAsync(
        string main,
        string worktree,
        Handable handable,
        SharedLedger ledger,
        IReadOnlyDictionary<string, FileIdentity?>? held,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handable);
        ArgumentNullException.ThrowIfNull(ledger);

        var count = 0;
        string? stopped = null;

        try
        {
            await HandEachAsync(
                main,
                worktree,
                handable,
                held,
                async (path, digest) =>
                {
                    await HandedAsync(ledger, worktree, new HandedPath(path, digest)).ConfigureAwait(false);
                    count++;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            stopped = ex is OperationCanceledException ? "it was interrupted" : ex.Message.TrimEnd('.');
        }

        return (count, stopped);
    }

    /// <summary>
    /// The one way anything is handed to an agent: each deletion of <paramref name="handable"/> made in its worktree, with
    /// the directories that leaves empty, then each file copied from the main tree there and proved, each path
    /// <paramref name="held"/> names first asked to hold what was measured there; <paramref name="handed"/> is told of each
    /// as it is done, so what stops part way leaves word of exactly what was handed.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="handable">What to hand it.</param>
    /// <param name="held">What the worktree held at each path when it was measured; null where nothing is asked.</param>
    /// <param name="handed">Told of each path handed, with the digest of the file copied, or null for a deletion.</param>
    /// <param name="cancellationToken">Stops it between one path and the next.</param>
    /// <exception cref="IOException">A path is not what was measured, or could not be handed; what was handed before it stays.</exception>
    private async Task HandEachAsync(
        string main,
        string worktree,
        Handable handable,
        IReadOnlyDictionary<string, FileIdentity?>? held,
        Func<string, string?, Task> handed,
        CancellationToken cancellationToken)
    {
        async Task RequireHeldAsync(string path, string copy, string what)
        {
            if (held is not null && held.TryGetValue(path, out var measured))
            {
                if (measured is { } identity)
                {
                    await RequireWeighedAsync(copy, path, identity, AgentTreeName, what).ConfigureAwait(false);
                }
                else
                {
                    await RequireMeasuredAsync(copy, path, null, AgentTreeName, what).ConfigureAwait(false);
                }
            }
        }

        foreach (var path in handable.Deletions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var copy = Path.Combine(worktree, path);
            await RequireHeldAsync(path, copy, "removed").ConfigureAwait(false);

            switch (_fileSystem.KindOf(copy))
            {
                case PathKind.File or PathKind.Link:
                    _fileSystem.DeleteFile(copy);
                    _fileSystem.RemoveEmptiedDirectories(worktree, Path.GetDirectoryName(copy), _platform.PathComparison);
                    break;
                case PathKind.Directory:
                    throw new IOException($"'{path}' is a directory in the agent's worktree, where the main tree deleted a file");
            }

            await handed(path, null).ConfigureAwait(false);
        }

        foreach (var path in handable.Files)
        {
            var copy = Path.Combine(worktree, path);
            await RequireHeldAsync(path, copy, "written over").ConfigureAwait(false);
            var made = await VerifiedFileCopy.CopyAsync(_fileSystem, Path.Combine(main, path), copy, cancellationToken: cancellationToken).ConfigureAwait(false);
            await handed(path, made.Content).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records in <paramref name="ledger"/> a path handed to the agent: absent from both trees, or the file copied, its bytes
    /// kept from the copy just written and proved - what the seed records the agent as holding.
    /// </summary>
    private async Task HandedAsync(SharedLedger ledger, string worktree, HandedPath handed)
    {
        var copy = Path.Combine(worktree, handed.Path);

        if (handed.Digest is { } digest)
        {
            await ledger.FileAsync(handed.Path, digest, IsExecutable(copy), copy).ConfigureAwait(false);
        }
        else
        {
            ledger.Absent(handed.Path);
        }
    }

    /// <summary>
    /// Measures moving the agent's base from <paramref name="from"/> to <paramref name="to"/>; writes nothing. Each path the
    /// two commits hold differently, as git compares them, is one of: shared with the main tree, which stays as its seed
    /// records it; held as <paramref name="to"/> holds it already, a file the agent made that is the very file
    /// <paramref name="to"/> holds there among them; held as <paramref name="from"/> holds it, which comes in as
    /// <paramref name="to"/> holds it; or changed by the agent - an edit, a deletion, or anything else of its own where its
    /// base held nothing - which is refused, unless declared settled by hand, when its copy stays as its change on the new
    /// base. A file it changed that the main tree committed a change to is two changes of one file: what a three-way merge
    /// of them gives - from what <paramref name="from"/> holds, of what <paramref name="to"/> holds and the agent's file -
    /// is measured and said, and a clean one is taken where <paramref name="policy"/> names it. What comes in never goes
    /// over anything of the agent's: a file or a link where the new base needs a directory, and anything in a directory
    /// where it holds a file, refuse the move too - its own, settled, or shared alike.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="from">The commit its worktree was made from.</param>
    /// <param name="to">The commit its base moves to.</param>
    /// <param name="seed">What it shares with the main tree.</param>
    /// <param name="settled">The paths declared settled by hand.</param>
    /// <param name="policy">What decides how a path both changed is resolved: the paths named to be merged among it.</param>
    /// <param name="changes">Measures what a three-way merge of such a path gives.</param>
    /// <param name="finishing">
    /// Whether this finishes a move its record says stopped part way: a file its base did not hold that git lists as
    /// unchanged since <paramref name="to"/> is then what that move wrote, and is held as <paramref name="to"/> holds it.
    /// Otherwise such a file is the agent's own - unless it is the very file <paramref name="to"/> holds there, read and
    /// compared, which is held as <paramref name="to"/> holds it whether a move is being finished or not.
    /// </param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <exception cref="HarnessException">git could not answer, or names a path that is not UTF-8.</exception>
    public async Task<RebasePlan> MeasureRebaseAsync(
        string worktree,
        string from,
        string to,
        SeedRecord seed,
        IReadOnlyCollection<string> settled,
        TwoChangePolicy policy,
        TwoChanges changes,
        bool finishing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(changes);

        var named = new List<string>();

        foreach (var name in await _gitClient.ListNamesAsync(worktree, ["diff", "--name-only", "-z", "--no-renames", "--ignore-submodules=none", from, to, "--"], cancellationToken).ConfigureAwait(false))
        {
            if (!name.IsUtf8)
            {
                throw name.Unreadable("the agent's base cannot be moved");
            }

            named.Add(name.Text);
        }

        IReadOnlyList<string> committed = [.. named.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var settledPaths = settled.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal);
        var shared = new Baselines(seed);
        var sinceFrom = await _gitClient.ListChangedSinceAsync(worktree, from, cancellationToken).ConfigureAwait(false);
        var sinceTo = await _gitClient.ListChangedSinceAsync(worktree, to, cancellationToken).ConfigureAwait(false);

        // Everything below a directory git will not look into - a repository of the agent's own, which status names as the
        // directory - is its own.
        var repositories = (await _gitClient.ReadStatusAsync(worktree, cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.Index == '?' && entry.Path.Text.EndsWith('/'))
            .Select(entry => entry.Path.Text)
            .ToList();
        var atFrom = await _gitClient.HeldAtAsync(worktree, from, committed, cancellationToken).ConfigureAwait(false);
        var atTo = await _gitClient.HeldAtAsync(worktree, to, committed, cancellationToken).ConfigureAwait(false);

        bool InRepository(string path) => repositories.Any(directory => path.StartsWith(directory, StringComparison.Ordinal));

        // Where the worktree's file system finds a name in any case, a path the new base holds that a path the old base
        // held, and the new base does not, spells in another case is that file renamed: what the worktree holds at the new
        // name is the file at the old one, never a file the agent made, and the new name is written once the old is gone.
        var renamed = RenamedInCase(worktree, committed, atFrom, atTo);

        PathKind KindOf(string path) => renamed.ContainsKey(path) ? PathKind.None : _fileSystem.KindOf(Path.Combine(worktree, path));

        // A file the agent made where its base held nothing that is the very file the new base holds there, as git would
        // compare them once its index is the new base's - untracked, staged, or one git ignores: nothing to reconcile.
        var madeAsTo = await _gitClient.ListHeldAsAtAsync(
            worktree,
            to,
            [.. committed.Where(path => atFrom[path].IsNothing && atTo[path].Blob is not null && !InRepository(path) && KindOf(path) is PathKind.File or PathKind.Link)],
            cancellationToken).ConfigureAwait(false);

        // Anything the agent holds where its base held nothing is its own, whatever git makes of it - untracked, staged, or
        // one git ignores, which no status lists - and no diff against a commit says so.
        bool Own(string path) => InRepository(path) || (atFrom[path].IsNothing && KindOf(path) != PathKind.None);

        // Held as the new base holds it, as git compares them. Where its base held nothing, the very file the new base
        // holds, or what a move that stopped part way wrote: any other is two makings of one path, the agent's and the main
        // tree's.
        bool AsTo(string path) => !InRepository(path) && (madeAsTo.Contains(path) || (!sinceTo.Contains(path) && (finishing || !atFrom[path].IsNothing)));

        // Held as its base holds it: nothing where it held nothing; a directory where it held one, whose files are weighed
        // path by path, and anything else of the agent's in it below; and otherwise as git compares them.
        bool AsFrom(string path)
            => !InRepository(path) && atFrom[path] switch
            {
                { IsNothing: true } => KindOf(path) == PathKind.None,
                { IsDirectory: true } => KindOf(path) is PathKind.Directory or PathKind.None,
                _ => !sinceFrom.Contains(path),
            };

        // Written where the agent's worktree may hold a directory, which it would remove or hide: a file, or a submodule's
        // entry its base did not hold.
        bool OverDirectory(string path) => atTo[path].Blob is not null || (atTo[path].Commit is not null && atFrom[path].Commit is null);

        var taken = new List<string>();
        var kept = new List<string>();
        var already = new List<string>();
        var settledHere = new List<string>();
        var refused = new List<(string Path, string Why)>();
        var twice = new List<(string Path, string Why)>();
        var at = OrchestrationReports.Base(from);

        foreach (var path in committed)
        {
            if (shared.Of(path) is not null)
            {
                kept.Add(path);
            }
            else if (AsTo(path))
            {
                already.Add(path);
            }
            else if (AsFrom(path))
            {
                taken.Add(path);
            }
            else if (settledPaths.Contains(path))
            {
                settledHere.Add(path);
            }
            else if (policy.RuleFor(path) == TwoChangeRule.Arms && atFrom[path].IsNothing && !InRepository(path) && KindOf(path) is PathKind.File or PathKind.Link
                && atTo[path].Blob is not null)
            {
                // The arm registry both made is merged by arm from nothing, as no other path both made is.
                twice.Add((path, $"'{path}': the agent made it, and the main tree committed a file there since the agent's base {at}"));
            }
            else if (Own(path))
            {
                refused.Add((path, $"'{path}': the agent made it, and the main tree committed a file there since the agent's base {at}"));
            }
            else if (KindOf(path) == PathKind.None)
            {
                refused.Add((path, $"the agent deleted '{path}', and the main tree committed a change to it since the agent's base {at}"));
            }
            else
            {
                var why = $"'{path}': the agent changed it, and the main tree committed a change to it since the agent's base {at}";

                // Two changes of one file, each holding a file there: what a merge of them gives is measured below - the arm
                // registry's by arm. Anything else the agent holds there - a directory - is refused as it is.
                if (KindOf(path) is PathKind.File or PathKind.Link && atFrom[path].Blob is not null && atTo[path].Blob is not null)
                {
                    twice.Add((path, why));
                }
                else
                {
                    refused.Add((path, why));
                }
            }
        }

        // Every path both changed is weighed in full, asked for or not.
        var weighed = new List<(TwoChangeOffer Offer, string Why, IReadOnlyList<MergeTarget> Into)>();

        if (twice.Count > 0)
        {
            // What each base held there - a link, which is no file to merge, and whether it could be run, never read where
            // the platform keeps no such bit - asked for those paths alone.
            IReadOnlyList<string> asked = [.. twice.Select(two => two.Path)];
            var atFromEntries = await _gitClient.ListEntriesAtAsync(worktree, from, asked, cancellationToken).ConfigureAwait(false);
            var atToEntries = await _gitClient.ListEntriesAtAsync(worktree, to, asked, cancellationToken).ConfigureAwait(false);
            var reached = _fileSystem.ResolveLinks(worktree);

            foreach (var (path, why) in twice)
            {
                var file = Path.Combine(worktree, path);
                var oldEntry = atFromEntries.GetValueOrDefault(path);
                var newEntry = atToEntries.GetValueOrDefault(path);

                // The agent's copy is a link, or reached through one - its directory turned into one - and is never read or
                // written through it: what a merge wrote there would land outside its worktree.
                var linked = ThroughLink(worktree, reached, path);
                var agent = linked ? default : await IdentityAsync(file, cancellationToken).ConfigureAwait(false);
                var offer = await changes.OfferAsync(
                    policy,
                    new TwoChangeAsk(
                        path,
                        atFrom[path].Blob is { } held ? new TwoChangeAncestor.Committed(held, Bit(oldEntry), oldEntry is { IsLink: true }) : null,
                        new TwoChangeSide(new GitContent.Blob(atTo[path].Blob!), Bit(newEntry), newEntry is { IsLink: true }),
                        new TwoChangeSide(new GitContent.File(file), agent.Executable, linked),
                        worktree),
                    cancellationToken).ConfigureAwait(false);

                weighed.Add((offer, why, linked ? [] : [new MergeTarget(file, AgentTreeName, agent)]));
            }
        }

        // The arm registry merged by arm is judged as a registry of the agent's worktree as the move leaves it: each path it
        // takes as the new base holds it, and each merge - read only where the registry cites it.
        await JudgeAsync(
            changes,
            policy,
            weighed,
            worktree,
            RebaseWriter,
            merges => TreeAfter.Moved(_gitClient, worktree, taken.Select(path => (path, atTo[path].Blob)), merges),
            cancellationToken).ConfigureAwait(false);

        // A clean merge that was asked for - or the arm registry's, which reads as one - is taken, and any other ends the
        // path's refusal with what a merge of it gives.
        var merged = new List<MergedPath>();
        var shown = new List<string>();
        var mergeable = new List<string>();

        foreach (var (offer, why, into) in weighed)
        {
            if (TwoChanges.Taken(policy, offer))
            {
                merged.Add(new MergedPath(offer.Path, offer.Working!, offer.Digest!, offer.Executable, offer.Listed, into));
                continue;
            }

            refused.Add((offer.Path, offer.After(why)));
            shown.AddRange(offer.Shown);

            if (offer.IsClean)
            {
                mergeable.Add(offer.Path);
            }
        }

        // What comes in never goes over anything of the agent's: git writes over a file or a link where a path it writes
        // needs a directory, removes a directory, with all it holds, where it writes a file, and hides all a directory holds
        // where it writes a submodule's entry its base did not hold - a submodule's own checkout it leaves alone. What it
        // writes over that the agent holds as its base did is taken with it; a path refused on its own line already says why.
        var takenPaths = taken.ToHashSet(StringComparer.Ordinal);
        var settledThere = settledHere.ToHashSet(StringComparer.Ordinal);
        var refusedPaths = refused.Select(refusal => refusal.Path).ToHashSet(StringComparer.Ordinal);
        var ways = InTheWay(worktree, [.. taken.Where(path => !atTo[path].IsNothing).Select(path => (path, OverDirectory(path)))], takenPaths)
            .Where(way => !refusedPaths.Contains(way.Own))
            .ToList();

        foreach (var way in ways.Where(way => !way.Below).DistinctBy(way => way.Own))
        {
            refused.Add((way.Own, settledThere.Contains(way.Own)
                ? $"'{way.Own}' was declared settled, and the main tree committed '{way.Written}' under it since the agent's base {at}, which needs a directory "
                    + "there, so it cannot stay as the agent's change: reconcile it by hand, and run again without --settled for it"
                : shared.Of(way.Own) is not null
                    ? $"'{way.Own}' is shared with the main tree - handed to the agent, or folded - and the main tree committed '{way.Written}' under it since "
                        + $"the agent's base {at}, which needs a directory there: refresh-agent hands it what the main tree holds there first"
                    : $"'{way.Own}': the agent made it, and the main tree committed '{way.Written}' under it since the agent's base {at}, which needs a "
                        + "directory there: move it aside, and run again"));
        }

        foreach (var written in ways.Where(way => way.Below).GroupBy(way => way.Written, StringComparer.Ordinal))
        {
            var below = written.Select(way => way.Own).Distinct(StringComparer.Ordinal).ToList();
            var sharedBelow = below.Where(path => shared.Of(path) is not null).ToList();
            var settledBelow = below.Where(settledThere.Contains).ToList();
            var ownBelow = below.Except(sharedBelow).Except(settledBelow).ToList();
            var file = atTo[written.Key].Blob is not null;
            var there = $"'{written.Key}': the main tree committed {(file ? "a file" : "a submodule's entry")} there since the agent's base {at}";
            var lost = file ? "remove" : "hide from git";

            if (ownBelow.Count > 0)
            {
                refused.Add((written.Key, $"{there}, where the agent's worktree holds a directory with files of its own - {ReportText.Listed(ownBelow)} - that writing "
                    + $"it would {lost}: move them aside, and run again"));
            }

            if (sharedBelow.Count > 0)
            {
                refused.Add((written.Key, $"{there}, where the agent shares {ReportText.Listed(sharedBelow)} with the main tree, which writing it would {lost}: "
                    + "refresh-agent hands it what the main tree holds there first"));
            }

            if (settledBelow.Count > 0)
            {
                refused.Add((written.Key, $"{there}, and writing it would {lost} {ReportText.Listed(settledBelow)}, declared settled: reconcile them by hand, and "
                    + "run again without --settled for them"));
            }
        }

        foreach (var stray in settledPaths.Where(path => !settledHere.Contains(path, StringComparer.Ordinal)).Order(StringComparer.Ordinal))
        {
            refused.Add((stray, $"--settled '{stray}' names no path the agent changed that the main tree committed a change to since, so it settles nothing: check its spelling"));
        }

        // A path named to be merged that the two commits hold differently and has nothing to merge at is let stand, so the
        // command line that finishes a stopped move runs whatever that move wrote; one they hold alike is the misspelling it
        // most likely is.
        var differing = committed.ToHashSet(StringComparer.Ordinal);

        foreach (var stray in policy.NamedAsText().Where(path => !differing.Contains(path)))
        {
            refused.Add((stray, $"{FoldAllowances.MergeOption} '{stray}' names no path the agent changed that the main tree committed a change to since, so it merges nothing: check its spelling"));
        }

        refused.AddRange(policy.NamedNeverMerged());

        // A file or a submodule's entry where the old base held a directory is written once what was below it is gone, and
        // a file renamed in case alone once its old name is: written first, removing the old name would remove it.
        var removed = taken.Where(path => atTo[path].IsNothing).ToHashSet(StringComparer.Ordinal);
        var last = taken.Where(path => (atFrom[path].IsDirectory && atTo[path] is { IsNothing: false, IsDirectory: false })
            || (renamed.TryGetValue(path, out var old) && removed.Contains(old))).ToList();

        return new RebasePlan
        {
            Taken = taken,
            Kept = kept,
            AlreadyThere = already,
            Settled = settledHere,
            Merged = merged,
            Refusals = [.. refused.OrderBy(refusal => refusal.Path, StringComparer.Ordinal).Select(refusal => refusal.Why)],
            Shown = shown,
            Mergeable = mergeable,
            Written = [.. taken.Except(last, StringComparer.Ordinal)],
            WrittenLast = last,
        };
    }

    /// <summary>
    /// Each path of <paramref name="committed"/> the new base holds that, where <paramref name="worktree"/>'s file system
    /// finds a name in any case, names the same file as a path the old base held and the new base does not: that file
    /// renamed in case alone, each with its old name. Empty where names are told apart by case.
    /// </summary>
    private Dictionary<string, string> RenamedInCase(
        string worktree,
        IReadOnlyList<string> committed,
        IReadOnlyDictionary<string, GitHeld> atFrom,
        IReadOnlyDictionary<string, GitHeld> atTo)
    {
        var names = PathCase.In(_fileSystem, worktree);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);

        if (names.Equals("a", "A"))
        {
            var old = committed.Where(path => !atFrom[path].IsNothing && atTo[path].IsNothing).ToLookup(path => path, names);

            foreach (var path in committed.Where(path => atFrom[path].IsNothing && !atTo[path].IsNothing))
            {
                if (old[path].FirstOrDefault(other => other != path) is { } before)
                {
                    renamed[path] = before;
                }
            }
        }

        return renamed;
    }

    /// <summary>
    /// What the agent holds of its own that handing it <paramref name="handable"/> would write over or through - a file or a
    /// link where a file handed needs a directory, and what a directory holds where a file handed stands in its place - each
    /// once; what the hand-over itself deletes is in the way of nothing.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="handable">What it would be handed.</param>
    /// <exception cref="IOException">A path could not be looked at.</exception>
    public IReadOnlyList<string> InTheWayOfHanding(string worktree, Handable handable)
    {
        ArgumentNullException.ThrowIfNull(handable);

        return [.. WaysOfHanding(worktree, handable).Select(way => way.Own).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>What of the agent's own handing it <paramref name="handable"/> would write over or through, each with the path handed it is in the way of.</summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="handable">What it would be handed.</param>
    /// <exception cref="IOException">A path could not be looked at.</exception>
    private List<InTheWayOf> WaysOfHanding(string worktree, Handable handable)
        => InTheWay(worktree, [.. handable.Files.Select(path => (path, true))], handable.Deletions.ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// What of the agent's own writing <paramref name="written"/> into its worktree would go over or through, each with the
    /// path written it is in the way of: above it, the first part of its path that is a file or a link where it needs a
    /// directory; below it, where it is written as a file and the worktree holds a directory there, everything that
    /// directory holds - every file, and every link to a directory, which a walk never follows. What
    /// <paramref name="replaced"/> names the same writing replaces, and is in the way of nothing.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="written">Each path written, and whether it is written as a file.</param>
    /// <param name="replaced">The paths the same writing replaces or removes.</param>
    /// <exception cref="IOException">A path could not be looked at.</exception>
    private List<InTheWayOf> InTheWay(string worktree, IReadOnlyList<(string Path, bool AsFile)> written, IReadOnlySet<string> replaced)
    {
        var found = new List<InTheWayOf>();

        foreach (var (path, asFile) in written)
        {
            foreach (var part in Leading(path))
            {
                var kind = _fileSystem.KindOf(Path.Combine(worktree, part));

                if (kind == PathKind.Directory)
                {
                    continue;
                }

                if (kind != PathKind.None && !replaced.Contains(part))
                {
                    found.Add(new InTheWayOf(part, path, Below: false));
                }

                break;
            }

            var directory = Path.Combine(worktree, path);

            if (asFile && _fileSystem.KindOf(directory) == PathKind.Directory)
            {
                found.AddRange(_fileSystem.EnumerateFiles(directory, recursive: true)
                    .Concat(_fileSystem.EnumerateDirectoryLinks(directory))
                    .Select(held => $"{path}/{Path.GetRelativePath(directory, held).Replace(Path.DirectorySeparatorChar, '/')}")
                    .Where(held => !replaced.Contains(held))
                    .Order(StringComparer.Ordinal)
                    .Select(held => new InTheWayOf(held, path, Below: true)));
            }
        }

        return found;
    }

    /// <summary>The paths of the directories <paramref name="path"/> lies in, outermost first: <c>a</c> and <c>a/b</c> for <c>a/b/c</c>.</summary>
    private static IEnumerable<string> Leading(string path)
    {
        for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            yield return path[..slash];
        }
    }

    /// <summary>
    /// Which of <paramref name="paths"/> the agent changed: its copy is not what it shares with the main tree - a file with
    /// other content or mode, absent where a file was, or a file where none was - or, for a path it shares nothing for,
    /// differs from its base as git status compares, or is a file its base does not hold. Handing the main tree's over any
    /// of these would undo the agent's work unseen.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="baseCommit">The commit it was made from.</param>
    /// <param name="seed">What it shares with the main tree.</param>
    /// <param name="paths">The paths to ask about.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public Task<IReadOnlyList<string>> EditedAsync(string worktree, string baseCommit, SeedRecord seed, IReadOnlyList<string> paths, CancellationToken cancellationToken)
        => DiffersAsync(worktree, baseCommit, seed, paths, movedSinceBase: null, cancellationToken);

    /// <summary>
    /// Which of <paramref name="paths"/> the tree at <paramref name="tree"/> - the agent's worktree or the main tree - holds
    /// otherwise than the agent shares with the main tree: not what its seed records both trees held - a file with other
    /// content or mode, absent where a file was, or a file where none was - or, for a path it shares nothing for, not what
    /// the agent's base holds, as git status compares, or a file its base does not hold.
    /// </summary>
    /// <param name="tree">The tree to ask about.</param>
    /// <param name="baseCommit">The commit the agent's worktree was made from.</param>
    /// <param name="seed">What the agent shares with the main tree.</param>
    /// <param name="paths">The paths to ask about.</param>
    /// <param name="movedSinceBase">What git already said the tree changes since the base; asked where null.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    private async Task<IReadOnlyList<string>> DiffersAsync(
        string tree,
        string baseCommit,
        SeedRecord seed,
        IReadOnlyList<string> paths,
        IReadOnlySet<string>? movedSinceBase,
        CancellationToken cancellationToken)
    {
        var shared = new Baselines(seed);
        var differs = new List<string>();
        var unseeded = new List<(string Path, Side Side)>();

        foreach (var path in paths)
        {
            // A link the tree holds at the path is neither the file it leads to nor an absence: it differs, as a change of
            // its own - never read as one, which is what SideAsync raises for.
            if (_fileSystem.KindOf(Path.Combine(tree, path)) == PathKind.Link)
            {
                differs.Add(path);
                continue;
            }

            var side = await SideAsync(Path.Combine(tree, path), cancellationToken).ConfigureAwait(false);

            if (shared.Of(path) is { } baseline)
            {
                if (side != baseline)
                {
                    differs.Add(path);
                }
            }
            else
            {
                unseeded.Add((path, side));
            }
        }

        if (unseeded.Count > 0)
        {
            var based = await _gitClient.HeldAtAsync(tree, baseCommit, [.. unseeded.Select(entry => entry.Path)], cancellationToken).ConfigureAwait(false);
            IReadOnlySet<string> moved = movedSinceBase ?? (unseeded.Any(entry => !based[entry.Path].IsNothing && !based[entry.Path].IsDirectory)
                ? await _gitClient.ListChangedSinceAsync(tree, baseCommit, cancellationToken).ConfigureAwait(false)
                : new HashSet<string>(StringComparer.Ordinal));

            // A file or a submodule its base holds differs where git says the tree moved from it, its deletion included; a
            // directory its base holds, whose files are weighed path by path, differs where a file stands in its place; and a
            // path its base holds nothing at differs where anything is there.
            differs.AddRange(unseeded
                .Where(entry => based[entry.Path] switch
                {
                    { IsNothing: true } => entry.Side.Kind != Kind.Absent,
                    { IsDirectory: true } => entry.Side.Kind == Kind.File,
                    _ => moved.Contains(entry.Path),
                })
                .Select(entry => entry.Path));
        }

        return [.. differs.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The paths the agent's worktree changes that are its own: every one its status lists, less those it shares with the
    /// main tree and still holds as both trees held them - a copy it was handed and left alone is not its change.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="seed">What it shares with the main tree, where it was ever seeded.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<IReadOnlyList<string>> OwnAsync(string worktree, SeedRecord? seed, IReadOnlyList<string> floor, CancellationToken cancellationToken)
    {
        var changed = await ChangedAsync(worktree, floor, "what the agent changed cannot be told", within: null, cancellationToken).ConfigureAwait(false);

        if (seed is null)
        {
            return changed;
        }

        var shared = new Baselines(seed);
        var own = new List<string>();

        foreach (var path in changed)
        {
            // A link the agent holds there is its own change, never the file it leads to read as one.
            if (shared.Of(path) is not { } baseline
                || _fileSystem.KindOf(Path.Combine(worktree, path)) == PathKind.Link
                || await SideAsync(Path.Combine(worktree, path), cancellationToken).ConfigureAwait(false) != baseline)
            {
                own.Add(path);
            }
        }

        return own;
    }

    /// <summary>Whether the main tree's copy at <paramref name="main"/> is already what the agent holds at <paramref name="agent"/>: both files and the same file, or both absent.</summary>
    /// <param name="main">Where the main tree holds a path, or where one may be.</param>
    /// <param name="agent">Where the agent holds it.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<bool> SameAsync(string main, string agent, CancellationToken cancellationToken)
        => await SideAsync(main, cancellationToken).ConfigureAwait(false) == await SideAsync(agent, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Whether the agent already holds what the main tree does at <paramref name="path"/>, asked of a copy the main tree
    /// moved or a stale name weighs: as <see cref="SameAsync"/> finds it, but a link the agent holds there never is that -
    /// a link is no copy of anything, and what it leads to is its own change to name, never a file to read.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="path">The path, relative to the tree.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    private async Task<bool> HoldsAlikeAsync(string main, string worktree, string path, CancellationToken cancellationToken)
        => _fileSystem.KindOf(Path.Combine(worktree, path)) != PathKind.Link
            && await SameAsync(Path.Combine(main, path), Path.Combine(worktree, path), cancellationToken).ConfigureAwait(false);

    /// <summary>Whether the file at <paramref name="path"/> could be run - its execute bit set - on a platform with one; never on Windows, which has none.</summary>
    /// <param name="path">A file.</param>
    public bool IsExecutable(string path) => _platform.Current != PlatformId.Windows && _permissions.IsExecutable(path);

    /// <summary>
    /// Every path a fold of the agent weighs: each its status lists, and each it shares with the main tree, whether its
    /// status lists it or not - one it put back as its base holds leaves its status, and is still its change to fold.
    /// </summary>
    private async Task<IReadOnlyList<string>> CandidatesAsync(string worktree, SeedRecord? seed, IReadOnlyList<string> floor, string consequence, CancellationToken cancellationToken)
        => [.. (await ChangedAsync(worktree, floor, consequence, within: null, cancellationToken).ConfigureAwait(false))
            .Concat((seed?.Weighed ?? []).Where(path => !TreeFloor.Covers(floor, path)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>Whether a commit's entry is one that can be run, where the platform keeps such a bit: never on Windows, which keeps none.</summary>
    private bool Bit(GitEntry? entry) => _platform.Current != PlatformId.Windows && entry is { IsExecutable: true };

    /// <summary>
    /// Whether <paramref name="path"/> in the tree at <paramref name="root"/> is a link, or is reached through one along it:
    /// what it leads to is not the path git names. One that cannot be followed is taken for one.
    /// </summary>
    /// <param name="root">The tree.</param>
    /// <param name="reached">Where the tree's root itself leads.</param>
    /// <param name="path">The path, relative to the tree.</param>
    private bool ThroughLink(string root, string reached, string path)
    {
        try
        {
            return LinkPaths.LeadsElsewhere(_fileSystem, root, reached, path, _platform.PathComparison) is not null;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>Whether both trees hold a file at the path looked at: what a merge of two changes needs of each.</summary>
    private static bool BothHoldAFile(Look look) => look is { Main.Kind: Kind.File, Agent.Kind: Kind.File };

    /// <summary>
    /// What a path that is or lies in a submodule of the main tree's is, to stand in brackets as one a fold never writes:
    /// the submodule's own path is a directory in the main tree, as any directory git will not look into is named.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="gitlink">The submodule's path.</param>
    private static string InMainSubmodule(string path, string gitlink)
        => path == gitlink ? MainDirectory : $"in '{gitlink}', a submodule of the main tree's - a repository of its own";

    /// <summary>
    /// What the path looked at is that a fold never reads or writes - reached through a link, or a directory git will not
    /// look into, in either tree - to stand in brackets; null where it is neither.
    /// </summary>
    private static string? Unread(Look look)
        => look.Link is { } link ? $"reached through a symbolic link or junction in {link}"
            : look.Agent.Kind == Kind.Directory ? AgentDirectory
            : look.Main.Kind == Kind.Directory ? MainDirectory
            : null;

    /// <summary>
    /// Why a path declared settled by hand that only the agent changed is refused: the main tree holds it as the agent last
    /// shared it - or, for a path it shares nothing for, as its base does - so nothing was reconciled there, and what
    /// settling hands would undo the agent's change.
    /// </summary>
    /// <param name="look">What both trees hold at the path.</param>
    /// <param name="shared">Whether the agent shares the path with the main tree.</param>
    /// <param name="at">The agent's base, as a report names it.</param>
    private static string NothingReconciled(Look look, bool shared, string at)
    {
        var absent = look.Main.Kind == Kind.Absent;
        var holds = (absent, shared) switch
        {
            (false, true) => "the main tree holds it as it did when the agent last shared it",
            (true, true) => "the main tree holds no file there, as when the agent last shared it",
            (false, false) => $"the main tree holds it as the agent's base {at} does",
            (true, false) => $"the main tree holds no file there, as the agent's base {at} holds none",
        };

        return $"{FoldAllowances.SettledOption} '{look.Path}': {holds}, so nothing was reconciled there, and "
            + $"{(absent ? "removing the agent's copy" : "handing the agent the main tree's copy")} would undo the agent's change: drop it";
    }

    /// <summary>What the two trees hold at <paramref name="path"/>, and whether a link lies along it in either.</summary>
    private async Task<Look> LookAsync(string path, Roots roots, CancellationToken cancellationToken)
    {
        var comparison = _platform.PathComparison;
        string? inMain;
        string? inAgent;

        try
        {
            // A path from git never passes through a link in the tree git read it from; the same path in the other tree
            // can, and anything written or read through it would be another file than the one measured.
            inMain = LinkPaths.LeadsElsewhere(_fileSystem, roots.Main, roots.ReachedMain, path, comparison);
            inAgent = LinkPaths.LeadsElsewhere(_fileSystem, roots.Agent, roots.ReachedAgent, path, comparison);
        }
        catch (IOException ex)
        {
            return new Look(path, $"a link that cannot be followed ({ex.Message.TrimEnd('.')})", Escapes: false, default, default);
        }

        if (inMain is not null)
        {
            return PathContainment.IsStrictlyInside(roots.ReachedMain, inMain, comparison)
                ? new Look(path, $"the main tree, where it leads to '{inMain}'", Escapes: false, default, default)
                : new Look(path, $"a link in the main tree, to '{inMain}'", Escapes: true, default, default);
        }

        if (inAgent is not null)
        {
            return new Look(path, $"the agent's worktree, where it leads to '{inAgent}'", Escapes: false, default, default);
        }

        return new Look(
            path,
            Link: null,
            Escapes: false,
            await SideAsync(Path.Combine(roots.Agent, path), cancellationToken).ConfigureAwait(false),
            await SideAsync(Path.Combine(roots.Main, path), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// What one tree holds at <paramref name="path"/>, asked so that a path this process cannot look at raises: read as
    /// absent, an agent's file would be taken for its deletion of the main tree's. A directory is a repository of its own
    /// where it holds its own <c>.git</c>, which git will not look into; any other holds no file there, its own files
    /// weighed on paths of their own - so a file turned into a directory is the file's deletion.
    /// </summary>
    private async Task<Side> SideAsync(string path, CancellationToken cancellationToken)
        => _fileSystem.KindOf(path) switch
        {
            PathKind.File => new Side(Kind.File, await IdentityAsync(path, cancellationToken).ConfigureAwait(false)),
            PathKind.Directory when _fileSystem.KindOf(Path.Combine(path, ".git")) != PathKind.None => new Side(Kind.Directory, null),
            PathKind.Directory or PathKind.None => new Side(Kind.Absent, null),
            _ => throw new IOException($"'{path}' is a link, and a link is never read as the file it leads to."),
        };

    /// <summary>What the file at <paramref name="path"/> is: its content, and whether it could be run.</summary>
    private async Task<FileIdentity> IdentityAsync(string path, CancellationToken cancellationToken)
        => new((await FileContentHash.OfAsync(_fileSystem, path, cancellationToken).ConfigureAwait(false)).Content, IsExecutable(path));

    /// <summary>
    /// The digest of the file at <paramref name="path"/>, or null where it holds none; a path that cannot be looked at
    /// raises. A link holds the file it leads to, where it leads to one; a link to a directory, or to nothing, holds none.
    /// </summary>
    private async Task<string?> DigestAsync(string path, CancellationToken cancellationToken)
        => _fileSystem.KindOf(path) switch
        {
            PathKind.File => (await FileContentHash.OfAsync(_fileSystem, path, cancellationToken).ConfigureAwait(false)).Content,
            PathKind.Link when _fileSystem.FileExists(path) => (await FileContentHash.OfAsync(_fileSystem, path, cancellationToken).ConfigureAwait(false)).Content,
            _ => null,
        };

    /// <summary>
    /// Stops a write or a removal where the file at <paramref name="file"/>, in the tree named <paramref name="tree"/>, no
    /// longer holds what was measured - another program wrote it since - so nothing is lost that was never weighed.
    /// </summary>
    /// <param name="file">The file about to be written over or removed.</param>
    /// <param name="path">Its path in the tree, as the stop names it.</param>
    /// <param name="measured">The digest it held when it was measured; null where it held no file.</param>
    /// <param name="tree">The tree it is in, as the stop names it.</param>
    /// <param name="what">What was not done to it, to end the sentence.</param>
    private async Task RequireMeasuredAsync(string file, string path, string? measured, string tree, string what)
    {
        var now = _fileSystem.KindOf(file) switch
        {
            PathKind.File => (await FileContentHash.OfAsync(_fileSystem, file, CancellationToken.None).ConfigureAwait(false)).Content,
            PathKind.None => null,
            _ => throw new IOException($"'{path}' is no longer a file in {tree}, so it was not {what}"),
        };

        if (now != measured)
        {
            throw new IOException($"'{path}' holds other content in {tree} now than when it was measured, so it was not {what}");
        }
    }

    /// <summary>
    /// Stops a write over or a removal of the file at <paramref name="file"/> where it is no longer the file weighed - its
    /// content, or, where the platform keeps one, its bit: a file made runnable since is another file, and writing over or
    /// removing it would take that change away unrecorded.
    /// </summary>
    /// <param name="file">The file about to be written over or removed.</param>
    /// <param name="path">Its path in the tree, as the stop names it.</param>
    /// <param name="weighed">What it was when it was weighed.</param>
    /// <param name="tree">The tree it is in, as the stop names it.</param>
    /// <param name="what">What was not done to it, to end the sentence.</param>
    private async Task RequireWeighedAsync(string file, string path, FileIdentity weighed, string tree, string what)
    {
        await RequireMeasuredAsync(file, path, weighed.Digest, tree, what).ConfigureAwait(false);

        if (IsExecutable(file) != weighed.Executable)
        {
            throw new IOException(weighed.Executable
                ? $"'{path}' can no longer be run in {tree}, which it could when it was measured, so it was not {what}"
                : $"'{path}' can now be run in {tree}, which it could not when it was measured, so it was not {what}");
        }
    }

    /// <summary>What a path is in one tree.</summary>
    private enum Kind
    {
        Absent,
        File,
        Directory,
    }

    /// <summary>What a tree holds at a path, as what the main tree did since the agent's base and how is weighed.</summary>
    internal enum Holds
    {
        /// <summary>Nothing.</summary>
        Nothing,

        /// <summary>A directory: one with no <c>.git</c> of its own, a repository of its own, or a commit's tree.</summary>
        Directory,

        /// <summary>A file, or a link: a commit's blob.</summary>
        File,
    }

    /// <summary>What git's entry holds, of what <see cref="Holds"/> tells apart.</summary>
    /// <param name="entry">A commit's entry at a path, or none.</param>
    internal static Holds HoldsOf(GitEntry? entry) => entry switch
    {
        null => Holds.Nothing,
        { IsBlob: true } => Holds.File,
        _ => Holds.Directory,
    };

    /// <summary>What the main tree holds at the path <paramref name="look"/> looked at.</summary>
    /// <param name="main">The main tree.</param>
    /// <param name="look">What both trees hold at the path.</param>
    private Holds HoldsAt(string main, Look look) => look.Main.Kind switch
    {
        Kind.File => Holds.File,
        Kind.Directory => Holds.Directory,

        // A directory with no .git of its own holds no file at its path, and is a directory all the same.
        Kind.Absent => _fileSystem.KindOf(Path.Combine(main, look.Path)) == PathKind.Directory ? Holds.Directory : Holds.Nothing,
        var kind => throw new ArgumentOutOfRangeException(nameof(look), kind, "Not what a tree holds at a path."),
    };

    /// <summary>
    /// What the main tree did since the agent's base at a path the agent shares nothing for: what it holds there now, and
    /// the words that say it.
    /// </summary>
    internal abstract record Since
    {
        /// <summary>What the main tree holds at the path now.</summary>
        public abstract Holds Now { get; }

        /// <summary>
        /// What the main tree did at a path since the agent's base, where it did anything - the one rule every weighing of
        /// such a path reads: made a file where the base holds none, removed the base's file, made a directory of it, or
        /// changed it; <see langword="null"/> where the main tree holds what the base holds, a file or none.
        /// </summary>
        /// <param name="baseBlob">The blob the base holds at the path, or none where it holds no file there.</param>
        /// <param name="now">What the main tree holds there now.</param>
        /// <param name="moved">Whether git says the main tree moved from the base there, as git status compares.</param>
        public static Since? Of(string? baseBlob, Holds now, bool moved) => (baseBlob, now) switch
        {
            (null, Holds.File) => new Made(),
            (null, Holds.Nothing or Holds.Directory) => null,
            ({ } blob, Holds.Nothing) => new Removed(blob),
            ({ } blob, Holds.Directory) => new MadeADirectory(blob),
            ({ } blob, Holds.File) => moved ? new Changed(blob) : null,
            var unknown => throw new ArgumentOutOfRangeException(nameof(now), unknown, "Not what a main tree holds at a path."),
        };

        /// <summary>The refusal of the path, saying how the main tree came to hold what it holds there.</summary>
        /// <param name="path">The path.</param>
        /// <param name="at">The agent's base, as a line names it.</param>
        /// <param name="came">How the main tree came to hold it.</param>
        /// <param name="agentRemoved">Whether the agent removed the path, rather than holding a file there.</param>
        public abstract string Said(string path, string at, Came came, bool agentRemoved);

        /// <summary>
        /// What it did, short, as a list of files in the way of the agent's says each: <paramref name="since"/> naming the
        /// agent's base, or the base already named.
        /// </summary>
        /// <param name="since">When from: since the agent's base, named, or since then.</param>
        /// <param name="came">How the main tree came to hold it.</param>
        public abstract string Short(string since, Came came);

        /// <summary>Made a file where the base holds none, as the agent did - or holds one git ignores, which says nothing of when it came.</summary>
        public sealed record Made : Since
        {
            public override Holds Now => Holds.File;

            public override string Said(string path, string at, Came came, bool agentRemoved)
            {
                ArgumentNullException.ThrowIfNull(came);

                return came.Ignored
                    ? $"'{path}' is not at the agent's base {at}, and the main tree holds a file there that git ignores, {Lose(agentRemoved, "that file")}"
                    : $"'{path}' is not at the agent's base {at}, and the main tree made it since, {came.How}, {Lose(agentRemoved, "that change")}";
            }

            public override string Short(string since, Came came)
            {
                ArgumentNullException.ThrowIfNull(came);

                return came.Ignored ? "ignored by git" : $"made {since} {came.How}";
            }
        }

        /// <summary>
        /// Did something to the file the base holds there, whose blob is <paramref name="BaseBlob"/>: what a merge of the
        /// two is measured from, where both trees hold a file.
        /// </summary>
        /// <param name="BaseBlob">The blob the base holds at the path.</param>
        public abstract record OfABaseFile(string BaseBlob) : Since;

        /// <summary>Changed the file the base holds there.</summary>
        /// <param name="BaseBlob">The blob the base holds at the path.</param>
        public sealed record Changed(string BaseBlob) : OfABaseFile(BaseBlob)
        {
            public override Holds Now => Holds.File;

            public override string Said(string path, string at, Came came, bool agentRemoved)
            {
                ArgumentNullException.ThrowIfNull(came);

                return $"'{path}': the main tree changed it after the agent's base {at}, {came.How}, {Lose(agentRemoved, "that change")}";
            }

            public override string Short(string since, Came came)
            {
                ArgumentNullException.ThrowIfNull(came);

                return $"changed {since} {came.How}";
            }
        }

        /// <summary>Made a directory of the file the base holds there.</summary>
        /// <param name="BaseBlob">The blob the base holds at the path.</param>
        public sealed record MadeADirectory(string BaseBlob) : OfABaseFile(BaseBlob)
        {
            public override Holds Now => Holds.Directory;

            public override string Said(string path, string at, Came came, bool agentRemoved)
            {
                ArgumentNullException.ThrowIfNull(came);

                return $"'{path}' is at the agent's base {at}, and the main tree holds a directory there now, made {came.How}, {Lose(agentRemoved, "that change")}";
            }

            public override string Short(string since, Came came)
            {
                ArgumentNullException.ThrowIfNull(came);

                return $"made a directory {since} {came.How}";
            }
        }

        /// <summary>Removed the file the base holds there.</summary>
        /// <param name="BaseBlob">The blob the base holds at the path.</param>
        public sealed record Removed(string BaseBlob) : OfABaseFile(BaseBlob)
        {
            public override Holds Now => Holds.Nothing;

            public override string Said(string path, string at, Came came, bool agentRemoved)
            {
                ArgumentNullException.ThrowIfNull(came);

                return $"'{path}' is at the agent's base {at}, and missing from the main tree, removed {came.How}, {Lose(agentRemoved, "that change")}";
            }

            public override string Short(string since, Came came)
            {
                ArgumentNullException.ThrowIfNull(came);

                return $"removed {since} {came.How}";
            }
        }
    }

    /// <summary>
    /// Whether a path lies in a repository the main tree records as a submodule - a gitlink in its index above the path -
    /// and whether its HEAD holds that record too. git looks at nothing below one.
    /// </summary>
    internal enum Submodule
    {
        /// <summary>In none: git looks at the path itself.</summary>
        None,

        /// <summary>In one the index holds and HEAD does not: staged, uncommitted.</summary>
        Staged,

        /// <summary>In one HEAD holds.</summary>
        Committed,
    }

    /// <summary>What an uncommitted edit did at a path, from what the main tree's HEAD holds there to what the main tree holds.</summary>
    private enum Edit
    {
        /// <summary>Nothing: the main tree holds what its HEAD holds.</summary>
        None,

        /// <summary>Changed what its HEAD holds there, a file or a directory still.</summary>
        Changed,

        /// <summary>Made of it something of another kind than its HEAD holds there: a file, a directory, or nothing.</summary>
        Replaced,

        /// <summary>Made something its HEAD holds nothing of, which git says it ignores, so its status lists nothing there.</summary>
        Ignored,
    }

    /// <summary>How the main tree came to hold what it holds at a path since the agent's base.</summary>
    /// <param name="Now">What the main tree holds there now.</param>
    /// <param name="Based">The base's entry there, or none.</param>
    /// <param name="Head">
    /// Its HEAD's entry there - blob and mode, as git compares a commit - or none: found as the main tree's git spells the
    /// path, which where the repository ignores case may differ from the agent's spelling in case alone.
    /// </param>
    /// <param name="HeadSpelledOtherwise">Whether its HEAD spells the path otherwise than the agent does: a rename of case alone.</param>
    /// <param name="Listed">
    /// Whether git status, untracked files listed and its paths compared as the repository's <c>core.ignorecase</c> says,
    /// lists the path, anything below it, or a directory above it that it folds into one entry - an untracked repository
    /// nested in the tree, which it does not look into: false where the main tree holds there what its HEAD holds, what
    /// git ignores, or an empty directory.
    /// </param>
    /// <param name="GitIgnores">
    /// Whether git says it ignores the path, as <c>git add</c> judges it: asked only where its status lists nothing at the
    /// path, below it or above it, HEAD holds nothing there, and it lies in no submodule.
    /// </param>
    /// <param name="InSubmodule">
    /// Whether the path lies in a submodule of the main tree's, staged or committed: what is there came as that record
    /// did - and <paramref name="Listed"/> then says whether git status lists the record.
    /// </param>
    internal sealed record Came(Holds Now, GitEntry? Based, GitEntry? Head, bool HeadSpelledOtherwise, bool Listed, bool GitIgnores, Submodule InSubmodule)
    {
        /// <summary>
        /// Whether the main tree holds there something git ignores where neither its HEAD nor the base holds anything: git
        /// never recorded it, so nothing says when it came, and no refusal claims it came since the base.
        /// </summary>
        public bool Ignored => Edited == Edit.Ignored && !Committed;

        /// <summary>
        /// How, in the one wording every refusal of a path the main tree moved since the agent's base says it: by a commit
        /// alone where its HEAD already holds what the main tree holds there now; by an uncommitted edit alone where its HEAD
        /// holds what the base holds; and where a commit moved it and an edit moved it again, both, each with what it did -
        /// the edit after what the commit did where the edit made it of another kind than HEAD holds, an edit git ignores
        /// where what it made is ignored, and the commit then the edit's change where the commit already made it of that
        /// kind.
        /// </summary>
        /// <exception cref="InvalidOperationException">It is <see cref="Ignored"/>: nothing says how it came.</exception>
        public string How => (Edited, Committed) switch
        {
            (Edit.Ignored, false) => throw new InvalidOperationException("What git ignores where neither HEAD nor the base holds anything came no way a refusal can say."),

            // Nothing edited and nothing committed is git's two answers read at different moments: the main tree moved, uncommitted.
            (Edit.None or Edit.Changed or Edit.Replaced, false) => "by an uncommitted edit",
            (Edit.None, true) => "by a commit",
            (Edit.Changed, true) => "by a commit, then changed by an uncommitted edit",
            (Edit.Replaced, true) => $"by an uncommitted edit, after a commit {Did}",
            (Edit.Ignored, true) => $"by an uncommitted edit git ignores, after a commit {Did}",
            var unknown => throw new ArgumentOutOfRangeException(nameof(Edited), unknown, "Not something an uncommitted edit did."),
        };

        /// <summary>Whether a commit moved the path: its HEAD's entry there not the base's, or spelled otherwise.</summary>
        private bool Committed => InSubmodule switch
        {
            Submodule.None => Head != Based || HeadSpelledOtherwise,

            // Below a submodule, what is there came as its record did: by the commit that holds it, or staged.
            Submodule.Staged => false,
            Submodule.Committed => true,
            var unknown => throw new ArgumentOutOfRangeException(nameof(InSubmodule), unknown, "Not where a path lies."),
        };

        /// <summary>What an uncommitted edit did there, from its HEAD's entry to what the main tree holds.</summary>
        private Edit Edited => (InSubmodule, Listed, HoldsOf(Head) == Now) switch
        {
            // Below a submodule HEAD holds, an edit changed it where git status lists the record; below one only staged,
            // the staging is the edit.
            (Submodule.Committed, true, _) => Edit.Changed,
            (Submodule.Committed or Submodule.Staged, _, _) => Edit.None,

            // Nothing there, as HEAD holds nothing: nothing in the tree an edit changed, whatever the index stages.
            (Submodule.None, _, true) when Now == Holds.Nothing => Edit.None,
            (Submodule.None, true, true) => Edit.Changed,
            (Submodule.None, true, false) => Edit.Replaced,
            (Submodule.None, false, true) => Edit.None,

            // Unlisted, yet of another kind than HEAD holds: what git says it ignores, and otherwise an edit all the same -
            // an empty directory, of which git lists nothing, or git's two answers read at different moments.
            (Submodule.None, false, false) => GitIgnores ? Edit.Ignored : Edit.Replaced,
            var unknown => throw new ArgumentOutOfRangeException(nameof(InSubmodule), unknown.Item1, "Not where a path lies."),
        };

        /// <summary>What the commit did there, from the base's entry to its HEAD's, where an edit made of it another kind since.</summary>
        private string Did => (HoldsOf(Based), HoldsOf(Head)) switch
        {
            (Holds.File, Holds.File) when Head == Based => "renamed it",
            (Holds.File, Holds.File) => "changed it",
            (Holds.File, Holds.Nothing) => "removed it",
            (Holds.File, Holds.Directory) => "made a directory of it",
            (Holds.Directory, Holds.Directory) when Head == Based => "renamed the directory there",
            (Holds.Directory, Holds.Directory) => "changed the directory there",
            (Holds.Directory, Holds.Nothing) => "removed the directory there",
            (Holds.Nothing, Holds.Directory) => "made a directory there",
            var unsaid => throw new InvalidOperationException($"No refusal says a commit took {unsaid.Item1} to {unsaid.Item2} before an edit made of it another kind."),
        };
    }

    /// <summary>
    /// What git answers of the main tree in one measuring of a fold, each question asked once, and only where something
    /// asks it: whether it moved from the agent's base, what its status lists, whether it ignores case, what its HEAD
    /// holds, which submodules its index records, and which paths it ignores.
    /// </summary>
    /// <param name="git">Asks git.</param>
    /// <param name="main">The main tree.</param>
    /// <param name="baseCommit">The agent's base.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    private sealed class MainTreeAnswers(IGitClient git, string main, string baseCommit, CancellationToken cancellationToken)
    {
        private IReadOnlySet<string>? _movedFromBase;
        private IReadOnlyList<(string Path, bool Folded)>? _listed;
        private bool? _ignoresCase;
        private IReadOnlyList<string>? _headFiles;
        private readonly Dictionary<string, bool> _ignored = new(StringComparer.Ordinal);
        private Submodules? _submodules;

        /// <summary>Every path where the main tree holds otherwise than the base, as git status compares.</summary>
        public async Task<IReadOnlySet<string>> MovedFromBaseAsync()
            => _movedFromBase ??= await git.ListChangedSinceAsync(main, baseCommit, cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// Whether git status, untracked files listed one by one and ignored ones not, lists <paramref name="path"/>,
        /// anything below it, or a directory above it that it folds into one entry - an untracked repository nested in the
        /// tree, which git does not look into: the main tree holds there otherwise than its HEAD, or something untracked.
        /// </summary>
        /// <param name="path">The path, as the agent spells it.</param>
        public async Task<bool> ListsAsync(string path)
        {
            _listed ??= [.. (await git.ReadStatusAsync(main, cancellationToken).ConfigureAwait(false))
                .SelectMany(entry => entry.Paths)
                .Where(name => name.IsUtf8)
                .Select(name => (name.Text.TrimEnd('/'), name.Text.EndsWith('/')))];

            var comparison = await IgnoresCaseAsync().ConfigureAwait(false) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            return _listed.Any(listed => string.Equals(listed.Path, path, comparison)
                || Below(listed.Path, path, comparison)
                || (listed.Folded && Below(path, listed.Path, comparison)));
        }

        /// <summary>The submodule of the main tree's each of <paramref name="paths"/> is or lies in, where one does (<see cref="Submodules"/>).</summary>
        /// <param name="paths">The paths, as the agent spells them.</param>
        public Task<IReadOnlyDictionary<string, (string Gitlink, Submodule In)>> SubmodulesAsync(IReadOnlyList<string> paths)
            => (_submodules ??= new Submodules(git, main, IgnoresCaseAsync, cancellationToken)).OfAsync(paths);

        /// <summary>
        /// Which of <paramref name="paths"/> git says it ignores, as <c>git add</c> judges them: one process for the paths
        /// not asked of before, none where there are none.
        /// </summary>
        /// <param name="paths">The paths, as the agent spells them.</param>
        public async Task<IReadOnlySet<string>> IgnoredAsync(IReadOnlyList<string> paths)
        {
            var unasked = paths.Where(path => !_ignored.ContainsKey(path)).Distinct(StringComparer.Ordinal).ToList();

            if (unasked.Count > 0)
            {
                var ignored = await git.FindIgnoredAsync(main, unasked, cancellationToken).ConfigureAwait(false);

                foreach (var path in unasked)
                {
                    _ignored[path] = ignored.Contains(path);
                }
            }

            return paths.Where(path => _ignored[path]).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>
        /// Its HEAD's entry at each of <paramref name="paths"/>, found as the main tree's git spells the path: as asked, and
        /// where the repository ignores case and HEAD holds nothing so spelled, as HEAD spells it in another case - with
        /// whether it is spelled otherwise.
        /// </summary>
        /// <param name="paths">The paths, as the agent spells them.</param>
        public async Task<IReadOnlyDictionary<string, (GitEntry Entry, bool SpelledOtherwise)>> HeadAsync(IReadOnlyList<string> paths)
        {
            var exact = await git.ListEntriesAtAsync(main, "HEAD", paths, cancellationToken).ConfigureAwait(false);
            var found = exact.ToDictionary(held => held.Key, held => (held.Value, SpelledOtherwise: false), StringComparer.Ordinal);
            var missing = paths.Where(path => !exact.ContainsKey(path)).ToList();

            if (missing.Count == 0 || !await IgnoresCaseAsync().ConfigureAwait(false))
            {
                return found;
            }

            _headFiles ??= [.. (await git.ListFilesAtCommitAsync(main, "HEAD", cancellationToken).ConfigureAwait(false)).Where(name => name.IsUtf8).Select(name => name.Text)];

            // A file spelled so in another case, or the directory a file lies in.
            var spelled = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var path in missing)
            {
                var spelling = _headFiles.FirstOrDefault(file => string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
                    ?? (_headFiles.FirstOrDefault(file => Below(file, path, StringComparison.OrdinalIgnoreCase)) is { } below ? below[..path.Length] : null);

                if (spelling is not null)
                {
                    spelled[path] = spelling;
                }
            }

            if (spelled.Count > 0)
            {
                var otherwise = await git.ListEntriesAtAsync(main, "HEAD", [.. spelled.Values.Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);

                foreach (var (path, spelling) in spelled)
                {
                    if (otherwise.TryGetValue(spelling, out var entry))
                    {
                        found[path] = (entry, SpelledOtherwise: true);
                    }
                }
            }

            return found;
        }

        /// <summary>Whether the repository ignores case in the names of its paths: its <c>core.ignorecase</c>.</summary>
        private async Task<bool> IgnoresCaseAsync()
            => _ignoresCase ??= await git.IgnoresCaseAsync(main, cancellationToken).ConfigureAwait(false);

        /// <summary>Whether <paramref name="name"/> lies below the directory <paramref name="path"/>.</summary>
        private static bool Below(string name, string path, StringComparison comparison)
            => name.Length > path.Length && name[path.Length] == '/' && name.StartsWith(path, comparison);
    }

    /// <summary>
    /// The submodules a tree records - the gitlinks its index holds, read once and only where a path is asked about - and
    /// the one each path asked about is or lies in, compared as the repository's <c>core.ignorecase</c> says, with whether
    /// the tree's HEAD holds that gitlink too. The one reading of it, for the main tree a fold writes into and the agent's
    /// worktree a hand-over writes into.
    /// </summary>
    /// <param name="git">Asks git.</param>
    /// <param name="tree">The tree.</param>
    /// <param name="ignoresCase">Whether the repository ignores case in the names of its paths.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    private sealed class Submodules(IGitClient git, string tree, Func<Task<bool>> ignoresCase, CancellationToken cancellationToken)
    {
        private IReadOnlyList<string>? _gitlinks;

        /// <summary>The submodule each of <paramref name="paths"/> is or lies in, where one does, and where its record is.</summary>
        /// <param name="paths">The paths.</param>
        public async Task<IReadOnlyDictionary<string, (string Gitlink, Submodule In)>> OfAsync(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0)
            {
                return new Dictionary<string, (string, Submodule)>(StringComparer.Ordinal);
            }

            _gitlinks ??= [.. (await git.ListIndexAsync(tree, cancellationToken).ConfigureAwait(false))
                .Where(entry => entry.IsSubmodule)
                .Select(entry => entry.Path)
                .Distinct(StringComparer.Ordinal)];

            if (_gitlinks.Count == 0)
            {
                return new Dictionary<string, (string, Submodule)>(StringComparer.Ordinal);
            }

            var comparison = await ignoresCase().ConfigureAwait(false) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var found = paths
                .Distinct(StringComparer.Ordinal)
                .Select(path => (Path: path, Gitlink: _gitlinks.FirstOrDefault(gitlink => string.Equals(path, gitlink, comparison) || Below(path, gitlink, comparison))))
                .Where(path => path.Gitlink is not null)
                .ToList();
            var held = found.Count == 0
                ? new Dictionary<string, GitEntry>(StringComparer.Ordinal)
                : await git.ListEntriesAtAsync(tree, "HEAD", [.. found.Select(path => path.Gitlink!).Distinct(StringComparer.Ordinal)], cancellationToken)
                    .ConfigureAwait(false);

            return found.ToDictionary(
                path => path.Path,
                path => (path.Gitlink!, held.TryGetValue(path.Gitlink!, out var entry) && entry.Type == "commit" ? Submodule.Committed : Submodule.Staged),
                StringComparer.Ordinal);
        }

        /// <summary>Whether <paramref name="name"/> lies below the directory <paramref name="path"/>.</summary>
        private static bool Below(string name, string path, StringComparison comparison)
            => name.Length > path.Length && name[path.Length] == '/' && name.StartsWith(path, comparison);
    }

    /// <summary>
    /// How the main tree came to hold what it holds at each of <paramref name="paths"/> since the agent's base: the base
    /// asked for its entries there, and the main tree, through <paramref name="answers"/>, for its HEAD's, the submodule a
    /// path HEAD holds nothing at lies in, what its status lists where a commit moved a path or neither HEAD nor the base
    /// holds one - of the submodule's record, for a path in one - and which paths git ignores where its status lists
    /// nothing, HEAD holds nothing and no submodule holds the path. Nothing is written.
    /// </summary>
    /// <param name="answers">What git answers of the main tree in this measuring.</param>
    /// <param name="worktree">The agent's worktree, where its base is read.</param>
    /// <param name="baseCommit">The agent's base.</param>
    /// <param name="paths">Each path, with what the main tree holds there now.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    private async Task<IReadOnlyDictionary<string, Came>> CameAsync(
        MainTreeAnswers answers,
        string worktree,
        string baseCommit,
        IReadOnlyList<(string Path, Holds Now)> paths,
        CancellationToken cancellationToken)
    {
        var asked = paths.DistinctBy(path => path.Path, StringComparer.Ordinal).ToList();
        IReadOnlyList<string> names = [.. asked.Select(path => path.Path)];
        var based = await _gitClient.ListEntriesAtAsync(worktree, baseCommit, names, cancellationToken).ConfigureAwait(false);
        var head = await answers.HeadAsync(names).ConfigureAwait(false);
        var unrecorded = asked.Where(path => path.Now != Holds.Nothing && !head.ContainsKey(path.Path)).Select(path => path.Path).ToList();

        // git looks at nothing below a submodule, and check-ignore refuses such a path: it came as the record above it did.
        var submodules = await answers.SubmodulesAsync(unrecorded).ConfigureAwait(false);
        var read = new List<(string Path, Holds Now, GitEntry? AtBase, GitEntry? AtHead, bool SpelledOtherwise, bool Listed, Submodule In)>();

        foreach (var (path, now) in asked)
        {
            var atBase = based.GetValueOrDefault(path);
            var found = head.TryGetValue(path, out var held);
            var atHead = found ? held.Entry : null;
            var spelledOtherwise = found && held.SpelledOtherwise;

            if (submodules.TryGetValue(path, out var submodule))
            {
                read.Add((path, now, atBase, atHead, spelledOtherwise, await answers.ListsAsync(submodule.Gitlink).ConfigureAwait(false), submodule.In));
                continue;
            }

            // Whether git lists it decides how only where a commit moved it, or where neither HEAD nor the base holds
            // anything there and what the main tree holds may be what git ignores; otherwise it was an uncommitted edit.
            var asks = atHead != atBase || spelledOtherwise || (atHead is null && atBase is null);
            var listed = asks && await answers.ListsAsync(path).ConfigureAwait(false);

            read.Add((path, now, atBase, atHead, spelledOtherwise, listed, Submodule.None));
        }

        // Only git says what it ignores: asked once, of what its status lists nothing at, HEAD holds nothing at and no
        // submodule holds.
        var ignored = await answers.IgnoredAsync([.. read
                .Where(path => !path.Listed && path.AtHead is null && path.Now != Holds.Nothing && path.In == Submodule.None)
                .Select(path => path.Path)])
            .ConfigureAwait(false);

        return read.ToDictionary(
            path => path.Path,
            path => new Came(path.Now, path.AtBase, path.AtHead, path.SpelledOtherwise, path.Listed, ignored.Contains(path.Path), path.In),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// What writing or removing the agent's copy of a path the main tree changed would lose: the one wording of it.
    /// </summary>
    /// <param name="agentRemoved">Whether the agent removed the path, rather than holding a file there.</param>
    /// <param name="lost">What would be lost: that change, or a file.</param>
    private static string Lose(bool agentRemoved, string lost) => agentRemoved ? $"so removing it would lose {lost}" : $"so writing the agent's copy would lose {lost}";

    /// <summary>What one tree holds at a path.</summary>
    private readonly record struct Side(Kind Kind, FileIdentity? File);

    /// <summary>What both trees hold at a path, or the link along it that stops it being read.</summary>
    private sealed record Look(string Path, string? Link, bool Escapes, Side Agent, Side Main);

    /// <summary>A path of the agent's own in the way of writing <see cref="Written"/>: above it, or <see cref="Below"/> it.</summary>
    private readonly record struct InTheWayOf(string Own, string Written, bool Below);

    /// <summary>Both trees' roots, as spelt and with every link along them followed.</summary>
    private sealed record Roots(string Main, string ReachedMain, string Agent, string ReachedAgent);

    /// <summary>What an agent shares with the main tree, read from its seed once: each shared path's baseline.</summary>
    private sealed class Baselines(SeedRecord seed)
    {
        private readonly HashSet<string> _executable = (seed.Executable ?? []).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _absent = (seed.Absent ?? []).ToHashSet(StringComparer.Ordinal);

        /// <summary>What both trees held at <paramref name="path"/> when the agent was last handed it or folded; null where it shares nothing for it.</summary>
        public Side? Of(string path)
            => seed.Paths.TryGetValue(path, out var digest) ? new Side(Kind.File, new FileIdentity(digest, _executable.Contains(path)))
                : _absent.Contains(path) ? new Side(Kind.Absent, null)
                : null;
    }
}
