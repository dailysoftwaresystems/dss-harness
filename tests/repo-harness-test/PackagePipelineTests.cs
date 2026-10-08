namespace RepoHarness.Tests;

/// <summary>
/// What the workflow that publishes a release says of this suite - run for the one commit nothing else tested, and for
/// no other - and what the workflow that starts it says, which is what makes the other runs safe to publish from.
/// </summary>
public sealed class PackagePipelineTests
{
    /// <summary>
    /// The suite runs only where nothing else tested the commit being published. Never in a run Deploy started, which
    /// names the commit it promoted: the full matrix passed on that commit, or for beta on its parent, which differs
    /// only in the version. Never in a run resuming a commit an earlier run tagged: that run's package job passed, by
    /// this same rule, before its tag was made. And never where the version is on nuget.org already, and nothing is
    /// built. What is left is a run started by hand that names no commit, on a commit no run has packaged - a release
    /// branch moved past the rules that guard it has nothing else behind it.
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

    /// <summary>
    /// A run Deploy started publishes without running the suite because of three things the workflows say, and only
    /// while they say all three: Deploy promotes a commit once the full matrix has passed on that very commit; it starts
    /// the package run naming the commit it promoted; and the package run publishes nothing where its branch holds
    /// another. Without the first, what is published was tested by nothing; without the others, it need not be what was.
    /// </summary>
    [Fact]
    public void WhatDeployStarts_PublishesTheCommitTheMatrixPassedOn_AndNoOther()
    {
        var matrix = WorkflowFiles.Job("deploy.yml", "test");
        var promote = WorkflowFiles.Job("deploy.yml", "promote");

        Assert.Contains("uses: ./.github/workflows/test.yml", matrix);
        Assert.Contains("ref: ${{ needs.plan.outputs.sha }}", matrix);
        Assert.Contains("needs: [plan, test]", promote);
        Assert.Contains("TESTED_SHA: ${{ needs.plan.outputs.sha }}", promote);

        var start = WorkflowFiles.StepNamed("deploy.yml", "Start the package pipeline");

        Assert.Contains("if ! gh workflow run pipeline-pkg.yml \\", start);
        Assert.Contains("-f expected_sha=\"$PROMOTED_SHA\"; then", start);

        var confirm = WorkflowFiles.StepNamed("pipeline-pkg.yml", "Confirm this is the promoted commit");

        Assert.Contains("if: inputs.expected_sha != ''", confirm);
        Assert.Contains("EXPECTED_SHA: ${{ inputs.expected_sha }}", confirm);
        Assert.Contains("actual=$(git rev-parse HEAD)", confirm);
        Assert.Contains("if [ \"$actual\" != \"$EXPECTED_SHA\" ]; then", confirm);
        Assert.Contains("exit 1", confirm);
    }
}
