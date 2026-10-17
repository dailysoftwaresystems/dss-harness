using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Output;
using DssHarness.Core.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Cli;

/// <summary>
/// The command line's host: the registrations are the tool's own, in Core; what this adds is the console - where an
/// operation writes, and how it asks for a password at a terminal without echoing what is typed.
/// </summary>
internal static class ConsoleHost
{
    /// <summary>
    /// The host every command of this process runs through: one session per invocation, its lifecycle in Core and its
    /// console here.
    /// </summary>
    internal static readonly DssHarnessHost Host = new(Build);

    /// <summary>Builds the services of one operation, as the command line runs it.</summary>
    /// <param name="options">What the operation is asked and how it runs.</param>
    /// <returns>The operation's services, disposed with its session.</returns>
    internal static ServiceProvider Build(SessionOptions options)
        => new ServiceCollection()
            .AddDssHarness(
                options,
                prompt: provider => new ConsoleSuperuserPrompt(
                    provider.GetRequiredService<IHarnessOutput>(),
                    options.Prompting))
            .BuildServiceProvider();
}
