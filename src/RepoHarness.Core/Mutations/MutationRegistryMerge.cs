using System.Globalization;
using RepoHarness.Core.Output;

namespace RepoHarness.Core.Mutations;

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

/// <summary>Why one of the three texts could not be read as a registry.</summary>
/// <param name="Side">Which of the three.</param>
/// <param name="Line">The line, or <see langword="null"/> where it is the text as a whole.</param>
/// <param name="Why">What is wrong, as what follows the text's name: <c>is not UTF-8: ...</c>, <c>a row names no arm</c>.</param>
public sealed record UnreadVersion(MergeSide Side, int? Line, string Why)
{
    /// <summary>The problem as a line says it, the text called what the caller calls it.</summary>
    /// <param name="sides">What the three are called.</param>
    public string Said(MergeSides sides)
    {
        ArgumentNullException.ThrowIfNull(sides);

        return Line is { } line
            ? string.Create(CultureInfo.InvariantCulture, $"{sides.Of(Side)}'s, line {line}: {Why}")
            : $"{sides.Of(Side)}'s version {Why}";
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
}

/// <summary>An arm the two sides did not leave mergeable: what they disagree over, how, and each side's version.</summary>
/// <param name="Arm">The arm's id, as the main tree spells it where it holds the arm; what the lines are, where the conflict is over no arm.</param>
/// <param name="Kind">How the two disagree.</param>
/// <param name="Base">What both held of it, or <see langword="null"/> where they held none.</param>
/// <param name="Main">The main tree's version, or <see langword="null"/> where it has none.</param>
/// <param name="Agent">The agent's version, or <see langword="null"/> where it has none.</param>
public sealed record ArmConflict(string Arm, ArmConflictKind Kind, ArmVersion? Base, ArmVersion? Main, ArmVersion? Agent)
{
    /// <summary>What the lines above the first row are called, in a conflict over them.</summary>
    public const string AboveTheFirstRow = "the lines above the first row";

    /// <summary>What the lines below the last row are called, in a conflict over them.</summary>
    public const string BelowTheLastRow = "the lines below the last row";

    /// <summary>What the conflict is over: an arm, unless it says otherwise.</summary>
    public RegistryPart Part { get; init; } = RegistryPart.Arm;

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
        _ => $"arm '{Arm}'",
    };

    private string How(MergeSides sides) => Kind switch
    {
        ArmConflictKind.AddedDifferently => "added by both, each differently",
        ArmConflictKind.SpelledApart => "added by both, under ids that differ only in the case of their letters",
        ArmConflictKind.ChangedDifferently => "changed by both, each differently",
        ArmConflictKind.RemovedByMain => $"removed by {sides.Main}, changed by {sides.Agent}",
        ArmConflictKind.RemovedByAgent => $"removed by {sides.Agent}, changed by {sides.Main}",
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
    /// <param name="Conflicts">Every conflict: the lines above the first row, then each arm, then the lines below the last row.</param>
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
/// comment and blank lines directly above it; and the comment and blank lines below its last row. An arm is the
/// segments naming it, in the order they stand, ids compared ignoring case. Two versions of an arm are the same where
/// their segments are the same in number and order and each line's text is equal: how a line ends is no part of it, so
/// a checkout with other line endings changes nothing.
/// </para>
/// <para>
/// Each arm is taken from the side that alone added, removed or changed it, and is a conflict where both did so
/// differently. The lines above the first row, and those below the last, are each weighed the same way, as one.
/// </para>
/// <para>
/// The main tree's text is kept line for line and changed only where the agent contributed. An arm taken from the
/// agent in place of the main tree's lies over the main tree's segments of it, one for one; an arm only the agent has
/// stands after the segment it follows in the agent's text, and after what the main tree alone holds there - so two
/// sides adding at one place come out as the main tree's addition, then the agent's. Each of the main tree's lines is
/// written as the main tree holds it, each of the agent's with the ending of the main tree's first line, behind a byte
/// order mark where the main tree's text has one and ending with a line ending where it does.
/// </para>
/// <para>
/// So a text merged with itself is itself, a text merged with a side that changed nothing is the other, and a merge
/// run again with its own result on either side gives that result, each byte for byte. The last holds because a merge
/// is only offered where its text reads back as it was merged (<see cref="ArmConflictKind.WouldOpenTheText"/>).
/// </para>
/// </remarks>
public static class MutationRegistryMerge
{
    /// <summary>Why a text holding a row with no second field is unreadable.</summary>
    private const string NamesNoArm = "a row names no arm";

    /// <summary>Why a text whose first line still opens with a byte order mark is unreadable.</summary>
    private const string SecondMark = "it opens with a second byte order mark";

    /// <summary>The byte order mark, as a character of a text.</summary>
    private const char MarkCharacter = (char)0xFEFF;

    /// <summary>Which side an arm, or the lines above the first row or below the last, is taken from.</summary>
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
        var header = Weigh(Held(held?.Header), Held(mains.Header), Held(agents.Header), Same, new Tally(), out var disagreed);

        if (header is null)
        {
            conflicts.Add(Conflict(ArmConflict.AboveTheFirstRow, disagreed, Held(held?.Header), Held(mains.Header), Held(agents.Header)) with { Part = RegistryPart.AboveTheFirstRow });
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

            conflicts.Add(Conflict(
                (theirs ?? ours ?? before)![0].Arm,
                disagreed == ArmConflictKind.AddedDifferently && SpelledApart(theirs!, ours!) ? ArmConflictKind.SpelledApart : disagreed,
                Lines(before),
                Lines(theirs),
                Lines(ours)));
        }

        var trailer = Weigh(Held(held?.Trailer), Held(mains.Trailer), Held(agents.Trailer), Same, new Tally(), out disagreed);

        if (trailer is null)
        {
            conflicts.Add(Conflict(ArmConflict.BelowTheLastRow, disagreed, Held(held?.Trailer), Held(mains.Trailer), Held(agents.Trailer)) with { Part = RegistryPart.BelowTheLastRow });
        }

        var mainCounts = new MergeCounts(tally.MainAdded, tally.MainRemoved, tally.MainChanged);
        var agentCounts = new MergeCounts(tally.AgentAdded, tally.AgentRemoved, tally.AgentChanged);

        if (conflicts.Count > 0)
        {
            return new RegistryMerge.Conflicted(conflicts, mainCounts, agentCounts);
        }

        var placed = Placed(mains, agents, arms);
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
        return RegistryText.TryRead(text.ToBytes(), out var read, out _)
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
    /// The merged text's segments, in order: the main tree's, each where it stands; the agent's version of an arm over
    /// the main tree's segments of it; and each arm only the agent has, where the agent's text places it.
    /// </summary>
    private static List<Segment> Placed(Reading mains, Reading agents, Dictionary<string, Taken> arms)
    {
        var placed = new List<Segment>();

        foreach (var segment in mains.Segments)
        {
            switch (arms[segment.Arm])
            {
                case Taken.Main:
                    placed.Add(segment);
                    break;

                case Taken.Agent:
                    // One for one in order; where the agent has more, the rest follow the last of the main tree's, and
                    // where it has fewer, the main tree's remaining places go.
                    var theirs = mains.Arm(segment.Arm)!;
                    var ours = agents.Arm(segment.Arm)!;

                    if (segment.Within < ours.Count)
                    {
                        placed.Add(ours[segment.Within]);
                    }

                    if (segment.Within == theirs.Count - 1)
                    {
                        placed.AddRange(ours.Skip(theirs.Count));
                    }

                    break;
            }
        }

        // What the main tree alone holds: a segment of an arm the agent's text has none of. Placed, it is one the main
        // tree added - had both held the arm, the agent removed it, and it would be gone or a conflict.
        bool MainsAlone(Segment segment) => segment.Side == MergeSide.Main && agents.Arm(segment.Arm) is null;

        int Past(int at)
        {
            while (at < placed.Count && MainsAlone(placed[at]))
            {
                at++;
            }

            return at;
        }

        foreach (var segment in agents.Segments.Where(segment => arms[segment.Arm] == Taken.Agent && mains.Arm(segment.Arm) is null))
        {
            var at = segment.At == 0 ? Past(0) : placed.Count;

            // After the nearest segment above it in the agent's text that the result holds: the agent's own, or the main
            // tree's segment of that arm - its last, where the agent's is the last of its arm, so that a row only the
            // main tree's version holds stays with its arm.
            for (var above = segment.At - 1; above >= 0; above--)
            {
                var follows = agents.Segments[above];
                var anchor = arms[follows.Arm] switch
                {
                    Taken.Agent => follows,
                    Taken.Main when mains.Arm(follows.Arm) is { } theirs
                        => theirs[follows.Within == agents.Arm(follows.Arm)!.Count - 1 ? theirs.Count - 1 : Math.Min(follows.Within, theirs.Count - 1)],
                    _ => null,
                };

                if (anchor is not null)
                {
                    at = Past(placed.IndexOf(anchor) + 1);
                    break;
                }
            }

            // Never above an earlier segment of its own arm: the rows of one arm keep their order.
            if (segment.Within > 0)
            {
                at = Math.Max(at, placed.IndexOf(agents.Arm(segment.Arm)![segment.Within - 1]) + 1);
            }

            placed.Insert(at, segment);
        }

        return placed;
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

        var version = new ArmVersion(
            placed.Count > 0 ? placed[0].Arm : ArmConflict.BelowTheLastRow,
            [.. lines.Take(blank + 1).Select(line => (line.Number, line.Text))]);

        return new ArmConflict(
            version.Id,
            ArmConflictKind.WouldOpenTheText,
            null,
            lines[0].Side == MergeSide.Main ? version : null,
            lines[0].Side == MergeSide.Agent ? version : null)
        {
            Part = placed.Count > 0 ? RegistryPart.Arm : RegistryPart.BelowTheLastRow,
        };
    }

    private static ArmConflict Conflict(string named, ArmConflictKind kind, IReadOnlyList<Line>? held, IReadOnlyList<Line>? main, IReadOnlyList<Line>? agent)
    {
        ArmVersion? Version(IReadOnlyList<Line>? lines) => lines is null ? null : new ArmVersion(named, [.. lines.Select(line => (line.Number, line.Text))]);

        return new ArmConflict(named, kind, Version(held), Version(main), Version(agent));
    }

    private static IReadOnlyList<Line>? Lines(IReadOnlyList<Segment>? arm) => arm is null ? null : [.. arm.SelectMany(segment => segment.Lines)];

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

    /// <summary>Whether two versions of an arm are the same: their segments the same in number and order, each line's text equal.</summary>
    private static bool Same(IReadOnlyList<Segment>? one, IReadOnlyList<Segment>? other)
        => one is null || other is null
            ? one is null && other is null
            : one.Count == other.Count && one.Zip(other).All(pair => Same(pair.First.Lines, pair.Second.Lines));

    /// <summary>
    /// Whether two versions of an arm differ only in how each spells the arm's id: line for line they are one text
    /// once both spell it one way, and so are cut into the same segments, each ending at its row.
    /// </summary>
    private static bool SpelledApart(IReadOnlyList<Segment> one, IReadOnlyList<Segment> other)
    {
        var spelled = one[0].Arm;

        IEnumerable<string> Respelled(IReadOnlyList<Segment> arm) => arm.SelectMany(segment => segment.Lines).Select(line => line.Read.NamedAs(spelled));

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

        /// <summary>Its place among its arm's segments, from 0.</summary>
        public int Within { get; set; }

        /// <summary>Where the last blank line above its row is, or -1 where none is.</summary>
        public int LastBlankAbove => Lines.ToList().FindLastIndex(line => line.Blank);
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

            // A mark the reader left in the first line stands behind the one it took for the text's own: written under
            // another text's mark, or under none, the line would read back as another.
            if (lines.Length > 0 && lines[0].Text.StartsWith(MarkCharacter))
            {
                unread.Add(new UnreadVersion(side, 1, SecondMark));
            }

            foreach (var line in lines)
            {
                above.Add(line);

                if (line.Read.Row is null)
                {
                    continue;
                }

                if (line.Read.Arm is null)
                {
                    unread.Add(new UnreadVersion(side, line.Number, NamesNoArm));
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
            if (RegistryText.TryRead(bytes, out var text, out var why))
            {
                return new Reading(side, text, unread);
            }

            unread.Add(new UnreadVersion(side, null, why));

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
    }
}
