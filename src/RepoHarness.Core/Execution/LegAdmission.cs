using System.Globalization;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Execution;

/// <summary>
/// How a heavy leg's machine took it: how long it waited, the memory in use when it was let start, and the room it
/// claimed.
/// </summary>
/// <param name="Admitted">Whether it was let start; a leg that was not is <c>not-admitted</c>, and nothing of it ran.</param>
/// <param name="WaitedSeconds">How long it waited, for a slot, then for the memory, then for room for its build.</param>
/// <param name="MemoryPercent">
/// The memory in use it was let start at or, for a leg not let start while it held a slot, the reading it last took;
/// absent where it waited for a slot, or the memory was never read.
/// </param>
/// <param name="Memory">The same reading as a line says it, with what it was counted from.</param>
/// <param name="Unmeasured">
/// Why the memory in use could not be read, where it could not: a leg let start without it, having never read it in its
/// wait, or one not let start, having lost the count it had read.
/// </param>
/// <param name="Holders">
/// The legs that held the machine's slots, or claimed the room it needed, each as its line names it, where it was not
/// let start for want of either.
/// </param>
/// <param name="Record">The record of the machine's heavy legs it asked in: where to look at who holds and waits.</param>
/// <param name="Room">
/// The room it claimed as it was let start, as a line says it, or why that room could not be read; for a leg not let
/// start for want of room, the room it last read. Absent where its build needs nothing anyone said, and for a leg not let
/// start for any other reason.
/// </param>
public sealed record AdmissionFact(
    bool Admitted,
    double WaitedSeconds,
    double? MemoryPercent = null,
    string? Memory = null,
    string? Unmeasured = null,
    IReadOnlyList<string>? Holders = null,
    string? Record = null,
    string? Room = null)
{
    /// <summary>
    /// The fact as an admitted leg's line says it, beside its detail: <c>admitted after 3m12s, memory 71.2% in use
    /// (commit 81 GiB of 113.7 GiB)</c>; <see langword="null"/> for a leg not let start, which says why as its detail.
    /// </summary>
    public string? Describe()
    {
        if (!Admitted)
        {
            return null;
        }

        var waited = WaitedSeconds < 1
            ? "at once"
            : $"after {LedgerReport.FormatDuration(TimeSpan.FromSeconds(WaitedSeconds))}";

        var room = Room is null ? string.Empty : $"; room {Room}";

        return Memory is not null
            ? $"admitted {waited}, memory {Memory}{room}"
            : $"admitted {waited} without the memory in use, which could not be read: {Unmeasured}{room}";
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

    /// <summary>A leg let start, holding its slot, and any room it claimed, until disposed.</summary>
    internal static Admission Taken(SlotPlace place, AdmissionFact fact)
        => fact.Admitted ? new(place, fact, null) : throw new ArgumentException("A leg taken is one let start.", nameof(fact));

    /// <summary>A leg not let start, having given its place back.</summary>
    internal static Admission Refused(AdmissionFact fact, string refusal)
        => fact.Admitted ? throw new ArgumentException("A leg refused is one not let start.", nameof(fact)) : new(null, fact, refusal);

    /// <summary>Gives the leg's slot, and the room it claimed, back, once its work has ended.</summary>
    public void Dispose() => _place?.Dispose();
}

/// <summary>A heavy leg asking its machine to take it.</summary>
/// <param name="Rule">What the machine admits heavy legs by, as the command's configuration declares it.</param>
/// <param name="RunId">The run the leg is part of.</param>
/// <param name="Command">The command running it.</param>
/// <param name="Leg">The leg.</param>
/// <param name="Host">The host its tree is on, as the command line names it.</param>
/// <param name="Tree">The tree it works in, on that host.</param>
/// <param name="Variant">Its build variant, where it has one.</param>
/// <param name="Progress">Says what the leg is doing now, under its own name.</param>
/// <param name="Room">
/// What its build needs of the machine's room, where something says; <see langword="null"/> where nothing does, or where
/// its build needs nothing more than its directory holds.
/// </param>
public sealed record AdmissionRequest(
    AdmissionRule Rule,
    string RunId,
    string Command,
    string Leg,
    string Host,
    string Tree,
    string? Variant,
    Action<string> Progress,
    RoomNeed? Room = null);

/// <summary>
/// Admits a heavy leg onto its machine before its work starts: first one of the machine's heavy-leg slots, in the order
/// legs asked, then the memory in use below the machine's limit - read again after a settle where another leg holds a
/// slot, so two legs taking theirs together do not both start on one reading - and then, where its build's need is
/// known, the room that need takes, beside what every other admitted leg there claims. A leg that waits longer than the
/// machine allows is not let start, and its line names what held the slots, the memory in use it waited on, or the room
/// and who claimed it.
/// </summary>
/// <remarks>
/// Asked by the DssHarness process on the machine the leg's work runs on - for a WSL leg, the one that dispatched it -
/// which outlives that work: its slot, and the room it claimed, are given back when the work ends and, should the
/// process end first, are reclaimed by the next leg that looks at them. Waited with the monotonic clock, never the time
/// of day, which a host this serves steps by 25 seconds every few seconds.
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
    /// Waits until the machine takes the leg - a slot, then the memory, then room for its build where its need is known -
    /// or until it has waited as long as the machine allows. A leg taken holds its slot, and the room it claimed, until
    /// the answer is disposed; one not taken has given its place back.
    /// </summary>
    /// <param name="request">The leg, and what its machine admits by.</param>
    /// <param name="cancellationToken">Stops the wait, giving the leg's place back.</param>
    /// <exception cref="Results.HarnessException">
    /// The machine's record of its slots, or of the room its heavy legs claim, could not be named, read or written.
    /// </exception>
    public async Task<Admission> AdmitAsync(AdmissionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rule = request.Rule;
        var started = _clock.GetTimestamp();
        var place = _slots.Ask(request.RunId, request.Command, request.Leg, request.Host, request.Tree, request.Variant, rule.HeavyLegs);

        // The room as this wait last read it, the legs claiming it as a line last named them, and whether it stopped being
        // readable since.
        RoomStanding? lastRoom = null;
        IReadOnlyList<SlotEntry>? roomSaid = null;
        var roomLost = false;

        try
        {
            IReadOnlyList<SlotEntry>? heldBy = null;
            MemoryReading? last = null;
            var waitingForMemory = false;
            var readingLost = false;
            var settled = false;
            var everBelow = false;

            while (true)
            {
                // Looked at every time round, the memory's wait included: a leg whose place went - its record removed
                // by hand - is back in line, and waits its turn again rather than starting on a slot it no longer holds.
                var standing = _slots.Look(place);

                if (!standing.Holding)
                {
                    if (heldBy is null || !heldBy.SequenceEqual(standing.Holders))
                    {
                        request.Progress($"waits for {Slots(standing)}, {standing.Ahead} leg(s) ahead; held by {Holders(standing)}");
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
                            $"not admitted after {Waited(started)} waiting for {Slots(standing)}, held by {Holders(standing)}; "
                            + $"the machine's heavy legs are recorded in '{_slots.Location}'");
                    }

                    await _wait(Waits.Shorter(rule.Poll, Left(started, rule)), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                heldBy = null;

                var (reading, unmeasured) = _gauge.Read();

                if (reading is null)
                {
                    var why = unmeasured ?? "it gave no reading";

                    if (last is null)
                    {
                        // Never read in this wait: let start without it - on its slot, and its room where its build's need
                        // is known, for which it may still wait - and said on its line, as a leg placed where its room could
                        // not be measured is. Refused instead, a machine whose count cannot be read would never take a heavy
                        // leg.
                        if (TakeWithRoom(new AdmissionFact(true, Seconds(started), Unmeasured: why, Record: _slots.Location), null) is { } taken)
                        {
                            return taken;
                        }

                        // Waited for room: the memory is read, and settled, again before the leg starts.
                        settled = false;
                        await _wait(Waits.Shorter(rule.Poll, Left(started, rule)), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // Read before in this wait and not now: nothing is decided on a reading this old - one above the limit
                    // would otherwise let the leg start the moment the count failed once - so it is read again next time.
                    if (!readingLost)
                    {
                        request.Progress($"holds a heavy-leg slot, and could not read the memory in use again: {why}; it last read {last.Describe()}");
                        readingLost = true;
                    }

                    if (Left(started, rule) <= TimeSpan.Zero)
                    {
                        return Refuse(
                            place,
                            started,
                            last,
                            null,
                            $"not admitted after {Waited(started)}: it held a heavy-leg slot, and the memory in use, last read as "
                            + $"{last.Describe()}, could not be read again: {why}",
                            why);
                    }

                    await _wait(Waits.Shorter(rule.Poll, Left(started, rule)), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                (last, readingLost) = (reading, false);

                // Decided on the share a line gives, so a leg is never let start at a reading its own line would show at
                // the limit.
                if (reading.Rounded < rule.MaxMemoryPercent)
                {
                    everBelow = true;

                    // Where another leg holds a slot, read again after a settle, and started only if still below: two legs
                    // taking their slots together would otherwise both start on one reading.
                    if (settled || rule.SettleMost <= TimeSpan.Zero || !standing.Holders.Any(holder => holder != place.Entry))
                    {
                        if (TakeWithRoom(new AdmissionFact(true, Seconds(started), reading.Rounded, reading.Describe(), Record: _slots.Location), reading) is { } taken)
                        {
                            return taken;
                        }

                        // Waited for room: another leg may have started meanwhile, so the memory is settled again before
                        // this one starts on a reading.
                        settled = false;
                        await _wait(Waits.Shorter(rule.Poll, Left(started, rule)), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var settling = Waits.Shorter(_settle(rule.SettleLeast, rule.SettleMost), Left(started, rule));

                    request.Progress($"memory {reading.Describe()}; another leg holds a slot, so it looks again in {Said(settling)}");
                    await _wait(settling, cancellationToken).ConfigureAwait(false);
                    settled = true;
                    continue;
                }

                settled = false;

                if (!waitingForMemory)
                {
                    request.Progress($"holds a heavy-leg slot, and waits for the memory {reading.Describe()} to fall below {Limit(rule)}");
                    waitingForMemory = true;
                }

                if (Left(started, rule) <= TimeSpan.Zero)
                {
                    return Refuse(
                        place,
                        started,
                        reading,
                        null,
                        everBelow
                            ? $"not admitted after {Waited(started)}: it held a heavy-leg slot, and the memory fell below {Limit(rule)} "
                                + $"only to rise above it again before the leg could start; it last read {reading.Describe()}"
                            : $"not admitted after {Waited(started)}: it held a heavy-leg slot, and the memory {reading.Describe()} "
                                + $"never fell below {Limit(rule)}");
                }

                await _wait(Waits.Shorter(rule.Poll, Left(started, rule)), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Stopped or failed while it waited: its place goes back, as it does once a taken leg's work ends.
            place.Dispose();
            throw;
        }

        // The leg taken with the room its build needs claimed, where it fits beside every other admitted leg's claim on
        // that filesystem - claimed as it is taken, so two legs let start together never both take the one room left - or
        // not taken: null where it waits for room and looks again, or the leg refused once it has waited as long as the
        // machine allows. A room never read in this wait lets the leg start, claimed against every filesystem; one read
        // before and not now decides nothing, as a memory reading lost does not.
        Admission? TakeWithRoom(AdmissionFact fact, MemoryReading? reading)
        {
            if (request.Room is not { } room)
            {
                return Take(request, place, fact);
            }

            var claim = _slots.Claim(place, room, takeUnread: lastRoom is null);

            if (claim.Fits)
            {
                return Take(
                    request,
                    place,
                    fact with
                    {
                        Room = claim.Disk is null
                            ? $"unread, so ~{DiskSpace.Size(room.Bytes)} is claimed against every filesystem here: {claim.Unmeasured}"
                            : Claimed(room, claim),
                    });
            }

            if (claim.Disk is null)
            {
                var read = lastRoom!.Describe(room);

                if (!roomLost)
                {
                    request.Progress($"holds a heavy-leg slot, and could not read the room again: {claim.Unmeasured}; it last read {read}");
                    roomLost = true;
                }

                return Left(started, rule) > TimeSpan.Zero
                    ? null
                    : Refuse(
                        place,
                        started,
                        reading,
                        null,
                        $"not admitted after {Waited(started)}: it held a heavy-leg slot, and the room, last read as {read}, could "
                            + $"not be read again: {claim.Unmeasured}",
                        claimants: lastRoom.Claimants,
                        record: _slots.RoomLocation,
                        room: read);
            }

            (lastRoom, roomLost) = (claim, false);

            // Said again whenever the legs claiming it change, as the legs holding the slots are.
            if (roomSaid is null || !roomSaid.SequenceEqual(claim.Claimants))
            {
                request.Progress($"holds a heavy-leg slot, and waits for room: {claim.Describe(room)}");
                roomSaid = claim.Claimants;
            }

            return Left(started, rule) > TimeSpan.Zero
                ? null
                : Refuse(
                    place,
                    started,
                    reading,
                    null,
                    $"not admitted after {Waited(started)}: it held a heavy-leg slot, and its build would not fit: "
                        + $"{claim.Describe(room)}; the room each heavy leg claims is recorded in '{_slots.RoomLocation}'",
                    claimants: claim.Claimants,
                    record: _slots.RoomLocation,
                    room: claim.Describe(room));
        }
    }

    /// <summary>
    /// The room a leg claimed as it was let start, as its line says it: <c>~31 GiB of 40 GiB free on '/'</c>, with what the
    /// other legs claim there where they claim any.
    /// </summary>
    private static string Claimed(RoomNeed room, RoomStanding claim)
    {
        var disk = claim.Disk!;
        var beside = claim.Claimed > 0 ? $", beside ~{DiskSpace.Size(claim.Claimed)} other legs claim" : string.Empty;

        return $"~{DiskSpace.Size(room.Bytes)} of {DiskSpace.Size(disk.FreeBytes)} free on '{disk.Filesystem}'{room.Where}{beside}";
    }

    /// <summary>A leg let start, holding its slot, as its line then says.</summary>
    private static Admission Take(AdmissionRequest request, SlotPlace place, AdmissionFact fact)
    {
        request.Progress(fact.Describe()!);
        return Admission.Taken(place, fact);
    }

    /// <summary>
    /// A leg not let start, its place given back, its line naming what held it: the slots' holders and their record, or the
    /// room, the legs claiming it and the room's record.
    /// </summary>
    private Admission Refuse(
        SlotPlace place,
        long started,
        MemoryReading? reading,
        SlotStanding? standing,
        string refusal,
        string? unmeasured = null,
        IReadOnlyList<SlotEntry>? claimants = null,
        string? record = null,
        string? room = null)
    {
        place.Dispose();

        var holders = standing?.Holders ?? claimants;

        return Admission.Refused(
            new AdmissionFact(
                false,
                Seconds(started),
                reading?.Rounded,
                reading?.Describe(),
                unmeasured,
                holders is null ? null : [.. holders.Select(holder => holder.Describe())],
                record ?? _slots.Location,
                room),
            refusal);
    }

    /// <summary>The slots a leg waits for, as its line names them: <c>one of this machine's 2 heavy-leg slot(s)</c>.</summary>
    private static string Slots(SlotStanding standing) => $"one of this machine's {standing.Slots} heavy-leg slot(s)";

    private static string Holders(SlotStanding standing) => string.Join("; ", standing.Holders.Select(holder => holder.Describe()));

    /// <summary>The machine's limit, as a line gives it: <c>76%</c>.</summary>
    private static string Limit(AdmissionRule rule) => string.Create(CultureInfo.InvariantCulture, $"{rule.MaxMemoryPercent:0.#}%");

    private TimeSpan Left(long started, AdmissionRule rule) => rule.MaxWait - _clock.GetElapsedTime(started);

    private double Seconds(long started) => LedgerReport.Seconds(_clock.GetElapsedTime(started));

    private string Waited(long started) => Said(_clock.GetElapsedTime(started));

    /// <summary>A time as a line says it, as the ledger's table does, and a time of none as <c>0s</c>.</summary>
    private static string Said(TimeSpan time) => LedgerReport.FormatDuration(time) is { Length: > 0 } said ? said : "0s";
}
