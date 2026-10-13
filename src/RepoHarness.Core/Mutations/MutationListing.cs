using System.Globalization;
using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Output;

namespace RepoHarness.Core.Mutations;

/// <summary>One arm's row of a listing on one leg: whether anything read here moved, and why.</summary>
public sealed record ListedArm
{
    /// <summary>The arm, as the registry names it.</summary>
    public required string Arm { get; init; }

    /// <summary>Whether it is pending; otherwise nothing read here moved, which is never to say it holds.</summary>
    public required bool Pending { get; init; }

    /// <summary>
    /// The verdict shown: the newest the record holds that nothing read here moved for, else the newest; <see langword="null"/>
    /// where the record holds none, or cannot be read.
    /// </summary>
    public RecordedVerdict? Last { get; init; }

    /// <summary>How many texts it cites, read here.</summary>
    public required int Texts { get; init; }

    /// <summary>How many sites it mutates, read here.</summary>
    public required int Sites { get; init; }

    /// <summary>How many files of the tree the tables of the verdict shown name, read here.</summary>
    public required int Files { get; init; }

    /// <summary>What of what it is judged on a listing does not read, as each part is named.</summary>
    public required IReadOnlyList<string> NotRead { get; init; }

    /// <summary>Each file of the tree its leg's build is generated from that moved since the verdict shown, as the record spells it.</summary>
    public IReadOnlyList<string> CausesMoved { get; init; } = [];

    /// <summary>Whether what the configuration gives the configure changed since the verdict shown.</summary>
    public bool GenerationMoved { get; init; }

    /// <summary>Every reason it is pending, the first that applies first; none where it is not.</summary>
    public IReadOnlyList<PendingReason> Why { get; init; } = [];
}

/// <summary>One leg of a listing: whether it can be swept, whether its record could be read, and its arms.</summary>
/// <param name="Leg">The leg.</param>
/// <param name="Unsweepable">Why it cannot be swept, as a sweep's refusal says it; <see langword="null"/> where it can be.</param>
/// <param name="RecordUnread">Why its record cannot be read; <see langword="null"/> where it was read, or is not there.</param>
/// <param name="Arms">Each arm its S row names, in the registry's order.</param>
public sealed record ListedLeg(string Leg, string? Unsweepable, RecordUnread? RecordUnread, IReadOnlyList<ListedArm> Arms)
{
    /// <summary>How many of its arms are pending.</summary>
    public int Pending => Arms.Count(arm => arm.Pending);

    /// <summary>How many of its arms nothing read here moved for.</summary>
    public int Unmoved => Arms.Count - Pending;
}

/// <summary>
/// What a listing reads of one leg, beside the registry: its arms, whether it can be swept, its record, the tree's one
/// reading, the host it names, the settings it would be swept with, and what its configure would be given.
/// </summary>
/// <param name="Leg">The leg.</param>
/// <param name="Arms">The arms selected that its S row names.</param>
/// <param name="Unsweepable">Why it cannot be swept, or <see langword="null"/>.</param>
/// <param name="Record">What the record holds of it.</param>
/// <param name="Table">Reads a table of its record by its digest.</param>
/// <param name="Tree">The one reading of the tree it acts on here.</param>
/// <param name="Host">The host it names, as a dispatcher names it; <see langword="null"/> where it names none.</param>
/// <param name="Settings">The settings it would be swept with, on the host it names or on this machine.</param>
/// <param name="Mutations">The mutation settings a sweep of it runs by: what each runner reads at run time among them.</param>
/// <param name="Generation">The digest of what its configure would be given (<see cref="JudgedCauses.GenerationOf"/>); <see langword="null"/> where it builds nothing.</param>
internal sealed record ListingInput(
    string Leg,
    IReadOnlyList<MutationArm> Arms,
    string? Unsweepable,
    LegVerdictRecord Record,
    Func<string, (BuiltFromTable? Table, string? Unread)> Table,
    TreeFiles Tree,
    string? Host,
    SweepSettings Settings,
    MutationSettings Mutations,
    string? Generation);

/// <summary>
/// The listing of <c>check-mutations --list</c>: for each arm on each leg, the verdict the record shows and whether anything
/// this machine reads moved since - the arm's rows and texts, the tree's files its tables name and the settings - by the
/// rules a sweep compares the same parts by. It never says an arm holds: a compiler, how a target is built, a file a build
/// makes and one outside the tree are read only where the leg is swept.
/// </summary>
internal static class MutationListing
{
    /// <summary>The parts of what an arm is judged on a listing never reads, as each is named.</summary>
    private static readonly string[] NeverRead = ["lines", "set", "made", "fetched", "outside", "compilers", "developerEnvironment"];

    /// <summary>The verdict a recorded entry holds where it passed, as the ledger names it.</summary>
    private static readonly string Passed = Verdicts.Display(LegVerdict.Passed);

    /// <summary>The closing note: what a listing read, and why that is never to say an arm holds.</summary>
    public const string Note =
        "'unmoved here' is not 'holds'. This machine read each arm's rows and texts, the tree's files and the settings. A "
        + "compiler, how a target is built, a file a build makes and a file outside the tree are read only where the leg is swept.";

    /// <summary>Lists each of <paramref name="input"/>'s arms.</summary>
    /// <param name="input">What was read of the leg.</param>
    public static ListedLeg Of(ListingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var notRead = input.Host is null ? [.. NeverRead, "host"] : NeverRead;

        return new ListedLeg(
            input.Leg,
            input.Unsweepable,
            input.Record.Unread,
            [.. input.Arms.Select(arm => Arm(input, arm, notRead))]);
    }

    private static ListedArm Arm(ListingInput input, MutationArm arm, IReadOnlyList<string> notRead)
    {
        var texts = JudgedOn.TextsOf(arm);
        var listed = new ListedArm
        {
            Arm = arm.Id,
            Pending = true,
            Texts = texts.Count,
            Sites = arm.Sites.Count,
            Files = 0,
            NotRead = notRead,
        };

        if (input.Record.Unread is { } unread)
        {
            return listed with { Why = [unread.Format is null ? PendingReason.RecordUnreadable(unread.Why!) : PendingReason.RecordNewer] };
        }

        List<PendingReason> before = input.Unsweepable is { } why ? [PendingReason.LegUnsweepable(why)] : [];
        var entries = input.Record.Arms.GetValueOrDefault(arm.Id) ?? [];

        if (entries.Count == 0)
        {
            return listed with { Why = [.. before, PendingReason.NeverSwept] };
        }

        var here = new ReadHere(
            ArmJudge.Revision,
            JudgedOn.RowsOf(arm),
            texts.ToDictionary(path => path, input.Tree.HashOf, StringComparer.Ordinal),
            arm.Sites.ToDictionary(site => site.Site, site => input.Tree.HashOf(site.Site), StringComparer.Ordinal),
            input.Tree,
            JudgedOn.RunReadsOf(arm, input.Mutations, input.Tree),
            input.Settings,
            input.Host);
        var read = entries.Reverse().Select(entry => Read(input, entry, here)).ToList();
        var shown = read.Find(entry => entry.Unmoved) ?? read[0];
        var causes = shown.Entry.Causes;

        listed = listed with
        {
            Last = shown.Entry,
            Files = shown.Files,
            CausesMoved = causes is null
                ? []
                : [.. causes.GeneratedFrom.Where(file => input.Tree.HashOf(file.Key) != file.Value).Select(file => file.Key).Order(StringComparer.Ordinal)],
            GenerationMoved = causes is not null && input.Generation is not null && causes.Generation != input.Generation,
        };

        if (shown.Unmoved)
        {
            List<PendingReason> held = [.. before, .. shown.Entry.Verdict == Passed ? [] : new[] { PendingReason.LastNotPassed(shown.Entry.Verdict) }];

            return listed with { Pending = held.Count > 0, Why = held };
        }

        return listed with { Why = [.. before, .. shown.Why] };
    }

    /// <summary>
    /// What a listing makes of one recorded verdict: every reason it is not one that holds as far as this machine reads -
    /// its tables unread, its verdict not a passed, its key never read, and what moved here - and whether nothing read here
    /// moved for its key.
    /// </summary>
    private static EntryRead Read(ListingInput input, RecordedVerdict entry, ReadHere here)
    {
        var why = new List<PendingReason>();
        var tables = new Dictionary<string, BuiltFromTable>(StringComparer.Ordinal);
        var built = entry.JudgedOn?.Built ?? new Dictionary<string, string>();

        foreach (var (target, digest) in built.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var (table, unread) = input.Table(digest);

            if (table is null)
            {
                why.Add(PendingReason.RecordUnreadable(unread!));
            }
            else
            {
                tables[target] = table;
            }
        }

        if (entry.Verdict != Passed)
        {
            why.Add(PendingReason.LastNotPassed(entry.Verdict));
        }

        if (entry.Key is null)
        {
            why.Add(PendingReason.NotKeyed(entry.KeyUnread ?? "nothing says why"));
        }

        var moved = entry.JudgedOn is { } on
            ? KeyDifference.Listed(on, tables, here)
            : [];
        var unmoved = entry.Key is not null && entry.JudgedOn is not null && tables.Count == entry.JudgedOn.Built.Count && moved.Count == 0;

        why.AddRange(moved);

        return new EntryRead(entry, unmoved, why, tables.Values.SelectMany(table => table.Files.Keys).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>What a listing made of one recorded verdict.</summary>
    /// <param name="Entry">The verdict.</param>
    /// <param name="Unmoved">Whether it has a key, and nothing read here moved for it.</param>
    /// <param name="Why">Every reason it is not one that holds, as far as this machine reads.</param>
    /// <param name="Files">How many files of the tree its tables name.</param>
    private sealed record EntryRead(RecordedVerdict Entry, bool Unmoved, IReadOnlyList<PendingReason> Why, int Files);

    /// <summary>
    /// The listing as a table, a row to each arm and leg - its last verdict, its run, when, and what it is now - with a
    /// line below to each leg counting its arms pending, grouped by the first reason each is pending for, and unmoved.
    /// </summary>
    /// <param name="legs">Each leg listed.</param>
    /// <param name="show">Tells each line as a line of the harness's own is told to whoever reads it.</param>
    public static IReadOnlyList<string> Table(IReadOnlyList<ListedLeg> legs, Func<string, string> show)
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(show);

        var rows = new List<string[]> { new[] { "ARM", "LEG", "LAST", "RUN", "WHEN", "NOW" } };

        foreach (var leg in legs)
        {
            rows.AddRange(leg.Arms.Select(arm => new[]
            {
                arm.Arm,
                leg.Leg,
                arm.Last?.Verdict ?? "-",
                arm.Last?.Run ?? "-",
                arm.Last is { } last ? last.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm'Z'", CultureInfo.InvariantCulture) : "-",
                Now(arm),
            }));
        }

        var widths = Enumerable.Range(0, 5).Select(column => rows.Max(row => row[column].Length)).ToArray();
        var lines = new List<string> { string.Empty };

        lines.AddRange(rows.Select(row => string.Join("  ", row.Take(5).Select((cell, column) => cell.PadRight(widths[column]))) + "  " + row[5]));
        lines.Add(string.Empty);
        lines.AddRange(legs.Select(Counted));

        return [.. lines.Select(show)];
    }

    /// <summary>The listing as one document, for <c>--json</c>.</summary>
    /// <param name="legs">Each leg listed.</param>
    /// <param name="registry">The registry, as the configuration names it.</param>
    /// <param name="record">The record's directory.</param>
    /// <param name="show">Tells a path, or a line of the harness's own, as it is told to whoever reads the document.</param>
    public static string Json(IReadOnlyList<ListedLeg> legs, string registry, string record, Func<string, string> show)
    {
        ArgumentNullException.ThrowIfNull(legs);
        ArgumentNullException.ThrowIfNull(show);

        return JsonSerializer.Serialize(
            new
            {
                ExitCode = Results.HarnessExit.Success,
                Summary = Summary(legs),
                Registry = registry,
                MutationRecord = show(record),
                Legs = legs.Select(leg => new
                {
                    leg.Leg,
                    Sweepable = leg.Unsweepable is null,
                    Unsweepable = leg.Unsweepable is { } why ? show(why) : null,
                    RecordUnread = leg.RecordUnread is { } unread ? show(unread.Said) : null,
                    leg.Pending,
                    leg.Unmoved,
                    Arms = leg.Arms.Select(arm => new
                    {
                        arm.Arm,
                        State = arm.Pending ? "pending" : "unmoved",
                        Verified = false,
                        Last = arm.Last is { } last
                            ? new
                            {
                                last.Verdict,
                                Detail = show(last.Detail),
                                last.Run,
                                last.At,
                                last.SweptIn,
                                last.JudgedOn?.Host,
                                last.Key,
                                VerifiedBy = last.Verified,
                            }
                            : null,
                        Read = new { arm.Texts, arm.Sites, arm.Files },
                        arm.NotRead,
                        arm.CausesMoved,
                        arm.GenerationMoved,
                        Why = arm.Why.Select(reason => new
                        {
                            reason.Code,
                            Said = show(reason.Said),
                            reason.Target,
                            Paths = reason.Paths.Count > 0 ? reason.Paths : null,
                        }),
                    }),
                }),
            },
            ReportJson.Options);
    }

    /// <summary>What the listing found, in one line: how many arms on how many legs, pending and unmoved.</summary>
    /// <param name="legs">Each leg listed.</param>
    public static string Summary(IReadOnlyList<ListedLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);

        return $"{ReportText.Counted(legs.Sum(leg => leg.Arms.Count), "arm")} on {ReportText.Counted(legs.Count, "leg")}: "
            + $"{legs.Sum(leg => leg.Pending).ToString(CultureInfo.InvariantCulture)} pending, "
            + $"{legs.Sum(leg => leg.Unmoved).ToString(CultureInfo.InvariantCulture)} unmoved here";
    }

    /// <summary>What an arm is now, as its row says it.</summary>
    private static string Now(ListedArm arm)
    {
        if (arm.Pending)
        {
            return $"pending: {arm.Why[0].Row}";
        }

        var moved = new List<string>();

        if (arm.CausesMoved.Count > 0)
        {
            moved.Add($"{ReportText.Counted(arm.CausesMoved.Count, "file")} its build is generated from moved ({ReportText.Listed(arm.CausesMoved)})");
        }

        if (arm.GenerationMoved)
        {
            moved.Add("what the configuration gives its configure changed");
        }

        return moved.Count > 0
            ? $"unmoved here, though {string.Join(", and ", moved)}"
            : $"unmoved here: its rows, {ReportText.Counted(arm.Texts, "text")}, {ReportText.Counted(arm.Sites, "site")} and "
                + $"{ReportText.Counted(arm.Files, "file")} of the tree are as recorded";
    }

    /// <summary>A leg's line below the table: its arms, those pending grouped by the first reason each is pending for, and those unmoved.</summary>
    private static string Counted(ListedLeg leg)
    {
        var parts = new List<string>();

        if (leg.Pending > 0)
        {
            var groups = leg.Arms
                .Where(arm => arm.Pending)
                .GroupBy(arm => arm.Why[0].Counted, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Count().ToString(CultureInfo.InvariantCulture)} {group.Key}");

            parts.Add($"{leg.Pending.ToString(CultureInfo.InvariantCulture)} pending ({string.Join(", ", groups)})");
        }

        if (leg.Unmoved > 0)
        {
            parts.Add($"{leg.Unmoved.ToString(CultureInfo.InvariantCulture)} unmoved here");
        }

        return $"{leg.Leg}: {ReportText.Counted(leg.Arms.Count, "arm")}" + (parts.Count > 0 ? $": {string.Join("; ", parts)}" : string.Empty);
    }
}
