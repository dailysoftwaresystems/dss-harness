using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents;
using DssHarness.Core.Application;
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
/// masked and cut, and recorded.
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

    /// <summary>A step is offered the tools of its capabilities, by name; the run's reporter the three that read and no other.</summary>
    [Fact]
    public void TheToolsOffered_AreThoseOfTheStepsCapabilities()
    {
        Assert.Equal(
            [AgentTools.CreateDirectory, AgentTools.CreateFile, AgentTools.DeleteDirectory, AgentTools.DeleteFile, AgentTools.FindFiles, AgentTools.InsertInFile,
             AgentTools.MovePath, AgentTools.ReplaceInFile, AgentTools.RunCommand, AgentTools.SearchFiles, AgentTools.ViewFile, AgentTools.WebFetch],
            Names(Open()));
        Assert.Equal([AgentTools.FindFiles, AgentTools.SearchFiles, AgentTools.ViewFile], Names(Open(capabilities: [AgentCapability.Read])));
        Assert.Equal([AgentTools.FindFiles, AgentTools.SearchFiles, AgentTools.ViewFile, AgentTools.WebFetch], Names(Open(capabilities: [AgentCapability.Read, AgentCapability.Web])));
        Assert.Equal([AgentTools.FindFiles, AgentTools.SearchFiles, AgentTools.ViewFile], Names(Open(reporterRoot: _worktree)));

        static IEnumerable<string> Names(AgentToolRuntime runtime) => runtime.Tools.Select(tool => tool.Name);
    }

    /// <summary>A call of a tool the step was not given is refused, and recorded as every denial is: a line, an event, its end.</summary>
    [Fact]
    public async Task ACallOfAToolNotGiven_IsRefused_AndRecorded()
    {
        var runtime = Open(capabilities: [AgentCapability.Read]);

        var result = await Call(runtime, AgentTools.RunCommand, ("program", "git"));

        Assert.False(result.Ok);
        Assert.Equal("'run_command' is no tool this step is given: it is given find_files, search_files, view_file", result.Text);

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

    /// <summary>A file is viewed numbered, from a line to a line; a long one says how to ask for the rest; a directory is listed.</summary>
    [Fact]
    public async Task AView_NumbersItsLines_AndListsADirectory()
    {
        Write("a.txt", "one\ntwo\nthree\n");
        Write("long.txt", string.Concat(Enumerable.Range(1, 2500).Select(line => $"l{line}\n")));
        Write("empty.txt", string.Empty);
        Directory.CreateDirectory(Path.Combine(_worktree, "src"));
        File.WriteAllBytes(Path.Combine(_worktree, "bin.dat"), [0x61, 0x00, 0x62]);
        var runtime = Open();

        Assert.Equal("1\tone\n2\ttwo\n3\tthree", (await View(runtime, "a.txt")).Text);
        Assert.Equal("2\ttwo", (await Call(runtime, AgentTools.ViewFile, ("path", "a.txt"), ("start_line", 2), ("end_line", 2))).Text);
        Assert.Equal("3\tthree", (await Call(runtime, AgentTools.ViewFile, ("path", "a.txt"), ("start_line", 3))).Text);
        Assert.Equal("'a.txt' has 3 line(s): start_line 4 is past its end", (await Call(runtime, AgentTools.ViewFile, ("path", "a.txt"), ("start_line", 4))).Text);
        Assert.Equal("end_line 1 is before start_line 2", (await Call(runtime, AgentTools.ViewFile, ("path", "a.txt"), ("start_line", 2), ("end_line", 1))).Text);

        // A number past what a double holds is past the bound, never a fault.
        var huge = await runtime.RunAsync(new AgentToolCallRequest("h", AgentTools.ViewFile, """{"path":"a.txt","start_line":1e400}"""), Token);
        Assert.Equal("'a.txt' has 3 line(s): start_line 2147483647 is past its end", huge.Text);

        var first = (await View(runtime, "long.txt")).Text;
        Assert.StartsWith("1\tl1\n", first, StringComparison.Ordinal);
        Assert.EndsWith("2000\tl2000\n[lines 1-2000 of 2500; ask again with start_line 2001]", first, StringComparison.Ordinal);
        Assert.EndsWith("2500\tl2500", (await Call(runtime, AgentTools.ViewFile, ("path", "long.txt"), ("start_line", 2001))).Text, StringComparison.Ordinal);
        Assert.EndsWith("2000\tl2000\n[lines 1-2000 of 2500; ask again with start_line 2001]", (await Call(runtime, AgentTools.ViewFile, ("path", "long.txt"), ("end_line", 2500))).Text, StringComparison.Ordinal);

        Assert.Equal("'empty.txt' is empty", (await View(runtime, "empty.txt")).Text);
        Assert.Equal($"'bin.dat' is binary: {EncodedText.BinaryWhy}", (await View(runtime, "bin.dat")).Text);
        Assert.Equal("there is no file or directory at 'nope.txt'", (await View(runtime, "nope.txt")).Text);
        Assert.Equal("./ holds 5 entry(ies):\na.txt\nbin.dat\nempty.txt\nlong.txt\nsrc/", (await View(runtime, ".")).Text);
        Assert.Equal("src/ is empty", (await View(runtime, "src")).Text);
    }

    /// <summary>
    /// A view is held under the cap of a call's answer, so it is never cut: it stops at the last whole line the bound holds
    /// and says how to ask for the rest, and a line longer than the bound is given as far as it reaches, and said so.
    /// </summary>
    [Fact]
    public async Task AView_IsHeldUnderTheCap_AndALongLineIsGivenAsFarAsItReaches()
    {
        var line = new string('x', 99);
        Write("wide.txt", string.Concat(Enumerable.Repeat(line + "\n", 400)));
        var runtime = Open();

        var shown = 0;
        var length = 0;

        while ($"{shown + 1}\t{line}\n".Length + length <= AgentTools.MostViewedCharacters)
        {
            length += $"{shown + 1}\t{line}\n".Length;
            shown++;
        }

        var wide = await View(runtime, "wide.txt");
        Assert.Null(wide.File);
        Assert.Equal(
            string.Concat(Enumerable.Range(1, shown).Select(at => $"{at}\t{line}\n")) + $"[lines 1-{shown} of 400; ask again with start_line {shown + 1}]",
            wide.Text);

        var longest = new string('y', 40_000);
        Write("long.txt", longest);
        var cut = await View(runtime, "long.txt");
        Assert.Null(cut.File);
        Assert.Equal(
            $"1\t{longest[..(AgentTools.MostViewedCharacters - 2)]}\n[line 1 is 40000 characters: the first {AgentTools.MostViewedCharacters - 2} are given]",
            cut.Text);

        // A directory's entries are held under the cap as a file's lines are, and paged by the same start.
        var names = Enumerable.Range(0, 400).Select(at => $"{at:D4}{new string('n', 116)}").ToList();

        foreach (var name in names)
        {
            Write(Path.Combine("many", name), string.Empty);
        }

        var heading = "many/ holds 400 entry(ies):\n";
        var listed = 0;

        while (heading.Length + names.Take(listed + 1).Sum(name => name.Length + 1) <= AgentTools.MostViewedCharacters)
        {
            listed++;
        }

        var page = await View(runtime, "many");
        Assert.Null(page.File);
        Assert.Equal($"{heading}{string.Join('\n', names.Take(listed))}\n[entries 1-{listed} of 400; ask again with start_line {listed + 1}]", page.Text);

        var next = await Call(runtime, AgentTools.ViewFile, ("path", "many"), ("start_line", listed + 1), ("end_line", listed + 2));
        Assert.Equal($"{heading}{string.Join('\n', names.Skip(listed).Take(2))}", next.Text);
        Assert.Equal("many/ holds 400 entry(ies): start_line 401 is past its end", (await Call(runtime, AgentTools.ViewFile, ("path", "many"), ("start_line", 401))).Text);

        // The room left for the notes holds them at their widest: every number at the most a count or a length can be.
        var widest = $"\n[line {int.MaxValue} is {int.MaxValue} characters: the first {int.MaxValue} are given]\n".Length
            + $"[entries {int.MaxValue}-{int.MaxValue} of {int.MaxValue}; ask again with start_line {int.MaxValue + 1L}]".Length;
        Assert.Equal(AgentCallRecords.ResultCap - widest, AgentTools.MostViewedCharacters);

        // Far into a file both notes are as wide as their numbers, and the view still fits under the cap whole.
        Write("far.txt", string.Concat(Enumerable.Repeat("a\n", 9_999)) + longest + "\nb");
        var far = await Call(runtime, AgentTools.ViewFile, ("path", "far.txt"), ("start_line", 10_000));
        Assert.Null(far.File);
        Assert.Equal(
            $"10000\t{longest[..(AgentTools.MostViewedCharacters - 6)]}\n[line 10000 is 40000 characters: the first {AgentTools.MostViewedCharacters - 6} are given]\n"
                + "[lines 10000-10000 of 10001; ask again with start_line 10001]",
            far.Text);
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

        // A file past the bound is held whole by neither: refused before it is read, a view of it and an edit of one grown past it.
        var past = new string('x', (int)AgentTools.MostViewedBytes + 1);
        var refused = $"'a.txt' holds {DiskSpace.Size(AgentTools.MostViewedBytes + 1)}, past the 16 MiB a view or an edit holds whole: search it, or read a part of it with a command";
        await View(runtime, "a.txt");
        Write("a.txt", past);
        Assert.Equal(refused, (await Replace(runtime, "a.txt", "x", "y")).Text);
        Assert.Equal(refused, (await View(runtime, "a.txt")).Text);
        Assert.Equal(past.Length, new FileInfo(Path.Combine(_worktree, "a.txt")).Length);
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

    /// <summary>A listing gives at most its bound of files, and says how many more there were.</summary>
    [Fact]
    public async Task FindFiles_GivesAtMostItsBound()
    {
        foreach (var at in Enumerable.Range(0, AgentTools.MostFound + 1))
        {
            Write($"many/f{at:D3}.txt", string.Empty);
        }

        var found = await Call(Open(), AgentTools.FindFiles, ("pattern", "*.txt"), ("path", "many"));

        Assert.Equal(
            string.Join('\n', Enumerable.Range(0, AgentTools.MostFound).Select(at => $"many/f{at:D3}.txt")) + "\n[1 more file(s) not shown: narrow the pattern or the directory]",
            found.Text);
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
            "src/a.cs:2: beta\nsrc/b.cs:1: beta two\n[1 file(s) you may not read were left out]\n[1 file(s) passed over: binary, over 2 MiB, or no text this tool reads]",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"))).Text);
        Assert.Equal(
            "src/a.cs-1- alpha\nsrc/a.cs:2: beta\nsrc/a.cs-3- gamma\n--\nsrc/b.cs:1: beta two\n[1 file(s) you may not read were left out]",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"), ("glob", "*.cs"), ("context", 1))).Text);
        Assert.Equal(
            "src/a.cs:2: beta\n[1 more matching line(s) not shown: narrow the search, or raise max_results]\n[1 file(s) you may not read were left out]",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "beta"), ("glob", "*.cs"), ("max_results", 1))).Text);
        Assert.Equal("src/a.cs:2: beta", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "^BETA$"), ("case_insensitive", true), ("path", "src"), ("glob", "a.cs"))).Text);
        Assert.Equal("no line matches, in 2 file(s) read\n[1 file(s) you may not read were left out]", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "zeta"), ("glob", "*.cs"))).Text);

        Assert.StartsWith("pattern '(' is no .NET regular expression: ", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "("))).Text, StringComparison.Ordinal);
        Assert.Equal("context is 11, and is 0 to 10", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "a"), ("context", 11))).Text);
        Assert.Equal("max_results is 0, and is 1 to 500", (await Call(runtime, AgentTools.SearchFiles, ("pattern", "a"), ("max_results", 0))).Text);
    }

    /// <summary>A search passes over a file past its bound, and refuses a pattern that takes past its time on a file's text.</summary>
    [Fact]
    public async Task SearchFiles_PassesOverAFilePastItsBound_AndRefusesAPatternPastItsTime()
    {
        Write("big/huge.txt", new string('a', (int)AgentFiles.MostSearchedBytes) + "\n");
        Write("big/small.txt", "small\n");
        Write("slow/a.txt", new string('a', 40) + "!\n");
        var runtime = Open();

        Assert.Equal(
            "no line matches, in 1 file(s) read\n[1 file(s) passed over: binary, over 2 MiB, or no text this tool reads]",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "zz"), ("path", "big"))).Text);
        Assert.Equal(
            "pattern '(a+)+$' took longer than 2 s on a file's text: make it simpler",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "(a+)+$"), ("path", "slow"))).Text);
    }

    /// <summary>A pattern's time is a file's, its lines together: past the bound across them the search is refused, though no one line is.</summary>
    [Fact]
    public async Task SearchFiles_HoldsAPatternToItsTimeOnAFileWhole()
    {
        Write("lines/a.txt", "a\nb\nc\nd\ne\n");
        var runtime = Open(clock: new TickingClock());

        Assert.Equal(
            "pattern 'z' took longer than 2 s on a file's text: make it simpler",
            (await Call(runtime, AgentTools.SearchFiles, ("pattern", "z"), ("path", "lines"))).Text);
    }

    /// <summary>A search stops between any two lines of a file when it is stopped, not only between files.</summary>
    [Fact]
    public async Task ASearch_StopsBetweenTheLinesOfAFile()
    {
        Write("lines/a.txt", "a\nb\nc\nd\ne\n");
        using var stop = new CancellationTokenSource();
        var files = new AgentFiles(_harness.GitClient, _harness.FileSystem, new StoppingClock(stop));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.SearchAsync(
            Path.Combine(_worktree, "lines"), new Regex("z"), null, StringComparison.Ordinal, 0, 10, _ => true, stop.Token));
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
        foreach (var at in Enumerable.Range(0, 6))
        {
            Write($"f{at}.txt", $"{at}");
        }

        var reads = new SlowReads(_harness.FileSystem, _worktree);
        var runtime = Open(fileSystem: reads);

        var results = await runtime.RunTurnAsync([.. Enumerable.Range(0, 6).Select(at => Request($"{at}", AgentTools.ViewFile, ("path", $"f{at}.txt")))], Token);

        Assert.Equal(Enumerable.Range(0, 6).Select(at => $"1\t{at}"), results.Select(result => result.Text));
        Assert.Equal(AgentToolRuntime.MostTogether, reads.Most);
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
        Assert.EndsWith(
            $" Each source gives at most {DssHarness.Core.Web.Search.WebSearchSources.DefaultResultsPerSource} results; name some in sources to ask them alone, else every one is asked. This step's sources:\n- alpha: Alpha's answers\n- beta: Beta's packages",
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

    /// <summary>An answer is masked, and one past the cap is cut, the whole kept in the step's tool-results.</summary>
    [Fact]
    public async Task AnAnswer_IsMasked_AndCutPastTheCap()
    {
        Write("secret.txt", "token=s3cr3t\n");
        Write("big.txt", string.Concat(Enumerable.Range(0, 400).Select(line => new string('x', 99) + "\n")));
        var runtime = Open(mask: new Masking("s3cr3t"));

        Assert.Equal("1\ttoken=***", (await View(runtime, "secret.txt")).Text);

        var big = await Call(runtime, AgentTools.SearchFiles, "big", ("pattern", "x"), ("glob", "big.txt"), ("max_results", AgentTools.MostSearchResults));
        Assert.Equal(AgentCallRecords.ResultFile(_step, "big"), big.File);
        Assert.Equal(AgentCallRecords.Cut(File.ReadAllText(big.File!), big.File!), big.Text);
        Assert.EndsWith("big.txt:400: " + new string('x', 99), File.ReadAllText(big.File!), StringComparison.Ordinal);
        Assert.Equal((big.File, big.Text), (_events.All.OfType<ToolCallFinished>().Last().ResultFile, _events.All.OfType<ToolCallFinished>().Last().Result));
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
        AgentWebSearch? webSearch = null)
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

        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, endpoint ?? new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var files = fileSystem ?? _harness.FileSystem;
        var search = webSearch is null ? null : Search(fetch, clock ?? TimeProvider.System);
        var host = new AgentToolHost(files, _harness.Platform, _harness.GitClient, new PhaseRunner(processes ?? _harness.ProcessRunner, files, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), clock ?? TimeProvider.System, search: search);
        var runtime = host.Open(AgentRuns.Of(_step, _worktree, mask, ground, limits, commands, withheld, webSearch), _events);

        _opened.Add(fetch);
        _opened.Add(runtime);

        return runtime;
    }

    /// <summary>The harness's own search over <paramref name="fetch"/>, its pace kept beside the step's records.</summary>
    private DssHarness.Core.Web.Search.HarnessWebSearch Search(HarnessFetch fetch, TimeProvider clock)
    {
        var files = new PhysicalFileSystem(DssHarness.Core.Platform.FilePermissionsFactory.Create());
        var indexes = new DssHarness.Core.Web.Index.WebIndexes(files, new DssHarness.Core.Web.Index.SitemapFill(files, fetch, clock, (_, _) => Task.CompletedTask), clock);
        var pace = new DssHarness.Core.Web.Search.WebSearchPace(files, () => Path.Combine(_step, "..", "pace.json"), clock, (_, _) => Task.CompletedTask);
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
            DssHarness.Core.Web.Search.WebSearchSources.DefaultResultsPerSource,
            maxPerStep,
            new Dictionary<string, DssHarness.Core.Web.Search.WebSearchKey>());

    private static HttpResponseMessage SearchAnswer(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    /// <summary>A clock that never moves, and stops <paramref name="stop"/> at its second reading: once a search has read a line of a file.</summary>
    private sealed class StoppingClock(CancellationTokenSource stop) : TimeProvider
    {
        private int _readings;

        public override long GetTimestamp()
        {
            if (Interlocked.Increment(ref _readings) == 2)
            {
                stop.Cancel();
            }

            return 0;
        }
    }

    /// <summary>A clock whose every reading is a second past the last: each line a search reads takes it a second.</summary>
    private sealed class TickingClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => 1;

        public override long GetTimestamp() => Interlocked.Increment(ref _ticks);
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

    /// <summary>A file system where the file named <paramref name="gone"/> is no longer there, and those named <paramref name="unread"/> cannot be read.</summary>
    private sealed class UnreadFiles(IFileSystem inner, string gone, params string[] unread) : PassThroughFileSystem(inner)
    {
        public override FileStanding? StandingOf(string path) => Path.GetFileName(path) == gone ? null : base.StandingOf(path);

        public override Stream OpenRead(string path)
            => unread.Contains(Path.GetFileName(path)) ? throw new IOException("it is held by another process.") : base.OpenRead(path);
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

        /// <summary>A stream each read of which waits a little first, and says when it is closed.</summary>
        private sealed class Slow(Stream inner, Action closed) : Stream
        {
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
