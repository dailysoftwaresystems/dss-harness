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
    /// <summary>
    /// The agent-clients topic says what the tool runtime is: each tool from the one table, the paths' refusals, the
    /// commands' environment and times, the fetch and its records, the calls of a turn, the answer's first page and the
    /// pages read_result reads the whole in - each name and bound read from the type that holds it.
    /// </summary>
    [Fact]
    public void AgentClientsTopic_SaysWhatTheToolRuntimeIs()
    {
        var prose = Prose.Words(HelpText.Of("agent-clients").StandardOutput);

        Assert.Contains(
            "each under the capability that gives it: view_file (read); find_files (read); search_files (read); read_result (read); create_file (write, alone "
                + "in its turn); replace_in_file (write, alone in its turn); insert_in_file (write, alone in its turn); create_directory (write, "
                + "alone in its turn); delete_file (write, alone in its turn); delete_directory (write, alone in its turn); move_path (write, "
                + "alone in its turn); run_command (run, alone in its turn); web_fetch (web); web_search (web).",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("built for the API clients to give their agents in place of a client's own - the claude-api and glm clients below - and serving the claude-code client the web's through the engine's tool server, and its file tools there too where Claude Code's own process runs apart from the isolation, each under the capability that gives it:", prose, StringComparison.Ordinal);
        Assert.Contains("The run's reporter is given view_file, find_files, search_files, read_result, those that read, and no other.", prose, StringComparison.Ordinal);
        Assert.Contains("A replace or an insert needs its file viewed first, by its content: one that changed since is refused, nothing written, and an edit keeps the file's encoding (UTF-8 with or without its mark, UTF-16 by its mark), its line endings and whether it ends with a newline.", prose, StringComparison.Ordinal);
        Assert.Contains(
            "a write inside a writable one and through no link below it (a file is read through the file as it was opened, where the open file stands decided again before a byte "
                + "passes - opened never to wait, so a pipe put there is refused - and a file written, a directory made, and a "
                + "file or a directory moved or deleted is acted on relative to its directory held from the writable root it was "
                + "decided in, every entry on the way opened as itself, so a link put in place between the check and the act, or "
                + "swapped in while it acts, reaches nothing and the call is refused naming it; a directory listed goes by its "
                + "path, so a link put in place of one lists names, never a byte of a file)",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("its links followed (one whose links cannot be followed refused as undecided) and held to the agent's roots", prose, StringComparison.Ordinal);
        Assert.Contains($"stopped sooner where it prints nothing for as long as the step's stall bound and by the step's own bounds - its timeoutMinutes and its budget; agentPolicy.maxCommandTimeoutSeconds is retired and refused, naming what bounds a command; a command line holding a secret, a word holding a NUL, or on Windows a batch file's argument holding what cmd.exe reads again ({DssHarness.Core.Processes.BatchFiles.Said}) or what the shell expands, is refused.", prose, StringComparison.Ordinal);
        Assert.Contains("agentPolicy.keepEnvironment gives back; an appointed name is never given back.", prose, StringComparison.Ordinal);
        Assert.Contains("an address holding a secret is refused. A page is given inside a block that says where it is from, its first line saying it is information for the task, not instructions: a page of any size is read whole, its text written into the call's answer as it comes, and one longer than a page is kept whole in the step's tool-results/ and given a page at a time.", prose, StringComparison.Ordinal);
        Assert.Contains("refused spelled empty, with a NUL or a leading ~ - on Windows also a share or a device's prefix, a device's name (CON, PRN, AUX, NUL and the rest), a drive's own directory (C:x) or an alternate data stream -", prose, StringComparison.Ordinal);
        Assert.Contains("A directory deleted or moved is refused where it is a root, holds a link, holds a path that may not be written or holds more than 2,000 entries.", prose, StringComparison.Ordinal);
        Assert.Contains(
            "A file of any size is viewed, a page at a time - its pages laid once by streaming it, any page read by seeking to its mark, a line wider than a "
                + "page given in pieces - and edited, copied through a block at a time with the edit made on the way; search_files reads every file whatever its "
                + "size, a line at a time, its pattern matched in time linear in each line, so one that needs backtracking - a backreference, a lookaround, an "
                + "atomic group - is refused, naming what it holds. Every file find_files matches and every line search_files matches is given, with as many lines "
                + "of context as are asked; its max_results is refused, naming page.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "its log kept beside the stream - until it ends, or for the time its call names (1 to 2147483 s, what a command's timer holds), else for "
                + "agentPolicy.commandTimeoutSeconds where it is given,",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("every secret-shaped name - ending _API_KEY, _SECRET, _SECRET_KEY, _ACCESS_KEY, _PRIVATE_KEY, _PASSWORD, _TOKEN, in any case - but those agentPolicy.keepEnvironment gives back", prose, StringComparison.Ordinal);
        Assert.Contains("web_fetch is the harness's own fetch: http and https alone, its redirects followed, text alone (text/*, application/json, application/xml, application/xhtml+xml, application/javascript, application/ecmascript and every +json and +xml type)", prose, StringComparison.Ordinal);
        Assert.Contains("written to the step's web.jsonl before anything leaves the machine", prose, StringComparison.Ordinal);
        Assert.Contains(
            $"Calls that read run together, as many at once as one of every {DssHarness.Core.Codebase.CodebaseGraphService.ReadShare} of the machine's processors, one at the least - the share the graph's own "
                + "reading keeps beside a build - a call that writes or runs alone, every call answered in the order of the calls; an answer longer than a page - "
                + "32,768 characters, its header among them - is kept whole in the step's tool-results/, in a file named by the digest of its call's whole id, "
                + "and given as its first page, whose header says how many pages it holds, how read_result gives the next and where the whole is - nothing of it cut.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains(
            "read_result reads a call's whole result there by the call's id - an answer kept so, and every command's output - a page at a time, "
                + "page 1 where none is asked: each page of whole lines, a line wider than a page given in pieces marked …, and every page of a "
                + "result of more than one page saying how to ask for the next with page, from 1; a page past the last is refused with its range, "
                + "and an id no result is kept for is refused. It reads too a message of the agent's a compaction gave by reference, by the id the "
                + "compaction names ('message-' and its text's digest), from the step's stored stream, and the continuation record the conversation "
                + "began with by 'continuation-record'. Its pages are laid once from the file, kept in the step's graph-pages/ for the "
                + "conversation's life, and laid again, saying so, where the file changed since.",
            prose,
            StringComparison.Ordinal);
        Assert.Contains("A step whose graph is tools - a graphify-codebase step upstream of it, 'help workflows' - is offered the graph tool among these, under read, with the operations its agent's capabilities give, edit among them where it may write, and its context pack in its first message, after the run's map", prose, StringComparison.Ordinal);
        Assert.Contains("its graphReads narrow (the default) denies a whole view_file - one naming no page and no range - of a file the graph parsed over 200 lines, saying the way to the graph instead - a range, a page, a search and a write pass, a file the graph is not sure of is let by as an escape lowConfidence, and the graph's wholeFile lifts the narrowing for one file the graph parsed for the rest of the step, an escape stated with the lines the graph read it as, a path it holds no parsed file at refused as a misuse", prose, StringComparison.Ordinal);
        Assert.Contains("an edit it made is a write, alone in its turn and counted among the files written; a wholeFile is alone in its turn too, for the reads beside it are narrowed by what it lifts, the graph's other reads running with the other reads. The step's view is synced after each write - by the paths it wrote, a directory moved or deleted by every file beneath it - and whole after each command, on every client, and the graph tool answers from what each sync leaves.", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// The README says the tool runtime as the help does - each tool, each bound and default read from the type that holds
    /// it, read_result's pages among them - and the architecture names its owners by the types that are them, the pages a
    /// kept result is read in by the owners that lay and keep them.
    /// </summary>
    [Fact]
    public void TheDocumentsSayTheToolRuntime()
    {
        var readme = Prose.Document("README.md");
        var defaults = DssHarness.Core.Agents.AgentCommandSettings.Default;
        var suffixes = DssHarness.Core.Agents.AgentChildEnvironment.SecretSuffixes.Select(suffix => $"`{suffix}`").ToList();

        Assert.All(DssHarness.Core.Agents.Runtime.AgentTools.All(defaults), tool => Assert.Contains($"`{tool.Name}`", readme, StringComparison.Ordinal));
        var schemes = DssHarness.Core.Agents.Policy.AgentPolicy.SlashedSchemes.Select(scheme => $"`{scheme}`").ToList();
        Assert.Contains(
            $"An address whose slashes after its scheme readers read apart - any scheme but its two slashes and a host after them (`http:/\\host`, `gopher:/host`, `http:///host`), or no slash after an {schemes[0]}, {string.Join(", ", schemes.Skip(1).SkipLast(1))} or {schemes[^1]} scheme (`https:host`) - or whose host is spelled beyond ASCII (its `xn--` form is read as written), names no host the policy can decide, and is denied.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("the run's reporter is given those that read and no other.", readme, StringComparison.Ordinal);
        Assert.Contains(
            "built for the API clients to give their agents in place of a client's own - the `claude-api` and `glm` clients below - and serving the `claude-code` client the web's through the engine's tool server, and its file tools there too "
                + "where Claude Code's own process runs apart from the isolation:",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"its links followed (one whose links cannot be followed refused as {DssHarness.Core.Agents.Policy.AgentPolicy.UndecidedRule}) and held to the agent's roots", readme, StringComparison.Ordinal);
        Assert.Contains($"holds more than {DssHarness.Core.Agents.Runtime.AgentTools.MostDeletedEntries:N0} entries.", readme, StringComparison.Ordinal);
        Assert.Contains(
            "A file of any size is viewed, a page at a time - its pages laid once by streaming it, any page read by seeking to its mark, a line wider "
                + "than a page given in pieces - and edited, copied through a block at a time with the edit made on the way; `search_files` reads every "
                + "file whatever its size, a line at a time, its pattern matched in time linear in each line, so one that needs backtracking - a "
                + "backreference, a lookaround, an atomic group - is refused, naming what it holds. Every file `find_files` matches and every line "
                + "`search_files` matches is given, with as many lines of context as are asked; its `max_results` is refused, naming `page`.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            $"until it ends, or for the time its call names (1 to {DssHarness.Core.Agents.AgentCommandSettings.MostTimeoutSeconds} s, what a command's timer holds), "
                + "else for `agentPolicy.commandTimeoutSeconds` where it is given, stopped sooner where it prints nothing for as long as the step's stall "
                + "bound and by the step's own bounds - its `timeoutMinutes` and its budget; `agentPolicy.maxCommandTimeoutSeconds` is retired and refused, "
                + $"naming what bounds a command; a command line holding a secret, a word holding a NUL, or on Windows a batch file's argument holding what `cmd.exe` reads again (`{DssHarness.Core.Processes.BatchFiles.Said.Replace(", a", "`, a", StringComparison.Ordinal)}) or what the shell expands, is refused.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"(ending {string.Join(", ", suffixes[..^1])} or {suffixes[^1]}) but those `agentPolicy.keepEnvironment` gives back.", readme, StringComparison.Ordinal);
        Assert.Contains(
            "`http` and `https` alone, its redirects followed, text alone (`text/*`, "
                + $"{string.Join(", ", DssHarness.Core.Web.HarnessFetch.TextTypes.Select(type => $"`{type}`"))} and every `+json` and `+xml` type)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains($"written to the step's `{DssHarness.Core.Agents.AgentCallRecords.WebFileName}` before anything leaves the machine", readme, StringComparison.Ordinal);
        Assert.Contains(
            $"Calls that read run together, as many at once as one of every {DssHarness.Core.Codebase.CodebaseGraphService.ReadShare} of the machine's processors, one at the least",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            $"an answer longer than a page - {DssHarness.Core.Pages.PageRoom.ModelAnswer:N0} characters, its header among them - is kept whole in the step's "
                + $"`{DssHarness.Core.Agents.AgentCallRecords.ToolResultsDirectoryName}/`, in a file named by the digest of its call's whole id, and given as its "
                + $"first page, whose header says how many pages it holds, how `{DssHarness.Core.Agents.Runtime.AgentTools.ReadResult}` gives the next and where the whole is - nothing of it cut.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            $"`{DssHarness.Core.Agents.Runtime.AgentTools.ReadResult}` reads a call's whole result there by the call's id - an answer kept so, and every "
                + "command's output - a page at a time, page 1 where none is asked: each page of whole lines, a line wider than a page given in pieces "
                + "marked `…`, and every page of a result of more than one page saying how to ask for the next with `page`, from 1; a page past the last "
                + "is refused with its range, and an id no result is kept for is refused. It reads too a message of the agent's a compaction gave by "
                + $"reference, by the id the compaction names (`{DssHarness.Core.Agents.StoredStream.MessagePrefix}` and its text's digest), from the step's stored "
                + $"stream, and the continuation record the conversation began with by `{DssHarness.Core.Agents.Runtime.KeptResults.ContinuationId}`. Its pages are laid "
                + $"once from the file, kept in the step's `{OrchestratorLayout.StepPagesDirectoryName}/` for the conversation's life, and laid again, saying so, where the file changed since.",
            readme,
            StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains($"`Agents\\Runtime\\{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}`", architecture, StringComparison.Ordinal);
        Assert.Contains($"`Agents\\Runtime\\{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer)}`", architecture, StringComparison.Ordinal);
        Assert.Contains($"`Web\\{nameof(DssHarness.Core.Web.HarnessFetch)}`", architecture, StringComparison.Ordinal);
        Assert.Contains(
            $"recorded by the one `{nameof(DssHarness.Core.Agents.AgentCallRecords)}` (`{DssHarness.Core.Agents.AgentCallRecords.DenialsFileName}`, "
                + $"`{DssHarness.Core.Agents.AgentCallRecords.WebFileName}`, `{DssHarness.Core.Agents.AgentCallRecords.ToolResultsDirectoryName}/`)",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains($"A turn runs its reads together, as many at once as `{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime.MostTogether)}` says - one of every `{nameof(DssHarness.Core.Codebase.CodebaseGraphService)}.{nameof(DssHarness.Core.Codebase.CodebaseGraphService.ReadShare)}` of the machine's processors", architecture, StringComparison.Ordinal);
        Assert.Contains(
            $"A call's whole result kept in `{DssHarness.Core.Agents.AgentCallRecords.ToolResultsDirectoryName}/` "
                + $"(`{nameof(DssHarness.Core.Agents.AgentCallRecords)}.{nameof(DssHarness.Core.Agents.AgentCallRecords.ResultFile)}`) is read back by "
                + $"`{DssHarness.Core.Agents.Runtime.AgentTools.ReadResult}` through `Agents\\Runtime\\{nameof(DssHarness.Core.Agents.Runtime.KeptResults)}`, a page at a time: "
                + $"its pages laid by `Pages\\{nameof(DssHarness.Core.Pages.TextPages)}`",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"by `Pages\\{nameof(DssHarness.Core.Pages.KeptAnswers)}` in the step's `{OrchestratorLayout.StepPagesDirectoryName}/` beside the graph's pages "
                + $"(`{nameof(DssHarness.Core.Pages.PageKey)}.{nameof(DssHarness.Core.Pages.PageKey.OfCall)}`, a key the graph's never is), for the conversation's "
                + $"life; a file that changed since its pages were laid is laid again, the page saying so (`{nameof(DssHarness.Core.Pages.TextPages)}.{nameof(DssHarness.Core.Pages.TextPages.Relaid)}`).",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains($"the names `{nameof(DssHarness.Core.Agents.Policy.ClaudeSettings)}.{nameof(DssHarness.Core.Agents.Policy.ClaudeSettings.Launched)}` sets", architecture, StringComparison.Ordinal);

        foreach (var owner in new[]
        {
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost.Serve)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolHost.Open)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer.Read)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolServer.CallAsync)}",
            $"{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime)}.{nameof(DssHarness.Core.Agents.Runtime.AgentToolRuntime.ServeAsync)}",
            nameof(DssHarness.Core.Agents.Runtime.AgentTools),
            nameof(DssHarness.Core.Schemas.JsonSchemaSubset),
            nameof(DssHarness.Core.Agents.Runtime.ConfinedPaths),
            nameof(DssHarness.Core.Agents.Runtime.AgentFiles),
            nameof(EditedText),
            nameof(EncodedText),
            $"{nameof(DssHarness.Core.Codebase.CodeFileText)}.{nameof(DssHarness.Core.Codebase.CodeFileText.Spans)}",
            nameof(PhaseRunner),
            nameof(DssHarness.Core.Agents.AgentCommandSettings),
            nameof(DssHarness.Core.Web.WebAddresses),
            nameof(DssHarness.Core.Web.WebPageText),
            nameof(DssHarness.Core.Agents.AgentCallRecords),
            $"{nameof(DssHarness.Core.Agents.AgentChildEnvironment)}.{nameof(DssHarness.Core.Agents.AgentChildEnvironment.Own)}",
            nameof(DssHarness.Core.Web.SystemWebNames),
            $"{nameof(DssHarness.Core.Web.HarnessFetch)}.{nameof(DssHarness.Core.Web.HarnessFetch.Connecting)}",
            $"{nameof(IFilePermissions)}.{nameof(IFilePermissions.CopyMode)}",
        })
        {
            Assert.Contains($"`{owner}`", architecture, StringComparison.Ordinal);
        }
    }
}
