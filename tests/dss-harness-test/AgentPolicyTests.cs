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
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run((IReadOnlyList<string>)["git", "push"]));
        Assert.Equal(AgentPolicyAnswer.Allow, policy.Run("git push origin main"));
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
        var policy = ground.Policy([new("Run(git push *)", AgentPolicySource.Config)]);

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
        var policy = ground.Policy([new("Run(git config *)", AgentPolicySource.Config)]);

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
            "agentPolicy.restrictions[0] 'web(pastebin.com)' is no rule this tool reads: the verbs are Run, Write and Read",
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

        public AgentPolicy Policy(IReadOnlyList<AgentRestriction>? restrictions = null)
            => new(
                new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())),
                HostDoubles.Platform(platform),
                new AgentPolicyGround(
                    Worktree,
                    [Main],
                    [Worktree, WorkDirectory],
                    [Slot],
                    [ClaudeDirectory],
                    restrictions ?? []));

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
