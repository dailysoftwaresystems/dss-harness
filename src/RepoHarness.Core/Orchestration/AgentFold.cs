using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;

namespace RepoHarness.Core.Orchestration;

/// <summary>
/// What folding an agent does, measured: every path its worktree changes and every path it was handed, each in exactly
/// one list.
/// </summary>
public sealed record FoldPlan
{
    /// <summary>The commit the agent's worktree was made from, which every path it was not handed is measured against.</summary>
    public required string Base { get; init; }

    /// <summary>The agent's own work: written into the main tree.</summary>
    public required IReadOnlyList<string> Written { get; init; }

    /// <summary>The paths it deleted: removed from the main tree.</summary>
    public required IReadOnlyList<string> Deleted { get; init; }

    /// <summary>The paths it was handed and left as they were: not its work, and never written back.</summary>
    public required IReadOnlyList<string> Inherited { get; init; }

    /// <summary>The paths the main tree already holds as the agent does, absent from both included: nothing to write.</summary>
    public required IReadOnlyList<string> AlreadyIn { get; init; }

    /// <summary>The paths declared settled by hand: neither compared nor written.</summary>
    public required IReadOnlyList<string> Settled { get; init; }

    /// <summary>Why the fold cannot be applied, a line for each path; while there is one, nothing is written.</summary>
    public required IReadOnlyList<string> Refusals { get; init; }

    /// <summary>The paths the agent was handed.</summary>
    public required IReadOnlySet<string> Seeded { get; init; }

    /// <summary>What each written path held in the agent's worktree when it was measured: what is written, and nothing else.</summary>
    public required IReadOnlyDictionary<string, string> AgentContent { get; init; }

    /// <summary>What each deleted path held in the main tree when it was measured: what is removed, and nothing else.</summary>
    public required IReadOnlyDictionary<string, string> MainContent { get; init; }

    /// <summary>Whether nothing is left to write, delete or refuse: what deleting an agent requires once its fold is in.</summary>
    public bool NothingLeft => Written.Count == 0 && Deleted.Count == 0 && Refusals.Count == 0;
}

/// <summary>What applying a fold did.</summary>
/// <param name="Written">How many of its paths were written into the main tree.</param>
/// <param name="Deleted">How many were removed from it.</param>
/// <param name="Stopped">Why it stopped part way; null where it wrote everything.</param>
public sealed record FoldApplied(int Written, int Deleted, string? Stopped);

/// <summary>
/// Measures and applies an agent's fold: what the agent changed, told apart from what it was handed and from what the
/// main tree did meanwhile, and written into the main tree only where nothing can be lost.
/// </summary>
/// <remarks>
/// An agent's contribution is a measurement, never an assumption: its worktree's status and every path it was handed,
/// less the handed paths it left as they were. A handed path is compared with what it was handed; any other with the
/// blob at the agent's own base - never the main tree's HEAD, which a sibling's committed fold moves - through the clean
/// filters git compares a working file through, so a line-ending conversion is not taken for a change. A file is its
/// content and, where the platform has one, its execute bit. Every refusal is collected before anything is written, and
/// any one refuses the whole fold: a fold that wrote nine files and refused the tenth would leave a tree nobody can
/// reason about.
/// </remarks>
/// <param name="gitClient">Reads the worktree's status and the blobs to measure against.</param>
/// <param name="fileSystem">Reads both trees, and writes the main one.</param>
/// <param name="permissions">Reads a file's execute bit.</param>
/// <param name="platform">Whether files have an execute bit here, and how paths compare.</param>
internal sealed class AgentFold(IGitClient gitClient, IFileSystem fileSystem, IFilePermissions permissions, IHostPlatform platform)
{
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFilePermissions _permissions = permissions;
    private readonly IHostPlatform _platform = platform;

    /// <summary>Measures folding the agent whose worktree is <paramref name="worktree"/> into <paramref name="main"/>; writes nothing.</summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree, which must be its own registered worktree.</param>
    /// <param name="baseCommit">The commit its worktree was made from, which its HEAD must still be.</param>
    /// <param name="seed">What it was handed.</param>
    /// <param name="settled">The paths declared settled by hand.</param>
    /// <param name="floor">The paths never moved between trees (<see cref="TreeFloor"/>).</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <exception cref="Results.HarnessException">git could not answer, or a changed path is not named in UTF-8.</exception>
    public async Task<FoldPlan> MeasureAsync(
        string main,
        string worktree,
        string baseCommit,
        SeedRecord seed,
        IReadOnlyCollection<string> settled,
        IReadOnlyList<string> floor,
        CancellationToken cancellationToken)
    {
        var candidates = (await ChangedAsync(worktree, floor, "what the agent did with it cannot be measured", within: null, cancellationToken).ConfigureAwait(false))
            .Concat(seed.Paths.Keys.Where(path => !TreeFloor.Covers(floor, path)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var settledPaths = settled.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal);
        var executable = (seed.Executable ?? []).ToHashSet(StringComparer.Ordinal);
        var roots = new Roots(main, _fileSystem.ResolveLinks(main), worktree, _fileSystem.ResolveLinks(worktree));
        var looks = new List<Look>(candidates.Count);

        foreach (var path in candidates)
        {
            looks.Add(await LookAsync(path, roots, cancellationToken).ConfigureAwait(false));
        }

        // One git process each for every path that was not handed over: its blob at the agent's base, and the main tree's
        // working file as git would store it. A hundred paths are routine, and two processes each would be two hundred.
        var unseeded = looks.Where(look => look.Link is null && !seed.Paths.ContainsKey(look.Path) && !settledPaths.Contains(look.Path)).ToList();
        var baseBlobs = await _gitClient.BlobIdsAtAsync(worktree, baseCommit, [.. unseeded.Select(look => look.Path)], cancellationToken).ConfigureAwait(false);
        var mainBlobs = await _gitClient
            .HashWorkingFilesAsync(main, [.. unseeded.Where(look => look.Main.Kind == Kind.File).Select(look => look.Path)], cancellationToken)
            .ConfigureAwait(false);

        var written = new List<string>();
        var deleted = new List<string>();
        var agentContent = new Dictionary<string, string>(StringComparer.Ordinal);
        var mainContent = new Dictionary<string, string>(StringComparer.Ordinal);
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

            Identity? handed = seed.Paths.TryGetValue(path, out var digest) ? new Identity(digest, executable.Contains(path)) : null;
            var baseBlob = baseBlobs.GetValueOrDefault(path);

            if (look.Agent.Kind == Kind.Absent)
            {
                // Only a path the main tree still holds is a deletion to make; and removing one is destructive, so it
                // is proved against the same baseline a copy is.
                if (look.Main.Kind == Kind.Absent)
                {
                    alreadyIn.Add(path);
                }
                else if (look.Main.Kind == Kind.Directory)
                {
                    refused.Add((path, $"the agent removed '{path}', which is a directory in the main tree, and a fold removes files only"));
                }
                else if (handed is { } given)
                {
                    if (look.Main.File == given)
                    {
                        deleted.Add(path);
                        mainContent[path] = look.Main.File!.Value.Digest;
                    }
                    else
                    {
                        refused.Add((path, $"'{path}': the main tree changed it after it was handed to the agent, so removing it would lose that change"));
                    }
                }
                else if (baseBlob is null)
                {
                    refused.Add((path, $"the agent deleted '{path}', which its base {at} does not hold, so nothing proves removing it from the main tree safe"));
                }
                else if (mainBlobs[path] != baseBlob)
                {
                    drifted.Add((path, baseBlob, "so removing it would lose that change"));
                }
                else
                {
                    deleted.Add(path);
                    mainContent[path] = look.Main.File!.Value.Digest;
                }

                continue;
            }

            if (handed is { } unchanged && look.Agent.File == unchanged)
            {
                inherited.Add(path);
                continue;
            }

            // What the main tree already holds as the agent does is neither written nor refused: writing it changes nothing,
            // and refusing it would refuse every fold run again after one that stopped part way.
            if (look.Main.Kind == Kind.File && look.Main.File == look.Agent.File)
            {
                alreadyIn.Add(path);
                continue;
            }

            if (look.Main.Kind == Kind.Directory)
            {
                refused.Add((path, $"'{path}' is a directory in the main tree, and the agent holds a file there"));
                continue;
            }

            if (handed is { } before)
            {
                if (look.Main.Kind != Kind.File)
                {
                    refused.Add((path, $"'{path}' was handed to the agent, and the main tree no longer holds it"));
                    continue;
                }

                if (look.Main.File != before)
                {
                    refused.Add((path, $"'{path}': the main tree changed it after it was handed to the agent, so writing the agent's copy would lose that change"));
                    continue;
                }
            }
            else if (baseBlob is not null)
            {
                if (look.Main.Kind != Kind.File)
                {
                    refused.Add((path, $"'{path}' is at the agent's base {at}, and missing from the main tree"));
                    continue;
                }

                if (mainBlobs[path] != baseBlob)
                {
                    drifted.Add((path, baseBlob, "so writing the agent's copy would lose that change"));
                    continue;
                }
            }
            else if (look.Main.Kind != Kind.Absent)
            {
                refused.Add((path, $"'{path}' is not at the agent's base {at}, and the main tree holds it"));
                continue;
            }

            written.Add(path);
            agentContent[path] = look.Agent.File!.Value.Digest;
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
            Base = baseCommit,
            Written = written,
            Deleted = deleted,
            Inherited = inherited,
            AlreadyIn = alreadyIn,
            Settled = settledHere,
            Refusals = [.. refused.OrderBy(refusal => refusal.Path, StringComparer.Ordinal).Select(refusal => refusal.Why)],
            Seeded = seed.Paths.Keys.ToHashSet(StringComparer.Ordinal),
            AgentContent = agentContent,
            MainContent = mainContent,
        };
    }

    /// <summary>
    /// Writes a measured fold into <paramref name="main"/>: each written path copied and proved, then each deleted path
    /// removed, with the directories that leaves empty up to - never including - the main tree's root. Never cancelled
    /// part way. Only what was measured is written or removed: an agent's file that changed since, or a main-tree file
    /// that did, stops it there, with what it did so far said.
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

        var written = 0;
        var deleted = 0;

        try
        {
            foreach (var path in plan.Written)
            {
                await VerifiedFileCopy.CopyAsync(_fileSystem, Path.Combine(worktree, path), Path.Combine(main, path), plan.AgentContent[path], CancellationToken.None).ConfigureAwait(false);
                written++;
            }

            foreach (var path in plan.Deleted)
            {
                var file = Path.Combine(main, path);
                var now = (await FileContentHash.OfAsync(_fileSystem, file, CancellationToken.None).ConfigureAwait(false)).Content;

                if (now != plan.MainContent[path])
                {
                    throw new IOException($"'{path}' holds other content in the main tree now than when it was measured, so it was not removed.");
                }

                _fileSystem.DeleteFile(file);
                deleted++;
                RemoveEmptiedDirectories(main, Path.GetDirectoryName(file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FoldApplied(written, deleted, ex.Message);
        }

        return new FoldApplied(written, deleted, null);
    }

    /// <summary>
    /// What the worktree holds at every path its status lists and every one of <paramref name="paths"/>: each file's digest,
    /// or null where it holds none. What closing an agent records, and what finishing its deletion compares the worktree
    /// with - never the main tree, which later agents go on changing.
    /// </summary>
    /// <param name="worktree">The agent's worktree, which must be its own registered worktree.</param>
    /// <param name="paths">The paths to weigh beside what its status lists.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<Dictionary<string, string?>> HeldAsync(string worktree, IEnumerable<string> paths, IReadOnlyList<string> floor, CancellationToken cancellationToken)
    {
        var held = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var changed = await ChangedAsync(worktree, floor, "what the agent holds cannot be recorded", within: null, cancellationToken).ConfigureAwait(false);

        foreach (var path in changed.Concat(paths.Where(path => !TreeFloor.Covers(floor, path))).Distinct(StringComparer.Ordinal))
        {
            var file = Path.Combine(worktree, path);
            held[path] = _fileSystem.FileExists(file) ? (await FileContentHash.OfAsync(_fileSystem, file, cancellationToken).ConfigureAwait(false)).Content : null;
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
    /// <param name="seed">What it was handed, where it was.</param>
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

        var now = await HeldAsync(worktree, closing.Held.Keys.Concat(seed?.Paths.Keys ?? Enumerable.Empty<string>()), floor, cancellationToken).ConfigureAwait(false);
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
    /// untracked directory git will not look into - a repository of its own - named as the directory.
    /// </summary>
    /// <param name="directory">A work tree's root.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="consequence">What cannot be done with a path not named in UTF-8, to end a sentence.</param>
    /// <param name="within">Only the paths these cover (<see cref="PathPatterns"/>); every path where null.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="Results.HarnessException">git could not read the status, or a path is not named in UTF-8.</exception>
    public async Task<IReadOnlyList<string>> ChangedAsync(
        string directory,
        IReadOnlyList<string> floor,
        string consequence,
        IReadOnlyList<string>? within,
        CancellationToken cancellationToken)
    {
        var paths = new List<string>();

        foreach (var name in (await _gitClient.ReadStatusAsync(directory, cancellationToken).ConfigureAwait(false)).SelectMany(entry => entry.Paths))
        {
            var path = name.Text.TrimEnd('/');

            if (TreeFloor.Covers(floor, path) || (within is not null && !PathPatterns.Matches(within, path)))
            {
                continue;
            }

            if (!name.IsUtf8)
            {
                throw name.Unreadable(consequence);
            }

            paths.Add(path);
        }

        return [.. paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The main tree's changed files under <paramref name="within"/> - every one where null - that can be handed to an agent,
    /// off the floor: not a deletion, nor an untracked directory git will not look into. Every one is checked before any is
    /// copied, so a hand-over refused is refused before anything is written.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="floor">The paths never moved between trees.</param>
    /// <param name="within">Only the paths these cover; every path where null.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="Results.HarnessException">
    /// A path is a symbolic link, which is never handed over as the file it leads to, or is not named in UTF-8
    /// (<see cref="Results.HarnessExit.Refused"/>).
    /// </exception>
    public async Task<IReadOnlyList<string>> HandableAsync(string main, IReadOnlyList<string> floor, IReadOnlyList<string>? within, CancellationToken cancellationToken)
    {
        var handable = new List<string>();
        var links = new List<string>();

        foreach (var path in await ChangedAsync(main, floor, "it cannot be handed to an agent", within, cancellationToken).ConfigureAwait(false))
        {
            switch (_fileSystem.KindOf(Path.Combine(main, path)))
            {
                case PathKind.File:
                    handable.Add(path);
                    break;
                case PathKind.Link:
                    links.Add(path);
                    break;
            }
        }

        return links.Count switch
        {
            0 => handable,
            1 => throw new Results.HarnessException(
                Results.HarnessExit.Refused,
                $"'{ReportText.Printable(links[0])}' is a symbolic link, which is never handed to an agent as the file it leads to: commit it, or remove "
                + "it, first. Nothing was handed over."),
            _ => throw new Results.HarnessException(
                Results.HarnessExit.Refused,
                $"{ReportText.Listed(links)} are symbolic links, which are never handed to an agent as the files they lead to: commit them, or remove "
                + "them, first. Nothing was handed over."),
        };
    }

    /// <summary>
    /// Copies each of <paramref name="paths"/> from the main tree into the agent's worktree, proving each copy, and records
    /// in <paramref name="seed"/> what it was handed as it goes: a hand-over that stops part way leaves a record of exactly
    /// what was handed, never one that takes a copied file for the agent's own work.
    /// </summary>
    /// <param name="main">The main checkout's root.</param>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="paths">The paths, as <see cref="HandableAsync"/> gives them.</param>
    /// <param name="seed">The seed to add them to.</param>
    /// <param name="cancellationToken">Stops it between one copy and the next.</param>
    /// <returns>The seed with what was handed, and why it stopped where it did not hand everything.</returns>
    public async Task<(SeedRecord Seed, int Handed, string? Stopped)> HandAsync(
        string main,
        string worktree,
        IReadOnlyList<string> paths,
        SeedRecord seed,
        CancellationToken cancellationToken)
    {
        var handed = new Dictionary<string, string>(seed.Paths, StringComparer.Ordinal);
        var executable = (seed.Executable ?? []).ToHashSet(StringComparer.Ordinal);
        var count = 0;
        string? stopped = null;

        foreach (var path in paths)
        {
            var copy = Path.Combine(worktree, path);

            try
            {
                handed[path] = (await VerifiedFileCopy.CopyAsync(_fileSystem, Path.Combine(main, path), copy, cancellationToken: cancellationToken).ConfigureAwait(false)).Content;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                stopped = ex is OperationCanceledException ? "it was interrupted" : ex.Message.TrimEnd('.');
                break;
            }

            if (IsExecutable(copy))
            {
                executable.Add(path);
            }
            else
            {
                executable.Remove(path);
            }

            count++;
        }

        return (seed with
        {
            Paths = handed,
            Empty = seed.Empty && handed.Count == 0,
            Executable = executable.Count > 0 ? [.. executable.Order(StringComparer.Ordinal)] : null,
        }, count, stopped);
    }

    /// <summary>
    /// Which of <paramref name="paths"/> the agent changed: its copy is neither what it was handed nor what its base holds,
    /// compared through the clean filters git compares a working file through - or it deleted one it was handed, or one its
    /// base holds. Copying the main tree's file over any of these would undo the agent's work unseen.
    /// </summary>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="baseCommit">The commit it was made from.</param>
    /// <param name="seed">What it was handed.</param>
    /// <param name="paths">The paths to ask about.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<IReadOnlyList<string>> EditedAsync(string worktree, string baseCommit, SeedRecord seed, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var edited = new List<string>();
        var unseeded = new List<string>();
        var executable = (seed.Executable ?? []).ToHashSet(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var copy = Path.Combine(worktree, path);
            var present = _fileSystem.KindOf(copy) == PathKind.File;

            if (seed.Paths.TryGetValue(path, out var handed))
            {
                if (!present || await IdentityAsync(copy, cancellationToken).ConfigureAwait(false) != new Identity(handed, executable.Contains(path)))
                {
                    edited.Add(path);
                }
            }
            else
            {
                unseeded.Add(path);
            }
        }

        if (unseeded.Count > 0)
        {
            var based = await _gitClient.BlobIdsAtAsync(worktree, baseCommit, unseeded, cancellationToken).ConfigureAwait(false);
            var present = unseeded.Where(path => _fileSystem.KindOf(Path.Combine(worktree, path)) == PathKind.File).ToList();
            var working = await _gitClient.HashWorkingFilesAsync(worktree, present, cancellationToken).ConfigureAwait(false);

            // A file the base holds and the agent no longer does is its deletion; one the base lacks and it lacks is new to
            // it, and handing it over undoes nothing.
            edited.AddRange(unseeded.Where(path => working.TryGetValue(path, out var id) ? based[path] != id : based[path] is not null));
        }

        return [.. edited.Order(StringComparer.Ordinal)];
    }

    /// <summary>Whether <paramref name="one"/> and <paramref name="other"/> are both files, and the same file: content and execute bit.</summary>
    /// <param name="one">A file, or where one may be.</param>
    /// <param name="other">Another.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<bool> SameAsync(string one, string other, CancellationToken cancellationToken)
        => _fileSystem.KindOf(one) == PathKind.File
            && _fileSystem.KindOf(other) == PathKind.File
            && await IdentityAsync(one, cancellationToken).ConfigureAwait(false) == await IdentityAsync(other, cancellationToken).ConfigureAwait(false);

    /// <summary>Whether the file at <paramref name="path"/> could be run - its execute bit set - on a platform with one; never on Windows, which has none.</summary>
    /// <param name="path">A file.</param>
    public bool IsExecutable(string path) => _platform.Current != PlatformId.Windows && _permissions.IsExecutable(path);

    /// <summary>What the two trees hold at <paramref name="path"/>, and whether a link lies along it in either.</summary>
    private async Task<Look> LookAsync(string path, Roots roots, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(roots.Main, path);
        var source = Path.Combine(roots.Agent, path);
        string reachedInMain;
        string reachedInAgent;

        try
        {
            reachedInMain = _fileSystem.ResolveLinks(destination);
            reachedInAgent = _fileSystem.ResolveLinks(source);
        }
        catch (IOException ex)
        {
            return new Look(path, $"a link that cannot be followed ({ex.Message.TrimEnd('.')})", Escapes: false, default, default);
        }

        var comparison = _platform.PathComparison;

        if (!PathContainment.IsStrictlyInside(roots.ReachedMain, reachedInMain, comparison))
        {
            return new Look(path, $"a link in the main tree, to '{reachedInMain}'", Escapes: true, default, default);
        }

        // A path from git never passes through a link in the tree git read it from; the same path in the other tree can,
        // and anything written or read through it would be another file than the one measured.
        if (!PathContainment.AreSame(reachedInMain, Path.Combine(roots.ReachedMain, path), comparison))
        {
            return new Look(path, $"the main tree, where it leads to '{reachedInMain}'", Escapes: false, default, default);
        }

        if (!PathContainment.AreSame(reachedInAgent, Path.Combine(roots.ReachedAgent, path), comparison))
        {
            return new Look(path, $"the agent's worktree, where it leads to '{reachedInAgent}'", Escapes: false, default, default);
        }

        return new Look(
            path,
            Link: null,
            Escapes: false,
            await SideAsync(source, cancellationToken).ConfigureAwait(false),
            await SideAsync(destination, cancellationToken).ConfigureAwait(false));
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
            _ => throw new IOException($"'{path}' became a link while it was being read."),
        };

    /// <summary>What the file at <paramref name="path"/> is: its content, and whether it could be run.</summary>
    private async Task<Identity> IdentityAsync(string path, CancellationToken cancellationToken)
        => new((await FileContentHash.OfAsync(_fileSystem, path, cancellationToken).ConfigureAwait(false)).Content, IsExecutable(path));

    private void RemoveEmptiedDirectories(string main, string? directory)
    {
        while (directory is not null
            && PathContainment.IsStrictlyInside(main, directory, _platform.PathComparison)
            && _fileSystem.DirectoryExists(directory)
            && !_fileSystem.EnumerateDirectories(directory).Any()
            && !_fileSystem.EnumerateFiles(directory, recursive: false).Any())
        {
            _fileSystem.DeleteDirectory(directory);
            directory = Path.GetDirectoryName(directory);
        }
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
}
