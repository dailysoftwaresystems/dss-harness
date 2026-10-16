using DssHarness.Core.Sync;
using DssHarness.Core.Configuration;
using System.Globalization;
using DssHarness.Core.Output;
using DssHarness.Core.Execution;
using System.Text.Json;
using NSubstitute;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Mutations;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Tools;

namespace DssHarness.Tests;

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
    /// on - is handed to the command, never added to its arguments; a run that is not a run id, and a blank drive, are
    /// refused, and nothing runs.
    /// </summary>
    [Theory]
    [InlineData("20261008-120000-0a1b2c3d", "C:\\", null)]
    [InlineData("../../somewhere", "C:\\", "the request names its run as '../../somewhere', which is not a run id")]
    [InlineData("20261008-120000-0a1b2c3d", " ", "the request names a blank drive as where WSL keeps the distribution's disk")]
    public async Task Run_HandsTheCommandWhatTheRequestSaysOfIt_AndRefusesARunThatIsNotARunId(string run, string drive, string? refusal)
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
                DiskImageDrive = drive,
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

        if (refusal is null)
        {
            Assert.Equal(HarnessExit.Success, exitCode);
            Assert.NotNull(ranFor);
            Assert.Equal((run, drive, Nonce), (ranFor.RunId?.ToString(), ranFor.DiskImageDrive, ranFor.Nonce));
            Assert.Equal(["build", "--json"], ranWith!);
        }
        else
        {
            Assert.Equal(HarnessExit.UsageError, exitCode);
            Assert.Null(ranWith);
            Assert.Contains(refusal, error.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A command a host serves for another machine tells it something while it runs by notes on its own standard error:
    /// each whole note cut into pieces of at most <see cref="HostAgentProtocol.NotePiece"/> bytes, each piece one line
    /// carrying the request's nonce, its kind and whether it is the last, so the machine that asked reads it whole and
    /// relays none of it. A note of nothing is one line, so no reader is left waiting for a last piece that says nothing;
    /// and nothing is written where no machine asked, which is a command typed on the host by hand.
    /// </summary>
    [Fact]
    public void ACommandServedForAnotherMachine_WritesItsNotesInPiecesOfOneLineEach_AndNothingWhereNoMachineAsked()
    {
        var lines = new StringWriter();
        var notes = new HostNotes(
            CommandOrigin.Served(Dispatch.Of(new HostAgentRequest { Kind = HostAgentRequestKind.Run, Arguments = ["check-mutations"], Nonce = Nonce })),
            new ConsoleHarnessOutput(new StringWriter(), lines, verbose: false));

        notes.Write(NoteSink.ArmKind, new string('t', HostAgentProtocol.NotePiece(NoteSink.ArmKind) * 2 + 3));
        notes.Write(NoteSink.VerifiedKind, string.Empty);
        new HostNotes(CommandOrigin.Typed, new ConsoleHarnessOutput(new StringWriter(), lines, verbose: false)).Write(NoteSink.ArmKind, "typed by hand");

        var written = lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();

        Assert.Equal(4, written.Count);

        var gathered = new List<byte>();

        foreach (var (line, last) in written.Take(3).Select((line, at) => (line, at == 2)))
        {
            Assert.True(HostAgentProtocol.TryReadNoteLine(line, out var nonce, out var kind, out var readLast, out var base64));
            Assert.Equal((Nonce, NoteSink.ArmKind, last), (nonce, kind, readLast));
            gathered.AddRange(Convert.FromBase64String(base64));
        }

        Assert.Equal(new string('t', HostAgentProtocol.NotePiece(NoteSink.ArmKind) * 2 + 3), System.Text.Encoding.UTF8.GetString([.. gathered]));

        Assert.True(HostAgentProtocol.TryReadNoteLine(written[3], out var nonceOfNone, out var kindOfNone, out var lastOfNone, out var base64OfNone));
        Assert.Equal((Nonce, NoteSink.VerifiedKind, true, ""), (nonceOfNone, kindOfNone, lastOfNone, base64OfNone));
    }

    /// <summary>
    /// Every line a note is cut into fits whole what a stream kept as a tail is handed on in - the longest line the
    /// process reader hands on, which cuts a longer one in two and would land the note's base64 damaged: fed through that
    /// very cut, each piece arrives whole and the note reads back as it was written, however many pieces it took.
    /// </summary>
    [Fact]
    public void EachLineOfANote_FitsWhatAReaderHandsOnInPieces()
    {
        var lines = new StringWriter();
        var notes = new HostNotes(
            CommandOrigin.Served(Dispatch.Of(new HostAgentRequest { Kind = HostAgentRequestKind.Run, Arguments = ["check-mutations"], Nonce = Nonce })),
            new ConsoleHarnessOutput(new StringWriter(), lines, verbose: false));
        var text = new string('t', HostAgentProtocol.NotePiece(NoteSink.VerifiedKind) * 2 + 3);

        notes.Write(NoteSink.VerifiedKind, text);

        var written = lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();

        // The real cut the dispatcher's tail-kept standard error is read through.
        var read = new List<string>();
        var splitter = new LineSplitter(read.Add, ProcessRunner.LongestLine);

        foreach (var line in written)
        {
            splitter.Add(line + "\n");
        }

        splitter.End();

        Assert.Equal(written, read);

        var noted = new List<(string Kind, string Text)>();
        var agent = new HostAgentLines(Nonce, (kind, body) => noted.Add((kind, body)));

        foreach (var line in read)
        {
            Assert.False(agent.Error(line), "no piece of a note is relayed");
        }

        Assert.Equal([(NoteSink.VerifiedKind, text)], noted);
    }

    /// <summary>
    /// A note of the request is never relayed, whether or not the machine that asked reads notes: where it reads none - a
    /// sweep typed by hand in a synced copy, which records nothing - each line of it is dropped, before the agent serves
    /// and after, while a line of the command's own and a note of another request are relayed as ever.
    /// </summary>
    [Fact]
    public void ANoteOfTheRequest_IsDroppedNeverRelayed_WhereTheMachineThatAskedReadsNone()
    {
        var lines = new StringWriter();
        var notes = new HostNotes(
            CommandOrigin.Served(Dispatch.Of(new HostAgentRequest { Kind = HostAgentRequestKind.Run, Arguments = ["check-mutations"], Nonce = Nonce })),
            new ConsoleHarnessOutput(new StringWriter(), lines, verbose: false));

        notes.Write(NoteSink.ArmKind, new string('t', HostAgentProtocol.NotePiece(NoteSink.ArmKind) * 2 + 3));
        notes.Write(NoteSink.VerifiedKind, string.Empty);

        var written = lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
        var agent = new HostAgentLines(Nonce);

        Assert.Equal(4, written.Count);
        Assert.False(agent.Error(written[0]), "a note's line is relayed before the agent serves");
        Assert.False(agent.Error(HostAgentProtocol.StartedLine(Nonce)));

        foreach (var line in written.Skip(1))
        {
            Assert.False(agent.Error(line), "a note's line is relayed");
        }

        Assert.True(agent.Error("check-mutations: arm charge, on worker 1: passed"));
        Assert.True(agent.Error(HostAgentProtocol.NoteLine("fedcba9876543210fedcba9876543210", NoteSink.ArmKind, last: true, "e30=")));
    }

    /// <summary>
    /// The architecture names the cut a note crosses by - pieces of at most <see cref="HostAgentProtocol.NotePiece"/>
    /// bytes, each small enough that its whole line fits what the machine that asked hands lines on in
    /// (<see cref="ProcessRunner.LongestLine"/>) - in the words the protocol owns, and names no other cut.
    /// </summary>
    [Fact]
    public void TheArchitecture_SaysTheCutANoteCrossesBy_InTheProtocolsOwnWords()
    {
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "carrying the text as base64 pieces of at most `NotePiece` bytes - each small enough that its whole line "
                + "fits what that machine hands lines on in as it reads the host's standard error kept as a tail "
                + "(`ProcessRunner.LongestLine`), so no piece is cut in two on the way",
            architecture,
            StringComparison.Ordinal);

        Assert.DoesNotContain("`CarriedPiece`", architecture, StringComparison.Ordinal);
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
        Assert.Equal([Path.GetDirectoryName(tool)!], info.AppendedDirectories);
    }

    /// <summary>
    /// An answer looks in each developer environment it was asked about - one with no instance here leaving the host's
    /// own PATH all there is - and names from its home every copy it held to a minimum, there and on that PATH: the copy
    /// it judged, and what that copy said.
    /// </summary>
    [Fact]
    public async Task Info_LooksInEachDeveloperEnvironmentAsked_AndNamesEveryCopyItJudgedFromItsHome()
    {
        using var home = new TempDirectory();
        var tool = home.WriteProgram("bin", "harness-home-tool");
        var told = "~" + tool[home.Path.Length..];
        using var output = new StringWriter();

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Info,
                Programs = ["harness-home-tool"],
                ToolSearchDirectories = new(StringComparer.OrdinalIgnoreCase) { [HostDoubles.Platform(ThisPlatform).PlatformKey] = ["~/bin"] },
                Floors = [new ToolFloor { Name = "harness-home-tool", MinVersion = "1.0.0", Args = ["--version"], Regex = @"(\d+\.\d+\.\d+)" }],
                DeveloperEnvironments = new(StringComparer.OrdinalIgnoreCase) { ["elsewhere"] = new DeveloperEnvironmentConfig { Kind = "elsewhere" } },
                InDeveloperEnvironments = [new DeveloperEnvironmentLook { Name = "elsewhere", Processor = "arm64", Programs = ["harness-home-tool"] }],
            },
            HostAgentProtocol.JsonOptions);

        // Held open while it is answered, as the asker holds it: the copy is started to ask its version, and an input
        // that ended would say the asker had gone.
        using var input = new HeldOpenReader(request);

        await Service(home.Path, current: ThisPlatform).ServeAsync(
            input,
            output,
            new StringWriter(),
            NothingRuns,
            TestContext.Current.CancellationToken);

        var info = JsonSerializer.Deserialize<HostAgentInfo>(output.ToString(), HostAgentProtocol.JsonOptions)!;
        var program = Assert.Single(info.Programs);
        var look = Assert.Single(info.InDeveloperEnvironments);
        var there = Assert.Single(look.Programs);

        // The copy cannot say a version - it is no program here, or prints none - so it is judged and not known to meet it.
        Assert.Equal((ProgramFound.FloorUnknown, told, told), (program.Found, program.Path, program.Judged?.Copy));
        Assert.DoesNotContain(home.Path, program.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(home.Path, program.Judged?.Problem ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(("elsewhere", "arm64", (string?)null), (look.Name, look.Processor, look.Unestablished));
        Assert.Equal((ProgramFound.FloorUnknown, told), (there.Found, there.Judged?.Copy));
        Assert.DoesNotContain(home.Path, there.Reason, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// A run whose asker stops writing its beat is cancelled though the input stays open - the sign that needs nothing of
    /// whatever carries the input - once as many beats as a host lets pass have gone unheard, and says why where that
    /// machine would read it, were it still there. What the run started stopped in time, so nothing more is ended.
    /// </summary>
    [Fact]
    public async Task Run_CancelsTheCommand_WhenTheBeatFallsSilent_ThoughTheInputStaysOpen_AndSaysWhy()
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, beatSeconds: 1, "verify-git"));
        using var error = new StringWriter();
        var lastResort = new RecordingLastResort();
        var stepped = new SteppedPatience();
        var cancellationToken = TestContext.Current.CancellationToken;
        var cancelledEarly = false;

        // A beat a second, so eight unheard are 8 s by the host's clock, which it looks at four times in that long.
        var exitCode = await Service(lastResort: lastResort, patience: stepped.Patience).ServeAsync(
            input,
            new StringWriter(),
            error,
            async (_, _, _, token) =>
            {
                for (var look = 0; look < 4; look++)
                {
                    // Not before the eighth: at 6 s, as at none, the run goes on.
                    cancelledEarly |= token.IsCancellationRequested;
                    await stepped.StepAsync(meanwhile: null, cancellationToken);
                }

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
            cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        Assert.Equal(HarnessExit.Cancelled, exitCode);
        Assert.False(cancelledEarly, "The run was cancelled before eight beats had gone unheard.");
        Assert.Equal(TimeSpan.FromSeconds(8), stepped.Clock.Moved);
        Assert.Contains(
            "host-agent: FAIL - the machine that asked has gone - it wrote nothing on the input it holds open here for 8 s, where it writes a beat "
            + "while it is there - so what it started here is stopped",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.EndsWith(HostAgentProtocol.CompletionLine(Nonce, HarnessExit.Cancelled) + Environment.NewLine, error.ToString(), StringComparison.Ordinal);
        Assert.False(lastResort.Ended.IsCompleted);
    }

    /// <summary>
    /// A run whose asker goes on writing its beat is left running, however long it takes: eight times as long, by the
    /// host's clock, as the silence that would have cancelled it, a beat heard between every two looks at it.
    /// </summary>
    [Fact]
    public async Task Run_LeavesTheCommandRunning_WhileTheBeatGoesOn_HoweverLongItTakes()
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, beatSeconds: 1, "verify-git"));
        using var error = new StringWriter();
        var stepped = new SteppedPatience();
        var cancellationToken = TestContext.Current.CancellationToken;
        bool? cancelled = null;

        var exitCode = await Service(patience: stepped.Patience).ServeAsync(
            input,
            new StringWriter(),
            error,
            async (_, _, _, token) =>
            {
                for (var look = 0; look < 32; look++)
                {
                    await stepped.StepAsync(() => input.BeatHeardAsync(cancellationToken), cancellationToken);
                }

                cancelled = token.IsCancellationRequested;
                return HarnessExit.Success;
            },
            cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);
        Assert.Equal(TimeSpan.FromSeconds(64), stepped.Clock.Moved);
        Assert.False(cancelled);
        Assert.DoesNotContain("has gone", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run whose request says its asker writes no beat is never cancelled for its silence: the host does not watch for
    /// one, however long nothing is read on its input.
    /// </summary>
    [Fact]
    public async Task Run_IsNeverCancelledForItsSilence_WhereTheRequestSaysItsAskerWritesNoBeat()
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, beatSeconds: 0, "verify-git"));
        using var error = new StringWriter();
        var stepped = new SteppedPatience();
        var cancellationToken = TestContext.Current.CancellationToken;
        (bool Watching, bool Cancelled)? found = null;

        var exitCode = await Service(patience: stepped.Patience).ServeAsync(
            input,
            new StringWriter(),
            error,
            (_, _, _, token) =>
            {
                stepped.Clock.Advance(TimeSpan.FromDays(1));
                found = (stepped.Watching, token.IsCancellationRequested);

                return Task.FromResult(HarnessExit.Success);
            },
            cancellationToken);

        Assert.Equal(HarnessExit.Success, exitCode);
        Assert.Equal((false, false), found);
        Assert.DoesNotContain("has gone", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An input that cannot be read has ended as surely as one that was closed, whatever the failure: the run is
    /// cancelled, where a failure nobody caught left it running with nothing left to end it.
    /// </summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    public async Task Run_CancelsTheCommand_WhenTheInputCannotBeRead_WhateverTheFailure(Type failure)
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, "verify-git"));
        using var error = new StringWriter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;

        var serving = Service().ServeAsync(
            input,
            new StringWriter(),
            error,
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
        input.Fail((Exception)Activator.CreateInstance(failure, "the input broke.")!);

        Assert.Equal(HarnessExit.Cancelled, await serving.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));

        // Said, where an input that ended is not: this host's own fault as likely as the other machine's going, and a
        // machine still reading is owed why its run stopped.
        Assert.Contains(
            "host-agent: FAIL - the machine that asked has gone - the input it holds open here could not be read: the input broke - so what it "
            + "started here is stopped",
            error.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The silence a host lets pass is counted in the beat its request says, eight of them: a request whose asker beats
    /// every five seconds is left running through thirty-nine seconds of silence, and cancelled at the fortieth.
    /// </summary>
    [Fact]
    public async Task Run_CountsTheSilenceItLetsPass_InTheBeatItsRequestSays()
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, beatSeconds: 5, "verify-git"));
        using var error = new StringWriter();
        var stepped = new SteppedPatience();
        var cancellationToken = TestContext.Current.CancellationToken;
        var cancelledAt = new List<(TimeSpan Silent, bool Cancelled)>();

        var exitCode = await Service(patience: stepped.Patience).ServeAsync(
            input,
            new StringWriter(),
            error,
            async (_, _, _, token) =>
            {
                for (var look = 0; look < 4; look++)
                {
                    cancelledAt.Add((stepped.Clock.Moved, token.IsCancellationRequested));
                    await stepped.StepAsync(meanwhile: null, cancellationToken);
                }

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
            cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        Assert.Equal(HarnessExit.Cancelled, exitCode);
        Assert.Equal(
            [(TimeSpan.Zero, false), (TimeSpan.FromSeconds(10), false), (TimeSpan.FromSeconds(20), false), (TimeSpan.FromSeconds(30), false)],
            cancelledAt);
        Assert.Equal(TimeSpan.FromSeconds(40), stepped.Clock.Moved);
        Assert.Contains("it wrote nothing on the input it holds open here for 40 s", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A request saying a beat no machine writes - less than none, or one a host would wait most of a day on the silence
    /// of - is refused before anything runs: watched for, it is a wait no clock can hold, and the run would go unwatched
    /// without a word.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    [InlineData(int.MaxValue)]
    public async Task ARequestSayingABeatNoMachineWrites_IsRefused_BeforeAnythingRuns(int beatSeconds)
    {
        using var copy = new TempDirectory();
        using var error = new StringWriter();
        var ran = false;

        var exitCode = await Service().ServeAsync(
            new StringReader(RunRequest(copy.Path, beatSeconds, "verify-git")),
            new StringWriter(),
            error,
            (_, _, _, _) =>
            {
                ran = true;
                return Task.FromResult(HarnessExit.Success);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, exitCode);
        Assert.False(ran);
        Assert.Contains(
            $"host-agent: FAIL - the request says its asker writes a beat every {beatSeconds} s, and a host counts one of 0, for none, to 3600 s",
            error.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The agent ends itself whatever ending what it started came to: a last resort that fails is the reason it gives no
    /// other, never a reason to stay.
    /// </summary>
    [Fact]
    public async Task Run_EndsItself_WhateverEndingWhatItStartedCameTo()
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, "verify-git"));
        using var error = new StringWriter();
        var lastResort = new RecordingLastResort { Fails = new InvalidOperationException("the table broke") };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;

        var serving = Service(lastResort: lastResort, patience: new HostAgentPatience { Unwind = TimeSpan.FromMilliseconds(100) }).ServeAsync(
            input,
            new StringWriter(),
            error,
            (_, _, _, _) =>
            {
                started.SetResult();
                return release.Task;
            },
            cancellationToken);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            input.End();

            Assert.Equal(HarnessExit.Cancelled, await lastResort.Ended.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
            Assert.True(lastResort.Asked);
        }
        finally
        {
            release.TrySetResult(HarnessExit.Success);
        }

        // The failure is the agent's own to raise, once it has asked to end: nothing reads it where the process is gone.
        await Assert.ThrowsAsync<InvalidOperationException>(() => serving.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
    }

    /// <summary>
    /// What a run started that has not stopped within the host's patience of being cancelled is ended by the agent's last
    /// resort, and the agent with it, saying so and how the request finished: a command stuck reading what a process its
    /// child left behind still holds would otherwise keep the host for good, with nobody reading it.
    /// </summary>
    [Fact]
    public async Task Run_EndsWhatItStarted_AndItself_WhereTheCommandHasNotStoppedInTimeOnceCancelled()
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, "verify-git"));
        using var error = new StringWriter();
        var lastResort = new RecordingLastResort();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;

        var serving = Service(lastResort: lastResort, patience: new HostAgentPatience { Unwind = TimeSpan.FromMilliseconds(100) }).ServeAsync(
            input,
            new StringWriter(),
            error,
            (_, _, _, _) =>
            {
                // Never stops for its token, as a command whose child's output never ends.
                started.SetResult();
                return release.Task;
            },
            cancellationToken);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            Assert.False(lastResort.Ended.IsCompleted);

            input.End();

            Assert.Equal(HarnessExit.Cancelled, await lastResort.Ended.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
            Assert.Equal(HarnessExit.Cancelled, await serving.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));

            // What was ended, as the last resort found it: said once it was tried, never before.
            Assert.Contains(
                "host-agent: FAIL - what the request started here was cancelled - the input it holds open here ended - and had not stopped 1 s later: "
                + "2 processes it started were ended, each with what it started (pid 41, pid 42), and this process ends",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.EndsWith(HostAgentProtocol.CompletionLine(Nonce, HarnessExit.Cancelled) + Environment.NewLine, error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            release.TrySetResult(HarnessExit.Success);
        }
    }

    /// <summary>Every command past its point of no return that a host is ever asked to run.</summary>
    public static TheoryData<string> CommandsPastThePointOfNoReturn
        => [.. PointOfNoReturn.Commands.Where(command => !HostAgentProtocol.IsNotForwardable(command)).Order(StringComparer.Ordinal)];

    /// <summary>
    /// A command past its point of no return goes on once cancelled, to finish what it began or to say what is left: a
    /// host gives it as long as the command line gives one interrupted where it was typed, and ends nothing meanwhile.
    /// Cut off at the time any other command is given, a deletion was ended part way with nothing said of what remained.
    /// </summary>
    [Theory]
    [MemberData(nameof(CommandsPastThePointOfNoReturn))]
    public async Task ACommandPastItsPointOfNoReturn_IsGivenItsOwnTimeToFinish_OnceCancelled(string command)
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, command, "anything"));
        using var error = new StringWriter();
        var lastResort = new RecordingLastResort();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;

        var serving = Service(lastResort: lastResort, patience: new HostAgentPatience { Unwind = TimeSpan.FromMilliseconds(100), Finishing = TimeSpan.FromMinutes(5) })
            .ServeAsync(
                input,
                new StringWriter(),
                error,
                async (_, _, _, token) =>
                {
                    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    await using var registration = token.Register(() => cancelled.TrySetResult());

                    started.SetResult();
                    await cancelled.Task;

                    // Goes on, well past the time any other command is given to stop in.
                    await Task.Delay(TimeSpan.FromMilliseconds(700), CancellationToken.None);
                    return 9;
                },
                cancellationToken);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        input.End();

        Assert.Equal(9, await serving.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
        Assert.False(lastResort.Asked, "What the command started was ended while it finished what it began.");
        Assert.EndsWith(HostAgentProtocol.CompletionLine(Nonce, 9) + Environment.NewLine, error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run asked to set up goes on once cancelled too, for its legs to bring down what they brought up: a host gives it
    /// the time a command past its point of no return is given, and ends nothing meanwhile.
    /// </summary>
    [Theory]
    [InlineData("test", "--legs", "a", "--set-up")]
    [InlineData("run", "corpus", "--set-up")]
    public async Task ARunAskedToSetUp_IsGivenTheTimeToBringItsSetUpsDown_OnceCancelled(params string[] commandLine)
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, commandLine));
        using var error = new StringWriter();
        var lastResort = new RecordingLastResort();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;

        var serving = Service(lastResort: lastResort, patience: new HostAgentPatience { Unwind = TimeSpan.FromMilliseconds(100), Finishing = TimeSpan.FromMinutes(5) })
            .ServeAsync(
                input,
                new StringWriter(),
                error,
                async (_, _, _, token) =>
                {
                    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                    await using var registration = token.Register(() => cancelled.TrySetResult());

                    started.SetResult();
                    await cancelled.Task;

                    // Brings its set-ups down, well past the time a run not asked to set up is given to stop in.
                    await Task.Delay(TimeSpan.FromMilliseconds(700), CancellationToken.None);
                    return HarnessExit.Cancelled;
                },
                cancellationToken);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        input.End();

        Assert.Equal(HarnessExit.Cancelled, await serving.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
        Assert.False(lastResort.Asked, "What the run started was ended while its legs brought their set-ups down.");
    }

    /// <summary>
    /// The time a command past its point of no return is given is a bound all the same, and its own: one that never
    /// finishes is ended once it is up, whatever any other command would have been given, and the time said is that one.
    /// </summary>
    [Fact]
    public async Task ACommandPastItsPointOfNoReturn_ThatNeverFinishes_IsEndedOnceItsOwnTimeIsUp()
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, PointOfNoReturn.Commands.First(command => !HostAgentProtocol.IsNotForwardable(command)), "anything"));
        using var error = new StringWriter();
        var lastResort = new RecordingLastResort();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;

        var serving = Service(lastResort: lastResort, patience: new HostAgentPatience { Unwind = TimeSpan.FromMinutes(5), Finishing = TimeSpan.FromMilliseconds(1500) })
            .ServeAsync(
                input,
                new StringWriter(),
                error,
                (_, _, _, _) =>
                {
                    started.SetResult();
                    return release.Task;
                },
                cancellationToken);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            input.End();

            Assert.Equal(HarnessExit.Cancelled, await lastResort.Ended.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
            Assert.Equal(HarnessExit.Cancelled, await serving.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
            Assert.Contains("and had not stopped 2 s later: ", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            release.TrySetResult(HarnessExit.Success);
        }
    }

    /// <summary>
    /// What the agent says to a machine that has stopped reading holds nothing up. The console's own writer writes where
    /// it is called, and one whose reader has gone never returns: the run is cancelled all the same, and where its last
    /// line is never read either, so that the request never answers, the agent ends what it started and itself. Waited
    /// on where it was written, the first word of why held the cancellation it came before, for as long as the
    /// connection stood - which is the machine the beat is there to notice.
    /// </summary>
    [Fact]
    public async Task Run_IsCancelled_AndTheAgentEnds_ThoughNothingItSaysIsReadAnyMore()
    {
        using var copy = new TempDirectory();
        using var input = new BeatingReader(RunRequest(copy.Path, "verify-git"));
        using var error = new UnreadWriter();
        var lastResort = new RecordingLastResort();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;

        var serving = Service(
                lastResort: lastResort,
                patience: new HostAgentPatience { Saying = TimeSpan.FromMilliseconds(50), Unwind = TimeSpan.FromMilliseconds(100) })
            .ServeAsync(
                input,
                new StringWriter(),
                error,
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
                        cancelled.SetResult();
                        return HarnessExit.Cancelled;
                    }
                },
                cancellationToken);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

            // The sign that is said, to a machine that reads nothing any more.
            error.Stall();
            input.Fail(new IOException("the input broke."));

            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            Assert.Equal(HarnessExit.Cancelled, await lastResort.Ended.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
            Assert.True(lastResort.Asked);
        }
        finally
        {
            error.Release();
        }

        Assert.Equal(HarnessExit.Cancelled, await serving.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
    }

    /// <summary>
    /// A writer whose reader may stop reading: from then on whatever writes to it does not return, as the console's own
    /// writer does not where the pipe it writes to is full, until it is read again.
    /// </summary>
    private sealed class UnreadWriter : StringWriter
    {
        private readonly ManualResetEventSlim _read = new(initialState: true);

        public void Stall() => _read.Reset();

        public void Release() => _read.Set();

        public override Task WriteLineAsync(string? value)
        {
            _read.Wait();
            return base.WriteLineAsync(value);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            _read.Wait();
            return base.FlushAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Every request says the beat its asker writes, and the beat written for it is the one it says: a request that says
    /// none is written none.
    /// </summary>
    [Fact]
    public void ARequest_SaysTheBeatItsAskerWrites_AndIsWrittenThatBeat()
    {
        var run = new HostAgentRequest { Kind = HostAgentRequestKind.Run };
        var read = JsonSerializer.Deserialize<HostAgentRequest>(JsonSerializer.Serialize(run, HostAgentProtocol.JsonOptions), HostAgentProtocol.JsonOptions)!;

        Assert.Equal(11, HostAgentProtocol.Version);
        Assert.Equal(15, read.BeatSeconds);
        Assert.Equal(new InputBeat(TimeSpan.FromSeconds(15), "host-agent: beat"), HostAgentProtocol.BeatOf(read));
        Assert.Null(HostAgentProtocol.BeatOf(new HostAgentRequest { Kind = HostAgentRequestKind.Run, BeatSeconds = 0 }));
    }

    /// <summary>
    /// A survey carries every declared tool's minimum version to the host, which holds the copies it finds to them, and
    /// its answer carries back the directories it raised, apart from the ones it appends: a host told only one list
    /// would put a raised directory behind the older copy it was raised past.
    /// </summary>
    [Fact]
    public void ASurvey_CarriesTheToolsMinimums_AndItsAnswerTheDirectoriesRaised()
    {
        var floor = new DssHarness.Core.Tools.ToolFloor { Name = "python3", MinVersion = "3.12.0", Args = ["--version"], Regex = @"(\d+\.\d+\.\d+)", Platforms = ["macos"] };
        var asked = new HostAgentRequest { Kind = HostAgentRequestKind.Info, Floors = [floor] };
        var answer = new HostAgentInfo { Version = "1.0.0", AssemblySha256 = "00", Os = "macos", Processor = "arm64", RaisedDirectories = ["/opt/homebrew/bin"], AppendedDirectories = ["/usr/local/bin"] };

        var read = JsonSerializer.Deserialize<HostAgentRequest>(JsonSerializer.Serialize(asked, HostAgentProtocol.JsonOptions), HostAgentProtocol.JsonOptions)!;
        var told = JsonSerializer.Deserialize<HostAgentInfo>(JsonSerializer.Serialize(answer, HostAgentProtocol.JsonOptions), HostAgentProtocol.JsonOptions)!;

        var carried = Assert.Single(read.Floors);
        Assert.Equal((floor.Name, floor.MinVersion, floor.Regex), (carried.Name, carried.MinVersion, carried.Regex));
        Assert.Equal(floor.Args, carried.Args);
        Assert.Equal(floor.Platforms, carried.Platforms);
        Assert.Equal(["/opt/homebrew/bin"], told.RaisedDirectories);
        Assert.Equal(["/usr/local/bin"], told.AppendedDirectories);
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
        => RunRequest(directory, HostAgentProtocol.BeatSeconds, arguments);

    /// <summary>A run request whose asker says it writes a beat every <paramref name="beatSeconds"/> seconds; none where zero.</summary>
    private static string RunRequest(string directory, int beatSeconds, params string[] arguments)
        => JsonSerializer.Serialize(
            new HostAgentRequest { Kind = HostAgentRequestKind.Run, Directory = directory, Arguments = [.. arguments], Nonce = Nonce, BeatSeconds = beatSeconds },
            HostAgentProtocol.JsonOptions);

    private static HostAgentService Service(
        string? home = null,
        IProcessRunner? keepingAwake = null,
        PlatformId current = PlatformId.Linux,
        IFileSystem? files = null,
        IHostAgentLastResort? lastResort = null,
        HostAgentPatience? patience = null)
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
            new DeveloperEnvironmentProvider(platform, processRunner, fileSystem, new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false)),
            fileSystem,
            HostDoubles.Floors(platform, processRunner),
            new KeepAwake(keepingAwake ?? processRunner, new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false)),
                new HoldAwakeStore(new PhysicalFileSystem(FilePermissionsFactory.Create()), Path.Combine(TestHost.TemporaryRoot, "holds", Guid.NewGuid().ToString("N") + ".json")),
                new RecordingLauncher(),
                HomeShorthand.Of(platform, fileSystem),
                lastResort ?? new RecordingLastResort(),
                patience);
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
