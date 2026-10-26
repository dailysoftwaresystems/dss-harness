using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;

namespace DssHarness.Tests;

/// <summary>A run as a client is given it, for a test of what works one: its step's directory, its places and its mask.</summary>
internal static class AgentRuns
{
    /// <summary>
    /// A run whose step keeps its records in <paramref name="stepDirectory"/>, working in <paramref name="workingDirectory"/>,
    /// masked by <paramref name="mask"/> - nothing where none is given - and decided against <paramref name="ground"/>.
    /// </summary>
    internal static AgentRun Of(
        string stepDirectory,
        string workingDirectory,
        ILineMask? mask = null,
        AgentPolicyGround? ground = null,
        AgentLimits? limits = null,
        AgentCommandSettings? commands = null,
        IReadOnlyList<string>? withheld = null,
        AgentWebSearch? webSearch = null)
        => new(
            "system",
            "the task",
            new AgentWorkspace(workingDirectory, workingDirectory, workingDirectory, [], workingDirectory, workingDirectory),
            Output: null,
            Conversation: null,
            limits ?? new AgentLimits(null, null, null, null),
            Provider: "anthropic",
            Model: "claude-opus-5-5",
            Effort: null,
            [],
            [],
            [],
            mask ?? new Masking(),
            stepDirectory,
            new AgentHandoffSignal(TimeSpan.FromSeconds(1)),
            new AgentMessages())
        {
            Ground = ground,
            Commands = commands ?? AgentCommandSettings.Default,
            Withheld = withheld ?? [],
            WebSearch = webSearch,
        };
}

/// <summary>A mask that masks one secret, or nothing where it is given none.</summary>
/// <param name="secret">The secret.</param>
/// <param name="masked">What stands in its place.</param>
internal sealed class Masking(string? secret = null, string masked = "***") : ILineMask
{
    public string Redact(string? text) => secret is null ? text ?? string.Empty : (text ?? string.Empty).Replace(secret, masked, StringComparison.Ordinal);

    public int Cut(string text) => text.Length;
}

/// <summary>Every event a run emitted, in the order each was taken, from any number of writers.</summary>
internal sealed class RecordedEvents : IAgentEventSink
{
    private readonly List<AgentEvent> _events = [];
    private readonly Lock _gate = new();

    /// <summary>The events, in the order taken.</summary>
    public IReadOnlyList<AgentEvent> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public ValueTask EmitAsync(AgentEvent @event)
    {
        lock (_gate)
        {
            _events.Add(@event);
        }

        return ValueTask.CompletedTask;
    }
}
