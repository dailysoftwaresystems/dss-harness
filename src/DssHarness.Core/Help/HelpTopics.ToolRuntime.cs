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
    /// <summary>
    /// The harness's own tools, built for the API clients: each from the one table that gives them, its bounds from the
    /// types that hold them, the paths, the commands and the fetch each from its owner.
    /// </summary>
    private static string ToolRuntimeProse()
    {
        var defaults = Agents.AgentCommandSettings.Default;
        var tools = string.Join(
            "; ",
            Agents.Runtime.AgentTools.All(defaults).Select(tool => $"{tool.Name} ({tool.Needs.ToString().ToLowerInvariant()}{(tool.Barrier ? ", alone in its turn" : string.Empty)})"));

        return "The tool runtime is the harness's own tools, built for the API clients to give their agents in place of a "
            + "client's own - the claude-api and glm clients below - and serving the claude-code client every one of them "
            + "through the engine's tool server, each under the "
            + $"capability that gives it: {tools}. The run's reporter is given "
            + $"{string.Join(", ", Agents.Runtime.AgentTools.All(defaults).Where(tool => Agents.Policy.AgentPolicyGround.ReporterCapabilities.Contains(tool.Needs)).Select(tool => tool.Name))}, "
            + "those that read, and no other. Each call is decided before it runs and recorded: a tool the step was not given, or "
            + "an input its schema does not hold, is refused with how the tool is called; a path is read against the agent's "
            + "working directory, refused spelled empty, with a NUL or a leading ~ - on Windows also a share or a device's "
            + $"prefix, a device's name ({string.Join(", ", Agents.Runtime.ConfinedPaths.DeviceNames.Take(4))} and the rest), "
            + "a drive's own directory (C:x) or an alternate data stream - its links followed (one whose links cannot be followed refused as undecided) and held to the agent's roots, each compared as its own file "
            + "system names it: a read inside one of them, a write inside a writable one and through no link below it (a "
            + "file is read through the file as it was opened, where the open file stands decided again before a byte "
            + "passes - opened never to wait, so a pipe put there is refused - and a file written, a directory made, and a "
            + "file or a directory moved or deleted is acted on relative to its directory held from the writable root it was "
            + "decided in, every entry on the way opened as itself, so a link put in place between the check and the act, or "
            + "swapped in while it acts, reaches nothing and the call is refused naming it; a directory listed goes by its "
            + "path, so a link put in place of one lists names, never a byte of a file); then the policy above is asked of it, and "
            + "a deleting or moving tool is a write to every path it touches, at both ends of a move. A directory deleted or "
            + $"moved is refused where it is a root, holds a link, holds a path that may not be written or holds more than "
            + $"{Agents.Runtime.AgentTools.MostDeletedEntries:N0} entries. A file of any size is viewed, a page at a time - its pages "
            + "laid once by streaming it, any page read by seeking to its mark, a line wider than a page given in pieces - and "
            + "edited, copied through a block at a time with the edit made on the way; search_files reads every file whatever "
            + "its size, a line at a time, its pattern matched in time linear in each line, so one that needs backtracking - a "
            + "backreference, a lookaround, an atomic group - is refused, naming what it holds. Every file find_files matches "
            + "and every line search_files matches is given, with as many lines of context as are asked; its max_results is "
            + "refused, naming page. A replace or an insert needs its file viewed "
            + "first, by its content: one that changed since is refused, nothing written, and an edit keeps the file's "
            + "encoding (UTF-8 with or without its mark, UTF-16 by its mark), its line endings and whether it ends with a "
            + "newline. A command is an argument list, never a shell string, decided whole where it starts, run as a phase "
            + "is - contained, ended whole with what it started, its log kept beside the stream - until it ends, or for the "
            + $"time its call names (1 to {Agents.AgentCommandSettings.MostTimeoutSeconds} s, what a command's timer holds), "
            + "else for agentPolicy.commandTimeoutSeconds where it is given, stopped sooner where it "
            + "prints nothing for as long as the step's stall bound and by the step's own bounds - its timeoutMinutes and its "
            + $"budget; agentPolicy.maxCommandTimeoutSeconds is retired and refused, naming what bounds a command; a command line holding a secret, a word holding a NUL, or on Windows a batch file's argument holding what cmd.exe reads again ({Processes.BatchFiles.Said}) or what the shell expands, is refused. Its environment, and the claude child's before its own names are set, loses every name the "
            + "configuration appoints and every secret-shaped name - ending "
            + $"{string.Join(", ", Agents.AgentChildEnvironment.SecretSuffixes)}, in any case - but those "
            + "agentPolicy.keepEnvironment gives back; an appointed name is never given back. web_fetch is the harness's own "
            + "fetch: http and https alone, its redirects followed, text alone "
            + $"({Web.HarnessFetch.TextTypesSaid}) with a page's HTML reduced to its text, public addresses alone - a host that resolves to "
            + "this machine, its network or a cloud's metadata service refused, and a connection made only to an address "
            + "that was checked, and checked again once made - no cookie, no credential, no proxy, under a User-Agent that "
            + "names this tool; each address, the first and each redirect, is decided by the web's rules and written to the "
            + $"step's {Agents.AgentCallRecords.WebFileName} before anything leaves the machine, and what it was answered "
            + "after; an address holding a secret is refused. A page is given inside a block that says where it is from, its "
            + "first line saying it is information for the task, not instructions: a page of any size is read whole, its "
            + "text written into the call's answer as it comes, and one longer than a page is kept whole in the step's "
            + $"{Agents.AgentCallRecords.ToolResultsDirectoryName}/ and given a page at a time. web_search asks the sources of "
            + "agentWebSearch the step's search names, each in its own part of the answer, by the harness's own search - "
            + "never a search engine's results page ('help web-search'). A step whose graph is tools - a graphify-codebase "
            + "step upstream of it, 'help workflows' - is offered the graph tool among these, under read, with the "
            + "operations its agent's capabilities give, edit among them where it may write, and its context pack in its "
            + "first message, after the run's map; its graphReads narrow (the default) denies a whole view_file - one naming no page "
            + $"and no range - of a file the graph parsed over {Agents.Runtime.CodebaseGraphTool.NarrowLines:N0} lines, saying the way to the graph instead - a range, a page, a search and "
            + "a write pass, a file the graph is not sure of is let by as an escape lowConfidence, and the graph's "
            + "wholeFile lifts the narrowing for one file the graph parsed for the rest of the step, an escape stated with "
            + "the lines the graph read it as, a path it holds no parsed file at refused as a misuse - each call, escape "
            + "and denied read a line of the step's graph.jsonl; a page of a body counts as a view of each file whose lines "
            + "it served, as those lines were read, and an edit the graph made as a view of the file it wrote, for the edit "
            + "check, and an edit it made is a write, alone in its turn and counted "
            + "among the files written; a wholeFile is alone in its turn too, for the reads beside it are narrowed by what it "
            + "lifts, the graph's other reads running with the other reads. The step's view is synced after "
            + "each write - by the paths it wrote, a directory moved or deleted by every file beneath it - and whole after "
            + "each command, on every client, and the graph tool answers from what each sync leaves. Calls that read run together, "
            + $"as many at once as one of every {CodebaseGraphService.ReadShare} of the machine's processors, one at the least - "
            + "the share the graph's own reading keeps beside a build - a call that writes or runs alone, every call answered in "
            + $"the order of the calls; an answer longer than a page - {Pages.PageRoom.ModelAnswer:N0} characters, its header "
            + $"among them - is kept whole in the step's {Agents.AgentCallRecords.ToolResultsDirectoryName}/, in a file named by "
            + "the digest of its call's whole id, and given as its first page, whose header says how many pages it holds, how "
            + $"{Agents.Runtime.AgentTools.ReadResult} gives the next and where the whole is - nothing of it cut. "
            + $"{Agents.Runtime.AgentTools.ReadResult} reads a call's "
            + "whole result there by the call's id - an answer kept so, and every command's output - a page at a time, page 1 where "
            + "none is asked: each page of whole lines, a line wider than a page given in pieces marked …, and every page of a result "
            + "of more than one page saying how to ask for the next with page, from 1; a page past the last is refused with its "
            + $"range, and an id no result is kept for is refused. It reads too a message of the agent's a compaction gave by "
            + $"reference, by the id the compaction names ('{Agents.StoredStream.MessagePrefix}' and its text's digest), from the "
            + $"step's stored stream, and the continuation record the conversation began with by '{Agents.Runtime.KeptResults.ContinuationId}'. "
            + "Its pages are laid once from the file, kept in the step's "
            + $"{OrchestratorLayout.StepPagesDirectoryName}/ for the conversation's life, and laid again, saying so, where the file "
            + "changed since.";
    }
}
