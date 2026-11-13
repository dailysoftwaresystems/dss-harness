using System.Security.Cryptography;
using System.Text;
using DssHarness.Core.Agents;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// A workflow used as a step from beyond the repository: the source's spelling, the fetch through the forge's tool -
/// a ref resolved to a commit of the repository's own, the commit's tree walked to the workflow's directory, every path
/// under it held to the tree's rule and every blob checked against the hash the tree gives - the hash cache it is kept
/// in as bytes, read again as it is read, the trust a configuration alone grants and the one credential a host is
/// given, the offline fallback, an agent's command that reads the cache alone, the pruning, and the export that vendors
/// the whole for review.
/// </summary>
public sealed class RemoteWorkflowUseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Moved = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string TagObject = "cccccccccccccccccccccccccccccccccccccccc";
    private const string Repository = "repos/dailysoftwaresystems/shared";
    private const string Source = "dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1";
    private const string Directory = "workflows/security-review";
    private const string Yml = "workflows/security-review/security-review.yml";
    private const string PromptPath = "workflows/security-review/prompts/review.md";
    private const string GhesSource = "ghe.acme.internal/acme/shared/workflows/security-review/security-review.yml@v1";

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

    /// <summary>What gh says where the network is not there: a name that does not resolve.</summary>
    private const string Unreachable = "error connecting to api.github.com\ncheck your internet connection or https://githubstatus.com\n";

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

        static (string, string, string, string, string)? AsParts(string source)
            => WorkflowRemote.Source(source) is { } parsed ? (parsed.Host, parsed.Owner, parsed.Repo, parsed.File, parsed.Ref) : null;
    }

    /// <summary>
    /// A ref git would not take is refused by git's own rules before anything is asked of the forge: the ref is put into
    /// the forge's addresses as it is spelled, and one that reads as part of the address could reach another ref.
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
        var (forge, _, fetcher, _) = Fixture(temp);
        var source = new WorkflowRemoteSource("github.com", "dailysoftwaresystems", "shared", Yml, @ref);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(source, Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"'{source.Spelled}' names '{@ref}' is no ref git takes: {why}; nothing was asked of the forge", refused.Message);
        Assert.Empty(forge.Requests);
        Assert.Null(WorkflowRemote.RefProblem("v1.4.0"));
        Assert.Null(WorkflowRemote.RefProblem("release/2026-11"));
    }

    /// <summary>
    /// A tag is resolved to its commit through the repository's own ref, the commit's tree walked to the workflow's
    /// directory a part at a time, the directory listed whole and every blob under it fetched and hashed as git hashes
    /// one - each call naming its host - the whole kept in the cache, blobs by their git hash and the entry by its
    /// repository, commit and path, the resolution recorded with its time; the second resolution of the same tag asks
    /// the tag alone.
    /// </summary>
    [Fact]
    public async Task ATagIsResolved_ItsDirectoryWalkedFetchedAndHashChecked_KeptInTheCache()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var (forge, cache, fetcher, warned) = Fixture(temp, clock);
        Holds(forge, Commit, ("security-review.yml", Definition), ("prompts/review.md", Prompt));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal((Commit, Definition, false, false), (resolved.Commit, resolved.Definition, resolved.Moving, resolved.FromLastResolved));
        Assert.Equal(
            [
                ["api", "--hostname", "github.com", $"{Repository}/git/ref/tags/v1"],
                ["api", "--hostname", "github.com", $"{Repository}/git/commits/{Commit}"],
                ["api", "--hostname", "github.com", $"{Repository}/git/trees/{Tree(Commit, 0)}"],
                ["api", "--hostname", "github.com", $"{Repository}/git/trees/{Tree(Commit, 1)}"],
                ["api", "--hostname", "github.com", $"{Repository}/git/trees/{Tree(Commit, 2)}?recursive=1"],
                ["api", "--hostname", "github.com", $"{Repository}/git/blobs/{WorkflowCache.BlobSha(Definition)}"],
                ["api", "--hostname", "github.com", $"{Repository}/git/blobs/{WorkflowCache.BlobSha(Prompt)}"],
            ],
            forge.Requests.Select(request => request.Arguments.ToArray()));

        var listed = Assert.Single(cache.List().Entries);

        Assert.Equal(("github.com", "dailysoftwaresystems", "shared", Commit, Directory), (listed.Entry.Host, listed.Entry.Owner, listed.Entry.Repo, listed.Entry.Commit, listed.Entry.Path));
        Assert.Equal([PromptPath, Yml], [.. listed.Entry.Files.Select(file => file.Path).Order(StringComparer.Ordinal)]);
        Assert.Equal(
            new WorkflowCacheResolution("github.com", "dailysoftwaresystems", "shared", Directory, "v1", Commit, Branch: false, clock.GetUtcNow()),
            cache.LastResolved("github.com", "dailysoftwaresystems", "shared", Directory, "v1"));
        Assert.Equal(
            $"github.com/dailysoftwaresystems/shared at {Commit[..12]}: {Directory} (2 file(s), {(Encoding.UTF8.GetByteCount(Definition) + Encoding.UTF8.GetByteCount(Prompt)).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes, fetched 2026-09-30, last used 2026-09-30, the newest resolution of the tag v1 on 2026-09-30)",
            WorkflowCacheService.EntryLine(listed));
        Assert.Empty(warned);

        // The same tag again: one call re-resolves it - a tag needs that, at every run's start - and the cache answers
        // the commit it holds, with no tree or blob asked for.
        forge.Requests.Clear();
        var again = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal((Commit, Definition, false, false), (again.Commit, again.Definition, again.Moving, again.FromLastResolved));
        Assert.Equal([$"{Repository}/git/ref/tags/v1"], forge.Asked);
    }

    /// <summary>
    /// An annotated tag's ref names its tag object, never the commit: it is peeled through the tag object to the commit,
    /// and that commit - a real one - is what is recorded, named and cached.
    /// </summary>
    [Fact]
    public async Task AnAnnotatedTag_IsPeeledToTheCommitItNames()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = Json($$$"""{"ref":"refs/tags/v1","object":{"sha":"{{{TagObject}}}","type":"tag"}}""");
        forge.Answers[$"{Repository}/git/tags/{TagObject}"] = Json($$$"""{"sha":"{{{TagObject}}}","tag":"v1","object":{"sha":"{{{Commit}}}","type":"commit"}}""");

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal(Commit, resolved.Commit);
        Assert.Equal(Commit, Assert.Single(cache.List().Entries).Entry.Commit);
        Assert.Equal([$"{Repository}/git/ref/tags/v1", $"{Repository}/git/tags/{TagObject}", $"{Repository}/git/commits/{Commit}"], forge.Asked.Take(3));

        // A tag of a tree is no commit a workflow is read at.
        forge.Answers[$"{Repository}/git/tags/{TagObject}"] = Json($$$"""{"sha":"{{{TagObject}}}","object":{"sha":"{{{Moved}}}","type":"tree"}}""");
        var tree = await Assert.ThrowsAsync<HarnessException>(() => Over(cache, forge).ResolveAsync(Parsed() with { Ref = "v2" }, Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, tree.ExitCode);
    }

    /// <summary>
    /// A blob that does not hash to what the commit's tree says of it is refused, and nothing is cached: what a run
    /// would run must be what the repository it came from says it is.
    /// </summary>
    [Fact]
    public async Task ABlobThatDoesNotHashToWhatTheTreeSays_IsRefused_AndNothingIsCached()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        forge.Answers[$"{Repository}/git/blobs/{WorkflowCache.BlobSha(Definition)}"] = Blob(Encoding.UTF8.GetBytes("something else entirely\n"));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}': the blob '{WorkflowCache.BlobSha(Definition)}' of '{Yml}' does not hash to what the commit's tree says of it; nothing was cached",
            refused.Message);
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
        Assert.False(System.IO.Directory.Exists(cache.Directory));
    }

    /// <summary>A directory past the fetch's bound of files is refused with the count, and no blob of it is fetched.</summary>
    [Fact]
    public async Task ADirectoryPastTheBoundOfFiles_IsRefusedWithTheCount_AndNoBlobIsFetched()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, [("security-review.yml", Definition), .. Enumerable.Range(0, WorkflowCache.MostFiles).Select(at => ($"f{at}.md", "x"))]);
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"'{Source}' resolved to a directory of 2001 files, past the fetch's bound of 2000; nothing was fetched", refused.Message);
        Assert.DoesNotContain(forge.Asked, asked => asked.Contains("/git/blobs/", StringComparison.Ordinal));
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>A directory past the fetch's bound of bytes is refused with the size the tree gives, and no blob of it is fetched.</summary>
    [Fact]
    public async Task ADirectoryPastTheBoundOfBytes_IsRefusedWithTheSize_AndNoBlobIsFetched()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        // The tree says the one file is a byte past the bound.
        forge.Answers[$"{Repository}/git/trees/{Tree(Commit, 2)}?recursive=1"] = Listing(("security-review.yml", "blob", "100644", WorkflowCache.BlobSha(Definition), WorkflowCache.MostBytes + 1));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"'{Source}' resolved to a directory of 20971521 bytes, past the fetch's bound of 20971520; nothing was fetched", refused.Message);
        Assert.DoesNotContain(forge.Asked, asked => asked.Contains("/git/blobs/", StringComparison.Ordinal));
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
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
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-reviews.yml", Definition), ("prompts/review.md", Prompt));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names '{Yml}', which the directory '{Directory}' does not hold at the commit '{Commit}': its workflow files are 'workflows/security-review/security-reviews.yml'; nothing was fetched",
            refused.Message);
        Assert.DoesNotContain(forge.Asked, asked => asked.Contains("/git/blobs/", StringComparison.Ordinal));
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>
    /// A listing of the directory the forge cut short is refused, and nothing is cached: a part of a directory read as
    /// the whole would have a prompt the cut took read as one the workflow does not hold.
    /// </summary>
    [Fact]
    public async Task ATreeListingCutShort_IsRefused_AndNothingIsCached()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        forge.Answers[$"{Repository}/git/trees/{Tree(Commit, 2)}?recursive=1"] = Listing(truncated: true, ("security-review.yml", "blob", "100644", WorkflowCache.BlobSha(Definition), 10));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}': the forge listed the directory '{Directory}' cut short, past what one answer of it holds; a part of a directory is not fetched as the directory. Nothing was fetched",
            refused.Message);
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
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition), (path, Prompt));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"'{Source}' resolved to the commit '{Commit}', whose directory '{Directory}' holds a path no workflow's may: {problem}. Nothing was fetched", refused.Message);
        Assert.DoesNotContain(forge.Asked, asked => asked.Contains("/git/blobs/", StringComparison.Ordinal));
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
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        forge.Answers[$"{Repository}/git/trees/{Tree(Commit, 2)}?recursive=1"] = Listing(
            ("security-review.yml", "blob", "100644", WorkflowCache.BlobSha(Definition), 10),
            ("prompts", "blob", "120000", WorkflowCache.BlobSha("../shared-prompts"), 17));

        var link = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(
            $"'{Source}' resolved to the commit '{Commit}', whose '{Directory}/prompts' is a symbolic link: a workflow's files are its directory's own, and a link is followed nowhere. Nothing was fetched",
            link.Message);

        forge.Answers[$"{Repository}/git/trees/{Tree(Commit, 2)}?recursive=1"] = Listing(
            ("security-review.yml", "blob", "100644", WorkflowCache.BlobSha(Definition), 10),
            ("vendor", "commit", "160000", Moved, null));

        var submodule = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(
            $"'{Source}' resolved to the commit '{Commit}', whose '{Directory}/vendor' is a submodule: a workflow's files are its directory's own, and another repository's are not fetched with them. Nothing was fetched",
            submodule.Message);
        Assert.Equal((HarnessExit.Refused, HarnessExit.Refused), (link.ExitCode, submodule.ExitCode));
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>
    /// An answer of the forge's that does not read as what was asked - no JSON, a member missing or of the wrong kind,
    /// base64 that is none, a hash that is no hash - is the forge's failure, said as one naming what was asked
    /// (<see cref="HarnessExit.CommandFailed"/>), never an exception of the runtime's that ends the command as a defect;
    /// and nothing is cached.
    /// </summary>
    [Theory]
    [InlineData("commits", "not json at all", "the commit 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("commits", """{"sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""", "the commit 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("commits", """{"sha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","tree":{"sha":"1111111111111111111111111111111111111111"}}""", "the commit 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("listing", """{"tree":[{"path":"security-review.yml","type":"blob","mode":"100644","sha":"short","size":3}]}""", "the directory 'workflows/security-review'")]
    [InlineData("listing", """{"tree":[{"path":7,"type":"blob","mode":"100644","sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":3}]}""", "the directory 'workflows/security-review'")]
    [InlineData("listing", """{"tree":[{"path":"security-review.yml","type":"blob","mode":"100644","sha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":"many"}]}""", "the directory 'workflows/security-review'")]
    [InlineData("blob", """{"content":"!!! not base64 !!!","encoding":"base64"}""", "the blob")]
    [InlineData("blob", """{"content":"eA==","encoding":"rot13"}""", "the blob")]
    public async Task AnAnswerThatDoesNotRead_IsRefusedAsTheForgesFailure_NeverAnExceptionOfTheRuntimes(string which, string answer, string what)
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        forge.Answers[which switch
        {
            "commits" => $"{Repository}/git/commits/{Commit}",
            "listing" => $"{Repository}/git/trees/{Tree(Commit, 2)}?recursive=1",
            _ => $"{Repository}/git/blobs/{WorkflowCache.BlobSha(Definition)}",
        }] = Json(answer);

        var failed = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.CommandFailed, failed.ExitCode);
        Assert.StartsWith($"'{Source}' could not be resolved: the forge answered {what}", failed.Message, StringComparison.Ordinal);
        Assert.EndsWith("; nothing was cached", failed.Message, StringComparison.Ordinal);
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>
    /// A repository the configuration does not allow is refused before anything is asked of the forge, and an allow
    /// list names an owner whole, its every repository, or one repository - dotted, as GitHub's may be - another
    /// host's under its own name alone.
    /// </summary>
    [Fact]
    public async Task ARepositoryTheConfigurationDoesNotAllow_IsRefusedBeforeAnythingIsAsked()
    {
        using var temp = new TempDirectory();
        var (forge, _, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), null, temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names the repository 'dailysoftwaresystems/shared', which workflowSources.allow does not allow; a remote workflow directs agents and machine steps in this repository, so it is trusted by configuration alone; the allow list is empty, so no remote source is allowed until the repository allows its owner",
            refused.Message);
        Assert.Empty(forge.Requests);

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal(Commit, resolved.Commit);

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
    /// name, asked once the tag's own ref answered nothing and the repository answered it is there.
    /// </summary>
    [Fact]
    public async Task ThePinHoldsTheRefToATagOrACommit_ABranchOnlyWhereItSaysAny()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, warned) = Fixture(temp);
        forge.Answers[Repository] = Json("""{"default_branch":"main"}""");

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names the ref 'v1', which is no tag of 'dailysoftwaresystems/shared'; workflowSources.pin is tag-or-commit, which takes a tag or a full commit SHA - a branch moves, and what a run ran must be what it said",
            refused.Message);
        Assert.Equal([$"{Repository}/git/ref/tags/v1", Repository], forge.Asked);

        forge.Requests.Clear();
        var commit = await Assert.ThrowsAsync<HarnessException>(
            () => fetcher.ResolveAsync(Parsed(), new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "commit" }, temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(
            $"'{Source}' names the ref 'v1', which is no full commit SHA; workflowSources.pin is commit, which takes a full commit SHA only - a tag or a branch moves, and a commit is what it is",
            commit.Message);
        Assert.Empty(forge.Requests);

        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/heads/v1"] = Json($$$"""{"ref":"refs/heads/v1","object":{"sha":"{{{Commit}}}","type":"commit"}}""");

        var any = await fetcher.ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "any" },
            temp.Path,
            NoVariables,
            recorded: null,
            Token);

        Assert.Equal((Commit, true), (any.Commit, any.Moving));
        Assert.True(Assert.Single(cache.List().Entries).Resolutions.Single().Branch);
        Assert.Equal(
            [$"{WorkflowRemoteFetcher.Said}: '{Source}' names the branch 'v1', which moves: it resolved to the commit {Commit} now, and may resolve to another at the next run - pin it by a tag or a commit where what a run ran must be what it said"],
            warned);

        // A ref that is neither a tag nor a branch of the repository.
        var neither = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(
            Parsed() with { Ref = "nowhere" },
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "any" },
            temp.Path,
            NoVariables,
            recorded: null,
            Token));

        Assert.Equal("'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@nowhere' names the ref 'nowhere', which is neither a tag nor a branch of 'dailysoftwaresystems/shared'; nothing was cached", neither.Message);
    }

    /// <summary>
    /// A repository that is not there, or that the credential cannot see, answers a tag's ref as a tag that is not there
    /// does: the repository is asked, and the refusal names it - never "no tag of it", and never the network not being there.
    /// </summary>
    [Fact]
    public async Task AMissingRepository_IsRefusedAsOne_NeverAsAMissingTag()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved", Pin = "any" },
            temp.Path,
            NoVariables,
            recorded: null,
            Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names the repository 'dailysoftwaresystems/shared' on 'github.com', which the forge answers is not there: it is misspelt, or the credential gh reaches 'github.com' with cannot see it; nothing was cached",
            refused.Message);
        Assert.Equal([$"{Repository}/git/ref/tags/v1", Repository], forge.Asked);
        Assert.Equal(new WorkflowCacheListing([], []), Listed(cache));
    }

    /// <summary>
    /// A commit named by its SHA - the strictest pin's only form - is fetched once the repository's default branch is
    /// found to reach it, with no tag asked for; and a SHA GitHub serves from a fork of the repository, which no branch or
    /// tag of the repository reaches, is refused as the imposter it is, with nothing cached.
    /// </summary>
    [Fact]
    public async Task ACommitNamedByItsSha_IsFetchedOnlyWhereABranchOrTagOfTheRepositoryReachesIt()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        var pinned = Parsed() with { Ref = Commit };
        var commitOnly = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "commit" };
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[Repository] = Json("""{"default_branch":"main"}""");
        forge.Answers[$"{Repository}/compare/main...{Commit}"] = Exited(0, "behind\n");

        var resolved = await fetcher.ResolveAsync(pinned, commitOnly, temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal(Commit, resolved.Commit);
        Assert.Equal([Repository, $"{Repository}/compare/main...{Commit}", $"{Repository}/git/commits/{Commit}"], forge.Asked.Take(3));
        Assert.DoesNotContain(forge.Asked, asked => asked.Contains("/git/ref/", StringComparison.Ordinal));

        // A commit of a fork: the default branch diverges from it, and no tag or branch the forge lists holds or reaches it.
        Holds(forge, Moved, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/compare/main...{Moved}"] = Exited(0, "diverged\n");
        forge.Answers[$"{Repository}/tags?per_page=100"] = Exited(0, $"v1 {Commit}\n");
        forge.Answers[$"{Repository}/branches?per_page=100"] = Exited(0, $"main {Commit}\nfeature/x {Commit}\n");
        forge.Answers[$"{Repository}/compare/v1...{Moved}"] = Exited(0, "ahead\n");
        forge.Answers[$"{Repository}/compare/feature/x...{Moved}"] = Exited(0, "diverged\n");
        forge.Requests.Clear();

        var imposter = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(pinned with { Ref = Moved }, commitOnly, temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, imposter.ExitCode);
        Assert.Equal(
            $"'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@{Moved}' names the commit '{Moved}', which no branch or tag of 'dailysoftwaresystems/shared' reaches - its default branch 'main', and the first 20 of the tags and branches the forge lists, were compared: GitHub serves a commit of any fork of a repository under the repository's own name, and one that is not the repository's own is never run; name a tag of it, or a commit one of its branches or tags holds. Nothing was cached",
            imposter.Message);
        Assert.Equal(
            [Repository, $"{Repository}/compare/main...{Moved}", $"{Repository}/tags?per_page=100", $"{Repository}/branches?per_page=100", $"{Repository}/compare/v1...{Moved}", $"{Repository}/compare/feature/x...{Moved}"],
            forge.Asked);
        Assert.Equal([Commit], cache.List().Entries.Select(listed => listed.Entry.Commit));
    }

    /// <summary>
    /// Where the forge cannot be reached, the run is refused naming what gh said - unless the configuration says
    /// last-resolved, and then the cache's newest resolution of the ref stands in, warned of.
    /// </summary>
    [Fact]
    public async Task OfflineTheRunIsRefused_UnlessLastResolvedStandsIn()
    {
        using var temp = new TempDirectory();
        var (forge, cache, online, warned) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition), ("prompts/review.md", Prompt));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        _ = await online.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        // The network is not there any more: the tag cannot be resolved, and the refusal says what gh said and what the
        // configuration would have stand in.
        forge.Always = Exited(1, stderr: Unreachable);
        var refused = await Assert.ThrowsAsync<HarnessException>(() => online.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' could not be resolved, the forge not being reached: gh api --hostname github.com {Repository}/git/ref/tags/v1 failed, so '{Source}' was not resolved: error connecting to api.github.com\ncheck your internet connection or https://githubstatus.com; workflowSources.offline is refuse, so nothing stands in - last-resolved would stand at the cache's newest resolution of the ref",
            refused.Message);

        var stood = await online.ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" },
            temp.Path,
            NoVariables,
            recorded: null,
            Token);

        Assert.Equal((Commit, Definition, true, false), (stood.Commit, stood.Definition, stood.FromLastResolved, stood.Moving));
        Assert.Equal(
            $"{WorkflowRemoteFetcher.Said}: '{Source}' could not be resolved, the forge not being reached: gh api --hostname github.com {Repository}/git/ref/tags/v1 failed, so '{Source}' was not resolved: error connecting to api.github.com\ncheck your internet connection or https://githubstatus.com; the workflow cache's newest resolution of 'v1' stands in, the commit {Commit} it resolved to on 2026-09-30, as workflowSources.offline says last-resolved",
            Assert.Single(warned));
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
        var (forge, cache, fetcher, _) = Fixture(temp, clock);
        var lastResolved = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" };
        Holds(forge, Commit, ("security-review.yml", Definition));
        Holds(forge, Moved, ("security-review.yml", "name: security-review\nsteps: []\n"));

        // v1 resolves to the first commit, then - moved - to the second, a day later.
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, temp.Path, NoVariables, recorded: null, Token);
        clock.Advance(TimeSpan.FromDays(1));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Moved);
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, temp.Path, NoVariables, recorded: null, Token);

        forge.Always = Exited(1, stderr: Unreachable);
        Assert.Equal(Moved, (await fetcher.ResolveAsync(Parsed(), lastResolved, temp.Path, NoVariables, recorded: null, Token)).Commit);

        // Moved back to the first commit, which the cache holds: no fetch, and still the newest resolution.
        forge.Always = null;
        clock.Advance(TimeSpan.FromDays(1));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, temp.Path, NoVariables, recorded: null, Token);

        forge.Always = Exited(1, stderr: Unreachable);
        Assert.Equal(Commit, (await fetcher.ResolveAsync(Parsed(), lastResolved, temp.Path, NoVariables, recorded: null, Token)).Commit);
        Assert.Equal(clock.GetUtcNow(), cache.LastResolved("github.com", "dailysoftwaresystems", "shared", Directory, "v1")!.ResolvedAt);
    }

    /// <summary>
    /// A forge that answered - a credential refused, access forbidden, a tag not there, a login gh does not have - is
    /// never the network not being there: the run is refused naming what gh said, and the cache's last resolution never
    /// stands in for it, however the configuration says last-resolved.
    /// </summary>
    [Theory]
    [InlineData("gh: Bad credentials (HTTP 401)")]
    [InlineData("gh: Resource protected by organization SAML enforcement. You must grant your Personal Access token access to this organization. (HTTP 403)")]
    [InlineData("To get started with GitHub CLI, please run:  gh auth login\nAlternatively, populate the GH_TOKEN environment variable with a GitHub API authentication token.")]
    public async Task AForgeThatAnswered_IsNeverTheNetworkNotBeingThere(string said)
    {
        using var temp = new TempDirectory();
        var (forge, _, fetcher, warned) = Fixture(temp);
        var lastResolved = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" };
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, temp.Path, NoVariables, recorded: null, Token);

        forge.Always = Exited(4, stderr: said + "\n");
        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), lastResolved, temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' could not be resolved, the forge refusing what was asked: gh api --hostname github.com {Repository}/git/ref/tags/v1 failed, so '{Source}' was not resolved: {said.TrimEnd('.')}. Nothing stands in for an answer the forge gave",
            refused.Message);
        Assert.Empty(warned);
    }

    /// <summary>
    /// Under last-resolved, a tag the forge answers is not there - deleted since, the repository there - is refused,
    /// never served from the cache's last resolution of it.
    /// </summary>
    [Fact]
    public async Task ATagTheForgeSaysIsNotThere_IsRefused_NeverServedFromTheCache()
    {
        using var temp = new TempDirectory();
        var (forge, _, fetcher, warned) = Fixture(temp);
        var lastResolved = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" };
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        forge.Answers[Repository] = Json("""{"default_branch":"main"}""");
        _ = await fetcher.ResolveAsync(Parsed(), lastResolved, temp.Path, NoVariables, recorded: null, Token);

        _ = forge.Answers.Remove($"{Repository}/git/ref/tags/v1");
        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), lastResolved, temp.Path, NoVariables, recorded: null, Token));

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
        var (forge, cache, fetcher, warned) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition), ("prompts/review.md", Prompt));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        var first = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);
        var blob = cache.BlobFile(first.Entry.Files.Single(file => file.Path == changed).Blob);
        await File.WriteAllTextAsync(blob, "changed on disk\n", Token);
        forge.Requests.Clear();

        var again = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal((Commit, Definition), (again.Commit, again.Definition));
        Assert.Contains(forge.Asked, asked => asked.EndsWith($"/git/blobs/{first.Entry.Files.Single(file => file.Path == changed).Blob}", StringComparison.Ordinal));
        Assert.Equal(Prompt, cache.ReadText(Assert.Single(cache.List().Entries).Entry, PromptPath));
        Assert.StartsWith($"{WorkflowRemoteFetcher.Said}: '{Source}': the workflow cache's entry for the commit {Commit} is not whole (the workflow cache's '{blob}', the file '{changed}' of the entry", Assert.Single(warned), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run's own recorded commit is used again however the ref has moved: the tag answers another commit now, and
    /// the resolution stands at the one the run began with, asking the forge nothing.
    /// </summary>
    [Fact]
    public async Task ARecordedCommitIsUsedAgain_HoweverTheRefHasMoved()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        // The tag has moved since the run began: it answers another commit now.
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Moved);
        forge.Requests.Clear();

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: Commit, Token);

        Assert.Equal(Commit, resolved.Commit);
        Assert.Empty(forge.Requests);

        // The cache pruned before the resume: the recorded commit's own tree is fetched, proved the repository's own as
        // any commit named by its SHA is, and the ref is never asked again.
        System.IO.Directory.Delete(cache.Directory, recursive: true);
        forge.Answers[Repository] = Json("""{"default_branch":"main"}""");
        forge.Answers[$"{Repository}/compare/main...{Commit}"] = Exited(0, "identical\n");

        var refetched = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: Commit, Token);

        Assert.Equal((Commit, Definition), (refetched.Commit, refetched.Definition));
        Assert.Equal([Repository, $"{Repository}/compare/main...{Commit}", $"{Repository}/git/commits/{Commit}"], forge.Asked.Take(3));
        Assert.DoesNotContain(forge.Asked, asked => asked.Contains("/git/ref/", StringComparison.Ordinal));
    }

    /// <summary>
    /// A GitHub Enterprise Server host's credential, named in the configuration, is handed to every call as the one
    /// variable gh reads for such a host - GH_ENTERPRISE_TOKEN - every other token variable and GH_HOST taken away, so no
    /// token of the machine's reaches a host a repository's configuration named; and it is written nowhere else.
    /// </summary>
    [Fact]
    public async Task AnEnterpriseServerHost_IsGivenItsCredentialAsTheEnterpriseToken_AndNoOtherToken()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        HoldsOn(forge, "repos/acme/shared", Commit, ("security-review.yml", Definition));
        forge.Answers["repos/acme/shared/git/ref/tags/v1"] = TagAt(Commit);

        var resolved = await fetcher.ResolveAsync(
            WorkflowRemote.Source(GhesSource)!,
            Hosted("ghe.acme.internal"),
            temp.Path,
            name => name == "GHE_TOKEN" ? "the-token" : null,
            recorded: null,
            Token);

        Assert.Equal(Commit, resolved.Commit);
        Assert.Equal(6, forge.Requests.Count);
        Assert.All(forge.Requests, request =>
        {
            Assert.Equal(["api", "--hostname", "ghe.acme.internal"], request.Arguments.Take(3).ToArray());
            Assert.Equal(
                [("GH_ENTERPRISE_TOKEN", "the-token"), ("GH_HOST", null), ("GH_TOKEN", null), ("GITHUB_ENTERPRISE_TOKEN", null), ("GITHUB_TOKEN", null)],
                TokenEnvironment(request));
            Assert.DoesNotContain("the-token", request.Arguments, StringComparer.Ordinal);
        });

        foreach (var file in System.IO.Directory.EnumerateFiles(cache.Directory, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("the-token", await File.ReadAllTextAsync(file, Token), StringComparison.Ordinal);
        }
    }

    /// <summary>A <c>*.ghe.com</c> host is given its credential as GH_TOKEN, the variable gh reads for it, and no other token.</summary>
    [Fact]
    public async Task AGheComHost_IsGivenItsCredentialAsGhToken_AndNoOtherToken()
    {
        using var temp = new TempDirectory();
        var (forge, _, fetcher, _) = Fixture(temp);
        HoldsOn(forge, "repos/acme/shared", Commit, ("security-review.yml", Definition));
        forge.Answers["repos/acme/shared/git/ref/tags/v1"] = TagAt(Commit);

        _ = await fetcher.ResolveAsync(
            WorkflowRemote.Source("acme.ghe.com/acme/shared/workflows/security-review/security-review.yml@v1")!,
            Hosted("acme.ghe.com"),
            temp.Path,
            name => name == "GHE_TOKEN" ? "the-token" : null,
            recorded: null,
            Token);

        Assert.All(forge.Requests, request => Assert.Equal(
            [("GH_ENTERPRISE_TOKEN", null), ("GH_HOST", null), ("GH_TOKEN", "the-token"), ("GITHUB_ENTERPRISE_TOKEN", null), ("GITHUB_TOKEN", null)],
            TokenEnvironment(request)));
    }

    /// <summary>
    /// A github.com call names its host, GH_HOST taken away, so a machine logged in to another host never answers it from
    /// there; it keeps the machine's own GH_TOKEN and login, and is given no enterprise token.
    /// </summary>
    [Fact]
    public async Task AGitHubComCall_NamesItsHost_AndIsGivenNoEnterpriseToken()
    {
        using var temp = new TempDirectory();
        var (forge, _, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.All(forge.Requests, request =>
        {
            Assert.Equal(["api", "--hostname", "github.com"], request.Arguments.Take(3).ToArray());
            Assert.Equal([("GH_ENTERPRISE_TOKEN", null), ("GH_HOST", null), ("GITHUB_ENTERPRISE_TOKEN", null)], TokenEnvironment(request));
        });
    }

    /// <summary>
    /// A host beyond github.com whose credential the configuration does not name, or whose variable this process does
    /// not hold, is refused before anything is asked of it: it is never reached with gh's own login or a machine token.
    /// </summary>
    [Fact]
    public async Task AHostWhoseCredentialIsNotThere_IsRefused_AndNothingIsAsked()
    {
        using var temp = new TempDirectory();
        var (forge, _, fetcher, _) = Fixture(temp);

        var unset = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(WorkflowRemote.Source(GhesSource)!, Hosted("ghe.acme.internal"), temp.Path, NoVariables, recorded: null, Token));

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
            temp.Path,
            name => name == "GH_ENTERPRISE_TOKEN" ? "the-machines-own" : null,
            recorded: null,
            Token));

        Assert.Equal(
            $"'{GhesSource}' names the host 'ghe.acme.internal', for which workflowSources.hosts names no credential: a host beyond github.com is reached with the one credential its entry names, and never with the machine's own tokens; nothing was asked of it",
            unnamed.Message);
        Assert.Empty(forge.Requests);
    }

    /// <summary>
    /// Files are kept as the bytes the forge gave: one beginning with a byte order mark hashes, caches and reads again
    /// as it was - read as text the way a repository's own file is, the mark dropped - and one that is not UTF-8 is
    /// cached whole, its bytes read back unchanged, and refused only where it is read as text.
    /// </summary>
    [Fact]
    public async Task AFileWithAByteOrderMarkOrNotUtf8_IsCachedAsItsBytes_AndReadAsTheRepositorysOwnFileReads()
    {
        using var temp = new TempDirectory();
        var (forge, cache, fetcher, warned) = Fixture(temp);
        byte[] withMark = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(Definition)];
        byte[] latin1 = [0x63, 0x61, 0x66, 0xE9, 0x0A];
        Holds(forge, Commit, ("security-review.yml", withMark), ("prompts/review.md", latin1));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal(Definition, resolved.Definition);
        Assert.Equal(withMark, cache.ReadBytes(resolved.Entry, Yml));
        Assert.Equal(latin1, cache.ReadBytes(resolved.Entry, PromptPath));

        var notText = Assert.Throws<HarnessException>(() => cache.ReadText(resolved.Entry, PromptPath));

        Assert.Equal(HarnessExit.ConfigInvalid, notText.ExitCode);
        Assert.Equal($"'{PromptPath}' of the workflow 'github.com/dailysoftwaresystems/shared' at '{Commit}' is not UTF-8 text, and a workflow reads its files as text", notText.Message);

        // Read again from the cache, nothing is fetched and nothing reads as changed on disk.
        forge.Requests.Clear();
        var again = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal(Definition, again.Definition);
        Assert.Equal([$"{Repository}/git/ref/tags/v1"], forge.Asked);
        Assert.Empty(warned);
    }

    /// <summary>
    /// A command an agent runs - its environment marked - reads the workflow cache alone: a tag is resolved by the
    /// cache's newest resolution of it, nothing is asked of the forge and nothing of the cache is written, not even the
    /// use's date; one the cache does not hold is refused naming the command a person runs to fetch it.
    /// </summary>
    [Fact]
    public async Task AnAgentsCommand_ReadsTheCacheAlone_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var (forge, cache, fetcher, warned) = Fixture(temp, clock);
        static string? Agent(string name) => name == AgentChildEnvironment.AgentMarker ? "1" : null;

        var unfetched = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, Agent, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, unfetched.ExitCode);
        Assert.Equal(
            $"'{Source}' is not in the workflow cache as a command an agent runs needs it (it holds no resolution of 'v1'): such a command never reaches the forge nor writes the cache, so a person runs 'dssharness validate-workflow' to fetch it; nothing was asked of the forge",
            unfetched.Message);
        Assert.Empty(forge.Requests);
        Assert.False(System.IO.Directory.Exists(cache.Directory));

        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);
        var written = Snapshot(cache.Directory);
        forge.Requests.Clear();
        clock.Advance(TimeSpan.FromDays(3));

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, Agent, recorded: null, Token);

        Assert.Equal((Commit, Definition), (resolved.Commit, resolved.Definition));
        Assert.Empty(forge.Requests);
        Assert.Equal(written, Snapshot(cache.Directory));
        Assert.Equal(
            [$"{WorkflowRemoteFetcher.Said}: '{Source}' was resolved from the workflow cache alone, for a command an agent runs never reaches the forge: it stands at the commit {Commit} the cache's newest resolution of 'v1' names, from 2026-09-30"],
            warned);

        // A commit the cache does not hold, named by its SHA: refused, and still nothing asked.
        var sha = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed() with { Ref = Moved }, Allowed(), temp.Path, Agent, recorded: null, Token));

        Assert.StartsWith($"'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@{Moved}' is not in the workflow cache as a command an agent runs needs it (it holds no entry for the commit '{Moved}')", sha.Message, StringComparison.Ordinal);
        Assert.Empty(forge.Requests);
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
                Fetch: _ => Task.FromResult(new WorkflowRemoteResolution(parsed, Commit, Entry(), Definition))));

        Assert.Empty(problems);
        Assert.Equal((parsed, Commit), (Assert.Single(remotes).Source, Assert.Single(remotes).Commit));

        var step = definition.Steps.Single();

        Assert.Equal(WorkflowRemote.Name(parsed, Commit), step.Use!.Name);
        Assert.Equal(Commit, step.Use.Fingerprint);
        Assert.Equal(WorkflowRemote.Name(parsed, Commit), step.Steps.Single().WorkflowOf);
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
                Fetch: _ => Task.FromResult(new WorkflowRemoteResolution(parsed, Commit, Entry(), self))));

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
        var (forge, cache, fetcher, _) = Fixture(temp);
        var root = temp.Combine("workflow-cache");
        Holds(forge, Commit, ("security-review.yml", Outer), ("inner/inner.yml", Inner), ("inner/prompts/check.md", Check));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        var resolution = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);
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

        var name = $"remote/github.com/dailysoftwaresystems/shared/{Commit}/workflows/security-review/inner";
        var nested = definition.Steps.Single().Steps.Single();

        Assert.Empty(problems);
        Assert.Empty(local);
        Assert.Equal(("review", name, Commit), (nested.Name, nested.Use!.Name, nested.Use.Fingerprint));
        Assert.Equal(name, nested.Steps.Single().WorkflowOf);

        // At run time the nested workflow's files are read from the entry that holds it, by its own directory.
        var named = new WorkflowNamedFiles(new PhysicalFileSystem(new HarnessFactory().FilePermissions), StringComparison.Ordinal, cache);
        var layout = new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path);

        Assert.Equal(Check, named.ReadText(layout, name, "prompts/check.md", "step 'scan.review.check''s prompt"));

        // A blob changed on disk is refused as it is read, never run.
        await File.WriteAllTextAsync(Path.Combine(root, "blobs", WorkflowCache.BlobSha(Check)[..2], WorkflowCache.BlobSha(Check)[2..]), "Something else.\n", Token);

        Assert.Equal(HarnessExit.ConfigInvalid, Assert.Throws<HarnessException>(() => named.ReadText(layout, name, "prompts/check.md", "step 'scan.review.check''s prompt")).ExitCode);

        // A name the cache holds no entry for is said so.
        var gone = $"remote/github.com/dailysoftwaresystems/shared/{Moved}/workflows/security-review/inner";

        Assert.Equal(
            $"'prompts/check.md', step 'scan.review.check''s prompt, is of the workflow '{gone}', which the workflow cache holds no entry for; fetch it again where a use names it",
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
                    Fetch: _ => Task.FromResult(new WorkflowRemoteResolution(parsed, Commit, Entry(), outer)),
                    ReadCached: cached));

            return problems;
        }

        Assert.Equal(
            ["step 'scan.review' uses '../other/other.yml', which leaves 'workflows/security-review', the directory the remote workflow of 'github.com/dailysoftwaresystems/shared' was fetched as: a use inside a remote workflow resolves inside the same cache entry, as a use inside a local one resolves inside the repository's workflows directory"],
            await ProblemsOf("name: security-review\nsteps:\n  - name: review\n    use: ../other/other.yml\n", (_, _) => null));
        Assert.Equal(
            [$"step 'scan.review' uses './inner/inner.yml', which could not be read: it is a workflow inside the remote workflow 'remote/github.com/dailysoftwaresystems/shared/{Commit}/workflows/security-review', and no resolution of this kind reads the cache entry that holds it"],
            await ProblemsOf("name: security-review\nsteps:\n  - name: review\n    use: ./inner/inner.yml\n", cached: null));

        // One the entry does not hold is said as the commit's, never looked for in the repository.
        Assert.Equal(
            ["step 'scan.review' uses './inner/inner.yml', and the commit 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' of 'github.com/dailysoftwaresystems/shared' holds no workflow file at 'workflows/security-review/inner/inner.yml'"],
            await ProblemsOf("name: security-review\nsteps:\n  - name: review\n    use: ./inner/inner.yml\n", (_, _) => null));
    }

    /// <summary>
    /// A fetch refused for what the source is - not allowed, a ref the pin refuses - is the definition's problem, said
    /// with the use; a fetch that could not run - the forge's tool missing, the network not there, a call that failed -
    /// is no problem of the definition's, and the resolution goes on with its own code.
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
        var (forge, cache, fetcher, _) = Fixture(temp, clock);
        Holds(forge, Moved, ("security-review.yml", Definition), ("prompts/old.md", "an old prompt\n"));
        Holds(forge, Commit, ("security-review.yml", Definition), ("prompts/review.md", Prompt));

        // The stale entry, at the moved commit, used forty days ago; the kept one used yesterday. The yml is one blob
        // both hold.
        forge.Answers[$"{Repository}/git/ref/tags/v0"] = TagAt(Moved);
        _ = await fetcher.ResolveAsync(Parsed() with { Ref = "v0" }, Allowed(), temp.Path, NoVariables, recorded: null, Token);
        clock.Advance(TimeSpan.FromDays(39));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        var kept = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);
        clock.Advance(TimeSpan.FromDays(1));

        var pruning = cache.Prune(30, clock.GetUtcNow());

        Assert.Equal((1, 1, true), (pruning.Entries, pruning.Blobs, pruning.Whole));
        Assert.Equal([Commit], cache.List().Entries.Select(listed => listed.Entry.Commit));
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
        var fileSystem = new UndeletableBlobs(new PhysicalFileSystem(new HarnessFactory().FilePermissions));
        var cache = new WorkflowCache(fileSystem, root);
        var forge = new Forge();
        var fetcher = new WorkflowRemoteFetcher(forge.Gh(), cache, clock: clock);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);
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

        var blind = new WorkflowCache(new PhysicalFileSystem(new HarnessFactory().FilePermissions), root).Prune(0, clock.GetUtcNow() + TimeSpan.FromDays(1));

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
        var (forge, cache, fetcher, warned) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        var file = System.IO.Directory.EnumerateFiles(Path.Combine(cache.Directory, "entries")).Single();
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file, Token)).Replace(WorkflowCache.BlobSha(Definition), "../../escape", StringComparison.Ordinal), Token);

        var refused = Assert.Throws<HarnessException>(() => cache.Find("github.com", "dailysoftwaresystems", "shared", Commit, Directory));

        Assert.Equal(HarnessExit.ConfigInvalid, refused.ExitCode);
        Assert.Equal($"the workflow cache's entry '{file}' names the blob '../../escape', which is no 40 hex characters; it was not read, and a resolution of it fetches it again", refused.Message);
        Assert.Equal([file], cache.List().Unread.Select(unread => unread.File));

        var again = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

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
        var cache = new WorkflowCache(new PhysicalFileSystem(new HarnessFactory().FilePermissions), () =>
        {
            asked++;
            throw new DirectoryNotFoundException("no directory of this user's own application data could be named: the process was given no home, and the system has none for its account");
        });

        Assert.Equal(0, asked);

        var refused = Assert.Throws<HarnessException>(() => cache.List());

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "the workflow cache has nowhere to be kept: no directory of this user's own application data could be named: the process was given no home, and the system has none for its account; a remote workflow is neither fetched nor read, and a workflow that names none runs as it would",
            refused.Message);
    }

    /// <summary>
    /// A run reads a remote workflow's files from its cache entry by the workflow's name, hashed again as they are read:
    /// a prompt reads as the forge gave it, and a blob changed on disk since is refused at the read, never run.
    /// </summary>
    [Fact]
    public async Task ARunReadsARemoteWorkflowsFilesFromTheCache_AndRefusesABlobChangedOnDisk()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(forge, Commit, ("security-review.yml", Definition), ("prompts/review.md", Prompt));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);
        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);
        var name = WorkflowRemote.Name(resolved.Source, resolved.Commit);
        var files = new WorkflowNamedFiles(harness.FileSystem, StringComparison.Ordinal, cache);
        var layout = new HarnessLayout(temp.Path, temp.Path);

        Assert.Equal(Prompt, files.ReadText(layout, name, "prompts/review.md", "a prompt"));

        await File.WriteAllTextAsync(cache.BlobFile(WorkflowCache.BlobSha(Prompt)), "Review nothing.\n", Token);

        var refused = Assert.Throws<HarnessException>(() => files.ReadText(layout, name, "prompts/review.md", "a prompt"));

        Assert.Equal(HarnessExit.ConfigInvalid, refused.ExitCode);
        Assert.StartsWith($"the workflow cache's '{cache.BlobFile(WorkflowCache.BlobSha(Prompt))}', the file '{PromptPath}' of the entry for 'github.com/dailysoftwaresystems/shared' at '{Commit}', does not hash to", refused.Message, StringComparison.Ordinal);
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
        var (forge, cache, fetcher, _) = Fixture(temp);
        byte[] marked = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(Prompt)];
        byte[] binary = [0x89, 0x50, 0x4E, 0x47, 0xFF, 0x00, 0xC3];
        Holds(forge, Commit, ("security-review.yml", Encoding.UTF8.GetBytes(Definition)), ("prompts/review.md", marked), ("assets/mark.png", binary));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

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
        Assert.Equal(
            ["security-review.yml", "prompts/review.md", "assets/mark.png", WorkflowExportService.VendoredNote],
            export.Files);
        Assert.Equal(Definition, await File.ReadAllTextAsync(Path.Combine(destination, "security-review.yml"), Token));
        Assert.Equal(marked, await File.ReadAllBytesAsync(Path.Combine(destination, "prompts", "review.md"), Token));
        Assert.Equal(binary, await File.ReadAllBytesAsync(Path.Combine(destination, "assets", "mark.png"), Token));
        Assert.Contains(
            $"Vendored from github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1, which resolved to the commit {Commit}",
            await File.ReadAllTextAsync(Path.Combine(destination, WorkflowExportService.VendoredNote), Token),
            StringComparison.Ordinal);
        Assert.Equal(
            $"github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1 at commit {Commit}",
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
        var (forge, cache, fetcher, _) = Fixture(temp);
        var anyRef = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "any" };

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { WorkflowSources = anyRef });

        WorkflowExportService Export()
            => new(Substitute.For<IWorkflowValidationService>(), harness.FileSystem, new FixedProcessContext { Fixed = temp.Path }, harness.ContextLoader, fetcher, cache);

        forge.Answers[Repository] = Json("""{"default_branch":"main"}""");
        Holds(forge, Commit, ("security-review.yml", Definition), ("prompts/review.md", Prompt));
        forge.Answers[$"{Repository}/git/ref/heads/v1"] = Json($$$"""{"ref":"refs/heads/v1","object":{"sha":"{{{Commit}}}","type":"commit"}}""");

        var branch = await Export()
            .ExportAsync(temp.Path, new WorkflowExportRequest(Workflow: null, temp.Combine("vendor"), Shipped: false, From: Source), Token);

        Assert.Equal(
            $"github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1 at commit {Commit}, where the branch 'v1' stood then - a branch moves",
            branch.Vendored);
        Assert.StartsWith(
            $"Vendored from github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1, which resolved to the commit {Commit}, where the branch 'v1' stood then - a branch moves, and was fetched on ",
            await File.ReadAllTextAsync(temp.Combine("vendor", "security-review", WorkflowExportService.VendoredNote), Token),
            StringComparison.Ordinal);

        const string Note = "A note the workflow keeps of its own.\n";
        Holds(forge, Moved, ("security-review.yml", Definition), ("source.md", Note));
        forge.Answers[$"{Repository}/git/ref/tags/v2"] = TagAt(Moved);

        var colliding = await Assert.ThrowsAsync<HarnessException>(() => Export()
            .ExportAsync(temp.Path, new WorkflowExportRequest(Workflow: null, temp.Combine("elsewhere"), Shipped: false, From: "dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v2"), Token));

        Assert.Equal(HarnessExit.Refused, colliding.ExitCode);
        Assert.Equal(
            "'dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v2' holds a 'source.md' of its own in its directory, the name the note of where a vendored workflow came from is written as; export-workflow writes that note beside a workflow's files and never over one of them. Nothing was written.",
            colliding.Message);
        Assert.False(System.IO.Directory.Exists(temp.Combine("elsewhere")));
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
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(
            forge,
            Commit,
            ("security-review.yml", Outer),
            ("inner/inner.yml", Inner),
            ("inner/agents/reviewer.md", Agent),
            ("inner/prompts/check.md", Check),
            ("inner/schemas/check.json", Schema));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

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
        await File.WriteAllTextAsync(temp.Combine(".harness-config", "workflows", "flow", "flow.yml"), $"name: flow\nsteps:\n  - name: scan\n    use: {Source}\n", Token);

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
    /// validate-workflow names the workflow cache's directory beside each remote resolution it prints - where the
    /// check reads what it resolved, and writes what it fetches - so the person who ran it knows where that landed.
    /// </summary>
    [Fact]
    public async Task Validation_NamesTheWorkflowCachesDirectory_BesideEachRemoteResolution()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        const string Outer = "name: security-review\nsteps:\n  - name: review\n    use: ./inner/inner.yml\n";
        const string Inner = "name: inner\nsteps:\n  - name: check\n    type: reason\n    agent: reviewer\n    prompt: prompts/check.md\n    outputs: schemas/check.json\n";
        const string Agent = "---\nname: reviewer\ndescription: reviews the paths\n---\nYou review.\n";
        const string Check = "Check the paths.\n";
        const string Schema = """{ "type": "object", "properties": { "done": { "type": "boolean" } }, "required": ["done"], "additionalProperties": false }""";
        var (forge, cache, fetcher, _) = Fixture(temp);
        Holds(
            forge,
            Commit,
            ("security-review.yml", Outer),
            ("inner/inner.yml", Inner),
            ("inner/agents/reviewer.md", Agent),
            ("inner/prompts/check.md", Check),
            ("inner/schemas/check.json", Schema));
        forge.Answers[$"{Repository}/git/ref/tags/v1"] = TagAt(Commit);

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
        await File.WriteAllTextAsync(temp.Combine(".harness-config", "workflows", "flow", "flow.yml"), $"name: flow\nsteps:\n  - name: scan\n    use: {Source}\n", Token);

        var report = await new WorkflowValidationService(
            harness.ContextLoader,
            harness.FileSystem,
            harness.Platform,
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform),
            DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild,
            fetcher,
            cache).CheckAsync(temp.Path, "flow", Token);

        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Workflows.Single().Lines));

        var resolution = Assert.Single(report.Workflows.Single().Lines.Where(line => line.StartsWith("'scan' uses ", StringComparison.Ordinal)).ToList());

        Assert.StartsWith(
            $"'scan' uses {Source} -> github.com/dailysoftwaresystems/shared/workflows/security-review at commit {Commit[..12]}, kept in the workflow cache at '{cache.Directory}' (",
            resolution,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflowSources section is held to its rules: each allowed source named as one is - never blank - under a
    /// host the section names where it is under one, each host a host with the environment variable its credential is
    /// read from, none without one, and pin and offline spelled as they may be - github.com needing no host entry, for
    /// the machine's own gh login reaches it.
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
                "workflowSources.hosts names 'github.com', which needs no entry: the machine's own gh login reaches it",
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
    /// bounds and names: the variable each host kind's credential is given as, the blob's path, the refs a commit is
    /// compared with, the days a pruning takes and the code it ends incomplete with - and the configuration's help the
    /// shape a host's credential is written in.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayWhatTheFetchAndTheCacheDo()
    {
        var help = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("workflows"));
        var remote = Prose.Words(DssHarness.Core.Help.HelpTopics.RemoteWorkflowsHelp);

        Assert.Contains(remote, help, StringComparison.Ordinal);
        Assert.Contains(Prose.Words(DssHarness.Core.Help.HelpTopics.WorkflowCacheHelp), help, StringComparison.Ordinal);
        Assert.Contains($"given to its calls as {GhProcess.TokenVariable} for a *.ghe.com host and {GhProcess.EnterpriseTokenVariable} for any other with every other token of the machine's taken away", remote, StringComparison.Ordinal);
        Assert.Contains(
            Prose.Words("hosts, GitHub Enterprise hosts, each with the one credential its calls are given, \"credential\": {\"from\": \"env\", \"name\": \"<VARIABLE>\"} (required)"),
            Prose.Words(DssHarness.Core.Help.HelpTopics.Render("config")),
            StringComparison.Ordinal);

        var readme = Prose.Document("README.md");

        foreach (var said in new[]
        {
            $"given to its calls as `{GhProcess.TokenVariable}` for a `*.ghe.com` host and `{GhProcess.EnterpriseTokenVariable}` for any other, every other token of the machine's taken away",
            "each blob as its bytes at `blobs/<first 2 hex>/<remaining 38 hex>`",
            $"or one of the first {WorkflowRemoteFetcher.MostRefsCompared} tags and branches the forge lists, reaches it",
            $"`workflow-cache prune --unused-days N`, the days required and from 0 to {WorkflowCache.MostUnusedDays.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)},",
            $"every blob an entry it keeps holds kept, and no blob removed at all while a file of the cache's own could not be read; a file it could not remove or read is said, and the pruning ends incomplete ({HarnessExit.Incomplete})",
            "`offline` says what happens where the network keeps the forge from being reached",
        })
        {
            Assert.Contains(said, readme, StringComparison.Ordinal);
        }

        var architecture = Prose.Document("docs", "architecture.md");

        foreach (var said in new[]
        {
            $"(`WorkflowRemote.{nameof(WorkflowRemote.RefProblem)}`, before anything is asked)",
            $"(`WorkflowCache.{nameof(WorkflowCache.TreePathProblems)}`)",
            $"with every other token variable taken away (`GhProcess.{nameof(GhProcess.HostEnvironment)}`)",
            $"(`GhProcess.{nameof(GhProcess.Unreached)}`) - is the network not being there",
            $"(`WorkflowCache.{nameof(WorkflowCache.BlobOf)}`, the inverse of the blob's path)",
            "at `blobs/<first 2 hex>/<remaining 38 hex>`",
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

    /// <summary>The token variables and GH_HOST a call was given or had taken away, by name.</summary>
    private static (string, string?)[] TokenEnvironment(ProcessRequest request)
        => [.. request.Environment
            .Where(variable => variable.Key is "GH_HOST" || GhProcess.TokenVariables.Contains(variable.Key, StringComparer.Ordinal))
            .OrderBy(variable => variable.Key, StringComparer.Ordinal)
            .Select(variable => (variable.Key, variable.Value))];

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

    /// <summary>A tree's hash for the commit <paramref name="commit"/> at the depth <paramref name="level"/>: the root, the workflows directory, the workflow's own.</summary>
    private static string Tree(string commit, int level)
        => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{commit}:{level.ToString(System.Globalization.CultureInfo.InvariantCulture)}")));

    /// <summary>The fixture's repository holding, at <paramref name="commit"/>, <paramref name="files"/> under the workflow's directory.</summary>
    private static void Holds(Forge forge, string commit, params (string Path, string Content)[] files)
        => HoldsOn(forge, Repository, commit, [.. files.Select(file => (file.Path, Encoding.UTF8.GetBytes(file.Content)))]);

    /// <summary>The fixture's repository holding, at <paramref name="commit"/>, <paramref name="files"/> under the workflow's directory, as bytes.</summary>
    private static void Holds(Forge forge, string commit, params (string Path, byte[] Content)[] files)
        => HoldsOn(forge, Repository, commit, files);

    private static void HoldsOn(Forge forge, string repository, string commit, params (string Path, string Content)[] files)
        => HoldsOn(forge, repository, commit, [.. files.Select(file => (file.Path, Encoding.UTF8.GetBytes(file.Content)))]);

    /// <summary>
    /// <paramref name="repository"/> holding, at <paramref name="commit"/>, <paramref name="files"/> under
    /// <c>workflows/security-review</c>: the commit, each tree on the way to the directory, the directory's own listing -
    /// its subdirectories among it - and each blob.
    /// </summary>
    private static void HoldsOn(Forge forge, string repository, string commit, params (string Path, byte[] Content)[] files)
    {
        forge.Answers[$"{repository}/git/commits/{commit}"] = Json($$$"""{"sha":"{{{commit}}}","tree":{"sha":"{{{Tree(commit, 0)}}}"}}""");
        forge.Answers[$"{repository}/git/trees/{Tree(commit, 0)}"] = Listing(("workflows", "tree", "040000", Tree(commit, 1), null), ("README.md", "blob", "100644", WorkflowCache.BlobSha("readme\n"), 7));
        forge.Answers[$"{repository}/git/trees/{Tree(commit, 1)}"] = Listing(("security-review", "tree", "040000", Tree(commit, 2), null));

        var directories = files
            .SelectMany(file => file.Path.Split('/')[..^1].Select((_, at) => string.Join('/', file.Path.Split('/')[..(at + 1)])))
            .Distinct(StringComparer.Ordinal)
            .Select(directory => (directory, "tree", "040000", Tree(commit + directory, 3), (long?)null));

        forge.Answers[$"{repository}/git/trees/{Tree(commit, 2)}?recursive=1"] = Listing(
            [.. files.Select(file => (file.Path, "blob", "100644", WorkflowCache.BlobSha(file.Content), (long?)file.Content.Length)), .. directories]);

        foreach (var (_, content) in files)
        {
            forge.Answers[$"{repository}/git/blobs/{WorkflowCache.BlobSha(content)}"] = Blob(content);
        }
    }

    /// <summary>A tree's listing as the forge answers it.</summary>
    private static ProcessResult Listing(params (string Path, string Type, string Mode, string Sha, long? Size)[] items)
        => Listing(truncated: false, items);

    private static ProcessResult Listing(bool truncated, params (string Path, string Type, string Mode, string Sha, long? Size)[] items)
        => Json(new System.Text.Json.Nodes.JsonObject
        {
            ["sha"] = "0000000000000000000000000000000000000000",
            ["tree"] = new System.Text.Json.Nodes.JsonArray([.. items.Select(item => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = item.Path,
                ["mode"] = item.Mode,
                ["type"] = item.Type,
                ["sha"] = item.Sha,
                ["size"] = item.Size,
            })]),
            ["truncated"] = truncated,
        }.ToJsonString());

    /// <summary>A lightweight tag's ref, naming <paramref name="commit"/>.</summary>
    private static ProcessResult TagAt(string commit) => Json($$$"""{"ref":"refs/tags/v1","object":{"sha":"{{{commit}}}","type":"commit"}}""");

    /// <summary>One blob's answer, base64 with the newlines the forge's JSON holds them over, escaped as JSON escapes them.</summary>
    private static ProcessResult Blob(byte[] content)
    {
        var base64 = Convert.ToBase64String(content);
        var wrapped = new StringBuilder();

        for (var at = 0; at < base64.Length; at += 60)
        {
            _ = wrapped.Append(base64[at..Math.Min(at + 60, base64.Length)]).Append('\n');
        }

        return Json($$"""{"content":"{{wrapped.ToString().TrimEnd('\n').Replace("\n", "\\n", StringComparison.Ordinal)}}","encoding":"base64","size":{{content.Length}}}""");
    }

    private static ProcessResult Json(string answer) => Exited(0, answer);

    /// <summary>The fetcher, its forge and its cache in <paramref name="temp"/>, and what it warned of.</summary>
    private static (Forge Forge, WorkflowCache Cache, WorkflowRemoteFetcher Fetcher, List<string> Warned) Fixture(TempDirectory temp, ManualClock? clock = null)
    {
        var forge = new Forge();
        var cache = new WorkflowCache(new PhysicalFileSystem(new HarnessFactory().FilePermissions), temp.Combine("workflow-cache"));
        var warned = new List<string>();
        var output = Substitute.For<IHarnessOutput>();
        output.When(said => said.Warn(Arg.Any<string>(), Arg.Any<string>())).Do(call => warned.Add($"{call.ArgAt<string>(0)}: {call.ArgAt<string>(1)}"));

        return (forge, cache, new WorkflowRemoteFetcher(forge.Gh(), cache, output, clock ?? new ManualClock()), warned);
    }

    /// <summary>Another fetcher over <paramref name="cache"/>, asking <paramref name="forge"/>.</summary>
    private static WorkflowRemoteFetcher Over(WorkflowCache cache, Forge forge) => new(forge.Gh(), cache, clock: new ManualClock());

    /// <summary>The cache entry of the fixture, as a resolution hands it to the resolver.</summary>
    private static WorkflowCacheEntry Entry()
        => new(
            "github.com",
            "dailysoftwaresystems",
            "shared",
            Commit,
            Directory,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            [new WorkflowCacheFile(Yml, WorkflowCache.BlobSha(Definition), "100644")]);

    private static ProcessResult Exited(int exitCode, string standardOutput = "", string stderr = "")
        => new(exitCode, standardOutput, stderr, TimeSpan.Zero, TimedOut: false);

    /// <summary>
    /// The forge as gh answers it: each call by the address it asks, every call kept; an address it holds no answer for
    /// answered as gh answers a thing not there, and every call answered alike where <see cref="Always"/> is set.
    /// </summary>
    private sealed class Forge
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Dictionary<string, ProcessResult> Answers { get; } = new(StringComparer.Ordinal);

        public ProcessResult? Always { get; set; }

        /// <summary>The address each call asked, in order.</summary>
        public IReadOnlyList<string> Asked => [.. Requests.Select(request => request.Arguments[3])];

        public GhProcess Gh()
        {
            var runner = Substitute.For<IProcessRunner>();

            runner.FindExecutable(GhProcess.Executable).Returns(GhProcess.Executable);
            runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var request = call.Arg<ProcessRequest>();
                Requests.Add(request);

                return Task.FromResult(Always ?? (Answers.TryGetValue(request.Arguments[3], out var answer) ? answer : Exited(1, stderr: "gh: Not Found (HTTP 404)\n")));
            });

            return new GhProcess(runner, GitLocalVariables.Fixed(["GIT_DIR"]), NoVariables);
        }
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
