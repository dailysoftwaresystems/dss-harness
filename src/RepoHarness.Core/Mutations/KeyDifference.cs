using System.Globalization;
using RepoHarness.Core.Output;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// Why what an arm is judged on is not what it was: one of a closed list, each with its code and the words a line says it
/// in. The list is ordered as a reader asks - what the judge and the registry say first, then the tree, the settings and
/// the host, then what only a build in a worker reads - and the first that applies is the one a line names.
/// </summary>
/// <param name="Code">The reason, as data names it: <c>files-moved</c>.</param>
/// <param name="Said">The reason, as a line says it: <c>what 'lex_tests' is built from moved: tests/lex/a_test.cpp</c>.</param>
public sealed record PendingReason(string Code, string Said)
{
    /// <summary>The judge's rules changed between the two.</summary>
    public static PendingReason JudgeRevised(int then, int now)
        => new("judge-revised", string.Create(CultureInfo.InvariantCulture, $"the judge's rules changed since (revision {then}, now {now})"));

    /// <summary>Rows of the given kinds changed.</summary>
    public static PendingReason RowsChanged(IReadOnlyList<string> kinds) => new("rows-changed", $"its {string.Join(", ", kinds)} rows changed");

    /// <summary>A text the arm cites holds something else, or is gone.</summary>
    public static PendingReason TextMoved(IReadOnlyList<string> paths) => new("text-moved", $"a text it cites moved: {ReportText.Listed(paths)}");

    /// <summary>A site of the arm holds something else, or is gone.</summary>
    public static PendingReason SiteMoved(IReadOnlyList<string> paths) => new("site-moved", $"its site moved: {ReportText.Listed(paths)}");

    /// <summary>A file of the tree a target is built from holds something else, or is gone.</summary>
    public static PendingReason FilesMoved(string target, IReadOnlyList<string> paths)
        => new("files-moved", $"what '{target}' is built from moved: {ReportText.Listed(paths)}");

    /// <summary>The environment the build is given changed.</summary>
    public static PendingReason BuildEnvironmentChanged { get; } = new("build-environment-changed", "the environment its build is given changed");

    /// <summary>How the run starts changed.</summary>
    public static PendingReason RunSettingsChanged { get; } = new("run-settings-changed", "how its run starts changed");

    /// <summary>What the run is judged by changed.</summary>
    public static PendingReason JudgeSettingsChanged { get; } = new("judge-settings-changed", "what its run is judged by changed");

    /// <summary>The arm was judged on another host than the leg runs on.</summary>
    public static PendingReason HostChanged(string then, string now) => new("host-changed", $"it was judged on {then}, and this leg runs on {now}");

    /// <summary>A target's build lines changed.</summary>
    public static PendingReason LinesChanged(string target) => new("lines-changed", $"how '{target}' is built changed");

    /// <summary>A file joined or left what a target is built from.</summary>
    public static PendingReason SetChanged(string target) => new("set-changed", $"a file joined or left what '{target}' is built from");

    /// <summary>A file a worker's configure made, and a target's build reads, holds something else.</summary>
    public static PendingReason MadeMoved(string directory) => new("made-moved", $"a file its build makes moved, in {directory}");

    /// <summary>The sources of a dependency the worker was given hold something else.</summary>
    public static PendingReason FetchedMoved(string dependency) => new("fetched-moved", $"the sources of '{dependency}' its build fetched moved");

    /// <summary>A file outside the tree a target's build reads holds something else.</summary>
    public static PendingReason OutsideMoved(string directory) => new("outside-moved", $"a file outside the tree its build reads moved, in {directory}");

    /// <summary>A compiler changed.</summary>
    public static PendingReason CompilerChanged(string language, string then, string now) => new("compiler-changed", $"its compiler changed: {language} {then}, now {now}");

    /// <summary>The developer environment changed.</summary>
    public static PendingReason EnvironmentChanged { get; } = new("environment-changed", "its developer environment changed");

    /// <summary>What the arm would be judged on could not all be read.</summary>
    public static PendingReason KeyUnreadable(string why) => new("key-unreadable", $"what it would be judged on cannot be read now: {why}");
}

/// <summary>
/// The one function that names every difference between what an arm was judged on and what it would be judged on now,
/// for every reader of a key: the self-test comparing its workers, as each reader after it.
/// </summary>
internal static class KeyDifference
{
    /// <summary>Every reason <paramref name="now"/> is not <paramref name="then"/>, in the order a line names them; none where they are one.</summary>
    /// <param name="then">What the arm was judged on.</param>
    /// <param name="now">What it would be judged on now.</param>
    public static IReadOnlyList<PendingReason> Between(ArmKey then, ArmKey now)
    {
        ArgumentNullException.ThrowIfNull(then);
        ArgumentNullException.ThrowIfNull(now);

        if ((now.Unread ?? then.Unread) is { } why)
        {
            return [PendingReason.KeyUnreadable(why)];
        }

        var was = then.JudgedOn;
        var @is = now.JudgedOn;
        var reasons = new List<PendingReason>();

        if (was.Judge != @is.Judge)
        {
            reasons.Add(PendingReason.JudgeRevised(was.Judge, @is.Judge));
        }

        string[] kinds =
        [
            .. new[] { ("A", was.Rows.A, @is.Rows.A), ("C", was.Rows.C, @is.Rows.C), ("G", was.Rows.G, @is.Rows.G), ("B", was.Rows.B, @is.Rows.B), ("M", was.Rows.M, @is.Rows.M) }
                .Where(row => !string.Equals(row.Item2, row.Item3, StringComparison.Ordinal))
                .Select(row => row.Item1),
        ];

        if (kinds.Length > 0)
        {
            reasons.Add(PendingReason.RowsChanged(kinds));
        }

        if (Moved(was.Texts, @is.Texts) is { Count: > 0 } texts)
        {
            reasons.Add(PendingReason.TextMoved(texts));
        }

        if (Moved(was.Sites, @is.Sites) is { Count: > 0 } sites)
        {
            reasons.Add(PendingReason.SiteMoved(sites));
        }

        var tables = Tables(then, now);

        foreach (var (target, before, after) in tables)
        {
            // A file both read that holds something else; one that joined or left is the set's, which only a build reads.
            if (Moved(before.Files, after.Files).Where(path => before.Files.ContainsKey(path) && after.Files.ContainsKey(path)).ToList() is { Count: > 0 } files)
            {
                reasons.Add(PendingReason.FilesMoved(target, files));
            }
        }

        if (was.Settings.Build != @is.Settings.Build)
        {
            reasons.Add(PendingReason.BuildEnvironmentChanged);
        }

        if (was.Settings.Run != @is.Settings.Run)
        {
            reasons.Add(PendingReason.RunSettingsChanged);
        }

        if (was.Settings.Judge != @is.Settings.Judge)
        {
            reasons.Add(PendingReason.JudgeSettingsChanged);
        }

        if (!string.Equals(was.Host, @is.Host, StringComparison.Ordinal))
        {
            reasons.Add(PendingReason.HostChanged(was.Host, @is.Host));
        }

        foreach (var (target, before, after) in tables)
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

    /// <summary>Each target both keys hold a table of, with its two tables, in the order of its name.</summary>
    private static List<(string Target, BuiltFromTable Before, BuiltFromTable After)> Tables(ArmKey then, ArmKey now)
        => [.. then.Tables.Keys
            .Where(now.Tables.ContainsKey)
            .Order(StringComparer.Ordinal)
            .Select(target => (target, then.Tables[target], now.Tables[target]))];

    /// <summary>Each key of the two maps whose value differs, or that only one holds, in ordinal order.</summary>
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
