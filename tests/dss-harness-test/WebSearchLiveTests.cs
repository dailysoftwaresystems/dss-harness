using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Web.Index;
using DssHarness.Core.Web.Search;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// The live check of the web search's sources (slice 22b3): each built-in source, and the two of part 03's worked example,
/// asked one real query through the tool runtime's <c>web_search</c> - the harness's own search and fetch, over the
/// network - its answer read, and its outcome in the step's <c>web.jsonl</c>, kept under the directory the person names.
/// </summary>
/// <remarks>
/// Person-run alone: skipped unless <see cref="RecordsVariable"/> names where its records go (under <c>.temp\</c>), never in
/// continuous integration, for it asks the network and, for the GitHub sources, signs in with the machine's <c>gh</c>
/// login. A sitemap source is checked with a small <c>maxPages</c>, so its first index is read in minutes: its sitemap,
/// its <c>robots.txt</c> and its pages are what is checked, and the entry's other keys are as built in. A source whose
/// live answer no longer reads is fixed with its entry, or its kind's version raised.
/// </remarks>
public sealed class WebSearchLiveTests
{
    /// <summary>The variable naming the directory the check's records go to; it runs only where it is set.</summary>
    public const string RecordsVariable = "DSSHARNESS_TESTS_LIVE_WEB_SEARCH";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Each source checked, with the query it is asked.</summary>
    public static TheoryData<string, string> Sources() => new()
    {
        { "stackoverflow", "center a div with flexbox" },
        { "github-repos", "dotnet global tool" },
        { "github-issues", "System.Text.Json source generator" },
        { "nuget", "Newtonsoft.Json" },
        { "npm", "left-pad" },
        { "pub-dev", "http" },
        { "learn", "dotnet tool install" },
        { "mdn", "flexbox" },
        { "wikipedia", "Model Context Protocol" },
        { "hackernews", "rust async" },
        { "readthedocs", "project:requests session" },
        { "federal-register", "accessibility" },
        { "digital-gov", "accessibility" },
    };

    /// <summary>
    /// A source answers a real query: its part of the answer read as results - a sitemap source's once its first index
    /// holds pages - and its outcome in the step's <c>web.jsonl</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public async Task ASource_AnswersARealQuery(string source, string query)
    {
        var records = Environment.GetEnvironmentVariable(RecordsVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(records), $"{RecordsVariable} is not set: the live check of the web search's sources runs only where the person said it might.");

        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider();

        var main = Directory.CreateDirectory(Path.Combine(records!, "main")).FullName;
        var step = Path.Combine(records!, "steps", source);

        if (Directory.Exists(step))
        {
            Directory.Delete(step, recursive: true);
        }

        var sources = WebSearchSources.Of(new HarnessConfig { AgentWebSearch = WorkedExample() }, provider.GetRequiredService<WebSearchKinds>());
        Assert.Empty(sources.Problems);

        var layout = new HarnessLayout(main, main);
        var keys = await provider.GetRequiredService<WebSearchKeys>().ReadAsync(layout, [], sources.Chosen([source]), Token);
        var secret = keys.Values.Select(key => key.Value).OfType<string>().FirstOrDefault();
        var run = AgentRuns.Of(
            step,
            main,
            new Masking(secret),
            PolicyGrounds.Of(main, [main], [main], [], [], [], capabilities: [AgentCapability.Web]),
            webSearch: AgentWebSearch.Of(sources, [source], keys));
        var events = new RecordedEvents();
        var arguments = new JsonObject { ["query"] = query, ["sources"] = new JsonArray(source) }.ToJsonString();
        AgentToolCallResult answer;

        // The runtime closes the step's records as it is closed: they are read once it is.
        using (var runtime = provider.GetRequiredService<AgentToolHost>().Open(run, events))
        {
            answer = await runtime.RunAsync(new AgentToolCallRequest("live-1", AgentTools.WebSearch, arguments), Token);

            if (sources.Find(source)!.Kind == "sitemap")
            {
                // Its first search began its index: once the fill has read what it reads, the same query is asked again.
                await provider.GetRequiredService<WebIndexes>().FilledAsync(OrchestratorLayout.WebIndexDirectory(layout, source)).WaitAsync(TimeSpan.FromMinutes(20), Token);
                answer = await runtime.RunAsync(new AgentToolCallRequest("live-2", AgentTools.WebSearch, arguments), Token);
            }
        }

        File.WriteAllText(Path.Combine(step, "answer.txt"), answer.Text);
        TestContext.Current.SendDiagnosticMessage($"{source}: {(answer.Ok ? "ok" : "failed")}, {answer.Text.Length} characters");

        Assert.True(answer.Ok, answer.Text);

        var searched = File.ReadAllLines(Path.Combine(step, AgentCallRecords.WebFileName))
            .Select(line => JsonNode.Parse(line)!)
            .Last(line => line["line"]?.GetValue<string>() == "searched");
        Assert.True(searched["outcome"]?.GetValue<string>() == nameof(WebSearchOutcome.Ok), searched.ToJsonString());
    }

    /// <summary>
    /// Part 03's worked example - the Federal Register's JSON API and digital.gov's guidance by its sitemap - beside the
    /// built-ins, each sitemap source's <c>maxPages</c> small enough that its first index is read in minutes.
    /// </summary>
    private static AgentWebSearchConfig WorkedExample()
        => new()
        {
            Sources = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["federal-register"] = Element("""
                    {
                      "kind": "json-api",
                      "tags": ["gov", "regulations"],
                      "description": "US federal rules, proposed rules and notices, from the Federal Register",
                      "url": "https://www.federalregister.gov/api/v1/documents.json?conditions[term]={query}&per_page={limit}&order=relevance",
                      "items": "$.results",
                      "title": "{@.title}",
                      "link": "{@.html_url}",
                      "snippet": "{@.abstract}",
                      "total": "$.count",
                      "perMinute": 20
                    }
                    """),
                ["digital-gov"] = Element("""
                    {
                      "kind": "sitemap",
                      "tags": ["gov", "docs"],
                      "description": "digital.gov: the US government's guidance for digital services",
                      "site": "https://digital.gov",
                      "sitemap": "https://digital.gov/sitemap.xml",
                      "include": ["/guides/**", "/resources/**", "/topics/**"],
                      "maxPages": 12,
                      "refreshDays": 14
                    }
                    """),
                ["mdn"] = Element("""{ "maxPages": 30 }"""),
            },
        };

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
