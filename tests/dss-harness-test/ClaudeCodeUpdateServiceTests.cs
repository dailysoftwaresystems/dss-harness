using DssHarness.Core.Agents.ClaudeCode;
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
    [Fact]
    public async Task AReadyInstallation_IsSaidInOneLine_AndNothingIsChanged()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal($"claude 2.1.296, installed with npm: '{kit.Program}'", answer.Message);
        Assert.Equal(
            [$"{kit.Node} {kit.PrefixScript}", $"{kit.Node} {kit.RootScript} root -g", $"{kit.Program} --version"],
            kit.Runner.Ran);
    }

    [Fact]
    public async Task NoNpm_IsRefusedWithWhatWasLookedFor()
    {
        using var kit = new Kit(npm: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: " (steps: plan, plan-audit)", Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            "this run needs a Claude model through the client 'claude-code' (steps: plan, plan-audit), and no npm is on the PATH. "
            + "Looked for: npm.exe and npm.cmd, and the node.exe and npm-cli.js npm.cmd starts. This tool installs Claude Code "
            + "and brings it up to date with npm. Install Node.js 22 or later, which brings npm, and start the run again",
            answer.Message);
    }

    [Fact]
    public async Task NpmWithNoClaudeCode_InstallsIt_AndSaysSoInOneLine()
    {
        using var kit = new Kit(package: null);
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal($"npm has no Claude Code installed: installed 2.1.287 with '{ClaudeCodeUpdateService.InstallCommand()}'", answer.Message);
        Assert.Contains($"{kit.Node} {kit.RootScript} install -g @anthropic-ai/claude-code@{ClaudeCodeUpdateService.Needed}", kit.Runner.Ran);
    }

    [Fact]
    public async Task AClaudeCodeOlderThanNeeded_IsBroughtUpToDate_AndSaysSoInOneLine()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal($"claude 2.1.201 is older than this tool needs ({ClaudeCodeUpdateService.Needed}): updated with '{ClaudeCodeUpdateService.InstallCommand()}'; it now says 2.1.287", answer.Message);
    }

    [Fact]
    public async Task OnceTheChangeHasBegun_NothingStopsIt()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.OnInstall = () => kit.WritePackage("2.1.287", "2.1.287");

        await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        // The readings before the change take the token; the change, its reading back and the program's own version
        // take none.
        Assert.Equal([true, true, false, false, false], kit.Runner.Stoppable);
    }

    [Fact]
    public async Task ASessionOfTheProgram_RefusesTheChange_NamedByItsId()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Table = new ScriptedTable([[new(4242, 1, "claude", null, null), new(9000, 1, "dotnet", null, null)]]);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.Refused, answer.ExitCode);
        Assert.Equal("a session of Claude Code is running on this machine (pid 4242); start the run again when they have ended.", answer.Message);
        Assert.DoesNotContain(kit.Runner.Ran, line => line.Contains(" install ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondUpdateOfNpmPlace_RefusedAtOnce()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        using var held = new ProgramUpdate(kit.Runner, kit.Platform).TryHold(kit.Root);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.Refused, answer.ExitCode);
        Assert.Equal($"another update is changing npm's '{kit.Root}' on this machine; run again once it has ended.", answer.Message);
    }

    [Fact]
    public async Task TheUpdateKeyOff_RefusesWithTheCommandToRunByHand()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: false, steps: string.Empty, Token);

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

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

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

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{kit.Manifest}' names no program under bin.claude: this is not a Claude Code this tool can start. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand()}",
            answer.Message);
    }

    [Fact]
    public async Task AProgramThatWouldNotAnswerItsVersion_IsRefused()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296");
        kit.Runner.Versions.Clear();

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{kit.Program}' would not answer its own --version, so this tool cannot start it. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand()}; an installation whose step after the install did not run "
            + "(npm install --ignore-scripts) is the usual cause.",
            answer.Message);
    }

    [Fact]
    public async Task NpmRanAndClaudeIsStillOlder_IsRefusedNamingTheCauses()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"npm ran, and Claude Code is still missing or older than this tool needs: npm holds claude 2.1.201, and this tool needs {ClaudeCodeUpdateService.Needed}. "
            + $"The command that ran: '{ClaudeCodeUpdateService.InstallCommand()}'. "
            + "A Node.js older than 22, which the package asks for, and an installation whose step after the install did not run (npm install --ignore-scripts) are the causes.",
            answer.Message);
    }

    [Fact]
    public async Task AProgramWhoseVersionDisagreesWithItsPackage_IsRefused()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.100");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{kit.Program}' says it is claude 2.1.100 and its package says 2.1.296, and the two do not agree. "
            + $"Install it again: {ClaudeCodeUpdateService.InstallCommand()}; an installation whose step after the install did not run "
            + "(npm install --ignore-scripts) is the usual cause.",
            answer.Message);
    }

    [Fact]
    public async Task NpmThatWouldNotSayItsRoot_IsRefused()
    {
        using var kit = new Kit(package: "2.1.296", program: "2.1.296");
        kit.Runner.Root = null;

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"npm was found but would not say where its global packages are, so Claude Code could not be looked for. "
            + $"{ClaudeCodeUpdateService.InstallCommand()} by hand installs the one this tool needs ({ClaudeCodeUpdateService.Needed}).",
            answer.Message);
    }

    [Fact]
    public async Task ARegistryNotReached_IsRefusedAsIncomplete_AndTryingAgainCanHelp()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.Install = new ProcessResult(1, string.Empty, "npm ERR! network request to https://registry.npmjs.org failed, reason: getaddrinfo ENOTFOUND\n", TimeSpan.FromSeconds(3), TimedOut: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.Incomplete, answer.ExitCode);
        Assert.Contains("running it again can help", answer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProgramTheSlotNames_ThatWouldNotAnswer_IsRefused()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var named = kit.Temp.WriteFile("mine.exe", "a program");
        kit.Temp.WriteFile(".harness-config/workflows/.env/named.env", $"CLAUDE_EXECUTABLE={named}\n");

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named(named)], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            $"'{named}', the program the slot names, would not answer its --version. "
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

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named("x")], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ConfigInvalid, answer.ExitCode);
        Assert.Equal(
            $"'CLAUDE_EXECUTABLE' is declared in '{Path.Combine(slot, "keys.env")}' and in '{Path.Combine(slot, "work.env")}': a name is declared once",
            answer.Message);
    }

    [Fact]
    public async Task NpmRefusedForNoRightToWrite_SaysTheToolNeverRunsSudo()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.Install = new ProcessResult(1, string.Empty, "EACCES: permission denied, access '/usr/lib/node_modules'\n", TimeSpan.FromSeconds(3), TimedOut: false);

        var answer = await kit.Service().ReadyAsync(kit.Layout, [], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal(
            "npm could not change what it holds (the install failed (exit 1): EACCES: permission denied, access '/usr/lib/node_modules'); "
            + $"this tool never runs sudo. Run it by hand: {ClaudeCodeUpdateService.InstallCommand()}.",
            answer.Message);
    }

    [Fact]
    public async Task AProgramTheSlotNames_IsReadAndNeverMoved()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var named = kit.Temp.WriteFile("mine.exe", "a program");
        kit.Temp.WriteFile(".harness-config/workflows/.env/named.env", $"CLAUDE_EXECUTABLE={named}\n");
        kit.Runner.Versions[$"{named} --version"] = "2.1.296";

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named(named)], update: true, steps: string.Empty, Token);

        Assert.True(answer.Ready);
        Assert.Equal($"claude 2.1.296, named by the slot: '{named}'", answer.Message);
        Assert.DoesNotContain(kit.Runner.Ran, line => line.Contains(" install ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProgramTheSlotNames_OlderThanNeeded_IsRefusedWithTheTwoWaysOn()
    {
        using var kit = new Kit(package: "2.1.201", program: "2.1.201");
        var named = kit.Temp.WriteFile("mine.exe", "a program");
        kit.Temp.WriteFile(".harness-config/workflows/.env/named.env", $"CLAUDE_EXECUTABLE={named}\n");
        kit.Runner.Versions[$"{named} --version"] = "2.1.100";

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named(named)], update: true, steps: string.Empty, Token);

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

        var answer = await kit.Service().ReadyAsync(kit.Layout, [Named(named)], update: true, steps: string.Empty, Token);

        Assert.False(answer.Ready);
        Assert.Equal(HarnessExit.ToolMissing, answer.ExitCode);
        Assert.Equal($"'{named}' is a batch file, which is never started (its arguments would be parsed twice); name the program it starts", answer.Message);
    }

    private static AppointedKey Named(string program)
        => new(ClaudeCodeUpdateService.ExecutableVariable, "agentClients.claude-code", "the program's path");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A machine of fakes: npm on the PATH as <c>npm.cmd</c> with node beside it and a prefix of its own, a global
    /// place holding whatever package the test writes, a process table the test scripts, and one runner that records
    /// every command and stops nothing once begun. No test reaches npm, the network or the machine's own table.
    /// </summary>
    private sealed class Kit : IDisposable
    {
        public TempDirectory Temp { get; } = new();

        public ScriptedRunner Runner { get; } = new();

        public IHostPlatform Platform { get; } = HostDoubles.Platform(PlatformId.Windows);

        public ScriptedTable Table { get; set; } = new([[]]);

        public DssHarness.Core.Repository.HarnessLayout Layout { get; }

        public string Root { get; }

        public string Node { get; }

        public string PrefixScript { get; }

        public string RootScript { get; }

        public string Manifest { get; }

        public string Program { get; }

        private DssHarness.Core.FileSystem.IFileSystem Files { get; } = new PassThroughFileSystem(new PhysicalFileSystem(new FixedPermissions()));

        private readonly string? _path;

        private readonly string? _disableUpdates;

        public Kit(string? package = null, string? program = null, bool npm = true, bool disableUpdates = false)
        {
            Layout = new(Temp.Path, Temp.Path);
            _path = npm ? Path.Combine(Temp.Path, "path") : null;
            _disableUpdates = disableUpdates ? "1" : null;

            var path = Path.Combine(Temp.Path, "path");
            var prefix = Path.Combine(Temp.Path, "prefix");
            Temp.WriteFile("path/npm.cmd", "@echo off\n");
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

            if (package is not null && program is not null)
            {
                WritePackage(package, program);
            }
        }

        public ClaudeCodeUpdateService Service()
            => new(
                Table,
                Runner,
                Files,
                Platform,
                new HarnessFactory().Identity,
                keys: new AgentKeyStore(Files, new FixedPermissions(), Platform),
                environment: Name);

        public void WritePackage(string version, string answered, bool bin = true)
        {
            Temp.WriteFile(
                Path.GetRelativePath(Temp.Path, Manifest).Replace('\\', '/'),
                "{\n  \"version\": \"" + version + "\",\n  \"bin\": " + (bin ? "{\"claude\": \"bin/claude.exe\"}" : "{}") + "\n}\n");
            Runner.Versions[$"{Program} --version"] = answered;
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

        public Action? OnInstall { get; set; }

        public ProcessResult Install { get; set; } = new(0, "added 1 package in 4s\n", string.Empty, TimeSpan.FromSeconds(4), TimedOut: false);

        public string? Prefix { get; set; }

        public string? Root { get; set; }

        public Dictionary<string, string> Versions { get; } = [];

        public string? FindExecutable(string command) => null;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var line = string.Join(' ', [request.FileName, .. request.Arguments]);

            Ran.Add(line);
            Stoppable.Add(cancellationToken.CanBeCanceled);

            return Task.FromResult(line.EndsWith("npm-prefix.js", StringComparison.Ordinal)
                ? new ProcessResult(0, (Prefix ?? string.Empty) + "\n", string.Empty, TimeSpan.Zero, TimedOut: false)
                : line.EndsWith("root -g", StringComparison.Ordinal)
                    ? new ProcessResult(0, ((Root ?? string.Empty) is { Length: > 0 } root ? root + "\n" : string.Empty), string.Empty, TimeSpan.Zero, TimedOut: false)
                    : line.Contains(" install -g ", StringComparison.Ordinal)
                        ? SideEffect()
                        : Versions.TryGetValue(line, out var version)
                            ? new ProcessResult(0, version + "\n", string.Empty, TimeSpan.Zero, TimedOut: false)
                            : new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        private ProcessResult SideEffect()
        {
            OnInstall?.Invoke();

            return Install;
        }
    }

    /// <summary>A process table that reads as each of <paramref name="readings"/> in turn, and as the last of them from then on.</summary>
    private sealed class ScriptedTable(SampledProcess[][] readings) : IProcessTable
    {
        private int _read;

        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessTableReading(readings[Math.Min(_read++, readings.Length - 1)], null));
    }

    /// <summary>Every file private, every answer a privacy rule could ask for: nothing here is refused for one.</summary>
    private sealed class FixedPermissions : IFilePermissions
    {
        public void ProtectSecret(string path)
        {
        }

        public bool IsExecutable(string path) => true;

        public void SetExecutable(string path, bool executable)
        {
        }

        public bool IsPrivate(string path) => true;

        public bool IsWritableByOthers(string path) => false;
    }
}
