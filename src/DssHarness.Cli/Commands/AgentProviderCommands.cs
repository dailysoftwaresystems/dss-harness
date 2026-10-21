using System.CommandLine;
using DssHarness.Cli;
using DssHarness.Core.Agents;

namespace DssHarness.Cli.Commands;

internal static class ListAgentProviderCommand
{
    internal const string Name = AgentProvidersService.ListCommand;

    private static readonly Option<bool> TemplateOption = new("--template")
    {
        Description = "Print the defaults.agentTier, providers and tierConfig sections as init writes them into a new config, to paste into an existing one.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Print the listing as one JSON document.",
    };

    /// <summary>
    /// Its --json, which a listing that stops before it lists anything answers with its listing for, listing nothing -
    /// and --template, whose document is its own, with the template's, printing none.
    /// </summary>
    private static readonly JsonAnswer JsonAnswerOfList = new(
        JsonOption,
        (flags, exitCode, summary, _, _) => flags.IsSet(TemplateOption)
            ? AgentProviderReports.TemplateStopped(exitCode, summary)
            : AgentProviderReports.Stopped(exitCode, summary));

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "List the configuration's providers, models, tiers and keys: for every name the configuration appoints, present or missing - never a value.");
        command.Options.Add(TemplateOption);
        command.Options.Add(JsonOption);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => context.Get<IAgentProviders>()
                .ListAsync(context.Directory, context.ParseResult.GetValue(TemplateOption), context.Json, cancellationToken),
            JsonAnswerOfList);

        return command;
    }
}
