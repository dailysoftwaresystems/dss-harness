namespace DssHarness.Core.Processes;

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
    /// words, or <see langword="null"/> where it did not: the child stopped taking its input and went on. Nothing
    /// more was written to it, and whatever counts on the beat at the child's far end hears no more of this process.
    /// </summary>
    public string? BeatLost { get; init; }

    /// <summary>
    /// What a contained child's container could not reach when it was ended - at the child's own end or at a stop -
    /// and why, or <see langword="null"/> where it reached everything it holds: a session ended on Linux or macOS
    /// without the process table it is read from ends the child's group and nothing outside it.
    /// </summary>
    public string? Uncontained { get; init; }

    /// <summary>True only when the process ran to completion and reported success.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>stdout with surrounding whitespace removed, for single line probes.</summary>
    public string TrimmedOutput => StandardOutput.Trim();

    /// <summary>
    /// What a program that never ran is reported as, <paramref name="why"/> on its standard error: so a caller reads
    /// a start that failed as it reads a run that did.
    /// </summary>
    /// <param name="why">Why it never ran.</param>
    public static ProcessResult NotRun(string why) => new(-1, string.Empty, why, TimeSpan.Zero, TimedOut: false);
}
