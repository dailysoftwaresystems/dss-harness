using System.Text;

namespace DssHarness.Core.Processes;

/// <summary>
/// The end of a stream, as it was written: its last <see cref="Length"/> characters, however much came before them.
/// </summary>
/// <remarks>
/// What the process runner keeps of a stream whose lines its caller takes as they come, for the messages that quote it - an
/// excerpt of what a program said last, the line in which ssh said it never connected. Held to twice its length and cut back to
/// one character past it as it fills, so that keeping it costs a copy now and then rather than one per character, and a cut
/// is always one <see cref="Output.TextCut.Tail"/> sees.
/// </remarks>
/// <param name="length">How many characters are kept.</param>
internal sealed class StreamTail(int length)
{
    private readonly StringBuilder _kept = new();

    /// <summary>How many characters are kept.</summary>
    public int Length { get; } = length > 0 ? length : throw new ArgumentOutOfRangeException(nameof(length), length, "A tail keeps at least one character.");

    /// <summary>Adds what the stream carried next.</summary>
    /// <param name="text">The characters, as they were written.</param>
    public void Add(ReadOnlySpan<char> text)
    {
        if (text.Length > Length)
        {
            _kept.Clear().Append(text[^(Length + 1)..]);
            return;
        }

        _kept.Append(text);

        if (_kept.Length > 2 * Length)
        {
            _kept.Remove(0, _kept.Length - Length - 1);
        }
    }

    /// <summary>
    /// The last <see cref="Length"/> characters, or all of them where fewer came. The second half of a surrogate pair whose
    /// first half was cut away reads as nothing, so the tail starts after it.
    /// </summary>
    public override string ToString() => Output.TextCut.Tail(_kept.ToString(), Length);
}
