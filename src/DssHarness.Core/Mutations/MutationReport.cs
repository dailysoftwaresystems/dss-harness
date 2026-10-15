using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// How a test binary is told where to write the JUnit report a run is judged by: <c>mutations.reportArgs</c>, each
/// filled in with the report's path where it names <c>{report}</c>.
/// </summary>
public static class MutationReport
{
    /// <summary>The one name <c>mutations.reportArgs</c> may hold in braces: the report's path.</summary>
    public const string Placeholder = "report";

    /// <summary>The setting the arguments come from, as a refusal names it.</summary>
    public const string Setting = "mutations.reportArgs";

    /// <summary>The names <c>mutations.reportArgs</c> is filled in with: <see cref="Placeholder"/>, as <paramref name="reportPath"/>.</summary>
    /// <param name="reportPath">The file the run writes its report to.</param>
    public static IReadOnlyDictionary<string, string> Names(string reportPath)
        => new Dictionary<string, string>(StringComparer.Ordinal) { [Placeholder] = reportPath };

    /// <summary><paramref name="reportArgs"/>, each filled in with <paramref name="reportPath"/> where it names <c>{report}</c>.</summary>
    /// <param name="reportArgs">The configured arguments.</param>
    /// <param name="reportPath">The file the run writes its report to.</param>
    /// <exception cref="Results.HarnessException">An argument names something else in braces.</exception>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> reportArgs, string reportPath)
    {
        ArgumentNullException.ThrowIfNull(reportArgs);

        var names = Names(reportPath);

        return [.. reportArgs.Select(argument => LegPathNames.Fill(argument, names, Setting))];
    }
}
