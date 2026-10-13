using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>The git client against real git and real repositories.</summary>
public sealed class GitClientTests
{
    [Fact]
    public void IsInstalled_FindsGit()
    {
        Assert.True(new HarnessFactory().GitClient.IsInstalled());
    }

    [Fact]
    public async Task RepositoryQueries_AnswerNotARepository_OutsideOne()
    {
        using var temp = new TempDirectory();
        var git = new HarnessFactory().GitClient;
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.False(await git.IsRepositoryAsync(temp.Path, cancellationToken));
        Assert.Null(await git.GetRepositoryRootAsync(temp.Path, cancellationToken));
        Assert.Null(await git.GetMainWorktreeAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task GetRepositoryRootAsync_FindsTheRoot_FromASubdirectory()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        var nested = Directory.CreateDirectory(temp.Combine("a", "b")).FullName;

        var root = await harness.GitClient.GetRepositoryRootAsync(nested, cancellationToken);

        Assert.NotNull(root);
        PathAssert.Same(temp.Path, root);
        Assert.True(await harness.GitClient.IsRepositoryAsync(nested, cancellationToken));
    }

    [Fact]
    public async Task GetMainWorktreeAsync_FindsTheMainCheckout_FromInsideALinkedWorktree()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(repository.Path, cancellationToken);

        var linked = elsewhere.Combine("linked");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", linked], cancellationToken);

        var main = await harness.GitClient.GetMainWorktreeAsync(linked, cancellationToken);
        var linkedRoot = await harness.GitClient.GetRepositoryRootAsync(linked, cancellationToken);

        Assert.NotNull(main);
        Assert.True(main.IsMain);
        Assert.False(main.IsBare);
        PathAssert.Same(repository.Path, main.Path);

        Assert.NotNull(linkedRoot);
        PathAssert.Same(linked, linkedRoot);
    }

    /// <summary>
    /// A submodule's main checkout is its own - never the git directory its superproject keeps it in under
    /// .git/modules, which git names as the main worktree - from the submodule and from a linked worktree
    /// of it alike, as that directory's configuration records it.
    /// </summary>
    [Fact]
    public async Task GetMainWorktreeAsync_FindsASubmodulesOwnCheckout_NotTheGitDirectoryGitNames()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var submodule = await AddSubmoduleAsync(harness, temp, cancellationToken);
        var linked = temp.Combine("linked");

        await harness.RunGitAsync(submodule, ["worktree", "add", "--detach", linked], cancellationToken);

        foreach (var from in new[] { submodule, linked })
        {
            var main = await harness.GitClient.GetMainWorktreeAsync(from, cancellationToken);

            Assert.NotNull(main);
            Assert.True(main.IsMain);
            PathAssert.Same(submodule, main.Path);
        }
    }

    /// <summary>
    /// A checkout whose git directory was made apart from it, with --separate-git-dir, is its own main
    /// checkout, though git names that directory as the main worktree and records no checkout for it. From
    /// a linked worktree of it, nothing says which checkout is the main one, and none is named.
    /// </summary>
    [Fact]
    public async Task GetMainWorktreeAsync_FindsACheckoutWhoseGitDirectoryIsApart_AsItsOwnMain()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var checkout = Directory.CreateDirectory(temp.Combine("checkout")).FullName;
        var linked = temp.Combine("linked");

        await harness.RunGitAsync(checkout, ["init", "--quiet", $"--separate-git-dir={temp.Combine("git")}", "."], cancellationToken);
        await harness.RunGitAsync(
            checkout,
            ["-c", "user.name=Harness Test", "-c", "user.email=harness@test.invalid", "commit", "--quiet", "--allow-empty", "-m", "initial"],
            cancellationToken);

        var main = await harness.GitClient.GetMainWorktreeAsync(checkout, cancellationToken);

        Assert.NotNull(main);
        PathAssert.Same(checkout, main.Path);

        await harness.RunGitAsync(checkout, ["worktree", "add", "--detach", linked], cancellationToken);

        Assert.Null(await harness.GitClient.GetMainWorktreeAsync(linked, cancellationToken));
    }

    /// <summary>
    /// Adds a repository as the submodule <c>sub</c> of another, both under <paramref name="temp"/>, and
    /// returns the submodule's checkout.
    /// </summary>
    internal static async Task<string> AddSubmoduleAsync(HarnessFactory harness, TempDirectory temp, CancellationToken cancellationToken)
    {
        var inner = Directory.CreateDirectory(temp.Combine("inner")).FullName;
        var super = Directory.CreateDirectory(temp.Combine("super")).FullName;

        await harness.InitializeGitRepositoryAsync(inner, cancellationToken);
        await harness.InitializeGitRepositoryAsync(super, cancellationToken);

        // git refuses a submodule from a local path unless told to allow the file protocol.
        await harness.RunGitAsync(super, ["-c", "protocol.file.allow=always", "submodule", "add", "--quiet", inner, "sub"], cancellationToken);

        return Path.Combine(super, "sub");
    }

    [Fact]
    public async Task Roots_AreReportedInOneForm_WhenTheRepositoryIsReachedThroughASymbolicLink()
    {
        // The tree root and the main checkout root are compared with each other. Were one
        // spelled through the link and the other resolved, the main checkout would look
        // like a worktree of itself; macOS's temporary directory, under /var, is such a link.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var real = Directory.CreateDirectory(temp.Combine("real")).FullName;
        await harness.InitializeGitRepositoryAsync(real, cancellationToken);

        var link = temp.Combine("link");
        TestLinks.OrSkip(() => Directory.CreateSymbolicLink(link, real));

        var root = await harness.GitClient.GetRepositoryRootAsync(link, cancellationToken);
        var main = await harness.GitClient.GetMainWorktreeAsync(link, cancellationToken);

        Assert.NotNull(root);
        Assert.NotNull(main);
        PathAssert.Same(root, main.Path);
    }

    [Fact]
    public async Task Roots_AreReportedExactly_ForAPathOutsideAscii()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = Directory.CreateDirectory(temp.Combine("ação-日本")).FullName;
        await harness.InitializeGitRepositoryAsync(repository, cancellationToken);

        var root = await harness.GitClient.GetRepositoryRootAsync(repository, cancellationToken);
        var main = await harness.GitClient.GetMainWorktreeAsync(repository, cancellationToken);

        Assert.NotNull(root);
        Assert.NotNull(main);
        PathAssert.Same(repository, root);
        PathAssert.Same(repository, main.Path);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsNothing_ForACleanTree()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        Assert.Empty(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));
        Assert.False(await harness.GitClient.IsDirtyAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task GetStatusAsync_ReportsARename_AsOneChange()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.RunGitAsync(temp.Path, ["mv", "README.md", "RENAMED.md"], cancellationToken);

        var entry = Assert.Single(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));

        Assert.StartsWith("R", entry.Line, StringComparison.Ordinal);
        Assert.EndsWith("RENAMED.md", entry.Line, StringComparison.Ordinal);
        Assert.True(await harness.GitClient.IsDirtyAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task GetStatusAsync_KeepsAnUnusualPathIntact()
    {
        // Without -z git quotes such a path and escapes every byte outside ASCII.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("ação 'quoted' file.txt", "x");

        var entry = Assert.Single(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));

        Assert.Equal("?? ação 'quoted' file.txt", entry.Line);
    }

    [Fact]
    public async Task GetStatusAsync_ListsAnUntrackedFile_WhenConfigurationHidesThem()
    {
        // Under status.showUntrackedFiles=no a plain status lists nothing for a new file, and a
        // caller about to discard the tree would read it as clean.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.RunGitAsync(temp.Path, ["config", "status.showUntrackedFiles", "no"], cancellationToken);
        temp.WriteFile("notes.txt", "never committed");

        var entry = Assert.Single(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));

        Assert.Equal("?? notes.txt", entry.Line);
        Assert.True(await harness.GitClient.IsDirtyAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task GetStatusAsync_ReportsARenameInTheWorkTree_AsOneChange()
    {
        // git marks a rename it finds in the work tree, here of an intent-to-add file, in the
        // second status column rather than the first, and still follows it with the old path.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        File.Move(temp.Combine("README.md"), temp.Combine("RENAMED.md"));
        await harness.RunGitAsync(temp.Path, ["add", "--intent-to-add", "RENAMED.md"], cancellationToken);

        var entry = Assert.Single(await harness.GitClient.GetStatusAsync(temp.Path, cancellationToken));

        Assert.Equal(" R RENAMED.md", entry.Line);
    }

    [Fact]
    public async Task GetStatusAsync_Throws_WhenGitCannotAnswer()
    {
        // An empty answer here would read as a clean tree, and a caller would proceed
        // over work it never saw.
        using var temp = new TempDirectory();

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.GetStatusAsync(temp.Path, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
    }

    /// <summary>
    /// What the work tree changes since a commit is asked as git status compares: an edit, a deletion and a staged change
    /// are named; an untracked file is not; and a file committed with carriage returns, left alone in a repository that
    /// converts line endings, is no change.
    /// </summary>
    [Fact]
    public async Task ListChangedSinceAsync_ComparesAsGitStatusDoes()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("edited.txt", "one\n");
        temp.WriteFile("deleted.txt", "one\n");
        temp.WriteFile("crlf.txt", "one\r\n");
        await harness.RunGitAsync(temp.Path, ["-c", "core.autocrlf=false", "add", "."], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["-c", "core.autocrlf=false", "commit", "-q", "-m", "files"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["config", "core.autocrlf", "true"], cancellationToken);
        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        temp.WriteFile("edited.txt", "one\ntwo\n");
        File.Delete(temp.Combine("deleted.txt"));
        temp.WriteFile("untracked.txt", "new\n");

        var changed = await harness.GitClient.ListChangedSinceAsync(temp.Path, head, cancellationToken);

        Assert.Equal(["deleted.txt", "edited.txt"], changed.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Checking paths out at a commit writes each as the commit holds it and removes each it does not hold, reads every path
    /// literally - a name that reads as a wildcard matches only itself - and leaves every other path, and HEAD, alone.
    /// </summary>
    [Fact]
    public async Task CheckOutAtAsync_WritesExactlyThePathsNamed_AsTheCommitHoldsThem()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("a[x]b.txt", "one\n");
        temp.WriteFile("axb.txt", "one\n");
        await harness.CommitAllAsync(temp.Path, "files", cancellationToken);
        var first = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        temp.WriteFile("a[x]b.txt", "two\n");
        temp.WriteFile("axb.txt", "two\n");
        temp.WriteFile("new.txt", "new\n");
        await harness.CommitAllAsync(temp.Path, "second", cancellationToken);
        var second = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;

        await harness.GitClient.CheckOutAtAsync(temp.Path, first, ["a[x]b.txt", "new.txt"], cancellationToken);

        Assert.Equal("one\n", File.ReadAllText(temp.Combine("a[x]b.txt")));
        Assert.Equal("two\n", File.ReadAllText(temp.Combine("axb.txt")));
        Assert.False(File.Exists(temp.Combine("new.txt")));
        Assert.Equal(second, await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken));
    }

    /// <summary>
    /// Which files a work tree holds as a commit holds them is asked of git as its status would compare them were the
    /// commit's entries the index's: a file never added that holds the commit's bytes is one, and so is one checked out
    /// with other line endings where the repository converts them; a file a byte off, a path the work tree does not hold,
    /// and a path the commit holds no file at are not. The work tree's own index is neither read nor written, and nothing
    /// is left behind.
    /// </summary>
    [Fact]
    public async Task ListHeldAsAtAsync_ComparesAsStatusWould_WhateverTheIndexHolds_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(".gitattributes", "*.txt text eol=lf\n");
        temp.WriteFile("keep.txt", "kept\n");
        await harness.CommitAllAsync(temp.Path, "first", cancellationToken);
        var first = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        temp.WriteFile("same.txt", "one\ntwo\n");
        temp.WriteFile("endings.txt", "one\ntwo\n");
        temp.WriteFile("near.txt", "one\ntwo\n");
        temp.WriteFile("gone.txt", "one\n");
        temp.WriteFile(Path.Combine("docs", "x.txt"), "x\n");
        await harness.CommitAllAsync(temp.Path, "second", cancellationToken);
        var second = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;

        // Back on the first commit, with the second's files left in the work tree, untracked.
        await harness.GitClient.ResetToAsync(temp.Path, first, cancellationToken);
        temp.WriteFile("endings.txt", "one\r\ntwo\r\n");
        temp.WriteFile("near.txt", "one\ntwo\n.");
        File.Delete(temp.Combine("gone.txt"));
        var index = await harness.GitClient.GetIndexFileAsync(temp.Path, cancellationToken);
        var before = File.ReadAllBytes(index);
        var listed = Directory.GetFiles(Path.GetDirectoryName(index)!);

        var held = await harness.GitClient.ListHeldAsAtAsync(temp.Path, second, ["same.txt", "endings.txt", "near.txt", "gone.txt", "docs", "never.txt", "docs/x.txt"], cancellationToken);

        Assert.Equal(["docs/x.txt", "endings.txt", "same.txt"], held.Order(StringComparer.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(index));
        Assert.Equal(listed, Directory.GetFiles(Path.GetDirectoryName(index)!));
        Assert.Empty(await harness.GitClient.ListHeldAsAtAsync(temp.Path, second, [], cancellationToken));
    }

    /// <summary>
    /// A path holding letters outside ASCII, or a space, is compared like any other: it is written into the question's
    /// index and read back from git's answer byte for byte, so a file held as the commit holds it is listed, and one a
    /// byte off is not, whatever its name.
    /// </summary>
    [Fact]
    public async Task ListHeldAsAtAsync_ComparesAPathHoldingLettersOutsideAscii_AsAnyOther()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        const string Same = "relat\u00f3rios/medi\u00e7\u00e3o final.txt";
        const string Changed = "relat\u00f3rios/\u65e5\u672c\u8a9e.txt";

        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("keep.txt", "kept\n");
        await harness.CommitAllAsync(temp.Path, "first", cancellationToken);
        var first = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        temp.WriteFile(Same, "one\n");
        temp.WriteFile(Changed, "one\n");
        await harness.CommitAllAsync(temp.Path, "second", cancellationToken);
        var second = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;

        await harness.GitClient.ResetToAsync(temp.Path, first, cancellationToken);
        temp.WriteFile(Changed, "one\n.");

        var held = await harness.GitClient.ListHeldAsAtAsync(temp.Path, second, [Same, Changed], cancellationToken);

        Assert.Equal([Same], held);
    }

    /// <summary>
    /// The index a comparison writes for itself is removed where it can be, and where it cannot nothing is raised for
    /// it: raised as the comparison ended, it took the place of whatever the comparison had failed with, and failed one
    /// that had been made.
    /// </summary>
    [Fact]
    public void AQuestionsOwnTemporaryFile_ThatCannotBeRemoved_RaisesNothing()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("held", "dssharness-index-test");
        temp.WriteFile(Path.Combine("held", "dssharness-index-test"), "index");

        if (OperatingSystem.IsWindows())
        {
            // Open with no sharing: Windows deletes no file another handle holds so.
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                GitClient.Discard(file);
                Assert.True(File.Exists(file));
            }
        }
        else
        {
            Assert.SkipWhen(Environment.UserName == "root", "No directory refuses root.");

            // A directory that may not be written loses no entry.
            File.SetUnixFileMode(temp.Combine("held"), UnixFileMode.UserRead | UnixFileMode.UserExecute);

            try
            {
                GitClient.Discard(file);
                Assert.True(File.Exists(file));
            }
            finally
            {
                File.SetUnixFileMode(temp.Combine("held"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        GitClient.Discard(file);
        GitClient.Discard(file);

        Assert.False(File.Exists(file));
    }

    /// <summary>Resetting to a commit moves HEAD and the index there, and leaves every file in the work tree as it was.</summary>
    [Fact]
    public async Task ResetToAsync_MovesHeadAndTheIndex_AndNoFile()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("a.txt", "one\n");
        await harness.CommitAllAsync(temp.Path, "first", cancellationToken);
        var first = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        temp.WriteFile("a.txt", "two\n");
        await harness.CommitAllAsync(temp.Path, "second", cancellationToken);

        await harness.GitClient.ResetToAsync(temp.Path, first, cancellationToken);

        Assert.Equal(first, await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken));
        Assert.Equal("two\n", File.ReadAllText(temp.Combine("a.txt")));
        Assert.Equal(["a.txt"], await harness.GitClient.ListChangedSinceAsync(temp.Path, first, cancellationToken));
        Assert.Empty((await harness.RunGitAsync(temp.Path, ["diff", "--cached", "--name-only"], cancellationToken)).StandardOutput.Trim());
    }

    /// <summary>
    /// What a commit holds at a path is told apart - a file with its blob, a directory, a submodule's entry whether this
    /// repository holds the commit it names or not, and nothing - and so it is for a name holding a line break, which git
    /// cannot be asked about line by line: a directory or a submodule answered as nothing is how a directory a commit held
    /// was taken for one an agent made. A submodule's entry naming a commit this repository does not hold is one git 2.43
    /// answers as missing, and later versions as a submodule: told apart on each.
    /// </summary>
    [Fact]
    public async Task HeldAtAsync_TellsAFileADirectoryASubmoduleAndNothingApart()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "x.md"), "x\n");
        await harness.CommitAllAsync(temp.Path, "files", cancellationToken);
        var held = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        var blob = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD:docs/x.md"], cancellationToken)).StandardOutput.Trim();
        var elsewhere = new string('1', held.Length);

        foreach (var entry in new[] { $"160000,{held},lib/held", $"160000,{elsewhere},lib/elsewhere", $"160000,{elsewhere},lib/unasked", $"100644,{blob},odd\nfile.md", $"100644,{blob},odd\ndir/inner.md", $"160000,{held},odd\nsub" })
        {
            // Git for Windows protects the names NTFS cannot hold by refusing them, a line break among them.
            await harness.RunGitAsync(temp.Path, ["-c", "core.protectNTFS=false", "update-index", "--add", "--cacheinfo", entry], cancellationToken);
        }

        await harness.RunGitAsync(temp.Path, ["commit", "-q", "-m", "entries"], cancellationToken);

        string[] asked = ["docs/x.md", "docs", "lib/held", "lib/elsewhere", "none.txt", "odd\nfile.md", "odd\ndir", "odd\nsub", "odd\nnone"];
        var answered = await harness.GitClient.HeldAtAsync(temp.Path, "HEAD", asked, cancellationToken);

        // Each path asked about is answered, and nothing else: the submodule the commit holds at a path nobody asked about
        // among them.
        Assert.Equal(asked.Order(StringComparer.Ordinal), answered.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(GitHeld.File(blob), answered["docs/x.md"]);
        Assert.Equal(GitHeld.Directory, answered["docs"]);
        Assert.Equal(GitHeld.Submodule(held), answered["lib/held"]);
        Assert.Equal(GitHeld.Submodule(elsewhere), answered["lib/elsewhere"]);
        Assert.Equal(GitHeld.Nothing, answered["none.txt"]);
        Assert.Equal(GitHeld.File(blob), answered["odd\nfile.md"]);
        Assert.Equal(GitHeld.Directory, answered["odd\ndir"]);
        Assert.Equal(GitHeld.Submodule(held), answered["odd\nsub"]);
        Assert.Equal(GitHeld.Nothing, answered["odd\nnone"]);
    }

    /// <summary>A file the commit lists that git cannot read is never answered as no file there: that would take it for one the agent added.</summary>
    [Fact]
    public async Task HeldAtAsync_RefusesAFileTheCommitListsButGitCannotRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("lost.txt", "a file whose object goes missing\n");
        await harness.RunGitAsync(temp.Path, ["add", "lost.txt"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "-q", "-m", "a file"], cancellationToken);
        var blob = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD:lost.txt"], cancellationToken)).StandardOutput.Trim();
        var loose = temp.Combine(".git", "objects", blob[..2], blob[2..]);
        File.SetAttributes(loose, FileAttributes.Normal);
        File.Delete(loose);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.HeldAtAsync(temp.Path, "HEAD", ["lost.txt"], cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.Contains("git lists 'lost.txt' at HEAD but could not read it", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A commit git cannot read is refused, never answered as holding nothing: git answers a path of a commit it cannot
    /// read exactly as it answers one the commit does not hold, and every file would be taken for one never there.
    /// </summary>
    [Fact]
    public async Task HeldAtAsync_RefusesACommitGitCannotRead_RatherThanAnsweringNothingThere()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        const string Unknown = "0123456789abcdef0123456789abcdef01234567";

        var refused = await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.HeldAtAsync(temp.Path, Unknown, ["README.md"], cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.StartsWith($"git cannot read commit {Unknown}, so which files it holds cannot be told: ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListWorktreesAsync_ListsTheMainWorktreeFirst_AndNamesEachBranch()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(repository.Path, cancellationToken);

        var detached = elsewhere.Combine("detached");
        var onBranch = elsewhere.Combine("on-branch");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", detached], cancellationToken);
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "-b", "feature", onBranch], cancellationToken);

        var worktrees = await harness.GitClient.ListWorktreesAsync(repository.Path, cancellationToken);

        Assert.Equal(3, worktrees.Count);
        Assert.Equal([true, false, false], worktrees.Select(worktree => worktree.IsMain));
        Assert.All(worktrees, worktree => Assert.False(worktree.IsBare));
        Assert.All(worktrees, worktree => Assert.Matches("^[0-9a-f]{40,64}$", worktree.Commit ?? string.Empty));
        PathAssert.Same(repository.Path, worktrees[0].Path);

        var detachedEntry = Assert.Single(worktrees, worktree => PathAssert.AreSame(detached, worktree.Path));
        var branchEntry = Assert.Single(worktrees, worktree => PathAssert.AreSame(onBranch, worktree.Path));
        Assert.Null(detachedEntry.Branch);
        Assert.Equal("feature", branchEntry.Branch);
    }

    [Fact]
    public async Task ListWorktreesAsync_Throws_OutsideARepository()
    {
        // An empty list would read as "no worktrees", a different answer from "unknown".
        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.ListWorktreesAsync(temp.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IsIgnoredAsync_AnswersBothWays()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(".gitignore", "*.log\n");

        Assert.True(await harness.GitClient.IsIgnoredAsync(temp.Path, "build.log", cancellationToken));
        Assert.False(await harness.GitClient.IsIgnoredAsync(temp.Path, "main.c", cancellationToken));
    }

    /// <summary>
    /// Where two histories part: an ancestor itself, the commit two branches last share, and nothing for two commits that
    /// share no history; a name that names no commit is no answer at all.
    /// </summary>
    [Fact]
    public async Task MergeBaseAsync_NamesWhereTwoHistoriesPart_AndNullWhereTheyShareNone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var git = harness.GitClient;
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        var forked = (await git.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.RunGitAsync(temp.Path, ["branch", "other"], cancellationToken);
        temp.WriteFile("ahead.txt", "ahead");
        await harness.CommitAllAsync(temp.Path, "ahead", cancellationToken);
        var ahead = (await git.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "other"], cancellationToken);
        temp.WriteFile("aside.txt", "aside");
        await harness.CommitAllAsync(temp.Path, "aside", cancellationToken);

        var unrelated = (await harness.RunGitAsync(temp.Path, ["commit-tree", "HEAD^{tree}", "-m", "unrelated"], cancellationToken))
            .StandardOutput.Trim();

        Assert.Equal(forked, await git.MergeBaseAsync(temp.Path, forked, ["HEAD"], cancellationToken));
        Assert.Equal(forked, await git.MergeBaseAsync(temp.Path, ahead, ["HEAD"], cancellationToken));
        Assert.Null(await git.MergeBaseAsync(temp.Path, unrelated, ["HEAD"], cancellationToken));
        await Assert.ThrowsAsync<HarnessException>(() => git.MergeBaseAsync(temp.Path, "no-such-branch", ["HEAD"], cancellationToken));

        // A name that starts with a dash is a name to look up, never an option to obey.
        await Assert.ThrowsAsync<HarnessException>(() => git.MergeBaseAsync(temp.Path, "--octopus", ["HEAD"], cancellationToken));
    }

    /// <summary>
    /// A short id is read as the commit it names, as ResolveCommitAsync reads one, though a blob's id opens the same
    /// way: asked as written, git refused it as ambiguous.
    /// </summary>
    [Fact]
    public async Task MergeBaseAsync_ReadsAShortIdAsTheCommitItNames_ThoughABlobSharesIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var git = harness.GitClient;
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        var head = (await git.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        var prefix = head[..4];
        var format = (await harness.RunGitAsync(temp.Path, ["rev-parse", "--show-object-format"], cancellationToken)).StandardOutput.Trim();
        Func<byte[], byte[]> hash = format == "sha256" ? SHA256.HashData : SHA1.HashData;

        // Hashed as git names a blob, until one opens as the commit does.
        var content = Enumerable.Range(0, int.MaxValue)
            .Select(index => $"collision {index}\n")
            .First(text => Convert.ToHexStringLower(hash(Encoding.UTF8.GetBytes($"blob {text.Length}\0{text}")))
                .StartsWith(prefix, StringComparison.Ordinal));
        temp.WriteFile("collision.txt", content);
        await harness.RunGitAsync(temp.Path, ["hash-object", "-w", "--no-filters", "collision.txt"], cancellationToken);
        Assert.False((await git.RunAsync(temp.Path, ["merge-base", prefix, "HEAD"], cancellationToken: cancellationToken)).Succeeded);

        Assert.Equal(head, await git.MergeBaseAsync(temp.Path, prefix, ["HEAD"], cancellationToken));
    }

    /// <summary>
    /// In a shallow clone the history can stop before two branches part, which is never read as two histories that
    /// share nothing: the answer asks for the rest of the history.
    /// </summary>
    [Fact]
    public async Task MergeBaseAsync_InAShallowClone_AsksForTheRestOfTheHistory()
    {
        using var origin = new TempDirectory();
        using var clone = new TempDirectory();
        var harness = new HarnessFactory();
        var git = harness.GitClient;
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(origin.Path, cancellationToken);
        await harness.RunGitAsync(origin.Path, ["checkout", "-q", "-b", "ahead"], cancellationToken);
        origin.WriteFile("ahead.txt", "ahead");
        await harness.CommitAllAsync(origin.Path, "ahead", cancellationToken);
        await harness.RunGitAsync(origin.Path, ["checkout", "-q", "-b", "aside", "HEAD~1"], cancellationToken);
        origin.WriteFile("aside.txt", "aside");
        await harness.CommitAllAsync(origin.Path, "aside", cancellationToken);

        // --depth needs a URL: a clone from a plain path copies the whole history and ignores it.
        await harness.RunGitAsync(
            clone.Path,
            ["clone", "-q", "--depth", "1", "--no-single-branch", new Uri(origin.Path).AbsoluteUri, "."],
            cancellationToken);
        Assert.Equal("true", (await harness.RunGitAsync(clone.Path, ["rev-parse", "--is-shallow-repository"], cancellationToken)).StandardOutput.Trim());
        Assert.Equal("aside", (await harness.RunGitAsync(clone.Path, ["branch", "--show-current"], cancellationToken)).StandardOutput.Trim());
        Assert.False((await git.RunAsync(clone.Path, ["merge-base", "origin/ahead", "HEAD"], cancellationToken: cancellationToken)).Succeeded);
        Assert.True(await git.IsShallowAsync(clone.Path, cancellationToken));
        Assert.False(await git.IsShallowAsync(origin.Path, cancellationToken));

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => git.MergeBaseAsync(clone.Path, "origin/ahead", ["HEAD"], cancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.Contains("shallow", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("git fetch --unshallow", refusal.Message, StringComparison.Ordinal);

        // Where the shallow history still holds the point the two part, it is named as anywhere else.
        var tip = await git.ResolveCommitAsync(clone.Path, "HEAD", cancellationToken);
        Assert.Equal(tip, await git.MergeBaseAsync(clone.Path, "HEAD", ["HEAD"], cancellationToken));
    }

    /// <summary>
    /// A pull request's merge commit checked out alone, as CI checks one out, with the branch it merges into fetched
    /// alone too: the history stops at both, yet the merge commit's own object names that branch's tip as a parent,
    /// so that tip is where the two part - whichever is asked first.
    /// </summary>
    [Fact]
    public async Task MergeBaseAsync_InAShallowClone_NamesAParentTheOthersOwnCommitNames()
    {
        using var origin = new TempDirectory();
        using var clone = new TempDirectory();
        var harness = new HarnessFactory();
        var git = harness.GitClient;
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(origin.Path, cancellationToken);
        await harness.RunGitAsync(origin.Path, ["branch", "-M", "main"], cancellationToken);
        await harness.RunGitAsync(origin.Path, ["checkout", "-q", "-b", "change"], cancellationToken);
        origin.WriteFile("change.txt", "change");
        await harness.CommitAllAsync(origin.Path, "the change", cancellationToken);
        await harness.RunGitAsync(origin.Path, ["checkout", "-q", "main"], cancellationToken);
        origin.WriteFile("later.txt", "later");
        await harness.CommitAllAsync(origin.Path, "main moves on", cancellationToken);
        var into = (await git.ResolveCommitAsync(origin.Path, "main", cancellationToken))!;
        await harness.RunGitAsync(origin.Path, ["checkout", "-q", "-b", "merged"], cancellationToken);
        await harness.RunGitAsync(origin.Path, ["merge", "-q", "--no-ff", "-m", "merge the change", "change"], cancellationToken);

        var url = new Uri(origin.Path).AbsoluteUri;
        await harness.RunGitAsync(clone.Path, ["clone", "-q", "--depth", "1", "--branch", "merged", url, "."], cancellationToken);
        await harness.RunGitAsync(clone.Path, ["fetch", "-q", "--depth", "1", "origin", "main:refs/remotes/origin/main"], cancellationToken);
        Assert.False((await git.RunAsync(clone.Path, ["merge-base", "origin/main", "HEAD"], cancellationToken: cancellationToken)).Succeeded);

        Assert.Equal(into, await git.MergeBaseAsync(clone.Path, "origin/main", ["HEAD"], cancellationToken));
        Assert.Equal(into, await git.MergeBaseAsync(clone.Path, "HEAD", ["origin/main"], cancellationToken));
    }

    /// <summary>
    /// Measured against several commits, the merge base is where the first parts from the commit merging them all would:
    /// with a branch merged in, that branch's tip. During an unfinished merge MERGE_HEAD names what it brings in, and
    /// nothing is named before it starts or once it is given up.
    /// </summary>
    [Fact]
    public async Task MergeBaseAsync_AgainstSeveralCommits_IsWhereTheirMergeWouldPart_AsAnUnfinishedMergeNamesThem()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var git = harness.GitClient;
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        var forked = (await git.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.RunGitAsync(temp.Path, ["branch", "upstream"], cancellationToken);
        temp.WriteFile("work.txt", "work");
        await harness.CommitAllAsync(temp.Path, "work", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "upstream"], cancellationToken);
        temp.WriteFile("upstream.txt", "upstream");
        await harness.CommitAllAsync(temp.Path, "upstream moves on", cancellationToken);
        var upstream = (await git.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.RunGitAsync(temp.Path, ["checkout", "-q", "-"], cancellationToken);

        Assert.Empty(await git.ListMergeHeadsAsync(temp.Path, cancellationToken));
        Assert.Equal(forked, await git.MergeBaseAsync(temp.Path, "upstream", ["HEAD"], cancellationToken));

        await harness.RunGitAsync(temp.Path, ["merge", "-q", "--no-commit", "--no-ff", "upstream"], cancellationToken);

        Assert.Equal([upstream], await git.ListMergeHeadsAsync(temp.Path, cancellationToken));
        Assert.Equal(upstream, await git.MergeBaseAsync(temp.Path, "upstream", ["HEAD", upstream], cancellationToken));

        await harness.RunGitAsync(temp.Path, ["merge", "--abort"], cancellationToken);

        Assert.Empty(await git.ListMergeHeadsAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task ResolveCommitAsync_ResolvesACommit_AndAnswersNullForANameThatIsNone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        Assert.Matches("^[0-9a-f]{40,64}$", await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken) ?? string.Empty);
        Assert.Null(await harness.GitClient.ResolveCommitAsync(temp.Path, "no-such-branch", cancellationToken));

        // A name that starts with a dash is a name to look up, never an option to obey.
        Assert.Null(await harness.GitClient.ResolveCommitAsync(temp.Path, "--output=elsewhere", cancellationToken));
    }

    [Fact]
    public async Task ReadFileAtCommitAsync_ReadsTheCommittedContent_AndNullForAFileThatWasNotThere()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "committed ✅\r\nline two\n");
        await harness.CommitAllAsync(temp.Path, "notes", cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "changed since");

        var head = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);

        Assert.Equal("committed ✅\r\nline two\n", await harness.GitClient.ReadFileAtCommitAsync(temp.Path, head!, "docs/notes.md", cancellationToken));
        Assert.Null(await harness.GitClient.ReadFileAtCommitAsync(temp.Path, head!, "docs/absent.md", cancellationToken));
    }

    /// <summary>
    /// An index made to hold exactly a set of files holds them - each staged as it stands on disk, a name
    /// no command line would carry among them - and no other: an entry the set does not name is removed,
    /// and a named file that is not there is not held.
    /// </summary>
    [Fact]
    public async Task IndexExactlyAsync_LeavesTheIndexHoldingExactlyTheFilesNamed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var created = await harness.GitClient.RunAsync(temp.Path, ["init", "--quiet", "."], cancellationToken: cancellationToken);
        Assert.True(created.Succeeded, created.FailureMessage);

        temp.WriteFile(Path.Combine("src", "a.c"), "a\n");
        temp.WriteFile(Path.Combine("src", "-b.c"), "b\n");
        temp.WriteFile("stale.txt", "old\n");

        async Task<IReadOnlyList<string>> IndexedAsync()
            => [.. (await harness.GitClient.ListIndexAsync(temp.Path, cancellationToken)).Select(entry => entry.Path).Order(StringComparer.Ordinal)];

        await harness.GitClient.IndexExactlyAsync(temp.Path, ["src/a.c", "src/-b.c", "stale.txt"], cancellationToken);
        Assert.Equal(["src/-b.c", "src/a.c", "stale.txt"], await IndexedAsync());

        // A second set replaces the first: what it drops leaves, and a file named but gone is not held.
        await harness.GitClient.IndexExactlyAsync(temp.Path, ["src/a.c", "src/-b.c", "missing.c"], cancellationToken);
        Assert.Equal(["src/-b.c", "src/a.c"], await IndexedAsync());

        // Staged as it stands: a file changed on disk is staged again, so the index is not stale about it.
        temp.WriteFile(Path.Combine("src", "a.c"), "changed\n");
        await harness.GitClient.IndexExactlyAsync(temp.Path, ["src/a.c", "src/-b.c"], cancellationToken);

        var status = await harness.GitClient.RunAsync(temp.Path, ["status", "--porcelain", "--untracked-files=no"], cancellationToken: cancellationToken);
        Assert.DoesNotContain(" M src/a.c", status.StandardOutput, StringComparison.Ordinal);

        // And an empty set empties it.
        await harness.GitClient.IndexExactlyAsync(temp.Path, [], cancellationToken);
        Assert.Empty(await IndexedAsync());
    }

    /// <summary>
    /// A directory inside another repository's work tree is part of that repository, so nothing is
    /// staged there: its files would go into that repository's index, and what it tracks below the
    /// directory would come out.
    /// </summary>
    [Fact]
    public async Task IndexExactlyAsync_RefusesADirectoryInsideAnotherRepositorysWorkTree()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var created = await harness.GitClient.RunAsync(temp.Path, ["init", "--quiet", "."], cancellationToken: cancellationToken);
        Assert.True(created.Succeeded, created.FailureMessage);
        temp.WriteFile(Path.Combine("copy", "src", "a.c"), "a\n");

        var refused = await Assert.ThrowsAsync<HarnessException>(
            () => harness.GitClient.IndexExactlyAsync(temp.Combine("copy"), ["src/a.c"], cancellationToken));

        Assert.Contains("not the top of a git repository of its own", refused.Message, StringComparison.Ordinal);
        Assert.Empty(await harness.GitClient.ListIndexAsync(temp.Path, cancellationToken));
    }

    /// <summary>
    /// Each file's bytes are read as they stand, through none of the filters and conversions the
    /// repository's attributes name: a required filter the machine cannot run, and a line ending
    /// conversion the configuration refuses, each failed staging - and with it every sync to that host.
    /// </summary>
    [Fact]
    public async Task IndexExactlyAsync_ReadsBytesAsTheyStand_WhateverTheRepositoryWouldFilterThemThrough()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var created = await harness.GitClient.RunAsync(temp.Path, ["init", "--quiet", "."], cancellationToken: cancellationToken);
        Assert.True(created.Succeeded, created.FailureMessage);

        foreach (var (key, value) in new[]
        {
            ("filter.unreachable.clean", "false"),
            ("filter.unreachable.required", "true"),
            ("core.autocrlf", "false"),
            ("core.safecrlf", "true"),
            ("core.eol", "lf"),
        })
        {
            var set = await harness.GitClient.RunAsync(temp.Path, ["config", key, value], cancellationToken: cancellationToken);
            Assert.True(set.Succeeded, set.FailureMessage);
        }

        temp.WriteFile(".gitattributes", "*.bin filter=unreachable\n*.txt text=auto\n");
        temp.WriteFile("asset.bin", "large object\n");
        temp.WriteFile("windows.txt", "one\r\ntwo\r\n");

        await harness.GitClient.IndexExactlyAsync(temp.Path, [".gitattributes", "asset.bin", "windows.txt"], cancellationToken);

        Assert.Equal(
            [".gitattributes", "asset.bin", "windows.txt"],
            (await harness.GitClient.ListIndexAsync(temp.Path, cancellationToken)).Select(entry => entry.Path).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A lock another git left on the index, and what a staging of this tool's own left when it was
    /// stopped part way, stop nothing: a sync dropped while it staged left the lock behind, and every
    /// sync after it that changed a file failed until somebody removed it by hand.
    /// </summary>
    [Fact]
    public async Task IndexExactlyAsync_IsNotStoppedByWhatAStoppedGitLeftBehind()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var created = await harness.GitClient.RunAsync(temp.Path, ["init", "--quiet", "."], cancellationToken: cancellationToken);
        Assert.True(created.Succeeded, created.FailureMessage);
        temp.WriteFile("a.c", "a\n");

        var index = await harness.GitClient.GetIndexFileAsync(temp.Path, cancellationToken);
        await File.WriteAllTextAsync(index + ".lock", string.Empty, cancellationToken);
        await File.WriteAllTextAsync(index + ".harness-sync", "not an index", cancellationToken);
        await File.WriteAllTextAsync(index + ".harness-sync.lock", string.Empty, cancellationToken);

        await harness.GitClient.IndexExactlyAsync(temp.Path, ["a.c"], cancellationToken);

        Assert.Equal(["a.c"], (await harness.GitClient.ListIndexAsync(temp.Path, cancellationToken)).Select(entry => entry.Path));
        Assert.False(File.Exists(index + ".harness-sync"));
        Assert.False(File.Exists(index + ".harness-sync.lock"));
    }

    /// <summary>
    /// Each file's mode is the one git itself would record: executable as its execute bit says where the
    /// repository trusts file modes, as git sets it wherever the file system keeps them, and never where
    /// it does not - there every file has an execute bit to ask about, and none is one git would mark.
    /// </summary>
    [Fact]
    public async Task IndexExactlyAsync_RecordsEachFilesModeAsGitWould()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        var created = await harness.GitClient.RunAsync(temp.Path, ["init", "--quiet", "."], cancellationToken: cancellationToken);
        Assert.True(created.Succeeded, created.FailureMessage);
        temp.WriteFile("run.sh", "#!/bin/sh\n");
        temp.WriteFile("plain.txt", "text\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp.Combine("run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var trusted = await harness.GitClient.RunAsync(temp.Path, ["config", "--bool", "--get", "core.filemode"], cancellationToken: cancellationToken);
        var trustsModes = !trusted.Succeeded || trusted.StandardOutput.Trim() != "false";

        await harness.GitClient.IndexExactlyAsync(temp.Path, ["plain.txt", "run.sh"], cancellationToken);

        var modes = (await harness.GitClient.ListIndexAsync(temp.Path, cancellationToken)).ToDictionary(entry => entry.Path, entry => entry.Mode);
        Assert.Equal("100644", modes["plain.txt"]);
        Assert.Equal(trustsModes ? "100755" : "100644", modes["run.sh"]);
    }

    /// <summary>
    /// Every file of a commit is read by one git process, as it was committed: its line breaks, its
    /// characters beyond ASCII, a byte order mark dropped as reading the file on its own drops it, and
    /// bytes that are not text as the replacement they decode to.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_ReadsEveryFile_ThroughOneProcess()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "committed ✅\r\nline two\n");
        temp.WriteFile(Path.Combine("src", "naïve name.txt"), "日本語\n");
        await File.WriteAllBytesAsync(temp.Combine("bom.txt"), [0xEF, 0xBB, 0xBF, (byte)'b', (byte)'o', (byte)'m'], cancellationToken);
        await File.WriteAllBytesAsync(temp.Combine("data.bin"), [0x00, 0xFF, 0x0A, 0x41], cancellationToken);
        await File.WriteAllBytesAsync(temp.Combine("wide.txt"), [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("wide ✅\n")], cancellationToken);
        await harness.CommitAllAsync(temp.Path, "files", cancellationToken);

        var head = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);
        var processes = new CountingProcesses(harness.ProcessRunner);

        var read = await new GitClient(processes, harness.Output, localVariables: harness.LocalVariables).ReadFilesAtCommitAsync(
            temp.Path,
            head!,
            ["docs/notes.md", "src/naïve name.txt", "bom.txt", "data.bin", "wide.txt"],
            cancellationToken);

        Assert.Equal("committed ✅\r\nline two\n", read["docs/notes.md"]);
        Assert.Equal("日本語\n", read["src/naïve name.txt"]);
        Assert.Equal("bom", read["bom.txt"]);
        Assert.Equal("\0\uFFFD\nA", read["data.bin"]);
        Assert.Equal("wide ✅\n", read["wide.txt"]);
        Assert.Equal(["cat-file"], processes.Started.Select(request => request.Arguments[0]));
    }

    /// <summary>
    /// A path that names no file at the commit is answered null: one never there, a directory, a
    /// submodule's entry. Only the commit's listing tells such a path from a file git cannot read, so
    /// it costs that one listing more - and a read of files that are all there costs nothing more.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_AnswersNull_ForAPathThatNamesNoFileThere()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "notes\n");
        await harness.CommitAllAsync(temp.Path, "notes", cancellationToken);

        var first = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.RunGitAsync(temp.Path, ["update-index", "--add", "--cacheinfo", $"160000,{first},sub"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "submodule"], cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        var processes = new CountingProcesses(harness.ProcessRunner);

        var read = await new GitClient(processes, harness.Output, localVariables: harness.LocalVariables).ReadFilesAtCommitAsync(
            temp.Path,
            head,
            ["docs/notes.md", "docs/absent.md", "docs", "sub"],
            cancellationToken);

        Assert.Equal("notes\n", read["docs/notes.md"]);
        Assert.Null(read["docs/absent.md"]);
        Assert.Null(read["docs"]);
        Assert.Null(read["sub"]);
        Assert.Equal(["cat-file", "ls-tree"], processes.Started.Select(request => request.Arguments[0]));
    }

    /// <summary>
    /// A file the commit lists that git cannot read throws, naming it, read among others or alone. git
    /// answers "missing" for it as for a path that names nothing, and passed off as absent, a registry
    /// nobody could read was reported missing at the base.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_Throws_ForAFileTheCommitListsButGitCannotRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "lost.md"), "lost\n");
        await harness.CommitAllAsync(temp.Path, "lost", cancellationToken);
        await harness.LoseObjectAsync(temp.Path, "HEAD:docs/lost.md", cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;

        var among = await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ReadFilesAtCommitAsync(
            temp.Path, head, ["README.md", "docs/lost.md"], cancellationToken));
        var alone = await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ReadFileAtCommitAsync(
            temp.Path, head, "docs/lost.md", cancellationToken));

        foreach (var refusal in new[] { among, alone })
        {
            Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
            Assert.Contains("'docs/lost.md'", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("git fsck", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A path holding a line break - which git allows, and no line of the batch can carry - is read by
    /// the object the commit's listing gives it, and is null where the commit lists no such file.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_ReadsAPathHoldingALineBreak()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "git for Windows refuses a name holding a line break.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.StageAsync(temp.Path, "\"docs/line\\nbreak.md\"", "wrapped\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "line break"], cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        var read = await harness.GitClient.ReadFilesAtCommitAsync(
            temp.Path,
            head,
            ["docs/line\nbreak.md", "docs/other\nbreak.md", "README.md"],
            cancellationToken);

        Assert.Equal("wrapped\n", read["docs/line\nbreak.md"]);
        Assert.Null(read["docs/other\nbreak.md"]);
        Assert.Equal("test repository", read["README.md"]);
    }

    /// <summary>
    /// A commit's files are listed from the repository's root whichever directory git is asked from,
    /// a submodule's entry left out. A name that is not UTF-8 is said to be one, and quoted as git
    /// quotes it.
    /// </summary>
    [Fact]
    public async Task ListFilesAtCommitAsync_ListsTheCommitsFiles_EachNameAsGitHoldsIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("src", "naïve name.txt"), "text\n");
        await harness.CommitAllAsync(temp.Path, "files", cancellationToken);

        var first = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        await harness.StageAsync(temp.Path, "\"src/caf\\351.md\"", "bytes\n", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["update-index", "--add", "--cacheinfo", $"160000,{first},sub"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "names"], cancellationToken);

        var head = (await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken))!;
        var listed = await harness.GitClient.ListFilesAtCommitAsync(temp.Combine("src"), head, cancellationToken);

        Assert.Equal(
            [
                new GitName("README.md", true),
                new GitName("src/caf�.md", false) { Quoted = @"src/caf\351.md" },
                new GitName("src/naïve name.txt", true),
            ],
            listed.OrderBy(name => name.Text, StringComparer.Ordinal));
    }

    /// <summary>
    /// A listing's names are read as git holds them: one beyond ASCII as its text, and one that is not
    /// UTF-8 said to be one, and quoted as git quotes it. Read as UTF-8 text alone, that name became
    /// another, which named no file, and nothing said so.
    /// </summary>
    [Fact]
    public async Task ListNamesAsync_ReadsEachNameAsGitHoldsIt_AndThrowsWhenGitFails()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("naïve.md", "text\n");
        await harness.StageAsync(temp.Path, "\"caf\\351.md\"", "bytes\n", cancellationToken);

        var listed = await harness.GitClient.ListNamesAsync(
            temp.Path,
            ["ls-files", "-z", "--cached", "--others"],
            cancellationToken);

        Assert.Equal(
            [
                new GitName("README.md", true),
                new GitName("caf�.md", false) { Quoted = @"caf\351.md" },
                new GitName("naïve.md", true),
            ],
            listed.OrderBy(name => name.Text, StringComparer.Ordinal));

        await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ListNamesAsync(
            temp.Path,
            ["ls-files", "-z", "--no-such-option"],
            cancellationToken));
    }

    /// <summary>
    /// A name may begin with the character a byte order mark is, and git's answer then begins with its bytes: they are
    /// the name's, and are kept, rather than taken to say how the answer is written and dropped.
    /// </summary>
    [Fact]
    public async Task ANameThatBeginsAsAByteOrderMarkDoes_IsListedWithIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        var head = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();
        const string Name = "\uFEFFmarked.md";
        temp.WriteFile(Name, "text\n");
        await harness.RunGitAsync(temp.Path, ["add", "--", Name], cancellationToken);

        Assert.Equal([Name], await harness.GitClient.ListChangedSinceAsync(temp.Path, head, cancellationToken));
    }

    /// <summary>
    /// One path this machine spelled has this machine's separator turned into git's: on Windows,
    /// 'docs\notes.md' is the file git calls 'docs/notes.md'.
    /// </summary>
    [Fact]
    public async Task ReadFileAtCommitAsync_ReadsAPathThisMachineSpelled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows separates a path with a backslash.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile(Path.Combine("docs", "notes.md"), "notes\n");
        await harness.CommitAllAsync(temp.Path, "notes", cancellationToken);

        var head = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);

        Assert.Equal("notes\n", await harness.GitClient.ReadFileAtCommitAsync(temp.Path, head!, @"docs\notes.md", cancellationToken));
    }

    /// <summary>
    /// A path is read as git spells it. A backslash is an ordinary character in a name on Linux and
    /// macOS; rewritten as a separator, the name read another file, and its answer was filed under a
    /// key nobody had asked for.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_ReadsAPathAsGitSpellsIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows cannot hold a backslash in a file name.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(temp.Path, @"odd\name.txt"), "odd\n", cancellationToken);
        await harness.CommitAllAsync(temp.Path, "odd", cancellationToken);

        var head = await harness.GitClient.ResolveCommitAsync(temp.Path, "HEAD", cancellationToken);
        var read = await harness.GitClient.ReadFilesAtCommitAsync(temp.Path, head!, [@"odd\name.txt"], cancellationToken);

        Assert.Equal("odd\n", read[@"odd\name.txt"]);
    }

    /// <summary>
    /// A commit git cannot read is refused when many files are read from it as when one is: git
    /// answers 'missing' for it as it does for an absent file, and read that way it passed off a
    /// commit nobody could read as files that were not there.
    /// </summary>
    [Fact]
    public async Task ReadFilesAtCommitAsync_Throws_ForACommitGitCannotRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ReadFilesAtCommitAsync(
            temp.Path, new string('0', 40), ["README.md", "docs/notes.md"], cancellationToken));
    }

    [Fact]
    public async Task ReadFileAtCommitAsync_Throws_ForACommitGitCannotRead()
    {
        // Answering null here would pass off an unreadable commit as a file that did not exist yet.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.ReadFileAtCommitAsync(
            temp.Path, new string('0', 40), "README.md", cancellationToken));
    }

    [Fact]
    public async Task IsIgnoredAsync_Throws_WhenGitCannotAnswer()
    {
        // check-ignore exits 128 outside a repository. Read as "not ignored", a secret
        // would be reported as safe to copy because the question itself failed.
        using var temp = new TempDirectory();

        await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.IsIgnoredAsync(temp.Path, ".secret", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// git names the rule deciding each path, in the order asked, whether or not the path exists: one
    /// that ignores, a re-include, a directory above the path - which no re-include below it reaches -
    /// and none at all.
    /// </summary>
    [Fact]
    public async Task ExplainIgnoredAsync_NamesTheRuleDecidingEachPath_InTheOrderAsked()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", "*.log\n!keep.log\nout/\n!out/kept.txt\n");

        var decisions = await harness.GitClient.ExplainIgnoredAsync(
            temp.Path,
            ["a.log", "keep.log", "out/kept.txt", "src/main.c"],
            token);

        Assert.Equal(
            [
                new IgnoreDecision("a.log", ".gitignore", 1, "*.log"),
                new IgnoreDecision("keep.log", ".gitignore", 2, "!keep.log"),
                new IgnoreDecision("out/kept.txt", ".gitignore", 3, "out/"),
                new IgnoreDecision("src/main.c", null, 0, null),
            ],
            decisions);
        Assert.Equal([true, false, true, false], decisions.Select(decision => decision.Ignored));
    }

    /// <summary>
    /// A question git could not answer is refused, never read as "no rule": every managed path would
    /// then read as ruled by nothing.
    /// </summary>
    [Fact]
    public async Task ExplainIgnoredAsync_Throws_WhenGitCannotAnswer()
    {
        using var temp = new TempDirectory();

        var refusal = await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.ExplainIgnoredAsync(temp.Path, [".secret"], TestContext.Current.CancellationToken));

        // Refused for git's own failure, naming it - never for an answer read in a form it was not.
        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.StartsWith("Could not ask git which rules decide 1 path(s)", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path beyond a symbolic link makes git refuse the whole question. Asked again one at a time,
    /// every other path is answered, and that one says why it was not, in git's words.
    /// </summary>
    [Fact]
    public async Task ExplainIgnoredAsync_AnswersEveryOtherPath_WhereOneIsBeyondALink()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", "*.log\n");

        TestLinks.OrSkip(() => Directory.CreateSymbolicLink(temp.Combine("linked"), elsewhere.Path));

        var decisions = await harness.GitClient.ExplainIgnoredAsync(temp.Path, ["linked/a.log", "a.log", "src/main.c"], token);

        Assert.Null(decisions[0].Source);
        Assert.Contains("symbolic link", decisions[0].Unanswered, StringComparison.Ordinal);
        Assert.Equal(new IgnoreDecision("a.log", ".gitignore", 1, "*.log"), decisions[1]);
        Assert.Equal(new IgnoreDecision("src/main.c", null, 0, null), decisions[2]);
    }

    /// <summary>Where git answers about none of the paths, one at a time either, the question is refused as before.</summary>
    [Fact]
    public async Task ExplainIgnoredAsync_Throws_WhenGitAnswersAboutNoneOfThePaths()
    {
        using var temp = new TempDirectory();

        var refusal = await Assert.ThrowsAsync<HarnessException>(() =>
            new HarnessFactory().GitClient.ExplainIgnoredAsync(temp.Path, [".secret", ".key"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.StartsWith("Could not ask git which rules decide 2 path(s)", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer whose line is not a number is refused as one this build cannot read, naming the path,
    /// rather than escaping as a defect in this tool.
    /// </summary>
    [Fact]
    public void ReadDecisions_RefusesALineThatIsNotANumber()
    {
        var refusal = Assert.Throws<HarnessException>(() => GitClient.ReadDecisions(".gitignore\0one\0*.log\0a.log\0", ["a.log"]));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.Equal("git answered which rule decides 'a.log' in a form this build cannot read: 'one' is not a line number.", refusal.Message);
    }

    private static readonly GitMergeNames MergeNames = new("the main tree", "what both held", "the agent");

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Text(byte[]? bytes) => Encoding.UTF8.GetString(bytes ?? []);

    /// <summary>Every file under the repository's object directory: what a stored object would add to.</summary>
    private static string[] ObjectFiles(string repository)
        => [.. Directory.EnumerateFiles(Path.Combine(repository, ".git", "objects"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A repository whose text files are checked out with CRLF and stored with LF, holding <c>a.txt</c> so, and the blob
    /// its commit holds it as.
    /// </summary>
    private static async Task<string> CommitCheckedOutWithCrlfAsync(HarnessFactory harness, string repository, CancellationToken cancellationToken)
    {
        await harness.InitializeGitRepositoryAsync(repository, cancellationToken);
        File.WriteAllText(Path.Combine(repository, ".gitattributes"), "*.txt text eol=crlf\n*.bin -text\n");
        File.WriteAllText(Path.Combine(repository, "a.txt"), "one\r\ntwo\r\n");
        await harness.CommitAllAsync(repository, "a file checked out with other line endings than it is stored with", cancellationToken);

        return (await harness.RunGitAsync(repository, ["rev-parse", "HEAD:a.txt"], cancellationToken)).StandardOutput.Trim();
    }

    /// <summary>
    /// The three versions of a merge are brought to the form git stores before they are merged, and the result to the
    /// form a checkout writes: a file one tree holds with other line endings then merges as git itself would merge it.
    /// </summary>
    [Fact]
    public async Task AFile_IsReadAsGitWouldStoreIt_AndAFileCheckedOutWithOtherLineEndings_IsReadAsTheCommitsOwn()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var committed = await CommitCheckedOutWithCrlfAsync(harness, temp.Path, cancellationToken);
        var kept = outside.WriteFile("kept", "one\r\nthree\r\n");

        var staged = await harness.GitClient.ReadStagedAsync(
            temp.Path,
            "a.txt",
            [new GitContent.Blob(committed), new GitContent.File(temp.Combine("a.txt")), new GitContent.File(kept), new GitContent.Bytes(Utf8("one\r\nfour\r\n"))],
            cancellationToken);

        Assert.Equal(["one\ntwo\n", "one\ntwo\n", "one\nthree\n", "one\nfour\n"], staged.Select(Text));

        // A path whose bytes are the file is stored as it stands, and checked out so.
        var asItStands = await harness.GitClient.ReadStagedAsync(temp.Path, "a.bin", [new GitContent.File(kept)], cancellationToken);

        Assert.Equal("one\r\nthree\r\n", Text(Assert.Single(asItStands)));
        Assert.Equal("one\r\nmerged\r\n", Text(await harness.GitClient.ReadCheckedOutAsync(temp.Path, "a.txt", Utf8("one\nmerged\n"), cancellationToken)));
        Assert.Equal("one\nmerged\n", Text(await harness.GitClient.ReadCheckedOutAsync(temp.Path, "a.bin", Utf8("one\nmerged\n"), cancellationToken)));
    }

    /// <summary>
    /// A file that begins with a byte order mark is bytes like any other: its merge, and its checkout, are read back
    /// byte for byte - the mark kept, and the text after it not decoded as the mark would say it is written.
    /// </summary>
    [Fact]
    public async Task AFileThatBeginsWithAByteOrderMark_IsMergedAndCheckedOut_ByteForByte()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await CommitCheckedOutWithCrlfAsync(harness, temp.Path, cancellationToken);
        byte[] mark = [0xEF, 0xBB, 0xBF];

        byte[] Version(string first, string last) => [.. mark, .. Utf8($"{first}\ncafé\nthree\nfour\nfive\n{last}\n")];

        var merge = await harness.GitClient.MergeTextAsync(Version("one", "six"), Version("ONÉ", "six"), Version("one", "SÍX"), MergeNames, cancellationToken);

        Assert.True(merge.IsClean);
        Assert.Equal(Version("ONÉ", "SÍX"), merge.Merged);

        byte[] checkedOut = [.. mark, .. Utf8("café\r\nmerged\r\n")];

        Assert.Equal(checkedOut, await harness.GitClient.ReadCheckedOutAsync(temp.Path, "a.txt", [.. mark, .. Utf8("café\nmerged\n")], cancellationToken));

        // Nor does a mark of another encoding say how what follows it is read: these bytes are no UTF-16.
        byte[] other = [0xFF, 0xFE, .. Utf8("café\n")];

        Assert.Equal(other, await harness.GitClient.ReadCheckedOutAsync(temp.Path, "a.bin", other, cancellationToken));
    }

    /// <summary>
    /// git reads a file as it would store it by storing it, and what it stores for a question goes to a directory of the
    /// question's own: a merge that was only measured leaves the repository as it found it.
    /// </summary>
    [Fact]
    public async Task ReadingAFileAsGitWouldStoreIt_LeavesNoObjectInTheRepository_AndNoChangeToItsIndex()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await CommitCheckedOutWithCrlfAsync(harness, temp.Path, cancellationToken);
        var edited = temp.WriteFile("a.txt", "one\r\nnever stored\r\n");
        var index = Path.Combine(temp.Path, ".git", "index");
        var objects = ObjectFiles(temp.Path);
        var indexed = File.ReadAllBytes(index);
        var processes = new CountingProcesses(harness.ProcessRunner);
        var git = new GitClient(processes, harness.Output, localVariables: harness.LocalVariables);

        var staged = await git.ReadStagedAsync(temp.Path, "a.txt", [new GitContent.File(edited), new GitContent.Bytes(Utf8("nor this\r\n"))], cancellationToken);
        var checkedOut = await git.ReadCheckedOutAsync(temp.Path, "a.txt", Utf8("nor this either\n"), cancellationToken);
        var ids = await git.ListStagedIdsAsync(temp.Path, ["a.txt"], cancellationToken);

        Assert.Equal(["one\nnever stored\n", "nor this\n"], staged.Select(Text));
        Assert.Equal("nor this either\r\n", Text(checkedOut));
        Assert.Equal(objects, ObjectFiles(temp.Path));
        Assert.Equal(indexed, File.ReadAllBytes(index));

        // And git does not hold what was read: the id is the one git would give it, of an object it was never given.
        var unknown = await harness.GitClient.RunAsync(temp.Path, ["cat-file", "-e", Assert.Single(ids)], cancellationToken: cancellationToken);
        Assert.False(unknown.Succeeded, "The repository holds an object for a file that was only read.");

        // Every object git was told to write went to a directory outside the repository, set once the inherited one was
        // cleared, with the repository's own beside it, as one entry of a list.
        var written = processes.Started.Where(request => request.Arguments.Contains("-w")).ToList();
        Assert.NotEmpty(written);
        Assert.All(written, request =>
        {
            Assert.Null(request.Environment["GIT_COMMON_DIR"]);
            Assert.False(
                Path.GetFullPath(request.Environment["GIT_OBJECT_DIRECTORY"]!).StartsWith(Path.GetFullPath(temp.Path), StringComparison.OrdinalIgnoreCase),
                "git was told to write into the repository.");
            Assert.StartsWith("\"", request.Environment["GIT_ALTERNATE_OBJECT_DIRECTORIES"], StringComparison.Ordinal);
        });
        Assert.All(written, request => Assert.False(Directory.Exists(request.Environment["GIT_OBJECT_DIRECTORY"]), "The directory of a question's own was left behind."));
    }

    /// <summary>
    /// A linked worktree keeps its objects in the repository it belongs to, not under itself: read from inside one, the
    /// repository's objects are still found beside the directory of the question's own, and still not written.
    /// </summary>
    [Fact]
    public async Task FromInsideALinkedWorktree_AFileIsStillReadAsGitWouldStoreIt_AndTheRepositoryIsNotWritten()
    {
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var committed = await CommitCheckedOutWithCrlfAsync(harness, temp.Path, cancellationToken);
        var linked = elsewhere.Combine("linked");
        await harness.RunGitAsync(temp.Path, ["worktree", "add", "--quiet", "--detach", linked], cancellationToken);
        File.WriteAllText(Path.Combine(linked, "a.txt"), "one\r\nchanged in the worktree\r\n");
        var objects = ObjectFiles(temp.Path);

        var staged = await harness.GitClient.ReadStagedAsync(
            linked,
            "a.txt",
            [new GitContent.Blob(committed), new GitContent.File(Path.Combine(linked, "a.txt"))],
            cancellationToken);
        var checkedOut = await harness.GitClient.ReadCheckedOutAsync(linked, "a.txt", staged[1], cancellationToken);

        Assert.Equal(["one\ntwo\n", "one\nchanged in the worktree\n"], staged.Select(Text));
        Assert.Equal("one\r\nchanged in the worktree\r\n", Text(checkedOut));
        Assert.Equal(objects, ObjectFiles(temp.Path));
    }

    /// <summary>
    /// git reads its other object directories from one variable, as a list parted by a colon - a semicolon on Windows -
    /// and a repository may sit under a directory whose name holds that character: its objects are still read.
    /// </summary>
    [Fact]
    public async Task ARepositoryWhosePathHoldsTheCharacterThatPartsGitsListOfObjectDirectories_IsStillRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = Directory.CreateDirectory(temp.Combine(OperatingSystem.IsWindows() ? "one;two" : "one:two \"quoted\"")).FullName;
        var committed = await CommitCheckedOutWithCrlfAsync(harness, repository, cancellationToken);

        var staged = await harness.GitClient.ReadStagedAsync(repository, "a.txt", [new GitContent.Blob(committed), new GitContent.Bytes(Utf8("in hand\r\n"))], cancellationToken);

        Assert.Equal(["one\ntwo\n", "in hand\n"], staged.Select(Text));
    }

    [Fact]
    public async Task ThreeVersions_MergeClean_WhereTheTwoSidesChangedDifferentLines_AndTheMergedBytesAreGits()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        // A byte that is no UTF-8 among them: what is merged is bytes, carried as they are.
        byte[] Version(string first, string last) => [.. Utf8($"{first}\ntwo\ncaf"), 0xE9, .. Utf8($"\nfour\nfive\nsix\n{last}\n")];

        var merge = await harness.GitClient.MergeTextAsync(Version("one", "seven"), Version("ONE", "seven"), Version("one", "SEVEN"), MergeNames, cancellationToken);

        Assert.True(merge.IsClean);
        Assert.Equal(Version("ONE", "SEVEN"), merge.Merged);
        Assert.Equal(0, merge.Conflicts);
        Assert.Empty(merge.Shown);
        Assert.Null(merge.NotMerged);

        // git's own, run by hand on the same three.
        File.WriteAllBytes(temp.Combine("a"), Version("one", "seven"));
        File.WriteAllBytes(temp.Combine("o"), Version("ONE", "seven"));
        File.WriteAllBytes(temp.Combine("t"), Version("one", "SEVEN"));
        await harness.RunGitAsync(temp.Path, ["merge-file", "o", "a", "t"], cancellationToken);

        Assert.Equal(File.ReadAllBytes(temp.Combine("o")), merge.Merged);
    }

    [Fact]
    public async Task TwoSidesThatChangedOneLineDifferently_AreAConflict_Counted_AndShownWithBothVersionsAndWhatBothHeld()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        static byte[] Version(string second, string eighth) => Utf8($"one\n{second}\nthree\nfour\nfive\nsix\nseven\n{eighth}\nnine\n");

        var merge = await new HarnessFactory().GitClient.MergeTextAsync(Version("two", "eight"), Version("OURS", "eight of ours"), Version("THEIRS", "eight of theirs"), MergeNames, cancellationToken);

        Assert.False(merge.IsClean);
        Assert.Null(merge.Merged);
        Assert.Null(merge.NotMerged);
        Assert.Equal(2, merge.Conflicts);
        Assert.Equal("2 place(s)", merge.ConflictsSaid);
        Assert.Equal(
            [
                "<<<<<<< the main tree", "OURS", "||||||| what both held", "two", "=======", "THEIRS", ">>>>>>> the agent",
                "<<<<<<< the main tree", "eight of ours", "||||||| what both held", "eight", "=======", "eight of theirs", ">>>>>>> the agent",
            ],
            merge.Shown);
    }

    [Fact]
    public async Task AChangeBothSidesMadeAlike_MergesClean_Once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var merge = await new HarnessFactory().GitClient.MergeTextAsync(Utf8("one\ntwo\nthree\n"), Utf8("one\nBOTH\nthree\n"), Utf8("one\nBOTH\nthree\n"), MergeNames, cancellationToken);

        Assert.True(merge.IsClean);
        Assert.Equal("one\nBOTH\nthree\n", Text(merge.Merged));

        // Counted for each side, since each made it.
        Assert.Equal((1, 1), (merge.OursHunks, merge.TheirsHunks));
    }

    /// <summary>
    /// A file may hold a line that reads as a conflict's marker - a document about merging does. The markers of its merge
    /// are made longer than any such line, so the line is merged like any other and never taken for a conflict.
    /// </summary>
    [Fact]
    public async Task AFileHoldingALineOfSevenOpeningMarks_IsStillMerged_AndItsLineIsNotShownAsAConflict()
    {
        var git = new HarnessFactory().GitClient;
        var cancellationToken = TestContext.Current.CancellationToken;

        static byte[] Version(string first, string fifth) => Utf8($"{first}\n<<<<<<< not a marker\n=======\n>>>>>>> nor this\n{fifth}\nsix\n");

        var clean = await git.MergeTextAsync(Version("one", "five"), Version("ONE", "five"), Version("one", "FIVE"), MergeNames, cancellationToken);

        Assert.True(clean.IsClean);
        Assert.Equal(Version("ONE", "FIVE"), clean.Merged);

        var conflict = await git.MergeTextAsync(Version("one", "five"), Version("one", "OURS"), Version("one", "THEIRS"), MergeNames, cancellationToken);

        Assert.Equal(1, conflict.Conflicts);
        Assert.Equal(
            ["<<<<<<<< the main tree", "OURS", "|||||||| what both held", "five", "========", "THEIRS", ">>>>>>>> the agent"],
            conflict.Shown);
    }

    /// <summary>
    /// Each of a conflict's marks alone, beginning a line of a file, makes the markers longer than it: a line of any one
    /// of them would read as the marker made of it.
    /// </summary>
    [Theory]
    [InlineData('<')]
    [InlineData('=')]
    [InlineData('>')]
    [InlineData('|')]
    public async Task ALineOfSevenOfAnyOneMark_MakesTheMarkersLonger(char mark)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        byte[] Version(string fifth) => Utf8($"one\n{new string(mark, 7)} not a marker\nthree\nfour\n{fifth}\nsix\n");

        var conflict = await new HarnessFactory().GitClient.MergeTextAsync(Version("five"), Version("OURS"), Version("THEIRS"), MergeNames, cancellationToken);

        Assert.Equal(1, conflict.Conflicts);
        Assert.Equal(
            ["<<<<<<<< the main tree", "OURS", "|||||||| what both held", "five", "========", "THEIRS", ">>>>>>>> the agent"],
            conflict.Shown);
    }

    [Fact]
    public async Task ABinaryFile_IsNotMerged_InGitsWords_AndIsNotAnErrorOfTheQuestion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        static byte[] Version(byte changed) => [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, changed, 0x00, 0x0A, 0xFF];

        var merge = await new HarnessFactory().GitClient.MergeTextAsync(Version(1), Version(2), Version(3), MergeNames, cancellationToken);

        Assert.False(merge.IsClean);
        Assert.Equal(0, merge.Conflicts);
        Assert.Empty(merge.Shown);
        Assert.False(string.IsNullOrWhiteSpace(merge.NotMerged), "git's reason was not kept.");
    }

    [Fact]
    public async Task MoreThan127Conflicts_AreSaidAs127OrMore_AndOnlyTheFirstAreShown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Two hundred lines the two sides changed differently, each among lines neither changed.
        static byte[] Version(string changed)
            => Utf8(string.Concat(Enumerable.Range(0, 200).Select(at => $"kept {at} a\nkept {at} b\nkept {at} c\nkept {at} d\n{changed} {at}\n")));

        var merge = await new HarnessFactory().GitClient.MergeTextAsync(Version("line"), Version("ours"), Version("theirs"), MergeNames, cancellationToken);

        Assert.False(merge.IsClean);
        Assert.Equal(GitTextMerge.MostConflictsCounted, merge.Conflicts);
        Assert.Equal("127 place(s) or more", merge.ConflictsSaid);
        Assert.Equal((GitTextMerge.MostConflictsShown * 7) + 1, merge.Shown.Count);
        Assert.Equal(["<<<<<<< the main tree", "ours 5", "||||||| what both held", "line 5", "=======", "theirs 5", ">>>>>>> the agent"], merge.Shown.Skip(35).Take(7));
        Assert.Equal("(194 more conflict(s), not shown)", merge.Shown[^1]);
    }

    [Fact]
    public async Task AConflictLongerThanIsShown_IsCutAtItsBound_WithHowMuchMoreThereIs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        static byte[] Version(string changed, int lines) => Utf8("first\n" + string.Concat(Enumerable.Range(0, lines).Select(at => $"{changed} {at}\n")) + "last\n");

        var merge = await new HarnessFactory().GitClient.MergeTextAsync(Version("line", 1), Version("ours", 50), Version("theirs", 2), MergeNames, cancellationToken);

        // Opening marker, fifty of ours, the marker of what both held, its one line, the divider, two of theirs, the closing marker.
        Assert.Equal(1, merge.Conflicts);
        Assert.Equal(GitTextMerge.MostLinesShown + 1, merge.Shown.Count);
        Assert.Equal("<<<<<<< the main tree", merge.Shown[0]);
        Assert.Equal("ours 38", merge.Shown[GitTextMerge.MostLinesShown - 1]);
        Assert.Equal("(17 more line(s) of this conflict, not shown)", merge.Shown[^1]);
    }

    [Fact]
    public async Task TheHunksOfEachSide_AreCountedAsGitCountsThem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        static byte[] Version(params int[] changed)
            => Utf8(string.Concat(Enumerable.Range(1, 30).Select(line => changed.Contains(line) ? $"CHANGED {line}\n" : $"line {line}\n")));

        // Ours: two runs of changed lines. Theirs: three, one of them two lines long.
        var merge = await new HarnessFactory().GitClient.MergeTextAsync(Version(), Version(2, 10), Version(16, 17, 22, 29), MergeNames, cancellationToken);

        Assert.True(merge.IsClean);
        Assert.Equal(Version(2, 10, 16, 17, 22, 29), merge.Merged);
        Assert.Equal((2, 3), (merge.OursHunks, merge.TheirsHunks));
    }

    [Fact]
    public async Task TheHistoryOfAPath_ListsEachVersionWithItsCommit_NewestFirst_StopsAtItsBound_AndSaysItStopped()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        async Task<string> CommitAsync(string file, string content)
        {
            temp.WriteFile(file, content);
            await harness.CommitAllAsync(temp.Path, $"{file}: {content}", cancellationToken);

            return (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();
        }

        async Task<string> BlobAsync(string commit, string file)
            => (await harness.RunGitAsync(temp.Path, ["rev-parse", $"{commit}:{file}"], cancellationToken)).StandardOutput.Trim();

        var first = await CommitAsync("a.txt", "first");
        var other = await CommitAsync("b.txt", "another file");
        await CommitAsync("unrelated.txt", "neither of them");
        var second = await CommitAsync("a.txt", "second");
        var third = await CommitAsync("a.txt", "third");
        var again = await CommitAsync("a.txt", "first");

        var whole = await harness.GitClient.ListHeldInHistoryAsync(temp.Path, "HEAD", ["a.txt", "b.txt", "never.txt"], 64, cancellationToken);

        Assert.False(whole.Cut);
        Assert.Equal(5, whole.Commits);

        // The commit that changed only the other file holds the first version too, and is no version of its own: the
        // run is named by its oldest commit, the one that made it.
        Assert.Equal(
            [
                new GitHeldAt(again, await BlobAsync(first, "a.txt")),
                new GitHeldAt(third, await BlobAsync(third, "a.txt")),
                new GitHeldAt(second, await BlobAsync(second, "a.txt")),
                new GitHeldAt(first, await BlobAsync(first, "a.txt")),
            ],
            whole.Held["a.txt"]);
        Assert.Equal([new GitHeldAt(other, await BlobAsync(other, "b.txt"))], whole.Held["b.txt"]);
        Assert.Empty(whole.Held["never.txt"]);

        var bounded = await harness.GitClient.ListHeldInHistoryAsync(temp.Path, "HEAD", ["a.txt"], 2, cancellationToken);

        Assert.True(bounded.Cut, "A history cut short at its bound was not said to be.");
        Assert.Equal(2, bounded.Commits);
        Assert.Equal([again, third], bounded.Held["a.txt"].Select(held => held.Commit));

        // Exactly as many as the bound is the whole of it, and is not said to be cut.
        var exact = await harness.GitClient.ListHeldInHistoryAsync(temp.Path, "HEAD", ["a.txt"], 4, cancellationToken);

        Assert.False(exact.Cut);
        Assert.Equal(4, exact.Held["a.txt"].Count);
    }

    /// <summary>
    /// A merge that kept its own side of a file dropped what the other side held there, and that version was still the
    /// history's: an agent seeded from the other side holds it. Walked as git walks by default, only the side taken is
    /// followed and the version is never found.
    /// </summary>
    [Fact]
    public async Task AVersionAMergeDropped_IsStillListed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("a.txt", "what the main side holds");
        await harness.CommitAllAsync(temp.Path, "a file", cancellationToken);
        var main = (await harness.RunGitAsync(temp.Path, ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken)).StandardOutput.Trim();

        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", "-b", "side"], cancellationToken);
        temp.WriteFile("a.txt", "what the other side held");
        await harness.CommitAllAsync(temp.Path, "the other side's version", cancellationToken);
        var dropped = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();
        var droppedBlob = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD:a.txt"], cancellationToken)).StandardOutput.Trim();

        await harness.RunGitAsync(temp.Path, ["checkout", "--quiet", main], cancellationToken);
        temp.WriteFile("other.txt", "something else");
        await harness.CommitAllAsync(temp.Path, "another file on the main side", cancellationToken);
        await harness.RunGitAsync(temp.Path, ["merge", "--quiet", "-s", "ours", "-m", "the other side merged, its file dropped", "side"], cancellationToken);

        var history = await harness.GitClient.ListHeldInHistoryAsync(temp.Path, "HEAD", ["a.txt"], 64, cancellationToken);

        Assert.Contains(new GitHeldAt(dropped, droppedBlob), history.Held["a.txt"]);
        Assert.False(history.Cut);
    }

    /// <summary>
    /// A path is the path it names, never a pattern: the history of <c>[id].tsx</c> - a name a web project's routes use -
    /// is the commits that changed that file, and not those that changed <c>i.tsx</c> or <c>d.tsx</c> beside it.
    /// </summary>
    [Fact]
    public async Task TheHistoryOfAPathThatReadsAsAPattern_IsTheHistoryOfThatPathAlone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("[id].tsx", "the route");
        await harness.CommitAllAsync(temp.Path, "a route", cancellationToken);
        var made = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();
        var blob = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD:[id].tsx"], cancellationToken)).StandardOutput.Trim();

        foreach (var (file, content) in new[] { ("i.tsx", "one"), ("d.tsx", "two"), ("i.tsx", "three") })
        {
            temp.WriteFile(file, content);
            await harness.CommitAllAsync(temp.Path, $"{file}: {content}", cancellationToken);
        }

        var history = await harness.GitClient.ListHeldInHistoryAsync(temp.Path, "HEAD", ["[id].tsx"], 64, cancellationToken);

        Assert.Equal(1, history.Commits);
        Assert.Equal([new GitHeldAt(made, blob)], history.Held["[id].tsx"]);
    }

    /// <summary>
    /// A path holding a line break - which git allows, and no line of git's input can carry - has its history read too:
    /// named to the walk as an argument, and read from each commit apart, beside a path read the usual way.
    /// </summary>
    [Fact]
    public async Task TheHistoryOfAPathHoldingALineBreak_IsRead_BesideAnotherPaths()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "git for Windows refuses a name holding a line break.");

        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        const string Wrapped = "docs/line\nbreak.md";

        async Task<string> CommitAsync(string message)
        {
            await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", message], cancellationToken);

            return (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();
        }

        async Task<string> BlobAsync(string commit)
            => (await harness.RunGitAsync(temp.Path, ["rev-parse", $"{commit}:{Wrapped}"], cancellationToken)).StandardOutput.Trim();

        await harness.StageAsync(temp.Path, "\"docs/line\\nbreak.md\"", "first\n", cancellationToken);
        var first = await CommitAsync("a wrapped name");
        await harness.StageAsync(temp.Path, "\"plain.md\"", "beside it\n", cancellationToken);
        var plain = await CommitAsync("another file");
        await harness.StageAsync(temp.Path, "\"docs/line\\nbreak.md\"", "second\n", cancellationToken);
        var second = await CommitAsync("the wrapped name changed");

        var history = await harness.GitClient.ListHeldInHistoryAsync(temp.Path, "HEAD", [Wrapped, "plain.md"], 64, cancellationToken);

        Assert.False(history.Cut);
        Assert.Equal(2, history.Commits);
        Assert.Equal([new GitHeldAt(second, await BlobAsync(second)), new GitHeldAt(first, await BlobAsync(first))], history.Held[Wrapped]);
        Assert.Equal(
            [new GitHeldAt(plain, (await harness.RunGitAsync(temp.Path, ["rev-parse", $"{plain}:plain.md"], cancellationToken)).StandardOutput.Trim())],
            history.Held["plain.md"]);
    }

    [Fact]
    public async Task TheEntriesOfACommit_AreListedForThePathsAskedAlone_EachWithItsMode()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        temp.WriteFile("plain.txt", "a file");
        temp.WriteFile("run.sh", "#!/bin/sh\n");
        temp.WriteFile("[ab].txt", "a name that reads as a pattern");
        temp.WriteFile("a.txt", "what the pattern would match");
        temp.WriteFile(Path.Combine("dir", "inner.txt"), "inside a directory");
        await harness.RunGitAsync(temp.Path, ["add", "-A"], cancellationToken);

        // Set in the index, so the commit holds them on a file system that keeps no such bit and makes no such link.
        await harness.RunGitAsync(temp.Path, ["update-index", "--chmod=+x", "run.sh"], cancellationToken);
        var target = (await harness.RunGitAsync(temp.Path, ["rev-parse", ":plain.txt"], cancellationToken)).StandardOutput.Trim();
        await harness.RunGitAsync(temp.Path, ["update-index", "--add", "--cacheinfo", $"120000,{target},link"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "entries of every kind"], cancellationToken);
        var commit = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD"], cancellationToken)).StandardOutput.Trim();

        var entries = await harness.GitClient.ListEntriesAtAsync(
            temp.Path,
            commit,
            ["plain.txt", "run.sh", "[ab].txt", "dir", "dir/inner.txt", "link", "absent.txt"],
            cancellationToken);

        Assert.Equal(["[ab].txt", "dir", "dir/inner.txt", "link", "plain.txt", "run.sh"], entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new GitEntry("100644", "blob", target), entries["plain.txt"]);
        Assert.True(entries["plain.txt"] is { IsBlob: true, IsExecutable: false, IsLink: false });
        Assert.True(entries["run.sh"] is { IsBlob: true, IsExecutable: true, IsLink: false });
        Assert.True(entries["link"] is { IsBlob: true, IsExecutable: false, IsLink: true });
        Assert.True(entries["dir"] is { IsBlob: false, Type: "tree" });
        Assert.True(entries["dir/inner.txt"].IsBlob);
        Assert.Empty(await harness.GitClient.ListEntriesAtAsync(temp.Path, commit, [], cancellationToken));
    }

    [Fact]
    public async Task TheBlobAFileWouldBeStoredAs_IsNamedWithoutStoringIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var committed = await CommitCheckedOutWithCrlfAsync(harness, temp.Path, cancellationToken);
        temp.WriteFile("new.txt", "one\r\ntwo\r\n");
        temp.WriteFile("other.txt", "something else\r\n");
        var objects = ObjectFiles(temp.Path);

        var ids = await harness.GitClient.ListStagedIdsAsync(temp.Path, ["other.txt", "a.txt", "new.txt"], cancellationToken);

        // The file as checked out and one new to the tree with the same lines are the blob the commit holds.
        Assert.Equal(3, ids.Count);
        Assert.NotEqual(committed, ids[0]);
        Assert.Equal([committed, committed], ids.Skip(1));
        Assert.Equal(objects, ObjectFiles(temp.Path));
        Assert.Empty(await harness.GitClient.ListStagedIdsAsync(temp.Path, [], cancellationToken));
    }

    [Fact]
    public async Task Blobs_AreReadAsTheirBytes_AndWhatIsNoBlobGitCanRead_AsNone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        byte[] content = [.. Utf8("caf"), 0xE9, 0x00, 0x0A, .. Utf8("more\n")];
        File.WriteAllBytes(temp.Combine("bytes.bin"), content);
        File.WriteAllText(temp.Combine(".gitattributes"), "*.bin -text\n");
        await harness.CommitAllAsync(temp.Path, "bytes that are no text", cancellationToken);
        var blob = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD:bytes.bin"], cancellationToken)).StandardOutput.Trim();
        var tree = (await harness.RunGitAsync(temp.Path, ["rev-parse", "HEAD^{tree}"], cancellationToken)).StandardOutput.Trim();

        var read = await harness.GitClient.ReadBlobsAsync(temp.Path, [blob, new string('0', blob.Length), tree, "HEAD:bytes.bin"], cancellationToken);

        Assert.Equal(4, read.Count);
        Assert.Equal(content, read[0]);
        Assert.Null(read[1]);
        Assert.Null(read[2]);
        Assert.Equal(content, read[3]);
        Assert.Empty(await harness.GitClient.ReadBlobsAsync(temp.Path, [], cancellationToken));
    }

    [Fact]
    public async Task WhatGitsAttributesDeclare_IsReadForEveryPathAsked_HoweverMany()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        File.WriteAllText(temp.Combine(".gitattributes"), "*.txt text eol=lf\n*.bin -text\n*.gen merge=union\n*.own merge\n*.never -merge\n");

        // More paths than one command line carries, none of them a file that exists: the attributes are of names.
        string[] many = [.. Enumerable.Range(0, 450).Select(at => $"deep/directory/of/many/files/number-{at}.txt")];

        var declared = await harness.GitClient.ReadAttributesAsync(
            temp.Path,
            ["text", "eol", "merge"],
            ["a.txt", "b.bin", "c.gen", "d.own", "e.never", "with space.other", .. many],
            cancellationToken: cancellationToken);

        Assert.Equal(456, declared.Count);
        Assert.Equal((GitAttributes.Set, "lf", GitAttributes.Unspecified), (declared["a.txt"]["text"], declared["a.txt"]["eol"], declared["a.txt"]["merge"]));
        Assert.Equal((GitAttributes.Unset, GitAttributes.Unspecified), (declared["b.bin"]["text"], declared["b.bin"]["merge"]));
        Assert.Equal("union", declared["c.gen"]["merge"]);
        Assert.Equal(GitAttributes.Set, declared["d.own"]["merge"]);
        Assert.Equal(GitAttributes.Unset, declared["e.never"]["merge"]);
        Assert.All(declared["with space.other"].Values, value => Assert.Equal(GitAttributes.Unspecified, value));
        Assert.All(many, path => Assert.Equal("lf", declared[path]["eol"]));
        Assert.Empty(await harness.GitClient.ReadAttributesAsync(temp.Path, ["text"], [], cancellationToken: cancellationToken));
    }

    /// <summary>
    /// What git's attributes declare for a path whose name begins as a byte order mark does is filed under that path, the
    /// mark kept: read as text, the mark was taken for one and dropped, and the path read as declaring nothing.
    /// </summary>
    [Fact]
    public async Task WhatGitsAttributesDeclare_ForAPathBeginningAsAByteOrderMarkDoes_IsFiledUnderThatPath()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        const string Name = "\uFEFFmarked.reg";
        File.WriteAllText(temp.Combine(".gitattributes"), "*.reg -merge eol=lf\n");

        var declared = await harness.GitClient.ReadAttributesAsync(temp.Path, ["merge", "eol"], [Name, "plain.reg"], cancellationToken: cancellationToken);

        Assert.Equal(["plain.reg", Name], declared.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((GitAttributes.Unset, "lf"), (declared[Name]["merge"], declared[Name]["eol"]));
    }
}

/// <summary>What the git client sends to git, and how it reads answers, against scripted results.</summary>
public sealed class GitClientProtocolTests
{
    private const string NotARepository = "fatal: not a git repository (or any of the parent directories): .git";

    [Fact]
    public async Task EveryCommand_TellsGitNobodyCanAnswerAPrompt()
    {
        var (git, requests) = Scripted(Exited(0));

        await git.RunAsync("/repo", ["fetch"], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("0", Assert.Single(requests).Environment["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public async Task EveryCommand_ClearsEveryVariableGitReadsARepositoryFrom()
    {
        // Each outranks `-C <directory>`. A git hook runs with some of them set, so a harness command
        // invoked from a hook, or from a shell left in another checkout, would read and write a
        // repository nobody named: with GIT_COMMON_DIR naming another repository, deleting a worktree
        // was refused and left it registered. Every name the list gives is cleared, not three of them.
        var (git, requests) = Scripted(Exited(0));

        await git.RunAsync("/repo", ["status"], cancellationToken: TestContext.Current.CancellationToken);

        var environment = Assert.Single(requests).Environment;

        foreach (var name in LocalNames)
        {
            Assert.True(environment.ContainsKey(name), $"{name} was not cleared.");
            Assert.Null(environment[name]);
        }
    }

    [Fact]
    public async Task AnIndexACallerAsksFor_IsSetAfterTheInheritedOneIsCleared()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        var processes = new CountingProcesses(harness.ProcessRunner);

        await new GitClient(processes, harness.Output, localVariables: harness.LocalVariables)
            .IndexExactlyAsync(temp.Path, ["README.md"], cancellationToken);

        var indexed = processes.Started.Where(request => request.Environment.TryGetValue("GIT_INDEX_FILE", out var index) && index is not null).ToList();
        Assert.NotEmpty(indexed);
        Assert.All(indexed, request => Assert.Null(request.Environment["GIT_COMMON_DIR"]));
    }

    [Fact]
    public async Task RepositoryQueries_RunUntranslated_WhileOtherCommandsKeepTheUsersLocale()
    {
        // The queries are matched against git's English messages. Everything else, hooks
        // included, must keep the user's locale.
        var (git, requests) = Scripted(Exited(0, "/repo\n"));
        var cancellationToken = TestContext.Current.CancellationToken;

        await git.GetRepositoryRootAsync("/repo", cancellationToken);
        await git.RunAsync("/repo", ["commit", "-m", "x"], cancellationToken: cancellationToken);

        Assert.Equal("C", requests[0].Environment["LC_ALL"]);
        Assert.False(requests[1].Environment.ContainsKey("LC_ALL"));
    }

    /// <summary>
    /// git's answer that a directory is in no repository it can open is an answer, in either wording: below a .git file
    /// naming a repository that is gone - a worktree whose record was deleted - git 2.56 says so in its own words, where
    /// older git said "not a git repository". Read as a failure, deleting such a worktree stopped as unchecked.
    /// </summary>
    [Theory]
    [InlineData(NotARepository)]
    [InlineData("fatal: gitfile does not point to a valid repository: /somewhere/.git")]
    public async Task NotARepository_IsAnAnswer(string answer)
    {
        var (git, _) = Scripted(Exited(128, stderr: answer));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.False(await git.IsRepositoryAsync("/somewhere", cancellationToken));
        Assert.Null(await git.GetRepositoryRootAsync("/somewhere", cancellationToken));
        Assert.Null(await git.GetMainWorktreeAsync("/somewhere", cancellationToken));
        Assert.Null(await git.GetLocationAsync("/somewhere", cancellationToken));
    }

    [Fact]
    public async Task ARepositoryGitRefuses_IsAFailure_NotAMissingRepository()
    {
        // Reported as "not a repository", this sends the user to `git init` inside a
        // repository that exists, when git's own message names the fix.
        var (git, _) = Scripted(Exited(
            128,
            stderr: "fatal: detected dubious ownership in repository at '/repo'"));

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            git.IsRepositoryAsync("/repo", TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
        Assert.Contains("dubious ownership", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeout_IsNeverReadAsNotARepository()
    {
        var (git, _) = Scripted(Exited(-1, stderr: NotARepository, timedOut: true));

        await Assert.ThrowsAsync<HarnessException>(() =>
            git.GetRepositoryRootAsync("/repo", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetMainWorktreeAsync_RecognisesABareRepository()
    {
        var (git, _) = Scripted(Exited(
            0,
            "worktree /srv/repo.git\nbare\n\nworktree /srv/wt\nHEAD 0123456789abcdef0123456789abcdef01234567\ndetached\n\n"));

        var main = await git.GetMainWorktreeAsync("/srv/wt", TestContext.Current.CancellationToken);

        Assert.NotNull(main);
        Assert.True(main.IsMain);
        Assert.True(main.IsBare);
        Assert.EndsWith("repo.git", main.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatusAsync_CountsARenameOrCopyOnce_WhicheverColumnMarksIt()
    {
        // A rename or copy is followed by its original path, whether the first status column marks
        // it, as for a staged one, or the second, as for one git finds in the work tree. No other
        // entry is followed by one, an unmerged one included.
        var (git, _) = Scripted(Exited(
            0,
            "R  new.txt\0old.txt\0 R moved.txt\0was.txt\0C  copy.txt\0big.txt\0UU f.txt\0?? untracked.txt\0"));

        var entries = await git.GetStatusAsync("/repo", TestContext.Current.CancellationToken);

        Assert.Equal(["R  new.txt", " R moved.txt", "C  copy.txt", "UU f.txt", "?? untracked.txt"], entries.Select(entry => entry.Line));
    }

    [Fact]
    public async Task ListWorktreesAsync_ReadsALock_WithOrWithoutAReason()
    {
        const string Head = "HEAD 0123456789abcdef0123456789abcdef01234567\n";
        var (git, _) = Scripted(Exited(
            0,
            $"worktree /repo\n{Head}branch refs/heads/main\n\n"
            + $"worktree /repo/a\n{Head}detached\nlocked on a USB disk\n\n"
            + $"worktree /repo/b\n{Head}detached\nlocked\n\n"));

        var worktrees = await git.ListWorktreesAsync("/repo", TestContext.Current.CancellationToken);

        Assert.Equal(new string?[] { null, "on a USB disk", string.Empty }, worktrees.Select(worktree => worktree.LockReason));
    }

    [Fact]
    public async Task ListWorktreesAsync_ReadsAnUnbornHead_AsNoCommit()
    {
        // git lists the HEAD of an orphan branch, which names no commit yet, as the null object id.
        // Handed back to git as a commit, it fails every command it reaches.
        var (git, _) = Scripted(Exited(
            0,
            "worktree /repo\nHEAD 0123456789abcdef0123456789abcdef01234567\nbranch refs/heads/main\n\n"
            + "worktree /repo/a\nHEAD 0000000000000000000000000000000000000000\nbranch refs/heads/unborn\n\n"));

        var worktrees = await git.ListWorktreesAsync("/repo", TestContext.Current.CancellationToken);

        Assert.Equal(
            new string?[] { "0123456789abcdef0123456789abcdef01234567", null },
            worktrees.Select(worktree => worktree.Commit));
    }

    [Fact]
    public async Task ListIndexAsync_ReadsTheFlagsThatHideAnEdit_AndSubmodules()
    {
        var (git, _) = Scripted(Exited(
            0,
            "H 100644 aaaa 0\tplain.txt\0h 100644 bbbb 0\tassumed.txt\0S 100755 cccc 0\tsparse dir/tool\0H 160000 dddd 0\tlib\0"));

        var entries = await git.ListIndexAsync("/repo", TestContext.Current.CancellationToken);

        Assert.Equal(["plain.txt", "assumed.txt", "sparse dir/tool", "lib"], entries.Select(entry => entry.Path));
        Assert.Equal([false, true, false, false], entries.Select(entry => entry.IsAssumedUnchanged));
        Assert.Equal([false, false, true, false], entries.Select(entry => entry.IsSkipWorktree));
        Assert.Equal([false, false, false, true], entries.Select(entry => entry.IsSubmodule));
    }

    [Fact]
    public async Task IsIgnoredAsync_TreatsATimeout_AsAFailure()
    {
        var (git, _) = Scripted(Exited(1, timedOut: true));

        await Assert.ThrowsAsync<HarnessException>(() =>
            git.IsIgnoredAsync("/repo", ".secret", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EchoedCommands_StreamBothOutputs()
    {
        var (git, requests) = Scripted(Exited(0));

        await git.RunAsync("/repo", ["pull"], echoOutput: true, TestContext.Current.CancellationToken);

        var request = Assert.Single(requests);
        Assert.NotNull(request.OnOutputLine);
        Assert.NotNull(request.OnErrorLine);
    }

    /// <summary>
    /// Where git finds nothing two commits share, that is the answer only where the clone is known to hold its whole
    /// history: a shallow one answers with a parent the other commit's own object names, or asks for the rest; and one
    /// that could not say which - git failing, or a git too old to know the question echoing it back - is neither
    /// answered as two unrelated histories nor called shallow. A merge-base that ran out of time fails, and nothing more
    /// is asked of git.
    /// </summary>
    [Fact]
    public async Task MergeBaseAsync_AnswersThatNothingIsShared_OnlyWhereTheCloneIsKnownWhole()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var (whole, _) = Scripted(Exited(1), Exited(0, "false\n"));
        Assert.Null(await whole.MergeBaseAsync("/repo", "main", ["HEAD"], cancellationToken));

        const string Main = "1111111111111111111111111111111111111111";
        const string Head = "2222222222222222222222222222222222222222";
        const string Other = "3333333333333333333333333333333333333333";

        var (named, _) = Scripted(
            Exited(1), Exited(0, "true\n"), Exited(0, Main + "\n"), Exited(0, Head + "\n"),
            Exited(0, $"tree 4444444444444444444444444444444444444444\nparent {Main}\nparent {Other}\nauthor a\n\nparent {Head} in the message\n"));
        Assert.Equal(Main, await named.MergeBaseAsync("/repo", "main", ["HEAD"], cancellationToken));

        // A message line that reads as a parent is no parent: the header ends at the first empty line.
        var (shallow, _) = Scripted(
            Exited(1), Exited(0, "true\n"), Exited(0, Main + "\n"), Exited(0, Head + "\n"),
            Exited(0, $"tree 4444444444444444444444444444444444444444\nparent {Other}\n\nparent {Main}\n"),
            Exited(0, "tree 4444444444444444444444444444444444444444\n\nmain's root\n"));
        var asked = await Assert.ThrowsAsync<HarnessException>(() => shallow.MergeBaseAsync("/repo", "main", ["HEAD"], cancellationToken));
        Assert.Contains("'main' and 'HEAD'", asked.Message, StringComparison.Ordinal);
        Assert.Contains("git fetch --unshallow", asked.Message, StringComparison.Ordinal);

        var (unknown, _) = Scripted(Exited(1), Exited(128, stderr: "fatal: unable to read the shallow file"));
        var untold = await Assert.ThrowsAsync<HarnessException>(() => unknown.MergeBaseAsync("/repo", "main", ["HEAD"], cancellationToken));
        Assert.Equal(HarnessExit.CommandFailed, untold.ExitCode);
        Assert.Contains("could not say whether this clone holds their whole history", untold.Message, StringComparison.Ordinal);
        Assert.Contains("unable to read the shallow file", untold.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is shallow", untold.Message, StringComparison.Ordinal);

        var (older, _) = Scripted(Exited(1), Exited(0, "--is-shallow-repository\n"));
        var unasked = await Assert.ThrowsAsync<HarnessException>(() => older.MergeBaseAsync("/repo", "main", ["HEAD"], cancellationToken));
        Assert.Contains("could not say whether this clone holds their whole history: '--is-shallow-repository'", unasked.Message, StringComparison.Ordinal);

        var (timedOut, requests) = Scripted(Exited(1, timedOut: true));
        await Assert.ThrowsAsync<HarnessException>(() => timedOut.MergeBaseAsync("/repo", "main", ["HEAD"], cancellationToken));
        Assert.Single(requests);
    }

    /// <summary>
    /// Measured against several commits in a shallow clone, the first is where they part where any of them names it as a
    /// parent; the first naming one of them is no answer, since it may share more with another than with that one.
    /// </summary>
    [Fact]
    public async Task MergeBaseAsync_InAShallowClone_AgainstSeveral_AnswersOnlyWithAParentOneOfThemNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string Main = "1111111111111111111111111111111111111111";
        const string Head = "2222222222222222222222222222222222222222";
        const string Merging = "3333333333333333333333333333333333333333";
        const string Tree = "tree 4444444444444444444444444444444444444444\n";

        var (named, _) = Scripted(
            Exited(1), Exited(0, "true\n"), Exited(0, Main + "\n"), Exited(0, Head + "\n"),
            Exited(0, Tree + "parent 5555555555555555555555555555555555555555\n\n"), Exited(0, Merging + "\n"),
            Exited(0, Tree + $"parent {Main}\n\n"));
        Assert.Equal(Main, await named.MergeBaseAsync("/repo", "main", ["HEAD", Merging], cancellationToken));

        var (naming, _) = Scripted(
            Exited(1), Exited(0, "true\n"), Exited(0, Main + "\n"), Exited(0, Head + "\n"),
            Exited(0, Tree + "\n"), Exited(0, Merging + "\n"), Exited(0, Tree + "\n"),
            Exited(0, Tree + $"parent {Head}\n\n"));
        var asked = await Assert.ThrowsAsync<HarnessException>(() => naming.MergeBaseAsync("/repo", "main", ["HEAD", Merging], cancellationToken));
        Assert.Contains($"'main' and 'HEAD' merged with '{Merging}'", asked.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every name is asked for as the commit it names, after git's options end: one that starts with a dash is a name to
    /// look up, and a short id a blob shares is still its commit's. Several are asked at once, as the commit merging them
    /// would be.
    /// </summary>
    [Fact]
    public async Task MergeBaseAsync_AsksForEachNameAsACommit_AfterTheOptionsEnd()
    {
        const string Base = "1111111111111111111111111111111111111111";
        var (git, requests) = Scripted(Exited(0, Base + "\n"));

        Assert.Equal(Base, await git.MergeBaseAsync("/repo", "--octopus", ["HEAD", "abc1234"], TestContext.Current.CancellationToken));
        Assert.Equal(
            ["merge-base", "--end-of-options", "--octopus^{commit}", "HEAD^{commit}", "abc1234^{commit}"],
            Assert.Single(requests).Arguments);
    }

    /// <summary>Whether a clone is shallow is git's answer, and no answer where git fails or echoes a question it does not know.</summary>
    [Fact]
    public async Task IsShallowAsync_IsGitsAnswer_OrNoneWhereGitGivesNone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await Scripted(Exited(0, "true\n")).Client.IsShallowAsync("/repo", cancellationToken));
        Assert.False(await Scripted(Exited(0, "false\n")).Client.IsShallowAsync("/repo", cancellationToken));
        Assert.Null(await Scripted(Exited(0, "--is-shallow-repository\n")).Client.IsShallowAsync("/repo", cancellationToken));
        Assert.Null(await Scripted(Exited(128, stderr: "fatal: not a git repository")).Client.IsShallowAsync("/repo", cancellationToken));
    }

    /// <summary>
    /// An answer of other than as many lines as git was asked for is refused in the words it always was: which files the
    /// commit holds cannot be told.
    /// </summary>
    [Fact]
    public async Task HeldAtAsync_RefusesAnAnswerShortOfWhatItAsked_SayingWhichFilesItHoldsCannotBeTold()
    {
        var (git, _) = Scripted(Exited(0, "commit 0123456789abcdef0123456789abcdef01234567\n"));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => git.HeldAtAsync("/repo", "abc", ["a.txt"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.Equal("git answered 1 line(s) for the 2 it was asked about abc, so which files it holds cannot be told.", refused.Message);
    }

    private static (GitClient Client, List<ProcessRequest> Requests) Scripted(params ProcessResult[] results)
    {
        var requests = new List<ProcessRequest>();
        var runner = Substitute.For<IProcessRunner>();

        // In order, and the last again for every request after it.
        runner.RunAsync(Arg.Do<ProcessRequest>(requests.Add), Arg.Any<CancellationToken>()).Returns(results[0], results[1..]);
        runner.FindExecutable("git").Returns("git");

        return (new GitClient(runner, Substitute.For<IHarnessOutput>(), localVariables: GitLocalVariables.Fixed(LocalNames)), requests);
    }

    /// <summary>
    /// The names git gives, handed to a client whose runner answers every request alike: asked through it,
    /// the question would be answered as if it were the command under test.
    /// </summary>
    private static readonly string[] LocalNames =
    [
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT", "GIT_OBJECT_DIRECTORY",
        "GIT_DIR", "GIT_WORK_TREE", "GIT_IMPLICIT_WORK_TREE", "GIT_GRAFT_FILE", "GIT_INDEX_FILE", "GIT_NO_REPLACE_OBJECTS",
        "GIT_REPLACE_REF_BASE", "GIT_PREFIX", "GIT_SHALLOW_FILE", "GIT_COMMON_DIR",
    ];

    private static ProcessResult Exited(int exitCode, string standardOutput = "", string stderr = "", bool timedOut = false)
        => new(exitCode, standardOutput, stderr, TimeSpan.Zero, timedOut);
}

public sealed class GitCommandResultTests
{
    [Fact]
    public void ATimedOutCommand_NeverSucceeds_WhateverItsExitCode()
    {
        var result = new GitCommandResult(0, string.Empty, string.Empty, TimedOut: true);

        Assert.False(result.Succeeded);
        Assert.Contains("time budget", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureMessage_PrefersStandardError_ThenStandardOutput()
    {
        Assert.Equal("from stderr", new GitCommandResult(1, "from stdout", " from stderr \n").FailureMessage);
        Assert.Equal("from stdout", new GitCommandResult(1, "from stdout\n", "  ").FailureMessage);
    }

    [Fact]
    public void FailureMessage_IsNeverEmpty()
    {
        // Callers interpolate it after a colon; nothing after the colon explains nothing.
        Assert.Equal("git exited with code 128.", new GitCommandResult(128, string.Empty, string.Empty).FailureMessage);
    }

    [Fact]
    public void OutputLines_DropsBlankLinesAndCarriageReturns()
    {
        var result = new GitCommandResult(0, "one\r\n\r\ntwo\n\n", string.Empty);

        Assert.Equal(["one", "two"], result.OutputLines);
    }
}

/// <summary>Runs every program through <paramref name="inner"/>, and keeps what each was asked to start.</summary>
internal sealed class CountingProcesses(IProcessRunner inner) : IProcessRunner
{
    private readonly List<ProcessRequest> _started = [];

    /// <summary>Every program started, in order.</summary>
    public IReadOnlyList<ProcessRequest> Started
    {
        get
        {
            lock (_started)
            {
                return [.. _started];
            }
        }
    }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        lock (_started)
        {
            _started.Add(request);
        }

        return inner.RunAsync(request, cancellationToken);
    }

    public string? FindExecutable(string command) => inner.FindExecutable(command);
}
