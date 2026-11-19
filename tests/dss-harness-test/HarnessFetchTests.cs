using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// The harness's own fetch, over an endpoint a test answers: each address decided before anything is sent, a host that
/// resolves to an address no public host holds refused, its redirects followed as a chain to its end or its first revisit,
/// an answer read whole for as long as it moves, text alone read.
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
    /// The stall bound is an answer's silence alone: a decision and a turn waited for, each hop's, are not charged to it, so
    /// a slow pace never takes a request's time.
    /// </summary>
    [Fact]
    public async Task ADecisionAndATurn_AreWaitedForOutsideTheStallBound()
    {
        using var fetch = new HarnessFetch(
            new RunningToolDouble("1.2.3"),
            WebNamesDouble.AllPublic,
            new WebEndpoint(request => request.RequestUri!.AbsolutePath == "/a" ? WebEndpoint.Redirect("/b") : WebEndpoint.Page("paced", "text/plain")));
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
            Stall = TimeSpan.FromSeconds(1),
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

    /// <summary>
    /// A redirect back to an address the chain asked before, by the same method, is a loop: it ends at its first revisit,
    /// the address it leads back to never asked again - however few hops the loop is, with no count of hops.
    /// </summary>
    [Fact]
    public async Task ARedirectLoop_EndsAtItsFirstRevisit()
    {
        var endpoint = new WebEndpoint(request => WebEndpoint.Redirect(request.RequestUri!.AbsolutePath == "/a" ? "/b" : "/a"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, hops) = await FetchAsync(fetch, "https://example.com/a");

        Assert.Equal(["https://example.com/a", "https://example.com/b"], decided);
        Assert.Equal(2, endpoint.Asked.Count);
        Assert.Equal([302, 302], hops.Select(hop => hop.Status));
        Assert.Equal(
            ("https://example.com/b", "'https://example.com/a' redirects in a loop: 'https://example.com/b' leads back to 'https://example.com/a', asked before, and it is not asked again"),
            (fetched.Address, fetched.Failure));
    }

    /// <summary>
    /// A chain of new addresses is followed to its end, each decided and answered, however long: twelve redirects, a
    /// relative one read against the address that gave it, then the page.
    /// </summary>
    [Fact]
    public async Task ATwelveHopChain_IsFollowed()
    {
        var endpoint = new WebEndpoint(request => int.Parse(request.RequestUri!.AbsolutePath.AsSpan(4), System.Globalization.CultureInfo.InvariantCulture) is var hop and < 12
            ? WebEndpoint.Redirect($"/hop{hop + 1}")
            : WebEndpoint.Page("the end", "text/plain"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, hops) = await FetchAsync(fetch, "https://example.com/hop0");

        Assert.Equal(13, endpoint.Asked.Count);
        Assert.Equal([.. Enumerable.Range(0, 13).Select(hop => $"https://example.com/hop{hop}")], decided);
        Assert.Equal(13, hops.Count);
        Assert.Equal(("https://example.com/hop12", "the end", (string?)null), (fetched.Address, fetched.Text, fetched.Failure));
    }

    /// <summary>
    /// An answer that goes on coming, however slowly, is read to its end - its wait told every five minutes with how much
    /// has come, never cut at a time of the fetch's own: a server that sends a byte every seven seconds for an hour is
    /// read whole.
    /// </summary>
    [Fact]
    public async Task ATricklingServer_IsToldNotCut()
    {
        const int Bytes = 520;
        var clock = new TimerClock();
        var trickle = new Trickle(clock, Bytes, TimeSpan.FromSeconds(7));
        using var fetch = new HarnessFetch(
            new RunningToolDouble("1.2.3"),
            WebNamesDouble.AllPublic,
            new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(trickle) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } } }),
            clock);
        var told = new List<Core.Waiting.WaitTold>();
        var gate = WebFetchGate.Open with { Stall = TimeSpan.FromSeconds(30), Tell = told.Add };

        var fetching = fetch.FetchAsync("https://example.com/slow", WebFetchAsk.Page, gate, TestContext.Current.CancellationToken);

        // Moved a second at a time once the wait and the trickle's next byte both wait on the clock.
        while (!fetching.IsCompleted)
        {
            await Task.WhenAny(clock.WaitingAsync(2), fetching).WaitAsync(ClockDrive.Within, TestContext.Current.CancellationToken);

            if (!fetching.IsCompleted)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        var fetched = await fetching;

        Assert.Equal((new string('a', Bytes), (string?)null), (fetched.Text, fetched.Failure));
        Assert.True(told.Count >= 12, $"told {told.Count} times over {Bytes * 7} s");
        Assert.All(told, said => Assert.Equal("'https://example.com/slow' to answer", said.Subject));
        Assert.Equal("42 bytes so far", told[0].Progress);
        Assert.Equal(TimeSpan.FromMinutes(5), told[0].Waited);
    }

    /// <summary>
    /// An answer that falls silent - nothing of its head or its body coming - for as long as the step's stall bound is given
    /// up, said so; one whose head came keeps its status and headers, so the pause a 429 asks for holds.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASilentServer_EndsAtTheStepsStallBound(bool headCame)
    {
        var clock = new TimerClock();
        using var fetch = new HarnessFetch(
            new RunningToolDouble("1.2.3"),
            WebNamesDouble.AllPublic,
            new Answering(headCame
                ? (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) },
                    Content = new StreamContent(new Stalled()) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
                })
                : async (_, cancellationToken) =>
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("never answered");
                }),
            clock);
        var hops = new List<WebHop>();
        var gate = new WebFetchGate(_ => Task.FromResult(AgentPolicyAnswer.Allow), hop =>
        {
            hops.Add(hop);
            return Task.CompletedTask;
        }) { Stall = TimeSpan.FromSeconds(30) };

        var fetched = await ClockDrive.UntilEndedAsync(clock, fetch.FetchAsync("https://example.com/silent", new WebFetchAsk { AnyType = true }, gate, TestContext.Current.CancellationToken), TimeSpan.FromSeconds(1), 31, TestContext.Current.CancellationToken);

        Assert.Equal("nothing came from it for 30 s, the step's stall bound", fetched.Failure);
        Assert.Equal(fetched.Failure, Assert.Single(hops).Failure);
        Assert.Equal(headCame ? 429 : null, fetched.Status);
        Assert.Equal(headCame ? "30" : null, fetched.Headers.GetValueOrDefault("Retry-After"));
    }

    /// <summary>
    /// A body is read whole, as large as it is, streamed into the writer its asker gives - never held as one text - and
    /// laid in pages a reader asks for one at a time: three hundred megabytes from a server on this machine.
    /// </summary>
    [Fact]
    public async Task AThreeHundredMegabyteBody_IsStreamedAndPaged()
    {
        const long Size = 300L * 1024 * 1024;
        const string Line = "a line of the body, sixty-four bytes long with its own newline\n";

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

            while (await reader.ReadLineAsync(TestContext.Current.CancellationToken) is { Length: > 0 })
            {
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {Size}\r\nConnection: close\r\n\r\n"), TestContext.Current.CancellationToken);

            var chunk = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(Line, 1024)));

            for (var sent = 0L; sent < Size; sent += chunk.Length)
            {
                await stream.WriteAsync(chunk, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        using var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, handler);
        using var directory = new TempDirectory();
        var path = directory.Combine("body.txt");
        WebFetched fetched;

        await using (var into = new StreamWriter(path, append: false, new UTF8Encoding(false)))
        {
            fetched = await fetch.FetchAsync("http://big.test/body", new WebFetchAsk { Into = into }, WebFetchGate.Open, TestContext.Current.CancellationToken);
        }

        await serving;

        Assert.Equal((Size, (string?)null, (string?)null), (fetched.Written, fetched.Text, fetched.Failure));
        Assert.Equal(Size, new FileInfo(path).Length);

        var files = new Core.FileSystem.PhysicalFileSystem(Core.Platform.FilePermissionsFactory.Create());
        var kept = await Core.Pages.TextPages.LayAsync(files, path, "body", "the body", Core.Pages.PageRoom.Least, numbered: false, TestContext.Current.CancellationToken);
        var last = await Core.Pages.TextPages.PageAsync(files, kept, kept.Marks.Count, numbered: false, null, TestContext.Current.CancellationToken);

        Assert.True(kept.Marks.Count > 1000, $"{kept.Marks.Count} pages");
        Assert.Contains(Line.TrimEnd('\n'), Assert.IsType<string>(last.Text), StringComparison.Ordinal);
    }

    /// <summary>
    /// What an asker's own protocol bounds - a sitemap's 50 MiB - is its fact, said in its words: an answer said past it is
    /// refused unread, one that runs past it unsaid is read no further, a gzip once decompressed past it alike; and an
    /// asker with no such fact reads the same answers whole.
    /// </summary>
    [Fact]
    public async Task AnAskersMost_IsItsProtocolsFact()
    {
        const long Most = 1024 * 1024;
        var ask = new WebFetchAsk { Most = Most, MostSaid = "the sitemaps protocol lets a sitemap be", Gzip = true };
        using var said = Fetch(new WebEndpoint(_ =>
        {
            var answer = WebEndpoint.Page("small", "text/plain");
            answer.Content.Headers.ContentLength = Most + 1;
            return answer;
        }));
        using var endless = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new Endless()) { Headers = { ContentType = new MediaTypeHeaderValue("text/plain") } },
        }));
        using var big = Fetch(new WebEndpoint(_ => Gzipped(Encoding.ASCII.GetBytes(new string('a', (int)Most + 10)), "application/gzip")));
        using var broken = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x1F, 0x8B, 1, 2, 3]) { Headers = { ContentType = new MediaTypeHeaderValue("application/gzip") } } }));
        using var plain = Fetch(new WebEndpoint(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("<urlset/>"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/x-gzip") } } }));

        var refused = await said.FetchAsync("https://example.com/s.xml", ask, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var cut = await endless.FetchAsync("https://example.com/s.xml", ask, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var past = await big.FetchAsync("https://example.com/s.gz", ask, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var whole = await big.FetchAsync("https://example.com/s.gz", new WebFetchAsk { Gzip = true }, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var none = await broken.FetchAsync("https://example.com/s.gz", ask, WebFetchGate.Open, TestContext.Current.CancellationToken);
        var text = await plain.FetchAsync("https://example.com/s.gz", ask, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Equal($"{Most + 1:N0} bytes, past the 1 MiB the sitemaps protocol lets a sitemap be", refused.Failure);
        Assert.Equal("more than the 1 MiB the sitemaps protocol lets a sitemap be", cut.Failure);
        Assert.Equal("more than the 1 MiB the sitemaps protocol lets a sitemap be once decompressed", past.Failure);
        Assert.Equal(((int)Most + 10, (string?)null), (whole.Text?.Length, whole.Failure));
        Assert.StartsWith("its body could not be read: ", none.Failure, StringComparison.Ordinal);
        Assert.Equal(("<urlset/>", (string?)null), (text.Text, text.Failure));
    }

    /// <summary>A body past what the fetch once read - five mebibytes - is read whole, as any is.</summary>
    [Fact]
    public async Task ABodyPastFiveMebibytes_IsReadWhole()
    {
        var body = new string('a', (5 * 1024 * 1024) + 10);
        using var fetch = Fetch(new WebEndpoint(_ => WebEndpoint.Page(body, "text/plain")));

        var read = await fetch.FetchAsync("https://example.com/big", new WebFetchAsk(), WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Equal((body.Length, (string?)null), (read.Text?.Length, read.Failure));
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

    /// <summary>
    /// A POST sends its body as JSON, whatever type the asker says; the asker's headers are sent beside the fetch's own, an
    /// Accept of theirs over the ask's, and none that the request itself says nor one that carries a key; and the key goes
    /// to the scheme, host and port first asked.
    /// </summary>
    [Fact]
    public async Task APost_SendsItsBodyAndHeaders_AndTheKeyToItsOwnHost()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("{}", "application/json"));
        using var fetch = Fetch(endpoint);
        var ask = new WebFetchAsk
        {
            AnyType = true,
            Accept = "text/plain",
            Method = "POST",
            Body = """{"q":"x"}""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/vnd.example+json", ["X-Api-Version"] = "2", ["User-Agent"] = "browser", ["Authorization"] = "Basic own", ["Cookie"] = "c=1" },
            Key = new WebFetchKey("X-Api-Key", "k-123") { Origins = ["https://api.example.com"] },
        };

        var fetched = await fetch.FetchAsync("https://api.example.com/search", ask, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Null(fetched.Failure);
        var sent = Assert.Single(endpoint.Asked);
        Assert.Equal(("POST", """{"q":"x"}"""), (sent.Method, sent.Body));
        Assert.Equal("application/vnd.example+json", sent.Headers["Accept"]);
        Assert.Equal("2", sent.Headers["X-Api-Version"]);
        Assert.Equal("application/json; charset=utf-8", sent.Headers["Content-Type"]);
        Assert.Equal("k-123", sent.Headers["X-Api-Key"]);
        Assert.Equal((false, false), (sent.Authorization, sent.Cookie));
        Assert.Equal("dssharness/1.2.3 (+https://github.com/dailysoftwaresystems/dss-harness)", sent.UserAgent);
        Assert.Equal("X-Api-Key", ask.Key.ToString());

        // A type the asker says is the request's own to say: the body is JSON.
        var typed = await fetch.FetchAsync(
            "https://api.example.com/search",
            ask with { Headers = new Dictionary<string, string> { ["Content-Type"] = "application/x-ndjson" }, Key = null },
            WebFetchGate.Open,
            TestContext.Current.CancellationToken);

        Assert.Null(typed.Failure);
        Assert.Equal("application/json; charset=utf-8", endpoint.Asked[^1].Headers["Content-Type"]);
        Assert.False(endpoint.Asked[^1].Headers.ContainsKey("X-Api-Key"));
    }

    /// <summary>
    /// A key sent as a query parameter is added to what is sent to the scheme, host and port first asked alone, escaped
    /// whatever it holds: the address decided, recorded and answered is the one asked, which holds no form of it; a redirect
    /// to another host carries none.
    /// </summary>
    [Fact]
    public async Task AKeyInTheQuery_IsSentAlone_AndNoAddressHoldsIt()
    {
        const string Key = "a(b)*+/= c";
        var endpoint = new WebEndpoint(request => request.RequestUri!.Host == "api.example.com" && request.RequestUri.AbsolutePath == "/first"
            ? WebEndpoint.Redirect("https://cdn.example.net/next?page=2")
            : WebEndpoint.Page("ok", "text/plain"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, hops) = await FetchAsync(fetch, "https://api.example.com/first?q=x", key: new WebFetchKey("key", Key, Query: true) { Origins = ["https://api.example.com"] });

        Assert.Null(fetched.Failure);
        Assert.Equal(["q=x", "key=" + Uri.EscapeDataString(Key)], endpoint.Asked[0].Address.Query.TrimStart('?').Split('&'));
        Assert.Equal(Key, Uri.UnescapeDataString(endpoint.Asked[0].Address.Query.Split("key=")[1]));
        Assert.Equal("?page=2", endpoint.Asked[1].Address.Query);
        Assert.Equal(["https://api.example.com/first?q=x", "https://cdn.example.net/next?page=2"], decided);
        Assert.Equal(["https://api.example.com/first?q=x", "https://cdn.example.net/next?page=2"], hops.Select(hop => hop.Address));
        Assert.Equal("https://cdn.example.net/next?page=2", fetched.Address);
        Assert.Equal("?key", new WebFetchKey("key", Key, Query: true) { Origins = ["https://api.example.com"] }.ToString());

        // An address with no query of its own is given one.
        var (bare, _, _) = await FetchAsync(fetch, "https://api.example.com/plain", key: new WebFetchKey("key", "k", Query: true) { Origins = ["https://api.example.com"] });

        Assert.Null(bare.Failure);
        Assert.Equal("https://api.example.com/plain?key=k", endpoint.Asked[^1].Address.AbsoluteUri);
        Assert.Equal("https://api.example.com/plain", bare.Address);

        // A fragment, which is never sent, never holds it.
        var (fragment, _, _) = await FetchAsync(fetch, "https://api.example.com/plain?q=y#top", key: new WebFetchKey("key", "k", Query: true) { Origins = ["https://api.example.com"] });

        Assert.Null(fragment.Failure);
        Assert.Equal("?q=y&key=k", endpoint.Asked[^1].Address.Query);
    }

    /// <summary>A host longer than a name may be is one no host answers: said, and nothing sent, as any name that cannot be looked up.</summary>
    [Fact]
    public async Task AHostLongerThanAName_IsNotLookedUp()
    {
        var host = string.Join('.', Enumerable.Repeat(new string('a', 50), 6)) + ".test";
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("ok", "text/plain"));
        using var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), new SystemWebNames(), endpoint);

        var fetched = await fetch.FetchAsync($"https://{host}/", WebFetchAsk.Page, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.StartsWith($"'{host}' could not be looked up: ", fetched.Failure, StringComparison.Ordinal);
        Assert.Empty(endpoint.Asked);
    }

    /// <summary>A key never travels in the clear: a fetch that carries one and begins at an http address is refused by the key's rule, nothing sent, nothing decided.</summary>
    [Fact]
    public async Task AKey_IsNeverSentInTheClear()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("ok", "text/plain"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, _) = await FetchAsync(fetch, "http://api.example.com/search?q=x", key: new WebFetchKey("key", "k", Query: true) { Origins = ["https://api.example.com"] });

        Assert.Equal(("http://api.example.com/search?q=x", HarnessFetch.KeyRule, "a key is sent over https alone, and this address is no https one"), (fetched.Address, fetched.Denial?.Rule, fetched.Denial?.Reason));
        Assert.Empty(decided);
        Assert.Empty(endpoint.Asked);
    }

    /// <summary>
    /// A key is sent to the scheme, host and port first asked alone: a redirect on that host is asked with it, one to the
    /// host over http, to another of its ports or to another host without it.
    /// </summary>
    [Fact]
    public async Task AKey_IsNeverSentToAnotherSchemeHostOrPort()
    {
        var endpoint = new WebEndpoint(request => request.RequestUri!.AbsolutePath switch
        {
            "/first" => WebEndpoint.Redirect("/second"),
            "/second" => WebEndpoint.Redirect("http://api.example.com/third"),
            "/third" => WebEndpoint.Redirect("https://api.example.com:8443/fourth"),
            "/fourth" => WebEndpoint.Redirect("https://cdn.example.net/fifth"),
            _ => WebEndpoint.Page("ok", "text/plain"),
        });
        using var fetch = Fetch(endpoint);

        var fetched = await fetch.FetchAsync("https://api.example.com/first", new WebFetchAsk { Key = new WebFetchKey("Authorization", "Bearer t") { Origins = ["https://api.example.com"] } }, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Null(fetched.Failure);
        Assert.Equal(
            ["https://api.example.com/first", "https://api.example.com/second", "http://api.example.com/third", "https://api.example.com:8443/fourth", "https://cdn.example.net/fifth"],
            endpoint.Asked.Select(sent => sent.Address.AbsoluteUri));
        Assert.Equal([true, true, false, false, false], endpoint.Asked.Select(sent => sent.Authorization));
    }

    /// <summary>
    /// A key's query parameter is the fetch's alone: a redirect that echoes it back is followed without it - the key sent
    /// once on its own host, never on another - and no address decided, recorded or answered holds it.
    /// </summary>
    [Fact]
    public async Task AKeyEchoedByARedirect_IsDroppedFromEveryAddress()
    {
        const string Key = "a+b/c";
        var echoed = "key=" + Uri.EscapeDataString(Key);
        var endpoint = new WebEndpoint(request => request.RequestUri!.AbsolutePath switch
        {
            "/first" => WebEndpoint.Redirect($"https://api.example.com/second?q=x&{echoed}"),
            "/second" => WebEndpoint.Redirect($"https://cdn.example.net/third?{echoed}&page=2"),
            _ => WebEndpoint.Page("ok", "text/plain"),
        });
        using var fetch = Fetch(endpoint);

        var (fetched, decided, hops) = await FetchAsync(fetch, "https://api.example.com/first?q=x", key: new WebFetchKey("key", Key, Query: true) { Origins = ["https://api.example.com"] });

        Assert.Null(fetched.Failure);
        Assert.Equal(["q=x&" + echoed, "q=x&" + echoed, "page=2"], endpoint.Asked.Select(sent => sent.Address.Query.TrimStart('?')));
        Assert.Equal(["https://api.example.com/first?q=x", "https://api.example.com/second?q=x", "https://cdn.example.net/third?page=2"], decided);
        Assert.Equal(decided, hops.Select(hop => hop.Address));
        Assert.Equal("https://cdn.example.net/third?page=2", fetched.Address);
    }

    /// <summary>A first address that names the parameter its key is sent as is refused, nothing sent: the key would replace what it says.</summary>
    [Fact]
    public async Task AnAddressNamingTheKeysParameter_IsRefused()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain"));
        using var fetch = Fetch(endpoint);

        var (fetched, decided, _) = await FetchAsync(fetch, "https://api.example.com/search?q=x&page=2", key: new WebFetchKey("q", "k", Query: true) { Origins = ["https://api.example.com"] });

        Assert.Equal((HarnessFetch.KeyRule, "'https://api.example.com/search?q=x&page=2' names 'q', the parameter its key is sent as"), (fetched.Denial?.Rule, fetched.Denial?.Reason));
        Assert.Null(fetched.Failure);
        Assert.Empty(decided);
        Assert.Empty(endpoint.Asked);
    }

    /// <summary>
    /// A key goes to its origins alone - a gh login's host and its API, a source's own address's scheme, host and port - and
    /// is refused at any other first address, another port of its host among them, nothing sent and nothing decided; one
    /// whose source names no origin of its own is refused at every address. At one of its origins, however its address
    /// spells it, it is sent.
    /// </summary>
    [Fact]
    public async Task AKey_IsSentToItsOriginsAlone()
    {
        var endpoint = new WebEndpoint(_ => WebEndpoint.Page("x", "text/plain"));
        using var fetch = Fetch(endpoint);
        var key = new WebFetchKey("Authorization", "Bearer t") { Origins = ["https://github.com", "https://api.github.com"] };

        var (elsewhere, decided, _) = await FetchAsync(fetch, "https://ghe.example.com/api/v3/search", key: key);
        var (port, _, _) = await FetchAsync(fetch, "https://api.github.com:8443/search", key: key);
        var (nowhere, _, _) = await FetchAsync(fetch, "https://api.github.com/search", key: key with { Origins = [] });
        var (api, _, _) = await FetchAsync(fetch, "https://API.github.com:443/search", key: key);

        Assert.Equal(
            [
                (HarnessFetch.KeyRule, "its key is sent to https://github.com or https://api.github.com alone, and this address is https://ghe.example.com's"),
                (HarnessFetch.KeyRule, "its key is sent to https://github.com or https://api.github.com alone, and this address is https://api.github.com:8443's"),
                (HarnessFetch.KeyRule, "its key is sent to no scheme, host and port, for its source's configuration writes none of its own, and a host a document it read names is never sent one: this address is https://api.github.com's"),
            ],
            new[] { elsewhere, port, nowhere }.Select(fetched => (fetched.Denial?.Rule, fetched.Denial?.Reason)));
        Assert.Empty(decided);
        Assert.Equal(("x", true), (api.Text, Assert.Single(endpoint.Asked).Authorization));
    }

    /// <summary>A 303 is followed as a GET with no body, as are a 301 and a 302 of a POST; a 307 and a 308 keep the method and body.</summary>
    [Theory]
    [InlineData(HttpStatusCode.SeeOther, "GET", null)]
    [InlineData(HttpStatusCode.MovedPermanently, "GET", null)]
    [InlineData(HttpStatusCode.Found, "GET", null)]
    [InlineData(HttpStatusCode.TemporaryRedirect, "POST", "{}")]
    [InlineData(HttpStatusCode.PermanentRedirect, "POST", "{}")]
    public async Task ARedirectOfAPost_IsFollowedAsItsStatusSays(HttpStatusCode status, string method, string? body)
    {
        var endpoint = new WebEndpoint(request => request.RequestUri!.AbsolutePath == "/post"
            ? new HttpResponseMessage(status) { Headers = { Location = new Uri("/answer", UriKind.Relative) } }
            : WebEndpoint.Page("ok", "text/plain"));
        using var fetch = Fetch(endpoint);

        var fetched = await fetch.FetchAsync("https://example.com/post", new WebFetchAsk { Method = "POST", Body = "{}" }, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Null(fetched.Failure);
        Assert.Equal(("POST", "{}"), (endpoint.Asked[0].Method, endpoint.Asked[0].Body));
        Assert.Equal((method, body), (endpoint.Asked[1].Method, endpoint.Asked[1].Body));

        // A GET stays a GET whatever redirects it.
        var got = await fetch.FetchAsync("https://example.com/post", WebFetchAsk.Page, WebFetchGate.Open, TestContext.Current.CancellationToken);

        Assert.Null(got.Failure);
        Assert.Equal("GET", endpoint.Asked[^1].Method);
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
        Func<string, AgentPolicyAnswer>? decide = null,
        WebFetchKey? key = null)
    {
        var decided = new List<string>();
        var hops = new List<WebHop>();

        var fetched = await fetch.FetchAsync(
            address,
            key is null ? WebFetchAsk.Page : WebFetchAsk.Page with { Key = key },
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

    /// <summary>A body never sent: each read waits until it is given up.</summary>
    private sealed class Stalled : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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

    /// <summary>A body sent a byte at a time, each after a wait on the test's clock, until all are sent.</summary>
    private sealed class Trickle(TimeProvider clock, int bytes, TimeSpan every) : Stream
    {
        private int _sent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent == bytes || buffer.IsEmpty)
            {
                return 0;
            }

            await Task.Delay(every, clock, cancellationToken);
            buffer.Span[0] = (byte)'a';
            _sent++;
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A handler that answers as a test's function does, in its own time: one that never answers among them.</summary>
    private sealed class Answering(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => answer(request, cancellationToken);
    }
}
