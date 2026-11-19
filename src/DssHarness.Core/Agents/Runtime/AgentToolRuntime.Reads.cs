using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Pages;
using DssHarness.Core.Repository;
using DssHarness.Core.Text;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>The tools that read: a file viewed or a directory listed, files found by a pattern, files' text searched, a call's whole result read a page at a time.</summary>
public sealed partial class AgentToolRuntime
{
    /// <summary>What a call of <c>search_files</c> naming <c>max_results</c> is refused with: every hit is given, a page at a time.</summary>
    public const string MaxResultsRetired =
        "max_results is no longer taken: every matching line is given, a page at a time - the answer's first page says how many pages it holds, and read_result with this call's id and page gives each of the rest";

    /// <summary>
    /// <c>view_file</c>: a file's numbered lines a page at a time - the pages laid once by streaming the file through its
    /// decided open, and any page read by seeking to its mark (<see cref="TextPages"/>), so a file of any size, and a line
    /// of any width, is reachable page by page in the memory of a page - or the lines from a start to an end, within the
    /// page that holds the start, and how to ask for the rest; its digest recorded for an edit. A directory's entries.
    /// </summary>
    private async Task<Answer> ViewAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var path = Text(input, "path")!;
        var start = Number(input, "start_line");
        var end = Number(input, "end_line");
        var page = Number(input, "page");

        if (page is not null && (start is not null || end is not null))
        {
            return Answer.NotRun("page and start_line or end_line are two ways to ask for a part of a file: give page alone, or a range");
        }

        if (page is < 1)
        {
            return Answer.NotRun(Paging.BelowTheFirst(page.Value));
        }

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
            if (page is not null)
            {
                return Answer.NotRun($"'{Said(full)}/' is a directory, and page is a file's: its entries are given whole, from start_line to end_line where they are given, a page at a time by read_result where they pass one");
            }

            Tally.Read(full);
            var entries = Entries(full);

            return entries.Count == 0
                ? Answer.Done($"{Said(full)}/ is empty")
                : Listed($"{Said(full)}/ holds {ReportText.Counted(entries.Count, "entry")}", entries, start, end);
        }

        if (!files.FileExists(full))
        {
            return Answer.Failed($"there is no file or directory at '{Said(full)}'");
        }

        TextPagesKept kept;
        string? note;

        try
        {
            (kept, note) = await ViewedAsync(full, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentReadRefusedException moved)
        {
            return Answer.Refused(path, moved.Denied);
        }
        catch (InvalidDataException ex)
        {
            return Answer.Failed(SaidIn(ex, full));
        }

        // A whole read of a parsed file the graph holds is narrowed (graphReads: narrow): denied with the way to the
        // graph, let by as an escape where the graph is not sure of the file or a wholeFile lifted it - each escape and
        // each denial a line of the run's graph lines, the denial recorded as every denial is. A read denied shows the
        // agent nothing, so it is neither a view an edit may follow nor a read of the run's. A page or a range is no
        // whole read.
        if (start is null && end is null && page is null && Narrowed(path, full, kept.Lines, TokensOf(full)) is { } narrowed)
        {
            return Answer.Refused(path, narrowed);
        }

        // What an edit is checked against: the file as it was when its pages were laid, whatever part of it was viewed.
        _viewed[full] = kept.Digest;
        Tally.Read(full);

        if (kept.Lines == 1 && files.StandingOf(full) is { } standing && standing.Length == kept.Marks[0].Offset)
        {
            return Answer.Done($"'{Said(full)}' is empty");
        }

        try
        {
            return start is null && end is null
                ? await PageOfAsync(kept, page ?? 1, note, cancellationToken).ConfigureAwait(false)
                : await RangeAsync(full, kept, start ?? 1, end, note, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentReadRefusedException moved)
        {
            return Answer.Refused(path, moved.Denied);
        }
        catch (InvalidDataException ex)
        {
            return Answer.Failed(SaidIn(ex, full));
        }
    }

    /// <summary>
    /// What a whole read of the file at <paramref name="full"/> is under the narrowing of reads, where the run is given a
    /// graph that narrows (<c>graphReads: narrow</c>): the refusal, recorded as every denial is, where the graph holds
    /// the file as parsed and it is over <see cref="CodebaseGraphTool.NarrowLines"/> lines; an escape let by, recorded,
    /// where the graph is not sure of the file; null where the read passes plainly - a range, a page, a search and a write
    /// are never narrowed, and neither is a file the graph does not parse.
    /// </summary>
    /// <param name="path">The file as the agent's tool asked it.</param>
    /// <param name="full">The file, absolute.</param>
    /// <param name="lines">How many lines the file holds, as a view counts them.</param>
    /// <param name="tokens">The file's estimated tokens.</param>
    private AgentPolicyAnswer.Denied? Narrowed(string path, string full, long lines, int tokens)
    {
        if (_run.Graph is not { Narrow: true, Tool: { } tool } graph)
        {
            return null;
        }

        var tree = _run.Workspace.Worktree ?? _run.Workspace.MainCheckout;

        if (AgentPolicy.Beneath(tree, full, _host.Platform.PathComparison) is not { } relative)
        {
            return null;
        }

        var counted = (int)Math.Min(lines, int.MaxValue);
        var whole = tool.WholeRead(relative, counted);

        if (whole.Narrowed)
        {
            graph.Recorded?.Invoke(new GraphDeniedLine(whole.Path ?? relative, counted, tokens));
            return new AgentPolicyAnswer.Denied("graphReads: narrow", AgentPolicySource.Builtin, whole.Text);
        }

        if (whole.Escape is { } escape)
        {
            graph.Recorded?.Invoke(new GraphEscapeLine(whole.Path ?? relative, counted, tokens, escape, whole.Text));
        }

        return null;
    }

    /// <summary>
    /// The pages of the file at <paramref name="full"/> as a view gives them, numbered: those kept for it where it stands
    /// as it did when they were laid, else laid again by streaming it once through its open - the place the open file
    /// stands at decided again before a byte of it is read, as every page read after is - and what the page given from
    /// them says after its counts.
    /// </summary>
    /// <exception cref="AgentReadRefusedException">The open file stands where the reader may not read.</exception>
    /// <exception cref="InvalidDataException">The file does not read as text.</exception>
    private Task<(TextPagesKept Kept, string? Note)> ViewedAsync(string full, CancellationToken cancellationToken)
        => _results.LaidAsync(full, PageKey.Of(AgentTools.ViewFile, new JsonObject { ["path"] = full }), $"{AgentTools.ViewFile} {Said(full)}", numbered: true, OpenToView, cancellationToken);

    /// <summary>A file a view reads opened as itself, where it stands decided again as a read before a byte is read (<see cref="AgentFiles.OpenedOrRefused"/>).</summary>
    private Stream OpenToView(string file) => _host.Files.OpenedOrRefused(file, opened => OpenedPlace(opened, write: false).Denied);

    /// <summary>The <paramref name="page"/> of a file's pages, its header saying how to ask for the next; a page past the last refused with the file's range.</summary>
    private async Task<Answer> PageOfAsync(TextPagesKept kept, long page, string? note, CancellationToken cancellationToken)
    {
        var answer = await TextPages.PageAsync(OpenToView, kept, page, numbered: true, note, Paging.AskAgain, cancellationToken).ConfigureAwait(false);

        return answer.Refusal is null ? Answer.Page(answer.Text) : Answer.Failed(answer.Text);
    }

    /// <summary>
    /// The lines of a file from <paramref name="first"/> to <paramref name="end"/> - as far as its last where no end is
    /// given - within the page that holds the first (<see cref="TextPages.PageOfLine"/>), numbered, and how to ask for the
    /// rest: the next start, or the next page where the page ends inside a line wider than its room.
    /// </summary>
    private async Task<Answer> RangeAsync(string full, TextPagesKept kept, long first, long? end, string? note, CancellationToken cancellationToken)
    {
        if (first > kept.Lines)
        {
            return Answer.Failed($"'{Said(full)}' has {ReportText.Counted(kept.Lines, "line")}: start_line {Count(first)} is past its end");
        }

        var holding = TextPages.PageOfLine(kept, first);
        var answer = await TextPages.PageAsync(OpenToView, kept, holding, numbered: true, null, Paging.AskAgain, cancellationToken).ConfigureAwait(false);
        var mark = kept.Marks[holding - 1];
        var entries = answer.Sections.SelectMany(section => section.Entries).ToList();
        var last = Math.Min(end ?? long.MaxValue, kept.Lines);
        var cut = holding < kept.Marks.Count && kept.Marks[holding].FirstColumn > 0;
        var builder = new StringBuilder();

        if (note is not null)
        {
            builder.Append('[').Append(note).Append("]\n");
        }

        var shown = first - 1;
        var goesOn = false;

        // Entry k of the page is of line FirstLine + k: a page's entries are its lines in order, a piece one of its own.
        for (var at = 0; at < entries.Count; at++)
        {
            var line = mark.FirstLine + at;

            if (line < first)
            {
                continue;
            }

            if (line > last)
            {
                break;
            }

            builder.Append(entries[at].Text).Append('\n');
            shown = line;
            goesOn = cut && at == entries.Count - 1;
        }

        if (goesOn)
        {
            builder.Append($"[line {Count(shown)} goes on: ask again with page: {Count(holding + 1)}]");
        }
        else if (shown < last)
        {
            builder.Append(Rest("lines", first, shown, kept.Lines));
        }

        return Answer.Page(builder.ToString().TrimEnd('\n'));
    }

    /// <summary>
    /// A directory's entries, under what <paramref name="said"/> says of it, from <paramref name="start"/> to
    /// <paramref name="end"/> where they are given: every one, a listing longer than a page given a page at a time by
    /// <c>read_result</c> as every answer is.
    /// </summary>
    private static Answer Listed(string said, IReadOnlyList<string> entries, long? start, long? end)
    {
        var first = start ?? 1;

        if (first > entries.Count)
        {
            return Answer.Failed($"{said}: start_line {Count(first)} is past its end");
        }

        var last = Math.Min(end ?? long.MaxValue, entries.Count);
        var builder = new StringBuilder($"{said}:\n");

        for (var at = (int)first; at <= last; at++)
        {
            builder.Append(entries[at - 1]).Append('\n');
        }

        return Answer.Done(builder.ToString().TrimEnd('\n'));
    }

    /// <summary>What a view says where it stops before what was asked for: what it gave, and how to ask for the rest.</summary>
    private static string Rest(string plural, long first, long shown, long count)
        => $"[{plural} {Count(first)}-{Count(shown)} of {Count(count)}; ask again with start_line {Count(shown + 1)}]";

    /// <summary>The estimated tokens of the file at <paramref name="full"/>, by its bytes as it stands.</summary>
    private int TokensOf(string full)
        => _host.FileSystem.StandingOf(full) is { } standing ? EstimatedTokens.OfBytes(Math.Min(standing.Length, (long)int.MaxValue * EstimatedTokens.BytesPerToken)) : 0;

    /// <summary>Why a file does not read, as a view says it: the file named as every answer names a path.</summary>
    private string SaidIn(InvalidDataException ex, string full) => ex.Message.Replace($"'{full}'", $"'{Said(full)}'", StringComparison.Ordinal);

    /// <summary>A directory's entries, a directory's name ending with a separator, in ordinal order.</summary>
    private List<string> Entries(string directory)
    {
        var files = _host.FileSystem;

        return [.. files.EnumerateDirectories(directory).Select(entry => Path.GetFileName(entry) + "/")
            .Concat(files.EnumerateFiles(directory, recursive: false).Select(entry => Path.GetFileName(entry)))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// <c>find_files</c>: every file under a directory whose path matches a pattern, written a line at a time into the
    /// call's answer as it is found - a page of it given, every page of it by <c>read_result</c> - those the agent may not
    /// read left out and counted.
    /// </summary>
    private async Task<Answer> FindAsync(string callId, JsonObject input, CancellationToken cancellationToken)
    {
        var text = Text(input, "pattern")!;

        if (GlobPattern.Problem(text) is { } wrong)
        {
            return Answer.NotRun($"pattern '{text}' {wrong}");
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
            return Answer.NotRun($"there is no directory at '{Said(directory)}'");
        }

        var refused = 0;
        var listing = await _host.Files.ListAsync(directory, cancellationToken).ConfigureAwait(false);
        await using var lines = _records.Lines(callId);

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

            await lines.LineAsync(Said(full), cancellationToken).ConfigureAwait(false);
        }

        if (lines.Count == 0)
        {
            await lines.LineAsync($"no file under '{Said(directory)}' matches '{text}'", cancellationToken).ConfigureAwait(false);
        }

        if (refused > 0)
        {
            await lines.LineAsync(AgentListing.Refused(refused), cancellationToken).ConfigureAwait(false);
        }

        foreach (var note in listing.LeftOut(SaidIn(directory)))
        {
            await lines.LineAsync(note, cancellationToken).ConfigureAwait(false);
        }

        return await EndedAsync(callId, lines, ok: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>search_files</c>: every line under a directory a pattern matches - the pattern matched in time linear in each
    /// line (<see cref="LinearRegex"/>), every file read whatever its size, a line at a time - written into the call's
    /// answer as each hit is found, with as many lines of context around it as are asked: a page of it given, every page
    /// of it by <c>read_result</c>.
    /// </summary>
    private async Task<Answer> SearchAsync(string callId, JsonObject input, CancellationToken cancellationToken)
    {
        var text = Text(input, "pattern")!;
        var asked = Number(input, "context") ?? 0;

        if (asked < 0)
        {
            return Answer.NotRun($"context is {Count(asked)}, and is 0 or more");
        }

        // More lines than a file holds are as many as it holds: a count past an int's is every line around a hit.
        var context = (int)Math.Min(asked, int.MaxValue);

        if (!LinearRegex.TryCompile(text, Flag(input, "case_insensitive") ? RegexOptions.IgnoreCase : RegexOptions.None, out var pattern, out var refusal))
        {
            return Answer.NotRun($"{refusal.TrimEnd('.')}: write it without that");
        }

        GlobPattern? glob = null;

        if (Text(input, "glob") is { } globText)
        {
            if (GlobPattern.Problem(globText) is { } wrong)
            {
                return Answer.NotRun($"glob '{globText}' {wrong}");
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
            return Answer.NotRun($"there is no directory at '{Said(directory)}'");
        }

        await using var lines = _records.Lines(callId);
        var any = false;

        async ValueTask Found(AgentSearchHit hit, CancellationToken token)
        {
            var said = Said(Path.Combine(directory, hit.Path.Replace('/', Path.DirectorySeparatorChar)));

            if (context > 0 && any)
            {
                await lines.LineAsync("--", token).ConfigureAwait(false);
            }

            any = true;

            foreach (var line in hit.Before)
            {
                await lines.LineAsync($"{said}-{Count(line.Line)}- {line.Text}", token).ConfigureAwait(false);
            }

            await lines.LineAsync($"{said}:{Count(hit.Line)}: {hit.Text}", token).ConfigureAwait(false);

            foreach (var line in hit.After)
            {
                await lines.LineAsync($"{said}-{Count(line.Line)}- {line.Text}", token).ConfigureAwait(false);
            }
        }

        var search = await _host.Files.SearchAsync(directory, pattern, glob, _host.Platform.PathComparison, context, MayRead, Found, cancellationToken).ConfigureAwait(false);

        if (search.Found == 0)
        {
            await lines.LineAsync($"no line matches, in {ReportText.Counted(search.Searched, "file")} read", cancellationToken).ConfigureAwait(false);
        }

        foreach (var note in search.LeftOut(SaidIn(directory)))
        {
            await lines.LineAsync(note, cancellationToken).ConfigureAwait(false);
        }

        return await EndedAsync(callId, lines, ok: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The answer a call written a line at a time comes to, once its last line is written: the lines whole where they fit
    /// a page; else the first page of the call's file, which holds every one, its header saying how <c>read_result</c>
    /// gives the next and where the whole is.
    /// </summary>
    private async Task<Answer> EndedAsync(string callId, ResultLines lines, bool ok, CancellationToken cancellationToken)
    {
        await lines.EndAsync().ConfigureAwait(false);

        if (lines.Text is { } whole)
        {
            return ok ? Answer.Done(whole) : Answer.Failed(whole);
        }

        return Answer.Kept(ok, await _results.FirstPageAsync(callId, cancellationToken).ConfigureAwait(false), lines.File!);
    }

    /// <summary>
    /// <c>read_result</c>: a page of the whole result of a call of this conversation, by the call's id, from the file its
    /// records keep it in (<see cref="KeptResults"/>) - a message of the step's stored stream a compaction gave by its id,
    /// and the continuation record the conversation began with, among them - page 1 where none is asked, a page past the
    /// last refused with the result's range, a page below the first as the graph refuses it, and an id no result is kept
    /// for named.
    /// </summary>
    private async Task<Answer> ReadResultAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var id = Text(input, "id")!;
        var page = Number(input, "page") ?? 1;

        if (page < 1)
        {
            return Answer.NotRun(Paging.BelowTheFirst(page));
        }

        PageAnswer<TextEntry>? answer;

        try
        {
            answer = await _results.ReadAsync(id, page, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return Answer.Failed($"the result of '{ReportText.Printable(id)}' does not read: {ex.Message.TrimEnd('.')}");
        }

        return answer is null ? Answer.Failed(KeptResults.NoneKept(id))
            : answer.Refusal is null ? Answer.Page(answer.Text)
            : Answer.Failed(answer.Text);
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
