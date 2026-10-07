using System.Text.RegularExpressions;
using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Build;

/// <summary>
/// What ninja says as a build ends, where that decides what the harness reports. Whenever ninja itself ends a build that
/// did not succeed, it says why on a line of its own, under its own name - a step that failed, an error of its own, or
/// that it was interrupted - and so does samurai, which CMake runs for a ninja generator where that is what is
/// installed. So a build under either that exited without one was stopped from outside before it finished - killed, as a
/// tool ending its process tree does - which proves nothing about the code.
/// </summary>
internal static class Ninja
{
    /// <summary>What ninja begins the line naming each step that failed with.</summary>
    private const string FailedStep = "FAILED: ";

    /// <summary>The name ninja says everything of its own under, whatever its file is called.</summary>
    private const string OwnName = "ninja";

    /// <summary>
    /// What the program says after its name where it was interrupted, rather than ending the build for a failure:
    /// ninja's line, and samurai's, which names the signal.
    /// </summary>
    private static readonly string[] Interruptions = ["build stopped: interrupted by user", "received signal: "];

    /// <summary>
    /// What the program says after its name that is no reason a build ended: a warning, the directory it works in, a
    /// build with nothing to do, and samurai's lines explaining why a step runs.
    /// </summary>
    private static readonly string[] Asides = ["warning: ", "Entering directory", "Leaving directory", "no work to do", "nothing to do", "explain "];

    /// <summary>A terminal's colour codes, which ninja writes around what it says where something makes it colour its output.</summary>
    private static readonly Regex Colour = new(@"\x1B\[[0-9;]*m", RegexOptions.CultureInvariant);

    /// <summary>Whether a CMake generator, as configuration or CMake's cache names it, is one of ninja's: Ninja, or Ninja Multi-Config.</summary>
    /// <param name="generator">The generator, or <see langword="null"/> where nothing names one.</param>
    public static bool Generates(string? generator) => generator?.StartsWith("Ninja", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The verdict of <paramref name="phase"/>, a build ninja ran that did not pass, where ninja did not end it for a
    /// failure: <see cref="LegVerdict.Stopped"/>, naming the exit code and whether ninja said it was interrupted or said
    /// nothing; <see langword="null"/> where ninja said anything of its own that is a reason a build ends - a step that
    /// failed, an error, a failure - or the phase exited 0 or stalled: its own verdict stands then.
    /// </summary>
    /// <param name="phase">The build phase.</param>
    /// <param name="program">
    /// The program CMake ran for ninja, as its cache records it, or <see langword="null"/> where it records none. samurai
    /// says what it says under the name it was started by, which is that file's.
    /// </param>
    /// <remarks>
    /// Measured on Windows: ninja killed part way - <c>taskkill /F</c>, as a tool ending a process tree does - exits 1, as
    /// a build ninja stopped for a failed step does, and says nothing more, so only what ninja said can tell the two
    /// apart. Read as anything it says under its name that is not known to be no reason, rather than as the lines ninja
    /// prints for a failure: a build whose tool said why in words this does not know is failed, as every build was before
    /// a stopped one was told apart, and never reported as one running again would finish.
    /// </remarks>
    public static ReachedVerdict? Stopped(PhaseResult phase, string? program)
    {
        ArgumentNullException.ThrowIfNull(phase);

        if (phase.ExitCode == 0 || phase.Stalled)
        {
            return null;
        }

        var file = Path.GetFileName(program);
        string[] names = string.IsNullOrEmpty(file) ? [OwnName] : [OwnName, file];
        var interrupted = false;

        foreach (var line in Colour.Replace(phase.Output, string.Empty).ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith(FailedStep, StringComparison.Ordinal))
            {
                return null;
            }

            if (Said(line, names) is not { } said || Asides.Any(aside => said.StartsWith(aside, StringComparison.Ordinal)))
            {
                continue;
            }

            if (!Interruptions.Any(interruption => said.StartsWith(interruption, StringComparison.Ordinal)))
            {
                return null;
            }

            interrupted = true;
        }

        var tool = Path.GetFileNameWithoutExtension(program) is { Length: > 0 } named ? named : OwnName;

        return ReachedVerdict.Of(
            LegVerdict.Stopped,
            interrupted
                ? $"{phase.Phase} exited {phase.ExitCode}: {tool} says it was interrupted before it finished"
                : $"{phase.Phase} exited {phase.ExitCode} without {tool} saying why, as it says whenever it ends a build itself: "
                  + "something stopped it from outside before it finished");
    }

    /// <summary>
    /// What <paramref name="line"/> says after one of <paramref name="names"/> and a colon, where the program said it of its
    /// own; <see langword="null"/> where it is no line of the program's own.
    /// </summary>
    private static string? Said(string line, IReadOnlyList<string> names)
        => names
            .Where(name => line.StartsWith(name, StringComparison.Ordinal) && line.AsSpan(name.Length).StartsWith(": ", StringComparison.Ordinal))
            .Select(name => line[(name.Length + 2)..])
            .FirstOrDefault();
}
