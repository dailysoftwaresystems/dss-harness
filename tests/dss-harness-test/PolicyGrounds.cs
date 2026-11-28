using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// Policy grounds as the tests build them: every field the engine gives, with the switches on, every capability, no
/// narrowing and no state where a test says nothing else.
/// </summary>
internal static class PolicyGrounds
{
    /// <summary>The start tree a ground's writes are read against where a test names none: one that holds no directory.</summary>
    public static TreeDirectories NoDirectories { get; } = TreeDirectories.Of("start", []);

    public static AgentPolicyGround Of(
        string workingDirectory,
        IReadOnlyList<string> readRoots,
        IReadOnlyList<string> writableRoots,
        IReadOnlyList<string> slotDirectories,
        IReadOnlyList<string> claudeCodeDirectories,
        IReadOnlyList<AgentRestriction> restrictions,
        IReadOnlyList<AgentCapability>? capabilities = null,
        IReadOnlyList<string>? webDomains = null,
        IReadOnlyList<string>? writes = null,
        string? reporterRoot = null,
        bool worktreeRules = true,
        bool protectedPaths = true,
        IReadOnlyList<string>? stateDirectories = null,
        IReadOnlyList<HiddenPlace>? credentialPlaces = null,
        TreeDirectories? writesTree = null)
        => new(
            workingDirectory,
            readRoots,
            writableRoots,
            slotDirectories,
            claudeCodeDirectories,
            credentialPlaces ?? [],
            restrictions,
            capabilities ?? AgentPolicyGround.EveryCapability,
            webDomains,
            writes,
            reporterRoot,
            worktreeRules,
            protectedPaths,
            stateDirectories ?? [])
        {
            WritesTree = writes is null ? null : writesTree ?? NoDirectories,
        };
}
