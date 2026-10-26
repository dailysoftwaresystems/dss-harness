using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>The tools that read: a file viewed or a directory listed, files found by a pattern, files' text searched.</summary>
public sealed partial class AgentToolRuntime
{
    /// <summary><c>view_file</c>: a file's lines from a start to an end, numbered, within the view's bounds - its hash recorded for an edit - or a directory's entries.</summary>
    private async Task<Answer> ViewAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var path = Text(input, "path")!;
        var start = Number(input, "start_line");
        var end = Number(input, "end_line");

        if (start is < 1 || end is < 1)
        {
            return Answer.Failed("start_line and end_line count from 1");
        }

        if (start is { } from && end is { } to && to < from)
        {
            return Answer.Failed($"end_line {to} is before start_line {from}");
        }

        var place = ReadPlace(path);

        if (place.Denied is { } denied)
        {
            return Answer.Refused(path, denied);
        }

        var full = place.Full!;
        var files = _host.FileSystem;

        if (files.DirectoryExists(full))
        {
            Tally.Read(full);
            var entries = Entries(full);

            return entries.Count == 0
                ? Answer.Done($"{Said(full)}/ is empty")
                : Paged($"{Said(full)}/ holds {ReportText.Counted(entries.Count, "entry")}", entries, "entries", start, end, numbered: false);
        }

        if (!files.FileExists(full))
        {
            return Answer.Failed($"there is no file or directory at '{Said(full)}'");
        }

        if (TooLarge(full) is { } large)
        {
            return Answer.Failed(large);
        }

        var bytes = await files.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
        var reading = EncodedText.Read(bytes);

        if (!reading.IsRead)
        {
            return Answer.Failed($"'{Said(full)}' is {(reading.Binary ? "binary" : "no text this tool reads")}: {reading.Why}");
        }

        // What an edit is checked against: the file as it was when it was viewed, whatever part of it was.
        _viewed[full] = Hash(bytes);
        Tally.Read(full);

        if (reading.Text.Text.Length == 0)
        {
            return Answer.Done($"'{Said(full)}' is empty");
        }

        var lines = CodeFileText.Lines(reading.Text.Text);

        return Paged($"'{Said(full)}' has {ReportText.Counted(lines.Count, "line")}", lines, "lines", start, end, numbered: true);
    }

    /// <summary>
    /// The most a view's notes take - <see cref="LongLine"/> and <see cref="Rest"/> at the widest numbers and word a view
    /// can hold - which a view leaves of a call's cap for them, so a view is never cut.
    /// </summary>
    internal static int ViewNotesRoom { get; } = LongLine(int.MaxValue, int.MaxValue, int.MaxValue).Length + Rest("entries", int.MaxValue, int.MaxValue, int.MaxValue).Length;

    /// <summary>
    /// What a view gives of <paramref name="items"/> - a file's lines, each numbered, or a directory's entries, under what
    /// <paramref name="said"/> says of it - from <paramref name="start"/> to <paramref name="end"/> where they are given,
    /// at most <see cref="AgentTools.MostViewedLines"/> of them and <see cref="AgentTools.MostViewedCharacters"/> characters,
    /// and how to ask for the rest. One longer than the whole bound is given as far as the bound reaches, and said so.
    /// </summary>
    /// <param name="said">What the view says of what it reads, as <c>'a.txt' has 3 line(s)</c>: before a start past its end, and before a directory's entries.</param>
    /// <param name="items">The lines or the entries, in order.</param>
    /// <param name="plural">What they are, as the note on the rest names them: <c>lines</c>, <c>entries</c>.</param>
    /// <param name="start">The first given, from 1; the first where it is null.</param>
    /// <param name="end">The last given; as far as the bounds reach where it is null.</param>
    /// <param name="numbered">Whether each is numbered, as a file's lines are.</param>
    private static Answer Paged(string said, IReadOnlyList<string> items, string plural, long? start, long? end, bool numbered)
    {
        var first = (int)Math.Min(start ?? 1, int.MaxValue);

        if (first > items.Count)
        {
            return Answer.Failed($"{said}: start_line {Count(first)} is past its end");
        }

        var last = (int)Math.Min(Math.Min(end ?? long.MaxValue, items.Count), first + (long)AgentTools.MostViewedLines - 1);
        var builder = new StringBuilder(numbered ? string.Empty : $"{said}:\n");
        var shown = first - 1;

        for (var at = first; at <= last; at++)
        {
            var number = numbered ? $"{at}\t" : string.Empty;
            var text = items[at - 1];

            if (builder.Length + number.Length + text.Length + 1 > AgentTools.MostViewedCharacters)
            {
                if (shown >= first)
                {
                    break;
                }

                // The first is longer than the view's whole bound: it is given as far as the bound reaches, and said so.
                var given = TextCut.Head(text, Math.Max(0, AgentTools.MostViewedCharacters - builder.Length - number.Length));
                builder.Append(number).Append(given).Append(LongLine(at, text.Length, given.Length));
                shown = at;
                break;
            }

            builder.Append(number).Append(text).Append('\n');
            shown = at;
        }

        if (shown < items.Count && (end is null || shown < end))
        {
            builder.Append(Rest(plural, first, shown, items.Count));
        }

        return Answer.Done(builder.ToString().TrimEnd('\n'));
    }

    /// <summary>What a view says after a line longer than its bound: how long the line is, and how much of it was given.</summary>
    private static string LongLine(long line, long length, long given)
        => $"\n[line {Count(line)} is {Count(length)} characters: the first {Count(given)} are given]\n";

    /// <summary>What a view says where it stops before what was asked for: what it gave, and how to ask for the rest.</summary>
    private static string Rest(string plural, long first, long shown, long count)
        => $"[{plural} {Count(first)}-{Count(shown)} of {Count(count)}; ask again with start_line {Count(shown + 1)}]";

    /// <summary>
    /// Why the file at <paramref name="full"/> is held whole by no view or edit - it holds more than
    /// <see cref="AgentTools.MostViewedBytes"/> - before it is read; null where it may be.
    /// </summary>
    private string? TooLarge(string full)
        => _host.FileSystem.StandingOf(full) is { Length: > AgentTools.MostViewedBytes } standing
            ? $"'{Said(full)}' holds {DiskSpace.Size(standing.Length)}, past the {DiskSpace.Size(AgentTools.MostViewedBytes)} a view or an edit holds whole: read or search it with a command"
            : null;

    /// <summary>A directory's entries, a directory's name ending with a separator, in ordinal order.</summary>
    private List<string> Entries(string directory)
    {
        var files = _host.FileSystem;

        return [.. files.EnumerateDirectories(directory).Select(entry => Path.GetFileName(entry) + "/")
            .Concat(files.EnumerateFiles(directory, recursive: false).Select(entry => Path.GetFileName(entry)))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary><c>find_files</c>: the files under a directory whose path matches a pattern, those the agent may not read left out and counted.</summary>
    private async Task<Answer> FindAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var text = Text(input, "pattern")!;

        if (GlobPattern.Problem(text) is { } wrong)
        {
            return Answer.Failed($"pattern '{text}' {wrong}");
        }

        var pattern = GlobPattern.Parse(text);
        var path = Text(input, "path") ?? ".";
        var place = SearchPlace(path);

        if (place.Denied is { } denied)
        {
            return Answer.Refused(path, denied);
        }

        var directory = place.Full!;

        if (!_host.FileSystem.DirectoryExists(directory))
        {
            return Answer.Failed($"there is no directory at '{Said(directory)}'");
        }

        var found = new List<string>();
        var refused = 0;
        var listing = await _host.Files.ListAsync(directory, cancellationToken).ConfigureAwait(false);

        foreach (var file in listing.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!pattern.Matches(file, _host.Platform.PathComparison))
            {
                continue;
            }

            var full = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));

            if (!MayRead(full))
            {
                refused++;
                continue;
            }

            found.Add(Said(full));
        }

        var builder = new StringBuilder();
        builder.AppendJoin('\n', found.Take(AgentTools.MostFound));

        if (found.Count == 0)
        {
            builder.Append($"no file under '{Said(directory)}' matches '{text}'");
        }

        if (found.Count > AgentTools.MostFound)
        {
            builder.Append($"\n[{ReportText.Counted(found.Count - AgentTools.MostFound, "more file")} not shown: narrow the pattern or the directory]");
        }

        if (refused > 0)
        {
            builder.Append('\n').Append(AgentListing.Refused(refused));
        }

        foreach (var note in listing.LeftOut(SaidIn(directory)))
        {
            builder.Append('\n').Append(note);
        }

        return Answer.Done(builder.ToString());
    }

    /// <summary><c>search_files</c>: the lines under a directory a pattern matches, by the one search the runtime runs.</summary>
    private async Task<Answer> SearchAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var text = Text(input, "pattern")!;
        var context = Number(input, "context") ?? 0;
        var most = Number(input, "max_results") ?? AgentTools.DefaultSearchResults;

        if (context is < 0 or > AgentTools.MostSearchContext)
        {
            return Answer.Failed($"context is {context}, and is 0 to {AgentTools.MostSearchContext}");
        }

        if (most is < 1 or > AgentTools.MostSearchResults)
        {
            return Answer.Failed($"max_results is {most}, and is 1 to {AgentTools.MostSearchResults}");
        }

        Regex pattern;

        try
        {
            pattern = new Regex(text, RegexOptions.CultureInvariant | (Flag(input, "case_insensitive") ? RegexOptions.IgnoreCase : RegexOptions.None), AgentFiles.PatternBound);
        }
        catch (ArgumentException ex)
        {
            return Answer.Failed($"pattern '{text}' is no .NET regular expression: {ex.Message.TrimEnd('.')}");
        }

        GlobPattern? glob = null;

        if (Text(input, "glob") is { } globText)
        {
            if (GlobPattern.Problem(globText) is { } wrong)
            {
                return Answer.Failed($"glob '{globText}' {wrong}");
            }

            glob = GlobPattern.Parse(globText);
        }

        var path = Text(input, "path") ?? ".";
        var place = SearchPlace(path);

        if (place.Denied is { } denied)
        {
            return Answer.Refused(path, denied);
        }

        var directory = place.Full!;

        if (!_host.FileSystem.DirectoryExists(directory))
        {
            return Answer.Failed($"there is no directory at '{Said(directory)}'");
        }

        AgentSearch search;

        try
        {
            search = await _host.Files.SearchAsync(directory, pattern, glob, _host.Platform.PathComparison, (int)context, (int)most, MayRead, cancellationToken).ConfigureAwait(false);
        }
        catch (RegexMatchTimeoutException)
        {
            return Answer.Failed($"pattern '{text}' took longer than {AgentFiles.PatternBound.TotalSeconds} s on a file's text: make it simpler");
        }

        return Answer.Done(Rendered(search, directory, (int)context));
    }

    /// <summary>A search's hits as the model reads them: <c>path:line: text</c>, its context as <c>path-line- text</c>, groups apart; then what it did not give.</summary>
    private string Rendered(AgentSearch search, string directory, int context)
    {
        var builder = new StringBuilder();

        foreach (var hit in search.Hits)
        {
            var said = Said(Path.Combine(directory, hit.Path.Replace('/', Path.DirectorySeparatorChar)));

            if (context > 0 && builder.Length > 0)
            {
                builder.Append("--\n");
            }

            foreach (var line in hit.Before)
            {
                builder.Append($"{said}-{line.Line}- {line.Text}\n");
            }

            builder.Append($"{said}:{hit.Line}: {hit.Text}\n");

            foreach (var line in hit.After)
            {
                builder.Append($"{said}-{line.Line}- {line.Text}\n");
            }
        }

        if (search.Hits.Count == 0)
        {
            builder.Append($"no line matches, in {ReportText.Counted(search.Searched, "file")} read\n");
        }

        if (search.More > 0)
        {
            builder.Append($"[{ReportText.Counted(search.More, "more matching line")} not shown: narrow the search, or raise max_results]\n");
        }

        foreach (var note in search.LeftOut(SaidIn(directory)))
        {
            builder.Append(note).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>How a place under <paramref name="directory"/> is said, from its path relative to it, as every path an answer gives is said.</summary>
    private Func<string, string> SaidIn(string directory)
        => relative => Said(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Whether the agent may read <paramref name="file"/>, absolute: inside its roots by where it leads, and allowed by the policy.</summary>
    private bool MayRead(string file) => ReadPlace(file).Full is not null;

    /// <summary>
    /// <paramref name="full"/> as an answer says it: relative to the agent's working directory with forward separators,
    /// where it is inside it; else whole.
    /// </summary>
    private string Said(string full)
    {
        var working = _run.Ground!.WorkingDirectory;

        return AgentPolicy.Beneath(working, full, _host.Platform.PathComparison) is { } relative ? relative
            : PathContainment.AreSame(Path.GetFullPath(working), full, _host.Platform.PathComparison) ? "."
            : full;
    }
}
