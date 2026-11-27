using System.Text.Json.Nodes;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The files one issue the recovery is fixing holds, and which of them a work item owns - the one owner of both (part 04,
/// "Pre-existing issues"). From the moment a triage sends an issue to fix until the recovery's visit for it ends, the
/// issue holds the files its triage's <c>where</c> names - each entry that is a path from the repository's root, never
/// the command an issue may be in instead - and, once a check of that visit has read the remedy items its diagnosis gives,
/// every path those items own. An issue joined to one still being triaged holds no file yet - the issue it joined holds
/// none before its triage sends it to fix - and is held under that triage. A work item that owns any of the files, or
/// that names the issue among those whose fix it needs (<c>needs</c>), and has not begun starts once that visit has ended
/// - one that needs an issue joined to another still being triaged once that triage has ended, and then the visit of the
/// fix it goes with - so it builds on the tree the fix leaves and never writes the fix's lines beside it; every other
/// goes on at once. Every path is read from the repository's root, as the
/// plan's check reads an item's (<see cref="GlobPattern.FromRoot"/>): a root-level file a fix holds meets the item that
/// owns that file, and no other. The engine says which issues are being fixed and when an item has begun; this says
/// what each holds and what an item meets.
/// </summary>
public sealed class FlowIssueHold
{
    /// <summary>
    /// How a path an issue holds and a path an item owns are compared: with case folded, whatever the tree's own rule. A
    /// hold wider than the tree's case rule only keeps an item waiting for the fix; one narrower would let it build the
    /// fix's lines beside it on a file system that folds case.
    /// </summary>
    private const StringComparison Comparison = StringComparison.OrdinalIgnoreCase;

    private readonly IReadOnlyList<(string Text, GlobPattern Pattern)> _held;
    private readonly Func<string, bool> _directories;

    private FlowIssueHold(string issue, string title, string fixer, IReadOnlyList<(string Text, GlobPattern Pattern)> held, Func<string, bool> directories)
    {
        Issue = issue;
        Title = title;
        Fixer = fixer;
        _held = held;
        _directories = directories;
    }

    /// <summary>The issue's id, as <c>{run.preExisting}</c> words it - its triage's - which a work item that needs its fix names.</summary>
    public string Issue { get; }

    /// <summary>The issue's title, as its triage gave it: what a step held for it says it waits for.</summary>
    public string Title { get; }

    /// <summary>The id of what fixes it: the recovery's visit at work on it, the triage that sent it while that visit waits to be made, or - for an issue joined to one still being triaged - that one's triage.</summary>
    public string Fixer { get; }

    /// <summary>Every path it holds, as written: each path of its triage's <c>where</c>, then each pattern a remedy item of its visit owns.</summary>
    public IReadOnlyList<string> Paths => [.. _held.Select(held => held.Text)];

    /// <summary>
    /// What an issue being fixed holds: each entry of <paramref name="where"/> that is a path (<see cref="IsPath"/>), and
    /// each pattern every item of <paramref name="remedies"/> owns, as the item writes it - each once, in that order, each
    /// read from the repository's root. A pattern a remedy item owns that does not read as one holds nothing, as the plan's
    /// own check refuses it.
    /// </summary>
    /// <param name="issue">The issue's id.</param>
    /// <param name="title">The issue's title.</param>
    /// <param name="fixer">The id of what fixes it.</param>
    /// <param name="where">The triage's <c>where</c>: a list of texts, or one text; anything else names nothing - null for an issue joined to one still being triaged, which holds no file yet.</param>
    /// <param name="remedies">The remedy items the checks of its visit read; none before its diagnosis is checked.</param>
    /// <param name="directories">
    /// Whether a path from the repository's root names a directory of the run's start tree, where the question is whether
    /// two entries meet (<see cref="TreeDirectories.Meeting"/>): a path this holds, and one an item owns, holds what is
    /// beneath it only where it does.
    /// </param>
    public static FlowIssueHold Of(string issue, string title, string fixer, JsonNode? where, IEnumerable<JsonNode?> remedies, Func<string, bool> directories)
    {
        ArgumentNullException.ThrowIfNull(remedies);
        ArgumentNullException.ThrowIfNull(directories);

        var held = new List<(string Text, GlobPattern Pattern)>();
        IEnumerable<string?> entries = where switch
        {
            JsonArray list => list.Select(Text),
            JsonValue => [Text(where)],
            _ => [],
        };

        foreach (var entry in entries.OfType<string>().Where(IsPath))
        {
            // Spelled as the hold says it, from the root, a trailing separator kept where it says the path names a directory.
            var path = PathPatterns.Normalize(entry) + (entry.EndsWith('/') || entry.EndsWith('\\') ? "/" : string.Empty);
            held.Add((path, GlobPattern.FromRoot(path, directories)));
        }

        held.AddRange(remedies.SelectMany(remedy => OwnedOf(remedy, directories)));

        return new FlowIssueHold(issue, title, fixer, [.. held.DistinctBy(entry => entry.Text, StringComparer.Ordinal)], directories);
    }

    /// <summary>
    /// Whether <paramref name="entry"/>, one entry of a triage's <c>where</c>, is a path from the repository's root rather
    /// than the command the issue is in: a path relative to the tree (<see cref="OrchestrationRules.RelativePathProblem"/>),
    /// holding no wildcard and no whitespace - a command carries its arguments after a space, and a pattern names no one
    /// file. Only a path holds a file.
    /// </summary>
    /// <param name="entry">The entry, as the triage wrote it.</param>
    public static bool IsPath(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Length > 0
            && !entry.Any(char.IsWhiteSpace)
            && !GlobPattern.IsPattern(entry)
            && OrchestrationRules.RelativePathProblem(entry.TrimEnd('/', '\\'), "the path") is null;
    }

    /// <summary>
    /// The paths this holds that the work item <paramref name="item"/> owns - each held path some entry of its
    /// <c>ownedPaths</c> meets (<see cref="GlobPattern.Overlaps"/>), both read from the root - in the order this holds
    /// them; empty where it owns none of them, and the item starts at once.
    /// </summary>
    /// <param name="item">The work item, as its instance of the work-item group holds it.</param>
    public IReadOnlyList<string> Meeting(JsonNode? item)
    {
        var owned = OwnedOf(item, _directories).Select(entry => entry.Pattern).ToList();

        return [.. _held.Where(held => owned.Any(mine => mine.Overlaps(held.Pattern, Comparison))).Select(held => held.Text)];
    }

    /// <summary>
    /// Whether the work item <paramref name="item"/> names this issue among those whose fix it needs
    /// (<see cref="FlowWorkItems.NeedsOf"/>): it builds on what the fix makes, whatever files it owns, so it starts once
    /// the fix has ended.
    /// </summary>
    /// <param name="item">The work item, as its instance of the work-item group holds it.</param>
    public bool NeededBy(JsonNode? item) => FlowWorkItems.NeedsOf(item).Contains(Issue, StringComparer.Ordinal);

    /// <summary>Each entry <paramref name="item"/>'s <c>ownedPaths</c> names, as written, read from the root as the plan's check reads it; one that does not read is left out.</summary>
    private static IEnumerable<(string Text, GlobPattern Pattern)> OwnedOf(JsonNode? item, Func<string, bool> directories)
        => ((item as JsonObject)?["ownedPaths"] as JsonArray ?? [])
            .Select(Text)
            .OfType<string>()
            .Where(pattern => GlobPattern.Problem(pattern) is null)
            .Select(pattern => (pattern, GlobPattern.FromRoot(pattern, directories)));

    /// <summary>A value as text; null where it is none, or not text.</summary>
    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
