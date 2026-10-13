using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Core.Repository;

/// <summary>
/// Whether git ignores what would be made in one of a tree's root slots (<see cref="HarnessLayout.RootSlots"/>), asked
/// before anything is made there, on the machine that makes it: everything made in a slot is that machine's own, and
/// committed by <c>git add -A</c> it would travel to every clone. Where git does not ignore it, the work is refused,
/// naming <c>init</c>, which keeps the slot in git and everything made in it out; nothing here writes an ignore rule of
/// its own.
/// </summary>
public static class SlotIgnored
{
    /// <summary>
    /// Why <paramref name="probe"/> may not be made in <paramref name="tree"/> - git does not ignore it - or
    /// <see langword="null"/> where git ignores it. What refuses says what was not done after it.
    /// </summary>
    /// <param name="git">Asks git.</param>
    /// <param name="tree">The tree, whose ignore rules are asked.</param>
    /// <param name="probe">What would be made, relative to the tree with forward separators, its slot first.</param>
    /// <param name="what">What would be committed, as the refusal names it: <c>what orchestrator 'o1' keeps</c>.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    /// <exception cref="Results.HarnessException">git could not be asked: never read as an answer.</exception>
    public static async Task<string?> RefusalAsync(IGitClient git, string tree, string probe, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);

        if (await git.IsIgnoredAsync(tree, probe, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var slot = probe.Split('/')[0];

        return $"git does not ignore '{probe}', so {what} would be committed. Run '{ToolPackage.Command} init', which keeps {slot} in git and everything made "
            + "in it out.";
    }
}
