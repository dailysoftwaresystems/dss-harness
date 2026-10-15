using System.Text;

namespace RepoHarness.Core.Processes;

/// <summary>
/// The end of a stream, as it was written: its last <see cref="Length"/> characters, however much came before them.
/// </summary>
/// <remarks>
/// What the process runner keeps of a stream whose lines its caller takes as they come, for the messages that quote it - an
/// excerpt of what a program said last, the line in which ssh said it never connected. Held to twice its length and cut back to
/// it as it fills, so that keeping it costs a copy now and then rather than one per character.
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
        if (text.Length >= Length)
        {
            _kept.Clear().Append(text[^Length..]);
            return;
        }

        _kept.Append(text);

        if (_kept.Length > 2 * Length)
        {
            _kept.Remove(0, _kept.Length - Length);
        }
    }

    /// <summary>
    /// The last <see cref="Length"/> characters, or all of them where fewer came. The second half of a surrogate pair whose
    /// first half was cut away reads as nothing, so the tail starts after it.
    /// </summary>
    public override string ToString()
    {
        var start = Math.Max(0, _kept.Length - Length);

        if (start < _kept.Length && char.IsLowSurrogate(_kept[start]))
        {
            start++;
        }

        return _kept.ToString(start, _kept.Length - start);
    }
}
