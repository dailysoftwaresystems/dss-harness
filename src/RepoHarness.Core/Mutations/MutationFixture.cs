using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// The fixture <c>check-mutations --self-test</c> sweeps: a CMake library, a test binary that writes its own JUnit report,
/// and a registry of an arm to each verdict an arm's design can reach on any machine - one whose mutation is coupled
/// across two files, and one whose mutation is two sites of one file - each held to that verdict. Embedded, so every
/// DssHarness carries the very fixture its own tests swept.
/// </summary>
/// <remarks>
/// <para>
/// Swept as a leg's own project is - in workers of its own, through the leg's toolchain, configuration and sanitizer - so
/// what a self-test proves is that this tool judges each arm as designed on that machine, with that compiler. An arm judged
/// otherwise is this tool's defect, never the fixture's, and is reported violated, naming both verdicts.
/// </para>
/// <para>
/// Kept among this user's own data for this tool (<see cref="MutationFixtureStore"/>): a fixture written once is the one
/// every later self-test on the machine finds, whichever repository asks. Each file is written only where it differs, so
/// a self-test of the same build of this tool leaves it as it was. Its workers are kept apart from it, beside the tree of
/// the leg self-tested (<see cref="WorkerFamily.SelfTestMark"/>): they are that repository's, built by its toolchain,
/// their builds warm for its next self-test, and removed with its own.
/// </para>
/// </remarks>
public static class MutationFixture
{
    /// <summary>What each of its files is embedded under, followed by the file's path in the fixture.</summary>
    public const string ResourcePrefix = "RepoHarness.Core.MutationFixture/";

    /// <summary>The directory it is kept in, among this user's own data for this tool.</summary>
    public const string DirectoryName = "mutation-fixture";

    /// <summary>
    /// The longest path its build makes below its build directory - a file of CMake's own check of a compiler, the deepest
    /// of them - with a little to spare, which a worker's paths are reckoned with in place of
    /// <c>worktrees.pathBudgetReserve</c>, the leg's own project's.
    /// </summary>
    public const int LongestBuildPath = 100;

    /// <summary>
    /// The project it is: built by CMake, from its root, its build witnessed by the test binary it links, as every whole
    /// build is witnessed by what its project says it makes. A new one each time it is asked for, so nothing one sweep
    /// does to it reaches another.
    /// </summary>
    public static ProjectConfig Project => new()
    {
        Name = DirectoryName,
        Type = "cmake",
        Path = ".",
        BuildOutputs = [BuildOutput.Keyed([new(PlatformNames.Windows, "fixture_tests.exe"), new(PlatformScope.Every, "fixture_tests")])],
    };

    /// <summary>Each arm its registry declares, with the verdict that arm is designed to reach.</summary>
    public static IReadOnlyDictionary<string, LegVerdict> Designed { get; } = new Dictionary<string, LegVerdict>(StringComparer.Ordinal)
    {
        ["charge-bound"] = LegVerdict.Passed,
        ["depth-type"] = LegVerdict.Passed,
        ["depth-coupled"] = LegVerdict.Passed,
        ["slack-twofold"] = LegVerdict.Passed,
        ["floor-misdeclared"] = LegVerdict.Violated,
        ["spare-unseen"] = LegVerdict.Survived,
        ["sanity-lost"] = LegVerdict.Unattributed,
        ["depth-unlinked"] = LegVerdict.Failed,
    };

    /// <summary>The verdicts an arm's design decides between, which a self-test holds an arm to.</summary>
    private static readonly HashSet<LegVerdict> Decided =
    [
        LegVerdict.Passed,
        LegVerdict.Failed,
        LegVerdict.Violated,
        LegVerdict.Survived,
        LegVerdict.Unattributed,
        LegVerdict.Unwitnessed,
    ];

    /// <summary>
    /// The settings it is swept with: its own registry, texts and report argument, with the workers and the run-time factor
    /// <paramref name="configured"/> - the repository's own - gives every sweep.
    /// </summary>
    /// <param name="configured">The repository's mutation testing settings.</param>
    public static MutationSettings SettingsFor(MutationSettings configured)
    {
        ArgumentNullException.ThrowIfNull(configured);

        return new MutationSettings
        {
            Registry = "arms.txt",
            TextDirectory = "texts",
            ReportArgs = [$"--report={{{MutationReport.Placeholder}}}"],
            Workers = configured.Workers,
            RunTimeFactor = configured.RunTimeFactor,
        };
    }

    /// <summary>Its files, by their paths in it spelt with '/', as this assembly carries them.</summary>
    public static IReadOnlyDictionary<string, byte[]> Files()
    {
        var assembly = typeof(MutationFixture).Assembly;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The resource '{name}' this assembly lists is not in it.");
            using var bytes = new MemoryStream();

            stream.CopyTo(bytes);

            // Embedded under the path the build that embedded it spelt, with that machine's separator.
            files[name[ResourcePrefix.Length..].Replace('\\', '/')] = bytes.ToArray();
        }

        return files;
    }

    /// <summary>Its registry, read as a sweep reads one: every arm it declares, its texts every file of its text directory.</summary>
    /// <exception cref="InvalidOperationException">It does not read whole: a defect of this build, which its own tests catch.</exception>
    public static MutationRegistry Registry()
    {
        var files = Files();
        var settings = SettingsFor(new MutationSettings());
        var texts = settings.TextDirectory! + "/";
        var reading = MutationRegistryParser.Parse(
            MutationRegistryParser.Lines(Encoding.UTF8.GetString(files[settings.Registry!])),
            new TextDirectoryListing(
                settings.TextDirectory!,
                [.. files.Keys.Where(path => path.StartsWith(texts, StringComparison.Ordinal)).Select(path => path[texts.Length..])]));

        return reading.Valid
            ? reading.Registry
            : throw new InvalidOperationException($"The self-test's own registry does not read whole: {string.Join("; ", reading.Problems)}");
    }

    /// <summary>
    /// Makes <paramref name="directory"/> hold the fixture and nothing else: each of its files written only where it
    /// differs, and each other file - an earlier build's of this tool - removed, with no other process on the machine
    /// writing it meanwhile.
    /// </summary>
    /// <param name="fileSystem">Reads and writes the directory.</param>
    /// <param name="directory">Where the fixture is kept.</param>
    /// <exception cref="HarnessException">
    /// It could not be written, or another process kept writing it too long (<see cref="HarnessExit.Refused"/>): until it
    /// can be, no self-test runs on this machine.
    /// </exception>
    public static void Extract(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var files = Files();

        // Under the one lock of the directory every process on the machine takes, as a file every run decides by is
        // written: two legs of one sweep, or two sweeps, each writing a file the other is reading would each fail.
        MachineWideFile.Update(directory, MachineWideFile.Window, () =>
        {
            MachineWideFile.Written(
                $"The self-test's fixture '{directory}'",
                "Until it can be, no self-test runs on this machine.",
                () =>
                {
                    foreach (var (path, bytes) in files)
                    {
                        var file = InFixture(directory, path);

                        if (fileSystem.FileExists(file) && fileSystem.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))
                        {
                            continue;
                        }

                        // Its text, which is UTF-8 without a byte order mark, as the atomic write writes it back.
                        fileSystem.WriteAllTextAtomic(file, Encoding.UTF8.GetString(bytes));
                    }

                    foreach (var file in fileSystem.EnumerateFiles(directory, recursive: true).ToList())
                    {
                        if (!files.ContainsKey(Path.GetRelativePath(directory, file).Replace('\\', '/')))
                        {
                            fileSystem.DeleteFile(file);
                        }
                    }
                });

            return 0;
        });
    }

    /// <summary>
    /// The fixture in <paramref name="directory"/>, as a sync reads a tree: each of its files by its size and hash, so every
    /// worker of a self-test is made, and every site checked, against what this assembly carries.
    /// </summary>
    /// <param name="directory">Where the fixture is kept.</param>
    internal static SyncSource Reading(string directory)
    {
        var config = new HarnessConfig();
        var entries = Files().ToDictionary(
            pair => pair.Key,
            pair => new SyncEntry(pair.Key, pair.Value.Length, FileContentHash.Of(pair.Value)),
            StringComparer.Ordinal);

        // A configuration of nothing: the fixture's workers are built with the leg's, which no file in them is read for.
        // And no commit: the fixture is this assembly's, and no repository's tree.
        return new SyncSource(
            new HarnessContext(new HarnessLayout(directory, directory), config),
            new SyncExclusions(config.Sync, config.Worktrees.Root),
            new SyncManifest(directory, entries),
            "{}\n"u8.ToArray(),
            history: null);
    }

    /// <summary>
    /// The verdict a self-test gives arm <paramref name="arm"/>, as the judge reached <paramref name="reached"/>: held by
    /// <see cref="Judge"/> to the verdict it is designed to reach, or violated where it is no arm the fixture designs.
    /// </summary>
    /// <param name="arm">The arm's id.</param>
    /// <param name="reached">What the judge made of it.</param>
    public static ReachedVerdict Hold(string arm, ReachedVerdict reached)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(reached);

        return Designed.TryGetValue(arm, out var designed)
            ? Judge(designed, reached)
            : ReachedVerdict.Of(
                LegVerdict.Violated,
                $"the fixture designs no verdict for this arm, which reached {Verdicts.Display(reached.Verdict)}: {reached.Detail}");
    }

    /// <summary>
    /// The verdict a self-test gives an arm designed to reach <paramref name="designed"/> that reached
    /// <paramref name="reached"/>: passed where it reached it, saying so; violated where it reached another verdict an arm's
    /// design decides, naming both; and what it reached where that is no verdict of the judge's - stopped, not admitted,
    /// poisoned - which says nothing of how this tool judged it.
    /// </summary>
    /// <param name="designed">The verdict the arm is designed to reach.</param>
    /// <param name="reached">What the sweep judged it.</param>
    public static ReachedVerdict Judge(LegVerdict designed, ReachedVerdict reached)
    {
        ArgumentNullException.ThrowIfNull(reached);

        if (!Decided.Contains(reached.Verdict))
        {
            return reached;
        }

        return reached.Verdict == designed
            ? ReachedVerdict.Of(LegVerdict.Passed, $"{Verdicts.Display(designed)}, as designed: {reached.Detail}")
            : ReachedVerdict.Of(
                LegVerdict.Violated,
                $"designed to reach {Verdicts.Display(designed)}, and reached {Verdicts.Display(reached.Verdict)}: {reached.Detail}");
    }

    /// <summary>Where <paramref name="path"/>, spelt with '/', is in the fixture in <paramref name="directory"/>.</summary>
    private static string InFixture(string directory, string path) => Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// Where this machine keeps the fixture a self-test sweeps - one per user of the machine, whichever repositories reach
/// it - and its writing there.
/// </summary>
/// <param name="fileSystem">Reads and writes the fixture.</param>
/// <param name="directory">
/// Names the directory it is kept in, asked the first time a self-test needs it: a process that can name no directory of
/// its user's own refuses a self-test, saying why, and nothing else it does waits on that.
/// </param>
public sealed class MutationFixtureStore(IFileSystem fileSystem, Func<string> directory)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly Lazy<string> _directory = new(() => Kept(directory));

    /// <summary>A store keeping the fixture in <paramref name="directory"/>.</summary>
    /// <param name="fileSystem">Reads and writes the fixture.</param>
    /// <param name="directory">The directory it is kept in.</param>
    public MutationFixtureStore(IFileSystem fileSystem, string directory)
        : this(fileSystem, () => directory)
    {
    }

    /// <summary>Names where the fixture is kept for the user running this process: among this user's own data for this tool.</summary>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string DefaultDirectory() => UserState.File(MutationFixture.DirectoryName);

    /// <summary>The directory the fixture is kept in.</summary>
    /// <exception cref="HarnessException">No directory of this user's own could be named (<see cref="HarnessExit.Refused"/>).</exception>
    public string Directory => _directory.Value;

    /// <summary>Makes <see cref="Directory"/> hold the fixture and nothing else, as <see cref="MutationFixture.Extract"/> writes it.</summary>
    /// <exception cref="HarnessException">
    /// No directory of this user's own could be named, it could not be written, or another process kept writing it too
    /// long (<see cref="HarnessExit.Refused"/>): until it can be, no self-test runs on this machine.
    /// </exception>
    public void Write() => MutationFixture.Extract(_fileSystem, Directory);

    /// <summary>Where the fixture is kept, as <paramref name="place"/> names it.</summary>
    /// <param name="place">Names where it is kept, or throws where no directory of this user's own can be named.</param>
    /// <exception cref="HarnessException">No directory of this user's own could be named (<see cref="HarnessExit.Refused"/>).</exception>
    private static string Kept(Func<string> place)
    {
        ArgumentNullException.ThrowIfNull(place);

        try
        {
            return place();
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"The self-test's fixture has nowhere to be kept: {ex.Message.TrimEnd('.')}. Nothing was swept.",
                ex);
        }
    }
}

/// <summary>The embedded fixture, read as a self-test's every worker is made from it.</summary>
internal sealed class FixtureMutationSource : IMutationSource
{
    /// <inheritdoc/>
    public Task<SyncSource> ReadAsync(string treeRoot, CancellationToken cancellationToken)
        => Task.FromResult(MutationFixture.Reading(treeRoot));
}
