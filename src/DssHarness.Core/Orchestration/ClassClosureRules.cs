using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Core.Orchestration;

/// <summary>The exits a <c>class-closure</c> check fails with: its record's problems (1), and a home audit it requires (2).</summary>
public static class ClassClosureExit
{
    /// <summary>The record has problems: the repair is <c>close-class</c>, which goes on with the implementation.</summary>
    public const int Findings = 1;

    /// <summary>
    /// The record holds no home audit where one is required - the home was touched by other work: the repair is
    /// <c>home-audit</c>, not a closing of the record.
    /// </summary>
    public const int HomeAudit = 2;
}

/// <summary>
/// One class a plan declares an item decides, as the plan's own check reads it: its id, its invariant, its one home, its
/// sites, its searches and its universe - the shape <c>plan.schema.json</c> holds as <c>closureClass</c>.
/// </summary>
/// <param name="Id">Its id, unique in the plan.</param>
/// <param name="Invariant">What it decides, in one sentence.</param>
/// <param name="HomePath">The file its one home is declared in, from the tree's root, read as the plan check reads it: either separator, made forward (<see cref="ClassClosureReading.PathOf"/>).</param>
/// <param name="HomeSymbol">The symbol its one home is declared as.</param>
/// <param name="Sites">Each path it is decided at today, read as <paramref name="HomePath"/> is: the file, the symbol, and what the plan says of it after.</param>
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
                ClassClosureReading.PathOf(Text(home["path"])),
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
                read.Add((ClassClosureReading.PathOf(path), Text(site["symbol"]) ?? string.Empty, Text(site["then"])));
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

    /// <summary>
    /// A class's path - a home's or a site's, of the plan or of the record - as the plan check reads it: either separator,
    /// made forward, read past at either end (<see cref="Repository.PathPatterns.Normalize"/>), so git's spelling of the
    /// file and the one the plan check took are one; empty where none is given.
    /// </summary>
    /// <param name="path">The path as it is written.</param>
    public static string PathOf(string? path) => Repository.PathPatterns.Normalize(path);
}

/// <summary>
/// One class's closing record, as the implement step's answer holds it: the shape <c>implement.schema.json</c> requires of
/// every entry of <c>closure</c>, read here as one model so the check that judges it and the fold that judges a manual
/// lane's <c>closure.json</c> read the same thing.
/// </summary>
/// <param name="Class">The class it closes, by the id the plan knows it by.</param>
/// <param name="Invariant">What the class decides, in one sentence.</param>
/// <param name="HomePath">The file its home is declared in, from the tree's root, read as a plan's is: either separator, made forward (<see cref="ClassClosureReading.PathOf"/>).</param>
/// <param name="HomeSymbol">The symbol its home is declared as.</param>
/// <param name="HomeMoved">Why the work moved the home off the one the plan named, where it says so; null where it says nothing.</param>
/// <param name="Sites">Each path it was decided at, read as <paramref name="HomePath"/> is: the file, the symbol, what decided there before, and what decides there after.</param>
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
    string? HomeMoved,
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
                ClassClosureReading.PathOf(Text(entry["home"]?["path"])),
                Text(entry["home"]?["symbol"]) ?? string.Empty,
                Text(entry["homeMoved"]?["why"]),
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
                read.Add((ClassClosureReading.PathOf(path), Text(site["symbol"]) ?? string.Empty, Text(site["before"]) ?? string.Empty, Text(site["after"]) ?? string.Empty));
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

/// <summary>
/// What one measure call of an item's stored streams measured, as the call's own record keeps it
/// (<see cref="Agents.AgentMeasureSupport.RecordOf"/>) - the record the tool runtime writes as it answers the call, read
/// from the directory of the step whose stream holds the call; never the stream's capped text of the answer, nor what the
/// closing record says of it.
/// </summary>
/// <param name="Record">Where the call's record is (<see cref="Agents.AgentMeasureSupport.RecordFileOf"/>), as a problem names it where it cannot be read.</param>
/// <param name="Measured">What the call measured and answered, as its record says it, and when the record was written; null where the record cannot be read.</param>
public sealed record ClassClosureMeasure(string Record, ClassClosureMeasured? Measured)
{
    /// <summary>
    /// What the measure call <paramref name="callId"/>, held by the stream of the step kept at
    /// <paramref name="stepDirectory"/>, measured, as its record says it, and when the record was written
    /// (<see cref="FileSystem.IFileSystem.LastWriteTimeUtc"/>): unread, and named by its path, where no record that reads
    /// as one is there - one that names no leg, variant, filter or cell, or no result a measure call answers.
    /// </summary>
    /// <param name="fileSystem">The file system the step's records are read on.</param>
    /// <param name="stepDirectory">The directory of the step whose stored stream holds the call.</param>
    /// <param name="callId">The call's id, as the stream holds it.</param>
    public static ClassClosureMeasure Of(FileSystem.IFileSystem fileSystem, string stepDirectory, string callId)
    {
        var record = Agents.AgentMeasureSupport.RecordFileOf(stepDirectory, callId);

        return new(
            record,
            Agents.AgentMeasureSupport.RecordOf(fileSystem, stepDirectory, callId) is { } said
                && ClassClosureReading.Text(said["leg"]) is { } leg
                && ClassClosureReading.Text(said["variant"]) is { } variant
                && ClassClosureReading.Text(said["filter"]) is { } filter
                && ClassClosureReading.Text(said["cell"]) is { } cell
                && ClassClosureReading.Text(said["result"]) is { } result
                    ? new ClassClosureMeasured(leg, variant, filter, cell, result, fileSystem.LastWriteTimeUtc(record))
                    : null);
    }
}

/// <summary>
/// What one measure call measured and answered, as its own record says it (<see cref="Agents.AgentMeasureSupport.Said"/>):
/// the measurement - the leg the call ran, the variant it built, the filter of its tests and the cell it named - its
/// result, and when its record was written. A measurement is the four together, never the cell alone: the cell is the
/// builder's own label, and what was measured is the tests the filter ran on that leg, built as that variant.
/// </summary>
/// <param name="Leg">The leg the call measured on, as the call gave it.</param>
/// <param name="Variant">The build configuration it measured as, as the call gave it.</param>
/// <param name="Filter">The tests it ran, as the leg's runner took the filter; empty for all of them.</param>
/// <param name="Cell">The cell of the universe it named, as the call gave it.</param>
/// <param name="Result">What it answered: passed, failed or unmeasured.</param>
/// <param name="Written">
/// When its record was written, which the tool runtime does as it answers the call: what orders one call of a measurement
/// after another.
/// </param>
public sealed record ClassClosureMeasured(string Leg, string Variant, string Filter, string Cell, string Result, DateTime Written)
{
    /// <summary>
    /// Whether <paramref name="other"/> made the measurement this one made: the same leg, variant, filter and cell, each as
    /// the calls gave them - the measure tool takes each as it is spelt.
    /// </summary>
    /// <param name="other">Another call's measurement.</param>
    public bool SameMeasurement(ClassClosureMeasured other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return string.Equals(Leg, other.Leg, StringComparison.Ordinal)
            && string.Equals(Variant, other.Variant, StringComparison.Ordinal)
            && string.Equals(Filter, other.Filter, StringComparison.Ordinal)
            && string.Equals(Cell, other.Cell, StringComparison.Ordinal);
    }
}

/// <summary>
/// What a closing record is judged against, beside itself: the paths the agent changed, the calls its stored streams hold
/// and what each of their measure calls measured, what its worktree still declares, the arms its worktree's registry
/// declares, and how often each home was touched in this run. A manual lane - a lane folded by hand, whose closing record
/// is its <c>closure.json</c> - has no stored streams and no run, and says so by what it leaves null: what it leaves null is
/// read as given, never taken for none.
/// </summary>
/// <param name="Changed">
/// The paths the agent changed: what its fold would write, remove or merge, and what the run's earlier folds of it changed.
/// Null for a manual lane, whose sites that read their home are read as given.
/// </param>
/// <param name="Calls">
/// Every tool call of the item's stored streams that was answered, by the id a record cites, each with whether its tool
/// ran - empty where they hold none, so a call cited is never read as made. Null for a manual lane, whose searches are read
/// as given, their command lines.
/// </param>
/// <param name="Measures">
/// Every measure call of the item's stored streams, by the id a record's <c>measuredBy</c> cites - answered passed, failed
/// or unmeasured alike, each with what its own record says it measured - its leg, variant, filter and cell - and when that
/// record was written - empty where they
/// hold none, so a measurement cited is never read as made. Null for a manual lane, whose measurements are read as given,
/// their command lines.
/// </param>
/// <param name="Declares">
/// Whether a file of the agent's worktree still declares a symbol - the graph's view of its tree, or text where the graph
/// lists the file only. Null where there is no worktree to read: a manual lane, judged by hand.
/// </param>
/// <param name="TestDeclared">Whether a test the pin names is declared in the worktree - the graph's tests edges, or text. Null as <paramref name="Declares"/> is.</param>
/// <param name="Arms">
/// Each arm the registry of the agent's worktree declares, with the file its own site mutates - the registry the fold
/// merges in, so an arm the work adds is among them; null where that tree's configuration declares no arms registry.
/// </param>
/// <param name="TouchesByOthers">How often each home path was touched in this run by work other than this item's own commits.</param>
/// <param name="TouchesAll">How often each home path was touched in this run, this item's own commits among them.</param>
/// <param name="TouchesRequired">How many touches by others require a home audit; 0 where none is required.</param>
/// <param name="HotspotAt">How many touches name a home a hotspot; never reached, nothing is listed.</param>
/// <param name="PathComparison">How this machine compares paths (<see cref="Platform.IHostPlatform.PathComparison"/>), as the plan check compares a class's.</param>
public sealed record ClassClosureProof(
    IReadOnlyList<string>? Changed,
    IReadOnlyDictionary<string, Agents.AgentStreamCall>? Calls,
    IReadOnlyDictionary<string, ClassClosureMeasure>? Measures,
    Func<string, string, bool>? Declares,
    Func<string, bool>? TestDeclared,
    IReadOnlyList<(string Id, string Site)>? Arms,
    IReadOnlyDictionary<string, int> TouchesByOthers,
    IReadOnlyDictionary<string, int> TouchesAll,
    int TouchesRequired,
    int HotspotAt,
    StringComparison PathComparison)
{
    /// <summary>Whether the record is a manual lane's, judged by hand: no stored stream holds its calls.</summary>
    public bool ByHand => Calls is null;

    /// <summary>
    /// Whether <paramref name="path"/> and <paramref name="other"/> name one file of the tree, as the plan check that held
    /// the class's paths reads them: either separator (<see cref="ClassClosureReading.PathOf"/>), compared as this machine
    /// compares paths. The one comparison of a class's paths - its home's and its sites', in the plan and in the record,
    /// the paths the agent changed, an arm's site and a home's touches - so a path the plan check took is never another
    /// file here for its spelling.
    /// </summary>
    /// <param name="path">A path from the tree's root.</param>
    /// <param name="other">Another.</param>
    public bool SamePath(string path, string other)
        => string.Equals(ClassClosureReading.PathOf(path), ClassClosureReading.PathOf(other), PathComparison);

    /// <summary>
    /// A proof that reads nothing but the record itself and the registry: a manual lane's, judged by hand, its sites that
    /// read their home, its searches and its measurements read as given.
    /// </summary>
    /// <param name="arms">Each arm the registry of the lane's worktree declares, with its own site; null where it declares none.</param>
    /// <param name="pathComparison">How this machine compares paths, an arm's site against a home.</param>
    public static ClassClosureProof Manual(IReadOnlyList<(string Id, string Site)>? arms, StringComparison pathComparison)
        => new(
            Changed: null,
            Calls: null,
            Measures: null,
            Declares: null,
            TestDeclared: null,
            arms,
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            TouchesRequired: 0,
            HotspotAt: int.MaxValue,
            pathComparison);
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
/// class has an entry, and a manual lane's record names one at least; a planned class's home is the plan's, or the record
/// says why it moved; every planned site is accounted for, a deleted site no longer declaring its symbol in the worktree
/// and a site that reads its home among the paths the agent changed; every search was made - it cites the call that made
/// it, a tool call of the item's stored streams whose tool ran, however it ended, one of whose input's texts holds its
/// query - and every neighbour was measured - its measurement a <c>measure</c> call of the same streams whose own record
/// says it measured the neighbour's cell and answered its result (failed for failed-fixed), and the newest call of that
/// measurement - the same leg, variant, filter and cell - to measure anything answered passed: the call cited or a later
/// one for passed, a call whose record was written after the failing call's for failed-fixed - or reasoned where it says
/// it was not;
/// every test of the pin is declared in the worktree; where the worktree's configuration declares an arms registry, the
/// arm is one of its arms and mutates the home's own file, or the record says why the class has none; the universe holds
/// every axis the plan names, and the claim names every axis of it; and where the home was touched by other work, the
/// record carries a home audit. A class's paths - in the plan and in the record, the paths changed, an arm's site and the
/// touches - are read as the plan check reads them, either separator, and compared as this machine compares paths
/// (<see cref="ClassClosureProof.SamePath"/>).
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

        // A manual lane's record is what closes its row, with no plan beside it to say which classes it decides: one that
        // names no class closes none, and an empty file would close the row with nothing judged.
        if (proof.ByHand && record.Count == 0)
        {
            problems.Add("the record names no class: a row closes by the record of each class it closes, and an empty one closes none");
        }

        return new ClassClosureJudged(problems, audits, hotspots, touches);
    }

    /// <summary>The problems of one entry of the record, judged against its class's plan and the proof.</summary>
    private static IReadOnlyList<string> EntryProblems(ClassClosureRecord entry, PlannedClass? planned, ClassClosureProof proof)
    {
        List<string> problems = [];

        // The plan named the class's one home: a record that names another moved it, and says why or is refused. Its path is
        // another only as the plan check reads paths (SamePath): the plan's spelling of the file, which that check took, and
        // git's in the record are one file.
        if (planned is not null
            && (!proof.SamePath(entry.HomePath, planned.HomePath) || !string.Equals(entry.HomeSymbol, planned.HomeSymbol, StringComparison.Ordinal))
            && string.IsNullOrWhiteSpace(entry.HomeMoved))
        {
            problems.Add($"class '{entry.Class}': the home is '{entry.HomeSymbol}' in '{entry.HomePath}', not the planned '{planned.HomeSymbol}' in '{planned.HomePath}', and the record says not why it moved (homeMoved)");
        }

        foreach (var (path, _, _) in planned?.Sites ?? [])
        {
            if (!entry.Sites.Any(site => proof.SamePath(site.Path, path)))
            {
                problems.Add($"class '{entry.Class}': the site '{path}' has no after in the record");
            }
        }

        foreach (var site in entry.Sites)
        {
            // A site that is a whole file names no symbol of its own: what it declared there was the class's, by its home's name.
            var symbol = string.IsNullOrWhiteSpace(site.Symbol) ? entry.HomeSymbol : site.Symbol;

            if (site.After == "deleted" && proof.Declares is { } declares && declares(site.Path, symbol))
            {
                problems.Add($"class '{entry.Class}': the site '{site.Path}' is deleted, but it still declares '{symbol}'");
            }

            if (site.After == "reads-home" && proof.Changed is { } changed && !changed.Any(path => proof.SamePath(path, site.Path)))
            {
                problems.Add($"class '{entry.Class}': the site '{site.Path}' reads its home, but the agent changed nothing at it");
            }
        }

        foreach (var (data, query, call) in entry.Searches)
        {
            if (call.Length == 0)
            {
                problems.Add($"class '{entry.Class}': the search for '{data}' cites no call: a search that was not made does not count");
                continue;
            }

            if (proof.Calls is not { } calls)
            {
                // A manual lane, judged by hand: the call is read as given, its command line, for no stream holds it.
                continue;
            }

            if (!calls.TryGetValue(call, out var cited))
            {
                problems.Add($"class '{entry.Class}': the search for '{data}' cites the call '{call}', which is not a tool call of this item's stored streams");
                continue;
            }

            // A search is made by a call whose tool ran, however it ended: a search that found nothing exits 1 as grep, rg
            // and git grep do, and is the evidence a class of one site rests on. A call refused, or whose input did not
            // read, searched nothing.
            if (!cited.Ran)
            {
                problems.Add($"class '{entry.Class}': the search for '{data}' cites the call '{call}', which never ran: a call refused, or whose input did not read, searched nothing");
                continue;
            }

            // The query as the model wrote it, looked for in what the call was given as the model wrote it: never in the
            // input's JSON text, where a client escapes what the query holds.
            if (query.Length > 0 && !cited.Texts().Any(text => text.Contains(query, StringComparison.Ordinal)))
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

            if (measuredBy.Length == 0)
            {
                problems.Add($"class '{entry.Class}': the neighbour '{cell}' is measured by no call");
                continue;
            }

            if (proof.Measures is not { } measures)
            {
                // A manual lane: the measurement is read as given, its command line.
                continue;
            }

            // The streams' measure calls alone, by the runtime's name whichever client called them (AgentStreamCalls.ToolOf),
            // whatever they answered.
            if (!measures.TryGetValue(measuredBy, out var cited))
            {
                problems.Add($"class '{entry.Class}': the neighbour '{cell}' is measured by the call '{measuredBy}', which is not a measure call of this item's stored streams");
                continue;
            }

            // What the call measured is its own record's, never the builder's word for it: a call that measured another cell
            // says nothing of this one, and one that answered otherwise than the record says did not show what it claims.
            if (cited.Measured is not { } measured)
            {
                problems.Add($"class '{entry.Class}': the neighbour '{cell}' is measured by the call '{measuredBy}', whose record '{cited.Record}' cannot be read");
                continue;
            }

            if (!string.Equals(measured.Cell, cell, StringComparison.Ordinal))
            {
                problems.Add($"class '{entry.Class}': the neighbour '{cell}' is measured by the call '{measuredBy}', which measured the cell '{measured.Cell}'");
                continue;
            }

            if (!string.Equals(measured.Result, AnswerOf(result), StringComparison.Ordinal))
            {
                problems.Add($"class '{entry.Class}': the neighbour '{cell}' is '{result}' in the record, but the call '{measuredBy}' that measured it answered '{measured.Result}'");
                continue;
            }

            // What holds of a measurement is its newest word: a pass a later call of the same measurement failed no longer
            // shows the cell passing, and the failing call shows the failure, never the fix - a fix is shown by the same
            // tests measured again after it, the newest of them passing. The builder's word that it fixed the cell is not
            // one, nor a pass of other tests, on another leg or built otherwise, that it named by the cell.
            var newest = NewestAfter(measures, measured);

            if (result == "passed" && newest is { Measured.Result: "failed" } broken)
            {
                problems.Add($"class '{entry.Class}': the neighbour '{cell}' is 'passed' in the record, but the call '{broken.Call}', the newest to measure it after the call '{measuredBy}', answered 'failed'");
            }
            else if (result == "failed-fixed" && newest is null)
            {
                problems.Add($"class '{entry.Class}': the neighbour '{cell}' is 'failed-fixed', and no measure call of this item's stored streams measured it passing - on the leg '{measured.Leg}', as '{measured.Variant}', with the filter '{measured.Filter}' - after the call '{measuredBy}' answered 'failed'");
            }
            else if (result == "failed-fixed" && newest is { Measured.Result: "failed" } still)
            {
                problems.Add($"class '{entry.Class}': the neighbour '{cell}' is 'failed-fixed', but the call '{still.Call}', the newest to measure it after the call '{measuredBy}', answered 'failed'");
            }
        }

        if (proof.TestDeclared is { } declared)
        {
            foreach (var test in entry.PinTests.Where(test => !declared(test)))
            {
                problems.Add($"class '{entry.Class}': the test '{test}' of the pin is not declared in the worktree");
            }
        }

        // Where the worktree keeps arms, a class is armed by one of them, or says why it has nothing to mutate - the arm's
        // other form, as the record's schema holds it; one that names neither is refused.
        if (proof.Arms is { } arms)
        {
            if (entry.ArmId is { } armId)
            {
                var named = arms.FirstOrDefault(candidate => string.Equals(candidate.Id, armId, StringComparison.OrdinalIgnoreCase));

                if (named.Id is null)
                {
                    problems.Add($"class '{entry.Class}': the arm '{armId}' is not an arm of the registry its worktree holds");
                }
                else if (!proof.SamePath(named.Site, entry.HomePath))
                {
                    problems.Add($"class '{entry.Class}': the arm '{armId}' mutates '{named.Site}', not the home '{entry.HomePath}'");
                }
            }
            else if (string.IsNullOrWhiteSpace(entry.ArmWhy))
            {
                problems.Add($"class '{entry.Class}': arms nothing and says not why, but this repository declares an arms registry");
            }
        }

        // The universe the plan names is the least the record covers: an axis it leaves out is one the claim never ranges over.
        List<string> axes = [.. entry.Axes];

        foreach (var axis in (planned?.Axes ?? []).Where(axis => !entry.Axes.Contains(axis, StringComparer.Ordinal)))
        {
            problems.Add($"class '{entry.Class}': the universe leaves out the planned axis '{axis}'");
            axes.Add(axis);
        }

        if (entry.Claim.Length == 0)
        {
            problems.Add($"class '{entry.Class}': the claim is empty");
        }
        else
        {
            foreach (var axis in axes.Where(axis => !entry.Claim.Contains(axis, StringComparison.Ordinal)))
            {
                problems.Add($"class '{entry.Class}': the claim does not name the axis '{axis}'");
            }
        }

        return problems;
    }

    /// <summary>
    /// What the measure call a neighbour cites answered, where the neighbour's result is true: <c>failed</c> for
    /// <c>failed-fixed</c> - the measurement showed the failure, and the fix is the item's, after it, shown by the newest
    /// later call of the same measurement answering passed (<see cref="NewestAfter"/>) - and every other result as it is
    /// spelt, <c>passed</c> a call that answered passed.
    /// </summary>
    /// <param name="result">The neighbour's result, as the record spells it.</param>
    private static string AnswerOf(string result) => result == "failed-fixed" ? "failed" : result;

    /// <summary>
    /// The newest measure call of <paramref name="measures"/> that made the measurement <paramref name="measured"/> made
    /// (<see cref="ClassClosureMeasured.SameMeasurement"/>) after it - its record written later - and measured something:
    /// answered passed or failed, for a call answered unmeasured measured nothing of the cell. Of calls whose records were
    /// written at the same moment a failure is the newest, so a pass beside it never speaks for it. Null where no later
    /// call measured it.
    /// </summary>
    /// <param name="measures">Every measure call of the item's stored streams, by its id.</param>
    /// <param name="measured">What the call a neighbour cites measured.</param>
    private static (string Call, ClassClosureMeasured Measured)? NewestAfter(IReadOnlyDictionary<string, ClassClosureMeasure> measures, ClassClosureMeasured measured)
        => measures
            .Where(other => other.Value.Measured is { Result: "passed" or "failed" } again && again.Written > measured.Written && again.SameMeasurement(measured))
            .Select(other => ((string Call, ClassClosureMeasured Measured)?)(other.Key, other.Value.Measured!))
            .OrderByDescending(other => other!.Value.Measured.Written)
            .ThenBy(other => other!.Value.Measured.Result == "passed")
            .ThenBy(other => other!.Value.Call, StringComparer.Ordinal)
            .FirstOrDefault();

    /// <summary>
    /// The audits one entry requires, and its home's touches and hotspots: the touches of every path that is its home's as
    /// the class's paths are compared (<see cref="ClassClosureProof.SamePath"/>), git's spelling of the file among them.
    /// </summary>
    private static void AuditsAndTouches(ClassClosureRecord entry, ClassClosureProof proof, List<string> audits, List<ClassClosureHotspot> hotspots, List<ClassClosureTouch> touches)
    {
        var byOthers = proof.TouchesByOthers.Where(touched => proof.SamePath(touched.Key, entry.HomePath)).Sum(touched => touched.Value);
        var all = proof.TouchesAll.Where(touched => proof.SamePath(touched.Key, entry.HomePath)).Sum(touched => touched.Value);
        touches.Add(new ClassClosureTouch(entry.Class, entry.HomePath, byOthers, all));

        if (all >= proof.HotspotAt)
        {
            hotspots.Add(new ClassClosureHotspot(entry.Class, entry.HomePath, all));
        }

        if (entry.HomeAudit is null && proof.TouchesRequired > 0 && byOthers >= proof.TouchesRequired)
        {
            audits.Add($"class '{entry.Class}': home touched {byOthers.ToString(System.Globalization.CultureInfo.InvariantCulture)} times in this run by other work");
        }
    }
}
