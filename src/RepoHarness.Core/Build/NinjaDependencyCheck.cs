using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Build;

/// <summary>What a build directory's dependency records say.</summary>
/// <param name="ObjectsRead">How many objects had a dependency record at all.</param>
/// <param name="WithoutHeaders">Objects that recorded no header dependencies, and are not excused.</param>
/// <param name="Excused">
/// Objects that recorded none legitimately, each with the source that explains it.
/// </param>
/// <param name="Skipped">Why the check did not run, or null when it did.</param>
public sealed record NinjaDependencyReport(
    int ObjectsRead,
    IReadOnlyList<string> WithoutHeaders,
    IReadOnlyDictionary<string, string> Excused,
    string? Skipped)
{
    /// <summary>Whether every object that should have recorded headers did.</summary>
    public bool IsClean => Skipped is not null || WithoutHeaders.Count == 0;
}

/// <summary>
/// Finds objects a build produced without recording which headers they depend on.
/// </summary>
/// <remarks>
/// An object with no recorded header dependencies is never rebuilt when a header it includes
/// changes, so the next build links yesterday's object and reports success. The check runs per leg,
/// on that leg's own build directory, because every leg has one: run only where the harness happens
/// to be, it leaves every other leg unchecked, and the leg most likely to be misconfigured is the
/// one nobody is sitting at.
/// </remarks>
public sealed class NinjaDependencyCheck(IProcessRunner processRunner, IFileSystem fileSystem)
{
    /// <summary>The file a ninja build directory describes itself in.</summary>
    public const string ManifestFileName = "build.ninja";

    /// <summary>
    /// What a Ninja generator starts to build, and the name the check looks ninja up by when the build
    /// recorded no program of its own.
    /// </summary>
    public const string Program = "ninja";

    /// <summary>
    /// How long <c>ninja -t deps</c> may take. A probe, not a phase: it reads a log and prints, so a
    /// budget here bounds a hang rather than guessing at a workload.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    /// <summary>Runs ninja in <paramref name="buildDirectory"/> as the build ran it, within the budget of a probe.</summary>
    /// <param name="buildDirectory">The build directory, which ninja runs in.</param>
    /// <param name="arguments">What ninja is asked.</param>
    /// <param name="appendToPath">
    /// The directories the build was given, for a ninja looked up by name: the one the survey found for the
    /// build, not "not installed".
    /// </param>
    /// <param name="program">The ninja the build ran, as its configuration recorded it, or <see langword="null"/> to look one up.</param>
    /// <param name="environment">The environment the build's phases ran in.</param>
    internal static ProcessRequest Request(
        string buildDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> appendToPath,
        string? program,
        IReadOnlyDictionary<string, string?>? environment) => new()
        {
            FileName = string.IsNullOrWhiteSpace(program) ? Program : ProcessRunner.Anchored(program, buildDirectory),
            Arguments = arguments,
            AppendToPath = appendToPath,
            Environment = environment ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            WorkingDirectory = buildDirectory,
            Timeout = Budget,
        };

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>Checks one build directory.</summary>
    /// <param name="buildDirectory">The directory to read.</param>
    /// <param name="appendToPath">The directories the build appended to its PATH, which ninja is looked up on too.</param>
    /// <param name="program">
    /// The ninja the build ran, as its configuration recorded it, or <see langword="null"/> to look one
    /// up. Read by the program that wrote them, the records are read the way they were written, and a
    /// ninja only the build's own environment could find is found all the same. A relative one is read
    /// from the build directory, where the check starts.
    /// </param>
    /// <param name="environment">
    /// The environment the build's phases ran in, which the check runs in too: a ninja looked up by
    /// name is found on the PATH the build had.
    /// </param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <exception cref="HarnessException">
    /// The check could not run: the directory is missing, ninja could not be started, or it answered
    /// with nothing. An empty answer is a failure and never a pass — it is indistinguishable from
    /// "every object recorded its headers", and reading it as one is how a broken build directory
    /// stays green.
    /// </exception>
    public async Task<NinjaDependencyReport> CheckAsync(
        string buildDirectory,
        IReadOnlyList<string> appendToPath,
        string? program = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appendToPath);

        if (!_fileSystem.DirectoryExists(buildDirectory))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{buildDirectory}' does not exist, so its dependency records cannot be read.");
        }

        var manifest = Path.Combine(buildDirectory, ManifestFileName);

        if (!_fileSystem.FileExists(manifest))
        {
            // The one legitimate skip: a build system other than ninja records dependencies its own
            // way, and there is nothing here to read.
            return new NinjaDependencyReport(0, [], EmptyExcuses, $"'{buildDirectory}' is not a ninja build directory");
        }

        var records = await NinjaRebuildGraph
            .ReadRecordsAsync(_processRunner, buildDirectory, appendToPath, program, environment, cancellationToken)
            .ConfigureAwait(false);

        var withoutHeaders = records.Values.Where(record => record.Count == 0).Select(record => record.Object).ToList();

        if (withoutHeaders.Count == 0)
        {
            return new NinjaDependencyReport(records.Count, [], EmptyExcuses, null);
        }

        var (flagged, excused) = Excuse(buildDirectory, records, withoutHeaders);

        return new NinjaDependencyReport(records.Count, flagged, excused, null);
    }

    /// <summary>
    /// Separates objects that legitimately recorded no headers from those that did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only an object built under <c>deps = msvc</c> - its own build line's, or else its rule's - can
    /// record zero legitimately. Ninja reads its headers from <c>/showIncludes</c>, which never names
    /// the source itself, and drops every header whose path, as ninja holds it, names
    /// <c>program files</c> or <c>microsoft visual studio</c> as the system's own; so a translation
    /// unit that includes nothing, or only the standard library and the Windows SDK, records none. So
    /// does one built from a precompiled header under <c>/Yu</c> that includes, besides, only headers
    /// the precompiled header holds and guards - with <c>#pragma once</c> or an include guard - which
    /// cl has compiled and never opens again. Under <c>deps = gcc</c> the source is always listed, so
    /// zero can never be legitimate, and an object built that way keeps the check's full strength
    /// whatever else the manifest builds.
    /// </para>
    /// <para>
    /// So a zero is excused only where ninja rebuilds the object, all the same, for every header its
    /// compile surely includes: each header its command force-includes with <c>/FI</c>, and what its
    /// source and each of those headers include by a quoted include resolving beside them, where cl
    /// looks first, of a header ninja keeps - never inside a comment, and inside a conditional block
    /// only where the block is surely compiled. A block is surely compiled where its condition is a
    /// number, or asks whether <c>__cplusplus</c> is defined, which the unit's language answers - C++
    /// under <c>/TP</c> or for a C++ source, C under <c>/TC</c> or for a <c>.c</c> one - and never where
    /// it asks anything else: one under <c>#ifndef _WIN32</c> may be compiled out. CMake force-includes
    /// the header it precompiles in the object compiling it and in every unit built from it, holding
    /// its includes under <c>#ifdef __cplusplus</c> for C++.
    /// </para>
    /// <para>
    /// What ninja rebuilds the object for is the inputs of its build line and, for each input that is
    /// itself built, the dependencies its build recorded and its own inputs, however far back. The
    /// object compiling a precompiled header records everything the header holds, and each unit built
    /// from it names its <c>.pch</c>, so a held header is rebuilt for through that object; while it
    /// records nothing, nothing is rebuilt for a header the precompiled header holds, and neither that
    /// object nor any unit built from it is excused.
    /// </para>
    /// <para>
    /// Only what the object's own build line leads to ever excuses it: another object's record may have
    /// been written by an older build, under a Visual Studio in another language, or replayed by a
    /// compiler cache, and says nothing of whether ninja read what cl named for this one.
    /// </para>
    /// <para>
    /// The manifest is read the way ninja reads it - across the files it includes, where CMake keeps its
    /// rules, with ninja's escapes undone and its variables evaluated - so the source CMake names
    /// absolutely is a file that can be read, and the command a build line ran is the one cl was given.
    /// An object no build line produces, and a source that is not there or cannot be read, are never
    /// excused.
    /// </para>
    /// </remarks>
    private (IReadOnlyList<string> Flagged, IReadOnlyDictionary<string, string> Excused) Excuse(
        string buildDirectory,
        IReadOnlyDictionary<string, DepsRecord> records,
        List<string> withoutHeaders)
    {
        var graph = new NinjaRebuildGraph(
            buildDirectory,
            NinjaManifest.Read(_fileSystem, buildDirectory),
            records,
            _fileSystem,
            PathCase.In(_fileSystem, buildDirectory));
        var flagged = new List<string>();
        var excused = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var obj in withoutHeaders)
        {
            if (graph.EdgeFor(obj) is { Deps: "msvc", Source: { } source } edge
                && graph.SurelyIncluded(edge, source) is { } included
                && graph.RebuildsFor(edge, included))
            {
                excused[obj] = source;
            }
            else
            {
                flagged.Add(obj);
            }
        }

        return (flagged, excused);
    }

    private static readonly Dictionary<string, string> EmptyExcuses = new(StringComparer.Ordinal);
}
