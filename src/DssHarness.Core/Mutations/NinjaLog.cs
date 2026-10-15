using RepoHarness.Core.Build;

namespace RepoHarness.Core.Mutations;

/// <summary>One step ninja ran, as its log keeps it: when it started and ended, the output's time, and the command's hash.</summary>
/// <param name="Output">The output, as ninja canonicalizes it.</param>
/// <param name="Start">When the step started, as the log writes it.</param>
/// <param name="End">When it ended.</param>
/// <param name="Written">The output's time once the step ran.</param>
/// <param name="Hash">The hash of the command it ran.</param>
public sealed record NinjaLogEntry(string Output, string Start, string End, string Written, string Hash);

/// <summary>
/// What a build directory's <c>.ninja_log</c> says ninja last ran for each output: the record that witnesses a step
/// was run, compared before a build and after it.
/// </summary>
/// <remarks>
/// <para>
/// ninja appends one line for every output of every step it runs - start and end, the output's time, the output, and a
/// hash of the command, tab-separated after a <c># ninja log v</c> header - and at times rewrites the file keeping only
/// the last line for each output. Versions 5 to 7 are read, which write those five fields alike; a log of any other
/// version, or none, is not read at all rather than read wrong.
/// </para>
/// <para>
/// A step's line is compared whole, never by a date: a stepped clock dates a fresh output before an old one, and the
/// start and end a line holds count from the build that ran it. Rewriting the log keeps every last line as it was, so
/// an output is rebuilt exactly when its last line differs, or is there only after the build.
/// </para>
/// </remarks>
public sealed class NinjaLog
{
    /// <summary>The log's name in a build directory.</summary>
    public const string FileName = ".ninja_log";

    /// <summary>What the first line opens with, before the version.</summary>
    private const string Header = "# ninja log v";

    /// <summary>The versions read: those that write five tab-separated fields to a line.</summary>
    private static readonly int[] Versions = [5, 6, 7];

    private readonly Dictionary<string, NinjaLogEntry> _entries;

    private NinjaLog(int version, Dictionary<string, NinjaLogEntry> entries)
    {
        Version = version;
        _entries = entries;
    }

    /// <summary>The log's version, as its header says it.</summary>
    public int Version { get; }

    /// <summary>The last line for each output, keyed as ninja canonicalizes the output.</summary>
    public IReadOnlyDictionary<string, NinjaLogEntry> Entries => _entries;

    /// <summary>
    /// Reads a log's <paramref name="lines"/>, keeping the last line for each output; <see langword="null"/> where the
    /// first line is no header of a version read here. A line that is not five fields - one a build killed mid-write
    /// left - is passed over, as ninja passes it over.
    /// </summary>
    /// <param name="lines">The log's lines, in order.</param>
    public static NinjaLog? Read(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        using var reading = lines.GetEnumerator();

        if (!reading.MoveNext()
            || !reading.Current.TrimEnd('\r').StartsWith(Header, StringComparison.Ordinal)
            || !int.TryParse(reading.Current.TrimEnd('\r')[Header.Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version)
            || !Versions.Contains(version))
        {
            return null;
        }

        var entries = new Dictionary<string, NinjaLogEntry>(StringComparer.Ordinal);

        while (reading.MoveNext())
        {
            var fields = reading.Current.TrimEnd('\r').Split('\t');

            if (fields.Length != 5 || fields[3].Length == 0)
            {
                continue;
            }

            var output = NinjaManifest.Normalize(fields[3]);

            entries[output] = new NinjaLogEntry(output, fields[0], fields[1], fields[2], fields[4]);
        }

        return new NinjaLog(version, entries);
    }

    /// <summary>
    /// Which of <paramref name="outputs"/> ninja ran a step for between <paramref name="before"/> and
    /// <paramref name="after"/>: those whose last line differs, or that have a line only after. In the order given,
    /// each as ninja canonicalizes it.
    /// </summary>
    /// <param name="before">The log as it was before the build.</param>
    /// <param name="after">The log as the build left it.</param>
    /// <param name="outputs">The outputs asked about, relative to the build directory.</param>
    public static IReadOnlyList<string> Rebuilt(NinjaLog before, NinjaLog after, IEnumerable<string> outputs)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(outputs);

        return
        [
            .. outputs
                .Select(NinjaManifest.Normalize)
                .Distinct(StringComparer.Ordinal)
                .Where(output => after._entries.TryGetValue(output, out var now)
                    && (!before._entries.TryGetValue(output, out var then) || now != then)),
        ];
    }
}
