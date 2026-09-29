using System.CommandLine;
using RepoHarness.Core.Orchestration;

namespace RepoHarness.Cli.Commands;

/// <summary>The arguments and options the orchestrator and agent commands share, each spelt once.</summary>
internal static class OrchestrationArguments
{
    internal static Argument<string> Orchestrator(string description) => new("orchestrator") { Description = description };

    internal static Argument<string> Agent(string description) => new("agent") { Description = description };

    internal static Option<string> Model(string description) => new("--model") { Description = description, Required = true };

    internal static Option<string?> Session(string description) => new("--session") { Description = description };

    internal static Option<bool> Apply(string description) => new("--apply") { Description = description };

    internal static Option<string[]> Settled() => new("--settled")
    {
        Description = "A path you reconciled in the main tree by hand, left out of the fold so the rest can go in: neither compared nor written. Once for each path.",
    };
}

/// <summary>Wires <c>DssHarness create-orchestrator</c>.</summary>
internal static class CreateOrchestratorCommand
{
    internal const string Name = OrchestratorService.CreateCommand;

    private static readonly Argument<string> NameArgument = OrchestrationArguments.Orchestrator(
        "The orchestrator's name, which names its directory under .orchestrators and the directory its agents' worktrees are made in: lowercase letters and digits joined by hyphens.");

    private static readonly Option<string> ModelOption = OrchestrationArguments.Model("The model the orchestrating session runs on, as its id.");

    private static readonly Option<int?> ParallelOption = new("--parallel")
    {
        Description = $"The most agents with a worktree at once; {OrchestratorRecord.DefaultParallel} for a new orchestrator where none is given. Run again, it changes only this and --session.",
    };

    private static readonly Option<string?> SessionOption = OrchestrationArguments.Session("The orchestrating session's Claude session id.");

    internal static Command Create()
    {
        var command = new Command(Name, "Create an orchestrator under .orchestrators: its record, logs, work and plans directories, and where its agents are kept.");
        command.Arguments.Add(NameArgument);
        command.Options.Add(ModelOption);
        command.Options.Add(ParallelOption);
        command.Options.Add(SessionOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) => context.Get<IOrchestratorService>()
            .CreateAsync(
                context.Directory,
                context.ParseResult.GetRequiredValue(NameArgument),
                context.ParseResult.GetRequiredValue(ModelOption),
                context.ParseResult.GetValue(ParallelOption),
                context.ParseResult.GetValue(SessionOption),
                cancellationToken)));

        return command;
    }
}

/// <summary>Wires <c>DssHarness delete-orchestrator</c>.</summary>
internal static class DeleteOrchestratorCommand
{
    internal const string Name = OrchestratorService.DeleteCommand;

    private static readonly Argument<string> NameArgument = OrchestrationArguments.Orchestrator("The orchestrator to delete.");

    private static readonly Option<bool> DeleteEvidenceOption = new("--delete-evidence")
    {
        Description = "Delete the evidence and transcripts its agents kept with it, which it refuses to delete otherwise.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "Delete an orchestrator's directory, once every one of its agents is deleted and no worktree is left below its directory under the worktrees root.");
        command.Arguments.Add(NameArgument);
        command.Options.Add(DeleteEvidenceOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) => context.Get<IOrchestratorService>()
            .DeleteAsync(
                context.Directory,
                context.ParseResult.GetRequiredValue(NameArgument),
                context.ParseResult.GetValue(DeleteEvidenceOption),
                cancellationToken)));

        return command;
    }
}

/// <summary>Wires <c>DssHarness list-orchestrator</c>.</summary>
internal static class ListOrchestratorCommand
{
    internal const string Name = OrchestratorService.ListCommand;

    private static readonly Argument<string?> NameArgument = new("orchestrator")
    {
        Description = "The orchestrator to list; every one where none is given.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Print the listing as one JSON document.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "List orchestrators, their agents and where each stands, and worktrees below an orchestrator's directory that no agent records.");
        command.Arguments.Add(NameArgument);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(
            Name,
            (context, cancellationToken) => context.Get<IOrchestratorService>()
                .ListAsync(context.Directory, context.ParseResult.GetValue(NameArgument), context.ParseResult.GetValue(JsonOption), cancellationToken),
            JsonOption));

        return command;
    }
}
