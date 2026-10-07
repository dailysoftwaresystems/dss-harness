using System.Globalization;
using System.Text;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// A child that prints more than any string can hold. A consumer's test started an interactive interpreter with no console,
/// which printed the same traceback for four minutes; ctest printed all two gigabytes of it, and the harness, which kept every
/// character of a phase's output in memory - in four copies - died with an OutOfMemoryException and 34 GB of the machine
/// free: no string holds more than about a billion characters. A phase's output belongs in its log, and the runner keeps in
/// memory only what is bounded whatever the child prints.
/// </summary>
[Collection(MemoryMeasured.Name)]
public sealed class PhaseFloodTests
{
    /// <summary>
    /// How far this process's heap may grow while a phase runs, whatever its child prints: room for what reading a line at
    /// a time leaves to be collected, and for nothing that grows with the output.
    /// </summary>
    private const long HeapBound = 256L * 1024 * 1024;

    /// <summary>How long each line of a flood is.</summary>
    private const int LineLength = 1024;

    /// <summary>
    /// More lines than one string can hold, as text: 1,126,400,000 characters, where a string holds at most 1,073,741,791.
    /// </summary>
    private const long FloodLines = 1_100_000;

    /// <summary>
    /// A child printing more than a string can hold, through the real process runner and the real phase runner, leaves
    /// every line of it in the log, byte for byte, between the header and the exit line, and the heap no larger than the
    /// bound while it ran; the phase's output reads back from the log as the very lines printed, within the same bound,
    /// and its last lines are the last the child printed.
    /// </summary>
    /// <remarks>
    /// Measured on the runner that kept the output as text, this ended as the consumer's leg did: an
    /// OutOfMemoryException from StringBuilder.ToString in ProcessRunner.CaptureAsync. Measured since, the heap grew by
    /// under 18 MB while the phase ran.
    /// </remarks>
    [Fact]
    public async Task AChildPrintingMoreThanAStringHolds_IsKeptWhole_InItsLog_WithinABoundedHeap()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var log = temp.Combine("test.log");

        PhaseResult result;

        using (var heap = new HeapWatch())
        {
            result = await new PhaseRunner(factory.ProcessRunner, factory.FileSystem, factory.Output).RunAsync(
                Child("flood", log, FloodLines.ToString(CultureInfo.InvariantCulture), LineLength.ToString(CultureInfo.InvariantCulture)) with
                {
                    SuccessPattern = $"^{FloodLines - 1:D12}",
                },
                TestContext.Current.CancellationToken);

            TestContext.Current.TestOutputHelper?.WriteLine($"the heap grew by at most {heap.Growth:N0} bytes while the phase ran");

            AssertWithin(heap, "while the phase ran");
        }

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Witnessed, "the last line the child printed was never read");
        Assert.Equal(
            [.. Enumerable.Range((int)FloodLines - PhaseResult.TailLines, PhaseResult.TailLines).Select(index => TestChild.FloodLine(index, LineLength))],
            result.LastLines);

        AssertLogHolds(log, FloodLines, LineLength);

        using (var heap = new HeapWatch())
        {
            var read = 0L;

            foreach (var line in result.Output.Lines())
            {
                if (!string.Equals(line, TestChild.FloodLine(read, LineLength), StringComparison.Ordinal))
                {
                    Assert.Fail($"line {read} of the phase's output does not read back as the child printed it");
                }

                read++;
            }

            Assert.Equal(FloodLines, read);
            AssertWithin(heap, "while the output was read back");
        }
    }

    /// <summary>
    /// A child that prints one line with no line feed for longer than any line is held whole - hundreds of millions of
    /// characters - has it kept, every character in order, as lines of at most the longest the runner hands on, and the
    /// heap no larger than the bound: the runner that held a line until it ended held this one whole, several times over.
    /// </summary>
    [Fact]
    public async Task AChildPrintingOneLineThatNeverEnds_HasItKeptWhole_InPieces_WithinABoundedHeap()
    {
        const long Giant = 200L * 1024 * 1024;

        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var log = temp.Combine("build.log");

        PhaseResult result;

        using (var heap = new HeapWatch())
        {
            result = await new PhaseRunner(factory.ProcessRunner, factory.FileSystem, factory.Output).RunAsync(
                Child("flood", log, "2", LineLength.ToString(CultureInfo.InvariantCulture), Giant.ToString(CultureInfo.InvariantCulture)),
                TestContext.Current.CancellationToken);

            TestContext.Current.TestOutputHelper?.WriteLine($"the heap grew by at most {heap.Growth:N0} bytes while the phase ran");

            AssertWithin(heap, "while the phase ran");
        }

        Assert.Equal(0, result.ExitCode);

        using var lines = result.Output.Lines().GetEnumerator();

        Assert.True(lines.MoveNext());
        Assert.Equal(TestChild.FloodLine(0, LineLength), lines.Current);
        Assert.True(lines.MoveNext());
        Assert.Equal(TestChild.FloodLine(1, LineLength), lines.Current);

        var kept = 0L;

        while (lines.MoveNext())
        {
            Assert.InRange(lines.Current.Length, 1, ProcessRunner.LongestLine);

            if (lines.Current.AsSpan().ContainsAnyExcept('g'))
            {
                Assert.Fail($"the piece starting at character {kept} of the line is not what the child printed");
            }

            kept += lines.Current.Length;
        }

        Assert.Equal(Giant, kept);
        Assert.All(result.LastLines, line => Assert.Equal(TestChild.GiantLine(line.Length), line));
    }

    /// <summary>That what <paramref name="heap"/> watched grew the heap by no more than <see cref="HeapBound"/>.</summary>
    private static void AssertWithin(HeapWatch heap, string when)
        => Assert.True(
            heap.Growth <= HeapBound,
            $"the heap grew by {heap.Growth:N0} bytes {when}, past the bound of {HeapBound:N0}");

    /// <summary>
    /// That <paramref name="log"/> holds the harness's header, then exactly the <paramref name="count"/> lines a flood of
    /// lines <paramref name="length"/> long printed, each ended as the log ends a line, then the exit line and nothing more:
    /// read a line at a time, as the log is too large to read whole.
    /// </summary>
    private static void AssertLogHolds(string log, long count, int length)
    {
        using var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1 << 20);

        var ending = Encoding.ASCII.GetBytes(Environment.NewLine);

        for (var header = 0; header < 3; header++)
        {
            Assert.StartsWith("# ", ReadLine(file), StringComparison.Ordinal);
        }

        var expected = new byte[length + ending.Length];
        var actual = new byte[length + ending.Length];

        ending.CopyTo(expected, length);

        for (var index = 0L; index < count; index++)
        {
            Encoding.ASCII.GetBytes(TestChild.FloodLine(index, length), expected);
            file.ReadExactly(actual);

            if (!actual.AsSpan().SequenceEqual(expected))
            {
                Assert.Fail($"line {index} of what the child printed is not in the log as it printed it");
            }
        }

        Assert.StartsWith("# exit 0 after ", ReadLine(file), StringComparison.Ordinal);
        Assert.Equal(file.Length, file.Position);
    }

    /// <summary>The next line of <paramref name="file"/>, without its ending.</summary>
    private static string ReadLine(FileStream file)
    {
        var line = new List<byte>();

        for (var next = file.ReadByte(); next >= 0 && next != '\n'; next = file.ReadByte())
        {
            line.Add((byte)next);
        }

        return Encoding.UTF8.GetString([.. line]).TrimEnd('\r');
    }

    /// <summary>A phase that runs this assembly as a child, exactly as the process tests do.</summary>
    private static PhaseRequest Child(string mode, string logFile, params string[] arguments)
    {
        var request = TestHost.ChildRequest(mode, arguments);

        return new PhaseRequest
        {
            Leg = "win-msvc-release",
            Phase = "test",
            FileName = request.FileName,
            Arguments = request.Arguments,
            Environment = request.Environment,
            LogFile = logFile,
        };
    }
}
