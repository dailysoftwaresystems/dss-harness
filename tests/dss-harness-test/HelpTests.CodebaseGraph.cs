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
        Assert.Contains(Prose.Words($"or which page of how many and how to ask for the next - graph's {DssHarness.Core.Agents.Runtime.CodebaseGraphTool.NameOf(DssHarness.Core.Agents.Runtime.GraphOp.Pack)} with page - the pack's pages laid once whole with it, so every page's last line names the total, and the last saying it is;"), prose, StringComparison.Ordinal);
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
        Assert.Contains(Prose.Words($"{DssHarness.Core.Agents.Runtime.CodebaseGraphTool.NameOf(DssHarness.Core.Agents.Runtime.GraphOp.Sites)} - every place that decides data - a symbol as node takes one, its handle, its id, a path:line or a name, the node a handle, an id or a path:line names asked of alone and searched for by its name, never read as a key path, a name written with its scopes searched for by its last part, as code writes it - this[] of Canvas.this[]; a key path, dotted - a key whose own name holds a dot or a bracket quoted in brackets, in double or single quotes, Logging.LogLevel[\"Microsoft.AspNetCore\"] or Logging.LogLevel['Microsoft.AspNetCore'], or with its names' own dots as they are, Logging.LogLevel.Microsoft.AspNetCore, which asks of every key it may mean - or as .NET's configuration writes one (Payments:TimeoutSeconds, Payments__TimeoutSeconds, Logging:LogLevel:Microsoft.AspNetCore), asked of every key that names it in any spelling - one whose own name spells it, an environment variable's, among them - searched for by its last key, as data writes it, and by the path of every key it names as an environment variable writes one, however many - a name spelt so read as a key path where it names a key, where only the path it spells finds its definers, code's members, none a key (Secret of JwtSettings:Secret, as the options pattern binds a section), or where nothing names it, and as the name code declares where a definer, none of them a key, is found by it as it is written - by its name, its qualified name or with some of its scopes (RETRY__LIMIT of Retry.RETRY__LIMIT, __validate of Foo.__validate); a diagnostic id; a rule's selector as a rule writes it (.btn, #main, .card__title), never a key path, asked by its name as a class list writes it too; or a quoted literal, asked by its value alone:"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a text search of what the graph does not answer of it: the files the view does not hold, those it holds in a state no implementation of a language read them in - listed only, for no extractor reads their language, unreadable, ambiguous - those whose extract it no longer holds, those it is unsure of, those it outlines, whose values and text no literal of it holds, those whose nodes make a reference of the data's name it bound to nothing of that name, and, for a quoted literal, every file;"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("what defines it, holds it as a key, calls it, uses it, makes it, names it by its kind - a type mention, an isinstance, an instanceof, a catch - inherits it or mixes it in, implements it or answers it for a type, overrides it or is overridden by it"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a literal's and a text hit's lines beneath it, never on a page without it - a place no page holds whole given in parts, each page after its first beginning with its card again -"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"the cards grouped by file kind - the language that read the file, else its extension or its name - and tier - the project nearest above the file, else its top directory - {DssHarness.Core.Agents.Runtime.CodebaseGraphTool.RelatedLines} places a page; every place held to the step's reads, those they do not reach counted;"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("with the coverage lines that end every such answer - how many files of each kind of what the graph does not answer were searched once they were read, with their hits, the files it lists only and those it is unsure of said where none was, the forms a selector and a key path were searched in, and, where any file the graph lists only was searched, that nothing is assumed of a language with no extractor - and every limit of each language whose files the graph read - one some of those files show the sign of in full, with how many show it, and every other by its short name, on one line for the language that points to 'help codebase-graph-languages', which says each in full under its name;"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("--body <symbol>... or --sites <data>, any of them with --page <n>"), prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The topic says every spelling the keys' join names a key by - a literal of its last segment, of its whole path, of
    /// its path in brackets in either quote mark, of the path as .NET's configuration spells it, and of a path through a
    /// YAML alias or merge - each read from the code, so a later wording change to the spellings is held.
    /// </summary>
    [Fact]
    public void TheCodebaseGraphTopic_SaysTheSpellingsTheKeysJoinNames_AsTheCodeHoldsThem()
    {
        var prose = Prose.Words(HelpText.Of("codebase-graph").StandardOutput);

        Assert.Contains(
            Prose.Words(
                $"The keys' join gives a configuration key a uses edge of {DssHarness.Core.Codebase.CodeConfidences.Candidates} from each declaration "
                + "holding a string literal of its last segment, of its whole path, of its path in brackets in either quote mark "
                + "(Logging.LogLevel['Microsoft.AspNetCore']), or of the path as .NET's configuration spells it "
                + "(Payments:TimeoutSeconds, Payments__TimeoutSeconds, Limits:1:Value, Logging:LogLevel:Microsoft.AspNetCore, "
                + "a name holding a dot one place), or of a path that runs through a YAML alias or merge to it"),
            prose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The codebase-graph-languages topic, under each of its names, says every implementation this build holds - its version, what
    /// it claims, what it reads and each limit it does not see under its short name, the name a sites answer gives a limit no file
    /// it read shows the sign of - each read from the code; and the codebase-graph topic points to it.
    /// </summary>
    [Fact]
    public void TheCodebaseGraphLanguagesTopic_SaysEveryImplementationAndEachLimitUnderItsName_FromTheCode()
    {
        var topic = HelpText.Of(HelpTopics.CodebaseGraphLanguages).StandardOutput;
        var prose = Prose.Words(topic);

        Assert.Equal(topic, HelpText.Of("graph-languages").StandardOutput);

        foreach (var extractor in DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild.All)
        {
            Assert.Contains($"{extractor.Language} (version {extractor.Version.ToString(CultureInfo.InvariantCulture)};", topic, StringComparison.Ordinal);
            Assert.Contains(Prose.Words($"reads {extractor.Help.Reads}."), prose, StringComparison.Ordinal);
            Assert.All(extractor.Help.DoesNotSee, limit => Assert.Contains(Prose.Words($"{limit.Name}: does not see {limit.Says}."), prose, StringComparison.Ordinal));
        }

        Assert.Contains("listed (version 1; every file no other claims)", topic, StringComparison.Ordinal);
        Assert.Contains("markdown (version 2; .md)", topic, StringComparison.Ordinal);
        Assert.Contains("    fenced-headings: does not see a heading inside a fenced block, which is code.", topic, StringComparison.Ordinal);
        Assert.Contains(HelpTopics.Described, described => described.Name == HelpTopics.CodebaseGraphLanguages);
        Assert.Contains(Prose.Words("Each implementation this build holds - its language, its version and what it claims, what it reads, and each limit it does not see under its short name - is said by 'help codebase-graph-languages'."), Prose.Words(HelpText.Of("codebase-graph").StandardOutput), StringComparison.Ordinal);
    }

    /// <summary>
    /// The codebase-graph topic, under each of its names, says every key of codebaseGraph, the graph's files, the step's output,
    /// its failOn and its time limit, each read from the code; and the orchestrators topic lists the graph's files.
    /// </summary>
    [Fact]
    public void TheCodebaseGraphTopic_SaysEveryImplementationKeyAndFile_FromTheCode()
    {
        var topic = HelpText.Of("codebase-graph").StandardOutput;
        var prose = Prose.Words(topic);

        Assert.Equal(topic, HelpText.Of("graph").StandardOutput);
        Assert.Equal(topic, HelpText.Of("graphify-codebase").StandardOutput);
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
