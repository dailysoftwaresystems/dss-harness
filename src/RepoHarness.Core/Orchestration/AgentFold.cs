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

    /// <summary>The paths declared settled by hand: neither compared nor written.</summary>
    public required IReadOnlyList<string> Settled { get; init; }

    /// <summary>Why the fold cannot be applied, a line for each path; while there is one, nothing is written.</summary>
    public required IReadOnlyList<string> Refusals { get; init; }

    /// <summary>The paths the agent shares with the main tree: handed to it, or written or removed by an earlier fold.</summary>
    public required IReadOnlySet<string> Seeded { get; init; }

    /// <summary>
    /// What each written path, and each already in the main tree, held in the agent's worktree when it was measured - its
    /// digest, or null where it held no file: what is written, and what both trees are recorded as sharing once it is.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> AgentContent { get; init; }

    /// <summary>
    /// What each written and each deleted path held in the main tree when it was measured - its digest, or null where it
    /// held no file: what a write replaces and a removal removes, and nothing else.
    /// </summary>
    public required IReadOnlyDictionary<string, string?> MainContent { get; init; }

    /// <summary>Whether nothing is left to write, delete or refuse: what deleting an agent requires once its fold is in.</summary>
    public bool NothingLeft => Written.Count == 0 && Deleted.Count == 0 && Refusals.Count == 0;
}

/// <summary>What applying a fold did.</summary>
/// <param name="Written">The paths it wrote into the main tree, in order.</param>
/// <param name="Deleted">The paths it removed from it, in order.</param>
/// <param name="Stopped">Why it stopped part way; null where it wrote everything.</param>
public sealed record FoldApplied(IReadOnlyList<string> Written, IReadOnlyList<string> Deleted, string? Stopped);

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

    /// <summary>Why its base cannot be moved, path by path; empty where it can.</summary>
    public required IReadOnlyList<string> Refusals { get; init; }
}

/// <summary>What the main tree hands an agent: each file copied, each deletion made, and what it cannot hand.</summary>
/// <param name="Files">The changed files, each copied into the agent's worktree.</param>
/// <param name="Deletions">The paths the main tree deleted, each removed from the agent's worktree.</param>
/// <param name="Directories">The untracked directories git will not look into - repositories of their own - which a fold never moves: named, and not handed.</param>
/// <param name="Links">
/// The symbolic links the main tree committed since the agent's base, which are never handed as the files they lead to:
/// named, and not handed; moving the agent's base brings each in as git holds it. One not committed refuses the hand-over.
/// </param>
public sealed record Handable(IReadOnlyList<string> Files, IReadOnlyList<string> Deletions, IReadOnlyList<string> Directories, IReadOnlyList<string>? Links = null)
{
    /// <summary>A hand-over of nothing.</summary>
    public static Handable None { get; } = new([], [], []);

    /// <summary>How many paths are handed over: files and deletions.</summary>
    public int Count => Files.Count + Deletions.Count;
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

    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFilePermissions _permissions = permissions;
    private readonly IHostPlatform _platform = platform;

    /// <summary>Measures folding the agent whose worktree is <paramref name="worktree"/> into <paramref name="main"/>; writes nothing.</summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree, which must be its own registered worktree.</param>
    /// <param name="baseCommit">The commit its worktree was made from, which its HEAD must still be.</param>
    /// <param name="seed">What it shares with the main tree.</param>
    /// <param name="settled">The paths declared settled by hand.</param>
    /// <param name="floor">The paths never moved between trees (<see cref="TreeFloor"/>).</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <exception cref="HarnessException">git could not answer, or a changed path is not one a record can keep.</exception>
    public async Task<FoldPlan> MeasureAsync(
        string main,
        string worktree,
        string baseCommit,
        SeedRecord seed,
        IReadOnlyCollection<string> settled,
        IReadOnlyList<string> floor,
        CancellationToken cancellationToken)
    {
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
        var unseeded = looks.Where(look => look.Link is null && shared.Of(look.Path) is null && !settledPaths.Contains(look.Path)).ToList();
        var baseBlobs = await _gitClient.BlobIdsAtAsync(worktree, baseCommit, [.. unseeded.Select(look => look.Path)], cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> mainMoved = unseeded.Any(look => baseBlobs[look.Path] is not null)
            ? await _gitClient.ListChangedSinceAsync(main, baseCommit, cancellationToken).ConfigureAwait(false)
            : new HashSet<string>(StringComparer.Ordinal);

        var written = new List<string>();
        var deleted = new List<string>();
        var agentContent = new Dictionary<string, string?>(StringComparer.Ordinal);
        var mainContent = new Dictionary<string, string?>(StringComparer.Ordinal);
        var inherited = new List<string>();
        var alreadyIn = new List<string>();
        var settledHere = new List<string>();
        var refused = new List<(string Path, string Why)>();
        var drifted = new List<(string Path, string BaseBlob, string Consequence)>();
        var at = OrchestrationReports.Base(baseCommit);

        foreach (var look in looks)
        {
            var path = look.Path;

            // Asked before --settled, which never reaches outside the main tree.
            if (look.Escapes)
            {
                refused.Add((path, $"'{path}' leads out of the main tree, through {look.Link}"));
                continue;
            }

            // Asked before everything else it could be refused for, deletions included: a path settled by hand is the one
            // way out of an all-or-nothing refusal, and a refusal asked first would refuse it again.
            if (settledPaths.Contains(path))
            {
                settledHere.Add(path);
                continue;
            }

            if (look.Link is { } link)
            {
                refused.Add((path, $"'{path}' is reached through a symbolic link or junction in {link}, and a fold never reads or writes through one"));
                continue;
            }

            if (look.Agent.Kind == Kind.Directory)
            {
                refused.Add((path, $"'{path}' is a directory in the agent's worktree - a submodule, or a repository of its own - and a fold moves files only"));
                continue;
            }

            var baseline = shared.Of(path);

            // What it shares and still holds as both trees held it is not its work, whatever the main tree did since.
            if (baseline is { } kept && look.Agent == kept)
            {
                inherited.Add(path);
                continue;
            }

            // What the main tree already holds as the agent does is neither written nor refused: writing it changes
            // nothing, and refusing it would refuse every fold run again after one that stopped part way.
            if (look.Main == look.Agent)
            {
                alreadyIn.Add(path);
                agentContent[path] = look.Agent.File?.Digest;
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
                // of the main tree's that writing or removing the agent's would lose.
                if (look.Main != before)
                {
                    refused.Add((path, (before.Kind, look.Main.Kind) switch
                    {
                        (Kind.File, Kind.Absent) => $"'{path}' was handed to the agent, and the main tree no longer holds it",
                        (Kind.Absent, _) => $"'{path}' was absent from both trees when the agent was last handed it or folded, and the main tree holds it now",
                        _ when look.Agent.Kind == Kind.Absent => $"'{path}': the main tree changed it after it was handed to the agent, so removing it would lose that change",
                        _ => $"'{path}': the main tree changed it after it was handed to the agent, so writing the agent's copy would lose that change",
                    }));
                    continue;
                }
            }
            else if (baseBlobs[path] is not { } baseBlob)
            {
                if (look.Agent.Kind == Kind.Absent)
                {
                    refused.Add((path, $"the agent deleted '{path}', which its base {at} does not hold, so nothing proves removing it from the main tree safe"));
                    continue;
                }

                if (look.Main.Kind != Kind.Absent)
                {
                    refused.Add((path, $"'{path}' is not at the agent's base {at}, and the main tree holds it"));
                    continue;
                }
            }
            else if (look.Main.Kind == Kind.Absent)
            {
                refused.Add((path, $"'{path}' is at the agent's base {at}, and missing from the main tree"));
                continue;
            }
            else if (mainMoved.Contains(path))
            {
                drifted.Add((path, baseBlob, look.Agent.Kind == Kind.Absent ? "so removing it would lose that change" : "so writing the agent's copy would lose that change"));
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
                agentContent[path] = look.Agent.File!.Value.Digest;
            }
        }

        // How the main tree moved, because the two are reconciled differently: a commit once a sibling's fold was committed,
        // an edit while it is not.
        if (drifted.Count > 0)
        {
            var head = await _gitClient.BlobIdsAtAsync(main, "HEAD", [.. drifted.Select(drift => drift.Path)], cancellationToken).ConfigureAwait(false);

            foreach (var (path, baseBlob, consequence) in drifted)
            {
                var how = head.GetValueOrDefault(path) != baseBlob ? "by a commit - its HEAD no longer holds what the base holds -" : "by an uncommitted edit";
                refused.Add((path, $"'{path}': the main tree changed it after the agent's base {at}, {how} {consequence}"));
            }
        }

        foreach (var stray in settledPaths.Where(path => !candidates.Contains(path, StringComparer.Ordinal)).Order(StringComparer.Ordinal))
        {
            refused.Add((stray, $"--settled '{stray}' names no path this fold weighs, so it settles nothing: check its spelling"));
        }

        return new FoldPlan
        {
            Written = written,
            Deleted = deleted,
            Inherited = inherited,
            AlreadyIn = alreadyIn,
            Settled = settledHere,
            Refusals = [.. refused.OrderBy(refusal => refusal.Path, StringComparer.Ordinal).Select(refusal => refusal.Why)],
            Seeded = seed.Weighed.ToHashSet(StringComparer.Ordinal),
            AgentContent = agentContent,
            MainContent = mainContent,
        };
    }

    /// <summary>
    /// Writes a measured fold into <paramref name="main"/>: each written path copied and proved, then each deleted path
    /// removed, with the directories that leaves empty up to - never including - the main tree's root. Never cancelled
    /// part way. Only what was measured is written or removed: an agent's file that changed since, or a main-tree file
    /// that did - one about to be written over or removed - stops it there, with what it did so far said.
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

        try
        {
            foreach (var path in plan.Written)
            {
                var destination = Path.Combine(main, path);
                await RequireMeasuredAsync(destination, path, plan.MainContent[path], "written over").ConfigureAwait(false);
                await VerifiedFileCopy.CopyAsync(_fileSystem, Path.Combine(worktree, path), destination, plan.AgentContent[path], CancellationToken.None).ConfigureAwait(false);
                written.Add(path);
            }

            foreach (var path in plan.Deleted)
            {
                var file = Path.Combine(main, path);
                await RequireMeasuredAsync(file, path, plan.MainContent[path], "removed").ConfigureAwait(false);
                _fileSystem.DeleteFile(file);
                deleted.Add(path);
                _fileSystem.RemoveEmptiedDirectories(main, Path.GetDirectoryName(file), _platform.PathComparison);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FoldApplied(written, deleted, ex.Message);
        }

        return new FoldApplied(written, deleted, null);
    }

    /// <summary>
    /// What <paramref name="seed"/> becomes once a fold is applied, as far as it went: each path it wrote, and each the main
    /// tree already held as the agent does, shared as the file both trees now hold - or as absent from both - and each it
    /// removed as absent from both. A later fold weighs those paths against that, never against the agent's base: a review
    /// that sends the agent back, and the agent putting a path back as it was, is then its change to fold like any other.
    /// </summary>
    /// <param name="seed">What the agent shared with the main tree before.</param>
    /// <param name="plan">The fold measured.</param>
    /// <param name="applied">What applying it did.</param>
    /// <param name="worktree">The agent's worktree, whose copies say which files could be run.</param>
    public SeedRecord Folded(SeedRecord seed, FoldPlan plan, FoldApplied applied, string worktree)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(applied);

        var shared = new Dictionary<string, string>(seed.Paths, StringComparer.Ordinal);
        var executable = (seed.Executable ?? []).ToHashSet(StringComparer.Ordinal);
        var absent = (seed.Absent ?? []).ToHashSet(StringComparer.Ordinal);

        foreach (var path in applied.Written.Concat(plan.AlreadyIn))
        {
            if (plan.AgentContent[path] is { } digest)
            {
                shared[path] = digest;
                absent.Remove(path);
                Mark(executable, path, IsExecutable(Path.Combine(worktree, path)));
            }
            else
            {
                MarkAbsent(shared, executable, absent, path);
            }
        }

        foreach (var path in applied.Deleted)
        {
            MarkAbsent(shared, executable, absent, path);
        }

        return Shared(seed, shared, executable, absent);
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
        => Classify(main, await ChangedAsync(main, floor, HandingConsequence, within, cancellationToken).ConfigureAwait(false), uncommitted: null);

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

        return Classify(main, [.. handed], uncommitted.ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// <paramref name="paths"/> sorted by what the main tree holds at each: a file to copy, a deletion to make, a directory
    /// git will not look into, or a link - refused where the main tree has not committed it, named and not handed where
    /// it has. <paramref name="uncommitted"/> is the main tree's uncommitted state; every path is in it where null.
    /// </summary>
    private Handable Classify(string main, IReadOnlyList<string> paths, IReadOnlySet<string>? uncommitted)
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

        return links.Count switch
        {
            0 => new Handable(files, deletions, directories, committedLinks.Count > 0 ? committedLinks : null),
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
    /// Hands the agent what <paramref name="handable"/> names - each file copied from the main tree into its worktree and
    /// proved, each deletion made there - and records in <paramref name="seed"/> what it was handed as it goes: a hand-over
    /// that stops part way leaves a record of exactly what was handed, never one that takes a copied file for the agent's
    /// own work.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="handable">What to hand it, as <see cref="HandableAsync"/> gives it.</param>
    /// <param name="seed">The seed to add them to.</param>
    /// <param name="cancellationToken">Stops it between one path and the next.</param>
    /// <returns>The seed with what was handed, how many paths were, and why it stopped where it did not hand everything.</returns>
    public async Task<(SeedRecord Seed, int Handed, string? Stopped)> HandAsync(
        string main,
        string worktree,
        Handable handable,
        SeedRecord seed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handable);
        ArgumentNullException.ThrowIfNull(seed);

        var shared = new Dictionary<string, string>(seed.Paths, StringComparer.Ordinal);
        var executable = (seed.Executable ?? []).ToHashSet(StringComparer.Ordinal);
        var absent = (seed.Absent ?? []).ToHashSet(StringComparer.Ordinal);
        var count = 0;
        string? stopped = null;

        try
        {
            foreach (var path in handable.Files)
            {
                var copy = Path.Combine(worktree, path);
                shared[path] = (await VerifiedFileCopy.CopyAsync(_fileSystem, Path.Combine(main, path), copy, cancellationToken: cancellationToken).ConfigureAwait(false)).Content;
                absent.Remove(path);
                Mark(executable, path, IsExecutable(copy));
                count++;
            }

            foreach (var path in handable.Deletions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var copy = Path.Combine(worktree, path);

                switch (_fileSystem.KindOf(copy))
                {
                    case PathKind.File or PathKind.Link:
                        _fileSystem.DeleteFile(copy);
                        break;
                    case PathKind.Directory:
                        throw new IOException($"'{path}' is a directory in the agent's worktree, where the main tree deleted a file");
                }

                MarkAbsent(shared, executable, absent, path);
                count++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            stopped = ex is OperationCanceledException ? "it was interrupted" : ex.Message.TrimEnd('.');
        }

        return (Shared(seed, shared, executable, absent), count, stopped);
    }

    /// <summary>
    /// Measures moving the agent's base from <paramref name="from"/> to <paramref name="to"/>; writes nothing. Each path the
    /// two commits hold differently, as git compares them, is one of: shared with the main tree, which stays as its seed
    /// records it; held as <paramref name="to"/> holds it already; held as <paramref name="from"/> holds it, which comes in
    /// as <paramref name="to"/> holds it; or changed by the agent - an edit, a deletion, or a file of its own git does not
    /// track - which is refused, unless declared settled by hand, when its copy stays as its change on the new base.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="from">The commit its worktree was made from.</param>
    /// <param name="to">The commit its base moves to.</param>
    /// <param name="seed">What it shares with the main tree.</param>
    /// <param name="settled">The paths declared settled by hand.</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <exception cref="HarnessException">git could not answer, or names a path that is not UTF-8.</exception>
    public async Task<RebasePlan> MeasureRebaseAsync(
        string worktree,
        string from,
        string to,
        SeedRecord seed,
        IReadOnlyCollection<string> settled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var committed = new List<string>();

        foreach (var name in await _gitClient.ListNamesAsync(worktree, ["diff", "--name-only", "-z", "--no-renames", "--ignore-submodules=none", from, to, "--"], cancellationToken).ConfigureAwait(false))
        {
            if (!name.IsUtf8)
            {
                throw name.Unreadable("the agent's base cannot be moved");
            }

            committed.Add(name.Text);
        }

        var settledPaths = settled.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal);
        var shared = new Baselines(seed);
        var sinceFrom = await _gitClient.ListChangedSinceAsync(worktree, from, cancellationToken).ConfigureAwait(false);
        var sinceTo = await _gitClient.ListChangedSinceAsync(worktree, to, cancellationToken).ConfigureAwait(false);

        // A file the agent holds where its base held none is its own, whatever git makes of it - untracked, staged, or one
        // git ignores, which no status lists - and no diff against a commit says so; so is everything below a directory git
        // will not look into, a repository of its own, which status names as the directory.
        var untracked = (await _gitClient.ReadStatusAsync(worktree, cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.Index == '?' && entry.Path.Text.EndsWith('/'))
            .Select(entry => entry.Path.Text)
            .ToList();
        var based = await _gitClient.BlobIdsAtAsync(worktree, from, committed, cancellationToken).ConfigureAwait(false);

        bool Own(string path)
            => untracked.Any(directory => path.StartsWith(directory, StringComparison.Ordinal))
                || (based[path] is null && _fileSystem.KindOf(Path.Combine(worktree, path)) != PathKind.None);

        var taken = new List<string>();
        var kept = new List<string>();
        var already = new List<string>();
        var settledHere = new List<string>();
        var refused = new List<(string Path, string Why)>();
        var at = OrchestrationReports.Base(from);

        foreach (var path in committed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var own = Own(path);

            if (shared.Of(path) is not null)
            {
                kept.Add(path);
            }
            else if (!own && !sinceTo.Contains(path))
            {
                already.Add(path);
            }
            else if (!own && !sinceFrom.Contains(path))
            {
                taken.Add(path);
            }
            else if (settledPaths.Contains(path))
            {
                settledHere.Add(path);
            }
            else
            {
                refused.Add((path, own
                    ? $"'{path}': the agent made it, and the main tree committed a file there since the agent's base {at}"
                    : _fileSystem.KindOf(Path.Combine(worktree, path)) == PathKind.None
                        ? $"the agent deleted '{path}', and the main tree committed a change to it since the agent's base {at}"
                        : $"'{path}': the agent changed it, and the main tree committed a change to it since the agent's base {at}"));
            }
        }

        foreach (var stray in settledPaths.Where(path => !settledHere.Contains(path, StringComparer.Ordinal)).Order(StringComparer.Ordinal))
        {
            refused.Add((stray, $"--settled '{stray}' names no path the agent changed that the main tree committed a change to since, so it settles nothing: check its spelling"));
        }

        return new RebasePlan
        {
            Taken = taken,
            Kept = kept,
            AlreadyThere = already,
            Settled = settledHere,
            Refusals = [.. refused.OrderBy(refusal => refusal.Path, StringComparer.Ordinal).Select(refusal => refusal.Why)],
        };
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
            var based = await _gitClient.BlobIdsAtAsync(tree, baseCommit, [.. unseeded.Select(entry => entry.Path)], cancellationToken).ConfigureAwait(false);
            IReadOnlySet<string> moved = movedSinceBase ?? (unseeded.Any(entry => based[entry.Path] is not null)
                ? await _gitClient.ListChangedSinceAsync(tree, baseCommit, cancellationToken).ConfigureAwait(false)
                : new HashSet<string>(StringComparer.Ordinal));

            // A path its base holds differs where it is not what the base holds, its deletion included; one its base lacks
            // differs where anything is there.
            differs.AddRange(unseeded
                .Where(entry => based[entry.Path] is not null ? moved.Contains(entry.Path) : entry.Side.Kind != Kind.Absent)
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
            if (shared.Of(path) is not { } baseline || await SideAsync(Path.Combine(worktree, path), cancellationToken).ConfigureAwait(false) != baseline)
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
    /// absent, an agent's file would be taken for its deletion of the main tree's.
    /// </summary>
    private async Task<Side> SideAsync(string path, CancellationToken cancellationToken)
        => _fileSystem.KindOf(path) switch
        {
            PathKind.File => new Side(Kind.File, await IdentityAsync(path, cancellationToken).ConfigureAwait(false)),
            PathKind.Directory => new Side(Kind.Directory, null),
            PathKind.None => new Side(Kind.Absent, null),
            _ => throw new IOException($"'{path}' is a link, and a link is never read as the file it leads to."),
        };

    /// <summary>What the file at <paramref name="path"/> is: its content, and whether it could be run.</summary>
    private async Task<Identity> IdentityAsync(string path, CancellationToken cancellationToken)
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
    /// Stops a write or a removal where the main tree's file at <paramref name="file"/> no longer holds what was measured -
    /// another program wrote it since - so the fold never loses what it never weighed.
    /// </summary>
    private async Task RequireMeasuredAsync(string file, string path, string? measured, string what)
    {
        var now = _fileSystem.KindOf(file) switch
        {
            PathKind.File => (await FileContentHash.OfAsync(_fileSystem, file, CancellationToken.None).ConfigureAwait(false)).Content,
            PathKind.None => null,
            _ => throw new IOException($"'{path}' is no longer a file in the main tree, so it was not {what}"),
        };

        if (now != measured)
        {
            throw new IOException($"'{path}' holds other content in the main tree now than when it was measured, so it was not {what}");
        }
    }

    /// <summary>A seed holding <paramref name="shared"/>, <paramref name="executable"/> and <paramref name="absent"/>: handed nothing only where it still names nothing.</summary>
    private static SeedRecord Shared(SeedRecord seed, Dictionary<string, string> shared, HashSet<string> executable, HashSet<string> absent)
        => seed with
        {
            Paths = shared,
            Empty = seed.Empty && shared.Count == 0 && absent.Count == 0,
            Executable = executable.Count > 0 ? [.. executable.Order(StringComparer.Ordinal)] : null,
            Absent = absent.Count > 0 ? [.. absent.Order(StringComparer.Ordinal)] : null,
        };

    private static void Mark(HashSet<string> executable, string path, bool runnable)
    {
        if (runnable)
        {
            executable.Add(path);
        }
        else
        {
            executable.Remove(path);
        }
    }

    private static void MarkAbsent(Dictionary<string, string> shared, HashSet<string> executable, HashSet<string> absent, string path)
    {
        shared.Remove(path);
        executable.Remove(path);
        absent.Add(path);
    }

    /// <summary>What a path is in one tree.</summary>
    private enum Kind
    {
        Absent,
        File,
        Directory,
    }

    /// <summary>What a file is: its content, and whether it could be run.</summary>
    private readonly record struct Identity(string Digest, bool Executable);

    /// <summary>What one tree holds at a path.</summary>
    private readonly record struct Side(Kind Kind, Identity? File);

    /// <summary>What both trees hold at a path, or the link along it that stops it being read.</summary>
    private sealed record Look(string Path, string? Link, bool Escapes, Side Agent, Side Main);

    /// <summary>Both trees' roots, as spelt and with every link along them followed.</summary>
    private sealed record Roots(string Main, string ReachedMain, string Agent, string ReachedAgent);

    /// <summary>What an agent shares with the main tree, read from its seed once: each shared path's baseline.</summary>
    private sealed class Baselines(SeedRecord seed)
    {
        private readonly HashSet<string> _executable = (seed.Executable ?? []).ToHashSet(StringComparer.Ordinal);
        private readonly HashSet<string> _absent = (seed.Absent ?? []).ToHashSet(StringComparer.Ordinal);

        /// <summary>What both trees held at <paramref name="path"/> when the agent was last handed it or folded; null where it shares nothing for it.</summary>
        public Side? Of(string path)
            => seed.Paths.TryGetValue(path, out var digest) ? new Side(Kind.File, new Identity(digest, _executable.Contains(path)))
                : _absent.Contains(path) ? new Side(Kind.Absent, null)
                : null;
    }
}
