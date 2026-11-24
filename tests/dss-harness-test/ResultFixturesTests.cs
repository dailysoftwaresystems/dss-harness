using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The fixtures of the results forms: real output from each tool the parsers read - captured from the tool where it runs
/// here, written from its documented form where it does not - each read through the parsers the phase that printed it
/// chooses, and held two ways. Its <c>expect.json</c>, written by hand from the tool's own words and never by the code,
/// says what a person reads off the output: the counts, the first error and its place, the failures' names, the first
/// stack's frames and which of them are the project's, the causes; it is asserted first. Its <c>digest.json</c>, the whole
/// document, pins everything else against drift, and only it is rewritten when the regenerate switch is set. The bytes a
/// model is handed for every fixture are pinned in all. Each digest is read against the tree its tool ran in as the
/// expectation names it - its root, holding exactly the files the expectation calls the tree's - so whether a frame is
/// the tree's is the same answer on every machine and every system, as the tool's own paths are.
/// </summary>
public sealed class ResultFixturesTests
{
    private static string Root => Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test", "fixtures", "results");

    /// <summary>The bytes every fixture's digest said for a model costs, in all: pinned, so a change that grows or shrinks them is seen.</summary>
    private const int HeldBytes = 32_602;

    /// <summary>Every fixture's directory: one tool's output, its <c>expect.json</c> and its <c>digest.json</c> inside.</summary>
    private static IReadOnlyList<string> Fixtures =>
        [.. Directory.GetDirectories(Root).Order(StringComparer.Ordinal).Select(Path.GetFileName)!];

    public static TheoryData<string> Each()
    {
        var fixtures = new TheoryData<string>();

        foreach (var fixture in Fixtures)
        {
            fixtures.Add(fixture);
        }

        return fixtures;
    }

    /// <summary>
    /// Each tool's output gives what a person reads off it, as its <c>expect.json</c> says: the counts of each severity and
    /// outcome, the first error's tool, code, file, line and column, each diagnostic's severity, code and place, each
    /// failure's name, the first stack's frames and which of them are inside the tree that printed them, the causes, and
    /// how many lines that look like errors no parser read.
    /// </summary>
    [Theory]
    [MemberData(nameof(Each))]
    public void EachToolsOutput_GivesWhatAPersonReadsOffIt(string fixture)
    {
        var expect = Expectation(fixture);
        var digest = Read(fixture, expect);

        Assert.NotNull(digest);
        Assert.Null(digest!.Problem());

        Assert.Equal(
            (Count(expect, "errors"), Count(expect, "warnings"), Count(expect, "notes"), Count(expect, "failed"), Count(expect, "errored"), Count(expect, "timedOut"), Count(expect, "crashed"), Count(expect, "unparsed")),
            (digest.Errors, digest.Warnings, digest.Notes, digest.Failed, digest.Errored, digest.TimedOut, digest.Crashed, digest.Unparsed?.Count ?? 0));

        if (expect.ContainsKey("first"))
        {
            Assert.Equal(
                expect["first"] is JsonObject first ? $"{first["tool"]} {first["code"] ?? "-"} {first["file"] ?? "-"}:{first["line"] ?? "-"}:{first["column"] ?? "-"}" : null,
                digest.First is { } said ? $"{said.Tool} {said.Code ?? "-"} {said.File ?? "-"}:{Text(said.Line)}:{Text(said.Column)}" : null);
        }

        if (expect["diagnostics"] is JsonArray diagnostics)
        {
            Assert.Equal(
                diagnostics.Select(diagnostic => diagnostic!.GetValue<string>()),
                digest.Diagnostics.Select(diagnostic => $"{diagnostic.Severity} {diagnostic.Code ?? "-"} {diagnostic.File ?? "-"}:{Text(diagnostic.Line)}{(diagnostic.Count > 1 ? $" x{diagnostic.Count}" : string.Empty)}"));
        }

        if (expect["failures"] is JsonArray failures)
        {
            Assert.Equal(
                failures.Select(failure => failure!.GetValue<string>()),
                digest.Failures.Select(failure => $"{failure.Outcome} {failure.Id}"));
        }

        if (expect["stack"] is JsonObject stack)
        {
            // The frames the tool printed, said as it printed them, and which of them the digest placed in the tree.
            var frames = StackOf(digest, stack["of"]?.GetValue<string>());
            var placed = StackOf(Placed(fixture, expect, digest), stack["of"]?.GetValue<string>());

            Assert.Equal(
                $"{stack["frames"]!.GetValue<int>()} frames; in the tree: {string.Join(" | ", stack["inTree"]!.AsArray().Select(frame => frame!.GetValue<string>()))}",
                $"{frames.Count} frames; in the tree: {string.Join(" | ", frames.Where((_, at) => placed[at].InTree).Select(frame => $"{frame.File}:{Text(frame.Line)}"))}");
        }

        if (expect["causes"] is JsonArray causes)
        {
            Assert.Equal(causes.Select(cause => cause!.GetValue<string>()), CausesOf(digest));
        }
    }

    /// <summary>
    /// Each tool's output gives the whole digest stored beside it, every member of every finding: what the expectation
    /// does not name, pinned against drift. Only this file is rewritten when <c>DSSHARNESS_REGENERATE_RESULTS</c> is set -
    /// never the expectation a person wrote.
    /// </summary>
    [Theory]
    [MemberData(nameof(Each))]
    public void EachToolsOutput_GivesItsStoredDigest(string fixture)
    {
        var expect = Expectation(fixture);
        var document = Placed(fixture, expect, Read(fixture, expect)!).Document().ToJsonString();
        var stored = Path.Combine(Root, fixture, "digest.json");

        if (Environment.GetEnvironmentVariable("DSSHARNESS_REGENERATE_RESULTS") is not null)
        {
            File.WriteAllText(stored, document);
            return;
        }

        Assert.True(File.Exists(stored), $"no stored digest for '{fixture}': run once with DSSHARNESS_REGENERATE_RESULTS=1 to write it, then read it against the output");
        Assert.Equal(File.ReadAllText(stored).TrimEnd(), document);
    }

    /// <summary>
    /// The bytes a model is handed, held: for every fixture, a short log's among them, the digest said for a model - written
    /// as every JSON a model reads is (AgentEnvelope.Written) - costs no more than what the model was handed before digests
    /// existed - the fifty last lines, and the whole log the old instruction sent it to read beside them. The total over
    /// every fixture is pinned exactly, so a change that grows or shrinks what a model reads is a change someone made on
    /// purpose.
    /// </summary>
    [Fact]
    public void TheBytesAModelIsHanded_AreHeld_AndTheirTotalIsPinned()
    {
        var total = 0;

        foreach (var fixture in Fixtures)
        {
            var expect = Expectation(fixture);
            var lines = File.ReadAllLines(Output(fixture));
            var held = Encoding.UTF8.GetByteCount(Placed(fixture, expect, Read(fixture, expect)!).ForAModel().ToJsonString(DssHarness.Core.Agents.AgentEnvelope.Written));

            var before = Encoding.UTF8.GetByteCount(string.Join("\n", lines.TakeLast(50))) + Encoding.UTF8.GetByteCount(string.Join("\n", lines));

            Assert.True(held <= before, $"'{fixture}': the digest for a model costs {held} bytes against the {before} it was handed before");

            total += held;
        }

        Assert.Equal(HeldBytes, total);
    }

    /// <summary>The fixture's output: the one file beside its expectation and its stored digest.</summary>
    private static string Output(string fixture)
        => Directory.GetFiles(Path.Combine(Root, fixture))
            .Single(file => Path.GetFileName(file) is not ("digest.json" or "expect.json"));

    private static JsonObject Expectation(string fixture)
        => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, fixture, "expect.json")))!.AsObject();

    /// <summary>
    /// Reads one fixture's output as the phase that printed it reads it (<c>read</c>): a build's by its project type and the
    /// compiler its toolchain declares, a test run's by its runner, a runner step's, a CI job's log as a red check's is read
    /// for the repository its job ran (<c>ci</c>), or a results file by its own shape.
    /// </summary>
    private static ResultDigest? Read(string fixture, JsonObject expect)
    {
        var output = Output(fixture);
        var read = expect["read"]!.AsObject();

        if (read.ContainsKey("file"))
        {
            return ResultFiles.Digest(File.ReadAllText(output), out _);
        }

        if (read["ci"] is { } repository)
        {
            return DssHarness.Core.Ci.CiDigest.Of(File.ReadAllText(output), [], repository.GetValue<string>(), said => said, []);
        }

        var config = new HarnessConfig();
        IReadOnlyList<IResultParser> parsers;

        if (read["build"] is { } build)
        {
            if (read["compiler"] is { } compiler)
            {
                config.Toolchains["fixture"] = new ToolchainConfig { CompilerId = { ["C"] = compiler.GetValue<string>(), ["CXX"] = compiler.GetValue<string>() } };
            }

            parsers = PhaseParsers.ForBuild(config, build.GetValue<string>(), read.ContainsKey("compiler") ? "fixture" : null);
        }
        else if (read["tests"] is { } runner)
        {
            parsers = PhaseParsers.ForTests(config, runner.GetValue<string>(), null);
        }
        else
        {
            parsers = PhaseParsers.ForRun(config, new RunnerConfig());
        }

        return PhaseParsers.DigestOf(File.ReadLines(output), parsers);
    }

    /// <summary>
    /// <paramref name="digest"/> with every place read against the tree its tool ran in, as a run reads it: the
    /// expectation's root, holding exactly the files its <c>tree</c> names - every project file the output names, each
    /// below the root with forward slashes, as a real tree holds them, and with its text where a file's text is read in
    /// placing (<c>{"path": ..., "text": ...}</c>: a <c>go.mod</c>) - a relative path read from where the tool ran
    /// (<c>workingDirectory</c>, the root where it names none), so the in-tree marks are what a real run gives, and the
    /// same on every machine. The frames the expectation calls the tree's are among those files. A CI job's digest is
    /// placed already, against the job's workspace on its runner.
    /// </summary>
    private static ResultDigest Placed(string fixture, JsonObject expect, ResultDigest digest)
    {
        if (expect["read"]!.AsObject().ContainsKey("ci"))
        {
            return digest;
        }

        var root = expect["root"]?.GetValue<string>() ?? throw new InvalidOperationException($"'{fixture}': its expectation names no root to read its places against");
        var ranIn = expect["workingDirectory"]?.GetValue<string>();
        var held = (expect["tree"] as JsonArray ?? throw new InvalidOperationException($"'{fixture}': its expectation names no tree - every project file its output names, below its root"))
            .Select(file => file is JsonObject withText
                ? (Path: withText["path"]!.GetValue<string>(), Text: withText["text"]!.GetValue<string>())
                : (Path: file!.GetValue<string>(), Text: string.Empty))
            .ToDictionary(file => file.Path, file => file.Text, StringComparer.Ordinal);

        foreach (var frame in (expect["stack"]?["inTree"]?.AsArray() ?? []).Select(frame => frame!.GetValue<string>()))
        {
            if (PlatformPaths.RelativeAsWritten(root, frame[..frame.LastIndexOf(':')], ranIn) is not { } file || !held.ContainsKey(file))
            {
                throw new InvalidOperationException($"'{fixture}': '{frame}' is in the tree by its expectation, and names no file of its tree");
            }
        }

        return digest.WithInTree(new DigestTree(root, ranIn, new FixtureTree(root, held)));
    }

    /// <summary>
    /// The stack the expectation names: the failure's named <paramref name="of"/> where it names one, else the first a reader
    /// meets - the first error's chain where there is a first error, else the first failure's thrown error's stack, else its
    /// own.
    /// </summary>
    private static IReadOnlyList<StackFrame> StackOf(ResultDigest digest, string? of)
        => (of is null ? digest.First : null) is { } first ? first.Stack ?? []
            : digest.Failures.FirstOrDefault(failure => of is null || failure.Id == of) is { } failure ? failure.Exception?.Stack ?? failure.Stack ?? []
            : [];

    /// <summary>The causes of the first failure that is a thrown error, outermost first, each the first cause of the one before.</summary>
    private static List<string> CausesOf(ResultDigest digest)
    {
        var causes = new List<string>();

        for (var cause = digest.Failures.FirstOrDefault(failure => failure.Exception is not null)?.Exception!.CausedBy.FirstOrDefault(); cause is not null; cause = cause.CausedBy.FirstOrDefault())
        {
            causes.Add(cause.Type);
        }

        return causes;
    }

    private static int Count(JsonObject expect, string name) => expect[name]?.GetValue<int>() ?? 0;

    /// <summary>
    /// The tree a fixture's tool ran in, holding exactly <paramref name="held"/>, each file with its text: a file is the
    /// tree's where its path is <paramref name="root"/>, a separator and one of them - the root joined to a name below it
    /// by whichever machine reads it, one form's root joined by the other's separator among them; a directory holds the
    /// directories its files are below, each named as the directory joined to it with <c>/</c>; nothing in it is a link,
    /// and nothing outside it is read from the disk.
    /// </summary>
    private sealed class FixtureTree(string root, IReadOnlyDictionary<string, string> held) : PassThroughFileSystem(new DssHarness.Core.FileSystem.PhysicalFileSystem(FilePermissionsFactory.Create()))
    {
        public override bool FileExists(string path) => Below(path) is { } file && held.ContainsKey(file);

        public override bool IsLink(string path) => false;

        public override Stream OpenRead(string path)
            => Below(path) is { } file && held.TryGetValue(file, out var text)
                ? new MemoryStream(Encoding.UTF8.GetBytes(text))
                : throw new FileNotFoundException($"'{path}' is no file of the fixture's tree.", path);

        public override IEnumerable<string> EnumerateDirectories(string path)
        {
            var directory = path == root ? string.Empty
                : Below(path) is { } below ? $"{below}/"
                : throw new DirectoryNotFoundException($"'{path}' is no directory of the fixture's tree.");
            var names = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var file in held.Keys.Where(file => file.StartsWith(directory, StringComparison.Ordinal)))
            {
                if (file.IndexOf('/', directory.Length) is var slash and >= 0)
                {
                    names.Add(file[directory.Length..slash]);
                }
            }

            return [.. names.Select(name => $"{path}/{name}")];
        }

        /// <summary>A path below the root, with forward slashes; null where it is not below it.</summary>
        private string? Below(string path)
            => path.Length > root.Length + 1 && path.StartsWith(root, StringComparison.Ordinal) && path[root.Length] is '/' or '\\'
                ? path[(root.Length + 1)..].Replace('\\', '/')
                : null;
    }

    private static string Text(int? number) => number?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
