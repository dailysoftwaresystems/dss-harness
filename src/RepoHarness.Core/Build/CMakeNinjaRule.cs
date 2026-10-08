namespace RepoHarness.Core.Build;

/// <summary>
/// What CMake's ninja generator says of a build line through the name it gives the rule the line runs: the language,
/// then what the rule does, then two underscores and the target it serves - <c>CXX_COMPILER__app_unscanned_Debug</c>,
/// <c>CXX_EXECUTABLE_LINKER__app_Debug</c>, <c>CXX_SHARED_LIBRARY_LINKER__core_Debug</c>.
/// </summary>
/// <remarks>
/// Read before the target's name and never within it: a project names a target as it likes, as a rule that links is
/// named too.
/// </remarks>
internal static class CMakeNinjaRule
{
    /// <summary>What stands between what a rule does and the target it serves.</summary>
    private const string BeforeTheTarget = "__";

    /// <summary>What ends the name of what every rule that links does, whatever it links.</summary>
    private const string Linker = "_LINKER";

    /// <summary>What ends the name of what a rule that links a program does.</summary>
    private const string ProgramLinker = "_EXECUTABLE_LINKER";

    /// <summary>
    /// Whether <paramref name="edge"/> links what objects were built for - a program, or a library of any kind - and so
    /// builds no object itself.
    /// </summary>
    public static bool Links(NinjaEdge edge) => Does(edge).EndsWith(Linker, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="edge"/> links a program.</summary>
    public static bool LinksAProgram(NinjaEdge edge) => Does(edge).EndsWith(ProgramLinker, StringComparison.Ordinal);

    /// <summary>What <paramref name="edge"/>'s rule does, as its name says before the target's: the whole name where it serves none.</summary>
    private static string Does(NinjaEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);

        return edge.Rule.Split(BeforeTheTarget, 2)[0];
    }
}
