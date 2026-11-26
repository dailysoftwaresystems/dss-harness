using System.Globalization;
using System.Text;
using DssHarness.Core.Ci;
using DssHarness.Core.Anchors;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
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
using DssHarness.Core.Secrets;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

public static partial class HelpTopics
{
    private static string RenderCodebaseGraph()
    {
        var builder = new StringBuilder();
        var extractors = CodeGraphExtractors.OfThisBuild;

        builder.AppendLine("Codebase graph");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A run's graphify-codebase step maps the repository and keeps the map in the run's directory, "
            + $"{HarnessLayout.OrchestratorsDirectoryName}/<run>/{OrchestratorLayout.GraphDirectoryName}/: every file the tree lists is a node, "
            + "and each file an implementation reads gives the declarations in it - projects, namespaces, types, members, a "
            + "document's sections - each with its lines, its header on one line and the first sentence the code writes of itself, and the "
            + $"directed edges between them, each of one kind: {string.Join("; ", CodeEdgeKinds.Described.Select(described => $"{described.Kind}, {described.Meaning}"))}. "
            + $"An edge says how sure it is: {CodeConfidences.Exact} where the code itself says where to look, "
            + $"{CodeConfidences.Unique} where one declaration has that name and arity, {CodeConfidences.Candidates} where two or "
            + "more do, an edge to each, however many, none picked and none dropped; a name the repository does not declare is "
            + "counted external. The keys' join gives a configuration key a uses edge of "
            + $"{CodeConfidences.Candidates} from each declaration holding a string literal of its last segment, of its whole path, of its path in brackets in either quote mark (Logging.LogLevel['Microsoft.AspNetCore']), "
            + "or of the path as .NET's configuration spells it (Payments:TimeoutSeconds, Payments__TimeoutSeconds, Limits:1:Value, Logging:LogLevel:Microsoft.AspNetCore, a name holding a dot one place), "
            + "or of a path that runs through a YAML alias or merge to it, "
            + "and from each field or property its last segment names as codebaseGraph.keyNaming writes it, even where one key alone "
            + "is so named, for nothing says the code reads that key; every key a literal or a name names is joined, however many "
            + "keys share it - a name or a version each file of data holds - and a project's own MSBuild property, which is its "
            + "file's alone, is joined to none. A use is recall and never a doubt: no uses edge is counted, nor makes a node or a "
            + "file unsure. "
            + "The graph masks nothing as it reads and stores - a literal, a key's value, an element's text are kept unmasked "
            + "- and a run's secrets are masked where the graph reaches the run: each answer of the graph tool and the "
            + "codebase part of each message, as every answer and message of a run is, and a result of more than one page before "
            + "it is paged and kept; the verb answers a person's own view, unmasked. "
            + "A configuration key, an element and every node a style sheet declares - a rule, keyframes, a mixin, a function, "
            + "a font face, a variable - are reached only by what names one so - a key's join, an MSBuild $(Name), a selector, "
            + "a sheet's var(--x), $x, @x, @include, function call, animation name or font family, a use through an import's "
            + "prefix, a name an import binds that the file exports the node by (import { btn } from './x.module.css') - never "
            + "by a name alone, as code names code; a sheet's name is looked for nearest first - the sheet's own, what it "
            + "imports, then the repository's - and a CSS module's class is seen from its own file and its importers alone, "
            + "by name from its own sheet's composes or @extend, never by a class markup writes, which the module renames - a "
            + "Vue component's <style module> class through $style.x alone. "
            + "No model writes any of it: every node and edge is read from a file by a program.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "What it reads. The main checkout as it stands - its HEAD and whatever is uncommitted in it - listed by three calls of "
            + "git that take no lock: ls-files, status and hash-object, so a file git ignores is not in it; a path whose file git "
            + "does not look at - kept out of the checkout, as a sparse checkout or skip-worktree keeps it, or marked "
            + "assume-unchanged - is in it where its file is there, hashed and read as it stands, and a repository inside the tree "
            + "that git lists whole is not entered. Each file is read by "
            + "the one implementation that claims it: by its exact name, else by an ending codebaseGraph.claims maps, else by its "
            + "extension, else by the fallback, which lists "
            + "it; where two claim one file their tests of its whole text decide, and a file both or neither says is theirs is "
            + "listed ambiguous. Each file read is listed once more when the reads are done - git asked of it again as a sync "
            + "asks, never a hash of a file git finds clean - and what was made of it is kept only where the listing still gives "
            + "it the blob it was read under and the file still stands, size and time, as its read saw it: a file written while "
            + "it was read, or after and put back as it was, keeps nothing. "
            + "A file git hashes that goes while git hashes it is said gone, and every other is hashed. "
            + "A sync reads a file again only where its blob, the implementation that claims it, that "
            + "implementation's version, or what languages decide of it changed, or where its extract is gone; a "
            + "file it could not read is tried again at every sync, and every other file keeps its extract. A run with no graph "
            + "yet starts from the newest earlier run's. Every graphify-codebase step of a run syncs the one graph, wherever the "
            + "flow puts it - each instance, visit and retry one more sync, and a sync of a tree that did not change reading "
            + "nothing but what it could not read before.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An agent's view. With in, a graphify-codebase step syncs the view of that agent's worktree: the worktree listed the "
            + "same way and held against the base - the main checkout's graph, synced first where the run has none - as an "
            + "overlay of each file the worktree holds otherwise and each of the base's it does not hold, so no file is read "
            + "whose extract the base or an earlier sync made. An agent that only reads is served the base: its own tree, brought "
            + "to the main checkout's HEAD as each of its steps starts, is never synced as a view. "
            + "The engine syncs on its own too, under the claim its step already holds: a reason step's view where it starts, "
            + "and a class-closure check's, which reads what the worktree still declares from it - "
            + "its agent's, the one the step it goes on with works in, or the base for one that reads; the base by every path "
            + "the run's folds changed that it has not taken yet - after a fold or a delete-agent's fold, and before each "
            + "attempt of one - with no listing, the run's ledger of its folds saying which it took, and with every path git "
            + "names changed by what moved the checkout's HEAD since the graph's last sync, or whole where git cannot name "
            + "them; the target's view after a "
            + "refresh or a rebase, which write the agent's tree alone; and the view of each agent no longer live leaves the "
            + "graph. One of these that fails, for any cause but a stop, is said - a warning that says what takes it up again, "
            + "and a line of the sync log that says why where it names the view - and its step goes on, since the graph is a "
            + "map. Syncs "
            + "of one view wait for each other and syncs of different views run side by side, each on one snapshot of the base; "
            + "when the base moves, each view is held against it again and no file is read - one that could not be is said, and "
            + "made again by its next whole sync. Linking again finds again only the "
            + "references of the files that changed and those that read what a change altered - a name, a scope, a base, an "
            + "import, a path - and every other reference keeps what it found.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "What it does not see. The credential slots, the tool's own state and the workflows' own files (.harness-config/workflows: "
            + "the prompts, agents and schemas every step is given already, which a step's pack would otherwise match first) are never listed, so never served. A file "
            + $"with a NUL among its first {BinaryContent.Window.ToString("N0", CultureInfo.InvariantCulture)} bytes - git's own window - or a Git LFS pointer, is {CodeFileStates.Binary}; "
            + "every other file it claims is read whole, streamed, whatever its size - a view an earlier build left holding a file "
            + $"{CodeFileStates.TooLarge}, unread for its size, says so once at its next sync, which reads it; one that is neither UTF-8 nor UTF-16 by its mark, is named "
            + "in no UTF-8 or by no path relative to the tree that every machine reads alike - a drive, a leading slash or "
            + "backslash, a part empty, '.' or '..' - went or changed while it was read, could not be read, or that a content test or its implementation "
            + $"failed on is {CodeFileStates.Unreadable}, with why - an implementation's defect among them; a link, or a path one lies "
            + "along - a directory junction among them, whether or not a file is where it leads - is listed and not followed; a "
            + "submodule is listed and not entered. "
            + "Generated code is kept, its file's entry naming why it is generated, or - under generated: exclude - listed with its "
            + "declarations left out. What a program cannot bind by reading the code - reflection, a call a macro writes, a call "
            + "through a pointer, what dependency injection hands in - is no edge; an interface's call reaches each implementation "
            + "through a dispatches edge.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"Each implementation this build holds - its language, its version and what it claims, what it reads, and each limit it does "
            + $"not see under its short name - is said by 'help {CodebaseGraphLanguages}'.");
        builder.AppendLine();

        AppendWrapped(
            builder,
            "The workflow's codebaseGraph shapes the run's one graph, said once for the run since every graphify-codebase step "
            + $"syncs the same graph; codebaseGraph.languages names the implementations used - {string.Join(", ", extractors.Languages)} - and "
            + "a file whose claimant is left out is listed by the fallback:");
        builder.AppendLine();
        AppendKeys(builder, WorkflowKeys.CodebaseGraph);
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The graph's files, under its directory: "
            + $"{OrchestratorLayout.GraphManifestFileName}, the manifest of the main checkout - every file it keeps, with its blob, "
            + $"its state and its extract; {OrchestratorLayout.GraphSymbolsFileName}, the link result - every node with its handle, every "
            + $"edge - made again where it does not match its manifest; {OrchestratorLayout.GraphExtractsDirectoryName}/<ab>/<blob>-<path>.<language>-<version>.json, "
            + "one file's extract, written once and named by its blob, the whole digest of its path, its implementation and version; and "
            + $"{OrchestratorLayout.GraphViewsDirectoryName}/<agent>.json, an agent's view - the base it is held against, the "
            + "worktree's HEAD, each file it holds otherwise and each it does not hold; and "
            + $"{OrchestratorLayout.GraphSyncFileName}, one line for each sync, counting the files it could not read. Each is written whole, an extract before the manifest "
            + "or view that names it, so a sync that stops part way leaves the last whole manifest, and the last sync to end with "
            + "none under way removes each extract neither the manifest nor a view names; a manifest that does not read is built again whole, and never guessed at, and a link result "
            + "linked by another build's linker is linked again, no file read again. delete-orchestrator removes the graph with its run, and never counts it among what "
            + "--delete-evidence guards: it is made from the tree, and made again whenever asked.");
        builder.AppendLine();
        var answers = Json.JsonDepth.ParseNode(WorkflowStepTypes.Find("graphify-codebase")!.Output!)!["properties"]!.AsObject().Select(member => member.Key).ToList();

        AppendWrapped(
            builder,
            $"A graphify-codebase step answers {string.Join(", ", answers)}: languages gives each implementation's files and nodes, "
            + "and skipped every file the graph holds no declaration of, with its state and why. A graph is a map, never red: a file it could not read leaves the step done, listing it, "
            + $"unless the step says failOn: {WorkflowStepTypes.GraphIncomplete}, which fails it ({HarnessExit.CommandFailed}) naming each. git that "
            + $"could not be started fails it ({HarnessExit.ToolMissing}), git that failed to list the tree fails it "
            + $"({HarnessExit.CommandFailed}), saying what git said, and so does a time limit passed ({HarnessExit.Incomplete}; "
            + $"{WorkflowStepTypes.Find("graphify-codebase")!.DefaultTimeoutMinutes} minutes where it says no timeoutMinutes). Its extraction "
            + $"runs in the engine's process on one of every {CodebaseGraphService.ReadShare} of the machine's processors, so a build beside it is not starved; it "
            + "runs no leg and counts against no limit; with in, its view is that agent's - the base for one that only reads - "
            + "and its output's view names the one it synced.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "What an agent asks of the graph is one tool, graph, whose op says what is asked: "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Find)} - the nodes a query's words match, best first, generated code after every other, a tie broken by how near the step's own work each is - its context pack or a working set it was given, a file it writes, a file its agent changed, a path or a name its prompt holds - then by id, {CodebaseGraphTool.FindLines} a page; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Node)} - a node's card and its edges by kind, {CodebaseGraphTool.NodeLinesAKind} of each a page; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Related)} - the nodes one relation reaches ({string.Join("; ", GraphRelations.Described.Select(relation => $"{relation.Name}, {relation.Meaning}"))}), as many hops as asked, from 1, by distance - every node reached gone through and reached once, so a hop that reaches nothing new ends the walk, every other part of a declaration walked as the node itself but by {GraphRelations.Parts} - {CodebaseGraphTool.RelatedLines} a page; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Outline)} - a directory's files, its subdirectories' too, a section for each directory by path - each file one result: its own node's line with the language that read it, or the state none read it in, and test where it is test code, then each of its top declarations, a type or a member no type or member of the file holds, with what it and the declarations inside it test where the graph's tests edges say so - a file's or a type's declarations, or, with neither, the repository's, the root's own files outlined as a directory's are; {CodebaseGraphTool.OutlineLines} a page; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Around)} - a node's place: what it is declared in, outermost first - its file, its namespace, its type - its siblings in the nearest of those, its members, the declarations just before and just after it in its file, and its callers and callees, {CodebaseGraphTool.NodeLinesAKind} of each a page; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Impact)} - what a change of a node reaches, by the one walk the change-first pack leads with: its callers at every distance, what it overrides and what overrides it, its implementations, the interface members that dispatch to it and its derived types, and every test that reaches any of them, each under the relation whose edge reached it, with how many edges its best path has and that path, nearest first, {CodebaseGraphTool.RelatedLines} of each a page; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Body)} - the lines of the nodes named, one or more - each node served once and each file read once, again only where its lines moved - read fresh, whatever the file holds, and numbered as view_file numbers them, a type's members' bodies left out unless full - each member's signature line kept - {CodebaseGraphTool.BodyLines} lines a page; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Sites)} - every place that decides data - a symbol as node takes one, its handle, its id, a path:line or a name, the node a handle, an id or a path:line names asked of alone and searched for by its name, never read as a key path, a name written with its scopes searched for by its last part, as code writes it - this[] of Canvas.this[]; a key path, dotted - a key whose own name holds a dot or a bracket quoted in brackets, in double or single quotes, Logging.LogLevel[\"Microsoft.AspNetCore\"] or Logging.LogLevel['Microsoft.AspNetCore'], or with its names' own dots as they are, Logging.LogLevel.Microsoft.AspNetCore, which asks of every key it may mean - or as .NET's configuration writes one (Payments:TimeoutSeconds, Payments__TimeoutSeconds, Logging:LogLevel:Microsoft.AspNetCore), asked of every key that names it in any spelling - one whose own name spells it, an environment variable's, among them - searched for by its last key, as data writes it, and by the path of every key it names as an environment variable writes one, however many - a name spelt so read as a key path where it names a key, where only the path it spells finds its definers, code's members, none a key (Secret of JwtSettings:Secret, as the options pattern binds a section), or where nothing names it, and as the name code declares where a definer, none of them a key, is found by it as it is written - by its name, its qualified name or with some of its scopes (RETRY__LIMIT of Retry.RETRY__LIMIT, __validate of Foo.__validate); a diagnostic id; a rule's selector as a rule writes it (.btn, #main, .card__title), never a key path, asked by its name as a class list writes it too; or a quoted literal, asked by its value alone: what defines it, holds it as a key, calls it, uses it, makes it, names it by its kind - a type mention, an isinstance, an instanceof, a catch - inherits it or mixes it in, implements it or answers it for a type, overrides it or is overridden by it - only the definers' own edges walked, one hop each, so a hub is listed and never walked through - every literal that holds it as a whole word, and, joined to them, a text search of what the graph does not answer of it: the files the view does not hold, those it holds in a state no implementation of a language read them in - listed only, for no extractor reads their language, unreadable, ambiguous - those whose extract it no longer holds, those it is unsure of, those it outlines, whose values and text no literal of it holds, those whose nodes make a reference of the data's name it bound to nothing of that name, and, for a quoted literal, every file; each place one card - the node at it, or a raw line where the graph read no node of its file - saying every way it decides, an edge's other than exact with how sure it is, a literal's and a text hit's lines beneath it, never on a page without it - a place no page holds whole given in parts, each page after its first beginning with its card again - the cards grouped by file kind - the language that read the file, else its extension or its name - and tier - the project nearest above the file, else its top directory - {CodebaseGraphTool.RelatedLines} places a page; every place held to the step's reads, those they do not reach counted; with the coverage lines that end every such answer - how many files of each kind of what the graph does not answer were searched once they were read, with their hits, the files it lists only and those it is unsure of said where none was, the forms a selector and a key path were searched in, and, where any file the graph lists only was searched, that nothing is assumed of a language with no extractor - and every limit of each language whose files the graph read - one some of those files show the sign of in full, with how many show it, and every other by its short name, on one line for the language that points to 'help {CodebaseGraphLanguages}', which says each in full under its name; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Edit)} - a node's lines replaced where the hash given is its hash now - a node spread over files in the part that hash names - the file's encoding, line endings and final newline kept, and nothing written where it changed; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.WholeFile)} - the narrowing of reads lifted for one file, with why; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Pack)} - a page of the step's context pack, by page - given where the pack has a page its first message does not hold: every page, from page 1, where none was pushed; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Changes)} - a page of the codebase changes a conversation gone on with is told, by page - given where they go on past its message's; "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Map)} - a page of the repository map, by page, from page 1 - given wherever the run has a map, for none is pushed to a step with the tool. "
            + "body and edit reach a file only inside the tree, each link resolved, never through a link out of it. "
            + "A step whose graph is tools is offered the tool - the API clients among their own, Claude Code through the engine's tool server - with the read operations, wholeFile only where its reads are narrowed and edit only on the API clients (the probe measured Claude Code takes an Edit after a ranged Read, so an agent that means to change a body reads its lines and Edits them), its schema naming those alone, and query-codebase-graph below runs its read operations by hand. "
            + $"The reads such a step makes already answer from the graph too ('help agent-clients'), so the graph costs it no turn of its own: {Agents.Runtime.AgentTools.SearchFiles} gives each hit inside a declaration of a file the graph parsed under that declaration's line, the innermost that holds it, a range of {Agents.Runtime.AgentTools.ViewFile} is headed by the declarations its lines meet, and where its reads are narrowed a whole {Agents.Runtime.AgentTools.ViewFile} of a file the graph parsed over {CodebaseGraphTool.NarrowLines} lines is answered with the file's outline in its place - each declaration checked against the file as it stands, as a body is. "
            + "An operation given a member it does not read, one of the wrong type, or missing one it needs, is refused with a usage line. "
            + $"Every node an answer names carries path:start-end - a declaration # with the first eight characters of its digest too, a file its lines - and no answer of a read is cut: each is pages of whole entries (GraphPages), every page's entries within {CodebaseGraphTool.MostCharacters} characters less what the pages reserve - an entry wider than that given in pieces, a page each, each marked with … where it goes on - and a block's head on the page that serves its first line; an answer of more than one page starts with the line 'page k of n', which gives the exact total and how to ask for the next on every page but the last, which says it is the last, and any page is asked with page, from 1, an answer with no header holding all of it; a page past the last is refused with the answer's range, and the totals a header says are counted, never estimated. An edit's answer and a wholeFile's are one page, said once it is done, and neither takes page. "
            + $"The page that computes an answer of more than one page keeps its whole result - under the digest of the graph the view answers from, its link result and every file as the view holds it, so the step's own writes move it as a fold does, in {OrchestratorLayout.StepPagesDirectoryName}/ under the step's directory or the side-task's, every result kept for the conversation's life whatever it holds, packed - its one line compressed - and put away when the conversation ends: a step done whose work no later step goes on with, every step of a run that is done - and a later page, a resumed conversation and a step that goes on with the work read pages of that one result, each under the read rules it was computed under; an answer of one page is never kept, and page 1 never reads what was. A later page asked after the graph moved, or with no result of page 1 that reads, is made again on the graph as it stands, and its header says which. The last page of every answer ends naming the files whose extract the graph no longer holds, if any. Where the graph is not sure - no node matched, a name it does not hold, "
            + $"an edge of {CodeConfidences.Candidates} but a use's, which is recall and never a doubt, a node named with external callees or in a file dynamic or generated - the answer adds text: lines that hold the name as a whole word, "
            + $"searched as search_files searches but in every file whatever it holds and never timed, each path:line with no hash and its line whole, a section of their own, {CodebaseGraphTool.TextHits} of each word a page, all of them paged - nothing left out - and the files it did not read said; a path or a path:line is searched for by no word.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A reason step that has a graph has, at its start, its context pack made - the part of the graph its work touches, made from its view and what the step says alone, "
            + "so the same step on the same tree is given the same pack. Its task is what the run handed its prompt - the values the prompt was filled with: the request, the work item, "
            + "the upstream outputs it reads, with a handoff - never the prompt's own words, which every run of the workflow is given alike. Its seeds are what that task names exactly - a symbol or a path in backquotes (a symbol several nodes the step may read carry seeding every one of them, however many, the weight of a name written shared among them), a qualified name or its last parts (Order.Total), "
            + $"a handle, a path:line as a stack frame or a compiler writes one - every site and home of the classes of the work item it is an instance of, where its prompt reads them ({{input.item.classes}}), each site's declaration at the weight of a node named and its file's declarations at a named file's where it names no symbol its file carries, so a fresh agent holds every site's declaration - the working sets it is given, the declarations of the files its writes covers, and every node its words match, each at its score's share of the best match's weight. "
            + "From all of them at once the walk goes best first over calls both ways, the type relations, tests and the types a node names, with no count of hops: each node is reached once, at its best score - its seed's weight "
            + $"halved at each edge, weighed by the edge's confidence ({string.Join(", ", CodeConfidences.All.Select(confidence => $"{confidence} {CodebaseContextPack.Confidence(confidence).ToString(CultureInfo.InvariantCulture)}"))}) "
            + "and shared among the neighbours of that kind the node it was reached from has, so a hub is gone through and its many neighbours are ranked down by how many they are, never cut. "
            + $"It is laid in pages of the step's graphPromptTokens, or of {CodebaseContextPack.DefaultTokens.ToString(CultureInfo.InvariantCulture)} estimated tokens - bytes over {EstimatedTokens.BytesPerToken} - where it says none, or 0 on a step with the tool, best first, "
            + "a page's lines of the task by file, one line each as find gives it with how it stands to the node it was reached from. Pull, not push: a step whose graph is tools is pushed none of it - "
            + $"graph's {CodebaseGraphTool.NameOf(GraphOp.Pack)} gives its pages, from page 1, when asked - unless its graphPromptTokens says a size, which pushes page 1 at that size at the head of its first message, "
            + $"for a step with the tool was measured using almost none of a pack pushed to it; a step whose graph is map, which has no tool, is pushed page 1 at its graphPromptTokens, {WorkflowStep.DefaultGraphPromptTokens.ToString(CultureInfo.InvariantCulture)} where it says none. "
            + "Page 1's last line says how many nodes and tokens it holds and either that nothing is left out "
            + $"or which page of how many and how to ask for the next - graph's {CodebaseGraphTool.NameOf(GraphOp.Pack)} with page - the pack's pages laid once whole with it, so every page's last line names the total, and the last saying it is; a step whose graph is not tools is told the rest is on pages a step with the tool asks for. "
            + "graphPromptTokens 0 gives a step whose graph is map no pack, and so does a size that cannot hold a page's first and last lines. A step that starts from its changes is packed first from "
            + "the declarations its tree changed against its base - an agent's where its worktree was made or last moved to, the main checkout's the run's start - each with where its lines differ, "
            + "one moved between files and one taken out as the base held it, then their impact walked the same way from them - their tests, carried whole, first, then their callers, overrides, implementations and "
            + $"dispatches, and derived types, as far as it goes - then from the task; where nothing changed, its first line says so and the task's seeds stand in. A file that differs and could not be compared - "
            + $"generated, or not read, there or at the base - is listed under '{CodebaseContextPack.NotCompared}' with why, and nothing in it is said unchanged.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The run's repository map is the tree at a glance: a header line - the commit it is of, how many files and "
            + "declarations the graph holds, and that the graph tool answers for the tree as it is now - the projects and "
            + "top directories with their counts, then the most central declarations one line each as find gives it, a "
            + "declaration's centrality the number of files other than its own and a test's whose references bind to it, raised by how "
            + "its name matches the run's task as find weighs a name's words - the share of the task's words its name holds "
            + "whole, each weighed by how often the task says it and how rare it is among the view's names, a name that "
            + $"holds them all raised as far as {Codebase.CodebaseGraphQueries.TaskRaise.ToString(CultureInfo.InvariantCulture)} more such files would raise it - no "
            + $"declaration of a file that declares a test and nothing generated. It is {FlowStepGraph.MapsKept} with what it was made from in "
            + $"{OrchestratorLayout.GraphMapMetaFileName} under the run's graph, made under its own lock so steps that start together keep one, and never made again in the run, a resumed run included - the base moves as folds land and the map does not - paged at whole lines under "
            + $"codebaseGraph.mapTokens ({Codebase.CodebaseGraphShape.DefaultMapTokens.ToString(CultureInfo.InvariantCulture)} by default; 0 gives no map), no line dropped: page 1 holds the header and the whole lines that fit that size, and ends, where more pages follow, with a line naming which page of how many - what a kept map's count is read from - and its pages are asked of the graph tool's "
            + $"{CodebaseGraphTool.NameOf(GraphOp.Map)}, from page 1 - the one wording of that line serving every step of the map's ground of reads, with the tool and without it, for the map is one file every step of the ground reads. One that cannot be made or read is said, and the step goes on without it. "
            + "A step whose graph is tools is pushed no map: no step with the tool was measured asking it for a page. Every launch of a step whose graph is map that begins a conversation of its own is given it as the <repository-map> part ahead of its "
            + "context pack, the same bytes for every step that reads alike, so a client that caches a prefix reads it from the "
            + "cache ('help agent-clients' says where each client sets its marks). A launch that goes on with a stored "
            + "conversation on the API clients is told, in its one new user message, <codebase-changes> instead: the "
            + "declarations its view changed since what that conversation was told of them - its pack and every delta after it - moved or changed with their new "
            + "range and hash, removed - and, where its step pushes its pack, the nodes of its own pack the conversation was not given, every one, told in pages of the step's pack's size, page 1 in the message and each later page by "
            + $"graph's {CodebaseGraphTool.NameOf(GraphOp.Changes)} with page, a page never too small to hold its first and last lines and a line; never a map or a "
            + "pack again whole, for a conversation's prefix is never changed, and no earlier message, tool result, map or "
            + "pack is ever sent changed; a step that pushes no pack, whose conversation holds no declaration of the codebase, is told none. A launch the walk of the step's order moves to another client or provider begins a "
            + "conversation of its own, and is given what its step pushes, as Claude Code, whose every launch begins one, "
            + "is. Each launch's first message is a line of the step's graph.jsonl - what of the graph it was pushed and what "
            + "that cost, or why it was given no pack - and the step's figures are its last launch's, with the pages of the map and the pack its tool was asked for counted apart.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"When a reason step that has a graph ends, the engine writes its working set, {OrchestratorLayout.StepWorkingSetFileName} - never the model: "
            + $"each node it {string.Join(", ", WorkingSetUses.All.SkipLast(1))} or {WorkingSetUses.All[^1]}, with its handle, file, lines and hash at the step's end, every one however many, "
            + "edited first, then by how often each was touched; a node the view no longer holds kept by its id, marked gone. It is made from the step's "
            + $"{OrchestratorLayout.StepGraphFileName} - each node a graph call asked about by name (found), a line of which body gave (served), or an edit wrote, by its lines - and the tool calls its stored stream holds, "
            + "on every client's form: a read (Read, view_file) by where the lines its answer gave stand once in the file as the step left it, else by their numbers, a write "
            + "(Edit, replace_in_file, insert_in_file) by where its new text stands once there - every place, with replace_all - else by the lines it names, a whole file's "
            + "write (Write, create_file) by every line - each matched to the declarations whose own lines it meets, a member's lines never its type's; a deletion writes "
            + "no lines, and is counted with the writes placed nowhere, as a whole file's write is where the file does not read as the step left it. A reason step's pack is to be given the working sets of the step it goes on with (resume), of each step it depends on "
            + "directly, a group by each reason step in it, and of its own earlier visits of the same work item, newest first: each node a seed named exactly, weighed by how often the work touched it, an edited one above the rest.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"{ToolPackage.Command} {CodebaseGraphQueryService.CommandName} <run> runs one of the read operations by hand - --find <query>, --node <symbol>, "
            + "--related <symbol> --relation <relation> [--depth <n>], --outline [<path, directory or symbol>], --around <symbol>, --impact <symbol>, --body <symbol>... or --sites <data>, any of them with --page <n> - on the run's graph, the base or with "
            + "--agent <agent> that agent's view, and prints the answer an agent would be given, its page's header naming the pages; --json the same in one document holding the digest of the graph answered from, the page, the pages, the whole answer's total and the page's entries, each with what it counts toward the total, its section's caption and its node. Each call counts its page on the graph as it stands, keeping nothing: a script that reads page after page compares the digest to know they are of one graph. It reads only - no sync, "
            + "no lock, nothing written - the main checkout's run and configuration wherever it is typed, a misuse told before anything is read, and a view not made yet said and the base answered. It ends 0, "
            + $"{HarnessExit.Refused} where the run has no graph, and {HarnessExit.UsageError} for a usage error; no host is asked to run it.");

        return builder.ToString();
    }
}
