namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The string literals one file's reader keeps, by the one rule every implementation keeps them by: each value as its
/// language reads it - its escapes decoded - its line endings read as one newline, so a checkout's own way of writing them
/// moves no answer, and its text whole UTF-16; of at least <see cref="FileExtract.LeastLiteral"/> characters, every one
/// the file holds kept, in the order its reader reads them; each with its line and the node whose code holds it.
/// </summary>
internal sealed class ExtractLiterals
{
    private readonly List<ExtractLiteral> _kept = [];

    /// <summary>
    /// The literal <paramref name="value"/>, decoded, on <paramref name="line"/>, written in the code of the node
    /// <paramref name="node"/> names - as the reader names it - or in none: kept where it is data a search asks for.
    /// </summary>
    public void Add(string value, int line, string? node)
    {
        ArgumentNullException.ThrowIfNull(value);

        value = ExtractText.Whole(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));

        if (value.Length < FileExtract.LeastLiteral)
        {
            return;
        }

        _kept.Add(new ExtractLiteral(value, line) { Node = node });
    }

    /// <summary>
    /// What is kept, each one's node as <paramref name="idOf"/> gives the node the reader named - its id once the file is
    /// read; <see langword="null"/> where the file holds none.
    /// </summary>
    /// <param name="idOf">The id of the node each literal names, from what the reader named it by - its key, or none; the names as they are where none is given.</param>
    public IReadOnlyList<ExtractLiteral>? Kept(Func<string?, string?>? idOf = null)
        => _kept.Count == 0 ? null
            : idOf is null ? _kept
            : [.. _kept.Select(literal => literal with { Node = idOf(literal.Node) })];
}
