using System.Text.Json.Nodes;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The probe a run's start and its orient step read alike (slice 20a): the branch and the commit, a merge under way,
/// what is staged and changed, the remote the run's branch goes to and the branch it names its default - read by
/// <see cref="GitRemotes"/> over git itself - and what a run cannot start from; with the pull request's base filled by
/// the one template grammar, and the glob patterns two work items own told apart where they meet.
/// </summary>
public sealed class FlowCheckoutTests : IDisposable
{
    /// <summary>Every directory a test made, removed once it ends.</summary>
    private readonly List<TempDirectory> _made = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var made in _made)
        {
            made.Dispose();
        }
    }

    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    private static readonly WorkflowDefinition Bare = Parse("""
        name: flow
        steps:
          - name: orient
            type: orient
        """);

    /// <summary>A repository on branch 'work', and a bare remote 'origin' that holds 'main' and names it its default.</summary>
    private async Task<(HarnessFactory Harness, TempDirectory Repository, TempDirectory Remote)> WithOriginAsync()
    {
        var harness = new HarnessFactory();
        var repository = new TempDirectory();
        var remote = new TempDirectory();
        _made.AddRange([repository, remote]);

        await harness.InitializeGitRepositoryAsync(repository.Path, Token);
        await harness.RunGitAsync(remote.Path, ["init", "--quiet", "--bare", "."], Token);
        await harness.RunGitAsync(repository.Path, ["branch", "-M", "main"], Token);
        await harness.RunGitAsync(repository.Path, ["remote", "add", "origin", remote.Path], Token);
        await harness.RunGitAsync(repository.Path, ["push", "--quiet", "-u", "origin", "main"], Token);
        await harness.RunGitAsync(repository.Path, ["remote", "set-head", "origin", "main"], Token);
        await harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "work"], Token);

        return (harness, repository, remote);
    }

    /// <summary>The remote's own HEAD names its default; a branch tracks the remote it was pushed to; the remotes are listed.</summary>
    [Fact]
    public async Task TheRemotesDefaultBranch_IsWhatItsHeadNames_AndTheTrackedRemoteIsRead()
    {
        var (harness, repository, _) = await WithOriginAsync();
        var git = harness.GitClient;

        Assert.Equal(["origin"], await git.ListRemotesAsync(repository.Path, Token));
        Assert.Equal("origin", await git.TrackedRemoteAsync(repository.Path, "main", Token));
        Assert.Null(await git.TrackedRemoteAsync(repository.Path, "work", Token));
        Assert.Equal(new GitDefaultBranch("origin/main", "origin/main"), await git.DefaultBranchAsync(repository.Path, "origin", Token));
        Assert.Equal("work", await git.GetBranchAsync(repository.Path, Token));
    }

    /// <summary>
    /// A remote whose HEAD names no branch here is read by its conventional branches, the one it named said; one with
    /// neither names none.
    /// </summary>
    [Fact]
    public async Task ARemoteWhoseHeadNamesNothingHere_IsReadByItsConventionalBranches()
    {
        var (harness, repository, _) = await WithOriginAsync();
        var git = harness.GitClient;

        // Its HEAD names a branch since renamed: main is still there by convention.
        await harness.RunGitAsync(repository.Path, ["symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/trunk"], Token);
        Assert.Equal(new GitDefaultBranch("origin/main", "origin/trunk"), await git.DefaultBranchAsync(repository.Path, "origin", Token));

        // Its HEAD named nowhere, and nothing conventional there: none.
        await harness.RunGitAsync(repository.Path, ["remote", "set-head", "origin", "--delete"], Token);
        await harness.RunGitAsync(repository.Path, ["update-ref", "-d", "refs/remotes/origin/main"], Token);
        Assert.Equal(new GitDefaultBranch(null, null), await git.DefaultBranchAsync(repository.Path, "origin", Token));
    }

    /// <summary>
    /// The probe reads the branch, the commit, the staged and changed paths, and the remote: origin, whose default is
    /// main - which a pull request with no base of its own is opened against.
    /// </summary>
    [Fact]
    public async Task TheProbe_ReadsTheCheckout_AndTheRemotesDefault()
    {
        var (harness, repository, _) = await WithOriginAsync();
        repository.WriteFile("staged.txt", "staged\n");
        await harness.RunGitAsync(repository.Path, ["add", "staged.txt"], Token);
        repository.WriteFile("loose.txt", "loose\n");

        var checkout = await FlowCheckout.ReadAsync(harness.GitClient, repository.Path, Bare, Token);

        Assert.Equal("work", checkout.Branch);
        Assert.Equal((await harness.RunGitAsync(repository.Path, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim(), checkout.Head);
        Assert.False(checkout.MergeInProgress);
        Assert.Equal(["staged.txt"], checkout.Staged);
        Assert.Equal(["loose.txt", "staged.txt"], checkout.Dirty);
        Assert.True(checkout.HasRemote);
        Assert.Equal("origin", checkout.Remote);
        Assert.Equal("main", checkout.RemoteDefault);
        Assert.Equal("main", checkout.BaseBranch(null));
        Assert.Equal("release", checkout.BaseBranch("release"));
    }

    /// <summary>
    /// The remote is the push step's where it names one, else the remote the branch tracks - a branch of this repository's
    /// own being none - else origin, else the only one.
    /// </summary>
    [Fact]
    public async Task TheRemote_IsThePushStepsThenTheTrackedThenOriginThenTheOnlyOne()
    {
        var (harness, repository, remote) = await WithOriginAsync();
        await harness.RunGitAsync(repository.Path, ["remote", "add", "upstream", remote.Path], Token);
        await harness.RunGitAsync(repository.Path, ["fetch", "--quiet", "upstream"], Token);

        var pushing = Parse("""
            name: flow
            steps:
              - name: orient
                type: orient
              - name: send
                type: push
                remote: upstream
                dependsOn: [orient]
            """);

        Assert.Equal("upstream", (await FlowCheckout.ReadAsync(harness.GitClient, repository.Path, pushing, Token)).Remote);
        Assert.Equal("origin", (await FlowCheckout.ReadAsync(harness.GitClient, repository.Path, Bare, Token)).Remote);

        await harness.RunGitAsync(repository.Path, ["branch", "--quiet", "--set-upstream-to", "main"], Token);
        Assert.Equal("origin", (await FlowCheckout.ReadAsync(harness.GitClient, repository.Path, Bare, Token)).Remote);

        await harness.RunGitAsync(repository.Path, ["branch", "--quiet", "--set-upstream-to", "upstream/main"], Token);
        Assert.Equal("upstream", (await FlowCheckout.ReadAsync(harness.GitClient, repository.Path, Bare, Token)).Remote);

        await harness.RunGitAsync(repository.Path, ["branch", "--quiet", "--unset-upstream"], Token);
        await harness.RunGitAsync(repository.Path, ["remote", "remove", "origin"], Token);
        var only = await FlowCheckout.ReadAsync(harness.GitClient, repository.Path, Bare, Token);
        Assert.Equal("upstream", only.Remote);
    }

    /// <summary>With no remote there is none, nothing it names, and no base branch where the pull request names none.</summary>
    [Fact]
    public async Task WithNoRemote_ThereIsNoBaseBranch_ButThePullRequestsOwn()
    {
        var harness = new HarnessFactory();
        using var repository = new TempDirectory();
        await harness.InitializeGitRepositoryAsync(repository.Path, Token);

        var checkout = await FlowCheckout.ReadAsync(harness.GitClient, repository.Path, Bare, Token);

        Assert.False(checkout.HasRemote);
        Assert.Null(checkout.Remote);
        Assert.Null(checkout.RemoteDefault);
        Assert.Null(checkout.BaseBranch(null));
        Assert.Equal("develop", checkout.BaseBranch("develop"));
    }

    /// <summary>A detached HEAD reads no branch, and a merge under way is read as one.</summary>
    [Fact]
    public async Task ADetachedHeadAndAMergeUnderWay_AreRead()
    {
        var harness = new HarnessFactory();
        using var repository = new TempDirectory();
        await harness.InitializeGitRepositoryAsync(repository.Path, Token);
        await harness.RunGitAsync(repository.Path, ["branch", "-M", "main"], Token);
        await harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "other"], Token);
        repository.WriteFile("README.md", "theirs\n");
        await harness.RunGitAsync(repository.Path, ["commit", "--quiet", "-am", "theirs"], Token);
        await harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "main"], Token);
        repository.WriteFile("README.md", "ours\n");
        await harness.RunGitAsync(repository.Path, ["commit", "--quiet", "-am", "ours"], Token);

        var conflicted = await harness.GitClient.RunAsync(repository.Path, ["merge", "--quiet", "other"], cancellationToken: Token);
        Assert.False(conflicted.Succeeded);
        Assert.True((await FlowCheckout.ReadAsync(harness.GitClient, repository.Path, Bare, Token)).MergeInProgress);

        await harness.RunGitAsync(repository.Path, ["merge", "--abort"], Token);
        await harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "--detach"], Token);
        Assert.Null((await FlowCheckout.ReadAsync(harness.GitClient, repository.Path, Bare, Token)).Branch);
    }

    /// <summary>What a run cannot start from, worded once, each in its turn; a checkout on a branch of its own, clean of staged changes, starts.</summary>
    [Fact]
    public void WhatARunCannotStartFrom_IsSaid()
    {
        static FlowCheckout On(string? branch, bool merging = false, params string[] staged)
            => new(branch, "abc", merging, staged, staged, ["origin"], "origin", "main");

        Assert.Equal(
            "the main checkout's HEAD is detached, and a run commits its work on a branch: check out a branch of its own for it",
            On(null).StartRefusal("main"));
        Assert.Equal(
            "the main checkout is on 'main', the branch the run's pull request is opened against, and a run commits its work on a branch of its own: create one for it and check it out",
            On("main").StartRefusal("main"));
        Assert.Equal(
            "a merge is under way in the main checkout, and a run folds its work into the tree the merge is writing: finish the merge, or abort it",
            On("work", merging: true).StartRefusal("main"));
        Assert.Equal(
            "the main checkout has staged changes (6 path(s): a, b, c, d, e, ...), and a run's commit stages what it folded alone: commit them, or unstage them",
            On("work", false, "a", "b", "c", "d", "e", "f").StartRefusal("main"));
        Assert.Null(On("work").StartRefusal("main"));

        // Where more than one holds, the first in that order is the one said.
        Assert.Equal(On(null).StartRefusal("main"), On(null, merging: true, "a").StartRefusal("main"));
        Assert.Equal(On("main").StartRefusal("main"), On("main", merging: true, "a").StartRefusal("main"));
        Assert.Equal(On("work", merging: true).StartRefusal("main"), On("work", true, "a").StartRefusal("main"));

        // The base is the pull request's where it names one, else the remote's default branch; with neither, any branch starts.
        Assert.Equal(On("main").StartRefusal("main"), On("main").StartRefusal(null));
        Assert.Null(On("main").StartRefusal("release"));
        Assert.Null((On("main") with { RemoteDefault = null }).StartRefusal(null));
    }

    /// <summary>
    /// A start whose base cannot be told, where the repository has a remote - no pull-request base, and the remote names
    /// no default here, or no remote is the run's - says so, naming the branch it held to none and the way to tell it;
    /// a base told, a detached HEAD, which refuses, and no remote at all say nothing.
    /// </summary>
    [Fact]
    public void AStartWhoseBaseCannotBeTold_SaysSo()
    {
        var unknown = new FlowCheckout("work", "abc", false, [], [], ["origin"], "origin", null);

        Assert.Equal(
            "the branch the run's pull request is opened against cannot be told here - no pull-request step names a base, and remote "
                + "'origin' names no default branch here - so the main checkout's branch 'work' was held to none: name the base on the "
                + "pull-request step, or read the remote's own with git remote set-head origin --auto",
            unknown.BaseUnknown(null));
        Assert.Equal(
            "the branch the run's pull request is opened against cannot be told here - no pull-request step names a base, and no remote "
                + "is the one the run's branch goes to (the push step names none, the branch tracks none, and none is origin) - so the main "
                + "checkout's branch 'work' was held to none: name the base on the pull-request step, or the remote on the push step",
            (unknown with { Remotes = ["upstream", "fork"], Remote = null }).BaseUnknown(null));
        Assert.Null(unknown.BaseUnknown("release"));
        Assert.Null((unknown with { RemoteDefault = "main" }).BaseUnknown(null));
        Assert.Null((unknown with { Branch = null }).BaseUnknown(null));
        Assert.Null((unknown with { Remotes = [], Remote = null }).BaseUnknown(null));
    }

    /// <summary>A branch tracks no remote only where git says its key is not set: a configuration git cannot read is said, never read as none.</summary>
    [Fact]
    public async Task ATrackedRemoteGitCannotRead_IsSaid_NeverReadAsNone()
    {
        var (harness, repository, _) = await WithOriginAsync();
        File.AppendAllText(Path.Combine(repository.Path, ".git", "config"), "[branch \"work\"\n\tremote = broken\n");

        var refused = await Assert.ThrowsAsync<HarnessException>(() => harness.GitClient.TrackedRemoteAsync(repository.Path, "work", Token));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.StartsWith("git could not read the remote branch 'work' tracks: ", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The pull request's base is filled from the inputs and the run's name; with no pull-request base, none; a name that reads nothing, why.</summary>
    [Fact]
    public void ThePullRequestsBase_IsFilledFromTheInputsAndTheRunsName()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: orient
                type: orient
              - name: pr
                type: pull-request
                dependsOn: [orient]
                title: t
                body: body.md
                base: 'release/{input.line}-{run.name}-{{x}}'
            """);

        Assert.Equal("release/7-r2-{x}", FlowTemplate.PullRequestBase(definition, new Dictionary<string, string> { ["line"] = "7" }, "r2", out var problem));
        Assert.Null(problem);

        Assert.Null(FlowTemplate.PullRequestBase(definition, new Dictionary<string, string>(), "r2", out var missing));
        Assert.Equal("'{input.line}' reads nothing", missing);

        Assert.Null(FlowTemplate.PullRequestBase(Bare, new Dictionary<string, string>(), "r2", out var none));
        Assert.Null(none);
    }

    /// <summary>
    /// A template is read by the configuration's one brace grammar: a brace doubled - either - stands for one, another
    /// expander's ${...} is left as written, a placeholder's name is what stands before its '|', trimmed; and one that
    /// names nothing, or is never closed, fills nothing and says so - as the definition's check refuses it.
    /// </summary>
    [Fact]
    public void ATemplate_IsReadByTheOneBraceGrammar()
    {
        static FlowExpression.PathRead Read(FlowExpression.Path path)
            => path.Root == "input" ? new(FlowValue.Of(new JsonObject { ["line"] = "7" }), 0) : new(FlowValue.Missing, 0);

        Assert.Equal("{7} ${HOME} {x}} 7", FlowTemplate.Fill("{{{input.line}}} ${HOME} {{x}}}} { input.line | text }", Read, out var problem));
        Assert.Null(problem);

        Assert.Null(FlowTemplate.Fill("a {} b", Read, out var empty));
        Assert.Equal("'{}' is no name in braces", empty);
        Assert.Equal("the placeholder at column 3 of line 1 names nothing (a brace meant literally is written twice)", WorkflowExpressions.TemplateSyntaxProblem("a {} b"));

        Assert.Null(FlowTemplate.Fill("a {input.line", Read, out var open));
        Assert.Equal("'{input.line' is no name in braces", open);
        Assert.Equal("the placeholder at column 3 of line 1 is never closed", WorkflowExpressions.TemplateSyntaxProblem("a {input.line"));

        Assert.Null(WorkflowExpressions.TemplateSyntaxProblem("{{ json: {{\"a\": 1}} }} ${PATH}"));
        Assert.Equal(["input.line"], WorkflowExpressions.TemplateReads("{{x}} ${y} {input.line|text}").Select(read => read.Whole));
    }

    /// <summary>Two patterns overlap where some path - there or not yet - is matched by both, and not otherwise.</summary>
    [Theory]
    [InlineData("src/a.cs", "src/a.cs", true)]
    [InlineData("src/**", "src/a/b.cs", true)]
    [InlineData("src/**", "srcx/a.cs", false)]
    [InlineData("src/*.cs", "src/a.*", true)]
    [InlineData("src/*.cs", "src/a.txt", false)]
    [InlineData("src/a", "src/a/b.cs", true)]
    [InlineData("docs/", "docs/x.md", true)]
    [InlineData("a.cs", "deep/down/a.cs", true)]
    [InlineData("/a.cs", "deep/a.cs", false)]
    [InlineData("src/**/test/*.cs", "src/x/y/test/z.cs", true)]
    [InlineData("src/**/test/*.cs", "lib/test/z.cs", false)]
    [InlineData("src/?.cs", "src/ab.cs", false)]
    [InlineData("src/?b.cs", "src/a?.cs", true)]
    [InlineData("tests/**", "src/**", false)]
    public void TwoPatterns_OverlapWhereSomePathMatchesBoth(string left, string right, bool overlap)
    {
        Assert.Equal(overlap, GlobPattern.Parse(left).Overlaps(GlobPattern.Parse(right)));
        Assert.Equal(overlap, GlobPattern.Parse(right).Overlaps(GlobPattern.Parse(left)));
    }

    /// <summary>
    /// Overlap is what matching says it is: wherever a path matches both of two patterns, the two overlap - checked
    /// over every pair of a set of patterns and a set of paths, as each pattern matches them.
    /// </summary>
    [Fact]
    public void TwoPatternsThatMatchOnePath_Overlap()
    {
        string[] patterns =
        [
            "src/a.cs", "src/**", "**/a.cs", "src/*.cs", "src/*/b.cs", "docs/**", "*.md", "src/a?.cs", "src/", "src/a",
            "**", "src/**/b.cs", "a/**/b/**/c", "/a.cs", "a.cs", "src/**/test/*.cs", "**/test/**", "x?/**",
        ];
        string[] paths =
        [
            "src/a.cs", "src/ab.cs", "src/x/b.cs", "src/x/y/b.cs", "docs/x.md", "x.md", "a/b/c", "a/x/b/y/c", "src/b.cs",
            "a.cs", "deep/a.cs", "src/a/b.cs", "src/x/test/z.cs", "xy/z", "lib/test/z.cs", "docs/deep/a.cs",
        ];

        foreach (var left in patterns)
        {
            foreach (var right in patterns)
            {
                if (paths.FirstOrDefault(path => GlobPattern.Parse(left).Matches(path) && GlobPattern.Parse(right).Matches(path)) is { } both)
                {
                    Assert.True(GlobPattern.Parse(left).Overlaps(GlobPattern.Parse(right)), $"'{left}' and '{right}' both match '{both}'");
                }
            }
        }
    }

    /// <summary>Names compare by the tree's case rule.</summary>
    [Fact]
    public void Overlap_ComparesNamesByTheTreesCaseRule()
    {
        Assert.False(GlobPattern.Parse("Src/A.cs").Overlaps(GlobPattern.Parse("src/a.cs")));
        Assert.True(GlobPattern.Parse("Src/A.cs").Overlaps(GlobPattern.Parse("src/a.cs"), StringComparison.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => GlobPattern.Parse("a").Overlaps(GlobPattern.Parse("a"), StringComparison.InvariantCulture));
    }
}
