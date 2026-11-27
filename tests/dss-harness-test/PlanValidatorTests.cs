using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The machine's check of a plan's classes (P-C1 to P-C8): an item declares the decisions it changes or says why it
/// changes none; a class id names one class in a plan; each home and each site is a path inside the tree; the home of a
/// class is the item's own to change and there in the tree where it exists; a site is marked home exactly where it is
/// the home, and every other site, which must change, is the item's too, for patching some of a class's sites is never
/// done; more than one site means consolidate, as one site that is not the home does; a class is searched by data it
/// can name again, and its universe answers each axis one of the two ways an axis is answered and counts the cells its
/// axes that apply make; and one home belongs to one item. And of
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
        => await CheckAsync(repository, await new HarnessFactory().StartTreeAsync(repository.Path, Token), items);

    /// <summary>The check of a plan's items against <paramref name="startTree"/>, as the validate-plan step of a run that started there makes it.</summary>
    private static async Task<IReadOnlyList<string>> CheckAsync(TempDirectory repository, TreeDirectories startTree, params JsonNode?[] items)
    {
        var harness = new HarnessFactory();

        return new PlanValidator(harness.FileSystem, harness.PathBudget, harness.Platform)
            .Check(items, await harness.ContextLoader.LoadAsync(repository.Path, Token), "r1", Flow, startTree);
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

    /// <summary>
    /// Each path an item owns or must not touch is read from the repository's root, as its agent's writes and its fold's
    /// within read it: the plans the live runs had refused pass - an item owning the root's calc.c, calc.h and main.c that
    /// must not touch the root's Makefile, and one owning src/click's types.py and a test beside one owning the root's
    /// pyproject.toml and uv.lock - for a root-level name meets no other root-level name and no nested file. What does meet
    /// is refused still: a directory the tree holds and a file in it, a pattern and what it matches, and a path not yet
    /// made and one beneath it.
    /// </summary>
    [Fact]
    public async Task EachPathAnItemNames_IsReadFromTheRoot_SoARootLevelFileMeetsNoOtherPath()
    {
        var repository = new TempDirectory();
        _made.Add(repository);

        foreach (var file in new[] { "file.txt", "Makefile", "calc.c", "calc.h", "main.c", "pyproject.toml", "uv.lock", "src/click/testing.py", "src/click/types.py", "tests/test_types/test_Tuple.py" })
        {
            repository.WriteFile(file, "content\n");
        }

        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(repository.Path, Token);
        await harness.CommitAllAsync(repository.Path, "initial", Token);

        Assert.Empty(await CheckAsync(
            repository,
            Owning("add-mul", ["calc.c", "calc.h", "main.c"], forbidden: ["Makefile"]),
            Owning("tuple-types", ["src/click/types.py", "tests/test_types/test_Tuple.py"], forbidden: ["src/click/testing.py"]),
            Owning("packaging", ["pyproject.toml", "uv.lock"], forbidden: ["src/**"])));

        Assert.Equal(
            [
                "work items 'package' and 'testing' both own what 'src/click' and 'src/click/testing.py' match: no file is written by two items, so give it to one",
                "work items 'globbed' and 'built' both own what '*.c' and 'calc.c' match: no file is written by two items, so give it to one",
                "work items 'made' and 'inside' both own what 'src/new' and 'src/new/a.py' match: no file is written by two items, so give it to one",
                "work item 'kept' owns 'tests/test_types' and must not touch 'tests/test_types/test_Tuple.py', which match the same paths: an item's forbidden paths are kept by the paths it owns",
            ],
            await CheckAsync(
                repository,
                Owning("package", ["src/click"]),
                Owning("testing", ["src/click/testing.py"]),
                Owning("globbed", ["*.c"]),
                Owning("built", ["calc.c"]),
                Owning("made", ["src/new"]),
                Owning("inside", ["src/new/a.py"]),
                Owning("kept", ["tests/test_types"], forbidden: ["tests/test_types/test_Tuple.py"])));

        static JsonObject Owning(string name, string[] owned, string[]? forbidden = null)
        {
            var item = Item(name);
            item["ownedPaths"] = new JsonArray([.. owned.Select(path => (JsonNode?)path)]);
            item["forbiddenPaths"] = new JsonArray([.. (forbidden ?? []).Select(path => (JsonNode?)path)]);

            return item;
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
    /// A home or a site outside the item's owned paths only because the reading from the root changed what an entry meant
    /// says how to write what it meant, in the words a step's writes and a fold's within say it: a directory to be made,
    /// named with no trailing separator, names a file; a wildcard of one name names the root's own. Written so, they pass.
    /// </summary>
    [Fact]
    public async Task AHomeOrASiteAnEntrysReadingLeftOut_SaysHowToWriteWhatItMeant()
    {
        var (_, repository) = await RepositoryAsync();
        var made = Item("made", "src/fresh", classes: [Class("C1", "src/fresh/home.cs", exists: false, action: "consolidate", sites: [Site("src/one/home.cs", then: "reads-home")])]);
        made["ownedPaths"] = new JsonArray("src/fresh", "src/one/**");
        var wild = Item("wild", "*.cs", classes: [Class("C2", "src/two/home.cs", sites: [Site("src/two/home.cs"), Site("src/two/site.cs", then: "reads-home")], action: "consolidate")]);
        wild["ownedPaths"] = new JsonArray("*.cs", "src/two/home.cs");

        Assert.Equal(
            [
                "work item 'made''s C1 has its home at 'src/fresh/home.cs', which the item does not own: the home changes, and the item that changes it owns it; 'src/fresh' names a file: a directory to be made is written 'src/fresh/'",
                "work item 'wild''s C2 has its site at 'src/two/site.cs' (reads-home) outside the item: the item owns it, or the class belongs to the item that owns it; '*.cs' names the root's own: '**/*.cs' names any depth",
            ],
            await CheckAsync(repository, made, wild));

        made["ownedPaths"] = new JsonArray("src/fresh/", "src/one/**");
        wild["ownedPaths"] = new JsonArray("**/*.cs");

        // Each alone: '**/*.cs' owns what the other item owns too.
        Assert.Empty(await CheckAsync(repository, made));
        Assert.Empty(await CheckAsync(repository, wild));
    }

    /// <summary>
    /// An entry an item owns, written with no trailing separator, that names a directory of the main tree the run's start
    /// tree does not hold - one an earlier round's fold made - names that one path to every reader of the item's paths: the
    /// builder's every write beneath it would be refused, and its fold would carry none of it. So the check refuses it, in
    /// the words every refusal of the reading uses, saying to write it with a trailing separator; a directory both trees
    /// hold, and the same entry written so, pass. An entry an item must not touch, written so, is no problem: nothing
    /// gates a write by a forbidden path, so none would be refused for it.
    /// </summary>
    [Fact]
    public async Task AnOwnedEntryNamingADirectoryOnlyTheMainTreeHolds_IsAProblem_SayingToWriteItEndingInASeparator_AndAForbiddenOneIsNot()
    {
        var (harness, repository) = await RepositoryAsync();
        var startTree = await harness.StartTreeAsync(repository.Path, Token);

        // The first round's fold made src/feature in the main tree since the run began.
        repository.WriteFile("src/feature/made.cs", "made by the first round\n");
        await harness.CommitAllAsync(repository.Path, "the first round", Token);

        var made = Item("made", "src/feature");
        var kept = Item("kept", "src/two/**");
        kept["forbiddenPaths"] = new JsonArray("src/feature");
        var there = Item("there", "src/other");

        // The forbidden entry is said of nothing: the owned one alone is refused.
        Assert.Equal(
            ["work item 'made' owns 'src/feature', which names nothing beneath it: 'src/feature' names a file of the run's start tree, which the run's lists of paths are read against, and a directory of the main tree: write it 'src/feature/'"],
            await CheckAsync(repository, startTree, made, kept, there));
        Assert.Empty(await CheckAsync(repository, startTree, kept));

        made["ownedPaths"] = new JsonArray("src/feature/");

        Assert.Empty(await CheckAsync(repository, startTree, made, kept, there));
    }

    /// <summary>
    /// Against a start tree that could not be read, two lists of entries that meet are named by a pair that meets whatever
    /// the tree holds - read narrowly, as what may be written beneath each - with nothing said of the tree, before any pair
    /// that meets only because every path was taken for one of its directories: a real conflict is never hidden behind one
    /// the unread tree made, nor said with that tree's reason. Where no pair meets narrowly, the wide reading's pair is
    /// named with the reason - two items' owned paths, and an item's owned and forbidden ones alike.
    /// </summary>
    [Fact]
    public async Task AgainstAStartTreeThatCouldNotBeRead_AnOverlapNamesAPairThatMeetsWhateverTheTreeHolds_First()
    {
        var (_, repository) = await RepositoryAsync();
        var unread = TreeDirectories.NoCommit;
        var wide = "every path is taken for a directory of the run's start tree, which could not be read: the run names no commit it started from";
        // Paths the main tree does not hold, so nothing but the overlaps is said.
        var a = Item("a", "pkg/one");
        a["ownedPaths"] = new JsonArray("pkg/one", "lib/x.c");
        var b = Item("b", "pkg/**/*.cs");
        b["ownedPaths"] = new JsonArray("pkg/**/*.cs", "lib/x.c");
        var kept = Item("kept", "etc/one");
        kept["ownedPaths"] = new JsonArray("etc/one", "etc/y.c");
        kept["forbiddenPaths"] = new JsonArray("etc/**/*.h", "etc/y.c");

        Assert.Equal(
            [
                "work items 'a' and 'b' both own what 'lib/x.c' and 'lib/x.c' match: no file is written by two items, so give it to one",
                "work item 'kept' owns 'etc/y.c' and must not touch 'etc/y.c', which match the same paths: an item's forbidden paths are kept by the paths it owns",
            ],
            await CheckAsync(repository, unread, a, b, kept));

        // With the pair that meets whatever the tree holds gone, the one the wide reading alone makes is named, saying why.
        b["ownedPaths"] = new JsonArray("pkg/**/*.cs", "lib/z.c");
        kept["forbiddenPaths"] = new JsonArray("etc/**/*.h", "etc/w.c");

        Assert.Equal(
            [
                $"work items 'a' and 'b' both own what 'pkg/one' and 'pkg/**/*.cs' match: no file is written by two items, so give it to one; {wide}",
                $"work item 'kept' owns 'etc/one' and must not touch 'etc/**/*.h', which match the same paths: an item's forbidden paths are kept by the paths it owns; {wide}",
            ],
            await CheckAsync(repository, unread, a, b, kept));
    }

    /// <summary>
    /// Against a start tree that could not be read, every refusal its reading causes says why: an entry read as naming no
    /// directory - what may be written beneath it - that the main tree holds as one, and a class's home and site beneath
    /// it, say it after how to write the entry; two items' entries that meet only because every path was taken for a
    /// directory - where two entries are weighed for meeting - say it too. A refusal the tree did not cause says nothing of it.
    /// </summary>
    [Fact]
    public async Task AgainstAStartTreeThatCouldNotBeRead_EveryRefusalItsReadingCauses_SaysWhy()
    {
        var (_, repository) = await RepositoryAsync();
        var unread = TreeDirectories.NoCommit;
        var narrow = "no path is taken for a directory of the run's start tree, which could not be read: the run names no commit it started from";
        var wide = "every path is taken for a directory of the run's start tree, which could not be read: the run names no commit it started from";
        var classed = Item("a", "src/one", classes: [Class(sites: [Site(), Site("src/one/second.cs", then: "reads-home")], action: "consolidate")]);
        var wild = Item("b", "src/**/*.cs");
        var file = Item("c", "src/created/home.cs");

        Assert.Equal(
            [
                $"work items 'a' and 'b' both own what 'src/one' and 'src/**/*.cs' match: no file is written by two items, so give it to one; {wide}",
                $"work item 'a' owns 'src/one', which names nothing beneath it: 'src/one' names a file of the run's start tree, which the run's lists of paths are read against, and a directory of the main tree: write it 'src/one/'; {narrow}",
                $"work item 'a''s C1 has its home at 'src/one/home.cs', which the item does not own: the home changes, and the item that changes it owns it; 'src/one' names a file: a directory to be made is written 'src/one/'; {narrow}",
                $"work item 'a''s C1 has its site at 'src/one/second.cs' (reads-home) outside the item: the item owns it, or the class belongs to the item that owns it; 'src/one' names a file: a directory to be made is written 'src/one/'; {narrow}",

                // A file a pattern matches meets it whatever the tree holds: the tree caused none of it.
                "work items 'b' and 'c' both own what 'src/**/*.cs' and 'src/created/home.cs' match: no file is written by two items, so give it to one",
            ],
            await CheckAsync(repository, unread, classed, wild, file));
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
    /// P-C7: every axis of a universe - each the schema lists and each further one - is answered in the one shape of an
    /// axis and in one of its two ways: applying exactly where it names values, with where the repository declares them
    /// and no why; not applying where it names none, with why and nothing declared. An answer that is neither is a
    /// problem naming the axis and what it says wrongly, a blank declaredIn or why read as none; the plan's check holds it
    /// within the step that gave it, as the schema holds the shape, so a planner chooses no shape and an answer neither way
    /// is put right before an audit reads it.
    /// </summary>
    [Fact]
    public async Task AnAxisAnsweredNeitherWay_IsAProblem()
    {
        var (_, repository) = await RepositoryAsync();
        var undeclared = Universes.Of(("formats", ["cs", "md"]));
        undeclared["formats"] = Universes.Answer(["cs", "md"], " ", string.Empty);
        var reasonedToo = Universes.Of(("formats", ["cs", "md"]));
        reasonedToo["formats"] = Universes.Answer(["cs", "md"], "closure.md", "it has one format");
        var unreasoned = Universes.Of();
        unreasoned["forms"] = Universes.Answer([], string.Empty, " ");
        var declaredNone = Universes.Of();
        declaredNone["tiers"] = Universes.Answer([], "src/Tiers.cs", "the decision passes one tier");
        var further = Universes.Of(("region", ["eu"]));
        further["further"]![0]!["answer"] = Universes.Answer(["eu"], string.Empty, "every region is one");
        const string Rule = "an axis applies exactly where it names values - then it says where the repository declares them, and gives no why - and does not apply where it names none - then it says why, and declares nothing";

        Assert.Equal(
            [
                $"work item 'one''s C1 names the values of its axis 'formats' and says not where the repository declares them: {Rule}",
                $"work item 'one''s C2 names the values of its axis 'formats' and says why it does not apply: {Rule}",
                $"work item 'one''s C3 names no value of its axis 'forms' and says not why it does not apply: {Rule}",
                $"work item 'one''s C4 names no value of its axis 'tiers' and says where its values are declared: {Rule}",
                $"work item 'one''s C5 names the values of its axis 'region' and says not where the repository declares them, and why it does not apply: {Rule}",
            ],
            await CheckAsync(
                repository,
                Item(classes: [Class(universe: undeclared), Class("C2", universe: reasonedToo), Class("C3", universe: unreasoned), Class("C4", universe: declaredNone), Class("C5", universe: further)])));

        // Each answered one way or the other, the universes pass.
        Assert.Empty(await CheckAsync(repository, Item(classes: [Class(universe: Universes.Of(("formats", ["cs", "md"]), ("region", ["eu"]))), Class("C2", universe: Universes.Of())])));
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
