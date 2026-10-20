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

        Assert.Equal(Path.Combine(temp.Path, "path", "npm.exe"), found!.Program);
        Assert.Empty(found.Leading);
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
        Assert.Equal(dance.Node, found!.Program);
        Assert.Equal([dance.InstalledCli], found.Leading);
        Assert.Equal([$"{dance.Node} {dance.PrefixScript}"], runner.Ran);
    }

    [Fact]
    public async Task OnWindows_AnNpmCmdWithoutNodeBeside_TakesNodeFromThePath()
    {
        using var temp = new TempDirectory();
        var dance = new Dance(temp);
        File.Delete(Path.Combine(temp.Path, "path", "node.exe"));
        var node = temp.WriteFile("other/node.exe", "a program");
        var (client, _) = Client(temp, ["path"], node: node);

        var found = await client.FindAsync(Token);

        Assert.Equal(node, found!.Program);
        Assert.Equal([dance.InstalledCli], found.Leading);
    }

    [Fact]
    public async Task OnWindows_APrefixWithNoNpmOfItsOwn_StartsTheOneBesideNpmCmd()
    {
        using var temp = new TempDirectory();
        new Dance(temp, ownNpm: false);
        var (client, _) = Client(temp, ["path"]);

        var found = await client.FindAsync(Token);

        Assert.Equal([Path.Combine(temp.Path, "path", "node_modules", "npm", "bin", "npm-cli.js")], found!.Leading);
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

        Assert.Equal(exe, found!.Program);
    }

    [Fact]
    public async Task WhereNothingIsNamedByThePath_NothingIsFound()
    {
        using var temp = new TempDirectory();
        var (client, _) = Client(temp, ["empty"]);

        Assert.Null(await client.FindAsync(Token));
    }

    /// <summary>
    /// The fake <c>npm.cmd</c> and the fake prefix that holds an npm of its own: the files the PATH names, as the plan
    /// says a test pins each step with.
    /// </summary>
    private sealed class Dance
    {
        public string Node { get; }

        public string PrefixScript { get; }

        public string InstalledCli { get; }

        public Dance(TempDirectory temp, bool ownNpm = true)
        {
            temp.WriteFile("path/npm.cmd", "@echo off\n");
            temp.WriteFile("path/node.exe", "a program");
            temp.WriteFile("path/node_modules/npm/bin/npm-prefix.js", "a script");

            if (ownNpm)
            {
                temp.WriteFile("prefix/node_modules/npm/bin/npm-cli.js", "a script");
            }

            Node = Path.Combine(temp.Path, "path", "node.exe");
            PrefixScript = Path.Combine(temp.Path, "path", "node_modules", "npm", "bin", "npm-prefix.js");
            InstalledCli = Path.Combine(temp.Path, "prefix", "node_modules", "npm", "bin", "npm-cli.js");
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (NpmClient Client, RecordingRunner Runner) Client(TempDirectory temp, string[] directories, string? node = null)
    {
        var path = string.Join(Path.PathSeparator, directories.Select(named => Path.Combine(temp.Path, named)));
        var runner = new RecordingRunner { Prefix = Path.Combine(temp.Path, "prefix"), Node = node };

        return (new NpmClient(
            runner,
            new PassThroughFileSystem(new PhysicalFileSystem(new PrivateFiles())),
            HostDoubles.Platform(PlatformId.Windows),
            variable => variable == "PATH" ? path : null), runner);
    }

    /// <summary>Every file private: nothing here is refused for its permissions.</summary>
    private sealed class PrivateFiles : IFilePermissions
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

    /// <summary>Answers the prefix question with the prefix, and where a test says, node from the PATH; records every command.</summary>
    private sealed class RecordingRunner : IProcessRunner
    {
        public List<string> Ran { get; } = [];

        public string Prefix { get; set; } = string.Empty;

        public string? Node { get; set; }

        public string? FindExecutable(string command) => command == "node" ? Node : null;

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
