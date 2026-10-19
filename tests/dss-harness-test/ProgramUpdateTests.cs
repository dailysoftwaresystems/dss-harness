using DssHarness.Core.Hosts;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// The update sequence every program's updater shares: the change and the reading of what it left are run never
/// stopped, so each must carry a bound of its own, and what the reading says is told three ways apart.
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

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => new ProgramUpdate(runner).ChangeAsync(
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
        var failed = await new ProgramUpdate(new RecordingRunner(Listing: new ProcessResult(1, string.Empty, "locked\n", TimeSpan.Zero, TimedOut: false)))
            .ChangeAsync(Request("change", bounded: true), Request("list", bounded: true), _ => ListedRelease.Read("1.2.3"));
        var absent = await new ProgramUpdate(new RecordingRunner(Listing: ProcessResultOf("{}")))
            .ChangeAsync(Request("change", bounded: true), Request("list", bounded: true), _ => ListedRelease.Read(null));
        var named = await new ProgramUpdate(new RecordingRunner(Listing: ProcessResultOf("{}")))
            .ChangeAsync(Request("change", bounded: true), Request("list", bounded: true), listing => string.Equals(listing, "{}", StringComparison.Ordinal) ? ListedRelease.Read("1.2.3") : ListedRelease.Unreadable);

        Assert.Equal((false, (string?)null), (failed.Now.Readable, failed.Now.Release));
        Assert.Same(ListedRelease.Absent, absent.Now);
        Assert.Equal((true, (string?)null), (absent.Now.Readable, absent.Now.Release));
        Assert.Equal((true, "1.2.3"), (named.Now.Readable, named.Now.Release));
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
