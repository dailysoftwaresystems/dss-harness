namespace DssHarness.Tests;

/// <summary>
/// What the workflows that release this tool say of this suite - neither runs it - and what Deploy says of the package
/// run it starts, which is what makes that run publish the commit Deploy promoted.
/// </summary>
public sealed class PackagePipelineTests
{
    /// <summary>
    /// Neither the workflow that promotes a release nor the one that publishes it runs this suite on any commit - not by
    /// the test project's name, not by <c>dotnet test</c>, and not by calling the matrix workflow as the pull-request
    /// pipeline does: what they release passed the matrix before it reached <c>main</c>, through the check merging a
    /// pull request waits for.
    /// </summary>
    [Theory]
    [InlineData("deploy.yml")]
    [InlineData("pipeline-pkg.yml")]
    public void TheSuite_NeverRunsInAWorkflowThatReleases(string workflow)
    {
        var lines = WorkflowFiles.Lines(workflow);

        Assert.DoesNotContain(lines, line => line.Contains(typeof(WorkflowFiles).Assembly.GetName().Name!, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("dotnet test", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("test.yml", StringComparison.Ordinal));
    }

    /// <summary>
    /// A run Deploy started publishes the commit Deploy pinned - for beta, with only the version bumped on top - because
    /// of three things the workflows say, and only while they say all three: the promote job works on the very commit
    /// plan pinned, checked out by that commit and never by its branch, which may have moved since, and promotes what it
    /// then stands on; it starts the package run naming the commit it promoted; and the package run publishes nothing
    /// where its branch holds another. Without them, what is published need not be what was pinned.
    /// </summary>
    [Fact]
    public void WhatDeployStarts_PublishesWhatItPromoted_FromTheCommitItPinned_AndNoOther()
    {
        var promote = WorkflowFiles.Job("deploy.yml", "promote");

        Assert.Contains("needs: plan", promote);
        Assert.Contains("PINNED_SHA: ${{ needs.plan.outputs.sha }}", promote);

        var checkout = WorkflowFiles.StepNamed("deploy.yml", "Checkout the pinned commit");

        Assert.All(checkout, line => Assert.Contains(line, promote));
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
