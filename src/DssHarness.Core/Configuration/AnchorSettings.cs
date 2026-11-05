namespace DssHarness.Core.Configuration;

/// <summary>Where the anchor registries live, and how a new anchor id is spelled.</summary>
public sealed class AnchorSettings
{
    /// <summary>The pending registry used when config.json names none.</summary>
    public const string DefaultPendingAnchorsPath = ".plans/_deferred-anchor-registry.md";

    /// <summary>The done registry used when config.json names none.</summary>
    public const string DefaultDoneAnchorsPath = ".plans/_deferred-anchor-registry-done.md";

    /// <summary>
    /// The registry of live anchors (open, gated and disclosed), relative to the repository root.
    /// </summary>
    public string PendingAnchorsPath { get; init; } = DefaultPendingAnchorsPath;

    /// <summary>
    /// The archive of closed anchors, relative to the repository root. An anchor moves here when
    /// its status becomes closed, and back when it is reopened.
    /// </summary>
    public string DoneAnchorsPath { get; init; } = DefaultDoneAnchorsPath;

    /// <summary>What every anchor id starts with, before its first hyphen.</summary>
    public string IdPrefix { get; init; } = "D";

    /// <summary>
    /// Whether a row's Trigger carries its verdict too. When true, a closed row's Trigger opens with the
    /// closed mark, as its Status does, and no other row's does: write-anchor and set-anchor refuse a row
    /// whose two cells disagree, and read-anchors --lint and check-anchor-balance report one. A closed row's
    /// Trigger may open with the bookkeeping pair instead, the closed mark and then the bookkeeping mark, to
    /// say its closure only repairs the mark of work done before the change that closes it:
    /// check-anchor-balance counts the anchor closed and credits that change with nothing. False, the
    /// default: the Status cell is the only verdict, and no verdict or count is read from the prose beside it.
    /// </summary>
    /// <remarks>
    /// For a registry whose rows open a closed Trigger with the closure itself - the mark, when, and what
    /// closed it - where the two cells can state two verdicts, and each reader believes the one it reads.
    /// </remarks>
    public bool TriggerCarriesVerdict { get; init; }

    /// <summary>
    /// Fewest hyphen-separated segments after the prefix that a NEW id may have. Existing ids are
    /// never checked against it: an id is permanent once written, because renaming one orphans
    /// every citation of it.
    /// </summary>
    public int MinimumIdSegments { get; init; } = 3;

    /// <summary>
    /// Files and directories, relative to the repository root, where every cited anchor id must
    /// resolve to a row. Nothing outside a declared root is scanned.
    /// </summary>
    /// <remarks>
    /// Which code is production code is a judgement a repository makes, not one a tool can infer, so
    /// it is declared rather than guessed. Scanning everything would make the check fail on ids that
    /// were never anchors, and scanning nothing would make it pass on a citation that resolves to
    /// nothing; an empty list therefore scans nothing and the command says so, rather than quietly
    /// reporting success.
    /// </remarks>
    public List<string> CitationRoots { get; init; } = [];

    /// <summary>
    /// The words a row's cells may state a pass of a sweep with, matched ignoring case. A pass word followed by only
    /// whitespace and brackets or punctuation and then a run id is a citation of that run, which check-anchor-arms
    /// reports wherever it does not sit inside a span matching one of <see cref="HistoryForms"/>: a pass holds only of
    /// the bytes its run read, so it goes stale as what it judged moves, and history is where a row may state one.
    /// </summary>
    public List<string> PassWords { get; init; } = [DefaultPassWord];

    /// <summary>The pass word a repository names none of.</summary>
    public const string DefaultPassWord = "passed";

    /// <summary>
    /// The forms of history a row's cells may state a pass in, each a pattern with <c>{commit}</c> and <c>{run}</c>
    /// placeholders - a commit is hex, a run id as runs spell them - such as <c>closed at {commit} on sweep {run}</c>.
    /// A pass citation inside a span matching one of them is history and is not reported; empty, no form is history.
    /// </summary>
    public List<string> HistoryForms { get; init; } = [];
}
