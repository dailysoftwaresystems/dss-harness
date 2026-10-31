using DssHarness.Core.Agents;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// The process tree a command tells it runs under an agent by: a process of this tool's among the ones that started it,
/// walked by each process's parent - a recycled id never read as a parent - and a tree that could not be read said, never
/// read as one with no agent in it.
/// </summary>
public sealed class AgentAncestryTests
{
    private const int Current = 500;

    private static readonly DateTimeOffset Early = new(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A process table that answers one reading, whatever is asked.</summary>
    /// <param name="reading">The reading it answers.</param>
    internal sealed class FixedTable(ProcessTableReading reading) : IProcessTable
    {
        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(reading);
    }

    /// <summary>The file this process runs as, as a test says it.</summary>
    /// <param name="path">The file; null where the platform would not say.</param>
    internal sealed class OwnProgramAt(string? path) : IOwnProgram
    {
        public string? Path => path;

        public void LoadWhole()
        {
        }
    }

    /// <summary>A table where <paramref name="current"/> was started by a person's shell, which a terminal started: no process of the tool's above it.</summary>
    /// <param name="current">This process's id.</param>
    internal static ProcessTableReading UnderAShell(int current)
        => new(
            [
                new SampledProcess(1, null, "terminal", Early, "terminal", "/usr/bin/terminal"),
                new SampledProcess(2, 1, "bash", Early.AddMinutes(1), "-bash", "/usr/bin/bash"),
                new SampledProcess(current, 2, "dssharness", Early.AddMinutes(2), "dssharness run-side-task", "/home/me/.dotnet/tools/dssharness"),
            ],
            null);

    /// <summary>A person's shell started the command: no ancestor is the tool's, and nothing stands in its way.</summary>
    [Fact]
    public void ACommandAPersonsShellStarted_HasNoAncestorOfTheTools()
    {
        Assert.Equal(new AgentAncestryAnswer(null, null), AgentAncestry.Of(UnderAShell(Current), Current, "/home/me/.dotnet/tools/dssharness", StringComparison.Ordinal));
    }

    /// <summary>
    /// The engine started the agent, the agent a shell, the shell the command: the engine is the nearest ancestor of the
    /// tool's, through every process between - whatever the command's environment says.
    /// </summary>
    [Fact]
    public void ACommandAnAgentsShellStarted_HasTheEngineForItsAncestor()
    {
        var engine = new SampledProcess(10, 2, "dssharness", Early.AddMinutes(1), "dssharness run-orchestrator implementation", "/home/me/.dotnet/tools/dssharness");
        var table = new ProcessTableReading(
            [
                new SampledProcess(2, null, "bash", Early, "-bash", "/usr/bin/bash"),
                engine,
                new SampledProcess(20, 10, "claude", Early.AddMinutes(2), "claude --print", "/usr/bin/node"),
                new SampledProcess(30, 20, "bash", Early.AddMinutes(3), "bash -c 'printf ... | sed e'", "/usr/bin/bash"),
                new SampledProcess(Current, 30, "dssharness", Early.AddMinutes(4), "dssharness run-side-task --prompt x", "/home/me/.dotnet/tools/dssharness"),
            ],
            null);

        Assert.Equal(new AgentAncestryAnswer(engine, null), AgentAncestry.Of(table, Current, "/home/me/.dotnet/tools/dssharness", StringComparison.Ordinal));
    }

    /// <summary>
    /// A process holding the id of the command's parent that started after the command is another process given a freed
    /// id, not its parent: the walk ends there, and a process of the tool's that took the id is no ancestor.
    /// </summary>
    [Fact]
    public void AProcessThatTookAFreedId_IsNoAncestor()
    {
        var table = new ProcessTableReading(
            [
                new SampledProcess(30, null, "dssharness", Early.AddMinutes(10), "dssharness run-orchestrator implementation", "/home/me/.dotnet/tools/dssharness"),
                new SampledProcess(Current, 30, "dssharness", Early.AddMinutes(4), "dssharness run-side-task --prompt x", "/home/me/.dotnet/tools/dssharness"),
            ],
            null);

        Assert.Equal(new AgentAncestryAnswer(null, null), AgentAncestry.Of(table, Current, "/home/me/.dotnet/tools/dssharness", StringComparison.Ordinal));
    }

    /// <summary>
    /// A process of the tool's however it was started: by the tool's command's name, the very file this process runs as
    /// under any name, or a runtime hosting the tool's assembly - and a runtime hosting anything else, or any other
    /// program, is not.
    /// </summary>
    [Fact]
    public void AProcessOfTheTools_IsToldByItsName_ItsFile_OrTheAssemblyItsRuntimeHosts()
    {
        const string Own = @"C:\tools\renamed.exe";
        var cases = new (SampledProcess Process, bool Tool)[]
        {
            (new(1, null, "dssharness", null, null, @"C:\Users\me\.dotnet\tools\dssharness.exe"), true),
            (new(1, null, "DSSHARNESS", null, null, null), true),
            (new(1, null, "renamed", null, null, Own), true),
            (new(1, null, "dotnet", null, @"dotnet exec C:\src\dss-harness\build\bin\DssHarness.Cli\debug\dssharness.dll run-orchestrator x", @"C:\Program Files\dotnet\dotnet.exe"), true),
            (new(1, null, "dotnet", null, "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\my tools\\dssharness.dll\" run-orchestrator x", null), true),
            (new(1, null, "dotnet", null, "dotnet exec --runtimeconfig /b/dssharness.runtimeconfig.json --depsfile /b/dssharness.deps.json /b/dssharness.dll run-orchestrator x", "/usr/share/dotnet/dotnet"), true),
            (new(1, null, "dotnet", null, "/usr/share/dotnet/dotnet /home/me/my build/dssharness.dll run-orchestrator x", "/usr/share/dotnet/dotnet"), true),
            (new(1, null, "dotnet", null, @"dotnet C:\src\other\notdssharness.dll run", @"C:\Program Files\dotnet\dotnet.exe"), false),
            (new(1, null, "dotnet", null, "dotnet /b/other.dll fix /x/dssharness.dll", "/usr/share/dotnet/dotnet"), false),
            (new(1, null, "dotnet", null, "dotnet /home/me/other tool/other.dll fix /x/dssharness.dll", "/usr/share/dotnet/dotnet"), false),
            (new(1, null, "dotnet", null, "dotnet dssharness run-side-task fix why /x/dssharness.dll does not load", "/usr/share/dotnet/dotnet"), false),
            (new(1, null, "dotnet", null, "dotnet --depsfile /b/dssharness.dll /b/other.dll", "/usr/share/dotnet/dotnet"), false),
            (new(1, null, "dotnet", null, "dotnet build", @"C:\Program Files\dotnet\dotnet.exe"), false),
            (new(1, null, "dotnet", null, null, @"C:\Program Files\dotnet\dotnet.exe"), false),
            (new(1, null, "node", null, "node claude.js", @"C:\Program Files\nodejs\node.exe"), false),
        };

        Assert.Equal(
            cases.Select(one => one.Tool),
            cases.Select(one => AgentAncestry.IsTheTool(one.Process, Own, StringComparison.OrdinalIgnoreCase)));

        // A runtime is the program of many things: the file this process runs as names no process of the tool's where it
        // is the runtime itself, as a build from source runs.
        Assert.False(AgentAncestry.IsTheTool(new SampledProcess(1, null, "dotnet", null, "dotnet build", @"C:\Program Files\dotnet\dotnet.exe"), @"C:\Program Files\dotnet\dotnet.exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The application a runtime host runs is the first word past its program, its exec and its own options with their
    /// values - a path begun with a directory running on to its .dll where the line was joined with spaces - and a word
    /// of that application's arguments is never it.
    /// </summary>
    [Fact]
    public void TheApplicationARuntimeHostRuns_IsTheFirstWordPastItsOwnOptions()
    {
        var lines = new (string Line, string? Application)[]
        {
            ("dotnet exec --runtimeconfig /b/x.runtimeconfig.json --depsfile /b/x.deps.json /b/x.dll a b", "/b/x.dll"),
            ("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\my tools\\x.dll\" a", @"C:\my tools\x.dll"),
            ("/usr/bin/dotnet /home/me/my build/x.dll a /y/z.dll", "/home/me/my build/x.dll"),
            ("dotnet -d --fx-version 10.0.0 x.dll", "x.dll"),
            ("dotnet dssharness run-side-task a /y/z.dll", "dssharness"),
            ("dotnet /home/me/x a", "/home/me/x"),
            ("dotnet", null),
            ("dotnet exec --depsfile", null),
        };

        Assert.Equal(lines.Select(one => one.Application), lines.Select(one => AgentAncestry.HostedApplication(one.Line)));
    }

    /// <summary>
    /// A table read by names alone, or one that does not hold this process, cannot say which processes started it: the
    /// answer says why, and names no ancestor - never read as a tree with no agent in it.
    /// </summary>
    [Fact]
    public void ATreeThatCouldNotBeRead_SaysWhy()
    {
        var byNames = new ProcessTableReading([new SampledProcess(Current, null, "dssharness", null, null)], "the process table could not be read from WMI through PowerShell (it exited 1)");

        Assert.Equal(
            new AgentAncestryAnswer(null, "which processes started this one could not be read - the process table could not be read from WMI through PowerShell (it exited 1) -"),
            AgentAncestry.Of(byNames, Current, null, StringComparison.Ordinal));
        Assert.Equal(
            new AgentAncestryAnswer(null, "which processes started this one could not be read - the process table does not hold this process -"),
            AgentAncestry.Of(UnderAShell(Current + 1), Current, null, StringComparison.Ordinal));
    }

    /// <summary>What the service reads is the machine's table, walked from this process's own id.</summary>
    [Fact]
    public async Task TheAncestry_ReadsTheTable_FromThisProcess()
    {
        var harness = new HarnessFactory();
        var current = harness.Identity.CurrentId;
        var engine = new SampledProcess(current + 1, null, "dssharness", null, "dssharness run-orchestrator x", null);
        var table = new ProcessTableReading([engine, new SampledProcess(current, engine.Id, "dssharness", null, null)], null);

        var answer = await new AgentAncestry(new FixedTable(table), harness.Identity, new OwnProgramAt(null), harness.Platform).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new AgentAncestryAnswer(engine, null), answer);
    }
}
