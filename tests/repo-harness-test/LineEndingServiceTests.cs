using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.LineEndings;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// fix-line-endings against a real repository and a real <c>.gitattributes</c>. The policy is never
/// restated in the test either: every expectation below is what the attributes file declares, read
/// back through git, so a test that passed while the tool invented its own policy is not possible.
/// </summary>
public sealed class LineEndingServiceTests
{
    private const string Attributes =
        """
        *.txt text eol=lf
        *.win text eol=crlf
        *.blob -text
        """;

    [Fact]
    public async Task All_RewritesExactlyTheFilesTheAttributesFileNames()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var report = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.All, CheckOnly: false),
            cancellationToken);

        Assert.Equal(
            ["declared.txt", "declared.win"],
            report.Changed.Select(change => change.Path).Order(StringComparer.Ordinal));

        Assert.Equal("one\ntwo\n", Read(temp, "declared.txt"));
        Assert.Equal("one\r\ntwo\r\n", Read(temp, "declared.win"));

        // Declared binary, and undeclared: neither is the policy's business, so neither moved.
        Assert.Equal("one\r\ntwo\r\n", Read(temp, "kept.blob"));
        Assert.Equal("one\r\ntwo\r\n", Read(temp, "undeclared.dat"));
    }

    /// <summary>
    /// The document and the lines say each scope and each ending as their names, a document stopped before it read
    /// anything names no scope and says the check its command line asked, and a scope or an ending this build does not
    /// know is refused rather than said as another.
    /// </summary>
    [Fact]
    public void TheReportSaysEachScopeAndEachEnding_AndRefusesOneThisBuildDoesNotKnow()
    {
        static LineEndingReport Report(LineEndingScope scope, params DeclaredLineEnding[] endings)
            => new(scope, CheckOnly: false, endings.Length, 0, 0, 0, [.. endings.Select((ending, at) => new LineEndingChange($"f{at}", ending, 0))]);

        static System.Text.Json.Nodes.JsonObject Document(LineEndingReport report)
            => System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(LineEndingReports.Render(report, json: true).Data))!.AsObject();

        Assert.Equal("all", Document(Report(LineEndingScope.All))["scope"]!.GetValue<string>());
        Assert.Equal("changed", Document(Report(LineEndingScope.Changed))["scope"]!.GetValue<string>());
        Assert.Equal(
            ["f0   -> undeclared", "f1   -> binary", "f2   -> LF", "f3   -> CRLF"],
            LineEndingReports.Render(Report(LineEndingScope.All, DeclaredLineEnding.None, DeclaredLineEnding.Binary, DeclaredLineEnding.Lf, DeclaredLineEnding.Crlf), json: false).Data);

        var stopped = System.Text.Json.Nodes.JsonNode.Parse(LineEndingReports.Stopped(HarnessExit.UsageError, "stopped", checkOnly: true))!.AsObject();

        Assert.Null(stopped["scope"]);
        Assert.True(stopped["check"]!.GetValue<bool>());

        Assert.Throws<ArgumentOutOfRangeException>(() => LineEndingReports.Render(Report((LineEndingScope)99), json: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => LineEndingReports.Render(Report(LineEndingScope.All, (DeclaredLineEnding)99), json: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => LineEndingReports.Render(Report(LineEndingScope.All, (DeclaredLineEnding)99), json: true));
    }

    [Fact]
    public async Task Check_RefusesAndWritesNothing()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        var report = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.All, CheckOnly: true),
            cancellationToken);

        var outcome = LineEndingReports.Render(report, json: false);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(2, report.Changed.Count);

        // The refusal is the whole of what --check does: the bytes on disk are untouched.
        Assert.Equal("one\r\ntwo\r\n", Read(temp, "declared.txt"));
        Assert.Equal("one\ntwo\n", Read(temp, "declared.win"));
    }

    [Fact]
    public async Task ASecondRun_ChangesNothing_AndSaysSo()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var service = Service(harness);
        var request = new LineEndingRequest(LineEndingScope.All, CheckOnly: false);

        await service.ApplyAsync(temp.Path, request, cancellationToken);
        var again = await service.ApplyAsync(temp.Path, request, cancellationToken);

        Assert.Empty(again.Changed);
        Assert.Equal(HarnessExit.Success, LineEndingReports.Render(again, json: false).ExitCode);
    }

    [Fact]
    public async Task AnExcludedPath_IsLeftAlone()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, new LineEndingSettings { Exclude = ["generated"] });

        var report = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.All, CheckOnly: false),
            cancellationToken);

        Assert.DoesNotContain("generated/made.txt", report.Changed.Select(change => change.Path));
        Assert.Equal("one\r\ntwo\r\n", Read(temp, Path.Combine("generated", "made.txt")));
        Assert.True(report.Excluded > 0);
    }

    [Fact]
    public async Task Changed_CoversTheWorkingSetAndNothingElse()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        // Everything is committed, so nothing is staged or unstaged and the working set is empty.
        await harness.CommitAllAsync(temp.Path, "fixtures", cancellationToken);

        var settled = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.Changed, CheckOnly: false),
            cancellationToken);

        Assert.Empty(settled.Changed);

        File.WriteAllText(temp.Combine("declared.txt"), "three\r\nfour\r\n");

        var working = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.Changed, CheckOnly: false),
            cancellationToken);

        Assert.Equal("declared.txt", Assert.Single(working.Changed).Path);
        Assert.Equal("three\nfour\n", Read(temp, "declared.txt"));
    }

    /// <summary>
    /// A file the policy reaches whose name is not UTF-8 refuses the run, naming it: read as UTF-8, its
    /// name became another, which the disk did not have, and the file was passed over without a word.
    /// Excluded, it is no concern of the run - but 'generated' then a stray byte is not under
    /// 'generated', where a quoted escape's backslash put it, excluding it unread.
    /// </summary>
    [Fact]
    public async Task ANameThatIsNotUtf8_IsRefused_UnlessExcluded()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, new LineEndingSettings { Exclude = ["generated"] });
        var check = new LineEndingRequest(LineEndingScope.All, CheckOnly: true);

        await harness.StageAsync(temp.Path, "\"generated/caf\\351.txt\"", "one\r\ntwo\r\n", cancellationToken);

        Assert.Equal(
            ["declared.txt", "declared.win"],
            (await Service(harness).ApplyAsync(temp.Path, check, cancellationToken)).Changed.Select(change => change.Path).Order(StringComparer.Ordinal));

        await harness.StageAsync(temp.Path, "\"generated\\240old/caf.txt\"", "one\r\ntwo\r\n", cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(harness).ApplyAsync(temp.Path, check, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains(@"'generated\240old/caf.txt' is not named in UTF-8", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two names that read alike are two files: one holding U+FFFD of its own, and one whose stray
    /// byte reads as U+FFFD. Taken for one, the second - listed after the first - went unchecked and
    /// unrefused.
    /// </summary>
    [Theory]
    [InlineData(LineEndingScope.All)]
    [InlineData(LineEndingScope.Changed)]
    public async Task TwoNamesThatReadAlike_AreTwoFiles(LineEndingScope scope)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);

        await harness.StageAsync(temp.Path, "\"caf\\357\\277\\275.txt\"", "one\r\ntwo\r\n", cancellationToken);
        await harness.StageAsync(temp.Path, "\"caf\\377.txt\"", "one\r\ntwo\r\n", cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(scope, CheckOnly: true),
            cancellationToken));

        Assert.Contains(@"'caf\377.txt' is not named in UTF-8", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file in conflict is one file: git lists it once for each side of the conflict, and each
    /// listing was checked, and reported, as a file of its own.
    /// </summary>
    [Fact]
    public async Task AFileInConflict_IsOneFile()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.CommitAllAsync(temp.Path, "fixtures", cancellationToken);

        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "side"], cancellationToken);
        Write(temp, "declared.txt", "side\n");
        await harness.CommitAllAsync(temp.Path, "side", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-"], cancellationToken);
        Write(temp, "declared.txt", "main\n");
        await harness.CommitAllAsync(temp.Path, "main", cancellationToken);

        var merge = await harness.GitClient.RunAsync(temp.Path, ["merge", "--quiet", "side"], cancellationToken: cancellationToken);
        Assert.False(merge.Succeeded, "the merge was to leave declared.txt in conflict");

        // Resolved in the editor with the wrong line endings, and not yet added.
        Write(temp, "declared.txt", "main\r\nside\r\n");

        var report = await Service(harness).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.All, CheckOnly: true),
            cancellationToken);

        Assert.Equal("declared.txt", Assert.Single(report.Changed, change => change.Path == "declared.txt").Path);
    }

    /// <summary>
    /// Where git cannot say what the attributes declare, the refusal says so in the words it always has - which git
    /// command failed, what that left undone, and git's own reason - and no file is rewritten.
    /// </summary>
    [Fact]
    public async Task APolicyGitCannotRead_IsRefusedInItsOwnWords_AndNothingIsRewritten()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var git = new GitClient(new UnansweredCheckAttr(harness.ProcessRunner), harness.Output, localVariables: harness.LocalVariables);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => new LineEndingService(harness.ContextLoader, git, harness.FileSystem).ApplyAsync(
            temp.Path,
            new LineEndingRequest(LineEndingScope.All, CheckOnly: false),
            cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.Equal(
            $"git check-attr failed, so the declared policy could not be read and nothing was rewritten: {UnansweredCheckAttr.Reason}",
            refusal.Message);
        Assert.Equal("one\r\ntwo\r\n", Read(temp, "declared.txt"));
    }

    /// <summary>Runs every program as it is, but answers <c>git check-attr</c> with a failure of its own.</summary>
    private sealed class UnansweredCheckAttr(IProcessRunner inner) : IProcessRunner
    {
        public const string Reason = "fatal: the attributes could not be read";

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
            => request.Arguments.Contains("check-attr")
                ? Task.FromResult(new ProcessResult(128, string.Empty, Reason, TimeSpan.Zero, TimedOut: false))
                : inner.RunAsync(request, cancellationToken);

        public string? FindExecutable(string command) => inner.FindExecutable(command);
    }

    private static LineEndingService Service(HarnessFactory harness)
        => new(harness.ContextLoader, harness.GitClient, harness.FileSystem);

    private static string Read(TempDirectory temp, string relativePath)
        => File.ReadAllText(temp.Combine(relativePath));

    private static async Task<HarnessFactory> PrepareAsync(
        TempDirectory temp,
        LineEndingSettings? lineEndings = null)
    {
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(
            temp.Path,
            cancellationToken,
            new HarnessConfig { LineEndings = lineEndings ?? new LineEndingSettings() });

        Write(temp, ".gitattributes", Attributes + "\n");

        // Each fixture starts on the wrong side of what the attributes file declares for it.
        Write(temp, "declared.txt", "one\r\ntwo\r\n");
        Write(temp, "declared.win", "one\ntwo\n");
        Write(temp, "kept.blob", "one\r\ntwo\r\n");
        Write(temp, "undeclared.dat", "one\r\ntwo\r\n");

        // Only where a test declares an exclusion: otherwise it is an ordinary .txt the policy reaches.
        if (lineEndings is not null)
        {
            Write(temp, Path.Combine("generated", "made.txt"), "one\r\ntwo\r\n");
        }

        // Tracked, because --all covers what git tracks. Adding does not touch the working tree.
        await harness.RunGitAsync(temp.Path, ["add", "-A"], cancellationToken);

        return harness;
    }

    /// <summary>Writes exactly these bytes, so no writer's own newline handling changes the fixture.</summary>
    private static void Write(TempDirectory temp, string relativePath, string contents)
    {
        var full = temp.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(contents));
    }
}
