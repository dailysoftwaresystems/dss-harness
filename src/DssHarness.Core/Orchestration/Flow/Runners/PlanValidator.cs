using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The machine's check of a plan's work items before an agent is started on any of them (part 04, "What
/// <c>validate-plan</c> checks"): at least one and at most <see cref="MostItems"/>; each of the shape a work item has;
/// every name valid and used once; no path owned by two items; each item's forbidden paths kept by its own owned
/// paths; the path budget of the worktree each would get, with the copies and workers its legs keep inside it and the
/// deepest file it owns; and the lists that hold an item to what the repository has - every path it reuses or replaces
/// there in the main tree, something under one of the three, and a reason and a search for everything it creates. Each
/// breach is a problem of the plan, worded once here; none is a failure.
/// </summary>
/// <param name="fileSystem">Reads the main tree for the paths the items reuse and replace.</param>
/// <param name="pathBudget">Reckons the paths of the worktree each item would get.</param>
/// <param name="platform">This machine: its operating system, and how the tree's names compare.</param>
public sealed class PlanValidator(IFileSystem fileSystem, IPathBudget pathBudget, IHostPlatform platform)
{
    /// <summary>The most work items a plan holds.</summary>
    public const int MostItems = 8;

    /// <summary>The lists every work item has, beside its name and the paths it owns and must not touch.</summary>
    private static readonly string[] Lists = ["reuses", "creates", "replaces"];

    /// <summary>What a plan with no work item is: a problem, never a round that ends at once.</summary>
    public const string NoItems = "the plan holds no work item: it would build nothing";

    /// <summary>Checks <paramref name="items"/>, a plan's work items, as the run <paramref name="run"/> of <paramref name="definition"/> would build them.</summary>
    /// <param name="items">The work items, as the plan gives them.</param>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="run">The run's name, which names its agents' worktrees' directory.</param>
    /// <param name="definition">The workflow, whose leg steps say which copies a worktree keeps.</param>
    /// <returns>Every problem of the plan, in the order of its items; none where it passes.</returns>
    public IReadOnlyList<string> Check(IReadOnlyList<JsonNode?> items, HarnessContext context, string run, WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);
        ArgumentNullException.ThrowIfNull(definition);

        if (items.Count == 0)
        {
            return [NoItems];
        }

        var problems = new List<string>();

        if (items.Count > MostItems)
        {
            problems.Add($"the plan holds {items.Count.ToString(CultureInfo.InvariantCulture)} work items, and a plan holds at most {MostItems.ToString(CultureInfo.InvariantCulture)}, each one builder's cycle: merge the smallest, or leave some to a later run");
        }

        var read = new List<Item>();

        foreach (var (node, at) in items.Select((node, at) => (node, at)))
        {
            if (ItemOf(node, at, problems) is { } item)
            {
                read.Add(item);
            }
        }

        var comparison = platform.PathComparison;

        foreach (var named in read.GroupBy(item => item.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            problems.Add($"{named.Count().ToString(CultureInfo.InvariantCulture)} work items are named '{named.Key}', and a work item is known by its name: name each once");
        }

        foreach (var (item, at) in read.Select((item, at) => (item, at)))
        {
            // No file is written by two items: each pair is said once, by the first patterns found to meet.
            foreach (var other in read.Skip(at + 1))
            {
                if (Meeting(item.Owned, other.Owned, comparison) is var (mine, theirs))
                {
                    problems.Add($"work items '{item.Name}' and '{other.Name}' both own what '{mine}' and '{theirs}' match: no file is written by two items, so give it to one");
                }
            }

            if (Meeting(item.Owned, item.Forbidden, comparison) is var (owned, forbidden))
            {
                problems.Add($"work item '{item.Name}' owns '{owned}' and must not touch '{forbidden}', which match the same paths: an item's forbidden paths are kept by the paths it owns");
            }

            var deepest = item.Owned.Select(pattern => Literal(pattern.Text).Length).DefaultIfEmpty(0).Max();
            var (budget, tree) = FlowRunPaths.AtStart(pathBudget, context, run, definition, platform.PlatformKey, deepest);

            if (!budget.IsWithinBudget)
            {
                problems.Add($"work item '{item.Name}' would work in paths too long for this machine - its worktree, with the copies its legs keep inside it and the deepest file it owns: {budget.Describe(tree)}");
            }

            ListProblems(item, context.Layout.MainCheckoutRoot, problems);
        }

        return problems;
    }

    /// <summary>A work item read whole: its name, the paths it owns and must not touch, and its three lists.</summary>
    private sealed record Item(string Name, IReadOnlyList<(string Text, GlobPattern Pattern)> Owned, IReadOnlyList<(string Text, GlobPattern Pattern)> Forbidden, JsonObject Node);

    /// <summary>
    /// The work item <paramref name="node"/> is, where it is of a work item's shape and its paths read as patterns; each
    /// thing wrong with it is a problem, and an item of the wrong shape is checked no further.
    /// </summary>
    private static Item? ItemOf(JsonNode? node, int at, List<string> problems)
    {
        var place = $"work item {(at + 1).ToString(CultureInfo.InvariantCulture)}";

        if (node is not JsonObject item)
        {
            problems.Add($"{place} is of the wrong shape: it is no object, and a work item is one, with its name, the paths it owns and must not touch, and what it reuses, creates and replaces");
            return null;
        }

        if (Text(item["name"]) is not { } name)
        {
            problems.Add($"{place} is of the wrong shape: it has no name, and a work item is known by its name");
            return null;
        }

        var where = $"work item '{name}'";
        var wrong = new[] { "ownedPaths", "forbiddenPaths" }.Where(member => Texts(item[member]) is null)
            .Concat(Lists.Where(member => item[member] is not JsonArray))
            .ToList();

        if (wrong.Count > 0)
        {
            problems.Add($"{where} is of the wrong shape: {string.Join(", ", wrong)} {(wrong.Count == 1 ? "is" : "are")} not there as {(wrong.Count == 1 ? "a list" : "lists")}, and each is part of what a work item is");
            return null;
        }

        if (OrchestrationRules.NameProblem(name) is not null)
        {
            problems.Add($"{where} is not named in lowercase letters, digits and single hyphens, as a work item is");
        }

        var owned = Patterns(item, "ownedPaths", "owns", where, problems);
        var forbidden = Patterns(item, "forbiddenPaths", "must not touch", where, problems);

        if (owned.Count == 0 && Texts(item["ownedPaths"])!.Count == 0)
        {
            problems.Add($"{where} owns no path, and an item writes only the paths it owns: name them");
        }

        return new Item(name, owned, forbidden, item);
    }

    /// <summary>The patterns of <paramref name="member"/>, each read as a glob; one that does not read is a problem, and is left out.</summary>
    private static List<(string Text, GlobPattern Pattern)> Patterns(JsonObject item, string member, string does, string where, List<string> problems)
    {
        var patterns = new List<(string, GlobPattern)>();

        foreach (var text in Texts(item[member])!)
        {
            if (GlobPattern.Problem(text) is { } problem)
            {
                problems.Add($"{where} {does} '{text}', which {problem}");
                continue;
            }

            patterns.Add((text, GlobPattern.Parse(text)));
        }

        return patterns;
    }

    /// <summary>The first pattern of <paramref name="left"/> and of <paramref name="right"/> that some path matches both, in the order written; null where none meet.</summary>
    private static (string Left, string Right)? Meeting(
        IReadOnlyList<(string Text, GlobPattern Pattern)> left,
        IReadOnlyList<(string Text, GlobPattern Pattern)> right,
        StringComparison comparison)
        => left.SelectMany(mine => right.Where(theirs => mine.Pattern.Overlaps(theirs.Pattern, comparison)).Select(theirs => ((string, string)?)(mine.Text, theirs.Text)))
            .FirstOrDefault();

    /// <summary>
    /// What a work item's lists say of the repository, checked against the main tree at <paramref name="root"/>: something
    /// under one of the three; every path it reuses or replaces there; a reason and a search for everything it creates.
    /// </summary>
    private void ListProblems(Item item, string root, List<string> problems)
    {
        var where = $"work item '{item.Name}'";

        if (Lists.All(member => item.Node[member] is JsonArray { Count: 0 }))
        {
            problems.Add($"{where} names nothing it reuses, creates or replaces: say what it builds on, or what it adds where nothing there serves");
        }

        foreach (var (member, does) in new[] { ("reuses", "reuses"), ("replaces", "replaces") })
        {
            foreach (var (entry, at) in ((JsonArray)item.Node[member]!).Select((entry, at) => (entry, at)))
            {
                if (Text((entry as JsonObject)?["path"]) is not { } path)
                {
                    problems.Add($"{where}'s {member} entry {(at + 1).ToString(CultureInfo.InvariantCulture)} names no path");
                }
                else if (OrchestrationRules.PathsProblem([path], $"the path {where} {does}") is { } problem)
                {
                    problems.Add(problem);
                }
                else if (fileSystem.KindOf(Path.Combine(root, PathPatterns.Normalize(path))) == PathKind.None)
                {
                    problems.Add($"{where} {does} '{path}', which is not in the main tree: a plan builds on what is there");
                }
            }
        }

        foreach (var (entry, at) in ((JsonArray)item.Node["creates"]!).Select((entry, at) => (entry, at)))
        {
            var what = Text((entry as JsonObject)?["what"]) ?? $"entry {(at + 1).ToString(CultureInfo.InvariantCulture)}";
            var missing = new[] { ("why", "why nothing there serves"), ("searched", "where it looked") }.Where(member => Text((entry as JsonObject)?[member.Item1]) is null).Select(member => member.Item2).ToList();

            if (missing.Count > 0)
            {
                problems.Add($"{where} creates '{what}' and does not say {string.Join(" or ", missing)}: what is made from nothing says why, and where it searched first");
            }
        }
    }

    /// <summary>A pattern's part before its first wildcard: the deepest path it names outright, from the tree's root.</summary>
    private static string Literal(string pattern)
        => string.Join('/', PathPatterns.Normalize(pattern).Split('/').TakeWhile(segment => segment.IndexOfAny(['*', '?']) < 0));

    /// <summary>A member's text, where it is one that is not blank; null otherwise.</summary>
    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>A member's list of texts, where it is a list holding texts alone; null otherwise.</summary>
    private static IReadOnlyList<string>? Texts(JsonNode? node)
        => node is JsonArray list && list.All(entry => entry is JsonValue value && value.TryGetValue<string>(out _))
            ? [.. list.Select(entry => entry!.GetValue<string>())]
            : null;
}
