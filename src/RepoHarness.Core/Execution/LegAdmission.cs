using System.Globalization;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Execution;

/// <summary>How a heavy leg's machine took it: how long it waited, and the memory in use when it was let start.</summary>
/// <param name="Admitted">Whether it was let start; a leg that was not is <c>not-admitted</c>, and nothing of it ran.</param>
/// <param name="WaitedSeconds">How long it waited, for a slot and then for the memory.</param>
/// <param name="MemoryPercent">The memory in use it was let start at, or last read at where it was not; absent where it could not be read.</param>
/// <param name="Memory">The same reading as a line says it, with what it was counted from.</param>
/// <param name="Unmeasured">Why the memory in use could not be read, where it could not: a leg let start on its slot alone.</param>
/// <param name="Holders">The legs that held the machine's slots when it was not let start, each as its line names it.</param>
public sealed record AdmissionFact(
    bool Admitted,
    double WaitedSeconds,
    double? MemoryPercent = null,
    string? Memory = null,
    string? Unmeasured = null,
    IReadOnlyList<string>? Holders = null)
{
    /// <summary>
    /// The fact as an admitted leg's line says it, beside its detail: <c>admitted after 3m12s, memory 71.2% in use
    /// (commit 81 GiB of 113.7 GiB)</c>. A leg not let start says why as its detail instead.
    /// </summary>
    public string Describe()
    {
        var waited = WaitedSeconds < 1
            ? "at once"
            : $"after {LedgerReport.FormatDuration(TimeSpan.FromSeconds(WaitedSeconds))}";

        return Memory is not null
            ? $"admitted {waited}, memory {Memory}"
            : $"admitted {waited} on its slot alone, the memory in use unread: {Unmeasured}";
    }
}

/// <summary>What asking a machine to take a heavy leg came to: its place there, held until disposed, or why it was not taken.</summary>
public sealed class Admission : IDisposable
{
    private readonly SlotPlace? _place;

    private Admission(SlotPlace? place, AdmissionFact fact, string? refusal)
    {
        _place = place;
        Fact = fact;
        Refusal = refusal;
    }

    /// <summary>What the leg's line says of it.</summary>
    public AdmissionFact Fact { get; }

    /// <summary>Why the leg was not let start, as its <c>not-admitted</c> line says it; <see langword="null"/> where it was.</summary>
    public string? Refusal { get; }

    /// <summary>A leg let start, holding its slot until disposed.</summary>
    internal static Admission Taken(SlotPlace place, AdmissionFact fact) => new(place, fact, null);

    /// <summary>A leg not let start, having given its place back.</summary>
    internal static Admission Refused(AdmissionFact fact, string refusal) => new(null, fact, refusal);

    /// <summary>Gives the leg's slot back, once its heavy work has ended.</summary>
    public void Dispose() => _place?.Dispose();
}

/// <summary>A heavy leg asking its machine to take it.</summary>
/// <param name="Rule">What the machine admits heavy legs by.</param>
/// <param name="RunId">The run the leg is part of.</param>
/// <param name="Command">The command running it.</param>
/// <param name="Leg">The leg.</param>
/// <param name="Tree">The tree it works in, as a message names it.</param>
/// <param name="Variant">Its build variant, where it has one.</param>
/// <param name="Progress">Says what the leg is doing now, under its own name.</param>
public sealed record AdmissionRequest(
    AdmissionRule Rule,
    string RunId,
    string Command,
    string Leg,
    string Tree,
    string? Variant,
    Action<string> Progress);

/// <summary>
/// Admits a heavy leg onto its machine before its heavy work starts: first one of the machine's heavy-leg slots, in
/// the order legs asked, then the memory in use below the machine's limit - read again after a settle where another
/// leg holds a slot, so two legs taking theirs together do not both start on one reading. A leg that waits longer
/// than the machine allows is not let start, and its line names what held the slots and the memory in use.
/// </summary>
/// <remarks>
/// Asked by the DssHarness process on the machine the leg's work runs on, which lives exactly as long as that work:
/// its slot is held by that process, and is reclaimed the moment anyone looks after it has ended. Waited with the
/// monotonic clock, never the time of day, which a host this serves steps by 25 seconds every few seconds.
/// </remarks>
/// <param name="slots">The machine's heavy-leg slots.</param>
/// <param name="gauge">Reads the memory in use.</param>
/// <param name="clock">Measures the wait.</param>
/// <param name="wait">Waits as long as it is given; production waits on <paramref name="clock"/>.</param>
/// <param name="settle">Picks a settle between the least and the most; production picks at random.</param>
public sealed class LegAdmission(
    HeavyLegSlots slots,
    IMemoryGauge gauge,
    TimeProvider clock,
    Func<TimeSpan, CancellationToken, Task> wait,
    Func<TimeSpan, TimeSpan, TimeSpan> settle)
{
    private readonly HeavyLegSlots _slots = slots;
    private readonly IMemoryGauge _gauge = gauge;
    private readonly TimeProvider _clock = clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait;
    private readonly Func<TimeSpan, TimeSpan, TimeSpan> _settle = settle;

    /// <summary>What production admits by: waits on the system's clock, and settles for a time picked at random.</summary>
    /// <param name="slots">The machine's heavy-leg slots.</param>
    /// <param name="gauge">Reads the memory in use.</param>
    public LegAdmission(HeavyLegSlots slots, IMemoryGauge gauge)
        : this(
            slots,
            gauge,
            TimeProvider.System,
            (delay, token) => Task.Delay(delay, TimeProvider.System, token),
            (least, most) => TimeSpan.FromSeconds(Random.Shared.Next((int)least.TotalSeconds, (int)most.TotalSeconds + 1)))
    {
    }

    /// <summary>
    /// Waits until the machine takes the leg - a slot, then the memory - or until it has waited as long as the machine
    /// allows. A leg taken holds its slot until the answer is disposed; one not taken has given its place back.
    /// </summary>
    /// <param name="request">The leg, and what its machine admits by.</param>
    /// <param name="cancellationToken">Stops the wait, giving the leg's place back.</param>
    /// <exception cref="Results.HarnessException">The machine's record of its slots could not be read or written.</exception>
    public async Task<Admission> AdmitAsync(AdmissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = request.Rule;
        var started = _clock.GetTimestamp();
        var place = _slots.Ask(request.RunId, request.Command, request.Leg, request.Tree, request.Variant);

        try
        {
            IReadOnlyList<SlotEntry>? heldBy = null;
            var waitingForMemory = false;
            var settled = false;

            while (true)
            {
                // Looked at every time round, the memory's wait included: a leg whose place went - its record removed
                // by hand - is back in line, and waits its turn again rather than starting on a slot it no longer holds.
                var standing = _slots.Look(place, rule.HeavyLegs);

                if (!standing.Holding)
                {
                    if (heldBy is null || !heldBy.SequenceEqual(standing.Holders))
                    {
                        request.Progress(
                            $"waits for one of this machine's {rule.HeavyLegs} heavy-leg slot(s), {standing.Ahead} leg(s) ahead; "
                            + $"held by {Holders(standing)}");
                        heldBy = standing.Holders;
                    }

                    (waitingForMemory, settled) = (false, false);

                    if (Left(started, rule) <= TimeSpan.Zero)
                    {
                        return Refuse(
                            place,
                            started,
                            null,
                            standing,
                            $"not admitted after {Waited(started)} waiting for one of this machine's {rule.HeavyLegs} heavy-leg "
                            + $"slot(s), held by {Holders(standing)}");
                    }

                    await _wait(Shorter(rule.Poll, Left(started, rule)), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                heldBy = null;

                var (reading, unmeasured) = _gauge.Read();

                if (reading is null)
                {
                    return Unread(request, place, started, unmeasured);
                }

                if (reading.Percent < rule.MaxMemoryPercent)
                {
                    // Where another leg holds a slot, read again after a settle, and started only if still below: two legs
                    // taking their slots together would otherwise both start on one reading.
                    if (settled || rule.SettleMost <= TimeSpan.Zero || !standing.Holders.Any(holder => holder != place.Entry))
                    {
                        return Admit(request, place, started, reading);
                    }

                    var settling = Shorter(_settle(rule.SettleLeast, rule.SettleMost), Left(started, rule));

                    request.Progress($"memory {reading.Describe()}; another leg holds a slot, so it looks again in {Said(settling)}");
                    await _wait(settling, cancellationToken).ConfigureAwait(false);
                    settled = true;
                    continue;
                }

                settled = false;

                if (!waitingForMemory)
                {
                    request.Progress(
                        string.Create(CultureInfo.InvariantCulture, $"holds a heavy-leg slot, and waits for the memory {reading.Describe()} to fall below {rule.MaxMemoryPercent:0.#}%"));
                    waitingForMemory = true;
                }

                if (Left(started, rule) <= TimeSpan.Zero)
                {
                    return Refuse(
                        place,
                        started,
                        reading,
                        null,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"not admitted after {Waited(started)}: it held a heavy-leg slot, and the memory {reading.Describe()} never fell below {rule.MaxMemoryPercent:0.#}%"));
                }

                await _wait(Shorter(rule.Poll, Left(started, rule)), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Stopped or failed while it waited: its place goes back, as it does once a taken leg's work ends.
            place.Dispose();
            throw;
        }
    }

    private Admission Admit(AdmissionRequest request, SlotPlace place, long started, MemoryReading reading)
    {
        var fact = new AdmissionFact(true, Seconds(started), reading.Rounded, reading.Describe());

        request.Progress(fact.Describe());
        return Admission.Taken(place, fact);
    }

    /// <summary>
    /// A leg let start on its slot alone, the memory in use unread: said on its line, as a leg placed where its room
    /// could not be measured is. Refused instead, a machine whose count cannot be read would never take a heavy leg.
    /// </summary>
    private Admission Unread(AdmissionRequest request, SlotPlace place, long started, string? unmeasured)
    {
        var fact = new AdmissionFact(true, Seconds(started), Unmeasured: unmeasured ?? "it gave no reading");

        request.Progress(fact.Describe());
        return Admission.Taken(place, fact);
    }

    private Admission Refuse(SlotPlace place, long started, MemoryReading? reading, SlotStanding? standing, string refusal)
    {
        place.Dispose();

        return Admission.Refused(
            new AdmissionFact(
                false,
                Seconds(started),
                reading?.Rounded,
                reading?.Describe(),
                Holders: standing is null ? null : [.. standing.Holders.Select(holder => holder.Describe())]),
            refusal);
    }

    private static string Holders(SlotStanding standing) => string.Join("; ", standing.Holders.Select(holder => holder.Describe()));

    private TimeSpan Left(long started, AdmissionRule rule) => rule.MaxWait - _clock.GetElapsedTime(started);

    private double Seconds(long started) => Math.Round(_clock.GetElapsedTime(started).TotalSeconds, 3);

    private string Waited(long started) => Said(_clock.GetElapsedTime(started));

    /// <summary>A time as a line says it, as the ledger's table does, and a time of none as <c>0s</c>.</summary>
    private static string Said(TimeSpan time) => LedgerReport.FormatDuration(time) is { Length: > 0 } said ? said : "0s";

    /// <summary>
    /// <paramref name="wanted"/>, or what is <paramref name="left"/> of the wait where that is less - never below nothing,
    /// since time passes between the look that found some left and the wait.
    /// </summary>
    private static TimeSpan Shorter(TimeSpan wanted, TimeSpan left)
        => left <= TimeSpan.Zero ? TimeSpan.Zero : wanted < left ? wanted : left;
}
