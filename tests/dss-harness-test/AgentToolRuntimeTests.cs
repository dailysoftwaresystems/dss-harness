using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents;
using DssHarness.Core.Application;
using DssHarness.Core.Codebase;
using DssHarness.Core.Composition;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Web;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// The harness's own tools, run for one agent's run on real trees: each call decided before it runs - its tool among
/// those given, its input held to the schema, each path held to the roots and asked of the policy - then run, its answer
/// masked and paged, and recorded.
/// </summary>
public sealed class AgentToolRuntimeTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();
    private readonly RecordedEvents _events = new();
    private readonly List<IDisposable> _opened = [];

    /// <summary>The first cut of a turn stands: what cuts it later answers no call, and a cut says why.</summary>
    [Fact]
    public void TheFirstCutOfATurn_Stands()
    {
        using var cut = new AgentTurnCut();

        Assert.Equal((null, false), (cut.Why, cut.Token.IsCancellationRequested));

        cut.Cut("the run is pausing");
        cut.Cut("the step ran past its time");

        Assert.Equal(("the run is pausing", true), (cut.Why, cut.Token.IsCancellationRequested));
        Assert.Throws<ArgumentException>(() => cut.Cut(" "));
    }

    /// <summary>The indexes of the last search a test opened, whose fills it waits for by disposing them.</summary>
    private DssHarness.Core.Web.Index.WebIndexes? _indexes;
    private readonly string _worktree;
    private readonly string _main;
    private readonly string _outside;
    private readonly string _extra;
    private readonly string _step;

    public AgentToolRuntimeTests()
    {
        // By their real paths, so a temporary root reached through a link (macOS's /var) says nothing of the links here.
        string Real(string name) => _harness.FileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine(name)).FullName);

        _worktree = Real("worktree");
        _main = Real("main");
        _outside = Real("outside");
        _extra = Real("extra");
        _step = Path.Combine(Real("run"), "step");
    }

    public void Dispose()
    {
        foreach (var opened in _opened)
        {
            opened.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>A step is offered the tools of its capabilities, by name; the run's reporter the four that read and no other.</summary>
    [Fact]
    public void TheToolsOffered_AreThoseOfTheStepsCapabilities()
    {
        Assert.Equal(
            [AgentTools.CreateDirectory, AgentTools.CreateFile, AgentTools.DeleteDirectory, AgentTools.DeleteFile, AgentTools.FindFiles, AgentTools.InsertInFile,
             AgentTools.MovePath, AgentTools.ReadResult, AgentTools.ReplaceInFile, AgentTools.RunCommand, AgentTools.SearchFiles, AgentTools.ViewFile, AgentTools.WebFetch],
            Names(Open()));
        Assert.Equal([AgentTools.FindFiles, AgentTools.ReadResult, AgentTools.SearchFiles, AgentTools.ViewFile], Names(Open(capabilities: [AgentCapability.Read])));
        Assert.Equal([AgentTools.FindFiles, AgentTools.ReadResult, AgentTools.SearchFiles, AgentTools.ViewFile, AgentTools.WebFetch], Names(Open(capabilities: [AgentCapability.Read, AgentCapability.Web])));
        Assert.Equal([AgentTools.FindFiles, AgentTools.ReadResult, AgentTools.SearchFiles, AgentTools.ViewFile], Names(Open(reporterRoot: _worktree)));

        static IEnumerable<string> Names(AgentToolRuntime runtime) => runtime.Tools.Select(tool => tool.Name);
    }

    /// <summary>
    /// The run's measure tool, where its run is given one: offered among the harness's own to a ground that runs
    /// commands - one that does not is not offered it, nor is the run's reporter, by the one rule the Claude Code client
    /// serves it by - standing alone in its turn (Barrier), for a measurement is a build and a test run, never a read
    /// beside others. Its call is handed its own id, as the stored stream records it, and where its records go - the
    /// call's own folder of the step's directory, by that id - and is answered with what the cell showed, the leg, the
    /// variant, the filter and the cell said back with where the run's records and the call's are, so a record's
    /// <c>measuredBy</c> cites a call whose input the run's stored stream holds whole; what it answered is kept, masked,
    /// in the call's own records, read back by the id alone. A cell that could not be measured is a failed call carrying
    /// the reason, which the agent names unmeasured; a call of another tool, or an id no measure call made, has no record,
    /// and a record that names no cell reads as none.
    /// </summary>
    [Fact]
    public async Task TheRunsMeasureTool_IsOfferedToAGroundThatRunsCommands_AndItsCallAnswersWhatTheCellShowed()
    {
        AgentMeasureCall? asked = null;
        var runtime = Open(
            mask: new Masking("s3cr3t-value", "***"),
            measure: new((call, _) =>
            {
                asked = call;
                return Task.FromResult(call.Cell == "the good cell"
                    ? new AgentMeasureAnswer(Measured: true, Passed: true, "passed", "the tests passed with s3cr3t-value", null) { RunDirectory = "/runs/r1" }
                    : new AgentMeasureAnswer(false, false, string.Empty, null, "the tree's lock was held"));
            }));

        Assert.Contains(AgentMeasureSupport.ToolName, runtime.Tools.Select(tool => tool.Name));
        Assert.DoesNotContain(AgentMeasureSupport.ToolName, Open(capabilities: [AgentCapability.Read], measure: new((_, _) => Task.FromResult(new AgentMeasureAnswer(true, true, "passed", null, null)))).Tools.Select(tool => tool.Name));
        Assert.DoesNotContain(AgentMeasureSupport.ToolName, Open(reporterRoot: _worktree, measure: new((_, _) => Task.FromResult(new AgentMeasureAnswer(true, true, "passed", null, null)))).Tools.Select(tool => tool.Name));

        // It stands alone: after every call before it and before every call after.
        Assert.True(runtime.Alone(new AgentToolCallRequest("call_2", AgentMeasureSupport.ToolName, """{"leg":"windows","variant":"debug","filter":"","cell":"x"}""")));
        Assert.True(runtime.Locks(new AgentToolCallRequest("call_3", AgentMeasureSupport.ToolName, """{"leg":"windows","variant":"debug","filter":"","cell":"x"}""")));

        var passed = await Call(runtime, AgentMeasureSupport.ToolName, "toolu_01:A", ("leg", "windows"), ("variant", "debug"), ("filter", "Shape"), ("cell", "the good cell"));
        var records = Path.Combine(_step, AgentMeasureSupport.RecordsDirectoryName, "toolu_01_A");

        Assert.True(passed.Ok, passed.Text);
        Assert.Equal(new AgentMeasureCall("toolu_01:A", "windows", "debug", "Shape", "the good cell", records), asked);
        Assert.Equal(records, AgentMeasureSupport.RecordsOf(_step, "toolu_01:A"));
        Assert.Equal(Path.Combine(records, AgentMeasureSupport.RecordFileName), AgentMeasureSupport.RecordFileOf(_step, "toolu_01:A"));

        var said = JsonNode.Parse(passed.Text)!;

        Assert.Equal(
            new JsonObject
            {
                ["leg"] = "windows",
                ["variant"] = "debug",
                ["filter"] = "Shape",
                ["cell"] = "the good cell",
                ["result"] = "passed",
                ["verdict"] = "passed",
                ["detail"] = "the tests passed with ***",
                ["runDirectory"] = "/runs/r1",
                ["records"] = records,
            }.ToJsonString(),
            said.ToJsonString());

        // What it answered, kept masked in the call's own records and read back by its id alone.
        Assert.Equal(said.ToJsonString(), AgentMeasureSupport.RecordOf(_harness.FileSystem, _step, "toolu_01:A")!.ToJsonString());
        Assert.DoesNotContain("s3cr3t-value", File.ReadAllText(Path.Combine(records, AgentMeasureSupport.RecordFileName)), StringComparison.Ordinal);

        var unmeasured = await Call(runtime, AgentMeasureSupport.ToolName, "call_2", ("leg", "windows"), ("variant", "debug"), ("filter", ""), ("cell", "the held cell"));

        Assert.False(unmeasured.Ok);

        var held = JsonNode.Parse(unmeasured.Text)!;

        Assert.Equal("unmeasured", held!["result"]!.GetValue<string>());
        Assert.Equal("the tree's lock was held", held["reason"]!.GetValue<string>());
        Assert.Null(held["runDirectory"]);
        Assert.Equal("unmeasured", AgentMeasureSupport.RecordOf(_harness.FileSystem, _step, "call_2")!["result"]!.GetValue<string>());

        // A call of another tool, and an id no call made, leave no measure's record.
        _ = await View(runtime, "nothing-here.txt");
        Assert.Null(AgentMeasureSupport.RecordOf(_harness.FileSystem, _step, "call_1"));
        Assert.Null(AgentMeasureSupport.RecordOf(_harness.FileSystem, _step, "toolu_never"));

        // A record that names no cell reads as none: what a call measured is its cell and its result, both.
        Directory.CreateDirectory(AgentMeasureSupport.RecordsOf(_step, "toolu_cellless"));
        File.WriteAllText(AgentMeasureSupport.RecordFileOf(_step, "toolu_cellless"), """{"leg":"windows","result":"passed"}""" + "\n");

        Assert.Null(AgentMeasureSupport.RecordOf(_harness.FileSystem, _step, "toolu_cellless"));
    }

    /// <summary>A call of a tool the step was not given is refused, and recorded as every denial is: a line, an event, its end.</summary>
    [Fact]
    public async Task ACallOfAToolNotGiven_IsRefused_AndRecorded()
    {
        var runtime = Open(capabilities: [AgentCapability.Read]);

        var result = await Call(runtime, AgentTools.RunCommand, ("program", "git"));

        Assert.False(result.Ok);
        Assert.Equal("'run_command' is no tool this step is given: it is given find_files, read_result, search_files, view_file", result.Text);

        var denied = Assert.Single(_events.All.OfType<PermissionDenied>());
        Assert.Equal((AgentTools.RunCommand, AgentTools.RunCommand, AgentToolRuntime.ToolsRule), (denied.Tool, denied.Subject, denied.Rule));

        var finished = Assert.Single(_events.All.OfType<ToolCallFinished>());
        Assert.Equal(("call_1", false, result.Text), (finished.Id, finished.Ok, finished.Result));

        Assert.Equal((1, 1), (runtime.Tally.Calls, runtime.Tally.Denials));

        runtime.Dispose();
        var line = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(_step, AgentCallRecords.DenialsFileName))))!;
        Assert.Equal(AgentToolRuntime.ToolsRule, line["rule"]!.GetValue<string>());
    }

    /// <summary>An input that is no JSON, or does not hold to the tool's schema, fails and says how the tool is called.</summary>
    [Fact]
    public async Task AnInputThatDoesNotHold_Fails_AndSaysHowTheToolIsCalled()
    {
        var runtime = Open();
        const string Usage = "view_file takes path (string), start_line? (integer), end_line? (integer)";

        var missing = await runtime.RunAsync(new AgentToolCallRequest("a", AgentTools.ViewFile, "{}"), Token);
        Assert.False(missing.Ok);
        Assert.Equal($"view_file's input does not hold: $: 'path' is required and missing; {Usage}", missing.Text);

        var extra = await runtime.RunAsync(new AgentToolCallRequest("b", AgentTools.ViewFile, """{"path":"a","more":1}"""), Token);
        Assert.Equal($"view_file's input does not hold: $.more: is said by no property of the schema; {Usage}", extra.Text);

        var typed = await runtime.RunAsync(new AgentToolCallRequest("c", AgentTools.ViewFile, """{"path":7}"""), Token);
        Assert.Equal($"view_file's input does not hold: $.path: 7 is not a string; {Usage}", typed.Text);

        var unread = await runtime.RunAsync(new AgentToolCallRequest("d", AgentTools.ViewFile, "not json"), Token);
        Assert.StartsWith("view_file's input does not read as JSON (", unread.Text, StringComparison.Ordinal);
        Assert.EndsWith(Usage, unread.Text, StringComparison.Ordinal);

        // Half of a surrogate pair the parser lets by, which no string holds: refused with the input, never met by a tool.
        var half = await runtime.RunAsync(new AgentToolCallRequest("e", AgentTools.ViewFile, """{"path":"\ud83d"}"""), Token);
        Assert.False(half.Ok);
        Assert.StartsWith("view_file's input does not read as JSON (", half.Text, StringComparison.Ordinal);
        Assert.EndsWith(Usage, half.Text, StringComparison.Ordinal);

        Assert.Equal(0, runtime.Tally.Denials);
    }

    /// <summary>
    /// A file is viewed numbered, a page at a time - each page saying which of how many it is and how to ask for the next -
    /// or from a line to a line within the page that holds the first, saying how to ask for the rest; a directory is listed.
    /// </summary>
    [Fact]
    public async Task AView_NumbersItsLines_AndListsADirectory()
    {
        Write("a.txt", "one\ntwo\nthree\n");
        Write("long.txt", string.Concat(Enumerable.Range(1, 5000).Select(line => $"l{line}\n")));
        Write("empty.txt", string.Empty);
        Directory.CreateDirectory(Path.Combine(_worktree, "src"));
        File.WriteAllBytes(Path.Combine(_worktree, "bin.dat"), [0x61, 0x00, 0x62]);
        var runtime = Open();

        Assert.Equal("1\tone\n2\ttwo\n3\tthree", (await View(runtime, "a.txt")).Text);
        Assert.Equal("2\ttwo", (await Call(runtime, AgentTools.ViewFile, ("path", "a.txt"), ("start_line", 2), ("end_line", 2))).Text);
        Assert.Equal("3\tthree", (await Call(runtime, AgentTools.ViewFile, ("path", "a.txt"), ("start_line", 3))).Text);
        Assert.Equal("'a.txt' has 3 line(s): start_line 4 is past its end", (await Call(runtime, AgentTools.ViewFile, ("path", "a.txt"), ("start_line", 4))).Text);
        Assert.Equal("end_line 1 is before start_line 2", (await Call(runtime, AgentTools.ViewFile, ("path", "a.txt"), ("start_line", 2), ("end_line", 1))).Text);

        // A number past what a double holds is past the file's end, never a fault.
        var huge = await runtime.RunAsync(new AgentToolCallRequest("h", AgentTools.ViewFile, """{"path":"a.txt","start_line":1e400}"""), Token);
        Assert.Equal($"'a.txt' has 3 line(s): start_line {long.MaxValue} is past its end", huge.Text);

        var first = (await View(runtime, "long.txt")).Text;
        Assert.StartsWith("page 1 of 2 · 5000 results · ask again with page: 2 for the next\n1\tl1\n", first, StringComparison.Ordinal);
        var lastOfFirst = int.Parse(first.Split('\n')[^1].Split('\t')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"{lastOfFirst}\tl{lastOfFirst}", first.Split('\n')[^1]);

        var second = await Call(runtime, AgentTools.ViewFile, ("path", "long.txt"), ("page", 2));
        Assert.Null(second.File);
        Assert.StartsWith($"page 2 of 2 · the last\n{lastOfFirst + 1}\tl{lastOfFirst + 1}\n", second.Text, StringComparison.Ordinal);
        Assert.EndsWith("\n5000\tl5000", second.Text, StringComparison.Ordinal);
        Assert.Equal("page 3 of 'view_file long.txt': it has 2 pages (1 to 2)", (await Call(runtime, AgentTools.ViewFile, ("path", "long.txt"), ("page", 3))).Text);
        Assert.Equal("page is 0: pages start at 1", (await Call(runtime, AgentTools.ViewFile, ("path", "long.txt"), ("page", 0))).Text);
        Assert.Equal(
            "page and start_line or end_line are two ways to ask for a part of a file: give page alone, or a range",
            (await Call(runtime, AgentTools.ViewFile, ("path", "long.txt"), ("page", 1), ("start_line", 2))).Text);

        Assert.EndsWith(
            $"{lastOfFirst}\tl{lastOfFirst}\n[lines 1-{lastOfFirst} of 5000; ask again with start_line {lastOfFirst + 1}]",
            (await Call(runtime, AgentTools.ViewFile, ("path", "long.txt"), ("end_line", 5000))).Text,
            StringComparison.Ordinal);
        var rest = (await Call(runtime, AgentTools.ViewFile, ("path", "long.txt"), ("start_line", lastOfFirst + 1))).Text;
        Assert.StartsWith($"{lastOfFirst + 1}\tl{lastOfFirst + 1}\n", rest, StringComparison.Ordinal);
        Assert.EndsWith("\n5000\tl5000", rest, StringComparison.Ordinal);
        Assert.Equal("4999\tl4999\n5000\tl5000", (await Call(runtime, AgentTools.ViewFile, ("path", "long.txt"), ("start_line", 4999), ("end_line", 5000))).Text);

        Assert.Equal("'empty.txt' is empty", (await View(runtime, "empty.txt")).Text);
        Assert.Equal($"'bin.dat' is binary: {EncodedText.BinaryWhy}", (await View(runtime, "bin.dat")).Text);
        Assert.Equal("there is no file or directory at 'nope.txt'", (await View(runtime, "nope.txt")).Text);
        Assert.Equal("./ holds 5 entry(ies):\na.txt\nbin.dat\nempty.txt\nlong.txt\nsrc/", (await View(runtime, ".")).Text);
        Assert.Equal("src/ is empty", (await View(runtime, "src")).Text);
    }

    /// <summary>
    /// A line of 30 MB - a minified bundle's - is reachable page by page: page 1 gives its first piece numbered and marked
    /// where it goes on, each page after it the next piece, marked where it went on and goes on, and the pieces, read
    /// one after another, are the line whole; a range that starts at it says on which page it goes on.
    /// </summary>
    [Fact]
    public async Task AThirtyMegabyteLine_IsReachablePageByPage()
    {
        var line = string.Concat(Enumerable.Range(0, 30 * 1024 * 1024 / 8).Select(at => (at % 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture)));
        Write("bundle.min.js", line + "\nafter\n");
        var runtime = Open();

        var first = await View(runtime, "bundle.min.js");
        var header = first.Text.Split('\n')[0];
        var pages = int.Parse(header.Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"page 1 of {pages} · 2 results · ask again with page: 2 for the next", header);
        Assert.True(pages > 1000, $"a 30 MB line is {pages} pages");
        Assert.Null(first.File);

        var given = new StringBuilder();
        var piece = first.Text.Split('\n')[1];
        Assert.StartsWith("1\t", piece, StringComparison.Ordinal);
        Assert.EndsWith("…", piece, StringComparison.Ordinal);
        given.Append(piece[2..^1]);

        for (var page = 2; page <= pages; page++)
        {
            var text = (await Call(runtime, AgentTools.ViewFile, $"page_{page}", ("path", "bundle.min.js"), ("page", page))).Text;
            Assert.True(text.Length <= DssHarness.Core.Pages.PageRoom.ModelAnswer, $"page {page} is {text.Length} characters");
            var body = text.Split('\n')[1];

            // Each page goes on with the line's next piece, until a page begins with the line after it.
            if (body.StartsWith('…'))
            {
                given.Append(body.EndsWith('…') ? body[1..^1] : body[1..]);
            }
            else
            {
                Assert.Equal(("2\tafter", pages), (body, page));
            }

            if (page == pages)
            {
                Assert.EndsWith("\n2\tafter", text, StringComparison.Ordinal);
            }
        }

        Assert.Equal(line, given.ToString());
        Assert.Equal(
            $"1\t{piece[2..]}\n[line 1 goes on: ask again with page: 2]",
            (await Call(runtime, AgentTools.ViewFile, ("path", "bundle.min.js"), ("start_line", 1), ("end_line", 1))).Text);
    }

    /// <summary>
    /// A directory's entries are given whole, every one, from a start to an end where they are given: a listing longer
    /// than a page is the call's answer kept whole and given as its first page, every page of it by read_result.
    /// </summary>
    [Fact]
    public async Task ADirectoryOfManyEntries_IsGivenWhole_APageAtATime()
    {
        var names = Enumerable.Range(0, 400).Select(at => $"{at:D4}{new string('n', 116)}").ToList();

        foreach (var name in names)
        {
            Write(Path.Combine("many", name), string.Empty);
        }

        var runtime = Open();
        var heading = "many/ holds 400 entry(ies):";

        var listed = await Call(runtime, AgentTools.ViewFile, "listing", ("path", "many"));
        Assert.Equal(AgentCallRecords.ResultFile(_step, "listing"), listed.File);
        Assert.Equal($"{heading}\n{string.Join('\n', names)}", File.ReadAllText(listed.File!));
        Assert.StartsWith("page 1 of 2 · 401 results · ask read_result with id: 'listing', page: 2 for the next", listed.Text, StringComparison.Ordinal);

        var next = await Call(runtime, AgentTools.ViewFile, ("path", "many"), ("start_line", 399), ("end_line", 400));
        Assert.Equal($"{heading}\n{string.Join('\n', names.Skip(398))}", next.Text);
        Assert.Equal("many/ holds 400 entry(ies): start_line 401 is past its end", (await Call(runtime, AgentTools.ViewFile, ("path", "many"), ("start_line", 401))).Text);
        Assert.Equal(
            "'many/' is a directory, and page is a file's: its entries are given whole, from start_line to end_line where they are given, a page at a time by read_result where they pass one",
            (await Call(runtime, AgentTools.ViewFile, ("path", "many"), ("page", 2))).Text);
    }

    /// <summary>A path outside the agent's roots is refused by them; one inside a read-only root is read, and said whole.</summary>
    [Fact]
    public async Task AViewOutsideTheRoots_IsRefused_AndARootOfReadingIsRead()
    {
        File.WriteAllText(Path.Combine(_outside, "x.txt"), "x");
        File.WriteAllText(Path.Combine(_main, "m.txt"), "m");
        var runtime = Open();

        var outside = await View(runtime, Path.Combine(_outside, "x.txt"));
        Assert.False(outside.Ok);
        Assert.StartsWith($"'{Path.Combine(_outside, "x.txt")}' is outside the roots of this agent: ", outside.Text, StringComparison.Ordinal);
        Assert.Equal(ConfinedPaths.RootsRule, Assert.Single(_events.All.OfType<PermissionDenied>()).Rule);

        Assert.Equal("1\tm", (await View(runtime, Path.Combine(_main, "m.txt"))).Text);
    }

    /// <summary>
    /// An edit needs its file viewed first, as it stands: never viewed or changed since is refused, nothing written; the
    /// runtime's own write is known, so a second edit needs no view between.
    /// </summary>
    [Fact]
    public async Task AnEdit_NeedsItsFileViewedAsItStands()
    {
        Write("a.txt", "one\ntwo\n");
        var runtime = Open();

        Assert.Equal(AgentToolRuntime.NeverViewed, (await Replace(runtime, "a.txt", "two", "2")).Text);
        Assert.Equal("one\ntwo\n", Read("a.txt"));

        await View(runtime, "a.txt");
        var replaced = await Replace(runtime, "a.txt", "two", "2");
        Assert.True(replaced.Ok);
        Assert.Equal("edited 'a.txt': 2 line(s) now", replaced.Text);

        Assert.True((await Replace(runtime, "a.txt", "one", "1")).Ok);
        Assert.Equal("1\n2\n", Read("a.txt"));

        Write("a.txt", "changed\n");
        Assert.Equal(AgentToolRuntime.ChangedSinceViewed, (await Replace(runtime, "a.txt", "changed", "x")).Text);
        Assert.Equal(AgentToolRuntime.ChangedSinceViewed, (await Call(runtime, AgentTools.InsertInFile, ("path", "a.txt"), ("line", 1), ("text", "zero"))).Text);
        Assert.Equal("changed\n", Read("a.txt"));

        await View(runtime, "a.txt");
        Assert.True((await Call(runtime, AgentTools.InsertInFile, ("path", "a.txt"), ("line", 1), ("text", "zero"))).Ok);
        Assert.True((await Call(runtime, AgentTools.InsertInFile, ("path", "a.txt"), ("line", 3), ("text", "end"))).Ok);
        Assert.Equal("zero\nchanged\nend\n", Read("a.txt"));
        Assert.Equal("line is 9, and the file has 3 line(s): give 1 to 4, one past the last to append", (await Call(runtime, AgentTools.InsertInFile, ("path", "a.txt"), ("line", 9), ("text", "x"))).Text);
    }

    /// <summary>
    /// A file link out of the roots - a tracked notes.md leading to a key under the home - is read, written and searched by
    /// no tool: a view, a replace, an insert and an overwrite refuse it by where it leads, nothing written, and a search and
    /// a find never list a link, so the text it leads to is in no answer.
    /// </summary>
    [Fact]
    public async Task AFileLinkOutOfTheRoots_IsReachedByNoTool()
    {
        var key = Path.Combine(_outside, "id_rsa");
        var notes = Path.Combine(_worktree, "notes.md");
        File.WriteAllText(key, "BEGIN PRIVATE KEY");
        Write("plain.md", "BEGIN nothing");
        TestLinks.OrSkip(() => File.CreateSymbolicLink(notes, key));
        var runtime = Open();
        var unread = $"'{notes}' leads to '{key}', outside the roots of this agent: {_worktree}, {_main}";
        var unwritten = $"'{notes}' leads to '{key}', outside the writable roots of this agent: {_worktree}";

        foreach (var (answer, refused) in new[]
        {
            (await View(runtime, "notes.md"), unread),
            (await Replace(runtime, "notes.md", "BEGIN", "END"), unwritten),
            (await Call(runtime, AgentTools.InsertInFile, ("path", "notes.md"), ("line", 1), ("text", "x")), unwritten),
            (await Call(runtime, AgentTools.CreateFile, ("path", "notes.md"), ("content", "x"), ("overwrite", true)), unwritten),
        })
        {
            Assert.Equal((false, refused), (answer.Ok, answer.Text));
        }

        Assert.Equal("plain.md:1: BEGIN nothing", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "BEGIN"))).Text);
        Assert.Equal("plain.md", (await Call(runtime, AgentTools.FindFiles, ("pattern", "*.md"))).Text);
        Assert.Equal("BEGIN PRIVATE KEY", File.ReadAllText(key));
        Assert.Equal([ConfinedPaths.RootsRule, ConfinedPaths.RootsRule, ConfinedPaths.RootsRule, ConfinedPaths.RootsRule], _events.All.OfType<PermissionDenied>().Select(denied => denied.Rule));
    }

    /// <summary>
    /// A defect of the tool's own - a guard's refusal of a null or of a value out of its range, as a record's line holding a
    /// line break is - is raised, never told the model as the tool's failure; a file system's fault is told it.
    /// </summary>
    [Fact]
    public async Task ADefectOfTheToolsOwn_IsRaised_NeverToldTheModel()
    {
        Write("null.txt", "x");
        Write("range.txt", "x");
        Write("held.txt", "x");
        var runtime = Open(fileSystem: new FaultingStandings(_harness.FileSystem));

        await Assert.ThrowsAsync<ArgumentNullException>(() => View(runtime, "null.txt"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => View(runtime, "range.txt"));

        var told = await View(runtime, "held.txt");
        Assert.Equal((false, "view_file failed: it is held by another process"), (told.Ok, told.Text));
    }

    /// <summary>A replace says why it would not: text not there, there more than once, empty, or the same as its replacement.</summary>
    [Fact]
    public async Task AReplace_SaysWhyItWouldNot()
    {
        Write("a.txt", "x\nx\ny\n");
        var runtime = Open();
        await View(runtime, "a.txt");

        Assert.Equal("old_string is not in the file: view it again, and give the text exactly as it stands", (await Replace(runtime, "a.txt", "z", "w")).Text);
        Assert.Equal("old_string stands 2 times in the file: give more of the text around it so it stands once, or replace_all: true", (await Replace(runtime, "a.txt", "x", "w")).Text);
        Assert.Equal("old_string is empty: give the text to replace, as the file holds it", (await Replace(runtime, "a.txt", string.Empty, "w")).Text);
        Assert.Equal("old_string and new_string are the same: nothing would change", (await Replace(runtime, "a.txt", "y", "y")).Text);

        Assert.True((await Call(runtime, AgentTools.ReplaceInFile, ("path", "a.txt"), ("old_string", "x"), ("new_string", "w"), ("replace_all", true))).Ok);
        Assert.Equal("w\nw\ny\n", Read("a.txt"));
    }

    /// <summary>An edit keeps the file's encoding, its mark and its CR LF endings, text given with LF written in them.</summary>
    [Fact]
    public async Task AnEdit_KeepsTheFilesEncodingAndEndings()
    {
        var path = Path.Combine(_worktree, "w.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("a\r\nb\r\n")]);
        var runtime = Open();
        await View(runtime, "w.txt");

        Assert.True((await Replace(runtime, "w.txt", "a\nb", "x\ny\nz")).Ok);

        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("x\r\ny\r\nz\r\n")], File.ReadAllBytes(path));
    }

    /// <summary>A file is created in UTF-8 with no mark, the directories on its way made; one there is written over only when asked.</summary>
    [Fact]
    public async Task CreateFile_WritesANewFile_AndOverwritesOnlyWhenAsked()
    {
        var runtime = Open();

        var made = await Call(runtime, AgentTools.CreateFile, ("path", "deep/er/new.txt"), ("content", "hi\n"));
        Assert.Equal("wrote 'deep/er/new.txt' (3 bytes)", made.Text);
        Assert.Equal("hi\n"u8.ToArray(), File.ReadAllBytes(Path.Combine(_worktree, "deep", "er", "new.txt")));

        Assert.Equal("'deep/er/new.txt' is there already: overwrite: true writes over it", (await Call(runtime, AgentTools.CreateFile, ("path", "deep/er/new.txt"), ("content", "x"))).Text);
        Assert.True((await Call(runtime, AgentTools.CreateFile, ("path", "deep/er/new.txt"), ("content", "x"), ("overwrite", true))).Ok);
        Assert.Equal("x", Read("deep/er/new.txt"));
        Assert.Equal("'deep' is a directory", (await Call(runtime, AgentTools.CreateFile, ("path", "deep"), ("content", "x"))).Text);

        // A file the runtime wrote is one the agent knows: it is edited with no view between.
        Assert.True((await Replace(runtime, "deep/er/new.txt", "x", "y")).Ok);

        Assert.Equal(3, runtime.Tally.FilesWritten);
    }

    /// <summary>A write outside the writable roots, or against a Write rule, is refused, nothing made on its way.</summary>
    [Fact]
    public async Task AWrite_IsRefused_ByTheRootsAndTheRules()
    {
        var runtime = Open(restrictions: [Restriction("Write(locked/**)")]);

        var readOnly = await Call(runtime, AgentTools.CreateFile, ("path", Path.Combine(_main, "x.txt")), ("content", "x"));
        Assert.Equal($"'{Path.Combine(_main, "x.txt")}' is outside the writable roots of this agent: {_worktree}", readOnly.Text);

        var locked = await Call(runtime, AgentTools.CreateFile, ("path", "locked/x.txt"), ("content", "x"));
        Assert.Matches(@"^'.+' is denied by Write\(locked/\*\*\), a restriction of .+$", locked.Text);

        var denials = _events.All.OfType<PermissionDenied>().ToList();
        Assert.Equal([ConfinedPaths.RootsRule, "Write(locked/**)"], denials.Select(denial => denial.Rule));
        Assert.Equal("locked/x.txt", denials[1].Subject);
        Assert.False(File.Exists(Path.Combine(_main, "x.txt")));
        Assert.False(Directory.Exists(Path.Combine(_worktree, "locked")));
    }

    /// <summary>
    /// The directories made on a write's way are part of that write: a step whose writes name a file makes the directories
    /// it needs, though a directory of its own is outside them.
    /// </summary>
    [Fact]
    public async Task TheDirectoriesOnAWritesWay_ArePartOfIt()
    {
        var runtime = Open(writes: ["made/deeper/x.txt"]);

        Assert.Equal("wrote 'made/deeper/x.txt' (1 byte)", (await Call(runtime, AgentTools.CreateFile, ("path", "made/deeper/x.txt"), ("content", "x"))).Text);
        Assert.False((await Call(runtime, AgentTools.CreateDirectory, ("path", "other"))).Ok);
        Assert.Equal("the step's writes", Assert.Single(_events.All.OfType<PermissionDenied>()).Rule);
        Assert.False(Directory.Exists(Path.Combine(_worktree, "other")));
    }

    /// <summary>A directory is made with every one on its way; one there is no error, a file there is.</summary>
    [Fact]
    public async Task CreateDirectory_MakesItsWay()
    {
        Write("f.txt", "x");
        var runtime = Open();

        Assert.Equal("made 'a/b/c'", (await Call(runtime, AgentTools.CreateDirectory, ("path", "a/b/c"))).Text);
        Assert.True(Directory.Exists(Path.Combine(_worktree, "a", "b", "c")));
        Assert.Equal("'a/b/c' is there already", (await Call(runtime, AgentTools.CreateDirectory, ("path", "a/b/c"))).Text);
        Assert.Equal("'f.txt' is a file", (await Call(runtime, AgentTools.CreateDirectory, ("path", "f.txt"))).Text);
    }

    /// <summary>A file is deleted as a write; one not there, or a directory, is an error.</summary>
    [Fact]
    public async Task DeleteFile_DeletesOneFile_AsAWrite()
    {
        Write("a.txt", "x");
        Directory.CreateDirectory(Path.Combine(_worktree, "src"));
        File.WriteAllText(Path.Combine(_main, "m.txt"), "m");
        var runtime = Open(restrictions: [Restriction("Write(kept.txt)")]);
        Write("kept.txt", "k");

        Assert.Equal("deleted 'a.txt'", (await Call(runtime, AgentTools.DeleteFile, ("path", "a.txt"))).Text);
        Assert.False(File.Exists(Path.Combine(_worktree, "a.txt")));
        Assert.Equal("there is no file at 'a.txt'", (await Call(runtime, AgentTools.DeleteFile, ("path", "a.txt"))).Text);
        Assert.Equal("'src' is a directory: delete_directory deletes one", (await Call(runtime, AgentTools.DeleteFile, ("path", "src"))).Text);

        Assert.False((await Call(runtime, AgentTools.DeleteFile, ("path", Path.Combine(_main, "m.txt")))).Ok);
        Assert.False((await Call(runtime, AgentTools.DeleteFile, ("path", "kept.txt"))).Ok);
        Assert.True(File.Exists(Path.Combine(_main, "m.txt")));
        Assert.True(File.Exists(Path.Combine(_worktree, "kept.txt")));
        Assert.Equal(2, runtime.Tally.Denials);
    }

    /// <summary>An empty directory is deleted; one that holds entries only with recursive: true.</summary>
    [Fact]
    public async Task DeleteDirectory_DeletesWhatItHolds_OnlyWhenAsked()
    {
        Directory.CreateDirectory(Path.Combine(_worktree, "empty"));
        Write("full/a.txt", "a");
        Write("full/sub/b.txt", "b");
        var runtime = Open();

        Assert.Equal("deleted 'empty' and the 0 entry(ies) it held", (await Call(runtime, AgentTools.DeleteDirectory, ("path", "empty"))).Text);
        Assert.Equal("'full' holds 3 entry(ies): recursive: true deletes them with it", (await Call(runtime, AgentTools.DeleteDirectory, ("path", "full"))).Text);
        Assert.True(Directory.Exists(Path.Combine(_worktree, "full")));

        Assert.Equal("deleted 'full' and the 3 entry(ies) it held", (await Call(runtime, AgentTools.DeleteDirectory, ("path", "full"), ("recursive", true))).Text);
        Assert.False(Directory.Exists(Path.Combine(_worktree, "full")));
        Assert.Equal("there is no directory at 'full'", (await Call(runtime, AgentTools.DeleteDirectory, ("path", "full"))).Text);
    }

    /// <summary>
    /// A file whose open stands somewhere its path did not lead when it was decided - a link put on its way between the
    /// check and the open - is decided again where the open file stands before a byte passes: a view, an edit and a search
    /// read nothing outside the roots or in a Claude Code directory; and a write whose directory, held, stands outside the
    /// writable roots - moved there since it was decided - lands nothing there, the target as it was. Each refusal is
    /// recorded as every denial is.
    /// </summary>
    [Fact]
    public async Task AFileWhoseOpenStandsElsewhere_IsDecidedAgain_BeforeAByte()
    {
        var claude = Path.Combine(_main, "cc");
        var secret = Path.Combine(_outside, "id_rsa");
        var credential = Path.Combine(claude, ".credentials.json");
        Write("a.txt", "BEGIN plain\n");
        Write("b.txt", "BEGIN kept\n");
        var files = new OpenedElsewhere(_harness.FileSystem, new Dictionary<string, string>(StringComparer.Ordinal) { ["a.txt"] = secret, ["b.txt"] = credential }, _outside);
        var runtime = Open(fileSystem: files, claudeCodeDirectories: [claude]);
        var a = Path.Combine(_worktree, "a.txt");
        var b = Path.Combine(_worktree, "b.txt");
        var c = Path.Combine(_worktree, "c.txt");
        var policy = new AgentPolicy(_harness.FileSystem, _harness.Platform, PolicyGrounds.Of(_worktree, [_main], [_worktree], [], [claude], [], null, null, null, null));

        string Unread(string asked, string opened, string why) => $"'{asked}' was opened as '{opened}', where a link put on its way after it was decided leads; nothing was read: {why}";

        Assert.Equal(
            (false, Unread(a, secret, $"'{secret}' is where the open file stands, outside the roots of this agent: {_worktree}, {_main}")),
            Said(await View(runtime, "a.txt")));
        Assert.Equal((false, Unread(b, credential, policy.Read(credential).Refusal()!.Reason)), Said(await View(runtime, "b.txt")));
        Assert.Equal(
            (false, Unread(a, secret, $"'{secret}' is where the open file stands, outside the writable roots of this agent: {_worktree}")),
            Said(await Replace(runtime, "a.txt", "BEGIN", "END")));
        Assert.Equal(
            (true, $"no line matches, in {ReportText.Counted(0, "file")} read\n{AgentListing.Refused(2)}"),
            Said(await Call(runtime, AgentTools.SearchFiles, ("pattern", "BEGIN"))));

        var landing = Path.Combine(_outside, "c.txt");
        Assert.Equal(
            (false, $"'{c}' would be written as '{landing}', where a link put on its way after it was decided leads; nothing was written: '{landing}' is where the open file stands, outside the writable roots of this agent: {_worktree}"),
            Said(await Call(runtime, AgentTools.CreateFile, ("path", "c.txt"), ("content", "x"))));

        Assert.Equal(["a.txt", "b.txt"], Directory.GetFiles(_worktree).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["BEGIN plain\n", "BEGIN kept\n"], [this.Read("a.txt"), this.Read("b.txt")]);
        Assert.Empty(Directory.GetFiles(_outside));
        Assert.Equal(4, _events.All.OfType<PermissionDenied>().Count());

        static (bool, string) Said(AgentToolCallResult result) => (result.Ok, result.Text);
    }

    /// <summary>
    /// A directory made a real link at the moment a file under it is opened - after the file's path was decided - is met
    /// by where the open file stands, as the system names it, for a view of the file, which reads nothing from where the
    /// link leads; and by the write's own walk to its directory, which opens each directory on the way as itself and meets
    /// the link, so a file written under it is refused naming the link and lands nothing there.
    /// </summary>
    [Fact]
    public async Task ADirectoryMadeALink_AtAFilesOpen_LeadsNoReadOrWriteOutOfTheRoots()
    {
        Write("docs/a.txt", "plain");
        File.WriteAllText(Path.Combine(_outside, "a.txt"), "SECRET");
        var docs = Path.Combine(_worktree, "docs");
        var runtime = Open(fileSystem: new LinkedAtOpen(_harness.FileSystem, docs, _outside));
        var opened = Path.Combine(_outside, "a.txt");

        var view = await View(runtime, "docs/a.txt");

        Assert.Equal(
            (false, $"'{Path.Combine(docs, "a.txt")}' was opened as '{opened}', where a link put on its way after it was decided leads; nothing was read: '{opened}' is where the open file stands, outside the roots of this agent: {_worktree}, {_main}"),
            (view.Ok, view.Text));

        // The link is back to the directory it was: a write under it is begun where the link leads at its open.
        Directory.Delete(docs);
        Directory.Move(docs + "-was", docs);
        var landing = Path.Combine(_outside, "b.txt");

        var written = await Call(runtime, AgentTools.CreateFile, ("path", "docs/b.txt"), ("content", "x"));

        Assert.Equal(
            (false, $"'{Path.Combine(docs, "b.txt")}' is reached through the link '{docs}', which the tool never follows in a place an isolated process writes; nothing was done"),
            (written.Ok, written.Text));
        Assert.False(File.Exists(landing));
        Assert.Equal(["a.txt"], Directory.GetFiles(_outside).Select(Path.GetFileName));
        Assert.Equal("SECRET", File.ReadAllText(opened));
    }

    /// <summary>
    /// A command left running in the background that swaps a directory of the worktree for a link to a place outside every
    /// root, back and forth, while the agent's tools delete in it, move into it and write in it, leads no tool outside: each
    /// acts relative to the directory held from the writable root it was decided in, opened as itself, so a call decided
    /// while the directory was one and acting once the link is there is refused, and the user's file outside stays, nothing
    /// landing beside it.
    /// </summary>
    [Fact]
    public async Task ALinkSwappedOnTheWayWhileTheToolsAct_LeadsNoToolOutOfTheRoots()
    {
        var runtime = Open();
        var x = Path.Combine(_worktree, "x");
        var real = Path.Combine(_worktree, "x-dir");
        var link = Path.Combine(_worktree, "x-link");
        var victim = Path.Combine(_outside, "victim.txt");
        Directory.CreateDirectory(real);
        TestLinks.DirectoryLink(link, _outside);
        File.WriteAllText(victim, "the user's own file");

        using var stop = new CancellationTokenSource();
        var swapper = Task.Run(
            () =>
            {
                static void Try(Action act)
                {
                    try
                    {
                        act();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Held by a call at that instant: the swap goes on with the next step.
                    }
                }

                while (!stop.IsCancellationRequested)
                {
                    Try(() => Directory.Move(real, x));
                    Try(() => Directory.Move(x, real));
                    Try(() => Directory.Move(link, x));
                    Try(() => Directory.Move(x, link));

                    // A directory a call made at x while nothing stood there goes, so the swap can go on.
                    Try(() =>
                    {
                        if (Directory.Exists(real) && Directory.Exists(link) && new DirectoryInfo(x) is { Exists: true, LinkTarget: null })
                        {
                            Directory.Delete(x, recursive: true);
                        }
                    });
                }
            },
            Token);

        var calls = 0;

        try
        {
            for (var until = Environment.TickCount64 + 4000; Environment.TickCount64 < until && Directory.GetFiles(_outside).Length == 1; calls++)
            {
                try
                {
                    File.WriteAllText(Path.Combine(real, "victim.txt"), "the agent's");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Moved at that instant: the delete has its own file to find another time.
                }

                File.WriteAllText(Path.Combine(_worktree, "payload.txt"), "payload");
                await Call(runtime, AgentTools.DeleteFile, ("path", "x/victim.txt"));
                await Call(runtime, AgentTools.MovePath, ("from", "payload.txt"), ("to", "x/landed.txt"), ("overwrite", true));
                await Call(runtime, AgentTools.CreateFile, ("path", "x/written.txt"), ("content", "planted"), ("overwrite", true));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await swapper;
        }

        Assert.True(calls > 0);
        Assert.Equal(["victim.txt"], Directory.GetFiles(_outside).Select(Path.GetFileName));
        Assert.Equal("the user's own file", File.ReadAllText(victim));
    }

    /// <summary>
    /// A recursive delete is refused for one of the agent's roots, for a directory that holds a link - whatever it leads
    /// to left whole - for one that holds a path a rule keeps, and for one past the bound of entries.
    /// </summary>
    [Fact]
    public async Task ARecursiveDelete_IsRefused_ForARootALinkARuleAndTheBound()
    {
        File.WriteAllText(Path.Combine(_outside, "kept.txt"), "kept");
        Write("linking/a.txt", "a");
        TestLinks.DirectoryLink(Path.Combine(_worktree, "linking", "away"), _outside);
        Write("guarded/keep.txt", "k");
        Write("guarded/other.txt", "o");
        Directory.CreateDirectory(Path.Combine(_worktree, "many"));

        foreach (var at in Enumerable.Range(0, AgentTools.MostDeletedEntries + 1))
        {
            File.WriteAllText(Path.Combine(_worktree, "many", $"{at}.txt"), string.Empty);
        }

        var runtime = Open(restrictions: [Restriction("Write(guarded/keep.txt)")]);

        var root = await Call(runtime, AgentTools.DeleteDirectory, ("path", "."), ("recursive", true));
        Assert.Equal($"'{_worktree}' is one of this agent's roots, which no tool deletes", root.Text);

        var link = await Call(runtime, AgentTools.DeleteDirectory, ("path", "linking"), ("recursive", true));
        Assert.Equal(
            $"'{Path.Combine(_worktree, "linking")}' holds the link '{Path.Combine(_worktree, "linking", "away")}': no tool acts on a directory that holds a link",
            link.Text);
        Assert.True(File.Exists(Path.Combine(_outside, "kept.txt")));
        Assert.True(File.Exists(Path.Combine(_worktree, "linking", "a.txt")));

        var guarded = await Call(runtime, AgentTools.DeleteDirectory, ("path", "guarded"), ("recursive", true));
        Assert.StartsWith($"'{Path.Combine(_worktree, "guarded")}' holds '{Path.Combine(_worktree, "guarded", "keep.txt")}', which may not be written: ", guarded.Text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_worktree, "guarded", "other.txt")));

        var many = await Call(runtime, AgentTools.DeleteDirectory, ("path", "many"), ("recursive", true));
        Assert.Equal($"'{Path.Combine(_worktree, "many")}' holds more than 2000 entries, the most one call of a tool acts on", many.Text);
        Assert.True(Directory.Exists(Path.Combine(_worktree, "many")));

        Assert.Equal(
            [ConfinedPaths.RootsRule, ConfinedPaths.LinksRule, "Write(guarded/keep.txt)", AgentToolRuntime.MostEntriesRule],
            _events.All.OfType<PermissionDenied>().Select(denial => denial.Rule));
    }

    /// <summary>A file or a directory is moved, the directories on the way made; a file at the end is replaced only when asked, a directory never.</summary>
    [Fact]
    public async Task MovePath_MovesAFileOrADirectory()
    {
        Write("a.txt", "a");
        Write("c.txt", "c");
        Write("dir/f.txt", "f");
        Directory.CreateDirectory(Path.Combine(_worktree, "there"));
        var runtime = Open();
        await View(runtime, "a.txt");

        Assert.Equal("moved 'a.txt' to 'moved/b.txt'", (await Call(runtime, AgentTools.MovePath, ("from", "a.txt"), ("to", "moved/b.txt"))).Text);
        Assert.Equal("a", Read("moved/b.txt"));
        Assert.False(File.Exists(Path.Combine(_worktree, "a.txt")));

        // What was viewed goes with the file: it is edited at its new place unviewed, and its old place is known no more.
        Assert.True((await Replace(runtime, "moved/b.txt", "a", "b")).Ok);
        Write("a.txt", "a");
        Assert.Equal(AgentToolRuntime.NeverViewed, (await Replace(runtime, "a.txt", "a", "b")).Text);

        Assert.Equal("a file is at 'c.txt': overwrite: true replaces it", (await Call(runtime, AgentTools.MovePath, ("from", "moved/b.txt"), ("to", "c.txt"))).Text);
        Assert.True((await Call(runtime, AgentTools.MovePath, ("from", "moved/b.txt"), ("to", "c.txt"), ("overwrite", true))).Ok);
        Assert.Equal("b", Read("c.txt"));
        Assert.True((await Replace(runtime, "c.txt", "b", "c")).Ok);

        Assert.Equal("a directory is at 'there', and a move never replaces one", (await Call(runtime, AgentTools.MovePath, ("from", "c.txt"), ("to", "there"))).Text);
        Assert.Equal("a file is at 'c.txt', and a directory never replaces one", (await Call(runtime, AgentTools.MovePath, ("from", "dir"), ("to", "c.txt"), ("overwrite", true))).Text);
        Assert.Equal("'dir/sub' is 'dir' or inside it: nothing moves into itself", (await Call(runtime, AgentTools.MovePath, ("from", "dir"), ("to", "dir/sub"))).Text);
        Assert.Equal("there is nothing at 'gone' to move", (await Call(runtime, AgentTools.MovePath, ("from", "gone"), ("to", "x"))).Text);

        await View(runtime, "dir/f.txt");
        Assert.Equal("moved 'dir' to 'new/dir'", (await Call(runtime, AgentTools.MovePath, ("from", "dir"), ("to", "new/dir"))).Text);
        Assert.Equal("f", Read("new/dir/f.txt"));
        Assert.True((await Replace(runtime, "new/dir/f.txt", "f", "g")).Ok);
    }

    /// <summary>
    /// A name changed in its case alone is renamed, a file's and a directory's, where names fold case as where they do not,
    /// what was viewed of it going with it; a directory above it is renamed by no move of what it holds.
    /// </summary>
    [Fact]
    public async Task AMove_RenamesANameInItsCaseAlone()
    {
        Write("readme.md", "r");
        Write("docs/a.md", "a");
        var runtime = Open();
        await View(runtime, "readme.md");

        Assert.Equal("moved 'readme.md' to 'README.md'", (await Call(runtime, AgentTools.MovePath, ("from", "readme.md"), ("to", "README.md"))).Text);
        Assert.True((await Replace(runtime, "README.md", "r", "s")).Ok);
        Assert.Equal("moved 'docs' to 'Docs'", (await Call(runtime, AgentTools.MovePath, ("from", "docs"), ("to", "Docs"))).Text);

        Assert.Equal(["Docs", "README.md"], Directory.EnumerateFileSystemEntries(_worktree).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal));
        Assert.Equal("a", Read(Path.Combine("Docs", "a.md")));

        // Where names fold case the two spell one file, which moves into itself; where they do not, the first is no file.
        var folds = File.Exists(Path.Combine(_worktree, "docs", "a.md"));
        Assert.Equal(
            folds ? "'Docs/a.md' is 'docs/a.md' or inside it: nothing moves into itself" : "there is nothing at 'docs/a.md' to move",
            (await Call(runtime, AgentTools.MovePath, ("from", "docs/a.md"), ("to", "Docs/a.md"))).Text);
        Assert.Equal("a", Read(Path.Combine("Docs", "a.md")));
    }

    /// <summary>
    /// A move is a write at both ends, every path beneath a directory moved at the end it lands at among them; a root is
    /// never moved, and a move from a root of reading is refused by the writable ones.
    /// </summary>
    [Fact]
    public async Task AMove_IsAWriteAtBothEnds()
    {
        Write("dir/f.txt", "f");
        File.WriteAllText(Path.Combine(_main, "m.txt"), "m");
        var runtime = Open(writable: [_worktree, _extra], restrictions: [Restriction("Write(landing/f.txt)")]);

        var landing = await Call(runtime, AgentTools.MovePath, ("from", "dir"), ("to", "landing"));
        Assert.StartsWith("'landing/f.txt', where 'dir/f.txt' would land, is refused: ", landing.Text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_worktree, "dir", "f.txt")));
        Assert.False(Directory.Exists(Path.Combine(_worktree, "landing")));

        var fromMain = await Call(runtime, AgentTools.MovePath, ("from", Path.Combine(_main, "m.txt")), ("to", "m.txt"));
        Assert.Equal($"'{Path.Combine(_main, "m.txt")}' is outside the writable roots of this agent: {_worktree}, {_extra}", fromMain.Text);

        var root = await Call(runtime, AgentTools.MovePath, ("from", _extra), ("to", "extra"));
        Assert.Equal($"'{_extra}' is one of this agent's roots, which no tool moves", root.Text);
        Assert.True(Directory.Exists(_extra));
    }

    /// <summary>
    /// Files are found by a pattern: in a git work tree as git lists them, its ignore rules applied, elsewhere by a walk;
    /// those the agent may not read left out and counted.
    /// </summary>
    [Fact]
    public async Task FindFiles_ListsByThePattern_AsGitDoesInAWorkTree()
    {
        Write("src/a.cs", "a");
        Write("src/deep/b.cs", "b");
        Write("src/c.txt", "c");
        Write("src/secret.cs", "s");
        File.WriteAllText(Path.Combine(_main, "m.cs"), "m");
        var runtime = Open(restrictions: [Restriction("Read(src/secret.cs)")]);

        Assert.Equal("src/a.cs\nsrc/deep/b.cs\n[1 file(s) you may not read were left out]", (await Call(runtime, AgentTools.FindFiles, ("pattern", "*.cs"))).Text);
        Assert.Equal("src/deep/b.cs", (await Call(runtime, AgentTools.FindFiles, ("pattern", "deep/*.cs"), ("path", "src"))).Text);
        Assert.Equal(Path.Combine(_main, "m.cs"), (await Call(runtime, AgentTools.FindFiles, ("pattern", "*.cs"), ("path", _main))).Text);
        Assert.Equal("no file under '.' matches '*.java'", (await Call(runtime, AgentTools.FindFiles, ("pattern", "*.java"))).Text);
        Assert.Equal("there is no directory at 'nope'", (await Call(runtime, AgentTools.FindFiles, ("pattern", "*"), ("path", "nope"))).Text);

        await _harness.InitializeGitRepositoryAsync(_worktree, Token);
        Write(".gitignore", "ignored/\n");
        Write("ignored/x.cs", "x");

        Assert.Equal("src/a.cs\nsrc/deep/b.cs\n[1 file(s) you may not read were left out]", (await Call(runtime, AgentTools.FindFiles, ("pattern", "*.cs"))).Text);
    }

    /// <summary>
    /// A listing gives every file it matches - none left out past a count - its answer kept whole in the call's file and
    /// given a page at a time where it passes one, each page by read_result with the call's id.
    /// </summary>
    [Fact]
    public async Task FindFiles_GivesEveryMatch_APageAtATime()
    {
        foreach (var at in Enumerable.Range(0, 3000))
        {
            Write($"many/f{at:D4}.txt", string.Empty);
        }

        var runtime = Open();
        var found = await Call(runtime, AgentTools.FindFiles, "find", ("pattern", "*.txt"), ("path", "many"));
        var expected = Enumerable.Range(0, 3000).Select(at => $"many/f{at:D4}.txt").ToList();

        Assert.True(found.Ok);
        Assert.Equal(AgentCallRecords.ResultFile(_step, "find"), found.File);
        Assert.Equal(string.Join('\n', expected), File.ReadAllText(found.File!));
        Assert.StartsWith($"page 1 of 2 · 3000 results · ask read_result with id: 'find', page: 2 for the next · the whole is '{found.File}'\nmany/f0000.txt\n", found.Text, StringComparison.Ordinal);

        var second = await Call(runtime, AgentTools.ReadResult, ("id", "find"), ("page", 2));
        Assert.Equal(expected, [.. found.Text.Split('\n').Skip(1), .. second.Text.Split('\n').Skip(1)]);
    }

    /// <summary>Text is searched a line at a time: each hit with its context, bounded, the rest counted, binary files passed over.</summary>
    [Fact]
    public async Task SearchFiles_GivesEachHit_WithItsContext()
    {
        Write("src/a.cs", "alpha\nbeta\ngamma\n");
        Write("src/b.cs", "beta two\n");
        Write("src/secret.cs", "beta secret\n");
        File.WriteAllBytes(Path.Combine(_worktree, "src", "bin.dat"), [0x62, 0x00, 0x65]);
        var runtime = Open(restrictions: [Restriction("Read(src/secret.cs)")]);

        Assert.Equal(
            "src/a.cs:2: beta\nsrc/b.cs:1: beta two\n[1 file(s) you may not read were left out]\n[1 file(s) passed over: binary, or no text this tool reads]",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"))).Text);
        Assert.Equal(
            "src/a.cs-1- alpha\nsrc/a.cs:2: beta\nsrc/a.cs-3- gamma\n--\nsrc/b.cs:1: beta two\n[1 file(s) you may not read were left out]",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"), ("glob", "*.cs"), ("context", 1))).Text);
        Assert.Equal("src/a.cs:2: beta", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "^BETA$"), ("case_insensitive", true), ("path", "src"), ("glob", "a.cs"))).Text);
        Assert.Equal("no line matches, in 2 file(s) read\n[1 file(s) you may not read were left out]", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "zeta"), ("glob", "*.cs"))).Text);

        Assert.Contains("'('", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "("))).Text, StringComparison.Ordinal);
        Assert.Equal("context is -1, and is 0 or more", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "a"), ("context", -1))).Text);
    }

    /// <summary>
    /// max_results is refused before anything is searched, naming page and read_result - every matching line is given, a
    /// page at a time - however it is given; the call ran nothing.
    /// </summary>
    [Fact]
    public async Task MaxResults_IsRefusedNamingPage()
    {
        Write("src/a.cs", "beta\n");
        var runtime = Open();

        foreach (var most in new object[] { 1, 500, 0 })
        {
            var refused = await Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"), ("max_results", most));

            Assert.Equal((false, false), (refused.Ok, refused.Ran));
            Assert.StartsWith(AgentToolRuntime.MaxResultsRetired + "; search_files takes ", refused.Text, StringComparison.Ordinal);
            Assert.Contains("page", refused.Text, StringComparison.Ordinal);
            Assert.Contains(AgentTools.ReadResult, refused.Text, StringComparison.Ordinal);
        }

        var schema = JsonNode.Parse(AgentTools.All(AgentCommandSettings.Default).Single(tool => tool.Name == AgentTools.SearchFiles).Schema)!;
        Assert.Null(schema["properties"]!["max_results"]);
    }

    /// <summary>A hit is given with as many lines around it as context asks - fifty, past the ten it was once held to - each numbered.</summary>
    [Fact]
    public async Task AContextOfFiftyLines_IsGiven()
    {
        Write("long.txt", string.Join('\n', Enumerable.Range(1, 200).Select(at => at == 100 ? "the hit" : $"line {at}")) + "\n");
        var runtime = Open();

        var hit = await Call(runtime, AgentTools.SearchFiles, ("pattern", "^the hit$"), ("context", 50));

        Assert.Equal(
            string.Join('\n', [.. Enumerable.Range(50, 50).Select(at => $"long.txt-{at}- line {at}"), "long.txt:100: the hit", .. Enumerable.Range(101, 50).Select(at => $"long.txt-{at}- line {at}")]),
            hit.Text);
    }

    /// <summary>
    /// A thousand matching lines - twice the 500 a search once gave at most - are every one given: the answer kept whole in
    /// the call's file and its first page given, every page after it by read_result, every hit once and in order.
    /// </summary>
    [Fact]
    public async Task AThousandMatches_AreReachableByPage()
    {
        Write("hits.txt", string.Concat(Enumerable.Range(1, 1000).Select(at => $"match {at:D4} {new string('m', 60)}\n")));
        var runtime = Open();

        var found = await Call(runtime, AgentTools.SearchFiles, "hits", ("pattern", "^match"));
        var expected = Enumerable.Range(1, 1000).Select(at => $"hits.txt:{at}: match {at:D4} {new string('m', 60)}").ToList();

        Assert.Equal(AgentCallRecords.ResultFile(_step, "hits"), found.File);
        Assert.Equal(string.Join('\n', expected), File.ReadAllText(found.File!));

        var header = found.Text.Split('\n')[0];
        var pages = int.Parse(header.Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"page 1 of {pages} · 1000 results · ask read_result with id: 'hits', page: 2 for the next · the whole is '{found.File}'", header);

        var given = found.Text.Split('\n').Skip(1).ToList();

        for (var page = 2; page <= pages; page++)
        {
            given.AddRange((await Call(runtime, AgentTools.ReadResult, $"read_{page}", ("id", "hits"), ("page", page))).Text.Split('\n').Skip(1));
        }

        Assert.Equal(expected, given);
    }

    /// <summary>
    /// A file of 50 MB - past the 2 MiB a search once passed over - is searched whole, a line at a time: the hit on its
    /// last line is found.
    /// </summary>
    [Fact]
    public async Task AFiftyMegabyteFile_IsSearched()
    {
        var full = Path.Combine(_worktree, "big", "dump.sql");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        using (var writer = new StreamWriter(full, append: false, new UTF8Encoding(false)))
        {
            var row = "INSERT INTO t VALUES (" + new string('1', 77) + ");";

            for (var at = 0; at < 50 * 1024 * 1024 / (row.Length + 1); at++)
            {
                writer.Write(row);
                writer.Write('\n');
            }

            writer.Write("-- the needle\n");
        }

        Assert.True(new FileInfo(full).Length > 50L * 1000 * 1000);

        var found = await Call(Open(), AgentTools.SearchFiles, ("pattern", "needle"), ("path", "big"));

        Assert.True(found.Ok, found.Text);
        Assert.Matches(@"^dump\.sql:\d+: -- the needle$", found.Text);
    }

    /// <summary>
    /// A search's pattern is matched in time linear in the text it reads, with no time bound: a pattern that would
    /// backtrack without end, on a long line, ends at once; one that needs backtracking is refused, naming what it holds.
    /// </summary>
    [Fact]
    public async Task SearchPatterns_AreLinear()
    {
        Write("slow/a.txt", new string('a', 1_000_000) + "!\n");
        var runtime = Open();

        var started = System.Diagnostics.Stopwatch.StartNew();
        var none = await Call(runtime, AgentTools.SearchFiles, ("pattern", "(a+)+b"), ("path", "slow"));

        Assert.Equal((true, "no line matches, in 1 file(s) read"), (none.Ok, none.Text));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"a linear match took {started.Elapsed}");

        var back = await Call(runtime, AgentTools.SearchFiles, ("pattern", "(a)\\1"), ("path", "slow"));
        Assert.Equal((false, false), (back.Ok, back.Ran));
        Assert.Contains("'(a)\\1'", back.Text, StringComparison.Ordinal);
        Assert.EndsWith(": write it without that", back.Text, StringComparison.Ordinal);
    }

    /// <summary>A search stops between any two lines of a file when it is stopped, not only between files.</summary>
    [Fact]
    public async Task ASearch_StopsBetweenTheLinesOfAFile()
    {
        Write("lines/a.txt", "a\nb\nc\nd\ne\n");
        using var stop = new CancellationTokenSource();
        var files = new AgentFiles(_harness.GitClient, _harness.FileSystem, TimeProvider.System);
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.SearchAsync(
            Path.Combine(_worktree, "lines"),
            new Regex("[a-e]"),
            null,
            StringComparison.Ordinal,
            0,
            _ => true,
            (_, _) =>
            {
                if (++seen == 2)
                {
                    stop.Cancel();
                }

                return ValueTask.CompletedTask;
            },
            stop.Token));

        Assert.Equal(2, seen);
    }

    /// <summary>
    /// A file a search could not read is counted apart from those it passed over, with why the first was not; a file the
    /// listing names that is no longer there is none to search, and none said.
    /// </summary>
    [Fact]
    public async Task ASearch_SaysTheFilesItCouldNotRead()
    {
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            Write($"unread/{name}.txt", "beta");
        }

        var runtime = Open(fileSystem: new UnreadFiles(_harness.FileSystem, gone: "d.txt", "b.txt", "c.txt"));

        Assert.Equal(
            "unread/a.txt:1: beta\n[2 file(s) could not be read, the first 'unread/b.txt': it is held by another process]",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"), ("path", "unread"))).Text);
    }

    /// <summary>
    /// A place under a directory that cannot be read - a directory beneath it, or what a listed path is - is passed by and
    /// counted, with why the first was not, by a listing and by a search alike: the rest is listed and searched.
    /// </summary>
    [Fact]
    public async Task AListing_PassesByAPlaceItCannotRead_AndSaysIt()
    {
        foreach (var name in new[] { "a", "b" })
        {
            Write($"places/{name}.txt", "beta");
        }

        var runtime = Open(fileSystem: new UnreadPlaces(_harness.FileSystem));

        Assert.Equal(
            "places/a.txt\n[2 place(s) could not be read, the first 'places/locked': it is closed to this process]",
            (await Call(runtime, AgentTools.FindFiles, ("pattern", "*.txt"), ("path", "places"))).Text);
        Assert.Equal(
            "places/a.txt:1: beta\n[2 place(s) could not be read, the first 'places/locked': it is closed to this process]",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"), ("path", "places"))).Text);
    }

    /// <summary>A pipe is never opened by a view or an edit, which fail at once saying what it is; a search reads past it.</summary>
    [Fact]
    public async Task APipe_IsNeitherViewedNorEdited_AndASearchReadsPastIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows holds no pipe among a directory's files.");

        Write("pipes/plain.txt", "beta");
        var pipe = Path.Combine(_worktree, "pipes", "pipe");
        using (var made = System.Diagnostics.Process.Start("mkfifo", [pipe]))
        {
            await made.WaitForExitAsync(Token);
            Assert.Equal(0, made.ExitCode);
        }

        var runtime = Open();

        Assert.Equal($"view_file failed: '{pipe}' is a pipe, which is no file to read", (await Bounded(() => View(runtime, "pipes/pipe"))).Text);
        Assert.Equal($"replace_in_file failed: '{pipe}' is a pipe, which is no file to read", (await Bounded(() => Replace(runtime, "pipes/pipe", "a", "b"))).Text);
        Assert.Equal(
            $"pipes/plain.txt:1: beta\n[1 file(s) could not be read, the first 'pipes/pipe': '{pipe}' is a pipe, which is no file to read]",
            (await Bounded(() => Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"), ("path", "pipes")))).Text);

        // Run beside the test, so a call that waits on the pipe fails the test rather than holding it.
        static Task<AgentToolCallResult> Bounded(Func<Task<AgentToolCallResult>> call) => Task.Run(call, Token).WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    /// <summary>
    /// A listing looks at its token between the paths it asks what they are, and a find between the files it matches: a
    /// call stopped is stopped there, whatever the walk before it took.
    /// </summary>
    [Fact]
    public async Task AListingAndAFind_StopBetweenTheirFiles()
    {
        Write("stop/a.txt", "a");
        Write("stop/b.txt", "b");
        var directory = Path.Combine(_worktree, "stop");

        using (var atFirst = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            var files = new AgentFiles(_harness.GitClient, new StoppingAtKinds(_harness.FileSystem, atFirst, 1), TimeProvider.System);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.ListAsync(directory, atFirst.Token));
        }

        using var atLast = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var runtime = Open(fileSystem: new StoppingAtKinds(_harness.FileSystem, atLast, 2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.RunAsync(Request("call_1", AgentTools.FindFiles, ("pattern", "*.txt"), ("path", "stop")), atLast.Token));
    }

    /// <summary>A file whose name no pattern reads - a '\' read as a separator where it is none - is left out of a listing and a search, and counted.</summary>
    [Fact]
    public async Task AFileWhoseNameNoPatternReads_IsLeftOut_AndCounted()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A '\\' is a separator on Windows, and no name holds one there.");

        Write("names/plain.txt", "beta");
        File.WriteAllText(Path.Combine(_worktree, "names", "x\\..\\y.txt"), "beta");
        var runtime = Open();

        Assert.Equal("names/plain.txt\n[1 file(s) whose names no pattern reads were left out]", (await Call(runtime, AgentTools.FindFiles, ("pattern", "*.txt"), ("path", "names"))).Text);
        Assert.Equal("names/plain.txt:1: beta\n[1 file(s) whose names no pattern reads were left out]", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"), ("path", "names"))).Text);
    }

    /// <summary>A recursive delete that stops at what it cannot remove says how much is gone, and what is gone is a write.</summary>
    [Fact]
    public async Task ARecursiveDelete_ThatStopsPartWay_SaysWhatIsGone()
    {
        Assert.SkipWhen(Environment.UserName == "root", "No directory refuses root.");

        Write("held/a.txt", "a");
        Write("held/b.txt", "b");
        Write("held/kept/c.txt", "c");
        var kept = Path.Combine(_worktree, "held", "kept");
        var runtime = Open();
        await View(runtime, "held/a.txt");
        FileStream? open = null;

        // What a delete cannot remove: on Windows a file held open, elsewhere a file in a directory that may not be written.
        if (OperatingSystem.IsWindows())
        {
            open = new FileStream(Path.Combine(kept, "c.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(kept, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            var stopped = await Call(runtime, AgentTools.DeleteDirectory, ("path", "held"), ("recursive", true));
            var gone = new[] { "a.txt", "b.txt", Path.Combine("kept", "c.txt") }.Count(file => !File.Exists(Path.Combine(_worktree, "held", file)));

            Assert.False(stopped.Ok);
            Assert.StartsWith($"deleting 'held' stopped part way, {gone} of its 3 file(s) gone: ", stopped.Text, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(kept, "c.txt")));
            Assert.Equal((gone, gone > 0), (runtime.Tally.FilesWritten, runtime.Tally.Files.Contains(Path.Combine(_worktree, "held"))));

            // What is gone is known no more: the same text written there again is a file never viewed.
            if (!File.Exists(Path.Combine(_worktree, "held", "a.txt")))
            {
                Write("held/a.txt", "a");
                Assert.Equal(AgentToolRuntime.NeverViewed, (await Replace(runtime, "held/a.txt", "a", "b")).Text);
            }
        }
        finally
        {
            open?.Dispose();

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(kept, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>In a turn a write stands between the reads: those before it run before it, those after see what it wrote; answers keep the calls' order.</summary>
    [Fact]
    public async Task ATurn_RunsAWriteBetweenItsReads_InTheCallsOrder()
    {
        Write("a.txt", "a");
        var runtime = Open();

        var results = await runtime.RunTurnAsync(
            [
                Request("1", AgentTools.ViewFile, ("path", "new.txt")),
                Request("2", AgentTools.ViewFile, ("path", "a.txt")),
                Request("3", AgentTools.CreateFile, ("path", "new.txt"), ("content", "made")),
                Request("4", AgentTools.ViewFile, ("path", "new.txt")),
                Request("5", AgentTools.FindFiles, ("pattern", "*.txt")),
            ],
            Token);

        Assert.Equal(["1", "2", "3", "4", "5"], results.Select(result => result.Id));
        Assert.Equal("there is no file or directory at 'new.txt'", results[0].Text);
        Assert.Equal("1\ta", results[1].Text);
        Assert.True(results[2].Ok);
        Assert.Equal("1\tmade", results[3].Text);
        Assert.Equal("a.txt\nnew.txt", results[4].Text);
        Assert.Equal(5, _events.All.OfType<ToolCallFinished>().Count());
    }

    /// <summary>Calls that read run together, at most the bound at once, each read of a file still under way while the others start.</summary>
    [Fact]
    public async Task ATurnsReads_RunAtMostTheBoundAtOnce()
    {
        var count = (AgentToolRuntime.MostTogether * 2) + 2;

        foreach (var at in Enumerable.Range(0, count))
        {
            Write($"f{at}.txt", $"{at}");
        }

        var reads = new SlowReads(_harness.FileSystem, _worktree);
        var runtime = Open(fileSystem: reads);

        var results = await runtime.RunTurnAsync([.. Enumerable.Range(0, count).Select(at => Request($"{at}", AgentTools.ViewFile, ("path", $"f{at}.txt")))], Token);

        Assert.Equal(Enumerable.Range(0, count).Select(at => $"1\t{at}"), results.Select(result => result.Text));
        Assert.Equal(AgentToolRuntime.MostTogether, reads.Most);
    }

    /// <summary>
    /// The share of a turn's reads that run at once is the one owner's - the graph's share of the machine's processors
    /// beside a build - never a number of the runtime's own: one at the least.
    /// </summary>
    [Fact]
    public void TheReadShare_ComesFromItsOwner()
    {
        Assert.Equal(Math.Max(1, Environment.ProcessorCount / CodebaseGraphService.ReadShare), AgentToolRuntime.MostTogether);
    }

    /// <summary>A command runs where it is asked to start, given its input, and answers how it ended and what it printed, its log kept whole.</summary>
    [Fact]
    public async Task RunCommand_RunsAProgram_AndAnswersHowItEnded()
    {
        Directory.CreateDirectory(Path.Combine(_worktree, "sub"));
        var runtime = Open();

        var (program, arguments) = Shell("echo hello", "echo hello");
        var said = await Call(runtime, AgentTools.RunCommand, "c1", ("program", program), ("args", arguments));
        Assert.True(said.Ok, said.Text);
        Assert.Matches(@"^exit 0, after \d+\.\d s:\nhello$", said.Text);
        Assert.Equal(AgentCallRecords.ResultFile(_step, "c1"), said.File);
        Assert.True(File.Exists(said.File));

        var (inProgram, inArguments) = Shell("sort", "cat");
        var given = await Call(runtime, AgentTools.RunCommand, "c2", ("program", inProgram), ("args", inArguments), ("stdin", "fed\n"));
        Assert.EndsWith(":\nfed", given.Text, StringComparison.Ordinal);

        var (cdProgram, cdArguments) = Shell("cd", "pwd");
        var started = await Call(runtime, AgentTools.RunCommand, "c3", ("program", cdProgram), ("args", cdArguments), ("cwd", "sub"));
        Assert.EndsWith($":\n{Path.Combine(_worktree, "sub")}", started.Text, StringComparison.Ordinal);

        var (failProgram, failArguments) = Shell("exit 3", "exit 3");
        var failed = await Call(runtime, AgentTools.RunCommand, "c4", ("program", failProgram), ("args", failArguments));
        Assert.False(failed.Ok);
        Assert.Matches(@"^exit 3, after \d+\.\d s; it printed nothing$", failed.Text);

        var missing = await Call(runtime, AgentTools.RunCommand, "c5", ("program", "dh-no-such-program-7f3"));
        Assert.False(missing.Ok);
        Assert.StartsWith("'dh-no-such-program-7f3' could not be started: ", missing.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every call's answer, and its end in the stream, says whether its tool ran, apart from whether it succeeded: a
    /// command that started ran however it exited - grep, rg and git grep exit 1 finding nothing, and that is a search
    /// made - as a search that matched no line did; a call refused, one whose input does not hold, one whose search is no
    /// pattern or has no directory to search, and a command that could not be started or has no directory to start in did
    /// none of their work. What a closing record cites as a search is held to it.
    /// </summary>
    [Fact]
    public async Task EveryCallSaysWhetherItsToolRan_ApartFromWhetherItSucceeded()
    {
        var runtime = Open(restrictions: [Restriction("Read(secret.txt)")]);
        Write("a.txt", "alpha\n");

        var (failProgram, failArguments) = Shell("exit 1", "exit 1");
        var answers = new[]
        {
            await Call(runtime, AgentTools.RunCommand, "exited", ("program", failProgram), ("args", failArguments)),
            await Call(runtime, AgentTools.SearchFiles, "unmatched", ("pattern", "zeta")),
            await Call(runtime, AgentTools.ViewFile, "refused", ("path", "secret.txt")),
            await runtime.RunAsync(new AgentToolCallRequest("unheld", AgentTools.ViewFile, "{}"), Token),
            await Call(runtime, AgentTools.SearchFiles, "no-pattern", ("pattern", "(")),
            await Call(runtime, AgentTools.FindFiles, "no-directory", ("pattern", "*"), ("path", "nope")),
            await Call(runtime, AgentTools.RunCommand, "unstarted", ("program", "dh-no-such-program-7f3")),
            await Call(runtime, AgentTools.RunCommand, "nowhere", ("program", "git"), ("cwd", "nope")),
        };

        Assert.Equal(
            [
                ("exited", false, true),
                ("unmatched", true, true),
                ("refused", false, false),
                ("unheld", false, false),
                ("no-pattern", false, false),
                ("no-directory", false, false),
                ("unstarted", false, false),
                ("nowhere", false, false),
            ],
            answers.Select(answer => (answer.Id, answer.Ok, answer.Ran)));

        // The stream says it of each call as the answer does.
        Assert.Equal(
            answers.Select(answer => (answer.Id, (bool?)answer.Ran)),
            _events.All.OfType<ToolCallFinished>().Select(finished => (finished.Id, finished.Ran)));
    }

    /// <summary>
    /// A command is decided with the input its call writes it: a program that reads its script from its input is refused
    /// where the call gives one, and nothing of it runs.
    /// </summary>
    [Fact]
    public async Task RunCommand_GivenAnInput_IsDecidedAsTheProgramReadsIt()
    {
        var runtime = Open();

        var given = await Call(runtime, AgentTools.RunCommand, "c1", ("program", "sh"), ("stdin", "echo hi\n"));

        Assert.False(given.Ok);
        Assert.Equal("this tool could not decide 'sh' ('sh' reads the script it runs from its input, which this does not read): it denies what it cannot decide", given.Text);
    }

    /// <summary>A program named by a relative path is the one where the command starts, never one in this process's directory.</summary>
    [Fact]
    public async Task RunCommand_ReadsARelativeProgramWhereItStarts()
    {
        var name = OperatingSystem.IsWindows() ? "run.cmd" : "run.sh";
        Write(Path.Combine("sub", name), OperatingSystem.IsWindows() ? "@echo from-sub\r\n" : "#!/bin/sh\necho from-sub\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(_worktree, "sub", name), (UnixFileMode)Convert.ToInt32("755", 8));
        }

        var ran = await Call(Open(), AgentTools.RunCommand, ("program", "./" + name), ("cwd", "sub"));

        Assert.True(ran.Ok, ran.Text);
        Assert.EndsWith(":\nfrom-sub", ran.Text, StringComparison.Ordinal);
    }

    /// <summary>A command that prints nothing for the step's bound on silence is ended whole, and says so.</summary>
    [Fact]
    public async Task RunCommand_EndsACommandSilentPastItsBound()
    {
        var runtime = Open(limits: new AgentLimits(null, null, null, StallSeconds: 1));
        var (program, arguments) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" })
            : Shell(string.Empty, "sleep 30");

        var stopped = await Call(runtime, AgentTools.RunCommand, ("program", program), ("args", arguments));

        Assert.False(stopped.Ok);
        Assert.StartsWith("stopped: it printed nothing for 1 s, after ", stopped.Text, StringComparison.Ordinal);
    }

    /// <summary>A command runs contained, in a session recorded beside the step, and what its container could not reach is kept.</summary>
    [Fact]
    public async Task RunCommand_RunsContained_AndKeepsWhatItsContainerCouldNotReach()
    {
        var processes = new Uncontaining();
        var runtime = Open(processes: processes);

        var ran = await Call(runtime, AgentTools.RunCommand, ("program", "tool"), ("args", new[] { "go" }));

        Assert.True(ran.Ok, ran.Text);
        var request = Assert.Single(processes.Started);
        Assert.True(request.Contained);
        Assert.NotNull(request.SessionRecord);
        Assert.Equal(["tool go: a process outside the job"], runtime.Tally.Uncontained);
    }

    /// <summary>
    /// A command runs inside the isolation its launch was given for commands - the profile that hides the Claude Code
    /// directories, never the one a whole Claude Code runs in - and with none where the launch was given none.
    /// </summary>
    [Fact]
    public async Task RunCommand_RunsInsideTheLaunchsIsolationForCommands()
    {
        var processes = new Uncontaining();
        var mechanism = new PassThroughMechanism();
        var commands = new DssHarness.Core.Processes.IsolationProfile([new DssHarness.Core.Processes.IsolatedPath("/home/me/.claude", Directory: true)], []);
        var launch = new DssHarness.Core.Agents.Isolation.LaunchIsolation("required", mechanism, commands, null, null);
        var runtime = Open(processes: processes, isolation: launch);

        var ran = await Call(runtime, AgentTools.RunCommand, ("program", "tool"), ("args", new[] { "go" }));

        Assert.True(ran.Ok, ran.Text);
        Assert.Equal(new DssHarness.Core.Processes.ProcessIsolation(mechanism, commands), Assert.Single(processes.Started).Isolation);

        var bare = new Uncontaining();
        var plain = Open(processes: bare);

        Assert.True((await Call(plain, AgentTools.RunCommand, ("program", "tool"), ("args", new[] { "go" }))).Ok);
        Assert.Null(Assert.Single(bare.Started).Isolation);
    }

    /// <summary>A command the run's own stop ends still keeps what its container could not reach, and the stop goes on.</summary>
    [Fact]
    public async Task ACommandTheRunsStopEnds_StillKeepsWhatItsContainerCouldNotReach()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var runtime = Open(processes: new Uncontaining(stop));

        await Assert.ThrowsAsync<ProcessStoppedException>(() => runtime.RunAsync(Request("call_1", AgentTools.RunCommand, ("program", "tool"), ("args", new[] { "go" })), stop.Token));

        Assert.Equal(["tool go: a process outside the job"], runtime.Tally.Uncontained);
    }

    /// <summary>A command's paths are read where it starts: one that reaches a Claude Code directory from there is refused, the same words elsewhere not.</summary>
    [Fact]
    public async Task RunCommand_ReadsItsPathsWhereItStarts()
    {
        Directory.CreateDirectory(Path.Combine(_worktree, "sub"));
        var runtime = Open(claudeCodeDirectories: [Path.Combine(_worktree, "cc")]);

        var fromSub = await Call(runtime, AgentTools.RunCommand, ("program", "cat"), ("args", new[] { "../cc/settings.json" }), ("cwd", "sub"));
        Assert.False(fromSub.Ok);
        Assert.Single(_events.All.OfType<PermissionDenied>());

        await Call(runtime, AgentTools.RunCommand, ("program", "cat"), ("args", new[] { "../cc/settings.json" }));
        Assert.Single(_events.All.OfType<PermissionDenied>());
    }

    /// <summary>
    /// A command whose call names no time, where the configuration names none, runs until it ends: past the two minutes a
    /// command was once stopped at, by the clock it is timed by, it is still running, and it ends as it ends.
    /// </summary>
    [Fact]
    public async Task ACommandRunsPastTwoMinutes_WhenNoTimeIsNamed()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-11-19T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var processes = new ClockedCommand(clock, TimeSpan.FromHours(3));
        var runtime = Open(processes: processes, clock: clock);
        var (program, arguments) = Shell("echo built", "echo built");

        var built = await Call(runtime, AgentTools.RunCommand, "build", ("program", program), ("args", arguments));

        Assert.True(built.Ok, built.Text);
        Assert.Null(processes.Timeout);
        Assert.StartsWith("exit 0, after ", built.Text, StringComparison.Ordinal);
        Assert.Contains("after 10800.0 s", built.Text, StringComparison.Ordinal);

        var configured = Open(processes: processes, clock: clock, commands: AgentCommandSettings.Default with { CommandTimeoutSeconds = 600 });
        await Call(configured, AgentTools.RunCommand, "timed", ("program", program), ("args", arguments));
        Assert.Equal(TimeSpan.FromSeconds(600), processes.Timeout);

        await Call(configured, AgentTools.RunCommand, "asked", ("program", program), ("args", arguments), ("timeout_seconds", 7200));
        Assert.Equal(TimeSpan.FromSeconds(7200), processes.Timeout);
    }

    /// <summary>A command that runs past its time is ended whole, and says so.</summary>
    [Fact]
    public async Task RunCommand_EndsACommandPastItsTime()
    {
        var runtime = Open();
        var (program, arguments) = Shell("ping -n 30 127.0.0.1", "sleep 30");

        var stopped = await Call(runtime, AgentTools.RunCommand, ("program", program), ("args", arguments), ("timeout_seconds", 1));

        Assert.False(stopped.Ok);
        Assert.StartsWith("stopped: it ran past its 1 s, after ", stopped.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A command is refused before it runs: a time out of bounds, a line that holds the run's secret, a start outside the
    /// roots or not there, and a Run rule that matches it.
    /// </summary>
    [Fact]
    public async Task RunCommand_IsRefused_BeforeItRuns()
    {
        var runtime = Open(mask: new Masking("s3cr3t"), restrictions: [Restriction("Run(npm publish *)")]);

        Assert.Equal("timeout_seconds is 0, and is 1 to 3600", (await Call(runtime, AgentTools.RunCommand, ("program", "git"), ("timeout_seconds", 0))).Text);
        Assert.Equal("timeout_seconds is 3601, and is 1 to 3600", (await Call(runtime, AgentTools.RunCommand, ("program", "git"), ("timeout_seconds", 3601))).Text);
        Assert.Equal("program is empty: name the program to run", (await Call(runtime, AgentTools.RunCommand, ("program", string.Empty))).Text);

        var secret = await Call(runtime, AgentTools.RunCommand, ("program", "curl"), ("args", new[] { "-H", "token: s3cr3t" }));
        Assert.Equal("the command holds a secret of this run's, which a command's line never carries: give it through what the program reads instead", secret.Text);

        var outside = await Call(runtime, AgentTools.RunCommand, ("program", "git"), ("args", new[] { "status" }), ("cwd", _outside));
        Assert.StartsWith($"'{_outside}' is outside the roots of this agent: ", outside.Text, StringComparison.Ordinal);

        Assert.Equal("there is no directory at 'nope' to start in", (await Call(runtime, AgentTools.RunCommand, ("program", "git"), ("cwd", "nope"))).Text);

        var published = await Call(runtime, AgentTools.RunCommand, ("program", "npm"), ("args", new[] { "publish", "--tag", "next" }));
        Assert.False(published.Ok);

        var denials = _events.All.OfType<PermissionDenied>().ToList();
        Assert.Equal([AgentToolRuntime.MaskRule, ConfinedPaths.RootsRule, "Run(npm publish *)"], denials.Select(denial => denial.Rule));
        Assert.Equal("curl -H token: ***", denials[0].Subject);

        runtime.Dispose();
        Assert.DoesNotContain("s3cr3t", File.ReadAllText(Path.Combine(_step, AgentCallRecords.DenialsFileName)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A command's environment loses every name appointed for a key and every secret-shaped name, but those
    /// agentPolicy.keepEnvironment gives back; the rest of the tool's environment it keeps.
    /// </summary>
    [Fact]
    public async Task RunCommand_GivesTheChildEnvironmentOfAnAgent()
    {
        var tag = "DH_RT_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        string[] names = [$"{tag}_API_KEY", $"{tag}_TOKEN", $"{tag}_APPOINTED", $"{tag}_PLAIN"];

        foreach (var name in names)
        {
            Environment.SetEnvironmentVariable(name, "value");
        }

        try
        {
            var runtime = Open(
                commands: AgentCommandSettings.Default with { KeepEnvironment = [$"{tag}_TOKEN"] },
                withheld: [$"{tag}_APPOINTED"]);

            var (program, arguments) = Shell(
                "echo " + string.Join(string.Empty, names.Select(name => $"[%{name}%]")),
                $"echo \"{string.Join(string.Empty, names.Select(name => $"[${name}]"))}\"");
            var result = await Call(runtime, AgentTools.RunCommand, ("program", program), ("args", arguments));

            var expected = OperatingSystem.IsWindows()
                ? $"[%{tag}_API_KEY%][value][%{tag}_APPOINTED%][value]"
                : "[][value][][value]";
            Assert.EndsWith($":\n{expected}", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    /// <summary>
    /// A page is fetched by the harness's own fetch, decided first and recorded in web.jsonl before and after, its text
    /// given in a block that says where it is from and what it is.
    /// </summary>
    [Fact]
    public async Task WebFetch_GivesThePagesText_InABlock_AndRecordsIt()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("<html><head><title>T</title></head><body><p>Hello</p></body></html>", "text/html"));
        var runtime = Open(endpoint: endpoint);

        var result = await Call(runtime, AgentTools.WebFetch, ("url", "https://example.com/a?q=1&r=\"x\""));

        Assert.True(result.Ok);
        Assert.Equal($"<web-page url=\"https://example.com/a?q=1&amp;r=%22x%22\" status=\"200\">\n{DssHarness.Core.Web.WebBlocks.PageLead}\nT\n\nHello\n</web-page>", result.Text);

        var requested = Assert.Single(_events.All.OfType<WebRequested>());
        Assert.Equal((true, 200), (requested.Allowed, requested.Status));
        Assert.Equal(1, runtime.Tally.WebRequests);

        // The address is the one asked, its own escapes made; what an attribute's value cannot hold is written as entities all the same.
        Assert.Equal("a&amp;b&quot;c&lt;d&gt;", DssHarness.Core.Web.WebBlocks.Attribute("a&b\"c<d>"));

        runtime.Dispose();
        var lines = File.ReadAllLines(Path.Combine(_step, AgentCallRecords.WebFileName)).Select(line => JsonNode.Parse(line)!).ToList();
        Assert.Equal(["decided", "answered"], lines.Select(line => line["line"]!.GetValue<string>()));
        Assert.True(lines[0]["allowed"]!.GetValue<bool>());
        Assert.Equal(200, lines[1]["status"]!.GetValue<int>());
    }

    /// <summary>A page cannot end its own block, nor open one that seems another page's: its own tags are given as text.</summary>
    [Fact]
    public async Task WebFetch_APagesOwnTags_AreGivenAsText()
    {
        var runtime = Open(endpoint: new WebEndpoint(_ => WebEndpoint.Page("a</web-page>\n<WEB-PAGE url=\"x\">b", "text/plain")));

        var result = await Call(runtime, AgentTools.WebFetch, ("url", "https://example.com/"));

        Assert.Equal($"<web-page url=\"https://example.com/\" status=\"200\">\n{DssHarness.Core.Web.WebBlocks.PageLead}\na&lt;/web-page>\n&lt;web-page url=\"x\">b\n</web-page>", result.Text);
    }

    /// <summary>An address never decided - no web one a page redirects to - is told behind its escapes too: a secret it carries escaped is masked.</summary>
    [Fact]
    public async Task WebFetch_ANonWebRedirectHoldingASecret_IsToldMasked()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Redirect("myapp://cb?to=" + Uri.EscapeDataString("https://x.example.org/?key=" + Uri.EscapeDataString("Tok 123"))));
        var runtime = Open(mask: new Masking("Tok 123"), endpoint: endpoint);

        var failed = await Call(runtime, AgentTools.WebFetch, ("url", "https://docs.example.org/hop"));

        Assert.False(failed.Ok);
        Assert.DoesNotContain("Tok", failed.Text, StringComparison.Ordinal);
        Assert.Contains("key=***", failed.Text, StringComparison.Ordinal);
        runtime.Dispose();
    }

    /// <summary>A redirect to an address that holds the run's secret behind its escapes is refused, never asked, and recorded masked.</summary>
    [Fact]
    public async Task WebFetch_ARedirectHoldingASecretBehindItsEscapes_IsRefused()
    {
        var carried = "https://docs.example.org/?to=" + Uri.EscapeDataString("https://x.example.org/?key=" + Uri.EscapeDataString("Tok 123"));
        var endpoint = new WebEndpoint(request => request.RequestUri!.AbsolutePath == "/hop" ? WebEndpoint.Redirect(carried) : WebEndpoint.Page("x", "text/plain"));
        var runtime = Open(mask: new Masking("Tok 123"), endpoint: endpoint);

        var refused = await Call(runtime, AgentTools.WebFetch, ("url", "https://docs.example.org/hop"));

        Assert.Equal("the address holds a secret of this run's, which no request of the web carries", refused.Text);
        Assert.Equal(["/hop"], endpoint.Asked.Select(sent => sent.Address.AbsolutePath));

        runtime.Dispose();
        Assert.Contains("https://docs.example.org/?to=https://x.example.org/?key=***", File.ReadAllText(Path.Combine(_step, AgentCallRecords.WebFileName)), StringComparison.Ordinal);
    }

    /// <summary>
    /// An address the step's domains leave out, or that holds the run's secret, is refused and recorded, nothing sent - the
    /// secret held as the address was asked, before a reading of it folds the host's case or escapes what it holds.
    /// </summary>
    [Fact]
    public async Task WebFetch_IsRefused_ByTheDomainsAndTheMask_NothingSent()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain"));
        var runtime = Open(webDomains: ["docs.example.org"], mask: new Masking("Tok 123"), endpoint: endpoint);

        var domain = await Call(runtime, AgentTools.WebFetch, ("url", "https://example.com/"));
        Assert.False(domain.Ok);

        foreach (var url in new[] { "https://docs.example.org/?key=Tok 123", "https://Tok 123.docs.example.org/", "https://docs.example.org/?to=" + Uri.EscapeDataString("https://x.example.org/?key=" + Uri.EscapeDataString("Tok 123")) })
        {
            var secret = await Call(runtime, AgentTools.WebFetch, ("url", url));
            Assert.Equal("the address holds a secret of this run's, which no request of the web carries", secret.Text);
        }

        Assert.Empty(endpoint.Asked);
        Assert.All(_events.All.OfType<WebRequested>(), requested => Assert.False(requested.Allowed));
        Assert.Equal(AgentToolRuntime.MaskRule, _events.All.OfType<PermissionDenied>().Last().Rule);

        runtime.Dispose();
        var web = File.ReadAllText(Path.Combine(_step, AgentCallRecords.WebFileName));
        Assert.DoesNotContain("tok", web, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://docs.example.org/?key=***", web, StringComparison.Ordinal);
        Assert.Contains("https://docs.example.org/?to=https://x.example.org/?key=***", web, StringComparison.Ordinal);
        Assert.Equal(4, File.ReadAllLines(Path.Combine(_step, AgentCallRecords.DenialsFileName)).Length);
    }

    /// <summary>
    /// web_search is offered to a step that has the web and a source to search, its schema naming the step's sources and
    /// its description saying what each is for; a step with no source, or without the web, is offered none.
    /// </summary>
    [Fact]
    public void WebSearch_IsOfferedWithTheStepsSources_AndNamesThem()
    {
        Assert.DoesNotContain(AgentTools.WebSearch, Names(Open(capabilities: [AgentCapability.Read, AgentCapability.Web])));
        Assert.DoesNotContain(AgentTools.WebSearch, Names(Open(capabilities: [AgentCapability.Read], webSearch: TwoSources())));
        Assert.DoesNotContain(AgentTools.WebSearch, Names(Open(capabilities: [AgentCapability.Read, AgentCapability.Web], webSearch: TwoSources() with { Sources = [] })));

        var tool = Assert.Single(Open(capabilities: [AgentCapability.Read, AgentCapability.Web], webSearch: TwoSources()).Tools, offered => offered.Name == AgentTools.WebSearch);

        static IEnumerable<string> Names(AgentToolRuntime runtime) => runtime.Tools.Select(tool => tool.Name);
        var schema = JsonNode.Parse(tool.Schema)!;

        Assert.Equal(["alpha", "beta"], schema["properties"]!["sources"]!["items"]!["enum"]!.AsArray().Select(name => name!.GetValue<string>()));
        Assert.Equal(["query"], schema["required"]!.AsArray().Select(name => name!.GetValue<string>()));
        Assert.Equal(("integer", 1), (schema["properties"]!["page"]!["type"]!.GetValue<string>(), schema["properties"]!["page"]!["minimum"]!.GetValue<int>()));
        Assert.EndsWith(
            " Each source gives every result of one page of its answer; ask its next page with page, each page one more request of each source asked; name some in sources to ask them alone, else every one is asked. This step's sources:\n- alpha: Alpha's answers\n- beta: Beta's packages",
            tool.Description,
            StringComparison.Ordinal);

        // A host given no search cannot open a step that has sources.
        var ground = PolicyGrounds.Of(_worktree, [_main], [_worktree], [], [], [], [AgentCapability.Web], null, null, null);
        using var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain")));
        var host = new AgentToolHost(_harness.FileSystem, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System);

        Assert.Throws<ArgumentException>(() => host.Open(AgentRuns.Of(_step, _worktree, ground: ground, webSearch: TwoSources()), _events));
    }

    /// <summary>
    /// A search is answered in each source's part, by the harness's own search: each address decided and answered in
    /// web.jsonl with the source that asked it, each part a line of its own, and counted as the step's requests.
    /// </summary>
    [Fact]
    public async Task WebSearch_GivesEachSourcesPart_AndRecordsIt()
    {
        var endpoint = new WebEndpoint(request => request.RequestUri!.Host == "alpha.example.test"
            ? SearchAnswer("""{ "items": [ { "title": "One", "link": "https://one.example.test/" } ] }""")
            : SearchAnswer("not json"));
        var runtime = Open(capabilities: [AgentCapability.Read, AgentCapability.Web], endpoint: endpoint, webSearch: TwoSources());

        var result = await Call(runtime, AgentTools.WebSearch, ("query", "group by"));
        var changed = DssHarness.Core.Web.Search.WebSearchKinds.OfThisBuild.Find("json-api")!
            .Read(TwoSources().Sources[1], DssHarness.Core.Web.Search.WebSearchFound.None, WebSearchFixtures.Answer("not json")).Why;

        Assert.True(result.Ok);
        Assert.Equal(
            DssHarness.Core.Web.WebBlocks.Search("group by", "alpha", "1. One\n   https://one.example.test/")
                + "\n\n" + DssHarness.Core.Web.WebBlocks.Search("group by", "beta", $"beta's answer could not be read (it changed); nothing was read from it: {changed}"),
            result.Text);
        Assert.Equal(2, runtime.Tally.WebRequests);
        Assert.Equal(
            [$"web-search source 'beta' answered what its kind 'json-api' (version 1) could not read: {changed}"],
            _events.All.OfType<Notice>().Select(notice => notice.Text));

        var only = await Call(runtime, AgentTools.WebSearch, ("query", "other"), ("sources", new[] { "alpha" }));

        Assert.True(only.Ok);
        Assert.Equal(3, endpoint.Asked.Count);

        runtime.Dispose();
        var lines = File.ReadAllLines(Path.Combine(_step, AgentCallRecords.WebFileName)).Select(line => JsonNode.Parse(line)!).ToList();
        var searched = lines.Where(line => line["line"]!.GetValue<string>() == "searched").ToList();

        Assert.Equal(["alpha", "beta", "alpha"], searched.Select(line => line["by"]!.GetValue<string>()));
        Assert.Equal(["Ok", "Changed", "Ok"], searched.Select(line => line["outcome"]!.GetValue<string>()));
        Assert.All(lines.Where(line => line["line"]!.GetValue<string>() is "decided" or "answered"), line => Assert.Equal(AgentTools.WebSearch, line["tool"]!.GetValue<string>()));
        Assert.Equal(["alpha", "alpha", "beta"], lines.Where(line => line["line"]!.GetValue<string>() == "decided").Select(line => line["by"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The provider's own search is asked through what its opener gives - the client's loop, as the run stands - and the
    /// part it comes to is one more part of the same search, recorded as every source's is, beside the configured
    /// sources' answers; the run stopped while it searches is no failed part - the stop goes on as every stop of the run
    /// does.
    /// </summary>
    [Fact]
    public async Task WebSearch_TheNativePart_IsAskedThroughItsOpener_TheRunStoppedIsAStop()
    {
        var endpoint = new WebEndpoint(_ => SearchAnswer("""{ "items": [ { "title": "One", "link": "https://one.example.test/" } ] }"""));
        var asked = new List<string>();
        var failing = Open(
            capabilities: [AgentCapability.Read, AgentCapability.Web],
            endpoint: endpoint,
            webSearch: TwoSources(),
            native: (query, _) =>
            {
                asked.Add(query);
                return Task.FromResult(NoWire.FailedPart(query, "the lane of WORK_KEY is held"));
            });

        var answered = await Call(failing, AgentTools.WebSearch, ("query", "group by"), ("sources", new[] { "alpha" }));

        Assert.True(answered.Ok);
        Assert.Equal(["group by"], asked);
        Assert.EndsWith(DssHarness.Core.Web.WebBlocks.Search("group by", "native", "native: the lane of WORK_KEY is held"), answered.Text, StringComparison.Ordinal);
        failing.Dispose();

        var searched = File.ReadAllLines(Path.Combine(_step, AgentCallRecords.WebFileName)).Select(line => JsonNode.Parse(line)!).Where(line => line["line"]!.GetValue<string>() == "searched").ToList();

        Assert.Equal([("alpha", "Ok"), ("native", "Failed")], searched.Select(line => (line["by"]!.GetValue<string>(), line["outcome"]!.GetValue<string>())));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var stopped = Open(
            capabilities: [AgentCapability.Read, AgentCapability.Web],
            endpoint: endpoint,
            webSearch: TwoSources(),
            native: async (_, cancellationToken) =>
            {
                await stop.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("the stop did not reach the search");
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped.RunAsync(Request("call_1", AgentTools.WebSearch, ("query", "group by"), ("sources", new[] { "alpha" })), stop.Token));
    }

    /// <summary>
    /// A runtime opened for a run that carries a native search is given what asks it, and one given an asker carries a
    /// native search: neither is left without the other.
    /// </summary>
    [Fact]
    public void ANativeSearch_AndWhatAsksIt_AreGivenTogether()
    {
        var host = new AgentToolHost(_harness.FileSystem, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output), _harness.ChildSessions, new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain"))), new RunningToolDouble("1.2.3"), TimeProvider.System);
        var run = AgentRuns.Of(_step, _worktree, null, PolicyGrounds.Of(_worktree, [_main], [_worktree], [], [], [], [AgentCapability.Read]));

        Assert.Equal(
            "the run carries a native search, and nothing was given to ask it as the run stands (Parameter 'native')",
            Assert.Throws<ArgumentException>(() => host.Open(run with { NativeSearch = new NoWire() }, _events)).Message);
        Assert.Equal(
            "the run carries no native search, and the runtime was given one to ask (Parameter 'native')",
            Assert.Throws<ArgumentException>(() => host.Open(run, _events, (query, _) => Task.FromResult(NoWire.FailedPart(query, "never")))).Message);
    }

    /// <summary>A provider's own search the runtime never asks itself: the loop asks it, as the run stands.</summary>
    private sealed class NoWire : IAgentNativeSearch
    {
        public string Source => "native";

        public long InputTokens(string query, string model) => 0;

        public Task<AgentNativeSearched> SearchAsync(string query, string model, AgentKey key, CancellationToken cancellationToken)
            => throw new InvalidOperationException("the runtime asked the wire itself");

        /// <summary>The part of a search of <paramref name="query"/> that failed for <paramref name="why"/>, in the one shape the wire gives it.</summary>
        public static DssHarness.Core.Web.Search.WebSearchPart FailedPart(string query, string why) => ((IAgentNativeSearch)new NoWire()).Failed(query, why);
    }

    /// <summary>A query holding the run's secret is denied by the mask, nothing asked; a search no source answered fails, saying what each came to.</summary>
    [Fact]
    public async Task WebSearch_ASecretIsDenied_AndASearchNoneAnsweredFails()
    {
        var endpoint = new WebEndpoint(_ => SearchAnswer("{}", HttpStatusCode.InternalServerError));
        var runtime = Open(capabilities: [AgentCapability.Read, AgentCapability.Web], mask: new Masking("Tok 123"), endpoint: endpoint, webSearch: TwoSources());

        var secret = await Call(runtime, AgentTools.WebSearch, ("query", "find Tok 123"));

        Assert.False(secret.Ok);
        Assert.Equal("the query holds a secret of this run's, which no request of the web carries", secret.Text);
        Assert.Equal(AgentToolRuntime.MaskRule, _events.All.OfType<PermissionDenied>().Last().Rule);
        Assert.Empty(endpoint.Asked);

        var none = await Call(runtime, AgentTools.WebSearch, ("query", "x"));

        Assert.False(none.Ok);
        Assert.Equal(
            "no source answered the search:\n\n"
                + DssHarness.Core.Web.WebBlocks.Search("x", "alpha", "alpha: it answered 500: {}")
                + "\n\n" + DssHarness.Core.Web.WebBlocks.Search("x", "beta", "beta: it answered 500: {}"),
            none.Text);

        runtime.Dispose();
        Assert.DoesNotContain("Tok 123", File.ReadAllText(Path.Combine(_step, AgentCallRecords.DenialsFileName)), StringComparison.Ordinal);
    }

    /// <summary>
    /// A sitemap source's site is decided by the step's own rules, and its index's fill by the run's: a page the fill reads
    /// on a host only the agent's own list refuses is asked all the same, for the index is the repository's.
    /// </summary>
    [Fact]
    public async Task WebSearch_AnIndexIsFilledUnderTheRunsRules()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain"));
        var docs = WebSearchFixtures.Source("docs", """{ "kind": "sitemap", "description": "The docs", "site": "https://docs.example.test", "sitemap": "https://cdn.example.test/sitemap.xml" }""");
        var runtime = Open(
            capabilities: [AgentCapability.Read, AgentCapability.Web],
            restrictions: [new AgentRestriction("Web(cdn.example.test)", AgentPolicySource.Agent)],
            endpoint: endpoint,
            webSearch: TwoSources() with { Sources = [docs] });

        var result = await Call(runtime, AgentTools.WebSearch, ("query", "flexbox"));

        // The fill runs on after the search: waited for until it has asked the sitemap, then stopped.
        for (var waited = 0; endpoint.Asked.Count < 2 && waited < 3000; waited++)
        {
            await Task.Delay(10, Token);
        }

        _indexes!.Dispose();

        Assert.True(result.Ok);
        Assert.Equal(["https://docs.example.test/robots.txt", "https://cdn.example.test/sitemap.xml"], endpoint.Asked.Select(sent => sent.Address.AbsoluteUri));

        // The site itself is the step's to decide: an agent's rule that refuses it refuses the source, nothing asked.
        var refusing = new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain"));
        var refused = Open(
            capabilities: [AgentCapability.Read, AgentCapability.Web],
            restrictions: [new AgentRestriction("Web(docs.example.test)", AgentPolicySource.Agent)],
            endpoint: refusing,
            webSearch: TwoSources() with { Sources = [docs] });

        var answer = await Call(refused, AgentTools.WebSearch, ("query", "flexbox"));

        Assert.False(answer.Ok);
        Assert.Empty(refusing.Asked);
    }

    /// <summary>The runtime's host and the harness's fetch are registered in AddDssHarness, each built from what it registers.</summary>
    [Fact]
    public void TheHostAndTheFetch_AreRegistered_InAddDssHarness()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.Same(provider.GetRequiredService<HarnessFetch>(), provider.GetRequiredService<AgentToolHost>().Fetch);
        Assert.Same(provider.GetRequiredService<DssHarness.Core.Web.Search.HarnessWebSearch>(), provider.GetRequiredService<AgentToolHost>().Search);
        Assert.Same(provider.GetRequiredService<PhaseRunner>(), provider.GetRequiredService<AgentToolHost>().Phases);
        Assert.IsType<SystemWebNames>(provider.GetRequiredService<IWebNames>());

        // The claude-code client serves its steps' web from the same host.
        Assert.Same(provider.GetRequiredService<AgentToolHost>(), provider.GetServices<DssHarness.Core.Agents.IAgentClient>().OfType<DssHarness.Core.Agents.ClaudeCode.ClaudeCodeClient>().Single().Tools);
    }

    /// <summary>A fetch that reads nothing says why, and records the failure.</summary>
    [Fact]
    public async Task WebFetch_ThatReadsNothing_SaysWhy()
    {
        var runtime = Open(endpoint: new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) { Headers = { ContentType = new("image/png") } } }));

        var result = await Call(runtime, AgentTools.WebFetch, ("url", "https://example.com/i.png"));

        Assert.Equal($"'https://example.com/i.png' was not read: its type is 'image/png', and this tool's fetch reads text alone: {HarnessFetch.TextTypesSaid}", result.Text);

        runtime.Dispose();
        Assert.Equal("failed", JsonNode.Parse(File.ReadAllLines(Path.Combine(_step, AgentCallRecords.WebFileName))[^1])!["line"]!.GetValue<string>());
    }

    /// <summary>
    /// An answer is masked, and one longer than a page is kept whole in the step's tool-results and given as its first
    /// page - the event carrying what the model was given, and the file.
    /// </summary>
    [Fact]
    public async Task AnAnswer_IsMasked_AndPagedPastAPage()
    {
        Write("secret.txt", "token=s3cr3t\n");
        Write("big.txt", string.Concat(Enumerable.Range(0, 400).Select(line => new string('x', 99) + "\n")));
        var runtime = Open(mask: new Masking("s3cr3t"));

        Assert.Equal("1\ttoken=***", (await View(runtime, "secret.txt")).Text);

        var big = await Call(runtime, AgentTools.SearchFiles, "big", ("pattern", "x"), ("glob", "big.txt"));
        Assert.Equal(AgentCallRecords.ResultFile(_step, "big"), big.File);
        Assert.StartsWith($"page 1 of 2 · 400 results · ask read_result with id: 'big', page: 2 for the next · the whole is '{big.File}'\nbig.txt:1: ", big.Text, StringComparison.Ordinal);
        Assert.EndsWith("big.txt:400: " + new string('x', 99), File.ReadAllText(big.File!), StringComparison.Ordinal);
        Assert.Equal((big.File, big.Text), (_events.All.OfType<ToolCallFinished>().Last().ResultFile, _events.All.OfType<ToolCallFinished>().Last().Result));
    }

    /// <summary>
    /// read_result gives a call's whole result by the call's id, a page at a time: page 1 where none is asked, and the page
    /// asked - whole lines of the answer kept past the cap, as many as a page's room holds, each page saying which of how
    /// many it is and how to ask for the next - and a page past the last refused with the result's range.
    /// </summary>
    [Fact]
    public async Task ReadResult_GivesPageTwoOfAKeptCallResult()
    {
        Write("big.txt", string.Concat(Enumerable.Range(0, 400).Select(_ => new string('x', 99) + "\n")));
        var runtime = Open();

        var big = await Call(runtime, AgentTools.SearchFiles, "big", ("pattern", "x"), ("glob", "big.txt"));
        var lines = DssHarness.Core.Codebase.CodeFileText.Lines(File.ReadAllText(big.File!));

        // Whole lines while a page's room lasts, each costing its characters and its line's end.
        var pages = new List<List<string>> { new() };
        var left = KeptResults.Room;

        foreach (var line in lines)
        {
            if (line.Length + 1 > left)
            {
                pages.Add([]);
                left = KeptResults.Room;
            }

            pages[^1].Add(line);
            left -= line.Length + 1;
        }

        Assert.Equal(2, pages.Count);

        var first = await Call(runtime, AgentTools.ReadResult, "read_1", ("id", "big"));
        var second = await Call(runtime, AgentTools.ReadResult, "read_2", ("id", "big"), ("page", 2));
        var past = await Call(runtime, AgentTools.ReadResult, "read_3", ("id", "big"), ("page", 3));

        Assert.Equal((true, $"page 1 of 2 · {lines.Count} results · ask again with page: 2 for the next\n{string.Join('\n', pages[0])}"), (first.Ok, first.Text));
        Assert.Equal((true, $"page 2 of 2 · the last\n{string.Join('\n', pages[1])}"), (second.Ok, second.Text));
        Assert.Equal((false, "page 3 of 'read_result big': it has 2 pages (1 to 2)"), (past.Ok, past.Text));
    }

    /// <summary>
    /// read_result refuses an id no result is kept for in this conversation, naming the id, and a page below the first as
    /// every paged answer refuses it.
    /// </summary>
    [Fact]
    public async Task ReadResult_RefusesAnUnknownId_NamingIt()
    {
        var runtime = Open();

        var unknown = await Call(runtime, AgentTools.ReadResult, ("id", "toolu_none"));
        var below = await Call(runtime, AgentTools.ReadResult, ("id", "toolu_none"), ("page", 0));

        Assert.Equal((false, "no result is kept for 'toolu_none' in this conversation"), (unknown.Ok, unknown.Text));
        Assert.Equal((false, "page is 0: pages start at 1"), (below.Ok, below.Text));
    }

    /// <summary>An edit keeps the file's mode: a script stays one the system runs.</summary>
    [Fact]
    public async Task AnEdit_KeepsTheFilesMode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no mode with a file: AnEdit_KeepsTheFilesEncodingAndEndings holds what it keeps.");

        Write("run.sh", "#!/bin/sh\necho one\n");
        var path = Path.Combine(_worktree, "run.sh");
        _harness.FilePermissions.SetExecutable(path, executable: true);
        var runtime = Open();
        await View(runtime, "run.sh");

        Assert.True((await Replace(runtime, "run.sh", "one", "two")).Ok);

        Assert.Equal("#!/bin/sh\necho two\n", Read("run.sh"));
        Assert.True(_harness.FilePermissions.IsExecutable(path), "an edit took the script's execute bit");
    }

    /// <summary>A call refused unrun - one of a handoff turn - is recorded as every denial is.</summary>
    [Fact]
    public async Task ACallRefusedUnrun_IsRecordedAsADenial()
    {
        var runtime = Open();

        var result = await runtime.RefuseAsync(
            Request("h", AgentTools.ViewFile, ("path", "a.txt")),
            new AgentPolicyAnswer.Denied("the handoff", AgentPolicySource.Builtin, "no tool runs in the handoff turn"));

        Assert.Equal(("h", false, "no tool runs in the handoff turn"), (result.Id, result.Ok, result.Text));
        Assert.Equal(AgentTools.ViewFile, Assert.Single(_events.All.OfType<PermissionDenied>()).Subject);
        Assert.Equal((1, 1), (runtime.Tally.Calls, runtime.Tally.Denials));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A program and its arguments that run <paramref name="windows"/> in cmd on Windows, <paramref name="unix"/> in sh elsewhere.</summary>
    private static (string Program, string[] Arguments) Shell(string windows, string unix)
        => OperatingSystem.IsWindows() ? ("cmd", ["/c", windows]) : ("sh", ["-c", unix]);

    private static AgentRestriction Restriction(string text) => new(text, AgentPolicySource.Config);

    private static AgentToolCallRequest Request(string id, string tool, params (string Name, object? Value)[] members)
    {
        var input = new JsonObject();

        foreach (var (name, value) in members)
        {
            input[name] = JsonSerializer.SerializeToNode(value);
        }

        return new AgentToolCallRequest(id, tool, input.ToJsonString());
    }

    /// <summary>
    /// A side-task's write holds the main tree's lock for its duration, as the engine's folds and commits do: it waits
    /// out whatever holds the tree, saying so as the engine's step records say the same of theirs, and nothing it writes
    /// reaches the tree before the other side lets go.
    /// </summary>
    [Fact]
    public async Task ASideTasksWrite_TakesTheMainTreesLock_WaitingOutWhateverHoldsIt_AndSayingSo()
    {
        var runtime = Open(capabilities: [AgentCapability.Read, AgentCapability.Write], sideTask: true);

        var tree = DssHarness.Core.Orchestration.MainTreeWriteLock.NameFor(_worktree);

        using var engine = DssHarness.Core.Platform.MachineWideMutex.TryHold(tree, "a test as the engine");

        Assert.NotNull(engine);

        var waits = DssHarness.Core.Platform.MachineWideHold.WaitBegins(tree);
        var writing = Task.Run(() => Call(runtime, AgentTools.CreateFile, ("path", "w.txt"), ("content", "written\n")));

        // Let go only once the write waits for the lock: it waited then, and wrote nothing meanwhile.
        await waits.WaitAsync(Token);
        Assert.False(writing.IsCompleted, "the side-task's write answered while the engine held the main tree's lock");
        Assert.False(File.Exists(Path.Combine(_worktree, "w.txt")), "the side-task wrote while the engine held the main tree's lock");

        engine.Dispose();

        var result = await writing;

        Assert.True(result.Ok, result.Text);
        Assert.Equal("written\n", Read("w.txt"));

        var said = Assert.Single(_events.All.OfType<Notice>());
        Assert.Matches($@"^waited (a moment|\d+(\.\d)? s) for {Regex.Escape(DssHarness.Core.Orchestration.MainTreeWriteLock.WaitedOutOnFromSideTask)} to end$", said.Text);
    }

    /// <summary>
    /// A step's agent takes no hold of the main tree's lock: its tree is the run's own, held by the run's own means, and
    /// its writes go straight through a lock another holds.
    /// </summary>
    [Fact]
    public async Task AStepAgentsWrite_TakesNoHoldOfTheMainTreesLock()
    {
        var runtime = Open(capabilities: [AgentCapability.Read, AgentCapability.Write]);

        using var engine = DssHarness.Core.Platform.MachineWideMutex.TryHold(
            DssHarness.Core.Orchestration.MainTreeWriteLock.NameFor(_worktree),
            "a test as the engine");

        Assert.NotNull(engine);

        var result = await Call(runtime, AgentTools.CreateFile, ("path", "w.txt"), ("content", "written\n"));

        Assert.True(result.Ok, result.Text);
        Assert.Equal("written\n", Read("w.txt"));
        Assert.Empty(_events.All.OfType<Notice>());
    }

    /// <summary>
    /// A side-task's edit of config.json, where a run is working over the tree, says in the chat where the edit reaches:
    /// the run's steps that start after it, and never one under way - for the agents' configuration is read when each
    /// step starts, as its prompts are. Where no run is live, or the file edited is another, nothing is said.
    /// </summary>
    [Fact]
    public async Task ASideTasksEditOfTheConfiguration_SaysInTheChatWhereItReaches_WhereARunIsLive()
    {
        var runtime = Open(capabilities: [AgentCapability.Read, AgentCapability.Write], sideTask: true, liveRun: () => "r1");

        var edited = await Call(runtime, AgentTools.CreateFile, ("path", ".harness-config/config.json"), ("content", "{}\n"), ("overwrite", true));

        Assert.True(edited.Ok, edited.Text);
        Assert.Equal(
            "run 'r1' is working over this tree: the edit of config.json reaches its steps that start after it, and never one under way",
            Assert.Single(_events.All.OfType<Notice>()).Text);

        var other = await Call(runtime, AgentTools.CreateFile, ("path", "plain.txt"), ("content", "x\n"));

        Assert.True(other.Ok, other.Text);
        Assert.Single(_events.All.OfType<Notice>());

        runtime.Dispose();
        var none = Open(capabilities: [AgentCapability.Read, AgentCapability.Write], sideTask: true);

        var unsaid = await Call(none, AgentTools.CreateFile, ("path", ".harness-config/config.json"), ("content", "{}\n"), ("overwrite", true));

        Assert.True(unsaid.Ok, unsaid.Text);
        Assert.Empty(_events.All.OfType<Notice>().Skip(1));
    }

    /// <summary>
    /// A step whose reads are narrowed (<c>graphReads: narrow</c>) is denied a whole <c>view_file</c> of a file the view
    /// holds as parsed over <see cref="CodebaseGraphTool.NarrowLines"/> lines, with the way to the graph, recorded as
    /// every denial is and as a line of its graph's; a range passes; a file the graph is not sure of passes as an escape
    /// <c>lowConfidence</c>; a <c>wholeFile</c> is an escape <c>stated</c> and lifts the narrowing for its file.
    /// </summary>
    [Fact]
    public async Task AWholeViewOfAAFileOverNarrowLines_IsDeniedWithTheWayToTheGraph_WhileRangesPassAndEscapesAreLines()
    {
        var big = string.Join("\n", ["namespace Demo;", string.Empty, .. Enumerable.Range(1, 220).Select(at => $"// line {at} of the big file")]) + "\n";
        var generated = "// <auto-generated/>\n" + string.Join("\n", ["namespace Demo;", string.Empty, .. Enumerable.Range(1, 220).Select(at => $"// line {at} of the generated file")]) + "\n";
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/Big.cs", big), ("src/Generated.g.cs", generated));
        var queries = await CodebaseGraphToolTests.QueriesAsync(tree);
        var tool = CodebaseGraphToolTests.Tool(tree, queries);
        var lines = new List<GraphStepLine>();
        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var host = new AgentToolHost(
            tree.Harness.FileSystem,
            tree.Harness.Platform,
            tree.Harness.GitClient,
            new PhaseRunner(tree.Harness.ProcessRunner, tree.Harness.FileSystem, tree.Harness.Output),
            tree.Harness.ChildSessions,
            fetch,
            new RunningToolDouble("1.2.3"),
            TimeProvider.System);
        var ground = PolicyGrounds.Of(tree.Tree, [tree.Tree], [tree.Tree], [], [], [], [AgentCapability.Read, AgentCapability.Write]);
        var runtime = host.Open(AgentRuns.Of(_step, tree.Tree, ground: ground) with
        {
            Graph = new AgentGraphSupport(tool, line => lines.Add(line), Operations: CodebaseGraphTool.Operations(write: true, narrow: true), Narrow: true),
        }, _events);

        var denied = await View(runtime, "src/Big.cs");

        Assert.False(denied.Ok);
        Assert.Equal("'src/Big.cs' is 222 lines and in the codebase graph: ask graph for its outline and the bodies you need, read a range of it, or, if you need it whole, say why with graph's wholeFile", denied.Text);
        Assert.Equal((1, 1), (runtime.Tally.Calls, runtime.Tally.Denials));

        var deniedLine = Assert.IsType<GraphDeniedLine>(Assert.Single(lines));
        Assert.Equal(("src/Big.cs", 222), (deniedLine.Path, deniedLine.Lines));
        Assert.True(deniedLine.Tokens > 0);
        Assert.Single(_events.All.OfType<PermissionDenied>(), denied => denied.Rule == "graphReads: narrow");

        // A range of the same file passes, narrowed by nothing.
        var ranged = await Call(runtime, AgentTools.ViewFile, ("path", "src/Big.cs"), ("start_line", 1), ("end_line", 4));
        Assert.True(ranged.Ok, ranged.Text);
        Assert.Single(lines);

        // A file the graph is not sure of passes whole, an escape lowConfidence with why.
        var unsure = await View(runtime, "src/Generated.g.cs");
        Assert.True(unsure.Ok, unsure.Text);
        var escaped = Assert.IsType<GraphEscapeLine>(lines.OfType<GraphEscapeLine>().Single());
        Assert.Equal(("src/Generated.g.cs", GraphEscapeLine.LowConfidence, "the graph holds it as generated code"), (escaped.Path, escaped.Escape, escaped.Why));

        // A wholeFile is an escape stated with the agent's reason, and lifts the narrowing for its file.
        Assert.True((await Call(runtime, AgentGraphSupport.ToolName, ("op", "wholeFile"), ("path", "src/Big.cs"), ("reason", "the move needs it whole"))).Ok);
        var stated = lines.OfType<GraphEscapeLine>().Single(line => line.Escape == GraphEscapeLine.Stated);
        Assert.Equal(("src/Big.cs", "the move needs it whole"), (stated.Path, stated.Why));

        var lifted = await View(runtime, "src/Big.cs");
        Assert.True(lifted.Ok, lifted.Text);
        Assert.Single(lines.OfType<GraphDeniedLine>());
        Assert.Equal(2, lines.OfType<GraphEscapeLine>().Count());

        runtime.Dispose();
        fetch.Dispose();
    }

    /// <summary>
    /// A side-task's write whose take of the main tree's lock fails - the lock cannot be opened - is refused with the lock's
    /// own words, recorded as every denial is, and nothing is written; the run goes on, its next call served.
    /// </summary>
    [Fact]
    public async Task ASideTasksWriteWhoseTakeOfTheTreesLockFails_IsRefusedWithTheLocksWords_AndTheRunGoesOn()
    {
        var failing = true;
        var runtime = Open(
            capabilities: [AgentCapability.Read, AgentCapability.Write],
            sideTask: true,
            treeLock: (main, token) => failing
                ? Task.FromException<DssHarness.Core.Platform.MachineWideHold?>(new DssHarness.Core.Results.HarnessException(DssHarness.Core.Results.HarnessExit.Refused, "The lock 'x' belongs to another user on this machine."))
                : DssHarness.Core.Orchestration.MainTreeWriteLock.HoldAsync(main, token));
        var said = DssHarness.Core.Orchestration.MainTreeWriteLock.Refusal(new InvalidOperationException("The lock 'x' belongs to another user on this machine.")).Reason;

        var refused = await Call(runtime, AgentTools.CreateFile, ("path", "w.txt"), ("content", "written\n"));

        Assert.Equal((false, said), (refused.Ok, refused.Text));
        Assert.False(File.Exists(Path.Combine(_worktree, "w.txt")));
        Assert.Equal(1, runtime.Tally.Denials);

        var denied = Assert.Single(_events.All.OfType<PermissionDenied>());
        Assert.Equal((AgentTools.CreateFile, AgentTools.CreateFile, DssHarness.Core.Orchestration.MainTreeWriteLock.Rule, said), (denied.Tool, denied.Subject, denied.Rule, denied.Reason));
        Assert.Equal(
            "the main tree's lock, which a side-task's write or command holds for its duration, could not be taken: The lock 'x' belongs to another user on this machine",
            said);

        failing = false;
        var written = await Call(runtime, AgentTools.CreateFile, ("path", "w.txt"), ("content", "written\n"));

        Assert.True(written.Ok, written.Text);
        Assert.Equal("written\n", Read("w.txt"));
    }

    /// <summary>
    /// A side-task's <c>run_command</c> that leaves a process running after it ends - a script that runs one in the
    /// background, a program that keeps what it starts apart - is refused by the side-task's policy before anything starts,
    /// as on Claude Code: the session a contained command ends does not reach what left it.
    /// </summary>
    [Fact]
    public async Task ASideTasksRunCommandThatLeavesAProcessBehind_IsRefused_NothingStarted()
    {
        var processes = new Uncontaining();
        var runtime = Open(sideTask: true, processes: processes);

        var backgrounded = await Call(runtime, AgentTools.RunCommand, ("program", "sh"), ("args", new[] { "-c", "make &" }));
        var detached = await Call(runtime, AgentTools.RunCommand, ("program", "setsid"), ("args", new[] { "make" }));

        Assert.Equal((false, DssHarness.Core.Orchestration.MainTreeWriteLock.LeftBehind("sh -c make &").Reason), (backgrounded.Ok, backgrounded.Text));
        Assert.Equal((false, DssHarness.Core.Orchestration.MainTreeWriteLock.LeftBehind("setsid make").Reason), (detached.Ok, detached.Text));
        Assert.Empty(processes.Started);
        Assert.Equal(2, runtime.Tally.Denials);

        var ran = await Call(runtime, AgentTools.RunCommand, ("program", "sh"), ("args", new[] { "-c", "make && make test > test.log 2>&1" }));

        Assert.True(ran.Ok, ran.Text);
        Assert.Single(processes.Started);
    }

    /// <summary>
    /// What stands alone in its turn and what holds a side-task's lock are two questions: a graph <c>wholeFile</c> stands
    /// alone - the narrowed reads beside it are answered by what it lifts, so they run before or after it as the model
    /// ordered them - and holds no lock, for it writes nothing; an <c>edit</c> of the graph's and every tool that writes or
    /// runs do both; the graph's other reads and the runtime's own do neither.
    /// </summary>
    [Fact]
    public async Task AWholeFile_StandsAloneInItsTurn_HoldingNoLock_WhileAnEditDoesBoth()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/Big.cs", BigFile));
        var runtime = Narrowing(tree, CodebaseGraphToolTests.Tool(tree, await CodebaseGraphToolTests.QueriesAsync(tree)), []);

        var calls = new[]
        {
            Request("w1", AgentGraphSupport.ToolName, ("op", "wholeFile"), ("path", "src/Big.cs"), ("reason", "the move needs it whole")),
            Request("e1", AgentGraphSupport.ToolName, ("op", "edit"), ("symbol", "Twice"), ("hash", "#aac0401c"), ("text", "x")),
            Request("f1", AgentGraphSupport.ToolName, ("op", "find"), ("query", "Total")),
            Request("c1", AgentTools.CreateFile, ("path", "n.txt"), ("content", "x")),
            Request("v1", AgentTools.ViewFile, ("path", "src/Big.cs")),
        };

        Assert.Equal(
            [(true, false), (true, true), (false, false), (true, true), (false, false)],
            calls.Select(call => (runtime.Alone(call), runtime.Locks(call))));
    }

    /// <summary>
    /// A side-task's command holds the main tree's lock while it runs, as its write does, and lets it go at its end: the
    /// command finds the lock held, and the tree's lock is free once the call has answered.
    /// </summary>
    [Fact]
    public async Task ASideTasksCommand_HoldsTheMainTreesLockWhileItRuns_AndLetsItGoAtItsEnd()
    {
        var name = DssHarness.Core.Orchestration.MainTreeWriteLock.NameFor(_worktree);
        var processes = new ProbingTheLock(name);
        var runtime = Open(sideTask: true, processes: processes);

        var ran = await Call(runtime, AgentTools.RunCommand, ("program", "tool"), ("args", new[] { "go" }));

        Assert.True(ran.Ok, ran.Text);
        Assert.Equal(["held"], processes.Found);

        using var free = DssHarness.Core.Platform.MachineWideMutex.TryHold(name, "a test as the engine");

        Assert.NotNull(free);
    }

    /// <summary>
    /// The lock a side-task's graph call holds is its operation's: a read of the graph - here a <c>find</c> - holds none of
    /// the main tree's lock, as a <c>view_file</c> holds none, while an <c>edit</c>, a write, holds it as every write of its
    /// does.
    /// </summary>
    [Fact]
    public async Task ASideTasksGraphRead_HoldsNoneOfTheMainTreesLock_WhileItsGraphEditHoldsIt()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync();
        var name = DssHarness.Core.Orchestration.MainTreeWriteLock.NameFor(_worktree);
        var found = new List<(string Asked, string Lock)>();
        string Probe()
        {
            using var taken = DssHarness.Core.Platform.MachineWideMutex.TryHold(name, "a test as the engine");
            return taken is null ? "held" : "free";
        }

        var tool = CodebaseGraphToolTests.Tool(
            tree,
            await CodebaseGraphToolTests.QueriesAsync(tree),
            mayRead: _ =>
            {
                lock (found)
                {
                    found.Add(("read", Probe()));
                }

                return true;
            },
            mayWrite: _ =>
            {
                lock (found)
                {
                    found.Add(("write", Probe()));
                }

                return AgentPolicyAnswer.Allow;
            });
        var runtime = Open(capabilities: [AgentCapability.Read, AgentCapability.Write], sideTask: true, graph: new AgentGraphSupport(tool));

        var read = await Call(runtime, AgentGraphSupport.ToolName, ("op", "find"), ("query", "Total"));

        Assert.True(read.Ok, read.Text);
        Assert.NotEmpty(found);
        Assert.All(found, probe => Assert.Equal("free", probe.Lock));

        found.Clear();
        var edited = await Call(runtime, AgentGraphSupport.ToolName, ("op", "edit"), ("symbol", "Twice"), ("hash", "#aac0401c"), ("text", "    public static double Twice(IShape shape) => 0;"));

        Assert.True(edited.Ok, edited.Text);
        Assert.Contains(("write", "held"), found);
        Assert.All(found, probe => Assert.Equal("held", probe.Lock));
    }

    /// <summary>
    /// A graph edit the policy refuses is answered with the policy's refusal and recorded as every denial is - its event,
    /// the run's count - as a <c>replace_in_file</c> the policy refuses is; the file is as it was.
    /// </summary>
    [Fact]
    public async Task AGraphEditThePolicyRefuses_IsRecordedAsEveryDenialIs()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync();
        var before = File.ReadAllText(Path.Combine(tree.Tree, "src", "Geometry.cs"));
        var refusal = new AgentPolicyAnswer.Denied("Write(src/**)", AgentPolicySource.Config, "'src/Geometry.cs' is denied by Write(src/**)");
        var tool = CodebaseGraphToolTests.Tool(tree, await CodebaseGraphToolTests.QueriesAsync(tree), mayWrite: _ => refusal);
        var runtime = Open(capabilities: [AgentCapability.Read, AgentCapability.Write], graph: new AgentGraphSupport(tool));

        var edited = await Call(runtime, AgentGraphSupport.ToolName, ("op", "edit"), ("symbol", "Twice"), ("hash", "#aac0401c"), ("text", "    public static double Twice(IShape shape) => 0;"));

        Assert.Equal((false, refusal.Reason), (edited.Ok, edited.Text));
        Assert.Equal(1, runtime.Tally.Denials);

        var denied = Assert.Single(_events.All.OfType<PermissionDenied>());
        Assert.Equal((AgentGraphSupport.ToolName, "src/Geometry.cs", "Write(src/**)", refusal.Reason), (denied.Tool, denied.Subject, denied.Rule, denied.Reason));
        Assert.Equal(before, File.ReadAllText(Path.Combine(tree.Tree, "src", "Geometry.cs")));
    }

    /// <summary>
    /// Each write and each command of the run's is followed by its graph's sync, by what it wrote: a file created or edited
    /// by its path, both ends of a move, a directory deleted whole by the directory - which names every file the graph held
    /// beneath it - and a command, which may have written anything, by no path; a read is followed by nothing.
    /// </summary>
    [Fact]
    public async Task EachWriteAndCommand_IsFollowedByTheGraphsSync_ByWhatItWrote()
    {
        var synced = new List<(string Tool, IReadOnlyList<string>? Paths)>();
        var runtime = Open(processes: new Uncontaining(), graph: new AgentGraphSupport(null, AfterWriteAsync: (write, _) =>
        {
            synced.Add((write.Tool, write.Paths));
            return Task.CompletedTask;
        }));

        Assert.True((await Call(runtime, AgentTools.CreateFile, ("path", "src/a.txt"), ("content", "alpha\n"))).Ok);
        Assert.True((await View(runtime, "src/a.txt")).Ok);
        Assert.True((await Replace(runtime, "src/a.txt", "alpha", "beta")).Ok);
        Assert.True((await Call(runtime, AgentTools.MovePath, ("from", "src"), ("to", "lib"))).Ok);
        Assert.True((await Call(runtime, AgentTools.DeleteDirectory, ("path", "lib"), ("recursive", true))).Ok);
        Assert.True((await Call(runtime, AgentTools.RunCommand, ("program", "tool"), ("args", new[] { "go" }))).Ok);

        Assert.Equal(
            [
                (AgentTools.CreateFile, Path.Combine(_worktree, "src", "a.txt")),
                (AgentTools.ReplaceInFile, Path.Combine(_worktree, "src", "a.txt")),
                (AgentTools.MovePath, Path.Combine(_worktree, "src") + " | " + Path.Combine(_worktree, "lib")),
                (AgentTools.DeleteDirectory, Path.Combine(_worktree, "lib")),
                (AgentTools.RunCommand, (string?)null),
            ],
            synced.Select(write => (write.Tool, write.Paths is { } paths ? string.Join(" | ", paths) : null)));
    }

    /// <summary>
    /// A whole view the narrowing denies shows the agent nothing: it is no view an edit may follow and no read of the run's,
    /// while a range of the same file is both.
    /// </summary>
    [Fact]
    public async Task AWholeViewTheNarrowingDenies_IsNoViewAnEditMayFollow_NorARead()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/Big.cs", BigFile));
        var runtime = Narrowing(tree, CodebaseGraphToolTests.Tool(tree, await CodebaseGraphToolTests.QueriesAsync(tree)), []);

        Assert.False((await View(runtime, "src/Big.cs")).Ok);
        Assert.Empty(runtime.Tally.Files);

        var unseen = await Replace(runtime, "src/Big.cs", "// line 1 of the big file", "// the first line of the big file");

        Assert.Equal((false, AgentToolRuntime.NeverViewed), (unseen.Ok, unseen.Text));

        Assert.True((await Call(runtime, AgentTools.ViewFile, ("path", "src/Big.cs"), ("start_line", 1), ("end_line", 4))).Ok);
        Assert.Equal([Path.Combine(tree.Tree, "src", "Big.cs")], runtime.Tally.Files);

        var edited = await Replace(runtime, "src/Big.cs", "// line 1 of the big file", "// the first line of the big file");

        Assert.True(edited.Ok, edited.Text);
    }

    /// <summary>
    /// The narrowing a client's own whole read is asked under reads nothing it cannot narrow: a run that narrows nothing
    /// reads not a byte of the file, and in one that narrows, a file not there, one the view does not parse and one that
    /// cannot be read pass - null, left to the client's own tool - while a parsed file over its lines is denied as a whole
    /// view of it is.
    /// </summary>
    [Fact]
    public async Task TheNarrowingOfAClientsOwnWholeRead_ReadsNothingItCannotNarrow()
    {
        Write("a.txt", "alpha\n");
        var plain = Open(fileSystem: new Held(_harness.FileSystem, Path.Combine(_worktree, "a.txt")));

        Assert.Null(await plain.NarrowedReadAsync("a.txt", Token));

        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/Big.cs", BigFile), ("src/Held.cs", BigFile.Replace("big file", "held file", StringComparison.Ordinal)));
        var lines = new List<GraphStepLine>();
        var runtime = Narrowing(tree, CodebaseGraphToolTests.Tool(tree, await CodebaseGraphToolTests.QueriesAsync(tree)), lines, new Held(tree.Harness.FileSystem, Path.Combine(tree.Tree, "src", "Held.cs")));

        Assert.Null(await runtime.NarrowedReadAsync("src/Missing.cs", Token));
        Assert.Null(await runtime.NarrowedReadAsync("docs/zoo.txt", Token));
        Assert.Null(await runtime.NarrowedReadAsync("src/Held.cs", Token));
        Assert.Empty(lines);

        var denied = await runtime.NarrowedReadAsync("src/Big.cs", Token);

        Assert.Equal(
            ("graphReads: narrow", "'src/Big.cs' is 222 lines and in the codebase graph: ask graph for its outline and the bodies you need, read a range of it, or, if you need it whole, say why with graph's wholeFile"),
            (denied?.Rule, denied?.Reason));
        Assert.Equal(("src/Big.cs", 222), (Assert.IsType<GraphDeniedLine>(Assert.Single(lines)).Path, ((GraphDeniedLine)lines[0]).Lines));
    }

    /// <summary>
    /// A page of a graph body is a view of the files whose lines it served, as they were read, and of no other: after page
    /// 1, a file whose lines only a later page holds is no view an edit may follow; after page 2 - read from what page 1
    /// kept, the file changed on disk since - it is the view of the file as its lines were read, so the edit meets the
    /// change and is refused, never made over lines the agent did not see.
    /// </summary>
    [Fact]
    public async Task APageOfAGraphBody_IsAViewOfTheFilesItServed_AsTheyWereRead()
    {
        var big = "namespace Demo;\n\npublic static class Big\n{\n" + string.Concat(Enumerable.Range(0, 700).Select(at => $"    // line {at}\n")) + "}\n";
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/Big.cs", big));
        using var pages = new TempDirectory();
        var store = new DssHarness.Core.Codebase.GraphPageStore(pages.Combine("graph-pages"), tree.Harness.FileSystem);
        var runtime = Narrowing(tree, CodebaseGraphToolTests.Tool(tree, await CodebaseGraphToolTests.QueriesAsync(tree), sync: false, pages: store), []);
        var geometry = Path.Combine(tree.Tree, "src", "Geometry.cs");

        var first = await Call(runtime, AgentGraphSupport.ToolName, ("op", "body"), ("symbols", new[] { "Demo.Big", "Twice" }), ("full", true));
        var unseen = await Replace(runtime, "src/Geometry.cs", "return Total([shape, shape]);", "return 0;");

        Assert.True(first.Ok, first.Text);
        Assert.Equal((false, AgentToolRuntime.NeverViewed), (unseen.Ok, unseen.Text));

        File.WriteAllText(geometry, "// formatted\n" + File.ReadAllText(geometry));
        var second = await Call(runtime, AgentGraphSupport.ToolName, ("op", "body"), ("symbols", new[] { "Demo.Big", "Twice" }), ("full", true), ("page", 2));
        var stale = await Replace(runtime, "src/Geometry.cs", "return Total([shape, shape]);", "return 0;");

        Assert.True(second.Ok, second.Text);
        Assert.StartsWith("page 2 of 2 · the last\n", second.Text, StringComparison.Ordinal);
        Assert.Equal((false, AgentToolRuntime.ChangedSinceViewed), (stale.Ok, stale.Text));
        Assert.StartsWith("// formatted\n", File.ReadAllText(geometry), StringComparison.Ordinal);
        Assert.Contains("return Total([shape, shape]);", File.ReadAllText(geometry), StringComparison.Ordinal);
    }

    /// <summary>A C# file of 222 lines, past the narrowing's bound.</summary>
    private static readonly string BigFile = string.Join("\n", ["namespace Demo;", string.Empty, .. Enumerable.Range(1, 220).Select(at => $"// line {at} of the big file")]) + "\n";

    /// <summary>A step's runtime over <paramref name="tree"/> whose whole reads are narrowed by <paramref name="tool"/>, its graph lines kept in <paramref name="lines"/>.</summary>
    private AgentToolRuntime Narrowing(CodebaseGraphTree tree, CodebaseGraphTool tool, List<GraphStepLine> lines, IFileSystem? files = null)
    {
        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var host = new AgentToolHost(
            files ?? tree.Harness.FileSystem,
            tree.Harness.Platform,
            tree.Harness.GitClient,
            new PhaseRunner(tree.Harness.ProcessRunner, files ?? tree.Harness.FileSystem, tree.Harness.Output),
            tree.Harness.ChildSessions,
            fetch,
            new RunningToolDouble("1.2.3"),
            TimeProvider.System);
        var ground = PolicyGrounds.Of(tree.Tree, [tree.Tree], [tree.Tree], [], [], [], [AgentCapability.Read, AgentCapability.Write]);
        var runtime = host.Open(AgentRuns.Of(_step, tree.Tree, ground: ground) with
        {
            Graph = new AgentGraphSupport(tool, line => lines.Add(line), Operations: CodebaseGraphTool.Operations(write: true, narrow: true), Narrow: true),
        }, _events);

        _opened.Add(fetch);
        _opened.Add(runtime);

        return runtime;
    }

    private static Task<AgentToolCallResult> Call(AgentToolRuntime runtime, string tool, params (string Name, object? Value)[] members)
        => Call(runtime, tool, "call_1", members);

    private static Task<AgentToolCallResult> Call(AgentToolRuntime runtime, string tool, string id, params (string Name, object? Value)[] members)
        => runtime.RunAsync(Request(id, tool, members), Token);

    private static Task<AgentToolCallResult> View(AgentToolRuntime runtime, string path) => Call(runtime, AgentTools.ViewFile, ("path", path));

    private static Task<AgentToolCallResult> Replace(AgentToolRuntime runtime, string path, string old, string replacement)
        => Call(runtime, AgentTools.ReplaceInFile, ("path", path), ("old_string", old), ("new_string", replacement));

    private void Write(string relative, string text)
    {
        var full = Path.Combine(_worktree, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_worktree, relative));

    private AgentToolRuntime Open(
        IReadOnlyList<AgentCapability>? capabilities = null,
        IReadOnlyList<AgentRestriction>? restrictions = null,
        IReadOnlyList<string>? webDomains = null,
        string? reporterRoot = null,
        IReadOnlyList<string>? writable = null,
        Masking? mask = null,
        AgentCommandSettings? commands = null,
        IReadOnlyList<string>? withheld = null,
        WebEndpoint? endpoint = null,
        IReadOnlyList<string>? claudeCodeDirectories = null,
        IFileSystem? fileSystem = null,
        IReadOnlyList<string>? writes = null,
        AgentLimits? limits = null,
        IProcessRunner? processes = null,
        TimeProvider? clock = null,
        AgentWebSearch? webSearch = null,
        AgentNativeAsk? native = null,
        DssHarness.Core.Agents.Isolation.LaunchIsolation? isolation = null,
        bool sideTask = false,
        Func<string?>? liveRun = null,
        AgentGraphSupport? graph = null,
        AgentMeasureSupport? measure = null,
        Func<string, CancellationToken, Task<DssHarness.Core.Platform.MachineWideHold?>>? treeLock = null)
    {
        var ground = PolicyGrounds.Of(
            _worktree,
            [_main],
            writable ?? [_worktree],
            [],
            claudeCodeDirectories ?? [],
            restrictions ?? [],
            capabilities,
            webDomains,
            writes,
            reporterRoot);

        if (sideTask)
        {
            ground = ground with { SideTask = true, LiveRun = liveRun, WorktreeRules = false, ProtectedPaths = false };
        }

        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, endpoint ?? new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var files = fileSystem ?? _harness.FileSystem;
        var search = webSearch is null ? null : Search(fetch, clock ?? TimeProvider.System);
        var host = new AgentToolHost(files, _harness.Platform, _harness.GitClient, new PhaseRunner(processes ?? _harness.ProcessRunner, files, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), clock ?? TimeProvider.System, search: search)
        {
            TreeLock = treeLock ?? DssHarness.Core.Orchestration.MainTreeWriteLock.HoldAsync,
        };
        var runtime = host.Open(AgentRuns.Of(_step, _worktree, mask, ground, limits, commands, withheld, webSearch) with { NativeSearch = native is null ? null : new NoWire(), Isolation = isolation?.Processes, Graph = graph, Measure = measure }, _events, native);

        _opened.Add(fetch);
        _opened.Add(runtime);

        return runtime;
    }

    /// <summary>The harness's own search over <paramref name="fetch"/>, its pace kept beside the step's records.</summary>
    private DssHarness.Core.Web.Search.HarnessWebSearch Search(HarnessFetch fetch, TimeProvider clock)
    {
        var files = new PhysicalFileSystem(DssHarness.Core.Platform.FilePermissionsFactory.Create());
        var indexes = new DssHarness.Core.Web.Index.WebIndexes(files, new DssHarness.Core.Web.Index.SitemapFill(files, fetch, clock, (_, _) => Task.CompletedTask), clock);
        var pace = new DssHarness.Core.Web.Search.WebSearchPace(files, () => Path.Combine(_step, "..", "pace.json"), clock, (_, _) => Task.CompletedTask, new DssHarness.Core.Execution.ProcessIdentity(new DssHarness.Core.Platform.HostPlatform()));
        var search = new DssHarness.Core.Web.Search.HarnessWebSearch(DssHarness.Core.Web.Search.WebSearchKinds.OfThisBuild, fetch, indexes, pace, files, clock);

        _opened.Add(search);
        _opened.Add(indexes);
        _indexes = indexes;

        return search;
    }

    /// <summary>The step's search of the sources <c>alpha</c> and <c>beta</c>, whose endpoints answer as a test's does.</summary>
    private static AgentWebSearch TwoSources(int maxPerStep = 60)
        => new(
            [
                WebSearchFixtures.Source("alpha", """{ "kind": "json-api", "description": "Alpha's answers", "url": "https://alpha.example.test/?q={query}", "items": "$.items", "title": "{@.title}", "link": "{@.link}" }"""),
                WebSearchFixtures.Source("beta", """{ "kind": "json-api", "description": "Beta's packages", "url": "https://beta.example.test/?q={query}", "items": "$.items", "title": "{@.title}", "link": "{@.link}" }"""),
            ],
            maxPerStep,
            new Dictionary<string, DssHarness.Core.Web.Search.WebSearchKey>());

    private static HttpResponseMessage SearchAnswer(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>
    /// A command that takes <paramref name="takes"/> by <paramref name="clock"/> - moved on by as much as it runs - and
    /// ends well, keeping the time it was given.
    /// </summary>
    private sealed class ClockedCommand(ManualClock clock, TimeSpan takes) : IProcessRunner
    {
        public TimeSpan? Timeout { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Timeout = request.Timeout;
            clock.Advance(takes);

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, takes, TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>A command that finds, as it runs, whether the lock <paramref name="name"/> is held - <c>held</c> or <c>free</c> - and ends at once.</summary>
    private sealed class ProbingTheLock(string name) : IProcessRunner
    {
        public List<string> Found { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            using (var taken = DssHarness.Core.Platform.MachineWideMutex.TryHold(name, "a test's command"))
            {
                Found.Add(taken is null ? "held" : "free");
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>
    /// A process runner that starts nothing: each command ends at once, having printed nothing, its container having
    /// missed a process - or, given <paramref name="stop"/>, is stopped by it, the stop saying what the container missed.
    /// </summary>
    private sealed class Uncontaining(CancellationTokenSource? stop = null) : IProcessRunner
    {
        public List<ProcessRequest> Started { get; } = [];

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Started.Add(request);

            if (stop is not null)
            {
                await stop.CancelAsync();
                throw new ProcessStoppedException("a process outside the job", cancellationToken);
            }

            return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false) { Uncontained = "a process outside the job" };
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>A file system that stops <paramref name="stop"/> once it has been asked what <paramref name="at"/> text files are.</summary>
    private sealed class StoppingAtKinds(IFileSystem inner, CancellationTokenSource stop, int at) : PassThroughFileSystem(inner)
    {
        private int _asked;

        public override PathKind KindOf(string path)
        {
            if (path.EndsWith(".txt", StringComparison.Ordinal) && Interlocked.Increment(ref _asked) == at)
            {
                stop.Cancel();
            }

            return base.KindOf(path);
        }
    }

    /// <summary>
    /// A file system whose walk passes by a directory 'locked' it cannot read, beside each directory walked, and which
    /// cannot say what 'b.txt' is.
    /// </summary>
    private sealed class UnreadPlaces(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override IEnumerable<string> EnumerateReachableFiles(string path, Action<string, Exception> unread)
        {
            unread(Path.Combine(path, "locked"), new UnauthorizedAccessException("it is closed to this process."));
            return base.EnumerateReachableFiles(path, unread);
        }

        public override PathKind KindOf(string path)
            => Path.GetFileName(path) == "b.txt" ? throw new IOException("it could not be looked up.") : base.KindOf(path);
    }

    /// <summary>
    /// A file system that cannot say how large three files are: 'null.txt' and 'range.txt' by a guard's refusal, as a defect
    /// of the tool's own would refuse, and 'held.txt' by a file system's fault.
    /// </summary>
    private sealed class FaultingStandings(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override FileStanding? StandingOf(string path) => Path.GetFileName(path) switch
        {
            "null.txt" => throw new ArgumentNullException(nameof(path)),
            "range.txt" => throw new ArgumentOutOfRangeException(nameof(path)),
            "held.txt" => throw new IOException("it is held by another process."),
            _ => base.StandingOf(path),
        };
    }

    /// <summary>A file system where the file named <paramref name="gone"/> is no longer there, and those named <paramref name="unread"/> cannot be read.</summary>
    private sealed class UnreadFiles(IFileSystem inner, string gone, params string[] unread) : PassThroughFileSystem(inner)
    {
        public override FileStanding? StandingOf(string path) => Path.GetFileName(path) == gone ? null : base.StandingOf(path);

        public override Stream OpenRead(string path)
            => unread.Contains(Path.GetFileName(path)) ? throw new IOException("it is held by another process.") : base.OpenRead(path);
    }

    /// <summary>
    /// A file system whose open files stand elsewhere than their paths led, as a link put on their way after the check
    /// would make them: a file named in <paramref name="opened"/> stands at the place it maps it to, and a directory a write
    /// holds stands at <paramref name="begun"/>, as one moved there since would.
    /// </summary>
    private sealed class OpenedElsewhere(IFileSystem inner, IReadOnlyDictionary<string, string> opened, string begun) : PassThroughFileSystem(inner)
    {
        public override HeldDirectory Hold(string anchor, string directory, bool make, string? asked = null) => base.Hold(begun, begun, make, asked);

        public override string FinalPathOf(Stream stream)
        {
            var real = base.FinalPathOf(stream);
            var name = Path.GetFileName(real);

            return opened.TryGetValue(name, out var elsewhere) ? elsewhere
                : name.Contains(".tmp-", StringComparison.Ordinal) ? Path.Combine(begun, name)
                : real;
        }
    }

    /// <summary>
    /// A file system that makes <paramref name="directory"/> a real link to <paramref name="target"/> - the directory moved
    /// aside - the moment a file under it is opened to be read or written, after its path was decided.
    /// </summary>
    private sealed class LinkedAtOpen(IFileSystem inner, string directory, string target) : PassThroughFileSystem(inner)
    {
        public override Stream OpenRead(string path)
        {
            Link(path);
            return base.OpenRead(path);
        }

        public override HeldDirectory Hold(string anchor, string held, bool make, string? asked = null)
        {
            Link(asked ?? held);
            return base.Hold(anchor, held, make, asked);
        }

        private void Link(string path)
        {
            if (path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !IsLink(directory))
            {
                Directory.Move(directory, directory + "-was");
                TestLinks.DirectoryLink(directory, target);
            }
        }
    }

    /// <summary>A file system whose reads of files under a directory take a while, which counts how many are under way at once.</summary>
    private sealed class SlowReads(IFileSystem inner, string under) : PassThroughFileSystem(inner)
    {
        private int _open;
        private int _most;

        /// <summary>The most reads under way at once.</summary>
        public int Most => Volatile.Read(ref _most);

        public override Stream OpenRead(string path)
        {
            if (!path.StartsWith(under, StringComparison.Ordinal))
            {
                return base.OpenRead(path);
            }

            var open = Interlocked.Increment(ref _open);

            for (var most = Volatile.Read(ref _most); open > most; most = Volatile.Read(ref _most))
            {
                Interlocked.CompareExchange(ref _most, open, most);
            }

            return new Slow(base.OpenRead(path), () => Interlocked.Decrement(ref _open));
        }

        /// <summary>Where the file a slowed stream wraps stands, as the file system names the stream it opened.</summary>
        public override string FinalPathOf(Stream opened) => base.FinalPathOf(opened is Slow slow ? slow.Inner : opened);

        /// <summary>A stream each read of which waits a little first, and says when it is closed.</summary>
        private sealed class Slow(Stream inner, Action closed) : Stream
        {
            /// <summary>The stream the file system opened, which this one slows.</summary>
            public Stream Inner => inner;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => inner.Length;

            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(50, cancellationToken);
                return await inner.ReadAsync(buffer, cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    inner.Dispose();
                    closed();
                }

                base.Dispose(disposing);
            }
        }
    }
}
