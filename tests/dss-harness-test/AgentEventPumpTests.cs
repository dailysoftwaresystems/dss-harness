using System.Collections.Concurrent;
using System.Globalization;
using DssHarness.Core.Agents;

namespace DssHarness.Tests;

/// <summary>
/// The run's event pump under back-pressure: producers that outrun a record that holds its ground wait for room - none
/// of their events dropped, none taken out of its producer's order - and once the record moves again every event is
/// recorded, numbered in the order the pump took them.
/// </summary>
public sealed class AgentEventPumpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Four producers post several times the pump's room each while the record is held: they wait, never finishing while
    /// it is held - for a pump that dropped, as a Drop mode would, would let them through and lose what it dropped - and
    /// once it is let go, every event of every producer is recorded once, each producer's in the order it posted them,
    /// numbered one after another from 1.
    /// </summary>
    [Fact]
    public async Task TheEventPump_WaitsAndNeverDrops()
    {
        const int Producers = 4;
        var each = AgentEventPump.Capacity * 3;
        using var gate = new ManualResetEventSlim();
        var recorded = new ConcurrentQueue<AgentEvent>();
        var record = new HeldSink(gate, recorded);
        var pump = new AgentEventPump(record, TimeProvider.System, Token);

        var producing = Enumerable.Range(0, Producers).Select(producer => Task.Run(
            async () =>
            {
                for (var count = 0; count < each; count++)
                {
                    await pump.EmitAsync(new Notice($"{producer}:{count.ToString(CultureInfo.InvariantCulture)}"));
                }
            },
            Token)).ToList();

        // The record holds the first event, so the producers fill the pump's room and wait: none can finish.
        var all = Task.WhenAll(producing);
        var sprint = await Task.WhenAny(all, Task.Delay(TimeSpan.FromMilliseconds(500), Token));

        Assert.NotSame(all, sprint);
        Assert.True(recorded.Count <= 1, $"{recorded.Count} events were recorded while the record was held");

        gate.Set();

        await all.WaitAsync(TimeSpan.FromSeconds(60), Token);
        await pump.DisposeAsync();

        var events = recorded.ToList();

        Assert.Equal(Producers * each, events.Count);
        Assert.Equal(Enumerable.Range(1, Producers * each).Select(seq => (long)seq), events.Select(@event => @event.Seq));

        foreach (var producer in Enumerable.Range(0, Producers))
        {
            Assert.Equal(
                Enumerable.Range(0, each).Select(count => $"{producer}:{count.ToString(CultureInfo.InvariantCulture)}"),
                events.Cast<Notice>().Select(notice => notice.Text).Where(text => text.StartsWith($"{producer}:", StringComparison.Ordinal)));
        }

        Assert.Null(pump.RecordFailure);
    }

    /// <summary>A record that holds every event until <paramref name="gate"/> is set, then keeps each it is handed.</summary>
    private sealed class HeldSink(ManualResetEventSlim gate, ConcurrentQueue<AgentEvent> kept) : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent @event)
        {
            gate.Wait(TimeSpan.FromSeconds(60));
            kept.Enqueue(@event);
            return ValueTask.CompletedTask;
        }
    }
}
