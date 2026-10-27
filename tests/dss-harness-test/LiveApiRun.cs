using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// What the API clients' live smokes share: the template's provider of a type, a key of it read from the person's own
/// slot by the key store alone, and the steps of one run in a scratch tree, each request's body and answer recorded as
/// the wire carried them - never a header, so never the key.
/// </summary>
internal sealed class LiveApiRun : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServiceProvider _services;
    private readonly AgentRunner _runner;
    private readonly string _tree;
    private readonly string _work;

    /// <summary>Opens the run: the template's provider of <paramref name="providerType"/>, its key from the slot of <paramref name="root"/>, and the client <paramref name="build"/> makes over the recorded wire.</summary>
    /// <param name="root">The checkout whose key slot signs the requests.</param>
    /// <param name="providerType">The type of the template's provider the run reaches.</param>
    /// <param name="client">The client's name, which reads the provider's keys.</param>
    /// <param name="build">Makes the client over the harness's file system, the tool host and the recorded connection.</param>
    public LiveApiRun(string root, string providerType, string client, Func<HarnessFactory, AgentToolHost, ApiConnection, IAgentClient> build)
    {
        var harness = new HarnessFactory();

        (Provider, Key) = Signed(harness, root, providerType, client);
        Client = client;

        _tree = Directory.CreateDirectory(_temp.Combine("tree")).FullName;
        _work = Directory.CreateDirectory(_temp.Combine("work")).FullName;
        Wire = Directory.CreateDirectory(_temp.Combine("wire")).FullName;
        File.WriteAllText(Path.Combine(_tree, "a.txt"), "alpha\n");

        _services = new ServiceCollection().AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New()).BuildServiceProvider();
        var connection = new ApiConnection(TimeProvider.System, new WireRecording(ApiConnection.Connecting(), Wire));
        _runner = new AgentRunner([build(harness, _services.GetRequiredService<AgentToolHost>(), connection)], harness.FileSystem, harness.Platform, TimeProvider.System);
    }

    /// <summary>
    /// The template's provider of <paramref name="providerType"/>, and the first key <paramref name="client"/> reads of it,
    /// as the key store reads it from the slot of <paramref name="root"/>.
    /// </summary>
    /// <param name="harness">The harness the slot is read through.</param>
    /// <param name="root">The checkout whose key slot holds the key.</param>
    /// <param name="providerType">The type of the template's provider.</param>
    /// <param name="client">The client, which reads the provider's keys.</param>
    public static (AgentProviderConfig Provider, AgentKey Key) Signed(HarnessFactory harness, string root, string providerType, string client)
    {
        var (tier, providers, tiers) = AgentProviderTemplates.Read();
        var (resolved, problems) = AgentProviders.Resolve(new HarnessConfig
        {
            Defaults = new HarnessDefaults { AgentTier = tier },
            Providers = [.. providers],
            TierConfig = new Dictionary<string, List<string>>(tiers, StringComparer.OrdinalIgnoreCase),
        });
        Assert.Empty(problems);

        var provider = providers.Single(each => each.Type == providerType);
        var named = resolved!.KeysRead(provider, client)[0];
        var key = new AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform).Value(new HarnessLayout(root, root), resolved.Appointed(), named);
        Assert.True(key.Value is not null, key.Problem);

        return (provider, new AgentKey(named, key.Value!));
    }

    /// <summary>The template's provider the run reaches.</summary>
    public AgentProviderConfig Provider { get; }

    /// <summary>The client the run's steps are worked by.</summary>
    public string Client { get; }

    /// <summary>The key every request is signed with.</summary>
    public AgentKey Key { get; }

    /// <summary>Where each request's body and answer are recorded, as the wire carried them.</summary>
    public string Wire { get; }

    /// <summary>Each request's body the wire carried, in the order sent.</summary>
    public string[] Requests() => [.. Directory.GetFiles(Wire, "*-request.json").Order(StringComparer.Ordinal)];

    /// <summary>Works one step of the run: the read of the tree's one file allowed, on <paramref name="model"/> at <paramref name="effort"/>, going on in <paramref name="session"/> where one is named.</summary>
    /// <param name="step">The step's name.</param>
    /// <param name="model">The model of the template's provider.</param>
    /// <param name="effort">The step's effort.</param>
    /// <param name="prompt">The step's prompt.</param>
    /// <param name="session">The conversation the step goes on in; null for a new one.</param>
    /// <param name="entry">The client's entry under <c>agentClients</c>; null where the configuration declares none.</param>
    /// <param name="cancellationToken">Ends the step.</param>
    /// <param name="output">The output the step is to produce, held to its envelope; null for an answer of text.</param>
    public Task<AgentRunResult> StepAsync(string step, string model, string effort, string prompt, string? session, AgentClientConfig? entry, CancellationToken cancellationToken, AgentOutput? output = null)
    {
        var request = new AgentStepRequest(
            "r1",
            "a1",
            step,
            1,
            null,
            Provider.Name,
            Client,
            null,
            model,
            effort,
            null,
            "You are a careful agent. Do exactly what you are asked, and nothing more.",
            prompt,
            new Dictionary<string, string>(),
            null,
            new AgentWorkspace(_tree, _work, _temp.Path, [], _tree, Path.Combine(_temp.Path, ".orchestrators", "r1")),
            output,
            session is null ? null : new AgentSessionRef(Client, Provider.Name, session),
            new AgentLimits(8, 0.5m, TimeSpan.FromMinutes(5), 120),
            new Masking(Key.Value))
        {
            Ground = PolicyGrounds.Of(_tree, [_tree, _work], [_tree, _work], [], [], [], capabilities: [AgentCapability.Read]),
            Credential = new AgentCredential(AgentAuth.ApiKey, [Key]),
            Entries = new AgentEntries(Provider, Provider.Models.Single(each => each.Code == model), entry),
        };

        return _runner.RunAsync(request, new RecordedEvents(), cancellationToken);
    }

    /// <summary>The directory of the run's step <paramref name="step"/>, where its records are kept.</summary>
    /// <param name="step">The step's instance, as <see cref="StepAsync"/> named it.</param>
    public string StepDirectory(string step) => OrchestratorLayout.Of(new HarnessLayout(_temp.Path, _temp.Path), "r1").AgentStepDirectory("a1", step);

    /// <summary>Copies the steps' records and the wire to where <see cref="LiveRecords.RecordsVariable"/> names, for the person to read.</summary>
    public void Keep() => LiveRecords.Copy((Path.Combine(_temp.Path, ".orchestrators"), "steps"), (Wire, "wire"));

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }
}
