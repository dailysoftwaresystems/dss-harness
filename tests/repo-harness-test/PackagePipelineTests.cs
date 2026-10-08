namespace RepoHarness.Tests;

/// <summary>
/// What the workflow that publishes a release says of this suite: run for the one commit nothing else tested, and for no
/// other.
/// </summary>
public sealed class PackagePipelineTests
{
    /// <summary>
    /// The suite runs only where nothing else tested the commit being published. Never in a run Deploy started, which
    /// names the commit it promoted: the full matrix passed on that commit, or for beta on its parent, which differs
    /// only in the version. Never in a run resuming a commit an earlier run tagged: that run's package job passed, by
    /// this same rule, before its tag was made. And never where the version is on nuget.org already, and nothing is
    /// built. What is left is a run started by hand on a commit no run has packaged - a release branch moved past the
    /// rules that guard it has nothing else behind it.
    /// </summary>
    [Fact]
    public void TheSuite_RunsOnlyForACommitNothingElseTested()
    {
        var step = WorkflowFiles.StepRunning("pipeline-pkg.yml", "repo-harness-test");

        Assert.Equal("- name: Test", step[0]);
        Assert.Equal(
            "if: env.ON_NUGET != 'true' && inputs.expected_sha == '' && steps.resume.outputs.tag_exists != 'true'",
            Assert.Single(step, line => line.StartsWith("if:", StringComparison.Ordinal)));
    }
}
