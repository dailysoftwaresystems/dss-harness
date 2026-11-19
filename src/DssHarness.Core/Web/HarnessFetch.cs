using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Hosts;
using DssHarness.Core.Waiting;
using DssHarness.Core.Walks;

namespace DssHarness.Core.Web;

/// <summary>
/// The harness's own fetch, which makes every request a reader of the web sends - every page an agent reads, every
/// request a site's index is filled by: <c>http</c> and <c>https</c> alone; each address - the first and each redirect,
/// followed as a chain to its end, a loop ended at its first revisit - decided before anything is sent, then given its
/// turn by the reader's pace, where it has one (<see cref="WebFetchGate"/>); a host that resolves to any address no public
/// host holds refused, and a connection made only to an address that was checked, and checked again once made; an answer
/// waited for for as long as it moves, its silence told and ended at the reader's stall bound
/// (<see cref="WebFetchGate.Stall"/>) or its stop, a decision and a turn waited for apart; read whole, streamed into the
/// reader's writer where it names one (<see cref="WebFetchAsk.Into"/>); text alone - <c>text/*</c>, JSON and XML, and a
/// gzip of text where the reader asks - read as the reader asks (<see cref="WebFetchAsk"/>): a page's HTML reduced to its
/// text, read into its parts or kept as its markup; no cookie, no proxy, nothing of the tool's environment and no
/// credential of the machine's sent, under a fixed <c>User-Agent</c> that names the tool - a search source's own key alone
/// where its asker gives one (<see cref="WebFetchAsk.Key"/>), to the scheme, host and port first asked, one its source's
/// configuration names.
/// </summary>
public sealed class HarnessFetch : IDisposable
{
    /// <summary>The rule an address a search source's key may not be sent to is refused by (<see cref="WebFetchAsk.Key"/>): https alone, its origins alone, and no parameter of its own name.</summary>
    public const string KeyRule = "the source's key";

    /// <summary>The types of what a fetch reads, beside every <c>text/</c> one and every <c>+json</c> and <c>+xml</c> one.</summary>
    public static IReadOnlyList<string> TextTypes { get; } = ["application/json", "application/xml", "application/xhtml+xml", "application/javascript", "application/ecmascript"];

    /// <summary>Every type a fetch reads, as help, a tool's description and a refusal say them.</summary>
    public static string TextTypesSaid => $"text/*, {string.Join(", ", TextTypes)} and every +json and +xml type";

    /// <summary>How many bytes or characters an answer is read in at a time.</summary>
    private const int Chunk = 81920;

    private readonly IWebNames _names;
    private readonly TimeProvider _clock;
    private readonly string _userAgent;
    private readonly Lazy<HttpMessageInvoker> _client;

    /// <summary>Fetches over this machine's own network, its names looked up by <paramref name="names"/>.</summary>
    /// <param name="identity">The tool, whose name and version the <c>User-Agent</c> says.</param>
    /// <param name="names">Finds a host's addresses.</param>
    public HarnessFetch(IToolIdentityProvider identity, IWebNames names)
        : this(identity, names, handler: null)
    {
    }

    /// <summary>Fetches through <paramref name="handler"/>, or over this machine's own network where none is given.</summary>
    /// <param name="identity">The tool, whose name and version the <c>User-Agent</c> says.</param>
    /// <param name="names">Finds a host's addresses.</param>
    /// <param name="handler">What sends the requests: a test's endpoint, or null for <see cref="Connecting"/>'s own.</param>
    /// <param name="clock">The clock an answer's silence is measured on: the machine's where none is given, a test's that it moves.</param>
    internal HarnessFetch(IToolIdentityProvider identity, IWebNames names, HttpMessageHandler? handler, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(names);

        _names = names;
        _clock = clock ?? TimeProvider.System;
        _userAgent = $"{ToolPackage.Command}/{identity.Current.Version} (+{ToolPackage.ProjectUrl})";
        _client = new(() => new HttpMessageInvoker(handler ?? Connecting(names), disposeHandler: true), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The handler the fetch sends through on this machine's own network: no redirect followed by itself, no cookie, no
    /// credential, no proxy, no time of its own for a connection - the fetch's wait on the answer's silence holds it - and
    /// each connection made only to an address <paramref name="names"/> gives that is public - every one the host resolves
    /// to checked, so none is reached by a lookup's second answer - and checked again as the address the connection was in
    /// fact made to.
    /// </summary>
    /// <param name="names">Finds a host's addresses.</param>
    internal static SocketsHttpHandler Connecting(IWebNames names)
    {
        ArgumentNullException.ThrowIfNull(names);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = Timeout.InfiniteTimeSpan,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(names, context.DnsEndPoint, cancellationToken),
        };
    }

    /// <summary>
    /// Connects to <paramref name="endpoint"/> by an address of its host's that is public, every address it resolves to
    /// checked first, and the one connected to checked again.
    /// </summary>
    /// <param name="names">Finds the host's addresses.</param>
    /// <param name="endpoint">The host and port asked for.</param>
    /// <param name="cancellationToken">Stops the connection.</param>
    /// <exception cref="HttpRequestException">The host resolves to an address no public host holds, or connected to one.</exception>
    internal static async ValueTask<Stream> ConnectAsync(IWebNames names, DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var addresses = await AddressesAsync(names, endpoint.Host, cancellationToken).ConfigureAwait(false);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync([.. addresses], endpoint.Port, cancellationToken).ConfigureAwait(false);

            if (socket.RemoteEndPoint is not IPEndPoint connected || WebAddresses.NotPublic(connected.Address) is { } what)
            {
                throw new HttpRequestException($"the connection to '{endpoint.Host}' was made to {Said(socket.RemoteEndPoint)}, which is {What(socket.RemoteEndPoint)}: this tool's fetch reaches public addresses alone");
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        static string Said(EndPoint? point) => point?.ToString() ?? "no address";

        static string What(EndPoint? point) => point is IPEndPoint ip ? WebAddresses.NotPublic(ip.Address) ?? "an address" : "no internet address";
    }

    /// <summary>
    /// Fetches <paramref name="address"/> as a page: each address - the first and each redirect - decided by
    /// <paramref name="gate"/> and given its turn before anything is sent, and each answer told to it; the page's text, or
    /// why there is none.
    /// </summary>
    /// <param name="address">The address an agent asked for.</param>
    /// <param name="gate">
    /// Decides each address before it is asked - a denial ends the fetch, nothing sent - gives it its turn, and is told what
    /// each was answered.
    /// </param>
    /// <param name="cancellationToken">Stops the fetch.</param>
    public Task<WebFetched> FetchAsync(string address, WebFetchGate gate, CancellationToken cancellationToken)
        => FetchAsync(address, WebFetchAsk.Page, gate, cancellationToken);

    /// <summary>
    /// Fetches <paramref name="address"/> as <paramref name="ask"/> says - a page's parts read or its markup kept, a gzip of
    /// text read, the text streamed into a writer - each address decided by <paramref name="gate"/> first and each answer
    /// told to it, as <see cref="FetchAsync(string, WebFetchGate, CancellationToken)"/> does.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="ask">What is read, and how.</param>
    /// <param name="gate">
    /// Decides each address before it is asked - a denial ends the fetch, nothing sent - gives it its turn, is told what
    /// each was answered, and bounds and tells an answer's silence.
    /// </param>
    /// <param name="cancellationToken">Stops the fetch.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> stopped it.</exception>
    public async Task<WebFetched> FetchAsync(string address, WebFetchAsk ask, WebFetchGate gate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(gate);

        var run = new FetchRun(this, ask, gate);
        Exception? failed = null;

        try
        {
            // Followed as a chain to its end: every address asked, by its method, is kept, and a redirect back to one is a
            // loop, ended at its first revisit (RFC 9110, 15.4) before it is asked again. No count ends it: a chain of new
            // addresses is followed, each decided and recorded, until it ends or the reader's bounds or stop end the fetch.
            var chain = await Chain.FollowAsync(
                new WebAsking(address, ask.Method, ask.Body, First: true),
                run.HopAsync,
                asking => (asking.Method, run.KeyOf(asking.Address)),
                followed: null,
                cancellationToken).ConfigureAwait(false);

            if (chain.Cycled)
            {
                var last = run.KeyOf(chain.Members[^1].Address);
                return WebFetched.Refused(last, $"'{address}' redirects in a loop: '{last}' leads back to '{run.KeyOf(chain.RepeatedAt!.Address)}', asked before, and it is not asked again");
            }

            return run.Ended!;
        }
        catch (Exception ex)
        {
            failed = ex;
            throw;
        }
        finally
        {
            await run.EndTurnAsync(failed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// An address a search source's key may not be sent to, refused by the key's own rules for <paramref name="why"/>:
    /// before any rule of its asker decides it, nothing sent - a refusal, as a rule's is, never a failure of the source's.
    /// </summary>
    private static AgentPolicyAnswer.Denied KeyDenial(string why) => new(KeyRule, AgentPolicySource.Builtin, why);

    /// <summary><paramref name="uri"/> with every query parameter whose name, unescaped, is <paramref name="name"/> dropped; itself where it has none.</summary>
    private static Uri WithoutParameter(Uri uri, string name)
    {
        var pairs = uri.Query.Length > 1 ? uri.Query[1..].Split('&') : [];
        var kept = pairs.Where(pair => !string.Equals(Uri.UnescapeDataString(pair.Split('=', 2)[0]), name, StringComparison.Ordinal)).ToList();

        return kept.Count == pairs.Length
            ? uri
            : new Uri(uri.GetLeftPart(UriPartial.Path) + (kept.Count > 0 ? "?" + string.Join('&', kept) : string.Empty) + uri.Fragment);
    }

    /// <summary>Closes the connections the fetch holds.</summary>
    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    /// <summary>The headers a request itself says - which tool asks, of which host, how much and of what type - which no asker sends of its own.</summary>
    public static IReadOnlyList<string> RequestHeaders { get; } = ["User-Agent", "Host", "Content-Length", "Content-Type"];

    /// <summary>The headers that carry a key, which an asker sends only as its key (<see cref="WebFetchAsk.Key"/>), kept secret.</summary>
    public static IReadOnlyList<string> KeyHeaders { get; } = ["Authorization", "Proxy-Authorization", "Cookie"];

    /// <summary>
    /// Whether a request carries <paramref name="name"/> of its own: a header only a body says of itself - its language, its
    /// encoding, when it expires - no request carries, so an asker that names one is refused where it is configured.
    /// </summary>
    /// <param name="name">A header's name.</param>
    public static bool IsRequestHeader(string name)
    {
        using var request = new HttpRequestMessage();
        return request.Headers.TryAddWithoutValidation(name, string.Empty);
    }

    /// <summary>Why an answer whose silence reached <paramref name="stall"/> was not read: the reader's stall bound.</summary>
    internal static string Silent(TimeSpan stall)
        => $"nothing came from it for {stall.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s, the step's stall bound";

    /// <summary>
    /// Asks one address as <paramref name="method"/> with <paramref name="body"/> - the key the ask carries where
    /// <paramref name="keyed"/> - and answers its answer, a redirect's next address, or why it failed: its markup as it came,
    /// read into its text by the caller, or streamed into the ask's writer where it is no page to reduce. Each byte read is
    /// told to <paramref name="moved"/>, which the wait on the answer's silence reads. <paramref name="cancellationToken"/>
    /// is that wait's - its stall bound's and the caller's stop - and <paramref name="caller"/> the caller's stop alone.
    /// </summary>
    private async Task<WebHop> AskAsync(Uri uri, WebFetchAsk ask, string method, string? body, bool keyed, Action<int> moved, TimeSpan? stall, CancellationToken cancellationToken, CancellationToken caller)
    {
        var said = uri.AbsoluteUri;

        // The host is checked before anything is sent; the connection checks it again, and the address it reached.
        try
        {
            await AddressesAsync(_names, uri.IdnHost, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return WebHop.Failed(said, ex.Message);
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Headers.UserAgent.ParseAdd(_userAgent);

        var asked = ask.Headers ?? WebFetchAsk.NoHeaders;
        var accept = asked.FirstOrDefault(header => string.Equals(header.Key, "Accept", StringComparison.OrdinalIgnoreCase)).Value;

        // A key sent as a query parameter is added to what is sent alone - after the query, never inside a fragment, which
        // is not sent: the address decided, recorded and answered is the one asked, which holds no parameter of its name.
        if (keyed && ask.Key is { Query: true } parameter)
        {
            request.RequestUri = new Uri(uri.GetLeftPart(UriPartial.Query) + (uri.Query.Length > 0 ? "&" : "?") + Uri.EscapeDataString(parameter.Name) + "=" + Uri.EscapeDataString(parameter.Value));
        }

        Say(request, "Accept", accept ?? ask.Accept ?? "text/html, text/plain;q=0.9, application/json;q=0.9, application/xml;q=0.8, text/*;q=0.7");

        // The asker's own headers, never one the request itself says: the body's type goes with the body.
        foreach (var (name, value) in asked.Where(header => !string.Equals(header.Key, "Accept", StringComparison.OrdinalIgnoreCase)
            && !RequestHeaders.Concat(KeyHeaders).Contains(header.Key, StringComparer.OrdinalIgnoreCase)))
        {
            Say(request, name, value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (keyed && ask.Key is { Query: false } key)
        {
            Say(request, key.Name, key.Value);
        }

        HttpResponseMessage response;

        try
        {
            response = await _client.Value.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // The inner failure's words where the outer ones leave them out ("see inner exception"), never said twice.
            return WebHop.Failed(said, ex.InnerException is { } inner && !ex.Message.Contains(inner.Message, StringComparison.Ordinal)
                ? $"{ex.Message.TrimEnd('.')}: {inner.Message}"
                : ex.Message);
        }

        using (response)
        {
            // The answer's head came: a move, as each byte of its body is.
            moved(0);

            var headers = HeadersOf(response);

            try
            {
                return await AnsweredAsync(said, uri, ask, response, moved, cancellationToken).ConfigureAwait(false) with { Headers = headers };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !caller.IsCancellationRequested && stall is { } bound)
            {
                // The answer fell silent past the stall bound in its body: the status and headers already read are kept, so a
                // pause they ask for holds.
                return WebHop.Failed(said, Silent(bound), (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType) with { Headers = headers };
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="name"/> to what <paramref name="request"/> says: a header no request carries (<see cref="IsRequestHeader"/>)
    /// is refused where it is configured, so one asked here is a defect, never dropped unsaid - and its value, a key's maybe, is never told.
    /// </summary>
    /// <exception cref="InvalidOperationException">No request carries it.</exception>
    private static void Say(HttpRequestMessage request, string name, string value)
    {
        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            throw new InvalidOperationException($"'{name}' was asked to be sent, and is no header a request carries");
        }
    }

    /// <summary>
    /// Reads one answer: a redirect's next address, its text - whole, streamed into the ask's writer where it names one and
    /// the answer is no page to reduce - or why it is none.
    /// </summary>
    private static async Task<WebHop> AnsweredAsync(string said, Uri uri, WebFetchAsk ask, HttpResponseMessage response, Action<int> moved, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var type = response.Content.Headers.ContentType?.MediaType;

        if (status is 301 or 302 or 303 or 307 or 308)
        {
            return response.Headers.Location is not { } location
                ? WebHop.Failed(said, $"a redirect ({status.ToString(CultureInfo.InvariantCulture)}) that names no address", status)
                : Uri.TryCreate(uri, location, out var next)
                    ? WebHop.Redirected(said, status, next.AbsoluteUri)
                    : WebHop.Failed(said, $"a redirect ({status.ToString(CultureInfo.InvariantCulture)}) to '{location.OriginalString}', which reads as no address", status);
        }

        // Refused unread where the asker's own protocol bounds what it reads, and the answer's size is past it: said in why,
        // and no byte came back.
        if (ask.Most is { } most && response.Content.Headers.ContentLength is { } length && length > most)
        {
            return WebHop.Failed(said, $"{length.ToString("N0", CultureInfo.InvariantCulture)} bytes, past the {FileSystem.DiskSpace.Size(most)} {ask.MostSaid}", status, type);
        }

        if (!(ask.Gzip && IsGzip(type, uri)) && !ask.AnyType && !IsText(type))
        {
            return WebHop.Failed(said, $"its type is {(type is null ? "not said" : $"'{type}'")}, and this tool's fetch reads text alone: {TextTypesSaid}", status, type);
        }

        // A page reduced to its text, or read into its parts, is read whole as markup first: its reader needs it whole.
        var page = IsHtml(type) && ask.Html != WebFetchHtml.Markup;
        var into = page ? null : ask.Into;
        var read = 0L;
        var gzip = false;
        var writing = false;

        WebHop Past(long most)
            => WebHop.Failed(said, $"more than the {FileSystem.DiskSpace.Size(most)} {ask.MostSaid}{(gzip ? " once decompressed" : string.Empty)}", status, type, read);

        try
        {
            await using var raw = new FetchStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), ReadOnlyMemory<byte>.Empty, moved);
            Stream stream = raw;

            // A gzip is known by its bytes, never by what it is said to be: one said to be bytes or XML is a gzip all the
            // same, and one the transport already decompressed, still said to be a gzip, is the text it holds.
            if (ask.Gzip)
            {
                var head = new byte[2];
                var headRead = await raw.ReadAtLeastAsync(head, 2, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                stream = new FetchStream(raw, head.AsMemory(0, headRead), null);
                gzip = headRead == 2 && head[0] == 0x1F && head[1] == 0x8B;

                if (gzip)
                {
                    stream = new GZipStream(stream, CompressionMode.Decompress);
                }
            }

            // Read a chunk at a time, never held as bytes: decoded as it comes, and held as one text, or written as it is
            // read into the ask's writer.
            await using var counted = new FetchStream(stream, ReadOnlyMemory<byte>.Empty, bytes => read += bytes);
            using var reader = new StreamReader(counted, EncodingOf(gzip ? null : response.Content.Headers.ContentType?.CharSet), detectEncodingFromByteOrderMarks: true, Chunk, leaveOpen: true);
            var chunk = new char[Chunk];
            var text = into is null ? new StringBuilder() : null;
            var written = 0L;

            while (await reader.ReadAsync(chunk, cancellationToken).ConfigureAwait(false) is var characters and > 0)
            {
                if (ask.Most is { } bound && read > bound)
                {
                    return Past(bound);
                }

                if (into is not null)
                {
                    writing = true;
                    await into.WriteAsync(chunk.AsMemory(0, characters), cancellationToken).ConfigureAwait(false);
                    writing = false;
                }
                else
                {
                    text!.Append(chunk, 0, characters);
                }

                written += characters;
            }

            if (ask.Most is { } last && read > last)
            {
                return Past(last);
            }

            return text is not null
                ? WebHop.Read(said, status, type ?? (gzip ? "application/gzip" : null), read, text.ToString())
                : WebHop.Read(said, status, type ?? (gzip ? "application/gzip" : null), read, null) with { Written = written };
        }
        catch (Exception ex) when (!writing && (ex is IOException or HttpRequestException) && cancellationToken.IsCancellationRequested)
        {
            // A read the wait's stop ended, said by the transport as a failure of its own: the stop it is.
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (Exception ex) when (!writing && (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException))
        {
            // A body that ends early, or that its encoding does not decode: gzip said of a plain page raises
            // InvalidDataException, a broken brotli InvalidOperationException, as the runtime's decoders do. What the ask's
            // writer fails at is its own failure, raised through.
            return WebHop.Failed(said, $"its body could not be read: {ex.Message.TrimEnd('.')}", status, type);
        }
    }

    /// <summary>Whether an answer of <paramref name="type"/> from <paramref name="uri"/> is a gzip: said so, or bytes of no type said under a name ending in <c>.gz</c>.</summary>
    private static bool IsGzip(string? type, Uri uri)
        => type is not null && (type.Equals("application/gzip", StringComparison.OrdinalIgnoreCase) || type.Equals("application/x-gzip", StringComparison.OrdinalIgnoreCase))
            || ((type is null || type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));

    /// <summary>An answer's headers by name in any case, the content's among them, each one's values joined by commas.</summary>
    private static IReadOnlyDictionary<string, string> HeadersOf(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            headers[name] = string.Join(", ", values);
        }

        return headers;
    }

    /// <summary>Whether <paramref name="type"/> is one a fetch reads: every <c>text/</c> type, <see cref="TextTypes"/>, and every JSON and XML type.</summary>
    private static bool IsText(string? type)
        => type is not null
            && (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || TextTypes.Contains(type, StringComparer.OrdinalIgnoreCase)
                || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="type"/> is a page's markup, which is read as the ask says (<see cref="WebFetchHtml"/>): HTML or XHTML, in any case.</summary>
    /// <param name="type">A type an answer said.</param>
    internal static bool IsHtml(string? type)
        => string.Equals(type, "text/html", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The encoding <paramref name="name"/> names: one the runtime holds, or a code page's - windows-1252, shift_jis - which
    /// the runtime holds only through <see cref="CodePagesEncodingProvider"/>, asked here alone rather than for the process.
    /// </summary>
    /// <exception cref="ArgumentException">Neither knows it.</exception>
    private static Encoding Named(string name) => CodePagesEncodingProvider.Instance.GetEncoding(name) ?? Encoding.GetEncoding(name);

    /// <summary>
    /// The encoding of a body by the character set the answer names - else UTF-8, its mark read first where it has one - a
    /// byte that forms no character read as U+FFFD, for a page is read, never written back.
    /// </summary>
    private static Encoding EncodingOf(string? charset)
    {
        try
        {
            return charset is { Length: > 0 } named ? Named(named.Trim('"', '\'')) : Encoding.UTF8;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // A set neither knows, or one the runtime holds and will not read - UTF-7, which it turned off.
            return Encoding.UTF8;
        }
    }

    /// <summary>Every address <paramref name="host"/> resolves to, each public; refused where any is not, or none is given.</summary>
    /// <exception cref="HttpRequestException">The host resolves to an address no public host holds, or to none.</exception>
    private static async Task<IReadOnlyList<IPAddress>> AddressesAsync(IWebNames names, string host, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> addresses;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await names.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                // A name the system's lookup takes no question of - longer than a name may be - is one no host answers.
                throw new HttpRequestException($"'{host}' could not be looked up: {ex.Message}", ex);
            }
        }

        if (addresses.Count == 0)
        {
            throw new HttpRequestException($"'{host}' resolves to no address");
        }

        foreach (var address in addresses)
        {
            if (WebAddresses.NotPublic(address) is { } what)
            {
                throw new HttpRequestException($"'{host}' resolves to {address}, which is {what}: this tool's fetch reaches public addresses alone");
            }
        }

        return addresses;
    }

    /// <summary>One address of a fetch's chain: what is asked, how, and whether it is the first.</summary>
    /// <param name="Address">The address, as asked or as a redirect named it.</param>
    /// <param name="Method">The method it is asked by.</param>
    /// <param name="Body">The body it is sent; none for a GET.</param>
    /// <param name="First">Whether it is the address first asked.</param>
    private sealed record WebAsking(string Address, string Method, string? Body, bool First);

    /// <summary>One fetch, its chain's hops one by one: where its key may go, whether its pace gave it a turn, and how it ended.</summary>
    private sealed class FetchRun(HarnessFetch fetch, WebFetchAsk ask, WebFetchGate gate)
    {
        private string? _keyedTo;
        private bool _turned;

        /// <summary>What the fetch ended with, at the chain's end; null while it goes on.</summary>
        public WebFetched? Ended { get; private set; }

        /// <summary>
        /// The address a chain member is known by: as it is asked - read, and with the key's parameter dropped where the key
        /// is sent as one, so a redirect that echoes it back is the address it came from - else as written.
        /// </summary>
        public string KeyOf(string asked)
            => !WebAddresses.IsWeb(asked, out var uri) ? asked
                : ask.Key is { Query: true } parameter ? WithoutParameter(uri, parameter.Name).AbsoluteUri
                : uri.AbsoluteUri;

        /// <summary>Tells the gate the fetch has ended, where its pace was asked for a turn: what lets a turn held for the whole fetch go.</summary>
        public async Task EndTurnAsync(Exception? failed)
        {
            if (!_turned || gate.Ended is not { } ended)
            {
                return;
            }

            try
            {
                await ended().ConfigureAwait(false);
            }
            catch (Exception releasing) when (failed is not null)
            {
                // What ended the fetch is never hidden by what failed letting its turn go: both are raised.
                throw new AggregateException(failed, releasing);
            }
        }

        /// <summary>One hop: decided, given its turn, asked under the wait on its silence, and read; the chain's next address, or its end.</summary>
        public async Task<ChainNext<WebAsking>> HopAsync(WebAsking asking, CancellationToken cancellationToken)
        {
            var current = asking.Address;

            if (!WebAddresses.IsWeb(current, out var uri))
            {
                return End(WebFetched.Refused(current, $"'{current}' is no http or https address, and this tool's fetch asks no other"));
            }

            // A key sent as a query parameter is the fetch's alone: a parameter of its name - a redirect that echoes it back -
            // is dropped from every address before it is decided, so none is recorded, answered, sent elsewhere or sent twice.
            // The address first asked naming one of its own would have what it says replaced by the key: it is refused.
            if (ask.Key is { Query: true } parameter)
            {
                var without = WithoutParameter(uri, parameter.Name);

                if (asking.First && without.AbsoluteUri != uri.AbsoluteUri)
                {
                    return End(WebFetched.Denied(uri.AbsoluteUri, KeyDenial($"'{uri.AbsoluteUri}' names '{parameter.Name}', the parameter its key is sent as")));
                }

                uri = without;
            }

            // The address decided, recorded and answered is the one asked, as it was read: a text a reader reads its own
            // way ('http:/\host') is decided as the host it reaches, never as the text's own spelling.
            current = uri.AbsoluteUri;

            // A source's key goes where its source's configuration says alone - the scheme, host and port its address writes,
            // or a gh login's host and its API - never to one a document the source fetched names, first asked or not; of
            // those, to the one first asked alone, never on a redirect to another; and never in the clear: a fetch that
            // carries one begins at an https address of its own, else nothing is sent.
            if (_keyedTo is null && ask.Key is not null && uri.Scheme != Uri.UriSchemeHttps)
            {
                return End(WebFetched.Denied(current, KeyDenial("a key is sent over https alone, and this address is no https one")));
            }

            if (_keyedTo is null && ask.Key is { Origins: var origins } && uri.GetLeftPart(UriPartial.Authority) is var origin && !origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                return End(WebFetched.Denied(current, KeyDenial(origins.Count == 0
                    ? $"its key is sent to no scheme, host and port, for its source's configuration writes none of its own, and a host a document it read names is never sent one: this address is {origin}'s"
                    : $"its key is sent to {string.Join(" or ", origins)} alone, and this address is {origin}'s")));
            }

            _keyedTo ??= uri.GetLeftPart(UriPartial.Authority);

            if ((await gate.Decide(current).ConfigureAwait(false)).Refusal() is { } denied)
            {
                return End(WebFetched.Denied(current, denied));
            }

            // Its turn, once it is allowed: a turn refused is no request, so nothing is answered for it. Whatever the pace
            // took - all of it, or part before it failed - is the gate's to let go once the fetch ends.
            if (gate.Pace is { } pace)
            {
                _turned = true;

                if (await pace(cancellationToken).ConfigureAwait(false) is { } until)
                {
                    return End(WebFetched.Held(current, until));
                }
            }

            var told = await AskWatchedAsync(uri, current, asking, cancellationToken).ConfigureAwait(false);

            await gate.Answered(told).ConfigureAwait(false);

            if (told.Failure is { } failure)
            {
                return End(WebFetched.Refused(current, failure) with { Status = told.Status, Headers = told.Headers });
            }

            if (told.Location is { } next)
            {
                // As browsers follow one: a 303, and a 301 or 302 of what is no GET, asks the next address with a GET and no
                // body; a 307 and a 308 ask it as the first was asked.
                return ChainNext<WebAsking>.To(told.Status is 303 || (told.Status is 301 or 302 && asking.Method != "GET")
                    ? new WebAsking(next, "GET", null, First: false)
                    : asking with { Address = next, First = false });
            }

            return End(await ReadAsync(told, current, cancellationToken).ConfigureAwait(false));
        }

        /// <summary>The chain's end, with what the fetch ended with.</summary>
        private ChainNext<WebAsking> End(WebFetched ended)
        {
            Ended = ended;
            return ChainNext<WebAsking>.End;
        }

        /// <summary>
        /// Asks one address under the wait on its silence (<see cref="Waits.WhileMovingAsync"/>): for as long as its answer
        /// moves, told every five minutes, ended where it falls silent for the reader's stall bound, or at the caller's stop.
        /// What it came to, its silence's end said as a failure.
        /// </summary>
        /// <exception cref="OperationCanceledException">The caller's stop ended it.</exception>
        private async Task<WebHop> AskWatchedAsync(Uri uri, string current, WebAsking asking, CancellationToken cancellationToken)
        {
            var clock = fetch._clock;
            var movedAt = clock.GetTimestamp();
            var bytes = 0L;
            var keyed = ask.Key is not null && uri.GetLeftPart(UriPartial.Authority) == _keyedTo;
            var stall = gate.Stall is { } given && given > TimeSpan.Zero ? given : (TimeSpan?)null;

            void Moved(int read)
            {
                Interlocked.Add(ref bytes, read);
                Interlocked.Exchange(ref movedAt, clock.GetTimestamp());
            }

            WaitMoved Stands()
            {
                var read = Interlocked.Read(ref bytes);
                return new WaitMoved(Interlocked.Read(ref movedAt), read == 0 ? "nothing has come yet" : $"{read.ToString("N0", CultureInfo.InvariantCulture)} bytes so far");
            }

            WaitEnd<WebHop> waited;

            try
            {
                waited = await Waits.WhileMovingAsync(
                    token => fetch.AskAsync(uri, ask, asking.Method, asking.Body, keyed, Moved, stall, token, cancellationToken),
                    Stands,
                    new WaitWatch($"'{current}' to answer", gate.Tell ?? (_ => { }), stall, clock),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // What no answer here foresaw still leaves the address its line, for the request may have left the machine;
                // the defect is raised all the same - beside what failed recording it, which never hides it.
                try
                {
                    await gate.Answered(WebHop.Failed(current, ex.Message)).ConfigureAwait(false);
                }
                catch (Exception recording)
                {
                    throw new AggregateException(ex, recording);
                }

                throw;
            }

            return waited.Kind switch
            {
                WaitEndKind.Done => waited.Value!,
                WaitEndKind.Bound => WebHop.Failed(current, Silent(stall!.Value)),
                _ => throw new OperationCanceledException(cancellationToken),
            };
        }

        /// <summary>
        /// The fetch's end, of an address read: its text as asked - a page's markup reduced to its text or read into its parts,
        /// waited for as long as the reading takes, told every five minutes - or as it came.
        /// </summary>
        private async Task<WebFetched> ReadAsync(WebHop told, string current, CancellationToken cancellationToken)
        {
            var ended = new WebFetched(current, told.Status, told.Type, told.Text, Failure: null, Denial: null) { Headers = told.Headers, Written = told.Written };

            if (!IsHtml(told.Type) || ask.Html == WebFetchHtml.Markup || told.Text is not { } markup)
            {
                return ended;
            }

            var watch = new WaitWatch($"'{current}''s page to be read", gate.Tell ?? (_ => { }), null, fetch._clock);

            if (ask.Html == WebFetchHtml.Parts)
            {
                var parts = await Waits.ForAsync(token => WebPageText.PartsAsync(markup, token), watch, cancellationToken).ConfigureAwait(false);
                return parts.Kind == WaitEndKind.Done ? ended with { Text = parts.Value!.Text, Parts = parts.Value } : throw new OperationCanceledException(cancellationToken);
            }

            if (ask.Into is { } into)
            {
                var written = await Waits.ForAsync(token => WebPageText.WriteAsync(markup, into, token), watch, cancellationToken).ConfigureAwait(false);
                return written.Kind == WaitEndKind.Done ? ended with { Text = null, Written = written.Value } : throw new OperationCanceledException(cancellationToken);
            }

            var text = await Waits.ForAsync(token => WebPageText.OfAsync(markup, token), watch, cancellationToken).ConfigureAwait(false);
            return text.Kind == WaitEndKind.Done ? ended with { Text = text.Value } : throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// A body as the fetch reads it: what was read ahead of it first - a gzip's first two bytes - then the body itself, each
    /// read told to whoever watches it.
    /// </summary>
    private sealed class FetchStream(Stream inner, ReadOnlyMemory<byte> ahead, Action<int>? read) : Stream
    {
        private ReadOnlyMemory<byte> _ahead = ahead;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Ahead(buffer) is var given and > 0)
            {
                return given;
            }

            var count = inner.Read(buffer);
            read?.Invoke(count);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Ahead(buffer.Span) is var given and > 0)
            {
                return given;
            }

            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            read?.Invoke(count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>Gives what was read ahead first, as much as <paramref name="buffer"/> holds; how much.</summary>
        private int Ahead(Span<byte> buffer)
        {
            if (_ahead.IsEmpty || buffer.IsEmpty)
            {
                return 0;
            }

            var given = Math.Min(_ahead.Length, buffer.Length);
            _ahead.Span[..given].CopyTo(buffer);
            _ahead = _ahead[given..];
            return given;
        }
    }
}

/// <summary>What one address of a fetch was answered: its status, its type, its size, and its text, a redirect's next address, or why it failed.</summary>
/// <param name="Address">The address asked.</param>
/// <param name="Status">The status the server answered; null where none was read.</param>
/// <param name="Type">The type of what came back; null where none was said or read.</param>
/// <param name="Bytes">How many bytes came back; null where none were read.</param>
/// <param name="Text">The text read, as the fetch was asked to read it (<see cref="WebFetchAsk"/>); null but where it was read and held.</param>
/// <param name="Location">The address a redirect named; null for any other answer.</param>
/// <param name="Failure">Why it failed; null where it did not.</param>
public sealed record WebHop(string Address, int? Status, string? Type, long? Bytes, string? Text, string? Location, string? Failure)
{
    /// <summary>The headers it was answered with, by name in any case; none where nothing was answered.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = WebFetchAsk.NoHeaders;

    /// <summary>The parts of the page it read, where its parts were asked (<see cref="WebFetchHtml.Parts"/>) and it was HTML; null otherwise.</summary>
    public WebPageParts? Parts { get; init; }

    /// <summary>How many characters of its text were written into the ask's writer (<see cref="WebFetchAsk.Into"/>), where it was; null otherwise.</summary>
    public long? Written { get; init; }

    /// <summary>An address read: its text, or none held where it was written into the ask's writer.</summary>
    internal static WebHop Read(string address, int status, string? type, long bytes, string? text) => new(address, status, type, bytes, text, null, null);

    /// <summary>An address that redirected to <paramref name="location"/>.</summary>
    internal static WebHop Redirected(string address, int status, string location) => new(address, status, null, null, null, location, null);

    /// <summary>An address that failed, for <paramref name="why"/>.</summary>
    internal static WebHop Failed(string address, string why, int? status = null, string? type = null, long? bytes = null) => new(address, status, type, bytes, null, null, why);
}

/// <summary>What a fetch ended with: the text it read, why it read none, the denial that stopped it, or the turn it was refused.</summary>
/// <param name="Address">The address it ended at, its redirects followed.</param>
/// <param name="Status">The status the last address answered; null where none answered.</param>
/// <param name="Type">The type of what it read; null where it read nothing.</param>
/// <param name="Text">The text read, as the fetch was asked to read it; null where none was read, or it was written into the ask's writer.</param>
/// <param name="Failure">Why none was read; null where one was, or the policy denied it.</param>
/// <param name="Denial">The denial that stopped it before an address was asked; null where none did.</param>
public sealed record WebFetched(string Address, int? Status, string? Type, string? Text, string? Failure, AgentPolicyAnswer.Denied? Denial)
{
    /// <summary>The headers its last address was answered with, by name in any case; none where nothing was answered.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = WebFetchAsk.NoHeaders;

    /// <summary>When its source may be asked again, where the reader's pace refused an address its turn; null where none did.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>The parts of the page it read, where its parts were asked (<see cref="WebFetchHtml.Parts"/>) and it was HTML; null otherwise.</summary>
    public WebPageParts? Parts { get; init; }

    /// <summary>How many characters of its text were written into the ask's writer (<see cref="WebFetchAsk.Into"/>), where it read one so; null otherwise.</summary>
    public long? Written { get; init; }

    /// <summary>A fetch whose address the reader's pace refused its turn until <paramref name="until"/>: nothing was sent.</summary>
    internal static WebFetched Held(string address, DateTimeOffset until)
        => new(address, null, null, null, $"its turn is spent until {WebRateLimit.Said(until)}, and nothing was sent", null) { Until = until };

    /// <summary>A fetch that read nothing, for <paramref name="why"/>.</summary>
    internal static WebFetched Refused(string address, string why) => new(address, null, null, null, why, null);

    /// <summary>A fetch the policy stopped at <paramref name="address"/>.</summary>
    internal static WebFetched Denied(string address, AgentPolicyAnswer.Denied denied) => new(address, null, null, null, null, denied);
}

/// <summary>
/// What decides, records and paces each address a reader of the web asks through the harness's fetch: the policy's
/// <c>Web(...)</c> rules and the mask before an address is asked, the record of what it was answered, and - for a reader
/// that asks a source many times, as a site's index is filled and a search's sources are asked - the source's turn on
/// this machine, which the fetch waits for after the decision and before each request, a redirect's too; and how long an
/// answer may fall silent, and where that silence is told.
/// </summary>
/// <param name="Decide">Decides an address before it is asked: a denial and nothing is sent.</param>
/// <param name="Answered">Told what each address was answered, or why it failed.</param>
public sealed record WebFetchGate(Func<string, Task<AgentPolicyAnswer>> Decide, Func<WebHop, Task> Answered)
{
    /// <summary>A gate that allows every address, records none and paces nothing: for what a test reads alone, never a reader's.</summary>
    internal static WebFetchGate Open { get; } = new(_ => Task.FromResult(AgentPolicyAnswer.Allow), _ => Task.CompletedTask);

    /// <summary>
    /// Waits for the source's turn on this machine before each request, and answers when it may be asked again where its
    /// pace is spent for longer than a wait - its day's - or <see langword="null"/> to ask now; none where the reader asks
    /// once, as an agent's page is read.
    /// </summary>
    public Func<CancellationToken, Task<DateTimeOffset?>>? Pace { get; init; }

    /// <summary>
    /// Told once the fetch has ended, however it ended, where its pace gave it a turn: what lets go a turn the source's pace
    /// holds for a whole fetch - a source asked one request at a time; none where the pace holds nothing past a request.
    /// </summary>
    public Func<Task>? Ended { get; init; }

    /// <summary>
    /// How long an answer may fall silent - nothing of its head or its body coming - before it is given up: the reader's
    /// stall bound, its step's <c>stallSeconds</c>; <see langword="null"/> where it has none, and an answer is waited for
    /// for as long as the reader's stop lets it. An answer that goes on coming, however slowly, is read to its end.
    /// </summary>
    public TimeSpan? Stall { get; init; }

    /// <summary>Told where a wait on an answer stands, every five minutes it lasts (<see cref="Waits.SaidEvery"/>); none where nothing is told.</summary>
    public Action<WaitTold>? Tell { get; init; }
}

/// <summary>What a fetch reads and how: a page as an agent reads it, or what a reader of its own takes - markup, a page's parts, a gzip.</summary>
public sealed record WebFetchAsk
{
    /// <summary>A page as an agent reads it: its HTML reduced to its text, no gzip read.</summary>
    public static WebFetchAsk Page { get; } = new();

    /// <summary>No headers: what an address that was never answered was answered with.</summary>
    public static IReadOnlyDictionary<string, string> NoHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How a page's HTML is read: reduced to its text, as an agent reads it, where none is said.</summary>
    public WebFetchHtml Html { get; init; }

    /// <summary>
    /// Whether a gzip of text - its type <c>application/gzip</c>, or none said under a name ending in <c>.gz</c>, as a
    /// sitemap's - is read as the UTF-8 text it holds.
    /// </summary>
    public bool Gzip { get; init; }

    /// <summary>The types the request asks for (<c>Accept</c>); a page's where none is said.</summary>
    public string? Accept { get; init; }

    /// <summary>
    /// Whether an answer is read as text whatever type it says it is, or where it says none: a file that is text by its
    /// name - a robots.txt (RFC 9309, 2.3), a sitemap - whose server may call it anything, read by a reader of its own.
    /// </summary>
    public bool AnyType { get; init; }

    /// <summary>
    /// The most bytes the asker's own protocol lets its answer be - once decompressed, where it is a gzip - as a sitemap's
    /// 50 MiB is the sitemaps protocol's: an answer past it is refused, said with <see cref="MostSaid"/>. <see langword="null"/>
    /// where the asker's protocol bounds none, and the answer is read whole.
    /// </summary>
    public long? Most { get; init; }

    /// <summary>What a refusal says of <see cref="Most"/> after its size: whose word it is, as <c>the sitemaps protocol lets a sitemap be</c>.</summary>
    public string MostSaid { get; init; } = "its reader takes";

    /// <summary>
    /// Where the answer's text is written as it is read, rather than held as one text: what keeps a page an agent fetches
    /// whole - as large as it is - as it comes. A page whose HTML is reduced to its text is read whole as markup, its reader
    /// needing it whole, and its text written here line by line. <see langword="null"/> where the text is held and given.
    /// </summary>
    public TextWriter? Into { get; init; }

    /// <summary>The request's method: <c>GET</c> where none is said, or <c>POST</c> with <see cref="Body"/> - a search source's request.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>A <c>POST</c>'s body, sent as JSON; <see langword="null"/> for a <c>GET</c>.</summary>
    public string? Body { get; init; }

    /// <summary>
    /// The headers the asker sends beside the fetch's own <c>User-Agent</c> - a search source's, its <c>Accept</c> among
    /// them - by name in any case; none that the request itself says, and never a key, which is <see cref="Key"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// A search source's key, sent to the scheme, host and port first asked alone - one of its
    /// <see cref="WebFetchKey.Origins"/>, never on a redirect to another, and never by a page an agent fetches, which carries
    /// none - as a header or a query parameter added to what is sent, never to the address decided, recorded or answered: a
    /// query parameter of its name is dropped from every address, and a first address that names one is refused. A fetch
    /// that carries one begins at an https address of its origins, else nothing is sent: a key never travels in the clear,
    /// nor to a host a document its source fetched names.
    /// </summary>
    public WebFetchKey? Key { get; init; }
}

/// <summary>A key a search source is sent: the header or query parameter it is sent as, its value, which no text made of it says, and where alone it goes.</summary>
/// <param name="Name">The header's name, or the query parameter's.</param>
/// <param name="Value">What is sent, the key in it.</param>
/// <param name="Query">Whether it is a query parameter rather than a header.</param>
public sealed record WebFetchKey(string Name, string Value, bool Query = false)
{
    /// <summary>
    /// The origins alone it is sent to, the first address one of them - each a scheme, host and port as an address's
    /// authority reads (<see cref="Uri.GetLeftPart"/>), as <c>https://api.example.com</c>: its source's own address's, as
    /// its configuration writes it, or a gh login's host and that host's API. None where the configuration writes none of
    /// its own, and the key goes nowhere; never one a document the source fetched names.
    /// </summary>
    public required IReadOnlyList<string> Origins { get; init; }

    /// <summary>The key by where it is sent alone, so no text made of it holds the value.</summary>
    public override string ToString() => Query ? $"?{Name}" : Name;
}

/// <summary>How a fetch reads an answer that is HTML.</summary>
public enum WebFetchHtml
{
    /// <summary>Reduced to its text, as an agent reads a page (<see cref="WebPageText.OfAsync"/>).</summary>
    Text,

    /// <summary>Kept as its markup, for a reader that reads it itself: a robots.txt or a sitemap said to be HTML.</summary>
    Markup,

    /// <summary>Read into the parts a site's index keeps (<see cref="WebPageText.PartsAsync"/>), its text among them.</summary>
    Parts,
}
