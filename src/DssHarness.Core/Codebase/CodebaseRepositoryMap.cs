using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The repository map a side-task is given with its first message (part 08, "The repository map"): the tree at a glance,
/// made once from the base as it stands when the side-task opens - a cache every later message reads as it is, while the
/// pack and the tool say what the tree is now.
/// </summary>
public static class CodebaseRepositoryMap
{
    /// <summary>
    /// The map of <paramref name="queries"/>'s view: a header line, the projects and top directories with their counts,
    /// then the most central declarations one line each as <see cref="GraphLines"/> writes a node - a declaration's
    /// centrality the number of other files whose references bind to it, raised where its name holds the task's terms -
    /// capped by <paramref name="tokens"/>, cut at whole lines.
    /// </summary>
    /// <param name="queries">The view, as the side-task's opening sync left it.</param>
    /// <param name="terms">The words of the task the map is made for - a side-task's first message - split as a name is.</param>
    /// <param name="tokens">The map's cap in estimated tokens; 0 gives none.</param>
    public static (string Text, JsonNode Meta) Make(CodebaseGraphQueries queries, string terms, int tokens)
    {
        ArgumentNullException.ThrowIfNull(queries);

        var lines = new List<string>
        {
            $"{(queries.Head is { } head ? $"at commit {head}, " : string.Empty)}"
                + $"{ReportText.Counted(queries.FileCount, "file")} and {ReportText.Counted(queries.DeclarationCount, "declaration")}: the graph tool answers for the tree as it is now",
        };

        var overview = queries.Overview();
        lines.AddRange(overview.Projects.Select(project => $"{project.Qualified} ({GraphLines.Of(project)})"));
        lines.AddRange(overview.Directories.Select(directory => $"{directory.Directory} {directory.Files.Count} files"));

        var words = CodebaseGraphQueries.Split(terms).Distinct(StringComparer.Ordinal).ToArray();
        var central = queries.Central(words);

        foreach (var card in central)
        {
            lines.Add(GraphLines.Of(card));
        }

        var text = Cap(lines, tokens);

        return (text, new JsonObject
        {
            ["head"] = queries.Head,
            ["files"] = queries.FileCount,
            ["declarations"] = central.Count,
            ["tokens"] = EstimatedTokens.Of(text),
        });
    }

    /// <summary>
    /// The lines that fit <paramref name="tokens"/> estimated tokens, cut at a whole line with the header always kept.
    /// </summary>
    private static string Cap(List<string> lines, int tokens)
    {
        var kept = new List<string>();

        foreach (var line in lines)
        {
            kept.Add(line);

            if (EstimatedTokens.Of(string.Join('\n', kept)) > tokens && kept.Count > 1)
            {
                kept.RemoveAt(kept.Count - 1);
                break;
            }
        }

        return string.Join('\n', kept);
    }
}
