using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;

namespace RepoHarness.Tests;

/// <summary>
/// A leg placed on another machine runs there. These pin the part that makes that true: the request
/// the host is sent, and how its answer becomes this run's ledger line.
/// </summary>
public sealed class RemoteLegRunnerTests
{
    [Fact]
    public async Task TheHostIsAskedToRunTheSameCommand_ForThatLegAlone_AndOnItself()
    {
        HostCommand? sent = null;

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
            Answer(command, Ledger("passed", "412 tests", 2.5, 2.1, 412));

            return HostResults.Finished(command, 0);
        });

        var entry = await Runner(hosts).RunAsync(
            "test",
            Leg(),
            ["--filter", "auth"],
            TestContext.Current.CancellationToken);

        var request = Request(sent);

        Assert.Equal(HostAgentRequestKind.Run, request.Kind);
        Assert.Equal("/home/dev/repo", request.Directory);

        // The command this machine was asked to run, for this leg only, answered as data, and with
        // the host told to run it on itself: a host free to dispatch onward would put the verdict one
        // further hop from the reader, and could not terminate by construction.
        Assert.Equal(
            ["test", "--legs", "wsl-debug", "--json", RemoteLegRunner.HereOption, "wsl Example-Linux", "--filter", "auth"],
            request.Arguments);

        Assert.Equal(LegVerdict.Passed, entry.Verdict);
        Assert.Equal("412 tests", entry.Detail);
        Assert.Equal(412, entry.TestCount);
        Assert.Equal(TimeSpan.FromSeconds(2.5), entry.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2.1), entry.CommandTime);
    }

    /// <summary>
    /// A command run with -v has the host run the leg's command with it too, so the leg's step output reaches the reader
    /// as a leg run here shows it, relayed from the host's standard error: with the host's agent alone verbose, a leg on a
    /// host printed its verdict and nothing of its steps. Without -v, neither is.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AVerboseCommand_HasTheHostRunTheLegsCommandVerboseToo(bool verbose)
    {
        HostCommand? sent = null;

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
            Answer(command, Ledger("passed", "412 tests", 2.5, 2.1, 412));

            // What the leg's command writes of its steps where it is verbose, to its standard error under --json.
            var steps = Request(command).Arguments.Contains(HostAgentProtocol.VerboseOption) ? "test: step 'compile': cc -c a.c\n" : string.Empty;

            return HostResults.Finished(command, 0, steps);
        });

        var error = new StringWriter();
        var output = new ConsoleHarnessOutput(new StringWriter(), error, verbose);

        await new RemoteLegRunner(hosts, output).RunAsync("test", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(
            verbose
                ? ["test", "--legs", "wsl-debug", "--json", RemoteLegRunner.HereOption, "wsl Example-Linux", HostAgentProtocol.VerboseOption]
                : ["test", "--legs", "wsl-debug", "--json", RemoteLegRunner.HereOption, "wsl Example-Linux"],
            Request(sent).Arguments);
        Assert.Equal(verbose, sent!.Arguments.Contains(HostAgentProtocol.VerboseOption));
        Assert.Equal(verbose, error.ToString().Contains("test: step 'compile': cc -c a.c", StringComparison.Ordinal));
    }

    /// <summary>
    /// A host runs the leg under a run of its own, and says where that run keeps its records: the
    /// leg's line carries it, so the caller is told where its records are as for a leg run here. A
    /// host that says nothing about it names nothing.
    /// </summary>
    [Theory]
    [InlineData("/home/dev/repo/.harness-config/runs/20260919-101500-0a1b2c3d")]
    [InlineData(null)]
    public async Task TheHostsOwnRunDirectory_IsCarriedOnTheLegsLine(string? runDirectory)
    {
        var ledger = Ledger("passed", "412 tests", 2.5, 2.1, 412);

        if (runDirectory is not null)
        {
            ledger = ledger.Replace("\"exitCode\": 0,", $"\"exitCode\": 0,\n  \"runDirectory\": \"{runDirectory}\",", StringComparison.Ordinal);
        }

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, ledger);

            return HostResults.Finished(command, 0);
        });

        var entry = await Runner(hosts).RunAsync("test", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(runDirectory, entry.RunDirectory);
    }

    /// <summary>
    /// The steps the host's operating system left out travel on the leg's line, read from the very
    /// document a host writes, so the two ends cannot disagree about where they are kept; a host
    /// that ran every step names none.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheStepsTheHostsSystemLeftOut_AreCarriedOnTheLegsLine(bool leftOut)
    {
        IReadOnlyList<string> skipped = leftOut ? ["msvc", "sign"] : [];

        var written = LedgerReport
            .From([new LegEntry { Leg = "wsl-debug", Verdict = LegVerdict.Passed, SkippedSteps = skipped }], durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: []);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, 0);
        });

        var entry = await Runner(hosts).RunAsync("run", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(skipped, entry.SkippedSteps);
    }

    /// <summary>
    /// The manual steps a host ran, and the steps its run did not select, travel on the leg's line read
    /// from the document the host writes: a leg run there is no less marked than one run here.
    /// </summary>
    [Fact]
    public async Task TheManualStepsAHostRan_AndTheStepsItLeftOut_AreCarriedOnTheLegsLine()
    {
        var written = LedgerReport
            .From([new LegEntry { Leg = "wsl-debug", Verdict = LegVerdict.Passed, RanSteps = ["prepare", "bench"], ManualSteps = ["bench"], UnselectedSteps = ["build"] }], durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: []);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, 0);
        });

        var entry = await Runner(hosts).RunAsync("run", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(["prepare", "bench"], entry.RanSteps);
        Assert.Equal(["bench"], entry.ManualSteps);
        Assert.Equal(["build"], entry.UnselectedSteps);
    }

    /// <summary>
    /// What a leg's steps kept on a host travels on its line, each relative to the tree: the same path in this
    /// machine's tree, which sync --pull takes to bring it back.
    /// </summary>
    [Fact]
    public async Task WhatALegKeptOnAHost_IsNamedOnItsLine_AsSyncPullTakesIt()
    {
        const string Kept = ".harness-config/runner/actions/corpus/artifacts/run-1/wsl-debug/pack/payload.txt";

        var written = LedgerReport
            .From([new LegEntry { Leg = "wsl-debug", Verdict = LegVerdict.Passed, KeptOutputs = [Kept] }], durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: []);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, 0);
        });

        var entry = await Runner(hosts).RunAsync("run", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal([Kept], entry.KeptOutputs);
    }

    /// <summary>
    /// Each arm a host's sweep was asked about travels on its leg's line as the host judged it, read from the very
    /// document the host writes: what its run measured beside what it declares, and its records as the host names
    /// them, which stay on that host - its detail naming the host as declared, never by an address its name resolved
    /// to, and a verdict this build does not know read as poisoned rather than as any it does.
    /// </summary>
    [Fact]
    public async Task EachArmAHostsSweepJudged_IsCarriedOnItsLegsLine()
    {
        var written = LedgerReport
            .From(
                [
                    new LegEntry
                    {
                        Leg = "wsl-debug",
                        Verdict = LegVerdict.Survived,
                        Arms =
                        [
                            new ArmEntry
                            {
                                Arm = "charge-bound",
                                Verdict = LegVerdict.Survived,
                                Detail = "ran 3 case(s) beside 192.0.2.10, and none failed",
                                Duration = TimeSpan.FromSeconds(12.5),
                                Worker = 2,
                                Cases = 3,
                                DeclaredCases = 3,
                                Reds = [],
                                DeclaredReds = ["Fixture.Charge"],
                                Records = "/home/dev/repo/.harness-config/runs/r1/wsl-debug/arms/charge-bound",
                            },
                            new ArmEntry { Arm = "floor", Verdict = LegVerdict.Passed, Detail = "a verdict of a later build", DeclaredCases = 3, DeclaredReds = ["Fixture.Floor"] },
                        ],
                    },
                ],
                durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: [])
            .Replace("\"verdict\": \"passed\"", "\"verdict\": \"exploded\"", StringComparison.Ordinal);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, 0);
        });

        var mac = HostId.Ssh("mac");
        var connection = new HostConnection
        {
            Host = mac,
            Address = "mac.invalid",
            Resolved = new ResolvedAddresses(new AddressResolution("mac.invalid", Attempts: 1, ["192.0.2.10"]), NSubstitute.Substitute.For<IHostAddressResolver>()),
        };
        var leg = Leg() with { Host = Leg().Host with { Host = mac, Session = new HostSession(connection, ".dotnet/tools/dssharness") } };

        var entry = await Runner(hosts).RunAsync(MutationService.CommandName, leg, [], TestContext.Current.CancellationToken);

        Assert.Equal(["charge-bound", "floor"], entry.Arms.Select(arm => arm.Arm));

        var charge = entry.Arms[0];

        Assert.Equal((LegVerdict.Survived, "ran 3 case(s) beside mac.invalid, and none failed"), (charge.Verdict, charge.Detail));
        Assert.Equal(TimeSpan.FromSeconds(12.5), charge.Duration);
        Assert.Equal((2, 3, 3), (charge.Worker, charge.Cases, charge.DeclaredCases));
        Assert.NotNull(charge.Reds);
        Assert.Empty(charge.Reds);
        Assert.Equal(["Fixture.Charge"], charge.DeclaredReds);
        Assert.Equal("/home/dev/repo/.harness-config/runs/r1/wsl-debug/arms/charge-bound", charge.Records);

        var floor = entry.Arms[1];

        Assert.Equal(LegVerdict.Poisoned, floor.Verdict);
        Assert.Equal((null, null, null, null), (floor.Worker, floor.Cases, floor.Reds, floor.Records));
        Assert.Equal(["Fixture.Floor"], floor.DeclaredReds);
    }

    /// <summary>
    /// The last lines the phase that failed on a host printed travel on the leg's line, read from the very
    /// document the host writes: its log stays on that host, and a reader here has nothing else.
    /// </summary>
    [Fact]
    public async Task TheLastLinesAPhaseThatFailedTherePrinted_AreCarriedOnTheLegsLine()
    {
        IReadOnlyList<string> tail = ["[ 99%] Linking CXX executable app", "ld: symbol(s) not found for architecture arm64"];

        var written = LedgerReport
            .From([new LegEntry { Leg = "wsl-debug", Verdict = LegVerdict.Failed, Detail = "build exited 2", LogTail = tail }], durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: []);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, 0);
        });

        var entry = await Runner(hosts).RunAsync("build", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(tail, entry.LogTail);
    }

    /// <summary>
    /// What a host's leg printed is carried under the name the configuration declares, never by an address the
    /// host's name resolved to: the reason it gave, and the last lines its failed phase printed - the lines its
    /// standard error carried here already say it so.
    /// </summary>
    [Theory]
    [InlineData("skipped-unavailable", "ssh mac: no route to mac.invalid")]
    [InlineData("failed", "no route to mac.invalid")]
    public async Task WhatAHostsLegPrinted_NamesTheHostAsDeclared_NeverByAnAddressItsNameResolvedTo(string verdict, string detail)
    {
        var written = LedgerReport
            .From(
                [new LegEntry { Leg = "wsl-debug", Verdict = Verdicts.Parse(verdict)!.Value, Detail = "no route to 192.0.2.10", LogTail = ["connect 192.0.2.10: refused"] }],
                durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: []);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, 0);
        });

        var mac = HostId.Ssh("mac");
        var connection = new HostConnection
        {
            Host = mac,
            Address = "mac.invalid",
            Resolved = new ResolvedAddresses(new AddressResolution("mac.invalid", Attempts: 1, ["192.0.2.10"]), NSubstitute.Substitute.For<IHostAddressResolver>()),
        };
        var leg = Leg() with { Host = Leg().Host with { Host = mac, Session = new HostSession(connection, ".dotnet/tools/dssharness") } };

        var entry = await Runner(hosts).RunAsync("build", leg, [], TestContext.Current.CancellationToken);

        Assert.Equal(detail, entry.Detail);
        Assert.Equal(["connect mac.invalid: refused"], entry.LogTail);
    }

    /// <summary>
    /// The project and test set a host's count belongs to travel with the count, read from the very
    /// document the host writes: the host ran what it had, and this machine compares the count with
    /// its siblings as it was counted there, never as its own configuration would name it now.
    /// </summary>
    [Fact]
    public async Task TheProjectAndTestSetAHostCountedFor_AreCarriedWithTheCount()
    {
        var written = LedgerReport
            .From([new LegEntry { Leg = "wsl-debug", Verdict = LegVerdict.Passed, TestCount = 2238, Project = "app", TestSet = "windows" }], durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: []);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, 0);
        });

        var entry = await Runner(hosts).RunAsync("test", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(2238, entry.TestCount);
        Assert.Equal("app", entry.Project);
        Assert.Equal("windows", entry.TestSet);
    }

    /// <summary>
    /// The compilers a host's build was configured with travel on the leg's line, read from the very
    /// document the host writes, and are named once on this machine's line - not once per machine
    /// the answer passed through.
    /// </summary>
    [Fact]
    public async Task TheCompilersTheHostBuiltWith_AreCarried_AndNamedOnce()
    {
        IReadOnlyList<RepoHarness.Core.Build.CompilerFact> gnu = [new("C", "GNU", "13.2.0"), new("CXX", "GNU", "13.2.0")];

        var written = LedgerReport
            .From([new LegEntry { Leg = "wsl-debug", Verdict = LegVerdict.Failed, Detail = "2 tests failed", Compilers = gnu }], durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: []);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, HarnessExit.CommandFailed);
        });

        var entry = await Runner(hosts).RunAsync("test", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(gnu, entry.Compilers);

        var row = LedgerReport.From([entry], durationWarningFactor: 0).Render()[1];

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(row, "compiler:"));
        Assert.EndsWith("2 tests failed; compiler: GNU 13.2.0 (C, CXX)", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// The developer environment a host set up for the leg travels on the leg's line, read from the
    /// very document the host writes, and is named once on this machine's line.
    /// </summary>
    [Fact]
    public async Task TheDeveloperEnvironmentTheHostSetUp_IsCarried_AndNamedOnce()
    {
        var visualStudio = new DeveloperEnvironmentFact("vs", @"C:\VS", "18.0.1", "14.50.35717", "amd64");

        var written = LedgerReport
            .From([new LegEntry { Leg = "wsl-debug", Verdict = LegVerdict.Passed, DeveloperEnvironment = visualStudio }], durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: []);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, HarnessExit.Success);
        });

        var entry = await Runner(hosts).RunAsync("build", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(visualStudio, entry.DeveloperEnvironment);

        var row = LedgerReport.From([entry], durationWarningFactor: 0).Render()[1];

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(row, "developer environment:"));
        Assert.EndsWith("developer environment: vs (Visual Studio 18.0.1, MSVC 14.50.35717, amd64)", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg a host did not admit is not-admitted here, named by the host this machine knows, carrying what held the
    /// host's slots, why it could not read its memory, and where it keeps its record - nothing of it ran there.
    /// </summary>
    [Fact]
    public async Task ALegAHostDidNotAdmit_IsNotAdmittedHere_NamedByThatHost()
    {
        var fact = new AdmissionFact(
            false,
            3600,
            Unmeasured: "the host gave no reading",
            Holders: ["'/srv/other' on local (leg 'other', build, box pid 7, run r, since 2026-09-30 16:29:42Z)"],
            Record: "/var/lib/dssharness/admission-x.json");

        var written = LedgerReport
            .From([new LegEntry { Leg = "wsl-debug", Verdict = LegVerdict.NotAdmitted, Detail = "not admitted after 1h00m waiting for one of this machine's 1 heavy-leg slot(s)", Admission = fact }], durationWarningFactor: 0)
            .ToJson(cancelled: false, unfinished: []);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, written);

            return HostResults.Finished(command, LegExit.NotAdmitted);
        });

        var entry = await Runner(hosts).RunAsync("build", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.NotAdmitted, entry.Verdict);
        Assert.Equal("wsl Example-Linux: not admitted after 1h00m waiting for one of this machine's 1 heavy-leg slot(s)", entry.Detail);
        Assert.False(entry.Admission!.Admitted);
        Assert.Equal(fact.Holders, entry.Admission.Holders);
        Assert.Equal("the host gave no reading", entry.Admission.Unmeasured);
        Assert.Equal("/var/lib/dssharness/admission-x.json", entry.Admission.Record);
    }

    /// <summary>
    /// A host whose leg failed ends its command on its own failure line, summing up its one leg: this machine says how
    /// the leg ended itself, by the leg's line, so that line is never shown - shown, it was said twice, and read as this
    /// run's closing line - while everything the host said before it is.
    /// </summary>
    [Fact]
    public async Task AFailedLegsHostConclusion_IsLeftToThisMachine_AndNeverShown()
    {
        var harness = new HarnessFactory();
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke("build: wsl-debug: build: cmake --build");
            command.OnErrorLine?.Invoke(FailureLine.For("build", "failed: 1 of 1 leg(s)"));
            Answer(command, Ledger("failed", "build exited 1", 1, 1, null));

            return HostResults.Finished(command, HarnessExit.CommandFailed);
        });

        var entry = await Runner(hosts, harness).RunAsync("build", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal((LegVerdict.Failed, "build exited 1"), (entry.Verdict, entry.Detail));
        Assert.Contains("build: wsl-debug: build: cmake --build", harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("failed: 1 of 1 leg(s)", harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A line shaped as a failure line that the command's own work printed - a run of this tool inside a test suite
    /// prints one - is no conclusion of the host's where the command ended well, and is shown, with everything after
    /// it, in the order it came.
    /// </summary>
    [Fact]
    public async Task AFailureLineTheCommandsOwnWorkPrinted_IsShown_WhereTheCommandEndedWell()
    {
        var harness = new HarnessFactory();
        var inner = FailureLine.For("build", "an inner run's own failure, printed by a step");
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(inner);
            command.OnErrorLine?.Invoke("the step's output goes on");
            Answer(command, Ledger("passed", string.Empty, 1, 1, null));

            return HostResults.Finished(command, HarnessExit.Success);
        });

        var entry = await Runner(hosts, harness).RunAsync("build", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, entry.Verdict);
        Assert.Contains(inner + Environment.NewLine + "the step's output goes on", harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheVerdictTheHostReached_IsTheVerdictThisRunReports()
    {
        // Including the ones a local run could not produce: the host is the only machine that could
        // have seen its own tree move or its own build directory contended.
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, Ledger("inputs-moved", "2 inputs changed", 1, 1, null));

            return HostResults.Finished(command, LegExit.InputsMoved);
        });

        var entry = await Runner(hosts).RunAsync(
            "test", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.InputsMoved, entry.Verdict);
        Assert.Equal("2 inputs changed", entry.Detail);
    }

    [Fact]
    public async Task AHostThatNeverSaidHowItFinished_IsNeverReportedAsAFailedLeg()
    {
        // The command may not have run, or run only in part. A red verdict would blame the code for
        // a connection, which is the one thing a leg's verdict must never do.
        var hosts = new ScriptedHostCommands((_, _) => HostResults.Failed(255, "ssh: connection closed"));

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "test", Leg(), [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, failure.ExitCode);
        Assert.Contains("never reported how it finished", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg on a host ssh never connected to is said as that host not reached, in ssh's words, rather
    /// than as a command that may have run in part: nothing of the leg ran, and nothing there needs looking at.
    /// </summary>
    [Fact]
    public async Task ALegOnAHostSshNeverConnectedTo_IsSaidAsThatHostNotReached()
    {
        var hosts = new ScriptedHostCommands((_, _) => HostResults.Failed(255, "ssh: Could not resolve hostname mac.local: No such host is known.\n"));
        var mac = HostId.Ssh("mac");
        var leg = Leg() with
        {
            Host = Leg().Host with { Host = mac, Session = new HostSession(new HostConnection { Host = mac }, ".dotnet/tools/dssharness") },
        };

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "test", leg, [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, failure.ExitCode);
        Assert.Equal("ssh mac: the host could not be reached: ssh said ssh: Could not resolve hostname mac.local: No such host is known.", failure.Message);
    }

    [Fact]
    public async Task AnAnswerWithNoLedger_IsNotReadAsAPass()
    {
        var hosts = new ScriptedHostCommands((_, command) => HostResults.Finished(command, 0));

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "build", Leg(), [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, failure.ExitCode);
        Assert.Contains("without a ledger entry", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerWithNoLedger_NamesTheCodeTheHostExitedWith()
    {
        // The only thing the host did say. A copy with no configuration, a leg that cannot be
        // placed there and a tool that refused before it began all end this way, and without the
        // code every one of them reads as the same shrug about a host that answered fine.
        var hosts = new ScriptedHostCommands((_, command) => HostResults.Finished(command, 11));

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "build", Leg(), [], TestContext.Current.CancellationToken));

        Assert.Contains("exited 11", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that refused the whole run - a configuration, a command line or a policy its copy cannot
    /// satisfy - refused this run: the same refusal on this machine stops the run, and read as a host
    /// that could not be reached it became a skipped leg, with its reason and fix left on the host.
    /// </summary>
    [Theory]
    [InlineData(HarnessExit.Refused)]
    [InlineData(HarnessExit.ConfigInvalid)]
    [InlineData(HarnessExit.UsageError)]
    public async Task AHostsRefusalOfTheRun_IsThisRunsRefusal_InTheHostsOwnWords(int code)
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For("run", "git does not ignore this action's 'artifacts/'. Nothing has run."));

            return HostResults.Finished(command, code);
        });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "run", Leg(), ["corpus"], TestContext.Current.CancellationToken));

        Assert.Equal(code, refusal.ExitCode);
        Assert.Equal(
            "wsl Example-Linux refused 'run' for leg 'wsl-debug': git does not ignore this action's 'artifacts/'. Nothing has run.",
            refusal.Message);
    }

    [Fact]
    public async Task AHostsRefusalThatSaidNothing_StillRefuses_NamingTheCode()
    {
        var hosts = new ScriptedHostCommands((_, command) => HostResults.Finished(command, HarnessExit.Refused));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "run", Leg(), [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.EndsWith($"it exited {HarnessExit.Refused} and said nothing more", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host's refusal of the run that came with the leg's line - a sweep whose arms were judged before something there
    /// refused the run - is this run's refusal too: the line is carried, with every arm on it, and ends the run with the
    /// host's code and words, as the same sweep on this machine does. A code that is the leg's own verdict's ends none.
    /// </summary>
    [Theory]
    [InlineData(HarnessExit.Refused, true)]
    [InlineData(HarnessExit.ConfigInvalid, true)]
    [InlineData(HarnessExit.UsageError, true)]
    [InlineData(HarnessExit.Incomplete, false)]
    [InlineData(1, false)]
    [InlineData(HarnessExit.CommandFailed, false)]
    public async Task AHostsRefusalWithTheLegsLine_CarriesTheLine_AndEndsThisRunAsTheHostsDid(int code, bool refuses)
    {
        const string Said = "The mutation worker's claim file '/home/dev/repo.mutation-357e24cw-2.claim.json' could not be written.";

        var written = LedgerReport
            .From(
                [
                    new LegEntry
                    {
                        Leg = "wsl-debug",
                        Verdict = LegVerdict.Stopped,
                        Detail = "2 arm(s): 1 passed, 1 stopped",
                        Arms =
                        [
                            new ArmEntry { Arm = "charge-bound", Verdict = LegVerdict.Passed, Worker = 1 },
                            new ArmEntry { Arm = "floor", Verdict = LegVerdict.Stopped, Detail = "the sweep was stopped before a worker drove it" },
                        ],
                    },
                ],
                durationWarningFactor: 0)
            .ToJson(code, Said);

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For(MutationService.CommandName, Said));
            Answer(command, written);

            return HostResults.Finished(command, code);
        });

        var entry = await Runner(hosts).RunAsync(MutationService.CommandName, Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal((LegVerdict.Stopped, "2 arm(s): 1 passed, 1 stopped"), (entry.Verdict, entry.Detail));
        Assert.Equal([("charge-bound", LegVerdict.Passed), ("floor", LegVerdict.Stopped)], entry.Arms.Select(arm => (arm.Arm, arm.Verdict)));
        Assert.Equal(refuses ? code : null, entry.EndsTheRun?.ExitCode);
        Assert.Equal(
            refuses ? $"wsl Example-Linux refused '{MutationService.CommandName}' for leg 'wsl-debug': {Said}" : null,
            entry.EndsTheRun?.Message);
    }

    /// <summary>
    /// Any other end without a ledger is still no verdict about the code, and now says what the host
    /// said about it rather than only the code it exited with.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithNoLedger_QuotesWhatTheHostSaid()
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For("build", "no selected leg can run"));

            return HostResults.Finished(command, 1);
        });

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "build", Leg(), [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, failure.ExitCode);
        Assert.EndsWith("exited 1 without a ledger entry for it, saying: no selected leg can run", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal runs over several lines - one for each program an action may not start - and the
    /// whole of it travels, from the failure line to the end, rather than the first line alone; said by this machine's
    /// refusal, none of it is shown as the host said it, while what the host said before it is.
    /// </summary>
    [Fact]
    public async Task AHostsRefusalOverSeveralLines_TravelsWhole()
    {
        const string First = "  - line 4: 'gcc' is not declared under 'tools' and is not a path inside the repository (declared: 'dotnet').";
        const string Second = "  - line 5: 'ninja' is not declared under 'tools' and is not a path inside the repository (declared: 'dotnet').";

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke("run: corpus: resolving the action");
            command.OnErrorLine?.Invoke(FailureLine.For("run", "'actions/corpus/corpus.yml' names 2 program(s) that may not run:"));
            command.OnErrorLine?.Invoke(First);
            command.OnErrorLine?.Invoke(Second);

            return HostResults.Finished(command, HarnessExit.Refused);
        });

        var harness = new HarnessFactory();
        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts, harness).RunAsync(
            "run", Leg(), ["corpus"], TestContext.Current.CancellationToken));

        Assert.Equal(
            "wsl Example-Linux refused 'run' for leg 'wsl-debug': 'actions/corpus/corpus.yml' names 2 program(s) that may not run:"
            + Environment.NewLine + First
            + Environment.NewLine + Second,
            refusal.Message);

        var shown = harness.StandardError.ToString();
        Assert.Contains("run: corpus: resolving the action", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("may not run", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(First, shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a host's command prints is relayed as it comes, and only its end is kept, while its ledger arrives whole. A
    /// failure line followed by more lines than any failure runs to was printed by the command's own work: what was held
    /// with it is shown then, in order, everything after it as it comes, and none of it is taken for how the command ended
    /// - holding it all would hold the rest of what the run prints.
    /// </summary>
    [Fact]
    public async Task AFailureLineFollowedByMoreThanAnyFailureRunsTo_IsShownAsItComes_AndIsNoConclusion()
    {
        var harness = new HarnessFactory();
        var inner = FailureLine.For("run", "an inner run's own failure, printed by a step");
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(inner);

            for (var line = 0; line < RemoteLegRunner.MostHeldLines + 50; line++)
            {
                command.OnErrorLine?.Invoke($"step output {line}");
            }

            return HostResults.Finished(command, HarnessExit.Refused);
        });

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts, harness).RunAsync(
            "run", Leg(), ["corpus"], TestContext.Current.CancellationToken));

        Assert.EndsWith($"it exited {HarnessExit.Refused} and said nothing more", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(
            string.Join(Environment.NewLine, [inner, .. Enumerable.Range(0, RemoteLegRunner.MostHeldLines + 50).Select(line => $"step output {line}")]),
            harness.StandardError.ToString(),
            StringComparison.Ordinal);

        var (_, command) = Assert.Single(hosts.Calls);
        Assert.Equal((StreamKept.Whole, StreamKept.Tail), (command.OutputKept, command.ErrorKept));
    }

    /// <summary>
    /// A failure line the command's own output carried - a run of this tool inside a test suite
    /// prints one - is not the command's failure: the last one is, with what follows it. The earlier one is shown, with
    /// what followed it, and the command's own is not.
    /// </summary>
    [Fact]
    public async Task TheLastFailureLine_IsTheCommandsOwn()
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For("run", "an inner run's own failure, printed by a step"));
            command.OnErrorLine?.Invoke("the step's output goes on");
            command.OnErrorLine?.Invoke(FailureLine.For("run", "git does not ignore this action's 'artifacts/'. Nothing has run."));

            return HostResults.Finished(command, HarnessExit.Refused);
        });

        var harness = new HarnessFactory();
        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts, harness).RunAsync(
            "run", Leg(), ["corpus"], TestContext.Current.CancellationToken));

        Assert.Equal(
            "wsl Example-Linux refused 'run' for leg 'wsl-debug': git does not ignore this action's 'artifacts/'. Nothing has run.",
            refusal.Message);

        var shown = harness.StandardError.ToString();
        Assert.Contains(
            FailureLine.For("run", "an inner run's own failure, printed by a step") + Environment.NewLine + "the step's output goes on",
            shown,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing has run.", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host's agent fails under its own name when it could not start the command at all, and
    /// what it said travels as the command's own failure would.
    /// </summary>
    [Fact]
    public async Task AFailureTheHostsAgentReported_IsQuoted()
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            command.OnErrorLine?.Invoke(FailureLine.For(HostAgentProtocol.CommandName, "the directory '/home/dev/repo' does not exist"));

            return HostResults.Finished(command, HarnessExit.InternalError);
        });

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "build", Leg(), [], TestContext.Current.CancellationToken));

        Assert.EndsWith("saying: the directory '/home/dev/repo' does not exist", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Why a host did not run a leg is that host's reason, named by the host this machine knows: the
    /// host places the leg on itself, and has no name for itself but "this machine".
    /// </summary>
    [Fact]
    public async Task WhyAHostDidNotRunALeg_IsNamedByTheHostThisMachineKnows()
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, Ledger("skipped-tool-missing", "'cmake' is not installed there", 0, 0, tests: null));

            return HostResults.Finished(command, HarnessExit.Incomplete);
        });

        var entry = await Runner(hosts).RunAsync("test", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.SkippedToolMissing, entry.Verdict);
        Assert.Equal("wsl Example-Linux: 'cmake' is not installed there", entry.Detail);

        // A host that gave no reason is not given an empty one after its name.
        var silent = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, Ledger("skipped-unavailable", string.Empty, 0, 0, tests: null));

            return HostResults.Finished(command, HarnessExit.Incomplete);
        });

        var unexplained = await Runner(silent).RunAsync("test", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, unexplained.Detail);
    }

    [Fact]
    public async Task ProgressPrecedingTheLedger_IsNotReadAsPartOfIt()
    {
        // The host writes its progress to standard error under --json, so this end parses the whole
        // of standard output. Measured: a leg whose detail held a brace was reported as a host that
        // could not be reached, turning a real verdict into a connection failure.
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            Answer(command, Ledger("failed", "error: expected '}' before 'x'", 1.5, 1.0, tests: null));

            return HostResults.Finished(command, HarnessExit.CommandFailed);
        });

        var entry = await Runner(hosts).RunAsync(
            "test", Leg(), [], TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Failed, entry.Verdict);
        Assert.Contains("expected '}'", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatWasNeverReached_IsRefusedBeforeAnythingIsSent()
    {
        var hosts = new ScriptedHostCommands((_, _) =>
            throw new InvalidOperationException("Nothing should have been sent."));

        var unreachable = Leg() with
        {
            Host = new HostReport { Host = HostId.Wsl("Example-Linux"), Reason = "it did not answer" },
        };

        var failure = await Assert.ThrowsAsync<HarnessException>(() => Runner(hosts).RunAsync(
            "test", unreachable, [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, failure.ExitCode);
        Assert.Contains("it did not answer", failure.Message, StringComparison.Ordinal);
    }

    private static RemoteLegRunner Runner(ScriptedHostCommands hosts)
        => Runner(hosts, new HarnessFactory());

    /// <summary>A runner over <paramref name="hosts"/> whose lines <paramref name="harness"/> keeps.</summary>
    private static RemoteLegRunner Runner(ScriptedHostCommands hosts, HarnessFactory harness)
        => new(hosts, harness.Output);

    private static void Answer(HostCommand command, string ledger) => ScriptedHostCommands.Answer(command, ledger);

    private static HostAgentRequest Request(HostCommand? sent)
    {
        Assert.NotNull(sent);

        return System.Text.Json.JsonSerializer.Deserialize<HostAgentRequest>(
            sent.StandardInput!,
            HostAgentProtocol.JsonOptions)!;
    }

    /// <summary>
    /// A leg placed on a host names the tree its sync writes as a reader names it - the host's copy, then the
    /// host - never by the key its sync is shared by, whose parts a NUL joins. A consumer found that NUL in the
    /// progress line of every leg on a host, over ssh as over WSL.
    /// </summary>
    [Theory]
    [InlineData(false, "'/home/dev/repo' on wsl Example-Linux")]
    [InlineData(true, "'/home/dev/repo' on ssh example-mac")]
    public void ALegOnAHost_NamesTheTreeItsSyncWrites_AsAReaderNamesIt(bool overSsh, string tree)
    {
        var leg = Leg();

        if (overSsh)
        {
            var mac = HostId.Ssh("example-mac");

            leg = leg with
            {
                Host = leg.Host with { Host = mac, Session = new HostSession(new HostConnection { Host = mac }, ".dotnet/tools/dssharness") },
                HostSettings = new SshHostConfig { RepositoryPath = "/home/dev/repo" },
            };
        }

        var plan = leg.ToPlan();

        Assert.Equal(tree, plan.Tree);
        Assert.Contains('\0', plan.TreeKey);
    }

    private static PlacedLeg Leg()
    {
        var host = new HostReport
        {
            Host = HostId.Wsl("Example-Linux"),
            Os = "linux",
            Processor = "x86_64",
            Session = new HostSession(
                new HostConnection { Host = HostId.Wsl("Example-Linux"), Distribution = "Example-Linux" },
                ".dotnet/tools/dssharness"),
        };

        return new PlacedLeg(
            "wsl-debug",
            new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" },
            host,
            Project: null,
            new RepoHarness.Core.Build.VariantKey("x86_64", "gcc", "debug", null),
            TreeRoot: @"C:\src\repo",
            HostTreeRoot: "/home/dev/repo",
            "/home/dev/repo/build/x86_64-gcc-debug",
            new WslHostConfig { RepositoryPath = "/home/dev/repo" },
            Emulated: false);
    }

    private static string Ledger(string verdict, string detail, double duration, double command, int? tests)
        => $$"""
            {
              "verdict": "{{verdict}}",
              "exitCode": 0,
              "passed": true,
              "legs": [
                {
                  "leg": "wsl-debug",
                  "verdict": "{{verdict}}",
                  "failure": false,
                  "durationSeconds": {{duration}},
                  "commandSeconds": {{command}},
                  "overheadSeconds": 0,
                  "detail": "{{detail}}",
                  "timingsSuspect": false,
                  "timingNotes": [],
                  "testCount": {{(tests?.ToString() ?? "null")}}
                }
              ]
            }
            """;
}
