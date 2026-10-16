using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Hosts;

/// <summary>
/// Where a process started with an environment looks for a program named by its name: the PATH that
/// environment sets, or this process's own where it sets none, with the directories a survey found
/// programs in around it.
/// </summary>
/// <param name="Path">
/// The PATH the process is given: the one its environment sets, and this process's where it sets none.
/// </param>
/// <param name="ProgramDirectories">The directories put around it: the raised ones ahead, and the ones a survey found programs in off it after.</param>
/// <remarks>
/// One answer for everything that asks which file a name starts - the guard on a configured build
/// directory, a leg checking what its developer environment put on its PATH, and the look that
/// decides whether a tool is missing - so none of them can look somewhere a leg would not.
/// </remarks>
public sealed record PathSearch(string? Path, ProgramDirectories ProgramDirectories)
{
    /// <summary>Where a process started with <paramref name="environment"/> looks.</summary>
    /// <param name="environment">The environment it starts with, over this process's own.</param>
    /// <param name="programDirectories">What its PATH is given around its own.</param>
    public static PathSearch For(IReadOnlyDictionary<string, string?> environment, ProgramDirectories programDirectories)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(programDirectories);

        var declared = environment.FirstOrDefault(pair => string.Equals(pair.Key, "PATH", StringComparison.OrdinalIgnoreCase));

        return new PathSearch(
            declared.Key is null ? Environment.GetEnvironmentVariable("PATH") : declared.Value,
            programDirectories);
    }

    /// <summary>Where <paramref name="program"/> is, looked for as such a process starts it.</summary>
    /// <param name="platform">This machine.</param>
    /// <param name="filePermissions">Says whether a candidate file would start.</param>
    /// <param name="program">The name, or path, to find.</param>
    /// <remarks>
    /// The raised directories are part of the PATH it is looked for on, as they are of the one such a process is
    /// given; the appended ones are looked in after it, as the directories searched beyond a PATH are.
    /// </remarks>
    public ProgramLocation Find(IHostPlatform platform, IFilePermissions filePermissions, string program)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(filePermissions);

        var path = ProgramDirectories.Ahead(Path, System.IO.Path.PathSeparator);

        return new LocalProgramResolver(platform, filePermissions, () => path).Find(program, ProgramDirectories.Appended);
    }
}
