using System.Text;
using DssHarness.Core.Codebase;
using Graph = DssHarness.Tests.CodebaseGraphTree;

namespace DssHarness.Tests;

/// <summary>What a sync of the codebase graph holds of a file it reads, however large: the file's text and what is made of it.</summary>
/// <remarks>Measured alone (<see cref="MemoryMeasured"/>): the heap one of them is held to is the process's.</remarks>
[Collection(MemoryMeasured.Name)]
public sealed class CodebaseGraphMemoryTests
{
    /// <summary>The bytes of the lock the test writes, about.</summary>
    private const int LockBytes = 40 * 1024 * 1024;

    /// <summary>
    /// A forty-megabyte package lock is read whole and extracted, under the retired maxFileBytes too: decoded streamed, so
    /// the sync never holds the file's bytes beside its text, and what it holds at its most is the text, its lines and its
    /// nodes - a few times the file's bytes, never a copy of the file for each way it is read. Red with maxFileBytes, which
    /// left the file unread.
    /// </summary>
    /// <remarks>
    /// Bounded by what its reader keeps and not by the nodes alone: an implementation reads its file's text by
    /// position (<see cref="CodeText"/>), so no string of the file is held while it reads - what holds a file's
    /// nodes and no more is what its reader keeps of it.
    /// </remarks>
    [Fact]
    public async Task AFortyMegabytePackageLock_IsExtracted()
    {
        using var graph = await Graph.CreateAsync(shape: CodebaseGraphShape.Default);
        var line = "  \"node_modules/" + new string('a', 1000) + "\": { \"version\": \"1.0.0\" },\n";
        var text = new StringBuilder("class Lock\n", LockBytes + line.Length + 16);

        while (text.Length < LockBytes)
        {
            text.Append(line);
        }

        graph.Write("package-lock.toy", text.ToString());
        text.Clear();
        text.Capacity = 0;

        // The lock is the tree's one file: the README the fixture seeds would be outlined with it, and this measures
        // the lock alone.
        File.Delete(Path.Combine(graph.Tree, "README.md"));

        await graph.CommitAsync();
        var bytes = new FileInfo(Path.Combine(graph.Tree, "package-lock.toy")).Length;
        CodebaseGraphSynced synced;

        using (var heap = new HeapWatch())
        {
            synced = await graph.SyncAsync();

            // The text as .NET holds it - two bytes a character - its lines as many again, and the extract's nodes: no more
            // than six times the file's bytes; a copy of the bytes, or of the text, for each way the file is read passes it.
            Assert.True(heap.Growth <= 6 * bytes, $"the heap grew by {heap.Growth:N0} bytes as a file of {bytes:N0} was read, past six times its bytes: {heap.Where}");
        }

        Assert.True(bytes >= LockBytes, $"the lock is {bytes:N0} bytes");
        Assert.Equal(1, synced.Extracted);
        Assert.Equal(CodeFileStates.Parsed, graph.Manifest().Files["package-lock.toy"].State);
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:Lock");
    }
}
