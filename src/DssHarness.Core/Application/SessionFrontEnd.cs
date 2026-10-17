using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Application;

/// <summary>What a front end knows of the operation it began, as its factories are handed it.</summary>
/// <param name="Options">What was asked and how it runs.</param>
/// <param name="Id">The operation's id.</param>
/// <param name="Run">The run it began, where it began one.</param>
/// <param name="Output">Where the operation writes.</param>
public sealed record SessionContext(SessionOptions Options, OperationId Id, RunId? Run, IHarnessOutput Output);

/// <summary>
/// What a front end supplies to the operations it begins: where they write, who they may ask, and what the document
/// of one that answers with a document says where it stops before its body has an answer.
/// </summary>
/// <param name="Output">
/// Where the operation writes, for a front end that supplies its own. Refused with
/// <see cref="NotSupportedException"/> when the session begins, until a front end other than the command line builds
/// an operation's services: an operation writes through the output its host's services build, which is what the command
/// line supplies, and an output given here would otherwise be ignored without a word. <see langword="null"/>, as the
/// command line gives it.
/// </param>
/// <param name="Prompt">
/// How a superuser password is asked of whoever is there, for a front end that supplies its own. Refused as
/// <paramref name="Output"/> is, and for the same reason; <see langword="null"/>, as the command line gives it.
/// </param>
/// <param name="Stopped">
/// The stopped form of the operation's document, given the code it exits with and the line it ends on;
/// <see langword="null"/> where it answers with no document. Its own document's shape, which only the front end that
/// knows the command can supply.
/// </param>
public sealed record SessionFrontEnd(
    Func<SessionContext, IHarnessOutput>? Output = null,
    Func<SessionContext, ISuperuserPrompt>? Prompt = null,
    Func<SessionContext, int, string, string>? Stopped = null);
