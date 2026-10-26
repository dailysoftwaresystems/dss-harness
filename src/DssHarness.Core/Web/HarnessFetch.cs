using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Hosts;

namespace DssHarness.Core.Web;

/// <summary>
/// The harness's own fetch, which makes every request a reader of the web sends - every page an agent reads, every
/// request a site's index is filled by: <c>http</c> and <c>https</c> alone; each address - the first and each redirect,
/// at most <see cref="MostRedirects"/> - decided before anything is sent, then given its turn by the reader's pace, where
/// it has one (<see cref="WebFetchGate"/>); a host that resolves to any address no public host holds refused, and a
/// connection made only to an address that was checked, and checked again once made; <see cref="Bound"/> for the asking
/// of them all, a decision and a turn waited for apart; <see cref="MostBytes"/> at most; text alone - <c>text/*</c>, JSON
/// and XML, and a gzip of text where the reader asks - read as the reader asks (<see cref="WebFetchAsk"/>): a page's HTML
/// reduced to its text, read into its parts or kept as its markup, within the bound; no cookie, no proxy, nothing of the
/// tool's environment and no credential of the machine's sent, under a fixed <c>User-Agent</c> that names the tool - a
/// search source's own key alone where its asker gives one (<see cref="WebFetchAsk.Key"/>), to the scheme, host and port
/// first asked.
/// </summary>
public sealed class HarnessFetch : IDisposable
{
    /// <summary>The most redirects one fetch follows: one more is refused.</summary>
    public const int MostRedirects = 5;

    /// <summary>The rule an address a search source's key may not be sent to is refused by (<see cref="WebFetchAsk.Key"/>): https alone, its hosts alone, and no parameter of its own name.</summary>
    public const string KeyRule = "the source's key";

    /// <summary>The most bytes a page may hold: more is refused, read no further.</summary>
    public const long MostBytes = 5 * 1024 * 1024;

    /// <summary>How long the asking of one fetch may take, its redirects and its reading among it: a decision or a turn waited for is not.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>The types of what a fetch reads, beside every <c>text/</c> one and every <c>+json</c> and <c>+xml</c> one.</summary>
    public static IReadOnlyList<string> TextTypes { get; } = ["application/json", "application/xml", "application/xhtml+xml", "application/javascript", "application/ecmascript"];

    /// <summary>Every type a fetch reads, as help, a tool's description and a refusal say them.</summary>
    public static string TextTypesSaid => $"text/*, {string.Join(", ", TextTypes)} and every +json and +xml type";

    private readonly IWebNames _names;
    private readonly TimeSpan _bound;
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
    /// <param name="bound">How long one fetch may take: <see cref="Bound"/> where none is given, a test's shorter.</param>
    internal HarnessFetch(IToolIdentityProvider identity, IWebNames names, HttpMessageHandler? handler, TimeSpan? bound = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(names);

        _names = names;
        _bound = bound ?? Bound;
        _userAgent = $"{ToolPackage.Command}/{identity.Current.Version} (+{ToolPackage.ProjectUrl})";
        _client = new(() => new HttpMessageInvoker(handler ?? Connecting(names), disposeHandler: true), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The handler the fetch sends through on this machine's own network: no redirect followed by itself, no cookie, no
    /// credential, no proxy, and each connection made only to an address <paramref name="names"/> gives that is public -
    /// every one the host resolves to checked, so none is reached by a lookup's second answer - and checked again as the
    /// address the connection was in fact made to.
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
            ConnectTimeout = Bound,
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
    /// Fetches <paramref name="address"/> as <paramref name="ask"/> says - a page's parts read or its markup kept, a gzip of text read - each
    /// address decided by <paramref name="gate"/> first and each answer told to it, as
    /// <see cref="FetchAsync(string, WebFetchGate, CancellationToken)"/> does.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="ask">What is read, and how.</param>
    /// <param name="gate">
    /// Decides each address before it is asked - a denial ends the fetch, nothing sent - gives it its turn, and is told what
    /// each was answered.
    /// </param>
    /// <param name="cancellationToken">Stops the fetch.</param>
    public async Task<WebFetched> FetchAsync(string address, WebFetchAsk ask, WebFetchGate gate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(gate);

        // The bound is the asking's alone: a decision and a turn are waited for apart, so a slow pace never takes a
        // request's time.
        var left = _bound;
        var current = address;
        var method = ask.Method;
        var body = ask.Body;
        string? keyedTo = null;

        for (var hop = 0; ; hop++)
        {
            if (!WebAddresses.IsWeb(current, out var uri))
            {
                return WebFetched.Refused(current, $"'{current}' is no http or https address, and this tool's fetch asks no other");
            }

            // A key sent as a query parameter is the fetch's alone: a parameter of its name - a redirect that echoes it back -
            // is dropped from every address before it is decided, so none is recorded, answered, sent elsewhere or sent twice.
            // The address first asked naming one of its own would have what it says replaced by the key: it is refused.
            if (ask.Key is { Query: true } parameter)
            {
                var without = WithoutParameter(uri, parameter.Name);

                if (hop == 0 && without.AbsoluteUri != uri.AbsoluteUri)
                {
                    return WebFetched.Denied(uri.AbsoluteUri, KeyDenial($"'{uri.AbsoluteUri}' names '{parameter.Name}', the parameter its key is sent as"));
                }

                uri = without;
            }

            // The address decided, recorded and answered is the one asked, as it was read: a text a reader reads its own
            // way ('http:/\host') is decided as the host it reaches, never as the text's own spelling.
            current = uri.AbsoluteUri;

            // A source's key goes to the scheme, host and port first asked alone: never on a redirect to another, and never
            // in the clear - a fetch that carries one begins at an https address, else nothing is sent.
            if (keyedTo is null && ask.Key is not null && uri.Scheme != Uri.UriSchemeHttps)
            {
                return WebFetched.Denied(current, KeyDenial("a key is sent over https alone, and this address is no https one"));
            }

            // A key that is some hosts' alone - a gh login, its host's and its API's - goes to no other, first asked or not.
            if (keyedTo is null && ask.Key is { Hosts: { } hosts } && !hosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase))
            {
                return WebFetched.Denied(current, KeyDenial($"its key is sent to {string.Join(" or ", hosts)} alone, and this address is {uri.IdnHost}'s"));
            }

            keyedTo ??= uri.GetLeftPart(UriPartial.Authority);

            if (hop > MostRedirects)
            {
                return WebFetched.Refused(current, $"'{address}' redirects more than {MostRedirects} times, and no more are followed");
            }

            if (await gate.Decide(current).ConfigureAwait(false) is AgentPolicyAnswer.Denied denied)
            {
                return WebFetched.Denied(current, denied);
            }

            // Its turn, once it is allowed: a turn refused is no request, so nothing is answered for it.
            if (gate.Pace is { } pace && await pace(cancellationToken).ConfigureAwait(false) is { } until)
            {
                return WebFetched.Held(current, until);
            }

            WebHop told;
            var asked = Stopwatch.GetTimestamp();

            using (var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                bound.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);

                try
                {
                    told = await AskAsync(uri, ask, method, body, ask.Key is not null && uri.GetLeftPart(UriPartial.Authority) == keyedTo, bound.Token, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (bound.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    told = WebHop.Failed(current, Unanswered);
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
            }

            left -= Stopwatch.GetElapsedTime(asked);

            await gate.Answered(told).ConfigureAwait(false);

            if (told.Failure is { } failure)
            {
                return WebFetched.Refused(current, failure) with { Status = told.Status, Headers = told.Headers };
            }

            if (told.Location is { } next)
            {
                // As browsers follow one: a 303, and a 301 or 302 of what is no GET, asks the next address with a GET and no
                // body; a 307 and a 308 ask it as the first was asked.
                if (told.Status is 303 || (told.Status is 301 or 302 && method != "GET"))
                {
                    (method, body) = ("GET", null);
                }

                current = next;
                continue;
            }

            return new WebFetched(current, told.Status, told.Type, told.Text, Failure: null, Denial: null) { Headers = told.Headers, Parts = told.Parts };
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

    /// <summary>Why an asking its bound ran out on read no answer.</summary>
    private string Unanswered => $"no answer read within {_bound.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s";

    /// <summary>
    /// Asks one address as <paramref name="method"/> with <paramref name="body"/> - the key the ask carries where <paramref name="keyed"/> - and answers its answer, a redirect's next address, or why it failed;
    /// <paramref name="cancellationToken"/> is the asking's bound, the caller's stop among it, and <paramref name="caller"/> the caller's stop alone.
    /// </summary>
    private async Task<WebHop> AskAsync(Uri uri, WebFetchAsk ask, string method, string? body, bool keyed, CancellationToken cancellationToken, CancellationToken caller)
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
            var headers = HeadersOf(response);

            try
            {
                return await AnsweredAsync(said, uri, ask, response, cancellationToken).ConfigureAwait(false) with { Headers = headers };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !caller.IsCancellationRequested)
            {
                // The bound ran out on the body: the status and headers already read are kept, so a pause they ask for holds.
                return WebHop.Failed(said, Unanswered, (int)response.StatusCode, response.Content.Headers.ContentType?.MediaType) with { Headers = headers };
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

    /// <summary>Reads one answer: a redirect's next address, its text, or why it is none.</summary>
    private static async Task<WebHop> AnsweredAsync(string said, Uri uri, WebFetchAsk ask, HttpResponseMessage response, CancellationToken cancellationToken)
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

        var most = ask.Most ?? MostBytes;

        // Refused unread: the size is the answer's word for it, said in why, and no byte came back - unless its head is asked.
        if (!ask.Head && response.Content.Headers.ContentLength is { } length && length > most)
        {
            return WebHop.Failed(said, $"{length.ToString("N0", CultureInfo.InvariantCulture)} bytes, past the {FileSystem.DiskSpace.Size(most)} this tool's fetch reads", status, type);
        }

        if (!(ask.Gzip && IsGzip(type, uri)) && !ask.AnyType && !IsText(type))
        {
            return WebHop.Failed(said, $"its type is {(type is null ? "not said" : $"'{type}'")}, and this tool's fetch reads text alone: {TextTypesSaid}", status, type);
        }

        byte[] bytes;
        bool over;
        var gzip = false;

        try
        {
            (bytes, over) = await ReadAsync(response.Content, most, cancellationToken).ConfigureAwait(false);

            // A gzip is known by its bytes, never by what it is said to be: one said to be bytes or XML is a gzip all the
            // same, and one the transport already decompressed, still said to be a gzip, is the text it holds.
            gzip = ask.Gzip && !over && bytes is [0x1F, 0x8B, ..];

            if (gzip)
            {
                (bytes, over) = await GunzipAsync(bytes, most, cancellationToken).ConfigureAwait(false);
            }

            if (over && ask.Head)
            {
                (bytes, over) = (bytes[..(int)most], false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException)
        {
            // A body that ends early, or that its encoding does not decode: gzip said of a plain page raises
            // InvalidDataException, a broken brotli InvalidOperationException, as the runtime's decoders do.
            return WebHop.Failed(said, $"its body could not be read: {ex.Message.TrimEnd('.')}", status, type);
        }

        if (over)
        {
            return WebHop.Failed(said, $"more than the {FileSystem.DiskSpace.Size(most)} this tool's fetch reads{(gzip ? " once decompressed" : string.Empty)}", status, type, bytes.Length);
        }

        var text = Decoded(bytes, gzip ? null : response.Content.Headers.ContentType?.CharSet);
        var read = WebHop.Read(said, status, type ?? (gzip ? "application/gzip" : null), bytes.Length, text);

        // A page's markup is read within the bound, as its bytes are: markup nested deep makes the reading long.
        if (!IsHtml(type) || ask.Html == WebFetchHtml.Markup)
        {
            return read;
        }

        if (ask.Html == WebFetchHtml.Parts)
        {
            var parts = await WebPageText.PartsAsync(text, cancellationToken).ConfigureAwait(false);
            return read with { Text = parts.Text, Parts = parts };
        }

        return read with { Text = await WebPageText.OfAsync(text, cancellationToken).ConfigureAwait(false) };
    }

    /// <summary>Whether an answer of <paramref name="type"/> from <paramref name="uri"/> is a gzip: said so, or bytes of no type said under a name ending in <c>.gz</c>.</summary>
    private static bool IsGzip(string? type, Uri uri)
        => type is not null && (type.Equals("application/gzip", StringComparison.OrdinalIgnoreCase) || type.Equals("application/x-gzip", StringComparison.OrdinalIgnoreCase))
            || ((type is null || type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)) && uri.AbsolutePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase));

    /// <summary>What the gzip <paramref name="bytes"/> holds, read no further than one byte past <paramref name="most"/>, and whether it held more.</summary>
    /// <exception cref="InvalidDataException">The bytes are no gzip.</exception>
    private static async Task<(byte[] Bytes, bool Over)> GunzipAsync(byte[] bytes, long most, CancellationToken cancellationToken)
    {
        await using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var held = new MemoryStream();
        var buffer = new byte[81920];

        while (await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is var read and > 0)
        {
            held.Write(buffer, 0, read);

            if (held.Length > most)
            {
                return (held.ToArray(), true);
            }
        }

        return (held.ToArray(), false);
    }

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

    /// <summary>What <paramref name="content"/> holds, read no further than one byte past <paramref name="most"/>, and whether it held more.</summary>
    private static async Task<(byte[] Bytes, bool Over)> ReadAsync(HttpContent content, long most, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var held = new MemoryStream();
        var buffer = new byte[81920];

        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is var read and > 0)
        {
            held.Write(buffer, 0, read);

            if (held.Length > most)
            {
                return (held.ToArray(), true);
            }
        }

        return (held.ToArray(), false);
    }

    /// <summary>
    /// The encoding <paramref name="name"/> names: one the runtime holds, or a code page's - windows-1252, shift_jis - which
    /// the runtime holds only through <see cref="CodePagesEncodingProvider"/>, asked here alone rather than for the process.
    /// </summary>
    /// <exception cref="ArgumentException">Neither knows it.</exception>
    private static Encoding Named(string name) => CodePagesEncodingProvider.Instance.GetEncoding(name) ?? Encoding.GetEncoding(name);

    /// <summary>The text of <paramref name="bytes"/> by the character set the answer names, by its mark, else as UTF-8 - a byte that forms no character read as U+FFFD, for a page is read, never written back.</summary>
    private static string Decoded(byte[] bytes, string? charset)
    {
        Encoding encoding;

        try
        {
            encoding = charset is { Length: > 0 } named ? Named(named.Trim('"', '\'')) : Encoding.UTF8;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // A set neither knows, or one the runtime holds and will not read - UTF-7, which it turned off.
            encoding = Encoding.UTF8;
        }

        using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
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
}

/// <summary>What one address of a fetch was answered: its status, its type, its size, and its text, a redirect's next address, or why it failed.</summary>
/// <param name="Address">The address asked.</param>
/// <param name="Status">The status the server answered; null where none was read.</param>
/// <param name="Type">The type of what came back; null where none was said or read.</param>
/// <param name="Bytes">How many bytes came back; null where none were read.</param>
/// <param name="Text">The text read, as the fetch was asked to read it (<see cref="WebFetchAsk"/>); null but where it was read.</param>
/// <param name="Location">The address a redirect named; null for any other answer.</param>
/// <param name="Failure">Why it failed; null where it did not.</param>
public sealed record WebHop(string Address, int? Status, string? Type, long? Bytes, string? Text, string? Location, string? Failure)
{
    /// <summary>The headers it was answered with, by name in any case; none where nothing was answered.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = WebFetchAsk.NoHeaders;

    /// <summary>The parts of the page it read, where its parts were asked (<see cref="WebFetchHtml.Parts"/>) and it was HTML; null otherwise.</summary>
    public WebPageParts? Parts { get; init; }

    /// <summary>An address read: its text.</summary>
    internal static WebHop Read(string address, int status, string? type, long bytes, string text) => new(address, status, type, bytes, text, null, null);

    /// <summary>An address that redirected to <paramref name="location"/>.</summary>
    internal static WebHop Redirected(string address, int status, string location) => new(address, status, null, null, null, location, null);

    /// <summary>An address that failed, for <paramref name="why"/>.</summary>
    internal static WebHop Failed(string address, string why, int? status = null, string? type = null, long? bytes = null) => new(address, status, type, bytes, null, null, why);
}

/// <summary>What a fetch ended with: the text it read, why it read none, the denial that stopped it, or the turn it was refused.</summary>
/// <param name="Address">The address it ended at, its redirects followed.</param>
/// <param name="Status">The status the last address answered; null where none answered.</param>
/// <param name="Type">The type of what it read; null where it read nothing.</param>
/// <param name="Text">The text read, as the fetch was asked to read it; null where none was read.</param>
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
/// this machine, which the fetch waits for after the decision and before each request, a redirect's too.
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
    /// sitemap's - is read as the UTF-8 text it holds, no more of it than the fetch reads of any answer.
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
    /// The most bytes read of the answer - once decompressed, where it is a gzip - where it is not the fetch's own
    /// <see cref="HarnessFetch.MostBytes"/>: a sitemap's, which its protocol lets be 50 MiB.
    /// </summary>
    public long? Most { get; init; }

    /// <summary>
    /// Whether an answer longer than the most read is kept to its head, the rest unread, rather than refused: a robots.txt,
    /// of which only the first 500 KiB are parsed (RFC 9309, 2.5), its reader leaving the line its cut ends in unread.
    /// </summary>
    public bool Head { get; init; }

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
    /// A search source's key, sent to the scheme, host and port first asked alone - never on a redirect to another, and
    /// never by a page an agent fetches, which carries none - as a header or a query parameter added to what is sent, never
    /// to the address decided, recorded or answered: a query parameter of its name is dropped from every address, and a
    /// first address that names one is refused. A fetch that carries one begins at an https address - one of its
    /// <see cref="WebFetchKey.Hosts"/>, where it names them - else nothing is sent: a key never travels in the clear.
    /// </summary>
    public WebFetchKey? Key { get; init; }
}

/// <summary>A key a search source is sent: the header or query parameter it is sent as, and its value, which no text made of it says.</summary>
/// <param name="Name">The header's name, or the query parameter's.</param>
/// <param name="Value">What is sent, the key in it.</param>
/// <param name="Query">Whether it is a query parameter rather than a header.</param>
public sealed record WebFetchKey(string Name, string Value, bool Query = false)
{
    /// <summary>The hosts alone it is sent to, the first address one of them - a gh login's, its host and that host's API; any where null.</summary>
    public IReadOnlyList<string>? Hosts { get; init; }

    /// <summary>The key by where it is sent alone, so no text made of it holds the value.</summary>
    public override string ToString() => Query ? $"?{Name}" : Name;
}

/// <summary>How a fetch reads an answer that is HTML - within its bound, as it reads the answer's bytes.</summary>
public enum WebFetchHtml
{
    /// <summary>Reduced to its text, as an agent reads a page (<see cref="WebPageText.OfAsync"/>).</summary>
    Text,

    /// <summary>Kept as its markup, for a reader that reads it itself: a robots.txt or a sitemap said to be HTML.</summary>
    Markup,

    /// <summary>Read into the parts a site's index keeps (<see cref="WebPageText.PartsAsync"/>), its text among them.</summary>
    Parts,
}
