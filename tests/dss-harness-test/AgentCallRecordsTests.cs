using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// What a step's calls leave beside its stored stream, on every client: a denial's line and event, a request of the web's
/// lines and event, a result too large for its event kept whole and cut.
/// </summary>
public sealed class AgentCallRecordsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A result no longer than the cap is carried whole and kept in no file; a longer one is its head and its tail, the whole kept.</summary>
    [Fact]
    public void AResultPastTheCap_IsItsHeadAndItsTail_TheWholeKept()
    {
        using var records = Records(out var events, out var run);

        var small = new string('s', AgentCallRecords.ResultCap);
        Assert.Equal((small, (string?)null), records.Kept("call-1", small));
        Assert.Equal(small, AgentCallRecords.Cut(small, "f"));

        var whole = string.Concat(Enumerable.Range(0, AgentCallRecords.ResultCap / 8).Select(at => at.ToString("D8", System.Globalization.CultureInfo.InvariantCulture))) + "tail-end";
        var (shown, file) = records.Kept("call-2", whole);

        Assert.Equal(AgentCallRecords.ResultFile(run.StepDirectory, "call-2"), file);
        Assert.Equal(whole, File.ReadAllText(file!));
        Assert.Equal(
            $"{whole[..AgentCallRecords.ResultHead]}\n[{whole.Length - AgentCallRecords.ResultCap} characters left out: the whole result is '{file}']\n{whole[^(AgentCallRecords.ResultCap - AgentCallRecords.ResultHead)..]}",
            shown);
        Assert.Empty(events.All);
    }

    /// <summary>A result is masked before it is carried or kept.</summary>
    [Fact]
    public void AResult_IsMaskedBeforeItIsCarriedOrKept()
    {
        using var records = Records(out _, out var run, new Masking("hunter2", "***"));

        var (shown, _) = records.Kept("c", "the key is hunter2");
        Assert.Equal("the key is ***", shown);

        var (cut, file) = records.Kept("d", "hunter2" + new string('x', AgentCallRecords.ResultCap));
        Assert.StartsWith("***", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", File.ReadAllText(file!), StringComparison.Ordinal);
    }

    /// <summary>The lines of a result cut as a result cut whole is, never held whole: the same text either way.</summary>
    [Fact]
    public void LinesCut_AreTheWholeCut()
    {
        foreach (var count in new[] { 1, 10, 4096, 9000, 40000 })
        {
            var lines = Enumerable.Range(0, count).Select(at => $"line {at}").ToList();
            Assert.Equal(AgentCallRecords.Cut(string.Join('\n', lines), "f"), AgentCallRecords.Cut(lines, "f"));
        }
    }

    /// <summary>No pair of surrogates is cut, at the head or at the tail.</summary>
    [Fact]
    public void ACut_NeverPartsAPairOfSurrogates()
    {
        var pair = "\U0001F600";
        var text = new string('a', AgentCallRecords.ResultHead - 1) + pair + new string('b', AgentCallRecords.ResultCap) + pair + new string('c', AgentCallRecords.ResultCap - AgentCallRecords.ResultHead - 1);
        var cut = AgentCallRecords.Cut(text, "f");

        for (var at = 0; at < cut.Length; at++)
        {
            Assert.False(char.IsHighSurrogate(cut[at]) && (at + 1 == cut.Length || !char.IsLowSurrogate(cut[at + 1])), $"a high surrogate stands alone at {at}");
            Assert.False(char.IsLowSurrogate(cut[at]) && (at == 0 || !char.IsHighSurrogate(cut[at - 1])), $"a low surrogate stands alone at {at}");
        }

        Assert.StartsWith(new string('a', AgentCallRecords.ResultHead - 1) + "\n[", cut, StringComparison.Ordinal);
    }

    /// <summary>A call's id names its file with every character no file name holds on every system made '_'.</summary>
    [Fact]
    public void ACallsId_NamesItsFile_AsEverySystemHoldsIt()
    {
        Assert.Equal(Path.Combine("s", "tool-results", "call_01-x.txt"), AgentCallRecords.ResultFile("s", "call_01-x"));
        Assert.Equal(Path.Combine("s", "tool-results", "______x.txt"), AgentCallRecords.ResultFile("s", "../:\\.x"));
        Assert.Equal(Path.Combine("s", "tool-results", "call.txt"), AgentCallRecords.ResultFile("s", string.Empty));
        Assert.Equal(96 + ".txt".Length, Path.GetFileName(AgentCallRecords.ResultFile("s", new string('a', 200))).Length);
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

    /// <summary>Lines cut begin with the first, an empty one among them: a command that printed a blank line first printed it.</summary>
    [Fact]
    public void LinesCut_KeepAnEmptyFirstLine()
    {
        Assert.Equal("\na", AgentCallRecords.Cut(["", "a"], "f"));
        Assert.Equal("\n", AgentCallRecords.Cut(["", ""], "f"));
        Assert.Equal(string.Empty, AgentCallRecords.Cut([""], "f"));
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
