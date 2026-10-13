using System.Text;
using NSubstitute;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

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

    /// <summary>A file system that writes other bytes than it is given, as a disk that garbles a write would.</summary>
    private sealed class WritingOtherBytes(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
            => base.WriteAllBytesAtomicAsync(path, "other"u8.ToArray(), cancellationToken);
    }
}
