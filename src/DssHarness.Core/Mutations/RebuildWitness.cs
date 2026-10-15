using RepoHarness.Core.Build;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// Which objects a mutation must rebuild, read from the build's own records: what a registry once had to name by hand,
/// one translation unit at a time, and can no longer leave one out of.
/// </summary>
public static class RebuildWitness
{
    /// <summary>
    /// The objects in <paramref name="closure"/> that depend on any of <paramref name="sites"/> - each the first output of
    /// a build line that links nothing and whose build recorded its dependencies, as a compile does - as ninja
    /// canonicalizes it, in the order the closure reaches them. A site counts however it reaches the object: as its
    /// source, through a header it includes, through a precompiled header holding it, or through a header a command
    /// generates from it.
    /// </summary>
    /// <param name="graph">The build directory's rebuild graph, from its manifest and its dependency records.</param>
    /// <param name="closure">The build lines an arm's target builds, as <see cref="NinjaManifest.Closure"/> gives them.</param>
    /// <param name="sites">The files the arm mutates, absolute.</param>
    /// <remarks>
    /// <para>
    /// Objects, and never what links them: CMake has ninja restat a shared library's import library, so a program linked
    /// against one is not linked again when only the library's code changed - it loads the new library as it is - and a
    /// witness asking for every link along the way would call a sound build unwitnessed.
    /// </para>
    /// <para>
    /// A link is told by its rule, as <see cref="CMakeNinjaRule.Links"/> reads it, and never by what it recorded: where
    /// the linker can write what it read, as GNU ld does on Linux, CMake has it do so, and the program records its
    /// dependencies exactly as an object does. Taken for an object, a link that failed would be the mutation not
    /// compiling, where it compiled and what it left could not be linked.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> DependentObjects(NinjaRebuildGraph graph, IEnumerable<NinjaEdge> closure, IReadOnlyCollection<string> sites)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(closure);
        ArgumentNullException.ThrowIfNull(sites);

        return
        [
            .. closure
                .Where(edge => edge.Deps is not null && !CMakeNinjaRule.Links(edge) && edge.Outputs.Count > 0 && sites.Any(site => graph.DependsOn(edge, site)))
                .Select(edge => NinjaManifest.Normalize(edge.Outputs[0]))
                .Distinct(StringComparer.Ordinal),
        ];
    }
}
