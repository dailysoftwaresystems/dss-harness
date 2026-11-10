using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;

namespace DssHarness.Tests;

/// <summary>
/// The mechanism of the saving, in the suite, with no model (part 08, "What the saving is"): over three fixture
/// repositories kept with the tests - one C#, one C, one Dart - a list of tasks each names the declarations its
/// reference answer needs; for each task the step's context pack is made from the task's text and the <c>graph</c> tool
/// is driven the way the pack and the tool's description lead an agent - the cards, the bodies needed - and the bytes
/// served, pack included, are compared with the bytes of the whole files that hold those declarations. The test pins,
/// for each task and as a median, the ratio and how many of the needed declarations the pack held, so a change that
/// makes answers larger, packs less of what a task needs, or loses a declaration from them, goes red.
/// </summary>
public sealed class CodebaseGraphServingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>One task of a fixture repository: its text, and the declarations its reference answer needs.</summary>
    private sealed record FixtureTask(string Task, string[] Needs);

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
            var (queries, tool, tasks, files) = await RepositoryAsync(language);

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
                ("csharp", "Sum the areas of every shape in an order and report the largest of them", 95.0),
                ("csharp", "Describe each shape for the report table", 99.3),
                ("c", "Compute the total and the largest of the shapes' areas", 70.8),
                ("c", "Describe each circle for the report table", 99.0),
                ("dart", "Find the largest of the shapes by its area", 77.5),
                ("dart", "Build the report table of the shapes with each described", 78.3),
            ],
            ratios);

        // The median task's share: the middle of the six, ordered.
        var ordered = ratios.Select(one => one.Ratio).Order().ToArray();
        Assert.Equal(78.3, ordered[(ordered.Length - 1) / 2]);
    }

    /// <summary>
    /// One fixture repository, synced as the run's graph is: its files written into a git tree, the graph synced by the
    /// service over every implementation of this build, its tasks read, and the tool over the view the step is served.
    /// </summary>
    private static async Task<(CodebaseGraphQueries Queries, CodebaseGraphTool Tool, IReadOnlyList<FixtureTask> Tasks, IReadOnlyDictionary<string, string> Files)> RepositoryAsync(string language)
    {
        var root = Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test", "fixtures", "codebase-graph", "serving", language);
        var tree = await CodebaseGraphTree.CreateAsync();
        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);

        Dictionary<string, string> files = [];
        string tasks = string.Empty;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file, Token);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative == "tasks.json")
            {
                tasks = text;
                continue;
            }

            files[relative] = text;
            tree.Write(relative, text);
        }

        await tree.CommitAsync();
        await tree.SyncAsync();

        var queries = new CodebaseGraphQueries((await tree.CheckAsync(null, null)).Read);
        var read = JsonSerializer.Deserialize<List<FixtureTask>>(tasks, Json) ?? [];
        return (queries, CodebaseGraphToolTests.Tool(tree, queries, sync: false), read, files);
    }

    /// <summary>The UTF-8 bytes of <paramref name="text"/> as it reads on either platform, its line endings normalized.</summary>
    /// <param name="text">A text the graph serves or a file holds.</param>
    private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text.Replace("\r\n", "\n"));

    /// <summary>The tasks are read as they are written, their names never renamed.</summary>
    private static JsonSerializerOptions Json { get; } = new() { PropertyNameCaseInsensitive = true };
}
