namespace DssHarness.Core.Application;

/// <summary>
/// Whether a command answers with a document on standard output.
/// </summary>
/// <remarks>
/// A command asked for <c>--json</c> answers with one document on every exit, and standard output holds only that
/// document from the first line: the scope that keeps it whole is opened where this is not <see cref="None"/>. The
/// stopped form - what the document says where the command stops before its body has an answer - is the command's
/// own, whose members no kind could list: a leg ledger, the agent verbs' document, a listing, a report each stop in a
/// shape of their own, which the front end that begins the session supplies (<see cref="SessionFrontEnd.Stopped"/>).
/// </remarks>
public enum DocumentKind
{
    /// <summary>The command answers with no document.</summary>
    None,

    /// <summary>The command answers with its own document, its stopped form supplied by the front end that began the session.</summary>
    Own,
}
