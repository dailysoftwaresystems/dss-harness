using DssHarness.Core.Application.Hosting;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>
/// The use case behind the hidden sync-serve command, as the machine asking reaches it: each operation served
/// in-process over the transport this machine uses for a copy of its own, its answers read back exactly as the
/// asking side reads them off the wire.
/// </summary>
public sealed class SyncServeHandlerTests
{
    /// <summary>
    /// A copy made and a file written answer nothing - the wire must stay silent rather than say a status line the
    /// machine did not ask for - and what the asking side then reads back, through the same readers it reads the
    /// wire with, is what was written and where.
    /// </summary>
    [Fact]
    public async Task WhatTheHandlerServes_TheMachineAskingReadsBack()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var root = temp.Combine("copy");
        var handler = Handler();

        // The copy is made, and a file written to it: both answer nothing.
        Assert.Null((await handler.ServeAsync(SyncServe.Create, [root], token)).Line);
        Assert.Null((await handler.ServeAsync(
            SyncServe.Write,
            [root, "src/a.txt", Convert.ToBase64String("a\n"u8)],
            token)).Line);

        Assert.Equal("a\n", await File.ReadAllTextAsync(Path.Combine(root, "src", "a.txt"), token));

        // The listing answers, as one line the asking side reads back with its own reader.
        var listed = await handler.ServeAsync(SyncServe.List, [root, "src"], token);

        Assert.NotNull(listed.Line);
        Assert.Empty(listed.Following);

        var listing = SyncServe.ReadAnswer<SyncDirectoryListing>(listed.Line!);

        Assert.NotNull(listing);
        Assert.Equal(["src/a.txt"], [.. listing!.Files.Select(file => file.Path)]);

        // The read answers what it carries - how many bytes the file holds, the hash of all of it and of the piece - and
        // the piece's bytes follow it, a line at a time, decoding back to what was written.
        var read = await handler.ServeAsync(SyncServe.Read, [root, "src/a.txt", "0", "8192"], token);

        Assert.NotNull(read.Line);

        var file = SyncServe.ReadAnswer<SyncFileAnswer>(read.Line!);
        var carried = new MemoryStream();
        var piece = new byte[8192];

        foreach (var line in read.Following)
        {
            carried.Write(piece, 0, SyncServe.ReadContentLine(line, piece, "src/a.txt"));
        }

        Assert.Equal("a\n"u8.ToArray(), carried.ToArray());
        Assert.Equal(new SyncFileAnswer("a\n"u8.Length, FileContentHash.Of("a\n"u8), FileContentHash.Of("a\n"u8)), file);

        // A directory a deletion emptied is taken away, and the prune says so; a copy removed says what it was.
        Directory.CreateDirectory(temp.Combine("copy", "gone"));

        var pruned = SyncServe.ReadAnswer<SyncPruneAnswer>(
            (await handler.ServeAsync(SyncServe.Prune, [root, "gone", "src"], token)).Line!)!;

        Assert.True(Assert.Single(pruned.Directories, entry => entry.Path == "gone").Removed);
        Assert.False(Assert.Single(pruned.Directories, entry => entry.Path == "src").Removed);
        Assert.False(Directory.Exists(temp.Combine("copy", "gone")));

        Assert.Equal(
            CopyRemoval.Removed,
            SyncServe.ReadAnswer<SyncRemoveAnswer>(
                (await handler.ServeAsync(SyncServe.RemoveCopy, [root], token)).Line!)!.Removal);
    }

    /// <summary>
    /// A piece of a file read from a copy holds what the file has from where it starts, up to as many bytes as were asked
    /// for, each piece answered with its own hash: only the one that starts the file says what all of it holds, which the
    /// rest are held to. Asked for none, the first answers that alone - what a file landed reads back as, no byte of it
    /// carried back.
    /// </summary>
    [Fact]
    public async Task APieceRead_HoldsWhatTheFileHasFromWhereItStarts_AndOnlyTheFirstSaysWhatAllOfItHolds()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var root = temp.Combine("copy");
        var handler = Handler();

        await handler.ServeAsync(SyncServe.Create, [root], token);
        await File.WriteAllTextAsync(Path.Combine(root, "big.bin"), "abcdefghij", token);

        Assert.Equal(
            (new SyncFileAnswer(10, FileContentHash.Of("abcdefghij"u8), FileContentHash.Of("abcd"u8)), "abcd"),
            await PieceAsync(handler, root, "0", "4", token));
        Assert.Equal(
            (new SyncFileAnswer(10, null, FileContentHash.Of("efgh"u8)), "efgh"),
            await PieceAsync(handler, root, "4", "4", token));
        Assert.Equal(
            (new SyncFileAnswer(10, null, FileContentHash.Of("ij"u8)), "ij"),
            await PieceAsync(handler, root, "8", "4", token));
        Assert.Equal(
            (new SyncFileAnswer(10, FileContentHash.Of("abcdefghij"u8), FileContentHash.Of(ReadOnlySpan<byte>.Empty)), string.Empty),
            await PieceAsync(handler, root, "0", "0", token));
    }

    /// <summary>
    /// A piece of a file sent to a copy whose bytes hash otherwise than its sender said is refused naming the piece - the
    /// file, where it starts, how many bytes, both hashes - and nothing of it is kept: the piece before it stays aside, the
    /// file is not put in place, and the next piece sent at the same place is taken. Kept unchecked, a piece damaged on the
    /// way would be found only once the whole file failed its hash, naming none of it.
    /// </summary>
    [Fact]
    public async Task APieceWhoseHashIsWrong_IsRefusedNamingThePiece_AndNothingOfItIsKept()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var root = temp.Combine("copy");
        var handler = Handler();
        var kept = Path.Combine(root, LocalSyncTransport.PiecesDirectory, FileContentHash.Of("big.bin"u8));

        await handler.ServeAsync(SyncServe.Create, [root], token);
        await handler.ServeAsync(SyncServe.WritePiece, Piece(root, 0, "abcd"u8.ToArray(), FileContentHash.Of("abcd"u8)), token);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => handler.ServeAsync(SyncServe.WritePiece, Piece(root, 4, "efgh"u8.ToArray(), FileContentHash.Of("efgX"u8)), token));

        Assert.Equal(
            (HarnessExit.CommandFailed,
                $"'big.bin' changed between the machine that sent it and this host: its piece of 4 byte(s) at byte 4 was read as "
                + $"{FileContentHash.Of("efgX"u8)} and arrived as {FileContentHash.Of("efgh"u8)}. Nothing of it was kept."),
            (refusal.ExitCode, refusal.Message));
        Assert.Equal(["0000000000000000004"], Directory.GetFiles(kept).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(root, "big.bin")), "a file was put in place before all of it had arrived");

        // Sent again as it was read, the piece is taken where the damaged one was refused, and the file placed whole.
        await handler.ServeAsync(SyncServe.WritePiece, Piece(root, 4, "efgh"u8.ToArray(), FileContentHash.Of("efgh"u8)), token);
        await handler.ServeAsync(SyncServe.PlacePieces, [root, "big.bin", "8", FileContentHash.Of("abcdefgh"u8)], token);

        Assert.Equal("abcdefgh", await File.ReadAllTextAsync(Path.Combine(root, "big.bin"), token));
        Assert.False(Directory.Exists(Path.Combine(root, LocalSyncTransport.PiecesDirectory)), "the pieces outlived the file they made");
    }

    /// <summary>
    /// The pieces of a file follow one another exactly, and the file is put in place only whole and as it was read: a piece
    /// out of its turn is refused, keeping nothing; a file whose pieces hold fewer bytes than it is said to, or other bytes,
    /// is not put in place, what was at its path staying as it was; a first piece begins the file again; and pieces dropped
    /// leave nothing aside. Placed from pieces that did not follow, or that held something else, the copy would hold a file
    /// nobody sent under the name of one somebody did.
    /// </summary>
    [Fact]
    public async Task PiecesFollowExactly_AndAFileIsPutInPlaceOnlyWholeAndAsItWasRead()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var root = temp.Combine("copy");
        var handler = Handler();
        var kept = Path.Combine(root, LocalSyncTransport.PiecesDirectory, FileContentHash.Of("big.bin"u8));

        await handler.ServeAsync(SyncServe.Create, [root], token);
        await File.WriteAllTextAsync(Path.Combine(root, "big.bin"), "old", token);
        await handler.ServeAsync(SyncServe.WritePiece, Piece(root, 0, "abcd"u8.ToArray(), FileContentHash.Of("abcd"u8)), token);

        var outOfTurn = await Assert.ThrowsAsync<HarnessException>(
            () => handler.ServeAsync(SyncServe.WritePiece, Piece(root, 8, "ijkl"u8.ToArray(), FileContentHash.Of("ijkl"u8)), token));

        Assert.Equal(
            (HarnessExit.CommandFailed, $"A piece of 'big.bin' sent to '{root}' did not follow what arrived before it: it starts at byte 8, and 4 byte(s) had arrived. Nothing of it was kept."),
            (outOfTurn.ExitCode, outOfTurn.Message));

        var short_ = await Assert.ThrowsAsync<HarnessException>(
            () => handler.ServeAsync(SyncServe.PlacePieces, [root, "big.bin", "8", FileContentHash.Of("abcdefgh"u8)], token));

        Assert.Equal(
            (HarnessExit.CommandFailed, $"'big.bin' did not arrive whole in '{root}': 4 byte(s) of its 8 did. Nothing was put in its place."),
            (short_.ExitCode, short_.Message));

        var other = await Assert.ThrowsAsync<HarnessException>(
            () => handler.ServeAsync(SyncServe.PlacePieces, [root, "big.bin", "4", FileContentHash.Of("abcX"u8)], token));

        Assert.Equal(
            (HarnessExit.CommandFailed,
                $"'big.bin' did not arrive in '{root}' as it was read: it was read as {FileContentHash.Of("abcX"u8)}, 4 byte(s), and its "
                + $"pieces hold {FileContentHash.Of("abcd"u8)}, 4 byte(s). Nothing was put in its place."),
            (other.ExitCode, other.Message));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(root, "big.bin"), token));

        // A first piece begins the file again, whatever was kept of it before.
        await handler.ServeAsync(SyncServe.WritePiece, Piece(root, 0, "wxyz"u8.ToArray(), FileContentHash.Of("wxyz"u8)), token);

        Assert.Equal(["0000000000000000004"], Directory.GetFiles(kept).Select(Path.GetFileName));
        Assert.Equal("wxyz", await File.ReadAllTextAsync(Path.Combine(kept, "0000000000000000004"), token));

        await handler.ServeAsync(SyncServe.DropPieces, [root, "big.bin"], token);

        Assert.False(Directory.Exists(Path.Combine(root, LocalSyncTransport.PiecesDirectory)), "dropped pieces were left aside");
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(root, "big.bin"), token));
    }

    /// <summary>A write-piece request's arguments for 'big.bin': the piece at <paramref name="offset"/>, sent saying <paramref name="hash"/>.</summary>
    private static string[] Piece(string root, long offset, byte[] bytes, string hash)
        => [root, "big.bin", offset.ToString(System.Globalization.CultureInfo.InvariantCulture), hash, Convert.ToBase64String(bytes)];

    /// <summary>The answer to a read of 'big.bin' at <paramref name="offset"/>, and the text its piece decodes to.</summary>
    private static async Task<(SyncFileAnswer Answer, string Text)> PieceAsync(SyncServeHandler handler, string root, string offset, string count, CancellationToken token)
    {
        var read = await handler.ServeAsync(SyncServe.Read, [root, "big.bin", offset, count], token);
        var carried = new MemoryStream();
        var piece = new byte[8192];

        foreach (var line in read.Following)
        {
            carried.Write(piece, 0, SyncServe.ReadContentLine(line, piece, "big.bin"));
        }

        return (SyncServe.ReadAnswer<SyncFileAnswer>(read.Line!)!, System.Text.Encoding.UTF8.GetString(carried.ToArray()));
    }

    /// <summary>
    /// An operation given fewer arguments than it takes is refused naming how many it needs, and one this build
    /// does not know is refused by name - never answered as though nothing happened, which would hide that the two
    /// ends are different builds.
    /// </summary>
    [Fact]
    public async Task AnOperationShortOfAnArgumentOrUnknownToThisBuild_IsRefused()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var handler = Handler();
        var root = temp.Combine("copy");

        var short_ = await Assert.ThrowsAsync<HarnessException>(
            () => handler.ServeAsync(SyncServe.Read, [root], token));

        Assert.Equal(
            (HarnessExit.UsageError, $"sync operation '{SyncServe.Read}' needs 2 argument(s); it was given 1."),
            (short_.ExitCode, short_.Message));

        var unknown = await Assert.ThrowsAsync<HarnessException>(
            () => handler.ServeAsync("everything", [root], token));

        Assert.Equal(
            (HarnessExit.UsageError, "'everything' is not a sync operation this build serves."),
            (unknown.ExitCode, unknown.Message));
    }

    /// <summary>
    /// An empty operation is one this build does not serve, refused by name as a usage error - the two ends are
    /// different builds - and never taken for a defect of this tool.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AnEmptyOperation_IsRefusedByName(string operation)
    {
        using var temp = new TempDirectory();

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Handler().ServeAsync(operation, [temp.Combine("copy")], TestContext.Current.CancellationToken));

        Assert.Equal(
            (HarnessExit.UsageError, $"'{operation}' is not a sync operation this build serves."),
            (refusal.ExitCode, refusal.Message));
    }

    /// <summary>
    /// An operation given no copy to act on, and a history taken with no commit to take, are each refused naming what is
    /// missing: the two ends are different builds.
    /// </summary>
    [Fact]
    public async Task AnOperationGivenNoCopy_OrAHistoryTakenWithNoCommit_IsRefused()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var handler = Handler();

        var copyless = await Assert.ThrowsAsync<HarnessException>(() => handler.ServeAsync(SyncServe.Read, [], token));

        Assert.Equal(
            (HarnessExit.UsageError, $"sync operation '{SyncServe.Read}' was given no copy to act on. The two ends are different builds."),
            (copyless.ExitCode, copyless.Message));

        var commitless = await Assert.ThrowsAsync<HarnessException>(
            () => handler.ServeAsync(SyncServe.TakeHistory, [temp.Combine("copy"), SyncServe.Nothing, SyncServe.Nothing], token));

        Assert.Equal(
            (HarnessExit.UsageError, $"sync operation '{SyncServe.TakeHistory}' was given no commit to take. The two ends are different builds."),
            (commitless.ExitCode, commitless.Message));
    }

    /// <summary>
    /// What the isolation hides on this host no pull out of the copy reaches, as the host serves it: a file a link in the
    /// copy leads into a hidden place is refused naming that place, and so is a listing of a directory the link leads
    /// into - a credential slot or a Claude Code directory is never carried back through a link a leg's work left.
    /// </summary>
    [Fact]
    public async Task APullOrAListingALinkLeadsIntoAHiddenPlace_IsRefused()
    {
        using var temp = new TempDirectory();
        using var hidden = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var place = hidden.Path;
        var root = temp.Combine("copy");
        var handler = new SyncServeHandler(
            SyncKit.Transport(new HarnessFactory()),
            new TestIsolation { HiddenPlaces = [new IsolatedPath(place, Directory: true)] });

        await File.WriteAllTextAsync(Path.Combine(place, "key.txt"), "secret", token);
        await handler.ServeAsync(SyncServe.Create, [root], token);
        TestLinks.DirectoryLink(Path.Combine(root, "out"), place);

        var pulled = await Assert.ThrowsAsync<HarnessException>(() => handler.ServeAsync(SyncServe.Pull, [root, "out/key.txt", "0", "8192"], token));

        Assert.Equal(
            (HarnessExit.CommandFailed, $"'out/key.txt' leads to '{Path.Combine(place, "key.txt")}' from '{root}', inside '{place}', which the isolation hides from every command and no pull reaches; nothing was read from it."),
            (pulled.ExitCode, pulled.Message));

        var listed = await Assert.ThrowsAsync<HarnessException>(() => handler.ServeAsync(SyncServe.List, [root, "out"], token));

        Assert.Equal(
            (HarnessExit.CommandFailed, $"'out/' leads to '{place}' from '{root}', inside '{place}', which the isolation hides from every command and no pull reaches; nothing was read from it."),
            (listed.ExitCode, listed.Message));
    }

    /// <summary>
    /// The handler serves through the transport this machine reads its own copies through, with no place hidden -
    /// as the host of a copy serves it, where nothing of a user's sits beside the copy.
    /// </summary>
    private static SyncServeHandler Handler()
        => new(SyncKit.Transport(new HarnessFactory()), new TestIsolation());
}
