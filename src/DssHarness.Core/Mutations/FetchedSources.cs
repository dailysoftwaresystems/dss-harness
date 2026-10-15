using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>One dependency a leg's own build has the sources of: the name FetchContent declared it under, and where they are.</summary>
/// <param name="Name">The name, as its <c>FETCHCONTENT_SOURCE_DIR_&lt;NAME&gt;</c> cache entry spells it.</param>
/// <param name="Directory">Where the leg's build has its sources.</param>
internal sealed record FetchedSource(string Name, string Directory);

/// <summary>One dependency's sources as a sweep read them, once, for every worker to be given.</summary>
/// <param name="Source">The dependency.</param>
/// <param name="Files">Every file of its sources, by size and hash.</param>
internal sealed record FetchedReading(FetchedSource Source, SyncManifest Files)
{
    /// <summary>What the files come to together.</summary>
    public long Bytes => Files.Entries.Values.Sum(entry => entry.Size);
}

/// <summary>The dependency sources a leg's own build has, of those FetchContent declared in it.</summary>
/// <param name="Found">Each dependency whose sources are there.</param>
/// <param name="Every">Whether every dependency the build declared is among them.</param>
internal sealed record FetchedSet(IReadOnlyList<FetchedSource> Found, bool Every)
{
    /// <summary>No dependency: a leg never built, or one whose build declared none.</summary>
    public static FetchedSet None { get; } = new([], Every: false);
}

/// <summary>
/// The dependency sources a leg's own build fetched, given to every worker of its sweep: what a registry once had to name
/// by hand, one F row to each, read now from the leg's own CMake cache.
/// </summary>
/// <remarks>
/// <para>
/// The cache holds one <c>FETCHCONTENT_SOURCE_DIR_&lt;NAME&gt;</c> entry to each dependency FetchContent declared, and
/// each one the build populated is in <c>&lt;FETCHCONTENT_BASE_DIR&gt;/&lt;name&gt;-src</c> - or where the entry names, for
/// one the leg itself was pointed at. A worker's configure is pointed at each that is there, so every worker builds the
/// very dependency sources the leg's own build did; and told it is fully disconnected only where every declared
/// dependency was found, so a worker the network cannot reach still configures, and one with a dependency missing
/// fetches it as the leg would.
/// </para>
/// <para>
/// Carried into each worker, by content, from one reading the sweep takes of them as it takes one of the tree: they are
/// in the leg's own build directory, which a clean of the leg removes and a build of it may fetch again while a sweep of
/// hours runs. Pointed at in place, every arm after such a clean failed at its build, and every arm after such a build
/// was measured against other sources than the arms before it - each blamed on the arm. A worker keeps them in the
/// harness's own directory within it (<see cref="KeptIn(string, string)"/>), which no sync of the tree carries or
/// deletes and no build from clean removes. Sources the tree itself holds - a directory of it the leg was pointed at -
/// are the worker's own copy of that directory, which its sites are mutated in.
/// </para>
/// </remarks>
internal static class FetchedSources
{
    /// <summary>What turns FetchContent's fetching off, where every dependency is found.</summary>
    public const string FullyDisconnected = "FETCHCONTENT_FULLY_DISCONNECTED";

    /// <summary>
    /// The cache entries every configure of a worker removes before it sets its own: every dependency's source
    /// directory, and what turns fetching off. A worker's build directory stays between sweeps, and its cache would go
    /// on holding what an earlier sweep's configure was given - a directory the worker no longer keeps, or fetching
    /// turned off over a dependency no longer found. Removed first, each is what this sweep gives, what the project
    /// sets itself, or the project's own default.
    /// </summary>
    public static IReadOnlyList<string> Unset { get; } = [BuildDirectoryGuard.FetchContentSource + "*", FullyDisconnected];

    /// <summary>
    /// The dependencies the leg's build <paramref name="record"/> says FetchContent declared whose sources are there;
    /// none where it says none, or there is no record - a leg never built, whose workers configure as it would, and fetch.
    /// </summary>
    /// <param name="record">What the leg's own build directory was configured with, or <see langword="null"/>.</param>
    /// <param name="fileSystem">Tells which sources are there.</param>
    public static FetchedSet Of(BuildDirectoryRecord? record, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (record is null || record.FetchContentSources.Count == 0)
        {
            return FetchedSet.None;
        }

        var found = new List<FetchedSource>();

        foreach (var (name, named) in record.FetchContentSources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var source = named.Length > 0 ? named
                : record.FetchContentBaseDirectory is { Length: > 0 } fetchedInto ? Path.Combine(fetchedInto, name.ToLowerInvariant() + "-src")
                : null;

            if (source is not null && fileSystem.DirectoryExists(source))
            {
                found.Add(new FetchedSource(name, source));
            }
        }

        return new FetchedSet(found, found.Count == record.FetchContentSources.Count);
    }

    /// <summary>Where a worker keeps the dependency sources it is given, one directory to each.</summary>
    /// <param name="worker">The worker's copy.</param>
    public static string KeptIn(string worker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);

        return Path.Combine(worker, HarnessLayout.DirectoryName, HarnessLayout.DependencySourcesDirectoryName);
    }

    /// <summary>
    /// Where a worker keeps the sources of the dependency <paramref name="name"/>: under its name in lower case, as
    /// FetchContent names where it fetches one.
    /// </summary>
    /// <param name="worker">The worker's copy.</param>
    /// <param name="name">The dependency's name.</param>
    public static string KeptIn(string worker, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Path.Combine(KeptIn(worker), name.ToLowerInvariant());
    }

    /// <summary>
    /// Where the sources of <paramref name="source"/> are within the tree at <paramref name="treeRoot"/>, relative to
    /// it with forward separators, where <paramref name="reading"/> carries them: so each worker has them already, in
    /// its own copy of the tree. <see langword="null"/> where it carries none of them - they are outside the tree, or in
    /// what a sync withholds from it, as the leg's own build directory is.
    /// </summary>
    /// <param name="source">The dependency.</param>
    /// <param name="treeRoot">The tree the sweep read.</param>
    /// <param name="reading">Every file of the tree a sync carries.</param>
    /// <param name="names">How the tree's file system compares names.</param>
    public static string? CarriedAt(FetchedSource source, string treeRoot, SyncManifest reading, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(names);

        // Outside the tree it is no path any file of the reading is beneath: one that climbs out of it, or the tree itself.
        var within = ManifestBuilder.Relative(Path.GetFullPath(treeRoot), Path.GetFullPath(source.Directory)).TrimEnd('/');
        var beneath = within + "/";

        return reading.Entries.Keys.Any(path => path.Length > beneath.Length && names.Equals(path[..beneath.Length], beneath)) ? within : null;
    }

    /// <summary>
    /// The cache variables a worker configures with, beneath the project's own, for the dependencies of
    /// <paramref name="fetched"/>: each pointed at where <paramref name="where"/> says the worker has its sources,
    /// spelt with forward separators, as CMake spells every path it is given; and fetching turned off where every
    /// dependency the leg's build declared was found.
    /// </summary>
    /// <param name="fetched">The dependency sources the leg's own build has.</param>
    /// <param name="where">Where the worker has the sources of a dependency.</param>
    public static IReadOnlyDictionary<string, string> CacheVarsFor(FetchedSet fetched, Func<FetchedSource, string> where)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        ArgumentNullException.ThrowIfNull(where);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in fetched.Found)
        {
            variables[BuildDirectoryGuard.FetchContentSource + source.Name] = where(source).Replace('\\', '/');
        }

        if (fetched.Every)
        {
            variables[FullyDisconnected] = "ON";
        }

        return variables;
    }
}
