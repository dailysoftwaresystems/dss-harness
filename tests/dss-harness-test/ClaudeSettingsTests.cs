using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using Xunit;

namespace DssHarness.Tests;

/// <summary>
/// The launch's settings file for Claude Code, rendered from the same policy the engine answers
/// with: an ask rule for every tool, the restriction rules as deny rules - absolute once for each
/// root, none where it would cover a root the launch writes - the engine's env, and the user's
/// hooks off.
/// </summary>
public sealed class ClaudeSettingsTests
{
    /// <summary>The ground of an agent with a worktree: a main checkout it reads, a worktree and a work directory it writes.</summary>
    private static (string Main, string Worktree, string Work) Ground()
    {
        var main = Path.Combine(Path.GetTempPath(), "dssharness-main", "repo");
        var worktree = Path.Combine(Path.GetTempPath(), "dssharness-main", "worktrees", "a1");
        var work = Path.Combine(Path.GetTempPath(), "dssharness-main", "repo", ".orchestrators", "r1", "work", "a1");

        return (main, worktree, work);
    }

    private static AgentPolicyGround SettingsGround((string Main, string Worktree, string Work) ground, IReadOnlyList<AgentRestriction> restrictions)
        => new(
            ground.Worktree,
            [ground.Main],
            [ground.Worktree, ground.Work],
            [],
            [],
            restrictions);

    /// <summary>A root as Claude Code's own absolute rules spell it: two slashes whatever the system spells beneath them.</summary>
    private static string Absolute(string root)
        => (root.StartsWith('/') ? "/" : "//") + root.Replace('\\', '/').TrimEnd('/');

    [Fact]
    public void TheSettingsHoldWhatTheEngineDecidesAndNothingElse()
    {
        var (main, worktree, work) = Ground();
        var rendered = ClaudeSettings.Render(
            ["Read", "Grep", "Glob", "Write", "Edit", "Bash"],
            SettingsGround((main, worktree, work), []),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            $$"""
            {
              "disableAllHooks": true,
              "fallbackModel": [],
              "switchModelsOnFlag": false,
              "outputStyle": "default",
              "env": {
                "DISABLE_COMPACT": "1",
                "DISABLE_AUTOUPDATER": "1",
                "CLAUDE_CODE_SUBPROCESS_ENV_SCRUB": "1",
                "CLAUDE_CODE_RETRY_WATCHDOG": "0"
              },
              "permissions": {
                "ask": [
                  "Read",
                  "Grep",
                  "Glob",
                  "Write",
                  "Edit",
                  "Bash"
                ],
                "deny": []
              }
            }
            """.ReplaceLineEndings().TrimEnd(),
            rendered.ReplaceLineEndings().TrimEnd());
    }

    [Fact]
    public void EachCredentialSlotAndClaudeCodeDirectory_IsAReadDenyRule_ThatClaudeCodeAppliesToItsSearches()
    {
        var (main, worktree, work) = Ground();
        var slot = Path.Combine(main, ".harness-config", "workflows", ".env");
        var claude = Path.Combine(Path.GetTempPath(), "dssharness-main", "claude");

        var rendered = ClaudeSettings.Render(
            ["Read", "Grep"],
            new AgentPolicyGround(worktree, [main], [worktree, work], [slot], [claude], []),
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains($"\"Read({Absolute(slot)}/**)\"", rendered, StringComparison.Ordinal);
        Assert.Contains($"\"Read({Absolute(claude)}/**)\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRestrictionsAreDenyRules_AbsoluteOnceForEachRoot()
    {
        var (main, worktree, work) = Ground();
        var rendered = ClaudeSettings.Render(
            ["Bash", "Read", "Write"],
            SettingsGround(
                (main, worktree, work),
                [
                    new("Run(git push *)", AgentPolicySource.Config),
                    new("Run(git config)", AgentPolicySource.Config),
                    new("Write(.github/**)", AgentPolicySource.Config),
                    new("Read(secrets/**)", AgentPolicySource.Agent),
                ]),
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains("\"Bash(git push:*)\"", rendered, StringComparison.Ordinal);
        Assert.Contains("\"Bash(git config)\"", rendered, StringComparison.Ordinal);

        // Once for each root the launch has, the worktree and the main checkout alike, written
        // absolute in the one grammar Claude Code's own deny rules read.
        Assert.Contains($"\"Write({Absolute(worktree)}/.github/**)\"", rendered, StringComparison.Ordinal);
        Assert.Contains($"\"Write({Absolute(main)}/.github/**)\"", rendered, StringComparison.Ordinal);
        Assert.Contains($"\"Read({Absolute(main)}/secrets/**)\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleThatWouldCoverARootTheLaunchWrites_IsNotWrittenAsADenyRule()
    {
        var (main, worktree, work) = Ground();
        var rendered = ClaudeSettings.Render(
            ["Bash"],
            SettingsGround((main, worktree, work), [new("Write(.orchestrators/**)", AgentPolicySource.Config)]),
            StringComparison.OrdinalIgnoreCase);

        // The main checkout's .orchestrators/** holds the agent's own work directory: Claude
        // Code's deny rules know no exception, so the engine holds that rule alone - not written
        // for the main checkout, written for the worktree, where nothing of it is written.
        Assert.DoesNotContain($"\"Write({Absolute(main)}/.orchestrators/**)\"", rendered, StringComparison.Ordinal);
        Assert.Contains($"\"Write({Absolute(worktree)}/.orchestrators/**)\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void WebRulesAreWebFetchDomainRules_AndTheWebWholeWebSearchBesideIt()
    {
        var (main, worktree, work) = Ground();
        var domain = ClaudeSettings.Render(["Bash"], SettingsGround((main, worktree, work), [new("Web(pastebin.com)", AgentPolicySource.Config)]), StringComparison.OrdinalIgnoreCase);
        var whole = ClaudeSettings.Render(["Bash"], SettingsGround((main, worktree, work), [new("Web(*)", AgentPolicySource.Config)]), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("\"WebFetch(domain:pastebin.com)\"", domain, StringComparison.Ordinal);
        Assert.Contains("\"WebFetch\"", whole, StringComparison.Ordinal);
        Assert.Contains("\"WebSearch\"", whole, StringComparison.Ordinal);
    }
}
