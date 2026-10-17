using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Application;

/// <summary>An operation began. Nothing is said to the terminal; a front end learns that its work has started.</summary>
/// <param name="Command">The command the operation runs as.</param>
public sealed record CommandBegan(string Command) : RunEvent;

/// <summary>
/// The run an operation began, named in its first line before anything could refuse it: the line
/// <see cref="RunLine.TryRead"/> reads back, spelled by the one owner of that spelling.
/// </summary>
/// <param name="Command">The command the operation runs as.</param>
/// <param name="Run">The run it began.</param>
public sealed record RunBegan(string Command, RunId Run) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms() => [new(LineKind.Info, Command, RunLine.Message(Run))];
}

/// <summary>
/// What a command's body answered, rendered as it has always been: its document, its lines, and the line it ends on -
/// the stopped form of its document where the body answered without one, so a reader of the document reads one shape
/// whatever happened.
/// </summary>
/// <param name="Command">The command the operation runs as.</param>
/// <param name="Outcome">What its body answered.</param>
/// <param name="Stopped">The stopped form of its document, where it answers with one and its body answered without one.</param>
public sealed record CommandReported(string Command, CommandOutcome Outcome, string? Stopped = null) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms()
    {
        if (Stopped is { } document && Outcome.Data.Count == 0)
        {
            yield return new(LineKind.Data, null, document);
        }

        foreach (var text in Outcome.Data)
        {
            yield return new(LineKind.Data, null, text);
        }

        foreach (var detail in Outcome.Details ?? [])
        {
            yield return new(LineKind.Info, Command, detail);
        }

        if (!Outcome.Succeeded)
        {
            yield return new(LineKind.Fail, Command, Outcome.Message);
        }
        else if (!Outcome.Quiet)
        {
            yield return new(LineKind.Ok, Command, Outcome.Message);
        }
    }
}

/// <summary>
/// A command ended before its body answered - an exception ended it, or the directory it was asked to act on is not
/// one it can: the stopped form of its document where it answers with one, the failure line
/// <see cref="FailureLine.TryRead"/> reads back, and a defect's trace only under verbose.
/// </summary>
/// <param name="Command">The command the operation runs as.</param>
/// <param name="ExitCode">The code it exits with.</param>
/// <param name="Message">The line it ends on.</param>
/// <param name="Defect">Whether this is a defect in this tool, rather than a failure of the thing asked about.</param>
/// <param name="Trace">The defect's trace, as its reader is told it; <see langword="null"/> where there is none.</param>
/// <param name="StoppedDocument">The stopped form of its document, where it answers with one.</param>
public sealed record CommandFailed(string Command, int ExitCode, string Message, bool Defect, string? Trace = null, string? StoppedDocument = null) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms()
    {
        if (StoppedDocument is { } document)
        {
            yield return new(LineKind.Data, null, document);
        }

        yield return new(LineKind.Fail, Command, Message);

        if (Defect && Trace is { } trace)
        {
            yield return new(LineKind.RawError, null, trace, VerboseOnly: true);
        }
    }
}

/// <summary>An operation ended, however it ended. Nothing is said to the terminal.</summary>
/// <param name="Command">The command the operation runs as.</param>
public sealed record CommandEnded(string Command) : RunEvent;
