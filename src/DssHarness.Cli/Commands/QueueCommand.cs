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
            + "who holds and who waits, by tenant (a local command's repository), the waiting units in the order the queue "
            + "serves them, each with its cost, the share its tenant is credited each round and its tenant's deficit. Heavy "
            + "legs are granted their slots by deficit round-robin over their tenants: each round a waiting tenant is "
            + "credited its weight times its run's priority, doubled for each period a unit of its waits past the aging "
            + "bound, and a unit starts once its tenant owes nothing, charged its cost as it starts - a debt outliving its "
            + "unit, paid back by its share each round, the faster once its tenant's next unit has waited past the aging "
            + "bound. Nothing is pre-empted, and nothing is changed by looking; the machine steps and reasoning agents are "
            + "shown, and asked by no unit yet. A machine shared by several users is not seen whole: each user's queue is "
            + "their own.");
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            async (context, cancellationToken) => QueueReports.Render(
                await context.Get<IShowQueue>().ShowAsync(context.Directory, cancellationToken).ConfigureAwait(false),
                context.Json),
            new JsonAnswer(JsonOption, (_, exitCode, summary, _, _) => QueueReports.Stopped(exitCode, summary)));

        return command;
    }
}
