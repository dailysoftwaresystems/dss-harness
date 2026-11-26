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
    /// The web-search topic, under each of its names, says every key of agentWebSearch and of every source, every kind this
    /// build holds with its version, help and keys, and every built-in source with its tags, pace, description and terms -
    /// each read from the code - and where a run's searches are kept; the config topic names the section.
    /// </summary>
    [Fact]
    public void TheWebSearchTopic_SaysEveryKindKeyAndBuiltInSource_FromTheCode()
    {
        var topic = HelpText.Of("web-search").StandardOutput;
        var prose = Prose.Words(topic);

        Assert.Equal(topic, HelpText.Of("search").StandardOutput);
        Assert.Equal(topic, HelpText.Of("web_search").StandardOutput);
        Assert.Equal(topic, HelpText.Of("agentwebsearch").StandardOutput);
        Assert.Contains(HelpTopics.Described, described => described.Name == "web-search");
        Assert.Contains("The claude-code client gives them a step through the engine's tool server ('help agent-clients'), and the API clients are to offer them.", prose, StringComparison.Ordinal);

        // The files the web's tools leave are written by each client whose step has them ('help orchestrators').
        var files = Prose.Words(HelpText.Of("orchestrators").StandardOutput);
        Assert.Contains("each search source's part ('help web-search'); written by every client whose step has the web", files, StringComparison.Ordinal);
        Assert.Contains("which the same query again is answered from; written by every client whose step searches", files, StringComparison.Ordinal);

        foreach (var key in DssHarness.Core.Web.Search.WebSearchSources.Keys.Concat(DssHarness.Core.Web.Search.WebSearchSource.OwnKeys))
        {
            Assert.Contains($"  {key.Name}", topic, StringComparison.Ordinal);
        }

        foreach (var kind in DssHarness.Core.Web.Search.WebSearchKinds.OfThisBuild.All)
        {
            Assert.Contains(Prose.Words($"Kind {kind.Kind} (version {kind.Version.ToString(CultureInfo.InvariantCulture)}): {kind.Help}"), prose, StringComparison.Ordinal);
            Assert.All(kind.Keys, key => Assert.Contains($"  {key.Name}", topic, StringComparison.Ordinal));
        }

        var builtIn = DssHarness.Core.Web.Search.WebSearchSources.Of(new HarnessConfig(), DssHarness.Core.Web.Search.WebSearchKinds.OfThisBuild);

        foreach (var source in builtIn.All)
        {
            Assert.Contains(Prose.Words($"{source.Name} ({source.Kind}; tags {string.Join(", ", source.Tags)}; {(source.PerMinute is { } perMinute ? $"perMinute {perMinute.ToString(CultureInfo.InvariantCulture)}" : "one request at a time")}"), prose, StringComparison.Ordinal);
            Assert.Contains(Prose.Words($"{source.Description}. Its owner's terms: {source.Terms}."), prose, StringComparison.Ordinal);
        }

        Assert.Contains(Prose.Words($"A step that says no search asks the defaults: {string.Join(", ", builtIn.Defaults)}."), prose, StringComparison.Ordinal);
        Assert.Contains($"{OrchestratorLayout.WebDirectoryName}/{OrchestratorLayout.WebSearchesFileName}", prose, StringComparison.Ordinal);
        Assert.Contains($"{DssHarness.Core.Web.Search.WebSearchPace.FilePrefix}-<machine>.json", prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("every result of the page it answered - each one's title, address and snippet, whole"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("A later page of each source's results is asked by web_search's page, from 1, each a request counted against maxPerStep"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a source that names no perMinute is asked one request at a time, the next once the last is answered"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("one that says not for how long holds it for the source's own pace between two requests, doubled each time it asks so again"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a page that could not be read is recorded and the fill goes on, and a fill that stopped short is begun again by the next search"), prose, StringComparison.Ordinal);

        // A source's key goes where its configuration writes alone, never to a host a document it read names.
        Assert.Contains(Prose.Words("a variable of the .env slot, sent to the scheme, host and port the source's own address writes alone, or the machine's gh login"), prose, StringComparison.Ordinal);

        // The gh login is read once a session, again before its expiry, and once more where the host refuses it.
        Assert.Contains(Prose.Words("its token read once in an engine's session, read again before the expiry the host tells of it passes, and read again once where the host refuses a request as unsigned (401), that request then sent once more with the token read where it is another, never a third time"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("a host a document the source read names, an OpenSearch description's template or what discover found, is never sent one"), prose, StringComparison.Ordinal);
        Assert.Contains(Prose.Words("A source whose address takes its host from what discover finds is refused a key of the .env slot where the configuration is read."), prose, StringComparison.Ordinal);

        // The README names every built-in source, as the template holds them.
        var named = DssHarness.Core.Web.Search.WebSearchSources.BuiltIn.Sources!.Keys.ToList();

        Assert.Contains(
            Prose.Words($"The built-in sources ({string.Join(", ", named.SkipLast(1).Select(name => $"`{name}`"))}, `{named[^1]}`)"),
            Prose.Document("README.md"),
            StringComparison.Ordinal);

        // The orchestrators topic lists where a run's and a step's web records are.
        var layout = HelpText.Of("orchestrators").StandardOutput;

        foreach (var file in new[] { $"<step>/{DssHarness.Core.Agents.AgentCallRecords.WebFileName}", $"<step>/{DssHarness.Core.Agents.AgentCallRecords.DenialsFileName}", $"<step>/{DssHarness.Core.Agents.AgentCallRecords.ToolResultsDirectoryName}/{DssHarness.Core.Pages.PageKey.CallPrefix}<digest>.txt", $"{OrchestratorLayout.WebDirectoryName}/{OrchestratorLayout.WebSearchesFileName}", $"../{OrchestratorLayout.WebIndexesDirectoryName}/<source>/" })
        {
            Assert.Contains($"  {file}", layout, StringComparison.Ordinal);
        }

        var config = Prose.Words(HelpText.Of("config").StandardOutput);

        Assert.Contains("agentWebSearch the harness's own web search", config, StringComparison.Ordinal);
        Assert.Contains(Prose.Words($"and maxPerStep ({DssHarness.Core.Web.Search.WebSearchSources.DefaultMaxPerStep}), the requests a step makes, a later page of a source's results among them ('help web-search')"), config, StringComparison.Ordinal);
    }
}
