using System.Text.Json;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// How a value is digested where two of them must compare by what they say: its JSON written one way - the options every
/// state file is written with, on one line, each map sorted by its keys as ordinal strings - and the SHA-256 of those
/// bytes, as <see cref="FileContentHash"/> hashes any bytes.
/// </summary>
internal static class CanonicalJson
{
    /// <summary>The SHA-256 of <paramref name="value"/>'s canonical JSON.</summary>
    /// <param name="value">The value, each map in it already sorted (<see cref="Sorted{T}"/>).</param>
    public static string Digest<T>(T value) => FileContentHash.Of(JsonSerializer.SerializeToUtf8Bytes(value, JsonStateFile.LineOptions));

    /// <summary><paramref name="map"/> sorted by its keys as ordinal strings, as canonical JSON writes a map.</summary>
    /// <param name="map">The map.</param>
    public static IReadOnlyDictionary<string, T> Sorted<T>(IEnumerable<KeyValuePair<string, T>> map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var sorted = new SortedDictionary<string, T>(StringComparer.Ordinal);

        foreach (var (key, value) in map)
        {
            sorted[key] = value;
        }

        return sorted;
    }
}

/// <summary>One digest for each kind of row the sweep and the judge read of an arm, or nothing where it has no such row.</summary>
/// <remarks>A kind the arm has no row of is left out where the digests are written, and read back as nothing.</remarks>
/// <param name="A">Its A row: the site, the before- and after-texts, the red kind, the target, the runner, the cases and the diagnostic.</param>
/// <param name="C">Its C rows' cases, sorted.</param>
/// <param name="G">Its G rows' cases, sorted.</param>
/// <param name="B">Its B row: the paired control's before- and after-texts.</param>
/// <param name="M">Its M rows, each its site and its texts, in the order declared.</param>
public sealed record RowDigests(string A, string? C = null, string? G = null, string? B = null, string? M = null);

/// <summary>A compiler as an arm's key holds it: what CMake identified in the worker's build.</summary>
/// <param name="Language">The language, as CMake names it.</param>
/// <param name="Id">CMake's id for the compiler.</param>
/// <param name="Version">Its version, as CMake identified it.</param>
/// <param name="Program">The compiler CMake runs, as its record names it, with the worker's own path told (<see cref="WorkerPath"/>).</param>
/// <param name="Arguments">The words CMake runs it with before its own, each with the worker's own path told.</param>
public sealed record JudgedCompiler(string Language, string Id, string Version, string Program, IReadOnlyList<string> Arguments);

/// <summary>
/// What an arm was judged on, in ten parts: everything this tool reads that a verdict of the judge's on that arm rests on.
/// Its key is the SHA-256 of them. Where all ten are what they were, nothing it reads has moved; what a test binary reads
/// at run time that <see cref="MutationSettings.RunReads"/> does not name is read by no part, so editing it moves no key.
/// </summary>
/// <remarks>
/// <para>
/// Effects, never causes: what a target is built from is its build lines and the files they read
/// (<see cref="BuiltFromTable"/>), never the list files and cache variables they were generated from, so an edit for
/// another target moves no arm of this one. No part is a commit, a date, a run, a tree or a worker: a worker made again
/// after a clean configures and builds the same content to the same key, and an arm judged in one tree holds in another
/// of the same content.
/// </para>
/// <para>
/// Not read of the registry: a row's why, its comments, its line numbers, the letter case of the arm's id, and the S row,
/// which says where an arm runs - and what an arm is judged on is kept leg by leg.
/// </para>
/// </remarks>
public sealed record JudgedOn
{
    /// <summary>The judge's revision (<see cref="ArmJudge.Revision"/>).</summary>
    public required int Judge { get; init; }

    /// <summary>What the sweep and the judge read of the arm's rows.</summary>
    public required RowDigests Rows { get; init; }

    /// <summary>Each text the arm cites, by its path as the rows spell it, with what the reading of the tree says it holds; nothing where it holds none.</summary>
    public required IReadOnlyDictionary<string, string?> Texts { get; init; }

    /// <summary>Each site the arm mutates, by its path as the rows spell it, with what the reading of the tree says it holds; nothing where it holds none.</summary>
    public required IReadOnlyDictionary<string, string?> Sites { get; init; }

    /// <summary>Each target the arm's build builds (<see cref="ArmJudge.Builds"/>), with its table's digest.</summary>
    public required IReadOnlyDictionary<string, string> Built { get; init; }

    /// <summary>Each compiler CMake identified in the worker's build.</summary>
    public required IReadOnlyList<JudgedCompiler> Compilers { get; init; }

    /// <summary>The developer environment the leg's toolchain names, as it was set up; nothing where it names none.</summary>
    public DeveloperEnvironmentFact? DeveloperEnvironment { get; init; }

    /// <summary>What of the configuration the build, the run and the judge are given (<see cref="SweepSettings"/>).</summary>
    public required SweepSettings Settings { get; init; }

    /// <summary>
    /// Each file of the tree the arm's runner reads at run time, as <see cref="MutationSettings.RunReads"/> names them for
    /// it, by its path as the reading of the tree spells it, with what the reading says it holds; nothing for a BUILD-RED
    /// arm, which runs nothing (<see cref="RunReadsOf"/>).
    /// </summary>
    public required IReadOnlyDictionary<string, string> RunReads { get; init; }

    /// <summary>The host the leg ran on, as the machine that dispatched it names it.</summary>
    public required string Host { get; init; }

    /// <summary>The key: the SHA-256 of the ten parts' canonical JSON.</summary>
    public string Key() => CanonicalJson.Digest(this with
    {
        Texts = CanonicalJson.Sorted(Texts),
        Sites = CanonicalJson.Sorted(Sites),
        Built = CanonicalJson.Sorted(Built),
        Compilers = [.. Compilers.OrderBy(compiler => compiler.Language, StringComparer.Ordinal)],
        RunReads = CanonicalJson.Sorted(RunReads),
    });

    /// <summary>
    /// What <paramref name="arm"/>'s runner reads at run time, of <paramref name="tree"/>: each file
    /// <paramref name="settings"/>' <c>runReads</c> names for that runner covers, with what the reading says it holds; nothing
    /// for a BUILD-RED arm, which runs nothing, or a runner it names nothing for.
    /// </summary>
    /// <param name="arm">The arm.</param>
    /// <param name="settings">The mutation settings the sweep runs by.</param>
    /// <param name="tree">The tree's one reading.</param>
    internal static IReadOnlyDictionary<string, string> RunReadsOf(MutationArm arm, MutationSettings settings, TreeFiles tree)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tree);

        return arm.Kind == RedKind.TestRed && settings.PatternsFor(arm.Runner) is { Count: > 0 } patterns
            ? tree.Matching(patterns)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// What the sweep and the judge read of <paramref name="arm"/>'s rows, one digest to a kind: A, its site, texts, red
    /// kind, target, runner, cases and diagnostic; C and G, their cases sorted, as the judge compares them as sets; B, the
    /// paired control's texts; M, each row's site and texts in the order declared, as each is found in the site as the
    /// tree holds it in that order. The id is no part of it: what is judged is kept by arm.
    /// </summary>
    /// <param name="arm">The arm.</param>
    public static RowDigests RowsOf(MutationArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);

        var further = arm.Own.Further
            .Select(text => (Site: arm.Own.Site, text.Before, text.After, text.Line))
            .Concat(arm.Coupled.SelectMany(site => site.Texts.Select(text => (site.Site, text.Before, text.After, text.Line))))
            .OrderBy(row => row.Line)
            .Select(row => new[] { row.Site, row.Before, row.After })
            .ToList();

        return new RowDigests(
            CanonicalJson.Digest(new[]
            {
                arm.Own.Site,
                arm.Own.Before,
                arm.Own.After,
                arm.Kind == RedKind.TestRed ? MutationRegistryParser.TestRed : MutationRegistryParser.BuildRed,
                arm.Target,
                arm.Runner,
                arm.Cases.ToString(System.Globalization.CultureInfo.InvariantCulture),
                arm.Diagnostic,
            }),
            arm.Reds.Count == 0 ? null : CanonicalJson.Digest(arm.Reds.Order(StringComparer.Ordinal)),
            arm.Greens.Count == 0 ? null : CanonicalJson.Digest(arm.Greens.Order(StringComparer.Ordinal)),
            arm.Control is { } control ? CanonicalJson.Digest(new[] { control.Before, control.After }) : null,
            further.Count == 0 ? null : CanonicalJson.Digest(further));
    }

    /// <summary>
    /// The texts <paramref name="arm"/> cites, each once, as its rows spell them: its A row's and each M row's before- and
    /// after-texts, a TEST-RED arm's diagnostic, and a BUILD-RED arm's paired control's.
    /// </summary>
    /// <param name="arm">The arm.</param>
    public static IReadOnlyList<string> TextsOf(MutationArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);

        IEnumerable<string> cited =
        [
            .. arm.Sites.SelectMany(site => site.Texts.SelectMany(text => new[] { text.Before, text.After })),
            .. arm.Kind == RedKind.TestRed ? [arm.Diagnostic] : Array.Empty<string>(),
            .. arm.Control is { } control ? [control.Before, control.After] : Array.Empty<string>(),
        ];

        return [.. cited.Distinct(StringComparer.Ordinal)];
    }
}

/// <summary>
/// What made a leg's build lines what they are, as a worker's build of it read them: kept beside each arm's key and never
/// in it, so that what moved can be said of an arm whose key holds nothing of it.
/// </summary>
/// <remarks>
/// Causes, never effects: an arm's key holds what the build of each of its targets read, so a list file edited for
/// another target moves nothing of it. Kept in the key, every arm of a leg would be pending after every edit of any list
/// file.
/// </remarks>
/// <param name="GeneratedFrom">
/// Each file of the tree the leg's build manifest is generated from - an input of the build line that makes
/// <c>build.ninja</c>, which the sweep's reading of the tree holds - by its path as the reading spells it, with what it holds.
/// </param>
/// <param name="Generation">The digest of what the configuration gives the configure (<see cref="GenerationOf"/>).</param>
public sealed record JudgedCauses(IReadOnlyDictionary<string, string> GeneratedFrom, string Generation)
{
    /// <summary>The build line that makes the manifest, by the file it makes, relative to the build directory.</summary>
    public const string Manifest = "build.ninja";

    /// <summary>
    /// The digest of what <paramref name="config"/> gives the configure of <paramref name="project"/> as
    /// <paramref name="variant"/> builds it: the generator its toolchain declares, the build type its configuration
    /// declares, the cache variables its overlay sets, and the project's path - the generator and the build type asked of
    /// the adapter that configures and builds the project, which owns them both.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="project">The project its workers build.</param>
    public static string GenerationOf(HarnessConfig config, VariantKey variant, ProjectConfig project)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(project);

        var adapter = Build.BuildAdapters.For(project.Type);

        return CanonicalJson.Digest(new
        {
            Generator = adapter.GeneratorOf(config, variant.Toolchain),
            BuildType = adapter.BuildTypeOf(config, variant.Config),
            CacheVars = CanonicalJson.Sorted(variant.Overlay(config, project).CacheVars),
            project.Path,
        });
    }
}

/// <summary>
/// The files each runner's runs read at run time, of one reading of a tree, each runner read once however many arms run
/// it: what <see cref="JudgedOn.RunReadsOf"/> says, beside each entry of <c>runReads</c> that covers no file of the
/// reading - which, like a text the arm cites that the reading lacks, leaves every arm that runner runs with no key,
/// rather than a key that silently reads none of what its runs do.
/// </summary>
internal sealed class RunReadsOf(MutationSettings settings, TreeFiles tree)
{
    private readonly Dictionary<string, (IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> Uncovered)> _of = new(StringComparer.Ordinal);

    /// <summary>What <paramref name="arm"/>'s runner reads at run time, and which entries of <c>runReads</c> cover no file of the tree.</summary>
    /// <param name="arm">The arm.</param>
    public (IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> Uncovered) Of(MutationArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);

        var runner = arm.Kind == RedKind.TestRed ? arm.Runner : string.Empty;

        if (_of.TryGetValue(runner, out var read))
        {
            return read;
        }

        var files = JudgedOn.RunReadsOf(arm, settings, tree);
        var uncovered = new List<string>();

        // The union is what the arm is judged on; each entry on its own says whether it covers anything, so one that
        // names nothing is named rather than silently holding none of what the runner reads.
        foreach (var pattern in settings.PatternsFor(arm.Runner) ?? [])
        {
            if (tree.Matching([pattern]).Count == 0)
            {
                uncovered.Add(pattern);
            }
        }

        return _of[runner] = (files, (IReadOnlyList<string>)[.. uncovered]);
    }
}

/// <summary>
/// What one arm would be judged on as one worker's build reads it, with the tables of the targets its build builds, and
/// its key; or, where a part could not be read, why - an arm with no key never holds. Made one way (<see cref="Of"/>), so
/// what each target is built from and its table are one, and its key is of what it holds.
/// </summary>
internal sealed class ArmKey
{
    private ArmKey(JudgedOn judgedOn, IReadOnlyDictionary<string, BuiltFromTable> tables, string? unread, JudgedCauses? causes)
    {
        JudgedOn = judgedOn;
        Tables = tables;
        Unread = unread;
        Causes = causes;
        Key = unread is null ? judgedOn.Key() : null;
    }

    /// <summary>The ten parts, as far as each could be read: a target whose table could not be made is in none of them.</summary>
    public JudgedOn JudgedOn { get; }

    /// <summary>The table of each target its build builds whose table could be made.</summary>
    public IReadOnlyDictionary<string, BuiltFromTable> Tables { get; }

    /// <summary>Why a part could not be read, as what follows "cannot be read now: "; <see langword="null"/> where all ten were.</summary>
    public string? Unread { get; }

    /// <summary>The key, or <see langword="null"/> where a part could not be read.</summary>
    public string? Key { get; }

    /// <summary>What made the leg's build lines what they are, as the same worker's build read them; kept beside the key.</summary>
    public JudgedCauses? Causes { get; }

    /// <summary>
    /// What an arm is judged on: <paramref name="parts"/>, but for what each target its build builds is built from, which
    /// <paramref name="built"/> gives - each target whose table could be made, by its digest, whatever
    /// <paramref name="parts"/> holds of it - read whole unless a reading made no table, or <paramref name="unread"/> says
    /// why another part could not be read.
    /// </summary>
    /// <param name="parts">The other nine parts.</param>
    /// <param name="built">The reading of each target its build builds.</param>
    /// <param name="unread">Why a part other than a table could not be read, or <see langword="null"/>.</param>
    /// <param name="causes">What made the leg's build lines what they are, kept beside the key.</param>
    public static ArmKey Of(JudgedOn parts, IReadOnlyList<BuiltFromReading> built, string? unread = null, JudgedCauses? causes = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(built);

        var read = built.Where(reading => reading.Table is not null).ToList();

        return new ArmKey(
            parts with { Built = read.ToDictionary(reading => reading.Target, reading => reading.Digest!, StringComparer.Ordinal) },
            read.ToDictionary(reading => reading.Target, reading => reading.Table!, StringComparer.Ordinal),
            built.Select(reading => reading.Unread).FirstOrDefault(why => why is not null) ?? unread,
            causes);
    }
}
