using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// check-anchor-citations against a real repository: what is scanned, what resolves, and what the
/// command answers when it was given nothing to scan.
/// </summary>
public sealed class AnchorCitationServiceTests
{
    private const string Known = "D-AREA-TOPIC-ONE";

    [Fact]
    public async Task AnIdCitedAfterAnEscape_IsCheckedLikeAnyOther()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        // The literal the guard this replaces drops, carrying an id that resolves to no row.
        temp.WriteFile(Path.Combine("src", "emit.cpp"), @"    stream << ""\nD-AREA-TOPIC-MISSING: note"";" + "\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        var found = Assert.Single(report.Unresolved);
        Assert.Equal("D-AREA-TOPIC-MISSING", found.Id);
        Assert.Equal("src/emit.cpp", found.Path);
        Assert.Equal(1, found.LineNumber);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task ACitationWithARow_Resolves_AndAWordThatMerelyLooksLikeOneIsNotACitation()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await WriteAnchorAsync(harness, temp, Known);

        temp.WriteFile(
            Path.Combine("src", "thing.cpp"),
            $"// see {Known}\n// a FIXED-32-BIT-WORD value\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(1, report.CitationsFound);
    }

    /// <summary>
    /// A citation resolves to a row whose id is exactly the id cited, by the rule read-anchor finds a
    /// row by. A parent's id is not answered by a more specific row, and a fragment a wrapped line cut
    /// short is not answered by the id it was cut from: resolved by containment, both passed, and
    /// the gate called present a row read-anchor could not find.
    /// </summary>
    [Fact]
    public async Task ACitation_ResolvesOnlyToARowWhoseIdItIs_AsReadAnchorFindsIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await WriteAnchorAsync(harness, temp, Known + "-DETAIL");
        await WriteAnchorAsync(harness, temp, "D-AREA-TOPIC-THIRTY");

        temp.WriteFile(
            Path.Combine("src", "thing.cpp"),
            $"// see {Known}\n// see D-AREA-TOPIC-THIRTY\n// a note that wraps (D-AREA-TOPIC-THIR-\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.Equal([Known, "D-AREA-TOPIC-THIR"], report.Unresolved.Select(citation => citation.Id));

        var lookup = await harness.AnchorRegistryService.ReadAsync(
            temp.Path,
            [Known, "D-AREA-TOPIC-THIRTY", "D-AREA-TOPIC-THIR"],
            AnchorScope.All,
            cancellationToken);

        Assert.Equal(
            report.Unresolved.Select(citation => citation.Id),
            lookup.Missing.Select(result => result.Id));
    }

    /// <summary>
    /// A citation cut at the end of its line is reported whatever rows exist - even when the part
    /// before the cut is itself a row, which it does not mean: the id it was cut from is on the next
    /// line, and would stop resolving without the gate saying so.
    /// </summary>
    [Fact]
    public async Task ACitationCutAtTheEndOfItsLine_IsReported_EvenWhereThePartBeforeTheCutIsARow()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await WriteAnchorAsync(harness, temp, Known);
        await WriteAnchorAsync(harness, temp, Known + "-DETAIL");

        temp.WriteFile(Path.Combine("src", "thing.cpp"), $"// (see {Known}-\n// DETAIL)\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        var cut = Assert.Single(report.Unresolved);
        Assert.Equal(Known, cut.Id);
        Assert.True(cut.Cut);

        // Nor does the headline send the reader to add a row: a cut fails whatever rows exist.
        var outcome = AnchorCitationReports.Render(report, json: false);
        Assert.Equal($"src/thing.cpp:1: {Known}- (cut at the end of the line)", Assert.Single(outcome.Data));
        Assert.StartsWith("1 citation(s) are cut at the end of a line", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("add the row", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An id cut just before a hyphen - the next line opening with it - is reported as cut, as its line
    /// holds it, even though what it spells on its own is a row: read that way it resolved to a row it
    /// never meant, and the one it did mean went unchecked.
    /// </summary>
    [Fact]
    public async Task ACitationCutJustBeforeAHyphen_IsReportedAsCut_ThoughWhatItSpellsIsARow()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await WriteAnchorAsync(harness, temp, Known);
        await WriteAnchorAsync(harness, temp, Known + "-DETAIL");

        temp.WriteFile(Path.Combine("src", "thing.cpp"), $"// see {Known}\n// -DETAIL for the rest\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        var cut = Assert.Single(report.Unresolved);
        Assert.True(cut.Cut);
        Assert.Equal($"src/thing.cpp:1: {Known} (cut at the end of the line)", Assert.Single(AnchorCitationReports.Render(report, json: false).Data));
    }

    /// <summary>
    /// The headline says cut citations apart from those no row resolves, each with its own remedy. An
    /// id cut at its second hyphen is found too, where the next line carries it on.
    /// </summary>
    [Fact]
    public async Task TheHeadline_SaysCutCitationsApart_FromThoseNoRowResolves()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "thing.cpp"), "// D-AREA-TOPIC-MISSING, and a wrapped (D-PP-\n// PRESCAN)\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);
        var outcome = AnchorCitationReports.Render(report, json: false);

        Assert.Equal(
            ["src/thing.cpp:1: D-AREA-TOPIC-MISSING", "src/thing.cpp:1: D-PP- (cut at the end of the line)"],
            outcome.Data);
        Assert.StartsWith(
            "1 citation(s) of 1 anchor id(s) resolve to no row in either registry; add the row, or correct the citation. "
            + "1 citation(s) are cut at the end of a line, so none spells the id it was cut from; keep each id whole on one line. "
            + "Scanned ",
            outcome.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Two names that read alike are two files: one holding U+FFFD of its own, and one whose stray
    /// byte reads as U+FFFD. Taken for one, the second - listed after the first - went unread and
    /// unrefused.
    /// </summary>
    [Fact]
    public async Task TwoNamesThatReadAlike_AreTwoFiles()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        await harness.StageAsync(temp.Path, "\"src/caf\\357\\277\\275.cpp\"", "// D-AREA-TOPIC-OWN\n", cancellationToken);
        await harness.StageAsync(temp.Path, "\"src/caf\\377.cpp\"", "// D-AREA-TOPIC-STRAY\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "alike"], cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken));

        Assert.Contains(@"'src/caf\377.cpp' is not named in UTF-8", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file in conflict is scanned once: git lists it once for each side of the conflict, and scanned
    /// that many times, each citation in it was reported that many times.
    /// </summary>
    [Fact]
    public async Task AFileInConflict_IsScannedOnce()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        var thing = Path.Combine("src", "thing.cpp");

        temp.WriteFile(thing, "// D-AREA-TOPIC-MISSING\nbase\n");
        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "side"], cancellationToken);
        temp.WriteFile(thing, "// D-AREA-TOPIC-MISSING\nside\n");
        await harness.CommitAllAsync(temp.Path, "side", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-"], cancellationToken);
        temp.WriteFile(thing, "// D-AREA-TOPIC-MISSING\nmain\n");
        await harness.CommitAllAsync(temp.Path, "main", cancellationToken);

        var merge = await harness.GitClient.RunAsync(temp.Path, ["merge", "--quiet", "side"], cancellationToken: cancellationToken);
        Assert.False(merge.Succeeded, "the merge was to leave src/thing.cpp in conflict");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(["D-AREA-TOPIC-MISSING"], report.Unresolved.Select(citation => citation.Id));
    }

    /// <summary>
    /// A file in a root whose name is not UTF-8 refuses the check, naming it, however the files are
    /// read: no file opens by such a name here, and read as UTF-8 it became another name - one the
    /// commit "could not read", sending the reader to git fsck, and one the disk silently did not
    /// have. Outside every root it is no concern of the check - 'src' then a stray byte included,
    /// which a quoted escape's backslash put inside the root 'src'.
    /// </summary>
    [Fact]
    public async Task ANameThatIsNotUtf8_InARoot_RefusesTheCheck_HoweverTheFilesAreRead()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        await harness.StageAsync(temp.Path, "\"notes/caf\\351.md\"", "// D-AREA-TOPIC-ELSEWHERE\n", cancellationToken);
        await harness.StageAsync(temp.Path, "\"src\\351.bak\"", "// D-AREA-TOPIC-BESIDE\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "outside"], cancellationToken);

        Assert.True((await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken)).Passed);
        Assert.True((await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken)).Passed);

        await harness.StageAsync(temp.Path, "\"src/caf\\351.cpp\"", "// D-AREA-TOPIC-INSIDE\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "inside"], cancellationToken);

        foreach (var subject in new[] { AnchorCitationSubject.CurrentCommit, AnchorCitationSubject.CurrentTree })
        {
            var refusal = await Assert.ThrowsAsync<HarnessException>(
                () => Service(harness).CheckAsync(temp.Path, subject, cancellationToken));

            Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
            Assert.Contains(@"'src/caf\351.cpp' is not named in UTF-8", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A file the commit lists that git cannot read refuses the check, naming it: skipped, a file
    /// whose object was gone passed a check that read nothing in it.
    /// </summary>
    [Fact]
    public async Task AFileTheCommitListsButGitCannotRead_RefusesTheCheck()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "lost.cpp"), "// D-AREA-TOPIC-LOST\n");
        await harness.CommitAllAsync(temp.Path, "src", cancellationToken);
        await harness.LoseObjectAsync(temp.Path, "HEAD:src/lost.cpp", cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken));

        Assert.Contains("'src/lost.cpp'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("git fsck", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A submodule's entry names a commit in another repository, which is no file here: it is not
    /// read, and not mistaken for a file git could not read.
    /// </summary>
    [Fact]
    public async Task ASubmodulesEntry_IsNotReadAsAFile()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "thing.cpp"), "// D-AREA-TOPIC-THING\n");
        await harness.CommitAllAsync(temp.Path, "src", cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.GitClient.RunAsync(temp.Path, ["update-index", "--add", "--cacheinfo", $"160000,{head},src/sub"], cancellationToken: cancellationToken);
        await harness.GitClient.RunAsync(temp.Path, ["commit", "-q", "-m", "submodule"], cancellationToken: cancellationToken);

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken);

        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(["D-AREA-TOPIC-THING"], report.Unresolved.Select(citation => citation.Id));
    }

    /// <summary>
    /// A commit's files are read by one git process however many there are. Asked for one at a time,
    /// each cost two, and 2,385 files took twenty minutes where reading the disk took seconds.
    /// </summary>
    [Fact]
    public async Task ACommitsFiles_AreReadByOneGitProcess()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        for (var index = 0; index < 5; index++)
        {
            temp.WriteFile(Path.Combine("src", $"file{index}.cpp"), $"// D-AREA-TOPIC-FILE{index}\n");
        }

        await harness.CommitAllAsync(temp.Path, "src", cancellationToken);

        // Changed on disk since: the commit's own text is what a commit's check reads.
        temp.WriteFile(Path.Combine("src", "file0.cpp"), "// D-AREA-TOPIC-CHANGED\n");

        var processes = new CountingProcesses(harness.ProcessRunner);
        var service = new AnchorCitationService(
            harness.ContextLoader,
            harness.AnchorRegistryService,
            new GitClient(processes, harness.Output, localVariables: harness.LocalVariables),
            harness.FileSystem);

        var report = await service.CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken);

        Assert.Equal(5, report.FilesScanned);
        Assert.Equal(
            ["D-AREA-TOPIC-FILE0", "D-AREA-TOPIC-FILE1", "D-AREA-TOPIC-FILE2", "D-AREA-TOPIC-FILE3", "D-AREA-TOPIC-FILE4"],
            report.Unresolved.Select(citation => citation.Id).Order(StringComparer.Ordinal));
        Assert.Single(processes.Started, request => request.Arguments[0] == "cat-file");
        Assert.DoesNotContain(processes.Started, request => request.Arguments[0] == "show");
    }

    [Fact]
    public async Task ACitationOutsideEveryDeclaredRoot_IsNotScanned()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("notes", "scratch.md"), "// D-AREA-TOPIC-ELSEWHERE\n");
        temp.WriteFile("srcsibling.txt", "// D-AREA-TOPIC-SIBLING\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal(0, report.CitationsFound);
        Assert.Equal(0, report.FilesScanned);
    }

    [Fact]
    public async Task NoDeclaredRoot_IsRefused_RatherThanReportedAsAPass()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, []);

        temp.WriteFile(Path.Combine("src", "thing.cpp"), "// D-AREA-TOPIC-MISSING\n");

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("anchors.citationRoots", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCommit_AndTheTree_AnswerAboutDifferentFiles()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "committed.cpp"), "// D-AREA-TOPIC-COMMITTED\n");
        await harness.CommitAllAsync(temp.Path, "src", cancellationToken);

        temp.WriteFile(Path.Combine("src", "pending.cpp"), "// D-AREA-TOPIC-PENDING\n");

        var commit = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken);
        var tree = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);

        Assert.Equal(["D-AREA-TOPIC-COMMITTED"], commit.Unresolved.Select(citation => citation.Id));

        Assert.Equal(
            ["D-AREA-TOPIC-COMMITTED", "D-AREA-TOPIC-PENDING"],
            tree.Unresolved.Select(citation => citation.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheReport_NamesEveryUnresolvedCitation_AndFailsWithTheAnchorCode()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);

        temp.WriteFile(Path.Combine("src", "thing.cpp"), "// D-AREA-TOPIC-MISSING\n");

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentTree, cancellationToken);
        var outcome = AnchorCitationReports.Render(report, json: false);

        Assert.Equal(AnchorExit.Findings, outcome.ExitCode);
        Assert.Equal("src/thing.cpp:1: D-AREA-TOPIC-MISSING", Assert.Single(outcome.Data));
    }

    /// <summary>
    /// --current-pr reads what the branch changed since it left the default branch: not a file it never touched, even
    /// one the default branch changed after it left.
    /// </summary>
    [Fact]
    public async Task ThePullRequest_IsWhatTheBranchChangedSinceItLeftTheDefaultBranch()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await harness.RunGitAsync(temp.Path, ["branch", "-M", "main"], cancellationToken);
        temp.WriteFile(Path.Combine("src", "untouched.cpp"), "// D-AREA-TOPIC-UNTOUCHED\n");
        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);
        var left = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;

        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "work"], cancellationToken);
        temp.WriteFile(Path.Combine("src", "changed.cpp"), "// D-AREA-TOPIC-CHANGED\n");
        await harness.CommitAllAsync(temp.Path, "the branch", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "main"], cancellationToken);
        temp.WriteFile(Path.Combine("src", "untouched.cpp"), "// D-AREA-TOPIC-UNTOUCHED\n// edited on main\n");
        await harness.CommitAllAsync(temp.Path, "main moves on", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "work"], cancellationToken);

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentPullRequest, cancellationToken);

        Assert.Equal(["D-AREA-TOPIC-CHANGED"], report.Unresolved.Select(citation => citation.Id));
        Assert.Equal($"what this branch changed against main ({ReportText.Commit(left)})", report.SubjectDescription);
    }

    /// <summary>
    /// --current-pr refuses a HEAD it cannot tell a branch's change apart in: one that shares no history with the
    /// default branch, and one that names no commit yet - refused as --current-commit refuses it, and as the balance does.
    /// </summary>
    [Fact]
    public async Task ThePullRequest_IsRefused_WhereHeadSharesNoHistoryWithTheDefaultBranch_OrNamesNoCommit()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await harness.RunGitAsync(temp.Path, ["branch", "-M", "main"], cancellationToken);
        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);

        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "--orphan", "unrelated"], cancellationToken);

        var unborn = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentPullRequest, cancellationToken));

        Assert.Equal(HarnessExit.Refused, unborn.ExitCode);
        Assert.StartsWith("HEAD names no commit yet", unborn.Message, StringComparison.Ordinal);
        Assert.EndsWith("so nothing this branch changed can be told apart. Commit first, or use --current-tree.", unborn.Message, StringComparison.Ordinal);

        var noCommit = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentCommit, cancellationToken));

        Assert.Equal(HarnessExit.Refused, noCommit.ExitCode);
        Assert.StartsWith("HEAD names no commit yet", noCommit.Message, StringComparison.Ordinal);
        Assert.EndsWith("so there is no commit to check. Commit first, or use --current-tree.", noCommit.Message, StringComparison.Ordinal);

        await harness.CommitAllAsync(temp.Path, "no shared history", cancellationToken);

        var unrelated = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentPullRequest, cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, unrelated.ExitCode);
        Assert.StartsWith("HEAD shares no history with main", unrelated.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// During an unfinished merge the disk already holds what the merge brings in, so --current-pr measures the branch as
    /// the commit finishing the merge would be: a file only the default branch changed is not the branch's change.
    /// </summary>
    [Fact]
    public async Task ThePullRequest_DuringAnUnfinishedMerge_IsStillOnlyWhatTheBranchChanged()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await harness.RunGitAsync(temp.Path, ["branch", "-M", "main"], cancellationToken);
        temp.WriteFile(Path.Combine("src", "untouched.cpp"), "// D-AREA-TOPIC-UNTOUCHED\n");
        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);

        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "work"], cancellationToken);
        temp.WriteFile(Path.Combine("src", "changed.cpp"), "// D-AREA-TOPIC-CHANGED\n");
        await harness.CommitAllAsync(temp.Path, "the branch", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "main"], cancellationToken);
        temp.WriteFile(Path.Combine("src", "untouched.cpp"), "// D-AREA-TOPIC-UNTOUCHED\n// edited on main\n");
        await harness.CommitAllAsync(temp.Path, "main moves on", cancellationToken);
        var main = (await harness.GitClient.ResolveCommitAsync(temp.Path, "main", cancellationToken))!;
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "work"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["merge", "--quiet", "--no-commit", "--no-ff", "main"], cancellationToken);

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentPullRequest, cancellationToken);

        Assert.Equal(["D-AREA-TOPIC-CHANGED"], report.Unresolved.Select(citation => citation.Id));
        Assert.Equal($"what this branch changed against main ({ReportText.Commit(main)})", report.SubjectDescription);
    }

    /// <summary>
    /// A remote's HEAD still naming a branch that is not here, as after master became main, is passed over for the
    /// conventional names; where none of them resolves either, the refusal says what the remote's HEAD names, and how to
    /// set it again. Taken at its word, it failed in git's own words, naming a branch nobody typed.
    /// </summary>
    [Fact]
    public async Task ThePullRequest_PassesOverARemoteHeadNamingNoBranchHere()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, ["src"]);
        await harness.RunGitAsync(temp.Path, ["branch", "-M", "main"], cancellationToken);
        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);
        var main = (await harness.GitClient.ResolveCommitAsync(temp.Path, "main", cancellationToken))!;
        await harness.RunGitAsync(temp.Path, ["symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/master"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "work"], cancellationToken);

        var report = await Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentPullRequest, cancellationToken);

        Assert.Equal($"what this branch changed against main ({ReportText.Commit(main)})", report.SubjectDescription);

        await harness.RunGitAsync(temp.Path, ["branch", "-M", "main", "trunk"], cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).CheckAsync(temp.Path, AnchorCitationSubject.CurrentPullRequest, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("tried the remote's own HEAD, which names origin/master, a branch not here", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("git remote set-head origin --auto", refusal.Message, StringComparison.Ordinal);
    }

    private static AnchorCitationService Service(HarnessFactory harness)
        => new(harness.ContextLoader, harness.AnchorRegistryService, harness.GitClient, harness.FileSystem);

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, string[] roots)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Anchors = new AnchorSettings { CitationRoots = [.. roots] } });

        return harness;
    }

    private static Task WriteAnchorAsync(HarnessFactory harness, TempDirectory temp, string id)
        => harness.AnchorRegistryService.WriteAsync(
            temp.Path,
            new AnchorWriteRequest(id, "P1", "trigger"),
            dryRun: false,
            TestContext.Current.CancellationToken);
}
