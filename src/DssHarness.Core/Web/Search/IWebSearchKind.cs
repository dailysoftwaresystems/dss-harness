using DssHarness.Core.Configuration;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// One kind of source of the harness's web search: how a source of it is asked a query and how its answer is read. Every
/// member is pure - nothing is sent, read or written by one; the harness's search sends what <see cref="Lookups"/> and
/// <see cref="Request"/> ask, under its policy and its pace, and hands back what was answered - so a kind is held whole
/// by tests over stored answers. Found by the one scan of the core (<see cref="WebSearchKinds.Implementations"/>): a new
/// kind is one class and its tests.
/// </summary>
public interface IWebSearchKind
{
    /// <summary>Its name, as a source's <c>kind</c> names it: <see cref="WebSearchSource.NameRule"/>.</summary>
    string Kind { get; }

    /// <summary>A positive integer, raised whenever what it asks or how it reads an answer changes; recorded with each search.</summary>
    int Version { get; }

    /// <summary>
    /// The keys a source of this kind takes beside every source's own (<see cref="WebSearchSource.OwnKeys"/>), each with
    /// whether it is required and what it is: what the configuration's reader holds a source to, and what help lists.
    /// </summary>
    IReadOnlyList<KeyDescription> Keys { get; }

    /// <summary>What help says of it: what it asks, and how it reads what is answered.</summary>
    string Help { get; }

    /// <summary>
    /// Why the values of <paramref name="source"/>'s keys of this kind cannot be used, each problem one line naming its key;
    /// empty where they can. A key missing or not this kind's is the reader's to refuse, by <see cref="Keys"/>.
    /// </summary>
    /// <param name="source">A source of this kind.</param>
    IReadOnlyList<string> Problems(WebSearchSource source);

    /// <summary>
    /// What is read before the source's first request - a document an address is discovered in, a description; none for
    /// most kinds - what it finds kept for the run, and one that found nothing asked again by the next query.
    /// </summary>
    /// <param name="source">A source of this kind, its keys held to <see cref="Problems"/>.</param>
    IReadOnlyList<WebSearchRequest.Http> Lookups(WebSearchSource source);

    /// <summary>What the answers to <see cref="Lookups"/> give, in their order, or why they give nothing.</summary>
    /// <param name="source">The source.</param>
    /// <param name="answers">The lookups' answers, in their order: one each, or those up to the first whose status failed, which ends them.</param>
    WebSearchFound Found(WebSearchSource source, IReadOnlyList<WebSearchResponse.Http> answers);

    /// <summary>
    /// The scheme, host and port of the address <paramref name="source"/>'s configuration writes for it, as an address's
    /// authority reads (<see cref="Uri.GetLeftPart"/>) - the one place a key of its own is sent, never a host a document it
    /// fetched names - or <see langword="null"/> where its configuration writes none of its own: a host a lookup finds.
    /// </summary>
    /// <param name="source">A source of this kind, its keys held to <see cref="Problems"/>.</param>
    string? KeyOrigin(WebSearchSource source);

    /// <summary>The request <paramref name="query"/> makes of <paramref name="source"/>, asking at most <paramref name="limit"/> results.</summary>
    /// <param name="source">The source.</param>
    /// <param name="found">What its lookups found.</param>
    /// <param name="query">The query, as the agent wrote it.</param>
    /// <param name="limit">The most results asked.</param>
    WebSearchRequest Request(WebSearchSource source, WebSearchFound found, string query, int limit);

    /// <summary>What <paramref name="response"/> answers: results, none, or why none - never an empty list for a failure.</summary>
    /// <param name="source">The source.</param>
    /// <param name="found">What its lookups found.</param>
    /// <param name="response">What its request was answered.</param>
    WebSearchAnswer Read(WebSearchSource source, WebSearchFound found, WebSearchResponse response);
}
