using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using Xunit;

namespace DssHarness.Tests;

public sealed class NpmClientTests
{
    [Fact]
    public async Task OnWindows_AnNpmExeOnThePath_IsStartedAsItIs()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("path/npm.exe", "a program");
        var (client, runner) = Client(temp, ["path"]);

        var found = await client.FindAsync(Token);

        Assert.Equal(Path.Combine(temp.Path, "path", "npm.exe"), found.Npm!.Program);
        Assert.Empty(found.Npm.Leading);
        Assert.Null(found.Missing);
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public async Task OnWindows_AnNpmCmd_IsNeverStarted_WhatItStartsIs()
    {
        using var temp = new TempDirectory();
        var dance = new Dance(temp);
        var (client, runner) = Client(temp, ["path"]);

        var found = await client.FindAsync(Token);

        // The node beside npm.cmd, starting the npm-cli.js of the prefix npm names - the npm a person installed over
        // the one Node.js brought - never the batch file itself.
        Assert.Equal(dance.Node, found.Npm!.Program);
        Assert.Equal([dance.InstalledCli], found.Npm.Leading);
        Assert.Equal([$"{dance.Node} {dance.PrefixScript}"], runner.Ran);
    }

    /// <summary>
    /// Where no node.exe is beside npm.cmd, node is taken from the PATH npm.cmd was found on - one PATH for the whole
    /// finding, never the process's own beside the one npm was looked for on.
    /// </summary>
    [Fact]
    public async Task OnWindows_AnNpmCmdWithoutNodeBeside_TakesNodeFromTheSamePath()
    {
        using var temp = new TempDirectory();
        var dance = new Dance(temp);
        File.Delete(dance.Node);
        temp.WriteFile("other/node.exe", "a program");
        var node = Path.Combine(temp.Path, "other", "node.exe");
        var (client, runner) = Client(temp, ["path", "other"]);

        var found = await client.FindAsync(Token);

        Assert.Equal(node, found.Npm!.Program);
        Assert.Equal([dance.InstalledCli], found.Npm.Leading);
        Assert.Equal(0, runner.Looked);
    }

    [Fact]
    public async Task OnWindows_APrefixWithNoNpmOfItsOwn_StartsTheOneBesideNpmCmd()
    {
        using var temp = new TempDirectory();
        new Dance(temp, ownNpm: false);
        var (client, _) = Client(temp, ["path"]);

        var found = await client.FindAsync(Token);

        Assert.Equal([Path.Combine(temp.Path, "path", "node_modules", "npm", "bin", "npm-cli.js")], found.Npm!.Leading);
    }

    [Fact]
    public async Task OnWindows_AnNpmExeBeforeAnyNpmCmd_Wins()
    {
        using var temp = new TempDirectory();
        var exe = Path.Combine(temp.Path, "first", "npm.exe");
        temp.WriteFile("first/npm.exe", "a program");
        temp.WriteFile("second/npm.cmd", "@echo off\n");
        var (client, _) = Client(temp, ["first", "second"]);

        var found = await client.FindAsync(Token);

        Assert.Equal(exe, found.Npm!.Program);
    }

    /// <summary>Within one directory npm.exe is taken over the npm.cmd beside it, as the system resolves a name.</summary>
    [Fact]
    public async Task OnWindows_AnNpmExeBesideAnNpmCmd_Wins()
    {
        using var temp = new TempDirectory();
        new Dance(temp);
        temp.WriteFile("path/npm.exe", "a program");
        var exe = Path.Combine(temp.Path, "path", "npm.exe");
        var (client, runner) = Client(temp, ["path"]);

        var found = await client.FindAsync(Token);

        Assert.Equal(exe, found.Npm!.Program);
        Assert.Empty(runner.Ran);
    }

    [Fact]
    public async Task WhereNothingIsNamedByThePath_NothingIsFound_AndWhatWasLookedForIsSaid()
    {
        using var temp = new TempDirectory();
        var (client, _) = Client(temp, ["empty"]);

        var found = await client.FindAsync(Token);

        Assert.Null(found.Npm);
        Assert.Equal("no npm is on the PATH. Looked for: npm.exe and npm.cmd, and the node.exe and npm-cli.js npm.cmd starts", found.Missing);
    }

    /// <summary>An npm.cmd with node nowhere is said as that: npm.cmd is on the PATH, and nothing can start what it starts.</summary>
    [Fact]
    public async Task OnWindows_AnNpmCmdWithNodeNowhere_SaysSo()
    {
        using var temp = new TempDirectory();
        var dance = new Dance(temp);
        File.Delete(dance.Node);
        var (client, runner) = Client(temp, ["path"]);

        var found = await client.FindAsync(Token);

        Assert.Null(found.Npm);
        Assert.Equal(
            $"'{dance.Cmd}' is on the PATH, but no node.exe is beside it or on the PATH to start what it starts. "
            + "Looked for: npm.exe and npm.cmd, and the node.exe and npm-cli.js npm.cmd starts",
            found.Missing);
        Assert.Empty(runner.Ran);
    }

    /// <summary>An npm.cmd whose npm-cli.js is neither in npm's prefix nor beside it is said as that, never started half-made.</summary>
    [Fact]
    public async Task OnWindows_AnNpmCmdWhoseScriptIsNowhere_SaysSo()
    {
        using var temp = new TempDirectory();
        var dance = new Dance(temp, ownNpm: false);
        var (client, _) = Client(temp, ["path"]);
        File.Delete(Path.Combine(temp.Path, "path", "node_modules", "npm", "bin", "npm-cli.js"));

        var found = await client.FindAsync(Token);

        Assert.Null(found.Npm);
        Assert.Equal(
            $"'{dance.Cmd}' is on the PATH, but the npm-cli.js it starts is in neither the prefix npm names nor beside it "
            + $"('{Path.Combine(temp.Path, "path", "node_modules", "npm", "bin", "npm-cli.js")}'). "
            + "Looked for: npm.exe and npm.cmd, and the node.exe and npm-cli.js npm.cmd starts",
            found.Missing);
    }

    /// <summary>
    /// On Linux and macOS npm is the script the PATH names, started as it is; a Windows npm.cmd on the PATH is no npm
    /// there, and a PATH with none is said with what was looked for on that system.
    /// </summary>
    [Theory]
    [InlineData(PlatformId.Linux)]
    [InlineData(PlatformId.MacOs)]
    public async Task OnLinuxAndMacOs_NpmIsTheScriptThePathNames(PlatformId system)
    {
        using var temp = new TempDirectory();
        temp.WriteFile("cmds/npm.cmd", "@echo off\n");
        temp.WriteFile("bin/npm", "#!/bin/sh\n");
        var npm = Path.Combine(temp.Path, "bin", "npm");
        var (found, _) = Client(temp, ["cmds", "bin"], system);
        var (none, runner) = Client(temp, ["cmds"], system);

        var program = await found.FindAsync(Token);
        var missing = await none.FindAsync(Token);

        Assert.Equal(npm, program.Npm!.Program);
        Assert.Empty(program.Npm.Leading);
        Assert.Null(missing.Npm);
        Assert.Equal("no npm is on the PATH. Looked for: npm", missing.Missing);
        Assert.Empty(runner.Ran);
    }

    /// <summary>npm's root is what it prints; a failure is said with its exit code and npm's words, and silence as that.</summary>
    [Fact]
    public void TheRoot_IsWhatNpmPrints_OrHowAskingItEnded()
    {
        Assert.Equal(new NpmClient.NpmRoot("/usr/lib/node_modules", null), NpmClient.ReadRoot(new ProcessResult(0, "/usr/lib/node_modules\n", string.Empty, TimeSpan.Zero, TimedOut: false)));
        Assert.Equal(new NpmClient.NpmRoot(null, "'npm root -g' printed nothing"), NpmClient.ReadRoot(new ProcessResult(0, "\n", string.Empty, TimeSpan.Zero, TimedOut: false)));
        Assert.Equal(
            new NpmClient.NpmRoot(null, "'npm root -g' failed (exit 1): npm ERR! code EPERM"),
            NpmClient.ReadRoot(new ProcessResult(1, string.Empty, "npm ERR! code EPERM\n", TimeSpan.Zero, TimedOut: false)));
    }

    /// <summary>
    /// The fake <c>npm.cmd</c> and the fake prefix that holds an npm of its own: the files the PATH names, as the plan
    /// says a test pins each step with.
    /// </summary>
    private sealed class Dance
    {
        public string Cmd { get; }

        public string Node { get; }

        public string PrefixScript { get; }

        public string InstalledCli { get; }

        public Dance(TempDirectory temp, bool ownNpm = true)
        {
            temp.WriteFile("path/npm.cmd", "@echo off\n");
            temp.WriteFile("path/node.exe", "a program");
            temp.WriteFile("path/node_modules/npm/bin/npm-prefix.js", "a script");
            temp.WriteFile("path/node_modules/npm/bin/npm-cli.js", "a script");

            if (ownNpm)
            {
                temp.WriteFile("prefix/node_modules/npm/bin/npm-cli.js", "a script");
            }

            Cmd = Path.Combine(temp.Path, "path", "npm.cmd");
            Node = Path.Combine(temp.Path, "path", "node.exe");
            PrefixScript = Path.Combine(temp.Path, "path", "node_modules", "npm", "bin", "npm-prefix.js");
            InstalledCli = Path.Combine(temp.Path, "prefix", "node_modules", "npm", "bin", "npm-cli.js");
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (NpmClient Client, RecordingRunner Runner) Client(TempDirectory temp, string[] directories, PlatformId system = PlatformId.Windows)
    {
        var path = string.Join(Path.PathSeparator, directories.Select(named => Path.Combine(temp.Path, named)));
        var runner = new RecordingRunner { Prefix = Path.Combine(temp.Path, "prefix") };
        var files = new FilesThatAreThere();

        return (new NpmClient(
            runner,
            new PassThroughFileSystem(new PhysicalFileSystem(files)),
            files,
            HostDoubles.Platform(system),
            variable => variable == "PATH" ? path : null), runner);
    }

    /// <summary>Every file private, and a file that is there one this machine starts, as Windows decides it.</summary>
    private sealed class FilesThatAreThere : IFilePermissions
    {
        public void ProtectSecret(string path)
        {
        }

        public bool IsExecutable(string path) => File.Exists(path);

        public void SetExecutable(string path, bool executable)
        {
        }

        public bool IsPrivate(string path) => true;

        public bool IsWritableByOthers(string path) => false;
    }

    /// <summary>
    /// Answers the prefix question with the prefix and records every command; counts every lookup on the process's own
    /// PATH, which a finding never makes.
    /// </summary>
    private sealed class RecordingRunner : IProcessRunner
    {
        public List<string> Ran { get; } = [];

        public string Prefix { get; set; } = string.Empty;

        public int Looked { get; private set; }

        public string? FindExecutable(string command)
        {
            Looked++;

            return null;
        }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var line = string.Join(' ', [request.FileName, .. request.Arguments]);
            Ran.Add(line);

            return Task.FromResult(new ProcessResult(
                0,
                (line.EndsWith("npm-prefix.js", StringComparison.Ordinal) ? Prefix : string.Empty) + "\n",
                string.Empty,
                TimeSpan.Zero,
                TimedOut: false));
        }
    }
}
