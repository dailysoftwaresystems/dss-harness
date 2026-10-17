using DssHarness.Core.Application;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// The directory a command acts on, resolved once before its body runs: the one it was asked for, or the current one
/// where it was asked for none, absolute and there - or the problem with it, in the line every command refuses by.
/// </summary>
public sealed class CommandDirectoryTests : IDisposable
{
    private readonly TempDirectory _current = new();
    private readonly PhysicalFileSystem _fileSystem = new(FilePermissionsFactory.Create());

    /// <summary>A directory asked for relative is answered absolute, against the current one.</summary>
    [Fact]
    public void ADirectoryAskedForRelative_IsAnsweredAbsolute_AgainstTheCurrentOne()
    {
        var answer = CommandDirectory.Resolve(".", () => _current.Path, _fileSystem);

        Assert.True(answer.Holds);
        Assert.Equal(Path.GetFullPath(_current.Path), answer.Directory);
        Assert.Null(answer.Problem);
    }

    /// <summary>Where no directory is asked for, the current one is acted on.</summary>
    [Fact]
    public void WhereNoDirectoryIsAskedFor_TheCurrentOneIsActedOn()
    {
        var answer = CommandDirectory.Resolve(null, () => _current.Path, _fileSystem);

        Assert.True(answer.Holds);
        Assert.Equal(Path.GetFullPath(_current.Path), answer.Directory);
    }

    /// <summary>
    /// A directory that is not there is refused by the line every command refuses by, naming the directory absolute -
    /// not the fragment that was typed, which would send a reader looking for a directory that is spelled two ways.
    /// </summary>
    [Fact]
    public void ADirectoryThatIsNotThere_IsRefused_NamingItAbsolute()
    {
        var answer = CommandDirectory.Resolve("not-there", () => _current.Path, _fileSystem);

        Assert.False(answer.Holds);
        Assert.Null(answer.Directory);
        Assert.Equal($"Directory '{Path.GetFullPath(Path.Combine(_current.Path, "not-there"))}' does not exist.", answer.Problem);
    }

    /// <summary>
    /// A path that cannot be used at all - one the operating system refuses to make sense of - says so, naming what was
    /// asked for and the reason, so the reader is not sent after a missing directory that was never spelled.
    /// </summary>
    [Fact]
    public void APathThatCannotBeUsed_SaysSo_NamingWhatWasAskedFor()
    {
        var answer = CommandDirectory.Resolve("not\0a\0directory", () => _current.Path, _fileSystem);

        Assert.False(answer.Holds);
        Assert.Null(answer.Directory);
        Assert.StartsWith("'not\0a\0directory' is not a usable directory path: ", answer.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A directory asked for blank names none, and is refused naming what was asked for - never made whole against the
    /// current directory, which would act on a tree nobody asked for.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ADirectoryAskedForBlank_IsRefused_NeverTakenForTheCurrentOne(string blank)
    {
        var answer = CommandDirectory.Resolve(blank, () => _current.Path, _fileSystem);

        Assert.False(answer.Holds);
        Assert.Null(answer.Directory);
        Assert.Equal($"'{blank}' is not a usable directory path: it names no directory.", answer.Problem);
    }

    /// <summary>
    /// A directory asked for whole is resolved without the current directory being read at all: a shell standing in a
    /// directory since removed, whose current directory Linux and macOS cannot read, still runs a command given another.
    /// </summary>
    [Fact]
    public void ADirectoryAskedForWhole_IsResolved_WithoutTheCurrentDirectoryBeingRead()
    {
        var read = 0;

        var answer = CommandDirectory.Resolve(
            _current.Path,
            () =>
            {
                read++;
                throw new FileNotFoundException("the directory this process stands in was removed");
            },
            _fileSystem);

        Assert.Equal(new ResolvedDirectory(Path.GetFullPath(_current.Path), null), answer);
        Assert.Equal(0, read);
    }

    /// <inheritdoc/>
    public void Dispose() => _current.Dispose();
}
