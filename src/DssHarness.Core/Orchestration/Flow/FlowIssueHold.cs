using System.Text.Json.Nodes;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The files one issue the recovery is fixing holds, and which of them a work item owns - the one owner of both (part 04,
/// "Pre-existing issues"). From the moment a triage sends an issue to fix until the recovery's visit for it ends, the
/// issue holds the files its triage's <c>where</c> names - each entry that is a path from the repository's root, never
/// the command an issue may be in instead - and, once a check of that visit has read the remedy items its diagnosis gives,
/// every path those items own. A work item that owns any of them, or that names the issue among those whose fix it needs
/// (<c>needs</c>), and has not begun starts once that visit has ended, so it builds on the tree the fix leaves and never
/// writes the fix's lines beside it; every other goes on at once. The engine says which issues are being fixed and when
/// an item has begun; this says what each holds and what an item meets.
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

    private FlowIssueHold(string issue, string title, string fixer, IReadOnlyList<(string Text, GlobPattern Pattern)> held)
    {
        Issue = issue;
        Title = title;
        Fixer = fixer;
        _held = held;
    }

    /// <summary>The issue's id, as <c>{run.preExisting}</c> words it - its triage's - which a work item that needs its fix names.</summary>
    public string Issue { get; }

    /// <summary>The issue's title, as its triage gave it: what a step held for it says it waits for.</summary>
    public string Title { get; }

    /// <summary>The id of what fixes it: the recovery's visit at work on it, or the triage that sent it while that visit waits to be made.</summary>
    public string Fixer { get; }

    /// <summary>Every path it holds, as written: each path of its triage's <c>where</c>, then each pattern a remedy item of its visit owns.</summary>
    public IReadOnlyList<string> Paths => [.. _held.Select(held => held.Text)];

    /// <summary>
    /// What an issue being fixed holds: each entry of <paramref name="where"/> that is a path (<see cref="IsPath"/>), read
    /// from the repository's root, and each pattern every item of <paramref name="remedies"/> owns, as the item writes it -
    /// each once, in that order. A pattern a remedy item owns that does not read as one holds nothing, as the plan's own
    /// check refuses it.
    /// </summary>
    /// <param name="issue">The issue's id.</param>
    /// <param name="title">The issue's title.</param>
    /// <param name="fixer">The id of what fixes it.</param>
    /// <param name="where">The triage's <c>where</c>: a list of texts, or one text; anything else names nothing.</param>
    /// <param name="remedies">The remedy items the checks of its visit read; none before its diagnosis is checked.</param>
    public static FlowIssueHold Of(string issue, string title, string fixer, JsonNode? where, IEnumerable<JsonNode?> remedies)
    {
        ArgumentNullException.ThrowIfNull(remedies);

        var held = new List<(string Text, GlobPattern Pattern)>();
        IEnumerable<string?> entries = where switch
        {
            JsonArray list => list.Select(Text),
            JsonValue => [Text(where)],
            _ => [],
        };

        foreach (var entry in entries.OfType<string>().Where(IsPath))
        {
            // A path from the root is anchored there: 'main.c' is the root's main.c, never a main.c at any depth, which
            // the bare name would match as an owned pattern does. A trailing separator says it names a directory, and
            // holds what is beneath it.
            var path = PathPatterns.Normalize(entry) + (entry.EndsWith('/') || entry.EndsWith('\\') ? "/" : string.Empty);
            held.Add((path, GlobPattern.Parse("/" + path)));
        }

        held.AddRange(remedies.SelectMany(OwnedOf));

        return new FlowIssueHold(issue, title, fixer, [.. held.DistinctBy(entry => entry.Text, StringComparer.Ordinal)]);
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
    /// The paths this holds that the work item <paramref name="item"/> owns - each held path some pattern of its
    /// <c>ownedPaths</c> meets (<see cref="GlobPattern.Overlaps"/>) - in the order this holds them; empty where it owns
    /// none of them, and the item starts at once.
    /// </summary>
    /// <param name="item">The work item, as its instance of the work-item group holds it.</param>
    public IReadOnlyList<string> Meeting(JsonNode? item)
    {
        var owned = OwnedOf(item).Select(entry => entry.Pattern).ToList();

        return [.. _held.Where(held => owned.Any(mine => mine.Overlaps(held.Pattern, Comparison))).Select(held => held.Text)];
    }

    /// <summary>
    /// Whether the work item <paramref name="item"/> names this issue among those whose fix it needs
    /// (<see cref="FlowWorkItems.NeedsOf"/>): it builds on what the fix makes, whatever files it owns, so it starts once
    /// the fix has ended.
    /// </summary>
    /// <param name="item">The work item, as its instance of the work-item group holds it.</param>
    public bool NeededBy(JsonNode? item) => FlowWorkItems.NeedsOf(item).Contains(Issue, StringComparer.Ordinal);

    /// <summary>Each pattern <paramref name="item"/>'s <c>ownedPaths</c> names, as written, read as the plan's check reads it; one that does not read is left out.</summary>
    private static IEnumerable<(string Text, GlobPattern Pattern)> OwnedOf(JsonNode? item)
        => ((item as JsonObject)?["ownedPaths"] as JsonArray ?? [])
            .Select(Text)
            .OfType<string>()
            .Where(pattern => GlobPattern.Problem(pattern) is null)
            .Select(pattern => (pattern, GlobPattern.Parse(pattern)));

    /// <summary>A value as text; null where it is none, or not text.</summary>
    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
