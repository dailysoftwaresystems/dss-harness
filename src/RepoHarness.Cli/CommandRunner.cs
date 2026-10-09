using System.CommandLine;
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
internal sealed record CommandContext(IServiceProvider Services, ParseResult ParseResult, string Directory)
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
    /// Marks what this flow runs from here on as run for <paramref name="origin"/>: by the host agent before it serves a
    /// request, so nothing that flow runs is taken for something typed here, and with what the machine that asked says of
    /// a command before that command runs.
    /// </summary>
    /// <param name="origin">Who the commands are run for.</param>
    internal static void Serve(CommandOrigin origin) => Origin.Value = origin;

    /// <summary>Wraps a command body into an action the parser can invoke.</summary>
    /// <param name="commandName">The command, which prefixes every line it writes.</param>
    /// <param name="body">What the command does.</param>
    /// <param name="ledger">
    /// For a command that runs legs, its <c>--json</c>. Asked for its ledger as data, it answers with
    /// a ledger on every exit - one with no legs, when it stopped before any leg had a line: a leg
    /// nobody declared, a configuration that does not load, a runner nobody declared. A script
    /// reading standard output has one document to read whatever happened.
    /// </param>
    internal static Func<ParseResult, CancellationToken, Task<int>> Wrap(
        string commandName,
        Func<CommandContext, CancellationToken, Task<CommandOutcome>> body,
        Option<bool>? ledger = null)
        => Wrap(commandName, (context, _, cancellationToken) => body(context, cancellationToken), ledger, beginsRun: false);

    /// <summary>
    /// Wraps the body of a command that keeps runs - build, test, run and check-mutations - into an action the parser
    /// can invoke, beginning its run before anything can refuse it, and handing the body that run.
    /// </summary>
    /// <param name="commandName">The command, which prefixes every line it writes.</param>
    /// <param name="body">What the command does, given the run it began.</param>
    /// <param name="ledger">The command's <c>--json</c>, as <see cref="Wrap(string, Func{CommandContext, CancellationToken, Task{CommandOutcome}}, Option{bool}?)"/> takes it.</param>
    /// <remarks>
    /// The run is named in the command's first line, and as <c>runId</c> in its <c>--json</c> ledger on every exit, and
    /// is the one its records are kept under: a run refused before it had a directory keeps no records, and its id is
    /// then all there is to cite it by.
    /// </remarks>
    internal static Func<ParseResult, CancellationToken, Task<int>> Wrap(
        string commandName,
        Func<CommandContext, RunId, CancellationToken, Task<CommandOutcome>> body,
        Option<bool>? ledger = null)
        => Wrap(commandName, (context, run, cancellationToken) => body(context, run!, cancellationToken), ledger, beginsRun: true);

    /// <summary>Wraps a command body, given its run where it <paramref name="beginsRun"/>, into an action the parser can invoke.</summary>
    private static Func<ParseResult, CancellationToken, Task<int>> Wrap(
        string commandName,
        Func<CommandContext, RunId?, CancellationToken, Task<CommandOutcome>> body,
        Option<bool>? ledger,
        bool beginsRun)
    {
        return async (parseResult, cancellationToken) =>
        {
            var verbose = parseResult.GetValue(GlobalOptions.Verbose);
            var prompting = !parseResult.GetValue(GlobalOptions.NoPrompt);
            await using var services = HarnessServices.Build(verbose, prompting, Origin.Value ?? CommandOrigin.Typed);
            var output = services.GetRequiredService<IHarnessOutput>();
            var answersWithLedger = ledger is not null && parseResult.GetValue(ledger);

            // The document is the whole of standard output from the first line, not from the moment
            // the legs are surveyed: a line written before then would sit in front of it.
            using var document = answersWithLedger ? output.DataOnly() : null;

            var run = beginsRun ? RunId.New() : null;

            if (run is not null)
            {
                output.Info(commandName, $"run {run.Value}");
            }

            try
            {
                if (!TryResolveDirectory(parseResult, services, out var directory, out var problem))
                {
                    return Stop(output, commandName, HarnessExit.UsageError, problem, answersWithLedger, run);
                }

                var context = new CommandContext(services, parseResult, directory);
                var outcome = await body(context, run, cancellationToken).ConfigureAwait(false);

                Report(output, commandName, outcome);
                return outcome.ExitCode;
            }
            catch (Exception ex)
            {
                return Fail(output, commandName, ex, answersWithLedger, run);
            }
            finally
            {
                // However the command ended: after each host's own refusal and the command's conclusion, and with
                // each host it reached that asks to be held awake between commands held.
                await services.GetRequiredService<CommandEnd>().EndAsync(commandName).ConfigureAwait(false);
            }
        };
    }

    /// <summary>
    /// Reports the exception that ended a command, and returns the exit code that says what it means.
    /// The one place a failure becomes an exit code, so a command a host serves for another machine
    /// fails exactly as a command typed here does.
    /// </summary>
    /// <param name="output">Where the command writes.</param>
    /// <param name="commandName">The command, which prefixes its failure line.</param>
    /// <param name="exception">What ended it.</param>
    /// <param name="ledger">Whether the command was asked for its ledger as data, which it then answers with.</param>
    /// <param name="run">The run the command had begun, which its ledger names; <see langword="null"/> where it began none.</param>
    internal static int Fail(IHarnessOutput output, string commandName, Exception exception, bool ledger = false, RunId? run = null)
    {
        var (exitCode, message, defect) = Meaning(exception);

        Stop(output, commandName, exitCode, message, ledger, run);

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
    /// Ends a command that did not succeed: with its ledger first, as the whole of standard output,
    /// when it was asked for one, and then the line that says why.
    /// </summary>
    private static int Stop(IHarnessOutput output, string commandName, int exitCode, string message, bool ledger, RunId? run = null)
    {
        if (ledger)
        {
            output.Data(LedgerReport.Stopped(exitCode, message, output.Shown, run));
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

    private static void Report(IHarnessOutput output, string commandName, CommandOutcome outcome)
    {
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
