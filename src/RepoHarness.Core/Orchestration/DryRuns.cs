using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Orchestration;

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
    /// What a dry run answers: <paramref name="planned"/> as its --apply would meet the request now. Where what the
    /// command weighs could not be read, or the request is refused, that is the answer as it is; where it needs a consent
    /// nobody gave, the same words and the same lines refused, since the --apply refuses it, writing nothing, until each is
    /// named. A run holding a tree the --apply takes refuses it, whatever else the request needs - save where what the
    /// command weighs could not be read, which stands above a holder in the plan's order and keeps its own code, the holder
    /// named on the dry run's lines as ever - and a lock file that cannot be read does too, since the --apply could not
    /// take its trees either; a run holding only a copy a deletion removes leaves it saying that, unfinished. Otherwise
    /// <paramref name="planned"/> itself.
    /// </summary>
    /// <param name="planned">What the verb composed: its summary and its lines, or the failure or refusal it met.</param>
    /// <param name="standing">How the request stands; null where what it weighs could not be read.</param>
    /// <param name="holders">Who holds what the --apply takes, where the verb asked; null where it asked nobody.</param>
    public static CommandOutcome Outcome(CommandOutcome planned, RequestStanding? standing, TreeHeld? holders = null)
    {
        ArgumentNullException.ThrowIfNull(planned);

        if (holders?.Unread is { } unread)
        {
            var said = $"whether a run holds what --apply needs cannot be told: {unread}; --apply is refused until the lock file can be read";

            if (standing is null)
            {
                return planned with { Details = [.. planned.Details ?? [], said] };
            }

            return CommandOutcome.Refused($"{planned.Message}; {said}", planned.Details);
        }

        if (holders?.HoldsATree == true)
        {
            // What cannot be read stands above a holder: a dry run whose own reading failed keeps its failure's code,
            // and its lines name the holder as any dry run's do.
            if (standing is null)
            {
                return planned;
            }

            return CommandOutcome.Refused(
                standing == RequestStanding.Sound ? $"{planned.Message}, {TreeHolders.Blocked}" : $"{planned.Message}{TreeHolders.AlsoHeld}",
                planned.Details);
        }

        return standing switch
        {
            RequestStanding.NeedsConsent => CommandOutcome.Refused(planned.Message, planned.Details),
            RequestStanding.Refused or null => planned,
            RequestStanding.Sound when holders?.HoldsOnlyCopies == true => CommandOutcome.Failed(
                HarnessExit.Incomplete,
                $"{planned.Message}, {TreeHolders.RemovalHeld}",
                planned.Details),
            _ => planned,
        };
    }

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
