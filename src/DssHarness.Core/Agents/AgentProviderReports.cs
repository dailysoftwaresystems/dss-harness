using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Agents;

/// <summary>What <c>list-agent-providers</c> reports: the providers, tiers, clients and appointed names, and never a value.</summary>
public static class AgentProviderReports
{
    private static readonly JsonSerializerOptions JsonOptions = ReportJson.Options;

    /// <summary>
    /// The widths of the table's columns, each two spaces past its longest usual value: a longer value shifts the rest
    /// of its row, two spaces still after it, and never truncates.
    /// </summary>
    private static readonly int[] Columns = [11, 11, 30, 13, 28];

    /// <summary>The fewest spaces after a column's value, where it is as long as its column or longer.</summary>
    private const int Gap = 2;

    /// <summary>What <c>list-agent-providers</c> reports, or the sections as <c>init</c> writes them where asked.</summary>
    /// <param name="list">The listing, every problem already refused.</param>
    /// <param name="json">Whether to answer with one JSON document rather than lines.</param>
    public static CommandOutcome List(AgentProviderList list, bool json)
    {
        ArgumentNullException.ThrowIfNull(list);

        var message = Summary(list);

        if (json)
        {
            var document = Document(list).ToJsonString(JsonOptions);

            return CommandOutcome.Ok(message) with { Data = [document], Quiet = true };
        }

        return CommandOutcome.Ok(message, Lines(list));
    }

    /// <summary>
    /// The template the command prints: the <c>defaults.agentTier</c>, <c>providers</c> and <c>tierConfig</c> sections,
    /// serialized by the writer that writes a config - the same values a config <c>init</c> wrote holds, for a
    /// repository that has a config already and wants them in it.
    /// </summary>
    /// <param name="json">Whether to answer with one JSON document rather than the sections' own text.</param>
    public static CommandOutcome Template(bool json)
    {
        var agents = AgentProviderTemplates.Read();
        var text = JsonSerializer.Serialize(
            new { defaults = new { agentTier = agents.AgentTier }, providers = agents.Providers, tierConfig = agents.TierConfig },
            DssHarness.Core.Configuration.JsonConfigOptions.Default);

        if (json)
        {
            return CommandOutcome.Ok("the agent sections as init writes them into a new config") with { Data = [TemplateDocument(text).ToJsonString(JsonOptions)], Quiet = true };
        }

        return CommandOutcome.Ok("the agent sections as init writes them into a new config", [text]);
    }

    /// <summary>
    /// The template's own document where <c>--template</c> stops before it prints anything - a <c>--directory</c> that
    /// does not exist, a command line the parser refuses: its shape, no template, after the code it exits with and the
    /// line it ends on - never the listing's.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string TemplateStopped(int exitCode, string summary)
        => ReportJson.Stopped(exitCode, summary, TemplateDocument(text: null));

    /// <summary>The one shape of the template's document: the sections' text, or null where none was printed.</summary>
    private static JsonObject TemplateDocument(string? text) => new() { ["template"] = text };

    /// <summary>
    /// The listing the command answers with where it stops before it lists anything - a directory that does not
    /// exist, a repository not initialised: its own document, listing nothing, after the code it exits with and the
    /// line it ends on. The slot is named even stopped, for it is where it was stopped on the way to reading.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary)
        => ReportJson.Stopped(
            exitCode,
            summary,
            Document(new AgentProviderList(
                DssHarness.Core.Repository.HarnessLayout.WorkflowsEnvSlotRelative + "/",
                0,
                [],
                false,
                [],
                [],
                [],
                [],
                [])));

    private static string Summary(AgentProviderList list)
    {
        var appointed = list.Providers.SelectMany(provider => provider.Keys).ToList();

        return list.Providers.Count == 0
            ? "no providers are declared"
            : $"{ReportText.Counted(list.Providers.Count, "provider")}, {ReportText.Counted(list.Tiers.Count, "tier")}; "
                + $"the slot holds {appointed.Count(key => key.Held)} of {ReportText.Counted(appointed.Count, "appointed name")}";
    }

    private static IReadOnlyList<string> Lines(AgentProviderList list)
    {
        var lines = new List<string>();

        if (list.Providers.Count == 0)
        {
            lines.Add($"no providers are declared; {AgentProvidersService.TemplateHint}");
        }
        else
        {
            lines.Add(Row("provider", "type", "api", "client", "keys, in order", "models"));

            foreach (var provider in list.Providers)
            {
                lines.Add(Row(
                    provider.Name,
                    provider.Type,
                    provider.Api,
                    provider.Client,
                    string.Join(", ", provider.Keys.Select(key => $"{key.Name,-17}  {(key.Held ? "present" : "missing")}")),
                    string.Join(", ", provider.Models.Select(model => model.Code))));
            }

            // Choosing a key is the engine's, when it runs a run's steps: the line says what it is to do, and that no
            // command of this build does it.
            lines.Add(list.BalanceKeyUsage
                ? "balanceKeyUsage is on: a run's steps are to take the key that spent least - no command runs a step in this build"
                : "balanceKeyUsage is off: a run's steps are to take each provider's keys in order - no command runs a step in this build");
        }

        foreach (var provider in list.Providers)
        {
            lines.Add(PriceLine(provider.Name, provider.Currency, provider.ReadOn, [.. provider.Models.Select(model => (model.Code, model.Priced))], everyModel: true));
        }

        foreach (var tier in list.Tiers)
        {
            lines.Add($"tier {(tier.Name + ":").PadRight(10)}{string.Join(", ", tier.Models.Select(model => $"{model.Name} ({model.Code})"))}");
        }

        foreach (var client in list.Clients)
        {
            lines.Add(client.Keys.Count == 0
                ? $"client '{client.Name}' signs in with {client.SignIn} and reads no key"
                : $"client '{client.Name}' signs in with {client.SignIn} ({string.Join(", ", client.Keys.Select(key => $"{key.Name}, {(key.Held ? "present" : "missing")}"))})");
        }

        foreach (var client in list.Settings.GroupBy(setting => setting.Client))
        {
            lines.Add($"client '{client.Key}' reads its settings where the slot holds them, none needed: {string.Join(", ", client.Select(setting => $"{setting.Name} ({setting.Key}), {(setting.Held ? "present" : "absent")}"))}");
        }

        foreach (var source in list.Sources)
        {
            lines.Add($"web-search source '{source.Source}' is sent its key where the slot holds it, none needed: {source.Name}, {(source.Held ? "present" : "absent")}");
        }

        if (list.Unappointed.Count > 0)
        {
            lines.Add($"declared in the slot and appointed by nothing: {string.Join(", ", list.Unappointed)}");
        }

        var files = list.Files switch
        {
            0 => "no file",
            1 => "1 file",
            var count => $"{count} files",
        };

        lines.Add($"keys are read from {list.Slot} ({files}) and from nowhere else");

        return lines;
    }

    /// <summary>
    /// The one line that says whether a provider's models have a price and when its numbers were read - said by the
    /// listing of every model, and by <c>validate-workflow</c> of the models a step of the definition may run on: the
    /// currency and the date beside the priced, and a model with no price named with its cost unknown.
    /// </summary>
    /// <param name="provider">The provider's name.</param>
    /// <param name="currency">Its currency, where it names one.</param>
    /// <param name="readOn">The date its numbers were read, where it names one.</param>
    /// <param name="models">The models said, in its order, each with whether it is priced (<see cref="AgentProviders.Priced"/>).</param>
    /// <param name="everyModel">Whether they are every model it lists, which alone lets every one priced be said as all of them.</param>
    public static string PriceLine(string provider, string? currency, string? readOn, IReadOnlyList<(string Code, bool Priced)> models, bool everyModel)
    {
        ArgumentNullException.ThrowIfNull(models);

        var priced = models.Where(model => model.Priced).Select(model => model.Code).ToList();
        var unpriced = models.Where(model => !model.Priced).Select(model => model.Code).ToList();

        if (priced.Count == 0)
        {
            return $"prices of '{provider}': none; the cost of {Joined(unpriced)} is unknown";
        }

        return everyModel && unpriced.Count == 0 && priced.Count > 1
            ? $"prices of '{provider}' ({currency}, read {readOn}): all {priced.Count} models priced"
            : $"prices of '{provider}' ({currency}, read {readOn}): {string.Join(", ", priced)} priced"
                + (unpriced.Count == 0 ? string.Empty : $"; the cost of {Joined(unpriced)} is unknown");
    }

    /// <summary>A list said as the tool says one: comma-separated, the last apart, "and" before it.</summary>
    private static string Joined(IReadOnlyList<string> items)
        => items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => $"{string.Join(", ", items.ToArray()[..^1])} and {items[^1]}",
        };

    /// <summary>
    /// One row of the table: every column but the last padded to its width, or past its value by the gap where the value
    /// is as long or longer, the last said as it is.
    /// </summary>
    private static string Row(params string[] cells)
    {
        var row = new StringBuilder();

        for (var at = 0; at < cells.Length; at++)
        {
            row.Append(at < Columns.Length && at < cells.Length - 1
                ? cells[at].PadRight(Math.Max(Columns[at], cells[at].Length + Gap))
                : cells[at]);
        }

        return row.ToString().TrimEnd();
    }

    private static JsonObject Document(AgentProviderList list)
    {
        var providers = new JsonArray();

        foreach (var provider in list.Providers)
        {
            providers.Add(new JsonObject
            {
                ["name"] = provider.Name,
                ["type"] = provider.Type,
                ["api"] = provider.Api,
                ["client"] = provider.Client,
                ["keys"] = new JsonArray([.. provider.Keys.Select(key => new JsonObject { ["name"] = key.Name, ["held"] = key.Held })]),
                ["models"] = new JsonArray([.. provider.Models.Select(model => new JsonObject
                {
                    ["code"] = model.Code,
                    ["name"] = model.Name,
                    ["tiers"] = new JsonArray([.. model.Tiers.Select(tier => (JsonNode?)tier)]),
                    ["efforts"] = new JsonArray([.. model.Efforts.Select(effort => (JsonNode?)effort)]),
                    ["contextTokens"] = model.ContextTokens,
                    ["maxOutputTokens"] = model.MaxOutputTokens,
                    ["priced"] = model.Priced,
                    ["prices"] = new JsonObject([.. model.Prices.Select(price => new KeyValuePair<string, JsonNode?>(price.Key, (JsonNode?)price.Value))]),
                })]),
                ["currency"] = provider.Currency,
                ["readOn"] = provider.ReadOn,
            });
        }

        var tiers = new JsonArray();

        foreach (var tier in list.Tiers)
        {
            tiers.Add(new JsonObject
            {
                ["name"] = tier.Name,
                ["models"] = new JsonArray([.. tier.Models.Select(model => new JsonObject { ["code"] = model.Code, ["name"] = model.Name })]),
            });
        }

        var clients = new JsonArray();

        foreach (var client in list.Clients)
        {
            clients.Add(new JsonObject
            {
                ["name"] = client.Name,
                ["signIn"] = client.SignIn,
                ["keys"] = new JsonArray([.. client.Keys.Select(key => new JsonObject { ["name"] = key.Name, ["held"] = key.Held })]),
            });
        }

        return new JsonObject
        {
            ["slot"] = list.Slot,
            ["files"] = list.Files,
            ["providers"] = providers,
            ["balanceKeyUsage"] = list.BalanceKeyUsage,
            ["tiers"] = tiers,
            ["clients"] = clients,
            ["settings"] = new JsonArray([.. list.Settings.Select(setting => new JsonObject { ["client"] = setting.Client, ["key"] = setting.Key, ["name"] = setting.Name, ["held"] = setting.Held })]),
            ["sources"] = new JsonArray([.. list.Sources.Select(source => new JsonObject { ["source"] = source.Source, ["name"] = source.Name, ["held"] = source.Held })]),
            ["unappointed"] = new JsonArray([.. list.Unappointed.Select(name => (JsonNode?)name)]),
        };
    }
}
