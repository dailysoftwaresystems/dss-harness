using System.CommandLine;
using DssHarness.Core.Tools;

namespace DssHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness install-missing-tools</c>.</summary>
internal static class InstallMissingToolsCommand
{
    internal const string Name = ToolProvisionService.CommandName;

    private static readonly Option<string[]> LegsOption = new("--legs")
    {
        Description = "Only these legs or leg sets: --legs a,b or --legs a b. Without it, every declared leg.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Write what each leg's host has, and what was installed there, as JSON.",
    };

    private static readonly Option<bool> DryRunOption = new("--dry-run")
    {
        Description = "Reach and ask every host as usual, and install nothing: name each command that would run, sudo and all, without asking for a password.",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            "Install what each selected leg's host is missing: the .NET SDK on every WSL distribution and ssh host, "
            + "and every tool under \"tools\" that declares how to install it. A tool that declares no install is reported, never installed.");

        command.Options.Add(LegsOption);
        command.Options.Add(JsonOption);
        command.Options.Add(DryRunOption);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(command, async (context, cancellationToken) =>
        {
            var arguments = context.ParseResult;

            // Left out, --legs selects every leg; given, it must name one. Which of the two happened is told by
            // whether the option appeared at all, never by what its value holds.
            var legs = arguments.GetResult(LegsOption) is { Implicit: false }
                ? arguments.GetValue(LegsOption) ?? []
                : null;

            var report = await context.Get<IToolProvisionService>()
                .ProvisionAsync(context.Directory, legs, arguments.GetValue(DryRunOption), cancellationToken)
                .ConfigureAwait(false);

            return ToolProvisionReports.Render(report, context.Json);
        }, new JsonAnswer(JsonOption, (flags, exitCode, summary, _, _) => ToolProvisionReports.Stopped(exitCode, summary, flags.IsSet(DryRunOption))));

        return command;
    }
}
