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
    private static string RenderWebSearch()
    {
        var builder = new StringBuilder();
        var kinds = Web.Search.WebSearchKinds.OfThisBuild;
        var builtIn = Web.Search.WebSearchSources.Of(new HarnessConfig(), kinds);

        builder.AppendLine("Web search");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"An agent whose step has the web searches with {AgentTools.WebSearch}, and reads the pages it chooses with {AgentTools.WebFetch}. "
            + "The claude-code client gives them a step through the engine's tool server ('help agent-clients'), and the API clients are to offer them. "
            + "dssharness makes every search itself, inside the tool, over HTTP: it asks the sources the configuration declares - "
            + "search APIs that answer JSON, RSS and Atom feeds, OpenSearch descriptions, and its own index of a site's pages read "
            + "through the site's sitemap - with no key required, no provider's search and no service to install, and it never "
            + "reads a search engine's results page, for none of the general engines lets programs read one in so many words. A "
            + "source answers data, never a page for people: an API its owner documents for programs, or, documenting none, one "
            + "whose robots.txt does not refuse it.");
        builder.AppendLine();
        AppendWrapped(builder, "The section agentWebSearch of config.json declares it, read strictly: a key no part of it takes, and a key a source's kind does not take, are refused, each by name.");
        builder.AppendLine();
        AppendKeys(builder, Web.Search.WebSearchSources.Keys);
        builder.AppendLine();
        AppendWrapped(builder, "Every source takes these keys, whatever its kind:");
        builder.AppendLine();
        AppendKeys(builder, Web.Search.WebSearchSource.OwnKeys);

        foreach (var kind in kinds.All)
        {
            builder.AppendLine();
            AppendWrapped(builder, $"Kind {kind.Kind} (version {kind.Version.ToString(CultureInfo.InvariantCulture)}): {kind.Help}");
            builder.AppendLine();
            AppendKeys(builder, kind.Keys);
        }

        builder.AppendLine();
        AppendWrapped(
            builder,
            "The built-in sources are entries in this same form, kept as a data file of the tool, which init writes into the "
            + "configuration it creates, and which a configuration without agentWebSearch has: a repository's entry of a built-in's "
            + "name changes it key by key, enabled: false turns it off, and an entry of a new name adds a source. None needs a key. "
            + $"A step that says no search asks the defaults: {string.Join(", ", builtIn.Defaults)}.");

        foreach (var source in builtIn.All)
        {
            builder.AppendLine();
            AppendWrapped(builder, $"{source.Name} ({source.Kind}{(source.Tags.Count > 0 ? $"; tags {string.Join(", ", source.Tags)}" : string.Empty)}; {(source.PerMinute is { } perMinute ? $"perMinute {perMinute.ToString(CultureInfo.InvariantCulture)}" : "one request at a time")}{(source.PerDay is { } perDay ? $", perDay {perDay.ToString(CultureInfo.InvariantCulture)}" : string.Empty)}): {source.Description}. Its owner's terms: {source.Terms}.");
        }

        builder.AppendLine();
        AppendWrapped(
            builder,
            "A reason step chooses its sources with search - names of sources or tags, as search: [docs, gov] - in the order its "
            + "answer gives them; absent, the defaults. The tool lists the step's sources with what each is for, and the agent may "
            + "name some of them in a call's sources, else every one is asked. A name that is no source or tag, a list given empty "
            + "or naming one twice, a source turned off, a tag none of whose sources is on, and search on a step that goes on with "
            + "another's work are refused when the workflow is checked. The defaults leave out a source turned off, and refuse a "
            + "name that is no source or tag, a list given empty and one naming one twice where the configuration is read.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Every query goes through dssharness. The query is held to the mask: a secret of the run's never leaves in one. Each "
            + "address a source asks for the step - its endpoint, a lookup it reads before its first request, each redirect, and "
            + "the site of a sitemap source's index - is held to the mask, a secret behind its escapes too, as a page an agent "
            + "fetches is, and to the step's Web(...) rules, and each one asked decided and written "
            + $"to the step's {Agents.AgentCallRecords.WebFileName} before it is asked; agentPolicy.webDomains does not hold back a source, which the configuration "
            + "declares, and holds its results: one whose address the step could not read is left out and counted. An answer that "
            + "holds a secret of the run's behind its escapes - its key echoed as a page writes it - is not read. Each request "
            + "a source is sent counts against the step's maxPerStep, past which a source is refused and told; an address its "
            + "rules refuse, and a turn its pace refuses, is no request. A page an index's fill reads is held to the run's "
            + "Web(...) rules, the configuration's, never one step's own, for the index is the repository's and outlives the step "
            + "that began it: the fill writes each page it asks to its own record, and counts against no step. No source is "
            + "asked more than its perMinute in a minute, nor its perDay in a day, on this machine, whatever runs ask it - one "
            + $"record of the machine's, {Web.Search.WebSearchPace.FilePrefix}-<machine>.json in this user's directory for the "
            + "tool, beside admission's - a request past its minute waiting for it; a source that names no perMinute is asked one "
            + "request at a time, the next once the last is answered, whichever process asks it. A source's key, where it names one - a "
            + "variable of the .env slot, sent to the scheme, host and port the source's own address writes alone, or the "
            + "machine's gh login for the source's host, sent to that host or its api. host alone, its token read once in an engine's "
            + "session, read again before the expiry the host tells of it passes, and read again once where the host refuses a "
            + "request as unsigned (401), that request then sent once more with the token read where it is another, never a third "
            + "time; a reading that gives no token - gh signed out, or not answering in time - is held nowhere, so the next step "
            + "reads gh again, and never takes the place of a token held, and a step that began without one is sent the one the "
            + "session comes to hold - is used where it is there, "
            + "and the fetch adds it to what it sends to the scheme, host and port each of that source's searches first asks "
            + "alone, where it is one of those - a lookup is sent none, and a host a document the source read names, an "
            + "OpenSearch description's template or what discover found, is never sent one - as a header or a query parameter, "
            + "over https alone - an address it may not go to refused, nothing sent - never to an address any record or answer "
            + "holds; missing, the source is asked without it, and the record says why. A source whose address takes its host "
            + "from what discover finds is refused a key of the .env slot where the configuration is read. One source's "
            + "failure - what this machine could not do for it among them - is its own part's, never another's.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "A limit is obeyed and told, never looped on: a 429, or a 403 or 503 with Retry-After or spent requests, a RateLimit "
            + "or X-RateLimit-Remaining header at zero, or a body's backoff, holds that source on the machine until the time it "
            + "gave, as given, and nothing is sent to it before then; one that says not for how long holds it for the source's "
            + "own pace between two requests, doubled each time it asks so again. Each source answers in its own part, in the "
            + "step's order: every result of the page it answered - each one's title, address and snippet, whole; or no "
            + "results; or a limit it asked for; or an answer that could not be read (it changed), which is a notice too; or a "
            + "failure, the source's own words whole; or a refusal, each with its cause - inside a <web-search> block that says "
            + "it is from the web. A later page of each source's results is asked by web_search's page, from 1, each a request "
            + "counted against maxPerStep; a source whose address names no {page} or {start} answers one page, and says so for a "
            + "later one. One source's failure never "
            + "hides another's results, and a search none of whose sources answered fails. Each request carries the tool's own "
            + "User-Agent, naming it, its version and its project's address: no address rotated, no browser imitated, no script "
            + "run and no challenge answered.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"What a run's sources found once, and each page of a query a source answered, is kept in {OrchestratorLayout.WebDirectoryName}/{OrchestratorLayout.WebSearchesFileName} "
            + "in the run's directory: the same page of the same query again, of the same source, its key sent or not alike, is answered from "
            + "there with no request, under "
            + "the asking step's own rules - each address the source was asked at decided again, and each result held to its "
            + "web. A "
            + "sitemap source's index is the repository's, under "
            + $"{HarnessLayout.OrchestratorsDirectoryName}/{OrchestratorLayout.WebIndexesDirectoryName}/<source>/, filled in the background on its first "
            + "search and whenever it is older than refreshDays or its settings change, at the slower of its site's Crawl-delay "
            + "and the source's pace, every page its robots.txt allows and its include names read whole and indexed as it is "
            + "read; a page that could not be read is recorded and the fill goes on, and a fill that stopped short is begun "
            + "again by the next search. A search answers from what it holds, a page of hits at a time, saying how many pages "
            + "it holds of how many its sitemaps list.");

        return builder.ToString();
    }
}
