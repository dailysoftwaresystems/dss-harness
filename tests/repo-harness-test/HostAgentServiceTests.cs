using RepoHarness.Core.Sync;
using RepoHarness.Core.Configuration;
using System.Globalization;
using RepoHarness.Core.Output;
using RepoHarness.Core.Execution;
using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>What the DssHarness on a host answers, what it agrees to run, and how it says a run ended.</summary>
public sealed class HostAgentServiceTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private static readonly Func<string, string[], Dispatch, CancellationToken, Task<int>> NothingRuns
        = (_, _, _, _) => throw new InvalidOperationException("Nothing should have run.");

    [Fact]
    public async Task Info_AnswersWithThisBuild_AndThisMachine()
    {
        using var output = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader("""{"kind":"info"}"""),
            output,
            new StringWriter(),
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);

        var info = JsonSerializer.Deserialize<HostAgentInfo>(output.ToString(), HostAgentProtocol.JsonOptions);
        Assert.NotNull(info);
        Assert.Equal("1.2.3", info.Version);
        Assert.Equal("abc123", info.AssemblySha256);
        Assert.Equal("linux", info.Os);
        Assert.Equal("arm64", info.Processor);
    }

    /// <summary>
    /// Asked about the room, the host answers the room where its copies are kept, and for each build directory
    /// whether it is there, what the build that last finished there recorded it came to, and the room where it
    /// is - or would be. A path from the home directory is read from this user's; one no build recorded says
    /// nothing about what it holds. Nothing is walked.
    /// </summary>
    [Fact]
    public async Task Info_AnswersTheRoom_AndWhatEachBuildDirectoryRecorded()
    {
        using var home = new TempDirectory();
        var built = home.Combine(Path.Combine("repo", "build", "arm64-gcc-debug"));
        home.WriteFile(Path.Combine("repo", "build", "arm64-gcc-debug", ".harness-build"), "clean\narm64-gcc-debug\nsize 4096\n");
        home.WriteFile(Path.Combine("repo", "build", "arm64-gcc-release", "unrecorded.o"), "x");
        using var output = new StringWriter();

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Info,
                SpaceAt = "~/repo",
                Builds = ["~/repo/build/arm64-gcc-debug", built, "~/repo/build/arm64-gcc-release", "~/repo.worktree-x/build/arm64-gcc-debug"],
            },
            HostAgentProtocol.JsonOptions);

        await Service(home.Path).ServeAsync(new StringReader(request), output, new StringWriter(), NothingRuns, TestContext.Current.CancellationToken);

        var info = JsonSerializer.Deserialize<HostAgentInfo>(output.ToString(), HostAgentProtocol.JsonOptions)!;

        Assert.NotNull(info.Space);
        Assert.True(info.Space.TotalBytes > 0);
        Assert.Null(info.SpaceUnmeasured);

        Assert.Equal(
            [
                ("~/repo/build/arm64-gcc-debug", true, (long?)4096),
                (built, true, 4096),
                ("~/repo/build/arm64-gcc-release", true, null),
                ("~/repo.worktree-x/build/arm64-gcc-debug", false, null),
            ],
            info.Builds.Select(room => (room.Path, room.Exists, room.RecordedBytes)));
        Assert.All(info.Builds, room => Assert.Equal(info.Space.Filesystem, room.Disk?.Filesystem));
    }

    [Fact]
    public async Task Run_RunsTheCommand_InTheHostsCopy_WithItsArgumentsUnchanged_AndSaysHowItFinished()
    {
        using var copy = new TempDirectory();
        using var error = new StringWriter();
        string? ranIn = null;
        string[]? ranWith = null;

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(copy.Path, "read-anchor", "D-A B", "--json")),
            new StringWriter(),
            error,
            (directory, arguments, _, _) =>
            {
                ranIn = directory;
                ranWith = arguments;
                return Task.FromResult(4);
            },
            TestContext.Current.CancellationToken);

        // The command's own exit code is what the host reports, unchanged.
        Assert.Equal(4, exitCode);
        Assert.Equal(copy.Path, ranIn);
        Assert.NotNull(ranWith);
        Assert.Equal(["read-anchor", "D-A B", "--json"], ranWith);

        // The first line marks where this request's own output begins, so that whatever the host's login
        // shell wrote to the same stream before the agent ran is not taken for it; the last says how the
        // command finished, where the machine that asked reads it instead of from ssh's exit code.
        Assert.Equal(
            [HostAgentProtocol.StartedLine(Nonce), HostAgentProtocol.CompletionLine(Nonce, 4)],
            error.ToString().TrimEnd().Split('\n').Select(line => line.TrimEnd('\r')));
    }

    /// <summary>
    /// What a request says of the command it asks for beside its line - the run it is a leg of, the drive its WSL disk grows
    /// on - is handed to the command, never added to its arguments; a run that is not a run id is refused, and nothing runs.
    /// </summary>
    [Theory]
    [InlineData("20261008-120000-0a1b2c3d", true)]
    [InlineData("../../somewhere", false)]
    public async Task Run_HandsTheCommandWhatTheRequestSaysOfIt_AndRefusesARunThatIsNotARunId(string run, bool valid)
    {
        using var copy = new TempDirectory();
        using var error = new StringWriter();
        Dispatch? ranFor = null;
        string[]? ranWith = null;
        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,
                Directory = copy.Path,
                Arguments = ["build", "--json"],
                Nonce = Nonce,
                RunId = run,
                DiskImageDrive = "C:\\",
            },
            HostAgentProtocol.JsonOptions);

        var exitCode = await Service().ServeAsync(
            new StringReader(request),
            new StringWriter(),
            error,
            (_, arguments, dispatched, _) =>
            {
                (ranWith, ranFor) = (arguments, dispatched);
                return Task.FromResult(0);
            },
            TestContext.Current.CancellationToken);

        if (valid)
        {
            Assert.Equal(HarnessExit.Success, exitCode);
            Assert.Equal(new Dispatch(run, "C:\\"), ranFor);
            Assert.Equal(["build", "--json"], ranWith!);
        }
        else
        {
            Assert.Equal(HarnessExit.UsageError, exitCode);
            Assert.Null(ranWith);
            Assert.Contains("the request names its run as '../../somewhere', which is not a run id", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Run_FindsACopyNamedFromTheHomeDirectory()
    {
        using var home = new TempDirectory();
        Directory.CreateDirectory(home.Combine("src", "repo"));
        string? ranIn = null;

        var exitCode = await Service(home.Path).ServeAsync(
            new StringReader(RunRequest("~/src/repo", "verify-git")),
            new StringWriter(),
            new StringWriter(),
            (directory, _, _, _) =>
            {
                ranIn = directory;
                return Task.FromResult(0);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);
        Assert.Equal(Path.GetFullPath(home.Combine("src", "repo")), ranIn);
    }

    [Fact]
    public async Task Run_ReportsAHostWithNoCopyOfTheRepository_AsUnavailable_InItsCompletionLineToo()
    {
        using var temp = new TempDirectory();
        using var error = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(temp.Combine("absent"), "verify-git")),
            new StringWriter(),
            error,
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, exitCode);
        Assert.Contains("no copy of the repository", error.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(
            HostAgentProtocol.CompletionLine(Nonce, HarnessExit.HostUnavailable),
            error.ToString().TrimEnd(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The copy a host has not got is named from its home, as <c>~</c>, as it is in every line the host writes
    /// for another machine: the machine that asked quotes the refusal into every leg it reports.
    /// </summary>
    [Fact]
    public async Task Run_NamesACopyItHasNot_FromItsHome()
    {
        using var home = new TempDirectory();
        using var error = new StringWriter();

        var exitCode = await Service(home.Path, current: ThisPlatform).ServeAsync(
            new StringReader(RunRequest("~/src/absent", "verify-git")),
            new StringWriter(),
            error,
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, exitCode);
        Assert.Contains($"this host has no copy of the repository at '{Path.Combine("~", "src", "absent")}'", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(home.Path, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An answer about the host names from its home every place it only describes - where each program is, and
    /// where the filesystem its copies are on is mounted - and names in full the directories the programs were
    /// found in, which the machine that asked hands back for a hold here to look for its command in.
    /// </summary>
    [Fact]
    public async Task Info_NamesWhatItDescribesFromItsHome_AndTheDirectoriesItFoundProgramsInInFull()
    {
        using var home = new TempDirectory();
        var tool = home.WriteProgram("bin", "harness-home-tool");
        using var output = new StringWriter();

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Info,
                Programs = ["harness-home-tool"],
                ToolSearchDirectories = new(StringComparer.OrdinalIgnoreCase) { [HostDoubles.Platform(ThisPlatform).PlatformKey] = ["~/bin"] },
                SpaceAt = "~/repo",
            },
            HostAgentProtocol.JsonOptions);

        await Service(home.Path, current: ThisPlatform, files: new MountedAt(home.Path)).ServeAsync(
            new StringReader(request),
            output,
            new StringWriter(),
            NothingRuns,
            TestContext.Current.CancellationToken);

        var info = JsonSerializer.Deserialize<HostAgentInfo>(output.ToString(), HostAgentProtocol.JsonOptions)!;

        Assert.Equal("~", info.Space?.Filesystem);
        Assert.Equal("~" + tool[home.Path.Length..], Assert.Single(info.Programs).Path);
        Assert.Equal([Path.GetDirectoryName(tool)!], info.ProgramDirectories);
    }

    /// <summary>
    /// Every reason an answer gives names the host's home as <c>~</c> - why the room here, or a build
    /// directory's, could not be measured, and why an emulator cannot run - while each build directory is named
    /// as the machine that asked named it, which it matches the answer to its question by.
    /// </summary>
    [Fact]
    public async Task Info_TellsEveryReasonFromItsHome_AndNamesEachBuildDirectoryAsItWasAsked()
    {
        using var home = new TempDirectory();
        var build = home.Combine("repo", "build", "x");
        var absent = home.Combine("bin", "absent");
        var platform = HostDoubles.Platform(ThisPlatform, "arm64");
        using var output = new StringWriter();

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Info,
                SpaceAt = "~/repo",
                Builds = [build],
                Emulators = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["qemu"] = new EmulatorConfig
                    {
                        HostOs = platform.PlatformKey,
                        HostProcessor = "arm64",
                        Processor = "x86_64",
                        Requires = [absent],
                        Witness = new EmulatorWitness { Command = ["witness"], Pattern = "x" },
                    },
                },
            },
            HostAgentProtocol.JsonOptions);

        await Service(home.Path, current: ThisPlatform, files: new Unmeasurable()).ServeAsync(
            new StringReader(request),
            output,
            new StringWriter(),
            NothingRuns,
            TestContext.Current.CancellationToken);

        var info = JsonSerializer.Deserialize<HostAgentInfo>(output.ToString(), HostAgentProtocol.JsonOptions)!;
        var room = Assert.Single(info.Builds);

        Assert.Equal($"'{Path.Combine("~", "repo")}' cannot be measured", info.SpaceUnmeasured);
        Assert.Equal(build, room.Path);
        Assert.Equal($"'{Path.Combine("~", "repo", "build", "x")}' cannot be measured", room.Unmeasured);
        Assert.Equal($"{Path.Combine("~", "bin", "absent")} is missing", info.Emulators["qemu"].Reason);
    }

    [Fact]
    public async Task Run_ReportsACopyThatCannotBeEntered_AsUnavailable()
    {
        using var copy = new TempDirectory();
        using var error = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(copy.Path, "verify-git")),
            new StringWriter(),
            error,
            (_, _, _, _) => throw new UnauthorizedAccessException("Access to the path is denied."),
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, exitCode);
        Assert.Contains("could not be entered: Access to the path is denied.", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_CancelsTheCommand_WhenTheInputEnds_BecauseTheMachineThatAskedHasGone()
    {
        using var copy = new TempDirectory();
        using var input = new HeldOpenReader(RunRequest(copy.Path, "verify-git"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;

        var serving = Service().ServeAsync(
            input,
            new StringWriter(),
            new StringWriter(),
            async (_, _, _, token) =>
            {
                started.SetResult();

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return HarnessExit.Success;
                }
                catch (OperationCanceledException)
                {
                    return HarnessExit.Cancelled;
                }
            },
            cancellationToken);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        input.End();

        Assert.Equal(HarnessExit.Cancelled, await serving.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
    }

    [Fact]
    public async Task Run_LeavesTheCommandRunning_WhileTheInputStaysOpen()
    {
        using var copy = new TempDirectory();
        using var input = new HeldOpenReader(RunRequest(copy.Path, "verify-git"));
        var cancellationToken = TestContext.Current.CancellationToken;
        bool? cancelled = null;

        var exitCode = await Service().ServeAsync(
            input,
            new StringWriter(),
            new StringWriter(),
            async (_, _, _, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                cancelled = token.IsCancellationRequested;
                return HarnessExit.Success;
            },
            cancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);
        Assert.False(cancelled);
    }

    [Theory]
    [InlineData(HostExecService.CommandName)]
    [InlineData(HostAgentProtocol.CommandName)]
    [InlineData(HoldAwakeService.CommandName)]
    public async Task Run_RefusesToPassTheWorkOnToAnotherHost(string command)
    {
        using var temp = new TempDirectory();

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(temp.Path, command, "--ssh", "other", "--", "verify-git")),
            new StringWriter(),
            new StringWriter(),
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, exitCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"kind":"info","protocol":99}""")]
    [InlineData("""{"kind":"run","arguments":[]}""")]
    [InlineData("""{"kind":"run","directory":"/r","arguments":["verify-git"]}""")]
    [InlineData("""{"kind":"info","unexpected":true}""")]
    [InlineData("""{"kind":"run","directory":"/r","arguments":["verify-git",7],"nonce":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"kind":"run","directory":"/r","arguments":["verify-git",{}],"nonce":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"kind":"run","directory":"/r","arguments":["verify-git",null],"nonce":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"kind":"run","directory":"/r","arguments":[null],"nonce":"0123456789abcdef0123456789abcdef"}""")]
    [InlineData("""{"kind":"info","emulators":{"qemu":{"hostOs":"linux","hostProcessor":"x86_64","processor":"arm64","witness":{"command":["w"],"pattern":"x"}},"QEMU":{}}}""")]
    public async Task ARequestThatCannotBeServed_IsAUsageError_Explained(string request)
    {
        using var error = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader(request),
            new StringWriter(),
            error,
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, exitCode);
        Assert.StartsWith("host-agent: FAIL - ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestInAnotherProtocol_IsRefusedAsThat_ThoughItHasFieldsThisBuildDoesNotKnow()
    {
        using var error = new StringWriter();

        var other = HostAgentProtocol.Version + 1;

        var exitCode = await Service().ServeAsync(
            new StringReader($$"""{"kind":"info","protocol":{{other}},"addedLater":true}"""),
            new StringWriter(),
            error,
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, exitCode);
        Assert.Contains(
            $"speaks protocol {other}, and DssHarness 1.2.3 on this host speaks {HostAgentProtocol.Version}",
            error.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Asked about a developer environment, a host says whether it can set it up there, under the name
    /// it was asked by, compared ignoring case; this one runs Linux, where Visual Studio never does.
    /// </summary>
    [Fact]
    public async Task Info_SaysWhichDeveloperEnvironmentsCanBeSetUpHere()
    {
        using var output = new StringWriter();

        var exitCode = await Service().ServeAsync(
            new StringReader("""{"kind":"info","developerEnvironments":{"VS":{"kind":"visualStudio"}}}"""),
            output,
            new StringWriter(),
            NothingRuns,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);

        var info = JsonSerializer.Deserialize<HostAgentInfo>(output.ToString(), HostAgentProtocol.JsonOptions);
        Assert.NotNull(info);

        var check = info.DeveloperEnvironments["vs"];
        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal("Visual Studio is set up on windows, and this host runs linux", check.Reason);
    }

    [Fact]
    public void ARequestAndAnAnswer_ReadBack_CompareEmulatorNamesIgnoringCase()
    {
        // Emulator names are names a person typed in config.json, where case is ignored. Read back on
        // either side of a host, they must compare the same way, not by the serializer's default comparer.
        var request = JsonSerializer.Deserialize<HostAgentRequest>(
            """{"kind":"info","emulators":{"Qemu-Arm64":{"hostOs":"linux","hostProcessor":"x86_64","processor":"arm64","witness":{"command":["uname","-m"],"pattern":"^aarch64$"}}}}""",
            HostAgentProtocol.JsonOptions);
        var info = JsonSerializer.Deserialize<HostAgentInfo>(
            """{"version":"1.2.3","assemblySha256":"abc123","os":"linux","processor":"x86_64","emulators":{"Qemu-Arm64":{"available":true,"witnessed":"aarch64"}}}""",
            HostAgentProtocol.JsonOptions);

        Assert.NotNull(request);
        Assert.NotNull(info);
        Assert.True(request.Emulators.ContainsKey("qemu-arm64"));
        Assert.True(info.Emulators.ContainsKey("qemu-arm64"));
    }

    /// <summary>
    /// A host holds itself awake while it serves a request, by the command the machine that asked carries for
    /// it, filled in with the agent's own process there so that it ends with the request however the
    /// connection ends. A sync to a fresh copy is the longest work a host does with no leg of its own running
    /// there, and a leg's own hold was all that ever kept one awake: a consumer's first sync of a worktree's
    /// copy ran past a Mac's wake, and both its legs were left unavailable.
    /// </summary>
    [Fact]
    public async Task Run_HoldsTheHostAwake_WhileItServesTheRequest()
    {
        using var copy = new TempDirectory();
        var processes = new HeldProcesses();
        var heldWhileRunning = false;

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,
                Directory = copy.Path,
                Arguments = [SyncServe.CommandName, SyncServe.WriteMany],
                KeepAwake = ["caffeinate", "-dimsu", "-w", "{pid}"],
                Nonce = Nonce,
            },
            HostAgentProtocol.JsonOptions);

        await Service(keepingAwake: processes).ServeAsync(
            new StringReader(request),
            new StringWriter(),
            new StringWriter(),
            (_, _, _, _) =>
            {
                heldWhileRunning = processes.Started.Count == 1;
                return Task.FromResult(0);
            },
            TestContext.Current.CancellationToken);

        Assert.True(heldWhileRunning, "the host was not held awake while the request was served");

        var held = processes.Started[0].Request;
        Assert.Equal("caffeinate", held.FileName);
        Assert.Equal(["-dimsu", "-w", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], held.Arguments);
    }

    /// <summary>
    /// A request carrying no such command starts nothing: a host whose configuration declares none is held
    /// awake by nothing, here as when a leg runs on it.
    /// </summary>
    [Fact]
    public async Task Run_StartsNothing_WhereTheRequestCarriesNoCommandToHoldTheHostAwake()
    {
        using var copy = new TempDirectory();
        var processes = new HeldProcesses();

        await Service(keepingAwake: processes).ServeAsync(
            new StringReader(RunRequest(copy.Path, "read-anchor", "D-A B")),
            new StringWriter(),
            new StringWriter(),
            (_, _, _, _) => Task.FromResult(0),
            TestContext.Current.CancellationToken);

        Assert.Empty(processes.Started);
    }

    /// <summary>
    /// A request written as it is encoded is one line - the very line the serializer writes of it whole - and reads back as
    /// the request: each argument as its text, and what an argument carries as the base64 text of its bytes, however its
    /// length falls against the pieces it is encoded in, and a text longer than what is gathered before it is written on.
    /// </summary>
    [Fact]
    public void ARequestWrittenAsItIsEncoded_IsTheLineTheSerializerWrites_AndReadsBackWithWhatItCarriesAsBase64()
    {
        var random = new Random(9);
        int[] lengths = [0, 1, HostAgentProtocol.CarriedPiece - 1, HostAgentProtocol.CarriedPiece, HostAgentProtocol.CarriedPiece + 1, (3 * HostAgentProtocol.CarriedPiece) + 2];
        var carried = lengths.Select(length => { var bytes = new byte[length]; random.NextBytes(bytes); return bytes; }).ToList();
        var longText = string.Concat(Enumerable.Repeat("João + \"quoted\" / ", 20_000));

        var textOnly = new HostAgentRequest
        {
            Kind = HostAgentRequestKind.Run,
            Directory = "~/src/repo",
            Arguments = [SyncServe.CommandName, SyncServe.Index, SyncServe.OperandsFollow, "/r", longText, "a+b/c=d"],
            KeepAwakeEnvironment = new() { ["LANG"] = "pt_BR.UTF-8" },
            Nonce = Nonce,
        };

        Assert.Equal(JsonSerializer.Serialize(textOnly, HostAgentProtocol.JsonOptions) + "\n", HostAgentProtocol.Input(textOnly).Read());

        var line = HostAgentProtocol.Input(new HostAgentRequest
        {
            Kind = HostAgentRequestKind.Run,
            Directory = textOnly.Directory,
            Arguments = [.. textOnly.Arguments, .. carried.Select(bytes => HostArgument.Carrying(bytes))],
            Nonce = Nonce,
        }).Read();

        Assert.Single(line, character => character == '\n');
        Assert.EndsWith("\n", line, StringComparison.Ordinal);

        var read = JsonSerializer.Deserialize<HostAgentRequest>(line, HostAgentProtocol.JsonOptions)!;

        Assert.Equal(
            [.. textOnly.Arguments.Select(argument => argument.Text), .. carried.Select(Convert.ToBase64String)],
            read.Arguments.Select(argument => argument.Text));
    }

    /// <summary>
    /// Windows on Windows and Linux anywhere else, so the paths the tests make are read as this machine writes them.
    /// </summary>
    private static PlatformId ThisPlatform => OperatingSystem.IsWindows() ? PlatformId.Windows : PlatformId.Linux;

    private static string RunRequest(string directory, params string[] arguments)
        => JsonSerializer.Serialize(
            new HostAgentRequest { Kind = HostAgentRequestKind.Run, Directory = directory, Arguments = [.. arguments], Nonce = Nonce },
            HostAgentProtocol.JsonOptions);

    private static HostAgentService Service(
        string? home = null,
        IProcessRunner? keepingAwake = null,
        PlatformId current = PlatformId.Linux,
        IFileSystem? files = null)
    {
        var platform = HostDoubles.Platform(current, "arm64", home);

        var identity = Substitute.For<IToolIdentityProvider>();
        identity.Current.Returns(new ToolIdentity("1.2.3", "abc123"));

        var fileSystem = files ?? new PhysicalFileSystem(FilePermissionsFactory.Create());
        var processRunner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create());

        return new HostAgentService(
            platform,
            identity,
            new EmulatorProbe(platform, processRunner, fileSystem),
            new DeveloperEnvironmentProbe(platform, processRunner),
            fileSystem,
            new LocalProgramResolver(platform, FilePermissionsFactory.Create()),
            new KeepAwake(keepingAwake ?? processRunner, new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false)),
                new HoldAwakeStore(new PhysicalFileSystem(FilePermissionsFactory.Create()), Path.Combine(TestHost.TemporaryRoot, "holds", Guid.NewGuid().ToString("N") + ".json")),
                new RecordingLauncher(),
                HomeShorthand.Of(platform, fileSystem));
    }

    /// <summary>The real file system, except that every filesystem is mounted at <paramref name="mount"/>.</summary>
    private sealed class MountedAt(string mount) : PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create()))
    {
        public override DiskSpace SpaceAt(string path) => base.SpaceAt(path) with { Filesystem = mount };
    }

    /// <summary>The real file system, except that the room at no path can be measured, as the system says it of one.</summary>
    private sealed class Unmeasurable() : PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create()))
    {
        public override DiskSpace SpaceAt(string path) => throw new IOException($"'{path}' cannot be measured.");
    }
}
