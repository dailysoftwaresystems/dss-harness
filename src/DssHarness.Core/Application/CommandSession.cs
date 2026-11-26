using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Json;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Waiting;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Core.Application;

/// <summary>How an operation ended: the code it exits with, what its body answered where it answered, and the run it began where it began one.</summary>
/// <param name="ExitCode">The code it exits with.</param>
/// <param name="Outcome">What its body answered; <see langword="null"/> where nothing did.</param>
/// <param name="Run">The run it began; <see langword="null"/> where it began none.</param>
public sealed record CommandCompletion(int ExitCode, CommandOutcome? Outcome, RunId? Run);

/// <summary>
/// One operation of this tool, from its first line to its end: the lifecycle every command shares, in the one place
/// that owns it.
/// </summary>
/// <remarks>
/// The order inside <see cref="RunAsync"/> is the order the command line has always run in, and it is load-bearing:
/// the document scope opens before anything is said, so the document is the whole of standard output from the first
/// line; the run is named before anything could refuse it, so a run refused before it had a directory keeps its id to
/// be cited by; and what the operation leaves on the hosts it reached is said however it ended, in its own end - which
/// an operation begun as a step of a run leaves to that run to await, whatever ended the run, together with the
/// services that end runs on.
/// </remarks>
public sealed class CommandSession : IAsyncDisposable
{
    private readonly DssHarnessHost _host;
    private readonly SessionOptions _options;
    private readonly IServiceProvider _services;
    private readonly IHarnessOutput _output;
    private readonly Func<SessionContext, int, string, string>? _stopped;

    /// <summary>
    /// Whether this operation's end was handed to the operation it is a step of: that end runs on this operation's
    /// services, so they are that end's to dispose once it has run, and never this session's.
    /// </summary>
    private bool _endHandedOn;

    /// <summary>Whether the operation's end met a defect, once it has run; <see langword="null"/> until it has.</summary>
    private bool? _defective;

    /// <summary>
    /// Whether the end ran before the operation said how it ended - where a document is asked for - and the event that
    /// says it ended is still to be published, after that.
    /// </summary>
    private bool _endedUnsaid;

    /// <summary>Set once the services are disposed, by whichever of this session and its handed end disposes them.</summary>
    private int _servicesDisposed;

    /// <summary>The operation's output logs, where its front end asked for them; <see langword="null"/> where it asked for none.</summary>
    private FlowOutputLogs? _logs;

    /// <summary>Whether writing the operation's output logs met a defect of this tool, which the operation then ends with.</summary>
    private bool _logsDefect;

    /// <summary>The operation's id.</summary>
    public OperationId Id { get; }

    /// <summary>The run this operation began; <see langword="null"/> where its request began none.</summary>
    public RunId? Run { get; }

    internal CommandSession(DssHarnessHost host, SessionOptions options, SessionFrontEnd? frontEnd, IServiceProvider services, OperationId id)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);

        RequireHonoured(options, frontEnd);

        _host = host;
        _options = options;
        _services = services;

        // The one service resolved as the session begins, since everything it says goes through it. What it is built
        // from asks nothing of a machine this build cannot run on (HomeShorthand), so even that machine is told why it
        // was refused; everything else is resolved inside the lifecycle, where a failure to build it is reported.
        _output = services.GetRequiredService<IHarnessOutput>();
        _stopped = frontEnd?.Stopped;

        Id = id;
        Run = options.BeginsRun ? RunId.New() : null;

        // Open to the ends its own steps hand it from here until its own end takes them.
        host.Open(Id);
    }

    /// <summary>
    /// Runs the operation's body inside its lifecycle: the document scope, the run's first line, the directory, the
    /// body's answer, and what the operation leaves when it ends - however it ends.
    /// </summary>
    /// <param name="body">What the operation does, given its scope.</param>
    /// <param name="cancellationToken">What cancels the body.</param>
    /// <returns>
    /// How the operation ended: with the code a defect has where one of its ends met a defect of this tool, whatever its
    /// body answered, since the failure line it was said with would otherwise sit beside a code that says all went well.
    /// </returns>
    public async Task<CommandCompletion> RunAsync(Func<CommandScope, CancellationToken, Task<CommandOutcome>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        _output.Publish(new CommandBegan(_options.Command));

        // The document is the whole of standard output from the first line, not from the moment the work is
        // surveyed: a line written before then would sit in front of it.
        using var document = _options.Document != DocumentKind.None ? _output.DataOnly() : null;

        if (Run is not null)
        {
            _output.Publish(new RunBegan(_options.Command, Run));
        }

        CommandCompletion? completion = null;

        try
        {
            try
            {
                // The current directory is read only where none was asked for, or one relative to it: a shell standing in a
                // directory since removed can still name another.
                var resolved = CommandDirectory.Resolve(
                    _options.Directory,
                    () => _services.GetRequiredService<IProcessContext>().CurrentDirectory,
                    _services.GetRequiredService<IFileSystem>());

                // The output logs' path is held before anything is weighed: one the zip cannot be written at is refused,
                // nothing started; one that holds is the zip's however the operation then ends, a directory refused below
                // among its ends.
                if (_options.OutputLogs is { } asked)
                {
                    _logs = _services.GetRequiredService<FlowOutputLogs>();

                    if (await _logs.CheckAsync(asked.Path, asked.CommandLine, resolved.Directory, cancellationToken).ConfigureAwait(false) is { } unusable)
                    {
                        completion = await StopAsync(HarnessExit.Refused, unusable).ConfigureAwait(false);
                    }
                }

                if (completion is not null)
                {
                    // Refused already: the output logs' path is no place a zip can be written.
                }
                else if (!resolved.Holds)
                {
                    completion = await StopAsync(HarnessExit.UsageError, resolved.Problem!).ConfigureAwait(false);
                }
                else
                {
                    var scope = new CommandScope(_services, resolved.Directory!, Run);

                    // A commit a line names is shown as git abbreviates it in the repository the operation works in, and a
                    // wait deep below the body - a machine-wide file's update, a child's end - is said as the body's lines are.
                    using var commits = CommitIds.Use(Commits(resolved.Directory!));
                    using var telling = Waits.TellingTo(told => _output.Info(_options.Command, told.Line()));
                    var outcome = await body(scope, cancellationToken).ConfigureAwait(false);

                    if (await EndedDefectiveAsync().ConfigureAwait(false))
                    {
                        outcome = DefectiveEnd(outcome);
                    }

                    // Its body answers with its document exactly where a stop would: the stopped form read once, here. Either
                    // says what the operation did that every document says.
                    var answered = outcome;
                    var reported = (CommandReported)await ReportAsync(
                        () => new CommandReported(
                            _options.Command,
                            answered.Data.Count == 0 ? answered : answered with { Data = [.. answered.Data.Select(Documented)] },
                            _stopped is not null && answered.Data.Count == 0 ? Documented(_stopped(Context(), answered.ExitCode, answered.Message)) : null),
                        answered.ExitCode,
                        answered.Message).ConfigureAwait(false);

                    completion = new(reported.Outcome.ExitCode, reported.Outcome, Run);
                }
            }
            catch (Exception ex)
            {
                completion = await FailAsync(ex).ConfigureAwait(false);
            }
        }
        finally
        {
            // However the operation ended - its body returning, an exception, a cancellation - what it leaves is
            // said, and its end is awaited or handed to the operation it is a step of, once.
            if (_defective is null)
            {
                _defective = await EndAsync().ConfigureAwait(false);
            }
            else if (_endedUnsaid)
            {
                // The end ran first, for the document's code; that it ended is said last, as it always is.
                _output.Publish(new CommandEnded(_options.Command));
            }
        }

        return _defective is true || _logsDefect ? completion with { ExitCode = HarnessExit.InternalError } : completion;
    }

    /// <summary>
    /// Where a document is asked for, runs the end now - once - and says whether it met a defect: a document says the code
    /// the process exits with, and nothing can be taken back once written, so a defect the end meets is the document's
    /// code too. Where none is asked for, the end runs after the operation has said how it ended, and nothing is said here.
    /// </summary>
    private async Task<bool> EndedDefectiveAsync()
    {
        if (_options.Document == DocumentKind.None)
        {
            return false;
        }

        if (_defective is null)
        {
            _defective = await EndAsync(sayEnded: false).ConfigureAwait(false);
            _endedUnsaid = !_endHandedOn;
        }

        return _defective is true;
    }

    /// <summary>
    /// <paramref name="outcome"/> as an operation whose end met a defect answers it: the code a defect has, in the outcome
    /// and in each document it carries that says a code - its other members as the body gave them, the failure itself
    /// already said where the end met it.
    /// </summary>
    private static CommandOutcome DefectiveEnd(CommandOutcome outcome)
        => outcome with
        {
            ExitCode = HarnessExit.InternalError,
            Data = [.. outcome.Data.Select(Recoded)],
        };

    /// <summary>
    /// The ids this operation shows commits at: git's own abbreviation in the repository <paramref name="directory"/> is
    /// in, asked once, the first time a commit is shown; whole where the operation's services hold no git, or git cannot
    /// say.
    /// </summary>
    /// <param name="directory">The directory the operation works in.</param>
    private CommitIds Commits(string directory)
    {
        var git = _services.GetService<IGitClient>();

        return new CommitIds(() => git is null ? null : Abbreviation(git, directory));
    }

    /// <summary>
    /// How many characters git's own abbreviation of HEAD takes in the repository <paramref name="directory"/> is in, or
    /// <see langword="null"/> where there is none, HEAD names no commit yet, or git could not be asked: a commit is then
    /// shown whole, and nothing the operation does waits on why.
    /// </summary>
    private static int? Abbreviation(IGitClient git, string directory)
    {
        try
        {
            var asked = git.RunAsync(directory, ["rev-parse", "--short", "--verify", "--quiet", "--end-of-options", "HEAD^{commit}"])
                .GetAwaiter()
                .GetResult();

            return asked.Succeeded && asked.StandardOutput.Trim() is { Length: > 0 } shown ? shown.Length : null;
        }
        catch (Exception ex) when (ex is ProgramStartException or HarnessException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The event an operation ends with, its code and every document it carries saying the code a defect has.</summary>
    private static RunEvent Recoded(RunEvent told)
        => told switch
        {
            CommandReported reported => reported with
            {
                Outcome = DefectiveEnd(reported.Outcome),
                Stopped = reported.Stopped is { } stopped ? Recoded(stopped) : null,
            },
            CommandFailed failed => failed with
            {
                ExitCode = HarnessExit.InternalError,
                StoppedDocument = failed.StoppedDocument is { } document ? Recoded(document) : null,
            },
            _ => told,
        };

    /// <summary>A document whose root says an <c>exitCode</c>, saying the code a defect has; any other, as it was.</summary>
    private static string Recoded(string document)
    {
        try
        {
            if (JsonDepth.ParseNode(document) is JsonObject root && root.ContainsKey("exitCode"))
            {
                root["exitCode"] = HarnessExit.InternalError;
                return root.ToJsonString(ReportJson.Options);
            }
        }
        catch (JsonException)
        {
            // Not a JSON document: it says no code to change.
        }

        return document;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Where this operation's end was handed to the operation it is a step of, that end takes what was handed to this
    /// one and disposes its services once it has run on them, so nothing of either is touched here. Otherwise an end
    /// handed to this operation that its own end never took - one handed to an operation never run - runs here, so none
    /// is lost, and its services are disposed. What such an end meets is said as an end met in a run says it, a defect
    /// as a failure; an operation never run has no exit code for it to change.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_endHandedOn)
        {
            return;
        }

        await RunHandedEndsAsync().ConfigureAwait(false);
        await DisposeServicesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses what a session would otherwise ignore or cannot honour, before any service is built for it: a step's end
    /// handed to no operation, a document with no stopped form to answer a stop with, a stopped form for an operation
    /// that answers with no document, and a front end's own output or prompt, which this build does not honour yet and
    /// would otherwise run without a word.
    /// </summary>
    /// <param name="options">What the operation is asked and how it runs.</param>
    /// <param name="frontEnd">What the front end supplies.</param>
    /// <exception cref="InvalidOperationException">The options contradict themselves or the front end.</exception>
    /// <exception cref="NotSupportedException">The front end supplies an output or a prompt of its own, which this build does not honour yet.</exception>
    internal static void RequireHonoured(SessionOptions options, SessionFrontEnd? frontEnd)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.DefersCommandEnd && options.Parent is null)
        {
            throw new InvalidOperationException(
                $"'{options.Command}' leaves its end to the operation it is a step of, and names none ({nameof(SessionOptions.Parent)}): nobody would ever run it.");
        }

        if (options.Document == DocumentKind.Own && frontEnd?.Stopped is null)
        {
            throw new InvalidOperationException(
                $"'{options.Command}' answers with a document of its own, and its front end supplies no stopped form ({nameof(SessionFrontEnd.Stopped)}) to answer a stop with.");
        }

        if (options.Document == DocumentKind.None && frontEnd?.Stopped is not null)
        {
            throw new InvalidOperationException(
                $"'{options.Command}' answers with no document ({nameof(DocumentKind)}.{nameof(DocumentKind.None)}), and its front end supplies a stopped form "
                + $"({nameof(SessionFrontEnd.Stopped)}) it would answer a stop with all the same: a document nobody asked for.");
        }

        if (options.OutputLogs is not null && !FlowOutputLogs.Commands.Contains(options.Command, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{options.Command}' writes no output logs ({nameof(SessionOptions.OutputLogs)}): {string.Join(", ", FlowOutputLogs.Commands)} alone write them.");
        }

        if (frontEnd?.Output is not null || frontEnd?.Prompt is not null)
        {
            throw new NotSupportedException(
                $"A front end's own {(frontEnd.Output is not null ? nameof(SessionFrontEnd.Output) : nameof(SessionFrontEnd.Prompt))} is honoured once a "
                + "front end other than the command line builds an operation's services, which this build does not have yet: "
                + "an operation writes and asks through the services its host builds.");
        }
    }

    /// <summary>
    /// What the operation leaves when it ends: its own end - unless this operation is a step of a run, whose end the
    /// run awaits, on this operation's services - and every end an operation of its own handed to this one, each even
    /// where the one before it threw. A step whose run has already taken its ends runs its own at once, since nothing
    /// else ever would.
    /// </summary>
    /// <returns>Whether an end this operation ran met a defect of this tool, which the operation then ends with.</returns>
    /// <param name="sayEnded">Whether the event that says the operation ended is published when its own end has run.</param>
    private async Task<bool> EndAsync(bool sayEnded = true)
    {
        if (_options.DefersCommandEnd && _host.Defer(_options.Parent!.Value, HandedEndAsync))
        {
            _endHandedOn = true;
            return false;
        }

        return await OwnEndAsync(sayEnded).ConfigureAwait(false);
    }

    /// <summary>
    /// This operation's end, as the operation it is a step of runs it: on this operation's services, which it then
    /// disposes - they are kept until now for it. A defect it met is said on this operation's own output and ends the
    /// operation it was handed to as a defect ends one (<see cref="DefectSaid"/>).
    /// </summary>
    private async Task HandedEndAsync()
    {
        try
        {
            if (await OwnEndAsync().ConfigureAwait(false))
            {
                throw new DefectSaid(_options.Command);
            }
        }
        finally
        {
            await DisposeServicesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>This operation's own end, then every end handed to it, then the event that says it ended.</summary>
    /// <param name="sayEnded">Whether the event that says it ended is published here, or left to the one who ran it early.</param>
    /// <returns>Whether any of them met a defect of this tool.</returns>
    private async Task<bool> OwnEndAsync(bool sayEnded = true)
    {
        var defective = false;

        try
        {
            // However the operation ended: after each host's own refusal and the operation's conclusion, and with each
            // host it reached that asks to be held awake between commands held. Resolved here, inside the guard, so an
            // end that cannot even be built is said as one that could not run, and the ends handed to it still run.
            await _services.GetRequiredService<CommandEnd>().EndAsync(_options.Command).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            defective = Said("this operation's own end could not run", ex);
        }

        defective |= await RunHandedEndsAsync().ConfigureAwait(false);

        if (sayEnded)
        {
            _output.Publish(new CommandEnded(_options.Command));
        }

        return defective;
    }

    /// <summary>Runs every end an operation of its own handed to this one, in the order they were handed, each even where the one before it threw.</summary>
    /// <returns>Whether any of them met a defect of this tool.</returns>
    private async Task<bool> RunHandedEndsAsync()
    {
        var defective = false;

        foreach (var deferred in _host.TakeDeferred(Id))
        {
            try
            {
                await deferred().ConfigureAwait(false);
            }
            catch (DefectSaid)
            {
                // Said already, on the output of the operation whose end it was.
                defective = true;
            }
            catch (Exception ex)
            {
                // One unfinished end never stops the rest from running.
                defective |= Said("an end this operation was handed could not run", ex);
            }
        }

        return defective;
    }

    /// <summary>
    /// Says each failure an end met, as what it means (<see cref="KnownCauses.Meaning"/>): one an end is expected to meet
    /// as a warning that changes no exit code, since the operation's work is done - a host or a stream gone
    /// (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>), or a cause this tool already names, a
    /// refusal it worded, a program that would not start and a cancellation among them; and a defect of this tool as a
    /// failure, its trace under verbose where someone is actually diagnosing it, as a command's own failure shows it. The
    /// failures of several hosts at once are said one by one.
    /// </summary>
    /// <param name="what">Which end could not run, as the line begins.</param>
    /// <param name="exception">What it threw.</param>
    /// <returns>Whether any of it was a defect, which the operation then ends with.</returns>
    private bool Said(string what, Exception exception)
    {
        var defective = false;

        foreach (var failure in exception is AggregateException several ? several.Flatten().InnerExceptions : [exception])
        {
            var meaning = KnownCauses.Meaning(failure);

            if (failure is IOException or UnauthorizedAccessException || !meaning.Defect)
            {
                _output.Warn(_options.Command, $"{what}: {meaning.Message}");
                continue;
            }

            defective = true;
            _output.Fail(_options.Command, $"{what}: {meaning.Message}");

            if (_output.IsVerbose)
            {
                _output.RawError(_output.Shown(failure.ToString()));
            }
        }

        return defective;
    }

    private async ValueTask DisposeServicesAsync()
    {
        if (Interlocked.Exchange(ref _servicesDisposed, 1) != 0)
        {
            return;
        }

        switch (_services)
        {
            case IAsyncDisposable asynchronous:
                await asynchronous.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <summary>
    /// Ends the operation that did not succeed: with its document first, as the whole of standard output, when it was
    /// asked for one, and then the line that says why.
    /// </summary>
    private async Task<CommandCompletion> StopAsync(int exitCode, string message)
    {
        var said = await EndedDefectiveAsync().ConfigureAwait(false) ? HarnessExit.InternalError : exitCode;

        _ = await ReportAsync(
            () => new CommandFailed(_options.Command, exitCode, message, Defect: false, StoppedDocument: _stopped is not null ? Documented(_stopped(Context(), said, message)) : null),
            said,
            message).ConfigureAwait(false);

        return new(exitCode, null, Run);
    }

    /// <summary>
    /// Reports the exception that ended the operation: what it means, read from the one table that owns the answer,
    /// and the defect's trace there under verbose where someone is actually diagnosing it.
    /// </summary>
    private async Task<CommandCompletion> FailAsync(Exception exception)
    {
        var meaning = KnownCauses.Meaning(exception);
        var said = await EndedDefectiveAsync().ConfigureAwait(false) ? HarnessExit.InternalError : meaning.ExitCode;
        var trace = meaning.Defect ? _output.Shown(exception.ToString()) : null;

        _ = await ReportAsync(
            () => new CommandFailed(_options.Command, meaning.ExitCode, meaning.Message, meaning.Defect, trace, _stopped is not null ? Documented(_stopped(Context(), said, meaning.Message)) : null),
            said,
            meaning.Message).ConfigureAwait(false);

        return new(meaning.ExitCode, null, Run);
    }

    /// <summary>
    /// Publishes the event the operation ends with, as <paramref name="report"/> builds it - every document it carries saying
    /// what every document says - and, where the operation's output logs were asked for and their path held, writes them
    /// first, with that event and the line that names them kept as they are about to be written, so the zip holds the
    /// operation's output as it ends: the zip named in its documents, and in the last of the lines that name its records -
    /// a line of its own before the line it ends on, where it stopped before its body answered. Where the zip could not be
    /// written, that is said in its place - a cause this tool knows as a warning, the code standing as a run's log stands
    /// where it could not take a line; a defect as a failure, which the operation ends with - and the report is built
    /// again naming none.
    /// </summary>
    /// <param name="report">Builds the event, as the operation's facts stand when it is called.</param>
    /// <param name="exitCode">The code the event says the operation exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    /// <returns>The event published.</returns>
    private async Task<RunEvent> ReportAsync(Func<RunEvent> report, int exitCode, string summary)
    {
        if (_logs is not { HasAny: true } logs)
        {
            var told = report();
            _output.Publish(told);
            return told;
        }

        // Named as the last of the lines that name the operation's records, where its report has any, and else in a line of
        // its own before the line it ends on.
        IReadOnlyList<RunEvent> named = report() is CommandReported reported
            ? [reported with { Outcome = reported.Outcome with { Details = [.. reported.Outcome.Details ?? [], logs.Line] } }]
            : [new TextLine(LineKind.Info, _options.Command, logs.Line), report()];

        try
        {
            // Apart from the operation's own token, as its end is: an interrupted command still leaves its zip.
            await logs.WriteAsync(_options.Command, exitCode, summary, named, _options.Document != DocumentKind.None, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logs.Unnamed();
            _logsDefect = Said($"the output logs '{logs.Target}' could not be written", ex);

            var unnamed = _logsDefect ? Recoded(report()) : report();
            _output.Publish(unnamed);
            return unnamed;
        }

        foreach (var told in named)
        {
            _output.Publish(told);
        }

        _output.Publish(new RecordWritten(FlowOutputLogs.Record, logs.Target!));
        return named[^1];
    }

    private SessionContext Context() => new(_options, Id, Run, _output);

    /// <summary>
    /// <paramref name="document"/> as the operation answers with it: with what it did that every document it answers with
    /// says (<see cref="IDocumentFacts"/>) written into its root where there is any - its own document's and the stopped
    /// form's alike, however the operation ended, so no command answers without it - and exactly as written otherwise: where
    /// the operation answers with no document, where it did nothing a document says, and where the root is no object, which
    /// has no member to hold it.
    /// </summary>
    /// <param name="document">A document the operation answers with, as its command wrote it.</param>
    private string Documented(string document)
    {
        if (_options.Document == DocumentKind.None)
        {
            return document;
        }

        // Asked of the operation's own services, which hold what it alone did; a provider that registers none says nothing.
        var facts = (_services.GetService(typeof(IEnumerable<IDocumentFacts>)) as IEnumerable<IDocumentFacts> ?? []).Where(fact => fact.HasAny).ToList();

        if (facts.Count == 0)
        {
            return document;
        }

        JsonObject root;

        try
        {
            if (JsonDepth.ParseNode(document) is not JsonObject parsed)
            {
                return document;
            }

            root = parsed;
        }
        catch (JsonException)
        {
            return document;
        }

        foreach (var fact in facts)
        {
            fact.WriteInto(root);
        }

        return root.ToJsonString(ReportJson.Options);
    }

    /// <summary>
    /// A defect an operation's handed end met, said on that operation's own output, carried to the operation it was
    /// handed to: that one ends as a defect ends one, and says nothing of it twice.
    /// </summary>
    /// <param name="command">The operation whose end it was.</param>
    private sealed class DefectSaid(string command)
        : Exception($"'{command}' met a defect of this tool at its end, said in its own output.");
}

/// <summary>
/// What a command body receives: its services, its directory and its run. A safe pause is no session's to ask: the run
/// verbs count their own interrupts (<c>FlowTerminal</c>).
/// </summary>
/// <param name="services">The services built for this operation.</param>
/// <param name="directory">The directory to act on: absolute, and already confirmed to exist.</param>
/// <param name="run">The run this operation began; <see langword="null"/> where it began none.</param>
public sealed class CommandScope(IServiceProvider services, string directory, RunId? run)
{
    /// <summary>The services built for this operation.</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>The directory to act on: absolute, and already confirmed to exist.</summary>
    public string Directory { get; } = directory;

    /// <summary>The run this operation began; <see langword="null"/> where it began none.</summary>
    public RunId? Run { get; } = run;

    /// <summary>Resolves a use case of this tool.</summary>
    /// <typeparam name="T">The use case.</typeparam>
    /// <returns>The use case, built for this operation.</returns>
    /// <exception cref="InvalidOperationException">The operation's services build no such use case: a registration missing, named.</exception>
    public T Use<T>()
        where T : class, IUseCase
        => Services.GetRequiredService<T>();
}
