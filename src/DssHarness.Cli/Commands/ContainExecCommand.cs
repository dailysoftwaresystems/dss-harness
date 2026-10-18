using System.CommandLine;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Cli.Commands;

/// <summary>
/// Wires the hidden <c>DssHarness contain-exec</c>: the step in starting a process that puts it
/// where it is contained first. Hidden because nobody types it - the process runner starts it as
/// the one way it has of placing a contained child, and no host is ever asked to run it.
/// </summary>
internal static class ContainExecCommand
{
    /// <summary>Builds the command.</summary>
    internal static Command Create()
    {
        var command = new Command(
            ContainExec.CommandName,
            "Join the container asked for, then become, or start and wait for, the program after '--'.")
        {
            Hidden = true,
        };

        var job = new Option<string>(ContainExec.JobOption)
        {
            Description = "The Windows job object to join, named by the process runner that made it.",
            HelpName = "name",
        };
        var group = new Option<bool>(ContainExec.GroupOption)
        {
            Description = "Start the program in a process group of its own, apart from the terminal.",
        };

        command.Options.Add(job);
        command.Options.Add(group);

        // The program and its arguments are everything after '--', handed on as they are given:
        // this command owns no word of theirs.
        command.TreatUnmatchedTokensAsErrors = false;

        command.SetAction(parseResult =>
        {
            var tokens = parseResult.UnmatchedTokens;

            if (tokens.Count == 0)
            {
                Console.Error.WriteLine("contain-exec was given no program after '--'.");
                return Task.FromResult(HarnessExit.UsageError);
            }

            // Said in the error stream the step's own failures are said in, and nowhere else: the
            // streams it was given belong to the program it starts.
            return Task.FromResult(ContainExec.Run(
                parseResult.GetValue(job),
                parseResult.GetValue(group),
                tokens[0],
                tokens.Skip(1).ToArray(),
                Console.Error));
        });

        return command;
    }
}
