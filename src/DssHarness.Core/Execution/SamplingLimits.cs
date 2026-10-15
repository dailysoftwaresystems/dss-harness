namespace RepoHarness.Core.Execution;

/// <summary>
/// What sampling the process table cannot see, each limit one member: a phrase that finishes "sampling cannot see",
/// which <see cref="Said"/> puts before it, so no caller spells either.
/// </summary>
/// <remarks>
/// Each was a clause of its own, written where the report was made, and the line that prints them put "sampling cannot
/// see" before every one: "sampling cannot see no command line could be read on this machine". The phrases are written
/// for that frame, and only here.
/// </remarks>
public static class SamplingLimits
{
    /// <summary>A tool a relative path started from inside the build directory: always.</summary>
    public const string RelativeTool =
        "a tool started from inside the build directory by a relative path, since no process's working directory is read";

    /// <summary>The command line of a process that hides it: always.</summary>
    public const string HiddenCommandLine =
        "the command line of a process that does not expose it, so such a process is counted and never matched against the build directory";

    /// <summary>Any command line at all, where no process sampled had one that could be read.</summary>
    public const string NoCommandLine = "any command line on this machine, so nothing was matched against the build directory";

    /// <summary>When some processes started, where some had no start time that could be read.</summary>
    /// <remarks>
    /// Without a start time an id is all there is to go on, so an id freed and handed to something else inside one leg
    /// is reported as one process that ran throughout.
    /// </remarks>
    public const string NoStartTimes =
        "when some processes on this machine started, so a process id reused during the leg is reported as one process";

    /// <summary>The two limits every report states, a clean one too.</summary>
    /// <remarks>
    /// A reader who does not know what was not looked at reads "no contender" as "nothing could have contended".
    /// </remarks>
    public static IReadOnlyList<string> Always { get; } = [RelativeTool, HiddenCommandLine];

    /// <summary>The line a report's <paramref name="limit"/> is said in.</summary>
    /// <param name="limit">One of this type's limits.</param>
    public static string Said(string limit) => $"sampling cannot see {limit}";
}
