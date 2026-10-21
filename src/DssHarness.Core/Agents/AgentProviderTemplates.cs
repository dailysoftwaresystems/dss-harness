using System.Text.Json;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Agents;

/// <summary>
/// The agent sections <c>init</c> writes into a new config - <c>defaults.agentTier</c>, <c>providers</c> and
/// <c>tierConfig</c> - read from the one template the tool holds, <c>Templates/agent-providers.json</c>: the prices
/// and sizes it holds are data with their date, the one file of the tool that holds a price. Three things read it,
/// each through <see cref="Read"/>: <c>init</c>, which writes the sections into a new config;
/// <c>list-agent-providers --template</c>, which prints them serialized by the writer that writes a config, so what is
/// pasted is what <c>init</c> wrote; and <c>help agent-clients</c>, whose sample file for the key slot names the keys
/// the template's providers appoint.
/// </summary>
public static class AgentProviderTemplates
{
    private static readonly JsonSerializerOptions Options = JsonConfigOptions.Default;

    /// <summary>
    /// The sections as <c>init</c> writes them, read from the embedded template. The numbers are as of the date each
    /// provider's <c>readOn</c> names; a repository changes them in its own config from then on.
    /// </summary>
    public static (string? AgentTier, IReadOnlyList<AgentProviderConfig> Providers, IReadOnlyDictionary<string, List<string>> TierConfig) Read()
    {
        using var stream = typeof(AgentProviderTemplates).Assembly.GetManifestResourceStream("DssHarness.Core.AgentProviders")
            ?? throw new InvalidOperationException("the agent providers template is not embedded in this build");

        var template = JsonSerializer.Deserialize<AgentSections>(stream, Options)
            ?? throw new InvalidOperationException("the agent providers template does not read back");

        return (template.Defaults?.AgentTier, template.Providers, template.TierConfig);
    }

    private sealed class AgentSections
    {
        public AgentDefaultsSection? Defaults { get; init; }

        public List<AgentProviderConfig> Providers { get; init; } = [];

        public Dictionary<string, List<string>> TierConfig { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AgentDefaultsSection
    {
        public string? AgentTier { get; init; }
    }
}
