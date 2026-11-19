using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
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

    /// <summary>
    /// The tools of a launch by Claude Code's own names: the engine's tool server's - none of Claude Code's own shell or
    /// file tools is ever given.
    /// </summary>
    private static readonly string[] Served = ["mcp__dssharness__view_file", "mcp__dssharness__run_command"];

    /// <summary>
    /// The settings hold the engine's names and nothing else: no name of Claude Code's own shell - no launch is given it -
    /// and the tokens one answer of the engine's tool server may take, a page whole, written as Claude Code reads it.
    /// </summary>
    [Fact]
    public void TheSettingsHoldWhatTheEngineDecidesAndNothingElse()
    {
        var rendered = ClaudeSettings.Render(Served, SettingsGround(Windows, []), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            """
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
                "MAX_MCP_OUTPUT_TOKENS": "98304"
              },
              "permissions": {
                "ask": [
                  "mcp__dssharness__view_file",
                  "mcp__dssharness__run_command"
                ],
                "deny": []
              }
            }
            """.ReplaceLineEndings().TrimEnd(),
            rendered.ReplaceLineEndings().TrimEnd());
    }

    /// <summary>
    /// A launch run inside this tool's isolation turns Claude Code's own sandbox off, written after the output style and
    /// before the environment, so the user's own settings cannot start one that nothing inside could run in; its
    /// environment is every launch's, and no other.
    /// </summary>
    [Fact]
    public void AnIsolatedLaunch_TurnsClaudeCodesOwnSandboxOff()
    {
        var rendered = ClaudeSettings.Render(Served, SettingsGround(Posix, []), StringComparison.Ordinal, isolated: true);
        var whole = JsonNode.Parse(rendered)!;

        Assert.False(whole["sandbox"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(ClaudeSettings.Launched.Select(variable => variable.Key), whole["env"]!.AsObject().Select(variable => variable.Key));
        Assert.Equal(["disableAllHooks", "fallbackModel", "switchModelsOnFlag", "outputStyle", "sandbox", "env", "permissions"], whole.AsObject().Select(entry => entry.Key));
    }

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
            Served,
            PolicyGrounds.Of(worktree, [main], [worktree, work], [slot], [claude], []),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal([slotRule, claudeRule], Deny(rendered));
    }

    /// <summary>
    /// Each place the user keeps credentials in is a read deny rule after the slots' and the Claude Code directories': a
    /// directory for all it holds, a file for itself.
    /// </summary>
    [Theory]
    [InlineData("windows", @"C:\Users\dev", "Read(//c/Users/dev/.ssh/**)", "Read(//c/Users/dev/.netrc)")]
    [InlineData("posix", "/home/dev", "Read(//home/dev/.ssh/**)", "Read(//home/dev/.netrc)")]
    public void EachPlaceTheUserKeepsCredentialsIn_IsAReadDenyRule_ADirectoryForAllItHolds(string system, string home, string directoryRule, string fileRule)
    {
        var (main, worktree, work) = Ground(system);
        var separator = system == "windows" ? @"\" : "/";

        var rendered = ClaudeSettings.Render(
            Served,
            PolicyGrounds.Of(worktree, [main], [worktree, work], [], [], [], credentialPlaces: [new(home + separator + ".ssh", Directory: true), new(home + separator + ".netrc", Directory: false)]),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal([directoryRule, fileRule], Deny(rendered));
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
            Served,
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
            Served,
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
            Served,
            SettingsGround(Ground(system), [new("Write(.orchestrators/**)", AgentPolicySource.Config)]),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal([worktreeEdit, workEdit], Deny(rendered));
    }

    [Fact]
    public void WebRulesAreWebFetchDomainRules_AndTheWebWholeWebSearchBesideIt()
    {
        var domain = ClaudeSettings.Render(["WebFetch", "WebSearch"], SettingsGround(Posix, [new("Web(pastebin.com)", AgentPolicySource.Config)]), StringComparison.Ordinal);
        var whole = ClaudeSettings.Render(["WebFetch", "WebSearch"], SettingsGround(Posix, [new("Web(*)", AgentPolicySource.Config)]), StringComparison.Ordinal);

        Assert.Equal(["WebFetch(domain:pastebin.com)"], Deny(domain));
        Assert.Equal(["WebFetch", "WebSearch"], Deny(whole));
    }
}
