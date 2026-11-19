using System.CommandLine;
using DssHarness.Core.Output;

namespace DssHarness.Core.Execution;

/// <summary>
/// How a command typed at a terminal is run against its interrupts: no clock of the command line's own ever ends it.
/// A command that counts its own interrupts (<see cref="PointOfNoReturn.CountsItsOwnInterrupts"/>) takes them itself;
/// every other's first interrupt cancels it, said, and it ends once its work has stopped, for as long as that takes -
/// a leg's ledger written, a child that is slow to end waited for and told - and the second is the hard stop the system
/// gives every process, which ends it where it stands (<see cref="TerminalSignals.StopAtTheFirst"/>). The one choice of
/// it, which the command line makes by.
/// </summary>
/// <remarks>
/// The command line's own handling of termination is off for every command: it cancels at the first interrupt and ends
/// the process once its clock - two seconds, by its own default - runs out, so a command still writing what it reached
/// was cut off part way with nothing said.
/// </remarks>
public static class CommandInterrupts
{
    /// <summary>What a command that does not count its own interrupts says at the first.</summary>
    public const string StoppingLine = "interrupted: it ends once what is under way has stopped, for as long as that takes; a second interrupt ends it where it stands";

    /// <summary>
    /// Runs the command <paramref name="parseResult"/> parsed, as <paramref name="commandLine"/> types it: taking the
    /// terminal's interrupts for it where <paramref name="atTheTerminal"/> and it does not count its own, and none of the
    /// command line's own.
    /// </summary>
    /// <param name="parseResult">The parsed command line, with no error.</param>
    /// <param name="commandLine">The command's name, then its arguments as typed.</param>
    /// <param name="atTheTerminal">
    /// Whether the command is the process's own, typed at its terminal; false for one a host agent serves for another
    /// machine, whose interrupts are the agent's.
    /// </param>
    /// <param name="error">
    /// Where the first interrupt is said - for a command a person types: a hidden one, which a program of this tool's
    /// starts, says nothing, its streams being its starter's.
    /// </param>
    /// <param name="cancellationToken">Stops the command as an interrupt does.</param>
    /// <returns>The command's exit code.</returns>
    public static async Task<int> InvokeAsync(
        ParseResult parseResult,
        IReadOnlyList<string> commandLine,
        bool atTheTerminal,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(error);

        var invocation = new InvocationConfiguration { ProcessTerminationTimeout = null };

        if (!atTheTerminal || PointOfNoReturn.CountsItsOwnInterrupts(commandLine))
        {
            return await parseResult.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        }

        var command = commandLine.Count > 0 ? commandLine[0] : Hosts.ToolPackage.Command;
        var typed = !parseResult.CommandResult.Command.Hidden;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var interrupts = TerminalSignals.StopAtTheFirst(
            stop,
            () =>
            {
                if (typed)
                {
                    Say(error, command);
                }
            });

        return await parseResult.InvokeAsync(invocation, stop.Token).ConfigureAwait(false);
    }

    /// <summary>Says the first interrupt as every warning of the command's is said, where the terminal still takes it.</summary>
    private static void Say(TextWriter error, string command)
    {
        try
        {
            new TextRunEvents(TextWriter.Null, error, verbose: false).Emit(new TextLine(LineKind.Warn, command, StoppingLine));
        }
        catch (IOException)
        {
            // The terminal is gone: nobody reads the line.
        }
    }
}
