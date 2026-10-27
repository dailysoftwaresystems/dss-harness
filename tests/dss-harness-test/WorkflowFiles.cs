namespace DssHarness.Tests;

/// <summary>
/// The workflow that runs this suite, and the two that release this tool, as written: embedded into the tests, so a
/// test can hold a job or a step of one to what it has to say. Nothing runs a workflow here; what one says is all a
/// test of it can read.
/// </summary>
internal static class WorkflowFiles
{
    /// <summary>
    /// The one step of <paramref name="workflow"/> whose run line, or the script its run block holds, names
    /// <paramref name="runs"/>: its lines from its name to the line that names it, each without the space around it.
    /// </summary>
    /// <param name="workflow">The workflow's file name, under <c>.github/workflows</c>.</param>
    /// <param name="runs">What the step's run line or script names.</param>
    public static IReadOnlyList<string> StepRunning(string workflow, string runs)
    {
        var lines = Lines(workflow);
        var run = Assert.Single(
            Enumerable.Range(0, lines.Length),
            line => lines[line].Contains(runs, StringComparison.Ordinal) && (IsRun(lines[line]) || InRunBlock(lines, line)));
        var named = Array.FindLastIndex(lines, run, line => line.TrimStart().StartsWith("- name:", StringComparison.Ordinal));

        Assert.True(named >= 0, $"No step of '{workflow}' is named above the line that runs {runs}.");

        return [.. lines[named..(run + 1)].Select(line => line.Trim())];
    }

    /// <summary>
    /// The job of <paramref name="workflow"/> keyed <paramref name="job"/>: its lines, down to the next job's, each
    /// without the space around it.
    /// </summary>
    /// <param name="workflow">The workflow's file name, under <c>.github/workflows</c>.</param>
    /// <param name="job">The job's key, as <c>needs</c> names it.</param>
    public static IReadOnlyList<string> Job(string workflow, string job)
    {
        var lines = Lines(workflow);
        var keyed = Assert.Single(Enumerable.Range(0, lines.Length), line => lines[line].TrimEnd() == $"  {job}:");

        // A job's key is two spaces in: the next line no further in than that, a comment aside, is where this one ends.
        var next = Array.FindIndex(lines, keyed + 1, line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#') && Indent(line) <= 2);

        return [.. lines[keyed..(next < 0 ? lines.Length : next)].Select(line => line.Trim())];
    }

    /// <summary>
    /// The one step of <paramref name="workflow"/> named <paramref name="step"/>: its lines, down to the next step's,
    /// each without the space around it.
    /// </summary>
    /// <param name="workflow">The workflow's file name, under <c>.github/workflows</c>.</param>
    /// <param name="step">The step's name.</param>
    public static IReadOnlyList<string> StepNamed(string workflow, string step)
    {
        var lines = Lines(workflow);
        var named = Assert.Single(Enumerable.Range(0, lines.Length), line => lines[line].Trim() == "- name: " + step);
        var next = Array.FindIndex(lines, named + 1, line => line.Trim().Length > 0 && Indent(line) <= Indent(lines[named]));

        return [.. lines[named..(next < 0 ? lines.Length : next)].Select(line => line.Trim())];
    }

    /// <summary>The lines of <paramref name="workflow"/>, as written.</summary>
    /// <param name="workflow">The workflow's file name, under <c>.github/workflows</c>.</param>
    public static string[] Lines(string workflow)
    {
        using var held = typeof(WorkflowFiles).Assembly.GetManifestResourceStream("workflows/" + workflow);

        Assert.NotNull(held);

        using var reader = new StreamReader(held);

        return reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n');
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;

    private static bool IsRun(string line) => line.TrimStart().StartsWith("run:", StringComparison.Ordinal);

    /// <summary>Whether line <paramref name="at"/> is in the script a <c>run: |</c> block holds: the first line further out than it is that run.</summary>
    private static bool InRunBlock(string[] lines, int at)
    {
        var outer = Array.FindLastIndex(lines, at, line => line.Trim().Length > 0 && Indent(line) < Indent(lines[at]));

        return outer >= 0 && IsRun(lines[outer]) && lines[outer].TrimEnd().EndsWith('|');
    }
}
