using System.CommandLine;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;

namespace RepoHarness.Cli;

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
    /// where it stops before its body has an outcome, and where its body answers without one (<see cref="Report"/>).
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
            await using var services = HarnessServices.Build(verbose: false, prompting: false, Origin.Value ?? CommandOrigin.Typed);
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
            var verbose = parseResult.GetValue(GlobalOptions.Verbose);
            var prompting = !parseResult.GetValue(GlobalOptions.NoPrompt);
            await using var services = HarnessServices.Build(verbose, prompting, Origin.Value ?? CommandOrigin.Typed);
            var output = services.GetRequiredService<IHarnessOutput>();
            var run = beginsRun(parseResult) ? RunId.New() : null;
            var flags = new CommandFlags(parseResult);

            // Asked for its document, a command that stops answers in its own document's shape, as it registered it.
            Func<int, string, string>? stopped = json is not null && json.Asked(flags)
                ? (exitCode, summary) => json.Stopped(flags, exitCode, summary, output.Shown, run)
                : null;

            // The document is the whole of standard output from the first line, not from the moment
            // the legs are surveyed: a line written before then would sit in front of it.
            using var document = stopped is not null ? output.DataOnly() : null;

            if (run is not null)
            {
                output.Info(commandName, RunLine.Message(run));
            }

            try
            {
                if (!TryResolveDirectory(parseResult, services, out var directory, out var problem))
                {
                    return Stop(output, commandName, HarnessExit.UsageError, problem, stopped);
                }

                // Its body answers with its document exactly where a stop would: --json read once, here.
                var context = new CommandContext(services, parseResult, directory, Json: stopped is not null);
                var outcome = await body(context, run, cancellationToken).ConfigureAwait(false);

                Report(output, commandName, outcome, stopped);
                return outcome.ExitCode;
            }
            catch (Exception ex)
            {
                return Fail(output, commandName, ex, stopped);
            }
            finally
            {
                // However the command ended: after each host's own refusal and the command's conclusion, and with
                // each host it reached that asks to be held awake between commands held.
                await services.GetRequiredService<CommandEnd>().EndAsync(commandName).ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Reports the exception that ended a command, and returns the exit code that says what it means.
    /// The one place a failure becomes an exit code, so a command a host serves for another machine
    /// fails exactly as a command typed here does.
    /// </summary>
    /// <param name="output">Where the command writes.</param>
    /// <param name="commandName">The command, which prefixes its failure line.</param>
    /// <param name="exception">What ended it.</param>
    /// <param name="stopped">
    /// The document the command answers with, given the code it exits with and the line it ends on, where it was asked
    /// for one (<see cref="JsonAnswer"/>); <see langword="null"/> where it was not.
    /// </param>
    internal static int Fail(IHarnessOutput output, string commandName, Exception exception, Func<int, string, string>? stopped = null)
    {
        var (exitCode, message, defect) = Meaning(exception);

        Stop(output, commandName, exitCode, message, stopped);

        // A defect's stack trace is there under --verbose, where someone is actually diagnosing it. It is the
        // harness's own report, which repeats the message its failure line said, so it is told as that line is.
        if (defect && output.IsVerbose)
        {
            output.RawError(output.Shown(exception.ToString()));
        }

        return exitCode;
    }

    /// <summary>
    /// What <paramref name="exception"/> means: the code the command exits with, the line it ends on,
    /// and whether it is a defect in this tool.
    /// </summary>
    private static (int ExitCode, string Message, bool Defect) Meaning(Exception exception)
    {
        // A cause both this and the leg executor know, read from the one table they share, so the
        // same missing program means the same thing whether it stopped a command or a leg.
        if (KnownCauses.ExitCodeFor(exception) is { } known)
        {
            return (known, exception.Message, false);
        }

        return exception switch
        {
            // The service already decided what this failure means.
            HarnessException harness => (harness.ExitCode, harness.Message, false),
            ConfigException => (HarnessExit.ConfigInvalid, exception.Message, false),

            // Not CommandFailed: nothing ran to completion, and a caller reading a failure code
            // would report a red verdict for an interrupted run.
            OperationCanceledException => (HarnessExit.Cancelled, "Interrupted before completion.", false),

            // A defect in the harness, not a failure of the thing being asked about. Reported as a
            // message with a defined exit code rather than an unhandled exception, whose exit code
            // would collide with a command's own contract.
            _ => (HarnessExit.InternalError, $"Unexpected {exception.GetType().Name}: {exception.Message}", true),
        };
    }

    /// <summary>
    /// Ends a command that did not succeed: with its document first, as the whole of standard output,
    /// when it was asked for one, and then the line that says why.
    /// </summary>
    private static int Stop(IHarnessOutput output, string commandName, int exitCode, string message, Func<int, string, string>? stopped)
    {
        if (stopped is not null)
        {
            output.Data(stopped(exitCode, message));
        }

        output.Fail(commandName, message);
        return exitCode;
    }

    /// <summary>
    /// Resolves <c>--directory</c> to an absolute path and confirms it exists.
    /// </summary>
    /// <remarks>
    /// Done once, here. Without the existence check, the first service to start a child
    /// process in a missing directory fails with an error that names the wrong cause, and
    /// a relative path would reach every message as a fragment rather than the directory
    /// the user meant.
    /// </remarks>
    private static bool TryResolveDirectory(
        ParseResult parseResult,
        IServiceProvider services,
        out string directory,
        out string problem)
    {
        var requested = parseResult.GetValue(GlobalOptions.Directory) ?? System.Environment.CurrentDirectory;
        directory = string.Empty;
        problem = string.Empty;

        try
        {
            directory = Path.GetFullPath(requested);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"'{requested}' is not a usable directory path: {ex.Message}";
            return false;
        }

        if (!services.GetRequiredService<IFileSystem>().DirectoryExists(directory))
        {
            problem = $"Directory '{directory}' does not exist.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reports what a command's body answered: its document, its lines, and the line it ends on. Asked for its document,
    /// a body that answered without one - a usage error or a refusal it returned before it weighed anything - answers with
    /// the document it registered where it was wired (<see cref="JsonAnswer"/>), so a command asked for one answers with
    /// one on every exit.
    /// </summary>
    /// <param name="output">Where the command writes.</param>
    /// <param name="commandName">The command, which prefixes its lines.</param>
    /// <param name="outcome">What its body answered.</param>
    /// <param name="stopped">
    /// The document the command answers with where it stops, given the code it exits with and the line it ends on, where
    /// it was asked for one; <see langword="null"/> where it was not.
    /// </param>
    private static void Report(IHarnessOutput output, string commandName, CommandOutcome outcome, Func<int, string, string>? stopped)
    {
        if (stopped is not null && outcome.Data.Count == 0)
        {
            output.Data(stopped(outcome.ExitCode, outcome.Message));
        }

        foreach (var text in outcome.Data)
        {
            output.Data(text);
        }

        foreach (var detail in outcome.Details ?? [])
        {
            output.Info(commandName, detail);
        }

        if (!outcome.Succeeded)
        {
            output.Fail(commandName, outcome.Message);
        }
        else if (!outcome.Quiet)
        {
            output.Ok(commandName, outcome.Message);
        }
    }
}
