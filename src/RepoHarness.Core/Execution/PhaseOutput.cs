using System.Text;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Execution;

/// <summary>
/// What a phase printed could not be read back whole from its log: the log is gone or cannot be opened, was cut short, or
/// was written again since the phase left it. What is left of it settles nothing, so nothing is decided on it: work a
/// verdict hangs on ends unmeasured, saying this, and a reader that only measures leaves its measure out.
/// </summary>
/// <param name="logFile">The log.</param>
/// <param name="why">Why it could not be read back, as a line ends with it.</param>
/// <param name="innerException">What reading it raised, where something did.</param>
public sealed class PhaseOutputUnreadException(string logFile, string why, Exception? innerException = null)
    : Exception($"what the phase printed could not be read back from its log '{logFile}': {why}", innerException)
{
    /// <summary>The log.</summary>
    public string LogFile { get; } = logFile;
}

/// <summary>
/// What a phase's child printed: its lines, both streams in the order they came, with the run's secrets masked - read
/// back from the phase's log each time they are asked for, never held.
/// </summary>
/// <remarks>
/// <para>
/// The log is where a child's output lives. What the harness holds of it in memory is bounded whatever the child prints:
/// whether the success pattern matched, the timing marks, the last lines. Everything that reads more - ninja's last word
/// on a build, ctest's count and its summary, the exception a failing step printed, the marker a run check looks for -
/// reads it from here, a line at a time. Holding it as text cannot work for a child that prints gigabytes: no string
/// holds more than about a billion characters, so the harness died out of memory however much the machine had free.
/// </para>
/// <para>
/// Only the child's own lines are read: the header the harness wrote above them, which echoes the command line and would
/// witness a pattern the command contains, and the exit line below them are not part of it. Each line reads as the log
/// keeps it - a line longer than <see cref="ProcessRunner.LongestLine"/> characters as pieces of at most that many, each a
/// line of its own, as the phase that wrote it read it.
/// </para>
/// <para>
/// And only from a log that still holds them: one that is gone, cut short, or written again since - held to its length
/// and to the exit line the phase closed its child's lines with - is said as unread
/// (<see cref="PhaseOutputUnreadException"/>) before any line is handed on, and never read as what is there.
/// </para>
/// </remarks>
public abstract class PhaseOutput
{
    /// <summary>How many bytes of a log are read at a time.</summary>
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>How the log is written, and so read: UTF-8, without a byte order mark.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Output with no line in it.</summary>
    public static PhaseOutput Empty { get; } = new Text(string.Empty);

    /// <summary>
    /// Each line, in order, from the first that was printed. Read afresh on every call, from the log for a phase that ran;
    /// stopping early reads no further.
    /// </summary>
    public abstract IEnumerable<string> Lines();

    /// <summary>
    /// <paramref name="text"/> as a child that printed it would have it read: for a phase made up rather than run, and the
    /// tests of what reads one.
    /// </summary>
    /// <param name="text">What the child printed, its lines ended however they were.</param>
    public static PhaseOutput Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new Text(text);
    }

    /// <summary>The lines of each of <paramref name="outputs"/>, one after another: what several phases printed, in the order they ran.</summary>
    /// <param name="outputs">The phases' outputs.</param>
    public static PhaseOutput Joined(IEnumerable<PhaseOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        return new Concatenated([.. outputs]);
    }

    /// <summary>
    /// The lines between byte <paramref name="start"/> and byte <paramref name="end"/> of <paramref name="logFile"/>: where a
    /// phase's child wrote, below the header and above the exit line.
    /// </summary>
    /// <param name="logFile">The phase's log.</param>
    /// <param name="start">Where the child's first line begins.</param>
    /// <param name="end">Where the line after the child's last one begins.</param>
    /// <param name="closes">
    /// The bytes the phase wrote directly after its child's last line - its exit line, which says to the tick how long it
    /// ran - or none where nothing says. The log is held to them before any line of the range is handed on, so a log
    /// written again since is never read as this phase's, by a reader that stops early no more than by one that reads on.
    /// </param>
    internal static PhaseOutput InLog(string logFile, long start, long end, ReadOnlyMemory<byte> closes = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFile);

        return new LogRange(logFile, start, end, closes);
    }

    /// <summary>Text held in memory, read as the process runner reads a child's stream.</summary>
    private sealed class Text(string text) : PhaseOutput
    {
        public override IEnumerable<string> Lines()
        {
            var lines = new List<string>();
            var splitter = new LineSplitter(lines.Add, ProcessRunner.LongestLine);

            splitter.Add(text);
            splitter.End();

            return lines;
        }
    }

    /// <summary>Several outputs, one after another.</summary>
    private sealed class Concatenated(IReadOnlyList<PhaseOutput> parts) : PhaseOutput
    {
        public override IEnumerable<string> Lines() => parts.SelectMany(part => part.Lines());
    }

    /// <summary>A range of a log's bytes, read only from a log that still holds what its phase left in it.</summary>
    private sealed class LogRange(string logFile, long start, long end, ReadOnlyMemory<byte> closes) : PhaseOutput
    {
        /// <exception cref="PhaseOutputUnreadException">The log no longer holds the range: said as the first line is asked for.</exception>
        public override IEnumerable<string> Lines()
        {
            var lines = new Queue<string>();

            // Every line ends as the phase runner ended it: a carriage return before the line feed is that ending only where
            // the runner writes one, and otherwise the line's own.
            var splitter = new LineSplitter(
                lines.Enqueue,
                ProcessRunner.LongestLine,
                carriageReturnEnds: string.Equals(PhaseRunner.LogLineEnding, "\r\n", StringComparison.Ordinal));

            using var log = Open();

            var decoder = Utf8NoBom.GetDecoder();
            var bytes = new byte[ReadBufferSize];
            var chars = new char[Utf8NoBom.GetMaxCharCount(ReadBufferSize)];

            for (var remaining = end - start; remaining > 0;)
            {
                var read = Read(log, bytes, (int)Math.Min(bytes.Length, remaining));

                remaining -= read;
                splitter.Add(chars.AsSpan(0, decoder.GetChars(bytes, 0, read, chars, 0, flush: remaining == 0)));

                while (lines.TryDequeue(out var line))
                {
                    yield return line;
                }
            }

            splitter.End();

            while (lines.TryDequeue(out var line))
            {
                yield return line;
            }
        }

        /// <summary>
        /// The log, opened where the range begins once it is seen to hold what the phase left: as many bytes as the phase
        /// wrote up to the end of what closed its child's lines, and those very bytes there.
        /// </summary>
        private FileStream Open()
        {
            FileStream log;

            try
            {
                // Shared for writing, so a log another process still holds open can be read, as it can while a phase runs.
                log = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PhaseOutputUnreadException(logFile, ex.Message, ex);
            }

            try
            {
                if (log.Length < end + closes.Length)
                {
                    throw new PhaseOutputUnreadException(logFile, $"it holds {log.Length} byte(s), fewer than the {end + closes.Length} the phase left in it");
                }

                var found = new byte[closes.Length];

                log.Seek(end, SeekOrigin.Begin);
                log.ReadExactly(found);

                if (!found.AsSpan().SequenceEqual(closes.Span))
                {
                    throw new PhaseOutputUnreadException(logFile, "it no longer holds what the phase wrote after its last line: it was written again since");
                }

                log.Seek(start, SeekOrigin.Begin);

                return log;
            }
            catch (Exception ex)
            {
                log.Dispose();

                if (ex is IOException or UnauthorizedAccessException)
                {
                    throw new PhaseOutputUnreadException(logFile, ex.Message, ex);
                }

                throw;
            }
        }

        /// <summary>Reads up to <paramref name="count"/> bytes of the range, which the log held as it was opened.</summary>
        private int Read(FileStream log, byte[] bytes, int count)
        {
            int read;

            try
            {
                read = log.Read(bytes, 0, count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PhaseOutputUnreadException(logFile, ex.Message, ex);
            }

            // Whole as it was opened, so cut short only since: what is left is not what the phase printed.
            return read > 0 ? read : throw new PhaseOutputUnreadException(logFile, "it was cut short while it was read");
        }
    }
}
