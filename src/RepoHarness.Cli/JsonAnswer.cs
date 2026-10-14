using System.CommandLine;
using RepoHarness.Core.Execution;

namespace RepoHarness.Cli;

/// <summary>
/// The document a command asked for <c>--json</c> answers with where it stops before its body has an outcome of its
/// own - a <c>--directory</c> that does not exist, a repository not initialised, a usage error, a failure its body threw:
/// its own document's shape, empty, with the code it exits with and the line it ends on.
/// </summary>
/// <param name="parseResult">The parsed command line, where what it asked for decides which of its documents it is.</param>
/// <param name="exitCode">The code the command exits with.</param>
/// <param name="summary">The line it ends on.</param>
/// <param name="shown">How a path or a line of the harness's own is told to whoever reads the document.</param>
/// <param name="run">The run the command began, where it began one.</param>
internal delegate string StoppedDocument(ParseResult parseResult, int exitCode, string summary, Func<string, string> shown, RunId? run);

/// <summary>
/// A command's <c>--json</c>, with the document it answers with where it stops before its body has an outcome: each
/// command registers its own with <see cref="CommandRunner"/>, so a script reading its standard output reads one shape of
/// document whatever happened, and never another command's.
/// </summary>
/// <param name="Option">The command's <c>--json</c>.</param>
/// <param name="Stopped">The document it answers with where it stops: its own document's shape, empty.</param>
internal sealed record JsonAnswer(Option<bool> Option, StoppedDocument Stopped)
{
    /// <summary>
    /// A command that runs legs: its ledger, with no legs, on every exit - a leg nobody declared, a configuration that
    /// does not load, a runner nobody declared - naming the run where it began one.
    /// </summary>
    /// <param name="option">The command's <c>--json</c>.</param>
    internal static JsonAnswer Ledger(Option<bool> option)
        => new(option, (_, exitCode, summary, shown, run) => LedgerReport.Stopped(exitCode, summary, shown, run));
}
