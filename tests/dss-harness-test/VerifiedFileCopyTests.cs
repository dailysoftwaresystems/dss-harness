using System.Text;
using NSubstitute;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>Bytes no file holds yet, written as a copy between trees is: proved before they are put in place.</summary>
public sealed class VerifiedFileCopyTests
{
    private static readonly byte[] Merged = Encoding.UTF8.GetBytes("what two changes of one file merged to\n");

    [Fact]
    public async Task BytesInHand_AreWrittenToTheirDestination_ItsDirectoryMade_AndNothingIsLeftBesideIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var destination = temp.Combine("made", "for", "it", "merged.txt");

        var written = await VerifiedFileCopy.WriteAsync(harness.FileSystem, harness.FilePermissions, Merged, destination, executable: false, TestContext.Current.CancellationToken);

        Assert.Equal(FileContent.Of(Merged), written);
        Assert.Equal(Merged, File.ReadAllBytes(destination));
        Assert.Equal(["merged.txt"], Directory.EnumerateFiles(Path.GetDirectoryName(destination)!).Select(Path.GetFileName));
    }

    /// <summary>
    /// What is put in place is what was read back, and bytes that came out other than they were given never replace the
    /// file that was there: a merge is not written over a tree's file as something neither side made.
    /// </summary>
    [Fact]
    public async Task BytesThatDoNotReadBackAsGiven_AreNotPutInPlace_AndWhatWasWrittenIsTakenBack()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var destination = temp.WriteFile("merged.txt", "what the tree held");
        var fileSystem = new WritingOtherBytes(harness.FileSystem);

        var refusal = await Assert.ThrowsAsync<IOException>(()
            => VerifiedFileCopy.WriteAsync(fileSystem, harness.FilePermissions, Merged, destination, executable: false, TestContext.Current.CancellationToken));

        Assert.Equal(
            $"'{destination}' was written as 5 byte(s) with SHA-256 {FileContentHash.Of("other"u8)}, where it was given "
            + $"{Merged.Length} with {FileContentHash.Of(Merged)}; what was written was not put in place.",
            refusal.Message);
        Assert.Equal("what the tree held", File.ReadAllText(destination));
        Assert.Equal(["merged.txt"], Directory.EnumerateFiles(temp.Path).Select(Path.GetFileName));
    }

    /// <summary>
    /// The bit asked for is given to what is written before it takes the destination's place, so the file is never in
    /// place with another: and it is the bit asked for, whatever the file replaced held.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheFileWritten_IsGivenTheBitItIsAskedFor_BeforeItIsPutInPlace(bool executable)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var destination = temp.WriteFile("run.sh", "#!/bin/sh\n");
        var permissions = Substitute.For<IFilePermissions>();
        var given = new List<(string Path, bool Executable, bool InPlace)>();

        permissions
            .When(asked => asked.SetExecutable(Arg.Any<string>(), Arg.Any<bool>()))
            .Do(call => given.Add((call.ArgAt<string>(0), call.ArgAt<bool>(1), File.ReadAllBytes(destination).SequenceEqual(Merged))));

        await VerifiedFileCopy.WriteAsync(harness.FileSystem, permissions, Merged, destination, executable, TestContext.Current.CancellationToken);

        var (path, bit, inPlace) = Assert.Single(given);
        Assert.StartsWith(destination + ".tmp-", path, StringComparison.Ordinal);
        Assert.Equal(executable, bit);
        Assert.False(inPlace, "The file was in place before it was given its bit.");
        Assert.Equal(Merged, File.ReadAllBytes(destination));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OnAPlatformThatKeepsTheBit_TheFileWrittenHoldsTheOneAskedFor_NotTheOneOfTheFileReplaced(bool replaced, bool asked)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var destination = temp.WriteFile("run.sh", "#!/bin/sh\n");
        harness.FilePermissions.SetExecutable(destination, replaced);

        await VerifiedFileCopy.WriteAsync(harness.FileSystem, harness.FilePermissions, Merged, destination, asked, TestContext.Current.CancellationToken);

        Assert.Equal(asked, harness.FilePermissions.IsExecutable(destination));
        Assert.Equal(Merged, File.ReadAllBytes(destination));
    }

    /// <summary>
    /// A file's bytes written anew are written as bytes in hand are, and the file written carries none of the source's
    /// attributes: one made from a read-only file is not read-only, and is given the bit asked for.
    /// </summary>
    [Fact]
    public async Task AFilesBytesWrittenAnew_AreWrittenAsBytesInHand_NoneOfItsAttributesCarried()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var source = temp.Combine("source.txt");
        File.WriteAllBytes(source, Merged);
        File.SetAttributes(source, FileAttributes.ReadOnly);
        var destination = temp.Combine("kept", "copy");

        try
        {
            var written = await VerifiedFileCopy.RewriteAsync(
                harness.FileSystem,
                harness.FilePermissions,
                source,
                destination,
                FileContentHash.Of(Merged),
                executable: false,
                TestContext.Current.CancellationToken);

            Assert.Equal(FileContent.Of(Merged), written);
            Assert.Equal(Merged, File.ReadAllBytes(destination));
            Assert.False(File.GetAttributes(destination).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(source, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// A file whose bytes are written anew that holds other bytes now than when it was measured is not written, in the words
    /// a copy of one says it: what would be written is not what was weighed.
    /// </summary>
    [Fact]
    public async Task AFileThatHoldsOtherBytesThanMeasured_IsNotWrittenAnew_NorCopied_InOneSentence()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var source = temp.WriteFile("source.txt", "changed since\n");
        var measured = FileContentHash.Of(Merged);
        var now = FileContentHash.Of("changed since\n"u8);
        var said = $"'{source}' holds other content now than when it was measured - SHA-256 {now}, where it was {measured} - so it was not copied.";

        var rewritten = await Assert.ThrowsAsync<IOException>(() => VerifiedFileCopy.RewriteAsync(
            harness.FileSystem, harness.FilePermissions, source, temp.Combine("rewritten"), measured, executable: false, TestContext.Current.CancellationToken));
        var copied = await Assert.ThrowsAsync<IOException>(() => VerifiedFileCopy.CopyAsync(
            harness.FileSystem, source, temp.Combine("copied"), measured, TestContext.Current.CancellationToken));

        Assert.Equal(said, rewritten.Message);
        Assert.Equal(said, copied.Message);
        Assert.Equal(["source.txt"], Directory.EnumerateFiles(temp.Path).Select(Path.GetFileName));
    }

    /// <summary>A file system that writes other bytes than it is given, as a disk that garbles a write would.</summary>
    private sealed class WritingOtherBytes(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
            => base.WriteAllBytesAtomicAsync(path, "other"u8.ToArray(), cancellationToken);
    }
}
