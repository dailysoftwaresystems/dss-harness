using System.CommandLine;
using DssHarness.Core.Application;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Cli;

/// <summary>
/// The output logs the commands that drive a session of a run take (<see cref="FlowOutputLogs.Commands"/>): parsed here,
/// and asked of the session as its request (<see cref="OutputLogsRequest"/>); everything else of them is the core's.
/// </summary>
internal static class OutputLogsOption
{
    /// <summary>Where the zip is written; absent, none is.</summary>
    internal static Option<string?> Option { get; } = new(FlowOutputLogs.Option)
    {
        HelpName = "file",
        Description = "Write one zip at <file> holding everything needed to understand the run and to put right what it met: "
            + "the run's whole directory, the configuration it was read with and manifest.json - what was run, how it ended "
            + "and why, its open questions and this command's own output - rewritten whole at every end, refused or not, and "
            + "named in the text and in --json. Read relative to the directory the shell stands in.",
    };

    /// <summary>Adds the option to <paramref name="command"/>.</summary>
    /// <param name="command">One of the commands that drive a session of a run.</param>
    internal static void AddTo(Command command) => command.Options.Add(Option);

    /// <summary>
    /// What <paramref name="parseResult"/> asks of the output logs: the zip's path and the command line as it was typed, the
    /// tool's name first; null where it asks for none.
    /// </summary>
    /// <param name="parseResult">The command line, parsed.</param>
    internal static OutputLogsRequest? Requested(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        return parseResult.GetValue(Option) is { } path
            ? new OutputLogsRequest(path, [ToolPackage.Command, .. parseResult.Tokens.Select(token => token.Value)])
            : null;
    }
}
