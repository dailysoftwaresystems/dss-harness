using System.CommandLine;
using DssHarness.Core.Agents.SideTasks;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;

namespace DssHarness.Cli.Commands;

/// <summary>
/// Wires the two side-task verbs: <c>run-side-task</c> and <c>list-side-tasks</c>. A side-task counts the terminal's
/// interrupts: the first cuts the agent's turn, a second ends a chat (<see cref="SideTaskInterrupts"/>).
/// </summary>
internal static class SideTaskCommands
{
    /// <summary>
    /// The <c>--json</c> answer of <c>run-side-task</c>: the one document of a one-shot side-task, the same shape
    /// wherever it stops.
    /// </summary>
    internal static readonly JsonAnswer Answer = new(OrchestrationArguments.Json, (_, exitCode, summary, _, _) => SideTaskReports.Stopped(exitCode, summary));

    /// <summary>The <c>--json</c> answer of <c>list-side-tasks</c>: the listing, empty where it stops before it listed.</summary>
    internal static readonly JsonAnswer ListAnswer = new(OrchestrationArguments.Json, (_, exitCode, summary, _, _) => SideTaskReports.ListStopped(exitCode, summary));
}

/// <summary>Wires <c>DssHarness run-side-task</c>.</summary>
internal static class RunSideTaskCommand
{
    internal const string Name = SideTaskService.RunCommand;

    private static readonly Option<string?> PromptOption = new("--prompt")
    {
        Description = "One message, answered once: the agent works until its turn ends, its final text is printed, and the command exits. Without it the command holds a chat in the terminal.",
    };

    private static readonly Option<string?> PromptFileOption = new("--prompt-file")
    {
        HelpName = "path",
        Description = "A file holding the one message of a one-shot side-task, read relative to --directory, in the place of --prompt.",
    };

    private static readonly Option<string?> ResumeOption = new("--resume")
    {
        HelpName = "task",
        Description = "The side-task to open again, given its conversation as its records hold it: as a chat, or with --prompt for one more message.",
    };

    private static readonly Option<string?> NameOption = new("--name")
    {
        HelpName = "task",
        Description = "The side-task's own name, which names its directory under .side-tasks; where none is given, the date and time it began - with the first words of the message, in one shot.",
    };

    private static readonly Option<string?> ModelOption = new("--model")
    {
        HelpName = "code",
        Description = "The model to run on, by its code, as a step's model is named outright.",
    };

    private static readonly Option<string?> TierOption = new("--tier")
    {
        HelpName = "tier",
        Description = "The tier whose first model runs, whose later models the side-task goes on by as a step does.",
    };

    private static readonly Option<string?> ClientOption = new("--client")
    {
        HelpName = "client",
        Description = "The client to reach the model through, among the clients that reach it.",
    };

    private static readonly Option<string[]> EffortOption = new("--effort")
    {
        HelpName = "level | code=level",
        Description = "The effort to run at: one level for every model (--effort high), or one entry per model with all=<level> for the rest. Once for each entry. Checked against what each model takes.",
    };

    private static readonly Option<bool> NoGraphOption = new("--no-graph")
    {
        Description = "Give the side-task no codebase graph: no graph tool, no repository map, no context pack, nothing synced.",
    };

    internal static Command Create()
    {
        var command = new Command(Name, "An agent outside every workflow: a conversation with a person over the repository's main tree, with no step and no limit of a run's - every action still decided by the engine before it runs and recorded, the built-in refusals kept. A chat in the terminal by default - the agent's text streamed, each action one line as it is decided, Ctrl+C interrupting a turn and a second ending the chat as /exit does - a turn under way cut at its result, so what it used and cost is kept - or one message with --prompt, answered once, Ctrl+C there cutting its turn. Where no turn runs, while it is set up or a model made ready, an interrupt stops it: a one shot's first, a chat's second. Where it cannot be set up it exits 12, 13, 14 or 21 - 14 where its client cannot start or, under agentPolicy.isolation \"required\", this machine cannot isolate the agent's commands, 21 where the install of Claude Code timed out or could not reach the registry; 'help side-tasks' gives every code. Its model is resolved as a step's; its records live under .side-tasks/<task>/, named on every exit and kept until a person removes them.");
        command.Options.Add(PromptOption);
        command.Options.Add(PromptFileOption);
        command.Options.Add(ResumeOption);
        command.Options.Add(NameOption);
        command.Options.Add(ModelOption);
        command.Options.Add(TierOption);
        command.Options.Add(ClientOption);
        command.Options.Add(EffortOption);
        command.Options.Add(NoGraphOption);
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            async (context, cancellationToken) =>
            {
                var prompt = context.ParseResult.GetValue(PromptOption);
                var promptFile = context.ParseResult.GetValue(PromptFileOption);
                var output = context.Get<IHarnessOutput>();
                var oneShot = prompt is not null || promptFile is not null;

                // The command counts its own interrupts (PointOfNoReturn.ChatsWithAPerson): the terminal's are taken
                // through the one registration of the process's signals, for as long as the side-task runs, and none
                // ends the process.
                var terminal = new SideTaskTerminal(output, chat: !oneShot);

                using (terminal.TakeSignals())
                {
                    return await context.Get<ISideTaskService>().RunAsync(
                        new SideTaskCall(
                            context.Directory,
                            prompt,
                            promptFile,
                            context.ParseResult.GetValue(ResumeOption),
                            context.ParseResult.GetValue(NameOption),
                            context.ParseResult.GetValue(ModelOption),
                            context.ParseResult.GetValue(TierOption),
                            context.ParseResult.GetValue(ClientOption),
                            context.ParseResult.GetValue(EffortOption),
                            context.Json,
                            oneShot ? null : Console(output))
                        {
                            Interrupts = terminal.Interrupts,
                            NoGraph = context.ParseResult.GetValue(NoGraphOption),
                        },
                        cancellationToken).ConfigureAwait(false);
                }
            },
            SideTaskCommands.Answer);

        return command;
    }

    /// <summary>
    /// The terminal's own chat front end: the person's lines read from the console on a reader of their own - left
    /// behind where the chat ends first, for the command's end ends the process - and the chat's own lines said
    /// unprefixed. The interrupts are the side-task's own (<see cref="SideTaskTerminal"/>), which end a chat whatever its
    /// reading is doing.
    /// </summary>
    /// <param name="output">Where the chat's own lines are said, unprefixed.</param>
    private static SideTaskChat Console(IHarnessOutput output)
    {
        var lines = System.Threading.Channels.Channel.CreateUnbounded<string?>();

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            while (true)
            {
                string? read;

                try
                {
                    read = System.Console.ReadLine();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    read = null;
                }

                if (!lines.Writer.TryWrite(read) || read is null)
                {
                    return;
                }
            }
        });

        return new SideTaskChat(cancellationToken => lines.Reader.ReadAsync(cancellationToken).AsTask(), line => output.Raw(line));
    }
}

/// <summary>Wires <c>DssHarness list-side-tasks</c>.</summary>
internal static class ListSideTasksCommand
{
    internal const string Name = SideTaskService.ListCommand;

    internal static Command Create()
    {
        var command = new Command(Name, "The repository's side-tasks, newest first: each by its name, when it began and last spoke, its model, its turns and cost, and whether it is open now. Their records are kept until a person removes them.");
        command.Options.Add(OrchestrationArguments.Json);
        GlobalOptions.AddTo(command);

        CommandRunner.Wire(
            command,
            (context, cancellationToken) => context.Get<ISideTaskService>().ListAsync(context.Directory, context.Json, cancellationToken),
            SideTaskCommands.ListAnswer);

        return command;
    }
}
