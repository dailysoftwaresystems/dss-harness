using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// The update sequence every program's updater shares: the change is run only under the machine-wide lock on the place
/// it writes, the change and the reading of what it left are run never stopped, so each must carry a bound of its own,
/// and what the reading says is told three ways apart.
/// </summary>
public sealed class ProgramUpdateTests
{
    /// <summary>
    /// A change or a listing that carries no bound is refused before anything runs: neither can be stopped once begun,
    /// so one with none would hold the change for good on a machine that never answers.
    /// </summary>
    [Theory]
    [InlineData(false, true, "change", "A change is run never stopped, so it carries a bound of its own. (Parameter 'change')")]
    [InlineData(true, false, "listSince", "A listing since a change is run never stopped, so it carries a bound of its own. (Parameter 'listSince')")]
    public async Task ARequestWithoutABound_IsRefused_AndNothingRuns(bool changeBounded, bool listingBounded, string parameter, string message)
    {
        var runner = new RecordingRunner(Listing: ProcessResultOf("listed"));
        var update = Update(runner);
        using var held = update.TryHold(Place());

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => update.ChangeAsync(
            held!,
            Request("change", changeBounded),
            Request("list", listingBounded),
            _ => ListedRelease.Absent));

        Assert.Equal(parameter, refused.ParamName);
        Assert.Equal(message, refused.Message);
        Assert.Empty(runner.Ran);
    }

    /// <summary>
    /// What is installed since the change is told three ways apart: a listing that failed is unreadable, whatever the
    /// reader would have made of it; and one that ran is what the reader says - a release, absent, or unreadable.
    /// </summary>
    [Fact]
    public async Task WhatIsListedSince_IsUnreadableWhereTheListingFailed_AndTheReadersOtherwise()
    {
        var failed = await ChangeAsync(new RecordingRunner(Listing: new ProcessResult(1, string.Empty, "locked\n", TimeSpan.Zero, TimedOut: false)), _ => ListedRelease.Read("1.2.3"));
        var absent = await ChangeAsync(new RecordingRunner(Listing: ProcessResultOf("{}")), _ => ListedRelease.Read(null));
        var named = await ChangeAsync(
            new RecordingRunner(Listing: ProcessResultOf("{}")),
            listing => string.Equals(listing, "{}", StringComparison.Ordinal) ? ListedRelease.Read("1.2.3") : ListedRelease.Unreadable);

        Assert.Equal((false, (string?)null), (failed.Now.Readable, failed.Now.Release));
        Assert.Same(ListedRelease.Absent, absent.Now);
        Assert.Equal((true, (string?)null), (absent.Now.Readable, absent.Now.Release));
        Assert.Equal((true, "1.2.3"), (named.Now.Readable, named.Now.Release));
    }

    /// <summary>
    /// The lock on a place is had by one update at a time, however the place is spelled: a second update of it finds the
    /// lock held while the first holds it, and takes it once the first has let it go. An update of another place is not
    /// held up by it - one program kept in two places is two changes that touch nothing of each other's.
    /// </summary>
    [Fact]
    public void ThePlacesLock_IsHadByOneUpdateAtATime_HoweverThePlaceIsSpelled_AndHoldsUpNoOtherPlace()
    {
        var update = Update(new RecordingRunner(Listing: ProcessResultOf("{}")));
        var place = Place();
        var first = update.TryHold(place);

        try
        {
            Assert.NotNull(first);
            Assert.Null(update.TryHold(place));
            Assert.Null(update.TryHold(Path.Combine(place, ".")));

            using var elsewhere = update.TryHold(Place());

            Assert.NotNull(elsewhere);
        }
        finally
        {
            first?.Dispose();
        }

        using var next = update.TryHold(place);

        Assert.NotNull(next);
    }

    /// <summary>
    /// A change is run only under a lock that is held: given one already let go, it is refused before anything runs, since
    /// a second update of the place could hold it by then.
    /// </summary>
    [Fact]
    public async Task AChangeUnderALockLetGo_IsRefused_AndNothingRuns()
    {
        var runner = new RecordingRunner(Listing: ProcessResultOf("{}"));
        var update = Update(runner);
        var held = update.TryHold(Place())!;

        held.Dispose();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => update.ChangeAsync(
            held,
            Request("change", bounded: true),
            Request("list", bounded: true),
            _ => ListedRelease.Absent));

        Assert.Equal(
            ("held", "A change is run under the lock on the place it writes, and this one has been let go. (Parameter 'held')"),
            (refused.ParamName, refused.Message));
        Assert.Empty(runner.Ran);
    }

    /// <summary>
    /// A lock whose holder ended holding it - a process killed part way through its change, a thread here - is taken by
    /// the next update, which reads what that change left as it reads anything installed: never refused for good.
    /// </summary>
    [Fact]
    public void ALockWhoseHolderEndedHoldingIt_IsTakenByTheNextUpdate()
    {
        var update = Update(new RecordingRunner(Listing: ProcessResultOf("{}")));
        var place = Place();

        // Kept open here, so the mutex outlives the thread that ends holding it, as a lock outlives a killed process
        // while any other process has it open.
        using var kept = MachineWideMutex.Open(update.LockName(place), "a test's place");
        using var taken = new ManualResetEventSlim();
        using var end = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            kept.WaitOne();
            taken.Set();
            end.Wait();
        });

        holder.Start();
        taken.Wait(TestContext.Current.CancellationToken);

        Assert.Null(update.TryHold(place));

        end.Set();
        holder.Join();

        using var next = update.TryHold(place);

        Assert.NotNull(next);
    }

    private static ProgramUpdate Update(IProcessRunner runner) => new(runner, HostDoubles.Platform());

    /// <summary>A place no other test, and no other run of this suite, holds the lock on.</summary>
    private static string Place() => Path.Combine(TestHost.TemporaryRoot, "tools-" + Guid.NewGuid().ToString("N"));

    /// <summary>A change through <paramref name="runner"/>, under a lock of its own.</summary>
    private static async Task<ProgramChange> ChangeAsync(RecordingRunner runner, Func<string, ListedRelease> readRelease)
    {
        var update = Update(runner);
        using var held = update.TryHold(Place());

        return await update.ChangeAsync(held!, Request("change", bounded: true), Request("list", bounded: true), readRelease);
    }

    private static ProcessRequest Request(string program, bool bounded) => new()
    {
        FileName = program,
        Timeout = bounded ? TimeSpan.FromMinutes(1) : null,
    };

    private static ProcessResult ProcessResultOf(string output) => new(0, output, string.Empty, TimeSpan.Zero, TimedOut: false);

    /// <summary>A runner that answers every change with success and every listing with <paramref name="Listing"/>, recording what it ran.</summary>
    private sealed record RecordingRunner(ProcessResult Listing) : IProcessRunner
    {
        public List<string> Ran { get; } = [];

        public string? FindExecutable(string command) => command;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Ran.Add(request.FileName);

            return Task.FromResult(string.Equals(request.FileName, "list", StringComparison.Ordinal) ? Listing : ProcessResultOf("changed"));
        }
    }
}
