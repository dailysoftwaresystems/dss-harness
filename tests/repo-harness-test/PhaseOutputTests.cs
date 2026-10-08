using System.Text;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// A phase's output is its child's lines read back from the phase's log - the bytes between the header the harness wrote
/// and the exit line - a line at a time, never held.
/// </summary>
public sealed class PhaseOutputTests
{
    /// <summary>
    /// Only the child's own lines are read: not the header, which echoes the command line and would witness a pattern the
    /// command contains, nor the exit line; and each reads as the child printed it, however it was ended.
    /// </summary>
    [Fact]
    public async Task APhasesOutput_IsItsChildsLines_ReadBackFromItsLog()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var log = temp.Combine("build.log");

        var result = await new PhaseRunner(factory.ProcessRunner, factory.FileSystem, factory.Output).RunAsync(
            Child("echo-crlf", log, "first", "João, 日本語", "", "last"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["first", "João, 日本語", "", "last"], result.Output.Lines());
        Assert.Equal(["first", "João, 日本語", "", "last"], result.Output.Lines());

        var written = await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken);

        Assert.StartsWith("# command ", written[1], StringComparison.Ordinal);
        Assert.StartsWith("# exit 0 after ", written[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A range is read as the bytes it spans, whatever falls where one read of the file ends and the next begins - a
    /// character written in several bytes among them - and a line ends only as the log ends lines.
    /// </summary>
    [Fact]
    public void ARangeOfALog_IsReadAsTheBytesItSpans()
    {
        using var temp = new TempDirectory();
        var log = temp.Combine("test.log");
        var ending = PhaseRunner.LogLineEnding;

        // Each line a two-byte character short of a read, so the characters fall across where reads end.
        var lines = Enumerable.Range(0, 40).Select(index => $"{index:D3} " + new string('é', 20_000 + index)).ToList();
        var header = "# leg l, phase p" + ending;
        var child = string.Concat(lines.Select(line => line + ending));

        File.WriteAllText(log, header + child + "# exit 0 after 00:00:01" + ending, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var start = Encoding.UTF8.GetByteCount(header);
        var end = start + Encoding.UTF8.GetByteCount(child);

        Assert.Equal(lines, PhaseOutput.InLog(log, start, end).Lines());
        Assert.Equal(lines.Take(2), PhaseOutput.InLog(log, start, start + Encoding.UTF8.GetByteCount(lines[0] + ending + lines[1] + ending)).Lines());
        Assert.Empty(PhaseOutput.InLog(log, start, start).Lines());
    }

    /// <summary>
    /// A log that no longer holds what its phase left in it - gone, no file any more, cut short, or written again
    /// since - is said as unread, naming it and why, before any line of it is handed on: read as what is there, a build
    /// ninja failed read as one stopped from outside, and a step's lines as another's. A reader that stops at its first
    /// line is told too.
    /// </summary>
    [Theory]
    [InlineData("gone", "could not be read back from its log")]
    [InlineData("a directory", "could not be read back from its log")]
    [InlineData("cut short", "byte(s), fewer than the")]
    [InlineData("written again", "it no longer holds what the phase wrote after its last line: it was written again since")]
    public async Task ALogThatNoLongerHoldsWhatItsPhaseLeft_IsSaidAsUnread_BeforeAnyLineIsHandedOn(string how, string why)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var log = temp.Combine("build.log");

        var result = await new PhaseRunner(factory.ProcessRunner, factory.FileSystem, factory.Output).RunAsync(
            Child("echo-crlf", log, "first", "second", "last"),
            TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second", "last"], result.Output.Lines());

        var written = await File.ReadAllBytesAsync(log, TestContext.Current.CancellationToken);

        switch (how)
        {
            case "gone":
                File.Delete(log);
                break;

            case "a directory":
                // Opened as the file it was, it is refused as one this user may not read: no kind of what a file gone raises.
                File.Delete(log);
                Directory.CreateDirectory(log);
                break;

            case "cut short":
                await File.WriteAllBytesAsync(log, written[..^8], TestContext.Current.CancellationToken);
                break;

            default:
                // As long as it was, and every byte another step's.
                await File.WriteAllBytesAsync(log, [.. written.Select(_ => (byte)'x')], TestContext.Current.CancellationToken);
                break;
        }

        var whole = Assert.Throws<PhaseOutputUnreadException>(() => result.Output.Lines().ToList());
        var first = Assert.Throws<PhaseOutputUnreadException>(() => result.Output.Lines().First());

        Assert.Equal(log, whole.LogFile);
        Assert.StartsWith($"what the phase printed could not be read back from its log '{log}': ", whole.Message, StringComparison.Ordinal);
        Assert.Contains(why, whole.Message, StringComparison.Ordinal);
        Assert.Equal(whole.Message, first.Message);
    }

    /// <summary>A range past the end of its log is said as unread too, where nothing says what closed it.</summary>
    [Fact]
    public void ALogShorterThanItsRange_IsSaidAsUnread()
    {
        using var temp = new TempDirectory();
        var log = temp.WriteFile("short.log", "one\ntwo\n");

        var unread = Assert.Throws<PhaseOutputUnreadException>(() => PhaseOutput.InLog(log, 0, 1_000).Lines().ToList());

        Assert.Contains("it holds 8 byte(s), fewer than the 1000 the phase left in it", unread.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A log cut short while it is read - whole as it was opened - is said as unread at the read that finds it so: never
    /// read as what was left, nor read on for ever waiting for bytes that will not come.
    /// </summary>
    [Fact]
    public void ALogCutShortWhileItIsRead_IsSaidAsUnread()
    {
        using var temp = new TempDirectory();
        var log = temp.WriteFile("long.log", string.Concat(Enumerable.Range(0, 40_000).Select(index => $"line {index:D6}\n")));

        using var lines = PhaseOutput.InLog(log, 0, new FileInfo(log).Length).Lines().GetEnumerator();

        Assert.True(lines.MoveNext());
        Assert.Equal("line 000000", lines.Current);

        using (var cut = new FileStream(log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            cut.SetLength(100_000);
        }

        var unread = Assert.Throws<PhaseOutputUnreadException>(() =>
        {
            while (lines.MoveNext())
            {
            }
        });

        Assert.Contains("it was cut short while it was read", unread.Message, StringComparison.Ordinal);
    }

    /// <summary>A reader that stops early reads no further and leaves the log free: nothing still holds it open.</summary>
    [Fact]
    public void AReaderThatStopsEarly_LeavesTheLogFree()
    {
        using var temp = new TempDirectory();
        var log = temp.WriteFile("free.log", string.Concat(Enumerable.Range(0, 1000).Select(index => $"line {index}\n")));

        Assert.Equal("line 0", PhaseOutput.InLog(log, 0, new FileInfo(log).Length).Lines().First());

        using var exclusive = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// Output made up rather than run reads as a child's stream is read - each line however it ended, one too long in
    /// pieces - and several phases' outputs read one after another.
    /// </summary>
    [Fact]
    public void OutputMadeUp_ReadsAsAChildsStreamIs_AndOutputsJoinInOrder()
    {
        var long1 = new string('x', ProcessRunner.LongestLine + 3);

        Assert.Equal(["a", "b\rc", new string('x', ProcessRunner.LongestLine), "xxx"], PhaseOutput.Of("a\r\nb\rc\n" + long1).Lines());
        Assert.Empty(PhaseOutput.Empty.Lines());
        Assert.Equal(
            ["configure", "build 1", "build 2"],
            PhaseOutput.Joined([PhaseOutput.Of("configure\n"), PhaseOutput.Empty, PhaseOutput.Of("build 1\nbuild 2")]).Lines());
    }

    /// <summary>A phase that runs this assembly as a child, exactly as the process tests do.</summary>
    private static PhaseRequest Child(string mode, string logFile, params string[] arguments)
    {
        var request = TestHost.ChildRequest(mode, arguments);

        return new PhaseRequest
        {
            Leg = "native",
            Phase = "build",
            FileName = request.FileName,
            Arguments = request.Arguments,
            Environment = request.Environment,
            LogFile = logFile,
        };
    }

    /// <summary>Output whose log no longer holds it: every read of it is said as unread.</summary>
    internal sealed class Unread : PhaseOutput
    {
        /// <summary>What every read of it says.</summary>
        public const string Said = "what the phase printed could not be read back from its log 'build.log': it was written again since";

        public override IEnumerable<string> Lines() => throw new PhaseOutputUnreadException("build.log", "it was written again since");
    }

    /// <summary>Output that reads as <paramref name="lines"/>, and is then found cut short in its log: unread from there on.</summary>
    internal sealed class UnreadAfter(params string[] lines) : PhaseOutput
    {
        public override IEnumerable<string> Lines()
        {
            foreach (var line in lines)
            {
                yield return line;
            }

            throw new PhaseOutputUnreadException("build.log", "it was cut short while it was read");
        }
    }

    /// <summary>
    /// Output that reads as <paramref name="lines"/> up to the first <paramref name="decides"/> says settles what it is read
    /// for, and fails the test if anything reads past it: for a reader that must stop there, since a phase's output can be
    /// larger than any text the harness could hold.
    /// </summary>
    internal sealed class ReadUpTo(Func<string, bool> decides, params string[] lines) : PhaseOutput
    {
        public override IEnumerable<string> Lines()
        {
            foreach (var line in lines)
            {
                yield return line;

                if (decides(line))
                {
                    break;
                }
            }

            Assert.Fail("the output was read past the line that decides");
        }
    }
}
