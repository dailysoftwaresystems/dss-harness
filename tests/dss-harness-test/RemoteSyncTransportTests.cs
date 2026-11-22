using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

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

        Assert.Equal("at\n", Encoding.UTF8.GetString(await SyncPieces.ReadWholeAsync(transport, root, "@notes", cancellationToken)));
        Assert.Equal("dash\n", Encoding.UTF8.GetString(await SyncPieces.ReadWholeAsync(transport, root, "-v", cancellationToken)));
        Assert.Equal("one\n", await File.ReadAllTextAsync(Path.Combine(root, "@single"), cancellationToken));

        await transport.DeleteFileAsync(root, "@notes", cancellationToken);
        await transport.DeleteFileAsync(root, "-v", cancellationToken);

        Assert.False(File.Exists(Path.Combine(root, "@notes")));
        Assert.False(File.Exists(Path.Combine(root, "-v")));
        Assert.Equal("keep me\n", await File.ReadAllTextAsync(Path.Combine(root, "victim.txt"), cancellationToken));
    }

    /// <summary>
    /// A piece of a file read back arrives as the bytes its answer says it holds, a line at a time after the answer, and is
    /// refused where the lines say otherwise: fewer bytes than that is a piece cut short on the way; a line that is not
    /// base64, or that runs past that length, one this build cannot read; a length below nothing is refused before
    /// anything is made for it; and a piece that starts the file without saying what all of it holds, one this build
    /// cannot read either - the rest of the file would be held to nothing. However large the file is said to be, a piece
    /// of it is no larger than was asked for: one past what a request once held whole is read as any other.
    /// </summary>
    [Theory]
    [InlineData("short", "'a.bin' did not arrive whole from ssh vps: 3 of the 10 bytes of its piece at byte 0 did.")]
    [InlineData("long", "'a.bin' arrived in a shape this build cannot read: a line of it is not base64, or carries more than its answer said would follow.")]
    [InlineData("not base64", "'a.bin' arrived in a shape this build cannot read: a line of it is not base64, or carries more than its answer said would follow.")]
    [InlineData("below nothing", "ssh vps said 'a.bin' holds -1 bytes, which no file does.")]
    [InlineData("no whole", "ssh vps answered with a piece of 'a.bin' at byte 0 in a shape this build cannot read: the hash of the whole file comes with the piece that starts it, and only with that one.")]
    [InlineData("past a string's hold", "'a.bin' did not arrive whole from ssh vps: 3 of the 1024 bytes of its piece at byte 0 did.")]
    public async Task APieceReadBack_IsRefused_WhereWhatArrivedIsNotWhatItsAnswerSaid(string arrival, string expected)
    {
        var three = "abc"u8.ToArray();
        var hash = FileContentHash.Of(three);

        (SyncFileAnswer Answer, string[] Lines) answered = arrival switch
        {
            "short" => (new SyncFileAnswer(10, hash, hash), [.. SyncServe.ContentLines(three)]),
            "long" => (new SyncFileAnswer(3, hash, hash), [.. SyncServe.ContentLines("abcdef"u8.ToArray())]),
            "not base64" => (new SyncFileAnswer(3, hash, hash), [SyncServe.ContentPrefix + "@@@@"]),
            "below nothing" => (new SyncFileAnswer(-1, hash, hash), []),
            "no whole" => (new SyncFileAnswer(3, null, hash), [.. SyncServe.ContentLines(three)]),
            _ => (new SyncFileAnswer(804_519_910, hash, hash), [.. SyncServe.ContentLines(three)]),
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Answering(answered.Answer, answered.Lines)
            .ReadFileAsync("/home/dev/repo", "a.bin", 0, 1024, TestContext.Current.CancellationToken));

        Assert.Equal((HarnessExit.CommandFailed, expected), (refusal.ExitCode, refusal.Message));
    }

    /// <summary>
    /// A piece read back whose bytes arrived whole but not as the far side hashed them is refused naming the piece - the
    /// file, how many bytes, where it starts, and both hashes - and nothing of it is answered: kept, it would cross on as
    /// part of a file nobody sent, found only once the whole file failed its hash, naming no piece of it.
    /// </summary>
    [Fact]
    public async Task APieceReadBackWhoseHashIsWrong_IsRefusedNamingThePiece()
    {
        var piece = "efgh"u8.ToArray();
        var damaged = FileContentHash.Of("efgX"u8);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Answering(new SyncFileAnswer(12, null, damaged), [.. SyncServe.ContentLines(piece)])
            .ReadFileAsync("/home/dev/repo", "big.bin", 4, 4, TestContext.Current.CancellationToken));

        Assert.Equal(
            (HarnessExit.CommandFailed,
                $"'big.bin' changed between ssh vps and here: its piece of 4 byte(s) at byte 4 was read as {damaged} and arrived as "
                + $"{FileContentHash.Of(piece)}. Nothing of it was kept."),
            (refusal.ExitCode, refusal.Message));
    }

    /// <summary>An empty file is read back as the empty file it is: its answer says so, and no line follows it.</summary>
    [Fact]
    public async Task AnEmptyFileReadBack_IsEmpty()
    {
        var empty = FileContentHash.Of(ReadOnlySpan<byte>.Empty);
        var read = await Answering(new SyncFileAnswer(0, empty, empty), [])
            .ReadFileAsync("/home/dev/repo", "empty.txt", 0, 1024, TestContext.Current.CancellationToken);

        Assert.Equal(new SyncFilePiece(0, empty, []), read with { Bytes = [] });
        Assert.Empty(read.Bytes);
        Assert.True(read.IsWholeFile);
    }

    /// <summary>
    /// A file larger than a piece is written to a host a piece to a request, and put in place there only once all of it has
    /// arrived - nothing at its path until then, nothing kept aside after - and read back a piece at a time as it was
    /// written; in a batch, the files before it and after it cross as one, and in their order. Held whole inside one request,
    /// a file was refused past what a request's one line could carry.
    /// </summary>
    [Fact]
    public async Task AFileLargerThanAPiece_IsWrittenToAHostInPieces_AndReadBackSo()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = temp.Combine("copy");
        var harness = new HarnessFactory();
        var asked = new AskedOperations(new HostCommandRunner(harness.ProcessRunner));
        var transport = new RemoteSyncTransport(
            HostId.Ssh("vps"),
            new HostSession(new HostConnection { Host = HostId.Local }, CliRunner.CliProgram),
            asked,
            harness.Output,
            pieceBytes: 4);
        var large = "abcdefghij"u8.ToArray();

        await transport.CreateRootAsync(root, cancellationToken: cancellationToken);
        asked.Operations.Clear();

        await transport.WriteFilesAsync(
            root,
            [new SyncFileContent("a.txt", "a"u8.ToArray()), new SyncFileContent("big.bin", large), new SyncFileContent("z.txt", "z"u8.ToArray())],
            cancellationToken);

        Assert.Equal(
            [
                "write-many a.txt YQ==",
                "write-piece big.bin 0",
                "write-piece big.bin 4",
                "write-piece big.bin 8",
                "place-pieces big.bin 10",
                "write-many z.txt eg==",
            ],
            asked.Operations);
        Assert.Equal("abcdefghij", await File.ReadAllTextAsync(Path.Combine(root, "big.bin"), cancellationToken));
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(root, "a.txt"), cancellationToken));
        Assert.Equal("z", await File.ReadAllTextAsync(Path.Combine(root, "z.txt"), cancellationToken));
        Assert.False(Directory.Exists(Path.Combine(root, LocalSyncTransport.PiecesDirectory)), "pieces were kept aside once the file was in place");

        var first = await transport.ReadFileAsync(root, "big.bin", 0, transport.PieceBytes, cancellationToken);
        var last = await transport.ReadFileAsync(root, "big.bin", 8, transport.PieceBytes, cancellationToken);

        Assert.Equal((10L, FileContentHash.Of(large), "abcd"), (first.Length, first.Content, Encoding.UTF8.GetString(first.Bytes)));
        Assert.Equal((10L, (string?)null, "ij"), (last.Length, last.Content, Encoding.UTF8.GetString(last.Bytes)));
        Assert.Equal(large, await SyncPieces.ReadWholeAsync(transport, root, "big.bin", cancellationToken));
    }

    /// <summary>
    /// A far side of the protocol before files crossed in pieces - 14, which a host not yet updated speaks - refuses a
    /// piece of a file this build sends it, before it reads any part of the request it does not know, naming both
    /// protocols and its own version; this machine says so under the host's name, and asks the host to remove what it was
    /// sent, which it refuses alike. Without the protocol raised, such a host would read the request as its own, and turn
    /// it away over whichever operation it happens not to know.
    /// </summary>
    [Fact]
    public async Task AFarSideOfTheProtocolBeforePieces_RefusesAPiece_NamingBothProtocols()
    {
        const int BeforePieces = 14;
        var asked = new List<string>();

        var transport = new RemoteSyncTransport(
            HostId.Ssh("vps"),
            new HostSession(new HostConnection { Host = HostId.Ssh("vps") }, "dssharness"),
            new ScriptedHostCommands((_, command) =>
            {
                // What every build since the protocol was carried does first: the protocol read on its own, and a request in
                // another refused, naming both, with no completion line.
                using var request = System.Text.Json.JsonDocument.Parse(command.StandardInput.Read());
                var protocol = request.RootElement.GetProperty("protocol").GetInt32();

                asked.Add(request.RootElement.GetProperty("arguments")[1].GetString()!);

                return protocol == BeforePieces
                    ? HostResults.Finished(command, HarnessExit.Success)
                    : HostResults.Failed(
                        HarnessExit.UsageError,
                        FailureLine.For(HostAgentProtocol.CommandName, $"the request speaks protocol {protocol}, and DssHarness 0.6.21 on this host speaks {BeforePieces}"));
            }),
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false),
            pieceBytes: 4);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => transport.WriteFileAsync("/home/dev/repo", "big.bin", "abcdefghij"u8.ToArray(), TestContext.Current.CancellationToken));

        Assert.Equal(
            (HarnessExit.HostUnavailable,
                $"ssh vps: '{SyncServe.WritePiece}' never reported how it finished, so it may not have run, or run only in part; the "
                + $"connection ended with exit {HarnessExit.UsageError}: host-agent: FAIL - the request speaks protocol "
                + $"{HostAgentProtocol.Version}, and DssHarness 0.6.21 on this host speaks {BeforePieces}"),
            (refusal.ExitCode, refusal.Message));
        Assert.Equal([SyncServe.WritePiece, SyncServe.DropPieces], asked);
    }

    /// <summary>
    /// Each of a sync's requests holds the host's input open and writes on it the beat the request says, as a leg's does:
    /// a sync's operation on a host is cancelled there too once neither its asker's input nor its beat reaches it.
    /// </summary>
    [Fact]
    public async Task ASyncsRequest_HoldsItsInputOpen_AndWritesTheBeatItSays()
    {
        HostCommand? sent = null;

        var transport = new RemoteSyncTransport(
            HostId.Ssh("vps"),
            new HostSession(new HostConnection { Host = HostId.Ssh("vps") }, "dssharness"),
            new ScriptedHostCommands((_, command) =>
            {
                sent = command;
                return HostResults.Finished(command, HarnessExit.Success);
            }),
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));

        await transport.DeleteFileAsync("/home/dev/repo", "a.txt", TestContext.Current.CancellationToken);

        Assert.NotNull(sent);
        Assert.True(sent.HoldStandardInputOpen);
        Assert.Equal(new InputBeat(TimeSpan.FromSeconds(HostAgentProtocol.BeatSeconds), HostAgentProtocol.BeatLine), sent.StandardInputBeat);
        Assert.Equal(
            HostAgentProtocol.BeatSeconds,
            System.Text.Json.JsonSerializer.Deserialize<HostAgentRequest>(sent.StandardInput.Read(), HostAgentProtocol.JsonOptions)!.BeatSeconds);
    }

    /// <summary>
    /// Reaches a host as <paramref name="inner"/> does, saying each sync operation it asks - the operation, then the first two
    /// values after the copy's root - in order.
    /// </summary>
    private sealed class AskedOperations(IHostCommandRunner inner) : IHostCommandRunner
    {
        public List<string> Operations { get; } = [];

        public Task<ProcessResult> RunAsync(HostConnection connection, HostCommand command, CancellationToken cancellationToken = default)
        {
            var arguments = System.Text.Json.JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput.Read(), HostAgentProtocol.JsonOptions)!.Arguments;

            // sync-serve, the operation, the mark that operands follow, the copy's root, then the operands.
            Operations.Add(string.Join(' ', [arguments[1].Text, .. arguments.Skip(4).Take(2).Select(argument => argument.Text)]));

            return inner.RunAsync(connection, command, cancellationToken);
        }

        public Task<ProcessResult> ProbeShellAsync(HostConnection connection, CancellationToken cancellationToken = default)
            => inner.ProbeShellAsync(connection, cancellationToken);

        public Task<ProcessResult> ReadSshSettingsAsync(HostConnection connection, CancellationToken cancellationToken = default)
            => inner.ReadSshSettingsAsync(connection, cancellationToken);

        public Task<ProcessResult> ProbeDefaultWslDistributionAsync(CancellationToken cancellationToken = default)
            => inner.ProbeDefaultWslDistributionAsync(cancellationToken);

        public Task<ProcessResult> ListRunningWslDistributionsAsync(CancellationToken cancellationToken = default)
            => inner.ListRunningWslDistributionsAsync(cancellationToken);
    }

    /// <summary>A host whose agent answers a read with <paramref name="answer"/>, then <paramref name="lines"/>.</summary>
    private static RemoteSyncTransport Answering(SyncFileAnswer answer, string[] lines) => new(
        HostId.Ssh("vps"),
        new HostSession(new HostConnection { Host = HostId.Ssh("vps") }, "dssharness"),
        new ScriptedHostCommands((_, command) =>
        {
            command.OnOutputLine?.Invoke(SyncServe.Answer(answer));

            foreach (var line in lines)
            {
                command.OnOutputLine?.Invoke(line);
            }

            return HostResults.Finished(command, HarnessExit.Success);
        }),
        new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));
}
