using System.CommandLine;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Cli.Commands;

/// <summary>
/// Wires the hidden <c>DssHarness isolate-exec</c>: the step that starts a program inside MXC's process container on
/// Windows, which the contained step starts in an isolated child's place. Hidden because nobody types it.
/// </summary>
internal static class IsolateExecCommand
{
    /// <summary>Builds the command.</summary>
    internal static Command Create()
    {
        var command = new Command(
            MxcProcessContainer.CommandName,
            "Start the program after '--' inside a process container of MXC's, the places named denied, sealed or written, and wait for it.")
        {
            Hidden = true,
        };

        var hidden = Places(MxcProcessContainer.HiddenOption, "A place neither read nor written.");
        var sealedPlaces = Places(MxcProcessContainer.SealedOption, "A place written by nobody.");
        var writable = Places(MxcProcessContainer.WritableOption, "A writable root.");
        var variables = new Option<string[]>(MxcProcessContainer.VariableOption)
        {
            Description = "A variable set in the program's environment, as NAME=VALUE.",
            HelpName = "variable",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false,
        };

        command.Options.Add(hidden);
        command.Options.Add(sealedPlaces);
        command.Options.Add(writable);
        command.Options.Add(variables);

        // The program and its arguments are everything after '--', handed on as they are given.
        command.TreatUnmatchedTokensAsErrors = false;

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var tokens = parseResult.UnmatchedTokens;

            if (tokens.Count == 0)
            {
                Console.Error.WriteLine($"{MxcProcessContainer.CommandName} was given no program after '--'.");
                return HarnessExit.UsageError;
            }

            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine($"{MxcProcessContainer.CommandName} starts a program inside MXC's process container, which is Windows' alone.");
                return ContainExec.Failed;
            }

            var set = new List<KeyValuePair<string, string>>();

            foreach (var variable in parseResult.GetValue(variables) ?? [])
            {
                if (variable.IndexOf('=', StringComparison.Ordinal) is not (var cut and > 0))
                {
                    Console.Error.WriteLine($"{MxcProcessContainer.CommandName} was given the variable '{variable}', which is no NAME=VALUE.");
                    return HarnessExit.UsageError;
                }

                set.Add(new(variable[..cut], variable[(cut + 1)..]));
            }

            var places = new ContainedPlaces(
                parseResult.GetValue(hidden) ?? [],
                parseResult.GetValue(sealedPlaces) ?? [],
                parseResult.GetValue(writable) ?? []);

            // Its own failures are said on the error stream alone: the streams it was given belong to the program.
            return await IsolateExec.RunAsync(places, tokens[0], [.. tokens.Skip(1)], set, Console.Error, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>An option naming places, given once for each.</summary>
    private static Option<string[]> Places(string name, string description)
        => new(name) { Description = description, HelpName = "path", Arity = ArgumentArity.ZeroOrMore, AllowMultipleArgumentsPerToken = false };
}
