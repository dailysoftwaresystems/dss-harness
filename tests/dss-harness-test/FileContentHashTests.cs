using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>What a file or a stream holds is hashed by one implementation, read a block at a time.</summary>
public sealed class FileContentHashTests
{
    /// <summary>SHA-256 of no bytes.</summary>
    private const string OfNothing = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>SHA-256 of the three bytes <c>abc</c>.</summary>
    private const string OfAbc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    /// <summary>
    /// A stream is hashed from where it stands to its end, as its bytes in hand hash: a stream that gives a little at a
    /// time and cannot seek is read to its end over three megabytes, a stream partly read is hashed from where it was
    /// left, and each is left at its end and open, since its caller owns it.
    /// </summary>
    [Fact]
    public async Task AStream_HashesAsItsBytesDo()
    {
        var bytes = new byte[(3 << 20) + 17];
        new Random(20261119).NextBytes(bytes);

        using var trickle = new Trickle(bytes, start: 100, most: 1000);
        using var partlyRead = new MemoryStream(Encoding.ASCII.GetBytes("xyzabc")) { Position = 3 };
        using var empty = new MemoryStream();

        Assert.Equal(FileContentHash.Of(bytes.AsSpan(100)), await FileContentHash.OfStreamAsync(trickle, TestContext.Current.CancellationToken));
        Assert.Equal(OfAbc, await FileContentHash.OfStreamAsync(partlyRead, TestContext.Current.CancellationToken));
        Assert.Equal(OfNothing, await FileContentHash.OfStreamAsync(empty, TestContext.Current.CancellationToken));
        Assert.Equal(0, trickle.Left);
        Assert.Equal((6L, true), (partlyRead.Position, partlyRead.CanRead));
    }

    /// <summary>A file is hashed as the stream of its bytes is, with its length beside it.</summary>
    [Fact]
    public async Task AFile_HashesAsItsStreamDoes()
    {
        using var directory = new TempDirectory();
        var file = directory.WriteFile("abc.txt", "abc");

        Assert.Equal(
            new FileContent(3, OfAbc),
            await FileContentHash.OfAsync(new PhysicalFileSystem(FilePermissionsFactory.Create()), file, TestContext.Current.CancellationToken));
    }

    /// <summary>A stream that cannot seek and gives at most a few bytes a read, from a start within its bytes.</summary>
    private sealed class Trickle(byte[] bytes, int start, int most) : Stream
    {
        private int _at = start;

        /// <summary>How many bytes are still to be read.</summary>
        public int Left => bytes.Length - _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var given = Math.Min(Math.Min(buffer.Length, most), Left);
            bytes.AsSpan(_at, given).CopyTo(buffer);
            _at += given;
            return given;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
