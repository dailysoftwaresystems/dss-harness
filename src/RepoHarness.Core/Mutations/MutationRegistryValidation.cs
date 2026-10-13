using RepoHarness.Core.Configuration;
using RepoHarness.Core.Output;

namespace RepoHarness.Core.Mutations;

/// <summary>A registry's text as validated: what it declares, and everything wrong with it.</summary>
/// <param name="Registry">The arms that could be read.</param>
/// <param name="Scopes">The legs each arm with an S row runs on, keyed by the arm's id (<see cref="ArmSelection.Scopes"/>).</param>
/// <param name="Problems">
/// Every problem, in the order a reader meets them: the rows', each with its line; then, of a registry whose rows read,
/// the texts they cite; then each scope naming neither a leg nor a leg set.
/// </param>
public sealed record RegistryValidation(MutationRegistry Registry, IReadOnlyDictionary<string, IReadOnlySet<string>> Scopes, IReadOnlyList<string> Problems)
{
    /// <summary>Whether nothing was found wrong, so the registry can be swept.</summary>
    public bool Valid => Problems.Count == 0;

    /// <summary>
    /// The refusal of a registry that cannot be swept, a line each: what cannot be swept and how many problems it has,
    /// then every problem, each to fix. Nothing where it has none.
    /// </summary>
    /// <param name="registry">The registry's path, as the configuration names it.</param>
    /// <param name="subject">
    /// Whose text was validated, in the caller's words, where it is not the registry the tree holds:
    /// <c>as the fold would write it</c>. <see langword="null"/> for the tree's own.
    /// </param>
    public IReadOnlyList<string> Refusal(string registry, string? subject = null)
        => Valid
            ? []
            :
            [
                $"The arms registry '{registry}'{(subject is null ? string.Empty : $", {subject},")} cannot be swept: {ReportText.Counted(Problems.Count, "problem")}, each to fix:",
                .. Problems.Select(problem => "  - " + problem),
            ];
}

/// <summary>
/// Validates a registry's text against the files of a tree: the one owner of what makes a registry one a sweep can
/// read, for the registry a tree holds and for a text a command is about to write into one.
/// </summary>
/// <remarks>
/// It reads and writes nothing but through the view it is given, so validating a text nobody has written yet shows
/// exactly what writing it would leave.
/// </remarks>
public static class MutationRegistryValidation
{
    /// <summary>What keeps a sync from carrying a file, as a refusal names them: git's rules, or the configuration's.</summary>
    internal const string Withholders = "git ignores it, or sync.neverTransfer, sync.exclude or worktrees.root covers it";

    /// <summary>
    /// Reads <paramref name="text"/> as a registry of the tree <paramref name="files"/> shows: every row and every
    /// arm's shape, that every file in the text directory is cited, each cited text where the rows read, and each
    /// scope's legs.
    /// </summary>
    /// <param name="text">The registry's text.</param>
    /// <param name="files">The tree the registry stands in, or will.</param>
    /// <param name="settings">Where the registry's texts are kept.</param>
    /// <param name="config">The configuration declaring the legs and leg sets a scope names.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public static async Task<RegistryValidation> ValidateAsync(
        string text,
        IRegistryFiles files,
        MutationSettings settings,
        HarnessConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(config);

        var reading = MutationRegistryParser.Parse(MutationRegistryParser.Lines(text), files.List(settings.TextDirectory), files.Names);
        var problems = new List<string>(reading.Problems);

        if (reading.Valid)
        {
            // A text so kept would be missing to every arm citing it, each read violated for a text nobody carried.
            problems.AddRange(await TextProblemsAsync(reading.Registry, files, cancellationToken).ConfigureAwait(false));
        }

        // Every problem of the registry in one list, whatever else is wrong with it: a scope naming no leg is the
        // registry's own, and is said beside a row it does not read rather than by the run after that row is fixed.
        var scopes = ArmSelection.Scopes(config, reading.Registry, problems);

        return new RegistryValidation(reading.Registry, scopes, problems);
    }

    /// <summary>
    /// What is wrong with the texts <paramref name="registry"/>'s rows cite, read as the tree holds them: each that is not a
    /// file; each before-text, control before-text or diagnostic holding nothing - which occurs everywhere, and which
    /// every run would say; and each text of an arm that shares a byte of its file with another of that arm's, as the
    /// tree holds the file, said at the later row's line.
    /// </summary>
    private static async Task<List<string>> TextProblemsAsync(MutationRegistry registry, IRegistryFiles files, CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        async Task<bool> SoundAsync(string cited, int line, bool empty)
        {
            if (await TextProblemAsync(files, cited, line, empty, cancellationToken).ConfigureAwait(false) is not { } problem)
            {
                return true;
            }

            problems.Add(problem);

            return false;
        }

        foreach (var arm in registry.Arms)
        {
            foreach (var site in arm.Sites)
            {
                var whole = true;

                foreach (var text in site.Texts)
                {
                    whole &= await SoundAsync(text.Before, text.Line, empty: true).ConfigureAwait(false);
                    whole &= await SoundAsync(text.After, text.Line, empty: false).ConfigureAwait(false);
                }

                if (whole)
                {
                    problems.AddRange(await OverlapsAsync(files, site, cancellationToken).ConfigureAwait(false));
                }
            }

            if (arm.Control is { } control)
            {
                await SoundAsync(control.Before, control.Line, empty: true).ConfigureAwait(false);
                await SoundAsync(control.After, control.Line, empty: false).ConfigureAwait(false);
            }

            if (arm.Kind == RedKind.TestRed)
            {
                await SoundAsync(arm.Diagnostic, arm.Line, empty: true).ConfigureAwait(false);
            }
        }

        return problems;
    }

    /// <summary>
    /// Each two texts of <paramref name="site"/> that overlap in the file as the tree holds it, the later row's problem:
    /// the texts of one file are replaced together, each as the tree holds it, so two that share a byte of it say two
    /// things of that byte. Nothing of a site with one text; of a file the tree does not hold, which its arm's pre-flight
    /// says of the worker's copy; or of a text not there exactly once, which that pre-flight counts.
    /// </summary>
    private static async Task<IReadOnlyList<string>> OverlapsAsync(IRegistryFiles files, MutationSite site, CancellationToken cancellationToken)
    {
        if (site.Further.Count == 0 || !files.Exists(site.Site))
        {
            return [];
        }

        var texts = site.Texts;

        try
        {
            var file = await files.ReadAsync(site.Site, cancellationToken).ConfigureAwait(false);
            var replaced = new List<(byte[] Before, byte[] After)>();

            foreach (var text in texts)
            {
                replaced.Add((
                    SiteEdit.Text(await files.ReadAsync(text.Before, cancellationToken).ConfigureAwait(false)),
                    SiteEdit.Text(await files.ReadAsync(text.After, cancellationToken).ConfigureAwait(false))));
            }

            var edits = SiteEdit.ApplyAll(file, replaced);

            return
            [
                .. edits.Overlapping.Select(pair =>
                    $"line {texts[pair.Later].Line}: the text in '{texts[pair.Later].Before}' overlaps the text in '{texts[pair.Earlier].Before}', "
                    + $"line {texts[pair.Earlier].Line}, in '{site.Site}': the texts of one arm in one file are replaced together, each as the "
                    + "tree holds the file, so no two share a byte of it"),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The site, there and not read: nothing can be said of its texts here, and the arm's pre-flight reads them
            // again in each worker's copy, where an overlap is the arm's own violation.
            return [];
        }
    }

    /// <summary>
    /// What is wrong with the text <paramref name="cited"/> on line <paramref name="line"/>, or <see langword="null"/>:
    /// no file of the tree, one a sync withholds, so that no copy of the tree would hold it, or, where
    /// <paramref name="empty"/> says a text holding nothing cannot be swept, one holding nothing - or one that could not
    /// be read to tell.
    /// </summary>
    private static async Task<string?> TextProblemAsync(IRegistryFiles files, string cited, int line, bool empty, CancellationToken cancellationToken)
    {
        if (!files.Exists(cited))
        {
            return $"line {line}: text '{cited}' is not a file in the tree";
        }

        if (files.IsWithheld(cited))
        {
            return $"line {line}: text '{cited}' is withheld from every copy of the tree by a sync - {Withholders} - so no worker would hold it";
        }

        if (!empty)
        {
            return null;
        }

        try
        {
            return SiteEdit.Text(await files.ReadAsync(cited, cancellationToken).ConfigureAwait(false)).Length == 0
                ? $"line {line}: the text in '{cited}' holds nothing, which occurs everywhere and which every run says"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"line {line}: text '{cited}' could not be read: {ex.Message}";
        }
    }
}
