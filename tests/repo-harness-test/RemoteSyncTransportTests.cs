using System.Text;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// A sync's requests as a host's agent serves them: this build's own DssHarness, started on this machine as a host starts
/// it, so each request crosses as it does to any host - one line of JSON on its standard input, its command parsed there.
/// </summary>
public sealed class RemoteSyncTransportTests
{
    /// <summary>
    /// A file named as a file of arguments is - '@notes' - is written, read and deleted on the host as itself. Read there as
    /// a file of arguments, its deletion read 'notes' beside the copy and deleted the path that named instead; and a file
    /// named as an option is, '-v', was taken for one, and its operation for one given too few arguments.
    /// </summary>
    [Fact]
    public async Task FilesNamedAsArgumentsOrOptions_CrossAndAreDeletedOnTheHostAsThemselves_AndNothingElseIs()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = temp.Combine("copy");
        var transport = SyncKit.AgentHere(new HarnessFactory());

        // What a file of arguments named '@notes' would hold, beside the copy, where the agent serves from.
        await File.WriteAllTextAsync(temp.Combine("notes"), "victim.txt\n", cancellationToken);

        await transport.CreateRootAsync(root, cancellationToken: cancellationToken);
        await transport.WriteFilesAsync(
            root,
            [
                new SyncFileContent("victim.txt", Encoding.UTF8.GetBytes("keep me\n")),
                new SyncFileContent("@notes", Encoding.UTF8.GetBytes("at\n")),
                new SyncFileContent("-v", Encoding.UTF8.GetBytes("dash\n")),
            ],
            cancellationToken);
        await transport.WriteFileAsync(root, "@single", Encoding.UTF8.GetBytes("one\n"), cancellationToken);

        Assert.Equal("at\n", Encoding.UTF8.GetString(await transport.ReadFileAsync(root, "@notes", cancellationToken)));
        Assert.Equal("dash\n", Encoding.UTF8.GetString(await transport.ReadFileAsync(root, "-v", cancellationToken)));
        Assert.Equal("one\n", await File.ReadAllTextAsync(Path.Combine(root, "@single"), cancellationToken));

        await transport.DeleteFileAsync(root, "@notes", cancellationToken);
        await transport.DeleteFileAsync(root, "-v", cancellationToken);

        Assert.False(File.Exists(Path.Combine(root, "@notes")));
        Assert.False(File.Exists(Path.Combine(root, "-v")));
        Assert.Equal("keep me\n", await File.ReadAllTextAsync(Path.Combine(root, "victim.txt"), cancellationToken));
    }

    /// <summary>
    /// A file read back arrives as the bytes its answer says it holds, a piece to a line after the answer, and is refused
    /// where the pieces say otherwise: fewer bytes than that is a file cut short on the way; a piece that is not base64,
    /// or that runs past that length, one this build cannot read; a length no file it can send holds is refused before
    /// anything is made for it; and bytes that arrived whole but not as the far side hashed them, a file that changed.
    /// </summary>
    [Theory]
    [InlineData("short", "'a.bin' did not arrive whole from ssh vps: 3 of its 10 bytes did.")]
    [InlineData("long", "'a.bin' arrived in a shape this build cannot read: a piece of it is not base64, or carries more than the file was said to hold.")]
    [InlineData("not base64", "'a.bin' arrived in a shape this build cannot read: a piece of it is not base64, or carries more than the file was said to hold.")]
    [InlineData("past the largest", "ssh vps said 'a.bin' holds 804519910 bytes, which no file it can send does.")]
    [InlineData("changed", "'a.bin' changed between ssh vps and here")]
    public async Task AFileReadBack_IsRefused_WhereWhatArrivedIsNotWhatItsAnswerSaid(string arrival, string expected)
    {
        var three = "abc"u8.ToArray();

        (long Length, string Hash, string[] Pieces) answered = arrival switch
        {
            "short" => (10, FileContentHash.Of(three), [.. SyncServe.ContentLines(three)]),
            "long" => (3, FileContentHash.Of(three), [.. SyncServe.ContentLines("abcdef"u8.ToArray())]),
            "not base64" => (3, FileContentHash.Of(three), [SyncServe.ContentPrefix + "@@@@"]),
            "past the largest" => (SyncServe.LargestFile + 1, FileContentHash.Of(three), []),
            _ => (3, FileContentHash.Of("abd"u8.ToArray()), [.. SyncServe.ContentLines(three)]),
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Answering(answered.Length, answered.Hash, answered.Pieces)
            .ReadFileAsync("/home/dev/repo", "a.bin", TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.StartsWith(expected, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An empty file is read back as the empty file it is: its answer says so, and no piece follows it.</summary>
    [Fact]
    public async Task AnEmptyFileReadBack_IsEmpty()
    {
        var read = await Answering(0, FileContentHash.Of([]), [])
            .ReadFileAsync("/home/dev/repo", "empty.txt", TestContext.Current.CancellationToken);

        Assert.Empty(read);
    }

    /// <summary>A host whose agent answers a read with <paramref name="length"/> and <paramref name="hash"/>, then <paramref name="pieces"/>.</summary>
    private static RemoteSyncTransport Answering(long length, string hash, string[] pieces) => new(
        HostId.Ssh("vps"),
        new HostSession(new HostConnection { Host = HostId.Ssh("vps") }, "dssharness"),
        new ScriptedHostCommands((_, command) =>
        {
            command.OnOutputLine?.Invoke(SyncServe.Answer(new SyncFileAnswer(length, hash)));

            foreach (var piece in pieces)
            {
                command.OnOutputLine?.Invoke(piece);
            }

            return HostResults.Finished(command, HarnessExit.Success);
        }),
        new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));
}
