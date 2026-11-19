using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The core's rules for a file's bytes, which hold before any implementation reads it: what is a Git LFS pointer, what
/// the graph calls a file it does not read - which encodings are read being <see cref="EncodedText"/>'s one rule - and
/// how a text is cut into the lines every span counts. The one place the cut is, so the graph, an answer that reads a body
/// fresh and an edit (<see cref="EditedText"/>) count a file's lines alike.
/// </summary>
public static class CodeFileText
{
    /// <summary>How a Git LFS pointer begins: a file of a few lines that stands for one git keeps elsewhere.</summary>
    private static readonly byte[] LfsPointer = "version https://git-lfs.github.com/spec/"u8.ToArray();

    /// <summary>
    /// What a Git LFS pointer is under, in bytes, by the Git LFS specification's own rule: a file that begins like one and
    /// holds this many bytes or more is a file of its own.
    /// </summary>
    private const int LfsPointerUnder = 1024;

    /// <summary>
    /// What <paramref name="bytes"/>, a file's whole content, are to the graph: a Git LFS pointer and a file with a NUL in
    /// its first <see cref="BinaryContent.Window"/> bytes are binary; UTF-16 is read by its mark, and UTF-8 with or without
    /// its mark; anything else is unreadable, with why.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    public static CodeFileDecoding Decode(ReadOnlySpan<byte> bytes)
        => bytes.StartsWith(LfsPointer) && bytes.Length < LfsPointerUnder ? LfsPointerDecoding : Of(EncodedText.Read(bytes));

    /// <summary>
    /// What the file at <paramref name="path"/>, of <paramref name="length"/> bytes as it stood, is to the graph, by the rules
    /// <see cref="Decode"/> holds a file's bytes to - whatever its size: a file as short as a Git LFS pointer is read whole,
    /// for what it begins with tells; any other is streamed into its text (<see cref="Pages.TextPages.ReadWholeAsync"/>), so
    /// no array of its bytes is ever held beside its text. A file whose two streamed readings differ is
    /// <see cref="CodeFileDecoding.Changed"/>.
    /// </summary>
    /// <param name="fileSystem">The file system the file is read through.</param>
    /// <param name="path">The file, absolute.</param>
    /// <param name="length">Its size, as it stood before it was read.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public static async Task<CodeFileDecoding> DecodeAsync(IFileSystem fileSystem, string path, long length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (length < LfsPointerUnder)
        {
            return Decode(await fileSystem.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return await Pages.TextPages.ReadWholeAsync(() => fileSystem.OpenRead(path), cancellationToken).ConfigureAwait(false) is { } reading
            ? Of(reading)
            : CodeFileDecoding.ChangedWhileRead;
    }

    /// <summary>What a Git LFS pointer is to the graph: binary, its file kept by git elsewhere.</summary>
    private static CodeFileDecoding LfsPointerDecoding { get; } = CodeFileDecoding.Refused(CodeFileStates.Binary, "a Git LFS pointer, whose file git keeps elsewhere");

    /// <summary>What a file's reading by the one rule of which encodings are read (<see cref="EncodedText"/>) is to the graph, its states named here.</summary>
    private static CodeFileDecoding Of(EncodedTextReading reading)
        => reading.IsRead
            ? CodeFileDecoding.Read(reading.Text.Text)
            : CodeFileDecoding.Refused(reading.Binary ? CodeFileStates.Binary : CodeFileStates.Unreadable, reading.Why);

    /// <summary>
    /// <paramref name="text"/>'s lines, without their endings: cut at each CR LF, LF and CR, a final ending closing the
    /// last line rather than opening another. An empty text is one empty line, so every file has a line 1.
    /// </summary>
    /// <remarks>
    /// Deliberately not the output's set of line breaks (<see cref="Output.TextLines"/>, which also breaks at a form feed,
    /// NEL and the Unicode separators): a span here is a line number as an editor, git and a compiler's diagnostics count
    /// it, and those count CR, LF and CR LF alone - a form feed in a C file is whitespace on its line.
    /// </remarks>
    /// <param name="text">A file's text.</param>
    public static IReadOnlyList<string> Lines(string text)
        => [.. Spans(text).Select(line => text[line.Start..line.End])];

    /// <summary>
    /// Where each of <paramref name="text"/>'s lines begins, as <see cref="Lines"/> cuts them: an implementation that reads
    /// a parser's positions turns each into a line by it, so its spans count the lines the core counts - a parser's own
    /// lines also break where this does not, at NEL and the Unicode separators.
    /// </summary>
    /// <param name="text">A file's text.</param>
    public static CodeLineIndex LineIndex(string text)
        => new([.. Spans(text).Select(line => line.Start)]);

    /// <summary>
    /// Each line of <paramref name="text"/> as <see cref="Lines"/> cuts it - at each CR LF, LF and CR, a final ending
    /// closing the last line, an empty text one empty line, by the one rule of where a line ends
    /// (<see cref="Text.LineEnds"/>) - with where its text ends and where its ending does.
    /// </summary>
    /// <param name="text">A file's text.</param>
    public static IReadOnlyList<CodeLineSpan> Spans(string text)
        => Text.LineEnds.Spans(text, (start, end, next) => new CodeLineSpan(start, end, next));

    /// <summary>
    /// What a claim's content test is given of a file's decoded text: all of it, read as far as the test reads - a test
    /// defined by a format's start (a shebang, an XML prolog) stops there by its own grammar, and one that looks for a mark
    /// finds it wherever the file holds it - so no file is claimed or passed over by what lies past a count of characters.
    /// </summary>
    /// <param name="text">The file's decoded text.</param>
    public static string Probe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text;
    }
}

/// <summary>One line of a text, as <see cref="CodeFileText.Spans"/> cuts it.</summary>
/// <param name="Start">Where the line begins.</param>
/// <param name="End">Where its text ends, before its ending.</param>
/// <param name="Next">Where its ending ends: where the next line begins, or the text's end.</param>
public readonly record struct CodeLineSpan(int Start, int End, int Next);

/// <summary>Where a text's lines begin (<see cref="CodeFileText.LineIndex"/>): a position in it told as the line it is on.</summary>
/// <param name="Starts">Where each line begins, in order; the first at 0.</param>
public sealed record CodeLineIndex(IReadOnlyList<int> Starts)
{
    /// <summary>The line, from 1, that <paramref name="position"/> - a character of the text, or its end - is on.</summary>
    /// <param name="position">A position in the text.</param>
    public int LineAt(int position)
    {
        var low = 0;
        var high = Starts.Count - 1;

        // The last line that begins at or before the position.
        while (low < high)
        {
            var middle = (low + high + 1) / 2;

            if (Starts[middle] <= position)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low + 1;
    }
}

/// <summary>
/// What a file's bytes are to the graph: its text, or the state it is listed with and why - one or the other, as
/// <see cref="Read"/> and <see cref="Refused"/> alone make it.
/// </summary>
public sealed record CodeFileDecoding
{
    private CodeFileDecoding(string? text, string? state, string? why)
    {
        Text = text;
        State = state;
        Why = why;
    }

    /// <summary>The decoded text; <see langword="null"/> where it is not read.</summary>
    public string? Text { get; }

    /// <summary>The state it is listed with where it is not read (<see cref="CodeFileStates"/>); <see langword="null"/> where it is.</summary>
    public string? State { get; }

    /// <summary>Why it is not read; <see langword="null"/> where it is.</summary>
    public string? Why { get; }

    /// <summary>Whether the file was read as text: its text there, else its state and why.</summary>
    [MemberNotNullWhen(true, nameof(Text))]
    [MemberNotNullWhen(false, nameof(State), nameof(Why))]
    public bool IsRead
    {
        get
        {
            if (Text is not null)
            {
                return true;
            }

            // Made by Read or Refused alone, so a file not read has its state and why.
            return State is null || Why is null ? throw new UnreachableException("a decoding holds neither a text nor a state") : false;
        }
    }

    /// <summary>A file read as <paramref name="text"/>.</summary>
    internal static CodeFileDecoding Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(text, null, null);
    }

    /// <summary>A file not read, listed as <paramref name="state"/> for <paramref name="why"/>.</summary>
    internal static CodeFileDecoding Refused(string state, string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        return new(null, state, why);
    }

    /// <summary>
    /// Whether the file read otherwise in the second of its streamed readings than in the first
    /// (<see cref="CodeFileText.DecodeAsync"/>): it changed while it was read, and nothing is made of what was read.
    /// </summary>
    public bool Changed { get; private init; }

    /// <summary>A file whose two streamed readings differ: unreadable, as one that changed while it was read is.</summary>
    internal static CodeFileDecoding ChangedWhileRead { get; } = new(null, CodeFileStates.Unreadable, "it changed while it was read") { Changed = true };
}
