using System.CommandLine;
using System.Runtime.InteropServices;
using DssHarness.Core.Application;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Cli.Commands;

/// <summary>
/// Wires the hidden <c>DssHarness host-hold</c>, which holds this machine awake between commands for the hold a
/// host agent was asked for. Hidden because nobody types it: the host agent starts it, detached, and it goes on
/// once the connection that asked has ended.
/// </summary>
internal static class HostHoldCommand
{
    private static readonly Argument<string> GenerationArgument = new("generation")
    {
        Description = "The hold this process holds the machine awake for.",
    };

    /// <summary>Builds the command.</summary>
    internal static Command Create()
    {
        var command = new Command(
            HoldAwakeService.CommandName,
            "Hold this machine awake for the hold named, until a command's own keepAwake ends it or its time is up.")
        {
            Hidden = true,
        };

        command.Arguments.Add(GenerationArgument);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // Nobody reads what it would say: the process that started it has gone, and its streams with it.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);

            // Nor does anybody hang up on it: a command run over ssh has no terminal, and a connection that
            // ends is no reason to stop.
            using var hangUp = OperatingSystem.IsWindows()
                ? null
                : PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);

            var session = ConsoleHost.Host.Begin(new SessionOptions
            {
                Command = HoldAwakeService.CommandName,
                Prompting = false,
            });

            await using (session.ConfigureAwait(false))
            {
                await session.RunAsync(
                    async (scope, token) =>
                    {
                        var holds = scope.Services.GetRequiredService<HoldAwakeService>();

                        try
                        {
                            await holds.RunAsync(parseResult.GetValue(GenerationArgument)!, token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
                        {
                            // A hold that could not be kept ends: the machine sleeps as it would have without one.
                        }

                        return new CommandOutcome(HarnessExit.Success, "held") with { Quiet = true };
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            return HarnessExit.Success;
        });

        return command;
    }
}
