namespace RepoHarness.Core.Execution;

/// <summary>
/// The exit codes the commands that run legs — <c>build</c>, <c>test</c>, <c>run</c> and
/// <c>check-mutations</c> — add to the shared ones. Each stands for a verdict whose remedy differs from
/// every other verdict's, which is the whole reason it is not reported as a plain failure.
/// </summary>
/// <remarks>
/// These live in the range 1-9, reserved for a command's own contract, like
/// <see cref="Git.VerifyGitStatus"/> and <see cref="Legs.LegsExit"/>. A shared code landing in that
/// range collides with a command contract without anyone deciding it should, which
/// <c>ExitCodeContractTests</c> exists to catch. <see cref="Violated"/>, <see cref="Survived"/> and
/// <see cref="Unattributed"/> are reached only by a mutation arm, so only <c>check-mutations</c> exits
/// with them.
/// </remarks>
public static class LegExit
{
    /// <summary>
    /// A mutation arm's declaration did not hold: its site, its target, the cases its mutation reddened or ran, or its
    /// diagnostic, or a mutation declared to stop the build built. Remedy: fix the arm's declaration, or the code it
    /// guards. Reported apart from a plain failure because the code under test may well be right: what is wrong is
    /// what the registry says about it, or the code the arm guards moved from under the declaration.
    /// </summary>
    public const int Violated = 1;

    /// <summary>
    /// A mutation arm's mutation built and ran, and no case failed. Remedy: strengthen the test that should have
    /// failed. The one verdict that says the tests themselves are too weak, where every other says the code, the
    /// declaration or the machine is at fault.
    /// </summary>
    public const int Survived = 2;

    /// <summary>
    /// Files the tests read changed while they ran, or whether they held still could not be
    /// established; or, for a leg on another machine, a file its copy needed changed, or was
    /// removed, after the run began - before it was carried there, or while the tree was being
    /// read. Remedy: let the tree settle, then run again. Reported apart from a failure because it
    /// says nothing about the code: what was tested was no tree that ever existed, or, where the
    /// copy could not be made, nothing was.
    /// </summary>
    public const int InputsMoved = 3;

    /// <summary>
    /// Another process used the leg's build directory while it ran. Remedy: wait for the other run.
    /// A test run started by hand in a shared build directory while a gate ran turned a green suite
    /// red, with four test processes live at once, and no lock can see a tool nobody locked.
    /// </summary>
    public const int Contended = 4;

    /// <summary>
    /// The command exited zero, but the pattern that proves it ran never matched its output.
    /// Remedy: find out what actually ran. A wrapper that reports success without evidence is
    /// indistinguishable from one that never ran.
    /// </summary>
    public const int Unwitnessed = 5;

    /// <summary>
    /// Another live run owns this run's log path, so this run cannot write the evidence for its own
    /// verdict. Remedy: find out which run still owns this leg's logs. Distinct from a
    /// held lock, which stopped the run before it started: here the work could run and its record
    /// could not be kept.
    /// </summary>
    public const int LogHeld = 6;

    /// <summary>
    /// A heavy leg waited its machine's <c>maxWaitMinutes</c> for a heavy-leg slot, or holding one for the memory in
    /// use to fall below the limit, or for room for its build beside what the other admitted legs claim, and nothing of
    /// it ran. Remedy: wait for the heavy legs its line names, free memory or room on the filesystem it names, or raise
    /// the machine's limits. Distinct from a held lock, which is about one tree and one variant: this is about the whole
    /// machine.
    /// </summary>
    public const int NotAdmitted = 7;

    /// <summary>
    /// A mutation arm's run failed, and nothing ties the failure to a case: the runner wrote no report, or an unreadable
    /// one, or exited failing with a report naming no failing case, or ran past its bound and was stopped. Remedy:
    /// contain the crash or hang in the case, or make the runner write its report. Distinct from a survived arm, whose
    /// run passed: here something failed, and the evidence of what is missing.
    /// </summary>
    public const int Unattributed = 8;
}
