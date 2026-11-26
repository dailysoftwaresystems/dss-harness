using System.Text.RegularExpressions;

namespace DssHarness.Tests;

/// <summary>
/// What the workflow that runs this suite on every runner says of how it shares the suite out: nothing here runs it, so
/// what it says is what a test of it can hold.
/// </summary>
public sealed class TestMatrixTests
{
    /// <summary>
    /// No awk of the matrix's is handed a value through <c>-v</c>, which reads the escapes in it: on Windows the runner's
    /// temporary directory, <c>D:\a\_temp</c>, came through as <c>D:</c>, a bell and <c>_temp</c>, the files under it were
    /// never read, and every class weighed alike while nothing said so. Each file reaches awk as an operand, which it opens
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
