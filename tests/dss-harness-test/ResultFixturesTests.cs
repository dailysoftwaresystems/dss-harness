using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The fixtures of the results forms: real output from each tool the parsers read, each with the digest it must
/// produce, stored beside it (<c>digest.json</c>, the whole document) - so a parser that drifts from what its tool
/// prints fails here, against the tool's own words and not a hand-typed expectation. The bytes a model is handed are
/// held too: for every fixture, the digest said for a model never costs more than the fifty tail lines the model was
/// handed before, and their total is pinned - a change that grows them fails, as the graph's serving test does.
/// </summary>
public sealed class ResultFixturesTests
{
    private static string Root => Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test", "fixtures", "results");

    /// <summary>The tree each fixture's places are read against: this repository's, which holds none of the files the fixtures' tools named.</summary>
    private static DigestTree Tree => new(Prose.RepositoryRoot(), null, new DssHarness.Core.FileSystem.PhysicalFileSystem(DssHarness.Core.Platform.FilePermissionsFactory.Create()));

    /// <summary>The bytes every fixture's digest said for a model costs, in all: the bound a change that grows them fails against.</summary>
    private const int HeldBytes = 19_000;

    /// <summary>Every fixture's directory: one a tool, its output and its stored digest inside.</summary>
    private static IReadOnlyList<string> ToolNames =>
        [.. Directory.GetDirectories(Root).Order(StringComparer.Ordinal).Select(Path.GetFileName)!];

    public static TheoryData<string> Tools()
    {
        var tools = new TheoryData<string>();

        foreach (var tool in ToolNames)
        {
            tools.Add(tool);
        }

        return tools;
    }

    /// <summary>Each tool's output gives the digest stored beside it, whole: every diagnostic and failure, their counts, the unparsed lines.</summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public void EachToolsOutput_GivesItsStoredDigest(string tool)
    {
        var directory = Path.Combine(Root, tool);
        var output = Directory.GetFiles(directory).Where(file => Path.GetFileName(file) != "digest.json").Order(StringComparer.Ordinal).First();
        var digest = Read(output, tool);

        Assert.NotNull(digest);
        Assert.Null(digest!.Problem());

        var document = digest.WithInTree(Tree).Document().ToJsonString();
        var stored = Path.Combine(directory, "digest.json");

        if (Environment.GetEnvironmentVariable("DSSHARNESS_REGENERATE_RESULTS") is not null)
        {
            File.WriteAllText(stored, document);
            return;
        }

        Assert.True(File.Exists(stored), $"no stored digest for '{tool}': run once with DSSHARNESS_REGENERATE_RESULTS=1 to write it");
        Assert.Equal(File.ReadAllText(stored).TrimEnd(), document);
    }

    /// <summary>
    /// The bytes a model is handed, held: for every fixture, the digest said for a model costs no more than what the
    /// model was handed before digests existed - the fifty tail lines plus the log read the old instruction sent it on
    /// ("read the output and find the first thing that went wrong") - and their total is pinned, so a change that grows
    /// them fails. What the digest buys for those bytes is the first error, its place, its stack and its causes.
    /// </summary>
    [Fact]
    public void TheBytesAModelIsHanded_AreHeld_TheDigestNeverAboveTheTailAndTheRead()
    {
        var total = 0L;

        foreach (var tool in ToolNames)
        {
            var directory = Path.Combine(Root, tool);
            var output = Directory.GetFiles(directory).Where(file => Path.GetFileName(file) != "digest.json").Order(StringComparer.Ordinal).First();
            var digest = Read(output, tool);

            Assert.NotNull(digest);

            var lines = File.ReadAllLines(output);
            var before = Encoding.UTF8.GetByteCount(string.Join("\n", lines.TakeLast(50))) + Encoding.UTF8.GetByteCount(string.Join("\n", lines));
            var held = digest!.WithInTree(Tree).ForAModel().ToJsonString();

            Assert.True(
                Encoding.UTF8.GetByteCount(held) <= before,
                $"'{tool}': the digest for a model costs {Encoding.UTF8.GetByteCount(held)} bytes against the tail-and-read's {before}");
            total += Encoding.UTF8.GetByteCount(held);
        }

        Assert.True(total <= HeldBytes, $"every fixture's digest for a model, in bytes: {total} (held at {HeldBytes})");
    }

    /// <summary>Reads one fixture's output: a file form by its own shape, a CI log with the forge's marks stripped and the whole CI choice, a tool's lines by its own parser.</summary>
    private static ResultDigest? Read(string output, string tool)
    {
        var text = File.ReadAllText(output);

        if (Path.GetExtension(output) is ".xml" or ".trx" or ".json" or ".jsonl")
        {
            return ResultFiles.Digest(text, out _);
        }

        if (tool == "ci-wait")
        {
            return PhaseParsers.DigestOf(PhaseParsers.StrippedCi(text), PhaseParsers.ForCi([]));
        }

        return PhaseParsers.DigestOf([.. File.ReadLines(output)], [ResultParsers.Of(tool)!]);
    }
}
