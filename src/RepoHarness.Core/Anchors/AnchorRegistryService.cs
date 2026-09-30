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

        // Every value is checked before the registries are opened, so a mistake in the arguments is
        // reported as that and never costs anyone the lock.
        var row = ComposeNew(request, rules, context.Config.Anchors, Cuts(context, rules));
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        return _registryLock.RunExclusive(registries, () =>
        {
            var placement = PlaceNew(row, registries, Load(registries.Pending, rules), Load(registries.Done, rules), rules, batch: null, written: !dryRun);

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
        var cells = ComposeChanges(request, rules, Cuts(context, rules));
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        return _registryLock.RunExclusive(registries, () =>
        {
            var placement = PlaceChange(
                request,
                cells,
                registries,
                Load(registries.Pending, rules),
                Load(registries.Done, rules),
                rules,
                context.Config.Anchors,
                batch: null,
                written: !dryRun);

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
        var cuts = Cuts(context, rules);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);
        var batch = rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var named = request.New.ToHashSet(StringComparer.Ordinal);
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
            var losing = new HashSet<AnchorRowCell>();

            foreach (var declared in rows)
            {
                try
                {
                    var pending = Parse(registries.Pending, texts[AnchorRegistryKind.Pending], rules);
                    var done = Parse(registries.Done, texts[AnchorRegistryKind.Done], rules);
                    var matches = Matches(declared.Id, (pending, registries.Pending), (done, registries.Done));
                    Placement placement;

                    if (matches.Count == 0)
                    {
                        // A row no registry holds is one the batch makes, and a typo in an existing row's id is one too: it is
                        // made only where the person applying the batch said it is new, as write-anchor is only ever asked to make
                        // one. Every other refusal of it is named beside that one, so one run shows all there is to correct.
                        var refusals = new List<string>();

                        if (!named.Contains(declared.Id))
                        {
                            var similar = SameNamespace(declared.Id, Entries([(pending, registries.Pending), (done, registries.Done)]));

                            refusals.Add(
                                $"'{declared.Id}' has no row in either registry, and it was not named new: a typo in an existing row's id "
                                + $"would make it a second row. If it is a new row, pass --new {declared.Id}"
                                + (similar.Count == 0 ? "." : $"; rows that begin the same way: {string.Join(", ", similar)}."));
                        }

                        if (declared.Priority is null)
                        {
                            refusals.Add($"'{declared.Id}' has no row yet, and a new anchor needs a priority: declare one of {string.Join(", ", AnchorPriority.Bands)}.");
                        }

                        Placement? made = null;

                        try
                        {
                            // Composed with a stand-in priority where it declares none, only so the rest of it is judged too:
                            // refused for that, it is never written.
                            var write = AsWrite(declared) with { Priority = declared.Priority ?? AnchorPriority.Bands[0] };
                            made = PlaceNew(ComposeNew(write, rules, settings, cuts), registries, pending, done, rules, batch, written);
                        }
                        catch (HarnessException ex) when (ex.ExitCode is HarnessExit.UsageError or HarnessExit.Refused)
                        {
                            refusals.Add(ex.Message);
                        }

                        if (refusals.Count > 0)
                        {
                            problems.AddRange(refusals.Select(refusal => $"{declared.Id}: {refusal}"));
                            continue;
                        }

                        placement = made!;
                        outcomes.Add(new AnchorRowOutcome(declared.Id, AnchorRowAction.New, placement.Change));
                    }
                    else
                    {
                        if (matches.Count > 1)
                        {
                            throw Duplicate(declared.Id, matches);
                        }

                        var (existing, registry) = (matches[0].Entry.Row, matches[0].Entry.Registry);
                        var change = ChangesTo(declared, existing);

                        // A row named new that stands exactly as declared is the batch's own, made by an earlier application.
                        if (!change.HasChanges)
                        {
                            outcomes.Add(new AnchorRowOutcome(
                                declared.Id,
                                AnchorRowAction.AlreadyIn,
                                new AnchorChange(declared.Id, registry, registry, existing.Status, existing.Status, [], Written: false)));
                            continue;
                        }

                        if (named.Contains(declared.Id))
                        {
                            throw new HarnessException(
                                HarnessExit.Refused,
                                $"'{declared.Id}' was named new, but {Locations(matches)} holds it, other than as declared: drop --new "
                                + $"{declared.Id} to change that row, or give the new row an id of its own.");
                        }

                        var lost = Lost(declared.Id, change, existing, accepted);
                        losing.UnionWith(lost.Select(cell => new AnchorRowCell(declared.Id, cell.Cell)));

                        placement = PlaceChange(change, ComposeChanges(change, rules, cuts), registries, pending, done, rules, settings, batch, written);
                        outcomes.Add(new AnchorRowOutcome(declared.Id, AnchorRowAction.Changed, placement.Change) { Lost = lost });

                        // Planned, a loss is shown for a person to read; checked or written, it is refused until they accept it.
                        if (mode != AnchorBatchMode.Plan)
                        {
                            problems.AddRange(lost.Where(cell => !cell.Accepted).Select(cell =>
                                $"{declared.Id}: its {cell.Cell} would lose stored text that the declared text does not keep, as its "
                                + $"word diff shows, so it is written only with --accept-lost {declared.Id}:{cell.Cell}."));
                        }
                    }

                    foreach (var write in placement.Writes)
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
                .Select(id => $"--new {id} names no row of the batch: drop it, or correct the id."));
            problems.AddRange(accepted.Where(cell => !losing.Contains(cell)).Select(cell => cell.ToString()).Order(StringComparer.Ordinal)
                .Select(cell => $"--accept-lost {cell} names a cell that loses no stored text here: drop it, or correct it."));

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

        // Reading takes no lock: every write replaces a whole file in one rename.
        return Differing(rows, registries, Load(registries.Pending, rules), Load(registries.Done, rules));
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
    /// Composes and checks a new anchor's row: everything write-anchor checks before the registries are opened, and every
    /// row a batch adds is held to.
    /// </summary>
    private static NewRow ComposeNew(AnchorWriteRequest request, AnchorIdRules rules, AnchorSettings settings, AnchorCellCuts cuts)
    {
        if (!rules.IsMintable(request.Id))
        {
            throw Usage(
                $"'{request.Id}' cannot name a new anchor. A new id is '{rules.Prefix}-' followed by at least "
                + $"{rules.MinimumSegments} hyphen-separated segments of letters, digits or underscores, the "
                + $"first in capitals, for example {rules.Example()}. Spell a compound word as one segment "
                + "(ALWAYSINLINE, not ALWAYS-INLINE).");
        }

        var priority = RequirePriority(request.Priority);
        var status = AnchorStatus.Render(RequireStatus(request.Status));
        RequireTrigger(request.Trigger);

        ProseCell[] cells =
        [
            new(TriggerField, null, request.Trigger),
            new(ClosingField, null, request.ClosingWork),
            new(CrossRefsField, null, request.CrossRefs),
        ];

        RequireWhole(cells, cuts);

        var row = ComposeRow(
            $" `{request.Id}` ",
            $" {priority} ",
            $" {status} ",
            AnchorCells.Format(request.Trigger, TriggerField),
            AnchorCells.Format(request.ClosingWork, ClosingField),
            AnchorCells.Format(request.CrossRefs, CrossRefsField));

        VerifyRow(row, request.Id, rules);
        RequireOneVerdict(row, settings);

        return new NewRow(request.Id, row, status, cells);
    }

    /// <summary>
    /// Composes and checks the cells a change names: everything set-anchor checks before the registries are opened. Only
    /// the cells named are rebuilt; every other cell is written back exactly as it was read.
    /// </summary>
    private static CellChanges ComposeChanges(AnchorSetRequest request, AnchorIdRules rules, AnchorCellCuts cuts)
    {
        if (!rules.IsWellFormed(request.Id))
        {
            throw Usage(
                $"'{request.Id}' is not an anchor id: an id is '{rules.Prefix}-' followed by hyphen-separated "
                + "letters, digits or underscores.");
        }

        if (!request.HasChanges)
        {
            throw Usage("Nothing to change. Give at least one of --priority, --status, --trigger, --closing or --cross-refs.");
        }

        if (request.Trigger is not null)
        {
            RequireTrigger(request.Trigger);
        }

        RequireWhole([new(TriggerField, null, request.Trigger), new(ClosingField, null, request.ClosingWork), new(CrossRefsField, null, request.CrossRefs)], cuts);

        return new CellChanges(
            request.Priority is null ? null : RequirePriority(request.Priority),
            request.Status is null ? null : AnchorStatus.Render(RequireStatus(request.Status)),
            request.Trigger is null ? null : AnchorCells.Format(request.Trigger, TriggerField),
            request.ClosingWork is null ? null : AnchorCells.Format(request.ClosingWork, ClosingField),
            request.CrossRefs is null ? null : AnchorCells.Format(request.CrossRefs, CrossRefsField));
    }

    /// <summary>
    /// Places a new row in the registry its status belongs in: refused where its id already has a row, or where it cites
    /// an id no row holds - of either registry, or of <paramref name="batch"/>, the ids of the batch it is applied in, null
    /// for a row written alone.
    /// </summary>
    private static Placement PlaceNew(
        NewRow row,
        AnchorRegistries registries,
        AnchorRegistryDocument pending,
        AnchorRegistryDocument done,
        AnchorIdRules rules,
        IReadOnlySet<string>? batch,
        bool written)
    {
        var existing = Matches(row.Id, (pending, registries.Pending), (done, registries.Done));

        if (existing.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{row.Id}' already has a row in {Locations(existing)}. write-anchor adds a new anchor; "
                + "change an existing one with set-anchor.");
        }

        RequireCitedRows(row.Id, row.Cells, pending, done, rules, batch);

        var destination = registries.HomeOf(row.Status);
        var document = destination.Kind == AnchorRegistryKind.Pending ? pending : done;

        document.AppendRow(row.Row);

        return new Placement(new AnchorChange(row.Id, null, destination, null, row.Status, [], written), [(destination, document.ToText())]);
    }

    /// <summary>
    /// Places a change to an existing row, moving it where its new status belongs: refused where it has no row in scope,
    /// or more than one anywhere, or where a cell it changes newly cites an id no row holds - of either registry, or of
    /// <paramref name="batch"/>, the ids of the batch it is applied in, null for a change made alone.
    /// </summary>
    private static Placement PlaceChange(
        AnchorSetRequest request,
        CellChanges cells,
        AnchorRegistries registries,
        AnchorRegistryDocument pending,
        AnchorRegistryDocument done,
        AnchorIdRules rules,
        AnchorSettings settings,
        IReadOnlySet<string>? batch,
        bool written)
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

        var (existing, source, sourceDocument) = (inScope[0].Entry.Row, inScope[0].Entry.Registry, inScope[0].Document);
        var raw = AnchorCells.RawCells(existing.RawLine);

        if (existing.CellCount != AnchorRow.ExpectedCellCount || raw.Count != AnchorRow.ExpectedCellCount)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{request.Id}' at {source.RelativePath}:{existing.LineNumber} has {existing.CellCount} cells, not "
                + $"{AnchorRow.ExpectedCellCount}, so which cell is which cannot be known. Repair the row by hand, "
                + "then run set-anchor again.");
        }

        // A citation the stored cell already made is history, and is not judged again: a row naming one since retired stays
        // as it was written.
        RequireCitedRows(
            existing.Id,
            [
                new(TriggerField, existing.Trigger, request.Trigger),
                new(ClosingField, existing.ClosingWork, request.ClosingWork),
                new(CrossRefsField, existing.CrossRefs, request.CrossRefs),
            ],
            pending,
            done,
            rules,
            batch);

        var row = ComposeRow(
            raw[0],
            cells.Priority is null ? raw[1] : $" {cells.Priority} ",
            cells.Status is null ? raw[2] : $" {cells.Status} ",
            cells.Trigger ?? raw[3],
            cells.ClosingWork ?? raw[4],
            cells.CrossRefs ?? raw[5]);

        VerifyRow(row, existing.Id, rules);
        RequireOneVerdict(row, settings);

        var fields = new List<AnchorFieldChange>();
        AddChange(fields, AnchorCellNames.Priority, existing.Priority, cells.Priority);
        AddChange(fields, AnchorCellNames.Status, existing.Status, cells.Status);
        AddChange(fields, AnchorCellNames.Trigger, existing.Trigger, request.Trigger);
        AddChange(fields, AnchorCellNames.Closing, existing.ClosingWork, request.ClosingWork);
        AddChange(fields, AnchorCellNames.CrossRefs, existing.CrossRefs, request.CrossRefs);

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
    /// registry its status belongs in, with every declared cell.
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

            var (row, registry) = (matches[0].Entry.Row, matches[0].Entry.Registry);
            var status = AnchorStatus.Render(RequireStatus(declared.Status));
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

            if (registry.Kind != AnchorRegistry.KindFor(status))
            {
                differs.Add($"it is in the {registry.Name} registry");
            }

            if (cells.Trigger)
            {
                differs.Add("its Trigger differs");
            }

            if (cells.ClosingWork)
            {
                differs.Add("its Closing work differs");
            }

            if (cells.CrossRefs)
            {
                differs.Add("its Cross-refs differ");
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
    private static IReadOnlyList<AnchorLostCell> Lost(string id, AnchorSetRequest change, AnchorRow existing, IReadOnlySet<AnchorRowCell> accepted)
    {
        (string Cell, string Stored, string? Written)[] cells =
        [
            (AnchorCellNames.Trigger, existing.Trigger, change.Trigger),
            (AnchorCellNames.Closing, existing.ClosingWork, change.ClosingWork),
            (AnchorCellNames.CrossRefs, existing.CrossRefs, change.CrossRefs),
        ];

        return [.. cells
            .Where(cell => cell.Written is not null && AnchorCellComparison.Fate(cell.Stored, AnchorCells.Flatten(cell.Written)) == AnchorCellFate.Lost)
            .Select(cell => new AnchorLostCell(
                cell.Cell,
                AnchorCellComparison.WordDiff(cell.Stored, AnchorCells.Flatten(cell.Written!)),
                accepted.Contains(new AnchorRowCell(id, cell.Cell))))];
    }

    /// <summary>
    /// What refuses a cell the door would store broken, for the tree <paramref name="context"/> is in: a path a cell cites
    /// starts at one of its top directories, which are read now, and one that cannot be read refuses the write - an
    /// answer of none would pass unseen every path that check exists for.
    /// </summary>
    private AnchorCellCuts Cuts(HarnessContext context, AnchorIdRules rules)
    {
        var root = context.Layout.RepositoryRoot;

        try
        {
            return new AnchorCellCuts(
                rules,
                [.. _fileSystem.EnumerateDirectories(root)
                    .Select(directory => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)))
                    .Where(name => !string.Equals(name, ".git", StringComparison.Ordinal))]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The directories at the top of '{root}' cannot be listed, so whether a cell holds a path cut in two cannot be told: "
                + $"{ex.Message.TrimEnd('.')}. Nothing was written.");
        }
    }

    /// <summary>Refuses every prose cell the door would store broken, naming each problem of each.</summary>
    private static void RequireWhole(IEnumerable<ProseCell> cells, AnchorCellCuts cuts)
    {
        var problems = cells.Where(cell => cell.After is not null).SelectMany(cell => cuts.Of(cell.After!, cell.Field)).ToList();

        if (problems.Count > 0)
        {
            throw Usage(string.Join(" ", problems));
        }
    }

    /// <summary>
    /// Refuses a row whose cells newly cite an id no row holds: every id a new row's cell cites, and every id a changed
    /// cell cites that its stored text did not. The ids of the batch it is applied in resolve too, so rows made together
    /// may cite one another.
    /// </summary>
    private static void RequireCitedRows(
        string id,
        IEnumerable<ProseCell> cells,
        AnchorRegistryDocument pending,
        AnchorRegistryDocument done,
        AnchorIdRules rules,
        IReadOnlySet<string>? batch)
    {
        var known = pending.Rows.Concat(done.Rows).Select(row => row.Id).Concat(batch ?? Enumerable.Empty<string>()).Append(id).ToHashSet(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var cell in cells.Where(cell => cell.After is not null))
        {
            var before = rules.CitedIds(cell.Before ?? string.Empty);
            var cited = rules.CitedIds(AnchorCells.Flatten(cell.After!))
                .Where(cited => !before.Contains(cited) && !known.Contains(cited))
                .Order(StringComparer.Ordinal)
                .ToList();

            if (cited.Count > 0)
            {
                unresolved.Add($"{string.Join(", ", cited)} in its {cell.Field}");
            }
        }

        if (unresolved.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{id}' cites {string.Join("; ", unresolved)}, which no row of either registry holds"
                + (batch is null ? string.Empty : ", nor any row of the batch")
                + ": write the row it names first, or correct the id. An id followed by '-', '*' or '{' names a family of ids, and is not looked up.");
        }
    }

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

    /// <summary>The Trigger cell, as a refusal names it.</summary>
    private const string TriggerField = "Trigger";

    /// <summary>The Closing work cell, as a refusal names it.</summary>
    private const string ClosingField = "Closing work";

    /// <summary>The Cross-refs cell, as a refusal names it.</summary>
    private const string CrossRefsField = "Cross-refs";

    /// <summary>A new anchor's row, composed and checked, the status that decides where it goes, and its prose cells as written.</summary>
    private sealed record NewRow(string Id, string Row, string Status, IReadOnlyList<ProseCell> Cells);

    /// <summary>A prose cell a write names: what it holds now, null for a new row, and what it is written as, null where it is left.</summary>
    private sealed record ProseCell(string Field, string? Before, string? After);

    /// <summary>The cells a change names, each composed and checked; null for a cell left as it is.</summary>
    private sealed record CellChanges(string? Priority, string? Status, string? Trigger, string? ClosingWork, string? CrossRefs);

    /// <summary>
    /// How a stored row differs from a declared one: the status and the priority it would take, null where it has them
    /// already, and whether each text cell differs.
    /// </summary>
    private sealed record RowDifferences(string? Status, string? Priority, bool Trigger, bool ClosingWork, bool CrossRefs);

    /// <summary>A change planned against both registries: what it is, and each file's text after it, in the order to write them.</summary>
    private sealed record Placement(AnchorChange Change, IReadOnlyList<(AnchorRegistry Registry, string Text)> Writes);

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
    private static void RequireOneVerdict(string row, AnchorSettings settings)
    {
        var cells = AnchorCells.Split(row);

        if (settings.TriggerCarriesVerdict && AnchorStatus.SplitVerdict(cells[3].Trim(), cells[4].Trim()) is { } split)
        {
            throw Usage(
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
        => AnchorPriority.TryNormalize(value, out var band)
            ? band
            : throw Usage($"'{value}' is not a priority. Use one of {string.Join(", ", AnchorPriority.Bands)}; P0 is the most urgent.");

    private static AnchorState RequireStatus(string? value)
        => AnchorStatus.TryParse(value, out var state)
            ? state
            : throw Usage($"'{value}' is not a status. Use one of {string.Join(", ", AnchorStatus.Words)}.");

    private static void RequireTrigger(string? trigger)
    {
        // Judged as it will be written. A cell's line breaks are written as spaces, and some of them - a file,
        // group or record separator - are not whitespace to .NET: a Trigger of nothing else would pass as
        // text here, and be written as an empty cell.
        if (trigger is null || string.IsNullOrWhiteSpace(AnchorCells.Flatten(trigger)))
        {
            throw Usage(
                "The Trigger is empty. A row says what is wrong and what would make it worth doing; a status "
                + "alone explains nothing to the next person who reads it.");
        }
    }

    private static HarnessException Usage(string message) => new(HarnessExit.UsageError, message);
}
