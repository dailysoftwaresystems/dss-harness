using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Hosts;

namespace DssHarness.Core.Web;

/// <summary>
/// The harness's own fetch, which makes every page an agent reads: <c>http</c> and <c>https</c> alone; each address -
/// the first and each redirect, at most <see cref="MostRedirects"/> - decided before anything is sent; a host that resolves
/// to any address no public host holds refused, and a connection made only to an address that was checked, and checked
/// again once made; <see cref="Bound"/> for the whole; <see cref="MostBytes"/> at most; text alone - <c>text/*</c>, JSON and
/// XML - a page's HTML reduced to its text; no cookie, no credential, no proxy and nothing of the tool's environment sent,
/// under a fixed <c>User-Agent</c> that names the tool.
/// </summary>
public sealed class HarnessFetch : IDisposable
{
    /// <summary>The most redirects one fetch follows: one more is refused.</summary>
    public const int MostRedirects = 5;

    /// <summary>The most bytes a page may hold: more is refused, read no further.</summary>
    public const long MostBytes = 5 * 1024 * 1024;

    /// <summary>How long one fetch may take, its redirects and its reading among it.</summary>
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
    /// Fetches <paramref name="address"/>: each address - the first and each redirect - asked of <paramref name="decide"/>
    /// before anything is sent, and each answer told to <paramref name="answered"/>; the page's text, or why there is none.
    /// </summary>
    /// <param name="address">The address an agent asked for.</param>
    /// <param name="decide">Decides each address, before it is asked: a denial ends the fetch, nothing sent.</param>
    /// <param name="answered">Told what each address was answered, or why it failed, once it was asked.</param>
    /// <param name="cancellationToken">Stops the fetch.</param>
    public async Task<WebFetched> FetchAsync(
        string address,
        Func<string, Task<AgentPolicyAnswer>> decide,
        Func<WebHop, Task> answered,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(decide);
        ArgumentNullException.ThrowIfNull(answered);

        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(_bound);

        var current = address;

        for (var hop = 0; ; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                return WebFetched.Refused(current, $"'{current}' is no http or https address, and this tool's fetch asks no other");
            }

            // The address decided, recorded and answered is the one asked, as it was read: a text a reader reads its own
            // way ('http:/\host') is decided as the host it reaches, never as the text's own spelling.
            current = uri.AbsoluteUri;

            if (hop > MostRedirects)
            {
                return WebFetched.Refused(current, $"'{address}' redirects more than {MostRedirects} times, and no more are followed");
            }

            if (await decide(current).ConfigureAwait(false) is AgentPolicyAnswer.Denied denied)
            {
                return WebFetched.Denied(current, denied);
            }

            WebHop told;

            try
            {
                told = await AskAsync(uri, bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bound.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                told = WebHop.Failed(current, $"no answer read within {_bound.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // What no answer here foresaw still leaves the address its line, for the request may have left the machine;
                // the defect is raised all the same.
                await answered(WebHop.Failed(current, ex.Message)).ConfigureAwait(false);
                throw;
            }

            await answered(told).ConfigureAwait(false);

            if (told.Failure is { } failure)
            {
                return WebFetched.Refused(current, failure);
            }

            if (told.Location is { } next)
            {
                current = next;
                continue;
            }

            return new WebFetched(current, told.Status, told.Type, told.Text, Failure: null, Denial: null);
        }
    }

    /// <summary>Closes the connections the fetch holds.</summary>
    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    /// <summary>Asks one address: its answer, a redirect's next address, or why it failed.</summary>
    private async Task<WebHop> AskAsync(Uri uri, CancellationToken cancellationToken)
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

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        request.Headers.Accept.ParseAdd("text/html, text/plain;q=0.9, application/json;q=0.9, application/xml;q=0.8, text/*;q=0.7");

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

            // Refused unread: the size is the answer's word for it, said in why, and no byte came back.
            if (response.Content.Headers.ContentLength is > MostBytes and var length)
            {
                return WebHop.Failed(said, $"{length.ToString("N0", CultureInfo.InvariantCulture)} bytes, past the {FileSystem.DiskSpace.Size(MostBytes)} this tool's fetch reads", status, type);
            }

            if (!IsText(type))
            {
                return WebHop.Failed(said, $"its type is {(type is null ? "not said" : $"'{type}'")}, and this tool's fetch reads text alone: {TextTypesSaid}", status, type);
            }

            byte[] bytes;
            bool over;

            try
            {
                (bytes, over) = await ReadAsync(response.Content, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException)
            {
                // A body that ends early, or that its encoding does not decode: gzip said of a plain page raises
                // InvalidDataException, a broken brotli InvalidOperationException, as the runtime's decoders do.
                return WebHop.Failed(said, $"its body could not be read: {ex.Message.TrimEnd('.')}", status, type);
            }

            if (over)
            {
                return WebHop.Failed(said, $"more than the {FileSystem.DiskSpace.Size(MostBytes)} this tool's fetch reads", status, type, bytes.Length);
            }

            var text = Decoded(bytes, response.Content.Headers.ContentType?.CharSet);

            return WebHop.Read(said, status, type!, bytes.Length, IsHtml(type) ? await WebPageText.OfAsync(text, cancellationToken).ConfigureAwait(false) : text);
        }
    }

    /// <summary>Whether <paramref name="type"/> is one a fetch reads: every <c>text/</c> type, <see cref="TextTypes"/>, and every JSON and XML type.</summary>
    private static bool IsText(string? type)
        => type is not null
            && (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || TextTypes.Contains(type, StringComparer.OrdinalIgnoreCase)
                || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="type"/> is a page's markup, which is read as its text: HTML or XHTML, in any case.</summary>
    private static bool IsHtml(string? type)
        => string.Equals(type, "text/html", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>What <paramref name="content"/> holds, read no further than one byte past <see cref="MostBytes"/>, and whether it held more.</summary>
    private static async Task<(byte[] Bytes, bool Over)> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var held = new MemoryStream();
        var buffer = new byte[81920];

        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is var read and > 0)
        {
            held.Write(buffer, 0, read);

            if (held.Length > MostBytes)
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
            catch (SocketException ex)
            {
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
/// <param name="Text">The text read, a page's HTML reduced to its text; null but where it was read.</param>
/// <param name="Location">The address a redirect named; null for any other answer.</param>
/// <param name="Failure">Why it failed; null where it did not.</param>
public sealed record WebHop(string Address, int? Status, string? Type, long? Bytes, string? Text, string? Location, string? Failure)
{
    /// <summary>An address read: its text.</summary>
    internal static WebHop Read(string address, int status, string type, long bytes, string text) => new(address, status, type, bytes, text, null, null);

    /// <summary>An address that redirected to <paramref name="location"/>.</summary>
    internal static WebHop Redirected(string address, int status, string location) => new(address, status, null, null, null, location, null);

    /// <summary>An address that failed, for <paramref name="why"/>.</summary>
    internal static WebHop Failed(string address, string why, int? status = null, string? type = null, long? bytes = null) => new(address, status, type, bytes, null, null, why);
}

/// <summary>What a fetch ended with: the text of the page it read, why it read none, or the denial that stopped it.</summary>
/// <param name="Address">The address it ended at, its redirects followed.</param>
/// <param name="Status">The status the last address answered; null where none answered.</param>
/// <param name="Type">The type of what it read; null where it read nothing.</param>
/// <param name="Text">The page's text; null where none was read.</param>
/// <param name="Failure">Why none was read; null where one was, or the policy denied it.</param>
/// <param name="Denial">The denial that stopped it before an address was asked; null where none did.</param>
public sealed record WebFetched(string Address, int? Status, string? Type, string? Text, string? Failure, AgentPolicyAnswer.Denied? Denial)
{
    /// <summary>A fetch that read nothing, for <paramref name="why"/>.</summary>
    internal static WebFetched Refused(string address, string why) => new(address, null, null, null, why, null);

    /// <summary>A fetch the policy stopped at <paramref name="address"/>.</summary>
    internal static WebFetched Denied(string address, AgentPolicyAnswer.Denied denied) => new(address, null, null, null, null, denied);
}
