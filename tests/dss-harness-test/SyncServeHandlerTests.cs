using DssHarness.Core.Application.Hosting;
using DssHarness.Core.FileSystem;
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

        Assert.Equal(HarnessExit.UsageError, short_.ExitCode);
        Assert.Contains("needs 2 argument(s); it was given 1", short_.Message, StringComparison.Ordinal);

        var unknown = await Assert.ThrowsAsync<HarnessException>(
            () => handler.ServeAsync("everything", [root], token));

        Assert.Equal(HarnessExit.UsageError, unknown.ExitCode);
        Assert.Contains("'everything' is not a sync operation this build serves", unknown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The handler serves through the transport this machine reads its own copies through, with no place hidden -
    /// as the host of a copy serves it, where nothing of a user's sits beside the copy.
    /// </summary>
    private static SyncServeHandler Handler()
        => new(SyncKit.Transport(new HarnessFactory()), new TestIsolation());
}
