using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RepoHarness.Core.Processes;

/// <summary>
/// What a child is given on its standard input: text, or a writer that writes it as it makes it. Written as UTF-8 on every
/// platform, the encoding output is read with.
/// </summary>
/// <remarks>
/// A writer is for input too large to hold whole as text: what it writes is never in memory all at once, however long it
/// is. A consumer's first sync of a worktree to an ssh host built each batch of files as text inside text before handing it
/// to the child, and left the process that carried it holding 3.2 GiB.
/// </remarks>
public sealed record ChildInput
{
    /// <summary>How many characters of text are encoded at a time, so that no text is ever held again as bytes whole.</summary>
    private const int TextPiece = 16 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _text;
    private readonly Action<Stream>? _write;

    private ChildInput(string? text, Action<Stream>? write)
    {
        _text = text;
        _write = write;
    }

    /// <summary><paramref name="text"/>, written as it is.</summary>
    /// <param name="text">The text.</param>
    public static ChildInput Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new(text, null);
    }

    /// <summary>What <paramref name="write"/> writes to the child's input, as it writes it.</summary>
    /// <param name="write">
    /// Writes the input to the stream it is given, and returns once it has written all of it. Run on a thread of its own,
    /// so it may write synchronously, for as long as the child takes to read what it writes. It may be run more than once
    /// for one input - a call over ssh that failed before any session began is made again, and its input written again
    /// from the start - so it writes from what it was made with, and uses up nothing it reads as it goes.
    /// </param>
    public static ChildInput WrittenBy(Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        return new(null, write);
    }

    /// <summary><paramref name="text"/>, written as it is; no input where there is no text.</summary>
    /// <param name="text">The text, or <see langword="null"/>.</param>
    [return: NotNullIfNotNull(nameof(text))]
    public static implicit operator ChildInput?(string? text) => text is null ? null : Of(text);

    /// <summary>Writes the input to <paramref name="stream"/>, all of it, as UTF-8.</summary>
    /// <param name="stream">Where the child reads it from.</param>
    public void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (_write is not null)
        {
            _write(stream);
            return;
        }

        using var writer = new StreamWriter(stream, Utf8, TextPiece, leaveOpen: true);

        writer.Write(_text);
    }
}
