using System.Globalization;
using DssHarness.Core.Output;

namespace DssHarness.Core.Mutations;

/// <summary>Which of the three texts a merge is given.</summary>
public enum MergeSide
{
    /// <summary>The text both sides last held.</summary>
    Base,

    /// <summary>The text whose lines the merged text keeps.</summary>
    Main,

    /// <summary>The text whose changes are brought into it.</summary>
    Agent,
}

/// <summary>What a line calls each of the three texts: the caller's words, so a command names them one way throughout.</summary>
/// <param name="Base">The text both sides last held: <c>what both held</c>.</param>
/// <param name="Main">The text whose lines are kept: <c>the main tree</c>.</param>
/// <param name="Agent">The text whose changes are brought in: <c>the agent</c>.</param>
public sealed record MergeSides(string Base, string Main, string Agent)
{
    /// <summary>What <paramref name="side"/> is called.</summary>
    /// <param name="side">One of the three.</param>
    public string Of(MergeSide side) => side switch
    {
        MergeSide.Base => Base,
        MergeSide.Main => Main,
        _ => Agent,
    };
}

/// <summary>The arms one side contributed since the text both held: those it alone added, removed or changed.</summary>
/// <param name="Added">The arms it added.</param>
/// <param name="Removed">The arms it removed.</param>
/// <param name="Changed">The arms it changed.</param>
public sealed record MergeCounts(int Added, int Removed, int Changed)
{
    /// <summary>How many arms the side contributed, however.</summary>
    public int All => Added + Removed + Changed;

    /// <summary>How many arms the side contributed and how, as a line says it: <c>68 from the main tree (66 added, 2 changed)</c>.</summary>
    internal string From(string side)
    {
        string[] how = [.. new[] { (Added, "added"), (Changed, "changed"), (Removed, "removed") }
            .Where(part => part.Item1 > 0)
            .Select(part => string.Create(CultureInfo.InvariantCulture, $"{part.Item1} {part.Item2}"))];

        return string.Create(CultureInfo.InvariantCulture, $"{All} from {side}") + (how.Length == 0 ? string.Empty : $" ({string.Join(", ", how)})");
    }
}

/// <summary>Why one of the three texts could not be read as a registry: the text as a whole, or one line of it.</summary>
public sealed record UnreadVersion
{
    private UnreadVersion(MergeSide side, int? line, string why)
    {
        Side = side;
        Line = line;
        Why = why;
    }

    /// <summary>Which of the three.</summary>
    public MergeSide Side { get; }

    /// <summary>The line, or <see langword="null"/> where it is the text as a whole.</summary>
    public int? Line { get; }

    /// <summary>
    /// What is wrong: of the text as a whole, as what follows its name - <c>is not UTF-8: ...</c>; of a line, as what follows
    /// the line - <c>a row names no arm</c>.
    /// </summary>
    public string Why { get; }

    /// <summary>The text of <paramref name="side"/> as a whole cannot be read, for <paramref name="why"/>, said as what follows its name.</summary>
    /// <param name="side">Which of the three.</param>
    /// <param name="why">What is wrong: <c>is not UTF-8: ...</c>.</param>
    public static UnreadVersion OfText(MergeSide side, string why) => new(side, null, why);

    /// <summary>Line <paramref name="line"/> of the text of <paramref name="side"/> cannot be read, for <paramref name="why"/>, said as what follows the line.</summary>
    /// <param name="side">Which of the three.</param>
    /// <param name="line">The line, from 1.</param>
    /// <param name="why">What is wrong: <c>a row names no arm</c>.</param>
    public static UnreadVersion OfLine(MergeSide side, int line, string why) => new(side, line, why);

    /// <summary>
    /// The problem as a line says it, the text called what the caller calls it: <c>the text of the agent is not UTF-8:
    /// ...</c>, <c>line 14 of the text of the main tree: a row names no arm</c>.
    /// </summary>
    /// <param name="sides">What the three are called.</param>
    public string Said(MergeSides sides)
    {
        ArgumentNullException.ThrowIfNull(sides);

        return Line is { } line
            ? string.Create(CultureInfo.InvariantCulture, $"line {line} of the text of {sides.Of(Side)}: {Why}")
            : $"the text of {sides.Of(Side)} {Why}";
    }
}

/// <summary>One side's version of an arm, or of the lines above the first row or below the last.</summary>
/// <param name="Id">The arm's id, as that side spells it; what the lines are, where they are no arm's.</param>
/// <param name="Lines">Each line of it, with the number it has in that side's text.</param>
public sealed record ArmVersion(string Id, IReadOnlyList<(int Line, string Text)> Lines);

/// <summary>The ways two sides disagree over an arm, each with the words a line says it in (<see cref="ArmConflict.Said"/>).</summary>
public enum ArmConflictKind
{
    /// <summary>Neither held it before, and each added it its own way.</summary>
    AddedDifferently,

    /// <summary>Neither held it before, and each added it under an id that differs from the other's only in the case of its letters.</summary>
    SpelledApart,

    /// <summary>Both held it, and each changed it its own way.</summary>
    ChangedDifferently,

    /// <summary>The main tree removed it, and the agent changed it.</summary>
    RemovedByMain,

    /// <summary>The agent removed it, and the main tree changed it.</summary>
    RemovedByAgent,

    /// <summary>
    /// The two do not disagree over the arm itself, and merged it would not read back as it was merged: it would stand
    /// first, and the lines kept above it down to a blank line would then be read as the lines above the first row.
    /// </summary>
    WouldOpenTheText,

    /// <summary>
    /// The two do not disagree over a section's heading, and merged no row would stand under it - every arm under it
    /// removed by one side or the other: it would then be read as part of what stands below it, and the merge would not
    /// read back as it was merged.
    /// </summary>
    HeadsNoArm,
}

/// <summary>What of a registry a conflict is over.</summary>
public enum RegistryPart
{
    /// <summary>An arm.</summary>
    Arm,

    /// <summary>The lines above the first row, down to their last blank line.</summary>
    AboveTheFirstRow,

    /// <summary>The comment and blank lines below the last row.</summary>
    BelowTheLastRow,

    /// <summary>
    /// A section's heading: the comments a blank line parts from the row below them, below the first row, named for the
    /// arm whose row they stand above.
    /// </summary>
    Heading,
}

/// <summary>An arm, or lines that are no arm's, the two sides did not leave mergeable: what they disagree over, how, and each side's version.</summary>
/// <remarks>
/// Made by its four factories and nothing else, one to each part of a registry a conflict can be over, so what it is
/// over, what it is called and how the two disagree always agree.
/// </remarks>
public sealed record ArmConflict
{
    /// <summary>What the lines above the first row are called, in a conflict over them.</summary>
    public const string AboveTheFirstRow = "the lines above the first row";

    /// <summary>What the lines below the last row are called, in a conflict over them.</summary>
    public const string BelowTheLastRow = "the lines below the last row";

    private ArmConflict(RegistryPart part, string arm, ArmConflictKind kind, ArmVersion? @base, ArmVersion? main, ArmVersion? agent)
    {
        Part = part;
        Arm = arm;
        Kind = kind;
        Base = @base;
        Main = main;
        Agent = agent;
    }

    /// <summary>What the conflict is over.</summary>
    public RegistryPart Part { get; }

    /// <summary>
    /// The arm's id, as the main tree spells it where it holds the arm, else as the agent does, else as both did; the
    /// arm whose row a heading stands above, where the conflict is over a heading; what the lines are,
    /// <see cref="AboveTheFirstRow"/> or <see cref="BelowTheLastRow"/>, where it is over those.
    /// </summary>
    public string Arm { get; }

    /// <summary>How the two disagree.</summary>
    public ArmConflictKind Kind { get; }

    /// <summary>What both held of it, or <see langword="null"/> where they held none.</summary>
    public ArmVersion? Base { get; }

    /// <summary>The main tree's version, or <see langword="null"/> where it has none.</summary>
    public ArmVersion? Main { get; }

    /// <summary>The agent's version, or <see langword="null"/> where it has none.</summary>
    public ArmVersion? Agent { get; }

    /// <summary>A conflict over the arm <paramref name="arm"/>.</summary>
    /// <param name="arm">The arm's id, as the main tree spells it where it holds the arm, else as the agent does, else as both did.</param>
    /// <param name="kind">How the two disagree.</param>
    /// <param name="base">What both held of it, or <see langword="null"/> where they held none.</param>
    /// <param name="main">The main tree's version, or <see langword="null"/> where it has none.</param>
    /// <param name="agent">The agent's version, or <see langword="null"/> where it has none.</param>
    /// <exception cref="ArgumentException">The two cannot disagree over an arm as <paramref name="kind"/> says: an arm is no heading.</exception>
    public static ArmConflict OverArm(string arm, ArmConflictKind kind, ArmVersion? @base, ArmVersion? main, ArmVersion? agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);

        return kind is ArmConflictKind.HeadsNoArm
            ? throw new ArgumentException($"arm '{arm}' cannot conflict as {kind}.", nameof(kind))
            : new(RegistryPart.Arm, arm, kind, @base, main, agent);
    }

    /// <summary>
    /// A conflict over the heading standing above the row of arm <paramref name="arm"/>: never under ids that differ in
    /// case, which a heading has none of, and never as lines that would stand first, which a heading never does - the
    /// arm under it would, and the conflict is then that arm's.
    /// </summary>
    /// <param name="arm">The arm whose row the heading stands above: in the main tree where it holds the heading, else in the agent's text, else in what both held.</param>
    /// <param name="kind">How the two disagree.</param>
    /// <param name="base">What both held of it, or <see langword="null"/> where they held none.</param>
    /// <param name="main">The main tree's, or <see langword="null"/> where it has none.</param>
    /// <param name="agent">The agent's, or <see langword="null"/> where it has none.</param>
    /// <exception cref="ArgumentException">The two cannot disagree over a heading as <paramref name="kind"/> says.</exception>
    public static ArmConflict OverTheHeading(string arm, ArmConflictKind kind, ArmVersion? @base, ArmVersion? main, ArmVersion? agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arm);

        return kind is ArmConflictKind.SpelledApart or ArmConflictKind.WouldOpenTheText
            ? throw new ArgumentException($"the heading above arm '{arm}' cannot conflict as {kind}.", nameof(kind))
            : new(RegistryPart.Heading, arm, kind, @base, main, agent);
    }

    /// <summary>
    /// A conflict over the lines above the first row: never under ids that differ in case, which they have none of,
    /// never as lines that would stand first, which they always do, and never as a heading under which no row stands.
    /// </summary>
    /// <param name="kind">How the two disagree.</param>
    /// <param name="base">What both held of them, or <see langword="null"/> where they held none.</param>
    /// <param name="main">The main tree's, or <see langword="null"/> where it has none.</param>
    /// <param name="agent">The agent's, or <see langword="null"/> where it has none.</param>
    /// <exception cref="ArgumentException">The two cannot disagree over these lines as <paramref name="kind"/> says.</exception>
    public static ArmConflict OverTheLinesAbove(ArmConflictKind kind, ArmVersion? @base, ArmVersion? main, ArmVersion? agent)
        => kind is ArmConflictKind.SpelledApart or ArmConflictKind.WouldOpenTheText or ArmConflictKind.HeadsNoArm
            ? throw new ArgumentException($"{AboveTheFirstRow} cannot conflict as {kind}.", nameof(kind))
            : new(RegistryPart.AboveTheFirstRow, AboveTheFirstRow, kind, @base, main, agent);

    /// <summary>
    /// A conflict over the lines below the last row: never under ids that differ in case, which they have none of, and
    /// never as a heading under which no row stands.
    /// </summary>
    /// <param name="kind">How the two disagree.</param>
    /// <param name="base">What both held of them, or <see langword="null"/> where they held none.</param>
    /// <param name="main">The main tree's, or <see langword="null"/> where it has none.</param>
    /// <param name="agent">The agent's, or <see langword="null"/> where it has none.</param>
    /// <exception cref="ArgumentException">The two cannot disagree over these lines as <paramref name="kind"/> says.</exception>
    public static ArmConflict OverTheLinesBelow(ArmConflictKind kind, ArmVersion? @base, ArmVersion? main, ArmVersion? agent)
        => kind is ArmConflictKind.SpelledApart or ArmConflictKind.HeadsNoArm
            ? throw new ArgumentException($"{BelowTheLastRow} cannot conflict as {kind}.", nameof(kind))
            : new(RegistryPart.BelowTheLastRow, BelowTheLastRow, kind, @base, main, agent);

    /// <summary>
    /// The conflict as lines say it: what it is over and how the two disagree, then, beneath it, each side's lines that
    /// the versions do not all hold - every line of each, where they hold the same lines otherwise arranged - each with
    /// what the caller calls that side and the line's number there.
    /// </summary>
    /// <param name="sides">What the three are called.</param>
    public IReadOnlyList<string> Said(MergeSides sides)
    {
        ArgumentNullException.ThrowIfNull(sides);

        (string Side, ArmVersion Version)[] versions = [.. new[] { (sides.Base, Base), (sides.Main, Main), (sides.Agent, Agent) }
            .Where(held => held.Item2 is not null)
            .Select(held => (held.Item1, held.Item2!))];
        var shared = versions.Length < 2
            ? []
            : versions.Skip(1).Aggregate(
                new HashSet<string>(versions[0].Version.Lines.Select(line => line.Text), StringComparer.Ordinal),
                (all, held) =>
                {
                    all.IntersectWith(held.Version.Lines.Select(line => line.Text));
                    return all;
                });

        if (versions.All(held => held.Version.Lines.All(line => shared.Contains(line.Text))))
        {
            shared.Clear();
        }

        (string Label, string Text)[] shown = [.. versions.SelectMany(held => held.Version.Lines
            .Where(line => !shared.Contains(line.Text))
            .Select(line => (string.Create(CultureInfo.InvariantCulture, $"{held.Side}, line {line.Line}:"), line.Text)))];
        var width = shown.Length == 0 ? 0 : shown.Max(line => line.Label.Length);

        return
        [
            $"{Named}: {How(sides)}",
            .. shown.Select(line => $"  {line.Label.PadRight(width)}  {ReportText.Printable(line.Text)}".TrimEnd()),
        ];
    }

    private string Named => Part switch
    {
        RegistryPart.AboveTheFirstRow => AboveTheFirstRow,
        RegistryPart.BelowTheLastRow => BelowTheLastRow,
        RegistryPart.Heading => $"the heading above arm '{Arm}'",
        _ => $"arm '{Arm}'",
    };

    private string How(MergeSides sides) => Kind switch
    {
        ArmConflictKind.AddedDifferently => "added by both, each differently",
        ArmConflictKind.SpelledApart => "added by both, under ids that differ only in the case of their letters",
        ArmConflictKind.ChangedDifferently => "changed by both, each differently",
        ArmConflictKind.RemovedByMain => $"removed by {sides.Main}, changed by {sides.Agent}",
        ArmConflictKind.RemovedByAgent => $"removed by {sides.Agent}, changed by {sides.Main}",
        ArmConflictKind.HeadsNoArm => "no row would stand under it, and it would then be read as part of what stands below it",
        _ when Part == RegistryPart.Arm => "it would stand first, and the lines kept above it down to a blank line would then be read as " + AboveTheFirstRow,
        _ => "no row would be left above them, and those down to a blank line would then be read as " + AboveTheFirstRow,
    };
}

/// <summary>What merging a registry by arm came to.</summary>
public abstract record RegistryMerge
{
    private RegistryMerge()
    {
    }

    /// <summary>The two merged: no arm was changed differently by both.</summary>
    /// <param name="Bytes">The merged text, as it is to be written.</param>
    /// <param name="Text">The same text, decoded: what a validation of it reads.</param>
    /// <param name="Main">The arms the main tree contributed since the text both held.</param>
    /// <param name="Agent">The arms the agent contributed since then.</param>
    /// <param name="Same">The arms both added, removed or changed alike.</param>
    /// <param name="Arms">How many arms the merged text holds.</param>
    public sealed record Merged(byte[] Bytes, string Text, MergeCounts Main, MergeCounts Agent, int Same, int Arms) : RegistryMerge
    {
        /// <summary>
        /// The merge as a line says it: <c>merged by arm: 30 from the agent (30 added), 68 from the main tree (66 added,
        /// 2 changed); 307 arm(s) in all</c>.
        /// </summary>
        /// <param name="sides">What the three are called.</param>
        public string Said(MergeSides sides)
        {
            ArgumentNullException.ThrowIfNull(sides);

            var alike = Same == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", {Same} alike in both");

            return $"merged by arm: {Agent.From(sides.Agent)}, {Main.From(sides.Main)}{alike}; {ReportText.Counted(Arms, "arm")} in all";
        }
    }

    /// <summary>The two cannot be merged: nothing is offered, and each conflict says what a person has to reconcile.</summary>
    /// <param name="Conflicts">
    /// Every conflict: the lines above the first row, then each heading, then each arm, then the lines below the last row;
    /// or the one conflict of a merge that would not read back as it was merged.
    /// </param>
    /// <param name="Main">The arms the main tree contributed that are in no conflict.</param>
    /// <param name="Agent">The arms the agent contributed that are in no conflict.</param>
    public sealed record Conflicted(IReadOnlyList<ArmConflict> Conflicts, MergeCounts Main, MergeCounts Agent) : RegistryMerge;

    /// <summary>One of the three is not UTF-8, or holds a row that names no arm: nothing is offered.</summary>
    /// <param name="Versions">Each problem: which of the three, where, and why.</param>
    public sealed record Unreadable(IReadOnlyList<UnreadVersion> Versions) : RegistryMerge
    {
        /// <summary>Each problem as a line says it, the three called what the caller calls them.</summary>
        /// <param name="sides">What the three are called.</param>
        public IReadOnlyList<string> Said(MergeSides sides) => [.. Versions.Select(version => version.Said(sides))];
    }
}

/// <summary>
/// Merges an arms registry by arm: what each of two sides did to each arm since the text both last held, never line
/// against line. Pure: it reads no file, no clock and no configuration.
/// </summary>
/// <remarks>
/// <para>
/// A text is read as the lines above its first row down to their last blank line; its segments, each one row with the
/// comment and blank lines directly above it; and the comment and blank lines below its last row. The comments a blank
/// line parts from the row below them are a heading - a section's header - and never that row's arm's: an arm is the
/// same whatever heading stands above it, so arms added before a section's first arm leave that arm as it was - while
/// comments abutting their row are the arm's own, and go where it goes. An arm is the segments naming it, in the order
/// they stand, ids compared ignoring case. Two versions of an arm are the same where their segments are the same in
/// number and order and each holds the same lines of the arm's own: the blank lines and the heading above them only part
/// it from what stands above it, so an arm removed or added beside another changes nothing of that one. How a line ends
/// is no part of it either, so a checkout with other line endings changes nothing.
/// </para>
/// <para>
/// Each arm is taken from the side that alone added, removed or changed it, and is a conflict where both did so
/// differently. The lines above the first row, those below the last, and each heading are each weighed the same way, as
/// one: a heading as the heading above an arm's segment in its place among the arm's segments - where a merge lays one
/// side's segments of an arm over the other's - so a heading one side renamed or removed is that side's change, and one
/// a side moved to another arm's row is removed above the one and added above the other.
/// </para>
/// <para>
/// The main tree's text is kept line for line and changed only where the agent contributed. Each segment is written with
/// the heading taken for its place, and with the blank lines that part it from what stands above it where none is - one
/// of them, where a heading stood there. An arm taken from the agent in place of the main tree's lies over the main
/// tree's segments of it, one for one: where the agent has more, the rest follow the last of the main tree's, and where
/// it has fewer, the main tree's remaining places go. An arm only the agent has stands after the nearest segment above it
/// in the agent's text that the result holds, and after what the main tree alone holds there - so two sides adding at one
/// place come out as the main tree's addition, then the agent's. A heading only the agent added above a row that the
/// result already stands under a heading of the same lines - the nearest written above it, with only rows between, one
/// the main tree wrote where it stands, never one both held there - is that heading, whether both held it elsewhere
/// before or both renamed or added it alike, each above a new first arm of its own: it is not written again. Any other
/// stands where the agent stood it: above a row the result stands anywhere else, though the main tree added one of the
/// same lines elsewhere; under a heading of the same lines the agent wrote or both held; and wherever the agent's own text
/// holds a heading of the same lines nearest above it - each a heading the agent's text holds twice, whatever the main
/// tree did with the first. A heading taken for a place no segment of the result stands in is a conflict of its own
/// (<see cref="ArmConflictKind.HeadsNoArm"/>). The segment that comes to stand first is written without the blank lines
/// that open it. Each of the main tree's lines is written as the main tree holds it, each of the agent's with the ending
/// of the main tree's first line, behind a byte order mark where the main tree's text has one and ending with a line
/// ending where it does.
/// </para>
/// <para>
/// So a text merged with itself is itself, and a merge in which the agent changed nothing is the main tree's text,
/// each byte for byte; one in which the main tree changed nothing holds the agent's arms, in the main tree's order,
/// with its line endings and its byte order mark. And a merge run again with its own result on either side gives that
/// result, byte for byte, because a merge is only offered where its text reads back as it was merged
/// (<see cref="ArmConflictKind.WouldOpenTheText"/>, <see cref="ArmConflictKind.HeadsNoArm"/>), each heading of the
/// result stands in the place its side stood it, and no text is read in which a line opens with a byte order mark
/// (<see cref="RegistryText.TryRead"/>), which would read as the text's own once it stood first.
/// </para>
/// </remarks>
public static class MutationRegistryMerge
{
    /// <summary>Why a text holding a row with no second field is unreadable.</summary>
    private const string NamesNoArm = "a row names no arm";

    /// <summary>Which side an arm, a heading, or the lines above the first row or below the last, is taken from.</summary>
    private enum Taken
    {
        /// <summary>The main tree's lines stay where they are: it is the main tree's, or the same in both.</summary>
        Main,

        /// <summary>The agent's: it alone added or changed it.</summary>
        Agent,

        /// <summary>Neither's: it is removed.</summary>
        Gone,
    }

    /// <summary>Merges <paramref name="agent"/>'s changes since <paramref name="base"/> into <paramref name="main"/>.</summary>
    /// <param name="base">
    /// The bytes both sides last held, or <see langword="null"/> where neither held a registry then. With none, an arm one
    /// side removed cannot be told from an arm the other added, and is kept.
    /// </param>
    /// <param name="main">The bytes of the text whose lines are kept, as its tree holds them.</param>
    /// <param name="agent">The bytes of the text whose changes are brought in, as its tree holds them.</param>
    public static RegistryMerge Merge(ReadOnlyMemory<byte>? @base, ReadOnlyMemory<byte> main, ReadOnlyMemory<byte> agent)
    {
        var unread = new List<UnreadVersion>();
        var held = @base is { } bytes ? Reading.Of(MergeSide.Base, bytes.Span, unread) : null;
        var mains = Reading.Of(MergeSide.Main, main.Span, unread);
        var agents = Reading.Of(MergeSide.Agent, agent.Span, unread);

        if (unread.Count > 0 || mains is null || agents is null)
        {
            return new RegistryMerge.Unreadable(unread);
        }

        var tally = new Tally();
        var conflicts = new List<ArmConflict>();
        var arms = new Dictionary<string, Taken>(StringComparer.OrdinalIgnoreCase);
        var headings = new Dictionary<string, Taken>(StringComparer.OrdinalIgnoreCase);

        var header = Weigh(Held(held?.Header), Held(mains.Header), Held(agents.Header), Same, new Tally(), out var disagreed);

        if (header is null)
        {
            conflicts.Add(ArmConflict.OverTheLinesAbove(
                disagreed,
                Version(ArmConflict.AboveTheFirstRow, Held(held?.Header)),
                Version(ArmConflict.AboveTheFirstRow, Held(mains.Header)),
                Version(ArmConflict.AboveTheFirstRow, Held(agents.Header))));
        }

        foreach (var place in mains.Headed.Concat(agents.Headed).Concat(held?.Headed ?? []).Select(Place).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = held?.HeadedAt(place);
            var theirs = mains.HeadedAt(place);
            var ours = agents.HeadedAt(place);

            if (Weigh(before?.Heading, theirs?.Heading, ours?.Heading, Same, new Tally(), out disagreed) is { } taken)
            {
                headings[place] = taken;
                continue;
            }

            conflicts.Add(ArmConflict.OverTheHeading((theirs ?? ours ?? before)!.Arm, disagreed, HeadingVersion(before), HeadingVersion(theirs), HeadingVersion(ours)));
        }

        foreach (var arm in mains.Ids.Concat(agents.Ids).Concat(held?.Ids ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var before = held?.Arm(arm);
            var theirs = mains.Arm(arm);
            var ours = agents.Arm(arm);

            if (Weigh(before, theirs, ours, Same, tally, out disagreed) is { } taken)
            {
                arms[arm] = taken;
                continue;
            }

            conflicts.Add(ArmConflict.OverArm(
                (theirs ?? ours ?? before)![0].Arm,
                disagreed == ArmConflictKind.AddedDifferently && SpelledApart(theirs!, ours!) ? ArmConflictKind.SpelledApart : disagreed,
                Version(before),
                Version(theirs),
                Version(ours)));
        }

        var trailer = Weigh(Held(held?.Trailer), Held(mains.Trailer), Held(agents.Trailer), Same, new Tally(), out disagreed);

        if (trailer is null)
        {
            conflicts.Add(ArmConflict.OverTheLinesBelow(
                disagreed,
                Version(ArmConflict.BelowTheLastRow, Held(held?.Trailer)),
                Version(ArmConflict.BelowTheLastRow, Held(mains.Trailer)),
                Version(ArmConflict.BelowTheLastRow, Held(agents.Trailer))));
        }

        var mainCounts = new MergeCounts(tally.MainAdded, tally.MainRemoved, tally.MainChanged);
        var agentCounts = new MergeCounts(tally.AgentAdded, tally.AgentRemoved, tally.AgentChanged);

        if (conflicts.Count > 0)
        {
            return new RegistryMerge.Conflicted(conflicts, mainCounts, agentCounts);
        }

        var placing = new Placing(held, mains, agents, arms, headings);
        var placed = placing.Placed();

        if (placing.Unheaded() is { } unheaded)
        {
            return new RegistryMerge.Conflicted([unheaded], mainCounts, agentCounts);
        }

        var above = header!.Value switch
        {
            Taken.Main => mains.Header,
            Taken.Agent => agents.Header,
            _ => null,
        };
        var below = trailer!.Value switch
        {
            Taken.Main => mains.Trailer,
            Taken.Agent => agents.Trailer,
            _ => null,
        };

        if (Unsettled(placed, below) is { } unsettled)
        {
            return new RegistryMerge.Conflicted([unsettled], mainCounts, agentCounts);
        }

        var text = Written([.. above ?? [], .. placed.SelectMany(segment => segment.Lines), .. below ?? []], mains);

        return new RegistryMerge.Merged(
            text.ToBytes(),
            text.Text,
            mainCounts,
            agentCounts,
            tally.Same,
            placed.Select(segment => segment.Arm).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The merged <paramref name="lines"/> as a text: each of the main tree's lines ended as the main tree ends it, each
    /// of the agent's as the main tree ends its first, behind a byte order mark where the main tree's text has one, and
    /// with no ending on the last line where the main tree's last has none.
    /// </summary>
    /// <remarks>
    /// Two endings give way, so that the text reads back as the lines it was written from. An empty last line is ended
    /// whatever the main tree's is: without an ending of its own it could not be told from no line at all. And an empty
    /// line a line feed would end, written after a line a carriage return alone ends, is ended by the two together: the
    /// carriage return and the line feed side by side would read as one ending, and the empty line as none.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The text does not read back as the lines it was written from.</exception>
    private static RegistryText Written(Line[] lines, Reading mains)
    {
        var written = new List<RegistryLine>(lines.Length);

        foreach (var line in lines)
        {
            var ending = written.Count == lines.Length - 1 && !mains.EndsItsLastLine && line.Text.Length > 0 ? string.Empty
                : line.Side == MergeSide.Main && line.Ending.Length > 0 ? line.Ending
                : mains.LineEnding;

            written.Add(new RegistryLine(line.Text, ending == "\n" && line.Text.Length == 0 && written is [.., { Ending: "\r" }] ? "\r\n" : ending));
        }

        var text = RegistryText.Of(written, mains.ByteOrderMark);

        // What is offered is merged again whenever a command that wrote part of it is run again, and gives itself only
        // where it reads back as it was merged: a text that does not is this tool's defect, and is never offered.
        return RegistryText.TryRead(text.ToBytes(), out var read, out _, out _)
            && read.ByteOrderMark == mains.ByteOrderMark
            && Reading.Ended(read).Select(line => line.Text).SequenceEqual(lines.Select(line => line.Text), StringComparer.Ordinal)
                ? text
                : throw new InvalidOperationException("A registry merged by arm does not read back as the lines it was merged from.");
    }

    /// <summary>
    /// Which side <paramref name="main"/> and <paramref name="agent"/> leave one thing to, given what both
    /// <paramref name="held"/> of it - each <see langword="null"/> where that text has none - or <see langword="null"/>
    /// where they disagree, with how.
    /// </summary>
    private static Taken? Weigh<T>(T? held, T? main, T? agent, Func<T?, T?, bool> same, Tally tally, out ArmConflictKind disagreed)
        where T : class
    {
        disagreed = default;

        if (same(main, agent))
        {
            if (!same(held, main))
            {
                tally.Same++;
            }

            return main is null ? Taken.Gone : Taken.Main;
        }

        if (held is null)
        {
            if (agent is null)
            {
                tally.MainAdded++;
                return Taken.Main;
            }

            if (main is null)
            {
                tally.AgentAdded++;
                return Taken.Agent;
            }

            disagreed = ArmConflictKind.AddedDifferently;
            return null;
        }

        if (same(main, held))
        {
            if (agent is null)
            {
                tally.AgentRemoved++;
                return Taken.Gone;
            }

            tally.AgentChanged++;
            return Taken.Agent;
        }

        if (same(agent, held))
        {
            if (main is null)
            {
                tally.MainRemoved++;
                return Taken.Gone;
            }

            tally.MainChanged++;
            return Taken.Main;
        }

        disagreed = main is null ? ArmConflictKind.RemovedByMain
            : agent is null ? ArmConflictKind.RemovedByAgent
            : ArmConflictKind.ChangedDifferently;

        return null;
    }

    /// <summary>
    /// The conflict of a merge whose text would not read back as it was merged, or <see langword="null"/> where it
    /// would: the lines above the first row end at their last blank line, so a blank line the first segment keeps above
    /// its row - or, with no row left, one among the lines that were below the last - would be read as theirs. Merged
    /// again, such a text would be weighed as another, and give another.
    /// </summary>
    private static ArmConflict? Unsettled(List<Segment> placed, IReadOnlyList<Line>? below)
    {
        var lines = placed.Count > 0 ? placed[0].Lines : below ?? [];
        var blank = placed.Count > 0 ? placed[0].LastBlankAbove : lines.ToList().FindLastIndex(line => line.Blank);

        if (blank < 0)
        {
            return null;
        }

        var version = Version(placed.Count > 0 ? placed[0].Arm : ArmConflict.BelowTheLastRow, [.. lines.Take(blank + 1)]);
        var main = lines[0].Side == MergeSide.Main ? version : null;
        var agent = lines[0].Side == MergeSide.Agent ? version : null;

        return placed.Count > 0
            ? ArmConflict.OverArm(version!.Id, ArmConflictKind.WouldOpenTheText, null, main, agent)
            : ArmConflict.OverTheLinesBelow(ArmConflictKind.WouldOpenTheText, null, main, agent);
    }

    /// <summary>
    /// Where a segment stands among its arm's, as a heading is weighed by it: its arm, ids compared ignoring case, and its
    /// place among the arm's segments.
    /// </summary>
    private static string Place(Segment segment) => Place(segment.Arm, segment.Within);

    /// <inheritdoc cref="Place(Segment)"/>
    private static string Place(string arm, int within) => string.Create(CultureInfo.InvariantCulture, $"{arm}|{within}");

    /// <summary>One side's version of an arm: its segments' own lines, the arm named as that side spells it; nothing where it has none.</summary>
    private static ArmVersion? Version(IReadOnlyList<Segment>? arm) => arm is null ? null : Version(arm[0].Arm, [.. arm.SelectMany(segment => segment.Own)]);

    /// <summary>One side's version of a heading: its lines, named for the arm whose row it stands above there; nothing where it has none.</summary>
    private static ArmVersion? HeadingVersion(Segment? under) => under is null ? null : Version(under.Arm, under.Heading);

    /// <summary>One side's version of lines named <paramref name="named"/>, each with its number there; nothing where it has none.</summary>
    private static ArmVersion? Version(string named, IReadOnlyList<Line>? lines) => lines is null ? null : new ArmVersion(named, [.. lines.Select(line => (line.Number, line.Text))]);

    /// <summary>
    /// The lines above the first row, or below the last, as one thing a text holds or does not: <see langword="null"/>
    /// where it has no such line, as it holds no arm it does not name.
    /// </summary>
    private static IReadOnlyList<Line>? Held(IReadOnlyList<Line>? lines) => lines is { Count: > 0 } ? lines : null;

    /// <summary>Whether two runs of lines are the same: as many, each with the other's text, however each ends.</summary>
    private static bool Same(IReadOnlyList<Line>? one, IReadOnlyList<Line>? other)
        => one is null || other is null
            ? one is null && other is null
            : one.Select(line => line.Text).SequenceEqual(other.Select(line => line.Text), StringComparer.Ordinal);

    /// <summary>
    /// Whether two versions of an arm are the same: their segments the same in number and order, and each the same in
    /// what its arm holds of it - its row and the comments abutting it.
    /// </summary>
    private static bool Same(IReadOnlyList<Segment>? one, IReadOnlyList<Segment>? other)
        => one is null || other is null
            ? one is null && other is null
            : one.Count == other.Count && one.Zip(other).All(pair => Same(pair.First.Own, pair.Second.Own));

    /// <summary>
    /// Whether two versions of an arm differ only in how each spells the arm's id: line for line they are one text
    /// once both spell it one way, what parts each segment from what stands above it left out, and so are cut into the
    /// same segments, each ending at its row.
    /// </summary>
    private static bool SpelledApart(IReadOnlyList<Segment> one, IReadOnlyList<Segment> other)
    {
        var spelled = one[0].Arm;

        IEnumerable<string> Respelled(IReadOnlyList<Segment> arm) => arm.SelectMany(segment => segment.Own).Select(line => line.Read.NamedAs(spelled));

        return Respelled(one).SequenceEqual(Respelled(other), StringComparer.Ordinal);
    }

    /// <summary>What each side contributed, counted as the arms are weighed.</summary>
    private sealed class Tally
    {
        public int MainAdded { get; set; }

        public int MainRemoved { get; set; }

        public int MainChanged { get; set; }

        public int AgentAdded { get; set; }

        public int AgentRemoved { get; set; }

        public int AgentChanged { get; set; }

        public int Same { get; set; }
    }

    /// <summary>
    /// The merged text's segments, placed: the main tree's, each where it stands; the agent's version of an arm over the
    /// main tree's segments of it; and each arm only the agent has, where the agent's text places it - each written with
    /// the heading taken for its place, or what parts it from what stands above it where none is, and the first of them
    /// all without the blank lines that open it.
    /// </summary>
    /// <param name="held">What both held, or <see langword="null"/> where neither held a registry.</param>
    /// <param name="mains">The main tree's text.</param>
    /// <param name="agents">The agent's text.</param>
    /// <param name="arms">Which side each arm is taken from.</param>
    /// <param name="headings">Which side the heading of each place is taken from.</param>
    private sealed class Placing(Reading? held, Reading mains, Reading agents, Dictionary<string, Taken> arms, Dictionary<string, Taken> headings)
    {
        /// <summary>
        /// The segments placed, in order, each by the segment whose own lines it writes, the segment whose place it is written
        /// in, and what it is written under.
        /// </summary>
        private readonly List<(Segment Source, Segment Slot, IReadOnlyList<Line> Above)> _placed = [];

        /// <summary>The places whose heading taken was written above the segment that stands there.</summary>
        private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The places of the headings only the agent added that the result already holds over the row under each (<see cref="Join"/>).</summary>
        private readonly HashSet<string> _joined = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The merged text's segments, in order.</summary>
        public List<Segment> Placed()
        {
            foreach (var segment in mains.Segments)
            {
                switch (arms[segment.Arm])
                {
                    case Taken.Main:
                        Add(segment, segment);
                        break;

                    case Taken.Agent:
                        // One for one in order, each written as the agent's own lines in the main tree's segment's place: where
                        // the agent has more, the rest follow the last of the main tree's, and where it has fewer, the main
                        // tree's remaining places go.
                        var theirs = mains.Arm(segment.Arm)!;
                        var ours = agents.Arm(segment.Arm)!;

                        if (segment.Within < ours.Count)
                        {
                            Add(ours[segment.Within], segment);
                        }

                        if (segment.Within == theirs.Count - 1)
                        {
                            foreach (var more in ours.Skip(theirs.Count))
                            {
                                Add(more, more);
                            }
                        }

                        break;
                }
            }

            foreach (var segment in agents.Segments.Where(segment => arms[segment.Arm] == Taken.Agent && mains.Arm(segment.Arm) is null))
            {
                _placed.Insert(Where(segment), Written(segment, segment));
            }

            Join();

            var laid = _placed.Select(placed => new Segment(placed.Source.Side, [.. placed.Above, .. placed.Source.Own], placed.Source.At) { Within = placed.Source.Within }).ToList();

            // The blank lines that open a segment part it from what stands above it, and nothing stands above the first but
            // the lines above the first row, which end at their own last blank line.
            if (laid is [{ Leading: > 0 } first, ..])
            {
                laid[0] = new Segment(first.Side, [.. first.Lines.SkipWhile(line => line.Blank)], first.At) { Within = first.Within };
            }

            return laid;
        }

        /// <summary>
        /// The conflict of a heading taken for a place no segment of the result stands in, or <see langword="null"/> where
        /// each stands above its own: the row under it removed by one side, the heading kept or changed by the other, it
        /// would be read as part of what stands below it.
        /// </summary>
        public ArmConflict? Unheaded()
        {
            foreach (var (place, taken) in headings)
            {
                if (taken == Taken.Gone || _written.Contains(place) || _joined.Contains(place))
                {
                    continue;
                }

                var under = (taken == Taken.Main ? mains : agents).HeadedAt(place)!;
                var version = HeadingVersion(under);

                return ArmConflict.OverTheHeading(under.Arm, ArmConflictKind.HeadsNoArm, null, taken == Taken.Main ? version : null, taken == Taken.Agent ? version : null);
            }

            return null;
        }

        /// <summary>
        /// Takes back each heading only the agent added - at a place where the main tree holds none - that the result
        /// already holds over the row under it: where the nearest heading written above that row, with only rows between, is
        /// the main tree's own, of the same lines, which the main tree wrote where it stands - a heading both held there is
        /// none of the main tree's writing. The two sides headed one section, each above a new first arm of its own, and the
        /// one heading is written once, where the main tree stands it; whether both held it elsewhere before is no part of
        /// it, and one both renamed or added alike is as much one heading as one both held. Any other stands where the
        /// agent stood it, as any heading added at a place does: above a row the result stands under another heading or
        /// none, though the main tree added one of the same lines elsewhere; under one the agent wrote, or one both held
        /// there; and wherever the agent's own text holds a heading of those lines nearest above its own - the agent wrote
        /// it twice, whatever the main tree did with the first: renamed it alike, held it with no text both held, or moved
        /// it above a new first arm of its own.
        /// </summary>
        private void Join()
        {
            Segment? over = null;

            for (var index = 0; index < _placed.Count; index++)
            {
                var (source, slot, _) = _placed[index];
                var place = Place(slot);

                if (!_written.Contains(place))
                {
                    continue;
                }

                if (headings[place] == Taken.Agent
                    && mains.HeadedAt(place) is null
                    && over is { } above
                    && headings[Place(above)] == Taken.Main
                    && mains.HeadedAt(Place(above)) is { } theirs
                    && !Same(held?.HeadedAt(Place(above))?.Heading, theirs.Heading)
                    && Same(theirs.Heading, Heading(place))
                    && !Same(agents.HeadingAbove(agents.HeadedAt(place)!), theirs.Heading))
                {
                    _written.Remove(place);
                    _joined.Add(place);
                    _placed[index] = (source, slot, slot.Parting);
                    continue;
                }

                over = slot;
            }
        }

        /// <summary>The heading taken for <paramref name="place"/> as the result writes it; none where none is taken.</summary>
        private IReadOnlyList<Line>? Heading(string place)
            => headings.GetValueOrDefault(place, Taken.Gone) switch
            {
                Taken.Main => mains.HeadedAt(place)?.Heading,
                Taken.Agent => agents.HeadedAt(place)?.Heading,
                _ => null,
            };

        /// <summary>Places <paramref name="source"/>'s own lines last, in <paramref name="slot"/>'s place.</summary>
        private void Add(Segment source, Segment slot) => _placed.Add(Written(source, slot));

        /// <summary>
        /// <paramref name="source"/>'s own lines as they are written in <paramref name="slot"/>'s place: under the heading
        /// taken for that place, with the blank lines it stands among where the side it is taken from holds it; and where
        /// none is taken, under what parts the slot from what stands above it - as a heading <see cref="Join"/> takes back
        /// is written, once every segment is placed.
        /// </summary>
        private (Segment Source, Segment Slot, IReadOnlyList<Line> Above) Written(Segment source, Segment slot)
        {
            var place = Place(slot);
            var heading = headings.GetValueOrDefault(place, Taken.Gone);

            if (heading != Taken.Gone)
            {
                _written.Add(place);
            }

            return (source, slot, heading switch
            {
                Taken.Main => mains.HeadedAt(place)!.Above,
                Taken.Agent => agents.HeadedAt(place)!.Above,
                _ => slot.Parting,
            });
        }

        /// <summary>Where the result holds <paramref name="source"/>'s own lines; -1 where it does not.</summary>
        private int IndexOf(Segment source) => _placed.FindIndex(placed => placed.Source == source);

        /// <summary>Whether the main tree's version of the arm of the agent's segment holds one like it.</summary>
        private bool Kept(Segment ours) => mains.Arm(ours.Arm)!.Any(theirs => Same(theirs.Own, ours.Own));

        /// <summary>
        /// Where the agent's segment of an arm kept from the main tree stands in the result. Where the main tree's version
        /// holds none like it: nowhere, where that version holds fewer segments of the arm - it dropped this one - and
        /// otherwise the main tree's segment in its place, the arm's last for the agent's last, since the main tree reworded
        /// it. Where the main tree's version holds one like it: the arm's last, where no later segment of the arm in the
        /// agent's text is kept, so that a row only the main tree's version holds stays with its arm; otherwise that one.
        /// </summary>
        private Segment? KeptAs(Segment ours)
        {
            var theirs = mains.Arm(ours.Arm)!;
            var all = agents.Arm(ours.Arm)!;

            if (!Kept(ours))
            {
                return theirs.Count < all.Count ? null
                    : ours.Within == all.Count - 1 ? theirs[^1]
                    : theirs[Math.Min(ours.Within, theirs.Count - 1)];
            }

            return all.Skip(ours.Within + 1).Any(Kept)
                ? theirs.Where(kept => Same(kept.Own, ours.Own)).MinBy(kept => Math.Abs(kept.Within - ours.Within))
                : theirs[^1];
        }

        /// <summary>
        /// What the main tree alone holds: a segment of an arm the agent's text has none of. Placed, it is one the main tree
        /// added - had both held the arm, the agent removed it, and it would be gone or a conflict.
        /// </summary>
        private bool MainsAlone(int at) => _placed[at].Source.Side == MergeSide.Main && agents.Arm(_placed[at].Source.Arm) is null;

        /// <summary>The first place from <paramref name="at"/> past what the main tree alone holds there.</summary>
        private int Past(int at)
        {
            while (at < _placed.Count && MainsAlone(at))
            {
                at++;
            }

            return at;
        }

        /// <summary>
        /// Where the segment of an arm only the agent has goes: after the nearest segment above it in the agent's text that
        /// the result holds - the agent's own, as it stands there, or the main tree's like it - past what the main tree
        /// alone holds there; the agent's first segment first, past that; one above which the result holds nothing after
        /// the last; and never above an earlier segment of its own arm, so the rows of one arm keep their order.
        /// </summary>
        private int Where(Segment segment)
        {
            var at = segment.At == 0 ? Past(0) : _placed.Count;

            for (var above = segment.At - 1; above >= 0; above--)
            {
                var follows = agents.Segments[above];
                var anchor = arms[follows.Arm] switch
                {
                    Taken.Agent => follows,
                    Taken.Main => KeptAs(follows),
                    _ => null,
                };

                if (anchor is not null && IndexOf(anchor) is >= 0 and var found)
                {
                    at = Past(found + 1);
                    break;
                }
            }

            if (segment.Within > 0)
            {
                at = Math.Max(at, IndexOf(agents.Arm(segment.Arm)![segment.Within - 1]) + 1);
            }

            return at;
        }
    }

    /// <summary>A line of one of the three texts.</summary>
    /// <param name="Side">Which text.</param>
    /// <param name="Number">Its number there, from 1.</param>
    /// <param name="Read">The line.</param>
    private sealed record Line(MergeSide Side, int Number, RegistryLine Read)
    {
        public string Text => Read.Text;

        public string Ending => Read.Ending;

        public bool Blank => Read.Kind == RegistryLineKind.Blank;
    }

    /// <summary>One row, with the comment and blank lines directly above it.</summary>
    /// <param name="side">Which text it is of.</param>
    /// <param name="lines">The lines above the row, then the row.</param>
    /// <param name="at">Its place among its text's segments, from 0.</param>
    private sealed class Segment(MergeSide side, IReadOnlyList<Line> lines, int at)
    {
        public MergeSide Side { get; } = side;

        public IReadOnlyList<Line> Lines { get; } = lines;

        public int At { get; } = at;

        /// <summary>The arm its row names, as the row spells it; nothing where the row names none, which makes its text unreadable.</summary>
        public string Arm { get; } = lines[^1].Read.Arm ?? string.Empty;

        /// <summary>
        /// Its lines below the last blank line above its row: the row and the comments abutting it, all that its arm holds
        /// of it.
        /// </summary>
        public IReadOnlyList<Line> Own { get; } = [.. OwnLines(lines)];

        /// <summary>
        /// The heading above it: its comments a blank line parts from its row, from the first of them to the last, the
        /// blank lines between them among them; none where it has no such comment.
        /// </summary>
        public IReadOnlyList<Line> Heading { get; } = HeadingOf(lines);

        /// <summary>Its lines above its own: the blank lines that part it from what stands above it, and its heading among them.</summary>
        public IReadOnlyList<Line> Above => [.. Lines.Take(Leading)];

        /// <summary>
        /// What parts it from what stands above it where no heading stands over it: the blank lines above its own, or,
        /// where a heading stood there, the one blank line that parted the heading from its row.
        /// </summary>
        public IReadOnlyList<Line> Parting => Heading.Count == 0 ? Above : [Above[^1]];

        /// <summary>How many lines stand above its own.</summary>
        public int Leading => Lines.Count - Own.Count;

        /// <summary>Its place among its arm's segments, from 0.</summary>
        public int Within { get; set; }

        /// <summary>Where the last blank line above its row is, or -1 where none is.</summary>
        public int LastBlankAbove => Lines.ToList().FindLastIndex(line => line.Blank);

        /// <summary>The lines of <paramref name="lines"/> below the last blank line above their row.</summary>
        private static IReadOnlyList<Line> OwnLines(IReadOnlyList<Line> lines)
        {
            for (var at = lines.Count - 2; at >= 0; at--)
            {
                if (lines[at].Blank)
                {
                    return [.. lines.Skip(at + 1)];
                }
            }

            return lines;
        }

        /// <summary>The lines of <paramref name="lines"/> above their own, from the first that is not blank to the last.</summary>
        private static IReadOnlyList<Line> HeadingOf(IReadOnlyList<Line> lines)
        {
            var above = lines.Take(lines.Count - OwnLines(lines).Count).ToList();
            var first = above.FindIndex(line => !line.Blank);

            return first < 0 ? [] : above[first..(above.FindLastIndex(line => !line.Blank) + 1)];
        }
    }

    /// <summary>One of the three texts as the merge reads it.</summary>
    private sealed class Reading
    {
        private readonly Dictionary<string, List<Segment>> _arms = new(StringComparer.OrdinalIgnoreCase);

        private Reading(MergeSide side, RegistryText text, List<UnreadVersion> unread)
        {
            Line[] lines = [.. Ended(text).Select((line, index) => new Line(side, index + 1, line))];
            var rows = new List<List<Line>>();
            var above = new List<Line>();

            foreach (var line in lines)
            {
                above.Add(line);

                if (line.Read.Row is null)
                {
                    continue;
                }

                if (line.Read.Arm is null)
                {
                    unread.Add(UnreadVersion.OfLine(side, line.Number, NamesNoArm));
                }

                rows.Add(above);
                above = [];
            }

            // The lines above the first row are the registry's own down to their last blank line; the comments below
            // that line are the first row's. With no row, the same line parts what stands above from what stands below.
            var first = rows.Count > 0 ? rows[0] : above;
            var opening = first.FindLastIndex(line => line.Blank) + 1;

            Header = first[..opening];
            first.RemoveRange(0, opening);

            Trailer = above;
            Segments = [.. rows.Select((row, index) => new Segment(side, row, index))];
            Headed = [.. Segments.Where(segment => segment.Heading.Count > 0)];
            ByteOrderMark = text.ByteOrderMark;
            EndsItsLastLine = lines.Length == 0 || lines[^1].Ending.Length > 0;
            LineEnding = lines.Length > 0 && lines[0].Ending.Length > 0 ? lines[0].Ending : "\n";

            foreach (var segment in Segments)
            {
                if (!_arms.TryGetValue(segment.Arm, out var arm))
                {
                    _arms[segment.Arm] = arm = [];
                    Ids.Add(segment.Arm);
                }

                segment.Within = arm.Count;
                arm.Add(segment);
            }
        }

        /// <summary>The lines above the first row, down to their last blank line; none where there is no such line.</summary>
        public IReadOnlyList<Line> Header { get; }

        /// <summary>Each row with the lines directly above it, in order.</summary>
        public IReadOnlyList<Segment> Segments { get; }

        /// <summary>Each segment a heading stands above, in order.</summary>
        public IReadOnlyList<Segment> Headed { get; }

        /// <summary>The comment and blank lines below the last row.</summary>
        public IReadOnlyList<Line> Trailer { get; }

        /// <summary>Each arm the text names, as its first row spells it, in the order they first stand.</summary>
        public List<string> Ids { get; } = [];

        public bool ByteOrderMark { get; }

        /// <summary>Whether its last line ends with a line ending, as every line of a text holding none does.</summary>
        public bool EndsItsLastLine { get; }

        /// <summary>How its first line ends: a line feed, where it has no line or its only line does not end.</summary>
        public string LineEnding { get; }

        /// <summary>
        /// The text of <paramref name="bytes"/>, or <see langword="null"/> where they are not UTF-8; each reason it cannot
        /// be merged - that, or a row naming no arm - added to <paramref name="unread"/>.
        /// </summary>
        public static Reading? Of(MergeSide side, ReadOnlySpan<byte> bytes, List<UnreadVersion> unread)
        {
            if (RegistryText.TryRead(bytes, out var text, out var why, out var line))
            {
                return new Reading(side, text, unread);
            }

            unread.Add(line is { } at ? UnreadVersion.OfLine(side, at, why) : UnreadVersion.OfText(side, why));

            return null;
        }

        /// <summary>
        /// The lines <paramref name="text"/> holds, each ended: the text after the last line ending is a line of its own
        /// to the reader, and empty, it is none here.
        /// </summary>
        public static IEnumerable<RegistryLine> Ended(RegistryText text)
            => text.Lines[^1] is { Text.Length: 0, Ending.Length: 0 } ? text.Lines.Take(text.Lines.Count - 1) : text.Lines;

        /// <summary>The segments naming <paramref name="arm"/>, in order, ids compared ignoring case; <see langword="null"/> where none does.</summary>
        public IReadOnlyList<Segment>? Arm(string arm) => _arms.GetValueOrDefault(arm);

        /// <summary>The nearest heading this text holds above <paramref name="segment"/>'s own; <see langword="null"/> where it holds none above it.</summary>
        /// <param name="segment">One of this text's segments.</param>
        public IReadOnlyList<Line>? HeadingAbove(Segment segment)
            => Segments.Take(segment.At).LastOrDefault(above => above.Heading.Count > 0)?.Heading;

        /// <summary>The segment standing at <paramref name="place"/> where a heading stands above it; <see langword="null"/> where none does.</summary>
        /// <param name="place">An arm and a place among its segments (<see cref="Place(string, int)"/>).</param>
        public Segment? HeadedAt(string place)
        {
            var parted = place.LastIndexOf('|');

            return Arm(place[..parted]) is { } arm
                && int.Parse(place.AsSpan(parted + 1), CultureInfo.InvariantCulture) is var within
                && within < arm.Count
                && arm[within].Heading.Count > 0
                    ? arm[within]
                    : null;
        }
    }
}
