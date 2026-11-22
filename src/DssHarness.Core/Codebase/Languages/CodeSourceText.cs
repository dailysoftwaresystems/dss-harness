using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A file's text as the C# compiler's parser is given it: a <see cref="SourceText"/> that reads the core's text by position
/// (<see cref="CodeText"/>) as the parser asks - its lexer copies the characters it reads a window at a time - so no string
/// of the whole file, nor an array of all its characters, is made to parse it. What the parse holds is the syntax tree it
/// builds. Its length is an <see cref="int"/>, as every <see cref="SourceText"/>'s is: a text of more characters is not
/// given one.
/// </summary>
internal sealed class CodeSourceText : SourceText
{
    private readonly CodeText _text;

    /// <summary>The compiler's text of <paramref name="text"/>.</summary>
    /// <param name="text">A text of at most <see cref="int.MaxValue"/> characters.</param>
    public CodeSourceText(CodeText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, int.MaxValue);

        _text = text;
        Length = (int)text.Length;
    }

    /// <summary>None: the text is the core's, decoded already, and is never written back.</summary>
    public override Encoding? Encoding => null;

    /// <inheritdoc/>
    public override int Length { get; }

    /// <inheritdoc/>
    public override char this[int position] => _text[position];

    /// <inheritdoc/>
    public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
        => _text.CopyTo(sourceIndex, destination.AsSpan(destinationIndex, count));
}
