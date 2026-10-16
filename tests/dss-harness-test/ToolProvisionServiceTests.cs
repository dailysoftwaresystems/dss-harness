using System.Runtime.CompilerServices;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;
using DssHarness.Core.Tools;

namespace DssHarness.Tests;

/// <summary>
/// Installing what a leg's host is missing, before anything tries to run there. Every host is
/// answered by a script, so no test needs WSL, ssh, a package manager or a network, and every
/// address, user, distribution and credential here is fictitious.
/// </summary>
public sealed class ToolProvisionServiceTests
{
    private const string Distro = "wsl-a";

    /// <summary>A second distribution, so two hosts that each need a password can be told apart.</summary>
    private const string OtherDistro = "wsl-b";

    /// <summary>The ssh host a fixture declares when asked to reach its leg over ssh.</summary>
    private const string SshName = "build-box";

    /// <summary>The name an ssh host may be declared by, which resolves to 192.0.2.10.</summary>
    private const string ResolvedName = "build-box.invalid";

    private const string Home = "/home/harness";

    private const string Credential = "not-a-real-password";

    /// <summary>What a tool reports once this run has installed it.</summary>
    private const string InstalledVersion = "1.12.1";

    [Fact]
    public async Task AHostWithNoDotnetOnItsLoginFreePath_HasTheSdkInstalled_AndIsThenReady()
    {
        using var fixture = new Fixture();

        var report = await fixture.ProvisionAsync();

        var leg = Assert.Single(report.Legs);
        var dotnet = Assert.Single(leg.Tools, tool => tool.Tool == "dotnet");
        Assert.Equal(ToolState.Installed, dotnet.State);
        Assert.Equal("10.0.100", dotnet.Version);
        Assert.True(report.Passed);

        // The installer is a shell script travelling on standard input, so nothing in it has to survive
        // a shell's word splitting on the way to the host.
        var script = Assert.Single(fixture.Host.Calls, call => call.Program == "sh" && call.Arguments.Count == 0);
        Assert.Contains("dotnet-install.sh", script.StandardInput, StringComparison.Ordinal);
        Assert.Contains("--channel 10.0", script.StandardInput, StringComparison.Ordinal);

        // Measured again after the install: the SDK lands in ~/.dotnet, which is on no PATH of a
        // command run without a login shell, so only its absolute path starts it.
        Assert.Contains(
            fixture.Host.Calls,
            call => call.Program == $"{Home}/.dotnet/dotnet" && call.Arguments.Contains("--list-sdks"));
    }

    [Fact]
    public async Task ASecondRun_ReportsAlreadyCurrent_AndChangesNothing()
    {
        using var fixture = new Fixture();

        _ = await fixture.ProvisionAsync();
        fixture.Host.Calls.Clear();

        var report = await fixture.ProvisionAsync();

        Assert.True(report.Passed);
        Assert.All(Assert.Single(report.Legs).Tools, tool => Assert.Equal("already current", tool.StateName));
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sh" && call.Arguments.Count == 0);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo");
    }

    [Fact]
    public async Task Legs_RestrictsWhatIsProvisioned()
    {
        using var fixture = new Fixture(twoLegs: true);

        var report = await fixture.ProvisionAsync(["only-local"]);

        var leg = Assert.Single(report.Legs);
        Assert.Equal("only-local", leg.Leg);
        Assert.Equal(HostId.Local, leg.Host);

        // Nothing reached the distribution at all: the leg that runs there was not selected.
        Assert.Empty(fixture.Host.Calls);
    }

    [Fact]
    public async Task AnUnreachableHost_IsNamed_AndTheOtherLegsStillGoAhead()
    {
        using var fixture = new Fixture(twoLegs: true, distributionExists: false);

        var report = await fixture.ProvisionAsync();

        Assert.Equal(2, report.Legs.Count);
        var remote = Assert.Single(report.Legs, leg => leg.Host.Kind == HostKind.Wsl);
        Assert.NotNull(remote.Unreachable);
        Assert.Contains("WSL has no distribution named 'Example-Linux'", remote.Unreachable, StringComparison.Ordinal);
        Assert.Contains("could not be reached", fixture.Error.ToString(), StringComparison.Ordinal);

        var local = Assert.Single(report.Legs, leg => leg.Host.Kind == HostKind.Local);
        Assert.True(local.Provisioned);

        // A host that could not be reached is a different answer from a tool that is missing.
        var outcome = ToolProvisionReports.Render(report, json: false);
        Assert.Equal(HarnessExit.HostUnavailable, outcome.ExitCode);
    }

    /// <summary>
    /// Each host is asked its own survey once - the one a leg there is placed by - about every tool its legs need and the
    /// program each install would run, save one run through sudo, which looks its program up itself; nothing about where
    /// a tool is is asked of the host's shell.
    /// </summary>
    [Fact]
    public async Task EachHost_IsAskedItsOwnSurvey_AboutEveryToolItsLegsNeed_AndTheProgramEachInstallRuns()
    {
        var brew = $"{FakeHost.Homebrew}/brew";
        using var fixture = new Fixture(
            tools: [Apt("ninja"), Brew("python3")],
            present: new() { ["ninja"] = "1.12.0" },
            offPath: new() { [$"{FakeHost.Homebrew}/python3"] = "1.13.0", [brew] = "4.4.0" });

        var report = await fixture.ProvisionAsync();

        Assert.True(report.Passed);
        var survey = Assert.Single(fixture.Survey.Surveys);
        Assert.Equal(HostId.Wsl(Distro), survey.Host);
        Assert.Equal(["ninja", "python3", "brew"], survey.Programs);
        Assert.Empty(survey.Looks);
        string[] asked = ["ninja", "python3", "brew"];
        Assert.DoesNotContain(
            fixture.Host.Calls,
            call => call.Program is "ls" or "sh" or "where.exe" && call.Arguments.Any(argument => asked.Contains(argument.Split('/', ' ')[^1])));
    }

    /// <summary>
    /// A tool the host's survey could not establish is not known either way - it could not look everywhere it was told to,
    /// or the survey itself could not be run there - and is reported with the survey's own reason, and nothing is
    /// installed: a second copy of a tool a leg there can already start is the one outcome this must never produce.
    /// </summary>
    [Fact]
    public async Task AToolTheSurveyCouldNotEstablish_IsUnknown_WithItsOwnReason_AndNothingIsInstalled()
    {
        const string Unsearched = "the home directory is not known here, so '~/tools' could not be searched";

        using var fixture = new Fixture(tools: [Apt("ninja")]);
        fixture.Survey.Scripted = (_, program) => program == "ninja" ? new ProgramLocation(program, ProgramFound.Unreadable, Reason: Unsearched) : null;

        var unsearched = Assert.Single(Assert.Single((await fixture.ProvisionAsync()).Legs).Tools, tool => tool.Tool == "ninja");

        Assert.Equal((ToolState.Unknown, Unsearched), (unsearched.State, unsearched.Detail));

        using var unsurveyed = new Fixture(tools: [Apt("ninja")]);
        unsurveyed.Survey.Fails = _ => "its global .NET tools could not be listed";

        var unasked = Assert.Single(Assert.Single((await unsurveyed.ProvisionAsync()).Legs).Tools, tool => tool.Tool == "ninja");

        Assert.Equal((ToolState.Unknown, "the host's survey could not be run there: its global .NET tools could not be listed"), (unasked.State, unasked.Detail));
        Assert.DoesNotContain(fixture.Host.Calls.Concat(unsurveyed.Host.Calls), call => call.Program == "sudo");
    }

    /// <summary>
    /// A dry run on a host with no .NET SDK yet would install one, and has every other tool unknown: the survey that
    /// judges them runs on DssHarness, which the SDK it would install is what runs; nothing is installed, and no
    /// survey's answer is made up in its place.
    /// </summary>
    [Fact]
    public async Task ADryRun_OnAHostWithNoSdkYet_HasEveryToolUnknown_AndRunsNothingThere()
    {
        using var fixture = new Fixture(tools: [Apt("ninja"), Apt("cmake", minVersion: "3.20.0")], present: new() { ["cmake"] = "3.16.0" });

        var report = await fixture.ProvisionAsync(dryRun: true);
        var tools = Assert.Single(report.Legs).Tools;

        Assert.Equal(ToolState.WouldInstall, Assert.Single(tools, tool => tool.Tool == "dotnet").State);
        Assert.All(
            tools.Where(tool => tool.Tool != "dotnet"),
            tool => Assert.Equal((ToolState.Unknown, $"the host's survey could not be run there: {FakeSurvey.NoSdk}"), (tool.State, tool.Detail)));
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo" || (call.Program == "sh" && call.Arguments.Count == 0));
        Assert.False(report.Passed);
    }

    /// <summary>
    /// A host none of whose legs needs a tool there is never surveyed: the survey brings DssHarness there to this
    /// machine's version, for an answer nothing would read.
    /// </summary>
    [Fact]
    public async Task AHostWhoseLegsNeedNoToolThere_IsNeverSurveyed()
    {
        using var fixture = new Fixture(tools: [new ToolConfig { Name = "cl", Platforms = ["windows"] }], sdk: true);

        var report = await fixture.ProvisionAsync();

        Assert.True(report.Passed);
        Assert.Empty(fixture.Survey.Surveys);
        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "dotnet").State);
    }

    /// <summary>
    /// A copy that meets the minimum behind an older one a directory raised for another tool holds - the one a leg there
    /// starts - is reported outdated, saying which copy to update or remove, and nothing is installed: an install would
    /// not say which copy a leg starts, as a leg turned away over it is told.
    /// </summary>
    [Fact]
    public async Task ACopyAShadowedToolsLegsStart_IsOutdated_NamingWhatToUpdate_AndNothingIsInstalled()
    {
        const string Shadowed = "/opt/homebrew/bin/cmake is 3.20.0, below the minVersion 3.28.0 'cmake' is declared with, and a directory "
            + "raised ahead of the PATH for another tool puts it ahead of /usr/bin/cmake, which is 3.31.0; update or remove it, so the "
            + "copy that meets it is the one a leg starts";

        using var fixture = new Fixture(tools: [Apt("cmake", minVersion: "3.28.0")], present: new() { ["cmake"] = "3.31.0" });
        fixture.Survey.Scripted = (_, program) => program == "cmake"
            ? new ProgramLocation(program, ProgramFound.FloorShadowed, "/opt/homebrew/bin/cmake", Shadowed) { Judged = new FloorCopy("/opt/homebrew/bin/cmake", "3.20.0", null) }
            : null;

        var cmake = Assert.Single(Assert.Single((await fixture.ProvisionAsync()).Legs).Tools, tool => tool.Tool == "cmake");

        Assert.Equal((ToolState.Outdated, "3.20.0", Shadowed), (cmake.State, cmake.Version, cmake.Detail));
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo");
    }

    [Fact]
    public async Task ATool_IsInstalledThroughItsManager_WithTheCredentialOnStandardInputAlone()
    {
        // Somebody is at the terminal, and must not be interrupted for a password this host already
        // declares: the credential is looked for before anyone is asked, not after.
        using var fixture = new Fixture(tools: [Apt("ninja")], prompting: PromptAvailability.Available);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Installed, ninja.State);

        var install = Assert.Single(fixture.Host.Calls, call => call.Program == "sudo");

        // -S makes sudo read the password from standard input, which is the only place it travels: an
        // argument list is visible in that host's own process table to every user on it.
        Assert.Equal(["-S", "apt-get", "install", "-y", "ninja-build"], install.Arguments);
        Assert.Equal(Credential + "\n", install.StandardInput);
        Assert.Empty(fixture.Prompt.Asked);
    }

    [Fact]
    public async Task TheCredential_ReachesNoLog_NoArgumentList_AndNoMessage()
    {
        // A host that echoes what it was given is exactly how a credential reaches a log nobody meant
        // to hold one, so what it printed is scrubbed before it can be reported.
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            installFails: $"sudo: a password was supplied: {Credential}",
            sdk: true);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);

        var rendered = ToolProvisionReports.Render(report, json: true);
        var everythingSaid = string.Join('\n', [.. rendered.Data, rendered.Message, fixture.Output.ToString(), fixture.Error.ToString()]);

        Assert.DoesNotContain(Credential, everythingSaid, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, ninja.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Host.Calls,
            call => call.Arguments.Any(argument => argument.Contains(Credential, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task APrivilegedInstallWithNoCredential_IsRefusedByName_RatherThanAttemptedWithNothing()
    {
        using var fixture = new Fixture(tools: [Apt("ninja")], credential: null, passwordlessSudo: false);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);
        Assert.Contains($"wslDistros/{Distro}/.env", ninja.Detail, StringComparison.Ordinal);
        Assert.Contains("SUDO_PASSWORD", ninja.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    [Fact]
    public async Task AHostWhereSudoNeedsNoPassword_IsUsedWithoutOne()
    {
        // As above, for the other way a host needs no answer from anybody: whether sudo wants a
        // password at all is measured before a person is interrupted for one.
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            passwordlessSudo: true,
            prompting: PromptAvailability.Available);

        var report = await fixture.ProvisionAsync();

        Assert.Equal(ToolState.Installed, Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja").State);
        Assert.Contains(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments[0] == "-n" && call.Arguments.Contains("apt-get"));
        Assert.Empty(fixture.Prompt.Asked);
    }

    /// <summary>
    /// What holding one in memory is for: a host with two privileged tools is asked once, not once for
    /// each of them.
    /// </summary>
    [Fact]
    public async Task APromptedPassword_IsAskedOnceForAHost_ThenUsedForEveryToolOnIt()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja"), Apt("cmake")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => Credential);

        var report = await fixture.ProvisionAsync();

        var installed = Assert.Single(report.Legs).Tools.Where(tool => tool.Tool is "ninja" or "cmake").ToList();
        Assert.Equal(2, installed.Count);
        Assert.All(installed, tool => Assert.Equal(ToolState.Installed, tool.State));

        Assert.Equal([$"wsl {Distro}"], fixture.Prompt.Asked);

        // Checked against the host once, before either install ran on the strength of it.
        Assert.Single(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments is ["-S", "-v"]);

        // Both installs carried it, on standard input, exactly as a declared credential does.
        var sudo = fixture.Host.Calls.Where(call => call.Program == "sudo" && call.Arguments.Contains("apt-get")).ToList();
        Assert.Equal(2, sudo.Count);
        Assert.All(sudo, call => Assert.Equal("-S", call.Arguments[0]));
        Assert.All(sudo, call => Assert.Equal(Credential + "\n", call.StandardInput));
    }

    /// <summary>
    /// Checked against the host before an install that may run for half an hour rides on it, and
    /// checked once. Asking again for the next tool would spend a second failed authentication on the
    /// same mistake, and a host may count those toward locking the account.
    /// </summary>
    [Fact]
    public async Task AWrongPromptedPassword_RefusesTheTool_WithoutInstalling_AndWithoutAskingTwice()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja"), Apt("cmake")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => "not-what-that-host-has");

        var report = await fixture.ProvisionAsync();

        var refused = Assert.Single(report.Legs).Tools.Where(tool => tool.Tool is "ninja" or "cmake").ToList();
        Assert.Equal(2, refused.Count);
        Assert.All(refused, tool => Assert.Equal(ToolState.Failed, tool.State));
        Assert.All(refused, tool => Assert.Contains("was not accepted", tool.Detail, StringComparison.Ordinal));

        Assert.Single(fixture.Prompt.Asked);
        Assert.Single(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments is ["-S", "-v"]);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    /// <summary>
    /// Answering with nothing is declining, and declining is an answer: the next tool on that host is
    /// refused rather than asked the same question again.
    /// </summary>
    [Fact]
    public async Task ADeclinedPrompt_IsNotPutAgainToTheSameHost()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja"), Apt("cmake")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => string.Empty);

        var report = await fixture.ProvisionAsync();

        var refused = Assert.Single(report.Legs).Tools.Where(tool => tool.Tool is "ninja" or "cmake").ToList();
        Assert.Equal(2, refused.Count);
        Assert.All(refused, tool => Assert.Equal(ToolState.Failed, tool.State));
        Assert.All(refused, tool => Assert.Contains("no password for one", tool.Detail, StringComparison.Ordinal));

        Assert.Single(fixture.Prompt.Asked);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments is ["-S", "-v"]);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    /// <summary>
    /// The constraint the design turns on. A password belongs to the host it was typed for; two hosts
    /// are two questions, because trying the first answer on the second spends somebody's failed-login
    /// budget on a machine they never meant to touch.
    /// </summary>
    [Fact]
    public async Task APasswordTypedForOneHost_IsNeverOfferedToAnother()
    {
        const string First = "password-for-the-first-distribution";
        const string Second = "password-for-the-second-distribution";

        // Each host has its own correct password, so both can succeed. A leak then shows up as a host
        // installing with the other one's answer, not merely as one host failing.
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: host => host.Name == Distro ? First : Second,
            secondDistro: true,
            rootPassword: host => host.Name == Distro ? First : Second);

        var report = await fixture.ProvisionAsync();

        // Both got there, each on its own answer.
        Assert.Equal(2, report.Legs.Count);
        Assert.All(
            report.Legs,
            leg => Assert.Equal(ToolState.Installed, Assert.Single(leg.Tools, tool => tool.Tool == "ninja").State));

        var installs = fixture.Host.Calls
            .Where(call => call.Program == "sudo" && call.Arguments.Contains("apt-get"))
            .ToDictionary(call => call.Host.ToString(), call => call.StandardInput, StringComparer.Ordinal);

        Assert.Equal(First + "\n", installs[$"wsl {Distro}"]);
        Assert.Equal(Second + "\n", installs[$"wsl {OtherDistro}"]);

        // Each host was asked on its own account rather than one answer being reused for both.
        Assert.Equal(2, fixture.Prompt.Asked.Count);
        Assert.Contains($"wsl {Distro}", fixture.Prompt.Asked);
        Assert.Contains($"wsl {OtherDistro}", fixture.Prompt.Asked);

        var checks = fixture.Host.Calls
            .Where(call => call.Program == "sudo" && call.Arguments is ["-S", "-v"])
            .ToDictionary(call => call.Host.ToString(), call => call.StandardInput, StringComparer.Ordinal);

        Assert.Equal(First + "\n", checks[$"wsl {Distro}"]);
        Assert.Equal(Second + "\n", checks[$"wsl {OtherDistro}"]);

        // Neither host ever saw the other's password, on any command at all.
        Assert.DoesNotContain(
            fixture.Host.Calls,
            call => call.Host.Name == OtherDistro && call.StandardInput.Contains(First, StringComparison.Ordinal));

        Assert.DoesNotContain(
            fixture.Host.Calls,
            call => call.Host.Name == Distro && call.StandardInput.Contains(Second, StringComparison.Ordinal));
    }

    /// <summary>
    /// A host that stopped answering is not somebody who mistyped. Told apart because the verdict is
    /// remembered for every later tool on that host, and remembering the wrong one sends the reader
    /// to check their typing while the machine is off.
    /// </summary>
    [Fact]
    public async Task APasswordCheckThatNeverFinished_IsNotReportedAsAWrongPassword()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => Credential,
            checkNeverFinishes: true);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);
        Assert.Contains("never finished", ninja.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("was not accepted", ninja.Detail ?? string.Empty, StringComparison.Ordinal);

        // Nothing was installed on the strength of a password nobody confirmed.
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    /// <summary>
    /// This machine has no item of its own and so can declare no credential at all. Before there was
    /// anyone to ask, a privileged install here could only be done by hand, or by a user whose sudo
    /// needs no password.
    /// </summary>
    [Fact]
    public async Task TheLocalMachine_CanBeAskedForAPassword_WhereNoItemCouldEverDeclareOne()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            twoLegs: true,
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => Credential,
            localPlatform: PlatformId.Linux);

        var report = await fixture.ProvisionAsync(["only-local"]);

        Assert.Equal(
            ToolState.Installed,
            Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja").State);

        Assert.Equal(["local"], fixture.Prompt.Asked);

        var install = Assert.Single(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments.Contains("apt-get"));
        Assert.Equal(HostId.Local, install.Host);
        Assert.Equal(Credential + "\n", install.StandardInput);
    }

    /// <summary>
    /// A run with nobody to ask refuses as it always has, so what a script reads still parses — and is
    /// told the remedy that suits it, which is to run as a user that needs no password at all.
    /// </summary>
    [Fact]
    public async Task WithNobodyToAsk_TheRefusalStillNamesTheEnvFile_AndOffersRunningAsRoot()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Unavailable);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);
        Assert.Contains("SUDO_PASSWORD", ninja.Detail, StringComparison.Ordinal);
        Assert.Contains($"wslDistros/{Distro}/.env", ninja.Detail, StringComparison.Ordinal);
        Assert.Contains("as root", ninja.Detail, StringComparison.Ordinal);

        // Nobody was asked, nothing was attempted in place of asking, and no flag is blamed for it.
        Assert.Empty(fixture.Prompt.Asked);
        Assert.DoesNotContain("--no-prompt", ninja.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    /// <summary>
    /// Told apart from having nobody to ask: somebody is there, and a flag said not to. Naming it is
    /// the difference between a message that explains itself and one that looks like a bug.
    /// </summary>
    [Fact]
    public async Task WhenAskingIsRefusedByTheFlag_TheRefusalNamesTheFlag()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Suppressed);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);
        Assert.Contains("--no-prompt", ninja.Detail, StringComparison.Ordinal);
        Assert.Empty(fixture.Prompt.Asked);
    }

    /// <summary>
    /// A typed password is a credential like any other, and reaches a report by exactly the same route
    /// a declared one would: a host that echoes what it was given.
    /// </summary>
    [Fact]
    public async Task APromptedPassword_ReachesNoLogOrReport_JustAsADeclaredOneDoes()
    {
        const string Typed = "typed-at-the-terminal";

        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => Typed,
            rootPassword: _ => Typed,
            installFails: $"sudo: a password was supplied: {Typed}",
            sdk: true);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);

        var rendered = ToolProvisionReports.Render(report, json: true);
        var everythingSaid = string.Join('\n', [.. rendered.Data, rendered.Message, fixture.Output.ToString(), fixture.Error.ToString()]);

        // The host's own text reached the report, and only the password was taken out of it: absences
        // alone would still hold if the detail had simply been blanked.
        Assert.Contains("a password was supplied", ninja.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(Typed, everythingSaid, StringComparison.Ordinal);
        Assert.DoesNotContain(Typed, ninja.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Host.Calls,
            call => call.Arguments.Any(argument => argument.Contains(Typed, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Ctrl+C at the prompt stops the run. Turned into a refusal for that host instead, it would carry
    /// on to the next one and ask again for an answer somebody has just declined to give.
    /// </summary>
    [Fact]
    public async Task AnInterruptedPrompt_StopsTheRun_RatherThanMovingOnToTheNextHost()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: host => host.Name == Distro
                ? throw new OperationCanceledException()
                : "never-asked-for",
            secondDistro: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ProvisionAsync());

        // The host that was interrupted is the only one anybody was asked about.
        Assert.Equal([$"wsl {Distro}"], fixture.Prompt.Asked);
    }

    [Fact]
    public async Task AToolWithNoInstall_IsAnAllowlistEntry_ReportedWhenMissingAndNeverInstalled()
    {
        var tool = new ToolConfig { Name = "clang-tidy", Why = "the static analysis gate reads its output" };
        using var fixture = new Fixture(tools: [tool]);

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "clang-tidy");
        Assert.Equal(ToolState.Missing, outcome.State);
        Assert.Contains("the static analysis gate reads its output", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("install it by hand", outcome.Detail, StringComparison.Ordinal);
        Assert.False(report.Passed);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo");

        Assert.Equal(ToolsExit.NotProvisioned, ToolProvisionReports.Render(report, json: false).ExitCode);
    }

    [Fact]
    public async Task AToolBelowItsMinimumVersion_IsUpdatedThroughItsInstall()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12.0")],
            present: new() { ["ninja"] = "1.10.0" });

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");
        Assert.Equal(ToolState.Updated, outcome.State);
        Assert.Equal("1.12.1", outcome.Version);
    }

    [Fact]
    public async Task AToolAtItsMinimumVersion_IsLeftAlone()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12.0")],
            present: new() { ["ninja"] = "1.12.0" });

        var report = await fixture.ProvisionAsync();

        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja").State);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo");
    }

    [Fact]
    public async Task AVersionThatCannotBeComparedWithTheMinimum_IsReported_RatherThanPassedOffAsCurrent()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12.0", regex: "never matches this")],
            present: new() { ["ninja"] = "1.10.0" });

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");
        Assert.Equal(ToolState.Unknown, outcome.State);
        Assert.False(report.Passed);
    }

    /// <summary>
    /// The two sides fail to parse for unrelated reasons and have unrelated remedies. One message
    /// for both sent everyone who wrote a two-component minVersion — which this parser refuses,
    /// because the field is a semantic version — to adjust a probe regex that had just worked.
    /// </summary>
    [Fact]
    public async Task AMinVersionThatCannotBeParsed_BlamesTheMinVersion_NotTheProbe()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12")],
            present: new() { ["ninja"] = "1.12.0" });

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");

        Assert.Equal(ToolState.Unknown, outcome.State);
        Assert.Contains("minVersion '1.12'", outcome.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("major.minor.patch", outcome.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("probe", outcome.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionTheProbeCouldNotRead_StillBlamesTheProbe()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12.0", regex: "never matches this")],
            present: new() { ["ninja"] = "1.10.0" });

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");

        Assert.Contains("probe", outcome.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The package manager an install runs is started where the survey found it. Measured on a Mac over ssh: brew was
    /// found in /opt/homebrew/bin, which the PATH of a command run there does not name, and the install then ran it by
    /// its name and failed with "command not found". A dry run names the command as the install would run it.
    /// </summary>
    [Fact]
    public async Task AManagerFoundOffThePath_IsRunByThePathItWasFoundAt_AndADryRunSaysSo()
    {
        var brew = $"{FakeHost.Homebrew}/brew";

        using (var dry = new Fixture(tools: [Brew("python3")], ssh: true, offPath: new() { [brew] = "4.4.0" }, sdk: true))
        {
            var would = Assert.Single(Assert.Single((await dry.ProvisionAsync(dryRun: true)).Legs).Tools, entry => entry.Tool == "python3");

            Assert.Equal(ToolState.WouldInstall, would.State);
            Assert.Equal($"would run '{brew} install python3-build'", would.Detail);
            Assert.DoesNotContain(dry.Host.Calls, call => call.Arguments is ["install", ..]);
        }

        using var fixture = new Fixture(tools: [Brew("python3")], ssh: true, offPath: new() { [brew] = "4.4.0" });

        var outcome = Assert.Single(Assert.Single((await fixture.ProvisionAsync()).Legs).Tools, entry => entry.Tool == "python3");

        Assert.Equal(ToolState.Installed, outcome.State);
        Assert.Equal(InstalledVersion, outcome.Version);
        Assert.Equal(brew, Assert.Single(fixture.Host.Calls, call => call.Arguments is ["install", "python3-build"]).Program);
    }

    /// <summary>
    /// A tool with a minimum version is current where any copy a leg finds meets it, the first that does being the one
    /// a leg starts: one behind an older copy on the PATH is named, with the copy it is started ahead of, and nothing
    /// is installed. Judged by the first copy alone, an older system copy - macOS's own python3 - kept the tool
    /// reported behind however many newer copies were installed beside it.
    /// </summary>
    [Fact]
    public async Task ACopyThatMeetsTheMinimum_BehindAnOlderOne_IsCurrent_AndNamedAheadOfIt()
    {
        var newer = $"{FakeHost.Homebrew}/python3";
        using var fixture = new Fixture(
            tools: [Brew("python3", minVersion: "1.12.0")],
            present: new() { ["python3"] = "1.10.0" },
            ssh: true,
            offPath: new() { [newer] = "1.13.0", [$"{FakeHost.Homebrew}/brew"] = "4.4.0" });

        var outcome = Assert.Single(Assert.Single((await fixture.ProvisionAsync()).Legs).Tools, entry => entry.Tool == "python3");

        Assert.Equal(ToolState.AlreadyCurrent, outcome.State);
        Assert.Equal("1.13.0", outcome.Version);
        Assert.Equal($"{newer} is 1.13.0, which a leg there starts ahead of /usr/bin/python3 (1.10.0)", outcome.Detail);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments is ["install", ..]);
    }

    /// <summary>
    /// Where no copy meets the minimum, the tool is installed, and the copy the install left is judged as a leg would
    /// find it: brew installs off the PATH, behind the older copy still on it, and that copy is the one that counts.
    /// </summary>
    [Fact]
    public async Task NoCopyMeetingTheMinimum_IsInstalled_AndTheNewCopyBehindTheOldOneCounts()
    {
        using var fixture = new Fixture(
            tools: [Brew("python3", minVersion: "1.12.0")],
            present: new() { ["python3"] = "1.10.0" },
            ssh: true,
            offPath: new() { [$"{FakeHost.Homebrew}/brew"] = "4.4.0" });

        var outcome = Assert.Single(Assert.Single((await fixture.ProvisionAsync()).Legs).Tools, entry => entry.Tool == "python3");

        Assert.Equal(ToolState.Updated, outcome.State);
        Assert.Equal(InstalledVersion, outcome.Version);
    }

    /// <summary>
    /// A copy whose version could not be read, beside one older than the minimum, leaves the tool unknown and nothing
    /// installed: it may be the one that meets it, as a leg's own survey there holds, which turns the leg away as not
    /// known to have the tool rather than as missing it.
    /// </summary>
    [Fact]
    public async Task ACopyWhoseVersionCouldNotBeRead_LeavesTheToolUnknown_AndInstallsNothing()
    {
        using var fixture = new Fixture(
            tools: [Brew("python3", minVersion: "1.12.0")],
            present: new() { ["python3"] = "1.10.0" },
            ssh: true,
            offPath: new() { [$"{FakeHost.Homebrew}/python3"] = "1.13", [$"{FakeHost.Homebrew}/brew"] = "4.4.0" });

        var outcome = Assert.Single(Assert.Single((await fixture.ProvisionAsync()).Legs).Tools, entry => entry.Tool == "python3");

        Assert.Equal(ToolState.Unknown, outcome.State);
        Assert.Equal(
            "whether a copy of 'python3' there is at least 1.12.0, the minVersion it is declared with, could not be established: "
            + $"/usr/bin/python3 is 1.10.0; {FakeHost.Homebrew}/python3: the probe regex of 'python3' matched nothing in what it "
            + "printed: python3 version 1.13",
            outcome.Detail);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments is ["install", ..]);
    }

    /// <summary>
    /// An ssh host whose shell is cmd or PowerShell is a Windows one: the SDK is not installed there with the shell
    /// script, which it has no shell to run, and the host is told how it is installed instead.
    /// </summary>
    [Theory]
    [InlineData("C:\\WINDOWS\\system32\\cmd.exe C:\\WINDOWS\\system32\\cmd.exe\r\n")]
    [InlineData("%COMSPEC%\r\n%COMSPEC%\r\n")]
    public async Task AWindowsShellsHost_IsNeverHandedTheInstallScript(string probe)
    {
        using var fixture = new Fixture(ssh: true, shellProbe: HostResults.Ok(probe));

        var dotnet = Assert.Single(Assert.Single((await fixture.ProvisionAsync()).Legs).Tools, tool => tool.Tool == "dotnet");

        Assert.Equal(ToolState.Failed, dotnet.State);
        Assert.StartsWith("the .NET 10 SDK is installed there with the Windows installer, which this command cannot run", dotnet.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sh");
    }

    /// <summary>
    /// A copy on this machine that will not start - a file that is no program for it, or may not be run - never ran,
    /// and could not say its version: the tool is unknown, as a leg's own survey holds it, and nothing is installed,
    /// rather than the whole command ending over it.
    /// </summary>
    [Fact]
    public async Task ACopyHereThatWillNotStart_LeavesTheToolUnknown_AndTheCommandGoesOn()
    {
        using var fixture = new Fixture(
            tools: [Brew("python3", minVersion: "1.12.0")],
            present: new() { ["python3"] = "1.10.0" },
            twoLegs: true,
            localPlatform: PlatformId.Linux);
        fixture.Host.WontStart = command => Path.GetFileName(command.Program) == "python3" && command.Program != "python3";

        var outcome = Assert.Single(Assert.Single((await fixture.ProvisionAsync(["only-local"])).Legs).Tools, entry => entry.Tool == "python3");

        Assert.Equal(ToolState.Unknown, outcome.State);
        Assert.Contains("'python3' would not report its version there (exit -1): it is not a program this machine can run", outcome.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments is ["install", ..]);
    }

    /// <summary>
    /// A copy an install left that cannot say its version leaves the tool unknown after it, saying the install reported
    /// success: nothing says the copy a leg would start meets the minimum, nor that it does not.
    /// </summary>
    [Fact]
    public async Task ACopyAnInstallLeft_ThatCannotSayItsVersion_LeavesTheToolUnknown()
    {
        using var fixture = new Fixture(
            tools: [Brew("python3", minVersion: "1.12.0")],
            present: new() { ["python3"] = "1.10.0" },
            ssh: true,
            offPath: new() { [$"{FakeHost.Homebrew}/brew"] = "4.4.0" },
            installed: "1.13");

        var outcome = Assert.Single(Assert.Single((await fixture.ProvisionAsync()).Legs).Tools, entry => entry.Tool == "python3");

        Assert.Equal(ToolState.Unknown, outcome.State);
        Assert.Equal(
            "its install reported success, and whether a copy of 'python3' there is at least 1.12.0, the minVersion it is declared "
            + $"with, could not be established: /usr/bin/python3 is 1.10.0; {FakeHost.Homebrew}/python3: the probe regex of 'python3' "
            + "matched nothing in what it printed: python3 version 1.13",
            outcome.Detail);
    }

    /// <summary>Every copy a leg could find is named, with what it is, where none meets the minimum.</summary>
    [Fact]
    public async Task NoCopyMeetingTheMinimum_NamesEveryCopy_WhereNothingDeclaresAnInstall()
    {
        using var fixture = new Fixture(
            tools: [new ToolConfig { Name = "python3", Probe = new ToolProbe { Regex = @"(\d+\.\d+\.\d+)" }, MinVersion = "1.12.0" }],
            present: new() { ["python3"] = "1.10.0" },
            ssh: true,
            offPath: new() { [$"{FakeHost.Homebrew}/python3"] = "1.11.0" });

        var outcome = Assert.Single(Assert.Single((await fixture.ProvisionAsync()).Legs).Tools, entry => entry.Tool == "python3");

        Assert.Equal(ToolState.Outdated, outcome.State);
        Assert.Equal(
            $"no copy of 'python3' there is at least 1.12.0, the minVersion it is declared with: /usr/bin/python3 is 1.10.0; "
            + $"{FakeHost.Homebrew}/python3 is 1.11.0; it declares no install, so update it there by hand",
            outcome.Detail);
    }

    /// <summary>
    /// What a tool printed over ssh is quoted with every address the host's name resolved to written as the
    /// host's item declares it, as everything a host prints is: here, the version a tool with no minimum is asked for.
    /// </summary>
    [Fact]
    public async Task WhatAToolPrintedOverSsh_NamesTheHostAsDeclared_NeverByAnAddressItsNameResolvedTo()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", regex: "never matches this")],
            present: new() { ["ninja"] = "1.10.0 built on 192.0.2.10" },
            ssh: true,
            sshAddress: ResolvedName);

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");

        Assert.Equal(
            $"the probe regex of 'ninja' matched nothing in what it printed: ninja version 1.10.0 built on {ResolvedName}",
            outcome.Detail);
    }

    /// <summary>
    /// A tool needed only on one platform is not probed on the others, so a repository declaring
    /// both a Windows compiler and a POSIX one can have every leg provisioned. Before this, each
    /// was reported missing on the other's hosts and no leg was ever green.
    /// </summary>
    [Fact]
    public async Task AToolScopedToAnotherPlatform_IsNeitherProbedNorCountedAgainstTheLeg()
    {
        using var fixture = new Fixture(
            tools:
            [
                new ToolConfig { Name = "cl", Platforms = ["windows"] },
                Apt("ninja"),
            ],
            present: new() { ["ninja"] = "1.12.0" });

        var report = await fixture.ProvisionAsync();
        var tools = Assert.Single(report.Legs).Tools;

        Assert.DoesNotContain(tools, entry => entry.Tool == "cl");
        Assert.True(report.Passed, "a tool this platform does not need must not hold the leg back");
    }

    [Fact]
    public async Task AToolScopedToThisPlatform_IsStillProbed()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", platforms: ["linux"])],
            present: new() { ["ninja"] = "1.12.0" });

        var report = await fixture.ProvisionAsync();

        Assert.Contains(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");
    }

    /// <summary>
    /// Two legs on one host share what is installed there and differ in what they need: each is told
    /// only about the tools it needs, whichever scope narrows them, and each tool is asked about
    /// there once. Scoped to the platform alone, a compiler one leg needs was reported missing on
    /// every other leg of that operating system.
    /// </summary>
    [Fact]
    public async Task LegsSharingAHost_AreEachToldOnlyWhatTheyNeed_AndEachToolIsAskedOnce()
    {
        using var fixture = new Fixture(
            tools:
            [
                Apt("ninja"),
                Apt("gcc", toolchains: ["gcc"]),
                Apt("cross", processors: ["arm64"]),
                Apt("qemu", emulators: ["qemu-arm64"]),
                Apt("probe", legs: ["on-distro"]),
            ],
            present: new() { ["ninja"] = "1.12.0", ["gcc"] = "13.2.0", ["cross"] = "2.0.0", ["qemu"] = "8.2.0", ["probe"] = "1.0.0" },
            configure: config => config.Legs["arm-gcc"] = new LegConfig
            {
                Os = "linux",
                Processor = "arm64",
                Config = "debug",
                Toolchain = "gcc",
                Emulator = "qemu-arm64",
                Wsl = Distro,
            });

        var report = await fixture.ProvisionAsync();

        IReadOnlyList<string> Declared(string leg)
            => [.. Assert.Single(report.Legs, entry => entry.Leg == leg).Tools.Select(tool => tool.Tool).Where(tool => tool != "dotnet")];

        Assert.Equal(["ninja", "probe"], Declared("on-distro"));
        Assert.Equal(["ninja", "gcc", "cross", "qemu"], Declared("arm-gcc"));
        Assert.True(report.Passed);

        foreach (var tool in new[] { "ninja", "gcc", "cross", "qemu", "probe" })
        {
            Assert.Single(fixture.Host.Calls, call => Path.GetFileName(call.Program) == tool && call.Arguments is ["--version"]);
        }
    }

    /// <summary>
    /// A tool no selected leg on a host needs is never asked about there: scoped to a leg --legs left
    /// out, it is neither probed nor installed.
    /// </summary>
    [Fact]
    public async Task AToolNoSelectedLegNeeds_IsNeverAskedAbout()
    {
        using var fixture = new Fixture(tools: [Apt("ninja"), Apt("probe", legs: ["only-local"])], present: new() { ["ninja"] = "1.12.0" }, twoLegs: true);

        var report = await fixture.ProvisionAsync(["on-distro"]);

        Assert.DoesNotContain(Assert.Single(report.Legs).Tools, tool => tool.Tool == "probe");
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Any(argument => argument.Contains("probe", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A dry run reaches the host and asks it what a run asks, and installs nothing there: each tool
    /// it would install or update names the command that would, sudo and all, and nobody is asked for
    /// a password - or whether one is needed.
    /// </summary>
    [Fact]
    public async Task ADryRun_NamesWhatItWouldInstall_AndRunsNothingThere()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja"), Apt("cmake", minVersion: "3.20.0")],
            present: new() { ["cmake"] = "3.16.0" },
            prompting: PromptAvailability.Available,
            typed: _ => Credential,
            sdk: true);

        var report = await fixture.ProvisionAsync(dryRun: true);
        var tools = Assert.Single(report.Legs).Tools;

        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(tools, tool => tool.Tool == "dotnet").State);

        var ninja = Assert.Single(tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.WouldInstall, ninja.State);
        Assert.Equal("would run 'sudo apt-get install -y ninja-build'", ninja.Detail);

        var cmake = Assert.Single(tools, tool => tool.Tool == "cmake");
        Assert.Equal(ToolState.WouldUpdate, cmake.State);
        Assert.Equal("3.16.0", cmake.Version);

        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo" || (call.Program == "sh" && call.Arguments.Count == 0));
        Assert.Empty(fixture.Prompt.Asked);
        Assert.False(fixture.Host.Has(HostId.Wsl(Distro), "ninja"));

        Assert.True(report.DryRun);
        Assert.False(report.Passed);

        var table = ToolProvisionReports.Render(report, json: false);
        var document = ToolProvisionReports.Render(report, json: true);

        Assert.Equal(ToolsExit.NotProvisioned, table.ExitCode);
        Assert.EndsWith("--dry-run installed nothing", table.Message, StringComparison.Ordinal);
        Assert.Contains(table.Details!, line => line.EndsWith("ninja would install: would run 'sudo apt-get install -y ninja-build'", StringComparison.Ordinal));

        using var parsed = System.Text.Json.JsonDocument.Parse(Assert.Single(document.Data!));
        Assert.True(parsed.RootElement.GetProperty("dryRun").GetBoolean());
    }

    /// <summary>
    /// dotnet-install.sh uses bash features that dash does not have, and dash is /bin/sh on Debian
    /// and Ubuntu. Handed to sh it fails with "Illegal option -o pipefail", which says nothing about
    /// a shell and sends the reader after the SDK instead.
    /// </summary>
    [Fact]
    public async Task TheSdkInstaller_HandsTheScriptToBash_NotToBinSh()
    {
        using var fixture = new Fixture();

        _ = await fixture.ProvisionAsync();

        var script = Assert.Single(fixture.Host.Calls, call => call.Program == "sh" && call.Arguments.Count == 0);

        Assert.Contains("bash \"$script\"", script.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain("\nsh \"$script\"", script.StandardInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSdkInstaller_RefusesOnAHostWithNoBash_SayingWhatToInstall()
    {
        using var fixture = new Fixture();

        _ = await fixture.ProvisionAsync();

        var script = Assert.Single(fixture.Host.Calls, call => call.Program == "sh" && call.Arguments.Count == 0);

        // The guard runs before anything is downloaded, and quotes back what to install.
        Assert.Contains("command -v bash", script.StandardInput, StringComparison.Ordinal);
        Assert.Contains(ToolProvisionService.BashMissing, script.StandardInput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that answered <c>uname</c> with a system this build has no name for is some POSIX
    /// machine, not a Windows one. Guessing Windows there would skip every tool scoped to Linux and
    /// then report the leg as having everything it needs.
    /// </summary>
    [Fact]
    public async Task AHostWhoseSystemCouldNotBeNamed_StillHasEveryToolChecked()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", platforms: ["linux"])],
            present: new() { ["ninja"] = "1.12.0" },
            kernel: "Minix");

        var report = await fixture.ProvisionAsync();

        Assert.Contains(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");
    }

    /// <summary>
    /// An ssh host that did not answer <c>uname</c> at all said nothing about what it is. Read as
    /// Windows, it would have been asked about no tool scoped to Linux and reported as having
    /// everything it needs.
    /// </summary>
    [Fact]
    public async Task AHostThatDidNotAnswerWhatItIs_StillHasEveryToolChecked()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", platforms: ["linux"])],
            present: new() { ["ninja"] = "1.12.0" },
            kernel: null,
            ssh: true);

        var report = await fixture.ProvisionAsync();

        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja").State);
    }

    /// <summary>
    /// A host whose transport stops starting part way through is named as unreachable, and the other
    /// hosts are still provisioned: ended there, the command lost what it had done everywhere else.
    /// </summary>
    [Fact]
    public async Task AHostWhoseTransportStopsStarting_IsNamed_AndTheOtherHostsAreStillProvisioned()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            present: new() { ["ninja"] = "1.12.0" },
            secondDistro: true,
            transportStops: (host, command) => host.Equals(HostId.Wsl(Distro)) && command.Program == "sh" && command.Arguments.Count == 0);

        var report = await fixture.ProvisionAsync();

        Assert.Equal(
            "'wsl' could not be started: The file cannot be accessed by the system.",
            Assert.Single(report.Legs, leg => leg.Leg == "on-distro").Unreachable);
        Assert.Null(Assert.Single(report.Legs, leg => leg.Leg == "on-other").Unreachable);
    }

    [Fact]
    public void RepositoryPath_IsRequiredOfEveryRemoteHost()
    {
        // R37: a WSL distribution and an ssh host each have to say where their copy of the repository
        // is, so nothing falls back to a directory nobody chose. Confirmed rather than assumed.
        foreach (var type in new[] { typeof(WslHostConfig), typeof(SshHostConfig) })
        {
            var property = type.GetProperty(nameof(RemoteHostConfig.RepositoryPath));

            Assert.NotNull(property);
            Assert.Equal(typeof(string), property.PropertyType);
            Assert.NotEmpty(property.GetCustomAttributes(typeof(RequiredMemberAttribute), inherit: true));
        }

        Assert.Null(typeof(LocalHostConfig).GetProperty(nameof(RemoteHostConfig.RepositoryPath)));
    }

    /// <summary>
    /// A tool a leg's developer environment provides - cl, where the leg builds with msvc - is looked
    /// for on the PATH that environment sets up, as the run finds it, and never reported missing for
    /// being off the PATH a plain shell has.
    /// </summary>
    [Fact]
    public async Task AToolTheLegsDeveloperEnvironmentProvides_IsNotReportedMissing()
    {
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cl");
        using var here = new VisualStudioHere(visualStudio, [new ToolConfig { Name = "cl", Toolchains = ["msvc"] }]);

        var report = await here.ProvisionAsync();

        var leg = Assert.Single(report.Legs);
        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(leg.Tools).State);
        Assert.True(report.Passed);
        Assert.Single(visualStudio.Captures);
    }

    /// <summary>
    /// Two legs on one host are each told about a tool as the PATH they start it on holds it: CMake,
    /// carried by Visual Studio alone, is there for the leg in its developer environment and missing
    /// for the one that builds from a plain shell.
    /// </summary>
    [Fact]
    public async Task EachLegOnAHost_IsToldAboutATool_AsThePathItStartsItOnHoldsIt()
    {
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cmake");
        using var here = new VisualStudioHere(visualStudio, [new ToolConfig { Name = "cmake" }], plainLeg: true);

        var report = await here.ProvisionAsync();

        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(report.Legs.Single(leg => leg.Leg == "native").Tools).State);
        Assert.Equal(ToolState.Missing, Assert.Single(report.Legs.Single(leg => leg.Leg == "mingw").Tools).State);
        Assert.False(report.Passed);
    }

    /// <summary>
    /// A tool's install runs once on a host, whichever of its PATHs asked for it first, and each PATH
    /// then looks again with the environment set up afresh. Where Visual Studio's own older copy still
    /// comes first on the PATH its environment sets up, the leg starting it there is told so, since
    /// that copy is the one it starts.
    /// </summary>
    [Fact]
    public async Task AnInstallTwoPathsNeed_RunsOnce_AndEachPathLooksAgain()
    {
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "ninja");
        var ninja = new ToolConfig
        {
            Name = "ninja",
            Probe = new ToolProbe { Args = ["--version"] },
            MinVersion = "1.12.0",
            Install = { ["all"] = new ToolInstall { Command = [VisualStudioHere.InstallNinja] } },
        };

        using var here = new VisualStudioHere(visualStudio, [ninja], plainLeg: true);

        var report = await here.ProvisionAsync();

        var plain = Assert.Single(report.Legs.Single(leg => leg.Leg == "mingw").Tools);
        Assert.Equal(ToolState.Installed, plain.State);
        Assert.Equal(VisualStudioHere.InstalledNinja, plain.Version);

        var native = Assert.Single(report.Legs.Single(leg => leg.Leg == "native").Tools);
        Assert.Equal(ToolState.Failed, native.State);
        Assert.StartsWith(
            "its install reported success, and no copy of 'ninja' there is at least 1.12.0, the minVersion it is declared with: ",
            native.Detail,
            StringComparison.Ordinal);
        Assert.EndsWith($" is {VisualStudioHere.StudioNinja}", native.Detail, StringComparison.Ordinal);

        Assert.Equal(1, here.Installs);
        Assert.Equal(2, visualStudio.Captures.Count);
    }

    /// <summary>
    /// A tool looked for again after its install, where the PATH its leg starts it on can no longer
    /// be looked at - Visual Studio's environment will not set up the second time, and the install put
    /// the tool nowhere else - is unknown, saying the install reported success and why nobody can say
    /// more: never failed on a guess, nor passed as installed.
    /// </summary>
    [Fact]
    public async Task ATool_WhoseEnvironmentWillNotSetUpWhenLookedAtAgain_IsUnknown_AfterItsInstall()
    {
        const string Full = "There is not enough space on the disk.";

        using var visualStudio = new ScriptedVisualStudio
        {
            Holding = (capture, _) => capture == 1 ? throw new IOException(Full) : Task.CompletedTask,
        };
        var ninja = new ToolConfig
        {
            Name = "ninja",
            Install = { ["all"] = new ToolInstall { Command = [VisualStudioHere.InstallNinjaIntoVisualStudio] } },
        };

        using var here = new VisualStudioHere(visualStudio, [ninja]);

        var report = await here.ProvisionAsync();

        var tool = Assert.Single(Assert.Single(report.Legs).Tools);
        Assert.Equal(ToolState.Unknown, tool.State);
        Assert.StartsWith(
            "its install reported success, and whether 'ninja' is there could not be established: the PATH developer environment "
            + "'vs' sets up, where the legs that need it start it, could not be set up: ",
            tool.Detail,
            StringComparison.Ordinal);
        Assert.EndsWith(Full, tool.Detail, StringComparison.Ordinal);
        Assert.Equal(1, here.Installs);
        Assert.Equal(2, visualStudio.Captures.Count);
    }

    /// <summary>
    /// A developer environment that will not set up leaves a tool unknown, saying why, rather than missing or found on the
    /// host's own PATH: the PATH its legs would find it on could not be looked at.
    /// </summary>
    [Fact]
    public async Task AToolOffThePath_WhereTheDeveloperEnvironmentWillNotSetUp_IsUnknown_SayingWhy()
    {
        using var visualStudio = new ScriptedVisualStudio { ExitCode = "1" }.Carrying("x64", "cl");
        using var here = new VisualStudioHere(visualStudio, [new ToolConfig { Name = "cl", Toolchains = ["msvc"] }]);

        var report = await here.ProvisionAsync();

        var cl = Assert.Single(Assert.Single(report.Legs).Tools);
        Assert.Equal(ToolState.Unknown, cl.State);
        Assert.Equal(
            "the PATH developer environment 'vs' sets up, where the legs that need it start it, "
            + "could not be set up: developer environment 'vs': vcvarsall.bat amd64 exited 1 and printed nothing",
            cl.Detail);
    }

    /// <summary>
    /// A developer environment whose instance could not be looked for leaves a tool unknown too, saying why: whether the
    /// PATH it would set up holds the tool is not known either way.
    /// </summary>
    [Fact]
    public async Task AToolWhereTheDeveloperEnvironmentCouldNotBeLookedFor_IsUnknown_SayingWhy()
    {
        using var visualStudio = new ScriptedVisualStudio { VsWhereExitCode = 1 };
        using var here = new VisualStudioHere(visualStudio, [new ToolConfig { Name = "cl", Toolchains = ["msvc"] }]);

        var cl = Assert.Single(Assert.Single((await here.ProvisionAsync()).Legs).Tools);

        Assert.Equal(ToolState.Unknown, cl.State);
        Assert.StartsWith(
            "the PATH developer environment 'vs' sets up, where the legs that need it start it, could not be looked at: ",
            cl.Detail,
            StringComparison.Ordinal);
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>
    /// Where no instance of the developer environment is installed, the host's own PATH is all its legs
    /// could find a tool on, so a tool missing there is missing - and installing it is what would help.
    /// </summary>
    [Fact]
    public async Task AToolOffThePath_WhereNoInstanceIsInstalled_IsMissing()
    {
        using var visualStudio = new ScriptedVisualStudio { Instances = "[]" };
        using var here = new VisualStudioHere(visualStudio, [new ToolConfig { Name = "cl", Toolchains = ["msvc"] }]);

        var report = await here.ProvisionAsync();

        Assert.Equal(ToolState.Missing, Assert.Single(Assert.Single(report.Legs).Tools).State);
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>
    /// A leg on another host that starts in a developer environment finds its tools on the PATH that environment sets up
    /// there, which that host's own survey is asked to set up and look on, for the leg's processor: a tool below its
    /// minimum there is one an install settles, and one the environment would not show is unknown, saying why, and never
    /// installed a second time.
    /// </summary>
    [Fact]
    public async Task AToolOnAnotherHost_ForALegInADeveloperEnvironment_IsJudgedOnThePathItSetsUpThere()
    {
        void InVisualStudio(HarnessConfig config)
        {
            config.DeveloperEnvironments["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio };
            config.Toolchains["msvc"] = new ToolchainConfig { DeveloperEnvironment = "vs" };
            config.Legs["on-distro"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = Distro, Toolchain = "msvc" };
        }

        const string Below = "no copy of 'cmake' there is at least 3.28.0, the minVersion it is declared with: /vs/bin/cmake is 3.20.0";
        using var fixture = new Fixture(
            tools: [Apt("cl"), Apt("cmake", minVersion: "3.28.0")],
            present: new() { ["cmake"] = "3.31.0" },
            configure: InVisualStudio,
            sdk: true);
        fixture.Survey.Looked = (_, look) => new DeveloperEnvironmentPrograms
        {
            Name = look.Name,
            Processor = look.Processor,
            Programs =
            [
                new ProgramLocation("cl", ProgramFound.Nowhere),
                new ProgramLocation("cmake", ProgramFound.BelowFloor, "/vs/bin/cmake", Below) { Judged = new FloorCopy("/vs/bin/cmake", "3.20.0", null) },
            ],
        };

        var tools = Assert.Single((await fixture.ProvisionAsync(dryRun: true)).Legs).Tools;

        var look = Assert.Single(Assert.Single(fixture.Survey.Surveys).Looks);
        Assert.Equal(("vs", "x86_64"), (look.Name, look.Processor));
        Assert.Equal(["cl", "cmake"], look.Programs);
        Assert.Equal((ToolState.WouldInstall, "would run 'sudo apt-get install -y cl-build'"), (tools.Single(tool => tool.Tool == "cl").State, tools.Single(tool => tool.Tool == "cl").Detail));
        Assert.Equal((ToolState.WouldUpdate, "3.20.0"), (tools.Single(tool => tool.Tool == "cmake").State, tools.Single(tool => tool.Tool == "cmake").Version));

        using var unset = new Fixture(tools: [Apt("cl")], configure: InVisualStudio, sdk: true);
        unset.Survey.Looked = (_, look) => new DeveloperEnvironmentPrograms { Name = look.Name, Processor = look.Processor, Unestablished = "could not be set up: vcvarsall.bat exited 1" };

        var cl = Assert.Single(Assert.Single((await unset.ProvisionAsync()).Legs).Tools, tool => tool.Tool == "cl");

        Assert.Equal(ToolState.Unknown, cl.State);
        Assert.Equal(
            "the PATH developer environment 'vs' sets up, where the legs that need it start it, could not be set up: vcvarsall.bat exited 1",
            cl.Detail);
        Assert.DoesNotContain(unset.Host.Calls, call => call.Program == "sudo" || call.Arguments.Contains("cl-build"));
    }

    /// <summary>
    /// Two legs on one host in one developer environment, built for two processors, are each told about a tool as the
    /// PATH it sets up for their own processor holds it: one set up for one processor says nothing of the other's.
    /// </summary>
    [Fact]
    public async Task LegsInOneDeveloperEnvironment_ForTwoProcessors_AreEachToldWhatTheirOwnLookFound()
    {
        using var fixture = new Fixture(
            tools: [Apt("cmake", minVersion: "3.28.0")],
            configure: config =>
            {
                config.DeveloperEnvironments["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio };
                config.Toolchains["msvc"] = new ToolchainConfig { DeveloperEnvironment = "vs" };
                config.Legs["on-distro"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = Distro, Toolchain = "msvc" };
                config.Legs["on-distro-arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Wsl = Distro, Toolchain = "msvc" };
            },
            sdk: true);
        fixture.Survey.Looked = (_, look) => look.Processor == "x86_64"
            ? new DeveloperEnvironmentPrograms
            {
                Name = look.Name,
                Processor = look.Processor,
                Programs = [new ProgramLocation("cmake", ProgramFound.OnPath, "/vs/bin/cmake") { Judged = new FloorCopy("/vs/bin/cmake", "3.31.0", null, Meets: true) }],
            }
            : new DeveloperEnvironmentPrograms { Name = look.Name, Processor = look.Processor, Unestablished = "could not be set up: vcvarsall.bat x64_arm64 exited 1" };

        var report = await fixture.ProvisionAsync();

        Assert.Equal(["arm64", "x86_64"], Assert.Single(fixture.Survey.Surveys).Looks.Select(look => look.Processor).Order(StringComparer.Ordinal));
        var native = Assert.Single(Assert.Single(report.Legs, leg => leg.Leg == "on-distro").Tools, tool => tool.Tool == "cmake");
        var arm = Assert.Single(Assert.Single(report.Legs, leg => leg.Leg == "on-distro-arm").Tools, tool => tool.Tool == "cmake");
        Assert.Equal((ToolState.AlreadyCurrent, "3.31.0"), (native.State, native.Version));
        Assert.Equal(
            (ToolState.Unknown, "the PATH developer environment 'vs' sets up, where the legs that need it start it, could not be set up: vcvarsall.bat x64_arm64 exited 1"),
            (arm.State, arm.Detail));
    }

    /// <summary>A tool brew installs, whose package is named after it.</summary>
    private static ToolConfig Brew(string name, string? minVersion = null) => new()
    {
        Name = name,
        Probe = new ToolProbe { Args = ["--version"], Regex = @"(\d+\.\d+\.\d+)" },
        MinVersion = minVersion,
        Install = { ["all"] = new ToolInstall { Manager = "brew", Id = name + "-build" } },
    };

    /// <summary>A tool apt installs, whose package is named after it.</summary>
    private static ToolConfig Apt(
        string name,
        string? minVersion = null,
        string regex = @"(\d+\.\d+\.\d+)",
        List<string>? platforms = null,
        List<string>? toolchains = null,
        List<string>? legs = null,
        List<string>? processors = null,
        List<string>? emulators = null) => new()
    {
        Name = name,
        Probe = new ToolProbe { Args = ["--version"], Regex = regex },
        MinVersion = minVersion,
        Platforms = platforms ?? [],
        Toolchains = toolchains ?? [],
        Legs = legs ?? [],
        Processors = processors ?? [],
        Emulators = emulators ?? [],
        Install = { ["linux"] = new ToolInstall { Manager = "apt", Id = name + "-build" } },
    };

    /// <summary>
    /// This machine, with a leg that builds with msvc in Visual Studio's developer environment, and,
    /// when asked for, one beside it that builds from a plain shell. Programs are looked for as this
    /// machine looks for them, by file: nothing is on the PATH a plain shell has until an install puts
    /// it there, and Visual Studio carries what a test says it does.
    /// </summary>
    private sealed class VisualStudioHere : IDisposable
    {
        /// <summary>The command that installs ninja on this machine's own PATH.</summary>
        public const string InstallNinja = "install-ninja";

        /// <summary>The command that adds ninja to Visual Studio, as its CMake component carries it.</summary>
        public const string InstallNinjaIntoVisualStudio = "install-ninja-into-visual-studio";

        /// <summary>What the ninja an install puts on this machine's own PATH reports.</summary>
        public const string InstalledNinja = "1.12.1";

        /// <summary>What the ninja Visual Studio carries reports.</summary>
        public const string StudioNinja = "1.10.2";

        private readonly TempDirectory _repository = new();
        private readonly TempDirectory _path = new();
        private readonly TempDirectory _searched = new();
        private readonly ScriptedVisualStudio _visualStudio;
        private readonly ToolProvisionService _service;
        private int _installs;

        public VisualStudioHere(ScriptedVisualStudio visualStudio, IReadOnlyList<ToolConfig> tools, bool plainLeg = false)
        {
            _visualStudio = visualStudio;

            var config = new HarnessConfig
            {
                Tools = [.. tools],

                // Nothing searched beyond a PATH but a directory nothing is in, on any machine: the
                // built-in list would find this machine's own tools, where Linux keeps them.
                ToolSearchDirectories = new(StringComparer.OrdinalIgnoreCase) { ["all"] = [_searched.Path] },
                DeveloperEnvironments = { ["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio } },
                Toolchains =
                {
                    ["msvc"] = new ToolchainConfig { DeveloperEnvironment = "vs" },
                    ["mingw"] = new ToolchainConfig(),
                },

                // The PATH Visual Studio's environment is set up over, as this machine's own is: what an
                // install puts on the one is on the other too, behind Visual Studio's own directories.
                Hosts = new HostsConfig
                {
                    Local = new LocalHostConfig { Env = new(StringComparer.OrdinalIgnoreCase) { ["PATH"] = _path.Path } },
                },
                Legs = { ["native"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "msvc" } },
            };

            if (plainLeg)
            {
                config.Legs["mingw"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "mingw" };
            }

            var platform = new HostPlatform();
            var permissions = FilePermissionsFactory.Create();
            var fileSystem = new PhysicalFileSystem(permissions);
            var commands = new ScriptedHostCommands(Respond);
            var programs = new HostProgramResolver(new LocalProgramResolver(platform, permissions, () => _path.Path), commands);
            var connector = new HostConnector(
                platform,
                Substitute.For<IProcessRunner>(),
                commands,
                new HostSecretsStore(fileSystem, permissions, platform),
                new HostAddressResolver(Substitute.For<INameLookup>(), TimeProvider.System, TimeSpan.Zero),
                programs,
                new SshWakeWindow(Substitute.For<INameLookup>(), TimeProvider.System, TimeSpan.Zero),
                new SyncedCopyRefusals(Substitute.For<IHarnessOutput>()));

            // This machine's own survey, run in-process as it is for a leg here: Visual Studio's environment set up by
            // the agent's own provider, and each copy of ninja asked its version by its path.
            var output = new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false);
            var processes = Substitute.For<IProcessRunner>();
            processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Version(call.Arg<ProcessRequest>().FileName) is { } version
                    ? HostResults.Ok(version + "\n")
                    : throw new InvalidOperationException($"'{call.Arg<ProcessRequest>().FileName}' was not expected to be started.")));
            var identity = Substitute.For<IToolIdentityProvider>();
            identity.Current.Returns(new ToolIdentity("1.2.3", "abc123"));
            var agent = new HostAgentService(
                platform,
                identity,
                new EmulatorProbe(platform, processes, fileSystem),
                new DeveloperEnvironmentProbe(HostDoubles.Platform(PlatformId.Windows, "x86_64"), visualStudio),
                visualStudio.Provider(),
                fileSystem,
                new ProgramFloors(new LocalProgramResolver(platform, permissions, () => _path.Path), processes, platform),
                new KeepAwake(processes, output),
                new HoldAwakeStore(fileSystem, Path.Combine(TestHost.TemporaryRoot, "holds", Guid.NewGuid().ToString("N") + ".json")),
                new RecordingLauncher(),
                HomeShorthand.Of(platform, fileSystem),
                new RecordingLastResort());
            var inspector = new HostInspector(
                commands,
                connector,
                identity,
                agent,
                new HoldAwakeRegistry(commands, output, stopsWithin: TimeSpan.Zero),
                Substitute.For<IWslDiskImages>(),
                fileSystem);

            _service = new ToolProvisionService(
                HostDoubles.Loader(config, _repository.Path),
                connector,
                commands,
                programs,
                inspector,
                output,
                new FakePrompt(PromptAvailability.Unavailable));
        }

        /// <summary>
        /// What a copy of ninja reports, started by its path: Visual Studio's, under its instance, or the one an install put
        /// on this machine's own PATH; nothing for any other program.
        /// </summary>
        private string? Version(string program)
            => Path.GetFileNameWithoutExtension(program) != "ninja" ? null
                : program.StartsWith(_visualStudio.InstallationPath, StringComparison.OrdinalIgnoreCase) ? StudioNinja
                : InstalledNinja;

        /// <summary>How many times ninja's install ran.</summary>
        public int Installs => _installs;

        public Task<ToolProvisionReport> ProvisionAsync()
            => _service.ProvisionAsync(_repository.Path, null, dryRun: false, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _searched.Dispose();
            _path.Dispose();
            _repository.Dispose();
        }

        /// <summary>This machine running what provisioning asks: ninja's install, and ninja telling its version.</summary>
        private ProcessResult Respond(HostConnection connection, HostCommand command)
        {
            if (command.Program == InstallNinja)
            {
                _installs++;
                _path.WriteProgram(".", "ninja");
                return HostResults.Ok("installed\n");
            }

            if (command.Program == InstallNinjaIntoVisualStudio)
            {
                _installs++;
                _visualStudio.Carrying("x64", "ninja");
                return HostResults.Ok("installed\n");
            }

            if (Version(command.Program) is { } version)
            {
                return HostResults.Ok(version + "\n");
            }

            throw HostResults.Unexpected(command);
        }
    }

    /// <summary>One command a host was asked to run, and what it was given.</summary>
    private sealed record HostCall(HostId Host, string Program, IReadOnlyList<string> Arguments, string StandardInput);

    /// <summary>
    /// Somebody at a terminal: what they would type for each host, and every host they were asked
    /// about. A prompt that throws is how a test says they pressed Ctrl+C instead of answering.
    /// </summary>
    private sealed class FakePrompt(PromptAvailability availability, Func<HostId, string>? typed = null)
        : ISuperuserPrompt
    {
        private readonly List<string> _asked = [];

        public PromptAvailability Availability { get; } = availability;

        /// <summary>Every host somebody was asked about, in order, named as the command line names it.</summary>
        public IReadOnlyList<string> Asked => _asked;

        public Task<string> ReadPasswordAsync(HostId host, CancellationToken cancellationToken)
        {
            _asked.Add(host.ToString());

            return Task.FromResult(typed is null ? string.Empty : typed(host));
        }
    }

    /// <summary>
    /// A distribution that starts with no .NET SDK and no tools, and changes as commands are run on it,
    /// so that a second run measures what the first one actually did.
    /// </summary>
    private sealed class FakeHost(
        bool exists,
        Dictionary<string, string> present,
        Dictionary<string, string> installs,
        bool passwordlessSudo,
        string? installFails,
        string? kernel,
        Func<HostId, string> rootPassword,
        bool checkNeverFinishes,
        bool homeUnreadable,
        Func<HostId, HostCommand, bool>? transportStops,
        Dictionary<string, string> offPath,
        bool sdk)
    {
        /// <summary>Where brew is, off the PATH, and where it installs.</summary>
        public const string Homebrew = "/opt/homebrew/bin";

        /// <summary>Says which commands name a program of this machine's own that will not start.</summary>
        public Func<HostCommand, bool>? WontStart { get; set; }

        private readonly Dictionary<string, Dictionary<string, string>> _present = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _offPath = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dotnet = new(StringComparer.Ordinal);

        public List<HostCall> Calls { get; } = [];

        /// <summary>Whether one host has a program, which is not what another host has.</summary>
        public bool Has(HostId host, string program) => Present(host).ContainsKey(program);

        /// <summary>Whether one host has the .NET SDK DssHarness runs on: every host had it, or an install put it there.</summary>
        public bool HasSdk(HostId host) => sdk || _dotnet.Contains(host.ToString());

        /// <summary>
        /// Every copy of <paramref name="program"/> one host has, in the order its own survey looks - on its PATH, then off
        /// it - with the version each reports.
        /// </summary>
        public IReadOnlyList<(string Path, string Version)> CopiesOf(HostId host, string program)
            =>
            [
                .. Present(host).TryGetValue(program, out var onPath) ? [($"/usr/bin/{program}", onPath)] : Array.Empty<(string, string)>(),
                .. OffPath(host).Where(entry => entry.Key.Split('/')[^1] == program).Select(entry => (entry.Key, entry.Value)),
            ];

        /// <summary>What one copy prints when its survey starts it to ask its version, or why it would not start.</summary>
        public ProcessResult Answer(HostId host, string copy, IReadOnlyList<string> arguments)
        {
            if (WontStart?.Invoke(new HostCommand { Program = copy, Arguments = arguments }) == true)
            {
                return ProcessResult.NotRun(new ProgramStartException(copy, "it is not a program this machine can run").Message);
            }

            var name = copy.Split('/')[^1];

            return CopiesOf(host, name).FirstOrDefault(found => found.Path == copy) is { Path: not null } found
                ? HostResults.Ok($"{name} version {found.Version}\n")
                : HostResults.Failed(127, $"{copy}: not found\n");
        }

        /// <summary>
        /// The files one host has off its PATH, by path, each the version it reports; what every host was said to
        /// have, and what brew installed there since.
        /// </summary>
        private Dictionary<string, string> OffPath(HostId host)
        {
            if (!_offPath.TryGetValue(host.ToString(), out var found))
            {
                found = new Dictionary<string, string>(offPath, StringComparer.Ordinal);
                _offPath[host.ToString()] = found;
            }

            return found;
        }

        /// <summary>Whether <paramref name="path"/> is a file one host has: on its PATH, or off it.</summary>
        private bool Exists(HostId host, string path, bool dotnet)
            => (dotnet && path == $"{Home}/.dotnet/dotnet")
                || OffPath(host).ContainsKey(path)
                || (path.StartsWith("/usr/bin/", StringComparison.Ordinal) && Has(host, path["/usr/bin/".Length..]));

        public ProcessResult Respond(HostConnection connection, HostCommand command)
        {
            var host = connection.Host;
            Calls.Add(new HostCall(host, command.Program, command.Arguments, command.StandardInput.Read()));

            if (transportStops?.Invoke(host, command) == true)
            {
                throw HostResults.TransportWouldNotStart(host);
            }

            if (WontStart?.Invoke(command) == true)
            {
                throw new ProgramStartException(command.Program, "it is not a program this machine can run");
            }

            if (!exists && host.Kind != HostKind.Local)
            {
                return HostResults.Failed(1, "Wsl/Service/WSL_E_DISTRO_NOT_FOUND");
            }

            if (Lookup(command) is { } name)
            {
                return Has(host, name) ? HostResults.Ok($"/usr/bin/{name}\n") : HostResults.Failed(1, string.Empty);
            }

            var dotnetPath = $"{Home}/.dotnet/dotnet";
            var dotnet = HasSdk(host);

            return (command.Program, command.Arguments.FirstOrDefault()) switch
            {
                ("uname", _) => kernel is null
                    ? HostResults.Failed(HostProbes.SshFailed, "Connection closed by remote host")
                    : HostResults.Ok($"{kernel} x86_64\n"),
                ("pwd", _) => homeUnreadable ? HostResults.Failed(1, "pwd: cannot read the current directory") : HostResults.Ok(Home + "\n"),
                ("ls", _) => HostResults.Ok(string.Join('\n', command.Arguments.Where(path => Exists(host, path, dotnet))) + "\n"),
                ("printenv", "PATH") => HostResults.Ok("/usr/bin:/bin\n"),
                ("sh", null) => Install(host, dotnet: true),

                // Matched ahead of the install below, which is every other way sudo is reached: this is
                // the one that only checks a password and installs nothing.
                ("sudo", "-S") when command.Arguments is ["-S", "-v"] => checkNeverFinishes
                    ? new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromMinutes(2), TimedOut: true)
                    : string.Equals(command.StandardInput.Read(), rootPassword(host) + "\n", StringComparison.Ordinal)
                        ? HostResults.Ok(string.Empty)
                        : HostResults.Failed(1, $"sudo: a password is required: {command.StandardInput.Read().TrimEnd('\n')}"),
                ("sudo", "-n") when command.Arguments.Count == 2 => passwordlessSudo
                    ? HostResults.Ok(string.Empty)
                    : HostResults.Failed(1, "sudo: a password is required"),
                ("sudo", _) => Install(host, dotnet: false, command),
                _ when command.Program == dotnetPath && command.Arguments.FirstOrDefault() == "--list-sdks" =>
                    dotnet ? HostResults.Ok($"10.0.100 [{Home}/.dotnet/sdk]\n") : HostResults.Failed(1, "not found"),
                _ when command.Program == $"{Homebrew}/brew" && OffPath(host).ContainsKey(command.Program) && command.Arguments is ["install", var id] =>
                    Brew(host, id),
                _ when OffPath(host).TryGetValue(command.Program, out var version) =>
                    HostResults.Ok($"{Path.GetFileName(command.Program)} version {version}\n"),
                _ when Present(host).TryGetValue(Path.GetFileName(command.Program), out var version) =>
                    HostResults.Ok($"{Path.GetFileName(command.Program)} version {version}\n"),
                _ => throw HostResults.Unexpected(command),
            };
        }

        /// <summary>What one host has, starting from whatever every host was said to have.</summary>
        private Dictionary<string, string> Present(HostId host)
        {
            if (!_present.TryGetValue(host.ToString(), out var found))
            {
                found = new Dictionary<string, string>(present, StringComparer.Ordinal);
                _present[host.ToString()] = found;
            }

            return found;
        }

        /// <summary>
        /// The program a PATH lookup asked about: a distribution is asked through a shell, and an ssh
        /// host with its shell's own builtin.
        /// </summary>
        private static string? Lookup(HostCommand command) => (command.Program, command.Arguments.FirstOrDefault()) switch
        {
            ("sh", "-c") => command.Arguments[1].Split(' ')[^1],
            ("command", "-v") => command.Arguments[1],
            ("where" or "where.exe", not null) => command.Arguments[0],
            _ => null,
        };

        /// <summary>Brew installing <paramref name="package"/> where it installs everything: its own directory, off the PATH.</summary>
        private ProcessResult Brew(HostId host, string package)
        {
            if (installFails is { } said)
            {
                return HostResults.Failed(1, said);
            }

            var name = package[..^"-build".Length];
            OffPath(host)[$"{Homebrew}/{name}"] = installs[name];

            return HostResults.Ok($"==> Pouring {package}\n");
        }

        private ProcessResult Install(HostId host, bool dotnet, HostCommand? command = null)
        {
            if (installFails is { } said)
            {
                return HostResults.Failed(1, said);
            }

            if (dotnet)
            {
                _dotnet.Add(host.ToString());
                return HostResults.Ok("dotnet-install: Installation finished.\n");
            }

            // The package id ends in -build; the program it installs is the tool's own name.
            var package = command!.Arguments[^1];
            var name = package[..^"-build".Length];
            Present(host)[name] = installs[name];

            return HostResults.Ok($"Setting up {package}\n");
        }
    }

    /// <summary>
    /// Each host's own survey, answered from what the fake host has: a program where it is - on the host's PATH, or off it -
    /// and a declared tool with a minimum version held to it as the host's own survey holds one, its copies asked in that
    /// order until one meets it, which is the copy a leg starts, ahead of an older one where one comes first. Every survey
    /// is recorded, and a test can script what a host answers.
    /// </summary>
    /// <param name="host">What each host has.</param>
    /// <param name="hereFindsEverything">Whether this machine finds every program, as a test about a distribution has it do.</param>
    /// <param name="here">This machine's platform, whose floors its own survey holds; every other host is Linux.</param>
    /// <remarks>
    /// A host reached through a transport answers only once it has the .NET SDK, as the real survey runs there only on
    /// DssHarness, which needs one.
    /// </remarks>
    private sealed class FakeSurvey(FakeHost host, bool hereFindsEverything, string here) : IHostInspector
    {
        /// <summary>Why a host with no SDK is not surveyed.</summary>
        public const string NoSdk = "the .NET 10 SDK is not installed there";

        private readonly List<(HostId Host, IReadOnlyList<string> Programs, IReadOnlyList<DeveloperEnvironmentLook> Looks)> _surveys = [];

        /// <summary>Every survey, in order: the host, the programs it was asked to find, and where it was asked to look.</summary>
        public IReadOnlyList<(HostId Host, IReadOnlyList<string> Programs, IReadOnlyList<DeveloperEnvironmentLook> Looks)> Surveys => _surveys;

        /// <summary>What a host answers for one program, where a test scripts it; unscripted, what the host has.</summary>
        public Func<HostId, string, ProgramLocation?>? Scripted { get; set; }

        /// <summary>Why a host's survey could not be run, where a test says it could not.</summary>
        public Func<HostId, string?>? Fails { get; set; }

        /// <summary>
        /// What looking in a developer environment found on a host, where a test scripts it; unscripted, the programs as the
        /// host's own PATH has them, as on a host with no instance of it.
        /// </summary>
        public Func<HostId, DeveloperEnvironmentLook, DeveloperEnvironmentPrograms?>? Looked { get; set; }

        public async Task<HostReport> InspectAsync(
            HarnessContext context,
            HostId inspected,
            IReadOnlyDictionary<string, EmulatorConfig> emulators,
            IReadOnlyDictionary<string, DeveloperEnvironmentConfig> developerEnvironments,
            IReadOnlyList<string> programs,
            RoomQuestions? room = null,
            IReadOnlyList<DeveloperEnvironmentLook>? inDeveloperEnvironments = null,
            CancellationToken cancellationToken = default)
        {
            var looks = inDeveloperEnvironments ?? [];
            _surveys.Add((inspected, programs, looks));

            if ((inspected.Kind != HostKind.Local && !host.HasSdk(inspected) ? NoSdk : Fails?.Invoke(inspected)) is { } why)
            {
                return new HostReport { Host = inspected, Reason = why };
            }

            var platformKey = inspected.Kind == HostKind.Local ? here : PlatformNames.Linux;
            var floors = ToolFloor.Of(context.Config.Tools).Where(floor => floor.AppliesOn(platformKey)).ToList();
            var found = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal);

            foreach (var program in programs.Distinct(StringComparer.Ordinal))
            {
                found[program] = Scripted?.Invoke(inspected, program)
                    ?? await LocatedAsync(inspected, program, floors.FirstOrDefault(floor => floor.Name == program), cancellationToken);
            }

            return new HostReport
            {
                Host = inspected,
                Os = "linux",
                Processor = "x86_64",
                Programs = found,
                InDeveloperEnvironments =
                [
                    .. looks.Select(look => Looked?.Invoke(inspected, look) ?? new DeveloperEnvironmentPrograms
                    {
                        Name = look.Name,
                        Processor = look.Processor,
                        Programs = [.. look.Programs.Select(program => found[program])],
                    }),
                ],
            };
        }

        /// <summary>Where a leg on <paramref name="inspected"/> finds <paramref name="program"/>, held to <paramref name="floor"/> where it has one.</summary>
        private async Task<ProgramLocation> LocatedAsync(HostId inspected, string program, ToolFloor? floor, CancellationToken cancellationToken)
        {
            IReadOnlyList<(string Path, string Version)> copies = hereFindsEverything && inspected.Kind == HostKind.Local
                ? [($"/usr/bin/{program}", InstalledVersion)]
                : host.CopiesOf(inspected, program);

            if (copies.Count == 0)
            {
                return new ProgramLocation(program, ProgramFound.Nowhere);
            }

            if (floor is null)
            {
                return Where(program, copies[0].Path);
            }

            var search = await floor.SearchAsync(
                new ProgramCopies([.. copies.Select(copy => copy.Path)]),
                (copy, _) => Task.FromResult(host.Answer(inspected, copy, floor.Args)),
                cancellationToken);
            var first = search.Probed[0];

            return search.Chosen is { } chosen
                ? Where(program, chosen.Copy) with { Judged = chosen, PassedOver = chosen == first ? null : first }
                : new ProgramLocation(
                    program,
                    search.Below ? ProgramFound.BelowFloor : ProgramFound.FloorUnknown,
                    first.Copy,
                    search.Below ? search.Refusal() : search.Unestablished()) { Judged = first };
        }

        private static ProgramLocation Where(string program, string path)
            => new(program, path.StartsWith("/usr/bin/", StringComparison.Ordinal) ? ProgramFound.OnPath : ProgramFound.OffPath, path);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _repository = new();
        private readonly ToolProvisionService _service;

        public Fixture(
            IReadOnlyList<ToolConfig>? tools = null,
            Dictionary<string, string>? present = null,
            bool twoLegs = false,
            bool distributionExists = true,
            string? credential = Credential,
            bool passwordlessSudo = false,
            string? installFails = null,
            string? kernel = "Linux",
            PromptAvailability prompting = PromptAvailability.Unavailable,
            Func<HostId, string>? typed = null,
            PlatformId localPlatform = PlatformId.Windows,
            bool secondDistro = false,
            Func<HostId, string>? rootPassword = null,
            bool checkNeverFinishes = false,
            Dictionary<string, List<string>>? searchDirectories = null,
            bool homeUnreadable = false,
            bool ssh = false,
            Func<HostId, HostCommand, bool>? transportStops = null,
            Action<HarnessConfig>? configure = null,
            string sshAddress = "192.0.2.10",
            Dictionary<string, string>? offPath = null,
            ProcessResult? shellProbe = null,
            string installed = InstalledVersion,
            bool sdk = false)
        {
            var declared = tools ?? [];

            Prompt = new FakePrompt(prompting, typed);

            Host = new FakeHost(
                distributionExists,
                present ?? [],
                declared.ToDictionary(tool => tool.Name, _ => installed, StringComparer.Ordinal),
                passwordlessSudo,
                installFails,
                kernel,
                rootPassword ?? (_ => Credential),
                checkNeverFinishes,
                homeUnreadable,
                transportStops,
                offPath ?? [],
                sdk);

            _repository.WriteFile(
                Path.Combine(".harness-config", "wslDistros", Distro, ".env"),
                $"DISTRO=Example-Linux\n{(credential is null ? string.Empty : $"SUDO_PASSWORD={credential}\n")}");

            if (ssh)
            {
                _repository.WriteFile(Path.Combine(".harness-config", "sshItems", SshName, ".env"), $"ADDRESS={sshAddress}\nUSER=harness\n");
                _repository.WriteFile(Path.Combine(".harness-config", "sshItems", SshName, ".key"), "not a real key");
                _repository.WriteFile(Path.Combine(".harness-config", "sshItems", SshName, "known_hosts"), "192.0.2.10 ssh-ed25519 AAAA\n");
            }

            if (secondDistro)
            {
                // Declares no credential of its own, so it reaches the prompt exactly as the first does.
                _repository.WriteFile(
                    Path.Combine(".harness-config", "wslDistros", OtherDistro, ".env"),
                    "DISTRO=Other-Linux\n");
            }

            var config = new HarnessConfig
            {
                WslDistros = { Distro },
                Tools = [.. declared],
                ToolSearchDirectories = new(searchDirectories ?? [], StringComparer.OrdinalIgnoreCase),
                Hosts = new HostsConfig { Wsl = { [Distro] = new WslHostConfig { RepositoryPath = "~/repo" } } },
                Legs =
                {
                    ["on-distro"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = Distro },
                },
            };

            if (twoLegs)
            {
                config.Legs["only-local"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" };
            }

            if (ssh)
            {
                // In place of the distribution's leg, so the one host provisioned is reached over ssh.
                config.SshItems.Add(SshName);
                config.Hosts.Ssh[SshName] = new SshHostConfig { RepositoryPath = "/srv/repo", ConnectTimeoutSeconds = 5 };
                config.Legs.Remove("on-distro");
                config.Legs["on-ssh"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Ssh = SshName };
            }

            if (secondDistro)
            {
                config.WslDistros.Add(OtherDistro);
                config.Hosts.Wsl[OtherDistro] = new WslHostConfig { RepositoryPath = "~/repo" };
                config.Legs["on-other"] = new LegConfig
                {
                    Os = "linux",
                    Processor = "x86_64",
                    Config = "debug",
                    Wsl = OtherDistro,
                };
            }

            configure?.Invoke(config);

            var platform = HostDoubles.Platform(localPlatform);

            var processRunner = Substitute.For<IProcessRunner>();

            // This machine finds every program, which is what keeps a local host out of the way of a
            // test about a distribution. A test that provisions the local host itself says so with
            // localPlatform, and then the lookup has to answer from what that host has, so that a tool
            // is missing until this run installs it.
            processRunner.FindExecutable(Arg.Any<string>()).Returns(call =>
                localPlatform == PlatformId.Windows || Host.Has(HostId.Local, call.Arg<string>())
                    ? "/usr/bin/" + call.Arg<string>()
                    : null);

            var permissions = Substitute.For<IFilePermissions>();
            permissions.IsPrivate(Arg.Any<string>()).Returns(true);

            // The same answer, given where this machine's resolver asks it: a file on the one PATH
            // entry below starts exactly when the program it is named for is one this host has.
            permissions.IsExecutable(Arg.Any<string>()).Returns(call =>
                localPlatform == PlatformId.Windows
                || Host.Has(HostId.Local, Path.GetFileNameWithoutExtension(call.Arg<string>())));

            var commands = new ScriptedHostCommands(Host.Respond);

            if (shellProbe is not null)
            {
                commands.ShellProbe = shellProbe;
            }

            var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
            var secrets = new HostSecretsStore(fileSystem, permissions, platform);
            var addresses = new HostAddressResolver(new NameLookup(), TimeProvider.System, TimeSpan.Zero);
            var programs = new HostProgramResolver(new LocalProgramResolver(platform, permissions, () => "/usr/bin"), commands);
            var connector = new HostConnector(
                platform,
                processRunner,
                commands,
                secrets,
                addresses,
                programs,
                new SshWakeWindow(Substitute.For<INameLookup>(), TimeProvider.System, TimeSpan.Zero),
                new SyncedCopyRefusals(Substitute.For<IHarnessOutput>()));

            var output = new ConsoleHarnessOutput(Output, Error, verbose: false);

            Survey = new FakeSurvey(Host, hereFindsEverything: localPlatform == PlatformId.Windows, platform.PlatformKey);

            _service = new ToolProvisionService(
                HostDoubles.Loader(config, _repository.Path),
                connector,
                commands,
                programs,
                Survey,
                output,
                Prompt);
        }

        public FakeHost Host { get; }

        /// <summary>Each host's own survey.</summary>
        public FakeSurvey Survey { get; }

        public FakePrompt Prompt { get; }

        public StringWriter Output { get; } = new();

        public StringWriter Error { get; } = new();

        public Task<ToolProvisionReport> ProvisionAsync(IReadOnlyList<string>? legs = null, bool dryRun = false)
            => _service.ProvisionAsync(_repository.Path, legs, dryRun, TestContext.Current.CancellationToken);

        public void Dispose() => _repository.Dispose();

        /// <summary>
        /// Resolves the one name an ssh host here may be declared by, <see cref="ResolvedName"/>; every other ssh
        /// host is declared by its address, so no other name is ever looked up.
        /// </summary>
        private sealed class NameLookup : INameLookup
        {
            public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
                => name == ResolvedName
                    ? Task.FromResult<IReadOnlyList<string>>(["192.0.2.10"])
                    : throw new InvalidOperationException($"'{name}' was looked up, and only '{ResolvedName}' should be.");
        }
    }
}
