using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
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
    private readonly CancellationTokenSource _pause = new();

    /// <summary>
    /// Whether this operation's end was handed to the operation it is a step of: that end runs on this operation's
    /// services, so they are that end's to dispose once it has run, and never this session's.
    /// </summary>
    private bool _endHandedOn;

    /// <summary>Set once the services are disposed, by whichever of this session and its handed end disposes them.</summary>
    private int _servicesDisposed;

    /// <summary>The operation's id.</summary>
    public OperationId Id { get; }

    /// <summary>The run this operation began; <see langword="null"/> where its request began none.</summary>
    public RunId? Run { get; }

    internal CommandSession(DssHarnessHost host, SessionOptions options, SessionFrontEnd? frontEnd, IServiceProvider services)
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

        Id = OperationId.New();
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
    /// <returns>How the operation ended.</returns>
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

        CommandCompletion completion;

        try
        {
            try
            {
                var resolved = CommandDirectory.Resolve(
                    _options.Directory,
                    _services.GetRequiredService<IProcessContext>().CurrentDirectory,
                    _services.GetRequiredService<IFileSystem>());

                if (!resolved.Holds)
                {
                    completion = Stop(HarnessExit.UsageError, resolved.Problem!);
                }
                else
                {
                    var scope = new CommandScope(_services, resolved.Directory!, Run, _pause.Token);
                    var outcome = await body(scope, cancellationToken).ConfigureAwait(false);

                    // Its body answers with its document exactly where a stop would: the stopped form read once, here.
                    var stopped = _stopped is not null && outcome.Data.Count == 0 ? _stopped(Context(), outcome.ExitCode, outcome.Message) : null;
                    _output.Publish(new CommandReported(_options.Command, outcome, stopped));

                    completion = new(outcome.ExitCode, outcome, Run);
                }
            }
            catch (Exception ex)
            {
                completion = Fail(ex);
            }
        }
        finally
        {
            // However the operation ended - its body returning, an exception, a cancellation - what it leaves is
            // said, and its end is awaited or handed to the operation it is a step of.
            await EndAsync().ConfigureAwait(false);
        }

        return completion;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Where this operation's end was handed to the operation it is a step of, that end takes what was handed to this
    /// one and disposes its services once it has run on them, so nothing of either is touched here. Otherwise an end
    /// handed to this operation that its own end never took - one handed to an operation never run - runs here, so none
    /// is lost, and its services are disposed.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _pause.Dispose();

        if (_endHandedOn)
        {
            return;
        }

        await RunHandedEndsAsync().ConfigureAwait(false);
        await DisposeServicesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses what a session would otherwise ignore or cannot honour, before any service is built for it: a step's end
    /// handed to no operation, a document with no stopped form to answer a stop with, and the options and parts of a
    /// front end a later slice gives their meaning, which this build would otherwise run without a word.
    /// </summary>
    /// <param name="options">What the operation is asked and how it runs.</param>
    /// <param name="frontEnd">What the front end supplies.</param>
    /// <exception cref="InvalidOperationException">The options contradict themselves or the front end.</exception>
    /// <exception cref="NotSupportedException">An option or a part of the front end this build does not honour yet.</exception>
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

        if (options.PausesOnInterrupt)
        {
            throw new NotSupportedException(
                $"{nameof(SessionOptions.PausesOnInterrupt)} is honoured by the run verbs of an orchestrator run, which this build does not have yet: "
                + "every interrupt still cancels.");
        }

        if (options.ApartFromTerminal)
        {
            throw new NotSupportedException(
                $"{nameof(SessionOptions.ApartFromTerminal)} is honoured once a process can be started apart from the terminal "
                + $"({nameof(Processes.ProcessRequest)}), which this build cannot do yet: what an operation starts is in reach of an interrupt.");
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
    private async Task EndAsync()
    {
        if (_options.DefersCommandEnd && _host.Defer(_options.Parent!.Value, HandedEndAsync))
        {
            _endHandedOn = true;
            return;
        }

        await OwnEndAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// This operation's end, as the operation it is a step of runs it: on this operation's services, which it then
    /// disposes - they are kept until now for it.
    /// </summary>
    private async Task HandedEndAsync()
    {
        try
        {
            await OwnEndAsync().ConfigureAwait(false);
        }
        finally
        {
            await DisposeServicesAsync().ConfigureAwait(false);
        }
    }

    private async Task OwnEndAsync()
    {
        try
        {
            // However the operation ended: after each host's own refusal and the operation's conclusion, and with each
            // host it reached that asks to be held awake between commands held. Resolved here, inside the guard, so an
            // end that cannot even be built is said as one that could not run, and the ends handed to it still run.
            await _services.GetRequiredService<CommandEnd>().EndAsync(_options.Command).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Warn("this operation's own end could not run", ex);
        }

        await RunHandedEndsAsync().ConfigureAwait(false);
        _output.Publish(new CommandEnded(_options.Command));
    }

    /// <summary>Runs every end an operation of its own handed to this one, in the order they were handed, each even where the one before it threw.</summary>
    private async Task RunHandedEndsAsync()
    {
        foreach (var deferred in _host.TakeDeferred(Id))
        {
            try
            {
                await deferred().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An end that throws is said as a warning and changes no exit code: the operation is over, and one
                // unfinished end never stops the rest from running.
                Warn("an end this operation was handed could not run", ex);
            }
        }
    }

    /// <summary>
    /// Says an end that could not run, as what ended it means (<see cref="KnownCauses.Meaning"/>): a defect's trace there
    /// under verbose, where someone is actually diagnosing it, as a command's own failure shows it.
    /// </summary>
    private void Warn(string what, Exception exception)
    {
        var meaning = KnownCauses.Meaning(exception);

        _output.Warn(_options.Command, $"{what}: {meaning.Message}");

        if (meaning.Defect && _output.IsVerbose)
        {
            _output.RawError(_output.Shown(exception.ToString()));
        }
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
    private CommandCompletion Stop(int exitCode, string message)
    {
        var document = _stopped is not null ? _stopped(Context(), exitCode, message) : null;
        _output.Publish(new CommandFailed(_options.Command, exitCode, message, Defect: false, StoppedDocument: document));
        return new(exitCode, null, Run);
    }

    /// <summary>
    /// Reports the exception that ended the operation: what it means, read from the one table that owns the answer,
    /// and the defect's trace there under verbose where someone is actually diagnosing it.
    /// </summary>
    private CommandCompletion Fail(Exception exception)
    {
        var meaning = KnownCauses.Meaning(exception);
        var document = _stopped is not null ? _stopped(Context(), meaning.ExitCode, meaning.Message) : null;
        var trace = meaning.Defect ? _output.Shown(exception.ToString()) : null;

        _output.Publish(new CommandFailed(_options.Command, meaning.ExitCode, meaning.Message, meaning.Defect, trace, document));
        return new(meaning.ExitCode, null, Run);
    }

    private SessionContext Context() => new(_options, Id, Run, _output);
}

/// <summary>What a command body receives: its services, its directory, its run, and the pause a safe one is asked by.</summary>
/// <param name="services">The services built for this operation.</param>
/// <param name="directory">The directory to act on: absolute, and already confirmed to exist.</param>
/// <param name="run">The run this operation began; <see langword="null"/> where it began none.</param>
/// <param name="pauseRequested">Cancelled by the first interrupt, where the operation pauses safely; the body's own token is cancelled by the second.</param>
public sealed class CommandScope(IServiceProvider services, string directory, RunId? run, CancellationToken pauseRequested)
{
    /// <summary>The services built for this operation.</summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>The directory to act on: absolute, and already confirmed to exist.</summary>
    public string Directory { get; } = directory;

    /// <summary>The run this operation began; <see langword="null"/> where it began none.</summary>
    public RunId? Run { get; } = run;

    /// <summary>Cancelled by the first interrupt, where the operation pauses safely; the body's own token is cancelled by the second.</summary>
    public CancellationToken PauseRequested { get; } = pauseRequested;

    /// <summary>Resolves a use case of this tool.</summary>
    /// <typeparam name="T">The use case.</typeparam>
    /// <returns>The use case, built for this operation.</returns>
    /// <exception cref="InvalidOperationException">The operation's services build no such use case: a registration missing, named.</exception>
    public T Use<T>()
        where T : class, IUseCase
        => Services.GetRequiredService<T>();
}
