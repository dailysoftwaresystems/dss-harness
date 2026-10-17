using DssHarness.Core.Application.Legs;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Configuration;
using DssHarness.Core.Mutations;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// check-mutations' use case, in process: the option rules of a sweep and of a listing, and --held, which the machine
/// that dispatched a leg's sweep carries to the host that sweeps it, decoded here - each refused before any configuration
/// is read, in a directory no repository holds, which a read would refuse first.
/// </summary>
public sealed class MutationLegsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A self-test is never recorded, so a sweep of it under --pending has nothing pending: a usage error.</summary>
    [Fact]
    public async Task ASelfTestUnderPending_IsAUsageError_BeforeAnyConfigurationIsRead()
    {
        using var temp = new TempDirectory();

        var refused = await SweptAsync(new MutationLegsRequest { Directory = temp.Path, SelfTest = true, Pending = true });

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal("A self-test is never recorded, so nothing of it is pending: leave --pending out.", refused.Message);
    }

    /// <summary>
    /// --held is given by the machine that dispatched a leg's sweep, so without --here naming the host it was given to it
    /// is a usage error; with it, it is decoded and the sweep goes on to read its configuration.
    /// </summary>
    [Fact]
    public async Task Held_IsAUsageErrorWithoutHere_AndDecodedWithIt()
    {
        using var temp = new TempDirectory();
        var held = Convert.ToBase64String(new RecordedArms().Carried());

        var withoutHere = await SweptAsync(new MutationLegsRequest { Directory = temp.Path, Held = held });
        var withHere = await SweptAsync(new MutationLegsRequest { Directory = temp.Path, Held = held, Here = HostId.Local });

        Assert.Equal(HarnessExit.UsageError, withoutHere.ExitCode);
        Assert.Equal(
            "--held is given by the machine that dispatches a leg's sweep to the host that sweeps it, so it needs --here: leave it out.",
            withoutHere.Message);
        LegUseCases.IsTheConfigurationsRead(withHere, temp);
    }

    /// <summary>
    /// --list sweeps nothing, so an option only a sweep has a use for is a usage error beside it, each saying why - and
    /// --list alone goes on to read its configuration.
    /// </summary>
    [Theory]
    [InlineData("--pending", "has no arm to sweep")]
    [InlineData("--self-test", "has no fixture to sweep")]
    [InlineData("--force-lock", "has no lock to take")]
    [InlineData("--use-staged", "has nothing staged to sweep")]
    [InlineData("--held", "is given to a host sweeping one leg of a run")]
    [InlineData(RemoteLegRunner.HereOption, "has no leg to sweep for another machine")]
    [InlineData(null, null)]
    public async Task ListWithAnOptionOnlyASweepHasAUseFor_IsAUsageError(string? option, string? wants)
    {
        using var temp = new TempDirectory();
        var request = new MutationLegsRequest
        {
            Directory = temp.Path,
            List = true,
            Pending = option == "--pending",
            SelfTest = option == "--self-test",
            ForceLock = option == "--force-lock",
            UseStaged = option == "--use-staged",
            Held = option == "--held" ? Convert.ToBase64String(new RecordedArms().Carried()) : null,
            Here = option == RemoteLegRunner.HereOption ? HostId.Local : null,
        };

        var refused = await ListedAsync(request);

        if (option is null)
        {
            LegUseCases.IsTheConfigurationsRead(refused, temp);
            return;
        }

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal($"--list sweeps nothing, so {option} {wants}: leave one of them out.", refused.Message);
    }

    /// <summary>The rules of a sweep are its own: a sweep given what only --list refuses goes on to read its configuration.</summary>
    [Fact]
    public async Task ASweepGivenWhatOnlyAListingRefuses_GoesOnToReadItsConfiguration()
    {
        using var temp = new TempDirectory();

        var refused = await SweptAsync(new MutationLegsRequest { Directory = temp.Path, Pending = true, ForceLock = true, UseStaged = true });

        LegUseCases.IsTheConfigurationsRead(refused, temp);
    }

    /// <summary>
    /// --list begins no run and answers an <see cref="ArmListing"/>: each leg with its arms, the registry and the record's
    /// directory - the very facts its document is written from, so a front end and a step of a run read one thing.
    /// </summary>
    [Fact]
    public async Task List_AnswersAnArmListing_TheFactsItsDocumentIsWrittenFrom()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(
            temp.Path,
            Token,
            new HarnessConfig
            {
                BuildConfigs = { ["debug"] = new BuildConfiguration() },
                Toolchains = { ["gcc"] = new ToolchainConfig { Generator = "Ninja", CompilerId = { ["CXX"] = "GNU" } } },
                Projects = [new ProjectConfig { Name = "app", Type = "cmake" }],
                Legs = { ["native"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "gcc", Project = "app" } },
                Mutations = new MutationSettings { Registry = "mutations/arms.txt", TextDirectory = "mutations/texts", ReportArgs = ["--gtest_output=xml:{report}"] },
            });
        temp.WriteFile(
            Path.Combine("mutations", "arms.txt"),
            "A | charge | src/fixture.cpp | mutations/texts/charge.before | mutations/texts/charge.after | TEST-RED | fixture | fixture_tests | 3 | mutations/texts/charge.diag | the charge is pinned\n"
                + "C | charge | Fixture.Charge | red\n");
        temp.WriteFile(Path.Combine("mutations", "texts", "charge.before"), "c <= b");
        temp.WriteFile(Path.Combine("mutations", "texts", "charge.after"), "c < b");
        temp.WriteFile(Path.Combine("mutations", "texts", "charge.diag"), "charge exceeded");
        temp.WriteFile(Path.Combine("src", "fixture.cpp"), "bool within(int c, int b) { return c <= b; }\n");
        await using var services = LegUseCases.Of(harness, MutationService.CommandName);

        var listed = await services.GetRequiredService<IMutationLegs>().ListAsync(new MutationLegsRequest { Directory = temp.Path, List = true, Json = true }, Token);

        var listing = Assert.IsType<ArmListing>(listed.Facts);
        var leg = Assert.Single(listing.Legs);

        Assert.Equal(HarnessExit.Success, listed.Outcome.ExitCode);
        Assert.Equal("native", leg.Leg);
        Assert.Equal(["charge"], leg.Arms.Select(arm => arm.Arm));
        Assert.Equal("mutations/arms.txt", listing.Registry);
        Assert.Equal(new ArmVerdictRecord(harness.FileSystem, new HarnessLayout(temp.Path, temp.Path)).Directory, listing.RecordDirectory);
        Assert.Empty(listing.Unread);
        Assert.Equal([MutationListing.Json(listing.Legs, listing.Registry, listing.RecordDirectory, harness.Output.Shown)], listed.Outcome.Data);
    }

    /// <summary>
    /// A sweep's request carries nothing its use case would let pass without a word: check-mutations takes no timing, no
    /// set-ups and no say in where a leg works, so its request has no member for any of them, and none can be given.
    /// </summary>
    [Fact]
    public void ASweepsRequest_CarriesNothingItsUseCaseWouldIgnore()
    {
        Assert.Equal(
            ["ArmNames", "Directory", "ForceLock", "Held", "Here", "Json", "LegNames", "List", "Pending", "SelfTest", "UseStaged"],
            typeof(MutationLegsRequest).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private static async Task<HarnessException> SweptAsync(MutationLegsRequest request)
    {
        await using var services = LegUseCases.Of(new HarnessFactory(), MutationService.CommandName);

        return await Assert.ThrowsAsync<HarnessException>(() => services.GetRequiredService<IMutationLegs>().RunAsync(request, RunId.New(), Token));
    }

    private static async Task<HarnessException> ListedAsync(MutationLegsRequest request)
    {
        await using var services = LegUseCases.Of(new HarnessFactory(), MutationService.CommandName);

        return await Assert.ThrowsAsync<HarnessException>(() => services.GetRequiredService<IMutationLegs>().ListAsync(request, Token));
    }
}
