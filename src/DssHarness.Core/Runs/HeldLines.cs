using System.Text;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Runs;

/// <summary>
/// The lines a host's command printed from its last failure line on, held until the command has said whether that failure
/// is how it ended: shown then, in order, where it was not, and read whole as the failure where it was. However many
/// lines follow the failure line - a run of this tool inside a test suite prints one, and under --verbose every line of
/// every step follows it - every one is held, and none in this process's memory past a batch: they go to a file of the
/// command's own as they come, and only where that file is is kept here.
/// </summary>
/// <remarks>
/// Fed from one stream's reader, a line at a time, as every stream of a command is read. The file is made the first time
/// a batch is written, so a failure of a few lines never makes one, and it goes with the lines it held.
/// </remarks>
/// <param name="fileSystem">Where the held lines are written past a batch.</param>
/// <param name="show">Shows a line, as it would have been shown had it never been held.</param>
/// <param name="warn">Says, for the command, that the lines could not be held or read back, and why.</param>
internal sealed class HeldLines(IFileSystem fileSystem, Action<string> show, Action<string> warn) : IDisposable
{
    /// <summary>How many bytes of lines are kept in memory before they are written to the file: a batch, never a bound.</summary>
    internal const int Batch = 64 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly Action<string> _show = show;
    private readonly Action<string> _warn = warn;
    private readonly MemoryStream _batch = new();

    private ScratchDirectory? _scratch;
    private IAppendFile? _file;
    private string? _line;
    private string? _said;

    /// <summary>Whether a failure line is held.</summary>
    public bool Any => _line is not null;

    /// <summary>What the failure line held said, its first line: the cause of the failure; <see langword="null"/> where none is held.</summary>
    public string? Cause => _said;

    /// <summary>
    /// Shows what was held, in order, and holds <paramref name="line"/> from now on: a failure line, which said
    /// <paramref name="said"/>. One failure line followed by another is no conclusion of the command's, which ends on its
    /// last.
    /// </summary>
    /// <param name="line">The failure line, as it would be shown.</param>
    /// <param name="said">What it said.</param>
    public void Begin(string line, string said)
    {
        Show();
        (_line, _said) = (line, said);
    }

    /// <summary>Holds <paramref name="line"/>, after what is held; a line with no failure line before it is shown at once.</summary>
    /// <param name="line">The line, as it would be shown.</param>
    public void Add(string line)
    {
        if (!Any)
        {
            _show(line);
            return;
        }

        var bytes = Utf8.GetBytes(line + "\n");
        _batch.Write(bytes);

        if (_batch.Length < Batch)
        {
            return;
        }

        try
        {
            File().AppendFlushed(_batch.GetBuffer().AsSpan(0, checked((int)_batch.Length)));
            _batch.SetLength(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never lost: what could not be held is shown now, in order, and what follows as it comes - and the failure
            // it followed is then not read from it.
            _warn($"the lines a host printed after its failure line could not be held at '{_scratch?.Path}': {ex.Message.TrimEnd('.')}; "
                + "they are shown now, and the failure is not read from them");
            Show();
        }
    }

    /// <summary>Shows each line held, in order, and holds none after.</summary>
    public void Show()
    {
        if (!Any)
        {
            return;
        }

        _show(_line!);
        Following(_show);
        Clear();
    }

    /// <summary>
    /// What the failure held said, and every line after it, one to a line: the whole of it, however long; <see langword="null"/>
    /// where none is held. Read from what is held, which it leaves held.
    /// </summary>
    public string? Failure()
    {
        if (!Any)
        {
            return null;
        }

        var failure = new StringBuilder(_said);

        Following(line => failure.Append(Environment.NewLine).Append(line));

        return failure.ToString();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _file?.Dispose();
        _scratch?.Dispose();
        _batch.Dispose();
    }

    /// <summary>The file the held lines are written to, made the first time a batch is.</summary>
    private IAppendFile File()
    {
        if (_file is not null)
        {
            return _file;
        }

        _scratch ??= new ScratchDirectory(_fileSystem, "held", _warn);
        _fileSystem.CreateDirectory(_scratch.Path);

        return _file = _fileSystem.OpenForAppend(Path.Combine(_scratch.Path, "lines"));
    }

    /// <summary>Gives each line held after the failure line to <paramref name="line"/>, in order: those in the file, then those of the batch.</summary>
    private void Following(Action<string> line)
    {
        var decoder = Utf8.GetDecoder();
        var whole = new StringBuilder();
        var bytes = new byte[Batch];
        var chars = new char[Utf8.GetMaxCharCount(Batch)];

        try
        {
            for (long at = 0; _file is not null && at < _file.Length;)
            {
                var count = (int)Math.Min(Batch, _file.Length - at);

                _file.ReadExactlyAt(at, bytes.AsSpan(0, count));
                Lines(bytes, count);
                at += count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn($"the lines a host printed after its failure line could not be read back from '{_scratch?.Path}': {ex.Message.TrimEnd('.')}");
        }

        Lines(_batch.GetBuffer(), checked((int)_batch.Length));

        // Each line held ends in its newline, so the bytes end on a whole line.
        void Lines(byte[] read, int count)
        {
            var decoded = decoder.GetChars(read, 0, count, chars, 0, flush: false);
            var from = 0;

            for (var at = 0; at < decoded; at++)
            {
                if (chars[at] != '\n')
                {
                    continue;
                }

                whole.Append(chars, from, at - from);
                line(whole.ToString());
                whole.Clear();
                from = at + 1;
            }

            whole.Append(chars, from, decoded - from);
        }
    }

    /// <summary>Holds nothing: the failure line, the batch and the file emptied.</summary>
    private void Clear()
    {
        (_line, _said) = (null, null);
        _batch.SetLength(0);

        try
        {
            _file?.TruncateTo(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be emptied is let go, and the next batch held in a file of its own.
            _warn($"'{_scratch?.Path}' could not be emptied: {ex.Message.TrimEnd('.')}");
            _file?.Dispose();
            _file = null;
            _scratch?.Dispose();
            _scratch = null;
        }
    }
}
