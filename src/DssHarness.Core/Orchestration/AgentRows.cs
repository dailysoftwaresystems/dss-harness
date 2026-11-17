using DssHarness.Core.Anchors;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration;

/// <summary>An agent's rows as a fold weighs them: what applying those it declared anew does, or did, and those it did not.</summary>
/// <param name="Batch">What applying the rows it declared anew - or never had applied - does, or did.</param>
/// <param name="Unchanged">
/// The ids an earlier fold of it applied that it declares as it did then: never applied again, so a change the registries
/// took since is never undone by a row the agent did not touch.
/// </param>
public sealed record AgentRowsPlan(AnchorBatch Batch, IReadOnlyList<string> Unchanged)
{
    /// <summary>How many rows applying it writes: new, or changed.</summary>
    public int Planned => Batch.Rows.Count(row => row.Action is AnchorRowAction.New or AnchorRowAction.Changed);

    /// <summary>
    /// Why the closing records of rows that close refuse the fold, under <c>anchors.closure: required</c>: a row whose
    /// status moves to closed holds none, or the record it holds does not close its classes. Empty where no record was
    /// judged - the configuration says <c>off</c>, or no row closes.
    /// </summary>
    public IReadOnlyList<string> Closure { get; init; } = [];

    /// <summary>How many closing records were judged, refusing or not: what a manual lane holds, read as given.</summary>
    public int ClosureJudged { get; init; }
}

/// <summary>
/// The anchor rows an agent files, in <c>agents/&lt;agent&gt;/rows/</c>: a directory for each row, named for its anchor,
/// holding a file for each cell - <c>status.txt</c>, <c>trigger.txt</c>, <c>closing.txt</c> and <c>cross-refs.txt</c>,
/// and <c>priority.txt</c> where it declares one - and, where the configuration says a lane folded by hand closes its
/// classes by record, the one JSON cell <c>closure.json</c>, the closing record the fold judges. Cells go by file, never
/// by argument: a long row crosses the longest command line Windows takes.
/// </summary>
/// <remarks>
/// Read strictly, every entry accounted for: a draft left beside the cells, or a row missing a cell, is refused by name,
/// never skipped - a row skipped is a row that dies with the worktree unapplied. Each cell's file is read as the anchor
/// commands read a <c>--&lt;cell&gt;-file</c> (<see cref="AnchorCellInputs"/>): UTF-8 and nothing else. The closure cell
/// is read as text, for its schema is judged where it is weighed. No rows directory is no rows.
/// </remarks>
public static class AgentRows
{
    /// <summary>The extension of every cell's file.</summary>
    public const string CellExtension = ".txt";

    /// <summary>The one JSON cell a row may hold: the closing record, where a lane folded by hand closes its classes by record.</summary>
    public const string ClosureCell = "closure.json";

    /// <summary>The cells every row declares.</summary>
    public static IReadOnlyList<string> RequiredCells { get; } = [AnchorCellNames.Status, .. AnchorCellNames.Text];

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
            var closure = null as string;

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

                if (name == ClosureCell)
                {
                    if (fileSystem.IsLink(file))
                    {
                        problems.Add($"'{id}/{name}' is a link, and a cell is read from its own file only");
                        sound = false;
                        continue;
                    }

                    try
                    {
                        // UTF-8 and nothing else, as every cell is: strict, so a record that cannot be read is refused
                        // by name rather than silently replaced.
                        closure = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(fileSystem.ReadAllBytes(file));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        problems.Add($"'{id}/{name}' cannot be read: {ex.Message.TrimEnd('.')}");
                        sound = false;
                    }
                    catch (System.Text.DecoderFallbackException ex)
                    {
                        problems.Add($"'{id}/{name}' is not UTF-8, and a cell is read as UTF-8 and nothing else: {ex.Message.TrimEnd('.')}");
                        sound = false;
                    }

                    continue;
                }

                if (cell is null || !(RequiredCells.Contains(cell, StringComparer.Ordinal) || cell == AnchorCellNames.Priority))
                {
                    problems.Add($"'{id}' holds '{name}', which is not one of its cells ({string.Join(", ", RequiredCells.Append(AnchorCellNames.Priority).Select(known => known + CellExtension).Append(ClosureCell))}): keep drafts out of the rows directory");
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
                rows.Add(new AnchorRowDeclaration(
                    id,
                    cells[AnchorCellNames.Status].Trim(),
                    cells[AnchorCellNames.Trigger],
                    cells[AnchorCellNames.Closing],
                    cells[AnchorCellNames.CrossRefs])
                {
                    Priority = cells.TryGetValue(AnchorCellNames.Priority, out var priority) ? priority.Trim() : null,
                    Closure = closure,
                });
            }
        }

        return (rows, problems);
    }
}
