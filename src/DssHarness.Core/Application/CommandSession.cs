using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

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
/// an operation begun as a step of a run leaves to that run to await, whatever ended the run.
/// </remarks>
public sealed class CommandSession : IAsyncDisposable
{
    private readonly DssHarnessHost _host;
    private readonly SessionOptions _options;
    private readonly IServiceProvider _services;
    private readonly IHarnessOutput _output;
    private readonly Func<SessionContext, int, string, string>? _stopped;
    private readonly IProcessContext _process;
    private readonly IFileSystem _files;
    private readonly CommandEnd _end;
    private readonly CancellationTokenSource _pause = new();

    /// <summary>The operation's id.</summary>
    public OperationId Id { get; }

    /// <summary>The run this operation began; <see langword="null"/> where its request began none.</summary>
    public RunId? Run { get; }

    internal CommandSession(DssHarnessHost host, SessionOptions options, SessionFrontEnd? frontEnd, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);

        _host = host;
        _options = options;
        _services = services;
        _output = services.GetService(typeof(IHarnessOutput)) as IHarnessOutput
            ?? throw new InvalidOperationException("the operation's services build no output.");
        _process = services.GetService(typeof(IProcessContext)) as IProcessContext
            ?? throw new InvalidOperationException("the operation's services build no reader of the current directory.");
        _files = services.GetService(typeof(IFileSystem)) as IFileSystem
            ?? throw new InvalidOperationException("the operation's services build no file system.");
        _end = services.GetService(typeof(CommandEnd)) as CommandEnd
            ?? throw new InvalidOperationException("the operation's services build no end.");

        Id = OperationId.New();
        Run = options.BeginsRun ? RunId.New() : null;

        var context = new SessionContext(options, Id, Run, _output);
        _stopped = frontEnd?.Stopped;
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
                var resolved = CommandDirectory.Resolve(_options.Directory, _process.CurrentDirectory, _files);

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
    public async ValueTask DisposeAsync()
    {
        _pause.Dispose();

        if (_services is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What the operation leaves when it ends: its own end - unless this operation is a step of a run, whose end the
    /// run awaits - and every end an operation of its own handed to this one, each even where the one before it threw.
    /// </summary>
    private async Task EndAsync()
    {
        if (_options.DefersCommandEnd && _options.Parent is { } outer)
        {
            _host.Defer(outer, OwnEndAsync);
            return;
        }

        await OwnEndAsync().ConfigureAwait(false);
    }

    private async Task OwnEndAsync()
    {
        // However the operation ended: after each host's own refusal and the operation's conclusion, and with each
        // host it reached that asks to be held awake between commands held.
        await _end.EndAsync(_options.Command).ConfigureAwait(false);

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
                _output.Warn(_options.Command, $"an end this operation was handed could not run: {ex.Message}");
            }
        }

        _output.Publish(new CommandEnded(_options.Command));
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
    public T Use<T>()
        where T : class, IUseCase
        => (T)Services.GetService(typeof(T))!;
}
