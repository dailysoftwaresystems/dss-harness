using System.CommandLine;
using DssHarness.Core.Application.Dispatch;
using DssHarness.Core.Execution;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness queue</c>.</summary>
internal static class QueueCommand
{
    internal const string Name = QueueReports.CommandName;

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Answer the queue as one JSON document.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Show this machine's dispatch queue: each class of unit - heavy legs, machine steps, reasoning agents - its capacity, "
            + "who holds and who waits, by tenant, with each waiting unit's cost and the share it waits for. The queue is fair by "
            + "tenant: a tenant's turn comes when the charge against its share is the least, its share its weight times its run's "
            + "priority, doubled for each period a unit of its waits past the aging bound. Nothing is pre-empted, and nothing is "
            + "changed by looking. A machine shared by several users is not seen whole: each user's queue is their own.");
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => Task.FromResult(QueueReports.Render(context.Get<IShowQueue>().Show(), context.Json)),
            new JsonAnswer(JsonOption, (_, exitCode, summary, _, _) => QueueReports.Stopped(exitCode, summary)));

        return command;
    }
}
