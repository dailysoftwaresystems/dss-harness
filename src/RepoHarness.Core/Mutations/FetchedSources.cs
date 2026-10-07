using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// The dependency sources a leg's own build fetched, given to every worker of its sweep: what a registry once had to name
/// by hand, one F row to each, read now from the leg's own CMake cache.
/// </summary>
/// <remarks>
/// <para>
/// The cache holds one <c>FETCHCONTENT_SOURCE_DIR_&lt;NAME&gt;</c> entry to each dependency FetchContent declared, and
/// each one the build populated is in <c>&lt;FETCHCONTENT_BASE_DIR&gt;/&lt;name&gt;-src</c> - or where the entry names, for
/// one the leg itself was pointed at. A worker's configure is pointed at each that is there, so every worker, and the
/// leg's own build, build the very same dependency sources; and told it is fully disconnected only where every declared
/// dependency was found, so a worker the network cannot reach still configures, and one with a dependency missing
/// fetches it as the leg would.
/// </para>
/// <para>
/// Pointed at in place, in the leg's own build directory, rather than carried into each worker: what a sync copies is
/// a tree, and these are outside it. A clean of the leg's build directory while a sweep runs takes them from under the
/// workers, whose next configure then fails, as a build of the leg would.
/// </para>
/// </remarks>
internal static class FetchedSources
{
    /// <summary>What turns FetchContent's fetching off, where every dependency is found.</summary>
    public const string FullyDisconnected = "FETCHCONTENT_FULLY_DISCONNECTED";

    /// <summary>
    /// The cache variables a worker configures with, beneath the project's own, for the dependencies the leg's build
    /// <paramref name="record"/> says FetchContent declared; none where it says none, or there is no record - a leg
    /// never built, whose workers configure as it would, and fetch.
    /// </summary>
    /// <param name="record">What the leg's own build directory was configured with, or <see langword="null"/>.</param>
    /// <param name="fileSystem">Tells which sources are there.</param>
    public static IReadOnlyDictionary<string, string> CacheVarsFor(BuildDirectoryRecord? record, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        if (record is null || record.FetchContentSources.Count == 0)
        {
            return variables;
        }

        foreach (var (name, named) in record.FetchContentSources)
        {
            var source = named.Length > 0 ? named
                : record.FetchContentBaseDirectory is { Length: > 0 } fetchedInto ? Path.Combine(fetchedInto, name.ToLowerInvariant() + "-src")
                : null;

            if (source is not null && fileSystem.DirectoryExists(source))
            {
                // Spelt with forward separators, as CMake spells every path it is given.
                variables[BuildDirectoryGuard.FetchContentSource + name] = source.Replace('\\', '/');
            }
        }

        if (variables.Count == record.FetchContentSources.Count)
        {
            variables[FullyDisconnected] = "ON";
        }

        return variables;
    }
}
