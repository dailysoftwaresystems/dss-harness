using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Results;

namespace DssHarness.Core.Repository;

/// <summary>
/// Whether git ignores what would be made in one of a tree's root slots (<see cref="HarnessLayout.RootSlots"/>), asked
/// before anything is made there, on the machine that makes it: everything made in a slot is that machine's own, and
/// committed by <c>git add -A</c> it would travel to every clone. Where git does not ignore it, the work is refused,
/// naming <c>init</c>, which keeps the slot in git and everything made in it out; nothing here writes an ignore rule of
/// its own. Where git cannot say, the tree cannot hold the work (<see cref="IsIgnoredAsync"/>): the one answer every
/// command that asks before it writes in a tree gives.
/// </summary>
public static class SlotIgnored
{
    /// <summary>
    /// Whether git ignores <paramref name="probe"/> in <paramref name="tree"/>, asked before <paramref name="what"/> is
    /// written there.
    /// </summary>
    /// <param name="git">Asks git.</param>
    /// <param name="tree">The tree, whose ignore rules are asked.</param>
    /// <param name="probe">A path inside what would be written, relative to the tree with forward separators.</param>
    /// <param name="what">What would be written, as the failure names it: <c>what orchestrator 'o1' keeps</c>.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    /// <exception cref="HarnessException">
    /// git could not answer in the tree (<see cref="HarnessExit.HostUnavailable"/>), in git's own words.
    /// </exception>
    /// <remarks>
    /// A question that could not be asked is no answer either way, and no verdict on the work. It is about this tree - a
    /// worktree whose link to its repository is broken, a copy git can no longer read - so the work cannot run in it, and
    /// a leg on another tree still reports: a failure of git's own read as the leg failing, and a refusal of the run
    /// ended every leg over one tree's repository.
    /// </remarks>
    public static async Task<bool> IsIgnoredAsync(IGitClient git, string tree, string probe, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);

        try
        {
            return await git.IsIgnoredAsync(tree, probe, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"git could not say whether it ignores '{probe}' in '{tree}', so whether {what} would be committed is unknown. "
                + $"Nothing has run. {ex.Message}");
        }
    }

    /// <summary>
    /// Why <paramref name="probe"/> may not be made in <paramref name="tree"/> - git does not ignore it - or
    /// <see langword="null"/> where git ignores it. What refuses says what was not done after it.
    /// </summary>
    /// <param name="git">Asks git.</param>
    /// <param name="tree">The tree, whose ignore rules are asked.</param>
    /// <param name="probe">What would be made, relative to the tree with forward separators, its slot first.</param>
    /// <param name="what">What would be committed, as the refusal names it: <c>what orchestrator 'o1' keeps</c>.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    /// <exception cref="HarnessException">git could not be asked (<see cref="IsIgnoredAsync"/>): never read as an answer.</exception>
    public static async Task<string?> RefusalAsync(IGitClient git, string tree, string probe, string what, CancellationToken cancellationToken)
    {
        if (await IsIgnoredAsync(git, tree, probe, what, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var slot = probe.Split('/')[0];

        return $"git does not ignore '{probe}', so {what} would be committed. Run '{ToolPackage.Command} init', which keeps {slot} in git and everything made "
            + "in it out.";
    }
}
