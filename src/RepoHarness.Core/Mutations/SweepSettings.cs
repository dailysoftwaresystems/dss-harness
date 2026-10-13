using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Testing;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// What of the configuration a sweep's verdict on an arm rests on beyond what its build records, as three digests: what
/// its build is given, how its run starts, and what its run is judged by.
/// </summary>
/// <remarks>
/// <para>
/// Every other value of the configuration either cannot change a verdict - how many workers a sweep runs, the cores, the
/// admission of heavy legs, the path limits, the tolerance of a clock's step, which only adds a note - or reaches the
/// arm only through something else an arm is judged on: the cache variables, the build type, the generator, the
/// project's path and targets through the build lines; what a sync carries through the reading of the tree; where tools
/// are looked for through the compilers identified; what a runner reads at run time through the files
/// <c>mutations.runReads</c> names (<see cref="JudgedOn.RunReads"/>).
/// </para>
/// <para>
/// Read through the owners every command reads them through - the variant, its overlay, the test settings and their
/// invocation for the leg's system - and resolving nothing of its own.
/// </para>
/// </remarks>
/// <param name="Build">
/// The digest of what the build is given: the variant's name, as its build directory is named; the environment the
/// variant's toolchain, configuration, sanitizer and project give it; and the environment the leg's host declares.
/// </param>
/// <param name="Run">
/// The digest of how the run starts: the working directory the test settings declare, as declared, and their
/// environment, on the leg's system; and the report arguments.
/// </param>
/// <param name="Judge">The digest of what the run is judged by: the run-time factor, and how long a run may print nothing.</param>
public sealed record SweepSettings(string Build, string Run, string Judge)
{
    /// <summary>What a sweep of <paramref name="leg"/> rests on of <paramref name="config"/>, on a host declaring <paramref name="host"/>.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="host">What the host the leg runs on declares for itself.</param>
    public static SweepSettings Of(HarnessConfig config, LegConfig leg, HostSettings host)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(host);

        var project = VariantKey.ProjectFor(config, leg);

        return Of(
            config,
            VariantKey.For(config, leg, leg.Os),
            project,
            TestInvocationResolver.SettingsFor(config, leg, project),
            leg.Os,
            config.Mutations,
            host.Env);
    }

    /// <summary>What a sweep rests on of <paramref name="config"/>, each part already resolved: a self-test's, whose project is the fixture.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="project">The project its workers build, or <see langword="null"/> where it names none.</param>
    /// <param name="tests">The test settings its runs start by, or <see langword="null"/> where it has none.</param>
    /// <param name="os">The operating system the leg runs on.</param>
    /// <param name="mutations">The mutation settings the sweep runs by.</param>
    /// <param name="hostEnvironment">The environment the leg's host declares.</param>
    public static SweepSettings Of(
        HarnessConfig config,
        VariantKey variant,
        ProjectConfig? project,
        TestConfig? tests,
        string os,
        MutationSettings mutations,
        IReadOnlyDictionary<string, string> hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        var invocation = tests is null ? null : TestInvocationResolver.InvocationFor(tests, os);

        return new SweepSettings(
            CanonicalJson.Digest(new
            {
                Variant = variant.DirectoryName,
                Env = CanonicalJson.Sorted(project is null ? [] : variant.Overlay(config, project).Env),
                Host = CanonicalJson.Sorted(hostEnvironment),
            }),
            CanonicalJson.Digest(new
            {
                invocation?.WorkingDirectory,
                Env = CanonicalJson.Sorted(invocation?.Env ?? new Dictionary<string, string>()),
                ReportArgs = mutations.ReportArgs ?? [],
            }),
            CanonicalJson.Digest(new
            {
                mutations.RunTimeFactor,
                config.Defaults.StallSeconds,
            }));
    }
}
