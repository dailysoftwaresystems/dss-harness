using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace DssHarness.Tests;

/// <summary>
/// The Claude Code probe (part 03's slice 12): a live <c>claude</c>, streamed both ways in a scratch repository, against
/// the oldest version this tool takes, the stable channel's version and the latest, started by this test - which the
/// suite skips unless the machine says it may run it, for it uses the person's login and runs a live program. The
/// engine never starts it.
/// </summary>
/// <remarks>
/// <para>
/// Each fact below is one of the probe's points, run against every channel at once, each launch from a scratch
/// repository of its own. It records everything it sees and everything it writes under
/// <c>fixtures/claude-code/&lt;channel&gt;/raw/&lt;launch&gt;/</c> in the test tree - git-ignored, for a raw transcript
/// holds the machine's own commands, skills and paths - and the no-network tests of this slice read what is curated
/// from those recordings by hand: the stream reader and the fake <c>claude</c> are built from them. It resumes
/// nothing, changes nothing built, never writes the person's own settings or login, and installs nothing global -
/// each version comes from a scratch npm prefix of its own.
/// </para>
/// <para>
/// Set <see cref="RequiredVariable"/> to run it. It is local and person-run only: a continuous-integration runner never
/// sets the variable - it has no login and must not - so the probe is skipped there by design, and the suite's coverage
/// of the protocol is the committed fixtures and the fake <c>claude</c>, both offline.
/// </para>
/// </remarks>
public sealed class ClaudeCodeProbeTests
{
    /// <summary>
    /// The environment variable that says this machine may run the live probe, naming the program file of the stable
    /// channel's scratch install, <c>&lt;root&gt;/stable/node_modules/@anthropic-ai/claude-code/bin/claude[.exe]</c>; every
    /// other channel is looked for beside it, under <c>&lt;root&gt;/&lt;channel&gt;/</c>.
    /// </summary>
    public const string RequiredVariable = "DSSHARNESS_TESTS_LIVE_CLAUDE";

    /// <summary>The environment variable that narrows a run to some channels, comma-separated (all of them where it is not set).</summary>
    public const string ChannelsVariable = "DSSHARNESS_TESTS_LIVE_CLAUDE_CHANNELS";

    /// <summary>The channels the probe runs against: the oldest version this tool takes (2.1.287), the npm <c>stable</c> tag and <c>latest</c>.</summary>
    private static readonly string[] AllChannels = ["oldest", "stable", "latest"];

    /// <summary>A model id that names no model, for the launches that need the provider to refuse one.</summary>
    private const string UnknownModel = "claude-no-such-model-0-0";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The scenario every channel is held to, in one launch: a read, a second message, an interrupt and a message after
    /// it, allow and deny across write, command and web, the read-only tools <c>Grep</c> and <c>Glob</c>, the worktree's
    /// <c>CLAUDE.md</c> and skills, a protected path and a path outside the working directory, an ask the host answers
    /// <c>error</c>, and a silent host. The model is passed as an id, as the engine passes it.
    /// </summary>
    [Fact]
    public Task TheScenario_IsRecordedOnEveryChannel() => EveryChannelAsync(async probe =>
    {
        using var scratch = await Scratch.CreateAsync(Token);
        await using var launch = Launch.Start(probe, "scenario", scratch, new LaunchOptions());

        await launch.InitializeAsync(Token);

        // (a) A first turn that reads, under the ask rules, and a second message after its result.
        await launch.TurnAsync("Read the file a.txt and say only what text it holds.", Allow, Token);
        await launch.TurnAsync("In one word, what colour is the sky?", Allow, Token);

        // (b, c) A turn ended by the interrupt, and a message after it that must hold what went before.
        await launch.SayAsync("Count slowly from one to five hundred, one number per line.", Token);
        await Task.Delay(TimeSpan.FromSeconds(5), Token);
        var interruptedAt = launch.Count;
        await launch.WriteAsync(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = "probe-interrupt",
            ["request"] = new JsonObject { ["subtype"] = "interrupt", ["cancel_queued"] = true },
        }, Token);
        await launch.AnswerUntilResultAsync(interruptedAt, Allow, TimeSpan.FromSeconds(60), Token);
        await launch.TurnAsync("Without counting again: which file did you read first in this session?", Allow, Token);

        // (d) The kinds of call under the ask rules: allowed ones run, denied ones do not.
        await launch.TurnAsync("Write the text 'written by the probe' into a new file named written.txt.", Allow, Token);
        await launch.TurnAsync("Write the text 'denied' into a new file named denied.txt.", Deny, Token);
        await launch.TurnAsync("Run exactly this command in the Bash tool, changing nothing: echo probe-ran > ran.txt; echo marker=$PROBE_MARKER", Allow, Token);
        await launch.TurnAsync("Search the web for: dssharness claude code probe", Allow, Token, TimeSpan.FromSeconds(240));
        await launch.TurnAsync("Use the Grep tool, and no other tool, to find which files contain the word alpha.", Allow, Token);
        await launch.TurnAsync("Use the Glob tool, and no other tool, to list the files whose names match *.txt.", Allow, Token);

        // What of the worktree reaches the model under --setting-sources user: its CLAUDE.md, and its skills.
        await launch.TurnAsync(
            "Without using any tool, answer two things in one line: the whole marker that begins CLAUDE_MD_MARKER if a line of your context holds one (else none), and whether a skill named probe-worktree-skill is available to you (yes or no).",
            Allow,
            Token);

        // A path Claude Code protects, and a path outside the working directory: whether either reaches the host.
        await launch.TurnAsync("Write the text 'protected' into the file .git/probe-protected.txt.", Allow, Token);
        await launch.TurnAsync("Write the text '{}' into the file .claude/settings.local.json.", Allow, Token);
        await launch.TurnAsync($"Write the text 'outside' into the file {scratch.Outside("probe-outside.txt")}.", Allow, Token);

        // An ask the host answers with an error rather than a decision.
        await launch.TurnAsync("Read the file error.txt and say what it holds.", Error, Token);

        // (e) A call the host never answers: nothing runs while the host is silent.
        await launch.SayAsync("Read the file probe.txt and say what it holds.", Token);
        var before = launch.Count;
        await Task.Delay(TimeSpan.FromSeconds(45), Token);
        launch.Findings["ranWhileHostSilent"] = launch.Lines(before).Any(IsResult);
        launch.Findings["waitedSeconds"] = 45;

        launch.Findings["wroteWritten"] = scratch.Exists("written.txt");
        launch.Findings["wroteDenied"] = scratch.Exists("denied.txt");
        launch.Findings["wroteRan"] = scratch.Exists("ran.txt");
        launch.Findings["wroteProtected"] = scratch.Exists(Path.Combine(".git", "probe-protected.txt"));
        launch.Findings["wroteLocalSettings"] = scratch.Exists(Path.Combine(".claude", "settings.local.json"));
        launch.Findings["wroteOutside"] = File.Exists(scratch.Outside("probe-outside.txt"));
    });

    /// <summary>
    /// The result subtypes and terminal reasons beyond a completed and an interrupted turn: the turn limit, the budget,
    /// and a model the provider does not know.
    /// </summary>
    [Fact]
    public Task TheOtherResults_AreRecordedOnEveryChannel() => EveryChannelAsync(async probe =>
    {
        using (var scratch = await Scratch.CreateAsync(Token))
        {
            await using var launch = Launch.Start(probe, "max-turns", scratch, new LaunchOptions { MaxTurns = 1 });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Read the file a.txt, then write its text into a new file named copy.txt.", Allow, Token);
            launch.Findings["wroteCopy"] = scratch.Exists("copy.txt");
        }

        using (var scratch = await Scratch.CreateAsync(Token))
        {
            await using var launch = Launch.Start(probe, "budget", scratch, new LaunchOptions { MaxBudgetUsd = "0.0001" });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Read the file a.txt and say only what text it holds.", Allow, Token);
            await launch.TurnAsync("In one word, what colour is the sky?", Allow, Token);
        }

        using (var scratch = await Scratch.CreateAsync(Token))
        {
            await using var launch = Launch.Start(probe, "unknown-model", scratch, new LaunchOptions { Model = UnknownModel });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Say only: ok", Allow, Token);
            await launch.TurnAsync("Say only: ok", Allow, Token);
        }
    });

    /// <summary>The <c>-p</c> half: the same launch with <c>-p</c>, streamed both ways, and whether it writes a session file.</summary>
    [Fact]
    public Task ThePrintMode_IsRecordedOnEveryChannel() => EveryChannelAsync(async probe =>
    {
        using (var scratch = await Scratch.CreateAsync(Token))
        {
            await using var launch = Launch.Start(probe, "print", scratch, new LaunchOptions { Print = true });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Read the file a.txt and say only what text it holds.", Allow, Token);

            // Whether print mode ends the process after its first result while its input is still open.
            var exited = await launch.ExitedWithinAsync(TimeSpan.FromSeconds(15));
            launch.Findings["exitedAfterFirstResultWithInputOpen"] = exited;

            if (!exited)
            {
                await launch.TurnAsync("In one word, what colour is the sky?", Allow, Token);
            }
        }

        using (var scratch = await Scratch.CreateAsync(Token))
        {
            await using var launch = Launch.Start(probe, "print-budget", scratch, new LaunchOptions { Print = true, MaxBudgetUsd = "0.0001" });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Read the file a.txt and say only what text it holds.", Allow, Token);
        }

        // The help names --permission-prompts as print mode's: whether, under -p, it sends an ask to the host alone.
        using (var scratch = await Scratch.CreateAsync(Token))
        {
            await using var launch = Launch.Start(probe, "print-prompts-host-only", scratch, new LaunchOptions { Print = true, PromptTool = false });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Read the file a.txt and say only what text it holds.", Allow, Token, TimeSpan.FromSeconds(120));
            launch.Findings["asks"] = launch.Lines(0).Count(IsAsk);
        }
    });

    /// <summary>
    /// The host gone while a call waits for it: its input closed, the host still reading; and the host dead, its input
    /// and its reading end both closed with no answer given.
    /// </summary>
    [Fact]
    public Task TheHostGone_IsRecordedOnEveryChannel() => EveryChannelAsync(async probe =>
    {
        using (var scratch = await Scratch.CreateAsync(Token))
        {
            await using var launch = Launch.Start(probe, "input-closed", scratch, new LaunchOptions());
            await launch.InitializeAsync(Token);
            await launch.SayAsync("Write the text 'gone' into a new file named gone.txt.", Token);

            var asked = await launch.UntilAsync(0, IsAsk, TimeSpan.FromSeconds(180), Token);
            launch.Findings["asked"] = asked is not null;

            var closedAt = launch.Count;
            launch.CloseInput();

            launch.Findings["exitedWithinSixtySecondsOfTheClose"] = await launch.ExitedWithinAsync(TimeSpan.FromSeconds(60));
            launch.Findings["linesAfterTheClose"] = new JsonArray(launch.Lines(closedAt).Select(line => (JsonNode?)Kind(line)).ToArray());
            launch.Findings["wroteGone"] = scratch.Exists("gone.txt");
        }

        using (var scratch = await Scratch.CreateAsync(Token))
        {
            await using var launch = Launch.Start(probe, "host-dead", scratch, new LaunchOptions { DiesOnTheFirstAsk = true });
            await launch.InitializeAsync(Token);
            await launch.SayAsync("Write the text 'gone' into a new file named gone.txt.", Token);

            launch.Findings["died"] = await launch.DiedWithinAsync(TimeSpan.FromSeconds(180), Token);
            launch.Findings["exitedWithinSixtySecondsOfTheDeath"] = await launch.ExitedWithinAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(TimeSpan.FromSeconds(2), Token);
            launch.Findings["wroteGone"] = scratch.Exists("gone.txt");
        }
    });

    /// <summary>
    /// A full context: the window Claude Code assumes is lowered for this launch alone, by the documented
    /// <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c> (which holds for a model it knows only beside <c>DISABLE_COMPACT</c>, which the
    /// launch's settings set), and each turn reads a large file until a turn does not complete.
    /// </summary>
    [Fact]
    public Task AFullContext_IsRecordedOnEveryChannel() => EveryChannelAsync(async probe =>
    {
        using var scratch = await Scratch.CreateAsync(Token);
        await using var launch = Launch.Start(probe, "full-context", scratch, new LaunchOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["CLAUDE_CODE_MAX_CONTEXT_TOKENS"] = ContextWindow },
        });
        await launch.InitializeAsync(Token);

        var reasons = new JsonArray();
        launch.Findings["contextWindow"] = ContextWindow;
        launch.Findings["terminalReasons"] = reasons;

        for (var file = 1; file <= BigFiles; file++)
        {
            var result = await launch.TurnAsync(FormattableString.Invariant($"Read the whole file big-{file}.txt and say only its last line's first word."), Allow, Token);
            var reason = result is null ? "none" : JsonNode.Parse(result)?["terminal_reason"]?.GetValue<string>() ?? "absent";
            reasons.Add(reason);

            if (reason != "completed")
            {
                // What the session does with a message after the turn that filled it.
                var after = await launch.TurnAsync("Say only: ok", Allow, Token);
                launch.Findings["afterTheFullTurn"] = after is null ? "none" : JsonNode.Parse(after)?["terminal_reason"]?.GetValue<string>() ?? "absent";
                break;
            }
        }
    });

    /// <summary>The window the full-context launch assumes, in tokens: small enough to fill in a few turns of a large read.</summary>
    private const string ContextWindow = "50000";

    /// <summary>How many large files a scratch repository holds, one for each turn of the full-context launch.</summary>
    private const int BigFiles = 6;

    /// <summary>
    /// Which of the two flags sends an ask to the host: <c>--permission-prompts host</c> alone, <c>--permission-prompt-tool
    /// stdio</c> alone, and neither.
    /// </summary>
    [Fact]
    public Task ThePermissionFlags_AreRecordedOnEveryChannel() => EveryChannelAsync(async probe =>
    {
        foreach (var (name, tool, host) in new[] { ("prompts-host-only", false, true), ("prompt-tool-only", true, false), ("no-prompt-flag", false, false) })
        {
            using var scratch = await Scratch.CreateAsync(Token);
            await using var launch = Launch.Start(probe, name, scratch, new LaunchOptions { PromptTool = tool, PromptsHost = host });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Read the file a.txt and say only what text it holds.", Allow, Token, TimeSpan.FromSeconds(120));
            launch.Findings["asks"] = launch.Lines(0).Count(IsAsk);
        }
    });

    /// <summary>
    /// Whether an empty <c>fallbackModel</c> in <c>--settings</c> takes away a chain a lower settings file names. The
    /// person's own settings are never written, so the lower file is the scratch repository's project settings, read
    /// beside the user's (<c>--setting-sources user,project</c>), and a model the provider does not know is what fails
    /// over: once with the launch's settings naming no fallback (the chain below is taken), once naming an empty one.
    /// </summary>
    [Fact]
    public Task TheFallbackChain_IsRecordedOnEveryChannel() => EveryChannelAsync(async probe =>
    {
        foreach (var (name, emptied) in new[] { ("fallback-below", false), ("fallback-emptied", true) })
        {
            using var scratch = await Scratch.CreateAsync(Token);
            scratch.WriteProjectSettings(new JsonObject { ["fallbackModel"] = new JsonArray("claude-haiku-5-5") });

            await using var launch = Launch.Start(probe, name, scratch, new LaunchOptions
            {
                Model = UnknownModel,
                SettingSources = "user,project",
                Settings = settings =>
                {
                    if (!emptied)
                    {
                        settings.Remove("fallbackModel");
                    }
                },
            });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Say only: ok", Allow, Token);
            launch.Findings["fellBack"] = launch.Lines(0).Any(line => line.Contains("\"subtype\":\"model_fallback\"", StringComparison.Ordinal));
        }
    });

    /// <summary>
    /// The <c>env</c> of <c>--settings</c> against a lower settings file's <c>env</c> for a name both set: the user's own
    /// layer, from a scratch Claude Code directory (no login there, so a hook tells what the session's environment holds
    /// before the provider refuses the turn), and the project's beside the person's login, where the command tool tells
    /// it. The person's own files are never written.
    /// </summary>
    [Fact]
    public Task TheSettingsEnv_IsRecordedOnEveryChannel() => EveryChannelAsync(async probe =>
    {
        using (var scratch = await Scratch.CreateAsync(Token))
        {
            scratch.WriteProjectSettings(new JsonObject { ["env"] = new JsonObject { ["PROBE_MARKER"] = "project", ["PROBE_LOWER_ONLY"] = "project" } });

            await using var launch = Launch.Start(probe, "env-project", scratch, new LaunchOptions { SettingSources = "user,project", ProcessMarker = false });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Run exactly this command in the Bash tool, changing nothing: echo marker=$PROBE_MARKER lower=$PROBE_LOWER_ONLY", Allow, Token);
        }

        using (var scratch = await Scratch.CreateAsync(Token))
        {
            var configuration = scratch.UserConfiguration(new JsonObject { ["env"] = new JsonObject { ["PROBE_MARKER"] = "user", ["PROBE_LOWER_ONLY"] = "user" } });
            var marker = scratch.Outside("hook-marker.txt").Replace('\\', '/');

            await using var launch = Launch.Start(probe, "env-user", scratch, new LaunchOptions
            {
                ProcessMarker = false,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["CLAUDE_CONFIG_DIR"] = configuration },
                Settings = settings => settings["hooks"] = new JsonObject
                {
                    ["UserPromptSubmit"] = new JsonArray(new JsonObject
                    {
                        ["hooks"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = $"echo marker=$PROBE_MARKER lower=$PROBE_LOWER_ONLY > '{marker}'",
                        }),
                    }),
                },
            });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Say only: ok", Allow, Token, TimeSpan.FromSeconds(120));
            launch.Findings["hookSaw"] = File.Exists(scratch.Outside("hook-marker.txt")) ? File.ReadAllText(scratch.Outside("hook-marker.txt")).Trim() : "no hook ran";
            launch.Findings["sessionFileInTheScratchDirectory"] = Launch.SessionFile(launch.SessionId, configuration);
        }

        // The scenario's command turn, started as a desktop app's host session starts its children: whether the
        // settings' env still wins over the process's when that session's entry point is inherited.
        using (var scratch = await Scratch.CreateAsync(Token))
        {
            await using var launch = Launch.Start(probe, "env-desktop-entrypoint", scratch, new LaunchOptions
            {
                Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["CLAUDE_CODE_ENTRYPOINT"] = "claude-desktop" },
            });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Run exactly this command in the Bash tool, changing nothing: echo marker=$PROBE_MARKER", Allow, Token);
        }
    });

    /// <summary>Each built-in model id other than the scenario's, passed with <c>--model</c> as written: what runs, and what the stream names.</summary>
    [Fact]
    public Task TheModelIds_AreRecordedOnEveryChannel() => EveryChannelAsync(async probe =>
    {
        foreach (var model in new[] { "claude-fable-5-1", "claude-opus-5-5", "claude-haiku-5-5" })
        {
            using var scratch = await Scratch.CreateAsync(Token);
            await using var launch = Launch.Start(probe, $"model-{model}", scratch, new LaunchOptions { Model = model });
            await launch.InitializeAsync(Token);
            await launch.TurnAsync("Say only: ok", Allow, Token);
        }
    });

    /// <summary>The host's answer that allows a call as asked.</summary>
    private static JsonObject? Allow(JsonNode ask) => Answer(ask, new JsonObject { ["behavior"] = "allow", ["updatedInput"] = ask["request"]?["input"]?.DeepClone() });

    /// <summary>The host's answer that denies a call.</summary>
    private static JsonObject? Deny(JsonNode ask) => Answer(ask, new JsonObject { ["behavior"] = "deny", ["message"] = "denied by the probe" });

    /// <summary>The host's answer that is an error, in the envelope's own error shape, and no decision.</summary>
    private static JsonObject? Error(JsonNode ask) => new()
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject
        {
            ["subtype"] = "error",
            ["request_id"] = ask["request_id"]?.GetValue<string>(),
            ["error"] = "the probe's host failed to decide",
        },
    };

    /// <summary>A success answer naming the ask, carrying <paramref name="decision"/> one level further in.</summary>
    private static JsonObject Answer(JsonNode ask, JsonObject decision) => new()
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject
        {
            ["subtype"] = "success",
            ["request_id"] = ask["request_id"]?.GetValue<string>(),
            ["response"] = decision,
        },
    };

    /// <summary>Whether a line is a turn's result.</summary>
    private static bool IsResult(string line) => Kind(line).Split('/')[0] == "result";

    /// <summary>Whether a line is an ask the child puts to its host.</summary>
    private static bool IsAsk(string line) => line.Contains("\"subtype\":\"can_use_tool\"", StringComparison.Ordinal);

    /// <summary>A line's kind: its <c>type</c>, and its <c>subtype</c> beside it where it has one.</summary>
    private static string Kind(string line)
    {
        try
        {
            var node = JsonNode.Parse(line);
            var type = node?["type"]?.GetValue<string>() ?? "";
            var subtype = node?["subtype"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

            return subtype is null ? type : $"{type}/{subtype}";
        }
        catch (System.Text.Json.JsonException)
        {
            return "not json";
        }
    }

    /// <summary>Runs one point against every channel at once, each from its own scratch prefix.</summary>
    private static Task EveryChannelAsync(Func<Probe, Task> point)
    {
        var stable = Environment.GetEnvironmentVariable(RequiredVariable);

        Assert.SkipWhen(string.IsNullOrWhiteSpace(stable), $"{RequiredVariable} is not set: the live probe runs only where the person said it might.");

        // <root>/stable/node_modules/@anthropic-ai/claude-code/bin/claude[.exe]: six levels up is the root that holds every channel's prefix.
        var root = stable!;

        for (var level = 0; level < 6; level++)
        {
            root = Path.GetDirectoryName(root)!;
        }

        var only = Environment.GetEnvironmentVariable(ChannelsVariable);
        var channels = string.IsNullOrWhiteSpace(only) ? AllChannels : only.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var probes = channels
            .Select(channel => new Probe(channel, Path.Combine(root, channel, "node_modules", "@anthropic-ai", "claude-code", "bin", Path.GetFileName(stable!))))
            .ToList();

        foreach (var probe in probes)
        {
            Assert.True(File.Exists(probe.Claude), $"The {probe.Channel} channel's claude is not at '{probe.Claude}'; install it with npm install --prefix.");
        }

        return Task.WhenAll(probes.Select(point));
    }

    /// <summary>One channel the probe runs against, and its program.</summary>
    private sealed record Probe(string Channel, string Claude)
    {
        /// <summary>Where this channel's raw recordings go, in the test tree (git-ignored).</summary>
        public string Raw(string launch) => ClaudeCodeProbe.Fixtures(Channel, "raw", launch);
    }

    /// <summary>What one launch varies of the launch's argument list, settings and environment.</summary>
    private sealed record LaunchOptions
    {
        public string Model { get; init; } = "claude-sonnet-5-5";

        public int MaxTurns { get; init; } = 10;

        public string? MaxBudgetUsd { get; init; }

        public bool PromptTool { get; init; } = true;

        public bool PromptsHost { get; init; } = true;

        public bool Print { get; init; }

        public string SettingSources { get; init; } = "user";

        public Action<JsonObject>? Settings { get; init; }

        public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Whether the host dies at the first ask: its input and its reading end closed, no answer given.</summary>
        public bool DiesOnTheFirstAsk { get; init; }

        /// <summary>Whether the process's own environment sets <c>PROBE_MARKER</c>, which the launch's settings also set.</summary>
        public bool ProcessMarker { get; init; } = true;
    }

    /// <summary>A scratch repository outside any checkout, and a directory beside it that is outside the working directory.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly TempDirectory _root;

        private Scratch(TempDirectory root) => _root = root;

        public string Repository => _root.Combine("repo");

        public static async Task<Scratch> CreateAsync(CancellationToken cancellationToken)
        {
            var scratch = new Scratch(new TempDirectory());

            // Large files, each a different one, so that every read adds its whole to the context: a file read
            // again is answered with a short note that it has not changed, which fills nothing.
            for (var file = 1; file <= BigFiles; file++)
            {
                var big = new StringBuilder();

                for (var line = 1; line <= 400; line++)
                {
                    big.Append(FormattableString.Invariant($"{file}.{line}: a line of the probe's big file {file}, which is here to fill the context.\n"));
                }

                scratch._root.WriteFile(Path.Combine("repo", FormattableString.Invariant($"big-{file}.txt")), big.ToString());
            }

            scratch._root.WriteFile(Path.Combine("repo", "a.txt"), "alpha\n");
            scratch._root.WriteFile(Path.Combine("repo", "probe.txt"), "the probe's own file\n");
            scratch._root.WriteFile(Path.Combine("repo", "error.txt"), "the error turn's file\n");
            scratch._root.WriteFile(Path.Combine("repo", "CLAUDE.md"), "A marker line for the probe: CLAUDE_MD_MARKER_7f3a.\n");
            scratch._root.WriteFile(
                Path.Combine("repo", ".claude", "skills", "probe-worktree-skill", "SKILL.md"),
                "---\nname: probe-worktree-skill\ndescription: A skill of the probe's scratch worktree, which a launch reading the user's settings alone must not load.\n---\n\nSay PROBE_WORKTREE_SKILL_LOADED.\n");
            Directory.CreateDirectory(scratch._root.Combine("outside"));

            await RunAsync(scratch.Repository, "git", ["init", "-q"], cancellationToken);
            await RunAsync(scratch.Repository, "git", ["add", "-A"], cancellationToken);
            await RunAsync(scratch.Repository, "git", ["-c", "user.email=probe@example.com", "-c", "user.name=probe", "commit", "-q", "-m", "probe"], cancellationToken);

            return scratch;
        }

        public bool Exists(string relative) => File.Exists(Path.Combine(Repository, relative));

        public string Outside(string name) => _root.Combine("outside", name);

        /// <summary>Writes the scratch repository's own project settings, which a launch reads only beside <c>project</c> in its setting sources.</summary>
        public void WriteProjectSettings(JsonObject settings)
            => _root.WriteFile(Path.Combine("repo", ".claude", "settings.json"), settings.ToJsonString() + "\n");

        /// <summary>A scratch Claude Code directory holding <paramref name="settings"/> as the user's own, and one skill of the user's.</summary>
        public string UserConfiguration(JsonObject settings)
        {
            _root.WriteFile(Path.Combine("config", "settings.json"), settings.ToJsonString() + "\n");
            _root.WriteFile(
                Path.Combine("config", "skills", "probe-user-skill", "SKILL.md"),
                "---\nname: probe-user-skill\ndescription: A skill of the scratch user's own directory.\n---\n\nSay PROBE_USER_SKILL_LOADED.\n");

            return _root.Combine("config");
        }

        public void Dispose() => _root.Dispose();
    }

    /// <summary>One live launch of <c>claude</c>: its input, what it said, and the recordings it leaves when disposed.</summary>
    private sealed class Launch : IAsyncDisposable
    {
        private readonly Process _child;
        private readonly List<string> _stream = [];
        private readonly List<string> _written = [];
        private readonly List<string> _stderr = [];
        private readonly Task _reading;
        private readonly Task _failing;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly string _raw;
        private readonly Probe _probe;
        private readonly string _name;
        private readonly LaunchOptions _options;
        private readonly TaskCompletionSource _died = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _inputClosed;
        private double? _diedAt;

        private Launch(Probe probe, string name, Scratch scratch, LaunchOptions options)
        {
            _probe = probe;
            _name = name;
            _options = options;
            _raw = probe.Raw(name);

            // The raw recordings of this launch live in its own directory and only there: the curated fixtures beside
            // raw/ are hand-written from them, and a regeneration must not be able to delete them.
            if (Directory.Exists(_raw))
            {
                Directory.Delete(_raw, recursive: true);
            }

            Directory.CreateDirectory(_raw);

            var settings = Settings();
            options.Settings?.Invoke(settings);
            File.WriteAllText(Path.Combine(_raw, "claude-settings.json"), settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.WriteAllText(Path.Combine(_raw, "system.md"), "You are the subject of a protocol probe. Be terse.\n");

            SessionId = Guid.NewGuid().ToString("D");

            var start = new ProcessStartInfo(probe.Claude)
            {
                WorkingDirectory = scratch.Repository,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = !options.DiesOnTheFirstAsk,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = options.DiesOnTheFirstAsk ? null : Encoding.UTF8,
                CreateNoWindow = true,
            };

            foreach (var argument in Arguments())
            {
                start.ArgumentList.Add(argument);
            }

            // The launch is the one a person's terminal makes: nothing of a Claude Code session that started this test
            // reaches it (a test run from inside one inherits its entry point, account and messaging variables, and an
            // inherited CLAUDE_CODE_ENTRYPOINT changes which env wins - the env-desktop-entrypoint launch records it).
            var stripped = new JsonArray();

            foreach (var variable in start.Environment.Keys.Where(IsHostSessionVariable).Order(StringComparer.Ordinal).ToList())
            {
                start.Environment.Remove(variable);
                stripped.Add(variable);
            }

            // The marker the settings' env also sets: which of the two the child's tools see is one of the probe's answers.
            if (options.ProcessMarker)
            {
                start.Environment["PROBE_MARKER"] = "process";
            }

            foreach (var (variable, value) in options.Environment)
            {
                start.Environment[variable] = value;
            }

            Findings["channel"] = probe.Channel;
            Findings["launch"] = name;
            Findings["sessionId"] = SessionId;
            Findings["arguments"] = new JsonArray(Arguments().Select(argument => (JsonNode?)argument).ToArray());
            Findings["environmentSet"] = new JsonArray(options.Environment.Keys.Select(key => (JsonNode?)key).ToArray());
            Findings["environmentStripped"] = stripped;

            _child = Process.Start(start)!;
            _reading = Task.Run(() => ReadAll(_child.StandardOutput, _stream, options.DiesOnTheFirstAsk ? Die : null));
            _failing = options.DiesOnTheFirstAsk ? Task.CompletedTask : Task.Run(() => ReadAll(_child.StandardError, _stderr, null));
        }

        /// <summary>The session id the launch was given.</summary>
        public string SessionId { get; }

        /// <summary>What the point found beside the recordings, written as <c>findings.json</c>.</summary>
        public JsonObject Findings { get; } = [];

        /// <summary>How many lines the child has said so far.</summary>
        public int Count
        {
            get
            {
                lock (_stream)
                {
                    return _stream.Count;
                }
            }
        }

        public static Launch Start(Probe probe, string name, Scratch scratch, LaunchOptions options) => new(probe, name, scratch, options);

        /// <summary>Where the session file of <paramref name="sessionId"/> would be under a Claude Code directory's projects, or <c>none</c>.</summary>
        public static string SessionFile(string sessionId, string? configuration)
        {
            var projects = Path.Combine(configuration ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "projects");

            if (!Directory.Exists(projects))
            {
                return "none";
            }

            return Directory.EnumerateFiles(projects, sessionId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault() is { } found
                ? found
                : "none";
        }

        /// <summary>The lines said from <paramref name="from"/> on.</summary>
        public List<string> Lines(int from)
        {
            lock (_stream)
            {
                return [.. _stream.Skip(from)];
            }
        }

        /// <summary>Writes the initialize request the SDK sends before anything else, and waits for its answer.</summary>
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await WriteAsync(new JsonObject
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

            Findings["initializeAnswered"] = await UntilAsync(0, line => line.Contains("\"probe-init\"", StringComparison.Ordinal), TimeSpan.FromSeconds(30), cancellationToken) is not null;
        }

        /// <summary>Writes one user message.</summary>
        public Task SayAsync(string message, CancellationToken cancellationToken) => WriteAsync(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = message },
        }, cancellationToken);

        /// <summary>One turn: the message, every ask it raises answered by <paramref name="answer"/>, until its result; null where none came in the bound.</summary>
        public async Task<string?> TurnAsync(string message, Func<JsonNode, JsonObject?> answer, CancellationToken cancellationToken, TimeSpan? bound = null)
        {
            var before = Count;

            try
            {
                await SayAsync(message, cancellationToken);
            }
            catch (IOException)
            {
                return null;
            }

            return await AnswerUntilResultAsync(before, answer, bound ?? TimeSpan.FromSeconds(180), cancellationToken);
        }

        /// <summary>Answers every ask from <paramref name="from"/> on until a result comes; null where none came in the bound.</summary>
        public async Task<string?> AnswerUntilResultAsync(int from, Func<JsonNode, JsonObject?> answer, TimeSpan bound, CancellationToken cancellationToken)
        {
            var deadline = _clock.Elapsed + bound;
            var next = from;

            while (_clock.Elapsed < deadline)
            {
                foreach (var line in Lines(next))
                {
                    next++;

                    if (IsAsk(line) && answer(JsonNode.Parse(line)!) is { } response)
                    {
                        await WriteAsync(response, cancellationToken);
                    }

                    if (IsResult(line))
                    {
                        return line;
                    }
                }

                if (_child.HasExited && next == Count)
                {
                    return null;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            return null;
        }

        /// <summary>Waits for a line from <paramref name="from"/> on that holds; null where none came in the bound.</summary>
        public async Task<string?> UntilAsync(int from, Func<string, bool> holds, TimeSpan bound, CancellationToken cancellationToken)
        {
            var deadline = _clock.Elapsed + bound;

            while (_clock.Elapsed < deadline)
            {
                if (Lines(from).FirstOrDefault(holds) is { } line)
                {
                    return line;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            return null;
        }

        /// <summary>Writes one line to the child's input, recording it as written.</summary>
        public async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
        {
            var line = message.ToJsonString(DssHarness.Core.FileSystem.JsonStateFile.LineOptions);

            await _child.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await _child.StandardInput.FlushAsync(cancellationToken);

            lock (_written)
            {
                _written.Add(line);
            }
        }

        /// <summary>Closes the child's input, the host still reading what it says.</summary>
        public void CloseInput()
        {
            Findings["inputClosedAtSeconds"] = Math.Round(_clock.Elapsed.TotalSeconds, 1);
            _inputClosed = true;
            _child.StandardInput.Close();
        }

        /// <summary>Whether the child exits within <paramref name="bound"/>, recording when it did.</summary>
        public async Task<bool> ExitedWithinAsync(TimeSpan bound)
        {
            using var waiting = new CancellationTokenSource(bound);

            try
            {
                await _child.WaitForExitAsync(waiting.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            Findings["exitedAtSeconds"] = Math.Round(_clock.Elapsed.TotalSeconds, 1);
            Findings["exitCode"] = _child.ExitCode;
            return true;
        }

        /// <summary>Whether the host died (at the first ask) within <paramref name="bound"/>.</summary>
        public async Task<bool> DiedWithinAsync(TimeSpan bound, CancellationToken cancellationToken)
        {
            try
            {
                await _died.Task.WaitAsync(bound, cancellationToken);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_inputClosed && !_died.Task.IsCompleted)
                {
                    _child.StandardInput.Close();
                }

                _child.WaitForExit(5_000);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Already gone.
            }

            if (!_child.HasExited)
            {
                Findings["killed"] = true;
                _child.Kill(entireProcessTree: true);
                _child.WaitForExit(5_000);
            }
            else if (!Findings.ContainsKey("exitCode"))
            {
                Findings["exitCode"] = _child.ExitCode;
            }

            Findings["seconds"] = Math.Round(_clock.Elapsed.TotalSeconds, 1);

            if (_diedAt is { } diedAt)
            {
                Findings["diedAtSeconds"] = diedAt;
            }
            Findings["sessionFile"] = SessionFile(SessionId, _options.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var configuration) ? configuration : null);

            // The recordings are written before anything is joined: what the child said is the evidence a failure
            // needs, and a reader that will not end must not keep it from the disk.
            await File.WriteAllLinesAsync(Path.Combine(_raw, "stream.jsonl"), Lines(0), CancellationToken.None);

            lock (_written)
            {
                File.WriteAllLines(Path.Combine(_raw, "control.jsonl"), _written);
            }

            lock (_stderr)
            {
                File.WriteAllLines(Path.Combine(_raw, "claude-stderr.log"), _stderr);
            }

            await File.WriteAllTextAsync(Path.Combine(_raw, "findings.json"), Findings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n", CancellationToken.None);

            try
            {
                await Task.WhenAll(_reading, _failing).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                // A reader that outlived the child holds no more evidence than what is already written.
            }

            _child.Dispose();
        }

        /// <summary>Whether a variable is a Claude Code session's own, which a child started inside one would inherit.</summary>
        private static bool IsHostSessionVariable(string variable)
            => variable is "CLAUDECODE" or "CLAUDE_PID" or "CLAUDE_CONFIG_DIR"
                || variable.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal)
                || variable.StartsWith("CLAUDE_AGENT_SDK_", StringComparison.Ordinal)
                || variable.StartsWith("CLAUDE_PREVIEW_", StringComparison.Ordinal)
                || variable.StartsWith("ANTHROPIC_", StringComparison.Ordinal)
                || variable.StartsWith("MCP_", StringComparison.Ordinal);

        /// <summary>The host's death at the first ask: its input closed and its reading end closed, from the reading thread itself, so no read is pending on it.</summary>
        private bool Die(string line)
        {
            if (!IsAsk(line))
            {
                return false;
            }

            _diedAt = Math.Round(_clock.Elapsed.TotalSeconds, 1);
            _child.StandardInput.Close();
            _child.StandardOutput.Close();
            _died.TrySetResult();
            return true;
        }

        /// <summary>Reads every line of <paramref name="from"/> into <paramref name="into"/>, until the stream ends or <paramref name="stopAt"/> says the host is gone.</summary>
        private static void ReadAll(StreamReader from, List<string> into, Func<string, bool>? stopAt)
        {
            try
            {
                string? line;

                while ((line = from.ReadLine()) is not null)
                {
                    lock (into)
                    {
                        into.Add(line);
                    }

                    if (stopAt?.Invoke(line) == true)
                    {
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The host closed its end.
            }
        }

        /// <summary>The launch's argument list, whole, as part 03's Permissions spell it, varied only as the options say.</summary>
        private List<string> Arguments()
        {
            List<string> arguments = [];

            if (_options.Print)
            {
                arguments.Add("-p");
            }

            arguments.AddRange(
            [
                "--input-format", "stream-json",
                "--output-format", "stream-json",
                "--verbose",
                "--include-partial-messages",
                "--replay-user-messages",
                "--session-id", SessionId,
                "--no-session-persistence",
                "--model", _options.Model,
                "--effort", "low",
                "--max-turns", _options.MaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ]);

            if (_options.MaxBudgetUsd is { } budget)
            {
                arguments.AddRange(["--max-budget-usd", budget]);
            }

            arguments.AddRange(["--permission-mode", "default"]);

            if (_options.PromptTool)
            {
                arguments.AddRange(["--permission-prompt-tool", "stdio"]);
            }

            if (_options.PromptsHost)
            {
                arguments.AddRange(["--permission-prompts", "host"]);
            }

            arguments.AddRange(
            [
                "--tools", "Read,Grep,Glob,Write,Edit,NotebookEdit,Bash,WebSearch,WebFetch",
                "--setting-sources", _options.SettingSources,
                "--strict-mcp-config",
                "--settings", Path.Combine(_raw, "claude-settings.json"),
                "--append-system-prompt-file", Path.Combine(_raw, "system.md"),
                "--json-schema", "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"],\"additionalProperties\":false}",
            ]);

            return arguments;
        }

        /// <summary>The launch's own settings, as the engine writes them: ask rules for every tool, the engine's env, nothing else.</summary>
        private static JsonObject Settings()
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
                    ["CLAUDE_BASH_MAINTAIN_PROJECT_WORKING_DIR"] = "1",
                    ["PROBE_MARKER"] = "settings",
                },
            };
        }
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
