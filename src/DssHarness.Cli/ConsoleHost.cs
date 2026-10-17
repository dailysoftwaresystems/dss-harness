using DssHarness.Core.Application;
using DssHarness.Core.Composition;

namespace DssHarness.Cli;

/// <summary>
/// The command line's host: the registrations, and the services each operation is built, are the tool's own, in Core;
/// what this adds is the console's prompt - how a password is asked for at a terminal without echoing what is typed.
/// </summary>
internal static class ConsoleHost
{
    /// <summary>
    /// The host every command of this process runs through: one session per invocation, its lifecycle and its services
    /// in Core and its prompt here.
    /// </summary>
    internal static readonly DssHarnessHost Host = ServiceRegistration.Host(
        (output, options) => new ConsoleSuperuserPrompt(output, options.Prompting));
}
