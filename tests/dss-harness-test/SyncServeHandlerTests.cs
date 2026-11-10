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

        // The read answers what it carries - how many bytes, the hash of what they are - and the bytes follow it,
        // a line at a time, decoding back to what was written.
        var read = await handler.ServeAsync(SyncServe.Read, [root, "src/a.txt"], token);

        Assert.NotNull(read.Line);

        var file = SyncServe.ReadAnswer<SyncFileAnswer>(read.Line!);
        var carried = new MemoryStream();
        var piece = new byte[8192];

        foreach (var line in read.Following)
        {
            carried.Write(piece, 0, SyncServe.ReadContentLine(line, piece, "src/a.txt"));
        }

        Assert.Equal("a\n"u8.ToArray(), carried.ToArray());
        Assert.Equal("a\n"u8.Length, file!.Length);
        Assert.Equal(FileContentHash.Of("a\n"u8.ToArray()), file.ContentHash);

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

        var pulled = await Assert.ThrowsAsync<HarnessException>(() => handler.ServeAsync(SyncServe.Pull, [root, "out/key.txt"], token));

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
