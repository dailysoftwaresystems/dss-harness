using DssHarness.Core.Output;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Application;

/// <summary>
/// What one operation of this tool is asked and how it runs: a command typed here today, and later a step of an
/// orchestrator run, which names the run it belongs to.
/// </summary>
public sealed record SessionOptions
{
    /// <summary>The command's name, which prefixes every line it writes.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// The directory to act on as it was asked for; <see langword="null"/> where the current directory is meant - read
    /// per call through the one reader of it, so a front end that changes where "here" is between operations is obeyed.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>Whether detail is shown.</summary>
    public bool Verbose { get; init; }

    /// <summary>Whether a person is there to ask; false where nobody is, as a host answering another machine.</summary>
    public bool Prompting { get; init; } = true;

    /// <summary>Who the operation runs for.</summary>
    public CommandOrigin Origin { get; init; } = CommandOrigin.Typed;

    /// <summary>
    /// Whether this operation begins a run, as its own request says - a listing sweeps nothing and begins none. The run
    /// is named in the operation's first line and its id answers in every document the operation writes.
    /// </summary>
    public bool BeginsRun { get; init; }

    /// <summary>
    /// Whether the operation answers with a document, and whose; <see cref="DocumentKind.None"/> where it answers with
    /// none. <see cref="DocumentKind.Own"/> needs the front end's stopped form (<see cref="SessionFrontEnd.Stopped"/>), and
    /// a session begun without one is refused (<see cref="InvalidOperationException"/>).
    /// </summary>
    public DocumentKind Document { get; init; } = DocumentKind.None;

    /// <summary>The operation this one belongs to - a step of an orchestrator run, naming the run - where there is one.</summary>
    public OperationId? Parent { get; init; }

    /// <summary>
    /// Whether this operation's own end - what it says and leaves on the hosts it reached - is left to its
    /// <see cref="Parent"/> to await, as a step of a run leaves it to the run: the end runs however the outer
    /// operation ended, never cut short by this one finishing, on this operation's services, which are kept for it. A
    /// session begun with this and no <see cref="Parent"/> is refused (<see cref="InvalidOperationException"/>); one whose
    /// outer operation has already taken its ends runs its own at once.
    /// </summary>
    public bool DefersCommandEnd { get; init; }
}
