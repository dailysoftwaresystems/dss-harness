using System.CommandLine;
using DssHarness.Core.Execution;

namespace DssHarness.Cli;

/// <summary>
/// The document a command asked for <c>--json</c> answers with where it has none of its own to answer with - its command
/// line refused by the parser, a <c>--directory</c> that does not exist, a repository not initialised, a failure its body
/// threw, or a usage error or a refusal its body returned without a document: its own document's shape, empty, carrying
/// the code it exits with and the line it ends on where that shape has members to carry them - an array has none, and
/// the process's exit code and the line it ends on say them then.
/// </summary>
/// <param name="flags">
/// The command line's flags, where what it asked for decides which of its documents it is or what it says: read as the
/// parser left them, refused or not, by the one reader that never throws (<see cref="CommandFlags"/>).
/// </param>
/// <param name="exitCode">The code the command exits with.</param>
/// <param name="summary">The line it ends on.</param>
/// <param name="shown">How a path or a line of the harness's own is told to whoever reads the document.</param>
/// <param name="run">The run the command began, where it began one.</param>
internal delegate string StoppedDocument(CommandFlags flags, int exitCode, string summary, Func<string, string> shown, RunId? run);

/// <summary>
/// A command's <c>--json</c>, with the document it answers with where it has none of its own (<see cref="StoppedDocument"/>):
/// each command registers its own with <see cref="CommandRunner"/> where it is wired, so a script reading its standard
/// output reads one shape of document whatever happened, and never another command's.
/// </summary>
/// <param name="Option">The command's <c>--json</c>.</param>
/// <param name="Stopped">The document it answers with where it has none of its own: its own document's shape, empty.</param>
internal sealed record JsonAnswer(Option<bool> Option, StoppedDocument Stopped)
{
    /// <summary>
    /// A command that runs legs: its ledger, with no legs, on every exit - a leg nobody declared, a configuration that
    /// does not load, a runner nobody declared - naming the run where it began one.
    /// </summary>
    /// <param name="option">The command's <c>--json</c>.</param>
    internal static JsonAnswer Ledger(Option<bool> option)
        => new(option, (_, exitCode, summary, shown, run) => LedgerReport.Stopped(exitCode, summary, shown, run));

    /// <summary>
    /// Whether <paramref name="flags"/> ask for the document: where the parser itself read <c>--json</c> as this command's
    /// own and set - wherever it sits among the command's arguments and options, and never where it is another option's
    /// value, follows <c>--</c>, comes before the command's name, is given false or is given twice with a value each, its
    /// value then refused (<see cref="CommandFlags.IsSet"/>). The one reading of it, the same whether the command line
    /// parsed or the parser refused it: a command's body reads it as read here (<see cref="CommandContext.Json"/>).
    /// </summary>
    /// <param name="flags">The command line's flags, parsed or refused.</param>
    internal bool Asked(CommandFlags flags)
    {
        ArgumentNullException.ThrowIfNull(flags);

        return flags.IsSet(Option);
    }
}
