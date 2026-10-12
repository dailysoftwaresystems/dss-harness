using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// <c>update-tool</c>, the one door a machine's installed DssHarness is updated through: it moves the installation up to
/// the newest release only while no DssHarness runs on the machine, names each that does, waits for them where asked,
/// and never has the installed tool replace itself where the operating system does not let a running program be replaced.
/// No test reaches nuget.org, lists this machine's tools or reads its process table.
/// </summary>
public sealed class ToolUpdateServiceTests
{
    private const string Source = "https://api.nuget.org/v3/index.json";

    private const string Global = "this user's global tools";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A tool behind the newest release, on a machine running no DssHarness, is updated to exactly that release from
    /// nuget.org alone, and said updated only once it is listed as that release - the one reading that is not the
    /// installer's word.
    /// </summary>
    [Fact]
    public async Task AToolBehindTheNewestRelease_IsUpdatedToIt_FromNugetOrgAlone_AndSaidOnceItIsListedSo()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(
            (HarnessExit.Success, $"updated DssHarness in {Global} from 0.6.12 to 0.6.13, and it is listed there as 0.6.13 since"),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal(
            [
                "dotnet tool list --global --format json",
                $"dotnet tool update --global DssHarness --version 0.6.13 --source {Source}",
                "dotnet tool list --global --format json",
            ],
            kit.Dotnet.Ran);
    }

    /// <summary>A tool at the newest release, or ahead of it, is left as it is: versions only move up, and nothing is run.</summary>
    [Theory]
    [InlineData("0.6.13", "0.6.13")]
    [InlineData("0.7.0", "0.6.13")]
    public async Task AToolAtTheNewestRelease_IsLeftAsItIs(string installed, string newest)
    {
        // A DssHarness running beside it changes nothing of that: there is nothing to refuse.
        var kit = new Kit(installed, newest) { Table = new ScriptedTable([[Tool(7, "run")]]) };

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(
            (HarnessExit.Success, $"DssHarness {installed} in {Global} is the newest release: nothing to update"),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal(["dotnet tool list --global --format json"], kit.Dotnet.Ran);
    }

    /// <summary>A machine that has no DssHarness installed is given the newest release, installed rather than updated.</summary>
    [Fact]
    public async Task AToolNotInstalled_IsInstalledAtTheNewestRelease()
    {
        var kit = new Kit(installed: null, newest: "0.6.13");

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(
            (HarnessExit.Success, $"installed DssHarness 0.6.13 in {Global}, and it is listed there as 0.6.13 since"),
            (outcome.ExitCode, outcome.Message));
        Assert.Contains($"dotnet tool install --global DssHarness --version 0.6.13 --source {Source}", kit.Dotnet.Ran);
    }

    /// <summary>
    /// A tool kept in a directory of its own is listed, updated and read back there, and every line says where.
    /// </summary>
    [Fact]
    public async Task AToolKeptInADirectoryOfItsOwn_IsUpdatedThere()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(ToolPath: "/opt/tools"), Token);

        Assert.Equal(
            (HarnessExit.Success, "updated DssHarness in '/opt/tools' from 0.6.12 to 0.6.13, and it is listed there as 0.6.13 since"),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal(
            [
                "dotnet tool list --tool-path /opt/tools --format json",
                $"dotnet tool update --tool-path /opt/tools DssHarness --version 0.6.13 --source {Source}",
                "dotnet tool list --tool-path /opt/tools --format json",
            ],
            kit.Dotnet.Ran);
    }

    /// <summary>
    /// An update beside a DssHarness that is running is refused with nothing changed, naming each by its process and what
    /// it was asked - never the command asking, though it is a DssHarness too, nor a program of another name.
    /// </summary>
    [Fact]
    public async Task AnUpdateBesideARunningDssHarness_IsRefused_NamingEach_AndChangesNothing()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");
        kit.Table = new ScriptedTable(
        [[
            Tool(kit.Identity.CurrentId, "update-tool"),
            Tool(4242, "run", "ci", "--legs", "native"),
            new SampledProcess(5151, 1, "DSSHARNESS", null, null),
            new SampledProcess(6000, 1, "dotnet", null, "dotnet exec dssharness.dll run ci"),
        ]]);

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(
            $"DssHarness in {Global} was not updated from 0.6.12 to 0.6.13: 2 DssHarness processes are running on this machine, and an "
            + "update beside one fails where a running program cannot be replaced, takes its files from under it where one can, and "
            + "leaves no tool to find for whatever is typed meanwhile. Nothing was changed.",
            outcome.Message);
        Assert.Equal(
            [
                "  pid 4242 (dssharness run)",
                "  pid 5151",
                "Run again once each has ended, or with --wait to wait for them.",
            ],
            outcome.Details);
        Assert.Equal(["dotnet tool list --global --format json"], kit.Dotnet.Ran);
    }

    /// <summary>
    /// Asked to wait, the update says who holds it, looks again until each has ended - saying who still does every five
    /// minutes - and only then updates.
    /// </summary>
    [Fact]
    public async Task AnUpdateAskedToWait_SaysWhoHoldsIt_WaitsForEachToEnd_ThenUpdates()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");
        var looks = (int)(ToolUpdateService.SaidAgainEvery / ToolUpdateService.LooksEvery);

        kit.Table = new ScriptedTable(
        [
            [Tool(4242, "run"), Tool(4343, "host-agent")],
            .. Enumerable.Repeat<SampledProcess[]>([Tool(4343, "host-agent")], looks),
            [],
        ]);

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(Wait: true), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            [
                $"update-tool: waiting to update DssHarness in {Global} from 0.6.12 to 0.6.13 until 2 DssHarness processes on this machine have ended: "
                + "pid 4242 (dssharness run), pid 4343 (dssharness host-agent)",
                $"update-tool: still waiting to update DssHarness in {Global} from 0.6.12 to 0.6.13 until 1 DssHarness process on this machine has ended: "
                + "pid 4343 (dssharness host-agent)",
                "update-tool: no DssHarness is running on this machine now",
                $"update-tool: updating DssHarness in {Global} from 0.6.12 to 0.6.13",
            ],
            kit.Said());
        Assert.Equal(TimeSpan.FromTicks(ToolUpdateService.LooksEvery.Ticks * (looks + 1)), kit.Waited);
        Assert.Contains($"dotnet tool update --global DssHarness --version 0.6.13 --source {Source}", kit.Dotnet.Ran);
    }

    /// <summary>
    /// A wait asked from inside a DssHarness that is running - a step of its run - would never end, since what it waits
    /// for is waiting for it: so it is refused, saying so, and nothing is changed.
    /// </summary>
    [Fact]
    public async Task AWaitAskedFromInsideARunningDssHarness_IsRefused_SinceItWouldNeverEnd()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");
        kit.Table = new ScriptedTable(
        [[
            new SampledProcess(kit.Identity.CurrentId, 900, "dotnet", null, "dotnet exec dssharness.dll update-tool --wait"),
            new SampledProcess(900, 800, "bash", null, "bash -c step"),
            Tool(800, "run", "ci"),
            Tool(4242, "test"),
        ]]);

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(Wait: true), Token);

        Assert.Equal(
            (HarnessExit.Refused,
                $"DssHarness in {Global} was not updated from 0.6.12 to 0.6.13: this command was started by a DssHarness that is still "
                + "running, pid 800 (dssharness run), which an update would take the files of, and which a wait from inside it would never "
                + "see end. Nothing was changed: run it once that command has ended."),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal(TimeSpan.Zero, kit.Waited);
        Assert.Equal(["dotnet tool list --global --format json"], kit.Dotnet.Ran);
    }

    /// <summary>
    /// On Windows, where no program is replaced while it runs, the installed tool asked to update itself changes nothing
    /// and says the one command that does: this release run beside the installed one, without installing it - with the
    /// options it was given, and with who that command would wait for.
    /// </summary>
    [Theory]
    [InlineData(false, null, "dotnet tool exec DssHarness --yes --source https://api.nuget.org/v3/index.json -- update-tool")]
    [InlineData(true, null, "dotnet tool exec DssHarness --yes --source https://api.nuget.org/v3/index.json -- update-tool --wait")]
    [InlineData(true, @"C:\tools", @"dotnet tool exec DssHarness --yes --source https://api.nuget.org/v3/index.json -- update-tool --tool-path C:\tools --wait")]
    [InlineData(false, @"C:\My Tools", @"dotnet tool exec DssHarness --yes --source https://api.nuget.org/v3/index.json -- update-tool --tool-path ""C:\My Tools""")]
    public async Task OnWindows_TheInstalledToolChangesNothing_AndSaysTheCommandThatUpdatesItFromBesideIt(bool wait, string? toolPath, string command)
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13", PlatformId.Windows, program: @"C:\Users\dev\.dotnet\tools\dssharness.exe");
        kit.Table = new ScriptedTable([[Tool(kit.Identity.CurrentId, "update-tool"), Tool(4242, "run")]]);

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(wait, toolPath), Token);
        var where = toolPath is null ? Global : $"'{toolPath}'";

        Assert.Equal(
            (HarnessExit.Refused,
                $"DssHarness in {where} was not updated from 0.6.12 to 0.6.13: Windows replaces no program while it runs, and this command "
                + "is itself a DssHarness running on this machine. Nothing was changed."),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal(
            [
                "Run the update from a copy of the newest release beside the installed one, which this does without installing it:",
                "  " + command,
                "1 DssHarness process is running on this machine, which that command waits for with --wait, and is refused by without it:",
                "  pid 4242 (dssharness run)",
            ],
            outcome.Details);
        Assert.Equal(TimeSpan.Zero, kit.Waited);
        Assert.DoesNotContain(kit.Dotnet.Ran, ran => ran.Contains(" update ", StringComparison.Ordinal));
    }

    /// <summary>
    /// On Windows, a copy run beside the installed tool - by <c>dotnet tool exec</c>, as the program <c>dotnet</c> - holds
    /// none of its files, and updates it.
    /// </summary>
    [Fact]
    public async Task OnWindows_ACopyRunBesideTheInstalledTool_UpdatesIt()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13", PlatformId.Windows, program: @"C:\Program Files\dotnet\dotnet.exe");

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Contains($"dotnet tool update --global DssHarness --version 0.6.13 --source {Source}", kit.Dotnet.Ran);
        Assert.False(kit.Program.LoadedWhole);
    }

    /// <summary>
    /// Off Windows the installed tool updates itself, its files replaced under it while it runs: so it loads the whole
    /// of itself first, and what it has left to do once they are gone asks nothing of a file that is no longer there.
    /// </summary>
    [Theory]
    [InlineData(PlatformId.Linux)]
    [InlineData(PlatformId.MacOs)]
    public async Task OffWindows_TheInstalledToolLoadsItselfWhole_BeforeItsFilesAreReplaced(PlatformId platform)
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13", platform, program: "/home/dev/.dotnet/tools/dssharness");
        var loadedBeforeTheUpdate = false;

        kit.Dotnet.Running = ran => loadedBeforeTheUpdate |= ran.Contains(" update ", StringComparison.Ordinal) && kit.Program.LoadedWhole;

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.True(loadedBeforeTheUpdate, "The installed tool replaced its own files before it had loaded the whole of itself.");
    }

    /// <summary>
    /// An update that fails says what dotnet said, each DssHarness that started while it ran - which is what stops one -
    /// and what is listed since, which is what the machine now runs.
    /// </summary>
    [Fact]
    public async Task AnUpdateThatFails_SaysWhatDotnetSaid_WhoStartedWhileItRan_AndWhatIsListedSince()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");
        kit.Dotnet.Change = new ProcessResult(1, string.Empty, "Access to the path 'dssharness.dll' is denied.\n", TimeSpan.FromSeconds(10), TimedOut: false);
        kit.Dotnet.Listed = ["0.6.12", "0.6.12"];
        kit.Table = new ScriptedTable([[], [Tool(4242, "test")]]);

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(
            (HarnessExit.CommandFailed,
                $"updating DssHarness in {Global} from 0.6.12 to 0.6.13 failed (exit 1): Access to the path 'dssharness.dll' is denied."),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal(
            [
                "1 DssHarness process started on this machine while it ran, which is what stops an update:",
                "  pid 4242 (dssharness test)",
                "DssHarness is listed there as 0.6.12 since.",
                "Run again once each has ended, or with --wait to wait for them.",
            ],
            outcome.Details);
    }

    /// <summary>An update dotnet called done that is not listed as the release it was moved to is a failure, saying what is listed.</summary>
    [Fact]
    public async Task AnUpdateSaidDone_ThatIsNotListedAsTheReleaseSince_IsAFailure()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");
        kit.Dotnet.Listed = ["0.6.12", "0.6.12"];

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(
            (HarnessExit.CommandFailed,
                $"dotnet said it updated DssHarness in {Global} from 0.6.12 to 0.6.13, and it is listed there as 0.6.12 since"),
            (outcome.ExitCode, outcome.Message));
    }

    /// <summary>
    /// A feed that does not say which release is newest leaves nothing to move the tool to, and nothing is changed, with
    /// why it did not - unless the copy running is itself a release newer than the installed one, which is then the
    /// release it is moved to, and said to be, with why the feed named none: the tool is moved to a release nobody
    /// named. A copy that is no release is never that: nuget.org, the one place the tool is installed from, holds none.
    /// </summary>
    [Theory]
    [InlineData("0.6.12", null)]
    [InlineData("0.6.11", null)]
    [InlineData("0.6.14-beta.1", null)]
    [InlineData("0.6.13", "0.6.13")]
    public async Task AFeedThatDoesNotAnswer_ChangesNothing_UnlessTheCopyRunningIsAReleaseNewerThanTheInstalledOne(string running, string? movedTo)
    {
        var kit = new Kit(installed: "0.6.12", newest: null, running: running);

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        if (movedTo is not null)
        {
            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
            Assert.Contains($"dotnet tool update --global DssHarness --version {movedTo} --source {Source}", kit.Dotnet.Ran);
            Assert.Equal(
                "update-tool: nuget.org did not say which release of DssHarness is the newest - it could not be reached: no such host is known - "
                + $"so the release this copy is, {movedTo}, is the one the 0.6.12 installed is moved to",
                kit.Said()[0]);
            return;
        }

        Assert.Equal(
            (HarnessExit.CommandFailed,
                "nuget.org did not say which release of DssHarness is the newest - it could not be reached: no such host is known - so nothing "
                + $"was changed: DssHarness 0.6.12 is installed in {Global}"),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal(["dotnet tool list --global --format json"], kit.Dotnet.Ran);
    }

    /// <summary>
    /// Where neither what is installed nor the copy asking has a version a release can be told newer than, the feed is
    /// never asked, and that is what is said: not that nuget.org did not answer, which it was never given the chance to.
    /// </summary>
    [Theory]
    [InlineData(null, "DssHarness is not installed in this user's global tools, and this copy's own version, 'dev', is not one a release can be told newer than")]
    [InlineData("odd", "DssHarness odd is installed in this user's global tools, and neither 'odd' nor this copy's own version, 'dev', is one a release can be told newer than")]
    public async Task VersionsNoReleaseCanBeToldNewerThan_AreSaidAsThat_AndTheFeedIsNeverAsked(string? installed, string why)
    {
        var kit = new Kit(installed, newest: "0.6.13", running: "dev");

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(
            (HarnessExit.CommandFailed, $"which release of DssHarness is the newest was not asked of nuget.org, so nothing was changed: {why}"),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal(0, kit.Feed.Asked);
        Assert.Equal(["dotnet tool list --global --format json"], kit.Dotnet.Ran);
    }

    /// <summary>
    /// A process table read by names alone still tells which processes are a DssHarness, so an update beside one is
    /// refused as ever - saying that what each was asked could not be read, and why, where its line would otherwise
    /// look like a process asked nothing. It names no process's parent, so a wait is refused too: one asked from inside
    /// a running DssHarness could not be told from one that ends, and would be waited on for good without a word.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATableReadByNamesAlone_StillRefusesAnUpdateBesideARunningDssHarness_AndNeverWaits(bool wait)
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");

        // Gone by a second look, so a wait that was let through would end, and update.
        kit.Table = new ScriptedTable([[new SampledProcess(4242, null, "dssharness", null, null)], []]) { Degraded = "the query was refused." };

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(wait), Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(TimeSpan.Zero, kit.Waited);
        Assert.Equal(["dotnet tool list --global --format json"], kit.Dotnet.Ran);

        if (wait)
        {
            Assert.Equal(
                $"DssHarness in {Global} was not updated from 0.6.12 to 0.6.13: 1 DssHarness process is running on this machine, and which "
                + "process started which could not be read - the query was refused - so a wait asked from inside one of them could not be told "
                + "from one that ends. Nothing was changed.",
                outcome.Message);
            Assert.Equal(["  pid 4242", "Run again once each has ended."], outcome.Details);
        }
        else
        {
            Assert.Equal(
                ["  pid 4242", "What each was asked could not be read: the query was refused.", "Run again once each has ended, or with --wait to wait for them."],
                outcome.Details);
        }
    }

    /// <summary>A table read by names alone that names no DssHarness holds nothing up: the names are what an update turns on.</summary>
    [Fact]
    public async Task ATableReadByNamesAlone_ThatNamesNoDssHarness_HoldsNothingUp()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");
        kit.Table = new ScriptedTable([[new SampledProcess(4242, null, "dotnet", null, null)]]) { Degraded = "the query was refused." };

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(Wait: true), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
    }

    /// <summary>
    /// Once the update has begun the command stops neither it nor the reading of what it left: interrupted as the update
    /// starts, it lets it run to its end and says what is listed since. Cut off part way it left a machine with no tool,
    /// or half of one, and a command that said nothing of which.
    /// </summary>
    [Fact]
    public async Task OnceTheUpdateHasBegun_TheCommandStopsNeitherIt_NorTheReadingOfWhatItLeft()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");
        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(Token);

        kit.Dotnet.Running = ran =>
        {
            if (ran.Contains(" update ", StringComparison.Ordinal))
            {
                interrupted.Cancel();
            }
        };

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), interrupted.Token);

        Assert.Equal(
            (HarnessExit.Success, $"updated DssHarness in {Global} from 0.6.12 to 0.6.13, and it is listed there as 0.6.13 since"),
            (outcome.ExitCode, outcome.Message));

        // The first listing may be stopped, since nothing has been changed by then; nothing after it may.
        Assert.Equal([true, false, false], kit.Dotnet.Stoppable);
    }

    /// <summary>
    /// An update whose reading back fails says so in dotnet's own words, whether dotnet called the update done or not:
    /// what the machine now runs is then not known, and "could not be listed" alone sent nobody anywhere.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUpdateThatCannotBeReadBack_SaysWhyInDotnetsOwnWords(bool changed)
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");
        var unlisted = new ProcessResult(1, string.Empty, "The tools manifest is locked.\n", TimeSpan.Zero, TimedOut: false);

        kit.Dotnet.Running = ran => kit.Dotnet.List = ran.Contains(" update ", StringComparison.Ordinal) ? unlisted : kit.Dotnet.List;

        if (!changed)
        {
            kit.Dotnet.Change = new ProcessResult(1, string.Empty, "Access is denied.\n", TimeSpan.Zero, TimedOut: false);
        }

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        const string Unknown = "hat is listed there since is not known: its tools could not be listed (exit 1): The tools manifest is locked";

        Assert.Equal(HarnessExit.CommandFailed, outcome.ExitCode);

        if (changed)
        {
            Assert.Equal($"dotnet said it updated DssHarness in {Global} from 0.6.12 to 0.6.13, and w{Unknown}.", outcome.Message);
        }
        else
        {
            Assert.Equal($"updating DssHarness in {Global} from 0.6.12 to 0.6.13 failed (exit 1): Access is denied.", outcome.Message);
            Assert.Equal([$"W{Unknown}."], outcome.Details);
        }
    }

    /// <summary>Tools that cannot be listed are a failure saying what dotnet said, with nothing changed.</summary>
    [Fact]
    public async Task ToolsThatCannotBeListed_AreAFailure_AndNothingIsChanged()
    {
        var kit = new Kit(installed: "0.6.12", newest: "0.6.13");
        kit.Dotnet.List = new ProcessResult(1, string.Empty, "dotnet: command not understood\n", TimeSpan.Zero, TimedOut: false);

        var outcome = await kit.Service().RunAsync(new ToolUpdateRequest(), Token);

        Assert.Equal(
            (HarnessExit.CommandFailed, $"the .NET tools in {Global} could not be listed, so nothing was changed (exit 1): dotnet: command not understood"),
            (outcome.ExitCode, outcome.Message));
        Assert.Single(kit.Dotnet.Ran);
    }

    /// <summary>
    /// What a running DssHarness was asked is read from its command line as the word after its program - quoted or not,
    /// whatever directory it was started from - and nothing else of that line is repeated: an input's value is on it.
    /// </summary>
    [Theory]
    [InlineData("dssharness run ci --input token=s3cret", "run")]
    [InlineData("\"C:\\Users\\a b\\.dotnet\\tools\\dssharness.exe\" host-agent", "host-agent")]
    [InlineData("/home/dev/.dotnet/tools/dssharness  check-mutations --arms a", "check-mutations")]
    [InlineData("dssharness -C /src test", null)]
    [InlineData("dssharness", null)]
    [InlineData(null, null)]
    public void WhatARunningDssHarnessWasAsked_IsTheWordAfterItsProgram_AndNothingElseOfItsLine(string? commandLine, string? verb)
        => Assert.Equal(verb, ToolUpdateService.AskedOf(commandLine));

    /// <summary>A DssHarness process the table lists, asked <paramref name="arguments"/>.</summary>
    private static SampledProcess Tool(int id, params string[] arguments)
        => new(id, 1, "dssharness", null, string.Join(' ', ["dssharness", .. arguments]));

    /// <summary>The service, with everything it asks of the machine scripted.</summary>
    private sealed class Kit(string? installed, string? newest, PlatformId platform = PlatformId.Linux, string program = "/usr/share/dotnet/dotnet", string running = "0.6.12")
    {
        private readonly HarnessFactory _harness = new();

        public ScriptedDotnet Dotnet { get; } = new(installed, newest);

        public PublishedVersionsDouble Feed { get; } = new(newest);

        public ScriptedTable Table { get; set; } = new([[]]);

        public OwnProgramDouble Program { get; } = new(program);

        public IProcessIdentity Identity => _harness.Identity;

        /// <summary>How long the service has waited, by its own count.</summary>
        public TimeSpan Waited { get; private set; }

        public ToolUpdateService Service()
            => new(
                Table,
                Dotnet,
                Feed,
                new RunningToolDouble(running),
                _harness.Identity,
                Program,
                HostDoubles.Platform(platform),
                _harness.Output,
                (span, _) =>
                {
                    Waited += span;
                    return Task.CompletedTask;
                });

        /// <summary>Every line the command wrote as it went.</summary>
        public string[] Said()
            => (_harness.StandardOutput.ToString() + _harness.StandardError)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>A process table that reads as each of <paramref name="readings"/> in turn, and as the last of them from then on.</summary>
    private sealed class ScriptedTable(SampledProcess[][] readings) : IProcessTable
    {
        private int _read;

        /// <summary>Why every reading is less than it should be, where a test says it is.</summary>
        public string? Degraded { get; init; }

        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
        {
            var reading = readings[Math.Min(_read++, readings.Length - 1)];

            return Task.FromResult(new ProcessTableReading(reading, Degraded));
        }
    }

    /// <summary>
    /// A <c>dotnet</c> that lists the tool as installed at each of <see cref="Listed"/> in turn - at first as the kit
    /// says, and after a change as the release it was moved to - and records every command it was given.
    /// </summary>
    private sealed class ScriptedDotnet(string? installed, string? newest) : IProcessRunner
    {
        private int _listed;

        public List<string> Ran { get; } = [];

        /// <summary>Whether each command, in turn, was given a token that can stop it.</summary>
        public List<bool> Stoppable { get; } = [];

        /// <summary>What each listing says is installed, in turn; the last from then on.</summary>
        public string?[] Listed { get; set; } = [installed, newest ?? "0.6.13"];

        /// <summary>What a listing answers with instead, where set.</summary>
        public ProcessResult? List { get; set; }

        /// <summary>What an install or an update answers with.</summary>
        public ProcessResult Change { get; set; } = new(0, "Tool 'dssharness' was successfully updated.\n", string.Empty, TimeSpan.Zero, TimedOut: false);

        /// <summary>Called with each command as it starts.</summary>
        public Action<string>? Running { get; set; }

        public string? FindExecutable(string command) => command;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var ran = string.Join(' ', [request.FileName, .. request.Arguments]);

            Ran.Add(ran);
            Stoppable.Add(cancellationToken.CanBeCanceled);
            Running?.Invoke(ran);

            if (!ran.Contains(" list ", StringComparison.Ordinal))
            {
                return Task.FromResult(Change);
            }

            if (List is not null)
            {
                return Task.FromResult(List);
            }

            var version = Listed[Math.Min(_listed++, Listed.Length - 1)];
            var data = version is null ? string.Empty : $$"""{"packageId":"dssharness","version":"{{version}}","commands":["dssharness"]}""";

            return Task.FromResult(new ProcessResult(0, $$"""{"version":1,"data":[{{data}}]}""", string.Empty, TimeSpan.Zero, TimedOut: false));
        }
    }

    /// <summary>The program a test says this process is.</summary>
    private sealed class OwnProgramDouble(string path) : IOwnProgram
    {
        public string? Path { get; } = path;

        public bool LoadedWhole { get; private set; }

        public void LoadWhole() => LoadedWhole = true;
    }
}
