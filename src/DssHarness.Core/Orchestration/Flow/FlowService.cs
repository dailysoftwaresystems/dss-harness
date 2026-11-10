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
    /// makes the run's name, record and owner inside the repository's one section - refused (13), with nothing made,
    /// where the main checkout is no place a run starts from (<see cref="FlowCheckout.StartRefusal"/>), where its pull
    /// request's base cannot be filled, or where its worktrees would pass this machine's path limit - and runs the session.
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
    /// <param name="again">Whether it changes the last answer a person gave: a plan update, within its window and once.</param>
    /// <param name="json">Whether to answer with the run's document.</param>
    /// <param name="interrupts">The session's interrupts, as a terminal or a front end counts them.</param>
    /// <param name="cancellationToken">A hard stop of the session: the run ends stopped, in order.</param>
    Task<CommandOutcome> AnswerAsync(string startDirectory, string run, string answer, string? question, string? text, string? textFile, bool again, bool json, FlowInterrupts? interrupts = null, CancellationToken cancellationToken = default);

    /// <summary>Amends the request a run was started with: handed to the engine where one is running it (<c>inbox.json</c>), else recorded as a plan update <c>queued</c>, taken first when the run is next gone on with. Starts nothing.</summary>
    /// <param name="startDirectory">The directory the command acts on, and where <paramref name="textFile"/> is read from.</param>
    /// <param name="run">The run.</param>
    /// <param name="text">The amendment, in the person's words: it adds to the request, corrects it or narrows it.</param>
    /// <param name="textFile">A file holding the amendment, in the place of <paramref name="text"/>.</param>
    /// <param name="json">Whether to answer with the run's document.</param>
    /// <param name="cancellationToken">A hard stop of the command.</param>
    Task<CommandOutcome> AmendAsync(string startDirectory, string run, string? text, string? textFile, bool json, CancellationToken cancellationToken = default);

    /// <summary>The safe pause, asked of the engine running the run: waits until the pause is complete, names each handoff and each machine step still in progress.</summary>
    Task<CommandOutcome> PauseAsync(string startDirectory, string run, bool all, bool json, CancellationToken cancellationToken = default);

    /// <summary>The hard stop: asks the engine to stop where it stands and waits for it; <paramref name="now"/> ends its process without asking, on this machine only.</summary>
    Task<CommandOutcome> StopAsync(string startDirectory, string run, bool now, bool json, CancellationToken cancellationToken = default);

    /// <summary>Every question the run raised and what became of it, the open ones first, from <c>questions.json</c>.</summary>
    Task<CommandOutcome> QuestionsAsync(string startDirectory, string run, bool json, CancellationToken cancellationToken = default);

    /// <summary>
    /// The tool's own question about a run - what is happening now, what is done, what is left, what it waits for, and
    /// when it may finish - answered with the engine's facts and, where the workflow names a reporter, its words. Reads
    /// only: nothing of the run is written or locked but the command's own record under the run's <c>asks/</c>, and its
    /// cost is the person's, never the run's. Where no model words the facts, they are still the answer, and the command
    /// says why no model did.
    /// </summary>
    /// <param name="startDirectory">The directory the command acts on.</param>
    /// <param name="run">The run.</param>
    /// <param name="json">Whether to answer with one JSON document.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    Task<CommandOutcome> AskStatusAsync(string startDirectory, string run, bool json, CancellationToken cancellationToken = default);

    /// <summary>
    /// A person's question about a run, answered from its records by the workflow's reporter over the engine's facts.
    /// Reads only, as <see cref="AskStatusAsync"/> is; where no model can answer - no reporter named, no key, no Claude
    /// Code, a limit, a failure - the command is refused, naming why.
    /// </summary>
    /// <param name="startDirectory">The directory the command acts on.</param>
    /// <param name="run">The run.</param>
    /// <param name="question">The person's question, in their words; null never reaches here, for that is the status command's own call.</param>
    /// <param name="json">Whether to answer with one JSON document.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    Task<CommandOutcome> AskAsync(string startDirectory, string run, string? question, bool json, CancellationToken cancellationToken = default);
}

/// <summary>How often the commands of a run look at what another process asked or recorded.</summary>
/// <param name="PollEvery">How often a running engine reads what another process asked of it: <c>stop.json</c> and <c>inbox.json</c>.</param>
/// <param name="WatchEvery">How often <c>pause-orchestrator</c> and <c>stop-orchestrator</c> look again at what the engine has recorded.</param>
/// <param name="StopWait">How long <c>stop-orchestrator</c> waits for the engine to end before <c>--now</c> is the way, and how long <c>--now</c> waits for the process it ended.</param>
public sealed record FlowTimings(TimeSpan PollEvery, TimeSpan WatchEvery, TimeSpan StopWait)
{
    /// <summary>The tool's own: a poll and a look at every command's cadence (<see cref="StopCadence"/>), a stop waited for 30 s.</summary>
    public static FlowTimings Default { get; } = new(StopCadence.PollEvery, StopCadence.WatchEvery, TimeSpan.FromSeconds(30));
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
/// <param name="log">One line for each command the tool ran for the run.</param>
/// <param name="children">Ends what a killed engine's steps left running, first in every command over a run.</param>
/// <param name="pathBudget">Reckons the run's deepest paths against this machine's limit at its start.</param>
/// <param name="output">Says what the engine's poller met and what it did about it.</param>
/// <param name="gitClient">Reads the checkout a run starts from, by the probe its orient step reads.</param>
/// <param name="agents">Makes and deletes agents: the engine's own read-only agents among them.</param>
/// <param name="agentRunner">Works the run's advisor, whatever the client is.</param>
/// <param name="keyStore">The slot the advisor's model reads its key from.</param>
/// <param name="isolation">Decides the operating system's isolation the advisor's and the reporter's agents run under.</param>
/// <param name="clock">When the run's records are written, and how every wait of these commands is timed.</param>
/// <param name="timings">How often the commands look; <see cref="FlowTimings.Default"/> where not given.</param>
/// <param name="updates">Finds the Claude Code the advisor's and the reporter's orders start, never changing it; null where nothing starts one.</param>
public sealed class FlowService(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IFileSystem fileSystem,
    OrchestrationStore store,
    IProcessIdentity identity,
    FlowSessions sessions,
    IWorkflowValidationService validation,
    IFlowStepRunner runner,
    OrchestrationLog log,
    ChildSessions children,
    IPathBudget pathBudget,
    IHarnessOutput output,
    Git.IGitClient gitClient,
    IAgentService agents,
    Agents.IAgentRunner agentRunner,
    Secrets.AgentKeyStore keyStore,
    Agents.Isolation.IAgentIsolation isolation,
    TimeProvider? clock = null,
    FlowTimings? timings = null,
    Agents.ClaudeCode.ClaudeCodeUpdateService? updates = null) : IFlowService
{
    /// <summary>The command that starts a run.</summary>
    public const string RunCommand = "run-orchestrator";

    /// <summary>The command that goes on with one.</summary>
    public const string ResumeCommand = "resume-orchestrator";

    /// <summary>The command that answers a question of one.</summary>
    public const string AnswerCommand = "answer-orchestrator";

    /// <summary>The command that amends the request one was started with.</summary>
    public const string AmendCommand = "amend-orchestrator-request";

    /// <summary>The command that asks the safe pause of one.</summary>
    public const string PauseCommand = "pause-orchestrator";

    /// <summary>The command that stops one where it stands.</summary>
    public const string StopCommand = "stop-orchestrator";

    /// <summary>The command that asks the tool's own question of a run.</summary>
    public const string AskStatusCommand = "ask-orchestrator-status";

    /// <summary>The command that asks a person's question of a run.</summary>
    public const string AskCommand = "ask-orchestrator";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostPlatform _platform = platform;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly OrchestrationStore _store = store;
    private readonly IProcessIdentity _identity = identity;
    private readonly FlowSessions _sessions = sessions;
    private readonly IWorkflowValidationService _validation = validation;
    private readonly IFlowStepRunner _runner = runner;
    private readonly OrchestrationLog _log = log;
    private readonly ChildSessions _children = children;
    private readonly IPathBudget _pathBudget = pathBudget;
    private readonly IHarnessOutput _output = output;
    private readonly Git.IGitClient _gitClient = gitClient;
    private readonly IAgentService _agents = agents;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly FlowTimings _timings = timings ?? FlowTimings.Default;
    private readonly FlowRunOwner _owner = new(store, identity, clock);

    /// <summary>How long an answer handed to a running engine waits between looks at what became of it.</summary>
    private static readonly TimeSpan HandOverSlice = TimeSpan.FromMilliseconds(50);

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
        // takes - the engine holds its run to the same - so nothing is written for a start that is refused. The checkout
        // the run starts from is read by the probe its orient step reads, and held to what a run starts from once the
        // run's name, which its pull request's base may read, is chosen.
        IReadOnlyDictionary<string, string> resolved;
        FlowCheckout checkout;

        try
        {
            resolved = FlowInputs.Resolve(workflow, given);
            checkout = await FlowCheckout.ReadAsync(_gitClient, context.Layout.MainCheckoutRoot, workflow, cancellationToken).ConfigureAwait(false);
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
            (layout, runContext) = await _sessions.CreateAsync(
                context.Layout,
                workflow,
                run,
                given,
                checkout.Head,
                checkout.Branch,
                refusal: name => StartRefusal(checkout, workflow, resolved, name)
                    ?? FlowRunPaths.RefusalAtStart(_pathBudget, context, name, workflow, _platform.PlatformKey),
                cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return new CommandOutcome(ex.ExitCode, ex.Message);
        }

        // A base that cannot be told held the checkout's branch to nothing: the start says so, never silently.
        return await SessionAsync(
            context,
            layout,
            RunCommand,
            checkout.BaseUnknown(FlowTemplate.PullRequestBase(workflow, resolved, layout.Name, out _)),
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
            reclaimed = await _sessions.ClaimAsync(context.Layout, layout, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            var outcome = new CommandOutcome(ex.ExitCode, ex.Message);
            return Ended(layout, ResumeCommand, outcome, ResultOf(layout, outcome), json);
        }

        return await SessionAsync(context, layout, ResumeCommand, reclaimed, () => Resumed(layout, digest!), answered: null, interrupts, json, cancellationToken).ConfigureAwait(false);
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
        //
        // A changed answer names none of those: it is asked of a set already answered - the last a person answered,
        // which the command shows and the engine holds to its window and to once.
        FlowQuestionEntry asked;

        if (again)
        {
            if (FlowAnswerRules.LastSetAnswered(questions.Questions) is not { } set)
            {
                return Refuse(layout, AnswerCommand, FlowAnswerRules.NoSetToChange(run), json);
            }

            // The answer is checked against the set's answers before anything of the run is touched, by the one rule
            // the engine holds it to: usage errors, never a run's refusals - and one that is the answer and the text
            // the set already has is one of them.
            if (FlowAnswerRules.Problem(set.Id, set.Options, answer, text, (set.Answers[^1].Answer, set.Answers[^1].Text)) is { } notAChange)
            {
                return CommandOutcome.Usage(notAChange);
            }

            var changes = FlowAnswerRules.TextOf(text);

            // A set is changed one time - the run's updates the authority, by the one rule the engine holds it to - and
            // the command says so as it says every other refusal of the run's own.
            if (FlowAnswerRules.ChangedAlready(run, set.Id, set.Answers.FirstOrDefault(answer => answer.ReplacedAt is not null)?.ReplacedAt, _store.ReadFlowUpdates(layout)?.Updates ?? []) is { } once)
            {
                return Refuse(layout, AnswerCommand, once, json);
            }

            // The definition the run began with says whether it takes a changed answer at all; where it does not, the
            // refusal is the command's, and nothing is started for it.
            var frozen = _store.ReadDefinition(layout)
                ?? throw new HarnessException(HarnessExit.Refused, $"'{layout.DefinitionFile}' is not there, so run '{layout.Name}' holds no definition to go on with");

            if (frozen.Amend is not { ChangedAnswerMinutes: not null })
            {
                return Refuse(layout, AnswerCommand, $"orchestrator '{frozen.Name}' takes no changed answer: its amend names no changedAnswerMinutes", json);
            }

            // Where an engine is running the run, the change is handed to it and the command returns: the engine takes
            // it at its poll, within the set's window as from the moment stamped here.
            if (_owner.Live(layout) is not null)
            {
                return HandChangeOver(layout, run, set, answer, changes, json);
            }

            // The session is held to the definition it began with, as a resume is, and begins with the change: the
            // engine holds the set to its window and to once, and the change goes by the gates every update passes.
            var (digestOfChange, changedDefinition) = await EntryDigestAsync(context.Layout.RepositoryRoot, layout, cancellationToken).ConfigureAwait(false);

            if (changedDefinition is not null)
            {
                return Ended(layout, AnswerCommand, changedDefinition, ResultOf(layout, changedDefinition), json);
            }

            string? reclaimedChange;

            try
            {
                reclaimedChange = await _sessions.ClaimAsync(context.Layout, layout, cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex)
            {
                var outcome = new CommandOutcome(ex.ExitCode, ex.Message);
                return Ended(layout, AnswerCommand, outcome, ResultOf(layout, outcome), json);
            }

            return await SessionAsync(
                context,
                layout,
                AnswerCommand,
                reclaimedChange,
                () => Resumed(layout, digestOfChange!),
                (set.Id, answer, changes, null),
                interrupts,
                json,
                cancellationToken,
                again: true).ConfigureAwait(false);
        }

        if (question is { } named)
        {
            if (questions.Of(named) is not { } entry)
            {
                return Refuse(layout, AnswerCommand, $"run '{run}' holds no question '{named}'", json);
            }

            if (entry.State != "open")
            {
                return Refuse(layout, AnswerCommand, $"question '{named}' of run '{run}' is {entry.State}{(open.Count > 0 ? $"; what is open now: {string.Join(", ", open.Select(one => one.Id))}" : string.Empty)}", json);
            }

            asked = entry;
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

        // The answer is checked against the question's answers before anything of the run is touched, by the one rule
        // the engine holds it to: a usage error, never a run's refusal.
        if (FlowAnswerRules.Problem(asked.Id, asked.Options, answer, text) is { } wrong)
        {
            return CommandOutcome.Usage(wrong);
        }

        var takes = FlowAnswerRules.TextOf(text);

        // Where an engine is running the run, the answer is handed to it and the command returns: it lands in the
        // inbox under an id of its own, the engine takes it at its poll, and no second engine is started. An engine
        // whose session ends before it took it leaves it there, and the next session of the run takes it - this
        // command waits out that taking, and where the owner went with its session, so that no session is left to
        // read the inbox, this command is that next session itself, and its answer is the entry it handed over.
        string? handedUnder = null;

        if (_owner.Live(layout) is not null)
        {
            var (ended, entry) = await HandOverAsync(layout, run, asked, answer, takes, json, cancellationToken).ConfigureAwait(false);

            if (ended is not null)
            {
                return ended;
            }

            handedUnder = entry;
        }

        // A refusal before the session begins leaves an answer handed over where it is: in the inbox, for the next
        // session of the run to take - said, and the inbox named.
        CommandOutcome NotBegun(CommandOutcome outcome)
        {
            if (handedUnder is null)
            {
                return Ended(layout, AnswerCommand, outcome, ResultOf(layout, outcome), json);
            }

            var left = outcome with
            {
                Details =
                [
                    .. outcome.Details ?? [],
                    $"the answer '{answer}' handed over for question '{asked.Id}' stays in '{layout.InboxFile}', and the next session of the run takes it",
                    $"inbox: {layout.InboxFile}",
                ],
            };

            return Ended(layout, AnswerCommand, left, ResultOf(layout, left) with { Records = [new FlowRecordPath("inbox", layout.InboxFile)] }, json);
        }

        // The session is held to the definition it began with, as a resume is.
        string? digest;
        string? reclaimed;

        try
        {
            (digest, var changed) = await EntryDigestAsync(context.Layout.RepositoryRoot, layout, cancellationToken).ConfigureAwait(false);

            if (changed is not null)
            {
                return NotBegun(changed);
            }

            reclaimed = await _sessions.ClaimAsync(context.Layout, layout, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return NotBegun(new CommandOutcome(ex.ExitCode, ex.Message));
        }

        // The session begins with the answer: the question it names, answered by a person through this command - under
        // the inbox entry it was handed over under, where it was, so the run weighs that entry once - and everything
        // that can run after it.
        return await SessionAsync(context, layout, AnswerCommand, reclaimed, () => Resumed(layout, digest!), (asked.Id, answer, takes, handedUnder), interrupts, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands an answer to the engine running the run: written to the inbox under an id of its own, then waited for - on
    /// the service's clock, at most two of the engine's polls - until the run's questions say what became of that entry,
    /// or the engine's owner is gone. Taken, the command answers so; refused - the question answered by another answer
    /// first, or no longer asked - it answers the run's refusal; neither, with the engine still running, it answers that
    /// the engine takes it at its poll. Every answer names the inbox, a cancelled wait's among them, for the entry stands.
    /// </summary>
    /// <returns>The command's answer where it ends here; else, the owner gone and the entry not weighed, the entry's id, which the command's own session takes.</returns>
    private async Task<(CommandOutcome? Ended, string Entry)> HandOverAsync(OrchestratorLayout layout, string run, FlowQuestionEntry asked, string answer, string? text, bool json, CancellationToken cancellationToken)
    {
        var entry = FlowInboxAnswer.NewId();
        _store.AddToInbox(layout, new FlowInboxAnswer(entry, asked.Id, answer, _clock.GetUtcNow(), text));

        var records = new[] { new FlowRecordPath("inbox", layout.InboxFile) };
        var handed = $"handed '{answer}' for question '{asked.Id}' to the engine running '{run}'";
        (FlowQuestionEntry? Took, FlowRefusedAnswer? Refused) Weighed() => SafeRead(() => _store.ReadFlowQuestions(layout))?.Weighed(entry) ?? (null, null);

        var weighed = Weighed();

        try
        {
            for (var waited = TimeSpan.Zero; weighed is (null, null) && waited < _timings.PollEvery + _timings.PollEvery && _owner.Live(layout) is not null; waited += HandOverSlice)
            {
                await Task.Delay(HandOverSlice, _clock, cancellationToken).ConfigureAwait(false);
                weighed = Weighed();
            }
        }
        catch (OperationCanceledException)
        {
            var cancelled = CommandOutcome.Failed(
                HarnessExit.Cancelled,
                $"waiting for the engine running '{run}' to take '{answer}' for question '{asked.Id}' was cancelled; the answer stays handed over, and the engine takes it at its poll",
                [$"inbox: {layout.InboxFile}"]);

            return (Ended(layout, AnswerCommand, cancelled, ResultOf(layout, cancelled) with { Records = records }, json), entry);
        }

        if (weighed.Took is not null)
        {
            var taken = new CommandOutcome(
                HarnessExit.Success,
                handed,
                [
                    $"the engine took it within {_timings.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s, while the question was open",
                    $"inbox: {layout.InboxFile}",
                ]);

            return (Ended(layout, AnswerCommand, taken, ResultOf(layout, taken) with { Records = records }, json), entry);
        }

        if (weighed.Refused is { } refusal)
        {
            var refused = CommandOutcome.Refused(
                $"the engine running '{run}' did not take '{answer}' for question '{asked.Id}': {refusal.Why}",
                [$"questions: {layout.QuestionsFile}", $"inbox: {layout.InboxFile}"]);

            return (Ended(layout, AnswerCommand, refused, ResultOf(layout, refused) with { Records = [new FlowRecordPath("questions", layout.QuestionsFile), .. records] }, json), entry);
        }

        if (_owner.Live(layout) is not null)
        {
            var reads = new CommandOutcome(
                HarnessExit.Success,
                handed,
                [
                    $"the engine reads it within {_timings.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s and takes it while the question is open; where its session ends first, the next session of the run takes it",
                    $"inbox: {layout.InboxFile}",
                ]);

            return (Ended(layout, AnswerCommand, reads, ResultOf(layout, reads) with { Records = records }, json), entry);
        }

        return (null, entry);
    }

    /// <summary>Holds a changed answer to the engine running the run, under an id of its own, and returns: the engine takes it at its poll, within the set's window as from the moment stamped here.</summary>
    /// <param name="layout">The run's layout.</param>
    /// <param name="run">The run's name.</param>
    /// <param name="set">The set the answer changes.</param>
    /// <param name="answer">The new answer, spelled as the set's options spell it.</param>
    /// <param name="text">The text given with it, where the answer takes one.</param>
    /// <param name="json">Whether the command answers in its own JSON shape.</param>
    private CommandOutcome HandChangeOver(OrchestratorLayout layout, string run, FlowQuestionEntry set, string answer, string? text, bool json)
    {
        _store.AddToInbox(layout, new FlowInboxAnswer(FlowInboxAnswer.NewId(), set.Id, answer, _clock.GetUtcNow(), text, Again: true));

        var handed = new CommandOutcome(
            HarnessExit.Success,
            $"handed '{answer}' to change the answer of question '{set.Id}' to the engine running '{run}'",
            [
                $"the engine reads it within {_timings.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s and takes it while the set is still the last a person answered and inside its window; where its session ends first, the next session of the run takes it",
                $"inbox: {layout.InboxFile}",
            ]);

        return Ended(layout, AnswerCommand, handed, ResultOf(layout, handed) with { Records = [new FlowRecordPath("inbox", layout.InboxFile)] }, json);
    }

    /// <inheritdoc/>
    public async Task<CommandOutcome> AmendAsync(string startDirectory, string run, string? text, string? textFile, bool json, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        if (text is not null && textFile is not null)
        {
            return CommandOutcome.Usage("the amendment is given two ways, as the command's own argument and as --file; give one");
        }

        if (textFile is not null)
        {
            var (read, problem) = FlowInputs.TextOf(_fileSystem, startDirectory, textFile, "--file");

            if (problem is not null)
            {
                return CommandOutcome.Usage(problem);
            }

            text = read;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return CommandOutcome.Usage("the amendment is empty; give one, as the command's own argument or in a file named by --file");
        }

        var (refused, context, layout) = await LoadAsync(startDirectory, run, AmendCommand, cancellationToken).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        var definition = _store.ReadDefinition(layout)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{layout.DefinitionFile}' is not there, so run '{layout.Name}' holds no definition to go on with");

        if (definition.Amend is not { Input: not null })
        {
            return Refuse(layout, AmendCommand, $"orchestrator '{definition.Name}' takes no amended request: its amend names no input", json);
        }

        var flow = _store.ReadFlow(layout)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{layout.FlowFile}' is not there, so run '{layout.Name}' holds no state to weigh an amendment against");

        // The gates every change passes, read of the run as it stands; nothing is recorded for one they refuse.
        if (FlowUpdateGates.Refused(definition, flow, _store.ReadFlowUpdates(layout), FlowUpdateCauses.Amendment) is { } gate)
        {
            return Refuse(layout, AmendCommand, gate, json);
        }

        // Where an engine is running the run, the amendment is handed to it and the command returns: it lands in the
        // inbox under an id of its own, the engine weighs it against the same gates at its poll, and no second engine
        // is started. An engine whose session ends before it took it leaves it there, and the next session takes it.
        // Where none is, the amendment is recorded queued, and taken first when the run is next gone on with. Which of
        // the two is decided in the repository's one section, where every session claims the run: an engine that starts
        // meanwhile claims the run either before - and the amendment is handed to it - or after the entry is written,
        // and reads it then; never between, where it would write the run's updates whole over an entry it never read.
        var given = _clock.GetUtcNow();
        var entry = _sessions.AcrossRuns(context!.Layout, () =>
        {
            if (_owner.Live(layout) is not null)
            {
                _store.AddAmendmentToInbox(layout, new FlowInboxAmendment(FlowInboxAmendment.NewId(), text, given, AmendCommand));
                return null;
            }

            return _store.AddUpdate(layout, run, given, id => new FlowUpdateEntry(id, FlowUpdateCauses.Amendment, FlowUpdateStates.Queued, given, Amendment: text, Through: AmendCommand));
        });

        if (entry is null)
        {
            var handed = new CommandOutcome(
                HarnessExit.Success,
                $"handed the amendment to the engine running '{run}'",
                [
                    $"the engine reads it within {_timings.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s and takes it where the run can carry it; where its session ends first, the next session of the run takes it",
                    $"inbox: {layout.InboxFile}",
                ]);

            return Ended(layout, AmendCommand, handed, ResultOf(layout, handed) with { Records = [new FlowRecordPath("inbox", layout.InboxFile)] }, json);
        }

        // No engine runs the run: the amendment is recorded, and taken first when the run is next gone on with - by
        // resume-orchestrator or by an answer. The command starts nothing, and says so.
        var recorded = new CommandOutcome(
            HarnessExit.Success,
            $"the amendment is recorded as update {entry.Id} of run '{run}', and is taken first when the run is next gone on with",
            [
                "this command starts nothing; resume-orchestrator, or an answer, takes it before anything else runs",
                $"updates: {layout.UpdatesFile}",
            ]);

        return Ended(layout, AmendCommand, recorded, ResultOf(layout, recorded) with { Records = [new FlowRecordPath("updates", layout.UpdatesFile)] }, json);
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

    /// <inheritdoc/>
    public Task<CommandOutcome> AskStatusAsync(string startDirectory, string run, bool json, CancellationToken cancellationToken = default)
        => AskAsync(startDirectory, run, question: null, json, cancellationToken);

    /// <inheritdoc/>
    public async Task<CommandOutcome> AskAsync(string startDirectory, string run, string? question, bool json, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);

        if (question is { Length: 0 })
        {
            return CommandOutcome.Usage("the question is empty: ask-orchestrator takes it as its argument, in the person's words");
        }

        if (OrchestrationRules.NameProblem(run) is { } shape)
        {
            return CommandOutcome.Usage(shape);
        }

        // Both commands only read: no sweep of what a killed engine's steps left running, no lock, no line in the run's
        // log - what a call leaves behind is its own record, under the run's asks/ alone.
        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, run);

        if (_store.ReadOrchestrator(layout) is null)
        {
            return CommandOutcome.Refused(OrchestrationReports.NoOrchestrator(run));
        }

        if (_store.ReadFlow(layout) is not { } flow)
        {
            return HandMade(layout, question is not null, json);
        }

        var definition = _store.ReadDefinition(layout);

        if (definition is null)
        {
            return CommandOutcome.Refused($"'{layout.DefinitionFile}' is not there, so run '{run}' holds no definition to ask about");
        }

        var asOf = _clock.GetUtcNow();
        var engine = OrchestratorService.EngineOf(_store, _owner, layout, flow);
        var facts = FlowStatus.Facts(definition, flow, _store.ReadFlowQuestions(layout), _store.ReadFlowUpdates(layout), engine, asOf);
        var text = string.Join("\n", facts);

        // The call's own record, in a directory the engine never reads: the facts as given, and what asking cost. The
        // files of the reporter's own attempt, where one runs, are kept by its runner under the same directory.
        var stamp = $"{asOf.ToUniversalTime():yyyyMMddTHHmmssZ}-{Random.Shared.Next(0x10000, 0xFFFFF).ToString("x", CultureInfo.InvariantCulture)}";
        var asks = Path.Combine(layout.AsksDirectory, stamp);
        _fileSystem.WriteAllTextAtomic(Path.Combine(asks, "facts.md"), text + "\n");

        if (definition.Reporter is not { } reporter)
        {
            var none = "the workflow names no reporter, so no model words the run's facts";

            WriteAsk(layout, asks, question, model: null, spent: null, none);

            return question is null
                ? Answered(layout, CommandOutcome.Ok(facts[0], [.. facts.Skip(1), string.Empty, $"no model worded this: {none}"]), asks, text, null, null, null, null, json)
                : CommandOutcome.Failed(
                    HarnessExit.ConfigInvalid,
                    $"ask-orchestrator was refused: {none}",
                    [$"record: {Path.Combine(asks, AskFileName)}"]);
        }

        var reported = await new ReporterRunner(_contextLoader, _platform, agentRunner, _fileSystem, keyStore, isolation, updates).ReportAsync(
            new FlowReporterCall(
                run,
                definition.Name,
                reporter,
                text,
                question,
                layout.Directory,
                stamp,
                new FlowRunPlace(context.Layout.MainCheckoutRoot, layout.Name, flow.StartCommit, definition, flow.Branch)),
            cancellationToken).ConfigureAwait(false);

        // What the answer spent, where any turn of the reporter's was reported: the dollars as far as they were counted,
        // and why the rest is not known where a turn's cost could not be.
        (FlowDollars Dollars, long Tokens)? spent = reported.Tokens is { } used ? (reported.Dollars, used) : null;
        WriteAsk(layout, asks, question, reported.Model, spent, reported.WhyNot);

        if (question is null)
        {
            return Answered(
                layout,
                reported.Answer is { } worded
                    ? CommandOutcome.Ok(facts[0], [.. facts.Skip(1), string.Empty, worded])
                    : CommandOutcome.Ok(facts[0], [.. facts.Skip(1), string.Empty, $"no model worded this: {reported.WhyNot}"]),
                asks,
                text,
                reported.Answer,
                reported.WhyNot,
                reported.Model,
                spent,
                json);
        }

        return reported.Answer is { } answered
            ? Answered(
                layout,
                CommandOutcome.Ok($"run '{run}' answered the question, worded by {reported.Model ?? "its reporter"}", [.. facts, string.Empty, answered]),
                asks,
                text,
                answered,
                null,
                reported.Model,
                spent,
                json)
            : CommandOutcome.Failed(
                reported.ExitCode ?? HarnessExit.CommandFailed,
                $"ask-orchestrator was refused: no model worded an answer: {reported.WhyNot}",
                [.. facts, string.Empty, $"record: {Path.Combine(asks, AskFileName)}"]);
    }

    /// <summary>The name of the file one call of the two ask commands keeps its own record in.</summary>
    private const string AskFileName = "ask.json";

    /// <summary>
    /// What one of the two ask commands answers once the run's facts are read: its own record named, what the answer
    /// cost - the person's, never the run's - and, where <c>--json</c> was asked for, the facts and the answer apart.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    /// <param name="outcome">The outcome as the facts and the answer leave it.</param>
    /// <param name="asks">The call's own directory under the run's <c>asks/</c>.</param>
    /// <param name="facts">The facts as text.</param>
    /// <param name="answer">The reporter's answer; null where none worded them.</param>
    /// <param name="whyNot">Why no model worded them; null where one did.</param>
    /// <param name="model">The model that served the reporter, where one did.</param>
    /// <param name="cost">What the answer spent - its dollars as far as they were counted, and why the rest is not known where a turn's cost could not be, and the tokens its clients reported; null where nothing was reported.</param>
    /// <param name="json">Whether to answer with one JSON document.</param>
    private CommandOutcome Answered(
        OrchestratorLayout layout,
        CommandOutcome outcome,
        string asks,
        string facts,
        string? answer,
        string? whyNot,
        string? model,
        (FlowDollars Dollars, long Tokens)? cost,
        bool json)
    {
        var said = outcome with
        {
            Details =
            [
                .. outcome.Details ?? [],
                string.Empty,
                cost is not null || model is { Length: > 0 }
                    ? $"This answer cost {cost?.Dollars.Said(format: "0.####") ?? "an unknown amount"}{(model is { Length: > 0 } ? $" ({model}{(cost is { Tokens: var tokens } ? $", {tokens.ToString(CultureInfo.InvariantCulture)} tokens" : string.Empty)})" : string.Empty)}. It is not counted in the run's budget."
                    : "This answer cost nothing: no model worded it.",
                $"Record: {asks}",
            ],
        };

        return json
            ? said with
            {
                Data = [ReportJson.Stopped(said.ExitCode, said.Message, FlowReports.AskDocument(facts, answer, whyNot, model, cost?.Dollars, cost?.Tokens, asks))],
                Quiet = true,
            }
            : said;
    }

    /// <summary>
    /// Writes the call's own record, <c>ask.json</c>: the question, the model, the tokens, the cost - its dollars as far as
    /// they were counted, and why the rest is not known where a turn's cost could not be - or why no model ran.
    /// </summary>
    private void WriteAsk(OrchestratorLayout layout, string asks, string? question, string? model, (FlowDollars Dollars, long Tokens)? spent, string? whyNot)
    {
        var record = new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = 1,
            ["run"] = layout.Name,
            ["command"] = question is null ? AskStatusCommand : AskCommand,
            ["at"] = FlowTimeConverter.Written(_clock.GetUtcNow()),
            ["question"] = question,
            ["model"] = model,
            ["usd"] = spent?.Dollars.Counted,
            ["tokens"] = spent?.Tokens,
            ["whyNot"] = whyNot,
        };

        if (spent?.Dollars.Uncounted is { } uncounted)
        {
            record["uncounted"] = uncounted;
        }

        JsonStateFile.Write(_fileSystem, Path.Combine(asks, AskFileName), record);
    }

    /// <summary>
    /// What the two ask commands answer for an orchestrator that holds no run - one made by hand, which no flow runs:
    /// the orchestrator's own facts, said to be such, and no reporter's words, for it names none.
    /// </summary>
    /// <param name="layout">The orchestrator's layout.</param>
    /// <param name="asked">Whether the person's question was asked (<c>ask-orchestrator</c>), refused over the tool's own.</param>
    /// <param name="json">Whether to answer with one JSON document.</param>
    private CommandOutcome HandMade(OrchestratorLayout layout, bool asked, bool json)
    {
        var record = _store.ReadOrchestrator(layout);
        var agents = _store.Agents(layout);
        var facts = new List<string>
        {
            $"orchestrator '{layout.Name}' holds no run: it was made by hand, and these are list-orchestrator's facts of it",
            $"model {record?.Model ?? "unrecorded"}, at most {(record?.Parallel ?? OrchestratorRecord.DefaultParallel).ToString(CultureInfo.InvariantCulture)} agents open",
            agents.Count == 0
                ? "no agents"
                : $"agents: {string.Join(", ", agents.Select(agent => $"{agent.Name} ({(agent.Record?.State ?? "unreadable").ToLower(CultureInfo.InvariantCulture)})"))}",
            "list-orchestrator names its agents' worktrees",
        };

        // The call's own record, as every call keeps one: the orchestrator's own facts, and why no model worded them.
        var now = _clock.GetUtcNow();
        var asks = Path.Combine(layout.AsksDirectory, $"{now.ToUniversalTime():yyyyMMddTHHmmssZ}-{Random.Shared.Next(0x10000, 0xFFFFF).ToString("x", CultureInfo.InvariantCulture)}");
        _fileSystem.WriteAllTextAtomic(Path.Combine(asks, "facts.md"), string.Join("\n", facts) + "\n");
        WriteAsk(layout, asks, question: null, model: null, spent: null, whyNot: "the orchestrator holds no run");

        var text = string.Join("\n", facts);

        if (!asked)
        {
            return Answered(layout, CommandOutcome.Ok(facts[0], [.. facts.Skip(1)]), asks, text, null, "the orchestrator holds no run", null, null, json);
        }

        var refused = CommandOutcome.Failed(
            HarnessExit.ConfigInvalid,
            $"ask-orchestrator was refused: orchestrator '{layout.Name}' holds no run, so there is nothing to answer from and no reporter to answer",
            [.. facts, string.Empty, $"record: {asks}"]);

        return json
            ? refused with { Data = [ReportJson.Stopped(refused.ExitCode, refused.Message, FlowReports.AskDocument(text, null, "the orchestrator holds no run", null, null, null, asks))], Quiet = true }
            : refused;
    }

    /// <summary>
    /// Why run <paramref name="run"/> of <paramref name="workflow"/> cannot start from <paramref name="checkout"/>: its
    /// pull request's base cannot be filled, or the checkout is not one a run starts from (<see cref="FlowCheckout.StartRefusal"/>,
    /// read against that base, else the remote's default branch); null where it can.
    /// </summary>
    internal static string? StartRefusal(FlowCheckout checkout, WorkflowDefinition workflow, IReadOnlyDictionary<string, string> inputs, string run)
    {
        var filled = FlowTemplate.PullRequestBase(workflow, inputs, run, out var problem);
        var refusal = problem is not null
            ? $"its pull request's base cannot be filled: {problem}"
            : checkout.StartRefusal(filled);

        return refusal is null ? null : $"run '{run}' of '{workflow.Name}' cannot start: {refusal}. Nothing was made.";
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
    /// A run fact a template reads that the run's own state does not hold (part 03): where the step's instance works -
    /// its agent's worktree, the scratch and plans the run keeps for that agent, the rows it files - and this machine.
    /// Null for every other name, which reads nothing as it did.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="layout">The run's own places.</param>
    /// <param name="instance">The instance whose template is filled.</param>
    /// <param name="fact">The fact's name.</param>
    private string? RunFactOf(HarnessContext context, OrchestratorLayout layout, FlowInstance instance, string fact)
    {
        var agent = instance.WorksIn ?? instance.Made;

        return fact switch
        {
            "os" => _platform.PlatformKey,
            "worktree" when agent is not null => _store.ReadAgent(layout, agent)?.WorktreePath(context.Layout),
            "work" when agent is not null => layout.WorkDirectory(agent),
            "plans" when agent is not null => layout.PlansDirectory(agent),
            "rows" when agent is not null => layout.RowsDirectory(agent),
            _ => null,
        };
    }

    /// <summary>
    /// One session of a run, from its owner claimed to its end: the engine built over the run's records, the answer it
    /// begins with, what was handed to an engine that ended before it took it, then driven - again, where an answer
    /// handed over as it ended settles the question it paused on - and the report written. However it ends - a
    /// refusal, a defect - the owner is released and the outcome is in the run's log. An answer it begins with that the
    /// command handed over first (<c>Inbox</c>, the entry's id) is that entry: weighed under its id, so the run records
    /// one outcome of it, and the inbox named on every exit.
    /// </summary>
    private async Task<CommandOutcome> SessionAsync(
        HarnessContext context,
        OrchestratorLayout layout,
        string command,
        string? opening,
        Func<(WorkflowDefinition Definition, FlowRunContext Context, FlowResumeState? Resume)> begin,
        (string Question, string Answer, string? Text, string? Inbox)? answered,
        FlowInterrupts? interrupts,
        bool json,
        CancellationToken cancellationToken,
        bool again = false)
    {
        var said = new List<string>();
        var inbox = answered?.Inbox is not null;
        var offered = new Offered();

        // What the session says before anything else: a dead owner it reclaimed, or what a start could not hold its
        // checkout to.
        if (opening is not null)
        {
            said.Add(opening);
        }

        // Cut off by its terminal, the session answers for the run as every command over it does, the session itself
        // left where it stands.
        interrupts?.CutOffAnsweredBy(drain => CutOff(layout, command, drain, inbox, json));

        try
        {
            var (definition, runContext, resume) = begin();

            using var records = new FlowRunRecords(_fileSystem, _store, layout, runContext, _clock);

            // Every step is handed the run it belongs to, and each create-agent step the model its agent is made for,
            // resolved from the configuration as the definition's check resolves it; a run step claims its tree as the
            // configuration's runners say a run of it writes there.
            var coordinator = new FlowCoordinator(
                definition,
                _runner,
                new ConfiguredRunnerFacts(context.Config),
                _clock,
                runContext.Inputs,
                records,
                resume,
                layout.Name,
                new FlowRunPlace(context.Layout.MainCheckoutRoot, layout.Name, runContext.StartCommit, definition, runContext.Branch),
                Agents.AgentProviders.Resolve(context.Config).Providers is { } providers
                    ? WorkflowValidator.AgentModels(
                        definition,
                        providers,
                        named => new WorkflowNamedFiles(_fileSystem, _platform.PathComparison).ReadText(context.Layout, definition.Name, named, "an agent definition"))
                    : null,
                named => new WorkflowNamedFiles(_fileSystem, _platform.PathComparison).ReadText(context.Layout, definition.Name, named, "a workflow file"),
                async (run, agent, token) =>
                {
                    // The engine's own deleting of a read-only agent whose work is over: applied, with nothing to fold
                    // and no worktree to remove, and its failure - refused, or thrown - said in the run's log, for the
                    // run goes on whatever it did. An agent with no record is deleted already, by an earlier session of
                    // the run's or by its person, and nothing is left to delete or to say.
                    CommandOutcome outcome;

                    try
                    {
                        if (_store.ReadAgent(layout, agent) is null)
                        {
                            return CommandOutcome.Ok($"agent '{agent}' of '{layout.Name}' is deleted already");
                        }

                        outcome = (await _agents.DeleteAsync(
                            new DeleteAgentRequest(context.Layout.MainCheckoutRoot, layout.Name, agent, new FoldAllowances(), Apply: true, DiscardUncommitted: false),
                            token).ConfigureAwait(false)).Outcome;
                    }
                    catch (HarnessException refused)
                    {
                        outcome = CommandOutcome.Failed(refused.ExitCode, refused.Message);
                    }
                    catch (Exception failed) when (failed is not OperationCanceledException)
                    {
                        outcome = CommandOutcome.Failed(HarnessExit.InternalError, $"deleting agent '{agent}' failed: {failed.Message}");
                    }

                    if (!outcome.Succeeded)
                    {
                        _ = _log.Append(layout, agent, "delete-agent", outcome);
                    }

                    return outcome;
                },
                new AdvisorRunner(_contextLoader, _platform, _store, agentRunner, _fileSystem, keyStore, _output, isolation, updates),
                updates: () => _store.ReadFlowUpdates(layout),
                workflowFileIfThere: named => new WorkflowNamedFiles(_fileSystem, _platform.PathComparison).ReadTextIfThere(context.Layout, definition.Name, named, "a workflow file"),
                runFacts: (instance, fact) => RunFactOf(context, layout, instance, fact),
                agentLive: agent => _store.ReadAgent(layout, agent) is { State: Orchestration.AgentStates.Live });
            var before = _store.ReadFlowQuestions(layout);

            // What a session before this one recorded and left queued - an amendment given while no engine ran - is
            // taken before anything else, for the next session of the run takes it first.
            said.AddRange(coordinator.TakeQueuedUpdates());

            if (answered is { } given)
            {
                // The session begins with the command's own act: an answer to its open question, or - where it changes
                // one already given - a change held to its window and to once by the engine, which says why where it
                // was not taken. An answer the command handed over first is its inbox entry, weighed under the entry's
                // id by the one owner of an entry weighed once: taken or refused, the run's records hold it under that
                // id, so the inbox's pass below never weighs it a second time, and the poller empties it as settled.
                string? problem;
                bool taken;

                if (again)
                {
                    taken = coordinator.TryChangeAnswer(given.Answer, given.Text, out problem, AnswerCommand, given.Question);
                }
                else if (given.Inbox is { } entry)
                {
                    var handed = coordinator.AnswerHandedOver(given.Question, given.Answer, given.Text, out problem, AnswerCommand, entry);
                    offered[entry] = Task.FromResult(handed);
                    taken = handed != FlowHandedOver.Refused;
                }
                else
                {
                    taken = coordinator.TryAnswer(given.Question, given.Answer, given.Text, out problem, AnswerCommand);
                }

                if (!taken)
                {
                    throw new HarnessException(HarnessExit.Refused, problem!);
                }
            }

            // What was handed to an engine whose session ended before it took it is this session's to take - and what
            // cannot be read of it is said and left for the next session: the command's own answer, recorded above,
            // stands, and the run is driven on. What it takes, the poller empties from the inbox at its first read.
            inbox |= Guarded("handed to the engine as its session began", said, () => TakeInbox(layout, coordinator, offered, said)).Seen > 0;

            FlowSessionEnd end;
            string? unemptied;

            while (true)
            {
                end = await DriveAsync(layout, command, coordinator, interrupts, offered, cancellationToken).ConfigureAwait(false);

                // The last poll, the poller stopped: where an answer it takes settles the question the session paused
                // on, the run is driven again - whatever emptying the inbox of it then meets, for it is taken.
                var (taken, seen) = LastPoll(layout, coordinator, offered, said);
                inbox |= seen > 0;
                unemptied = Empty(layout, offered);

                if (taken > 0 && end is { Status: FlowRunStatus.Paused, Pause: null })
                {
                    continue;
                }

                break;
            }

            // An answer offered that the session never settled stays for the next session; one the session settled - taken,
            // or refused and recorded - that the inbox would not let go is said apart, for it was weighed: the run's records
            // hold it under its entry's id, so the next session only empties it from the inbox.
            inbox |= SayUnsettled(layout, offered, said);
            inbox |= SayUnemptied(layout, offered, unemptied, said);

            var flow = _store.ReadFlow(layout)
                ?? throw new HarnessException(HarnessExit.Refused, $"'{layout.FlowFile}' is not there after the session of run '{layout.Name}', which writes it at every change");
            var questions = _store.ReadFlowQuestions(layout);
            var exit = ExitOf(end, flow);
            var left = FlowReports.Write(_fileSystem, layout, flow, questions, end.Summary, exit, _clock.GetUtcNow(), _store.ReadFlowUpdates(layout));
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
            // last session's, for a refused session is none. An answer handed over that a refused start weighed was
            // handed back, for the start wrote nothing: it stays in the inbox, said as one the session never settled.
            // The command's own answer, handed over before its session began and never weighed - the session was refused
            // first - stays there too; one weighed and refused is settled under its entry's id, and leaves the inbox now
            // as what a session settled does at its end.
            inbox |= SayUnsettled(layout, offered, said);

            if (answered is { Inbox: { } own } unweighed && !offered.ContainsKey(own))
            {
                said.Add($"the answer '{unweighed.Answer}' handed over for question '{unweighed.Question}' stays in '{layout.InboxFile}', and the next session of the run takes it");
            }

            inbox |= SayUnemptied(layout, offered, Empty(layout, offered), said);
            var flow = SafeRead(() => _store.ReadFlow(layout));
            IReadOnlyList<FlowRecordPath> records = flow is not null ? RecordsOf(layout, inbox) : inbox ? [new FlowRecordPath("inbox", layout.InboxFile)] : [];
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
            // The session's interrupts are counted for the whole of it, every drive of it alike, and cleared at its end.
            interrupts?.SessionEnded();
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
        Offered offered,
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
    /// or a stop in <c>stop.json</c>, an answer, a changed answer or an amendment in <c>inbox.json</c> - and hands each to
    /// the engine, which alone applies it. It never waits on what it handed: each is offered, and emptied from the inbox
    /// once the loop has taken or refused it. What a tick meets that can pass - a file being replaced, a lock held - is said once and read again
    /// at the next; anything else is said, and the run is asked to pause rather than be left with nobody reading what
    /// is asked of it.
    /// </summary>
    private async Task PollAsync(OrchestratorLayout layout, string command, FlowCoordinator coordinator, Offered offered, CancellationToken cancellationToken)
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
                    FlowTerminal.Warn(_output, command, $"what was asked of the engine of run '{layout.Name}' could not be read: {ex.Message.TrimEnd('.')}; it is read again at the next poll");
                }
            }
            catch (Exception ex)
            {
                FlowTerminal.Warn(_output, command, $"the engine of run '{layout.Name}' can no longer read what is asked of it ({ex.GetType().Name}: {ex.Message.TrimEnd('.')}); the run is asked to pause");
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

    /// <summary>
    /// One poll: the request taken and handed on, what the loop has settled emptied from the inbox, and each answer, change
    /// of an answer and amendment not yet offered offered under its entry's id - which the loop weighs once, never again
    /// where the run's records already hold it.
    /// </summary>
    private void Tick(OrchestratorLayout layout, FlowCoordinator coordinator, Offered offered)
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

        var inbox = _store.ReadInbox(layout);

        // Every answer, change of an answer and amendment handed over is offered at the poll that first reads it, as
        // the command that handed it over says: the loop weighs each once, a change within its window as from the
        // moment the command stamped, and an amendment against the gates as the run then stands.
        foreach (var entry in inbox?.Answers ?? [])
        {
            if (!offered.ContainsKey(entry.Id))
            {
                offered[entry.Id] = coordinator.OfferAnswerAsync(entry.Question, entry.Answer, entry.Text, AnswerCommand, entry.Id, entry.Again, entry.At);
            }
        }

        foreach (var entry in inbox?.Amendments ?? [])
        {
            if (!offered.ContainsKey(entry.Id))
            {
                offered[entry.Id] = coordinator.OfferAmendmentAsync(entry.Text, entry.Through ?? AmendCommand, entry.Id, entry.At);
                _ = offered.Amendments.Add(entry.Id);
            }
        }
    }

    /// <summary>
    /// The last poll of a session, its poller stopped: a request asked as the session ended was asked of this session and
    /// of none after it, and an answer the loop never weighed - never offered it, or handed back - is taken here. What
    /// cannot be read is said and left for the next session of the run, and the session's end stands. Emptying the inbox
    /// of what the session settled is <see cref="Empty"/>'s, after, apart from the take: a removal that fails never
    /// unsays an answer taken.
    /// </summary>
    /// <returns>How many answers were taken, and how many were read.</returns>
    private (int Taken, int Seen) LastPoll(OrchestratorLayout layout, FlowCoordinator coordinator, Offered offered, List<string> said)
        => Guarded("asked of the engine as its session ended", said, () =>
        {
            if (_store.TakeStopFile(layout) is { } late)
            {
                said.Add($"a {(late.Pause is not null ? "pause" : "stop")} asked as the session ended was taken with it");
            }

            return TakeInbox(layout, coordinator, offered, said);
        });

    /// <summary>
    /// Empties from the inbox, between two drives, what the session settled (<see cref="Settle"/>), where what the removal
    /// meets is answered and never thrown: an answer taken stays taken, counted, and settled - never offered or taken
    /// again - and its removal is tried again by the next drive's poller, where the session goes on.
    /// </summary>
    /// <returns>Why the inbox could not be emptied of what the session settled; null where it was.</returns>
    private string? Empty(OrchestratorLayout layout, Offered offered)
    {
        try
        {
            Settle(layout, offered);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    /// <summary>
    /// Says the answers handed over that the session never settled - never weighed, or handed back by the loop, as a
    /// refused start hands back what it weighed - each of which stays in the inbox for the next session of the run.
    /// </summary>
    /// <returns>Whether there was one, so the inbox is named among the session's records.</returns>
    private static bool SayUnsettled(OrchestratorLayout layout, Offered offered, List<string> said)
    {
        var handed = offered.Where(pair => !pair.Value.IsCompletedSuccessfully).Select(pair => pair.Key).ToList();

        if (handed.Count == 0)
        {
            return false;
        }

        said.Add($"{offered.Counted(handed)} handed to this engine {(handed.Count == 1 ? "was" : "were")} not taken before its session ended; {(handed.Count == 1 ? "it stays" : "they stay")} in '{layout.InboxFile}', and the next session of the run takes {(handed.Count == 1 ? "it" : "them")}");
        return true;
    }

    /// <summary>
    /// Says each answer or amendment the session settled - taken, or refused and recorded - that the inbox would not let
    /// go (<paramref name="unemptied"/>, why): it was weighed, and the run's records hold it under its entry's id, so the
    /// next session only empties it from the inbox.
    /// </summary>
    /// <returns>Whether there was one, so the inbox is named among the session's records.</returns>
    private static bool SayUnemptied(OrchestratorLayout layout, Offered offered, string? unemptied, List<string> said)
    {
        var settled = offered.Where(pair => pair.Value.IsCompletedSuccessfully).Select(pair => pair.Key).ToList();

        if (unemptied is null || settled.Count == 0)
        {
            return false;
        }

        said.Add($"{offered.Counted(settled)} this session settled could not be emptied from '{layout.InboxFile}': {unemptied}; {(settled.Count == 1 ? "it stays there, and the next session of the run only empties it" : "they stay there, and the next session of the run only empties them")}");
        return true;
    }

    /// <summary>
    /// What a session holds of what was handed to its engine through the inbox, by each entry's id: the answer or the
    /// amendment offered to the loop, or taken between two drives, with how it was settled - and which of them were
    /// amendments, so what is said of them names each by what it is.
    /// </summary>
    private sealed class Offered : Dictionary<string, Task<FlowHandedOver>>
    {
        /// <summary>A session's entries, by their ids, none yet.</summary>
        public Offered()
            : base(StringComparer.Ordinal)
        {
        }

        /// <summary>The ids of the entries that are amendments of the request; every other is an answer.</summary>
        public HashSet<string> Amendments { get; } = new(StringComparer.Ordinal);

        /// <summary>The entries <paramref name="ids"/> counted by what each is: answers and amendments, each kind named once where any is.</summary>
        public string Counted(IReadOnlyCollection<string> ids)
        {
            var amendments = ids.Count(Amendments.Contains);

            return ReportText.Listed([
                .. ids.Count - amendments > 0 ? [ReportText.Counted(ids.Count - amendments, "answer")] : Array.Empty<string>(),
                .. amendments > 0 ? [ReportText.Counted(amendments, "amendment")] : Array.Empty<string>(),
            ]);
        }
    }

    /// <summary>
    /// Reads what was asked of the engine between its drives - at a session's start or its end - where what cannot be
    /// read never ends the session: it is said, as <paramref name="what"/>, and left for the next session of the run.
    /// </summary>
    /// <returns>How many answers were taken, and how many were read - one unread counted as read, for the inbox is named.</returns>
    private static (int Taken, int Seen) Guarded(string what, List<string> said, Func<(int Taken, int Seen)> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            said.Add($"what was {what} could not be read: {ex.Message.TrimEnd('.')}; it stays for the next session of the run");
            return (0, 1);
        }
    }

    /// <summary>
    /// Empties from the inbox each answer the session settled - an offer the loop took, refused or found recorded already,
    /// which it settles only once what became of it is written (a session's start holds its writes, and hands back what
    /// it weighed where it is refused), and one weighed between two drives (<see cref="TakeInbox"/>) - and forgets each
    /// offer the loop handed back, which stays in the inbox for whoever takes it next. The one owner of emptying a
    /// settled answer: it is forgotten only once the inbox has let it go - a removal that fails leaves it settled, tried
    /// again at the next poll or the next drive's end, and never offered to the engine nor taken a second time; one the
    /// inbox still holds as the session ends is settled by the run's records, under its entry's id, so the next session
    /// only empties it.
    /// </summary>
    private void Settle(OrchestratorLayout layout, Offered offered)
    {
        foreach (var (id, offer) in offered.Where(pair => pair.Value.IsCompleted).ToList())
        {
            if (!HandedBack(offer))
            {
                _ = _store.RemoveFromInbox(layout, id);
            }

            _ = offered.Remove(id);
            _ = offered.Amendments.Remove(id);
        }
    }

    /// <summary>Whether the loop handed <paramref name="offer"/> back - cancelled as its session ended, never weighed, or weighed by a start that wrote nothing: the answer is no longer the session's, and stays in the inbox.</summary>
    private static bool HandedBack(Task<FlowHandedOver> offer)
        => offer is { IsCompleted: true, IsCompletedSuccessfully: false };

    /// <summary>
    /// Takes everything in the inbox the session does not hold - never offered to the loop, or handed back by it -
    /// between two drives: each answer weighed by the engine under its entry's id
    /// (<see cref="FlowCoordinator.AnswerHandedOver(string, string, string?, out string?, string?, string?, bool, DateTimeOffset?)"/>) - and each amendment of
    /// the request weighed against the same gates
    /// (<see cref="FlowCoordinator.AmendmentHandedOver(string, string, out string?, string?, DateTimeOffset?, string?)"/>) -
    /// applied as a person's through <c>answer-orchestrator</c> or <c>amend-orchestrator-request</c>; or - where the
    /// run does not take it - said, its refusal recorded as the loop records one it refuses; or, where the run's
    /// records hold it already - a session before this one weighed it, and its removal failed or was cut off - only
    /// emptied from the inbox. Each is settled as one the loop weighed is - held as an offer completed - and counted
    /// then, so <see cref="Settle"/> empties it from the inbox and tries again where that fails, and nothing takes or
    /// offers it a second time.
    /// </summary>
    /// <returns>How many were taken, and how many were read.</returns>
    private (int Taken, int Seen) TakeInbox(OrchestratorLayout layout, FlowCoordinator coordinator, Offered offered, List<string> said)
    {
        var taken = 0;
        var seen = 0;
        var held = _store.ReadInbox(layout);

        foreach (var entry in held?.Answers ?? [])
        {
            if (offered.TryGetValue(entry.Id, out var offer) && !HandedBack(offer))
            {
                // The loop's to weigh still, or settled already and waiting to leave the inbox.
                continue;
            }

            seen++;
            var handed = coordinator.AnswerHandedOver(entry.Question, entry.Answer, entry.Text, out var problem, AnswerCommand, entry.Id, entry.Again, entry.At);
            var about = $"the answer '{entry.Answer}' handed over for question '{entry.Question}'";

            switch (handed)
            {
                case FlowHandedOver.Taken:
                    taken++;
                    said.Add($"{about} was taken from the inbox");
                    break;
                case FlowHandedOver.Refused:
                    said.Add($"{about} was not taken: {problem}");
                    break;
                default:
                    said.Add($"{about} was recorded by the run already, so it is only emptied from the inbox");
                    break;
            }

            offered[entry.Id] = Task.FromResult(handed);
        }

        foreach (var entry in held?.Amendments ?? [])
        {
            if (offered.TryGetValue(entry.Id, out var offer) && !HandedBack(offer))
            {
                continue;
            }

            seen++;
            var handed = coordinator.AmendmentHandedOver(entry.Text, entry.Through ?? AmendCommand, out var problem, entry.Id, entry.At);
            var about = "the amendment handed over";

            switch (handed)
            {
                case FlowHandedOver.Taken:
                    taken++;
                    said.Add($"{about} was taken from the inbox{(coordinator.TakenAs(entry.Id) is { } how ? $": {how}" : string.Empty)}");
                    break;
                case FlowHandedOver.Refused:
                    said.Add($"{about} was not taken: {problem}");
                    break;
                default:
                    said.Add($"{about} was recorded by the run already, so it is only emptied from the inbox");
                    break;
            }

            offered[entry.Id] = Task.FromResult(handed);
            _ = offered.Amendments.Add(entry.Id);
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
            new("updates", layout.UpdatesFile),
            new("issues", layout.PreExistingIssuesFile),
            new("definition", layout.DefinitionFile),
            new("events", layout.EventsFile),
            new("report", layout.ReportFile),
            new("reportJson", layout.ReportJsonFile),
            new("folds", layout.FoldsFile),
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

    /// <summary>
    /// What a session its terminal cut off answers, <paramref name="drain"/> after the second interrupt, while the session
    /// goes on behind it: the run and the command that goes on with it named, every record the run holds, the run's state
    /// as its records hold it, and the command's line in the run's log - what is left is what a crash leaves.
    /// </summary>
    private CommandOutcome CutOff(OrchestratorLayout layout, string command, TimeSpan drain, bool inbox, bool json)
    {
        var records = RecordsOf(layout, inbox);
        var outcome = CommandOutcome.Failed(HarnessExit.Cancelled, FlowTerminal.CutOffLine(drain, layout.Name), [.. records.Select(record => $"{record.What}: {record.Path}")]);

        return Ended(layout, command, outcome, ResultOf(layout, outcome) with { Records = records }, json);
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
    /// the process its owner names by its start, and never this one - then its owner is removed and what its steps left
    /// running is ended: a contained child's session, a leg's process group. A process that could not be ended is a
    /// failure, said; what is left is what a crash leaves, less those processes.
    /// </summary>
    private async Task<CommandOutcome> EndNowAsync(OrchestratorLayout layout, RunOwner held, bool json, CancellationToken cancellationToken)
    {
        var name = _owner.Named(held);

        CommandOutcome Answer(CommandOutcome outcome) => Ended(layout, StopCommand, outcome, ResultOf(layout, outcome), json);

        var ending = await _identity.EndWhereItStandsAsync(held.Machine, held.ProcessId, held.Stamp, _timings.StopWait, cancellationToken).ConfigureAwait(false);

        if (ending.NothingMoreToDo($"the engine of run '{layout.Name}'", name, "owner", $"{StopCommand} {layout.Name}", _timings.StopWait) is { } nothingMore)
        {
            return Answer(nothingMore);
        }

        // Gone already or ended now, the engine leaves its steps' children to the sweep, which ends what its owner's end left.
        _owner.RemoveGone(layout);
        var ended = await _children.SweepAsync(layout, StopCommand).ConfigureAwait(false);
        IReadOnlyList<string> endedWithIt = ended.Count == 0 ? [] : [$"what its steps left running was ended with it: {EndedChildSessions.Counted(ended)}"];

        if (ending.How == ProcessEnded.AlreadyGone)
        {
            return Answer(CommandOutcome.Ok(
                $"the engine of run '{layout.Name}' ({name}) had already ended; its owner was removed",
                [.. endedWithIt, $"{ResumeCommand} goes on from the records"]));
        }

        return Answer(CommandOutcome.Ok(
            $"ended the engine of run '{layout.Name}' ({name}) where it stood",
            [.. endedWithIt, $"what it left is what a crash leaves; {ResumeCommand} goes on from the records"]));
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

            if (question.Recommendation is { } said)
            {
                yield return $"  {RecommendationOf(said)}";
            }

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

    /// <summary>
    /// A question's recommendation as the listing says it: what is recommended and why, with who gave it - or, where none
    /// was obtained, why. The report's own saying of it stays the one in <see cref="FlowReports"/>; this is the listing's,
    /// beside the answers a reader may still give.
    /// </summary>
    /// <param name="said">The recommendation, as the question holds it.</param>
    private static string RecommendationOf(FlowRecommendation said)
        => said.State == "given"
            ? $"recommended: '{said.Answer}'{(said.Why is { } why ? $", {why}" : string.Empty)}{(said.By is { } by ? $" (by {by})" : string.Empty)}"
            : said.Why ?? "no recommendation was sought";

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
