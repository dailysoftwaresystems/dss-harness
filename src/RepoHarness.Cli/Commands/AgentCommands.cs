using System.CommandLine;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness create-agent</c>.</summary>
internal static class CreateAgentCommand
{
    internal const string Name = AgentService.CreateCommand;

    private static readonly Argument<string> OrchestratorArgument = OrchestrationArguments.Orchestrator("The orchestrator the agent works for.");

    private static readonly Argument<string> AgentArgument = OrchestrationArguments.Agent(
        "The agent's name, which names its directory and its worktree, orchestrator/agent under the worktrees root; never used twice.");

    private static readonly Option<string> ModelOption = OrchestrationArguments.Model("The model the agent's session runs on, as its id.");

    private static readonly Option<bool> EmptyOption = new("--empty")
    {
        Description = "Hand it nothing: record that it was made at a commit and given none of the main tree's uncommitted state.",
    };

    private static readonly Option<string?> SessionOption = OrchestrationArguments.Session(
        "The agent's Claude session id, or its subagent's id, whose transcripts delete-agent keeps. Run again, it records only this.");

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Create an agent of an orchestrator: its record, its worktree, and its seed - the main tree's uncommitted state handed to it, each changed file copied and each deletion made, every path recorded; refused past the orchestrator's limit.");
        command.Arguments.Add(OrchestratorArgument);
        command.Arguments.Add(AgentArgument);
        command.Options.Add(ModelOption);
        command.Options.Add(EmptyOption);
        command.Options.Add(SessionOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) => context.Get<IAgentService>()
            .CreateAsync(
                context.Directory,
                context.ParseResult.GetRequiredValue(OrchestratorArgument),
                context.ParseResult.GetRequiredValue(AgentArgument),
                context.ParseResult.GetRequiredValue(ModelOption),
                context.ParseResult.GetValue(EmptyOption),
                context.ParseResult.GetValue(SessionOption),
                cancellationToken)));

        return command;
    }
}

/// <summary>Wires <c>DssHarness seed-agent</c>.</summary>
internal static class SeedAgentCommand
{
    internal const string Name = AgentService.SeedCommand;

    private static readonly Argument<string> OrchestratorArgument = OrchestrationArguments.Orchestrator("The agent's orchestrator.");

    private static readonly Argument<string> AgentArgument = OrchestrationArguments.Agent("The agent to seed again.");

    private static readonly Option<bool> EmptyOption = new("--empty")
    {
        Description = "Hand it nothing more, and copy nothing: what it was handed before stays recorded.",
    };

    private static readonly Option<bool> ForceOption = new("--force")
    {
        Description = "Seed it though its worktree holds changes of its own, which the copies overwrite.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Seed a live agent again with the main tree's uncommitted state, and every path the main tree holds otherwise than the agent shares it, committed or "
            + "not; refused where its worktree holds changes of its own - a copy it was handed and left alone is not one - unless --force.");
        command.Arguments.Add(OrchestratorArgument);
        command.Arguments.Add(AgentArgument);
        command.Options.Add(EmptyOption);
        command.Options.Add(ForceOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) => context.Get<IAgentService>()
            .SeedAsync(
                context.Directory,
                context.ParseResult.GetRequiredValue(OrchestratorArgument),
                context.ParseResult.GetRequiredValue(AgentArgument),
                context.ParseResult.GetValue(EmptyOption),
                context.ParseResult.GetValue(ForceOption),
                cancellationToken)));

        return command;
    }
}

/// <summary>Wires <c>DssHarness refresh-agent</c>.</summary>
internal static class RefreshAgentCommand
{
    internal const string Name = AgentService.RefreshCommand;

    private static readonly Argument<string> OrchestratorArgument = OrchestrationArguments.Orchestrator("The agent's orchestrator.");

    private static readonly Argument<string> AgentArgument = OrchestrationArguments.Agent("The agent to refresh.");

    private static readonly Argument<string[]> PathsArgument = new("path")
    {
        Description = "The paths to refresh, relative to the tree; the directory the anchor registries are kept in where none is given.",
        Arity = ArgumentArity.ZeroOrMore,
    };

    private static readonly Option<bool> ApplyOption = OrchestrationArguments.Apply("Hand them over; without it, only say what would be handed over.");

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Hand a live agent every path under the paths that the main tree holds otherwise than the agent shares it - not what it was last handed, or "
            + "not what its base holds - committed or not, recorded as handed to it so its fold leaves them out; refused, copying nothing, where the agent "
            + "changed or deleted one of them.");
        command.Arguments.Add(OrchestratorArgument);
        command.Arguments.Add(AgentArgument);
        command.Arguments.Add(PathsArgument);
        command.Options.Add(ApplyOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) => context.Get<IAgentService>()
            .RefreshAsync(
                context.Directory,
                context.ParseResult.GetRequiredValue(OrchestratorArgument),
                context.ParseResult.GetRequiredValue(AgentArgument),
                context.ParseResult.GetValue(PathsArgument) ?? [],
                context.ParseResult.GetValue(ApplyOption),
                cancellationToken)));

        return command;
    }
}

/// <summary>Wires <c>DssHarness rebase-agent</c>.</summary>
internal static class RebaseAgentCommand
{
    internal const string Name = AgentService.RebaseCommand;

    private static readonly Argument<string> OrchestratorArgument = OrchestrationArguments.Orchestrator("The agent's orchestrator.");

    private static readonly Argument<string> AgentArgument = OrchestrationArguments.Agent("The agent whose base to move.");

    private static readonly Option<string[]> SettledOption = new(FoldAllowances.SettledOption)
    {
        HelpName = "path",
        Description = "A path the agent changed that the main tree committed a change to since its base, whose copy you reconciled by hand: kept as its own change on the new base. Once for each path.",
    };

    private static readonly Option<bool> ApplyOption = OrchestrationArguments.Apply("Move it; without it, only say what moving it would bring in and keep.");

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Move a live agent's base to the main tree's HEAD: what the main tree committed since comes into its worktree as git holds it, and its own "
            + "changes and what it was handed stay; refused, moving nothing, where it changed a path the main tree committed a change to since, "
            + "unless --settled. Run again after one that stopped part way, it finishes it.");
        command.Arguments.Add(OrchestratorArgument);
        command.Arguments.Add(AgentArgument);
        command.Options.Add(SettledOption);
        command.Options.Add(ApplyOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) => context.Get<IAgentService>()
            .RebaseAsync(
                context.Directory,
                context.ParseResult.GetRequiredValue(OrchestratorArgument),
                context.ParseResult.GetRequiredValue(AgentArgument),
                context.ParseResult.GetValue(SettledOption) ?? [],
                context.ParseResult.GetValue(ApplyOption),
                cancellationToken)));

        return command;
    }
}

/// <summary>Wires <c>DssHarness fold-agent</c>.</summary>
internal static class FoldAgentCommand
{
    internal const string Name = AgentService.FoldCommand;

    private static readonly Argument<string> OrchestratorArgument = OrchestrationArguments.Orchestrator("The agent's orchestrator.");

    private static readonly Argument<string> AgentArgument = OrchestrationArguments.Agent("The agent to fold.");

    private static readonly Option<bool> ApplyOption = OrchestrationArguments.Apply("Write the fold and the rows; without it, only say what would be written.");

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Fold a live agent's own changes into the main tree, then apply the anchor rows it filed: everything that refuses it is named before "
            + "anything is written, the rows go in all or nothing, and a file of either tree that changed after it was weighed stops the fold "
            + $"part way (exit {HarnessExit.Incomplete}) rather than being written over or copied changed; its worktree is kept.");
        command.Arguments.Add(OrchestratorArgument);
        command.Arguments.Add(AgentArgument);
        command.Options.Add(ApplyOption);
        FoldOptions.AddTo(command);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) => context.Get<IAgentService>()
            .FoldAsync(
                context.Directory,
                context.ParseResult.GetRequiredValue(OrchestratorArgument),
                context.ParseResult.GetRequiredValue(AgentArgument),
                FoldOptions.Read(context.ParseResult),
                context.ParseResult.GetValue(ApplyOption),
                cancellationToken)));

        return command;
    }
}

/// <summary>Wires <c>DssHarness delete-agent</c>.</summary>
internal static class DeleteAgentCommand
{
    internal const string Name = AgentService.DeleteCommand;

    private static readonly Argument<string> OrchestratorArgument = OrchestrationArguments.Orchestrator("The agent's orchestrator.");

    private static readonly Argument<string> AgentArgument = OrchestrationArguments.Agent("The agent to delete.");

    private static readonly Option<bool> ApplyOption = OrchestrationArguments.Apply("Do it; without it, only say what would be done.");

    private static readonly Option<bool> DiscardUncommittedOption = new("--discard-uncommitted")
    {
        Description = "Abandon the agent: fold nothing and apply no rows, and remove its worktree with its changes; its evidence and transcripts are still kept.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Delete an agent: fold what is left of it and apply its rows, keep its evidence and transcripts, then remove its worktree and its copies on hosts; its directory stays as its history.");
        command.Arguments.Add(OrchestratorArgument);
        command.Arguments.Add(AgentArgument);
        command.Options.Add(ApplyOption);
        FoldOptions.AddTo(command);
        command.Options.Add(DiscardUncommittedOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) => context.Get<IAgentService>()
            .DeleteAsync(
                context.Directory,
                context.ParseResult.GetRequiredValue(OrchestratorArgument),
                context.ParseResult.GetRequiredValue(AgentArgument),
                FoldOptions.Read(context.ParseResult),
                context.ParseResult.GetValue(ApplyOption),
                context.ParseResult.GetValue(DiscardUncommittedOption),
                cancellationToken)));

        return command;
    }
}
