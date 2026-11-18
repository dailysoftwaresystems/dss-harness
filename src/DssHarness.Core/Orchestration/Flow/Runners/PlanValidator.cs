using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The machine's check of a plan's work items before an agent is started on any of them (part 04, "What
/// <c>validate-plan</c> checks"): at least one and at most <see cref="MostItems"/>; each of the shape a work item has;
/// every name valid and used once - in the list, and across the run where the list opens a round, for a work item is
/// known by its name alone; no path owned by two items; each item's forbidden paths kept by its own owned
/// paths; the path budget of the worktree each would get, with the copies and workers its legs keep inside it and the
/// deepest file it owns; and the lists that hold an item to what the repository has - every path it reuses or replaces
/// there in the main tree, something under one of the three, and a reason and a search for everything it creates. Each
/// item also carries its classes - the decisions it changes, each with its one home, its sites, its searches and its
/// universe - or says in <c>noClass</c> why it decides nothing, and the classes are held to what the tree and the
/// item's own paths say of them (P-C1 to P-C8); and each names the tests its change affects, the units it is tested on
/// alone, or says in <c>noTests</c> why it affects none (P-C9). Each breach is a problem of the plan, worded once here;
/// none is a failure.
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
    /// <param name="mostItems">Whether the list is held to the bound of <see cref="MostItems"/>: a plan's list and a round's own are; what updates place in a round may come to hold more.</param>
    /// <param name="noItems">Whether a list of no work item is a problem: one that carries only what updates placed is not.</param>
    /// <param name="carried">
    /// The names of the work items earlier rounds of the run carry, which no item of the list may take: a work item is
    /// known by its name across the run - its record of affected tests, its plan updates and its account are kept under
    /// it - so a later item under an earlier one's name would stand in that item's place. None where the list opens no
    /// round, the run has none before it, or the round builds none of the list's own items: one added to a work-item
    /// group with no loop around it carries the owed work alone, its own items built by the round before.
    /// </param>
    /// <returns>Every problem of the plan, in the order of its items; none where it passes.</returns>
    public IReadOnlyList<string> Check(IReadOnlyList<JsonNode?> items, HarnessContext context, string run, WorkflowDefinition definition, bool mostItems = true, bool noItems = true, IReadOnlyCollection<string>? carried = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);
        ArgumentNullException.ThrowIfNull(definition);

        if (items.Count == 0)
        {
            return noItems ? [NoItems] : [];
        }

        var problems = new List<string>();

        if (mostItems && items.Count > MostItems)
        {
            problems.Add(MostItemsProblem(items.Count));
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

        foreach (var name in read.Select(item => item.Name).Distinct(StringComparer.Ordinal).Where(name => carried?.Contains(name, StringComparer.Ordinal) == true))
        {
            problems.Add(ReusedProblem(name));
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
            ClassesProblems(item, context.Layout.MainCheckoutRoot, comparison, problems);
        }

        ClassIdProblems(read, problems);
        HomeProblems(read, comparison, problems);

        return problems;
    }

    /// <summary>What says a work item a round opens with takes the name <paramref name="name"/>, which an earlier round's item carries.</summary>
    private static string ReusedProblem(string name)
        => $"work item '{name}' is named as a work item an earlier round of the run carries, and a work item is known by its name across the run - its tests and its updates are kept under it: a further item is named for what it settles, and one that does that item again is a changed piece of it";

    /// <summary>What says a list of work items holds more than a plan may: <paramref name="count"/> is how many it holds.</summary>
    public static string MostItemsProblem(int count)
        => $"the plan holds {count.ToString(CultureInfo.InvariantCulture)} work items, and a plan holds at most {MostItems.ToString(CultureInfo.InvariantCulture)}, each one builder's cycle: merge the smallest, or leave some to a later run";

    /// <summary>
    /// Checks the pieces of a plan update, as the plan's scope concluded them, with the items the plan holds: each
    /// piece of the shape the engine takes - by the one rule of a piece's shape the engine reads it by
    /// (<see cref="FlowUpdatePiece.Read"/>), its first breach said; an <c>added</c> or a <c>changed</c> piece's item
    /// an item of the list, by name and member for member; a <c>removed</c> piece's of a work item's shape; no two
    /// pieces naming one work item; a <c>changed</c> or a <c>removed</c> piece naming a work item the plan had before
    /// the update, and an <c>added</c> one naming none a round of the run carries; and the items the update places
    /// checked with those of the round it joins, as one list - where the run has one; among themselves where it does not.
    /// </summary>
    /// <param name="pieces">The pieces, as the plan's scope yielded them.</param>
    /// <param name="items">The plan's items, as its <c>items</c> yielded them.</param>
    /// <param name="round">The items of the round the pieces would join, every one it holds; null where the run has no round yet.</param>
    /// <param name="known">The names of the work items the plan had before the update.</param>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="run">The run's name.</param>
    /// <param name="definition">The workflow.</param>
    /// <param name="carried">
    /// The names every instance of the work-item group carries, over every round: an added piece under one of them would
    /// make a second work item of a name the run knows one by - the one it would replace is done again by a changed piece.
    /// </param>
    /// <returns>Every problem of the pieces, in their order; none where they pass.</returns>
    public IReadOnlyList<string> CheckPieces(
        IReadOnlyList<JsonNode?> pieces,
        IReadOnlyList<JsonNode?> items,
        IReadOnlyList<JsonNode?>? round,
        IReadOnlyList<string> known,
        HarnessContext context,
        string run,
        WorkflowDefinition definition,
        IReadOnlyCollection<string>? carried = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(known);

        var problems = new List<string>();

        // Each piece read by the one rule of a piece's shape the engine reads it by, its first breach said of it.
        var held = new List<FlowUpdatePiece>();

        foreach (var (node, at) in pieces.Select((node, at) => (node, at)))
        {
            if (FlowUpdatePiece.Read(node, out var wrong) is not { } piece)
            {
                problems.Add($"piece {(at + 1).ToString(CultureInfo.InvariantCulture)} of the plan's scope {wrong}");
                continue;
            }

            if (piece.Kind == FlowUpdatePieceKinds.Told)
            {
                continue;
            }

            var name = piece.ItemName()!;

            if (piece.Kind == FlowUpdatePieceKinds.Removed)
            {
                // The item under the name it had, written for the step that takes it back: of a work item's shape,
                // so the step that takes back owns the paths it may write.
                _ = ItemOf(piece.Item, 0, problems);
            }
            else if (!items.Any(other => string.Equals(FlowWorkItems.NameOf(other), name, StringComparison.Ordinal) && JsonNode.DeepEquals(other, piece.Item)))
            {
                problems.Add($"the piece for '{name}' is not an item of the plan's list, by name and member for member: an item the update adds or changes is an item of the updated plan");
            }

            held.Add(piece);
        }

        if (FlowUpdatePiece.NamedTwice(held) is { } twice)
        {
            problems.Add($"the pieces of the plan's scope {twice}");
        }

        foreach (var piece in held.Where(piece => piece.Kind is FlowUpdatePieceKinds.Changed or FlowUpdatePieceKinds.Removed))
        {
            if (!known.Contains(piece.ItemName()!, StringComparer.Ordinal))
            {
                problems.Add($"the piece for '{piece.ItemName()}' is '{piece.Kind}', and no work item the plan had is named so: one an instance of the work-item group carries, or an item of the list this check checked on its visit before");
            }
        }

        foreach (var piece in held.Where(piece => piece.Kind == FlowUpdatePieceKinds.Added && carried?.Contains(piece.ItemName()!, StringComparer.Ordinal) == true))
        {
            problems.Add($"the piece for '{piece.ItemName()}' is 'added', and a work item a round of the run carries is named so, and a work item is known by its name across the run: name the new item for what it settles, or make the piece 'changed' to do that item again");
        }

        // The items the update places, checked with those of the round it joins as one list - its own less the work
        // items a changed or a removed piece names, for their item replaces them - or, where the run has no round,
        // among themselves.
        var namedByPieces = held.Where(piece => piece.Kind is FlowUpdatePieceKinds.Changed or FlowUpdatePieceKinds.Removed)
            .Select(piece => piece.ItemName()!)
            .ToList();

        var joined = (round ?? [])
            .Where(other => FlowWorkItems.NameOf(other) is not { } named || !namedByPieces.Contains(named, StringComparer.Ordinal))
            .Concat(held.Select(piece => piece.Item))
            .ToList();

        problems.AddRange(Check(joined, context, run, definition, mostItems: false, noItems: false));

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

        if (FlowWorkItems.NameOf(item) is not { } name)
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

        // P-C1: an item declares its classes or says why it has none - one without either decides something unnamed.
        var classes = item["classes"] as JsonArray;
        var noClass = Text(item["noClass"]);

        if ((classes?.Count ?? 0) == 0 && noClass is null)
        {
            problems.Add($"{where} declares no class and says no reason: an item that changes a decision gives it a class, and one that decides nothing - a document, a rename the tools make, a dependency bump - says why in noClass");
        }
        else if ((classes?.Count ?? 0) > 0 && noClass is not null)
        {
            problems.Add($"{where} gives both classes and noClass: noClass says why an item decides nothing, and this one has classes");
        }

        // P-C9: an item names the tests its change affects or says why it affects none - it is tested on those alone, so
        // one without either would be tested on nothing, unsaid, and one with both says two things of one change.
        var tests = Texts(item["tests"]);
        var noTests = Text(item["noTests"]);

        if (tests is null && item["tests"] is not null)
        {
            problems.Add($"{where} gives tests that are not a list of units: each entry names the tests of one unit, written as a leg's runner takes it after its filter option");
        }
        else if ((tests?.Count ?? 0) == 0 && noTests is null)
        {
            problems.Add($"{where} names no test and says no reason: an item is tested on the tests its change affects alone, so it names them in tests, or says in noTests why its change affects none - a document, a comment, a file no test reads");
        }
        else if ((tests?.Count ?? 0) > 0 && noTests is not null)
        {
            problems.Add($"{where} gives both tests and noTests: noTests says why an item's change affects no test, and this one names tests");
        }

        if ((tests ?? []).Any(string.IsNullOrWhiteSpace))
        {
            problems.Add($"{where} names a test unit that is empty, or spaces alone: a runner reads it as no filter at all, or as one that matches every test - name the tests, or take it out");
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

    /// <summary>
    /// What a work item's classes say of the repository and of the item's own paths (P-C3 to P-C7): each home and each
    /// site a path inside the tree, as the paths an item reuses and replaces are; the home of each class the item's to
    /// change and there in the tree where it exists; a site marked the home exactly where it is the home, by its path and
    /// its symbol; every other site the item's too, for it changes - it reads the home or is deleted - and patching some
    /// of a class's sites is never done; more than one site means consolidate, as one site that is not the home does; a
    /// class without a search, a search without a query and a universe without axes decide nothing checkable.
    /// </summary>
    /// <param name="item">The work item, its classes read as they are given.</param>
    /// <param name="root">The main tree's root, the paths of a home and a site are checked against.</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="problems">The problems of the plan, each breach added to.</param>
    private void ClassesProblems(Item item, string root, StringComparison comparison, List<string> problems)
    {
        var where = $"work item '{item.Name}'";

        foreach (var (node, at) in (item.Node["classes"] as JsonArray ?? []).Select((node, at) => (node, at)))
        {
            if (node is not JsonObject @class)
            {
                problems.Add($"{where}'s class {(at + 1).ToString(CultureInfo.InvariantCulture)} is of the wrong shape: it is no object");
                continue;
            }

            var id = Text(@class["id"]) ?? $"class {(at + 1).ToString(CultureInfo.InvariantCulture)}";
            var home = @class["home"] as JsonObject;
            var homePath = Text(home?["path"]);
            var homeSymbol = SymbolOf(home?["symbol"]);
            var homeExists = home?["exists"] is JsonValue exists && exists.TryGetValue<bool>(out var there) && there;
            var sites = @class["sites"] as JsonArray ?? [];
            var action = Text(@class["action"]);

            if (homePath is null)
            {
                problems.Add($"{where}'s {id} names no home: the one place that decides the class, or one to create where none does");
                continue;
            }

            // A home that is no path inside the tree is said, and nothing more is asked of it: matched against the item's
            // paths or looked for in the tree it would name no path the tree holds, or one outside it.
            var homeAt = TreePathOf(homePath, $"the home {where}'s {id} names", problems);

            if (homeAt is not null)
            {
                // P-C3: the home is the item's to change, and where it exists it is in the tree.
                if (!item.Owned.Any(pattern => pattern.Pattern.Matches(homeAt, comparison)))
                {
                    problems.Add($"{where}'s {id} has its home at '{homePath}', which the item does not own: the home changes, and the item that changes it owns it");
                }

                if (homeExists && fileSystem.KindOf(Path.Combine(root, homeAt)) == PathKind.None)
                {
                    problems.Add($"{where}'s {id} has its home at '{homePath}', which is not in the main tree: a class is closed at what is there, or its home is created (exists: false)");
                }
            }

            var homeIsASite = false;

            foreach (var site in sites)
            {
                var path = Text((site as JsonObject)?["path"]);

                if (path is null)
                {
                    problems.Add($"{where}'s {id} has a site that names no path");
                    continue;
                }

                if (TreePathOf(path, $"a site {where}'s {id} names", problems) is not { } siteAt)
                {
                    continue;
                }

                var symbol = SymbolOf((site as JsonObject)?["symbol"]);
                var then = Text((site as JsonObject)?["then"]);

                if (fileSystem.KindOf(Path.Combine(root, siteAt)) == PathKind.None)
                {
                    problems.Add($"{where}'s {id} has its site at '{path}', which is not in the main tree: the sites are the places that decide today");
                }

                // A site is the home only where its path and its symbol are the home's, and then it is marked so; a site
                // marked home anywhere else is a place that decides today, which the plan would leave deciding.
                var isHome = homeAt is not null && string.Equals(siteAt, homeAt, comparison) && string.Equals(symbol, homeSymbol, StringComparison.Ordinal);

                if (isHome && !string.Equals(then, "home", StringComparison.Ordinal))
                {
                    problems.Add($"{where}'s {id} has its site at '{path}' marked '{then}', and it is its home: the home's own site is marked home, for it is the place that decides");
                }
                else if (homeAt is not null && !isHome && string.Equals(then, "home", StringComparison.Ordinal))
                {
                    problems.Add($"{where}'s {id} has its site at '{path}' marked home, and its home is '{homePath}' at '{homeSymbol}': a site is the home only at the home's path and symbol - every other site reads the home or is deleted");
                }

                // P-C4: every site that must change is the item's - every one but the home, where the home is known; each
                // marked reads-home or deleted, where it is not - so patching N-1 of N is refused.
                var changes = homeAt is not null ? !isHome : then is "reads-home" or "deleted";

                if (changes && !item.Owned.Any(pattern => pattern.Pattern.Matches(siteAt, comparison)))
                {
                    problems.Add($"{where}'s {id} has its site at '{path}' ({then}) outside the item: the item owns it, or the class belongs to the item that owns it");
                }

                homeIsASite |= isHome;
            }

            // P-C5: one decision, one place - a class of many sites is consolidated, and one site that is not the home
            // is a duplicate deciding; creating a home is where none exists.
            if (sites.Count > 1 && !string.Equals(action, "consolidate", StringComparison.Ordinal))
            {
                problems.Add($"{where}'s {id} holds {sites.Count.ToString(CultureInfo.InvariantCulture)} sites and its action is '{action}': more than one site means consolidate - the home decides, every other site reads it");
            }

            if (sites.Count == 1 && homeAt is not null && !homeIsASite && !string.Equals(action, "consolidate", StringComparison.Ordinal))
            {
                problems.Add($"{where}'s {id} holds one site, which is not its home, and its action is '{action}': consolidate - the home decides, every other site reads it");
            }

            if (string.Equals(action, "create-home", StringComparison.Ordinal) && homeExists)
            {
                problems.Add($"{where}'s {id} says create-home and its home exists: create-home is where no place decides it today");
            }

            // P-C6: the sites are found by searching the data, and each search can be made again.
            var searches = @class["searches"] as JsonArray ?? [];

            if (searches.Count == 0)
            {
                problems.Add($"{where}'s {id} names no search: the sites are found by searching the data, and each search is recorded");
            }

            foreach (var search in searches)
            {
                if (Text((search as JsonObject)?["query"]) is null)
                {
                    problems.Add($"{where}'s {id} has a search that gives no query: a search is made again by its query");
                }
            }

            // P-C7: the universe is the axes the repository declares, each with its values.
            var axes = (@class["universe"] as JsonObject)?["axes"] as JsonArray;

            if (axes is not { Count: > 0 })
            {
                problems.Add($"{where}'s {id} names no axis of its universe: the repository's closure text declares the axes a decision ranges over");
            }
            else
            {
                foreach (var axis in axes)
                {
                    var name = Text((axis as JsonObject)?["name"]) ?? "an axis";

                    if (!((axis as JsonObject)?["values"] is JsonArray { Count: > 0 }))
                    {
                        problems.Add($"{where}'s {id} holds no value for the axis '{name}': an axis holds what the repository declares for it");
                    }
                }
            }
        }
    }

    /// <summary>
    /// P-C2: a class id is used once in a plan - findings, records and the ledger name a class by it, so it names one.
    /// </summary>
    private static void ClassIdProblems(IReadOnlyList<Item> items, List<string> problems)
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            foreach (var @class in item.Node["classes"] as JsonArray ?? [])
            {
                if (Text((@class as JsonObject)?["id"]) is not { } id)
                {
                    continue;
                }

                if (byId.TryGetValue(id, out var first))
                {
                    problems.Add($"the class id '{id}' is used twice in the plan, by work items '{first}' and '{item.Name}': an id names one class");
                }
                else
                {
                    byId[id] = item.Name;
                }
            }
        }
    }

    /// <summary>
    /// P-C8: two items' classes name the same home, by path and symbol - one home, one item, for a home two items
    /// change is a home decided in two places. Two classes of one item may share a home: one place may decide more than
    /// one thing, and the item that owns it changes it in one sitting, so no rule of one item's own is broken. Where two
    /// items share one, the home rule (P-C3) and the rule that no path is owned by two items say so as well, each of its
    /// own part; this names the cause. A home that is no path inside the tree is said by the class rules, and compared
    /// with none.
    /// </summary>
    private static void HomeProblems(IReadOnlyList<Item> items, StringComparison comparison, List<string> problems)
    {
        Func<string, string> fold = comparison == StringComparison.OrdinalIgnoreCase
            ? static path => path.ToUpperInvariant()
            : static path => path;
        var homes = new Dictionary<(string Path, string Symbol), Item>();

        foreach (var item in items)
        {
            foreach (var @class in item.Node["classes"] as JsonArray ?? [])
            {
                var home = (@class as JsonObject)?["home"] as JsonObject;

                if (Text(home?["path"]) is not { } path || TreePathOf(path, "a home", problems: null) is not { } homeAt)
                {
                    continue;
                }

                var named = (Path: fold(homeAt), Symbol: SymbolOf(home?["symbol"]));

                if (!homes.TryGetValue(named, out var first))
                {
                    homes[named] = item;
                }
                else if (!ReferenceEquals(first, item))
                {
                    problems.Add($"work items '{first.Name}' and '{item.Name}' both hold a class whose home is '{path}' at '{named.Symbol}': one home, one item");
                }
            }
        }
    }

    /// <summary>
    /// The path a class's home or site names, as the class rules compare it - its separators made forward slashes, read
    /// past at either end - where it is a path inside the tree by the rule the paths an item reuses and replaces are held
    /// to (<see cref="OrchestrationRules.PathsProblem"/>): neither rooted nor climbing out, with no empty or <c>.</c>
    /// part. Null where it is not, its problem added to <paramref name="problems"/> where they are given, for no pattern
    /// matches such a path and no look in the tree for it stays inside the tree.
    /// </summary>
    /// <param name="path">The path, as the plan gives it.</param>
    /// <param name="what">What names it, as its problem says it.</param>
    /// <param name="problems">The problems of the plan, the path's own added to; null where it is said elsewhere.</param>
    private static string? TreePathOf(string path, string what, List<string>? problems)
    {
        if (OrchestrationRules.PathsProblem([path], what) is { } problem)
        {
            problems?.Add(problem);
            return null;
        }

        return PathPatterns.Normalize(path);
    }

    /// <summary>A symbol as the class rules compare it: its text, or empty where none is given.</summary>
    private static string SymbolOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var symbol) ? symbol : string.Empty;

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
