using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>Checks that a change did not create more anchors than its work closed.</summary>
public interface IAnchorBalanceService
{
    /// <summary>
    /// Compares the registries in the working tree with the registries where HEAD's history - with what an unfinished
    /// merge brings in - left <paramref name="baseReference"/>'s: at the base itself, where it is an ancestor of HEAD.
    /// </summary>
    Task<AnchorBalanceReport> CheckAsync(string startDirectory, string? baseReference, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAnchorBalanceService"/>
/// <remarks>
/// Anchors are compared by id across both registries, so moving a row from one to the other is
/// neither progress nor regression. The count that must not rise is open anchors, less those newly
/// disclosed and plus those whose closure is bookkeeping or that were open and have no row now: a disclosed
/// anchor records debt that already existed, a bookkeeping closure work that already existed, and a lost row
/// no work at all. A lost row fails the balance whether it was open or closed, since a row moves between the
/// registries and is never deleted. The registries are also checked as they stand, because a closed anchor left
/// in the pending registry, an open one in the done registry, or an id with two rows makes both counts wrong.
/// </remarks>
public sealed class AnchorBalanceService(
    IHarnessContextLoader contextLoader,
    IAnchorRegistryLocator locator,
    IAnchorRegistryLock registryLock,
    IGitClient gitClient,
    IFileSystem fileSystem) : IAnchorBalanceService
{
    /// <summary>The base used when none is given.</summary>
    public const string DefaultBase = "HEAD";

    private const int ExcerptLength = 80;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IAnchorRegistryLocator _locator = locator;
    private readonly IAnchorRegistryLock _registryLock = registryLock;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<AnchorBalanceReport> CheckAsync(
        string startDirectory,
        string? baseReference,
        CancellationToken cancellationToken = default)
    {
        var reference = string.IsNullOrWhiteSpace(baseReference) ? DefaultBase : baseReference.Trim();

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var settings = context.Config.Anchors;
        var rules = AnchorIdRules.From(settings);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        foreach (var registry in registries.All.Where(registry => registry.IsIgnored))
        {
            // Treating an ignored registry as empty at the base would report every open anchor in it
            // as newly opened: a failure on every run, which a check is soon taught to ignore.
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{registry.RelativePath}' is ignored by git, so it has no history to compare against. "
                + "check-anchor-balance can only measure a registry git tracks.");
        }

        var root = context.Layout.RepositoryRoot;

        await _gitClient
            .RequireHeadAsync(root, "there is no point this change began from to compare against", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var baseCommit = await _gitClient.ResolveCommitAsync(root, reference, cancellationToken).ConfigureAwait(false);

        if (baseCommit is null)
        {
            // A shallow clone holds no commit before where its history was cut, so HEAD~1 there names none.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                await _gitClient.IsShallowAsync(root, cancellationToken).ConfigureAwait(false) == true
                    ? $"'{reference}' names no commit this clone holds, so there is nothing to compare against: its history is "
                      + "shallow, and the commit may lie beyond it. Fetch the rest of it (git fetch --unshallow), or fetch "
                      + "the base, and run again."
                    : $"'{reference}' does not name a commit, so there is nothing to compare against.");
        }

        // What the change did is measured from where its history left the base's, as --current-pr of
        // check-anchor-citations measures a branch. Compared with directly, a base that has moved on since counted its
        // own later changes, reversed, as the change's: an anchor it closed as one the change created, and one it
        // gained as one the change lost. Where the base is an ancestor of HEAD, that is the base itself. During an
        // unfinished merge the working tree already holds what the merge brings in, so the change is measured as the
        // commit finishing it would be: from where the base parts from HEAD merged with each commit coming in. Asked
        // by the names given rather than the commits they resolved to, so a refusal names them as they were written.
        var merging = await _gitClient.ListMergeHeadsAsync(root, cancellationToken).ConfigureAwait(false);

        var commit = await _gitClient.MergeBaseAsync(root, reference, ["HEAD", .. merging], cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{reference}' shares no history with HEAD{(merging.Count > 0 ? " or the commits its unfinished merge brings in" : string.Empty)}, "
                + "so there is no point this change began from to compare against. Where it should share some, git fsck "
                + "reports a commit between them that git cannot read.");

        var missingAtBase = new List<string>();
        var malformedAtBase = new List<string>();
        var atBase = new List<(AnchorRegistryDocument Document, AnchorRegistry Registry)>();

        foreach (var registry in registries.All)
        {
            var text = await _gitClient
                .ReadFileAtCommitAsync(root, commit, registry.RelativePath, cancellationToken)
                .ConfigureAwait(false);

            if (text is null)
            {
                missingAtBase.Add(registry.RelativePath);
                continue;
            }

            var document = AnchorRegistryDocument.Parse(text, rules);
            atBase.Add((document, registry));

            // A row the file hid there counts as absent there, so deleting it goes unseen and recovering it reads as new.
            // Noted rather than failed: no change can repair the history, and failing would fail the change repairing the file.
            if (!document.IsSound)
            {
                malformedAtBase.Add(registry.RelativePath);
            }
        }

        var findings = new List<AnchorFinding>();
        var now = new List<(AnchorRegistryDocument Document, AnchorRegistry Registry)>();

        // Both read as one moment: a row a change was moving between them would otherwise count as closed, or lost.
        foreach (var (registry, text) in _registryLock.ReadTogether(registries, _fileSystem))
        {
            if (text is null)
            {
                findings.Add(registry.Fatal(
                    registry.Kind == AnchorRegistryKind.Done
                        ? "there is no done registry, so closing an anchor has nowhere to move it and this check cannot tell a closed anchor from a lost one"
                        : "there is no pending registry, so no open anchor can be counted"));
                continue;
            }

            var document = AnchorRegistryDocument.Parse(text, rules);
            now.Add((document, registry));

            findings.AddRange(document.Findings.Select(registry.Finding));

            // A misfiled row is refused as it stands rather than compared with the base: an open row in
            // the done registry is never picked up as work, whatever the base held.
            findings.AddRange(document.Rows.Select(registry.Misfiling).OfType<AnchorFinding>());

            // And a row whose Status cell is not one of the four spellings, for the same reason and
            // in the same words 'read-anchors --lint' uses. Whether a row is closed is decided here
            // by what the cell OPENS with, so a cell nothing can parse still lands in one column or
            // the other and is silently counted. A consumer measured the consequence: --lint exited
            // 1 on three rows while this verb answered that the balance held, and both verbs cannot
            // be right about the same file.
            findings.AddRange(document.Rows
                .Where(row => !AnchorStatus.IsCanonical(row.Status))
                .Select(row => registry.Fatal(
                    row,
                    $"anchor '{row.Id}' has status '{row.Status}', which is not one of "
                    + $"{string.Join(" / ", AnchorStatus.Cells)}, so whether it is open cannot be "
                    + "counted")));

            // And, where a Trigger carries its row's verdict too, a row whose two cells disagree, as --lint reports
            // one: whether a closure is bookkeeping is read from the Trigger, so a Trigger contradicting its Status
            // would be counted from whichever cell was asked.
            findings.AddRange(document.Rows.Select(row => registry.SplitVerdict(row, settings)).OfType<AnchorFinding>());
        }

        // And an id with more than one row, as --lint reports one: everything here is counted by id, so two rows of one
        // id count as one, and what becomes of either cannot be told apart.
        findings.AddRange(AnchorEntry.Duplicates(now));

        var openAtBase = OpenAnchors(atBase);
        var openNow = OpenAnchors(now);
        var rowsAtBase = AnchorEntry.Of(atBase).ToLookup(entry => entry.Row.Id, AnchorIdMatch.Comparer);
        var rowsNow = AnchorEntry.Of(now).ToLookup(entry => entry.Row.Id, AnchorIdMatch.Comparer);

        // An id with a row where the change began and none now was lost - deleted, or renamed by hand - whether that row
        // was open or closed: a row moves between the registries and is never deleted. One open there leaves the open
        // count, and that is no work done, so it is not credited; one closed there leaves no count, and no other check
        // compares the registries with where the change began - --lint reads them as they stand, and
        // check-anchor-citations notices only a citation the row leaves behind. Each is said of the registry its row was
        // read from there, its open row's where it had one. Judged only where both registries were read whole: a missing
        // or malformed one is its own finding, and every row it hides would read as lost - every closed anchor the done
        // registry ever recorded among them.
        var readWhole = now.Count == registries.All.Count && now.All(pair => pair.Document.IsSound);

        var lost = rowsAtBase
            .Where(rows => readWhole && !rowsNow.Contains(rows.Key))
            .Select(rows => openAtBase.TryGetValue(rows.Key, out var open)
                ? (Loss: new AnchorLoss(rows.Key, AlreadyClosed: false), Entry: open)
                : (Loss: new AnchorLoss(rows.Key, AlreadyClosed: true), Entry: rows.First()))
            .OrderBy(pair => pair.Loss.Id, StringComparer.Ordinal)
            .ToList();

        // An id open where the change began and not now that still has rows, every one closed, was closed by the change:
        // its work, unless a row of it says the closure is bookkeeping, or it had a closed row there already beside the
        // open one, when the change took away only the open copy of a duplicate.
        bool ClosedAlready(string id) => rowsAtBase[id].Any(entry => entry.Row.IsClosed);

        var closed = openAtBase.Keys
            .Where(id => !openNow.ContainsKey(id) && rowsNow.Contains(id))
            .Order(StringComparer.Ordinal)
            .Select(id => new AnchorClosing(id, ClosedAlready(id) || rowsNow[id].Any(entry => entry.Row.IsBookkeepingClosure(settings))))
            .ToList();

        findings.AddRange(closed
            .Where(closing => ClosedAlready(closing.Id))
            .Select(closing => rowsNow[closing.Id].First())
            .Select(entry => entry.Registry.Note(
                entry.Row,
                $"anchor '{entry.Row.Id}' had a closed row already where this change began, beside its open one, so closing "
                + "it is no work and is not credited")));

        // A closure whose Trigger opens with the bookkeeping pair that is not read as one is credited as work, which its
        // writer may not have meant: said, and counted as it reads.
        findings.AddRange(closed
            .SelectMany(closing => rowsNow[closing.Id])
            .Select(entry => entry.Registry.UnreadBookkeepingPair(entry.Row, settings))
            .OfType<AnchorFinding>());

        // A row whose Anchor cell did not name one id there read as the cell's text, or as the first id it named, so
        // repairing that cell, as --lint asks, changes the id it reads as, which cannot be told from a loss by id: noted,
        // since failing would fail the change repairing it.
        findings.AddRange(lost.Select(pair => rules.NamesOneId(pair.Entry.Row.AnchorCell)
            ? pair.Entry.Registry.Fatal(
                $"anchor '{pair.Loss.Id}' was {(pair.Loss.AlreadyClosed ? "closed" : "open")} where this change began and "
                + "neither registry holds it now: a row moves between the registries and is never deleted, so its row was "
                + "lost - deleted, or its id changed by hand")
            : pair.Entry.Registry.Note(
                $"the row read as '{pair.Loss.Id}' where this change began did not name one id in its Anchor cell, and no "
                + $"row reads as '{pair.Loss.Id}' now: repairing that cell changes what its row reads as, so whether the row "
                + "was repaired or lost cannot be told")));

        var opened = openNow
            .Where(pair => !openAtBase.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new AnchorOpening(
                pair.Key,
                AnchorCells.Excerpt(pair.Value.Row.Trigger, ExcerptLength),
                AnchorStatus.IsDisclosed(pair.Value.Row.Status)))
            .ToList();

        return new AnchorBalanceReport(
            Base: reference,
            BaseCommit: baseCommit,
            Commit: commit,
            Merging: merging,
            OpenAtBase: openAtBase.Count,
            OpenNow: openNow.Count,
            Closed: closed,
            Lost: [.. lost.Select(pair => pair.Loss)],
            Opened: opened,
            MissingAtBase: missingAtBase,
            MalformedAtBase: malformedAtBase,
            Findings: [.. findings.OrderBy(finding => finding.File, StringComparer.Ordinal).ThenBy(finding => finding.LineNumber)]);
    }

    /// <summary>
    /// Every id with at least one open row, across both registries, with that row and the registry holding it. An id
    /// with an open row and a closed one counts as open: a closed copy must never hide an open original.
    /// </summary>
    private static Dictionary<string, AnchorEntry> OpenAnchors(IEnumerable<(AnchorRegistryDocument Document, AnchorRegistry Registry)> documents)
    {
        var open = new Dictionary<string, AnchorEntry>(AnchorIdMatch.Comparer);

        foreach (var (document, registry) in documents)
        {
            foreach (var row in document.Rows.Where(row => !row.IsClosed))
            {
                open.TryAdd(row.Id, new AnchorEntry(row, registry));
            }
        }

        return open;
    }
}
