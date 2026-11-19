using System.Security.Cryptography;
using System.Text;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// What one pass over a text file read of it for an edit (<see cref="AgentEdits.ScanAsync"/>): the digest of its bytes, its
/// lines as an edit counts them (none for an empty text), the one ending its lines have - null where they have several
/// kinds, or none ends - and the endings of the lines an insertion is decided by: those either side of the line it goes
/// before, and the file's last two. An ending is empty for a line that has none - a last line with no newline - and null
/// for a line the file does not hold.
/// </summary>
/// <param name="Digest">The digest of the file's bytes (<see cref="FileContentHash"/>).</param>
/// <param name="Lines">How many lines the file holds, as an edit counts them.</param>
/// <param name="Ending">The one ending every line that ends has; null where they have several kinds, or none ends.</param>
/// <param name="Before">The ending of the line before the one asked about; null where there is none.</param>
/// <param name="At">The ending of the line asked about; null where the file holds no such line.</param>
/// <param name="LastButOne">The ending of the file's last line but one; null where it holds fewer than two.</param>
/// <param name="Last">The ending of the file's last line; null where it holds none.</param>
internal sealed record EditScan(string Digest, long Lines, string? Ending, string? Before, string? At, string? LastButOne, string? Last);

/// <summary>What an edit copied through wrote: how many times the text replaced stood in the file, and the digest and the lines of what was written.</summary>
/// <param name="Found">How many times the text sought stood in the file, without overlap; none for an insertion.</param>
/// <param name="Digest">The digest of the bytes written (<see cref="FileContentHash"/>): the file as the edit leaves it.</param>
/// <param name="Lines">How many lines the file holds now, as an edit counts them.</param>
internal sealed record EditCopy(int Found, string Digest, long Lines);

/// <summary>The file an edit copies through changed between the pass that read it and the pass that copies it: nothing was written.</summary>
internal sealed class EditMovedException() : IOException("the file changed while it was edited, and nothing was written");

/// <summary>
/// The edits of <c>replace_in_file</c> and <c>insert_in_file</c>, copied through: a file is read once whole for what decides
/// its edit (<see cref="ScanAsync"/>), then copied a block at a time into the file that replaces it, the edit made on the
/// way, its encoding and its mark kept, the bytes it is copied from held to the digest the first pass read - so a file of
/// any size is edited in a block of memory, and never as it stood after it was read. What an edit does to a file's text is
/// <see cref="EditedText"/>'s rule alone: its line endings - text given written with the file's own ending where it holds
/// one kind (<see cref="EditedText.InFileEndings"/>), the lines an insertion gives and how the last of them ends
/// (<see cref="EditedText.Insert"/>) - asked of it with the lines about the edit that decide them.
/// </summary>
internal static class AgentEdits
{
    /// <summary>How many characters are copied at a time.</summary>
    private const int Block = 64 * 1024;

    /// <summary>
    /// Reads <paramref name="stream"/> whole, a block at a time: the digest of its bytes, its lines and their endings, and
    /// the endings about the line <paramref name="line"/> where one is asked about.
    /// </summary>
    /// <param name="stream">The file's bytes, from its start; its caller owns it.</param>
    /// <param name="path">The file, as a refusal names it.</param>
    /// <param name="line">The line an insertion goes before, from 1; null for a replace.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="StreamedTextUnreadException">The file is no text this tool reads, with why.</exception>
    public static async Task<EditScan> ScanAsync(Stream stream, string path, long? line, CancellationToken cancellationToken)
    {
        using var text = await StreamedText.OpenAsync(stream, path, IncrementalHash.CreateHash(HashAlgorithmName.SHA256), cancellationToken).ConfigureAwait(false);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        string? before = null;
        string? at = null;
        string? lastButOne = null;
        string? last = null;

        var lines = new LineCount((number, ending) =>
        {
            if (ending.Length > 0)
            {
                kinds.Add(ending);
            }

            if (line is { } asked)
            {
                before = number == asked - 1 ? ending : before;
                at = number == asked ? ending : at;
            }

            (lastButOne, last) = (last, ending);
        });

        var chunk = new char[Block];

        while (await text.ReadAsync(chunk, cancellationToken).ConfigureAwait(false) is var count and > 0)
        {
            lines.Add(chunk.AsSpan(0, count));
        }

        lines.End();

        return new EditScan(text.Digest(), lines.Count, kinds.Count == 1 ? kinds.Single() : null, before, at, lastButOne, last);
    }

    /// <summary>
    /// Copies the file <paramref name="source"/> reads into <paramref name="written"/>, every occurrence of
    /// <paramref name="old"/> - written as the file writes its endings - replaced by <paramref name="replacement"/> - written
    /// so too - without overlap, from the start: what <see cref="EditedText.Replace"/> makes of the file, never holding it.
    /// How many there were is said, and the caller keeps or drops what was written by it.
    /// </summary>
    /// <param name="source">The file's bytes, from its start.</param>
    /// <param name="path">The file, as a refusal names it.</param>
    /// <param name="written">Where the file that replaces it is written.</param>
    /// <param name="scan">What the first pass read of the file.</param>
    /// <param name="old">The text replaced; never empty.</param>
    /// <param name="replacement">What replaces it.</param>
    /// <exception cref="EditMovedException">The file's bytes are not those the first pass read.</exception>
    public static EditCopy Replace(Stream source, string path, Stream written, EditScan scan, string old, string replacement)
    {
        var endings = InEndingsOf(scan.Ending);
        var sought = endings.InFileEndings(old);
        var given = endings.InFileEndings(replacement);

        using var text = StreamedText.Open(source, path, IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
        var output = new Written(written, text.Encoding, text.Mark);
        var window = new char[Block + sought.Length];
        var carried = 0;
        var found = 0;

        while (text.Read(window.AsSpan(carried, Block)) is var count and > 0)
        {
            var held = window.AsSpan(0, carried + count);
            var at = 0;

            for (var next = held.IndexOf(sought, StringComparison.Ordinal); next >= 0; next = held[at..].IndexOf(sought, StringComparison.Ordinal))
            {
                next += at;
                output.Write(held[at..next]);
                output.Write(given);
                found++;
                at = next + sought.Length;

                if (at >= held.Length)
                {
                    break;
                }
            }

            // What could begin an occurrence the next block ends is carried into it; everything before is written.
            var keep = Math.Max(at, held.Length - (sought.Length - 1));
            output.Write(held[at..keep]);
            held[keep..].CopyTo(window);
            carried = held.Length - keep;
        }

        output.Write(window.AsSpan(0, carried));

        return Ended(text, scan, output, found);
    }

    /// <summary>
    /// Copies the file <paramref name="source"/> reads into <paramref name="written"/> with <paramref name="lines"/> inserted
    /// as lines before line <paramref name="line"/> - one past the last appending - as <see cref="EditedText.Insert"/>
    /// inserts them: never holding the file.
    /// </summary>
    /// <param name="source">The file's bytes, from its start.</param>
    /// <param name="path">The file, as a refusal names it.</param>
    /// <param name="written">Where the file that replaces it is written.</param>
    /// <param name="scan">What the first pass read of the file, the endings about <paramref name="line"/> among it.</param>
    /// <param name="line">The line the text goes before, from 1, one of the file's or one past its last.</param>
    /// <param name="lines">The text to insert as lines.</param>
    /// <exception cref="EditMovedException">The file's bytes are not those the first pass read.</exception>
    public static EditCopy Insert(Stream source, string path, Stream written, EditScan scan, long line, string lines)
    {
        var inserted = Inserted(scan, line, lines);

        using var text = StreamedText.Open(source, path, IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
        var output = new Written(written, text.Encoding, text.Mark);
        var chunk = new char[Block];
        var done = false;

        // The lines before the insertion are copied as they end: it goes in once line - 1 of them have ended, a carriage
        // return's line ending only where the character after it is no line feed.
        long ended = 0;
        var afterCarriageReturn = false;

        void InsertHere()
        {
            if (!done && ended == line - 1)
            {
                output.Write(inserted);
                done = true;
            }
        }

        InsertHere();

        while (text.Read(chunk) is var count and > 0)
        {
            var rest = chunk.AsSpan(0, count);

            while (!done && !rest.IsEmpty)
            {
                if (afterCarriageReturn)
                {
                    afterCarriageReturn = false;
                    ended++;

                    if (rest[0] == '\n')
                    {
                        output.Write(rest[..1]);
                        rest = rest[1..];
                    }

                    InsertHere();
                    continue;
                }

                var ending = Text.LineEnds.IndexOf(rest);

                if (ending < 0)
                {
                    output.Write(rest);
                    rest = [];
                    break;
                }

                output.Write(rest[..(ending + 1)]);

                if (rest[ending] == '\r')
                {
                    afterCarriageReturn = true;
                }
                else
                {
                    ended++;
                    InsertHere();
                }

                rest = rest[(ending + 1)..];
            }

            output.Write(rest);
        }

        if (afterCarriageReturn)
        {
            ended++;
        }

        InsertHere();

        // Appended past a last line, the text goes in at the end, as the scan counted the lines.
        if (!done)
        {
            output.Write(inserted);
        }

        return Ended(text, scan, output, 0);
    }

    /// <summary>
    /// What an insertion of <paramref name="lines"/> before line <paramref name="line"/> writes at the place it goes in, as
    /// <see cref="EditedText.Insert"/> decides it: asked of a text of the lines about the place that decide it - each line's
    /// text no matter, its ending the file's - whose one ending, and whose nearest ending to the place, are the file's; what
    /// that text gains there, an ending added to a last line that had none among it, is what the file gains there.
    /// </summary>
    private static string Inserted(EditScan scan, long line, string lines)
    {
        List<string> about;
        int place;

        if (scan.Lines == 0)
        {
            (about, place) = ([], 1);
        }
        else if (line <= scan.Lines && line >= 2)
        {
            (about, place) = ([scan.Before!, scan.At!], 2);
        }
        else if (line <= scan.Lines)
        {
            (about, place) = ([scan.At!], 1);
        }
        else if (scan.Lines >= 2)
        {
            (about, place) = ([scan.LastButOne!, scan.Last!], 3);
        }
        else
        {
            (about, place) = ([scan.Last!], 2);
        }

        var asked = EditedText.Of(new EncodedText(string.Concat(about.Select(ending => "x" + ending)), TextEncoding.Utf8));
        var prefix = string.Concat(about.Take(place - 1).Select(ending => "x" + ending)).Length;
        var suffix = string.Concat(about.Skip(place - 1).Select(ending => "x" + ending)).Length;

        asked.Insert(place, lines);

        var text = asked.Text;

        return text[prefix..(text.Length - suffix)];
    }

    /// <summary>An edited text whose one ending is <paramref name="ending"/> - none where it is null - so text given is written in the file's endings by its rule.</summary>
    private static EditedText InEndingsOf(string? ending)
        => EditedText.Of(new EncodedText(ending is null ? string.Empty : "x" + ending, TextEncoding.Utf8));

    /// <summary>What a copy came to, once the file it was copied from is read to its end and held to the digest the first pass read.</summary>
    private static EditCopy Ended(StreamedText text, EditScan scan, Written output, int found)
    {
        if (!string.Equals(text.Digest(), scan.Digest, StringComparison.Ordinal))
        {
            throw new EditMovedException();
        }

        var (digest, lines) = output.End();

        return new EditCopy(found, digest, lines);
    }

    /// <summary>
    /// How many lines a text holds, as an edit counts them - none for an empty text, each ending closing its line, and a
    /// last line with no ending one more - each line's ending told as it ends: CR LF whole, whatever block parts it.
    /// </summary>
    private sealed class LineCount(Action<long, string>? ended = null)
    {
        private long _ended;
        private bool _open;
        private bool _afterCarriageReturn;

        /// <summary>How many lines the text holds so far.</summary>
        public long Count => _ended + (_open ? 1 : 0);

        /// <summary>Reads the next characters of the text.</summary>
        public void Add(ReadOnlySpan<char> chars)
        {
            while (!chars.IsEmpty)
            {
                if (_afterCarriageReturn)
                {
                    _afterCarriageReturn = false;

                    if (chars[0] == '\n')
                    {
                        End("\r\n");
                        chars = chars[1..];
                        continue;
                    }

                    End("\r");
                }

                var ending = Text.LineEnds.IndexOf(chars);

                if (ending < 0)
                {
                    _open = true;
                    return;
                }

                _open = true;

                if (chars[ending] == '\r')
                {
                    _afterCarriageReturn = true;
                }
                else
                {
                    End("\n");
                }

                chars = chars[(ending + 1)..];
            }
        }

        /// <summary>Ends the text: a carriage return it ends with ends its line, and a last line with no ending is told so.</summary>
        public void End()
        {
            if (_afterCarriageReturn)
            {
                _afterCarriageReturn = false;
                End("\r");
            }

            if (_open)
            {
                ended?.Invoke(_ended + 1, string.Empty);
            }
        }

        private void End(string ending)
        {
            _ended++;
            _open = false;
            ended?.Invoke(_ended, ending);
        }
    }

    /// <summary>
    /// The file an edit writes: its mark first, as the file it replaces had it, then its characters in that file's encoding,
    /// strictly, a block at a time - every byte taken into the digest of what is written, and its lines counted.
    /// </summary>
    private sealed class Written
    {
        private readonly Stream _stream;
        private readonly Encoder _encoder;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly LineCount _lines = new();
        private readonly byte[] _bytes;

        public Written(Stream stream, Encoding encoding, byte[] mark)
        {
            _stream = stream;
            _encoder = encoding.GetEncoder();
            _bytes = new byte[encoding.GetMaxByteCount(Block)];
            _stream.Write(mark);
            _hash.AppendData(mark);
        }

        /// <summary>Writes <paramref name="chars"/>.</summary>
        public void Write(ReadOnlySpan<char> chars)
        {
            _lines.Add(chars);
            Encoded(chars, flush: false);
        }

        /// <summary>Writes <paramref name="text"/>.</summary>
        public void Write(string text) => Write(text.AsSpan());

        /// <summary>Ends the file: the digest of every byte written, and its lines.</summary>
        public (string Digest, long Lines) End()
        {
            Encoded([], flush: true);
            _lines.End();

            using (_hash)
            {
                return (Convert.ToHexStringLower(_hash.GetHashAndReset()), _lines.Count);
            }
        }

        private void Encoded(ReadOnlySpan<char> chars, bool flush)
        {
            var completed = false;

            while (!completed)
            {
                _encoder.Convert(chars, _bytes, flush, out var charsUsed, out var bytesUsed, out completed);
                _stream.Write(_bytes, 0, bytesUsed);
                _hash.AppendData(_bytes, 0, bytesUsed);
                chars = chars[charsUsed..];
            }
        }
    }
}
