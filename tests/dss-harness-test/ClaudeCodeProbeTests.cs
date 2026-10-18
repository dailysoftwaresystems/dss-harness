using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace DssHarness.Tests;

/// <summary>
/// The Claude Code probe (part 03's slice 12): a live <c>claude</c>, streamed both ways in a scratch repository,
/// against the stable channel's version and the latest, started by this test - which the suite skips unless the
/// machine says it may run it, for it uses the person's login and runs a live program. The engine never starts it.
/// </summary>
/// <remarks>
/// <para>
/// It records everything it sees and everything it writes under <c>fixtures/claude-code/&lt;version&gt;/</c> in the
/// test tree, and the no-network tests of this slice read those recordings: the stream reader and the fake
/// <c>claude</c> are built from them. It resumes nothing, changes nothing built, and installs nothing global - each
/// version comes from a scratch npm prefix of its own, named by <see cref="ExecutableVariable"/>.
/// </para>
/// <para>
/// Set <see cref="RequiredVariable"/> to run it; the recordings it leaves are committed, so an ordinary run of the
/// suite never needs it again. It is local and person-run only: a continuous-integration runner never sets the
/// variable - it has no login and must not - so the probe is skipped there by design, and the suite's coverage of
/// the protocol is the committed fixtures and the fake <c>claude</c>, both offline.
/// </para>
/// </remarks>
public sealed class ClaudeCodeProbeTests
{
    /// <summary>The environment variable that says this machine may run the live probe, naming the program file of the stable version's scratch install (the latest is looked for beside it).</summary>
    public const string RequiredVariable = "DSSHARNESS_TESTS_LIVE_CLAUDE";

    /// <summary>The environment variable that names the stable version's scratch <c>claude</c>, as a probe run takes it.</summary>
    public const string ExecutableVariable = "DSSHARNESS_TESTS_LIVE_CLAUDE";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Whether this run is the fast one: the initialize handshake and a first turn only, for shaping the protocol by hand.</summary>
    public const string SmokeVariable = "DSSHARNESS_TESTS_LIVE_CLAUDE_SMOKE";

    /// <summary>
    /// Runs the whole probe against both versions and records the fixtures. One run of it is enough for a lifetime of
    /// the suite: what it leaves is what the no-network tests read.
    /// </summary>
    [Fact]
    public async Task TheLiveProbe_RecordsTheFixturesOfBothVersions()
    {
        var stable = Environment.GetEnvironmentVariable(ExecutableVariable);

        Assert.SkipWhen(string.IsNullOrWhiteSpace(stable), $"{RequiredVariable} is not set: the live probe runs only where the person said it might.");

        var latest = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(stable)))))!,
            "latest",
            "node_modules",
            "@anthropic-ai",
            "claude-code",
            "bin",
            Path.GetFileName(stable));
        var smoke = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SmokeVariable));

        await ProbeAsync("stable", stable, Token, smoke);

        if (!smoke)
        {
            await ProbeAsync("latest", latest, Token, smoke);
        }
    }

    /// <summary>One probe of one version: the whole scenario, everything recorded.</summary>
    private static async Task ProbeAsync(string channel, string claude, CancellationToken cancellationToken, bool smoke)
    {
        var fixtures = Fixtures(channel);
        var raw = Path.Combine(fixtures, "raw");

        // The raw recordings live under raw/ and only there: the curated shapes.jsonl and probe.md beside
        // it are hand-written from them, and a regeneration must not be able to delete them.
        Directory.CreateDirectory(raw);

        foreach (var leftover in Directory.GetFiles(raw))
        {
            File.Delete(leftover);
        }

        using var repo = new TempDirectory();
        await RunAsync(repo.Path, "git", ["init", "-q"], cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "alpha\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "probe.txt"), "the probe's own file\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "CLAUDE.md"), "A marker line for the probe: CLAUDE_MD_MARKER_7f3a.\n", cancellationToken);
        await RunAsync(repo.Path, "git", ["add", "-A"], cancellationToken);
        await RunAsync(repo.Path, "git", ["-c", "user.email=probe@example.com", "-c", "user.name=probe", "commit", "-q", "-m", "probe"], cancellationToken);

        var settings = Path.Combine(raw, "claude-settings.json");
        await File.WriteAllTextAsync(settings, Settings(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(raw, "system.md"), "You are the subject of a protocol probe. Be terse.\n", cancellationToken);

        var child = new Process
        {
            StartInfo = new ProcessStartInfo(claude)
            {
                WorkingDirectory = repo.Path,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in Arguments(raw))
        {
            child.StartInfo.ArgumentList.Add(argument);
        }

        // The marker the settings' env also sets: which of the two the child's tools see is one of the probe's answers.
        child.StartInfo.Environment["PROBE_MARKER"] = "process";

        var started = Stopwatch.StartNew();
        child.Start();

        var stream = new List<string>();
        var written = new List<string>();
        var stderr = new List<string>();

        var reading = Task.Run(() => ReadAll(child.StandardOutput, stream), cancellationToken);
        var failing = Task.Run(() => ReadAll(child.StandardError, stderr), cancellationToken);

        string SessionFiles()
        {
            // Where claude keeps a project's sessions, named as it names them: the working
            // directory's path with every separator and colon a dash.
            var slug = repo.Path.Replace(':', '-').Replace('\\', '-').Replace('/', '-');
            var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", slug);

            return Directory.Exists(projects) ? string.Join(", ", Directory.GetFiles(projects, "*.jsonl").Select(Path.GetFileName)) : "none";
        }

        try
        {
            // The initialize request the SDK sends before anything else; what comes back is one of the recordings.
            await WriteAsync(child, written, new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = "probe-init",
                ["request"] = new JsonObject
                {
                    ["subtype"] = "initialize",
                    ["protocolVersion"] = "1",
                    ["clientInfo"] = new JsonObject { ["name"] = "dssharness-probe", ["version"] = "0" },
                },
            }, cancellationToken);

            await UntilAsync(() => Snapshot(stream).Any(line => line.Contains("\"type\":\"control_response\"", StringComparison.Ordinal) || line.Contains("\"subtype\"", StringComparison.Ordinal)), TimeSpan.FromSeconds(30), "the initialize answer", cancellationToken);

            // (a) A first turn that reads, under the ask rules: the host is asked, allows, and the turn ends in a result.
            await TurnAsync(child, written, stream, "Read the file a.txt and say only what text it holds.", allow: true, TimeSpan.FromSeconds(180), cancellationToken);

            if (smoke)
            {
                throw new OperationCanceledException("the smoke run ends here");
            }

            // (b) A second message after the first result: answered on the same stream (point a of the probe).
            await TurnAsync(child, written, stream, "In one word, what colour is the sky?", allow: true, TimeSpan.FromSeconds(180), cancellationToken);

            // (c) A turn ended by the interrupt request, and then a further message answered with what it held before.
            await WriteAsync(child, written, new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Count slowly from one to five hundred, one number per line." },
            }, cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var interruptedAt = Snapshot(stream).Count;

            await WriteAsync(child, written, new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = "probe-interrupt",
                ["request"] = new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = true },
            }, cancellationToken);

            await UntilAsync(
                () => Snapshot(stream).Skip(interruptedAt).Any(line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(60),
                "the interrupted turn's result",
                cancellationToken);

            await TurnAsync(child, written, stream, "Without counting again: which file did you read first in this session?", allow: true, TimeSpan.FromSeconds(180), cancellationToken);

            // (d) The four kinds of call under the ask rules: allowed ones run, denied ones do not.
            await TurnAsync(child, written, stream, "Write the text 'written by the probe' into a new file named written.txt.", allow: true, TimeSpan.FromSeconds(180), cancellationToken);
            await TurnAsync(child, written, stream, "Write the text 'denied' into a new file named denied.txt.", allow: false, TimeSpan.FromSeconds(180), cancellationToken);
            await TurnAsync(child, written, stream, "Run exactly this command in the Bash tool, changing nothing: echo probe-ran > ran.txt; echo marker=$PROBE_MARKER", allow: true, TimeSpan.FromSeconds(180), cancellationToken);
            await TurnAsync(child, written, stream, "Search the web for: dssharness claude code probe", allow: true, TimeSpan.FromSeconds(240), cancellationToken);

            // (e) A call the host never answers: nothing runs while the host is silent. The child is ended whole after.
            await WriteAsync(child, written, new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Read the file probe.txt and say what it holds." },
            }, cancellationToken);

            var before = Snapshot(stream).Count;
            await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken);
            var grew = Snapshot(stream).Skip(before).Any(line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));

            await File.WriteAllTextAsync(
                Path.Combine(raw, "silent-host.json"),
                $"{new JsonObject { ["ranWhileHostSilent"] = grew, ["waitedSeconds"] = 45 }}\n",
                cancellationToken);
        }
        finally
        {
            try
            {
                child.StandardInput.Close();
                child.WaitForExit(5_000);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already gone.
            }

            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5_000);
            }

            // The recordings are written before anything is joined: what the child said is the evidence a
            // failure needs, and a reader that will not end must not keep it from the disk.
            await File.WriteAllLinesAsync(Path.Combine(raw, "stream.jsonl"), stream, CancellationToken.None);
            await File.WriteAllLinesAsync(Path.Combine(raw, "control.jsonl"), written, CancellationToken.None);
            await File.WriteAllLinesAsync(Path.Combine(raw, "claude-stderr.log"), stderr, CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(raw, "findings.json"),
                $"{new JsonObject { ["channel"] = channel, ["seconds"] = Math.Round(started.Elapsed.TotalSeconds, 1), ["wroteDenied"] = File.Exists(Path.Combine(repo.Path, "denied.txt")), ["wroteWritten"] = File.Exists(Path.Combine(repo.Path, "written.txt")), ["wroteRan"] = File.Exists(Path.Combine(repo.Path, "ran.txt")), ["sessionFilesWithoutDashP"] = SessionFiles() }}\n",
                CancellationToken.None);

            try
            {
                await Task.WhenAll(reading, failing).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // A reader that outlived the child holds no more evidence than what is already written.
            }
        }
    }

    /// <summary>One turn: the user message, every <c>can_use_tool</c> it raises answered as asked, until the turn's result.</summary>
    private static async Task TurnAsync(Process child, List<string> written, List<string> stream, string message, bool allow, TimeSpan bound, CancellationToken cancellationToken)
    {
        var before = Snapshot(stream).Count;
        var answered = new HashSet<string>(StringComparer.Ordinal);

        await WriteAsync(child, written, new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = message },
        }, cancellationToken);

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waiting.CancelAfter(bound);

        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), waiting.Token);

            foreach (var line in Snapshot(stream).Skip(before))
            {
                if (!line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal))
                {
                    continue;
                }

                var asked = JsonNode.Parse(line);
                var id = asked?["request_id"]?.GetValue<string>();

                if (id is null || !answered.Add(id))
                {
                    continue;
                }

                await WriteAsync(child, written, new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject
                    {
                        ["subtype"] = "success",
                        ["request_id"] = id,
                        ["response"] = allow
                            ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = asked?["request"]?["input"]?.DeepClone() }
                            : new JsonObject { ["behavior"] = "deny", ["message"] = "denied by the probe" },
                    },
                }, cancellationToken);
            }

            if (Snapshot(stream).Skip(before).Any(line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal)))
            {
                return;
            }
        }
    }

    /// <summary>A copy of the lines read so far, safe to read while the reader goes on.</summary>
    private static List<string> Snapshot(List<string> stream)
    {
        lock (stream)
        {
            return [.. stream];
        }
    }

    /// <summary>Writes one line to the child's input, recording it as written.</summary>
    private static async Task WriteAsync(Process child, List<string> written, JsonObject message, CancellationToken cancellationToken)
    {
        var line = message.ToJsonString(DssHarness.Core.FileSystem.JsonStateFile.LineOptions);

        await child.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await child.StandardInput.FlushAsync(cancellationToken);
        written.Add(line);
    }

    /// <summary>Reads every line of <paramref name="from"/> into <paramref name="into"/>, until the stream ends.</summary>
    private static void ReadAll(StreamReader from, List<string> into)
    {
        string? line;

        while ((line = from.ReadLine()) is not null)
        {
            lock (into)
            {
                into.Add(line);
            }
        }
    }

    /// <summary>Waits until <paramref name="holds"/> does, or fails the probe saying what it waited for.</summary>
    private static async Task UntilAsync(Func<bool> holds, TimeSpan bound, string what, CancellationToken cancellationToken)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        token.CancelAfter(bound);

        while (!holds())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), token.Token).ConfigureAwait(false);
        }
    }

    /// <summary>The launch's argument list, whole, as part 03's Permissions spell it.</summary>
    private static IEnumerable<string> Arguments(string raw) =>
    [
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--replay-user-messages",
        "--session-id", Guid.NewGuid().ToString("D"),
        "--no-session-persistence",
        "--model", "sonnet",
        "--effort", "low",
        "--max-turns", "10",
        "--permission-mode", "default",
        "--permission-prompt-tool", "stdio",
        "--permission-prompts", "host",
        "--tools", "Read,Grep,Glob,Write,Edit,NotebookEdit,Bash,WebSearch,WebFetch",
        "--setting-sources", "user",
        "--strict-mcp-config",
        "--settings", Path.Combine(raw, "claude-settings.json"),
        "--append-system-prompt-file", Path.Combine(raw, "system.md"),
        "--json-schema", "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}",
    ];

    /// <summary>The launch's own settings, as sent: ask rules for every tool, the engine's env, nothing else.</summary>
    private static string Settings()
    {
        var ask = new JsonArray();

        foreach (var tool in new[] { "Read", "Grep", "Glob", "Write", "Edit", "NotebookEdit", "Bash", "WebSearch", "WebFetch" })
        {
            ask.Add(tool);
        }

        return new JsonObject
        {
            ["permissions"] = new JsonObject { ["ask"] = ask },
            ["fallbackModel"] = new JsonArray(),
            ["switchModelsOnFlag"] = false,
            ["outputStyle"] = "default",
            ["env"] = new JsonObject
            {
                ["DISABLE_COMPACT"] = "1",
                ["DISABLE_AUTOUPDATER"] = "1",
                ["CLAUDE_CODE_SUBPROCESS_ENV_SCRUB"] = "1",
                ["CLAUDE_CODE_RETRY_WATCHDOG"] = "0",
                ["PROBE_MARKER"] = "settings",
            },
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>Where one version's fixtures are kept, in the test tree.</summary>
    private static string Fixtures(string channel)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !directory.EnumerateFiles("DssHarness.slnx").Any())
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "dss-harness-test", "fixtures", "claude-code", channel);
    }

    /// <summary>Runs one short command to set the scratch repository up.</summary>
    private static async Task RunAsync(string directory, string program, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var done = Process.Start(start)!;
        await done.WaitForExitAsync(cancellationToken);

        if (done.ExitCode != 0)
        {
            throw new InvalidOperationException($"{program} {string.Join(' ', arguments)} failed: {done.ExitCode}.");
        }
    }
}
