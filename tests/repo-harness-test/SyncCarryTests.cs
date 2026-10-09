using System.Text;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

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
    /// <summary>How much more the heap may hold while batches are carried, or after: a small part of one batch.</summary>
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

        var before = GC.GetTotalMemory(forceFullCollection: true);
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
        var held = GC.GetTotalMemory(forceFullCollection: true) - before;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"carrying {carried:N0} bytes grew the heap by at most {growth:N0}, allocated {allocated:N0}, and left {held:N0} more held");

        Assert.Equal(Batches + 1, host.Requests);
        Assert.True(growth <= HeapBound, $"the heap grew by {growth:N0} bytes while batches crossed, past the bound of {HeapBound:N0}");
        Assert.True(held <= HeapBound, $"the heap held {held:N0} bytes more once the batches had crossed, past the bound of {HeapBound:N0}");
        Assert.True(allocated < carried, $"carrying {carried:N0} bytes allocated {allocated:N0}");
    }

    /// <summary>
    /// A file read back from a host - what a pull brings, and what carrying an artifact reads back to prove it landed -
    /// leaves the heap holding nothing more once it is read, and allocates a few times its size at most: its content
    /// arrives a line at a time and is decoded as it comes. Read as one line of text, its answer was held in five copies,
    /// and the serializer rented three times its length to read it, which the shared pool then kept: one 64 MiB file left
    /// a dispatcher holding 2 GiB, and 640 MiB of it after every collection.
    /// </summary>
    [Fact]
    public async Task AFileReadBackFromAHost_HoldsNothingAfter_AndAllocatesAFewTimesItsSize()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var transport = SyncKit.AgentHere(harness);
        var size = 32 * 1024 * 1024;

        Directory.CreateDirectory(temp.Combine("copy"));

        var contents = new byte[size];
        new Random(5).NextBytes(contents);
        await File.WriteAllBytesAsync(temp.Combine("copy", "artifact.bin"), contents, cancellationToken);
        contents = null;

        // Once, so that what is made the first time anything runs is not counted as the reading's.
        await File.WriteAllBytesAsync(temp.Combine("copy", "small.bin"), [1, 2, 3], cancellationToken);
        await transport.ReadFileAsync(temp.Combine("copy"), "small.bin", cancellationToken);

        var before = GC.GetTotalMemory(forceFullCollection: true);
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
        await transport.ReadFileAsync(temp.Combine("copy"), "small.bin", cancellationToken);

        var held = GC.GetTotalMemory(forceFullCollection: true) - before;
        var after = $"{HeapWatch.Parts()}; {ThreadPool.ThreadCount} pool thread(s), against {threadsBefore} before; "
            + $"{harness.StandardOutput.GetStringBuilder().Length:N0} and {harness.StandardError.GetStringBuilder().Length:N0} character(s) "
            + "written to the output and its errors";

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"reading {size:N0} bytes back grew the heap by at most {growth:N0}, allocated {allocated:N0}, and left {held:N0} more held");

        Assert.Equal(size, length);
        Assert.True(
            growth <= size + HeapBound,
            $"the heap grew by {growth:N0} bytes while a file of {size:N0} was read, past the file and {HeapBound:N0}: {where}; after: {after}");
        Assert.True(held <= HeapBound, $"the heap held {held:N0} bytes more once the file was read, past the bound of {HeapBound:N0}: {after}");
        Assert.True(allocated <= 8L * size, $"reading {size:N0} bytes allocated {allocated:N0}");
    }

    /// <summary>
    /// How long the file <paramref name="relativePath"/> read back from <paramref name="root"/> is, read in a method of its
    /// own so that nothing of this test's holds its bytes once it returns.
    /// </summary>
    private static async Task<int> LengthReadAsync(RemoteSyncTransport transport, string root, string relativePath, CancellationToken cancellationToken)
        => (await transport.ReadFileAsync(root, relativePath, cancellationToken)).Length;

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

        public Task<ProcessResult> ProbeShellAsync(HostConnection connection, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> ReadSshSettingsAsync(HostConnection connection, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> ProbeDefaultWslDistributionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProcessResult> ListRunningWslDistributionsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
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
