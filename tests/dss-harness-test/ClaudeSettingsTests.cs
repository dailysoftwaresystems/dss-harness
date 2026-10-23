using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Repository;
using Xunit;

namespace DssHarness.Tests;

/// <summary>
/// The launch's settings file for Claude Code, rendered from the same policy the engine answers
/// with: an ask rule for every tool, the restriction rules as deny rules - absolute once for each
/// root in the POSIX form Claude Code matches, a write refused by an Edit rule, none where it would
/// cover a root the launch writes - the engine's env, and the user's hooks off.
/// </summary>
/// <remarks>
/// Every rule is asserted as Claude Code reads it, written out whole for a Windows root and a POSIX
/// root alike: what Claude Code matches is the string, so the string is what is pinned. The roots
/// are spelled as text and never touched, so both spellings are rendered on every system.
/// </remarks>
public sealed class ClaudeSettingsTests
{
    /// <summary>The ground of an agent with a worktree, on a Windows drive: a main checkout it reads, a worktree and a work directory it writes.</summary>
    private static readonly (string Main, string Worktree, string Work) Windows =
        (@"C:\Users\dev\repo", @"C:\Users\dev\worktrees\a1", @"C:\Users\dev\repo\.orchestrators\r1\work\a1");

    /// <summary>The same ground on a POSIX system.</summary>
    private static readonly (string Main, string Worktree, string Work) Posix =
        ("/home/dev/repo", "/home/dev/worktrees/a1", "/home/dev/repo/.orchestrators/r1/work/a1");

    private static (string Main, string Worktree, string Work) Ground(string system) => system == "windows" ? Windows : Posix;

    private static AgentPolicyGround SettingsGround((string Main, string Worktree, string Work) ground, IReadOnlyList<AgentRestriction> restrictions)
        => PolicyGrounds.Of(
            ground.Worktree,
            [ground.Main],
            [ground.Worktree, ground.Work],
            [],
            [],
            restrictions);

    /// <summary>The deny rules of a rendered settings file, in the order they are written.</summary>
    private static List<string> Deny(string rendered)
        => [.. JsonNode.Parse(rendered)!["permissions"]!["deny"]!.AsArray().Select(rule => (string)rule!)];

    [Fact]
    public void TheSettingsHoldWhatTheEngineDecidesAndNothingElse()
    {
        var rendered = ClaudeSettings.Render(
            ["Read", "Grep", "Glob", "Write", "Edit", "Bash"],
            SettingsGround(Windows, []),
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
                "CLAUDE_CODE_RETRY_WATCHDOG": "0",
                "CLAUDE_BASH_MAINTAIN_PROJECT_WORKING_DIR": "1"
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

    /// <summary>
    /// Every shell command of a launch starts in the launch's own directory, never where an earlier one's cd left it: the
    /// policy reads a command's relative paths against that directory, so a cd in one call and a relative path in the
    /// next would reach a place it never decided. Set in the settings' env and the child's environment alike.
    /// </summary>
    [Fact]
    public void EveryShellCommandStartsInTheLaunchsDirectory()
        => Assert.Contains(new KeyValuePair<string, string>("CLAUDE_BASH_MAINTAIN_PROJECT_WORKING_DIR", "1"), ClaudeSettings.Controlled);

    [Theory]
    [InlineData("windows", "Read(//c/Users/dev/repo/.harness-config/workflows/.env/**)", "Read(//c/Users/dev/claude/**)")]
    [InlineData("posix", "Read(//home/dev/repo/.harness-config/workflows/.env/**)", "Read(//home/dev/claude/**)")]
    public void EachCredentialSlotAndClaudeCodeDirectory_IsAReadDenyRule_ThatClaudeCodeAppliesToItsSearches(string system, string slotRule, string claudeRule)
    {
        var (main, worktree, work) = Ground(system);
        var separator = system == "windows" ? @"\" : "/";
        var slot = string.Join(separator, main, ".harness-config", "workflows", ".env");
        var claude = string.Join(separator, main[..main.LastIndexOf(separator, StringComparison.Ordinal)], "claude");

        var rendered = ClaudeSettings.Render(
            ["Read", "Grep"],
            PolicyGrounds.Of(worktree, [main], [worktree, work], [slot], [claude], []),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal([slotRule, claudeRule], Deny(rendered));
    }

    /// <summary>
    /// Each restriction is written once for each root, absolute in the POSIX form Claude Code matches every path in - a
    /// Windows drive a lowercase segment of its own, its colon gone - and a write refused by an Edit rule, which Claude
    /// Code holds its Write, Edit and NotebookEdit tools to: it matches a file tool's path against Read and Edit rules
    /// alone, and a Write rule never.
    /// </summary>
    [Theory]
    [InlineData(
        "windows",
        "Edit(//c/Users/dev/worktrees/a1/.github/**)",
        "Edit(//c/Users/dev/repo/.github/**)",
        "Edit(//c/Users/dev/repo/.orchestrators/r1/work/a1/.github/**)",
        "Read(//c/Users/dev/worktrees/a1/secrets/**)",
        "Read(//c/Users/dev/repo/secrets/**)",
        "Read(//c/Users/dev/repo/.orchestrators/r1/work/a1/secrets/**)")]
    [InlineData(
        "posix",
        "Edit(//home/dev/worktrees/a1/.github/**)",
        "Edit(//home/dev/repo/.github/**)",
        "Edit(//home/dev/repo/.orchestrators/r1/work/a1/.github/**)",
        "Read(//home/dev/worktrees/a1/secrets/**)",
        "Read(//home/dev/repo/secrets/**)",
        "Read(//home/dev/repo/.orchestrators/r1/work/a1/secrets/**)")]
    public void TheRestrictionsAreDenyRules_AbsoluteOnceForEachRoot(
        string system,
        string worktreeEdit,
        string mainEdit,
        string workEdit,
        string worktreeRead,
        string mainRead,
        string workRead)
    {
        var rendered = ClaudeSettings.Render(
            ["Bash", "Read", "Write", "Edit"],
            SettingsGround(
                Ground(system),
                [
                    new("Run(git push *)", AgentPolicySource.Config),
                    new("Run(git config)", AgentPolicySource.Config),
                    new("Write(.github/**)", AgentPolicySource.Config),
                    new("Read(secrets/**)", AgentPolicySource.Agent),
                ]),
            StringComparison.OrdinalIgnoreCase);

        // The working directory first, then the main checkout, then the work directory: the one list of roots, in its
        // order.
        Assert.Equal(["Bash(git push:*)", "Bash(git config)", worktreeEdit, mainEdit, workEdit, worktreeRead, mainRead, workRead], Deny(rendered));
    }

    /// <summary>
    /// The working directory is a root of the launch's though no list of roots names it: a rule is written for it as for
    /// each root the policy decides over, from the one list of them.
    /// </summary>
    [Theory]
    [InlineData("windows", "Read(//c/Users/dev/worktrees/a1/secrets/**)", "Read(//c/Users/dev/repo/secrets/**)")]
    [InlineData("posix", "Read(//home/dev/worktrees/a1/secrets/**)", "Read(//home/dev/repo/secrets/**)")]
    public void TheWorkingDirectory_IsARootTheRulesAreWrittenFor_ThoughNoListNamesIt(string system, string worktreeRead, string mainRead)
    {
        var (main, worktree, _) = Ground(system);
        var rendered = ClaudeSettings.Render(
            ["Read"],
            PolicyGrounds.Of(worktree, [main], [], [], [], [new("Read(secrets/**)", AgentPolicySource.Config)]),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal([worktreeRead, mainRead], Deny(rendered));
    }

    /// <summary>
    /// The main checkout's .orchestrators/** holds the agent's own work directory: Claude Code's deny rules know no
    /// exception, so the engine holds that rule alone - not written for the main checkout, written for the worktree and the
    /// work directory, where nothing of it is written.
    /// </summary>
    [Theory]
    [InlineData("windows", "Edit(//c/Users/dev/worktrees/a1/.orchestrators/**)", "Edit(//c/Users/dev/repo/.orchestrators/r1/work/a1/.orchestrators/**)")]
    [InlineData("posix", "Edit(//home/dev/worktrees/a1/.orchestrators/**)", "Edit(//home/dev/repo/.orchestrators/r1/work/a1/.orchestrators/**)")]
    public void ARuleThatWouldCoverARootTheLaunchWrites_IsNotWrittenAsADenyRule(string system, string worktreeEdit, string workEdit)
    {
        var rendered = ClaudeSettings.Render(
            ["Bash"],
            SettingsGround(Ground(system), [new("Write(.orchestrators/**)", AgentPolicySource.Config)]),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal([worktreeEdit, workEdit], Deny(rendered));
    }

    [Fact]
    public void WebRulesAreWebFetchDomainRules_AndTheWebWholeWebSearchBesideIt()
    {
        var domain = ClaudeSettings.Render(["Bash"], SettingsGround(Posix, [new("Web(pastebin.com)", AgentPolicySource.Config)]), StringComparison.Ordinal);
        var whole = ClaudeSettings.Render(["Bash"], SettingsGround(Posix, [new("Web(*)", AgentPolicySource.Config)]), StringComparison.Ordinal);

        Assert.Equal(["WebFetch(domain:pastebin.com)"], Deny(domain));
        Assert.Equal(["WebFetch", "WebSearch"], Deny(whole));
    }
}
