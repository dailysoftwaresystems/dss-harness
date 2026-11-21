namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The string literals one file's reader keeps, by the one rule every implementation keeps them by: each value as its
/// language reads it - its escapes decoded - its line endings read as one newline, so a checkout's own way of writing them
/// moves no answer, and its text whole UTF-16; every one that holds a character kept, however short, each value once - in
/// the order the reader first reads it - with every place it is read, in order: its line and the node whose code holds
/// it. A value read a million times is held once, its places one each.
/// </summary>
internal sealed class ExtractLiterals
{
    private readonly List<(string Value, List<ExtractLiteralPlace> Places)> _kept = [];
    private readonly Dictionary<string, int> _at = new(StringComparer.Ordinal);

    /// <summary>
    /// The literal <paramref name="value"/>, decoded, on <paramref name="line"/>, written in the code of the node
    /// <paramref name="node"/> names - as the reader names it, a name (<see cref="QualifiedName"/>) - or in none: kept
    /// where it holds a character.
    /// </summary>
    public void Add(string value, int line, QualifiedName? node)
    {
        ArgumentNullException.ThrowIfNull(value);

        value = ExtractText.Whole(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));

        if (value.Length == 0)
        {
            return;
        }

        if (!_at.TryGetValue(value, out var at))
        {
            _at[value] = at = _kept.Count;
            _kept.Add((value, []));
        }

        _kept[at].Places.Add(new ExtractLiteralPlace(line) { Node = node });
    }

    /// <summary>Every place of each of <paramref name="literals"/>, kept as <see cref="Add"/> keeps one: what merging another extract's literals into these does.</summary>
    /// <param name="literals">An extract's literals, or none.</param>
    public void AddAll(IReadOnlyList<ExtractLiteral>? literals)
    {
        foreach (var literal in literals ?? [])
        {
            foreach (var place in literal.Places)
            {
                Add(literal.Value, place.Line, place.Node);
            }
        }
    }

    /// <summary>
    /// What is kept, each place's node as <paramref name="idOf"/> gives the node the reader named - its id once the file is
    /// read; <see langword="null"/> where the file holds none.
    /// </summary>
    /// <param name="idOf">The id of the node each place names, from what the reader named it by - its key, or none; the names as they are where none is given.</param>
    public IReadOnlyList<ExtractLiteral>? Kept(Func<QualifiedName?, QualifiedName?>? idOf = null)
        => _kept.Count == 0 ? null
            : [.. _kept.Select(kept => new ExtractLiteral(kept.Value, idOf is null ? [.. kept.Places] : [.. kept.Places.Select(place => place with { Node = idOf(place.Node) })]))];
}
