using RepoHarness.Core.Anchors;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Orchestration;

/// <summary>
/// The anchor rows an agent files, in <c>agents/&lt;agent&gt;/rows/</c>: a directory for each row, named for its anchor,
/// holding a file for each cell - <c>status.txt</c>, <c>trigger.txt</c>, <c>closing.txt</c> and <c>cross-refs.txt</c>, and
/// <c>priority.txt</c> where it declares one. Cells go by file, never by argument: a long row crosses the longest command
/// line Windows takes.
/// </summary>
/// <remarks>
/// Read strictly, every entry accounted for: a draft left beside the cells, or a row missing a cell, is refused by name,
/// never skipped - a row skipped is a row that dies with the worktree unapplied. Each cell's file is read as the anchor
/// commands read a <c>--&lt;cell&gt;-file</c> (<see cref="AnchorCellInputs"/>): UTF-8 and nothing else. No rows directory is
/// no rows.
/// </remarks>
public static class AgentRows
{
    /// <summary>The extension of every cell's file.</summary>
    public const string CellExtension = ".txt";

    /// <summary>The cell a row may leave out: an existing row keeps its own, and a new row must declare one.</summary>
    public const string PriorityCell = "priority";

    /// <summary>The cells every row declares.</summary>
    public static IReadOnlyList<string> RequiredCells { get; } = ["status", "trigger", "closing", "cross-refs"];

    /// <summary>The rows filed in <paramref name="directory"/>, in the order of their ids, and every problem found reading them.</summary>
    /// <param name="fileSystem">Reads the files.</param>
    /// <param name="directory">An agent's rows directory.</param>
    public static (IReadOnlyList<AnchorRowDeclaration> Rows, IReadOnlyList<string> Problems) Read(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var rows = new List<AnchorRowDeclaration>();
        var problems = new List<string>();

        PathKind kind;

        try
        {
            kind = fileSystem.KindOf(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ([], [$"whether '{directory}' holds rows cannot be told: {ex.Message.TrimEnd('.')}"]);
        }

        switch (kind)
        {
            case PathKind.None:
                return ([], []);
            case PathKind.Link:
                return ([], [$"'{directory}' is a link, and rows are read from the agent's own directory only"]);
            case PathKind.File:
                return ([], [$"'{directory}' is a file, where an agent's rows are a directory for each"]);
        }

        foreach (var file in fileSystem.EnumerateFiles(directory, recursive: false).Order(StringComparer.Ordinal))
        {
            problems.Add($"'{file}' is a file directly in the rows directory: each row is a directory named for its anchor, holding a file for each cell");
        }

        foreach (var row in fileSystem.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(row));

            if (fileSystem.IsLink(row))
            {
                problems.Add($"'{id}' is a link, and a row is read from its own files only");
                continue;
            }

            var cells = new Dictionary<string, string>(StringComparer.Ordinal);
            var unread = new HashSet<string>(StringComparer.Ordinal);
            var sound = true;

            foreach (var entry in fileSystem.EnumerateDirectories(row).Order(StringComparer.Ordinal))
            {
                problems.Add($"'{id}' holds the directory '{Path.GetFileName(Path.TrimEndingDirectorySeparator(entry))}': keep drafts out of the rows directory");
                sound = false;
            }

            foreach (var file in fileSystem.EnumerateFiles(row, recursive: false).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var cell = name.EndsWith(CellExtension, StringComparison.Ordinal) ? name[..^CellExtension.Length] : null;

                if (cell is null || !(RequiredCells.Contains(cell, StringComparer.Ordinal) || cell == PriorityCell))
                {
                    problems.Add($"'{id}' holds '{name}', which is not one of its cells ({string.Join(", ", RequiredCells.Append(PriorityCell).Select(known => known + CellExtension))}): keep drafts out of the rows directory");
                    sound = false;
                    continue;
                }

                if (fileSystem.IsLink(file))
                {
                    problems.Add($"'{id}/{name}' is a link, and a cell is read from its own file only");
                    sound = false;
                    continue;
                }

                try
                {
                    cells[cell] = AnchorCellInputs.Resolve(fileSystem, new AnchorCellInput(cell, string.Empty, $"Row '{id}'", Inline: null, File: file))!;
                }
                catch (HarnessException ex)
                {
                    problems.Add(ex.Message);
                    unread.Add(cell);
                    sound = false;
                }
            }

            // A cell whose file was there and could not be read is refused as that, and never named missing as well.
            var missing = RequiredCells.Where(required => !cells.ContainsKey(required) && !unread.Contains(required)).ToList();

            if (missing.Count > 0)
            {
                problems.Add($"'{id}' is missing {string.Join(", ", missing.Select(cell => cell + CellExtension))}: a row declares every one of its cells but its priority");
                sound = false;
            }

            if (sound)
            {
                rows.Add(new AnchorRowDeclaration(id, cells["status"].Trim(), cells["trigger"], cells["closing"], cells["cross-refs"])
                {
                    Priority = cells.TryGetValue(PriorityCell, out var priority) ? priority.Trim() : null,
                });
            }
        }

        return (rows, problems);
    }
}
