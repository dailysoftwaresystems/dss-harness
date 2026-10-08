namespace RepoHarness.Tests;

/// <summary>
/// The workflows that run this suite, as written: embedded into the tests, so a test can hold a step of one to what it
/// has to say. Nothing runs a workflow here; what a step says is all a test of it can read.
/// </summary>
internal static class WorkflowFiles
{
    /// <summary>
    /// The one step of <paramref name="workflow"/> whose run line names <paramref name="runs"/>: its lines from its name
    /// to that line, each without the space around it.
    /// </summary>
    /// <param name="workflow">The workflow's file name, under <c>.github/workflows</c>.</param>
    /// <param name="runs">What the step's run line names.</param>
    public static IReadOnlyList<string> StepRunning(string workflow, string runs)
    {
        using var held = typeof(WorkflowFiles).Assembly.GetManifestResourceStream("workflows/" + workflow);

        Assert.NotNull(held);

        using var reader = new StreamReader(held);
        var lines = reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n');
        var run = Assert.Single(
            Enumerable.Range(0, lines.Length),
            line => lines[line].TrimStart().StartsWith("run:", StringComparison.Ordinal) && lines[line].Contains(runs, StringComparison.Ordinal));
        var named = Array.FindLastIndex(lines, run, line => line.TrimStart().StartsWith("- name:", StringComparison.Ordinal));

        Assert.True(named >= 0, $"No step of '{workflow}' is named above the line that runs {runs}.");

        return [.. lines[named..(run + 1)].Select(line => line.Trim())];
    }
}
