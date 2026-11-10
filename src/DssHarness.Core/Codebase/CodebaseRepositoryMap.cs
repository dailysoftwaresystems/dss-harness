using System.Text.Json.Nodes;
using DssHarness.Core.Output;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The repository map ("The repository map"): the tree at a glance, given at the head of a conversation's first message
/// - a run's, made once from the base as it stands when its first graphed reason step is given its prompt and raised for
/// the run's task, and a side-task's, made once from the base as it stands when the side-task opens - a cache every later
/// message reads as it is, while the pack and the tool say what the tree is now.
/// </summary>
public static class CodebaseRepositoryMap
{
    /// <summary>
    /// The map of <paramref name="queries"/>'s view: a header line, the projects and top directories with their counts,
    /// then the most central declarations one line each as <see cref="GraphLines"/> writes a node - ranked by
    /// <see cref="CodebaseGraphQueries.Central"/>, no test and nothing generated, raised where their names hold
    /// <paramref name="terms"/>' words - capped by <paramref name="tokens"/>, cut at whole lines with the header always
    /// kept. A cap of 0 gives no map: empty text, and the meta saying 0 tokens.
    /// </summary>
    /// <param name="queries">The view, as the run's or the side-task's sync left it.</param>
    /// <param name="terms">The task the map is made for - a run's inputs and the issue it triages - or empty for none, as a side-task's is.</param>
    /// <param name="tokens">The map's cap in estimated tokens; 0 gives none.</param>
    /// <param name="readable">Whether the steps the map is given may read a file, by its path relative to the tree: a project or a declaration of a file they may not is never listed; null where they may read every one.</param>
    public static (string Text, JsonNode Meta) Make(CodebaseGraphQueries queries, string terms, int tokens, Func<string, bool>? readable = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);

        if (tokens == 0)
        {
            return (string.Empty, Meta(queries, declarations: 0, text: string.Empty));
        }

        var lines = new List<string>
        {
            $"{(queries.Head is { } head ? $"at commit {head}, " : string.Empty)}"
                + $"{ReportText.Counted(queries.FileCount, "file")} and {ReportText.Counted(queries.DeclarationCount, "declaration")}: the graph tool answers for the tree as it is now",
        };

        var overview = queries.Overview();
        lines.AddRange(overview.Projects.Where(project => readable?.Invoke(project.Path) != false).Select(project => $"{project.Qualified} ({GraphLines.Of(project)})"));
        lines.AddRange(overview.Directories.Select(directory => $"{directory.Directory} {directory.Files.Count} files"));

        var central = queries.Central(terms).Where(card => readable?.Invoke(card.Path) != false).ToList();

        foreach (var card in central)
        {
            lines.Add(GraphLines.Of(card));
        }

        var text = Cap(lines, tokens);

        return (text, Meta(queries, central.Count, text));
    }

    /// <summary>What a map was made of: the view's commit and files, the declarations ranked for it, and its tokens.</summary>
    private static JsonObject Meta(CodebaseGraphQueries queries, int declarations, string text)
        => new()
        {
            ["head"] = queries.Head,
            ["files"] = queries.FileCount,
            ["declarations"] = declarations,
            ["tokens"] = EstimatedTokens.Of(text),
        };

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
