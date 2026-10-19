using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
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
/// by name, and the models are as the step named them. The resolution of tiers and fallbacks, the continuation of
/// earlier work and the policy a run is held to come with their own slices and join the request then; nothing here
/// names a provider, and nothing here runs anything live.
/// </para>
/// <para>
/// Whatever the end, the record says it: every run's stream begins with the runner's <see cref="RunStarted"/> and ends
/// with its <see cref="RunFinished"/> - a client that returned, one that threw, one that was stopped, and a run no client
/// could be taken for alike - and <c>result.json</c> is written once that last line is, so the two never disagree. A
/// client that threw has its failure raised after both are written. A record that could not be written stops the run,
/// and its result says so.
/// </para>
/// </remarks>
/// <param name="clients">The clients registered, one of which works each step.</param>
/// <param name="fileSystem">The file system the step's records are kept on.</param>
/// <param name="clock">When each event happened.</param>
public sealed class AgentRunner(IEnumerable<IAgentClient> clients, IFileSystem fileSystem, TimeProvider clock) : IAgentRunner
{
    /// <summary>The run's own record, in the step's directory: the result as it ended.</summary>
    public const string ResultFileName = "result.json";

    /// <summary>What a run stopped by its hard stop says it ended with.</summary>
    public const string StoppedError = "The run was stopped before it ended.";

    private readonly IReadOnlyList<IAgentClient> _clients = [.. (clients ?? throw new ArgumentNullException(nameof(clients)))];

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

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
                    result = lost is null ? worked : worked with
                    {
                        Status = AgentRunStatus.Failed,
                        Error = mask.Redact($"The step's stored stream '{stream.FilePath}' could not be written, so the run was stopped: {lost.Message}"),
                    };

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
            result = result with
            {
                Status = AgentRunStatus.Failed,
                Error = mask.Redact($"The step's stored stream '{stream.FilePath}' could not be closed whole, so its last lines may not be on the disk: {unclosed.Message}"),
            };
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

        var messages = new AgentMessages();

        try
        {
            var result = await client.RunAsync(Resolved(step, directory, mask, messages), pump, pump.Stopping).ConfigureAwait(false);
            return (result, null);
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

    /// <summary>The client the step names, or the one there is where it names none; null where there is none.</summary>
    /// <param name="step">The step.</param>
    private IAgentClient? Client(AgentStepRequest step)
        => step.Client is { } named
            ? _clients.FirstOrDefault(client => string.Equals(client.Name, named, StringComparison.Ordinal))
            : _clients.Count == 1 ? _clients[0] : null;

    /// <summary>Writes the run's own record beside its stream, in one step, so that one there is one written whole.</summary>
    /// <param name="directory">The step's own directory.</param>
    /// <param name="result">How the run ended.</param>
    private void Ended(string directory, AgentRunResult result)
        => _fileSystem.WriteAllTextAtomic(Path.Combine(directory, ResultFileName), JsonSerializer.Serialize(result, JsonStateFile.Options) + "\n");

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
            new AgentHandoffSignal(Bound(step)),
            messages);

    /// <summary>The step's own directory, as the one owner of the paths spells it.</summary>
    /// <param name="step">The step.</param>
    private static string StepDirectory(AgentStepRequest step)
    {
        var layout = OrchestratorLayout.Of(new HarnessLayout(step.Workspace.MainCheckout, step.Workspace.MainCheckout), step.Run);

        return layout.AgentStepDirectory(step.Agent, step.StepInstance);
    }

    /// <summary>How long the agent has to answer a handoff: the bound the step's limits name, three minutes unless they do.</summary>
    /// <param name="step">The step.</param>
    private static TimeSpan Bound(AgentStepRequest step)
        => step.Limits.HandoffSeconds is { } seconds and > 0 ? TimeSpan.FromSeconds(seconds) : AgentLimits.DefaultHandoffBound;

    /// <summary>The template with each of its values filled where the template names it, each place once.</summary>
    /// <param name="template">The template.</param>
    /// <param name="values">Its values.</param>
    /// <remarks>
    /// The grammar of a prompt's own placeholders comes with the definition read; this fills a name where it stands,
    /// reading the template once so a value that names another value is never filled in its turn.
    /// </remarks>
    private static string Filled(string template, IReadOnlyDictionary<string, string> values)
        => System.Text.RegularExpressions.Regex.Replace(
            template,
            @"\{(\w+)\}",
            matched => values.TryGetValue(matched.Groups[1].Value, out var value) ? value : matched.Value);

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
