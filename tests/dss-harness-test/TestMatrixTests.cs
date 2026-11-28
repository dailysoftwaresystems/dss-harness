using System.Text.RegularExpressions;

namespace DssHarness.Tests;

/// <summary>
/// What the workflow that runs this suite on every runner says of how it shares the suite out: nothing here runs it, so
/// what it says is what a test of it can hold.
/// </summary>
public sealed class TestMatrixTests
{
    /// <summary>
    /// Every shard of a run runs the classes the run's one deal handed it: the deal job, which the matrix needs, deals
    /// every runner's shards before any starts and gives the matrix as its output, and a shard reads its classes from
    /// that deal, never from a dealing or a listing of its own. A shard that deals itself reads whichever times another
    /// shard of the same run has just saved, so two shards of one runner dealt from different numbers run a class
    /// twice, or never, while the check goes green, and a failed shard run again deals itself anew.
    /// </summary>
    [Fact]
    public void EveryShard_RunsTheClassesTheRunsOneDealHandedIt()
    {
        var deal = WorkflowFiles.Job("test.yml", "deal");
        var test = WorkflowFiles.Job("test.yml", "test");

        Assert.Contains("matrix: ${{ steps.deal.outputs.matrix }}", deal);
        Assert.Contains("echo \"matrix={\\\"include\\\":[$matrix]}\" >> \"$GITHUB_OUTPUT\"", WorkflowFiles.StepNamed("test.yml", "Deal"));
        Assert.Contains("needs: deal", test);
        Assert.Contains("matrix: ${{ fromJSON(needs.deal.outputs.matrix) }}", test);

        // The deal reaches each shard as the artifact the deal job hands on, read before the suite runs.
        Assert.Contains("name: test-deal", WorkflowFiles.StepNamed("test.yml", "Hand on the deal"));
        Assert.Contains("name: test-deal", test);

        var shard = WorkflowFiles.StepNamed("test.yml", "Test");

        Assert.All(shard, line => Assert.Contains(line, test));
        Assert.Contains("--filter-class \"$@\"", shard);
        Assert.Contains("RUNS_ON: ${{ matrix.os }}", shard);
        Assert.Contains("SHARD: ${{ matrix.shard }}", shard);
        Assert.Contains("done < \"$deal/shards/$RUNS_ON/$SHARD.txt\"", shard);
        Assert.DoesNotContain(test, line => line.Contains("--xunit-list", StringComparison.Ordinal) || line.Contains("--list-tests", StringComparison.Ordinal));
    }

    /// <summary>
    /// The times a deal weighs are read once a run, by the deal alone, and kept once, by the job after every shard -
    /// whether the shards passed or not - under the key the next run's deal restores by: no shard reads or writes them,
    /// so no shard of a run is dealt from a file another shard of it wrote.
    /// </summary>
    [Fact]
    public void OnlyTheDealReadsTheTimes_AndOnlyTheJobAfterTheShardsKeepsThem()
    {
        const string Key = "key: test-class-times-${{ github.run_id }}-${{ github.run_attempt }}";
        var lines = WorkflowFiles.Lines("test.yml").Select(line => line.Trim()).ToList();
        var deal = WorkflowFiles.Job("test.yml", "deal");
        var times = WorkflowFiles.Job("test.yml", "times");

        Assert.Contains(Assert.Single(lines, line => line.Contains("actions/cache/restore@", StringComparison.Ordinal)), deal);
        Assert.Contains(Assert.Single(lines, line => line.Contains("actions/cache/save@", StringComparison.Ordinal)), times);
        Assert.Contains(Key, deal);
        Assert.Contains("restore-keys: test-class-times-", deal);
        Assert.Contains(Key, times);
        Assert.Contains("needs: [deal, test]", times);
        Assert.Contains("if: always() && needs.deal.result == 'success'", times);
    }

    /// <summary>
    /// A class the deal would leave out, or deal twice, fails the run rather than pass unrun: the deal holds the suite's
    /// two listings - its classes, and its tests by name - to one set of classes, and each runner's shards to every class
    /// once, each shard one at least, and ends the job, saying why, where any of them does not hold.
    /// </summary>
    [Fact]
    public void AClassTheDealWouldLeaveOut_FailsTheRun()
    {
        var deal = WorkflowFiles.StepNamed("test.yml", "Deal").ToList();

        foreach (var check in new[]
        {
            "if ! cut -d ' ' -f 2 \"$deal/tests.txt\" | LC_ALL=C sort | cmp -s - \"$deal/classes.txt\"; then",
            "if [ ! -s \"$deal/shards/$os/$shard.txt\" ]; then",
            "if ! cat \"$deal/shards/$os\"/*.txt | LC_ALL=C sort | cmp -s - \"$deal/classes.txt\"; then",
        })
        {
            var at = deal.IndexOf(check);

            Assert.True(at >= 0, $"The deal does not check: {check}");
            Assert.StartsWith("echo \"::error::", deal[at + 1], StringComparison.Ordinal);
            Assert.Equal("exit 1", deal[at + 2]);
        }
    }

    /// <summary>
    /// No awk of the matrix's is handed a value through <c>-v</c>, which reads the escapes in it: on Windows the runner's
    /// temporary directory, <c>D:\a\_temp</c>, comes through as <c>D:</c>, a bell and <c>_temp</c>, the files under it are
    /// never read, and every class weighs alike while nothing says so. Each file reaches awk as an operand, which it opens
    /// as named and fails on where it cannot, and each number through its environment.
    /// </summary>
    [Fact]
    public void NoAwkOfTheMatrix_IsHandedAValueThroughV()
    {
        var lines = WorkflowFiles.Lines("test.yml");

        // The matrix deals with awk at all, or the rule below holds of nothing.
        Assert.Contains(lines, line => Regex.IsMatch(line, @"\bawk\s+'"));
        Assert.DoesNotContain(lines, line => Regex.IsMatch(line, @"\bawk(\s+-\S+)*\s+-v\b"));
    }
}
