using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

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

    /// <summary>Whether the registry's file is there.</summary>
    /// <exception cref="HarnessException">A directory is there instead.</exception>
    public bool Exists(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        // Taken for no registry, a directory sent its reader to init, which then failed to write over it.
        if (fileSystem.DirectoryExists(FullPath))
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{RelativePath}', where {Setting} puts the {Name} registry, is a directory. Move it aside, or set {Setting} to a file.");
        }

        return fileSystem.FileExists(FullPath);
    }

    /// <summary>The file's text as it stands, or <see langword="null"/> where nothing is there.</summary>
    /// <exception cref="HarnessException">A directory is there instead (<see cref="Exists"/>), or the file could not be read.</exception>
    public string? ReadText(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        try
        {
            return Exists(fileSystem) ? fileSystem.ReadAllText(FullPath) : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Removed between the look and the read.
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The {Name} registry, '{RelativePath}', could not be read: {ex.Message.TrimEnd('.')}.");
        }
    }

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

        return Fatal(
            row,
            Kind == AnchorRegistryKind.Pending
                ? $"closed anchor '{row.Id}' is in the pending registry; closed anchors belong in the done registry"
                : $"live anchor '{row.Id}' is in the done registry, where nothing reads it as work");
    }

    /// <summary>
    /// The finding for a row this registry holds whose Trigger states another verdict than its Status, where
    /// <paramref name="settings"/> hold a Trigger to its row's verdict (<see cref="AnchorSettings.TriggerCarriesVerdict"/>),
    /// or null.
    /// </summary>
    public AnchorFinding? SplitVerdict(AnchorRow row, AnchorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(row);

        return AnchorStatus.SplitVerdict(row.Status, row.Trigger, settings) is { } split
            ? Fatal(row, $"{split}: anchors.triggerCarriesVerdict holds a row to one verdict, stated in both")
            : null;
    }

    /// <summary>
    /// The note for a closed row this registry holds whose Trigger opens with the bookkeeping pair that is not read as
    /// one, so its closure counts as work (<see cref="AnchorStatus.UnreadBookkeepingPair"/>), or null.
    /// </summary>
    public AnchorFinding? UnreadBookkeepingPair(AnchorRow row, AnchorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.IsClosed && AnchorStatus.UnreadBookkeepingPair(row.Trigger, settings) is { } why
            ? Note(
                row,
                $"anchor '{row.Id}' has a Trigger opening with the closed mark and then the bookkeeping mark, "
                + $"{AnchorStatus.ClosedMark}{AnchorStatus.BookkeepingMark}, that is not read as the bookkeeping pair: {why}; "
                + "its closure counts as work")
            : null;
    }

    /// <summary>
    /// A problem with a row this registry holds, as its file stands now, that stops the registry being trusted. A row read
    /// from another commit is at another line.
    /// </summary>
    public AnchorFinding Fatal(AnchorRow row, string message)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AnchorFinding(RelativePath, row.LineNumber, AnchorFindingSeverity.Fatal, message);
    }

    /// <summary>A problem at no one line of this registry as it stands now, that stops it being trusted.</summary>
    public AnchorFinding Fatal(string message) => new(RelativePath, 0, AnchorFindingSeverity.Fatal, message);

    /// <summary>Something about this registry at no one line of it as it stands now, that fails nothing.</summary>
    public AnchorFinding Note(string message) => new(RelativePath, 0, AnchorFindingSeverity.Note, message);

    /// <summary>Something about a row this registry holds, as its file stands now, that fails nothing.</summary>
    public AnchorFinding Note(AnchorRow row, string message)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AnchorFinding(RelativePath, row.LineNumber, AnchorFindingSeverity.Note, message);
    }

    /// <summary>A problem in the structure of this registry's file, as a finding of the registry.</summary>
    public AnchorFinding Finding(AnchorDocumentFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return new AnchorFinding(RelativePath, finding.LineNumber, finding.Severity, finding.Message);
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
public sealed record AnchorEntry(AnchorRow Row, AnchorRegistry Registry)
{
    /// <summary>Every row of <paramref name="documents"/>, in order, each with the registry it was read from.</summary>
    public static IEnumerable<AnchorEntry> Of(IEnumerable<(AnchorRegistryDocument Document, AnchorRegistry Registry)> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        return documents.SelectMany(pair => pair.Document.Rows.Select(row => new AnchorEntry(row, pair.Registry)));
    }

    /// <summary>
    /// A finding on each row of an id that more than one row of <paramref name="documents"/> holds, across both
    /// registries: a duplicate hands a reader two histories under one name.
    /// </summary>
    public static IEnumerable<AnchorFinding> Duplicates(IEnumerable<(AnchorRegistryDocument Document, AnchorRegistry Registry)> documents)
        => Of(documents)
            .GroupBy(entry => entry.Row.Id, AnchorIdMatch.Comparer)
            .Where(group => group.Count() > 1)
            .SelectMany(group =>
            {
                var locations = string.Join(", ", group.Select(entry => $"{entry.Registry.RelativePath}:{entry.Row.LineNumber}"));

                return group.Select(entry => entry.Registry.Fatal(
                    entry.Row,
                    $"'{group.Key}' has {group.Count()} rows ({locations}); one id has one row"));
            });
}

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

/// <summary>An anchor open now that was not open where the change began (<see cref="AnchorBalanceReport.Commit"/>).</summary>
/// <param name="Id">The anchor.</param>
/// <param name="Excerpt">The start of its Trigger.</param>
/// <param name="Disclosed">Whether it is disclosed, and so not counted against the balance.</param>
public sealed record AnchorOpening(string Id, string Excerpt, bool Disclosed);

/// <summary>
/// An anchor open where the change began (<see cref="AnchorBalanceReport.Commit"/>) and closed now: its id still has a
/// row, and every row it has reads closed.
/// </summary>
/// <param name="Id">The anchor.</param>
/// <param name="Bookkeeping">
/// Whether its closure only repairs the mark of work done before the change that closes it, and so is not counted to the
/// change's credit: a row of it says so, or it had a closed row already where the change began, beside its open one.
/// </param>
public sealed record AnchorClosing(string Id, bool Bookkeeping);

/// <summary>
/// An anchor whose id had a row where the change began (<see cref="AnchorBalanceReport.Commit"/>) and has none in either
/// registry now: deleted, or renamed by hand, since a row moves between the registries and is never deleted.
/// </summary>
/// <param name="Id">The anchor.</param>
/// <param name="AlreadyClosed">
/// Whether every row it had there was closed, so it counted nowhere and its loss changes no count. One open there leaves
/// the open count, and is not credited to the change, as a closure would be.
/// </param>
public sealed record AnchorLoss(string Id, bool AlreadyClosed);

/// <summary>What check-anchor-balance measured.</summary>
/// <param name="Base">The base as given.</param>
/// <param name="BaseCommit">The commit it resolved to.</param>
/// <param name="Commit">
/// The commit the working tree was compared with, where the change began: where HEAD's history left the base's - HEAD
/// merged with <paramref name="Merging"/>, as the commit finishing that merge would be - which is the base's own commit
/// where it is an ancestor of them.
/// </param>
/// <param name="Merging">
/// The commits an unfinished merge is bringing into HEAD, whose content the working tree holds; none where no merge is
/// in progress.
/// </param>
/// <param name="OpenAtBase">Distinct ids open at <paramref name="Commit"/>.</param>
/// <param name="OpenNow">Distinct ids open in the working tree.</param>
/// <param name="Closed">Anchors open at <paramref name="Commit"/> and closed now, in id order.</param>
/// <param name="Lost">
/// Anchors whose id had a row at <paramref name="Commit"/> and has none in either registry now, in id order: deleted, or
/// renamed by hand, and each a finding: a note, where its Anchor cell there did not name one id, since repairing that
/// cell changes the id its row reads as. One open there leaves the open count and is not credited to the change, as a
/// closure would be; one closed there changes no count. None where a registry is missing or malformed now: that is its
/// own finding, and every row it hides would read as lost.
/// </param>
/// <param name="Opened">Anchors open now and not at <paramref name="Commit"/>, in id order.</param>
/// <param name="MissingAtBase">Registries that did not exist at <paramref name="Commit"/>, and so count as empty there.</param>
/// <param name="MalformedAtBase">
/// Registries malformed at <paramref name="Commit"/>, so a row one held that could not be read was not counted there.
/// </param>
/// <param name="Findings">Problems in the registries as they are now, and notes that fail nothing.</param>
public sealed record AnchorBalanceReport(
    string Base,
    string BaseCommit,
    string Commit,
    IReadOnlyList<string> Merging,
    int OpenAtBase,
    int OpenNow,
    IReadOnlyList<AnchorClosing> Closed,
    IReadOnlyList<AnchorLoss> Lost,
    IReadOnlyList<AnchorOpening> Opened,
    IReadOnlyList<string> MissingAtBase,
    IReadOnlyList<string> MalformedAtBase,
    IReadOnlyList<AnchorFinding> Findings)
{
    /// <summary>Whether the base has moved on from where HEAD's history left it, so the change is measured from there.</summary>
    public bool BaseMovedOn => !string.Equals(BaseCommit, Commit, StringComparison.Ordinal);

    /// <summary>How many newly opened anchors are disclosed.</summary>
    public int Disclosed => Opened.Count(opening => opening.Disclosed);

    /// <summary>How many closures only repair the mark of work done before the change that closes them.</summary>
    public int Bookkeeping => Closed.Count(closing => closing.Bookkeeping);

    /// <summary>How many lost anchors were closed already where the change began, and so change no count.</summary>
    public int AlreadyClosed => Lost.Count(loss => loss.AlreadyClosed);

    /// <summary>
    /// The rise the balance counts: the anchors the change created less those its work closed - the change in open
    /// anchors, less those newly disclosed, plus the closures that are bookkeeping and the open anchors lost. The first two
    /// corrections pull opposite ways for one reason: a disclosed anchor records debt that already existed, so writing it
    /// down is not creating it, and a bookkeeping closure records work that already existed, so marking it is not doing
    /// it. A bookkeeping anchor, or a lost one that was open, still leaves the open count, as a row closed or gone must,
    /// while the change is credited with nothing for it.
    /// </summary>
    public int NetNew => Opened.Count - Disclosed - (Closed.Count - Bookkeeping);

    /// <summary>
    /// Whether the change created no more anchors than its work closed (<see cref="NetNew"/> is not positive) and no
    /// finding is fatal.
    /// </summary>
    public bool Passed => NetNew <= 0 && !Findings.Any(finding => finding.Severity == AnchorFindingSeverity.Fatal);
}
