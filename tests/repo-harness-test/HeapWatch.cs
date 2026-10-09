namespace RepoHarness.Tests;

/// <summary>
/// Watches how far this process's managed heap grows while something runs, and says the most it grew by.
/// </summary>
/// <remarks>
/// <para>
/// The heap is read every few tens of milliseconds on a thread of its own, above the priority of the work it watches,
/// from a baseline taken once everything collectable was collected - and each reading is taken once everything
/// collectable was collected again. So what is counted is what is still held: a reader that drops what it reads shows
/// no growth, however much garbage the collector lets gather before it collects on its own - which is the collector's
/// budget, as large as the machine's caches and its settings make it, and nothing the code under test decides.
/// </para>
/// <para>
/// The heap is the process's, so what the rest of a test run allocates at the same time is counted too: a test watched by
/// this runs in <see cref="MemoryMeasured"/>, which runs alone.
/// </para>
/// </remarks>
internal sealed class HeapWatch : IDisposable
{
    /// <summary>How often the heap is read, each reading a collection of its own.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(25);

    /// <summary>The parts of the heap a collection reports on, in the order it reports them.</summary>
    private static readonly string[] PartNames = ["generation 0", "generation 1", "generation 2", "large objects", "pinned objects"];

    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _reader;
    private readonly long _baseline;
    private readonly string _baselineParts;
    private long _peak;
    private string _peakParts;
    private long _readings;

    public HeapWatch()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        _baseline = GC.GetTotalMemory(forceFullCollection: true);
        _baselineParts = Parts();
        _peak = _baseline;
        _peakParts = _baselineParts;

        _reader = new Thread(Read)
        {
            IsBackground = true,
            Name = "heap watch",
            Priority = ThreadPriority.AboveNormal,
        };

        _reader.Start();
    }

    /// <summary>The most the heap grew by over its baseline, in bytes, so far.</summary>
    public long Growth => Interlocked.Read(ref _peak) - _baseline;

    /// <summary>How many times the heap has been read so far.</summary>
    public long Readings => Interlocked.Read(ref _readings);

    /// <summary>
    /// Where the heap held what it held at its most, part by part, against where it held its baseline: for a bound's
    /// failure to say what grew.
    /// </summary>
    public string Where => $"at its most {Volatile.Read(ref _peakParts)}; at its baseline {_baselineParts}";

    /// <summary>
    /// What each part of the heap held once the last full collection had run: each generation's live objects, and the
    /// large and the pinned objects' - and how many objects that collection found pinned, and left to finalize.
    /// </summary>
    public static string Parts()
    {
        var collection = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var generations = collection.GenerationInfo;
        var parts = new List<string>();

        for (var at = 0; at < Math.Min(generations.Length, PartNames.Length); at++)
        {
            parts.Add($"{PartNames[at]} {generations[at].SizeAfterBytes - generations[at].FragmentationAfterBytes:N0}");
        }

        parts.Add($"{collection.PinnedObjectsCount:N0} object(s) pinned");
        parts.Add($"{collection.FinalizationPendingCount:N0} awaiting finalization");

        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        _stop.Set();
        _reader.Join();
        _stop.Dispose();
    }

    private void Read()
    {
        do
        {
            // Collected first: what remains is held by something, and garbage is not.
            GC.Collect();

            var now = GC.GetTotalMemory(forceFullCollection: false);

            if (now > Interlocked.Read(ref _peak))
            {
                Interlocked.Exchange(ref _peak, now);
                Volatile.Write(ref _peakParts, Parts());
            }

            Interlocked.Increment(ref _readings);
        }
        while (!_stop.Wait(Every));
    }
}

/// <summary>The tests that measure this process's memory, run alone so that nothing else's allocations are counted as theirs.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MemoryMeasured
{
    /// <summary>The collection's name.</summary>
    public const string Name = "memory measured";
}
