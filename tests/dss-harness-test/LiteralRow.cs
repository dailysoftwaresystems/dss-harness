using DssHarness.Core.Codebase;

namespace DssHarness.Tests;

/// <summary>
/// One place a string literal is written, with its value, as a test names it: an extract keeps each value once with its
/// places (<see cref="FileExtract.Literals"/>), and a test reads them a place at a time.
/// </summary>
/// <param name="Value">The literal's value.</param>
/// <param name="Line">The line it is written on.</param>
/// <param name="Node">The node whose code holds it; none where the file's own does.</param>
internal sealed record LiteralRow(string Value, int Line, string? Node = null)
{
    /// <summary>Every place <paramref name="extract"/>'s literals are written: the values in the order it keeps them, each one's places in order.</summary>
    public static IReadOnlyList<LiteralRow> Of(FileExtract extract)
        => [.. (extract.Literals ?? []).SelectMany(literal => literal.Places.Select(place => new LiteralRow(literal.Value, place.Line, place.Node)))];

    /// <summary>The literals of an extract that writes <paramref name="rows"/>, in order: each value once, with its places.</summary>
    public static IReadOnlyList<ExtractLiteral> Table(params LiteralRow[] rows)
        => [.. rows.GroupBy(row => row.Value, StringComparer.Ordinal).Select(value => new ExtractLiteral(value.Key, [.. value.Select(row => new ExtractLiteralPlace(row.Line) { Node = row.Node })]))];
}
