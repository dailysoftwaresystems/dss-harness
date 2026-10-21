using System.Globalization;
using DssHarness.Core.Application;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The commands of a run, and what they answer from: <c>run-orchestrator</c>, <c>resume-orchestrator</c>,
/// <c>answer-orchestrator</c>, <c>pause-orchestrator</c> and <c>stop-orchestrator</c>. Run, resume and answer are
/// sessions - each claims the run's owner, drives the engine until nothing more can run, writes the report and maps
/// the session's end to its exit code; pause and stop ask of an engine that is running the run, through
/// <c>stop.json</c>, and wait for what they asked. Every one names each record it wrote or took, in its text and in its
/// <c>--json</c> document (<see cref="FlowReports.Document"/>), on every exit.
/// </summary>
public interface IFlowService : IUseCase
{
    /// <summary>
    /// Starts a run of a declared workflow: validates it (12), holds its inputs and its name to what they take (10),
    /// makes the run's name, record and owner inside the repository's one section - refused (13) where its worktrees
    /// would pass this machine's path limit - and runs the session.
    /// </summary>
    /// <param name="startDirectory">The directory the command acts on: the repository, and where an input's <c>@file</c> is read from.</param>
    /// <param name="definition">The workflow to run, as the repository's config declares it.</param>
    /// <param name="run">The run's name; null for one more than the highest <c>r&lt;N&gt;</c>.</param>
    /// <param name="inputs">The inputs as the command line gave them, each <c>name=value</c> or <c>name=@file</c>; null where none.</param>
    /// <param name="json">Whether to answer with the run's document.</param>
    /// <param name="interrupts">The session's interrupts, as a terminal or a front end counts them.</param>
    /// <param name="cancellationToken">A hard stop of the session: the run ends stopped, in order.</param>
    Task<CommandOutcome> RunAsync(string startDirectory, string definition, string? run, IReadOnlyList<string>? inputs, bool json, FlowInterrupts? interrupts = null, CancellationToken cancellationToken = default);

    /// <summary>Goes on with a run: everything that can run, beside any question still open, under the definition it began with - refused (13) where the repository's entry has changed since.</summary>
    /// <param name="startDirectory">The directory the command acts on.</param>
    /// <param name="run">The run.</param>
    /// <param name="json">Whether to answer with the run's document.</param>
    /// <param name="interrupts">The session's interrupts, as a terminal or a front end counts them.</param>
    /// <param name="cancellationToken">A hard stop of the session: the run ends stopped, in order.</param>
    Task<CommandOutcome> ResumeAsync(string startDirectory, string run, bool json, FlowInterrupts? interrupts = null, CancellationToken cancellationToken = default);

    /// <summary>Answers a question of a run and goes on: handed to the engine where one is running it (<c>inbox.json</c>), else a session of the run's own.</summary>
    /// <param name="startDirectory">The directory the command acts on, and where <paramref name="textFile"/> is read from.</param>
    /// <param name="run">The run.</param>
    /// <param name="answer">The answer, spelled as the question's options spell it.</param>
    /// <param name="question">The question's id; null for the one the run has open.</param>
    /// <param name="text">The text the answer is given with, where it takes one.</param>
    /// <param name="textFile">A file holding that text, in the place of <paramref name="text"/>.</param>
    /// <param name="again">Whether it changes an answer already given: a plan update, which this build does not take.</param>
    /// <param name="json">Whether to answer with the run's document.</param>
    /// <param name="interrupts">The session's interrupts, as a terminal or a front end counts them.</param>
    /// <param name="cancellationToken">A hard stop of the session: the run ends stopped, in order.</param>
    Task<CommandOutcome> AnswerAsync(string startDirectory, string run, string answer, string? question, string? text, string? textFile, bool again, bool json, FlowInterrupts? interrupts = null, CancellationToken cancellationToken = default);

    /// <summary>The safe pause, asked of the engine running the run: waits until the pause is complete, names each handoff and each machine step still in progress.</summary>
    Task<CommandOutcome> PauseAsync(string startDirectory, string run, bool all, bool json, CancellationToken cancellationToken = default);

    /// <summary>The hard stop: asks the engine to stop where it stands and waits for it; <paramref name="now"/> ends its process without asking, on this machine only.</summary>
    Task<CommandOutcome> StopAsync(string startDirectory, string run, bool now, bool json, CancellationToken cancellationToken = default);

    /// <summary>Every question the run raised and what became of it, the open ones first, from <c>questions.json</c>.</summary>
    Task<CommandOutcome> QuestionsAsync(string startDirectory, string run, bool json, CancellationToken cancellationToken = default);
}

/// <summary>How often the commands of a run look at what another process asked or recorded.</summary>
/// <param name="PollEvery">How often a running engine reads what another process asked of it: <c>stop.json</c> and <c>inbox.json</c>.</param>
/// <param name="WatchEvery">How often <c>pause-orchestrator</c> and <c>stop-orchestrator</c> look again at what the engine has recorded.</param>
/// <param name="StopWait">How long <c>stop-orchestrator</c> waits for the engine to end before <c>--now</c> is the way, and how long <c>--now</c> waits for the process it ended.</param>
public sealed record FlowTimings(TimeSpan PollEvery, TimeSpan WatchEvery, TimeSpan StopWait)
{
    /// <summary>The tool's own: a poll every 2 s, a look every 200 ms, a stop waited for 30 s.</summary>
    public static FlowTimings Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30));
}

/// <summary>Wires the commands of a run over the engine's own services.</summary>
/// <param name="contextLoader">Loads the main checkout's context, whichever tree the command was typed in.</param>
/// <param name="platform">Says which machine this is.</param>
/// <param name="fileSystem">Reads and writes the run's records.</param>
/// <param name="store">Reads and writes the records, strictly.</param>
/// <param name="identity">This process, and how another's liveness is told.</param>
/// <param name="sessions">One unfinished run at a time: the test at the start of every session, the run's name, record and owner.</param>
/// <param name="validation">Validates the workflow a run is started of, and reads the entry a resume is held to.</param>
/// <param name="runner">The step runner every step of the run is given to.</param>
/// <param name="runnerFacts">What the engine asks of the runners a workflow names.</param>
/// <param name="log">One line for each command the tool ran for the run.</param>
/// <param name="children">Ends what a killed engine's steps left running, first in every command over a run.</param>
/// <param name="pathBudget">Reckons the run's deepest paths against this machine's limit at its start.</param>
/// <param name="output">Says what the engine's poller met and what it did about it.</param>
/// <param name="clock">When the run's records are written, and how every wait of these commands is timed.</param>
/// <param name="timings">How often the commands look; <see cref="FlowTimings.Default"/> where not given.</param>
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
    ChildSessions children,
    IPathBudget pathBudget,
    IHarnessOutput output,
    TimeProvider? clock = null,
    FlowTimings? timings = null) : IFlowService
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

    /// <summary>The refusal of <c>answer-orchestrator --again</c>, worded once.</summary>
    internal const string AgainRefused = "--again changes an answer already given, which raises a plan update; this build takes no plan update yet";

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
    private readonly ChildSessions _children = children;
    private readonly IPathBudget _pathBudget = pathBudget;
    private readonly IHarnessOutput _output = output;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly FlowTimings _timings = timings ?? FlowTimings.Default;
    private readonly FlowRunOwner _owner = new(store, identity, clock);

    /// <inheritdoc/>
    public async Task<CommandOutcome> RunAsync(
        string startDirectory,
        string definition,
        string? run,
        IReadOnlyList<string>? inputs,
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

        // A run's name names its agents' worktrees' directory, and is held to their length as create-orchestrator holds
        // an orchestrator's.
        if (run is not null && !WorktreeName.Validate(run, context.Config.Worktrees.MaxNameLength).TryGetName(out _, out var length))
        {
            return CommandOutcome.Usage(length);
        }

        var (given, inputProblem) = FlowInputs.Read(_fileSystem, startDirectory, inputs);

        if (inputProblem is not null)
        {
            return CommandOutcome.Usage(inputProblem);
        }

        // The definition is checked as validate-workflow checks it, and nothing runs of one that does not hold. A
        // workflow not declared of the repository is a refusal naming what is (13); one declared that does not hold
        // is the configuration's own answer (12). It is read from the definition tree - the main checkout, whichever
        // tree the command was typed in - as every later session of the run reads it.
        WorkflowValidationReport report;

        try
        {
            report = await _validation.CheckAsync(context.Layout.RepositoryRoot, definition, cancellationToken).ConfigureAwait(false);
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

        // The inputs are held to the definition before anything of a run is made, by the one owner of what an input
        // takes - the engine holds its run to the same - so nothing is written for a start that is refused.
        try
        {
            _ = FlowInputs.Resolve(workflow, given);
        }
        catch (HarnessException ex)
        {
            return new CommandOutcome(ex.ExitCode, ex.Message);
        }

        // Inside the repository's one section: the test for another unfinished run, the name, the path budget its
        // worktrees would need, the record, the first status and the owner. Two starts cannot both pass the test, and
        // two cannot take one name.
        OrchestratorLayout layout;
        FlowRunContext runContext;

        try
        {
            (layout, runContext) = _sessions.Create(
                context.Layout,
                workflow,
                run,
                given,
                refusal: name => FlowRunPaths.RefusalAtStart(_pathBudget, context.Config, context.Layout, name, workflow, _platform.PlatformKey));
        }
        catch (HarnessException ex)
        {
            return new CommandOutcome(ex.ExitCode, ex.Message);
        }

        return await SessionAsync(
            layout,
            RunCommand,
            reclaimed: null,
            () => (workflow, runContext, null),
            answered: null,
            interrupts,
            json,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> ResumeAsync(string startDirectory, string run, bool json, FlowInterrupts? interrupts = null, CancellationToken cancellationToken = default)
    {
        var (refused, context, layout) = await LoadAsync(startDirectory, run, ResumeCommand, cancellationToken).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        var (digest, changed) = await EntryDigestAsync(context.Layout.RepositoryRoot, layout, cancellationToken).ConfigureAwait(false);

        if (changed is not null)
        {
            return Ended(layout, ResumeCommand, changed, ResultOf(layout, changed), json);
        }

        string? reclaimed;

        try
        {
            reclaimed = _sessions.Claim(context.Layout, layout);
        }
        catch (HarnessException ex)
        {
            var outcome = new CommandOutcome(ex.ExitCode, ex.Message);
            return Ended(layout, ResumeCommand, outcome, ResultOf(layout, outcome), json);
        }

        return await SessionAsync(layout, ResumeCommand, reclaimed, () => Resumed(layout, digest!), answered: null, interrupts, json, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> AnswerAsync(
        string startDirectory,
        string run,
        string answer,
        string? question,
        string? text,
        string? textFile,
        bool again,
        bool json,
        FlowInterrupts? interrupts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        if (question is not null && !FlowQuestionIds.IsWellFormed(question))
        {
            return CommandOutcome.Usage($"'{question}' is not a question's id");
        }

        if (text is not null && textFile is not null)
        {
            return CommandOutcome.Usage("--text and --text-file are one answer's text given two ways; give one");
        }

        if (textFile is not null)
        {
            var (read, problem) = FlowInputs.TextOf(_fileSystem, startDirectory, textFile, "--text-file");

            if (problem is not null)
            {
                return CommandOutcome.Usage(problem);
            }

            text = read;
        }

        var (refused, context, layout) = await LoadAsync(startDirectory, run, AnswerCommand, cancellationToken).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
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
                return Refuse(layout, AnswerCommand, $"run '{run}' holds no question '{named}'", json);
            }

            // A changed answer is asked of a question already answered, which the run's file keeps with every answer
            // it was given: it is found, and refused for what it would raise, never for being answered.
            if (again)
            {
                return Refuse(layout, AnswerCommand, AgainRefused, json);
            }

            if (entry.State != "open")
            {
                return Refuse(layout, AnswerCommand, $"question '{named}' of run '{run}' is {entry.State}{(open.Count > 0 ? $"; what is open now: {string.Join(", ", open.Select(one => one.Id))}" : string.Empty)}", json);
            }

            asked = entry;
        }
        else if (again)
        {
            // The answer it would change is one already given, not an open question's.
            return Refuse(layout, AnswerCommand, AgainRefused, json);
        }
        else if (open.Count == 0)
        {
            return Refuse(layout, AnswerCommand, $"run '{run}' holds no open question", json);
        }
        else if (open.Count > 1)
        {
            return CommandOutcome.Usage(
                $"run '{run}' holds {open.Count.ToString(CultureInfo.InvariantCulture)} open questions; --question names the one this answers",
                [.. open.Select(entry => $"{entry.Id} ({entry.Kind}): {entry.ProblemShown?.Happened ?? entry.Kind}")]);
        }
        else
        {
            asked = open[0];
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
        // inbox under an id of its own, the engine takes it at its poll, and no second engine is started. An engine
        // whose session ends before it took it leaves it there, and the next session of the run takes it.
        if (_owner.Live(layout) is not null)
        {
            _store.AddToInbox(layout, new FlowInboxAnswer(FlowInboxAnswer.NewId(), asked.Id, answer, _clock.GetUtcNow(), takes ? text : null, again));

            var handed = new CommandOutcome(
                HarnessExit.Success,
                $"handed '{answer}' for question '{asked.Id}' to the engine running '{run}'",
                [
                    $"the engine reads it within {_timings.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s and takes it while the question is open; where its session ends first, the next session of the run takes it",
                    $"inbox: {layout.InboxFile}",
                ]);

            return Ended(layout, AnswerCommand, handed, ResultOf(layout, handed) with { Records = [new FlowRecordPath("inbox", layout.InboxFile)] }, json);
        }

        // The session is held to the definition it began with, as a resume is.
        var (digest, changed) = await EntryDigestAsync(context.Layout.RepositoryRoot, layout, cancellationToken).ConfigureAwait(false);

        if (changed is not null)
        {
            return Ended(layout, AnswerCommand, changed, ResultOf(layout, changed), json);
        }

        string? reclaimed;

        try
        {
            reclaimed = _sessions.Claim(context.Layout, layout);
        }
        catch (HarnessException ex)
        {
            var outcome = new CommandOutcome(ex.ExitCode, ex.Message);
            return Ended(layout, AnswerCommand, outcome, ResultOf(layout, outcome), json);
        }

        // The session begins with the answer: the question it names, answered by a person through this command, and
        // everything that can run after it.
        return await SessionAsync(layout, AnswerCommand, reclaimed, () => Resumed(layout, digest!), (asked.Id, answer, takes ? text : null), interrupts, json, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> PauseAsync(string startDirectory, string run, bool all, bool json, CancellationToken cancellationToken = default)
    {
        var (refused, _, layout) = await LoadAsync(startDirectory, run, PauseCommand, cancellationToken).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        if (_owner.Live(layout) is null)
        {
            return Refuse(layout, PauseCommand, $"run '{run}' is not being run by an engine: there is nothing to pause", json);
        }

        // A pause asked of the engine from any terminal on the machine: written under the run's own section, read by
        // the engine at its poll, and waited for here until the pause is complete. A pause recorded before this one
        // was asked - the last session's, still in the state a new session has not written yet - is not this one's.
        var asked = Whole(_clock.GetUtcNow());
        _store.WriteStopFile(layout, new FlowStopFile(1, new FlowStopPause(all, _clock.GetUtcNow())));

        while (true)
        {
            var flow = _store.ReadFlow(layout);
            var taken = _fileSystem.KindOf(layout.StopFile) == PathKind.None;

            if (taken && flow?.Pause is { CompletedAt: not null } pause && (pause.At >= asked || flow.Status == nameof(FlowRunStatus.Running)))
            {
                return Paused(layout, flow, json, said: null);
            }

            if (_owner.Live(layout) is null)
            {
                return EngineLeft(layout, PauseCommand, "pause", json);
            }

            try
            {
                await Task.Delay(_timings.WatchEvery, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var cancelled = CommandOutcome.Failed(
                    HarnessExit.Cancelled,
                    $"waiting for the pause of '{run}' was cancelled; the pause stays asked, and the engine takes it at its poll",
                    [$"stop: {layout.StopFile}"]);

                return Ended(layout, PauseCommand, cancelled, ResultOf(layout, cancelled) with { Records = [new FlowRecordPath("stop", layout.StopFile)] }, json);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> StopAsync(string startDirectory, string run, bool now, bool json, CancellationToken cancellationToken = default)
    {
        var (refused, _, layout) = await LoadAsync(startDirectory, run, StopCommand, cancellationToken).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        if (_owner.Live(layout) is not { } held)
        {
            return Refuse(layout, StopCommand, $"run '{run}' is not being run by an engine: there is nothing to stop", json);
        }

        if (now)
        {
            return await EndNowAsync(layout, held, json, cancellationToken).ConfigureAwait(false);
        }

        _store.WriteStopFile(layout, new FlowStopFile(1, Stop: new FlowStopStop(_clock.GetUtcNow())));

        var until = _clock.GetUtcNow() + _timings.StopWait;

        while (_clock.GetUtcNow() < until)
        {
            if (_owner.Live(layout) is null)
            {
                return EngineLeft(layout, StopCommand, "stop", json);
            }

            try
            {
                await Task.Delay(_timings.WatchEvery, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var cancelled = CommandOutcome.Failed(
                    HarnessExit.Cancelled,
                    $"waiting for run '{run}' to stop was cancelled; the stop stays asked, and the engine takes it at its poll",
                    [$"stop: {layout.StopFile}"]);

                return Ended(layout, StopCommand, cancelled, ResultOf(layout, cancelled) with { Records = [new FlowRecordPath("stop", layout.StopFile)] }, json);
            }
        }

        var late = CommandOutcome.Refused(
            $"the engine running '{run}' did not stop within {_timings.StopWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s; the stop stays asked",
            [$"{StopCommand} {run} --now ends its process where it stands", $"stop: {layout.StopFile}"]);

        return Ended(layout, StopCommand, late, ResultOf(layout, late) with { Records = [new FlowRecordPath("stop", layout.StopFile)] }, json);
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> QuestionsAsync(string startDirectory, string run, bool json, CancellationToken cancellationToken = default)
    {
        const string command = "list-orchestrator";
        var (refused, _, layout) = await LoadAsync(startDirectory, run, command, cancellationToken).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        if (_store.ReadFlowQuestions(layout) is not { } questions)
        {
            return Refuse(layout, command, $"run '{run}' holds no questions: '{layout.QuestionsFile}' is not there", json);
        }

        return CommandOutcome.Ok(
            $"run '{run}' raised {ReportText.Counted(questions.Questions.Count, "question")}",
            [.. QuestionsText.Of(questions), $"questions: {layout.QuestionsFile}"]);
    }

    /// <summary>
    /// Loads the run's context and layout, and sweeps what a killed engine's steps left running before anything else of
    /// the run is read - or the refusal that answers for the command: a bad name (10), or no such run (13, with nothing
    /// written, for a run that is not there has no directory to log in).
    /// </summary>
    private async Task<(CommandOutcome? Refused, HarnessContext Context, OrchestratorLayout Layout)> LoadAsync(string startDirectory, string run, string command, CancellationToken cancellationToken)
    {
        if (OrchestrationRules.NameProblem(run) is { } shape)
        {
            return (CommandOutcome.Usage(shape), null!, null!);
        }

        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, run);

        if (_store.ReadOrchestrator(layout) is null)
        {
            return (CommandOutcome.Refused(OrchestrationReports.NoOrchestrator(run)), context, layout);
        }

        await _children.SweepAsync(layout, command).ConfigureAwait(false);
        return (null, context, layout);
    }

    /// <summary>
    /// The digest of the repository's entry for the workflow the run began with, as it reads now in the definition tree
    /// - the main checkout, <paramref name="definitionTree"/>, whichever tree the command was typed in - or the refusal
    /// (13) where the entry is no longer one that holds: a run goes on only under the definition it began with.
    /// </summary>
    private async Task<(string? Digest, CommandOutcome? Refused)> EntryDigestAsync(string definitionTree, OrchestratorLayout layout, CancellationToken cancellationToken)
    {
        var frozen = _store.ReadDefinition(layout);

        if (frozen is null)
        {
            return (null, CommandOutcome.Refused($"'{layout.DefinitionFile}' is not there, so run '{layout.Name}' holds no definition to go on with"));
        }

        WorkflowValidationReport report;

        try
        {
            report = await _validation.CheckAsync(definitionTree, frozen.Name, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return (null, CommandOutcome.Refused($"run '{layout.Name}' began of workflow '{frozen.Name}', and the repository's config cannot be read for it now: {ex.Message.TrimEnd('.')}. Nothing of the run was changed."));
        }

        if (report.Workflows.FirstOrDefault() is not { Holds: true, Definition: { } entry })
        {
            return (null, CommandOutcome.Refused(
                $"run '{layout.Name}' began of workflow '{frozen.Name}', which the repository's config no longer declares as one that holds; a run goes on only under the definition it began with. Nothing of the run was changed.",
                report.Workflows.FirstOrDefault()?.Lines ?? [WorkflowValidationReports.NoneDeclared]));
        }

        return (FlowRunContext.DigestOf(entry), null);
    }

    /// <summary>What a session over a run already made goes on from: the definition it began with, the state it left reconciled and held to <paramref name="digest"/>, and the run as its records name it.</summary>
    private (WorkflowDefinition Definition, FlowRunContext Context, FlowResumeState? Resume) Resumed(OrchestratorLayout layout, string digest)
    {
        var definition = _store.ReadDefinition(layout)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{layout.DefinitionFile}' is not there, so run '{layout.Name}' holds no definition to go on with");

        var resume = new FlowResume(_store, layout, _fileSystem).Load(digest);

        return (definition, FlowRunContext.Resumed(definition, resume.File), resume);
    }

    /// <summary>
    /// One session of a run, from its owner claimed to its end: the engine built over the run's records, the answer it
    /// begins with, what was handed to an engine that ended before it took it, then driven - again, where an answer
    /// handed over as it ended settles the question it paused on - and the report written. However it ends - a
    /// refusal, a defect - the owner is released and the outcome is in the run's log.
    /// </summary>
    private async Task<CommandOutcome> SessionAsync(
        OrchestratorLayout layout,
        string command,
        string? reclaimed,
        Func<(WorkflowDefinition Definition, FlowRunContext Context, FlowResumeState? Resume)> begin,
        (string Question, string Answer, string? Text)? answered,
        FlowInterrupts? interrupts,
        bool json,
        CancellationToken cancellationToken)
    {
        var said = new List<string>();
        var inbox = false;

        if (reclaimed is not null)
        {
            said.Add(reclaimed);
        }

        try
        {
            var (definition, runContext, resume) = begin();

            using var records = new FlowRunRecords(_fileSystem, _store, layout, runContext, _clock);
            var coordinator = new FlowCoordinator(definition, _runner, _runnerFacts, _clock, runContext.Inputs, records, resume, layout.Name);
            var before = _store.ReadFlowQuestions(layout);
            var offered = new Dictionary<string, Task<bool>>(StringComparer.Ordinal);

            if (answered is { } given && !coordinator.TryAnswer(given.Question, given.Answer, given.Text, out var problem, AnswerCommand))
            {
                throw new HarnessException(HarnessExit.Refused, problem!);
            }

            // What was handed to an engine whose session ended before it took it is this session's to take.
            inbox |= TakeInbox(layout, coordinator, offered, said).Seen > 0;

            FlowSessionEnd end;

            while (true)
            {
                end = await DriveAsync(layout, command, coordinator, interrupts, offered, cancellationToken).ConfigureAwait(false);

                // The last poll, the poller stopped: where an answer it takes settles the question the session paused
                // on, the run is driven again.
                var (taken, seen) = LastPoll(layout, coordinator, offered, said);
                inbox |= seen > 0;

                if (taken > 0 && end is { Status: FlowRunStatus.Paused, Pause: null })
                {
                    continue;
                }

                break;
            }

            if (offered.Count > 0)
            {
                inbox = true;
                said.Add($"{ReportText.Counted(offered.Count, "answer")} handed to this engine {(offered.Count == 1 ? "was" : "were")} not taken before its session ended; {(offered.Count == 1 ? "it stays" : "they stay")} in '{layout.InboxFile}', and the next session of the run takes {(offered.Count == 1 ? "it" : "them")}");
            }

            var flow = _store.ReadFlow(layout)
                ?? throw new HarnessException(HarnessExit.Refused, $"'{layout.FlowFile}' is not there after the session of run '{layout.Name}', which writes it at every change");
            var questions = _store.ReadFlowQuestions(layout);
            var exit = ExitOf(end, flow);
            var left = FlowReports.Write(_fileSystem, layout, flow, questions, end.Summary, exit, _clock.GetUtcNow());
            var named = RecordsOf(layout, inbox);

            var result = new FlowRunResult(
                flow.Status,
                layout.Name,
                end.Summary,
                questions?.Open ?? [],
                AnsweredSince(before, questions),
                end.PassedOver,
                end.Pause is { } pause ? [.. FlowReports.HandoffsOf(layout, flow).Where(handoff => pause.Handoffs.Contains(handoff.Instance, StringComparer.Ordinal))] : [],
                left,
                flow.Spent,
                exit,
                named);

            return Ended(layout, command, new CommandOutcome(exit, end.Summary, [.. said, .. left, .. result.RecordLines]), result, json);
        }
        catch (HarnessException ex)
        {
            // A refusal met inside the session - an answer the run does not take, a state that cannot be taken up, a
            // definition changed - is answered as a refusal, naming the run's records as they stand; the report is the
            // last session's, for a refused session is none.
            var flow = SafeRead(() => _store.ReadFlow(layout));
            var records = flow is null ? [] : RecordsOf(layout, inbox);
            var outcome = new CommandOutcome(ex.ExitCode, ex.Message, [.. said, .. records.Select(record => $"{record.What}: {record.Path}")]);
            var result = FlowRunResult.Empty(ex.ExitCode, ex.Message) with
            {
                Run = layout.Name,
                Status = flow?.Status,
                Spent = flow?.Spent,
                Records = records,
            };

            return Ended(layout, command, outcome, result, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A defect: in the run's log too, for whoever reads it later; the session's own answer is the defect's.
            _ = _log.Record(layout, layout.Name, command, CommandOutcome.Failed(HarnessExit.InternalError, $"the session ended by a defect: {ex.Message}"));
            throw;
        }
        finally
        {
            _owner.Release(layout);
        }
    }

    /// <summary>
    /// Drives one session of the run to its end - the poller reading what another process asked of the engine beside
    /// it. Nothing cancels the session's own waiting: its hard stop - the second interrupt, a stop asked through
    /// <c>stop.json</c>, or a cancellation of <paramref name="cancellationToken"/> - asks the engine to stop where it
    /// stands, and the engine's loop, which alone owns the graph, cancels what is in flight and ends the session in
    /// order, stopped, its records saying so. A session cut off part way would leave its steps for a later session to
    /// take for stopped ones; how long the stop may take is the terminal's to bound (<see cref="FlowTerminal"/>).
    /// </summary>
    private async Task<FlowSessionEnd> DriveAsync(
        OrchestratorLayout layout,
        string command,
        FlowCoordinator coordinator,
        FlowInterrupts? interrupts,
        Dictionary<string, Task<bool>> offered,
        CancellationToken cancellationToken)
    {
        using var poll = new CancellationTokenSource();
        using var outer = cancellationToken.Register(coordinator.RequestStop);

        interrupts?.BoundTo(coordinator);
        var polling = PollAsync(layout, command, coordinator, offered, poll.Token);

        try
        {
            return await coordinator.RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await poll.CancelAsync().ConfigureAwait(false);
            await polling.ConfigureAwait(false);
            interrupts?.Unbound();
        }
    }

    /// <summary>
    /// Reads what another process asked of the engine - at once, then every <see cref="FlowTimings.PollEvery"/>: a pause
    /// or a stop in <c>stop.json</c>, an answer in <c>inbox.json</c> - and hands each to the engine, which alone applies
    /// it. It never waits on what it handed: an answer is offered, and emptied from the inbox once the loop has taken or
    /// refused it. What a tick meets that can pass - a file being replaced, a lock held - is said once and read again
    /// at the next; anything else is said, and the run is asked to pause rather than be left with nobody reading what
    /// is asked of it.
    /// </summary>
    private async Task PollAsync(OrchestratorLayout layout, string command, FlowCoordinator coordinator, Dictionary<string, Task<bool>> offered, CancellationToken cancellationToken)
    {
        var said = new HashSet<string>(StringComparer.Ordinal);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Tick(layout, coordinator, offered);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
            {
                if (said.Add(ex.Message))
                {
                    _output.Warn(command, $"what was asked of the engine of run '{layout.Name}' could not be read: {ex.Message.TrimEnd('.')}; it is read again at the next poll");
                }
            }
            catch (Exception ex)
            {
                _output.Warn(command, $"the engine of run '{layout.Name}' can no longer read what is asked of it ({ex.GetType().Name}: {ex.Message.TrimEnd('.')}); the run is asked to pause");
                coordinator.RequestPause("command");
                return;
            }

            try
            {
                await Task.Delay(_timings.PollEvery, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One poll: the request taken and handed on, the answers the loop has settled emptied from the inbox, and each answer not yet offered offered.</summary>
    private void Tick(OrchestratorLayout layout, FlowCoordinator coordinator, Dictionary<string, Task<bool>> offered)
    {
        switch (_store.TakeStopFile(layout))
        {
            case { Pause: { } pause }:
                coordinator.RequestPause("command", pause.All);
                break;

            case { Stop: not null }:
                // The hard stop asked of the engine from another terminal: everything in flight cancelled and ended
                // stopped by the loop, which alone owns the graph, and the session ends with it.
                coordinator.RequestStop();
                break;
        }

        Settle(layout, offered);

        foreach (var entry in _store.ReadInbox(layout)?.Answers ?? [])
        {
            // An answer that changes another is a plan update, which the session's end says it does not take.
            if (!entry.Again && !offered.ContainsKey(entry.Id))
            {
                offered[entry.Id] = coordinator.OfferAnswerAsync(entry.Question, entry.Answer, entry.Text, AnswerCommand);
            }
        }
    }

    /// <summary>
    /// The last poll of a session, its poller stopped: a request asked as the session ended was asked of this session and
    /// of none after it, an answer the loop took is emptied from the inbox, and one it never saw is taken here. What
    /// cannot be read is said and left for the next session of the run, and the session's end stands.
    /// </summary>
    /// <returns>How many answers were taken, and how many were read.</returns>
    private (int Taken, int Seen) LastPoll(OrchestratorLayout layout, FlowCoordinator coordinator, Dictionary<string, Task<bool>> offered, List<string> said)
    {
        try
        {
            if (_store.TakeStopFile(layout) is { } late)
            {
                said.Add($"a {(late.Pause is not null ? "pause" : "stop")} asked as the session ended was taken with it");
            }

            Settle(layout, offered);
            return TakeInbox(layout, coordinator, offered, said);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            said.Add($"what was asked of the engine as its session ended could not be read: {ex.Message.TrimEnd('.')}; it stays for the next session of the run");
            return (0, 1);
        }
    }

    /// <summary>
    /// Empties from the inbox each answer whose offer the loop settled - taken, or refused and recorded as an event - and
    /// forgets each it cancelled, which stays in the inbox for whoever takes it next.
    /// </summary>
    private void Settle(OrchestratorLayout layout, Dictionary<string, Task<bool>> offered)
    {
        foreach (var (id, offer) in offered.Where(pair => pair.Value.IsCompleted).ToList())
        {
            _ = offered.Remove(id);

            if (offer.IsCompletedSuccessfully)
            {
                _ = _store.RemoveFromInbox(layout, id);
            }
        }
    }

    /// <summary>
    /// Takes every answer in the inbox the loop was never offered, between two sessions: each applied as a person's
    /// through <c>answer-orchestrator</c>, or - where the run does not take it - said and recorded as an <c>answer</c>
    /// event, as the loop records one it refuses; emptied from the inbox either way.
    /// </summary>
    /// <returns>How many answers were taken, and how many were read.</returns>
    private (int Taken, int Seen) TakeInbox(OrchestratorLayout layout, FlowCoordinator coordinator, Dictionary<string, Task<bool>> offered, List<string> said)
    {
        var taken = 0;
        var seen = 0;

        foreach (var entry in _store.ReadInbox(layout)?.Answers ?? [])
        {
            if (offered.ContainsKey(entry.Id))
            {
                continue;
            }

            seen++;

            if (entry.Again)
            {
                said.Add($"the answer '{entry.Answer}' handed over for question '{entry.Question}' changes one already given, which raises a plan update; this build takes no plan update yet, so it was not taken");
            }
            else if (coordinator.TryAnswerHandedOver(entry.Question, entry.Answer, entry.Text, out var problem, AnswerCommand))
            {
                taken++;
                said.Add($"the answer '{entry.Answer}' handed over for question '{entry.Question}' was taken from the inbox");
            }
            else
            {
                said.Add($"the answer '{entry.Answer}' handed over for question '{entry.Question}' was not taken: {problem}");
            }

            _ = _store.RemoveFromInbox(layout, entry.Id);
        }

        return (taken, seen);
    }

    /// <summary>
    /// What a session's end exits with: 0 done; 21 failed where every failure that ended it was incomplete after its
    /// retries (<see cref="FlowRetry.RunsAgain"/>), else 20; 1 where a question waits - the budget's pause among them, for
    /// it leaves its own question; 130 a safe pause or a stop.
    /// </summary>
    internal static int ExitOf(FlowSessionEnd end, FlowFile flow)
        => end.Status switch
        {
            FlowRunStatus.Done => HarnessExit.Success,
            FlowRunStatus.Failed when end.Failed.Count > 0 && end.Failed.All(id => IncompleteEnding(flow, id)) => HarnessExit.Incomplete,
            FlowRunStatus.Failed => HarnessExit.CommandFailed,
            FlowRunStatus.Paused when end.Pause is null || end.Pause.Cause == "budget" => FlowExit.Paused,
            _ => HarnessExit.Cancelled,
        };

    /// <summary>
    /// Whether the instance <paramref name="id"/> ended with a code the retry table runs again - incomplete, its retries
    /// spent - read from its record alone: its code, and the cause its record keeps where the code cannot say it, as a
    /// refusal over a held lock.
    /// </summary>
    private static bool IncompleteEnding(FlowFile flow, string id)
        => flow.Steps.FirstOrDefault(step => string.Equals(step.Instance, id, StringComparison.Ordinal)) is { Exit: { } exit } step
            && FlowRetry.RunsAgain(exit, step.Cause is { } cause ? FlowRecordNames.Of<OutcomeCause>(cause) : OutcomeCause.None);

    /// <summary>Every record a session of the run writes, by what it is and its path - the inbox where the session took or left an answer in it.</summary>
    private static IReadOnlyList<FlowRecordPath> RecordsOf(OrchestratorLayout layout, bool inbox)
        =>
        [
            new("run", layout.Directory),
            new("state", layout.FlowFile),
            new("status", layout.WorkflowStatusFile),
            new("questions", layout.QuestionsFile),
            new("issues", layout.PreExistingIssuesFile),
            new("definition", layout.DefinitionFile),
            new("events", layout.EventsFile),
            new("report", layout.ReportFile),
            new("reportJson", layout.ReportJsonFile),
            .. inbox ? new[] { new FlowRecordPath("inbox", layout.InboxFile) } : [],
        ];

    /// <summary>Every answer the questions were given after <paramref name="before"/>: the ones this command gave, each with who gave it and what the engine did.</summary>
    private static IReadOnlyList<FlowAnswered> AnsweredSince(FlowQuestionsFile? before, FlowQuestionsFile? after)
    {
        var had = (before?.Questions ?? []).ToDictionary(question => question.Id, question => question.Answers.Count, StringComparer.Ordinal);

        return
        [
            .. (after?.Questions ?? []).SelectMany(question => question.Answers
                .Skip(had.GetValueOrDefault(question.Id))
                .Select(answer => new FlowAnswered(
                    question.Id,
                    answer.Answer,
                    answer.By?.Who,
                    answer.By is { Who: "configuration" } by ? by.Where : answer.By?.Through,
                    answer.Did))),
        ];
    }

    /// <summary>The run's document for a command that reached the run and none of its sessions: its status, its open questions and what it spent, as the records hold them.</summary>
    private FlowRunResult ResultOf(OrchestratorLayout layout, CommandOutcome outcome)
    {
        var flow = SafeRead(() => _store.ReadFlow(layout));
        var questions = SafeRead(() => _store.ReadFlowQuestions(layout));

        return FlowRunResult.Empty(outcome.ExitCode, outcome.Message) with
        {
            Run = layout.Name,
            Status = flow?.Status,
            Questions = questions?.Open ?? [],
            Spent = flow?.Spent,
        };
    }

    /// <summary>A refusal of a command over a run, in the run's log and its document.</summary>
    private CommandOutcome Refuse(OrchestratorLayout layout, string command, string message, bool json)
    {
        var outcome = CommandOutcome.Refused(message);
        return Ended(layout, command, outcome, ResultOf(layout, outcome), json);
    }

    /// <summary>
    /// The command's end over a run: its line in the run's log, and - asked for <c>--json</c> - its document, which
    /// names every record the command wrote or took, the log among them.
    /// </summary>
    private CommandOutcome Ended(OrchestratorLayout layout, string command, CommandOutcome outcome, FlowRunResult result, bool json)
    {
        var named = result with { Records = [.. result.Records, new FlowRecordPath("log", layout.LogFile(layout.Name))] };
        var recorded = _log.Record(layout, layout.Name, command, outcome with { Facts = named });

        return json ? recorded with { Data = [FlowReports.Document(named)], Quiet = true } : recorded;
    }

    /// <summary>
    /// What <c>pause-orchestrator</c> answers once the pause is complete: each handoff it wrote, by its file, and each
    /// machine step still in progress, which the engine stays to record.
    /// </summary>
    private CommandOutcome Paused(OrchestratorLayout layout, FlowFile flow, bool json, string? said)
    {
        var pause = flow.Pause!;
        var handoffs = FlowReports.HandoffsOf(layout, flow).Where(handoff => pause.Handoffs.Contains(handoff.Instance, StringComparer.Ordinal)).ToList();
        var machine = flow.MachineStepsRunning();
        var records = new FlowRecordPath[] { new("stop", layout.StopFile), new("state", layout.FlowFile), new("status", layout.WorkflowStatusFile) };

        var outcome = CommandOutcome.Ok(
            $"run '{layout.Name}' is paused{(handoffs.Count == 0 ? string.Empty : $"; {ReportText.Counted(handoffs.Count, "handoff")} written")}",
            [
                .. said is null ? [] : new[] { said },
                .. handoffs.Select(handoff => $"handoff of {handoff.Instance}, written by the {handoff.By}: {handoff.File}"),
                .. machine.Count == 0 ? [] : new[]
                {
                    $"{ReportText.Counted(machine.Count, "machine step")} still in progress: {string.Join(", ", machine)}; the engine's process ends when {(machine.Count == 1 ? "it has" : "they have")}",
                },
                .. records.Select(record => $"{record.What}: {record.Path}"),
            ]);

        var result = ResultOf(layout, outcome) with { Handoffs = handoffs, Records = records };
        return Ended(layout, PauseCommand, outcome, result, json);
    }

    /// <summary>
    /// What a pause or a stop answers where the engine left before it saw it through - from the run's own state: a
    /// request it never took is taken back, so it reaches no later session; a run paused is paused, whoever paused it;
    /// one stopped by the stop asked is stopped; every other end is said as it is, and nothing was paused or stopped.
    /// </summary>
    private CommandOutcome EngineLeft(OrchestratorLayout layout, string command, string asked, bool json)
    {
        var untaken = _store.TakeStopFile(layout) is not null;
        var flow = SafeRead(() => _store.ReadFlow(layout));
        var status = flow?.Status;
        var records = new FlowRecordPath[] { new("stop", layout.StopFile), new("state", layout.FlowFile), new("report", layout.ReportFile) };
        var how = untaken ? $"its engine ended before it took the {asked}, which was taken back" : $"its engine took the {asked} and left";

        if (command == PauseCommand && flow?.Pause is { CompletedAt: not null } && status == nameof(FlowRunStatus.Paused))
        {
            return Paused(layout, flow, json, $"{how}; the run is paused");
        }

        CommandOutcome outcome;

        if (command == StopCommand && !untaken && status is nameof(FlowRunStatus.Stopped) or nameof(FlowRunStatus.Paused))
        {
            outcome = CommandOutcome.Ok(
                $"run '{layout.Name}' is {status!.ToLower(CultureInfo.InvariantCulture)}",
                [how, $"{ResumeCommand} goes on with it where it stands", .. records.Select(record => $"{record.What}: {record.Path}")]);
        }
        else
        {
            var stands = status switch
            {
                null => "holds no state",
                nameof(FlowRunStatus.Running) => "was left running: its engine has gone without ending it - a crash, or stop-orchestrator --now - and resume-orchestrator goes on with it",
                _ => $"is {status.ToLower(CultureInfo.InvariantCulture)}",
            };

            outcome = CommandOutcome.Refused(
                $"run '{layout.Name}' {stands}; {how}, so nothing was {(asked == "pause" ? "paused" : "stopped")}",
                [.. records.Select(record => $"{record.What}: {record.Path}")]);
        }

        return Ended(layout, command, outcome, ResultOf(layout, outcome) with { Records = records }, json);
    }

    /// <summary>
    /// <c>stop-orchestrator --now</c>: the engine's process ended without asking it - only one of this machine's, only
    /// the process its owner names by its start, and never this one - then what its steps' contained children left is
    /// ended and its owner is removed. A process that could not be ended is a failure, said; what is left is what a crash
    /// leaves, less those processes.
    /// </summary>
    private async Task<CommandOutcome> EndNowAsync(OrchestratorLayout layout, RunOwner held, bool json, CancellationToken cancellationToken)
    {
        var name = _owner.Named(held);

        CommandOutcome Answer(CommandOutcome outcome) => Ended(layout, StopCommand, outcome, ResultOf(layout, outcome), json);

        if (!_owner.IsHere(held))
        {
            return Answer(CommandOutcome.Refused(
                $"run '{layout.Name}' is owned by an engine on another machine ({name}); --now ends only a process of this machine. {StopCommand} {layout.Name} without --now asks it to stop, or end it on its own machine. Nothing was changed."));
        }

        if (held.ProcessId == _identity.CurrentId)
        {
            return Answer(CommandOutcome.Refused($"run '{layout.Name}' is owned by this very process; --now ends another engine's process. Nothing was changed."));
        }

        if (held.Stamp is not { } stamp || !_identity.IsSame(held.ProcessId, stamp))
        {
            if (!_identity.IsAlive(held.ProcessId, held.Stamp))
            {
                _owner.RemoveGone(layout);
                return Answer(CommandOutcome.Ok($"the engine of run '{layout.Name}' ({name}) had already ended; its owner was removed", [$"{ResumeCommand} goes on from the records"]));
            }

            return Answer(CommandOutcome.Refused(
                $"{name} cannot be told to be the engine of run '{layout.Name}' - its start cannot be read beside the one its owner recorded - so it was not ended. Nothing was changed."));
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(held.ProcessId);
            process.Kill(entireProcessTree: true);

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(_timings.StopWait);
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // It ended meanwhile.
        }
        catch (OperationCanceledException)
        {
            return Answer(CommandOutcome.Failed(HarnessExit.CommandFailed, $"{name}, the engine of run '{layout.Name}', was ended and had not gone within {_timings.StopWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s; its owner is kept"));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or AggregateException)
        {
            return Answer(CommandOutcome.Failed(HarnessExit.CommandFailed, $"{name}, the engine of run '{layout.Name}', could not be ended: {ex.Message.TrimEnd('.')}; its owner is kept"));
        }

        _owner.RemoveGone(layout);
        var ended = await _children.SweepAsync(layout, StopCommand).ConfigureAwait(false);

        return Answer(CommandOutcome.Ok(
            $"ended the engine of run '{layout.Name}' ({name}) where it stood",
            [
                .. ended.Count == 0 ? [] : new[] { $"{ReportText.Counted(ended.Count, "session")} its steps' contained children led {(ended.Count == 1 ? "was" : "were")} ended with it" },
                $"what it left is what a crash leaves; {ResumeCommand} goes on from the records",
            ]));
    }

    /// <summary>A record read for an answer that must not fail for it: null where it is not there or does not read.</summary>
    private static T? SafeRead<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (HarnessException)
        {
            return null;
        }
    }

    /// <summary>A moment as the run's records hold it: to the whole second.</summary>
    private static DateTimeOffset Whole(DateTimeOffset moment) => moment.AddTicks(-(moment.Ticks % TimeSpan.TicksPerSecond));
}

/// <summary>Every question of a run as text, the open ones first: what it asked, and what became of it.</summary>
public static class QuestionsText
{
    /// <summary>The lines of <paramref name="questions"/>, open first.</summary>
    public static IEnumerable<string> Of(FlowQuestionsFile questions)
    {
        ArgumentNullException.ThrowIfNull(questions);

        foreach (var question in questions.Questions.OrderBy(entry => entry.State != "open").ThenBy(entry => entry.Id, StringComparer.Ordinal))
        {
            yield return $"question {question.Id} ({question.Kind}, {question.State}): {question.ProblemShown?.Happened ?? question.Kind}";

            foreach (var option in question.Options)
            {
                yield return $"  {option.Answer}: {option.Does}";
            }

            foreach (var answer in question.Answers)
            {
                yield return $"  answered '{answer.Answer}' by {Who(answer.By)}{(answer.Text is { } given ? $": \"{given}\"" : string.Empty)}";
            }
        }
    }

    /// <summary>Who gave an answer, in words: a person and how, the configuration and where, the input it came from.</summary>
    /// <param name="by">Who gave it, as the answer records it; null where it records nobody.</param>
    public static string Who(FlowAnswerBy? by)
        => by switch
        {
            null => "someone unknown",
            { Who: "person", Through: { } through } => $"a person, through {through}",
            { Who: "person" } => "a person",
            { Who: "configuration", Where: { } where } => $"the configuration, {where}",
            { Who: "configuration" } => "the configuration",
            { Who: "input", Input: { } input } => $"the input '{input}'",
            _ => by.Who,
        };
}
