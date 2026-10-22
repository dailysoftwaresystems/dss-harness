using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// What the engine answers a call Claude Code asks it about (<c>can_use_tool</c>): the call read as the action its
/// tool's capability is - a read, a write, a command - of what its input names, by the one table of
/// <see cref="ClaudeCodeTools"/>, and decided by the same <see cref="AgentPolicy"/> every client asks, never re-decided here.
/// </summary>
/// <remarks>
/// A tool the launch was not given is refused by name, whatever the policy would say of its input, and so is a call whose
/// input does not name what the action needs: nothing is allowed that the engine could not see whole.
/// </remarks>
public static class ClaudeToolDecider
{
    /// <summary>The rule a refusal of this decider's own names: the launch's tools, and what each call must say.</summary>
    public const string ToolsRule = "the launch's tools";

    /// <summary>Decides one call.</summary>
    /// <param name="policy">The agent's policy.</param>
    /// <param name="offered">The tools the launch was given.</param>
    /// <param name="tool">The tool the call is of, as Claude Code names it.</param>
    /// <param name="input">The call's input, as asked.</param>
    /// <param name="workingDirectory">Where a search that names no path searches: the agent's working directory.</param>
    public static AgentPolicyAnswer Decide(AgentPolicy policy, IReadOnlyList<string> offered, string tool, JsonNode? input, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(tool);

        if (!offered.Contains(tool, StringComparer.Ordinal))
        {
            return Refused($"'{tool}' is no tool this agent was given; it was given: {(offered.Count == 0 ? "none" : string.Join(", ", offered))}");
        }

        if (ClaudeCodeTools.Find(tool) is not { } row)
        {
            return Refused($"'{tool}' is a tool whose calls this tool cannot read, so none of them runs");
        }

        if (SubjectOf(tool, input, workingDirectory) is not { } subject)
        {
            return Refused($"a call of '{tool}' that names no {row.Tool.Subject} cannot be decided, so it does not run");
        }

        return row.Capability switch
        {
            AgentCapability.Read => policy.Read(subject),
            AgentCapability.Write => policy.Write(subject),
            AgentCapability.Run => policy.Run(subject),
            _ => Refused($"'{tool}' is a tool whose calls this tool cannot read, so none of them runs"),
        };
    }

    /// <summary>
    /// What a call of <paramref name="tool"/> is of, as it is decided and as a denial names it: the path or the command
    /// its input names, the working directory for a search that names none; null where the input names none it needs, and
    /// for a tool the table does not name.
    /// </summary>
    /// <param name="tool">The tool the call is of, as Claude Code names it.</param>
    /// <param name="input">The call's input, as asked.</param>
    /// <param name="workingDirectory">The agent's working directory.</param>
    public static string? SubjectOf(string tool, JsonNode? input, string workingDirectory)
        => ClaudeCodeTools.Find(tool) is { Tool: var named }
            ? input is JsonObject holding && holding[named.Subject] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
                ? text
                : named.WorkingDirectoryWhereNone ? workingDirectory : null
            : null;

    /// <summary>A refusal of this decider's own.</summary>
    private static AgentPolicyAnswer.Denied Refused(string reason)
        => new(ToolsRule, AgentPolicySource.Builtin, reason);
}
