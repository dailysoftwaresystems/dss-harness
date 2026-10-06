using System.Text.Json;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>The balance against real commits: every base is a commit the test made.</summary>
public sealed class AnchorBalanceServiceTests
{
    private const string One = "D-AREA-TOPIC-ONE";
    private const string Two = "D-AREA-TOPIC-TWO";
    private const string StayOpen = "D-AREA-TOPIC-STAYOPEN";
    private const string StaleMark = "D-AREA-TOPIC-STALEMARK";
    private const string WorkDone = "D-AREA-TOPIC-WORKDONE";
    private const string NewDebt = "D-AREA-TOPIC-NEWDEBT";

    /// <summary>A Trigger that carries a closure by this change's work.</summary>
    private const string Worked = "✅ **CLOSED** 2026-10-01: the work landed in this change";

    /// <summary>A Trigger that carries a closure repairing the mark of work done before the base.</summary>
    private const string Bookkept = "✅🧾 **CLOSED** 2026-10-01, mark repaired: the work predates the base";

    [Fact]
    public async Task AChangeThatClosesAsMuchAsItOpens_Holds()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);

        await harness.AnchorRegistryService.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal((1, 1, 0), (report.OpenAtBase, report.OpenNow, report.NetNew));
        Assert.Equal([new AnchorClosing(One, Bookkeeping: false)], report.Closed);
        Assert.Equal([Two], report.Opened.Select(opening => opening.Id));
        Assert.Empty(report.Findings);
    }

    /// <summary>
    /// Two changes differing in one edit: whether a row whose work predates the base has its mark repaired with the
    /// bookkeeping pair. The open count must differ - the repaired row is closed - and the count a change is held to
    /// must not: the change is credited with nothing for the repair. Credited, it would buy the change a new anchor.
    /// </summary>
    [Fact]
    public async Task ABookkeepingClosure_LeavesTheOpenCount_AndCreditsTheChangeNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, triggerCarriesVerdict: true, StayOpen, StaleMark, WorkDone);
        var registry = harness.AnchorRegistryService;

        await registry.SetAsync(
            temp.Path, new AnchorSetRequest(WorkDone) { Status = "closed", Trigger = Worked }, dryRun: false, cancellationToken);
        await registry.WriteAsync(temp.Path, Anchor(NewDebt), dryRun: false, cancellationToken);

        var unrepaired = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        await registry.SetAsync(
            temp.Path, new AnchorSetRequest(StaleMark) { Status = "closed", Trigger = Bookkept }, dryRun: false, cancellationToken);

        var repaired = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal((3, 2), (unrepaired.OpenNow, repaired.OpenNow));
        Assert.Equal((0, 0), (unrepaired.NetNew, repaired.NetNew));
        Assert.Equal([new AnchorClosing(StaleMark, Bookkeeping: true), new AnchorClosing(WorkDone, Bookkeeping: false)], repaired.Closed);
        Assert.Equal(1, repaired.Bookkeeping);
        Assert.Equal(-1, repaired.OpenNow - repaired.OpenAtBase - repaired.Disclosed);
        Assert.True(repaired.Passed);
        Assert.Empty(repaired.Findings);
    }

    /// <summary>
    /// By default the Status is the only verdict and nothing is read from the Trigger, so the pair there is prose
    /// and the closure is credited.
    /// </summary>
    [Fact]
    public async Task ByDefault_TheBookkeepingPairIsProse_AndTheClosureIsCredited()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);

        await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed", Trigger = Bookkept }, dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([new AnchorClosing(One, Bookkeeping: false)], report.Closed);
        Assert.Equal(-1, report.NetNew);
    }

    /// <summary>
    /// Only an anchor that was open at the base can be closed by the change: the pair on a row closed before it is
    /// none of the change's business, and charges it nothing.
    /// </summary>
    [Fact]
    public async Task TheBookkeepingPairOnARowClosedBeforeTheBase_ChargesTheChangeNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, triggerCarriesVerdict: true);
        var registry = harness.AnchorRegistryService;

        await registry.WriteAsync(
            temp.Path, new AnchorWriteRequest(One, "P1", Worked) { Status = "closed" }, dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "closed before the base", cancellationToken);
        await registry.SetAsync(temp.Path, new AnchorSetRequest(One) { Trigger = Bookkept }, dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Empty(report.Closed);
        Assert.Equal(0, report.NetNew);
        Assert.True(report.Passed);
    }

    /// <summary>
    /// Where a Trigger carries its row's verdict, a row whose two cells disagree fails the balance either way round, as
    /// it fails --lint: whether a closure is bookkeeping is read from the Trigger, and the marks written the wrong way
    /// round would otherwise leave the closure credited. By default the Trigger is prose, and nothing is judged in it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARowStatingTwoVerdicts_FailsTheBalance_OnlyWhereTheTriggerCarriesOne(bool triggerCarriesVerdict)
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp, triggerCarriesVerdict, One);

        // Written by hand, the door being what refuses such rows: one closed with its marks the wrong way round, and one
        // open whose Trigger reads closed.
        TakeRowByHand(PendingPath(temp), One);
        File.AppendAllText(DonePath(temp), $"| `{One}` | P1 | ✅ CLOSED | 🧾✅ **CLOSED** 2026-10-01, mark repaired | - | - |\n");
        File.AppendAllText(PendingPath(temp), $"| `{Two}` | P1 | 🟠 OPEN | ✅ **CLOSED** 2026-10-01: the work landed | - | - |\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.Equal([new AnchorClosing(One, Bookkeeping: false)], report.Closed);
        Assert.Equal(0, report.NetNew);
        Assert.Equal(!triggerCarriesVerdict, report.Passed);
        Assert.Equal(
            triggerCarriesVerdict ? [AnchorSettings.DefaultDoneAnchorsPath, AnchorSettings.DefaultPendingAnchorsPath] : [],
            report.Findings
                .Where(finding => finding.Message.Contains("anchors.triggerCarriesVerdict holds a row to one verdict", StringComparison.Ordinal))
                .Select(finding => finding.File));
    }

    /// <summary>
    /// A closure is bookkeeping where any row its id has says so. Every row an id still has is closed, and crediting the
    /// closure would take every one of them saying the work was done by this change.
    /// </summary>
    [Fact]
    public async Task AClosureIsBookkeeping_WhereAnyRowOfItsIdSaysSo()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, triggerCarriesVerdict: true, One);

        await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed", Trigger = Bookkept }, dryRun: false, cancellationToken);
        File.AppendAllText(DonePath(temp), $"| `{One}` | P1 | ✅ CLOSED | {Worked} | - | - |\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([new AnchorClosing(One, Bookkeeping: true)], report.Closed);
        Assert.Equal(0, report.NetNew);
    }

    /// <summary>
    /// A row moves between the registries and is never deleted, so an anchor open at the base that neither holds now
    /// fails the balance, by name. Counted as closed, a row deleted or renamed by hand was credited as progress.
    /// </summary>
    [Fact]
    public async Task AnAnchorOpenAtTheBaseThatNeitherRegistryHoldsNow_FailsTheBalance()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp, One);

        // Renamed by hand: the id the base held is gone, and another holds its row.
        var pending = PendingPath(temp);
        File.WriteAllText(pending, File.ReadAllText(pending).Replace($"`{One}`", $"`{Two}`", StringComparison.Ordinal));

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, report.NetNew);
        Assert.False(report.Passed);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(AnchorSettings.DefaultPendingAnchorsPath, finding.File);
        Assert.Contains($"'{One}' was open where this change began and neither registry holds it now", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry malformed now is its own finding: an anchor whose row it no longer reads is not reported lost
    /// besides, since the row is there, where nothing reads it - whichever registry holds it.
    /// </summary>
    [Theory]
    [InlineData(AnchorRegistryKind.Pending)]
    [InlineData(AnchorRegistryKind.Done)]
    public async Task ARegistryMalformedNow_IsItsOwnFinding_NotEveryAnchorItHidesLost(AnchorRegistryKind kind)
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp, One);
        var path = await HoldingAsync(harness, temp, kind, One);

        // Stranded below a paragraph, outside the table.
        var row = TakeRowByHand(path, One);
        File.AppendAllText(path, $"\nA paragraph.\n\n{row}\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Contains("outside any table", Assert.Single(report.Findings).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A base that has moved on since HEAD left it is measured from where HEAD left it. Compared with directly, its own
    /// later changes counted, reversed, as the change's: the anchor it gained as one the change lost, and the anchor it
    /// closed as one the change created.
    /// </summary>
    [Fact]
    public async Task ABaseThatMovedOn_IsMeasuredFromWhereHeadLeftIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One, Two);
        var registry = harness.AnchorRegistryService;
        var forked = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);

        await GitAsync(harness, temp, "branch", "upstream");
        await GitAsync(harness, temp, "checkout", "-q", "-b", "work");
        await registry.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await registry.WriteAsync(temp.Path, Anchor(StayOpen), dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "the change", cancellationToken);

        await GitAsync(harness, temp, "checkout", "-q", "upstream");
        await registry.SetAsync(temp.Path, new AnchorSetRequest(Two) { Status = "closed" }, dryRun: false, cancellationToken);
        await registry.WriteAsync(temp.Path, Anchor(NewDebt), dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "the base moves on", cancellationToken);
        await GitAsync(harness, temp, "checkout", "-q", "work");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, "upstream", cancellationToken);

        Assert.True(report.BaseMovedOn);
        Assert.Equal(forked, report.Commit);
        Assert.Equal(await harness.GitClient.ResolveCommitAsync(temp.Path, "upstream", cancellationToken), report.BaseCommit);
        Assert.Equal([new AnchorClosing(One, Bookkeeping: false)], report.Closed);
        Assert.Equal([StayOpen], report.Opened.Select(opening => opening.Id));
        Assert.Empty(report.Findings);
        Assert.True(report.Passed);

        // The receipt names both commits, and what the count was measured against.
        var receipt = AnchorReports.Balance(report, json: false);
        Assert.Equal(
            $"base      upstream ({ReportText.Commit(report.BaseCommit)}), measured from where HEAD left it ({ReportText.Commit(report.Commit)})",
            receipt.Data[0]);
        Assert.Equal("open      2 where HEAD left upstream, 2 now", receipt.Data[1]);
        Assert.EndsWith("against 2 where HEAD left upstream", receipt.Message, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(AnchorReports.Balance(report, json: true).Data[0]);
        Assert.Equal(report.BaseCommit, json.RootElement.GetProperty("baseCommit").GetString());
        Assert.Equal(forked, json.RootElement.GetProperty("commit").GetString());

        // HEAD detached at the same commit is measured the same.
        await GitAsync(harness, temp, "checkout", "-q", "--detach");
        var detached = await harness.AnchorBalanceService.CheckAsync(temp.Path, "upstream", cancellationToken);

        Assert.Equal(report.Commit, detached.Commit);
        Assert.Equal(report.Closed, detached.Closed);
        Assert.Equal(report.Opened, detached.Opened);
    }

    /// <summary>
    /// A base ahead of HEAD on HEAD's own line is measured from HEAD, where HEAD's history leaves it, so only what the
    /// tree changed counts. Compared with directly, the anchor the base went on to close counted as one this change
    /// created.
    /// </summary>
    [Fact]
    public async Task ABaseAheadOfHead_IsMeasuredFromHead_SoOnlyWhatTheTreeChangedCounts()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        var registry = harness.AnchorRegistryService;
        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;

        await GitAsync(harness, temp, "branch", "behind");
        await GitAsync(harness, temp, "checkout", "-q", "-b", "upstream");
        await registry.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "the base moves on", cancellationToken);
        await GitAsync(harness, temp, "checkout", "-q", "behind");
        await registry.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, "upstream", cancellationToken);

        Assert.True(report.BaseMovedOn);
        Assert.Equal(head, report.Commit);
        Assert.Empty(report.Closed);
        Assert.Equal([Two], report.Opened.Select(opening => opening.Id));
        Assert.Equal(1, report.NetNew);
        Assert.Equal(
            $"base      upstream ({ReportText.Commit(report.BaseCommit)}), measured from where HEAD left it ({ReportText.Commit(head)})",
            AnchorReports.Balance(report, json: false).Data[0]);
    }

    /// <summary>
    /// A HEAD that names no commit yet shares no history with any base, so there is no point the change began from:
    /// refused, saying so, whether a base is named or not.
    /// </summary>
    [Fact]
    public async Task AHeadThatNamesNoCommit_IsRefused_WhateverTheBase()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        var committed = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);
        await GitAsync(harness, temp, "checkout", "-q", "--orphan", "unborn");

        foreach (var baseReference in new[] { null, committed })
        {
            var refusal = await Assert.ThrowsAsync<HarnessException>(
                () => harness.AnchorBalanceService.CheckAsync(temp.Path, baseReference, cancellationToken));

            Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
            Assert.StartsWith("HEAD names no commit yet", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The receipt says where each count was measured: at the base, or where HEAD left it where the base has moved on,
    /// a registry missing there included.
    /// </summary>
    [Theory]
    [InlineData(false, "at upstream")]
    [InlineData(true, "where HEAD left upstream")]
    public void TheReceipt_SaysWhereEachCountWasMeasured(bool movedOn, string measured)
    {
        var baseCommit = new string('a', 40);
        var report = new AnchorBalanceReport(
            "upstream", baseCommit, movedOn ? new string('b', 40) : baseCommit, 0, 0, [], [], [AnchorSettings.DefaultDoneAnchorsPath], []);

        var receipt = AnchorReports.Balance(report, json: false);

        Assert.Equal($"open      0 {measured}, 0 now", receipt.Data[1]);
        Assert.Contains($"note      {AnchorSettings.DefaultDoneAnchorsPath} did not exist {measured}, so it counts as empty there", receipt.Data);
        Assert.EndsWith($"against 0 {measured}", receipt.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The registries as they stand are read together under the lock every change holds, so a row a change is moving
    /// between them is never read in neither, which would count as closed, or lost. Held too long, the balance refuses.
    /// </summary>
    [Fact]
    public async Task TheBalance_ReadsBothRegistriesUnderTheLock()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var registries = await harness.AnchorRegistryLocator.LocateAsync(context, cancellationToken);
        var impatient = new AnchorBalanceService(
            harness.ContextLoader,
            harness.AnchorRegistryLocator,
            new NamedMutexAnchorRegistryLock(harness.Platform, TimeSpan.FromMilliseconds(200)),
            harness.GitClient,
            harness.FileSystem);

        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var holder = new Thread(() => harness.AnchorRegistryLock.RunExclusive(registries, () =>
        {
            held.Set();
            release.Wait(TimeSpan.FromSeconds(60));
            return 0;
        }));

        holder.Start();

        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(60), cancellationToken), "The holder never took the lock.");

            var refusal = await Assert.ThrowsAsync<HarnessException>(() => impatient.CheckAsync(temp.Path, null, cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        }
        finally
        {
            release.Set();
            holder.Join();
        }

        Assert.True((await impatient.CheckAsync(temp.Path, null, cancellationToken)).Passed);
    }

    /// <summary>A base that shares no history with HEAD has no point the change began from, and is refused.</summary>
    [Fact]
    public async Task ABaseThatSharesNoHistoryWithHead_IsRefused()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        var unrelated = (await GitAsync(harness, temp, "commit-tree", "HEAD^{tree}", "-m", "unrelated")).Trim();

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => harness.AnchorBalanceService.CheckAsync(temp.Path, unrelated, cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.Contains("shares no history with HEAD", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry missing now is its own finding: the anchors it held are not reported lost besides, since nothing
    /// could have been read where they would be - whichever registry it is.
    /// </summary>
    [Theory]
    [InlineData(AnchorRegistryKind.Pending, "no pending registry")]
    [InlineData(AnchorRegistryKind.Done, "no done registry")]
    public async Task ARegistryMissingNow_IsOneFinding_NotEveryAnchorItHeldLost(AnchorRegistryKind kind, string missing)
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp, One, Two);
        File.Delete(await HoldingAsync(harness, temp, kind, One));

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Contains(missing, Assert.Single(report.Findings).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a row is closed was decided by what its Status cell OPENS with, so a cell nothing
    /// can parse still landed in one column and was counted from there. Measured on a consumer's
    /// registry: 'read-anchors --lint' exited 1 on three rows while this verb answered that the
    /// balance held. Both verbs cannot be right about one file, and the one that COUNTS is the one
    /// that must not guess.
    /// </summary>
    [Fact]
    public async Task AStatusCellNothingCanParse_IsRefusedHereToo_NotSilentlyCounted()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);

        // Opens with the closed mark, so every reader testing the opening glyph takes it for
        // closed, and none of them reads the words after it.
        var registry = PendingPath(temp);
        var text = await File.ReadAllTextAsync(registry, cancellationToken);

        await File.WriteAllTextAsync(
            registry,
            text.Replace(
                AnchorStatus.Render(AnchorState.Open),
                AnchorStatus.ClosedMark + " CLOSED (superseded)",
                StringComparison.Ordinal),
            cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Contains(
            report.Findings,
            finding => finding.Message.Contains("is not one of", StringComparison.Ordinal));

        // The severity is what decides the verb's answer, and a Warning here would leave this verb
        // saying the balance holds while --lint exits 1 on the same file. That divergence is the
        // whole reason this test exists.
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task AChangeThatLeavesMoreOpenAnchors_Fails()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp);

        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(1, report.NetNew);
        Assert.Equal("trigger for " + One, Assert.Single(report.Opened).Excerpt);
    }

    [Fact]
    public async Task ANewlyDisclosedAnchor_IsNotCounted()
    {
        // Disclosure records debt that already existed; writing it down is not creating it.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp);

        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One, "disclosed"), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal((1, 0), (report.Disclosed, report.NetNew));
        Assert.True(Assert.Single(report.Opened).Disclosed);
    }

    [Fact]
    public async Task AnchorsAreCountedById_AcrossBothRegistries()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);

        // Moved by hand, still open: the count cannot change, and the misfiling is its own finding.
        File.AppendAllText(DonePath(temp), TakeRowByHand(PendingPath(temp), One) + "\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal((1, 1, 0), (report.OpenAtBase, report.OpenNow, report.NetNew));
        Assert.Empty(report.Opened);
        Assert.Empty(report.Closed);
        Assert.False(report.Passed);
        Assert.Contains(report.Findings, finding => finding.Message.Contains("live anchor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClosedAnchorLeftInPending_FailsTheCheck()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` | P1 | ✅ CLOSED | t | - | - |\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(0, report.NetNew);
        Assert.Contains(report.Findings, finding => finding.Message.Contains("closed anchor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARegistryThatDidNotExistAtTheBase_CountsAsEmpty_AndIsNoted()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.InitService.InitializeAsync(temp.Path, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One, "disclosed"), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([AnchorSettings.DefaultPendingAnchorsPath, AnchorSettings.DefaultDoneAnchorsPath], report.MissingAtBase);
        Assert.Equal(0, report.OpenAtBase);
        Assert.True(report.Passed);
    }

    /// <summary>
    /// A registry the base lists that git cannot read refuses the check, naming it. Read as absent, it
    /// was reported missing at the base, and every anchor open in it counted as newly opened.
    /// </summary>
    [Fact]
    public async Task ARegistryTheBaseListsButGitCannotRead_IsRefused_NotReportedMissing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        await harness.LoseObjectAsync(temp.Path, $"HEAD:{AnchorSettings.DefaultPendingAnchorsPath}", cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.Contains($"'{AnchorSettings.DefaultPendingAnchorsPath}'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("git fsck", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadGivenExplicitly_BehavesExactlyLikeNoBase()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        var implicitBase = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);
        var explicitBase = await harness.AnchorBalanceService.CheckAsync(temp.Path, "HEAD", cancellationToken);

        Assert.Equal("HEAD", implicitBase.Base);
        Assert.Equal(implicitBase.Commit, explicitBase.Commit);
        Assert.Equal(
            (implicitBase.OpenAtBase, implicitBase.OpenNow, implicitBase.NetNew, implicitBase.Passed),
            (explicitBase.OpenAtBase, explicitBase.OpenNow, explicitBase.NetNew, explicitBase.Passed));
        Assert.Equal(implicitBase.Closed, explicitBase.Closed);
        Assert.Equal(implicitBase.Opened, explicitBase.Opened);
        Assert.Equal(implicitBase.MissingAtBase, explicitBase.MissingAtBase);
        Assert.Equal(implicitBase.Findings, explicitBase.Findings);
    }

    [Fact]
    public async Task AnOlderBase_IsComparedWithTheTreeAsItIsNow()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        await harness.AnchorRegistryService.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "close one", cancellationToken);

        var sinceLast = await harness.AnchorBalanceService.CheckAsync(temp.Path, "HEAD", cancellationToken);
        var sinceBefore = await harness.AnchorBalanceService.CheckAsync(temp.Path, "HEAD~1", cancellationToken);

        Assert.Empty(sinceLast.Closed);
        Assert.Equal([new AnchorClosing(One, Bookkeeping: false)], sinceBefore.Closed);
    }

    [Fact]
    public async Task ABaseThatNamesNoCommit_IsRefused()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp);

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorBalanceService.CheckAsync(temp.Path, "no-such-branch", TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
    }

    [Fact]
    public async Task ARegistryGitIgnores_IsRefused_BecauseItHasNoHistory()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(".gitignore", "local/\n");
        harness.WriteConfig(temp.Path, new HarnessConfig
        {
            Anchors = new AnchorSettings { PendingAnchorsPath = "local/pending.md", DoneAnchorsPath = "local/done.md" },
        });
        await harness.InitService.InitializeAsync(temp.Path, cancellationToken);

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("ignored by git", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedOrMissingRegistry_FailsTheCheck()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp);
        File.AppendAllText(PendingPath(temp), $"\nA paragraph.\n\n| `{One}` | P1 | 🟠 OPEN | stray | - | - |\n");
        File.Delete(DonePath(temp));

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.False(report.Passed);
        Assert.Contains(report.Findings, finding => finding.Message.Contains("outside any table", StringComparison.Ordinal));
        Assert.Contains(report.Findings, finding => finding.Message.Contains("no done registry", StringComparison.Ordinal));
    }

    private static Task<HarnessFactory> PrepareCommittedAsync(TempDirectory temp, params string[] openAnchors)
        => PrepareCommittedAsync(temp, triggerCarriesVerdict: false, openAnchors);

    private static async Task<HarnessFactory> PrepareCommittedAsync(TempDirectory temp, bool triggerCarriesVerdict, params string[] openAnchors)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(
            temp.Path,
            cancellationToken,
            triggerCarriesVerdict ? HarnessFactory.TriggerCarriesVerdict() : null);

        foreach (var id in openAnchors)
        {
            await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(id), dryRun: false, cancellationToken);
        }

        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);
        return harness;
    }

    private static AnchorWriteRequest Anchor(string id, string status = "open")
        => new(id, "P1", $"trigger for {id}") { Status = status };

    /// <summary>Runs git in the test's repository, failing the test where git fails, and returns what it printed.</summary>
    private static async Task<string> GitAsync(HarnessFactory harness, TempDirectory temp, params string[] arguments)
    {
        var result = await harness.GitClient.RunAsync(temp.Path, arguments, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.FailureMessage);
        return result.StandardOutput;
    }

    /// <summary>Takes <paramref name="id"/>'s row out of the registry at <paramref name="path"/> by hand, as no command would, and returns it.</summary>
    private static string TakeRowByHand(string path, string id)
    {
        var row = File.ReadAllLines(path).Single(line => line.Contains(id, StringComparison.Ordinal));
        File.WriteAllText(path, File.ReadAllText(path).Replace(row + "\n", string.Empty, StringComparison.Ordinal));
        return row;
    }

    /// <summary>The registry of <paramref name="kind"/>, holding <paramref name="id"/>: closed by set-anchor, where that is the done one.</summary>
    private static async Task<string> HoldingAsync(HarnessFactory harness, TempDirectory temp, AnchorRegistryKind kind, string id)
    {
        if (kind == AnchorRegistryKind.Pending)
        {
            return PendingPath(temp);
        }

        await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(id) { Status = "closed" }, dryRun: false, TestContext.Current.CancellationToken);
        return DonePath(temp);
    }

    private static string PendingPath(TempDirectory temp) => temp.Combine(".plans", "_deferred-anchor-registry.md");

    private static string DonePath(TempDirectory temp) => temp.Combine(".plans", "_deferred-anchor-registry-done.md");
}
