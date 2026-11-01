using System.Text;
using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// One side-task's turns: the chat in the terminal and the one shot, over what the service set up. Each turn is one
/// <see cref="AgentStepRequest"/> handed to <see cref="IAgentRunner"/> as the engine hands a step's; what differs by
/// client is only how the chat goes on between the person's messages - a client that renders its conversations (Claude
/// Code) holds one launch open, the person's later messages reaching the agent through the channel a message to an agent
/// at work uses, and a new launch going on from the conversation so far wherever one ends; a client that stores
/// conversations runs one request per message, each going on with the stored one.
/// </summary>
internal sealed class Working
{
    private readonly IAgentRunner _runner;
    private readonly IReadOnlyList<IAgentClient> _clients;
    private readonly IFileSystem _fileSystem;
    private readonly IHarnessOutput _output;
    private readonly TimeProvider _clock;
    private readonly SideTaskCall _call;
    private readonly SideTaskCommon _common;
    private readonly SideTaskLayout _layout;
    private readonly SideTaskOwner _owner;
    private readonly Func<SideTaskRunsOn, string, CancellationToken, Task<(SideTaskSetUp? SetUp, CommandOutcome? Refused)>> _setUpAgain;
    private readonly IAgentSpendSink _spend;
    private readonly StreamTail _stream;
    private readonly List<SideTaskTurn> _turns = [];

    private SideTaskSetUp _now;
    private SideTaskRecord _record;
    private AgentSessionRef? _conversation;
    private AgentUsageReport? _inAll;
    private int _attempts;

    // The command's own stop, which a launch held open for the chat runs under: the person leaving - a second interrupt
    // among the ways - ends a launch as /exit does, its turn cut and its messages ended, never by stopping it hard, so
    // what it used, cost and answered is its result's.
    private CancellationToken _launching;

    /// <summary>Initializes the turn engine over the record the side-task goes on from.</summary>
    public Working(
        IAgentRunner runner,
        IReadOnlyList<IAgentClient> clients,
        IFileSystem fileSystem,
        IHarnessOutput output,
        TimeProvider clock,
        SideTaskCall call,
        SideTaskCommon common,
        SideTaskLayout layout,
        SideTaskOwner owner,
        SideTaskRecord record,
        SideTaskSetUp setUp,
        Func<SideTaskRunsOn, string, CancellationToken, Task<(SideTaskSetUp? SetUp, CommandOutcome? Refused)>> setUpAgain,
        IAgentSpendSink spend)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _call = call ?? throw new ArgumentNullException(nameof(call));
        _common = common ?? throw new ArgumentNullException(nameof(common));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _now = setUp ?? throw new ArgumentNullException(nameof(setUp));
        _setUpAgain = setUpAgain ?? throw new ArgumentNullException(nameof(setUpAgain));
        _spend = spend;
        _stream = new StreamTail(fileSystem, Path.Combine(layout.Directory, StoredStream.FileName));

        _conversation = record.Conversation;
        _inAll = record.InAll;
        _turns.AddRange(record.Turns);
    }

    /// <summary>The side-task's interrupts.</summary>
    private SideTaskInterrupts Interrupts => _call.Interrupts;

    /// <summary>
    /// The entry the side-task's turns are served by first: its order's first - the resolution's serving entry, or the
    /// one the walk moved a chat to (<see cref="AgentRunResult.GoesOnBy"/>).
    /// </summary>
    private AgentRunEntry Serving => _now.Order[0];

    /// <summary>Whether the client the side-task runs on holds one launch open between the person's messages.</summary>
    private bool LaunchStaysOpen
        => _clients.FirstOrDefault(client => string.Equals(client.Name, Serving.Client, StringComparison.Ordinal))?.Capabilities.GoesOnWith == AgentConversationHandling.Rendered;

    /// <summary>
    /// The one shot: the message is sent, the agent works until its turn ends - or the first interrupt cuts it - its
    /// final text is printed (or its one document, where <c>--json</c> asked for it), and the command ends with the code
    /// the turn's ending gives (<see cref="AgentRunResult.ToOutcome"/>).
    /// </summary>
    /// <param name="prompt">The one message.</param>
    /// <param name="json">Whether the answer is a document.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    public async Task<CommandOutcome> OneShotAsync(string prompt, bool json, CancellationToken cancellationToken)
    {
        using var front = new TurnFront(_layout, _fileSystem, _clock, say: null);
        var began = _clock.GetUtcNow();
        AgentRunResult result;

        try
        {
            result = await TurnAsync(prompt, front, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            // The turn itself refused - a key the provider rejects, a request the client cannot make - and its refusal
            // is the command's, with the records named.
            Kept(SideTaskStatus.Ended);
            var refused = new CommandOutcome(ex.ExitCode, ex.Message, [$"Records: {_layout.Directory}"]);

            return json ? refused with { Data = [SideTaskReports.Stopped(ex.ExitCode, ex.Message, _layout.Name, _layout.Directory)] } : refused;
        }

        var seen = front.Seen.Take();
        var turn = TurnOf(result, began);
        _turns.Add(turn);
        _inAll = AgentUsageReport.Sum(_inAll, result.Usage);
        Kept(SideTaskStatus.Ended);

        var exit = result.ToOutcome().ExitCode;
        var line = result.Status == AgentRunStatus.Completed
            ? $"side-task '{_layout.Name}' answered on {(result.Served is [_, ..] served ? served[0].Model : Serving.Model)}"
            : $"side-task '{_layout.Name}' ended {result.Status}{(result.Error is null ? string.Empty : ": " + result.Error)}";
        var outcome = new CommandOutcome(exit, line, [$"Records: {_layout.Directory}"]);

        if (json)
        {
            return outcome with { Data = [SideTaskReports.One(exit, line, _layout.Name, _layout.Directory, result, seen.Denials, turn)] };
        }

        return result.Status == AgentRunStatus.Completed && result.FinalText is { Length: > 0 } text
            ? outcome with { Data = [text] }
            : outcome;
    }

    /// <summary>
    /// The chat: the person's messages one at a time, the agent's text streamed as it comes and each action one line as
    /// it is decided, until the person leaves - <c>/exit</c>, an end of input, or a second interrupt - which ends it 0; a
    /// stop of the command's own, from outside it, ends it 130. Every way it ends writes its record ended and names its
    /// records.
    /// </summary>
    /// <param name="cancellationToken">The side-task's stop: the command's, and its second interrupt.</param>
    /// <param name="command">The command's own stop, which a launch held open for the chat runs under.</param>
    public async Task<CommandOutcome> ChatAsync(CancellationToken cancellationToken, CancellationToken command)
    {
        var chat = _call.Chat ?? throw new InvalidOperationException("a chat was asked for and no front end was given");
        using var front = new TurnFront(_layout, _fileSystem, _clock, chat.Say);
        Launch? open = null;
        Task<string?>? reading = null;

        _launching = command;
        _output.Info(SideTaskService.RunCommand, $"side-task '{_layout.Name}' on {Serving.Model} through {Serving.Client}; /help lists what the chat takes");

        try
        {
            while (!Interrupts.Ended.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                reading ??= ReadMessageAsync(chat, cancellationToken);
                Task[] waits = open is null ? [reading, Interrupts.Ended] : [reading, Interrupts.Ended, open.Run, front.Seen.TurnEnded];
                await Task.WhenAny(waits).ConfigureAwait(false);

                // Each turn of the open launch is recorded as it ends, whatever the person types meanwhile.
                if (open is not null)
                {
                    RecordEnded(open, front);
                }

                // A launch that ended by itself - a failed turn, a context outgrown, the program gone - is recorded first,
                // and the lines it never answered are sent again to a new one.
                if (open is { Run.IsCompleted: true } ended)
                {
                    open = await GoneOnAsync(ended, front, chat, cancellationToken).ConfigureAwait(false);
                }

                if (Interrupts.Ended.IsCompleted || !reading.IsCompleted)
                {
                    continue;
                }

                string? line;

                try
                {
                    line = await reading.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                reading = null;

                if (line is null)
                {
                    break;
                }

                if (line.Trim().Length == 0)
                {
                    continue;
                }

                if (line.StartsWith('/'))
                {
                    Commanded asked;

                    try
                    {
                        asked = await CommandAsync(line, chat, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Stopped while another model was made ready: the chat ends where it stands.
                        break;
                    }

                    if (asked == Commanded.Left)
                    {
                        break;
                    }

                    if (asked == Commanded.NewLaunch && open is not null)
                    {
                        // A new model or effort: the open launch gives way, and the next message begins a new one, going
                        // on from the conversation so far.
                        await EndAsync(open, front, chat, leaving: false).ConfigureAwait(false);
                        open = null;
                    }

                    continue;
                }

                if (!LaunchStaysOpen)
                {
                    await ChatTurnAsync(line, front, chat, cancellationToken).ConfigureAwait(false);
                }
                else if (open is null)
                {
                    open = Begin([line], front);
                }
                else
                {
                    if (!open.Post(line))
                    {
                        // The launch's messages were ended under it: the line is never dropped, but sent with a new one.
                        open = await GoneOnAsync(open, front, chat, cancellationToken, [line]).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            if (open is not null)
            {
                await EndAsync(open, front, chat, leaving: true).ConfigureAwait(false);
            }

            Kept(SideTaskStatus.Ended);
        }

        // The person's second interrupt is the person leaving, as /exit is; a stop the person did not give is the command's.
        var exit = cancellationToken.IsCancellationRequested && !Interrupts.Ended.IsCompleted ? HarnessExit.Cancelled : HarnessExit.Success;
        var summary = $"side-task '{_layout.Name}' ended, {ReportText.Counted(_turns.Count, "turn")} in all";

        return new CommandOutcome(exit, summary, [$"Records: {_layout.Directory}"]);
    }

    /// <summary>
    /// One turn of a client that stores its conversations, or of the one shot: one request, the first interrupt cutting
    /// it where it stands - a cancelled request - and the turn ending Cancelled.
    /// </summary>
    private async Task<AgentRunResult> TurnAsync(string message, TurnFront front, CancellationToken cancellationToken)
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        front.Seen.Take();

        // Told inside the interrupts' lock, and unwired before the source is let go: no press reaches it after.
        Interrupts.WhileTurning(count =>
        {
            _ = turn.CancelAsync();
            return true;
        });

        try
        {
            return await _runner.RunAsync(Request(message, messages: null, interrupt: null), front, turn.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            return new AgentRunResult(
                AgentRunStatus.Cancelled,
                Model: Serving.Model,
                Error: Interrupts.Presses > 0 ? "the person interrupted the turn" : "the command was stopped");
        }
        finally
        {
            Interrupts.WhileTurning(null);
        }
    }

    /// <summary>One message of a chat whose client stores its conversations: its turn, its record, and what its ending asks of the chat.</summary>
    private async Task ChatTurnAsync(string line, TurnFront front, SideTaskChat chat, CancellationToken cancellationToken)
    {
        var began = _clock.GetUtcNow();
        AgentRunResult result;

        try
        {
            result = await TurnAsync(line, front, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException refused)
        {
            // The turn itself refused - the run's records say why; the chat says it and stays open.
            chat.Say($"the turn was refused: {refused.Message}");
            return;
        }

        _turns.Add(TurnOf(result, began));
        _inAll = AgentUsageReport.Sum(_inAll, result.Usage);
        Kept(SideTaskStatus.Open);

        // A turn that outgrew its model's context is said as its ending alone: the compaction the next message begins
        // from is said once, as it is given (the Compacted event).
        if (result.Status == AgentRunStatus.Cancelled && !cancellationToken.IsCancellationRequested && !Interrupts.Ended.IsCompleted)
        {
            chat.Say("the turn was interrupted; the conversation keeps what was said - ask again, or /exit to leave");
        }
        else if (result.Status is not (AgentRunStatus.Completed or AgentRunStatus.Cancelled))
        {
            chat.Say($"the turn ended {result.Status}{(result.Error is null ? string.Empty : ": " + result.Error)}; "
                + (result.Status == AgentRunStatus.ContextExhausted
                    ? "the next message begins a new conversation, going on from the conversation so far"
                    : "the conversation keeps what was said"));
        }
    }

    /// <summary>
    /// Begins a launch held open for the chat, with the first of <paramref name="lines"/> as its prompt and the rest
    /// posted to it; each interrupt cuts its current turn alone, and only the command's own stop ends it hard.
    /// </summary>
    /// <param name="lines">The lines it is given, in order.</param>
    /// <param name="front">The chat's front.</param>
    /// <param name="alreadyWritten">How many of the first lines the transcript holds already: given to the launch before, which never answered them.</param>
    private Launch Begin(IReadOnlyList<string> lines, TurnFront front, int alreadyWritten = 0)
    {
        var launch = new Launch();

        front.Seen.LaunchBegins(alreadyWritten);
        launch.Run = _runner.RunAsync(Request(lines[0], launch.Messages, launch.Interrupt), front, _launching);
        launch.Given.Add(lines[0]);

        foreach (var line in lines.Skip(1))
        {
            launch.Post(line);
        }

        // Every press reaches the launch, whose client cuts the turn under way and leaves it be between turns; what is
        // said of it is whether a turn was under way.
        Interrupts.WhileTurning(_ =>
        {
            launch.Interrupt.Interrupt();
            return front.Seen.TurnUnderWay;
        });

        return launch;
    }

    /// <summary>
    /// A launch that ended by itself, recorded - compacted where it outgrew its context - and gone on from: the lines it
    /// never answered, and <paramref name="more"/>, sent on from the conversation so far - on the entry the walk moved the
    /// chat to where it handed a move back (<see cref="AgentRunResult.GoesOnBy"/>), as a new launch where that entry's
    /// client holds one open and as a request each where it stores its conversations, else to a new launch on the same
    /// entry. A new launch on the same entry that also ends before answering any of them gives them back to the person, so
    /// none is lost unsaid; a move goes on down the order, which ends.
    /// </summary>
    /// <returns>The new launch; null where none was begun.</returns>
    private async Task<Launch?> GoneOnAsync(Launch ended, TurnFront front, SideTaskChat chat, CancellationToken cancellationToken, IReadOnlyList<string>? more = null)
    {
        var (result, unsaid) = await EndAsync(ended, front, chat, leaving: false).ConfigureAwait(false);
        IReadOnlyList<string> lines = [.. unsaid, .. more ?? []];

        // The lines the transcript holds already: written to the launch that ended, which never answered them.
        var alreadyWritten = Math.Max(0, front.Seen.Given - (result?.MessagesAnswered ?? 0));

        // The walk's move, handed back: the chat goes on by the order the walk would have moved to.
        var moved = result?.GoesOnBy is { Count: > 0 } onward;

        if (moved)
        {
            _now = _now with { Order = result!.GoesOnBy! };
        }

        if (result is { } done && done.Status is not AgentRunStatus.Completed)
        {
            chat.Say($"the agent's launch ended {done.Status}{(done.Error is null ? string.Empty : ": " + done.Error)}; the next message begins a new launch, going on from the conversation so far");
        }

        if (lines.Count == 0 || cancellationToken.IsCancellationRequested || Interrupts.Ended.IsCompleted)
        {
            return null;
        }

        if (ended.Retried && unsaid.Count == ended.Given.Count && !moved)
        {
            chat.Say($"the new launch ended too before it answered {ReportText.Counted(lines.Count, "message")}; send again what still stands: {string.Join(" / ", lines)}");
            return null;
        }

        if (!LaunchStaysOpen)
        {
            // Moved to a client that stores its conversations: each line is a request of its own, as such a chat's are.
            front.Seen.LaunchBegins(alreadyWritten);

            foreach (var line in lines)
            {
                if (cancellationToken.IsCancellationRequested || Interrupts.Ended.IsCompleted)
                {
                    break;
                }

                await ChatTurnAsync(line, front, chat, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        var again = Begin(lines, front, alreadyWritten);
        again.Retried = true;

        return again;
    }

    /// <summary>
    /// Ends a launch - its messages closed where it is still open - and records what it did: the turns it ended, what it
    /// used, and its ending, its last turn's, compacting the conversation where it outgrew its context.
    /// </summary>
    /// <param name="launch">The launch.</param>
    /// <param name="front">The chat's front.</param>
    /// <param name="chat">The chat.</param>
    /// <param name="leaving">Whether the chat is ending, so nothing more is said of how the launch ended.</param>
    /// <returns>
    /// How it ended, where it said; and the lines given to it that no turn of it answered, in order - by its client's
    /// own count of the messages it answered (<see cref="AgentRunResult.MessagesAnswered"/>): a line written to it and
    /// never taken, or taken by a turn that ended without answering, is one.
    /// </returns>
    private async Task<(AgentRunResult? Result, IReadOnlyList<string> Unsaid)> EndAsync(Launch launch, TurnFront front, SideTaskChat chat, bool leaving)
    {
        launch.Messages.Complete();
        Interrupts.WhileTurning(null);

        AgentRunResult? result = null;

        try
        {
            result = await launch.Run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The command was stopped; what the launch had said is in its stream.
        }
        catch (HarnessException refused)
        {
            if (!leaving)
            {
                chat.Say($"the launch was refused: {refused.Message}");
            }
        }

        IReadOnlyList<string> unsaid = [.. launch.Given.Skip(result?.MessagesAnswered ?? 0)];

        RecordEnded(launch, front);

        // The launch's ending is its last turn's where that turn ended with it - the turns before it ended inside the launch,
        // which went on; where a turn was under way, or a line waited for a turn of its own, the launch ended where no
        // record holds a turn, and the turns recorded keep their own endings.
        if (result is not null && launch.Recorded > 0 && front.Seen.EndedAtATurnsEnd)
        {
            _turns[^1] = _turns[^1] with { Status = result.Status };
        }

        _inAll = AgentUsageReport.Sum(_inAll, result?.Usage);
        Kept(SideTaskStatus.Open);

        return (result, unsaid);
    }

    /// <summary>
    /// Records each turn of the open launch that ended since the last were recorded - one record each, from when it began,
    /// the agent taking the message it answers, with the model that served it and what it used - and keeps the side-task's record
    /// with them, so each turn reaches its record as it ends, whatever the person types meanwhile.
    /// </summary>
    private void RecordEnded(Launch launch, TurnFront front)
    {
        var ended = front.Seen.TakeEnded();

        if (ended.Count == 0)
        {
            return;
        }

        foreach (var turn in ended)
        {
            _turns.Add(new SideTaskTurn(
                turn.Began,
                Serving.Client,
                Serving.Provider,
                Serving.Model,
                Serving.Effort,
                turn.Served,
                turn.Usage,
                turn.Served.Count,
                Status: null,
                SideTaskPolicy.Said));
        }

        launch.Recorded += ended.Count;
        Kept(SideTaskStatus.Open);
    }

    /// <summary>The request of one turn of the side-task, or of one launch held open for the chat.</summary>
    /// <param name="message">The person's message, whole.</param>
    /// <param name="messages">The channel a launch held open reads the later messages from; null where each message is its own run.</param>
    /// <param name="interrupt">The signal that cuts the launch's current turn alone; null where the run is one turn's.</param>
    private AgentStepRequest Request(string message, AgentMessages? messages, AgentTurnInterrupt? interrupt)
    {
        _attempts++;

        var (conversation, compacted, continuation) = GoingOn();

        return new AgentStepRequest(
            _layout.Name,
            "side-task",
            $"turn-{_turns.Count + 1}",
            Attempt: _attempts,
            AgentDefinition: null,
            Provider: null,
            Serving.Client,
            _now.RunsOn.Model is null ? _now.RunsOn.Tier : null,
            Serving.Model,
            Serving.Effort,
            Fallback: null,
            SideTaskService.AgentSystemPrompt,
            message,
            ResumeTemplate: null,
            new AgentWorkspace(
                Worktree: null,
                _common.Context.Layout.MainCheckoutRoot,
                _common.Context.Layout.MainCheckoutRoot,
                [],
                _common.Context.Layout.MainCheckoutRoot,
                _layout.Directory),
            Output: null,
            conversation,
            new AgentLimits(MaxTurns: null, MaxBudgetUsd: null, MaxDuration: null, StallSeconds: null),
            _common.Mask ?? NoMask.One)
        {
            Ground = _now.Ground,
            Credential = _now.Order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = _common.Withheld,
            Order = _now.Order,
            WebSearch = _common.Search,
            Spend = _spend,
            // A launch held open for a chat is never tried again by the runner: a try begins again from its first message,
            // losing every turn after it and every line it had taken. It ends instead, and the chat goes on from the
            // conversation so far with the lines no turn answered (GoneOnAsync).
            Retries = messages is null ? SideTaskService.DefaultRetries : 0,
            BackoffSeconds = SideTaskService.DefaultBackoffSeconds,
            RecordDirectory = _layout.Directory,
            SessionsDirectory = _layout.SessionsDirectory,
            Continuation = continuation,
            Compaction = compacted,
            Program = _now.Program,
            ProgramVersion = _now.ProgramVersion,
            Messages = messages,
            Interrupt = interrupt,
            Chat = messages is not null,
        };
    }

    /// <summary>The turn's record, from the run's result: when it began, what it ran on and how it ended.</summary>
    private SideTaskTurn TurnOf(AgentRunResult result, DateTimeOffset began)
        => new(
            began,
            Serving.Client,
            Serving.Provider,
            Serving.Model,
            Serving.Effort,
            result.Served,
            result.Usage,
            result.Turns,
            result.Status,
            SideTaskPolicy.Said);

    /// <summary>
    /// Keeps the side-task's record whole, as it stands: what it runs on as the person last set it, the conversation its
    /// turns go on with, what it used in all and each turn - written under the claims' lock where this process still
    /// holds the side-task.
    /// </summary>
    /// <param name="status">How the side-task stands.</param>
    private void Kept(SideTaskStatus status)
    {
        _conversation = ConversationIn(Whole());

        var record = _record with
        {
            LastSpoke = _turns.Count > _record.Turns.Count ? _clock.GetUtcNow() : _record.LastSpoke,
            Status = status,
            RunsOn = _now.RunsOn,
            Conversation = _conversation,
            InAll = _inAll,
            Turns = [.. _turns],
        };

        if (_owner.Keep(_layout, record))
        {
            _record = record;
        }
        else
        {
            _output.Warn(SideTaskService.RunCommand, $"side-task '{_layout.Name}' is held by another process now; its record was left as that process keeps it");
        }
    }

    /// <summary>
    /// The stored conversation the side-task's stream names last, where it holds one it can go on with: the session's
    /// id with the client and provider that began it, and only where its <c>conversation.jsonl</c> is there - a client
    /// that renders its conversations, Claude Code among them, names a session of its own that no conversation can be
    /// handed to. Null where none is, and where the last run that worked outgrew its model's context
    /// (<see cref="Outgrown"/>): handed again, it would meet the same wall.
    /// </summary>
    private AgentSessionRef? ConversationIn(IReadOnlyList<AgentEvent> stream)
    {
        if (Outgrown(stream))
        {
            return null;
        }

        var last = stream.OfType<SessionStarted>().LastOrDefault(started => started.SessionId is { Length: > 0 });

        return last?.SessionId is { } id
            && _fileSystem.FileExists(Path.Combine(OrchestratorLayout.SessionDirectoryIn(_layout.SessionsDirectory, id), "conversation.jsonl"))
            ? new AgentSessionRef(last.Client, last.Provider ?? string.Empty, id)
            : null;
    }

    /// <summary>
    /// How the side-task's next turn goes on, read from its stream - the one owner of it for a chat's every turn and
    /// launch and for a side-task opened again, in this process or a later one. Where the last run that worked outgrew
    /// its model's context (<see cref="Outgrown"/>), no stored conversation is handed and the engine's compaction of the
    /// conversation is carried in the next conversation's first message, before the person's next message as its task
    /// (<see cref="AgentCompacted.BeforeItsTask"/>), said by the runner as a <see cref="DssHarness.Core.Agents.Compacted"/>
    /// event and counted as the client is given it - as a step's answer <c>compact</c> is, and said once. Otherwise the
    /// stored conversation goes on where there is one, and where the next turn begins a conversation of its own - a new
    /// launch, another provider's model, a fallback - the conversation so far is carried in its first message, reduced
    /// as the engine's compaction reduces it; where the stored conversation is handed, the client sends none of it.
    /// </summary>
    private (AgentSessionRef? Conversation, AgentCompacted? Compaction, string? Continuation) GoingOn()
    {
        var stream = Whole();

        if (Outgrown(stream))
        {
            return (null, Reduced(stream) is { } record ? record with { Why = AgentCompaction.OutgrewItsContext, BeforeItsTask = true } : null, null);
        }

        return (_conversation, null, _turns.Count > 0 ? Reduced(stream)?.Record : null);
    }

    /// <summary>
    /// Whether the conversation outgrew its model's context: the last run of the stream that worked - one that began a
    /// session - ended <see cref="AgentRunStatus.ContextExhausted"/>. A run given the compaction of it begins a session of
    /// its own, so once one has worked the conversation goes on as any does; a run that never began one leaves it as it
    /// stood.
    /// </summary>
    /// <param name="stream">The side-task's stream, read as one conversation.</param>
    private static bool Outgrown(IReadOnlyList<AgentEvent> stream)
    {
        var outgrown = false;
        var worked = false;

        foreach (var one in stream)
        {
            switch (one)
            {
                case SessionStarted:
                    worked = true;
                    break;

                case RunFinished finished when worked:
                    outgrown = finished.Status == AgentRunStatus.ContextExhausted;
                    worked = false;
                    break;
            }
        }

        return outgrown;
    }

    /// <summary>
    /// The conversation so far, reduced as the engine's compaction reduces a stream (<see cref="AgentCompaction"/>): the
    /// first message whole, every later message of the person's whole, the last iterations word for word and every
    /// earlier one shortened - null where nothing streamed yet.
    /// </summary>
    /// <param name="stream">The side-task's stream, read as one conversation.</param>
    private AgentCompacted? Reduced(IReadOnlyList<AgentEvent> stream)
    {
        return stream.Count == 0
            ? null
            : AgentCompaction.Reduce(
                stream,
                AgentContinuationBounds.Of(_common.Context.Config.AgentContext?.Continuation),
                AgentCompactionBounds.Of(_common.Context.Config.AgentContext?.Compaction));
    }

    /// <summary>
    /// The side-task's stored stream as one conversation: every event of every turn, the run start of each turn after
    /// the first left out, so what reads the newest attempt of a step reads the whole side-task - its turns numbered on
    /// across its runs, each run's own count going on from the last turn of the run before it, as an attempt's are - and
    /// each later turn's first message the person's alone, as a chat's later message is, never the conversation it
    /// carried before it. A message a chat's launch was given and never answered is read where the launch after it was
    /// given it again, never twice: its run's end says how many it settled (<see cref="RunFinished.MessagesAnswered"/>),
    /// the conversation's first message kept whatever it says.
    /// </summary>
    private IReadOnlyList<AgentEvent> Whole()
    {
        var whole = new List<AgentEvent>();
        var started = false;
        var asked = false;
        var before = 0;
        var last = 0;

        // Where each of the person's messages of the run read so far stands in the conversation, in the order the run was
        // given them; null for the conversation's first message, which stays.
        var given = new List<int?>();

        foreach (var one in _stream.Events())
        {
            switch (one)
            {
                case RunStarted when started:
                    before = last;
                    given.Clear();
                    continue;

                case RunStarted:
                    started = true;
                    break;

                case RunFinished { MessagesAnswered: { } answered }:
                    foreach (var at in given.Skip(answered).OfType<int>().Reverse())
                    {
                        whole.RemoveAt(at);
                    }

                    given.Clear();
                    break;

                case MessageSent { Which: AgentMessageKind.ChatMessage }:
                    given.Add(whole.Count);
                    break;

                case TurnStarted turn:
                    last = before + turn.Turn;
                    whole.Add(turn with { Turn = last });
                    continue;

                case TurnFinished finished:
                    whole.Add(finished with { Turn = before + finished.Turn });
                    continue;

                // Each later turn's first message is the person's: what it carried of the conversation before it is
                // that conversation's, which the stream holds itself, so it stands as the person's message alone.
                case MessageSent { Which: AgentMessageKind.FirstPrompt } sent when asked:
                    given.Add(whole.Count);
                    whole.Add(sent with { Text = AgentPromptParts.TaskOf(sent.Text), Which = AgentMessageKind.ChatMessage });
                    continue;

                case MessageSent { Which: AgentMessageKind.FirstPrompt }:
                    given.Add(null);
                    asked = true;
                    break;
            }

            whole.Add(one);
        }

        return whole;
    }

    /// <summary>
    /// Reads one message of the person's: a line, and where it ends with a backslash, the lines after it joined to it
    /// until one does not.
    /// </summary>
    private static async Task<string?> ReadMessageAsync(SideTaskChat chat, CancellationToken cancellationToken)
    {
        var line = await chat.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        while (line is not null && line.EndsWith('\\'))
        {
            var more = await chat.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            line = line[..^1].TrimEnd() + (more is null ? string.Empty : "\n" + more);

            if (more is null)
            {
                break;
            }
        }

        return line;
    }

    /// <summary>What the person's <c>/</c> command asked, done; whether it ends the chat or begins a new launch.</summary>
    /// <param name="line">The line, beginning with <c>/</c>.</param>
    /// <param name="chat">The chat's front end.</param>
    /// <param name="cancellationToken">Stops the setting up of another model.</param>
    private async Task<Commanded> CommandAsync(string line, SideTaskChat chat, CancellationToken cancellationToken)
    {
        var command = line.Split(' ', 2);
        var argument = command.Length > 1 ? command[1].Trim() : string.Empty;

        switch (command[0].ToLowerInvariant())
        {
            case "/exit":
                return Commanded.Left;

            case "/help":
                chat.Say(HelpLine);
                return Commanded.None;

            case "/records":
                chat.Say($"Records: {_layout.Directory}");
                return Commanded.None;

            case "/model":
                if (argument.Length == 0)
                {
                    chat.Say($"the chat runs on {Serving.Model} through {Serving.Client}; /model <model> names another");
                    return Commanded.None;
                }

                // A new model starts from the efforts that name it - its own entry, and the one for the rest - never from
                // an entry for a model it replaces, which it does not run on.
                var kept = _now.RunsOn.EffortByModel?
                    .Where(entry => string.Equals(entry.Key, argument, StringComparison.Ordinal) || string.Equals(entry.Key, SideTaskEffort.Every, StringComparison.Ordinal))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

                return await SetAsync(_now.RunsOn with { Model = argument, Tier = null, Client = null, EffortByModel = kept is { Count: > 0 } ? kept : null }, chat, cancellationToken).ConfigureAwait(false);

            case "/effort":
                if (argument.Length == 0)
                {
                    chat.Say(Serving.Effort is { Length: > 0 } running
                        ? $"the chat runs {Serving.Model} at effort {running}; /effort <level> or /effort <code>=<level> ... names another, /effort default the model's own"
                        : $"the chat runs {Serving.Model} at its own default effort; /effort <level> or /effort <code>=<level> ... names one");
                    return Commanded.None;
                }

                if (string.Equals(argument, DefaultEffort, StringComparison.OrdinalIgnoreCase))
                {
                    return await SetAsync(_now.RunsOn with { Effort = null, EffortByModel = null }, chat, cancellationToken).ConfigureAwait(false);
                }

                var (level, byModel, problem) = SideTaskEffort.Parse(argument.Split(' ', StringSplitOptions.RemoveEmptyEntries));

                if (problem is not null)
                {
                    chat.Say(problem.Replace("'--effort ", "'/effort ", StringComparison.Ordinal));
                    return Commanded.None;
                }

                return await SetAsync(_now.RunsOn with { Effort = level, EffortByModel = byModel }, chat, cancellationToken).ConfigureAwait(false);

            default:
                chat.Say($"'{command[0]}' is no command of this chat; /help lists them");
                return Commanded.None;
        }
    }

    /// <summary>What <c>/effort</c> takes to give each model its own default effort again.</summary>
    public const string DefaultEffort = "default";

    /// <summary>What <c>/help</c> says the chat takes.</summary>
    public const string HelpLine = "/exit leaves the chat; /model <model> and /effort <level>, <code>=<level> ... or default change what the next message is sent with; /records says where this side-task's records are; a line ending in \\ goes on to the next; Ctrl+C interrupts the agent's turn, and a second ends the chat";

    /// <summary>
    /// Sets the side-task up again on what <paramref name="runsOn"/> asks, by the one builder of it - the order, the
    /// ground and the readiness with the model - the chat saying what the next message goes with, or why it goes with
    /// what it went with.
    /// </summary>
    private async Task<Commanded> SetAsync(SideTaskRunsOn runsOn, SideTaskChat chat, CancellationToken cancellationToken)
    {
        var (setUp, refused) = await _setUpAgain(runsOn, "/effort", cancellationToken).ConfigureAwait(false);

        if (setUp is null)
        {
            chat.Say($"{refused!.Message}; the next message goes to {Serving.Model} as before");
            return Commanded.None;
        }

        _now = setUp;
        Kept(SideTaskStatus.Open);

        chat.Say(Serving.Effort is { Length: > 0 } effort
            ? $"the next message goes to {Serving.Model} through {Serving.Client}, at effort {effort}"
            : $"the next message goes to {Serving.Model} through {Serving.Client}, at its own default effort");

        return Commanded.NewLaunch;
    }

    /// <summary>What the person's <c>/</c> command asked of the chat.</summary>
    private enum Commanded
    {
        /// <summary>Nothing that changes the chat's run.</summary>
        None,

        /// <summary>The chat leaves: <c>/exit</c>.</summary>
        Left,

        /// <summary>The chat's next message begins a new launch: the model or the effort changed.</summary>
        NewLaunch,
    }

    /// <summary>
    /// One launch held open for a chat: its own messages and interrupt - a launch that ended keeps them ended, so the
    /// next has its own - the lines given to it in order, the first its prompt, and how many of its turns are recorded.
    /// </summary>
    private sealed class Launch
    {
        /// <summary>The channel the person's later lines reach it through.</summary>
        public AgentMessages Messages { get; } = new();

        /// <summary>The signal that cuts its current turn alone.</summary>
        public AgentTurnInterrupt Interrupt { get; } = new();

        /// <summary>The launch's run.</summary>
        public Task<AgentRunResult> Run { get; set; } = Task.FromResult(new AgentRunResult(AgentRunStatus.Failed));

        /// <summary>Every line given to it, in order, the first its prompt.</summary>
        public List<string> Given { get; } = [];

        /// <summary>How many of its turns the side-task's record holds.</summary>
        public int Recorded { get; set; }

        /// <summary>Whether it was begun to send again the lines an ended launch never took.</summary>
        public bool Retried { get; set; }

        /// <summary>Posts one more line to it; false where its messages were ended, and the line was not posted.</summary>
        public bool Post(string line)
        {
            if (!Messages.Post(line))
            {
                return false;
            }

            Given.Add(line);
            return true;
        }
    }

    /// <summary>
    /// What the turns said to the person and wrote to the side-task's transcript: the agent's text as it streams, each
    /// person's message and each action one line as it is decided, each denial with its rule - and what each turn used,
    /// for its own record. Every text it is given is masked by the keeper that hands it the event, as the stored stream
    /// is. Its events come on the run's own drain, apart from the chat's: what they leave is handed across whole
    /// (<see cref="Seen"/>). The transcript is one file a whole side-task appends to, so its log is held until the
    /// side-task ends.
    /// </summary>
    private sealed class TurnFront(SideTaskLayout layout, IFileSystem fileSystem, TimeProvider clock, Action<string>? say) : IAgentEventSink, IDisposable
    {
        private readonly SideTaskLayout _layout = layout;
        private readonly IFileSystem _fileSystem = fileSystem;
        private readonly Action<string>? _say = say;
        private AppendLog? _transcript;

        /// <summary>What the turns were seen to do.</summary>
        public SeenTurns Seen { get; } = new(clock);

        /// <summary>Closes the transcript's log, whole on the disk.</summary>
        public void Dispose() => _transcript?.Dispose();

        /// <inheritdoc/>
        public ValueTask EmitAsync(AgentEvent @event)
        {
            switch (@event)
            {
                case TextDelta delta:
                    Said(delta.Text);
                    break;

                case AssistantMessage message:
                    Write(string.Empty);
                    Write("agent:");
                    foreach (var line in Lines(message.Text))
                    {
                        Write($"    {line}");
                    }

                    break;

                case MessageSent sent when sent.Which is AgentMessageKind.FirstPrompt or AgentMessageKind.ChatMessage:
                    // A line given again, which the launch before never answered, is in the transcript once already.
                    if (!Seen.OneGiven())
                    {
                        break;
                    }

                    Write(string.Empty);
                    Write("person:");
                    foreach (var line in Lines(AgentPromptParts.TaskOf(sent.Text)))
                    {
                        Write($"    {line}");
                    }

                    break;

                case TurnStarted:
                    Seen.TurnBegan();
                    break;

                case MessageTaken taken:
                    Seen.Waiting(taken.Waiting);
                    break;

                case MessagesDropped dropped:
                    Seen.Waiting(dropped.Waiting);
                    Said($"the interrupt dropped {ReportText.Counted(dropped.Count, "message")} the agent had not taken yet; send again what still stands");
                    Write($"    dropped: {ReportText.Counted(dropped.Count, "message")} not taken");
                    break;

                case ToolCallStarted:
                    Seen.Called();
                    break;

                case ToolCallFinished finished:
                    var of = finished.Input.Length > 96 ? finished.Input[..96] + "..." : finished.Input.Replace("\n", " ", StringComparison.Ordinal);
                    Said($"> {finished.Id} {of}".TrimEnd());
                    Write($"    > {of}");
                    break;

                case PermissionDenied denied:
                    Seen.Denied($"{denied.Tool} {denied.Subject}: {denied.Reason}");
                    Said($"denied: {denied.Reason}");
                    Write($"    denied: {denied.Reason}");
                    break;

                case FallbackStarted moved:
                    Said($"{moved.FromModel} -> {moved.ToModel} ({moved.Why})");
                    Write($"    moved to {moved.ToModel}: {moved.Why}");
                    break;

                case Compacted compacted:
                    // The one place a compaction is said: as the run given it begins.
                    Said($"compacted: {compacted.Why}; this turn begins a new conversation from what it holds");
                    Write($"    compacted: {compacted.Why}");
                    break;

                case Notice notice:
                    Said(notice.Text);
                    Write($"    {notice.Text}");
                    break;

                case TurnFinished finished:
                    Seen.OneServed(finished.Model, finished.Usage);
                    break;
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>Says one line of text to the person, where a chat is listening.</summary>
        private void Said(string line)
        {
            if (line.Length > 0 && _say is not null)
            {
                _say(line);
            }
        }

        /// <summary>Writes one line of the transcript.</summary>
        private void Write(string line)
        {
            try
            {
                _transcript ??= AppendLog.Open(_fileSystem, _layout.TranscriptFile);
                _transcript.Append(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The transcript is the person's copy of the conversation; the stored stream is the record. A
                // transcript that cannot be written stops nothing.
            }
        }

        /// <summary>The text's lines, each without its ending.</summary>
        private static IEnumerable<string> Lines(string text)
            => text.Split('\n').Select(one => one.TrimEnd('\r'));
    }

    /// <summary>
    /// What the turns were seen to do, filled from the run's drain and taken whole by the chat: each turn's denials, the
    /// models that served it with what each used and its calls - for a run that is one turn of the side-task's - and,
    /// for a launch held open for a chat, each turn of the agent's as it ended, from when it began, how many of the
    /// person's messages the launch was given and how many of them wait, written and not taken, by its client's own
    /// count - every touch under one lock, and what is taken handed across as copies.
    /// </summary>
    /// <param name="clock">Tells when a turn began and ended.</param>
    internal sealed class SeenTurns(TimeProvider clock)
    {
        private readonly Lock _gate = new();
        private readonly List<string> _denials = [];
        private readonly List<AgentServedModel> _served = [];
        private readonly List<AgentModelUsage> _used = [];
        private readonly List<EndedTurn> _ended = [];
        private TaskCompletionSource _turnEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset? _began;
        private int _calls;
        private int _given;
        private int _waiting;
        private int _alreadyWritten;

        /// <summary>Completes when a turn ended that is not taken yet (<see cref="TakeEnded"/>).</summary>
        public Task TurnEnded
        {
            get
            {
                lock (_gate)
                {
                    return _turnEnded.Task;
                }
            }
        }

        /// <summary>
        /// Whether the launch's last turn ended with it: no turn of the agent's under way - one began and has not ended - and
        /// no message written to it waiting to be taken (<see cref="MessageTaken"/>), which would have begun a turn of its
        /// own: a launch gone between one turn's end and taking the next message ended between turns.
        /// </summary>
        public bool EndedAtATurnsEnd
        {
            get
            {
                lock (_gate)
                {
                    return _began is null && _waiting == 0;
                }
            }
        }

        /// <summary>Whether a turn of the agent's is under way: one began and has not ended.</summary>
        public bool TurnUnderWay
        {
            get
            {
                lock (_gate)
                {
                    return _began is not null;
                }
            }
        }

        /// <summary>How many of the person's messages the launch begun last was given, each as it was written to it.</summary>
        public int Given
        {
            get
            {
                lock (_gate)
                {
                    return _given;
                }
            }
        }

        /// <summary>What the turn so far was seen to do, taken: the next begins with none of it.</summary>
        public SeenTurn Take()
        {
            lock (_gate)
            {
                var taken = new SeenTurn(
                    [.. _denials],
                    [.. _served],
                    _used.Count == 0 ? null : new AgentUsageReport(_used.Aggregate(AgentUsage.None, (sum, one) => sum.Plus(one.Usage)), [.. _used]),
                    _calls);

                _denials.Clear();
                _served.Clear();
                _used.Clear();
                _calls = 0;
                TakeEndedLocked();

                return taken;
            }
        }

        /// <summary>Each turn that ended since the last were taken, in order: the next take begins with none of them.</summary>
        public IReadOnlyList<EndedTurn> TakeEnded()
        {
            lock (_gate)
            {
                return TakeEndedLocked();
            }
        }

        /// <summary>
        /// A launch - or a run of requests going on from one - begins: no turn is under way, nothing an earlier launch left
        /// is its, and the first <paramref name="alreadyWritten"/> of the person's messages it is given are ones the
        /// transcript holds already, given again after the launch before it never answered them.
        /// </summary>
        /// <param name="alreadyWritten">How many of its first messages the transcript holds already.</param>
        public void LaunchBegins(int alreadyWritten)
        {
            lock (_gate)
            {
                _began = null;
                _given = 0;
                _waiting = 0;
                _alreadyWritten = alreadyWritten;
                TakeEndedLocked();
            }
        }

        /// <summary>
        /// One more of the person's messages was given to the agent - written to it, waiting to be taken: whether the
        /// transcript is to write it - not one it holds already.
        /// </summary>
        public bool OneGiven()
        {
            lock (_gate)
            {
                _given++;
                _waiting++;

                if (_alreadyWritten > 0)
                {
                    _alreadyWritten--;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// How many of the messages written to the agent wait, not taken, as its client counts them - said as it takes
        /// one (<see cref="MessageTaken"/>) and as an interrupt drops some (<see cref="MessagesDropped"/>).
        /// </summary>
        /// <param name="waiting">The client's own count.</param>
        public void Waiting(int waiting)
        {
            lock (_gate)
            {
                _waiting = waiting;
            }
        }

        /// <summary>A turn of the agent's began: it took the message it answers.</summary>
        public void TurnBegan()
        {
            lock (_gate)
            {
                _began = clock.GetUtcNow();
            }
        }

        /// <summary>One more call was made.</summary>
        public void Called()
        {
            lock (_gate)
            {
                _calls++;
            }
        }

        /// <summary>One more denial was said.</summary>
        public void Denied(string denial)
        {
            lock (_gate)
            {
                _denials.Add(denial);
            }
        }

        /// <summary>One model served one turn, and used what it used.</summary>
        public void OneServed(string model, AgentUsage usage)
        {
            lock (_gate)
            {
                _served.Add(new AgentServedModel(model, _served.Count + 1));
                _used.Add(new AgentModelUsage(model, usage));

                _ended.Add(new EndedTurn(_began ?? clock.GetUtcNow(), [new AgentServedModel(model, 1)], new AgentUsageReport(usage, [new AgentModelUsage(model, usage)])));
                _began = null;
                _turnEnded.TrySetResult();
            }
        }

        /// <summary>The turns ended, taken, and the signal of one made ready for the next.</summary>
        private List<EndedTurn> TakeEndedLocked()
        {
            var taken = new List<EndedTurn>(_ended);

            _ended.Clear();

            if (_turnEnded.Task.IsCompleted)
            {
                _turnEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return taken;
        }
    }

    /// <summary>One turn of the agent's that ended inside a launch held open for a chat.</summary>
    /// <param name="Began">When it began: when the agent took the message it answers.</param>
    /// <param name="Served">The model that served it.</param>
    /// <param name="Usage">What it used, with no cost: a launch's cost is the launch's.</param>
    internal sealed record EndedTurn(DateTimeOffset Began, IReadOnlyList<AgentServedModel> Served, AgentUsageReport Usage);

    /// <summary>What one turn was seen to do, as a copy the chat holds.</summary>
    /// <param name="Denials">Its denials, as they were said.</param>
    /// <param name="Served">The models that served it, each from its own turn.</param>
    /// <param name="Usage">What its turns used, with no cost: a launch's cost is the launch's; null where nothing was used.</param>
    /// <param name="Calls">How many tool calls it made.</param>
    internal sealed record SeenTurn(IReadOnlyList<string> Denials, IReadOnlyList<AgentServedModel> Served, AgentUsageReport? Usage, int Calls);

    /// <summary>
    /// The side-task's stored stream, read as it grows: each read takes only the whole lines written since the last, so
    /// a long chat never reads its stream again from the start.
    /// </summary>
    /// <param name="fileSystem">Reads the stream.</param>
    /// <param name="file">The stored stream.</param>
    private sealed class StreamTail(IFileSystem fileSystem, string file)
    {
        private readonly List<AgentEvent> _events = [];
        private long _read;

        /// <summary>Every event the stream holds so far, in order; empty where nothing streamed yet.</summary>
        public IReadOnlyList<AgentEvent> Events()
        {
            if (!fileSystem.FileExists(file))
            {
                return [];
            }

            byte[] fresh;

            using (var stream = fileSystem.OpenRead(file))
            {
                if (stream.CanSeek && stream.Length < _read)
                {
                    // The stream was written anew: read from its start.
                    _events.Clear();
                    _read = 0;
                }

                using var rest = new MemoryStream();

                if (stream.CanSeek)
                {
                    stream.Seek(_read, SeekOrigin.Begin);
                    stream.CopyTo(rest);
                    fresh = rest.ToArray();
                }
                else
                {
                    stream.CopyTo(rest);
                    fresh = [.. rest.ToArray().Skip((int)Math.Min(_read, int.MaxValue))];
                }
            }

            // Only whole lines: a line still being written is read once it ends.
            var end = Array.LastIndexOf(fresh, (byte)'\n');

            if (end < 0)
            {
                return [.. _events];
            }

            _read += end + 1;

            foreach (var line in Encoding.UTF8.GetString(fresh, 0, end + 1).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<AgentEvent>(line.TrimEnd('\r'), JsonStateFile.LineOptions) is { } read)
                    {
                        _events.Add(read);
                    }
                }
                catch (JsonException)
                {
                    // A torn line a killed writer left takes itself back; the whole ones around it stand.
                }
            }

            return [.. _events];
        }
    }

    /// <summary>A mask that masks nothing, where the side-task's search carries no secret.</summary>
    private sealed class NoMask : ILineMask
    {
        /// <summary>The one that masks nothing.</summary>
        public static readonly NoMask One = new();

        public string Redact(string? text) => text ?? string.Empty;

        public int Cut(string text) => text.Length;
    }
}
