using System.CommandLine;
using DssHarness.Core.Application.Hosting;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Cli.Commands;

/// <summary>
/// Wires the hidden <c>DssHarness sync-serve</c>, which a machine runs on a host to act on that
/// host's copy of the repository.
/// </summary>
/// <remarks>
/// Hidden because nobody types it: it is the far side of a sync, and every operation it performs is
/// one the machine asking already decided on. <see cref="SyncServeHandler"/> serves them, through the
/// same <see cref="LocalSyncTransport"/> this machine uses for a copy of its own, so there is one
/// implementation of what a sync does to a tree and no second one to drift from it.
/// </remarks>
internal static class SyncServeCommand
{
    internal const string Name = SyncServe.CommandName;

    private static readonly Argument<string> OperationArgument = new("operation")
    {
        Description = "The operation to perform on this host's copy.",
    };

    private static readonly Argument<string[]> ArgumentsArgument = new("arguments")
    {
        Description = $"The operation's arguments, after '{SyncServe.OperandsFollow}': the copy's root, then whatever the operation takes, each value an argument of its own.",
        Arity = ArgumentArity.OneOrMore,
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Serve one sync operation on this host's copy of the repository. Not meant to be typed; the machine syncing runs it.")
        {
            Hidden = true,
        };

        command.Arguments.Add(OperationArgument);
        command.Arguments.Add(ArgumentsArgument);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, cancellationToken) =>
        {
            var operation = context.ParseResult.GetRequiredValue(OperationArgument);
            var arguments = context.ParseResult.GetRequiredValue(ArgumentsArgument);

            var served = await context.Get<SyncServeHandler>()
                .ServeAsync(operation, arguments, cancellationToken)
                .ConfigureAwait(false);

            return served.Line is { } line
                ? CommandOutcome.Ok(string.Empty) with { Data = [line, .. served.Following], Quiet = true }
                : CommandOutcome.Ok(string.Empty) with { Quiet = true };
        });

        return command;
    }
}
