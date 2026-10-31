using System.Globalization;
using System.Text;
using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// One side-task's turns: the chat in the terminal and the one shot, over the request the service resolved. Each turn
/// is one <see cref="AgentStepRequest"/> handed to <see cref="IAgentRunner"/> as the engine hands a step's; what differs
/// by client is only how the chat goes on between the person's messages - a client that renders its conversations
/// (Claude Code) holds one launch open for the whole chat, the person's later messages reaching the agent through the
/// channel a message to an agent at work uses, while a client that stores conversations runs one request per message,
/// each going on with the stored one.
/// </summary>
internal sealed class Working
{
    private readonly IAgentRunner _runner;
    private readonly IReadOnlyList<IAgentClient> _clients;
    private readonly IFileSystem _fileSystem;
    private readonly IHarnessOutput _output;
    private readonly TimeProvider _clock;
    private readonly SideTaskCall _call;
    private readonly HarnessContext _context;
    private readonly SideTaskLayout _layout;
    private readonly SideTaskStore _records;
    private readonly SideTaskRecord _record;
    private readonly string? _tier;
    private readonly IReadOnlyDictionary<string, string>? _effortByModel;
    private readonly IReadOnlyList<AgentRunEntry> _order;
    private readonly string? _program;
    private readonly string? _programVersion;
    private readonly Policy.AgentPolicyGround _ground;
    private readonly AgentWorkspace _workspace;
    private readonly AgentWebSearch? _search;
    private readonly ILineMask _mask;
    private readonly IAgentSpendSink _spend;

    private AgentResolution _resolution;
    private string? _effort;
    private AgentSessionRef? _conversation;
    private string? _compaction;
    private string? _continuation;
    private AgentUsageReport? _inAll;
    private readonly List<SideTaskTurn> _turns = [];
    private int _attempts;

    /// <summary>Initializes the turn engine over the record the side-task goes on from.</summary>
    public Working(
        IAgentRunner runner,
        IReadOnlyList<IAgentClient> clients,
        IFileSystem fileSystem,
        IHarnessOutput output,
        TimeProvider clock,
        SideTaskCall call,
        HarnessContext context,
        SideTaskLayout layout,
        SideTaskRecord record,
        AgentResolution resolution,
        string? tier,
        string? effort,
        IReadOnlyDictionary<string, string>? effortByModel,
        IReadOnlyList<AgentRunEntry> order,
        string? program,
        string? programVersion,
        Policy.AgentPolicyGround ground,
        AgentWorkspace workspace,
        AgentWebSearch? search,
        ILineMask? mask,
        IAgentSpendSink spend)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _call = call ?? throw new ArgumentNullException(nameof(call));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _records = new SideTaskStore(_fileSystem);
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _resolution = resolution ?? throw new ArgumentNullException(nameof(resolution));
        _tier = tier;
        _effort = effort;
        _effortByModel = effortByModel;
        _order = order ?? throw new ArgumentNullException(nameof(order));
        _program = program;
        _programVersion = programVersion;
        _ground = ground ?? throw new ArgumentNullException(nameof(ground));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _search = search;
        _mask = mask ?? NoMask.One;
        _spend = spend;

        _conversation = record.Conversation;
        _inAll = record.InAll;
        _turns.AddRange(record.Turns);
        _continuation = record.Turns is [_, ..] && LaunchStaysOpen ? ContinuationOf() : null;
    }

    /// <summary>Whether the chat's client holds one launch open between the person's messages.</summary>
    private bool LaunchStaysOpen
        => ClientOf(_resolution.Serving.Client)?.Capabilities.GoesOnWith == AgentConversationHandling.Rendered;

    /// <summary>The client registered under <paramref name="name"/>; null where none is.</summary>
    private IAgentClient? ClientOf(string name)
        => _clients.FirstOrDefault(client => string.Equals(client.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The one shot: the message is sent, the agent works until its turn ends, its final text is printed (or its one
    /// document, where <c>--json</c> asked for it), and the command ends.
    /// </summary>
    /// <param name="prompt">The one message.</param>
    /// <param name="json">Whether the answer is a document.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    public async Task<CommandOutcome> OneShotAsync(string prompt, bool json, CancellationToken cancellationToken)
    {
        using var front = new TurnFront(_layout, _fileSystem, say: null);
        AgentRunResult result;

        try
        {
            result = await _runner.RunAsync(Request(prompt, messages: null, interrupt: null), front, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            // The turn itself refused - a key the provider rejects, a request the client cannot make - and its refusal
            // is the command's, with the records named.
            return Ended(null, json, front, oneShot: true) with { ExitCode = ex.ExitCode, Message = ex.Message };
        }

        var turn = TurnOf(result);
        _turns.Add(turn);
        _inAll = Fold(_inAll, result.Usage);
        Kept();

        var outcome = Ended(result, json, front, oneShot: true);

        if (!json && result.Status == AgentRunStatus.Completed && result.FinalText is { Length: > 0 })
        {
            outcome = outcome with { Data = [result.FinalText, .. outcome.Data] };
        }

        return outcome;
    }

    /// <summary>
    /// The chat: the person's messages one at a time, the agent's text streamed as it comes and each action one line as
    /// it is decided, until the person leaves - <c>/exit</c>, an end of input, or a second interrupt.
    /// </summary>
    /// <param name="cancellationToken">Stops the command.</param>
    public async Task<CommandOutcome> ChatAsync(CancellationToken cancellationToken)
    {
        var chat = _call.Chat ?? throw new InvalidOperationException("a chat was asked for and no front end was given");
        var open = LaunchStaysOpen;
        var messages = new AgentMessages();
        var interrupt = new AgentTurnInterrupt();
        Task<AgentRunResult>? launch = null;
        var front = new TurnFront(_layout, _fileSystem, chat.Say);
        DateTimeOffset? began = null;
        var left = false;

        _output.Info(SideTaskService.RunCommand, $"side-task '{_layout.Name}' on {_resolution.Serving.Model} through {_resolution.Serving.Client}; /help lists what the chat takes");

        while (!left)
        {
            string? line;

            try
            {
                line = await chat.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null || chat.Interrupts.Presses >= 2)
            {
                break;
            }

            // A line that ends with a backslash goes on to the next.
            while (line.EndsWith("\\", StringComparison.Ordinal)
                && await chat.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } more)
            {
                line = line[..^1].TrimEnd() + "\n" + more;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('/'))
            {
                var asked = Command(line, chat);

                if (asked == Commanded.Left)
                {
                    break;
                }

                if (asked == Commanded.NewLaunch && launch is not null)
                {
                    // A new model: the open launch gives way to a new one, given the conversation so far as a step
                    // that goes on is.
                    messages.Complete();

                    try
                    {
                        _ = await launch.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // The launch ends whole or the bound passes; the chat goes on from what its stream holds.
                    }

                    Snapshot(front, began);
                    launch = null;
                    _continuation = ContinuationOf();
                }

                continue;
            }

            // What the last message's turn said is its own record now; this message begins its own turn.
            Snapshot(front, began);
            began = _clock.GetUtcNow();
            front.Begin();

            if (open)
            {
                if (launch is null)
                {
                    launch = _runner.RunAsync(Request(line, messages, interrupt), front, cancellationToken);
                }
                else
                {
                    messages.Post(line);
                }

                // The turn runs inside the one launch: what it said reaches the front as it happens, and the launch's
                // own end is awaited at the chat's end. The person types on; the launch answers in its own time.
                // An interrupt cuts the agent's current turn alone, the launch left open.
                chat.Interrupts.WhileTurning(count =>
                {
                    if (count == 1)
                    {
                        interrupt.Interrupt();
                    }
                    else
                    {
                        left = true;
                        interrupt.Interrupt();
                    }
                });
            }
            else
            {
                using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var cut = false;

                // The first interrupt cuts the turn where it stands - a cancelled request on a client that stores its
                // conversations - and the chat stays open; a second ends the chat.
                chat.Interrupts.WhileTurning(count =>
                {
                    if (count == 1)
                    {
                        turn.Cancel();
                    }
                    else
                    {
                        cut = true;
                        turn.Cancel();
                    }
                });

                try
                {
                    var result = await _runner.RunAsync(Request(line, messages: null, interrupt: null), front, turn.Token).ConfigureAwait(false);
                    _turns.Add(TurnOf(result));
                    _inAll = Fold(_inAll, result.Usage);
                    Kept();
                    began = null;

                    if (result.Status == AgentRunStatus.ContextExhausted)
                    {
                        Compacted();
                    }
                    else if (result.Status == AgentRunStatus.Cancelled && !cut && !cancellationToken.IsCancellationRequested)
                    {
                        chat.Say("the turn was interrupted; the conversation keeps what was said - ask again, or /exit to leave");
                    }
                }
                catch (OperationCanceledException) when (turn.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    chat.Say(cut ? "the chat ends; what was said is in the records" : "the turn was interrupted; the conversation keeps what was said - ask again, or /exit to leave");
                }
                catch (HarnessException refused)
                {
                    // The turn itself refused - the run's records say why; the chat says it and stays open.
                    chat.Say($"the turn was refused: {refused.Message}");
                }
                finally
                {
                    chat.Interrupts.WhileTurning(null);
                }

                if (cut)
                {
                    break;
                }
            }
        }

        if (launch is not null)
        {
            messages.Complete();
            chat.Interrupts.WhileTurning(null);

            try
            {
                var ended = await launch.ConfigureAwait(false);
                Snapshot(front, began, ended);
            }
            catch (OperationCanceledException)
            {
                // The chat ended; what its launch had said is in its stream.
            }
            catch (HarnessException)
            {
                // The launch failed as any run can; the chat says it through its records and the transcript.
            }
        }

        using (front)
        {
            return Ended(null, json: false, front, oneShot: false);
        }
    }

    /// <summary>
    /// What the front saw of the turn that began at <paramref name="began"/>, as its own record - kept where the chat
    /// runs one request per message and the run's own result said it already.
    /// </summary>
    /// <param name="front">The chat's front, whose seen turn this closes.</param>
    /// <param name="began">When the turn's message was sent; null where none was.</param>
    /// <param name="result">The launch's own result, where the whole chat was one launch; null where it was not.</param>
    private void Snapshot(TurnFront front, DateTimeOffset? began, AgentRunResult? result = null)
    {
        if (began is null || front.Seen.ServedModels.Count == 0)
        {
            return;
        }

        var usage = result is null ? null : result.Usage;
        var turn = new SideTaskTurn(
            began.Value,
            _resolution.Serving.Client,
            _resolution.Serving.Provider,
            _resolution.Serving.Model,
            _effort,
            [.. front.Seen.ServedModels],
            usage,
            front.Seen.ServedModels.Count,
            result?.Status,
            RunWorking: false,
            SideTaskPolicy.Said);

        _turns.Add(turn);
        _inAll = Fold(_inAll, usage);
        Kept();
    }

    /// <summary>The request of one turn of the side-task.</summary>
    /// <param name="message">The person's message, whole.</param>
    /// <param name="messages">The channel a client held open reads the later messages from; null where each message is its own run.</param>
    /// <param name="interrupt">The signal that cuts the current turn alone; null where the run is one turn's.</param>
    private AgentStepRequest Request(string message, AgentMessages? messages, AgentTurnInterrupt? interrupt)
    {
        _attempts++;

        return new AgentStepRequest(
            _layout.Name,
            "side-task",
            $"turn-{_turns.Count + 1}",
            Attempt: _attempts,
            AgentDefinition: null,
            Provider: null,
            _resolution.Serving.Client,
            _tier,
            _resolution.Serving.Model,
            _effort,
            Fallback: null,
            SideTaskService.AgentSystemPrompt,
            message,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ResumeTemplate: null,
            _workspace,
            Output: null,
            _conversation,
            new AgentLimits(MaxTurns: null, MaxBudgetUsd: null, MaxDuration: null, StallSeconds: null),
            _mask)
        {
            Ground = _ground,
            Credential = _order.FirstOrDefault(entry => entry.Usable)?.Credential,
            Withheld = AppointedOf(),
            Order = _order,
            WebSearch = _search,
            Spend = _spend,
            Retries = SideTaskService.DefaultRetries,
            BackoffSeconds = SideTaskService.DefaultBackoffSeconds,
            RecordDirectory = _layout.Directory,
            SessionsDirectory = _layout.SessionsDirectory,
            Compaction = _compaction,
            Continuation = _continuation,
            Program = _program,
            ProgramVersion = _programVersion,
            Messages = messages,
            Interrupt = interrupt,
            Chat = messages is not null,
        };
    }

    /// <summary>Every name the configuration appoints, withheld from the agent's children.</summary>
    private IReadOnlyList<string> AppointedOf()
        => _appointed ??= ReadAppointed();

    private IReadOnlyList<string>? _appointed;

    private IReadOnlyList<string> ReadAppointed()
    {
        try
        {
            return AgentProviders.Resolve(_context.Config) is { Providers: { } providers } ? [.. providers.Appointed().Select(one => one.Name)] : [];
        }
        catch (HarnessException)
        {
            return [];
        }
    }

    /// <summary>The turn's record, from the run's result.</summary>
    private SideTaskTurn TurnOf(AgentRunResult result)
        => new(
            _clock.GetUtcNow(),
            _resolution.Serving.Client,
            _resolution.Serving.Provider,
            _resolution.Serving.Model,
            _effort,
            result.Served,
            result.Usage,
            result.Turns,
            result.Status,
            RunWorking: false,
            SideTaskPolicy.Said);

    /// <summary>Keeps the conversation the turns go on with, and the record whole.</summary>
    private void Kept()
    {
        _conversation = ConversationIn(Stream());

        _records.Write(_layout, _record with
        {
            LastSpoke = _clock.GetUtcNow(),
            Status = SideTaskRecord.Open,
            Conversation = _conversation,
            InAll = _inAll,
            Turns = [.. _turns],
        });
    }

    /// <summary>
    /// The stored conversation the side-task's stream names last, where it holds one it can go on with: the session's
    /// id with the client and provider that began it, and only where its <c>conversation.jsonl</c> is there - a client
    /// that renders its conversations, Claude Code among them, names a session of its own that no conversation can be
    /// handed to. Null where none is.
    /// </summary>
    private AgentSessionRef? ConversationIn(IReadOnlyList<AgentEvent> stream)
    {
        var last = stream.OfType<SessionStarted>().LastOrDefault(started => started.SessionId is { Length: > 0 });

        return last?.SessionId is { } id
            && _fileSystem.FileExists(System.IO.Path.Combine(OrchestratorLayout.SessionDirectoryIn(_layout.SessionsDirectory, id), "conversation.jsonl"))
            ? new AgentSessionRef(last.Client, last.Provider ?? string.Empty, id)
            : null;
    }

    /// <summary>
    /// The engine's compaction of the conversation the side-task's stream holds: what the next message begins a new
    /// conversation from, as a step's answer <c>compact</c> does, said in the chat.
    /// </summary>
    private void Compacted()
    {
        var stream = Stream();

        if (stream.Count == 0)
        {
            return;
        }

        _compaction = AgentCompaction.Reduce(
            stream,
            AgentContinuationBounds.Of(_context.Config.AgentContext?.Continuation),
            AgentCompactionBounds.Of(_context.Config.AgentContext?.Compaction)).Record;
        _conversation = null;

        _output.Info(SideTaskService.RunCommand, "the conversation outgrew the model's context and was compacted; the next message begins a new conversation from what it holds");
    }

    /// <summary>The side-task's stored stream, masked as it holds it; empty where none streamed yet.</summary>
    private IReadOnlyList<AgentEvent> Stream()
    {
        var file = System.IO.Path.Combine(_layout.Directory, StoredStream.FileName);

        if (!_fileSystem.FileExists(file))
        {
            return [];
        }

        var events = new List<AgentEvent>();

        foreach (var line in _fileSystem.ReadAllText(file).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<AgentEvent>(line.TrimEnd('\r'), JsonStateFile.LineOptions) is { } read)
                {
                    events.Add(read);
                }
            }
            catch (JsonException)
            {
                // A torn line a killed writer left takes itself back; the whole ones before it stand.
            }
        }

        return [.. events];
    }

    /// <summary>
    /// The continuation record of the conversation so far, for a new launch on a client no conversation can be handed
    /// to: the side-task's own stream as a continuation renders a step that goes on with its own work.
    /// </summary>
    private string? ContinuationOf()
    {
        var stream = Stream();

        if (stream.Count == 0)
        {
            return null;
        }

        return AgentContinuation.Iterations(stream, AgentContinuationBounds.Of(_context.Config.AgentContext?.Continuation), System.IO.Path.Combine(_layout.Directory, AgentContinuation.CopiesFolder));
    }

    /// <summary>What the person's <c>/</c> command asked, done; whether it ends the chat or begins a new launch.</summary>
    /// <param name="line">The line, beginning with <c>/</c>.</param>
    /// <param name="chat">The chat's front end.</param>
    private Commanded Command(string line, SideTaskChat chat)
    {
        var command = line.Split(' ', 2);
        var argument = command.Length > 1 ? command[1].Trim() : string.Empty;

        switch (command[0].ToLowerInvariant())
        {
            case "/exit":
                return Commanded.Left;

            case "/help":
                chat.Say("/exit leaves the chat; /model <model> and /effort <level> change what the next message is sent with; /records says where this side-task's records are; a line ending in \\ goes on to the next; Ctrl+C interrupts the agent's turn, and a second ends the chat");
                return Commanded.None;

            case "/records":
                chat.Say($"Records: {_layout.Directory}");
                return Commanded.None;

            case "/model":
                if (argument.Length == 0)
                {
                    chat.Say($"the chat runs on {_resolution.Serving.Model}; /model <model> names another");
                    return Commanded.None;
                }

                var providers = AgentProviders.Resolve(_context.Config);

                if (providers is not { Providers: { } resolved } || AgentResolution.Of(resolved, tier: null, argument, _effort, _effortByModel, _call.Client, definition: null) is not { } named)
                {
                    chat.Say($"'{argument}' resolves to no model of the configuration; list-agent-providers lists them");
                    return Commanded.None;
                }

                _resolution = named;
                chat.Say($"the next message goes to {named.Serving.Model} through {named.Serving.Client}");
                return Commanded.NewLaunch;

            case "/effort":
                if (argument.Length == 0)
                {
                    chat.Say(_effort is { Length: > 0 } running ? $"the chat runs at effort {running}" : $"the chat runs at {_resolution.Serving.Model}'s own default effort; /effort <level> names one");
                    return Commanded.None;
                }

                var (asked, _, refusedLevel) = SideTaskEffort.Parse([argument]);

                if (refusedLevel is { } refused)
                {
                    chat.Say(refused);
                    return Commanded.None;
                }

                if (AgentProviders.Resolve(_context.Config) is not { Providers: { } resolvedEffort }
                    || AgentResolution.Of(resolvedEffort, tier: null, _resolution.Serving.Model, asked, effortByModel: null, client: null, definition: null) is not { } again)
                {
                    chat.Say($"'{_resolution.Serving.Model}' no longer resolves to a model of the configuration");
                    return Commanded.None;
                }

                _effort = asked;
                _resolution = again;
                chat.Say(asked is null ? "the next message goes at the model's own default effort" : $"the next message goes at effort {asked}");
                return Commanded.NewLaunch;

            default:
                chat.Say($"'{command[0]}' is no command of this chat; /help lists them");
                return Commanded.None;
        }
    }

    /// <summary>How the side-task ended: its record written ended, its records named, its exit code as the plan's table says.</summary>
    /// <param name="result">The one shot's result; null for a chat.</param>
    /// <param name="json">Whether the one-shot answered with a document.</param>
    /// <param name="front">The front, for what it saw of the turns.</param>
    /// <param name="oneShot">Whether this was a one shot.</param>
    private CommandOutcome Ended(AgentRunResult? result, bool json, TurnFront? front, bool oneShot)
    {
        _records.Write(_layout, _record with
        {
            Status = SideTaskRecord.Ended,
            LastSpoke = _turns.Count > 0 ? _clock.GetUtcNow() : _record.LastSpoke,
            Conversation = _conversation,
            InAll = _inAll,
            Turns = [.. _turns],
        });

        var records = $"Records: {_layout.Directory}";

        if (!oneShot || result is null)
        {
            return CommandOutcome.Ok($"side-task '{_layout.Name}' ended, {_turns.Count} turn{(_turns.Count == 1 ? string.Empty : "s")} in all", [records]);
        }

        var exit = result.Status switch
        {
            AgentRunStatus.Completed => HarnessExit.Success,
            AgentRunStatus.NotStarted => HarnessExit.ToolMissing,
            AgentRunStatus.Cancelled => HarnessExit.Incomplete,
            _ => HarnessExit.CommandFailed,
        };
        var line = result.Status == AgentRunStatus.Completed
            ? $"side-task '{_layout.Name}' answered on {(result.Served is [_, ..] served ? served[0].Model : _resolution.Serving.Model)}"
            : $"side-task '{_layout.Name}' ended {result.Status}{(result.Error is null ? string.Empty : ": " + result.Error)}";
        return json
            ? new CommandOutcome(exit, line, [records]) { Data = [SideTaskReports.One(_layout.Name, _layout.Directory, result, [.. (front?.Seen.Denials ?? [])], _turns, exit)] }
            : new CommandOutcome(exit, line, [records]);
    }

    /// <summary>What a turn's usage added to what every turn before it used, in all and per model.</summary>
    private static AgentUsageReport? Fold(AgentUsageReport? inAll, AgentUsageReport? turn)
    {
        if (turn is null)
        {
            return inAll;
        }

        if (inAll is null)
        {
            return turn;
        }

        return new(
            inAll.Total.Plus(turn.Total),
            [.. inAll.PerModel.Concat(turn.PerModel)],
            new AgentCost(
                (inAll.Cost?.Reported ?? 0) + (turn.Cost?.Reported ?? 0) is { } reported && (inAll.Cost?.Reported is not null || turn.Cost?.Reported is not null) ? reported : null,
                (inAll.Cost?.Counted ?? 0) + (turn.Cost?.Counted ?? 0) is { } counted && (inAll.Cost?.Counted is not null || turn.Cost?.Counted is not null) ? counted : null,
                inAll.Cost?.MissingPrice ?? turn.Cost?.MissingPrice));
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
    /// What one turn said to the person and wrote to the side-task's transcript: the agent's text as it streams, each
    /// action one line as it is decided, each denial with its rule - and what the turn used, for the turn's own record.
    /// Every text it is given is masked by the keeper that hands it the event, as the stored stream is. The transcript
    /// is one file a whole side-task appends to, so its log is held until the side-task ends.
    /// </summary>
    private sealed class TurnFront(SideTaskLayout layout, IFileSystem fileSystem, Action<string>? say) : IAgentEventSink, IDisposable
    {
        private readonly SideTaskLayout _layout = layout;
        private readonly IFileSystem _fileSystem = fileSystem;
        private readonly Action<string>? _say = say;
        private AppendLog? _transcript;

        /// <summary>What the turn was seen to do: the models that served it, what they used, its denials.</summary>
        public Seen Seen { get; } = new();

        /// <summary>Begins a turn: what the last said is the last's, kept, and this one starts with none of its own.</summary>
        public void Begin() => Seen.Begin();

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
                    Said(string.Empty);
                    Write(string.Empty);
                    Write("agent:");
                    foreach (var line in Lines(message.Text))
                    {
                        Write($"    {line}");
                    }

                    break;

                case MessageSent sent when sent.Which == AgentMessageKind.FirstPrompt:
                    Write("person:");
                    foreach (var line in Lines(sent.Text))
                    {
                        Write($"    {line}");
                    }

                    break;

                case ToolCallStarted:
                    Seen.Called();
                    break;

                case ToolCallFinished finished:
                    var of = finished.Input.Length > 96 ? finished.Input[..96] + "..." : finished.Input.Replace("\n", " ");
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
                    Said("the conversation was compacted");
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

    /// <summary>What a turn was seen to do, from its events, for its record and its document.</summary>
    internal sealed class Seen
    {
        private readonly List<string> _denials = [];
        private readonly List<AgentServedModel> _served = [];
        private readonly List<AgentUsage> _used = [];
        private int _calls;

        /// <summary>The turn's denials, as they were said.</summary>
        public IReadOnlyList<string> Denials => _denials;

        /// <summary>The models that served the turn, each from its own turn.</summary>
        public IReadOnlyList<AgentServedModel> ServedModels => _served;

        /// <summary>How many tool calls the turn made.</summary>
        public int Calls => _calls;

        /// <summary>Begins a turn: what the last said is the last's.</summary>
        public void Begin()
        {
            _denials.Clear();
            _served.Clear();
            _used.Clear();
            _calls = 0;
        }

        /// <summary>One more call was made.</summary>
        public void Called() => _calls++;

        /// <summary>One more denial was said.</summary>
        public void Denied(string denial) => _denials.Add(denial);

        /// <summary>One model served one turn.</summary>
        public void OneServed(string model, AgentUsage usage)
        {
            _served.Add(new AgentServedModel(model, _served.Count + 1));
            _used.Add(usage);
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
