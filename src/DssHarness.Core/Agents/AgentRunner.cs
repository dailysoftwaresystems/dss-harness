using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Agents;

/// <summary>
/// Works an agent's step, from its request to its result: it chooses the client, opens the step's stored stream - the
/// one sink always attached, and the one the run cannot do without - and drives the client through the contract, so
/// what happened is a stream of events and how it ended is a result, whatever the client is.
/// </summary>
/// <remarks>
/// <para>
/// The skeleton of it: the client is found by its name among those registered, the prompt is filled from its template
/// by name, and the models are as the step's order names them - each entry's client, model, effort and keys, walked by
/// the engine's fallback. The policy's ground, the program, the names withheld, the continuation record and the
/// compaction record are the request's and handed to the client as they are. Nothing here names a provider, and
/// nothing here runs anything live.
/// </para>
/// <para>
/// Whatever the end, the record says it: every run's stream begins with the runner's <see cref="RunStarted"/> and ends
/// with its <see cref="RunFinished"/> - a client that returned, one that threw, one that was stopped, and a run no client
/// could be taken for alike - and <c>result.json</c> is written once that last line is, so the two never disagree. A
/// client that threw has its failure raised after both are written. A record that could not be written stops the run,
/// and its result says so.
/// </para>
/// <para>
/// A run that never began is recorded as one, in the step's own directory: its stream's two ends and a
/// <c>result.json</c> saying <see cref="AgentRunStatus.NotStarted"/> and why. A step that was not started is a step of
/// the run all the same - its record says it was not, and for which reason <see cref="AgentNotStartedBecause"/> names -
/// and the result and every exit name where the step's records are, which a step left without any would not have.
/// </para>
/// <para>
/// Every launch of every client passes here, so this is where no agent starts while this tool itself runs elevated: such
/// a step is recorded <see cref="AgentNotStartedBecause.Elevated"/> before any client is taken, in the words of the
/// policy's own refusal (<see cref="AgentPolicy.RefusedWhileElevated"/>), for every rule of that policy assumes a user's
/// powers. So is where a step whose policy resolves the agent's paths from another directory than the one it starts in -
/// or from one that cannot be compared with it, blank or holding a character no path does - is recorded
/// <see cref="AgentNotStartedBecause.ConfigurationWrong"/>: that policy would decide for places it is not asked about.
/// </para>
/// <para>
/// A result is what <c>result.json</c> says and what the engine reads: one a client answers that does not hold together
/// (<see cref="AgentRunResult.Problem"/>) ends the run failed, naming why, keeping what a failed run keeps
/// (<see cref="AgentRunResult.FailedFor"/>) - bare where what it would keep is itself what is wrong - and none is
/// written that does not.
/// </para>
/// </remarks>
/// <param name="clients">The clients registered, one of which works each step.</param>
/// <param name="fileSystem">The file system the step's records are kept on.</param>
/// <param name="platform">Whether this tool itself runs elevated, which no agent starts under, and how it compares names.</param>
/// <param name="clock">When each event happened.</param>
/// <param name="gates">The lanes a run's requests go through, one per provider and key; null where none are held.</param>
public sealed class AgentRunner(IEnumerable<IAgentClient> clients, IFileSystem fileSystem, IHostPlatform platform, TimeProvider clock, ClientGates? gates = null) : IAgentRunner
{
    /// <summary>The run's own record, in the step's directory: the result as it ended.</summary>
    public const string ResultFileName = "result.json";

    /// <summary>What a run stopped by its hard stop says it ended with.</summary>
    public const string StoppedError = "The run was stopped before it ended.";

    /// <summary>Why a run paused while it waited its turn at the gate has no handoff: its client never began.</summary>
    public const string PausedAtTheGate = "the run was paused while it waited its turn at the gate, before its client began";

    /// <summary>How long a failed agent's first retry waits, in seconds, where neither its step nor the workflow names one.</summary>
    public const int WorkflowBackoffSeconds = 30;

    /// <summary>The most a failed agent's retry waits, doubling from its backoff: the cap a step's own backoff answers to.</summary>
    public const int MostBackoffSeconds = Orchestration.Flow.WorkflowStep.MostBackoffSeconds;

    private readonly IReadOnlyList<IAgentClient> _clients = [.. (clients ?? throw new ArgumentNullException(nameof(clients)))];

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    private readonly IHostPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    private readonly ClientGates? _gates = gates;

    /// <summary>Works the step, from its request to its result, emitting what happens beside the stored stream.</summary>
    /// <param name="step">The step, as the engine resolved it.</param>
    /// <param name="events">A listener beside the stored stream, handed each event after the stream has it; null where only the record is wanted.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    /// <returns>How the run ended, also written as <c>result.json</c> beside its stream.</returns>
    /// <exception cref="Exception">
    /// What the client threw, raised once the stream and <c>result.json</c> say how the run ended; else what the listener
    /// threw, or a callback on the stop the runner raised for a lost record, raised once the run has ended. A record that
    /// could not be written or closed is no exception: the result says the run failed, naming it.
    /// </exception>
    public async Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentOutOfRangeException.ThrowIfLessThan(step.Attempt, 1);

        var directory = StepDirectory(step);
        var mask = step.Mask ?? Masking.Nothing;

        AgentRunResult result;
        Exception? thrown;
        Exception? kept;
        var recordLost = false;
        Exception? unclosed = null;

        // The step's directory is every attempt's, and notes beside a result are that result's: an attempt begins with none
        // an earlier one left, before it does anything - so a file that cannot go fails it there, never its own record.
        _fileSystem.DeleteFile(Path.Combine(directory, OrchestratorLayout.StepNotesFileName));
        var stream = StoredStream.Open(_fileSystem, directory, mask);

        try
        {
            var pump = new AgentEventPump(stream, _clock, cancellationToken);

            await using (pump.ConfigureAwait(false))
            {
                if (events is not null)
                {
                    pump.Attach(events);
                }

                await pump.PostOwnAsync(new RunStarted(step.Attempt)).ConfigureAwait(false);

                (var worked, thrown) = await WorkAsync(step, directory, mask, pump, cancellationToken).ConfigureAwait(false);
                result = worked;

                // The end is made once every event before it has been handed over, for only then is it known whether the
                // record holds them: the record is the run's evidence, and without it the run is not one to keep -
                // whatever the client made of its stop, it failed, and its result names why.
                await pump.FinishAsync(lost =>
                {
                    result = lost is null
                        ? worked
                        : worked.FailedFor(mask.Redact($"The step's stored stream '{stream.FilePath}' could not be written, so the run was stopped: {lost.Message}"));

                    return new RunFinished(result.Status, result.Error);
                }).ConfigureAwait(false);

                // A stop the record's loss raised is the runner's own doing and not the client's failure; anything else
                // the client threw is still its own.
                recordLost = pump.RecordFailure is not null;

                if (recordLost && thrown is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    thrown = null;
                }

                kept = pump.ListenerFailure ?? pump.StopFailure;
            }
        }
        finally
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception ex)
            {
                // A record that cannot be closed whole is lost as one that could not be written is: it joins the result
                // below rather than escaping before the result is written.
                unclosed = ex;
            }
        }

        if (unclosed is not null && !recordLost)
        {
            result = result.FailedFor(mask.Redact($"The step's stored stream '{stream.FilePath}' could not be closed whole, so its last lines may not be on the disk: {unclosed.Message}"));
        }

        // The record is whole, closed, and the run has ended: the result is written after the stream that says how it
        // went, so the two cannot disagree. Then the client's own failure is raised, and else what a listener or a
        // callback on the run's stop raised.
        Ended(directory, result);

        if (thrown is not null)
        {
            ExceptionDispatchInfo.Throw(thrown);
        }

        if (kept is not null)
        {
            ExceptionDispatchInfo.Throw(kept);
        }

        return result;
    }

    /// <summary>Works the step, yielding what happens as it happens, the stored stream attached as always.</summary>
    /// <remarks>
    /// A reader that stops early or is cancelled leaves the run to finish on its own, writing its records for a step no
    /// one is watching: the enumerator is a view, never the run's own lifetime. What the run then raises is seen to, so
    /// that a failure no reader is left to take is never raised unobserved, and what it emits no reader holds.
    /// </remarks>
    /// <param name="step">The step, as the engine resolved it.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    /// <returns>Each event, in order, until the run has ended.</returns>
    public async IAsyncEnumerable<AgentEvent> Stream(AgentStepRequest step, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);

        var yielded = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });

        // Begun whatever the token says, so that a run stopped before it began is recorded as one.
        var run = Task.Run(
            async () =>
            {
                try
                {
                    return await RunAsync(step, new ChannelSink(yielded), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    yielded.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        var readToTheEnd = false;

        try
        {
            while (await yielded.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (yielded.Reader.TryRead(out var @event))
                {
                    yield return @event;
                }
            }

            readToTheEnd = true;
        }
        finally
        {
            if (!readToTheEnd)
            {
                yielded.Writer.TryComplete();
                _ = run.ContinueWith(
                    static alone => _ = alone.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        await run.ConfigureAwait(false);
    }

    /// <summary>
    /// Has the client work the step, where one can be taken: how it ended, and what it threw where it threw. A client that
    /// threw has ended too - stopped where the hard stop was what it threw, failed otherwise - for the record to say so.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="directory">The step's own directory.</param>
    /// <param name="mask">What masks the secrets the run's text may carry.</param>
    /// <param name="pump">The run's pump, the sink the client is handed.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    private async Task<(AgentRunResult Result, Exception? Thrown)> WorkAsync(
        AgentStepRequest step,
        string directory,
        ILineMask mask,
        AgentEventPump pump,
        CancellationToken cancellationToken)
    {
        if (_platform.IsPrivilegedProcess)
        {
            // Never begun, whatever the step names: no client is taken while this tool runs past a user's powers.
            var refused = (AgentPolicyAnswer.Denied)AgentPolicy.RefusedWhileElevated();

            return (new AgentRunResult(
                AgentRunStatus.NotStarted,
                AgentNotStartedBecause.Elevated,
                Model: step.Model,
                Error: $"The step's agent was not started: {refused.Reason}."),
                null);
        }

        if (step.Ground is { } ground && Elsewhere(ground.WorkingDirectory, step.Workspace.WorkingDirectory) is { } elsewhere)
        {
            // Never begun: a policy that resolves the agent's paths from one directory while it starts in another - or
            // in one that cannot be told from it - decides for places it is not asked about.
            return (new AgentRunResult(
                AgentRunStatus.NotStarted,
                AgentNotStartedBecause.ConfigurationWrong,
                Model: step.Model,
                Error: elsewhere),
                null);
        }

        if (step.Order is { Count: > 0 } order)
        {
            // The step's whole order is the engine's to walk: a decline, a limit and a refused key move it from entry
            // to entry, across clients as within one, until an ending stands.
            return await OrderedAsync(order, step, directory, mask, pump, cancellationToken).ConfigureAwait(false);
        }

        if (Client(step) is not { } client)
        {
            // Never begun, and the configuration is why: a record of the step exists all the same, saying so.
            return (new AgentRunResult(
                AgentRunStatus.NotStarted,
                AgentNotStartedBecause.ConfigurationWrong,
                Model: step.Model,
                Error: step.Client is { } missing ? $"No agent client named '{missing}' is registered."
                    : _clients.Count == 0 ? "The step names no agent client, and none is registered."
                    : $"The step names no agent client, and {_clients.Count} are registered ({string.Join(", ", _clients.Select(one => $"'{one.Name}'"))}): where there is more than one, a step names the one it is worked by."),
                null);
        }

        var messages = step.Messages ?? new AgentMessages();

        try
        {
            var resolved = Resolved(step, directory, mask, messages);
            WroteUserMd(resolved, NewConversation(client, resolved));
            var result = await InLaneAsync(client, resolved, pump, pump).ConfigureAwait(false);

            return (Holding(client, result, step, mask), null);
        }
        catch (Exception ex)
        {
            var stopped = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

            return (new AgentRunResult(
                stopped ? AgentRunStatus.Cancelled : AgentRunStatus.Failed,
                Model: step.Model,
                Error: stopped ? StoppedError : mask.Redact(ex.Message)),
                ex);
        }
        finally
        {
            messages.Complete();
        }
    }

    /// <summary>
    /// Works the step through its whole order, the engine's fallback: the entry that serves is taken first -
    /// each entry the machine cannot use said and passed over - and a declined request, an account at its limit and a
    /// key the provider refuses move the step to its next entry by the cause's rule, a move within the same provider and
    /// client already taken inside the client's own run. Where no entry is left the ending stands; a move to another
    /// client begins a new conversation with the step's prompt, and a limit the run met is remembered until its end. An
    /// entry the step moved to that could not be started is passed over, said so, as one the machine cannot use is: the
    /// step has done work by then, and the ending that moved it stands where no entry is left.
    /// </summary>
    /// <param name="order">The step's whole order, the serving entry first.</param>
    /// <param name="step">The step.</param>
    /// <param name="directory">The step's own directory.</param>
    /// <param name="mask">What masks the secrets the run's text may carry.</param>
    /// <param name="pump">The run's pump, the sink the client is handed.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    private async Task<(AgentRunResult Result, Exception? Thrown)> OrderedAsync(
        IReadOnlyList<AgentRunEntry> order,
        AgentStepRequest step,
        string directory,
        ILineMask mask,
        AgentEventPump pump,
        CancellationToken cancellationToken)
    {
        var totals = new WalkTotals();
        var refused = new HashSet<string>(StringComparer.Ordinal);
        var limits = new List<(AgentAccountLimit Limit, string Client)>();
        var retried = new Dictionary<(string Model, string Provider, string Client), int>();
        var watching = new Watching(pump);
        var messages = step.Messages ?? new AgentMessages();

        // The move that brought the walk to the entry it stands on - from which entry, and why - said again to the entry
        // after it where the one it moved to could not be started; null before the walk has moved.
        (AgentRunEntry From, string Why)? moved = null;

        try
        {
            // The slot holds none of the first model's keys: the step's agent run never begins, refused as one that could
            // not start, for no entry of the step's own model could serve it. A later model's missing key is only said and
            // passed over, and never refuses a run.
            var first = order.Where(entry => string.Equals(entry.Model, order[0].Model, StringComparison.Ordinal)).ToList();

            if (first.All(entry => !entry.Usable))
            {
                var keys = first.All(entry => entry.Key is not null);

                return (AgentEndings.NotStarted(
                    order[0].Client,
                    keys ? AgentNotStartedBecause.KeyMissing : AgentNotStartedBecause.ConfigurationWrong,
                    keys
                        ? $"the slot holds none of the keys of the step's first model ({string.Join(", ", first.Select(entry => entry.Key))})"
                        : first[0].WhyNot ?? "no entry of the step's first model can be used on this machine",
                    step.Model,
                    mask), null);
            }

            var at = 0;

            while (true)
            {
                while (at < order.Count && !order[at].Usable)
                {
                    await watching.EmitAsync(new FallbackPassedOver(order[at].Model, order[at].WhyNot ?? "it cannot be used on this machine")).ConfigureAwait(false);
                    at++;
                }

                if (at >= order.Count)
                {
                    break;
                }

                var entry = order[at];
                AgentRunResult result;
                var client = ByName(entry.Client);
                var broken = false;

                if (client is null)
                {
                    result = AgentEndings.NotStarted(entry.Client, AgentNotStartedBecause.ConfigurationWrong, $"No agent client named '{entry.Client}' is registered.", entry.Model, mask);
                }
                else
                {
                    watching.Began();

                    try
                    {
                        var now = _clock.GetUtcNow();
                        var resolved = Resolved(
                            step,
                            directory,
                            mask,
                            messages,
                            entry,
                            CredentialFor(order, at, refused, limits, now),
                            LaterFor(order, at, refused, limits, now),
                            step.Conversation,
                            client.Capabilities.GoesOnWith == AgentConversationHandling.Handed);

                        WroteUserMd(resolved, NewConversation(client, resolved));
                        var answered = await InLaneAsync(client, resolved, watching, pump).ConfigureAwait(false);

                        // An answer that does not hold together is no ending the engine could read, and no ending to
                        // move on from either.
                        broken = answered.Problem() is not null;
                        result = Holding(client, answered, step, mask);
                    }
                    catch (Exception ex)
                    {
                        var stopped = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
                        totals.Add(new AgentRunResult(
                            stopped ? AgentRunStatus.Cancelled : AgentRunStatus.Failed,
                            Model: step.Model,
                            Error: stopped ? StoppedError : mask.Redact(ex.Message)));

                        return (totals.Finished(step), ex);
                    }

                    // What the run spent is recorded for the next step's choice of a key, outside what can end the step:
                    // a balancing record that cannot be written is said, and the step goes on as it ended.
                    await SpentAsync(step, entry, result, watching).ConfigureAwait(false);
                }

                // An entry the step moved to that could not be started is passed over as one the machine cannot use: the
                // step goes on from the move that brought it there, to the entry after it, and where none is left the
                // ending that moved it stands. The step's own first entry that cannot start ends it so.
                if (result.Status == AgentRunStatus.NotStarted && moved is { } brought)
                {
                    await watching.EmitAsync(new FallbackPassedOver(entry.Model, result.Error ?? "it could not be started")).ConfigureAwait(false);

                    if (AgentFallback.Next(order, at, decline: false, refused, limits, _clock.GetUtcNow()) is not { } onward)
                    {
                        break;
                    }

                    at = await MovedAsync(watching, onward, order, brought.From, brought.Why, totals.Turns).ConfigureAwait(false);
                    continue;
                }

                totals.Add(result);

                if (client is null || broken || result.Status == AgentRunStatus.NotStarted)
                {
                    return (totals.Finished(step), null);
                }

                foreach (var key in result.KeysRefused)
                {
                    refused.Add(key);
                }

                // What moves the step, and nothing else: a decline - never one about what the prompt asks - an account
                // at its limit, a failed run whose provider and client have no key left unrefused, and a failed agent
                // with retries left on its model. Everything else is the step's own ending, however it ended.
                var decline = result.Status == AgentRunStatus.Refused && !string.Equals(result.DeclinedFor, AgentFallback.ReasoningExtraction, StringComparison.Ordinal);
                var atLimit = result.Status == AgentRunStatus.RateLimited;
                var keysGone = result.Status == AgentRunStatus.Failed && result.KeysRefused.Count > 0 && !order.Any(one =>
                    one.Usable
                    && string.Equals(one.Provider, entry.Provider, StringComparison.Ordinal)
                    && string.Equals(one.Client, entry.Client, StringComparison.Ordinal)
                    && (one.Key is not { Length: > 0 } key || !refused.Contains(key)));
                var agentFailed = result.Status is AgentRunStatus.Failed or AgentRunStatus.InvalidOutput or AgentRunStatus.WrongModel or AgentRunStatus.Stalled or AgentRunStatus.TimedOut
                    && !keysGone
                    && !result.ServiceFailed;

                if (!decline && !atLimit && !keysGone && !agentFailed)
                {
                    break;
                }

                if (atLimit)
                {
                    limits.Add((result.Limit!, entry.Client));
                }

                // Where the step stands now: the entry its client said it moved to last inside its own run, else the one
                // the walk started it on - every move and retry goes on from there, never asking an entry again.
                var from = watching.LastServed(entry);
                var where = Math.Max(PlaceOf(order, from, at), at);

                // A failed agent is tried again on its model before the step moves past it: `retries` times - the
                // step's own, the workflow's where it says none - waiting `backoffSeconds` before the first and
                // twice as long before each later one, at most fifteen minutes - on the first of the model's entries
                // from where it stands that the walk may still take, for a key refused or limited is never asked
                // again. The provider's service failing past the client's own tries is never retried, for every
                // model of the step is reached through the same service.
                if (agentFailed && RetryPlace(order, where, refused, limits, _clock.GetUtcNow()) is { } again)
                {
                    var model = (order[where].Model, order[where].Provider, order[where].Client);
                    var spent = retried.GetValueOrDefault(model);

                    if (spent < (step.Retries ?? 0))
                    {
                        retried[model] = spent + 1;

                        var wait = TimeSpan.FromSeconds(Math.Min(
                            (step.BackoffSeconds ?? WorkflowBackoffSeconds) * Math.Pow(2, spent),
                            (double)MostBackoffSeconds));

                        await watching.EmitAsync(new WaitingToRetry(
                            $"the agent failed: {result.Error ?? "it ended failed"}",
                            spent + 1,
                            step.Retries ?? 0,
                            wait,
                            _clock.GetUtcNow() + wait)).ConfigureAwait(false);

                        // The wait ends with the run's own stop: a record lost while it waits stops the run as it would
                        // stop the client's.
                        await Task.Delay(wait, _clock, pump.Stopping).ConfigureAwait(false);

                        at = again;
                        continue;
                    }
                }

                // A failure past its retries moves the step as a decline does: to the next model, past the model's
                // other keys, for another account of the same model is asked the same and answers the same.
                if (AgentFallback.Next(order, where, decline || agentFailed, refused, limits, _clock.GetUtcNow()) is not { } next)
                {
                    break;
                }

                var why = decline ? $"declined ({result.DeclinedFor ?? "the request"})"
                    : atLimit ? "limit"
                    : agentFailed ? $"failed past its {(step.Retries ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)} retries"
                    : "key refused";

                moved = (from, why);
                at = await MovedAsync(watching, next, order, from, why, totals.Turns).ConfigureAwait(false);
            }

            return (totals.Finished(step), null);
        }
        catch (Exception ex)
        {
            // Only the retry's wait throws here, stopped: what the client threw was taken above.
            var stopped = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            totals.Add(new AgentRunResult(
                stopped ? AgentRunStatus.Cancelled : AgentRunStatus.Failed,
                Model: step.Model,
                Error: stopped ? StoppedError : mask.Redact(ex.Message)));

            return (totals.Finished(step), ex);
        }
        finally
        {
            messages.Complete();
        }
    }

    /// <summary>
    /// Says the walk's move to <paramref name="next"/> - the entries passed over to reach it, then the move itself, a new
    /// conversation with the step's prompt - and answers where the walk stands now.
    /// </summary>
    /// <param name="watching">Where the walk's events go.</param>
    /// <param name="next">The move the fallback's rules made.</param>
    /// <param name="order">The step's whole order.</param>
    /// <param name="from">The entry the step moves from: the one that served it last.</param>
    /// <param name="why">Why it moves, as the record says it.</param>
    /// <param name="turn">The turns the step has taken.</param>
    private static async Task<int> MovedAsync(Watching watching, (int At, IReadOnlyList<(string Model, string Why)> PassedOver) next, IReadOnlyList<AgentRunEntry> order, AgentRunEntry from, string why, int turn)
    {
        foreach (var (model, passed) in next.PassedOver)
        {
            await watching.EmitAsync(new FallbackPassedOver(model, passed)).ConfigureAwait(false);
        }

        var to = order[next.At];

        await watching.EmitAsync(new FallbackStarted(
            from.Model,
            from.Provider,
            from.Client,
            to.Model,
            to.Provider,
            to.Client,
            why,
            AgentFallback.NewConversation,
            turn,
            ThrewAway: false,
            CreditAsked: false,
            from.Key,
            to.Key,
            from.Effort,
            to.Effort)).ConfigureAwait(false);

        return next.At;
    }

    /// <summary>
    /// The place a failed agent is tried again at: the first entry from <paramref name="where"/> on of the same model,
    /// provider and client that the walk may still take (<see cref="AgentFallback.Taken"/>); null where none is left, and
    /// the step moves past the model.
    /// </summary>
    private static int? RetryPlace(IReadOnlyList<AgentRunEntry> order, int where, IReadOnlyCollection<string> refused, IReadOnlyList<(AgentAccountLimit Limit, string Client)> limits, DateTimeOffset now)
    {
        for (var at = where; at < order.Count && SameModel(order[at], order[where]); at++)
        {
            if (AgentFallback.Taken(order[at], refused, limits, now) is null)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>Whether two entries are of one model on one provider and client: the entries a client's own walk moves between by key alone.</summary>
    private static bool SameModel(AgentRunEntry one, AgentRunEntry other)
        => string.Equals(one.Model, other.Model, StringComparison.Ordinal)
            && string.Equals(one.Provider, other.Provider, StringComparison.Ordinal)
            && string.Equals(one.Client, other.Client, StringComparison.Ordinal);

    /// <summary>
    /// The credential the run of <c>order[at]</c> signs in with: the entry's own key first, then the key of each entry
    /// after it of the same model, provider and client that the walk may still take - never one the run has seen refused
    /// or a remembered limit covers - each value read from its entry's own credential. Those are the keys a client moves
    /// through by itself, on a refused key and at a limit, before the model changes; the step's earlier entries are behind
    /// it. Null for an entry that signs in with the machine's login.
    /// </summary>
    private static AgentCredential? CredentialFor(IReadOnlyList<AgentRunEntry> order, int at, IReadOnlyCollection<string> refused, IReadOnlyList<(AgentAccountLimit Limit, string Client)> limits, DateTimeOffset now)
    {
        var entry = order[at];

        if (entry.Credential is not { } credential || KeyOf(entry) is not { } own)
        {
            return entry.Credential;
        }

        var keys = new List<AgentKey> { own };

        for (var next = at + 1; next < order.Count && SameModel(order[next], entry); next++)
        {
            if (AgentFallback.Taken(order[next], refused, limits, now) is null
                && KeyOf(order[next]) is { } key
                && !keys.Exists(held => string.Equals(held.Name, key.Name, StringComparison.Ordinal)))
            {
                keys.Add(key);
            }
        }

        return credential with { Keys = keys };
    }

    /// <summary>The key an entry signs in with, its value read from its own credential; null for a login, or where its credential does not hold it.</summary>
    internal static AgentKey? KeyOf(AgentRunEntry entry)
        => entry.Key is { Length: > 0 } name
            ? entry.Credential?.Keys.FirstOrDefault(key => string.Equals(key.Name, name, StringComparison.Ordinal))
            : null;

    /// <summary>
    /// The entries after <c>order[at]</c> as its client is handed them to go on with: each as the order holds it, and one
    /// the walk may no longer take - its key refused, a remembered limit covering it - marked so, with the reason, so the
    /// client passes it over as the walk would.
    /// </summary>
    private static IReadOnlyList<AgentRunEntry> LaterFor(IReadOnlyList<AgentRunEntry> order, int at, IReadOnlyCollection<string> refused, IReadOnlyList<(AgentAccountLimit Limit, string Client)> limits, DateTimeOffset now)
        => [.. order.Skip(at + 1).Select(entry => entry.Usable && AgentFallback.Taken(entry, refused, limits, now) is { } why ? entry with { Usable = false, WhyNot = why } : entry)];

    /// <summary>
    /// Works <paramref name="run"/> on <paramref name="client"/> through the gate: a client that paces its own requests is
    /// handed the gate's lanes and enters one for each request; any other is held in the lane of its provider and first
    /// key for the whole run - a Claude Code launch, whose requests the runner cannot see one by one - its wait said as it
    /// waits and ended by the run's pause, which leaves a run that never began with no handoff to give; the answer's
    /// tokens are charged once they are had, and an account limit the run met holds the lane until it lifts.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="run">The run, resolved.</param>
    /// <param name="events">Where the run's events go.</param>
    /// <param name="pump">The run's pump, whose stop the run is held to.</param>
    private async Task<AgentRunResult> InLaneAsync(IAgentClient client, AgentRun run, IAgentEventSink events, AgentEventPump pump)
    {
        if (_gates is null)
        {
            return await client.RunAsync(run, events, pump.Stopping).ConfigureAwait(false);
        }

        if (client.Capabilities.PacesRequests)
        {
            return await client.RunAsync(run with { Gates = _gates }, events, pump.Stopping).ConfigureAwait(false);
        }

        if (run.Provider is not { Length: > 0 } provider)
        {
            return await client.RunAsync(run, events, pump.Stopping).ConfigureAwait(false);
        }

        var lane = _gates.LaneOf(provider, client.Name, run.Entries?.Client, run.Credential?.Auth == AgentAuth.Login ? null : run.Credential?.Keys.FirstOrDefault());
        ClientGate.ClientSlot slot;

        using (var paused = CancellationTokenSource.CreateLinkedTokenSource(pump.Stopping))
        {
            var entering = lane.EnterAsync(
                0,
                (wait, why) => events.EmitAsync(new WaitingToRetry($"the run waits for {why}", 1, 1, wait, _clock.GetUtcNow() + wait)),
                paused.Token);

            // The run's pause ends the wait: a run that never began has nothing to hand off, and says why.
            if (await Task.WhenAny(entering, run.Handoff.WhenAsked).ConfigureAwait(false) != entering)
            {
                await paused.CancelAsync().ConfigureAwait(false);

                try
                {
                    (await entering.ConfigureAwait(false)).Dispose();
                }
                catch (OperationCanceledException) when (!pump.Stopping.IsCancellationRequested)
                {
                    // The wait the pause ended, as it was asked to.
                }

                return AgentEndings.HandedOff(null, PausedAtTheGate, run.Mask);
            }

            slot = await entering.ConfigureAwait(false);
        }

        AgentRunResult result;

        using (slot)
        {
            result = await client.RunAsync(run, events, pump.Stopping).ConfigureAwait(false);
        }

        slot.Answered(result.Usage?.Total.OutputTokens ?? 0);

        // An account at its limit holds the lane's every waiter until the provider says the limit lifts: the cool-down
        // is set from what it said, never shortened by a sooner one.
        if (result.Status == AgentRunStatus.RateLimited && result.Limit?.Lifts is { } lifts && lifts > _clock.GetUtcNow())
        {
            lane.RateLimited(lifts - _clock.GetUtcNow());
        }

        return result;
    }

    /// <summary>
    /// The result a client answered, or - where it does not hold together, no ending the engine could read - the run
    /// failed, saying why, keeping what a failed run keeps of what the client reported: what it did and used, the
    /// children it could not end, the session files it removed, the keys refused. Where what it keeps is itself what is
    /// wrong, it fails bare.
    /// </summary>
    private static AgentRunResult Holding(IAgentClient client, AgentRunResult result, AgentStepRequest step, ILineMask mask)
    {
        if (result.Problem() is not { } wrong)
        {
            return result;
        }

        var error = mask.Redact($"Client '{client.Name}' answered a result that does not hold together: {wrong}.");
        var failed = result.FailedFor(error);

        return failed.Problem() is null ? failed : new AgentRunResult(AgentRunStatus.Failed, Model: step.Model, Error: error);
    }

    /// <summary>The place of <paramref name="entry"/> in <paramref name="order"/>, matched by what it is - model, provider, client, key - and <paramref name="or"/> where none matches.</summary>
    private static int PlaceOf(IReadOnlyList<AgentRunEntry> order, AgentRunEntry entry, int or)
    {
        for (var at = 0; at < order.Count; at++)
        {
            if (SameModel(order[at], entry)
                && string.Equals(order[at].Key ?? string.Empty, entry.Key ?? string.Empty, StringComparison.Ordinal))
            {
                return at;
            }
        }

        return or;
    }

    /// <summary>
    /// Records what the entry's run spent, by the key's name and never its value, for the next step's choice of one:
    /// what the client reported the run cost where it reports one (a Claude Code launch's own number), else what the
    /// engine counted at the configuration's prices; nothing where neither is known, for a cost no price was there
    /// for is unknown and never zero. The record is for balancing alone: one that cannot be written - its section held
    /// past its wait, the disk full - is said as a notice, and never ends the step.
    /// </summary>
    /// <param name="step">The step, whose sink the run records through; null where nothing is counted.</param>
    /// <param name="entry">The entry that served the run, its key the one that spent.</param>
    /// <param name="result">How the run ended, with what it used and what that cost.</param>
    /// <param name="events">Where a record that could not be written is said.</param>
    private static async Task SpentAsync(AgentStepRequest step, AgentRunEntry entry, AgentRunResult result, IAgentEventSink events)
    {
        if (step.Spend is not { } sink || entry.Key is not { Length: > 0 } || result.Usage?.Cost?.Usd is not { } cost)
        {
            return;
        }

        try
        {
            sink.Record(entry.Provider, entry.Key, entry.Model, cost);
        }
        catch (Exception ex) when (ex is Results.HarnessException or IOException or UnauthorizedAccessException)
        {
            await events.EmitAsync(new Notice($"What {entry.Key} spent on '{entry.Model}' could not be recorded for the balancing of keys: {ex.Message}")).ConfigureAwait(false);
        }
    }

    /// <summary>The client named, or null where none is registered under the name.</summary>
    /// <param name="client">The client's key under <c>agentClients</c>.</param>
    private IAgentClient? ByName(string client)
        => _clients.FirstOrDefault(one => string.Equals(one.Name, client, StringComparison.Ordinal));

    /// <summary>
    /// The entry the step last served, watched from the events of the client that served it: a client moves inside its
    /// own provider and client by itself and says so (<see cref="FallbackStarted"/>), and the walk goes on from where
    /// the client said it got to, never asking an entry again.
    /// </summary>
    private sealed class Watching(AgentEventPump pump) : IAgentEventSink
    {
        private readonly AgentEventPump _pump = pump ?? throw new ArgumentNullException(nameof(pump));

        private AgentRunEntry? _last;

        /// <summary>A client's run begins on the entry the walk started it on: what the last run said is the last run's.</summary>
        public void Began() => _last = null;

        /// <summary>The entry the step last served: the last the client said it moved to, else <paramref name="started"/>.</summary>
        /// <param name="started">The entry the walk started this client on.</param>
        public AgentRunEntry LastServed(AgentRunEntry started) => _last ?? started;

        public async ValueTask EmitAsync(AgentEvent @event)
        {
            if (@event is FallbackStarted moved)
            {
                _last = new AgentRunEntry(moved.ToModel, moved.ToProvider, moved.ToClient, moved.ToKey, moved.ToEffort, Usable: true);
            }

            await _pump.EmitAsync(@event).ConfigureAwait(false);
        }
    }

    /// <summary>What every entry of the walk added to the step's one run, and the one result the step ends with.</summary>
    private sealed class WalkTotals
    {
        private readonly List<AgentModelUsage> _perModel = [];
        private readonly List<string> _keysRefused = [];
        private readonly List<string> _files = [];
        private readonly List<AgentServedModel> _served = [];
        private AgentUsage _total = AgentUsage.None;
        private decimal? _counted;
        private decimal? _reported;
        private bool _anyUsage;
        private bool _countedLost;
        private bool _reportedLost;
        private bool _anyReported;
        private string? _missingPrice;
        private AgentRunResult? _result;
        private int _turnsSoFar;
        private int _calls;
        private int _denials;
        private int _filesWritten;
        private int _webRequests;
        private int _compactions;
        private int _retries;
        private TimeSpan _ran;

        /// <summary>How many turns the step took over every entry that served it.</summary>
        public int Turns => _turnsSoFar;

        /// <summary>Adds what <paramref name="result"/> did and used on the entry that served it.</summary>
        public void Add(AgentRunResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            _result = result;

            if (result.Usage is { } usage)
            {
                _anyUsage = true;
                _total = _total.Plus(usage.Total);
                _perModel.AddRange(usage.PerModel);
                _missingPrice ??= usage.Cost?.MissingPrice;

                // Each entry's cost joins the walk's: counted where the engine counted it, reported where the client
                // said it - the engine's count standing in for an entry whose client says nothing, so that what the
                // step cost is every entry's own. An entry whose cost is known by neither makes the walk's unknown,
                // never zero: that sum is lost for good, and no later entry gives it back.
                var cost = usage.Cost;
                _countedLost |= cost?.Counted is null;
                _reportedLost |= cost?.Usd is null;
                _counted = _countedLost ? null : (_counted ?? 0) + cost!.Counted;
                _anyReported |= cost?.Reported is not null;
                _reported = _reportedLost ? null : (_reported ?? 0) + cost!.Usd;
            }

            _keysRefused.AddRange(result.KeysRefused.Where(key => !_keysRefused.Contains(key)));
            _files.AddRange(result.Files.Where(file => !_files.Contains(file)));
            _served.AddRange(result.Served.Select(served => served with { FromTurn = served.FromTurn + _turnsSoFar }));
            _turnsSoFar += result.Turns;
            _calls += result.ToolCalls;
            _denials += result.Denials;
            _filesWritten += result.FilesWritten;
            _webRequests += result.WebRequests;
            _compactions += result.Compactions;
            _retries += result.Retries;
            _ran += result.Duration;
        }

        /// <summary>
        /// The step's one result: the last ending's own shape - how it ended, what it said, what it asked, what it left -
        /// with what every entry did and used, the turns it took, the calls it ran and what the providers refused, the
        /// keys among them, and the models that served it, each from the turn of the step it began at. What it cost is
        /// every entry's own cost summed - <c>reported</c> where any client said one, the engine's count standing in for
        /// an entry whose client said none, and <c>counted</c> the engine's alone - and unknown, never zero, where an
        /// entry's cost is; a walk where no entry used anything says no usage at all.
        /// </summary>
        /// <param name="step">The step, whose model the result names as asked for.</param>
        public AgentRunResult Finished(AgentStepRequest step)
            => _result is null
                ? new AgentRunResult(AgentRunStatus.Failed, Model: step.Model, Error: "the step's order holds no entry it could run on")
                : _result with
                {
                    Model = step.Model,
                    Turns = _turnsSoFar,
                    ToolCalls = _calls,
                    Denials = _denials,
                    FilesWritten = _filesWritten,
                    WebRequests = _webRequests,
                    Compactions = _compactions,
                    Retries = _retries,
                    Duration = _ran,
                    Usage = _anyUsage ? new AgentUsageReport(_total, [.. _perModel], new AgentCost(_anyReported ? _reported : null, _counted, _missingPrice)) : null,
                    Served = [.. _served],
                    Files = [.. _files],
                    KeysRefused = [.. _keysRefused],
                };
    }

    /// <summary>
    /// Why a policy resolving the agent's paths from <paramref name="resolvedFrom"/> is not the agent's, which starts in
    /// <paramref name="startsIn"/>: the two are other directories, or one is no directory the two could be compared by
    /// (blank, or holding a character no path does); null where they are the same.
    /// </summary>
    /// <param name="resolvedFrom">The directory the step's policy resolves the agent's paths from.</param>
    /// <param name="startsIn">The directory the agent starts in.</param>
    private string? Elsewhere(string resolvedFrom, string startsIn)
    {
        try
        {
            return PathContainment.AreSame(resolvedFrom, startsIn, _platform.PathComparison)
                ? null
                : $"The step's policy resolves the agent's paths from '{resolvedFrom}', but the agent starts in '{startsIn}'.";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return $"The step's policy resolves the agent's paths from '{resolvedFrom}', and the agent starts in '{startsIn}': the two cannot be compared ({ex.Message}).";
        }
    }

    /// <summary>Whether the conversation <paramref name="run"/> begins on <paramref name="client"/> is a new one: every launch on a client that renders its conversations, and any run handed none.</summary>
    private static bool NewConversation(IAgentClient client, AgentRun run)
        => client.Capabilities.GoesOnWith == AgentConversationHandling.Rendered || run.Conversation is null;

    /// <summary>The step's own directory, as the one owner of the paths spells it.</summary>
    /// <param name="run">The run, whose step's records are kept there.</param>
    private static string StepDirectoryOf(AgentRun run)
        => run.StepDirectory;

    /// <summary>The client the step names, or the one there is where it names none; null where there is none.</summary>
    /// <param name="step">The step.</param>
    private IAgentClient? Client(AgentStepRequest step)
        => step.Client is { } named
            ? _clients.FirstOrDefault(client => string.Equals(client.Name, named, StringComparison.Ordinal))
            : _clients.Count == 1 ? _clients[0] : null;

    /// <summary>Writes the run's own record beside its stream, in one step, so that one there is one written whole - and, where its answer left notes, the notes before it (<see cref="OrchestratorLayout.StepNotesFileName"/>), so a result that holds notes is never read without them beside it - and the first user message as sent, masked (<see cref="OrchestratorLayout.StepUserFileName"/>), holding the parts the attempt was given.</summary>
    /// <param name="directory">The step's own directory.</param>
    /// <param name="result">How the run ended.</param>
    /// <exception cref="InvalidOperationException">The result does not hold together, which none the runner makes does.</exception>
    private void Ended(string directory, AgentRunResult result)
    {
        if (result.Problem() is { } problem)
        {
            throw new InvalidOperationException($"Refusing to write a result that does not read back: {problem}.");
        }

        if (result.Notes is { } notes)
        {
            _fileSystem.WriteAllTextAtomic(Path.Combine(directory, OrchestratorLayout.StepNotesFileName), notes);
        }

        JsonStateFile.Write(_fileSystem, Path.Combine(directory, ResultFileName), result);
    }

    /// <summary>Writes the first user message as it is sent, masked, before the client is taken: the parts the attempt was given, held by the one owner of their order, or the compaction record whole where the run begins a compacted conversation.</summary>
    /// <param name="run">The run the client is about to work.</param>
    /// <param name="newConversation">Whether the conversation the run begins is a new one.</param>
    private void WroteUserMd(AgentRun run, bool newConversation)
        => _fileSystem.WriteAllTextAtomic(
            Path.Combine(StepDirectoryOf(run), OrchestratorLayout.StepUserFileName),
            run.Mask.Redact(run.Compaction is { Length: > 0 } compacted ? compacted : AgentPromptParts.FirstMessage(run, newConversation)));

    /// <summary>The step's run, as the client takes it: the prompt filled, the places as they are, the directory its own.</summary>
    /// <param name="step">The step.</param>
    /// <param name="directory">The step's own directory.</param>
    /// <param name="mask">What masks the secrets its lines may carry.</param>
    /// <param name="messages">Where the engine tells the agent what it has to.</param>
    private static AgentRun Resolved(AgentStepRequest step, string directory, ILineMask mask, AgentMessages messages)
        => new(
            step.SystemPrompt,
            Filled(step.PromptTemplate, step.PromptValues),
            step.Workspace,
            step.Output,
            step.Conversation,
            step.Limits,
            step.Provider,
            step.Model,
            step.Effort,
            [],
            [],
            [],
            mask,
            directory,
            step.Handoff ?? new AgentHandoffSignal(Bound(step)),
            messages,
            step.RunContext,
            step.PlanUpdates,
            step.Continuation,
            step.Compaction)
        {
            Ground = step.Ground,
            Credential = step.Credential,
            Program = step.Program,
            ProgramVersion = step.ProgramVersion,
            Withheld = step.Withheld,
            Commands = step.Commands,
            WebSearch = step.WebSearch,
            SessionsDirectory = step.SessionsDirectory ?? Layout(step).AgentSessionsDirectory(step.Agent),
            Entries = step.Entries,
            Chat = step.Chat,
            Interrupt = step.Interrupt,
        };

    /// <summary>
    /// The run of one entry of the step's order, as its client takes it: the entry's model, provider, effort and
    /// entries, the credential it signs in with - its own key first, then its model's next keys - the entries after it
    /// for the client's own walk, and the stored conversation the step goes on with, handed only where this entry is the
    /// conversation's own client and provider and the client stores conversations - a move to another entry is never a
    /// conversation gone on with.
    /// </summary>
    /// <param name="step">The step.</param>
    /// <param name="directory">The step's own directory.</param>
    /// <param name="mask">What masks the secrets its lines may carry.</param>
    /// <param name="messages">Where the engine tells the agent what it has to.</param>
    /// <param name="entry">The entry that serves.</param>
    /// <param name="credential">The credential the run signs in with (<see cref="CredentialFor"/>); null for the machine's login.</param>
    /// <param name="later">The entries after it, of every client, as each may go on with.</param>
    /// <param name="conversation">The stored conversation the step's answer goes on with, where its attempt has one.</param>
    /// <param name="storesConversations">Whether the entry's client is one the engine hands a stored conversation to.</param>
    private static AgentRun Resolved(AgentStepRequest step, string directory, ILineMask mask, AgentMessages messages, AgentRunEntry entry, AgentCredential? credential, IReadOnlyList<AgentRunEntry> later, AgentSessionRef? conversation, bool storesConversations)
        => new(
            step.SystemPrompt,
            Filled(step.PromptTemplate, step.PromptValues),
            step.Workspace,
            step.Output,
            Conversation: conversation is { } handed
                && storesConversations
                && string.Equals(entry.Client, handed.Client, StringComparison.Ordinal)
                && string.Equals(entry.Provider ?? string.Empty, handed.Provider ?? string.Empty, StringComparison.Ordinal)
                ? handed
                : null,
            step.Limits,
            entry.Provider,
            entry.Model,
            entry.Effort,
            later,
            [],
            [],
            mask,
            directory,
            step.Handoff ?? new AgentHandoffSignal(Bound(step)),
            messages,
            step.RunContext,
            step.PlanUpdates,
            step.Continuation,
            step.Compaction)
        {
            Ground = step.Ground,
            Credential = credential,
            Program = step.Program,
            ProgramVersion = step.ProgramVersion,
            Withheld = step.Withheld,
            Commands = step.Commands,
            WebSearch = step.WebSearch,
            SessionsDirectory = step.SessionsDirectory ?? Layout(step).AgentSessionsDirectory(step.Agent),
            Entries = entry.Entries,
            Chat = step.Chat,
            Interrupt = step.Interrupt,
        };

    /// <summary>The step's own directory, as the one owner of the paths spells it - or the one its request names, for a run the engine keeps elsewhere than an agent's steps (the run's reporter, under the run's <c>asks/</c>).</summary>
    /// <param name="step">The step.</param>
    private static string StepDirectory(AgentStepRequest step) => step.RecordDirectory ?? Layout(step).AgentStepDirectory(step.Agent, step.StepInstance);

    /// <summary>The paths of the step's run, as the one owner of them spells them.</summary>
    /// <param name="step">The step.</param>
    private static OrchestratorLayout Layout(AgentStepRequest step)
        => OrchestratorLayout.Of(new HarnessLayout(step.Workspace.MainCheckout, step.Workspace.MainCheckout), step.Run);

    /// <summary>How long the agent has to answer a handoff: the bound the step's limits name, three minutes unless they do.</summary>
    /// <param name="step">The step.</param>
    private static TimeSpan Bound(AgentStepRequest step)
        => step.Limits.HandoffSeconds is { } seconds and > 0 ? TimeSpan.FromSeconds(seconds) : AgentLimits.DefaultHandoffBound;

    /// <summary>The template with each of its values filled where the template names it, each place once.</summary>
    /// <param name="template">The template.</param>
    /// <param name="values">Its values.</param>
    /// <remarks>
    /// Read by the one template grammar (<see cref="Orchestration.Flow.FlowTemplate.Pieces(string)"/>): a brace doubled stands for
    /// one, another expander's <c>${...}</c> is left as written, and a placeholder this step has no value for stands as
    /// written, as does the rest of a template from a placeholder never closed. The template is read once, so a value
    /// that names another value is never filled in its turn.
    /// </remarks>
    private static string Filled(string template, IReadOnlyDictionary<string, string> values)
    {
        var filled = new StringBuilder(template.Length);

        foreach (var piece in Orchestration.Flow.FlowTemplate.Pieces(template))
        {
            filled.Append(piece.Kind != Orchestration.Flow.FlowTemplate.PieceKind.Placeholder
                ? piece.Text
                : values.TryGetValue(piece.Name, out var value) ? value : $"{{{piece.Text}}}");
        }

        return filled.ToString();
    }

    /// <summary>One sink that takes each event into a channel, as the front end reads it; nothing once no one reads.</summary>
    /// <param name="yielded">The channel the events are yielded through.</param>
    private sealed class ChannelSink(Channel<AgentEvent> yielded) : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent @event)
        {
            yielded.Writer.TryWrite(@event);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A mask that masks nothing, where a step carries no secret.</summary>
file sealed class Masking : ILineMask
{
    /// <summary>The one that masks nothing.</summary>
    public static readonly Masking Nothing = new();

    public string Redact(string? text) => text ?? string.Empty;

    public int Cut(string text) => text.Length;
}
