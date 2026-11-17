using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Core.Orchestration;

/// <summary>The exits a <c>class-closure</c> check fails with: its record's problems (1), and a home audit it requires (2).</summary>
public static class ClassClosureExit
{
    /// <summary>The record has problems: the repair is <c>close-class</c>, which goes on with the implementation.</summary>
    public const int Findings = 1;

    /// <summary>
    /// The record holds no home audit where one is required - the home was touched by other work, or the class was reopened
    /// twice: the repair is <c>home-audit</c>, not a closing of the record.
    /// </summary>
    public const int HomeAudit = 2;
}

/// <summary>
/// One class a plan declares an item decides, as the plan's own check reads it: its id, its invariant, its one home, its
/// sites, its searches and its universe - the shape <c>plan.schema.json</c> holds as <c>closureClass</c>.
/// </summary>
/// <param name="Id">Its id, unique in the plan.</param>
/// <param name="Invariant">What it decides, in one sentence.</param>
/// <param name="HomePath">The file its one home is declared in, as git names it.</param>
/// <param name="HomeSymbol">The symbol its one home is declared as.</param>
/// <param name="Sites">Each path it is decided at today: the file, the symbol, and what the plan says of it after.</param>
/// <param name="Axes">The names of its universe's axes.</param>
public sealed record PlannedClass(
    string Id,
    string Invariant,
    string HomePath,
    string HomeSymbol,
    IReadOnlyList<(string Path, string Symbol, string? Then)> Sites,
    IReadOnlyList<string> Axes)
{
    /// <summary>
    /// The class <paramref name="node"/> declares, or <see langword="null"/> where it is not an object: the plan's own
    /// check has held its shape, so this reads without judging it again.
    /// </summary>
    /// <param name="node">One entry of a plan item's <c>classes</c>.</param>
    public static PlannedClass? In(JsonNode? node)
        => node is JsonObject entry && Text(entry["id"]) is { } id && entry["home"] is JsonObject home
            ? new PlannedClass(
                id,
                Text(entry["invariant"]) ?? string.Empty,
                Text(home["path"]) ?? string.Empty,
                Text(home["symbol"]) ?? string.Empty,
                ReadSites(entry["sites"] as JsonArray),
                ReadAxes(entry["universe"] as JsonObject))
            : null;

    private static IReadOnlyList<(string Path, string Symbol, string? Then)> ReadSites(JsonArray? sites)
    {
        var read = new List<(string, string, string?)>();

        foreach (var node in sites ?? [])
        {
            if (node is JsonObject site && Text(site["path"]) is { } path)
            {
                read.Add((path, Text(site["symbol"]) ?? string.Empty, Text(site["then"])));
            }
        }

        return read;
    }

    private static IReadOnlyList<string> ReadAxes(JsonObject? universe) => ClassClosureReading.Axes(universe);

    private static string? Text(JsonNode? node) => ClassClosureReading.Text(node);
}

/// <summary>What the closing models read of the JSON they are handed, written once.</summary>
internal static class ClassClosureReading
{
    /// <summary>The names of a universe's axes, or none where it holds no object.</summary>
    public static IReadOnlyList<string> Axes(JsonObject? universe)
    {
        var read = new List<string>();

        foreach (var node in universe?["axes"] as JsonArray ?? [])
        {
            if (Text(node?["name"]) is { } name)
            {
                read.Add(name);
            }
        }

        return read;
    }

    /// <summary>The text a node holds, or null where it holds none.</summary>
    public static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>
/// One class's closing record, as the implement step's answer holds it: the shape <c>implement.schema.json</c> requires of
/// every entry of <c>closure</c>, read here as one model so the check that judges it and the fold that judges a manual
/// lane's <c>closure.json</c> read the same thing.
/// </summary>
/// <param name="Class">The class it closes, by the id the plan knows it by.</param>
/// <param name="Invariant">What the class decides, in one sentence.</param>
/// <param name="HomePath">The file its home is declared in, as git names it.</param>
/// <param name="HomeSymbol">The symbol its home is declared as.</param>
/// <param name="Sites">Each path it was decided at: the file, the symbol, what decided there before, and what decides there after.</param>
/// <param name="Searches">Each search the data was searched by: what was searched, how, the query, and the call that made it.</param>
/// <param name="Axes">The names of the universe the record pins.</param>
/// <param name="PinTests">Each test the pin names.</param>
/// <param name="ArmId">The arm that proves the class closed, where one is named.</param>
/// <param name="ArmWhy">Why no arm is named, where none is.</param>
/// <param name="Neighbours">Each neighbour: its cell, what measured it, and what the measurement showed.</param>
/// <param name="Claim">The closing claim: the universe, never a case.</param>
/// <param name="HomeAudit">The home audit's record, where one was made.</param>
public sealed record ClassClosureRecord(
    string Class,
    string Invariant,
    string HomePath,
    string HomeSymbol,
    IReadOnlyList<(string Path, string Symbol, string Before, string After)> Sites,
    IReadOnlyList<(string Data, string Query, string Call)> Searches,
    IReadOnlyList<string> Axes,
    IReadOnlyList<string> PinTests,
    string? ArmId,
    string? ArmWhy,
    IReadOnlyList<(string Cell, string MeasuredBy, string Result, string Reason)> Neighbours,
    string Claim,
    ClassHomeAudit? HomeAudit)
{
    /// <summary>
    /// The record <paramref name="node"/> holds, or <see langword="null"/> where it is not an object: the schema the
    /// answer was held to has already judged its shape.
    /// </summary>
    /// <param name="node">One entry of the implement step's <c>closure</c>, or of a manual lane's <c>closure.json</c>.</param>
    public static ClassClosureRecord? In(JsonNode? node)
        => node is JsonObject entry && Text(entry["class"]) is { } declared
            ? new ClassClosureRecord(
                declared,
                Text(entry["invariant"]) ?? string.Empty,
                Text(entry["home"]?["path"]) ?? string.Empty,
                Text(entry["home"]?["symbol"]) ?? string.Empty,
                ReadSites(entry["sites"] as JsonArray),
                ReadSearches(entry["searches"] as JsonArray),
                ClassClosureReading.Axes(entry["universe"] as JsonObject),
                ReadTests((entry["pin"] as JsonObject)?["tests"] as JsonArray),
                Text(entry["arm"]?["id"]),
                Text(entry["arm"]?["why"]),
                ReadNeighbours(entry["neighbours"] as JsonArray),
                Text(entry["claim"]) ?? string.Empty,
                ClassHomeAudit.In(entry["homeAudit"]))
            : null;

    private static IReadOnlyList<(string Path, string Symbol, string Before, string After)> ReadSites(JsonArray? sites)
    {
        var read = new List<(string, string, string, string)>();

        foreach (var node in sites ?? [])
        {
            if (node is JsonObject site && Text(site["path"]) is { } path)
            {
                read.Add((path, Text(site["symbol"]) ?? string.Empty, Text(site["before"]) ?? string.Empty, Text(site["after"]) ?? string.Empty));
            }
        }

        return read;
    }

    private static IReadOnlyList<(string Data, string Query, string Call)> ReadSearches(JsonArray? searches)
    {
        var read = new List<(string, string, string)>();

        foreach (var node in searches ?? [])
        {
            if (node is JsonObject search && Text(search["data"]) is { } data)
            {
                read.Add((data, Text(search["query"]) ?? string.Empty, Text(search["call"]) ?? string.Empty));
            }
        }

        return read;
    }

    private static IReadOnlyList<string> ReadTests(JsonArray? tests)
    {
        var read = new List<string>();

        foreach (var node in tests ?? [])
        {
            if (Text(node) is { } test)
            {
                read.Add(test);
            }
        }

        return read;
    }

    private static IReadOnlyList<(string Cell, string MeasuredBy, string Result, string Reason)> ReadNeighbours(JsonArray? neighbours)
    {
        var read = new List<(string, string, string, string)>();

        foreach (var node in neighbours ?? [])
        {
            if (node is JsonObject neighbour && Text(neighbour["cell"]) is { } cell)
            {
                read.Add((cell, Text(neighbour["measuredBy"]) ?? string.Empty, Text(neighbour["result"]) ?? string.Empty, Text(neighbour["reason"]) ?? string.Empty));
            }
        }

        return read;
    }

    private static string? Text(JsonNode? node) => ClassClosureReading.Text(node);
}

/// <summary>
/// The home audit a closing record carries where one was required: what the audit consolidated, the invariants the home
/// should uphold, and what is now structural - one decision, one place.
/// </summary>
/// <param name="Consolidated">What was consolidated.</param>
/// <param name="Invariants">The invariants the home should uphold, and those it upheld only by coincidence.</param>
/// <param name="Structural">What is now structural: one decision, one place.</param>
public sealed record ClassHomeAudit(IReadOnlyList<string> Consolidated, IReadOnlyList<string> Invariants, IReadOnlyList<string> Structural)
{
    /// <summary>The audit <paramref name="node"/> holds, or <see langword="null"/> where the record carries none.</summary>
    /// <param name="node">The record's <c>homeAudit</c> member.</param>
    public static ClassHomeAudit? In(JsonNode? node)
        => node is JsonObject audit
            ? new ClassHomeAudit(
                [.. Lines(audit["consolidated"])],
                [.. Lines(audit["invariants"])],
                [.. Lines(audit["structural"])])
            : null;

    private static IEnumerable<string> Lines(JsonNode? node)
        => (node as JsonArray ?? []).Select(line => line is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>();
}

/// <summary>One tool call of an item's stored streams, as a closing record cites it: which tool, and the input it was given.</summary>
/// <param name="Tool">The tool, as the stream names it.</param>
/// <param name="Input">The whole input, as the stream stored it.</param>
public sealed record ClassClosureCall(string Tool, string Input);

/// <summary>
/// What a closing record is judged against, beside itself: the paths the agent changed, the calls its stored streams hold,
/// what its worktree still declares, the arms its repository declares, and how often each home was touched in this run.
/// </summary>
/// <param name="Changed">The paths the agent changed, as the fold would write them.</param>
/// <param name="Calls">Every tool call of the item's stored streams, by the id a record cites; empty where there is no stream to read.</param>
/// <param name="Declares">
/// Whether a file of the agent's worktree still declares a symbol - the graph's view of its tree, or text where the graph
/// lists the file only. Null where there is no worktree to read: a manual lane, judged by hand.
/// </param>
/// <param name="TestDeclared">Whether a test the pin names is declared in the worktree - the graph's tests edges, or text. Null as <paramref name="Declares"/> is.</param>
/// <param name="Arms">Each arm the repository's registry declares, with the file its own site mutates; empty where none is declared.</param>
/// <param name="ArmsDeclared">Whether the repository declares an arms registry at all.</param>
/// <param name="TouchesByOthers">How often each home path was touched in this run by work other than this item's own commits.</param>
/// <param name="TouchesAll">How often each home path was touched in this run, this item's own commits among them.</param>
/// <param name="TouchesRequired">How many touches by others require a home audit; 0 where none is required.</param>
/// <param name="HotspotAt">How many touches name a home a hotspot; never reached, nothing is listed.</param>
/// <param name="ReopenedTwice">The classes the run's ledger says were reopened a second time.</param>
public sealed record ClassClosureProof(
    IReadOnlyList<string> Changed,
    IReadOnlyDictionary<string, ClassClosureCall> Calls,
    Func<string, string, bool>? Declares,
    Func<string, bool>? TestDeclared,
    IReadOnlyList<(string Id, string Site)> Arms,
    bool ArmsDeclared,
    IReadOnlyDictionary<string, int> TouchesByOthers,
    IReadOnlyDictionary<string, int> TouchesAll,
    int TouchesRequired,
    int HotspotAt,
    IReadOnlySet<string> ReopenedTwice)
{
    /// <summary>A proof that reads nothing but the record itself: a manual lane's, judged by hand, with its calls read as given.</summary>
    public static ClassClosureProof Manual(IReadOnlyList<(string Id, string Site)> arms, bool armsDeclared)
        => new(
            [],
            new Dictionary<string, ClassClosureCall>(),
            Declares: null,
            TestDeclared: null,
            arms,
            armsDeclared,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            TouchesRequired: 0,
            HotspotAt: int.MaxValue,
            new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>One home the run touched often enough to name: the path, the class whose home it is, and how often.</summary>
/// <param name="Class">The class whose home it is.</param>
/// <param name="Path">The home's path.</param>
/// <param name="Touches">How often the run touched it.</param>
public sealed record ClassClosureHotspot(string Class, string Path, int Touches);

/// <summary>One home's touches, as the check's output states them.</summary>
/// <param name="Class">The class whose home it is.</param>
/// <param name="Path">The home's path.</param>
/// <param name="ByOthers">How often work other than this item's own commits touched it in this run.</param>
/// <param name="All">How often the run touched it, this item's own commits among them.</param>
public sealed record ClassClosureTouch(string Class, string Path, int ByOthers, int All);

/// <summary>What judging the record answered: the problems of the record, the audits it requires, and each home's touches.</summary>
/// <param name="Problems">Each problem of the record, as a sentence that names the class.</param>
/// <param name="Audits">Each home audit the record requires and does not carry, with the reason.</param>
/// <param name="Hotspots">Each home touched often enough to name.</param>
/// <param name="Touches">Each home's touches.</param>
public sealed record ClassClosureJudged(
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Audits,
    IReadOnlyList<ClassClosureHotspot> Hotspots,
    IReadOnlyList<ClassClosureTouch> Touches)
{
    /// <summary>Whether the record closed every class: no problem, and no audit it lacks.</summary>
    public bool Passed => Problems.Count == 0 && Audits.Count == 0;
}

/// <summary>
/// The rules a closing record is judged by, with no model - the one owner of the judgement, read by the
/// <c>class-closure</c> step before every fold and by a manual lane's fold under <c>anchors.closure</c>: every planned
/// class has an entry; every planned site is accounted for, a deleted site no longer declaring its symbol in the worktree
/// and a site that reads its home among the paths the agent changed; every search was made - its call a tool call of the
/// item's stored streams, its input holding its query - and every neighbour was measured - its measurement a
/// <c>measure</c> call of the same streams, or reasoned where it says it was not; every test of the pin is declared in the
/// worktree; where the repository declares an arms registry, the arm is one of its arms and mutates the home's own file;
/// the claim names every axis of the universe; and where the home was touched by other work, or the class was reopened
/// twice, the record carries a home audit.
/// </summary>
public static class ClassClosureRules
{
    /// <summary>
    /// The schema a closing record is read against, wherever one is judged: the shipped implement schema's own
    /// <c>closure</c> member - the one owner of the record's shape, which the run's implement answer and a manual lane's
    /// <c>closure.json</c> are both held to. Null where this build holds no shipped schema, which never is.
    /// </summary>
    public static JsonNode? RecordSchema()
        => JsonNode.Parse(Flow.WorkflowTemplates.TextOf("item-pipeline/schemas/implement.schema.json") ?? "{}")?["properties"]?["closure"];

    /// <summary>
    /// Judges <paramref name="record"/> against the classes <paramref name="planned"/> declares and the proof
    /// <paramref name="proof"/> gathered. A record with no entry for a planned class is a problem; an entry no plan
    /// declares - a class the builder added - is judged like the rest, for its sites, its searches, its neighbours and its
    /// claim are the record's own.
    /// </summary>
    /// <param name="planned">The classes the plan declares this item decides; empty where it decides none, or a manual lane holds no plan.</param>
    /// <param name="record">The record, one entry per class; empty where the builder answered none.</param>
    /// <param name="proof">What the record is judged against, beside itself.</param>
    public static ClassClosureJudged Judge(IReadOnlyList<PlannedClass> planned, IReadOnlyList<ClassClosureRecord> record, ClassClosureProof proof)
    {
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(proof);

        List<string> problems = [];
        List<string> audits = [];
        List<ClassClosureHotspot> hotspots = [];
        List<ClassClosureTouch> touches = [];

        foreach (var entry in record)
        {
            var plannedOfClass = planned.FirstOrDefault(plannedClass => string.Equals(plannedClass.Id, entry.Class, StringComparison.Ordinal));
            problems.AddRange(EntryProblems(entry, plannedOfClass, proof));
            AuditsAndTouches(entry, proof, audits, hotspots, touches);
        }

        foreach (var declared in planned)
        {
            if (!record.Any(entry => string.Equals(entry.Class, declared.Id, StringComparison.Ordinal)))
            {
                problems.Add($"class '{declared.Id}' has no entry in the record");
            }
        }

        return new ClassClosureJudged(problems, audits, hotspots, touches);
    }

    /// <summary>The problems of one entry of the record, judged against its class's plan and the proof.</summary>
    private static IReadOnlyList<string> EntryProblems(ClassClosureRecord entry, PlannedClass? planned, ClassClosureProof proof)
    {
        List<string> problems = [];

        foreach (var (path, symbol, then) in planned?.Sites ?? [])
        {
            if (!entry.Sites.Any(site => string.Equals(site.Path, path, StringComparison.Ordinal)))
            {
                problems.Add($"class '{entry.Class}': the site '{path}' has no after in the record");
            }
        }

        foreach (var site in entry.Sites)
        {
            if (site.After == "deleted" && proof.Declares is { } declares
                && declares(site.Path, string.IsNullOrWhiteSpace(site.Symbol) ? entry.HomeSymbol : site.Symbol))
            {
                problems.Add($"class '{entry.Class}': the site '{site.Path}' is deleted, but it still declares '{site.Symbol}'");
            }

            if (site.After == "reads-home" && proof.Changed.Count > 0
                && !proof.Changed.Contains(site.Path, StringComparer.Ordinal))
            {
                problems.Add($"class '{entry.Class}': the site '{site.Path}' reads its home, but the agent changed nothing at it");
            }
        }

        foreach (var (data, query, call) in entry.Searches)
        {
            if (proof.Calls.Count == 0 && call.Length > 0)
            {
                // A manual lane, judged by hand: the call is read as given, its command line, for no stream holds it.
                continue;
            }

            if (call.Length == 0)
            {
                problems.Add($"class '{entry.Class}': the search for '{data}' cites no call: a search that was not made does not count");
                continue;
            }

            if (!proof.Calls.TryGetValue(call, out var cited))
            {
                problems.Add($"class '{entry.Class}': the search for '{data}' cites the call '{call}', which is not a tool call of this item's stored streams");
                continue;
            }

            if (query.Length > 0 && !cited.Input.Contains(query, StringComparison.Ordinal))
            {
                problems.Add($"class '{entry.Class}': the search for '{data}' cites the call '{call}', whose input does not hold the query '{query}'");
            }
        }

        foreach (var (cell, measuredBy, result, reason) in entry.Neighbours)
        {
            if (result == "unmeasured")
            {
                if (reason.Length == 0)
                {
                    problems.Add($"class '{entry.Class}': the neighbour '{cell}' is unmeasured with no reason");
                }

                continue;
            }

            if (proof.Calls.Count == 0)
            {
                // A manual lane: the measurement is read as given, its command line.
                if (measuredBy.Length == 0)
                {
                    problems.Add($"class '{entry.Class}': the neighbour '{cell}' is measured by no call");
                }

                continue;
            }

            if (!proof.Calls.TryGetValue(measuredBy, out var cited) || cited.Tool != Agents.AgentMeasureSupport.ToolName)
            {
                problems.Add($"class '{entry.Class}': the neighbour '{cell}' is measured by the call '{measuredBy}', which is not a measure call of this item's stored streams");
            }
        }

        if (proof.TestDeclared is { } declared)
        {
            foreach (var test in entry.PinTests.Where(test => !declared(test)))
            {
                problems.Add($"class '{entry.Class}': the test '{test}' of the pin is not declared in the worktree");
            }
        }

        if (proof.ArmsDeclared)
        {
            if (entry.ArmId is not { } armId)
            {
                problems.Add($"class '{entry.Class}': arms nothing, but this repository declares an arms registry");
            }
            else
            {
                var named = proof.Arms.FirstOrDefault(candidate => string.Equals(candidate.Id, armId, StringComparison.OrdinalIgnoreCase));

                if (named.Id is null)
                {
                    problems.Add($"class '{entry.Class}': the arm '{armId}' is not an arm of this repository's registry");
                }
                else if (!string.Equals(named.Site, entry.HomePath, StringComparison.Ordinal))
                {
                    problems.Add($"class '{entry.Class}': the arm '{armId}' mutates '{named.Site}', not the home '{entry.HomePath}'");
                }
            }
        }

        if (entry.Claim.Length == 0)
        {
            problems.Add($"class '{entry.Class}': the claim is empty");
        }
        else
        {
            foreach (var axis in entry.Axes.Where(axis => !entry.Claim.Contains(axis, StringComparison.Ordinal)))
            {
                problems.Add($"class '{entry.Class}': the claim does not name the axis '{axis}'");
            }
        }

        return problems;
    }

    /// <summary>The audits one entry requires, and its home's touches and hotspots.</summary>
    private static void AuditsAndTouches(ClassClosureRecord entry, ClassClosureProof proof, List<string> audits, List<ClassClosureHotspot> hotspots, List<ClassClosureTouch> touches)
    {
        var byOthers = proof.TouchesByOthers.TryGetValue(entry.HomePath, out var others) ? others : 0;
        var all = proof.TouchesAll.TryGetValue(entry.HomePath, out var every) ? every : 0;
        touches.Add(new ClassClosureTouch(entry.Class, entry.HomePath, byOthers, all));

        if (all >= proof.HotspotAt)
        {
            hotspots.Add(new ClassClosureHotspot(entry.Class, entry.HomePath, all));
        }

        if (entry.HomeAudit is not null)
        {
            return;
        }

        if (proof.TouchesRequired > 0 && byOthers >= proof.TouchesRequired)
        {
            audits.Add($"class '{entry.Class}': home touched {byOthers.ToString(System.Globalization.CultureInfo.InvariantCulture)} times in this run by other work");
        }
        else if (proof.ReopenedTwice.Contains(entry.Class))
        {
            audits.Add($"class '{entry.Class}' reopened twice");
        }
    }
}
