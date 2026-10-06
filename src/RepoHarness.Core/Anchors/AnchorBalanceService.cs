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
/// disclosed and plus those whose closure is bookkeeping or whose row was lost: a disclosed anchor records
/// debt that already existed, a bookkeeping closure work that already existed, and a lost row no work at
/// all. The registries are also checked as they stand, because a closed anchor left in the pending
/// registry, or an open one in the done registry, makes both counts wrong.
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

        var openAtBase = OpenAnchors(atBase);
        var openNow = OpenAnchors(now);
        var rowsNow = AnchorEntry.Of(now).ToLookup(entry => entry.Row.Id, AnchorIdMatch.Comparer);

        // An id open where the change began and not now either still has rows, every one closed, or has none: a row moves
        // between the registries and is never deleted, so one with none was lost - deleted, or renamed by hand - and its
        // leaving the open count is no work done, so it is not credited.
        var gone = openAtBase.Keys.Where(id => !openNow.ContainsKey(id)).Order(StringComparer.Ordinal).ToList();
        var lost = gone.Where(id => !rowsNow.Contains(id)).ToList();

        var closed = gone
            .Where(rowsNow.Contains)
            .Select(id => new AnchorClosing(id, rowsNow[id].Any(entry => entry.Row.IsBookkeepingClosure(settings))))
            .ToList();

        // A closure whose Trigger opens with the bookkeeping pair that is not read as one is credited as work, which its
        // writer may not have meant: said, and counted as it reads.
        findings.AddRange(closed
            .SelectMany(closing => rowsNow[closing.Id])
            .Select(entry => entry.Registry.UnreadBookkeepingPair(entry.Row, settings))
            .OfType<AnchorFinding>());

        // A lost row is a finding only where both registries were read whole: a missing or malformed one is its own
        // finding, and every row it hides would read as lost.
        if (now.Count == registries.All.Count && now.All(pair => pair.Document.IsSound))
        {
            findings.AddRange(lost.Select(id => openAtBase[id].Registry.Fatal(
                $"anchor '{id}' was open where this change began and neither registry holds it now: a row moves between "
                + "the registries and is never deleted, so its row was lost - deleted, or its id changed by hand")));
        }

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
            Lost: lost,
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
