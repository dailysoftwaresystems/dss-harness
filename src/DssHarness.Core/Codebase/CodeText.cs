using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Text;
using DssHarness.Core.Pages;

namespace DssHarness.Core.Codebase;

/// <summary>
/// A file's decoded text as every implementation of the graph reads it: by position, a character, a run of them or a line
/// at a time - never one string of the whole file. A text opened over its file
/// (<see cref="OpenAsync(Func{Stream}, string, CancellationToken)"/>) is read through once, as
/// <see cref="FileSystem.EncodedText"/>'s rule reads a file, to learn where each of its blocks begins in its bytes and where
/// each of its lines begins; then each block is decoded again from the file only as it is read, and only a few blocks are
/// held at once. So what reading a file holds is what its reader keeps of it - its tokens, its nodes - and no file is too
/// long to read: a position is a <see cref="long"/>, and no run of the text the reader does not ask for is ever made a
/// string. A text of a string (<see cref="Of"/>) - a test's, a piece of code a markup file embeds - reads that string.
/// </summary>
/// <remarks>
/// Not safe for two threads at once: one reader reads one file. A text opened over its file holds the file open until it
/// is disposed; a block read again that does not decode as the first reading decoded it - the file changed while it was
/// read - throws <see cref="CodeTextChangedException"/>, which the core answers for the file, never its reader.
/// </remarks>
public sealed class CodeText : IDisposable
{
    /// <summary>How many characters a block of a text read from its file holds, but where a surrogate pair would be parted.</summary>
    internal const int BlockCharacters = 32 * 1024;

    /// <summary>The fewest characters a block holds: a surrogate pair, which no block parts.</summary>
    internal const int FewestBlockCharacters = 2;

    /// <summary>How many blocks a text read from its file holds decoded at once: what is read again is decoded again.</summary>
    private const int HeldBlocks = 64;

    private readonly string? _whole;
    private readonly CodeTextBlocks? _blocks;
    private readonly int _blockCharacters = BlockCharacters;
    private readonly char[]?[] _held = new char[]?[HeldBlocks];
    private readonly int[] _heldBlock = [.. Enumerable.Repeat(-1, HeldBlocks)];
    private readonly long[] _heldUsed = new long[HeldBlocks];
    private CodeLineIndex? _lines;
    private CodeText? _lineFed;
    private long _uses;
    private char[] _current = [];
    private long _currentStart;
    private int _currentLength;

    private CodeText(string whole)
    {
        _whole = whole;
        Length = whole.Length;
        HoldsCarriageReturn = whole.Contains('\r', StringComparison.Ordinal);
    }

    private CodeText(CodeTextBlocks blocks, CodeLineIndex lines, bool carriageReturn, IReadOnlyList<char[]> first, int blockCharacters)
    {
        _blocks = blocks;
        _blockCharacters = blockCharacters;
        _lines = lines;
        Length = blocks.Length;
        HoldsCarriageReturn = carriageReturn;

        for (var block = 0; block < first.Count && block < HeldBlocks; block++)
        {
            Hold(block, first[block]);
        }
    }

    /// <summary>How many characters it has.</summary>
    public long Length { get; }

    /// <summary>Whether a carriage return is among its characters: where none is, its lines end at line feeds alone.</summary>
    public bool HoldsCarriageReturn { get; }

    /// <summary>Where each of its lines begins, by the one rule of where a line ends (<see cref="Text.LineEnds"/>).</summary>
    public CodeLineIndex LineIndex => _lines ??= CodeLineIndex.Of(this);

    /// <summary>
    /// Its lines, without their endings, as <see cref="CodeFileText.Lines"/> cuts a string's: each read from the text when it
    /// is asked for, so only the lines a reader keeps are held.
    /// </summary>
    public IReadOnlyList<string> Lines => new CodeTextLines(this);

    /// <summary>The character at <paramref name="at"/>.</summary>
    /// <param name="at">A position in the text, from 0.</param>
    /// <exception cref="IndexOutOfRangeException">The position is not in the text.</exception>
    public char this[long at]
    {
        get
        {
            if (_whole is { } whole)
            {
                return (ulong)at < (ulong)whole.Length ? whole[(int)at] : throw new IndexOutOfRangeException();
            }

            var offset = at - _currentStart;
            return (ulong)offset < (ulong)_currentLength ? _current[offset] : Fetch(at);
        }
    }

    /// <summary>A text of <paramref name="text"/>.</summary>
    /// <param name="text">The text.</param>
    public static CodeText Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new CodeText(text);
    }

    /// <summary>A text of <paramref name="text"/> (<see cref="Of"/>).</summary>
    /// <param name="text">The text.</param>
    public static implicit operator CodeText(string text) => Of(text);

    /// <summary>
    /// The text of the file <paramref name="open"/> opens, read through once by <see cref="FileSystem.EncodedText"/>'s rule, streamed
    /// (<see cref="TextPages.TextSource"/>): where each block begins in its bytes and where each line begins is kept, and the
    /// first blocks decoded; the rest is decoded again from the file as it is read.
    /// </summary>
    /// <param name="open">Opens the file: once for the reading through, and once for the reads after it, which it holds open.</param>
    /// <param name="path">The file, as a refusal names it.</param>
    /// <param name="cancellationToken">Stops the reading through.</param>
    /// <exception cref="InvalidDataException">
    /// The file is binary, no text the rule reads, or holds more lines than a line's number counts - an
    /// <see cref="int"/>, as every line of the graph is numbered - each with why (<see cref="TextPages.ReadingOf"/>).
    /// </exception>
    internal static Task<CodeText> OpenAsync(Func<Stream> open, string path, CancellationToken cancellationToken)
        => OpenAsync(open, path, BlockCharacters, cancellationToken);

    /// <summary>
    /// The text of the file <paramref name="open"/> opens (<see cref="OpenAsync(Func{Stream}, string, CancellationToken)"/>),
    /// in blocks of <paramref name="blockCharacters"/>: a test's few, so that every read crosses a block's end and every
    /// block is let go and read again.
    /// </summary>
    /// <param name="open">Opens the file.</param>
    /// <param name="path">The file, as a refusal names it.</param>
    /// <param name="blockCharacters">How many characters a block holds; at least <see cref="FewestBlockCharacters"/>.</param>
    /// <param name="cancellationToken">Stops the reading through.</param>
    internal static async Task<CodeText> OpenAsync(Func<Stream> open, string path, int blockCharacters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockCharacters, FewestBlockCharacters);

        var starts = new LineStarts();
        var charStarts = new List<long>();
        var byteStarts = new List<long>();
        var first = new List<char[]>();
        var block = new char[blockCharacters];
        var filled = 0;
        long blockStart = 0;
        long byteStart;
        var carriageReturn = false;
        var afterReturn = false;
        Encoding encoding;

        var reading = open();

        await using (reading.ConfigureAwait(false))
        {
            var source = await TextPages.TextSource.OpenAsync(reading, path, hash: null, cancellationToken).ConfigureAwait(false);
            encoding = source.Decoding;
            byteStart = source.Preamble;

            // The block's characters given up to `keep` - one a high surrogate ends with left for the next, so no block parts
            // a pair of its bytes - its bytes counted, and the first blocks kept decoded.
            void Close(bool last)
            {
                var keep = !last && filled > 0 && char.IsHighSurrogate(block[filled - 1]) ? filled - 1 : filled;

                charStarts.Add(blockStart);
                byteStarts.Add(byteStart);

                if (first.Count < HeldBlocks)
                {
                    first.Add(block.AsSpan(0, keep).ToArray());
                }

                blockStart += keep;
                byteStart += encoding is UnicodeEncoding ? 2L * keep : Encoding.UTF8.GetByteCount(block.AsSpan(0, keep));

                if (keep < filled)
                {
                    block[0] = block[keep];
                }

                filled -= keep;
            }

            while (true)
            {
                var read = await source.ReadAsync(block.AsMemory(filled, blockCharacters - filled), cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                // Where each line begins after these characters: past each ending, a CR LF one ending, as LineEnds cuts them -
                // the LF right after a CR moving the line the CR began past itself.
                var span = block.AsSpan(filled, read);

                for (var at = span.IndexOfAny('\r', '\n'); at >= 0;)
                {
                    var position = blockStart + filled + at;

                    if (span[at] == '\n' && afterReturn && starts.Last == position)
                    {
                        starts.Last = position + 1;
                        afterReturn = false;
                    }
                    else
                    {
                        if (starts.Count == int.MaxValue)
                        {
                            throw TextPages.NotText($"'{path}' holds more lines than the graph numbers", binary: false, TooManyLines);
                        }

                        starts.Add(position + 1);
                        afterReturn = span[at] == '\r';
                        carriageReturn |= afterReturn;
                    }

                    var next = span[(at + 1)..].IndexOfAny('\r', '\n');
                    at = next < 0 ? -1 : at + 1 + next;
                }

                filled += read;

                if (filled == blockCharacters)
                {
                    Close(last: false);
                }
            }

            if (filled > 0 || charStarts.Count == 0)
            {
                Close(last: true);
            }
        }

        // An ending the text ends with closes its last line rather than opening another.
        if (starts.Count > 1 && starts.Last == blockStart)
        {
            starts.RemoveLast();
        }

        charStarts.Add(blockStart);
        byteStarts.Add(byteStart);

        var blocks = new FileBlocks(open, path, encoding, [.. charStarts], [.. byteStarts]);
        return new CodeText(blocks, new CodeLineIndex(starts), carriageReturn, first, blockCharacters);
    }

    /// <summary>Why a text of more lines than a line's number counts is not read.</summary>
    internal static string TooManyLines { get; } = string.Create(CultureInfo.InvariantCulture, $"it holds more than {int.MaxValue:N0} lines, past the Int32 every line of the graph is numbered by");

    /// <summary>
    /// This text with every line ending one line feed - a CR LF and a CR alike - its lines the ones it has: where it holds no
    /// carriage return, itself; else a text that reads it so, made once, by its own reading through of this one.
    /// </summary>
    public CodeText LineFed()
    {
        if (!HoldsCarriageReturn)
        {
            return this;
        }

        if (_whole is { } whole)
        {
            return _lineFed ??= Of(whole.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        }

        return _lineFed ??= LineFedBlocks.Over(this);
    }

    /// <summary>
    /// A text made of <paramref name="pieces"/>, in order: each a run of this text, or a string written between runs - what a
    /// reader that reads a text written otherwise than the file reads, made of the file's own runs, so no copy of the file's
    /// text is made: a string where this is one, else a text that reads each run from this one as it is read, its lines
    /// found by reading it through once.
    /// </summary>
    /// <param name="pieces">The runs and the strings, in order.</param>
    public CodeText Composed(IReadOnlyList<CodeTextPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        if (_whole is not null)
        {
            var composed = new StringBuilder();

            var chars = new char[1024];

            foreach (var piece in pieces)
            {
                for (long done = 0; done < piece.Length;)
                {
                    var count = (int)Math.Min(chars.Length, piece.Length - done);
                    piece.CopyTo(this, done, chars.AsSpan(0, count));
                    composed.Append(chars, 0, count);
                    done += count;
                }
            }

            return Of(composed.ToString());
        }

        return ComposedBlocks.Over(this, pieces);
    }

    /// <summary>The characters from <paramref name="start"/> to <paramref name="start"/> + <paramref name="length"/>, as a string.</summary>
    /// <param name="start">Where they begin.</param>
    /// <param name="length">How many.</param>
    public string Substring(long start, long length) => Slice(start, start + length);

    /// <summary>The characters from <paramref name="start"/> to the one before <paramref name="end"/>, as a string.</summary>
    /// <param name="start">Where they begin.</param>
    /// <param name="end">Where they end: past the last.</param>
    public string Slice(long start, long end)
    {
        Check(start, end);

        if (_whole is { } whole)
        {
            return whole[(int)start..(int)end];
        }

        return end == start ? string.Empty : string.Create(checked((int)(end - start)), (Text: this, Start: start), static (into, state) => state.Text.CopyTo(state.Start, into));
    }

    /// <summary>The characters from <paramref name="start"/> on, as many as <paramref name="into"/> holds, copied into it.</summary>
    /// <param name="start">Where they begin.</param>
    /// <param name="into">Where they are copied.</param>
    public void CopyTo(long start, Span<char> into)
    {
        Check(start, start + into.Length);

        if (_whole is { } whole)
        {
            whole.AsSpan((int)start, into.Length).CopyTo(into);
            return;
        }

        while (into.Length > 0)
        {
            var (chars, from, count) = Run(start);
            var taken = Math.Min(count, into.Length);

            chars.AsSpan(from, taken).CopyTo(into);
            into = into[taken..];
            start += taken;
        }
    }

    /// <summary>The characters from <paramref name="start"/> to the one before <paramref name="end"/>, appended to <paramref name="builder"/>.</summary>
    /// <param name="builder">What they are appended to.</param>
    /// <param name="start">Where they begin.</param>
    /// <param name="end">Where they end: past the last.</param>
    public void AppendTo(StringBuilder builder, long start, long end)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Check(start, end);

        if (_whole is { } whole)
        {
            builder.Append(whole, (int)start, (int)(end - start));
            return;
        }

        while (start < end)
        {
            var (chars, from, count) = Run(start);
            var taken = (int)Math.Min(count, end - start);

            builder.Append(chars, from, taken);
            start += taken;
        }
    }

    /// <summary>Whether the characters at <paramref name="at"/> are <paramref name="value"/>, ordinally; false where the text ends before they do.</summary>
    /// <param name="at">Where they would begin.</param>
    /// <param name="value">What they would be.</param>
    public bool At(long at, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (at < 0 || at > Length - value.Length)
        {
            return false;
        }

        if (_whole is { } whole)
        {
            return string.CompareOrdinal(whole, (int)at, value, 0, value.Length) == 0;
        }

        for (var index = 0; index < value.Length;)
        {
            var (chars, from, count) = Run(at + index);
            var taken = Math.Min(count, value.Length - index);

            if (!chars.AsSpan(from, taken).SequenceEqual(value.AsSpan(index, taken)))
            {
                return false;
            }

            index += taken;
        }

        return true;
    }

    /// <summary>Whether the characters at <paramref name="at"/> are <paramref name="value"/> by <paramref name="comparison"/>; false where the text ends before they do.</summary>
    /// <param name="at">Where they would begin.</param>
    /// <param name="value">What they would be.</param>
    /// <param name="comparison">How they are compared: <see cref="StringComparison.OrdinalIgnoreCase"/>, say.</param>
    public bool At(long at, string value, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (at < 0 || at > Length - value.Length)
        {
            return false;
        }

        if (_whole is { } whole)
        {
            return string.Compare(whole, (int)at, value, 0, value.Length, comparison) == 0;
        }

        var held = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        CopyTo(at, held);
        return held.Equals(value, comparison);
    }

    /// <summary>Whether the <paramref name="length"/> characters at <paramref name="start"/> - a token's - are <paramref name="value"/>, ordinally.</summary>
    /// <param name="start">Where they begin.</param>
    /// <param name="length">How many they are.</param>
    /// <param name="value">What they would be.</param>
    public bool Is(long start, long length, string value) => length == value.Length && At(start, value);

    /// <summary>Whether the text begins with <paramref name="value"/>, ordinally.</summary>
    /// <param name="value">What it would begin with.</param>
    public bool StartsWith(string value) => At(0, value);

    /// <summary>Whether the text ends with <paramref name="value"/>, ordinally.</summary>
    /// <param name="value">What it would end with.</param>
    public bool EndsWith(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length <= Length && At(Length - value.Length, value);
    }

    /// <summary>Whether <paramref name="value"/> stands anywhere in the text, ordinally.</summary>
    /// <param name="value">What is sought.</param>
    public bool Contains(string value) => IndexOf(value) >= 0;

    /// <summary>Whether <paramref name="value"/> stands anywhere in the text.</summary>
    /// <param name="value">The character sought.</param>
    public bool Contains(char value) => IndexOf(value) >= 0;

    /// <summary>Where <paramref name="value"/> first stands from <paramref name="start"/> on; -1 where it does not.</summary>
    /// <param name="value">The character sought.</param>
    /// <param name="start">Where the search begins.</param>
    public long IndexOf(char value, long start = 0) => FindAny([value], start, Length);

    /// <summary>
    /// Where <paramref name="value"/> first stands from <paramref name="start"/> to before <paramref name="end"/>; -1 where
    /// it does not. Named apart from a string's <c>IndexOf</c>, whose third number is a count.
    /// </summary>
    /// <param name="value">The character sought.</param>
    /// <param name="start">Where the search begins.</param>
    /// <param name="end">Where it ends: past the last character looked at.</param>
    public long Find(char value, long start, long end) => FindAny([value], start, end);

    /// <summary>Where the first of <paramref name="values"/> stands from <paramref name="start"/> to before <paramref name="end"/>; -1 where none does.</summary>
    /// <param name="values">The characters sought.</param>
    /// <param name="start">Where the search begins.</param>
    /// <param name="end">Where it ends: past the last character looked at.</param>
    public long FindAny(ReadOnlySpan<char> values, long start, long end)
    {
        Check(start, end);

        if (_whole is { } whole)
        {
            var found = whole.AsSpan((int)start, (int)(end - start)).IndexOfAny(values);
            return found < 0 ? -1 : start + found;
        }

        while (start < end)
        {
            var (chars, from, count) = Run(start);
            var taken = (int)Math.Min(count, end - start);
            var found = chars.AsSpan(from, taken).IndexOfAny(values);

            if (found >= 0)
            {
                return start + found;
            }

            start += taken;
        }

        return -1;
    }

    /// <summary>Where the first of <paramref name="values"/> stands from <paramref name="start"/> to before <paramref name="end"/>; -1 where none does.</summary>
    /// <param name="values">The characters sought.</param>
    /// <param name="start">Where the search begins.</param>
    /// <param name="end">Where it ends: past the last character looked at.</param>
    public long FindAny(SearchValues<char> values, long start, long end)
    {
        ArgumentNullException.ThrowIfNull(values);
        Check(start, end);

        if (_whole is { } whole)
        {
            var found = whole.AsSpan((int)start, (int)(end - start)).IndexOfAny(values);
            return found < 0 ? -1 : start + found;
        }

        while (start < end)
        {
            var (chars, from, count) = Run(start);
            var taken = (int)Math.Min(count, end - start);
            var found = chars.AsSpan(from, taken).IndexOfAny(values);

            if (found >= 0)
            {
                return start + found;
            }

            start += taken;
        }

        return -1;
    }

    /// <summary>Whether every character from <paramref name="start"/> to before <paramref name="end"/> is white space: none is, too.</summary>
    /// <param name="start">Where they begin.</param>
    /// <param name="end">Where they end.</param>
    public bool IsWhiteSpace(long start, long end)
    {
        Check(start, end);

        for (var at = start; at < end; at++)
        {
            if (!char.IsWhiteSpace(this[at]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Where <paramref name="value"/> first stands from <paramref name="start"/> on, ordinally; -1 where it does not.</summary>
    /// <param name="value">The characters sought.</param>
    /// <param name="start">Where the search begins.</param>
    public long IndexOf(string value, long start = 0) => Find(value, start, Length);

    /// <summary>
    /// Where <paramref name="value"/> first stands whole from <paramref name="start"/> to before <paramref name="end"/>,
    /// ordinally; -1 where it does not. Named apart from a string's <c>IndexOf</c>, whose third number is a count.
    /// </summary>
    /// <param name="value">The characters sought.</param>
    /// <param name="start">Where the search begins.</param>
    /// <param name="end">Where it ends: past the last character an occurrence may hold.</param>
    public long Find(string value, long start, long end)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        Check(start, end);

        if (_whole is { } whole)
        {
            var found = whole.AsSpan((int)start, (int)(end - start)).IndexOf(value, StringComparison.Ordinal);
            return found < 0 ? -1 : start + found;
        }

        for (var at = Find(value[0], start, end); at >= 0 && at <= end - value.Length; at = at + 1 < end ? Find(value[0], at + 1, end) : -1)
        {
            if (At(at, value))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>A reader of the characters from <paramref name="start"/> to before <paramref name="end"/>, a block at a time: what a parser that reads a <see cref="TextReader"/> is given.</summary>
    /// <param name="start">Where it begins.</param>
    /// <param name="end">Where it ends: past the last character it gives.</param>
    public TextReader Reader(long start, long end)
    {
        Check(start, end);
        return new CodeTextReader(this, start, end);
    }

    /// <summary>A reader of the whole text (<see cref="Reader(long, long)"/>).</summary>
    public TextReader Reader() => Reader(0, Length);

    /// <summary>
    /// The text's characters from <paramref name="start"/> on as the bytes of UTF-8, read a block at a time: what a parser
    /// that reads bytes is given, whatever encoding the file was found in. A lone surrogate - which no decoded file holds,
    /// only a string given as a text may - is written as U+FFFD, as <see cref="Encoding.UTF8"/> writes it.
    /// </summary>
    /// <param name="start">Where the bytes begin: 0, the first character, where none is given.</param>
    public Stream Utf8(long start = 0)
    {
        Check(start, Length);
        return new CodeTextUtf8(this, start);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _blocks?.Dispose();
        _lineFed?.Dispose();
    }

    /// <summary>The block that holds <paramref name="at"/>, made the one read first, and the character there.</summary>
    private char Fetch(long at)
    {
        if ((ulong)at >= (ulong)Length)
        {
            throw new IndexOutOfRangeException();
        }

        var (chars, from, _) = Run(at);
        return chars[from];
    }

    /// <summary>
    /// The block that holds <paramref name="at"/> - decoded from the file where it is not held, one held the longest
    /// unread let go for it - made the one read first: its characters, where <paramref name="at"/> stands in them, and how
    /// many of them are from there on.
    /// </summary>
    private (char[] Chars, int From, int Count) Run(long at)
    {
        var offset = at - _currentStart;

        if ((ulong)offset < (ulong)_currentLength)
        {
            return (_current, (int)offset, _currentLength - (int)offset);
        }

        var blocks = _blocks!;
        var block = blocks.BlockOf(at);
        char[]? chars = null;

        for (var slot = 0; slot < HeldBlocks; slot++)
        {
            if (_heldBlock[slot] == block)
            {
                chars = _held[slot];
                _heldUsed[slot] = ++_uses;
                break;
            }
        }

        chars ??= Hold(block, blocks.Read(block));
        _current = chars;
        _currentStart = blocks.StartOf(block);
        _currentLength = chars.Length;
        return (chars, (int)(at - _currentStart), _currentLength - (int)(at - _currentStart));
    }

    /// <summary>Holds <paramref name="chars"/>, the block <paramref name="block"/>, in the place of the one held the longest unread.</summary>
    private char[] Hold(int block, char[] chars)
    {
        var slot = 0;

        for (var other = 1; other < HeldBlocks; other++)
        {
            if (_heldUsed[other] < _heldUsed[slot])
            {
                slot = other;
            }
        }

        _held[slot] = chars;
        _heldBlock[slot] = block;
        _heldUsed[slot] = ++_uses;
        return chars;
    }

    /// <summary>Refuses a run of the text that is not in it.</summary>
    private void Check(long start, long end)
    {
        if (start < 0 || end < start || end > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"the run from {start} to {end} is not in a text of {Length} characters");
        }
    }

    /// <summary>Where a text's blocks are read from: a file's bytes, or another text.</summary>
    private abstract class CodeTextBlocks : IDisposable
    {
        /// <summary>Where each block begins, in characters, the text's length after the last.</summary>
        protected abstract long[] Starts { get; }

        /// <summary>How many characters the text has.</summary>
        public long Length => Starts[^1];

        /// <summary>The block that holds <paramref name="at"/>.</summary>
        public int BlockOf(long at)
        {
            var found = Array.BinarySearch(Starts, 0, Starts.Length - 1, at);
            return found >= 0 ? found : ~found - 1;
        }

        /// <summary>Where the block <paramref name="block"/> begins.</summary>
        public long StartOf(int block) => Starts[block];

        /// <summary>The characters of <paramref name="block"/>, decoded again.</summary>
        public abstract char[] Read(int block);

        /// <inheritdoc/>
        public virtual void Dispose()
        {
        }
    }

    /// <summary>
    /// The blocks of a file's text: each one's characters decoded strictly from its own bytes, which the reading through
    /// found - the file held open for them, sought to each block where it seeks, else opened again and read up to it.
    /// </summary>
    private sealed class FileBlocks(Func<Stream> open, string path, Encoding encoding, long[] starts, long[] bytes) : CodeTextBlocks
    {
        private Stream? _stream;

        protected override long[] Starts => starts;

        public override char[] Read(int block)
        {
            var count = checked((int)(bytes[block + 1] - bytes[block]));
            var chars = new char[starts[block + 1] - starts[block]];
            var held = ArrayPool<byte>.Shared.Rent(Math.Max(count, 1));

            try
            {
                var stream = _stream ??= open();

                if (stream.CanSeek)
                {
                    stream.Seek(bytes[block], SeekOrigin.Begin);
                }
                else
                {
                    stream.Dispose();
                    _stream = stream = open();

                    for (var left = bytes[block]; left > 0;)
                    {
                        var skipped = stream.Read(held, 0, (int)Math.Min(left, held.Length));
                        left -= skipped > 0 ? skipped : throw new EndOfStreamException();
                    }
                }

                stream.ReadExactly(held, 0, count);

                if (encoding.GetChars(held, 0, count, chars, 0) != chars.Length)
                {
                    throw new CodeTextChangedException(path);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or DecoderFallbackException or ArgumentException)
            {
                // Bytes that end before, or decode otherwise than, the reading through found them: not the file it read.
                throw new CodeTextChangedException(path, ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(held);
            }

            return chars;
        }

        public override void Dispose() => _stream?.Dispose();
    }

    /// <summary>
    /// The blocks of a text that reads another with every line ending one line feed: each one's characters read from the
    /// other from where its reading through found it begins, a CR LF read as one line feed and a CR as another.
    /// </summary>
    private sealed class LineFedBlocks(CodeText source, long[] starts, long[] sources) : CodeTextBlocks
    {
        protected override long[] Starts => starts;

        /// <summary>A text that reads <paramref name="source"/> line fed, its blocks and its lines found by one reading through of it.</summary>
        public static CodeText Over(CodeText source)
        {
            var starts = new List<long>();
            var sources = new List<long>();
            var lines = new LineStarts();
            var first = new List<char[]>();
            var size = source._blockCharacters;
            var block = new char[size];
            var filled = 0;
            long written = 0;

            void Close()
            {
                if (first.Count < HeldBlocks)
                {
                    first.Add(block.AsSpan(0, filled).ToArray());
                }

                written += filled;
                filled = 0;
            }

            for (long at = 0; at < source.Length;)
            {
                if (filled == 0)
                {
                    starts.Add(written);
                    sources.Add(at);
                }

                var character = source[at++];

                if (character == '\r')
                {
                    character = '\n';
                    at += at < source.Length && source[at] == '\n' ? 1 : 0;
                }

                if (character == '\n' && at < source.Length)
                {
                    lines.Add(written + filled + 1);
                }

                block[filled++] = character;

                if (filled == size)
                {
                    Close();
                }
            }

            if (filled > 0 || starts.Count == 0)
            {
                if (starts.Count == 0)
                {
                    starts.Add(0);
                    sources.Add(0);
                }

                Close();
            }

            starts.Add(written);
            sources.Add(source.Length);
            return new CodeText(new LineFedBlocks(source, [.. starts], [.. sources]), new CodeLineIndex(lines), carriageReturn: false, first, size);
        }

        public override char[] Read(int block)
        {
            var chars = new char[starts[block + 1] - starts[block]];
            var at = sources[block];

            for (var filled = 0; filled < chars.Length; filled++)
            {
                var character = source[at++];

                if (character == '\r')
                {
                    character = '\n';
                    at += at < source.Length && source[at] == '\n' ? 1 : 0;
                }

                chars[filled] = character;
            }

            return chars;
        }
    }

    /// <summary>
    /// The blocks of a text made of runs of another and strings between them (<see cref="Composed"/>): each piece kept as
    /// where it begins in the text made, each block as the piece it begins in, the text made read through once for its
    /// lines.
    /// </summary>
    private sealed class ComposedBlocks(CodeText source, CodeTextPiece[] pieces, long[] at, long[] starts) : CodeTextBlocks
    {
        protected override long[] Starts => starts;

        /// <summary>A text of <paramref name="pieces"/> over <paramref name="source"/>.</summary>
        public static CodeText Over(CodeText source, IReadOnlyList<CodeTextPiece> pieces)
        {
            var kept = new CodeTextPiece[pieces.Count];
            var at = new long[pieces.Count + 1];

            for (var index = 0; index < pieces.Count; index++)
            {
                var piece = pieces[index];

                if (piece.Written is null && piece.Repeated == '\0')
                {
                    source.Check(piece.Start, piece.End);
                }

                kept[index] = piece;
                at[index + 1] = at[index] + piece.Length;
            }

            var starts = new List<long>();

            for (long start = 0; start < at[^1]; start += source._blockCharacters)
            {
                starts.Add(start);
            }

            if (starts.Count == 0)
            {
                starts.Add(0);
            }

            starts.Add(at[^1]);

            var blocks = new ComposedBlocks(source, kept, at, [.. starts]);
            var text = new CodeText(blocks, new CodeLineIndex(new LineStarts()), carriageReturn: false, [], source._blockCharacters);

            // Its own lines and whether it holds a carriage return, read through once.
            return new CodeText(blocks, CodeLineIndex.Of(text), text.IndexOf('\r') >= 0, [], source._blockCharacters);
        }

        public override char[] Read(int block)
        {
            var start = starts[block];
            var chars = new char[starts[block + 1] - start];
            var found = Array.BinarySearch(at, start);
            var piece = found >= 0 ? found : ~found - 1;

            for (var filled = 0; filled < chars.Length; piece++)
            {
                var from = start + filled - at[piece];
                var count = (int)Math.Min(pieces[piece].Length - from, chars.Length - filled);

                if (count <= 0)
                {
                    continue;
                }

                pieces[piece].CopyTo(source, from, chars.AsSpan(filled, count));
                filled += count;
            }

            return chars;
        }
    }

    /// <summary>A text's lines, each read from it when it is asked for.</summary>
    private sealed class CodeTextLines(CodeText text) : IReadOnlyList<string>
    {
        public int Count => text.LineIndex.Count;

        public string this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

                var (start, end) = text.LineIndex.Span(text, index + 1);
                return text.Slice(start, end);
            }
        }

        public IEnumerator<string> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A reader of a run of a text, a block at a time.</summary>
    private sealed class CodeTextReader(CodeText text, long at, long end) : TextReader
    {
        public override int Peek() => at < end ? text[at] : -1;

        public override int Read() => at < end ? text[at++] : -1;

        public override int Read(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));

        public override int Read(Span<char> buffer)
        {
            var count = (int)Math.Min(buffer.Length, end - at);
            text.CopyTo(at, buffer[..count]);
            at += count;
            return count;
        }

        public override int ReadBlock(Span<char> buffer) => Read(buffer);

        public override int ReadBlock(char[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));
    }

    /// <summary>
    /// A text's characters as the bytes of UTF-8, encoded a block at a time as they are read: a read given room for fewer
    /// bytes than a character's is given as many of them as fit, the rest the next read's first - so a read answers no
    /// bytes at the text's end alone, as a stream's must.
    /// </summary>
    private sealed class CodeTextUtf8(CodeText text, long start) : Stream
    {
        private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();
        private readonly char[] _chars = new char[4096];
        private readonly byte[] _aside = new byte[8];
        private int _asideFrom;
        private int _asideTo;
        private long _at = start;
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            // What a character left aside, first.
            var written = Math.Min(buffer.Length, _asideTo - _asideFrom);
            _aside.AsSpan(_asideFrom, written).CopyTo(buffer);
            _asideFrom += written;

            while (written < buffer.Length && _at < text.Length)
            {
                if (buffer.Length - written >= 4)
                {
                    // Each character's bytes whole: as many characters as the room surely holds.
                    var count = (int)Math.Min(Math.Min(_chars.Length, (buffer.Length - written) / 3), text.Length - _at);
                    text.CopyTo(_at, _chars.AsSpan(0, count));
                    _encoder.Convert(_chars.AsSpan(0, count), buffer[written..], flush: _at + count == text.Length, out var used, out var bytes, out _);
                    _at += used;
                    written += bytes;
                    continue;
                }

                // Room for fewer bytes than a character may take: the next character - a pair's two halves - encoded aside,
                // as many of its bytes given as fit.
                var one = (int)Math.Min(char.IsHighSurrogate(text[_at]) ? 2 : 1, text.Length - _at);
                text.CopyTo(_at, _chars.AsSpan(0, one));
                _encoder.Convert(_chars.AsSpan(0, one), _aside, flush: _at + one == text.Length, out var taken, out var encoded, out _);
                _at += taken;

                var given = Math.Min(buffer.Length - written, encoded);
                _aside.AsSpan(0, given).CopyTo(buffer[written..]);
                (_asideFrom, _asideTo) = (given, encoded);
                written += given;
            }

            _position += written;
            return written;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Where a text's lines begin, by the one rule of where a line ends (<see cref="Text.LineEnds"/>): a position in it told as
/// the line it is on, and a line as where it begins and ends. Its positions are <see cref="long"/>, as a text's are, and
/// its lines as many as a line's number counts.
/// </summary>
public sealed class CodeLineIndex
{
    private readonly LineStarts _starts;

    /// <summary>An index of lines beginning at <paramref name="starts"/>, in order, the first at 0.</summary>
    /// <param name="starts">Where each line begins.</param>
    public CodeLineIndex(IEnumerable<long> starts)
    {
        ArgumentNullException.ThrowIfNull(starts);
        _starts = new LineStarts(empty: true);

        foreach (var start in starts)
        {
            _starts.Add(start);
        }

        if (_starts.Count == 0 || _starts[0] != 0)
        {
            throw new ArgumentException("a text's first line begins at 0", nameof(starts));
        }
    }

    internal CodeLineIndex(LineStarts starts) => _starts = starts;

    /// <summary>Where each line begins, in order; the first at 0.</summary>
    public IReadOnlyList<long> Starts => _starts;

    /// <summary>How many lines the text has: an empty text one.</summary>
    public int Count => _starts.Count;

    /// <summary>The index of <paramref name="text"/>'s lines, found by reading it through once.</summary>
    internal static CodeLineIndex Of(CodeText text)
    {
        var starts = new LineStarts();

        for (var at = text.FindAny("\r\n", 0, text.Length); at >= 0; at = at + 1 < text.Length ? text.FindAny("\r\n", at + 1, text.Length) : -1)
        {
            if (text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n')
            {
                at++;
            }

            if (at + 1 < text.Length)
            {
                starts.Add(at + 1);
            }
        }

        return new CodeLineIndex(starts);
    }

    /// <summary>The line, from 1, that <paramref name="position"/> - a character of the text, or its end - is on.</summary>
    /// <param name="position">A position in the text.</param>
    public int LineAt(long position) => Math.Max(_starts.CountAtOrBefore(position), 1);

    /// <summary>Where the line <paramref name="line"/>, from 1, begins.</summary>
    /// <param name="line">The line.</param>
    public long StartOf(int line) => _starts[line - 1];

    /// <summary>
    /// Where the line <paramref name="line"/>, from 1, of <paramref name="text"/> - the text this indexes - begins, and
    /// where its text ends before its ending: what <see cref="CodeText.Lines"/> reads of it.
    /// </summary>
    /// <param name="text">The text this indexes.</param>
    /// <param name="line">The line.</param>
    public (long Start, long End) Span(CodeText text, int line)
    {
        ArgumentNullException.ThrowIfNull(text);

        var start = _starts[line - 1];
        var end = line < _starts.Count ? _starts[line] : text.Length;

        if (end > start && text[end - 1] == '\n')
        {
            end -= end - 1 > start && text[end - 2] == '\r' ? 2 : 1;
        }
        else if (end > start && text[end - 1] == '\r')
        {
            end--;
        }

        return (start, end);
    }
}

/// <summary>
/// Where a text's lines begin, in order, held in segments of a fixed size: as many as a line's number counts, never one
/// array of them all, nor one copied whole as it grows.
/// </summary>
internal sealed class LineStarts : IReadOnlyList<long>
{
    private const int SegmentLength = 64 * 1024;
    private readonly List<long[]> _segments = [];

    /// <summary>Starts holding a text's first line, at 0, or none where <paramref name="empty"/>.</summary>
    public LineStarts(bool empty = false)
    {
        if (!empty)
        {
            Add(0);
        }
    }

    public int Count { get; private set; }

    /// <summary>Where the last line begins.</summary>
    public long Last
    {
        get => this[Count - 1];
        set => _segments[(Count - 1) / SegmentLength][(Count - 1) % SegmentLength] = value;
    }

    public long this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _segments[index / SegmentLength][index % SegmentLength];
        }
    }

    /// <summary>Adds a line beginning at <paramref name="start"/>, after every other.</summary>
    public void Add(long start)
    {
        if (Count == _segments.Count * SegmentLength)
        {
            _segments.Add(new long[SegmentLength]);
        }

        _segments[Count / SegmentLength][Count % SegmentLength] = start;
        Count++;
    }

    /// <summary>Lets the last line go.</summary>
    public void RemoveLast() => Count--;

    /// <summary>How many lines begin at or before <paramref name="position"/>: the line it is on, counted from 1.</summary>
    public int CountAtOrBefore(long position)
    {
        var low = 0;
        var high = Count;

        // The first line that begins past the position.
        while (low < high)
        {
            var middle = low + ((high - low) / 2);

            if (this[middle] <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    public IEnumerator<long> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// One piece of a text made of another's runs (<see cref="CodeText.Composed"/>): a run of that text, the same run with every
/// character a space but a line feed - code taken out where it stood - a string written there, or one character written a
/// number of times.
/// </summary>
/// <param name="Start">Where a run begins in the text it is of.</param>
/// <param name="End">Where it ends: past its last character; for a repeated character, how many times it is written.</param>
/// <param name="Written">The string, where the piece is one.</param>
/// <param name="Repeated">The character written <paramref name="End"/> times, where the piece is one; none otherwise.</param>
/// <param name="Blank">Whether the run is given blank: every character of it a space but a line feed.</param>
public readonly record struct CodeTextPiece(long Start, long End, string? Written = null, char Repeated = '\0', bool Blank = false)
{
    /// <summary>How many characters it gives.</summary>
    public long Length => Written?.Length ?? End - Start;

    /// <summary>The run from <paramref name="start"/> to before <paramref name="end"/>.</summary>
    public static CodeTextPiece Run(long start, long end) => new(start, end);

    /// <summary>The run from <paramref name="start"/> to before <paramref name="end"/>, given blank: every character a space but a line feed.</summary>
    public static CodeTextPiece Blanked(long start, long end) => new(start, end, Blank: true);

    /// <summary>The string <paramref name="written"/>.</summary>
    public static CodeTextPiece Of(string written) => new(0, 0, written);

    /// <summary><paramref name="character"/>, written <paramref name="count"/> times.</summary>
    public static CodeTextPiece Repeat(char character, long count) => new(0, count, Repeated: character);

    /// <summary>Its characters from <paramref name="from"/> on, as many as <paramref name="into"/> holds, copied into it: a run's read from <paramref name="source"/>.</summary>
    internal void CopyTo(CodeText source, long from, Span<char> into)
    {
        if (Written is { } written)
        {
            written.AsSpan((int)from, into.Length).CopyTo(into);
        }
        else if (Repeated != '\0')
        {
            into.Fill(Repeated);
        }
        else
        {
            source.CopyTo(Start + from, into);

            if (Blank)
            {
                for (var at = 0; at < into.Length; at++)
                {
                    into[at] = into[at] == '\n' ? '\n' : ' ';
                }
            }
        }
    }
}

/// <summary>
/// The pieces of a text made of another's runs, put in order: a run that goes on where the last ended joined to it, an
/// empty one left out, so a text written line for line holds a piece or two a line, never one a character.
/// </summary>
public sealed class CodeTextPieces
{
    private readonly List<CodeTextPiece> _pieces = [];

    /// <summary>The pieces put so far.</summary>
    public IReadOnlyList<CodeTextPiece> Pieces => _pieces;

    /// <summary>Puts the run from <paramref name="start"/> to before <paramref name="end"/>.</summary>
    /// <param name="start">Where it begins.</param>
    /// <param name="end">Where it ends.</param>
    public CodeTextPieces Run(long start, long end)
    {
        if (end <= start)
        {
            return this;
        }

        if (_pieces.Count > 0 && _pieces[^1] is { Written: null, Repeated: '\0', Blank: false } last && last.End == start)
        {
            _pieces[^1] = last with { End = end };
        }
        else
        {
            _pieces.Add(CodeTextPiece.Run(start, end));
        }

        return this;
    }

    /// <summary>Puts the string <paramref name="written"/>.</summary>
    /// <param name="written">What is written.</param>
    public CodeTextPieces Write(string written)
    {
        ArgumentNullException.ThrowIfNull(written);

        if (written.Length > 0)
        {
            _pieces.Add(CodeTextPiece.Of(written));
        }

        return this;
    }

    /// <summary>Puts <paramref name="character"/>, <paramref name="count"/> times.</summary>
    /// <param name="character">The character.</param>
    /// <param name="count">How many times.</param>
    public CodeTextPieces Write(char character, long count)
    {
        if (count > 0)
        {
            _pieces.Add(CodeTextPiece.Repeat(character, count));
        }

        return this;
    }

    /// <summary>Puts the run from <paramref name="start"/> to before <paramref name="end"/> blank: every character a space but a line feed.</summary>
    /// <param name="start">Where it begins.</param>
    /// <param name="end">Where it ends.</param>
    public CodeTextPieces Blank(long start, long end)
    {
        if (end > start)
        {
            _pieces.Add(CodeTextPiece.Blanked(start, end));
        }

        return this;
    }
}

/// <summary>
/// A file read again in the course of its reading that no longer reads as it did - shorter, or its bytes decoding otherwise:
/// it changed while it was read, and nothing is made of what was read of it. The core answers it for the file, never the
/// implementation reading it.
/// </summary>
public sealed class CodeTextChangedException : IOException
{
    /// <summary>A refusal of <paramref name="path"/>, which changed while it was read.</summary>
    /// <param name="path">The file.</param>
    /// <param name="inner">What reading it again met, where it threw.</param>
    public CodeTextChangedException(string path, Exception? inner = null)
        : base($"'{path}' changed while it was read", inner)
    {
    }
}
