using System.Globalization;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;

namespace DssHarness.Core.Mutations;

/// <summary>
/// Why an arm is pending on a leg, or why what it is judged on is not what it was: one of a closed list, each with its code,
/// the words a line says it in, and the words a leg's count of its pending arms groups it by. The list is ordered as a
/// reader asks - the record and the leg first, then what the judge and the registry say, then the tree, the settings and
/// the host, then what only a build in a worker reads - and the first that applies is the one a line names.
/// </summary>
public sealed class PendingReason
{
    private PendingReason(string code, string said)
    {
        Code = code;
        Said = said;
    }

    /// <summary>The reason, as data names it: <c>files-moved</c>.</summary>
    public string Code { get; }

    /// <summary>The reason, as a line says it: <c>what 'lex_tests' is built from moved: tests/lex/a_test.cpp</c>.</summary>
    public string Said { get; }

    /// <summary>
    /// Where in the list's order this reason stands: the first that applies is the one a line names, and a reader given
    /// every one - a listing under <c>--json</c>, a sweep - names them in this order however they were gathered.
    /// </summary>
    public int Rank => Array.IndexOf(InOrder, Code);

    /// <summary>The codes, in the order the list is stated in: a reason unknown to it stands last, keeping the others' order.</summary>
    private static readonly string[] InOrder =
    [
        "record-unreadable", "record-newer", "leg-unsweepable", "never-swept", "last-not-passed", "not-keyed", "judge-revised",
        "rows-changed", "text-moved", "site-moved", "files-moved", "run-reads-moved", "build-environment-changed", "run-settings-changed",
        "judge-settings-changed", "host-changed", "lines-changed", "set-changed", "made-moved", "fetched-moved", "outside-moved",
        "compiler-changed", "environment-changed", "key-unreadable", "key-moved",
    ];

    /// <summary>The words a leg's count of its pending arms groups an arm pending for this reason by: <c>20 never swept</c>.</summary>
    public string Counted { get; private init; } = string.Empty;

    /// <summary>
    /// The reason as an arm's row in a table says it: as a line says it, but for a reason said once above the table, which
    /// each row then points at.
    /// </summary>
    public string Row
    {
        get => field ?? Said;
        private init;
    }

    /// <summary>The target the reason names, where it names one.</summary>
    public string? Target { get; private init; }

    /// <summary>The paths of the tree the reason names, where it names any, as the record spells them.</summary>
    public IReadOnlyList<string> Paths { get; private init; } = [];

    /// <summary>The leg's record of verdicts cannot be read.</summary>
    public static PendingReason RecordUnreadable(string why)
        => new("record-unreadable", $"the record of this leg cannot be read: {why}") { Counted = "whose record cannot be read" };

    /// <summary>The leg's record of verdicts was written by a newer build.</summary>
    public static PendingReason RecordNewer { get; } = new("record-newer", $"the record of this leg was written by a newer {ToolPackage.Id}")
    {
        Counted = $"whose record a newer {ToolPackage.Id} wrote",
    };

    /// <summary>The leg cannot be swept, as a sweep's refusal says it.</summary>
    public static PendingReason LegUnsweepable(string why)
        => new("leg-unsweepable", $"this leg cannot be swept: {why}") { Counted = "on a leg that cannot be swept", Row = "this leg cannot be swept, as said above" };

    /// <summary>The record holds no verdict of the arm on the leg.</summary>
    public static PendingReason NeverSwept { get; } = new("never-swept", "never swept on this leg") { Counted = "never swept" };

    /// <summary>The verdict the reason is read against is not a passed.</summary>
    public static PendingReason LastNotPassed(string verdict)
        => new("last-not-passed", $"its last verdict is {verdict}, not passed") { Counted = "last read otherwise" };

    /// <summary>The verdict recorded has no key: what it was judged on could not be read when it was swept.</summary>
    public static PendingReason NotKeyed(string why)
        => new("not-keyed", $"what it was judged on could not be read when it was swept: {why}") { Counted = "whose key was not read" };

    /// <summary>The judge's rules changed between the two.</summary>
    public static PendingReason JudgeRevised(int then, int now)
        => new("judge-revised", string.Create(CultureInfo.InvariantCulture, $"the judge's rules changed since (revision {then}, now {now})"))
        {
            Counted = "judged by other rules",
        };

    /// <summary>Rows of the given kinds changed.</summary>
    public static PendingReason RowsChanged(IReadOnlyList<string> kinds)
        => new("rows-changed", $"its {string.Join(", ", kinds)} rows changed") { Counted = "whose rows changed" };

    /// <summary>A text the arm cites holds something else, or is gone.</summary>
    public static PendingReason TextMoved(IReadOnlyList<string> paths)
        => new("text-moved", $"a text it cites moved: {ReportText.Listed(paths)}") { Counted = "whose texts moved", Paths = paths };

    /// <summary>A site of the arm holds something else, or is gone.</summary>
    public static PendingReason SiteMoved(IReadOnlyList<string> paths)
        => new("site-moved", $"its site moved: {ReportText.Listed(paths)}") { Counted = "whose sites moved", Paths = paths };

    /// <summary>Files of the tree a target is built from hold something else, or - where <paramref name="gone"/> names them - are gone from the tree.</summary>
    public static PendingReason FilesMoved(string target, IReadOnlyList<string> paths, IReadOnlyCollection<string>? gone = null)
        => new(
            "files-moved",
            $"what '{target}' is built from moved: {ReportText.Listed([.. paths.Select(path => gone?.Contains(path) == true ? $"{path} (gone)" : path)])}")
        {
            Counted = "whose files moved",
            Target = target,
            Paths = paths,
        };

    /// <summary>A file the arm's runner reads at run time holds something else, joined or left what it reads, or is gone.</summary>
    public static PendingReason RunReadsMoved(IReadOnlyList<string> paths)
        => new("run-reads-moved", $"what its runner reads at run time moved: {ReportText.Listed(paths)}") { Counted = "whose run-time reads moved", Paths = paths };

    /// <summary>The environment the build is given changed.</summary>
    public static PendingReason BuildEnvironmentChanged { get; } = new("build-environment-changed", "the environment its build is given changed")
    {
        Counted = "whose settings changed",
    };

    /// <summary>How the run starts changed.</summary>
    public static PendingReason RunSettingsChanged { get; } = new("run-settings-changed", "how its run starts changed") { Counted = "whose settings changed" };

    /// <summary>What the run is judged by changed.</summary>
    public static PendingReason JudgeSettingsChanged { get; } = new("judge-settings-changed", "what its run is judged by changed") { Counted = "whose settings changed" };

    /// <summary>The arm was judged on another host than the leg runs on.</summary>
    public static PendingReason HostChanged(string then, string now)
        => new("host-changed", $"it was judged on {then}, and this leg runs on {now}") { Counted = "judged on another host" };

    /// <summary>A target's build lines changed.</summary>
    public static PendingReason LinesChanged(string target)
        => new("lines-changed", $"how '{target}' is built changed") { Counted = "built otherwise", Target = target };

    /// <summary>A file joined or left what a target is built from.</summary>
    public static PendingReason SetChanged(string target)
        => new("set-changed", $"a file joined or left what '{target}' is built from") { Counted = "whose files joined or left", Target = target };

    /// <summary>A file a worker's configure made, and a target's build reads, holds something else.</summary>
    public static PendingReason MadeMoved(string directory)
        => new("made-moved", $"a file its build makes moved, in {directory}") { Counted = "whose made files moved" };

    /// <summary>The sources of a dependency the worker was given hold something else.</summary>
    public static PendingReason FetchedMoved(string dependency)
        => new("fetched-moved", $"the sources of '{dependency}' its build fetched moved") { Counted = "whose fetched sources moved" };

    /// <summary>A file outside the tree a target's build reads holds something else.</summary>
    public static PendingReason OutsideMoved(string directory)
        => new("outside-moved", $"a file outside the tree its build reads moved, in {directory}") { Counted = "whose files outside the tree moved" };

    /// <summary>A compiler changed.</summary>
    public static PendingReason CompilerChanged(string language, string then, string now)
        => new("compiler-changed", $"its compiler changed: {language} {then}, now {now}") { Counted = "whose compiler changed" };

    /// <summary>The developer environment changed.</summary>
    public static PendingReason EnvironmentChanged { get; } = new("environment-changed", "its developer environment changed")
    {
        Counted = "whose developer environment changed",
    };

    /// <summary>What the arm would be judged on could not all be read.</summary>
    public static PendingReason KeyUnreadable(string why)
        => new("key-unreadable", $"what it would be judged on cannot be read now: {why}") { Counted = "whose key cannot be read now" };

    /// <summary>
    /// The key is not the one it is compared with, and no other reason names how: the reason that closes the list, so two
    /// keys that differ are never said to be one.
    /// </summary>
    public static PendingReason KeyMoved { get; } = new("key-moved", "its key moved, in no part another reason names") { Counted = "whose key moved" };

    /// <summary>
    /// Arms pending, counted by the first reason each is pending for, in the words a count groups them by - the largest
    /// group first, groups of one size in the order they first came: <c>9 never swept, 2 whose files moved</c>.
    /// </summary>
    /// <param name="firsts">The first reason each arm is pending for.</param>
    public static string Grouped(IEnumerable<PendingReason> firsts)
    {
        ArgumentNullException.ThrowIfNull(firsts);

        return string.Join(
            ", ",
            firsts
                .GroupBy(reason => reason.Counted, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Count().ToString(CultureInfo.InvariantCulture)} {group.Key}"));
    }
}

/// <summary>
/// What a listing reads, on the machine it is typed on, of what an arm would be judged on now: the judge's revision, its
/// rows, what the tree's one reading holds of each text it cites and each site it mutates and of every file of the tree its
/// recorded tables name, the settings, and the host where the leg names one. Never a compiler, a build line, a file a
/// build makes or one outside the tree: those are read only where the leg is swept.
/// </summary>
/// <param name="Judge">The judge's revision (<see cref="ArmJudge.Revision"/>).</param>
/// <param name="Rows">What the sweep and the judge would read of the arm's rows.</param>
/// <param name="Texts">Each text the arm cites, as its rows spell it, with what the reading holds of it; nothing where it holds none.</param>
/// <param name="Sites">Each site the arm mutates, as its rows spell it, with what the reading holds of it; nothing where it holds none.</param>
/// <param name="Tree">The tree's one reading.</param>
/// <param name="RunReads">Each file the arm's runner reads at run time, as the reading holds it (<see cref="JudgedOn.RunReadsOf"/>).</param>
/// <param name="Settings">
/// What of the configuration the build, the run and the judge would be given on a host, as a dispatcher names it: the
/// environment the build is given holds what that host declares.
/// </param>
/// <param name="Host">The host the leg names, as a dispatcher names it; <see langword="null"/> where it names none.</param>
internal sealed record ReadHere(
    int Judge,
    RowDigests Rows,
    IReadOnlyDictionary<string, string?> Texts,
    IReadOnlyDictionary<string, string?> Sites,
    TreeFiles Tree,
    IReadOnlyDictionary<string, string> RunReads,
    Func<string, SweepSettings> Settings,
    string? Host);

/// <summary>
/// The one place that names every difference between what an arm was judged on and what it would be judged on now, for
/// every reader of a key: the self-test comparing its workers, a listing comparing the record with what it reads here, and
/// each reader after them. Each part is compared by one rule, whoever asks.
/// </summary>
internal static class KeyDifference
{
    /// <summary>Every reason <paramref name="now"/> is not <paramref name="then"/>, in the order a line names them; none where they are one.</summary>
    /// <param name="then">What the arm was judged on.</param>
    /// <param name="now">What it would be judged on now, every part read.</param>
    public static IReadOnlyList<PendingReason> Between(ArmKey then, ArmKey now)
    {
        ArgumentNullException.ThrowIfNull(then);
        ArgumentNullException.ThrowIfNull(now);

        return (now.Unread ?? then.Unread) is { } why
            ? [PendingReason.KeyUnreadable(why)]
            : Closed(Between(then.JudgedOn, then.Tables, now), then.Key, now.Key);
    }

    /// <summary>
    /// <paramref name="reasons"/>, closed: where they are none and the two keys differ, the reason that the key moved - so
    /// two keys that differ are never said to be one, and nothing is pending for no reason. The one place that rule is.
    /// </summary>
    /// <param name="reasons">Every reason gathered.</param>
    /// <param name="then">The key compared with.</param>
    /// <param name="now">The key now.</param>
    public static IReadOnlyList<PendingReason> Closed(IReadOnlyList<PendingReason> reasons, string? then, string? now)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        return reasons.Count == 0 && !string.Equals(then, now, StringComparison.Ordinal) ? [PendingReason.KeyMoved] : reasons;
    }

    /// <summary>
    /// Every reason what a sweep reads of an arm now (<paramref name="now"/>) is not what it <paramref name="was"/> judged
    /// on, as the record keeps it, in the order a line names them: the parts as the record holds them, and each target's
    /// table the record could give (<paramref name="tables"/>) - a target whose table it could not give is compared by no
    /// part of its table. None where they are one.
    /// </summary>
    /// <param name="was">What the arm was judged on, as recorded.</param>
    /// <param name="tables">The tables of it the record could give, by target.</param>
    /// <param name="now">What it would be judged on now, as a worker's build read it.</param>
    public static IReadOnlyList<PendingReason> Between(JudgedOn was, IReadOnlyDictionary<string, BuiltFromTable> tables, ArmKey now)
    {
        ArgumentNullException.ThrowIfNull(was);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(now);

        if (now.Unread is { } why)
        {
            return [PendingReason.KeyUnreadable(why)];
        }

        var @is = now.JudgedOn;
        var reasons = new List<PendingReason>();
        var compared = Tables(tables, now.Tables);

        Read(was, @is.Judge, @is.Rows, @is.Texts, @is.Sites, reasons);

        foreach (var (target, before, after) in compared)
        {
            // A file both read that holds something else; one that joined or left is the set's, which only a build reads.
            if (Moved(before.Files, after.Files).Where(path => before.Files.ContainsKey(path) && after.Files.ContainsKey(path)).ToList() is { Count: > 0 } files)
            {
                reasons.Add(PendingReason.FilesMoved(target, files));
            }
        }

        RunRead(was.RunReads, @is.RunReads, reasons);
        Configured(was, @is.Settings, @is.Host, reasons);

        foreach (var (target, before, after) in compared)
        {
            if (before.Lines != after.Lines)
            {
                reasons.Add(PendingReason.LinesChanged(target));
            }

            if (!before.Files.Keys.Order(StringComparer.Ordinal).SequenceEqual(after.Files.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                reasons.Add(PendingReason.SetChanged(target));
            }

            reasons.AddRange(Moved(before.Made, after.Made).Select(PendingReason.MadeMoved));
            reasons.AddRange(Moved(before.Fetched, after.Fetched).Select(PendingReason.FetchedMoved));
            reasons.AddRange(Moved(before.Outside, after.Outside).Select(PendingReason.OutsideMoved));
        }

        foreach (var language in was.Compilers.Select(compiler => compiler.Language).Union(@is.Compilers.Select(compiler => compiler.Language), StringComparer.Ordinal))
        {
            var (before, after) = Described(
                was.Compilers.FirstOrDefault(compiler => compiler.Language == language),
                @is.Compilers.FirstOrDefault(compiler => compiler.Language == language));

            if (before != after)
            {
                reasons.Add(PendingReason.CompilerChanged(language, before, after));
            }
        }

        if (was.DeveloperEnvironment != @is.DeveloperEnvironment)
        {
            reasons.Add(PendingReason.EnvironmentChanged);
        }

        return reasons;
    }

    /// <summary>
    /// Every reason what a listing reads here (<paramref name="now"/>) is not what the arm <paramref name="was"/> judged on, in
    /// the order a line names them: the parts a listing reads, each by the rule a sweep compares it by, and of each table
    /// the files of the tree it names, as the reading holds them now - a file the reading no longer holds is gone, and
    /// moved. The settings are those of the host the leg names, or - where it names none, so which host a sweep would
    /// place it on is not read here - of the host it was judged on, as the host is compared. None where nothing read here
    /// moved, which is never to say the arm holds.
    /// </summary>
    /// <param name="was">What the arm was judged on, as recorded.</param>
    /// <param name="tables">The tables of it the record could give, by target.</param>
    /// <param name="now">What the listing read here.</param>
    public static IReadOnlyList<PendingReason> Listed(JudgedOn was, IReadOnlyDictionary<string, BuiltFromTable> tables, ReadHere now)
    {
        ArgumentNullException.ThrowIfNull(was);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(now);

        var reasons = new List<PendingReason>();

        Read(was, now.Judge, now.Rows, now.Texts, now.Sites, reasons);

        foreach (var (target, table) in tables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var moved = table.Files
                .Where(file => !string.Equals(now.Tree.HashOf(file.Key), file.Value, StringComparison.Ordinal))
                .Select(file => file.Key)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (moved.Count > 0)
            {
                reasons.Add(PendingReason.FilesMoved(target, moved, [.. moved.Where(path => now.Tree.Find(path) is null)]));
            }
        }

        var host = now.Host ?? was.Host;

        RunRead(was.RunReads, now.RunReads, reasons);
        Configured(was, now.Settings(host), host, reasons);

        return reasons;
    }

    /// <summary>
    /// The reasons of the parts the registry and the tree's reading say: the judge's revision, each kind of row, each text
    /// and each site.
    /// </summary>
    private static void Read(
        JudgedOn was,
        int judge,
        RowDigests rows,
        IReadOnlyDictionary<string, string?> texts,
        IReadOnlyDictionary<string, string?> sites,
        List<PendingReason> reasons)
    {
        if (was.Judge != judge)
        {
            reasons.Add(PendingReason.JudgeRevised(was.Judge, judge));
        }

        string[] kinds =
        [
            .. new[] { ("A", was.Rows.A, rows.A), ("C", was.Rows.C, rows.C), ("G", was.Rows.G, rows.G), ("B", was.Rows.B, rows.B), ("M", was.Rows.M, rows.M) }
                .Where(row => !string.Equals(row.Item2, row.Item3, StringComparison.Ordinal))
                .Select(row => row.Item1),
        ];

        if (kinds.Length > 0)
        {
            reasons.Add(PendingReason.RowsChanged(kinds));
        }

        if (Moved(was.Texts, texts) is { Count: > 0 } movedTexts)
        {
            reasons.Add(PendingReason.TextMoved(movedTexts));
        }

        if (Moved(was.Sites, sites) is { Count: > 0 } movedSites)
        {
            reasons.Add(PendingReason.SiteMoved(movedSites));
        }
    }

    /// <summary>
    /// The reason of what the runner reads at run time: each file that holds something else, or that joined or left what
    /// it reads, its paths compared as the key spells them - as the tree's reading spells each - so a file the reading
    /// spells otherwise, renamed by case alone where the tree's file system folds case, is one that left and one that
    /// joined, as a file of a target's table spelt otherwise changes what the target is built from.
    /// </summary>
    private static void RunRead(IReadOnlyDictionary<string, string> was, IReadOnlyDictionary<string, string> now, List<PendingReason> reasons)
    {
        if (Moved(was, now) is { Count: > 0 } moved)
        {
            reasons.Add(PendingReason.RunReadsMoved(moved));
        }
    }

    /// <summary>The reasons of the parts the configuration says: the build's, the run's and the judge's settings, and the host.</summary>
    private static void Configured(JudgedOn was, SweepSettings settings, string host, List<PendingReason> reasons)
    {
        if (was.Settings.Build != settings.Build)
        {
            reasons.Add(PendingReason.BuildEnvironmentChanged);
        }

        if (was.Settings.Run != settings.Run)
        {
            reasons.Add(PendingReason.RunSettingsChanged);
        }

        if (was.Settings.Judge != settings.Judge)
        {
            reasons.Add(PendingReason.JudgeSettingsChanged);
        }

        if (!string.Equals(was.Host, host, StringComparison.Ordinal))
        {
            reasons.Add(PendingReason.HostChanged(was.Host, host));
        }
    }

    /// <summary>Each target both hold a table of, with its two tables, in the order of its name.</summary>
    private static List<(string Target, BuiltFromTable Before, BuiltFromTable After)> Tables(
        IReadOnlyDictionary<string, BuiltFromTable> then,
        IReadOnlyDictionary<string, BuiltFromTable> now)
        => [.. then.Keys
            .Where(now.ContainsKey)
            .Order(StringComparer.Ordinal)
            .Select(target => (target, then[target], now[target]))];

    /// <summary>Each key of the two maps whose value differs, or that only one holds, in ordinal order - keys compared ordinally, as a key spells them.</summary>
    private static List<string> Moved<T>(IReadOnlyDictionary<string, T> before, IReadOnlyDictionary<string, T> after)
        => [.. before.Keys
            .Union(after.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(key => !(before.TryGetValue(key, out var was) && after.TryGetValue(key, out var @is) && Equals(was, @is)))];

    /// <summary>
    /// The two compilers as a line says each: its id and version - and its program, then its words, only where those
    /// alone would say the same of both - or <c>none</c> where there is no compiler of that language.
    /// </summary>
    private static (string Before, string After) Described(JudgedCompiler? before, JudgedCompiler? after)
    {
        static string Named(JudgedCompiler? compiler, int detail) => compiler is null ? "none"
            : detail switch
            {
                0 => $"{compiler.Id} {compiler.Version}",
                1 => $"{compiler.Id} {compiler.Version} '{compiler.Program}'",
                _ => $"{compiler.Id} {compiler.Version} '{compiler.Program}' {string.Join(" ", compiler.Arguments)}".TrimEnd(),
            };

        for (var detail = 0; detail < 2; detail++)
        {
            if (Named(before, detail) != Named(after, detail))
            {
                return (Named(before, detail), Named(after, detail));
            }
        }

        return (Named(before, 2), Named(after, 2));
    }
}
