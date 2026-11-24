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
        Description = "How many hops --related walks, from 1.",
    };

    private static readonly Option<string> OutlineOption = new("--outline")
    {
        Description = "Outline a file or a type by its path or symbol, or the repository where none is given.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Option<string[]> BodyOption = new("--body")
    {
        Description = "Give the exact lines of nodes, read fresh: one or more handles, ids or names.",
        Arity = ArgumentArity.OneOrMore,
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<string> SitesOption = new("--sites")
    {
        Description = "Every place that decides data - a name, a key path (dotted - a name holding a dot or a bracket quoted in brackets, A[\"B.C\"] or A['B.C'], or its dots as they are, A.B.C, asking of every key it may mean; or as .NET's configuration writes one: A:B, A__B), a diagnostic id, or a quoted literal asked by its value alone: what defines, calls, uses, makes, references, inherits, implements or overrides it, every literal that holds it and every key it names, with the coverage lines.",
    };

    private static readonly Option<int?> PageOption = new("--page")
    {
        Description = "Which page of the answer to give, from 1; the first where it is left out.",
    };

    private static readonly Option<string> AgentOption = new("--agent")
    {
        Description = "The agent whose view to read; the base where it is left out.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write the answer as JSON: the run, the view, the commit, the digest of the graph answered from, the operation, the answer, and its page, pages, total and entries.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Read a run's codebase graph: one of the graph tool's read operations - find, node, related, outline, body or sites - on the base or an agent's view, printing the answer an agent would be given, in pages of whole entries with any page asked by --page. It reads only: no sync, nothing written.");

        command.Arguments.Add(RunArgument);
        command.Options.Add(FindOption);
        command.Options.Add(NodeOption);
        command.Options.Add(RelatedOption);
        command.Options.Add(RelationOption);
        command.Options.Add(DepthOption);
        command.Options.Add(OutlineOption);
        command.Options.Add(BodyOption);
        command.Options.Add(SitesOption);
        command.Options.Add(PageOption);
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
            Sites = parseResult.GetValue(SitesOption),
            Page = parseResult.GetValue(PageOption),
        };
}
