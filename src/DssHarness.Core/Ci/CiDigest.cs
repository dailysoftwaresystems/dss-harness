using System.Text.RegularExpressions;
using DssHarness.Core.Configuration;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Text;

namespace DssHarness.Core.Ci;

/// <summary>
/// One line of a CI job's log as a digest reads it (<see cref="CiLog.Of"/>).
/// </summary>
/// <param name="Line">The line, its decoration taken off, numbered as the kept log numbers it.</param>
/// <param name="Marked">Whether the forge marked it an annotation's.</param>
public readonly record struct CiLine(NumberedLine Line, bool Marked);

/// <summary>
/// A CI job's log as a digest reads it: every line of the kept log, in order and numbered as the kept file numbers it by
/// the one rule of where lines end, with the forge's own decoration taken off each - the timestamp every line is
/// prefixed with, a group marker a workflow folded its log with and each line of a step's script the runner echoes
/// under its <c>Run</c> group, every line of it in the runner's cyan (each line left blank, never dropped, so no line
/// after it moves: the script is the workflow's text, never what a tool printed, and an <c>::error::</c> it echoes is no
/// error the job met), and the marker the forge writes ahead of an annotation's line (<c>##[error]</c>,
/// <c>##[warning]</c>, <c>##[notice]</c>), so what a tool printed under it - a problem matcher's line - is read as the
/// tool printed it.
/// </summary>
public static class CiLog
{
    /// <summary>The timestamp a forge prefixes each line of a job's log with.</summary>
    private static readonly Regex Timestamp = LinearRegex.Compile(@"^\d{4}-\d{2}-\d{2}T[\d:.]+Z ?");

    /// <summary>A group marker a workflow folds its log with, its title after it where it has one.</summary>
    private static readonly Regex Group = LinearRegex.Compile(@"^##\[(?:group|endgroup)\]");

    /// <summary>The marker the forge writes ahead of the line of an annotation it made, and the line under it.</summary>
    private static readonly Regex Annotation = LinearRegex.Compile(@"^##\[(?:error|warning|notice)\](?<text>.*)$");

    /// <summary>The escape the runner opens each line of a step's script it echoes with: bold cyan.</summary>
    private const string EchoOpens = "\u001b[36;1m";

    /// <summary>The escape the runner closes each line of a step's script it echoes with.</summary>
    private const string EchoCloses = "\u001b[0m";

    /// <summary>
    /// The job's log read as a digest reads it, a line at a time as it is read, each with whether the forge marked it -
    /// never held as a list of its lines beside the text it was read from.
    /// </summary>
    /// <param name="log">The job's log, whole, masked as it was kept.</param>
    public static IEnumerable<CiLine> Of(string log)
    {
        ArgumentNullException.ThrowIfNull(log);

        return LineEnds.Streamed(log, (start, end, _) => (start, end)).Select((span, at) => Read(log, span.start, span.end, at + 1));
    }

    /// <summary>
    /// One line of a kept job's log as a digest reads it (<see cref="Of"/>): its timestamp, a byte order mark at its start
    /// and the forge's marker taken off, as the parsers read it (<see cref="PhaseParsers.AsRead"/>) - so a page of the log
    /// handed beside a digest is matched to the digest's own lines.
    /// </summary>
    /// <param name="line">The line, as the kept log holds it.</param>
    public static string TextOf(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return PhaseParsers.AsRead(Read(line, 0, line.Length, 1).Line.Text);
    }

    private static CiLine Read(string log, int start, int end, int number)
    {
        // The log's first line carries the byte order mark the forge writes, which no reader of the file sees.
        var line = Timestamp.Replace(number == 1 ? log[start..end].TrimStart('\uFEFF') : log[start..end], string.Empty);

        if (Group.IsMatch(line) || line.StartsWith(EchoOpens, StringComparison.Ordinal) && line.EndsWith(EchoCloses, StringComparison.Ordinal))
        {
            return new CiLine(new NumberedLine(number, number, string.Empty), false);
        }

        return Annotation.Match(line) is { Success: true } annotation
            ? new CiLine(new NumberedLine(number, number, annotation.Groups["text"].Value), true)
            : new CiLine(new NumberedLine(number, number, line), false);
    }
}

/// <summary>
/// What a red check digested to: the one owner of a CI digest, made of the two things the forge keeps of a check - its
/// annotations, read from the check-runs API as data, and its job's log, read by every form a CI log holds
/// (<see cref="PhaseParsers.ForCi"/>) - into one digest, each finding said once.
/// </summary>
/// <remarks>
/// <para>
/// The annotations that name a file come first: they are the forge's own structured reading of what failed, its place
/// relative to the repository. An annotation names no line of the log, but the log holds each one: a problem matcher's
/// annotation is the tool's line under its marker, which the tool's own form reads too - the same file, line, column and
/// message, one finding, its code, chain and log range the log's; a workflow command's is its message alone under the
/// marker - one finding again, the annotation taking that line as its log range. An annotation that names no file - the
/// runner's <c>Process completed with exit code 1.</c>, a workflow's <c>::error::</c> with none - is a sentence with no
/// place, which says that a step failed and not what: the log holds every one under its marker, and a marked line no
/// form read is said among the lines no parser held, never made the first error ahead of the tool's own that the log
/// printed before it. A check with no log - one no workflow run made - is its annotations alone, those that name no file
/// said among the lines no parser held as the log's would be.
/// </para>
/// <para>
/// Every path is placed against the job's workspace on its runner, never against this machine's tree: the runner checks
/// a repository out at <c>&lt;work&gt;/&lt;repository&gt;/&lt;repository&gt;</c> on every runner, hosted or not, in a
/// container or not, so a file a tool there named under that directory is inside the tree and named by its path relative
/// to it, read in the form the runner wrote it (<see cref="PlatformPaths.DirectoryEndingAsWritten"/>); a path relative
/// already - an annotation's, a tool's run in the tree - is inside it as it stands. A Go test's place, which
/// <c>go test</c> prints relative to its package's directory, is left as printed: no file of the job's tells that
/// directory (<see cref="ResultDigest.WithInTree(Func{string, string?}, Func{string, string, string?})"/>).
/// </para>
/// </remarks>
public static class CiDigest
{
    /// <summary>The tool a digest names the forge by, for what the forge itself said.</summary>
    public const string Forge = "github-actions";

    /// <summary>
    /// The digest of a red check: its annotations and its job's log, merged as the remarks say, every path placed against
    /// the job's workspace; null where neither holds anything a digest says.
    /// </summary>
    /// <param name="log">The job's log, whole, masked as it was kept; null where the check is no job or its log was not read.</param>
    /// <param name="annotations">The check run's annotations, as the forge keeps them, in its order.</param>
    /// <param name="repository">The repository the job ran a workflow of, by its name alone; null where it is not known.</param>
    /// <param name="mask">Masks what an annotation says, as the log was masked.</param>
    /// <param name="matchers">The configured matchers, which read a CI log as they read every log.</param>
    public static ResultDigest? Of(string? log, IReadOnlyList<CiAnnotation> annotations, string? repository, Func<string, string> mask, IReadOnlyList<ProblemMatcher> matchers)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(matchers);

        string[]? workspace = repository is { Length: > 0 } ? [repository, repository] : null;

        string? Relative(string file)
            => PlatformPaths.RelativeAsWritten(workspace is null ? null : PlatformPaths.DirectoryEndingAsWritten(file, workspace), file);

        // The annotations with a place, each as the forge said it, in its order; and what each said, masked, for the
        // log's lines are matched against it as they were kept.
        var placed = annotations.Where(annotation => annotation.Path is not null).ToList();
        var said = placed.Select(annotation => Said(annotation, mask)).ToArray();
        var messages = placed.Select(annotation => mask(annotation.Message)).ToArray();

        if (log is null)
        {
            // No log holds the sentences of the annotations that name no file: they are read from the forge, among the
            // lines no parser held, as the log's own would be.
            IReadOnlyList<string> sentences = [.. annotations.Where(annotation => annotation.Path is null).Select(annotation => Said(annotation, mask).Message).Distinct(StringComparer.Ordinal)];

            return said.Length == 0 && sentences.Count == 0 ? null : ResultDigest.Of(said, [], sentences).WithInTree(Relative);
        }

        // The log read as it streams: each line the forge marked noted with its text as the pass meets it, for what a
        // marked line becomes is decided once the findings are known; every other line no form read gathered as met.
        var marked = new Dictionary<int, string>();
        var gathering = new ResultDigest.Gathering();
        var found = PhaseParsers.FindingsOf(
            CiLog.Of(log).Select(line =>
            {
                if (line.Marked)
                {
                    marked[line.Line.Number] = line.Line.Text;
                }

                return line.Line;
            }),
            PhaseParsers.ForCi(matchers),
            line =>
            {
                if (!marked.ContainsKey(line.Number))
                {
                    gathering.Unparsed(line.Text);
                }
            })!;

        // Each annotation with a place, by its place and message and by its message alone, in the forge's order, for the
        // one the log's line was made into or from to be found once.
        var byPlace = new Dictionary<(string File, int? Line, int? Column, string Message), Queue<int>>();
        var byMessage = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);

        for (var at = 0; at < placed.Count; at++)
        {
            Enqueue(byPlace, (placed[at].Path!, placed[at].StartLine, placed[at].StartColumn, messages[at]), at);
            Enqueue(byMessage, messages[at], at);
        }

        var logs = new List<Diagnostic>();
        var covered = new HashSet<int>();

        foreach (var diagnostic in found.Diagnostics)
        {
            if (diagnostic.Log is { } range)
            {
                for (var number = range.From; number <= range.To; number++)
                {
                    covered.Add(number);
                }
            }

            // A problem matcher's line, which the forge made an annotation of and the tool's form read: one finding, in the
            // annotation's place, said as the tool said it, its file the repository's own path.
            if (diagnostic.File is { } file
                && Taken(byPlace, (Relative(file) ?? file, diagnostic.Line, diagnostic.Column, diagnostic.Message)) is { } annotation)
            {
                said[annotation] = diagnostic with { File = placed[annotation].Path };
                continue;
            }

            logs.Add(diagnostic);
        }

        // Each marked line no form read: the line of a workflow command's annotation with a place, which that annotation
        // takes as its log range; else a line the forge marked an error, a warning or a notice that no form understood -
        // its own sentence, the runner's that a step failed among them - said among the lines no parser held, each
        // sentence once.
        var sentencesSaid = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (number, text) in marked.OrderBy(line => line.Key))
        {
            if (covered.Contains(number))
            {
                continue;
            }

            int? echoed = null;

            // The first annotation of this message no line of the log was found for yet: one already given its line by
            // its place is that line's, not this one's.
            while (echoed is null && Taken(byMessage, text) is { } candidate)
            {
                echoed = said[candidate].Log is null ? candidate : null;
            }

            if (echoed is { } annotation)
            {
                said[annotation] = said[annotation] with { Log = (number, number) };
            }
            else if (text.Length > 0 && sentencesSaid.Add(text))
            {
                gathering.Unparsed(text);
            }
        }

        foreach (var diagnostic in said.Concat(logs))
        {
            gathering.Add(diagnostic);
        }

        foreach (var failure in found.Failures)
        {
            gathering.Add(failure);
        }

        var digest = gathering.Digest();

        return digest.HoldsAFinding || digest.UnparsedCount > 0 ? digest.WithInTree(Relative) : null;
    }

    /// <summary>An annotation said as a diagnostic: the forge's, its place where it names one, its title before its message and its details below.</summary>
    private static Diagnostic Said(CiAnnotation annotation, Func<string, string> mask)
        => new(
            Forge,
            annotation.Severity,
            null,
            (annotation.Title is { } title ? $"{mask(title)}: " : string.Empty) + mask(annotation.Message) + (annotation.Details is { } details ? $"\n{mask(details)}" : string.Empty) is { Length: > 0 } message ? message : annotation.Severity,
            annotation.Path,
            annotation.StartLine,
            annotation.StartColumn,
            annotation.EndLine,
            annotation.EndColumn);

    private static void Enqueue<TKey>(Dictionary<TKey, Queue<int>> byKey, TKey key, int at)
        where TKey : notnull
    {
        if (!byKey.TryGetValue(key, out var queue))
        {
            byKey[key] = queue = new Queue<int>();
        }

        queue.Enqueue(at);
    }

    /// <summary>The first annotation held under <paramref name="key"/> not yet taken, taken; null where none is.</summary>
    private static int? Taken<TKey>(Dictionary<TKey, Queue<int>> byKey, TKey key)
        where TKey : notnull
        => byKey.TryGetValue(key, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
}
