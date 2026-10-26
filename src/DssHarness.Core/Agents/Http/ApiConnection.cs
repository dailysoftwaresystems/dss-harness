using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;

namespace DssHarness.Core.Agents.Http;

/// <summary>
/// The one way the two API clients reach their APIs (part 03, "How the two API clients talk to their APIs"): a request
/// posted as JSON and its answer read as it streams - each server-sent event as it comes - or the status and body of a
/// request the API refused, or why it never reached the API, or that its stream was cut. Nothing here knows one API from
/// the other: what an event, a status or a body means is the provider's.
/// </summary>
/// <remarks>
/// A request is given no time of its own: an answer may stream for many minutes, and the loop bounds it - by the step's
/// stall bound, its time and its pause - by the token it gives. A connection is made within
/// <see cref="ConnectBound"/>, through the machine's proxy where it names one.
/// </remarks>
public sealed class ApiConnection : IDisposable
{
    /// <summary>How long a connection to an API may take to be made.</summary>
    public static readonly TimeSpan ConnectBound = TimeSpan.FromSeconds(30);

    /// <summary>The most of a refused request's body read: what an API says of an error is a sentence, never a page.</summary>
    public const int MostRefusalBytes = 64 * 1024;

    /// <summary>The media type of an answer that streams.</summary>
    public const string EventStream = "text/event-stream";

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    /// <summary>A connection over the machine's own network.</summary>
    /// <param name="clock">Reads a wait an API names by its date.</param>
    public ApiConnection(TimeProvider clock)
        : this(clock, Connecting())
    {
    }

    /// <summary>A connection over <paramref name="handler"/>: the network a test stands in for.</summary>
    /// <param name="clock">Reads a wait an API names by its date.</param>
    /// <param name="handler">What sends each request.</param>
    internal ApiConnection(TimeProvider clock, HttpMessageHandler handler)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Sending = handler ?? throw new ArgumentNullException(nameof(handler));
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>What sends each request, as a test reads it.</summary>
    internal HttpMessageHandler Sending { get; }

    /// <summary>How long a request may take as a whole: no time of its own, for the loop bounds it.</summary>
    internal TimeSpan Bound => _http.Timeout;

    /// <summary>
    /// The handler a connection sends through on the machine's own network: a connection made within
    /// <see cref="ConnectBound"/>, no redirect followed - a request carries its key in a header of its own, which a
    /// redirect would send to wherever it points - and no cookie kept between requests.
    /// </summary>
    internal static SocketsHttpHandler Connecting()
        => new()
        {
            ConnectTimeout = ConnectBound,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseCookies = false,
        };

    /// <summary>
    /// Posts <paramref name="post"/> and yields what it answers: each event of the stream, in order, then nothing; or one
    /// <see cref="ApiEvent.Refused"/>, <see cref="ApiEvent.Unreached"/> or <see cref="ApiEvent.Cut"/>, which is the last.
    /// It throws for nothing the network or the API does: only the token ends it otherwise.
    /// </summary>
    /// <param name="post">The request.</param>
    /// <param name="cancellationToken">Ends the request where it stands.</param>
    public async IAsyncEnumerable<ApiEvent> PostAsync(ApiPost post, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);

        using var request = new HttpRequestMessage(HttpMethod.Post, post.Address) { Content = new ByteArrayContent(post.Body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(EventStream));

        foreach (var (name, value) in post.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage? response = null;
        string? unreached = null;

        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            unreached = ex.Message;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The connection's own bound, never the caller's token: the API was not reached in time.
            unreached = ex.Message;
        }

        if (response is null)
        {
            yield return new ApiEvent.Unreached(unreached ?? "no answer");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode || !IsEventStream(response))
            {
                yield return new ApiEvent.Refused((int)response.StatusCode, await RefusalAsync(response, cancellationToken).ConfigureAwait(false), RetryAfter(response));
                yield break;
            }

            Stream? body = null;
            string? cut = null;

            try
            {
                body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                cut = ex.Message;
            }

            if (body is null)
            {
                yield return new ApiEvent.Cut(cut ?? "no body");
                yield break;
            }

            await using (body.ConfigureAwait(false))
            {
                var events = SseParser.Create(body).EnumerateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

                await using (events.ConfigureAwait(false))
                {
                    while (true)
                    {
                        bool more;

                        try
                        {
                            more = await events.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or HttpRequestException)
                        {
                            cut = ex.Message;
                            more = false;
                        }

                        if (cut is not null)
                        {
                            yield return new ApiEvent.Cut(cut);
                            yield break;
                        }

                        if (!more)
                        {
                            yield break;
                        }

                        yield return new ApiEvent.Item(events.Current.EventType, events.Current.Data);
                    }
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    /// <summary>Whether an answer streams: an answer of another type, with whatever status, is no stream and is read as a refusal.</summary>
    private static bool IsEventStream(HttpResponseMessage response)
        => string.Equals(response.Content.Headers.ContentType?.MediaType, EventStream, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a refused request's body says, up to <see cref="MostRefusalBytes"/>; empty where it cannot be read.</summary>
    private static async Task<string> RefusalAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[MostRefusalBytes];
                var read = 0;

                while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false) is > 0 and var count)
                {
                    read += count;
                }

                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return string.Empty;
        }
    }

    /// <summary>The wait a refusal names, by seconds or by its date read on the clock; null where it names none.</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
        => response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - _clock.GetUtcNow() is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero,
            _ => null,
        };
}

/// <summary>One request to an API: where it goes, its headers and its body, as JSON.</summary>
/// <param name="Address">Where it is posted.</param>
/// <param name="Headers">Its headers beside the body's type and the stream asked for: its key among them, which nothing here writes anywhere.</param>
/// <param name="Body">Its body, as JSON.</param>
public sealed record ApiPost(Uri Address, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body)
{
    /// <summary>The address of <paramref name="path"/> under a provider's <c>api</c>: one slash between them, however the api ends.</summary>
    /// <param name="api">The provider's <c>api</c>, an absolute address.</param>
    /// <param name="path">The path under it.</param>
    public static Uri Under(string api, string path)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(path);

        return new Uri($"{api.TrimEnd('/')}/{path}", UriKind.Absolute);
    }
}

/// <summary>What an API answered a request with, as it came.</summary>
public abstract record ApiEvent
{
    /// <summary>One event of the answer's stream.</summary>
    /// <param name="Type">The event's type, as the stream names it.</param>
    /// <param name="Data">The event's data, whole.</param>
    public sealed record Item(string Type, string Data) : ApiEvent;

    /// <summary>The API refused the request, or answered it with no stream. The last.</summary>
    /// <param name="Status">The status it answered with.</param>
    /// <param name="Body">What its body said, cut at <see cref="ApiConnection.MostRefusalBytes"/>; empty where it said nothing.</param>
    /// <param name="RetryAfter">The wait it named before the request is asked again, where it named one.</param>
    public sealed record Refused(int Status, string Body, TimeSpan? RetryAfter) : ApiEvent;

    /// <summary>The request never reached the API: no connection, no answer. The last.</summary>
    /// <param name="Why">What the network said.</param>
    public sealed record Unreached(string Why) : ApiEvent;

    /// <summary>The answer's stream was cut part way. The last.</summary>
    /// <param name="Why">What the network said.</param>
    public sealed record Cut(string Why) : ApiEvent;
}
