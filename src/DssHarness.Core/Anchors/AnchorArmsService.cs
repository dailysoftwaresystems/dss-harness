using System.Text;
using System.Text.RegularExpressions;
using DssHarness.Core.Application;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Mutations;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Anchors;

/// <summary>Checks what anchor rows say about mutation arms against the mutation record.</summary>
public interface IAnchorArmsService : IUseCase
{
    /// <summary>
    /// Reads both anchor registries and the arms registry the configuration names, and checks every row's claims about
    /// mutation arms: an arm a done row names must be unmoved with a recorded passed on each leg its S row names, a pass
    /// word followed by a run id must be spelled as history, and a backticked arm id no A row holds - that one did, in
    /// git history - is reported. Reads no host, takes no lock and writes nothing, as <c>check-mutations --list</c>
    /// reads none.
    /// </summary>
    /// <param name="startDirectory">Where the command was typed.</param>
    /// <param name="legNames">What <c>--legs</c> was given, or <see langword="null"/> for every declared leg.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    Task<AnchorArmsReport> CheckAsync(string startDirectory, IReadOnlyList<string>? legNames, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAnchorArmsService"/>
/// <remarks>
/// A row names an arm by a backticked token equal to an A row's id, ignoring case as the arms registry compares ids. A
/// done row's named arms are judged exactly as <c>check-mutations --list</c> judges an arm on a leg - by the one
/// reading both share - so a close always rests on bytes that were judged, and what the check says of an arm is said as
/// the listing words it: pending, with its first reason, or unmoved here, which is never to say it holds. A pass word
/// followed by a run id is a citation of one sweep's judgement, true only of the bytes that run read; it belongs in
/// history, spelled by a form the configuration names, and is reported wherever it is not. A backticked id no A row
/// holds is recognized as an arm only where some commit's registry declared it, so an id that never was one is left
/// alone.
/// </remarks>
public sealed partial class AnchorArmsService(
    IHarnessContextLoader contextLoader,
    IAnchorRegistryLocator locator,
    IAnchorRegistryLock registryLock,
    ArmListingReader listings,
    IGitClient gitClient,
    IFileSystem fileSystem) : IAnchorArmsService
{
    /// <summary>How many commits of the arms registry's history are read for the ids an A row once declared.</summary>
    public const int HistoryCommits = 512;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IAnchorRegistryLocator _locator = locator;
    private readonly IAnchorRegistryLock _registryLock = registryLock;
    private readonly ArmListingReader _listings = listings;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>The arm ids a row may name in a cell: a backticked token of the characters an arm's id takes.</summary>
    [GeneratedRegex(@"`([A-Za-z0-9_-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex NamedIds();

    /// <summary>A run id as a citation's tail could spell it, wider than a run id, for <see cref="RunId.TryParse"/> to judge.</summary>
    [GeneratedRegex(@"[0-9]{8}-[0-9]{6}-[0-9a-fA-F]{8}", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdText();

    public async Task<AnchorArmsReport> CheckAsync(string startDirectory, IReadOnlyList<string>? legNames, CancellationToken cancellationToken = default)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var settings = context.Config.Anchors;
        var rules = AnchorIdRules.From(settings);
        var registries = await _locator.LocateAsync(context, cancellationToken).ConfigureAwait(false);

        // The arms registry read and verified as a listing reads it, with every leg's listing and each arm's scopes:
        // the one reading, so what this check says of an arm on a leg is what --list says of it.
        var read = await _listings.ReadListingAsync(context, armNames: null, legNames, cancellationToken).ConfigureAwait(false);
        var listing = read.Listing;

        var findings = new List<AnchorFinding>();
        var documents = new List<(AnchorRegistryDocument Document, AnchorRegistry Registry)>();
        var rows = 0;

        // Both anchor registries read as one moment, as every reader of both reads them.
        foreach (var (registry, text) in _registryLock.ReadTogether(registries, _fileSystem))
        {
            if (text is null)
            {
                findings.Add(registry.Fatal(
                    registry.Kind == AnchorRegistryKind.Done
                        ? "there is no done registry, so the arms its rows would name cannot be checked"
                        : "there is no pending registry, so the arms its rows would name cannot be checked"));
                continue;
            }

            var document = AnchorRegistryDocument.Parse(text, rules);

            documents.Add((document, registry));
            rows += document.Rows.Count;

            // A structural problem is this check's finding too, as --lint's and the balance's: a row it hides cannot
            // be judged, and answering from rows a registry cannot trust is what every other anchor reader refuses.
            findings.AddRange(document.Findings.Select(registry.Finding));
        }

        var done = documents.SingleOrDefault(pair => pair.Registry.Kind == AnchorRegistryKind.Done);
        var current = read.Arms.Registry;
        var selected = new List<string>(listing.Legs.Select(leg => leg.Leg));

        // The arms a done row names must be unmoved with a recorded passed on each leg its S row names, every selected
        // leg where it names none - judged nowhere else, as a listing judges no leg it did not select.
        var named = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var row in done.Document?.Rows ?? [])
        {
            foreach (var arm in ArmsNamedBy(row, current))
            {
                named.Add(arm.Id);

                var judged = read.Arms.Scopes.TryGetValue(arm.Id, out var scoped) ? [.. scoped] : selected;

                foreach (var leg in judged.Where(leg => listing.Legs.Any(listed => string.Equals(listed.Leg, leg, StringComparison.OrdinalIgnoreCase))))
                {
                    var onLeg = listing.Legs
                        .Single(listed => string.Equals(listed.Leg, leg, StringComparison.OrdinalIgnoreCase))
                        .Arms.SingleOrDefault(onLeg => string.Equals(onLeg.Arm, arm.Id, StringComparison.OrdinalIgnoreCase));

                    if (onLeg is { Pending: true })
                    {
                        findings.Add(done.Registry.Fatal(
                            row,
                            $"anchor '{row.Id}' names arm '{arm.Id}', and on leg '{leg}' it is pending: {onLeg.Why[0].Said}"));
                    }
                }
            }
        }

        // A pass word followed by a run id is a citation of one sweep's judgement; spelled as history it is the row's
        // own record of when, and anywhere else it goes stale the first time what it judged moves.
        var citations = settings.PassWords
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .Select(word => CitationOf(Regex.Escape(word.Trim())))
            .ToList();
        var history = settings.HistoryForms
            .Where(form => !string.IsNullOrWhiteSpace(form))
            .Select(HistorySpansOf)
            .ToList();

        foreach (var (document, registry) in documents)
        {
            foreach (var row in document.Rows)
            {
                foreach (var citation in CitationsOf(row, citations, history))
                {
                    findings.Add(registry.Fatal(
                        row,
                        $"anchor '{row.Id}' states a pass of run '{citation.Run}' ({citation.Shown}): a pass holds only of "
                            + "the bytes that run read, and goes stale the first time what it judged moves. State it as history - "
                            + "a span matching a form of anchors.historyForms - or drop it"));
                }
            }
        }

        // A backticked id no A row holds is reported only where some commit's registry declared it: one that never was
        // an arm is any other backticked thing a row may cite.
        var retired = await RetiredIdsAsync(context, current, cancellationToken).ConfigureAwait(false);

        if (retired.Note is { } note)
        {
            findings.Add(note);
        }

        foreach (var (document, registry) in documents)
        {
            foreach (var row in document.Rows)
            {
                foreach (var token in IdsNamedBy(row).Where(token => current.Find(token) is null && retired.Ids.Contains(token)))
                {
                    findings.Add(registry.Fatal(
                        row,
                        $"anchor '{row.Id}' names arm '{token}', which no A row of '{listing.Registry}' holds: an A row "
                            + "declared it once, so it was renamed or retired, and the row cites what the registry no longer declares"));
                }
            }
        }

        return new AnchorArmsReport(
            listing.Registry,
            listing.RecordDirectory,
            selected,
            rows,
            done.Document?.Rows.Count ?? 0,
            [.. named],
            [.. findings.OrderBy(finding => finding.File, StringComparer.Ordinal).ThenBy(finding => finding.LineNumber)]);
    }

    /// <summary>The arms <paramref name="row"/> names: each backticked token equal to an A row's id, ignoring case, in the order the row names them.</summary>
    private static IEnumerable<MutationArm> ArmsNamedBy(AnchorRow row, MutationRegistry registry)
        => IdsNamedBy(row)
            .Select(token => registry.Find(token))
            .OfType<MutationArm>()
            .DistinctBy(arm => arm.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every backticked token of the shape an arm's id takes, in every cell the row holds, in the order it names them.</summary>
    private static IEnumerable<string> IdsNamedBy(AnchorRow row)
        => CellsOf(row).SelectMany(cell => NamedIds().Matches(cell).Select(match => match.Groups[1].Value));

    /// <summary>Every cell the row holds, named and past-the-end alike: a malformed row still says what it says.</summary>
    private static IEnumerable<string> CellsOf(AnchorRow row)
        => Enumerable.Range(1, Math.Max(AnchorRow.ExpectedCellCount, row.CellCount)).Select(row.Cell);

    /// <summary>
    /// Every citation one row holds that no form of history spells: a pass word followed by only whitespace and
    /// brackets or punctuation and then a run id <see cref="RunId.TryParse"/> accepts, its whole span inside no span a
    /// configured form matches.
    /// </summary>
    private static IEnumerable<(string Run, string Shown)> CitationsOf(
        AnchorRow row,
        IReadOnlyList<Regex> citations,
        IReadOnlyList<Func<string, IReadOnlyList<(int From, int To)>>> history)
    {
        foreach (var cell in CellsOf(row))
        {
            IReadOnlyList<(int From, int To)> spans = [.. history.SelectMany(form => form(cell))];

            foreach (var match in citations.SelectMany(pattern => pattern.Matches(cell)))
            {
                var run = match.Groups["run"].Value;

                if (RunId.TryParse(run, out _)
                    && !spans.Any(span => span.From <= match.Index && match.Index + match.Length <= span.To))
                {
                    yield return (run, match.Value);
                }
            }
        }
    }

    /// <summary>The citation of a pass word: the word - matched ignoring case - then only whitespace and brackets or punctuation, then a run id.</summary>
    private static Regex CitationOf(string word)
        => new($"(?<!\\w)(?i:{word})[\\s\\p{{P}}\\p{{S}}]{{0,16}}(?<run>{RunIdText()})", RegexOptions.CultureInvariant);

    /// <summary>
    /// The spans of history a cell holds for a form: the form with its <c>{commit}</c> and <c>{run}</c> placeholders
    /// standing for a commit's hex and a run id.
    /// </summary>
    private static Func<string, IReadOnlyList<(int From, int To)>> HistorySpansOf(string form)
    {
        var pattern = new StringBuilder();
        var at = 0;

        while (at < form.Length)
        {
            var commit = form.IndexOf("{commit}", at, StringComparison.Ordinal);
            var run = form.IndexOf("{run}", at, StringComparison.Ordinal);
            var next = new[] { commit, run }.Where(index => index >= 0).DefaultIfEmpty(-1).Min();

            if (next < 0)
            {
                pattern.Append(Regex.Escape(form[at..]));
                break;
            }

            pattern.Append(Regex.Escape(form[at..next]));
            pattern.Append(next == commit ? "[0-9a-fA-F]{7,40}" : RunIdText().ToString());
            at = next + (next == commit ? "{commit}".Length : "{run}".Length);
        }

        var history = new Regex(pattern.ToString(), RegexOptions.CultureInvariant);

        return cell => [.. history.Matches(cell).Select(match => (match.Index, match.Index + match.Length))];
    }

    /// <summary>
    /// Every id an A row of the arms registry ever declared in git history and no A row holds now, with the note a cut
    /// history deserves: it can only say what it read.
    /// </summary>
    private async Task<(HashSet<string> Ids, AnchorFinding? Note)> RetiredIdsAsync(HarnessContext context, MutationRegistry current, CancellationToken cancellationToken)
    {
        var root = context.Layout.RepositoryRoot;
        var named = context.Config.Mutations.Registry!;
        var registry = AnchorRegistryLocator.Normalize(named);
        var history = await _gitClient.ListHeldInHistoryAsync(root, "HEAD", [registry], HistoryCommits, cancellationToken).ConfigureAwait(false);
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in history.Held.GetValueOrDefault(registry) ?? [])
        {
            var text = await _gitClient.ReadFileAtCommitAsync(root, version.Commit, registry, cancellationToken).ConfigureAwait(false);

            foreach (var id in MutationRegistryParser.ArmIdsOf(text ?? string.Empty))
            {
                held.Add(id);
            }
        }

        var retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in held.Where(id => current.Find(id) is null))
        {
            retired.Add(id);
        }

        var shallow = await _gitClient.IsShallowAsync(root, cancellationToken).ConfigureAwait(false) == true;

        return (retired, history.Cut || shallow
            ? new AnchorFinding(
                named,
                0,
                AnchorFindingSeverity.Note,
                (history.Cut, shallow) switch
                {
                    (true, true) => $"more commits changed '{named}' than were read, and this clone's history is shallow: an id no A row holds may have been declared before either cut",
                    (true, false) => $"more commits changed '{named}' than were read: an id no A row holds may have been declared before that cut",
                    _ => $"this clone's history is shallow, so commits before where it was cut name no version of '{named}': an id no A row holds may have been declared before the cut",
                })
            : null);
    }
}

/// <summary>What check-anchor-arms found: what it read, and every finding.</summary>
/// <param name="ArmsRegistry">The arms registry, as the configuration names it.</param>
/// <param name="MutationRecord">The record of verdicts the judgement read, absolute.</param>
/// <param name="Legs">The legs whose listings judged the arms, in order.</param>
/// <param name="Rows">The rows read across both registries.</param>
/// <param name="DoneRows">The rows read of the done registry.</param>
/// <param name="NamedArms">The distinct arms the done rows name, in id order.</param>
/// <param name="Findings">Every finding, fatal where a row's claim about an arm does not hold; notes fail nothing.</param>
public sealed record AnchorArmsReport(
    string ArmsRegistry,
    string MutationRecord,
    IReadOnlyList<string> Legs,
    int Rows,
    int DoneRows,
    IReadOnlyList<string> NamedArms,
    IReadOnlyList<AnchorFinding> Findings)
{
    /// <summary>Whether every row's claims about mutation arms held: no finding is fatal.</summary>
    public bool Passed => Findings.All(finding => finding.Severity != AnchorFindingSeverity.Fatal);
}
