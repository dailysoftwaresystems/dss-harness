using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// A run's records with a crash where a test puts one: the first write of the run's updates that <c>picks</c> chooses
/// - or the first change of a step that <c>picksStep</c> chooses - reaches the disk where <c>lands</c> says so, and
/// nothing after it does - the process ended there. What the engine goes on doing in memory is never written, so a
/// coordinator that takes the run up from the disk meets the run as the crash left it.
/// </summary>
/// <param name="inner">The run's records.</param>
/// <param name="picks">Chooses the write of the run's updates the process ends at.</param>
/// <param name="lands">Whether that write reaches the disk before the process ends.</param>
/// <param name="picksStep">Chooses the change of a step the process ends at, by the instance and the state it goes to; null for none.</param>
internal sealed class CrashingRecords(IFlowRecords inner, Func<FlowUpdatesFile, bool> picks, bool lands = true, Func<string?, string?, bool>? picksStep = null) : IFlowRecords
{
    private bool _ended;

    /// <summary>Whether the process has ended: nothing written from here on reaches the disk.</summary>
    public bool Ended => _ended;

    /// <inheritdoc/>
    public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
    {
        if (_ended)
        {
            return;
        }

        var picked = kind == FlowChangeKind.Step && picksStep?.Invoke(instance, to) == true;

        if (!picked || lands)
        {
            inner.Write(kind, snapshot, instance, from, to, attempt, detail);
        }

        _ended = picked;
    }

    /// <inheritdoc/>
    public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
    {
        if (!_ended)
        {
            inner.Question(questions, refused);
        }
    }

    /// <inheritdoc/>
    public void Updates(FlowUpdatesFile updates)
    {
        if (_ended)
        {
            return;
        }

        var picked = picks(updates);

        if (!picked || lands)
        {
            inner.Updates(updates);
        }

        _ended = picked;
    }
}
