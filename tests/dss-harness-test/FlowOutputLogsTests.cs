using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DssHarness.Tests;

/// <summary>
/// A command's output logs (<see cref="FlowOutputLogs"/>): one zip holding the run's whole directory, its folders kept,
/// the configuration and the manifest - every link, key store, dotenv file, the run's temp, the graph's caches and each
/// agent's worktree left out and named with why, no secret value anywhere in it - rewritten whole through a file beside
/// it; and the lifecycle every command shares writing it at every end, a refusal before any run and a crash among them,
/// named in the text and in the document, refusing a path it cannot be written at before anything runs.
/// </summary>
public sealed class FlowOutputLogsTests
{
    /// <summary>A workflow that pauses on its first step, q1, and has one more after it.</summary>
    internal const string Paused = """
        name: flow
        steps:
          - name: ask
            type: pause
            question: go on?
            options: [stay, go]
          - name: after
            type: orient
            dependsOn: [ask]
        """;

    /// <summary>A workflow that pauses on q1 while its other branch runs a step a test holds, slow.</summary>
    private const string Holding = """
        name: flow
        steps:
          - name: holding
            type: pause
            question: what now?
            options: [stay, go]
          - name: after
            type: orient
            dependsOn: [holding]
          - name: warm
            type: orient
          - name: slow
            type: orient
            dependsOn: [warm]
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The run's whole directory is in the zip under run/, every file as it is on disk and a folder that holds nothing as
    /// one of its own, then the configuration, and the manifest last, listing every entry with its size.
    /// </summary>
    [Fact]
    public async Task TheZip_HoldsTheRunsWholeDirectory_ItsFoldersKept_ThenTheConfiguration_AndTheManifestLast()
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        Write(scene.Run, "notes/deep/er/x.txt", "deep\n");
        Directory.CreateDirectory(Path.Combine(scene.Run.Directory, "notes", "empty"));
        var zip = outside.Combine("logs.zip");

        await WriteAsync(Logs(scene.Harness), scene, zip);

        var entries = Entries(zip);
        var files = Directory.EnumerateFiles(scene.Run.Directory, "*", SearchOption.AllDirectories)
            .Select(file => Relative(scene.Run, file))
            .Where(relative => !relative.StartsWith($"{HarnessLayout.OrchestratorTempDirectoryName}/", StringComparison.Ordinal))
            .ToList();
        var folders = Directory.EnumerateDirectories(scene.Run.Directory, "*", SearchOption.AllDirectories)
            .Where(folder => !Directory.EnumerateFileSystemEntries(folder).Any())
            .Select(folder => Relative(scene.Run, folder) + "/")
            .Where(relative => !relative.StartsWith($"{HarnessLayout.OrchestratorTempDirectoryName}/", StringComparison.Ordinal))
            .ToList();

        Assert.Contains("notes/empty/", folders);
        Assert.Equal(
            [.. files.Concat(folders).Select(relative => $"{FlowOutputLogs.RunFolder}/{relative}").Order(StringComparer.Ordinal)],
            [.. entries.Take(entries.Count - 2).Select(entry => entry.Name).Order(StringComparer.Ordinal)]);
        Assert.Equal([FlowOutputLogs.ConfigName, FlowOutputLogs.ManifestName], entries.TakeLast(2).Select(entry => entry.Name));

        foreach (var relative in files)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(scene.Run.Directory, relative)), entries.Single(entry => entry.Name == $"{FlowOutputLogs.RunFolder}/{relative}").Bytes);
        }

        var config = Path.Combine(scene.Layout.MainHarnessDirectory, HarnessLayout.ConfigFileName);
        Assert.Equal(File.ReadAllBytes(config), entries.Single(entry => entry.Name == FlowOutputLogs.ConfigName).Bytes);

        var manifest = Manifest(zip);
        Assert.Equal(config, manifest["config"]!.GetValue<string>());
        Assert.Equal(
            new JsonArray([.. entries.SkipLast(1).Select(entry => (JsonNode)new JsonObject { ["path"] = entry.Name, ["size"] = entry.Bytes.Length })]).ToJsonString(),
            manifest["entries"]!.ToJsonString());
        Assert.True(Directory.Exists(Path.Combine(scene.Run.Directory, HarnessLayout.OrchestratorTempDirectoryName)));
        Assert.Equal(TempLeftOut().ToJsonString(), manifest["leftOut"]!.ToJsonString());
    }

    /// <summary>
    /// Left out, each named with why and none of it in the zip: every link or junction, never followed; every key store and
    /// dotenv file at any depth, a directory of one whole, in any case, and the shared copy of one an agent was handed; the
    /// run's temp; the graph's caches - its map and its sync kept - and each agent's worktree, outside the run. A folder
    /// all of whose entries are left out is kept as one that holds nothing.
    /// </summary>
    [Fact]
    public async Task WhatIsLeftOut_IsNamedWithWhy_AndNoneOfItIsInTheZip_NoLinkFollowed()
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        outside.WriteFile("elsewhere/outside.txt", "from outside the run\n");

        Write(scene.Run, ".env", "A=1\n");
        Write(scene.Run, "notes/.env.local", "A=1\n");
        Write(scene.Run, "notes/.key", "key\n");
        Write(scene.Run, "notes/.secrets/token", "token\n");
        Write(scene.Run, "notes/Local.ENV", "A=1\n");
        Write(scene.Run, "notes/keys.env", "A=1\n");
        Write(scene.Run, "notes/kept.txt", "kept\n");
        Write(scene.Run, "notes/only-keys/.env", "A=1\n");
        TestLinks.DirectoryLink(Path.Combine(scene.Run.Directory, "notes", "out"), outside.Combine("elsewhere"));
        Write(scene.Run, $"{HarnessLayout.OrchestratorTempDirectoryName}/left.txt", "temp\n");

        foreach (var cache in FlowOutputLogs.GraphCaches)
        {
            Write(scene.Run, cache.Contains('.', StringComparison.Ordinal) ? $"graph/{cache}" : $"graph/{cache}/one.json", "{}\n");
        }

        Write(scene.Run, "graph/map.md", "# map\n");
        Write(scene.Run, "graph/sync.jsonl", "{}\n");

        var handedKey = Digest("SECRET=1\n");
        var handedFile = Digest("a\n");
        var agent = new AgentRecord
        {
            Kind = AgentRecord.KindName,
            Name = "a1",
            Orchestrator = scene.Run.Name,
            Model = "m",
            CreatedAt = DateTimeOffset.UnixEpoch,
            State = AgentStates.Live,
            Worktree = $"{scene.Run.Name}/a1",
            WorktreesRoot = ".harness-config/worktrees",
        };
        scene.Harness.OrchestrationStore.WriteAgent(scene.Run, agent);
        scene.Harness.OrchestrationStore.WriteSeed(
            scene.Run,
            "a1",
            new SeedRecord { SeededAt = DateTimeOffset.UnixEpoch, Empty = false, Paths = new() { ["config/.env.local"] = handedKey, ["src/a.txt"] = handedFile } });
        Directory.CreateDirectory(scene.Run.SharedDirectory("a1"));
        File.WriteAllText(scene.Run.SharedFile("a1", handedKey), "SECRET=1\n");
        File.WriteAllText(scene.Run.SharedFile("a1", handedFile), "a\n");
        var zip = outside.Combine("logs.zip");

        await WriteAsync(Logs(scene.Harness), scene, zip);

        var names = Entries(zip).Select(entry => entry.Name).ToList();
        var leftOut = Manifest(zip)["leftOut"]!.AsArray().Select(left => (left!["path"]!.GetValue<string>(), left["why"]!.GetValue<string>())).ToList();

        Assert.Equal(
            [
                (agent.WorktreePath(scene.Layout)!, "the worktree of agent 'a1', outside the run: the tree it worked in, which git holds; its records and its steps' records say what it did there"),
                ("run/.env", FlowOutputLogs.KeyStoreWhy),
                ($"run/agents/a1/shared/{handedKey}", FlowOutputLogs.KeyStoreWhy),
                ("run/graph/extracts", FlowOutputLogs.GraphCacheWhy),
                ("run/graph/graph.json", FlowOutputLogs.GraphCacheWhy),
                ("run/graph/symbols.jsonl", FlowOutputLogs.GraphCacheWhy),
                ("run/graph/views", FlowOutputLogs.GraphCacheWhy),
                ("run/notes/.env.local", FlowOutputLogs.KeyStoreWhy),
                ("run/notes/.key", FlowOutputLogs.KeyStoreWhy),
                ("run/notes/.secrets", FlowOutputLogs.KeyStoreWhy),
                ("run/notes/Local.ENV", FlowOutputLogs.KeyStoreWhy),
                ("run/notes/keys.env", FlowOutputLogs.KeyStoreWhy),
                ("run/notes/only-keys/.env", FlowOutputLogs.KeyStoreWhy),
                ("run/notes/out", FlowOutputLogs.LinkWhy),
                ("run/temp", FlowOutputLogs.TempWhy),
            ],
            leftOut);
        Assert.DoesNotContain(names, name => leftOut.Any(left => name == left.Item1 || name.StartsWith(left.Item1 + "/", StringComparison.Ordinal)));
        Assert.DoesNotContain(names, name => name.Contains("outside.txt", StringComparison.Ordinal));
        Assert.Contains("run/notes/kept.txt", names);
        Assert.Contains("run/notes/only-keys/", names);
        Assert.Contains("run/graph/map.md", names);
        Assert.Contains("run/graph/sync.jsonl", names);
        Assert.Contains($"run/agents/a1/shared/{handedFile}", names);
        Assert.Contains("run/agents/a1/agent.json", names);
    }

    /// <summary>
    /// The manifest says what was run and how it ended: its schema, when it was written, which build on which system, the
    /// command and its line, the directory, the code and the summary, the run's name, workflow, state and open questions,
    /// the command's own output - a document read as the JSON it is, or kept as its text - the configuration and the mask.
    /// </summary>
    [Fact]
    public async Task TheManifest_SaysWhatWasRun_HowItEnded_AndTheRunsOpenQuestions()
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var zip = outside.Combine("logs.zip");
        var clock = new ManualClock();

        await Logs(scene.Harness, clock: clock).WriteAsync(
            zip,
            new OutputLogsEnd(
                FlowService.ResumeCommand,
                ["dssharness", FlowService.ResumeCommand, "r1", "--json"],
                scene.Repository.Path,
                (scene.Layout, scene.Run),
                FlowExit.Paused,
                "r1 is paused on q1",
                new CapturedOutput(true, """{"exitCode":1,"run":"r1"}""", ["resume-orchestrator: r1 is paused on q1"])),
            Token);

        var manifest = Manifest(zip);
        manifest.Remove("entries");

        Assert.Equal(
            new JsonObject
            {
                ["schema"] = FlowOutputLogs.Schema,
                ["writtenAt"] = FlowTimeConverter.Written(clock.GetUtcNow()),
                ["harness"] = new JsonObject { ["version"] = "1.0.0", ["assemblySha256"] = "0000" },
                ["platform"] = new JsonObject { ["os"] = scene.Harness.Platform.PlatformKey, ["processor"] = scene.Harness.Platform.Processor },
                ["command"] = FlowService.ResumeCommand,
                ["commandLine"] = new JsonArray("dssharness", FlowService.ResumeCommand, "r1", "--json"),
                ["directory"] = scene.Repository.Path,
                ["exitCode"] = FlowExit.Paused,
                ["summary"] = "r1 is paused on q1",
                ["run"] = new JsonObject
                {
                    ["name"] = "r1",
                    ["workflow"] = FlowServiceTests.Workflow,
                    ["state"] = "Paused",
                    ["directory"] = scene.Run.Directory,
                    ["questions"] = new JsonArray(new JsonObject { ["id"] = "q1", ["kind"] = "pause", ["step"] = "ask", ["question"] = "go on?" }),
                },
                ["output"] = new JsonObject
                {
                    ["json"] = true,
                    ["document"] = new JsonObject { ["exitCode"] = 1, ["run"] = "r1" },
                    ["lines"] = new JsonArray("resume-orchestrator: r1 is paused on q1"),
                },
                ["config"] = Path.Combine(scene.Layout.MainHarnessDirectory, HarnessLayout.ConfigFileName),
                ["mask"] = new JsonObject { ["secrets"] = 0 },
                ["leftOut"] = TempLeftOut(),
            }.ToJsonString(),
            manifest.ToJsonString());

        await Logs(scene.Harness, clock: clock).WriteAsync(
            zip,
            new OutputLogsEnd(FlowService.ResumeCommand, ["dssharness"], scene.Repository.Path, null, HarnessExit.Refused, "refused", new CapturedOutput(true, "not a document", [])),
            Token);

        var kept = Manifest(zip);
        Assert.Equal("""{"json":true,"document":"not a document","lines":[]}""", kept["output"]!.ToJsonString());
        Assert.Null(kept["run"]);
    }

    /// <summary>
    /// No secret the session's records were masked of lands anywhere in the zip - each the configuration appoints, as the
    /// one builder of a launch's mask made it in the session, a web-search key's form a step was given, and a token a step's
    /// mask came to hold after it was told - in a file of the run, in UTF-8, in Latin-1 or in UTF-16 of either byte order
    /// with its mark or without, across the piece a long file is read in, in the command line, the output or its document:
    /// each masked where it stood, every other byte kept, though the configuration no longer reads by the end; the manifest
    /// says how many secrets the zip is held to.
    /// </summary>
    [Fact]
    public async Task NoSecretTheSessionsRecordsWereMaskedOf_LandsInTheZip_EachMaskedWhereItStood()
    {
        const string Anthropic = "test-anthropic-value-0123456789";
        const string Zai = "zai-clé-ünï-9876";
        const string Web = "web-search-value-4242";
        const string Later = "gho_read-again-7777";
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();
        using var scene = await PausedAsync(new HarnessConfig
        {
            Workflows = [FlowServiceTests.Workflow],
            Defaults = new() { AgentTier = agents.AgentTier },
            Providers = [.. agents.Providers],
            TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase),
        });
        using var outside = new TempDirectory();
        var slot = scene.Repository.Tree.WriteFile(".harness-config/workflows/.env/keys.env", $"ANTHROPIC_API_KEY={Anthropic}\nZAI_API_KEY={Zai}\n");
        scene.Harness.FilePermissions.ProtectSecret(slot);

        Write(scene.Run, "notes/said.txt", $"anthropic {Anthropic} zai {Zai} end\n");
        File.WriteAllBytes(Path.Combine(scene.Run.Directory, "notes", "latin1.txt"), Encoding.Latin1.GetBytes($"zai {Zai} end\n"));
        File.WriteAllBytes(Path.Combine(scene.Run.Directory, "notes", "utf16le.txt"), [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes($"zai {Zai} key {Web} end\n")]);
        File.WriteAllBytes(Path.Combine(scene.Run.Directory, "notes", "utf16be.txt"), Encoding.BigEndianUnicode.GetBytes($"anthropic {Anthropic} token {Later} end\n"));
        Write(scene.Run, "notes/web.txt", $"sent {Web} then {Later}\n");
        var across = new string('x', 81920 - 5);
        Write(scene.Run, "notes/across.txt", across + Anthropic + "\n");
        var zip = outside.Combine("logs.zip");

        // The session's masks, as a session of the run builds them: the configuration's secrets and a step's web-search
        // key by the one builder, and a mask that comes to hold a token only after it was told.
        var reached = new FlowRunReached();

        using (EngineSession.Begin())
        {
            var masks = SessionMasks.OfSession()!;
            reached.Masked(masks);
            var launch = new ReasonLaunch(scene.Harness.FileSystem, scene.Harness.Platform, new AgentKeyStore(scene.Harness.FileSystem, scene.Harness.FilePermissions, scene.Harness.Platform), updates: null, new TestIsolation());
            var providers = DssHarness.Core.Agents.AgentProviders.Resolve((await scene.Harness.ContextLoader.LoadAsync(scene.Repository.Path, Token)).Config).Providers!;
            _ = launch.Mask(scene.Layout, providers, new ActionValues(new Dictionary<string, string>(), new Dictionary<string, string> { ["web-search-key-1"] = Web }));
            var growing = new GrowingMask(ActionValues.Empty);
            masks.Hold(growing);
            growing.Hold([Later]);
        }

        // The configuration no longer reads by the end, and the zip is masked all the same.
        File.WriteAllText(Path.Combine(scene.Layout.MainHarnessDirectory, HarnessLayout.ConfigFileName), "{ not json");

        await Logs(scene.Harness, reached: reached).WriteAsync(
            zip,
            new OutputLogsEnd(
                FlowService.RunCommand,
                ["dssharness", FlowService.RunCommand, "flow", "--input", $"request={Anthropic}"],
                scene.Repository.Path,
                (scene.Layout, scene.Run),
                FlowExit.Paused,
                $"paused, {Zai}",
                new CapturedOutput(true, $$"""{"said":"{{Zai}}"}""", [$"run-orchestrator: said {Zai}"])),
            Token);

        var entries = Entries(zip);
        var forms = new[] { Anthropic, Zai, Web, Later }
            .SelectMany(secret => new[] { Encoding.UTF8, Encoding.Latin1, Encoding.Unicode, Encoding.BigEndianUnicode }.Select(encoding => encoding.GetBytes(secret)))
            .ToArray();

        foreach (var (name, bytes) in entries)
        {
            Assert.DoesNotContain(forms, form => bytes.AsSpan().IndexOf(form) >= 0);
        }

        Assert.Equal("anthropic *** zai *** end\n", Text(entries, "run/notes/said.txt"));
        Assert.Equal(Encoding.Latin1.GetBytes("zai *** end\n"), entries.Single(entry => entry.Name == "run/notes/latin1.txt").Bytes);
        // Each masked in its own encoding: the file still reads as UTF-16 past it.
        Assert.Equal([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("zai *** key *** end\n")], entries.Single(entry => entry.Name == "run/notes/utf16le.txt").Bytes);
        Assert.Equal(Encoding.BigEndianUnicode.GetBytes("anthropic *** token *** end\n"), entries.Single(entry => entry.Name == "run/notes/utf16be.txt").Bytes);
        Assert.Equal("sent *** then ***\n", Text(entries, "run/notes/web.txt"));
        Assert.Equal(across + "***\n", Text(entries, "run/notes/across.txt"));

        var manifest = Manifest(zip);
        var strings = Strings(manifest).ToList();

        Assert.DoesNotContain(strings, text => text.Contains(Anthropic, StringComparison.Ordinal) || text.Contains(Zai, StringComparison.Ordinal));
        Assert.Equal("request=***", manifest["commandLine"]![4]!.GetValue<string>());
        Assert.Equal("paused, ***", manifest["summary"]!.GetValue<string>());
        Assert.Equal("""{"json":true,"document":{"said":"***"},"lines":["run-orchestrator: said ***"]}""", manifest["output"]!.ToJsonString());
        Assert.Equal("""{"secrets":4}""", manifest["mask"]!.ToJsonString());
    }

    /// <summary>
    /// Where no mask of the session's records stands for the zip - the session could not build the mask of the secrets its
    /// configuration appoints, or the command began no session at all - every place the run's layout marks as kept as it
    /// came is left out whole, each named with why - an agent's evidence and its conversations, a red leg's and a red CI
    /// job's logs, what a step's agent was handed, a side agent's work, the continuation record - and the records the run's
    /// masks were applied to are kept beside them: never an unmasked copy of a transcript, a handed file, an agent's own
    /// work or a log of what its commands printed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AZipNoMaskStandsFor_LeavesOutWhatTheRunKeptAsItCame_EachNamedWithWhy(bool began)
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var zip = outside.Combine("logs.zip");
        var continuation = Path.GetFileName(DssHarness.Core.Agents.AgentCallRecords.ResultFile(".", DssHarness.Core.Agents.Runtime.KeptResults.ContinuationId));

        foreach (var file in new[]
        {
            "logs/a1/session.jsonl", "agents/a1/shared/0123", "agents/a1/rows/r.json", "work/a1/scratch.txt", "plans/a1/plan.md", "inputs/spec/spec.md", "notes/kept.txt",
            "remote-workflows/blobs/ab", "agents/a1/evidence/r1/TestResults/env.txt", "agents/a1/sessions/s1/conversation.jsonl",
            "steps/ci/logs/build.log", "steps/ci/step.json", "agents/a1/steps/fix/logs/a.build.log", "agents/a1/steps/fix/measures/m1/logs/a.test.log",
            "agents/a1/steps/fix/measures/m1/ledger.json", "agents/a1/steps/fix/handed/skills/s/SKILL.md", $"agents/a1/steps/fix/tool-results/{continuation}",
            "agents/a1/steps/fix/tool-results/call-other.txt", "agents/a1/steps/fix/ledger.json", "agents/a1/steps/fix/advice/1/work/x.txt",
            "agents/a1/steps/fix/advice/1/stream.jsonl", "asks/s1/sessions/c1/conversation.jsonl", "asks/s1/ask.json", "analysis/s1/work/y.txt",
        })
        {
            Write(scene.Run, file, "anything\n");
        }

        Write(scene.Run, "logs/r1.log", "the tool's own log\n");
        var reached = new FlowRunReached();

        if (began)
        {
            var masks = new SessionMasks();
            masks.Unbuilt("the configuration's providers do not resolve, so it appoints no secret a mask could be built of");
            reached.Masked(masks);
        }

        await WriteAsync(Logs(scene.Harness, reached: reached), scene, zip);

        var why = began
            ? "the configuration's providers do not resolve, so it appoints no secret a mask could be built of"
            : "the command began no session of a run, so no mask of a run's records was built";
        var manifest = Manifest(zip);
        var leftOut = manifest["leftOut"]!.AsArray().Select(left => (left!["path"]!.GetValue<string>(), left["why"]!.GetValue<string>())).ToList();
        var unmasked = $"kept in the run as it came, and no mask of the run's records stands for the zip: {why}";

        Assert.Equal(
            [
                ("run/agents/a1/evidence", unmasked),
                ("run/agents/a1/rows", unmasked),
                ("run/agents/a1/sessions", unmasked),
                ("run/agents/a1/shared", unmasked),
                ("run/agents/a1/steps/fix/advice/1/work", unmasked),
                ("run/agents/a1/steps/fix/handed", unmasked),
                ("run/agents/a1/steps/fix/logs", unmasked),
                ("run/agents/a1/steps/fix/measures/m1/logs", unmasked),
                ($"run/agents/a1/steps/fix/tool-results/{continuation}", unmasked),
                ("run/analysis/s1/work", unmasked),
                ("run/artifacts", unmasked),
                ("run/asks/s1/sessions", unmasked),
                ("run/inputs", unmasked),
                ("run/logs/a1", unmasked),
                ("run/plans", unmasked),
                ("run/remote-workflows", unmasked),

                // The advice the run's advisor was asked of the question the run is paused on, as every question is.
                ($"run/steps/ask/advice/1/tool-results/{continuation}", unmasked),
                ("run/steps/ask/advice/1/work", unmasked),
                ("run/steps/ci/logs", unmasked),
                ("run/temp", FlowOutputLogs.TempWhy),
                ("run/work", unmasked),
            ],
            leftOut);
        Assert.Equal((0, why), (manifest["mask"]!["secrets"]!.GetValue<int>(), manifest["mask"]!["whyNone"]!.GetValue<string>()));
        Assert.Equal(2, manifest["mask"]!.AsObject().Count);

        var names = Entries(zip).Select(entry => entry.Name).ToList();
        Assert.DoesNotContain(names, name => leftOut.Any(left => name == left.Item1 || name.StartsWith(left.Item1 + "/", StringComparison.Ordinal)));

        // The records beside them, which the run's masks were applied to as they were written, are kept.
        foreach (var kept in new[]
        {
            "run/notes/kept.txt", "run/logs/r1.log", "run/flow.json", "run/steps/ci/step.json", "run/agents/a1/steps/fix/ledger.json",
            "run/agents/a1/steps/fix/measures/m1/ledger.json", "run/agents/a1/steps/fix/tool-results/call-other.txt", "run/agents/a1/steps/fix/advice/1/stream.jsonl",
            "run/asks/s1/ask.json",
        })
        {
            Assert.Contains(kept, names);
        }
    }

    /// <summary>
    /// An answer handed to the engine that runs the run in another session begins no session of its own, so no mask of the
    /// run's records stands for its zip: a key an agent's test run wrote into its kept evidence is in no entry of the zip,
    /// for the evidence is left out whole and named with why - never copied unmasked as the configuration's mask would
    /// have masked it, had a session been begun.
    /// </summary>
    [Fact]
    public async Task AnAnswerHandedToAnEngineRunningElsewhere_LeavesTheKeptEvidenceOut_ItsKeyInNoEntry()
    {
        const string Key = "test-anthropic-value-in-the-evidence-5150";
        var (repository, runner) = await FlowServiceTests.RepositoryAsync(Holding);
        using var disposed = repository;
        using var outside = new TempDirectory();
        var zip = outside.Combine("logs.zip");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Holds("slow", release.Task);

        // The engine runs the run in a session of its own, holding a step while its question is open.
        var driving = FlowServiceTests.Service(repository.Harness, runner).RunAsync(repository.Path, FlowServiceTests.Workflow, run: "r1", inputs: null, json: false, cancellationToken: Token);
        await runner.WaitStartedAsync("slow");

        var run = FlowServiceTests.RunOf(repository);
        Write(run, "agents/a1/evidence/r1/TestResults/env.txt", $"ANTHROPIC_API_KEY={Key}\n");

        var answered = await SessionAsync(
            Options(FlowService.AnswerCommand, repository.Path, zip, json: false),
            (scope, token) => scope.Use<IFlowService>().AnswerAsync(scope.Directory, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: token));

        release.SetResult();
        _ = await driving.WaitAsync(FlowServiceTests.Bound, Token);

        Assert.Equal(HarnessExit.Success, answered.Completion.ExitCode);

        var why = "the command began no session of a run, so no mask of a run's records was built";
        var manifest = Manifest(zip);
        var leftOut = manifest["leftOut"]!.AsArray().Select(left => (left!["path"]!.GetValue<string>(), left["why"]!.GetValue<string>())).ToList();

        Assert.Contains(("run/agents/a1/evidence", $"kept in the run as it came, and no mask of the run's records stands for the zip: {why}"), leftOut);
        Assert.Equal(["secrets", "whyNone"], manifest["mask"]!.AsObject().Select(member => member.Key));
        Assert.Equal((0, why), (manifest["mask"]!["secrets"]!.GetValue<int>(), manifest["mask"]!["whyNone"]!.GetValue<string>()));
        Assert.DoesNotContain(Entries(zip), entry => entry.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Key)) >= 0);
        Assert.Contains("run/flow.json", Entries(zip).Select(entry => entry.Name));
    }

    /// <summary>
    /// The zip is written through a file beside it that then replaces whatever was there, whole each time: what the run
    /// holds now, never what it held before, and nothing left beside it.
    /// </summary>
    [Fact]
    public async Task TheZip_IsRewrittenWhole_ThroughAFileBesideIt()
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var zip = outside.WriteFile("logs.zip", "what was there before");
        var writes = new RecordedWrites(scene.Harness.FileSystem);
        Write(scene.Run, "notes/first.txt", "first\n");

        await WriteAsync(Logs(scene.Harness, writes), scene, zip);

        Assert.Contains("run/notes/first.txt", Entries(zip).Select(entry => entry.Name));

        File.Delete(Path.Combine(scene.Run.Directory, "notes", "first.txt"));
        Write(scene.Run, "notes/later.txt", "later\n");

        await WriteAsync(Logs(scene.Harness, writes), scene, zip);

        var names = Entries(zip).Select(entry => entry.Name).ToList();
        Assert.Equal([zip, zip], writes.Written);
        Assert.Contains("run/notes/later.txt", names);
        Assert.DoesNotContain("run/notes/first.txt", names);
        Assert.Equal(["logs.zip"], Directory.EnumerateFileSystemEntries(outside.Path).Select(Path.GetFileName));
    }

    /// <summary>A zip that could not be written throws, and leaves what was there as it was.</summary>
    [Fact]
    public async Task AZipThatCannotBeWritten_Throws_AndLeavesWhatWasThere()
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var zip = outside.WriteFile("logs.zip", "what was there before");

        var raised = await Assert.ThrowsAsync<IOException>(() => WriteAsync(Logs(scene.Harness, new FailedWrites(scene.Harness.FileSystem, new IOException("no room is left on the device"))), scene, zip));

        Assert.Equal("no room is left on the device", raised.Message);
        Assert.Equal("what was there before", File.ReadAllText(zip));
    }

    /// <summary>
    /// A path the zip cannot be written at is refused, saying nothing was started, and holds no target: a directory, one
    /// inside the repository's orchestrators, and one whose directory cannot be made.
    /// </summary>
    [Theory]
    [InlineData("directory")]
    [InlineData("inside")]
    [InlineData("parent")]
    public async Task APathTheZipCannotBeWrittenAt_IsRefused_SayingNothingWasStarted(string kind)
    {
        var (repository, _) = await FlowServiceTests.RepositoryAsync(Paused);
        using var disposed = repository;
        using var outside = new TempDirectory();
        var layout = (await repository.Harness.RepositoryLocator.LocateAsync(repository.Path, Token))!;
        var file = outside.WriteFile("file.txt", "a file\n");
        var target = kind switch
        {
            "directory" => outside.Path,
            "inside" => Path.Combine(layout.OrchestratorsDirectory, "logs.zip"),
            _ => Path.Combine(file, "logs.zip"),
        };
        var logs = Logs(repository.Harness);

        var refused = await logs.CheckAsync(target, ["dssharness"], repository.Path, Token);

        switch (kind)
        {
            case "directory":
                Assert.Equal($"--output-logs names '{target}', which is a directory; name the file the zip is written to. Nothing was started.", refused);
                break;
            case "inside":
                Assert.Equal(
                    $"--output-logs names '{target}', inside '{layout.OrchestratorsDirectory}', where every run keeps its own records; name a file outside it. Nothing was started.",
                    refused);
                break;
            default:
                Assert.StartsWith($"--output-logs names '{target}', whose directory '{file}' could not be made: ", refused, StringComparison.Ordinal);
                Assert.EndsWith("; name a file in a directory that can be. Nothing was started.", refused, StringComparison.Ordinal);
                break;
        }

        Assert.Null(logs.Target);
        Assert.False(logs.HasAny);
    }

    /// <summary>
    /// A path inside the repository's main checkout or one of its agents' trees, where that tree does not ignore it, is
    /// refused before anything runs - the zip would be one of the tree's uncommitted files, handed to every agent a run of
    /// it makes - whether it is there yet or not, its directory made or not, and where the tree tracks it, though its ignore
    /// rules match it; one the tree ignores is the zip's, and so is one in a work tree of another repository, which no run
    /// of this one hands or folds from.
    /// </summary>
    [Fact]
    public async Task APathInAWorkTreeThatDoesNotIgnoreIt_IsRefused_SayingNothingWasStarted()
    {
        var (repository, _) = await FlowServiceTests.RepositoryAsync(Paused);
        using var disposed = repository;
        var tree = repository.Path;
        repository.Tree.WriteFile("tracked.zip", "tracked\n");
        await repository.Harness.CommitAllAsync(tree, "a tracked zip", Token);
        repository.Tree.WriteFile(".gitignore", "*.zip\nignored/\n");
        await repository.Harness.CommitAllAsync(tree, "ignore zips", Token);
        var root = await repository.Harness.GitClient.GetRepositoryRootAsync(tree, Token);

        // An agent's tree: a repository of its own, which names the main checkout as its own.
        var agent = Path.Combine(new HarnessLayout(root!, root!).WorktreesDirectoryUnder(OrchestrationKit.Settings.Root), "r1", "a1");
        Directory.CreateDirectory(Path.GetDirectoryName(agent)!);
        Assert.Null((await new DssHarness.Core.Worktrees.AgentRepositories(repository.Harness.GitClient, repository.Harness.FileSystem, repository.Harness.Platform)
            .MakeAsync(root!, DssHarness.Core.Worktrees.WorktreeAddress.Nested("r1", "a1"), agent, Token)).Failure);

        foreach (var (refused, inTree) in new[] { (Path.Combine(tree, "r1.log"), root), (Path.Combine(tree, "not", "made", "yet.log"), root), (Path.Combine(tree, "tracked.zip"), root), (Path.Combine(agent, "r1.log"), agent) })
        {
            var logs = Logs(repository.Harness);

            Assert.Equal(
                $"--output-logs names '{refused}', inside the work tree '{inTree}' of the repository's main checkout or an agent's, which does not ignore it: it would be handed to the run's agents as uncommitted state; name a path outside the tree or one git ignores. Nothing was started.",
                await logs.CheckAsync(refused, ["dssharness"], tree, Token));
            Assert.Null(logs.Target);
        }

        Assert.False(Directory.Exists(Path.Combine(tree, "not")));

        // Another repository's work tree - dotfiles kept at the home, another project's checkout - is none of the run's.
        using var other = new TempDirectory();
        await repository.Harness.InitializeGitRepositoryAsync(other.Path, Token);

        foreach (var kept in new[] { Path.Combine(tree, "r1.zip"), Path.Combine(tree, "ignored", "deep", "r1.log"), Path.Combine(other.Path, "Downloads", "r1.log") })
        {
            var logs = Logs(repository.Harness);

            Assert.Null(await logs.CheckAsync(kept, ["dssharness"], tree, Token));
            Assert.Equal(kept, logs.Target);
        }
    }

    /// <summary>
    /// A path in a work tree the locator of a tree's repository refuses to run anything from - a worktree of another
    /// project's bare repository, which has no main checkout - is none a run of this repository hands or folds from, and
    /// takes the zip: whose tree it is is asked of git's common directory and the agent's mark, never of that refusal. A
    /// worktree git keeps of the run's own main checkout shares its common directory, and is the run's.
    /// </summary>
    [Fact]
    public async Task APathInAWorktreeOfABareRepository_TakesTheZip_AndOneInAWorktreeOfTheRunsCheckoutIsRefused()
    {
        var (repository, _) = await FlowServiceTests.RepositoryAsync(Paused);
        using var disposed = repository;
        var tree = repository.Path;

        // Another project kept as a bare repository, worked on in a worktree of it.
        using var other = new TempDirectory();
        var project = other.Combine("project");
        var bare = other.Combine("project.git");
        var worktree = other.Combine("work");
        Directory.CreateDirectory(project);
        await repository.Harness.InitializeGitRepositoryAsync(project, Token);
        File.WriteAllText(Path.Combine(project, "README.md"), "another project\n");
        await repository.Harness.CommitAllAsync(project, "another project", Token);
        await repository.Harness.RunGitAsync(other.Path, ["clone", "--quiet", "--bare", project, bare], Token);
        await repository.Harness.RunGitAsync(other.Path, ["--git-dir", bare, "worktree", "add", "--quiet", "--detach", worktree], Token);
        await Assert.ThrowsAsync<HarnessException>(async () => await repository.Harness.RepositoryLocator.LocateAsync(worktree, Token));

        var kept = Path.Combine(worktree, "logs", "r1.zip");
        var keeping = Logs(repository.Harness);

        Assert.Null(await keeping.CheckAsync(kept, ["dssharness"], tree, Token));
        Assert.Equal(kept, keeping.Target);

        // A worktree of the run's own checkout shares its git, and is handed to the run as any tree of the checkout is.
        var linked = other.Combine("linked");
        await repository.Harness.RunGitAsync(tree, ["worktree", "add", "--quiet", "--detach", linked], Token);
        var linkedRoot = await repository.Harness.GitClient.GetRepositoryRootAsync(linked, Token);
        var refused = Path.Combine(linked, "r1.log");
        var refusing = Logs(repository.Harness);

        Assert.Equal(
            $"--output-logs names '{refused}', inside the work tree '{linkedRoot}' of the repository's main checkout or an agent's, which does not ignore it: it would be handed to the run's agents as uncommitted state; name a path outside the tree or one git ignores. Nothing was started.",
            await refusing.CheckAsync(refused, ["dssharness"], tree, Token));
        Assert.Null(refusing.Target);
    }

    /// <summary>A relative path is read from the directory the process stands in, whose directories on the way are made.</summary>
    [Fact]
    public async Task ARelativePath_IsReadFromTheDirectoryTheProcessStandsIn_ItsDirectoryMade()
    {
        var (repository, _) = await FlowServiceTests.RepositoryAsync(Paused);
        using var disposed = repository;
        using var outside = new TempDirectory();
        repository.Harness.ProcessContext.Fixed = outside.Path;
        var logs = Logs(repository.Harness);

        Assert.Null(await logs.CheckAsync(Path.Combine("sub", "logs.zip"), ["dssharness"], repository.Path, Token));

        Assert.Equal(outside.Combine("sub", "logs.zip"), logs.Target);
        Assert.True(Directory.Exists(outside.Combine("sub")));
        Assert.True(logs.HasAny);
        Assert.Equal($"outputLogs: {outside.Combine("sub", "logs.zip")}", logs.Line);
    }

    /// <summary>A key store or a dotenv file, by its name alone: the tool's own slots' names, .env.* and *.env, in any case.</summary>
    [Theory]
    [InlineData(".env", true)]
    [InlineData(".ENV", true)]
    [InlineData(".secrets", true)]
    [InlineData(".key", true)]
    [InlineData(".env.local", true)]
    [InlineData("keys.env", true)]
    [InlineData("Prod.Env", true)]
    [InlineData("env", false)]
    [InlineData("venv", false)]
    [InlineData("environment.md", false)]
    [InlineData("key", false)]
    [InlineData("secrets.json", false)]
    public void AKeyStore_IsKnownByItsName(string name, bool keyStore)
        => Assert.Equal(keyStore, FlowOutputLogs.IsKeyStore(name));

    /// <summary>
    /// The capture keeps every line the console writes, in order, its --json answer apart, and changes nothing the console
    /// prints: the console teed writes exactly what it writes alone.
    /// </summary>
    [Fact]
    public void TheCapture_KeepsEveryLineTheConsoleWrites_AndChangesNothingItPrints()
    {
        var (plainOut, plainError, teedOut, teedError) = (new StringWriter(), new StringWriter(), new StringWriter(), new StringWriter());
        var capture = new OutputCapture(verbose: false);
        IHarnessOutput plain = new ConsoleHarnessOutput(plainOut, plainError, verbose: false);
        IHarnessOutput teed = new CapturedHarnessOutput(new ConsoleHarnessOutput(teedOut, teedError, verbose: false), capture);

        foreach (var output in new[] { plain, teed })
        {
            output.Info(FlowService.RunCommand, "one");
            output.Warn(FlowService.RunCommand, "two");

            using (output.DataOnly())
            {
                Assert.True(output.IsDataOnly);
                output.Info(FlowService.RunCommand, "three");
                output.Data("""{"a":1}""");
            }

            output.Fail(FlowService.RunCommand, "four");
        }

        Assert.Equal(plainOut.ToString(), teedOut.ToString());
        Assert.Equal(plainError.ToString(), teedError.ToString());

        var lines = new[] { "run-orchestrator: one", "run-orchestrator: WARN - two", "run-orchestrator: three", """{"a":1}""", "run-orchestrator: FAIL - four" };
        Assert.Equal(lines, capture.Captured(json: false).Lines);
        Assert.Null(capture.Captured(json: false).Document);

        var answered = capture.Captured(json: true);
        Assert.Equal("""{"a":1}""", answered.Document);
        Assert.Equal(lines.Where(line => line != """{"a":1}"""), answered.Lines);
    }

    /// <summary>
    /// A session asked for its output logs writes the zip as it ends, with what the command printed - its document whole,
    /// and every line - named as the last line naming its records and in its document's records.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASessionsZip_HoldsWhatItPrinted_NamedLastAmongItsRecords_AndInItsDocument(bool json)
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var zip = outside.Combine("logs.zip");

        var ended = await SessionAsync(
            Options(FlowService.ResumeCommand, scene.Repository.Path, zip, json),
            (scope, _) =>
            {
                scope.Services.GetRequiredService<FlowRunReached>().Reach(scene.Layout, scene.Run);
                return Task.FromResult(new CommandOutcome(FlowExit.Paused, "r1 is paused on q1", ["events: e.jsonl"]) { Data = json ? ["""{"exitCode":1,"records":{"events":"e.jsonl"}}"""] : [] });
            });

        Assert.Equal(FlowExit.Paused, ended.Completion.ExitCode);

        var manifest = Manifest(zip);
        Assert.Equal("r1", manifest["run"]!["name"]!.GetValue<string>());
        Assert.Equal(FlowExit.Paused, manifest["exitCode"]!.GetValue<int>());

        if (json)
        {
            var document = JsonNode.Parse(ended.Output)!;
            Assert.Equal("""{"events":"e.jsonl","outputLogs":"__ZIP__"}""".Replace("__ZIP__", zip.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal), document["records"]!.ToJsonString());
            Assert.True(JsonNode.DeepEquals(document, manifest["output"]!["document"]));
            Assert.Equal(ended.Error, Values(manifest["output"]!["lines"]!));
            Assert.Equal(["resume-orchestrator: events: e.jsonl", $"resume-orchestrator: outputLogs: {zip}", "resume-orchestrator: FAIL - r1 is paused on q1"], ended.Error);
        }
        else
        {
            Assert.Equal([.. Lines(ended.Output), .. ended.Error], Values(manifest["output"]!["lines"]!));
            Assert.Equal(["resume-orchestrator: events: e.jsonl", $"resume-orchestrator: outputLogs: {zip}", "resume-orchestrator: FAIL - r1 is paused on q1"], [.. Lines(ended.Output), .. ended.Error]);
        }
    }

    /// <summary>
    /// A crash the body throws still leaves the zip, the run it reached in it and the failure among its lines - named in a
    /// line of its own before the line the command ends on - and ends as a defect does.
    /// </summary>
    [Fact]
    public async Task ACrash_StillLeavesTheZip_WithTheRunItReached_AndEndsAsADefect()
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var zip = outside.Combine("logs.zip");

        var ended = await SessionAsync(
            Options(FlowService.RunCommand, scene.Repository.Path, zip, json: false),
            (scope, _) =>
            {
                scope.Services.GetRequiredService<FlowRunReached>().Reach(scene.Layout, scene.Run);
                throw new InvalidOperationException("the engine met a defect");
            });

        Assert.Equal(HarnessExit.InternalError, ended.Completion.ExitCode);

        var manifest = Manifest(zip);
        Assert.Equal((HarnessExit.InternalError, "Unexpected InvalidOperationException: the engine met a defect", "r1"), (manifest["exitCode"]!.GetValue<int>(), manifest["summary"]!.GetValue<string>(), manifest["run"]!["name"]!.GetValue<string>()));
        Assert.Equal([.. Lines(ended.Output), .. ended.Error], Values(manifest["output"]!["lines"]!));
        Assert.Equal([$"run-orchestrator: outputLogs: {zip}"], Lines(ended.Output));
        Assert.Equal(["run-orchestrator: FAIL - Unexpected InvalidOperationException: the engine met a defect"], ended.Error);
    }

    /// <summary>
    /// A session of a run begins the mask its records are kept through with every secret its configuration appoints that the
    /// key slot answers, and the zip is held to that mask - never to the configuration read again at the end, which may no
    /// longer read: a secret typed on the command line is masked in the manifest though the configuration was broken before
    /// the zip was written.
    /// </summary>
    [Fact]
    public async Task ASessionsZip_IsHeldToTheMaskItsSessionBegan_NeverToTheConfigurationReadAgain()
    {
        const string Anthropic = "test-anthropic-value-0123456789";
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();
        var (repository, _) = await FlowServiceTests.RepositoryAsync(Paused, new HarnessConfig
        {
            Workflows = [FlowServiceTests.Workflow],
            Defaults = new() { AgentTier = agents.AgentTier },
            Providers = [.. agents.Providers],
            TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase),
        });
        using var disposed = repository;
        using var outside = new TempDirectory();
        var slot = repository.Tree.WriteFile(".harness-config/workflows/.env/keys.env", $"ANTHROPIC_API_KEY={Anthropic}\n");
        repository.Harness.FilePermissions.ProtectSecret(slot);
        var zip = outside.Combine("logs.zip");
        var config = Path.Combine(repository.Path, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);

        var ended = await SessionAsync(
            new SessionOptions
            {
                Command = FlowService.RunCommand,
                Directory = repository.Path,
                Prompting = false,
                Document = DocumentKind.None,
                OutputLogs = new OutputLogsRequest(zip, ["dssharness", FlowService.RunCommand, FlowServiceTests.Workflow, "--input", $"request={Anthropic}"]),
            },
            async (scope, token) =>
            {
                var outcome = await scope.Use<IFlowService>().RunAsync(scope.Directory, FlowServiceTests.Workflow, run: "r1", inputs: null, json: false, cancellationToken: token);
                File.WriteAllText(config, "{ not json");
                return outcome;
            });

        Assert.Equal(FlowExit.Paused, ended.Completion.ExitCode);

        var manifest = Manifest(zip);
        Assert.Equal("request=***", manifest["commandLine"]![4]!.GetValue<string>());
        Assert.Equal("""{"secrets":1}""", manifest["mask"]!.ToJsonString());
        Assert.DoesNotContain(Entries(zip), entry => entry.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Anthropic)) >= 0);
    }

    /// <summary>
    /// A start refused before any run exists still writes the zip: the manifest, with no run, and the command's output; and
    /// so does a command refused before its directory could be read, which finds no repository and so no configuration.
    /// </summary>
    [Fact]
    public async Task ARefusedStart_StillWritesTheManifest_AndItsOutput_WithNoRun()
    {
        var (repository, _) = await FlowServiceTests.RepositoryAsync(Paused);
        using var disposed = repository;
        using var outside = new TempDirectory();
        var zip = outside.Combine("logs.zip");

        var refused = await SessionAsync(
            Options(FlowService.RunCommand, repository.Path, zip, json: true),
            (scope, token) => scope.Use<IFlowService>().RunAsync(scope.Directory, "nope", run: null, inputs: null, json: true, cancellationToken: token));

        var manifest = Manifest(zip);
        Assert.Equal(refused.Completion.ExitCode, manifest["exitCode"]!.GetValue<int>());
        Assert.NotEqual(HarnessExit.Success, refused.Completion.ExitCode);
        Assert.Null(manifest["run"]);
        Assert.Equal([FlowOutputLogs.ConfigName, FlowOutputLogs.ManifestName], Entries(zip).Select(entry => entry.Name));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(refused.Output), manifest["output"]!["document"]));
        Assert.Equal(refused.Error, Values(manifest["output"]!["lines"]!));
        Assert.False(Directory.Exists(Path.Combine(repository.Path, HarnessLayout.OrchestratorsDirectoryName, "r1")));

        var nowhere = outside.Combine("nowhere.zip");
        var missing = outside.Combine("not-there");
        var unread = await SessionAsync(Options(FlowService.RunCommand, missing, nowhere, json: false), (_, _) => throw new InvalidOperationException("never reached"));

        Assert.Equal(HarnessExit.UsageError, unread.Completion.ExitCode);

        var unreadManifest = Manifest(nowhere);
        Assert.Equal(
            (HarnessExit.UsageError, (string?)null, "no repository was found", """{"secrets":0,"whyNone":"no repository was found, so no configuration appoints a secret"}"""),
            (unreadManifest["exitCode"]!.GetValue<int>(), unreadManifest["directory"]?.GetValue<string>(), unreadManifest["config"]!.GetValue<string>(), unreadManifest["mask"]!.ToJsonString()));
        Assert.Equal([FlowOutputLogs.ManifestName], Entries(nowhere).Select(entry => entry.Name));
        Assert.Equal([$"run-orchestrator: outputLogs: {nowhere}", $"run-orchestrator: FAIL - Directory '{missing}' does not exist."], [.. Lines(unread.Output), .. unread.Error]);
    }

    /// <summary>
    /// A path the zip cannot be written at is refused before anything runs - the body never reached - in the command's own
    /// shape, naming no zip.
    /// </summary>
    [Theory]
    [InlineData("directory")]
    [InlineData("inside")]
    [InlineData("parent")]
    public async Task AnUnusablePath_IsRefusedBeforeAnythingRuns_NamingNoZip(string kind)
    {
        var (repository, _) = await FlowServiceTests.RepositoryAsync(Paused);
        using var disposed = repository;
        using var outside = new TempDirectory();
        var file = outside.WriteFile("file.txt", "a file\n");
        var target = kind switch
        {
            "directory" => outside.Path,
            "inside" => Path.Combine(repository.Path, HarnessLayout.OrchestratorsDirectoryName, "logs.zip"),
            _ => Path.Combine(file, "logs.zip"),
        };
        var reached = false;

        var ended = await SessionAsync(
            Options(FlowService.AnswerCommand, repository.Path, target, json: true),
            (_, _) =>
            {
                reached = true;
                return Task.FromResult(CommandOutcome.Ok("answered"));
            });

        Assert.False(reached);
        Assert.Equal(HarnessExit.Refused, ended.Completion.ExitCode);

        var document = JsonNode.Parse(ended.Output)!;
        Assert.Equal(HarnessExit.Refused, document["exitCode"]!.GetValue<int>());
        Assert.EndsWith("Nothing was started.", document["summary"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(document["records"]?["outputLogs"]);
        Assert.Equal([$"answer-orchestrator: FAIL - {document["summary"]!.GetValue<string>()}"], ended.Error);
        Assert.False(File.Exists(target));
    }

    /// <summary>
    /// A zip that could not be written is said in its place and named nowhere: a cause this tool knows as a warning, the
    /// code standing; a defect as a failure, which ends the command as one does - its document saying that code too.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AZipThatCouldNotBeWritten_IsSaid_NamedNowhere_TheCodeStandingSaveForADefect(bool defect)
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var zip = outside.Combine("logs.zip");
        Exception failure = defect ? new InvalidOperationException("the writer broke") : new IOException("no room is left on the device");

        var ended = await SessionAsync(
            Options(FlowService.ResumeCommand, scene.Repository.Path, zip, json: true),
            (scope, _) =>
            {
                scope.Services.GetRequiredService<FlowRunReached>().Reach(scene.Layout, scene.Run);
                return Task.FromResult(new CommandOutcome(FlowExit.Paused, "r1 is paused on q1") { Data = ["""{"exitCode":1}"""] });
            },
            services => services.Replace(ServiceDescriptor.Singleton<IFileSystem>(provider => new FailedWrites(new PhysicalFileSystem(provider.GetRequiredService<IFilePermissions>()), failure))));

        var code = defect ? HarnessExit.InternalError : FlowExit.Paused;
        Assert.Equal(code, ended.Completion.ExitCode);
        Assert.Equal($$"""{"exitCode":{{code}}}""", JsonNode.Parse(ended.Output)!.ToJsonString());
        Assert.Equal(
            [
                defect
                    ? $"resume-orchestrator: FAIL - the output logs '{zip}' could not be written: Unexpected InvalidOperationException: the writer broke"
                    : $"resume-orchestrator: WARN - the output logs '{zip}' could not be written: Unexpected IOException: no room is left on the device",
                "resume-orchestrator: FAIL - r1 is paused on q1",
            ],
            ended.Error);
        Assert.False(File.Exists(zip));
    }

    /// <summary>
    /// A wait the zip's replacing makes - over one another program holds open - is said as the command's lines are at every
    /// end of it, not only where its body answered: a command that ends by a defect, and one refused before its body for a
    /// directory that is not there, each say it, and then write the zip.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AWaitToReplaceTheZip_IsSaid_AtAFailuresEndAndARefusalsToo(bool failed)
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var zip = outside.Combine("logs.zip");
        var told = new DssHarness.Core.Waiting.WaitTold($"'{zip}' to be replaced", TimeSpan.FromMinutes(5), "another program", null);

        var ended = await SessionAsync(
            Options(FlowService.RunCommand, failed ? scene.Repository.Path : outside.Combine("not-there"), zip, json: false),
            (_, _) => throw new InvalidOperationException("the engine met a defect"),
            services => services.Replace(ServiceDescriptor.Singleton<IFileSystem>(provider => new WaitingWrites(new PhysicalFileSystem(provider.GetRequiredService<IFilePermissions>()), told))));

        Assert.Equal(failed ? HarnessExit.InternalError : HarnessExit.UsageError, ended.Completion.ExitCode);
        Assert.Contains($"run-orchestrator: {told.Line()}", Lines(ended.Output));
        Assert.True(File.Exists(zip));
    }

    /// <summary>
    /// Without --json as with it, the operation's own end runs before the zip is written: a defect it meets is the code the
    /// manifest says - the code the process exits with - and the line that says it is among the zip's lines.
    /// </summary>
    [Fact]
    public async Task TheOperationsEnd_RunsBeforeTheZip_WithoutJsonToo_ItsDefectTheManifestsCode()
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var zip = outside.Combine("logs.zip");

        var ended = await SessionAsync(
            Options(FlowService.ResumeCommand, scene.Repository.Path, zip, json: false),
            (scope, _) =>
            {
                scope.Services.GetRequiredService<FlowRunReached>().Reach(scene.Layout, scene.Run);
                return Task.FromResult(new CommandOutcome(FlowExit.Paused, "r1 is paused on q1"));
            },
            services => services.Replace(ServiceDescriptor.Singleton<DssHarness.Core.Hosts.CommandEnd>(_ => throw new InvalidOperationException("the end broke"))));

        Assert.Equal(HarnessExit.InternalError, ended.Completion.ExitCode);

        var manifest = Manifest(zip);
        Assert.Equal(HarnessExit.InternalError, manifest["exitCode"]!.GetValue<int>());
        Assert.Equal(
            [
                "resume-orchestrator: FAIL - this operation's own end could not run: Unexpected InvalidOperationException: the end broke",
                $"resume-orchestrator: outputLogs: {zip}",
                "resume-orchestrator: FAIL - r1 is paused on q1",
            ],
            Values(manifest["output"]!["lines"]!));
    }

    /// <summary>
    /// The second interrupt says the command writes its output logs once the run is stopped, after the bound - never that
    /// the command ends within it - where the zip was asked for, and the bound alone where it was not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheSecondInterrupt_SaysTheZipIsWrittenAfterTheRunIsStopped(bool asked)
    {
        using var scene = await PausedAsync();
        using var outside = new TempDirectory();
        var (standardOutput, standardError) = (new StringWriter(), new StringWriter());
        var output = new ConsoleHarnessOutput(standardOutput, standardError, verbose: false);
        var logs = Logs(scene.Harness);
        var zip = outside.Combine("logs.zip");

        if (asked)
        {
            Assert.Null(await logs.CheckAsync(zip, ["dssharness"], scene.Repository.Path, Token));
        }

        var terminal = new FlowTerminal(output, FlowService.RunCommand, end: logs);
        terminal.Interrupted();
        terminal.Interrupted();

        Assert.Equal(
            asked
                ? $"run-orchestrator: WARN - interrupted again: the run is stopped where it stands, within {PointOfNoReturn.HardStopBound.TotalSeconds} s whatever is left; the command then writes its output logs '{output.Shown(zip)}', for as long as packing the run's records takes, and ends"
                : $"run-orchestrator: WARN - interrupted again: the run is stopped where it stands, and the command ends within {PointOfNoReturn.HardStopBound.TotalSeconds} s whatever is left",
            Lines(standardError.ToString())[^1]);
    }

    /// <summary>A file system whose whole write of a zip first waits, as a rename over a file another program holds tells it.</summary>
    private sealed class WaitingWrites(IFileSystem inner, DssHarness.Core.Waiting.WaitTold told) : PassThroughFileSystem(inner)
    {
        public override void WriteAtomic(string path, Action<Stream> write)
        {
            if (path.EndsWith(".zip", StringComparison.Ordinal))
            {
                DssHarness.Core.Waiting.Waits.Told(told);
            }

            base.WriteAtomic(path, write);
        }
    }

    /// <summary>A command other than the three that drive a session of a run is refused its output logs before any service is built for it.</summary>
    [Fact]
    public void AnotherCommandAskedForOutputLogs_IsRefused_BeforeAnyServiceIsBuilt()
    {
        var built = 0;
        var host = new DssHarnessHost((_, _) =>
        {
            built++;
            return new ServiceCollection().BuildServiceProvider();
        });

        Assert.Equal(
            "'list-orchestrator' writes no output logs (OutputLogs): run-orchestrator, resume-orchestrator, answer-orchestrator alone write them.",
            Assert.Throws<InvalidOperationException>(() => host.Begin(new SessionOptions { Command = "list-orchestrator", OutputLogs = new OutputLogsRequest("logs.zip", ["dssharness"]) })).Message);
        Assert.Equal(0, built);
    }

    /// <summary>A repository whose run r1 of <see cref="Paused"/> is paused on q1.</summary>
    internal static async Task<Scene> PausedAsync(HarnessConfig? config = null)
    {
        var (repository, runner) = await FlowServiceTests.RepositoryAsync(Paused, config);
        var outcome = await FlowServiceTests.Service(repository.Harness, runner)
            .RunAsync(repository.Path, FlowServiceTests.Workflow, run: "r1", inputs: null, json: false, cancellationToken: Token)
            .WaitAsync(FlowServiceTests.Bound, Token);

        Assert.True(outcome.ExitCode == FlowExit.Paused, $"{outcome.ExitCode}: {outcome.Message}");

        var layout = (await repository.Harness.RepositoryLocator.LocateAsync(repository.Path, Token))!;
        return new Scene(repository, layout, FlowServiceTests.RunOf(repository));
    }

    /// <summary>
    /// The output logs over <paramref name="harness"/>'s services, on <paramref name="fileSystem"/>, of a command that reached
    /// <paramref name="reached"/> - where none is given, one whose one session built a mask of no secret.
    /// </summary>
    private static FlowOutputLogs Logs(HarnessFactory harness, IFileSystem? fileSystem = null, TimeProvider? clock = null, FlowRunReached? reached = null)
    {
        if (reached is null)
        {
            reached = new FlowRunReached();
            reached.Masked(new SessionMasks());
        }

        return new(
            fileSystem ?? harness.FileSystem,
            harness.RepositoryLocator,
            harness.GitClient,
            harness.OrchestrationStore,
            harness.Platform,
            harness.ProcessContext,
            new RunningToolDouble("1.0.0"),
            reached,
            capture: null,
            clock ?? new ManualClock());
    }

    /// <summary>Writes the zip of <paramref name="scene"/>'s run as a resume that left it paused would.</summary>
    private static Task WriteAsync(FlowOutputLogs logs, Scene scene, string zip)
        => logs.WriteAsync(
            zip,
            new OutputLogsEnd(
                FlowService.ResumeCommand,
                ["dssharness", FlowService.ResumeCommand, "r1"],
                scene.Repository.Path,
                (scene.Layout, scene.Run),
                FlowExit.Paused,
                "r1 is paused on q1",
                new CapturedOutput(false, null, ["resume-orchestrator: r1 is paused on q1"])),
            Token);

    /// <summary>What a session of <paramref name="command"/> is asked, its zip at <paramref name="zip"/>.</summary>
    private static SessionOptions Options(string command, string directory, string zip, bool json)
        => new()
        {
            Command = command,
            Directory = directory,
            Prompting = false,
            Document = json ? DocumentKind.Own : DocumentKind.None,
            OutputLogs = new OutputLogsRequest(zip, ["dssharness", command]),
        };

    /// <summary>Runs one session of <paramref name="options"/> over the tool's own registrations, changed as asked, writing to buffers.</summary>
    private static async Task<Ended> SessionAsync(
        SessionOptions options,
        Func<CommandScope, CancellationToken, Task<CommandOutcome>> body,
        Func<IServiceCollection, IServiceCollection>? change = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = new DssHarnessHost((asked, operation) => (change ?? (services => services))(
                TestRegistrations.Of(asked, operation, output: provider => new ConsoleHarnessOutput(output, error, verbose: false, provider.GetRequiredService<HomeShorthand>())))
            .BuildServiceProvider());
        var session = host.Begin(
            options,
            options.Document == DocumentKind.Own ? new SessionFrontEnd(Stopped: (_, exitCode, summary) => new JsonObject { ["exitCode"] = exitCode, ["summary"] = summary }.ToJsonString()) : null);
        CommandCompletion completion;

        await using (session.ConfigureAwait(false))
        {
            completion = await session.RunAsync(body, Token);
        }

        return new Ended(completion, output.ToString(), Lines(error.ToString()));
    }

    private static void Write(OrchestratorLayout run, string relative, string text)
    {
        var path = Path.Combine([run.Directory, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>What a paused run's zip leaves out where nothing else is planted: its temp, which every run has.</summary>
    private static JsonArray TempLeftOut()
        => new(new JsonObject { ["path"] = $"{FlowOutputLogs.RunFolder}/{HarnessLayout.OrchestratorTempDirectoryName}", ["why"] = FlowOutputLogs.TempWhy });

    private static string Relative(OrchestratorLayout run, string path) => Path.GetRelativePath(run.Directory, path).Replace('\\', '/');

    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Every entry of <paramref name="zip"/>, in the order it was written, with its bytes.</summary>
    internal static List<(string Name, byte[] Bytes)> Entries(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);

        return [.. archive.Entries.Select(entry =>
        {
            using var read = entry.Open();
            using var bytes = new MemoryStream();
            read.CopyTo(bytes);
            return (entry.FullName, bytes.ToArray());
        })];
    }

    /// <summary>The manifest of <paramref name="zip"/>.</summary>
    internal static JsonObject Manifest(string zip) => JsonNode.Parse(Entries(zip).Single(entry => entry.Name == FlowOutputLogs.ManifestName).Bytes)!.AsObject();

    private static string Text(List<(string Name, byte[] Bytes)> entries, string name) => Encoding.UTF8.GetString(entries.Single(entry => entry.Name == name).Bytes);

    private static List<string> Values(JsonNode array) => [.. array.AsArray().Select(value => value!.GetValue<string>())];

    private static List<string> Lines(string text) => [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0)];

    /// <summary>Every string of <paramref name="node"/>, at any depth.</summary>
    private static IEnumerable<string> Strings(JsonNode? node)
        => node switch
        {
            JsonObject members => members.SelectMany(member => Strings(member.Value)),
            JsonArray items => items.SelectMany(Strings),
            JsonValue value when value.TryGetValue<string>(out var text) => [text],
            _ => [],
        };

    /// <summary>A repository, its layout, and its run r1.</summary>
    internal sealed record Scene(FlowServiceTests.Repository Repository, HarnessLayout Layout, OrchestratorLayout Run) : IDisposable
    {
        public HarnessFactory Harness => Repository.Harness;

        public void Dispose() => Repository.Dispose();
    }

    /// <summary>How a session ended: its completion, its standard output, and its standard error's lines.</summary>
    private sealed record Ended(CommandCompletion Completion, string Output, List<string> Error);

    /// <summary>A file system that keeps the path of every whole write it is asked.</summary>
    private sealed class RecordedWrites(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public List<string> Written { get; } = [];

        public override void WriteAtomic(string path, Action<Stream> write)
        {
            Written.Add(path);
            base.WriteAtomic(path, write);
        }
    }

    /// <summary>A file system whose whole write of a zip is made and then fails as <paramref name="failure"/>, before it replaces anything; every other write as it is.</summary>
    private sealed class FailedWrites(IFileSystem inner, Exception failure) : PassThroughFileSystem(inner)
    {
        public override void WriteAtomic(string path, Action<Stream> write)
        {
            if (!path.EndsWith(".zip", StringComparison.Ordinal))
            {
                base.WriteAtomic(path, write);
                return;
            }

            using var made = new MemoryStream();
            write(made);
            throw failure;
        }
    }
}
