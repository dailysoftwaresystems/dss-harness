using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The repository map ("The repository map"): the tree at a glance, given at the head of a conversation's first message
/// - a run's, made once for each ground of reads its steps share from the base as it stands when the first graphed reason
/// step of that ground is given its prompt, and raised for the run's task, and a side-task's, made once from the base as it stands when the side-task opens - a cache every later
/// message reads as it is, while the pack and the tool say what the tree is now.
/// </summary>
public static class CodebaseRepositoryMap
{
    /// <summary>
    /// The map of <paramref name="queries"/>'s view as pages (<see cref="GraphTextPages"/>, laid whole): a header line, the
    /// projects and top directories with their counts, then the most central declarations one line each as
    /// <see cref="GraphLines"/> writes a node - ranked by <see cref="CodebaseGraphQueries.Central"/>, no test and nothing
    /// generated, raised where their names hold <paramref name="terms"/>' words - every line on one page or another, none
    /// dropped: page 1 holds the header and the whole lines that fit <paramref name="tokens"/>, then its last line where
    /// more pages follow, and the pages after it hold the rest, the same size each. A size of 0 gives no map: no pages,
    /// and the meta saying 0 tokens.
    /// </summary>
    /// <param name="queries">The view, as the run's or the side-task's sync left it.</param>
    /// <param name="terms">The task the map is made for - a run's inputs and the issue it triages - or empty for none, as a side-task's is.</param>
    /// <param name="tokens">The map's page in estimated tokens; 0 gives none.</param>
    /// <param name="readable">Whether the steps the map is given may read a file, by its path relative to the tree: a project or a declaration of a file they may not is never listed; null where they may read every one.</param>
    public static (IReadOnlyList<string> Pages, JsonNode Meta) Make(CodebaseGraphQueries queries, string terms, int tokens, Func<string, bool>? readable = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentOutOfRangeException.ThrowIfNegative(tokens);

        if (tokens == 0)
        {
            return ([], Meta(queries, declarations: 0, text: string.Empty));
        }

        var header = $"{(queries.Head is { } head ? $"at commit {head}, " : string.Empty)}"
            + $"{ReportText.Counted(queries.FileCount, "file")} and {ReportText.Counted(queries.DeclarationCount, "declaration")}: the graph tool answers for the tree as it is now";

        var overview = queries.Overview();
        var central = queries.Central(terms).Where(card => readable?.Invoke(card.Path) != false).ToList();

        List<GraphTextLine> lines =
        [
            .. overview.Projects.Where(project => readable?.Invoke(project.Path) != false).Select(project => new GraphTextLine(null, $"{project.Qualified} ({GraphLines.Of(project)})")),
            .. overview.Directories.Select(directory => new GraphTextLine(null, $"{directory.Directory} {directory.Files.Count} files")),
            .. central.Select(card => new GraphTextLine(null, GraphLines.Of(card))),
        ];

        // The map's page is its tokens' bytes, and its header - page 1's first line - is held whatever the size. A page's
        // room is counted a line's end each, a text joined once between its lines: one byte more than the tokens holds
        // the lines whose joined text is within them, so a page's lines, joined, are within its tokens.
        var room = Math.Max((long)tokens * EstimatedTokens.BytesPerToken + 1, Encoding.UTF8.GetByteCount(header) + 1);
        var pages = new GraphTextPages(lines, room, lastLineRoom: 0, number => number == 1 ? header : $"repository map page {Number(number)}", Last).All;

        return ([.. pages.Select(page => page.Text)], Meta(queries, central.Count, pages[0].Text));
    }

    /// <summary>
    /// A page's last line: none where the map is one page; else which page of how many, and - one map serving the steps of
    /// its ground of reads whatever their graph - where the rest is, in the words a step without the tool reads as plainly
    /// as one with it.
    /// </summary>
    private static string? Last(GraphTextPageEnd end)
        => end is { Last: true, Page: 1 } ? null
        : end.Last ? $"map page {Number(end.Page)} of {Number(end.Pages)} · the last"
        : $"map page {Number(end.Page)} of {Number(end.Pages)} · the rest is on the pages after it, which a step whose graph is tools asks the graph for";

    /// <summary>
    /// How many pages the map whose first page is <paramref name="first"/> holds, as that page's own last line says it -
    /// the one count of a kept map, read from the page every step of its ground is given, so what a step is told goes on
    /// is what it may ask for: 1 where the page ends with no such line, a map of one page.
    /// </summary>
    /// <param name="first">The map's first page, whole, as it was kept.</param>
    public static int PagesOf(string first)
    {
        ArgumentNullException.ThrowIfNull(first);

        const string Opening = "map page 1 of ";
        var line = first[(first.LastIndexOf('\n') + 1)..];
        var digits = line.StartsWith(Opening, StringComparison.Ordinal) ? line[Opening.Length..].TakeWhile(char.IsAsciiDigit).Count() : 0;

        // The line is read back only where it is, word for word, the last line this map gives a first page of that many.
        return digits > 0
            && int.TryParse(line.AsSpan(Opening.Length, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var pages)
            && pages > 1
            && string.Equals(Last(new GraphTextPageEnd(1, [], pages, 0)), line, StringComparison.Ordinal)
                ? pages
                : 1;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a map was made of: the view's commit and files, the declarations ranked for it, and its first page's tokens.</summary>
    private static JsonObject Meta(CodebaseGraphQueries queries, int declarations, string text)
        => new()
        {
            ["head"] = queries.Head,
            ["files"] = queries.FileCount,
            ["declarations"] = declarations,
            ["tokens"] = EstimatedTokens.Of(text),
        };
}
