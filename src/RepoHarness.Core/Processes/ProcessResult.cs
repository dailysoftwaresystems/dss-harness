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
    /// <summary>True only when the process ran to completion and reported success.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>stdout with surrounding whitespace removed, for single line probes.</summary>
    public string TrimmedOutput => StandardOutput.Trim();
}
