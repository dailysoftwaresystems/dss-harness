using Xunit.Sdk;

namespace RepoHarness.Tests;

/// <summary>
/// A test that really builds, on a machine lacking what it needs: skipped, naming what is lacked - and failed where the
/// machine says it is meant to hold every build tool, as the continuous integration runners do.
/// </summary>
public sealed class BuildToolsTests
{
    /// <summary>A machine that holds every program goes on, whatever it says of itself.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    public void AMachineHoldingEveryProgram_GoesOn(string? required)
        => BuildTools.Need(program => "/usr/bin/" + program, _ => required, "a real build", "cmake", "ninja");

    /// <summary>A machine lacking one skips the test, naming each it lacks and no other.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMachineLackingAProgram_SkipsTheTest_NamingWhatItLacks(string? required)
    {
        var skipped = Raised(() => BuildTools.Need(
            program => program == "cmake" ? "/usr/bin/cmake" : null,
            name => name == BuildTools.RequiredVariable ? required : "1",
            "a real build",
            "cmake",
            "ninja",
            "cc"));

        Assert.EndsWith("This machine lacks ninja, cc, which a real build needs.", Skip(skipped).Message, StringComparison.Ordinal);
    }

    /// <summary>A machine that says it is meant to hold them fails the test instead: there a skip would be green for good.</summary>
    [Fact]
    public void AMachineMeantToHoldThem_FailsTheTest_NamingWhatItLacksAndWhatSaysSo()
    {
        var failed = Raised(() => BuildTools.Need(
            _ => null,
            name => name == BuildTools.RequiredVariable ? "1" : null,
            "a real build",
            "ninja"));

        Assert.Equal(
            "This machine lacks ninja, which a real build needs. DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS is set, which says this machine "
            + "is meant to hold every build tool: install what it lacks, or unset it.",
            Failure(failed).Message);
    }

    /// <summary>
    /// What a machine of its kind could never hold - Visual Studio, anywhere but Windows - is skipped whatever the
    /// machine says of itself; what it could hold is failed for where it says it is meant to.
    /// </summary>
    [Fact]
    public void WhatAMachineCouldNeverHold_IsSkipped_WhateverItSaysOfItself()
    {
        const string Lacked = "This machine has no Visual Studio with the C++ build tools.";

        var elsewhere = Raised(() => BuildTools.Lacks(_ => "1", Lacked, couldHold: false));
        var here = Raised(() => BuildTools.Lacks(_ => "1", Lacked));
        var unsaid = Raised(() => BuildTools.Lacks(_ => null, Lacked));

        Assert.EndsWith(Lacked, Skip(elsewhere).Message, StringComparison.Ordinal);
        Assert.StartsWith(Lacked + " DSSHARNESS_TESTS_REQUIRE_BUILD_TOOLS is set", Failure(here).Message, StringComparison.Ordinal);
        Assert.EndsWith(Lacked, Skip(unsaid).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each workflow that runs this suite says its runner is meant to hold every build tool, by the very variable the
    /// suite reads, on the very step that runs it: spelt otherwise there, or set on another step, every real build
    /// would again be skipped green on a runner that lost a tool - which is all the variable is for.
    /// </summary>
    [Theory]
    [InlineData("test.yml")]
    [InlineData("pipeline-pkg.yml")]
    public void EachWorkflowRunningThisSuite_SaysItsRunnerHoldsEveryBuildTool(string workflow)
    {
        var step = WorkflowFiles.StepRunning(workflow, "repo-harness-test");

        // Among the step's own variables, the last of them: set just above the line that runs the suite.
        Assert.Contains("env:", step);
        Assert.Equal($"{BuildTools.RequiredVariable}: \"1\"", step[^2]);
    }

    /// <summary>
    /// What <paramref name="act"/> raises, a skip among it: the framework's own recorder hands a skip on, since a test
    /// raising one is skipped, and here the skip is what is asked about.
    /// </summary>
    private static Exception? Raised(Action act)
    {
        try
        {
            act();
            return null;
        }
        catch (Exception ex) when (ex is XunitException)
        {
            return ex;
        }
    }

    /// <summary><paramref name="raised"/>, which a test raises to be skipped.</summary>
    private static SkipException Skip(Exception? raised) => Assert.IsType<SkipException>(raised);

    /// <summary><paramref name="raised"/>, which fails a test: an assertion's, and no skip.</summary>
    private static XunitException Failure(Exception? raised)
    {
        Assert.IsNotType<SkipException>(raised);

        return Assert.IsAssignableFrom<XunitException>(raised);
    }
}
