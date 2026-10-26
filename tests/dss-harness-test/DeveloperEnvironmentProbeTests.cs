using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// Whether a host can set up a developer environment, as the survey asks it: Visual Studio's own
/// installer names the instance, and a host without one says what was looked for rather than
/// letting a leg fail halfway through for want of <c>cl</c>.
/// </summary>
public sealed class DeveloperEnvironmentProbeTests
{
    private static readonly DeveloperEnvironmentConfig VisualStudio = new() { Kind = DeveloperEnvironmentKinds.VisualStudio };

    /// <summary>
    /// vswhere is asked for the newest instance holding the component, in JSON and UTF-8, and the
    /// instance it names is the one that would be set up, with its version.
    /// </summary>
    [Fact]
    public async Task AnInstanceWithTheComponent_IsTheOneThatWouldBeSetUp()
    {
        using var visualStudio = new ScriptedVisualStudio();
        var environment = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio, RequiresComponent = "Microsoft.VisualStudio.Component.VC.Tools.ARM64" };

        var check = await visualStudio.Probe().CheckAsync(environment, TestContext.Current.CancellationToken);

        Assert.True(check.CanSetUp, check.Reason);
        Assert.Equal(visualStudio.InstallationPath, check.InstallationPath);
        Assert.Equal(ScriptedVisualStudio.InstallationVersion, check.InstallationVersion);

        // Naming no toolset, the environment is vcvarsall.bat's default, and the survey names none.
        Assert.Null(check.ToolsVersion);

        var asked = Assert.Single(visualStudio.Probes);
        Assert.Equal(DeveloperEnvironmentProbe.VsWherePath, asked.FileName);
        Assert.Equal(
            ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.ARM64", "-format", "json", "-utf8"],
            asked.Arguments);
    }

    /// <summary>
    /// An environment naming an MSVC toolset is surveyed in every instance holding the component, newest first, and the
    /// newest instance whose VC\Tools\MSVC holds a toolset it names is the one set up, with the newest of those it holds -
    /// compared by their numbers, never as text. A newer toolset of the line in an older instance does not outrank the
    /// newer instance: the instance is chosen first, as it is where no toolset is named.
    /// </summary>
    [Fact]
    public async Task ANamedToolset_ChoosesTheNewestInstanceHoldingIt_WithTheNewestOfItsLineThere()
    {
        using var newest = Instance("14.51.36231");
        using var holding = Instance("14.44.9999", "14.44.35207", "14.29.30133");
        using var oldest = Instance("14.44.35300");
        using var visualStudio = new ScriptedVisualStudio { Instances = Answer(newest, holding, oldest) };
        var environment = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio, ToolsVersion = "14.44" };

        var check = await visualStudio.Probe().CheckAsync(environment, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentCheck.Installed(holding.Path, "18.1.0", "14.44.35207"), check);
        Assert.Equal(
            ["-sort", "-products", "*", "-requires", DeveloperEnvironmentConfig.DefaultVisualStudioComponent, "-format", "json", "-utf8"],
            Assert.Single(visualStudio.Probes).Arguments);
    }

    /// <summary>A toolset named by all three of its numbers is that toolset alone, never a newer one of its line.</summary>
    [Fact]
    public async Task AToolsetNamedWhole_IsThatToolsetAlone()
    {
        using var instance = Instance("14.44.35207", "14.44.34823", "14.51.36231");
        using var visualStudio = new ScriptedVisualStudio { Instances = Answer(instance) };
        var environment = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio, ToolsVersion = "14.44.34823" };

        var check = await visualStudio.Probe().CheckAsync(environment, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentCheck.Installed(instance.Path, "18.0.0", "14.44.34823"), check);
    }

    /// <summary>
    /// Where no instance holds a toolset the environment names, the host cannot set it up - a tool missing there, naming
    /// the component, the toolset and what each instance holds, newest first, which is what to install or name instead -
    /// rather than setting up another toolset, which is the build the environment names its toolset to keep out. A
    /// directory under VC\Tools\MSVC that is no toolset's name, and one of a line that only begins like it, are not one.
    /// </summary>
    [Fact]
    public async Task NoInstanceHoldingTheNamedToolset_CannotSetItUp_NamingWhatEachHolds()
    {
        using var newest = Instance("14.51.36231", "14.440.1", "14.4", "14.44.x");
        using var bare = Instance();
        using var visualStudio = new ScriptedVisualStudio { Instances = Answer(newest, bare) };
        var environment = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio, ToolsVersion = "14.44" };

        var check = await visualStudio.Probe().CheckAsync(environment, TestContext.Current.CancellationToken);

        Assert.Equal(
            DeveloperEnvironmentCheck.Nowhere(
                $"no Visual Studio instance there with the component '{DeveloperEnvironmentConfig.DefaultVisualStudioComponent}' holds "
                + $"MSVC toolset 14.44, which toolsVersion names: '{newest.Path}' holds 14.440.1, 14.51.36231; '{bare.Path}' holds none"),
            check);
    }

    /// <summary>
    /// An instance whose toolsets cannot be listed leaves the survey unable to say whether the host can set the
    /// environment up - never read as one holding none, which would choose an older instance past it, or none at all.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnInstanceWhoseToolsetsCannotBeListed_IsNotKnownEitherWay(bool denied)
    {
        using var unlisted = Instance("14.44.35207");
        using var older = Instance("14.44.35207");
        using var visualStudio = new ScriptedVisualStudio { Instances = Answer(unlisted, older) };
        var environment = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio, ToolsVersion = "14.44" };
        var toolsets = Path.Combine(unlisted.Path, "VC", "Tools", "MSVC");

        var check = await visualStudio.Probe(new CannotList(new PhysicalFileSystem(FilePermissionsFactory.Create()), toolsets, denied))
            .CheckAsync(environment, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentCheck.Unreadable($"the MSVC toolsets in '{toolsets}' could not be listed: the disk would not say."), check);
    }

    /// <summary>
    /// What an environment may name as its toolset, and which of an instance's toolsets that names: two numbers name every
    /// toolset of their line, three name one, and nothing else names any - not a side-by-side component's four, not a
    /// line that only begins like it, as vcvarsall.bat would read 14.4 to take 14.44.
    /// </summary>
    [Theory]
    [InlineData("14.44", "14.44.35207", true)]
    [InlineData("14.44", "14.44.9999", true)]
    [InlineData("14.44.35207", "14.44.35207", true)]
    [InlineData("14.44.35207", "14.44.35208", false)]
    [InlineData("14.44.3520", "14.44.35207", false)]
    [InlineData("14.4", "14.44.35207", false)]
    [InlineData("14.44", "14.440.1", false)]
    [InlineData("14.44", "14.51.36231", false)]
    [InlineData("14.44", "14.44", false)]
    [InlineData("14.44", "14.44.35207.1", false)]
    [InlineData("14.44", "14.44.x", false)]
    public void AToolsVersion_NamesTheToolsetsOfItsLine_OrTheOneItSpellsWhole(string toolsVersion, string toolset, bool named)
        => Assert.Equal(named, VisualStudioToolsets.Names(toolsVersion, toolset));

    /// <summary>What config.json may give as toolsVersion: two or three numbers of ASCII digits joined by dots, and nothing else.</summary>
    [Theory]
    [InlineData("14.44", true)]
    [InlineData("14.44.35207", true)]
    [InlineData("0.0", true)]
    [InlineData("14", false)]
    [InlineData("14.44.17.14", false)]
    [InlineData("v143", false)]
    [InlineData("Preview", false)]
    [InlineData("14.44 ", false)]
    [InlineData(" 14.44", false)]
    [InlineData("14.44\n", false)]
    [InlineData("14..44", false)]
    [InlineData("14.44.", false)]
    [InlineData("14.x", false)]
    [InlineData("\u0661\u0664.\u0664\u0664", false)]
    [InlineData("", false)]
    public void AToolsVersion_IsTwoOrThreeNumbersJoinedByDots(string toolsVersion, bool wellFormed)
        => Assert.Equal(wellFormed, VisualStudioToolsets.IsToolsVersion(toolsVersion));

    /// <summary>Toolsets are ordered by their numbers, each in turn, never as text.</summary>
    [Fact]
    public void Toolsets_AreOrderedByTheirNumbers()
        => Assert.Equal(
            ["14.51.36231", "14.44.35207", "14.44.9999", "14.9.1", "9.0.1"],
            VisualStudioToolsets.Of(["14.44.9999", "9.0.1", "14.51.36231", "readme", "14.9.1", "14.44.35207"]));

    /// <summary>The C++ build tools for x86 and x64 are what an instance must have when nothing else is named.</summary>
    [Fact]
    public void TheComponentRequired_IsTheCppBuildTools_WhenNoneIsNamed()
        => Assert.Equal("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", VisualStudio.RequiresComponent);

    /// <summary>A host running another system is never asked: Visual Studio sets up on Windows alone.</summary>
    [Fact]
    public async Task AHostOfAnotherSystem_CannotSetItUp_AndVsWhereIsNeverStarted()
    {
        var processes = Substitute.For<IProcessRunner>();

        var check = await new DeveloperEnvironmentProbe(HostDoubles.Platform(PlatformId.Linux), processes, Substitute.For<IFileSystem>())
            .CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal("Visual Studio is set up on windows, and this host runs linux", check.Reason);
        await processes.DidNotReceiveWithAnyArgs().RunAsync(default!, TestContext.Current.CancellationToken);
    }

    /// <summary>A kind this build does not know is said to be one, never looked for as Visual Studio.</summary>
    [Fact]
    public async Task AKindThisBuildDoesNotKnow_CannotBeSetUp()
    {
        using var visualStudio = new ScriptedVisualStudio();

        var check = await visualStudio.Probe().CheckAsync(new DeveloperEnvironmentConfig { Kind = "xcode" }, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal("this build does not know how to set up a 'xcode' environment", check.Reason);
        Assert.Empty(visualStudio.Probes);
    }

    /// <summary>A machine without Visual Studio's installer says where vswhere was looked for.</summary>
    [Fact]
    public async Task NoInstaller_SaysWhereVsWhereWasLookedFor()
    {
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ExecutableNotFoundException(DeveloperEnvironmentProbe.VsWherePath));

        var check = await new DeveloperEnvironmentProbe(HostDoubles.Platform(PlatformId.Windows), processes, Substitute.For<IFileSystem>())
            .CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal($"Visual Studio's installer is not there: '{DeveloperEnvironmentProbe.VsWherePath}' was not found", check.Reason);
    }

    /// <summary>A vswhere the system will not start is said to be one, with why.</summary>
    [Fact]
    public async Task AVsWhereThatWillNotStart_SaysWhy()
    {
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProgramStartException(DeveloperEnvironmentProbe.VsWherePath, "Access is denied."));

        var check = await new DeveloperEnvironmentProbe(HostDoubles.Platform(PlatformId.Windows), processes, Substitute.For<IFileSystem>())
            .CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Unreadable, check.Found);
        Assert.Equal($"'{DeveloperEnvironmentProbe.VsWherePath}' could not be started: Access is denied.", check.Reason);
    }

    /// <summary>A vswhere that fails says so, with what it said, rather than reading its output as an answer.</summary>
    [Fact]
    public async Task AVsWhereThatFails_IsNotReadAsAnAnswer()
    {
        using var visualStudio = new ScriptedVisualStudio { VsWhereExitCode = 87 };

        var check = await visualStudio.Probe().CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Unreadable, check.Found);
        Assert.Equal($"'{DeveloperEnvironmentProbe.VsWherePath}' could not list the Visual Studio instances (exit 87): vswhere: no instances", check.Reason);
    }

    /// <summary>No instance holding the component names the component, which is what to install.</summary>
    [Fact]
    public async Task NoInstanceWithTheComponent_NamesTheComponent()
    {
        using var visualStudio = new ScriptedVisualStudio { Instances = "[]" };

        var check = await visualStudio.Probe().CheckAsync(VisualStudio, TestContext.Current.CancellationToken);

        Assert.Equal(DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal("no Visual Studio instance there has the component 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'", check.Reason);
    }

    /// <summary>
    /// What vswhere prints is read for its first instance's path and version; anything else is no
    /// instance, and what cannot be read at all says so.
    /// </summary>
    [Theory]
    [InlineData("""[{"installationPath":"C:\\VS","installationVersion":"18.0.1"}]""", true, "C:\\VS", "18.0.1")]
    [InlineData("""[{"installationPath":"C:\\VS"}]""", true, "C:\\VS", null)]
    [InlineData("""[{"installationPath":"C:\\VS","installationVersion":18}]""", true, "C:\\VS", null)]
    [InlineData("""[{"installationPath":7}]""", false, null, null)]
    [InlineData("""[{"installationPath":""}]""", false, null, null)]
    [InlineData("""[{"installationVersion":"18.0.1"}]""", false, null, null)]
    [InlineData("""[42]""", false, null, null)]
    [InlineData("""{"installationPath":"C:\\VS"}""", false, null, null)]
    public void WhatVsWherePrints_IsReadForItsFirstInstance(string json, bool available, string? path, string? version)
    {
        var check = Reader().Read(json, new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio, RequiresComponent = "C" });

        Assert.Equal(available ? DeveloperEnvironmentFound.Installed : DeveloperEnvironmentFound.Nowhere, check.Found);
        Assert.Equal(path, check.InstallationPath);
        Assert.Equal(version, check.InstallationVersion);
        Assert.Equal(available ? null : "no Visual Studio instance there has the component 'C'", check.Reason);
    }

    /// <summary>An answer that is not JSON is said to be unreadable, never read as no instance.</summary>
    [Fact]
    public void AnAnswerThatIsNotJson_IsSaidToBeUnreadable()
    {
        var check = Reader().Read("Visual Studio Locator version 3.1.7", VisualStudio);

        Assert.Equal(DeveloperEnvironmentFound.Unreadable, check.Found);
        Assert.StartsWith("vswhere answered in a form this build cannot read: ", check.Reason, StringComparison.Ordinal);
    }

    /// <summary>A survey that reads what it is handed, and starts and lists nothing.</summary>
    private static DeveloperEnvironmentProbe Reader()
        => new(HostDoubles.Platform(PlatformId.Windows), Substitute.For<IProcessRunner>(), Substitute.For<IFileSystem>());

    /// <summary>An instance of its own, holding <paramref name="toolsets"/> under VC\Tools\MSVC; none, where none are given.</summary>
    private static TempDirectory Instance(params string[] toolsets)
    {
        var instance = new TempDirectory();

        foreach (var toolset in toolsets)
        {
            Directory.CreateDirectory(instance.Combine("VC", "Tools", "MSVC", toolset));
        }

        return instance;
    }

    /// <summary>What vswhere -sort prints for <paramref name="instances"/>, newest first: the last 18.0.0, each before it one minor version newer.</summary>
    private static string Answer(params TempDirectory[] instances)
        => JsonSerializer.Serialize(instances.Select((instance, index) => new
        {
            installationPath = instance.Path,
            installationVersion = $"18.{instances.Length - 1 - index}.0",
        }));
}
