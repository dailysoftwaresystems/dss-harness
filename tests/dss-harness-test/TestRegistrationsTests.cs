using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// The registrations a test builds in process (<see cref="TestRegistrations"/>): their programs run through the built CLI,
/// and they are the only registrations a test builds, the composition root's own tests aside.
/// </summary>
public sealed class TestRegistrationsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A contained child of the registrations a test builds is placed by the built CLI's contain-exec and runs: placed by
    /// the program running the test - the test host, which answers contain-exec with its own help into a pipe nobody
    /// reads - it would never start, and a leg a test admitted by mistake would hang the test host instead of failing.
    /// </summary>
    [Fact]
    public async Task AContainedChildOfTheRegistrationsATestBuilds_IsPlacedByTheBuiltCli_AndRuns()
    {
        await using var services = TestRegistrations.Of(new SessionOptions { Command = "test", Prompting = false }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var ran = await services.GetRequiredService<IProcessRunner>()
            .RunAsync(TestHost.ChildRequest("echo-args", "a") with { Contained = true }, Token)
            .WaitAsync(TimeSpan.FromMinutes(2), Token);

        Assert.Equal((0, "[a]\n"), (ran.ExitCode, ran.StandardOutput.ReplaceLineEndings("\n")));
    }

    /// <summary>
    /// No test builds the tool's registrations in process but through <see cref="TestRegistrations"/>, save the
    /// composition root's own tests, whose subject is the root as a command builds it: a test that built them bare would
    /// start its contained children through the test host again. Read from the tests' own sources, so a registration a
    /// test adds anywhere turns this red.
    /// </summary>
    [Fact]
    public void EveryRegistrationATestBuildsInProcess_IsTheTestRegistrations()
    {
        var tests = Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test");
        var registered = $".{nameof(ServiceRegistration.AddDssHarness)}(";
        var bare = new List<string>();

        foreach (var file in Directory.EnumerateFiles(tests, "*.cs").Order(StringComparer.Ordinal))
        {
            if (BuildsTheRootBare.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && lines[i].Contains(registered, StringComparison.Ordinal))
                {
                    bare.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.Empty(bare);
    }

    /// <summary>The test sources that build the registrations bare, each with why.</summary>
    private static readonly HashSet<string> BuildsTheRootBare = new(StringComparer.Ordinal)
    {
        // The one owner of the registrations a test builds.
        $"{nameof(TestRegistrations)}.cs",

        // The root as a command builds it, which is its subject.
        $"{nameof(CompositionRootTests)}.cs",
    };
}
