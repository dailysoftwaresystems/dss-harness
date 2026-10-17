namespace DssHarness.Core.Application;

/// <summary>
/// Whether a command answers with a document on standard output, and whose.
/// </summary>
/// <remarks>
/// A command asked for <c>--json</c> answers with one document on every exit, and standard output holds only that
/// document from the first line: the scope that keeps it whole is opened where this is not <see cref="None"/>. The
/// stopped form - what the document says where the command stops before its body has an answer - is the command's
/// own, whose members no kind could list: the front end that begins the session supplies it. A command of the
/// engine's whose stopped form is a kind's own - a run's ledger, a listing - says that kind here.
/// </remarks>
public enum DocumentKind
{
    /// <summary>The command answers with no document.</summary>
    None,

    /// <summary>The command answers with its own document, its stopped form supplied by the front end that began the session.</summary>
    Own,
}
