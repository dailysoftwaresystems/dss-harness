using System.CommandLine;
using RepoHarness.Core.Anchors;
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

    /// <summary>
    /// The option that answers with one JSON document - the whole of standard output - on the commands that take it: the
    /// outcome as data, with what is wanted of the caller and who holds what its --apply needs.
    /// </summary>
    internal static readonly Option<bool> Json = new("--json")
    {
        Description = "Print the outcome as one JSON document, the whole of standard output: its exit code, its summary and its lines, what is wanted "
            + "of you before --apply would go through, and who holds what it needs.",
    };

    /// <summary>
    /// The --json of fold-agent, delete-agent, refresh-agent and rebase-agent, which a verb that stops before it weighs
    /// anything answers with the agent document for, empty.
    /// </summary>
    internal static readonly JsonAnswer AgentAnswer = new(Json, (_, exitCode, summary, _, _) => OrchestrationReports.Stopped(exitCode, summary));

    /// <summary>The option that takes a clean three-way merge of a path both trees changed: one name, whichever command takes it.</summary>
    internal static Option<string[]> Merge(string description) => new(FoldAllowances.MergeOption) { HelpName = "path", Description = description };
}

/// <summary>The options that let a fold through what it otherwise refuses, which fold-agent and delete-agent both take.</summary>
internal static class FoldOptions
{
    /// <summary>A path both trees changed, whose clean three-way merge the fold may write.</summary>
    internal static Option<string[]> Merge { get; } = OrchestrationArguments.Merge(
        "A path both trees changed whose three-way merge is clean, as the dry run says of it: the merged file is written into both trees and recorded as shared. "
        + "Never taken unasked, and a clean textual merge proves nothing about the program. Once for each path.");

    /// <summary>A path reconciled in the main tree by hand, whose state there the fold hands the agent.</summary>
    internal static Option<string[]> Settled { get; } = new(FoldAllowances.SettledOption)
    {
        HelpName = "path",
        Description = "A path you reconciled in the main tree by hand: the fold hands the agent the main tree's copy and records it as shared, so the rest can go in "
            + "and its next fold starts from it. Refused where only the agent changed the path. Once for each path.",
    };

    /// <summary>A row the agent filed that the fold may make.</summary>
    internal static Option<string[]> New { get; } = new(AnchorBatchRequest.NewOption)
    {
        HelpName = "ID",
        Description = "The id of a row the agent filed that the fold may make: a row no registry holds is otherwise refused, as a typo in an existing row's id would make it a second row. Once for each id.",
    };

    /// <summary>A cell of an existing row the fold may write though its stored text does not survive.</summary>
    internal static Option<string[]> AcceptLost { get; } = new(AnchorBatchRequest.AcceptLostOption)
    {
        HelpName = AnchorRowCell.Form.Replace("<", string.Empty, StringComparison.Ordinal).Replace(">", string.Empty, StringComparison.Ordinal),
        Description = $"A cell of an existing row - {string.Join(", ", AnchorCellNames.Text)} - the fold may write though the agent's text does not keep its stored text word for word: read its word diff in the dry run first. Once for each cell.",
    };

    /// <summary>A cell of a row an earlier fold applied that the registries and the agent both changed since, which the fold may write the agent's over.</summary>
    internal static Option<string[]> AcceptOver { get; } = new(AnchorBatchRequest.AcceptOverOption)
    {
        HelpName = AcceptLost.HelpName,
        Description = $"A cell of a row an earlier fold of the agent applied - {string.Join(", ", AnchorCellNames.All)} - that the registries and the agent both changed since, "
            + "each otherwise: the fold may write the agent's over the registries'. Read the three texts in the dry run first. Once for each cell.",
    };

    /// <summary>Adds the options to <paramref name="command"/>, in the order its help lists them.</summary>
    internal static void AddTo(Command command)
    {
        command.Options.Add(Merge);
        command.Options.Add(Settled);
        command.Options.Add(New);
        command.Options.Add(AcceptLost);
        command.Options.Add(AcceptOver);
    }

    /// <summary>What <paramref name="result"/> lets a fold through.</summary>
    internal static FoldAllowances Read(ParseResult result) => new()
    {
        Merge = result.GetValue(Merge) ?? [],
        Settled = result.GetValue(Settled) ?? [],
        New = result.GetValue(New) ?? [],
        AcceptLost = result.GetValue(AcceptLost) ?? [],
        AcceptOver = result.GetValue(AcceptOver) ?? [],
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

    /// <summary>Its --json, which a listing that stops before it lists anything answers with its listing for, listing nothing.</summary>
    private static readonly JsonAnswer JsonAnswerOfList = new(JsonOption, (_, exitCode, summary, _, _) => OrchestrationReports.ListStopped(exitCode, summary));

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
            JsonAnswerOfList));

        return command;
    }
}
