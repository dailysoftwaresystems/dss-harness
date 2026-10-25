using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// The harness's own fetch, over an endpoint a test answers: each address decided before anything is sent, a host that
/// resolves to an address no public host holds refused, its redirects bounded, its size bounded, text alone read.
/// </summary>
public sealed class HarnessFetchTests
{
    private static readonly IPAddress Public = WebNamesDouble.Public;

    /// <summary>A page's HTML is read as its text, under the tool's own User-Agent, and no cookie or credential is sent.</summary>
    [Fact]
    public async Task APage_IsReadAsItsText_UnderTheToolsName()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("<html><head><title>T</title></head><body><p>Hello <b>there</b></p></body></html>", "text/html"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, hops) = await FetchAsync(fetch, "https://example.com/page");

        Assert.Null(fetched.Failure);
        Assert.Equal(200, fetched.Status);
        Assert.Equal("text/html", fetched.Type);
        Assert.Equal("T\n\nHello there", fetched.Text);
        Assert.Equal(["https://example.com/page"], decided);
        Assert.Equal("https://example.com/page", Assert.Single(hops).Address);

        var asked = Assert.Single(endpoint.Asked);
        Assert.Equal("dssharness/1.2.3 (+https://github.com/dailysoftwaresystems/dss-harness)", asked.UserAgent);
        Assert.False(asked.Cookie);
        Assert.False(asked.Authorization);
    }

    /// <summary>A page's markup is known by its type in any case, and read as its text.</summary>
    [Fact]
    public async Task APagesMarkup_IsKnownInAnyCase()
    {
        using var fetch = Fetch(new WebEndpoint(_ => WebEndpoint.Page("<p>Hello <script>run()</script>there</p>", "Text/HTML")));

        var (fetched, _, _) = await FetchAsync(fetch, "https://example.com/");

        Assert.Equal("Hello there", fetched.Text);
    }

    /// <summary>
    /// A body its encoding does not decode fails its address, said and recorded, and nothing is read: as gzip raises it,
    /// and as brotli does.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ABodyThatDoesNotDecode_FailsItsAddress(bool brotli)
    {
        using var fetch = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new Undecodable(brotli)) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } },
        }));

        var (fetched, _, hops) = await FetchAsync(fetch, "https://example.com/broken");

        Assert.Equal("its body could not be read: the stream's encoding does not decode", fetched.Failure);
        Assert.Equal((fetched.Failure, (int?)200), (Assert.Single(hops).Failure, hops[0].Status));
    }

    /// <summary>A character set the runtime will not read - UTF-7 - is read as UTF-8, as one it does not know is.</summary>
    [Fact]
    public async Task ACharacterSetTheRuntimeWillNotRead_IsReadAsUtf8()
    {
        using var fetch = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("caf\u00e9 ok"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-7" } } },
        }));

        Assert.Equal("caf\u00e9 ok", (await FetchAsync(fetch, "https://example.com/seven.txt")).Fetched.Text);
    }

    /// <summary>What no answer foresaw still leaves the address its line in the record, and is raised.</summary>
    [Fact]
    public async Task AFailureNoAnswerForesaw_IsRecorded_AndRaised()
    {
        using var fetch = Fetch(new WebEndpoint(_ => throw new InvalidCastException("a defect")));
        var hops = new List<WebHop>();

        await Assert.ThrowsAsync<InvalidCastException>(() => fetch.FetchAsync(
            "https://example.com/defect",
            new WebFetchGate(
                _ => Task.FromResult<AgentPolicyAnswer>(new AgentPolicyAnswer.Allowed()),
                hop =>
                {
                    hops.Add(hop);
                    return Task.CompletedTask;
                }),
            TestContext.Current.CancellationToken));

        Assert.Equal(("https://example.com/defect", "a defect"), (Assert.Single(hops).Address, hops[0].Failure));
    }

    /// <summary>The fetch's bound holds the reading of a page's markup too, which markup nested deep makes long - its parts' as its text's.</summary>
    [Theory]
    [InlineData(WebFetchHtml.Text)]
    [InlineData(WebFetchHtml.Parts)]
    public async Task APageThatReadsPastTheBound_IsNoAnswer(WebFetchHtml html)
    {
        var deep = string.Concat(Enumerable.Repeat("<div>", 50_000)) + "deep";
        using var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page(deep, "text/html")), TimeSpan.FromSeconds(1));
        var hops = new List<WebHop>();

        var fetched = await fetch.FetchAsync(
            "https://example.com/deep",
            new WebFetchAsk { Html = html },
            new WebFetchGate(_ => Task.FromResult(AgentPolicyAnswer.Allow), hop =>
            {
                hops.Add(hop);
                return Task.CompletedTask;
            }) { Pace = _ => Task.FromResult<DateTimeOffset?>(null) },
            TestContext.Current.CancellationToken);

        Assert.Equal("no answer read within 1 s", fetched.Failure);
        Assert.Equal(fetched.Failure, Assert.Single(hops).Failure);
    }

    /// <summary>The bound is the whole asking's: each hop's time is taken from what is left of it, a redirect's among it.</summary>
    [Fact]
    public async Task TheBound_IsSharedByEveryHop()
    {
        using var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(request =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(700));
            return request.RequestUri!.AbsolutePath == "/a" ? WebEndpoint.Redirect("/b") : WebEndpoint.Page("late", "text/plain");
        }), TimeSpan.FromSeconds(1));

        var fetched = await fetch.FetchAsync("https://example.com/a", WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Equal(("https://example.com/b", "no answer read within 1 s"), (fetched.Address, fetched.Failure));
    }

    /// <summary>Asked for its parts, a page is read into them - its text the parts' text - and anything else is read as asked without them.</summary>
    [Fact]
    public async Task AskedForParts_APageIsReadIntoThem()
    {
        using var fetch = Fetch(new WebEndpoint(request => request.RequestUri!.AbsolutePath == "/plain"
            ? WebEndpoint.Page("plain <b>text</b>", "text/plain")
            : WebEndpoint.Page("<html><head><title>T</title></head><body><h1>H</h1><p>Body</p></body></html>", "text/html")));

        var page = await fetch.FetchAsync("https://example.com/", new WebFetchAsk { Html = WebFetchHtml.Parts }, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var plain = await fetch.FetchAsync("https://example.com/plain", new WebFetchAsk { Html = WebFetchHtml.Parts }, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Equal(("T", "H", "H\nBody", "H\nBody"), (page.Parts!.Title, Assert.Single(page.Parts.Headings), page.Parts.Text, page.Text));
        Assert.Equal(("plain <b>text</b>", (WebPageParts?)null), (plain.Text, plain.Parts));
    }

    /// <summary>
    /// The bound is the asking's alone: a decision and a turn waited for, each hop's, are not charged to it, so a slow pace
    /// never takes a request's time.
    /// </summary>
    [Fact]
    public async Task ADecisionAndATurn_AreWaitedForOutsideTheBound()
    {
        using var fetch = new HarnessFetch(
            new RunningToolDouble("1.2.3"),
            WebNamesDouble.AllPublic,
            new WebEndpoint(request => request.RequestUri!.AbsolutePath == "/a" ? WebEndpoint.Redirect("/b") : WebEndpoint.Page("paced", "text/plain")),
            TimeSpan.FromSeconds(1));
        var gate = new WebFetchGate(
            async _ =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);
                return AgentPolicyAnswer.Allow;
            },
            _ => Task.CompletedTask)
        {
            Pace = async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
                return null;
            },
        };

        var fetched = await fetch.FetchAsync("https://example.com/a", gate, TestContext.Current.CancellationToken);

        Assert.Equal(("paced", (string?)null), (fetched.Text, fetched.Failure));
    }

    /// <summary>
    /// Each address - a redirect's too - is given its turn once it is allowed; a turn refused ends the fetch where it is,
    /// nothing sent for it and nothing answered, saying until when.
    /// </summary>
    [Fact]
    public async Task ATurnRefused_EndsTheFetch_NothingSent()
    {
        var endpoint = new WebEndpoint(request => request.RequestUri!.AbsolutePath == "/a" ? WebEndpoint.Redirect("/b") : WebEndpoint.Page("never", "text/plain"));
        using var fetch = Fetch(endpoint);
        var until = new DateTimeOffset(2026, 10, 25, 13, 0, 0, TimeSpan.Zero);
        var turns = new List<string>();
        var decided = new List<string>();
        var hops = new List<WebHop>();
        var gate = new WebFetchGate(
            address =>
            {
                decided.Add(address);
                return Task.FromResult(AgentPolicyAnswer.Allow);
            },
            hop =>
            {
                hops.Add(hop);
                return Task.CompletedTask;
            })
        {
            Pace = _ =>
            {
                turns.Add(decided[^1]);
                return Task.FromResult<DateTimeOffset?>(turns.Count == 2 ? until : null);
            },
        };

        var fetched = await fetch.FetchAsync("https://example.com/a", gate, TestContext.Current.CancellationToken);

        Assert.Equal(["https://example.com/a", "https://example.com/b"], turns);
        Assert.Equal((until, "https://example.com/b", (string?)null), (fetched.Until, fetched.Address, fetched.Text));
        Assert.Equal("its turn is spent until 2026-10-25 13:00:00 UTC, and nothing was sent", fetched.Failure);
        Assert.Equal(["/a"], endpoint.Asked.Select(sent => sent.Address.AbsolutePath));
        Assert.Equal("https://example.com/a", Assert.Single(hops).Address);
    }

    /// <summary>JSON is read as it stands, by the character set its answer names.</summary>
    [Fact]
    public async Task JsonAndACharacterSet_AreReadAsTheyStand()
    {
        var endpoint = new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([.. "{\"name\":\"caf"u8, 0xE9, .. "\"}"u8])
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "iso-8859-1" } },
            },
        });
        using var fetch = Fetch(endpoint);

        var (fetched, _, _) = await FetchAsync(fetch, "https://example.com/data.json");

        Assert.Equal("{\"name\":\"café\"}", fetched.Text);

        // A code page's characters are read in it: 0x80 is the euro in windows-1252, where Latin-1 holds a control there.
        using var codePage = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([.. "price "u8, 0x80, .. "5"u8])
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "windows-1252" } },
            },
        }));

        Assert.Equal("price €5", (await FetchAsync(codePage, "https://example.com/price.txt")).Fetched.Text);
    }

    /// <summary>Only http and https are asked, and nothing else is decided or sent.</summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/a")]
    [InlineData("not an address")]
    public async Task AnAddressOfAnotherScheme_IsRefused_NothingAsked(string address)
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, _) = await FetchAsync(fetch, address);

        Assert.Equal($"'{address}' is no http or https address, and this tool's fetch asks no other", fetched.Failure);
        Assert.Empty(decided);
        Assert.Empty(endpoint.Asked);
    }

    /// <summary>A denial stops the fetch before anything is sent.</summary>
    [Fact]
    public async Task ADenial_StopsTheFetch_NothingSent()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain"));
        using var fetch = Fetch(endpoint);
        var denial = new AgentPolicyAnswer.Denied("Web(*)", AgentPolicySource.Builtin, "no");

        var (fetched, decided, hops) = await FetchAsync(fetch, "https://example.com/", _ => denial);

        Assert.Equal(denial, fetched.Denial);
        Assert.Equal(["https://example.com/"], decided);
        Assert.Empty(hops);
        Assert.Empty(endpoint.Asked);
    }

    /// <summary>An address of this machine or its network, or a host that resolves to one, is refused before anything is sent.</summary>
    [Theory]
    [InlineData("http://127.0.0.1/admin", "'127.0.0.1' resolves to 127.0.0.1, which is this machine (loopback)")]
    [InlineData("http://[::1]:8080/", "'::1' resolves to ::1, which is this machine (loopback)")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "'169.254.169.254' resolves to 169.254.169.254, which is a link-local range, a cloud's metadata service among it")]
    [InlineData("https://internal.test/", "'internal.test' resolves to 10.0.0.7, which is a private range")]
    [InlineData("https://mixed.test/", "'mixed.test' resolves to 192.168.0.2, which is a private range")]
    public async Task APrivateAddress_IsRefused_NothingSent(string address, string why)
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain"));
        using var fetch = Fetch(endpoint);

        var (fetched, _, hops) = await FetchAsync(fetch, address);

        Assert.Equal($"{why}: this tool's fetch reaches public addresses alone", fetched.Failure);
        Assert.Equal(fetched.Failure, Assert.Single(hops).Failure);
        Assert.Empty(endpoint.Asked);
    }

    /// <summary>
    /// The first address is decided, recorded and answered as it is read and asked, not as it is spelled: a spelling .NET
    /// reads its own way is decided as the host it reaches.
    /// </summary>
    [Fact]
    public async Task TheFirstAddress_IsDecidedAsItIsAsked()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, hops) = await FetchAsync(fetch, "http:/\\example.com/x");

        Assert.Equal(["http://example.com/x"], decided);
        Assert.Equal("http://example.com/x", fetched.Address);
        Assert.Equal("http://example.com/x", Assert.Single(hops).Address);
    }

    /// <summary>A redirect is decided as its first address was, and one to a private address is refused before it is asked.</summary>
    [Fact]
    public async Task ARedirectToAPrivateAddress_IsRefused()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Redirect("http://169.254.169.254/latest/meta-data/"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, hops) = await FetchAsync(fetch, "https://example.com/go");

        Assert.Equal(["https://example.com/go", "http://169.254.169.254/latest/meta-data/"], decided);
        Assert.Equal("http://169.254.169.254/latest/meta-data/", fetched.Address);
        Assert.StartsWith("'169.254.169.254' resolves to 169.254.169.254", fetched.Failure, StringComparison.Ordinal);
        Assert.Equal([302, null], hops.Select(hop => hop.Status));
        Assert.Single(endpoint.Asked);
    }

    /// <summary>A relative redirect is read against the address that gave it; a sixth redirect is not followed.</summary>
    [Fact]
    public async Task ASixthRedirect_IsNotFollowed()
    {
        var endpoint = new WebEndpoint(request => WebEndpoint.Redirect($"/hop{int.Parse(request.RequestUri!.AbsolutePath.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture) + 1}"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, _) = await FetchAsync(fetch, "https://example.com/hop0");

        Assert.Equal(HarnessFetch.MostRedirects + 1, endpoint.Asked.Count);
        Assert.Equal(HarnessFetch.MostRedirects + 1, decided.Count);
        Assert.Equal("https://example.com/hop5", decided[^1]);
        Assert.Equal("'https://example.com/hop0' redirects more than 5 times, and no more are followed", fetched.Failure);
    }

    /// <summary>A redirect that names no address fails, and so does one to what reads as no address against the one it came from.</summary>
    [Fact]
    public async Task ARedirectThatNamesNoAddress_Fails()
    {
        using var fetch = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.Found)));

        var (fetched, _, _) = await FetchAsync(fetch, "https://example.com/");

        Assert.Equal("a redirect (302) that names no address", fetched.Failure);

        using var unread = Fetch(new WebEndpoint(_ => WebEndpoint.Redirect("//exa mple.com/")));

        var (unreadFetched, _, _) = await FetchAsync(unread, "https://example.com/");

        Assert.Equal("a redirect (302) to '//exa mple.com/', which reads as no address", unreadFetched.Failure);
    }

    /// <summary>A page whose size is said past the bound is refused unread; one that runs past it unsaid is read no further.</summary>
    [Fact]
    public async Task APagePastTheBound_IsRefused()
    {
        using var said = Fetch(new WebEndpoint(_ =>
        {
            var answer = WebEndpoint.Page("small", "text/plain");
            answer.Content.Headers.ContentLength = HarnessFetch.MostBytes + 1;
            return answer;
        }));

        var (saidFetched, _, saidHops) = await FetchAsync(said, "https://example.com/big");

        Assert.Equal($"{HarnessFetch.MostBytes + 1:N0} bytes, past the 5 MiB this tool's fetch reads", saidFetched.Failure);
        Assert.Null(Assert.Single(saidHops).Bytes);

        using var unsaid = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new Endless()) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } },
        }));

        var (unsaidFetched, _, unsaidHops) = await FetchAsync(unsaid, "https://example.com/endless");

        Assert.Equal("more than the 5 MiB this tool's fetch reads", unsaidFetched.Failure);
        Assert.InRange(Assert.Single(unsaidHops).Bytes!.Value, HarnessFetch.MostBytes + 1, HarnessFetch.MostBytes + (128 * 1024));
    }

    /// <summary>What is no text - an image, an archive, a type unsaid - is refused, unless any type is asked for: then it is read as text.</summary>
    [Theory]
    [InlineData("image/png", "its type is 'image/png', and this tool's fetch reads text alone: ")]
    [InlineData("application/octet-stream", "its type is 'application/octet-stream', and this tool's fetch reads text alone: ")]
    [InlineData(null, "its type is not said, and this tool's fetch reads text alone: ")]
    public async Task WhatIsNoText_IsRefused(string? type, string why)
    {
        using var fetch = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = type is null ? null : new MediaTypeHeaderValue(type) } },
        }));

        var (fetched, _, hops) = await FetchAsync(fetch, "https://example.com/file");

        Assert.Equal(why + "text/*, application/json, application/xml, application/xhtml+xml, application/javascript, application/ecmascript and every +json and +xml type", fetched.Failure);
        Assert.Equal(200, Assert.Single(hops).Status);

        var any = await fetch.FetchAsync("https://example.com/file", new WebFetchAsk { AnyType = true }, WebFetchGate.Open, TestContext.Current.CancellationToken);
        Assert.Equal(("\u0001\u0002\u0003", (string?)null), (any.Text, any.Failure));
        Assert.Equal(type, any.Type);
    }

    /// <summary>The handler the fetch sends through follows no redirect of its own, and sends no cookie, no credential and through no proxy.</summary>
    [Fact]
    public void TheHandler_SendsNoCookieCredentialOrProxy_AndFollowsNoRedirectOfItsOwn()
    {
        using var handler = HarnessFetch.Connecting(WebNamesDouble.AllPublic);

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.NotNull(handler.ConnectCallback);
    }

    /// <summary>
    /// A host that resolves to a public address when it is checked, and to this machine's when the connection is made, is
    /// refused by the connection's own check: no lookup's second answer reaches past the first.
    /// </summary>
    [Fact]
    public async Task AHostThatResolvesToThisMachineOnTheSecondLookup_IsRefusedByTheConnection()
    {
        var lookups = 0;
        var names = new WebNamesDouble(_ => Interlocked.Increment(ref lookups) == 1 ? [Public] : [IPAddress.Loopback]);
        using var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), names);

        var (fetched, _, _) = await FetchAsync(fetch, "http://rebinding.test/");

        Assert.Equal(2, lookups);
        Assert.Contains("'rebinding.test' resolves to 127.0.0.1, which is this machine (loopback): this tool's fetch reaches public addresses alone", fetched.Failure, StringComparison.Ordinal);
    }

    /// <summary>The connection checks every address its host resolves to, a public one beside a private one refused with it.</summary>
    [Fact]
    public async Task TheConnection_ChecksEveryAddress()
    {
        var names = new WebNamesDouble(_ => [Public, IPAddress.Parse("10.0.0.1")]);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(async () => await HarnessFetch.ConnectAsync(names, new DnsEndPoint("two.test", 443), TestContext.Current.CancellationToken));

        Assert.Equal("'two.test' resolves to 10.0.0.1, which is a private range: this tool's fetch reaches public addresses alone", refused.Message);
    }

    /// <summary>A host no lookup finds fails with the lookup's own words.</summary>
    [Fact]
    public async Task AHostNoLookupFinds_Fails()
    {
        using var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), new WebNamesDouble(_ => throw new SocketException((int)SocketError.HostNotFound)), new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain")));

        var (fetched, _, _) = await FetchAsync(fetch, "https://nowhere.test/");

        Assert.StartsWith("'nowhere.test' could not be looked up: ", fetched.Failure, StringComparison.Ordinal);
    }

    private static HarnessFetch Fetch(WebEndpoint endpoint)
        => new(new RunningToolDouble("1.2.3"), new WebNamesDouble(host => host switch
        {
            "internal.test" => [IPAddress.Parse("10.0.0.7")],
            "mixed.test" => [Public, IPAddress.Parse("192.168.0.2")],
            _ => [Public],
        }), endpoint);

    /// <summary>Asked for markup, a page's HTML is kept as it was sent, for a reader that reads it itself; the Accept asked is sent.</summary>
    [Fact]
    public async Task AskedForMarkup_APagesHtmlIsKept()
    {
        string? accept = null;
        using var fetch = Fetch(new WebEndpoint(request =>
        {
            accept = request.Headers.Accept.ToString();
            return WebEndpoint.Page("<html><head><title>T</title></head><body><h1>H</h1></body></html>", "text/html");
        }));

        var fetched = await fetch.FetchAsync("https://example.com/", new WebFetchAsk { Html = WebFetchHtml.Markup, Accept = "text/html" }, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Equal("<html><head><title>T</title></head><body><h1>H</h1></body></html>", fetched.Text);
        Assert.Equal("text/html", accept);
    }

    /// <summary>Asked for a gzip, one said as one, or of no type said under a name ending in .gz, is read as the text it holds; unasked, it is no text.</summary>
    [Theory]
    [InlineData("application/gzip", "https://example.com/sitemap.xml.gz")]
    [InlineData("application/x-gzip", "https://example.com/sitemap")]
    [InlineData("application/octet-stream", "https://example.com/sitemap.xml.GZ")]
    public async Task AGzip_IsReadAsItsText_WhereAsked(string type, string address)
    {
        using var fetch = Fetch(new WebEndpoint(_ => Gzipped(Encoding.UTF8.GetBytes("<urlset/>"), type)));

        var read = await fetch.FetchAsync(address, new WebFetchAsk { Gzip = true }, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var unasked = (await FetchAsync(fetch, address)).Fetched;

        Assert.Equal("<urlset/>", read.Text);
        Assert.Null(read.Failure);
        Assert.Null(unasked.Text);
        Assert.StartsWith("its type is", unasked.Failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A gzip that holds more than the fetch reads is refused once decompressed past it, read no further; one whose gzip is
    /// broken, said so; and one said to be a gzip whose bytes are none - the transport's own decompressed - is its text.
    /// </summary>
    [Fact]
    public async Task AGzipPastTheBound_IsRefused()
    {
        using var big = Fetch(new WebEndpoint(_ => Gzipped(new byte[HarnessFetch.MostBytes + 10], "application/gzip")));
        using var broken = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x1F, 0x8B, 1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("application/gzip") } } }));
        using var plain = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("<urlset/>"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/x-gzip") } } }));

        var past = await big.FetchAsync("https://example.com/s.gz", new WebFetchAsk { Gzip = true }, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var none = await broken.FetchAsync("https://example.com/s.gz", new WebFetchAsk { Gzip = true }, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var text = await plain.FetchAsync("https://example.com/s.gz", new WebFetchAsk { Gzip = true }, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Equal("more than the 5 MiB this tool's fetch reads once decompressed", past.Failure);
        Assert.StartsWith("its body could not be read: ", none.Failure, StringComparison.Ordinal);
        Assert.Equal(("<urlset/>", (string?)null), (text.Text, text.Failure));
    }

    /// <summary>A gzip is known by its bytes: one said to be bytes of any name, or XML, is read as the text it holds where any type is asked.</summary>
    [Theory]
    [InlineData("binary/octet-stream", "https://example.com/sitemap.xml.gz")]
    [InlineData("application/xml", "https://example.com/sitemap.xml")]
    public async Task AGzip_IsKnownByItsBytes(string type, string address)
    {
        using var fetch = Fetch(new WebEndpoint(_ => Gzipped(Encoding.UTF8.GetBytes("<urlset/>"), type)));

        var read = await fetch.FetchAsync(address, new WebFetchAsk { Gzip = true, AnyType = true }, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var unasked = await fetch.FetchAsync(address, new WebFetchAsk { AnyType = true }, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Equal(("<urlset/>", (string?)null), (read.Text, read.Failure));
        Assert.NotEqual("<urlset/>", unasked.Text);
    }

    /// <summary>An asker's own most is read to, past the fetch's; and where its head is asked, an answer past the most is kept to it, not refused.</summary>
    [Fact]
    public async Task AnAskersMost_AndAHead_AreReadTo()
    {
        var body = new string('a', (int)HarnessFetch.MostBytes + 10);
        using var fetch = Fetch(new WebEndpoint(_ => WebEndpoint.Page(body, "text/plain")));

        var refused = await fetch.FetchAsync("https://example.com/big", new WebFetchAsk(), WebFetchGate.Open, TestContext.Current.CancellationToken);
        var most = await fetch.FetchAsync("https://example.com/big", new WebFetchAsk { Most = HarnessFetch.MostBytes * 2 }, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var head = await fetch.FetchAsync("https://example.com/big", new WebFetchAsk { Head = true }, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.NotNull(refused.Failure);
        Assert.Equal((body.Length, (string?)null), (most.Text?.Length, most.Failure));
        Assert.Equal(((int?)HarnessFetch.MostBytes, (string?)null), (head.Text?.Length, head.Failure));
    }

    /// <summary>What an address was answered keeps its headers, the content's among them, by name in any case - a failure's too.</summary>
    [Fact]
    public async Task AnAnswer_KeepsItsHeaders()
    {
        using var fetch = Fetch(new WebEndpoint(request => request.RequestUri!.AbsolutePath == "/limited"
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) }, Content = new ByteArrayContent([1, 2]) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } } }
            : WebEndpoint.Page("ok", "text/plain")));

        var (read, _, hops) = await FetchAsync(fetch, "https://example.com/");
        var (limited, _, _) = await FetchAsync(fetch, "https://example.com/limited");

        Assert.Equal("text/plain; charset=utf-8", read.Headers["content-type"]);
        Assert.Equal("text/plain; charset=utf-8", Assert.Single(hops).Headers["Content-Type"]);
        Assert.Equal((429, "30"), (limited.Status, limited.Headers["retry-after"]));
        Assert.NotNull(limited.Failure);
    }

    private static HttpResponseMessage Gzipped(byte[] bytes, string type)
    {
        using var held = new MemoryStream();

        using (var gzip = new System.IO.Compression.GZipStream(held, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(held.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue(type) } } };
    }

    private static async Task<(WebFetched Fetched, List<string> Decided, List<WebHop> Hops)> FetchAsync(
        HarnessFetch fetch,
        string address,
        Func<string, AgentPolicyAnswer>? decide = null)
    {
        var decided = new List<string>();
        var hops = new List<WebHop>();

        var fetched = await fetch.FetchAsync(
            address,
            new WebFetchGate(
                asked =>
                {
                    decided.Add(asked);
                    return Task.FromResult(decide?.Invoke(asked) ?? AgentPolicyAnswer.Allow);
                },
                hop =>
                {
                    hops.Add(hop);
                    return Task.CompletedTask;
                }),
            TestContext.Current.CancellationToken);

        return (fetched, decided, hops);
    }

    /// <summary>A body that does not decode, raising what gzip's decoder raises, or brotli's.</summary>
    private sealed class Undecodable(bool brotli) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
            => throw (brotli ? new InvalidOperationException("the stream's encoding does not decode") : new InvalidDataException("the stream's encoding does not decode"));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A body that never ends.</summary>
    private sealed class Endless : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'a', offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
