using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Execution;

/// <summary>
/// The matches of a phase's timing patterns, pattern by pattern, each in the order it appeared: every one of them, each
/// written to the phase's timing record beside its log as it was found (<see cref="RecordOf"/>), and read back from there
/// whenever they are asked for - never held, so a pattern that matches every line of a flood costs the record its lines
/// and the phase nothing in memory, and no mark is ever left out for a number of them.
/// </summary>
[CollectionBuilder(typeof(PhaseTimings), nameof(Create))]
public sealed class PhaseTimings : IEnumerable<PhaseTiming>
{
    /// <summary>What a timing record's name ends with, after its log's name less the log's own extension.</summary>
    public const string Extension = ".timings.jsonl";

    private readonly IReadOnlyList<PhaseTiming>? _held;
    private readonly IFileSystem? _fileSystem;
    private readonly IReadOnlyList<string> _patterns;

    private PhaseTimings(IReadOnlyList<PhaseTiming>? held, IFileSystem? fileSystem, string? record, IReadOnlyList<string> patterns, long count)
    {
        _held = held;
        _fileSystem = fileSystem;
        _patterns = patterns;
        Record = record;
        Count = count;
    }

    /// <summary>No marks: a phase that declares no timing pattern, or none that matched.</summary>
    public static PhaseTimings None { get; } = new([], null, null, [], 0);

    /// <summary>How many marks there are, every pattern's together.</summary>
    public long Count { get; }

    /// <summary>The record the marks are kept in, where they were kept in one; <see langword="null"/> for marks held as given.</summary>
    public string? Record { get; }

    /// <summary>
    /// <paramref name="marks"/>, held as given: for a phase made up rather than run, and the tests of what reads one - and
    /// what a collection expression makes.
    /// </summary>
    /// <param name="marks">The marks, in the order they are read.</param>
    public static PhaseTimings Create(ReadOnlySpan<PhaseTiming> marks) => marks.IsEmpty ? None : new([.. marks], null, null, [], marks.Length);

    /// <summary>Where the timing record of the phase whose log is <paramref name="logFile"/> is written: beside it, named for it.</summary>
    /// <param name="logFile">The phase's log.</param>
    public static string RecordOf(string logFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFile);

        return Path.Combine(Path.GetDirectoryName(logFile) ?? string.Empty, Path.GetFileNameWithoutExtension(logFile) + Extension);
    }

    /// <summary>
    /// The <paramref name="count"/> marks <paramref name="record"/> keeps of <paramref name="patterns"/>, each a line as a
    /// <see cref="Mark"/>, read back pattern by pattern.
    /// </summary>
    /// <param name="fileSystem">Opens the record, each time its marks are asked for.</param>
    /// <param name="record">The record.</param>
    /// <param name="patterns">The timing patterns, in the order they were declared: a mark names its pattern by its place here.</param>
    /// <param name="count">How many marks were written to it.</param>
    internal static PhaseTimings InRecord(IFileSystem fileSystem, string record, IReadOnlyList<string> patterns, long count)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(record);
        ArgumentNullException.ThrowIfNull(patterns);

        return count == 0 ? None : new(null, fileSystem, record, patterns, count);
    }

    /// <summary>
    /// Every mark, pattern by pattern, each in the order it appeared: read afresh from the record on every call, one pass
    /// of it a pattern, holding a line at a time.
    /// </summary>
    /// <exception cref="InvalidDataException">The record no longer holds the marks the phase wrote to it: gone, cut short, or written again.</exception>
    public IEnumerator<PhaseTiming> GetEnumerator()
    {
        if (_held is not null)
        {
            foreach (var mark in _held)
            {
                yield return mark;
            }

            yield break;
        }

        long read = 0;

        for (var pattern = 0; pattern < _patterns.Count; pattern++)
        {
            long lines = 0;

            foreach (var mark in Marks())
            {
                lines++;

                if (mark.Pattern < 0 || mark.Pattern >= _patterns.Count)
                {
                    throw Unread($"its line {lines.ToString(CultureInfo.InvariantCulture)} names no pattern the phase declared");
                }

                if (mark.Pattern == pattern)
                {
                    read++;
                    yield return new PhaseTiming(_patterns[pattern], mark.Text, mark.Value);
                }
            }

            if (lines != Count)
            {
                throw Unread($"it holds {lines.ToString(CultureInfo.InvariantCulture)} marks, where the phase wrote {Count.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (read != Count)
        {
            throw Unread($"{read.ToString(CultureInfo.InvariantCulture)} of its marks are of the phase's patterns, where it wrote {Count.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The record's lines, each read as a mark.</summary>
    private IEnumerable<Mark> Marks()
    {
        IEnumerator<FileLine> lines;

        try
        {
            lines = FileLines.Read(_fileSystem!, Record!).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Unread(ex.Message, ex);
        }

        using (lines)
        {
            while (true)
            {
                Mark? mark;

                try
                {
                    if (!lines.MoveNext())
                    {
                        yield break;
                    }

                    mark = JsonSerializer.Deserialize<Mark>(lines.Current.Text, JsonStateFile.LineOptions);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    throw Unread(ex.Message, ex);
                }

                yield return mark ?? throw Unread("a line of it holds null");
            }
        }
    }

    /// <summary>Why the marks could not be read back from the record.</summary>
    private InvalidDataException Unread(string why, Exception? inner = null)
        => new($"the timing marks of the phase could not be read back from '{Record}': {why}", inner);

    /// <summary>One line of a timing record: the pattern by its place among the phase's, the whole match, and its value.</summary>
    /// <param name="Pattern">The pattern's place among those the phase declared, from 0.</param>
    /// <param name="Text">The whole match, exactly as the command printed it.</param>
    /// <param name="Value">The first capturing group where the pattern has one, the whole match otherwise.</param>
    internal sealed record Mark(int Pattern, string Text, string Value);
}
