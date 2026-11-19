using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Pages;
using DssHarness.Core.Text;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// A text file's characters read in one pass, a block at a time, and never held whole: its encoding read from its first
/// bytes by the rule a text's pages are laid by (<see cref="TextPages.EncodingOf"/>) - UTF-16 by its mark, binary by a NUL
/// among its first <see cref="BinaryContent.Window"/> bytes, UTF-8 with or without its mark - and every byte after its mark
/// decoded strictly, a byte no character is made of refused in <see cref="EncodedText"/>'s words, where it stands. Every
/// byte read, its mark among them, is taken into a digest where one is asked for, so the digest of a whole read is the
/// file's (<see cref="FileContentHash"/>). What <c>search_files</c> reads a file's lines by, and what an edit copies a file
/// through by, so a file of any size costs a block of memory and the longest of its lines.
/// </summary>
internal sealed class StreamedText : IDisposable
{
    /// <summary>How many bytes are read at a time.</summary>
    private const int Block = 64 * 1024;

    private readonly Stream _stream;
    private readonly string _path;
    private readonly IncrementalHash? _hash;
    private readonly Decoder _decoder;
    private readonly bool _utf16;
    private readonly byte[] _block = new byte[Block];
    private readonly char[] _chars;
    private byte[]? _head;
    private int _headFrom;
    private int _headLength;
    private int _start;
    private int _end;
    private bool _drained;

    /// <summary>How many of the bytes after the mark were handed to the decoder before the block being decoded: where a byte that does not decode stands.</summary>
    private long _decoded;

    private StreamedText(Stream stream, string path, IncrementalHash? hash, Encoding encoding, byte[] head, int preamble, int read)
    {
        _stream = stream;
        _path = path;
        _hash = hash;
        Encoding = encoding;
        Mark = head[..preamble];
        _decoder = encoding.GetDecoder();
        _utf16 = encoding is UnicodeEncoding;
        _chars = new char[encoding.GetMaxCharCount(Math.Max(Block, head.Length)) + 4];
        _head = head;
        _headFrom = preamble;
        _headLength = read;
    }

    /// <summary>The strict encoding the text is in, with no mark of its own: what an edit writes it back in, after <see cref="Mark"/>.</summary>
    public Encoding Encoding { get; }

    /// <summary>The bytes of the text's mark, as its file holds them; none where it has none.</summary>
    public byte[] Mark { get; }

    /// <summary>The digest of every byte read so far, its mark among them: the file's (<see cref="FileContentHash"/>) once it is read to its end.</summary>
    public string Digest() => _hash is null ? throw new InvalidOperationException("no digest was asked of the text") : Convert.ToHexStringLower(_hash.GetCurrentHash());

    /// <summary>The text of <paramref name="stream"/>, from its first byte, its encoding read from its first bytes, read without waiting.</summary>
    /// <param name="stream">The file's bytes, from its start. It is read, never closed: its caller owns it.</param>
    /// <param name="path">The file, as a refusal names it.</param>
    /// <param name="hash">Takes every byte read into a digest; null where none is asked for.</param>
    /// <exception cref="StreamedTextUnreadException">It is binary: a NUL among its first bytes, where it is no UTF-16 by its mark.</exception>
    public static StreamedText Open(Stream stream, string path, IncrementalHash? hash)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(path);

        var head = new byte[BinaryContent.Window];
        var read = 0;

        while (read < head.Length && stream.Read(head, read, head.Length - read) is var got and > 0)
        {
            read += got;
        }

        return Opened(stream, path, hash, head, read);
    }

    /// <summary>The text of <paramref name="stream"/>, as <see cref="Open"/> reads it, read as its bytes come.</summary>
    /// <param name="stream">The file's bytes, from its start. It is read, never closed: its caller owns it.</param>
    /// <param name="path">The file, as a refusal names it.</param>
    /// <param name="hash">Takes every byte read into a digest; null where none is asked for.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="StreamedTextUnreadException">It is binary: a NUL among its first bytes, where it is no UTF-16 by its mark.</exception>
    public static async Task<StreamedText> OpenAsync(Stream stream, string path, IncrementalHash? hash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(path);

        var head = new byte[BinaryContent.Window];
        var read = 0;

        while (read < head.Length && await stream.ReadAsync(head.AsMemory(read), cancellationToken).ConfigureAwait(false) is var got and > 0)
        {
            read += got;
        }

        return Opened(stream, path, hash, head, read);
    }

    /// <summary>Lets go of the digest; the stream is its caller's.</summary>
    public void Dispose() => _hash?.Dispose();

    /// <summary>The next characters of the text, into <paramref name="buffer"/>: how many; none once the text has ended.</summary>
    /// <param name="buffer">Where they go.</param>
    /// <exception cref="StreamedTextUnreadException">A byte forms no character of the text's encoding, named where it stands.</exception>
    public int Read(Span<char> buffer)
    {
        while (_start == _end && !_drained)
        {
            Next(_head is null ? _stream.Read(_block, 0, _block.Length) : -1);
        }

        return Take(buffer);
    }

    /// <summary>The next characters of the text, as <see cref="Read"/> gives them, read as its bytes come.</summary>
    /// <param name="buffer">Where they go.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="StreamedTextUnreadException">A byte forms no character of the text's encoding, named where it stands.</exception>
    public async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken)
    {
        while (_start == _end && !_drained)
        {
            Next(_head is null ? await _stream.ReadAsync(_block, cancellationToken).ConfigureAwait(false) : -1);
        }

        return Take(buffer.Span);
    }

    /// <summary>
    /// The text's lines, in order, without their endings, as the one rule of where a line ends cuts them
    /// (<see cref="LineEnds"/>): at CR LF, LF and CR, an ending the text ends with closing its last line rather than
    /// opening another, and an empty text one empty line. One line is held at a time.
    /// </summary>
    /// <param name="cancellationToken">Stops the read, between one block and the next.</param>
    /// <exception cref="StreamedTextUnreadException">A byte forms no character of the text's encoding, named where it stands.</exception>
    public async IAsyncEnumerable<string> LinesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chunk = new char[Block];
        var line = new StringBuilder();
        var given = false;
        var afterCarriageReturn = false;

        while (await ReadAsync(chunk, cancellationToken).ConfigureAwait(false) is var count and > 0)
        {
            var rest = chunk.AsMemory(0, count);

            while (!rest.IsEmpty)
            {
                // A line feed straight after a carriage return is the second half of one ending, whatever block it is in.
                if (afterCarriageReturn)
                {
                    afterCarriageReturn = false;

                    if (rest.Span[0] == '\n')
                    {
                        rest = rest[1..];
                        continue;
                    }
                }

                var ending = LineEnds.IndexOf(rest.Span);

                if (ending < 0)
                {
                    line.Append(rest.Span);
                    break;
                }

                line.Append(rest.Span[..ending]);
                afterCarriageReturn = rest.Span[ending] == '\r';
                rest = rest[(ending + 1)..];
                given = true;

                yield return line.ToString();
                line.Clear();
            }
        }

        if (line.Length > 0 || !given)
        {
            yield return line.ToString();
        }
    }

    /// <summary>The text of a stream whose first <paramref name="read"/> bytes are <paramref name="head"/>: its encoding read from them, and binary refused.</summary>
    private static StreamedText Opened(Stream stream, string path, IncrementalHash? hash, byte[] head, int read)
    {
        var (encoding, preamble) = TextPages.EncodingOf(head.AsSpan(0, Math.Min(read, 4)));

        hash?.AppendData(head, 0, read);

        if (encoding is not UnicodeEncoding && BinaryContent.Holds(head.AsSpan(0, read)))
        {
            throw new StreamedTextUnreadException(path, binary: true, EncodedText.BinaryWhy);
        }

        return new StreamedText(stream, path, hash, encoding, head, preamble, read);
    }

    /// <summary>Hands the characters decoded and not yet taken to <paramref name="buffer"/>: how many.</summary>
    private int Take(Span<char> buffer)
    {
        var count = Math.Min(buffer.Length, _end - _start);

        _chars.AsSpan(_start, count).CopyTo(buffer);
        _start += count;
        return count;
    }

    /// <summary>
    /// Decodes the next bytes: the first bytes, after the mark, where they were not yet decoded, else the <paramref name="read"/>
    /// bytes of the block just read - the decoder flushed where the stream has ended.
    /// </summary>
    private void Next(int read)
    {
        ReadOnlySpan<byte> bytes;
        bool flush;

        if (_head is { } head)
        {
            bytes = head.AsSpan(_headFrom, _headLength - _headFrom);
            flush = _headLength < head.Length;
            _head = null;
        }
        else
        {
            _hash?.AppendData(_block, 0, read);
            bytes = _block.AsSpan(0, read);
            flush = read == 0;
        }

        _start = 0;
        _end = 0;
        var used = 0;

        try
        {
            while (used < bytes.Length || flush)
            {
                _decoder.Convert(bytes[used..], _chars.AsSpan(_end), flush, out var bytesUsed, out var charsUsed, out var completed);
                used += bytesUsed;
                _end += charsUsed;

                if (completed && used >= bytes.Length)
                {
                    break;
                }
            }
        }
        catch (DecoderFallbackException ex)
        {
            // Where the byte stands is counted from the first after the mark, as a whole text's reading counts it.
            throw new StreamedTextUnreadException(_path, binary: false, _utf16
                ? EncodedText.NotUtf16Why
                : EncodedText.NotUtf8Why(Utf8Text.NotUtf8(_decoded + used + ex.Index, ex.BytesUnknown?.FirstOrDefault() ?? 0)));
        }

        _decoded += bytes.Length;
        _drained = flush;
    }
}

/// <summary>
/// A file a streamed read of its text stopped at (<see cref="StreamedText"/>): binary by its first bytes, or holding bytes
/// that form no character of its encoding - said as a file read whole is said (<see cref="EncodedText"/>), with where.
/// </summary>
/// <param name="path">The file.</param>
/// <param name="binary">Whether it is binary by its first bytes.</param>
/// <param name="why">Why it is no text this tool reads, as what follows its name.</param>
internal sealed class StreamedTextUnreadException(string path, bool binary, string why)
    : IOException($"'{path}' is {(binary ? "binary" : "no text this tool reads")}: {why}")
{
    /// <summary>Whether the file is binary by its first bytes.</summary>
    public bool Binary { get; } = binary;

    /// <summary>Why it is no text this tool reads, as what follows its name.</summary>
    public string Why { get; } = why;
}
