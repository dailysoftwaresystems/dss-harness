using System.CommandLine;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness query-codebase-graph</c>.</summary>
internal static class QueryCodebaseGraphCommand
{
    internal const string Name = CodebaseGraphQueryService.CommandName;

    private static readonly Argument<string> RunArgument = new("run")
    {
        Description = "The run whose codebase graph to read, as its orchestrator is named.",
    };

    private static readonly Option<string> FindOption = new("--find")
    {
        Description = "Find the nodes that match a query, best first.",
    };

    private static readonly Option<string> NodeOption = new("--node")
    {
        Description = "Give a node's card and its edges by kind - a handle, id, path:line, or a qualified or simple name.",
    };

    private static readonly Option<string> RelatedOption = new("--related")
    {
        Description = "Walk from a node over one relation.",
    };

    private static readonly Option<string> RelationOption = new("--relation")
    {
        Description = $"Which edge --related follows: one of {string.Join(", ", GraphRelations.All)}.",
    };

    private static readonly Option<int?> DepthOption = new("--depth")
    {
        Description = $"How many hops --related walks, 1 to {CodebaseGraphTool.MostDepth}.",
    };

    private static readonly Option<string> OutlineOption = new("--outline")
    {
        Description = "Outline a file or a type by its path or symbol, or the repository where none is given.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Option<string[]> BodyOption = new("--body")
    {
        Description = $"Give the exact lines of nodes, read fresh: 1 to {CodebaseGraphTool.BodySymbols} handles, ids or names.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<string> AgentOption = new("--agent")
    {
        Description = "The agent whose view to read; the base where it is left out.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the answer as JSON: the run, the view, the commit, the operation, and the answer.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Read a run's codebase graph: one of the graph tool's read operations - find, node, related, outline or body - on the base or an agent's view, printing the answer an agent would be given. It reads only: no sync, nothing written.");

        command.Arguments.Add(RunArgument);
        command.Options.Add(FindOption);
        command.Options.Add(NodeOption);
        command.Options.Add(RelatedOption);
        command.Options.Add(RelationOption);
        command.Options.Add(DepthOption);
        command.Options.Add(OutlineOption);
        command.Options.Add(BodyOption);
        command.Options.Add(AgentOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => context.Get<CodebaseGraphQueryService>()
                .QueryAsync(context.Directory, Query(context.ParseResult), context.Json, cancellationToken),
            new JsonAnswer(JsonOption, (_, exitCode, summary, _, _) => CodebaseGraphQueryReports.Stopped(exitCode, summary)));

        return command;
    }

    private static CodebaseGraphQuery Query(ParseResult parseResult)
        => new(parseResult.GetRequiredValue(RunArgument), parseResult.GetValue(AgentOption))
        {
            Find = parseResult.GetValue(FindOption),
            Node = parseResult.GetValue(NodeOption),
            Related = parseResult.GetValue(RelatedOption),
            Relation = parseResult.GetValue(RelationOption),
            Depth = parseResult.GetValue(DepthOption),
            Outline = parseResult.GetResult(OutlineOption) is { Implicit: false },
            OutlineOf = parseResult.GetValue(OutlineOption),
            Body = parseResult.GetResult(BodyOption) is { Implicit: false } ? parseResult.GetValue(BodyOption) : null,
        };
}
