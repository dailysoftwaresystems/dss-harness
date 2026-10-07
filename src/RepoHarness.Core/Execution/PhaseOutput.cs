using System.Text;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Execution;

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
    internal static PhaseOutput InLog(string logFile, long start, long end)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFile);

        return new LogRange(logFile, start, end);
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

    /// <summary>A range of a log's bytes.</summary>
    private sealed class LogRange(string logFile, long start, long end) : PhaseOutput
    {
        public override IEnumerable<string> Lines()
        {
            var lines = new Queue<string>();

            // Every line ends as the phase runner ended it: a carriage return before the line feed is that ending only where
            // the runner writes one, and otherwise the line's own.
            var splitter = new LineSplitter(
                lines.Enqueue,
                ProcessRunner.LongestLine,
                carriageReturnEnds: string.Equals(PhaseRunner.LogLineEnding, "\r\n", StringComparison.Ordinal));

            // Shared for writing, so a log another process still holds open can be read, as it can while a phase runs.
            using var log = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);

            log.Seek(start, SeekOrigin.Begin);

            var decoder = Utf8NoBom.GetDecoder();
            var bytes = new byte[ReadBufferSize];
            var chars = new char[Utf8NoBom.GetMaxCharCount(ReadBufferSize)];

            for (var remaining = end - start; remaining > 0;)
            {
                var read = log.Read(bytes, 0, (int)Math.Min(bytes.Length, remaining));

                if (read == 0)
                {
                    // Shorter than the phase left it: what is there is all there is to read.
                    break;
                }

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
    }
}
