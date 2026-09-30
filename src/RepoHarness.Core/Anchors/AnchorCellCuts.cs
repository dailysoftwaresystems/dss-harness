using System.Text.RegularExpressions;

namespace RepoHarness.Core.Anchors;

/// <summary>
/// What a cell would store broken, refused before it is written: a cell is stored on one line, each line break a space
/// (<see cref="AnchorCells.Flatten"/>), so an id or a path that a line break or a space cuts is stored as a false one no
/// search for the real one finds.
/// </summary>
/// <remarks>
/// Two cuts were paid for once by a consumer: a composer that joined wrapped lines stored three paths cut after a
/// <c>/</c> - <c>tests/hir/ test_x.cpp</c> - in a registry, and an id wrapped after a hyphen is stored as two words.
/// <list type="bullet">
/// <item>An id cut where a line ends is found as check-anchor-citations finds one (<see cref="AnchorIdScanner"/>): at a
/// hyphen that ends the line, or where the next line carries it on into a row's id.</item>
/// <item>An id with a space after one of its hyphens - typed, or left by a composer that joined the lines - spanning the
/// segments a citation carries: so <c>the D- prefix</c> is no id cut, and <c>D-AREA- TOPIC</c> is one.</item>
/// <item>A path's directory ending a line before a file's name, in the text as written.</item>
/// <item>A path with a space after a <c>/</c> before a file's name, as the cell will be stored, where it starts at a
/// directory at the top of the tree, named as the tree spells it.</item>
/// </list>
/// A file's name is a word holding <c>_</c>, <c>.</c> or <c>-</c> and ending in a letter or a digit, so prose passes that
/// only looks like a path - "no src/hir/ or src/mir/", a sentence ending "read/ write." - as does a name with neither, such
/// as <c>Makefile</c>, which no rule can tell from a word. A cut the cell already held is history, and is not judged again.
/// </remarks>
public sealed class AnchorCellCuts
{
    /// <summary>What may not come just before a path: a character of one, which would make it the tail of a longer word.</summary>
    private const string NotAfterPathCharacter = "(?<![A-Za-z0-9_./-])";

    /// <summary>The directories a path runs through, each ended by its '/'.</summary>
    private const string Directories = "(?:[A-Za-z0-9_.-]+/)*";

    /// <summary>A file's name, as a word that could be one: holding '_', '.' or '-', and ending in a letter or a digit.</summary>
    private const string FileName = @"\.?[A-Za-z0-9]+[_.-][A-Za-z0-9_./-]*[A-Za-z0-9](?![A-Za-z0-9_./-])";

    /// <summary>A directory ending a line, and a file's name opening the next: a path the line cut after its '/'.</summary>
    private static readonly Regex WrappedPath = new(
        $@"{NotAfterPathCharacter}[A-Za-z0-9_.-]+/{Directories}[ \t]*{AnchorCells.LineBreakPattern}[ \t]*{FileName}",
        RegexOptions.CultureInvariant);

    private readonly AnchorIdScanner _scanner;
    private readonly Regex _spacedId;
    private readonly Regex? _spacedPath;

    /// <param name="scanner">How the tree's ids are found, and a cut one told.</param>
    /// <param name="roots">The names of the directories at the top of the tree, where a path a cell cites starts.</param>
    public AnchorCellCuts(AnchorIdScanner scanner, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(roots);

        _scanner = scanner;

        var prefix = Regex.Escape(scanner.Rules.Prefix);
        var segment = AnchorIdRules.Segment;

        _spacedId = new Regex(
            $@"(?<![A-Za-z0-9_-]){prefix}-(?:(?:{segment}-)+[ \t]+{segment}(?:-{segment})*|[ \t]+{segment}(?:-{segment})+)",
            RegexOptions.CultureInvariant);

        var named = roots.Where(root => root.Length > 0).Distinct(StringComparer.Ordinal).Select(Regex.Escape).ToList();

        _spacedPath = named.Count == 0
            ? null
            : new Regex($@"{NotAfterPathCharacter}(?:{string.Join('|', named)})/{Directories}[ \t]+{FileName}", RegexOptions.CultureInvariant);
    }

    /// <summary>Why <paramref name="text"/> would be stored broken: one sentence for each cut, but for those it held already.</summary>
    /// <param name="text">The value as its author wrote it.</param>
    /// <param name="heading">The cell, as a refusal names it.</param>
    /// <param name="stored">What the cell holds now, whose cuts are history; null for a row not written yet.</param>
    /// <param name="rows">The ids there are rows for, which say whether a line carries an id on.</param>
    public IReadOnlyList<string> Of(string text, string heading, string? stored, IReadOnlySet<string> rows)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(rows);

        var history = stored is null
            ? []
            : Cuts(stored, heading, rows).Select(cut => cut.Stored).ToHashSet(StringComparer.Ordinal);

        return [.. Cuts(text, heading, rows).DistinctBy(cut => cut.Stored).Where(cut => !history.Contains(cut.Stored)).Select(cut => cut.Why)];
    }

    /// <summary>Every cut <paramref name="text"/> holds: as the cell would store it, and why it is one.</summary>
    private IEnumerable<(string Stored, string Why)> Cuts(string text, string heading, IReadOnlySet<string> rows)
    {
        foreach (var citation in _scanner.Scan(heading, AnchorCells.WithLineFeeds(text), rows).Where(citation => citation.Cut))
        {
            yield return (
                citation.Written,
                $"The {heading} holds an anchor id cut where a line ends ('{citation.Written}'): stored, the line break is a space, "
                + "so the row would hold an id no search for the real one finds. Keep every id whole on one line.");
        }

        foreach (var match in _spacedId.Matches(text).Cast<Match>())
        {
            yield return (
                AnchorCells.Flatten(match.Value),
                $"The {heading} holds an anchor id with a space after one of its hyphens ('{match.Value}'), which cuts it in two. "
                + "Keep every id whole.");
        }

        foreach (var match in WrappedPath.Matches(text).Cast<Match>())
        {
            yield return (
                AnchorCells.Flatten(match.Value),
                $"The {heading} holds a path broken across a line after a '/' ('{Shown(match.Value)}'): stored, the path has a space "
                + "in it. Write every path whole on one line.");
        }

        if (_spacedPath is not null)
        {
            foreach (var match in _spacedPath.Matches(AnchorCells.Flatten(text)).Cast<Match>())
            {
                yield return (
                    match.Value,
                    $"The {heading} holds a path with a space after a '/' ('{match.Value}', as it would be stored), which cuts it in "
                    + "two. Write every path whole.");
            }
        }
    }

    /// <summary>A match holding a line break, each break written as <c>\n</c>, so the refusal is one line.</summary>
    private static string Shown(string text) => Regex.Replace(text, AnchorCells.LineBreakPattern, @"\n");
}
