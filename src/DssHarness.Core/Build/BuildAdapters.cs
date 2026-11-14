using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Build;

/// <summary>
/// How one kind of project is configured and built. One adapter per build system, chosen by the
/// project's declared type, so nothing about a build system is compiled into the leg machinery.
/// </summary>
public interface IBuildAdapter
{
    /// <summary>The project type this adapter serves.</summary>
    string Type { get; }

    /// <summary>The program every phase this adapter runs starts, by the name it is started with.</summary>
    /// <remarks>
    /// Named once, here, and read both by the phases and by the survey that decides whether a host
    /// can run a leg: a survey spelling it again for itself would be a second list, and a second list
    /// is how a leg came to be called runnable on a host where its build tool could not be found.
    /// </remarks>
    string Program { get; }

    /// <summary>
    /// The kinds of file whose content can change what this project type builds: extensions and
    /// whole file names, in one list.
    /// </summary>
    /// <remarks>
    /// What a clean rebuild is decided over. Every tracked file was the first answer and it is the
    /// safe one, but it makes a documentation edit discard a warm build directory on every leg:
    /// measured on a consumer's tree, editing one markdown file forced two legs through a full
    /// rebuild, one of them 11 minutes.
    /// <para>
    /// Narrowing is safe here for a reason that is worth stating, because the check exists to stop
    /// a stale binary reading as a pass. A build that spanned a clock step is rebuilt from clean
    /// before this set is ever consulted, so the host whose clock moves is unaffected. What is left
    /// is builds where no step occurred, and there the build system's own dependency graph is
    /// authoritative: ninja will rebuild what a changed file feeds, because the timestamps it
    /// compares mean what they say. This set is the belt over those braces, not the braces.
    /// </para>
    /// <para>
    /// Empty means every tracked file, which is what an adapter that cannot say says.
    /// </para>
    /// </remarks>
    BuildInputKinds InputKinds { get; }

    /// <summary>The build type this configuration names for this build system, or null when it names none.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="buildConfig">The build configuration's name.</param>
    string? BuildTypeOf(HarnessConfig config, string buildConfig);

    /// <summary>
    /// The generator this configuration names for the build system of <paramref name="toolchain"/>, or null when it names
    /// none. Asked of the adapter, so the value it configures with is the value every reader of the configuration holds.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="toolchain">The toolchain the build builds with.</param>
    string? GeneratorOf(HarnessConfig config, string toolchain);

    /// <summary>
    /// Every build tool this adapter's builds run, each with how it goes on past a unit that fails to build: what help lists
    /// for <see cref="KeepGoing.Option"/>, from the ways the adapter goes by.
    /// </summary>
    IReadOnlyList<KeepGoingWay> KeepGoingWays { get; }

    /// <summary>
    /// How a build with <paramref name="toolchain"/> goes on past a unit that fails to build, as
    /// <see cref="KeepGoing.Option"/> asks, or - <see cref="KeepGoingHow.Never"/> - why no way is known: the way of the
    /// build tool the configuration says builds it, never one guessed.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="toolchain">The toolchain the build builds with.</param>
    KeepGoingWay KeepGoingFor(HarnessConfig config, string toolchain);

    /// <summary>
    /// What a build phase of this adapter's that its tool failed says of the units it failed to build: each by its source
    /// where the tool's output says it, and why not where it cannot be read - never that none failed.
    /// </summary>
    /// <param name="failed">The build phase its tool failed.</param>
    FailedUnits FailedUnitsOf(FailedBuild failed);

    /// <summary>The phases that configure and build this project, in order.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="request">The leg's build.</param>
    /// <param name="buildDirectory">Where this variant builds.</param>
    /// <param name="overlay">The environment and cache variables this variant builds with.</param>
    /// <param name="environment">
    /// The environment every phase starts with - the host's, then the variant's - made once for the
    /// whole build, so every phase and every check of it agrees on which compiler it uses.
    /// </param>
    IEnumerable<PhaseRequest> Phases(
        HarnessConfig config,
        BuildRequest request,
        string buildDirectory,
        VariantOverlay overlay,
        IReadOnlyDictionary<string, string?> environment);
}

/// <summary>
/// The kinds of file whose content can change what a build produces.
/// </summary>
/// <remarks>
/// Extensions and whole file names go in one list, each as configuration spells it: <c>.cpp</c>,
/// <c>cpp</c> and <c>CMakeLists.txt</c> all mean what they look like.
/// <para>
/// Kinds rather than globs: the question is what kind of file this is, and a glob would have to be
/// written per directory layout by every repository that has one. One list rather than extensions
/// beside names, because a project overriding this writes the same shape the adapters do, and two
/// lists would mean a project that declared only formats silently lost its build system's own files.
/// </para>
/// </remarks>
public sealed record BuildInputKinds
{
    /// <summary>The kinds, blank entries removed.</summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>Builds the kinds from what an adapter or a project declared.</summary>
    /// <param name="entries">The kinds as written.</param>
    /// <remarks>
    /// Blank entries are dropped here rather than trusted to have been refused upstream. A blank one
    /// kept would make <see cref="Narrows"/> true while matching nothing, so a project whose list was
    /// <c>[""]</c> would narrow every tracked file away: an empty input set, no moving-tree guard,
    /// and a comparison that finds the tree unchanged on every build. The direction to fail in is
    /// the wide one.
    /// </remarks>
    public BuildInputKinds(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Entries = [.. entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())];
    }

    /// <summary>An adapter that cannot say, whose builds compare over every tracked file.</summary>
    public static BuildInputKinds Everything { get; } = new([]);

    /// <summary>Whether this says anything at all.</summary>
    public bool Narrows => Entries.Count > 0;

    /// <summary>Whether <paramref name="relativePath"/> is a file of one of these kinds.</summary>
    /// <param name="relativePath">A path relative to the tree root, spelled with either separator.</param>
    /// <remarks>
    /// A file with no extension at all is covered, whatever the entries say. An extension is the
    /// signal this reads, and a file carrying none carries no signal — so the safe reading is that
    /// it might matter. Measured on a consumer's tree: <c>VERSION</c>, extensionless, is read with
    /// <c>file(READ)</c> at configure time and feeds both the project version and a compiler
    /// predefine. Listing that name, and the next repository's, would be guessing at a vocabulary;
    /// treating "no extension" as unknown costs a clean rebuild when somebody edits <c>LICENSE</c>,
    /// which is rare and is the direction to be wrong in.
    /// <para>
    /// An entry matches a file's whole name or its extension, with or without the leading dot, so
    /// one list carries both questions and nobody has to know which kind of entry they wrote.
    /// </para>
    /// <para>
    /// Both halves compare without case, and deliberately not by how the platform compares paths.
    /// The question here is what kind of file this is, not which file it is: <c>NuGet.Config</c> is
    /// the file NuGet's own documentation names and Visual Studio writes, and on a case-sensitive
    /// filesystem an Ordinal name test would decide it is not a build input at all.
    /// </para>
    /// </remarks>
    public bool Covers(string relativePath)
    {
        if (!Narrows)
        {
            return true;
        }

        var name = Path.GetFileName(relativePath.Replace('\\', '/'));
        var extension = Path.GetExtension(name);

        if (extension.Length == 0)
        {
            return true;
        }

        foreach (var entry in Entries)
        {
            if (name.Equals(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // The extension compared without case wherever it is compared: a tracked file named
            // .CPP builds the same as one named .cpp everywhere, and a case-sensitive filesystem
            // does not make it a different kind of file.
            var spelled = entry.StartsWith('.') ? entry : "." + entry;

            if (extension.Equals(spelled, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The adapters this build knows, by project type.</summary>
public static class BuildAdapters
{
    private static readonly IBuildAdapter[] Known = [new CMakeAdapter(), new DotnetAdapter(), new DartAdapter()];

    /// <summary>
    /// The phase of every adapter's that builds what the project is made of - after CMake's configure, after Dart's
    /// <c>pub get</c> - whose failure is read for the units it failed to build.
    /// </summary>
    public const string BuildPhase = "build";

    /// <summary>Every adapter this build knows, in the order help lists them.</summary>
    public static IReadOnlyList<IBuildAdapter> All => Known;

    /// <summary>The adapter for a project type, or <see langword="null"/> when no adapter serves it.</summary>
    /// <param name="type">The project's declared type.</param>
    public static IBuildAdapter? Find(string? type)
        => Known.FirstOrDefault(candidate => string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>The adapter for a project type.</summary>
    /// <param name="type">The project's declared type.</param>
    /// <exception cref="HarnessException">No adapter serves that type.</exception>
    public static IBuildAdapter For(string type)
    {
        var adapter = Find(type);

        return adapter ?? throw new HarnessException(
            HarnessExit.ConfigInvalid,
            $"No build adapter serves project type '{type}'. Declared types: "
            + string.Join(", ", Known.Select(candidate => candidate.Type)) + ".");
    }

    /// <summary>
    /// The environment every phase of a build runs with: the host's own, the variant's over it, and a
    /// compiler cache keyed against the leg's own tree.
    /// </summary>
    /// <param name="overlay">The variant's environment.</param>
    /// <param name="request">The leg's build, whose host environment and tree the rest comes from.</param>
    internal static Dictionary<string, string?> EnvironmentFor(VariantOverlay overlay, BuildRequest request)
    {
        var environment = PhaseEnvironment.Layered(request.HostEnvironment, overlay.Env);

        // Set from the leg's own tree rather than inherited. A shared base directory lets one tree's
        // objects satisfy another tree's build, which is a contamination this prevents by
        // construction rather than by hoping the caches disagree.
        if (environment.ContainsKey("CCACHE_DIR"))
        {
            environment["CCACHE_BASEDIR"] = request.TreeRoot;
        }

        return environment;
    }

    internal static string LogFor(BuildRequest request, string phase)
        => new LegRecords(request.RunDirectory, request.Leg).PhaseLog(phase);
}

/// <summary>Configures and builds a CMake project.</summary>
public sealed class CMakeAdapter : IBuildAdapter
{
    /// <summary>The phase that configures the build directory, after which CMake can say which compilers it resolved.</summary>
    public const string ConfigurePhase = "configure";

    /// <summary>The phase that builds what configure generated, with the build tool its generator names.</summary>
    public const string BuildPhase = BuildAdapters.BuildPhase;

    /// <summary>ninja's way, for the generators that build with it: told to go on, by <see cref="Ninja.KeepGoingArguments"/>.</summary>
    private static readonly KeepGoingWay NinjaWay = new(
        "Ninja, Ninja Multi-Config",
        KeepGoingHow.WhenTold,
        Ninja.KeepGoingArguments,
        "ninja - and samurai, where CMake runs it for ninja - stops at its first failures, and -k 0 has it go on until nothing more can be built");

    /// <summary>make's way, for the generators that build with it: told to go on, by <see cref="Make.KeepGoingArguments"/>.</summary>
    private static readonly KeepGoingWay MakeWay = new(
        "Unix Makefiles, MinGW Makefiles, MSYS Makefiles",
        KeepGoingHow.WhenTold,
        Make.KeepGoingArguments,
        "make stops at its first failure, and -k has every make CMake's makefiles start go on with each target that does not need one that failed");

    /// <summary>MSBuild's way, for the Visual Studio generators: it goes on by itself.</summary>
    /// <remarks>
    /// Measured with Visual Studio 17 2022's MSBuild under <c>--parallel 1</c>: of two libraries each with a failing source, both
    /// were built and failed, and of one library with two, cl compiled and failed both.
    /// </remarks>
    private static readonly KeepGoingWay MsBuildWay = new(
        "Visual Studio generators",
        KeepGoingHow.ByItself,
        [],
        "MSBuild goes on by itself: it builds every project that does not need one that failed, and cl compiles every source of a project it builds");

    /// <summary>nmake's way, for the NMake Makefiles generator: none.</summary>
    /// <remarks>
    /// Measured with CMake 4.3 and Visual Studio 17 2022's nmake: given <c>/K</c>, only the first nmake went on - the ones
    /// CMake's makefiles start for each target were not given it, and stopped at their target's first failing source.
    /// </remarks>
    private static readonly KeepGoingWay NMakeWay = new(
        "NMake Makefiles",
        KeepGoingHow.Never,
        [],
        "nmake's /K does not reach the nmake CMake's makefiles start for each target, which stops at that target's first failing unit");

    /// <inheritdoc/>
    public string Type => "cmake";

    /// <inheritdoc/>
    public string Program => "cmake";

    /// <inheritdoc/>
    /// <remarks>
    /// Sources and headers, the build system's own files, and the templates <c>configure_file</c>
    /// reads. Assembly and the CUDA and Objective-C families are here because a project that has
    /// them compiles them; listing only C and C++ would leave a repository that builds a <c>.S</c>
    /// deciding a changed one cannot affect its build. C++20 module interface units are here for the
    /// same reason: CMake builds them under ninja, and a module interface is as much a source as a
    /// header is.
    /// </remarks>
    public BuildInputKinds InputKinds { get; } = new(
        [
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".m", ".mm", ".cu", ".cuh",
            ".h", ".hh", ".hpp", ".hxx", ".h++", ".inc", ".ipp", ".tcc", ".def",
            ".ixx", ".cppm", ".ccm", ".cxxm",
            ".s", ".asm", ".rc", ".manifest",
            ".cmake", ".in", ".ninja", ".make", ".mk", ".pc",
            "CMakeLists.txt", "Makefile", "GNUmakefile", "meson.build", "conanfile.txt", "conanfile.py", "vcpkg.json",
            "CMakePresets.json", "CMakeUserPresets.json",
        ]);

    /// <inheritdoc/>
    public string? BuildTypeOf(HarnessConfig config, string buildConfig)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.BuildConfigs.TryGetValue(buildConfig, out var declared) ? declared.CmakeBuildType : null;
    }

    /// <inheritdoc/>
    public string? GeneratorOf(HarnessConfig config, string toolchain)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.Toolchains.TryGetValue(toolchain, out var declared) && declared.Generator is { Length: > 0 } generator
            ? generator
            : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<KeepGoingWay> KeepGoingWays { get; } = [NinjaWay, MakeWay, MsBuildWay, NMakeWay];

    /// <inheritdoc/>
    /// <remarks>
    /// By the generator the toolchain names, never by one CMake would pick: where it names none, CMake's default for the
    /// system, a <c>CMAKE_GENERATOR</c> in the environment, or the generator an earlier configure of the directory cached
    /// decides the build tool as the build configures, so how it goes on is not known before it builds. A generator this
    /// adapter knows no way for - Xcode's, whose way could not be measured, among them - is refused rather than guessed at.
    /// </remarks>
    public KeepGoingWay KeepGoingFor(HarnessConfig config, string toolchain)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (GeneratorOf(config, toolchain) is not { } generator)
        {
            return new(
                "no generator",
                KeepGoingHow.Never,
                [],
                $"toolchain '{toolchain}' names no generator, so the build tool CMake picks for it - its default for the system, CMAKE_GENERATOR, "
                + $"or the one an earlier configure cached - and how that tool goes on past a failing unit are not known before it builds; "
                + $"name one with toolchains.{toolchain}.generator");
        }

        return Ninja.Generates(generator) ? NinjaWay
            : Make.Generates(generator) ? MakeWay
            : VisualStudio(generator) ? MsBuildWay
            : string.Equals(generator, NMakeWay.Builds, StringComparison.OrdinalIgnoreCase) ? NMakeWay
            : new(generator, KeepGoingHow.Never, [], $"toolchain '{toolchain}' builds with generator '{generator}', and no way is known to have its build tool go on past a failing unit");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// By the generator CMake's cache says the directory was configured with, which is the build tool that ran: ninja's
    /// lines and make's are read; every other tool's are not, and said so.
    /// </remarks>
    public FailedUnits FailedUnitsOf(FailedBuild failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        var generator = failed.Configured?.Generator;

        if (Ninja.Generates(generator))
        {
            return Ninja.FailedUnitsOf(failed);
        }

        if (Make.Generates(generator))
        {
            return Make.FailedUnitsOf(failed);
        }

        return VisualStudio(generator)
            ? FailedUnits.NotRead("MSBuild", failed.Request.KeepGoing, stopsAtFirst: false)
            : FailedUnits.NotRead(generator is { Length: > 0 } named ? $"generator '{named}'" : "the build tool", failed.Request.KeepGoing, stopsAtFirst: false);
    }

    /// <summary>Whether a CMake generator is one of Visual Studio's, whose builds MSBuild runs: <c>Visual Studio 17 2022</c>.</summary>
    private static bool VisualStudio(string? generator) => generator?.StartsWith("Visual Studio ", StringComparison.OrdinalIgnoreCase) == true;

    /// <inheritdoc/>
    public IEnumerable<PhaseRequest> Phases(
        HarnessConfig config,
        BuildRequest request,
        string buildDirectory,
        VariantOverlay overlay,
        IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(overlay);

        var source = Path.Combine(request.TreeRoot, request.Project.Path);
        var configure = new List<string> { "-S", source, "-B", buildDirectory };

        if (GeneratorOf(config, request.Variant.Toolchain) is { } generator)
        {
            configure.Add("-G");
            configure.Add(generator);
        }

        // Removed before anything is set, as CMake takes its arguments in order: an entry this configure sets is set
        // again below, and one it does not is the project's own default once more.
        foreach (var pattern in request.UnsetFirst)
        {
            configure.Add($"-U{pattern}");
        }

        if (BuildTypeOf(config, request.Variant.Config) is { Length: > 0 } buildType)
        {
            configure.Add($"-DCMAKE_BUILD_TYPE={buildType}");
        }

        foreach (var (name, value) in overlay.CacheVars)
        {
            configure.Add($"-D{name}={value}");
        }

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = ConfigurePhase,
            FileName = Program,
            Arguments = configure,
            WorkingDirectory = request.TreeRoot,
            Environment = environment,
            LogFile = BuildAdapters.LogFor(request, ConfigurePhase),
            ProgramDirectories = request.ProgramDirectories,
        };

        var build = new List<string>
        {
            "--build", buildDirectory,
            "--parallel", request.Cores.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        foreach (var target in request.Project.Targets ?? [])
        {
            build.Add("--target");
            build.Add(target);
        }

        // What the generator's own tool is given to go on past a failing unit, after the '--' that hands cmake --build's
        // last arguments to it. A tool that goes on by itself is given nothing; one with no known way never gets here, for
        // the run refuses the option before anything starts (KeepGoing.Require).
        if (request.KeepGoing && KeepGoingFor(config, request.Variant.Toolchain) is { How: KeepGoingHow.WhenTold } way)
        {
            build.Add("--");
            build.AddRange(way.Arguments);
        }

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = BuildPhase,
            FileName = Program,
            Arguments = build,
            WorkingDirectory = request.TreeRoot,
            Environment = environment,
            LogFile = BuildAdapters.LogFor(request, BuildPhase),
            ProgramDirectories = request.ProgramDirectories,
        };
    }
}

/// <summary>Builds a .NET project or solution.</summary>
public sealed class DotnetAdapter : IBuildAdapter
{
    /// <inheritdoc/>
    public string Type => "dotnet";

    /// <inheritdoc/>
    public string Program => "dotnet";

    /// <inheritdoc/>
    /// <remarks>
    /// Sources, project and solution files, and the files MSBuild imports on its own — a
    /// <c>Directory.Build.props</c> changes every assembly under it without appearing in any of
    /// them. Resources and settings are here because they are compiled in.
    /// </remarks>
    public BuildInputKinds InputKinds { get; } = new(
        [
            ".cs", ".vb", ".fs", ".fsi", ".fsx", ".razor", ".cshtml", ".xaml",
            ".csproj", ".vbproj", ".fsproj", ".shproj", ".projitems", ".sln", ".slnx", ".slnf",
            ".props", ".targets", ".resx", ".settings", ".ruleset", ".editorconfig", ".json", ".config",
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "nuget.config", "global.json",
        ]);

    /// <inheritdoc/>
    public string? BuildTypeOf(HarnessConfig config, string buildConfig)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.BuildConfigs.TryGetValue(buildConfig, out var declared) ? declared.DotnetConfiguration : null;
    }

    /// <inheritdoc/>
    public string? GeneratorOf(HarnessConfig config, string toolchain)
    {
        ArgumentNullException.ThrowIfNull(config);

        return null;
    }

    /// <summary>dotnet's way: it goes on by itself.</summary>
    /// <remarks>
    /// Measured with the .NET 10 SDK under <c>-m:1</c>: of a solution of two projects, each with a failing source, both were
    /// built and failed, and the C# compiler named both failing sources of the one that has two.
    /// </remarks>
    private static readonly KeepGoingWay Way = new(
        "dotnet",
        KeepGoingHow.ByItself,
        [],
        "dotnet build goes on by itself: MSBuild builds every project that does not need one that failed, and the compiler reports every failing source of a project in one run");

    /// <inheritdoc/>
    public IReadOnlyList<KeepGoingWay> KeepGoingWays { get; } = [Way];

    /// <inheritdoc/>
    public KeepGoingWay KeepGoingFor(HarnessConfig config, string toolchain)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Way;
    }

    /// <inheritdoc/>
    public FailedUnits FailedUnitsOf(FailedBuild failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        return FailedUnits.NotRead(Program, failed.Request.KeepGoing, stopsAtFirst: false);
    }

    /// <inheritdoc/>
    public IEnumerable<PhaseRequest> Phases(
        HarnessConfig config,
        BuildRequest request,
        string buildDirectory,
        VariantOverlay overlay,
        IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(overlay);

        var arguments = new List<string>
        {
            "build",
            Path.Combine(request.TreeRoot, request.Project.Path),

            // Each variant keeps its own output, so two legs never overwrite one another's
            // assemblies while both are being tested.
            $"--artifacts-path:{buildDirectory}",
        };

        if (BuildTypeOf(config, request.Variant.Config) is { Length: > 0 } configuration)
        {
            arguments.Add("--configuration");
            arguments.Add(configuration);
        }

        foreach (var (name, value) in overlay.CacheVars)
        {
            arguments.Add($"-p:{name}={value}");
        }

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = BuildAdapters.BuildPhase,
            FileName = Program,
            Arguments = arguments,
            WorkingDirectory = request.TreeRoot,
            Environment = environment,
            LogFile = BuildAdapters.LogFor(request, BuildAdapters.BuildPhase),
            ProgramDirectories = request.ProgramDirectories,
        };
    }
}

/// <summary>Builds a Dart or Flutter project.</summary>
public sealed class DartAdapter : IBuildAdapter
{
    /// <inheritdoc/>
    public string Type => "dart";

    /// <inheritdoc/>
    public string Program => "dart";

    /// <inheritdoc/>
    /// <remarks>
    /// Sources, the manifest and its lock, and the generated-code inputs a build reads. The lock is
    /// included because a resolved dependency set decides what is compiled as much as a source does.
    /// </remarks>
    public BuildInputKinds InputKinds { get; } = new(
        [".dart", ".yaml", ".yml", ".arb", ".json", "pubspec.lock"]);

    /// <inheritdoc/>
    public string? BuildTypeOf(HarnessConfig config, string buildConfig)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.BuildConfigs.TryGetValue(buildConfig, out var declared) ? declared.DartMode : null;
    }

    /// <inheritdoc/>
    public string? GeneratorOf(HarnessConfig config, string toolchain)
    {
        ArgumentNullException.ThrowIfNull(config);

        return null;
    }

    /// <summary>dart's way: it goes on by itself.</summary>
    /// <remarks>
    /// Measured with the Dart SDK Flutter carries: a program importing two libraries, each with an error, had
    /// <c>dart compile exe</c> name both errors before it failed.
    /// </remarks>
    private static readonly KeepGoingWay Way = new(
        "dart",
        KeepGoingHow.ByItself,
        [],
        "dart compile exe goes on by itself: its compiler reports every error it finds in the program in one run");

    /// <inheritdoc/>
    public IReadOnlyList<KeepGoingWay> KeepGoingWays { get; } = [Way];

    /// <inheritdoc/>
    public KeepGoingWay KeepGoingFor(HarnessConfig config, string toolchain)
    {
        ArgumentNullException.ThrowIfNull(config);

        return Way;
    }

    /// <inheritdoc/>
    public FailedUnits FailedUnitsOf(FailedBuild failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        return FailedUnits.NotRead(Program, failed.Request.KeepGoing, stopsAtFirst: false);
    }

    /// <inheritdoc/>
    public IEnumerable<PhaseRequest> Phases(
        HarnessConfig config,
        BuildRequest request,
        string buildDirectory,
        VariantOverlay overlay,
        IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(overlay);

        var project = Path.Combine(request.TreeRoot, request.Project.Path);

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = "pub-get",
            FileName = Program,
            Arguments = ["pub", "get"],
            WorkingDirectory = project,
            Environment = environment,
            LogFile = BuildAdapters.LogFor(request, "pub-get"),
            ProgramDirectories = request.ProgramDirectories,
        };

        var arguments = new List<string> { "compile", "exe" };

        foreach (var target in request.Project.Targets ?? [])
        {
            arguments.Add(target);
        }

        arguments.Add("--output");
        arguments.Add(Path.Combine(buildDirectory, "out"));

        if (BuildTypeOf(config, request.Variant.Config) is { Length: > 0 } mode)
        {
            arguments.Add($"--define=mode={mode}");
        }

        yield return new PhaseRequest
        {
            Leg = request.Leg,
            Phase = BuildAdapters.BuildPhase,
            FileName = Program,
            Arguments = arguments,
            WorkingDirectory = project,
            Environment = environment,
            LogFile = BuildAdapters.LogFor(request, BuildAdapters.BuildPhase),
            ProgramDirectories = request.ProgramDirectories,
        };
    }
}
