using System.Text.Json;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// A file of state read without stopping the command: what it holds, nothing where no file is there, or why it could
/// not be read - by the same rules, and from the same failure, as the read that refuses.
/// </summary>
public sealed class MachineWideFileTests
{
    /// <summary>No file there is nothing to read, which is an answer and no failure.</summary>
    [Fact]
    public void AFileThatIsNotThere_IsReadAsNothing_AndIsNoFailure()
    {
        using var temp = new TempDirectory();

        var read = MachineWideFile.TryRead<Kept>(new HarnessFactory().FileSystem, temp.Combine("kept.json"), out var value, out var unread);

        Assert.True(read);
        Assert.Null(value);
        Assert.Null(unread);
    }

    /// <summary>A file of the shape asked for is what it holds.</summary>
    [Fact]
    public void AFileOfTheShapeAsked_IsWhatItHolds()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("kept.json");
        File.WriteAllText(path, """{ "name": "first", "count": 3 }""");

        var read = MachineWideFile.TryRead<Kept>(new HarnessFactory().FileSystem, path, out var value, out var unread);

        Assert.True(read);
        Assert.Equal(new Kept("first", 3), value);
        Assert.Null(unread);
    }

    /// <summary>
    /// A file that is there and is not this build's JSON - no JSON at all, null, a member no type declares, a member
    /// the type needs left out - is answered with why, never thrown, and never read as absent: read as absent, whatever
    /// decides by it would write over it.
    /// </summary>
    [Theory]
    [InlineData("{ not json", "'n' is an invalid start of a property name")]
    [InlineData("null", "it holds null")]
    [InlineData("""{ "name": "first", "count": 3, "later": true }""", "later")]
    [InlineData("""{ "name": "first" }""", "count")]
    public void AFileThatIsNotThisBuildsJson_IsAnsweredWithWhy_NeverThrownAndNeverReadAsAbsent(string contents, string why)
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("kept.json");
        File.WriteAllText(path, contents);

        var read = MachineWideFile.TryRead<Kept>(new HarnessFactory().FileSystem, path, out var value, out var unread);

        Assert.False(read);
        Assert.Null(value);
        Assert.IsType<JsonException>(unread, exactMatch: false);
        Assert.Contains(why, unread.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file another process holds, or one this user may not read, is answered with what the system said, as the two
    /// kinds it raises them as.
    /// </summary>
    [Theory]
    [InlineData(false, typeof(IOException))]
    [InlineData(true, typeof(UnauthorizedAccessException))]
    public void AFileThatCannotBeLookedAt_IsAnsweredWithWhatTheSystemSaid(bool denied, Type raised)
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("kept.json");
        File.WriteAllText(path, """{ "name": "first", "count": 3 }""");

        var read = MachineWideFile.TryRead<Kept>(new Held(new HarnessFactory().FileSystem, path, denied), path, out var value, out var unread);

        Assert.False(read);
        Assert.Null(value);
        Assert.IsType(raised, unread);
        Assert.Equal(Held.Why, unread.Message);
    }

    /// <summary>
    /// The read that refuses is the same read: it refuses exactly what the other could not read, in its caller's words
    /// made from that failure, and gives what the other gives everywhere else.
    /// </summary>
    [Fact]
    public void TheReadThatRefuses_RefusesWhatTheOtherCouldNotRead_InItsCallersWords()
    {
        using var temp = new TempDirectory();
        var fileSystem = new HarnessFactory().FileSystem;
        var path = temp.Combine("kept.json");

        Assert.Null(MachineWideFile.Read<Kept>(fileSystem, path, ex => "never asked"));

        File.WriteAllText(path, """{ "name": "first", "count": 3 }""");

        Assert.Equal(new Kept("first", 3), MachineWideFile.Read<Kept>(fileSystem, path, ex => "never asked"));

        File.WriteAllText(path, "null");

        var refusal = Assert.Throws<HarnessException>(() => MachineWideFile.Read<Kept>(fileSystem, path, ex => $"The kept file cannot be read: {ex.Message}."));

        Assert.False(MachineWideFile.TryRead<Kept>(fileSystem, path, out _, out var unread));
        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Equal("The kept file cannot be read: it holds null.", refusal.Message);
        Assert.IsType<JsonException>(refusal.InnerException);
        Assert.Equal(unread.Message, refusal.InnerException.Message);
    }

    /// <summary>What the test's file holds.</summary>
    /// <param name="Name">A member the file must give.</param>
    /// <param name="Count">Another.</param>
    private sealed record Kept(string Name, int Count);
}
