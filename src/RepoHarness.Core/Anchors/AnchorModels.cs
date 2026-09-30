namespace RepoHarness.Core.Anchors;

/// <summary>The exit code the anchor commands share for an answer of "no".</summary>
/// <remarks>
/// From the range reserved for a command's own contract, like <see cref="Git.VerifyGitStatus"/>:
/// an id that was not found, a lint finding, or a balance that did not hold is a result the command
/// was asked to find out, not a failure of the command.
/// </remarks>
public static class AnchorExit
{
    /// <summary>An id was not found, the registries have problems, or the balance did not hold.</summary>
    public const int Findings = 1;
}

/// <summary>Which registries a command looks in.</summary>
public enum AnchorScope
{
    /// <summary>Both registries.</summary>
    All,

    /// <summary>The pending registry only.</summary>
    Pending,

    /// <summary>The done registry only.</summary>
    Done,
}

/// <summary>One registry file, located for the tree a command runs in.</summary>
/// <param name="Kind">Pending or done.</param>
/// <param name="RelativePath">The configured path, with forward slashes, relative to the repository root.</param>
/// <param name="FullPath">Where the file is on disk.</param>
/// <param name="IsIgnored">
/// Whether git ignores the path. An ignored registry belongs to no branch, so it has no history and
/// no copy in a worktree.
/// </param>
public sealed record AnchorRegistry(AnchorRegistryKind Kind, string RelativePath, string FullPath, bool IsIgnored)
{
    /// <summary>The registry's name, for messages.</summary>
    public string Name => Kind == AnchorRegistryKind.Pending ? "pending" : "done";

    /// <summary>The config.json setting that names this registry.</summary>
    public string Setting => Kind == AnchorRegistryKind.Pending ? "anchors.pendingAnchorsPath" : "anchors.doneAnchorsPath";

    /// <summary>
    /// The kind of registry a row with <paramref name="statusCell"/> belongs in: done when the row is
    /// closed, pending otherwise. Placing a row and finding a misfiled one both ask this, so a row is
    /// never written where a check would then call it misfiled.
    /// </summary>
    public static AnchorRegistryKind KindFor(string statusCell)
        => AnchorStatus.IsClosed(statusCell) ? AnchorRegistryKind.Done : AnchorRegistryKind.Pending;

    /// <summary>The finding for a row this registry holds that belongs in the other one, or null.</summary>
    public AnchorFinding? Misfiling(AnchorRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (KindFor(row.Status) == Kind)
        {
            return null;
        }

        return new AnchorFinding(
            RelativePath,
            row.LineNumber,
            AnchorFindingSeverity.Fatal,
            Kind == AnchorRegistryKind.Pending
                ? $"closed anchor '{row.Id}' is in the pending registry; closed anchors belong in the done registry"
                : $"live anchor '{row.Id}' is in the done registry, where nothing reads it as work");
    }
}

/// <summary>Both registries, located together.</summary>
public sealed record AnchorRegistries(AnchorRegistry Pending, AnchorRegistry Done)
{
    /// <summary>Pending first, then done.</summary>
    public IReadOnlyList<AnchorRegistry> All => [Pending, Done];

    /// <summary>The registry of <paramref name="kind"/>.</summary>
    public AnchorRegistry For(AnchorRegistryKind kind) => kind == AnchorRegistryKind.Pending ? Pending : Done;

    /// <summary>The registry a row with <paramref name="statusCell"/> belongs in.</summary>
    public AnchorRegistry HomeOf(string statusCell) => For(AnchorRegistry.KindFor(statusCell));
}

/// <summary>A new anchor, as given on the command line.</summary>
/// <param name="Id">The new id.</param>
/// <param name="Priority">P0 to P5.</param>
/// <param name="Trigger">What is wrong, and what would make it worth doing.</param>
public sealed record AnchorWriteRequest(string Id, string Priority, string Trigger)
{
    /// <summary>The status word; a closed anchor is filed straight into the done registry.</summary>
    public string Status { get; init; } = "open";

    /// <summary>What remains to be done to close it.</summary>
    public string? ClosingWork { get; init; }

    /// <summary>Where it is cited, and related anchors.</summary>
    public string? CrossRefs { get; init; }
}

/// <summary>A change to an existing anchor. A field left null is not changed.</summary>
/// <param name="Id">The existing id.</param>
public sealed record AnchorSetRequest(string Id)
{
    /// <summary>Where to look for the anchor. The status alone decides where it ends up.</summary>
    public AnchorScope Scope { get; init; } = AnchorScope.All;

    /// <summary>A new priority.</summary>
    public string? Priority { get; init; }

    /// <summary>A new status word.</summary>
    public string? Status { get; init; }

    /// <summary>A new Trigger.</summary>
    public string? Trigger { get; init; }

    /// <summary>A new Closing work.</summary>
    public string? ClosingWork { get; init; }

    /// <summary>A new Cross-refs.</summary>
    public string? CrossRefs { get; init; }

    /// <summary>Whether anything is to change at all.</summary>
    public bool HasChanges =>
        Priority is not null || Status is not null || Trigger is not null || ClosingWork is not null || CrossRefs is not null;
}

/// <summary>A row as declared: what the registries are to hold for its id once it is applied.</summary>
/// <param name="Id">The anchor.</param>
/// <param name="Status">The status word; it alone decides which registry the row is in.</param>
/// <param name="Trigger">What is wrong, and what would make it worth doing.</param>
/// <param name="ClosingWork">What remains to be done to close it.</param>
/// <param name="CrossRefs">Where it is cited, and related anchors.</param>
public sealed record AnchorRowDeclaration(string Id, string Status, string Trigger, string ClosingWork, string CrossRefs)
{
    /// <summary>Its priority; null keeps an existing row's, and a new row must declare one.</summary>
    public string? Priority { get; init; }
}

/// <summary>
/// A row's cells, each by the key the commands that change one use - an agent's row files, --accept-lost and every
/// --&lt;cell&gt; option - and by the heading the registry's table gives it, which every refusal of a cell names it by.
/// </summary>
public static class AnchorCellNames
{
    /// <summary>The priority band.</summary>
    public const string Priority = "priority";

    /// <summary>The status.</summary>
    public const string Status = "status";

    /// <summary>What is wrong, and what would make it worth doing.</summary>
    public const string Trigger = "trigger";

    /// <summary>What remains to be done to close it.</summary>
    public const string Closing = "closing";

    /// <summary>Where it is cited, and related anchors.</summary>
    public const string CrossRefs = "cross-refs";

    /// <summary>The heading of the cell holding a row's id.</summary>
    public const string AnchorHeading = "Anchor";

    /// <summary>The heading of <see cref="Priority"/>.</summary>
    public const string PriorityHeading = "Priority";

    /// <summary>The heading of <see cref="Status"/>.</summary>
    public const string StatusHeading = "Status";

    /// <summary>The heading of <see cref="Trigger"/>.</summary>
    public const string TriggerHeading = "Trigger";

    /// <summary>The heading of <see cref="Closing"/>.</summary>
    public const string ClosingHeading = "Closing work";

    /// <summary>The heading of <see cref="CrossRefs"/>.</summary>
    public const string CrossRefsHeading = "Cross-refs";

    /// <summary>The cells that hold prose: each stored on one line, whatever lines it was written on.</summary>
    public static IReadOnlyList<string> Text { get; } = [Trigger, Closing, CrossRefs];

    /// <summary>The heading the registry's table gives <paramref name="cell"/>.</summary>
    /// <param name="cell">One of the keys above.</param>
    public static string HeadingOf(string cell) => cell switch
    {
        Priority => PriorityHeading,
        Status => StatusHeading,
        Trigger => TriggerHeading,
        Closing => ClosingHeading,
        CrossRefs => CrossRefsHeading,
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, "Not a cell of an anchor's row."),
    };

    /// <summary>The option that gives <paramref name="cell"/> a value: <c>--trigger</c>, <c>--closing</c> and the rest.</summary>
    /// <param name="cell">One of the keys above.</param>
    public static string OptionOf(string cell) => "--" + cell;
}

/// <summary>One prose cell of one anchor's row, as --accept-lost names it: <c>&lt;ID&gt;:&lt;cell&gt;</c>.</summary>
/// <remarks>
/// Built only for a cell that holds prose and an id that could be one, so a set of them - what a batch accepts losing -
/// never holds a cell no loss could be judged in.
/// </remarks>
public sealed record AnchorRowCell
{
    /// <summary>How one is written, for a message that names the form.</summary>
    public const string Form = "<ID>:<cell>";

    /// <param name="id">The anchor.</param>
    /// <param name="cell">One of <see cref="AnchorCellNames.Text"/>.</param>
    /// <exception cref="ArgumentException">The id could be no id, or the cell holds no prose.</exception>
    public AnchorRowCell(string id, string cell)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(cell);

        if (IdProblem(id) is { } problem)
        {
            throw new ArgumentException(problem, nameof(id));
        }

        if (!AnchorCellNames.Text.Contains(cell, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{cell}' is not a cell that holds prose: {string.Join(", ", AnchorCellNames.Text)}.", nameof(cell));
        }

        Id = id;
        Cell = cell;
    }

    /// <summary>The anchor.</summary>
    public string Id { get; }

    /// <summary>One of <see cref="AnchorCellNames.Text"/>.</summary>
    public string Cell { get; }

    /// <summary>
    /// What is wrong with <paramref name="id"/> as --new or --accept-lost names one, before any registry is read: that it
    /// is empty, or holds a space or a colon, which no id does; <see langword="null"/> where nothing is.
    /// </summary>
    /// <param name="id">As given.</param>
    public static string? IdProblem(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return id.Length == 0 || id.Any(character => char.IsWhiteSpace(character) || character == ':')
            ? $"'{id}' is not an anchor id: an id is not empty, and holds no space and no colon"
            : null;
    }

    /// <summary>
    /// <paramref name="text"/> read as <see cref="Form"/>, split at its last colon: <see langword="null"/> where the cell
    /// is not one of <see cref="AnchorCellNames.Text"/>, or the id could be no id.
    /// </summary>
    /// <param name="text">As given.</param>
    public static AnchorRowCell? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var colon = text.LastIndexOf(':');

        return colon > 0 && IdProblem(text[..colon]) is null && AnchorCellNames.Text.Contains(text[(colon + 1)..], StringComparer.Ordinal)
            ? new AnchorRowCell(text[..colon], text[(colon + 1)..])
            : null;
    }

    /// <summary>As --accept-lost names it.</summary>
    public override string ToString() => $"{Id}:{Cell}";
}

/// <summary>How far applying a batch of rows goes.</summary>
public enum AnchorBatchMode
{
    /// <summary>Say what applying them would do, writing nothing: a cell that would lose stored text is shown, not refused.</summary>
    Plan,

    /// <summary>Refuse whatever applying them would refuse, writing nothing: the check before a write that must not stop part way.</summary>
    Check,

    /// <summary>Write them.</summary>
    Apply,
}

/// <summary>Rows to apply as one batch, and what the person applying them allows it that it otherwise refuses.</summary>
/// <param name="Rows">The rows, applied in order.</param>
public sealed record AnchorBatchRequest(IReadOnlyList<AnchorRowDeclaration> Rows)
{
    /// <summary>The option that names a row the batch may make, as the commands that apply one take it and its refusals name it.</summary>
    public const string NewOption = "--new";

    /// <summary>The option that accepts a cell's loss, as the commands that apply a batch take it and its refusals name it.</summary>
    public const string AcceptLostOption = "--accept-lost";

    /// <summary>The ids it may create: a row no registry holds is otherwise refused, as a typo would make it a second row.</summary>
    public IReadOnlyCollection<string> New { get; init; } = [];

    /// <summary>The cells of existing rows it may write though their stored text does not survive in what replaces it.</summary>
    public IReadOnlyCollection<AnchorRowCell> AcceptLost { get; init; } = [];

    /// <summary>
    /// Directories at the top of the tree, beside those it holds when the batch runs, where a path a cell cites may start:
    /// those a fold is about to make, so its rows are judged the same before its files are written and after.
    /// </summary>
    public IReadOnlyCollection<string> Roots { get; init; } = [];
}

/// <summary>A cell of an existing row whose stored text would not survive the text replacing it.</summary>
/// <param name="Cell">The row's cell.</param>
/// <param name="Diff">What it loses and what replaces it, word by word.</param>
/// <param name="Accepted">Whether the request accepted losing it.</param>
public sealed record AnchorLostCell(AnchorRowCell Cell, string Diff, bool Accepted);

/// <summary>How the registries hold a declared row other than as declared.</summary>
/// <param name="Id">The anchor.</param>
/// <param name="How">What differs, said of the row: <c>its status is '...'</c>, that it has no row, or how many it has.</param>
public sealed record AnchorDifference(string Id, string How);

/// <summary>What applying one declared row does.</summary>
public enum AnchorRowAction
{
    /// <summary>Its id had no row, and one is written.</summary>
    New,

    /// <summary>Its row is changed, in the cells that differ, and moved where its status now belongs.</summary>
    Changed,

    /// <summary>Its row already holds what was declared, and is not written again.</summary>
    AlreadyIn,
}

/// <summary>What applying one declared row did, or would do.</summary>
/// <param name="Id">The anchor.</param>
/// <param name="Action">What was done with it.</param>
/// <param name="Change">The change, as write-anchor or set-anchor describes one.</param>
public sealed record AnchorRowOutcome(string Id, AnchorRowAction Action, AnchorChange Change)
{
    /// <summary>The cells of a changed row whose stored text does not survive what replaces it.</summary>
    public IReadOnlyList<AnchorLostCell> Lost { get; init; } = [];
}

/// <summary>What applying declared rows did, or would do planned or checked (<see cref="AnchorBatchMode"/>).</summary>
/// <param name="Rows">Each row planned, in order; on a refusal, those that could be planned.</param>
/// <param name="Problems">Why rows were refused, each naming its id; nothing was written while there is one.</param>
public sealed record AnchorBatch(IReadOnlyList<AnchorRowOutcome> Rows, IReadOnlyList<string> Problems)
{
    /// <summary>Why writing them failed, once every row had passed its checks; null where nothing did.</summary>
    public string? Failure { get; init; }

    /// <summary>The registries put back byte for byte after <see cref="Failure"/>.</summary>
    public IReadOnlyList<string> Restored { get; init; } = [];

    /// <summary>The registries that could not be put back after <see cref="Failure"/>, and are not as they were.</summary>
    public IReadOnlyList<string> RestoreFailed { get; init; } = [];

    /// <summary>
    /// Whether every row was checked and, unless planned or checked only, is in the registries as declared. A plan succeeds
    /// with cells that lose stored text nobody accepted (<see cref="Unaccepted"/>), which it shows and a check refuses.
    /// </summary>
    public bool Succeeded => Problems.Count == 0 && Failure is null;

    /// <summary>The cells of its rows that would lose stored text, and that nobody accepted losing.</summary>
    public IReadOnlyList<AnchorRowCell> Unaccepted => [.. Rows.SelectMany(row => row.Lost).Where(cell => !cell.Accepted).Select(cell => cell.Cell)];
}

/// <summary>Which anchors to list.</summary>
public sealed record AnchorListFilter
{
    /// <summary>The registries to list.</summary>
    public AnchorScope Scope { get; init; } = AnchorScope.All;

    /// <summary>Only these priorities; empty lists every priority.</summary>
    public IReadOnlyList<string> Bands { get; init; } = [];

    /// <summary>Only anchors that are not closed.</summary>
    public bool OnlyOpen { get; init; }

    /// <summary>Only closed anchors.</summary>
    public bool OnlyClosed { get; init; }
}

/// <summary>A row, and the registry it was read from.</summary>
public sealed record AnchorEntry(AnchorRow Row, AnchorRegistry Registry);

/// <summary>One changed cell.</summary>
/// <param name="Field">The column.</param>
/// <param name="Before">Its value before the change.</param>
/// <param name="After">Its value after it.</param>
public sealed record AnchorFieldChange(string Field, string Before, string After);

/// <summary>What write-anchor or set-anchor did, or would do on a dry run.</summary>
/// <param name="Id">The anchor.</param>
/// <param name="From">The registry the row was in, or null for a new anchor.</param>
/// <param name="To">The registry the row is in now.</param>
/// <param name="StatusBefore">Its status before, or null for a new anchor.</param>
/// <param name="StatusAfter">Its status now.</param>
/// <param name="Fields">The cells that changed. Empty for a new anchor.</param>
/// <param name="Written">False on a dry run.</param>
public sealed record AnchorChange(
    string Id,
    AnchorRegistry? From,
    AnchorRegistry To,
    string? StatusBefore,
    string StatusAfter,
    IReadOnlyList<AnchorFieldChange> Fields,
    bool Written)
{
    /// <summary>Whether the anchor is new.</summary>
    public bool IsNew => From is null;

    /// <summary>Whether the row changed registry.</summary>
    public bool Moved => From is not null && From.Kind != To.Kind;
}

/// <summary>The rows found for one requested id.</summary>
/// <param name="Id">The id as requested.</param>
/// <param name="Matches">Every row carrying it. More than one is a duplicate.</param>
/// <param name="SameNamespace">When none matched, ids that begin the same way, as a hint.</param>
public sealed record AnchorLookupResult(string Id, IReadOnlyList<AnchorEntry> Matches, IReadOnlyList<string> SameNamespace);

/// <summary>The answer to read-anchor: one result per requested id, in the order requested.</summary>
public sealed record AnchorLookup(IReadOnlyList<AnchorLookupResult> Results)
{
    /// <summary>The requested ids no row carries.</summary>
    public IReadOnlyList<AnchorLookupResult> Missing => [.. Results.Where(result => result.Matches.Count == 0)];
}

/// <summary>A problem found in a registry.</summary>
/// <param name="File">The registry's configured path.</param>
/// <param name="LineNumber">The line the problem is at.</param>
/// <param name="Severity">Whether it stops the registry being trusted.</param>
/// <param name="Message">What is wrong.</param>
public sealed record AnchorFinding(string File, int LineNumber, AnchorFindingSeverity Severity, string Message);

/// <summary>An anchor open now that was not open at the base commit.</summary>
/// <param name="Id">The anchor.</param>
/// <param name="Excerpt">The start of its Trigger.</param>
/// <param name="Disclosed">Whether it is disclosed, and so not counted against the balance.</param>
public sealed record AnchorOpening(string Id, string Excerpt, bool Disclosed);

/// <summary>What check-anchor-balance measured.</summary>
/// <param name="Base">The base as given.</param>
/// <param name="Commit">The commit it resolved to.</param>
/// <param name="OpenAtBase">Distinct ids open at the base.</param>
/// <param name="OpenNow">Distinct ids open in the working tree.</param>
/// <param name="Closed">Ids open at the base and not now.</param>
/// <param name="Opened">Ids open now and not at the base.</param>
/// <param name="MissingAtBase">Registries that did not exist at the base, and so count as empty there.</param>
/// <param name="Findings">Problems in the registries as they are now.</param>
public sealed record AnchorBalanceReport(
    string Base,
    string Commit,
    int OpenAtBase,
    int OpenNow,
    IReadOnlyList<string> Closed,
    IReadOnlyList<AnchorOpening> Opened,
    IReadOnlyList<string> MissingAtBase,
    IReadOnlyList<AnchorFinding> Findings)
{
    /// <summary>How many newly opened anchors are disclosed.</summary>
    public int Disclosed => Opened.Count(opening => opening.Disclosed);

    /// <summary>
    /// The rise the balance counts: the change in open anchors, less those newly disclosed. A disclosed
    /// anchor records debt that already existed, so writing it down is not creating it.
    /// </summary>
    public int NetNew => OpenNow - OpenAtBase - Disclosed;

    /// <summary>Whether the change did not add open work and the registries are sound.</summary>
    public bool Passed => NetNew <= 0 && !Findings.Any(finding => finding.Severity == AnchorFindingSeverity.Fatal);
}
