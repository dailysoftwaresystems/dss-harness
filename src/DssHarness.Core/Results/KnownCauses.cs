using DssHarness.Core.Configuration;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Results;

/// <summary>
/// The exceptions whose cause this build already has a name for, and what each means.
/// </summary>
/// <remarks>
/// One table for the two places that turn an exception into an outcome: the command runner, which
/// ends a whole command, and the leg executor, which records one leg and lets the others go on.
/// Each used to decide for itself, and they disagreed about the same exception: a program that would
/// not start was a missing tool to the one and a defect in the harness to the other. So cmake missing
/// from a host read as exit 14 when a command was typed there, and as a poisoned leg — exit 70, "the
/// harness broke" — when a run placed a leg there. A missing program on a host is that host's
/// problem, and a defect is this tool's; sending a reader after the wrong one costs the whole of
/// the time they spend looking.
/// <para>
/// Each reader says it in its own terms. A command ends with the exit code named here; a leg that
/// was already running when one of these stopped it has failed, for the cause named - see
/// <see cref="Names"/>.
/// </para>
/// </remarks>
public static class KnownCauses
{
    /// <summary>
    /// Whether <paramref name="exception"/> has a cause this build can name, rather than being a
    /// defect in it.
    /// </summary>
    /// <param name="exception">What was thrown.</param>
    public static bool Names(Exception exception) => ExitCodeFor(exception) is not null;

    /// <summary>
    /// The exit code <paramref name="exception"/> means, or <see langword="null"/> when its cause is
    /// not one this build can name.
    /// </summary>
    /// <param name="exception">What was thrown.</param>
    public static int? ExitCodeFor(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // Missing, or there and unable to start: either way the instrument never ran.
            ProgramStartException => HarnessExit.ToolMissing,
            _ => null,
        };
    }

    /// <summary>What an exception that ended a command means, in the terms its front ends answer in.</summary>
    /// <param name="ExitCode">The code the command exits with.</param>
    /// <param name="Message">The line it ends on.</param>
    /// <param name="Defect">Whether this is a defect in this tool, rather than a failure of the thing asked about.</param>
    public sealed record MeaningOf(int ExitCode, string Message, bool Defect);

    /// <summary>
    /// What <paramref name="exception"/> means when it ends a command: the code the command exits with, the line it
    /// ends on, and whether it is a defect in this tool.
    /// </summary>
    /// <param name="exception">What ended the command.</param>
    /// <remarks>
    /// The one place a failure becomes an exit code, so a command a host serves for another machine, and a command a
    /// front end of any other kind runs, fail exactly as a command typed here does. <see cref="ExitCodeFor"/> is not
    /// widened for this: <see cref="DssHarness.Core.Execution.LegVerdict"/>'s reading of an exception relies on its
    /// narrow meaning through <see cref="Names"/>.
    /// </remarks>
    public static MeaningOf Meaning(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // A cause both this and the leg executor know, read from the one table they share, so the
        // same missing program means the same thing whether it stopped a command or a leg.
        if (ExitCodeFor(exception) is { } known)
        {
            return new(known, exception.Message, Defect: false);
        }

        return exception switch
        {
            // The service already decided what this failure means.
            HarnessException harness => new(harness.ExitCode, harness.Message, Defect: false),
            ConfigException => new(HarnessExit.ConfigInvalid, exception.Message, Defect: false),

            // Not CommandFailed: nothing ran to completion, and a caller reading a failure code
            // would report a red verdict for an interrupted run.
            OperationCanceledException => new(HarnessExit.Cancelled, "Interrupted before completion.", Defect: false),

            // A defect in the harness, not a failure of the thing being asked about. Reported as a
            // message with a defined exit code rather than an unhandled exception, whose exit code
            // would collide with a command's own contract.
            _ => new(HarnessExit.InternalError, $"Unexpected {exception.GetType().Name}: {exception.Message}", Defect: true),
        };
    }
}
