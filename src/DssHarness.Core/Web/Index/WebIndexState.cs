using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Web.Index;

/// <summary>
/// What an index of a site says of itself, in its directory's <c>state.json</c>: which site, how many of its pages its
/// sitemaps list, when it was last filled whole and under which settings, when a fill last began, when a fill may begin
/// again, why the last fill stopped short, and the generation of its pages' file - raised each time the file is written
/// anew, so a reader that read it from an offset reads it again from its start.
/// </summary>
/// <param name="Site">The site indexed.</param>
/// <param name="Generation">The generation of <c>pages.jsonl</c>.</param>
/// <param name="Known">How many pages its sitemaps list that it may read, at most <c>maxPages</c>; <see langword="null"/> until they were read.</param>
/// <param name="FillStartedAt">When the last fill began; <see langword="null"/> where none has.</param>
/// <param name="FilledAt">When the last fill read all it was to; <see langword="null"/> where none has.</param>
/// <param name="PausedUntil">
/// When a fill may begin again: the time the site asked to be left alone until, or said its requests are spent until, or
/// the source's turn on this machine is spent until (<see cref="PausedFor"/> says which), or <see cref="AfterProblem"/>
/// after a fill stopped short, so a site that fails is not asked at every search; <see langword="null"/> where none holds.
/// </param>
/// <param name="Problem">Why a fill stopped short, kept until one ends whole; <see langword="null"/> where none stopped short since one did.</param>
public sealed record WebIndexState(
    string Site,
    int Generation,
    int? Known,
    DateTimeOffset? FillStartedAt,
    DateTimeOffset? FilledAt,
    DateTimeOffset? PausedUntil,
    string? Problem)
{
    /// <summary>The file it is kept in, in the index's directory.</summary>
    public const string FileName = "state.json";

    /// <summary>How long after a fill stopped short the next may begin.</summary>
    public static readonly TimeSpan AfterProblem = TimeSpan.FromHours(1);

    /// <summary>
    /// The settings it was last filled whole under (<see cref="SitemapSettings.Fingerprint"/>); <see langword="null"/>
    /// where none has been. A source whose settings changed since is filled again, at once.
    /// </summary>
    public string? Settings { get; init; }

    /// <summary>
    /// Why a fill is held off until <see cref="PausedUntil"/>, where it was not for a problem: the site's limit, its requests
    /// spent, or the source's turn spent (<see cref="SitemapFill.SiteLimited"/> and its siblings); <see langword="null"/> otherwise.
    /// </summary>
    public string? PausedFor { get; init; }

    /// <summary>
    /// The settings a fill that stopped short stopped under (<see cref="SitemapSettings.All"/>): a pause after a problem holds a
    /// fill under them alone, and one under settings changed since begins at once. <see langword="null"/> where none stopped short.
    /// </summary>
    public string? PausedUnder { get; init; }

    /// <summary>
    /// When a fill first found a sitemap of the site it could not read, since its listing was last read as far as its rules
    /// let; <see langword="null"/> where none has. A failure younger than the index may be old costs none of the pages held;
    /// one older gives them up to the pages the listing names.
    /// </summary>
    public DateTimeOffset? FailingSince { get; init; }

    /// <summary>The settings <see cref="Known"/> was counted under; a count of others is never kept.</summary>
    public string? KnownUnder { get; init; }

    /// <summary>
    /// This state, a fill stopped short for <paramref name="problem"/>: held off until <paramref name="until"/> under
    /// <paramref name="settings"/> alone, for no word of the site's or the machine's.
    /// </summary>
    /// <param name="problem">Why it stopped short.</param>
    /// <param name="until">When the next fill under the same settings may begin.</param>
    /// <param name="settings">The settings it stopped under (<see cref="SitemapSettings.All"/>).</param>
    public WebIndexState Stopped(string problem, DateTimeOffset until, string settings)
        => this with { Problem = problem, PausedUntil = until, PausedFor = null, PausedUnder = settings };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The state of the index in <paramref name="directory"/>; a new index's of <paramref name="site"/> where it has none, or one of another site.</summary>
    /// <param name="fileSystem">Where it is kept.</param>
    /// <param name="directory">The index's directory.</param>
    /// <param name="site">The site it is of.</param>
    /// <remarks>A state that cannot be read is a new index's: the index is derived, and costs only the time to read the site again.</remarks>
    public static WebIndexState Read(IFileSystem fileSystem, string directory, string site)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var path = Path.Combine(directory, FileName);

        try
        {
            if (fileSystem.FileExists(path)
                && JsonSerializer.Deserialize<WebIndexState>(fileSystem.ReadAllText(path), Options) is { } state
                && string.Equals(state.Site, site, StringComparison.Ordinal))
            {
                return state;
            }
        }
        catch (JsonException)
        {
            // Read as a new index's, as said above.
        }

        return new WebIndexState(site, 0, null, null, null, null, null);
    }

    /// <summary>Writes this state into <paramref name="directory"/>, whole, a reader never seeing half of it.</summary>
    /// <param name="fileSystem">Where it is kept.</param>
    /// <param name="directory">The index's directory, already there.</param>
    public void Write(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        fileSystem.WriteAllTextAtomic(Path.Combine(directory, FileName), JsonSerializer.Serialize(this, Options));
    }

    /// <summary>
    /// Whether a fill under <paramref name="settings"/> is due at <paramref name="now"/>, none being held off: none
    /// finished, the last began after the last finished, the last finished under other settings, or it is older than
    /// their refresh.
    /// </summary>
    /// <param name="now">The time.</param>
    /// <param name="settings">What the index reads now.</param>
    public bool Due(DateTimeOffset now, SitemapSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // A pause the site or the machine asked for holds whatever the settings; one after a problem, the settings it stopped under alone.
        var paused = PausedUntil is { } until && until > now && (PausedFor is not null || string.Equals(PausedUnder, settings.All, StringComparison.Ordinal));

        return !paused
            && (FilledAt is not { } filled
                || (FillStartedAt is { } started && started > filled)
                || !string.Equals(Settings, settings.Fingerprint, StringComparison.Ordinal)
                || now - filled >= settings.Refresh);
    }
}
