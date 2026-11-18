using System.Globalization;
using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Mutations;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Runs;

/// <summary>One build directory of another tree that making room for a leg removed.</summary>
/// <param name="Tree">The tree it was in, as a line calls it: <c>worktree o1/xa</c>.</param>
/// <param name="Directory">The build directory.</param>
/// <param name="Bytes">What was removed of it.</param>
/// <param name="LastBuiltUtc">When a build last wrote its record there: what made it the least recently built.</param>
public sealed record ReclaimedBuild(string Tree, string Directory, long Bytes, DateTime LastBuiltUtc);

/// <summary>What making room for a leg from other trees' idle builds came to.</summary>
/// <param name="Removed">Each build directory removed, the least recently built first.</param>
/// <param name="Before">The room on the filesystem as it was read before anything was removed.</param>
/// <param name="After">The room as it was read once the removing ended; <see langword="null"/> where it could not be read again.</param>
/// <param name="Left">
/// Each idle-looking build there that was not removed, and why: a run or a sweep holding it, a lock that could not be read,
/// or a removal that failed.
/// </param>
public sealed record ReclaimedRoom(IReadOnlyList<ReclaimedBuild> Removed, DiskSpace Before, DiskSpace? After, IReadOnlyList<string> Left)
{
    /// <summary>
    /// The leg making room on the filesystem already, where one was: nothing was looked at or removed, and the room is to be
    /// read again once it has made it.
    /// </summary>
    public RoomMaking? Behind { get; init; }

    /// <summary>Why nothing at all was removed, where something says more than the builds <see cref="Left"/> names.</summary>
    public string? Unmade { get; init; }

    /// <summary>What was removed, in all.</summary>
    public long Bytes => Removed.Sum(build => build.Bytes);

    /// <summary>
    /// What was done as a leg's line says it: <c>removed ~15.8 GiB of other trees' idle builds on '/' to make room:
    /// worktree o1/xa's at '...', last built 2026-11-15 10:02 UTC; 1.9 GiB free before, 17.7 GiB after</c>, with what was
    /// left and why after it; or the leg making room there already, behind which this one waits.
    /// </summary>
    public string Describe()
    {
        if (Behind is { } behind)
        {
            return $"{behind.Describe()} is making room on '{Before.Filesystem}' from other trees' idle builds, so this leg removes none "
                + "beside it, and reads the room again once it has";
        }

        var said = Removed.Count == 0
            ? $"no other tree's idle build on '{Before.Filesystem}' could be removed to make room" + (Unmade is { } unmade ? $": {unmade}" : string.Empty)
            : $"removed ~{DiskSpace.Size(Bytes)} of other trees' idle builds on '{Before.Filesystem}' to make room: "
                + string.Join(", ", Removed.Select(build => $"{build.Tree}'s at '{build.Directory}', last built {IdleBuilds.Said(build.LastBuiltUtc)}"))
                + $"; {DiskSpace.Size(Before.FreeBytes)} free before, "
                + (After is { } after ? $"{DiskSpace.Size(after.FreeBytes)} after" : "and the room after could not be read");

        return Left.Count == 0 ? said : $"{said}; {string.Join("; ", Left)}";
    }
}

/// <summary>
/// The trees whose idle builds a leg may remove to make room on the machine its process runs on, and where the lock that
/// holds each one's builds is kept.
/// </summary>
/// <param name="Trees">
/// The repository's other trees on that machine, the leg's own and its copies left out: the main checkout, each worktree and
/// agent's repository, and each copy one of them keeps in its <c>.legs</c> - or, on a host, its copy of the main checkout
/// and each worktree's copy beside it.
/// </param>
/// <param name="Layout">
/// The repository whose lock file holds the builds of every one of them, or <see langword="null"/> where each is a copy a
/// host keeps, a repository of its own whose lock is its own.
/// </param>
/// <param name="For">Whom the room is made for, as the note left where a build was says it: <c>leg 'linux' of the main checkout</c>.</param>
/// <param name="RunId">The run the leg is part of, which its making of room is recorded under.</param>
public sealed record IdleBuildsScope(IReadOnlyList<RepositoryTree> Trees, HarnessLayout? Layout, string For, string RunId)
{
    /// <summary>
    /// The trees of <paramref name="trees"/> whose idle builds leg <paramref name="leg"/> may remove: every one but its own
    /// tree <paramref name="tree"/>, the tree it works in - <paramref name="workTree"/>, the same or a copy of it - and each
    /// copy its own tree keeps in <c>.legs</c>, which are its own work's.
    /// </summary>
    /// <param name="trees">The repository's trees on the machine the leg's process runs on, as they were listed.</param>
    /// <param name="tree">The leg's own tree.</param>
    /// <param name="workTree">The tree its work runs in: <paramref name="tree"/>, or a copy of it.</param>
    /// <param name="leg">The leg, as the note left where a build was names it.</param>
    /// <param name="layout">The repository whose lock holds every tree's builds, or <see langword="null"/> where each is its own.</param>
    /// <param name="runId">The run the leg is part of.</param>
    /// <param name="comparison">How the machine compares paths.</param>
    public static IdleBuildsScope Beside(RepositoryTreesFound trees, string tree, string workTree, string leg, HarnessLayout? layout, string runId, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(trees);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(workTree);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        bool Own(RepositoryTree candidate)
            => PathContainment.AreSame(candidate.Root, workTree, comparison)
                || PathContainment.AreSame(candidate.Root, tree, comparison)
                || (candidate.LegCopy && PathContainment.IsStrictlyInside(tree, candidate.Root, comparison));

        var named = trees.Trees.FirstOrDefault(candidate => PathContainment.AreSame(candidate.Root, workTree, comparison))?.Name ?? $"'{workTree}'";

        return new IdleBuildsScope([.. trees.Trees.Where(candidate => !Own(candidate))], layout, $"leg '{leg}' of {named}", runId);
    }
}

/// <summary>
/// Makes room for a heavy leg from the build directories the repository's other trees keep on the same filesystem of its
/// machine that nothing holds (<see cref="Configuration.AdmissionSettings.ReclaimIdleBuilds"/>): the least recently built
/// first, one at a time, until what was removed covers what the leg is to have made - and never one a run's lock names,
/// one a sweep of its variant in that tree holds, or one of the leg's own tree; and none at all where every one that could
/// be removed comes to less than the least the leg must have. Each is removed as <c>clean --legs</c> removes a build
/// directory (<see cref="BuildDirectoryRemoval"/>), and leaves a note where it was that its tree's next build says.
/// </summary>
/// <remarks>
/// <para>
/// A build directory is one below a tree's <c>build</c> holding the record a build of this tool writes as it begins
/// (<see cref="BuildRecord.FileName"/>): what a build of any variant left, whatever the tree's configuration says now, and
/// never a directory the tool did not make. A link there is left: what it holds is wherever it points. The least recently
/// built is the one whose record was written longest ago. Only directories on the filesystem that is short count: removing
/// one elsewhere gives that filesystem nothing - nor does removing anything inside a WSL distribution give the drive its
/// disk grows on any room back. What each holds is what its last finished build recorded, or, where none did, what it holds.
/// </para>
/// <para>
/// One leg makes room on a filesystem at a time, across every command this user runs on the machine
/// (<see cref="HeavyLegSlots.MakeRoomOn"/>): a leg that finds another making room there removes nothing beside it, and is
/// told so, to read the room again once that one has; the room is read again once the making is held, and what another
/// leg made since the caller read it is room the leg no longer lacks.
/// </para>
/// <para>
/// A consumer's arm64 host of 47 GiB held five trees' copies, a full build taking about 16 GiB: with other trees' idle
/// builds there, two legs were stopped at the floor mid-build, and removing an idle tree's build by hand made the room.
/// </para>
/// </remarks>
/// <param name="runLock">Reads which run or sweep holds a build.</param>
/// <param name="fileSystem">Lists, measures and removes the builds.</param>
/// <param name="slots">The machine's heavy-leg records, which hold which leg makes room on each filesystem.</param>
/// <param name="clock">When a build was removed, as its note says it.</param>
public sealed class IdleBuilds(RunLock runLock, IFileSystem fileSystem, HeavyLegSlots slots, TimeProvider clock)
{
    /// <summary>The command a removal asks the lock under: the leg's admission's.</summary>
    public const string CommandName = HeavyLegSlots.CommandName;

    private readonly RunLock _runLock = runLock;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly HeavyLegSlots _slots = slots;
    private readonly TimeProvider _clock = clock;
    private readonly BuildDirectoryRemoval _removal = new(runLock, fileSystem);

    /// <summary>
    /// Removes idle builds of <paramref name="scope"/>'s trees on the filesystem <paramref name="read"/> was read on, the
    /// least recently built first, until what was removed comes to <paramref name="most"/> - and none where all that could
    /// be removed comes to less than <paramref name="least"/>, which would make the leg no room it can go on with. Both are
    /// what the leg lacks beside <paramref name="read"/>: room another leg made there since is counted as made.
    /// <see langword="null"/> where nothing is to be removed - the room is there now, or no tree keeps a build there - or
    /// where the room there cannot be read.
    /// </summary>
    /// <param name="scope">The trees, the lock that holds their builds, and whom the room is made for.</param>
    /// <param name="read">The room on the filesystem that is short of it, as the leg read it; its filesystem is a path there.</param>
    /// <param name="least">The least the leg must have made, without which nothing is removed.</param>
    /// <param name="most">The most it is to have made, at least <paramref name="least"/>.</param>
    public ReclaimedRoom? MakeRoom(IdleBuildsScope scope, DiskSpace read, long least, long most)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfLessThan(most, least);

        if (least <= 0)
        {
            return null;
        }

        RoomMakingHold making;

        try
        {
            making = _slots.MakeRoomOn(scope.RunId, scope.For, read.Filesystem);
        }
        catch (HarnessException ex)
        {
            return new ReclaimedRoom([], read, null, []) { Unmade = ex.Message.TrimEnd('.') };
        }

        using (making)
        {
            if (making.By is { } by)
            {
                return new ReclaimedRoom([], read, null, []) { Behind = by };
            }

            // Read again now that no other leg makes room here: what one made since the leg read the room, it lacks no more.
            if (DiskSpace.Measure(_fileSystem, read.Filesystem).Space is not { } before)
            {
                return null;
            }

            var made = before.FreeBytes - read.FreeBytes;

            if (least - made <= 0)
            {
                return null;
            }

            return Remove(scope, before, least - made, most - made);
        }
    }

    /// <summary>A time as a line about a build says it: <c>2026-11-15 10:02 UTC</c>.</summary>
    /// <param name="utc">The time, in UTC.</param>
    internal static string Said(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Removes what <see cref="MakeRoom"/> decided to, holding the making of room on <paramref name="before"/>'s filesystem:
    /// the builds no run or sweep holds, weighed first, and removed only where they come to <paramref name="least"/>.
    /// </summary>
    private ReclaimedRoom? Remove(IdleBuildsScope scope, DiskSpace before, long least, long most)
    {
        var left = new List<string>();
        var candidates = Candidates(scope, before.Filesystem, left);

        if (candidates.Count == 0 && left.Count == 0)
        {
            return null;
        }

        // Weighed before any is removed: where all that could be removed comes to less than the least the leg must have, it
        // would destroy every one of them and still not let the leg go on.
        var free = new List<Candidate>();

        foreach (var candidate in candidates)
        {
            if (HeldOrUnread(scope, candidate, left))
            {
                continue;
            }

            free.Add(candidate);
        }

        var removable = free.Sum(candidate => candidate.Bytes);

        if (removable < least)
        {
            return new ReclaimedRoom([], before, before, left)
            {
                Unmade = free.Count == 0
                    ? null
                    : $"what no run or sweep holds there - {ReportText.Counted(free.Count, "build")} - comes to ~{DiskSpace.Size(removable)}, "
                        + $"less than the ~{DiskSpace.Size(least)} the leg lacks, so none of it was removed",
            };
        }

        var removed = new List<ReclaimedBuild>();
        var freed = 0L;

        foreach (var candidate in free)
        {
            if (freed >= most)
            {
                break;
            }

            var (tree, directory, lastBuilt, _) = candidate;

            try
            {
                // Asked again as it is removed: a run or a sweep may have taken it since it was weighed.
                if (HeldOrUnread(scope, candidate, left))
                {
                    continue;
                }

                var removal = _removal.Remove(LayoutOf(scope, tree), tree.Root, directory, PlacedLeg.BuildLock(HostId.Local, tree.Root, Path.GetFileName(directory), RunId.New(), CommandName));

                // What earlier removals of it had left aside is room made, whether or not a run took the build meanwhile.
                freed += removal.Bytes;

                if (removal.HeldBy is { } holder)
                {
                    left.Add($"{tree.Name}'s build at '{directory}' was left, as a run holds it: {holder}");
                    continue;
                }

                if (!removal.Removed)
                {
                    continue;
                }

                removed.Add(new ReclaimedBuild(tree.Name, directory, removal.Bytes, lastBuilt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add($"{tree.Name}'s build at '{directory}' could not be removed: {ex.Message.TrimEnd('.')}");
                continue;
            }
            catch (HarnessException ex)
            {
                left.Add($"{tree.Name}'s build at '{directory}' was left, as its tree's lock could not be read: {ex.Message.TrimEnd('.')}");
                continue;
            }

            // Said by its tree's next build, which starts from nothing. The build is gone whether or not the note is left.
            try
            {
                ReclaimedBuildNote.Write(_fileSystem, directory, $"its build directory was removed {Said(_clock.GetUtcNow().UtcDateTime)}, to make room for {scope.For}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add($"no note was left where {tree.Name}'s build at '{directory}' was, so its next build will not say why it starts from nothing: {ex.Message.TrimEnd('.')}");
            }
        }

        return new ReclaimedRoom(removed, before, DiskSpace.Measure(_fileSystem, before.Filesystem).Space, left);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is not to be removed now - a sweep of its variant in its tree holds it, or a run
    /// building it, or its tree's lock cannot be read, which tells neither - each said in <paramref name="left"/>.
    /// </summary>
    private bool HeldOrUnread(IdleBuildsScope scope, Candidate candidate, List<string> left)
    {
        var (tree, directory, _, _) = candidate;
        var layout = LayoutOf(scope, tree);
        var variant = Path.GetFileName(directory);

        try
        {
            // A sweep of the variant in that tree holds it as well as a build does: the sweep's own lock, which no build
            // takes, asked first.
            if (_runLock.HeldBy(layout, MutationWorkers.SweepLock(HostId.Local, WorkerFamily.Of(tree.Root, VariantKey.CopyKeyOf(variant)), RunId.New(), CommandName)) is { } sweeping)
            {
                left.Add($"{tree.Name}'s build at '{directory}' was left, as a sweep holds it: {sweeping}");
                return true;
            }

            if (_runLock.HeldBy(layout, PlacedLeg.BuildLock(HostId.Local, tree.Root, variant, RunId.New(), CommandName)) is { } building)
            {
                left.Add($"{tree.Name}'s build at '{directory}' was left, as a run holds it: {building}");
                return true;
            }

            return false;
        }
        catch (HarnessException ex)
        {
            left.Add($"{tree.Name}'s build at '{directory}' was left, as its tree's lock could not be read: {ex.Message.TrimEnd('.')}");
            return true;
        }
    }

    /// <summary>The lock that holds <paramref name="tree"/>'s builds: the repository's, or a host's copy's own.</summary>
    private static HarnessLayout LayoutOf(IdleBuildsScope scope, RepositoryTree tree) => scope.Layout ?? new HarnessLayout(tree.Root, tree.Root);

    /// <summary>
    /// Every build directory <paramref name="scope"/>'s trees keep on <paramref name="filesystem"/>, the least recently
    /// built first, with what each holds. A tree whose build root cannot be looked in has nothing of it removed, and is said
    /// in <paramref name="left"/>.
    /// </summary>
    private List<Candidate> Candidates(IdleBuildsScope scope, string filesystem, List<string> left)
    {
        var found = new List<Candidate>();

        foreach (var tree in scope.Trees)
        {
            var root = Path.Combine(tree.Root, VariantKey.BuildRootName);

            try
            {
                if (!_fileSystem.DirectoryExists(root) || _fileSystem.IsLink(root))
                {
                    continue;
                }

                foreach (var directory in _fileSystem.EnumerateDirectories(root))
                {
                    var record = Path.Combine(directory, BuildRecord.FileName);

                    // An aside a removal left, a link, and a directory no build of this tool recorded are none of its.
                    if (RemovalAside.Was(Path.GetFileName(directory)) is not null
                        || _fileSystem.IsLink(directory)
                        || !_fileSystem.FileExists(record)
                        || DiskSpace.Measure(_fileSystem, directory).Space?.Filesystem is not { } on
                        || !string.Equals(on, filesystem, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    found.Add(new Candidate(tree, directory, _fileSystem.LastWriteTimeUtc(record), BuildRecord.BytesIn(_fileSystem, directory) ?? _fileSystem.DirectorySize(directory)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add($"{tree.Name}'s builds at '{root}' could not be looked at: {ex.Message.TrimEnd('.')}");
            }
        }

        return [.. found.OrderBy(candidate => candidate.LastBuilt)];
    }

    /// <summary>A build directory of another tree that may be removed, when a build last recorded it, and what it holds.</summary>
    private sealed record Candidate(RepositoryTree Tree, string Directory, DateTime LastBuilt, long Bytes);
}
