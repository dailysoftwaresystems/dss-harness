using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Repository;
using DssHarness.Core.Web.Search;

using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// What a side-task's agent runs under: every limit of a run's dropped - no restriction list, no allow list of pages, the main checkout its writable root, the worktree
/// rules and the protected paths off, every capability - and the built-in refusals kept, as they are kept for every
/// agent: the root actions, every credential slot and every place the user keeps credentials in, the tool's own state (written by its verbs alone, and the verbs are
/// open to it), <c>Write(.git/**)</c>, and <c>run-side-task</c>. The one builder of a side-task's ground and its web
/// search, beside the policy that decides by one.
/// </summary>
public static class SideTaskPolicy
{
    /// <summary>
    /// What the side-task's agent ran under, as its record says it: the policy as resolved for it, one line.
    /// </summary>
    public const string Said = "no limits of a run's: no restriction list, no allow list of pages, the main tree its writable root; the built-in refusals kept";

    /// <summary>
    /// The ground of a side-task working over the main checkout: the main tree readable and writable, every capability,
    /// no restriction and no allow list, the worktree rules and the protected paths off - git's own files excepted,
    /// which no ground opens - and the tool's own state, every credential slot of the main checkout
    /// (<see cref="HarnessLayout.SecretSlotDirectoriesUnder"/>), every Claude Code directory and every place the user
    /// keeps credentials in still no action of its reaches.
    /// </summary>
    /// <param name="layout">The repository's layout: the main checkout is both where the agent works and what it writes.</param>
    /// <param name="config">The configuration, whose worktrees root and state paths are guarded.</param>
    /// <param name="claudeDirectories">Every Claude Code directory a client of the side-task uses, and the tool's own.</param>
    /// <param name="credentialPlaces">Every place the user keeps credentials in on this machine (<see cref="Isolation.UserCredentials.Of"/>).</param>
    /// <param name="liveRun">
    /// The name of the run whose owner is live over the tree, asked where the policy holds the agent off HEAD; null
    /// where the tree tells of none. Checked at each call, so a run that starts while the chat is open is held to from
    /// its first step, and one that ends releases it.
    /// </param>
    public static AgentPolicyGround Ground(
        HarnessLayout layout,
        HarnessConfig config,
        IReadOnlyList<string> claudeDirectories,
        IReadOnlyList<Isolation.HiddenPlace> credentialPlaces,
        Func<string?>? liveRun = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(claudeDirectories);
        ArgumentNullException.ThrowIfNull(credentialPlaces);

        var tree = layout.MainCheckoutRoot;

        // The main checkout is read and written alike: the one tree a side-task works over, and the tree whose
        // configuration an edit of it changes.
        return new AgentPolicyGround(
            WorkingDirectory: tree,
            ReadRoots: [tree],
            WritableRoots: [tree],
            SlotDirectories: HarnessLayout.SecretSlotDirectoriesUnder(tree),
            ClaudeCodeDirectories: [.. claudeDirectories.Distinct(StringComparer.OrdinalIgnoreCase)],
            CredentialPlaces: credentialPlaces,
            Restrictions: [],
            Capabilities: AgentPolicyGround.EveryCapability,
            WebDomains: null,
            Writes: null,
            ReporterRoot: null,
            WorktreeRules: false,
            ProtectedPaths: false,
            StateDirectories: HarnessLayout.StatePathsUnder(tree, config.Worktrees?.Root ?? WorktreeSettings.DefaultRoot),
            SideTask: true)
        {
            LiveRun = liveRun,
        };
    }

    /// <summary>
    /// The web search a side-task is given: every configured source turned on (<see cref="WebSearchSources.On"/>), in the
    /// configuration's order, with the bounds the configuration holds - <c>maxPerStep</c> dropped, for no bound of a run's
    /// limits a side-task, and a source's own pace its own still. Each source's key read as a step's is.
    /// </summary>
    /// <param name="sources">The configuration's web search, resolved.</param>
    /// <param name="keys">Each source's key, by its name, as <see cref="WebSearchKeys"/> read it.</param>
    public static AgentWebSearch WebSearch(WebSearchSources sources, IReadOnlyDictionary<string, WebSearchKey> keys)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(keys);

        return new([.. sources.On], sources.ResultsPerSource, int.MaxValue, keys);
    }
}
