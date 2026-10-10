namespace RepoHarness.Core.Processes;

/// <summary>Outcome of a child process.</summary>
/// <param name="ExitCode">Process exit code. Meaningless when <paramref name="TimedOut"/> is set.</param>
/// <param name="StandardOutput">
/// Captured stdout: all of it, or its end where the request kept it as a <see cref="StreamKept.Tail"/>.
/// </param>
/// <param name="StandardError">
/// Captured stderr: all of it, or its end where the request kept it as a <see cref="StreamKept.Tail"/>.
/// </param>
/// <param name="Duration">Wall clock time the process ran for.</param>
/// <param name="TimedOut">Whether the process was killed for exceeding its budget.</param>
public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut)
{
    /// <summary>
    /// Why the beat written on the child's held-open input stopped while the child was still running, in the system's
    /// words, or <see langword="null"/> where it did not: the child stopped reading its input and went on. The input
    /// was closed then, so a child that takes the end of its input for this process having gone is told at once, by the
    /// sign that is no guess, rather than by a silence it has to wait out.
    /// </summary>
    public string? BeatLost { get; init; }

    /// <summary>True only when the process ran to completion and reported success.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>stdout with surrounding whitespace removed, for single line probes.</summary>
    public string TrimmedOutput => StandardOutput.Trim();
}
