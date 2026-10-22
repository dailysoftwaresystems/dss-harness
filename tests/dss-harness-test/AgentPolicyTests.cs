using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using Xunit;

namespace DssHarness.Tests;

/// <summary>
/// The agents' policy: everything allowed except the root actions, the places no agent reaches and
/// the restrictions, each refusal by its own sentence with its rule and where the rule stands, and
/// nothing allowed where the tool cannot decide.
/// </summary>
public sealed class AgentPolicyTests
{
    [Fact]
    public void EverythingIsAllowedWhereNothingRefusesIt()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Read("src/a.cs"));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Write("src/a.cs"));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["make", "test"]));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run("make test -j4"));
    }

    [Fact]
    public void ARootActionIsRefusedWhereverACommandNamesIt()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        foreach (var command in new[]
        {
            (IReadOnlyList<string>)["sudo", "apt", "install", "x"],
            ["su", "root"],
            ["/usr/bin/sudo", "x"],
            ["env", "A=1", "pkexec", "x"],
            ["nohup", "gsudo", "x"],
            ["sh", "-c", "sudo x"],
            ["run0", "x"],
            ["systemd-run", "x"],
            ["runas", "/user:Administrator", "cmd"],
            ["psexec", "\\\\other", "cmd"],
        })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(command));

            Assert.Equal("the root actions", denied.Rule);
            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
            Assert.Contains("is a root action: no agent of this tool acts as root", denied.Reason, StringComparison.Ordinal);
        }

        var substitution = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("echo \"$(sudo x)\""));

        Assert.Contains("is a root action", substitution.Reason, StringComparison.Ordinal);
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["git", "status"]));
    }

    [Fact]
    public void ARootActionIsRefusedHoweverItsNameEscapesOrIsSpelled()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        foreach (var command in new[] { "\\sudo x", "su\\do x", "env -S \"sudo x\"" })
        {
            Assert.Equal("the root actions", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(command)).Rule);
        }

        // The list's own names, compared without regard to case on every system.
        Assert.Equal("the root actions", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run((IReadOnlyList<string>)["SUDO.EXE", "x"])).Rule);
        Assert.Equal("the root actions", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run((IReadOnlyList<string>)["Start-Process", "-Verb", "RunAs", "cmd"])).Rule);
    }

    [Fact]
    public void OnWindowsARootActionIsRefusedByNameWhateverItsCase()
    {
        using var ground = Ground(PlatformId.Windows);
        var policy = ground.Policy();

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run((IReadOnlyList<string>)["SUDO.EXE", "x"]));
        var runAs = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run((IReadOnlyList<string>)["Start-Process", "-Verb", "RunAs", "cmd"]));

        Assert.Contains("'sudo' is a root action", denied.Reason, StringComparison.Ordinal);
        Assert.Contains("'runas' is a root action", runAs.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WlStartingARootUserAndSchtasksRunningAsAnotherAccountAreRefused()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        foreach (var command in new[]
        {
            (IReadOnlyList<string>)["wsl", "-u", "root", "apt", "update"],
            ["wsl", "--user", "root"],
            ["wsl", "--user=root"],
            ["schtasks", "/create", "/tn", "x", "/ru", "SYSTEM"],
        })
        {
            var answer = policy.Run(command);
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(answer);

            Assert.Equal("the root actions", denied.Rule);
            Assert.Contains("no agent of this tool acts as root", denied.Reason, StringComparison.Ordinal);
        }

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["wsl", "-u", "dev", "ls"]));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["schtasks", "/query"]));
    }

    [Fact]
    public void NoFileToolReadsOrWritesACredentialSlot()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        var read = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Read(Path.Combine(ground.Slot, "keys.env")));
        var written = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(Path.Combine(ground.Slot, "planted.env")));

        Assert.Equal("the credential slots", read.Rule);
        Assert.Contains("is a credential slot of this tool: no file tool of an agent reads or writes one", read.Reason, StringComparison.Ordinal);
        Assert.Contains("is a credential slot of this tool", written.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCommandNamesASlotOrAClaudeCodeDirectory()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        var slot = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run((IReadOnlyList<string>)["cat", Path.Combine(ground.Slot, "keys.env")]));
        var shell = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run($"cat {ground.Slot.Replace('\\', '/')}/keys.env"));
        var claude = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run((IReadOnlyList<string>)["cat", Path.Combine(ground.ClaudeDirectory, "settings.json")]));

        Assert.Contains("names a credential slot of this tool: no command of an agent touches one", slot.Reason, StringComparison.Ordinal);
        Assert.Contains("names a credential slot", shell.Reason, StringComparison.Ordinal);
        Assert.Contains("lies in a Claude Code directory this run reads: no command of an agent touches one", claude.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteOutsideTheWritableRootsIsRefused_NamingThem()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(Path.Combine(ground.Main, "README.md")));

        Assert.Equal("the writable roots", denied.Rule);
        Assert.Equal(AgentPolicySource.Builtin, denied.Source);
        Assert.Equal(
            $"'{Path.GetFullPath(Path.Combine(ground.Main, "README.md"))}' is outside the writable roots of this agent: {ground.Worktree}, {ground.WorkDirectory}",
            denied.Reason);
    }

    [Fact]
    public void ARunRuleRefusesItsPrefix_TheBareCommandAndGitsOptionsDropped()
    {
        using var ground = Ground();
        var policy = ground.Policy([new("Run(git push *)", AgentPolicySource.Config)], worktreeRules: false);

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["git", "commit"]));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["gitx", "push"]));

        foreach (var command in new[]
        {
            (IReadOnlyList<string>)["git", "push"],
            ["git", "push", "origin", "main"],
            ["git", "-C", "..", "push", "origin"],
            ["env", "A=1", "git", "push"],
        })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(command));

            Assert.Equal("Run(git push *)", denied.Rule);
            Assert.Equal(AgentPolicySource.Config, denied.Source);
            Assert.EndsWith(", a restriction of agentPolicy.restrictions", denied.Reason, StringComparison.Ordinal);
        }

        var shell = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("sh -c 'git -C x push'"));

        Assert.Equal("Run(git push *)", shell.Rule);
    }

    [Fact]
    public void AGitCThatNamesAnAliasOrAProgramGitRuns_IsRefusedAsTheGitConfigItIs()
    {
        using var ground = Ground();
        var policy = ground.Policy([new("Run(git config *)", AgentPolicySource.Config)], worktreeRules: false);

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["git", "-c", "user.email=a@b", "commit"]));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["git", "commit"]));

        foreach (var command in new[]
        {
            (IReadOnlyList<string>)["git", "config", "user.email", "a@b"],
            ["git", "-c", "alias.x=commit", "x"],
            ["git", "-c", "core.hooksPath=.githooks", "status"],
        })
        {
            Assert.Equal("Run(git config *)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(command)).Rule);
        }
    }

    [Fact]
    public void WriteAndReadRulesMatchThePathAsItStandsUnderEachRoot()
    {
        using var ground = Ground();
        var policy = ground.Policy(
        [
            new("Write(.github/**)", AgentPolicySource.Config),
            new("Read(secrets/**)", AgentPolicySource.Agent),
        ]);

        var written = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(".github/workflows/x.yml"));
        var read = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Read("secrets/keys.json"));

        Assert.Equal("Write(.github/**)", written.Rule);
        Assert.EndsWith(", a restriction of agentPolicy.restrictions", written.Reason, StringComparison.Ordinal);
        Assert.Equal("Read(secrets/**)", read.Rule);
        Assert.Equal(AgentPolicySource.Agent, read.Source);
        Assert.EndsWith(", a restriction of this agent", read.Reason, StringComparison.Ordinal);

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Write("src/a.cs"));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Read("src/a.cs"));
    }

    [Fact]
    public void ARestrictionIsTriedOnThePathAsAskedAndAsResolved_SoALinkReachesPastNothing()
    {
        using var ground = Ground();
        TestLinks.DirectoryLink(Path.Combine(ground.Worktree, "linked"), Path.Combine(ground.Worktree, ".github"));
        var policy = ground.Policy([new("Write(.github/**)", AgentPolicySource.Config)]);

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write("linked/x.yml"));

        Assert.Equal("Write(.github/**)", denied.Rule);
    }

    [Fact]
    public void WhereTheToolCannotDecide_ItDenies()
    {
        using var ground = new BrokenGround();
        var policy = ground.Policy();

        foreach (var answer in new[] { policy.Read("src/a.cs"), policy.Write("src/a.cs") })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(answer);

            Assert.Equal("undecided", denied.Rule);
            Assert.Contains("this tool could not decide 'src/a.cs'", denied.Reason, StringComparison.Ordinal);
            Assert.EndsWith("it denies what it cannot decide", denied.Reason, StringComparison.Ordinal);
        }

        var run = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("git push"));

        Assert.Contains("this tool could not decide 'git push'", run.Reason, StringComparison.Ordinal);
        Assert.EndsWith("it denies what it cannot decide", run.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAgentStartsWhileTheToolItselfRunsElevated()
    {
        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(AgentPolicy.RefusedWhileElevated());

        Assert.Equal("the root actions", denied.Rule);
        Assert.Equal(AgentPolicySource.Builtin, denied.Source);
        Assert.Equal("this tool itself is running elevated, as root or an administrator: no agent starts while it does", denied.Reason);
    }

    [Fact]
    public void RuleProblems_RefuseWhatIsNoRuleNamingTheVerbs()
    {
        var problems = AgentPolicy.RuleProblems(new HarnessConfig
        {
            AgentPolicy = new AgentPolicyConfig { Restrictions = ["web(pastebin.com)", "push"] },
        });

        Assert.Equal(
        [
            "agentPolicy.restrictions[0] 'web(pastebin.com)' is no rule this tool reads: the verbs are Run, Write, Read and Web",
            "agentPolicy.restrictions[1] 'push' is no rule this tool reads: a rule is one string, its verb and its pattern in parentheses - Run(git push *), Write(.github/**), Read(secrets/**)",
        ],
        problems);
    }

    [Fact]
    public void AWriteRuleMatchesTheWritableRootsNoReadNames()
    {
        using var ground = Ground();
        var policy = ground.Policy([new("Write(out/**)", AgentPolicySource.Config)]);

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(Path.Combine(ground.WorkDirectory, "out", "x.txt")));

        Assert.Equal("Write(out/**)", denied.Rule);
        Assert.EndsWith(", a restriction of agentPolicy.restrictions", denied.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunRuleWhoseStarStandsBesideOtherCharactersIsRefusedWhereItIsDeclared()
    {
        var problems = AgentPolicy.RuleProblems(new HarnessConfig
        {
            AgentPolicy = new AgentPolicyConfig { Restrictions = ["Run(git push o*)"] },
        });

        Assert.Equal(
            ["agentPolicy.restrictions[0] 'Run(git push o*)' does not read: a Run rule's '*' stands for any arguments, at its end"],
            problems);
    }

    [Fact]
    public void RuleProblems_RefuseAPatternThatDoesNotRead()
    {
        var problems = AgentPolicy.RuleProblems(new HarnessConfig
        {
            AgentPolicy = new AgentPolicyConfig { Restrictions = ["Write(.git**)", "Run(git * push)"] },
        });

        Assert.Equal(
        [
            "agentPolicy.restrictions[0] 'Write(.git**)' does not read: the pattern '.git**' holds '**' inside a segment; '**' is a segment of its own, standing for any number of segments",
            "agentPolicy.restrictions[1] 'Run(git * push)' does not read: a Run rule's '*' stands for any arguments, at its end",
        ],
        problems);
    }

    [Fact]
    public void RuleProblems_AreEmptyWhereEveryRuleReads_AndWhereNothingIsDeclared()
    {
        Assert.Empty(AgentPolicy.RuleProblems(new HarnessConfig()));
        Assert.Empty(AgentPolicy.RuleProblems(new HarnessConfig { AgentPolicy = new AgentPolicyConfig() }));
        Assert.Empty(AgentPolicy.RuleProblems(new HarnessConfig
        {
            AgentPolicy = new AgentPolicyConfig { Restrictions = ["Run(git push *)", "Write(.github/**)", "Read(secrets/**)"] },
        }));
    }

    [Fact]
    public void AConfigFileHoldingARuleThatDoesNotReadIsRefusedWhereItIsRead()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".harness-config/config.json", "{ \"agentPolicy\": { \"restrictions\": [\"push\"] } }");
        var store = new JsonConfigStore(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())));

        var failure = Assert.Throws<ConfigException>(() => store.Load(Path.Combine(temp.Path, ".harness-config", "config.json")));

        Assert.Contains("agentPolicy.restrictions[0] 'push' is no rule this tool reads", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShellReaderIsNotMisledByALongOptionThatHoldsTheLetterC()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        Assert.Equal("the root actions", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("bash --norc -c 'sudo x'")).Rule);
        Assert.Equal("Run(git push *)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("zsh --no-rcs -c 'git push'")).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["bash", "-lc", "echo x"]));
    }

    [Fact]
    public void CmdKRunsWhatItIsGiven_AsCmdCDoes()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        Assert.Equal("Run(git push *)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run((IReadOnlyList<string>)["cmd", "/k", "git", "push"])).Rule);
    }

    [Fact]
    public void AHostSpelledWithItsRootDotMeetsTheRuleWrittenWithoutIt()
    {
        using var ground = Ground();
        var policy = ground.Policy([new("Web(pastebin.com)", AgentPolicySource.Config)]);

        Assert.Equal("Web(pastebin.com)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("curl -s https://pastebin.com./raw/1")).Rule);
    }

    [Fact]
    public void AGluedHostnameNamesItsHost_AsTheSeparateFormDoes()
    {
        using var ground = Ground();
        var policy = ground.Policy([new("Web(evil.example.com)", AgentPolicySource.Config)]);

        Assert.Equal("Web(evil.example.com)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("gh api --hostname=evil.example.com /user")).Rule);
    }

    [Fact]
    public void AValueATakingOptionHoldsIsNoAddress_AndLocalhostIsOne()
    {
        using var ground = Ground();
        var narrowed = ground.Policy(webDomains: ["allowed.example.com"]);

        // The output file's name is not a host; the one address of the command is allowed's.
        Assert.Equal(AgentPolicyAnswer.Allow, narrowed.Run("curl -o api.example.org.json https://allowed.example.com/x"));

        var withoutWeb = ground.Policy(web: false);

        Assert.Equal("the web capability", Assert.IsType<AgentPolicyAnswer.Denied>(withoutWeb.Run("curl localhost:8080/x")).Rule);
    }

    [Fact]
    public void TheToolsOwnStateIsNotReachedByALinkFromAWritableRoot()
    {
        using var ground = Ground();
        TestLinks.DirectoryLink(Path.Combine(ground.Worktree, "jump"), Path.Combine(ground.Main, ".orchestrators", "r1"));
        var policy = ground.Policy(stateDirectories: [Path.Combine(ground.Main, ".orchestrators")]);

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write("jump/flow.json"));

        Assert.Equal("the tool's own state", denied.Rule);
    }

    [Fact]
    public void TheReporterDoesNotReadThroughALinkOutOfItsRoot()
    {
        using var ground = Ground();
        var run = Path.Combine(ground.Main, ".orchestrators", "r1");
        Directory.CreateDirectory(run);
        TestLinks.DirectoryLink(Path.Combine(run, "into-main"), ground.Main);
        var policy = ground.Policy(reporterRoot: run);

        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Read(Path.Combine(run, "into-main", "README.md"))).Rule);
    }

    [Fact]
    public void AWebDomainsListIsRefusedGivenEmpty()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, ".harness-config"));
        File.WriteAllText(
            Path.Combine(temp.Path, ".harness-config", "config.json"),
            """{ "agentPolicy": { "webDomains": [] } }""");

        var failure = Assert.Throws<ConfigException>(() => new JsonConfigStore(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())))
            .Load(Path.Combine(temp.Path, ".harness-config", "config.json")));

        Assert.Contains(
            "agentPolicy.webDomains is given empty: leave the key out to allow any public address, or name the domains an agent may reach",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheSkillsDirectoryOpensToAWritesPatternSpelledWithEitherSeparator()
    {
        using var ground = Ground();
        var opened = ground.Policy(writes: [".harness-config\\skills\\release-notes\\**"]);

        Assert.Equal(AgentPolicyAnswer.Allow, opened.Write(Path.Combine(ground.Worktree, ".harness-config", "skills", "release-notes", "SKILL.md")));
    }

    /// <summary>The shipped protected-path list, pinned word for word: a change in it is a change someone made.</summary>
    [Fact]
    public void TheProtectedPathsArePinned()
    {
        Assert.Equal(
            new[]
            {
                ".git/**", ".config/git/**", ".claude/**", ".vscode/**", ".idea/**", ".husky/**", ".cargo/**",
                ".devcontainer/**", ".yarn/**", ".mvn/**", ".ssh/**", ".mcp.json", ".claude.json",
                ".bashrc", ".bash_profile", ".profile", ".zshrc", ".zprofile", ".zshenv", ".config/fish/config.fish",
                "package.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb",
                "Cargo.toml", "Cargo.lock", "go.mod", "go.sum", "pom.xml", "requirements.txt", "pyproject.toml",
                "Gemfile", "Gemfile.lock", "composer.json", "composer.lock",
            },
            AgentPolicy.ProtectedPathRules);
    }

    [Fact]
    public void TheWorktreeRulesRefuseGitsStateWritingVerbs()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        foreach (var command in new[]
        {
            (IReadOnlyList<string>)["git", "commit", "-m", "x"],
            ["git", "stash"],
            ["git", "checkout", "main"],
            ["git", "-C", "x", "push", "origin"],
            ["git", "config", "user.email", "a@b"],
        })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(command));

            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
            Assert.EndsWith(", a worktree rule of this tool", denied.Reason, StringComparison.Ordinal);
        }

        Assert.Equal("Run(git commit *)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("git commit -m x")).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["git", "status"]));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run("git log --oneline"));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["git", "blame", "a.cs"]));
    }

    [Fact]
    public void NeverMergesNeverStartsCi_BindsTheAgentsToo()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        foreach (var command in new[] { "gh pr merge 41", "gh workflow run build.yml", "gh run rerun 7" })
        {
            Assert.Equal(AgentPolicySource.Builtin, Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(command)).Source);
        }

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run("gh pr view 41"));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run("gh run watch 7"));
    }

    [Fact]
    public void TheToolsOwnVerbsAndRunSideTaskAreRefusedWhateverWorktreeRulesSay()
    {
        using var ground = Ground();
        var policy = ground.Policy(worktreeRules: false);

        foreach (var command in new[]
        {
            (IReadOnlyList<string>)["dssharness", "stop-orchestrator", "r1", "--now"],
            ["dssharness", "fold-agent", "a1", "--allow", "settled:1"],
            ["dssharness", "run-orchestrator", "implementation"],
            ["run-side-task", "--prompt", "x"],
        })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(command));

            Assert.Equal(AgentPolicySource.Builtin, denied.Source);
        }

        Assert.Equal("the orchestrator and agent verbs", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("dssharness delete-agent a1")).Rule);
        Assert.Equal("run-side-task", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("run-side-task")).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["dssharness", "build", "--legs", "windows"]));
    }

    [Fact]
    public void GitsOwnFilesAreNotWrittenWhateverProtectedPathsSays()
    {
        using var ground = Ground();
        var policy = ground.Policy(protectedPaths: false);

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(".git/config"));

        Assert.Equal("Write(.git/**)", denied.Rule);
        Assert.Contains("is git's own: written by git's verbs and this tool's, never by an agent's file tool, whatever protectedPaths says", denied.Reason, StringComparison.Ordinal);

        var off = ground.Policy(worktreeRules: false, protectedPaths: false);

        Assert.Equal(AgentPolicyAnswer.Allow, off.Write(".git/config"));
    }

    [Fact]
    public void AnAgentDoesNotEditItsOwnPolicyOrWhatTheNextLaunchLoads()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        foreach (var (path, rule) in new[]
        {
            (Path.Combine(ground.Main, ".harness-config", "config.json"), "Write(.harness-config/config.json)"),
            (Path.Combine(ground.Main, ".harness-config", "orchestrators", "impl", "agents", "a.md"), "Write(.harness-config/orchestrators/**)"),
            (Path.Combine(ground.Main, ".harness-config", "runner", "actions", "a.json"), "Write(.harness-config/runner/**)"),
            (Path.Combine(ground.Worktree, ".claude", "settings.local.json"), "Write(.claude/settings*.json)"),
            (Path.Combine(ground.Worktree, ".mcp.json"), "Write(.mcp.json)"),
        })
        {
            Assert.Equal(rule, Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(path)).Rule);
        }
    }

    [Fact]
    public void TheSkillsDirectoryOpensToAStepWhoseWritesNameItItself()
    {
        using var ground = Ground();
        var opened = ground.Policy(writes: [".harness-config/skills/release-notes/**"]);
        var broad = ground.Policy(writes: ["**"]);
        var none = ground.Policy();

        // A worktree holds the tracked skills as it holds any tracked file: it is its own copy an
        // agent edits, and the fold that moves the work to the main tree.
        var skill = Path.Combine(ground.Worktree, ".harness-config", "skills", "release-notes", "SKILL.md");
        var other = Path.Combine(ground.Worktree, ".harness-config", "skills", "other-skill", "SKILL.md");

        Assert.Equal(AgentPolicyAnswer.Allow, opened.Write(skill));
        Assert.Equal("Write(.harness-config/skills/**)", Assert.IsType<AgentPolicyAnswer.Denied>(opened.Write(other)).Rule);
        Assert.Equal("Write(.harness-config/skills/**)", Assert.IsType<AgentPolicyAnswer.Denied>(broad.Write(skill)).Rule);
        Assert.Equal("Write(.harness-config/skills/**)", Assert.IsType<AgentPolicyAnswer.Denied>(none.Write(skill)).Rule);
    }

    [Fact]
    public void TheToolsOwnStateIsWrittenByItsVerbsAlone_ItsOwnWorkDirectoryAloneExcepted()
    {
        using var ground = Ground();
        var policy = ground.Policy(stateDirectories: [Path.Combine(ground.Main, ".orchestrators"), Path.Combine(ground.Main, "build", "worktrees")]);

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(Path.Combine(ground.Main, ".orchestrators", "r1", "flow.json")));

        Assert.Equal("the tool's own state", denied.Rule);
        Assert.Contains("the tool's own state: written by its verbs alone, never by an agent's - its own work directory alone excepted", denied.Reason, StringComparison.Ordinal);

        // The agent's own work directory and worktree are writable roots of their own: the one
        // exception the rule makes, in whichever switch the rest of the rules stand.
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Write(Path.Combine(ground.WorkDirectory, "notes.md")));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Write("src/a.cs"));

        var off = ground.Policy(worktreeRules: false, protectedPaths: false, stateDirectories: [Path.Combine(ground.Main, ".orchestrators")]);

        Assert.Equal("the tool's own state", Assert.IsType<AgentPolicyAnswer.Denied>(off.Write(Path.Combine(ground.Main, ".orchestrators", "runs", "lock.json"))).Rule);
    }

    [Fact]
    public void TheProtectedPathsAreRefusedOnEveryRoot()
    {
        using var ground = Ground();
        var policy = ground.Policy();

        foreach (var path in new[]
        {
            Path.Combine(ground.Worktree, ".vscode", "settings.json"),
            Path.Combine(ground.Main, ".vscode", "settings.json"),
            Path.Combine(ground.Worktree, "package.json"),
            Path.Combine(ground.Main, ".idea", "vcs.xml"),
            Path.Combine(ground.Worktree, ".husky", "pre-commit"),
            Path.Combine(ground.WorkDirectory, "Cargo.toml"),
        })
        {
            Assert.Equal(AgentPolicySource.Builtin, Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(path)).Source);
        }

        Assert.Equal("Write(.vscode/**)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(".vscode/settings.json")).Rule);

        var off = ground.Policy(protectedPaths: false);

        Assert.Equal(AgentPolicyAnswer.Allow, off.Write(".vscode/settings.json"));
    }

    [Fact]
    public void AStepsWritesNarrowItsWorktree()
    {
        using var ground = Ground();
        var policy = ground.Policy(writes: ["src/a/**", "tests/a/**"]);

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Write("src/a/deep/file.cs"));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Write("tests/a/x.cs"));

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write("src/b/file.cs"));

        Assert.Equal("the step's writes", denied.Rule);
        Assert.Equal(
            $"'{Path.GetFullPath(Path.Combine(ground.Worktree, "src", "b", "file.cs"))}' is outside the paths this step may write: src/a/**, tests/a/**",
            denied.Reason);

        // A write outside the worktree - its work directory - is not narrowed by them.
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Write(Path.Combine(ground.WorkDirectory, "out", "f.txt")));
    }

    [Fact]
    public void TheReporterReadsItsOneRootAlone_AndNothingElse()
    {
        using var ground = Ground();
        var run = Path.Combine(ground.Main, ".orchestrators", "r1");
        var policy = ground.Policy(reporterRoot: run);

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Read(Path.Combine(run, "flow.json")));

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Read(Path.Combine(ground.Main, "README.md")));

        Assert.Equal("the reporter's reads", denied.Rule);
        Assert.Equal(
            $"'{Path.GetFullPath(Path.Combine(ground.Main, "README.md"))}' is outside what this agent may read: {run.Replace('\\', '/')}/**",
            denied.Reason);

        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("git status")).Rule);
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(Path.Combine(run, "x.md"))).Rule);
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Web("https://example.com/x")).Rule);
    }

    [Fact]
    public void WebRulesRefuseADomainAndEveryNameUnderIt()
    {
        using var ground = Ground();
        var policy = ground.Policy([new("Web(pastebin.com)", AgentPolicySource.Config)]);

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Web("https://example.com/page"));

        foreach (var address in new[] { "https://pastebin.com/x", "https://www.pastebin.com/raw/1" })
        {
            var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Web(address));

            Assert.Equal("Web(pastebin.com)", denied.Rule);
            Assert.EndsWith(", a restriction of agentPolicy.restrictions", denied.Reason, StringComparison.Ordinal);
        }

        // A command that runs a program which fetches is decided by the same rule, by each address.
        Assert.Equal("Web(pastebin.com)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("curl -s https://pastebin.com/raw/1")).Rule);
        Assert.Equal("Web(pastebin.com)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("wget pastebin.com/raw/1")).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run("curl -s https://example.com/x"));
    }

    [Fact]
    public void WebStarRefusesTheWebWhole()
    {
        using var ground = Ground();
        var policy = ground.Policy([new("Web(*)", AgentPolicySource.Agent)]);

        Assert.Equal("Web(*)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Web("https://example.com/x")).Rule);
        Assert.Equal("Web(*)", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("http example.com/x")).Rule);
    }

    [Fact]
    public void WebDomainsIsTheAllowListOfEveryRequest()
    {
        using var ground = Ground();
        var policy = ground.Policy(webDomains: ["example.com", "api.example.org"]);

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Web("https://api.example.com/v1"));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Web("https://example.com"));

        var denied = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Web("https://pastebin.com/x"));

        Assert.Equal("agentPolicy.webDomains", denied.Rule);
        Assert.Equal(AgentPolicySource.Config, denied.Source);
        Assert.Equal("'pastebin.com' is outside webDomains of agentPolicy: example.com, api.example.org", denied.Reason);

        Assert.Equal("agentPolicy.webDomains", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("curl https://other.net/x")).Rule);
    }

    [Fact]
    public void AStepWithoutTheWebIsRefusedARequestAndAFetcherAlike()
    {
        using var ground = Ground();
        var policy = ground.Policy(web: false);

        var page = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Web("https://example.com/x"));

        Assert.Equal("the web capability", page.Rule);
        Assert.Equal("'https://example.com/x' fetches 'https://example.com/x' and this step has no web", page.Reason);

        var fetcher = Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run("iwr https://example.com/x -OutFile o"));

        Assert.Equal("the web capability", fetcher.Rule);
        Assert.Contains("'iwr https://example.com/x -OutFile o' fetches 'https://example.com/x' and this step has no web", fetcher.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void GhApiIsApiGithubcoms_UnlessAHostnameNamesAnother()
    {
        using var ground = Ground();
        var refused = ground.Policy([new("Web(api.github.com)", AgentPolicySource.Config)]);

        Assert.Equal("Web(api.github.com)", Assert.IsType<AgentPolicyAnswer.Denied>(refused.Run("gh api repos/owner/repo/pulls")).Rule);
        Assert.Equal("Web(api.github.com)", Assert.IsType<AgentPolicyAnswer.Denied>(refused.Run("gh api --hostname api.github.com /user")).Rule);

        var other = ground.Policy([new("Web(ghe.example.com)", AgentPolicySource.Config)]);

        Assert.Equal("Web(ghe.example.com)", Assert.IsType<AgentPolicyAnswer.Denied>(other.Run("gh api --hostname ghe.example.com /user")).Rule);
        Assert.Equal(AgentPolicyAnswer.Allow, other.Run("gh api repos/owner/repo"));
    }

    [Fact]
    public void AnAddressAWebRuleDoesNotRefuse_AndACommandThatIsNoFetcher()
    {
        using var ground = Ground();
        var policy = ground.Policy([new("Web(pastebin.com)", AgentPolicySource.Config)]);

        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["powershell", "-Command", "Invoke-RestMethod -Uri https://example.com/api"]));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run("irm https://example.com/api"));

        // A program the list does not name is not decided as the web, whatever its arguments hold.
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run("git clone https://pastebin.com/x.git"));
    }

    [Fact]
    public void WebRulesAndDomainsAreReadWhereTheConfigurationIs()
    {
        using var temp = new TempDirectory();
        var store = new JsonConfigStore(new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())));
        store.Save(Path.Combine(temp.Path, ".harness-config", "config.json"), new HarnessConfig
        {
            AgentPolicy = new AgentPolicyConfig { Restrictions = ["Web(pastebin..com)"], WebDomains = ["example.com", "no host"] },
        });

        var failure = Assert.Throws<ConfigException>(() => store.Load(Path.Combine(temp.Path, ".harness-config", "config.json")));

        Assert.Contains("agentPolicy.restrictions[0] 'Web(pastebin..com)' does not read:", failure.Message, StringComparison.Ordinal);
        Assert.Contains("agentPolicy.webDomains[1] 'no host' is no domain:", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The ground of an agent with a worktree: a main checkout it reads, a worktree and a work directory it writes, the slot of the main checkout, and a Claude Code directory.</summary>
    private static AgentPolicyTree Ground(PlatformId platform = PlatformId.Linux) => new(platform);

    /// <summary>The ground of an agent with a worktree: a main checkout it reads, a worktree it writes, the slot of the main checkout, and a Claude Code directory.</summary>
    private class AgentPolicyTree(PlatformId platform = PlatformId.Linux) : IDisposable
    {
        private readonly TempDirectory _main = new();
        private readonly TempDirectory _claude = new();
        private readonly TempDirectory _worktree = new();
        private readonly TempDirectory _work = new();

        public string Main => _main.Path;

        public string Worktree => _worktree.Path;

        public string WorkDirectory => _work.Path;

        public string Slot => Path.Combine(Main, ".harness-config", "workflows", ".env");

        public string ClaudeDirectory => _claude.Path;

        public AgentPolicy Policy(
            IReadOnlyList<AgentRestriction>? restrictions = null,
            bool worktreeRules = true,
            bool protectedPaths = true,
            bool web = true,
            IReadOnlyList<string>? webDomains = null,
            IReadOnlyList<string>? writes = null,
            string? reporterRoot = null,
            IReadOnlyList<string>? stateDirectories = null)
            => new(
                new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())),
                HostDoubles.Platform(platform),
                new AgentPolicyGround(
                    Worktree,
                    [Main],
                    [Worktree, WorkDirectory],
                    [Slot],
                    [ClaudeDirectory],
                    restrictions ?? [],
                    Web: web,
                    WebDomains: webDomains,
                    Writes: writes,
                    ReporterRoot: reporterRoot,
                    WorktreeRules: worktreeRules,
                    ProtectedPaths: protectedPaths,
                    StateDirectories: stateDirectories));

        public void Dispose()
        {
            _main.Dispose();
            _claude.Dispose();
            _worktree.Dispose();
            _work.Dispose();
        }
    }

    /// <summary>A ground whose paths cannot be resolved, so every decision has to deny.</summary>
    private sealed class BrokenGround : AgentPolicyTree
    {
        public AgentPolicy Policy()
            => new(
                new UnresolvablePaths(new PhysicalFileSystem(FilePermissionsFactory.Create())),
                HostDoubles.Platform(),
                new AgentPolicyGround(
                    Path.Combine(Path.GetTempPath(), "nowhere"),
                    [],
                    [],
                    [],
                    [],
                    []));
    }

    /// <summary>A file system whose links under the ground alone can be read: every other path is undecided, as one is whose links form a cycle.</summary>
    private sealed class UnresolvablePaths(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public override string ResolveLinks(string path)
            => Path.GetFileName(path) == "nowhere" ? path : throw new IOException("the links form a cycle");
    }
}
