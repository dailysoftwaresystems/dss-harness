using System.CommandLine;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Cli.Commands;

/// <summary>Wires <c>DssHarness update-tool</c>.</summary>
internal static class UpdateToolCommand
{
    internal const string Name = ToolUpdateService.CommandName;

    private static readonly Option<bool> WaitOption = new(ToolUpdateService.WaitOption)
    {
        Description = $"Wait for every {ToolPackage.Id} running on this machine to end, saying who it waits for, rather than be refused by one.",
    };

    private static readonly Option<string?> ToolPathOption = new(ToolUpdateService.ToolPathOption)
    {
        Description = $"The directory {ToolPackage.Id} is kept in, where it was installed with dotnet's own --tool-path. Without it, this user's global tools.",
        HelpName = "directory",
    };

    internal static Command Create()
    {
        var command = new Command(
            Name,
            $"Update the {ToolPackage.Id} installed on this machine to the newest release, only while no {ToolPackage.Id} runs here: "
            + "an update beside one takes the tool away from every command typed meanwhile. On Windows, run it from beside the "
            + $"installed tool: {ToolUpdateService.FromBeside(new ToolUpdateRequest())}");

        command.Options.Add(WaitOption);
        command.Options.Add(ToolPathOption);
        GlobalOptions.AddTo(command);

        command.SetAction(CommandRunner.Wrap(Name, (context, cancellationToken) => context.Get<ToolUpdateService>().RunAsync(
            new ToolUpdateRequest(context.ParseResult.GetValue(WaitOption), context.ParseResult.GetValue(ToolPathOption)),
            cancellationToken)));

        return command;
    }
}
