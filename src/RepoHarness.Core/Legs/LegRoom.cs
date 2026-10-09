using System.Globalization;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Legs;

/// <summary>What a placed leg's build still needs of the room on the machine that admits it, and what said so.</summary>
/// <param name="Bytes">What it still needs, more than nothing: a build that needs nothing more claims nothing.</param>
/// <param name="Source">What said how much, as a line says it: <c>as its buildSpaceGiB, 4, declares</c>.</param>
/// <param name="At">
/// A path of the admitting machine on the filesystem the build fills - its build directory, or for a WSL distribution's
/// leg the drive of this machine where WSL keeps the distribution's disk - read again as the leg is admitted;
/// <see langword="null"/> where that drive could not be measured as the leg was placed.
/// </param>
/// <param name="Where">How a line names that room beyond its filesystem: empty, or <c>, where WSL keeps its disk</c>.</param>
/// <param name="Unmeasured">Why that drive could not be measured as the leg was placed, where <paramref name="At"/> is unknown.</param>
/// <remarks>
/// Decided here, with the rooms a build fills, and nowhere else: counted, as the leg is placed, beside the other legs of
/// its command; then held, as it is admitted, against what every other admitted leg on that machine claims - this one,
/// for a leg here or of a WSL distribution, and the host itself for one an ssh host runs, which places it again there.
/// </remarks>
public sealed record RoomNeed(long Bytes, string Source, string? At, string Where, string? Unmeasured = null);

/// <summary>
/// Whether a leg's host has the room its build still needs: what is asked of each host before placing, and
/// the legs placed where there is not enough.
/// </summary>
/// <remarks>
/// A consumer's two variants, the first builds of a new worktree's copy, filled a host's disk half way
/// through and died writing an object, while <c>legs</c> said the host could run them. A leg's need is what
/// its build directory comes to once built - declared as <see cref="LegConfig.BuildSpaceGiB"/>, or what a
/// build of its variant recorded there: in the tree's own copy, or else the most any other copy of the
/// repository there recorded, the main checkout's or a worktree's - less what that directory already holds. Nothing is walked: each build records what its directory came to as it
/// finishes. Legs sharing a filesystem on one host are counted together, in the order they were selected,
/// because every build directory stays once built; a leg that no longer fits beside the ones before it is
/// turned away, and they are kept. A leg whose need nothing says is placed as it always was; one whose need
/// is known where the room could not be measured is placed and said to be unchecked.
/// </remarks>
public static class LegRoom
{
    private const long Gibibyte = 1L << 30;

    /// <summary>
    /// What to ask each candidate host about the room on it: where its copies are kept, and the build
    /// directory there of each selected leg the command builds, with the main checkout's copy of the same
    /// variant.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="candidates">Each selected leg, with the hosts it could be placed on.</param>
    /// <param name="here">The host this machine is to the machine that sent the legs here, or <see langword="null"/>.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="workload">
    /// What the command has each leg do: a leg it builds - on that leg's system - has a build directory worth asking
    /// about, and one it does not build, or builds no tree of its own, has none worth asking about.
    /// </param>
    /// <param name="trees">The trees of the repository each host holds, where they were looked for.</param>
    public static IReadOnlyDictionary<HostId, RoomQuestions> Questions(
        HarnessContext context,
        IEnumerable<(SelectedLeg Leg, IReadOnlyList<HostId> Hosts)> candidates,
        HostId? here,
        StringComparison comparison,
        LegWorkload workload,
        IReadOnlyDictionary<HostId, RepositoryTreesFound> trees)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(trees);

        var asked = new Dictionary<HostId, (string? SpaceAt, List<string> Builds)>();

        foreach (var (leg, hosts) in candidates)
        {
            // Asked about nothing a build fills where the build fills none of the leg's tree: what is not asked about is
            // answered for no leg, which is then placed by no room its build needs.
            var builds = workload.On(leg.Leg.Os) is { Build: true, BuildsTheLegsTree: true };
            var sweeps = workload.On(leg.Leg.Os).AdmitsEachUnit;

            foreach (var host in hosts)
            {
                // A host that declares nowhere to keep a copy is refused where the leg is placed on it; asked
                // nothing here.
                if (Paths(context, leg.Leg, host, here, comparison, sweeps, trees) is not { } paths)
                {
                    continue;
                }

                if (!asked.TryGetValue(host, out var questions))
                {
                    questions = (paths.Main, []);
                    asked[host] = questions;
                }

                if (builds)
                {
                    questions.Builds.AddRange(new[] { paths.Fills, paths.Own }.Concat(paths.Siblings.Select(sibling => sibling.Build)).Distinct(StringComparer.Ordinal));
                }
            }
        }

        return asked.ToDictionary(pair => pair.Key, pair => new RoomQuestions(pair.Value.SpaceAt, [.. pair.Value.Builds.Distinct(StringComparer.Ordinal)]));
    }

    /// <summary>
    /// <paramref name="placements"/>, with each placed leg whose host lacks the room its build still needs
    /// turned away - skipped-unavailable, saying what is free and what it needs - and each leg placed with a
    /// known need where the room could not be measured, said as that.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="placements">Where each selected leg was placed, in the order the legs were selected.</param>
    /// <param name="here">The host this machine is to the machine that sent the legs here, or <see langword="null"/>.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="workload">
    /// What the command has each leg do: a sweep of its mutation arms fills the build directory of its first worker,
    /// never its own, and is placed by the room that worker's build needs - the least it runs with; a self-test of the
    /// sweep, which builds none of the leg's tree, by none, since <see cref="Questions"/> asked no host about any.
    /// </param>
    /// <param name="trees">The trees of the repository each host holds, as <see cref="Questions"/> was given them.</param>
    public static (IReadOnlyList<LegPlacement> Placements, IReadOnlyList<string> Unchecked) Apply(
        HarnessContext context,
        IReadOnlyList<LegPlacement> placements,
        HostId? here,
        StringComparison comparison,
        LegWorkload workload,
        IReadOnlyDictionary<HostId, RepositoryTreesFound> trees)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(trees);

        var placed = placements.ToList();
        var taken = new Dictionary<(HostId Host, string Filesystem), (long Bytes, List<string> Legs)>();
        var directories = new HashSet<(HostId Host, string Directory)>();
        var unmeasured = new List<string>();

        for (var index = 0; index < placed.Count; index++)
        {
            if (placed[index] is not { Host: { } host } placement
                || Need(context, placement.Leg, host, here, comparison, workload.On(placement.Leg.Leg.Os).AdmitsEachUnit, trees) is not { } need)
            {
                continue;
            }

            // Counted once however many legs name it: two legs building one directory are refused as that when
            // the run is planned, and would otherwise be refused here for room the second never takes.
            if (!directories.Add((host.Host, need.Directory)))
            {
                continue;
            }

            // Where the leg's build fills: the filesystem its build directory is on, and for a WSL distribution
            // the drive of this machine its disk grows on, which this machine's own legs fill too.
            var rooms = new List<(HostId Machine, DiskSpace Disk, string Where)>();

            foreach (var (machine, disk, why, where) in Rooms(host, need))
            {
                if (disk is null)
                {
                    unmeasured.Add(where.Length == 0
                        ? $"leg '{placement.Leg.Name}' was placed on {host.Host} without its room checked, which could not be measured there: {why}"
                        : $"leg '{placement.Leg.Name}' was placed on {host.Host} without the room checked on this machine's drive where WSL "
                            + $"keeps its disk, which could not be measured: {why}");
                    continue;
                }

                rooms.Add((machine, disk, where));
            }

            var lacking = rooms
                .Select(room => (Room: room, Before: taken.TryGetValue((room.Machine, room.Disk.Filesystem), out var counted) ? counted : (Bytes: 0L, Legs: new List<string>())))
                .FirstOrDefault(room => room.Before.Bytes + need.Bytes > room.Room.Disk.FreeBytes);

            if (lacking.Room.Disk is { } full)
            {
                var before = lacking.Before;
                var beside = before.Legs.Count == 0
                    ? string.Empty
                    : $", beside the ~{DiskSpace.Size(before.Bytes)} {string.Join(" and ", before.Legs.Select(name => $"'{name}'"))} need there";

                placed[index] = new LegPlacement(
                    placement.Leg,
                    null,
                    (here is null ? $"{host.Host}: " : string.Empty)
                    + full.Against(DiskSpace.Needs("this leg", need.Bytes, need.Source), lacking.Room.Where, beside))
                {
                    Verdict = LegVerdict.SkippedUnavailable,
                };

                continue;
            }

            foreach (var room in rooms)
            {
                var key = (room.Machine, room.Disk.Filesystem);
                var before = taken.TryGetValue(key, out var counted) ? counted : (Bytes: 0L, Legs: new List<string>());
                taken[key] = (before.Bytes + need.Bytes, [.. before.Legs, placement.Leg.Name]);
            }

            // Carried to the leg's admission, which holds it against every other admitted leg on the machine that admits
            // it: counted here, the room is this command's alone. Only the room of this machine, which admits the leg - an
            // ssh host places and admits its own - and only where the build needs more than its directory holds.
            if (need.Bytes > 0 && Rooms(host, need).FirstOrDefault(room => room.Machine.Kind == HostKind.Local) is { Machine: not null } mine)
            {
                var at = mine.Where.Length == 0 ? need.Directory : mine.Disk?.Filesystem;

                placed[index] = placement with { Need = new RoomNeed(need.Bytes, need.Source, at, mine.Where, at is null ? mine.Why : null) };
            }
        }

        return (placed, unmeasured);
    }

    /// <summary>
    /// Each room <paramref name="need"/> takes on <paramref name="host"/>: the filesystem of its build directory,
    /// and for a WSL distribution this machine's drive holding its disk - keyed by this machine, since this
    /// machine's own legs fill it too - each with why it could not be measured where it could not, and how a
    /// reason names it.
    /// </summary>
    private static IEnumerable<(HostId Machine, DiskSpace? Disk, string? Why, string Where)> Rooms(HostReport host, Needed need)
    {
        yield return (host.Host, need.Disk, need.Unmeasured, string.Empty);

        if (host.Host.Kind == HostKind.Wsl && (host.DiskImageSpace is not null || host.DiskImageUnmeasured is not null))
        {
            yield return (HostId.Local, host.DiskImageSpace, host.DiskImageUnmeasured, ", where WSL keeps its disk");
        }
    }

    /// <summary>What a leg's build still needs on its host, and what is known of the room there.</summary>
    /// <remarks>Counted against the room of one command's legs; <see cref="RoomNeed"/> is what reaches its admission.</remarks>
    /// <param name="Bytes">What it still needs.</param>
    /// <param name="Disk">The room where its build directory is, or <see langword="null"/> where it could not be measured.</param>
    /// <param name="Source">What said how much it needs.</param>
    /// <param name="Directory">Its build directory there.</param>
    /// <param name="Unmeasured">Why the room could not be measured, where it could not.</param>
    private sealed record Needed(long Bytes, DiskSpace? Disk, string Source, string Directory, string? Unmeasured);

    /// <summary>
    /// What <paramref name="leg"/>'s build still needs on <paramref name="host"/>, where something says, with
    /// the room where its build directory is and what said so; <see langword="null"/> where nothing does.
    /// </summary>
    private static Needed? Need(
        HarnessContext context,
        SelectedLeg leg,
        HostReport host,
        HostId? here,
        StringComparison comparison,
        bool sweeps,
        IReadOnlyDictionary<HostId, RepositoryTreesFound> trees)
    {
        if (Paths(context, leg.Leg, host.Host, here, comparison, sweeps, trees) is not { } paths
            || Answered(host, paths.Own) is not { } own
            || Answered(host, paths.Fills) is not { } fills
            || ExpectedBuildBytes(leg.Leg, own.RecordedBytes, paths.Siblings.Select(sibling => (sibling.Tree, Answered(host, sibling.Build)?.RecordedBytes)))
                is not var (bytes, source))
        {
            return null;
        }

        // What the directory holds counts against its need only where its build recorded it: one there that no
        // build of this version recorded holds an amount nothing measured, and is left to build as it always did.
        long? present = !fills.Exists ? 0 : fills.RecordedBytes;

        return present is { } held
            ? new Needed(Math.Max(0, bytes - held), fills.Disk, source, paths.Fills, fills.Unmeasured ?? "no reason was given")
            : null;
    }

    /// <summary>
    /// What a build of <paramref name="leg"/>'s variant is expected to come to, and what said so: its
    /// <see cref="LegConfig.BuildSpaceGiB"/> where it declares one, else what its last build recorded its directory came
    /// to, else the most any other tree's copy of the same variant on that machine came to, naming whose;
    /// <see langword="null"/> where nothing says.
    /// </summary>
    /// <param name="leg">The leg.</param>
    /// <param name="ownRecorded">What its own build directory's last build recorded, where one did.</param>
    /// <param name="others">
    /// Each other tree of the repository on that machine - the main checkout's first - as a line names it, with what its
    /// copy of the same variant recorded, where one did. The leg's own tree may be among them: what it recorded is
    /// <paramref name="ownRecorded"/>, taken first.
    /// </param>
    /// <remarks>
    /// The one reckoning of a build's size: a leg's own build is placed by it, and each worker of a sweep of the leg's
    /// arms, which builds the same variant in a copy of its own, is counted by it. The most, of the others: one variant
    /// comes to about as much in every tree, and a need said over keeps a leg out until there is room, where one said
    /// under fills a disk under every leg building on it.
    /// </remarks>
    public static (long Bytes, string Source)? ExpectedBuildBytes(LegConfig leg, long? ownRecorded, IEnumerable<(string Tree, long? Recorded)> others)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(others);

        if (leg.BuildSpaceGiB is { } declared)
        {
            return ((long)Math.Ceiling(declared * Gibibyte), string.Create(CultureInfo.InvariantCulture, $"as its buildSpaceGiB, {declared:0.###}, declares"));
        }

        if (ownRecorded is { } recorded)
        {
            return (recorded, "what its last build there came to");
        }

        (long Bytes, string Source)? most = null;

        foreach (var (tree, came) in others)
        {
            if (came is { } bytes && (most is null || bytes > most.Value.Bytes))
            {
                most = (bytes, $"what {tree}'s copy of the same variant came to there");
            }
        }

        return most;
    }

    /// <summary>What <paramref name="host"/> answered about the build directory at <paramref name="path"/>, as it was asked.</summary>
    private static BuildDirectoryRoom? Answered(HostReport host, string path)
        => host.Builds.FirstOrDefault(build => string.Equals(build.Path, path, StringComparison.Ordinal));

    /// <summary>
    /// The paths <paramref name="leg"/> has on <paramref name="host"/>: where the host keeps the main checkout's
    /// copy, the leg's own build directory there, the build directory the command fills - the leg's own, or for a sweep
    /// of its mutation arms its first worker's - and each other tree's copy of the same variant there, the main checkout's
    /// first, with what a line calls that tree; or <see langword="null"/> where the host declares nowhere to keep a copy.
    /// </summary>
    /// <remarks>
    /// The variant for the leg's own operating system: a host of another is never given the leg, so the variant
    /// it would have there is never built.
    /// </remarks>
    private static (string Main, string Own, string Fills, IReadOnlyList<(string Tree, string Build)> Siblings)? Paths(
        HarnessContext context,
        LegConfig leg,
        HostId host,
        HostId? here,
        StringComparison comparison,
        bool sweeps,
        IReadOnlyDictionary<HostId, RepositoryTreesFound> trees)
    {
        try
        {
            var variant = VariantKey.For(context.Config, leg, leg.Os);
            var main = LegTrees.On(context, host, context.Layout.MainCheckoutRoot, comparison);
            var own = LegTrees.On(context, host, LegTrees.Here(context, leg, here), comparison);

            // On a host running a leg another machine sent it, the tree here is a copy - a repository of its own, whose main
            // checkout is itself - so the main checkout's copy, which a worktree copy's first build there is measured
            // against, is where the configuration says this host keeps it. Measured against itself instead, that build
            // needed nothing anyone said, and claimed no room as it was admitted.
            var mainCopy = here is { Kind: not HostKind.Local } ? HostCopies.RepositoryPathOf(context.Config, here) : main;

            var built = variant.DirectoryOn(host, own);

            // The main checkout's copy is known without listing anything, so it is measured whether or not the trees beside
            // it could be listed. The leg's own tree may be among the rest: what its own build recorded is taken first, and
            // where it recorded nothing it has nothing to add.
            IReadOnlyList<(string Tree, string Build)> siblings =
            [
                (RepositoryTree.MainCheckout, variant.DirectoryOn(host, mainCopy)),
                .. (trees.GetValueOrDefault(host)?.Trees ?? []).Select(tree => (tree.Name, variant.DirectoryOn(host, tree.Root))),
            ];

            return (main, built, sweeps ? variant.DirectoryOn(host, MutationWorkers.PathOf(own, variant, 1)) : built, siblings);
        }
        catch (HarnessException)
        {
            return null;
        }
    }
}
