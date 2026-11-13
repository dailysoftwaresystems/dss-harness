using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What the workflow cache holds, and its pruning: the verbs' answer, said as lines or as JSON.</summary>
/// <param name="Listed">Each entry, one line: where it came from, its commit, its files and bytes, when it was fetched and last used.</param>
/// <param name="PrunedEntries">How many entries the pruning removed.</param>
/// <param name="PrunedBlobs">How many blobs the pruning removed, that no entry left held.</param>
public sealed record WorkflowCacheReport(IReadOnlyList<string> Listed, int? PrunedEntries = null, int? PrunedBlobs = null);

/// <summary>
/// Reads and prunes the hash cache of remote workflows: <c>workflow-cache list</c> says each entry the cache holds, and
/// <c>workflow-cache prune</c> removes the entries no run used for its days and the blobs no entry left holds.
/// </summary>
public interface IWorkflowCacheService : IUseCase
{
    /// <summary>
    /// Lists or prunes the workflow cache, as <paramref name="unusedDays"/> says: null lists, a number prunes the
    /// entries no run used for that many days.
    /// </summary>
    /// <param name="unusedDays">The days an entry may go unused before it is pruned; null to list.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    Task<WorkflowCacheReport> RunAsync(int? unusedDays, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IWorkflowCacheService"/>
public sealed class WorkflowCacheService(WorkflowCache cache, TimeProvider clock) : IWorkflowCacheService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "workflow-cache";

    private readonly WorkflowCache _cache = cache;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc/>
    public Task<WorkflowCacheReport> RunAsync(int? unusedDays, CancellationToken cancellationToken = default)
    {
        if (unusedDays is { } days)
        {
            var (entries, blobs) = _cache.Prune(days, _clock.GetUtcNow());

            return Task.FromResult(new WorkflowCacheReport([], entries, blobs));
        }

        return Task.FromResult(new WorkflowCacheReport([.. _cache.List().Select(EntryLine)]));
    }

    /// <summary>One entry, one line, as list says it: where it came from, its commit, its files and bytes, and when it was last used.</summary>
    public static string EntryLine((WorkflowCacheEntry Entry, long Bytes) listed)
    {
        var (entry, bytes) = listed;

        return $"{entry.Repository} at {entry.Commit[..12]}: {entry.Path} ({ReportText.Counted(entry.Files.Count, "file")}, {bytes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes, fetched {entry.FetchedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}, last used {entry.LastUsedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}"
            + (entry.AskedFor is { } askedFor ? $", asked for {askedFor}" : string.Empty)
            + ")";
    }
}

/// <summary>Turns what workflow-cache did into what it prints.</summary>
public static class WorkflowCacheReports
{
    /// <summary>What workflow-cache reports.</summary>
    /// <param name="report">What was read or pruned.</param>
    /// <param name="json">Whether to write the result as JSON instead of as lines.</param>
    public static CommandOutcome Render(WorkflowCacheReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.PrunedEntries is { } entries)
        {
            var data = json ? [Document(report).ToJsonString(ReportJson.Options)] : Array.Empty<string>();

            return CommandOutcome.Ok($"pruned {ReportText.Counted(entries, "entry")} and {ReportText.Counted(report.PrunedBlobs ?? 0, "blob")}") with
            {
                Data = data,
                Quiet = json,
            };
        }

        return (report.Listed.Count == 0
            ? CommandOutcome.Ok("the workflow cache holds no entry; a remote workflow a use names fills it")
            : CommandOutcome.Ok($"the workflow cache holds {ReportText.Counted(report.Listed.Count, "entry")}")) with
        {
            Data = json ? [Document(report).ToJsonString(ReportJson.Options)] : [],
            Quiet = json,
            Details = json ? null : report.Listed,
        };
    }

    /// <summary>
    /// The document workflow-cache answers with where it stops before it read anything: its own, nothing listed or
    /// pruned, after the code it exits with and the line it ends on.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary)
        => ReportJson.Stopped(exitCode, summary, Document(report: null));

    private static JsonObject Document(WorkflowCacheReport? report)
        => new()
        {
            ["entries"] = report is null ? null : new JsonArray([.. report.Listed.Select(line => (JsonNode)line!)]),
            ["prunedEntries"] = report?.PrunedEntries,
            ["prunedBlobs"] = report?.PrunedBlobs,
        };
}
