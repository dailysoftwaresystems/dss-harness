using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Build;

/// <summary>What an existing build directory was configured for.</summary>
/// <param name="HomeDirectory">The source tree it was configured from, or null when it did not say.</param>
/// <param name="CCompiler">The C compiler recorded in it, or null.</param>
/// <param name="CxxCompiler">The C++ compiler recorded in it, or null.</param>
/// <param name="BuildType">The build type recorded in it, or null.</param>
/// <param name="MakeProgram">The program that builds it, such as the ninja the build ran, or null.</param>
/// <param name="CCompilerArguments">The words recorded after the C compiler, or null when none were.</param>
/// <param name="CxxCompilerArguments">The words recorded after the C++ compiler, or null when none were.</param>
/// <param name="Generator">The generator it was configured with, such as <c>Ninja</c>, or null.</param>
public sealed record BuildDirectoryRecord(
    string? HomeDirectory,
    string? CCompiler,
    string? CxxCompiler,
    string? BuildType,
    string? MakeProgram = null,
    string? CCompilerArguments = null,
    string? CxxCompilerArguments = null,
    string? Generator = null)
{
    /// <summary>
    /// Each dependency FetchContent declared in it, by the name its <c>FETCHCONTENT_SOURCE_DIR_&lt;NAME&gt;</c> entry
    /// gives it, with the source directory that entry names - empty where it names none, and the dependency was fetched
    /// into <see cref="FetchContentBaseDirectory"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> FetchContentSources { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Where FetchContent puts what it fetches, <c>&lt;name&gt;-src</c> for each source, or null where it did not say.</summary>
    public string? FetchContentBaseDirectory { get; init; }
}

/// <summary>
/// Refuses a build directory that was configured for something other than this leg.
/// </summary>
/// <remarks>
/// Reusing one is not a performance question. A build directory records the tree it was configured
/// from, and a directory configured from another worktree watches that tree's sources: it produced
/// both a refusal nobody could explain and, worse, a silent wrong answer, where a build reported
/// success having compiled a tree the caller had not touched. The build type is checked for the same
/// reason as the compiler: silently reconfiguring it turns an incremental directory into a mix of
/// objects from two configurations, and nothing afterwards says which one a binary came from.
/// </remarks>
public sealed class BuildDirectoryGuard(IFileSystem fileSystem, IHostPlatform platform, IFilePermissions filePermissions)
{
    /// <summary>The file a CMake build directory records its configuration in.</summary>
    public const string CMakeCacheFileName = "CMakeCache.txt";

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IFilePermissions _filePermissions = filePermissions;

    /// <summary>Reads what a build directory was configured for, or null when it holds no record.</summary>
    /// <param name="buildDirectory">The directory to read.</param>
    public BuildDirectoryRecord? Read(string buildDirectory)
    {
        var cache = Path.Combine(buildDirectory, CMakeCacheFileName);

        if (!_fileSystem.FileExists(cache))
        {
            return null;
        }

        string? home = null;
        string? cCompiler = null;
        string? cxxCompiler = null;
        string? buildType = null;
        string? makeProgram = null;
        string? cArguments = null;
        string? cxxArguments = null;
        string? generator = null;
        string? fetchBase = null;
        var fetched = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in _fileSystem.ReadAllText(cache).Split('\n'))
        {
            var text = line.Trim();

            fetchBase ??= ValueOf(text, FetchContentBase);

            // FETCHCONTENT_SOURCE_DIR_<NAME>:PATH=<directory>, one to each dependency FetchContent declared - and by its type
            // alone, since CMake marks each advanced with an entry of the same name and -ADVANCED:INTERNAL after it.
            if (text.StartsWith(FetchContentSource, StringComparison.Ordinal)
                && text.IndexOf(PathEntry, StringComparison.Ordinal) is var end
                && end > FetchContentSource.Length)
            {
                fetched[text[FetchContentSource.Length..end]] = text[(end + PathEntry.Length)..].Trim();
            }

            home ??= ValueOf(text, "CMAKE_HOME_DIRECTORY");
            cCompiler ??= ValueOf(text, "CMAKE_C_COMPILER");
            cxxCompiler ??= ValueOf(text, "CMAKE_CXX_COMPILER");
            buildType ??= ValueOf(text, "CMAKE_BUILD_TYPE");
            makeProgram ??= ValueOf(text, "CMAKE_MAKE_PROGRAM");

            // Where CMake puts what followed the program in CC or CXX: 'ccache gcc' is recorded as
            // /usr/bin/ccache with ' gcc' here.
            cArguments ??= ValueOf(text, "CMAKE_C_COMPILER_ARG1");
            cxxArguments ??= ValueOf(text, "CMAKE_CXX_COMPILER_ARG1");
            generator ??= ValueOf(text, "CMAKE_GENERATOR");
        }

        return new BuildDirectoryRecord(home, cCompiler, cxxCompiler, buildType, makeProgram, cArguments, cxxArguments, generator)
        {
            FetchContentSources = fetched,
            FetchContentBaseDirectory = fetchBase,
        };
    }

    /// <summary>What every cache entry naming the source of a dependency FetchContent declared starts with.</summary>
    public const string FetchContentSource = "FETCHCONTENT_SOURCE_DIR_";

    /// <summary>The cache entry naming where FetchContent puts what it fetches.</summary>
    public const string FetchContentBase = "FETCHCONTENT_BASE_DIR";

    /// <summary>What follows a cache entry's name where its value is a directory.</summary>
    private const string PathEntry = ":PATH=";

    /// <summary>
    /// Refuses when the directory was configured from another tree, with another compiler, or for
    /// another build type.
    /// </summary>
    /// <param name="buildDirectory">The directory this leg would build in.</param>
    /// <param name="sourceDirectory">
    /// The directory the build is configured from, which is the leg's tree root joined with the
    /// project's own path. Compared against <c>CMAKE_HOME_DIRECTORY</c>, which records exactly that
    /// — so a project living in a subdirectory must be compared with the subdirectory, or every
    /// second build of every such leg is refused for a mismatch that is not one.
    /// </param>
    /// <param name="expectedCompiler">
    /// The C compiler this leg builds with, or null when it names none or names one only CMake can
    /// read.
    /// </param>
    /// <param name="expectedCxxCompiler">The C++ compiler, the same way.</param>
    /// <param name="expectedBuildType">The build type this leg builds, or null when it names none.</param>
    /// <param name="search">Where the build's phases look for a compiler named by its name.</param>
    /// <exception cref="HarnessException">The directory belongs to a different build.</exception>
    public void Check(
        string buildDirectory,
        string sourceDirectory,
        CompilerValue? expectedCompiler,
        CompilerValue? expectedCxxCompiler,
        string? expectedBuildType,
        PathSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);

        var record = Read(buildDirectory);

        if (record is null)
        {
            return;
        }

        if (record.HomeDirectory is { Length: > 0 } home && !SameTree(home, sourceDirectory))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{buildDirectory}' was configured from '{home}', not from '{sourceDirectory}'. Building in it "
                + "would compile that tree's sources and report on this one. Delete it, or point this leg "
                + "at its own build directory.");
        }

        if (expectedCompiler is not null
            && record.CCompiler is { Length: > 0 } recordedCompiler
            && !SameCompiler(recordedCompiler, record.CCompilerArguments, expectedCompiler, search, out var startsC))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{buildDirectory}' was configured with '{Recorded(recordedCompiler, record.CCompilerArguments)}', and this leg builds with "
                + $"'{expectedCompiler}'{Starts(startsC)}. A build system refuses that change on an existing cache; "
                + "delete the directory rather than reconfiguring it.");
        }

        // The C++ compiler as well as the C one. A directory configured with clang++ and rebuilt
        // with g++ mixes two ABIs in one place, and reading only CMAKE_C_COMPILER misses it entirely
        // for a project that compiles no C at all.
        if (expectedCxxCompiler is not null
            && record.CxxCompiler is { Length: > 0 } recordedCxx
            && !SameCompiler(recordedCxx, record.CxxCompilerArguments, expectedCxxCompiler, search, out var startsCxx))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{buildDirectory}' was configured with '{Recorded(recordedCxx, record.CxxCompilerArguments)}', and this leg builds with "
                + $"'{expectedCxxCompiler}'{Starts(startsCxx)}. A build system refuses that change on an existing cache; "
                + "delete the directory rather than reconfiguring it.");
        }

        if (expectedBuildType is { Length: > 0 }
            && record.BuildType is { Length: > 0 } recordedType
            && !string.Equals(recordedType, expectedBuildType, StringComparison.OrdinalIgnoreCase))
        {
            // Refused rather than reconfigured. Reconfiguring leaves objects from both types in one
            // directory, and nothing afterwards says which type a binary was built with.
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{buildDirectory}' was configured as '{recordedType}', and this leg builds "
                + $"'{expectedBuildType}'. A build type is fixed once per directory: delete it, or give "
                + "this leg a build directory of its own.");
        }
    }

    private bool SamePath(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Replace('/', Path.DirectorySeparatorChar))),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            _platform.PathComparison);

    /// <summary>
    /// Whether the tree a build directory recorded is <paramref name="sourceDirectory"/>: the two compared with
    /// every link along each followed, as CMake itself compares them and as git names the tree.
    /// </summary>
    /// <remarks>
    /// A tree reached through a link - a home at <c>/home</c> linked to <c>/var/home</c> - is recorded as it
    /// was spelt when the directory was configured, and named by git with the link followed. Compared as
    /// written, the same tree was refused as another; and a host answering another machine, which writes both
    /// spellings of its home as <c>~</c>, then named one path twice in the refusal.
    /// </remarks>
    private bool SameTree(string recorded, string sourceDirectory)
        => string.Equals(Followed(recorded.Replace('/', Path.DirectorySeparatorChar)), Followed(sourceDirectory), _platform.PathComparison);

    /// <summary><paramref name="path"/> made whole, with every link along it followed where the links can be read.</summary>
    private string Followed(string path)
    {
        var full = Path.GetFullPath(path);

        try
        {
            full = _fileSystem.ResolveLinks(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Compared as written: a link nobody may read still names somewhere.
        }

        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    /// Whether a directory's recorded compiler is <paramref name="expected"/>: the same program, and
    /// the same words after it. A switch from <c>ccache clang</c> to <c>ccache gcc</c> changes only
    /// the words, and CMake keeps what it cached either way.
    /// </summary>
    /// <param name="recorded">The program the cache records, which CMake stores as a whole path.</param>
    /// <param name="recordedArguments">The words the cache records after it.</param>
    /// <param name="expected">What this leg names.</param>
    /// <param name="search">Where the build's phases look for a program named by its name.</param>
    /// <param name="starts">The file <paramref name="expected"/> starts now, where the search found one.</param>
    private bool SameCompiler(string recorded, string? recordedArguments, CompilerValue expected, PathSearch search, out string? starts)
        => SameProgram(recorded, expected.Program, search, out starts)
            && (recordedArguments ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .SequenceEqual(expected.Arguments, StringComparer.Ordinal);

    /// <summary>A recorded compiler as a message quotes it, with the words recorded after it.</summary>
    private static string Recorded(string compiler, string? arguments)
        => arguments is { Length: > 0 } ? $"{compiler} {arguments}" : compiler;

    /// <summary>The file a compiler starts now, as a refusal adds it after the name, where it was found.</summary>
    private static string Starts(string? starts) => starts is null ? string.Empty : $", which starts '{starts}' now";

    /// <summary>
    /// Whether the program a cache records is the one <paramref name="expected"/> starts: the file it
    /// resolves to on the PATH the build's phases are given, the way each phase finds its program.
    /// </summary>
    /// <remarks>
    /// Compared by the file wherever the search finds one. A cache records a whole path and a leg names
    /// a program, so a name compared with a name let a directory configured with one gcc be rebuilt
    /// with another - a second installation earlier on the PATH, a toolchain upgraded beside the old
    /// one - and the leg reported on objects from both. A program the search finds nowhere cannot
    /// start; the build says so when it tries, and until then only its name can be compared.
    /// </remarks>
    private bool SameProgram(string recorded, string expected, PathSearch search, out string? starts)
    {
        starts = null;

        if (string.Equals(recorded, expected, _platform.PathComparison))
        {
            return true;
        }

        var found = search.Find(_platform, _filePermissions, expected);

        if (found.Found is ProgramFound.OnPath or ProgramFound.OffPath && found.Path is { Length: > 0 } path)
        {
            starts = path;

            return SamePath(recorded, path);
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(recorded),
            Path.GetFileNameWithoutExtension(expected),
            _platform.PathComparison);
    }

    private static string? ValueOf(string line, string name)
    {
        // CMakeCache.txt lines are NAME:TYPE=VALUE. The type is not part of the name, and a line
        // whose name merely starts with this one is a different variable.
        if (!line.StartsWith(name, StringComparison.Ordinal) || line.Length <= name.Length)
        {
            return null;
        }

        var rest = line[name.Length..];

        if (rest[0] is not (':' or '='))
        {
            return null;
        }

        var equals = rest.IndexOf('=', StringComparison.Ordinal);

        return equals < 0 ? null : rest[(equals + 1)..].Trim();
    }
}
