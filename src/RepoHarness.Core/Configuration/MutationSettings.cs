using System.ComponentModel;

namespace RepoHarness.Core.Configuration;

/// <summary>
/// Mutation testing: where a repository declares the arms <c>check-mutations</c> drives, how many worker copies a leg
/// sweeps them with, and how a test binary is told to write the report each arm is judged by.
/// </summary>
/// <remarks>
/// What <c>help mutations</c> says of each key here is that key's <see cref="DescriptionAttribute"/>, listed through
/// <see cref="ConfigKeys"/>. Nothing about the arms themselves is configured here: they are the repository's own data,
/// in the registry this names, read and checked whole before any host is touched.
/// </remarks>
public sealed class MutationSettings
{
    /// <summary>Worker copies a leg sweeps with when <see cref="Workers"/> is left out.</summary>
    /// <remarks>
    /// Two, and not one per core: a sweep runs inside one of several legs a run starts together, each worker is a
    /// whole copy of the tree with a build directory of its own, and the machines this tool serves include a small
    /// server whose disk two of them already fill.
    /// </remarks>
    public const int DefaultWorkers = 2;

    /// <summary>How many times the unmutated run's duration a mutated run may take, when <see cref="RunTimeFactor"/> is left out.</summary>
    public const double DefaultRunTimeFactor = 10;

    /// <summary>
    /// The arms registry, relative to the repository root, or <see langword="null"/> where the repository declares
    /// none, and <c>check-mutations</c> refuses to sweep, naming this key. Carried by sync like the rest of the tree,
    /// so a host reads the registry its copy holds: one inside the harness's own directory, which sync never carries
    /// but for its runner actions, is refused when the file is read, and so is one the configuration's own sync
    /// settings or its worktrees root keep a sync from carrying.
    /// </summary>
    [Description("the arms registry, relative to the repository root")]
    public string? Registry { get; init; }

    /// <summary>
    /// The directory, relative to the repository root, holding the texts the registry's rows cite - each mutation's
    /// before and after, each diagnostic, each control - every file of which some row must cite: a text nobody cites is
    /// a mutation nobody drives. Left out, the texts may lie anywhere in the tree, and nothing checks that each is
    /// cited. Carried by sync as the registry is, and refused where it would not be, as the registry is.
    /// </summary>
    [Description("the directory of the texts the registry cites, every file cited")]
    public string? TextDirectory { get; init; }

    /// <summary>
    /// Worker copies a leg sweeps its arms with at once, at least 1: fewer where the leg has fewer arms, or its
    /// machine fewer rooms for them, and one in a WSL distribution.
    /// </summary>
    [Description("worker copies a leg sweeps with at once, at least 1 (2)")]
    public int Workers { get; init; } = DefaultWorkers;

    /// <summary>
    /// The arguments that make a test binary write a JUnit XML report, one of which names <c>{report}</c> - filled in
    /// with the file each run writes, new for every run - such as <c>["--gtest_output=xml:{report}"]</c> for
    /// GoogleTest, <c>["--reporter", "JUnit::out={report}"]</c> for Catch2 or <c>["--reporters=junit",
    /// "--out={report}"]</c> for doctest. Required where an arm runs its binary.
    /// </summary>
    /// <remarks>
    /// A list, and not one argument: the runners that write a report under an option of its own, as doctest does, take
    /// two. Given to the binary after nothing else, since the arms run it whole: a filter would leave out the case a
    /// mutation reddens in place of the one its arm names.
    /// </remarks>
    [Description("the arguments a test binary writes its JUnit report to {report} with")]
    public List<string>? ReportArgs { get; init; }

    /// <summary>
    /// How many times the duration of a test binary's unmutated run one of its mutated runs may take before it is
    /// stopped as hung, above 1. Never less than that run's duration and a minute: a binary that takes a second
    /// unmutated is not stopped at ten.
    /// </summary>
    /// <remarks>
    /// A bound on the run's whole duration, which the harness otherwise never sets for a phase: a mutation that turns
    /// a loop endless prints nothing more, forever, and the unmutated run of the very same binary, measured minutes
    /// before on the same machine, says how long it takes when nothing is wrong.
    /// </remarks>
    [Description("how many times the unmutated run a mutated run may take, above 1 (10)")]
    public double RunTimeFactor { get; init; } = DefaultRunTimeFactor;
}
