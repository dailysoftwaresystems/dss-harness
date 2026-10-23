using DssHarness.Core.Git;

namespace DssHarness.Tests;

/// <summary>
/// The three calls that list a tree as it stands, against real git: none takes the index's lock, each reads what git says
/// of every kind of path, the paths a listing is given are named as they are spelt and batched as no command line
/// outgrows, and the blobs of many files are asked a batch at a time.
/// </summary>
public sealed class GitClientListingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The index lists each entry with its mode, object and stage - a file, an executable, a link, a submodule's commit -
    /// and a conflicted path once for each of its stages; asked without the index's lock.
    /// </summary>
    [Fact]
    public async Task TheIndex_ListsEachEntry_WithItsModeObjectAndStage()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var head = await HeadAsync(harness, temp.Path);
        temp.WriteFile("run.sh", "echo\n");
        temp.WriteFile("both.txt", "base\n");
        await harness.CommitAllAsync(temp.Path, "files", Token);
        await harness.RunGitAsync(temp.Path, ["update-index", "--chmod=+x", "run.sh"], Token);
        await harness.RunGitAsync(temp.Path, ["update-index", "--add", "--cacheinfo", $"120000,{(await harness.RunGitAsync(temp.Path, ["hash-object", "-w", "run.sh"], Token)).StandardOutput.Trim()},linked"], Token);
        await harness.RunGitAsync(temp.Path, ["update-index", "--add", "--cacheinfo", $"160000,{head},vendor/sub"], Token);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "kinds"], Token);
        await ConflictAsync(harness, temp, "both.txt");
        var processes = new CountingProcesses(harness.ProcessRunner);

        var entries = await Git(harness, processes).ListIndexAsync(temp.Path, Token);
        var listed = entries.Where(entry => entry.Stage == 0).ToDictionary(entry => entry.Path);

        Assert.Equal(["README.md", "linked", "run.sh", "vendor/sub"], listed.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([1, 2, 3], entries.Where(entry => entry.Path == "both.txt").Select(entry => entry.Stage));
        Assert.Equal(("100755", false, false), (listed["run.sh"].Mode, listed["run.sh"].IsLink, listed["run.sh"].IsSubmodule));
        Assert.True(listed["linked"].IsLink);
        Assert.Equal((true, head), (listed["vendor/sub"].IsSubmodule, listed["vendor/sub"].ObjectId));
        Assert.All(entries, entry => Assert.False(entry.IsSkipWorktree || entry.IsAssumedUnchanged));
        Assert.Equal(["--no-optional-locks", "ls-files", "--stage", "-v", "-z"], Assert.Single(processes.Started).Arguments.TakeLast(5));
    }

    /// <summary>
    /// A path whose file git does not look at - marked skip-worktree, as a sparse checkout marks it, or assume-unchanged -
    /// is listed so, each by its own mark, and no other.
    /// </summary>
    [Fact]
    public async Task APathWhoseFileGitDoesNotLookAt_IsListedSo()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        temp.WriteFile("kept.txt", "kept\n");
        temp.WriteFile("out.txt", "out\n");
        temp.WriteFile("assumed.txt", "assumed\n");
        await harness.CommitAllAsync(temp.Path, "files", Token);
        await harness.RunGitAsync(temp.Path, ["update-index", "--skip-worktree", "out.txt"], Token);
        await harness.RunGitAsync(temp.Path, ["update-index", "--assume-unchanged", "assumed.txt"], Token);

        var listed = (await Git(harness, new CountingProcesses(harness.ProcessRunner)).ListIndexAsync(temp.Path, Token)).ToDictionary(entry => entry.Path);

        Assert.Equal((true, false), (listed["out.txt"].IsSkipWorktree, listed["out.txt"].IsAssumedUnchanged));
        Assert.Equal((false, true), (listed["assumed.txt"].IsSkipWorktree, listed["assumed.txt"].IsAssumedUnchanged));
        Assert.All(new[] { "kept.txt", "README.md" }, path => Assert.False(listed[path].IsSkipWorktree || listed[path].IsAssumedUnchanged));
    }

    /// <summary>
    /// Paths given to a listing are each named as it is spelt, never read as a pattern: a name with brackets lists itself
    /// and not the name its brackets would match, and one that begins with a colon is a name and no pathspec magic.
    /// </summary>
    [Fact]
    public async Task PathsGivenToAListing_AreNamedAsTheyAreSpelt_NeverReadAsPatterns()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        temp.WriteFile("[a].cs", "bracketed\n");
        temp.WriteFile("a.cs", "plain\n");

        // A name that begins with a colon is made where the file system lets it be: Windows takes a colon for a stream.
        var colon = !OperatingSystem.IsWindows();

        if (colon)
        {
            temp.WriteFile(":x", "colon\n");
        }

        await harness.CommitAllAsync(temp.Path, "names", Token);
        temp.WriteFile("[a].cs", "bracketed again\n");
        temp.WriteFile("a.cs", "plain again\n");

        if (colon)
        {
            temp.WriteFile(":x", "colon again\n");
        }

        var git = Git(harness, new CountingProcesses(harness.ProcessRunner));
        string[] asked = colon ? ["[a].cs", ":x"] : ["[a].cs"];

        Assert.Equal(asked.Order(StringComparer.Ordinal), (await git.ListIndexAsync(temp.Path, asked, Token)).Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.Equal(asked.Order(StringComparer.Ordinal), (await git.ListWorkingFilesAsync(temp.Path, asked, Token)).Select(file => file.Name.Text).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Paths given to a listing travel as its arguments in batches no command line outgrows - as many as 8,000 characters
    /// of path make, not a count that assumes short names - and every path asked is listed, whichever batch held it.
    /// </summary>
    [Fact]
    public async Task PathsGivenToAListing_AreBatchedByWhatTheyCost_AndEachIsListed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);

        // 150 paths of 100 characters: 15,000 characters of path, which no count of 200 would cut.
        var paths = Enumerable.Range(0, 150).Select(at => $"d/{at:D3}-{new string('p', 94)}").ToList();

        foreach (var path in paths)
        {
            temp.WriteFile(path, "one\n");
        }

        await harness.CommitAllAsync(temp.Path, "long names", Token);

        foreach (var path in paths)
        {
            temp.WriteFile(path, "two\n");
        }

        var processes = new CountingProcesses(harness.ProcessRunner);
        var git = Git(harness, processes);

        var indexed = await git.ListIndexAsync(temp.Path, paths, Token);
        var working = await git.ListWorkingFilesAsync(temp.Path, paths, Token);

        Assert.Equal(paths, indexed.Select(entry => entry.Path).Order(StringComparer.Ordinal));
        Assert.Equal(paths, working.Select(file => file.Name.Text).Order(StringComparer.Ordinal));
        Assert.Equal(
            [("ls-files", 79), ("ls-files", 71), ("status", 79), ("status", 71)],
            processes.Started.Select(request => (request.Arguments[2], request.Arguments.Count - request.Arguments.ToList().IndexOf("--") - 1)));
    }

    /// <summary>
    /// Status lists every path whose working file is not the index's: changed, gone, untracked and conflicted ones each as
    /// such - a change staged and nothing more not differing, a file git ignores not at all - asked without the lock.
    /// </summary>
    [Fact]
    public async Task Status_ListsEveryPathWhoseWorkingFileIsNotTheIndexs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        temp.WriteFile(".gitignore", "*.log\n");
        temp.WriteFile("changed.txt", "one\n");
        temp.WriteFile("staged.txt", "one\n");
        temp.WriteFile("gone.txt", "one\n");
        temp.WriteFile("both.txt", "base\n");
        await harness.CommitAllAsync(temp.Path, "files", Token);
        await ConflictAsync(harness, temp, "both.txt");
        temp.WriteFile("changed.txt", "two\n");
        temp.WriteFile("staged.txt", "two\n");
        await harness.RunGitAsync(temp.Path, ["add", "staged.txt"], Token);
        File.Delete(temp.Combine("gone.txt"));
        temp.WriteFile("new dir/new.txt", "new\n");
        temp.WriteFile("noise.log", "noise\n");
        var processes = new CountingProcesses(harness.ProcessRunner);

        var working = (await Git(harness, processes).ListWorkingFilesAsync(temp.Path, Token)).ToDictionary(file => file.Name.Text);

        Assert.Equal(["both.txt", "changed.txt", "gone.txt", "new dir/new.txt", "staged.txt"], working.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((false, false, "100644", true), Facts(working["changed.txt"]));
        Assert.Equal((false, false, "100644", false), Facts(working["staged.txt"]));
        Assert.Equal((true, false, "000000", true), Facts(working["gone.txt"]));
        Assert.Equal((false, true, null, true), Facts(working["new dir/new.txt"]));
        Assert.Equal((false, false, "100644", true), Facts(working["both.txt"]));
        Assert.Equal(
            ["--no-optional-locks", "status", "--porcelain=v2", "-z", "--untracked-files=all", "--no-renames"],
            Assert.Single(processes.Started).Arguments.TakeLast(6));
    }

    /// <summary>
    /// The blobs of the working files are what git would store for each - its filters applied, a CR LF file's as its
    /// attributes say - asked a batch of 200 at a time, in order, nothing written, and without the lock.
    /// </summary>
    [Fact]
    public async Task TheWorkingFilesBlobs_AreAskedInBatches_AsGitWouldStoreThem()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        temp.WriteFile(".gitattributes", "*.txt text eol=lf\n");
        var paths = Enumerable.Range(0, 450).Select(at => $"f/{at:D4}.txt").ToList();

        foreach (var path in paths)
        {
            temp.WriteFile(path, $"line {path}\r\nnext\r\n");
        }

        var processes = new CountingProcesses(harness.ProcessRunner);

        var blobs = await Git(harness, processes).HashWorkingFilesAsync(temp.Path, paths, Token);

        await harness.RunGitAsync(temp.Path, ["add", "-A"], Token);
        var stored = (await harness.GitClient.ListIndexAsync(temp.Path, Token)).ToDictionary(entry => entry.Path, entry => entry.ObjectId);
        Assert.Equal(paths.Select(path => stored[path]), blobs);

        var hashed = processes.Started.Where(request => request.Arguments.Contains("hash-object")).ToList();
        Assert.Equal([200, 200, 50], hashed.Select(PathsGiven));
        Assert.All(hashed, request =>
        {
            Assert.Equal("--no-optional-locks", request.Arguments[request.Arguments.ToList().IndexOf("hash-object") - 1]);
            Assert.DoesNotContain("-w", request.Arguments);
            Assert.DoesNotContain("--no-filters", request.Arguments);
        });
    }

    /// <summary>
    /// A file not there when git hashed its batch answers null and fails nothing else: the batch git stopped in is asked
    /// again once without the files found gone, every other file's blob given in order; a batch whose files are all there
    /// is asked once.
    /// </summary>
    [Fact]
    public async Task AFileNotThereWhenGitHashedIt_AnswersNull_AndEveryOtherIsHashed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var paths = Enumerable.Range(0, 203).Select(at => $"f/{at:D3}.txt").ToList();

        foreach (var path in paths.Where(path => path != "f/001.txt"))
        {
            temp.WriteFile(path, $"text {path}\n");
        }

        var processes = new CountingProcesses(harness.ProcessRunner);

        var blobs = await Git(harness, processes).HashWorkingFilesAsync(temp.Path, paths, Token);

        await harness.RunGitAsync(temp.Path, ["add", "-A"], Token);
        var stored = (await harness.GitClient.ListIndexAsync(temp.Path, Token)).ToDictionary(entry => entry.Path, entry => entry.ObjectId);
        Assert.Equal(paths.Select(path => stored.GetValueOrDefault(path)), blobs);
        Assert.Null(blobs[1]);
        Assert.Equal([200, 199, 3], processes.Started.Where(request => request.Arguments.Contains("hash-object")).Select(PathsGiven));
    }

    /// <summary>
    /// A batch asked again without the files found gone, that git cannot hash either - another went meanwhile - is asked a
    /// file at a time: each file gone answers null, every other its blob.
    /// </summary>
    [Fact]
    public async Task ABatchThatLosesAnotherFileWhenAskedAgain_IsAskedAFileAtATime()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        var paths = Enumerable.Range(0, 5).Select(at => $"f/{at}.txt").ToList();

        foreach (var path in paths.Where(path => path != "f/1.txt"))
        {
            temp.WriteFile(path, $"text {path}\n");
        }

        var processes = new CountingProcesses(new GoneAtTheSecondHash(harness.ProcessRunner, Path.Combine(temp.Path, "f", "3.txt")));

        var blobs = await Git(harness, processes).HashWorkingFilesAsync(temp.Path, paths, Token);

        await harness.RunGitAsync(temp.Path, ["add", "-A"], Token);
        var stored = (await harness.GitClient.ListIndexAsync(temp.Path, Token)).ToDictionary(entry => entry.Path, entry => entry.ObjectId);
        Assert.Equal(paths.Select(path => stored.GetValueOrDefault(path)), blobs);
        Assert.Equal([1, 3], Enumerable.Range(0, blobs.Count).Where(at => blobs[at] is null));
        Assert.Equal([5, 4, 1, 1, 1, 1], processes.Started.Where(request => request.Arguments.Contains("hash-object")).Select(PathsGiven));
    }

    /// <summary>Runs every program through <paramref name="inner"/>, and deletes <paramref name="file"/> as the second <c>hash-object</c> starts.</summary>
    private sealed class GoneAtTheSecondHash(DssHarness.Core.Processes.IProcessRunner inner, string file) : DssHarness.Core.Processes.IProcessRunner
    {
        private int _hashes;

        public Task<DssHarness.Core.Processes.ProcessResult> RunAsync(DssHarness.Core.Processes.ProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Arguments.Contains("hash-object") && Interlocked.Increment(ref _hashes) == 2)
            {
                File.Delete(file);
            }

            return inner.RunAsync(request, cancellationToken);
        }

        public string? FindExecutable(string command) => inner.FindExecutable(command);
    }

    /// <summary>Two names that are no UTF-8, alike once decoded, are each listed: told apart by their bytes.</summary>
    [Fact]
    public async Task TwoNamesThatAreNoUtf8_AreListedApart_ThoughTheyDecodeAlike()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a file named in no UTF-8 is made through Linux's file system alone.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);

        using (var shell = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sh", ["-c", "printf 'a\\n' > \"$(printf 'caf\\351')\" && printf 'b\\n' > \"$(printf 'caf\\350')\""]) { WorkingDirectory = temp.Path })!)
        {
            shell.WaitForExit();
            Assert.Equal(0, shell.ExitCode);
        }

        await harness.CommitAllAsync(temp.Path, "names", Token);

        var listed = await Git(harness, new CountingProcesses(harness.ProcessRunner)).ListIndexAsync(temp.Path, Token);

        Assert.Equal(2, listed.Count(entry => !entry.Name.IsUtf8));
        Assert.Equal(2, listed.Where(entry => !entry.Name.IsUtf8).Select(entry => entry.Name.Quoted).Distinct(StringComparer.Ordinal).Count());
    }

    private static GitClient Git(HarnessFactory harness, CountingProcesses processes) => new(processes, harness.Output, localVariables: harness.LocalVariables);

    /// <summary>How many paths a <c>hash-object --stdin-paths</c> was given on its standard input.</summary>
    private static int PathsGiven(DssHarness.Core.Processes.ProcessRequest request)
    {
        using var given = new MemoryStream();
        request.StandardInput!.WriteTo(given);
        return System.Text.Encoding.UTF8.GetString(given.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static async Task<string> HeadAsync(HarnessFactory harness, string directory)
        => (await harness.RunGitAsync(directory, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();

    /// <summary>Leaves <paramref name="path"/> conflicted: changed one way on a branch and another on the one checked out, and merged.</summary>
    private static async Task ConflictAsync(HarnessFactory harness, TempDirectory temp, string path)
    {
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "other"], Token);
        temp.WriteFile(path, "theirs\n");
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "theirs", "--", path], Token);
        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-"], Token);
        temp.WriteFile(path, "ours\n");
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "ours", "--", path], Token);
        var merged = await harness.GitClient.RunAsync(temp.Path, ["merge", "--quiet", "other"], cancellationToken: Token);
        Assert.False(merged.Succeeded);
    }

    private static (bool Gone, bool Untracked, string? Mode, bool Differs) Facts(GitWorkingFile file) => (file.Gone, file.Untracked, file.Mode, file.Differs);
}
