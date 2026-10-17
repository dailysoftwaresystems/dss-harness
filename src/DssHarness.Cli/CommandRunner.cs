using System.CommandLine;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using DssHarness.Core.Application;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;

namespace DssHarness.Cli;

/// <summary>What a command body receives.</summary>
/// <param name="Services">The services for this invocation.</param>
/// <param name="ParseResult">The parsed command line, for the command's own arguments.</param>
/// <param name="Directory">
/// The directory to act on: absolute, and already confirmed to exist. Commands use this
/// rather than reading the option themselves, so none can act on a value that was not checked.
/// </param>
/// <param name="Json">
/// Whether the command was asked for its <c>--json</c> document, as <see cref="JsonAnswer.Asked"/> read it where the command
/// was wired; false for a command that takes no <c>--json</c>. Commands use this rather than reading the option again, so
/// none can answer otherwise than the document it registered says it was asked.
/// </param>
internal sealed record CommandContext(IServiceProvider Services, ParseResult ParseResult, string Directory, bool Json)
{
    /// <summary>Resolves a service.</summary>
    internal T Get<T>()
        where T : notnull
        => Services.GetRequiredService<T>();
}

/// <summary>
/// Shared plumbing for every command: validate the global options, build services,
/// run the command's own orchestration, translate failures into exit codes, and
/// report the outcome. Keeping this in one place is what lets each command file
/// stay orchestration only, and is why every command refuses a bad directory the
/// same way rather than each discovering it differently.
/// </summary>
internal static class CommandRunner
{
    /// <summary>
    /// Who the commands run from here on are run for: somebody typing them here, where nothing has said otherwise.
    /// </summary>
    /// <remarks>
    /// Held for the flow of the request the agent serves, rather than for the process: a run request goes
    /// back through this same parser, in the host's copy, and nothing in its command line may say who
    /// asked for it without the command then answering to an argument its own user never typed.
    /// </remarks>
    private static readonly AsyncLocal<CommandOrigin?> Origin = new();

    /// <summary>
    /// Each command's <c>--json</c>, with the document it answers with where it has none of its own, as the command was
    /// wired (<see cref="Wire(Command, Func{CommandContext, CancellationToken, Task{CommandOutcome}}, JsonAnswer?)"/>): the
    /// one place a command line the parser refused, which reaches no command's action, finds the document its command
    /// answers with.
    /// </summary>
    private static readonly ConditionalWeakTable<Command, JsonAnswer> Answers = new();

    /// <summary>
    /// The host every command runs through: one session per invocation, over the services the tool's own registrations
    /// build, the lifecycle in Core and the parsing and the console here.
    /// </summary>
    private static readonly DssHarnessHost Host = ConsoleHost.Host;

    /// <summary>
    /// Marks what this flow runs from here on as run for <paramref name="origin"/>: by the host agent before it serves a
    /// request, so nothing that flow runs is taken for something typed here, and with what the machine that asked says of
    /// a command before that command runs.
    /// </summary>
    /// <param name="origin">Who the commands are run for.</param>
    internal static void Serve(CommandOrigin origin) => Origin.Value = origin;

    /// <summary>Wires a command body as the action the parser invokes for <paramref name="command"/>.</summary>
    /// <param name="command">The command, whose name prefixes every line it writes.</param>
    /// <param name="body">What the command does.</param>
    /// <param name="json">
    /// The command's <c>--json</c>, with the document it answers with where it has none of its own - its own document's
    /// shape, empty (<see cref="JsonAnswer"/>): where the parser refuses its command line (<see cref="UnparsedAsync"/>),
    /// where it stops before its body has an outcome, and where its body answers without one (rendered by the session's
    /// own report).
    /// Asked for it, the command answers with one document on every exit save its usage, which <c>--help</c> asks for and
    /// the parser answers before any command's action, and a --json given twice with a value each, which asks for none:
    /// a script reading standard output has one document to read whatever happened.
    /// </param>
    internal static void Wire(
        Command command,
        Func<CommandContext, CancellationToken, Task<CommandOutcome>> body,
        JsonAnswer? json = null)
        => Wire(command, (context, _, cancellationToken) => body(context, cancellationToken), json, beginsRun: _ => false);

    /// <summary>
    /// Wires the body of a command that keeps runs - build, test, run and check-mutations - as the action the parser
    /// invokes for <paramref name="command"/>, beginning its run before anything can refuse it, and handing the body that
    /// run.
    /// </summary>
    /// <param name="command">The command, whose name prefixes every line it writes.</param>
    /// <param name="body">What the command does, given the run it began.</param>
    /// <param name="json">The command's <c>--json</c>, as <see cref="Wire(Command, Func{CommandContext, CancellationToken, Task{CommandOutcome}}, JsonAnswer?)"/> takes it.</param>
    /// <remarks>
    /// The run is named in the command's first line, and as <c>runId</c> in its <c>--json</c> ledger on every exit, and
    /// is the one its records are kept under: a run refused before it had a directory keeps no records, and its id is
    /// then all there is to cite it by.
    /// </remarks>
    internal static void Wire(
        Command command,
        Func<CommandContext, RunId, CancellationToken, Task<CommandOutcome>> body,
        JsonAnswer? json = null)
        => Wire(command, (context, run, cancellationToken) => body(context, run!, cancellationToken), json, beginsRun: _ => true);

    /// <summary>
    /// Wires the body of a command whose parsed command line decides whether it begins a run - check-mutations, whose
    /// --list sweeps nothing and begins none - handing the body the run where it began one, and nothing where not.
    /// </summary>
    /// <param name="command">The command, whose name prefixes every line it writes.</param>
    /// <param name="body">What the command does, given the run it began, or nothing where it began none.</param>
    /// <param name="beginsRun">Whether the parsed command line begins a run.</param>
    /// <param name="json">The command's <c>--json</c>, as <see cref="Wire(Command, Func{CommandContext, CancellationToken, Task{CommandOutcome}}, JsonAnswer?)"/> takes it.</param>
    /// <remarks>A command line that begins no run prints no <c>run &lt;id&gt;</c> line, and keeps no records.</remarks>
    internal static void Wire(
        Command command,
        Func<CommandContext, RunId?, CancellationToken, Task<CommandOutcome>> body,
        Func<ParseResult, bool> beginsRun,
        JsonAnswer? json = null)
        => Wire(command, body, json, beginsRun);

    /// <summary>
    /// Ends a command line the parser refused - an option its command does not take, an argument missing or one too
    /// many - with the code a usage error has, rather than the parser's own 1, which collides with verify-git's contract,
    /// where 1 means git is not installed. Asked for <c>--json</c>, its command answers first with the document it
    /// registered where it was wired, empty, the first error the line it ends on, as it answers on every other exit; no
    /// run is named, since a command line the parser refuses never began one. Then each error, and where usage is told.
    /// </summary>
    /// <param name="parseResult">The command line the parser refused.</param>
    /// <returns>The code a usage error has.</returns>
    internal static async Task<int> UnparsedAsync(ParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        // Read by the one reader that never throws: reading a flag whose own value the parser refused for that value would.
        var flags = new CommandFlags(parseResult);

        if (Answers.TryGetValue(parseResult.CommandResult.Command, out var json) && json.Asked(flags))
        {
            // Told as every other line the command writes is told: a host answering another machine writes its home as ~.
            await using var services = ConsoleHost.Build(new SessionOptions
            {
                Command = parseResult.CommandResult.Command.Name,
                Verbose = false,
                Prompting = false,
                Origin = Origin.Value ?? CommandOrigin.Typed,
            });
            var output = services.GetRequiredService<IHarnessOutput>();

            output.Data(json.Stopped(flags, HarnessExit.UsageError, parseResult.Errors[0].Message, output.Shown, run: null));
        }

        foreach (var error in parseResult.Errors)
        {
            Console.Error.WriteLine(error.Message);
        }

        Console.Error.WriteLine($"Run '{ToolPackage.Command} --help' for usage.");
        return HarnessExit.UsageError;
    }

    /// <summary>
    /// Wires a command body, given its run where its parsed command line <paramref name="beginsRun"/>, as the action the
    /// parser invokes for <paramref name="command"/>, and keeps the document it answers with where it has none of its own.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="command"/> was wired with its document already.</exception>
    private static void Wire(
        Command command,
        Func<CommandContext, RunId?, CancellationToken, Task<CommandOutcome>> body,
        JsonAnswer? json,
        Func<ParseResult, bool> beginsRun)
    {
        ArgumentNullException.ThrowIfNull(command);

        var commandName = command.Name;

        if (json is not null)
        {
            Answers.Add(command, json);
        }

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // Read by the one reader that never throws: reading a flag whose own value the parser refused for that value would.
            var flags = new CommandFlags(parseResult);

            // Asked for its document, a command that stops answers in its own document's shape, as it registered it.
            var asked = json is not null && json.Asked(flags);

            var session = Host.Begin(
                new SessionOptions
                {
                    Command = commandName,
                    Verbose = parseResult.GetValue(GlobalOptions.Verbose),
                    Prompting = !parseResult.GetValue(GlobalOptions.NoPrompt),
                    Origin = Origin.Value ?? CommandOrigin.Typed,
                    BeginsRun = beginsRun(parseResult),
                    Document = asked ? DocumentKind.Own : DocumentKind.None,
                    Directory = parseResult.GetValue(GlobalOptions.Directory),
                },
                new SessionFrontEnd(
                    Stopped: asked && json is not null
                        ? (context, exitCode, summary) => json.Stopped(flags, exitCode, summary, context.Output.Shown, context.Run)
                        : null));

            await using (session.ConfigureAwait(false))
            {
                var completion = await session.RunAsync(
                    (scope, token) => body(
                        new CommandContext(scope.Services, parseResult, scope.Directory, Json: asked),
                        scope.Run,
                        token),
                    cancellationToken).ConfigureAwait(false);

                return completion.ExitCode;
            }
        });
    }
}
