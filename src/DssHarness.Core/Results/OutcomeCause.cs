namespace DssHarness.Core.Results;

/// <summary>
/// Why an outcome ended as it did, where its exit code and its lines cannot say: the one kind of refusal a caller waits
/// on rather than puts right, which is how a step of a run tells a lock it must try again from one it must report.
/// </summary>
public enum OutcomeCause
{
    /// <summary>Nothing beyond what the exit code and the lines already say.</summary>
    None,

    /// <summary>
    /// A lock another run holds refused it. Nothing is wrong with the request: the same one, asked again once that run
    /// has ended, goes through - so a caller that can wait, waits, and one that cannot says who holds it.
    /// </summary>
    LockHeld,
}
