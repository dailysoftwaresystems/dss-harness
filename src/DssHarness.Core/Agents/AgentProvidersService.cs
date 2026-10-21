using DssHarness.Core.Application;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Agents;

/// <summary>
/// The configuration's providers, models, tiers and keys as one listing, for the command line now and a settings
/// screen later: which names the slot holds is said by name alone, so nothing this answers could carry a value.
/// </summary>
public interface IAgentProviders : IUseCase
{
    /// <summary>
    /// Lists the providers, models, tiers, clients and appointed names of the configuration at
    /// <paramref name="startDirectory"/>'s repository, or the sections as <c>init</c> writes them where
    /// <paramref name="template"/> is asked - the tool's own, answered before any repository is looked for.
    /// </summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="template">Print the agent sections as <c>init</c> writes them into a new config, to paste into an existing one.</param>
    /// <param name="json">Answer with one JSON document rather than lines.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    Task<CommandOutcome> ListAsync(string startDirectory, bool template, bool json, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAgentProviders"/>
public sealed class AgentProvidersService(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    AgentKeyStore keys) : IAgentProviders
{
    /// <summary>The command this service answers.</summary>
    public const string ListCommand = "list-agent-providers";

    /// <summary>
    /// How a configuration that declares no providers is told to add them: the command that prints the sections as
    /// <c>init</c> writes them - said by the listing, and by <c>validate-workflow</c> of a step that needs a model.
    /// </summary>
    public const string TemplateHint = $"{ToolPackage.Command} {ListCommand} --template prints them as init writes them into a new config, to paste";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostPlatform _platform = platform;
    private readonly AgentKeyStore _keys = keys;

    /// <inheritdoc/>
    public async Task<CommandOutcome> ListAsync(string startDirectory, bool template, bool json, CancellationToken cancellationToken = default)
    {
        // The sections as init writes them are the tool's own, read from no repository: answered before anything is
        // loaded, so a configuration refused for its agent sections - the one the template is printed to repair - and a
        // directory no repository holds both get them.
        if (template)
        {
            return AgentProviderReports.Template(json);
        }

        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var resolved = AgentProviders.Resolve(context.Config);

        if (resolved is not { Providers: { } providers } || resolved.Problems is [_, ..])
        {
            return CommandOutcome.Failed(
                HarnessExit.ConfigInvalid,
                $"the agent sections of the configuration do not read: {resolved.Problems[0]}",
                resolved.Problems);
        }

        var present = _keys.Present(context.Layout);

        if (present.Problems.Count > 0)
        {
            return CommandOutcome.Failed(
                HarnessExit.ConfigInvalid,
                $"the key slot '{HarnessLayout.WorkflowsEnvSlotRelative}/' does not read",
                present.Problems);
        }

        return AgentProviderReports.List(List(context.Config, providers, present), json);
    }

    /// <summary>Builds the listing of one configuration and one slot's presence, every problem already refused.</summary>
    private static AgentProviderList List(HarnessConfig config, AgentProviders resolved, AgentSlotPresent present)
    {
        bool Holds(string name) => present.Holds(name);

        List<AgentProviderListing> providers = [.. config.Providers.Select(provider => new AgentProviderListing(
            provider.Name,
            provider.Type,
            provider.Api,
            provider.Client,
            [.. provider.ApiKeyNames.Select(name => new AgentKeyListing(name, Holds(name)))],
            [.. provider.Models.Select(model => new AgentModelListing(
                model.Code,
                model.Name,
                model.Tiers,
                model.Efforts,
                model.ContextTokens,
                model.MaxOutputTokens,
                AgentProviders.Priced(model),
                AgentProviders.PricesOf(model).ToDictionary(price => price.Key, price => price.Value)))],
            provider.Currency,
            provider.ReadOn))];

        List<AgentTierListing> tiers = [.. config.TierConfig.Select(tier => new AgentTierListing(
            tier.Key,
            [.. tier.Value.Select(code => config.Providers.SelectMany(provider => provider.Models)
                .FirstOrDefault(model => model.Code == code) is { } named ? (named.Code, named.Name) : (code, code))]))];

        var appointed = resolved.Appointed();

        var clients = new List<AgentClientListing>();

        foreach (var client in config.Providers.Select(provider => provider.Client).Distinct(StringComparer.Ordinal))
        {
            // Only a client that signs in is said: a pure API client - glm - signs each request with its provider's
            // key, which the provider's own row says, and appoints no name of its own. How a client signs in is its
            // kind's, which no configuration changes in this build (part 03's auth is a later key of agentClients).
            if (resolved.SignsInWithLogin(client))
            {
                clients.Add(new AgentClientListing(client, "the machine's login", []));
            }
            else if (client == "claude-api")
            {
                foreach (var provider in config.Providers.Where(provider => provider.Client == client))
                {
                    clients.Add(new AgentClientListing(
                        client,
                        $"the key of provider '{provider.Name}'",
                        [.. provider.ApiKeyNames.Select(name => new AgentKeyListing(name, Holds(name)))]));
                }
            }
        }

        return new AgentProviderList(
            HarnessLayout.WorkflowsEnvSlotRelative + "/",
            present.Files.Count,
            providers,
            config.BalanceKeyUsage is true,
            tiers,
            clients,
            [.. present.Names.Where(name => !appointed.Any(key => string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase)))]);
    }
}
