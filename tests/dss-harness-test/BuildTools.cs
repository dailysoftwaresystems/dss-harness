using System.Diagnostics.CodeAnalysis;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// What a test that really builds needs on the machine running it: cmake, ninja, a compiler, or Visual Studio's own.
/// </summary>
/// <remarks>
/// A machine without them skips such a test, since nothing about the code can be said there - and a skip is green. So a
/// machine meant to hold them says so, by setting <see cref="RequiredVariable"/>, and fails the test instead: the
/// continuous integration runners set it, where a runner image that lost a tool would otherwise have every real build
/// skipped on it for good, and nothing say so.
/// </remarks>
internal static class BuildTools
{
    /// <summary>The environment variable a machine sets, to anything but nothing, to say it is meant to hold every build tool.</summary>
    public const string RequiredVariable = "DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS";

    /// <summary>
    /// Skips the test where this machine lacks one of <paramref name="programs"/>, naming each it lacks - or fails it,
    /// where the machine says it is meant to hold them.
    /// </summary>
    /// <param name="processes">Finds a program as a build would find it.</param>
    /// <param name="needs">What needs them, as the reason ends: <c>a real build</c>.</param>
    /// <param name="programs">The programs.</param>
    public static void Need(IProcessRunner processes, string needs, params string[] programs)
        => Need(processes.FindExecutable, Environment.GetEnvironmentVariable, needs, programs);

    /// <inheritdoc cref="Need(IProcessRunner, string, string[])"/>
    /// <param name="find">Where a program is, or <see langword="null"/> where this machine has none.</param>
    /// <param name="variable">What an environment variable holds, or <see langword="null"/>.</param>
    /// <param name="needs">What needs them, as the reason ends.</param>
    /// <param name="programs">The programs.</param>
    public static void Need(Func<string, string?> find, Func<string, string?> variable, string needs, params string[] programs)
    {
        var lacked = programs.Where(program => find(program) is null).ToList();

        if (lacked.Count > 0)
        {
            Lacks(variable, $"This machine lacks {string.Join(", ", lacked)}, which {needs} needs.");
        }
    }

    /// <summary>
    /// Skips the test for what <paramref name="lacked"/> says this machine lacks - or fails it, where the machine says
    /// it is meant to hold every build tool, and this is one it could hold.
    /// </summary>
    /// <param name="lacked">What this machine lacks, a sentence: the skip's reason, and how the failure begins.</param>
    /// <param name="couldHold">
    /// Whether a machine of this kind could hold it at all: Visual Studio is Windows', and no other system is meant to
    /// hold it, whatever it says of itself.
    /// </param>
    [DoesNotReturn]
    public static void Lacks(string lacked, bool couldHold = true)
        => Lacks(Environment.GetEnvironmentVariable, lacked, couldHold);

    /// <inheritdoc cref="Lacks(string, bool)"/>
    /// <param name="variable">What an environment variable holds, or <see langword="null"/>.</param>
    /// <param name="lacked">What this machine lacks, a sentence.</param>
    /// <param name="couldHold">Whether a machine of this kind could hold it at all.</param>
    [DoesNotReturn]
    public static void Lacks(Func<string, string?> variable, string lacked, bool couldHold = true)
    {
        Assert.False(
            couldHold && variable(RequiredVariable) is { Length: > 0 },
            $"{lacked} {RequiredVariable} is set, which says this machine is meant to hold every build tool: install what it lacks, or unset it.");
        Assert.Skip(lacked);
    }
}
