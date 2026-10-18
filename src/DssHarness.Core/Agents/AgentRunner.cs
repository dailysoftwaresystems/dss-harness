using System.Runtime.CompilerServices;
using System.Text.Json;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Agents;

/// <summary>
/// Works an agent's step, from its request to its result: it chooses the client, opens the step's stored stream - the
/// one sink always attached - and drives the client through the contract, so what happened is a stream of events and
/// how it ended is a result, whatever the client is.
/// </summary>
/// <remarks>
/// The skeleton of it: the client is found by its name among those registered, the prompt is filled from its template
/// by name, and the models are as the step named them. The resolution of tiers and fallbacks, the continuation of
/// earlier work and the policy a run is held to come with their own slices and join the request then; nothing here
/// names a provider, and nothing here runs anything live.
/// </remarks>
public sealed class AgentRunner(IEnumerable<IAgentClient> clients) : IAgentRunner
{
    /// <summary>The run's own record, in the step's directory: the result as it ended.</summary>
    public const string ResultFileName = "result.json";

    private readonly IReadOnlyList<IAgentClient> _clients = [.. (clients ?? throw new ArgumentNullException(nameof(clients)))];

    /// <summary>Works the step, from its request to its result, emitting what happens beside the stored stream.</summary>
    /// <param name="step">The step, as the engine resolved it.</param>
    /// <param name="events">A sink beside the stored stream; null where only the record is wanted.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    /// <returns>How the run ended.</returns>
    public async Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);

        var stream = StoredStream.Open(StepDirectory(step), step.Mask ?? Masking.Nothing);

        try
        {
            if (Client(step) is not { } client)
            {
                // Never begun, and the configuration is why: a record of the step exists all the same, saying so.
                return Ended(step, new AgentRunResult(
                    AgentRunStatus.NotStarted,
                    AgentNotStartedBecause.ConfigurationWrong,
                    Error: step.Client is { } missing
                        ? $"No agent client named '{missing}' is registered."
                        : "The step names no agent client, and there is not exactly one to take it."));
            }

            var messages = new AgentMessages();
            var pump = new AgentEventPump();
            pump.Attach(stream);

            if (events is not null)
            {
                pump.Attach(events);
            }

            AgentRunResult result;

            try
            {
                result = await client.RunAsync(Resolved(step, stream, messages), pump, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The run's own failure is the one to answer with: what was said is drained and the record closed
                // quietly around it, and a listener's failure is not raised into the middle of the run's.
                messages.Complete();
                pump.Complete();
                await pump.Drained.ConfigureAwait(false);
                stream.Dispose();
                throw;
            }

            messages.Complete();
            pump.Complete();
            await pump.Drained.ConfigureAwait(false);
            stream.Dispose();

            // The record is whole and the run has ended: the result is written after the stream that says how it went,
            // so the two cannot disagree, and a listener's failure is raised here, once the run is over.
            var ended = Ended(step, result);

            if (pump.Failure is { } failed)
            {
                throw failed;
            }

            return ended;
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>Works the step, yielding what happens as it happens, the stored stream attached as always.</summary>
    /// <remarks>
    /// A reader that stops early or is cancelled leaves the run to finish on its own, writing its records for a step
    /// no one is watching: the enumerator is a view, never the run's own lifetime.
    /// </remarks>
    /// <param name="step">The step, as the engine resolved it.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    /// <returns>Each event, in order, until the run has ended.</returns>
    public async IAsyncEnumerable<AgentEvent> Stream(AgentStepRequest step, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);

        var yielded = System.Threading.Channels.Channel.CreateUnbounded<AgentEvent>();

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
            cancellationToken);

        while (await yielded.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (yielded.Reader.TryRead(out var @event))
            {
                yield return @event;
            }
        }

        await run.ConfigureAwait(false);
    }

    /// <summary>The client the step names, or the one there is where it names none; null where there is none.</summary>
    /// <param name="step">The step.</param>
    private IAgentClient? Client(AgentStepRequest step)
        => step.Client is { } named
            ? _clients.FirstOrDefault(client => string.Equals(client.Name, named, StringComparison.Ordinal))
            : _clients.Count == 1 ? _clients[0] : null;

    /// <summary>Writes the run's own record beside its stream, and answers it.</summary>
    /// <param name="step">The step.</param>
    /// <param name="result">How the run ended.</param>
    /// <returns>The result.</returns>
    private AgentRunResult Ended(AgentStepRequest step, AgentRunResult result)
    {
        File.WriteAllText(Path.Combine(StepDirectory(step), ResultFileName), JsonSerializer.Serialize(result, JsonStateFile.Options) + "\n");

        return result;
    }

    /// <summary>The step's run, as the client takes it: the prompt filled, the places as they are, the stream its own.</summary>
    /// <param name="step">The step.</param>
    /// <param name="stream">The step's stored stream, opened.</param>
    /// <param name="messages">Where the engine tells the agent what it has to.</param>
    private static AgentRun Resolved(AgentStepRequest step, StoredStream stream, AgentMessages messages)
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
            step.Mask ?? Masking.Nothing,
            stream,
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

    /// <summary>One sink that takes each event into a channel, as the front end reads it.</summary>
    /// <param name="yielded">The channel the events are yielded through.</param>
    private sealed class ChannelSink(System.Threading.Channels.Channel<AgentEvent> yielded) : IAgentEventSink
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
