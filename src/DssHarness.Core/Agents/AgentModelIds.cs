namespace DssHarness.Core.Agents;

/// <summary>
/// The one rule a message's model is held to on every client: the model a step's configuration and the engine chose, or
/// no model at all - one written by another ends the step <see cref="AgentRunStatus.WrongModel"/>.
/// </summary>
public static class AgentModelIds
{
    /// <summary>Whether a model a message names is the one asked for: the same id, or the id with a date after it.</summary>
    /// <param name="asked">The model's id, as it was asked for.</param>
    /// <param name="named">The model a message names.</param>
    public static bool Same(string asked, string named)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(named);

        return string.Equals(asked, named, StringComparison.Ordinal)
            || (named.StartsWith(asked + "-", StringComparison.Ordinal) && named.Length == asked.Length + 9 && named[(asked.Length + 1)..].All(char.IsAsciiDigit));
    }
}
