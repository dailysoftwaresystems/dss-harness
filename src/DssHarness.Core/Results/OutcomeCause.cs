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
    /// A lock another run holds refused it, and nothing else did. Nothing is wrong with the request: the same one, asked
    /// again once that run has ended, goes through - so a caller that can wait, waits, and one that cannot says who holds
    /// it. Set where exactly that is the answer: the <c>--apply</c> of refresh-, rebase-, fold- or delete-agent refused at
    /// its hold (13); their dry run refused for a run holding a tree that <c>--apply</c> takes, where the request is
    /// otherwise sound and nothing kept in the worktree stops its removal (13); a run of legs - build, test, run, a sweep,
    /// clean - that exits 13 with every leg that did not pass refused-locked (<c>LedgerReport.CauseGiven</c>); and delete-worktree
    /// refused over a copy kept in the worktree that a run or a sweep holds (13). Never for a run whose logs another run
    /// owns (6), which is pointed elsewhere, a lock file that cannot be read, or a run holding only copies a deletion
    /// removes (21).
    /// </summary>
    LockHeld,
}
