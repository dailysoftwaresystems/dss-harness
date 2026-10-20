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

    /// <summary>The widths of the table's columns: a longer value shifts its row, never a shorter one truncates.</summary>
    private static readonly int[] Columns = [11, 11, 30, 13, 28];

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
            var document = new JsonObject { ["template"] = text };

            return CommandOutcome.Ok("the agent sections as init writes them into a new config") with { Data = [document.ToJsonString(JsonOptions)], Quiet = true };
        }

        return CommandOutcome.Ok("the agent sections as init writes them into a new config", [text]);
    }

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
            lines.Add($"no providers are declared; {ToolPackage.Command} {AgentProvidersService.ListCommand} --template prints them as init writes them into a new config, to paste");
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

            lines.Add(list.BalanceKeyUsage
                ? "keys are balanced by what each spent (balanceKeyUsage is on)"
                : "keys are tried in each provider's order (balanceKeyUsage is off)");
        }

        foreach (var provider in list.Providers)
        {
            var priced = provider.Models.Where(model => model.Priced).Select(model => model.Code).ToList();
            var unpriced = provider.Models.Where(model => !model.Priced).Select(model => model.Code).ToList();

            if (priced.Count == provider.Models.Count && priced.Count > 1)
            {
                lines.Add($"prices of '{provider.Name}' ({provider.Currency}, read {provider.ReadOn}): all {priced.Count} models priced");
            }
            else if (priced.Count > 0)
            {
                lines.Add(
                    $"prices of '{provider.Name}' ({provider.Currency}, read {provider.ReadOn}): {string.Join(", ", priced)} priced"
                    + (unpriced.Count == 0 ? string.Empty : $"; the cost of {Joined(unpriced)} is unknown"));
            }
            else
            {
                lines.Add($"prices of '{provider.Name}': none; the cost of {Joined(unpriced)} is unknown");
            }
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

    /// <summary>A list said as the tool says one: comma-separated, the last apart, "and" before it.</summary>
    private static string Joined(IReadOnlyList<string> items)
        => items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => $"{string.Join(", ", items.ToArray()[..^1])} and {items[^1]}",
        };

    /// <summary>One row of the table: every column but the last padded to its width, the last said as it is.</summary>
    private static string Row(params string[] cells)
    {
        var row = new StringBuilder();

        for (var at = 0; at < cells.Length; at++)
        {
            row.Append(at < Columns.Length && at < cells.Length - 1 ? cells[at].PadRight(Columns[at]) : cells[at]);
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
            ["unappointed"] = new JsonArray([.. list.Unappointed.Select(name => (JsonNode?)name)]),
        };
    }
}
