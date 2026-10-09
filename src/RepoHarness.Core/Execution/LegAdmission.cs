using System.Globalization;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
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

        var room = Room is null ? string.Empty : $", and {Room}";

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
/// <param name="RunId">
/// The run the leg is part of - for a leg on an ssh host, the run of the machine that dispatched it - which its slot's
/// entry records: a wait for slots only legs of that run hold does not count against the machine's limit.
/// </param>
/// <param name="Command">The command running it.</param>
/// <param name="Leg">The leg, or a unit of its work, named <c>&lt;leg&gt;/&lt;unit&gt;</c>.</param>
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
    RoomNeed? Room = null)
{
    /// <summary>
    /// Whether, where another leg holds a slot, the memory is read again after a settle before this one starts, so two
    /// legs taking their slots together do not both start on one reading. A unit of a leg its machine already took once -
    /// an arm of a sweep after the sweep's first - starts on one reading: the slots it would wait out are held by the
    /// other units of its own sweep, which a settle for each would hold back 15 to 90 seconds an arm.
    /// </summary>
    public bool Settle { get; init; } = true;

    /// <summary>
    /// Drops what this machine counts as in use and could have back at once - the page cache of WSL's virtual machine - as
    /// the leg is about to wait on the memory: at most once a minute in the process, whichever leg is about to wait, the
    /// memory then read again once what the drop gives back has reached this machine's count. <see langword="null"/> where
    /// nothing is dropped for it.
    /// </summary>
    public Func<CancellationToken, Task<PageCacheDrop?>>? DropPageCache { get; init; }
}

/// <summary>
/// Admits a heavy leg onto its machine before its work starts: first one of the machine's heavy-leg slots, in the order
/// legs asked, then the memory in use below the machine's limit - read again after a settle where another leg holds a
/// slot, so two legs taking theirs together do not both start on one reading - and then, where its build's need is
/// known, the room that need takes, beside what every other admitted leg there claims. A leg that waits longer than the
/// machine allows is not let start, and its line names what held the slots, the memory in use it waited on, or the room
/// and who claimed it. A wait for slots only legs of its own run hold - its command's other legs, asked for at once -
/// does not count: it is certain to end, and is said to be its own. A leg about to wait on the memory has what this
/// machine could have back at once given back first, where its request says how - WSL's page cache
/// (<see cref="AdmissionRequest.DropPageCache"/>) - and reads the memory again once it has come back.
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
    /// <summary>
    /// The longest a leg's wait goes without saying where it stands: said again, as it reads now, once this long has
    /// passed since its last line. A wait that says nothing for long reads as a hang - measured, a leg's one line of a
    /// 39-minute wait for the memory reached its reader with its admission, through a pipe that passed each line on only
    /// once the next came. No wait before a look runs past it: a machine's poll, and its settle, may each be an hour,
    /// so a poll is cut at when the next line is due, and a settle is waited whole in pieces no longer than this, the
    /// leg saying between them that it still waits to look.
    /// </summary>
    public static readonly TimeSpan SaidAgainEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The least time between two drops of WSL's page cache for legs about to wait on the memory, whichever leg of this
    /// process asks: a drop takes the cache every build in the virtual machine reads from, and what it gives back takes
    /// most of a minute to reach this machine's count (<see cref="PageCacheDrop.HandedBackWithin"/>).
    /// </summary>
    public static readonly TimeSpan DropsAtMostEvery = TimeSpan.FromMinutes(1);

    private readonly HeavyLegSlots _slots = slots;
    private readonly IMemoryGauge _gauge = gauge;
    private readonly TimeProvider _clock = clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait;
    private readonly Func<TimeSpan, TimeSpan, TimeSpan> _settle = settle;

    // When WSL's page cache was last dropped for a leg about to wait, by this process: guarded, legs waiting together ask
    // together.
    private readonly Lock _dropping = new();
    private long? _droppedAt;

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

        // When the wait last said where it stands: once SaidAgainEvery has passed since, it says so again, as it reads now.
        var saidAt = started;

        // What of the wait does not count against the machine's limit: the time the leg spent waiting only behind legs of
        // its own command - a WSL leg behind its command's Windows legs, asked for together - which is certain to end,
        // and is never a reason to turn it away. Counted from each look that found it so to the next look.
        var excused = TimeSpan.Zero;
        long? ownSince = null;

        try
        {
            IReadOnlyList<SlotEntry>? heldBy = null;
            bool? ownSaid = null;
            MemoryReading? last = null;
            var waitingForMemory = false;
            var readingLost = false;
            var settled = false;
            var everBelow = false;

            // A drop of WSL's page cache this wait made, with the memory as read before it, until the memory read again says
            // what came back; and why a drop could not be made, as the wait last said it.
            (MemoryReading Before, PageCacheDrop Drop, long At)? dropped = null;
            string? notDropped = null;

            while (true)
            {
                // Looked at every time round, the memory's wait included: a leg whose place went - its record removed
                // by hand - is back in line, and waits its turn again rather than starting on a slot it no longer holds.
                var standing = _slots.Look(place);
                var lookedAt = _clock.GetTimestamp();

                if (ownSince is { } since)
                {
                    excused += _clock.GetElapsedTime(since, lookedAt);
                }

                var own = !standing.Holding && standing.Ahead > 0 && standing.InLineAhead.All(entry => entry.RunId == request.RunId);
                ownSince = own ? lookedAt : null;

                if (!standing.Holding)
                {
                    var whose = own ? $", each its own command's, which does not count against the {Said(rule.MaxWait)} it may wait" : string.Empty;

                    if (heldBy is null || !heldBy.SequenceEqual(standing.Holders) || ownSaid != own)
                    {
                        Say($"waits for {Slots(standing)}, {standing.Ahead} leg(s) ahead{whose}; held by {Holders(standing)}");
                        (heldBy, ownSaid) = (standing.Holders, own);
                    }
                    else if (Due())
                    {
                        Say($"still waits for {Slots(standing)}{After()}, {standing.Ahead} leg(s) ahead{whose}; held by {Holders(standing)}");
                    }

                    (waitingForMemory, settled) = (false, false);

                    // Never for a wait its own command's legs alone hold it to: they give their slots back as their work ends.
                    if (!own && Remaining() <= TimeSpan.Zero)
                    {
                        return Refuse(
                            place,
                            started,
                            null,
                            standing,
                            $"not admitted after {Counted()} waiting for {Slots(standing)}, held by {Holders(standing)}; "
                            + $"the machine's heavy legs are recorded in '{_slots.Location}'");
                    }

                    await PauseAsync(own).ConfigureAwait(false);
                    continue;
                }

                (heldBy, ownSaid) = (null, null);

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
                        await PauseAsync().ConfigureAwait(false);
                        continue;
                    }

                    // Read before in this wait and not now: nothing is decided on a reading this old - one above the limit
                    // would otherwise let the leg start the moment the count failed once - so it is read again next time.
                    if (!readingLost)
                    {
                        Say($"holds a heavy-leg slot, and could not read the memory in use again: {why}; it last read {last.Describe()}");
                        readingLost = true;
                    }
                    else if (Due())
                    {
                        Say($"holds a heavy-leg slot, and still could not read the memory in use again{After()}: {why}; it last read {last.Describe()}");
                    }

                    if (Remaining() <= TimeSpan.Zero)
                    {
                        return Refuse(
                            place,
                            started,
                            last,
                            null,
                            $"not admitted after {Counted()}: it held a heavy-leg slot, and the memory in use, last read as "
                            + $"{last.Describe()}, could not be read again: {why}",
                            why);
                    }

                    await PauseAsync().ConfigureAwait(false);
                    continue;
                }

                (last, readingLost) = (reading, false);

                if (dropped is { } pending)
                {
                    Say($"{pending.Drop.Said}, and {Waited(pending.At)} later the memory read {reading.Describe()}, from {pending.Before.Describe()}");
                    dropped = null;
                }

                // Decided on the share a line gives, so a leg is never let start at a reading its own line would show at
                // the limit.
                if (reading.Rounded < rule.MaxMemoryPercent)
                {
                    everBelow = true;

                    // Where another leg holds a slot, read again after a settle, and started only if still below: two legs
                    // taking their slots together would otherwise both start on one reading. A unit asking not to settle
                    // starts on this one.
                    if (settled || !request.Settle || rule.SettleMost <= TimeSpan.Zero || !standing.Holders.Any(holder => holder != place.Entry))
                    {
                        if (TakeWithRoom(new AdmissionFact(true, Seconds(started), reading.Rounded, reading.Describe(), Record: _slots.Location), reading) is { } taken)
                        {
                            return taken;
                        }

                        // Waited for room: another leg may have started meanwhile, so the memory is settled again before
                        // this one starts on a reading.
                        settled = false;
                        await PauseAsync().ConfigureAwait(false);
                        continue;
                    }

                    var settling = Waits.Shorter(_settle(rule.SettleLeast, rule.SettleMost), Remaining());

                    Say($"memory {reading.Describe()}; another leg holds a slot, so it looks again in {Said(settling)}");

                    // Waited whole - the memory is read again only once all of it has passed - in pieces no longer than a
                    // wait goes without saying where it stands.
                    var left = settling;

                    do
                    {
                        var piece = Waits.Shorter(left, SaidAgainEvery);

                        await _wait(piece, cancellationToken).ConfigureAwait(false);
                        left -= piece;

                        if (left > TimeSpan.Zero)
                        {
                            Say($"another leg holds a slot, so it still looks again in {Said(left)}{After()}");
                        }
                    }
                    while (left > TimeSpan.Zero);

                    settled = true;
                    continue;
                }

                settled = false;

                if (!waitingForMemory)
                {
                    Say($"holds a heavy-leg slot, and waits for the memory {reading.Describe()} to fall below {Limit(rule)}");
                    waitingForMemory = true;
                }
                else if (Due())
                {
                    Say($"holds a heavy-leg slot, and still waits for the memory {reading.Describe()} to fall below {Limit(rule)}{After()}");
                }

                if (Remaining() <= TimeSpan.Zero)
                {
                    return Refuse(
                        place,
                        started,
                        reading,
                        null,
                        everBelow
                            ? $"not admitted after {Counted()}: it held a heavy-leg slot, and the memory fell below {Limit(rule)} "
                                + $"only to rise above it again before the leg could start; it last read {reading.Describe()}"
                            : $"not admitted after {Counted()}: it held a heavy-leg slot, and the memory {reading.Describe()} "
                                + $"never fell below {Limit(rule)}");
                }

                // About to wait on the memory: what this machine counts as in use and could have back - WSL's page cache - is
                // dropped first, at most once a minute whichever leg asks, and the memory read again, in place of the poll,
                // once what the drop gives back has reached this machine's count.
                if (request.DropPageCache is { } dropPageCache && DropDue() && await dropPageCache(cancellationToken).ConfigureAwait(false) is { } drop)
                {
                    if (drop.Done)
                    {
                        dropped = (reading, drop, _clock.GetTimestamp());
                        await _wait(Pause(PageCacheDrop.HandedBackWithin, Remaining(), _clock.GetElapsedTime(saidAt)), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // Said once, however often it is tried: the same failure each minute would say nothing new.
                    if (drop.Said != notDropped)
                    {
                        Say(drop.Said);
                        notDropped = drop.Said;
                    }
                }

                await PauseAsync().ConfigureAwait(false);
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
                            ? $"its room is unread, so ~{DiskSpace.Size(room.Bytes)} is claimed against every filesystem here: {claim.Unmeasured}"
                            : Claimed(room, claim),
                    });
            }

            if (claim.Disk is null)
            {
                var read = lastRoom!.Describe(room);

                if (!roomLost)
                {
                    Say($"holds a heavy-leg slot, and could not read the room again: {claim.Unmeasured}; it last read: {read}");
                    roomLost = true;
                }
                else if (Due())
                {
                    Say($"holds a heavy-leg slot, and still could not read the room again{After()}: {claim.Unmeasured}; it last read: {read}");
                }

                return Remaining() > TimeSpan.Zero
                    ? null
                    : Refuse(
                        place,
                        started,
                        reading,
                        null,
                        $"not admitted after {Counted()}: it held a heavy-leg slot, and the room could not be read again: "
                            + $"{claim.Unmeasured}; it last read: {read}",
                        claimants: lastRoom.Claimants,
                        record: _slots.RoomLocation,
                        room: read);
            }

            (lastRoom, roomLost) = (claim, false);

            // Said again whenever the legs claiming it change, as the legs holding the slots are.
            if (roomSaid is null || !roomSaid.SequenceEqual(claim.Claimants))
            {
                Say($"holds a heavy-leg slot, and waits for room: {claim.Describe(room)}");
                roomSaid = claim.Claimants;
            }
            else if (Due())
            {
                Say($"holds a heavy-leg slot, and still waits for room{After()}: {claim.Describe(room)}");
            }

            return Remaining() > TimeSpan.Zero
                ? null
                : Refuse(
                    place,
                    started,
                    reading,
                    null,
                    $"not admitted after {Counted()}: it held a heavy-leg slot, and its build would not fit: "
                        + $"{claim.Describe(room)}; the room each heavy leg claims is recorded in '{_slots.RoomLocation}'",
                    claimants: claim.Claimants,
                    record: _slots.RoomLocation,
                    room: claim.Describe(room));
        }

        // Says where the wait stands, and when it did.
        void Say(string line)
        {
            request.Progress(line);
            saidAt = _clock.GetTimestamp();
        }

        // Whether the wait has gone SaidAgainEvery without saying where it stands.
        bool Due() => _clock.GetElapsedTime(saidAt) >= SaidAgainEvery;

        // Waits before the next look, as long as a wait that last said where it stands when this one did may.
        // A wait behind its own command's legs alone is not cut short by what is left of the time the leg may wait, which
        // that wait does not spend.
        Task PauseAsync(bool own = false) => _wait(Pause(rule.Poll, own ? rule.Poll : Remaining(), _clock.GetElapsedTime(saidAt)), cancellationToken);

        // What is left of the time the leg may wait: all of it but what it spent behind its own command's legs alone.
        TimeSpan Remaining() => rule.MaxWait - (_clock.GetElapsedTime(started) - excused);

        // How long the leg has waited, and what of it did not count, as a line says it.
        string Counted() => excused > TimeSpan.Zero
            ? $"{Waited(started)} ({Said(excused)} of it behind its own command's legs, which does not count)"
            : Waited(started);

        // How long the leg has waited, of the time it may: what a line said again adds.
        string After() => $", after {Counted()} of the {Said(rule.MaxWait)} it may wait";
    }

    /// <summary>
    /// Whether WSL's page cache may be dropped for a leg about to wait - none was, by this process, within
    /// <see cref="DropsAtMostEvery"/> - and, where it may, that it is now: a drop that gave nothing back, or could not be
    /// made, counts as one.
    /// </summary>
    private bool DropDue()
    {
        lock (_dropping)
        {
            var now = _clock.GetTimestamp();

            if (_droppedAt is { } at && _clock.GetElapsedTime(at, now) < DropsAtMostEvery)
            {
                return false;
            }

            _droppedAt = now;
            return true;
        }
    }

    /// <summary>
    /// How long a wait waits before it looks again: the machine's <paramref name="poll"/>, never past what is
    /// <paramref name="left"/> of the wait, and never past when the wait is next due to say where it stands - a poll of an
    /// hour would otherwise hold that line back for an hour. Nothing where that line is due already, as the time that
    /// passes between a look and its wait can make it: the leg looks again at once, and says so, rather than waiting a
    /// poll out in silence.
    /// </summary>
    /// <param name="poll">The machine's poll.</param>
    /// <param name="left">What is left of the time the leg may wait.</param>
    /// <param name="sinceSaid">How long ago the wait last said where it stands.</param>
    internal static TimeSpan Pause(TimeSpan poll, TimeSpan left, TimeSpan sinceSaid)
        => Waits.Shorter(Waits.Shorter(poll, left), SaidAgainEvery - sinceSaid);

    /// <summary>
    /// The room a leg claimed as it was let start, as its line says it: <c>its build needs ~31 GiB; 40 GiB free on '/'</c>,
    /// with what the other legs claim there where they claim any.
    /// </summary>
    private static string Claimed(RoomNeed room, RoomStanding claim)
        => claim.Disk!.Against(
            DiskSpace.Needs("its build", room.Bytes),
            room.Where,
            claim.Claimed > 0 ? $", beside ~{DiskSpace.Size(claim.Claimed)} other legs claim" : string.Empty);

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

    private double Seconds(long started) => LedgerReport.Seconds(_clock.GetElapsedTime(started));

    private string Waited(long started) => Said(_clock.GetElapsedTime(started));

    /// <summary>A time as a line says it, as the ledger's table does, and a time of none as <c>0s</c>.</summary>
    private static string Said(TimeSpan time) => LedgerReport.FormatDuration(time) is { Length: > 0 } said ? said : "0s";
}
