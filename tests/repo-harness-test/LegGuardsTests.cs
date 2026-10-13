using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// One scope watches a span of work for every verb. What it must never do is turn "nobody looked"
/// into "nothing found", and what it must never do to a verb that asked for nothing is fail it.
/// </summary>
public sealed class LegGuardsTests
{
    private static readonly PhaseResult Passed = new(
        "leg", "step", ExitCode: 0, Stalled: false, StallSeconds: 0, Witnessed: null,
        Duration: TimeSpan.Zero, ClockDrift: TimeSpan.Zero, ClockStepped: false,
        Timings: [], LogFile: "log", Output: PhaseOutput.Empty);

    /// <summary>
    /// A set that is empty because nothing the build reads is tracked is not a set nobody could
    /// establish. Read as unmeasured it would fail every build in a repository that happens not to
    /// track the language it builds — which is what this did before a test existed for it.
    /// </summary>
    [Fact]
    public void AnEmptySetWithNoReason_ContributesNothing()
    {
        var report = new LegGuardReport(Inputs: null, Contention: null);

        Assert.Equal(LegVerdict.Passed, report.Decide("leg", [ReachedVerdict.Of(LegVerdict.Passed)]).Verdict);
    }

    /// <summary>
    /// A set that could not be established is the opposite, and must not read as a tree that held
    /// still.
    /// </summary>
    [Fact]
    public void ASetNobodyCouldEstablish_IsUnmeasured_NotClean()
    {
        var report = new LegGuardReport(
            new InputComparison(InputChange.Unmeasured, [], "git could not be asked"),
            Contention: null);

        Assert.Equal(LegVerdict.Unmeasured, report.Decide("leg", [ReachedVerdict.Of(LegVerdict.Passed)]).Verdict);
    }

    /// <summary>
    /// The worst of everything that decided, whichever of them it was: a verb holds its work to
    /// things a phase knows nothing about, and a build's witness is one of them.
    /// </summary>
    [Fact]
    public void TheWorstOfWhatEveryoneSaw_Wins_WhoeverSawIt()
    {
        var report = new LegGuardReport(
            new InputComparison(InputChange.Moved, ["src/a.c"], "src/a.c moved"),
            Contention: null);

        var decided = report.Decide("leg", [ReachedVerdict.Of(LegVerdict.Passed)]);

        Assert.NotEqual(LegVerdict.Passed, decided.Verdict);
        Assert.Contains("src/a.c", decided.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards whose watch cannot start are never opened: the refusal leaves for the work to be stopped before it begins,
    /// and nothing was started that somebody would have to stop - the process table is not read once.
    /// </summary>
    [Fact]
    public async Task GuardsWhoseWatchCannotStart_AreNeverOpened_AndStartNoSampling()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/a.c", "a");
        var factory = new HarnessFactory();
        var table = new CountingTable();
        var kind = ScriptedWatchKind.Refusing();

        var refused = await Assert.ThrowsAsync<InputsUnwatchedException>(
            () => LegGuards.OpenAsync(
                new InputFingerprint(factory.FileSystem, factory.Platform, kind),
                new ProcessSampler(table, factory.Platform, factory.Output),
                new LegGuardRequest
                {
                    TreeRoot = temp.Path,
                    Inputs = LegInputs.Watch(["src/a.c"]),
                    Contention = new ContentionRequest { Leg = "leg", BuildDirectory = temp.Combine("build") },
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(ScriptedWatchKind.Refused, refused.Message);
        Assert.Equal(1, kind.Starts);
        Assert.Equal(0, table.Readings);
    }

    /// <summary>
    /// Guards that open hold their watch until they are disposed of, and let go of it then, whether or not anybody asked
    /// what they saw.
    /// </summary>
    [Fact]
    public async Task GuardsThatOpened_HoldTheirWatch_UntilTheyAreDisposedOf()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("src/a.c", "a");
        var factory = new HarnessFactory();
        var kind = new ScriptedWatchKind();

        await using (await LegGuards.OpenAsync(
            new InputFingerprint(factory.FileSystem, factory.Platform, kind),
            new ProcessSampler(new CountingTable(), factory.Platform, factory.Output),
            new LegGuardRequest { TreeRoot = temp.Path, Inputs = LegInputs.Watch(["src/a.c"]) },
            TestContext.Current.CancellationToken))
        {
            Assert.Equal((1, 0), (kind.Starts, kind.Stops));
            Assert.Equal([WatchTarget.NamesOnly(temp.Path), WatchTarget.Entries(temp.Combine("src"))], kind.Targets);
        }

        Assert.Equal(1, kind.Stops);
    }

    /// <summary>A process table that counts how often it is read, and finds nothing but this test.</summary>
    private sealed class CountingTable : RepoHarness.Core.Platform.IProcessTable
    {
        private int _readings;

        /// <summary>How many times it was read.</summary>
        public int Readings => Volatile.Read(ref _readings);

        public Task<RepoHarness.Core.Platform.ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readings);

            return new QuietProcessTable().ReadAsync(cancellationToken);
        }
    }

    /// <summary>A span that decided nothing has no verdict, and says so rather than inventing one.</summary>
    [Fact]
    public void ASpanThatDecidedNothing_IsRefused()
        => Assert.Throws<ArgumentException>(() => new LegGuardReport(null, null).Decide("leg", []));
}
