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
            await records.WebAnsweredAsync("web_fetch", "https://example.com/", 200, "text/html", 1234, failed: null);
            await records.WebDecidedAsync("web_fetch", "fetch", "https://pastebin.com/", new AgentPolicyAnswer.Denied("Web(pastebin.com)", AgentPolicySource.Config, "refused"));
        }

        var lines = File.ReadAllLines(Path.Combine(run.StepDirectory, AgentCallRecords.WebFileName)).Select(line => JsonNode.Parse(line)!).ToList();
        Assert.Equal(["decided", "answered", "decided"], lines.Select(line => line["line"]!.GetValue<string>()));
        Assert.True(lines[0]["allowed"]!.GetValue<bool>());
        Assert.Equal(200, lines[1]["status"]!.GetValue<int>());
        Assert.False(lines[2]["allowed"]!.GetValue<bool>());
        Assert.Equal("Web(pastebin.com)", lines[2]["rule"]!.GetValue<string>());

        Assert.Equal(
            [new WebRequested("https://example.com/", AgentToolRunner.Harness, true, 200, 1234), new WebRequested("https://pastebin.com/", AgentToolRunner.Harness, false)],
            events.All.Cast<WebRequested>().Select(@event => @event with { At = default, Seq = 0 }));
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
