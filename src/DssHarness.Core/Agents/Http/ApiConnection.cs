using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Agents.Http;

/// <summary>
/// The one way the two API clients reach their APIs (part 03, "How the two API clients talk to their APIs"): a request
/// posted as JSON and its answer read as it streams - each server-sent event as it comes - or the status and body of a
/// request the API refused, or why it never reached the API, or that its stream was cut. Nothing here knows one API from
/// the other: what an event, a status or a body means is the provider's.
/// </summary>
/// <remarks>
/// A request is given no time of its own, and its connection none either: an answer may stream for many minutes, a slow
/// link may take long to connect, and the loop bounds both - by the step's stall bound, its time and its pause - by the
/// token it gives; the operating system's own connect retries are what fail a connection that never comes. A
/// connection goes through the machine's proxy where it names one.
/// </remarks>
public sealed class ApiConnection : IDisposable
{
    /// <summary>The media type of an answer that streams.</summary>
    public const string EventStream = "text/event-stream";

    /// <summary>How much of a refused request's body is read at a time, each piece kept before the next is read.</summary>
    private const int RefusalPiece = 64 * 1024;

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    /// <summary>A connection over the machine's own network.</summary>
    /// <param name="clock">Reads a wait an API names by its date, and times how often a refused body still coming is told.</param>
    public ApiConnection(TimeProvider clock)
        : this(clock, Connecting())
    {
    }

    /// <summary>A connection over <paramref name="handler"/>: the network a test stands in for.</summary>
    /// <param name="clock">Reads a wait an API names by its date, and times how often a refused body still coming is told.</param>
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
    /// The handler a connection sends through on the machine's own network: no time of its own to connect in - the
    /// operating system's own retries of a connection, and the loop's token, are what end one that never comes - no
    /// redirect followed - a request carries its key in a header of its own, which a redirect would send to wherever it
    /// points - and no cookie kept between requests.
    /// </summary>
    internal static SocketsHttpHandler Connecting()
        => new()
        {
            ConnectTimeout = Timeout.InfiniteTimeSpan,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseCookies = false,
        };

    /// <summary>
    /// Posts <paramref name="post"/> and yields what it answers: each event of the stream, in order, then nothing; or one
    /// <see cref="ApiEvent.Refused"/>, <see cref="ApiEvent.Unreached"/> or <see cref="ApiEvent.Cut"/>, which is the last -
    /// a refusal's body read to its end first, each piece of it kept as it comes (<see cref="ApiEvent.Refusing"/>). It
    /// throws for nothing the network or the API does: only the token ends it otherwise.
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
            // The network's own end of the attempt, never the caller's token: the API was not reached.
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
                await foreach (var refusing in RefusalAsync(response, post.Records, cancellationToken).ConfigureAwait(false))
                {
                    yield return refusing;
                }

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

    /// <summary>
    /// A refused request's body, read to its end and kept whole as it comes - in its file of <paramref name="records"/>,
    /// else held - each piece said as it is kept (<see cref="ApiEvent.Refusing"/>), told every <see cref="Waits.SaidEvery"/>
    /// while it is still coming; then the refusal, or, where the network cut the body, the cut, naming what of it was kept.
    /// A body cut part way is no refusal said: what it would have said is unknown, so it is asked again.
    /// </summary>
    private async IAsyncEnumerable<ApiEvent> RefusalAsync(HttpResponseMessage response, ApiRecords? records, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var (stream, unread) = await OpenedAsync(response, cancellationToken).ConfigureAwait(false);
        using var kept = ApiRefusedBody.Keeping(records);
        var told = _clock.GetTimestamp();

        if (stream is not null)
        {
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[RefusalPiece];

                while (true)
                {
                    var (read, failed) = await PieceAsync(stream, buffer, cancellationToken).ConfigureAwait(false);

                    if (failed is not null)
                    {
                        unread = failed;
                        break;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    kept.Append(buffer.AsSpan(0, read));

                    var now = _clock.GetTimestamp();
                    var tell = _clock.GetElapsedTime(told, now) >= Waits.SaidEvery;

                    if (tell)
                    {
                        told = now;
                    }

                    yield return new ApiEvent.Refusing(status, kept.Bytes, tell);
                }
            }
        }

        var body = kept.Kept();

        yield return unread is null
            ? new ApiEvent.Refused(status, body, RetryAfter(response))
            : new ApiEvent.Cut($"the API answered {status.ToString(CultureInfo.InvariantCulture)}, and its body could not be read to its end: {unread}{(body.File is { } file ? $"; the {body.Bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes that came are kept in '{file}'" : string.Empty)}");
    }

    /// <summary>The body's stream; or, where it could not be opened, what the network said of it.</summary>
    private static async Task<(Stream? Stream, string? Unread)> OpenedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return (await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>The next piece of a refused body: how many bytes came, none at its end; or what the network said where it failed.</summary>
    private static async Task<(int Read, string? Failed)> PieceAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return (0, ex.Message);
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

/// <summary>One request to an API: where it goes, its headers and its body, as JSON, and where a refusal's body is kept.</summary>
/// <param name="Address">Where it is posted.</param>
/// <param name="Headers">Its headers beside the body's type and the stream asked for: its key among them, which nothing here writes anywhere.</param>
/// <param name="Body">Its body, as JSON.</param>
/// <param name="Records">
/// Where the body of a refusal of it is kept whole, as it streams: the step's records; null for a request that keeps
/// none, whose refused body is held whole instead.
/// </param>
public sealed record ApiPost(Uri Address, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body, ApiRecords? Records = null)
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

/// <summary>
/// Where the bodies of the requests an API refuses are kept whole, as they stream: one file each in
/// <see cref="DirectoryName"/> under the step's directory, beside its other records - so a body of any size is read to
/// its end and never held whole, and a failure that cannot say it in its own words names where it is.
/// </summary>
/// <param name="FileSystem">Keeps the files.</param>
/// <param name="Directory">The step's directory.</param>
public sealed record ApiRecords(IFileSystem FileSystem, string Directory)
{
    /// <summary>The directory, in the step's directory, each refused request's body is kept in.</summary>
    public const string DirectoryName = "api-refusals";

    /// <summary>The file the next refused body is kept in, its directory made: named afresh each time, so no two refusals share one.</summary>
    internal string Next()
    {
        var directory = Path.Combine(Directory, DirectoryName);
        FileSystem.CreateDirectory(directory);

        return Path.Combine(directory, $"{Guid.NewGuid():N}.txt");
    }
}

/// <summary>
/// The body of a request an API refused, kept whole: in its file of the request's records (<see cref="ApiRecords"/>), or
/// held, for a request that keeps none. Read again from where it is kept, as a stream, so what reads it - the provider's
/// reading of the API's error - never holds it whole.
/// </summary>
public sealed class ApiRefusedBody
{
    private readonly IFileSystem? _fileSystem;
    private readonly byte[] _held;

    private ApiRefusedBody(long bytes, string? file, IFileSystem? fileSystem, byte[] held)
    {
        Bytes = bytes;
        File = file;
        _fileSystem = fileSystem;
        _held = held;
    }

    /// <summary>How many bytes the body holds.</summary>
    public long Bytes { get; }

    /// <summary>The file the body is kept in, whole; null where it is held.</summary>
    public string? File { get; }

    /// <summary>A body held, of <paramref name="text"/>'s UTF-8 bytes: one a test, or a request that keeps no records, gives.</summary>
    /// <param name="text">The body's text.</param>
    public static ApiRefusedBody Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = Encoding.UTF8.GetBytes(text);
        return new(bytes.Length, null, null, bytes);
    }

    /// <summary>The body, read from where it is kept.</summary>
    public Stream Open() => File is { } file ? _fileSystem!.OpenRead(file) : new MemoryStream(_held, writable: false);

    /// <summary>
    /// The body's text, whole, where it is held; null where it is kept in a file, which is named, never read whole here.
    /// </summary>
    public string? HeldText => File is null ? Encoding.UTF8.GetString(_held) : null;

    /// <summary>Begins keeping a refused body: in a file of <paramref name="records"/> where given, else held.</summary>
    /// <param name="records">The request's records; null for one that keeps none.</param>
    internal static Keeper Keeping(ApiRecords? records) => new(records);

    /// <summary>A refused body being kept as it comes, each piece kept before the next is read.</summary>
    internal sealed class Keeper : IDisposable
    {
        private readonly ApiRecords? _records;
        private readonly MemoryStream? _held;
        private IAppendFile? _file;
        private string? _path;

        public Keeper(ApiRecords? records)
        {
            _records = records;
            _held = records is null ? new MemoryStream() : null;
        }

        /// <summary>How many bytes have been kept.</summary>
        public long Bytes { get; private set; }

        /// <summary>Keeps <paramref name="piece"/>, after every piece before it.</summary>
        public void Append(ReadOnlySpan<byte> piece)
        {
            if (_held is not null)
            {
                _held.Write(piece);
            }
            else
            {
                _file ??= _records!.FileSystem.OpenForAppend(_path = _records.Next());
                _file.AppendFlushed(piece);
            }

            Bytes += piece.Length;
        }

        /// <summary>What was kept, whole: a body of no bytes is held, for there is nothing to name.</summary>
        public ApiRefusedBody Kept()
        {
            _file?.Dispose();
            _file = null;

            return _path is { } path
                ? new ApiRefusedBody(Bytes, path, _records!.FileSystem, [])
                : new ApiRefusedBody(Bytes, null, null, _held?.ToArray() ?? []);
        }

        public void Dispose()
        {
            _file?.Dispose();
            _held?.Dispose();
        }
    }
}

/// <summary>What an API answered a request with, as it came.</summary>
public abstract record ApiEvent
{
    /// <summary>One event of the answer's stream.</summary>
    /// <param name="Type">The event's type, as the stream names it.</param>
    /// <param name="Data">The event's data, whole.</param>
    public sealed record Item(string Type, string Data) : ApiEvent;

    /// <summary>
    /// The API is refusing the request and its body is still coming: so much of it is kept, and whether it is time to
    /// tell that it is still coming (<see cref="Waits.SaidEvery"/>) - an answer that trickles is not silent.
    /// </summary>
    /// <param name="Status">The status it answered with.</param>
    /// <param name="Bytes">How many bytes of its body are kept so far.</param>
    /// <param name="Told">Whether this piece is to be told: the first after each <see cref="Waits.SaidEvery"/> the body kept coming.</param>
    public sealed record Refusing(int Status, long Bytes, bool Told) : ApiEvent;

    /// <summary>The API refused the request, or answered it with no stream. The last.</summary>
    /// <param name="Status">The status it answered with.</param>
    /// <param name="Body">Its body, whole, where it is kept; of no bytes where it said nothing.</param>
    /// <param name="RetryAfter">The wait it named before the request is asked again, where it named one.</param>
    public sealed record Refused(int Status, ApiRefusedBody Body, TimeSpan? RetryAfter) : ApiEvent
    {
        /// <summary>A refusal of <paramref name="status"/> whose body, held, is <paramref name="body"/>: one a test gives.</summary>
        /// <param name="status">The status.</param>
        /// <param name="body">The body's text.</param>
        /// <param name="retryAfter">The wait it named.</param>
        public Refused(int status, string body, TimeSpan? retryAfter)
            : this(status, ApiRefusedBody.Of(body), retryAfter)
        {
        }
    }

    /// <summary>The request never reached the API: no connection, no answer. The last.</summary>
    /// <param name="Why">What the network said.</param>
    public sealed record Unreached(string Why) : ApiEvent;

    /// <summary>
    /// The answer's stream was cut part way - or the body of a refusal was, so what the API said of it is unknown. The last.
    /// </summary>
    /// <param name="Why">What the network said, after the status a refusal's cut body came with and where what came of it is kept.</param>
    public sealed record Cut(string Why) : ApiEvent;
}
