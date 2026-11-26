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
    /// page that holds the start, headed by the declarations they meet where the run's graph parsed the file, and how to ask
    /// for the rest; its digest recorded for an edit. A whole read the graph's narrowing takes is answered with the file's
    /// outline. A directory's entries.
    /// </summary>
    /// <param name="callId">The call's id, which a range whose head passes a page with its first line is kept and paged by.</param>
    /// <param name="input">The call's input.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    private async Task<Answer> ViewAsync(string callId, JsonObject input, CancellationToken cancellationToken)
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

        // A whole read of a parsed file the graph holds is narrowed (graphReads: narrow): answered with the file's outline
        // in its place, let by as an escape where the graph is not sure of the file or a wholeFile lifted it - each escape
        // and each outline given a line of the run's graph lines. An outline shows the agent the file's declarations and
        // none of its lines, so it is neither a view an edit may follow nor a read of the run's. A page or a range is no
        // whole read.
        if (start is null && end is null && page is null && await OutlinedAsync(full, kept.Lines, TokensOf(full), cancellationToken).ConfigureAwait(false) is { } outlined)
        {
            return Answer.Done(outlined);
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
                : await RangeAsync(callId, full, kept, start ?? 1, end, note, await DeclarationsAsync(full, again: true, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
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
    /// What a whole read of the file at <paramref name="full"/> is answered with under the narrowing of reads, where the run
    /// is given a graph that narrows (<c>graphReads: narrow</c>): the file's outline in its place, recorded as a line of the
    /// run's graph with what it cost and what the whole read would have, where the graph holds the file as parsed and it is
    /// over <see cref="CodebaseGraphTool.NarrowLines"/> lines (<see cref="CodebaseGraphTool.WholeReadAsync"/>); null where the
    /// read passes - an escape let by, recorded, where the graph is not sure of the file, and plainly else. A range, a page,
    /// a search and a write are never narrowed, and neither is a file the graph does not parse.
    /// </summary>
    /// <param name="full">The file, absolute.</param>
    /// <param name="lines">How many lines the file holds, as a view counts them.</param>
    /// <param name="tokens">The file's estimated tokens.</param>
    /// <param name="cancellationToken">Stops the reading of the outline.</param>
    private async Task<string?> OutlinedAsync(string full, long lines, int tokens, CancellationToken cancellationToken)
    {
        if (_run.Graph is not { Narrow: true, Tool: { } tool } graph || GraphPathOf(full) is not { } relative)
        {
            return null;
        }

        var counted = (int)Math.Min(lines, int.MaxValue);
        var whole = await tool.WholeReadAsync(relative, counted, cancellationToken).ConfigureAwait(false);

        if (whole.Narrowed)
        {
            graph.Recorded?.Invoke(new GraphOutlinedLine(whole.Path ?? relative, counted, tokens, EstimatedTokens.Of(whole.Text)));
            return whole.Text;
        }

        if (whole.Escape is { } escape)
        {
            graph.Recorded?.Invoke(new GraphEscapeLine(whole.Path ?? relative, counted, tokens, escape, whole.Text));
        }

        return null;
    }

    /// <summary>
    /// The declarations of the file at <paramref name="full"/> as it stands now, where the run is given a graph that holds
    /// it as a file it parsed (<see cref="CodebaseGraphTool.DeclarationsAsync"/>) - what a range of it is named by - the file
    /// checked again by the graph where any moved, or, for a search that checks every moved file of its call together, those
    /// its lines confirm alone (<paramref name="again"/> false, <see cref="CodebaseGraphTool.ConfirmedDeclarationsAsync"/>);
    /// null where the run has no graph, the file is outside the tree it works in, or the graph parsed no such file.
    /// </summary>
    /// <param name="full">The file, absolute.</param>
    /// <param name="again">Whether a file whose declarations moved is checked again by the graph here, on its own.</param>
    /// <param name="cancellationToken">Stops the reading of the file.</param>
    private async Task<GraphFileDeclarations?> DeclarationsAsync(string full, bool again, CancellationToken cancellationToken)
        => _run.Graph is { Tool: { } tool } && GraphPathOf(full) is { } relative
            ? again
                ? await tool.DeclarationsAsync(relative, cancellationToken).ConfigureAwait(false)
                : await tool.ConfirmedDeclarationsAsync(relative, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>The file at <paramref name="full"/> as the run's graph names it - relative to the tree the run works in, with forward separators - where it is beneath it; null else.</summary>
    private string? GraphPathOf(string full)
        => AgentPolicy.Beneath(_run.Workspace.Worktree ?? _run.Workspace.MainCheckout, full, _host.Platform.PathComparison);

    /// <summary>
    /// The pages of the file at <paramref name="full"/> as a view gives them, numbered: those kept for it where it stands
    /// as it did when they were laid, else laid again by streaming it once through its open - the place the open file
    /// stands at decided again before a byte of it is read, as every page read after is - and what the page given from
    /// them says after its counts.
    /// </summary>
    /// <exception cref="AgentReadRefusedException">The open file stands where the reader may not read.</exception>
    /// <exception cref="InvalidDataException">The file does not read as text.</exception>
    private Task<(TextPagesKept Kept, string? Note)> ViewedAsync(string full, CancellationToken cancellationToken)
        => _results.LaidAsync(full, PageKey.Of(AgentTools.ViewFile, new JsonObject { ["path"] = full }), $"{AgentTools.ViewFile} {Said(full)}", numbered: true, OpenToView, blocked: false, cancellationToken);

    /// <summary>A file a view reads opened as itself, where it stands decided again as a read before a byte is read (<see cref="AgentFiles.OpenedOrRefused"/>).</summary>
    private Stream OpenToView(string file) => _host.Files.OpenedOrRefused(file, opened => OpenedPlace(opened, write: false).Denied);

    /// <summary>The <paramref name="page"/> of a file's pages, its header saying how to ask for the next; a page past the last refused with the file's range.</summary>
    private async Task<Answer> PageOfAsync(TextPagesKept kept, long page, string? note, CancellationToken cancellationToken)
    {
        var answer = await TextPages.PageAsync(OpenToView, kept, page, numbered: true, note, Paging.AskAgain, cancellationToken).ConfigureAwait(false);

        return answer.Refusal is null ? Answer.Page(answer.Text) : Answer.Failed(answer.Text);
    }

    /// <summary>What heads the declarations a range of a file meets, where the run's graph holds the file as parsed.</summary>
    internal const string RangeDeclarations = "[the codebase graph's declarations these lines meet:]";

    /// <summary>
    /// The lines of a file from <paramref name="first"/> to <paramref name="end"/> - as far as its last where no end is
    /// given - within the page that holds the first (<see cref="TextPages.PageOfLine"/>), numbered, and how to ask for the
    /// rest: the next start, or the next page where the page ends inside a line wider than its room. Where the run's graph
    /// holds the file as parsed, the <paramref name="declarations"/> the lines given meet head them, each as the graph names
    /// a node - its handle, kind, signature and <c>path:start-end #hash</c> - so the agent can ask a body of one, or read the
    /// lines beside it, exactly; the head is charged to the page's room with the lines (<see cref="KeptResults.Room"/>), the
    /// first line's among them, the lines stopping where the two would pass it, the rest asked for as any is - and a head
    /// that passes the room with the first line is never sent over it: the two are written as a call's answer a line at a
    /// time (<see cref="ResultLines"/>), the head a block, kept and given a page at a time by <c>read_result</c> as every
    /// answer longer than a page is, a later page that begins inside the head giving its first line again, the last line
    /// saying how to ask for the lines after the first.
    /// </summary>
    private async Task<Answer> RangeAsync(string callId, string full, TextPagesKept kept, long first, long? end, string? note, GraphFileDeclarations? declarations, CancellationToken cancellationToken)
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

        // The declarations the lines may meet, in the order they begin, each said as the graph names a node: one joins the
        // head once a line given reaches its first line, so the head names those the lines given meet, and no other.
        var cards = declarations?.Meeting(first, last) ?? [];
        var meeting = cards.Select(card => "  " + GraphLines.Of(card)).ToList();
        var given = new StringBuilder();
        var joined = 0;
        var headCost = 0;
        var shown = first - 1;
        var goesOn = false;
        var paged = false;

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

            var joins = joined;
            var headed = headCost;

            while (joins < meeting.Count && cards[joins].Start <= line)
            {
                headed += meeting[joins].Length + 1;
                joins++;
            }

            // The head is charged to the page's room with the lines, the first line's among them: a line the room no longer
            // holds beside the head and the lines before it is asked for again, and a first line it does not hold beside the
            // head is paged with it. The page's own lines fit its room, so a range with no head is given as its page holds it.
            if (headed > 0 && headed + given.Length + entries[at].Text.Length + 1 > KeptResults.Room)
            {
                if (shown >= first)
                {
                    break;
                }

                paged = true;
            }

            joined = joins;
            headCost = headed;
            given.Append(entries[at].Text).Append('\n');
            shown = line;
            goesOn = cut && at == entries.Count - 1;

            if (paged)
            {
                break;
            }
        }

        var rest = goesOn ? $"[line {Count(shown)} goes on: ask again with page: {Count(holding + 1)}]"
            : shown < last ? Rest("lines", first, shown, kept.Lines)
            : null;

        if (paged)
        {
            return await PagedRangeAsync(callId, note, meeting.Take(joined), given.ToString().TrimEnd('\n'), rest, cancellationToken).ConfigureAwait(false);
        }

        if (joined > 0)
        {
            builder.Append(RangeDeclarations).Append('\n');

            foreach (var said in meeting.Take(joined))
            {
                builder.Append(said).Append('\n');
            }
        }

        builder.Append(given).Append(rest);

        return Answer.Page(builder.ToString().TrimEnd('\n'));
    }

    /// <summary>
    /// A range whose head passes a page's room with its first line, as the call <paramref name="callId"/>'s answer: what
    /// its view says, the head - <see cref="RangeDeclarations"/> heading a block of every declaration <paramref name="head"/>
    /// says - the first line, and how to ask for the lines after it, written a line at a time and given as every answer
    /// written so is: whole where it fits a page, else kept and given a page at a time by <c>read_result</c>, a page that
    /// begins among the declarations giving the head's first line again.
    /// </summary>
    private async Task<Answer> PagedRangeAsync(string callId, string? note, IEnumerable<string> head, string line, string? rest, CancellationToken cancellationToken)
    {
        await using var lines = _records.Lines(callId);

        if (note is not null)
        {
            await lines.LineAsync($"[{note}]", cancellationToken).ConfigureAwait(false);
        }

        await lines.HeadAsync(RangeDeclarations, cancellationToken).ConfigureAwait(false);

        foreach (var said in head)
        {
            await lines.BlockLineAsync(said, cancellationToken).ConfigureAwait(false);
        }

        await lines.LineAsync(line, cancellationToken).ConfigureAwait(false);

        if (rest is not null)
        {
            await lines.LineAsync(rest, cancellationToken).ConfigureAwait(false);
        }

        return await EndedAsync(callId, lines, ok: true, cancellationToken).ConfigureAwait(false);
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
    /// of it by <c>read_result</c>. Where the run has a graph, a hit inside a declaration of a file the graph parsed is
    /// written under that declaration's line, as the graph names a node, as the file stands now
    /// (<see cref="CodebaseGraphTool.DeclarationsAsync"/>), so the agent knows the declaration and the lines to read next
    /// without a view; a line each, paged as every line of the answer is, a page that begins among a declaration's lines
    /// giving its line again first (<see cref="ResultLines.HeadAsync"/>). Every file of the call whose declarations moved
    /// since the graph read it is checked again in one check before any hit is written under them
    /// (<see cref="CodebaseGraphTool.ReadAgainAsync"/>), so a call pays for those files and one view, however many it meets.
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

        // Where the run has a graph, each hit inside a declaration of a file it parsed is shown under that declaration -
        // the innermost that holds the hit's line, as the file stands now - its line said as the graph names a node, the
        // declaration's hits and their context indented beneath it; a hit outside every declaration is shown as it is. Hits
        // come file by file, each file's in its order, so one file's declarations are read at its first hit and held while
        // its hits are written, and a declaration is said again only where a hit outside it came between. The declaration's
        // line heads a block of the answer and the lines beneath it are the block's (ResultLines.HeadAsync), so a page of
        // the answer that begins among them gives the declaration's line again first.
        var tool = _run.Graph?.Tool;
        var lines = _records.Lines(callId);
        GraphFileDeclarations? declarations = null;
        string? under = null;
        var any = false;

        // A hit written into the answer, under the declaration of its file's that holds its line, where one does.
        async ValueTask WrittenAsync(AgentSearchHit hit, CancellationToken token)
        {
            var said = Said(Path.Combine(directory, hit.Path.Replace('/', Path.DirectorySeparatorChar)));
            var card = declarations?.Holding(hit.Line);
            var opens = card is not null && !string.Equals(card.Id, under, StringComparison.Ordinal);

            // A line of the hit, beneath its declaration's line where it is inside one: a line of that block.
            ValueTask Beneath(string line) => card is null ? lines.LineAsync(line, token) : lines.BlockLineAsync("  " + line, token);

            if (context > 0 && any)
            {
                // The separator before a declaration's line is no line of its block.
                await (opens ? lines.LineAsync("--", token) : Beneath("--")).ConfigureAwait(false);
            }

            any = true;
            under = card?.Id;

            if (opens)
            {
                await lines.HeadAsync(GraphLines.Of(card!), token).ConfigureAwait(false);
            }

            foreach (var line in hit.Before)
            {
                await Beneath($"{said}-{Count(line.Line)}- {line.Text}").ConfigureAwait(false);
            }

            await Beneath($"{said}:{Count(hit.Line)}: {hit.Text}").ConfigureAwait(false);

            foreach (var line in hit.After)
            {
                await Beneath($"{said}-{Count(line.Line)}- {line.Text}").ConfigureAwait(false);
            }
        }

        // The files that held a hit, in the order met, and those of them whose declarations moved since the graph read them
        // - an edit no sync followed. From the first such file the search writes nothing and only finds the rest: each is
        // checked again by the graph, all of them in one check, before any hit is written under their declarations.
        var held = new List<string>();
        var moved = new List<string>();

        async ValueTask Found(AgentSearchHit hit, CancellationToken token)
        {
            if (held.Count == 0 || !string.Equals(held[^1], hit.Path, StringComparison.Ordinal))
            {
                held.Add(hit.Path);
                declarations = tool is null ? null : await DeclarationsAsync(Path.Combine(directory, hit.Path.Replace('/', Path.DirectorySeparatorChar)), again: false, token).ConfigureAwait(false);
                under = null;

                if (declarations is { Moved: > 0 } && tool!.ChecksAgain)
                {
                    moved.Add(declarations.Path);
                }
            }

            if (moved.Count == 0)
            {
                await WrittenAsync(hit, token).ConfigureAwait(false);
            }
        }

        try
        {
            var search = await _host.Files.SearchAsync(directory, pattern, glob, _host.Platform.PathComparison, context, MayRead, Found, cancellationToken).ConfigureAwait(false);

            if (moved.Count > 0)
            {
                // The moved files checked again together, the answer begun again, and the files that held a hit searched
                // again for it - every hit written under its declarations as the files stand now; one that moved again since
                // is given those its lines still confirm.
                _ = await tool!.ReadAgainAsync(moved, cancellationToken).ConfigureAwait(false);
                await lines.DisposeAsync().ConfigureAwait(false);

                lines = _records.Lines(callId);
                var again = held.ToHashSet(StringComparer.Ordinal);
                string? file = null;
                any = false;

                async ValueTask FoundAgain(AgentSearchHit hit, CancellationToken token)
                {
                    if (!string.Equals(file, hit.Path, StringComparison.Ordinal))
                    {
                        file = hit.Path;
                        declarations = await DeclarationsAsync(Path.Combine(directory, hit.Path.Replace('/', Path.DirectorySeparatorChar)), again: false, token).ConfigureAwait(false);
                        under = null;
                    }

                    await WrittenAsync(hit, token).ConfigureAwait(false);
                }

                _ = await _host.Files.SearchAsync(directory, pattern.IsMatch, again.Contains, context, MayRead, FoundAgain, read: null, cancellationToken).ConfigureAwait(false);
            }

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
        finally
        {
            await lines.DisposeAsync().ConfigureAwait(false);
        }
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
