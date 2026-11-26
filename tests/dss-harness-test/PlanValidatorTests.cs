using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The machine's check of a plan's classes (P-C1 to P-C8): an item declares the decisions it changes or says why it
/// changes none; a class id names one class in a plan; each home and each site is a path inside the tree; the home of a
/// class is the item's own to change and there in the tree where it exists; a site is marked home exactly where it is
/// the home, and every other site, which must change, is the item's too, for patching some of a class's sites is never
/// done; more than one site means consolidate, as one site that is not the home does; a class is searched by data it
/// can name again, and its universe counts the cells its axes that apply make; and one home belongs to one item. And of
/// its tests (P-C9): an item names the units of the tests its change affects, or says why it affects none. Each rule
/// is red with itself removed, held by the reverts this file's twin methods name.
/// </summary>
public sealed class PlanValidatorTests : IDisposable
{
    /// <summary>Every directory a test made, removed once it ends.</summary>
    private readonly List<TempDirectory> _made = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WorkflowDefinition Flow = WorkflowFileParser.Parse(
        "workflows/flow/flow.yml",
        """
        name: flow
        steps:
          - name: orient
            type: orient
        """);

    /// <summary>A repository with the harness initialised, committed, on branch 'work', holding the files a class names.</summary>
    private async Task<(HarnessFactory Harness, TempDirectory Repository)> RepositoryAsync()
    {
        var harness = new HarnessFactory();
        var repository = new TempDirectory();
        _made.Add(repository);

        repository.WriteFile("file.txt", "content\n");
        repository.WriteFile("src/one/home.cs", "decides\n");
        repository.WriteFile("src/one/second.cs", "decides too\n");
        repository.WriteFile("src/other/other.cs", "decides elsewhere\n");
        repository.WriteFile("src/two/home.cs", "decides as well\n");
        repository.WriteFile("src/two/site.cs", "decides beside it\n");
        repository.WriteFile("src/created/home.cs", "decides at last\n");
        await harness.InitializeHarnessAsync(repository.Path, Token);
        await harness.CommitAllAsync(repository.Path, "initial", Token);
        await harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "work"], Token);

        return (harness, repository);
    }

    /// <summary>The check of a plan's items, as the validate-plan step makes it.</summary>
    private async Task<IReadOnlyList<string>> CheckAsync(TempDirectory repository, params JsonNode?[] items)
    {
        var harness = new HarnessFactory();

        return new PlanValidator(harness.FileSystem, harness.PathBudget, harness.Platform)
            .Check(items, await harness.ContextLoader.LoadAsync(repository.Path, Token), "r1", Flow);
    }

    /// <summary>
    /// A work item that owns <paramref name="owned"/> and changes the classes given, or says why it changes none; tested on
    /// the units given, or on one of its own where none are given and no reason says why it runs none.
    /// </summary>
    private static JsonObject Item(string name = "one", string owned = "src/one/**", JsonObject[]? classes = null, string? noClass = null, string[]? tests = null, string? noTests = null)
    {
        var item = new JsonObject
        {
            ["name"] = name,
            ["title"] = "The item",
            ["instructions"] = "Build it.",
            ["done"] = "it is built",
            ["ownedPaths"] = new JsonArray(owned),
            ["forbiddenPaths"] = new JsonArray(),
            ["reuses"] = new JsonArray(new JsonObject { ["path"] = "file.txt", ["what"] = "the tool" }),
            ["creates"] = new JsonArray(),
            ["replaces"] = new JsonArray(),
            ["tests"] = new JsonArray([.. (tests ?? (noTests is null ? ["OneTests"] : [])).Select(unit => (JsonNode?)unit)]),
            ["noTests"] = noTests ?? "",
        };

        item["classes"] = new JsonArray([.. classes ?? []]);
        item["noClass"] = noClass ?? (classes is null || classes.Length == 0 ? "the item decides nothing: it carries a document" : "");

        return item;
    }

    /// <summary>A class of the plan, sound as given: its home among the item's own paths, the home itself its one site.</summary>
    private static JsonObject Class(string id = "C1", string home = "src/one/home.cs", bool exists = true, string action = "fix-at-home", JsonObject[]? sites = null, JsonObject[]? searches = null, JsonObject? universe = null)
        => new()
        {
            ["id"] = id,
            ["invariant"] = "The report is decided by the frame for every week it covers",
            ["home"] = new JsonObject { ["path"] = home, ["symbol"] = "Decides", ["exists"] = exists },
            ["sites"] = new JsonArray([.. sites ?? [Site(path: home)]]),
            ["searches"] = new JsonArray([.. searches ?? [Search()]]),
            ["universe"] = universe ?? Universes.Of(("kinds", ["weekly", "monthly"])),
            ["action"] = action,
            ["notCovered"] = "",
        };

    /// <summary>A site of a class, the home itself by default.</summary>
    private static JsonObject Site(string path = "src/one/home.cs", string then = "home", string symbol = "Decides")
        => new() { ["path"] = path, ["symbol"] = symbol, ["decides"] = "it decides the report", ["then"] = then };

    /// <summary>A search that found the sites, by the data that decides.</summary>
    private static JsonObject Search(string query = "the diagnostic DX")
        => new() { ["data"] = "the diagnostic DX", ["via"] = "text", ["query"] = query, ["hits"] = 1 };

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var made in _made)
        {
            made.Dispose();
        }
    }

    /// <summary>A plan whose items carry sound classes - and one that decides nothing and says why - passes.</summary>
    [Fact]
    public async Task ASoundPlan_Passes()
    {
        var (_, repository) = await RepositoryAsync();
        var consolidated = Class(
            sites: [Site(), Site("src/one/second.cs", then: "reads-home")],
            action: "consolidate");

        Assert.Empty(await CheckAsync(repository, Item(classes: [consolidated]), Item("two", "src/two/**", classes: [Class("C2", "src/two/home.cs")])));
    }

    /// <summary>P-C1: an item declares its classes or says why it has none - one without either decides something unnamed, and one with both says two things.</summary>
    [Fact]
    public async Task AnItemWithoutAClassAndAReason_IsAProblem_AsIsOneWithBoth()
    {
        var (_, repository) = await RepositoryAsync();
        var neither = Item();
        neither.Remove("classes");
        neither.Remove("noClass");
        var both = Item("two", "src/two/**", classes: [Class("C1", "src/two/home.cs")], noClass: "the item decides nothing");

        Assert.Equal(
            [
                "work item 'one' declares no class and says no reason: an item that changes a decision gives it a class, and one that decides nothing - a document, a rename the tools make, a dependency bump - says why in noClass",
                "work item 'two' gives both classes and noClass: noClass says why an item decides nothing, and this one has classes",
            ],
            await CheckAsync(repository, neither, both));
    }

    /// <summary>
    /// P-C9: an item names the tests its change affects or says why it affects none - one without either would be tested on
    /// nothing, unsaid, and one with both says two things; a unit that is blank, or tests that are no list of units, are
    /// problems too. An item that names its units, or gives a reason alone, is sound.
    /// </summary>
    [Fact]
    public async Task AnItemWithoutTestsAndAReason_IsAProblem_AsIsOneWithBoth_OrABlankUnit()
    {
        var (_, repository) = await RepositoryAsync();
        var neither = Item(tests: []);
        neither.Remove("noTests");
        var absent = Item("two", "src/two/**");
        absent.Remove("tests");
        absent.Remove("noTests");
        var both = Item("three", "src/three/**", tests: ["ThreeTests"], noTests: "it carries a document");
        var blank = Item("four", "src/four/**", tests: ["FourTests", " "]);
        var shapeless = Item("five", "src/five/**");
        shapeless["tests"] = "FiveTests";

        Assert.Equal(
            [
                "work item 'one' names no test and says no reason: an item is tested on the tests its change affects alone, so it names them in tests, or says in noTests why its change affects none - a document, a comment, a file no test reads",
                "work item 'two' names no test and says no reason: an item is tested on the tests its change affects alone, so it names them in tests, or says in noTests why its change affects none - a document, a comment, a file no test reads",
                "work item 'three' gives both tests and noTests: noTests says why an item's change affects no test, and this one names tests",
                "work item 'four' names a test unit that is empty, or spaces alone: a runner reads it as no filter at all, or as one that matches every test - name the tests, or take it out",
                "work item 'five' gives tests that are not a list of units: each entry names the tests of one unit, written as a leg's runner takes it after its filter option",
            ],
            await CheckAsync(repository, neither, absent, both, blank, shapeless));
        Assert.Empty(await CheckAsync(repository, Item(tests: ["OneTests", "OtherTests"]), Item("two", "src/two/**", noTests: "it carries a document")));
    }

    /// <summary>P-C2: a class id is used once in a plan, for findings, records and the ledger name a class by it.</summary>
    [Fact]
    public async Task AClassIdUsedTwiceInThePlan_IsAProblem()
    {
        var (_, repository) = await RepositoryAsync();

        Assert.Equal(
            ["the class id 'C1' is used twice in the plan, by work items 'one' and 'two': an id names one class"],
            await CheckAsync(repository, Item(classes: [Class()]), Item("two", "src/two/**", classes: [Class("C1", "src/two/home.cs")])));
    }

    /// <summary>P-C3: the home of a class is the item's to change, and where it exists it is in the tree - its site with it, for the site is the home.</summary>
    [Fact]
    public async Task AHomeTheItemDoesNotOwnOrTheTreeDoesNotHold_IsAProblem()
    {
        var (_, repository) = await RepositoryAsync();
        var elsewhere = Item(classes: [Class(home: "src/other/other.cs")]);
        var missing = Item("two", "src/two/**", classes: [Class("C2", "src/two/none.cs")]);

        Assert.Equal(
            [
                "work item 'one''s C1 has its home at 'src/other/other.cs', which the item does not own: the home changes, and the item that changes it owns it",
                "work item 'two''s C2 has its home at 'src/two/none.cs', which is not in the main tree: a class is closed at what is there, or its home is created (exists: false)",
                "work item 'two''s C2 has its site at 'src/two/none.cs', which is not in the main tree: the sites are the places that decide today",
            ],
            await CheckAsync(repository, elsewhere, missing));
    }

    /// <summary>
    /// P-C4: a site that must change - it will read the home, or its duplicate deciding is deleted - is the item's:
    /// patching some of a class's sites is never done, and the class belongs to the item that owns them.
    /// </summary>
    [Fact]
    public async Task ASiteOutsideTheItem_IsAProblem()
    {
        var (_, repository) = await RepositoryAsync();
        var patched = Item(classes: [Class(sites: [Site(), Site("src/other/other.cs", then: "reads-home")], action: "consolidate")]);

        Assert.Equal(
            ["work item 'one''s C1 has its site at 'src/other/other.cs' (reads-home) outside the item: the item owns it, or the class belongs to the item that owns it"],
            await CheckAsync(repository, patched));
    }

    /// <summary>
    /// P-C5: more than one site means consolidate; one site that is not the home means it too; and create-home is
    /// refused where the home exists.
    /// </summary>
    [Fact]
    public async Task AClassOfManySitesOrOneThatIsNotTheHome_WithoutConsolidating_IsAProblem()
    {
        var (_, repository) = await RepositoryAsync();
        var many = Item(classes: [Class(sites: [Site(), Site("src/one/second.cs", then: "reads-home")])]);
        var notHome = Item("two", "src/two/**", classes: [Class("C2", "src/two/home.cs", sites: [Site("src/two/site.cs", then: "reads-home")])]);
        var created = Item("created", "src/created/**", classes: [Class("C3", "src/created/home.cs", action: "create-home")]);

        Assert.Equal(
            [
                "work item 'one''s C1 holds 2 sites and its action is 'fix-at-home': more than one site means consolidate - the home decides, every other site reads it",
                "work item 'two''s C2 holds one site, which is not its home, and its action is 'fix-at-home': consolidate - the home decides, every other site reads it",
                "work item 'created''s C3 says create-home and its home exists: create-home is where no place decides it today",
            ],
            await CheckAsync(repository, many, notHome, created));
    }

    /// <summary>P-C6: a class is searched by data it can name again - a class without a search, or a search without a query, found nothing checkable.</summary>
    [Fact]
    public async Task AClassWithoutASearchOrASearchWithoutAQuery_IsAProblem()
    {
        var (_, repository) = await RepositoryAsync();
        var unsearched = Item(classes: [Class(searches: [])]);
        var unqueried = Item("two", "src/two/**", classes: [Class("C2", "src/two/home.cs", searches: [Search(query: "")])]);

        Assert.Equal(
            [
                "work item 'one''s C1 names no search: the sites are found by searching the data, and each search is recorded",
                "work item 'two''s C2 has a search that gives no query: a search is made again by its query",
            ],
            await CheckAsync(repository, unsearched, unqueried));
    }

    /// <summary>
    /// P-C7: a universe counts the cells its axes that apply make - the product of the counts of their values, a further
    /// axis's among them, and 1 where none applies - its count written as JSON writes a whole number; a count that is not
    /// the product, or no count at all, is a problem naming the product and what makes it. Which axes a universe answers
    /// is its schema's to hold, never the check's: a universe in which no axis applies is checked for its count alone.
    /// </summary>
    [Fact]
    public async Task AUniverseWhoseCellsAreNotTheProductOfItsAxesThatApply_IsAProblem()
    {
        var (_, repository) = await RepositoryAsync();
        var miscounted = Universes.Of(("formats", ["cs", "md"]), ("directions", ["in", "out", "both"]));
        miscounted["cells"] = 4;
        var further = Universes.Of(("formats", ["cs", "md"]), ("region", ["eu", "us", "asia"]));
        further["cells"] = 2;
        var none = Universes.Of();
        none["cells"] = 2;
        var uncounted = Universes.Of(("kinds", ["weekly", "monthly"]));
        uncounted.Remove("cells");
        var whole = Universes.Of(("kinds", ["weekly", "monthly"]), ("tiers", ["parse", "check", "emit"]));
        whole["cells"] = JsonNode.Parse("6.0");
        const string Rule = "a universe's cells are the product of the counts of the values of its axes that apply";

        Assert.Equal(
            [
                $"work item 'one''s C1 counts 4 cells in its universe, and its axes that apply make 6 (formats 2 x directions 3): {Rule}",
                $"work item 'two''s C2 counts 2 cells in its universe, and its axes that apply make 6 (formats 2 x region 3): {Rule}",
                $"work item 'two''s C3 counts 2 cells in its universe, and no axis of it applies, which makes 1: {Rule}",
                $"work item 'two''s C4 counts no cells in its universe, and its axes that apply make 2 (kinds 2): {Rule}",
            ],
            await CheckAsync(
                repository,
                Item(classes: [Class(universe: miscounted)]),
                Item("two", "src/two/**", classes: [Class("C2", "src/two/home.cs", universe: further), Class("C3", "src/two/home.cs", universe: none), Class("C4", "src/two/home.cs", universe: uncounted)]),
                Item("three", "src/created/**", classes: [Class("C5", "src/created/home.cs", universe: whole), Class("C6", "src/created/home.cs", universe: Universes.Of())])));
    }

    /// <summary>
    /// P-C8: two items' classes name the same home, by path and symbol - one home, one item. The second item's home is
    /// not its own (P-C3), and the two names under one home are said beside it: the home belongs to one item.
    /// </summary>
    [Fact]
    public async Task TwoItemsHoldingOneHome_IsAProblem()
    {
        var (_, repository) = await RepositoryAsync();
        var other = Item("two", "src/two/**", classes: [Class("C2", "src/one/home.cs")]);

        Assert.Equal(
            [
                "work item 'two''s C2 has its home at 'src/one/home.cs', which the item does not own: the home changes, and the item that changes it owns it",
                "work items 'one' and 'two' both hold a class whose home is 'src/one/home.cs' at 'Decides': one home, one item",
            ],
            await CheckAsync(repository, Item(classes: [Class()]), other));
    }

    /// <summary>
    /// P-C8 is of two items: two classes of one item may share a home - one place that decides two things, which the
    /// item that owns it changes in one sitting - so no rule of one item's is broken, and nothing is said of it.
    /// </summary>
    [Fact]
    public async Task TwoClassesOfOneItemSharingAHome_AreSound()
    {
        var (_, repository) = await RepositoryAsync();

        Assert.Empty(await CheckAsync(repository, Item(classes: [Class(), Class("C2")])));
    }

    /// <summary>
    /// A home and a site are paths inside the tree, as the paths an item reuses and replaces are: one that climbs out, is
    /// rooted, or holds a doubled separator is the plan's problem, said by the rule that owns it, and nothing more is
    /// asked of it - never matched against the item's paths, which would end the check as the engine's own failure, nor
    /// looked for in the tree, which a rooted path would leave. The rest of its class is still checked.
    /// </summary>
    [Fact]
    public async Task AHomeOrASiteThatIsNoPathInsideTheTree_IsAProblemOfThePlan_NeverAFailureOfTheCheck()
    {
        var (_, repository) = await RepositoryAsync();
        var climbing = Item(classes: [Class(home: "src/one/../one/home.cs")]);
        var doubled = Item("two", "src/two/**", classes: [Class("C2", "src/two/home.cs", action: "consolidate", sites: [Site("src/two/home.cs"), Site("src//two/site.cs", then: "reads-home")])]);
        var beside = Item("created", "src/created/**", classes: [Class("C3", "src/created/home.cs", action: "consolidate", sites: [Site("src/created/home.cs"), Site("../shared/site.cs", then: "reads-home")])]);
        var rooted = Item("four", "src/four/**", classes: [Class("C4", "/src/four/home.cs", searches: [])]);

        Assert.Equal(
            [
                "the home work item 'one''s C1 names, 'src/one/../one/home.cs', is not a path relative to the tree, spelt with forward slashes",
                "a site work item 'one''s C1 names, 'src/one/../one/home.cs', is not a path relative to the tree, spelt with forward slashes",
                "a site work item 'two''s C2 names, 'src//two/site.cs', is not a path relative to the tree, spelt with forward slashes",
                "a site work item 'created''s C3 names, '../shared/site.cs', is not a path relative to the tree, spelt with forward slashes",
                "the home work item 'four''s C4 names, '/src/four/home.cs', is not a path relative to the tree, spelt with forward slashes",
                "a site work item 'four''s C4 names, '/src/four/home.cs', is not a path relative to the tree, spelt with forward slashes",
                "work item 'four''s C4 names no search: the sites are found by searching the data, and each search is recorded",
            ],
            await CheckAsync(repository, climbing, doubled, beside, rooted));
    }

    /// <summary>
    /// A home and a site are compared as paths, whichever separator they are written with: a site written with
    /// backslashes at the home is the home, and a class whose one site it is needs no consolidation.
    /// </summary>
    [Fact]
    public async Task ASiteWrittenWithBackslashesAtTheHome_IsTheHome()
    {
        var (_, repository) = await RepositoryAsync();

        Assert.Empty(await CheckAsync(repository, Item(classes: [Class(sites: [Site("src\\one\\home.cs")])])));
    }

    /// <summary>
    /// A site is the home exactly where its path and its symbol are the home's, and only that site is marked home: one
    /// marked home elsewhere - in another item's paths, or at another symbol of the home's own file - is a place that
    /// decides today, which a mark of home would leave deciding, held to the item's paths as every other site is; and
    /// the home's own site marked as a site that reads the home is refused too.
    /// </summary>
    [Fact]
    public async Task ASiteMarkedHomeThatIsNotTheHome_OrTheHomeMarkedOtherwise_IsAProblem()
    {
        var (_, repository) = await RepositoryAsync();
        var foreign = Item(classes: [Class(action: "consolidate", sites: [Site(), Site("src/other/other.cs")])]);
        var unmarked = Item("two", "src/two/**", classes: [Class("C2", "src/two/home.cs", action: "consolidate", sites: [Site("src/two/home.cs", then: "reads-home"), Site("src/two/site.cs", then: "reads-home")])]);
        var otherSymbol = Item("created", "src/created/**", classes: [Class("C3", "src/created/home.cs", action: "consolidate", sites: [Site("src/created/home.cs"), Site("src/created/home.cs", symbol: "Elsewhere")])]);

        Assert.Equal(
            [
                "work item 'one''s C1 has its site at 'src/other/other.cs' marked home, and its home is 'src/one/home.cs' at 'Decides': a site is the home only at the home's path and symbol - every other site reads the home or is deleted",
                "work item 'one''s C1 has its site at 'src/other/other.cs' (home) outside the item: the item owns it, or the class belongs to the item that owns it",
                "work item 'two''s C2 has its site at 'src/two/home.cs' marked 'reads-home', and it is its home: the home's own site is marked home, for it is the place that decides",
                "work item 'created''s C3 has its site at 'src/created/home.cs' marked home, and its home is 'src/created/home.cs' at 'Decides': a site is the home only at the home's path and symbol - every other site reads the home or is deleted",
            ],
            await CheckAsync(repository, foreign, unmarked, otherSymbol));
    }
}
