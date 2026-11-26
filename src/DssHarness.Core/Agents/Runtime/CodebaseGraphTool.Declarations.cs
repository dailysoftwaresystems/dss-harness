using DssHarness.Core.Codebase;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// The declarations of one file the graph parsed, as the file stood when they were read
/// (<see cref="CodebaseGraphTool.DeclarationsAsync"/>): each with its span and its hash as the file's lines gave them then -
/// what a search's hits are shown under, what a range of the file is named by and what a whole read of it is answered with,
/// so the graph is in the answers of the tools an agent reads with, and costs it no turn of its own.
/// </summary>
public sealed class GraphFileDeclarations
{
    private readonly Dictionary<string, GraphCard> _held;

    /// <summary>The declarations of the file at <paramref name="path"/> that <paramref name="view"/> holds, those its lines confirmed.</summary>
    internal GraphFileDeclarations(CodebaseGraphQueries view, string path, IReadOnlyList<GraphCard> cards, int moved)
    {
        View = view;
        Path = path;
        Cards = cards;
        Moved = moved;
        _held = cards.ToDictionary(card => card.Id, StringComparer.Ordinal);
    }

    /// <summary>The view the declarations were read from: the tool's as it stood, or the one an extraction of the file left.</summary>
    internal CodebaseGraphQueries View { get; }

    /// <summary>The file, by the path the view holds it at.</summary>
    public string Path { get; }

    /// <summary>
    /// The file's declarations as an outline of it gives them (<see cref="CodebaseGraphQueries.OutlineFile"/>), in the file's
    /// order, each whose lines the file held as the graph read them - its hash the one those lines give.
    /// </summary>
    public IReadOnlyList<GraphCard> Cards { get; }

    /// <summary>
    /// How many of the file's declarations were left out: their lines changed since the graph read the file, and the view
    /// they were read from does not hold them as they are - one the graph has not checked again yet, or could not.
    /// </summary>
    public int Moved { get; }

    /// <summary>
    /// The innermost declaration - a type, a member, a section (<see cref="CodeNodeKinds.Declarations"/>) - that holds
    /// <paramref name="line"/>, as a text hit is turned to a card (<see cref="CodebaseGraphQueries.AtLine(string, int)"/>); null where the
    /// line is in none - outside every declaration, in a namespace or a project alone, or in one whose lines changed since
    /// the graph read the file.
    /// </summary>
    /// <param name="line">A line of the file, from 1.</param>
    public GraphCard? Holding(long line)
        => line is >= 1 and <= int.MaxValue
            && View.AtLine(Path, (int)line) is { } card
            && CodeNodeKinds.Declarations.Contains(card.Kind, StringComparer.Ordinal)
            && _held.TryGetValue(card.Id, out var held)
                ? held
                : null;

    /// <summary>
    /// The declarations - types, members, sections - whose lines meet the lines <paramref name="first"/> to
    /// <paramref name="last"/>, those that hold them among them, in the file's order.
    /// </summary>
    /// <param name="first">The first line, from 1.</param>
    /// <param name="last">The last line, inclusive.</param>
    public IReadOnlyList<GraphCard> Meeting(long first, long last)
        => [.. Cards.Where(card => CodeNodeKinds.Declarations.Contains(card.Kind, StringComparer.Ordinal) && card.Start <= last && card.End >= first)];
}

/// <summary>The graph's part in the answers of the runtime's reads: a file's declarations as it stands now, and a whole read answered with its outline.</summary>
public sealed partial class CodebaseGraphTool
{
    /// <summary>
    /// The declarations of the file at <paramref name="path"/> as the file stands now, where the view holds it as one it
    /// parsed and the step's reads reach it: those the file's lines confirm (<see cref="ConfirmedDeclarationsAsync"/>), and,
    /// where any moved and the tool may check the file again, the file checked again - the one file the call names, in one
    /// check (<see cref="ReadAgainAsync"/>) - and answered from the view that leaves, as a moved body is; a declaration whose
    /// lines the file still does not hold as the graph read them left out and counted, so no span or hash given stands for
    /// lines the file does not hold. A check that cannot be made says nothing: the declarations are those the lines confirm.
    /// Null where the view holds no parsed file there, the step's reads do not reach it, or it does not read now.
    /// </summary>
    /// <param name="path">The file, relative to the tree, as the reader spells it.</param>
    /// <param name="cancellationToken">Stops the reading and the extraction.</param>
    public async Task<GraphFileDeclarations?> DeclarationsAsync(string path, CancellationToken cancellationToken)
    {
        var declarations = await ConfirmedDeclarationsAsync(path, cancellationToken).ConfigureAwait(false);

        return declarations is { Moved: > 0 } && await ReadAgainAsync([declarations.Path], cancellationToken).ConfigureAwait(false)
            ? await ConfirmedDeclarationsAsync(path, cancellationToken).ConfigureAwait(false)
            : declarations;
    }

    /// <summary>
    /// The declarations of the file at <paramref name="path"/> as the view holds them, where it holds the file as one it
    /// parsed and the step's reads reach it, each checked against the file as it stands now - the file read fresh, as a
    /// body's is, and each declaration's hash checked against the lines it spans now - and never checked again by the graph:
    /// a declaration whose lines the file no longer holds as the graph read them is left out and counted
    /// (<see cref="GraphFileDeclarations.Moved"/>), for a caller that checks every moved file of its call together
    /// (<see cref="ReadAgainAsync"/>) - a search, which meets them file by file. Null where the view holds no parsed file
    /// there, the step's reads do not reach it, or it does not read now.
    /// </summary>
    /// <param name="path">The file, relative to the tree, as the reader spells it.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<GraphFileDeclarations?> ConfirmedDeclarationsAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var view = _queries;

        if (Held(view, path) is not { } held || !view.Parsed(held) || !_mayRead(held))
        {
            return null;
        }

        var (lines, _, _) = await ReadLinesAsync(held, cancellationToken).ConfigureAwait(false);

        if (lines is null)
        {
            return null;
        }

        var cards = view.OutlineFile(held) ?? [];
        var confirmed = cards.Where(card => Confirmed(lines, card)).ToList();
        return new GraphFileDeclarations(view, held, confirmed, cards.Count - confirmed.Count);
    }

    /// <summary>Whether the tool may have the graph check a file again (<see cref="ReadAgainAsync"/>): false where nothing may be synced (the verb).</summary>
    public bool ChecksAgain => reExtract is not null;

    /// <summary>
    /// The files at <paramref name="paths"/> - relative to the tree as the view holds them - checked again by the graph,
    /// every one in one check (<see cref="ExtractedAgainAsync"/>), and the tool answering from the view that leaves from
    /// then on: what a call that met files whose declarations moved makes once, whatever it met, so a call pays for those
    /// files and one view. False where the tool may not check a file again, none is given, or the check could not be made -
    /// the view stands as it was, and the declarations a caller gives are those the lines confirm.
    /// </summary>
    /// <param name="paths">The files, as the view holds them.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    public async Task<bool> ReadAgainAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (reExtract is null || paths.Count == 0)
        {
            return false;
        }

        try
        {
            _ = await ExtractedAgainAsync([.. paths.Distinct(StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
        {
            // The view stands as it was: its declarations are those the lines confirm.
            return false;
        }
    }

    /// <summary>
    /// A whole read of the file at <paramref name="path"/> of <paramref name="lines"/> lines, as <see cref="WholeRead"/>
    /// decides it - and where the narrowing takes it, answered: its line, then the file's outline as <c>outline</c> with its
    /// path gives it - what the graph does not hold of the file, then every declaration with its span and its hash, as the
    /// file stands now (<see cref="DeclarationsAsync"/>) - every line of it, paged as every answer of the runtime is, so the
    /// step learns the file's structure in the call it read it by.
    /// </summary>
    /// <param name="path">The file, relative to the tree, as the reader spells it.</param>
    /// <param name="lines">How many lines the file holds, as the reader read it.</param>
    /// <param name="cancellationToken">Stops the reading of the file.</param>
    public async Task<GraphWholeRead> WholeReadAsync(string path, int lines, CancellationToken cancellationToken)
    {
        var whole = WholeRead(path, lines);

        if (!whole.Narrowed)
        {
            return whole;
        }

        var held = whole.Path!;
        var declarations = await DeclarationsAsync(held, cancellationToken).ConfigureAwait(false);
        var view = declarations?.View ?? _queries;
        var outline = OutlineOf(view, held, declarations?.Cards ?? view.OutlineFile(held) ?? []);
        var moved = declarations is { Moved: > 0 and var count }
            ? [$"{ReportText.Counted(count, "declaration")} left out: their lines changed since the graph read the file, and it could not read it again"]
            : Array.Empty<string>();

        return whole with
        {
            Text = string.Join('\n', [
                whole.Text,
                .. outline.Leading,
                .. outline.Sections.SelectMany(section => (section.Caption is { } caption ? [caption] : Array.Empty<string>()).Concat(section.Entries.Select(entry => entry.Text))),
                .. moved,
                .. UnheldLines(view)]),
        };
    }

    /// <summary>Whether <paramref name="lines"/> hold <paramref name="card"/>'s lines as the graph read them: its hash the one they give, a node with none - a file's - held as it is.</summary>
    private static bool Confirmed(IReadOnlyList<string> lines, GraphCard card) => card.Hash is null || Same(Hash(lines, card), card.Hash);
}
