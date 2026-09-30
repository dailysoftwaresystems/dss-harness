using System.Text.RegularExpressions;

namespace RepoHarness.Core.Anchors;

/// <summary>
/// What a cell would store broken, refused before it is written: a cell is stored on one line, each line break a space
/// (<see cref="AnchorCells.Flatten"/>), and a break inside an id or a path leaves a false one no search for the real one
/// finds.
/// </summary>
/// <remarks>
/// Each rule was paid for once by a consumer: a composer that joined wrapped lines stored three paths cut after a
/// <c>/</c> - <c>tests/hir/ test_x.cpp</c> - in a registry, and an id wrapped after a hyphen is stored as two words.
/// Judged as the cell will be stored, where the break is already a space - a check of the text as written cannot see a
/// space a composer put there - and, where the stored form cannot tell a break from prose, as it was written: an id's
/// start ending a line whose next line goes on with capitals and hyphens, or a path's directory ending a line whose next
/// line opens with a file's name. A path stored with a space after a <c>/</c> is refused where it starts at a directory
/// the tree has at its top, so prose such as "no src/hir/ or src/mir/" - whose next word is no file's name - passes.
/// </remarks>
public sealed class AnchorCellCuts
{
    /// <summary>A directory ending a line, and a file's name - a word holding '_', '.' or '-' - opening the next.</summary>
    private static readonly Regex WrappedPath = new(
        $@"(?<![A-Za-z0-9_./-])[A-Za-z0-9_.-]+/(?:[A-Za-z0-9_.-]+/)*[ \t]*{AnchorCells.LineBreakPattern}\s*[A-Za-z0-9]+[_.-][A-Za-z0-9_./-]*",
        RegexOptions.CultureInvariant);

    private readonly AnchorIdRules _rules;
    private readonly Regex? _spacedPath;

    /// <param name="rules">How the tree's ids are spelt.</param>
    /// <param name="roots">The names of the directories at the top of the tree, where a path a cell cites starts.</param>
    public AnchorCellCuts(AnchorIdRules rules, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(roots);

        _rules = rules;

        var named = roots.Where(root => root.Length > 0).Select(Regex.Escape).ToList();

        _spacedPath = named.Count == 0
            ? null
            : new Regex(
                $@"(?<![A-Za-z0-9_./-])(?:{string.Join('|', named)})/(?:[A-Za-z0-9_.-]+/)*[ \t]+[A-Za-z0-9]+[_.-][A-Za-z0-9_./-]*",
                RegexOptions.CultureInvariant);
    }

    /// <summary>Why <paramref name="text"/> would be stored broken, one sentence each; empty where it would be stored whole.</summary>
    /// <param name="text">The value as its author wrote it.</param>
    /// <param name="field">The cell, as a refusal names it.</param>
    public IReadOnlyList<string> Of(string text, string field)
    {
        ArgumentNullException.ThrowIfNull(text);

        var stored = AnchorCells.Flatten(text);
        var problems = new List<string>();

        if (_rules.BrokenAfterHyphen(stored) is { } broken)
        {
            problems.Add(
                $"The {field} holds an anchor id broken after a hyphen ('{broken}', as it would be stored): a line break is stored "
                + "as a space, so the row would hold an id no search for the real one finds. Keep every id whole on one line.");
        }

        if (_rules.WrappedInsideSegment(text) is { } wrapped)
        {
            problems.Add(
                $"The {field} holds an anchor id broken across a line ('{Shown(wrapped)}'): stored, it reads as a shorter id and "
                + "a word. Keep every id whole on one line.");
        }

        if (WrappedPath.Match(text) is { Success: true } path)
        {
            problems.Add(
                $"The {field} holds a path broken across a line after a '/' ('{Shown(path.Value)}'): stored, the path has a space "
                + "in it. Write every path whole on one line.");
        }
        else if (_spacedPath?.Match(stored) is { Success: true } spaced)
        {
            problems.Add(
                $"The {field} holds a path with a space after a '/' ('{spaced.Value}', as it would be stored), which cuts it in "
                + "two. Write every path whole.");
        }

        return problems;
    }

    /// <summary>A match holding a line break, each break written as <c>\n</c>, so the refusal is one line.</summary>
    private static string Shown(string text) => Regex.Replace(text, AnchorCells.LineBreakPattern, @"\n");
}
