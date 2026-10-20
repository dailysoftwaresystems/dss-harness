using DssHarness.Core.Application;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The commands of a run, and what they answer from: <c>run-orchestrator</c>, <c>resume-orchestrator</c>,
/// <c>answer-orchestrator</c>, <c>pause-orchestrator</c> and <c>stop-orchestrator</c>. Run, resume and answer are
/// sessions - each claims the run's owner, drives the engine until nothing more can run, writes the report and maps
/// the session's end to its exit code; pause and stop ask of an engine that is running the run, through
/// <c>stop.json</c>, and wait for what they asked.
/// </summary>
public interface IFlowService : IUseCase
{
    /// <summary>Starts a run of a declared workflow: validates it (12), makes the run's name and record inside the repository's one section, claims the owner and runs the session.</summary>
    Task<CommandOutcome> RunAsync(string startDirectory, string definition, string? run, IReadOnlyDictionary<string, string>? inputs, bool json, FlowInterrupts? interrupts = null, CancellationToken cancellationToken = default);

    /// <summary>Goes on with a run: everything that can run, beside any question still open.</summary>
    Task<CommandOutcome> ResumeAsync(string startDirectory, string run, bool json, FlowInterrupts? interrupts = null, CancellationToken cancellationToken = default);

    /// <summary>Answers a question of a run and goes on: handed to the engine where one is running it (<c>inbox.json</c>), else a session of the run's own.</summary>
    Task<CommandOutcome> AnswerAsync(string startDirectory, string run, string answer, string? question, string? text, bool again, bool json, FlowInterrupts? interrupts = null, CancellationToken cancellationToken = default);

    /// <summary>The safe pause, asked of the engine running the run: waits until the pause is complete, names each handoff and each machine step still in progress.</summary>
    Task<CommandOutcome> PauseAsync(string startDirectory, string run, bool all, bool json, CancellationToken cancellationToken = default);

    /// <summary>The hard stop: asks the engine to stop where it stands and waits for it; <paramref name="now"/> ends its process without asking.</summary>
    Task<CommandOutcome> StopAsync(string startDirectory, string run, bool now, bool json, CancellationToken cancellationToken = default);

    /// <summary>Every question the run raised and what became of it, the open ones first, from <c>questions.json</c>.</summary>
    Task<CommandOutcome> QuestionsAsync(string startDirectory, string run, bool json, CancellationToken cancellationToken = default);
}

/// <summary>
/// Counts the two interrupts of a run's own terminal: the first asks the engine for a safe pause and starts no
/// clock - the engine may stay for as long as a build takes - and the second, while the handoffs are being written
/// or once the pause is complete alike, is the hard stop: everything in flight cancelled, the run stopped.
/// </summary>
/// <remarks>
/// The command line binds this to Ctrl+C, SIGTERM and a closed terminal; a front end calls <see cref="Pressed"/>
/// from its own key handling. Tests drive it the same way, so the counting is asserted without a terminal.
/// </remarks>
public sealed class FlowInterrupts
{
    private int _pressed;
    private FlowCoordinator? _coordinator;
    private CancellationTokenSource? _hard;

    /// <summary>How many interrupts have been counted.</summary>
    public int Presses => _pressed;

    /// <summary>Binds the interrupts to the session they count for; called by the service that drives it.</summary>
    internal void BoundTo(FlowCoordinator coordinator, CancellationTokenSource hard)
    {
        _coordinator = coordinator;
        _hard = hard;
    }

    /// <summary>
    /// One interrupt: the first asks the engine for a safe pause; a second and every one after is the hard stop -
    /// the engine stopped where it stands and the session's waiting cancelled, for the drain is bounded.
    /// </summary>
    public void Pressed()
    {
        var count = Interlocked.Increment(ref _pressed);

        if (count == 1)
        {
            _coordinator?.RequestPause("interrupt");
            return;
        }

        _coordinator?.RequestStop();

        try
        {
            _hard?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The session has ended already; its engine stopped with it.
        }
    }
}

/// <summary>Wires the commands of a run over the engine's own services.</summary>
/// <param name="contextLoader">Loads the main checkout's context, whichever tree the command was typed in.</param>
/// <param name="platform">Says which machine this is.</param>
/// <param name="fileSystem">Reads and writes the run's records.</param>
/// <param name="store">Reads and writes the records, strictly.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="sessions">One unfinished run at a time: the test at the start of every session, the run's name and record.</param>
/// <param name="validation">Validates the workflow a run is started of.</param>
/// <param name="runner">The step runner every step of the run is given to.</param>
/// <param name="runnerFacts">What the engine asks of the runners a workflow names.</param>
/// <param name="log">One line for each command the tool ran for the run.</param>
/// <param name="clock">When the run's records are written.</param>
public sealed class FlowService(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IFileSystem fileSystem,
    OrchestrationStore store,
    IProcessIdentity identity,
    FlowSessions sessions,
    IWorkflowValidationService validation,
    IFlowStepRunner runner,
    IFlowRunnerFacts runnerFacts,
    OrchestrationLog log,
    TimeProvider? clock = null) : IFlowService
{
    /// <summary>The command that starts a run.</summary>
    public const string RunCommand = "run-orchestrator";

    /// <summary>The command that goes on with one.</summary>
    public const string ResumeCommand = "resume-orchestrator";

    /// <summary>The command that answers a question of one.</summary>
    public const string AnswerCommand = "answer-orchestrator";

    /// <summary>The command that asks the safe pause of one.</summary>
    public const string PauseCommand = "pause-orchestrator";

    /// <summary>The command that stops one where it stands.</summary>
    public const string StopCommand = "stop-orchestrator";

    /// <summary>How often a running engine reads what another process asked of it.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    /// <summary>How long <c>stop-orchestrator</c> waits for the engine to end before <c>--now</c> is the way.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    /// <summary>How long the waiting commands look again at what the engine has recorded.</summary>
    private static readonly TimeSpan WatchEvery = TimeSpan.FromMilliseconds(200);

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostPlatform _platform = platform;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly OrchestrationStore _store = store;
    private readonly IProcessIdentity _identity = identity;
    private readonly FlowSessions _sessions = sessions;
    private readonly IWorkflowValidationService _validation = validation;
    private readonly IFlowStepRunner _runner = runner;
    private readonly IFlowRunnerFacts _runnerFacts = runnerFacts;
    private readonly OrchestrationLog _log = log;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc/>
    public async Task<CommandOutcome> RunAsync(
        string startDirectory,
        string definition,
        string? run,
        IReadOnlyDictionary<string, string>? inputs,
        bool json,
        FlowInterrupts? interrupts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);

        if (run is not null && OrchestrationRules.NameProblem(run) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);

        // The definition is checked as validate-workflow checks it, and nothing runs of one that does not hold. A
        // workflow not declared of the repository is a refusal naming what is (13); one declared that does not hold
        // is the configuration's own answer (12).
        WorkflowValidationReport report;

        try
        {
            report = await _validation.CheckAsync(startDirectory, definition, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return CommandOutcome.Refused(ex.Message);
        }

        var check = report.Workflows.FirstOrDefault();

        if (check is not { Holds: true, Definition: { } workflow })
        {
            return CommandOutcome.Failed(
                HarnessExit.ConfigInvalid,
                $"workflow '{definition}' is not one this run can start",
                check?.Lines ?? [WorkflowValidationReports.NoneDeclared]);
        }

        // Inside the repository's one section: the test for another unfinished run, the name, the record and the
        // first status. Two starts cannot both pass the test, and two cannot take one name.
        OrchestratorLayout layout;
        FlowRunContext runContext;

        try
        {
            (layout, runContext) = _sessions.Create(context.Layout, workflow, run, inputs);
        }
        catch (HarnessException ex)
        {
            return CommandOutcome.Refused(ex.Message);
        }

        using var records = new FlowRunRecords(_fileSystem, _store, layout, runContext, _clock);
        var owner = new FlowRunOwner(_store, _identity, _clock);
        _ = owner.Claim(layout);

        return await DriveAsync(layout, workflow, runContext, resume: null, interrupts, cancellationToken, records: records).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> ResumeAsync(string startDirectory, string run, bool json, FlowInterrupts? interrupts = null, CancellationToken cancellationToken = default)
    {
        var (outcome, context, layout) = Load(startDirectory, run, ResumeCommand);

        if (outcome is not null)
        {
            return outcome;
        }

        try
        {
            _sessions.Guard(context.Layout, run);
        }
        catch (HarnessException ex)
        {
            return _log.Record(layout, run, ResumeCommand, CommandOutcome.Refused(ex.Message));
        }

        var owner = new FlowRunOwner(_store, _identity, _clock);
        string? reclaimed;

        try
        {
            reclaimed = owner.Claim(layout);
        }
        catch (HarnessException ex)
        {
            return _log.Record(layout, run, ResumeCommand, CommandOutcome.Refused(ex.Message));
        }

        return await TakeUpAsync(layout, ResumeCommand, reclaimed, interrupts, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> AnswerAsync(
        string startDirectory,
        string run,
        string answer,
        string? question,
        string? text,
        bool again,
        bool json,
        FlowInterrupts? interrupts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        if (question is not null && !FlowQuestionIds.IsWellFormed(question))
        {
            return CommandOutcome.Usage($"'{question}' is not a question's id");
        }

        var (outcome, context, layout) = Load(startDirectory, run, AnswerCommand);

        if (outcome is not null)
        {
            return outcome;
        }

        var questions = _store.ReadFlowQuestions(layout) ?? new FlowQuestionsFile(1, run, _clock.GetUtcNow(), []);
        var open = questions.Open;

        // The question the answer names - left out, the one the run has open, which the command shows; with several
        // open, leaving it out is a usage error that lists them. A question named that no longer waits says what
        // became of it and what is open now, so no answer lands on a question nobody saw.
        FlowQuestionEntry asked;

        if (question is { } named)
        {
            if (questions.Of(named) is not { } entry)
            {
                return _log.Record(layout, run, AnswerCommand, CommandOutcome.Refused($"run '{run}' holds no question '{named}'"));
            }

            if (entry.State != "open")
            {
                return _log.Record(
                    layout,
                    run,
                    AnswerCommand,
                    CommandOutcome.Refused($"question '{named}' of run '{run}' is {entry.State}{(open.Count > 0 ? $"; what is open now: {string.Join(", ", open.Select(one => one.Id))}" : string.Empty)}"));
            }

            asked = entry;
        }
        else if (open.Count == 0)
        {
            return _log.Record(layout, run, AnswerCommand, CommandOutcome.Refused($"run '{run}' holds no open question"));
        }
        else if (open.Count > 1)
        {
            return CommandOutcome.Usage(
                $"run '{run}' holds {open.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} open questions; --question names the one this answers",
                [.. open.Select(entry => $"{entry.Id} ({entry.Kind}): {entry.ProblemShown?.Happened ?? entry.Kind}")]);
        }
        else
        {
            asked = open[0];
        }

        if (again)
        {
            return _log.Record(
                layout,
                run,
                AnswerCommand,
                CommandOutcome.Refused("--again changes an answer already given, which raises a plan update; this build takes no plan update yet"));
        }

        // The answer is checked against the question's answers before anything of the run is touched: a usage error,
        // never a run's refusal.
        if (!asked.Options.Any(option => string.Equals(option.Answer, answer, StringComparison.Ordinal)) && answer != "abort")
        {
            return CommandOutcome.Usage(
                $"'{answer}' is not one of the answers '{asked.Id}' takes: {string.Join(", ", asked.Options.Select(option => option.Answer))}");
        }

        var takes = asked.Options.FirstOrDefault(option => string.Equals(option.Answer, answer, StringComparison.Ordinal))?.Text == true;

        if (takes && string.IsNullOrWhiteSpace(text))
        {
            return CommandOutcome.Usage($"'{answer}' is an answer '{asked.Id}' takes with a text; give one");
        }

        if (!takes && !string.IsNullOrWhiteSpace(text))
        {
            return CommandOutcome.Usage($"'{answer}' is an answer '{asked.Id}' takes with no text; give none");
        }

        // Where an engine is running the run, the answer is handed to it and the command returns: it lands in the
        // inbox, the engine takes it within two seconds, and no second engine is started.
        if (_store.ReadOwner(layout) is { } held && _identity.Stands(held.Machine, held.ProcessId, held.Stamp))
        {
            _ = _store.Exclusively(layout, () => { _store.AddToInbox(layout, new FlowInboxAnswer(asked.Id, answer, _clock.GetUtcNow(), takes ? text : null, again)); return false; });

            return _log.Record(
                layout,
                run,
                AnswerCommand,
                CommandOutcome.Ok(
                    $"handed '{answer}' for question '{asked.Id}' to the engine running '{run}'",
                    [$"the engine reads it within {PollEvery.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s and takes it while the question is open"]));
        }

        try
        {
            _sessions.Guard(context.Layout, run);
        }
        catch (HarnessException ex)
        {
            return _log.Record(layout, run, AnswerCommand, CommandOutcome.Refused(ex.Message));
        }

        var owner = new FlowRunOwner(_store, _identity, _clock);
        string? reclaimed;

        try
        {
            reclaimed = owner.Claim(layout);
        }
        catch (HarnessException ex)
        {
            return _log.Record(layout, run, AnswerCommand, CommandOutcome.Refused(ex.Message));
        }

        // The session begins with the answer: the question it names, answered by a person through this command, and
        // everything that can run after it.
        return await TakeUpAsync(layout, AnswerCommand, reclaimed, interrupts, (asked.Id, answer, takes ? text : null), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> PauseAsync(string startDirectory, string run, bool all, bool json, CancellationToken cancellationToken = default)
    {
        var (outcome, _, layout) = Load(startDirectory, run, PauseCommand);

        if (outcome is not null)
        {
            return outcome;
        }

        if (!EngineRuns(layout))
        {
            return _log.Record(layout, run, PauseCommand, CommandOutcome.Refused($"run '{run}' is not being run by an engine: there is nothing to pause"));
        }

        // A pause asked of the engine from any terminal on the machine: written under the store's lock, read by the
        // engine every two seconds, and waited for here until the pause is complete.
        _ = _store.Exclusively(layout, () => { _store.WriteStopFile(layout, new FlowStopFile(1, new FlowStopPause(all, _clock.GetUtcNow()))); return false; });

        while (!cancellationToken.IsCancellationRequested)
        {
            var flow = _store.ReadFlow(layout);

            if (flow?.Pause is { CompletedAt: { } })
            {
                var machine = flow.Steps.Where(step => step.State == "Running").Select(step => step.Instance).ToList();
                var handoffs = flow.Pause.Handoffs;

                return _log.Record(
                    layout,
                    run,
                    PauseCommand,
                    CommandOutcome.Ok(
                        $"run '{run}' is paused{(handoffs.Count == 0 ? string.Empty : $"; {handoffs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} handoff{(handoffs.Count == 1 ? string.Empty : "s")} written")}",
                        [
                            .. handoffs.Select(id => $"handoff: {id}"),
                            .. machine.Count == 0 ? [] : new[]
                            {
                                $"{machine.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} machine step{(machine.Count == 1 ? string.Empty : "s")} still in progress: {string.Join(", ", machine)}; the engine's process ends when they have",
                            },
                        ]));
            }

            if (!EngineRuns(layout))
            {
                var flow2 = _store.ReadFlow(layout);
                return _log.Record(
                    layout,
                    run,
                    PauseCommand,
                    CommandOutcome.Ok(
                        $"run '{run}' is paused",
                        [.. (flow2?.Pause?.Handoffs ?? []).Select(id => $"handoff: {id}")]));
            }

            try
            {
                await Task.Delay(WatchEvery, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        return _log.Record(
            layout,
            run,
            PauseCommand,
            CommandOutcome.Failed(HarnessExit.Cancelled, $"waiting for the pause of '{run}' was cancelled"));
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> StopAsync(string startDirectory, string run, bool now, bool json, CancellationToken cancellationToken = default)
    {
        var (outcome, _, layout) = Load(startDirectory, run, StopCommand);

        if (outcome is not null)
        {
            return outcome;
        }

        var held = _store.ReadOwner(layout);

        if (held is null || !_identity.Stands(held.Machine, held.ProcessId, held.Stamp))
        {
            return _log.Record(layout, run, StopCommand, CommandOutcome.Refused($"run '{run}' is not being run by an engine: there is nothing to stop"));
        }

        if (now)
        {
            // The engine no longer answers: its process ended where it stands, and every child its steps recorded
            // goes with it. What is left is what a crash leaves, less those processes.
            EndProcess(held);

            return _log.Record(
                layout,
                run,
                StopCommand,
                CommandOutcome.Ok(
                    $"ended the engine of run '{run}' (process {held.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}) where it stood",
                    ["what it left is what a crash leaves; resume-orchestrator goes on from the records, and stop-orchestrator run again once an engine holds it"]));
        }

        _ = _store.Exclusively(layout, () => { _store.WriteStopFile(layout, new FlowStopFile(1, Stop: new FlowStopStop(_clock.GetUtcNow()))); return false; });

        var until = _clock.GetUtcNow() + StopWait;

        while (_clock.GetUtcNow() < until)
        {
            if (_store.ReadOwner(layout) is null)
            {
                return _log.Record(
                    layout,
                    run,
                    StopCommand,
                    CommandOutcome.Ok($"run '{run}' is stopped", ["resume-orchestrator goes on with it where it stands"]));
            }

            try
            {
                await Task.Delay(WatchEvery, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        return _log.Record(
            layout,
            run,
            StopCommand,
            CommandOutcome.Refused(
                $"the engine running '{run}' did not stop within {StopWait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s",
                [$"{StopCommand} {run} --now ends its process where it stands"]));
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> QuestionsAsync(string startDirectory, string run, bool json, CancellationToken cancellationToken = default)
    {
        var (outcome, _, layout) = Load(startDirectory, run, "list-orchestrator");

        if (outcome is not null)
        {
            return outcome;
        }

        var questions = _store.ReadFlowQuestions(layout);

        if (questions is null)
        {
            return _log.Record(layout, run, "list-orchestrator", CommandOutcome.Refused(OrchestrationReports.NoOrchestrator(run)));
        }

        return CommandOutcome.Ok($"run '{run}' raised {questions.Questions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} question{(questions.Questions.Count == 1 ? string.Empty : "s")}", [.. QuestionsText.Of(questions)]);
    }

    /// <summary>Loads the run's context and layout, or the refusal that answers for the command.</summary>
    private (CommandOutcome? Outcome, HarnessContext Context, OrchestratorLayout Layout) Load(string startDirectory, string run, string command)
    {
        if (OrchestrationRules.NameProblem(run) is { } shape)
        {
            return (CommandOutcome.Usage(shape), null!, null!);
        }

        var context = MainTree.LoadAsync(_contextLoader, _platform, startDirectory, CancellationToken.None).GetAwaiter().GetResult();
        var layout = OrchestratorLayout.Of(context.Layout, run);

        if (_store.ReadOrchestrator(layout) is null)
        {
            return (_log.Record(layout, run, command, CommandOutcome.Refused(OrchestrationReports.NoOrchestrator(run))), context, layout);
        }

        return (null, context, layout);
    }

    /// <summary>Whether a live engine runs the run.</summary>
    private bool EngineRuns(OrchestratorLayout layout)
        => _store.ReadOwner(layout) is { } held && _identity.Stands(held.Machine, held.ProcessId, held.Stamp);

    /// <summary>Takes up a run another session left, or one a pause quieted, and drives it.</summary>
    private async Task<CommandOutcome> TakeUpAsync(
        OrchestratorLayout layout,
        string command,
        string? reclaimed,
        FlowInterrupts? interrupts,
        (string Question, string Answer, string? Text)? answered = null,
        CancellationToken cancellationToken = default)
    {
        var definition = _store.ReadDefinition(layout)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{layout.DefinitionFile}' is not there, so run '{layout.Name}' holds no definition to run");

        var resume = new FlowResume(_store, layout, _fileSystem).Load();
        var status = _store.ReadWorkflowStatus(layout);
        var runContext = FlowRunContext.Of(
            layout.Name,
            definition,
            resume.File.Inputs,
            resume.File.StartCommit,
            resume.File.Branch,
            status?.StartedAt);

        using var records = new FlowRunRecords(_fileSystem, _store, layout, runContext, _clock);
        var coordinator = new FlowCoordinator(definition, _runner, _runnerFacts, _clock, resume.File.Inputs, records, resume);

        if (answered is { } given)
        {
            if (!coordinator.TryAnswer(given.Question, given.Answer, given.Text, out var problem, "answer-orchestrator"))
            {
                return _log.Record(layout, layout.Name, command, CommandOutcome.Refused(problem!));
            }
        }

        var outcome = await DriveAsync(layout, definition, runContext, resume, interrupts, cancellationToken, coordinator, records, reclaimed, answered is not null ? command : ResumeCommand).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Drives one session of the run to its end - the poller reading what another process asked of the engine beside
    /// it - then writes the report, releases the owner and maps the session's end to its exit code.
    /// </summary>
    private async Task<CommandOutcome> DriveAsync(
        OrchestratorLayout layout,
        WorkflowDefinition definition,
        FlowRunContext runContext,
        FlowResumeState? resume,
        FlowInterrupts? interrupts,
        CancellationToken cancellationToken,
        FlowCoordinator? coordinator = null,
        FlowRunRecords? records = null,
        string? reclaimed = null,
        string command = RunCommand)
    {
        coordinator ??= new FlowCoordinator(definition, _runner, _runnerFacts, _clock, runContext.Inputs, records, resume);
        records ??= new FlowRunRecords(_fileSystem, _store, layout, runContext, _clock);

        using var hard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(hard.Token);
        interrupts?.BoundTo(coordinator, hard);

        var polling = PollAsync(layout, coordinator, poll.Token);
        FlowSessionEnd end;

        try
        {
            end = await coordinator.RunAsync(hard.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The second interrupt is the cancellation of the session's waiting: the run ends by the hard stop the
            // same interrupt asked for, and its records say so. The next session over the coordinator applies the
            // stop and ends this one in order.
            end = await coordinator.RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            poll.Cancel();
            await polling.ConfigureAwait(false);
        }

        var owner = new FlowRunOwner(_store, _identity, _clock);
        var flow = _store.ReadFlow(layout);
        var questions = _store.ReadFlowQuestions(layout);
        var left = FlowReports.Write(_fileSystem, layout, flow!, questions, end.Summary, ExitOf(end), _clock.GetUtcNow());
        owner.Release(layout);

        var exit = ExitOf(end);
        var result = new FlowRunResult(
            end.Status.ToString(),
            layout.Name,
            end.Summary,
            end.OpenQuestions,
            [.. (questions?.Questions ?? []).Where(question => question.State == "answered").Select(question => question.Id)],
            end.PassedOver,
            [.. end.Pause?.Handoffs ?? []],
            left,
            flow?.Spent ?? new FlowBudgetRecord(0, 0, 0),
            exit,
            layout.Directory,
            layout.FlowFile,
            layout.EventsFile,
            layout.QuestionsFile,
            layout.ReportFile);

        var outcome = end.Status switch
        {
            FlowRunStatus.Done => CommandOutcome.Ok(end.Summary, RecordsOf(layout)),
            FlowRunStatus.Failed => new CommandOutcome(HarnessExit.CommandFailed, end.Summary, RecordsOf(layout)),
            FlowRunStatus.Paused when end.Pause is null => new CommandOutcome(
                FlowExit.Paused,
                end.Summary,
                end.OpenQuestions.Count > 0
                    ? [.. RecordsOf(layout), $"answer with {AnswerCommand} {layout.Name} <answer> --question {end.OpenQuestions[0].Id}"]
                    : RecordsOf(layout)),
            _ => new CommandOutcome(HarnessExit.Cancelled, end.Summary, RecordsOf(layout)),
        } with { Facts = result };

        if (reclaimed is not null)
        {
            outcome = outcome with { Details = [.. outcome.Details ?? [], reclaimed] };
        }

        return _log.Record(layout, layout.Name, command, outcome);
    }

    /// <summary>What a session's end exits with.</summary>
    private static int ExitOf(FlowSessionEnd end)
        => end.Status switch
        {
            FlowRunStatus.Done => HarnessExit.Success,
            FlowRunStatus.Failed => HarnessExit.CommandFailed,
            FlowRunStatus.Paused when end.Pause is null => FlowExit.Paused,
            _ => HarnessExit.Cancelled,
        };

    /// <summary>Every record of the run, by its path, as each session's outcome names them.</summary>
    private static IReadOnlyList<string> RecordsOf(OrchestratorLayout layout)
        =>
        [
            $"state: {layout.FlowFile}",
            $"status: {layout.WorkflowStatusFile}",
            $"questions: {layout.QuestionsFile}",
            $"report: {layout.ReportFile}",
            $"events: {layout.EventsFile}",
        ];

    /// <summary>
    /// Reads what another process asked of the engine every two seconds - a pause or a stop in <c>stop.json</c>, an
    /// answer in <c>inbox.json</c> - and hands each to the engine, which alone applies it.
    /// </summary>
    private async Task PollAsync(OrchestratorLayout layout, FlowCoordinator coordinator, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollEvery, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            var asked = _store.Exclusively(layout, () => _store.TakeStopFile(layout));

            if (asked is { Pause: { } pause })
            {
                coordinator.RequestPause("command", pause.All);
            }
            else if (asked is { Stop: _ })
            {
                // The hard stop asked of the engine from another terminal: everything in flight cancelled and ended
                // stopped by the loop, which alone owns the graph, and the session ends with it.
                coordinator.RequestStop();
            }

            if (_store.ReadInbox(layout) is { } inbox)
            {
                foreach (var answer in inbox.Answers)
                {
                    _ = await coordinator.OfferAnswerAsync(answer.Question, answer.Answer, answer.Text, "answer-orchestrator").ConfigureAwait(false);
                    _ = _store.Exclusively(layout, () => { _store.RemoveFromInbox(layout, answer.At); return false; });
                }
            }
        }
    }

    /// <summary>Ends the process an owner names, with all it started: the runtime's own tree kill, for a process that is not this one's child.</summary>
    private static void EndProcess(RunOwner held)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(held.ProcessId);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // It has ended already.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // It is ending, or not this user's to end; the owner's record says what it was.
        }
    }
}

/// <summary>Every question of a run as text, the open ones first: what it asked, and what became of it.</summary>
public static class QuestionsText
{
    /// <summary>The lines of <paramref name="questions"/>, open first.</summary>
    public static IEnumerable<string> Of(FlowQuestionsFile questions)
    {
        foreach (var question in questions.Questions.OrderBy(entry => entry.State != "open").ThenBy(entry => entry.Id, StringComparer.Ordinal))
        {
            yield return $"question {question.Id} ({question.Kind}, {question.State}): {question.ProblemShown?.Happened ?? question.Kind}";

            foreach (var option in question.Options)
            {
                yield return $"  {option.Answer}: {option.Does}";
            }

            foreach (var answer in question.Answers)
            {
                var who = answer.By is { } by
                    ? by.Who == "person"
                        ? by.Through is { } through ? $"a person, through {through}" : "a person"
                        : by.Who == "configuration" ? $"the configuration, {by.Where}" : by.Who
                    : "someone unknown";
                yield return $"  answered '{answer.Answer}' by {who}{(answer.Text is { } given ? $": \"{given}\"" : string.Empty)}";
            }
        }
    }
}
