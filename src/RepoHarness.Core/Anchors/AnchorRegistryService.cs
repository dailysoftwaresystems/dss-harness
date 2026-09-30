using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>Adds, changes, reads and checks anchors.</summary>
public interface IAnchorRegistryService
{
    /// <summary>Adds a new anchor to the registry its status belongs in.</summary>
    Task<AnchorChange> WriteAsync(string startDirectory, AnchorWriteRequest request, bool dryRun, CancellationToken cancellationToken = default);

    /// <summary>Changes an existing anchor, moving it when its new status belongs in the other registry.</summary>
    Task<AnchorChange> SetAsync(string startDirectory, AnchorSetRequest request, bool dryRun, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the registries hold each of the request's rows as declared, all or nothing: a new id written as write-anchor
    /// writes one, an existing one changed as set-anchor changes it - naming only the cells that differ - and one already
    /// as declared left alone. A row no registry holds is written only where the request names it new, and a cell whose
    /// stored text does not survive only where the request accepts losing it. Every row is checked before any is written,
    /// and every refusal is named; a write that fails, or rows that do not read back as declared, put both registries back
    /// byte for byte.
    /// </summary>
    /// <param name="startDirectory">A directory in the tree whose registries are written.</param>
    /// <param name="request">The rows, and what the person applying them allows.</param>
    /// <param name="mode">Whether to plan them, check them as applying them would, or write them.</param>
    /// <param name="cancellationToken">Stops it before the registries are opened.</param>
    Task<AnchorBatch> ApplyAsync(string startDirectory, AnchorBatchRequest request, AnchorBatchMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which of <paramref name="rows"/> the registries do not hold as declared, and how each differs, read as they are now:
    /// nothing is checked against the rules a write is held to, and nothing is written.
    /// </summary>
    /// <param name="startDirectory">A directory in the tree whose registries are read.</param>
    /// <param name="rows">The rows as declared.</param>
    /// <param name="cancellationToken">Stops it before the registries are read.</param>
    Task<IReadOnlyList<AnchorDifference>> DifferencesAsync(string startDirectory, IReadOnlyList<AnchorRowDeclaration> rows, CancellationToken cancellationToken = default);

    /// <summary>Finds anchors by exact id.</summary>
    Task<AnchorLookup> ReadAsync(string startDirectory, IReadOnlyList<string> ids, AnchorScope scope, CancellationToken cancellationToken = default);

    /// <summary>Lists anchors.</summary>
    Task<IReadOnlyList<AnchorEntry>> ListAsync(string startDirectory, AnchorListFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Reports every row a reader cannot rely on, and every structural problem in the registries.</summary>
    Task<IReadOnlyList<AnchorFinding>> LintAsync(string startDirectory, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAnchorRegistryService"/>
/// <remarks>
/// Where a row lives is decided by its status and nothing else: a closed anchor is in the done
/// registry, every other status in the pending one. A caller never names the destination, so a
/// live anchor cannot be filed in the archive, where nothing reads it as work.
/// </remarks>
public sealed class AnchorRegistryService(
    IHarnessContextLoader contextLoader,
    IAnchorRegistryLocator locator,
    IAnchorRegistryLock registryLock,
    IFileSystem fileSystem) : IAnchorRegistryService
{
    private const int NamespaceHintLimit = 8;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IAnchorRegistryLocator _locator = locator;
    private readonly IAnchorRegistryLock _registryLock = registryLock;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<AnchorChange> WriteAsync(
        string startDirectory,
        AnchorWriteRequest request,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var refusals = new Refusals();

        // Every value that can be judged alone is checked before the registries are opened, so a mistake in the arguments is
        // reported as that and never costs anyone the lock. What a value cites, and whether a line carries an id on, needs
        // the rows there are, and is judged under the lock.
        var row = ComposeNew(request, rules, context.Config.Anchors, refusals);
        refusals.ThrowIfAny();

        var door = DoorFor(context, []);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        return _registryLock.RunExclusive(registries, () =>
        {
            var (pending, done) = (Load(registries.Pending, rules), Load(registries.Done, rules));
            var existing = Matches(request.Id, (pending, registries.Pending), (done, registries.Done));

            if (existing.Count > 0)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{request.Id}' already has a row in {Locations(existing)}. write-anchor adds a new anchor; "
                    + "change an existing one with set-anchor.");
            }

            JudgeProse(request.Id, ProseCells(null, request.Trigger, request.ClosingWork, request.CrossRefs), Known(pending, done, [], request.Id), door, refusals, inBatch: false);
            refusals.ThrowIfAny();

            var placement = PlaceNew(row!, registries, pending, done, written: !dryRun);

            if (!dryRun)
            {
                WriteAll(placement.Writes);
            }

            return placement.Change;
        });
    }

    public async Task<AnchorChange> SetAsync(
        string startDirectory,
        AnchorSetRequest request,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var refusals = new Refusals();
        var cells = ComposeChanges(request, rules, refusals);
        refusals.ThrowIfAny();

        var door = DoorFor(context, []);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        return _registryLock.RunExclusive(registries, () =>
        {
            var (pending, done) = (Load(registries.Pending, rules), Load(registries.Done, rules));
            var found = Find(request, registries, pending, done);

            JudgeProse(request.Id, ProseCells(found.Row, request.Trigger, request.ClosingWork, request.CrossRefs), Known(pending, done, [], request.Id), door, refusals, inBatch: false);
            refusals.ThrowIfAny();

            var placement = PlaceChange(found, request, cells!, registries, pending, done, rules, context.Config.Anchors, written: !dryRun);

            if (!dryRun)
            {
                WriteAll(placement.Writes);
            }

            return placement.Change;
        });
    }

    public async Task<AnchorBatch> ApplyAsync(
        string startDirectory,
        AnchorBatchRequest request,
        AnchorBatchMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var rows = request.Rows;

        if (rows.Count == 0 && request.New.Count == 0 && request.AcceptLost.Count == 0)
        {
            return new AnchorBatch([], []);
        }

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var settings = context.Config.Anchors;
        var door = DoorFor(context, request.Roots);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);
        var batch = rows.Select(row => row.Id).ToHashSet(AnchorIdMatch.Comparer);
        var named = request.New.ToHashSet(AnchorIdMatch.Comparer);
        var accepted = request.AcceptLost.ToHashSet();
        var written = mode == AnchorBatchMode.Apply;

        return _registryLock.RunExclusive(registries, () =>
        {
            // Each row is planned against the registries as the rows before it left them, exactly as one write-anchor or
            // set-anchor after another would find them, and written in the same order: a row that moves between the two
            // files is still written destination first.
            var texts = registries.All.ToDictionary(registry => registry.Kind, registry => Load(registry, rules).ToText());
            var outcomes = new List<AnchorRowOutcome>();
            var problems = new List<string>();
            var writes = new List<(AnchorRegistry Registry, string Text)>();

            // What the rows made of each acceptance: a cell that loses stored text, a row already as declared - which an
            // acceptance of an earlier run left as it is - or a row whose declaration did not read, whose loss is not judged.
            var losing = new HashSet<AnchorRowCell>();
            var standing = new HashSet<string>(AnchorIdMatch.Comparer);
            var unjudged = new HashSet<string>(AnchorIdMatch.Comparer);

            foreach (var declared in rows)
            {
                try
                {
                    var pending = Parse(registries.Pending, texts[AnchorRegistryKind.Pending], rules);
                    var done = Parse(registries.Done, texts[AnchorRegistryKind.Done], rules);
                    var matches = Matches(declared.Id, (pending, registries.Pending), (done, registries.Done));
                    var known = Known(pending, done, batch, declared.Id);

                    // Every refusal of a row is named with the rest, so one run shows what there is to correct in it.
                    var refusals = new Refusals();
                    Placement? placement = null;
                    AnchorRowOutcome? outcome = null;

                    if (matches.Count == 0)
                    {
                        // A row no registry holds is one the batch makes, and a typo in an existing row's id is one too: it is
                        // made only where the person applying the batch said it is new, as write-anchor is only ever asked to
                        // make one.
                        if (!named.Contains(declared.Id))
                        {
                            refusals.Refuse(NotNamedNew(declared.Id, pending, done, registries));
                        }

                        if (declared.Priority is null)
                        {
                            refusals.Usage($"'{declared.Id}' has no row yet, and a new anchor needs a priority: declare one of {string.Join(", ", AnchorPriority.Bands)}.");
                        }

                        // Composed with a stand-in priority where it declares none, only so the rest of it is judged too:
                        // refused for that, it is never written.
                        var row = ComposeNew(AsWrite(declared) with { Priority = declared.Priority ?? AnchorPriority.Bands[0] }, rules, settings, refusals);

                        JudgeProse(declared.Id, ProseCells(null, declared.Trigger, declared.ClosingWork, declared.CrossRefs), known, door, refusals, inBatch: true);

                        if (!refusals.Any)
                        {
                            placement = PlaceNew(row!, registries, pending, done, written);
                            outcome = new AnchorRowOutcome(declared.Id, AnchorRowAction.New, placement.Change);
                        }
                    }
                    else if (matches.Count > 1)
                    {
                        refusals.Refuse(Duplicate(declared.Id, matches).Message);
                    }
                    else
                    {
                        var (existing, registry) = (matches[0].Entry.Row, matches[0].Entry.Registry);
                        AnchorSetRequest? change = null;

                        try
                        {
                            change = ChangesTo(declared, existing);
                        }
                        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.UsageError)
                        {
                            refusals.Usage(ex.Message);
                            unjudged.Add(declared.Id);
                        }

                        // A row already as declared is left alone, named new or not: one named new is the batch's own.
                        if (change is { HasChanges: false })
                        {
                            outcomes.Add(new AnchorRowOutcome(
                                declared.Id,
                                AnchorRowAction.AlreadyIn,
                                new AnchorChange(declared.Id, registry, registry, existing.Status, existing.Status, [], Written: false)));
                            standing.Add(declared.Id);
                            continue;
                        }

                        if (change is not null)
                        {
                            if (named.Contains(declared.Id))
                            {
                                refusals.Refuse(
                                    $"'{declared.Id}' was named new, but {Locations(matches)} holds it, other than as declared: drop "
                                    + $"{AnchorBatchRequest.NewOption} {declared.Id} to change that row, or give the new row an id of its own.");
                            }

                            var lost = Lost(change, existing, accepted);
                            losing.UnionWith(lost.Select(cell => cell.Cell));

                            var cells = ComposeChanges(change, rules, refusals);

                            JudgeProse(declared.Id, ProseCells(existing, change.Trigger, change.ClosingWork, change.CrossRefs), known, door, refusals, inBatch: true);

                            // Planned, a loss is shown for a person to read; checked or written, it is refused until they accept
                            // it - and it is named with the rest wherever the row is refused anyway.
                            if (mode != AnchorBatchMode.Plan || refusals.Any)
                            {
                                foreach (var cell in lost.Where(cell => !cell.Accepted))
                                {
                                    refusals.Refuse(
                                        $"its {cell.Cell.Cell} does not keep its stored text word for word - {cell.Diff} - so it is written "
                                        + $"only with {AnchorBatchRequest.AcceptLostOption} {cell.Cell}.");
                                }
                            }

                            if (!refusals.Any)
                            {
                                placement = PlaceChange(Found.Of(matches[0]), change, cells!, registries, pending, done, rules, settings, written);
                                outcome = new AnchorRowOutcome(declared.Id, AnchorRowAction.Changed, placement.Change) { Lost = lost };
                            }
                        }
                    }

                    if (refusals.Any)
                    {
                        problems.AddRange(refusals.Messages.Select(message => $"{declared.Id}: {message}"));
                        continue;
                    }

                    outcomes.Add(outcome!);

                    foreach (var write in placement!.Writes)
                    {
                        texts[write.Registry.Kind] = write.Text;
                        writes.Add(write);
                    }
                }
                catch (HarnessException ex) when (ex.ExitCode is HarnessExit.UsageError or HarnessExit.Refused)
                {
                    problems.Add($"{declared.Id}: {ex.Message}");
                }
            }

            // What was allowed and matches nothing is a typo, or left over from a batch before: never silently dropped.
            problems.AddRange(named.Where(id => !batch.Contains(id)).Order(StringComparer.Ordinal)
                .Select(id => $"{AnchorBatchRequest.NewOption} {id} names no row of the batch: drop it, or correct the id."));
            problems.AddRange(accepted
                .Where(cell => !losing.Contains(cell) && !standing.Contains(cell.Id) && !unjudged.Contains(cell.Id))
                .Select(cell => cell.ToString())
                .Order(StringComparer.Ordinal)
                .Select(cell => $"{AnchorBatchRequest.AcceptLostOption} {cell} names a cell that keeps its stored text here: drop it, or correct it."));

            // Every row is checked, and every one refused is named, before any is written.
            if (problems.Count > 0 || !written || writes.Count == 0)
            {
                return new AnchorBatch(outcomes, problems);
            }

            var snapshot = registries.All.ToDictionary(registry => registry, registry => _fileSystem.ReadAllBytes(registry.FullPath));

            try
            {
                WriteAll(writes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Restored(outcomes, snapshot, $"writing the rows failed: {ex.Message.TrimEnd('.')}");
            }

            // Every row read back and compared: that a write returned is not proof of what the file holds.
            var wrong = Verify(rows, registries, rules);

            return wrong.Count > 0
                ? Restored(outcomes, snapshot, $"the rows did not read back as declared: {string.Join("; ", wrong)}")
                : new AnchorBatch(outcomes, []);
        });
    }

    public async Task<IReadOnlyList<AnchorDifference>> DifferencesAsync(
        string startDirectory,
        IReadOnlyList<AnchorRowDeclaration> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return [];
        }

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        // Read under the lock, both registries together, so a row moving between them is never seen in neither or both.
        return _registryLock.RunExclusive(registries, () => Differing(rows, registries, Load(registries.Pending, rules), Load(registries.Done, rules)));
    }

    public async Task<AnchorLookup> ReadAsync(
        string startDirectory,
        IReadOnlyList<string> ids,
        AnchorScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0 || ids.Any(string.IsNullOrWhiteSpace))
        {
            throw Usage("Give at least one anchor id, and no empty ones.");
        }

        var documents = await LoadForReadingAsync(startDirectory, cancellationToken).ConfigureAwait(false);

        var entries = Entries(documents).ToList();
        var inScope = entries.Where(entry => InScope(entry.Registry, scope)).ToList();

        var results = ids
            .Distinct(StringComparer.Ordinal)
            .Select(id =>
            {
                var matches = inScope.Where(entry => AnchorIdMatch.Comparer.Equals(entry.Row.Id, id)).ToList();
                var hint = matches.Count == 0 ? SameNamespace(id, entries) : [];
                return new AnchorLookupResult(id, matches, hint);
            })
            .ToList();

        return new AnchorLookup(results);
    }

    public async Task<IReadOnlyList<AnchorEntry>> ListAsync(
        string startDirectory,
        AnchorListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.OnlyOpen && filter.OnlyClosed)
        {
            throw Usage("--open and --closed cannot be combined: together they select nothing.");
        }

        var bands = filter.Bands.Select(RequirePriority).ToHashSet(StringComparer.Ordinal);

        var documents = await LoadForReadingAsync(startDirectory, cancellationToken).ConfigureAwait(false);

        return [.. Entries(documents)
            .Where(entry => InScope(entry.Registry, filter.Scope))
            .Where(entry => bands.Count == 0 || bands.Contains(entry.Row.Priority))
            .Where(entry => !filter.OnlyOpen || !entry.Row.IsClosed)
            .Where(entry => !filter.OnlyClosed || entry.Row.IsClosed)];
    }

    public async Task<IReadOnlyList<AnchorFinding>> LintAsync(string startDirectory, CancellationToken cancellationToken = default)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        var findings = new List<AnchorFinding>();
        var documents = new List<(AnchorRegistryDocument Document, AnchorRegistry Registry)>();

        foreach (var registry in registries.All)
        {
            if (!_fileSystem.FileExists(registry.FullPath))
            {
                findings.Add(new AnchorFinding(
                    registry.RelativePath,
                    0,
                    AnchorFindingSeverity.Fatal,
                    $"there is no {registry.Name} registry here; run '{ToolPackage.Command} init' to create it"));
                continue;
            }

            var document = AnchorRegistryDocument.Parse(_fileSystem.ReadAllText(registry.FullPath), rules);
            documents.Add((document, registry));

            findings.AddRange(document.Findings.Select(finding =>
                new AnchorFinding(registry.RelativePath, finding.LineNumber, finding.Severity, finding.Message)));

            foreach (var row in document.Rows)
            {
                findings.AddRange(LintRow(row, registry, rules, context.Config.Anchors));
            }
        }

        // One id, one row, across both registries: a duplicate hands a reader two histories under one name.
        foreach (var group in Entries(documents).GroupBy(entry => entry.Row.Id, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var locations = string.Join(", ", group.Select(entry => $"{entry.Registry.RelativePath}:{entry.Row.LineNumber}"));

            findings.AddRange(group.Select(entry => new AnchorFinding(
                entry.Registry.RelativePath,
                entry.Row.LineNumber,
                AnchorFindingSeverity.Fatal,
                $"'{group.Key}' has {group.Count()} rows ({locations}); one id has one row")));
        }

        return [.. findings
            .OrderBy(finding => finding.File == registries.Pending.RelativePath ? 0 : 1)
            .ThenBy(finding => finding.LineNumber)];
    }

    private static IEnumerable<AnchorFinding> LintRow(AnchorRow row, AnchorRegistry registry, AnchorIdRules rules, AnchorSettings settings)
    {
        AnchorFinding Finding(string message) =>
            new(registry.RelativePath, row.LineNumber, AnchorFindingSeverity.Fatal, message);

        if (row.CellCount != AnchorRow.ExpectedCellCount)
        {
            yield return Finding(
                $"{row.CellCount} cells, not {AnchorRow.ExpectedCellCount}: the cells after the break are shifted or dropped");
        }

        if (!rules.IsBareBacktickedId(row.AnchorCell))
        {
            yield return Finding($"the Anchor cell is not one id in backticks: {AnchorCells.Excerpt(row.AnchorCell, 70)}");
        }

        if (!AnchorPriority.IsBand(row.Priority))
        {
            yield return Finding($"priority '{row.Priority}' is not one of {string.Join(' ', AnchorPriority.Bands)}");
        }

        if (!AnchorStatus.IsCanonical(row.Status))
        {
            yield return Finding($"status '{row.Status}' is not one of {string.Join(" / ", AnchorStatus.Cells)}");
        }

        if (row.Trigger.Length == 0)
        {
            yield return Finding("the Trigger cell is empty, so the row explains nothing");
        }

        if (settings.TriggerCarriesVerdict && AnchorStatus.SplitVerdict(row.Status, row.Trigger) is { } split)
        {
            yield return Finding($"{split}: anchors.triggerCarriesVerdict holds a row to one verdict, stated in both");
        }

        if (registry.Misfiling(row) is { } misfiled)
        {
            yield return misfiled;
        }
    }

    private async Task<List<(AnchorRegistryDocument Document, AnchorRegistry Registry)>> LoadForReadingAsync(
        string startDirectory,
        CancellationToken cancellationToken)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        // Reading takes no lock: every write replaces a whole file in one rename, so a reader sees
        // either the old file or the new one, never a mixture.
        return [.. registries.All.Select(registry => (Load(registry, rules), registry))];
    }

    /// <summary>
    /// Reads a registry for a command that reads or changes anchors. A file with a structural problem is
    /// refused rather than read around: its rows could be miscounted, or given a second row, without anyone
    /// being told. Lint and the balance parse the files themselves, because reporting those problems is
    /// their job.
    /// </summary>
    private AnchorRegistryDocument Load(AnchorRegistry registry, AnchorIdRules rules)
    {
        if (!_fileSystem.FileExists(registry.FullPath))
        {
            throw new HarnessException(
                HarnessExit.NotInitialized,
                $"There is no {registry.Name} anchor registry at '{registry.RelativePath}'. Run '{ToolPackage.Command} init' "
                + $"to create it, or correct {registry.Setting} in config.json.");
        }

        return Parse(registry, _fileSystem.ReadAllText(registry.FullPath), rules);
    }

    /// <summary>Parses a registry's text, refusing one with a structural problem as <see cref="Load"/> does.</summary>
    private static AnchorRegistryDocument Parse(AnchorRegistry registry, string text, AnchorIdRules rules)
    {
        var document = AnchorRegistryDocument.Parse(text, rules);
        document.EnsureSound(registry.RelativePath);
        return document;
    }

    private static IEnumerable<AnchorEntry> Entries(IEnumerable<(AnchorRegistryDocument Document, AnchorRegistry Registry)> documents)
        => documents.SelectMany(pair => pair.Document.Rows.Select(row => new AnchorEntry(row, pair.Registry)));

    private static List<(AnchorEntry Entry, AnchorRegistryDocument Document)> Matches(
        string id,
        params (AnchorRegistryDocument Document, AnchorRegistry Registry)[] documents)
        => [.. documents.SelectMany(pair => pair.Document.Rows
            .Where(row => string.Equals(row.Id, id, StringComparison.Ordinal))
            .Select(row => (new AnchorEntry(row, pair.Registry), pair.Document)))];

    private static string Locations(IEnumerable<(AnchorEntry Entry, AnchorRegistryDocument Document)> matches)
        => string.Join(", ", matches.Select(match => $"{match.Entry.Registry.RelativePath}:{match.Entry.Row.LineNumber}"));

    private static List<string> SameNamespace(string id, IEnumerable<AnchorEntry> entries)
    {
        var segments = id.Split('-');
        if (segments.Length < 2)
        {
            return [];
        }

        var prefix = segments[0] + "-" + segments[1];

        return [.. entries
            .Select(entry => entry.Row.Id)
            .Where(candidate => candidate == prefix || candidate.StartsWith(prefix + "-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(NamespaceHintLimit)];
    }

    private static bool InScope(AnchorRegistry registry, AnchorScope scope) => scope switch
    {
        AnchorScope.Pending => registry.Kind == AnchorRegistryKind.Pending,
        AnchorScope.Done => registry.Kind == AnchorRegistryKind.Done,
        _ => true,
    };

    private static string ScopeName(AnchorScope scope) => scope switch
    {
        AnchorScope.Pending => "the pending registry",
        AnchorScope.Done => "the done registry",
        _ => "either registry",
    };

    /// <summary>
    /// Composes and checks a new anchor's row, every refusal of a value in it collected in <paramref name="refusals"/>:
    /// everything write-anchor checks before the registries are opened, and every row a batch adds is held to. Null where
    /// a value is refused.
    /// </summary>
    private static NewRow? ComposeNew(AnchorWriteRequest request, AnchorIdRules rules, AnchorSettings settings, Refusals refusals)
    {
        var before = refusals.Count;

        if (!rules.IsMintable(request.Id))
        {
            refusals.Usage(
                $"'{request.Id}' cannot name a new anchor. A new id is '{rules.Prefix}-' followed by at least "
                + $"{rules.MinimumSegments} hyphen-separated segments of letters, digits or underscores, the "
                + $"first in capitals, for example {rules.Example()}. Spell a compound word as one segment "
                + "(ALWAYSINLINE, not ALWAYS-INLINE).");
        }

        var priority = Priority(request.Priority, refusals);
        var status = Status(request.Status, refusals);
        Trigger(request.Trigger, refusals);

        var trigger = Formatted(request.Trigger, AnchorCellNames.TriggerHeading, refusals);
        var closing = Formatted(request.ClosingWork, AnchorCellNames.ClosingHeading, refusals);
        var crossRefs = Formatted(request.CrossRefs, AnchorCellNames.CrossRefsHeading, refusals);

        if (refusals.Count > before)
        {
            return null;
        }

        var rendered = AnchorStatus.Render(status!.Value);
        var row = ComposeRow($" `{request.Id}` ", $" {priority} ", $" {rendered} ", trigger!, closing!, crossRefs!);

        VerifyRow(row, request.Id, rules);
        OneVerdict(row, settings, refusals);

        return refusals.Count > before ? null : new NewRow(request.Id, row, rendered);
    }

    /// <summary>
    /// Composes and checks the cells a change names, every refusal of a value in it collected in
    /// <paramref name="refusals"/>: everything set-anchor checks before the registries are opened. Only the cells named are
    /// rebuilt; every other cell is written back exactly as it was read. Null where a value is refused.
    /// </summary>
    private static CellChanges? ComposeChanges(AnchorSetRequest request, AnchorIdRules rules, Refusals refusals)
    {
        var before = refusals.Count;

        if (!rules.IsWellFormed(request.Id))
        {
            refusals.Usage(
                $"'{request.Id}' is not an anchor id: an id is '{rules.Prefix}-' followed by hyphen-separated "
                + "letters, digits or underscores.");
        }

        if (!request.HasChanges)
        {
            refusals.Usage(
                $"Nothing to change. Give at least one of {string.Join(", ", new[] { AnchorCellNames.Priority, AnchorCellNames.Status, AnchorCellNames.Trigger, AnchorCellNames.Closing }.Select(AnchorCellNames.OptionOf))} "
                + $"or {AnchorCellNames.OptionOf(AnchorCellNames.CrossRefs)}.");
        }

        var priority = request.Priority is null ? null : Priority(request.Priority, refusals);
        var status = request.Status is null ? null : Status(request.Status, refusals);

        if (request.Trigger is not null)
        {
            Trigger(request.Trigger, refusals);
        }

        var trigger = request.Trigger is null ? null : Formatted(request.Trigger, AnchorCellNames.TriggerHeading, refusals);
        var closing = request.ClosingWork is null ? null : Formatted(request.ClosingWork, AnchorCellNames.ClosingHeading, refusals);
        var crossRefs = request.CrossRefs is null ? null : Formatted(request.CrossRefs, AnchorCellNames.CrossRefsHeading, refusals);

        return refusals.Count > before
            ? null
            : new CellChanges(priority, status is null ? null : AnchorStatus.Render(status.Value), trigger, closing, crossRefs);
    }

    /// <summary>Places a new row, composed and judged, in the registry its status belongs in.</summary>
    private static Placement PlaceNew(NewRow row, AnchorRegistries registries, AnchorRegistryDocument pending, AnchorRegistryDocument done, bool written)
    {
        var destination = registries.HomeOf(row.Status);
        var document = destination.Kind == AnchorRegistryKind.Pending ? pending : done;

        document.AppendRow(row.Row);

        return new Placement(new AnchorChange(row.Id, null, destination, null, row.Status, [], written), [(destination, document.ToText())]);
    }

    /// <summary>The one row a change is for: refused where it has no row in scope, or more than one anywhere.</summary>
    private static Found Find(AnchorSetRequest request, AnchorRegistries registries, AnchorRegistryDocument pending, AnchorRegistryDocument done)
    {
        var all = Matches(request.Id, (pending, registries.Pending), (done, registries.Done));
        var inScope = all.Where(match => InScope(match.Entry.Registry, request.Scope)).ToList();

        if (inScope.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"No row for '{request.Id}' in {ScopeName(request.Scope)}. set-anchor changes an existing "
                + "anchor; a new one is added with write-anchor.");
        }

        if (all.Count > 1)
        {
            throw Duplicate(request.Id, all);
        }

        return Found.Of(inScope[0]);
    }

    /// <summary>Places a change to the row <paramref name="found"/>, moving it where its new status belongs.</summary>
    private static Placement PlaceChange(
        Found found,
        AnchorSetRequest request,
        CellChanges cells,
        AnchorRegistries registries,
        AnchorRegistryDocument pending,
        AnchorRegistryDocument done,
        AnchorIdRules rules,
        AnchorSettings settings,
        bool written)
    {
        var (existing, source, sourceDocument, raw) = (found.Row, found.Source, found.Document, found.Raw);

        var row = ComposeRow(
            raw[0],
            cells.Priority is null ? raw[1] : $" {cells.Priority} ",
            cells.Status is null ? raw[2] : $" {cells.Status} ",
            cells.Trigger ?? raw[3],
            cells.ClosingWork ?? raw[4],
            cells.CrossRefs ?? raw[5]);

        VerifyRow(row, existing.Id, rules);

        var verdict = new Refusals();
        OneVerdict(row, settings, verdict);
        verdict.ThrowIfAny();

        var fields = new List<AnchorFieldChange>();
        AddChange(fields, AnchorCellNames.Priority, existing.Priority, cells.Priority);
        AddChange(fields, AnchorCellNames.Status, existing.Status, cells.Status);

        foreach (var cell in ProseCells(existing, request.Trigger, request.ClosingWork, request.CrossRefs))
        {
            AddChange(fields, cell.Name, cell.Before!, cell.After);
        }

        var statusAfter = AnchorCells.Split(row)[3].Trim();
        var destination = registries.HomeOf(statusAfter);
        var destinationDocument = destination.Kind == AnchorRegistryKind.Pending ? pending : done;
        var writes = new List<(AnchorRegistry Registry, string Text)>();

        if (destination.Kind == source.Kind)
        {
            sourceDocument.ReplaceRow(existing, row);
            writes.Add((destination, sourceDocument.ToText()));
        }
        else
        {
            destinationDocument.AppendRow(row);
            sourceDocument.RemoveRow(existing);

            // The destination is written first. Two files cannot be replaced in one step, and an
            // interruption between the writes must leave the row in both registries, where the
            // next change refuses the duplicate loudly, rather than in neither, where it would be
            // gone and would read, to every count, exactly like an anchor that was closed.
            writes.Add((destination, destinationDocument.ToText()));
            writes.Add((source, sourceDocument.ToText()));
        }

        return new Placement(new AnchorChange(existing.Id, source, destination, existing.Status, statusAfter, fields, written), writes);
    }

    /// <summary>
    /// The change that makes <paramref name="existing"/> what <paramref name="declared"/> declares, naming only the cells
    /// that differ, so a cell already as declared keeps its stored bytes; nothing to change where it already is.
    /// </summary>
    private static AnchorSetRequest ChangesTo(AnchorRowDeclaration declared, AnchorRow existing)
    {
        var differs = Differences(declared, existing);

        return new AnchorSetRequest(declared.Id)
        {
            Priority = differs.Priority,
            Status = differs.Status is null ? null : declared.Status,
            Trigger = differs.Trigger ? declared.Trigger : null,
            ClosingWork = differs.ClosingWork ? declared.ClosingWork : null,
            CrossRefs = differs.CrossRefs ? declared.CrossRefs : null,
        };
    }

    /// <summary>
    /// How <paramref name="existing"/> differs from what <paramref name="declared"/> declares: the one comparison that
    /// decides what a batch changes and whether its rows read back as declared. Each text cell is compared as it reads,
    /// its pipes plain - as the parser gives a stored cell back - never as it is written, escaped.
    /// </summary>
    private static RowDifferences Differences(AnchorRowDeclaration declared, AnchorRow existing)
    {
        var status = AnchorStatus.Render(RequireStatus(declared.Status));
        var priority = declared.Priority is null ? null : RequirePriority(declared.Priority);

        return new RowDifferences(
            status != existing.Status ? status : null,
            priority is not null && priority != existing.Priority ? priority : null,
            AnchorCells.Flatten(declared.Trigger) != existing.Trigger,
            AnchorCells.Flatten(declared.ClosingWork) != existing.ClosingWork,
            AnchorCells.Flatten(declared.CrossRefs) != existing.CrossRefs);
    }

    /// <summary>
    /// Which of <paramref name="rows"/> the registries do not hold as declared, read back from disk: one row for each id,
    /// in the registry its status belongs in, with every declared cell.
    /// </summary>
    private IReadOnlyList<string> Verify(IReadOnlyList<AnchorRowDeclaration> rows, AnchorRegistries registries, AnchorIdRules rules)
    {
        try
        {
            return [.. Differing(rows, registries, Load(registries.Pending, rules), Load(registries.Done, rules))
                .Select(difference => $"'{difference.Id}': {difference.How}")];
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
        {
            // A registry that cannot be read back is not one proved to hold the rows: it is put back like any other.
            return [ex.Message.TrimEnd('.')];
        }
    }

    /// <summary>
    /// Which of <paramref name="rows"/> the registries do not hold as declared, and how: one row for each id, in the
    /// registry its status belongs in, with every declared cell. A declaration whose status or priority does not read is
    /// said to differ, as such, rather than refused: the comparison holds a row to no rule a write is held to.
    /// </summary>
    private static List<AnchorDifference> Differing(
        IReadOnlyList<AnchorRowDeclaration> rows,
        AnchorRegistries registries,
        AnchorRegistryDocument pending,
        AnchorRegistryDocument done)
    {
        var differing = new List<AnchorDifference>();

        foreach (var declared in rows)
        {
            var matches = Matches(declared.Id, (pending, registries.Pending), (done, registries.Done));

            if (matches.Count != 1)
            {
                differing.Add(new AnchorDifference(declared.Id, matches.Count == 0 ? "it has no row in either registry" : $"it has {matches.Count} rows"));
                continue;
            }

            if (!AnchorStatus.TryParse(declared.Status, out var state) || (declared.Priority is { } band && !AnchorPriority.TryNormalize(band, out _)))
            {
                differing.Add(new AnchorDifference(declared.Id, $"what was declared of it, status '{declared.Status}' and priority '{declared.Priority}', does not read as a row"));
                continue;
            }

            var (row, registry) = (matches[0].Entry.Row, matches[0].Entry.Registry);
            var cells = Differences(declared, row);
            var differs = new List<string>();

            if (cells.Status is not null)
            {
                differs.Add($"its status is '{row.Status}'");
            }

            if (cells.Priority is not null)
            {
                differs.Add($"its priority is '{row.Priority}'");
            }

            if (registry.Kind != AnchorRegistry.KindFor(AnchorStatus.Render(state)))
            {
                differs.Add($"it is in the {registry.Name} registry");
            }

            if (cells.Trigger)
            {
                differs.Add($"its {AnchorCellNames.TriggerHeading} differs");
            }

            if (cells.ClosingWork)
            {
                differs.Add($"its {AnchorCellNames.ClosingHeading} differs");
            }

            if (cells.CrossRefs)
            {
                differs.Add($"its {AnchorCellNames.CrossRefsHeading} differ");
            }

            if (differs.Count > 0)
            {
                differing.Add(new AnchorDifference(declared.Id, string.Join(", ", differs)));
            }
        }

        return differing;
    }

    /// <summary>
    /// The cells of <paramref name="existing"/> that <paramref name="change"/> rewrites and whose stored text does not
    /// survive in what replaces it, each with its word diff and whether <paramref name="accepted"/> names it.
    /// </summary>
    private static List<AnchorLostCell> Lost(AnchorSetRequest change, AnchorRow existing, IReadOnlySet<AnchorRowCell> accepted)
    {
        var lost = new List<AnchorLostCell>();

        foreach (var cell in ProseCells(existing, change.Trigger, change.ClosingWork, change.CrossRefs).Where(cell => cell.After is not null))
        {
            var written = AnchorCells.Flatten(cell.After!);

            if (AnchorCellComparison.Fate(cell.Before!, written) == AnchorCellFate.Lost)
            {
                var named = new AnchorRowCell(change.Id, cell.Name);
                lost.Add(new AnchorLostCell(named, AnchorCellComparison.WordDiff(cell.Before!, written), accepted.Contains(named)));
            }
        }

        return lost;
    }

    /// <summary>
    /// What composing and judging a row takes in the tree <paramref name="context"/> is in: its ids' spelling, and the
    /// directories at its top - read now, and <paramref name="roots"/> beside them - where a path a cell cites starts.
    /// A listing that fails fails the write: an answer of none would pass unseen every path that check exists for.
    /// </summary>
    private Door DoorFor(HarnessContext context, IEnumerable<string> roots)
    {
        var rules = AnchorIdRules.From(context.Config.Anchors);
        var scanner = new AnchorIdScanner(rules);
        var root = context.Layout.RepositoryRoot;
        IReadOnlyList<string> listed;

        try
        {
            listed = [.. _fileSystem.EnumerateDirectories(root)
                .Select(directory => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)))
                .Where(name => !string.Equals(name, ".git", StringComparison.Ordinal))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The directories at the top of '{root}' cannot be listed, so whether a cell holds a path cut in two cannot be told: "
                + $"{ex.Message.TrimEnd('.')}. No row was written.");
        }

        return new Door(scanner, new AnchorCellCuts(scanner, listed.Concat(roots)));
    }

    /// <summary>
    /// Judges the prose cells a write names, collecting in <paramref name="refusals"/> every cut the door would store -
    /// an id or a path that a line break or a space cuts - and every id a cell newly cites that no row holds: every id a
    /// new row's cell cites, and every id a changed cell cites that its stored text did not. A citation, or a cut, the
    /// stored cell already made is history, and is not judged again; the ids of the batch a row is applied in resolve
    /// too, so rows made together may cite one another.
    /// </summary>
    private static void JudgeProse(string id, IEnumerable<ProseCell> cells, IReadOnlySet<string> known, Door door, Refusals refusals, bool inBatch)
    {
        var unresolved = new List<string>();

        foreach (var cell in cells.Where(cell => cell.After is not null))
        {
            foreach (var cut in door.Cuts.Of(cell.After!, cell.Heading, cell.Before, known))
            {
                refusals.Usage(cut);
            }

            var before = Cited(door.Scanner, cell.Before ?? string.Empty, known);
            var cited = Cited(door.Scanner, cell.After!, known)
                .Where(citation => !before.Contains(citation) && !known.Contains(citation))
                .Order(StringComparer.Ordinal)
                .ToList();

            if (cited.Count > 0)
            {
                unresolved.Add($"{string.Join(", ", cited)} in its {cell.Heading}");
            }
        }

        if (unresolved.Count > 0)
        {
            refusals.Refuse(
                $"'{id}' cites {string.Join("; ", unresolved)}, which no row of either registry holds"
                + (inBatch ? ", nor any row of the batch" : string.Empty)
                + ": write the row it names first, or correct the id. An id followed by '*' or '{', or by a hyphen and one of "
                + "those, names a family of ids, and is not looked up.");
        }
    }

    /// <summary>The ids <paramref name="text"/> cites whole, as check-anchor-citations reads them: a cut one is not one.</summary>
    private static HashSet<string> Cited(AnchorIdScanner scanner, string text, IReadOnlySet<string> known)
        => scanner.Scan(string.Empty, AnchorCells.WithLineFeeds(text), known).Where(citation => !citation.Cut).Select(citation => citation.Id).ToHashSet(AnchorIdMatch.Comparer);

    /// <summary>
    /// The ids a row being written may cite: those of both registries as they stand, those of the batch it is applied in,
    /// and its own.
    /// </summary>
    private static HashSet<string> Known(AnchorRegistryDocument pending, AnchorRegistryDocument done, IEnumerable<string> batch, string id)
        => pending.Rows.Concat(done.Rows).Select(row => row.Id).Concat(batch).Append(id).ToHashSet(AnchorIdMatch.Comparer);

    /// <summary>Why a row no registry holds is refused where nobody named it new, naming the rows that begin the same way.</summary>
    private static string NotNamedNew(string id, AnchorRegistryDocument pending, AnchorRegistryDocument done, AnchorRegistries registries)
    {
        var similar = SameNamespace(id, Entries([(pending, registries.Pending), (done, registries.Done)]));

        return $"'{id}' has no row in either registry, and it was not named new: a typo in an existing row's id would make it a "
            + $"second row. If it is a new row, pass {AnchorBatchRequest.NewOption} {id}"
            + (similar.Count == 0 ? "." : $"; rows that begin the same way: {string.Join(", ", similar)}.");
    }

    /// <summary>
    /// The prose cells a write names, in the table's order: each with what the row holds now - nothing for a new row - and
    /// what it is written as, null where it is left as it is.
    /// </summary>
    private static ProseCell[] ProseCells(AnchorRow? existing, string? trigger, string? closingWork, string? crossRefs) =>
    [
        new(AnchorCellNames.Trigger, existing?.Trigger, trigger),
        new(AnchorCellNames.Closing, existing?.ClosingWork, closingWork),
        new(AnchorCellNames.CrossRefs, existing?.CrossRefs, crossRefs),
    ];

    /// <summary>
    /// Puts each registry back as <paramref name="snapshot"/> holds it, byte for byte, and says which were put back and
    /// which could not be: rows that were not all written are never left half applied without a word.
    /// </summary>
    private AnchorBatch Restored(IReadOnlyList<AnchorRowOutcome> outcomes, IReadOnlyDictionary<AnchorRegistry, byte[]> snapshot, string failure)
    {
        var restored = new List<string>();
        var failed = new List<string>();

        foreach (var (registry, bytes) in snapshot)
        {
            try
            {
                if (_fileSystem.ReadAllBytes(registry.FullPath).AsSpan().SequenceEqual(bytes))
                {
                    continue;
                }

                // The registry lock's work is synchronous and must stay on the thread that took it (IAnchorRegistryLock),
                // so the write is waited on here.
                _fileSystem.WriteAllBytesAtomicAsync(registry.FullPath, bytes).GetAwaiter().GetResult();
                (_fileSystem.ReadAllBytes(registry.FullPath).AsSpan().SequenceEqual(bytes) ? restored : failed).Add(registry.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(registry.RelativePath);
            }
        }

        return new AnchorBatch(outcomes, []) { Failure = failure, Restored = restored, RestoreFailed = failed };
    }

    private void WriteAll(IEnumerable<(AnchorRegistry Registry, string Text)> writes)
    {
        foreach (var (registry, text) in writes)
        {
            _fileSystem.WriteAllTextAtomic(registry.FullPath, text);
        }
    }

    private static AnchorWriteRequest AsWrite(AnchorRowDeclaration declared)
        => new(declared.Id, declared.Priority!, declared.Trigger) { Status = declared.Status, ClosingWork = declared.ClosingWork, CrossRefs = declared.CrossRefs };

    private static HarnessException Duplicate(string id, List<(AnchorEntry Entry, AnchorRegistryDocument Document)> matches)
        => new(
            HarnessExit.Refused,
            $"'{id}' has {matches.Count} rows ({Locations(matches)}). One id has one row, and which of "
            + "these is the real one is for a person to decide, not a tool.");

    /// <summary>What composing and judging a row takes: how the tree's ids are found, and what a cell would store broken.</summary>
    private sealed record Door(AnchorIdScanner Scanner, AnchorCellCuts Cuts);

    /// <summary>A new anchor's row, composed and checked, and the status that decides where it goes.</summary>
    private sealed record NewRow(string Id, string Row, string Status);

    /// <summary>A prose cell a write names: what it holds now, null for a new row, and what it is written as, null where it is left.</summary>
    private sealed record ProseCell(string Name, string? Before, string? After)
    {
        /// <summary>The cell, as a refusal names it.</summary>
        public string Heading => AnchorCellNames.HeadingOf(Name);
    }

    /// <summary>The one row a change is for: where it is, and its cells exactly as its line spells them.</summary>
    private sealed record Found(AnchorRow Row, AnchorRegistry Source, AnchorRegistryDocument Document, IReadOnlyList<string> Raw)
    {
        /// <summary>
        /// The row <paramref name="match"/> names: refused where it has another number of cells than a row has, so which
        /// cell is which cannot be known.
        /// </summary>
        public static Found Of((AnchorEntry Entry, AnchorRegistryDocument Document) match)
        {
            var (row, source) = (match.Entry.Row, match.Entry.Registry);
            var raw = AnchorCells.RawCells(row.RawLine);

            if (row.CellCount != AnchorRow.ExpectedCellCount || raw.Count != AnchorRow.ExpectedCellCount)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{row.Id}' at {source.RelativePath}:{row.LineNumber} has {row.CellCount} cells, not "
                    + $"{AnchorRow.ExpectedCellCount}, so which cell is which cannot be known. Repair the row by hand, "
                    + "then run set-anchor again.");
            }

            return new Found(row, source, match.Document, raw);
        }
    }

    /// <summary>The cells a change names, each composed and checked; null for a cell left as it is.</summary>
    private sealed record CellChanges(string? Priority, string? Status, string? Trigger, string? ClosingWork, string? CrossRefs);

    /// <summary>
    /// How a stored row differs from a declared one: the status and the priority it would take, null where it has them
    /// already, and whether each text cell differs.
    /// </summary>
    private sealed record RowDifferences(string? Status, string? Priority, bool Trigger, bool ClosingWork, bool CrossRefs);

    /// <summary>A change planned against both registries: what it is, and each file's text after it, in the order to write them.</summary>
    private sealed record Placement(AnchorChange Change, IReadOnlyList<(AnchorRegistry Registry, string Text)> Writes);

    /// <summary>
    /// Every refusal of one row, collected so that one run names them all: a value that is not one, and a state of the
    /// registries that refuses the row. A write of one row alone refuses them together, as a usage error where any value
    /// is not one.
    /// </summary>
    private sealed class Refusals
    {
        private readonly List<string> _messages = [];
        private bool _usage;

        /// <summary>How many there are.</summary>
        public int Count => _messages.Count;

        /// <summary>Whether there is any.</summary>
        public bool Any => _messages.Count > 0;

        /// <summary>Each, in the order found.</summary>
        public IReadOnlyList<string> Messages => _messages;

        /// <summary>A value that is not one.</summary>
        public void Usage(string message)
        {
            _messages.Add(message);
            _usage = true;
        }

        /// <summary>A state that refuses the row.</summary>
        public void Refuse(string message) => _messages.Add(message);

        /// <summary>Refuses a write of one row alone with every refusal collected, where there is any.</summary>
        public void ThrowIfAny()
        {
            if (Any)
            {
                throw new HarnessException(_usage ? HarnessExit.UsageError : HarnessExit.Refused, string.Join(" ", _messages));
            }
        }
    }

    private static string ComposeRow(params string[] cells) => "|" + string.Join('|', cells) + "|";

    /// <summary>
    /// Reads the composed row back through the same parser every command uses, so a defect in
    /// composing a row is caught here rather than by the next person to read the registry.
    /// </summary>
    private static void VerifyRow(string row, string id, AnchorIdRules rules)
    {
        var pieces = AnchorCells.Split(row);

        if (pieces.Count != AnchorRow.ExpectedCellCount + 2 || !string.Equals(rules.Identify(pieces[1]), id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The row composed for '{id}' does not read back as that anchor: {row}");
        }
    }

    /// <summary>
    /// Refuses a row whose Trigger states another verdict than its Status, where the repository holds a
    /// Trigger to its row's verdict: see <see cref="AnchorSettings.TriggerCarriesVerdict"/>.
    /// </summary>
    /// <param name="row">The row as it would be written.</param>
    /// <param name="settings">The repository's anchor settings.</param>
    /// <param name="refusals">Where the refusal is collected.</param>
    private static void OneVerdict(string row, AnchorSettings settings, Refusals refusals)
    {
        var cells = AnchorCells.Split(row);

        if (settings.TriggerCarriesVerdict && AnchorStatus.SplitVerdict(cells[3].Trim(), cells[4].Trim()) is { } split)
        {
            refusals.Usage(
                $"The row would state two verdicts: {split}. anchors.triggerCarriesVerdict holds a row's Trigger to "
                + $"the verdict its Status states: open a closed row's Trigger with {AnchorStatus.ClosedMark}, and no "
                + "other row's, or set the status the Trigger means.");
        }
    }

    private static void AddChange(List<AnchorFieldChange> fields, string field, string before, string? after)
    {
        if (after is null)
        {
            return;
        }

        var shown = AnchorCells.Flatten(after);
        if (!string.Equals(before, shown, StringComparison.Ordinal))
        {
            fields.Add(new AnchorFieldChange(field, before, shown));
        }
    }

    private static string RequirePriority(string? value)
        => AnchorPriority.TryNormalize(value, out var band) ? band : throw Usage(PriorityProblem(value));

    private static AnchorState RequireStatus(string? value)
        => AnchorStatus.TryParse(value, out var state) ? state : throw Usage(StatusProblem(value));

    /// <summary>The band <paramref name="value"/> names; null, with the refusal collected, where it names none.</summary>
    private static string? Priority(string? value, Refusals refusals)
    {
        if (AnchorPriority.TryNormalize(value, out var band))
        {
            return band;
        }

        refusals.Usage(PriorityProblem(value));
        return null;
    }

    /// <summary>The status <paramref name="value"/> names; null, with the refusal collected, where it names none.</summary>
    private static AnchorState? Status(string? value, Refusals refusals)
    {
        if (AnchorStatus.TryParse(value, out var state))
        {
            return state;
        }

        refusals.Usage(StatusProblem(value));
        return null;
    }

    private static string PriorityProblem(string? value)
        => $"'{value}' is not a priority. Use one of {string.Join(", ", AnchorPriority.Bands)}; P0 is the most urgent.";

    private static string StatusProblem(string? value)
        => $"'{value}' is not a status. Use one of {string.Join(", ", AnchorStatus.Words)}.";

    /// <summary>Refuses an empty Trigger, collecting the refusal.</summary>
    private static void Trigger(string? trigger, Refusals refusals)
    {
        // Judged as it will be written. A cell's line breaks are written as spaces, and some of them - a file,
        // group or record separator - are not whitespace to .NET: a Trigger of nothing else would pass as
        // text here, and be written as an empty cell.
        if (trigger is null || string.IsNullOrWhiteSpace(AnchorCells.Flatten(trigger)))
        {
            refusals.Usage(
                $"The {AnchorCellNames.TriggerHeading} is empty. A row says what is wrong and what would make it worth doing; a status "
                + "alone explains nothing to the next person who reads it.");
        }
    }

    /// <summary><paramref name="text"/> as a cell (<see cref="AnchorCells.Format"/>); null, with the refusal collected, where it is refused.</summary>
    private static string? Formatted(string? text, string heading, Refusals refusals)
    {
        try
        {
            return AnchorCells.Format(text, heading);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.UsageError)
        {
            refusals.Usage(ex.Message);
            return null;
        }
    }

    private static HarnessException Usage(string message) => new(HarnessExit.UsageError, message);
}
