using System.Text;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// Setting up a developer environment on the machine that runs the leg: vcvarsall.bat is run once
/// for the leg's processor, over the host's own environment, and what it changed - and nothing else -
/// is what every process of the leg starts with. A vcvarsall.bat that fails, says it failed, or sets
/// up another processor is refused, naming why, rather than handed to a build as a working compiler.
/// </summary>
public sealed class DeveloperEnvironmentProviderTests
{
    private static readonly DeveloperEnvironmentConfig VisualStudio = new() { Kind = DeveloperEnvironmentKinds.VisualStudio };

    private static readonly Dictionary<string, string> NoHostEnvironment = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What vcvarsall.bat added or changed is set up - its tools ahead on PATH, INCLUDE, LIB, the
    /// tools version, the processor - and nothing it left alone: not the host's own variables, not
    /// cmd.exe's, and not what the harness handed the batch file.
    /// </summary>
    [Fact]
    public async Task WhatVcvarsallChanged_IsSetUp_AndNothingElse()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var host = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CCACHE_DIR"] = @"D:\cache" };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", host, TestContext.Current.CancellationToken);

        Assert.Null(setUp.Failure);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = visualStudio.BinFor("x64") + Path.PathSeparator + ScriptedVisualStudio.ShellPath,
                ["INCLUDE"] = visualStudio.Include,
                ["LIB"] = Path.Combine(visualStudio.InstallationPath, "VC", "Tools", "MSVC", ScriptedVisualStudio.ToolsVersion, "lib", "x64"),
                ["VCToolsVersion"] = ScriptedVisualStudio.ToolsVersion,
                ["VSCMD_ARG_TGT_ARCH"] = "x64",
                ["VSCMD_VER"] = "18.0.0",
            },
            setUp.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(
            new DeveloperEnvironmentFact("vs", visualStudio.InstallationPath, ScriptedVisualStudio.InstallationVersion, ScriptedVisualStudio.ToolsVersion, "amd64"),
            setUp.Fact);
        Assert.Equal(
            $"developer environment: vs (Visual Studio {ScriptedVisualStudio.InstallationVersion}, MSVC {ScriptedVisualStudio.ToolsVersion}, amd64)",
            setUp.Fact!.Describe());
    }

    /// <summary>
    /// The batch file is started by a relative path from a directory of its own, under the host's
    /// environment, with vcvarsall.bat and its arguments - the architecture alone, where the survey
    /// found no toolset to ask for - handed over in variables rather than in its text; the directory
    /// is gone afterwards.
    /// </summary>
    [Fact]
    public async Task TheCapture_RunsUnderTheHostsEnvironment_AndLeavesNothingBehind()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var host = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CCACHE_DIR"] = @"D:\cache" };

        await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", host, TestContext.Current.CancellationToken);

        var capture = Assert.Single(visualStudio.Captures);
        Assert.Equal(["/d", "/u", "/c", @".\capture.bat"], capture.Arguments);
        Assert.Equal(DeveloperEnvironmentProvider.CaptureBudget, capture.Timeout);
        Assert.Equal(@"D:\cache", capture.Environment["CCACHE_DIR"]);
        Assert.Equal(
            Path.Combine(visualStudio.InstallationPath, "VC", "Auxiliary", "Build", "vcvarsall.bat"),
            capture.Environment[DeveloperEnvironmentProvider.ScriptVariable]);
        Assert.Equal("amd64", capture.Environment[DeveloperEnvironmentProvider.ArgumentsVariable]);
        Assert.False(Directory.Exists(capture.WorkingDirectory), "the capture's directory is removed once read");
    }

    /// <summary>
    /// The toolset the survey found is asked of vcvarsall.bat as <c>-vcvars_ver=</c> after the architecture,
    /// and is what the leg's line names: the MSVC its legs build with, not the instance's default.
    /// </summary>
    [Fact]
    public async Task TheToolsetTheSurveyFound_IsAskedOfVcvarsall_AndNamedOnTheLegsLine()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var found = DeveloperEnvironmentCheck.Installed(visualStudio.InstallationPath, ScriptedVisualStudio.InstallationVersion, "14.44.35207");

        var setUp = await visualStudio.Provider().SetUpAsync("cuda", found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("amd64 -vcvars_ver=14.44.35207", Assert.Single(visualStudio.Captures).Environment[DeveloperEnvironmentProvider.ArgumentsVariable]);
        Assert.Null(setUp.Failure);
        Assert.Equal("14.44.35207", setUp.Environment["VCToolsVersion"]);
        Assert.Equal(
            new DeveloperEnvironmentFact("cuda", visualStudio.InstallationPath, ScriptedVisualStudio.InstallationVersion, "14.44.35207", "amd64"),
            setUp.Fact);
        Assert.Equal(
            $"developer environment: cuda (Visual Studio {ScriptedVisualStudio.InstallationVersion}, MSVC 14.44.35207, amd64)",
            setUp.Fact!.Describe());
    }

    /// <summary>
    /// A vcvarsall.bat asked for one toolset that comes out with another - or with none - without a word is refused,
    /// naming both: the build it would start is the one the environment names its toolset to keep out. Held to the
    /// toolset exactly, never to one whose name it begins.
    /// </summary>
    [Theory]
    [InlineData("14.51.36231")]
    [InlineData("14.44.3520")]
    [InlineData("14.44.352071")]
    [InlineData("")]
    public async Task AVcvarsallThatSetsUpAnotherToolset_IsRefused_NamingBoth(string reported)
    {
        using var visualStudio = new ScriptedVisualStudio { ReportedToolsVersion = reported };
        var found = DeveloperEnvironmentCheck.Installed(visualStudio.InstallationPath, ScriptedVisualStudio.InstallationVersion, "14.44.35207");

        var setUp = await visualStudio.Provider().SetUpAsync("cuda", found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal(
            $"developer environment 'cuda': vcvarsall.bat amd64 -vcvars_ver=14.44.35207 set up VCToolsVersion '{reported}', where '14.44.35207' was asked for",
            setUp.Failure);
        Assert.Empty(setUp.Environment);
    }

    /// <summary>
    /// What vcvarsall.bat is asked for besides the architecture - a toolset, or none for its default - is part of what a
    /// capture is shared by: an environment set up with one toolset is never handed to a leg asking for another, or for
    /// the default, however much else they share.
    /// </summary>
    [Fact]
    public async Task TheToolsetAskedFor_IsPartOfWhatACaptureIsSharedBy()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var provider = visualStudio.Provider();
        var token = TestContext.Current.CancellationToken;
        var older = DeveloperEnvironmentCheck.Installed(visualStudio.InstallationPath, ScriptedVisualStudio.InstallationVersion, "14.44.35207");

        var pinned = await provider.SetUpAsync("vs", older, "x86_64", NoHostEnvironment, token);
        var newest = await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, token);
        var again = await provider.SetUpAsync("vs", older, "x86_64", NoHostEnvironment, token);

        Assert.Equal(
            ["amd64 -vcvars_ver=14.44.35207", "amd64"],
            visualStudio.Captures.Select(capture => capture.Environment[DeveloperEnvironmentProvider.ArgumentsVariable]));
        Assert.Equal(("14.44.35207", ScriptedVisualStudio.ToolsVersion), (pinned.Fact!.ToolsVersion, newest.Fact!.ToolsVersion));
        Assert.Same(pinned, again);
    }

    /// <summary>
    /// A toolset that is no toolset's name never reaches vcvarsall.bat's command line, where call's own pass would read
    /// it: the call is refused as it is made, and nothing is run. No survey answers one.
    /// </summary>
    [Theory]
    [InlineData("14.44")]
    [InlineData("14.44.35207 & calc")]
    [InlineData("14.44.35207\"")]
    [InlineData("")]
    public void AToolsetThatIsNoToolsetsName_IsRefusedAsTheCallIsMade(string toolset)
    {
        using var visualStudio = new ScriptedVisualStudio();
        var found = DeveloperEnvironmentCheck.Installed(visualStudio.InstallationPath, ScriptedVisualStudio.InstallationVersion, toolset);

        Assert.Throws<ArgumentException>(
            "found",
            () => { _ = visualStudio.Provider().SetUpAsync("vs", found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken); });
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>
    /// A PATH the host declares is the one vcvarsall.bat puts its tools ahead of, so a program only the
    /// host's PATH holds is still found.
    /// </summary>
    [Fact]
    public async Task APathTheHostDeclares_IsKept_BehindVisualStudiosTools()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var host = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Path"] = @"D:\tools" };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", host, TestContext.Current.CancellationToken);

        Assert.Equal(visualStudio.BinFor("x64") + Path.PathSeparator + @"D:\tools", setUp.Environment["PATH"]);
    }

    /// <summary>
    /// A leg built for another processor than the machine's is set up to cross-compile, and held to
    /// the processor vcvarsall.bat says it set up.
    /// </summary>
    [Fact]
    public async Task ALegForAnotherProcessor_IsSetUpToCrossCompile()
    {
        using var visualStudio = new ScriptedVisualStudio();

        var setUp = await visualStudio.Provider("x86_64").SetUpAsync("vs", visualStudio.Found, "arm64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Null(setUp.Failure);
        Assert.Equal("amd64_arm64", setUp.Fact!.Architecture);
        Assert.Equal("arm64", setUp.Environment["VSCMD_ARG_TGT_ARCH"]);
        Assert.StartsWith(visualStudio.BinFor("arm64") + Path.PathSeparator, setUp.Environment["PATH"], StringComparison.Ordinal);
    }

    /// <summary>
    /// vcvarsall.bat runs once for every leg sharing an instance, a processor and a host environment -
    /// the same whatever order or spelling the host's variables come in - and again for each that
    /// differs.
    /// </summary>
    [Fact]
    public async Task LegsSharingAnInstanceAProcessorAndAnEnvironment_ShareOneRun()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var provider = visualStudio.Provider();
        var token = TestContext.Current.CancellationToken;

        var first = await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, token);
        var again = await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" }, token);

        Assert.Same(first, again);
        Assert.Single(visualStudio.Captures);

        await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", new Dictionary<string, string> { ["A"] = "1", ["B"] = "3" }, token);
        await provider.SetUpAsync("vs", visualStudio.Found, "arm64", new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, token);

        Assert.Equal(3, visualStudio.Captures.Count);
    }

    /// <summary>
    /// Two environments set up from one instance are each their own: what a leg's line names, and what
    /// a refusal names, is the environment its toolchain names.
    /// </summary>
    [Fact]
    public async Task TwoEnvironmentsFromOneInstance_AreEachSetUpUnderTheirOwnName()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var provider = visualStudio.Provider();
        var token = TestContext.Current.CancellationToken;

        var first = await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, token);
        var second = await provider.SetUpAsync("buildTools", visualStudio.Found, "x86_64", NoHostEnvironment, token);

        Assert.Equal("vs", first.Fact!.Name);
        Assert.Equal("buildTools", second.Fact!.Name);
    }

    /// <summary>
    /// A capture directory that cannot be removed afterwards - a scanner still holding a file in it -
    /// is said, and fails nothing: what vcvarsall.bat set was read before.
    /// </summary>
    [Fact]
    public async Task ACaptureDirectoryThatCannotBeRemoved_IsSaid_AndFailsNothing()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var harness = new HarnessFactory();
        var held = new HeldFiles(new PhysicalFileSystem(FilePermissionsFactory.Create()));

        var setUp = await visualStudio.Provider(fileSystem: held, output: harness.Output)
            .SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        var scratch = Assert.Single(visualStudio.Captures).WorkingDirectory!;

        try
        {
            Assert.Null(setUp.Failure);
            Assert.Contains(
                $"developer-environment: WARN - developer environment 'vs': '{scratch}' could not be removed: {HeldFiles.Said}",
                harness.StandardError.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// A capture the caller that started it gave up on is not what the next caller gets: that one
    /// runs its own, and is set up. The caller that gave up is told it was stopped.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ACaptureAnotherCallerStopped_IsRunAgain_ForACallerThatStillWantsIt()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var visualStudio = new ScriptedVisualStudio
        {
            Holding = async (index, stopping) =>
            {
                if (index == 0)
                {
                    firstStarted.SetResult();
                    await Task.Delay(Timeout.Infinite, stopping);
                }
            },
        };

        var provider = visualStudio.Provider();
        using var givingUp = new CancellationTokenSource();

        var abandoned = provider.SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, givingUp.Token);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var wanted = provider.SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);
        await givingUp.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        var setUp = await wanted;

        Assert.Null(setUp.Failure);
        Assert.Equal(2, visualStudio.Captures.Count);
    }

    /// <summary>
    /// What is set up is the instance the survey found, and nothing else: vswhere is not asked again,
    /// and asked to set up where the survey found none, the provider refuses the call outright - a leg
    /// is only ever placed where one was found.
    /// </summary>
    [Fact]
    public async Task OnlyTheInstanceTheSurveyFound_IsSetUp()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var provider = visualStudio.Provider();

        await provider.SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Empty(visualStudio.Probes);
        Assert.Single(visualStudio.Captures);

        foreach (var none in new[]
        {
            DeveloperEnvironmentCheck.Nowhere("no instance"),
            DeveloperEnvironmentCheck.Unreadable("vswhere did not answer"),
            new DeveloperEnvironmentCheck(DeveloperEnvironmentFound.Installed, null, null, null),
            new DeveloperEnvironmentCheck(DeveloperEnvironmentFound.Installed, null, string.Empty, null),
            new DeveloperEnvironmentCheck(DeveloperEnvironmentFound.Nowhere, "gone", visualStudio.InstallationPath, null),
        })
        {
            // Refused as the call is made, before any of it starts.
            Assert.Throws<ArgumentException>(
                "found",
                () => { _ = provider.SetUpAsync("vs", none, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken); });
        }

        Assert.Single(visualStudio.Captures);
    }

    /// <summary>A processor vcvarsall.bat has no word for is refused, naming both processors, and nothing is run.</summary>
    [Fact]
    public async Task AProcessorVcvarsallHasNoWordFor_IsRefused()
    {
        using var visualStudio = new ScriptedVisualStudio();

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "riscv64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs' has no vcvarsall.bat architecture for a riscv64 leg on a x86_64 host", setUp.Failure);
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>An instance without vcvarsall.bat is refused, naming the file looked for.</summary>
    [Fact]
    public async Task AnInstanceWithoutVcvarsall_IsRefused_NamingTheFile()
    {
        using var empty = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio();

        var setUp = await visualStudio.Provider().SetUpAsync(
            "vs",
            DeveloperEnvironmentCheck.Installed(empty.Path, null),
            "x86_64",
            NoHostEnvironment,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            $"developer environment 'vs' found Visual Studio at '{empty.Path}', which has no "
            + $"'{Path.Combine(empty.Path, "VC", "Auxiliary", "Build", "vcvarsall.bat")}'",
            setUp.Failure);
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>A vcvarsall.bat that exits non-zero is refused with its exit code and what it said.</summary>
    [Fact]
    public async Task AVcvarsallThatExitsNonZero_IsRefused_WithWhatItSaid()
    {
        using var visualStudio = new ScriptedVisualStudio
        {
            ExitCode = "1",
            Log = Encoding.Unicode.GetBytes("[ERROR:vcvarsall.bat] Invalid argument found : amd64x\r\n"),
        };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs': vcvarsall.bat amd64 exited 1: [ERROR:vcvarsall.bat] Invalid argument found : amd64x", setUp.Failure);
    }

    /// <summary>One that exits non-zero having printed nothing says that, rather than trailing off after its exit code.</summary>
    [Fact]
    public async Task AVcvarsallThatExitsNonZero_HavingPrintedNothing_SaysSo()
    {
        using var visualStudio = new ScriptedVisualStudio { ExitCode = "1" };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs': vcvarsall.bat amd64 exited 1 and printed nothing", setUp.Failure);
    }

    /// <summary>
    /// vcvarsall.bat can report a failure and exit 0. Its own lines are cmd.exe's UTF-16, what the
    /// programs it starts print is the console's code page, and an error in either is refused.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnErrorVcvarsallReports_IsRefused_ThoughItExitedZero(bool unicode)
    {
        const string Said = "[ERROR:vcvars.bat] Toolset directory for version '14.99' was not found.";

        using var visualStudio = new ScriptedVisualStudio
        {
            Log = (unicode ? Encoding.Unicode : Encoding.ASCII).GetBytes("**********\r\n" + Said + "\r\n"),
        };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal($"developer environment 'vs': vcvarsall.bat amd64 reported an error: ********** / {Said}", setUp.Failure);
    }

    /// <summary>An environment set up for another processor builds code for that one, and is refused.</summary>
    [Fact]
    public async Task AnEnvironmentForAnotherProcessor_IsRefused()
    {
        using var visualStudio = new ScriptedVisualStudio { ReportedTarget = "x86" };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs': vcvarsall.bat amd64 set up VSCMD_ARG_TGT_ARCH 'x86', where 'x64' was asked for", setUp.Failure);
    }

    /// <summary>A batch file cmd.exe never ran is refused with what cmd.exe said.</summary>
    [Fact]
    public async Task ABatchFileThatNeverRan_IsRefused_WithWhatCmdSaid()
    {
        using var visualStudio = new ScriptedVisualStudio { WritesNothing = true };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal(
            "developer environment 'vs': vcvarsall.bat amd64 could not be run: cmd.exe (exit 1): The system cannot find the path specified.",
            setUp.Failure);
    }

    /// <summary>
    /// A batch file that stopped before listing what vcvarsall.bat left is refused as one that could not
    /// be run, never read as an environment with nothing in it.
    /// </summary>
    [Fact]
    public async Task ABatchFileThatStoppedBeforeItsLastListing_IsRefused()
    {
        using var visualStudio = new ScriptedVisualStudio { WritesNoListingAfter = true };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.StartsWith("developer environment 'vs': vcvarsall.bat amd64 could not be run: ", setUp.Failure, StringComparison.Ordinal);
    }

    /// <summary>A vcvarsall.bat that outlives its budget is refused, naming the budget.</summary>
    [Fact]
    public async Task AVcvarsallThatNeverFinishes_IsRefused_NamingTheBudget()
    {
        using var visualStudio = new ScriptedVisualStudio { TimesOut = true };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.Equal("developer environment 'vs': vcvarsall.bat amd64 did not finish within 120 seconds", setUp.Failure);
    }

    /// <summary>A shell that will not start is refused, naming the shell.</summary>
    [Fact]
    public async Task AShellThatWillNotStart_IsRefused_NamingTheShell()
    {
        using var visualStudio = new ScriptedVisualStudio { ShellMissing = true };

        var setUp = await visualStudio.Provider().SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        var shell = Assert.Single(visualStudio.Captures).FileName;
        Assert.Equal(
            $"developer environment 'vs': vcvarsall.bat amd64 could not be run, because '{shell}' could not be started: "
            + new ExecutableNotFoundException(shell).Message,
            setUp.Failure);
    }

    /// <summary>
    /// A capture that cannot be written - a temporary directory this user cannot write, a disk that is
    /// full - fails the setup naming the directory, rather than escaping as a defect in this tool.
    /// </summary>
    [Fact]
    public async Task ACaptureThatCannotBeWritten_FailsTheSetup_NamingTheDirectory()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var unwritable = new UnwritableScratch(new PhysicalFileSystem(FilePermissionsFactory.Create()));

        var setUp = await visualStudio.Provider(fileSystem: unwritable)
            .SetUpAsync("vs", visualStudio.Found, "x86_64", NoHostEnvironment, TestContext.Current.CancellationToken);

        Assert.True(setUp.HasFailed);
        Assert.StartsWith("developer environment 'vs': the capture of vcvarsall.bat amd64 in '", setUp.Failure, StringComparison.Ordinal);
        Assert.EndsWith($"' could not be written or read: {UnwritableScratch.Said}", setUp.Failure, StringComparison.Ordinal);
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>
    /// The batch file reaches vcvarsall.bat however its instance's path is spelled - with a percent
    /// pair, a caret, an exclamation mark, an ampersand, spaces and parentheses - since call expands
    /// the path once and reads it as nothing but a path. Run by this machine's own cmd.exe; skipped
    /// where there is none.
    /// </summary>
    [Fact]
    public async Task TheCapture_ReachesAnInstanceWhateverItsPathHolds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe runs the capture on Windows alone.");

        using var temp = new TempDirectory();
        var platform = HostDoubles.Platform(PlatformId.Windows, "x86_64");
        var processes = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);
        var provider = new DeveloperEnvironmentProvider(platform, processes, new PhysicalFileSystem(FilePermissionsFactory.Create()), new HarnessFactory().Output);

        foreach (var name in new[] { "p%OS%q", "a^b", "x!y", "r&d (x86)" })
        {
            temp.WriteFile(
                Path.Combine(name, "VC", "Auxiliary", "Build", "vcvarsall.bat"),
                "@echo off\r\nset \"VSCMD_ARG_TGT_ARCH=x64\"\r\nset \"VCToolsVersion=14.99.00000\"\r\nexit /b 0\r\n");

            var setUp = await provider.SetUpAsync(
                "vs",
                DeveloperEnvironmentCheck.Installed(temp.Combine(name), "18.0.0"),
                "x86_64",
                NoHostEnvironment,
                TestContext.Current.CancellationToken);

            Assert.False(setUp.HasFailed, $"{name}: {setUp.Failure}");
            Assert.Equal("14.99.00000", setUp.Environment["VCToolsVersion"]);
        }
    }

    /// <summary>
    /// The toolset reaches vcvarsall.bat as the arguments it reads one - <c>-vcvars_ver</c>, then the toolset, after the
    /// architecture, since cmd.exe splits call's arguments at '=' as well as at spaces - and an environment naming no
    /// toolset passes the architecture alone. Run by this machine's own cmd.exe; skipped where there is none.
    /// </summary>
    [Fact]
    public async Task TheToolset_ReachesVcvarsallAsTheArgumentsItReads()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe runs the capture on Windows alone.");

        using var temp = new TempDirectory();
        var platform = HostDoubles.Platform(PlatformId.Windows, "x86_64");
        var processes = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);
        var provider = new DeveloperEnvironmentProvider(platform, processes, new PhysicalFileSystem(FilePermissionsFactory.Create()), new HarnessFactory().Output);

        // Sets what it was given, argument by argument, as vcvarsall.bat's own parser reads them.
        temp.WriteFile(
            Path.Combine("VC", "Auxiliary", "Build", "vcvarsall.bat"),
            "@echo off\r\nset \"VSCMD_ARG_TGT_ARCH=x64\"\r\nset \"GIVEN=[%1][%2][%3][%4]\"\r\n"
            + "if /I \"%2\"==\"-vcvars_ver\" set \"VCToolsVersion=%3\"\r\nexit /b 0\r\n");

        var pinned = await provider.SetUpAsync(
            "cuda",
            DeveloperEnvironmentCheck.Installed(temp.Path, "18.0.0", "14.44.35207"),
            "x86_64",
            NoHostEnvironment,
            TestContext.Current.CancellationToken);
        var unpinned = await provider.SetUpAsync(
            "vs",
            DeveloperEnvironmentCheck.Installed(temp.Path, "18.0.0"),
            "x86_64",
            NoHostEnvironment,
            TestContext.Current.CancellationToken);

        Assert.False(pinned.HasFailed, pinned.Failure);
        Assert.Equal("[amd64][-vcvars_ver][14.44.35207][]", pinned.Environment["GIVEN"]);
        Assert.Equal("14.44.35207", pinned.Fact.ToolsVersion);
        Assert.False(unpinned.HasFailed, unpinned.Failure);
        Assert.Equal("[amd64][][][]", unpinned.Environment["GIVEN"]);
        Assert.False(unpinned.Environment.ContainsKey("VCToolsVersion"), "an environment naming no toolset asks for none");
    }

    /// <summary>A file system in which no directory can be made, as where the temporary directory cannot be written.</summary>
    private sealed class UnwritableScratch(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public const string Said = "Access to the path is denied.";

        public override void CreateDirectory(string path) => throw new UnauthorizedAccessException(Said);
    }

    /// <summary>A file system whose directories cannot be removed, as while a scanner holds a file in one.</summary>
    private sealed class HeldFiles(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public const string Said = "The process cannot access the file because it is being used by another process.";

        public override void DeleteDirectory(string path) => throw new IOException(Said);
    }

    /// <summary>
    /// The argument vcvarsall.bat takes: the machine's processor alone for a native build, and the two
    /// joined to cross-compile; none for a processor it has no word for.
    /// </summary>
    [Theory]
    [InlineData("x86_64", "x86_64", "amd64", "x64")]
    [InlineData("arm64", "arm64", "arm64", "arm64")]
    [InlineData("x86_64", "arm64", "amd64_arm64", "arm64")]
    [InlineData("arm64", "x86_64", "arm64_amd64", "x64")]
    [InlineData("x86_64", "x86", "amd64_x86", "x86")]
    [InlineData("x86_64", "riscv64", null, null)]
    [InlineData("riscv64", "x86_64", null, null)]
    public void TheArchitecture_IsTheMachinesProcessorThenTheLegs(string host, string target, string? architecture, string? reported)
    {
        Assert.Equal(architecture, VisualStudioArchitecture.For(host, target));

        if (architecture is not null)
        {
            Assert.Equal(reported, VisualStudioArchitecture.Target(architecture));
        }
    }

    /// <summary>What <c>set</c> lists is read one variable to a line, splitting at the first '=' only.</summary>
    [Fact]
    public void WhatSetLists_IsReadOneVariableToALine()
    {
        var variables = DeveloperEnvironmentProvider.Variables("A=1\r\nB=x=y\r\n=C:=C:\\\r\nnothing\r\nc=\n");

        Assert.Equal(
            new Dictionary<string, string> { ["A"] = "1", ["B"] = "x=y", ["c"] = string.Empty },
            variables.ToDictionary(StringComparer.Ordinal));
        Assert.Equal("1", variables["a"]);
    }

    /// <summary>
    /// Visual Studio as it is installed on this machine: cl is on the PATH it sets up, beside INCLUDE
    /// and LIB, for this machine's own processor. Skipped where this machine has no Visual Studio with
    /// the C++ build tools - and failed there instead, where a Windows machine says it is meant to hold
    /// every build tool (<see cref="BuildTools"/>).
    /// </summary>
    [Fact]
    public async Task TheVisualStudioOnThisMachine_SetsUpCl()
    {
        var platform = new HostPlatform();
        var processes = new ProcessRunner(platform, FilePermissionsFactory.Create(), CliRunner.CliProgram);
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var probe = new DeveloperEnvironmentProbe(platform, processes, fileSystem);
        var token = TestContext.Current.CancellationToken;

        var found = await probe.CheckAsync(VisualStudio, token);

        if (!found.CanSetUp)
        {
            BuildTools.Lacks($"This machine has no Visual Studio with the C++ build tools: {found.Reason}", couldHold: OperatingSystem.IsWindows());
        }

        var provider = new DeveloperEnvironmentProvider(platform, processes, fileSystem, new HarnessFactory().Output);
        var setUp = await provider.SetUpAsync("vs", found, platform.Processor, NoHostEnvironment, token);

        Assert.Null(setUp.Failure);
        Assert.False(string.IsNullOrEmpty(setUp.Environment["INCLUDE"]));
        Assert.False(string.IsNullOrEmpty(setUp.Environment["LIB"]));
        Assert.Contains(
            setUp.Environment["PATH"].Split(';', StringSplitOptions.RemoveEmptyEntries),
            directory => File.Exists(Path.Combine(directory, "cl.exe")));
        Assert.Equal(VisualStudioArchitecture.For(platform.Processor, platform.Processor), setUp.Fact!.Architecture);
        Assert.NotEqual("unknown", setUp.Fact.ToolsVersion);
        Assert.Equal(found.InstallationPath, setUp.Fact.InstallationPath);
    }

    /// <summary>
    /// Visual Studio as it is installed on this machine, held to a toolset it holds - its oldest, by its line's two
    /// numbers, which is not the default wherever the instance holds two - by the survey and by vcvarsall.bat: the
    /// environment set up is that toolset's, its cl first on PATH. Skipped, or failed, as the test above is.
    /// </summary>
    [Fact]
    public async Task TheVisualStudioOnThisMachine_SetsUpTheToolsetNamed()
    {
        var platform = new HostPlatform();
        var processes = new ProcessRunner(platform, FilePermissionsFactory.Create(), CliRunner.CliProgram);
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var probe = new DeveloperEnvironmentProbe(platform, processes, fileSystem);
        var token = TestContext.Current.CancellationToken;

        var newest = await probe.CheckAsync(VisualStudio, token);

        if (!newest.CanSetUp)
        {
            BuildTools.Lacks($"This machine has no Visual Studio with the C++ build tools: {newest.Reason}", couldHold: OperatingSystem.IsWindows());
        }

        // The oldest line whose newest toolset builds for this machine's processor on it: a toolset installed for other
        // processors alone has no cl for this one, and setting it up would say so rather than test anything.
        var msvc = Path.Combine(newest.InstallationPath!, "VC", "Tools", "MSVC");
        var toolsets = VisualStudioToolsets.Of(fileSystem.EnumerateDirectories(msvc).Select(Path.GetFileName).OfType<string>());
        var target = VisualStudioArchitecture.Target(VisualStudioArchitecture.For(platform.Processor, platform.Processor)!);
        var line = toolsets
            .Select(toolset => string.Join('.', toolset.Split('.')[..2]))
            .Distinct(StringComparer.Ordinal)
            .LastOrDefault(line => File.Exists(Path.Combine(msvc, VisualStudioToolsets.Newest(line, toolsets)!, "bin", "Host" + target, target, "cl.exe")));

        if (line is null)
        {
            BuildTools.Lacks($"No MSVC toolset in '{msvc}' builds for {platform.Processor} on it.", couldHold: OperatingSystem.IsWindows());
        }

        var found = await probe.CheckAsync(new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio, ToolsVersion = line }, token);

        Assert.True(found.CanSetUp, found.Reason);
        Assert.StartsWith(line + ".", found.ToolsVersion, StringComparison.Ordinal);

        var setUp = await new DeveloperEnvironmentProvider(platform, processes, fileSystem, new HarnessFactory().Output)
            .SetUpAsync("pinned", found, platform.Processor, NoHostEnvironment, token);

        Assert.Null(setUp.Failure);
        Assert.Equal(found.ToolsVersion, setUp.Fact!.ToolsVersion);
        Assert.Equal(found.ToolsVersion, setUp.Environment["VCToolsVersion"]);
        Assert.Equal(found.InstallationPath, setUp.Fact.InstallationPath);

        // The first cl on the PATH it set up is that toolset's own.
        var cl = setUp.Environment["PATH"].Split(';', StringSplitOptions.RemoveEmptyEntries).First(directory => File.Exists(Path.Combine(directory, "cl.exe")));
        Assert.StartsWith(
            Path.Combine(found.InstallationPath!, "VC", "Tools", "MSVC", found.ToolsVersion!) + Path.DirectorySeparatorChar,
            cl,
            StringComparison.OrdinalIgnoreCase);
    }
}
