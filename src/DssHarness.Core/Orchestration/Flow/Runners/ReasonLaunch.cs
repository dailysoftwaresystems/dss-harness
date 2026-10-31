using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Runners;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What an agent's run is launched with beyond its model - a reason step's, the advisor's, the reporter's and a
/// side-task's alike: where a run of the engine's works, the order its model is reached by with each entry's credential,
/// the Claude Code directories no action of the agent's reaches, the mask its records and its answer are held to, and the
/// program a client that starts one starts. The one builder of each for every launch of an agent, so a rule of where an
/// agent works or what it may not reach is written once.
/// </summary>
/// <param name="fileSystem">Makes a run's work directory and finds the tool's own Claude Code directory.</param>
/// <param name="platform">This machine, whose home holds that directory.</param>
/// <param name="keys">The slot every key and secret is read from.</param>
/// <param name="updates">Makes ready, or only finds, the Claude Code a run starts; null where nothing starts one.</param>
public sealed class ReasonLaunch(IFileSystem fileSystem, IHostPlatform platform, AgentKeyStore keys, ClaudeCodeUpdateService? updates)
{
    /// <summary>
    /// Where an agent's run works: its agent's worktree where it has one - at the path its record resolves under the
    /// worktrees root it was made under, never the record's address - which is also where it starts; else, for an agent
    /// that reads the main tree or none at all, the main checkout, which it only reads, starting in its own work
    /// directory. Its work directory and the run's directory either way. The work directory is made here, before any
    /// client is handed the workspace: a run that starts in it - the advisor's and the reporter's, whose directories no
    /// agent of the run's made - starts nowhere else, and a program is never started in a directory that is not there.
    /// </summary>
    /// <param name="record">The agent's record; null where the run works as no agent of the run's.</param>
    /// <param name="repository">The repository's layout, the main checkout's.</param>
    /// <param name="workDirectory">The run's own work directory.</param>
    /// <param name="runDirectory">The run's directory.</param>
    public AgentWorkspace Workspace(AgentRecord? record, HarnessLayout repository, string workDirectory, string runDirectory)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);

        var worktree = record?.WorktreePath(repository);
        fileSystem.CreateDirectory(workDirectory);

        return new AgentWorkspace(worktree, workDirectory, repository.MainCheckoutRoot, [], worktree ?? workDirectory, runDirectory);
    }

    /// <summary>The tool's own Claude Code directory on this machine: <c>CLAUDE_CONFIG_DIR</c> of its process where set, else <c>~/.claude</c>.</summary>
    public string ToolClaudeDirectory => new ClaudeTranscripts(fileSystem, platform).ConfigDirectory;

    /// <summary>The running order of <paramref name="resolution"/>'s entries, each with its credential read from the slot (<see cref="AgentStepOrder.Of"/>).</summary>
    /// <param name="providers">The configuration's providers and tiers, resolved.</param>
    /// <param name="resolution">The run's resolution.</param>
    /// <param name="layout">The repository, whose slot holds the keys.</param>
    /// <param name="spent">What each key spent this week, where the configuration balances them; null where it does not.</param>
    public IReadOnlyList<AgentRunEntry> Order(
        AgentProviders providers,
        AgentResolution resolution,
        HarnessLayout layout,
        IReadOnlyDictionary<(string Provider, string Key), decimal>? spent = null)
        => AgentStepOrder.Of(providers, resolution, layout, keys, ToolClaudeDirectory, spent);

    /// <summary>
    /// Every Claude Code directory the agent's ground keeps each of its actions from (<see cref="Agents.Policy.AgentStepGround"/>):
    /// each one a client of the run's order reads, and the tool's own always, whatever client the run is on - its
    /// credentials and its settings are this machine's, and no agent reads them.
    /// </summary>
    /// <param name="order">The run's order.</param>
    public IReadOnlyList<string> ClaudeDirectories(IReadOnlyList<AgentRunEntry> order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return [.. order.Select(entry => entry.Credential?.ConfigDirectory).OfType<string>().Append(ToolClaudeDirectory).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The mask the run's records and its answer are held to: the value of every secret the configuration appoints that
    /// the slot answers - each provider's keys, each client's token, each web-search source's key - whatever of them the
    /// run itself is given, and <paramref name="with"/>'s beside them. Null where nothing is masked.
    /// </summary>
    /// <param name="layout">The repository, whose slot holds the secrets.</param>
    /// <param name="providers">The configuration's providers, clients and sources, resolved: what they appoint.</param>
    /// <param name="with">What the run masks beyond the slot's values - every form a web-search key is sent in; null where nothing.</param>
    public ActionValues? Mask(HarnessLayout layout, AgentProviders providers, ActionValues? with = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(providers);

        var appointed = providers.Appointed();
        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in appointed.Where(key => key.Secret).Select(key => key.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (keys.Value(layout, appointed, name) is { Value: { Length: > 0 } value })
            {
                secrets[name] = value;
            }
        }

        var slot = secrets.Count > 0 ? new ActionValues(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), secrets) : null;

        return (slot, with) switch
        {
            ({ } held, { } more) => held.With(more),
            ({ } held, null) => held,
            (null, var more) => more,
        };
    }

    /// <summary>
    /// The program the run's client starts, where its order holds the client that starts one (<c>claude-code</c>): made
    /// ready - installed and brought up to date where it must be - for a step of a run and for a side-task, and only
    /// found, never changed, for the advisor and the reporter, which only read. Null where the order holds no such client,
    /// or nothing here starts one; an answer that is not ready refuses the asker with its own words and code, saying what
    /// to start again in the asker's own noun.
    /// </summary>
    /// <param name="layout">The repository, whose slot may name the program.</param>
    /// <param name="providers">The configuration's providers and clients, resolved.</param>
    /// <param name="order">The run's order.</param>
    /// <param name="asker">What needs the program - a run's step, advisor or reporter, or a side-task - as a refusal names it.</param>
    /// <param name="change">Whether a change may run: true for a step of a run and a side-task, false for the advisor and the reporter, which only read.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    public async Task<ClaudeCodeAnswer?> ProgramAsync(
        HarnessLayout layout,
        AgentProviders providers,
        IReadOnlyList<AgentRunEntry> order,
        ClaudeCodeAsker asker,
        bool change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(asker);

        if (updates is null || !order.Any(entry => string.Equals(entry.Client, ClaudeCodeClient.ClientName, StringComparison.Ordinal)))
        {
            return null;
        }

        var client = providers.ClientOf(ClaudeCodeClient.ClientName);

        return change
            ? await updates.ReadyAsync(layout, providers.Appointed(), client, asker, cancellationToken).ConfigureAwait(false)
            : await updates.FoundAsync(layout, providers.Appointed(), client, asker, cancellationToken).ConfigureAwait(false);
    }
}
