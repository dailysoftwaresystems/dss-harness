using DssHarness.Core.Application.Legs;
using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// build's use case, in process: what it refuses before any leg is placed, and before any configuration is read where
/// the command line alone makes the request one it cannot do.
/// </summary>
public sealed class BuildLegsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// --local-copy with --in-place says two opposite things, refused as a usage error before any configuration is read -
    /// in a directory no repository holds, which a read would refuse first - with nothing said and no run kept.
    /// </summary>
    [Fact]
    public async Task LocalCopyAndInPlaceTogether_AreAUsageError_BeforeAnyConfigurationIsRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await using var services = LegUseCases.Of(harness, BuildService.CommandName);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => services.GetRequiredService<IBuildLegs>()
            .RunAsync(new BuildLegsRequest { Directory = temp.Path, LocalCopy = true, InPlace = true }, RunId.New(), Token));

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal("--local-copy and --in-place say opposite things about where this run's own legs work. Give one.", refused.Message);
        Assert.Equal(string.Empty, harness.StandardOutput.ToString());
        Assert.Equal(string.Empty, harness.StandardError.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    /// <summary>Either alone is no refusal of its own: the build goes on to read its configuration, which is what refuses it here.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task EitherAlone_IsNoRefusal_TheConfigurationIsReadThen(bool localCopy, bool inPlace)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await using var services = LegUseCases.Of(harness, BuildService.CommandName);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => services.GetRequiredService<IBuildLegs>()
            .RunAsync(new BuildLegsRequest { Directory = temp.Path, LocalCopy = localCopy, InPlace = inPlace }, RunId.New(), Token));

        LegUseCases.IsTheConfigurationsRead(refused, temp);
    }
}
