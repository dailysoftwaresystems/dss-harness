using System.Globalization;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// What a log's reading holds while it reads, measured: nothing that grows with how much a program prints, so a test
/// that logs half a million lines is read within the heap one that logs a line is.
/// </summary>
[Collection(MemoryMeasured.Name)]
public sealed class ResultReadingMemoryTests
{
    /// <summary>
    /// How much more this process's heap may hold while the log is read: many times what a reading holds of one test, and
    /// a small part of what the lines the test logged come to held as text, near a hundred megabytes.
    /// </summary>
    private const long HeapBound = 16L * 1024 * 1024;

    /// <summary>How many lines the test logs.</summary>
    private const int LoggedLines = 500_000;

    /// <summary>The first line the test logs, as its failure says it.</summary>
    private const string FirstLogged = "ingest_test.go:21: record 000000000 of shard 00 read with every field it holds checked against the store";

    /// <summary>
    /// A Go test that logs half a million lines - streamed under its <c>=== RUN</c> line by <c>go test -v</c>, or printed
    /// under its <c>--- FAIL</c> line without it - is read within a bounded heap: of its lines only where they begin and
    /// the first that names its place are held, and its failure is that line, its log range running over every line it
    /// logged, each one read away.
    /// </summary>
    [Fact]
    public void AGoTestThatLogsHalfAMillionLines_IsReadWithinABoundedHeap_ItsFailureItsFirstPlacedLine()
    {
        using var temp = new TempDirectory();

        var verbose = Measured(temp.Path, Verbose(), "while a verbose run streamed the test's lines");
        var plain = Measured(temp.Path, Plain(), "while a plain run printed the test's lines under its result line");

        Assert.Equal(
            ("TestIngest", (string?)FirstLogged, (string?)"ingest_test.go", (int?)21, (2, LoggedLines + 2)),
            (verbose.Id, verbose.Message, verbose.File, verbose.Line, verbose.Log!.Value));
        Assert.Equal(
            ("TestIngest", (string?)FirstLogged, (string?)"ingest_test.go", (int?)21, (1, LoggedLines + 1)),
            (plain.Id, plain.Message, plain.File, plain.Line, plain.Log!.Value));
    }

    /// <summary>
    /// The one failure <paramref name="lines"/> digest to, read by <c>go test</c>'s parsers as they stream against an
    /// empty tree at <paramref name="root"/>, with the heap grown by no more than <see cref="HeapBound"/> while they were
    /// read.
    /// </summary>
    private static TestFailure Measured(string root, IEnumerable<string> lines, string when)
    {
        ResultDigest? digest;

        using (var heap = new HeapWatch())
        {
            digest = PhaseDigest.Of(
                new Streamed(lines),
                1,
                PhaseParsers.ForTests(new HarnessConfig(), "go", null),
                new DigestTree(root, null, new PhysicalFileSystem(FilePermissionsFactory.Create())));

            TestContext.Current.TestOutputHelper?.WriteLine($"the heap grew by at most {heap.Growth:N0} bytes {when}");

            Assert.True(heap.Growth <= HeapBound, $"the heap grew by {heap.Growth:N0} bytes {when}, past the bound of {HeapBound:N0}; {heap.Where}");
        }

        return Assert.Single(digest!.Failures);
    }

    /// <summary><c>go test -v</c>'s output: the test's lines streamed under its <c>=== RUN</c> line, its result after them.</summary>
    private static IEnumerable<string> Verbose()
    {
        yield return "=== RUN   TestIngest";

        foreach (var line in Logged())
        {
            yield return line;
        }

        yield return "--- FAIL: TestIngest (12.50s)";
        yield return "FAIL";
        yield return "FAIL\texample.com/app/ingest\t12.512s";
    }

    /// <summary><c>go test</c>'s output without <c>-v</c>: the test's result line, and the lines it logged under it.</summary>
    private static IEnumerable<string> Plain()
    {
        yield return "--- FAIL: TestIngest (12.50s)";

        foreach (var line in Logged())
        {
            yield return line;
        }

        yield return "FAIL";
        yield return "FAIL\texample.com/app/ingest\t12.512s";
    }

    /// <summary>
    /// The lines the test logs, as <c>t.Logf</c> prints them indented under the test, each naming its place: every one a
    /// string of its own, made as it is read and never held by the test.
    /// </summary>
    private static IEnumerable<string> Logged()
    {
        for (var record = 0; record < LoggedLines; record++)
        {
            yield return $"    ingest_test.go:21: record {record.ToString("D9", CultureInfo.InvariantCulture)} of shard {(record % 64).ToString("D2", CultureInfo.InvariantCulture)} read with every field it holds checked against the store";
        }
    }

    /// <summary>A phase's output read as its log streams: each line made as it is asked for, none held.</summary>
    private sealed class Streamed(IEnumerable<string> lines) : PhaseOutput
    {
        public override IEnumerable<string> Lines() => lines;
    }
}
