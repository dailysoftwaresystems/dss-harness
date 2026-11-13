using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// A workflow used as a step from beyond the repository: the source's spelling, the fetch through the forge's tool -
/// a ref resolved to a commit, the commit's tree read, every blob under the workflow's directory checked against the
/// hash the tree gives - the hash cache it is kept in, read again as it is read, the trust a configuration alone
/// grants, the offline fallback, and the export that vendors the whole for review.
/// </summary>
public sealed class RemoteWorkflowUseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Moved = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Source = "dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1";

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

    /// <summary>The forms a source takes: an owner, a repository, a workflow file's path in it, and a ref after @ - and a host under its own name, where there is one.</summary>
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

        Assert.Null(WorkflowRemote.Source("./pipeline/pipeline.yml"));
        Assert.Null(WorkflowRemote.Source("../pipeline/pipeline.yml"));
        Assert.Null(WorkflowRemote.Source("dailysoftwaresystems/shared/workflows/pipeline/pipeline.yml"));
        Assert.Null(WorkflowRemote.Source("dailysoftwaresystems/shared@v1"));
        Assert.Null(WorkflowRemote.Source("dailysoftwaresystems/shared/pipeline.yml@v1"));

        static (string, string, string, string, string)? AsParts(string source)
            => WorkflowRemote.Source(source) is { } parsed ? (parsed.Host, parsed.Owner, parsed.Repo, parsed.File, parsed.Ref) : null;
    }

    /// <summary>
    /// A tag is resolved to its commit through its ref, the commit's tree read, and every blob under the workflow's
    /// directory fetched and hashed as git hashes one - the whole kept in the cache, blobs by their git hash and the
    /// entry by its repository, commit and path, and the second resolution of the same commit makes no request at all.
    /// </summary>
    [Fact]
    public async Task ATagIsResolved_ItsDirectoryFetchedAndHashChecked_KeptInTheCache()
    {
        using var temp = new TempDirectory();
        var (requests, root, cache, fetcher) = Fixture(temp, Tag(), Tree(), Blob(Definition), Blob(Prompt));

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal(Commit, resolved.Commit);
        Assert.False(resolved.Moving);
        Assert.Equal(Definition, resolved.Definition);
        Assert.Equal(
            [
                ["api", "repos/dailysoftwaresystems/shared/git/ref/tags/v1", "--jq", ".object.sha"],
                ["api", $"repos/dailysoftwaresystems/shared/git/trees/{Commit}?recursive=1"],
                ["api", $"repos/dailysoftwaresystems/shared/git/blobs/{WorkflowCache.BlobSha(Definition)}"],
                ["api", $"repos/dailysoftwaresystems/shared/git/blobs/{WorkflowCache.BlobSha(Prompt)}"],
            ],
            requests.Select(request => request.Arguments.ToArray()));

        var entry = Assert.Single(cache.List()).Entry;

        Assert.Equal(("github.com", "dailysoftwaresystems", "shared", Commit, "workflows/security-review", "v1"), (entry.Host, entry.Owner, entry.Repo, entry.Commit, entry.Path, entry.AskedFor));
        Assert.Equal(
            ["workflows/security-review/prompts/review.md", "workflows/security-review/security-review.yml"],
            [.. entry.Files.Select(file => file.Path).Order(StringComparer.Ordinal)]);

        // The same tag again: one call re-resolves it - a tag needs that, at every run's start - and the cache answers
        // the commit it holds, with no tree or blob asked for.
        var (asked, againFetcher) = Fetching(cache, Tag());
        var again = await againFetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal((Commit, Definition, false, false), (again.Commit, again.Definition, again.Moving, again.FromLastResolved));
        Assert.Single(asked);
    }

    /// <summary>
    /// A blob that does not hash to what the commit's tree says of it is refused, and nothing is cached: what a run
    /// would run must be what the repository it came from says it is.
    /// </summary>
    [Fact]
    public async Task ABlobThatDoesNotHashToWhatTheTreeSays_IsRefused_AndNothingIsCached()
    {
        using var temp = new TempDirectory();
        var (requests, _, cache, fetcher) = Fixture(temp, Tag(), Tree(), Blob("something else entirely\n"));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith(
            $"'{Source}': the blob '{WorkflowCache.BlobSha(Definition)}' of 'workflows/security-review/security-review.yml' does not hash to what the commit's tree says of it",
            refused.Message,
            StringComparison.Ordinal);
        Assert.Empty(cache.List());
        Assert.Equal(3, requests.Count);
    }

    /// <summary>A directory past the fetch's bound of files is refused with the count, and nothing of it is fetched.</summary>
    [Fact]
    public async Task ADirectoryPastTheBoundOfFiles_IsRefusedWithTheCount_AndNothingIsFetched()
    {
        using var temp = new TempDirectory();
        var entries = Enumerable.Range(0, WorkflowCache.MostFiles + 1)
            .Select(at => $$"""{"path":"workflows/security-review/f{{at}}.md","sha":"{{new string('c', 40)}}","type":"blob","mode":"100644","size":1}""");
        var (requests, _, cache, fetcher) = Fixture(
            temp,
            Tag(),
            Exited(0, $$"""{"tree":[{{string.Join(",", entries)}}]}"""));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            $"resolved to a directory of {(WorkflowCache.MostFiles + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} files, past the fetch's bound of {WorkflowCache.MostFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            refused.Message,
            StringComparison.Ordinal);
        Assert.Empty(cache.List());
        Assert.Equal(2, requests.Count);
    }

    /// <summary>
    /// A repository the configuration does not allow is refused before anything is asked of the forge, and an allow
    /// list names an owner whole, its every repository, or one repository - another host's under its own name alone.
    /// </summary>
    [Fact]
    public async Task ARepositoryTheConfigurationDoesNotAllow_IsRefusedBeforeAnythingIsAsked()
    {
        using var temp = new TempDirectory();
        var (requests, _, _, fetcher) = Fixture(temp, Tag(), Tree(), Blob(Definition), Blob(Prompt));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), null, temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names the repository 'dailysoftwaresystems/shared', which workflowSources.allow does not allow; a remote workflow directs agents and machine steps in this repository, so it is trusted by configuration alone; the allow list is empty, so no remote source is allowed until the repository allows its owner",
            refused.Message);
        Assert.Empty(requests);

        var resolved = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal(Commit, resolved.Commit);

        Assert.True(WorkflowSourceRules.Allows(["dailysoftwaresystems"], "github.com", "dailysoftwaresystems", "shared"));
        Assert.True(WorkflowSourceRules.Allows(["dailysoftwaresystems/*"], "github.com", "dailysoftwaresystems", "shared"));
        Assert.True(WorkflowSourceRules.Allows(["dailysoftwaresystems/shared"], "github.com", "dailysoftwaresystems", "shared"));
        Assert.False(WorkflowSourceRules.Allows(["dailysoftwaresystems/other"], "github.com", "dailysoftwaresystems", "shared"));
        Assert.False(WorkflowSourceRules.Allows(["dailysoftwaresystems/*"], "ghe.acme.internal", "dailysoftwaresystems", "shared"));
        Assert.True(WorkflowSourceRules.Allows(["ghe.acme.internal/dailysoftwaresystems/*"], "ghe.acme.internal", "dailysoftwaresystems", "shared"));
    }

    /// <summary>
    /// The pin holds the ref to a tag or a full commit SHA by default, a commit only where it says commit, and allows
    /// a branch - which moves - only where it says any; a branch is told from a tag by the tag's own ref answering
    /// nothing.
    /// </summary>
    [Fact]
    public async Task ThePinHoldsTheRefToATagOrACommit_ABranchOnlyWhereItSaysAny()
    {
        using var temp = new TempDirectory();
        var allowed = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"] };
        var (requests, _, cache, fetcher) = Fixture(temp, Missing());

        var refused = await Assert.ThrowsAsync<HarnessException>(() => fetcher.ResolveAsync(Parsed(), allowed, temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            $"'{Source}' names the ref 'v1', which is no tag of 'dailysoftwaresystems/shared'; workflowSources.pin is tag-or-commit, which takes a tag or a full commit SHA - a branch moves, and what a run ran must be what it said",
            refused.Message);
        Assert.Single(requests);

        var commit = await Assert.ThrowsAsync<HarnessException>(
            () => Fetcher(cache, Tag()).ResolveAsync(Parsed(), new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "commit" }, temp.Path, NoVariables, recorded: null, Token));

        Assert.StartsWith($"'{Source}' names the ref 'v1', which is no full commit SHA; workflowSources.pin is commit, which takes a full commit SHA only", commit.Message, StringComparison.Ordinal);

        var any = await Fetcher(cache, Missing(), CommitOf(), BranchTree(), Blob(Definition)).ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "any" },
            temp.Path,
            NoVariables,
            recorded: null,
            Token);

        Assert.Equal((Commit, true), (any.Commit, any.Moving));
        Assert.Single(cache.List());
    }

    /// <summary>
    /// Where the network is not there, the run is refused - unless the configuration says last-resolved, and then the
    /// cache's last resolution of the ref stands in, said so by the resolution the run records.
    /// </summary>
    [Fact]
    public async Task OfflineTheRunIsRefused_UnlessLastResolvedStandsIn()
    {
        using var temp = new TempDirectory();
        var (_, _, cache, online) = Fixture(temp, Tag(), Tree(), Blob(Definition), Blob(Prompt));

        _ = await online.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        // The network is not there any more: the tag cannot be resolved, and the refusals say what the configuration
        // would have stand in.
        var offline = Fetcher(cache, Exited(1, stderr: "gh: Could not resolve host: api.github.com\n"));
        var refused = await Assert.ThrowsAsync<HarnessException>(() => offline.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.EndsWith(
            "workflowSources.offline is refuse, so nothing stands in - last-resolved would use the cache's last resolution, which the run records that it did",
            refused.Message,
            StringComparison.Ordinal);

        var stood = await offline.ResolveAsync(
            Parsed(),
            new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Offline = "last-resolved" },
            temp.Path,
            NoVariables,
            recorded: null,
            Token);

        Assert.Equal((Commit, Definition, true), (stood.Commit, stood.Definition, stood.FromLastResolved));
    }

    /// <summary>
    /// A cache file changed on disk does not read as the workflow it cached: the blob is hashed again as it is read,
    /// refused, and the directory fetched again whole.
    /// </summary>
    [Fact]
    public async Task AChangedCacheFileIsRefused_AndTheDirectoryIsFetchedAgain()
    {
        using var temp = new TempDirectory();
        var (requests, root, cache, fetcher) = Fixture(temp, Tag(), BranchTree(), Blob(Definition), Blob(Definition));

        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        var blob = Directory.EnumerateFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(blob, "changed on disk\n", Token);

        // The refetch asks the tag, the tree and the blob again: the fetcher is a new one, with answers of its own.
        var refetching = Fetcher(cache, Tag(), BranchTree(), Blob(Definition));
        var again = await refetching.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        Assert.Equal(Commit, again.Commit);
        Assert.Equal(Definition, again.Definition);
        Assert.Equal(Definition, cache.ReadText(Assert.Single(cache.List()).Entry, "workflows/security-review/security-review.yml"));
    }

    /// <summary>
    /// A run's own recorded commit is used again however the ref has moved: the tag answers another commit now, and
    /// the resolution stands at the one the run began with, asking the forge nothing.
    /// </summary>
    [Fact]
    public async Task ARecordedCommitIsUsedAgain_HoweverTheRefHasMoved()
    {
        using var temp = new TempDirectory();
        var (_, _, cache, fetcher) = Fixture(temp, Tag(), Tree(), Blob(Definition), Blob(Prompt));

        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        // The tag has moved since the run began: it answers another commit now.
        var (moved, movedFetcher) = Fetching(cache, Exited(0, $""" "{Moved}" """.Trim()));

        var resolved = await movedFetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: Commit, Token);

        Assert.Equal(Commit, resolved.Commit);
        Assert.Empty(moved);
    }

    /// <summary>
    /// A GitHub Enterprise host's credential, named in the configuration as an environment variable, is handed to the
    /// call as its environment and written nowhere else - not to the cache, not to a record.
    /// </summary>
    [Fact]
    public async Task AHostsCredentialIsGivenToTheCallAndWrittenNowhere()
    {
        using var temp = new TempDirectory();
        const string source = "ghe.acme.internal/acme/shared/workflows/security-review/security-review.yml@v1";
        var (requests, root, cache, fetcher) = Fixture(temp, Tag(), Tree(), Blob(Definition), Blob(Prompt));

        var settings = new WorkflowSourcesConfig
        {
            Allow = ["ghe.acme.internal/acme/*"],
            Hosts = new Dictionary<string, WorkflowHostConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["ghe.acme.internal"] = new() { Credential = new WorkflowHostCredentialConfig { From = "env", Name = "GHE_TOKEN" } },
            },
        };

        var resolved = await fetcher.ResolveAsync(
            WorkflowRemote.Source(source)!,
            settings,
            temp.Path,
            name => name == "GHE_TOKEN" ? "the-token" : null,
            recorded: null,
            Token);

        Assert.Equal(Commit, resolved.Commit);
        Assert.All(requests, request =>
        {
            Assert.Equal(["api", "--hostname", "ghe.acme.internal"], request.Arguments.Take(3).ToArray());
            Assert.True(request.Environment.TryGetValue("GH_TOKEN", out var token) && token == "the-token");
            Assert.DoesNotContain("the-token", request.Arguments, StringComparer.Ordinal);
        });

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("the-token", await File.ReadAllTextAsync(file, Token), StringComparison.Ordinal);
        }
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

        var (placed, localProblems, _) = await WorkflowResolver.ResolveAsync(local, "flow", new WorkflowResolution(_ => "name: x\nsteps: []\n"));

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
        var (_, root, cache, fetcher) = Fixture(
            temp,
            Tag(),
            TreeOf(
                ("workflows/security-review/security-review.yml", Outer),
                ("workflows/security-review/inner/inner.yml", Inner),
                ("workflows/security-review/inner/prompts/check.md", Check)),
            Blob(Outer),
            Blob(Inner),
            Blob(Check));
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
    /// workflows directory is; and where the resolution reads no cache entry, the use is refused saying so - neither is
    /// ever read from the repository.
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
    /// The cache's list says each entry, and its pruning removes the entries no run used for its days and the blobs no
    /// entry left holds.
    /// </summary>
    [Fact]
    public async Task TheCacheListsItsEntries_AndPrunesWhatNoRunUsedForItsDays()
    {
        using var temp = new TempDirectory();
        var (_, root, cache, fetcher) = Fixture(temp, Tag(), BranchTree(), Blob(Definition));

        _ = await fetcher.ResolveAsync(Parsed(), Allowed(), temp.Path, NoVariables, recorded: null, Token);

        var listed = Assert.Single(cache.List());

        Assert.StartsWith($"github.com/dailysoftwaresystems/shared at {Commit[..12]}: workflows/security-review", WorkflowCacheService.EntryLine(listed), StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(Definition), listed.Bytes);

        // An entry whose last use is older than the pruning's days goes, with its blobs; nothing else holds one.
        var (entries, blobs) = cache.Prune(0, DateTimeOffset.UtcNow + TimeSpan.FromDays(1));

        Assert.Equal((1, 1), (entries, blobs));
        Assert.Empty(cache.List());
        Assert.False(Directory.EnumerateFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories).Any());
    }

    /// <summary>
    /// export-workflow --from vendors the remote workflow at its resolved commit: every file of its directory, byte for
    /// byte, and a note saying where it came from - so a team can review what is written and use it as ./.
    /// </summary>
    [Fact]
    public async Task ExportVendorsTheRemoteWorkflow_WithANoteOfWhereItCameFrom()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (_, _, cache, _) = Fixture(temp, Tag(), Tree(), Blob(Definition), Blob(Prompt));

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { WorkflowSources = Allowed() });

        var export = await new WorkflowExportService(
            Substitute.For<IWorkflowValidationService>(),
            harness.FileSystem,
            new FixedProcessContext { Fixed = temp.Path },
            harness.ContextLoader,
            Fetcher(cache, Tag(), Tree(), Blob(Definition), Blob(Prompt)),
            cache).ExportAsync(
            temp.Path,
            new WorkflowExportRequest(Workflow: null, temp.Combine("vendor"), Shipped: false, From: Source),
            Token);

        var destination = temp.Combine("vendor", "security-review");

        Assert.Equal(destination, export.Destination);
        Assert.Equal(
            ["security-review.yml", "prompts/review.md", WorkflowExportService.VendoredNote],
            export.Files);
        Assert.Equal(Definition, await File.ReadAllTextAsync(Path.Combine(destination, "security-review.yml"), Token));
        Assert.Equal(Prompt, await File.ReadAllTextAsync(Path.Combine(destination, "prompts", "review.md"), Token));
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
        var (_, _, cache, _) = Fixture(temp, Tag());
        var anyRef = new WorkflowSourcesConfig { Allow = ["dailysoftwaresystems/*"], Pin = "any" };

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { WorkflowSources = anyRef });

        WorkflowExportService Export(params ProcessResult[] answers)
            => new(Substitute.For<IWorkflowValidationService>(), harness.FileSystem, new FixedProcessContext { Fixed = temp.Path }, harness.ContextLoader, Fetcher(cache, answers), cache);

        var branch = await Export(Missing(), CommitOf(), Tree(), Blob(Definition), Blob(Prompt))
            .ExportAsync(temp.Path, new WorkflowExportRequest(Workflow: null, temp.Combine("vendor"), Shipped: false, From: Source), Token);

        Assert.Equal(
            $"github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1 at commit {Commit}, where the branch 'v1' stood then - a branch moves",
            branch.Vendored);
        Assert.StartsWith(
            $"Vendored from github.com/dailysoftwaresystems/shared/workflows/security-review/security-review.yml@v1, which resolved to the commit {Commit}, where the branch 'v1' stood then - a branch moves, and was fetched on ",
            await File.ReadAllTextAsync(temp.Combine("vendor", "security-review", WorkflowExportService.VendoredNote), Token),
            StringComparison.Ordinal);

        const string Note = "A note the workflow keeps of its own.\n";
        var colliding = await Assert.ThrowsAsync<HarnessException>(() => Export(Tag(), TreeOf(("workflows/other/other.yml", Definition), ("workflows/other/source.md", Note)), Blob(Definition), Blob(Note))
            .ExportAsync(temp.Path, new WorkflowExportRequest(Workflow: null, temp.Combine("elsewhere"), Shipped: false, From: "dailysoftwaresystems/shared/workflows/other/other.yml@v2"), Token));

        Assert.Equal(HarnessExit.Refused, colliding.ExitCode);
        Assert.Equal(
            "'dailysoftwaresystems/shared/workflows/other/other.yml@v2' holds a 'source.md' of its own in its directory, the name the note of where a vendored workflow came from is written as; export-workflow writes that note beside a workflow's files and never over one of them. Nothing was written.",
            colliding.Message);
        Assert.False(Directory.Exists(temp.Combine("elsewhere")));
    }

    /// <summary>
    /// The workflowSources section is held to its rules: each allowed source named as one is, each host a host with the
    /// environment variable its credential is read from, and pin and offline spelled as they may be - github.com
    /// needing no host entry, for the machine's own gh login reaches it.
    /// </summary>
    [Fact]
    public void TheWorkflowSourcesSection_IsHeldToItsRules()
    {
        Assert.Empty(HarnessConfigValidator.Validate(new HarnessConfig()));

        Assert.Equal(
            [
                "workflowSources.allow names 'acme/*/*', which puts * where a name is: the star stands for the repositories of an owner, owner/*",
                "workflowSources.allow names 'acme/shared/deep/one', which is neither an owner, owner/*, owner/repository nor host/owner/repository: each segment is named, and only a repository may be *",
                "workflowSources.hosts names 'ghe', which is no host: a host is named as its own name is, dotted, with no path in it",
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
                    Allow = ["acme/*/*", "acme/shared/deep/one"],
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
                Allow = ["acme", "acme/*", "acme/shared", "ghe.acme.internal/team/*"],
                Hosts = new Dictionary<string, WorkflowHostConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ghe.acme.internal"] = new() { Credential = new WorkflowHostCredentialConfig { From = "env", Name = "GHE_TOKEN" } },
                },
                Pin = "commit",
                Offline = "last-resolved",
            },
        }));
    }

    /// <summary>A fixed settings section that allows the fixture's repository.</summary>
    private static WorkflowSourcesConfig Allowed() => new() { Allow = ["dailysoftwaresystems/*"] };

    /// <summary>The fixture's source, parsed.</summary>
    private static WorkflowRemoteSource Parsed() => WorkflowRemote.Source(Source)!;

    /// <summary>An environment naming no variable.</summary>
    private static string? NoVariables(string name) => null;

    /// <summary>The tree of the commit, holding the workflow's directory: its yml and one prompt.</summary>
    private static ProcessResult Tree()
        => TreeOf(("workflows/security-review/security-review.yml", Definition), ("workflows/security-review/prompts/review.md", Prompt), ("README.md", "the repository's own readme\n"));

    /// <summary>The tree the moved tag answers, holding only the yml.</summary>
    private static ProcessResult BranchTree()
        => TreeOf(("workflows/security-review/security-review.yml", Definition));

    private static ProcessResult TreeOf(params (string Path, string Content)[] files)
    {
        static string EntryOf((string Path, string Content) file)
            => $$"""{"path":"{{file.Path}}","sha":"{{WorkflowCache.BlobSha(file.Content)}}","type":"blob","mode":"100644","size":{{Encoding.UTF8.GetByteCount(file.Content)}}}""";

        return Exited(0, $$"""{"tree":[{{string.Join(",", files.Select(EntryOf))}}]}""");
    }

    /// <summary>The tag's ref, answering the commit as <c>--jq .object.sha</c> answers it.</summary>
    private static ProcessResult Tag() => Exited(0, $""" "{Commit}" """.Trim());

    /// <summary>A branch's commit, as <c>--jq .sha</c> answers it.</summary>
    private static ProcessResult CommitOf() => Exited(0, $""" "{Commit}" """.Trim());

    /// <summary>A tag's ref that is not there, as gh answers it.</summary>
    private static ProcessResult Missing() => Exited(1, stderr: "HTTP 404: Not Found\n");

    /// <summary>One blob's answer, base64 with the newlines the forge's JSON holds them over, escaped as JSON escapes them.</summary>
    private static ProcessResult Blob(string content)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var wrapped = new StringBuilder();

        for (var at = 0; at < base64.Length; at += 60)
        {
            _ = wrapped.Append(base64[at..Math.Min(at + 60, base64.Length)]).Append('\n');
        }

        return Exited(0, $$"""{"content":"{{wrapped.ToString().TrimEnd('\n').Replace("\n", "\\n")}}","encoding":"base64"}""");
    }

    /// <summary>The fetcher and its cache in <paramref name="temp"/>, and the requests it made.</summary>
    private static (List<ProcessRequest> Requests, string Root, WorkflowCache Cache, WorkflowRemoteFetcher Fetcher) Fixture(
        TempDirectory temp,
        params ProcessResult[] answers)
    {
        var requests = new List<ProcessRequest>();
        var cache = new WorkflowCache(harnessFileSystem(temp), Path.Combine(temp.Path, "workflow-cache"));
        var fetcher = new WorkflowRemoteFetcher(Gh(requests, answers), cache);

        return (requests, Path.Combine(temp.Path, "workflow-cache"), cache, fetcher);

        static IFileSystem harnessFileSystem(TempDirectory temp) => new PhysicalFileSystem(new HarnessFactory().FilePermissions);
    }

    /// <summary>A fetcher over <paramref name="cache"/>, answering each call with the next of <paramref name="answers"/>.</summary>
    private static WorkflowRemoteFetcher Fetcher(WorkflowCache cache, params ProcessResult[] answers)
    {
        var requests = new List<ProcessRequest>();

        return new WorkflowRemoteFetcher(Gh(requests, answers), cache);
    }

    /// <summary>The fetcher over <paramref name="cache"/> that answers every call with <paramref name="answer"/>, saying what it asked.</summary>
    private static (List<ProcessRequest> Requests, WorkflowRemoteFetcher Fetcher) Fetching(WorkflowCache cache, ProcessResult answer)
    {
        var requests = new List<ProcessRequest>();

        return (requests, new WorkflowRemoteFetcher(Gh(requests, answer), cache));
    }

    /// <summary>The forge's tool, answering each call with the next of <paramref name="answers"/>, the last again once they run out.</summary>
    private static GhProcess Gh(List<ProcessRequest> requests, params ProcessResult[] answers)
    {
        var runner = Substitute.For<IProcessRunner>();
        var next = 0;

        runner.FindExecutable(GhProcess.Executable).Returns(GhProcess.Executable);
        runner.RunAsync(Arg.Do<ProcessRequest>(requests.Add), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(answers[Math.Min(next++, answers.Length - 1)]));

        return new GhProcess(runner, GitLocalVariables.Fixed(["GIT_DIR"]), NoVariables);
    }

    /// <summary>The cache entry of the fixture, as a resolution hands it to the resolver.</summary>
    private static WorkflowCacheEntry Entry()
        => new(
            "github.com",
            "dailysoftwaresystems",
            "shared",
            Commit,
            "workflows/security-review",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            AskedFor: "v1",
            [new WorkflowCacheFile("workflows/security-review/security-review.yml", WorkflowCache.BlobSha(Definition), "100644")]);

    private static ProcessResult Exited(int exitCode, string standardOutput = "", string stderr = "")
        => new(exitCode, standardOutput, stderr, TimeSpan.Zero, TimedOut: false);
}
