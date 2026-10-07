using System.Text.RegularExpressions;
using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Build;

/// <summary>
/// What ninja says as a build ends, where that decides what the harness reports. Whenever ninja itself ends a build that
/// did not succeed, it says why on a line of its own - a step that failed, an error of its own, or that it was
/// interrupted - so a build under it that exited without one was stopped from outside before it finished: killed, or its
/// machine stopped, which proves nothing about the code.
/// </summary>
internal static class Ninja
{
    /// <summary>What ninja begins the line naming each step that failed with.</summary>
    private const string FailedStep = "FAILED: ";

    /// <summary>What ninja begins the line saying why it stopped a build with.</summary>
    private const string BuildStopped = "ninja: build stopped: ";

    /// <summary>Why ninja says it stopped where it was interrupted, rather than for a failure.</summary>
    private const string Interrupted = BuildStopped + "interrupted by user";

    /// <summary>What ninja begins its errors, and its fatal ones, with.</summary>
    private static readonly string[] Errors = ["ninja: error: ", "ninja: fatal: "];

    /// <summary>A terminal's colour codes, which ninja writes around what it says where something makes it colour its output.</summary>
    private static readonly Regex Colour = new(@"\x1B\[[0-9;]*m", RegexOptions.CultureInvariant);

    /// <summary>Whether a CMake generator, as configuration or CMake's cache names it, is one of ninja's: Ninja, or Ninja Multi-Config.</summary>
    /// <param name="generator">The generator, or <see langword="null"/> where nothing names one.</param>
    public static bool Generates(string? generator) => generator?.StartsWith("Ninja", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The verdict of <paramref name="phase"/>, a build ninja ran that did not pass, where ninja did not end it for a
    /// failure: <see cref="LegVerdict.Stopped"/>, naming the exit code and whether ninja said it was interrupted or said
    /// nothing; <see langword="null"/> where ninja said a step failed or an error of its own ended the build, or the phase
    /// exited 0 or stalled - its own verdict stands then.
    /// </summary>
    /// <param name="phase">The build phase.</param>
    /// <remarks>
    /// Measured on Windows: ninja killed part way - <c>taskkill /F</c>, as a tool ending a process tree does - exits 1, as
    /// a build ninja stopped for a failed step does, and says nothing more, so only what ninja said can tell the two
    /// apart.
    /// </remarks>
    public static ReachedVerdict? Stopped(PhaseResult phase)
    {
        ArgumentNullException.ThrowIfNull(phase);

        if (phase.ExitCode == 0 || phase.Stalled)
        {
            return null;
        }

        var lines = Colour.Replace(phase.Output, string.Empty).ReplaceLineEndings("\n").Split('\n');

        bool Says(string start) => lines.Any(line => line.StartsWith(start, StringComparison.Ordinal));

        if (Says(FailedStep) || Errors.Any(Says) || lines.Any(line => line.StartsWith(BuildStopped, StringComparison.Ordinal) && !line.StartsWith(Interrupted, StringComparison.Ordinal)))
        {
            return null;
        }

        return ReachedVerdict.Of(
            LegVerdict.Stopped,
            Says(Interrupted)
                ? $"{phase.Phase} exited {phase.ExitCode}: ninja says it was interrupted before it finished"
                : $"{phase.Phase} exited {phase.ExitCode} without ninja saying why, as it says whenever it ends a build itself: "
                  + "something stopped it from outside before it finished");
    }
}
