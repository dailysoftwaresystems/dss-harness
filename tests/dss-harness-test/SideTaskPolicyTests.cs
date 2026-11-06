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
/// being allowed here; each thing it keeps, by its still being refused, by its rule and its whole reason.
/// </summary>
public sealed class SideTaskPolicyTests
{
    /// <summary>The reason every refusal of <c>run-side-task</c> gives.</summary>
    private static readonly string SideTaskReason = $"'run-side-task' is refused: {SideTaskService.NoAgentStartsOne}";

    /// <summary>The side-task's policy over a tree of its own, with the policy the tool builds for it, and its Claude Code directory.</summary>
    private static (AgentPolicy Policy, string Main, string Claude) Policy(PlatformId platform = PlatformId.Linux, IReadOnlyList<AgentRestriction>? restrictions = null, IReadOnlyList<string>? webDomains = null, Func<string?>? liveRun = null)
    {
        var main = new TempDirectory();
        var claude = new TempDirectory();
        var harness = Path.Combine(main.Path, ".harness-config");

        foreach (var slot in HarnessLayout.SecretSlots)
        {
            Directory.CreateDirectory(Path.Combine(main.Path, slot));
        }

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
        var ground = SideTaskPolicy.Ground(layout, config, [claude.Path], [], liveRun);

        return (new AgentPolicy(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())), HostDoubles.Platform(platform), ground), main.Path, claude.Path);
    }

    /// <summary>The same call over a step's ground - a reason step's own shape, restricted as a test says.</summary>
    private static AgentPolicy StepPolicy(PlatformId platform = PlatformId.Linux, IReadOnlyList<string>? restrictions = null, IReadOnlyList<string>? webDomains = null, string? worktree = null, string? main = null, string? work = null, string? claude = null)
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
            AgentStepGround.Of(config, null, step, workspace, webSearchGiven: webDomains is null, [claude ?? new TempDirectory().Path], []));
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
        var (side, _, _) = Policy(restrictions: [rule]);
        var step = StepPolicy(restrictions: ["Run(git push *)"], main: main.Path, worktree: Path.Combine(main.Path, "w"));

        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "push"]));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "commit", "-am", "wip"]));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["gh", "pr", "create"]));

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(step.Run((IReadOnlyList<string>)["git", "push"]));

        Assert.Equal("Run(git push *)", denied.Rule);
        Assert.Equal(AgentPolicySource.Agent, denied.Source);
    }

    /// <summary>
    /// The tool's verbs that read a credential slot or write its own state are refused to the side-task's agent as to a
    /// step's - inside the operating system's isolation each would fail part way - the step's own verbs among them; the
    /// verbs that only read, or write only the tree's own files, stay open to it as to the person's own session.
    /// </summary>
    [Fact]
    public void TheToolsVerbsThatReadASlotOrWriteState_AreRefusedToASideTask_TheRestOpen()
    {
        var (side, main, _) = Policy();
        var step = StepPolicy(main: main);

        Assert.Equal([], AgentPolicy.ToolVerbs.Except(AgentPolicy.SlotOrStateVerbs));

        foreach (var verb in AgentPolicy.SlotOrStateVerbs)
        {
            var refused = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run((IReadOnlyList<string>)["dssharness", verb, "x"]));

            Assert.Equal(
                ("the verbs that read a slot or write state", $"'dssharness {verb}' is refused: it reads a credential slot or writes this tool's own state or install, which no command of an agent may - and where the agent's commands run inside the operating system's isolation, every slot is hidden there and the state read-only - so a person runs it"),
                (refused.Rule, refused.Reason));
            Assert.Equal("the verbs that read a slot or write state", Assert.IsType<AgentPolicyAnswer.Denied>(side.Run($"dssharness {verb} x")).Rule);
        }

        foreach (var open in AgentPolicy.OpenVerbs)
        {
            Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["dssharness", open, "x"]));
        }

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(step.Run((IReadOnlyList<string>)["dssharness", AgentPolicy.ToolVerbs[0], "x"]));

        Assert.Equal("the orchestrator and agent verbs", denied.Rule);
    }

    /// <summary>
    /// While a run's owner is live over the tree, the side-task's agent is refused the git verbs that move HEAD, the
    /// index or the stash, the run's name in the refusal and git's own options before the verb seen through - and the
    /// git that moves nothing stays open beside them. It is checked at each call: a run that starts while the chat is
    /// open is held to from its first step, and one that ends releases it.
    /// </summary>
    [Fact]
    public void AGitVerbThatMovesHead_IsRefusedWhileARunsOwnerIsLive_WithTheRunsName_AndOpenWhereNoneIs()
    {
        const string run = "impl-2026-10-19";
        var live = false;
        var (side, _, _) = Policy(liveRun: () => live ? run : null);

        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "commit", "-am", "wip"]));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "status"]));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "log", "-1"]));

        live = true;

        foreach (var verb in AgentPolicy.HeadMovingGitVerbs)
        {
            var refused = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run((IReadOnlyList<string>)["git", verb]));

            Assert.Equal(
                ("a run working over the tree", $"run '{run}' is working over this tree and moves HEAD itself; ask again when it ends, or pause it yourself"),
                (refused.Rule, refused.Reason));
        }

        Assert.Equal("a run working over the tree", Assert.IsType<AgentPolicyAnswer.Denied>(side.Run((IReadOnlyList<string>)["git", "-C", ".", "stash"])).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "-C", ".", "status"]));

        live = false;

        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "commit", "-am", "wip"]));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run((IReadOnlyList<string>)["git", "stash"]));
    }

    /// <summary>
    /// Every verb the tool lists is decided by name: refused as one that reads a credential slot or writes the tool's state
    /// or install, open as one that only reads or writes only the tree's own files, or <c>run-side-task</c>, refused
    /// apart - and no name in either list is one the tool lacks but its hidden steps, which nobody types.
    /// </summary>
    [Fact]
    public async Task EveryVerbTheToolLists_IsRefusedOrOpen_ByName()
    {
        var help = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);
        var listed = help.StandardOutput.Split('\n')
            .SkipWhile(line => !line.StartsWith("Commands:", StringComparison.Ordinal))
            .Skip(1)
            .Where(line => line.StartsWith("  ", StringComparison.Ordinal) && line.Trim().Length > 0)
            .Select(line => line.Trim().Split(' ')[0])
            .ToList();
        IReadOnlyList<string> hidden = ["host-agent", "sync-serve", "host-hold", "contain-exec", "isolate-exec"];

        Assert.NotEmpty(listed);
        Assert.Empty(listed.Except([.. AgentPolicy.SlotOrStateVerbs, .. AgentPolicy.OpenVerbs, SideTaskService.RunCommand]));
        Assert.Empty(AgentPolicy.SlotOrStateVerbs.Concat(AgentPolicy.OpenVerbs).Except(listed).Except(hidden));
        Assert.Empty(AgentPolicy.SlotOrStateVerbs.Intersect(AgentPolicy.OpenVerbs));
    }

    /// <summary>
    /// <c>run-side-task</c> is refused to every agent alike - a step's, and a side-task's own - however it is started: by
    /// its own name, as the tool's verb by name or by a full path, behind <c>env</c>, inside <c>sh -c</c>, and as the
    /// verb of the tool's assembly started by <c>dotnet</c>.
    /// </summary>
    [Fact]
    public void RunSideTaskItselfIsRefusedToEveryAgent_HoweverItIsStarted()
    {
        var (side, main, _) = Policy();
        var step = StepPolicy(main: main);

        foreach (var command in new[]
        {
            (IReadOnlyList<string>)["run-side-task", "--prompt", "x"],
            ["dssharness", "run-side-task"],
            ["dssharness", "run-side-task", "--prompt", "again"],
            ["/usr/local/bin/dssharness", "run-side-task"],
            ["env", "A=1", "dssharness", "run-side-task"],
            ["sh", "-c", "dssharness run-side-task --prompt x"],
            ["dotnet", "/opt/tool/dssharness.dll", "run-side-task"],
        })
        {
            foreach (var policy in new[] { side, step })
            {
                var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(command));

                Assert.Equal("run-side-task", denied.Rule);
                Assert.Equal(AgentPolicySource.Builtin, denied.Source);
                Assert.Equal(SideTaskReason, denied.Reason);
            }
        }
    }

    /// <summary>The worktree rules and the protected paths are off: the files they keep a step from, the side-task writes.</summary>
    [Fact]
    public void TheWorktreeRulesAndProtectedPathsAreOff_TheFilesTheyGuardAStepFrom_TheSideTaskWrites()
    {
        var (side, main, _) = Policy();
        using var worktree = new TempDirectory();
        var step = StepPolicy(main: main, worktree: worktree.Path);

        Assert.Equal(AgentPolicyAnswer.Allow, side.Write(".harness-config/config.json"));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Write(".vscode/settings.json"));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Write(".claude/settings.json"));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Write("package.json"));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Write("src/Program.cs"));

        // The step writes its own worktree's files; the worktree rules keep it from these two.
        Assert.Equal(AgentPolicyAnswer.Allow, step.Write("src/Program.cs"));
        Assert.Equal(
            ["Write(.harness-config/config.json)", "Write(.vscode/**)"],
            new[] { ".harness-config/config.json", ".vscode/settings.json" }.Select(path => Assert.IsType<AgentPolicyAnswer.Denied>(step.Write(path)).Rule));
    }

    /// <summary>
    /// Git's own files stay refused, whatever the protected paths say: they are written by git's verbs and this tool's,
    /// never by an agent's file tool - nor by its command, whether it redirects into one, tees into one, edits one in
    /// place or copies over one.
    /// </summary>
    [Fact]
    public void GitsOwnFilesStayRefused_ToTheFileToolsAndToEveryCommandThatWritesOne()
    {
        var (side, _, _) = Policy();

        foreach (var path in new[] { ".git/config", ".git/HEAD", "sub/.git/objects/x" })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(side.Write(path));

            Assert.Equal(AgentPolicy.GitsOwnRule, denied.Rule);
            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
        }

        // Each denial names the command whole as the analysis reads it, a redirect joined to its target.
        foreach (var (command, whole, target) in new[]
        {
            ("echo x > .git/hooks/pre-commit", "echo x >.git/hooks/pre-commit", ".git/hooks/pre-commit"),
            ("echo x >> .git/config", "echo x >>.git/config", ".git/config"),
            ("tee .git/hooks/pre-commit", "tee .git/hooks/pre-commit", ".git/hooks/pre-commit"),
            ("sed -i s/a/b/ .git/config", "sed -i s/a/b/ .git/config", ".git/config"),
            ("cp evil .git/hooks/pre-commit", "cp evil .git/hooks/pre-commit", ".git/hooks/pre-commit"),
        })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run(command));

            Assert.Equal(AgentPolicy.GitsOwnRule, denied.Rule);
            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
            Assert.Equal($"'{whole}' writes '{target}', git's own: written by git's verbs and this tool's, never by an agent's command", denied.Reason);
        }

        // Reading one, and copying one out, write nothing of git's.
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run("cat .git/config"));
        Assert.Equal(AgentPolicyAnswer.Allow, side.Run("cp .git/config copy.txt"));
    }

    /// <summary>
    /// The tool's own state is written only by its verbs: the records a run and a side-task keep are no agent's to
    /// write, the side-task's own records among them - by its file tools, and by its commands alike.
    /// </summary>
    [Fact]
    public void TheToolsOwnStateIsWrittenOnlyByItsVerbs_ByNoFileToolAndNoCommand()
    {
        var (side, _, _) = Policy();

        foreach (var path in new[]
        {
            ".orchestrators/r1/flow.json",
            ".side-tasks/2026-10-19-143201-add-a-source/task.json",
            ".side-tasks/2026-10-19-143201-add-a-source/stream.jsonl",
            ".mutations/m1/file.txt",
            ".legs/l1/file.txt",
        })
        {
            Assert.Equal("the tool's own state", Assert.IsType<AgentPolicyAnswer.Denied>(side.Write(path)).Rule);
        }

        foreach (var (command, whole, target) in new[]
        {
            ("tee .side-tasks/x/task.json", "tee .side-tasks/x/task.json", ".side-tasks/x/task.json"),
            ("sed -i s/a/b/ .orchestrators/r/flow.json", "sed -i s/a/b/ .orchestrators/r/flow.json", ".orchestrators/r/flow.json"),
            ("echo {} > .side-tasks/x/task.json", "echo {} >.side-tasks/x/task.json", ".side-tasks/x/task.json"),
            ("rm -rf .orchestrators/r", "rm -rf .orchestrators/r", ".orchestrators/r"),
        })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run(command));

            Assert.Equal("the tool's own state", denied.Rule);
            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
            Assert.Equal($"'{whole}' writes '{target}', the tool's own state: written by its verbs alone, never by an agent's", denied.Reason);
        }
    }

    /// <summary>
    /// Every credential slot of the main checkout stays no agent's to read, write or name - each of the layout's five, not
    /// the agents' key slot alone - and so does every Claude Code directory the side-task's clients use.
    /// </summary>
    [Fact]
    public void EveryCredentialSlotAndTheClaudeDirectoriesStayNoAgentsToReadOrName()
    {
        var (side, main, claude) = Policy();

        foreach (var slot in HarnessLayout.SecretSlots)
        {
            var file = $"{slot}/held";
            var full = Path.Combine(main, slot.Replace('/', Path.DirectorySeparatorChar), "held");

            foreach (var refused in new[] { side.Read(file), side.Write(file) })
            {
                var denied = Assert.IsType<AgentPolicyAnswer.Denied>(refused);

                Assert.Equal("the credential slots", denied.Rule);
                Assert.Equal($"'{full}' is a credential slot of this tool: no file tool of an agent reads or writes one", denied.Reason);
            }

            var named = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run((IReadOnlyList<string>)["cat", file]));

            Assert.Equal("the credential slots", named.Rule);
            Assert.Equal($"'{file}' names a credential slot of this tool: no command of an agent touches one", named.Reason);
        }

        var inClaude = Path.Combine(claude, "settings.json");
        var claudeDenied = Assert.IsType<AgentPolicyAnswer.Denied>(side.Read(inClaude));

        Assert.Equal("the Claude Code directories", claudeDenied.Rule);
        Assert.Equal($"'{inClaude}' lies in a Claude Code directory this run reads: no file tool of an agent reads or writes one", claudeDenied.Reason);

        var printing = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run((IReadOnlyList<string>)["gh", "auth", "token"]));

        Assert.Equal(("Run(gh auth token)", AgentPolicySource.Builtin), (printing.Rule, printing.Source));
        Assert.Equal("'gh auth token' is denied by Run(gh auth token), a rule of this tool: it prints a credential, which an agent of this tool is never handed", printing.Reason);
    }

    /// <summary>The root actions stay refused to the side-task's agent as to every agent's, wherever they stand.</summary>
    [Fact]
    public void TheRootActionsStayRefused()
    {
        var (side, _, _) = Policy();

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
    /// and the web search it is given holds every source turned on with no bound of a run's on it; a source the
    /// configuration turned off is never asked.
    /// </summary>
    [Fact]
    public void TheWebIsTheWholeOfIt_AnyAddress_EverySourceTurnedOn_NoBound()
    {
        var (side, _, _) = Policy();
        var step = StepPolicy(webDomains: ["example.com"]);

        Assert.Equal(AgentPolicyAnswer.Allow, side.Web("https://anywhere.example.org/page"));
        Assert.IsType<AgentPolicyAnswer.Denied>(step.Web("https://anywhere.example.org/page"));

        var sources = WebSearchSourceTests.Sources(configured: 3, off: "feed1");
        var search = SideTaskPolicy.WebSearch(sources, new Dictionary<string, WebSearchKey>());

        Assert.Equal([.. sources.All.Where(source => source.Name != "feed1").Select(source => source.Name)], search.Sources.Select(source => source.Name));
        Assert.Contains(sources.All, source => source.Name == "feed1");
        Assert.Equal(int.MaxValue, search.MaxPerStep);
    }
}

/// <summary>The web-search sources a policy test builds: the configuration's own, as many as a test says.</summary>
file static class WebSearchSourceTests
{
    /// <summary>Sources resolved from a configuration naming <paramref name="configured"/> feeds, over the built-in template's, one turned off where a test names it.</summary>
    public static WebSearchSources Sources(int configured, string? off = null)
    {
        var entries = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);

        for (var at = 0; at < configured; at++)
        {
            var entry = new System.Text.Json.Nodes.JsonObject
            {
                ["kind"] = "feed",
                ["address"] = $"https://example.org/{at}/feed.xml",
            };

            if ($"feed{at}" == off)
            {
                entry["enabled"] = false;
            }

            entries[$"feed{at}"] = System.Text.Json.JsonSerializer.SerializeToElement(entry);
        }

        return WebSearchSources.Of(
            new HarnessConfig { AgentWebSearch = new AgentWebSearchConfig { Sources = entries } },
            WebSearchKinds.OfThisBuild);
    }
}
