using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Agents.ClaudeApi;

/// <summary>
/// The claude-api client: a step worked through the Claude API by the loop the two API clients share
/// (<see cref="ToolLoop"/>) over the Claude API's provider (<see cref="ClaudeApiProvider"/>), every call run by the
/// harness's own tool runtime and every request signed with a key of the provider's. What it reaches, how and with what
/// is the run's entries': the provider's address, the model's entry, and its own entry's tries, cache and additions.
/// </summary>
/// <param name="fileSystem">Keeps the conversation and the step's records.</param>
/// <param name="tools">The host each run's tool runtime is opened on.</param>
/// <param name="clock">Times the run's bounds and each wait.</param>
/// <param name="connection">How each request is posted and its answer read.</param>
/// <param name="wait">Waits before a request is asked again; the clock's own where none is given.</param>
/// <param name="jitter">A number from naught up to one, which takes up to a quarter off each wait; a random one where none is given.</param>
public sealed class ClaudeApiClient(
    IFileSystem fileSystem,
    AgentToolHost tools,
    TimeProvider clock,
    ApiConnection connection,
    Func<TimeSpan, CancellationToken, Task>? wait = null,
    Func<double>? jitter = null) : IAgentClient
{
    /// <summary>The client's key under <c>agentClients</c>.</summary>
    public const string ClientName = "claude-api";

    private readonly ToolLoop _loop = new(fileSystem, tools, clock, wait, jitter);
    private readonly ApiConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>What this client can do, as the capabilities table states it.</summary>
    public static AgentClientCapabilities Descriptor { get; } = new(
        Streaming: true,
        Thinking: true,
        WritableWhileWorking: true,
        GoesOnWith: AgentConversationHandling.Handed,
        SchemaEnforcedOutput: ClaudeApiRequest.HoldsToSchema,
        FileAndCommandTools: AgentToolsOwner.Harness,
        Web: AgentToolsOwner.Harness,
        ReportsCost: false,
        EffortLevels: Kind.Efforts,
        ProviderType: Kind.ProviderType,
        ToldModelBy: "the request's model");

    /// <summary>The client's kind, as the tool builds it in.</summary>
    private static AgentClientKind Kind => AgentProviders.ClientKinds.First(kind => kind.Name == ClientName);

    /// <inheritdoc/>
    public string Name => ClientName;

    /// <inheritdoc/>
    public AgentClientCapabilities Capabilities => Descriptor;

    /// <inheritdoc/>
    public Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);

        if (ToolLoopTerms.Unstartable(run, Kind) is { } why)
        {
            return Task.FromResult(AgentEndings.NotStarted(ClientName, AgentNotStartedBecause.ConfigurationWrong, why, run.Model, run.Mask));
        }

        var entries = run.Entries!;
        var provider = new ClaudeApiProvider(_connection, entries.Provider.Api, ClaudeApiSettings.Of(entries.Client));

        return _loop.RunAsync(run, events, provider, ToolLoopTerms.Of(ClientName, entries), cancellationToken);
    }
}
