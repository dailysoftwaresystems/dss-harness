using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DssHarness.Tests;

/// <summary>
/// The tool's own registrations (<see cref="ServiceRegistration.AddDssHarness"/>) as a test builds them in process: the
/// very services a command resolves, save that their programs run through a process runner whose contain-exec is the
/// built CLI (<see cref="CliRunner.CliProgram"/>), as <see cref="HarnessFactory"/>'s does. The one place a test builds
/// them, so no registration of a test's starts a contained child through the program running it - which in a test is
/// the test host, and answers contain-exec with its own help into a pipe nobody reads: a leg a test's scene admitted by
/// mistake would hang the test host there, rather than run and fail the assertion that it waits.
/// </summary>
/// <remarks>
/// <see cref="CompositionRootTests"/> alone builds the registrations bare, for its subject is the root as a command
/// builds it; every other registration a test builds is this one (<see cref="TestRegistrationsTests"/>).
/// </remarks>
internal static class TestRegistrations
{
    /// <summary>The registrations of one operation, as <see cref="ServiceRegistration.AddDssHarness"/> takes them.</summary>
    /// <param name="options">What the operation is asked and how it runs.</param>
    /// <param name="operation">The operation's id.</param>
    /// <param name="output">Where the operation writes; the console where left out.</param>
    /// <param name="prompt">How a superuser password is asked; nobody to ask where left out.</param>
    internal static IServiceCollection Of(
        SessionOptions options,
        OperationId operation,
        Func<IServiceProvider, IHarnessOutput>? output = null,
        Func<IServiceProvider, ISuperuserPrompt>? prompt = null)
        => new ServiceCollection()
            .AddDssHarness(options, operation, output, prompt)
            .Replace(ServiceDescriptor.Singleton<IProcessRunner>(provider => new ProcessRunner(
                provider.GetRequiredService<IHostPlatform>(),
                provider.GetRequiredService<IFilePermissions>(),
                CliRunner.CliProgram)));
}
