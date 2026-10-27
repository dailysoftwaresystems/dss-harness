using System.Net;
using System.Text;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>What a request carried, read as it was sent.</summary>
/// <param name="Address">The address asked.</param>
/// <param name="UserAgent">The User-Agent it named.</param>
/// <param name="Cookie">Whether it carried a cookie.</param>
/// <param name="Authorization">Whether it carried a credential.</param>
internal sealed record WebSent(Uri Address, string UserAgent, bool Cookie, bool Authorization)
{
    /// <summary>Its method.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>Its headers and its content's, by name in any case, each one's values joined.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Its body; <see langword="null"/> where it sent none.</summary>
    public string? Body { get; init; }
}

/// <summary>An endpoint a test answers in place of the network, which keeps what each request carried.</summary>
/// <param name="answer">What each request is answered.</param>
internal sealed class WebEndpoint(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    private readonly List<WebSent> _asked = [];
    private readonly Lock _gate = new();

    /// <summary>Every request, in the order sent.</summary>
    public IReadOnlyList<WebSent> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    /// <summary>A page of <paramref name="text"/>, of type <paramref name="type"/>, in UTF-8.</summary>
    public static HttpResponseMessage Page(string text, string type)
        => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, type) };

    /// <summary>A redirect to <paramref name="location"/>, whole or relative.</summary>
    public static HttpResponseMessage Redirect(string location)
        => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _asked.Add(new WebSent(
                request.RequestUri!,
                request.Headers.UserAgent.ToString(),
                request.Headers.Contains("Cookie"),
                request.Headers.Authorization is not null)
            {
                Method = request.Method.Method,
                Headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                    .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase),
                Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult(),
            });
        }

        var response = answer(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>A lookup of names a test answers.</summary>
/// <param name="resolve">The addresses each host resolves to.</param>
internal sealed class WebNamesDouble(Func<string, IReadOnlyList<IPAddress>> resolve) : IWebNames
{
    /// <summary>An address a public host holds.</summary>
    public static readonly IPAddress Public = IPAddress.Parse("93.184.215.14");

    /// <summary>A lookup that finds every host at <see cref="Public"/>.</summary>
    public static WebNamesDouble AllPublic { get; } = new(_ => [Public]);

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(resolve(host));
}
