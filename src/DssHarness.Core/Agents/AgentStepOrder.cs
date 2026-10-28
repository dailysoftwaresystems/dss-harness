using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Agents;

/// <summary>
/// The running order of a step's models as its resolution says it (<see cref="AgentResolution"/>), each entry given
/// what it runs by: its client's settings and the credential its key signs in with, read from the slot once for them
/// all. A key the slot does not hold is an entry the machine cannot use, said and passed over when its turn comes
/// (<see cref="AgentRunEntry.Usable"/>); where none of the step's first model's keys is held the run is refused before
/// it starts, as the walk's own rule says. The keys are in the list's order; balancing them by what each spent is
/// <c>balanceKeyUsage</c>'s, a slice of its own.
/// </summary>
public static class AgentStepOrder
{
    /// <summary>
    /// The running order of <paramref name="resolution"/>'s entries, each with its credential read from the slot of
    /// <paramref name="layout"/> where its client signs in with a key: a client that signs in with the machine's login
    /// reads none. A key the slot does not answer makes its entry unusable, the refusal's own sentence saying why.
    /// </summary>
    /// <param name="providers">The configuration's providers and tiers, resolved.</param>
    /// <param name="resolution">The step's resolution, its serving entry first.</param>
    /// <param name="layout">The repository, whose slot holds the keys.</param>
    /// <param name="keys">The slot the keys are read from.</param>
    /// <param name="toolConfigDirectory">Claude Code's own directory on this machine, which a launch reads where the slot holds no other for it.</param>
    public static IReadOnlyList<AgentRunEntry> Of(
        AgentProviders providers,
        AgentResolution resolution,
        HarnessLayout layout,
        AgentKeyStore keys,
        string? toolConfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(keys);

        var appointed = providers.Appointed();
        var answers = new Dictionary<string, AgentKeyAnswer>(StringComparer.OrdinalIgnoreCase);

        AgentKeyAnswer Asked(string name)
            => answers.TryGetValue(name, out var held) ? held : answers[name] = keys.Value(layout, appointed, name);

        AgentRunEntry Entry(AgentModelEntry entry)
        {
            var provider = providers.Providers.FirstOrDefault(one => string.Equals(one.Name, entry.Provider, StringComparison.Ordinal));
            var model = provider?.Models.FirstOrDefault(one => string.Equals(one.Code, entry.Model, StringComparison.Ordinal));
            var entries = provider is not null && model is not null ? new AgentEntries(provider, model, providers.ClientOf(entry.Client)) : null;

            // A client that signs in with the machine's login reads no key: the model is its own entry, the login itself.
            if (entry.Key is not { Length: > 0 } key)
            {
                var login = provider is null
                    ? $"provider '{entry.Provider}' is not in the configuration"
                    : model is null ? $"model '{entry.Model}' is not in the configuration" : null;

                return new AgentRunEntry(entry.Model, entry.Provider, entry.Client, null, entry.Effort, Usable: login is null, login)
                {
                    Entries = entries,
                };
            }

            var answer = Asked(key);
            var refused = provider is null
                ? $"provider '{entry.Provider}' is not in the configuration"
                : model is null
                    ? $"model '{entry.Model}' is not in the configuration"
                    : answer.Value is { Length: > 0 } ? null : answer.Problem ?? $"the slot holds nothing for the key '{key}'";

            return new AgentRunEntry(entry.Model, entry.Provider, entry.Client, key, entry.Effort, Usable: refused is null, refused)
            {
                Entries = entries,
                Credential = CredentialOf(providers, entry, answer, Asked, toolConfigDirectory),
            };
        }

        return [Entry(resolution.Serving), .. resolution.After.Select(Entry)];
    }

    /// <summary>
    /// The credential the entry signs in with, where its client signs in with a key: how, the key's value as the one
    /// entry's own, the provider's address where a launch that signs in with a key is sent to it, and the Claude Code
    /// directory a launch reads. Null where the slot did not answer the key - an entry that cannot run - and for the
    /// machine's login.
    /// </summary>
    /// <param name="providers">The configuration's providers and tiers, resolved.</param>
    /// <param name="entry">The entry, whose key the credential holds where the slot answered it.</param>
    /// <param name="answer">What the slot answered the entry's key.</param>
    /// <param name="asked">Reads one more name of the slot, cached by the caller.</param>
    /// <param name="toolConfigDirectory">Claude Code's own directory on this machine, which a launch reads where the slot holds no other for it.</param>
    private static AgentCredential? CredentialOf(
        AgentProviders providers,
        AgentModelEntry entry,
        AgentKeyAnswer answer,
        Func<string, AgentKeyAnswer> asked,
        string? toolConfigDirectory)
    {
        if (answer.Value is not { Length: > 0 } value)
        {
            return null;
        }

        var auth = providers.AuthOf(entry.Client) ?? AgentAuth.ApiKey;
        var api = auth == AgentAuth.Login
            ? null
            : providers.Providers.FirstOrDefault(one => string.Equals(one.Name, entry.Provider, StringComparison.Ordinal))?.Api is { Length: > 0 } address ? address : null;

        return new AgentCredential(auth, [new AgentKey(entry.Key!, value)], api, ConfigDirectoryOf(providers, entry.Client, asked, toolConfigDirectory));
    }

    /// <summary>
    /// The Claude Code directory a launch of <paramref name="client"/> reads, where it is one: what the slot holds
    /// under the name its <c>configDir</c> gives - <c>CLAUDE_CONFIG_DIR</c> where it gives none - else the tool's own
    /// on this machine, else none, which is Claude Code's own default.
    /// </summary>
    /// <param name="providers">The configuration's providers and tiers, resolved.</param>
    /// <param name="client">The client a launch is started by.</param>
    /// <param name="asked">Reads one more name of the slot, cached by the caller.</param>
    /// <param name="toolConfigDirectory">Claude Code's own directory on this machine, which a launch reads where the slot holds no other for it.</param>
    private static string? ConfigDirectoryOf(AgentProviders providers, string client, Func<string, AgentKeyAnswer> asked, string? toolConfigDirectory)
        => string.Equals(client, "claude-code", StringComparison.Ordinal)
            ? providers.SettingOf(client, AgentClientSetting.ConfigDirKey) is { Length: > 0 } named
                ? asked(named).Value is { Length: > 0 } fromSlot ? fromSlot : toolConfigDirectory
                : toolConfigDirectory
            : null;
}
