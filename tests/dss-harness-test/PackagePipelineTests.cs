namespace DssHarness.Tests;

/// <summary>
/// What the workflow that publishes a release says of this suite - it never runs it - and what the workflow that starts
/// it says, which is what makes the runs it starts safe to publish from.
/// </summary>
public sealed class PackagePipelineTests
{
    /// <summary>
    /// The workflow that publishes a release runs this suite on no commit - not by the test project's name, not by
    /// <c>dotnet test</c>, and not by calling the matrix workflow as Deploy and the pull-request pipeline do: a run
    /// Deploy started has the full matrix behind it, and a run a person starts publishes its release branch as it
    /// stands.
    /// </summary>
    [Fact]
    public void TheSuite_NeverRunsInTheWorkflowThatPublishes()
    {
        var lines = WorkflowFiles.Lines("pipeline-pkg.yml");

        Assert.DoesNotContain(lines, line => line.Contains(typeof(WorkflowFiles).Assembly.GetName().Name!, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("dotnet test", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("test.yml", StringComparison.Ordinal));
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
