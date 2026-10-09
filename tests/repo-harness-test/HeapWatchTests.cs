namespace RepoHarness.Tests;

/// <summary>
/// What a watched heap counts: what is still held, and never what was dropped and only waits to be collected - which is
/// as much as the collector's budget lets gather, and nothing the code under test decides.
/// </summary>
[Collection(MemoryMeasured.Name)]
public sealed class HeapWatchTests
{
    /// <summary>How much is dropped, and then how much is held.</summary>
    private const int Much = 64 * 1024 * 1024;

    /// <summary>
    /// What was allocated and dropped is no growth, even where the collector would have let all of it gather - as it does
    /// here, told to collect nothing of its own while this much is allocated; what is held is growth, all of it but
    /// what little else the process let go of meanwhile.
    /// </summary>
    [Fact]
    public void WhatWasDropped_IsNoGrowth_AndWhatIsHeld_IsAllOfIt()
    {
        using var watch = new HeapWatch();

        // The collector's own budget, made larger than what is dropped: a machine whose caches, or whose settings, let
        // that much gather before a collection of its own.
        Assert.True(GC.TryStartNoGCRegion(Much + (Much / 4)), "This machine could not set aside the memory the measure needs.");

        try
        {
            for (var dropped = 0; dropped < Much; dropped += 64 * 1024)
            {
                GC.KeepAlive(new byte[64 * 1024]);
            }

            AwaitAReading(watch);
        }
        finally
        {
            try
            {
                GC.EndNoGCRegion();
            }
            catch (InvalidOperationException)
            {
                // Already ended, by the collection a reading is taken after.
            }
        }

        Assert.True(watch.Growth <= Much / 4, $"what was dropped grew the heap by {watch.Growth:N0} bytes: {watch.Where}");

        var held = new byte[Much];

        AwaitAReading(watch);

        Assert.True(watch.Growth >= Much - (Much / 16), $"{Much:N0} bytes held grew the heap by {watch.Growth:N0}: {watch.Where}");
        GC.KeepAlive(held);
    }

    /// <summary>Waits for a reading taken wholly after this was called: the one under way may have begun before it.</summary>
    private static void AwaitAReading(HeapWatch watch)
    {
        var seen = watch.Readings;

        Assert.True(SpinWait.SpinUntil(() => watch.Readings >= seen + 2, TimeSpan.FromSeconds(60)), "The watch stopped reading the heap.");
    }
}
