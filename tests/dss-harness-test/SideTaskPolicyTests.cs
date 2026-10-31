using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.SideTasks;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Web.Search;

namespace DssHarness.Tests;

/// <summary>
/// The side-task's policy: everything a person's own session could do over the main tree, every limit of a run's
/// dropped - and each thing that stays, kept. Each limit it drops is pinned by the same call a step's policy refuses
/// being allowed here; each thing it keeps, by its still being refused.
/// </summary>
public sealed class SideTaskPolicyTests
{
    /// <summary>The side-task's policy over a tree of its own, with the policy the tool builds for it.</summary>
    private static (AgentPolicy Policy, string Main) Policy(PlatformId platform = PlatformId.Linux, IReadOnlyList<AgentRestriction>? restrictions = null, IReadOnlyList<string>? webDomains = null)
    {
        var main = new TempDirectory();
        var claude = new TempDirectory();
        var harness = Path.Combine(main.Path, ".harness-config");

        Directory.CreateDirectory(Path.Combine(harness, "workflows", ".env"));

        var config = new HarnessConfig
        {
            Worktrees = new WorktreeSettings(),
            AgentPolicy = new AgentPolicyConfig
            {
                Restrictions = [.. (restrictions ?? []).Select(one => one.Text)],
                WebDomains = [.. (webDomains ?? [])],
            },
        };
        var layout = new HarnessLayout(main.Path, main.Path);
        var ground = SideTaskPolicy.Ground(layout, config, [claude.Path], Path.Combine(harness, "workflows", ".env"));

        return (new AgentPolicy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), HostDoubles.Platform(platform), ground), main.Path);
    }

    /// <summary>The same call over a step's ground - a reason step's own shape, restricted as a test says.</summary>
    private static AgentPolicy StepPolicy(PlatformId platform = PlatformId.Linux, IReadOnlyList<string>? restrictions = null, IReadOnlyList<string>? webDomains = null, string? worktree = null, string? main = null, string? work = null, string? slot = null, string? claude = null)
    {
        var tree = worktree ?? new TempDirectory().Path;
        var step = new WorkflowStep { Name = "s", Type = "reason", Restrictions = restrictions ?? [] };
        var workspace = new AgentWorkspace(
            worktree is null ? null : tree,
            work ?? Path.Combine(tree, "work"),
            main ?? tree,
            [],
            worktree is null ? main ?? tree : tree,
            Path.Combine(main ?? tree, ".orchestrators", "r1"));

        var config = new HarnessConfig { Worktrees = new WorktreeSettings(), AgentPolicy = new AgentPolicyConfig { Restrictions = [], WebDomains = [.. (webDomains ?? [])] } };

        return new AgentPolicy(
            new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())),
            HostDoubles.Platform(platform),
            AgentStepGround.Of(config, null, step, workspace, webSearchGiven: webDomains is null, [claude ?? new TempDirectory().Path], slot ?? Path.Combine(main ?? tree, ".harness-config", "workflows", ".env")));
    }

    /// <summary>
    /// Every restriction list is dropped: the same <c>Run</c> rule that refuses a step's agent - the configuration's,
    /// a definition's, a step's - allows the side-task's, for the main tree is its own to work over as the person's is.
    /// </summary>
    [Fact]
    public void EveryRestrictionListIsDropped_TheSameCallAStepIsRefused_IsAllowedHere()
    {
        using var main = new TempDirectory();
        var rule = new AgentRestriction("Run(git push *)", AgentPolicySource.Config);
        var (side, _) = Policy(restrictions: [rule]);
        var step = StepPolicy(restrictions: ["Run(git push *)"], main: main.Path, worktree: Path.Combine(main.Path, "w"));

        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "push"]));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "commit", "-am", "wip"]));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["gh", "pr", "create"]));

        Assert.NotEqual(AgentPolicyAnswer.Allow, step.Run((IReadOnlyList<string>)["git", "push"]));
    }

    /// <summary>
    /// The tool's own verbs, refused to every step's agent whatever the worktree rules say, are open to the side-task's
    /// as the person's own session's are - it can edit the configuration, commit, branch, and run the tool itself.
    /// </summary>
    [Fact]
    public void TheToolsOwnVerbsRefusedToAStep_AreOpenToASideTask()
    {
        var (side, main) = Policy();
        var step = StepPolicy(main: main);

        foreach (var verb in AgentPolicy.ToolVerbs)
        {
            Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["dssharness", verb, "x"]));
            Assert.Equal(AgentPolicyAnswer.Allow, side.Run($"dssharness {verb} x"));
        }

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(step.Run((IReadOnlyList<string>)["dssharness", AgentPolicy.ToolVerbs[0], "x"]));

        Assert.Equal("the orchestrator and agent verbs", denied.Rule);
    }

    /// <summary>
    /// <c>run-side-task</c> is refused to every agent alike - a step's, and a side-task's own: an agent does not start
    /// another, whatever leaves the person's sight.
    /// </summary>
    [Fact]
    public void RunSideTaskItselfIsRefusedToEveryAgent_ASideTasksOwnIncluded()
    {
        var (side, _) = Policy();

        foreach (var command in new[]
        {
            (IReadOnlyList<string>)["run-side-task", "--prompt", "x"],
            ["dssharness", "run-side-task"],
            ["dssharness", "run-side-task", "--prompt", "again"],
        })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run(command));

            Assert.Equal("run-side-task", denied.Rule);
            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
        }
    }

    /// <summary>The worktree rules and the protected paths are off: the files they keep a step from, the side-task writes.</summary>
    [Fact]
    public void TheWorktreeRulesAndProtectedPathsAreOff_TheFilesTheyGuardAStepFrom_TheSideTaskWrites()
    {
        var (side, main) = Policy();
        var step = StepPolicy(main: main, worktree: System.IO.Path.Combine(main, "w"));

        Assert.Equal(AgentPolicyAnswer.Allow, side.Write(".harness-config/config.json"));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Write(".vscode/settings.json"));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Write(".claude/settings.json"));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Write("package.json"));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Write("src/Program.cs"));

        Assert.NotEqual(AgentPolicyAnswer.Allow, step.Write(".harness-config/config.json"));
        Assert.NotEqual(AgentPolicyAnswer.Allow, step.Write(".vscode/settings.json"));
    }

    /// <summary>
    /// Git's own files stay refused, whatever the protected paths say: they are written by git's verbs and this tool's,
    /// never by an agent's file tool.
    /// </summary>
    [Fact]
    public void GitsOwnFilesStayRefused()
    {
        var (side, _) = Policy();

        foreach (var path in new[] { ".git/config", ".git/HEAD", "sub/.git/objects/x" })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(side.Write(path));

            Assert.Equal(AgentPolicy.GitsOwnRule, denied.Rule);
            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
        }
    }

    /// <summary>
    /// The tool's own state is written only by its verbs: the records a run and a side-task keep are no agent's to
    /// write, the side-task's own records among them.
    /// </summary>
    [Fact]
    public void TheToolsOwnStateIsWrittenOnlyByItsVerbs_TheSideTasksOwnRecordsAmongIt()
    {
        var (side, main) = Policy();

        foreach (var path in new[]
        {
            ".orchestrators/r1/flow.json",
            ".side-tasks/2026-10-19-1432-add-a-source/task.json",
            ".side-tasks/2026-10-19-1432-add-a-source/stream.jsonl",
            ".mutations/m1/file.txt",
            ".legs/l1/file.txt",
        })
        {
            Assert.NotEqual(AgentPolicyAnswer.Allow, side.Write(path));
        }
    }

    /// <summary>The credential slot and the Claude Code directories stay no agent's to read or name.</summary>
    [Fact]
    public void TheCredentialSlotAndClaudeDirectoriesStayNoAgentsToReadOrName()
    {
        var (side, main) = Policy();

        Assert.NotEqual(AgentPolicyAnswer.Allow, side.Read(".harness-config/workflows/.env/a.env"));
        Assert.NotEqual(AgentPolicyAnswer.Allow, side.Write(".harness-config/workflows/.env/a.env"));
        Assert.NotEqual(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["cat", ".harness-config/workflows/.env/a.env"]));

        var printing = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run((IReadOnlyList<string>)["gh", "auth", "token"]));

        Assert.Equal(AgentPolicySource.Builtin, printing.Source);
        Assert.Contains("prints a credential", printing.Reason, StringComparison.Ordinal);
    }

    /// <summary>The root actions stay refused to the side-task's agent as to every agent's, wherever they stand.</summary>
    [Fact]
    public void TheRootActionsStayRefused()
    {
        var (side, _) = Policy();

        foreach (var command in new[]
        {
            (IReadOnlyList<string>)["sudo", "apt", "install", "x"],
            ["su", "root"],
            ["sh", "-c", "sudo x"],
        })
        {
            Assert.Equal("the root actions", Assert.IsType<AgentPolicyAnswer.Denied>(side.Run(command)).Rule);
        }
    }

    /// <summary>
    /// The web is the whole of it: no allow list of pages holds the side-task's agent - a page of any host is read -
    /// and the web search it is given holds every configured source with no bound of a run's on it.
    /// </summary>
    [Fact]
    public void TheWebIsTheWholeOfIt_AnyAddress_EverySource_NoBound()
    {
        var (side, _) = Policy();
        var step = StepPolicy(webDomains: ["example.com"]);

        Assert.Equal(AgentPolicyAnswer.Allow, side.Web("https://anywhere.example.org/page"));
        Assert.NotEqual(AgentPolicyAnswer.Allow, step.Web("https://anywhere.example.org/page"));

        var search = SideTaskPolicy.WebSearch(
            WebSearchSourceTests.Sources(configured: 3),
            new Dictionary<string, WebSearchKey>());

        Assert.Subset(
            search.Sources.Select(source => source.Name).ToHashSet(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "feed0", "feed1", "feed2" });
        Assert.Equal(int.MaxValue, search.MaxPerStep);
    }
}

/// <summary>The web-search sources a policy test builds: the configuration's own, as many as a test says.</summary>
file static class WebSearchSourceTests
{
    /// <summary>Sources resolved from a configuration naming <paramref name="configured"/> feeds, over the built-in template's.</summary>
    public static WebSearchSources Sources(int configured)
    {
        var entries = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);

        for (var at = 0; at < configured; at++)
        {
            entries[$"feed{at}"] = System.Text.Json.JsonSerializer.SerializeToElement(new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "feed",
                ["address"] = $"https://example.org/{at}/feed.xml",
            });
        }

        return WebSearchSources.Of(
            new HarnessConfig { AgentWebSearch = new AgentWebSearchConfig { Sources = entries } },
            WebSearchKinds.OfThisBuild);
    }
}
