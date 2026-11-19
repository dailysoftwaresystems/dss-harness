using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>
/// What carrying a tree to a host costs the machine that carries it. A consumer's first sync of a worktree of 85 MiB to an
/// ssh host left the process that ran it holding 3.2 GiB, flat for minutes after, while the same command on a copy that
/// already existed held 0.07 GiB: each batch of files was built as text inside text - its files' base64, the batch's
/// JSON, the request's JSON around that, the request again with its line feed - and escaping the batch's JSON inside the
/// request had the serializer rent a buffer six times its length, which the shared pool then kept for the life of the
/// process. Measured since on a tree of that shape synced to a WSL distribution: 3.4 GiB private before, flat to the end.
/// </summary>
[Collection(MemoryMeasured.Name)]
public sealed class SyncCarryTests
{
    /// <summary>How much more the heap may hold while batches are carried, or after: half of one batch.</summary>
    private const long HeapBound = 4L * 1024 * 1024;

    /// <summary>How many batches are carried.</summary>
    private const int Batches = 4;

    /// <summary>
    /// Batches carried to a host leave the heap holding no more than it did, while they cross and once they have, and allocate
    /// less than they carry: each file is written into the request as it is encoded, never held here as text, and nothing the
    /// size of a batch is kept by anything once its request has gone.
    /// </summary>
    [Fact]
    public async Task BatchesCarriedToAHost_HoldNothingWhileTheyCrossOrAfter_AndAllocateLessThanTheyCarry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var host = new DrainingHost();
        var transport = new RemoteSyncTransport(
            HostId.Ssh("vps"),
            new HostSession(new HostConnection { Host = HostId.Ssh("vps") }, "dssharness"),
            host,
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));

        // A batch as large as one is let grow, of files of a mebibyte each, as a tree's larger files are.
        var random = new Random(21);
        var batch = Enumerable.Range(0, (int)(SyncServe.LargestBatch / (1024 * 1024)))
            .Select(index =>
            {
                var contents = new byte[1024 * 1024];
                random.NextBytes(contents);
                return new SyncFileContent($"src/f{index}.bin", contents);
            })
            .ToList();

        var carried = (long)Batches * batch.Sum(file => file.Contents.LongLength);

        // Once, so that what is made the first time anything runs is not counted as the carrying's.
        await transport.WriteFilesAsync("/home/dev/repo", batch[..1], cancellationToken);

        var (before, _) = HeapWatch.Collected();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long growth;

        using (var heap = new HeapWatch())
        {
            for (var round = 0; round < Batches; round++)
            {
                await transport.WriteFilesAsync("/home/dev/repo", batch, cancellationToken);
            }

            growth = heap.Growth;
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var held = HeapWatch.Collected().Held - before;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"carrying {carried:N0} bytes grew the heap by at most {growth:N0}, allocated {allocated:N0}, and left {held:N0} more held");

        Assert.Equal(Batches + 1, host.Requests);
        Assert.True(growth <= HeapBound, $"the heap grew by {growth:N0} bytes while batches crossed, past the bound of {HeapBound:N0}");
        Assert.True(held <= HeapBound, $"the heap held {held:N0} bytes more once the batches had crossed, past the bound of {HeapBound:N0}");
        Assert.True(allocated < carried, $"carrying {carried:N0} bytes allocated {allocated:N0}");
    }

    /// <summary>
    /// A file read back from a host - what a pull brings - holds no more than a few of its pieces while it is read and
    /// nothing more once it is, and allocates a few times its size at most: it is read a piece at a time, each arriving a
    /// line at a time and decoded as it comes. Read as one line of text, its answer was held in five copies, and the
    /// serializer rented three times its length to read it, which the shared pool then kept: one 64 MiB file left a
    /// dispatcher holding 2 GiB, and 640 MiB of it after every collection.
    /// </summary>
    [Fact]
    public async Task AFileReadBackFromAHost_HoldsNothingAfter_AndAllocatesAFewTimesItsSize()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var size = 32 * 1024 * 1024;

        // Pieces of an eighth of the file, so that what a few of them hold is told apart from what the file does.
        var transport = SyncKit.AgentHere(harness, pieceBytes: size / 8);

        Directory.CreateDirectory(temp.Combine("copy"));

        var contents = new byte[size];
        new Random(5).NextBytes(contents);
        await File.WriteAllBytesAsync(temp.Combine("copy", "artifact.bin"), contents, cancellationToken);
        contents = null;

        // Once, so that what is made the first time anything runs is not counted as the reading's.
        await File.WriteAllBytesAsync(temp.Combine("copy", "small.bin"), [1, 2, 3], cancellationToken);
        await transport.ReadFileAsync(temp.Combine("copy"), "small.bin", 0, transport.PieceBytes, cancellationToken);

        var (before, _) = HeapWatch.Collected();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var threadsBefore = ThreadPool.ThreadCount;
        long growth;
        string where;
        int length;

        using (var heap = new HeapWatch())
        {
            length = await LengthReadAsync(transport, temp.Combine("copy"), "artifact.bin", cancellationToken);
            growth = heap.Growth;
            where = heap.Where;
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        // Read with the transport's next request gone too: until another runs, the threads that served this one can still
        // hold its last continuation, and with it the file this test has finished with.
        await transport.ReadFileAsync(temp.Combine("copy"), "small.bin", 0, transport.PieceBytes, cancellationToken);

        var (heldAfter, partsAfter) = HeapWatch.Collected();
        var held = heldAfter - before;
        var after = $"{partsAfter}; {ThreadPool.ThreadCount} pool thread(s), against {threadsBefore} before; "
            + $"{harness.StandardOutput.GetStringBuilder().Length:N0} and {harness.StandardError.GetStringBuilder().Length:N0} character(s) "
            + "written to the output and its errors";

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"reading {size:N0} bytes back grew the heap by at most {growth:N0}, allocated {allocated:N0}, and left {held:N0} more held");

        Assert.Equal(size, length);
        // The piece read last is held while the next is read into.
        Assert.True(
            growth <= (3L * transport.PieceBytes) + HeapBound,
            $"the heap grew by {growth:N0} bytes while a file of {size:N0} was read, past three pieces and {HeapBound:N0}: {where}; after: {after}");
        Assert.True(held <= HeapBound, $"the heap held {held:N0} bytes more once the file was read, past the bound of {HeapBound:N0}: {after}");
        Assert.True(allocated <= 8L * size, $"reading {size:N0} bytes allocated {allocated:N0}");
    }

    /// <summary>
    /// How long the file <paramref name="relativePath"/> read back from <paramref name="root"/> a piece at a time is, read in
    /// a method of its own so that nothing of this test's holds its bytes once it returns.
    /// </summary>
    private static async Task<int> LengthReadAsync(RemoteSyncTransport transport, string root, string relativePath, CancellationToken cancellationToken)
    {
        var read = 0L;
        long length;

        do
        {
            var piece = await transport.ReadFileAsync(root, relativePath, read, transport.PieceBytes, cancellationToken);

            length = piece.Length;
            read += piece.Bytes.Length;
        }
        while (read < length);

        return (int)read;
    }

    /// <summary>
    /// A file past what one request could hold whole - 804,519,909 bytes, the most whose base64 a single string holds - is
    /// synced to a host and pulled back, both through this build's own agent as every host is reached, and lands byte for
    /// byte the file it was: a piece to a request, each checked, put in place only once all of it has arrived. Neither way
    /// does this machine's heap hold more than a few of its pieces, however large it is. Held whole inside one request, it
    /// was refused by name before anything was sent, and a build output that size is ordinary.
    /// </summary>
    [Fact]
    public async Task AFilePastAStringsHold_IsCarriedInPieces()
    {
        // One byte past the largest file one request held whole.
        const long PastAStringsHold = 804_519_910;
        const int Piece = 32 * 1024 * 1024;

        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();

        // The file here, its copy there and the one pulled back, with the pieces of each kept aside as it lands.
        Assert.SkipWhen(
            harness.FileSystem.SpaceAt(temp.Path).FreeBytes < 4 * PastAStringsHold,
            $"Carrying a file of {PastAStringsHold:N0} bytes there and back writes up to four times that, more than '{temp.Path}' has free.");

        var here = temp.Combine("here");
        var host = temp.Combine("host");
        var back = temp.Combine("back");
        var warm = temp.Combine("warm");
        var transport = SyncKit.AgentHere(harness, pieceBytes: Piece);
        var service = SyncKit.Service(harness);

        Directory.CreateDirectory(here);
        Directory.CreateDirectory(host);
        Directory.CreateDirectory(warm);
        Generated(Path.Combine(here, "big.bin"), PastAStringsHold, Piece);

        // Once, so that what is made the first time anything runs is not counted as the carrying's - landing elsewhere, so the
        // file pulled back lands where nothing is yet.
        await File.WriteAllBytesAsync(Path.Combine(warm, "small.bin"), [1, 2, 3], cancellationToken);
        await service.PullAsync(transport, warm, temp.Combine("warmed"), ["small.bin"], [], cancellationToken);

        var read = await service.ReadDirectoryAsync(here, cancellationToken);
        var expected = read.Entries["big.bin"].Identity;
        long growth;
        string where;

        using (var heap = new HeapWatch())
        {
            await service.SyncDirectoryAsync(read, transport, host, cancellationToken);

            // Gone here before it comes back, so this machine's disk holds it no more than twice at once.
            File.Delete(Path.Combine(here, "big.bin"));

            var pulled = await service.PullAsync(transport, host, back, ["big.bin"], [], cancellationToken);

            growth = heap.Growth;
            where = heap.Where;

            Assert.Equal(["big.bin"], pulled.Files);
            Assert.Null(pulled.Stopped);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"carrying {PastAStringsHold:N0} bytes there and back grew the heap by at most {growth:N0}");

        Assert.Equal(expected, await FileContentHash.OfAsync(harness.FileSystem, Path.Combine(host, "big.bin"), cancellationToken));
        Assert.Equal(expected, await FileContentHash.OfAsync(harness.FileSystem, Path.Combine(back, "big.bin"), cancellationToken));
        Assert.Equal(PastAStringsHold, expected.Length);
        Assert.False(Directory.Exists(Path.Combine(host, LocalSyncTransport.PiecesDirectory)), "pieces were kept aside on the host once the file was in place");
        Assert.False(Directory.Exists(Path.Combine(back, HarnessLayout.DirectoryName)), "the pull left a harness directory where it landed");

        // The first piece of the file is held while the rest cross, one more as it is sent and the next as it is read.
        Assert.True(
            growth <= (4L * Piece) + HeapBound,
            $"the heap grew by {growth:N0} bytes while a file of {PastAStringsHold:N0} crossed in pieces of {Piece:N0}, past four pieces and {HeapBound:N0}: {where}");
    }

    /// <summary>
    /// Makes a file of <paramref name="length"/> bytes at <paramref name="path"/>, holding nothing but where a block of its
    /// own stands - at its start, either side of the first piece's end, in the middle, and at its end - so every piece is
    /// told apart by what it holds, and a piece put in the wrong place makes another file.
    /// </summary>
    private static void Generated(string path, long length, int piece)
    {
        var random = new Random(112);
        var block = new byte[4096];

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        file.SetLength(length);

        foreach (var at in new[] { 0L, piece - 2048L, length / 2, length - block.Length })
        {
            random.NextBytes(block);
            file.Position = at;
            file.Write(block);
        }
    }

    /// <summary>
    /// A host that reads each request and answers that it finished, keeping nothing of it: what the machine carrying holds
    /// is then its own.
    /// </summary>
    private sealed class DrainingHost : IHostCommandRunner
    {
        private int _requests;

        /// <summary>How many requests it was sent.</summary>
        public int Requests => _requests;

        public Task<ProcessResult> RunAsync(HostConnection connection, HostCommand command, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requests);

            // The nonce is the request's last member, so its end is all that need be kept to answer it.
            var end = new EndOfInput();
            command.StandardInput.WriteTo(end);

            var input = end.Text;
            var at = input.LastIndexOf("\"nonce\":\"", StringComparison.Ordinal) + "\"nonce\":\"".Length;
            var nonce = input[at..input.IndexOf('"', at)];

            command.OnErrorLine?.Invoke(HostAgentProtocol.CompletionLine(nonce, 0));

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        public Task<ProcessResult> ProbeShellAsync(HostConnection connection, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> ReadSshSettingsAsync(HostConnection connection, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> ProbeDefaultWslDistributionAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> ListRunningWslDistributionsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>An input read as a host reads it, of which only the last few hundred bytes are kept.</summary>
    private sealed class EndOfInput : Stream
    {
        private readonly byte[] _end = new byte[256];
        private long _written;

        /// <summary>What the input ended with.</summary>
        public string Text
        {
            get
            {
                if (_written <= _end.Length)
                {
                    return Encoding.UTF8.GetString(_end, 0, (int)_written);
                }

                var start = (int)(_written % _end.Length);

                return Encoding.UTF8.GetString([.. _end[start..], .. _end[..start]]);
            }
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            foreach (var value in buffer)
            {
                _end[_written % _end.Length] = value;
                _written++;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
