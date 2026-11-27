using System.Diagnostics;
using System.Globalization;
using System.Text;
using DssHarness.Core.Agents;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// A workflow used as a step from beyond the repository: the source's spelling, the fetch by git itself - a ref resolved
/// to a commit of the repository's own through every ref it lists, the commit's tree read whole at the workflow's
/// directory, every path under it held to the tree's rule and every blob checked against the hash the tree gives - the
/// hash cache it is kept in as bytes, read again as it is read, the trust a configuration alone grants and the one
/// credential a host is given, the offline fallback, an agent's command that reads the cache alone, the pruning, and
/// the export that vendors the whole for review. Each remote is a real repository git serves at its file:// address.
/// </summary>
public sealed class RemoteWorkflowUseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Source = "dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1";
    private const string Directory = "workflows/security-review";
    private const string Yml = "workflows/security-review/security-review.yml";
    private const string PromptPath = "workflows/security-review/prompts/review.md";
    private const string GhesSource = "ghe.acme.internal/acme/shared/workflows/security-review/security-review.yml@v1";

    /// <summary>The commit a resolution handed to the resolver names, which no remote of the fixture holds.</summary>
    private const string Pinned = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A commit the cache holds no entry for and no remote of the fixture holds.</summary>
    private const string Unheld = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>The workflow the repository holds, as the fetch answers it.</summary>
    private const string Definition =
        """
        name: security-review
        inputs:
          - name: paths
            kind: value
        steps:
          - name: review
            type: reason
            agent: reviewer
            prompt: prompts/review.md
            outputs: schemas/review.json
        """;

    private const string Prompt = "Review the paths you are given.\n";

    /// <summary>
    /// What a refusal of a command taken for an agent's says of where the cache is filled from: a person's own shell, and
    /// the two commands that fetch there - never the command that was just refused, run again where it was.
    /// </summary>
    private const string FilledFromAShell =
        "such a command never reaches the forge nor writes the cache, and nothing was asked of the forge. A person fills the cache from a shell of the person's own, which no process of this tool's started and which carries no DSSHARNESS_UNDER_AGENT, where 'dssharness validate-workflow' fetches every remote use of the workflows the repository declares, and 'dssharness export-workflow --from <source> --to <directory>' the one it names";

    /// <summary>What a resolution whose refs git could not list, never reaching the host, says before git's own words.</summary>
    private const string NotReached =
        $"'{Source}' could not be resolved, the forge not being reached: git could not list the refs of 'dailysoftwaresystems/shared', never reaching 'github.com': ";

    /// <summary>The forms a source takes: an owner, a repository - dotted, as GitHub's may be - a workflow file's path in it, and a ref after @, a full SHA read in lower case; and a host under its own name, where there is one.</summary>
    [Fact]
    public void ASourceNamesAnOwnerARepositoryAPathAndARef()
    {
        Assert.Equal(
            ("github.com", "dailysoftwaresystems", "dss-harness", "workflows/pipeline/pipeline.yml", "v1.4.0"),
            AsParts("dailysoftwaresystems/dss-harness/workflows/pipeline/pipeline.yml@v1.4.0"));
        Assert.Equal(
            ("ghe.acme.internal", "acme", "shared", "workflows/pipeline/pipeline.yml", "main"),
            AsParts("ghe.acme.internal/acme/shared/workflows/pipeline/pipeline.yml@main"));
        Assert.Equal(
            ("github.com", "acme", "shared", "workflows/pipeline/pipeline.yml", "1111111111111111111111111111111111111111"),
            AsParts("acme/shared/workflows/pipeline/pipeline.yml@1111111111111111111111111111111111111111"));

        // The organisation's .github repository, the home of its shared workflows, and a dotted repository's name.
        Assert.Equal(("github.com", "acme", ".github", "workflows/x/x.yml", "v1"), AsParts("acme/.github/workflows/x/x.yml@v1"));
        Assert.Equal(("github.com", "acme", "my.repo", "workflows/x/x.yml", "v1"), AsParts("acme/my.repo/workflows/x/x.yml@v1"));

        // A full SHA spelled in upper case is the same commit, one cache entry and one name.
        Assert.Equal(
            ("github.com", "acme", "shared", "workflows/x/x.yml", "abcdef0123456789abcdef0123456789abcdef01"),
            AsParts("acme/shared/workflows/x/x.yml@ABCDEF0123456789ABCDEF0123456789ABCDEF01"));

        Assert.Null(WorkflowRemote.Source("./pipeline/pipeline.yml"));
        Assert.Null(WorkflowRemote.Source("../pipeline/pipeline.yml"));
        Assert.Null(WorkflowRemote.Source("dailysoftwaresystems/shared/workflows/pipeline/pipeline.yml"));
        Assert.Null(WorkflowRemote.Source("dailysoftwaresystems/shared@v1"));
        Assert.Null(WorkflowRemote.Source("dailysoftwaresystems/shared/pipeline.yml@v1"));
        Assert.Null(WorkflowRemote.Source("acme/../workflows/x/x.yml@v1"));
        Assert.Null(WorkflowRemote.Source("acme/./workflows/x/x.yml@v1"));

        // A blank source names nothing remote, for every caller alike - never an exception of the runtime's.
        Assert.Null(WorkflowRemote.Source(string.Empty));
        Assert.Null(WorkflowRemote.Source(" \t"));

        static (string, string, string, string, string)? AsParts(string source)
            => WorkflowRemote.Source(source) is { } parsed ? (parsed.Host, parsed.Owner, parsed.Repo, parsed.File, parsed.Ref) : null;
    }

    /// <summary>
    /// A ref git would not take is refused by git's own rules before anything is asked of the remote: one that reads as
    /// part of a refspec or an address could reach another ref.
    /// </summary>
    [Theory]
    [InlineData("../heads/main", "it holds '..'")]
    [InlineData("v1.", "it ends with '.'")]
    [InlineData("v1?x", "it holds '?'")]
    [InlineData("v1 x", "it holds ' '")]
    [InlineData("rel//v1", "one of its parts is empty")]
    [InlineData("rel/.v1", "one of its parts begins with '.'")]
    [InlineData("v1.lock", "one of its parts ends with '.lock'")]
    [InlineData("v@{1}", "it holds '@{'")]
    [InlineData("v1~1", "it holds '~'")]
    public async Task ARefGitWouldNotTake_IsRefused_BeforeAnythingIsAsked(string @ref, string why)
    {
        using var temp = new TempDirectory();
        var (remote, _, fetcher, _) = Fixture(temp);
        var source = new WorkflowRemoteSource("github.com", "dailysoftwaresystems", "shared", Yml, @ref);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(source, Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"'{source.Spelled}' names '{@ref}' is no ref git takes: {why}; nothing was asked of the forge", refused.Message);
        Assert.Empty(remote.Runs);
        Assert.Null(WorkflowRemote.RefProblem("v1.4.0"));
        Assert.Null(WorkflowRemote.RefProblem("release/2026-11"));
    }

    /// <summary>
    /// A tag is resolved to its commit through the repository's own refs, listed in one call; the tag alone is fetched
    /// at its tip, then the commit's tree and the directory's blobs are asked for by their ids, each blob hashed as git
    /// hashes one, github.com reached with git's own helpers and handed nothing - the whole kept in the cache, blobs by
    /// their git hash and the entry by its repository, commit and path, the resolution recorded with its time; the second
    /// resolution of the same tag lists the refs alone.
    /// </summary>
    [Fact]
    public async Task ATagIsResolved_ItsDirectoryWalkedFetchedAndHashChecked_KeptInTheCache()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var (remote, cache, fetcher, warned) = Fixture(temp, clock);
        var commit = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt));
        await remote.TagAsync("v1", commit);

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal((commit, Definition, false, false), (resolved.Commit, resolved.Definition, resolved.Moving, resolved.FromLastResolved));
        Assert.Equal(["ls-remote", "fetch refs/tags/v1", "fetch by id", "fetch by id"], remote.Asked);
        Assert.All(remote.Runs, run => Assert.Null(run.Values));

        var listed = Assert.Single(cache.List().Entries);

        Assert.Equal(("github.com", "dailysoftwaresystems", "shared", commit, Directory), (listed.Entry.Host, listed.Entry.Owner, listed.Entry.Repo, listed.Entry.Commit, listed.Entry.Path));
        Assert.Equal([PromptPath, Yml], [.. listed.Entry.Files.Select(file => file.Path).Order(StringComparer.Ordinal)]);
        Assert.Equal(
            [WorkflowCache.BlobSha(Prompt), WorkflowCache.BlobSha(Definition)],
            [.. listed.Entry.Files.OrderBy(file => file.Path, StringComparer.Ordinal).Select(file => file.Blob)]);
        Assert.Equal(
            new WorkflowCacheResolution("github.com", "dailysoftwaresystems", "shared", Directory, "v1", commit, Branch: false, clock.GetUtcNow()),
            cache.LastResolved("github.com", "dailysoftwaresystems", "shared", Directory, "v1"));
        Assert.Equal(
            $"github.com/dailysoftwaresystems/shared at {ReportText.Commit(commit)}: {Directory} (2 file(s), {(Encoding.UTF8.GetByteCount(Definition) + Encoding.UTF8.GetByteCount(Prompt)).ToString("N0", CultureInfo.InvariantCulture)} bytes, fetched 2026-09-30, last used 2026-09-30, the newest resolution of the tag v1 on 2026-09-30)",
            WorkflowCacheService.EntryLine(listed));
        Assert.Empty(warned);

        // The same tag again: one call re-resolves it - a tag needs that, at every run's start - and the cache answers
        // the commit it holds, with nothing fetched.
        remote.Clear();
        var again = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal((commit, Definition, false, false), (again.Commit, again.Definition, again.Moving, again.FromLastResolved));
        Assert.Equal(["ls-remote"], remote.Asked);
    }

    /// <summary>
    /// An annotated tag's ref names its tag object, never the commit: the tag object is fetched with the tag and peeled to
    /// the commit, and that commit - a real one - is what is recorded, named and cached; a tag of a tree names no commit a
    /// workflow is read at.
    /// </summary>
    [Fact]
    public async Task AnAnnotatedTag_IsPeeledToTheCommitItNames()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        _ = await remote.AnnotatedTagAsync("v1", commit, "commit");

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal(commit, resolved.Commit);
        Assert.Equal(commit, Assert.Single(cache.List().Entries).Entry.Commit);
        Assert.Equal(["ls-remote", "fetch refs/tags/v1", "fetch by id", "fetch by id"], remote.Asked);

        // A tag of a tree is no commit a workflow is read at.
        var tree = await remote.TreeOfAsync(commit);
        _ = await remote.AnnotatedTagAsync("v2", tree, "tree");

        var ofATree = await Assert.ThrowsAsync<HarnessException>(() => Over(cache, remote).ResolveAsync(Parsed() with { Ref = "v2" }, Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, ofATree.ExitCode);
        Assert.Equal(
            $"'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v2': the tag 'v2' names a tree, '{tree}', and no commit; a workflow is read at a commit. Nothing was cached",
            ofATree.Message);
    }

    /// <summary>
    /// A blob whose bytes, as the clone reads them, do not hash to what the commit's tree says of it is refused, and
    /// nothing is cached: the cache names each blob by that hash, so what a run would run must be what the repository it
    /// came from says it is.
    /// </summary>
    [Fact]
    public async Task ABlobThatDoesNotHashToWhatTheTreeSays_IsRefused_AndNothingIsCached()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);
        remote.BlobsRead = (_, read) => [.. read.Select(_ => (byte[]?)Encoding.UTF8.GetBytes("something else entirely\n"))];

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}': the blob '{WorkflowCache.BlobSha(Definition)}' of '{Yml}' does not hash to what the commit's tree says of it; nothing was cached",
            refused.Message);
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
        Assert.False(System.IO.Directory.Exists(cache.Directory));
    }

    /// <summary>
    /// A use naming a file its directory does not hold at the commit - a typo - is refused naming the file and the
    /// workflow files the directory does hold, before any blob is fetched, and nothing is cached: no later resolution
    /// fetches the directory again to fail the same way.
    /// </summary>
    [Fact]
    public async Task AFileTheUseNamesThatTheDirectoryDoesNotHold_IsRefusedNamingIt_BeforeAnyBlobIsFetched()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-reviews.yml", Definition), ("prompts/review.md", Prompt));
        await remote.TagAsync("v1", commit);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names '{Yml}', which the directory '{Directory}' does not hold at the commit '{commit}': its workflow files are 'workflows/security-review/security-reviews.yml'; nothing was fetched",
            refused.Message);

        // The commit's tree is asked for, and no blob.
        Assert.Equal(["ls-remote", "fetch refs/tags/v1", "fetch by id"], remote.Asked);
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>
    /// A path a git tree may hold and a file system reads as a way out of the directory - a backslash, a drive's colon -
    /// or two paths a file system that folds case reads as one, refuses the whole fetch before any blob is fetched, and
    /// nothing is cached: an export of the directory writes inside it and nowhere else.
    /// </summary>
    [Theory]
    [InlineData("..\\..\\escape.cmd", "'..\\..\\escape.cmd' holds '\\', which no path of a workflow's may")]
    [InlineData("C:\\x", "'C:\\x' is rooted")]
    [InlineData("notes/x:y.md", "'notes/x:y.md' holds ':', which no path of a workflow's may")]
    [InlineData("Security-Review.yml", "'Security-Review.yml' and 'security-review.yml' are one path where case is folded")]
    public async Task APathNoWorkflowsMayHold_IsRefused_AndNothingIsCached(string path, string problem)
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition), (path, Prompt));
        await remote.TagAsync("v1", commit);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"'{Source}' resolved to the commit '{commit}', whose directory '{Directory}' holds a path no workflow's may: {problem}. Nothing was fetched", refused.Message);
        Assert.Equal(["ls-remote", "fetch refs/tags/v1", "fetch by id"], remote.Asked);
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>
    /// A symbolic link in the directory is refused naming it, never cached as a file holding its target's path; a
    /// submodule is refused naming it, never dropped without a word.
    /// </summary>
    [Fact]
    public async Task ASymbolicLinkOrASubmodule_IsRefusedNamingIt()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var linked = await remote.CommitAsync([Tracked.Text("security-review.yml", Definition), Tracked.Link("prompts", "../shared-prompts")]);
        await remote.TagAsync("v1", linked);

        var link = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(
            $"'{Source}' resolved to the commit '{linked}', whose '{Directory}/prompts' is a symbolic link: a workflow's files are its directory's own, and a link is followed nowhere. Nothing was fetched",
            link.Message);

        var vendored = await remote.CommitAsync([Tracked.Text("security-review.yml", Definition), Tracked.Submodule("vendor", Unheld)]);
        await remote.TagAsync("v1", vendored);

        var submodule = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(
            $"'{Source}' resolved to the commit '{vendored}', whose '{Directory}/vendor' is a submodule: a workflow's files are its directory's own, and another repository's are not fetched with them. Nothing was fetched",
            submodule.Message);
        Assert.Equal((HarnessExit.Refused, HarnessExit.Refused), (link.ExitCode, submodule.ExitCode));
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>
    /// A listing of the refs that does not read as one - a line naming no object, an object that is no hash - is git's
    /// failure, said as one naming what was asked (<see cref="HarnessExit.CommandFailed"/>), never an exception of the
    /// runtime's that ends the command as a defect; and nothing is cached.
    /// </summary>
    [Theory]
    [InlineData("not a ref listing")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz\trefs/tags/v1")]
    [InlineData("0123\trefs/tags/v1")]
    public async Task ARefListingThatDoesNotRead_IsRefusedAsGitsFailure_NeverAnExceptionOfTheRuntimes(string line)
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        remote.Instead = arguments => arguments.Contains("ls-remote") ? new GitCommandResult(0, $"{line}\n", string.Empty, false) : null;

        var failed = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.CommandFailed, failed.ExitCode);
        Assert.Equal($"'{Source}' could not be resolved: git answered the refs it listed with what does not read as one ('{line}' names no object and ref); nothing was cached", failed.Message);
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>
    /// A repository the configuration does not allow is refused before anything is asked of the remote, and an allow
    /// list names an owner whole, its every repository, or one repository - dotted, as GitHub's may be - another
    /// host's under its own name alone.
    /// </summary>
    [Fact]
    public async Task ARepositoryTheConfigurationDoesNotAllow_IsRefusedBeforeAnythingIsAsked()
    {
        using var temp = new TempDirectory();
        var (remote, _, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), null, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names the repository 'dailysoftwaresystems/shared', which workflowSources.allow does not allow; a remote workflow directs agents and machine steps in this repository, so it is trusted by configuration alone; the allow list is empty, so no remote source is allowed until the repository allows its owner",
            refused.Message);
        Assert.Empty(remote.Runs);

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal(commit, resolved.Commit);

        Assert.True(WorkflowSourceRules.Allows(["dailysoftwaresystems"], "github.com", "dailysoftwaresystems", "shared"));
        Assert.True(WorkflowSourceRules.Allows(["dailysoftwaresystems/*"], "github.com", "dailysoftwaresystems", "shared"));
        Assert.True(WorkflowSourceRules.Allows(["dailysoftwaresystems/shared"], "github.com", "dailysoftwaresystems", "shared"));
        Assert.True(WorkflowSourceRules.Allows(["dailysoftwaresystems/.github"], "github.com", "dailysoftwaresystems", ".github"));
        Assert.False(WorkflowSourceRules.Allows(["dailysoftwaresystems/other"], "github.com", "dailysoftwaresystems", "shared"));
        Assert.False(WorkflowSourceRules.Allows(["dailysoftwaresystems/*"], "ghe.acme.internal", "dailysoftwaresystems", "shared"));
        Assert.True(WorkflowSourceRules.Allows(["ghe.acme.internal/dailysoftwaresystems/*"], "ghe.acme.internal", "dailysoftwaresystems", "shared"));
        Assert.True(WorkflowSourceRules.Allows(["ghe.acme.internal/dailysoftwaresystems"], "ghe.acme.internal", "dailysoftwaresystems", "shared"));
    }

    /// <summary>
    /// The pin holds the ref to a tag or a full commit SHA by default, a commit only where it says commit, and allows
    /// a branch - which moves, and is warned of - only where it says any; a branch is the repository's own ref of that
    /// name, as the one listing of its refs names it, fetched alone at its tip.
    /// </summary>
    [Fact]
    public async Task ThePinHoldsTheRefToATagOrACommit_ABranchOnlyWhereItSaysAny()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, warned) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.BranchAsync("main", commit);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names the ref 'v1', which is no tag of 'dailysoftwaresystems/shared'; workflowSources.pin is tag-or-commit, which takes a tag or a full commit SHA - a branch moves, and what a run ran must be what it said",
            refused.Message);
        Assert.Equal(["ls-remote"], remote.Asked);

        remote.Clear();
        var pinnedToACommit = await Assert.ThrowsAsync<HarnessException>(
            () => fetcher.ResolveAsync(Parsed(), new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "commit" }, NoVariables, recorded: null, Token));

        Assert.Equal(
            $"'{Source}' names the ref 'v1', which is no full commit SHA; workflowSources.pin is commit, which takes a full commit SHA only - a tag or a branch moves, and a commit is what it is",
            pinnedToACommit.Message);
        Assert.Empty(remote.Runs);

        await remote.BranchAsync("v1", commit);

        var any = await fetcher.ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "any" },
            NoVariables,
            recorded: null,
            Token);

        Assert.Equal((commit, true), (any.Commit, any.Moving));
        Assert.Equal(["ls-remote", "fetch refs/heads/v1", "fetch by id", "fetch by id"], remote.Asked);
        Assert.True(Assert.Single(cache.List().Entries).Resolutions.Single().Branch);
        Assert.Equal(
            [$"{WorkflowRemoteFetcher.Said}: '{Source}' names the branch 'v1', which moves: it resolved to the commit {commit} now, and may resolve to another at the next run - pin it by a tag or a commit where what a run ran must be what it said"],
            warned);

        // A ref that is neither a tag nor a branch of the repository.
        var neither = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(
            Parsed() with { Ref = "nowhere" },
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "any" },
            NoVariables,
            recorded: null,
            Token));

        Assert.Equal("'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@nowhere' names the ref 'nowhere', which is neither a tag nor a branch of 'dailysoftwaresystems/shared'; nothing was cached", neither.Message);
    }

    /// <summary>
    /// A repository that is not there, or that the credential cannot see, is refused as one, naming what git said - never
    /// "no tag of it", and never the network not being there.
    /// </summary>
    [Fact]
    public async Task AMissingRepository_IsRefusedAsOne_NeverAsAMissingTag()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved", Pin = "any" },
            NoVariables,
            recorded: null,
            Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith(
            $"'{Source}' names the repository 'dailysoftwaresystems/shared' on 'github.com', which git could not read there: it is misspelt, or the credential it is reached with cannot see it (",
            refused.Message,
            StringComparison.Ordinal);
        Assert.EndsWith("); nothing was cached", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["ls-remote"], remote.Asked);
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>
    /// A commit named by its SHA - the strictest pin's only form - is fetched once git's own walk over every branch and
    /// tag of the repository finds one that reaches it, with no ref listed; and a SHA the server holds under no branch or
    /// tag of the repository - as GitHub serves a fork's commit under the repository's own name - is refused as the
    /// imposter it is, with nothing cached.
    /// </summary>
    [Fact]
    public async Task ACommitNamedByItsSha_IsFetchedOnlyWhereABranchOrTagOfTheRepositoryReachesIt()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commitOnly = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "commit" };
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.BranchAsync("main", commit);

        var resolved = await fetcher.ResolveAsync(Parsed() with { Ref = commit }, commitOnly, NoVariables, recorded: null, Token);

        Assert.Equal(commit, resolved.Commit);
        Assert.Equal(["fetch every ref", "fetch by id", "fetch by id"], remote.Asked);

        // A commit the server holds that no tag or branch of the repository holds or reaches.
        var imposter = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);
        await remote.BranchAsync("feature/x", commit);
        remote.Clear();

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed() with { Ref = imposter }, commitOnly, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@{imposter}' names the commit '{imposter}', which no branch or tag of 'dailysoftwaresystems/shared' reaches - every one of them was walked: GitHub serves a commit of any fork of a repository under the repository's own name, and one that is not the repository's own is never run; name a tag of it, or a commit one of its branches or tags holds. Nothing was cached",
            refused.Message);
        Assert.Equal(["fetch every ref"], remote.Asked);
        Assert.Equal([commit], cache.List().Entries.Select(listed => listed.Entry.Commit));
    }

    /// <summary>
    /// Where the remote cannot be reached and nothing stands in, the resolution fails naming what git said, with the code
    /// of the call that did not reach it - the network not being there is no refusal of the source, so a check never
    /// says it as a problem of the definition's - unless the configuration says last-resolved, and then the cache's
    /// newest resolution of the ref stands in, warned of.
    /// </summary>
    [Fact]
    public async Task Offline_NothingStandingIn_KeepsTheCallsOwnCode_UnlessLastResolvedStandsIn()
    {
        using var temp = new TempDirectory();
        var (remote, _, online, warned) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt));
        await remote.TagAsync("v1", commit);

        _ = await online.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        // The network is not there any more: the tag cannot be resolved, and the failure says what git said and what the
        // configuration would have stand in, with the code git's words for no connection are raised with.
        remote.Offline = true;
        var refused = await Assert.ThrowsAsync<HarnessException>(() => online.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.False(WorkflowResolution.RefusesTheSource(refused));
        Assert.StartsWith(NotReached, refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("; workflowSources.offline is refuse, so nothing stands in - last-resolved would stand at the cache's newest resolution of the ref", refused.Message, StringComparison.Ordinal);

        var stood = await online.ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" },
            NoVariables,
            recorded: null,
            Token);

        Assert.Equal((commit, Definition, true, false), (stood.Commit, stood.Definition, stood.FromLastResolved, stood.Moving));

        var said = Assert.Single(warned);

        Assert.StartsWith($"{WorkflowRemoteFetcher.Said}: {NotReached}", said, StringComparison.Ordinal);
        Assert.EndsWith($"; the workflow cache's newest resolution of 'v1' stands in, the commit {commit} it resolved to on 2026-09-30, as workflowSources.offline says last-resolved", said, StringComparison.Ordinal);

        // Where the cache holds no resolution of the ref, nothing stands in either, and the call's own code is kept.
        var none = await Assert.ThrowsAsync<HarnessException>(() => online.ResolveAsync(
            Parsed() with { Ref = "v2" },
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" },
            NoVariables,
            recorded: null,
            Token));

        Assert.Equal(HarnessExit.CommandFailed, none.ExitCode);
        Assert.EndsWith("; and the workflow cache holds no resolution of 'v2'", none.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cache standing in offline is held to the pin as the remote's own answer is: a ref the cache last resolved as a
    /// branch, under a repository that has since moved to the default pin, is refused as a branch the pin refuses - never
    /// run at the commit the branch last pointed to - and nothing stands in for it.
    /// </summary>
    [Fact]
    public async Task Offline_ABranchTheCacheLastResolved_IsRefusedWhereThePinTakesNone()
    {
        using var temp = new TempDirectory();
        var (remote, _, fetcher, warned) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.BranchAsync("v1", commit);

        // Resolved once as a branch, the pin then allowing one.
        var moving = await fetcher.ResolveAsync(Parsed(), new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "any" }, NoVariables, recorded: null, Token);

        Assert.True(moving.Moving);
        warned.Clear();

        // The repository moves to the default pin, and the remote cannot be reached.
        remote.Offline = true;
        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" },
            NoVariables,
            recorded: null,
            Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names the ref 'v1', which the workflow cache last resolved as a branch, and the cache stands in for no ref the pin refuses: workflowSources.pin is tag-or-commit, which takes a tag or a full commit SHA - a branch moves",
            refused.Message);
        Assert.Empty(warned);
    }

    /// <summary>
    /// An offline last-resolved stands at the ref's newest resolution: a tag that moved since its first fetch, and a
    /// resolution that found its commit already cached, are each the newest the cache records - never an older entry
    /// the cache happens to list first.
    /// </summary>
    [Fact]
    public async Task LastResolved_StandsAtTheNewestResolution_NeverAnOlderEntry()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var (remote, cache, fetcher, _) = Fixture(temp, clock);
        var lastResolved = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" };
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        var moved = await remote.CommitAsync(("security-review.yml", "name: security-review\nsteps: []\n"));

        // v1 resolves to the first commit, then - moved - to the second, a day later.
        await remote.TagAsync("v1", commit);
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token);
        clock.Advance(TimeSpan.FromDays(1));
        await remote.TagAsync("v1", moved);
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token);

        remote.Offline = true;
        Assert.Equal(moved, (await fetcher.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token)).Commit);

        // Moved back to the first commit, which the cache holds: no fetch, and still the newest resolution.
        remote.Offline = false;
        clock.Advance(TimeSpan.FromDays(1));
        await remote.TagAsync("v1", commit);
        remote.Clear();
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token);

        Assert.Equal(["ls-remote"], remote.Asked);

        remote.Offline = true;
        Assert.Equal(commit, (await fetcher.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token)).Commit);
        Assert.Equal(clock.GetUtcNow(), cache.LastResolved("github.com", "dailysoftwaresystems", "shared", Directory, "v1")!.ResolvedAt);
    }

    /// <summary>
    /// A remote that answered - a repository not there, a credential refused, access forbidden - is never the network not
    /// being there: the run is refused naming what git said, and the cache's last resolution never stands in for it,
    /// however the configuration says last-resolved.
    /// </summary>
    [Theory]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/dailysoftwaresystems/shared.git/' not found")]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/dailysoftwaresystems/shared.git/'")]
    [InlineData("fatal: unable to access 'https://github.com/dailysoftwaresystems/shared.git/': The requested URL returned error: 403")]
    public async Task ARemoteThatAnswered_IsNeverTheNetworkNotBeingThere(string said)
    {
        using var temp = new TempDirectory();
        var (remote, _, fetcher, warned) = Fixture(temp);
        var lastResolved = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" };
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token);

        remote.Instead = _ => new GitCommandResult(128, string.Empty, said + "\n", false);
        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names the repository 'dailysoftwaresystems/shared' on 'github.com', which git could not read there: it is misspelt, or the credential it is reached with cannot see it ({said}); nothing was cached",
            refused.Message);
        Assert.Empty(warned);
    }

    /// <summary>
    /// Under last-resolved, a tag the remote no longer lists - deleted since, the repository there - is refused, never
    /// served from the cache's last resolution of it.
    /// </summary>
    [Fact]
    public async Task ATagTheForgeSaysIsNotThere_IsRefused_NeverServedFromTheCache()
    {
        using var temp = new TempDirectory();
        var (remote, _, fetcher, warned) = Fixture(temp);
        var lastResolved = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" };
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);
        await remote.BranchAsync("main", commit);
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token);

        await remote.DeleteTagAsync("v1");
        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token));

        Assert.Equal(
            $"'{Source}' names the ref 'v1', which is no tag of 'dailysoftwaresystems/shared'; workflowSources.pin is tag-or-commit, which takes a tag or a full commit SHA - a branch moves, and what a run ran must be what it said",
            refused.Message);
        Assert.Empty(warned);
    }

    /// <summary>
    /// A cache file changed on disk does not read as the workflow it cached - the yml or any other file of the entry:
    /// every blob is hashed again as the entry is read, the change warned of, and the directory fetched again whole.
    /// </summary>
    [Theory]
    [InlineData(Yml)]
    [InlineData(PromptPath)]
    public async Task AChangedCacheFileIsRefused_AndTheDirectoryIsFetchedAgain(string changed)
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, warned) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt));
        await remote.TagAsync("v1", commit);

        var first = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var blob = cache.BlobFile(first.Entry.Files.Single(file => file.Path == changed).Blob);
        await File.WriteAllTextAsync(blob, "changed on disk\n", Token);
        remote.Clear();

        var again = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal((commit, Definition), (again.Commit, again.Definition));
        Assert.Equal(["ls-remote", "fetch refs/tags/v1", "fetch by id", "fetch by id"], remote.Asked);
        Assert.Equal(Prompt, cache.ReadText(Assert.Single(cache.List().Entries).Entry, PromptPath));
        Assert.StartsWith($"{WorkflowRemoteFetcher.Said}: '{Source}': the workflow cache's entry for the commit {commit} is not whole (the workflow cache's '{blob}', the file '{changed}' of the entry", Assert.Single(warned), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run's own recorded commit is used again however the ref has moved: the tag names another commit now, and the
    /// resolution stands at the one the run began with, asking the remote nothing.
    /// </summary>
    [Fact]
    public async Task ARecordedCommitIsUsedAgain_HoweverTheRefHasMoved()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.BranchAsync("main", commit);
        await remote.TagAsync("v1", commit);

        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        // The tag has moved since the run began: it names another commit now.
        await remote.TagAsync("v1", await remote.CommitAsync(("security-review.yml", "name: security-review\nsteps: []\n")));
        remote.Clear();

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: new WorkflowRecordedUse(commit), Token);

        Assert.Equal(commit, resolved.Commit);
        Assert.Empty(remote.Runs);

        // The cache pruned before the resume: the recorded commit's own tree is fetched, proved the repository's own as
        // any commit named by its SHA is, and the refs are never listed.
        System.IO.Directory.Delete(cache.Directory, recursive: true);

        var refetched = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: new WorkflowRecordedUse(commit), Token);

        Assert.Equal((commit, Definition), (refetched.Commit, refetched.Definition));
        Assert.Equal(["fetch every ref", "fetch by id", "fetch by id"], remote.Asked);
    }

    /// <summary>
    /// A commit a run recorded is read from the run's own copy first, whoever runs the command - a person's, with the
    /// user's cache pruned and the remote not there, and an agent's, which reads nothing beyond the copy and the cache -
    /// every file hashed again, nothing asked of the remote and nothing written; a copy that does not hold it whole is
    /// passed over for the cache and the remote, as at the run's start.
    /// </summary>
    [Fact]
    public async Task ARecordedCommit_IsReadFromTheRunsOwnCopyFirst_ByAPersonsCommandAndAnAgentsAlike()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (remote, cache, fetcher, warned) = Fixture(temp);
        static string? Agent(string name) => name == AgentChildEnvironment.AgentMarker ? "1" : null;
        var commit = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt));
        await remote.TagAsync("v1", commit);
        var began = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var run = new DssHarness.Core.Orchestration.OrchestratorLayout("r1", temp.Combine(".orchestrators", "r1"));
        var kept = WorkflowCache.OfRun(harness.FileSystem, run);
        kept.Keep([began]);
        var keptAsItWas = Snapshot(run.RemoteWorkflowsDirectory);

        // The user's cache pruned whole since the run began, and the network gone.
        System.IO.Directory.Delete(cache.Directory, recursive: true);
        remote.Offline = true;
        remote.Clear();

        var person = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: new WorkflowRecordedUse(commit, kept), Token);
        var agent = await fetcher.ResolveAsync(Parsed(), Allowed(), Agent, recorded: new WorkflowRecordedUse(commit, kept), Token);

        Assert.Equal((commit, Definition, Prompt), (person.Commit, person.Definition, person.Text(PromptPath)));
        Assert.Equal((commit, Definition, Prompt), (agent.Commit, agent.Definition, agent.Text(PromptPath)));
        Assert.Empty(remote.Runs);
        Assert.False(System.IO.Directory.Exists(cache.Directory));
        Assert.Equal(keptAsItWas, Snapshot(run.RemoteWorkflowsDirectory));
        Assert.Empty(warned);

        // A blob of the copy gone: the copy does not hold it whole, and the cache - pruned - and then the remote are asked.
        File.Delete(kept.BlobFile(WorkflowCache.BlobSha(Prompt)));

        var unreached = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: new WorkflowRecordedUse(commit, kept), Token));

        Assert.Equal(HarnessExit.CommandFailed, unreached.ExitCode);
        Assert.Equal(["fetch every ref"], remote.Asked);
    }

    /// <summary>
    /// A host beyond github.com - a GitHub Enterprise Server's, or a <c>*.ghe.com</c> one - is given the credential its
    /// configuration names, and nothing else: every helper git's configuration names is cleared for the one that hands
    /// the credential in git's environment, the token on no command line and in no file of the cache's.
    /// </summary>
    /// <param name="host">The host.</param>
    [Theory]
    [InlineData("ghe.acme.internal")]
    [InlineData("acme.ghe.com")]
    public async Task AHostBeyondGitHubCom_IsGivenItsOneCredentialInGitsEnvironment_AndNoneOfTheMachines(string host)
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var hosted = new Repo(host, "acme", "shared");
        var commit = await remote.CommitAsync(hosted, [Tracked.Text("security-review.yml", Definition)]);
        await remote.TagAsync("v1", commit, hosted);

        var resolved = await fetcher.ResolveAsync(
            WorkflowRemote.Source($"{host}/acme/shared/workflows/security-review/security-review.yml@v1")!,
            Hosted(host),
            name => name == "GHE_TOKEN" ? "the-token" : null,
            recorded: null,
            Token);

        Assert.Equal(commit, resolved.Commit);
        Assert.Equal(["ls-remote", "fetch refs/tags/v1", "fetch by id", "fetch by id"], remote.Asked);
        Assert.All(remote.Runs, run =>
        {
            Assert.Equal(["-c", "credential.helper=", "-c", $"credential.helper={GitCredentialValues.Helper}"], run.Arguments.Take(4).ToArray());
            Assert.Equal([(GitCredentialValues.PasswordVariable, "the-token"), (GitCredentialValues.UsernameVariable, "x-access-token")], Handed(run.Values));
            Assert.DoesNotContain(run.Arguments, argument => argument.Contains("the-token", StringComparison.Ordinal));
        });

        foreach (var file in System.IO.Directory.EnumerateFiles(cache.Directory, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("the-token", await File.ReadAllTextAsync(file, Token), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A github.com use is reached as git reaches it, with the helpers git's configuration names - the machine's own
    /// login - and is handed no credential and no helper of the tool's.
    /// </summary>
    [Fact]
    public async Task AGitHubComCall_IsReachedWithGitsOwnHelpers_AndHandedNoCredential()
    {
        using var temp = new TempDirectory();
        var (remote, _, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);

        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.NotEmpty(remote.Runs);
        Assert.All(remote.Runs, run =>
        {
            Assert.Null(run.Values);
            Assert.DoesNotContain(run.Arguments, argument => argument.StartsWith("credential.helper", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// A host beyond github.com whose credential the configuration does not name, or whose variable this process does
    /// not hold, is refused before anything is asked of it: it is never reached with git's own helpers or a machine token.
    /// </summary>
    [Fact]
    public async Task AHostWhoseCredentialIsNotThere_IsRefused_AndNothingIsAsked()
    {
        using var temp = new TempDirectory();
        var (remote, _, fetcher, _) = Fixture(temp);

        var unset = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(WorkflowRemote.Source(GhesSource)!, Hosted("ghe.acme.internal"), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, unset.ExitCode);
        Assert.Equal(
            $"'{GhesSource}' names the host 'ghe.acme.internal', whose credential is the environment variable 'GHE_TOKEN', which is not set in this process; nothing was asked of the host, and no other token is given to it",
            unset.Message);

        var unnamed = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(
            WorkflowRemote.Source(GhesSource)!,
            new WorkflowSourcesConfig
            {
                Allow = ["ghe.acme.internal/acme/*"],
                Hosts = new Dictionary<string, WorkflowHostConfig>(StringComparer.OrdinalIgnoreCase) { ["ghe.acme.internal"] = new() },
            },
            name => name == "GH_ENTERPRISE_TOKEN" ? "the-machines-own" : null,
            recorded: null,
            Token));

        Assert.Equal(
            $"'{GhesSource}' names the host 'ghe.acme.internal', for which workflowSources.hosts names no credential: a host beyond github.com is reached with the one credential its entry names, and never with the machine's own tokens; nothing was asked of it",
            unnamed.Message);
        Assert.Empty(remote.Runs);
    }

    /// <summary>
    /// Files are kept as the bytes the remote gave: one beginning with a byte order mark hashes, caches and reads again
    /// as it was - read as text the way a repository's own file is, the mark dropped - and one that is not UTF-8 is
    /// cached whole, its bytes read back unchanged, and refused only where it is read as text.
    /// </summary>
    [Fact]
    public async Task AFileWithAByteOrderMarkOrNotUtf8_IsCachedAsItsBytes_AndReadAsTheRepositorysOwnFileReads()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, warned) = Fixture(temp);
        byte[] withMark = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(Definition)];
        byte[] latin1 = [0x63, 0x61, 0x66, 0xE9, 0x0A];
        var commit = await remote.CommitAsync(("security-review.yml", withMark), ("prompts/review.md", latin1));
        await remote.TagAsync("v1", commit);

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal(Definition, resolved.Definition);
        Assert.Equal(withMark, cache.ReadBytes(resolved.Entry, Yml));
        Assert.Equal(latin1, cache.ReadBytes(resolved.Entry, PromptPath));

        var notText = Assert.Throws<HarnessException>(() => cache.ReadText(resolved.Entry, PromptPath));

        Assert.Equal(HarnessExit.ConfigInvalid, notText.ExitCode);
        Assert.Equal($"'{PromptPath}' of the workflow 'github.com/dailysoftwaresystems/shared' at '{commit}' is not UTF-8 text, and a workflow reads its files as text", notText.Message);

        // Read again from the cache, nothing is fetched and nothing reads as changed on disk.
        remote.Clear();
        var again = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal(Definition, again.Definition);
        Assert.Equal(["ls-remote"], remote.Asked);
        Assert.Empty(warned);
    }

    /// <summary>
    /// A command an agent runs - its environment marked - reads the workflow cache alone: a tag is resolved by the
    /// cache's newest resolution of it, nothing is asked of the remote and nothing of the cache is written, not even the
    /// use's date; one the cache does not hold is refused saying the mark made the command an agent's and where a person
    /// fills the cache instead.
    /// </summary>
    [Fact]
    public async Task AnAgentsCommand_ReadsTheCacheAlone_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var (remote, cache, fetcher, warned) = Fixture(temp, clock);
        static string? Agent(string name) => name == AgentChildEnvironment.AgentMarker ? "1" : null;

        var unfetched = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), Agent, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, unfetched.ExitCode);
        Assert.Equal(
            $"'{Source}' is not in the workflow cache as this command needs it (it holds no resolution of 'v1'), and this command reads the cache alone, taken for an agent's: it carries DSSHARNESS_UNDER_AGENT, the mark every agent's child is given, whatever the mark holds; {FilledFromAShell}",
            unfetched.Message);
        Assert.Empty(remote.Runs);
        Assert.False(System.IO.Directory.Exists(cache.Directory));

        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);
        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var written = Snapshot(cache.Directory);
        remote.Clear();
        clock.Advance(TimeSpan.FromDays(3));

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), Agent, recorded: null, Token);

        Assert.Equal((commit, Definition), (resolved.Commit, resolved.Definition));
        Assert.Empty(remote.Runs);
        Assert.Equal(written, Snapshot(cache.Directory));
        Assert.Equal(
            [$"{WorkflowRemoteFetcher.Said}: '{Source}' was resolved from the workflow cache alone, at the commit {commit} the cache's newest resolution of 'v1' names, from 2026-09-30, for this command is taken for an agent's, which never reaches the forge: it carries DSSHARNESS_UNDER_AGENT, the mark every agent's child is given, whatever the mark holds"],
            warned);

        // A commit the cache does not hold, named by its SHA: refused, and still nothing asked.
        var sha = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed() with { Ref = Unheld }, Allowed(), Agent, recorded: null, Token));

        Assert.Equal(
            $"'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@{Unheld}' is not in the workflow cache as this command needs it (it holds no entry for the commit '{Unheld}'), and this command reads the cache alone, taken for an agent's: it carries DSSHARNESS_UNDER_AGENT, the mark every agent's child is given, whatever the mark holds; {FilledFromAShell}",
            sha.Message);
        Assert.Empty(remote.Runs);
    }

    /// <summary>
    /// The resolver places a remote workflow as a local one: its steps the use step's own, each marked with the
    /// workflow its files are read under - the reserved <c>remote/</c> place, its commit in it - its fingerprint the
    /// commit it resolved to.
    /// </summary>
    [Fact]
    public async Task TheResolverPlacesARemoteWorkflow_AsALocalOne_MarkedWithItsOwnPlace()
    {
        var parsed = Parsed();
        var caller = WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            """
            name: flow
            steps:
              - name: review
                use: dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1
                with:
                  paths: 'run.changed'
            """);

        var (definition, problems, remotes) = await WorkflowResolver.ResolveAsync(
            caller,
            "flow",
            new WorkflowResolution(
                _ => null,
                Fetch: _ => Task.FromResult(Resolved(parsed, Definition))));

        Assert.Empty(problems);
        Assert.Equal((parsed, Pinned), (Assert.Single(remotes).Source, Assert.Single(remotes).Commit));

        var step = definition.Steps.Single();

        Assert.Equal(WorkflowRemote.Name(parsed, Pinned), step.Use!.Name);
        Assert.Equal(Pinned, step.Use.Fingerprint);
        Assert.Equal(WorkflowRemote.Name(parsed, Pinned), step.Steps.Single().WorkflowOf);
    }

    /// <summary>
    /// A workflow that uses itself through a remote source is refused with the whole chain, and a local use that
    /// resolves under <c>remote/</c> is refused: that place is the cache's own.
    /// </summary>
    [Fact]
    public async Task AWorkflowThatUsesItselfRemotely_IsRefusedWithTheChain_AndTheRemotePlaceIsReserved()
    {
        var parsed = Parsed();
        var definition = WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            """
            name: flow
            steps:
              - name: review
                use: dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1
                with:
                  paths: 'run.changed'
            """);

        // The workflow it fetches uses itself.
        var self = """
            name: security-review
            steps:
              - name: again
                use: dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1
            """;

        var (_, problems, _) = await WorkflowResolver.ResolveAsync(
            definition,
            "flow",
            new WorkflowResolution(
                _ => null,
                Fetch: _ => Task.FromResult(Resolved(parsed, self))));

        Assert.Contains(problems, problem => problem.Contains("which is already in use around it", StringComparison.Ordinal));

        var local = WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            """
            name: flow
            steps:
              - name: review
                use: ../remote/shared/security-review.yml
            """);

        var (_, localProblems, _) = await WorkflowResolver.ResolveAsync(local, "flow", new WorkflowResolution(_ => "name: x\nsteps: []\n"));

        Assert.Equal(
            "step 'review' uses '../remote/shared/security-review.yml', which resolves to 'remote/shared/security-review.yml' under the workflows directory: 'remote/' is the workflow cache's own reserved place, where a remote workflow's files are read, and no local use resolves there",
            Assert.Single(localProblems));
    }

    /// <summary>
    /// A relative use inside a remote workflow resolves inside the same cache entry: the workflow it names is read from
    /// the entry the remote one was fetched into - never from the repository, though a reader of the repository's files
    /// would answer for the same path - named under the remote place by its own directory, its fingerprint the commit,
    /// and its files read from that entry at run time; a changed blob of it is refused as it is read, and so is a name
    /// the cache holds no entry for.
    /// </summary>
    [Fact]
    public async Task AUseInsideARemoteWorkflow_ResolvesInsideItsCacheEntry_AndIsReadThereAsItRuns()
    {
        using var temp = new TempDirectory();
        const string Outer = "name: security-review\nsteps:\n  - name: review\n    use: ./inner/inner.yml\n";
        const string Inner = "name: inner\nsteps:\n  - name: check\n    type: reason\n    agent: reviewer\n    prompt: prompts/check.md\n    outputs: schemas/check.json\n";
        const string Check = "Check the paths.\n";
        var (remote, cache, fetcher, _) = Fixture(temp);
        var root = temp.Combine("workflow-cache");
        var commit = await remote.CommitAsync(("security-review.yml", Outer), ("inner/inner.yml", Inner), ("inner/prompts/check.md", Check));
        await remote.TagAsync("v1", commit);
        var resolution = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var local = new List<string>();

        var (definition, problems, _) = await WorkflowResolver.ResolveAsync(
            WorkflowFileParser.Parse("workflows/flow/flow.yml", $"name: flow\nsteps:\n  - name: scan\n    use: {Source}\n"),
            "flow",
            new WorkflowResolution(
                path =>
                {
                    local.Add(path);
                    return Inner;
                },
                Fetch: _ => Task.FromResult(resolution),
                ReadCached: (owner, path) => cache.FindNamed(owner) is { } entry ? cache.ReadText(entry, path) : null));

        var name = $"remote/github.com/dailysoftwaresystems/shared/{commit}/workflows/security-review/inner";
        var nested = definition.Steps.Single().Steps.Single();

        Assert.Empty(problems);
        Assert.Empty(local);
        Assert.Equal(("review", name, commit), (nested.Name, nested.Use!.Name, nested.Use.Fingerprint));
        Assert.Equal(name, nested.Steps.Single().WorkflowOf);

        // At run time the nested workflow's files are read from the entry that holds it, by its own directory.
        var named = new WorkflowNamedFiles(Physical(), StringComparison.Ordinal, cache);
        var layout = new HarnessLayout(temp.Path, temp.Path);

        Assert.Equal(Check, named.ReadText(layout, name, "prompts/check.md", "step 'scan.review.check''s prompt"));

        // A blob changed on disk is refused as it is read, never run.
        await File.WriteAllTextAsync(Path.Combine(root, "blobs", WorkflowCache.BlobSha(Check)[..2], WorkflowCache.BlobSha(Check)[2..]), "Something else.\n", Token);

        Assert.Equal(HarnessExit.ConfigInvalid, Assert.Throws<HarnessException>(() => named.ReadText(layout, name, "prompts/check.md", "step 'scan.review.check''s prompt")).ExitCode);

        // A name the cache holds no entry for is said so.
        var gone = $"remote/github.com/dailysoftwaresystems/shared/{Unheld}/workflows/security-review/inner";

        Assert.Equal(
            $"'prompts/check.md', step 'scan.review.check''s prompt, is of the workflow '{gone}', which the remote workflows at '{root}' hold no entry for; a run keeps every remote workflow it began with in its own, and the check a resume goes through keeps it again",
            Assert.Throws<HarnessException>(() => named.ReadText(layout, gone, "prompts/check.md", "step 'scan.review.check''s prompt")).Message);
    }

    /// <summary>
    /// A relative use inside a remote workflow that leaves its cache entry is refused, as a local one that leaves the
    /// workflows directory is; where the resolution reads no cache entry, the use is refused saying so, and where the
    /// entry holds no such workflow, it is said as the commit's - none is ever read from the repository.
    /// </summary>
    [Fact]
    public async Task AUseInsideARemoteWorkflow_ThatLeavesItsEntry_OrWhereNoEntryIsRead_IsRefused()
    {
        var parsed = Parsed();
        var caller = WorkflowFileParser.Parse("workflows/flow/flow.yml", $"name: flow\nsteps:\n  - name: scan\n    use: {Source}\n");

        async Task<IReadOnlyList<string>> ProblemsOf(string outer, Func<string, string, string?>? cached)
        {
            var (_, problems, _) = await WorkflowResolver.ResolveAsync(
                caller,
                "flow",
                new WorkflowResolution(
                    path => throw new InvalidOperationException($"'{path}' was read from the repository."),
                    Fetch: _ => Task.FromResult(Resolved(parsed, outer)),
                    ReadCached: cached));

            return problems;
        }

        Assert.Equal(
            ["step 'scan.review' uses '../other/other.yml', which leaves 'workflows/security-review', the directory the remote workflow of 'github.com/dailysoftwaresystems/shared' was fetched as: a use inside a remote workflow resolves inside the same cache entry, as a use inside a local one resolves inside the repository's workflows directory"],
            await ProblemsOf("name: security-review\nsteps:\n  - name: review\n    use: ../other/other.yml\n", (_, _) => null));
        Assert.Equal(
            [$"step 'scan.review' uses './inner/inner.yml', which could not be read: it is a workflow inside the remote workflow 'remote/github.com/dailysoftwaresystems/shared/{Pinned}/workflows/security-review', and no resolution of this kind reads the cache entry that holds it"],
            await ProblemsOf("name: security-review\nsteps:\n  - name: review\n    use: ./inner/inner.yml\n", cached: null));

        // One the entry does not hold is said as the commit's, never looked for in the repository.
        Assert.Equal(
            ["step 'scan.review' uses './inner/inner.yml', and the commit 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' of 'github.com/dailysoftwaresystems/shared' holds no workflow file at 'workflows/security-review/inner/inner.yml'"],
            await ProblemsOf("name: security-review\nsteps:\n  - name: review\n    use: ./inner/inner.yml\n", (_, _) => null));
    }

    /// <summary>
    /// A fetch refused for what the source is - not allowed, a ref the pin refuses - is the definition's problem, said
    /// with the use; a fetch that could not run - git missing, the network not there, a call that failed - is no problem
    /// of the definition's, and the resolution goes on with its own code.
    /// </summary>
    [Fact]
    public async Task AFetchThatCouldNotRun_KeepsItsOwnCode_WhereARefusalOfTheSourceIsAProblem()
    {
        var caller = WorkflowFileParser.Parse("workflows/flow/flow.yml", $"name: flow\nsteps:\n  - name: scan\n    use: {Source}\n");

        Task<(WorkflowDefinition, IReadOnlyList<string>, IReadOnlyList<WorkflowRemoteResolution>)> Resolving(int exitCode)
            => WorkflowResolver.ResolveAsync(
                caller,
                "flow",
                new WorkflowResolution(_ => null, Fetch: _ => throw new HarnessException(exitCode, "the fetch said no")));

        var (_, refused, _) = await Resolving(HarnessExit.Refused);

        Assert.Equal([$"step 'scan' uses '{Source}', which could not be resolved: the fetch said no"], refused);

        foreach (var code in new[] { HarnessExit.ToolMissing, HarnessExit.CommandFailed, HarnessExit.Incomplete })
        {
            Assert.Equal(code, (await Assert.ThrowsAsync<HarnessException>(() => Resolving(code))).ExitCode);
        }
    }

    /// <summary>
    /// The pruning removes an entry no run used for its days, the resolution that named it, and the blobs no entry
    /// left holds - and keeps every blob an entry it keeps holds, one the two shared among them: the kept entry reads
    /// whole after it, and the counts are exactly what went.
    /// </summary>
    [Fact]
    public async Task ThePruning_KeepsEveryBlobAnEntryItKeepsHolds_AndRemovesTheRest()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var (remote, cache, fetcher, _) = Fixture(temp, clock);
        var moved = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/old.md", "an old prompt\n"));
        var commit = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt));

        // The stale entry, at the moved commit, used forty days ago; the kept one used yesterday. The yml is one blob
        // both hold.
        await remote.TagAsync("v0", moved);
        _ = await fetcher.ResolveAsync(Parsed() with { Ref = "v0" }, Allowed(), NoVariables, recorded: null, Token);
        clock.Advance(TimeSpan.FromDays(39));
        await remote.TagAsync("v1", commit);
        var kept = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        clock.Advance(TimeSpan.FromDays(1));

        var pruning = cache.Prune(30, clock.GetUtcNow());

        Assert.Equal((1, 1, true), (pruning.Entries, pruning.Blobs, pruning.Whole));
        Assert.Equal([commit], cache.List().Entries.Select(listed => listed.Entry.Commit));
        Assert.Equal(Definition, cache.ReadText(kept.Entry, Yml));
        Assert.Equal(Prompt, cache.ReadText(kept.Entry, PromptPath));
        Assert.Null(cache.LastResolved("github.com", "dailysoftwaresystems", "shared", Directory, "v0"));
        Assert.NotNull(cache.LastResolved("github.com", "dailysoftwaresystems", "shared", Directory, "v1"));
        Assert.Equal(
            new[] { WorkflowCache.BlobSha(Definition), WorkflowCache.BlobSha(Prompt) }.Order(StringComparer.Ordinal),
            System.IO.Directory.EnumerateFiles(Path.Combine(cache.Directory, "blobs"), "*", SearchOption.AllDirectories).Select(file => WorkflowCache.BlobOf(file)!).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A pruning that could not remove a file says which and why, and is not whole - never "pruned" alone - and one
    /// beside an entry it could not read removes no blob at all, for which blobs that entry holds cannot be told.
    /// </summary>
    [Fact]
    public async Task APruningThatCouldNotRemoveOrReadAFile_SaysSo_AndIsNotWhole()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var root = temp.Combine("workflow-cache");
        var fileSystem = new UndeletableBlobs(Physical());
        var cache = new WorkflowCache(fileSystem, root);
        var remote = new Remote(temp.Combine("remotes"));
        var fetcher = new WorkflowRemoteFetcher(remote.Git, Physical(), cache, Person(), clock: clock, address: remote.AddressOf);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);
        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        clock.Advance(TimeSpan.FromDays(31));

        var stuck = cache.Prune(30, clock.GetUtcNow());

        Assert.Equal((1, 0, false), (stuck.Entries, stuck.Blobs, stuck.Whole));
        Assert.Equal([cache.BlobFile(WorkflowCache.BlobSha(Definition))], stuck.NotRemoved.Select(file => file.File));
        Assert.Equal(["this file system keeps its blobs"], stuck.NotRemoved.Select(file => file.Why));

        var outcome = WorkflowCacheReports.Render(new WorkflowCacheReport(cache.Directory, [], stuck.Unread, stuck), json: false);

        Assert.Equal(HarnessExit.Incomplete, outcome.ExitCode);
        Assert.Equal($"pruned 1 entry(ies) and 0 blob(s) from the workflow cache '{root}', and did not finish: 1 file(s) could not be removed; run it again once they can be", outcome.Message);
        Assert.Equal([$"could not be removed: '{cache.BlobFile(WorkflowCache.BlobSha(Definition))}': this file system keeps its blobs"], outcome.Details);

        // An entry that does not read: the stale blob stays, and the pruning says why.
        var unreadable = Path.Combine(root, "entries", "0000000000000000000000000000000000000000000000000000000000000000.json");
        await File.WriteAllTextAsync(unreadable, "{ not an entry", Token);

        var blind = new WorkflowCache(Physical(), root).Prune(0, clock.GetUtcNow() + TimeSpan.FromDays(1));

        Assert.Equal((0, 0, false), (blind.Entries, blind.Blobs, blind.Whole));
        Assert.Equal([unreadable], blind.Unread.Select(file => file.File));
        Assert.True(File.Exists(cache.BlobFile(WorkflowCache.BlobSha(Definition))));

        var said = WorkflowCacheReports.Render(new WorkflowCacheReport(root, [], blind.Unread, blind), json: false);

        Assert.Equal($"pruned 0 entry(ies) and 0 blob(s) from the workflow cache '{root}', and did not finish: 1 file(s) of its own could not be read, so no blob was removed; run it again once they can be", said.Message);
    }

    /// <summary>
    /// An entry that is not the one its key files it under - its commit or a blob changed by hand, a blob named by what
    /// is no hash - is refused as it is read, never taken for one that is not there; a resolution warns of it and fetches
    /// the directory again over it.
    /// </summary>
    [Fact]
    public async Task AnEntryNotTheOneOfItsKey_IsRefusedAsItIsRead_AndFetchedAgain()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, warned) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);
        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        var file = System.IO.Directory.EnumerateFiles(Path.Combine(cache.Directory, "entries")).Single();
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, Token)).Replace(WorkflowCache.BlobSha(Definition), "../../escape", StringComparison.Ordinal), Token);

        var refused = Assert.Throws<HarnessException>(() => cache.Find("github.com", "dailysoftwaresystems", "shared", commit, Directory));

        // A record of the cache's own that does not hold is the cache's failure, never a problem of a definition's.
        Assert.Equal(WorkflowCache.FailedExit, refused.ExitCode);
        Assert.Equal($"the workflow cache's entry '{file}' names the blob '../../escape', which is no 40 hex characters; it was not read, and a resolution of it fetches it again", refused.Message);
        Assert.Equal([file], cache.List().Unread.Select(unread => unread.File));

        var again = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal(Definition, again.Definition);
        Assert.Equal($"{WorkflowRemoteFetcher.Said}: '{Source}': {refused.Message}", Assert.Single(warned));
        Assert.Empty(cache.List().Unread);
    }

    /// <summary>
    /// The cache's directory is named the first time the cache is used, never as it is made: a process given no home of
    /// its own makes the cache - and so checks and runs every workflow that names nothing remote - and is told why the
    /// moment a remote one needs it.
    /// </summary>
    [Fact]
    public void TheCacheIsNamedAtItsFirstUse_AndAProcessWithNoHomeIsToldWhy()
    {
        var asked = 0;
        var cache = new WorkflowCache(Physical(), () =>
        {
            asked++;
            throw new DirectoryNotFoundException("no directory of this user's own application data could be named: the process was given no home, and the system has none for its account");
        });

        Assert.Equal(0, asked);

        var refused = Assert.Throws<HarnessException>(() => cache.List());

        // A cache with nowhere to be kept is the cache's own failure, which a resolution never takes for the source's.
        Assert.Equal(WorkflowCache.FailedExit, refused.ExitCode);
        Assert.Equal(
            "the workflow cache has nowhere to be kept: no directory of this user's own application data could be named: the process was given no home, and the system has none for its account; a remote workflow is neither fetched nor read, and a workflow that names none runs as it would",
            refused.Message);
    }

    /// <summary>
    /// A run reads a remote workflow's files from its cache entry by the workflow's name, hashed again as they are read:
    /// a prompt reads as the remote gave it, and a blob changed on disk since is refused at the read, never run.
    /// </summary>
    [Fact]
    public async Task ARunReadsARemoteWorkflowsFilesFromTheCache_AndRefusesABlobChangedOnDisk()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt));
        await remote.TagAsync("v1", commit);
        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var name = WorkflowRemote.Name(resolved.Source, resolved.Commit);
        var files = new WorkflowNamedFiles(harness.FileSystem, StringComparison.Ordinal, cache);
        var layout = new HarnessLayout(temp.Path, temp.Path);

        Assert.Equal(Prompt, files.ReadText(layout, name, "prompts/review.md", "a prompt"));

        await File.WriteAllTextAsync(cache.BlobFile(WorkflowCache.BlobSha(Prompt)), "Review nothing.\n", Token);

        var refused = Assert.Throws<HarnessException>(() => files.ReadText(layout, name, "prompts/review.md", "a prompt"));

        Assert.Equal(HarnessExit.ConfigInvalid, refused.ExitCode);
        Assert.StartsWith($"the workflow cache's '{cache.BlobFile(WorkflowCache.BlobSha(Prompt))}', the file '{PromptPath}' of the entry for 'github.com/dailysoftwaresystems/shared' at '{commit}', does not hash to", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// export-workflow --from vendors the remote workflow at its resolved commit: every file of its directory, byte for
    /// byte - a byte order mark kept, and a file that is no text at all as it is - and a note saying where it came from,
    /// so a team can review what is written and use it as ./.
    /// </summary>
    [Fact]
    public async Task ExportVendorsTheRemoteWorkflow_WithANoteOfWhereItCameFrom()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        byte[] marked = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Prompt)];
        byte[] binary = [0x89, 0x50, 0x4E, 0x47, 0xFF, 0x00, 0xC3];
        var commit = await remote.CommitAsync(("security-review.yml", Encoding.UTF8.GetBytes(Definition)), ("prompts/review.md", marked), ("assets/mark.png", binary));
        await remote.TagAsync("v1", commit);

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { WorkflowSources = Allowed() });

        var export = await new WorkflowExportService(
            Substitute.For<IWorkflowValidationService>(),
            harness.FileSystem,
            new FixedProcessContext { Fixed = temp.Path },
            harness.ContextLoader,
            fetcher,
            cache).ExportAsync(
            temp.Path,
            new WorkflowExportRequest(Workflow: null, temp.Combine("vendor"), Shipped: false, From: Source),
            Token);

        var destination = temp.Combine("vendor", "security-review");

        Assert.Equal(destination, export.Destination);

        // The files as the commit's tree holds them, the note of where they came from last.
        Assert.Equal(
            ["assets/mark.png", "prompts/review.md", "security-review.yml", WorkflowExportService.VendoredNote],
            export.Files);
        Assert.Equal(Definition, await File.ReadAllTextAsync(Path.Combine(destination, "security-review.yml"), Token));
        Assert.Equal(marked, await File.ReadAllBytesAsync(Path.Combine(destination, "prompts", "review.md"), Token));
        Assert.Equal(binary, await File.ReadAllBytesAsync(Path.Combine(destination, "assets", "mark.png"), Token));
        Assert.Contains(
            $"Vendored from github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1, which resolved to the commit {commit}",
            await File.ReadAllTextAsync(Path.Combine(destination, WorkflowExportService.VendoredNote), Token),
            StringComparison.Ordinal);
        Assert.Equal(
            $"github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1 at commit {commit}",
            export.Vendored);
    }

    /// <summary>
    /// export-workflow --from says how the commit came about where it is worth a word - a branch, which moves - in what
    /// it prints and in the note; and a remote directory holding a file of the note's own name is refused, nothing
    /// written, for the note is never written over a workflow's file.
    /// </summary>
    [Fact]
    public async Task ExportSaysABranchMoves_AndRefusesADirectoryHoldingTheNotesName()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var anyRef = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "any" };

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { WorkflowSources = anyRef });

        WorkflowExportService Export()
            => new(Substitute.For<IWorkflowValidationService>(), harness.FileSystem, new FixedProcessContext { Fixed = temp.Path }, harness.ContextLoader, fetcher, cache);

        var commit = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt));
        await remote.BranchAsync("v1", commit);

        var branch = await Export()
            .ExportAsync(temp.Path, new WorkflowExportRequest(Workflow: null, temp.Combine("vendor"), Shipped: false, From: Source), Token);

        Assert.Equal(
            $"github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1 at commit {commit}, where the branch 'v1' stood then - a branch moves",
            branch.Vendored);
        Assert.StartsWith(
            $"Vendored from github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1, which resolved to the commit {commit}, where the branch 'v1' stood then - a branch moves, and was fetched on ",
            await File.ReadAllTextAsync(temp.Combine("vendor", "security-review", WorkflowExportService.VendoredNote), Token),
            StringComparison.Ordinal);

        const string Note = "A note the workflow keeps of its own.\n";
        await remote.TagAsync("v2", await remote.CommitAsync(("security-review.yml", Definition), ("source.md", Note)));

        var colliding = await Assert.ThrowsAsync<HarnessException>(() => Export()
            .ExportAsync(temp.Path, new WorkflowExportRequest(Workflow: null, temp.Combine("elsewhere"), Shipped: false, From: "dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v2"), Token));

        Assert.Equal(HarnessExit.Refused, colliding.ExitCode);
        Assert.Equal(
            $"'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v2' holds a 'source.md' of its own in its directory, the name the note of where a vendored workflow came from is written as; export-workflow writes that note beside a workflow's files and never over one of them. Nothing of the workflow was written, and what was fetched is kept in the workflow cache '{cache.Directory}'.",
            colliding.Message);
        Assert.False(System.IO.Directory.Exists(temp.Combine("elsewhere")));
    }

    /// <summary>
    /// export-workflow --from looks at its folder before it fetches anything: a folder that is there already is refused
    /// with nothing written anywhere - nothing asked of the remote, the workflow cache never made; and a blank --from is a
    /// usage error naming the form a remote workflow takes, never an exception of the runtime's.
    /// </summary>
    [Fact]
    public async Task ExportFrom_LooksAtItsFolderBeforeItFetches_AndABlankSourceIsAUsageError()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { WorkflowSources = Allowed() });
        System.IO.Directory.CreateDirectory(temp.Combine("vendor", "security-review"));

        WorkflowExportService Export()
            => new(Substitute.For<IWorkflowValidationService>(), harness.FileSystem, new FixedProcessContext { Fixed = temp.Path }, harness.ContextLoader, fetcher, cache);

        var taken = await Assert.ThrowsAsync<HarnessException>(() => Export()
            .ExportAsync(temp.Path, new WorkflowExportRequest(Workflow: null, temp.Combine("vendor"), Shipped: false, From: Source), Token));

        Assert.Equal(
            (HarnessExit.Refused, $"'{temp.Combine("vendor", "security-review")}' is there already; export-workflow writes a workflow into a folder of its own that it makes, and never into or over one that is there. Nothing was written."),
            (taken.ExitCode, taken.Message));
        Assert.Empty(remote.Runs);
        Assert.False(System.IO.Directory.Exists(cache.Directory));

        foreach (var blank in new[] { string.Empty, "  " })
        {
            var usage = await Assert.ThrowsAsync<HarnessException>(() => Export()
                .ExportAsync(temp.Path, new WorkflowExportRequest(Workflow: null, temp.Combine("elsewhere"), Shipped: false, From: blank), Token));

            Assert.Equal(
                (HarnessExit.UsageError, "--from names nothing: it names a remote workflow as a use does, owner/repo/path/workflow.yml@ref - an owner, a repository, a workflow file's path in it, and a ref after @. Nothing was written."),
                (usage.ExitCode, usage.Message));
        }

        Assert.Empty(remote.Runs);
        Assert.False(System.IO.Directory.Exists(temp.Combine("elsewhere")));
    }

    /// <summary>
    /// export-workflow --from names the workflow cache it fetched into, in the line it ends on and in its document.
    /// </summary>
    [Fact]
    public async Task ExportFrom_NamesTheWorkflowCacheItFetchedInto_InTextAndInJson()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { WorkflowSources = Allowed() });

        var export = await new WorkflowExportService(Substitute.For<IWorkflowValidationService>(), harness.FileSystem, new FixedProcessContext { Fixed = temp.Path }, harness.ContextLoader, fetcher, cache)
            .ExportAsync(temp.Path, new WorkflowExportRequest(Workflow: null, temp.Combine("vendor"), Shipped: false, From: Source), Token);

        Assert.Equal(cache.Directory, export.Cache);
        Assert.Equal(
            $"'security-review', vendored from github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1 at commit {commit}, exported to '{temp.Combine("vendor", "security-review")}': 2 file(s); it was fetched into the workflow cache '{cache.Directory}'",
            WorkflowExportReports.Render(export, json: false).Message);
        Assert.Equal(
            cache.Directory,
            System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(WorkflowExportReports.Render(export, json: true).Data))!["cache"]!.GetValue<string>());
    }

    /// <summary>
    /// validate-workflow reads a workflow a remote one uses where its run reads it: each file a step of it names is
    /// looked for in the cache entry under that workflow's own directory, never the directory of the remote workflow
    /// that uses it, and the text of a prompt it names is read from there for its placeholders to be checked.
    /// </summary>
    [Fact]
    public async Task ValidationReadsTheFilesOfAWorkflowARemoteOneUses_UnderItsOwnDirectoryInTheEntry()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        const string Outer = "name: security-review\nsteps:\n  - name: review\n    use: ./inner/inner.yml\n";
        const string Inner = "name: inner\nsteps:\n  - name: check\n    type: reason\n    agent: reviewer\n    prompt: prompts/check.md\n    outputs: schemas/check.json\n";
        const string Agent = "---\nname: reviewer\ndescription: reviews the paths\n---\nYou review.\n";
        const string Check = "Check {nothing.here}.\n";
        const string Schema = """{ "type": "object", "properties": { "done": { "type": "boolean" } }, "required": ["done"], "additionalProperties": false }""";
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(
            ("security-review.yml", Outer),
            ("inner/inner.yml", Inner),
            ("inner/agents/reviewer.md", Agent),
            ("inner/prompts/check.md", Check),
            ("inner/schemas/check.json", Schema));
        await remote.TagAsync("v1", commit);

        // The agent sections as init writes them, so the step's tier resolves and only what the files say is said.
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
        {
            WorkflowSources = Allowed(),
            Workflows = ["flow"],
            Defaults = new HarnessDefaults { AgentTier = agents.AgentTier },
            Providers = [.. agents.Providers],
            TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase),
        });
        System.IO.Directory.CreateDirectory(temp.Combine(".harness-config", "workflows", "flow"));
        TestAdvice.WriteWorkflowFile(temp.Combine(".harness-config", "workflows", "flow", "flow.yml"), $"name: flow\nsteps:\n  - name: scan\n    use: {Source}\n");

        var report = await new WorkflowValidationService(
            harness.ContextLoader,
            harness.FileSystem,
            harness.Platform,
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform),
            DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild,
            fetcher,
            cache).CheckAsync(temp.Path, "flow", Token);

        // Its agent, prompt and schema are found where they sit, and the prompt's text is read to be held to its names.
        Assert.Equal(
            [
                ".harness-config/workflows/flow/flow.yml: step 'scan.review.check' prompt 'prompts/check.md' line 1 reads 'nothing.here', and a workflow used as a step reads only its own steps and its own names: it is of the workflow that called it. Give the fact to the called workflow as one of its inputs",
            ],
            report.Workflows.Single().Lines);
    }

    /// <summary>
    /// validate-workflow names the workflow cache's directory - where the check reads what it resolved, and writes what
    /// it fetches - on the line it ends on wherever it went to resolve a remote use, whether the workflows hold or not, and
    /// in its document on every exit, so the person who ran it knows where that landed; the line of each resolution says
    /// the resolution alone.
    /// </summary>
    [Fact]
    public async Task Validation_NamesTheWorkflowCache_OnTheLineItEndsOn_WhetherTheWorkflowsHoldOrNot()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await HoldsInnerAsync(remote, schema: """{ "type": "object", "properties": { "done": { "type": "boolean" } }, "required": ["done"], "additionalProperties": false }""");
        await remote.TagAsync("v1", commit);

        var report = await ValidatedAsync(temp, cache, fetcher, Source);

        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Workflows.Single().Lines));
        Assert.StartsWith(
            $"'scan' uses {Source} -> github.com/dailysoftwaresystems/shared/workflows/security-review at commit {commit} (",
            Assert.Single(report.Workflows.Single().Lines.Where(line => line.StartsWith("'scan' uses ", StringComparison.Ordinal)).ToList()),
            StringComparison.Ordinal);

        var holds = WorkflowValidationReports.Render(report, json: false);
        var document = System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(WorkflowValidationReports.Render(report, json: true).Data))!;

        Assert.Equal($"1 workflow(s) checked, all valid; remote workflows are read from and fetched into the workflow cache '{cache.Directory}'", holds.Message);
        Assert.Equal(cache.Directory, document["cache"]!.GetValue<string>());

        // A check that fails after it fetched names the cache as much as one that holds.
        await remote.TagAsync("v2", await remote.CommitAsync(("security-review.yml", "name: security-review\nsteps:\n  - name: review\n    use: ./missing/missing.yml\n")));

        var failing = WorkflowValidationReports.Render(await ValidatedAsync(temp, cache, fetcher, Source.Replace("@v1", "@v2", StringComparison.Ordinal)), json: false);

        Assert.Equal(HarnessExit.ConfigInvalid, failing.ExitCode);
        Assert.Equal($"0 workflow(s) valid, 1 with problems; remote workflows are read from and fetched into the workflow cache '{cache.Directory}'. Nothing was run.", failing.Message);
    }

    /// <summary>
    /// A remote workflow's step schemas are held to the subset of JSON Schema the engine reads, by the one rule a local
    /// workflow's are, read from what the resolution fetched: a schema no answer could be held to is said where the
    /// definition is checked, so no run starts over it.
    /// </summary>
    [Fact]
    public async Task Validation_HoldsARemoteWorkflowsSchemas_ToTheSubsetTheEngineReads()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        await remote.TagAsync("v1", await HoldsInnerAsync(remote, schema: "[]"));

        var report = await ValidatedAsync(temp, cache, fetcher, Source);

        Assert.Equal(
            [".harness-config/workflows/flow/flow.yml: 'schemas/check.json', step 'scan.review.check''s outputs, is not a schema the engine reads: $: a schema is a JSON object"],
            report.Workflows.Single().Lines);
    }

    /// <summary>
    /// Every file a check reads of a remote workflow is read from what its resolution fetched and hashed, never from the
    /// cache again: a cache pruned whole right after the resolution changes nothing the check says.
    /// </summary>
    [Fact]
    public async Task Validation_ReadsARemoteWorkflowsFiles_FromWhatItsResolutionRead_NeverFromTheCacheAgain()
    {
        using var temp = new TempDirectory();
        var (remote, cache, _, _) = Fixture(temp);
        var pruning = new PrunedAfterTheFetch(Physical(), temp.Combine("workflow-cache"));
        var pruned = new WorkflowCache(pruning, temp.Combine("workflow-cache"));
        await remote.TagAsync("v1", await HoldsInnerAsync(remote, schema: """{ "type": "object", "properties": { "done": { "type": "boolean" } }, "required": ["done"], "additionalProperties": false }"""));

        var report = await ValidatedAsync(temp, pruned, Over(pruned, remote), Source);

        Assert.True(pruning.Pruned);
        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Workflows.Single().Lines));
        Assert.Empty(cache.List().Entries);
    }

    /// <summary>
    /// A workflow cache that cannot be written is the cache's own failure, never a problem of the definition's: the check
    /// ends with the cache's code, naming the cache, rather than saying the use does not hold.
    /// </summary>
    [Fact]
    public async Task Validation_OverACacheThatCannotBeWritten_EndsWithTheCachesOwnCode_NamingIt()
    {
        using var temp = new TempDirectory();
        var (remote, _, _, _) = Fixture(temp);
        var root = temp.Combine("workflow-cache");
        var cache = new WorkflowCache(new Unwritable(Physical(), root), root);
        await remote.TagAsync("v1", await remote.CommitAsync(("security-review.yml", "name: security-review\nsteps:\n  - name: review\n    type: orient\n")));

        var failed = await Assert.ThrowsAsync<HarnessException>(() => ValidatedAsync(temp, cache, Over(cache, remote), Source));

        Assert.Equal(WorkflowCache.FailedExit, failed.ExitCode);
        Assert.StartsWith("The workflow cache's '", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith($"; remote workflows are read from and fetched into the workflow cache '{root}'", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fetch writes its blobs outside the cache's lock - another process's pruning between the first blob and the entry
    /// is held off by nothing, and takes the blob no entry holds yet - and records its entry under the lock only once every
    /// blob it names is there, the one the pruning took written again: the entry reads whole.
    /// </summary>
    [Fact]
    public async Task AFetchWritesItsBlobsOutsideTheLock_AndRecordsItsEntryOnlyOverEveryBlobItNames()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("workflow-cache");
        var physical = Physical();
        var elsewhere = new WorkflowCache(physical, root);
        WorkflowCachePruning? pruned = null;
        var fileSystem = new Meanwhile(
            physical,
            (done, path) => done == Meanwhile.Written && path.StartsWith(Path.Combine(root, "blobs"), StringComparison.Ordinal),
            () => pruned = Task.Run(() => elsewhere.Prune(0, DateTimeOffset.UtcNow.AddDays(1)), Token).GetAwaiter().GetResult());
        var cache = new WorkflowCache(fileSystem, root);
        var remote = new Remote(temp.Combine("remotes"));
        await remote.TagAsync("v1", await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt)));

        var resolved = await Over(cache, remote).ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.True(fileSystem.Happened);
        Assert.Equal((0, 1, true), (pruned!.Entries, pruned.Blobs, pruned.Whole));
        Assert.Equal(Definition, cache.ReadText(resolved.Entry, Yml));
        Assert.Equal(Prompt, cache.ReadText(resolved.Entry, PromptPath));
    }

    /// <summary>
    /// A pruning decides each stale entry again under the lock, on the entry as it reads then: one a run used after the
    /// pruning listed it is kept, with every blob it holds.
    /// </summary>
    [Fact]
    public async Task APruning_KeepsAnEntryARunUsedAfterItWasListed()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var (remote, cache, fetcher, _) = Fixture(temp, clock);
        var root = cache.Directory;
        await remote.TagAsync("v1", await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt)));
        var used = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        clock.Advance(TimeSpan.FromDays(31));

        var fileSystem = new Meanwhile(
            Physical(),
            (done, path) => done == Meanwhile.Read && path.StartsWith(Path.Combine(root, "entries"), StringComparison.Ordinal),
            () => _ = cache.Hold(used.Entry, resolution: null, clock.GetUtcNow()));

        var pruning = new WorkflowCache(fileSystem, root).Prune(30, clock.GetUtcNow());

        Assert.True(fileSystem.Happened);
        Assert.Equal((0, 0, true), (pruning.Entries, pruning.Blobs, pruning.Whole));
        Assert.Equal(Prompt, cache.ReadText(Assert.Single(cache.List().Entries).Entry, PromptPath));
    }

    /// <summary>
    /// A pruning reads which blobs the entries hold again under the lock for each batch it removes: a blob it listed that
    /// an entry recorded since holds - another process's fetch of a commit that shares it - is kept, and the rest go.
    /// </summary>
    [Fact]
    public async Task APruning_KeepsABlobAnEntryRecordedAfterTheBlobsWereListedHolds()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var (remote, cache, fetcher, _) = Fixture(temp, clock);
        var root = cache.Directory;
        var moved = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/old.md", "an old prompt\n"));
        var commit = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt));
        await remote.TagAsync("v0", moved);
        await remote.TagAsync("v1", commit);
        _ = await fetcher.ResolveAsync(Parsed() with { Ref = "v0" }, Allowed(), NoVariables, recorded: null, Token);
        clock.Advance(TimeSpan.FromDays(31));
        WorkflowRemoteResolution? fetched = null;

        // The other process's fetch runs on a thread of its own, as another process would, while this one prunes.
        var fileSystem = new Meanwhile(
            Physical(),
            (done, path) => done == Meanwhile.Listed && path == Path.Combine(root, "blobs"),
            () => fetched = Task.Run(() => fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token), Token).GetAwaiter().GetResult());

        var pruning = new WorkflowCache(fileSystem, root).Prune(30, clock.GetUtcNow());

        Assert.True(fileSystem.Happened);
        Assert.Equal((1, 1, true), (pruning.Entries, pruning.Blobs, pruning.Whole));
        Assert.Equal(Definition, cache.ReadText(fetched!.Entry, Yml));
        Assert.Equal(Prompt, cache.ReadText(fetched.Entry, PromptPath));
        Assert.Equal([commit], cache.List().Entries.Select(listed => listed.Entry.Commit));
    }

    /// <summary>
    /// A use is recorded with the look that its entry is still there: one a pruning removed as it was read is warned of
    /// and fetched again whole, never answered as held.
    /// </summary>
    [Fact]
    public async Task AnEntryPrunedAsItIsRead_IsFetchedAgain_NeverAnsweredAsHeld()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var root = cache.Directory;
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);
        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var entry = System.IO.Directory.EnumerateFiles(Path.Combine(root, "entries")).Single();
        var fileSystem = new Meanwhile(
            Physical(),
            (done, path) => done == Meanwhile.Read && path.StartsWith(Path.Combine(root, "blobs"), StringComparison.Ordinal),
            () => File.Delete(entry));
        var warned = new List<string>();
        var output = Substitute.For<IHarnessOutput>();
        output.When(said => said.Warn(Arg.Any<string>(), Arg.Any<string>())).Do(call => warned.Add($"{call.ArgAt<string>(0)}: {call.ArgAt<string>(1)}"));
        var reading = new WorkflowCache(fileSystem, root);
        remote.Clear();

        var resolved = await new WorkflowRemoteFetcher(remote.Git, Physical(), reading, Person(), output, new ManualClock(), remote.AddressOf).ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.True(fileSystem.Happened);
        Assert.Equal(Definition, resolved.Definition);
        Assert.Equal([$"{WorkflowRemoteFetcher.Said}: '{Source}': the workflow cache's entry for the commit {commit} was pruned as it was read; its directory is fetched again whole"], warned);
        Assert.Equal(["ls-remote", "fetch refs/tags/v1", "fetch by id", "fetch by id"], remote.Asked);
        Assert.NotNull(cache.Find("github.com", "dailysoftwaresystems", "shared", commit, Directory));

        // Offline, where the cache's last resolution stands in, what was read stands - the remote is not there to fetch it
        // again - and the pruning is said.
        var offline = new Meanwhile(
            Physical(),
            (done, path) => done == Meanwhile.Read && path.StartsWith(Path.Combine(root, "blobs"), StringComparison.Ordinal),
            () => File.Delete(entry));
        remote.Offline = true;
        warned.Clear();

        var stood = await new WorkflowRemoteFetcher(remote.Git, Physical(), new WorkflowCache(offline, root), Person(), output, new ManualClock(), remote.AddressOf).ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" },
            NoVariables,
            recorded: null,
            Token);

        Assert.True(offline.Happened);
        Assert.Equal((Definition, true), (stood.Definition, stood.FromLastResolved));
        Assert.Equal(
            $"{WorkflowRemoteFetcher.Said}: '{Source}': the workflow cache's entry for the commit {commit} was pruned as it was read; what was read stands, and it is fetched again where the forge is reached",
            warned[^1]);
    }

    /// <summary>
    /// The cache's lock another process holds is waited for while that process holds it, never refused as held, and the
    /// use is recorded once the lock is released.
    /// </summary>
    [Fact]
    public void TheCachesLockHeldByAnother_IsWaitedFor_AndTheUseRecordedOnceItIsReleased()
    {
        using var temp = new TempDirectory();
        var (_, cache, _, _) = Fixture(temp);
        Exception? failed = null;
        Thread? other = null;

        _ = DssHarness.Core.Execution.MachineWideFile.Update(
            Path.Combine(cache.Directory, "entries"),
            DssHarness.Core.Execution.MachineWideFile.Window,
            () =>
            {
                // A thread of its own, never this one: a machine-wide section is the thread's that took it, taken again
                // freely; while this one holds it, the other's hold waits.
                other = new Thread(() => failed = Record.Exception(() => cache.Hold(Entry(), resolution: null, DateTimeOffset.UtcNow)));
                other.Start();

                Assert.False(other.Join(TimeSpan.FromSeconds(2)), "the other thread's hold ended while the lock was held");
                return true;
            });

        Assert.True(other!.Join(TimeSpan.FromMinutes(1)), "the other thread's hold never ended once the lock was released");
        Assert.Null(failed);
    }

    /// <summary>
    /// A command an agent runs is told by the one rule run-side-task is told by: with its mark taken away, the process tree
    /// still says the engine started it, and it reads the workflow cache alone, asking the remote nothing and writing
    /// nothing - refused, where the cache does not hold it, naming the process of this tool's that made it an agent's by
    /// its name and id, so a person whose own command a leg or a step started knows what refused it; one whose tree cannot
    /// be read is no person's either, and is refused saying why. Each says where a person fills the cache instead.
    /// </summary>
    [Fact]
    public async Task ACommandUnderAnAgent_WithItsMarkTakenAway_IsToldByTheProcessTree_AndReadsTheCacheAlone()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.TagAsync("v1", commit);

        ProcessTableReading UnderTheEngine(int current)
            => new(
                [
                    new SampledProcess(10, null, "dssharness", null, "dssharness run-orchestrator implementation", null),
                    new SampledProcess(20, 10, "claude", null, "claude --print", "/usr/bin/node"),
                    new SampledProcess(30, 20, "bash", null, "bash -c 'env -u DSSHARNESS_UNDER_AGENT dssharness validate-workflow'", "/usr/bin/bash"),
                    new SampledProcess(current, 30, "dssharness", null, "dssharness validate-workflow", null),
                ],
                null);

        var underTheEngine = Over(cache, remote, Ancestry(UnderTheEngine));
        var unfetched = await Assert.ThrowsAsync<HarnessException>(() => underTheEngine.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, unfetched.ExitCode);
        Assert.Equal(
            $"'{Source}' is not in the workflow cache as this command needs it (it holds no resolution of 'v1'), and this command reads the cache alone, taken for an agent's: the process 'dssharness' (id 10) above it in the process tree is a process of this tool's, as an agent's engine is, and a command any process of this tool's starts is taken for an agent's, a leg's, a check's or a machine step's as much, for an agent's child can take its mark away but not its place in the tree; {FilledFromAShell}",
            unfetched.Message);
        Assert.Empty(remote.Runs);

        var blind = Over(
            cache,
            remote,
            Ancestry(current => new ProcessTableReading([new SampledProcess(current, null, "dssharness", null, null)], "the process table came back empty, which no running machine's is")));
        var unread = await Assert.ThrowsAsync<HarnessException>(() => blind.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, unread.ExitCode);
        Assert.Equal(
            $"'{Source}' is not in the workflow cache as this command needs it (it holds no resolution of 'v1'), and this command reads the cache alone, taken for an agent's: which processes started this one could not be read - the process table came back empty, which no running machine's is - so whether an agent started it could not be told; {FilledFromAShell}",
            unread.Message);
        Assert.Empty(remote.Runs);

        // A person's command fetches it; the agent's then reads it, asking and writing nothing.
        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var written = Snapshot(cache.Directory);
        remote.Clear();

        var resolved = await underTheEngine.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal((commit, Definition), (resolved.Commit, resolved.Definition));
        Assert.Empty(remote.Runs);
        Assert.Equal(written, Snapshot(cache.Directory));
    }

    /// <summary>
    /// Where nothing is fetched over it - under an agent, and offline where the cache's last resolution would stand in -
    /// an entry's record or a blob of the cache's own that could not be read is the cache's own failure, never said as a
    /// workflow the cache does not hold, which a check would take for the definition's problem; a blob gone leaves its
    /// entry not whole, and is refused as the cache not holding the workflow.
    /// </summary>
    [Fact]
    public async Task AFileOfTheCachesOwnThatCouldNotBeRead_IsTheCachesOwnFailure_WhereNothingIsFetchedOverIt()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        static string? Agent(string name) => name == AgentChildEnvironment.AgentMarker ? "1" : null;
        var lastResolved = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" };
        var commit = await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt));
        await remote.TagAsync("v1", commit);
        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var root = cache.Directory;
        var entry = System.IO.Directory.EnumerateFiles(Path.Combine(root, "entries")).Single();
        var blob = cache.BlobFile(WorkflowCache.BlobSha(Prompt));
        var physical = Physical();

        foreach (var (unreadable, why) in new[]
        {
            (entry, $"the workflow cache's entry '{entry}' could not be read: {Held.Why}; it is not taken for one that is not there"),
            (blob, $"the workflow cache's '{blob}', the file '{PromptPath}' of the entry for 'github.com/dailysoftwaresystems/shared' at '{commit}', could not be read: {Held.Why}; it is not taken for one that is not there"),
        })
        {
            var over = Over(new WorkflowCache(new Held(physical, unreadable), root), remote);
            remote.Offline = false;
            remote.Clear();

            var underAnAgent = await Assert.ThrowsAsync<HarnessException>(() => over.ResolveAsync(Parsed(), Allowed(), Agent, recorded: null, Token));

            Assert.Equal((WorkflowCache.FailedExit, why), (underAnAgent.ExitCode, underAnAgent.Message));
            Assert.False(WorkflowResolution.RefusesTheSource(underAnAgent));
            Assert.Empty(remote.Runs);

            remote.Offline = true;

            var offline = await Assert.ThrowsAsync<HarnessException>(() => over.ResolveAsync(Parsed(), lastResolved, NoVariables, recorded: null, Token));

            Assert.Equal(WorkflowCache.FailedExit, offline.ExitCode);
            Assert.StartsWith(NotReached, offline.Message, StringComparison.Ordinal);
            Assert.EndsWith($"; and the workflow cache, which would stand in, could not be read: {why}", offline.Message, StringComparison.Ordinal);
        }

        // A blob gone leaves the entry not whole: the cache does not hold the workflow, and the refusal says which file.
        File.Delete(blob);

        var gone = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), Agent, recorded: null, Token));

        Assert.Equal(
            (HarnessExit.Refused, $"'{Source}' is not in the workflow cache as this command needs it (its files of the commit '{commit}' are not whole: the workflow cache's '{blob}', the file '{PromptPath}' of the entry for 'github.com/dailysoftwaresystems/shared' at '{commit}', is not there; a resolution of it fetches it again), and this command reads the cache alone, taken for an agent's: it carries DSSHARNESS_UNDER_AGENT, the mark every agent's child is given, whatever the mark holds; {FilledFromAShell}"),
            (gone.ExitCode, gone.Message));
    }

    /// <summary>
    /// A run's own copy of a remote workflow is kept as the resolution read and hashed it, kept again only where it does
    /// not hold it whole, and read by the run's steps there by the workflow's name - whatever a pruning did to the user's
    /// cache since.
    /// </summary>
    [Fact]
    public async Task ARunsOwnCopy_IsKeptOnce_AndReadWhateverAPruningDoesToTheUsersCache()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new ManualClock();
        var (remote, cache, fetcher, _) = Fixture(temp, clock);
        await remote.TagAsync("v1", await remote.CommitAsync(("security-review.yml", Definition), ("prompts/review.md", Prompt)));
        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var run = new DssHarness.Core.Orchestration.OrchestratorLayout("r1", temp.Combine(".orchestrators", "r1"));
        var kept = WorkflowCache.OfRun(harness.FileSystem, run);

        kept.Keep([resolved]);
        var entry = System.IO.Directory.EnumerateFiles(Path.Combine(run.RemoteWorkflowsDirectory, "entries")).Single();
        File.SetLastWriteTimeUtc(entry, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        kept.Keep([resolved]);

        // Held whole already, it is not written again.
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(entry));

        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, cache.Prune(30, clock.GetUtcNow()).Entries);
        Assert.Empty(cache.List().Entries);

        var files = WorkflowNamedFiles.OfRun(harness.FileSystem, StringComparison.Ordinal, run);

        Assert.Equal(Prompt, files.ReadText(new HarnessLayout(temp.Path, temp.Path), WorkflowRemote.Name(resolved.Source, resolved.Commit), "prompts/review.md", "a prompt"));

        // A blob of the copy gone is kept again.
        File.Delete(kept.BlobFile(WorkflowCache.BlobSha(Prompt)));
        kept.Keep([resolved]);

        Assert.Equal(Prompt, kept.ReadText(resolved.Entry, PromptPath));
    }

    /// <summary>
    /// A run keeps the remote workflows it began with in its own directory as its session begins, names that copy among
    /// its records, and each later session - an answer's, as a resume's - keeps it again where it is not whole, so no
    /// pruning of the user's cache takes a file from a run that started.
    /// </summary>
    [Fact]
    public async Task ARun_KeepsTheRemoteWorkflowsItBeganWith_AndEachLaterSessionKeepsThemAgain()
    {
        const string Paused = "name: security-review\nsteps:\n  - name: review\n    type: pause\n    question: go on?\n    options: [stay, go]\n";
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Paused));
        await remote.TagAsync("v1", commit);
        var (repository, _) = await FlowServiceTests.RepositoryAsync(
            $"name: flow\nsteps:\n  - name: scan\n    use: {Source}\n",
            new HarnessConfig { Workflows = [FlowServiceTests.Workflow], WorkflowSources = Allowed(), Defaults = new HarnessDefaults { AgentTier = TestAdvice.AgentTier }, Providers = TestAdvice.Providers, TierConfig = TestAdvice.TierConfig });
        using var disposed = repository;
        var harness = repository.Harness;
        var validation = new WorkflowValidationService(
            harness.ContextLoader,
            harness.FileSystem,
            harness.Platform,
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform),
            DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild,
            fetcher,
            cache);
        var service = FlowServiceTests.Service(harness, new Pausing(), validation: validation);
        var run = FlowServiceTests.RunOf(repository);
        var kept = WorkflowCache.OfRun(harness.FileSystem, run);

        var started = await service.RunAsync(repository.Path, FlowServiceTests.Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(FlowServiceTests.Bound, Token);

        Assert.Equal(FlowExit.Paused, started.ExitCode);
        Assert.Equal(Paused, kept.ReadText(kept.Find("github.com", "dailysoftwaresystems", "shared", commit, Directory)!, Yml));
        Assert.Contains($"remoteWorkflows: {run.RemoteWorkflowsDirectory}", started.Details ?? []);

        System.IO.Directory.Delete(run.RemoteWorkflowsDirectory, recursive: true);

        var answered = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(FlowServiceTests.Bound, Token);

        Assert.Equal((HarnessExit.Success, "done"), (answered.ExitCode, answered.Message));
        Assert.Equal(Paused, kept.ReadText(kept.Find("github.com", "dailysoftwaresystems", "shared", commit, Directory)!, Yml));
    }

    /// <summary>
    /// A run's later session checks the remote workflows it began with by the run's own copy first, as its steps read
    /// them: the user's cache pruned since, and then the network not there - or the tag moved to another commit, and the
    /// one the run began with reached by no branch or tag of the repository any more - takes nothing from the run, which
    /// goes on asking the remote nothing and writing nothing into the user's cache.
    /// </summary>
    /// <param name="offline">Whether the network is gone, rather than the tag moved off the run's commit.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALaterSession_ReadsTheRunsOwnCopyFirst_WhateverAPruningTheNetworkOrTheTagDidSince(bool offline)
    {
        const string Paused = "name: security-review\nsteps:\n  - name: review\n    type: pause\n    question: go on?\n    options: [stay, go]\n";
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var (remote, cache, fetcher, _) = Fixture(temp, clock);
        await remote.TagAsync("v1", await remote.CommitAsync(("security-review.yml", Paused)));
        var (repository, _) = await FlowServiceTests.RepositoryAsync(
            $"name: flow\nsteps:\n  - name: scan\n    use: {Source}\n",
            new HarnessConfig { Workflows = [FlowServiceTests.Workflow], WorkflowSources = Allowed(), Defaults = new HarnessDefaults { AgentTier = TestAdvice.AgentTier }, Providers = TestAdvice.Providers, TierConfig = TestAdvice.TierConfig });
        using var disposed = repository;
        var harness = repository.Harness;
        var validation = new WorkflowValidationService(
            harness.ContextLoader,
            harness.FileSystem,
            harness.Platform,
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform),
            DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild,
            fetcher,
            cache);
        var service = FlowServiceTests.Service(harness, new Pausing(), validation: validation);

        var started = await service.RunAsync(repository.Path, FlowServiceTests.Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(FlowServiceTests.Bound, Token);

        Assert.Equal(FlowExit.Paused, started.ExitCode);

        // The user's cache pruned: the run's start is the entry's last use, a month ago.
        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, cache.Prune(30, clock.GetUtcNow()).Entries);

        if (offline)
        {
            remote.Offline = true;
        }
        else
        {
            // The tag moved to another commit, and no branch or tag reaches the one the run began with: a fetch of it now
            // could not prove it the repository's own.
            await remote.TagAsync("v1", await remote.CommitAsync(("security-review.yml", "name: security-review\nsteps: []\n")));
        }

        remote.Clear();

        var answered = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(FlowServiceTests.Bound, Token);

        Assert.Equal((HarnessExit.Success, "done"), (answered.ExitCode, answered.Message));
        Assert.Empty(remote.Runs);
        Assert.Empty(cache.List().Entries);
    }

    /// <summary>A workflow's directory of fifty thousand files is fetched whole - its tree and its blobs each asked for once, by their ids - and every file is kept and read back.</summary>
    [Fact]
    public async Task AFiftyThousandFileWorkflowDirectory_IsFetched()
    {
        const int Notes = 50_000;
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        static string Note(int at) => $"notes/n{at.ToString("D5", CultureInfo.InvariantCulture)}.md";
        static string Said(int at) => $"note {at.ToString(CultureInfo.InvariantCulture)}\n";
        var commit = await remote.CommitAsync([Tracked.Text("security-review.yml", Definition), .. Enumerable.Range(0, Notes).Select(at => Tracked.Text(Note(at), Said(at)))]);
        await remote.TagAsync("v1", commit);

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var entry = Assert.Single(cache.List().Entries).Entry;

        Assert.Equal((commit, Notes + 1, Notes + 1), (resolved.Commit, resolved.Files.Count, entry.Files.Count));
        Assert.All(Enumerable.Range(0, Notes), at => Assert.Equal(Said(at), resolved.Text($"{Directory}/{Note(at)}")));
        Assert.Equal(Said(0), cache.ReadText(entry, $"{Directory}/{Note(0)}"));
        Assert.Equal(Said(Notes - 1), cache.ReadText(entry, $"{Directory}/{Note(Notes - 1)}"));
        Assert.Equal(["ls-remote", "fetch refs/tags/v1", "fetch by id", "fetch by id"], remote.Asked);
    }

    /// <summary>A commit named by its SHA that only the hundredth of a hundred tags reaches is proved the repository's own by git's walk over every ref, and one no branch or tag reaches is refused.</summary>
    [Fact]
    public async Task ACommitReachableOnlyFromTheHundredthTag_IsProven()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commitOnly = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "commit" };
        var trunk = await remote.CommitAsync(("security-review.yml", Definition));
        var side = await remote.CommitAsync([Tracked.Text("security-review.yml", Definition), Tracked.Text("prompts/review.md", Prompt)], parent: trunk);
        await remote.BranchAsync("main", trunk);

        // The first ninety-nine tags, in the order git names them, stand at the trunk; the hundredth alone at the side line.
        await remote.RefsAsync([.. Enumerable.Range(1, 99).Select(at => ($"refs/tags/t{at.ToString("D3", CultureInfo.InvariantCulture)}", trunk)), ("refs/tags/t100", side)]);

        var resolved = await fetcher.ResolveAsync(Parsed() with { Ref = side }, commitOnly, NoVariables, recorded: null, Token);

        Assert.Equal((side, Prompt), (resolved.Commit, resolved.Text(PromptPath)));
        Assert.Equal(["fetch every ref", "fetch by id", "fetch by id"], remote.Asked);

        // A commit the remote holds that no branch or tag reaches.
        var dangling = await remote.CommitAsync([Tracked.Text("security-review.yml", Definition)], parent: side);
        remote.Clear();

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed() with { Ref = dangling }, commitOnly, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@{dangling}' names the commit '{dangling}', which no branch or tag of 'dailysoftwaresystems/shared' reaches - every one of them was walked: GitHub serves a commit of any fork of a repository under the repository's own name, and one that is not the repository's own is never run; name a tag of it, or a commit one of its branches or tags holds. Nothing was cached",
            refused.Message);
        Assert.Equal(["fetch every ref"], remote.Asked);
        Assert.Equal([side], cache.List().Entries.Select(listed => listed.Entry.Commit));
    }

    /// <summary>A tag fifteen tag objects deep - each annotated tag naming the one before it - is peeled through every one of them to the commit.</summary>
    [Fact]
    public async Task AFifteenDeepTagChain_Peels()
    {
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var commit = await remote.CommitAsync(("security-review.yml", Definition));
        var tagged = await remote.AnnotatedTagAsync("t1", commit, "commit");

        for (var depth = 2; depth < 15; depth++)
        {
            tagged = await remote.AnnotatedTagAsync($"t{depth.ToString(CultureInfo.InvariantCulture)}", tagged, "tag");
        }

        _ = await remote.AnnotatedTagAsync("v1", tagged, "tag");

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal((commit, Definition), (resolved.Commit, resolved.Definition));
        Assert.Equal(commit, Assert.Single(cache.List().Entries).Entry.Commit);
        Assert.Equal(["ls-remote", "fetch refs/tags/v1", "fetch by id", "fetch by id"], remote.Asked);
    }

    /// <summary>Two hundred tags are listed whole in one call, and the last of them in the order git names them resolves.</summary>
    [Fact]
    public async Task TwoHundredTags_AreAllListed()
    {
        using var temp = new TempDirectory();
        var (remote, _, fetcher, _) = Fixture(temp);
        var early = await remote.CommitAsync(("security-review.yml", "name: security-review\nsteps: []\n"));
        var last = await remote.CommitAsync(("security-review.yml", Definition));
        await remote.RefsAsync([.. Enumerable.Range(1, 199).Select(at => ($"refs/tags/v{at.ToString("D3", CultureInfo.InvariantCulture)}", early)), ("refs/tags/v200", last)]);

        var refs = await new WorkflowGitFetch(remote.Git, Physical(), null, TimeProvider.System, remote.AddressOf).RefsAsync(Parsed(), credential: null, Token);

        Assert.Equal(200, refs.Keys.Count(name => name.StartsWith("refs/tags/", StringComparison.Ordinal)));
        Assert.Equal(last, refs["refs/tags/v200"].Object);

        remote.Clear();
        var resolved = await fetcher.ResolveAsync(Parsed() with { Ref = "v200" }, Allowed(), NoVariables, recorded: null, Token);

        Assert.Equal((last, Definition), (resolved.Commit, resolved.Definition));
        Assert.Equal(["ls-remote", "fetch refs/tags/v200", "fetch by id", "fetch by id"], remote.Asked);
    }

    /// <summary>A directory past what one answer of the forge's tree listing holds - over a hundred thousand entries, and a path thirty directories deep - is fetched whole by git, every file in the entry.</summary>
    [Fact]
    public async Task ADirectoryPastTheApisTruncation_IsFetchedByGit()
    {
        const string Shared = "the same note\n";
        const string Deepest = "the deepest note\n";
        using var temp = new TempDirectory();
        var (remote, cache, fetcher, _) = Fixture(temp);
        var deep = string.Join('/', Enumerable.Range(0, 30).Select(at => $"d{at.ToString(CultureInfo.InvariantCulture)}")) + "/deep.md";
        static string Wide(int directory, int file) => $"s{directory.ToString("D4", CultureInfo.InvariantCulture)}/f{file.ToString("D3", CultureInfo.InvariantCulture)}.md";
        var commit = await remote.CommitAsync(
        [
            Tracked.Text("security-review.yml", Definition),
            Tracked.Text(deep, Deepest),
            .. Enumerable.Range(0, 1_000).SelectMany(directory => Enumerable.Range(0, 100).Select(file => Tracked.Text(Wide(directory, file), Shared))),
        ]);
        await remote.TagAsync("v1", commit);

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), NoVariables, recorded: null, Token);
        var entry = Assert.Single(cache.List().Entries).Entry;

        Assert.Equal((commit, 100_002, 100_002), (resolved.Commit, resolved.Files.Count, entry.Files.Count));
        Assert.Equal(3, entry.Files.Select(file => file.Blob).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Deepest, cache.ReadText(entry, $"{Directory}/{deep}"));
        Assert.Equal(Shared, cache.ReadText(entry, $"{Directory}/{Wide(999, 99)}"));
        Assert.All(Enumerable.Range(0, 1_000), directory => Assert.Equal(Shared, resolved.Text($"{Directory}/{Wide(directory, 0)}")));
    }

    /// <summary>
    /// The workflowSources section is held to its rules: each allowed source named as one is - never blank - under a
    /// host the section names where it is under one, each host a host with the environment variable its credential is
    /// read from, none without one, and pin and offline spelled as they may be - github.com needing no host entry, for
    /// the machine's own login reaches it.
    /// </summary>
    [Fact]
    public void TheWorkflowSourcesSection_IsHeldToItsRules()
    {
        Assert.Empty(HarnessConfigValidator.Validate(new HarnessConfig()));

        Assert.Equal(
            [
                "workflowSources.allow names 'acme/*/*', which puts * where a name is: the star stands for the repositories of an owner, owner/*",
                "workflowSources.allow names 'acme/shared/deep/one', which is neither an owner, owner/*, owner/repository nor host/owner/repository: each segment is named, and only a repository may be *",
                "workflowSources.allow names ' ', which names nothing: an entry is an owner, owner/*, owner/repository or the same under host/",
                "workflowSources.allow names 'elsewhere.example/acme/*', under the host 'elsewhere.example', which workflowSources.hosts does not name: a host beyond github.com is reached only with the credential its entry there names",
                "workflowSources.hosts names 'ghe', which is no host: a host is named as its own name is, dotted, with no path in it",
                "workflowSources.hosts.ghe names no credential; a host beyond github.com is reached with the one credential named here, \"credential\": {\"from\": \"env\", \"name\": \"<VARIABLE>\"}, and never with the machine's own tokens",
                "workflowSources.hosts names 'github.com', which needs no entry: git reaches it with the helpers its own configuration names, the machine's own login",
                "workflowSources.hosts.ghe.acme.internal.credential.from is 'file'; a host's credential is read from an environment variable, env",
                "workflowSources.hosts.other.acme.internal.credential names no environment variable; a host's credential is an environment variable, named here",
                "workflowSources.pin is 'branch'; it is one of tag-or-commit, commit, any",
                "workflowSources.offline is 'maybe'; it is one of refuse, last-resolved",
            ],
            HarnessConfigValidator.Validate(new HarnessConfig
            {
                WorkflowSources = new WorkflowSourcesConfig
                {
                    Allow = ["acme/*/*", "acme/shared/deep/one", " ", "elsewhere.example/acme/*"],
                    Hosts = new Dictionary<string, WorkflowHostConfig>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ghe"] = new(),
                        ["github.com"] = new(),
                        ["ghe.acme.internal"] = new() { Credential = new WorkflowHostCredentialConfig { From = "file", Name = "GHE_TOKEN" } },
                        ["other.acme.internal"] = new() { Credential = new WorkflowHostCredentialConfig { From = "env" } },
                    },
                    Pin = "branch",
                    Offline = "maybe",
                },
            }));

        Assert.Empty(HarnessConfigValidator.Validate(new HarnessConfig
        {
            WorkflowSources = new WorkflowSourcesConfig
            {
                Allow = ["acme", "acme/*", "acme/shared", "acme/.github", "ghe.acme.internal/team/*", "ghe.acme.internal/team", "github.com/acme"],
                Hosts = new Dictionary<string, WorkflowHostConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ghe.acme.internal"] = new() { Credential = new WorkflowHostCredentialConfig { From = "env", Name = "GHE_TOKEN" } },
                },
                Pin = "commit",
                Offline = "last-resolved",
            },
        }));
    }

    /// <summary>
    /// The help, the README and the architecture say what the fetch, the cache and the pruning do, from the code's own
    /// bounds and names: how git lists, fetches and proves, how a host's credential is handed, the blob's path, the days a
    /// pruning takes and the code it ends incomplete with - and the configuration's help the shape a host's credential is
    /// written in.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayWhatTheFetchAndTheCacheDo()
    {
        var help = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("workflows"));
        var remote = Prose.Words(DssHarness.Core.Help.HelpTopics.RemoteWorkflowsHelp);
        var cacheHelp = Prose.Words(DssHarness.Core.Help.HelpTopics.WorkflowCacheHelp);

        Assert.Contains(remote, help, StringComparison.Ordinal);
        Assert.Contains(cacheHelp, help, StringComparison.Ordinal);

        foreach (var said in new[]
        {
            "It is fetched by git itself.",
            "through the repository's own refs, every one of them listed in one call (git ls-remote)",
            "an annotated one peeled through each tag object it names, however many, to the commit",
            "is fetched only once git's own walk over every branch and tag of the repository (for-each-ref --contains) finds one that reaches it",
            "and the commit's tree, asked for by its id with no blob, read whole at the workflow's directory",
            "every blob of the directory comes in one transfer, asked for by its id with nothing checked out",
            "a host beyond it with the one credential its entry names, handed to git in its environment with every helper cleared, and none of the machine's",
            $"in {DssHarness.Core.Orchestration.OrchestratorLayout.RemoteWorkflowsDirectoryName}/ of its own directory",
            "a ref the cache last resolved as a branch refused where the pin is not any",
            "refused with what git said, and nothing stands in for it",
            $"the forge not reached with nothing standing in ({HarnessExit.CommandFailed}, git saying it made no connection)",
            $"or whose lock could not be taken ({WorkflowCache.FailedExit})",
            "A remote workflow's schemas are held to the subset of JSON Schema the engine reads as a local one's are.",
            $"told by the one rule {DssHarness.Core.Agents.SideTasks.SideTaskService.RunCommand} is told by, the agent's mark or a process of this tool's above it in the process tree, one whose tree cannot be read taken as no person's",
            "both name it in --json, as cache, on every exit.",
            "as the check a resume goes through reads them there first - the cache, and then the forge, asked only for a use the copy does not hold whole: neither a pruning of the cache, the network not being there, nor a tag deleted or a branch pushed over since takes a file from a run that started or refuses it",
            "so a command any process of this tool's starts (a leg's, a check's or a machine step's as much) is taken for an agent's",
            $"a use the cache does not hold whole is refused saying what made the command an agent's - the mark, the process of this tool's by its name and id, or why the tree could not be read - and that a person fills the cache from {AgentAncestryAnswer.APersonsShell}, with 'dssharness {WorkflowValidationService.CommandName}' or 'dssharness {WorkflowExportService.CommandName} --from'; a record or a blob of the cache's that could not be read is the cache's own failure there too ({WorkflowCache.FailedExit}).",
        })
        {
            Assert.Contains(said, remote, StringComparison.Ordinal);
        }

        Assert.Contains("held for one record or one batch of blobs at a time", cacheHelp, StringComparison.Ordinal);
        Assert.Contains("a wait for the lock lasts while the process holding it lives, told every five minutes", cacheHelp, StringComparison.Ordinal);
        Assert.Contains($"or whose lock could not be taken, ends incomplete ({WorkflowCache.FailedExit})", cacheHelp, StringComparison.Ordinal);
        Assert.Contains("the days required, 0 or more - any number, those reaching back past the clock's start putting the cut-off there", cacheHelp, StringComparison.Ordinal);
        Assert.Contains(
            "or a remote workflow, owner/repo/path/workflow.yml@ref or the same under a GitHub Enterprise host the configuration names, fetched into the workflow cache and read from it",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            Prose.Words("hosts, GitHub Enterprise hosts, each with the one credential its calls are given, \"credential\": {\"from\": \"env\", \"name\": \"<VARIABLE>\"} (required)"),
            Prose.Words(DssHarness.Core.Help.HelpTopics.Render("config")),
            StringComparison.Ordinal);

        var readme = Prose.Document("README.md");

        foreach (var said in new[]
        {
            "`workflowSources.hosts` names each GitHub Enterprise host with its one credential, `\"credential\": {\"from\": \"env\", \"name\": \"<VARIABLE>\"}`, handed to git in its environment with every helper git's configuration names cleared, and none of the machine's - a host with none, or whose variable is not set, is refused before anything is asked of it - while github.com is reached with the helpers git's configuration names, the machine's own login;",
            "each blob as its bytes at `blobs/<first 2 hex>/<remaining 38 hex>`",
            "every branch and tag listed by git itself in one call (`git ls-remote`)",
            "`workflow-cache prune --unused-days N`, the days required, 0 or more - any number, those reaching back past the clock's start putting the cut-off there -",
            $"every blob an entry it keeps holds kept, and no blob removed at all while a file of the cache's own could not be read; a file it could not remove or read is said, and the pruning ends incomplete ({HarnessExit.Incomplete})",
            "`offline` says what happens where the network keeps the forge from being reached",
            "in `remote-workflows/` of its own directory",
            $"the forge not reached with nothing standing in (20, git saying it made no connection), git failing otherwise or answering what does not read (20), and the workflow cache that could not be named, written or read, or whose lock could not be taken ({WorkflowCache.FailedExit})",
            "or its lock could not be taken",
            "both name it in `--json`, as `cache`, on every exit",
            "a `--from` that names no remote workflow, is a usage error (10)",
            "as the check a resume goes through reads them there first - the cache, and then the forge, asked only for a use the copy does not hold whole: neither a pruning of the cache, the network not being there, nor a tag deleted or a branch pushed over since takes a file from a run that started or refuses it",
            "so a command any process of this tool's starts (a leg's, a check's or a machine step's as much) is taken for an agent's",
            "a use the cache does not hold whole is refused saying what made the command an agent's - the mark, the process of this tool's by its name and id, or why the tree could not be read - and that a person fills the cache from a shell of their own, which no process of this tool's started and which carries no `DSSHARNESS_UNDER_AGENT`, with `dssharness validate-workflow` or `dssharness export-workflow --from`; a record or a blob of the cache's that could not be read is the cache's own failure there too (21).",
        })
        {
            Assert.Contains(said, readme, StringComparison.Ordinal);
        }

        var architecture = Prose.Document("docs", "architecture.md");

        foreach (var said in new[]
        {
            $"(`WorkflowRemote.{nameof(WorkflowRemote.RefProblem)}`, before anything is asked)",
            $"(`WorkflowCache.{nameof(WorkflowCache.TreePathProblems)}`)",
            $"every helper git's configuration names cleared, and one that answers with it alone put in their place (`{nameof(DssHarness.Core.Git.GitCredentialValues)}`), so no command line carries it",
            $"(`{nameof(WorkflowGitFetch)}.{nameof(WorkflowGitFetch.Unreached)}`) - is the network not being there",
            $"(`WorkflowCache.{nameof(WorkflowCache.BlobOf)}`, the inverse of the blob's path)",
            "at `blobs/<first 2 hex>/<remaining 38 hex>`",
            $"(`WorkflowCache.{nameof(WorkflowCache.BlobsPerHold)}`)",
            $"(`WorkflowCache.{nameof(WorkflowCache.Hold)}`)",
            $"(`OrchestratorLayout.{nameof(DssHarness.Core.Orchestration.OrchestratorLayout.RemoteWorkflowsDirectory)}`,",
            $"(`WorkflowCache.{nameof(WorkflowCache.Keep)}`,",
            $"(`WorkflowNamedFiles.{nameof(WorkflowNamedFiles.OfRun)}`)",
            $"(`WorkflowResolution.{nameof(WorkflowResolution.RefusesTheSource)}`: {HarnessExit.Refused}, or {HarnessExit.ConfigInvalid} for a workflow file that is no text)",
            $"(`WorkflowCache.{nameof(WorkflowCache.FailedExit)}`, {WorkflowCache.FailedExit})",
            $"(`AgentAncestry.{nameof(AgentAncestry.UnderAnAgentAsync)}`)",
            $"(`WorkflowFiles.{nameof(DssHarness.Core.Orchestration.Flow.WorkflowFiles.SchemaProblems)}`)",
            $"reads it from the run's own copy first (`{nameof(WorkflowRecordedUse)}`, which `{nameof(WorkflowRunRecord)}` hands the fetcher from the check)",
            "only where neither holds it whole: one order of where a recorded resolution's files come from, whoever runs the command",
            $"(`{nameof(AgentAncestryAnswer)}.{nameof(AgentAncestryAnswer.Why)}`: the mark, the process of this tool's by its name and id, or why the tree could not be read; `{nameof(AgentAncestryAnswer)}.{nameof(AgentAncestryAnswer.APersonsShell)}`:",
            $"(`WorkflowCache.{nameof(WorkflowCache.ReadBytes)}`: a blob gone or changed on disk leaves its entry not whole, one that could not be read is the cache's failure)",
        })
        {
            Assert.Contains(said, architecture, StringComparison.Ordinal);
        }
    }

    /// <summary>A fixed settings section that allows the fixture's repository.</summary>
    private static WorkflowSourcesConfig Allowed() => new() { Allow = ["dailysoftwaresystems/*"] };

    /// <summary>A section that allows <c>acme</c> on <paramref name="host"/>, its credential the variable <c>GHE_TOKEN</c>.</summary>
    private static WorkflowSourcesConfig Hosted(string host)
        => new()
        {
            Allow = [$"{host}/acme/*"],
            Hosts = new Dictionary<string, WorkflowHostConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [host] = new() { Credential = new WorkflowHostCredentialConfig { From = "env", Name = "GHE_TOKEN" } },
            },
        };

    /// <summary>The fixture's source, parsed.</summary>
    private static WorkflowRemoteSource Parsed() => WorkflowRemote.Source(Source)!;

    /// <summary>An environment naming no variable.</summary>
    private static string? NoVariables(string name) => null;

    /// <summary>The values a call that reached a remote was handed in its environment, by name.</summary>
    private static (string, string)[] Handed(IReadOnlyDictionary<string, string>? values)
        => [.. (values ?? new Dictionary<string, string>()).OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => (value.Key, value.Value))];

    /// <summary>A listing read whole, compared as a value: its entries by line and its unread files.</summary>
    private static WorkflowCacheListing Listed(WorkflowCache cache)
    {
        var listing = cache.List();

        return listing.Entries.Count == 0 && listing.Unread.Count == 0 ? new WorkflowCacheListing([], []) : listing;
    }

    /// <summary>Every file under <paramref name="root"/>, with its bytes, as a value two moments compare by.</summary>
    private static string Snapshot(string root)
        => string.Join(
            "\n",
            System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(file => $"{file}: {Convert.ToHexString(File.ReadAllBytes(file))}"));

    /// <summary>The disk as it is.</summary>
    private static PhysicalFileSystem Physical() => new(new HarnessFactory().FilePermissions);

    /// <summary>The fetcher, its remotes and its cache in <paramref name="temp"/>, and what it warned of.</summary>
    private static (Remote Remote, WorkflowCache Cache, WorkflowRemoteFetcher Fetcher, List<string> Warned) Fixture(TempDirectory temp, ManualClock? clock = null)
    {
        var remote = new Remote(temp.Combine("remotes"));
        var cache = new WorkflowCache(Physical(), temp.Combine("workflow-cache"));
        var warned = new List<string>();
        var output = Substitute.For<IHarnessOutput>();
        output.When(said => said.Warn(Arg.Any<string>(), Arg.Any<string>())).Do(call => warned.Add($"{call.ArgAt<string>(0)}: {call.ArgAt<string>(1)}"));

        return (remote, cache, new WorkflowRemoteFetcher(remote.Git, Physical(), cache, Person(), output, clock ?? new ManualClock(), remote.AddressOf), warned);
    }

    /// <summary>Another fetcher over <paramref name="cache"/>, reaching <paramref name="remote"/>, for a command whose process tree <paramref name="ancestry"/> reads - a person's where none is given.</summary>
    private static WorkflowRemoteFetcher Over(WorkflowCache cache, Remote remote, AgentAncestry? ancestry = null)
        => new(remote.Git, Physical(), cache, ancestry ?? Person(), clock: new ManualClock(), address: remote.AddressOf);

    /// <summary>The process tree of a command a person's shell started: no process of this tool's above it.</summary>
    private static AgentAncestry Person() => Ancestry(AgentAncestryTests.UnderAShell);

    /// <summary>What the process tree says of this process, as <paramref name="tree"/> reads it from this process's id.</summary>
    private static AgentAncestry Ancestry(Func<int, ProcessTableReading> tree)
    {
        var harness = new HarnessFactory();

        return new AgentAncestry(new AgentAncestryTests.FixedTable(tree(harness.Identity.CurrentId)), harness.Identity, new AgentAncestryTests.OwnProgramAt(null), harness.Platform, harness.FileSystem);
    }

    /// <summary>The cache entry a resolution handed to the resolver names, at <see cref="Pinned"/>.</summary>
    private static WorkflowCacheEntry Entry()
        => new(
            "github.com",
            "dailysoftwaresystems",
            "shared",
            Pinned,
            Directory,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [new WorkflowCacheFile(Yml, WorkflowCache.BlobSha(Definition), "100644")]);

    /// <summary>
    /// A commit of the fixture's repository holding a remote workflow that uses <c>./inner/inner.yml</c>, whose one reason
    /// step names its agent, its prompt and the schema <paramref name="schema"/>.
    /// </summary>
    private static Task<string> HoldsInnerAsync(Remote remote, string schema)
        => remote.CommitAsync(
            ("security-review.yml", "name: security-review\nsteps:\n  - name: review\n    use: ./inner/inner.yml\n"),
            ("inner/inner.yml", "name: inner\nsteps:\n  - name: check\n    type: reason\n    agent: reviewer\n    prompt: prompts/check.md\n    outputs: schemas/check.json\n"),
            ("inner/agents/reviewer.md", "---\nname: reviewer\ndescription: reviews the paths\n---\nYou review.\n"),
            ("inner/prompts/check.md", "Check the paths.\n"),
            ("inner/schemas/check.json", schema));

    /// <summary>
    /// validate-workflow over the repository in <paramref name="temp"/> - initialised the first time, with the agent
    /// sections as init writes them so a step's tier resolves and only what the files say is said - whose one workflow,
    /// <c>flow</c>, uses <paramref name="source"/>.
    /// </summary>
    private static async Task<WorkflowValidationReport> ValidatedAsync(TempDirectory temp, WorkflowCache cache, WorkflowRemoteFetcher fetcher, string source)
    {
        var harness = new HarnessFactory();

        if (!System.IO.Directory.Exists(temp.Combine(".harness-config", "workflows", "flow")))
        {
            var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();

            await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
            {
                WorkflowSources = Allowed(),
                Workflows = ["flow"],
                Defaults = new HarnessDefaults { AgentTier = agents.AgentTier },
                Providers = [.. agents.Providers],
                TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase),
            });
            System.IO.Directory.CreateDirectory(temp.Combine(".harness-config", "workflows", "flow"));
        }

        TestAdvice.WriteWorkflowFile(temp.Combine(".harness-config", "workflows", "flow", "flow.yml"), $"name: flow\nsteps:\n  - name: scan\n    use: {source}\n");

        return await new WorkflowValidationService(
            harness.ContextLoader,
            harness.FileSystem,
            harness.Platform,
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform),
            DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild,
            fetcher,
            cache).CheckAsync(temp.Path, "flow", Token);
    }

    /// <summary>A resolution of the fixture's source whose yml is <paramref name="definition"/>, as a fetch hands it to the resolver.</summary>
    private static WorkflowRemoteResolution Resolved(WorkflowRemoteSource parsed, string definition)
        => new(parsed, Pinned, Entry(), definition, new Dictionary<string, byte[]>(StringComparer.Ordinal) { [Yml] = Encoding.UTF8.GetBytes(definition) });

    /// <summary>A repository a source names: its host, its owner and its name.</summary>
    /// <param name="Host">The host.</param>
    /// <param name="Owner">The owner.</param>
    /// <param name="Name">The repository's name.</param>
    private sealed record Repo(string Host, string Owner, string Name);

    /// <summary>
    /// One entry a commit of the fixture holds under the workflow's directory: its path there, its mode, and its bytes -
    /// or, for a submodule, the commit it names.
    /// </summary>
    /// <param name="Path">Its path under the workflow's directory, forward slashes.</param>
    /// <param name="Mode">Its git mode.</param>
    /// <param name="Content">Its bytes; null for a submodule.</param>
    /// <param name="Object">The commit a submodule names; null else.</param>
    private sealed record Tracked(string Path, string Mode, byte[]? Content, string? Object = null)
    {
        /// <summary>A plain file holding <paramref name="content"/> as UTF-8.</summary>
        public static Tracked Text(string path, string content) => new(path, "100644", Encoding.UTF8.GetBytes(content));

        /// <summary>A plain file holding <paramref name="content"/>.</summary>
        public static Tracked Bytes(string path, byte[] content) => new(path, "100644", content);

        /// <summary>A symbolic link to <paramref name="target"/>.</summary>
        public static Tracked Link(string path, string target) => new(path, "120000", Encoding.UTF8.GetBytes(target));

        /// <summary>A submodule at <paramref name="commit"/>.</summary>
        public static Tracked Submodule(string path, string commit) => new(path, "160000", null, commit);
    }

    /// <summary>
    /// The repositories the fixture's sources name, as git serves them: one bare repository per host, owner and
    /// repository, made the first time a commit is made in it, allowing a fetch by id and a filtered one as GitHub does,
    /// and reached at its file:// address - or, offline, at an address git makes no connection to. Every commit is made
    /// by git's own importer, its time its own, and held by no ref until a tag or a branch is put on it; every call that
    /// reached a remote is kept, through the one git client every fetcher of a test runs over.
    /// </summary>
    private sealed class Remote
    {
        private readonly string _root;
        private readonly HashSet<string> _made = new(StringComparer.Ordinal);
        private int _commits;
        private int _seenFrom;

        /// <param name="root">The directory the repositories are made under.</param>
        public Remote(string root)
        {
            _root = root;
            Git = new InterceptingGitClient(new HarnessFactory().GitClient)
            {
                InsteadOfRemote = arguments => Instead?.Invoke(arguments),
                AfterReadBlobs = (objects, read) => BlobsRead?.Invoke(objects, read),
            };
        }

        /// <summary>The fixture's own repository: <c>github.com/dailysoftwaresystems/shared</c>.</summary>
        public static Repo Shared { get; } = new("github.com", "dailysoftwaresystems", "shared");

        /// <summary>The git client every fetcher over these remotes runs.</summary>
        public InterceptingGitClient Git { get; }

        /// <summary>Whether every remote is reached at an address git makes no connection to, as with the network gone.</summary>
        public bool Offline { get; set; }

        /// <summary>An answer to give a call that reaches a remote instead of git's; null runs git.</summary>
        public Func<IReadOnlyList<string>, GitCommandResult?>? Instead { get; set; }

        /// <summary>What a read of a clone's blobs answers instead of what git read, as a store whose bytes changed would; null keeps git's.</summary>
        public Func<IReadOnlyList<string>, IReadOnlyList<byte[]?>, IReadOnlyList<byte[]?>?>? BlobsRead { get; set; }

        /// <summary>Every call that reached a remote since the last <see cref="Clear"/>, with the values it was handed.</summary>
        public IReadOnlyList<(string[] Arguments, IReadOnlyDictionary<string, string>? Values)> Runs => [.. Git.RemoteRuns.Skip(_seenFrom)];

        /// <summary>
        /// What each call since the last <see cref="Clear"/> asked: the refs listed (<c>ls-remote</c>), one ref fetched at
        /// its tip (<c>fetch refs/tags/v1</c>), every branch and tag fetched (<c>fetch every ref</c>), or objects fetched by
        /// their ids (<c>fetch by id</c>).
        /// </summary>
        public IReadOnlyList<string> Asked => [.. Runs.Select(run => Described(run.Arguments))];

        /// <summary>Forgets the calls made so far.</summary>
        public void Clear() => _seenFrom = Git.RemoteRuns.Count;

        /// <summary>The address <paramref name="source"/>'s repository is reached at.</summary>
        public string AddressOf(WorkflowRemoteSource source)
        {
            if (Offline)
            {
                return $"https://127.0.0.1:1/{source.Owner}/{source.Repo}.git";
            }

            var path = System.IO.Path.GetFullPath(PathOf(new Repo(source.Host, source.Owner, source.Repo))).Replace('\\', '/');

            return path.StartsWith('/') ? $"file://{path}" : $"file:///{path}";
        }

        /// <summary>A commit of the fixture's repository holding <paramref name="files"/> under the workflow's directory.</summary>
        public Task<string> CommitAsync(params (string Path, string Content)[] files)
            => CommitAsync(Shared, [.. files.Select(file => Tracked.Text(file.Path, file.Content))]);

        /// <summary>A commit of the fixture's repository holding <paramref name="files"/>, as bytes, under the workflow's directory.</summary>
        public Task<string> CommitAsync(params (string Path, byte[] Content)[] files)
            => CommitAsync(Shared, [.. files.Select(file => Tracked.Bytes(file.Path, file.Content))]);

        /// <summary>A commit of the fixture's repository holding <paramref name="files"/>, its parent <paramref name="parent"/> where one is given.</summary>
        public Task<string> CommitAsync(IEnumerable<Tracked> files, string? parent = null) => CommitAsync(Shared, files, parent);

        /// <summary>
        /// A commit of <paramref name="repo"/> holding a README at its root and <paramref name="files"/> under the
        /// workflow's directory, its parent <paramref name="parent"/> where one is given: made by git's own importer,
        /// which writes any path a tree may hold, and held by no ref.
        /// </summary>
        public async Task<string> CommitAsync(Repo repo, IEnumerable<Tracked> files, string? parent = null)
        {
            var path = await RepositoryAsync(repo);
            var held = files.ToList();

            // The importer writes no path with '..' in it, and a tree may hold one - the way out of a directory a
            // file system reads - so a commit holding such a path has its trees written by hand instead.
            if (held.Any(file => file.Path.Split('/', '\\').Contains("..")))
            {
                return await CommittedByHandAsync(path, held, parent);
            }

            var made = ++_commits;
            var scratch = $"refs/fixture/{made.ToString(CultureInfo.InvariantCulture)}";
            using var stream = new MemoryStream();

            void Line(string text) => stream.Write(Encoding.UTF8.GetBytes(text + "\n"));

            void Data(byte[] bytes)
            {
                Line($"data {bytes.Length.ToString(CultureInfo.InvariantCulture)}");
                stream.Write(bytes);
                Line(string.Empty);
            }

            Line($"commit {scratch}");
            Line($"committer Test <test@example.com> {(1_700_000_000 + made).ToString(CultureInfo.InvariantCulture)} +0000");
            Data(Encoding.UTF8.GetBytes("c"));

            if (parent is not null)
            {
                Line($"from {parent}");
            }

            Line("M 100644 inline README.md");
            Data(Encoding.UTF8.GetBytes("readme\n"));

            foreach (var file in held)
            {
                if (file.Object is { } named)
                {
                    Line($"M {file.Mode} {named} {Directory}/{file.Path}");
                }
                else
                {
                    Line($"M {file.Mode} inline {Directory}/{file.Path}");
                    Data(file.Content!);
                }
            }

            Line("done");
            _ = await GitAsync(path, ["fast-import", "--quiet", "--done"], stream.ToArray());

            var commit = await GitAsync(path, ["rev-parse", scratch]);
            _ = await GitAsync(path, ["update-ref", "-d", scratch]);

            return commit;
        }

        /// <summary>
        /// A commit of the repository at <paramref name="path"/> holding a README at its root and <paramref name="files"/>
        /// under the workflow's directory, its every tree written by hand as git stores one - each blob hashed, each tree
        /// written by its entries, the commit made of the root tree - and held by no ref.
        /// </summary>
        private async Task<string> CommittedByHandAsync(string path, IReadOnlyList<Tracked> files, string? parent)
        {
            var entries = new List<(string Tail, string Mode, string Sha)>();

            foreach (var file in files)
            {
                entries.Add(($"{Directory}/{file.Path}", file.Mode, file.Object ?? await GitAsync(path, ["hash-object", "-w", "--stdin"], file.Content!)));
            }

            var readme = await GitAsync(path, ["hash-object", "-w", "--stdin"], Encoding.UTF8.GetBytes("readme\n"));
            var root = await TreeWrittenAsync(path, [new("README.md", "100644", readme), .. entries.Select(entry => (entry.Tail, entry.Mode, entry.Sha))]);

            var commitTree = new List<string> { "-c", "user.name=Test", "-c", "user.email=test@example.com", "commit-tree", root };

            if (parent is not null)
            {
                commitTree.AddRange(["-p", parent]);
            }

            commitTree.AddRange(["-m", "c"]);

            return await GitAsync(path, commitTree);
        }

        /// <summary>
        /// The tree of <paramref name="entries"/>, each by its whole path from the root, written as git stores a tree:
        /// the files a first part names, and a directory of the paths under it, its entries sorted as git sorts one.
        /// </summary>
        private async Task<string> TreeWrittenAsync(string path, IReadOnlyList<(string Tail, string Mode, string Sha)> entries)
        {
            var files = new List<(string Name, string Mode, string Sha, bool IsTree)>();
            var below = new Dictionary<string, List<(string Tail, string Mode, string Sha)>>(StringComparer.Ordinal);

            foreach (var (tail, mode, sha) in entries)
            {
                var slash = tail.IndexOf('/', StringComparison.Ordinal);

                if (slash < 0)
                {
                    files.Add((tail, mode, sha, IsTree: false));
                    continue;
                }

                var name = tail[..slash];

                if (!below.TryGetValue(name, out var heldUnder))
                {
                    below[name] = heldUnder = [];
                }

                heldUnder.Add((tail[(slash + 1)..], mode, sha));
            }

            foreach (var directory in below)
            {
                files.Add((directory.Key, "40000", await TreeWrittenAsync(path, directory.Value), IsTree: true));
            }

            using var tree = new MemoryStream();

            foreach (var entry in files.OrderBy(entry => entry.Name + (entry.IsTree ? "/" : string.Empty), StringComparer.Ordinal))
            {
                // "<mode> SP <name> NUL <20 raw bytes of the id>": a tree stores no type word, and the id raw.
                tree.Write(Encoding.UTF8.GetBytes($"{entry.Mode} {entry.Name}"));
                tree.WriteByte(0);
                tree.Write(Convert.FromHexString(entry.Sha));
            }

            return await GitAsync(path, ["hash-object", "-t", "tree", "-w", "--stdin"], tree.ToArray());
        }

        /// <summary>Puts the lightweight tag <paramref name="name"/> on <paramref name="target"/>, over any it had.</summary>
        public Task TagAsync(string name, string target, Repo? repo = null) => RefsAsync([($"refs/tags/{name}", target)], repo);

        /// <summary>Puts the branch <paramref name="name"/> on <paramref name="target"/>, over any it had.</summary>
        public Task BranchAsync(string name, string target) => RefsAsync([($"refs/heads/{name}", target)]);

        /// <summary>Puts every ref of <paramref name="refs"/> on its object, in one call.</summary>
        public async Task RefsAsync(IEnumerable<(string Name, string Target)> refs, Repo? repo = null)
        {
            var path = await RepositoryAsync(repo ?? Shared);
            _ = await GitAsync(path, ["update-ref", "--stdin"], Encoding.UTF8.GetBytes(string.Concat(refs.Select(named => $"update {named.Name} {named.Target}\n"))));
        }

        /// <summary>Deletes the tag <paramref name="name"/> of the fixture's repository.</summary>
        public async Task DeleteTagAsync(string name)
        {
            var path = await RepositoryAsync(Shared);
            _ = await GitAsync(path, ["update-ref", "-d", $"refs/tags/{name}"]);
        }

        /// <summary>
        /// An annotated tag object named <paramref name="name"/> of <paramref name="target"/>, whose type is
        /// <paramref name="type"/>, put on the tag ref of that name: the tag object's id.
        /// </summary>
        public async Task<string> AnnotatedTagAsync(string name, string target, string type)
        {
            var path = await RepositoryAsync(Shared);
            var tag = await GitAsync(path, ["mktag"], Encoding.UTF8.GetBytes($"object {target}\ntype {type}\ntag {name}\ntagger Test <test@example.com> 1700000000 +0000\n\n{name}\n"));
            await RefsAsync([($"refs/tags/{name}", tag)]);

            return tag;
        }

        /// <summary>The tree of <paramref name="commit"/> of the fixture's repository.</summary>
        public async Task<string> TreeOfAsync(string commit)
        {
            var path = await RepositoryAsync(Shared);

            return await GitAsync(path, ["rev-parse", $"{commit}^{{tree}}"]);
        }

        /// <summary>
        /// What one call that reached a remote asked, from its arguments: the refs listed, one ref fetched at its tip,
        /// every branch and tag fetched, or objects fetched by their ids.
        /// </summary>
        private static string Described(IReadOnlyList<string> arguments)
        {
            if (arguments.First(argument => argument is "ls-remote" or "fetch") == "ls-remote")
            {
                return "ls-remote";
            }

            if (arguments.Contains("--stdin"))
            {
                return "fetch by id";
            }

            var refspec = arguments[^1];

            return arguments.Contains("--depth=1") ? $"fetch {refspec[1..refspec.IndexOf(':', StringComparison.Ordinal)]}" : "fetch every ref";
        }

        /// <summary>The bare repository <paramref name="repo"/> is served from, made the first time it is asked for.</summary>
        private async Task<string> RepositoryAsync(Repo repo)
        {
            var path = PathOf(repo);

            if (_made.Add(path))
            {
                System.IO.Directory.CreateDirectory(path);
                _ = await GitAsync(path, ["init", "--bare", "--quiet"]);
                _ = await GitAsync(path, ["config", "uploadpack.allowFilter", "true"]);
                _ = await GitAsync(path, ["config", "uploadpack.allowAnySHA1InWant", "true"]);

                // A tree holds what a forge's may, on any machine: two names case alone tells apart stay two - the
                // importer folds them into one where the repository says case is ignored, as git sets it on a file
                // system that folds case - and a name no checkout here could write is still a name.
                _ = await GitAsync(path, ["config", "core.ignorecase", "false"]);
                _ = await GitAsync(path, ["config", "core.protectNTFS", "false"]);
            }

            return path;
        }

        private string PathOf(Repo repo) => System.IO.Path.Combine(_root, repo.Host, repo.Owner, $"{repo.Name}.git");

        /// <summary>Runs git in <paramref name="directory"/>, <paramref name="input"/> on its standard input, failing the test where git fails: what it printed, trimmed.</summary>
        private static async Task<string> GitAsync(string directory, IReadOnlyList<string> arguments, byte[]? input = null)
        {
            var start = new ProcessStartInfo("git")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(directory);

            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            // Nothing of the process running the tests steers which repository git works in.
            foreach (var inherited in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT" })
            {
                _ = start.Environment.Remove(inherited);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(Token);
            var error = process.StandardError.ReadToEndAsync(Token);

            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, Token);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(Token);

            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed ({process.ExitCode.ToString(CultureInfo.InvariantCulture)}): {await error}");

            return (await output).Trim();
        }
    }

    /// <summary>
    /// A file system over which the cache at <paramref name="root"/> is pruned whole the moment a fetch has written the
    /// last record of it - its resolution - as another process's pruning would between the fetch and whatever reads it next.
    /// </summary>
    private sealed class PrunedAfterTheFetch(IFileSystem inner, string root) : PassThroughFileSystem(inner)
    {
        /// <summary>Whether the cache was pruned.</summary>
        public bool Pruned { get; private set; }

        public override void ProtectSecretFile(string path)
        {
            base.ProtectSecretFile(path);

            if (!Pruned && path.StartsWith(Path.Combine(root, "resolutions"), StringComparison.Ordinal))
            {
                base.DeleteDirectory(root);
                Pruned = true;
            }
        }
    }

    /// <summary>
    /// A file system on which another process does <paramref name="meanwhile"/> once, the moment the first path
    /// <paramref name="at"/> picks has been read, listed or written - what the reader read, listed or wrote standing as it
    /// was - as another process would between one step of the reader's and the next.
    /// </summary>
    private sealed class Meanwhile(IFileSystem inner, Func<string, string, bool> at, Action meanwhile) : PassThroughFileSystem(inner)
    {
        /// <summary>A file read.</summary>
        public const string Read = "read";

        /// <summary>A directory's files listed.</summary>
        public const string Listed = "listed";

        /// <summary>A file written, private to this user.</summary>
        public const string Written = "written";

        /// <summary>Whether the other process acted.</summary>
        public bool Happened { get; private set; }

        public override Stream OpenRead(string path)
        {
            if (!Due(Read, path))
            {
                return base.OpenRead(path);
            }

            var read = new MemoryStream();

            using (var stream = base.OpenRead(path))
            {
                stream.CopyTo(read);
            }

            read.Position = 0;
            Happen();

            return read;
        }

        public override IEnumerable<string> EnumerateFiles(string path, bool recursive)
        {
            var listed = base.EnumerateFiles(path, recursive).ToList();

            if (Due(Listed, path))
            {
                Happen();
            }

            return listed;
        }

        public override void ProtectSecretFile(string path)
        {
            base.ProtectSecretFile(path);

            if (Due(Written, path))
            {
                Happen();
            }
        }

        private bool Due(string done, string path) => !Happened && at(done, path);

        private void Happen()
        {
            Happened = true;
            meanwhile();
        }
    }

    /// <summary>A file system on which nothing under <paramref name="root"/> can be written, as a full disk or a directory nobody may write.</summary>
    private sealed class Unwritable(IFileSystem inner, string root) : PassThroughFileSystem(inner)
    {
        public override void WriteAtomic(string path, Action<Stream> write)
        {
            if (path.StartsWith(root, StringComparison.Ordinal))
            {
                throw new IOException("There is not enough space on the disk.");
            }

            base.WriteAtomic(path, write);
        }
    }

    /// <summary>A runner whose pause step asks, and whose every other step passes.</summary>
    private sealed class Pausing : IFlowStepRunner
    {
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult(run.Step.Type == "pause"
                ? FlowStepResult.Asking(new FlowQuestion("asked", "pause", run.Instance, "go on?", ["stay", "go"]))
                : FlowStepResult.Ended(passed: true, 0));

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>A file system that keeps every blob of the cache's: deleting one fails, as a file held open would.</summary>
    private sealed class UndeletableBlobs(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override void DeleteFile(string path)
        {
            if (WorkflowCache.BlobOf(path) is not null)
            {
                throw new IOException("this file system keeps its blobs.");
            }

            base.DeleteFile(path);
        }
    }
}
