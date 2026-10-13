using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// What the record of verdicts holds of the arms one leg's sweep under <c>--pending</c> weighs: every entry of each, and
/// each table the newest of them names - or why such a table, or the whole record, cannot be read. A sweep holds an arm
/// by this alone, and says why each other is pending (<see cref="ArmHolding"/>).
/// </summary>
public sealed record RecordedArms
{
    /// <summary>Why the leg's record cannot be read, or <see langword="null"/> where it was read, or is not there.</summary>
    public RecordUnread? Unread { get; init; }

    /// <summary>Each arm's entries, by its id, compared ignoring case: one to a key, in the order recorded, the newest last.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RecordedVerdict>> Arms { get; init; } =
        new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Each table the newest entry of an arm names, by its digest.</summary>
    public IReadOnlyDictionary<string, BuiltFromTable> Tables { get; init; } = new Dictionary<string, BuiltFromTable>(StringComparer.Ordinal);

    /// <summary>Why each table the newest entry of an arm names that could not be read cannot be, by its digest.</summary>
    public IReadOnlyDictionary<string, string> TablesUnread { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>A record that cannot be read, for <paramref name="unread"/>: every arm of its leg is pending for it.</summary>
    /// <param name="unread">Why.</param>
    public static RecordedArms Unreadable(RecordUnread unread)
    {
        ArgumentNullException.ThrowIfNull(unread);

        return new RecordedArms { Unread = unread };
    }

    /// <summary>
    /// What a dispatch gives the host that sweeps this leg under <c>--pending</c>: each arm's newest entry and every
    /// recorded <c>passed</c> of it, with the tables they name, as bytes that cross as their base64 text. The host decides
    /// held and pending by the one rule, from its own reading and its own worker, so it needs no more than that - and no
    /// less: a pending arm's why is named from the parts and tables of the entry it is read against.
    /// </summary>
    public byte[] Carried()
    {
        var arms = new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (arm, entries) in Arms)
        {
            // The newest entry, and every recorded passed however old: what a sweep reads an arm by, and all it holds it by.
            var kept = entries.Where(entry => string.Equals(entry.Verdict, Passed, StringComparison.Ordinal) || ReferenceEquals(entry, entries[^1])).Distinct().ToList();

            arms[arm] = kept;
        }

        var named = arms.Values.SelectMany(entries => entries).SelectMany(entry => entry.JudgedOn?.Built.Values ?? []).ToHashSet(StringComparer.Ordinal);

        return JsonSerializer.SerializeToUtf8Bytes(
            this with
            {
                Arms = arms,
                Tables = new Dictionary<string, BuiltFromTable>(Tables.Where(pair => named.Contains(pair.Key)), StringComparer.Ordinal),
                TablesUnread = new Dictionary<string, string>(TablesUnread.Where(pair => named.Contains(pair.Key)), StringComparer.Ordinal),
            },
            JsonStateFile.LineOptions);
    }

    /// <summary>What <paramref name="carried"/> holds, as a host reads what its dispatch gave it.</summary>
    /// <param name="carried">The bytes the dispatch carried as the host's <c>--held</c>.</param>
    public static RecordedArms OfCarried(ReadOnlySpan<byte> carried)
        => carried.IsEmpty
            ? new RecordedArms()
            : JsonSerializer.Deserialize<RecordedArms>(carried, JsonStateFile.LineOptions) ?? new RecordedArms();

    /// <summary>The verdict a recorded entry holds where it passed, as the ledger names it.</summary>
    private static readonly string Passed = Execution.Verdicts.Display(Execution.LegVerdict.Passed);
}

/// <summary>What a sweep under <c>--pending</c> makes of one arm: held - its key a recorded <c>passed</c>'s - or pending, and why.</summary>
internal sealed class ArmStanding
{
    private ArmStanding(RecordedVerdict? held, IReadOnlyList<PendingReason> why)
    {
        Held = held;
        Why = why;
    }

    /// <summary>The recorded <c>passed</c> whose key is the arm's now, or <see langword="null"/> where it is pending.</summary>
    public RecordedVerdict? Held { get; }

    /// <summary>Every reason it is pending, the first that applies first; none where it is held.</summary>
    public IReadOnlyList<PendingReason> Why { get; }

    /// <summary>Held by <paramref name="passed"/>.</summary>
    public static ArmStanding HeldBy(RecordedVerdict passed) => new(passed, []);

    /// <summary>Pending, for <paramref name="why"/>: at least one reason.</summary>
    public static ArmStanding PendingFor(IReadOnlyList<PendingReason> why)
    {
        ArgumentNullException.ThrowIfNull(why);
        ArgumentOutOfRangeException.ThrowIfZero(why.Count, nameof(why));

        return new(null, why);
    }

    /// <summary>
    /// The detail of a held arm's line: <c>--pending left it out: its passed of run 20261012-145059-ba433418
    /// (2026-10-12 14:58Z) holds</c>.
    /// </summary>
    public string Said => Held is { } passed
        ? $"--pending left it out: its passed of {RunLine.Message(passed.Run)} ({passed.When}) holds"
        : throw new InvalidOperationException("A pending arm is said by why it is pending.");
}

/// <summary>
/// The one rule a sweep under <c>--pending</c> holds an arm by, on the machine that sweeps its leg: held where the key
/// of what it would be judged on now, read in full from that machine's first worker, is the key of a recorded
/// <c>passed</c> - and pending otherwise, saying why as a listing says it, by the same comparisons.
/// </summary>
internal static class ArmHolding
{
    /// <summary>The verdict a recorded entry holds where it passed, as the ledger names it.</summary>
    private static readonly string Passed = Execution.Verdicts.Display(Execution.LegVerdict.Passed);

    /// <summary>Whether <paramref name="arm"/> holds a recorded <c>passed</c> now, and if not why.</summary>
    /// <param name="recorded">What the record holds of the leg's arms.</param>
    /// <param name="arm">The arm, as the registry names it.</param>
    /// <param name="now">What it would be judged on now, as the first worker's build read it.</param>
    /// <param name="names">How the leg's tree compares names, as its file system does.</param>
    public static ArmStanding Of(RecordedArms recorded, string arm, ArmKey now, StringComparer? names = null)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(now);

        if (recorded.Unread is { } unread)
        {
            return ArmStanding.PendingFor([unread.Format is null ? PendingReason.RecordUnreadable(unread.Why!) : PendingReason.RecordNewer]);
        }

        if (recorded.Arms.GetValueOrDefault(arm) is not { Count: > 0 } entries)
        {
            return ArmStanding.PendingFor([PendingReason.NeverSwept]);
        }

        bool Same(RecordedVerdict entry) => now.Key is not null && string.Equals(entry.Key, now.Key, StringComparison.Ordinal);

        if (entries.FirstOrDefault(entry => Same(entry) && entry.Verdict == Passed) is { } passed)
        {
            return ArmStanding.HeldBy(passed);
        }

        // As a listing shows one: the newest on the key read now, which is then no passed, else the newest.
        var shown = entries.LastOrDefault(Same) ?? entries[^1];
        var why = new List<PendingReason>();
        var tables = new Dictionary<string, BuiltFromTable>(StringComparer.Ordinal);

        if (!Same(shown))
        {
            foreach (var (target, digest) in (shown.JudgedOn?.Built ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (recorded.Tables.GetValueOrDefault(digest) is { } table)
                {
                    tables[target] = table;
                }
                else
                {
                    why.Add(PendingReason.RecordUnreadable(recorded.TablesUnread.GetValueOrDefault(digest) ?? $"the table of '{target}' it names, {digest}, was not given"));
                }
            }
        }

        if (shown.Verdict != Passed)
        {
            why.Add(PendingReason.LastNotPassed(shown.Verdict));
        }

        if (shown.Key is null)
        {
            why.Add(PendingReason.NotKeyed(shown.KeyUnread ?? "nothing says why"));
        }

        if (Same(shown))
        {
            return ArmStanding.PendingFor(why);
        }

        if (shown.JudgedOn is { } on)
        {
            why.AddRange(KeyDifference.Between(on, tables, now, names));
        }
        else if (now.Unread is { } unreadNow)
        {
            why.Add(PendingReason.KeyUnreadable(unreadNow));
        }

        return ArmStanding.PendingFor(why);
    }
}
