using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// The use cases of the commands that run legs as the tool's own registrations build them - the very services a command
/// resolves - writing to a factory's output, so what they refuse is read in process as the command reads it, with no
/// process spawned.
/// </summary>
/// <remarks>
/// Only for what is refused before any leg is placed: a run that went on would measure this machine and keep its records
/// in this user's own data, which a test's process has no business writing.
/// </remarks>
internal static class LegUseCases
{
    /// <summary>The services of one operation of <paramref name="command"/>, writing to <paramref name="harness"/>'s output.</summary>
    /// <param name="harness">Whose output the operation writes to.</param>
    /// <param name="command">The command the operation runs as.</param>
    internal static ServiceProvider Of(HarnessFactory harness, string command)
        => new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = command, Prompting = false }, output: _ => harness.Output)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

    /// <summary>
    /// The directory <paramref name="temp"/> as a reading of its configuration refuses it - inside no repository - so a
    /// refusal that is anything else was made before any configuration was read.
    /// </summary>
    /// <param name="temp">A directory inside no repository.</param>
    internal static string NotARepository(TempDirectory temp) => $"'{temp.Path}' is not inside a git repository.";

    /// <summary>That <paramref name="refused"/> is the refusal a reading of the configuration of <paramref name="temp"/> makes.</summary>
    /// <param name="refused">What the use case threw.</param>
    /// <param name="temp">A directory inside no repository.</param>
    internal static void IsTheConfigurationsRead(HarnessException refused, TempDirectory temp)
    {
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(NotARepository(temp), refused.Message);
    }
}
