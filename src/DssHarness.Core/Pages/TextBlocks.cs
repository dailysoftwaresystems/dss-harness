using System.Globalization;
using System.Text.Json;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Pages;

/// <summary>
/// A block of a text: a line that heads it and the lines after it that belong to it, as the writer of the text said them -
/// a search's hits beneath the declaration they are in - each by the byte of the text's file it begins at, so a page of the
/// text that begins inside one gives its head again first (<see cref="TextPages"/>), however the text's lines are counted.
/// </summary>
/// <param name="Head">The byte the line that heads the block begins at.</param>
/// <param name="Body">The byte the line after its head begins at: the first of the lines that belong to it.</param>
/// <param name="Next">The byte past its last line: where the line after the block begins, or the text's end.</param>
public readonly record struct TextBlock(long Head, long Body, long Next);

/// <summary>
/// The blocks of a text (<see cref="TextBlock"/>), kept in a file beside it (<see cref="FileOf"/>) by the writer that wrote
/// the text and knew them, and read in the text's order as its pages are laid: the schema they are written under first,
/// then each block in the order the text holds them, then the digest of the bytes of the text they were written beside -
/// so blocks are read only of the text they are of. Blocks that do not read - another schema's, out of the text's order,
/// past its end, a file cut short - or that were written beside a text that has changed since are no blocks of the text,
/// and its pages are laid as its lines alone. One line of JSON each, read one at a time, so what a text's blocks cost in
/// memory is one block, however many it holds.
/// </summary>
public sealed class TextBlocks : IDisposable
{
    /// <summary>The schema this build writes and reads.</summary>
    public const int CurrentSchema = 1;

    /// <summary>What the file of a text's blocks is named by: the text's own file's name, then this.</summary>
    public const string Extension = ".blocks.jsonl";

    private readonly IEnumerator<FileLine> _lines;

    /// <summary>The block read last, which the offsets asked so far have not passed; null before the first and once every block is read.</summary>
    private TextBlock? _current;

    /// <summary>Where the block read last ends: the next begins there or after it.</summary>
    private long _passed;

    /// <summary>Whether every block is read: the digest after them is, and nothing may follow it.</summary>
    private bool _read;

    /// <summary>The digest the blocks end with: of the bytes of the text they were written beside.</summary>
    private string? _digest;

    private TextBlocks(IEnumerator<FileLine> lines) => _lines = lines;

    /// <summary>Why the blocks are no blocks of the text being laid; null while they read as its.</summary>
    public string? Problem { get; private set; }

    /// <summary>The file the blocks of the text at <paramref name="text"/> are kept in: beside it, named by its whole name.</summary>
    /// <param name="text">The text's file.</param>
    public static string FileOf(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return text + Extension;
    }

    /// <summary>
    /// The blocks kept beside the text at <paramref name="text"/>, read through <paramref name="fileSystem"/> a line at a
    /// time as its pages are laid, its schema read first; null where none are kept.
    /// </summary>
    /// <param name="fileSystem">The file system the blocks' file is read through.</param>
    /// <param name="text">The text's file.</param>
    public static TextBlocks? Read(IFileSystem fileSystem, string text)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var file = FileOf(text);

        if (fileSystem.StandingOf(file) is null)
        {
            return null;
        }

        var blocks = new TextBlocks(FileLines.Read(fileSystem, file).GetEnumerator());
        var first = blocks.NextLine();

        if (first != new TextBlocksLine(Schema: CurrentSchema))
        {
            blocks.Refuse(first?.Schema is { } schema
                ? $"their schema is {schema.ToString(CultureInfo.InvariantCulture)}, and this build reads {CurrentSchema.ToString(CultureInfo.InvariantCulture)}"
                : "they begin with the schema they are written under");
        }

        return blocks;
    }

    /// <summary>
    /// The block at <paramref name="offset"/> of the text, or the first after it: the first block not ended by then, every
    /// one before it passed - offsets are asked in the text's order; null where none is left, or the blocks do not read.
    /// </summary>
    /// <param name="offset">A byte of the text's file: where a line, or a page, begins.</param>
    internal TextBlock? At(long offset)
    {
        while (Problem is null && !_read && (_current is not { } current || current.Next <= offset))
        {
            _current = NextBlock();
        }

        return Problem is null && _current is { } block && block.Next > offset ? block : null;
    }

    /// <summary>Takes the blocks as no blocks of the text, for <paramref name="why"/>: from now on they give it none.</summary>
    /// <param name="why">Why they are not its blocks.</param>
    internal void Refuse(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        Problem ??= why;
    }

    /// <summary>
    /// The digest the blocks end with, once every block left is read - each within the text's <paramref name="length"/> - and
    /// nothing after the digest: what they were written beside; null where they do not read (<see cref="Problem"/>).
    /// </summary>
    /// <param name="length">How many bytes the text's file holds.</param>
    internal string? End(long length)
    {
        while (Problem is null && (_current is not { } current || current.Next <= length) && !_read)
        {
            _current = NextBlock();
        }

        if (_current is { } past && past.Next > length)
        {
            Refuse("a block goes on past the text's end");
        }

        if (Problem is null && NextLine() is not null)
        {
            Refuse("a line follows the digest they end with");
        }

        return Problem is null ? _digest : null;
    }

    /// <summary>Lets the blocks' file go.</summary>
    public void Dispose() => _lines.Dispose();

    /// <summary>The next block, in the text's order; null once the digest after the last is read, or where the line is none of either.</summary>
    private TextBlock? NextBlock()
    {
        var line = NextLine();

        if (line is { Digest: { } digest } && line == new TextBlocksLine(Digest: digest))
        {
            (_digest, _read) = (digest, true);
            return null;
        }

        if (line is not { Head: { } head, Body: { } body, Next: { } next } || line != new TextBlocksLine(Head: head, Body: body, Next: next))
        {
            Refuse(line is null ? "they end before the digest of the text they are of" : "a line between their schema and their digest is a block");
            return null;
        }

        if (head < _passed || body <= head || next <= body)
        {
            Refuse("their blocks follow one another in the text, each its head and then the lines of it");
            return null;
        }

        _passed = next;
        return new TextBlock(head, body, next);
    }

    /// <summary>The blocks' next line, read; null where the file has ended, or it does not read - why being the blocks' <see cref="Problem"/>.</summary>
    private TextBlocksLine? NextLine()
    {
        if (Problem is not null)
        {
            return null;
        }

        try
        {
            if (!_lines.MoveNext())
            {
                return null;
            }

            // A line no line feed ended is a file cut short: the writer ends each one.
            if (!_lines.Current.Ended)
            {
                Refuse("their last line is cut short");
                return null;
            }

            return JsonSerializer.Deserialize<TextBlocksLine>(_lines.Current.Text, JsonStateFile.LineOptions) ?? throw new JsonException("it holds null");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Refuse($"a line of theirs does not read: {ex.Message.TrimEnd('.')}");
            return null;
        }
    }

    /// <summary>
    /// Writes a text's blocks into its file as the text is written (<see cref="FileOf"/>): the schema first, each block once
    /// its last line is written, in the text's order, and the digest of the text's bytes once the text has ended.
    /// </summary>
    internal sealed class Writer
    {
        private readonly JsonLineWriter _lines;

        /// <summary>A writer of blocks into <paramref name="stream"/>, the schema written first.</summary>
        /// <param name="stream">The blocks' file, as it is written.</param>
        public Writer(Stream stream)
        {
            _lines = new JsonLineWriter(stream);
            _lines.Write(new TextBlocksLine(Schema: CurrentSchema));
        }

        /// <summary>Writes <paramref name="block"/>, the block after the one written last.</summary>
        /// <param name="block">The block.</param>
        public void Add(TextBlock block) => _lines.Write(new TextBlocksLine(Head: block.Head, Body: block.Body, Next: block.Next));

        /// <summary>Ends the blocks with <paramref name="digest"/>, of the bytes of the text they are of (<see cref="FileContentHash.Of(ReadOnlySpan{byte})"/>).</summary>
        /// <param name="digest">The text's digest.</param>
        public void End(string digest) => _lines.Write(new TextBlocksLine(Digest: digest));
    }
}

/// <summary>One line of a text's blocks' file: the schema they are written under, a block, or the digest of the text they are of - one of the three.</summary>
/// <param name="Schema">The schema the blocks are written under, on their first line.</param>
/// <param name="Head">A block's <see cref="TextBlock.Head"/>.</param>
/// <param name="Body">A block's <see cref="TextBlock.Body"/>.</param>
/// <param name="Next">A block's <see cref="TextBlock.Next"/>.</param>
/// <param name="Digest">The digest of the text's bytes, on their last line.</param>
internal sealed record TextBlocksLine(int? Schema = null, long? Head = null, long? Body = null, long? Next = null, string? Digest = null);
