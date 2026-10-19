using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What one leg of a step that runs legs came to, as the engine reads it: the leg's name and its own verdict.</summary>
/// <param name="Leg">The leg's name, as the step's command names it.</param>
/// <param name="Verdict">The verdict the leg reached, on its own and never the command's own code.</param>
public sealed record FlowLeg(string Leg, LegVerdict Verdict);

/// <summary>How the engine sorts a leg's own verdict: red (a finding about the code), incomplete (run again), a failure that is not red, or none of the three.</summary>
public enum FlowLegClass
{
    /// <summary>A finding about the code: a leg that failed, and of a sweep an arm that violated, survived or was unattributed.</summary>
    Red,

    /// <summary>The leg reached no verdict of its own, and running it again finishes it: its inputs moved, its build directory was contended, it was not admitted, a lock was held, or it was stopped or skipped.</summary>
    Incomplete,

    /// <summary>A failure running again does not change: the leg passed with nothing to show it ran, its log is another run's, or the tool reached no verdict.</summary>
    FailureThatIsNotRed,

    /// <summary>Decides nothing: the leg passed, or nobody asked for it.</summary>
    None,
}

/// <summary>
/// How the engine reads the legs of a step that runs them, each leg's verdict on its own and never the command's own
/// code: which legs are red, which incomplete, which a failure that is not red - and what the step ends with.
/// </summary>
/// <remarks>
/// <para>
/// Red is a verdict (the plan's own words): a red leg is a finding about the code, and a step with any red leg under
/// <c>failOn: red</c> fails with its <em>worst red leg's</em> code, by the order the verdict table already has - which
/// is not the command's exit code, for that ranks a leg whose inputs moved above a failed one. The facts of each
/// verdict come from <see cref="Verdicts"/>, the one table, and nothing here spells a verdict's meaning of its own.
/// </para>
/// <para>
/// An incomplete leg is run again (by the step's retries, and past them the step fails with the worst incomplete leg's
/// code); a failure that is not red - unwitnessed, log-held, poisoned - fails the step whatever <c>failOn</c> says, for
/// running it again would change nothing, and is not retried.
/// </para>
/// </remarks>
public static class FlowLegVerdicts
{
    /// <summary>
    /// How the engine reads <paramref name="verdict"/>: a leg that failed, or a sweep's own failure, is red; a failure
    /// whose remedy is to run again is incomplete, and so is every verdict a leg reached without one of its own -
    /// stopped, or skipped having been asked; the failures that remain are not red; and a leg that passed, or that
    /// nobody asked for, decides nothing.
    /// </summary>
    /// <param name="verdict">One leg's own verdict.</param>
    public static FlowLegClass ClassOf(LegVerdict verdict)
    {
        var info = Verdicts.Describe(verdict);

        if (verdict == LegVerdict.Failed || info.OfASweep)
        {
            return FlowLegClass.Red;
        }

        if (info.IsFailure)
        {
            // Only a failure whose code the retry table runs again is incomplete; the rest - unwitnessed, log-held,
            // poisoned - fail the step as they are.
            return info.ExitCode is LegExit.InputsMoved or LegExit.Contended or LegExit.NotAdmitted or HarnessExit.Refused
                ? FlowLegClass.Incomplete
                : FlowLegClass.FailureThatIsNotRed;
        }

        return Verdicts.ReachedNone(verdict) ? FlowLegClass.Incomplete : FlowLegClass.None;
    }

    /// <summary>Every leg of one class, in the order they were given.</summary>
    /// <param name="legs">Every leg the step ran, with its verdict.</param>
    /// <param name="class">The class to pick out.</param>
    public static IReadOnlyList<FlowLeg> OfClass(IReadOnlyList<FlowLeg> legs, FlowLegClass @class)
        => [.. legs.Where(leg => ClassOf(leg.Verdict) == @class)];

    /// <summary>
    /// The worst verdict among <paramref name="legs"/> - the most fundamental by the table's own order - or
    /// <see langword="null"/> where there is none.
    /// </summary>
    /// <param name="legs">Legs of one class.</param>
    public static LegVerdict? Worst(IReadOnlyList<FlowLeg> legs)
        => legs.Count == 0 ? null : Verdicts.Worst(legs.Select(leg => leg.Verdict));

    /// <summary>What the table says a run reports where <paramref name="verdict"/> decides it.</summary>
    /// <param name="verdict">A leg's own verdict.</param>
    public static int CodeOf(LegVerdict verdict) => Verdicts.ExitCodeFor(verdict);
}
