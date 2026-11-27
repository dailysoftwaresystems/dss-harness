using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The machine's check of a plan's work items before an agent is started on any of them (part 04, "What
/// <c>validate-plan</c> checks"): at least one, and as many as the work needs - no count of items is refused, for each is
/// sized to one builder's cycle and a round runs them in waves of the run's open agents (<c>maxOpenAgents</c>); each of
/// the shape a work item has;
/// every name valid and used once - in the list, and across the run where the list opens a round, for a work item is
/// known by its name alone; no path owned by two items; each item's forbidden paths kept by its own owned
/// paths - each entry read from the repository's root, as the item's agent's writes and its fold's within read it
/// (<see cref="GlobPattern.FromRoot"/>), whether it names a directory asked of the run's start tree as they ask it
/// (<see cref="TreeDirectories"/>), so two entries meet only where some path the tree can hold is matched by both; no
/// entry an item owns with no trailing separator that names a directory of the main tree the start tree does not hold -
/// one an earlier round made - for every write beneath it would be refused; the path budget of the worktree each would get, with the copies and workers its legs keep inside it and the
/// deepest file it owns; and the lists that hold an item to what the repository has - every path it reuses or replaces
/// there in the main tree, something under one of the three, and a reason and a search for everything it creates. Each
/// item also carries its classes - the decisions it changes, each with its one home, its sites, its searches and its
/// universe - or says in <c>noClass</c> why it decides nothing, and the classes are held to what the tree and the
/// item's own paths say of them, and each universe to the two ways an axis is answered and to the cells its axes make (P-C1 to P-C8); and each names the tests its change affects, the units it is tested on
/// alone, or says in <c>noTests</c> why it affects none (P-C9). The rules that read the main tree as it now is - an
/// owned entry naming one of its directories, a path reused or replaced, a class's home and sites being there - hold
/// only the items the check introduces or changes: an item a plan update's pieces leave as it was was checked when its
/// round opened, against the tree of that moment, and what a fold has made of the tree since is no breach of it. Each
/// breach is a problem of the plan, worded once here; none is a failure.
/// </summary>
/// <param name="fileSystem">Reads the main tree for the paths the items reuse and replace, and the directories it holds.</param>
/// <param name="pathBudget">Reckons the paths of the worktree each item would get.</param>
/// <param name="platform">This machine: its operating system, and how the tree's names compare.</param>
public sealed class PlanValidator(IFileSystem fileSystem, IPathBudget pathBudget, IHostPlatform platform)
{
    /// <summary>The lists every work item has, beside its name and the paths it owns and must not touch.</summary>
    private static readonly string[] Lists = ["reuses", "creates", "replaces"];

    /// <summary>What a plan with no work item is: a problem, never a round that ends at once.</summary>
    public const string NoItems = "the plan holds no work item: it would build nothing";

    /// <summary>Checks <paramref name="items"/>, a plan's work items, as the run <paramref name="run"/> of <paramref name="definition"/> would build them.</summary>
    /// <param name="items">The work items, as the plan gives them.</param>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="run">The run's name, which names its agents' worktrees' directory.</param>
    /// <param name="definition">The workflow, whose leg steps say which copies a worktree keeps.</param>
    /// <param name="startTree">
    /// The directories of the run's start tree (<see cref="FlowRunPlace.Directories"/>), which every entry is read against:
    /// for whether two entries meet, a path the tree could not answer for counts as a directory; for what an item owns, as
    /// none.
    /// </param>
    /// <param name="noItems">Whether a list of no work item is a problem: one that carries only what updates placed is not.</param>
    /// <param name="carried">
    /// The names of the work items earlier rounds of the run carry, which no item of the list may take: a work item is
    /// known by its name across the run - its record of affected tests, its plan updates and its account are kept under
    /// it - so a later item under an earlier one's name would stand in that item's place. None where the list opens no
    /// round, the run has none before it, or the round builds none of the list's own items: one added to a work-item
    /// group with no loop around it carries the owed work alone, its own items built by the round before.
    /// </param>
    /// <param name="introduced">
    /// The names of the items the check introduces or changes, which alone the rules that read the main tree as it now is
    /// hold (<see cref="DirectoryProblems"/>, and the paths <see cref="ListProblems"/> and <see cref="ClassesProblems"/>
    /// look for there): a plan update's, the items its added and changed pieces place (<see cref="Introduced"/>).
    /// Every other item of the list - one of the round's that no piece changes, or one the plan's list keeps as it was -
    /// was checked when it entered a round, against the main tree of that moment, and a fold since that made a directory
    /// of its bare entry or took away a path it reused is no breach of it. Null where the check introduces every item: a
    /// plan's own check, and the check that opens a round.
    /// </param>
    /// <returns>Every problem of the plan, in the order of its items; none where it passes.</returns>
    public IReadOnlyList<string> Check(IReadOnlyList<JsonNode?> items, HarnessContext context, string run, WorkflowDefinition definition, TreeDirectories startTree, bool noItems = true, IReadOnlyCollection<string>? carried = null, IReadOnlyCollection<string>? introduced = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(run);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(startTree);

        if (items.Count == 0)
        {
            return noItems ? [NoItems] : [];
        }

        var problems = new List<string>();
        var read = new List<Item>();

        foreach (var (node, at) in items.Select((node, at) => (node, at)))
        {
            if (ItemOf(node, at, startTree, problems) is { } item)
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
            // No file is written by two items: each pair is said once, by the patterns found to meet - a pair that meets
            // whatever the start tree holds first, and only where none does, one that meets because the start tree could
            // not be read and every path was taken for a directory, saying so.
            foreach (var other in read.Skip(at + 1))
            {
                if (Meeting(startTree, (item.Writes, other.Writes), (item.Owned, other.Owned), comparison) is var (mine, theirs, unread))
                {
                    problems.Add($"work items '{item.Name}' and '{other.Name}' both own what '{mine}' and '{theirs}' match: no file is written by two items, so give it to one{unread}");
                }
            }

            if (Meeting(startTree, (item.Writes, item.Kept), (item.Owned, item.Forbidden), comparison) is var (owned, forbidden, kept))
            {
                problems.Add($"work item '{item.Name}' owns '{owned}' and must not touch '{forbidden}', which match the same paths: an item's forbidden paths are kept by the paths it owns{kept}");
            }

            // What the main tree holds now is asked of the items the check introduces or changes alone.
            var asNow = introduced?.Contains(item.Name, StringComparer.Ordinal) ?? true;

            if (asNow)
            {
                DirectoryProblems(item, context.Layout.MainCheckoutRoot, problems);
            }

            var deepest = item.Owned.Select(pattern => Literal(pattern.Text).Length).DefaultIfEmpty(0).Max();
            var (budget, tree) = FlowRunPaths.AtStart(pathBudget, context, run, definition, platform.PlatformKey, deepest);

            if (!budget.IsWithinBudget)
            {
                problems.Add($"work item '{item.Name}' would work in paths too long for this machine - its worktree, with the copies its legs keep inside it and the deepest file it owns: {budget.Describe(tree)}");
            }

            ListProblems(item, asNow ? context.Layout.MainCheckoutRoot : null, problems);
            ClassesProblems(item, asNow ? context.Layout.MainCheckoutRoot : null, comparison, problems);
        }

        ClassIdProblems(read, problems);
        HomeProblems(read, comparison, problems);

        return problems;
    }

    /// <summary>What says a work item a round opens with takes the name <paramref name="name"/>, which an earlier round's item carries.</summary>
    private static string ReusedProblem(string name)
        => $"work item '{name}' is named as a work item an earlier round of the run carries, and a work item is known by its name across the run - its tests and its updates are kept under it: a further item is named for what it settles, and one that does that item again is a changed piece of it";

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
    /// <param name="round">
    /// The items of the round the pieces would join, every one it holds: the list the check that opens that round was
    /// opening it with, where that check raised the update - the round has not opened, and no instance carries them yet -
    /// else the round the run reached; null where the run has neither.
    /// </param>
    /// <param name="known">The names of the work items the plan had before the update, those of the round the pieces join among them.</param>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="run">The run's name.</param>
    /// <param name="definition">The workflow.</param>
    /// <param name="startTree">The directories of the run's start tree, which every entry is read against (<see cref="Check"/>).</param>
    /// <param name="carried">
    /// The names every instance of the work-item group carries, over every round: an added piece under one of them would
    /// make a second work item of a name the run knows one by - the one it would replace is done again by a changed piece.
    /// </param>
    /// <param name="joined">
    /// The list the round opens with once the update is placed, as it was checked: the round's items less those a
    /// changed or a removed piece names, then each piece's item - or the pieces' items alone where the run has no round.
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
        TreeDirectories startTree,
        out IReadOnlyList<JsonNode?> joined,
        IReadOnlyCollection<string>? carried = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(startTree);

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
                _ = ItemOf(piece.Item, 0, startTree, problems);
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

        joined = [.. (round ?? [])
            .Where(other => FlowWorkItems.NameOf(other) is not { } named || !namedByPieces.Contains(named, StringComparer.Ordinal))
            .Concat(held.Select(piece => piece.Item))];

        // The main tree as it now is holds the items the update places alone: the round's own were checked as it opened.
        problems.AddRange(Check(joined, context, run, definition, startTree, noItems: false, introduced: IntroducedOf(held)));

        return problems;
    }

    /// <summary>
    /// The names of the work items the pieces of a plan update introduce or change - each <c>added</c> and each
    /// <c>changed</c> piece's - which alone a check of the update holds to the main tree as it now is (<see cref="Check"/>):
    /// a <c>removed</c> piece's item is taken back and builds on nothing, a <c>told</c> one carries none, and one that
    /// does not read as a piece places nothing. The one owner of which items an update introduces, which the check of its
    /// pieces and the check of the plan's own list both read.
    /// </summary>
    /// <param name="pieces">The pieces, as the plan's scope yielded them.</param>
    public static IReadOnlyList<string> Introduced(IReadOnlyList<JsonNode?> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        return IntroducedOf(pieces.Select(node => FlowUpdatePiece.Read(node, out _)).OfType<FlowUpdatePiece>());
    }

    /// <summary>The names of the work items <paramref name="pieces"/>, each read as a piece, introduce or change (<see cref="Introduced"/>).</summary>
    private static List<string> IntroducedOf(IEnumerable<FlowUpdatePiece> pieces)
        => [.. pieces.Where(piece => piece.Kind is FlowUpdatePieceKinds.Added or FlowUpdatePieceKinds.Changed).Select(piece => piece.ItemName()).OfType<string>()];

    /// <summary>
    /// A work item read whole: its name, the paths it owns and must not touch - read for whether they meet another's - the
    /// same read for what may be written beneath them (<paramref name="Writes"/>, <paramref name="Kept"/>), and its three
    /// lists.
    /// </summary>
    /// <param name="Name">The item's name.</param>
    /// <param name="Owned">Its owned paths, read for whether they meet (<see cref="TreeDirectories.Meeting"/>).</param>
    /// <param name="Forbidden">Its forbidden paths, read for whether they meet.</param>
    /// <param name="Writes">Its owned paths, read for what its agent may write and its fold carry (<see cref="TreeDirectories.ForWriting"/>).</param>
    /// <param name="Kept">Its forbidden paths, read for what may be written beneath them, as its owned paths are.</param>
    /// <param name="Node">The item as the plan gives it.</param>
    private sealed record Item(
        string Name,
        IReadOnlyList<(string Text, GlobPattern Pattern)> Owned,
        IReadOnlyList<(string Text, GlobPattern Pattern)> Forbidden,
        IReadOnlyList<(string Text, GlobPattern Pattern)> Writes,
        IReadOnlyList<(string Text, GlobPattern Pattern)> Kept,
        JsonObject Node);

    /// <summary>
    /// The work item <paramref name="node"/> is, where it is of a work item's shape and its paths read from the
    /// repository's root (<see cref="GlobPattern.FromRoot"/>), as its agent's writes and its fold's within read them; each
    /// thing wrong with it is a problem, and an item of the wrong shape is checked no further.
    /// </summary>
    /// <param name="node">The item, as the plan gives it.</param>
    /// <param name="at">Where it stands in its list, from 0.</param>
    /// <param name="startTree">The directories of the run's start tree, which every entry is read against.</param>
    /// <param name="problems">The problems of the plan, its own added to.</param>
    private static Item? ItemOf(JsonNode? node, int at, TreeDirectories startTree, List<string> problems)
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

        var owned = Patterns(item, "ownedPaths", "owns", where, startTree.Meeting, problems);
        var forbidden = Patterns(item, "forbiddenPaths", "must not touch", where, startTree.Meeting, problems);
        var writes = owned.Select(entry => (entry.Text, startTree.ForWriting(entry.Text))).ToList();
        var kept = forbidden.Select(entry => (entry.Text, startTree.ForWriting(entry.Text))).ToList();

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

        return new Item(name, owned, forbidden, writes, kept, item);
    }

    /// <summary>The entries of <paramref name="member"/>, each read from the repository's root; one that does not read is a problem, and is left out.</summary>
    private static List<(string Text, GlobPattern Pattern)> Patterns(JsonObject item, string member, string does, string where, Func<string, bool> directories, List<string> problems)
    {
        var patterns = new List<(string, GlobPattern)>();

        foreach (var text in Texts(item[member])!)
        {
            if (GlobPattern.Problem(text) is { } problem)
            {
                problems.Add($"{where} {does} '{text}', which {problem}");
                continue;
            }

            patterns.Add((text, GlobPattern.FromRoot(text, directories)));
        }

        return patterns;
    }

    /// <summary>
    /// What the item's owned paths say of how to write what they once meant, where <paramref name="path"/> is none of
    /// theirs (<see cref="GlobPattern.WrittenFor(IEnumerable{ValueTuple{GlobPattern, string}}, IEnumerable{string}, StringComparison)"/>),
    /// as a clause after a semicolon; empty where they say nothing.
    /// </summary>
    private static string WrittenFor(Item item, string path, StringComparison comparison)
        => GlobPattern.WrittenFor(item.Writes.Select(entry => (entry.Pattern, entry.Text)), [path], comparison) is { } said ? $"; {said}" : string.Empty;

    /// <summary>
    /// The pair two lists of entries are said to meet at, with what the start tree says of it as a clause after a
    /// semicolon: the first pair, in the order written, that meets read narrowly - as what may be written beneath each
    /// (<paramref name="narrow"/>) - which meets whatever the tree holds and is said with nothing of it; and only where none
    /// does, the first that meets read wide - every path the tree could not answer for taken for one of its directories
    /// (<paramref name="wide"/>) - with why, where the tree could not be read (<see cref="TreeDirectories.UnreadForMeeting"/>).
    /// So a conflict that is real is never hidden behind one the unread tree alone made, and one it made is never said
    /// without why. Null where no pair meets either way; where the tree was read, the two readings are one.
    /// </summary>
    private static (string Left, string Right, string Unread)? Meeting(
        TreeDirectories startTree,
        (IReadOnlyList<(string Text, GlobPattern Pattern)> Left, IReadOnlyList<(string Text, GlobPattern Pattern)> Right) narrow,
        (IReadOnlyList<(string Text, GlobPattern Pattern)> Left, IReadOnlyList<(string Text, GlobPattern Pattern)> Right) wide,
        StringComparison comparison)
        => Meeting(narrow.Left, narrow.Right, comparison) is var (left, right) ? (left, right, string.Empty)
            : Meeting(wide.Left, wide.Right, comparison) is var (wideLeft, wideRight) ? (wideLeft, wideRight, startTree.UnreadForMeeting is { } said ? $"; {said}" : string.Empty)
            : null;

    /// <summary>
    /// An entry an item owns that names no directory of the run's start tree, read for what may be written beneath it - a
    /// path with no trailing separator - and a directory of the main tree at <paramref name="root"/>: one an earlier round's
    /// fold made since the run began, or one the start tree could not say it holds. Every reader of the item's paths reads
    /// it as that one path, so the builder's every write beneath it would be refused, and its fold would carry none of it;
    /// the check refuses it first, saying how to write it (<see cref="GlobPattern.WrittenForDirectory"/>). An entry an item
    /// must not touch is never refused so: nothing gates a write by an item's forbidden paths - the check weighs them for
    /// meeting the item's own owned paths, and its builder is told them - so no write is refused for how one is written.
    /// </summary>
    private void DirectoryProblems(Item item, string root, List<string> problems)
    {
        foreach (var (text, pattern) in item.Writes)
        {
            if (pattern.WrittenForDirectory(text) is { } said && fileSystem.KindOf(Path.Combine(root, PathPatterns.Normalize(text))) == PathKind.Directory)
            {
                problems.Add($"work item '{item.Name}' owns '{text}', which names nothing beneath it: {said}");
            }
        }
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
    /// under one of the three; every path it reuses or replaces a path inside the tree, and there; a reason and a search
    /// for everything it creates.
    /// </summary>
    /// <param name="item">The work item.</param>
    /// <param name="root">The main tree's root, every path it reuses or replaces is looked for in; null where the item is not held to the tree as it now is (<see cref="Check"/>'s <c>introduced</c>).</param>
    /// <param name="problems">The problems of the plan, each breach added to.</param>
    private void ListProblems(Item item, string? root, List<string> problems)
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
                else if (root is not null && fileSystem.KindOf(Path.Combine(root, PathPatterns.Normalize(path))) == PathKind.None)
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
    /// class without a search and a search without a query decide nothing checkable; and a universe answers each axis in
    /// one of the two ways an axis is answered, and counts the cells its axes that apply make.
    /// </summary>
    /// <param name="item">The work item, its classes read as they are given.</param>
    /// <param name="root">The main tree's root, the paths of a home and a site are looked for in; null where the item is not held to the tree as it now is (<see cref="Check"/>'s <c>introduced</c>).</param>
    /// <param name="comparison">How this machine compares paths.</param>
    /// <param name="problems">The problems of the plan, each breach added to.</param>
    private void ClassesProblems(Item item, string? root, StringComparison comparison, List<string> problems)
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
                if (!item.Writes.Any(pattern => pattern.Pattern.Matches(homeAt, comparison)))
                {
                    problems.Add($"{where}'s {id} has its home at '{homePath}', which the item does not own: the home changes, and the item that changes it owns it{WrittenFor(item, homeAt, comparison)}");
                }

                if (homeExists && root is not null && fileSystem.KindOf(Path.Combine(root, homeAt)) == PathKind.None)
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

                if (root is not null && fileSystem.KindOf(Path.Combine(root, siteAt)) == PathKind.None)
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

                if (changes && !item.Writes.Any(pattern => pattern.Pattern.Matches(siteAt, comparison)))
                {
                    problems.Add($"{where}'s {id} has its site at '{path}' ({then}) outside the item: the item owns it, or the class belongs to the item that owns it{WrittenFor(item, siteAt, comparison)}");
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

            foreach (var universe in UniverseProblems(@class["universe"] as JsonObject))
            {
                problems.Add($"{where}'s {id} {universe}");
            }
        }
    }

    /// <summary>
    /// What an answer of an axis must say beside its values, said once: where it applies, where they are declared and no
    /// why; where it does not, why and nothing declared.
    /// </summary>
    private const string AxisRule = "an axis applies exactly where it names values - then it says where the repository declares them, and gives no why - and does not apply where it names none - then it says why, and declares nothing";

    /// <summary>
    /// P-C7: what is wrong with a universe, each axis read by the one reading of a planned universe
    /// (<see cref="ClassClosureReading.PlannedAxes"/>): every axis answered in the one shape the plan's schema gives an axis
    /// (<c>axis</c>), and so in one of its two ways - applying exactly where it names values, with where the repository
    /// declares them and no why; not applying where it names none, with why and nothing declared - and the count of its
    /// cells the product of the counts of the values of the axes that apply, 1 where none applies. The count is the size
    /// of the decision as the plan states it: its audits, its builder and its closing review are each given the class
    /// whole, and read the cells a pin and a sweep cover beside it; no other rule of the engine reads it. That the universe
    /// answers every axis the closure text lists, by a member of its own, is the plan's schema's to hold, in the step that
    /// answers it, as it holds every answer's shape; these, which a shape cannot say, this check holds there too.
    /// </summary>
    /// <param name="universe">The class's universe, as the plan gives it.</param>
    private static IEnumerable<string> UniverseProblems(JsonObject? universe)
    {
        var axes = ClassClosureReading.PlannedAxes(universe);

        foreach (var axis in axes)
        {
            var declared = !string.IsNullOrWhiteSpace(axis.DeclaredIn);
            var reasoned = !string.IsNullOrWhiteSpace(axis.Why);
            List<string> wrong = axis.Applies
                ? [.. declared ? [] : new[] { "not where the repository declares them" }, .. reasoned ? new[] { "why it does not apply" } : []]
                : [.. reasoned ? [] : new[] { "not why it does not apply" }, .. declared ? new[] { "where its values are declared" } : []];

            if (wrong.Count > 0)
            {
                var named = axis.Applies ? $"names the values of its axis '{axis.Name}'" : $"names no value of its axis '{axis.Name}'";
                yield return $"{named} and says {string.Join(", and ", wrong)}: {AxisRule}";
            }
        }

        if (CellsProblem(universe, axes) is { } cells)
        {
            yield return cells;
        }
    }

    /// <summary>
    /// What is wrong with the count of a universe's cells, or null where it is right: the product of the counts of the
    /// values of the axes that apply, and 1 where none applies (<see cref="UniverseProblems"/>).
    /// </summary>
    /// <param name="universe">The class's universe, as the plan gives it.</param>
    /// <param name="axes">Its axes, as the one reading of a planned universe gives them.</param>
    private static string? CellsProblem(JsonObject? universe, IReadOnlyList<UniverseAxis> axes)
    {
        var applying = axes.Where(axis => axis.Applies).ToList();
        var product = applying.Aggregate(BigInteger.One, (made, axis) => made * axis.Values.Count);
        var cells = ClassClosureReading.CellsOf(universe);

        if (cells == product)
        {
            return null;
        }

        var counted = cells is { } given ? $"counts {given.ToString(CultureInfo.InvariantCulture)} cells in its universe" : "counts no cells in its universe";
        var made = applying.Count == 0
            ? "no axis of it applies, which makes 1"
            : $"its axes that apply make {product.ToString(CultureInfo.InvariantCulture)} ({string.Join(" x ", applying.Select(axis => $"{axis.Name} {axis.Values.Count.ToString(CultureInfo.InvariantCulture)}"))})";

        return $"{counted}, and {made}: a universe's cells are the product of the counts of the values of its axes that apply";
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
