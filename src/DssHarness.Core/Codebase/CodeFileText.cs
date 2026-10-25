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
    /// <summary>
    /// How many of a file's first characters, once decoded, are given to a claim's content test: as many as the bytes of
    /// the window a file is binary by a NUL in (<see cref="BinaryContent.Window"/>), so a test is given about what was
    /// looked at.
    /// </summary>
    public const int ProbeCharacters = BinaryContent.Window;

    /// <summary>How a Git LFS pointer begins: a file of a few lines that stands for one git keeps elsewhere.</summary>
    private static readonly byte[] LfsPointer = "version https://git-lfs.github.com/spec/"u8.ToArray();

    /// <summary>The longest a Git LFS pointer is, in bytes: a file that begins like one and is longer is a file of its own.</summary>
    private const int MostLfsPointer = 1024;

    /// <summary>
    /// What <paramref name="bytes"/>, a file's whole content, are to the graph: a Git LFS pointer and a file with a NUL in
    /// its first <see cref="BinaryContent.Window"/> bytes are binary; UTF-16 is read by its mark, and UTF-8 with or without
    /// its mark; anything else is unreadable, with why.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    public static CodeFileDecoding Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(LfsPointer) && bytes.Length <= MostLfsPointer)
        {
            return CodeFileDecoding.Refused(CodeFileStates.Binary, "a Git LFS pointer, whose file git keeps elsewhere");
        }

        // Which encodings are read is the file system's one rule (EncodedText), the graph's states named here.
        var reading = EncodedText.Read(bytes);

        return reading.IsRead
            ? CodeFileDecoding.Read(reading.Text.Text)
            : CodeFileDecoding.Refused(reading.Binary ? CodeFileStates.Binary : CodeFileStates.Unreadable, reading.Why);
    }

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
    /// closing the last line, an empty text one empty line - with where its text ends and where its ending does. The one
    /// cut of a text into lines.
    /// </summary>
    /// <param name="text">A file's text.</param>
    public static IReadOnlyList<CodeLineSpan> Spans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<CodeLineSpan>();
        var start = 0;

        for (var at = 0; at < text.Length; at++)
        {
            if (text[at] is '\r' or '\n')
            {
                var end = at;

                if (text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n')
                {
                    at++;
                }

                lines.Add(new CodeLineSpan(start, end, at + 1));
                start = at + 1;
            }
        }

        if (start < text.Length || lines.Count == 0)
        {
            lines.Add(new CodeLineSpan(start, text.Length, text.Length));
        }

        return lines;
    }

    /// <summary>The first <see cref="ProbeCharacters"/> characters of a file's decoded text, as a claim's content test is given them.</summary>
    /// <param name="text">The file's decoded text.</param>
    public static string Probe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Output.TextCut.Head(text, ProbeCharacters);
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
}
