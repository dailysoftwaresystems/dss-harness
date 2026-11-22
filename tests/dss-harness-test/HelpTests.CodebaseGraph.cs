using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

public sealed partial class HelpTests
{
    /// <summary>The codebase-graph topic says the context pack's seeds, walk, weights and pages, and find's ties, as the code holds them.</summary>
    [Fact]
    public void TheCodebaseGraphTopic_SaysTheContextPacksSeedsWalkAndPages_FromTheCode()
    {
        var prose = Prose.Words(HelpText.Of("codebase-graph").StandardOutput);

        Assert.Contains(Prose.Words("and every node its words match, each at its score's share of the best match's weight."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("every site and home of the classes of the work item it is an instance of, where its prompt reads them ({input.item.classes}), each site's declaration at the weight of a node named and its file's declarations at a named file's where it names no symbol its file carries, so a fresh agent holds every site's declaration"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("From all of them at once the walk goes best first over calls both ways, the type relations, tests and the types a node names, with no count of hops"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"({string.Join(", ", DssHarness.Core.Codebase.CodeConfidences.All.Select(confidence => $"{confidence} {DssHarness.Core.Codebase.CodebaseContextPack.Confidence(confidence).ToString(System.Globalization.CultureInfo.InvariantCulture)}"))})"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("so a hub is gone through and its many neighbours are ranked down by how many they are, never cut."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"or how to ask for the next page - graph's {DssHarness.Core.Agents.Runtime.CodebaseGraphTool.NameOf(DssHarness.Core.Agents.Runtime.GraphOp.Pack)} with page - each later page laid by going on with the same walk"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("graphPromptTokens 0 gives no pack, and so does a size that cannot hold a page's first and last lines."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("A step that starts from its changes is packed first from the declarations its tree changed against its base - an agent's where its worktree was made or last moved to, the main checkout's the run's start - each with where its lines differ"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("then their impact walked the same way from them - their tests, carried whole, first, then their callers, overrides, implementations and dispatches, and derived types, as far as it goes - then from the task; where nothing changed, its first line says so and the task's seeds stand in."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"It is given in pages of the step's graphPromptTokens ({DssHarness.Core.Codebase.CodebaseContextPack.DefaultTokens} estimated tokens - bytes over {DssHarness.Core.Codebase.EstimatedTokens.BytesPerToken} - by default), best first"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"is listed under '{DssHarness.Core.Codebase.CodebaseContextPack.NotCompared}' with why, and nothing in it is said unchanged."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"told in pages of the step's graphPromptTokens, page 1 in the message and each later page by graph's {DssHarness.Core.Agents.Runtime.CodebaseGraphTool.NameOf(DssHarness.Core.Agents.Runtime.GraphOp.Changes)} with page"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a tie broken by how near the step's own work each is - its context pack or a working set it was given, a file it writes, a file its agent changed, a path or a name its prompt holds - then by id"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"each node it {string.Join(", ", DssHarness.Core.Codebase.WorkingSetUses.All.SkipLast(1))} or {DssHarness.Core.Codebase.WorkingSetUses.All[^1]}, with its handle, file, lines and hash at the step's end, every one however many,"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("A reason step's pack is to be given the working sets of the step it goes on with (resume), of each step it depends on directly, a group by each reason step in it, and of its own earlier visits of the same work item, newest first"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("edited first, then by how often each was touched; a node the view no longer holds kept by its id, marked gone"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("by where its new text stands once there - every place, with replace_all - else by the lines it names"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a deletion writes no lines, and is counted with the writes placed nowhere, as a whole file's write is where the file does not read as the step left it."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("body and edit reach a file only inside the tree, each link resolved, never through a link out of it."), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"{DssHarness.Core.Agents.Runtime.CodebaseGraphTool.NameOf(DssHarness.Core.Agents.Runtime.GraphOp.Sites)} - every place that decides data, a name, a key path, a diagnostic id or a quoted literal asked by its value alone"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("with the coverage lines that end every such answer: the files searched by text alone, that nothing is assumed of a language with no extractor, and - where files of a language the graph is unsure of were searched - what that language's implementation does not see where they show it, each limit a line by the sign the file gives of it"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("--body <symbol>... or --sites <data>, any of them with --page <n>"), prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The codebase-graph topic, under each of its names, says every implementation this build holds - its version, what it
    /// claims, what it reads and what it does not see - every key of codebaseGraph, the graph's files, the step's output,
    /// its failOn and its time limit, each read from the code; and the orchestrators topic lists the graph's files.
    /// </summary>
    [Fact]
    public void TheCodebaseGraphTopic_SaysEveryImplementationKeyAndFile_FromTheCode()
    {
        var topic = HelpText.Of("codebase-graph").StandardOutput;
        var prose = Prose.Words(topic);

        Assert.Equal(topic, HelpText.Of("graph").StandardOutput);
        Assert.Equal(topic, HelpText.Of("graphify-codebase").StandardOutput);

        foreach (var extractor in DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild.All)
        {
            Assert.Contains($"{extractor.Language} (version {extractor.Version.ToString(CultureInfo.InvariantCulture)};", topic, StringComparison.Ordinal);
            Assert.Contains(Prose.Words($"reads {extractor.Help.Reads}."), prose, StringComparison.Ordinal);
            Assert.All(extractor.Help.DoesNotSee, limit => Assert.Contains(Prose.Words($"does not see {limit.Says}."), prose, StringComparison.Ordinal));
        }

        Assert.Contains("listed (version 1; every file no other claims)", topic, StringComparison.Ordinal);
        Assert.Contains("markdown (version 2; .md)", topic, StringComparison.Ordinal);
        Assert.All(WorkflowKeys.CodebaseGraph, key => Assert.Contains(key.Name, topic, StringComparison.Ordinal));
        Assert.Contains($"failOn: {WorkflowStepTypes.GraphIncomplete}, which fails it ({HarnessExit.CommandFailed.ToString(CultureInfo.InvariantCulture)})", prose, StringComparison.Ordinal);
        Assert.Contains("30 minutes where it says no timeoutMinutes", prose, StringComparison.Ordinal);
        Assert.Contains($"{HarnessLayout.OrchestratorsDirectoryName}/<run>/{OrchestratorLayout.GraphDirectoryName}/", prose, StringComparison.Ordinal);
        Assert.Contains(OrchestratorLayout.GraphManifestFileName, prose, StringComparison.Ordinal);
        Assert.Contains(OrchestratorLayout.GraphSyncFileName, prose, StringComparison.Ordinal);
        var answers = System.Text.Json.Nodes.JsonNode.Parse(WorkflowStepTypes.Find("graphify-codebase")!.Output!)!["properties"]!.AsObject().Select(member => member.Key).ToList();
        Assert.Contains("seconds", answers);
        Assert.Contains($"A graphify-codebase step answers {string.Join(", ", answers)}:", prose, StringComparison.Ordinal);
        Assert.Contains($"one of every {DssHarness.Core.Codebase.CodebaseGraphService.ReadShare.ToString(CultureInfo.InvariantCulture)} of the machine's processors", prose, StringComparison.Ordinal);
        Assert.Contains($"first {DssHarness.Core.FileSystem.BinaryContent.Window.ToString("N0", CultureInfo.InvariantCulture)} bytes - git's own window", prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"{OrchestratorLayout.GraphViewsDirectoryName}/<agent>.json, an agent's view"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("with in, its view is that agent's - the base for one that only reads - and its output's view names the one it synced"), prose, StringComparison.Ordinal);
        Assert.All(DssHarness.Core.Codebase.CodeEdgeKinds.Described, described => Assert.Contains(Prose.Words($"{described.Kind}, {described.Meaning}"), prose, StringComparison.Ordinal));
        Assert.Contains(Prose.Words("a file it could not read is tried again at every sync"), prose, StringComparison.Ordinal);
        Assert.Contains(HelpTopics.Described, described => described.Name == "codebase-graph");

        var layout = HelpText.Of("orchestrators").StandardOutput;

        foreach (var file in new[] { OrchestratorLayout.GraphManifestFileName, OrchestratorLayout.GraphSymbolsFileName, OrchestratorLayout.GraphExtractsDirectoryName, OrchestratorLayout.GraphSyncFileName })
        {
            Assert.Contains($"{OrchestratorLayout.GraphDirectoryName}/{file}", layout, StringComparison.Ordinal);
        }
    }
}
