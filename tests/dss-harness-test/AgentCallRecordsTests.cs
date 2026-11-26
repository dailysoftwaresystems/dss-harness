using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// What a step's calls leave beside its stored stream, on every client: a denial's line and event, a request of the web's
/// lines and event, a result longer than a page kept whole and given as its first page, and the blocks its lines headed kept beside it.
/// </summary>
public sealed class AgentCallRecordsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A result that is one page is carried whole and kept in no file; a longer one is kept whole in its call's file and
    /// carried as its first page, whose header says how many pages it holds, how read_result gives the next and where the
    /// whole is - and read_result's pages after it go on where it ends, every line of the result given once.
    /// </summary>
    [Fact]
    public async Task AnAnswerLongerThanAPage_GivesPageOne_TheWholeKept()
    {
        using var records = Records(out var events, out var run);

        var small = new string('s', KeptResults.Room - 1);
        Assert.Equal((small, (string?)null), records.Kept("call-1", small));

        var lines = Enumerable.Range(1, 20_000).Select(at => $"line {at.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)} of the answer").ToList();
        var whole = string.Join('\n', lines);
        var (shown, file) = records.Kept("call-2", whole);

        Assert.Equal(AgentCallRecords.ResultFile(run.StepDirectory, "call-2"), file);
        Assert.Equal(whole, File.ReadAllText(file!));

        var header = shown.Split('\n')[0];
        var pages = int.Parse(header.Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal($"page 1 of {pages} · 20000 results · ask read_result with id: 'call-2', page: 2 for the next · the whole is '{file}'", header);
        Assert.True(pages > 1);
        Assert.True(shown.Length <= DssHarness.Core.Pages.PageRoom.ModelAnswer, $"page 1 is {shown.Length} characters");

        var given = shown.Split('\n').Skip(1).ToList();
        var results = KeptResults.Of(new PhysicalFileSystem(FilePermissionsFactory.Create()), run.StepDirectory);

        for (var page = 2; page <= pages; page++)
        {
            var answer = await results.ReadAsync("call-2", page, TestContext.Current.CancellationToken);
            given.AddRange(answer!.Text.Split('\n').Skip(1));
        }

        Assert.Equal(lines, given);
        Assert.Empty(events.All);
    }

    /// <summary>A result is masked before it is carried or kept.</summary>
    [Fact]
    public void AResult_IsMaskedBeforeItIsCarriedOrKept()
    {
        using var records = Records(out _, out var run, new Masking("hunter2", "***"));

        var (shown, _) = records.Kept("c", "the key is hunter2");
        Assert.Equal("the key is ***", shown);

        var (first, file) = records.Kept("d", "hunter2\n" + string.Join('\n', Enumerable.Repeat(new string('x', 100), KeptResults.Room / 50)));
        Assert.Contains("\n***\n", first, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", first, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", File.ReadAllText(file!), StringComparison.Ordinal);
    }

    /// <summary>
    /// A call's result file is named by the full digest of its id (PageKey.OfCall), so two ids that share their first 96
    /// characters - once the whole of a name - keep two results; an id of no character is an id as any other.
    /// </summary>
    [Fact]
    public void TwoCallIdsSharingNinetySixCharacters_KeepTwoResults()
    {
        Assert.Equal(Path.Combine("s", "tool-results", DssHarness.Core.Pages.PageKey.OfCall("call_01-x") + ".txt"), AgentCallRecords.ResultFile("s", "call_01-x"));
        Assert.Equal(Path.Combine("s", "tool-results", DssHarness.Core.Pages.PageKey.OfCall("../:\\.x") + ".txt"), AgentCallRecords.ResultFile("s", "../:\\.x"));
        Assert.Equal(Path.Combine("s", "tool-results", DssHarness.Core.Pages.PageKey.OfCall(string.Empty) + ".txt"), AgentCallRecords.ResultFile("s", string.Empty));

        var shared = new string('a', 96);
        Assert.NotEqual(AgentCallRecords.ResultFile("s", shared + "1"), AgentCallRecords.ResultFile("s", shared + "2"));

        using var records = Records(out _, out var run);
        var one = string.Join('\n', Enumerable.Repeat("one", KeptResults.Room));
        var two = string.Join('\n', Enumerable.Repeat("two", KeptResults.Room));
        var (_, first) = records.Kept(shared + "1", one);
        var (_, second) = records.Kept(shared + "2", two);

        Assert.NotEqual(first, second);
        Assert.Equal((one, two), (File.ReadAllText(first!), File.ReadAllText(second!)));

        // What the step keeps of a call apart from its result is named by its id itself where it reads as itself, else by its digest.
        Assert.Equal("toolu_01-x", AgentCallRecords.NameOf("toolu_01-x"));
        Assert.Equal(DssHarness.Core.Pages.PageKey.OfCall(shared + "1"), AgentCallRecords.NameOf(shared + "1"));
        Assert.NotEqual(AgentCallRecords.NameOf(shared + "1"), AgentCallRecords.NameOf(shared + "2"));
        Assert.Equal("call", AgentCallRecords.NameOf(string.Empty));
    }

    /// <summary>
    /// An answer written a line at a time is held while it is one page, and from the line that passes it written on into
    /// its call's file whole, each line masked, what was held first: the same file a whole answer's keeping writes.
    /// </summary>
    [Fact]
    public async Task AnAnswerWrittenALineAtATime_IsHeldToAPage_TheRestWrittenOn()
    {
        using var records = Records(out _, out var run, new Masking("hunter2", "***"));
        var token = TestContext.Current.CancellationToken;

        await using (var short_ = records.Lines("short"))
        {
            await short_.LineAsync("a", token);
            await short_.LineAsync("b hunter2", token);
            await short_.EndAsync();
            Assert.Equal(("a\nb ***", (string?)null, 2L), (short_.Text, short_.File, short_.Count));
        }

        var lines = Enumerable.Range(0, 5_000).Select(at => $"hit {at} hunter2").ToList();

        await using (var long_ = records.Lines("long"))
        {
            foreach (var line in lines)
            {
                await long_.LineAsync(line, token);
            }

            await long_.EndAsync();
            Assert.Null(long_.Text);
            Assert.Equal(AgentCallRecords.ResultFile(run.StepDirectory, "long"), long_.File);
            Assert.Equal(string.Join('\n', lines.Select(line => line.Replace("hunter2", "***", StringComparison.Ordinal))), File.ReadAllText(long_.File!));

            // Its lines headed no block, so none is kept beside it.
            Assert.False(File.Exists(DssHarness.Core.Pages.TextBlocks.FileOf(long_.File!)));
        }

        // One abandoned before its end leaves no file.
        await using (var abandoned = records.Lines("abandoned"))
        {
            foreach (var line in lines)
            {
                await abandoned.LineAsync(line, token);
            }
        }

        Assert.False(File.Exists(AgentCallRecords.ResultFile(run.StepDirectory, "abandoned")));
    }

    /// <summary>
    /// An answer whose lines head blocks keeps them beside its file once it passes a page: each block whose head a line of it
    /// followed, where the file holds its head, the line after its head and the line after its last - the answer's end for
    /// the last block - counted in the masked lines' bytes, then the digest of the file's bytes; a head no line of its block
    /// followed is no block. One that fits a page keeps no file and no blocks, one abandoned leaves neither, and a line of a
    /// block with no head before it is refused.
    /// </summary>
    [Fact]
    public async Task AnAnswerWhoseLinesHeadBlocks_KeepsThemBesideItsFile_WhereTheFileHoldsEach()
    {
        using var records = Records(out _, out var run, new Masking("hunter2", "***"));
        var token = TestContext.Current.CancellationToken;
        var many = Enumerable.Range(0, 3_000).Select(at => $"  hit {at} hunter2 é").ToList();
        List<(string Text, char Role)> lines =
        [
            ("before the blocks", 'n'),
            ("décl one 😀", 'h'),
            .. many.Select(line => (line, 'b')),
            ("outside", 'n'),
            ("decl two", 'h'),
            ("  two's line", 'b'),
            ("decl alone", 'h'),
            ("decl three", 'h'),
            ("  three's last line", 'b'),
        ];

        async Task WriteAsync(ResultLines into, IEnumerable<(string Text, char Role)> written)
        {
            foreach (var (text, role) in written)
            {
                await (role switch { 'h' => into.HeadAsync(text, token), 'b' => into.BlockLineAsync(text, token), _ => into.LineAsync(text, token) });
            }
        }

        await using (var blocked = records.Lines("blocked"))
        {
            await WriteAsync(blocked, lines);
            await blocked.EndAsync();

            var masked = lines.Select(line => line.Text.Replace("hunter2", "***", StringComparison.Ordinal)).ToList();
            var starts = new List<long> { 0 };

            foreach (var line in masked)
            {
                starts.Add(starts[^1] + Encoding.UTF8.GetByteCount(line) + 1);
            }

            var length = starts[^1] - 1;
            int At(string text) => lines.FindIndex(line => line.Text == text);

            Assert.Equal(string.Join('\n', masked), File.ReadAllText(blocked.File!));
            Assert.Equal(
                [
                    "{\"schema\":1}",
                    $"{{\"head\":{starts[1]},\"body\":{starts[2]},\"next\":{starts[At("outside")]}}}",
                    $"{{\"head\":{starts[At("decl two")]},\"body\":{starts[At("decl two") + 1]},\"next\":{starts[At("decl alone")]}}}",
                    $"{{\"head\":{starts[At("decl three")]},\"body\":{starts[At("decl three") + 1]},\"next\":{length}}}",
                    $"{{\"digest\":\"{FileContentHash.Of(File.ReadAllBytes(blocked.File!))}\"}}",
                ],
                File.ReadAllLines(DssHarness.Core.Pages.TextBlocks.FileOf(blocked.File!)));
        }

        // One that fits a page is given whole: no file, and no blocks beside one.
        await using (var short_ = records.Lines("short"))
        {
            await WriteAsync(short_, [("decl", 'h'), ("  its line", 'b')]);
            await short_.EndAsync();

            Assert.Equal(("decl\n  its line", (string?)null), (short_.Text, short_.File));
            Assert.False(File.Exists(DssHarness.Core.Pages.TextBlocks.FileOf(AgentCallRecords.ResultFile(run.StepDirectory, "short"))));
        }

        // One abandoned before its end leaves neither its file nor its blocks.
        await using (var abandoned = records.Lines("abandoned"))
        {
            await WriteAsync(abandoned, lines);
        }

        Assert.False(File.Exists(AgentCallRecords.ResultFile(run.StepDirectory, "abandoned")));
        Assert.False(File.Exists(DssHarness.Core.Pages.TextBlocks.FileOf(AgentCallRecords.ResultFile(run.StepDirectory, "abandoned"))));

        await using var headless = records.Lines("headless");
        await headless.LineAsync("of no block", token);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await headless.BlockLineAsync("  of a block", token));

        Assert.Equal("A line of a block follows its head, and no block is open: the line written last is of none.", refused.Message);
    }

    /// <summary>A denial is a line of denials.jsonl and an event, masked, its source as the event names it.</summary>
    [Fact]
    public async Task ADenial_IsALineAndAnEvent_Masked()
    {
        var records = Records(out var events, out var run, new Masking("hunter2", "***"));

        using (records)
        {
            await records.DeniedAsync("view_file", "secrets/hunter2", new AgentPolicyAnswer.Denied("Read(secrets/**)", AgentPolicySource.Config, "'secrets/hunter2' is denied"));
        }

        var denied = Assert.IsType<PermissionDenied>(Assert.Single(events.All));
        Assert.Equal(new PermissionDenied("view_file", "secrets/***", "Read(secrets/**)", AgentPermissionSource.Config, "'secrets/***' is denied") { At = denied.At, Seq = denied.Seq }, denied);

        // Read once the records are closed: a log held open for its lines is not shared on Windows.
        var line = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(run.StepDirectory, AgentCallRecords.DenialsFileName))))!;
        Assert.Equal("secrets/***", line["subject"]!.GetValue<string>());
        Assert.Equal("Config", line["source"]!.GetValue<string>());
        Assert.Equal("'secrets/***' is denied", line["reason"]!.GetValue<string>());
    }

    /// <summary>A request of the web is a line when it is decided and one when it is answered; a refused one is said by its event at once.</summary>
    [Fact]
    public async Task ARequestOfTheWeb_IsALineDecided_AndOneAnswered()
    {
        var records = Records(out var events, out var run);

        using (records)
        {
            await records.WebDecidedAsync("web_fetch", "fetch", "https://example.com/", AgentPolicyAnswer.Allow);
            await records.WebAnsweredAsync("web_fetch", "fetch", "https://example.com/", 200, "text/html", 1234, failed: null);
            await records.WebDecidedAsync("web_fetch", "fetch", "https://pastebin.com/", new AgentPolicyAnswer.Denied("Web(pastebin.com)", AgentPolicySource.Config, "refused"));
        }

        var lines = File.ReadAllLines(Path.Combine(run.StepDirectory, AgentCallRecords.WebFileName)).Select(line => JsonNode.Parse(line)!).ToList();
        Assert.Equal(["decided", "answered", "decided"], lines.Select(line => line["line"]!.GetValue<string>()));
        Assert.True(lines[0]["allowed"]!.GetValue<bool>());
        Assert.Equal((200, "fetch"), (lines[1]["status"]!.GetValue<int>(), lines[1]["by"]!.GetValue<string>()));
        Assert.False(lines[2]["allowed"]!.GetValue<bool>());
        Assert.Equal("Web(pastebin.com)", lines[2]["rule"]!.GetValue<string>());

        Assert.Equal(
            [new WebRequested("https://example.com/", AgentToolRunner.Harness, true, 200, 1234), new WebRequested("https://pastebin.com/", AgentToolRunner.Harness, false)],
            events.All.Cast<WebRequested>().Select(@event => @event with { At = default, Seq = 0 }));
    }

    /// <summary>
    /// A request of the web decided by an answer that neither allows nor denies is recorded refused, as an undecided denial
    /// and its event: a record never says allowed of what the policy did not allow.
    /// </summary>
    [Fact]
    public async Task ARequestDecidedByNoAnswerThePolicyHas_IsRecordedRefused()
    {
        var records = Records(out var events, out var run);

        using (records)
        {
            await records.WebDecidedAsync("web_fetch", "fetch", "https://example.com/", ThirdAnswer.Made());
        }

        var line = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(run.StepDirectory, AgentCallRecords.WebFileName))))!;
        Assert.False(line["allowed"]!.GetValue<bool>());
        Assert.Equal(AgentPolicy.UndecidedRule, line["rule"]!.GetValue<string>());
        Assert.Equal("this tool answered 'ThirdAnswer', which neither allows nor denies: it denies what it cannot read", line["reason"]!.GetValue<string>());
        Assert.Equal(
            [new WebRequested("https://example.com/", AgentToolRunner.Harness, false)],
            events.All.Cast<WebRequested>().Select(@event => @event with { At = default, Seq = 0 }));
    }

    /// <summary>
    /// An address that holds a secret behind its escapes - carried in another address's parameter, escaped once more - is
    /// recorded masked at the reading that undoes them, decided, answered or refused; one that holds none as it was asked.
    /// </summary>
    [Fact]
    public async Task AnAddressHoldingASecretBehindItsEscapes_IsRecordedMasked()
    {
        const string Secret = "s3/cr+et";
        var carried = "https://sso.example.test/login?return_to=" + Uri.EscapeDataString("https://api.example.test/?key=" + Uri.EscapeDataString(Secret));
        const string Masked = "https://sso.example.test/login?return_to=https://api.example.test/?key=***";
        var records = Records(out var events, out var run, new Masking(Secret, "***"));

        using (records)
        {
            await records.WebDecidedAsync("web_search", "beta", carried, Core.Agents.Runtime.AgentToolRuntime.SecretAddress);
            await records.WebAnsweredAsync("web_search", "beta", carried, 302, null, null, failed: null);
            await records.WebAnsweredAsync("web_search", "beta", "https://api.example.test/", 302, null, null, failed: $"a redirect (302) to '{carried}', which reads as no address");
            await records.DeniedAsync("web_search", carried, Core.Agents.Runtime.AgentToolRuntime.SecretAddress);
            await records.WebDecidedAsync("web_search", "beta", "https://api.example.test/?q=a%2Fb", AgentPolicyAnswer.Allow);
        }

        var lines = File.ReadAllLines(Path.Combine(run.StepDirectory, AgentCallRecords.WebFileName)).Select(line => JsonNode.Parse(line)!).ToList();
        var denial = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(run.StepDirectory, AgentCallRecords.DenialsFileName))))!;

        Assert.Equal([Masked, Masked, "https://api.example.test/", "https://api.example.test/?q=a%2Fb"], lines.Select(line => line["address"]!.GetValue<string>()));
        Assert.Equal($"a redirect (302) to '{Masked}', which reads as no address", lines[2]["failed"]!.GetValue<string>());
        Assert.Equal(Masked, denial["subject"]!.GetValue<string>());
        Assert.Equal([Masked, Masked, "https://api.example.test/"], events.All.OfType<WebRequested>().Select(requested => requested.QueryOrAddress));
    }

    /// <summary>
    /// A source's part of a search is a line of its own - its kind, outcome, results and why, the query and every word
    /// masked - and an event; an answer the kind could not read is a notice too, naming the source and the kind's version.
    /// </summary>
    [Fact]
    public async Task ASourcesPart_IsALineAndAnEvent_AChangeANotice()
    {
        var records = Records(out var events, out var run, new Masking("s3cret"));
        var found = DssHarness.Core.Web.Search.WebSearchAnswer.Read([new("T", "https://t.example.test/", string.Empty)]);

        using (records)
        {
            await records.WebSearchedAsync(new("alpha", "json-api", 1, "group by", found, Withheld: 2, Cached: true, KeyMissing: "ALPHA_KEY s3cret is not in the slot"));
            await records.WebSearchedAsync(new("beta", "feed", 1, "q s3cret", DssHarness.Core.Web.Search.WebSearchAnswer.Changed("its answer is no feed: s3cret"), 0, false, null));
            await records.WebSearchedAsync(new("gamma", "feed", 1, "q", DssHarness.Core.Web.Search.WebSearchAnswer.Refused("past the step's bound"), 0, false, null));
        }

        var text = File.ReadAllText(Path.Combine(run.StepDirectory, AgentCallRecords.WebFileName));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToList();

        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Equal(("searched", "web_search"), (line["line"]!.GetValue<string>(), line["tool"]!.GetValue<string>())));
        Assert.Equal(["alpha", "beta", "gamma"], lines.Select(line => line["by"]!.GetValue<string>()));
        Assert.Equal(("json-api", 1, "group by", "Ok", 1, 2, true), (lines[0]["kind"]!.GetValue<string>(), lines[0]["version"]!.GetValue<int>(), lines[0]["query"]!.GetValue<string>(), lines[0]["outcome"]!.GetValue<string>(), lines[0]["results"]!.GetValue<int>(), lines[0]["withheld"]!.GetValue<int>(), lines[0]["cached"]!.GetValue<bool>()));
        Assert.Equal("ALPHA_KEY *** is not in the slot", lines[0]["keyMissing"]!.GetValue<string>());
        Assert.Equal(("Changed", "its answer is no feed: ***"), (lines[1]["outcome"]!.GetValue<string>(), lines[1]["why"]!.GetValue<string>()));

        Assert.Equal(
            [new WebRequested("group by", AgentToolRunner.Harness, true), new WebRequested("q ***", AgentToolRunner.Harness, true), new WebRequested("q", AgentToolRunner.Harness, false)],
            events.All.OfType<WebRequested>().Select(@event => @event with { At = default, Seq = 0 }));
        Assert.Equal(
            "web-search source 'beta' answered what its kind 'feed' (version 1) could not read: its answer is no feed: ***",
            Assert.Single(events.All.OfType<Notice>()).Text);
    }

    /// <summary>A record opens its file at its first line: a step that refused nothing and asked no page has neither file.</summary>
    [Fact]
    public void ARecordsFiles_AreOpenedAtTheirFirstLine()
    {
        using (Records(out _, out var run))
        {
            Assert.False(File.Exists(Path.Combine(run.StepDirectory, AgentCallRecords.DenialsFileName)));
            Assert.False(File.Exists(Path.Combine(run.StepDirectory, AgentCallRecords.WebFileName)));
        }
    }

    private AgentCallRecords Records(out RecordedEvents events, out AgentRun run, ILineMask? mask = null)
    {
        events = new RecordedEvents();
        run = AgentRuns.Of(_temp.Combine("step"), _temp.Path, mask: mask);
        return new AgentCallRecords(new PhysicalFileSystem(FilePermissionsFactory.Create()), run, TimeProvider.System, events);
    }
}
