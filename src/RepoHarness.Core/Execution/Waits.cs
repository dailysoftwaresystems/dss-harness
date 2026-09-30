namespace RepoHarness.Core.Execution;

/// <summary>How long a wait that looks again until a deadline waits each time.</summary>
internal static class Waits
{
    /// <summary>
    /// <paramref name="wanted"/>, or what is <paramref name="left"/> of the time allowed where that is less - never below
    /// nothing, since time passes between the look that found some left and the wait.
    /// </summary>
    /// <param name="wanted">How long it would wait: its poll, or a settle.</param>
    /// <param name="left">What is left of the time it is allowed.</param>
    public static TimeSpan Shorter(TimeSpan wanted, TimeSpan left)
        => left <= TimeSpan.Zero ? TimeSpan.Zero : wanted < left ? wanted : left;
}
