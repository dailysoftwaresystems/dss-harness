using System.Diagnostics;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Orchestration;

/// <summary>How a path both trees changed is resolved: the one decision, whichever command meets it.</summary>
internal enum TwoChangeRule
{
    /// <summary>As text, which nobody asked to have merged: what a merge gives is measured and said, and the path is refused.</summary>
    Refused,

    /// <summary>As text, named with <see cref="FoldAllowances.MergeOption"/>: a clean merge of it is taken.</summary>
    Text,

    /// <summary>The arm registry, which is never merged by line.</summary>
    Arms,

    /// <summary>An anchor registry, which changes only through the rows an agent files: never merged.</summary>
    Never,
}

/// <summary>What decides how a path both trees changed is resolved: the arm registry's path, the anchor registries', and the paths named with <see cref="FoldAllowances.MergeOption"/>.</summary>
/// <param name="ArmRegistry">The configured arm registry, as git names it; null where the repository declares none.</param>
/// <param name="AnchorRegistries">The anchor registries, as git names them.</param>
/// <param name="Merge">The paths named with <see cref="FoldAllowances.MergeOption"/>, as git names them.</param>
internal sealed record TwoChangePolicy(string? ArmRegistry, IReadOnlySet<string> AnchorRegistries, IReadOnlySet<string> Merge)
{
    /// <summary>The policy of a command run in <paramref name="context"/> that was given <paramref name="merge"/>.</summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="merge">The paths named with <see cref="FoldAllowances.MergeOption"/>, as they were typed.</param>
    public static TwoChangePolicy Of(HarnessContext context, IEnumerable<string> merge)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(merge);

        return new TwoChangePolicy(
            context.Config.Mutations.Registry is { Length: > 0 } registry ? PathPatterns.Normalize(registry) : null,
            new[] { context.Config.Anchors.PendingAnchorsPath, context.Config.Anchors.DoneAnchorsPath }.Select(AnchorRegistryLocator.Normalize).ToHashSet(StringComparer.Ordinal),
            merge.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>How <paramref name="path"/> is resolved where both trees changed it.</summary>
    /// <param name="path">The path, as git names it.</param>
    public TwoChangeRule RuleFor(string path)
        => string.Equals(path, ArmRegistry, StringComparison.Ordinal) ? TwoChangeRule.Arms
            : AnchorRegistries.Contains(path) ? TwoChangeRule.Never
            : Merge.Contains(path) ? TwoChangeRule.Text
            : TwoChangeRule.Refused;

    /// <summary>
    /// Each path named with <see cref="FoldAllowances.MergeOption"/> that can never be merged by line, whatever the command weighs - an
    /// anchor registry, the arm registry - with why it is refused, in order.
    /// </summary>
    public IEnumerable<(string Path, string Why)> NamedNeverMerged()
        => Merge.Order(StringComparer.Ordinal).Select(path => (Path: path, Why: RuleFor(path) switch
        {
            TwoChangeRule.Never => $"{FoldAllowances.MergeOption} '{path}' names an anchor registry, which a fold changes only through the rows the agent files, never as a file",
            TwoChangeRule.Arms => $"{FoldAllowances.MergeOption} '{path}' names the arm registry, which is never merged by line: drop it",
            _ => null,
        })).Where(named => named.Why is not null).Select(named => (named.Path, named.Why!));

    /// <summary>The paths named with <see cref="FoldAllowances.MergeOption"/> that a merge by line could be taken for, in order.</summary>
    public IEnumerable<string> NamedAsText() => Merge.Where(path => RuleFor(path) == TwoChangeRule.Text).Order(StringComparer.Ordinal);
}

/// <summary>One side of a path: a blob git holds, a file on disk, or bytes in hand; and whether it could be run.</summary>
/// <param name="Content">What it holds.</param>
/// <param name="Executable">Whether it could be run, where the platform keeps such a bit; never on one that keeps none.</param>
internal sealed record TwoChangeSide(GitContent Content, bool Executable);

/// <summary>What both trees held at a path, as far as the command that asks knows it: one of the two kinds below, and no other.</summary>
internal abstract record TwoChangeAncestor
{
    private TwoChangeAncestor(bool executable) => Executable = executable;

    /// <summary>Whether it could be run, where the platform keeps such a bit.</summary>
    public bool Executable { get; }

    /// <summary>What a commit holds at the path: the agent's base, for a path it shares nothing for.</summary>
    /// <param name="Blob">The blob the commit holds there.</param>
    /// <param name="Executable">Whether the commit's entry is one that can be run.</param>
    public sealed record Committed(string Blob, bool Executable) : TwoChangeAncestor(Executable);

    /// <summary>
    /// What the agent's seed records as shared at the path: the digest of the bytes both trees held, which are kept beside
    /// the seed, or looked for in the main tree's history where a build that kept none recorded them.
    /// </summary>
    /// <param name="Digest">The SHA-256 the seed records.</param>
    /// <param name="Executable">Whether the seed records the file as one that can be run.</param>
    public sealed record Shared(string Digest, bool Executable) : TwoChangeAncestor(Executable);
}

/// <summary>A path both trees changed, with its three versions, asked what a merge of them gives.</summary>
/// <param name="Path">The path, as git names it.</param>
/// <param name="Ancestor">What both held; null where neither held a file there.</param>
/// <param name="Ours">The side the merge is made into: the main tree's.</param>
/// <param name="Theirs">The side merged in: the agent's.</param>
/// <param name="Tree">The work tree whose attributes and configuration say how the path is stored and checked out: the one the merge is written into.</param>
internal sealed record TwoChangeAsk(string Path, TwoChangeAncestor? Ancestor, TwoChangeSide Ours, TwoChangeSide Theirs, string Tree);

/// <summary>
/// Why no three-way merge of a path is offered: a closed list, each reason one member with its sentence, so every command
/// that meets such a path words it alike.
/// </summary>
internal sealed record NoMerge
{
    private NoMerge(string said) => Said = said;

    /// <summary>The reason, to end the sentence "no three-way merge is offered: ".</summary>
    public string Said { get; }

    /// <summary>Neither tree held a file at the path before: there is nothing to merge from.</summary>
    public static NoMerge BothMade { get; } = new("both trees made it, and neither held a file there before");

    /// <summary>The seed records bytes no copy was kept of, and the main tree's history does not hold them, as far as it could be read.</summary>
    /// <param name="digest">The SHA-256 the seed records.</param>
    /// <param name="versions">How many versions of the path were read.</param>
    /// <param name="unread">How many of them git could not check out to compare, and why the first could not; null where none.</param>
    public static NoMerge NotInHistory(string digest, int versions, (int Count, string Why)? unread = null)
        => new($"the bytes both trees shared (SHA-256 {Short(digest)}) were recorded by a build that kept no copy of them, and none of the "
            + $"{ReportText.Counted(versions, "version")} of the path in the last {ReportText.Counted(TwoChanges.MostCommits, "commit")} of the main tree that changed it is those bytes"
            + (unread is var (count, why) ? $" - {count} of them could not be checked out to compare: {why.TrimEnd('.')}" : string.Empty));

    /// <summary>The copy kept of what both trees held no longer hashes to its name.</summary>
    /// <param name="file">The kept copy.</param>
    /// <param name="digest">The SHA-256 the seed records.</param>
    public static NoMerge KeptCopyChanged(string file, string digest)
        => new($"its kept copy '{file}' no longer holds the bytes recorded for it (SHA-256 {Short(digest)})");

    /// <summary>The path's <c>merge</c> attribute asks for another merge than git's own of text.</summary>
    /// <param name="value">What git's attributes declare for it.</param>
    public static NoMerge Attribute(string value)
        => new($"its merge attribute is {(value == GitAttributes.Unset ? "unset" : $"'{value}'")}, and only git's own merge of text is offered here");

    /// <summary>A version of it is above the size a merge is offered for.</summary>
    /// <param name="bytes">How many bytes the largest of its versions holds.</param>
    public static NoMerge TooLarge(long bytes)
        => new($"it is {DiskSpace.Size(bytes)}, and a merge is offered for a file up to {DiskSpace.Size(TwoChanges.MostBytes)}");

    /// <summary>git could not bring a version to the form it stores: a filter that failed, for one.</summary>
    /// <param name="why">git's words.</param>
    public static NoMerge NotStored(string why) => new($"git could not read it as it would store it - {why.TrimEnd('.')}");

    /// <summary>git would not merge the three: a binary file, for one.</summary>
    /// <param name="why">git's words.</param>
    public static NoMerge NotMerged(string why) => new($"git would not merge it - {why.TrimEnd('.')}");

    /// <summary>git could not bring the merged bytes to the form a checkout writes.</summary>
    /// <param name="why">git's words.</param>
    public static NoMerge NotCheckedOut(string why) => new($"git could not write the merge as it would check it out - {why.TrimEnd('.')}");

    /// <summary>A digest as a sentence names it: its first eight characters.</summary>
    private static string Short(string digest) => digest.Length > 8 ? $"{digest[..8]}..." : digest;
}

/// <summary>
/// What a merge of one path gives: one of four outcomes, each made by its own factory - clean, with the bytes as the tree
/// would hold them and whether the merged file can be run; conflicting, with what to show; none, with why; or not measured,
/// for a path never merged by line - so no offer holds two of them.
/// </summary>
internal sealed record TwoChangeOffer
{
    private TwoChangeOffer(string path, TwoChangeRule rule, byte[]? working, bool executable, string said, string listed, IReadOnlyList<string> shown, NoMerge? none)
    {
        Path = path;
        Rule = rule;
        Working = working;
        Digest = working is null ? null : FileContentHash.Of(working);
        Executable = executable;
        Said = said;
        Listed = listed;
        Shown = shown;
        None = none;
    }

    /// <summary>The path.</summary>
    public string Path { get; }

    /// <summary>How the path is resolved.</summary>
    public TwoChangeRule Rule { get; }

    /// <summary>The merged bytes as the tree would hold them, where the merge is clean; null otherwise.</summary>
    public byte[]? Working { get; }

    /// <summary>The SHA-256 of <see cref="Working"/>, where the merge is clean.</summary>
    public string? Digest { get; }

    /// <summary>Whether the merged file can be run, where the merge is clean.</summary>
    public bool Executable { get; }

    /// <summary>What the merge gives, as a clause to end the line that refuses the path; empty where nothing is said of one.</summary>
    public string Said { get; }

    /// <summary>The clause after the path in the list of merged paths, where the merge is clean.</summary>
    public string Listed { get; }

    /// <summary>The conflicts, as a block printed under the refusals; empty where there are none.</summary>
    public IReadOnlyList<string> Shown { get; }

    /// <summary>Why no merge is offered; null where one is, or where the rule never asks for one.</summary>
    public NoMerge? None { get; }

    /// <summary>Whether the three merged with no conflict.</summary>
    public bool IsClean => Working is not null;

    /// <summary>A path never merged by line: nothing is measured, and nothing said of a merge.</summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">How it is resolved: a registry's rule.</param>
    public static TwoChangeOffer Unmeasured(string path, TwoChangeRule rule) => new(path, rule, null, false, string.Empty, string.Empty, [], null);

    /// <summary>A path no merge is offered for, with why.</summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">How it is resolved.</param>
    /// <param name="none">Why.</param>
    public static TwoChangeOffer Unoffered(string path, TwoChangeRule rule, NoMerge none)
    {
        ArgumentNullException.ThrowIfNull(none);

        return new(path, rule, null, false, $"no three-way merge is offered: {none.Said}", string.Empty, [], none);
    }

    /// <summary>A merge that conflicts, with what to show of it.</summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">How it is resolved.</param>
    /// <param name="said">What the merge gives, to end the line that refuses the path.</param>
    /// <param name="shown">The conflicts, as a block printed under the refusals.</param>
    public static TwoChangeOffer Conflicting(string path, TwoChangeRule rule, string said, IReadOnlyList<string> shown) => new(path, rule, null, false, said, string.Empty, shown, null);

    /// <summary>A clean merge.</summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">How it is resolved.</param>
    /// <param name="working">The merged bytes as the tree would hold them.</param>
    /// <param name="executable">Whether the merged file can be run.</param>
    /// <param name="said">What the merge gives, to end the line that refuses the path where it is not taken.</param>
    /// <param name="listed">The clause after the path in the list of merged paths.</param>
    public static TwoChangeOffer Clean(string path, TwoChangeRule rule, byte[] working, bool executable, string said, string listed)
    {
        ArgumentNullException.ThrowIfNull(working);

        return new(path, rule, working, executable, said, listed, [], null);
    }

    /// <summary>The line that refuses the path, ended with what a merge of it gives.</summary>
    /// <param name="reason">The line as it reads without a word of a merge.</param>
    public string After(string reason) => Said.Length == 0 ? reason : $"{reason}; {Said}";
}

/// <summary>What a file is: its content, and whether it can be run, where the platform keeps such a bit.</summary>
/// <param name="Digest">The SHA-256 of its content.</param>
/// <param name="Executable">Whether it can be run; never on a platform that keeps no such bit.</param>
public readonly record struct FileIdentity(string Digest, bool Executable);

/// <summary>A path merged three ways, as a plan holds it until it is written.</summary>
/// <param name="Path">The path.</param>
/// <param name="Working">The merged bytes, as the tree they are written into holds them.</param>
/// <param name="Digest">Their SHA-256.</param>
/// <param name="Executable">Whether the merged file can be run.</param>
/// <param name="Listed">The clause after the path in the list of merged paths.</param>
/// <param name="MainWeighed">What the main tree's file was when it was weighed: what the merge may be written over.</param>
/// <param name="AgentWeighed">What the agent's file was when it was weighed.</param>
public sealed record MergedPath(string Path, byte[] Working, string Digest, bool Executable, string Listed, FileIdentity MainWeighed, FileIdentity AgentWeighed);

/// <summary>
/// The one place a path both trees changed is resolved, for every command that meets one: what a three-way merge of its
/// versions gives, measured in full before anything is written, and said whether or not anybody asked for it. Asking -
/// <see cref="FoldAllowances.MergeOption"/> - changes one thing: whether a clean merge is taken or refused.
/// </summary>
/// <remarks>
/// git merges; nothing here compares lines. The three versions are brought to the form git stores, merged there, and the
/// result brought to the form a checkout writes, so a file one tree holds with other line endings merges as git itself
/// would merge it. Whether the file can be run is merged with its content (<see cref="MergedBit"/>). Never merged,
/// whatever is named: a path neither tree held a file at before, one git reads as binary or will not merge, one whose
/// <c>merge</c> attribute asks for another merge, and a file above <see cref="MostBytes"/>.
/// </remarks>
/// <param name="git">Stores, merges and checks out.</param>
/// <param name="files">Reads how large a side's file is.</param>
/// <param name="kept">The bytes the agent shares, kept beside its seed: what both trees held at a shared path.</param>
/// <param name="main">The main checkout's root, whose history is read where no copy was kept.</param>
/// <param name="keeping">Whether bytes found in that history are kept, so they are looked for once: true of a command that writes.</param>
internal sealed class TwoChanges(IGitClient git, IFileSystem files, SharedCopies kept, string main, bool keeping)
{
    /// <summary>The most bytes of one file a merge is offered for, and so the most kept of one a seed records.</summary>
    public const long MostBytes = 16 * 1024 * 1024;

    /// <summary>The most versions of a path read from the main tree's history, looking for the bytes a seed records.</summary>
    public const int MostVersions = 64;

    /// <summary>The most commits that changed a path read from the main tree's history, looking for them.</summary>
    public const int MostCommits = 512;

    /// <summary>What the main tree's side is called, in a conflict's markers and in a sentence.</summary>
    public const string Ours = "the main tree";

    /// <summary>What the agent's side is called.</summary>
    public const string Theirs = "the agent";

    /// <summary>What both held is called, in a conflict's markers.</summary>
    public const string Ancestor = "what both held";

    /// <summary>The attribute that says how a path is merged.</summary>
    private const string MergeAttribute = "merge";

    private static readonly GitMergeNames Names = new(Ours, Ancestor, Theirs);

    /// <summary>
    /// Whether a file can be run once two changes of it are merged: the bit that differs from what both held, where either
    /// side's does. A bit has two values, so two sides that both changed it changed it alike, and it never conflicts.
    /// </summary>
    /// <param name="ancestor">Whether what both held could be run.</param>
    /// <param name="ours">Whether ours can.</param>
    /// <param name="theirs">Whether theirs can.</param>
    public static bool MergedBit(bool ancestor, bool ours, bool theirs) => ours != ancestor ? ours : theirs;

    /// <summary>Whether <paramref name="offer"/> is written: a clean merge of a path the policy lets be merged.</summary>
    /// <param name="policy">What decides it.</param>
    /// <param name="offer">What a merge of the path gives.</param>
    public static bool Taken(TwoChangePolicy policy, TwoChangeOffer offer)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(offer);

        return offer.IsClean && policy.RuleFor(offer.Path) == TwoChangeRule.Text;
    }

    /// <summary>What a three-way merge of <paramref name="ask"/> gives; nothing is written to either tree or to the repository.</summary>
    /// <param name="policy">How its path is resolved.</param>
    /// <param name="ask">The path and its three versions.</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    /// <exception cref="HarnessException">git could not be asked.</exception>
    /// <exception cref="IOException">A side's file, or a kept copy, could not be read.</exception>
    public async Task<TwoChangeOffer> OfferAsync(TwoChangePolicy policy, TwoChangeAsk ask, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ask);

        var rule = policy.RuleFor(ask.Path);

        // Neither registry is ever merged by line, so nothing is measured, and nothing said of a merge: the path is
        // refused as it is without one.
        if (rule is TwoChangeRule.Never or TwoChangeRule.Arms)
        {
            return TwoChangeOffer.Unmeasured(ask.Path, rule);
        }

        var (merge, none) = await MergeAsync(ask, cancellationToken).ConfigureAwait(false);

        if (none is not null)
        {
            return TwoChangeOffer.Unoffered(ask.Path, rule, none);
        }

        var path = OrchestrationReports.Argument(ask.Path);

        if (!merge!.IsClean)
        {
            return TwoChangeOffer.Conflicting(
                ask.Path,
                rule,
                $"a three-way merge of the two conflicts in {merge.ConflictsSaid}, shown below: reconcile it by hand, then {FoldAllowances.SettledOption} {path}",
                [
                    $"conflicts of '{ask.Path}', {merge.ConflictsSaid} - as {Ours} holds it, as both trees held it, as {Theirs} holds it:",
                    .. merge.Shown.Select(line => $"  {line}"),
                ]);
        }

        byte[] working;

        try
        {
            working = await git.ReadCheckedOutAsync(ask.Tree, ask.Path, merge.Merged!, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return TwoChangeOffer.Unoffered(ask.Path, rule, NoMerge.NotCheckedOut(ex.Message));
        }

        var executable = MergedBit(ask.Ancestor!.Executable, ask.Ours.Executable, ask.Theirs.Executable);
        var hunks = $"{ReportText.Counted(merge.TheirsHunks, "hunk")} from {Theirs}, {merge.OursHunks} from {Ours}";

        // Said only where the two copies differ in it: the bit is then the side's that changed it.
        var bit = ask.Ours.Executable == ask.Theirs.Executable
            ? string.Empty
            : $"; it can {(executable ? string.Empty : "no longer ")}be run, as {(executable == ask.Theirs.Executable ? Theirs : Ours)} made it";

        return TwoChangeOffer.Clean(
            ask.Path,
            rule,
            working,
            executable,
            $"a three-way merge of the two is clean - {hunks} - and {FoldAllowances.MergeOption} {path} writes it",
            $"({FoldAllowances.MergeOption}: {hunks}{bit})");
    }

    /// <summary>The three versions merged as git merges them, or why no merge of them is offered.</summary>
    private async Task<(GitTextMerge? Merge, NoMerge? None)> MergeAsync(TwoChangeAsk ask, CancellationToken cancellationToken)
    {
        if (ask.Ancestor is null)
        {
            return (null, NoMerge.BothMade);
        }

        // Asked of a file before it is read: one above the bound is never brought into memory.
        var largest = new[] { ask.Ours.Content, ask.Theirs.Content }.OfType<GitContent.File>().Select(file => SharedCopies.LengthOf(files, file.Path)).DefaultIfEmpty(0).Max();

        if (largest > MostBytes)
        {
            return (null, NoMerge.TooLarge(largest));
        }

        var declared = await git.ReadAttributesAsync(ask.Tree, [MergeAttribute], [ask.Path], cancellationToken: cancellationToken).ConfigureAwait(false);
        var attribute = declared.GetValueOrDefault(ask.Path)?.GetValueOrDefault(MergeAttribute) ?? GitAttributes.Unspecified;

        // Unspecified, set and "text" are all git's own merge of text; anything else asks for another, which is not made here.
        if (attribute is not (GitAttributes.Unspecified or GitAttributes.Set or "text"))
        {
            return (null, NoMerge.Attribute(attribute));
        }

        var (ancestor, lost) = await AncestorAsync(ask.Path, ask.Ancestor, cancellationToken).ConfigureAwait(false);

        if (lost is not null)
        {
            return (null, lost);
        }

        IReadOnlyList<byte[]> stored;

        try
        {
            stored = await git.ReadStagedAsync(ask.Tree, ask.Path, [ancestor!, ask.Ours.Content, ask.Theirs.Content], cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return (null, NoMerge.NotStored(ex.Message));
        }

        // A blob's size is known only once it is read: the same bound, asked of what git stores.
        if (stored.Max(version => version.LongLength) is var size && size > MostBytes)
        {
            return (null, NoMerge.TooLarge(size));
        }

        var merge = await git.MergeTextAsync(stored[0], stored[1], stored[2], Names, cancellationToken).ConfigureAwait(false);

        return merge.NotMerged is { } why ? (null, NoMerge.NotMerged(why)) : (merge, null);
    }

    /// <summary>
    /// The bytes both trees held at <paramref name="path"/>, as something git can read: a commit's blob as it is; for what
    /// a seed records, the copy kept under its digest where it still hashes to it, or else a version the main tree's
    /// history held, proved by hashing it to that digest - never a guess.
    /// </summary>
    private Task<(GitContent? Content, NoMerge? None)> AncestorAsync(string path, TwoChangeAncestor ancestor, CancellationToken cancellationToken)
        => ancestor switch
        {
            TwoChangeAncestor.Committed committed => Task.FromResult<(GitContent?, NoMerge?)>((new GitContent.Blob(committed.Blob), null)),
            TwoChangeAncestor.Shared shared => SharedAncestorAsync(path, shared.Digest, cancellationToken),
            _ => throw new UnreachableException($"What both trees held is one of two kinds, and {ancestor.GetType().Name} is neither."),
        };

    /// <summary>
    /// The bytes a seed records both trees held at <paramref name="path"/>: the copy kept under <paramref name="digest"/>
    /// where it still hashes to it, or else a version the main tree's history held, proved by hashing it to that digest.
    /// A version git cannot check out - a filter that fails on it - is not one, and is said with the rest.
    /// </summary>
    private async Task<(GitContent? Content, NoMerge? None)> SharedAncestorAsync(string path, string digest, CancellationToken cancellationToken)
    {
        if (await kept.FindAsync(digest, cancellationToken).ConfigureAwait(false) is { } copy)
        {
            return copy.Holds ? (new GitContent.File(copy.File), null) : (null, NoMerge.KeptCopyChanged(copy.File, digest));
        }

        // A seed written by a build that kept no copies: the main tree's history is where a file both trees held most
        // likely still is, as a commit holds it or as that commit is checked out.
        var history = await git.ListHeldInHistoryAsync(main, "HEAD", [path], MostCommits, cancellationToken).ConfigureAwait(false);
        var versions = history.Held.GetValueOrDefault(path, []).Take(MostVersions).ToList();
        var blobs = await git.ReadBlobsAsync(main, [.. versions.Select(version => version.Blob)], cancellationToken).ConfigureAwait(false);
        var found = blobs.OfType<byte[]>().FirstOrDefault(bytes => FileContentHash.Of(bytes) == digest);

        // Checked out only where no version is those bytes as git stores it: each costs git processes of its own.
        var unread = 0;
        string? why = null;

        foreach (var bytes in found is null ? blobs.OfType<byte[]>() : [])
        {
            byte[] checkedOut;

            try
            {
                checkedOut = await git.ReadCheckedOutAsync(main, path, bytes, cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
            {
                unread++;
                why ??= ex.Message;
                continue;
            }

            if (FileContentHash.Of(checkedOut) == digest)
            {
                found = checkedOut;
                break;
            }
        }

        if (found is null)
        {
            return (null, NoMerge.NotInHistory(digest, versions.Count, why is null ? null : (unread, why)));
        }

        if (keeping)
        {
            // Kept so the search is made once; where it cannot be, it is made again, and nothing is lost.
            await kept.KeepAsync(digest, found, cancellationToken).ConfigureAwait(false);
        }

        return (new GitContent.Bytes(found), null);
    }
}
