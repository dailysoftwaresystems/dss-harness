using System.Text.Json;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// A state file is written straight to the disk and read straight from it, never held as one text: a codebase graph's
/// link result runs to tens of megabytes, and its text held whole costs twice its size again, in buffers the runtime's
/// shared pool keeps.
/// </summary>
public sealed class JsonStateFileTests
{
    /// <summary>A record is written as its text would be, ending with a newline - indented, or on one line - and reads back.</summary>
    [Fact]
    public void ARecord_IsWrittenAsItsTextWouldBe_EndingWithANewline_AndReadsBack()
    {
        using var temp = new TempDirectory();
        var fileSystem = new HarnessFactory().FileSystem;
        var record = new Named(["C:\\tree\\a\u00E7\u00E3o", "<b>"], 3);
        var indented = temp.Combine("nested", "indented.json");
        var line = temp.Combine("line.json");

        JsonStateFile.Write(fileSystem, indented, record);
        JsonStateFile.Write(fileSystem, line, record, JsonStateFile.LineOptions);

        Assert.Equal(JsonSerializer.Serialize(record, JsonStateFile.Options) + "\n", File.ReadAllText(indented));
        Assert.Equal(JsonSerializer.Serialize(record, JsonStateFile.LineOptions) + "\n", File.ReadAllText(line));
        Assert.Equivalent(record, JsonStateFile.Read<Named>(fileSystem, indented), strict: true);
        Assert.Equivalent(record, JsonStateFile.Read<Named>(fileSystem, line), strict: true);
    }

    /// <summary>A file that does not read as the record is refused as JSON that does not, and one that holds null reads as null.</summary>
    [Fact]
    public void AFileThatIsNotTheRecord_IsRefused_AndNullReadsAsNull()
    {
        using var temp = new TempDirectory();
        var fileSystem = new HarnessFactory().FileSystem;

        Assert.Throws<JsonException>(() => JsonStateFile.Read<Named>(fileSystem, temp.WriteFile("empty.json", "")));
        Assert.Throws<JsonException>(() => JsonStateFile.Read<Named>(fileSystem, temp.WriteFile("other.json", "{\"names\":[],\"count\":1,\"more\":2}\n")));
        Assert.Null(JsonStateFile.Read<Named>(fileSystem, temp.WriteFile("null.json", "null\n")));
    }

    /// <summary>
    /// A large record is serialized onto the disk and parsed off it a little at a time: neither costs the text it is held
    /// as on the disk - twice its size as a string - nor a buffer of all its bytes.
    /// </summary>
    [Fact]
    public void ALargeRecord_IsNeverHeldAsOneText_WrittenOrRead()
    {
        using var temp = new TempDirectory();
        var fileSystem = new HarnessFactory().FileSystem;
        var path = temp.Combine("large.json");
        var record = new Flags([.. Enumerable.Repeat(false, 1 << 20)]);
        JsonStateFile.Write(fileSystem, temp.Combine("warm.json"), new Flags([true]));
        _ = JsonStateFile.Read<Flags>(fileSystem, temp.Combine("warm.json"));

        var before = GC.GetAllocatedBytesForCurrentThread();
        JsonStateFile.Write(fileSystem, path, record);
        var written = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        var read = JsonStateFile.Read<Flags>(fileSystem, path);
        var parsed = GC.GetAllocatedBytesForCurrentThread() - before;

        var length = new FileInfo(path).Length;
        Assert.Equal(record.Values.Count, read!.Values.Count);
        Assert.True(written < length / 4, $"writing a record of {length:N0} bytes allocated {written:N0}");
        Assert.True(parsed < length / 2, $"reading a record of {length:N0} bytes allocated {parsed:N0}");
    }

    /// <summary>
    /// A file of lines is read a line at a time: the first is in hand before the rest of the file is read, so a disk that
    /// fails past the first lines fails the reading of a later one, never of the first.
    /// </summary>
    [Fact]
    public void AFileOfLines_IsReadALineAtATime()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("lines.jsonl");
        var harness = new HarnessFactory();
        JsonStateFile.WriteLines(harness.FileSystem, path, lines =>
        {
            foreach (var at in Enumerable.Range(0, 20_000))
            {
                lines.Write(new Named([$"line {at}"], at));
            }
        });
        var read = new List<Named>();

        var failed = Assert.Throws<IOException>(() => JsonStateFile.ReadLines<object>(new FailsPast(harness.FileSystem, 64 << 10), path, lines =>
        {
            while (true)
            {
                read.Add(lines.Next<Named>("a line"));
            }
        }));

        Assert.Equal(FailsPast.Why, failed.Message);
        Assert.InRange(read.Count, 1, 19_999);
        Assert.Equal(["line 0"], read[0].Names);
    }

    private sealed record Named(IReadOnlyList<string> Names, int Count);

    /// <summary>The real file system, whose reads fail once <paramref name="bytes"/> of a file are read, as a disk that went away does.</summary>
    private sealed class FailsPast(IFileSystem inner, long bytes) : PassThroughFileSystem(inner)
    {
        /// <summary>What the disk says.</summary>
        public const string Why = "the disk went away.";

        public override Stream OpenRead(string path) => new Failing(base.OpenRead(path), bytes);

        /// <summary>A stream that fails once it has given <paramref name="bytes"/>.</summary>
        private sealed class Failing(Stream inner, long bytes) : Stream
        {
            private long _given;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (_given >= bytes)
                {
                    throw new IOException(Why);
                }

                var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, bytes - _given)]);
                _given += read;
                return read;
            }

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
                }

                base.Dispose(disposing);
            }
        }
    }

    private sealed record Flags(IReadOnlyList<bool> Values);
}
