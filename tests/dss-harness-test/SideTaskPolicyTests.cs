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
    /// While a run's owner is live over the tree, the side-task's agent is refused every command that writes the git
    /// state the run works over - HEAD, the index, a ref, the stash, the configuration, a worktree - the run's name in the
    /// refusal; each command read as the worktree rules read one, so neither git's own options before the verb, the
    /// verb's own program, a configuration that makes a name for it nor a verb the shell expands passes it by. It is
    /// checked at each call: a run that starts while the chat is open is held to from its first step, and one that ends
    /// releases it.
    /// </summary>
    [Fact]
    public void ACommandThatWritesTheGitStateARunWorksOver_IsRefusedWhileItsOwnerIsLive_WithTheRunsName_AndOpenWhereNoneIs()
    {
        const string run = "r1";
        var live = false;
        var (side, _, _) = Policy(liveRun: () => live ? run : null);

        foreach (var command in new[] { "git commit -am wip", "git add -A", "git stash", "gh pr checkout 5" })
        {
            Assert.Equal(AgentPolicyAnswer.Allow, side.Run(command));
        }

        live = true;

        // Each way a command writes what the run works over, spelled here rather than read off the policy's own list.
        foreach (var command in new[]
        {
            "git commit -am wip", "git stash", "git reset --hard HEAD~1", "git clean -fd", "git checkout other", "git switch other",
            "git restore --staged a.txt", "git rebase main", "git merge other", "git cherry-pick abc", "git revert abc", "git pull",
            "git push", "git am p.mbox", "git worktree add ../w", "git branch other", "git branch -D other", "git tag v1",
            "git update-ref refs/heads/x HEAD", "git config user.name x", "git bisect start", "git symbolic-ref HEAD refs/heads/other",
            "git notes add -m x", "git replace a b", "git sparse-checkout set src", "git read-tree HEAD",
            "git add -A", "git stage a.txt", "git rm a.txt", "git mv a.txt b.txt", "git update-index --assume-unchanged a.txt",
            "git apply --index p.patch", "git apply -p1 --cached p.patch", "gh pr checkout 5",

            // git's own options before the verb, the verb's own program, and a name a configuration makes for it.
            "git -C . stash", "git --attr-source HEAD commit -am m", "git --shallow-file f commit", "git --no-pager stash",
            "/usr/lib/git-core/git-commit -am m", "git -c alias.x=commit x -am msg", "git -c core.pager=less log",
        })
        {
            var refused = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run(command));

            Assert.Equal(
                (AgentPolicy.LiveRunRule, AgentPolicySource.Builtin, $"run '{run}' is working over this tree, and '{command}' writes the git state it works over - its HEAD, index, refs, stash, configuration or worktrees; ask again when it ends, or pause it yourself"),
                (refused.Rule, refused.Source, refused.Reason));
        }

        // A word the shell expands where a held word would stand may be any of them: held, and said so.
        foreach (var (command, whole) in new[]
        {
            ("v=commit; git $v -am msg", "git $v -am msg"),
            ("git co${E}mmit -am m", "git co${E}mmit -am m"),
            ("git branch $X", "git branch $X"),
        })
        {
            var refused = Assert.IsType<AgentPolicyAnswer.Denied>(side.Run(command));

            Assert.Equal(
                (AgentPolicy.LiveRunRule, $"run '{run}' is working over this tree, and '{whole}' may write the git state it works over: a word of it is one the shell expands, which this does not read; ask again when it ends, or pause it yourself"),
                (refused.Rule, refused.Reason));
        }

        // The git that writes none of it, and the listing forms of the verbs that do, stay open.
        foreach (var command in new[]
        {
            "git status", "git log -1", "git diff HEAD", "git -C . status", "git rev-parse --abbrev-ref HEAD",
            "git branch", "git branch -a", "git branch -vv", "git branch --show-current", "git branch --list 'feat*'", "git --no-pager branch",
            "git tag", "git tag -l 'v*'", "git stash list", "git stash show -p stash@{0}", "git worktree list --porcelain",
            "git config --get user.name", "git config --list", "git remote -v", "git reflog", "git submodule status",
        })
        {
            Assert.Equal(AgentPolicyAnswer.Allow, side.Run(command));
        }

        live = false;

        foreach (var command in new[] { "git commit -am wip", "git add -A", "git -c alias.x=commit x -am msg" })
        {
            Assert.Equal(AgentPolicyAnswer.Allow, side.Run(command));
        }
    }

    /// <summary>
    /// The help says what a side-task's agent is held off while a run works over the tree from the very lists the policy
    /// holds it by: a verb added to one is said where it is refused.
    /// </summary>
    [Fact]
    public void TheHelp_NamesEveryCommandHeldWhileARunWorksOverTheTree_AndEveryListingFormLeftOpen()
    {
        var help = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("side-tasks"));

        Assert.Contains($"git's state-writing verbs ({string.Join(", ", AgentPolicy.GitStateVerbs)}), the verbs that write its index ({string.Join(", ", AgentPolicy.GitIndexVerbs)} - apply whole,", help, StringComparison.Ordinal);
        Assert.Contains($"gh's that write git's state ({string.Join(", ", AgentPolicy.GhStateCommands)}),", help, StringComparison.Ordinal);
        Assert.Contains($"Their listing forms stay open ({string.Join(", ", AgentPolicy.LiveRunOpen)}, a trailing * any words after the form)", help, StringComparison.Ordinal);
        Assert.Equal(
            [.. AgentPolicy.GitStateVerbs.Select(verb => $"git {verb}"), .. AgentPolicy.GitIndexVerbs.Select(verb => $"git {verb}"), .. AgentPolicy.GhStateCommands],
            AgentPolicy.LiveRunHeld);
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

    /// <summary>
    /// A side-task's command that leaves a process running after it ends is refused by the main tree's lock, named as it was
    /// written: one the shell runs in the background - a lone <c>&amp;</c> takes its whole list, a pipe's, an and-or's, a
    /// group's, a loop's and a subshell's with it, and a script a shell runs is read the same - and one a program keeps
    /// apart. A step's agent, which holds no such lock, is refused none of it.
    /// </summary>
    [Theory]
    [InlineData("make &", "make")]
    [InlineData("npm run codegen > gen.log 2>&1 &", "npm run codegen >gen.log")]
    [InlineData("make && make test &", "make")]
    [InlineData("make | tee build.log &", "make")]
    [InlineData("(make; make test) &", "make")]
    [InlineData("{ make; make test; } &", "make")]
    [InlineData("while true; do make; done &", "true")]
    [InlineData("make & echo started", "make")]
    [InlineData("echo started; make &", "make")]
    [InlineData("bash -c 'make &'", "bash -c make &")]
    [InlineData("nohup make", "nohup make")]
    [InlineData("setsid make", "setsid make")]
    [InlineData("disown -a", "disown -a")]
    [InlineData("start notepad", "start notepad")]
    [InlineData("cmd /c start notepad", "cmd /c start notepad")]
    [InlineData("pwsh -c 'Start-Process notepad'", "pwsh -c Start-Process notepad")]
    public void ASideTasksCommandThatLeavesAProcessBehind_IsRefusedByTheMainTreesLock(string command, string named)
    {
        var (side, _, _) = Policy();

        Assert.Equal(MainTreeWriteLock.LeftBehind(named), side.Run(command));
        Assert.Equal(AgentPolicyAnswer.Allow, StepPolicy().Run("make &"));
    }

    /// <summary>
    /// An <c>&amp;</c> that runs nothing in the background refuses a side-task's command nothing: one that joins two
    /// commands, one of a redirect's, one a pipe of both outputs spells, and one quoted or escaped.
    /// </summary>
    [Theory]
    [InlineData("make && make test")]
    [InlineData("make &> build.log")]
    [InlineData("make &>> build.log")]
    [InlineData("make >build.log 2>&1")]
    [InlineData("make >&2")]
    [InlineData("make |& tee build.log")]
    [InlineData("echo 'a & b'")]
    [InlineData("echo \"a & b\"")]
    [InlineData("echo a\\&b")]
    public void AnAmpersandThatRunsNothingInTheBackground_RefusesASideTasksCommandNothing(string command)
    {
        var (side, _, _) = Policy();

        Assert.Equal(AgentPolicyAnswer.Allow, side.Run(command));
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

    /// <summary>
    /// The side-task's graph tool is held to the side-task's own policy, as a step's is held to its step's: a node in a file
    /// its reads do not reach - here under a Claude Code directory - is left out of what the tool finds, and an edit of it
    /// is refused as one of a file its reads do not reach, the file as it was; a file the policy lets it write is edited.
    /// </summary>
    [Fact]
    public async Task TheSideTasksGraphTool_IsHeldToTheSideTasksOwnPolicy()
    {
        const string Secret = "namespace Demo;\n\npublic static class Secret\n{\n    public static int Code()\n    {\n        return 7;\n    }\n}\n";
        using var tree = await CodebaseGraphToolTests.TreeAsync(("src/hidden/Secret.cs", Secret));
        var graph = new SideTaskGraph(tree.Service, tree.Harness.FileSystem, tree.Harness.Platform, tree.Harness.GitClient, tree.Store, TimeProvider.System, tree.Layout, SideTaskLayout.Of(tree.Layout, "one"), tree.WorktreesRoot);
        await graph.OpenAsync(TestContext.Current.CancellationToken);
        var hidden = Path.Combine(tree.Tree, "src", "hidden");
        var ground = SideTaskPolicy.Ground(tree.Layout, new HarnessConfig { Worktrees = new WorktreeSettings() }, [hidden], []);
        var tool = graph.Support(ground)!.Tool!;
        var operations = DssHarness.Core.Agents.Runtime.CodebaseGraphTool.Operations(write: true, narrow: false);

        var found = await tool.RunAsync(System.Text.Json.Nodes.JsonNode.Parse("""{"op":"find","query":"Code"}""")!.AsObject(), operations, TestContext.Current.CancellationToken);

        Assert.Equal("no node matched\n1 node(s) left out: in files your reads do not reach", found.Text);

        var edited = await tool.RunAsync(
            System.Text.Json.Nodes.JsonNode.Parse("""{"op":"edit","symbol":"Demo.Secret.Code","hash":"#00000000","text":"    public static int Code() => 8;"}""")!.AsObject(),
            operations,
            TestContext.Current.CancellationToken);

        Assert.Equal(DssHarness.Core.Agents.Runtime.GraphToolOutcome.Answered, edited.Outcome);
        Assert.StartsWith("'Demo.Secret.Code' is in a file your reads do not reach", edited.Text, StringComparison.Ordinal);
        Assert.Equal(Secret, File.ReadAllText(Path.Combine(hidden, "Secret.cs")));

        // What the tool may write is what the side-task's policy may write: a file it may write is edited.
        var twice = await tool.RunAsync(
            System.Text.Json.Nodes.JsonNode.Parse("""{"op":"edit","symbol":"Twice","hash":"#aac0401c","text":"    public static double Twice(IShape shape) => 0;"}""")!.AsObject(),
            operations,
            TestContext.Current.CancellationToken);

        Assert.Equal(DssHarness.Core.Agents.Runtime.GraphToolOutcome.Wrote, twice.Outcome);
    }

    /// <summary>
    /// The side-task's graph is synced by each write of its agent - its sync's record naming the side-task and the tool that
    /// wrote, never its opening - and its tool answers from the view the sync left.
    /// </summary>
    [Fact]
    public async Task TheSideTasksGraph_IsSyncedByEachWriteOfItsAgent_ItsRecordNamingTheWrite()
    {
        using var tree = await CodebaseGraphToolTests.TreeAsync();
        var layout = SideTaskLayout.Of(tree.Layout, "one");
        var graph = new SideTaskGraph(tree.Service, tree.Harness.FileSystem, tree.Harness.Platform, tree.Harness.GitClient, tree.Store, TimeProvider.System, tree.Layout, layout, tree.WorktreesRoot);
        await graph.OpenAsync(TestContext.Current.CancellationToken);
        var support = graph.Support(SideTaskPolicy.Ground(tree.Layout, new HarnessConfig { Worktrees = new WorktreeSettings() }, [], []))!;
        tree.Write("src/Added.cs", "namespace Demo;\n\npublic static class Added\n{\n    public static int One()\n    {\n        return 1;\n    }\n}\n");

        await support.AfterWriteAsync!(DssHarness.Core.Agents.Runtime.AgentGraphWrite.Of("create_file", Path.Combine(tree.Tree, "src", "Added.cs")), TestContext.Current.CancellationToken);

        var line = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllLines(layout.AsGraph.GraphSyncFile)[^1])!;
        Assert.Equal("the side-task 'one': create_file", line["why"]!.GetValue<string>());

        var found = await support.Tool!.RunAsync(
            System.Text.Json.Nodes.JsonNode.Parse("""{"op":"find","query":"Added"}""")!.AsObject(),
            DssHarness.Core.Agents.Runtime.CodebaseGraphTool.Operations(write: true, narrow: false),
            TestContext.Current.CancellationToken);

        Assert.Contains(" public static class Added | src/Added.cs:3-9 #", found.Text, StringComparison.Ordinal);
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
