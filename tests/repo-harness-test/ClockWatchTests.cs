namespace RepoHarness.Tests;

/// <summary>
/// What a watched clock explains of a test that failed: an assertion about what a build or a run came to, where the
/// clock stepped while it was watched - and nothing where it held, nor any failure that is no assertion's.
/// </summary>
public sealed class ClockWatchTests
{
    /// <summary>
    /// A failed assertion is explained only once the clock has stepped by more than it is allowed; a failure of the
    /// test's own - a key its reading of the output did not find, an operation it had no right to - is explained by no
    /// clock, so a test that set every failure aside for a clock that stepped would hide it.
    /// </summary>
    [Fact]
    public void AClockThatStepped_ExplainsAFailedAssertion_AndNoOtherFailure()
    {
        var stepped = 0L;

        using var watch = new ClockWatch(() => DateTimeOffset.UtcNow + TimeSpan.FromTicks(Interlocked.Read(ref stepped)));
        var assertion = Record.Exception(() => Assert.Fail("the build came to something else"));

        Assert.NotNull(assertion);
        Assert.True(watch.Held);
        Assert.False(watch.Explains(assertion));

        Interlocked.Exchange(ref stepped, TimeSpan.FromSeconds(2).Ticks);

        Assert.False(watch.Held);
        Assert.True(watch.Explains(assertion));
        Assert.False(watch.Explains(new InvalidOperationException("the test's own")));
        Assert.False(watch.Explains(new KeyNotFoundException("the test's own")));
    }
}
