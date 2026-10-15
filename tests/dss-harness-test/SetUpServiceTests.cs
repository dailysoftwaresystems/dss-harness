using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.SetUps;

namespace DssHarness.Tests;

/// <summary>
/// <c>up &lt;name&gt;</c> and <c>down &lt;name&gt;</c>: the line for this machine's platform, run at the root of the tree as
/// a program and its arguments, and every refusal met before anything runs.
/// </summary>
public sealed class SetUpServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A set-up's line runs at the root of the tree the command is given a directory of, whichever directory inside it
    /// that is: this platform's line from the map, its quoted argument reaching the script as one word, the name matched
    /// ignoring case and reported as declared.
    /// </summary>
    [Fact]
    public async Task Up_RunsThisPlatformsLine_AtTheRootOfTheTree_AQuotedWordAsOneArgument()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var service = Service(harness, temp, new HarnessConfig { SetUp = { ["e2e"] = new SetUpConfig { Up = SetUpScripts.Line("\"up e2e\""), Down = SetUpScripts.Line("down") } } });
        var inside = Directory.CreateDirectory(Path.Combine(temp.Path, "src", "deep")).FullName;

        SetUpScripts.Write(temp.Path);

        var report = await service.RunAsync(inside, SetUpDirection.Up, "E2E", NoInputs, Token);

        Assert.Equal("e2e", report.Name);
        Assert.Equal(harness.Platform.PlatformKey, report.Platform);
        Assert.True(report.Ran.Succeeded, $"the line exited {report.Ran.ExitCode}");
        Assert.Equal(["up e2e"], SetUpScripts.Logged(temp.Path));
        Assert.Empty(SetUpScripts.Logged(inside));
        Assert.Contains("up: 'e2e' in '", harness.StandardOutput.ToString() + harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A line that exits anything but zero is the command failing, 20, saying the set-up, the direction, the line and the
    /// code; under --json the document says the same, alone.
    /// </summary>
    [Fact]
    public async Task ALineExitingNonZero_IsTheCommandFailing_SayingTheLineAndItsCode()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var service = Service(harness, temp, new HarnessConfig { SetUp = { ["e2e"] = SetUpScripts.SetUp("e2e", downExit: 3) } });

        SetUpScripts.Write(temp.Path);

        var report = await service.RunAsync(temp.Path, SetUpDirection.Down, "e2e", NoInputs, Token);
        var line = report.Ran.Line;
        var text = SetUpReports.Render(report, json: false);
        var json = SetUpReports.Render(report, json: true);

        Assert.Equal(3, report.Ran.ExitCode);
        Assert.Equal(HarnessExit.CommandFailed, text.ExitCode);
        Assert.Equal($"'e2e' down: '{line}' exited 3", text.Message);
        Assert.Equal(HarnessExit.CommandFailed, json.ExitCode);
        Assert.True(json.Quiet, "a document is written alone");

        using var document = JsonDocument.Parse(Assert.Single(json.Data));
        var root = document.RootElement;

        Assert.Equal(
            ["down", "e2e", harness.Platform.PlatformKey],
            new[] { "direction", "name", "platform" }.Select(name => root.GetProperty(name).GetString()));
        Assert.Equal(line, root.GetProperty("ran").GetProperty("line").GetString());
        Assert.Equal(3, root.GetProperty("ran").GetProperty("exitCode").GetInt32());
        Assert.True(root.GetProperty("ran").TryGetProperty("durationSeconds", out _));
        Assert.Equal(["down-e2e"], SetUpScripts.Logged(temp.Path));
    }

    /// <summary>A name setUp does not declare is a usage error, naming what is declared; nothing runs.</summary>
    [Fact]
    public async Task ANameNothingDeclares_IsAUsageError_NamingWhatIs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var service = Service(harness, temp, new HarnessConfig { SetUp = { ["e2e"] = SetUpScripts.SetUp("e2e"), ["db"] = SetUpScripts.SetUp("db") } });

        var refused = await Assert.ThrowsAsync<HarnessException>(() => service.RunAsync(temp.Path, SetUpDirection.Up, "nope", NoInputs, Token));

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal("'nope' is not declared under setUp. Declared: db, e2e.", refused.Message);
        Assert.Empty(SetUpScripts.Logged(temp.Path));
    }

    /// <summary>
    /// A set-up whose line names no other platform than this machine's is refused, 13, naming the platforms it does
    /// name; nothing runs.
    /// </summary>
    [Fact]
    public async Task ASetUpWithNoLineForThisPlatform_IsRefused_NamingTheOnesItHas()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var elsewhere = harness.Platform.PlatformKey == "windows" ? "linux" : "windows";
        var service = Service(harness, temp, new HarnessConfig
        {
            SetUp = { ["e2e"] = new SetUpConfig { Up = SetUpLine.Keyed([new(elsewhere, "scripts/mark.sh up")]), Down = SetUpScripts.Line("down") } },
        });

        SetUpScripts.Write(temp.Path);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => service.RunAsync(temp.Path, SetUpDirection.Up, "e2e", NoInputs, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"setUp 'e2e' up has no command line for {harness.Platform.PlatformKey}: it names {elsewhere}. Nothing was run.", refused.Message);
        Assert.Empty(SetUpScripts.Logged(temp.Path));
    }

    /// <summary>
    /// A line's program is a tool declared under tools, or a path inside the repository: a name nothing declares, and a
    /// path outside the tree, are refused, 13, naming what is declared; a declared tool runs.
    /// </summary>
    [Theory]
    [InlineData("curl http://localhost", "setUp 'e2e' up 'curl' is not declared under 'tools' and is not a path inside the repository (declared: 'git'). Nothing was run.")]
    [InlineData("../outside/up.sh", "setUp 'e2e' up '../outside/up.sh' is not declared under 'tools' and is not a path inside the repository (declared: 'git'). Nothing was run.")]
    [InlineData("git --version", null)]
    public async Task ALinesProgram_IsADeclaredToolOrAPathInsideTheRepository(string line, string? refusal)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var service = Service(harness, temp, new HarnessConfig
        {
            Tools = [new ToolConfig { Name = "git" }],
            SetUp = { ["e2e"] = new SetUpConfig { Up = line, Down = "git --version" } },
        });

        if (refusal is null)
        {
            Assert.True((await service.RunAsync(temp.Path, SetUpDirection.Up, "e2e", NoInputs, Token)).Ran.Succeeded);
            return;
        }

        var refused = await Assert.ThrowsAsync<HarnessException>(() => service.RunAsync(temp.Path, SetUpDirection.Up, "e2e", NoInputs, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(refusal, refused.Message);
    }

    /// <summary>A program inside the repository that is not there cannot start: the command is a tool missing, 14.</summary>
    [Fact]
    public async Task AProgramThatIsNotThere_CannotStart()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var service = Service(harness, temp, new HarnessConfig { SetUp = { ["e2e"] = new SetUpConfig { Up = "scripts/missing.sh", Down = "scripts/missing.sh" } } });

        var failed = await Assert.ThrowsAnyAsync<ProgramStartException>(() => service.RunAsync(temp.Path, SetUpDirection.Up, "e2e", NoInputs, Token));

        Assert.Equal(HarnessExit.ToolMissing, KnownCauses.ExitCodeFor(failed));
    }

    /// <summary>
    /// down is refused, 13, while a leg of a run on this machine still holds the set-up in that tree - naming the leg and
    /// the record - since its work would go on against what is gone; up is not, and a hold in another tree is no hold.
    /// </summary>
    [Fact]
    public async Task Down_WhileALegStillUsesTheSetUp_IsRefused_NamingTheLeg()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var holds = Holds(harness, state);
        var service = Service(harness, temp, new HarnessConfig { SetUp = { ["e2e"] = SetUpScripts.SetUp("e2e") } }, holds);

        SetUpScripts.Write(temp.Path);

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Path.Combine(temp.Path, "elsewhere"), "run-1", "other-tree", _ => { }, Token));
        Assert.True((await service.RunAsync(temp.Path, SetUpDirection.Down, "e2e", NoInputs, Token)).Ran.Succeeded, "a hold in another tree holds nothing here");

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, temp.Path, "run-1", "win", _ => { }, Token));
        holds.Stood("e2e", temp.Path, "run-1", "win");

        var refused = await Assert.ThrowsAsync<HarnessException>(() => service.RunAsync(temp.Path, SetUpDirection.Down, "E2E", NoInputs, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith($"set-up 'e2e' in '{temp.Path}' is still used by leg 'win' (", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith($"the last of them brings it down as it ends (record: '{holds.Location}'). Nothing was run.", refused.Message, StringComparison.Ordinal);
        Assert.True((await service.RunAsync(temp.Path, SetUpDirection.Up, "e2e", NoInputs, Token)).Ran.Succeeded);
        Assert.Equal(["down-e2e", "up-e2e"], SetUpScripts.Logged(temp.Path));
    }

    /// <summary>
    /// down of a set-up the machine shares is refused while a leg in any tree of the repository on the machine still uses
    /// it, naming the tree that leg works in; another repository's leg holding a set-up of that name holds another.
    /// </summary>
    [Fact]
    public async Task Down_OfASetUpTheMachineShares_IsRefused_WhileALegInAnotherTreeUsesIt()
    {
        using var temp = new TempDirectory();
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var holds = Holds(harness, state);
        var shared = SetUpScripts.SetUp("e2e");
        var elsewhere = Path.Combine(temp.Path, "elsewhere");
        var worktree = Directory.CreateDirectory(Path.Combine(temp.Path, "worktree")).FullName;
        var service = Service(harness, temp, new HarnessConfig { SetUp = { ["e2e"] = new SetUpConfig { Up = shared.Up, Down = shared.Down, Shared = SetUpConfig.SharedByMachine } } }, holds, worktree);

        SetUpScripts.Write(worktree);

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Path.Combine(temp.Path, "..", "another-repository"), "run-0", "another-repository", _ => { }, Token, new SetUpSharing(Path.Combine(temp.Path, "..", "another-repository"), MachineWide: true)));
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, elsewhere, "run-1", "other-tree", _ => { }, Token, new SetUpSharing(Path.TrimEndingDirectorySeparator(Path.GetFullPath(temp.Path)), MachineWide: true)));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => service.RunAsync(temp.Path, SetUpDirection.Down, "e2e", NoInputs, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith("set-up 'e2e' on this machine is still used by leg 'other-tree' (", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("another-repository", refused.Message, StringComparison.Ordinal);
        Assert.Contains($") in '{elsewhere}': the last of them brings it down as it ends", refused.Message, StringComparison.Ordinal);
        Assert.Empty(SetUpScripts.Logged(temp.Path));
    }

    /// <summary>
    /// The commands, run as a user runs them: up and down exit as their line does, --json writes the document alone,
    /// and a name nothing declares answers in the command's own stopped shape, 10.
    /// </summary>
    [Fact]
    public async Task TheCommands_ExitAsTheLineDoes_AndAnswerInTheirOwnShape()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { SetUp = { ["e2e"] = SetUpScripts.SetUp("e2e", downExit: 4) } });
        SetUpScripts.Write(temp.Path);

        var up = await CliRunner.RunAsync(["up", "e2e", "--json", "-C", temp.Path], Token);
        var down = await CliRunner.RunAsync(["down", "e2e", "-C", temp.Path], Token);
        var nope = await CliRunner.RunAsync(["down", "nope", "--json", "-C", temp.Path], Token);

        Assert.True(up.ExitCode == 0, up.StandardError);
        Assert.Equal("up", JsonDocument.Parse(up.StandardOutput).RootElement.GetProperty("direction").GetString());
        Assert.Equal(HarnessExit.CommandFailed, down.ExitCode);
        Assert.Contains("'e2e' down: 'scripts/mark.", down.StandardOutput + down.StandardError, StringComparison.Ordinal);
        Assert.Equal(HarnessExit.UsageError, nope.ExitCode);

        var stopped = JsonDocument.Parse(nope.StandardOutput).RootElement;

        Assert.Equal(["exitCode", "summary", "direction"], stopped.EnumerateObject().Select(member => member.Name));
        Assert.Equal("down", stopped.GetProperty("direction").GetString());
        Assert.Equal(["up-e2e", "down-e2e"], SetUpScripts.Logged(temp.Path));
    }

    /// <summary>
    /// A set-up's inputs fill in its lines, each argument apart: what --input gives over the input's default, matched
    /// ignoring case. The values are said as the line runs, and in the report and its document.
    /// </summary>
    [Fact]
    public async Task Inputs_FillTheLine_WhatIsGivenOverTheDefault()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var service = Service(harness, temp, new HarnessConfig { SetUp = { ["e2e"] = WithInputs() } });

        SetUpScripts.Write(temp.Path);

        var up = await service.RunAsync(temp.Path, SetUpDirection.Up, "e2e", new Dictionary<string, string> { ["PROFILE"] = "ci two" }, Token);
        var down = await service.RunAsync(temp.Path, SetUpDirection.Down, "e2e", NoInputs, Token);

        Assert.Equal(["up-ci two-5432", "down-dev"], SetUpScripts.Logged(temp.Path));
        Assert.Equal(new Dictionary<string, string> { ["profile"] = "ci two", ["port"] = "5432" }, up.Inputs);
        Assert.Contains("up: 'e2e' (port=5432, profile=ci two) in '", harness.StandardOutput.ToString() + harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("'e2e' (port=5432, profile=ci two) up: '", SetUpReports.Render(up, json: false).Message, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(Assert.Single(SetUpReports.Render(down, json: true).Data));

        Assert.Equal("dev", document.RootElement.GetProperty("inputs").GetProperty("profile").GetString());
    }

    /// <summary>
    /// A value for an input the set-up does not declare, and an input given nothing that has no default, are a usage
    /// error, 10, every one named with what the input is for; nothing runs.
    /// </summary>
    [Fact]
    public async Task InputsThatCannotBeHad_AreAUsageError_EveryOneNamed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var setUp = WithInputs();

        setUp.Inputs["region"] = new SetUpInput { Description = "the region the services run in" };

        var service = Service(harness, temp, new HarnessConfig { SetUp = { ["e2e"] = setUp } });

        SetUpScripts.Write(temp.Path);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => service.RunAsync(
            temp.Path, SetUpDirection.Up, "e2e", new Dictionary<string, string> { ["nope"] = "1", ["port"] = "1", ["PORT"] = "2" }, Token));

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal(
            "--input gives input 'port' of set-up 'e2e' 2 values, as 'port' and 'PORT': an input is named ignoring case; "
            + "--input gives 'nope' a value, and set-up 'e2e' declares no such input: it declares profile, port, region; "
            + "--input gives input 'region' of set-up 'e2e' no value, and it has no default (the region the services run in). Nothing was run.",
            refused.Message);
        Assert.Empty(SetUpScripts.Logged(temp.Path));
    }

    /// <summary>up and down take --input, once per input, as run does; one given twice is refused before anything runs.</summary>
    [Fact]
    public async Task TheCommands_TakeInput_OncePerInput()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { SetUp = { ["e2e"] = WithInputs() } });
        SetUpScripts.Write(temp.Path);

        var up = await CliRunner.RunAsync(["up", "e2e", "--input", "profile=ci", "--input", "port=1", "-C", temp.Path], Token);
        var twice = await CliRunner.RunAsync(["down", "e2e", "--input", "profile=a", "--input", "profile=b", "-C", temp.Path], Token);

        Assert.True(up.ExitCode == 0, up.StandardError);
        Assert.Equal(HarnessExit.UsageError, twice.ExitCode);
        Assert.Contains("--input gives 'profile' a value more than once", twice.StandardOutput + twice.StandardError, StringComparison.Ordinal);
        Assert.Equal(["up-ci-1"], SetUpScripts.Logged(temp.Path));
    }

    /// <summary>A set-up whose lines name its two inputs, one of them in the middle of an argument.</summary>
    private static SetUpConfig WithInputs() => new()
    {
        Up = SetUpScripts.Line("up-{profile}-{port} 0"),
        Down = SetUpScripts.Line("down-{profile} 0"),
        Inputs =
        {
            ["profile"] = new SetUpInput { Default = "dev" },
            ["port"] = new SetUpInput { Default = "5432" },
        },
    };
    private static IReadOnlyDictionary<string, string> NoInputs { get; } = new Dictionary<string, string>();

    private static SetUpHolds Holds(HarnessFactory harness, TempDirectory state)
        => new(harness.FileSystem, harness.Output, harness.Identity, harness.Platform, () => state.Combine("setups.json"));

    private static SetUpService Service(HarnessFactory harness, TempDirectory temp, HarnessConfig config, SetUpHolds? holds = null, string? tree = null)
        => new(
            HostDoubles.Loader(config, tree ?? temp.Path, temp.Path),
            harness.ProcessRunner,
            harness.Platform,
            new ActionToolPolicy(harness.Platform),
            new LocalProgramResolver(harness.Platform, FilePermissionsFactory.Create()),
            holds ?? new SetUpHolds(harness.FileSystem, harness.Output, harness.Identity, harness.Platform, () => Path.Combine(temp.Path, ".holds", "setups.json")),
            harness.Output);
}
