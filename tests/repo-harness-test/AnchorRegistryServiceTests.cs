using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

public sealed class AnchorRegistryServiceTests
{
    private const string One = "D-AREA-TOPIC-ONE";
    private const string Two = "D-AREA-TOPIC-TWO";
    private const string Three = "D-AREA-TOPIC-THREE";

    [Fact]
    public async Task WriteAsync_AppendsAnOpenAnchorToThePendingRegistry()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var first = await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        Assert.True(first.IsNew);
        Assert.True(first.Written);
        Assert.Equal(AnchorRegistryKind.Pending, first.To.Kind);

        var text = File.ReadAllText(PendingPath(temp));
        Assert.EndsWith(
            $"| `{One}` | P1 | 🟠 OPEN | trigger for {One} | work | refs |\n| `{Two}` | P1 | 🟠 OPEN | trigger for {Two} | work | refs |\n",
            text,
            StringComparison.Ordinal);
        Assert.Empty(Rows(harness, DonePath(temp)));
    }

    [Fact]
    public async Task WriteAsync_FilesAClosedAnchorStraightIntoTheDoneRegistry()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var change = await harness.AnchorRegistryService.WriteAsync(
            temp.Path, Anchor(One, "closed"), dryRun: false, TestContext.Current.CancellationToken);

        Assert.Equal(AnchorRegistryKind.Done, change.To.Kind);
        Assert.Equal([One], Rows(harness, DonePath(temp)).Select(row => row.Id));
        Assert.Empty(Rows(harness, PendingPath(temp)));
    }

    [Fact]
    public async Task WriteAsync_RefusesAnIdThatAlreadyHasARow_InEitherRegistry()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two, "closed"), dryRun: false, cancellationToken);

        var again = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken));
        var reopened = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken));

        Assert.Equal(HarnessExit.Refused, again.ExitCode);
        Assert.Equal(HarnessExit.Refused, reopened.ExitCode);
        Assert.Contains("set-anchor", again.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("D-TWO-SEGMENTS", "P1", "open", "t")]
    [InlineData(One, "P9", "open", "t")]
    [InlineData(One, "P1", "done", "t")]
    [InlineData(One, "P1", "open", "  ")]
    [InlineData(One, "P1", "open", "\u001c")]
    [InlineData(One, "P1", "open", "\u001e\n")]
    [InlineData(One, "P1", "open", @"already \| escaped")]
    public async Task WriteAsync_RefusesAnInvalidValue_AndChangesNothing(string id, string priority, string status, string trigger)
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var before = File.ReadAllText(PendingPath(temp));

        var exception = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.WriteAsync(
            temp.Path,
            new AnchorWriteRequest(id, priority, trigger) { Status = status },
            dryRun: false,
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Equal(before, File.ReadAllText(PendingPath(temp)));
    }

    /// <summary>
    /// A Trigger is judged as it will be written: one of nothing but line breaks - a file, group or record separator
    /// among them, which .NET does not count as whitespace - would be written as an empty cell, so set-anchor
    /// refuses it as write-anchor does.
    /// </summary>
    [Theory]
    [InlineData("\u001c")]
    [InlineData("\u001e\n")]
    [InlineData(" \u2028 ")]
    public async Task SetAsync_RefusesATriggerThatWouldBeWrittenEmpty(string trigger)
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        var before = File.ReadAllText(PendingPath(temp));

        var exception = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Trigger = trigger }, dryRun: false, cancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Equal(before, File.ReadAllText(PendingPath(temp)));
    }

    [Fact]
    public async Task WriteAsync_WithADryRun_ReportsTheChange_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var before = File.ReadAllText(PendingPath(temp));

        var change = await harness.AnchorRegistryService.WriteAsync(
            temp.Path, Anchor(One), dryRun: true, TestContext.Current.CancellationToken);

        Assert.False(change.Written);
        Assert.Equal(before, File.ReadAllText(PendingPath(temp)));
    }

    [Fact]
    public async Task SetAsync_RebuildsOnlyTheCellsGiven_AndKeepsTheRestByteForByte()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` |  P1  | 🟠 OPEN | spaced  out \\| text |  work | refs |\n");

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Priority = "p0" }, dryRun: false, TestContext.Current.CancellationToken);

        Assert.EndsWith(
            $"| `{One}` | P0 | 🟠 OPEN | spaced  out \\| text |  work | refs |\n",
            File.ReadAllText(PendingPath(temp)),
            StringComparison.Ordinal);

        var field = Assert.Single(change.Fields);
        Assert.Equal(("priority", "P1", "P0"), (field.Field, field.Before, field.After));
        Assert.False(change.Moved);
    }

    [Fact]
    public async Task SetAsync_Closing_MovesTheRowToTheEndOfTheDoneRegistry()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Three, "closed"), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed", ClosingWork = "fixed" }, dryRun: false, cancellationToken);

        Assert.True(change.Moved);
        Assert.Equal((AnchorRegistryKind.Pending, AnchorRegistryKind.Done), (change.From!.Kind, change.To.Kind));
        Assert.Equal([Two], Rows(harness, PendingPath(temp)).Select(row => row.Id));

        var done = Rows(harness, DonePath(temp));
        Assert.Equal([Three, One], done.Select(row => row.Id));
        Assert.Equal(("✅ CLOSED", "fixed", $"trigger for {One}"), (done[1].Status, done[1].ClosingWork, done[1].Trigger));
    }

    [Fact]
    public async Task SetAsync_Reopening_MovesTheRowBackToPending()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One, "closed"), dryRun: false, cancellationToken);

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "open" }, dryRun: false, cancellationToken);

        Assert.True(change.Moved);
        Assert.Equal([One], Rows(harness, PendingPath(temp)).Select(row => row.Id));
        Assert.Empty(Rows(harness, DonePath(temp)));
    }

    [Theory]
    [InlineData("gated", "⏳ GATED")]
    [InlineData("disclosed", "🔵 DISCLOSED")]
    public async Task SetAsync_EveryLiveStatus_StaysInPending(string status, string cell)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = status }, dryRun: false, cancellationToken);

        Assert.False(change.Moved);
        Assert.Equal(cell, Assert.Single(Rows(harness, PendingPath(temp))).Status);
    }

    [Fact]
    public async Task SetAsync_RefusesAnIdWithNoRow_WhereItWasToldToLook()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var missing = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(Two) { Priority = "P2" }, dryRun: false, cancellationToken));
        var wrongRegistry = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Scope = AnchorScope.Done, Priority = "P2" }, dryRun: false, cancellationToken));

        Assert.Equal(HarnessExit.Refused, missing.ExitCode);
        Assert.Contains("write-anchor", missing.Message, StringComparison.Ordinal);
        Assert.Contains("the done registry", wrongRegistry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_RefusesAnIdWithTwoRows()
    {
        // Which of two rows is the real one is for a person to decide.
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` | P1 | 🟠 OPEN | one | - | - |\n");
        File.AppendAllText(DonePath(temp), $"| `{One}` | P1 | ✅ CLOSED | other | - | - |\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Priority = "P2" }, dryRun: false, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("2 rows", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_RefusesARowWhoseCellsCannotBeTold_Apart()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` | P1 | 🟠 OPEN |\n");

        var exception = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Priority = "P2" }, dryRun: false, TestContext.Current.CancellationToken));

        Assert.Contains("3 cells", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAsync_WithNothingToChange_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var exception = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One), dryRun: false, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
    }

    [Fact]
    public async Task AnInterruptedMove_LeavesTheRowInTheDestination_NeverInNeither()
    {
        // A move is two writes. A row lost between them would read, to every count, exactly like
        // an anchor that was closed; a row left in both is refused loudly on the next change.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var crashing = new AnchorRegistryService(
            harness.ContextLoader,
            harness.AnchorRegistryLocator,
            harness.AnchorRegistryLock,
            new FailingWriteFileSystem(harness.FileSystem, failOnWrite: 2));

        await Assert.ThrowsAsync<IOException>(() => crashing.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken));

        Assert.Equal([One], Rows(harness, DonePath(temp)).Select(row => row.Id));
        Assert.Equal([One], Rows(harness, PendingPath(temp)).Select(row => row.Id));

        var next = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Priority = "P0" }, dryRun: false, cancellationToken));
        Assert.Contains("2 rows", next.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChange_RefusesWhenAnotherHoldsTheLockTooLong_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var registries = await harness.AnchorRegistryLocator.LocateAsync(context, cancellationToken);
        var before = File.ReadAllText(PendingPath(temp));

        using (RegistryLockHolder.Take(harness.AnchorRegistryLock, registries, cancellationToken))
        {
            var impatient = new AnchorRegistryService(
                harness.ContextLoader,
                harness.AnchorRegistryLocator,
                new NamedMutexAnchorRegistryLock(harness.Platform, TimeSpan.FromMilliseconds(200)),
                harness.FileSystem);

            var exception = await Assert.ThrowsAsync<HarnessException>(() =>
                impatient.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken));

            Assert.Equal(HarnessExit.Refused, exception.ExitCode);
            Assert.Contains("has held the anchor registries", exception.Message, StringComparison.Ordinal);
            Assert.Equal(before, File.ReadAllText(PendingPath(temp)));
        }
    }

    /// <summary>
    /// A read takes the lock too, while it reads both registries: a change moves a row by writing one and then the other,
    /// so the two read apart could hold it in neither - an id not found - or in both, a duplicate the lint reports.
    /// Held too long, every read refuses as a change does.
    /// </summary>
    [Fact]
    public async Task AReadOfBothRegistries_TakesTheLock()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        var context = await harness.ContextLoader.LoadAsync(temp.Path, cancellationToken);
        var registries = await harness.AnchorRegistryLocator.LocateAsync(context, cancellationToken);
        var impatient = new AnchorRegistryService(
            harness.ContextLoader,
            harness.AnchorRegistryLocator,
            new NamedMutexAnchorRegistryLock(harness.Platform, TimeSpan.FromMilliseconds(200)),
            harness.FileSystem);

        using (RegistryLockHolder.Take(harness.AnchorRegistryLock, registries, cancellationToken))
        {
            Func<Task>[] reads =
            [
                () => impatient.LintAsync(temp.Path, cancellationToken),
                () => impatient.ListAsync(temp.Path, new AnchorListFilter(), cancellationToken),
                () => impatient.ReadAsync(temp.Path, [One], AnchorScope.All, cancellationToken),
                () => impatient.DifferencesAsync(temp.Path, [Declared(One, "open", "work")], cancellationToken),
            ];

            foreach (var read in reads)
            {
                var refusal = await Assert.ThrowsAsync<HarnessException>(read);

                Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
                Assert.Contains("has held the anchor registries", refusal.Message, StringComparison.Ordinal);
            }
        }

        Assert.Empty(await impatient.LintAsync(temp.Path, cancellationToken));
        Assert.Equal([One], (await impatient.ListAsync(temp.Path, new AnchorListFilter(), cancellationToken)).Select(entry => entry.Row.Id));
    }

    /// <summary>
    /// Every read of the registries reads both files under one hold of the lock: held once per file, a row a change moved
    /// between the two holds would be read in neither, or in both.
    /// </summary>
    [Fact]
    public async Task EveryRead_TakesTheLockOnce_ForBothRegistries()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        var counting = new CountingLock(harness.AnchorRegistryLock);
        var service = new AnchorRegistryService(harness.ContextLoader, harness.AnchorRegistryLocator, counting, harness.FileSystem);
        var balance = new AnchorBalanceService(harness.ContextLoader, harness.AnchorRegistryLocator, counting, harness.GitClient, harness.FileSystem);

        Func<Task>[] reads =
        [
            () => service.LintAsync(temp.Path, cancellationToken),
            () => service.ListAsync(temp.Path, new AnchorListFilter(), cancellationToken),
            () => service.ReadAsync(temp.Path, [One], AnchorScope.All, cancellationToken),
            () => service.DifferencesAsync(temp.Path, [Declared(One, "open", "work")], cancellationToken),
            () => balance.CheckAsync(temp.Path, null, cancellationToken),
        ];

        foreach (var read in reads)
        {
            var before = counting.Taken;
            await read();
            Assert.Equal(before + 1, counting.Taken);
        }
    }

    /// <summary>
    /// A directory where a registry's file belongs is refused, naming it, by every reader and by init. Taken for no
    /// registry, it sent the reader to init, which then failed to write over it with an internal error.
    /// </summary>
    [Fact]
    public async Task ARegistryPathHoldingADirectory_IsRefused_NotReadAsMissing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        File.Delete(DonePath(temp));
        Directory.CreateDirectory(DonePath(temp));

        Func<Task>[] reads =
        [
            () => harness.AnchorRegistryService.LintAsync(temp.Path, cancellationToken),
            () => harness.AnchorRegistryService.ListAsync(temp.Path, new AnchorListFilter(), cancellationToken),
            () => harness.AnchorBalanceService.CheckAsync(temp.Path, null, cancellationToken),
            () => harness.InitService.InitializeAsync(temp.Path, cancellationToken),
        ];

        foreach (var read in reads)
        {
            var refusal = await Assert.ThrowsAsync<HarnessException>(read);

            Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
            Assert.StartsWith($"'{AnchorSettings.DefaultDoneAnchorsPath}', where anchors.doneAnchorsPath puts the done registry, is a directory", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The lint notes a closed row whose Trigger opens with the bookkeeping pair that is not read as one, and still passes:
    /// the row is sound as it stands, and only its writer can say what they meant.
    /// </summary>
    [Fact]
    public async Task TheLint_NotesAnUnreadBookkeepingPair_AndStillPasses()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(
            temp.Path, new AnchorWriteRequest(One, "P1", "✅🧾 **CLOSED**, mark repaired") { Status = "closed" }, dryRun: false, cancellationToken);

        var findings = await harness.AnchorRegistryService.LintAsync(temp.Path, cancellationToken);

        var note = Assert.Single(findings);
        Assert.Equal((AnchorSettings.DefaultDoneAnchorsPath, AnchorFindingSeverity.Note), (note.File, note.Severity));
        Assert.Contains("anchors.triggerCarriesVerdict is not set", note.Message, StringComparison.Ordinal);

        var outcome = AnchorReports.Lint(findings, json: false);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains($"{note.File}:{note.LineNumber}   note: {note.Message}", outcome.Data);
    }

    [Fact]
    public async Task ConcurrentChanges_LoseNoAnchor()
    {
        // Without the lock, each writer reads the registry before the others write, and the last
        // write silently discards every other anchor.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var ids = Enumerable.Range(1, 8).Select(index => $"D-AREA-CONCURRENT-WRITER{index}").ToList();

        await Task.WhenAll(ids.Select(id => Task.Run(
            () => harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(id), dryRun: false, cancellationToken),
            cancellationToken)));

        Assert.Equal(ids.Order(StringComparer.Ordinal), Rows(harness, PendingPath(temp)).Select(row => row.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_AnswersEveryId_InTheOrderAsked()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two, "closed"), dryRun: false, cancellationToken);

        var lookup = await harness.AnchorRegistryService.ReadAsync(temp.Path, [Two, One], AnchorScope.All, cancellationToken);

        Assert.Equal([Two, One], lookup.Results.Select(result => result.Id));
        Assert.Empty(lookup.Missing);
        Assert.Equal(AnchorRegistryKind.Done, Assert.Single(lookup.Results[0].Matches).Registry.Kind);
        Assert.Equal(AnchorRegistryKind.Pending, Assert.Single(lookup.Results[1].Matches).Registry.Kind);
    }

    [Fact]
    public async Task ReadAsync_MatchesExactly_AndListsWhatIsMissing_WithIdsBeginningTheSameWay()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var lookup = await harness.AnchorRegistryService.ReadAsync(
            temp.Path, [One.ToLowerInvariant(), "D-AREA-OTHER-THING", "D-NOWHERE-AT-ALL"], AnchorScope.All, cancellationToken);

        Assert.Equal(3, lookup.Missing.Count);
        Assert.Equal([One], lookup.Missing[1].SameNamespace);
        Assert.Empty(lookup.Missing[2].SameNamespace);
    }

    [Fact]
    public async Task ReadAsync_ShowsEveryRowOfADuplicatedId()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), $"| `{One}` | P1 | 🟠 OPEN | one | - | - |\n| `{One}` | P2 | 🟠 OPEN | two | - | - |\n");

        var lookup = await harness.AnchorRegistryService.ReadAsync(temp.Path, [One], AnchorScope.All, TestContext.Current.CancellationToken);

        Assert.Equal(2, Assert.Single(lookup.Results).Matches.Count);
    }

    [Fact]
    public async Task ListAsync_FiltersByRegistry_Priority_AndStatus()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, new AnchorWriteRequest(Two, "P3", "t"), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Three, "closed"), dryRun: false, cancellationToken);

        async Task<IEnumerable<string>> ListAsync(AnchorListFilter filter)
            => (await harness.AnchorRegistryService.ListAsync(temp.Path, filter, cancellationToken)).Select(entry => entry.Row.Id);

        Assert.Equal([One, Two, Three], await ListAsync(new AnchorListFilter()));
        Assert.Equal([Three], await ListAsync(new AnchorListFilter { Scope = AnchorScope.Done }));
        Assert.Equal([Two], await ListAsync(new AnchorListFilter { Bands = ["p3"] }));
        Assert.Equal([One, Two], await ListAsync(new AnchorListFilter { OnlyOpen = true }));
        Assert.Equal([Three], await ListAsync(new AnchorListFilter { OnlyClosed = true }));

        var both = await Assert.ThrowsAsync<HarnessException>(() => ListAsync(new AnchorListFilter { OnlyOpen = true, OnlyClosed = true }));
        Assert.Equal(HarnessExit.UsageError, both.ExitCode);
    }

    [Fact]
    public async Task LintAsync_IsClean_ForRegistriesTheCommandsWrote()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two, "closed"), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.SetAsync(temp.Path, new AnchorSetRequest(One) { Status = "gated" }, dryRun: false, cancellationToken);

        Assert.Empty(await harness.AnchorRegistryService.LintAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task LintAsync_ReportsEveryKindOfProblem()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), string.Join("\n",
            "| `D-LINT-CLOSED-HERE` | P1 | ✅ CLOSED | closed in pending | - | - |",
            "| `D-LINT-BAD-PRIORITY` | P9 | 🟠 OPEN | t | - | - |",
            "| `D-LINT-BAD-STATUS` | P1 | ORANGE | t | - | - |",
            "| `D-LINT-EMPTY-TRIGGER` | P1 | 🟠 OPEN |  | - | - |",
            "| `D-LINT-SHORT-ROW` | P1 |",
            "| D-LINT-NO-BACKTICKS | P1 | 🟠 OPEN | t | - | - |",
            "| `D-LINT-TWICE-OVER` | P1 | 🟠 OPEN | t | - | - |") + "\n");
        File.AppendAllText(DonePath(temp), string.Join("\n",
            "| `D-LINT-OPEN-HERE` | P1 | 🟠 OPEN | open in done | - | - |",
            "| `D-LINT-TWICE-OVER` | P1 | ✅ CLOSED | t | - | - |") + "\n");

        var findings = await harness.AnchorRegistryService.LintAsync(temp.Path, TestContext.Current.CancellationToken);

        string[] expected =
        [
            "closed anchor 'D-LINT-CLOSED-HERE' is in the pending registry",
            "priority 'P9'",
            "status 'ORANGE'",
            "Trigger cell is empty",
            "2 cells, not 6",
            "not one id in backticks",
            "'D-LINT-TWICE-OVER' has 2 rows",
            "live anchor 'D-LINT-OPEN-HERE' is in the done registry",
        ];

        foreach (var text in expected)
        {
            Assert.Contains(findings, finding => finding.Message.Contains(text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ATrackedRegistry_IsChangedInTheWorktreeTheCommandRunsIn()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(repository);
        await harness.CommitAllAsync(repository.Path, "harness", cancellationToken);

        var worktree = elsewhere.Combine("wt");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", worktree], cancellationToken);

        await harness.AnchorRegistryService.WriteAsync(worktree, Anchor(One), dryRun: false, cancellationToken);

        Assert.Contains(One, File.ReadAllText(Path.Combine(worktree, ".plans", "_deferred-anchor-registry.md")), StringComparison.Ordinal);
        Assert.DoesNotContain(One, File.ReadAllText(PendingPath(repository)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIgnoredRegistry_IsChangedInTheMainCheckout_FromAWorktree()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(repository.Path, cancellationToken);

        repository.WriteFile(".gitignore", "local/\n");
        harness.WriteConfig(repository.Path, new HarnessConfig
        {
            Anchors = new AnchorSettings { PendingAnchorsPath = "local/pending.md", DoneAnchorsPath = "local/done.md" },
        });

        await harness.InitService.InitializeAsync(repository.Path, cancellationToken);
        await harness.CommitAllAsync(repository.Path, "harness", cancellationToken);

        var worktree = elsewhere.Combine("wt");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", worktree], cancellationToken);

        var change = await harness.AnchorRegistryService.WriteAsync(worktree, Anchor(One), dryRun: false, cancellationToken);

        Assert.True(change.To.IsIgnored);
        Assert.Contains(One, File.ReadAllText(repository.Combine("local", "pending.md")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(worktree, "local", "pending.md")));
    }

    [Fact]
    public async Task AMissingRegistry_IsReportedAsNotInitialised()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.Delete(DonePath(temp));

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            harness.AnchorRegistryService.ListAsync(temp.Path, new AnchorListFilter(), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.NotInitialized, exception.ExitCode);
        Assert.Contains("dssharness init", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no anchor table")]
    [InlineData("a second anchor table")]
    [InlineData("a row outside the table")]
    [InlineData("a row in another table")]
    public async Task AMalformedRegistry_IsNeitherReadNorWritten(string problem)
    {
        // Read around the problem, a registry could miscount an anchor or give it a second row, and
        // nobody would be told. Reporting it is --lint's job.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var registry = File.ReadAllText(PendingPath(temp));
        var malformed = problem switch
        {
            "no anchor table" => "# Someone removed the table\n",
            "a second anchor table" => registry
                + $"\nMore anchors:\n\n{AnchorRegistryDocument.TableHeader}\n{AnchorRegistryDocument.SeparatorRow}\n| `{Two}` | P1 | 🟠 OPEN | t | - | - |\n",
            "a row outside the table" => registry + $"\nA paragraph.\n\n| `{Two}` | P1 | 🟠 OPEN | stray | - | - |\n",
            _ => registry + $"\n| Id | Note |\n|---|---|\n| `{Two}` | kept elsewhere |\n",
        };

        File.WriteAllText(PendingPath(temp), malformed);

        Func<Task>[] commands =
        [
            () => harness.AnchorRegistryService.ReadAsync(temp.Path, [One], AnchorScope.All, cancellationToken),
            () => harness.AnchorRegistryService.ListAsync(temp.Path, new AnchorListFilter(), cancellationToken),
            () => harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken),
            () => harness.AnchorRegistryService.SetAsync(temp.Path, new AnchorSetRequest(One) { Priority = "P0" }, dryRun: false, cancellationToken),
        ];

        foreach (var command in commands)
        {
            var exception = await Assert.ThrowsAsync<HarnessException>(command);

            Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
            Assert.Contains("read-anchors --lint", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(malformed, File.ReadAllText(PendingPath(temp)));
    }

    [Fact]
    public async Task SetAsync_ChangesAnExistingIdTheMintingRuleWouldRefuse()
    {
        // Ids already in a registry are never re-checked against the minting rule: a row nobody could
        // maintain is worse than an old spelling, and renaming an id orphans every citation of it.
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        File.AppendAllText(PendingPath(temp), "| `D-OLD-NAME` | P1 | 🟠 OPEN | an id from before the rule | - | - |\n");

        var change = await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest("D-OLD-NAME") { Status = "closed" }, dryRun: false, TestContext.Current.CancellationToken);

        Assert.True(change.Moved);
        Assert.Empty(Rows(harness, PendingPath(temp)));

        var row = Assert.Single(Rows(harness, DonePath(temp)));
        Assert.Equal(("D-OLD-NAME", "✅ CLOSED"), (row.Id, row.Status));
    }

    /// <summary>
    /// Where a repository holds a Trigger to its row's verdict, a row whose two cells disagree is refused
    /// as written - by write-anchor either way, and by set-anchor closing a row whose Trigger does not say
    /// so - and nothing is written; one that agrees is written as ever.
    /// </summary>
    [Fact]
    public async Task WhereATriggerCarriesTheVerdict_ARowStatingTwo_IsRefused()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, triggerCarriesVerdict: true);
        var service = harness.AnchorRegistryService;

        var openButClosed = await Assert.ThrowsAsync<HarnessException>(() => service.WriteAsync(
            temp.Path, new AnchorWriteRequest(One, "P1", "✅ **CLOSED** - fixed"), dryRun: false, cancellationToken));
        var closedButOpen = await Assert.ThrowsAsync<HarnessException>(() => service.WriteAsync(
            temp.Path, new AnchorWriteRequest(One, "P1", "tokens expire mid-request") { Status = "closed" }, dryRun: false, cancellationToken));

        Assert.Equal(HarnessExit.UsageError, openButClosed.ExitCode);
        Assert.Contains("the Trigger opens with the closed mark", openButClosed.Message, StringComparison.Ordinal);
        Assert.Contains("the Status reads closed", closedButOpen.Message, StringComparison.Ordinal);
        Assert.Empty(Rows(harness, PendingPath(temp)));
        Assert.Empty(Rows(harness, DonePath(temp)));

        await service.WriteAsync(temp.Path, new AnchorWriteRequest(One, "P1", "tokens expire mid-request"), dryRun: false, cancellationToken);

        var closing = await Assert.ThrowsAsync<HarnessException>(() => service.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed" }, dryRun: false, cancellationToken));

        Assert.Contains("the Status reads closed", closing.Message, StringComparison.Ordinal);
        Assert.Equal("🟠 OPEN", Assert.Single(Rows(harness, PendingPath(temp))).Status);

        await service.SetAsync(
            temp.Path, new AnchorSetRequest(One) { Status = "closed", Trigger = "✅ **CLOSED 2026-09-23** - refreshed before expiry" }, dryRun: false, cancellationToken);

        Assert.Equal("✅ CLOSED", Assert.Single(Rows(harness, DonePath(temp))).Status);
        Assert.Empty(await service.LintAsync(temp.Path, cancellationToken));
    }

    /// <summary>
    /// By default the Status cell is the only verdict: a Trigger opening with the closed mark is prose, and
    /// written as given. Held to the verdict, the same row written by hand is a finding of the lint.
    /// </summary>
    [Fact]
    public async Task ByDefault_OnlyTheStatusIsAVerdict_AndHeldToIt_TheLintReportsATriggerThatDisagrees()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var loose = await PrepareAsync(temp);

        await loose.AnchorRegistryService.WriteAsync(
            temp.Path, new AnchorWriteRequest(One, "P1", "✅ **CLOSED** - fixed"), dryRun: false, cancellationToken);

        Assert.Empty(await loose.AnchorRegistryService.LintAsync(temp.Path, cancellationToken));

        using var held = new TempDirectory();
        var strict = await PrepareAsync(held, triggerCarriesVerdict: true);
        File.AppendAllText(PendingPath(held), $"| `{One}` | P1 | 🟠 OPEN | ✅ **CLOSED** - fixed | work | refs |\n");

        var finding = Assert.Single(await strict.AnchorRegistryService.LintAsync(held.Path, cancellationToken));
        Assert.Contains("the Trigger opens with the closed mark", finding.Message, StringComparison.Ordinal);
        Assert.Contains("anchors.triggerCarriesVerdict", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>A cell's runs and tabs are written as given, and read back as given.</summary>
    [Fact]
    public async Task ACellsRuns_AreWrittenAndReadBackAsGiven()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        await harness.AnchorRegistryService.WriteAsync(
            temp.Path, new AnchorWriteRequest(One, "P1", "inputs  : held still\t4  +  38"), dryRun: false, cancellationToken);

        Assert.Contains("| inputs  : held still\t4  +  38 |", File.ReadAllText(PendingPath(temp)), StringComparison.Ordinal);
        Assert.Equal("inputs  : held still\t4  +  38", Assert.Single(Rows(harness, PendingPath(temp))).Trigger);
    }

    /// <summary>
    /// A batch writes a new row named new, changes an existing row in the cells that differ only - a cell already as declared
    /// keeps its stored bytes, runs of spaces and all - and leaves a row already as declared alone.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WritesNewRows_ChangesOnlyWhatDiffers_AndLeavesRowsAlreadyAsDeclared()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two) with { ClosingWork = "keep  these   runs" }, dryRun: false, cancellationToken);

        var batch = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path,
            new AnchorBatchRequest(
            [
                Declared(One, "open", "work"),
                Declared(Two, "gated", "keep  these   runs"),
                Declared(Three, "open", "new work") with { Priority = "P2" },
            ])
            { New = [Three] },
            AnchorBatchMode.Apply,
            cancellationToken);

        Assert.True(batch.Succeeded, string.Join("; ", batch.Problems));
        Assert.Equal([AnchorRowAction.AlreadyIn, AnchorRowAction.Changed, AnchorRowAction.New], batch.Rows.Select(row => row.Action));
        Assert.Equal(["status"], batch.Rows[1].Change.Fields.Select(field => field.Field));

        var rows = Rows(harness, PendingPath(temp));
        Assert.Equal([One, Two, Three], rows.Select(row => row.Id));
        Assert.Contains($"| `{Two}` | P1 | {AnchorStatus.Render(AnchorState.Gated)} | trigger for {Two} | keep  these   runs | refs |", File.ReadAllText(PendingPath(temp)), StringComparison.Ordinal);
        Assert.Equal("P2", rows[2].Priority);
    }

    /// <summary>A declared row whose status now belongs in the other registry is moved there, as set-anchor moves one.</summary>
    [Fact]
    public async Task ApplyAsync_MovesARowWhoseStatusBelongsInTheOtherRegistry()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var batch = await harness.AnchorRegistryService.ApplyAsync(temp.Path, new AnchorBatchRequest([Declared(One, "closed", "work")]), AnchorBatchMode.Apply, cancellationToken);

        Assert.True(batch.Succeeded, string.Join("; ", batch.Problems));
        Assert.True(batch.Rows[0].Change.Moved);
        Assert.Empty(Rows(harness, PendingPath(temp)));
        Assert.Equal([One], Rows(harness, DonePath(temp)).Select(row => row.Id));
    }

    /// <summary>
    /// Every row is checked before any is written, and every one refused is named: one bad row writes none of them, and a
    /// plan writes nothing at all.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ChecksEveryRowFirst_AndWritesNoneWhenOneIsRefused()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        var pending = File.ReadAllBytes(PendingPath(temp));
        var done = File.ReadAllBytes(DonePath(temp));

        var refused = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path,
            new AnchorBatchRequest([Declared(Three, "open", "work") with { Priority = "P2" }, Declared(One, "bogus", "work"), Declared("D-AREA-TOPIC-FOUR", "open", "work")])
            {
                New = [Three, "D-AREA-TOPIC-FOUR"],
            },
            AnchorBatchMode.Apply,
            cancellationToken);
        var dry = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path, new AnchorBatchRequest([Declared(Three, "open", "work") with { Priority = "P2" }]) { New = [Three] }, AnchorBatchMode.Plan, cancellationToken);

        Assert.False(refused.Succeeded);
        Assert.Collection(
            refused.Problems,
            problem => Assert.StartsWith($"{One}: 'bogus' is not a status.", problem),
            problem => Assert.StartsWith("D-AREA-TOPIC-FOUR: 'D-AREA-TOPIC-FOUR' has no row yet, and a new anchor needs a priority", problem));
        Assert.True(dry.Succeeded);
        Assert.Equal(pending, File.ReadAllBytes(PendingPath(temp)));
        Assert.Equal(done, File.ReadAllBytes(DonePath(temp)));
    }

    /// <summary>
    /// A write that fails once rows are being written puts both registries back byte for byte - a byte order mark no write
    /// of the tool's would keep among them - and says so, so rows are never left half applied.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_PutsBothRegistriesBackByteForByte_WhenAWriteFails()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        File.WriteAllBytes(PendingPath(temp), [.. System.Text.Encoding.UTF8.Preamble, .. File.ReadAllBytes(PendingPath(temp))]);
        var pending = File.ReadAllBytes(PendingPath(temp));
        var done = File.ReadAllBytes(DonePath(temp));

        var crashing = new AnchorRegistryService(
            harness.ContextLoader,
            harness.AnchorRegistryLocator,
            harness.AnchorRegistryLock,
            new FailingWriteFileSystem(harness.FileSystem, failOnWrite: 3));

        var batch = await crashing.ApplyAsync(
            temp.Path,
            new AnchorBatchRequest([Declared(Three, "open", "work") with { Priority = "P2" }, Declared(One, "closed", "work")]) { New = [Three] },
            AnchorBatchMode.Apply,
            cancellationToken);

        Assert.False(batch.Succeeded);
        Assert.StartsWith("writing the rows failed:", batch.Failure);
        Assert.Equal([".plans/_deferred-anchor-registry.md", ".plans/_deferred-anchor-registry-done.md"], batch.Restored.Order(StringComparer.Ordinal).Reverse());
        Assert.Empty(batch.RestoreFailed);
        Assert.Equal(pending, File.ReadAllBytes(PendingPath(temp)));
        Assert.Equal(done, File.ReadAllBytes(DonePath(temp)));
    }

    /// <summary>
    /// A row whose cells hold a pipe is written, reads back as declared, and is already in when applied again: stored
    /// cells are compared as they read, pipes plain, never as they are written, escaped.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ARowWithAPipeInItsCells_ReadsBackAsDeclared_AndIsAlreadyInWhenAppliedAgain()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var rows = new AnchorBatchRequest([new(Three, "open", "a | b", "x || y", "z|w") { Priority = "P2" }]) { New = [Three] };

        var first = await harness.AnchorRegistryService.ApplyAsync(temp.Path, rows, AnchorBatchMode.Apply, cancellationToken);
        var again = await harness.AnchorRegistryService.ApplyAsync(temp.Path, rows, AnchorBatchMode.Apply, cancellationToken);

        Assert.True(first.Succeeded, first.Failure ?? string.Join("; ", first.Problems));
        Assert.Equal("a | b", Assert.Single(Rows(harness, PendingPath(temp))).Trigger);
        Assert.True(again.Succeeded, again.Failure ?? string.Join("; ", again.Problems));
        Assert.Equal(AnchorRowAction.AlreadyIn, Assert.Single(again.Rows).Action);
    }

    /// <summary>
    /// write-anchor and set-anchor refuse a cell they would store cut - an id broken at a line, a path broken after its
    /// '/' - and write nothing: the cut is the door's own rule, so the door is where it is seen.
    /// </summary>
    [Fact]
    public async Task EveryWrite_RefusesACellItWouldStoreCut_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        var before = File.ReadAllText(PendingPath(temp));

        var written = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.WriteAsync(
            temp.Path, new AnchorWriteRequest(Two, "P1", "cites D-AREA-\nTOPIC-ONE across a line"), dryRun: false, cancellationToken));
        var set = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(One) { ClosingWork = "see src/hir/\ntest_x.cpp for the case" }, dryRun: false, cancellationToken));

        Assert.Equal(HarnessExit.UsageError, written.ExitCode);
        Assert.StartsWith("The Trigger holds an anchor id cut where a line ends ('D-AREA-')", written.Message);
        Assert.Equal(HarnessExit.UsageError, set.ExitCode);
        Assert.StartsWith(@"The Closing work holds a path broken across a line after a '/' ('src/hir/\ntest_x.cpp')", set.Message);
        Assert.Equal(before, File.ReadAllText(PendingPath(temp)));
    }

    /// <summary>
    /// A write that newly cites an id no row holds is refused, naming it; a row it cites that is there, and a family of ids,
    /// pass. A citation the stored cell already made is history, and is not judged again when the cell is rewritten.
    /// </summary>
    [Fact]
    public async Task EveryWrite_RefusesANewCitationOfNoRow_ButNeverJudgesOneAlreadyStored()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(
            temp.Path, Anchor(Two) with { CrossRefs = $"after {One}, and the D-AREA-TOPIC-SUB-* family" }, dryRun: false, cancellationToken);
        File.WriteAllText(PendingPath(temp), File.ReadAllText(PendingPath(temp)).Replace($"| after {One},", "| after D-RETIRED-HARNESS-ROW,", StringComparison.Ordinal));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.WriteAsync(
            temp.Path, Anchor(Three) with { CrossRefs = "[[D-PROBE-NO-SUCH-ROW-ANYWHERE]]" }, dryRun: false, cancellationToken));
        await harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(Two) { CrossRefs = "after D-RETIRED-HARNESS-ROW, and now more" }, dryRun: false, cancellationToken);
        var added = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(
            temp.Path, new AnchorSetRequest(Two) { CrossRefs = "after D-RETIRED-HARNESS-ROW and D-AREA-TOPIC-NOWHERE" }, dryRun: false, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith($"'{Three}' cites D-PROBE-NO-SUCH-ROW-ANYWHERE in its Cross-refs, which no row of either registry holds: write the row it names first", refused.Message);
        Assert.Equal(HarnessExit.Refused, added.ExitCode);
        Assert.StartsWith($"'{Two}' cites D-AREA-TOPIC-NOWHERE in its Cross-refs, which no row", added.Message);
        Assert.Equal("after D-RETIRED-HARNESS-ROW, and now more", Rows(harness, PendingPath(temp)).Single(row => row.Id == Two).CrossRefs);
    }

    /// <summary>
    /// A batch makes only the rows named new: an id no registry holds that nobody named - here a typo of an existing row's
    /// id, closing it - is refused with the rows that begin the same way, and nothing is written. Named new, it is made.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_MakesOnlyTheRowsNamedNew_RefusingATypoOfAnExistingId()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        var typo = new AnchorBatchRequest([Declared("D-AREA-TOPIC-ONR", "closed", "work") with { Priority = "P2" }]);
        var done = File.ReadAllText(DonePath(temp));

        var refused = await harness.AnchorRegistryService.ApplyAsync(temp.Path, typo, AnchorBatchMode.Apply, cancellationToken);

        Assert.Equal(
            "D-AREA-TOPIC-ONR: 'D-AREA-TOPIC-ONR' has no row in either registry, and it was not named new: a typo in an existing row's id would make "
            + $"it a second row. If it is a new row, pass --new D-AREA-TOPIC-ONR; rows that begin the same way: {One}.",
            Assert.Single(refused.Problems));
        Assert.Equal(done, File.ReadAllText(DonePath(temp)));

        var made = await harness.AnchorRegistryService.ApplyAsync(temp.Path, typo with { New = ["D-AREA-TOPIC-ONR"] }, AnchorBatchMode.Apply, cancellationToken);

        Assert.True(made.Succeeded, string.Join("; ", made.Problems));
        Assert.Equal(["D-AREA-TOPIC-ONR"], Rows(harness, DonePath(temp)).Select(row => row.Id));
    }

    /// <summary>
    /// A row not named new is refused for everything else wrong with it beside that, so one run shows what there is to
    /// correct rather than one thing at a time.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_NamesEveryRefusalOfARowNotNamedNew_AtOnce()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var refused = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path, new AnchorBatchRequest([new(Three, "open", "cites D-AREA-\nTOPIC-ONE", "work", "refs")]), AnchorBatchMode.Plan, cancellationToken);

        Assert.Collection(
            refused.Problems,
            problem => Assert.StartsWith($"{Three}: '{Three}' has no row in either registry, and it was not named new", problem),
            problem => Assert.StartsWith($"{Three}: '{Three}' has no row yet, and a new anchor needs a priority", problem),
            problem => Assert.StartsWith($"{Three}: The Trigger holds an anchor id cut where a line ends ('D-AREA-')", problem));
    }

    /// <summary>
    /// A row named new that a registry holds other than as declared is refused; one standing exactly as declared - the
    /// batch's own, made before - is already in. A name, or an acceptance of a loss, that matches nothing is refused.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_RefusesANameThatDoesNotFit_AndOneThatMatchesNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var differs = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path, new AnchorBatchRequest([Declared(One, "gated", "work")]) { New = [One] }, AnchorBatchMode.Plan, cancellationToken);
        var madeBefore = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path, new AnchorBatchRequest([Declared(One, "open", "work")]) { New = [One], AcceptLost = [new(One, "closing")] }, AnchorBatchMode.Apply, cancellationToken);
        var stale = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path,
            new AnchorBatchRequest([Declared(One, "open", "work and more")]) { New = ["D-AREA-TOPIC-NINE"], AcceptLost = [new(One, "closing")] },
            AnchorBatchMode.Plan,
            cancellationToken);

        Assert.StartsWith($"{One}: '{One}' was named new, but .plans/_deferred-anchor-registry.md:", Assert.Single(differs.Problems));
        Assert.True(madeBefore.Succeeded, string.Join("; ", madeBefore.Problems));
        Assert.Equal(AnchorRowAction.AlreadyIn, Assert.Single(madeBefore.Rows).Action);
        Assert.Equal(
            [
                "--new D-AREA-TOPIC-NINE names no row of the batch: drop it, or correct the id.",
                $"--accept-lost {One}:closing names a cell that keeps its stored text here: drop it, or correct it.",
            ],
            stale.Problems);
    }

    /// <summary>
    /// A cell whose stored text would not survive is shown with its word diff when a batch is planned, refused when it is
    /// checked or applied, and written once accepted; an addendum that keeps the stored text needs no acceptance.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ShowsALosingCellWhenPlanned_RefusesItUntilAccepted_AndLetsAnAddendumThrough()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);
        var losing = new AnchorBatchRequest([Declared(One, "open", "a different plan"), Declared(Two, "open", "work and the test that proves it")]);
        var before = File.ReadAllText(PendingPath(temp));

        var planned = await harness.AnchorRegistryService.ApplyAsync(temp.Path, losing, AnchorBatchMode.Plan, cancellationToken);
        var checkedBatch = await harness.AnchorRegistryService.ApplyAsync(temp.Path, losing, AnchorBatchMode.Check, cancellationToken);
        var refused = await harness.AnchorRegistryService.ApplyAsync(temp.Path, losing, AnchorBatchMode.Apply, cancellationToken);

        Assert.True(planned.Succeeded, string.Join("; ", planned.Problems));
        Assert.Equal(new AnchorLostCell(new AnchorRowCell(One, "closing"), "[-work-] {+a different plan+}", Accepted: false), Assert.Single(planned.Rows[0].Lost));
        Assert.Empty(planned.Rows[1].Lost);
        Assert.Equal(
            $"{One}: its closing does not keep its stored text word for word - [-work-] {{+a different plan+}} - so it is written only with --accept-lost {One}:closing.",
            Assert.Single(checkedBatch.Problems));
        Assert.Equal(checkedBatch.Problems, refused.Problems);
        Assert.Equal(before, File.ReadAllText(PendingPath(temp)));

        var accepted = await harness.AnchorRegistryService.ApplyAsync(temp.Path, losing with { AcceptLost = [new(One, "closing")] }, AnchorBatchMode.Apply, cancellationToken);

        Assert.True(accepted.Succeeded, string.Join("; ", accepted.Problems));
        Assert.True(Assert.Single(accepted.Rows[0].Lost).Accepted);
        Assert.Equal(["a different plan", "work and the test that proves it"], Rows(harness, PendingPath(temp)).Select(row => row.ClosingWork));
    }

    /// <summary>
    /// Rows applied together may cite one another, new rows among them; a citation of a row neither the registries nor the
    /// batch holds is refused.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_LetsRowsOfOneBatchCiteOneAnother_AndRefusesACitationOfNoRow()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        const string Four = "D-AREA-TOPIC-FOUR";
        AnchorRowDeclaration[] rows =
        [
            new(Three, "open", $"waits on {Four}", "work", "refs") { Priority = "P2" },
            new(Four, "open", "found first", "work", $"[[{Three}]]") { Priority = "P2" },
        ];

        var together = await harness.AnchorRegistryService.ApplyAsync(temp.Path, new AnchorBatchRequest(rows) { New = [Three, Four] }, AnchorBatchMode.Plan, cancellationToken);
        var nowhere = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path,
            new AnchorBatchRequest([rows[0] with { Trigger = "waits on D-AREA-TOPIC-NOWHERE" }]) { New = [Three] },
            AnchorBatchMode.Plan,
            cancellationToken);

        Assert.True(together.Succeeded, string.Join("; ", together.Problems));
        Assert.StartsWith(
            $"{Three}: '{Three}' cites D-AREA-TOPIC-NOWHERE in its Trigger, which no row of either registry holds, nor any row of the batch",
            Assert.Single(nowhere.Problems));
    }

    /// <summary>
    /// How the registries hold rows other than as declared is read as they are, held to no rule a write is held to: a
    /// row declared with a value no write would take now - a path cut in two - still compares as the row it is.
    /// </summary>
    [Fact]
    public async Task DifferencesAsync_SaysHowEachRowIsHeldOtherThanDeclared_WhateverAWriteWouldRefuse()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);
        File.WriteAllText(PendingPath(temp), File.ReadAllText(PendingPath(temp)).Replace($"| trigger for {Two} | work |", $"| trigger for {Two} | see src/ main_x.c |", StringComparison.Ordinal));

        var differences = await harness.AnchorRegistryService.DifferencesAsync(
            temp.Path,
            [
                new(One, "gated", $"trigger for {One}", "work", "refs"),
                new(Two, "open", $"trigger for {Two}", "see src/\nmain_x.c", "refs"),
                new(Three, "open", "t", "c", "r"),
            ],
            cancellationToken);

        Assert.Equal(
            [
                new AnchorDifference(One, $"its status is '{AnchorStatus.Render(AnchorState.Open)}'"),
                new AnchorDifference(Three, "it has no row in either registry"),
            ],
            differences);
    }

    /// <summary>
    /// A write reads what a cell cites as check-anchor-citations reads it: an id of two segments after the prefix is a
    /// citation, as is one written straight after an escape such as <c>\n</c>, and each is refused where no row holds it.
    /// </summary>
    [Fact]
    public async Task EveryWrite_ReadsACitationAsCheckAnchorCitationsReadsOne()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var shortOne = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.WriteAsync(
            temp.Path, Anchor(One) with { CrossRefs = "see D-HIR-LOWERNIG" }, dryRun: true, cancellationToken));
        var escaped = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.WriteAsync(
            temp.Path, Anchor(One) with { ClosingWork = "prints << \"\\nD-AREA-TOPIC-NOWHERE: failed\"" }, dryRun: true, cancellationToken));

        Assert.StartsWith($"'{One}' cites D-HIR-LOWERNIG in its Cross-refs, which no row", shortOne.Message);
        Assert.StartsWith($"'{One}' cites D-AREA-TOPIC-NOWHERE in its Closing work, which no row", escaped.Message);
    }

    /// <summary>
    /// A path with a space after a '/' is a cut where it starts at a directory the tree has at its top, read as the write
    /// runs: before the tree has 'src', the same text passes.
    /// </summary>
    [Fact]
    public async Task EveryWrite_RefusesAPathSpacedAfterATopDirectory_OnlyWhereTheTreeHasOne()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var request = new AnchorWriteRequest(One, "P1", "see src/ main_x.c for the case");

        var passed = await harness.AnchorRegistryService.WriteAsync(temp.Path, request, dryRun: true, cancellationToken);
        Directory.CreateDirectory(temp.Combine("src"));
        var refused = await Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.WriteAsync(temp.Path, request, dryRun: true, cancellationToken));

        Assert.True(passed.IsNew);
        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.StartsWith("The Trigger holds a path with a space after a '/' ('src/ main_x.c', as it would be stored)", refused.Message);
    }

    /// <summary>
    /// set-anchor judges each prose cell it rewrites: a Cross-refs cut, and a new citation of no row in its Trigger or its
    /// Closing work, are each refused naming the cell. A row of the done registry resolves a citation as a row of the
    /// pending one does.
    /// </summary>
    [Fact]
    public async Task SetAnchor_JudgesEveryProseCellItRewrites_AndARowOfEitherRegistryResolves()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Three, "closed"), dryRun: false, cancellationToken);

        Task<HarnessException> Refused(AnchorSetRequest request) => Assert.ThrowsAsync<HarnessException>(() => harness.AnchorRegistryService.SetAsync(temp.Path, request, dryRun: true, cancellationToken));

        var cut = await Refused(new AnchorSetRequest(One) { CrossRefs = "see tests/hir/\ntest_x.cpp" });
        var trigger = await Refused(new AnchorSetRequest(One) { Trigger = "waits on D-AREA-TOPIC-NOWHERE" });
        var closing = await Refused(new AnchorSetRequest(One) { ClosingWork = "after D-AREA-TOPIC-NOWHERE" });
        var resolved = await harness.AnchorRegistryService.SetAsync(temp.Path, new AnchorSetRequest(One) { CrossRefs = $"closed by {Three}" }, dryRun: true, cancellationToken);

        Assert.StartsWith("The Cross-refs holds a path broken across a line after a '/'", cut.Message);
        Assert.StartsWith($"'{One}' cites D-AREA-TOPIC-NOWHERE in its Trigger, which no row", trigger.Message);
        Assert.StartsWith($"'{One}' cites D-AREA-TOPIC-NOWHERE in its Closing work, which no row", closing.Message);
        Assert.Equal(["cross-refs"], resolved.Fields.Select(field => field.Field));
    }

    /// <summary>
    /// Every refusal of a changed row is named at once - a cut, a citation of no row, and the loss of stored text a check
    /// refuses - and an acceptance of a loss is not called stale for a row refused before its loss could be judged.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_NamesEveryRefusalOfAChangedRowAtOnce_AndNeverCallsAnUnjudgedAcceptanceStale()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);

        var everything = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path,
            new AnchorBatchRequest([new(One, "open", $"trigger for {One}", "a different plan, see D-AREA-\nTOPIC-TWO", "refs [[D-AREA-TOPIC-NOWHERE]]")]),
            AnchorBatchMode.Check,
            cancellationToken);
        var unread = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path,
            new AnchorBatchRequest([Declared(One, "bogus", "a different plan")]) { AcceptLost = [new(One, "closing")] },
            AnchorBatchMode.Check,
            cancellationToken);

        Assert.Collection(
            everything.Problems,
            problem => Assert.StartsWith($"{One}: The Closing work holds an anchor id cut where a line ends ('D-AREA-')", problem),
            problem => Assert.StartsWith($"{One}: '{One}' cites D-AREA-TOPIC-NOWHERE in its Cross-refs", problem),
            problem => Assert.StartsWith($"{One}: its closing does not keep its stored text word for word - [-work-] {{+a different plan, see D-AREA- TOPIC-TWO+}}", problem));
        Assert.Equal([$"{One}: 'bogus' is not a status. Use one of {string.Join(", ", AnchorStatus.Words)}."], unread.Problems);
    }

    /// <summary>
    /// A cell filled where it was empty, or respaced with its words kept, needs no acceptance; one emptied loses what it
    /// held, shown as removed, and is written only once accepted. Each lost cell of a row needs its own acceptance.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_LetsAFillOrARespacingThrough_AndHoldsEveryLostCellToItsOwnAcceptance()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One) with { ClosingWork = null }, dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two) with { ClosingWork = "keep  these   runs" }, dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Three), dryRun: false, cancellationToken);
        var rows = new AnchorBatchRequest(
        [
            Declared(One, "open", "the fix"),
            Declared(Two, "open", "keep these runs"),
            new(Three, "open", "another trigger", string.Empty, "refs"),
        ]);

        var checkedBatch = await harness.AnchorRegistryService.ApplyAsync(temp.Path, rows, AnchorBatchMode.Check, cancellationToken);
        var half = await harness.AnchorRegistryService.ApplyAsync(temp.Path, rows with { AcceptLost = [new(Three, "closing")] }, AnchorBatchMode.Check, cancellationToken);
        var accepted = await harness.AnchorRegistryService.ApplyAsync(temp.Path, rows with { AcceptLost = [new(Three, "closing"), new(Three, "trigger")] }, AnchorBatchMode.Apply, cancellationToken);

        Assert.Collection(
            checkedBatch.Problems,
            problem => Assert.StartsWith($"{Three}: its trigger does not keep its stored text word for word", problem),
            problem => Assert.StartsWith($"{Three}: its closing does not keep its stored text word for word - [-work-] - so", problem));
        Assert.StartsWith($"{Three}: its trigger does not keep", Assert.Single(half.Problems));
        Assert.True(accepted.Succeeded, string.Join("; ", accepted.Problems));
        Assert.Equal([string.Empty, string.Empty], accepted.Rows.Take(2).Select(row => string.Concat(row.Lost.Select(cell => cell.Diff))));
        Assert.Equal(["the fix", "keep these runs", string.Empty], Rows(harness, PendingPath(temp)).Select(row => row.ClosingWork));
    }

    /// <summary>An acceptance of a loss in a batch of no rows is refused as the typo it is, never dropped unseen.</summary>
    [Fact]
    public async Task ApplyAsync_OfNoRows_StillRefusesAnAcceptanceOfNothing()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);

        var batch = await harness.AnchorRegistryService.ApplyAsync(
            temp.Path, new AnchorBatchRequest([]) { AcceptLost = [new(One, "closing")] }, AnchorBatchMode.Plan, TestContext.Current.CancellationToken);

        Assert.Equal([$"--accept-lost {One}:closing names a cell that keeps its stored text here: drop it, or correct it."], batch.Problems);
    }

    /// <summary>
    /// A row an id has twice is said to have two, and a declaration whose status does not read is said to differ as such:
    /// comparing holds a row to no rule a write is held to, and refuses nothing.
    /// </summary>
    [Fact]
    public async Task DifferencesAsync_SaysATwiceHeldRow_AndADeclarationThatDoesNotRead_WithoutRefusingEither()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(One), dryRun: false, cancellationToken);
        await harness.AnchorRegistryService.WriteAsync(temp.Path, Anchor(Two), dryRun: false, cancellationToken);
        var line = File.ReadAllLines(PendingPath(temp)).Single(text => text.Contains($"`{One}`", StringComparison.Ordinal));
        var done = File.ReadAllText(DonePath(temp));
        File.WriteAllText(DonePath(temp), (done.EndsWith('\n') ? done : done + "\n") + line + "\n");

        var differences = await harness.AnchorRegistryService.DifferencesAsync(temp.Path, [Declared(One, "open", "work"), Declared(Two, "bogus", "work")], cancellationToken);

        Assert.Equal(
            [
                new AnchorDifference(One, "it has 2 rows"),
                new AnchorDifference(Two, "what was declared of it, status 'bogus' and priority '', does not read as a row"),
            ],
            differences);
    }

    private static AnchorRowDeclaration Declared(string id, string status, string closing)
        => new(id, status, $"trigger for {id}", closing, "refs");

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, bool triggerCarriesVerdict = false)
    {
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            triggerCarriesVerdict ? HarnessFactory.TriggerCarriesVerdict() : null);
        return harness;
    }

    private static AnchorWriteRequest Anchor(string id, string status = "open")
        => new(id, "P1", $"trigger for {id}") { Status = status, ClosingWork = "work", CrossRefs = "refs" };

    private static string PendingPath(TempDirectory temp) => temp.Combine(".plans", "_deferred-anchor-registry.md");

    private static string DonePath(TempDirectory temp) => temp.Combine(".plans", "_deferred-anchor-registry-done.md");

    private static IReadOnlyList<AnchorRow> Rows(HarnessFactory harness, string path)
        => AnchorRegistryDocument.Parse(File.ReadAllText(path), new AnchorIdRules("D", 3)).Rows;

    /// <summary>The registry lock, counting how often it is taken.</summary>
    private sealed class CountingLock(IAnchorRegistryLock inner) : IAnchorRegistryLock
    {
        public int Taken { get; private set; }

        public T RunExclusive<T>(AnchorRegistries registries, Func<T> work)
        {
            Taken++;
            return inner.RunExclusive(registries, work);
        }
    }

    /// <summary>A file system that fails one atomic write, as a crash between the two writes of a move would.</summary>
    private sealed class FailingWriteFileSystem(IFileSystem inner, int failOnWrite) : PassThroughFileSystem(inner)
    {
        private int _writes;

        public override void WriteAllTextAtomic(string path, string contents)
        {
            if (Interlocked.Increment(ref _writes) == failOnWrite)
            {
                throw new IOException("Simulated failure on the second write of a move.");
            }

            base.WriteAllTextAtomic(path, contents);
        }
    }
}
