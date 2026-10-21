using System.Text.Json;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;
using Xunit;

namespace DssHarness.Tests;

public sealed class ClaudeCodeUpdateServiceTests
{
    /// <summary>
    /// A ready installation is said by the version its program answered - read from the line Claude Code really prints,
    /// <c>2.1.296 (Claude Code)</c>, and never compared as that text - and nothing is changed.
    /// </summary>
    [Fact]
    public async Task AReadyInstallation_IsSaidInOneLine_AndNothingIsChanged()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal($"claude 2.1.296, installed with npm: '{kit.Program}'", answer.Message);
        Assert.Empty(answer.Details);
        Assert.Equal(
            [$"{kit.Node} {kit.PrefixScript}", $"{kit.Node} {kit.RootScript} root -g", $"{kit.Program} --version"],
            kit.Runner.Ran);
        Assert.Equal(0, kit.Table.Reads);
    }

    /// <summary>
    /// A ready installation is left alone whatever else holds: a session of it running, installing and updating turned
    /// off either way, another update holding npm's place. Each of those stands only in the way of a change, and a run
    /// refused for one while Claude Code is open elsewhere would refuse every run.
    /// </summary>
    [Theory]
    [InlineData("session")]
    [InlineData("update-false")]
    [InlineData("DISABLE_UPDATES")]
    [InlineData("lock")]
    public async Task AReadyInstallation_IsReady_WhateverStandsInTheWayOfAChange(string standing)
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296", disableUpdates: standing == "DISABLE_UPDATES");
        kit.Table = new ScriptedTable([[new(4242, 1, "claude", null, kit.Program, kit.Program)]]);
        using var held = standing == "lock" ? new ProgramUpdate(kit.Runner, kit.Platform).TryHold(kit.Root) : null;

        var answer = await kit.Service().ReadyAsync(
            kit.Layout,
            [],
            client: standing == "update-false" ? new AgentClientConfig { Update = false } : null,
            steps: string.Empty,
            Token);

        Assert.True(answer.Ready);
        Assert.Equal($"claude 2.1.296, installed with npm: '{kit.Program}'", answer.Message);
        Assert.Equal(0, kit.Table.Reads);
    }

    [Fact]
    public async Task NoNpm_IsRefusedWithWhatWasLookedFor()
    {
        using var kit = new Kit(npm: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: " (steps: plan, plan-audit)", Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            "this run needs a Claude model through the client 'claude-code' (steps: plan, plan-audit), and no npm is on the PATH. "
            + "Looked for: npm.exe and npm.cmd, and the node.exe and npm-cli.js npm.cmd starts. This tool installs Claude Code "
            + "and brings it up to date with npm. Install Node.js 22 or later, which brings npm, and start the run again",
            answer.Message);
    }

    /// <summary>
    /// On Linux and macOS the refusal of a run with no npm says what was looked for there - npm, and nothing of the
    /// Windows batch file and what it starts.
    /// </summary>
    [Theory]
    [InlineData(PlatformId.Linux)]
    [InlineData(PlatformId.MacOs)]
    public async Task NoNpm_OnLinuxAndMacOs_SaysItLookedForNpm(PlatformId system)
    {
        using var kit = new Kit(npm: false, platform: system);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            "this run needs a Claude model through the client 'claude-code', and no npm is on the PATH. Looked for: npm. "
            + "This tool installs Claude Code and brings it up to date with npm. Install Node.js 22 or later, which brings npm, "
            + "and start the run again",
            answer.Message);
    }

    /// <summary>
    /// Where the PATH gives npm.cmd and node is nowhere, the refusal says that - npm.cmd is on the PATH, and nothing can
    /// start what it starts - never that no npm is on the PATH.
    /// </summary>
    [Fact]
    public async Task AnNpmCmdWithNoNodeAnywhere_IsRefusedSayingSo()
    {
        using var kit = new Kit(package: null);
        File.Delete(kit.Node);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"this run needs a Claude model through the client 'claude-code', and '{kit.NpmCmd}' is on the PATH, but no node.exe "
            + "is beside it or on the PATH to start what it starts. Looked for: npm.exe and npm.cmd, and the node.exe and "
            + "npm-cli.js npm.cmd starts. This tool installs Claude Code and brings it up to date with npm. Install Node.js 22 "
            + "or later, which brings npm, and start the run again",
            answer.Message);
        Assert.Empty(kit.Runner.Ran);
    }

    /// <summary>
    /// npm with no Claude Code installs it, says so in one line, and reads no process table: an installation where there
    /// was none meets no session of it, and a claude of any other installation is not one.
    /// </summary>
    [Fact]
    public async Task NpmWithNoClaudeCode_InstallsIt_AndSaysSoInOneLine()
    {
        using var kit = new Kit(package: null);
        kit.Table = new ScriptedTable([[new(4242, 1, "claude", null, "claude", @"C:\Users\someone\.local\bin\claude.exe")]]);
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal($"npm has no Claude Code installed: installed 2.1.287 with '{ClaudeCodeUpdateService.InstallCommand()}'", answer.Message);
        Assert.Contains($"{kit.Node} {kit.RootScript} install -g @anthropic-ai/claude-code@{ClaudeCodeUpdateService.Needed}", kit.Runner.Ran);
        Assert.Equal(0, kit.Table.Reads);
    }

    /// <summary>The client's entry saying nothing of update is installing and updating on: the one default, the service's own.</summary>
    [Fact]
    public async Task AClientEntryThatSaysNothingOfUpdate_InstallsAndUpdates()
    {
        using var kit = new Kit(package: null);
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: new AgentClientConfig(), steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal($"npm has no Claude Code installed: installed 2.1.287 with '{ClaudeCodeUpdateService.InstallCommand()}'", answer.Message);
    }

    /// <summary>What the client's update key comes to: on where no entry or no key says, and as the key says otherwise.</summary>
    [Fact]
    public void TheUpdateKey_IsOnWhereNothingIsSaid()
    {
        Assert.True(ClaudeCodeUpdateService.UpdatesOn(null));
        Assert.True(ClaudeCodeUpdateService.UpdatesOn(new AgentClientConfig()));
        Assert.True(ClaudeCodeUpdateService.UpdatesOn(new AgentClientConfig { Update = true }));
        Assert.False(ClaudeCodeUpdateService.UpdatesOn(new AgentClientConfig { Update = false }));
    }

    [Fact]
    public async Task AClaudeCodeOlderThanNeeded_IsBroughtUpToDate_AndSaysSoInOneLine()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal($"claude 2.1.201 is older than this tool needs ({ClaudeCodeUpdateService.Needed}): updated with '{ClaudeCodeUpdateService.InstallCommand()}'; it now says 2.1.287", answer.Message);
    }

    /// <summary>
    /// The change is bounded by the ten minutes an install is given, and the readings around it by their own half
    /// minute: a change bounded by a reading's budget would end every install on a slow connection.
    /// </summary>
    [Fact]
    public async Task TheChange_IsBoundedByTheInstallsBudget()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");

        await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(
            [NpmClient.AskBudget, NpmClient.AskBudget, ToolPackage.ChangeBudget, NpmClient.AskBudget, ClaudeCodeInstall.AskBudget],
            kit.Runner.Requests.Select(request => request.Timeout));
    }

    /// <summary>
    /// Every claude this tool starts - a package's program and a program a person named alike - is started with Claude
    /// Code's own background update off, so only this tool moves an installation.
    /// </summary>
    [Fact]
    public async Task EveryClaudeStarted_HasItsOwnUpdaterTurnedOff()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");
        await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);
        var named = kit.Temp.WriteFile("mine.exe", "a program");
        kit.Temp.WriteFile(".harness-config/workflows/.env/named.env", $"CLAUDE_EXECUTABLE={named}\n");
        kit.Runner.Versions[$"{named} --version"] = Says("2.1.296");
        await kit.Service().ReadyAsync(kit.Layout, [Named()], client: null, steps: string.Empty, Token);

        var started = kit.Runner.Requests.Where(request => request.Arguments is ["--version"]).ToList();

        Assert.Equal([kit.Program, named], started.Select(request => request.FileName));
        Assert.All(started, request => Assert.Equal("1", request.Environment["DISABLE_AUTOUPDATER"]));
    }

    [Fact]
    public async Task OnceTheChangeHasBegun_NothingStopsIt()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");

        await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        // The readings before the change take the token; the change, its reading back and the program's own version
        // take none.
        Assert.Equal([true, true, false, false, false], kit.Runner.Stoppable);
    }

    /// <summary>A session whose program is the installation's own refuses the change, named by its id.</summary>
    [Fact]
    public async Task ASessionOfTheInstallation_RefusesTheChange_NamedByItsId()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Table = new ScriptedTable([[new(4242, 1, "claude", null, "claude --resume", kit.Program), new(9000, 1, "dotnet", null, null)]]);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.Refused, answer.ExitCode);
        Assert.Equal("a session of Claude Code is running on this machine (pid 4242); start the run again when they have ended.", answer.Message);
        Assert.DoesNotContain(kit.Runner.Ran, line => line.Contains(" install ", StringComparison.Ordinal));
    }

    /// <summary>
    /// An older installation started by node - Claude Code before its native program - is a session of it too: node
    /// running the package's script refuses the change, though no process is called claude.
    /// </summary>
    [Fact]
    public async Task NodeRunningTheInstallationsScript_RefusesTheChange()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var script = Path.Combine(kit.Root, "@anthropic-ai", "claude-code", "cli.js");
        kit.Table = new ScriptedTable([[new(5150, 1, "node", null, $"\"{kit.Node}\" \"{script}\" --continue", kit.Node)]]);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.Refused, answer.ExitCode);
        Assert.Equal("a session of Claude Code is running on this machine (pid 5150); start the run again when they have ended.", answer.Message);
    }

    /// <summary>
    /// A path on a session's command line is followed through its links: npm's launcher on Linux and macOS is a link into
    /// the package, and node started through it names the link, never the package.
    /// </summary>
    [Fact]
    public async Task ASessionStartedThroughALinkIntoThePackage_RefusesTheChange()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var link = Path.Combine(kit.Temp.Path, "launchers");
        TestLinks.OrSkip(() => TestLinks.DirectoryLink(link, Path.Combine(kit.Root, "@anthropic-ai", "claude-code")));
        kit.Table = new ScriptedTable([[new(6060, 1, "node", null, $"node {Path.Combine(link, "cli.js")}", null)]]);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.Refused, answer.ExitCode);
        Assert.Equal("a session of Claude Code is running on this machine (pid 6060); start the run again when they have ended.", answer.Message);
    }

    /// <summary>
    /// A claude of another installation - a native launcher, Homebrew's, WinGet's - is no session of npm's: npm's change
    /// never touches it, and the update goes ahead beside it.
    /// </summary>
    [Fact]
    public async Task AClaudeOfAnotherInstallation_DoesNotStopTheUpdate()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var native = kit.Temp.WriteFile(".local/bin/claude.exe", "a program");
        kit.Table = new ScriptedTable([[new(4242, 1, "claude", null, $"\"{native}\"", native)]]);
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal(1, kit.Table.Reads);
    }

    /// <summary>
    /// A claude whose program the table could not read - another user's, or a table read by names alone - cannot be told
    /// apart from a session of the installation, and is counted as one, said as that: a change beside a session takes
    /// its program from under it.
    /// </summary>
    [Fact]
    public async Task AClaudeWhoseProgramCouldNotBeRead_RefusesTheChange_SaidAsThat()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Table = new ScriptedTable([[new(7070, 1, "claude", null, null), new(7071, 1, "node", null, null)]]);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.Refused, answer.ExitCode);
        Assert.Equal(
            "a session of Claude Code is running on this machine (pid 7070 (claude, whose program could not be read)); start the run again when they have ended.",
            answer.Message);
        Assert.DoesNotContain(kit.Runner.Ran, line => line.Contains(" install ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondUpdateOfNpmPlace_RefusedAtOnce()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        using var held = new ProgramUpdate(kit.Runner, kit.Platform).TryHold(kit.Root);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.Refused, answer.ExitCode);
        Assert.Equal($"another update is changing npm's '{kit.Root}' on this machine; run again once it has ended.", answer.Message);
    }

    [Fact]
    public async Task TheUpdateKeyOff_RefusesWithTheCommandToRunByHand()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: new AgentClientConfig { Update = false }, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"installing and updating Claude Code are turned off (the client's update key is false); npm holds claude 2.1.201, "
            + $"and this tool needs {ClaudeCodeUpdateService.Needed}. Run it by hand: {ClaudeCodeUpdateService.InstallCommand()}",
            answer.Message);
    }

    [Fact]
    public async Task DISABLE_UPDATES_RefusesWithTheCommandToRunByHand()
    {
        using var kit = new Kit(package: null, disableUpdates: true);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"installing and updating Claude Code are turned off ({ClaudeCodeUpdateService.UpdatesVariable} is set in this tool's own environment); "
            + $"npm holds no Claude Code, and this tool needs {ClaudeCodeUpdateService.Needed}. Run it by hand: {ClaudeCodeUpdateService.InstallCommand()}",
            answer.Message);
    }

    [Fact]
    public async Task APackageWithNoBinClaude_IsRefusedAsNoneThisToolCanStart()
    {
        using var kit = new Kit(package: null);
        kit.WritePackage("2.1.296", "2.1.296", bin: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{kit.Manifest}' names no program under bin.claude: this is not a Claude Code this tool can start. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand()}",
            answer.Message);
    }

    /// <summary>A package.json that does not read is refused with what stopped the reading, never as one that names no program.</summary>
    [Fact]
    public async Task APackageJsonThatDoesNotRead_IsRefusedWithWhatStoppedIt()
    {
        using var kit = new Kit(package: null);
        const string broken = "{ \"version\": \"2.1.296\", ";
        kit.Temp.WriteFile(Path.GetRelativePath(kit.Temp.Path, kit.Manifest).Replace('\\', '/'), broken);
        var stopped = Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(broken)).Message.TrimEnd('.');

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{kit.Manifest}' could not be read ({stopped}): this is not a Claude Code this tool can start. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand()}",
            answer.Message);
    }

    /// <summary>
    /// A package.json that reads but says no version, or holds no object at all, is refused by its sentence - never taken
    /// for no installation and installed over, and never a crash.
    /// </summary>
    [Theory]
    [InlineData("{ \"bin\": { \"claude\": \"bin/claude.exe\" } }", "names no version")]
    [InlineData("{ \"version\": \"latest\", \"bin\": { \"claude\": \"bin/claude.exe\" } }", "names version 'latest', which is no version this tool reads")]
    [InlineData("[]", "could not be read (it holds a JSON array, not an object)")]
    public async Task APackageJsonThatSaysNoVersion_IsRefusedBySentence(string manifest, string why)
    {
        using var kit = new Kit(package: null);
        kit.Temp.WriteFile(Path.GetRelativePath(kit.Temp.Path, kit.Manifest).Replace('\\', '/'), manifest);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{kit.Manifest}' {why}: this is not a Claude Code this tool can start. Install it again: {ClaudeCodeUpdateService.InstallCommand()}",
            answer.Message);
        Assert.DoesNotContain(kit.Runner.Ran, line => line.Contains(" install ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProgramThatWouldNotAnswerItsVersion_IsRefused()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296");
        kit.Runner.Versions.Clear();

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{kit.Program}' would not answer its own --version, so this tool cannot start it: it printed nothing. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand()}; an installation whose step after the install did not run "
            + "(npm install --ignore-scripts) is the usual cause.",
            answer.Message);
    }

    /// <summary>A program whose --version failed is refused with its exit code and its own words.</summary>
    [Fact]
    public async Task AProgramWhoseVersionFailed_IsRefusedWithItsExitAndItsWords()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296");
        kit.Runner.Versions[$"{kit.Program} --version"] = new ProcessResult(3, string.Empty, "error: the platform's binary is missing\n", TimeSpan.Zero, TimedOut: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(
            $"'{kit.Program}' would not answer its own --version, so this tool cannot start it: asking it failed (exit 3): "
            + "error: the platform's binary is missing. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand()}; an installation whose step after the install did not run "
            + "(npm install --ignore-scripts) is the usual cause.",
            answer.Message);
    }

    [Fact]
    public async Task NpmRanAndClaudeIsStillOlder_IsRefusedNamingTheCauses()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"npm ran, and Claude Code is still missing or older than this tool needs: npm holds claude 2.1.201, and this tool needs {ClaudeCodeUpdateService.Needed}. "
            + $"The command that ran: '{ClaudeCodeUpdateService.InstallCommand()}'. "
            + "A Node.js older than 22, which the package asks for, and an installation whose step after the install did not run (npm install --ignore-scripts) are the causes.",
            answer.Message);
        Assert.Equal(["added 1 package in 4s"], answer.Details);
    }

    /// <summary>An install that left a package naming no program is refused by the package's sentence, never a crash.</summary>
    [Fact]
    public async Task AnInstallThatLeftNoBinClaude_IsRefusedAsNoneThisToolCanStart()
    {
        using var kit = new Kit(package: null);
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287", bin: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{kit.Manifest}' names no program under bin.claude: this is not a Claude Code this tool can start. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand()}",
            answer.Message);
        Assert.Equal(["added 1 package in 4s"], answer.Details);
    }

    /// <summary>
    /// An install whose program then does not answer is refused as that - never as one still missing or older, which
    /// its package is not.
    /// </summary>
    [Fact]
    public async Task AnInstallWhoseProgramDoesNotAnswer_IsRefusedAsThat()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", answered: null);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"npm ran, and the Claude Code it holds does not answer: its package says 2.1.287, and its program '{kit.Program}' "
            + $"would not answer its --version (it printed nothing). The command that ran: '{ClaudeCodeUpdateService.InstallCommand()}'. "
            + "A Node.js older than 22, which the package asks for, and an installation whose step after the install did not run (npm install --ignore-scripts) are the causes.",
            answer.Message);
    }

    /// <summary>An install whose program still says an older version is refused naming both, though its package says the new one.</summary>
    [Fact]
    public async Task AnInstallWhoseProgramIsStillOlder_IsRefusedNamingBoth()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.201");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"npm ran, and Claude Code is still older than this tool needs: its package says 2.1.287, its program '{kit.Program}' "
            + $"says 2.1.201, and this tool needs {ClaudeCodeUpdateService.Needed}. The command that ran: '{ClaudeCodeUpdateService.InstallCommand()}'. "
            + "A Node.js older than 22, which the package asks for, and an installation whose step after the install did not run (npm install --ignore-scripts) are the causes.",
            answer.Message);
    }

    [Fact]
    public async Task AProgramWhoseVersionDisagreesWithItsPackage_IsRefused()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.300");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{kit.Program}' says it is claude 2.1.300 and its package says 2.1.296, and the two do not agree. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand()}; an installation whose step after the install did not run "
            + "(npm install --ignore-scripts) is the usual cause.",
            answer.Message);
    }

    [Fact]
    public async Task NpmThatWouldNotSayItsRoot_IsRefused()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296");
        kit.Runner.Root = null;

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"npm was found but would not say where its global packages are ('npm root -g' printed nothing), so Claude Code could not be looked for. "
            + $"{ClaudeCodeUpdateService.InstallCommand()} by hand installs the one this tool needs ({ClaudeCodeUpdateService.Needed}).",
            answer.Message);
    }

    /// <summary>npm failing to say its root is refused with npm's exit code and its own words.</summary>
    [Fact]
    public async Task NpmFailingToSayItsRoot_IsRefusedWithItsExitAndItsWords()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296");
        kit.Runner.RootAnswers.Enqueue(new ProcessResult(1, string.Empty, "npm ERR! Cannot find module 'semver'\n", TimeSpan.Zero, TimedOut: false));

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(
            "npm was found but would not say where its global packages are ('npm root -g' failed (exit 1): npm ERR! Cannot find module 'semver'), "
            + $"so Claude Code could not be looked for. {ClaudeCodeUpdateService.InstallCommand()} by hand installs the one this tool needs ({ClaudeCodeUpdateService.Needed}).",
            answer.Message);
    }

    /// <summary>
    /// The registry or the download not reached is the network's, in each of the ways npm says it, and running it again
    /// can help; what npm holds since is said under it.
    /// </summary>
    [Theory]
    [InlineData("npm ERR! network request to https://registry.npmjs.org failed, reason: getaddrinfo ENOTFOUND registry.npmjs.org")]
    [InlineData("npm ERR! code ECONNRESET")]
    [InlineData("npm ERR! code ETIMEDOUT")]
    [InlineData("npm ERR! network This is a problem related to network connectivity.")]
    public async Task ARegistryNotReached_IsRefusedAsIncomplete_AndTryingAgainCanHelp(string said)
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.Install = new ProcessResult(1, string.Empty, said + "\n", TimeSpan.FromSeconds(3), TimedOut: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.Incomplete, answer.ExitCode);
        Assert.Equal(
            $"npm reached neither the registry nor the download (the install was not reached (exit 1): {said}); running it again can help. "
            + $"Run it by hand: {ClaudeCodeUpdateService.InstallCommand()}.",
            answer.Message);
        Assert.Equal([$"npm holds claude 2.1.201 since, and its program '{kit.Program}' answers 2.1.201.", said], answer.Details);
    }

    /// <summary>
    /// An install that did not finish within its ten minutes is said as that - never as a network that was not reached,
    /// since a step after the install that hangs ends the same way - and what npm holds since is said under it.
    /// </summary>
    [Fact]
    public async Task AnInstallThatDidNotFinishInTime_IsSaidAsThat()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.Install = new ProcessResult(-1, "running postinstall\n", string.Empty, ToolPackage.ChangeBudget, TimedOut: true);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.Incomplete, answer.ExitCode);
        Assert.Equal(
            "npm did not finish within 10 minutes and was ended, part way through the download or the step after the install; "
            + $"running it again can help. Run it by hand: {ClaudeCodeUpdateService.InstallCommand()}.",
            answer.Message);
        Assert.Equal([$"npm holds claude 2.1.201 since, and its program '{kit.Program}' answers 2.1.201.", "running postinstall"], answer.Details);
    }

    /// <summary>
    /// A failed install that left a package whose program does not answer is said to have left half an installation:
    /// the command never stays silent on which of the two a failure left.
    /// </summary>
    [Fact]
    public async Task AFailedInstallThatLeftHalfAnInstallation_SaysSo()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", answered: null);
        kit.Runner.Install = new ProcessResult(1, string.Empty, "npm ERR! code ENOTEMPTY\n", TimeSpan.FromSeconds(3), TimedOut: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"npm could not change what it holds (the install failed (exit 1): npm ERR! code ENOTEMPTY). Run it by hand: {ClaudeCodeUpdateService.InstallCommand()}.",
            answer.Message);
        Assert.Equal(
            [
                $"npm holds claude 2.1.287 since, and its program '{kit.Program}' would not answer its --version (it printed nothing): the change left half an installation.",
                "npm ERR! code ENOTEMPTY",
            ],
            answer.Details);
    }

    /// <summary>A failed first install that left nothing says so, and one whose reading since failed says that it is not known, in npm's words.</summary>
    [Fact]
    public async Task AFailedInstall_SaysWhatNpmHoldsSince_OrThatItIsNotKnown()
    {
        using var kit = new Kit(package: null);
        kit.Runner.Install = new ProcessResult(1, string.Empty, "npm ERR! code E404\n", TimeSpan.FromSeconds(3), TimedOut: false);

        var nothing = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        kit.Runner.RootAnswers.Enqueue(new ProcessResult(0, kit.Root + "\n", string.Empty, TimeSpan.Zero, TimedOut: false));
        kit.Runner.RootAnswers.Enqueue(new ProcessResult(1, string.Empty, "npm ERR! EPERM\n", TimeSpan.Zero, TimedOut: false));
        var unknown = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.Equal(["npm holds no Claude Code since.", "npm ERR! code E404"], nothing.Details);
        Assert.Equal(["What npm holds since is not known: 'npm root -g' failed (exit 1): npm ERR! EPERM.", "npm ERR! code E404"], unknown.Details);
    }

    [Fact]
    public async Task AProgramTheSlotNames_ThatWouldNotAnswer_IsRefused()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var named = kit.Temp.WriteFile("mine.exe", "a program");
        kit.Temp.WriteFile(".harness-config/workflows/.env/named.env", $"CLAUDE_EXECUTABLE={named}\n");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named()], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{named}', the program the slot names, would not answer its --version: it printed nothing. "
            + $"Update it by hand, or take {ClaudeCodeUpdateService.ExecutableVariable} out of the slot",
            answer.Message);
    }

    [Fact]
    public async Task ASlotThatDoesNotRead_IsRefusedBeforeNpmIsAsked()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var slot = Path.Combine(kit.Temp.Path, ".harness-config", "workflows", ".env");
        kit.Temp.WriteFile(".harness-config/workflows/.env/keys.env", "CLAUDE_EXECUTABLE=one\n");
        kit.Temp.WriteFile(".harness-config/workflows/.env/work.env", "CLAUDE_EXECUTABLE=two\n");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named()], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ConfigInvalid, answer.ExitCode);
        Assert.Equal(
            $"'CLAUDE_EXECUTABLE' is declared in '{Path.Combine(slot, "keys.env")}' and in '{Path.Combine(slot, "work.env")}': a name is declared once",
            answer.Message);
        Assert.Empty(kit.Runner.Ran);
    }

    /// <summary>
    /// A program the slot names that the slot will not give - its file one other users can change - is refused (12) as
    /// the slot refuses it; npm is never asked, and npm's Claude Code, which the person did not name, never started.
    /// </summary>
    [Fact]
    public async Task AProgramTheSlotNamesButWillNotGive_IsRefused_AndNpmIsNeverAsked()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296");
        kit.Temp.WriteFile(".harness-config/workflows/.env/named.env", "CLAUDE_EXECUTABLE=mine.exe\n");
        var file = Path.Combine(kit.Temp.Path, ".harness-config", "workflows", ".env", "named.env");
        kit.Permissions.OthersCanWrite = true;

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named()], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ConfigInvalid, answer.ExitCode);
        Assert.Equal(
            $"other users can change '{file}', and whoever can change it can point a run at another key; {ItemEnvironment.HowToProtect(kit.Platform, file)}",
            answer.Message);
        Assert.Empty(kit.Runner.Ran);
    }

    [Fact]
    public async Task NpmRefusedForNoRightToWrite_SaysTheToolNeverRunsSudo()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.Install = new ProcessResult(1, string.Empty, "EACCES: permission denied, access '/usr/lib/node_modules'\n", TimeSpan.FromSeconds(3), TimedOut: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            "npm could not change what it holds (the install failed (exit 1): EACCES: permission denied, access '/usr/lib/node_modules'); "
            + $"this tool never runs sudo. Run it by hand: {ClaudeCodeUpdateService.InstallCommand()}.",
            answer.Message);
        Assert.Equal(
            [$"npm holds claude 2.1.201 since, and its program '{kit.Program}' answers 2.1.201.", "EACCES: permission denied, access '/usr/lib/node_modules'"],
            answer.Details);
    }

    [Fact]
    public async Task AProgramTheSlotNames_IsReadAndNeverMoved()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var named = kit.Temp.WriteFile("mine.exe", "a program");
        kit.Temp.WriteFile(".harness-config/workflows/.env/named.env", $"CLAUDE_EXECUTABLE={named}\n");
        kit.Runner.Versions[$"{named} --version"] = Says("2.1.296");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named()], client: null, steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal($"claude 2.1.296, named by the slot: '{named}'", answer.Message);
        Assert.Equal([$"{named} --version"], kit.Runner.Ran);
    }

    [Fact]
    public async Task AProgramTheSlotNames_OlderThanNeeded_IsRefusedWithTheTwoWaysOn()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var named = kit.Temp.WriteFile("mine.exe", "a program");
        kit.Temp.WriteFile(".harness-config/workflows/.env/named.env", $"CLAUDE_EXECUTABLE={named}\n");
        kit.Runner.Versions[$"{named} --version"] = Says("2.1.100");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named()], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{named}' is claude 2.1.100, older than this tool needs ({ClaudeCodeUpdateService.Needed}), and a program a person named is never updated. "
            + $"Update it by hand, or take {ClaudeCodeUpdateService.ExecutableVariable} out of the slot",
            answer.Message);
    }

    [Fact]
    public async Task AProgramTheSlotNames_AsABatchFile_IsRefused()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var named = kit.Temp.WriteFile("claude.cmd", "@echo off\n");
        kit.Temp.WriteFile(".harness-config/workflows/.env/named.env", $"CLAUDE_EXECUTABLE={named}\n");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named()], client: null, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal($"'{named}' is a batch file, which is never started (its arguments would be parsed twice); name the program it starts", answer.Message);
    }

    private static AppointedKey Named()
        => new(ClaudeCodeUpdateService.ExecutableVariable, "agentClients.claude-code", "the program's path", Secret: false);

    /// <summary>What Claude Code prints for its --version: the version, then its name.</summary>
    private static ProcessResult Says(string version)
        => new(0, $"{version} (Claude Code)\n", string.Empty, TimeSpan.Zero, TimedOut: false);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A machine of fakes: npm on the PATH as <c>npm.cmd</c> with node beside it and a prefix of its own (on Windows; on
    /// another system the PATH holds no npm), a global place holding whatever package the test writes, a process table the
    /// test scripts, and one runner that records every command and stops nothing once begun. No test reaches npm, the
    /// network or the machine's own table.
    /// </summary>
    private sealed class Kit : IDisposable
    {
        public TempDirectory Temp { get; } = new();

        public ScriptedRunner Runner { get; } = new();

        public IHostPlatform Platform { get; }

        public ScriptedTable Table { get; set; } = new([[]]);

        public FixedPermissions Permissions { get; } = new();

        public DssHarness.Core.Repository.HarnessLayout Layout { get; }

        public string Root { get; }

        public string NpmCmd { get; }

        public string Node { get; }

        public string PrefixScript { get; }

        public string RootScript { get; }

        public string Manifest { get; }

        public string Program { get; }

        private IFileSystem Files { get; } = new PassThroughFileSystem(new PhysicalFileSystem(new FixedPermissions()));

        private readonly string? _path;

        private readonly string? _disableUpdates;

        public Kit(string? package = null, string? program = null, bool npm = true, bool disableUpdates = false, PlatformId platform = PlatformId.Windows)
        {
            Platform = HostDoubles.Platform(platform);
            Layout = new(Temp.Path, Temp.Path);
            _path = Path.Combine(Temp.Path, npm ? "path" : "empty");
            _disableUpdates = disableUpdates ? "1" : null;

            var path = Path.Combine(Temp.Path, "path");
            var prefix = Path.Combine(Temp.Path, "prefix");
            Temp.WriteFile("path/npm.cmd", "@echo off\n");
            NpmCmd = Path.Combine(path, "npm.cmd");
            Temp.WriteFile("path/node.exe", "a program");
            Temp.WriteFile("path/node_modules/npm/bin/npm-prefix.js", "a script");
            Temp.WriteFile("prefix/node_modules/npm/bin/npm-cli.js", "a script");

            Node = Path.Combine(path, "node.exe");
            PrefixScript = Path.Combine(path, "node_modules", "npm", "bin", "npm-prefix.js");
            RootScript = Path.Combine(prefix, "node_modules", "npm", "bin", "npm-cli.js");
            Root = Path.Combine(Temp.Path, "root");
            Manifest = Path.Combine(Root, "@anthropic-ai", "claude-code", "package.json");
            Program = Path.Combine(Root, "@anthropic-ai", "claude-code", "bin", "claude.exe");

            Runner.Prefix = prefix;
            Runner.Root = Root;

            if (package is not null)
            {
                WritePackage(package, program);
            }
        }

        public ClaudeCodeUpdateService Service()
            => new(
                Table,
                Runner,
                Files,
                Permissions,
                Platform,
                new HarnessFactory().Identity,
                keys: new AgentKeyStore(Files, Permissions, Platform),
                environment: Name);

        /// <summary>Writes the package at <paramref name="version"/>, whose program answers <paramref name="answered"/> - or nothing, where that is null.</summary>
        public void WritePackage(string version, string? answered, bool bin = true)
        {
            Temp.WriteFile(
                Path.GetRelativePath(Temp.Path, Manifest).Replace('\\', '/'),
                "{\n  \"version\": \"" + version + "\",\n  \"bin\": " + (bin ? "{\"claude\": \"bin/claude.exe\"}" : "{}") + "\n}\n");

            if (answered is null)
            {
                Runner.Versions.Remove($"{Program} --version");
            }
            else
            {
                Runner.Versions[$"{Program} --version"] = Says(answered);
            }
        }

        private string? Name(string variable)
            => variable switch
            {
                "PATH" => _path,
                ClaudeCodeUpdateService.UpdatesVariable => _disableUpdates,
                _ => null,
            };

        public void Dispose() => Temp.Dispose();
    }

    /// <summary>Runs what the test scripts: npm's prefix, npm's root, the install, and any program's own version.</summary>
    private sealed class ScriptedRunner : IProcessRunner
    {
        public List<string> Ran { get; } = [];

        public List<bool> Stoppable { get; } = [];

        public List<ProcessRequest> Requests { get; } = [];

        public Action? OnInstall { get; set; }

        public ProcessResult Install { get; set; } = new(0, "added 1 package in 4s\n", string.Empty, TimeSpan.FromSeconds(4), TimedOut: false);

        public string? Prefix { get; set; }

        public string? Root { get; set; }

        /// <summary>How npm answers its root, in turn, before it answers as <see cref="Root"/> says.</summary>
        public Queue<ProcessResult> RootAnswers { get; } = [];

        public Dictionary<string, ProcessResult> Versions { get; } = [];

        public string? FindExecutable(string command) => null;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var line = string.Join(' ', [request.FileName, .. request.Arguments]);

            Ran.Add(line);
            Stoppable.Add(cancellationToken.CanBeCanceled);
            Requests.Add(request);

            return Task.FromResult(line.EndsWith("npm-prefix.js", StringComparison.Ordinal)
                ? new ProcessResult(0, (Prefix ?? string.Empty) + "\n", string.Empty, TimeSpan.Zero, TimedOut: false)
                : line.EndsWith("root -g", StringComparison.Ordinal)
                    ? RootAnswers.Count > 0
                        ? RootAnswers.Dequeue()
                        : new ProcessResult(0, (Root is { Length: > 0 } root ? root + "\n" : string.Empty), string.Empty, TimeSpan.Zero, TimedOut: false)
                    : line.Contains(" install -g ", StringComparison.Ordinal)
                        ? SideEffect()
                        : Versions.TryGetValue(line, out var version)
                            ? version
                            : new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        private ProcessResult SideEffect()
        {
            OnInstall?.Invoke();

            return Install;
        }
    }

    /// <summary>A process table that reads as each of <paramref name="readings"/> in turn, and as the last of them from then on; counts its readings.</summary>
    private sealed class ScriptedTable(SampledProcess[][] readings) : IProcessTable
    {
        public int Reads { get; private set; }

        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessTableReading(readings[Math.Min(Reads++, readings.Length - 1)], null));
    }

    /// <summary>
    /// Every file private and a file that is there one this machine starts, as Windows decides it; another user able to
    /// change the slot's files where a test says so.
    /// </summary>
    private sealed class FixedPermissions : IFilePermissions
    {
        public bool OthersCanWrite { get; set; }

        public void ProtectSecret(string path)
        {
        }

        public bool IsExecutable(string path) => File.Exists(path);

        public void SetExecutable(string path, bool executable)
        {
        }

        public bool IsPrivate(string path) => true;

        public bool IsWritableByOthers(string path) => OthersCanWrite;
    }
}
