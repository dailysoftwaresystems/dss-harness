using System.Text.Json.Nodes;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A remote workflow a use resolved to: its source as written, the commit the ref resolved to, the cache entry its
/// directory lives in, its own yml, and how the resolution came about - whether a moving ref was allowed and used,
/// and whether the network was not there and the cache's last resolution stood in.
/// </summary>
/// <param name="Source">The source as the use wrote it.</param>
/// <param name="Commit">The commit the ref resolved to, 40 hex characters.</param>
/// <param name="Entry">The cache entry holding the workflow's directory at that commit.</param>
/// <param name="Definition">The workflow's own yml.</param>
/// <param name="Moving">Whether the ref was a branch, allowed by <c>pin: any</c>, which moves.</param>
/// <param name="FromLastResolved">Whether the network was not there and the cache's last resolution was used.</param>
public sealed record WorkflowRemoteResolution(
    WorkflowRemoteSource Source,
    string Commit,
    WorkflowCacheEntry Entry,
    string Definition,
    bool Moving = false,
    bool FromLastResolved = false);

/// <summary>
/// The one way a remote workflow is fetched: a ref resolved to a commit, the commit's tree read, every blob under
/// the workflow's directory fetched and checked against the tree's hash of it, and the whole kept in the workflow
/// cache - read from it with no network where the commit is cached already. A remote source is trusted by
/// configuration alone: the repository's <c>workflowSources</c> allows its owner, names its host, pins the refs it
/// may name, and says what happens offline; nothing of that is decided by a response.
/// </summary>
/// <param name="gh">Runs the forge's tool, <c>gh api</c> among its calls.</param>
/// <param name="cache">The workflow cache the fetch is kept in.</param>
public sealed class WorkflowRemoteFetcher(GhProcess gh, WorkflowCache cache)
{
    private readonly GhProcess _gh = gh;
    private readonly WorkflowCache _cache = cache;

    /// <summary>
    /// Resolves the remote source a use names to a commit and a cached directory: the commit the run's own record
    /// says where it has one (<paramref name="recorded"/>, so a resume never resolves again, and a tag or branch
    /// that moved since changes no run already started), the cache where that commit is held, and the network where
    /// it is not - refused, or the cache's last resolution where the configuration says <c>last-resolved</c>.
    /// </summary>
    /// <param name="source">The source a step's <c>use:</c> names, parsed.</param>
    /// <param name="settings">The repository's <c>workflowSources</c>; null where the file holds none.</param>
    /// <param name="workingDirectory">The tree the forge's tool is run in.</param>
    /// <param name="environment">Reads this process's environment, for a host's credential.</param>
    /// <param name="recorded">The commit this run's own record holds for the source, where one does; null else.</param>
    /// <param name="cancellationToken">Stops the fetch.</param>
    /// <exception cref="HarnessException">
    /// The source's repository is not allowed, its host is not named, its ref is not one the pin takes, or the
    /// network is not there and nothing stands in (<see cref="HarnessExit.Refused"/>); the forge could not be
    /// reached or answered nothing that reads (<see cref="HarnessExit.CommandFailed"/> or the tool's own codes).
    /// </exception>
    public async Task<WorkflowRemoteResolution> ResolveAsync(
        WorkflowRemoteSource source,
        WorkflowSourcesConfig? settings,
        string workingDirectory,
        Func<string, string?> environment,
        string? recorded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);

        Trusted(source, settings);

        // A run's own record of the resolution: the commit it began with, used again however the ref has moved, so a
        // resume is refused by nothing it cannot help.
        if (recorded is { Length: 40 })
        {
            return await Read(source, settings, workingDirectory, environment, recorded, moving: false, cancellationToken).ConfigureAwait(false);
        }

        if (WorkflowRemoteSource.IsCommit(source.Ref))
        {
            return await Read(source, settings, workingDirectory, environment, source.Ref, moving: false, cancellationToken).ConfigureAwait(false);
        }

        Pin(source, WorkflowSourceRules.PinOf(settings));

        return await ResolveRefAsync(source, settings, workingDirectory, environment, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the configuration says of <paramref name="source"/>: that its repository is allowed and its host named.
    /// Nothing else trusts a remote source, for a workflow it brings directs agents and machine steps in the calling
    /// repository.
    /// </summary>
    private static void Trusted(WorkflowRemoteSource source, WorkflowSourcesConfig? settings)
    {
        if (!source.Host.Equals(WorkflowSourceRules.GitHub, StringComparison.OrdinalIgnoreCase)
            && !(settings?.Hosts ?? new Dictionary<string, WorkflowHostConfig>(StringComparer.OrdinalIgnoreCase)).ContainsKey(source.Host))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' names the host '{source.Host}', which the configuration does not: workflowSources.hosts names the GitHub Enterprise hosts a use may reach, and none names this one");
        }

        if (!WorkflowSourceRules.Allows(settings?.Allow ?? [], source.Host, source.Owner, source.Repo))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' names the repository '{source.Owner}/{source.Repo}', which workflowSources.allow does not allow; a remote workflow directs agents and machine steps in this repository, so it is trusted by configuration alone"
                    + (settings is null || settings.Allow.Count == 0
                        ? "; the allow list is empty, so no remote source is allowed until the repository allows its owner"
                        : $": {string.Join(", ", settings.Allow)}"));
        }
    }

    /// <summary>
    /// That <paramref name="source"/>'s ref is one the pin takes: a full commit SHA always; a tag or a branch where
    /// the pin is not <c>commit</c> - and the branch, which moves, is told from the tag by the resolution, which allows
    /// it only where the pin says <c>any</c>.
    /// </summary>
    private static void Pin(WorkflowRemoteSource source, string pin)
    {
        if (pin != WorkflowSourceRules.PinCommit)
        {
            return;
        }

        throw new HarnessException(
            HarnessExit.Refused,
            $"'{source.Spelled}' names the ref '{source.Ref}', which is no full commit SHA; workflowSources.pin is commit, which takes a full commit SHA only - a tag or a branch moves, and a commit is what it is");
    }

    /// <summary>
    /// The commit a ref resolves to, with the tag told from the branch: a tag is asked as the ref it is, and a branch
    /// - allowed only by <c>pin: any</c> - through the commit the forge answers for it. Where the network is not
    /// there, the configuration's <c>offline</c> says what stands in.
    /// </summary>
    private async Task<WorkflowRemoteResolution> ResolveRefAsync(
        WorkflowRemoteSource source,
        WorkflowSourcesConfig? settings,
        string workingDirectory,
        Func<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var moving = false;
        string commit;

        try
        {
            // A tag is asked as the ref it is, for the commit it points at; a branch answers no such ask, and is told
            // from the tag by that alone - allowed only where the pin says any, for a branch moves.
            var tag = await ApiAsync(
                source,
                settings,
                workingDirectory,
                environment,
                [$"repos/{source.Owner}/{source.Repo}/git/ref/tags/{source.Ref}", "--jq", ".object.sha"],
                cancellationToken).ConfigureAwait(false);

            if (tag is null)
            {
                if (WorkflowSourceRules.PinOf(settings) != WorkflowSourceRules.PinAny)
                {
                    throw new HarnessException(
                        HarnessExit.Refused,
                        $"'{source.Spelled}' names the ref '{source.Ref}', which is no tag of '{source.Owner}/{source.Repo}'; workflowSources.pin is {WorkflowSourceRules.PinOf(settings)}, which takes a tag or a full commit SHA - a branch moves, and what a run ran must be what it said");
                }

                moving = true;

                commit = Sha((await ApiAsync(
                    source,
                    settings,
                    workingDirectory,
                    environment,
                    [$"repos/{source.Owner}/{source.Repo}/commits/{source.Ref}", "--jq", ".sha"],
                    cancellationToken).ConfigureAwait(false)) ?? throw NotRead(source));
            }
            else
            {
                commit = Sha(tag);
            }
        }
        catch (HarnessException failed) when (failed.ExitCode != HarnessExit.Refused)
        {
            // The forge could not be reached, or answered nothing that reads: offline, the configuration may say the
            // cache's last resolution stands in, and the run records that it did. A refusal is not that - the pin and
            // the trust have already said their word.
            if (LastResolved(source, settings, failed) is { } last)
            {
                return last;
            }

            var offline = WorkflowSourceRules.OfflineOf(settings);

            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' could not be resolved: {failed.Message.TrimEnd('.')}"
                    + (offline == WorkflowSourceRules.OfflineLastResolved
                        ? "; and the workflow cache holds no last resolution of it"
                        : "; workflowSources.offline is refuse, so nothing stands in - last-resolved would use the cache's last resolution, which the run records that it did"),
                failed);
        }

        return await Read(source, settings, workingDirectory, environment, commit, moving, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The cache's last resolution of <paramref name="source"/>'s ref, where the configuration says one stands in;
    /// null where it says nothing stands in, or the cache holds none - and the refusal that reached here is the one
    /// the caller sees.
    /// </summary>
    private WorkflowRemoteResolution? LastResolved(WorkflowRemoteSource source, WorkflowSourcesConfig? settings, HarnessException failed)
    {
        if (WorkflowSourceRules.OfflineOf(settings) != WorkflowSourceRules.OfflineLastResolved)
        {
            return null;
        }

        if (_cache.LastResolved(source.Host, source.Owner, source.Repo, source.Directory, source.Ref) is not { } last)
        {
            return null;
        }

        var cached = Cached(source, last) ?? throw new HarnessException(
            HarnessExit.Refused,
            $"'{source.Spelled}': the network was not there, and the workflow cache's last resolution of '{source.Ref}' is the commit '{last.Commit}', whose files are not whole in the cache; it is fetched again where the network is",
            failed);

        _cache.Touch(last);

        return cached with { FromLastResolved = true };
    }

    /// <summary>
    /// The workflow at <paramref name="commit"/>, from the cache where it is held - read with no network, its blobs
    /// hashed again as they are read, and where one fails to hash, fetched again whole - else fetched, checked and
    /// cached.
    /// </summary>
    private async Task<WorkflowRemoteResolution> Read(
        WorkflowRemoteSource source,
        WorkflowSourcesConfig? settings,
        string workingDirectory,
        Func<string, string?> environment,
        string commit,
        bool moving,
        CancellationToken cancellationToken)
    {
        if (_cache.Find(source.Host, source.Owner, source.Repo, commit, source.Directory) is { } entry
            && Cached(source, entry) is { } cached)
        {
            _cache.Touch(cached.Entry);

            return cached with { Moving = moving };
        }

        var fetched = await FetchAsync(source, commit, settings, workingDirectory, environment, cancellationToken).ConfigureAwait(false);

        _cache.Touch(fetched.Entry);

        return fetched with { Moving = moving };
    }

    /// <summary>
    /// The workflow's directory at <paramref name="commit"/>: every blob of the repository's tree under it, each
    /// checked against the hash the tree gives, the whole bounded - and kept in the cache only once every check
    /// holds, so a mismatch or a bound refuses with nothing cached.
    /// </summary>
    private async Task<WorkflowRemoteResolution> FetchAsync(
        WorkflowRemoteSource source,
        string commit,
        WorkflowSourcesConfig? settings,
        string workingDirectory,
        Func<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var tree = JsonNode.Parse(await ApiAsync(
            source,
            settings,
            workingDirectory,
            environment,
            [$"repos/{source.Owner}/{source.Repo}/git/trees/{commit}?recursive=1"],
            cancellationToken).ConfigureAwait(false) ?? throw NotRead(source))!.AsObject();

        var under = $"{source.Directory}/";
        var blobs = new List<(string Path, string Blob, string Mode, long Size)>();

        foreach (var entry in (tree["tree"] as JsonArray) ?? [])
        {
            var path = entry!["path"]!.GetValue<string>();
            var kind = entry["type"]?.GetValue<string>();

            if (!path.StartsWith(under, StringComparison.Ordinal) || kind != "blob")
            {
                continue;
            }

            blobs.Add((path, entry["sha"]!.GetValue<string>(), entry["mode"]?.GetValue<string>() ?? "100644", entry["size"]?.GetValue<long>() ?? 0));
        }

        if (blobs.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' resolved to the commit '{commit}', whose tree holds no directory '{source.Directory}'");
        }

        if (blobs.Count > WorkflowCache.MostFiles)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' resolved to a directory of {blobs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} files, past the fetch's bound of {WorkflowCache.MostFiles.ToString(System.Globalization.CultureInfo.InvariantCulture)}; nothing was fetched");
        }

        var bytes = blobs.Sum(blob => blob.Size);

        if (bytes > WorkflowCache.MostBytes)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' resolved to a directory of {bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} bytes, past the fetch's bound of {WorkflowCache.MostBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}; nothing was fetched");
        }

        var contents = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var blob in blobs)
        {
            var fetched = JsonNode.Parse(await ApiAsync(
                source,
                settings,
                workingDirectory,
                environment,
                [$"repos/{source.Owner}/{source.Repo}/git/blobs/{blob.Blob}"],
                cancellationToken).ConfigureAwait(false) ?? throw NotRead(source))!.AsObject();

            var encoding = fetched["encoding"]?.GetValue<string>();
            var content = fetched["content"]?.GetValue<string>() ?? string.Empty;
            var text = encoding == "base64" ? FromBase64(content) : content;

            if (WorkflowCache.BlobSha(text) != blob.Blob)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{source.Spelled}': the blob '{blob.Blob}' of '{blob.Path}' does not hash to what the commit's tree says of it; nothing was cached, and the fetch is made again");
            }

            contents[blob.Path] = text;
        }

        var written = new WorkflowCacheEntry(
            source.Host,
            source.Owner,
            source.Repo,
            commit,
            source.Directory,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            WorkflowRemoteSource.IsCommit(source.Ref) ? null : source.Ref,
            [.. blobs.Select(blob => new WorkflowCacheFile(blob.Path, blob.Blob, blob.Mode))]);

        _cache.Write(written, contents);

        return new WorkflowRemoteResolution(source, commit, written, contents[source.File]);
    }

    /// <summary>The cached resolution of <paramref name="source"/> at its entry's commit, its files hashed again as they are read; null where the cache's files are not whole.</summary>
    private WorkflowRemoteResolution? Cached(WorkflowRemoteSource source, WorkflowCacheEntry entry)
    {
        try
        {
            var definition = _cache.ReadText(entry, source.File) ?? throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"'{source.Spelled}': the cache's entry for '{source.Repository}' at '{entry.Commit}' names no '{source.File}'");

            return new WorkflowRemoteResolution(source, entry.Commit, entry, definition);
        }
        catch (HarnessException)
        {
            // A cache file changed on disk is refused, and the directory fetched again.
            return null;
        }
    }

    /// <summary>One SHA as <c>gh api --jq</c> answers it - a JSON string, quoted - trimmed to its hex, lower case.</summary>
    private static string Sha(string answered) => answered.Trim().Trim('"').ToLowerInvariant();

    /// <summary>Base64 as the forge writes it: padded, with the newlines its JSON holds them over stripped.</summary>
    private static string FromBase64(string content)
        => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", string.Empty, StringComparison.Ordinal)));

    /// <summary>
    /// One <c>gh api</c> call, its answer whole, or null where the forge answered that the thing asked is not there -
    /// a missing tag, which the next call tells from a missing repository. The host's credential, where one is named,
    /// is handed to the call as its environment and written nowhere else.
    /// </summary>
    private async Task<string?> ApiAsync(
        WorkflowRemoteSource source,
        WorkflowSourcesConfig? settings,
        string workingDirectory,
        Func<string, string?> environment,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var with = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (!source.Host.Equals(WorkflowSourceRules.GitHub, StringComparison.OrdinalIgnoreCase)
            && (settings?.Hosts ?? new Dictionary<string, WorkflowHostConfig>(StringComparer.OrdinalIgnoreCase)).TryGetValue(source.Host, out var host)
            && host?.Credential is { From: "env", Name: { Length: > 0 } name })
        {
            with["GH_TOKEN"] = environment(name);
        }

        var forwarded = source.Host.Equals(WorkflowSourceRules.GitHub, StringComparison.OrdinalIgnoreCase)
            ? arguments
            : ["--hostname", source.Host, .. arguments];

        try
        {
            var answer = await _gh.RunAsync(
                workingDirectory,
                ["api", .. forwarded],
                $"so '{source.Spelled}' was not resolved",
                null,
                cancellationToken,
                withEnvironment: with).ConfigureAwait(false);

            return answer.Trim().Length == 0 ? null : answer;
        }
        catch (HarnessException failed) when (failed.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private static HarnessException NotRead(WorkflowRemoteSource source)
        => new(
            HarnessExit.CommandFailed,
            $"'{source.Spelled}' could not be resolved: the forge answered nothing that reads for it, so no workflow was fetched");
}
