using System.Diagnostics;
using DssHarness.Core.Anchors;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Mutations;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Core.Orchestration;

/// <summary>How a path both trees changed is resolved: the one decision, whichever command meets it.</summary>
internal enum TwoChangeRule
{
    /// <summary>As text, which nobody asked to have merged: what a merge gives is measured and said, and the path is refused.</summary>
    Refused,

    /// <summary>As text, named with <see cref="FoldAllowances.MergeOption"/>: a clean merge of it is taken.</summary>
    Text,

    /// <summary>The arm registry: merged by arm, unasked, and never by line.</summary>
    Arms,

    /// <summary>An anchor registry, which changes only through the rows an agent files: never merged.</summary>
    Never,
}

/// <summary>
/// What decides how a path both trees changed is resolved: the arm registry's path, the anchor registries', and the paths
/// named with <see cref="FoldAllowances.MergeOption"/>; and the configuration a merged arm registry is judged by.
/// </summary>
/// <param name="ArmRegistry">The configured arm registry, as git names it; null where the repository declares none.</param>
/// <param name="AnchorRegistries">The anchor registries, as git names them.</param>
/// <param name="Merge">The paths named with <see cref="FoldAllowances.MergeOption"/>, as git names them.</param>
/// <param name="Context">The repository and the configuration the command loaded.</param>
internal sealed record TwoChangePolicy(string? ArmRegistry, IReadOnlySet<string> AnchorRegistries, IReadOnlySet<string> Merge, HarnessContext Context)
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
            merge.Select(PathPatterns.Normalize).ToHashSet(StringComparer.Ordinal),
            context);
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
            TwoChangeRule.Arms => $"{FoldAllowances.MergeOption} '{path}' names the arm registry, which is merged by arm without it and never by line: drop it",
            _ => null,
        })).Where(named => named.Why is not null).Select(named => (named.Path, named.Why!));

    /// <summary>The paths named with <see cref="FoldAllowances.MergeOption"/> that a merge by line could be taken for, in order.</summary>
    public IEnumerable<string> NamedAsText() => Merge.Where(path => RuleFor(path) == TwoChangeRule.Text).Order(StringComparer.Ordinal);
}

/// <summary>One side of a path: a blob git holds, a file on disk, or bytes in hand; whether it could be run; and whether it is a link.</summary>
/// <param name="Content">What it holds.</param>
/// <param name="Executable">Whether it could be run, where the platform keeps such a bit; never on one that keeps none.</param>
/// <param name="Link">
/// Whether it is a link, or is reached through one along its path: no file to merge - what it leads to is its content -
/// and one a merge written there would land outside its tree through.
/// </param>
internal sealed record TwoChangeSide(GitContent Content, bool Executable, bool Link);

/// <summary>What both trees held at a path, as far as the command that asks knows it: one of the two kinds below, and no other.</summary>
internal abstract record TwoChangeAncestor
{
    private TwoChangeAncestor(bool executable) => Executable = executable;

    /// <summary>Whether it could be run, where the platform keeps such a bit.</summary>
    public bool Executable { get; }

    /// <summary>What a commit holds at the path: the agent's base, for a path it shares nothing for.</summary>
    /// <param name="Blob">The blob the commit holds there.</param>
    /// <param name="Executable">Whether the commit's entry is one that can be run.</param>
    /// <param name="Link">Whether the commit's entry is a link, whose blob is where it leads and no file's content.</param>
    public sealed record Committed(string Blob, bool Executable, bool Link) : TwoChangeAncestor(Executable);

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

    /// <summary>
    /// Both trees made a path merged with nothing both held - the arm registry - one so that it can be run and one not:
    /// with nothing before, neither bit is a change, and nothing says which is meant.
    /// </summary>
    public static NoMerge BitBothMade { get; } = new("both trees made it, one so that it can be run and one not, and nothing either held before says which is meant");

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

    /// <summary>
    /// One of its versions is a link, or is reached through one along its path: no file to merge, since what it leads to is
    /// its content, and a merge written there would land outside its tree.
    /// </summary>
    public static NoMerge Link { get; } = new("one of its versions is a link, or is reached through one, and only files are merged");

    /// <summary>git could not bring the merged bytes to the form a checkout writes.</summary>
    /// <param name="why">git's words.</param>
    public static NoMerge NotCheckedOut(string why) => new($"git could not write the merge as it would check it out - {why.TrimEnd('.')}");

    /// <summary>
    /// git could not bring a version a commit holds to the form a checkout writes, which a merge by arm reads it in: a
    /// filter that failed on it, for one.
    /// </summary>
    /// <param name="why">git's words.</param>
    public static NoMerge VersionNotCheckedOut(string why) => new($"git could not read the new base's version as a checkout would write it - {why.TrimEnd('.')}");

    /// <summary>A digest as a sentence names it: its first eight characters.</summary>
    private static string Short(string digest) => digest.Length > 8 ? $"{digest[..8]}..." : digest;
}

/// <summary>
/// What a merge of one path gives: one of six outcomes, each made by its own factory - clean, with the bytes as the tree
/// would hold them and whether the merged file can be run; merged by arm, the same with the text a validation reads;
/// conflicting, with what to show; none, with why; a merge by arm that is not taken, with what to show; or not measured,
/// for a path never merged - so no offer holds two of them.
/// </summary>
internal sealed record TwoChangeOffer
{
    private TwoChangeOffer(
        string path,
        TwoChangeRule rule,
        byte[]? working,
        bool executable,
        string gives,
        string said,
        string listed,
        IReadOnlyList<string> shown,
        NoMerge? none,
        RegistryMerge.Merged? registry = null)
    {
        Path = path;
        Rule = rule;
        Working = working;
        Digest = working is null ? null : FileContentHash.Of(working);
        Executable = executable;
        Gives = gives;
        Said = said;
        Listed = listed;
        Shown = shown;
        None = none;
        Registry = registry;
        Conflicts = false;
    }

    /// <summary>The path.</summary>
    public string Path { get; }

    /// <summary>How the path is resolved.</summary>
    public TwoChangeRule Rule { get; }

    /// <summary>
    /// Whether the three met and conflicted: the one refusal a merge cannot answer, told apart from a clean merge nobody
    /// asked for and from a merge never offered, so a reader of the plan need not read a line to know it.
    /// </summary>
    public bool Conflicts { get; private set; }

    /// <summary>The merged bytes as the tree would hold them, where the merge is clean; null otherwise.</summary>
    public byte[]? Working { get; }

    /// <summary>The SHA-256 of <see cref="Working"/>, where the merge is clean.</summary>
    public string? Digest { get; }

    /// <summary>Whether the merged file can be run, where the merge is clean.</summary>
    public bool Executable { get; }

    /// <summary>
    /// What the merge gives, as a clause, and nothing of what to do about it: what a command with ways out of its own says
    /// beside a path it refuses. Empty where nothing is said of one.
    /// </summary>
    public string Gives { get; }

    /// <summary>
    /// What the merge gives, as a clause to end the line that refuses the path, with the way on that a fold and a move of
    /// an agent's base offer; empty where nothing is said of one.
    /// </summary>
    public string Said { get; }

    /// <summary>The clause after the path in the list of merged paths, where the merge is clean.</summary>
    public string Listed { get; }

    /// <summary>The conflicts, as a block printed under the refusals; empty where there are none.</summary>
    public IReadOnlyList<string> Shown { get; }

    /// <summary>Why no merge is offered; null where one is, or where the rule never asks for one.</summary>
    public NoMerge? None { get; }

    /// <summary>
    /// The arm registry merged by arm, clean and not yet judged as a registry of the tree it is written into; null for
    /// every other offer, and for one already judged.
    /// </summary>
    public RegistryMerge.Merged? Registry { get; }

    /// <summary>Whether the three merged with no conflict.</summary>
    public bool IsClean => Working is not null;

    /// <summary>A path never merged: nothing is measured, and nothing said of a merge.</summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">How it is resolved: an anchor registry's rule.</param>
    public static TwoChangeOffer Unmeasured(string path, TwoChangeRule rule) => new(path, rule, null, false, string.Empty, string.Empty, string.Empty, [], null);

    /// <summary>A path no merge is offered for, with why.</summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">How it is resolved.</param>
    /// <param name="none">Why.</param>
    public static TwoChangeOffer Unoffered(string path, TwoChangeRule rule, NoMerge none)
    {
        ArgumentNullException.ThrowIfNull(none);

        var said = $"no three-way merge is offered: {none.Said}";

        return new(path, rule, null, false, said, said, string.Empty, [], none);
    }

    /// <summary>A merge that conflicts, with what to show of it.</summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">How it is resolved.</param>
    /// <param name="gives">What the merge gives, as a clause.</param>
    /// <param name="said">The same, with the way on, to end the line that refuses the path.</param>
    /// <param name="shown">The conflicts, as a block printed under the refusals.</param>
    public static TwoChangeOffer Conflicting(string path, TwoChangeRule rule, string gives, string said, IReadOnlyList<string> shown)
    {
        var offered = new TwoChangeOffer(path, rule, null, false, gives, said, string.Empty, shown, null);
        offered.Conflicts = true;
        return offered;
    }

    /// <summary>A clean merge.</summary>
    /// <param name="path">The path.</param>
    /// <param name="rule">How it is resolved.</param>
    /// <param name="working">The merged bytes as the tree would hold them.</param>
    /// <param name="executable">Whether the merged file can be run.</param>
    /// <param name="gives">What the merge gives, as a clause.</param>
    /// <param name="said">The same, with the option that takes it, to end the line that refuses the path where it is not taken.</param>
    /// <param name="listed">The clause after the path in the list of merged paths.</param>
    public static TwoChangeOffer Clean(string path, TwoChangeRule rule, byte[] working, bool executable, string gives, string said, string listed)
    {
        ArgumentNullException.ThrowIfNull(working);

        return new(path, rule, working, executable, gives, said, listed, [], null);
    }

    /// <summary>
    /// The arm registry merged by arm, clean: taken unasked once its text reads as a registry of the tree it is written
    /// into (<see cref="Judged"/>).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="merged">The merge, with the bytes to write and the text a validation reads.</param>
    /// <param name="executable">Whether the merged file can be run.</param>
    /// <param name="said">What the merge gives, as a clause.</param>
    /// <param name="listed">The clause after the path in the list of merged paths, once it reads as a registry.</param>
    public static TwoChangeOffer ByArm(string path, RegistryMerge.Merged merged, bool executable, string said, string listed)
    {
        ArgumentNullException.ThrowIfNull(merged);

        return new(path, TwoChangeRule.Arms, merged.Bytes, executable, said, said, listed, [], null, merged);
    }

    /// <summary>
    /// A merge by arm that is not taken: the two changed an arm differently, a version cannot be read as a registry, or
    /// the merged text does not read as one in the tree it would be written into.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="gives">Why, as a clause.</param>
    /// <param name="said">The same, with the way on, to end the line that refuses the path.</param>
    /// <param name="shown">What to show of it under the refusals; empty where the line says it all.</param>
    public static TwoChangeOffer NotTaken(string path, string gives, string said, IReadOnlyList<string> shown)
        => new(path, TwoChangeRule.Arms, null, false, gives, said, string.Empty, shown, null);

    /// <summary>
    /// This offer, judged by <paramref name="validation"/>: as it is, no longer waiting to be judged, where the merged text
    /// reads as a registry; otherwise not taken, its line saying how many problems it has, and every problem shown as
    /// check-mutations words them.
    /// </summary>
    /// <param name="validation">The merged text, validated against the tree it would be written into.</param>
    /// <param name="registry">The registry's path, as the configuration names it.</param>
    /// <param name="subject">Whose text it is, in the command's words: <c>as the fold would write it</c>.</param>
    public TwoChangeOffer Judged(RegistryValidation validation, string registry, string subject)
    {
        ArgumentNullException.ThrowIfNull(validation);

        var problems = $"merged by arm, it does not read as a registry - {ReportText.Counted(validation.Problems.Count, "problem")}, listed below";

        return validation.Valid
            ? new(Path, Rule, Working, Executable, Gives, Said, Listed, Shown, None)
            : NotTaken(Path, problems, problems, validation.Refusal(registry, subject));
    }

    /// <summary>The line that refuses the path, ended with what a merge of it gives.</summary>
    /// <param name="reason">The line as it reads without a word of a merge.</param>
    public string After(string reason) => Said.Length == 0 ? reason : $"{reason}; {Said}";
}

/// <summary>What a file is: its content, and whether it can be run, where the platform keeps such a bit.</summary>
/// <param name="Digest">The SHA-256 of its content.</param>
/// <param name="Executable">Whether it can be run; never on a platform that keeps no such bit.</param>
public readonly record struct FileIdentity(string Digest, bool Executable);

/// <summary>A copy a merge is written over: the file, the tree it is in, and what it was when it was weighed.</summary>
/// <param name="File">The file.</param>
/// <param name="Tree">The tree it is in, as a stop names it.</param>
/// <param name="Weighed">What it was when it was weighed: what the merge may be written over, and nothing else.</param>
public sealed record MergeTarget(string File, string Tree, FileIdentity Weighed);

/// <summary>A path merged three ways, as a plan holds it until it is written.</summary>
/// <param name="Path">The path.</param>
/// <param name="Working">The merged bytes, as the trees they are written into hold them.</param>
/// <param name="Digest">Their SHA-256.</param>
/// <param name="Executable">Whether the merged file can be run.</param>
/// <param name="Listed">The clause after the path in the list of merged paths.</param>
/// <param name="Into">
/// The copies the merge is written over, in the order they are written: the main tree's and then the agent's for a fold,
/// the agent's alone for a move of its base.
/// </param>
public sealed record MergedPath(string Path, byte[] Working, string Digest, bool Executable, string Listed, IReadOnlyList<MergeTarget> Into);

/// <summary>
/// The one place a path both trees changed is resolved, for every command that meets one: what a three-way merge of its
/// versions gives, measured in full before anything is written, and said whether or not anybody asked for it. Asking -
/// <see cref="FoldAllowances.MergeOption"/> - changes one thing: whether a clean merge is taken or refused.
/// </summary>
/// <remarks>
/// <para>
/// git merges text; nothing here compares lines. The three versions are brought to the form git stores, merged there, and
/// the result brought to the form a checkout writes, so a file one tree holds with other line endings merges as git
/// itself would merge it. Whether the file can be run is merged with its content (<see cref="MergedBit"/>). Never merged,
/// whatever is named: a path neither tree held a file at before, one git reads as binary or will not merge, one whose
/// <c>merge</c> attribute asks for another merge, and a file above <see cref="MostBytes"/>.
/// </para>
/// <para>
/// The arm registry is merged by arm, unasked, and never by line (<see cref="MutationRegistryMerge"/>): each version as
/// its tree holds it, from nothing where neither tree held a registry before, and taken only once its text reads as a
/// registry of the tree the command leaves (<see cref="JudgedAsync"/>).
/// </para>
/// </remarks>
/// <param name="git">Stores, merges and checks out.</param>
/// <param name="files">Reads how large a side's file is, and the files a merge by arm reads.</param>
/// <param name="sync">Says what a sync of the tree a merged arm registry is written into withholds from every copy of it.</param>
/// <param name="kept">The bytes the agent shares, kept beside its seed: what both trees held at a shared path.</param>
/// <param name="main">The main checkout's root, whose history is read where no copy was kept.</param>
/// <param name="keeping">Whether bytes found in that history are kept, so they are looked for once: true of a command that writes.</param>
internal sealed class TwoChanges(IGitClient git, IFileSystem files, ISyncService sync, SharedCopies kept, string main, bool keeping)
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

    /// <summary>The same three names, as a merge by arm says them: one set of words for both rules.</summary>
    private static readonly MergeSides Sides = new(Names.Ancestor, Names.Ours, Names.Theirs);

    /// <summary>
    /// Whether a file can be run once two changes of it are merged: the bit that differs from what both held, where either
    /// side's does. A bit has two values, so two sides that both changed it changed it alike, and it never conflicts -
    /// where something was held before; a registry both trees made whose bits differ is merged by no rule
    /// (<see cref="NoMerge.BitBothMade"/>).
    /// </summary>
    /// <param name="ancestor">Whether what both held could be run.</param>
    /// <param name="ours">Whether ours can.</param>
    /// <param name="theirs">Whether theirs can.</param>
    public static bool MergedBit(bool ancestor, bool ours, bool theirs) => ours != ancestor ? ours : theirs;

    /// <summary>
    /// Whether <paramref name="offer"/> is written: a clean merge of a path the policy lets be merged - one named to be, or
    /// the arm registry, which a command judges (<see cref="JudgedAsync"/>) before it takes it.
    /// </summary>
    /// <param name="policy">What decides it.</param>
    /// <param name="offer">What a merge of the path gives.</param>
    public static bool Taken(TwoChangePolicy policy, TwoChangeOffer offer)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(offer);

        return offer.IsClean && policy.RuleFor(offer.Path) is TwoChangeRule.Text or TwoChangeRule.Arms;
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

        // An anchor registry is never merged, so nothing is measured, and nothing said of a merge: the path is refused as
        // it is without one.
        if (rule == TwoChangeRule.Never)
        {
            return TwoChangeOffer.Unmeasured(ask.Path, rule);
        }

        // A link among the versions, or one along a version's path, is never merged, whatever is named: nothing is read
        // through it, and nothing would be written through it.
        if (ask.Ancestor is TwoChangeAncestor.Committed { Link: true } || ask.Ours.Link || ask.Theirs.Link)
        {
            return TwoChangeOffer.Unoffered(ask.Path, rule, NoMerge.Link);
        }

        if (rule == TwoChangeRule.Arms)
        {
            return await ByArmAsync(ask, cancellationToken).ConfigureAwait(false);
        }

        var (merge, none) = await MergeAsync(ask, cancellationToken).ConfigureAwait(false);

        if (none is not null)
        {
            return TwoChangeOffer.Unoffered(ask.Path, rule, none);
        }

        var path = OrchestrationReports.Argument(ask.Path);

        if (!merge!.IsClean)
        {
            var conflicts = $"a three-way merge of the two conflicts in {merge.ConflictsSaid}, shown below";

            return TwoChangeOffer.Conflicting(
                ask.Path,
                rule,
                conflicts,
                $"{conflicts}: reconcile it by hand, then {FoldAllowances.SettledOption} {path}",
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
        var clean = $"a three-way merge of the two is clean - {hunks}";

        return TwoChangeOffer.Clean(
            ask.Path,
            rule,
            working,
            executable,
            clean,
            $"{clean} - and {FoldAllowances.MergeOption} {path} writes it",
            $"({FoldAllowances.MergeOption}: {hunks}{BitSaid(ask, executable)})");
    }

    /// <summary>
    /// <paramref name="offer"/> judged, where it is the arm registry merged by arm and not yet judged: its text read as a
    /// registry of <paramref name="tree"/> as the command leaves it - <paramref name="changed"/> applied, each path read only
    /// where the validation asks for it - by the configuration the command loaded, as check-mutations reads one. Any other
    /// offer is as it was. Nothing is written.
    /// </summary>
    /// <param name="policy">The configuration the command loaded, among the rest.</param>
    /// <param name="offer">What a merge of the path gives.</param>
    /// <param name="tree">The tree the merged registry is written into.</param>
    /// <param name="writer">What writes it, as the refusal names it: <c>the fold</c>.</param>
    /// <param name="changed">Each path the command writes into that tree, the registry among them, and each it removes (<see cref="TreeAfter"/>).</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="HarnessException">git could not be asked what it ignores in the tree.</exception>
    public async Task<TwoChangeOffer> JudgedAsync(
        TwoChangePolicy policy,
        TwoChangeOffer offer,
        string tree,
        string writer,
        IEnumerable<(string Path, Func<CancellationToken, Task<byte[]>>? Read)> changed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(offer);

        if (offer.Registry is not { } merged)
        {
            return offer;
        }

        var config = policy.Context.Config;
        var after = await TreeAfter.OfAsync(sync, files, policy.Context, tree, changed, cancellationToken).ConfigureAwait(false);
        var validation = await MutationRegistryValidation.ValidateAsync(merged.Text, after, config.Mutations, config, cancellationToken).ConfigureAwait(false);

        return offer.Judged(validation, config.Mutations.Registry!, $"as {writer} would write it");
    }

    /// <summary>
    /// The arm registry's three versions merged by arm, each as its tree holds it - nothing staged, and git merges nothing -
    /// or why that merge is not offered or not taken. With nothing both held - neither tree held a registry there - it is
    /// merged from nothing; with something recorded whose bytes are not found it is not merged at all, since a merge with
    /// nothing to start from takes an arm one side removed for the other side's addition, and brings it back unsaid.
    /// </summary>
    private async Task<TwoChangeOffer> ByArmAsync(TwoChangeAsk ask, CancellationToken cancellationToken)
    {
        // Both made it with nothing before, one so that it can be run and one not: neither bit is a change, and taking
        // either would be a guess. Never on a platform that keeps no such bit, where both read as not runnable.
        if (ask.Ancestor is null && ask.Ours.Executable != ask.Theirs.Executable)
        {
            return TwoChangeOffer.Unoffered(ask.Path, TwoChangeRule.Arms, NoMerge.BitBothMade);
        }

        var (versions, none) = await WithinBoundAsync(
                ask,
                async () =>
                {
                    byte[]? held = null;

                    if (ask.Ancestor is { } ancestor)
                    {
                        var (content, lost) = await AncestorAsync(ask.Path, ancestor, cancellationToken).ConfigureAwait(false);

                        if (lost is not null)
                        {
                            return (null, lost);
                        }

                        held = await BytesAsync(ask.Tree, content!, cancellationToken).ConfigureAwait(false);
                    }

                    byte[] ours;

                    try
                    {
                        // A commit's version as a checkout into the tree writes it: the merge ends its lines as this side does.
                        ours = ask.Ours.Content is GitContent.Blob blob
                            ? await CommittedBytes.CheckedOutAsync(git, ask.Tree, ask.Path, blob.Id, cancellationToken).ConfigureAwait(false)
                            : await BytesAsync(ask.Tree, ask.Ours.Content, cancellationToken).ConfigureAwait(false);
                    }
                    catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
                    {
                        return (null, NoMerge.VersionNotCheckedOut(ex.Message));
                    }

                    return ([held, ours, await BytesAsync(ask.Tree, ask.Theirs.Content, cancellationToken).ConfigureAwait(false)], null);
                })
            .ConfigureAwait(false);

        if (none is not null)
        {
            return TwoChangeOffer.Unoffered(ask.Path, TwoChangeRule.Arms, none);
        }

        var path = OrchestrationReports.Argument(ask.Path);
        var settle = $"reconcile it by hand, then {FoldAllowances.SettledOption} {path}";

        switch (MutationRegistryMerge.Merge(versions![0], versions[1]!, versions[2]!))
        {
            case RegistryMerge.Merged merged:
            {
                var executable = MergedBit(ask.Ancestor?.Executable ?? false, ask.Ours.Executable, ask.Theirs.Executable);
                var said = merged.Said(Sides);

                return TwoChangeOffer.ByArm(ask.Path, merged, executable, said, $"({said}{BitSaid(ask, executable)}; it reads as a registry)");
            }

            case RegistryMerge.Conflicted conflicted:
            {
                var places = ReportText.Counted(conflicted.Conflicts.Count, "place");
                var conflicts = $"merged by arm, it conflicts in {places}, shown below";

                return TwoChangeOffer.NotTaken(
                    ask.Path,
                    conflicts,
                    $"{conflicts}: {settle}",
                    [
                        $"conflicts of '{ask.Path}' merged by arm, {places}:",
                        .. conflicted.Conflicts.SelectMany(conflict => conflict.Said(Sides)).Select(line => $"  {line}"),
                    ]);
            }

            case RegistryMerge.Unreadable unreadable:
            {
                var unread = $"merged by arm and never by line, and it cannot be read as a registry - {string.Join("; ", unreadable.Said(Sides))}";

                return TwoChangeOffer.NotTaken(ask.Path, unread, $"{unread}: {settle}", []);
            }

            case var other:
                throw new UnreachableException($"A merge by arm comes to one of three answers, and {other.GetType().Name} is none of them.");
        }
    }

    /// <summary>
    /// Whether the merged file can be run, as the clause after the path says it: only where the two copies differ in it,
    /// the bit then being the side's that changed it.
    /// </summary>
    private static string BitSaid(TwoChangeAsk ask, bool executable)
        => ask.Ours.Executable == ask.Theirs.Executable
            ? string.Empty
            : $"; it can {(executable ? string.Empty : "no longer ")}be run, as {(executable == ask.Theirs.Executable ? Theirs : Ours)} made it";

    /// <summary>
    /// The versions <paramref name="read"/> reads, or why no merge of them is offered: the one bound of every rule, asked
    /// of the two sides' files before anything is read - one above <see cref="MostBytes"/> is never brought into memory -
    /// and again of every version read, since a blob's size is known only once it is read.
    /// </summary>
    /// <param name="ask">The path and its versions.</param>
    /// <param name="read">Reads the versions to merge, or says why none is offered.</param>
    private async Task<(IReadOnlyList<byte[]?>? Versions, NoMerge? None)> WithinBoundAsync(
        TwoChangeAsk ask,
        Func<Task<(IReadOnlyList<byte[]?>? Versions, NoMerge? None)>> read)
    {
        var onDisk = new[] { ask.Ours.Content, ask.Theirs.Content }.OfType<GitContent.File>().Select(file => SharedCopies.LengthOf(files, file.Path));

        if (TooLarge(onDisk) is { } large)
        {
            return (null, large);
        }

        var (versions, none) = await read().ConfigureAwait(false);

        if (none is not null)
        {
            return (null, none);
        }

        return TooLarge(versions!.Select(version => version?.LongLength ?? 0)) is { } larger ? (null, larger) : (versions, null);

        static NoMerge? TooLarge(IEnumerable<long> sizes) => sizes.DefaultIfEmpty(0).Max() is var largest && largest > MostBytes ? NoMerge.TooLarge(largest) : null;
    }

    /// <summary>The bytes <paramref name="content"/> holds: a file's, a blob's as git stores it, or those in hand.</summary>
    /// <exception cref="HarnessException">git holds no such blob, or could not be asked.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    private async Task<byte[]> BytesAsync(string tree, GitContent content, CancellationToken cancellationToken)
        => content switch
        {
            GitContent.File file => await files.ReadAllBytesAsync(file.Path, cancellationToken).ConfigureAwait(false),
            GitContent.Blob blob => await CommittedBytes.StoredAsync(git, tree, blob.Id, cancellationToken).ConfigureAwait(false),
            GitContent.Bytes bytes => bytes.Content,
            _ => throw new UnreachableException($"Content is one of three kinds, and {content.GetType().Name} is none of them."),
        };

    /// <summary>The three versions merged as git merges them, or why no merge of them is offered.</summary>
    private async Task<(GitTextMerge? Merge, NoMerge? None)> MergeAsync(TwoChangeAsk ask, CancellationToken cancellationToken)
    {
        if (ask.Ancestor is not { } both)
        {
            return (null, NoMerge.BothMade);
        }

        var (stored, none) = await WithinBoundAsync(
                ask,
                async () =>
                {
                    var declared = await git.ReadAttributesAsync(ask.Tree, [MergeAttribute], [ask.Path], cancellationToken: cancellationToken).ConfigureAwait(false);
                    var attribute = declared.GetValueOrDefault(ask.Path)?.GetValueOrDefault(MergeAttribute) ?? GitAttributes.Unspecified;

                    // Unspecified, set and "text" are all git's own merge of text; anything else asks for another, which is not made here.
                    if (attribute is not (GitAttributes.Unspecified or GitAttributes.Set or "text"))
                    {
                        return (null, NoMerge.Attribute(attribute));
                    }

                    var (ancestor, lost) = await AncestorAsync(ask.Path, both, cancellationToken).ConfigureAwait(false);

                    if (lost is not null)
                    {
                        return (null, lost);
                    }

                    try
                    {
                        // Each as git stores it: what is merged, and so what the bound is asked of.
                        return (await git.ReadStagedAsync(ask.Tree, ask.Path, [ancestor!, ask.Ours.Content, ask.Theirs.Content], cancellationToken).ConfigureAwait(false), null);
                    }
                    catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
                    {
                        return (null, NoMerge.NotStored(ex.Message));
                    }
                })
            .ConfigureAwait(false);

        if (none is not null)
        {
            return (null, none);
        }

        var merge = await git.MergeTextAsync(stored![0]!, stored[1]!, stored[2]!, Names, cancellationToken).ConfigureAwait(false);

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
