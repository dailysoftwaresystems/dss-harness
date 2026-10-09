namespace RepoHarness.Tests;

/// <summary>
/// What the workflow that publishes a release says of this suite - run for the one commit nothing else tested, and for
/// no other - and what the workflow that starts it says, which is what makes the other runs safe to publish from.
/// </summary>
public sealed class PackagePipelineTests
{
    /// <summary>
    /// The suite runs only where nothing else tested the commit being published. Never in a run Deploy started - told by
    /// who started it, the workflow token Deploy starts it with, and not by anything typed: the full matrix passed on
    /// that commit, or for beta on its parent, which differs only in the version. Never in a run resuming a commit an
    /// earlier run tagged: that run's package job passed, by this same rule, before its tag was made. And never where
    /// the version is on nuget.org already, and nothing is built. What is left is a run a person started, on a commit no
    /// run has packaged - a release branch moved past the rules that guard it has nothing else behind it - whatever it
    /// names: told by the commit it names, a person's run that filled it in was taken for Deploy's and ran no suite.
    /// </summary>
    [Fact]
    public void TheSuite_RunsOnlyForACommitNothingElseTested()
    {
        var step = WorkflowFiles.StepRunning("pipeline-pkg.yml", "repo-harness-test");

        Assert.Equal("- name: Test", step[0]);
        Assert.Equal(
            "if: env.ON_NUGET != 'true' && github.actor != 'github-actions[bot]' && steps.resume.outputs.tag_exists != 'true'",
            Assert.Single(step, line => line.StartsWith("if:", StringComparison.Ordinal)));

        // A run started with the workflow's own token is github-actions[bot]'s, and stays so when a person runs it again.
        Assert.Contains("GH_TOKEN: ${{ github.token }}", WorkflowFiles.StepNamed("deploy.yml", "Start the package pipeline"));
    }

    /// <summary>
    /// A run Deploy started publishes without running the suite because of three things the workflows say, and only
    /// while they say all three: Deploy promotes a commit once the full matrix has passed on it - for beta on its
    /// parent, which differs only in the version; it starts the package run naming the commit it promoted; and the
    /// package run publishes nothing where its branch holds another. Without the first, what is published was tested by
    /// nothing; without the others, it need not be what was.
    /// </summary>
    /// <remarks>
    /// The first holds only while the promote job works on the very commit the matrix ran on - checked out by that
    /// commit, never by its branch, which may have moved since - and promotes what it then stands on, naming that same
    /// commit to the package run.
    /// </remarks>
    [Fact]
    public void WhatDeployStarts_PublishesWhatItPromoted_FromTheCommitTheMatrixPassedOn_AndNoOther()
    {
        var matrix = WorkflowFiles.Job("deploy.yml", "test");
        var promote = WorkflowFiles.Job("deploy.yml", "promote");

        Assert.Contains("uses: ./.github/workflows/test.yml", matrix);
        Assert.Contains("ref: ${{ needs.plan.outputs.sha }}", matrix);
        Assert.Contains("needs: [plan, test]", promote);
        Assert.Contains("TESTED_SHA: ${{ needs.plan.outputs.sha }}", promote);

        var checkout = WorkflowFiles.StepNamed("deploy.yml", "Checkout the tested commit");

        Assert.Contains(checkout, promote.Contains);
        Assert.Equal("ref: ${{ needs.plan.outputs.sha }}", Assert.Single(checkout, line => line.StartsWith("ref:", StringComparison.Ordinal)));

        var promoted = WorkflowFiles.StepNamed("deploy.yml", "Promote");

        Assert.Equal("promoted=$(git rev-parse HEAD)", Assert.Single(promoted, line => line.StartsWith("promoted=", StringComparison.Ordinal)));
        Assert.Equal(
            ["refs=(\"$promoted:refs/heads/main\" \"$promoted:refs/heads/$TARGET\")", "refs=(\"$promoted:refs/heads/$TARGET\")"],
            promoted.Where(line => line.StartsWith("refs=", StringComparison.Ordinal)));
        Assert.Contains("git push --atomic origin \"${refs[@]}\"; then", promoted);
        Assert.Equal(
            "echo \"PROMOTED_SHA=$promoted\" >> \"$GITHUB_ENV\"",
            Assert.Single(promoted, line => line.Contains("PROMOTED_SHA=", StringComparison.Ordinal)));

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
