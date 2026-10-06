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
    private const string Repaired = "D-AREA-TOPIC-REPAIRED";

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

        // The receipt says which closure was not credited, and only that one.
        var receipt = AnchorReports.Balance(repaired, json: false).Data;
        Assert.Equal($"base      HEAD ({ReportText.Commit(repaired.BaseCommit)})", receipt[0]);
        Assert.Contains("change    2 closed (1 bookkeeping), 1 opened (1 created, 0 disclosed); counted 0", receipt);
        Assert.Contains($"  - {StaleMark}   [bookkeeping: not credited]", receipt);
        Assert.Contains($"  - {WorkDone}", receipt);

        using var json = JsonDocument.Parse(AnchorReports.Balance(repaired, json: true).Data[0]);
        Assert.Equal(
            [(StaleMark, true), (WorkDone, false)],
            json.RootElement.GetProperty("closed").EnumerateArray()
                .Select(closing => (closing.GetProperty("anchor").GetString(), closing.GetProperty("bookkeeping").GetBoolean())));
    }

    /// <summary>
    /// By default the Status is the only verdict and no verdict is read from the Trigger, so the pair there is prose
    /// and the closure is credited; it is noted, failing nothing.
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
        Assert.Equal(AnchorFindingSeverity.Note, Assert.Single(report.Findings).Severity);
        Assert.True(report.Passed);
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
    /// A closure is bookkeeping where any row its id has says so, whichever row comes first. Every row an id still has is
    /// closed, and crediting the closure would take every one of them saying the work was done by this change. Two rows
    /// of one id are a finding of their own, on each row, as --lint's are.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClosureIsBookkeeping_WhereAnyRowOfItsIdSaysSo(bool bookkeptFirst)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, triggerCarriesVerdict: true, One);

        await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed", Trigger = bookkeptFirst ? Bookkept : Worked }, dryRun: false, cancellationToken);
        File.AppendAllText(DonePath(temp), $"| `{One}` | P1 | ✅ CLOSED | {(bookkeptFirst ? Worked : Bookkept)} | - | - |\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([new AnchorClosing(One, Bookkeeping: true)], report.Closed);
        Assert.Equal(0, report.NetNew);
        Assert.False(report.Passed);
        Assert.Equal(2, report.Findings.Count(finding => finding.Message.Contains($"'{One}' has 2 rows", StringComparison.Ordinal)));
    }

    /// <summary>
    /// An id with two rows fails the balance with the findings --lint gives it, one on each row: everything here is
    /// counted by id, so the two count as one, and what becomes of either cannot be told apart.
    /// </summary>
    [Fact]
    public async Task AnIdWithTwoRows_FailsTheBalance_AsItFailsLint()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        File.AppendAllText(DonePath(temp), $"| `{One}` | P1 | ✅ CLOSED | t | - | - |\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);
        var lint = await harness.AnchorRegistryService.LintAsync(temp.Path, cancellationToken);

        Assert.Equal((1, 1, 0), (report.OpenAtBase, report.OpenNow, report.NetNew));
        Assert.False(report.Passed);
        Assert.Equal(2, report.Findings.Count);
        Assert.Equal(
            lint.Select(finding => (finding.File, finding.LineNumber, finding.Severity, finding.Message)).Order(),
            report.Findings.Select(finding => (finding.File, finding.LineNumber, finding.Severity, finding.Message)).Order());
    }

    /// <summary>
    /// An anchor that had a closed row already where the change began, beside its open one, is not credited when the
    /// change takes the open row away: the work was recorded as done before the change, which took away only the open
    /// copy of a duplicate. Credited, deleting a row by hand paid for a new anchor. Noted, failing nothing itself.
    /// </summary>
    [Fact]
    public async Task TakingAwayTheOpenCopyOfAnAnchorClosedAlready_CreditsTheChangeNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        File.AppendAllText(DonePath(temp), $"| `{One}` | P1 | ✅ CLOSED | t | - | - |\n");
        await harness.CommitAllAsync(temp.Path, "a closed copy beside the open original", cancellationToken);

        TakeRowByHand(PendingPath(temp), One);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([new AnchorClosing(One, Bookkeeping: true)], report.Closed);
        Assert.Empty(report.Lost);
        Assert.Equal(1, report.NetNew);
        Assert.False(report.Passed);

        var note = Assert.Single(report.Findings);
        Assert.Equal((AnchorSettings.DefaultDoneAnchorsPath, AnchorFindingSeverity.Note), (note.File, note.Severity));
        Assert.Contains($"anchor '{One}' had a closed row already where this change began, beside its open one", note.Message, StringComparison.Ordinal);
        Assert.Contains($"  - {One}   [bookkeeping: not credited]", AnchorReports.Balance(report, json: false).Data);
    }

    /// <summary>
    /// A row moves between the registries and is never deleted, so an anchor open at the base that neither holds now
    /// fails the balance, by name, and is not credited. Counted as closed, a row deleted or renamed by hand was credited
    /// as progress, and paid for the anchor its new name made, which went unreported until the row came back.
    /// </summary>
    [Fact]
    public async Task AnAnchorOpenAtTheBaseThatNeitherRegistryHoldsNow_FailsTheBalance_AndIsNotCredited()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp, One);

        // Renamed by hand: the id the base held is gone, and another holds its row.
        var pending = PendingPath(temp);
        File.WriteAllText(pending, File.ReadAllText(pending).Replace($"`{One}`", $"`{Two}`", StringComparison.Ordinal));

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.Equal([new AnchorLoss(One, AlreadyClosed: false)], report.Lost);
        Assert.Empty(report.Closed);
        Assert.Equal([Two], report.Opened.Select(opening => opening.Id));
        Assert.Equal(1, report.NetNew);
        Assert.False(report.Passed);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(AnchorSettings.DefaultPendingAnchorsPath, finding.File);
        Assert.Equal(AnchorFindingSeverity.Fatal, finding.Severity);
        Assert.Contains($"'{One}' was open where this change began and neither registry holds it now", finding.Message, StringComparison.Ordinal);

        // Both failures in one run, and the lost anchor listed apart from the closures.
        var receipt = AnchorReports.Balance(report, json: false);
        Assert.Contains("change    0 closed (0 bookkeeping), 1 lost, 1 opened (1 created, 0 disclosed); counted +1", receipt.Data);
        Assert.Contains($"  ! {One}   [lost: not credited]", receipt.Data);
        Assert.Contains("creates 1 more anchor(s)", receipt.Message, StringComparison.Ordinal);
        Assert.Contains("1 problem(s)", receipt.Message, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(AnchorReports.Balance(report, json: true).Data[0]);
        Assert.Equal(
            [(One, false)],
            json.RootElement.GetProperty("lost").EnumerateArray()
                .Select(lost => (lost.GetProperty("anchor").GetString(), lost.GetProperty("alreadyClosed").GetBoolean())));
        Assert.Empty(json.RootElement.GetProperty("closed").EnumerateArray());
    }

    /// <summary>
    /// A registry whose only finding is a warning was still read whole, so the anchors open at the base that neither
    /// registry holds now are judged lost there as anywhere: only a fatal finding hides rows.
    /// </summary>
    [Fact]
    public async Task ARegistryWithOnlyAWarning_StillHasItsLostRowsJudged()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp, One, Two, StayOpen);
        var pending = PendingPath(temp);
        var stayOpen = File.ReadAllLines(pending).Single(line => line.Contains(StayOpen, StringComparison.Ordinal));
        File.WriteAllText(pending, File.ReadAllText(pending).Replace(stayOpen, "<!-- a note -->\n" + stayOpen, StringComparison.Ordinal));
        TakeRowByHand(pending, Two);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.Contains(report.Findings, finding => finding.Severity == AnchorFindingSeverity.Warning);
        Assert.Contains(
            report.Findings,
            finding => finding.Severity == AnchorFindingSeverity.Fatal
                && finding.Message.Contains($"'{Two}' was open where this change began", StringComparison.Ordinal));
        Assert.Equal([new AnchorLoss(Two, AlreadyClosed: false)], report.Lost);
        Assert.False(report.Passed);
    }

    /// <summary>
    /// A row closed where the change began moves between the registries and is never deleted too, so one neither
    /// registry holds now fails the balance as an open one does, whether it was deleted or its id changed by hand. It
    /// was counted nowhere, so it is listed apart and charges the change nothing; unreported, the done registry lost the
    /// record of work every later read of it trusts was kept.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAnchorClosedAtTheBaseThatNeitherRegistryHoldsNow_FailsTheBalance(bool renamed)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        var done = await HoldingAsync(harness, temp, AnchorRegistryKind.Done, One);
        await harness.CommitAllAsync(temp.Path, "closed before the change", cancellationToken);

        if (renamed)
        {
            File.WriteAllText(done, File.ReadAllText(done).Replace($"`{One}`", $"`{Two}`", StringComparison.Ordinal));
        }
        else
        {
            TakeRowByHand(done, One);
        }

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([new AnchorLoss(One, AlreadyClosed: true)], report.Lost);
        Assert.Empty(report.Closed);
        Assert.Empty(report.Opened);
        Assert.Equal((0, 0, 0), (report.OpenAtBase, report.OpenNow, report.NetNew));
        Assert.False(report.Passed);

        var finding = Assert.Single(report.Findings);
        Assert.Equal((AnchorSettings.DefaultDoneAnchorsPath, AnchorFindingSeverity.Fatal), (finding.File, finding.Severity));
        Assert.Contains($"anchor '{One}' was closed where this change began and neither registry holds it now", finding.Message, StringComparison.Ordinal);

        var receipt = AnchorReports.Balance(report, json: false);
        Assert.Contains("change    0 closed (0 bookkeeping), 1 lost (1 already closed), 0 opened (0 created, 0 disclosed); counted 0", receipt.Data);
        Assert.Contains($"  ! {One}   [lost: already closed]", receipt.Data);
        Assert.Equal(AnchorExit.Findings, receipt.ExitCode);
        Assert.Equal("1 problem(s) in the registries must be fixed first", receipt.Message);

        using var json = JsonDocument.Parse(AnchorReports.Balance(report, json: true).Data[0]);
        var lost = Assert.Single(json.RootElement.GetProperty("lost").EnumerateArray());
        Assert.Equal((One, true), (lost.GetProperty("anchor").GetString(), lost.GetProperty("alreadyClosed").GetBoolean()));
    }

    /// <summary>
    /// Rows lost from both registries in one change are each a finding, said of the registry each was read from where
    /// the change began, and listed apart in id order: the open one not credited, the closed one changing no count.
    /// </summary>
    [Fact]
    public async Task RowsLostFromBothRegistries_AreEachAFinding_OfTheRegistryEachWasReadFrom()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One, Two);
        var done = await HoldingAsync(harness, temp, AnchorRegistryKind.Done, Two);
        await harness.CommitAllAsync(temp.Path, "closed before the change", cancellationToken);

        TakeRowByHand(PendingPath(temp), One);
        TakeRowByHand(done, Two);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([new AnchorLoss(One, AlreadyClosed: false), new AnchorLoss(Two, AlreadyClosed: true)], report.Lost);
        Assert.Equal((1, 0, 0), (report.OpenAtBase, report.OpenNow, report.NetNew));
        Assert.False(report.Passed);
        Assert.Equal(
            [
                (AnchorSettings.DefaultDoneAnchorsPath, AnchorFindingSeverity.Fatal, $"anchor '{Two}' was closed where this change began"),
                (AnchorSettings.DefaultPendingAnchorsPath, AnchorFindingSeverity.Fatal, $"anchor '{One}' was open where this change began"),
            ],
            report.Findings.Select(finding => (finding.File, finding.Severity, finding.Message[..finding.Message.IndexOf(" and ", StringComparison.Ordinal)])));

        var receipt = AnchorReports.Balance(report, json: false);
        Assert.Contains("change    0 closed (0 bookkeeping), 2 lost (1 already closed), 0 opened (0 created, 0 disclosed); counted 0", receipt.Data);
        Assert.Equal(
            [$"  ! {One}   [lost: not credited]", $"  ! {Two}   [lost: already closed]"],
            receipt.Data.Where(line => line.StartsWith("  ! ", StringComparison.Ordinal)));
        Assert.Equal("2 problem(s) in the registries must be fixed first", receipt.Message);

        using var json = JsonDocument.Parse(AnchorReports.Balance(report, json: true).Data[0]);
        Assert.Equal(
            [(One, false), (Two, true)],
            json.RootElement.GetProperty("lost").EnumerateArray()
                .Select(lost => (lost.GetProperty("anchor").GetString(), lost.GetProperty("alreadyClosed").GetBoolean())));
    }

    /// <summary>
    /// A closed row lost from the pending registry it was misfiled in where the change began is said of that registry,
    /// where its row was: the done registry never held it.
    /// </summary>
    [Fact]
    public async Task AClosedRowLostFromPending_IsAFindingOfPending()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` | P1 | ✅ CLOSED | t | - | - |\n");
        await harness.CommitAllAsync(temp.Path, "misfiled before the change", cancellationToken);
        TakeRowByHand(PendingPath(temp), One);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([new AnchorLoss(One, AlreadyClosed: true)], report.Lost);
        var finding = Assert.Single(report.Findings);
        Assert.Equal((AnchorSettings.DefaultPendingAnchorsPath, AnchorFindingSeverity.Fatal), (finding.File, finding.Severity));
        Assert.Contains($"anchor '{One}' was closed where this change began", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row whose Anchor cell named no id, or more than one, read as the cell's text or as the first id it named, so
    /// repairing that cell - as --lint asks - changes the id it reads as, which cannot be told from a loss by id: noted,
    /// whether it was open or closed, rather than failing the change repairing it. An open one still leaves the open
    /// count uncredited, and its repaired id is counted as opened. A cell naming one id, however often and backticked or
    /// not, keeps its id through a repair, so its row rewritten to another id was lost.
    /// </summary>
    [Theory]
    [InlineData("an anchor with no id", false, AnchorFindingSeverity.Note)]
    [InlineData("an anchor with no id", true, AnchorFindingSeverity.Note)]
    [InlineData($"`{One}` and `{Two}`", false, AnchorFindingSeverity.Note)]
    [InlineData(One, false, AnchorFindingSeverity.Fatal)]
    [InlineData($"~~`{One}`~~ `{One}`", false, AnchorFindingSeverity.Fatal)]
    public async Task ARowRewrittenToAnotherId_IsNoted_WhereItsAnchorCellNamedNoOneId(string anchorCell, bool open, AnchorFindingSeverity severity)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp);
        var (path, status) = open ? (PendingPath(temp), "🟠 OPEN") : (DonePath(temp), "✅ CLOSED");
        File.AppendAllText(path, $"| {anchorCell} | P1 | {status} | t | - | - |\n");
        await harness.CommitAllAsync(temp.Path, "a row --lint refuses", cancellationToken);

        TakeRowByHand(path, anchorCell);
        File.AppendAllText(path, $"| `{Repaired}` | P1 | {status} | t | - | - |\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal(!open, Assert.Single(report.Lost).AlreadyClosed);
        Assert.Equal(open ? [Repaired] : [], report.Opened.Select(opening => opening.Id));
        Assert.Equal(open ? 1 : 0, report.NetNew);
        Assert.Equal(!open && severity == AnchorFindingSeverity.Note, report.Passed);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(
            (open ? AnchorSettings.DefaultPendingAnchorsPath : AnchorSettings.DefaultDoneAnchorsPath, severity),
            (finding.File, finding.Severity));
        Assert.Contains(
            severity == AnchorFindingSeverity.Note
                ? "did not name one id in its Anchor cell"
                : "neither registry holds it now",
            finding.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A closure whose Trigger opens with the bookkeeping pair that is not read as one - the setting is off, or emphasis
    /// stands between the marks - is credited as work, as it reads; and the balance says so, failing nothing, since the
    /// writer may have meant it as bookkeeping.
    /// </summary>
    [Theory]
    [InlineData(false, "✅🧾 **CLOSED** 2026-10-01, mark repaired", "anchors.triggerCarriesVerdict is not set")]
    [InlineData(true, "✅ **🧾 CLOSED** 2026-10-01, mark repaired", "emphasis stands between the two marks")]
    public async Task AnUnreadBookkeepingPair_IsNoted_AndTheClosureCountsAsWork(bool triggerCarriesVerdict, string trigger, string why)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, triggerCarriesVerdict, One);

        await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed", Trigger = trigger }, dryRun: false, cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([new AnchorClosing(One, Bookkeeping: false)], report.Closed);
        Assert.Equal(-1, report.NetNew);
        Assert.True(report.Passed);

        var note = Assert.Single(report.Findings);
        Assert.Equal((AnchorSettings.DefaultDoneAnchorsPath, AnchorFindingSeverity.Note), (note.File, note.Severity));
        Assert.Contains($"anchor '{One}' has a Trigger opening with the closed mark and then the bookkeeping mark", note.Message, StringComparison.Ordinal);
        Assert.Contains(why, note.Message, StringComparison.Ordinal);

        var receipt = AnchorReports.Balance(report, json: false);
        Assert.Contains($"note      {note.File}:{note.LineNumber}   {note.Message}", receipt.Data);
        Assert.DoesNotContain("problems", receipt.Data);
        Assert.Equal(0, receipt.ExitCode);

        using var json = JsonDocument.Parse(AnchorReports.Balance(report, json: true).Data[0]);
        Assert.Equal("note", Assert.Single(json.RootElement.GetProperty("findings").EnumerateArray()).GetProperty("severity").GetString());
    }

    /// <summary>
    /// A registry malformed where the change began hid the rows it could not read there, so they count as absent there:
    /// noted, so a deleted row that went unseen, or a repaired one read as new, can be told, and not failed, since no
    /// change can repair the history.
    /// </summary>
    [Fact]
    public async Task ARegistryMalformedWhereTheChangeBegan_IsNoted_NotFailed()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One, Two);
        var pending = PendingPath(temp);
        var row = TakeRowByHand(pending, One);
        File.AppendAllText(pending, $"\nA paragraph.\n\n{row}\n");
        await harness.CommitAllAsync(temp.Path, "a row stranded outside the table", cancellationToken);

        // The stranded row deleted: nothing that counted there is gone.
        File.WriteAllText(pending, File.ReadAllText(pending).Replace($"\nA paragraph.\n\n{row}\n", string.Empty, StringComparison.Ordinal));

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken);

        Assert.Equal([AnchorSettings.DefaultPendingAnchorsPath], report.MalformedAtBase);
        Assert.Empty(report.Findings);
        Assert.Empty(report.Lost);
        Assert.True(report.Passed);
        Assert.Contains(
            $"note      {AnchorSettings.DefaultPendingAnchorsPath} was malformed at HEAD, so a row it held that could not be read was not counted there",
            AnchorReports.Balance(report, json: false).Data);

        using var json = JsonDocument.Parse(AnchorReports.Balance(report, json: true).Data[0]);
        Assert.Equal(
            [AnchorSettings.DefaultPendingAnchorsPath],
            json.RootElement.GetProperty("malformedAtBase").EnumerateArray().Select(path => path.GetString()));
    }

    /// <summary>
    /// During an unfinished merge the working tree already holds what the merge brings in, so the change is measured as
    /// the commit finishing it would be. Measured from where HEAD alone left the base, the anchors the base opened after
    /// were counted as the change's.
    /// </summary>
    [Fact]
    public async Task AnUnfinishedMergeOfTheBase_IsMeasuredAsTheCommitFinishingItWouldBe()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One, StayOpen);
        var registry = harness.AnchorRegistryService;

        await harness.RunGitAsync(temp.Path, ["branch", "upstream"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "-b", "work"], cancellationToken);
        await registry.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "the change", cancellationToken);

        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "upstream"], cancellationToken);
        await registry.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);
        await registry.WriteAsync(temp.Path, Anchor(NewDebt), dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "the base moves on", cancellationToken);
        var upstream = (await harness.GitClient.ResolveCommitAsync(temp.Path, "upstream", cancellationToken))!;

        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "work"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["merge", "-q", "--no-commit", "--no-ff", "upstream"], cancellationToken);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, "upstream", cancellationToken);

        Assert.Equal([upstream], report.Merging);
        Assert.Equal(upstream, report.Commit);
        Assert.False(report.BaseMovedOn);
        Assert.Equal([new AnchorClosing(One, Bookkeeping: false)], report.Closed);
        Assert.Empty(report.Opened);
        Assert.Equal(-1, report.NetNew);
        Assert.True(report.Passed);
        Assert.Contains(
            $"note      HEAD is merging {ReportText.Commit(upstream)}, so the change is measured as the commit finishing that merge would be",
            AnchorReports.Balance(report, json: false).Data);

        using var json = JsonDocument.Parse(AnchorReports.Balance(report, json: true).Data[0]);
        Assert.Equal([upstream], json.RootElement.GetProperty("merging").EnumerateArray().Select(commit => commit.GetString()));
    }

    /// <summary>
    /// A pull request's merge commit checked out alone, as CI checks one out, with the branch it merges into fetched alone
    /// too: the merge commit names that branch's tip as a parent, so the change is measured from there, and the anchors
    /// the branch opened after the change left it are not counted as the change's.
    /// </summary>
    [Fact]
    public async Task ABalanceOnAMergeCommitCheckedOutAlone_IsMeasuredFromTheTipItMergesInto()
    {
        using var origin = new TempDirectory();
        using var clone = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(origin, One, StayOpen);
        var registry = harness.AnchorRegistryService;

        await harness.RunGitAsync(origin.Path, ["branch", "-M", "main"], cancellationToken);
        await harness.RunGitAsync(origin.Path, ["checkout", "-q", "-b", "change"], cancellationToken);
        await registry.SetAsync(origin.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await harness.CommitAllAsync(origin.Path, "the change", cancellationToken);
        await harness.RunGitAsync(origin.Path, ["checkout", "-q", "main"], cancellationToken);
        await registry.WriteAsync(origin.Path, Anchor(Two), dryRun: false, cancellationToken);
        await harness.CommitAllAsync(origin.Path, "main moves on", cancellationToken);
        var into = (await harness.GitClient.ResolveCommitAsync(origin.Path, "main", cancellationToken))!;
        await harness.RunGitAsync(origin.Path, ["checkout", "-q", "-b", "merged"], cancellationToken);
        await harness.RunGitAsync(origin.Path, ["merge", "-q", "--no-ff", "-m", "merge the change", "change"], cancellationToken);

        // --depth needs a URL: a clone from a plain path copies the whole history and ignores it.
        var url = new Uri(origin.Path).AbsoluteUri;
        await harness.RunGitAsync(clone.Path, ["clone", "-q", "--depth", "1", "--branch", "merged", url, "."], cancellationToken);
        await harness.RunGitAsync(clone.Path, ["fetch", "-q", "--depth", "1", "origin", "main:refs/remotes/origin/main"], cancellationToken);
        Assert.False((await harness.GitClient.RunAsync(clone.Path, ["merge-base", "origin/main", "HEAD"], cancellationToken: cancellationToken)).Succeeded);

        var report = await harness.AnchorBalanceService.CheckAsync(clone.Path, "origin/main", cancellationToken);

        Assert.Equal((into, into), (report.BaseCommit, report.Commit));
        Assert.False(report.BaseMovedOn);
        Assert.Equal([new AnchorClosing(One, Bookkeeping: false)], report.Closed);
        Assert.Empty(report.Opened);
        Assert.True(report.Passed);
    }

    /// <summary>
    /// A shallow clone holds no commit before where its history was cut, so a base there names none: said so, with how
    /// to fetch the rest, rather than as a name that names nothing.
    /// </summary>
    [Fact]
    public async Task ABaseBeyondAShallowClonesHistory_AsksForTheRest()
    {
        using var origin = new TempDirectory();
        using var clone = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(origin, One);
        await harness.CommitAllAsync(origin.Path, "one more", cancellationToken);

        await harness.RunGitAsync(clone.Path, ["clone", "-q", "--depth", "1", new Uri(origin.Path).AbsoluteUri, "."], cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => harness.AnchorBalanceService.CheckAsync(clone.Path, "HEAD~1", cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.StartsWith("'HEAD~1' names no commit this clone holds", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("git fetch --unshallow", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry malformed now is its own finding: an anchor whose row it no longer reads is not reported lost
    /// besides, since the row is there, where nothing reads it - whichever registry holds it, and whether it was open
    /// or closed where the change began.
    /// </summary>
    [Theory]
    [InlineData(AnchorRegistryKind.Pending, false)]
    [InlineData(AnchorRegistryKind.Done, false)]
    [InlineData(AnchorRegistryKind.Done, true)]
    public async Task ARegistryMalformedNow_IsItsOwnFinding_NotEveryAnchorItHidesLost(AnchorRegistryKind kind, bool closedAtBase)
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp, One);
        var path = await HoldingAsync(harness, temp, kind, One);

        if (closedAtBase)
        {
            await harness.CommitAllAsync(temp.Path, "closed before the change", TestContext.Current.CancellationToken);
        }

        // Stranded below a paragraph, outside the table.
        var row = TakeRowByHand(path, One);
        File.AppendAllText(path, $"\nA paragraph.\n\n{row}\n");

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Contains("outside any table", Assert.Single(report.Findings).Message, StringComparison.Ordinal);
        Assert.Empty(report.Lost);
        Assert.DoesNotContain(AnchorReports.Balance(report, json: false).Data, line => line.StartsWith("  ! ", StringComparison.Ordinal));
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

        await harness.RunGitAsync(temp.Path, ["branch", "upstream"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "-b", "work"], cancellationToken);
        await registry.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await registry.WriteAsync(temp.Path, Anchor(StayOpen), dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "the change", cancellationToken);

        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "upstream"], cancellationToken);
        await registry.SetAsync(temp.Path, new AnchorSetRequest(Two) { Status = "closed" }, dryRun: false, cancellationToken);
        await registry.WriteAsync(temp.Path, Anchor(NewDebt), dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "the base moves on", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "work"], cancellationToken);

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
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "--detach"], cancellationToken);
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

        await harness.RunGitAsync(temp.Path, ["branch", "behind"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "-b", "upstream"], cancellationToken);
        await registry.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken);
        await harness.CommitAllAsync(temp.Path, "the base moves on", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "behind"], cancellationToken);
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
    /// refused as a precondition, as check-anchor-citations refuses it, whether a base is named or not.
    /// </summary>
    [Fact]
    public async Task AHeadThatNamesNoCommit_IsRefused_WhateverTheBase()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareCommittedAsync(temp, One);
        var committed = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "--orphan", "unborn"], cancellationToken);

        foreach (var baseReference in new[] { null, committed })
        {
            var refusal = await Assert.ThrowsAsync<HarnessException>(
                () => harness.AnchorBalanceService.CheckAsync(temp.Path, baseReference, cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.StartsWith("HEAD names no commit yet", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("there is no point this change began from to compare against. Commit first.", refusal.Message, StringComparison.Ordinal);
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
            Base: "upstream",
            BaseCommit: baseCommit,
            Commit: movedOn ? new string('b', 40) : baseCommit,
            Merging: [],
            OpenAtBase: 0,
            OpenNow: 0,
            Closed: [],
            Lost: [],
            Opened: [],
            MissingAtBase: [AnchorSettings.DefaultDoneAnchorsPath],
            MalformedAtBase: [AnchorSettings.DefaultPendingAnchorsPath],
            Findings: []);

        var receipt = AnchorReports.Balance(report, json: false);

        Assert.Equal($"open      0 {measured}, 0 now", receipt.Data[1]);
        Assert.Contains($"note      {AnchorSettings.DefaultDoneAnchorsPath} did not exist {measured}, so it counts as empty there", receipt.Data);
        Assert.Contains(
            $"note      {AnchorSettings.DefaultPendingAnchorsPath} was malformed {measured}, so a row it held that could not be read was not counted there",
            receipt.Data);
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

        using (RegistryLockHolder.Take(harness.AnchorRegistryLock, registries, cancellationToken))
        {
            var refusal = await Assert.ThrowsAsync<HarnessException>(() => impatient.CheckAsync(temp.Path, null, cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains("has held the anchor registries", refusal.Message, StringComparison.Ordinal);
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
        var unrelated = (await harness.RunGitAsync(temp.Path, ["commit-tree", "HEAD^{tree}", "-m", "unrelated"], cancellationToken))
            .StandardOutput.Trim();

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => harness.AnchorBalanceService.CheckAsync(temp.Path, unrelated, cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.Contains("shares no history with HEAD", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry missing now is its own finding: the anchors it held are not reported lost besides, since nothing
    /// could have been read where they would be - whichever registry it is, and whether they were open or closed where
    /// the change began.
    /// </summary>
    [Theory]
    [InlineData(AnchorRegistryKind.Pending, "no pending registry", false)]
    [InlineData(AnchorRegistryKind.Done, "no done registry", false)]
    [InlineData(AnchorRegistryKind.Done, "no done registry", true)]
    public async Task ARegistryMissingNow_IsOneFinding_NotEveryAnchorItHeldLost(AnchorRegistryKind kind, string missing, bool closedAtBase)
    {
        using var temp = new TempDirectory();
        var harness = await PrepareCommittedAsync(temp, One, Two);
        var path = await HoldingAsync(harness, temp, kind, One);

        if (closedAtBase)
        {
            await harness.CommitAllAsync(temp.Path, "closed before the change", TestContext.Current.CancellationToken);
        }

        File.Delete(path);

        var report = await harness.AnchorBalanceService.CheckAsync(temp.Path, null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Contains(missing, Assert.Single(report.Findings).Message, StringComparison.Ordinal);
        Assert.Empty(report.Lost);
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
    public async Task AChangeThatCreatesMoreAnchorsThanItsWorkCloses_Fails()
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
        Assert.Equal("'no-such-branch' does not name a commit, so there is nothing to compare against.", exception.Message);
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
