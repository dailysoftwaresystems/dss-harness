using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// The Claude Code client: every launch a new session, streamed both ways, the engine its host. It writes the
/// <c>initialize</c> request and the prompt, answers every call the launch asks about from the agent's own policy, sends
/// back an answer that does not read against its schema, gives the agent what the engine tells it while it works, asks it
/// for a handoff when the run pauses, and turns everything the launch says into the run's events - which the runner writes
/// as the step's stored stream as they come. No launch is resumed and none keeps a session file: one it leaves all the
/// same is removed.
/// </summary>
/// <remarks>
/// <para>
/// The model is the engine's to choose. Every message the launch writes names the model that wrote it, and one written by
/// any model but the one asked for ends the step <see cref="AgentRunStatus.WrongModel"/>: a step configured for one model
/// and run on another has not done what it was configured to do.
/// </para>
/// <para>
/// A launch that signs in with a key is given its entry's key; one Claude Code reports invalid is passed over, and where
/// the step had done no work yet the next key is given a new launch with the step's prompt. Work a key refused part way
/// through leaves for the engine's fallback, which goes on from what it stored.
/// </para>
/// </remarks>
/// <param name="processes">Starts the launch.</param>
/// <param name="fileSystem">Writes what the launch is given and what it leaves beside the stream.</param>
/// <param name="platform">The machine's case rule and home, for the policy and Claude Code's directory.</param>
/// <param name="sessions">Where the contained launch records its session, so a killed engine's launch is ended by the next command.</param>
/// <param name="clock">When each thing happened, and the bounds of a handoff.</param>
/// <param name="environment">The tool's own environment, which a launch would otherwise inherit; the process's where none is given.</param>
public sealed class ClaudeCodeClient(
    IProcessRunner processes,
    IFileSystem fileSystem,
    IHostPlatform platform,
    ChildSessions sessions,
    TimeProvider clock,
    Func<IReadOnlyDictionary<string, string>>? environment = null) : IAgentClient
{
    /// <summary>The client's key under <c>agentClients</c>.</summary>
    public const string ClientName = "claude-code";

    /// <summary>How long a launch has to take the interrupt before it is ended whole.</summary>
    public static readonly TimeSpan InterruptBound = TimeSpan.FromSeconds(10);

    /// <summary>How long a launch whose input was closed has to end before it is ended whole.</summary>
    public static readonly TimeSpan EndBound = TimeSpan.FromSeconds(5);

    /// <summary>How long a launch has to answer the host's <c>initialize</c> request before it is ended, never started.</summary>
    public static readonly TimeSpan InitializeBound = TimeSpan.FromSeconds(30);

    /// <summary>The id of the host's <c>initialize</c> request, which its answer names.</summary>
    public const string InitializeId = "initialize";

    /// <summary>The stop reason of a message the model declined to write.</summary>
    private const string Declined = "refusal";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IProcessRunner _processes = processes ?? throw new ArgumentNullException(nameof(processes));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IHostPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    private readonly ChildSessions _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Func<IReadOnlyDictionary<string, string>> _environment = environment ?? AgentChildEnvironment.Own;

    /// <summary>What this client can do, as the capabilities table states it.</summary>
    public static AgentClientCapabilities Descriptor { get; } = new(
        Streaming: true,
        Thinking: true,
        WritableWhileWorking: true,
        GoesOnWith: AgentConversationHandling.Rendered,
        SchemaEnforcedOutput: true,
        FileAndCommandTools: AgentToolsOwner.Client,
        Web: AgentToolsOwner.Harness,
        ReportsCost: true,
        EffortLevels: Kind.Efforts,
        ProviderType: Kind.ProviderType,
        ToldModelBy: "--model with the id");

    /// <summary>The client's kind, as the tool builds it in.</summary>
    private static AgentClientKind Kind => AgentProviders.ClientKinds.First(kind => kind.Name == ClientName);

    /// <inheritdoc/>
    public string Name => ClientName;

    /// <inheritdoc/>
    public AgentClientCapabilities Capabilities => Descriptor;

    /// <inheritdoc/>
    public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);

        var began = _clock.GetTimestamp();

        if (run.Model is not { Length: > 0 } model)
        {
            return NotStarted(AgentNotStartedBecause.ConfigurationWrong, "the step names no model, and a launch is told its model by its id", run);
        }

        if (run.Program is not { Length: > 0 } program)
        {
            return NotStarted(AgentNotStartedBecause.ProgramMissing, "no Claude Code program was found ready for this launch", run);
        }

        if (run.Ground is not { } ground)
        {
            return NotStarted(AgentNotStartedBecause.ConfigurationWrong, "the step was given no policy to decide its calls by, and no call runs undecided", run);
        }

        IReadOnlyList<AgentKey?> keys = run.Credential is { } credential && credential.Auth != AgentAuth.Login ? [.. credential.Keys] : [null];

        if (keys.Count == 0)
        {
            return NotStarted(AgentNotStartedBecause.KeyMissing, $"the launch signs in with auth: {run.Credential!.Auth}, and the slot holds no value for it", run);
        }

        // The user's settings are read by every launch: one that would give it a credential refuses the run before any
        // launch, and the file and its digest are what each launch's record names.
        var settings = ClaudeUserSettings.Read(_fileSystem, Transcripts(run).ConfigDirectory, run.Credential?.Auth ?? AgentAuth.Login);

        if (settings.Refusal is { } refusal)
        {
            return NotStarted(AgentNotStartedBecause.ConfigurationWrong, refusal, run);
        }

        var totals = new Totals();
        var tools = ClaudeCodeTools.Of(ground.Given);
        var policy = new AgentPolicy(_fileSystem, _platform, ground);

        for (var entry = 0; ; entry++)
        {
            var launch = new Launch(this, run, model, program, events, policy, tools, keys[entry], settings, totals);
            var outcome = await launch.RunAsync(cancellationToken).ConfigureAwait(false);

            if (outcome.KeyRefused && keys[entry] is { } refused)
            {
                totals.Refused.Add(refused.Name);

                if (entry + 1 < keys.Count && !totals.Worked)
                {
                    await events.EmitAsync(new FallbackStarted(
                        model, run.Provider ?? string.Empty, ClientName, model, run.Provider ?? string.Empty, ClientName,
                        $"key refused: Claude Code reported {refused.Name} invalid", "a new launch with the step's prompt",
                        totals.Turns, ThrewAway: false, CreditAsked: false, refused.Name, keys[entry + 1]!.Name)).ConfigureAwait(false);
                    continue;
                }
            }

            return Finished(outcome.Result, run, totals, _clock.GetElapsedTime(began));
        }
    }

    /// <summary>A run that never began, and why.</summary>
    private static AgentRunResult NotStarted(AgentNotStartedBecause because, string why, AgentRun run)
        => new(AgentRunStatus.NotStarted, because, Model: run.Model, Error: run.Mask.Redact($"client '{ClientName}' was not started: {why}"));

    /// <summary>A launch's result with what every launch of the run used, said, refused and left.</summary>
    private static AgentRunResult Finished(AgentRunResult result, AgentRun run, Totals totals, TimeSpan duration)
        => result with
        {
            Model = run.Model,
            Turns = totals.Turns,
            ToolCalls = totals.Calls,
            Denials = totals.Denials,
            Duration = duration,
            Session = totals.Session,
            Usage = new AgentUsageReport(
                totals.Usage,
                [.. totals.PerModel.Select(served => new AgentModelUsage(served.Key, served.Value))],
                new AgentCost(Reported: totals.Cost)),
            Served = [.. totals.Served],
            KeysRefused = [.. totals.Refused],
            SessionFilesRemoved = [.. totals.Removed],
            Uncontained = [.. totals.Uncontained],
        };

    /// <summary>Claude Code's directory and transcripts as a launch of <paramref name="run"/> reads them: its credential's directory, else the tool's own.</summary>
    private ClaudeTranscripts Transcripts(AgentRun run)
        => new(
            _fileSystem,
            _platform,
            name => name == ClaudeTranscripts.ConfigDirectoryVariable && run.Credential?.ConfigDirectory is { Length: > 0 } directory
                ? directory
                : _environment().GetValueOrDefault(name));

    /// <summary>Whether a model a message names is the one asked for: the same id, or the id with a date after it.</summary>
    internal static bool Same(string asked, string named)
        => string.Equals(asked, named, StringComparison.Ordinal)
            || (named.StartsWith(asked + "-", StringComparison.Ordinal) && named.Length == asked.Length + 9 && named[(asked.Length + 1)..].All(char.IsAsciiDigit));

    /// <summary>What every launch of a run adds to.</summary>
    private sealed class Totals
    {
        public int Turns { get; set; }

        public int Calls { get; set; }

        public int Denials { get; set; }

        public bool Worked { get; set; }

        /// <summary>Whether the run did anything that stands: a call ran, or a turn ended.</summary>
        public bool Acted { get; set; }

        public string? Session { get; set; }

        public decimal? Cost { get; set; }

        public AgentUsage Usage { get; set; } = AgentUsage.None;

        public Dictionary<string, AgentUsage> PerModel { get; } = new(StringComparer.Ordinal);

        public List<AgentServedModel> Served { get; } = [];

        public List<string> Refused { get; } = [];

        public List<string> Removed { get; } = [];

        public List<string> Uncontained { get; } = [];
    }

    /// <summary>
    /// How one launch ended: its result, whether the key it signed in with was refused, and whether the child is ended at
    /// once - a launch that did what it was never let do, that the engine is not the host of, or that stalled is given no
    /// bound to end in by itself.
    /// </summary>
    private sealed record LaunchOutcome(AgentRunResult Result, bool KeyRefused = false, bool KillNow = false);

    /// <summary>What reaches a launch's one loop, in the order it reached it.</summary>
    private abstract record Arrival
    {
        public sealed record Line(string Text) : Arrival;

        public sealed record Ended(Task<ProcessResult> Process) : Arrival;

        public sealed record HandoffAsked(AgentHandoffAsked Asked) : Arrival;

        public sealed record Posted(string Text) : Arrival;

        public sealed record Bound(string Why, Handing While) : Arrival;

        /// <summary>The initialize request's bound passed.</summary>
        public sealed record Uninitialized : Arrival;

        /// <summary>The stall bound of a message the launch had not taken, counted from when <paramref name="Taken"/> messages were taken.</summary>
        public sealed record Untaken(int Taken) : Arrival;
    }

    /// <summary>Where a launch stands with the handoff.</summary>
    private enum Handing
    {
        No,
        Interrupting,
        Asking,
    }

    /// <summary>One launch: one process, and the one loop that reads everything that reaches it, in order.</summary>
    private sealed class Launch(
        ClaudeCodeClient client,
        AgentRun run,
        string model,
        string program,
        IAgentEventSink events,
        AgentPolicy policy,
        IReadOnlyList<string> tools,
        AgentKey? key,
        ClaudeUserSettings.Reading settings,
        Totals totals)
    {
        private readonly Channel<Arrival> _arrivals = Channel.CreateUnbounded<Arrival>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Channel<string> _input = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Dictionary<string, (string Tool, string Input, JsonNode? Asked, long Started)> _calls = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _callAt = [];

        // Every call the engine was asked about, by its id, with whether it allowed it.
        private readonly Dictionary<string, bool> _decided = new(StringComparer.Ordinal);

        // Every request of the launch's the engine answered, by its id.
        private readonly HashSet<string> _answered = new(StringComparer.Ordinal);
        private readonly string _session = Guid.NewGuid().ToString("D");
        private readonly string _auth = run.Credential?.Auth ?? AgentAuth.Login;

        private AppendLog? _control;
        private AgentCallRecords? _records;
        private bool _started;
        private bool _initialized;
        private string? _version = run.ProgramVersion;
        private bool _inTurn;

        // The ids of the messages written that no turn has taken yet, in the order they were written.
        private readonly List<string> _pending = [];
        private int _taken;
        private string? _declined;
        private AgentAccountLimit? _limit;
        private DateTimeOffset? _resets;
        private int _repairs;
        private int _handoffAnswers;
        private Handing _handing;
        private string? _interruptId;
        private string? _held;
        private bool _keyRefused;
        private string? _lastText;
        private string? _lead;
        private decimal _cost;

        public async Task<LaunchOutcome> RunAsync(CancellationToken cancellationToken)
        {
            var files = client._fileSystem;
            var directory = run.StepDirectory;
            files.CreateDirectory(directory);

            var settings = Path.Combine(directory, ClaudeLaunch.SettingsFileName);
            var system = Path.Combine(directory, ClaudeLaunch.SystemFileName);
            files.WriteAllTextAtomic(settings, ClaudeSettings.Render(tools, run.Ground!, run.Commands, client._platform.PathComparison));
            files.WriteAllTextAtomic(system, run.Mask.Redact(run.SystemPrompt));

            var working = run.Workspace.WorkingDirectory;
            var comparison = StringComparer.FromComparison(client._platform.PathComparison);
            var roots = run.Ground!.Roots(client._platform.PathComparison)
                .Where(root => !comparison.Equals(root, working))
                .ToList();

            using var control = _control = AppendLog.Open(files, Path.Combine(directory, ClaudeLaunch.ControlFileName));
            using var records = _records = new AgentCallRecords(files, run, client._clock, events);
            using var stderr = AppendLog.Open(files, Path.Combine(directory, ClaudeLaunch.ErrorFileName));
            using var ending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var request = new ProcessRequest
            {
                FileName = program,
                Arguments = ClaudeLaunch.Arguments(run, model, _session, tools, roots, settings, system),
                WorkingDirectory = working,
                Environment = ClaudeLaunch.Environment(run, key, client._environment().Keys),
                StandardInput = ChildInput.WrittenBy((stream, exited) => Pump(_input.Reader, stream, exited)),
                OnOutputLine = line => _arrivals.Writer.TryWrite(new Arrival.Line(line)),
                OutputKept = StreamKept.TailOfWholeLines,
                OnErrorLine = line => stderr.Append(run.Mask.Redact(line).ReplaceLineEndings(" ")),
                ErrorKept = StreamKept.Tail,
                CutLine = run.Mask.Cut,
                Contained = true,
                SessionRecord = client._sessions.For(directory),
                Timeout = run.Limits.MaxDuration,
            };

            totals.Session = _session;

            // The initialize request the SDK sends before anything else, then the prompt: both written before the child
            // reads a line, so nothing it does comes before the engine is its host.
            Control(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = InitializeId,
                ["request"] = new JsonObject { ["subtype"] = "initialize" },
            });
            await SayAsync(UserPrompt(), AgentMessageKind.FirstPrompt).ConfigureAwait(false);

            var process = client._processes.RunAsync(request, ending.Token);
            _ = process.ContinueWith(
                ended => _arrivals.Writer.TryWrite(new Arrival.Ended(ended)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _ = run.Handoff.WhenAsked.ContinueWith(
                asked => _arrivals.Writer.TryWrite(new Arrival.HandoffAsked(asked.Result)),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            var posting = PostAsync(ending.Token);
            After(InitializeBound, new Arrival.Uninitialized());

            try
            {
                var outcome = await LoopAsync(ending.Token).ConfigureAwait(false);

                if (outcome.Result.Handoff is { } handoff)
                {
                    await Emit(new HandoffAnswered(handoff.Answer is not null, handoff.WhyNone)).ConfigureAwait(false);
                }
                else if (_handing != Handing.No)
                {
                    // A launch that ended another way while it was asked for its handoff answered none, and says so.
                    await Emit(new HandoffAnswered(false, outcome.Result.Error ?? $"the launch ended {outcome.Result.Status}")).ConfigureAwait(false);
                }

                if (outcome.KillNow)
                {
                    await ending.CancelAsync().ConfigureAwait(false);
                }

                await EndAsync(process).ConfigureAwait(false);

                return outcome;
            }
            finally
            {
                // A launch still running is ended whole here, however the loop ended: past the bound its input's close
                // gave it where the loop came to its outcome, at once where a fault or the hard stop ended the loop.
                _input.Writer.TryComplete();
                await ending.CancelAsync().ConfigureAwait(false);
                await Quietly(posting).ConfigureAwait(false);

                try
                {
                    // However the launch ended - its outcome, a fault of the loop's, the hard stop - what its end left is
                    // said once it has gone: what its container could not reach, and a session file it kept all the same.
                    await EndedAsync(process).ConfigureAwait(false);
                    await RemoveSessionFileAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The run stopped with no room left in its record: what is left to say reaches nothing, and how the
                    // launch ended stands as it is.
                }
            }
        }

        /// <summary>The step's prompt, as the first message of a launch.</summary>
        private string UserPrompt() => run.UserPrompt;

        /// <summary>Reads what reaches the launch, one thing at a time, until it has ended one way or another.</summary>
        private async Task<LaunchOutcome> LoopAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var arrival = await _arrivals.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                var outcome = arrival switch
                {
                    Arrival.Line line => await OnLineAsync(line.Text).ConfigureAwait(false),
                    Arrival.Ended ended => OnEnded(ended.Process),
                    Arrival.HandoffAsked asked => await OnHandoffAskedAsync(asked.Asked).ConfigureAwait(false),
                    Arrival.Posted posted => await OnPostedAsync(posted.Text).ConfigureAwait(false),
                    Arrival.Bound bound => Bounded(bound),
                    Arrival.Uninitialized => _initialized
                        ? null
                        : NotHost($"{Claude} did not answer the host's initialize request within {InitializeBound.TotalSeconds:0} s: this tool runs Claude Code only as its host"),
                    Arrival.Untaken untaken => untaken.Taken == _taken && _pending.Count > 0 && !_inTurn
                        ? Ends(Failed(AgentRunStatus.Stalled, $"Claude Code did not take the engine's message within {run.Limits.StallSeconds} s ({Claude})"), killNow: true)
                        : null,
                    _ => null,
                };

                if (outcome is not null)
                {
                    return outcome;
                }
            }
        }

        private async Task<LaunchOutcome?> OnLineAsync(string text)
        {
            var line = ClaudeLine.Read(text);

            // What the launch runs as is said by its init before it does anything: one that works before its init has
            // given nothing its tools and its credential could be held to, and nothing it does is taken as its work.
            if (!_started && line is ClaudeLine.User or ClaudeLine.Piece or ClaudeLine.Assistant or ClaudeLine.ControlRequest or ClaudeLine.Refusal or ClaudeLine.Result)
            {
                if (line is ClaudeLine.ControlRequest early)
                {
                    Record(early.Node);
                }

                return NotHost($"{Claude} began its work before its init said what it runs as, so its tools and its credential were never held to this launch's: this tool runs Claude Code only as its host");
            }

            switch (line)
            {
                case ClaudeLine.Init init:
                    return await OnInitAsync(init).ConfigureAwait(false);

                case ClaudeLine.User user:
                    return await OnUserAsync(user).ConfigureAwait(false);

                case ClaudeLine.Piece piece:
                    await OnPieceAsync(piece).ConfigureAwait(false);
                    return null;

                case ClaudeLine.Assistant assistant:
                    return await OnAssistantAsync(assistant).ConfigureAwait(false);

                case ClaudeLine.ControlRequest asked:
                    Record(asked.Node);
                    await OnControlRequestAsync(asked).ConfigureAwait(false);
                    return null;

                case ClaudeLine.ControlResponse answered when _answered.Contains(answered.RequestId):
                    // The launch writes back each answer the engine gave it: kept once, as the engine wrote it.
                    return null;

                case ClaudeLine.ControlResponse answered:
                    Record(answered.Node);
                    return await OnControlResponseAsync(answered).ConfigureAwait(false);

                case ClaudeLine.ControlCancel cancelled:
                    Record(cancelled.Node);
                    return null;

                case ClaudeLine.Refusal refusal:
                    return await OnRefusalAsync(refusal).ConfigureAwait(false);

                case ClaudeLine.RateLimit limit:
                    return OnRateLimit(limit);

                case ClaudeLine.Retry retry:
                    await Emit(new WaitingToRetry(Masked(retry.Cause), retry.Attempt, retry.Of, retry.Delay, client._clock.GetUtcNow() + retry.Delay)).ConfigureAwait(false);
                    return null;

                case ClaudeLine.Result result:
                    return await OnResultAsync(result).ConfigureAwait(false);

                default:
                    return null;
            }
        }

        private async Task<LaunchOutcome?> OnInitAsync(ClaudeLine.Init init)
        {
            var named = init.Model ?? model;
            _version = init.Version ?? _version;

            // What the launch runs as is held at every turn's init: no tool it was not given, and no credential but the
            // one its way of signing in gave.
            var given = run.Output is null ? tools : [.. tools, ClaudeCodeTools.StructuredOutput];

            if (init.Tools.FirstOrDefault(tool => !given.Contains(tool, StringComparer.Ordinal)) is { } offered)
            {
                return NotHost(ClaudeCodeTools.NeverOffered.Contains(offered, StringComparer.Ordinal)
                    ? $"{Claude} offered the tool '{offered}', which this tool never gives an agent"
                    : $"{Claude} offered the tool '{offered}', which this launch was not given");
            }

            var sources = ClaudeLaunch.ApiKeySources(_auth);

            if (init.ApiKeySource is { } source && !sources.Contains(source, StringComparer.Ordinal))
            {
                return NotHost($"{Claude} signed in by '{source}', and this launch signs in with {AgentAuth.Described(_auth)}, which Claude Code names '{string.Join("' or '", sources)}'");
            }

            if (!_started)
            {
                _started = true;

                // An alias names no id a message could be compared with: what the launch says it runs is what it stands
                // for in this launch, and every message after is held to that.
                _held = model.Contains('-', StringComparison.Ordinal) ? model : named;

                await Emit(new SessionStarted(
                    ClientName,
                    named,
                    FileContentHash.Of(Encoding.UTF8.GetBytes(run.SystemPrompt)),
                    init.Tools,
                    [.. run.Skills.Select(skill => skill.Name)],
                    HandedConversation: false,
                    run.Provider,
                    Effort: run.Effort,
                    SessionId: _session,
                    Credential: key is null ? new AgentCredentialSource(AgentCredentialSource.Login) : new AgentCredentialSource(AgentCredentialSource.EnvironmentFile, key.Name),
                    ApiKeySource: init.ApiKeySource,
                    SettingsFile: settings.File,
                    SettingsDigest: settings.Digest,
                    Hooks: ClaudeUserSettings.HooksOff)).ConfigureAwait(false);
            }

            return Served(named, "its launch");
        }

        private async Task<LaunchOutcome?> OnUserAsync(ClaudeLine.User user)
        {
            if (user.Replay)
            {
                // Messages are taken in the order they were written: the echo is of the oldest still waiting.
                if (_pending.Count > 0)
                {
                    _pending.RemoveAt(0);
                }

                _taken++;

                if (!_inTurn)
                {
                    _inTurn = true;
                    _declined = null;
                    _limit = null;
                    totals.Turns++;
                    await Emit(new TurnStarted(totals.Turns)).ConfigureAwait(false);
                }

                return null;
            }

            foreach (var result in user.Results)
            {
                if (!_calls.Remove(result.CallId, out var call))
                {
                    continue;
                }

                totals.Worked = true;
                var (content, file) = _records!.Kept(result.CallId, result.Content);

                await Emit(new ToolCallFinished(result.CallId, result.Ok, client._clock.GetElapsedTime(call.Started), call.Input, content, file)).ConfigureAwait(false);

                // Every call that ran is one the engine was asked about and allowed: one that was not ends the launch at
                // once, its result told above, since the launch has done what nothing decided.
                if (!result.Ran)
                {
                    continue;
                }

                totals.Acted = true;

                if (!_decided.TryGetValue(result.CallId, out var allowed))
                {
                    return new LaunchOutcome(Failed(AgentRunStatus.Failed, $"Claude Code ran '{call.Tool}' without asking the engine ({Claude})"), KillNow: true);
                }

                if (!allowed)
                {
                    return new LaunchOutcome(Failed(AgentRunStatus.Failed, $"Claude Code ran '{call.Tool}' though the engine refused it ({Claude})"), KillNow: true);
                }
            }

            return null;
        }

        private async Task OnPieceAsync(ClaudeLine.Piece piece)
        {
            switch (piece.Kind)
            {
                case "text" when piece.Text is { Length: > 0 } text:
                    await Emit(new TextDelta(Masked(text))).ConfigureAwait(false);
                    break;
                case "thinking" when piece.Text is { Length: > 0 } thinking:
                    await Emit(new ThinkingDelta(Masked(thinking))).ConfigureAwait(false);
                    break;
                case "start":
                    // A message's blocks are counted from naught again: no piece of it is an earlier message's call.
                    _callAt.Clear();
                    break;
                case "stop" when piece.Text?.Split(':', 2) is [Declined, .. var category]:
                    _declined = category is [{ } named] ? named : string.Empty;
                    break;
                case "call" when piece.CallId is { } id:
                    if (piece.Text == ClaudeCodeTools.StructuredOutput)
                    {
                        // The answer's own block, which is no call: none of its pieces is told as one.
                        _callAt.Remove(piece.Index ?? -1);
                        break;
                    }

                    if (piece.Index is { } index)
                    {
                        _callAt[index] = id;
                    }

                    await StartedAsync(id, piece.Text ?? string.Empty).ConfigureAwait(false);
                    break;
                case "input" when piece.Index is { } at && _callAt.TryGetValue(at, out var call) && piece.Text is { Length: > 0 } fragment:
                    await Emit(new ToolCallInputDelta(call, Masked(fragment))).ConfigureAwait(false);
                    break;
            }
        }

        private async Task<LaunchOutcome?> OnAssistantAsync(ClaudeLine.Assistant assistant)
        {
            switch (assistant.Error)
            {
                case "authentication_failed":
                    _keyRefused = true;
                    break;

                // Written only after Claude Code's own tries: the account is at its limit, which the turn's end ends the step on.
                case "rate_limit":
                    _limit = new AgentAccountLimit(BalanceSpent: false, _resets);
                    break;
                case "billing_error":
                    _limit = new AgentAccountLimit(BalanceSpent: true);
                    break;
            }

            if (assistant.StopReason == Declined)
            {
                _declined = assistant.Category ?? _declined ?? string.Empty;
            }

            if (assistant.Model is { } named && named != ClaudeLine.Synthetic && Served(named, $"its turn {totals.Turns}") is { } wrong)
            {
                return wrong;
            }

            foreach (var call in assistant.Calls.Where(call => call.Tool != ClaudeCodeTools.StructuredOutput))
            {
                await StartedAsync(call.Id, call.Tool).ConfigureAwait(false);
                _calls[call.Id] = (call.Tool, Masked(call.Input?.ToJsonString() ?? "{}"), call.Input, _calls.TryGetValue(call.Id, out var known) ? known.Started : client._clock.GetTimestamp());
            }

            var calls = assistant.Calls.Where(call => call.Tool != ClaudeCodeTools.StructuredOutput).ToList();

            if (assistant.Text.Length > 0 || calls.Count > 0)
            {
                totals.Worked |= assistant.Error is null;
                _lastText = assistant.Text.Length > 0 ? assistant.Text : _lastText;

                await Emit(new AssistantMessage(
                    Masked(assistant.Text),
                    [.. calls.Select(call => new AgentToolCall(call.Id, call.Tool))],
                    assistant.Thinking is { } thinking ? Masked(thinking) : null)).ConfigureAwait(false);
            }

            return null;
        }

        private async Task StartedAsync(string id, string tool)
        {
            if (_calls.ContainsKey(id))
            {
                return;
            }

            _calls[id] = (tool, "{}", null, client._clock.GetTimestamp());
            totals.Calls++;
            await Emit(new ToolCallStarted(id, tool, AgentToolRunner.ClaudeCode)).ConfigureAwait(false);
        }

        private async Task OnControlRequestAsync(ClaudeLine.ControlRequest asked)
        {
            if (asked.Subtype != "can_use_tool")
            {
                Control(Error(asked.RequestId, $"this host answers no '{asked.Subtype}' request"));
                return;
            }

            var tool = asked.Tool ?? string.Empty;
            var answer = Decide(tool, asked.Input);

            Asked(asked.CallId, answer is AgentPolicyAnswer.Allowed);

            if (answer is AgentPolicyAnswer.Denied denied)
            {
                totals.Denials++;
                var subject = ClaudeToolDecider.SubjectOf(tool, asked.Input, run.Workspace.WorkingDirectory) ?? asked.Input?.ToJsonString() ?? string.Empty;
                Control(Answer(asked.RequestId, new JsonObject { ["behavior"] = "deny", ["message"] = Masked(denied.Reason) }));
                await _records!.DeniedAsync(tool, subject, denied).ConfigureAwait(false);
                return;
            }

            Control(Answer(asked.RequestId, new JsonObject { ["behavior"] = "allow", ["updatedInput"] = asked.Input?.DeepClone() ?? new JsonObject() }));
        }

        /// <summary>
        /// The engine's answer to a call of <paramref name="tool"/>: its policy's, or, in the handoff turn, that turn's
        /// refusal. A fault of the engine's own while deciding is a denial too, by <see cref="AgentPolicy.UndecidedRule"/> - recorded as
        /// every denial is, and never a failure that escapes - for nothing runs that was not decided.
        /// </summary>
        private AgentPolicyAnswer Decide(string tool, JsonNode? input)
        {
            if (_handing != Handing.No)
            {
                return new AgentPolicyAnswer.Denied(AgentHandoffSignal.TurnRule, AgentPolicySource.Builtin, AgentHandoffSignal.ToolRefusal);
            }

            try
            {
                return ClaudeToolDecider.Decide(policy, tools, tool, input, run.Workspace.WorkingDirectory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new AgentPolicyAnswer.Denied(AgentPolicy.UndecidedRule, AgentPolicySource.Builtin, $"the engine could not decide this call ({ex.Message.TrimEnd('.')}): it denies what it cannot decide");
            }
        }

        /// <summary>Notes that the engine was asked about the call <paramref name="callId"/>, and whether it allowed it; the last answer stands.</summary>
        private void Asked(string? callId, bool allowed)
        {
            if (callId is not null)
            {
                _decided[callId] = allowed;
            }
        }

        /// <summary>
        /// A call Claude Code refused by itself. Its own deny rules are the engine's restriction rules written a second
        /// time, so a call they refuse is one the engine refuses too; one the engine would have allowed was refused by a
        /// launch that did not ask its host - no call of it would ever be decided by the engine - and ends it.
        /// </summary>
        private async Task<LaunchOutcome?> OnRefusalAsync(ClaudeLine.Refusal refusal)
        {
            totals.Denials++;

            // Claude Code's own refusal is recorded as every denial is: a line of the step's denials, and its event.
            await _records!.DeniedAsync(refusal.Tool ?? string.Empty, string.Empty, new AgentPolicyAnswer.Denied(string.Empty, AgentPolicySource.ClaudeCode, refusal.Message)).ConfigureAwait(false);

            if (refusal.CallId is not { } id || _decided.ContainsKey(id) || !_calls.TryGetValue(id, out var call))
            {
                return null;
            }

            // What the engine cannot decide it would not have allowed: such a call is a denial, as its refusal said.
            return Decide(call.Tool, call.Asked) is AgentPolicyAnswer.Allowed
                ? NotHost($"Claude Code refused '{call.Tool}' without asking the engine, which allows it ({Claude}): {refusal.Message}; this tool runs Claude Code only as its host")
                : null;
        }

        /// <summary>
        /// The account's standing, as the stream says it: <c>rejected</c> is an account at its limit - an allowance used
        /// up, or its included usage spent - which ends the step at once, with when it lifts where it says.
        /// </summary>
        private LaunchOutcome? OnRateLimit(ClaudeLine.RateLimit limit)
        {
            _resets = limit.ResetsAt ?? _resets;

            if (limit.Status != "rejected")
            {
                return null;
            }

            return Ends(AtLimit(
                new AgentAccountLimit(BalanceSpent: limit.ErrorCode == "credits_required", limit.ResetsAt),
                $"its {limit.Window ?? "allowance"} rejected{(limit.ErrorCode is { } code ? $" ({code})" : string.Empty)}"));
        }

        /// <summary>The step's end at an account's limit.</summary>
        private AgentRunResult AtLimit(AgentAccountLimit limit, string said)
            => Failed(
                AgentRunStatus.RateLimited,
                $"Claude Code reported the account at its limit, {(limit.BalanceSpent ? "its balance or included usage spent" : "an allowance used up")}{(limit.Lifts is { } lifts ? $" until {lifts:u}" : string.Empty)}: {said}") with
            {
                Limit = limit,
            };

        private async Task<LaunchOutcome?> OnControlResponseAsync(ClaudeLine.ControlResponse answered)
        {
            if (answered.RequestId == InitializeId)
            {
                _initialized = answered.Succeeded;

                if (!answered.Succeeded)
                {
                    return NotHost($"{Claude} refused the host's initialize request ({Masked(answered.Error ?? "no reason given")}): this tool runs Claude Code only as its host");
                }

                // A message written before the launch answered waited on its start, not on the launch: its bound starts
                // now, as does the bound of an interrupt written meanwhile.
                Watch();
                WatchInterrupt();

                return null;
            }

            if (answered.RequestId == _interruptId && !answered.Succeeded)
            {
                return HandedOff(null, $"Claude Code refused the interrupt: {Masked(answered.Error ?? "no reason given")}");
            }

            if (answered.RequestId != _interruptId)
            {
                return null;
            }

            _interruptId = null;

            // The receipt names each waiting message the interrupt dropped, by the id the engine gave it: no turn takes
            // them. One Claude Code had taken for its next turn already is not named, and that turn runs - cut - first.
            _pending.RemoveAll(id => answered.Cancelled?.Contains(id, StringComparer.Ordinal) == true);

            return await AskWhenCutAsync().ConfigureAwait(false);
        }

        private async Task<LaunchOutcome?> OnResultAsync(ClaudeLine.Result result)
        {
            _inTurn = false;
            totals.Acted = true;
            totals.Usage = totals.Usage.Plus(result.Usage);
            var served = _held ?? model;
            totals.PerModel[served] = totals.PerModel.TryGetValue(served, out var used) ? used.Plus(result.Usage) : result.Usage;

            if (result.CostUsd is { } cost)
            {
                // The launch's running total, never another launch's: what the run cost is each launch's last.
                totals.Cost = (totals.Cost ?? 0) - _cost + cost;
                _cost = cost;
            }

            if (totals.Served.Count == 0)
            {
                totals.Served.Add(new AgentServedModel(served, 1));
            }

            await Emit(new TurnFinished(totals.Turns, served, result.Usage, result.StopReason)).ConfigureAwait(false);
            await Emit(new UsageReported(result.Usage, totals.Usage)).ConfigureAwait(false);

            switch (_handing)
            {
                case Handing.Interrupting:
                    return await AskWhenCutAsync().ConfigureAwait(false);
                case Handing.Asking:
                    return await HandoffAnsweredAsync(result).ConfigureAwait(false);
            }

            // A message the launch has not taken yet begins a turn of its own: this result is not the step's answer.
            if (_pending.Count > 0)
            {
                Watch();
                return null;
            }

            if (_keyRefused)
            {
                return new LaunchOutcome(Failed(AgentRunStatus.Failed, $"Claude Code reported the key {key?.Name ?? "it signed in with"} invalid: {result.Text ?? "no text"}"), KeyRefused: true);
            }

            if (Ending(result) is { } ended)
            {
                return new LaunchOutcome(ended);
            }

            if (run.Output is not { } output)
            {
                return new LaunchOutcome(new AgentRunResult(AgentRunStatus.Completed, FinalText: Masked(result.Text ?? _lastText ?? string.Empty)));
            }

            var problems = StructuredOutputGate.Problems(output, result.Structured);

            if (problems.Count == 0)
            {
                return new LaunchOutcome(new AgentRunResult(
                    AgentRunStatus.Completed,
                    FinalText: Masked(result.Text ?? string.Empty),
                    StructuredOutput: Masked(result.Structured!.ToJsonString())));
            }

            if (_repairs >= output.MaxRepairs)
            {
                return new LaunchOutcome(Failed(AgentRunStatus.InvalidOutput, $"the answer did not read against its schema after {_repairs} repairs: {string.Join("; ", problems)}"));
            }

            _repairs++;
            await Emit(new OutputRepair(_repairs, [.. problems.Select(Masked)])).ConfigureAwait(false);
            await SayAsync(StructuredOutputGate.Repair(problems), AgentMessageKind.Repair).ConfigureAwait(false);

            return null;
        }

        /// <summary>How a turn that ended on an error, a decline or the account's limit ends the step, where it did; null for a success.</summary>
        private AgentRunResult? Ending(ClaudeLine.Result result)
        {
            var said = Masked(string.Join("; ", result.Errors.Count > 0 ? result.Errors : [result.Text ?? result.Subtype]));

            if (_limit is { } limit)
            {
                return AtLimit(limit, said);
            }

            if (_declined is not null || result.StopReason == Declined)
            {
                return Failed(AgentRunStatus.Refused, $"the model declined the request{(_declined is { Length: > 0 } category ? $" ({category})" : string.Empty)}: {said}") with
                {
                    DeclinedFor = _declined is { Length: > 0 } named ? named : null,
                };
            }

            return (result.Subtype, result.TerminalReason) switch
            {
                ("error_max_turns", _) => Failed(AgentRunStatus.MaxTurns, said),
                ("error_max_budget_usd", _) => Failed(AgentRunStatus.BudgetSpent, $"Claude Code ended the launch on its --max-budget-usd: {said}"),
                ("error_max_structured_output_retries", _) => Failed(AgentRunStatus.InvalidOutput, $"Claude Code asked for the schema as often as it will: {said}"),
                (_, "prompt_too_long" or "blocking_limit") => Failed(AgentRunStatus.ContextExhausted, said),
                ("success", _) when !result.IsError => null,
                _ => Failed(AgentRunStatus.Failed, said),
            };
        }

        private async Task<LaunchOutcome?> OnHandoffAskedAsync(AgentHandoffAsked asked)
        {
            if (_handing != Handing.No)
            {
                return null;
            }

            await Emit(new HandoffAsked(asked.Why, run.Handoff.Bound)).ConfigureAwait(false);
            _lead = asked.Lead;
            After(run.Handoff.Bound, new Arrival.Bound("the bound passed before the agent answered", Handing.Asking));

            if (!_inTurn && _pending.Count == 0)
            {
                // Between turns there is nothing to cut: the agent is asked at once.
                return await AskHandoffAsync().ConfigureAwait(false);
            }

            _handing = Handing.Interrupting;
            _interruptId = "interrupt";
            Control(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = _interruptId,
                ["request"] = new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = true },
            });
            WatchInterrupt();

            return null;
        }

        /// <summary>
        /// Starts the interrupt's bound once the launch reads its input: an interrupt written before the launch answered
        /// the initialize waits on its start, so its bound starts then.
        /// </summary>
        private void WatchInterrupt()
        {
            if (_initialized && _handing == Handing.Interrupting && _interruptId is not null)
            {
                After(InterruptBound, new Arrival.Bound($"the agent did not take the interrupt within {InterruptBound.TotalSeconds:0} s", Handing.Interrupting));
            }
        }

        /// <summary>
        /// Asks the agent for its handoff once the interrupt has left nothing to come: no turn runs, and every message
        /// written was taken or dropped - a turn still to come would answer the question in its place. A question written
        /// before the receipt is read after the interrupt, which then has nothing left to drop.
        /// </summary>
        private async Task<LaunchOutcome?> AskWhenCutAsync()
            => _handing == Handing.Interrupting && !_inTurn && _pending.Count == 0
                ? await AskHandoffAsync().ConfigureAwait(false)
                : null;

        private async Task<LaunchOutcome?> AskHandoffAsync()
        {
            _handing = Handing.Asking;
            _handoffAnswers++;
            var question = _lead is { Length: > 0 } lead ? $"{lead}\n\n{AgentHandoffSignal.Prompt}" : AgentHandoffSignal.Prompt;
            await SayAsync(question, AgentMessageKind.HandoffQuestion).ConfigureAwait(false);

            return null;
        }

        private async Task<LaunchOutcome?> HandoffAnsweredAsync(ClaudeLine.Result result)
        {
            var answer = result.Structured is { } structured && structured["notes"] is JsonValue notes && notes.TryGetValue<string>(out var text)
                ? text
                : result.Structured?.ToJsonString() ?? result.Text;

            if (Ending(result) is { } ended)
            {
                return HandedOff(null, $"the handoff turn ended {ended.Status}: {ended.Error}");
            }

            if (answer is { Length: > 0 })
            {
                return HandedOff(answer, null);
            }

            if (_handoffAnswers >= run.Handoff.MaxAnswers)
            {
                return HandedOff(null, $"the agent gave no handoff in {_handoffAnswers} answers");
            }

            return await AskHandoffAsync().ConfigureAwait(false);
        }

        private LaunchOutcome HandedOff(string? answer, string? whyNone)
        {
            var handoff = answer is null ? new AgentHandoff(WhyNone: whyNone) : new AgentHandoff(Masked(answer));

            return new LaunchOutcome(new AgentRunResult(AgentRunStatus.HandedOff, Handoff: handoff));
        }

        private async Task<LaunchOutcome?> OnPostedAsync(string text)
        {
            if (_handing != Handing.No)
            {
                return null;
            }

            await SayAsync(text, AgentMessageKind.PlanUpdate).ConfigureAwait(false);

            return null;
        }

        /// <summary>
        /// A bound that passed, where it still holds: the interrupt's ends nothing once the launch said it took the
        /// interrupt - the turn it cuts then ends within the handoff's own bound - and nothing once the turn was cut.
        /// </summary>
        private LaunchOutcome? Bounded(Arrival.Bound bound)
            => _handing == Handing.No || (bound.While == Handing.Interrupting && (_handing != Handing.Interrupting || _interruptId is null))
                ? null
                : HandedOff(null, bound.Why);

        private LaunchOutcome OnEnded(Task<ProcessResult> process)
        {
            if (process.IsCanceled || process.Exception?.InnerException is OperationCanceledException)
            {
                throw new OperationCanceledException(AgentRunner.StoppedError);
            }

            if (process.Exception?.InnerException is ProgramStartException unstarted)
            {
                return new LaunchOutcome(new AgentRunResult(
                    AgentRunStatus.NotStarted,
                    AgentNotStartedBecause.ProgramMissing,
                    Error: Masked($"client '{ClientName}' was not started: {unstarted.Message}")));
            }

            var ended = process.Exception?.InnerException is { } failure
                ? $"Claude Code could not be run: {failure.Message}"
                : process.Result.TimedOut
                    ? null
                    : $"Claude Code ended before its turn's result, exit {process.Result.ExitCode}: {process.Result.StandardError.Trim()}";

            if (_handing != Handing.No)
            {
                return HandedOff(null, "the client had already ended");
            }

            return ended is null
                ? new LaunchOutcome(Failed(AgentRunStatus.TimedOut, $"the launch ran past its {run.Limits.MaxDuration}"))
                : new LaunchOutcome(Failed(AgentRunStatus.Failed, ended));
        }

        /// <summary>Holds every message to the model asked for, ending the step where another wrote one.</summary>
        private LaunchOutcome? Served(string named, string where)
        {
            if (_held is null || Same(_held, named))
            {
                return null;
            }

            return new LaunchOutcome(Failed(
                AgentRunStatus.WrongModel,
                $"the step asked for '{_held}' and {where} was written by '{named}'; a step's model is its configuration's and the engine's to choose, and neither chose this one"));
        }

        private AgentRunResult Failed(AgentRunStatus status, string error)
            => new(status, Error: Masked(error));

        /// <summary>
        /// A launch the engine is not the host of: never started where nothing it did stands - no call ran and no turn
        /// ended, for the program does not start as this tool starts it - and failed where something does; ended at once
        /// either way.
        /// </summary>
        private LaunchOutcome NotHost(string why)
            => new(totals.Acted ? Failed(AgentRunStatus.Failed, why) : ClaudeCodeClient.NotStarted(AgentNotStartedBecause.ProgramMissing, why, run), KillNow: true);

        /// <summary>The step's end <paramref name="ended"/>; in the handoff turn, the handoff that turn did not give.</summary>
        private LaunchOutcome Ends(AgentRunResult ended, bool killNow = false)
            => (_handing == Handing.No ? new LaunchOutcome(ended) : HandedOff(null, $"the handoff turn ended {ended.Status}: {ended.Error}")) with { KillNow = killNow };

        /// <summary>The program, by its version where it is known, as a refusal names it.</summary>
        private string Claude => _version is { Length: > 0 } version ? $"claude {version}" : "claude";

        /// <summary>
        /// Starts the stall bound where the launch is between turns with a message it has not taken: a message the engine
        /// writes begins a turn within the bound, or the step is <see cref="AgentRunStatus.Stalled"/>. A message written
        /// while a turn runs is taken when the turn ends, and one written before the launch answered the initialize is
        /// taken once it has, so their bounds start then.
        /// </summary>
        private void Watch()
        {
            if (run.Limits.StallSeconds is > 0 and var seconds && _initialized && _pending.Count > 0 && !_inTurn)
            {
                After(TimeSpan.FromSeconds(seconds), new Arrival.Untaken(_taken));
            }
        }

        /// <summary>Writes a user message, as the event that says so first.</summary>
        private async Task SayAsync(string text, AgentMessageKind kind)
        {
            await Emit(new MessageSent(Masked(text), kind)).ConfigureAwait(false);
            // Each message carries an id of the engine's, by which the interrupt's receipt names the messages it dropped.
            var id = Guid.NewGuid().ToString("D");
            _pending.Add(id);
            Write(new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
                ["uuid"] = id,
            });
            Watch();
        }

        /// <summary>Writes a control message, and records it.</summary>
        private void Control(JsonObject message)
        {
            if (message["response"]?["request_id"] is JsonValue id && id.TryGetValue<string>(out var answered))
            {
                _answered.Add(answered);
            }

            Record(message);
            Write(message);
        }

        private void Write(JsonObject message) => _input.Writer.TryWrite(message.ToJsonString());

        private void Record(JsonNode message) => _control?.Append(Masked(message.ToJsonString()));

        private ValueTask Emit(AgentEvent @event) => events.EmitAsync(@event);

        private string Masked(string text) => run.Mask.Redact(text);

        /// <summary>Starts the bound <paramref name="span"/>, which hands the loop <paramref name="passed"/> once it passes.</summary>
        private void After(TimeSpan span, Arrival passed)
            => _ = Task.Delay(span, client._clock).ContinueWith(
                _ => _arrivals.Writer.TryWrite(passed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        /// <summary>Hands each message the engine posts to the loop, until the launch ends.</summary>
        private async Task PostAsync(CancellationToken cancellationToken)
        {
            await foreach (var message in run.Messages.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _arrivals.Writer.TryWrite(new Arrival.Posted(message));
            }
        }

        /// <summary>
        /// Closes the launch's input and gives it the bound to end in by itself: one still running once the bound has
        /// passed is ended whole as the launch's run ends.
        /// </summary>
        private async Task EndAsync(Task<ProcessResult> process)
        {
            _input.Writer.TryComplete();
            await Task.WhenAny(process, Task.Delay(EndBound, client._clock)).ConfigureAwait(false);
        }

        /// <summary>
        /// Waits for the launch to have ended, and says what its container could not reach at that end, where it could not
        /// reach all it held - however it ended, stopped among the ways: what the launch started may still run, which the
        /// step's stream and its result say.
        /// </summary>
        private async Task EndedAsync(Task<ProcessResult> process)
        {
            string? unreached;

            try
            {
                unreached = (await process.ConfigureAwait(false)).Uncontained;
            }
            catch (ProcessStoppedException stopped)
            {
                unreached = stopped.Uncontained;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ProgramStartException or IOException)
            {
                // A launch that never started, or a stop that says nothing of its container: how it ended was seen to
                // where it mattered.
                return;
            }

            if (unreached is not null)
            {
                var said = Masked($"Claude Code's launch ended, and {unreached}");
                totals.Uncontained.Add(said);
                await Emit(new Notice(said)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Removes the session file the launch left all the same, told to keep none - its own, by its id, and nothing else -
        /// and says each directory a file could not be looked for in.
        /// </summary>
        private async Task RemoveSessionFileAsync()
        {
            var found = client.Transcripts(run).Find(_session);

            foreach (var unread in found.Unread)
            {
                await Emit(new Notice($"A session file Claude Code may have kept though told to keep none could not be looked for in {Masked(unread)}")).ConfigureAwait(false);
            }

            foreach (var file in found.Files.Where(file => Path.GetFileName(file) == _session + ".jsonl"))
            {
                try
                {
                    client._fileSystem.DeleteFile(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The launch's work stands: the file it left is said, and so is why it is still there.
                    await Emit(new Notice($"Claude Code kept a session file though told to keep none, and it could not be removed: '{file}': {Masked(ex.Message)}")).ConfigureAwait(false);
                    continue;
                }

                totals.Removed.Add(file);
                await Emit(new Notice($"Claude Code kept a session file though told to keep none, and it was removed: '{file}'")).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Writes what the loop gives the launch's input, line by line, until the input is closed, the launch stops reading
        /// or it has exited - a launch that ended by itself is waited on for nothing more, so its end reaches the loop.
        /// </summary>
        private static void Pump(ChannelReader<string> lines, Stream stream, CancellationToken exited)
        {
            try
            {
                while (lines.WaitToReadAsync(exited).AsTask().GetAwaiter().GetResult())
                {
                    while (lines.TryRead(out var line))
                    {
                        stream.Write(Utf8.GetBytes(line + "\n"));
                        stream.Flush();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // The launch has stopped reading, or has gone: what is left to write reaches nothing, and its end says why.
            }
        }

        private static JsonObject Answer(string requestId, JsonObject decision)
            => new()
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = decision },
            };

        private static JsonObject Error(string requestId, string error)
            => new()
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = requestId, ["error"] = error },
            };

        private static async Task Quietly(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ProgramStartException or IOException)
            {
                // Its end was seen to where it mattered.
            }
        }
    }
}
