using System.Globalization;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Results;
using DssHarness.Core.Waiting;
using DssHarness.Core.Walks;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>A ref of a remote repository as git lists it: the object it names, and the object it peels to where it is an annotated tag.</summary>
/// <param name="Name">The ref's whole name: <c>refs/tags/v1</c>, <c>refs/heads/main</c>.</param>
/// <param name="Object">The object it names: a commit, or a tag object.</param>
/// <param name="Peeled">The object it peels to, where the remote says it is a tag object; null else.</param>
internal sealed record WorkflowRemoteRef(string Name, string Object, string? Peeled);

/// <summary>One entry of a directory at a commit, as git's tree holds it.</summary>
/// <param name="Path">Its path under the directory, forward slashes.</param>
/// <param name="Type">Its type: <c>blob</c>, <c>tree</c> or <c>commit</c> (a submodule).</param>
/// <param name="Mode">Its git mode.</param>
/// <param name="Object">Its object's id.</param>
internal sealed record WorkflowTreeEntry(string Path, string Type, string Mode, string Object);

/// <summary>
/// The one way a remote repository is asked: git itself, over its own transport, at the address the source names. Its refs
/// are listed whole in one call (<c>git ls-remote</c>) - a page of a forge's listing is the forge's fact, and never ends
/// the list here. Where something must be read of it, it is fetched into a repository of the fetch's own as a partial
/// clone that holds commits and tag objects alone (<c>--filter=tree:0</c>): the one ref a tag or a branch names, at its
/// tip; or, for a commit named by its SHA, every branch and every tag with their history, so git's own walk over every
/// ref proves the commit the repository's own (<c>for-each-ref --contains</c>). A tag is peeled through each tag object it
/// names with the ones peeled kept (<see cref="Chain"/>) until a commit; the commit's tree is then asked for by its id with
/// no blob, and the pinned directory's blobs by theirs, in one transfer - of any number of files and any size, git's own
/// tree whole, which no listing of a forge's cuts short, and nothing of the directory checked out, so no attribute of the
/// remote's own runs a filter. Every transfer is waited for as long as it takes, and said while it lasts
/// (<see cref="Waits"/>).
/// </summary>
/// <remarks>
/// github.com is reached as git reaches it, with the helpers git's configuration names - the machine's own login. A host
/// beyond it is given the one credential its configuration names and none of the machine's: every helper is cleared and
/// the credential handed in the environment, which no command line carries (<see cref="GitCredentialValues"/>).
/// </remarks>
/// <param name="git">Runs git.</param>
/// <param name="fileSystem">Makes and removes the directory a fetch is made in.</param>
/// <param name="output">Says a transfer that lasts, and a directory that could not be removed; null says nothing.</param>
/// <param name="clock">Times what is said of a transfer.</param>
/// <param name="address">The address a source's repository is fetched from; <c>https://host/owner/repo.git</c> where none is given.</param>
internal sealed class WorkflowGitFetch(IGitClient git, IFileSystem fileSystem, IHarnessOutput? output, TimeProvider clock, Func<WorkflowRemoteSource, string>? address = null)
{
    /// <summary>The name a ref listing's line peels a tag by: the object it names, after the ref's name.</summary>
    private const string PeelMark = "^{}";

    /// <summary>
    /// What git says where it never reached the remote: a name that did not resolve, a connection that was refused or
    /// never made. A remote that answered - a repository not there, a credential refused - is never this.
    /// </summary>
    private static readonly string[] UnreachedWords =
    [
        "Could not resolve host",
        "Could not resolve proxy",
        "Failed to connect",
        "Couldn't connect to server",
        "Connection refused",
        "Connection timed out",
        "Operation timed out",
        "Network is unreachable",
        "No route to host",
        "Temporary failure in name resolution",
        "Name or service not known",
        "Connection reset by peer",
    ];

    /// <summary>What git says where the remote answered that the repository is not there, or that the credential may not see it.</summary>
    private static readonly string[] NotThereWords =
    [
        "Repository not found",
        "fatal: repository '",
        "does not appear to be a git repository",
        "Authentication failed",
        "could not read Username",
        "terminal prompts disabled",
        "The requested URL returned error: 401",
        "The requested URL returned error: 403",
        "The requested URL returned error: 404",
    ];

    /// <summary>
    /// Whether <paramref name="failed"/>, which a call of this class's raised, says git never reached the remote
    /// (<see cref="UnreachedWords"/>): what the configuration's <c>offline</c> answers.
    /// </summary>
    /// <param name="failed">What a call raised.</param>
    public static bool Unreached(HarnessException failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        return failed.ExitCode == HarnessExit.CommandFailed && UnreachedWords.Any(words => failed.Message.Contains(words, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The address <paramref name="source"/>'s repository is fetched from.</summary>
    /// <param name="source">The source.</param>
    public string AddressOf(WorkflowRemoteSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return address?.Invoke(source) ?? $"https://{source.Host}/{source.Owner}/{source.Repo}.git";
    }

    /// <summary>
    /// Every branch and tag of <paramref name="source"/>'s repository, by its whole name, in one call: an annotated tag
    /// with the object it peels to, as the remote says it.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="credential">The credential its host is reached with; null where git's own helpers answer.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">
    /// The repository is not there, or the credential may not see it (<see cref="HarnessExit.Refused"/>); git never reached
    /// it (<see cref="Unreached"/>), or failed otherwise (<see cref="HarnessExit.CommandFailed"/>).
    /// </exception>
    public async Task<IReadOnlyDictionary<string, WorkflowRemoteRef>> RefsAsync(WorkflowRemoteSource source, GitCredential? credential, CancellationToken cancellationToken)
    {
        using var scratch = Made(source);
        var listed = await RemoteAsync(source, scratch.Path, credential, ["ls-remote", "--tags", "--heads", "--", AddressOf(source)], $"list the refs of '{source.Owner}/{source.Repo}'", cancellationToken)
            .ConfigureAwait(false);

        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var peeled = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t', StringComparison.Ordinal);

            if (tab < 0 || !WorkflowRemoteSource.IsCommit(line[..tab]))
            {
                throw Malformed(source, "the refs it listed", $"'{line}' names no object and ref");
            }

            var (id, name) = (line[..tab], line[(tab + 1)..]);

            if (name.EndsWith(PeelMark, StringComparison.Ordinal))
            {
                peeled[name[..^PeelMark.Length]] = id;
            }
            else
            {
                named[name] = id;
            }
        }

        return named.ToDictionary(pair => pair.Key, pair => new WorkflowRemoteRef(pair.Key, pair.Value, peeled.GetValueOrDefault(pair.Key)), StringComparer.Ordinal);
    }

    /// <summary>
    /// <paramref name="source"/>'s repository fetched as a partial clone into a directory of its own, commits and tag
    /// objects alone: the one ref <paramref name="reference"/> names at its tip, or every branch and every tag with their
    /// history where it names none - removed when the clone is disposed.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="credential">The credential its host is reached with; null where git's own helpers answer.</param>
    /// <param name="reference">The one ref to fetch, by its whole name (<c>refs/tags/v1</c>); null for every branch and tag.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">As <see cref="RefsAsync"/>.</exception>
    public async Task<WorkflowClone> CloneAsync(WorkflowRemoteSource source, GitCredential? credential, string? reference, CancellationToken cancellationToken)
    {
        var scratch = Made(source);

        try
        {
            var clone = new WorkflowClone(this, source, credential, scratch);
            await clone.InitializeAsync(reference, cancellationToken).ConfigureAwait(false);
            return clone;
        }
        catch
        {
            scratch.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a git command that reaches <paramref name="source"/>'s repository in <paramref name="directory"/>, waited for as
    /// long as it takes and said while it lasts, with the credential handed where there is one; its output, or the failure
    /// it ended with, classed as the remote not there, not reached, or another failure of git's.
    /// </summary>
    internal async Task<string> RemoteAsync(
        WorkflowRemoteSource source,
        string directory,
        GitCredential? credential,
        IReadOnlyList<string> arguments,
        string what,
        CancellationToken cancellationToken,
        string? input = null)
    {
        var waited = await Waits.ForAsync(
                token => git.RunRemoteAsync(directory, [.. GitCredentialValues.Arguments(credential), .. arguments], GitCredentialValues.Values(credential), input, token),
                new WaitWatch($"git to {what}", told => output?.Info(WorkflowRemoteFetcher.Said, told.Line()), null, clock),
                cancellationToken)
            .ConfigureAwait(false);

        if (waited is not { Kind: WaitEndKind.Done, Value: { } result })
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException($"the wait for git to {what} ended before git did", cancellationToken);
        }

        if (result.Succeeded)
        {
            return result.StandardOutput;
        }

        var said = result.StandardError.Trim() is { Length: > 0 } error ? error : $"git exited {result.ExitCode.ToString(CultureInfo.InvariantCulture)}";

        if (UnreachedWords.Any(words => said.Contains(words, StringComparison.OrdinalIgnoreCase)))
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"git could not {what}, never reaching '{source.Host}': {said}");
        }

        if (NotThereWords.Any(words => said.Contains(words, StringComparison.OrdinalIgnoreCase)))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' names the repository '{source.Owner}/{source.Repo}' on '{source.Host}', which git could not read there: it is misspelt, or the credential it is reached with cannot see it ({said}); nothing was cached");
        }

        throw new HarnessException(HarnessExit.CommandFailed, $"git could not {what}: {said}");
    }

    /// <summary>A git command of the clone's own, which reaches no remote: its output, or a failure of git's.</summary>
    internal async Task<GitCommandResult> LocalAsync(string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => await git.RunAsync(directory, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>Reads blobs of the clone by their ids, a batch at a time, in order.</summary>
    internal Task<IReadOnlyList<byte[]?>> BlobsAsync(string directory, IReadOnlyList<string> blobs, CancellationToken cancellationToken)
        => git.ReadBlobsAsync(directory, blobs, cancellationToken);

    /// <summary>The failure of an answer git gave that does not read as one.</summary>
    internal static HarnessException Malformed(WorkflowRemoteSource source, string what, string why)
        => new(HarnessExit.CommandFailed, $"'{source.Spelled}' could not be resolved: git answered {what} with what does not read as one ({why}); nothing was cached");

    private void Warn(string said) => output?.Warn(WorkflowRemoteFetcher.Said, said);

    /// <summary>
    /// The scratch directory a fetch is made in, made before anything is: git runs in it as its working directory, and a
    /// program is never started in a directory that is not there - neither the listing of refs nor the clone itself.
    /// </summary>
    /// <param name="source">The source whose repository is asked.</param>
    private ScratchDirectory Made(WorkflowRemoteSource source)
    {
        var scratch = new ScratchDirectory(fileSystem, "remote-workflow", Warn);

        try
        {
            fileSystem.CreateDirectory(scratch.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            scratch.Dispose();
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"the clone '{source.Owner}/{source.Repo}' is fetched into could not be made at '{scratch.Path}': {exception.Message}");
        }

        return scratch;
    }
}

/// <summary>
/// A partial clone of a source's repository, in a directory of its own removed when it is disposed: commits and tag
/// objects, and only the tree and the blobs of the pinned directory it was asked for by their ids.
/// </summary>
internal sealed class WorkflowClone : IDisposable
{
    /// <summary>What the clone fetches with, wherever it fetches: commits and tag objects, or what is asked for by its id.</summary>
    private const string CommitsAlone = "--filter=tree:0";

    /// <summary>What a fetch of a tree by its id holds: the tree and every tree under it, and no blob.</summary>
    private const string NoBlob = "--filter=blob:none";

    private readonly WorkflowGitFetch _fetch;
    private readonly WorkflowRemoteSource _source;
    private readonly GitCredential? _credential;
    private readonly ScratchDirectory _scratch;

    internal WorkflowClone(WorkflowGitFetch fetch, WorkflowRemoteSource source, GitCredential? credential, ScratchDirectory scratch)
    {
        _fetch = fetch;
        _source = source;
        _credential = credential;
        _scratch = scratch;
    }

    /// <summary>The clone's repository.</summary>
    private string Root => _scratch.Path;

    /// <summary>
    /// The object the remote's ref <paramref name="reference"/> named when the clone fetched it, by its whole name
    /// (<c>refs/tags/v1</c>, <c>refs/heads/main</c>); null where the clone holds no such ref.
    /// </summary>
    /// <param name="reference">The remote's ref, by its whole name.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public async Task<string?> FetchedAsync(string reference, CancellationToken cancellationToken)
    {
        var read = await _fetch.LocalAsync(Root, ["rev-parse", "--verify", "--quiet", Local(reference)], cancellationToken).ConfigureAwait(false);

        return read.Succeeded && read.StandardOutput.Trim() is { } id && WorkflowRemoteSource.IsCommit(id) ? id : null;
    }

    /// <summary>
    /// The object <paramref name="tag"/> - a tag object, or a commit a lightweight tag names - peels to: each tag object
    /// read for what it names, the ones read kept, until an object that is no tag. A chain of tags however deep is peeled
    /// whole, and one that closes on itself is refused where it closes.
    /// </summary>
    /// <param name="tag">The object a tag's ref names.</param>
    /// <param name="what">What names it, for a refusal: <c>the tag 'v1'</c>.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <returns>The object it peels to, and its type.</returns>
    /// <exception cref="HarnessException">A tag object does not read as one, or the chain closes on itself.</exception>
    public async Task<(string Object, string Type)> PeelAsync(string tag, string what, CancellationToken cancellationToken)
    {
        var start = (Object: tag, Type: await TypeAsync(tag, what, cancellationToken).ConfigureAwait(false));
        var end = await Chain.FollowAsync(
                start,
                async (member, token) =>
                {
                    if (member.Type != "tag")
                    {
                        return ChainNext<(string Object, string Type)>.End;
                    }

                    var read = await _fetch.LocalAsync(Root, ["cat-file", "tag", member.Object], token).ConfigureAwait(false);
                    var lines = read.Succeeded ? read.StandardOutput.Split('\n') : [];
                    var named = lines.FirstOrDefault(line => line.StartsWith("object ", StringComparison.Ordinal))?["object ".Length..];
                    var type = lines.FirstOrDefault(line => line.StartsWith("type ", StringComparison.Ordinal))?["type ".Length..];

                    return named is not null && WorkflowRemoteSource.IsCommit(named) && type is { Length: > 0 }
                        ? ChainNext<(string Object, string Type)>.To((named, type))
                        : throw WorkflowGitFetch.Malformed(_source, what, $"the tag object '{member.Object}' does not read as one");
                },
                member => member.Object,
                followed: null,
                cancellationToken)
            .ConfigureAwait(false);

        return end.Cycled
            ? throw new HarnessException(HarnessExit.Refused, $"'{_source.Spelled}': {what} names a chain of tags that comes back to the tag object '{end.RepeatedAt.Object}', and names no commit; nothing was cached")
            : end.Members[^1];
    }

    /// <summary>
    /// Whether <paramref name="commit"/> is one of the repository's own: one some branch or tag of it holds, as git's own
    /// walk over every ref answers it - each commit visited once, however many refs there are.
    /// </summary>
    /// <param name="commit">The commit, by its whole SHA.</param>
    /// <param name="cancellationToken">Stops git.</param>
    public async Task<bool> ReachedAsync(string commit, CancellationToken cancellationToken)
    {
        // A commit no ref reaches was never fetched: git then knows no such object, which is the answer too.
        var contained = await _fetch.LocalAsync(Root, ["for-each-ref", "--count=1", "--format=%(refname)", $"--contains={commit}", "refs/remotes/origin", "refs/tags"], cancellationToken)
            .ConfigureAwait(false);

        return contained.Succeeded && contained.StandardOutput.Trim().Length > 0;
    }

    /// <summary>
    /// Every entry under <paramref name="directory"/> at <paramref name="commit"/>, from git's own tree - whole, whatever
    /// it holds - the commit's tree first asked of the remote by its id, with no blob; null where the commit holds no such
    /// directory.
    /// </summary>
    /// <param name="commit">The commit, one the clone holds.</param>
    /// <param name="directory">The directory, forward slashes.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">The commit or its tree does not read as one, or its tree could not be fetched.</exception>
    public async Task<IReadOnlyList<WorkflowTreeEntry>?> TreeAsync(string commit, string directory, CancellationToken cancellationToken)
    {
        var read = await _fetch.LocalAsync(Root, ["cat-file", "commit", commit], cancellationToken).ConfigureAwait(false);
        var tree = read.Succeeded
            ? read.StandardOutput.Split('\n').FirstOrDefault(line => line.StartsWith("tree ", StringComparison.Ordinal))?["tree ".Length..]
            : null;

        if (tree is null || !WorkflowRemoteSource.IsCommit(tree))
        {
            throw WorkflowGitFetch.Malformed(_source, $"the commit '{commit}'", read.Succeeded ? "it names no tree" : read.StandardError.Trim());
        }

        await ObjectsAsync([tree], NoBlob, $"fetch the tree of the commit {commit}", cancellationToken).ConfigureAwait(false);

        var kind = await _fetch.LocalAsync(Root, ["cat-file", "-t", $"{commit}:{directory}"], cancellationToken).ConfigureAwait(false);

        if (!kind.Succeeded || kind.StandardOutput.Trim() != "tree")
        {
            return null;
        }

        var listed = await _fetch.LocalAsync(Root, ["ls-tree", "-r", "-t", "-z", "--full-tree", commit, "--", $"{directory}/"], cancellationToken).ConfigureAwait(false);

        if (!listed.Succeeded)
        {
            throw WorkflowGitFetch.Malformed(_source, $"the directory '{directory}'", listed.StandardError.Trim());
        }

        var entries = new List<WorkflowTreeEntry>();

        foreach (var record in listed.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<mode> SP <type> SP <object> TAB <path>".
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            var fields = tab < 0 ? [] : record[..tab].Split(' ');
            var path = tab < 0 ? "" : record[(tab + 1)..];

            // The trees the listing names on its way into the directory - each tree above it, and the directory
            // itself - are no entries of it: the directory's own stand under it, and only they are read as entries.
            if (fields.Length == 3 && fields[1] == "tree"
                && (path == directory || directory.StartsWith($"{path}/", StringComparison.Ordinal)))
            {
                continue;
            }

            if (fields.Length != 3 || !path.StartsWith($"{directory}/", StringComparison.Ordinal))
            {
                throw WorkflowGitFetch.Malformed(_source, $"the directory '{directory}'", $"'{record}' is no entry of it");
            }

            entries.Add(new WorkflowTreeEntry(path[(directory.Length + 1)..], fields[1], fields[0], fields[2]));
        }

        return entries;
    }

    /// <summary>
    /// The bytes of <paramref name="blobs"/>, the files of <paramref name="directory"/> at <paramref name="commit"/>, each by
    /// its id: asked of the remote by their ids in one transfer, then read as git stores them - nothing checked out.
    /// </summary>
    /// <param name="commit">The commit.</param>
    /// <param name="directory">The directory, forward slashes.</param>
    /// <param name="blobs">The ids of its blobs.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">The transfer failed, or a blob is not there after it.</exception>
    public async Task<IReadOnlyDictionary<string, byte[]>> ReadAsync(string commit, string directory, IReadOnlyList<string> blobs, CancellationToken cancellationToken)
    {
        await ObjectsAsync(blobs, NoBlob, $"fetch the files of '{directory}' at {commit}", cancellationToken).ConfigureAwait(false);

        var read = await _fetch.BlobsAsync(Root, blobs, cancellationToken).ConfigureAwait(false);
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        for (var at = 0; at < blobs.Count; at++)
        {
            bytes[blobs[at]] = read[at] ?? throw new HarnessException(HarnessExit.CommandFailed, $"'{_source.Spelled}': git fetched the files of '{directory}' at {commit}, and holds no blob '{blobs[at]}' of them; nothing was cached");
        }

        return bytes;
    }

    /// <summary>Removes the clone's directory.</summary>
    public void Dispose() => _scratch.Dispose();

    /// <summary>
    /// Makes the clone: a repository of its own, the source's address its one remote, and the commits and tag objects of
    /// <paramref name="reference"/> at its tip - or of every branch and tag with their history where it is null - fetched.
    /// </summary>
    internal async Task InitializeAsync(string? reference, CancellationToken cancellationToken)
    {
        foreach (var arguments in new[]
        {
            new[] { "init", "--quiet" },
            ["config", "remote.origin.url", _fetch.AddressOf(_source)],
            ["config", "remote.origin.promisor", "true"],
            ["config", "remote.origin.partialclonefilter", "tree:0"],
        })
        {
            var made = await _fetch.LocalAsync(Root, arguments, cancellationToken).ConfigureAwait(false);

            if (!made.Succeeded)
            {
                throw new HarnessException(HarnessExit.CommandFailed, $"git could not make the clone '{_source.Owner}/{_source.Repo}' is fetched into, at '{Root}': {made.StandardError.Trim()}");
            }
        }

        string[] refspecs = reference is null
            ? ["+refs/heads/*:refs/remotes/origin/*", "+refs/tags/*:refs/tags/*"]
            : [$"+{reference}:{Local(reference)}"];

        await _fetch.RemoteAsync(
                _source,
                Root,
                _credential,
                ["fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "--recurse-submodules=no", CommitsAlone, .. reference is null ? Array.Empty<string>() : ["--depth=1"], "origin", .. refspecs],
                reference is null ? $"fetch every branch and tag of '{_source.Owner}/{_source.Repo}'" : $"fetch '{reference}' of '{_source.Owner}/{_source.Repo}'",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the remote for <paramref name="objects"/> by their ids, in one transfer: what each holds under
    /// <paramref name="filter"/> comes with it, and every one asked for by its id is sent whatever the filter says.
    /// </summary>
    private async Task ObjectsAsync(IReadOnlyList<string> objects, string filter, string what, CancellationToken cancellationToken)
    {
        if (objects.Count == 0)
        {
            return;
        }

        // The ids are read from the standard input, which no command line's length bounds; and nothing is said of what
        // the clone holds already, which a fetch by id has no use for.
        await _fetch.RemoteAsync(
                _source,
                Root,
                _credential,
                ["-c", "fetch.negotiationAlgorithm=noop", "fetch", "--quiet", "--no-tags", "--no-write-fetch-head", "--recurse-submodules=no", filter, "--stdin", "origin"],
                what,
                cancellationToken,
                string.Concat(objects.Select(id => id + "\n")))
            .ConfigureAwait(false);
    }

    /// <summary>The ref the clone keeps the remote's ref <paramref name="reference"/> as: a branch under the remote's own name, a tag as itself.</summary>
    private static string Local(string reference)
        => reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? $"refs/remotes/origin/{reference["refs/heads/".Length..]}" : reference;

    /// <summary>The type of the object <paramref name="id"/>, as the clone holds it.</summary>
    private async Task<string> TypeAsync(string id, string what, CancellationToken cancellationToken)
    {
        var typed = await _fetch.LocalAsync(Root, ["cat-file", "-t", id], cancellationToken).ConfigureAwait(false);

        return typed.Succeeded && typed.StandardOutput.Trim() is { Length: > 0 } type
            ? type
            : throw WorkflowGitFetch.Malformed(_source, what, $"the object '{id}' it names is not there");
    }
}
