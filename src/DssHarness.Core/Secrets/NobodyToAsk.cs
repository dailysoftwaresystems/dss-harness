using DssHarness.Core.Hosts;

namespace DssHarness.Core.Secrets;

/// <summary>
/// A front end that asks nobody: the prompt a session begun without one runs with, because reading a terminal without
/// echoing what is typed is the one thing the core cannot do for itself.
/// </summary>
/// <remarks>
/// Availability says what every consumer of a refused prompt already reads: nobody can be asked, and the run is
/// refused exactly as it always was without a credential. Reading is never reached - a consumer asks first, which is
/// the contract - and says so rather than return a password nobody typed.
/// </remarks>
public sealed class NobodyToAsk : ISuperuserPrompt
{
    /// <inheritdoc/>
    public PromptAvailability Availability => PromptAvailability.Unavailable;

    /// <inheritdoc/>
    public Task<string> ReadPasswordAsync(HostId host, CancellationToken cancellationToken)
        => throw new InvalidOperationException($"nobody can be asked for {host}'s password, and nobody was.");
}
