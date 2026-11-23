using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;

namespace DssHarness.Tests;

/// <summary>
/// The mechanism of the saving, in the suite, with no model (part 08, "What the saving is"; part 02, section 7): over nine
/// fixture repositories kept with the tests - one C#, one C, one Dart, and one each of HTML, CSS and TypeScript, of Vue, of
/// Angular, of Razor and Blazor, of JSON, YAML and XML read by C#, of Python - a list of tasks each names the declarations its reference
/// answer needs, and a list of site tasks each the data a reference answer decides by and every place that decides it; for each
/// task the step's context pack is made from the task's text and the <c>graph</c> tool is driven the way the pack and the tool's
/// description lead an agent - the cards, the bodies needed - and for each site task the tool's <c>sites</c> is read page by
/// page to its last; the bytes served, pack included, are compared with the bytes of the whole files that hold those
/// declarations and places, and every place is checked to be in the answer as the task says it decides - the card of the node
/// at its line saying so, or the line of a literal or a text hit. The test pins, for each task and site task, the ratio and
/// that the recall is whole, so a change that makes answers larger, packs less of what a task needs, loses a declaration from
/// them, or drops a place from a sites answer - a use, a literal's place, a key's join, a selector's reach, an override - goes
/// red.
/// </summary>
public sealed class CodebaseGraphServingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>One task of a fixture repository: its text, and the declarations its reference answer needs.</summary>
    private sealed record FixtureTask(string Task, string[] Needs);

    /// <summary>
    /// One site task of a fixture repository: the data a reference answer decides by, and every place that decides it, each the
    /// file and the line the decision stands on - a declaration, a call, a read, a literal, a key, a line of markup - with how it
    /// decides.
    /// </summary>
    private sealed record FixtureSite(string Data, FixturePlace[] Sites);

    /// <summary>
    /// One place a site task names: <c>path:line</c>, and how the answer is to say it decides there (<see cref="GraphSiteHows"/>) -
    /// a way of a node, said by the card of the node at the line; a literal, said by its line; a line of text, said as a hit.
    /// </summary>
    private sealed record FixturePlace(string Place, string How);

    /// <summary>
    /// For every task of the three fixture repositories: the pack made from the task's text holds every declaration the
    /// task needs, the bodies of those declarations cost under half the bytes of the whole files that hold them, the pack
    /// stays within its cap, and the bytes the pack and the bodies serve together are a pinned share of the whole files'.
    /// </summary>
    [Fact]
    public async Task EveryTaskOfTheThreeFixtureRepositories_IsServedItsDeclarationsForAPinnedShareOfTheWholeFiles()
    {
        var ratios = new List<(string Language, string Task, double Ratio)>();

        foreach (var language in new[] { "csharp", "c", "dart" })
        {
            var (queries, tool, tasks, files, _) = await RepositoryAsync(language);

            foreach (var task in tasks)
            {
                var pack = CodebaseContextPack.Make(queries, new ContextPackRequest(task.Task, _ => true) { Tokens = CodebaseContextPack.DefaultTokens });
                var needed = task.Needs.Select(name => queries.Resolve(name) is GraphResolve.Found found ? found.Card : null).OfType<GraphCard>().ToList();

                Assert.True(needed.Count == task.Needs.Length, $"{language}: a task names a declaration the view does not hold: {string.Join(", ", task.Needs)}");

                // The pack holds every declaration the task needs.
                var missed = needed.Where(card => !pack.Nodes.Contains(card.Id, StringComparer.Ordinal)).Select(card => card.Id).ToList();

                Assert.True(missed.Count == 0, $"{language} '{task.Task}': the pack held {needed.Count - missed.Count} of {needed.Count} needed declarations ({string.Join(", ", missed)})");

                // The bytes served: the pack, then the bodies of the needed declarations, as the tool's description leads
                // an agent to read them. Bytes are counted as the text reads on either platform, its line endings
                // normalized, so a checkout's own way of writing them moves no pin.
                var served = Bytes(pack.Text);

                foreach (var card in needed)
                {
                    var answer = await tool.RunAsync(
                        new JsonObject { ["op"] = "body", ["symbols"] = new JsonArray([card.Handle]) },
                        CodebaseGraphTool.Operations(write: false, narrow: false),
                        Token);
                    served += Bytes(answer.Text);
                }

                // The bytes of the whole files that hold what the task needs.
                var whole = needed.Select(card => card.Path).Distinct(StringComparer.Ordinal).Sum(path => Bytes(files[path]));

                // What the graph saves is the files: the bodies a task needs cost under half the bytes of the files that
                // hold them. The pack is what orientation costs, held to its cap whatever the fixture's size - so a line
                // more in its header moves the pinned share below, never these bounds.
                var bodies = served - Bytes(pack.Text);

                Assert.True(bodies * 2 < whole, $"{language} '{task.Task}': the bodies served {bodies} of {whole} whole bytes");
                Assert.True(EstimatedTokens.Of(pack.Text) <= CodebaseContextPack.DefaultTokens, $"{language} '{task.Task}': the pack is {EstimatedTokens.Of(pack.Text)} estimated tokens, past its cap of {CodebaseContextPack.DefaultTokens}");
                ratios.Add((language, task.Task, Math.Round(served * 100.0 / whole, 1)));
            }
        }

        Assert.Equal(
            [
                ("csharp", "Sum the areas of every shape in an order and report the largest of them", 161.6),
                ("csharp", "Describe each shape for the report table", 168.7),
                ("c", "Compute the total and the largest of the shapes' areas", 155.0),
                ("c", "Describe each circle for the report table", 145.4),
                ("dart", "Find the largest of the shapes by its area", 208.2),
                ("dart", "Build the report table of the shapes with each described", 280.2),
            ],
            ratios);

        // The median task's share: the middle of the six, ordered.
        var ordered = ratios.Select(one => one.Ratio).Order().ToArray();
        Assert.Equal(161.6, ordered[(ordered.Length - 1) / 2]);
    }

    /// <summary>
    /// For every task of the markup and data fixture repositories - one of HTML, CSS and TypeScript, one of Vue components, one
    /// of Angular, one of Razor and Blazor, one of JSON, YAML and XML read by C#, one of Python: the pack made from the task's text
    /// holds every declaration the task needs, the bodies of those declarations cost under half the bytes of the whole files that
    /// hold them, the pack stays within its cap, and the bytes the pack and the bodies serve together are a pinned share of the
    /// whole files'.
    /// </summary>
    [Fact]
    public async Task EveryTaskOfTheMarkupAndDataFixtureRepositories_IsServedItsDeclarationsForAPinnedShareOfTheWholeFiles()
    {
        var ratios = new List<(string Language, string Task, double Ratio)>();

        foreach (var language in MarkupAndData)
        {
            var (queries, tool, tasks, files, _) = await RepositoryAsync(language);

            foreach (var task in tasks)
            {
                var pack = CodebaseContextPack.Make(queries, new ContextPackRequest(task.Task, _ => true) { Tokens = CodebaseContextPack.DefaultTokens });
                var needed = task.Needs.Select(name => queries.Resolve(name) is GraphResolve.Found found ? found.Card : null).OfType<GraphCard>().ToList();

                Assert.True(needed.Count == task.Needs.Length, $"{language}: a task names a declaration the view does not hold: {string.Join(", ", task.Needs)}");

                var missed = needed.Where(card => !pack.Nodes.Contains(card.Id, StringComparer.Ordinal)).Select(card => card.Id).ToList();

                Assert.True(missed.Count == 0, $"{language} '{task.Task}': the pack held {needed.Count - missed.Count} of {needed.Count} needed declarations ({string.Join(", ", missed)})");

                var served = Bytes(pack.Text);

                foreach (var card in needed)
                {
                    served += Bytes((await tool.RunAsync(
                        new JsonObject { ["op"] = "body", ["symbols"] = new JsonArray([card.Handle]) },
                        CodebaseGraphTool.Operations(write: false, narrow: false),
                        Token)).Text);
                }

                var whole = needed.Select(card => card.Path).Distinct(StringComparer.Ordinal).Sum(path => Bytes(files[path]));

                Assert.True((served - Bytes(pack.Text)) * 2 < whole, $"{language} '{task.Task}': the bodies served {served - Bytes(pack.Text)} of {whole} whole bytes");
                Assert.True(EstimatedTokens.Of(pack.Text) <= CodebaseContextPack.DefaultTokens, $"{language} '{task.Task}': the pack is {EstimatedTokens.Of(pack.Text)} estimated tokens, past its cap of {CodebaseContextPack.DefaultTokens}");
                ratios.Add((language, task.Task, Math.Round(served * 100.0 / whole, 1)));
            }
        }

        Assert.Equal(
            [
                ("web", "Print a cart's receipt with its total in money", 123.9),
                ("web", "Add lines to a cart and count them", 263.2),
                ("vue", "Show a cart's lines with a button that adds one and its total", 162.4),
                ("vue", "Change what a line of the cart shows", 652.4),
                ("angular", "Show a cart's lines with a button that adds one and its total", 237.6),
                ("angular", "Change what a line of the cart shows", 572.0),
                ("razor-blazor", "Add a Line from a Blazor cart's button", 188.8),
                ("razor-blazor", "Render a cart's receipt total", 242.1),
                ("config", "Read the payment timeout and the retries from the configuration", 129.1),
                ("config", "Find the most items an order may hold", 132.9),
                ("config", "Tell whether shipping is free for a cart", 165.5),
                ("python", "Find the largest of the shapes by its area", 183.9),
                ("python", "Build the report table of the shapes with each described", 271.5),
            ],
            ratios);
    }

    /// <summary>
    /// For every site task of every fixture repository: the <c>sites</c> answer, read page by page to its last, holds every place
    /// the task says decides its datum, as the task says it decides - recall of 100%, a declaration, a call, a read, an
    /// instantiation, an override, a literal, a key and a line of markup alike, across every language the fixture holds, each
    /// place held by the card of the node at its line saying that way, or by its literal's or its hit's line, never by a card
    /// that only spans it - and the bytes it serves are a pinned share of the whole files those places stand in. A change that
    /// drops a use, a literal's place, a key's join, a selector's reach or an override from the answer turns it red. The share is
    /// of the whole answer, the lines that end it among them: every limit of each language whose files the answer read, those a
    /// file shows the sign of marked with how many.
    /// </summary>
    [Fact]
    public async Task EverySiteTask_IsAnsweredWithEveryPlaceThatDecides_AtFullRecall_ForAPinnedShareOfTheWholeFiles()
    {
        var ratios = new List<(string Language, string Data, double Ratio)>();

        foreach (var language in new[] { "csharp", "c", "dart" }.Concat(MarkupAndData))
        {
            var (queries, tool, _, files, sites) = await RepositoryAsync(language);
            var whole = sites.SelectMany(site => site.Sites).Select(site => site.Place[..site.Place.LastIndexOf(':')]).Distinct(StringComparer.Ordinal).Sum(path => Bytes(files[path]));

            foreach (var site in sites)
            {
                var answer = new StringBuilder();
                var pages = 1;

                for (var page = 1; page <= pages; page++)
                {
                    var given = await tool.RunAsync(new JsonObject { ["op"] = "sites", ["data"] = site.Data, ["page"] = page }, CodebaseGraphTool.Operations(write: false, narrow: false), Token);

                    Assert.Equal(GraphToolOutcome.Answered, given.Outcome);
                    answer.Append(given.Text).Append('\n');
                    pages = given.Paging!.Pages;
                }

                var lines = answer.ToString().Split('\n');
                var missed = site.Sites.Where(place => !Covers(lines, queries, place)).Select(place => $"{place.Place} {place.How}").ToList();

                Assert.True(missed.Count == 0, $"{language} '{site.Data}': the sites answer held {site.Sites.Length - missed.Count} of {site.Sites.Length} places ({string.Join(", ", missed)}):\n{answer}");
                ratios.Add((language, site.Data, Math.Round(Bytes(answer.ToString()) * 100.0 / whole, 1)));
            }
        }

        Assert.Equal(
            [
                ("csharp", "Largest", 19.2),
                ("csharp", "Describe", 23.1),
                ("csharp", "Items", 11.8),
                ("c", "largest_area", 6.7),
                ("c", "area_circle", 22.6),
                ("dart", "largest", 25.0),
                ("dart", "describe", 12.7),
                ("web", "money", 22.5),
                ("web", "total", 21.4),
                ("web", ".btn-primary", 6.6),
                ("web", "\"Your receipt\"", 7.7),
                ("vue", "total", 21.4),
                ("vue", "add", 22.2),
                ("vue", ".btn-primary", 18.2),
                ("vue", "lineText", 17.2),
                ("angular", "total", 19.4),
                ("angular", "app-line", 19.4),
                ("angular", ".btn-primary", 17.1),
                ("angular", "lineText", 20.0),
                ("razor-blazor", "Total", 32.0),
                ("razor-blazor", "AddTea", 24.8),
                ("razor-blazor", "Add", 27.1),
                ("razor-blazor", ".btn-primary", 22.7),
                ("config", "TimeoutSeconds", 35.8),
                ("config", "Retries", 34.1),
                ("config", "MaxItems", 25.1),
                ("config", "freeOverCents", 25.6),
                ("config", "rules", 15.6),
                ("python", "largest", 61.9),
                ("python", "describe", 35.5),
                ("python", "Shape.name", 0.0),
            ],
            ratios);
    }

    /// <summary>The fixture repositories of part 02's markup, frameworks and configuration files, and Python's of its own lane, beside the three plan 1 measured.</summary>
    private static readonly string[] MarkupAndData = ["web", "vue", "angular", "razor-blazor", "config", "python"];

    /// <summary>
    /// Whether the answer's <paramref name="lines"/> hold <paramref name="site"/> as it decides: a literal by the line the
    /// answer writes it on (<c>path:line holds the literal '...'</c>, beneath its node's card or alone); a line of text by its
    /// hit's line (<c>path:line text</c>); and a way of a node by the card of the node at the line - the innermost, as the
    /// view reads it (<see cref="CodebaseGraphQueries.AtLine"/>), told by its handle - whose ways, after its last
    /// <c> | </c>, hold that way, how sure it is said or not. A card that only spans the line - its type's, its file's - is
    /// never the place.
    /// </summary>
    private static bool Covers(IReadOnlyList<string> lines, CodebaseGraphQueries queries, FixturePlace site)
    {
        var colon = site.Place.LastIndexOf(':');
        var path = site.Place[..colon];
        var line = int.Parse(site.Place[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var at = $"{path}:{line.ToString(System.Globalization.CultureInfo.InvariantCulture)} ";
        var literal = $"{at}holds the literal '";

        if (site.How == GraphSiteHows.HoldsTheLiteral)
        {
            return lines.Any(text => text.TrimStart().StartsWith(literal, StringComparison.Ordinal));
        }

        if (site.How == GraphSiteHows.Text)
        {
            return lines.Any(text => text.TrimStart() is var trimmed && trimmed.StartsWith(at, StringComparison.Ordinal) && !trimmed.StartsWith(literal, StringComparison.Ordinal));
        }

        Assert.Contains(site.How, GraphSiteHows.All);

        if (queries.AtLine(path, line) is not { } card)
        {
            return false;
        }

        return lines.Any(text => text.StartsWith($"  {card.Handle} ", StringComparison.Ordinal)
            && text[(text.LastIndexOf(" | ", StringComparison.Ordinal) + 3)..].Split(", ").Any(how => how == site.How || how.StartsWith(site.How + " (", StringComparison.Ordinal)));
    }

    /// <summary>
    /// One fixture repository, synced as the run's graph is: its files written into a git tree, the graph synced by the
    /// service over every implementation of this build, its tasks and its site tasks read, and the tool over the view the
    /// step is served.
    /// </summary>
    private static async Task<(CodebaseGraphQueries Queries, CodebaseGraphTool Tool, IReadOnlyList<FixtureTask> Tasks, IReadOnlyDictionary<string, string> Files, IReadOnlyList<FixtureSite> Sites)> RepositoryAsync(string language)
    {
        var root = Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test", "fixtures", "codebase-graph", "serving", language);
        var tree = await CodebaseGraphTree.CreateAsync();
        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);

        Dictionary<string, string> files = [];
        string tasks = string.Empty;
        string sites = string.Empty;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file, Token);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative == "tasks.json")
            {
                tasks = text;
                continue;
            }

            if (relative == "sites.json")
            {
                sites = text;
                continue;
            }

            files[relative] = text;
            tree.Write(relative, text);
        }

        await tree.CommitAsync();
        await tree.SyncAsync();

        var queries = new CodebaseGraphQueries((await tree.CheckAsync(null, null)).Read);
        var read = JsonSerializer.Deserialize<List<FixtureTask>>(tasks, Json) ?? [];
        IReadOnlyList<FixtureSite> placed = sites.Length == 0 ? [] : JsonSerializer.Deserialize<List<FixtureSite>>(sites, Json) ?? [];
        return (queries, CodebaseGraphToolTests.Tool(tree, queries, sync: false), read, files, placed);
    }

    /// <summary>The UTF-8 bytes of <paramref name="text"/> as it reads on either platform, its line endings normalized.</summary>
    /// <param name="text">A text the graph serves or a file holds.</param>
    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text.Replace("\r\n", "\n"));

    /// <summary>The tasks are read as they are written, their names never renamed.</summary>
    private static JsonSerializerOptions Json { get; } = new() { PropertyNameCaseInsensitive = true };
}
