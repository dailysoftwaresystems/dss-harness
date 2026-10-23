using DssHarness.Core.Git;

namespace DssHarness.Tests;

/// <summary>
/// The three calls that list a tree as it stands for the codebase graph, against real git: none takes the index's lock,
/// each reads what git says of every kind of path, and the blobs of many files are asked a batch at a time.
/// </summary>
public sealed class GitClientListingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The index lists each path once with its mode and object - a file, an executable, a link, a submodule's commit - and
    /// a conflicted path once, whatever its stages; asked without the index's lock.
    /// </summary>
    [Fact]
    public async Task TheIndex_ListsEachPathOnce_WithItsModeAndObject()
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

        var listed = (await Git(harness, processes).ListIndexedFilesAsync(temp.Path, Token)).ToDictionary(file => file.Name.Text);

        Assert.Equal(["README.md", "both.txt", "linked", "run.sh", "vendor/sub"], listed.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("100755", false, false), (listed["run.sh"].Mode, listed["run.sh"].IsLink, listed["run.sh"].IsSubmodule));
        Assert.True(listed["linked"].IsLink);
        Assert.Equal((true, head), (listed["vendor/sub"].IsSubmodule, listed["vendor/sub"].ObjectId));
        Assert.All(listed.Values, file => Assert.False(file.SkipWorktree));
        Assert.Equal(["--no-optional-locks", "ls-files", "-s", "-t", "-z"], Assert.Single(processes.Started).Arguments.TakeLast(5));
    }

    /// <summary>A path git keeps out of the checkout - marked skip-worktree, as a sparse checkout marks it - is listed so, and no other.</summary>
    [Fact]
    public async Task APathKeptOutOfTheCheckout_IsListedSo()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, Token);
        temp.WriteFile("kept.txt", "kept\n");
        temp.WriteFile("out.txt", "out\n");
        await harness.CommitAllAsync(temp.Path, "files", Token);
        await harness.RunGitAsync(temp.Path, ["update-index", "--skip-worktree", "out.txt"], Token);

        var listed = (await Git(harness, new CountingProcesses(harness.ProcessRunner)).ListIndexedFilesAsync(temp.Path, Token)).ToDictionary(file => file.Name.Text);

        Assert.True(listed["out.txt"].SkipWorktree);
        Assert.False(listed["kept.txt"].SkipWorktree);
        Assert.False(listed["README.md"].SkipWorktree);
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
        var stored = (await harness.GitClient.ListIndexedFilesAsync(temp.Path, Token)).ToDictionary(file => file.Name.Text, file => file.ObjectId);
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

        var listed = await Git(harness, new CountingProcesses(harness.ProcessRunner)).ListIndexedFilesAsync(temp.Path, Token);

        Assert.Equal(2, listed.Count(file => !file.Name.IsUtf8));
        Assert.Equal(2, listed.Where(file => !file.Name.IsUtf8).Select(file => file.Name.Quoted).Distinct(StringComparer.Ordinal).Count());
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
