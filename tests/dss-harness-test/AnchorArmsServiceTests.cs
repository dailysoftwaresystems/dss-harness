using System.Text.Json;
using DssHarness.Core.Anchors;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Mutations;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// What check-anchor-arms holds of anchor rows' claims about mutation arms, over the one reading a listing is made of:
/// an arm a done row names must be unmoved here with a recorded passed on the legs its S row names, a pass cited beside
/// a run id must be spelled as history, and a backticked id no A row holds is an arm only where git history once
/// declared it.
/// </summary>
public sealed class AnchorArmsServiceTests
{
    private const string One = "D-AREA-TOPIC-ONE";
    private const string Two = "D-AREA-TOPIC-TWO";
    private const string Run = "20261021-192254-56514608";

    /// <summary>Where the done registry is, as the configuration names it by default.</summary>
    private const string Done = AnchorSettings.DefaultDoneAnchorsPath;

    /// <summary>One BUILD-RED arm and its paired control, citing four texts and one site.</summary>
    private const string Registry = """
        A | one | src/site.txt | texts/one.before | texts/one.after | BUILD-RED | app | - | 0 | PAIRED-CONTROL | why one
        B | one | texts/one.control-before | texts/one.control-after | another builds
        """;

    /// <summary>What a retired id's finding says of it, after the row and the id.</summary>
    private const string Retired = "an A row declared it once, so it was renamed or retired, and the row cites what the registry no longer declares";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A done row naming an arm no leg has swept is a finding that fails the check, worded as the listing words the
    /// arm's first reason, and the line the check ends on counts it.
    /// </summary>
    [Fact]
    public async Task ADoneRowNamingAnArmNeverSwept_IsAFinding_InTheListingsOwnWords()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await CloseAsync(harness, repository, One, "closed with `one`");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.False(report.Passed);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(AnchorFindingSeverity.Fatal, finding.Severity);
        Assert.Equal(
            $"anchor '{One}' names arm 'one', and on leg 'native' it is pending: never swept on this leg",
            finding.Message);

        var outcome = AnchorReports.Arms(report, json: false);

        Assert.Equal(AnchorExit.Findings, outcome.ExitCode);
        Assert.Equal("1 claim(s) about mutation arms that does not hold", outcome.Message);
        Assert.Contains(MutationListing.Note, string.Join("\n", outcome.Data), StringComparison.Ordinal);
    }

    /// <summary>
    /// A done row naming an arm whose recorded passed nothing read here moved for holds: the check passes, saying the
    /// arm is unmoved here and never that it holds.
    /// </summary>
    [Fact]
    public async Task ADoneRowWhoseArmIsUnmovedWithARecordedPassed_Holds()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await PassedAsync(harness, repository, "native", "one");
        await CloseAsync(harness, repository, One, "closed with `one`");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.True(report.Passed, string.Join(" | ", report.Findings.Select(finding => finding.Message)));
        Assert.Equal(["one"], report.NamedArms);
        Assert.Equal(["one"], report.JudgedArms);
        Assert.Equal((1, 1), (report.Rows, report.DoneRows));

        var outcome = AnchorReports.Arms(report, json: false);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("1 row(s) across both registries; every arm a done row names is unmoved here with a recorded passed", outcome.Message);
    }

    /// <summary>
    /// An open row's arms are not judged: only a done row claims its work is closed, so an open row naming an arm no leg
    /// has swept fails nothing - open work in a consumer's registry never fails its gate.
    /// </summary>
    [Fact]
    public async Task AnOpenRowNamingAnArmNeverSwept_IsNotJudged()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", "the report frame") { CrossRefs = "to close with `one`" },
            dryRun: false,
            Token);

        var report = await service.CheckAsync(repository, null, Token);

        Assert.True(report.Passed);
        Assert.Empty(report.Findings);
        Assert.Empty(report.NamedArms);
        Assert.Equal((1, 0), (report.Rows, report.DoneRows));
    }

    /// <summary>
    /// An arm is judged on each leg separately: pending on the leg that never swept it, unmoved with a recorded passed
    /// on the leg that did, and the finding names the one leg alone.
    /// </summary>
    [Fact]
    public async Task AnArmPendingOnOneLeg_IsAFindingForThatLegAlone()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp, legs: ["native", "second"]);
        await PassedAsync(harness, repository, "second", "one");
        await CloseAsync(harness, repository, One, "closed with `one`");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.Equal(["native", "second"], report.Legs);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(
            $"anchor '{One}' names arm 'one', and on leg 'native' it is pending: never swept on this leg",
            finding.Message);
    }

    /// <summary>
    /// <c>--legs</c> narrows what is judged as a listing narrows it: an arm unmoved with a recorded passed on the leg
    /// named holds, though another leg never swept it.
    /// </summary>
    [Fact]
    public async Task TheLegsNamed_AreTheLegsJudged()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp, legs: ["native", "second"]);
        await PassedAsync(harness, repository, "second", "one");
        await CloseAsync(harness, repository, One, "closed with `one`");

        var report = await service.CheckAsync(repository, ["second"], Token);

        Assert.True(report.Passed);
        Assert.Equal(["second"], report.Legs);
        Assert.Empty(report.Findings);
        Assert.Equal(["one"], report.JudgedArms);
    }

    /// <summary>
    /// An arm a selected leg does not judge - its S row names only legs <c>--legs</c> left out - is noted, never passed
    /// over as held: the check passes, the note names the legs that judge it, and the line it ends on speaks only of the
    /// arms a selected leg judged.
    /// </summary>
    [Fact]
    public async Task AnArmNoSelectedLegJudges_IsNoted_AndNeverSaidToHold()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            registry: Registry + "\nS | one | second | why one is scoped\n",
            legs: ["native", "second"]);
        await CloseAsync(harness, repository, One, "closed with `one`");

        var report = await service.CheckAsync(repository, ["native"], Token);

        Assert.True(report.Passed);
        var note = Assert.Single(report.Findings);

        Assert.Equal(
            (AnchorFindingSeverity.Note, Done, $"anchor '{One}' names arm 'one', which is judged on none of the selected legs: its S row, line 3, names second"),
            (note.Severity, note.File, note.Message));
        Assert.Equal(["one"], report.NamedArms);
        Assert.Empty(report.JudgedArms);
        Assert.Equal(
            "1 row(s) across both registries; every arm a done row names that a selected leg judges is unmoved here with a "
                + "recorded passed, and 1 arm(s) none of them judges",
            AnchorReports.Arms(report, json: false).Message);
    }

    /// <summary>
    /// Where no leg is declared, nothing could ever judge an arm a done row names, and the row's close rests on nothing
    /// judged: a finding that fails the check.
    /// </summary>
    [Fact]
    public async Task AnArmADoneRowNames_WhereNoLegIsDeclared_IsAFinding()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp, legs: []);
        await CloseAsync(harness, repository, One, "closed with `one`");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.False(report.Passed);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(
            (AnchorFindingSeverity.Fatal, $"anchor '{One}' names arm 'one', which is judged on none of the selected legs: no leg is declared"),
            (finding.Severity, finding.Message));
        Assert.Empty(report.Legs);
    }

    /// <summary>A row names an arm ignoring case, as the arms registry compares ids: the finding names the arm as its A row does.</summary>
    [Fact]
    public async Task AnArmNamedInAnotherCase_IsTheArm()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await CloseAsync(harness, repository, One, "closed with `ONE`");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.Equal(["one"], report.NamedArms);
        Assert.Equal(
            $"anchor '{One}' names arm 'one', and on leg 'native' it is pending: never swept on this leg",
            Assert.Single(report.Findings).Message);
    }

    /// <summary>
    /// A done row's arm on a leg that cannot be swept is pending with the leg's own reason, said as the listing says it.
    /// </summary>
    [Fact]
    public async Task ALegThatCannotBeSwept_IsTheReasonTheFindingNames()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            registry: Registry + "\nS | one | awkward | why one is scoped\n",
            legs: ["native", "awkward"],
            awkward: true);
        await CloseAsync(harness, repository, One, "closed with `one`");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.Equal(
            $"anchor '{One}' names arm 'one', and on leg 'awkward' it is pending: this leg cannot be swept: Leg 'awkward' builds "
                + "with toolchain 'other', whose generator is 'Visual Studio 17 2022': set toolchains.other.generator to \"Ninja\". "
                + "A sweep reads which objects each mutation rebuilt from ninja's own records of the build, which only the Ninja "
                + "generator keeps for one configuration alone.",
            Assert.Single(report.Findings).Message);
    }

    /// <summary>
    /// A row malformed in its registry's structure is this check's finding too, as it is --lint's: a row it hides cannot
    /// be judged.
    /// </summary>
    [Fact]
    public async Task AnAnchorRowOutsideTheTable_IsAStructuralFinding()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await CloseAsync(harness, repository, Two, "no arm here");
        var done = Path.Combine(repository, Done);
        await File.AppendAllTextAsync(done, "\nSome prose.\n\n| D-AREA-TOPIC-STRAY | P1 |\n", Token);
        var stray = Array.IndexOf(await File.ReadAllLinesAsync(done, Token), "| D-AREA-TOPIC-STRAY | P1 |") + 1;

        var report = await service.CheckAsync(repository, null, Token);

        Assert.False(report.Passed);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(
            (Done, stray, AnchorFindingSeverity.Fatal, "an anchor row sits outside any table, so nothing reads it: | D-AREA-TOPIC-STRAY | P1 |"),
            (finding.File, finding.LineNumber, finding.Severity, finding.Message));
    }

    /// <summary>A registry that is not there is a finding of this check, as it is the balance's: the rows it would hold cannot be judged.</summary>
    [Fact]
    public async Task AMissingRegistry_IsAFinding()
    {
        using var temp = new TempDirectory();
        var (_, repository, service) = await PrepareAsync(temp);
        File.Delete(Path.Combine(repository, Done));
        File.Delete(Path.Combine(repository, AnchorSettings.DefaultPendingAnchorsPath));

        var report = await service.CheckAsync(repository, null, Token);

        Assert.False(report.Passed);
        Assert.Equal(
            [
                (Done, AnchorFindingSeverity.Fatal, "there is no done registry, so the arms its rows would name cannot be checked"),
                (AnchorSettings.DefaultPendingAnchorsPath, AnchorFindingSeverity.Fatal, "there is no pending registry, so the arms its rows would name cannot be checked"),
            ],
            report.Findings.Select(finding => (finding.File, finding.Severity, finding.Message)));
    }

    /// <summary>
    /// A pass word followed by a run id, spelled by no form of history, is a finding: a pass holds only of the bytes
    /// that run read.
    /// </summary>
    [Fact]
    public async Task APassCitedBesideARunId_OutsideAnyHistoryForm_IsAFinding()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", $"the frame shipped; PASSED ({Run}) by the sweep") { Status = "closed" },
            dryRun: false,
            Token);

        var report = await service.CheckAsync(repository, null, Token);

        Assert.False(report.Passed);
        Assert.Equal(CitationOf(One, $"PASSED ({Run}"), Assert.Single(report.Findings).Message);
    }

    /// <summary>
    /// A pass cited inside a span matching a configured history form is the row's own record of when, and is not
    /// reported; one outside it still is, so the forms spell history rather than turn the rule off.
    /// </summary>
    [Fact]
    public async Task APassCitedInsideAConfiguredHistoryForm_IsNotReported()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            new AnchorSettings
            {
                HistoryForms = ["closed at {commit}, passed ({run})"],
            });
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", $"closed at 0123456789abcdef0123456789abcdef01234567, passed ({Run})") { Status = "closed" },
            dryRun: false,
            Token);

        var history = await service.CheckAsync(repository, null, Token);

        Assert.True(history.Passed, string.Join(" | ", history.Findings.Select(finding => finding.Message)));

        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(Two, "P2", $"but PASSED ({Run}) is not history"),
            dryRun: false,
            Token);

        var outside = await service.CheckAsync(repository, null, Token);

        // Only the row outside the form: the one inside it is history still.
        var finding = Assert.Single(outside.Findings);

        Assert.Equal((Two, CitationOf(Two, $"PASSED ({Run}")), (finding.RowId, finding.Message));
    }

    /// <summary>
    /// A form's commit stands for a commit of any repository the harness serves, a SHA-256 one's sixty-four hex characters
    /// among them, so a pass cited inside such a span is history.
    /// </summary>
    [Fact]
    public async Task AHistoryFormsCommit_TakesASha256CommitWhole()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            new AnchorSettings
            {
                HistoryForms = ["closed at {commit}, passed {run}"],
            });
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", $"closed at {string.Concat(Enumerable.Repeat("0123456789abcdef", 4))}, passed {Run}") { Status = "closed" },
            dryRun: false,
            Token);

        var report = await service.CheckAsync(repository, null, Token);

        Assert.True(report.Passed, string.Join(" | ", report.Findings.Select(finding => finding.Message)));
        Assert.Empty(report.Findings);
    }

    /// <summary>
    /// A backticked id no A row holds is an arm where git history shows an A row declared it once - renamed, or retired -
    /// and is a finding; a backticked token that never was an arm is left alone.
    /// </summary>
    [Fact]
    public async Task ARetiredArmId_OneDeclaredInHistory_IsAFinding_AnIdNeverDeclaredIsNot()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            registry: Registry.Replace("| one |", "| old-name |", StringComparison.Ordinal));
        RegistryWritten(temp, Registry);
        await harness.CommitAllAsync(repository, "the arm renamed", Token);
        await CloseAsync(harness, repository, One, "closed with `old-name` and `never-was`");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.False(report.Passed);
        Assert.Equal($"anchor '{One}' names arm 'old-name', which no A row of 'arms.md' holds: {Retired}", Assert.Single(report.Findings).Message);
    }

    /// <summary>A retired id a row cites twice, in whichever case, is one claim of the row and one finding.</summary>
    [Fact]
    public async Task ARetiredArmIdCitedTwiceInARow_IsOneFinding()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            registry: Registry.Replace("| one |", "| old-name |", StringComparison.Ordinal));
        RegistryWritten(temp, Registry);
        await harness.CommitAllAsync(repository, "the arm renamed", Token);
        await CloseAsync(harness, repository, One, "closed with `old-name`, as `OLD-NAME` said");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.Equal($"anchor '{One}' names arm 'old-name', which no A row of 'arms.md' holds: {Retired}", Assert.Single(report.Findings).Message);
    }

    /// <summary>
    /// An id an A row declared before the arms registry was renamed is one it declared: the history is read at the path
    /// the registry had then, across the rename, so a row citing an arm retired before the move is still a finding.
    /// </summary>
    [Fact]
    public async Task AnIdRetiredBeforeTheRegistryWasRenamed_IsAFinding()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            registry: Registry.Replace("| one |", "| old-name |", StringComparison.Ordinal));
        RegistryWritten(temp, Registry);
        await harness.CommitAllAsync(repository, "the arm renamed", Token);

        Directory.CreateDirectory(temp.Combine("mutations"));
        await harness.RunGitAsync(repository, ["mv", "arms.md", "mutations/arms.md"], Token);
        harness.WriteConfig(repository, ConfigOf(harness, ["native"], mutations: new MutationSettings { Registry = "mutations/arms.md", TextDirectory = "texts" }));
        await harness.CommitAllAsync(repository, "the registry moved", Token);
        await CloseAsync(harness, repository, One, "closed with `old-name`");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.Equal(
            $"anchor '{One}' names arm 'old-name', which no A row of 'mutations/arms.md' holds: {Retired}",
            Assert.Single(report.Findings).Message);
    }

    /// <summary>
    /// A version of the arms registry is read as the registry is read: a byte order mark it opens with is no part of its
    /// first row, so an arm that row declared is one it declared.
    /// </summary>
    [Fact]
    public async Task AVersionOpeningWithAByteOrderMark_IsReadForItsFirstRowsId()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            registry: "﻿" + Registry.Replace("| one |", "| old-name |", StringComparison.Ordinal));
        RegistryWritten(temp, Registry);
        await harness.CommitAllAsync(repository, "the arm renamed", Token);
        await CloseAsync(harness, repository, One, "closed with `old-name`");

        var report = await service.CheckAsync(repository, null, Token);

        Assert.Equal($"anchor '{One}' names arm 'old-name', which no A row of 'arms.md' holds: {Retired}", Assert.Single(report.Findings).Message);
    }

    /// <summary>
    /// The line a failing check ends on counts the claims that fail it, as Passed counts them: a registry's warning is
    /// reported and fails nothing, so it is not counted among them.
    /// </summary>
    [Fact]
    public async Task AWarning_IsNotCountedAmongTheClaimsThatFail()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await CloseAsync(harness, repository, One, "closed with `one`");
        await CloseAsync(harness, repository, Two, "no arm here");
        var done = Path.Combine(repository, Done);
        var lines = (await File.ReadAllLinesAsync(done, Token)).ToList();
        var two = lines.FindIndex(line => line.Contains(Two, StringComparison.Ordinal));
        lines.Insert(two, "<!-- a comment inside the table -->");
        await File.WriteAllTextAsync(done, string.Join("\n", lines) + "\n", Token);

        var report = await service.CheckAsync(repository, null, Token);

        Assert.Equal(
            [AnchorFindingSeverity.Fatal, AnchorFindingSeverity.Warning],
            report.Findings.OrderBy(finding => finding.Severity).Select(finding => finding.Severity));

        var outcome = AnchorReports.Arms(report, json: false);

        Assert.Equal((AnchorExit.Findings, "1 claim(s) about mutation arms that does not hold"), (outcome.ExitCode, outcome.Message));
    }

    /// <summary>
    /// A shallow clone's history is cut, and the check says so in a note that fails nothing: a clone otherwise clean
    /// passes, the note its one finding.
    /// </summary>
    [Fact]
    public async Task AShallowCloneCutHistory_IsANoteThatFailsNothing()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await CloseAsync(harness, repository, One, "no arm here");
        await harness.CommitAllAsync(repository, "the rows", Token);

        var clone = temp.Combine("clone");
        await harness.RunGitAsync(temp.Path, ["clone", "--quiet", "--depth", "1", new Uri(repository).AbsoluteUri, clone], Token);

        var report = await service.CheckAsync(clone, null, Token);

        Assert.True(report.Passed);
        var note = Assert.Single(report.Findings);

        Assert.Equal(
            (AnchorFindingSeverity.Note, "arms.md", 0, "this clone's history is shallow, so commits before where it was cut name no version of 'arms.md': an id no A row holds may have been declared before the cut"),
            (note.Severity, note.File, note.LineNumber, note.Message));
    }

    /// <summary>
    /// The check takes the anchor registries' lock while it reads both, as every reader of both does: while another
    /// process holds it past the wait, the check reads nothing and is refused.
    /// </summary>
    [Fact]
    public async Task ACheckWhileAnotherHoldsTheRegistries_IsRefused()
    {
        using var temp = new TempDirectory();
        var (harness, repository, _) = await PrepareAsync(temp);
        var context = await harness.ContextLoader.LoadAsync(repository, Token);
        var registries = await harness.AnchorRegistryLocator.LocateAsync(context, Token);
        var impatient = Service(harness, new NamedMutexAnchorRegistryLock(harness.Platform, TimeSpan.FromMilliseconds(200)));

        using (RegistryLockHolder.Take(harness.AnchorRegistryLock, registries, Token))
        {
            var refusal = await Assert.ThrowsAsync<HarnessException>(() => impatient.CheckAsync(repository, null, Token));

            Assert.Equal(
                (HarnessExit.Refused, $"Another {ToolPackage.Id} process has held the anchor registries for 0 seconds, so nothing was read or changed. Run the command again once it has finished."),
                (refusal.ExitCode, refusal.Message));
        }

        Assert.True((await impatient.CheckAsync(repository, null, Token)).Passed);
    }

    /// <summary>
    /// With no arms registry configured, the check is refused naming itself and the setting - never another command -
    /// and its document says so, reading nothing.
    /// </summary>
    [Fact]
    public async Task NoArmsRegistry_IsRefused_NamingThisCommand()
    {
        using var temp = new TempDirectory();
        var (_, repository, service) = await PrepareAsync(temp, mutations: new MutationSettings());
        const string Refusal = "check-anchor-arms has no arms registry to read: set mutations.registry to the registry's path, relative to the repository root.";

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.CheckAsync(repository, null, Token));

        Assert.Equal((HarnessExit.ConfigInvalid, Refusal), (refusal.ExitCode, refusal.Message));

        var stopped = await CliRunner.RunAsync(["check-anchor-arms", "--json", "-C", repository], Token);

        Assert.Equal(HarnessExit.ConfigInvalid, stopped.ExitCode);

        using var document = JsonDocument.Parse(stopped.StandardOutput.TrimEnd());

        Assert.Equal(HarnessExit.ConfigInvalid, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(Refusal, document.RootElement.GetProperty("summary").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("armsRegistry").ValueKind);
        Assert.Equal(0, document.RootElement.GetProperty("rows").GetInt32());
        Assert.False(document.RootElement.GetProperty("passed").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("findings").EnumerateArray());
    }

    /// <summary>
    /// --legs takes several legs to one token or one to each, as every command's --legs does, and as its description
    /// and the help say.
    /// </summary>
    [Theory]
    [InlineData("native second")]
    [InlineData("native,second")]
    public async Task TheLegsOption_TakesEitherSpelling(string legs)
    {
        using var temp = new TempDirectory();
        var (_, repository, _) = await PrepareAsync(temp, legs: ["native", "second"]);

        var checkedLegs = await CliRunner.RunAsync(["check-anchor-arms", "--json", "--legs", .. legs.Split(' '), "-C", repository], Token);

        Assert.True(HarnessExit.Success == checkedLegs.ExitCode, checkedLegs.StandardError + checkedLegs.StandardOutput);

        using var document = JsonDocument.Parse(checkedLegs.StandardOutput.TrimEnd());

        Assert.Equal(["native", "second"], document.RootElement.GetProperty("legs").EnumerateArray().Select(leg => leg.GetString()));
    }

    /// <summary>
    /// Every exit answers in the command's own document: a finding's, a pass's, and one stopped before it read
    /// anything, each with its code and the members it always holds.
    /// </summary>
    [Fact]
    public async Task EveryExitAnswersInItsOwnDocument()
    {
        using var temp = new TempDirectory();
        var (harness, repository, _) = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", $"the frame shipped; PASSED ({Run}) by the sweep") { Status = "closed" },
            dryRun: false,
            Token);

        var findings = await CliRunner.RunAsync(["check-anchor-arms", "--json", "-C", repository], Token);

        Assert.True(AnchorExit.Findings == findings.ExitCode, findings.StandardError + findings.StandardOutput);

        using (var document = JsonDocument.Parse(findings.StandardOutput.TrimEnd()))
        {
            Assert.Equal(
                ["armsRegistry", "mutationRecord", "legs", "rows", "doneRows", "namedArms", "passed", "findings"],
                document.RootElement.EnumerateObject().Select(member => member.Name));
            Assert.Equal("arms.md", document.RootElement.GetProperty("armsRegistry").GetString());
            Assert.Equal(CitationOf(One, $"PASSED ({Run}"), document.RootElement.GetProperty("findings").EnumerateArray().Single().GetProperty("message").GetString());
        }

        await PassedAsync(harness, repository, "native", "one");
        await harness.AnchorRegistryService.SetAsync(
            repository,
            new AnchorSetRequest(One) { Trigger = "the frame shipped, and the arm is named below", CrossRefs = "closed with `one`" },
            dryRun: false,
            Token);

        var clean = await CliRunner.RunAsync(["check-anchor-arms", "--json", "-C", repository], Token);

        Assert.Equal(HarnessExit.Success, clean.ExitCode);

        using (var document = JsonDocument.Parse(clean.StandardOutput.TrimEnd()))
        {
            Assert.True(document.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal(["one"], document.RootElement.GetProperty("namedArms").EnumerateArray().Select(arm => arm.GetString()));
        }

        var stopped = await CliRunner.RunAsync(["check-anchor-arms", "--json", "--legs", "no-such-leg", "-C", repository], Token);

        Assert.Equal(HarnessExit.UsageError, stopped.ExitCode);

        using (var document = JsonDocument.Parse(stopped.StandardOutput.TrimEnd()))
        {
            Assert.Equal(HarnessExit.UsageError, document.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("rows").GetInt32());
            Assert.False(document.RootElement.GetProperty("passed").GetBoolean());
            Assert.Empty(document.RootElement.GetProperty("findings").EnumerateArray());
        }
    }

    /// <summary>The listing the reading serves is unchanged: check-mutations --list still answers as it did.</summary>
    [Fact]
    public async Task TheListingAListAnswers_IsUnchanged()
    {
        using var temp = new TempDirectory();
        var (_, repository, _) = await PrepareAsync(temp);

        var listed = await CliRunner.RunAsync(["check-mutations", "--list", "-C", repository], Token);

        Assert.Equal(HarnessExit.Success, listed.ExitCode);
        Assert.Contains("listing 1 arm(s) of 'arms.md' on 1 leg(s); nothing is swept and no host is asked", listed.StandardOutput + listed.StandardError, StringComparison.Ordinal);
        Assert.Contains("'unmoved here' is not 'holds'", listed.StandardOutput + listed.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The anchors topic says what this check reports and how it exits: a missing or malformed registry is one of its
    /// findings, as it is --lint's and the balance's, and an arms registry left unset or refused exits as the
    /// configuration's refusal.
    /// </summary>
    [Fact]
    public void TheAnchorsTopic_SaysHowThisCheckReportsARegistryAndExits()
    {
        var topic = Prose.Words(HelpTopics.Render("anchors"));

        Assert.Contains(
            $"read-anchors --lint, check-anchor-balance and check-anchor-arms report a missing or malformed registry as one of their findings instead, so for them it exits {AnchorExit.Findings}.",
            topic,
            StringComparison.Ordinal);
        Assert.Contains(
            $"{HarnessExit.ConfigInvalid} for check-anchor-arms, mutations.registry is unset or names no file, or the arms registry it names is refused as check-mutations --list refuses it",
            topic,
            StringComparison.Ordinal);
        Assert.Contains("set-anchor with nothing to change, or a --legs of check-anchor-arms naming no leg or leg set", topic, StringComparison.Ordinal);
        Assert.Contains(
            "An arm none of the selected legs judges is noted, naming the legs its S row names, and is a finding where no leg is declared.",
            topic,
            StringComparison.Ordinal);
    }

    /// <summary>What a pass cited outside history says of the row and what it cited.</summary>
    private static string CitationOf(string row, string shown)
        => $"anchor '{row}' states a pass of run '{Run}' ({shown}): a pass holds only of the bytes that run read, and goes stale the first time "
            + "what it judged moves. State it as history - a span matching a form of anchors.historyForms - or drop it";

    /// <summary>Writes <paramref name="id"/> as a closed row whose Cross-refs cell is <paramref name="crossRefs"/>.</summary>
    private static Task CloseAsync(HarnessFactory harness, string repository, string id, string crossRefs)
        => harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(id, "P1", "the report frame") { Status = "closed", CrossRefs = crossRefs },
            dryRun: false,
            Token);

    /// <summary>The repository: one arm of two texts and a site, the legs the test names, and the service over it all.</summary>
    private static async Task<(HarnessFactory Harness, string Repository, AnchorArmsService Service)> PrepareAsync(
        TempDirectory temp,
        AnchorSettings? anchors = null,
        string registry = Registry,
        string[]? legs = null,
        bool awkward = false,
        MutationSettings? mutations = null)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(temp.Path, Token, ConfigOf(harness, legs ?? ["native"], awkward, anchors, mutations));

        RegistryWritten(temp, registry);
        await harness.CommitAllAsync(temp.Path, "the arms registry", Token);

        return (harness, temp.Path, Service(harness));
    }

    /// <summary>
    /// The scene's configuration: two toolchains - the ninja one, and the other one whose generator no sweep reads - a
    /// cmake project, the legs named, each on this machine and on the ninja toolchain but for an awkward leg where
    /// asked, and the arms registry at arms.md where no other mutation settings are given.
    /// </summary>
    private static HarnessConfig ConfigOf(HarnessFactory harness, string[] legs, bool awkward = false, AnchorSettings? anchors = null, MutationSettings? mutations = null)
        => new()
        {
            Toolchains =
            {
                ["ninja"] = new ToolchainConfig { Generator = "Ninja", Env = { ["CC"] = "cc", ["CXX"] = "c++" } },
                ["other"] = new ToolchainConfig { Generator = "Visual Studio 17 2022", Env = { ["CC"] = "cc", ["CXX"] = "c++" } },
            },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Projects = { Project() },
            Legs = legs.ToDictionary(
                name => name,
                name => new LegConfig
                {
                    Os = harness.Platform.PlatformKey,
                    Processor = harness.Platform.Processor,
                    Config = "debug",
                    Toolchain = awkward && name == "awkward" ? "other" : "ninja",
                },
                StringComparer.Ordinal),
            Worktrees = new WorktreeSettings { Root = ".worktrees" },
            Mutations = mutations ?? new MutationSettings { Registry = "arms.md", TextDirectory = "texts" },
            Anchors = anchors ?? new AnchorSettings(),
        };

    /// <summary>The check over the harness's own services, its registries' lock <paramref name="registryLock"/> where one is given.</summary>
    private static AnchorArmsService Service(HarnessFactory harness, IAnchorRegistryLock? registryLock = null)
        => new(
            harness.ContextLoader,
            harness.AnchorRegistryLocator,
            registryLock ?? harness.AnchorRegistryLock,
            new ArmListingReader(SyncKit.Service(harness), harness.FileSystem, harness.Platform),
            harness.GitClient,
            harness.FileSystem);

    /// <summary>Writes the registry and every file it cites.</summary>
    private static void RegistryWritten(TempDirectory temp, string registry)
    {
        temp.WriteFile("arms.md", registry.EndsWith('\n') ? registry : registry + "\n");
        temp.WriteFile(Path.Combine("src", "site.txt"), "site\n");

        (string Name, string Text)[] texts =
        [
            ("one.before", "before\n"),
            ("one.after", "after\n"),
            ("one.control-before", "control before\n"),
            ("one.control-after", "control after\n"),
        ];

        foreach (var (name, text) in texts)
        {
            temp.WriteFile(Path.Combine("texts", name), text);
        }
    }

    /// <summary>The project the legs build, of the kind a sweep can read the build of.</summary>
    private static ProjectConfig Project() => new()
    {
        Name = "app",
        Type = "cmake",
        Path = ".",
        BuildOutputs = [BuildOutput.Keyed([new(PlatformNames.Windows, "app.exe"), new(PlatformScope.Every, "app")])],
    };

    /// <summary>Records a passed of <paramref name="arm"/> on <paramref name="leg"/> that nothing this machine reads has moved since.</summary>
    private static async Task PassedAsync(HarnessFactory harness, string repository, string leg, string arm)
    {
        var context = await harness.ContextLoader.LoadAsync(repository, Token);
        var declared = MutationRegistryParser.Parse(
            MutationRegistryParser.Lines(await File.ReadAllTextAsync(Path.Combine(repository, context.Config.Mutations.Registry!), Token)),
            texts: null).Registry.Find(arm)
            ?? throw new InvalidOperationException($"no A row declares '{arm}'");

        RecordedPasses.Record(harness.FileSystem, context.Layout, context.Config, repository, leg, declared);
    }
}
