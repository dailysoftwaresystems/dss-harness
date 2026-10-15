using DssHarness.Core.Execution;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Orchestration;

/// <summary>What a request needs of its caller before its --apply goes through.</summary>
public enum RequestStanding
{
    /// <summary>Nothing: the request is as it should be.</summary>
    Sound,

    /// <summary>A consent nobody gave - a cell of its rows that --accept-lost or --accept-over must name.</summary>
    NeedsConsent,

    /// <summary>Something of it must be put right: a path, a row or an option is refused.</summary>
    Refused,
}

/// <summary>What a dry run answers: the outcome the one rule gives it, and how its request stands as that outcome says it.</summary>
/// <param name="Outcome">Its code, its summary and its lines.</param>
/// <param name="Standing">
/// What is wanted of the caller: the request as its verb weighed it, save where what is kept in a worktree that a
/// deletion's removal cannot take as it stands decides the code - refused then, since it must be put right; null where
/// what the command weighs could not be read.
/// </param>
public sealed record DryAnswer(CommandOutcome Outcome, RequestStanding? Standing);

/// <summary>
/// The one rule of what a dry run of fold-agent, delete-agent, refresh-agent or rebase-agent exits with: what its --apply,
/// given the same arguments, would exit with at that moment - 0 only where that --apply would go through. A dry run still
/// weighs the whole request and prints every line it prints; only its exit code, and with it where its summary is
/// printed, follows the rule.
/// </summary>
/// <remarks>
/// What an --apply learns only by doing its work - a file changed after it was weighed, a write that fails - is not in a
/// dry run, so 0 says that nothing known now stops the --apply, never that it will finish.
/// <para>
/// A holder is a fact about one moment: a run can end before the next command starts, and one can arrive after a dry run
/// that saw none, so 0 never promises that the --apply will find the tree free either.
/// </para>
/// </remarks>
public static class DryRuns
{
    /// <summary>
    /// What a dry run answers: <paramref name="planned"/> as its --apply would meet the request now, read downwards, the
    /// first row that applies deciding. A lock file that cannot be read refuses it, since the --apply could not take its
    /// trees either, and a run holding a tree the --apply takes refuses it, whatever else the request needs - save, for
    /// both, where what the command weighs could not be read, which stands above them in the plan's order and keeps its own
    /// code, and where the request is a usage error, which its --apply answers before it takes anything and so keeps its
    /// own code too; either way the holder is named on the dry run's lines as ever. Below them, where what the command
    /// weighs could not be read, or the request is refused, that is the answer as it is; where it needs a consent nobody
    /// gave, the same words and the same lines refused, since the --apply refuses it, writing nothing, until each is
    /// named. Only a sound request goes
    /// further: what is kept in a deletion's worktree that its removal cannot take as it stands - a line of
    /// <see cref="TreeHeld.KeptCopies"/> that <see cref="KeptCopyLine.Stops"/> - leaves it unfinished, its request refused
    /// until that is put right, which no waiting does; and a run or a sweep holding only a copy a deletion removes - a
    /// holder, never a line that only says what is kept in the worktree - leaves it unfinished, saying that. Otherwise
    /// <paramref name="planned"/> itself, as the verb weighed it.
    /// </summary>
    /// <param name="planned">What the verb composed: its summary and its lines, or the failure or refusal it met.</param>
    /// <param name="standing">How the request stands; null where what it weighs could not be read.</param>
    /// <param name="holders">Who holds what the --apply takes, where the verb asked; null where it asked nobody.</param>
    public static DryAnswer Answer(CommandOutcome planned, RequestStanding? standing, TreeHeld? holders = null)
    {
        ArgumentNullException.ThrowIfNull(planned);

        // What cannot be read stands above a lock file that cannot be read and a holder, and so does a usage error, which
        // its --apply answers before it takes anything: either keeps its own code, its lines naming the holder - or why none
        // could be named - as any dry run's do.
        var keepsItsCode = standing is null || planned.ExitCode == HarnessExit.UsageError;

        if (holders?.Unread is { } unread)
        {
            var said = $"whether a run holds what --apply needs cannot be told: {unread}; --apply is refused until the lock file can be read";

            if (keepsItsCode)
            {
                return new(planned with { Details = [.. planned.Details ?? [], said] }, standing);
            }

            return new(CommandOutcome.Refused($"{planned.Message}; {said}", planned.Details), standing);
        }

        if (holders?.HoldsATree == true)
        {
            if (keepsItsCode)
            {
                return new(planned, standing);
            }

            return new(
                CommandOutcome.Refused(
                    standing == RequestStanding.Sound ? $"{planned.Message}, {TreeHolders.Blocked}" : $"{planned.Message}{TreeHolders.AlsoHeld}",
                    planned.Details),
                standing);
        }

        return standing switch
        {
            RequestStanding.NeedsConsent => new(CommandOutcome.Refused(planned.Message, planned.Details), standing),
            RequestStanding.Refused or null => new(planned, standing),
            RequestStanding.Sound when holders?.KeptCopies.Any(line => line.Stops) == true => new(
                CommandOutcome.Failed(HarnessExit.Incomplete, $"{planned.Message}{Unremovable}", planned.Details),
                RequestStanding.Refused),
            RequestStanding.Sound when holders?.HoldsOnlyCopies == true => new(
                CommandOutcome.Failed(HarnessExit.Incomplete, $"{planned.Message}, {TreeHolders.RemovalHeld}", planned.Details),
                standing),
            _ => new(planned, standing),
        };
    }

    /// <summary>
    /// What a sound dry run of a deletion ends with where what is kept in the worktree cannot be removed as it stands - a
    /// copy that could not be told or removed, or copies that could not be looked for, said on the lines above: its
    /// --apply would do what it does before the removal and could not finish it, as every --apply would until that is
    /// put right.
    /// </summary>
    public const string Unremovable = "; --apply would go as far as its removal now and could not finish it, because the copies kept in its worktree cannot "
        + "be removed as they stand, said above: put that right, then run it - or run it now and run delete-agent again once that is done";

    /// <summary>
    /// How a request stands whose measure met <paramref name="met"/> rather than a plan: refused where something of it
    /// must be put right - a refusal, or an argument no command takes; null where what it weighs could not be read.
    /// </summary>
    /// <param name="met">The refusal or the failure the measure met.</param>
    public static RequestStanding? StandingOf(CommandOutcome met)
    {
        ArgumentNullException.ThrowIfNull(met);

        return met.ExitCode is HarnessExit.Refused or HarnessExit.UsageError ? RequestStanding.Refused : null;
    }
}
