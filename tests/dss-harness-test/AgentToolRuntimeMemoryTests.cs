using System.Globalization;
using System.Text;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Execution;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// The harness's own tools on what no answer holds whole: a command's output of 200 MB, its last page read by read_result,
/// and a file of a gigabyte viewed a page at a time and edited by copying it through - each in a heap that grows by a few
/// pages, never by the file.
/// </summary>
/// <remarks>Measured alone (<see cref="MemoryMeasured"/>): the heap each is held to is the process's.</remarks>
[Collection(MemoryMeasured.Name)]
public sealed class AgentToolRuntimeMemoryTests : IDisposable
{
    /// <summary>
    /// How much more this process's heap may hold while a file of hundreds of megabytes is paged or edited: many times what
    /// a page, its marks and an edit's blocks hold, and a small part of the file held whole.
    /// </summary>
    private const long HeapBound = 32L * 1024 * 1024;

    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();
    private readonly List<IDisposable> _opened = [];
    private readonly string _worktree;
    private readonly string _step;

    public AgentToolRuntimeMemoryTests()
    {
        _worktree = _harness.FileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine("worktree")).FullName);
        _step = Path.Combine(_harness.FileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine("run")).FullName), "step");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var opened in _opened)
        {
            opened.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>
    /// A command's output of some 200 MB, kept whole as its log, is read by read_result to its last page directly - its
    /// pages laid once by streaming the log, the last read by seeking to its mark - in a heap that grows by no more than a
    /// few pages, for no read of it holds it whole.
    /// </summary>
    [Fact]
    public async Task ReadResult_GivesTheLastPageOfATwoHundredMegabyteOutput()
    {
        const int Lines = 2_000_000;
        var log = AgentCallRecords.ResultFile(_step, "build");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);

        using (var writer = new StreamWriter(log, append: false, new UTF8Encoding(false)))
        {
            for (var at = 1; at <= Lines; at++)
            {
                writer.Write("compiling unit ");
                writer.Write(at.ToString("D9", CultureInfo.InvariantCulture));
                writer.Write(' ');
                writer.Write(new string('c', 75));
                writer.Write('\n');
            }
        }

        Assert.True(new FileInfo(log).Length >= 200L * 1000 * 1000);

        var runtime = Open();
        AgentToolCallResult first;
        AgentToolCallResult last;
        int pages;

        using (var heap = new HeapWatch())
        {
            first = await runtime.RunAsync(Request("read_1", AgentTools.ReadResult, ("id", "build")), Token);
            pages = int.Parse(first.Text.Split('\n')[0].Split(' ')[3], CultureInfo.InvariantCulture);
            last = await runtime.RunAsync(Request("read_last", AgentTools.ReadResult, ("id", "build"), ("page", pages)), Token);

            Assert.True(heap.Growth <= HeapBound, $"the heap grew by {heap.Growth:N0} bytes as the output was paged, past the bound of {HeapBound:N0}: {heap.Where}");
        }

        Assert.True(first.Ok, first.Text);
        Assert.True(pages > 7_000, $"200 MB is {pages} pages");
        Assert.StartsWith($"page 1 of {pages} · {Lines} results · ask again with page: 2 for the next\ncompiling unit 000000001 ", first.Text, StringComparison.Ordinal);
        Assert.True(last.Ok, last.Text);
        Assert.StartsWith($"page {pages} of {pages} · the last\n", last.Text, StringComparison.Ordinal);
        Assert.EndsWith($"compiling unit {Lines.ToString("D9", CultureInfo.InvariantCulture)} {new string('c', 75)}", last.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file of a gigabyte - past the 16 MiB a view or an edit once refused - is viewed a page at a time, its last page and
    /// a range near its end among them, and edited twice, a string replaced near its end and lines inserted near its start,
    /// each copied through a block at a time: the heap grows by no more than a few pages and an edit's blocks, the file is
    /// what the edits made of it, and an edit after them needs no view between, for the runtime knows what it wrote.
    /// </summary>
    [Fact]
    public async Task AGigabyteFile_IsViewedAndEdited_InBoundedMemory()
    {
        const long Size = 1024L * 1024 * 1024;
        var full = Path.Combine(_worktree, "dump.sql");
        var row = "INSERT INTO t VALUES (" + new string('7', 76) + ");";
        long lines = 0;

        using (var writer = new StreamWriter(full, append: false, new UTF8Encoding(false), 1 << 20))
        {
            for (long written = 0; written < Size; written += row.Length + 1)
            {
                writer.Write(row);
                writer.Write('\n');
                lines++;
            }

            writer.Write("-- the needle\n");
            lines++;
        }

        var length = new FileInfo(full).Length;
        Assert.True(length > Size);

        var runtime = Open();
        AgentToolCallResult first;
        AgentToolCallResult end;
        AgentToolCallResult range;
        AgentToolCallResult replaced;
        AgentToolCallResult inserted;
        int pages;

        using (var heap = new HeapWatch())
        {
            first = await runtime.RunAsync(Request("v1", AgentTools.ViewFile, ("path", "dump.sql")), Token);
            pages = int.Parse(first.Text.Split('\n')[0].Split(' ')[3], CultureInfo.InvariantCulture);
            end = await runtime.RunAsync(Request("v2", AgentTools.ViewFile, ("path", "dump.sql"), ("page", pages)), Token);
            range = await runtime.RunAsync(Request("v3", AgentTools.ViewFile, ("path", "dump.sql"), ("start_line", lines)), Token);
            replaced = await runtime.RunAsync(Request("e1", AgentTools.ReplaceInFile, ("path", "dump.sql"), ("old_string", "the needle"), ("new_string", "the thread")), Token);
            inserted = await runtime.RunAsync(Request("e2", AgentTools.InsertInFile, ("path", "dump.sql"), ("line", 2), ("text", "-- inserted")), Token);

            Assert.True(heap.Growth <= HeapBound, $"the heap grew by {heap.Growth:N0} bytes as the file was viewed and edited, past the bound of {HeapBound:N0}: {heap.Where}");
        }

        Assert.True(first.Ok, first.Text);
        Assert.StartsWith($"page 1 of {pages} · {lines} results · ask again with page: 2 for the next\n1\t{row}\n2\t{row}\n", first.Text, StringComparison.Ordinal);
        Assert.EndsWith($"\n{lines}\t-- the needle", end.Text, StringComparison.Ordinal);
        Assert.Equal($"{lines}\t-- the needle", range.Text);
        Assert.Equal((true, $"edited 'dump.sql': {lines} line(s) now"), (replaced.Ok, replaced.Text));
        Assert.Equal((true, $"edited 'dump.sql': {lines + 1} line(s) now"), (inserted.Ok, inserted.Text));

        // The file is what the edits made of it: one line more near its start, the needle a thread at its end.
        Assert.Equal(length + "-- inserted\n".Length, new FileInfo(full).Length);

        using var read = File.OpenRead(full);
        var head = new byte[(row.Length + 1) + "-- inserted\n".Length];
        read.ReadExactly(head);
        Assert.Equal($"{row}\n-- inserted\n", Encoding.UTF8.GetString(head));

        var tail = new byte["-- the thread\n".Length];
        read.Seek(-tail.Length, SeekOrigin.End);
        read.ReadExactly(tail);
        Assert.Equal("-- the thread\n", Encoding.UTF8.GetString(tail));
    }

    private static AgentToolCallRequest Request(string id, string tool, params (string Name, object? Value)[] members)
    {
        var input = new System.Text.Json.Nodes.JsonObject();

        foreach (var (name, value) in members)
        {
            input[name] = System.Text.Json.JsonSerializer.SerializeToNode(value);
        }

        return new AgentToolCallRequest(id, tool, input.ToJsonString());
    }

    /// <summary>A runtime of a step that reads and writes in the worktree, its records in the step's directory.</summary>
    private AgentToolRuntime Open()
    {
        var ground = PolicyGrounds.Of(_worktree, [], [_worktree], [], [], [], [AgentCapability.Read, AgentCapability.Write]);
        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var host = new AgentToolHost(
            _harness.FileSystem,
            _harness.Platform,
            _harness.GitClient,
            new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output),
            _harness.ChildSessions,
            fetch,
            new RunningToolDouble("1.2.3"),
            TimeProvider.System);
        var runtime = host.Open(AgentRuns.Of(_step, _worktree, ground: ground), new RecordedEvents());

        _opened.Add(fetch);
        _opened.Add(runtime);

        return runtime;
    }
}
