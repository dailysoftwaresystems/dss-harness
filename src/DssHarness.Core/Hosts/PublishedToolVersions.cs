using System.Globalization;
using System.Net;
using System.Text.Json;
using DssHarness.Core.Json;
using DssHarness.Core.Output;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Hosts;

/// <summary>What a feed said of the newest release: which it is, or why that could not be told.</summary>
/// <param name="Newest">The newest release, or <see langword="null"/> where the feed did not say.</param>
/// <param name="Untold">
/// Why it did not, as a clause about the feed - <c>it could not be reached: ...</c> - or <see langword="null"/> where it
/// did. Kept, because the causes are not one: a machine with no route out, a feed that is down, and an answer this build
/// cannot read are put right in three different places.
/// </param>
public sealed record PublishedAnswer(SemanticVersion? Newest, string? Untold)
{
    /// <summary>The feed said <paramref name="newest"/>.</summary>
    public static PublishedAnswer Of(SemanticVersion newest) => new(newest, null);

    /// <summary>The feed did not say, <paramref name="why"/> being how.</summary>
    public static PublishedAnswer NotTold(string why) => new(null, why);
}

/// <summary>Which versions of this tool are published where a host installs it from.</summary>
public interface IPublishedToolVersions
{
    /// <summary>
    /// The newest release published and listed that is newer than <paramref name="running"/>;
    /// <paramref name="running"/> itself where none is; or why that cannot be told: no network, a feed that refused, or
    /// an answer that is not what it should be. Waited for to its end, however long the feed takes, and told while it lasts.
    /// </summary>
    /// <param name="running">The build running here, which a release must be newer than to be named.</param>
    /// <param name="cancellationToken">Stops the asking; cancelling it is reported, not read as no answer.</param>
    Task<PublishedAnswer> NewestAsync(SemanticVersion running, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPublishedToolVersions"/>
/// <remarks>
/// Asked of nuget.org, the one feed a host installs the tool from (<see cref="ToolPackage.Source"/>). Its flat
/// container answers in one request with every version the package id holds, and that is unlisted ones too, so
/// each release newer than the one running is then asked of its registration leaf whether it is listed, the
/// newest first, until one is. A release unlisted is one withdrawn: <c>dotnet tool update</c> never picks it,
/// so no dispatcher runs it, and named as newer it had every copy say it trailed a release nobody was on - until
/// a newer one was listed. A host running the newest release asks one question. The addresses are the ones the
/// feed's service index names for nuget.org, spelt out rather than looked up, which would be one more request
/// for an answer that is only ever advisory. No clock of the code's own cuts the asking off: a machine with no route
/// out is told so by its own network - a name that does not resolve, a connection refused or timed out by the system -
/// and a feed that is slow to answer is waited for, told every few minutes, until the person stops the command.
/// </remarks>
public sealed class NuGetPublishedToolVersions : IPublishedToolVersions, IDisposable
{
    /// <summary>Where nuget.org lists every version of this tool's package, listed or not.</summary>
    public static readonly Uri VersionsAddress =
        new($"https://api.nuget.org/v3-flatcontainer/{ToolPackage.Id.ToLowerInvariant()}/index.json");

    /// <summary>Where nuget.org keeps each version's registration leaf, which says whether it is listed.</summary>
    public static readonly Uri LeavesAddress =
        new($"https://api.nuget.org/v3/registration5-gz-semver2/{ToolPackage.Id.ToLowerInvariant()}/");

    /// <summary>
    /// Made when the feed is first asked, and not before: every command that loads a repository resolves
    /// this, and only one typed in a synced copy ever asks.
    /// </summary>
    private readonly Lazy<HttpClient> _client;

    private readonly IHarnessOutput? _output;
    private readonly TimeProvider _clock;

    /// <summary>Asks nuget.org over this machine's own network.</summary>
    public NuGetPublishedToolVersions()
        : this(handler: null)
    {
    }

    /// <summary>Asks nuget.org over this machine's own network, telling <paramref name="output"/> of an answer that lasts.</summary>
    /// <param name="output">Where an answer slow to come is told.</param>
    public NuGetPublishedToolVersions(IHarnessOutput output)
        : this(handler: null, output)
    {
    }

    /// <summary>Asks through <paramref name="handler"/>, or over this machine's own network where none is given.</summary>
    /// <param name="handler">What sends the requests.</param>
    /// <param name="output">Where an answer slow to come is told; nowhere where none is given.</param>
    /// <param name="clock">The clock that wait is measured on; the machine's own where none is given.</param>
    internal NuGetPublishedToolVersions(HttpMessageHandler? handler, IHarnessOutput? output = null, TimeProvider? clock = null)
    {
        _output = output;
        _clock = clock ?? TimeProvider.System;

        // The registration leaves are served compressed; no timeout of the client's own, which the wait replaces.
        _client = new(() => new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
    }

    public async Task<PublishedAnswer> NewestAsync(SemanticVersion running, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(running);

        var asked = await Waits
            .ForAsync(
                token => AskAsync(running, token),
                new WaitWatch($"nuget.org to say which {ToolPackage.Id} release is the newest", ProgramWait.Told(_output, ToolPackage.Command), Bound: null, _clock),
                cancellationToken)
            .ConfigureAwait(false);

        return asked.Kind == WaitEndKind.Done ? asked.Value! : throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>Asks nuget.org which release is the newest; see <see cref="NewestAsync"/>.</summary>
    private async Task<PublishedAnswer> AskAsync(SemanticVersion running, CancellationToken cancellationToken)
    {
        try
        {
            var (versions, refused) = await ReadAsync(VersionsAddress, cancellationToken).ConfigureAwait(false);

            if (versions is null)
            {
                return PublishedAnswer.NotTold($"it answered {refused} for its list of versions");
            }

            if (Releases(versions) is not { } releases)
            {
                return PublishedAnswer.NotTold("its list of versions is not one this build reads");
            }

            foreach (var release in releases.Where(release => SemanticVersion.Compare(release, running) > 0))
            {
                var (leaf, withheld) = await ReadAsync(LeafAddress(release), cancellationToken).ConfigureAwait(false);

                switch (IsListed(leaf))
                {
                    case true:
                        return PublishedAnswer.Of(release);

                    case false:
                        continue;

                    default:
                        // Whether it is listed could not be told, and so neither can which release is newest.
                        return PublishedAnswer.NotTold(leaf is null
                            ? $"it answered {withheld} for whether {release} is listed"
                            : $"what it says of whether {release} is listed is not something this build reads");
                }
            }

            return PublishedAnswer.Of(running);
        }
        catch (HttpRequestException ex)
        {
            return PublishedAnswer.NotTold($"it could not be reached: {ex.Message.TrimEnd('.')}");
        }
    }

    /// <summary>The registration leaf of <paramref name="release"/>.</summary>
    /// <param name="release">A version of this tool's package.</param>
    public static Uri LeafAddress(SemanticVersion release)
    {
        ArgumentNullException.ThrowIfNull(release);

        return new Uri(LeavesAddress, $"{release.ToString().ToLowerInvariant()}.json");
    }

    /// <summary>
    /// Every release among the versions a flat container lists, the newest first, or <see langword="null"/>
    /// where <paramref name="json"/> lists none this build can read.
    /// </summary>
    /// <param name="json">The container's answer: <c>{"versions": ["0.5.9", "0.5.10", ...]}</c>.</param>
    /// <remarks>
    /// A prerelease is passed over. It sorts before the release it leads up to, and is never what a
    /// host is brought to unless its dispatcher runs one - in which case that dispatcher is ahead of
    /// every release, and so is the host it updates.
    /// </remarks>
    public static IReadOnlyList<SemanticVersion>? Releases(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, JsonDepth.Document);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("versions", out var versions)
                || versions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var releases = new List<SemanticVersion>();

            foreach (var entry in versions.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String
                    && SemanticVersion.TryParse(entry.GetString(), out var version)
                    && !version.IsPrerelease)
                {
                    releases.Add(version);
                }
            }

            releases.Sort((left, right) => SemanticVersion.Compare(right, left));

            return releases.Count > 0 ? releases : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest release among the versions a flat container lists, or <see langword="null"/> where
    /// <paramref name="json"/> lists none this build can read.
    /// </summary>
    /// <param name="json">The container's answer.</param>
    public static SemanticVersion? NewestRelease(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return Releases(json) is [var newest, ..] ? newest : null;
    }

    /// <summary>
    /// Whether a registration leaf says its version is listed: listed where it says nothing, as the feed's
    /// own rule has it; <see langword="null"/> where <paramref name="json"/> is not a leaf this build can read.
    /// </summary>
    /// <param name="json">The leaf, or <see langword="null"/> where the feed gave none.</param>
    public static bool? IsListed(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, JsonDepth.Document);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("listed", out var listed))
            {
                return true;
            }

            return listed.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    /// <summary>The body of what <paramref name="address"/> answers; or, where it answers with no success, the status it answers with.</summary>
    private async Task<(string? Body, string? Status)> ReadAsync(Uri address, CancellationToken cancellationToken)
    {
        using var response = await _client.Value.GetAsync(address, cancellationToken).ConfigureAwait(false);

        return response.IsSuccessStatusCode
            ? (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), null)
            : (null, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
    }
}
