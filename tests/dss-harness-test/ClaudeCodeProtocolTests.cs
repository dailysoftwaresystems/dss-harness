using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace DssHarness.Tests;

/// <summary>
/// The recorded protocol of Claude Code, as the probe of part 03's slice 12 took it: the curated shapes of both
/// channels hold every line kind and every one of the five points, and the fake <c>claude</c> - built from those
/// shapes - asks and waits as the real one does. Nothing here runs anything live or asks the network; the one live
/// test that made the recordings is the machine's own and never runs in continuous integration, where no login is.
/// </summary>
public sealed class ClaudeCodeProtocolTests
{
    /// <summary>Where the curated recordings live, in the test tree.</summary>
    private static string Fixtures(string channel, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !directory.EnumerateFiles("DssHarness.slnx").Any())
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "dss-harness-test", "fixtures", "claude-code", channel, file);
    }

    /// <summary>
    /// Both channels' recordings parse, and hold what a launch is: the tools as it names them - the schema's own
    /// <c>StructuredOutput</c> among them - the model <c>--model</c> resolved to, and the version of the program.
    /// </summary>
    [Theory]
    [InlineData("stable", "2.1.292")]
    [InlineData("latest", "2.1.302")]
    public void TheRecordingOfAChannel_HoldsWhatALaunchBeganAs(string channel, string version)
    {
        var began = ClaudeCodeProbe.Read(Fixtures(channel, "shapes.jsonl"))
            .Select(ClaudeCodeProbe.BeganOf)
            .OfType<ClaudeCodeProbe.Began>()
            .First();

        Assert.Equal(version, began.Version);
        Assert.Equal("claude-sonnet-5-5", began.Model);
        Assert.Equal("none", began.ApiKeySource);
        Assert.Equal(
            ["Bash", "Edit", "Glob", "Grep", "NotebookEdit", "Read", "StructuredOutput", "WebFetch", "WebSearch", "Write"],
            began.Tools);
        Assert.Contains("interrupt_receipt_v1", began.Capabilities, StringComparer.Ordinal);
    }

    /// <summary>
    /// The curated recordings are the public repository's, so they keep nothing of the account that made them: every id
    /// a server gave - a request's, a message's, a tool call's - and every uuid is a stable stand-in, the same one
    /// wherever the recording names the same thing, and the account's own members of a rate limit are elided. A
    /// regeneration that forgot to scrub them is refused here.
    /// </summary>
    [Theory]
    [InlineData("stable")]
    [InlineData("latest")]
    public void TheCuratedRecording_KeepsNothingOfTheAccountThatMadeIt(string channel)
    {
        var text = File.ReadAllText(Fixtures(channel, "shapes.jsonl"));

        // An id is a long run after its prefix, which a word of the protocol (msg_lifecycle_v1) is not.
        var ids = System.Text.RegularExpressions.Regex.Matches(text, @"\b(?:req|msg|toolu|srvtoolu)_[A-Za-z0-9]{16,}").Select(found => found.Value).ToList();
        var uuids = System.Text.RegularExpressions.Regex.Matches(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Select(found => found.Value).ToList();

        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.Matches("^(req|msg|toolu|srvtoolu)_[0-9]{24}$", id));
        Assert.All(uuids, uuid => Assert.StartsWith("00000000-0000-4000-8000-", uuid, StringComparison.Ordinal));

        // The stand-ins still pair up: every call a result or an ask names is a call a message made.
        var lines = ClaudeCodeProbe.Read(Fixtures(channel, "shapes.jsonl"));
        var made = lines
            .Where(line => line.Kind == "assistant")
            .SelectMany(line => (line.Node["message"]?["content"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(block => block["type"]?.GetValue<string>() == "tool_use")
            .Select(block => block["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        var asked = lines.Select(ClaudeCodeProbe.AskOf).OfType<ClaudeCodeProbe.Ask>().Select(ask => ask.ToolUseId).ToList();

        Assert.NotEmpty(asked);
        Assert.All(asked, call => Assert.Contains(call, made));

        var limits = lines.Where(line => line.Kind == "rate_limit_event").Select(line => line.Node["rate_limit_info"]!).ToList();

        Assert.NotEmpty(limits);
        Assert.All(limits, limit =>
        {
            Assert.Equal("<elided: the account's own>", limit["overageStatus"]!.GetValue<string>());
            Assert.Equal("<elided: the account's own>", limit["overageDisabledReason"]!.GetValue<string>());
        });
    }

    /// <summary>
    /// The recordings hold the five points: asks for every kind of call, their allowed tool results, a denial the
    /// host made and the result that counts it, an interrupt that ends the turn under way, a turn answered after
    /// it, and a call that failed because the host never answered.
    /// </summary>
    [Fact]
    public void TheRecording_HoldsTheFivePoints()
    {
        var lines = ClaudeCodeProbe.Read(Fixtures("stable", "shapes.jsonl"));
        var asks = lines.Select(ClaudeCodeProbe.AskOf).OfType<ClaudeCodeProbe.Ask>().ToList();
        var ends = lines.Select(ClaudeCodeProbe.EndOf).OfType<ClaudeCodeProbe.Ended>().ToList();

        // The four kinds of call, each asked: a read, a write (allowed and denied), a command, a search.
        Assert.Equal(["Read", "Write", "Write", "Bash", "WebSearch", "Read"], asks.Select(ask => ask.Tool));

        // (a) Two turns answered before anything else happened.
        Assert.Equal(2, ends.Take(2).Count(end => end.TerminalReason == "completed"));

        // (b) The interrupt ends the turn under way.
        Assert.Contains(ends, end => end.TerminalReason == "aborted_streaming");

        // (c) A turn completed after the interrupted one, the answer holding what went before.
        var aborted = ends.FindIndex(end => end.TerminalReason == "aborted_streaming");
        Assert.NotEqual(-1, aborted);
        Assert.Contains(ends.Skip(aborted + 1), end => end.TerminalReason == "completed");

        // (d) The denial the host made is counted by the turn's own result.
        Assert.Contains(ends, end => end.Denials == 1);

        // (e) The call the host never answered fails on the child's own, and nothing ran.
        var contents = lines
            .Where(line => line.Kind == "user")
            .SelectMany(line => (line.Node["message"]?["content"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Where(block => block["type"]?.GetValue<string>() == "tool_result")
                .Select(block => block["content"]?.GetValue<string>() ?? ""))
            .ToList();

        Assert.Contains(contents, content => content.Contains("denied by the probe", StringComparison.Ordinal));
        Assert.Contains(contents, content => content.Contains("marker=settings", StringComparison.Ordinal));
        Assert.Contains(contents, content => content.Contains("permission request failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The fake answers the initialize handshake with the recorded envelope, asks about the read, and runs it when
    /// the host allows - ending the turn completed, the schema's envelope in the result, no denial counted.
    /// </summary>
    [Fact]
    public async Task TheFakeAsksAndWaits_AnAllowRunsTheRead_AndTheTurnEndsCompleted()
    {
        using var child = Fake();
        await WriteAsync(child, """
            {"type":"control_request","request_id":"host-1","request":{"subtype":"initialize"}}
            """);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"init\"", StringComparison.Ordinal));

        await WriteAsync(child, """
            {"type":"user","message":{"role":"user","content":"read a.txt"}}
            """);
        var asked = await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        Assert.Contains("\"tool_name\":\"Read\"", asked, StringComparison.Ordinal);

        await WriteAsync(child, """
            {"type":"control_response","response":{"subtype":"success","request_id":"00000000-0000-4000-8000-000000000002","response":{"behavior":"allow","updatedInput":{"file_path":"a.txt"}}}}
            """);
        var ended = await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));

        // Every line the fake said is a line of JSON, as the recording's are: a line that cannot be
        // parsed is a shape a reader of the stream would choke on, whatever the test asked.
        lock (child.Seen)
        {
            Assert.All(child.Seen, line => Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(line)));
        }

        Assert.Contains("\"terminal_reason\":\"completed\"", ended, StringComparison.Ordinal);
        Assert.Contains("\"permission_denials\":[]", ended, StringComparison.Ordinal);
        Assert.Contains("{\"answer\":\"alpha\"}", ended, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deny does not run the read, and the turn's own result counts the denial - the denial's entry naming the
    /// tool and the call it held, as the recording shows.
    /// </summary>
    [Fact]
    public async Task TheFakeAsksAndWaits_ADenyRunsNothing_AndCountsTheDenial()
    {
        using var child = Fake();
        await WriteAsync(child, """
            {"type":"user","message":{"role":"user","content":"read a.txt"}}
            """);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        await WriteAsync(child, """
            {"type":"control_response","response":{"subtype":"success","request_id":"00000000-0000-4000-8000-000000000002","response":{"behavior":"deny","message":"denied by the host"}}}
            """);
        var ended = await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));

        Assert.Contains("\"terminal_reason\":\"completed\"", ended, StringComparison.Ordinal);
        Assert.Contains("\"permission_denials\":[{\"tool_name\":\"Read\"", ended, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha", ended, StringComparison.Ordinal);
    }

    /// <summary>
    /// The interrupt ends the turn under way: the answer acknowledges it, and the turn's result says
    /// <c>aborted_streaming</c>, as the recording does.
    /// </summary>
    [Fact]
    public async Task TheFakeEndsItsTurn_OnTheInterrupt()
    {
        using var child = Fake();
        await WriteAsync(child, """
            {"type":"user","message":{"role":"user","content":"read a.txt"}}
            """);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        await WriteAsync(child, """
            {"type":"control_request","request_id":"host-2","request":{"subtype":"interrupt"}}
            """);
        var ended = await UntilAsync(child, line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));

        Assert.Contains("\"terminal_reason\":\"aborted_streaming\"", ended, StringComparison.Ordinal);
    }

    /// <summary>
    /// A call with the host silent is waited for: nothing more is written, as long as the silence holds - and an
    /// answer in a shape the recording does not know is the same silence, which is what the real child does to a
    /// flat envelope (<c>probe.md</c>: a flat shape is not heard at all). Each shape names the ask and an allow, so
    /// only the envelope, read as JSON, tells them from the one that is heard.
    /// </summary>
    [Theory]
    [InlineData("""{"type":"control_response","response_to":"00000000-0000-4000-8000-000000000002","response":{"behavior":"allow"}}""")]
    [InlineData("""{"type":"control_response","request_id":"00000000-0000-4000-8000-000000000002","behavior":"allow"}""")]
    [InlineData("""{"type":"control_response","response":{"request_id":"00000000-0000-4000-8000-000000000002","behavior":"allow"}}""")]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"00000000-0000-4000-8000-000000000002","behavior":"allow"}}""")]
    [InlineData("""{"type":"control_response","response":{"request_id":"00000000-0000-4000-8000-000000000002","response":{"behavior":"allow"}}}""")]
    public async Task TheFakeWaitsWhileTheHostIsSilent_AndDoesNotHearAShapeItDoesNotKnow(string shape)
    {
        using var child = Fake();
        await WriteAsync(child, """
            {"type":"user","message":{"role":"user","content":"read a.txt"}}
            """);
        await UntilAsync(child, line => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal));

        await WriteAsync(child, shape);

        var after = await ReadForAsync(child, TimeSpan.FromSeconds(2));

        Assert.DoesNotContain("\"type\":\"result\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"tool_result\"", after, StringComparison.Ordinal);
    }

    /// <summary>Starts the fake <c>claude</c>, this assembly as a child in its <c>claude-code</c> mode.</summary>
    private static FakeClaude Fake()
    {
        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            Arguments = $"exec \"{TestHost.AssemblyPath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        start.Environment[TestHost.ChildModeVariable] = "claude-code";

        return new FakeClaude(Process.Start(start)!);}

    /// <summary>Writes one line to the fake's input.</summary>
    private static async Task WriteAsync(FakeClaude child, string line)
    {
        await child.Process.StandardInput.WriteLineAsync(line.AsMemory(), TestContext.Current.CancellationToken);
        await child.Process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits for the next line that holds <paramref name="what"/>, failing the test where none comes.</summary>
    private static async Task<string> UntilAsync(FakeClaude child, Func<string, bool> what)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(30));

        while (true)
        {
            var line = await child.Lines.Reader.ReadAsync(bound.Token);

            lock (child.Seen)
            {
                child.Seen.Add(line);
            }

            if (what(line))
            {
                return line;
            }
        }
    }

    /// <summary>Reads all that comes inside <paramref name="bound"/>, and returns it as one text.</summary>
    private static async Task<string> ReadForAsync(FakeClaude child, TimeSpan bound)
    {
        _ = Task.Run(async () =>
        {
            while (await child.Lines.Reader.WaitToReadAsync(CancellationToken.None))
            {
                while (child.Lines.Reader.TryRead(out var line))
                {
                    lock (child.Seen)
                    {
                        child.Seen.Add(line);
                    }
                }
            }
        });

        await Task.Delay(bound, TestContext.Current.CancellationToken);

        lock (child.Seen)
        {
            return string.Join("\n", child.Seen);
        }
    }

    /// <summary>The fake child and what it has said.</summary>
    private sealed class FakeClaude : IDisposable
    {
        public FakeClaude(Process process)
        {
            Process = process;

            _ = Task.Run(async () =>
            {
                string? line;

                while ((line = process.StandardOutput.ReadLine()) is not null)
                {
                    await Lines.Writer.WriteAsync(line);
                }

                Lines.Writer.TryComplete();
            });
        }

        /// <summary>Everything the fake has said so far, safe to read while it goes on saying.</summary>
        public List<string> Seen { get; } = [];

        /// <summary>The lines as they come.</summary>
        public System.Threading.Channels.Channel<string> Lines { get; } = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

        public Process Process { get; }

        public void Dispose()
        {
            try
            {
                Process.StandardInput.Close();
                Process.WaitForExit(2_000);
            }
            catch (IOException)
            {
                // Already gone.
            }

            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(2_000);
            }

            Process.Dispose();
        }
    }
}
