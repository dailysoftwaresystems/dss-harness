using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What the workflow cache holds, and its pruning: the verbs' answer, said as lines or as JSON.</summary>
/// <param name="Directory">The cache's own directory, where its entries, blobs and resolutions land.</param>
/// <param name="Listed">Each entry, one line: where it came from, its commit, its files and bytes, when it was fetched and last used, and the refs whose newest resolution it is.</param>
/// <param name="Unread">Each file of the cache's own that could not be read, and why.</param>
/// <param name="Pruning">What the pruning removed and could not; null for a listing.</param>
public sealed record WorkflowCacheReport(string Directory, IReadOnlyList<string> Listed, IReadOnlyList<WorkflowCacheUnread> Unread, WorkflowCachePruning? Pruning = null);

/// <summary>
/// Reads and prunes the hash cache of remote workflows: <c>workflow-cache list</c> says each entry the cache holds, and
/// <c>workflow-cache prune</c> removes the entries no run used for its days, the resolutions that named them and the
/// blobs no entry left holds.
/// </summary>
public interface IWorkflowCacheService : IUseCase
{
    /// <summary>Lists the workflow cache: every entry it holds, and every file of its own it could not read.</summary>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="HarnessException">No directory of this user's own could be named (<see cref="HarnessExit.Refused"/>).</exception>
    Task<WorkflowCacheReport> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Prunes the entries no run used, and none was fetched, for <paramref name="unusedDays"/> days.</summary>
    /// <param name="unusedDays">The days an entry may go unused before it is pruned, 0 to <see cref="WorkflowCache.MostUnusedDays"/>.</param>
    /// <param name="cancellationToken">Stops the pruning before it begins.</param>
    /// <exception cref="HarnessException">
    /// The days are below 0 or past <see cref="WorkflowCache.MostUnusedDays"/> (<see cref="HarnessExit.UsageError"/>), with
    /// nothing removed; no directory of this user's own could be named (<see cref="HarnessExit.Refused"/>).
    /// </exception>
    Task<WorkflowCacheReport> PruneAsync(int unusedDays, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWorkflowCacheService"/>
public sealed class WorkflowCacheService(WorkflowCache cache, TimeProvider clock) : IWorkflowCacheService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "workflow-cache";

    private readonly WorkflowCache _cache = cache;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc/>
    public Task<WorkflowCacheReport> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var listing = _cache.List();

        return Task.FromResult(new WorkflowCacheReport(_cache.Directory, [.. listing.Entries.Select(EntryLine)], listing.Unread));
    }

    /// <inheritdoc/>
    public Task<WorkflowCacheReport> PruneAsync(int unusedDays, CancellationToken cancellationToken = default)
    {
        if (unusedDays is < 0 or > WorkflowCache.MostUnusedDays)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"--unused-days is {unusedDays.ToString(CultureInfo.InvariantCulture)}; it is a number of days from 0 to {WorkflowCache.MostUnusedDays.ToString("N0", CultureInfo.InvariantCulture)}. Nothing was removed from the workflow cache"
                    + (WorkflowCache.DefaultDirectoryIfNamed() is { } directory ? $" '{directory}'." : "."));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var pruning = _cache.Prune(unusedDays, _clock.GetUtcNow());

        return Task.FromResult(new WorkflowCacheReport(_cache.Directory, [], pruning.Unread, pruning));
    }

    /// <summary>
    /// One entry, one line, as list says it: where it came from, its commit, its files and bytes - and the blobs of it
    /// not there, where any are not - when it was fetched and last used, and the refs whose newest resolution it is.
    /// </summary>
    /// <param name="listed">The entry, as the cache lists it.</param>
    public static string EntryLine(WorkflowCacheListed listed)
    {
        ArgumentNullException.ThrowIfNull(listed);

        var entry = listed.Entry;

        return $"{entry.Repository} at {entry.Commit[..12]}: {entry.Path} ({ReportText.Counted(entry.Files.Count, "file")}, {listed.Bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes"
            + (listed.Missing > 0 ? $", {ReportText.Counted(listed.Missing, "blob")} of it not there - it is fetched again where a use resolves to it" : string.Empty)
            + $", fetched {Day(entry.FetchedAt)}, last used {Day(entry.LastUsedAt)}"
            + string.Concat(listed.Resolutions.Select(resolution => $", the newest resolution of {(resolution.Branch ? "the branch" : "the tag")} {resolution.Ref} on {Day(resolution.ResolvedAt)}"))
            + ")";
    }

    private static string Day(DateTimeOffset when) => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Turns what workflow-cache did into what it prints.</summary>
public static class WorkflowCacheReports
{
    /// <summary>
    /// What workflow-cache reports, naming the cache's directory on every line it ends on: a listing with every file it
    /// could not read beneath it, and a pruning that did all it was to - one that could not remove or read a file of the
    /// cache's own is not done, and says each (<see cref="HarnessExit.Incomplete"/>).
    /// </summary>
    /// <param name="report">What was read or pruned.</param>
    /// <param name="json">Whether to write the result as JSON instead of as lines.</param>
    public static CommandOutcome Render(WorkflowCacheReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        var unread = report.Unread.Select(file => $"could not be read: '{file.File}': {file.Why}");

        if (report.Pruning is { } pruning)
        {
            var notRemoved = pruning.NotRemoved.Select(file => $"could not be removed: '{file.File}': {file.Why}");
            var pruned = $"pruned {ReportText.Counted(pruning.Entries, "entry")} and {ReportText.Counted(pruning.Blobs, "blob")} from the workflow cache '{report.Directory}'";
            var outcome = pruning.Whole
                ? CommandOutcome.Ok(pruned)
                : CommandOutcome.Failed(
                    HarnessExit.Incomplete,
                    $"{pruned}, and did not finish: "
                        + string.Join(
                            ", and ",
                            new[]
                            {
                                pruning.NotRemoved.Count == 0 ? null : $"{ReportText.Counted(pruning.NotRemoved.Count, "file")} could not be removed",
                                pruning.Unread.Count == 0 ? null : $"{ReportText.Counted(pruning.Unread.Count, "file")} of its own could not be read, so no blob was removed",
                            }.OfType<string>())
                        + "; run it again once they can be",
                    [.. notRemoved, .. unread]);

            return outcome with
            {
                Data = json ? [Document(report.Directory, report).ToJsonString(ReportJson.Options)] : [],
                Quiet = json,
                Details = json ? null : outcome.Details,
            };
        }

        var holds = report.Listed.Count == 0
            ? $"the workflow cache '{report.Directory}' holds no entry; a remote workflow a use names fills it"
            : $"the workflow cache '{report.Directory}' holds {ReportText.Counted(report.Listed.Count, "entry")}";

        return CommandOutcome.Ok(report.Unread.Count == 0 ? holds : $"{holds}, and {ReportText.Counted(report.Unread.Count, "file")} of its own could not be read") with
        {
            Data = json ? [Document(report.Directory, report).ToJsonString(ReportJson.Options)] : [],
            Quiet = json,
            Details = json ? null : [.. report.Listed, .. unread],
        };
    }

    /// <summary>
    /// The document workflow-cache answers with where it stops before it read anything: its own, nothing listed or
    /// pruned, the cache's directory where one can be named, after the code it exits with and the line it ends on.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary)
        => ReportJson.Stopped(exitCode, summary, Document(WorkflowCache.DefaultDirectoryIfNamed(), report: null));

    private static JsonObject Document(string? directory, WorkflowCacheReport? report)
        => new()
        {
            ["cache"] = directory,
            ["entries"] = report is null || report.Pruning is not null ? null : new JsonArray([.. report.Listed.Select(line => (JsonNode)line!)]),
            ["unread"] = report is null ? null : Files(report.Unread),
            ["prunedEntries"] = report?.Pruning?.Entries,
            ["prunedBlobs"] = report?.Pruning?.Blobs,
            ["notRemoved"] = report?.Pruning is { } pruning ? Files(pruning.NotRemoved) : null,
        };

    private static JsonArray Files(IReadOnlyList<WorkflowCacheUnread> files)
        => new([.. files.Select(file => (JsonNode)new JsonObject { ["file"] = file.File, ["why"] = file.Why })]);
}
