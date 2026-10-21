using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The run's list of issues it found and left, <c>pre-existing-issues.json</c>: created empty with the run, an entry
/// added at each triage answer <c>list</c> by one writer alone, always whole and atomically - each entry proven, with
/// a title and a description, and a quote or an output cut to 4,000 characters, the cut said.
/// </summary>
public sealed class PreExistingIssuesTests
{
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>The run's records in a directory of its own, with the issues' writer out.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, PreExistingIssuesWriter Issues, FlowRunRecords Records) Writer(string directory)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), "r1");
        var records = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", Parse("name: flow\nsteps:\n  - name: one\n    type: orient\n")));
        return (harness, layout, new PreExistingIssuesWriter(harness.OrchestrationStore, layout, "r1"), records);
    }

    /// <summary>The list is created empty with the run, and reads back as exactly that.</summary>
    [Fact]
    public void TheListOfIssuesIsCreatedEmptyWithTheRun()
    {
        using var temp = new TempDirectory();
        var (harness, layout, _, records) = Writer(temp.Path);

        using (records)
        {
            var issues = harness.OrchestrationStore.ReadPreExistingIssues(layout);

            Assert.NotNull(issues);
            Assert.Equal(1, issues.Schema);
            Assert.Equal("r1", issues.Run);
            Assert.Empty(issues.Issues);
            Assert.EndsWith("}\n", harness.FileSystem.ReadAllText(layout.PreExistingIssuesFile), StringComparison.Ordinal);
        }
    }

    /// <summary>An entry added is the next <c>pe-&lt;n&gt;</c> in the order added, written whole with when it was written, and never loses one or adds one twice.</summary>
    [Fact]
    public void AnEntryAddedIsTheNextInOrder_NeverLostNeverTwice()
    {
        using var temp = new TempDirectory();
        var (harness, layout, issues, records) = Writer(temp.Path);
        var at = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        using (records)
        {
            var first = issues.Add(
                "LegLedger drops the last line",
                "The ledger reads a leg's output by lines and keeps only those that end in a newline.",
                ["src/DssHarness.Core/Execution/LegLedger.cs"],
                new PreExistingProof("read", "3e0b8f5", "src/DssHarness.Core/Execution/LegLedger.cs", "212-219", "…", null, null),
                "not related to the request's items, not critical, and in no step's way",
                "rounds#1.items[2].implement",
                at,
                at.AddMinutes(1));

            Assert.Equal("pe-1", first.Id);

            var second = issues.Add(
                "Another one",
                "What it is.",
                ["src/A.cs"],
                new PreExistingProof("ran", "3e0b8f5", null, null, null, "dotnet test", "failed"),
                "not critical",
                "rounds#1.items[3].implement",
                at.AddMinutes(2),
                at.AddMinutes(3));

            Assert.Equal("pe-2", second.Id);

            var file = harness.OrchestrationStore.ReadPreExistingIssues(layout);

            Assert.NotNull(file);
            Assert.Equal(["pe-1", "pe-2"], file.Issues.Select(issue => issue.Id));
            Assert.Equal(at.AddMinutes(3), file.UpdatedAt);
        }
    }

    /// <summary>
    /// A quote or an output is cut to 4,000 characters with the cut said inside them - so what is cut is a proof the
    /// reader takes - and one that fits is kept whole.
    /// </summary>
    [Fact]
    public void AQuoteOrAnOutputIsCutToWhatItMayHold_TheCutSaid_AndTheReaderTakesIt()
    {
        var cut = PreExistingIssuesWriter.Cut(new string('x', PreExistingProof.Most + 100));
        var exactly = new string('y', PreExistingProof.Most);

        Assert.Equal(PreExistingProof.Most, cut.Length);
        Assert.EndsWith("… (cut to 4000 characters)", cut, StringComparison.Ordinal);
        Assert.Equal(new string('x', PreExistingProof.Most - PreExistingIssuesWriter.CutMark.Length), cut[..^PreExistingIssuesWriter.CutMark.Length]);
        Assert.Null(new PreExistingProof("ran", "abc123", Command: "make", Output: cut).Problem());
        Assert.Null(new PreExistingProof("read", "abc123", "a.c", "1-2", Quote: cut).Problem());
        Assert.Equal(exactly, PreExistingIssuesWriter.Cut(exactly));
        Assert.Equal("short", PreExistingIssuesWriter.Cut("short"));
    }

    /// <summary>Every entry is proven: what the triage read or ran on the base commit, never what the reporting agent said, and a proof that names nothing of its kind is refused.</summary>
    [Fact]
    public void EveryEntryIsProven_AndAProofThatNamesNothingIsRefused()
    {
        using var temp = new TempDirectory();
        var (harness, layout, issues, records) = Writer(temp.Path);
        var at = DateTimeOffset.UtcNow;

        using (records)
        {
            Assert.Throws<InvalidOperationException>(
                () => issues.Add(
                    "No proof of a read",
                    "What it is.",
                    ["src/A.cs"],
                    new PreExistingProof("read", "3e0b8f5", null, "1-2", "quote", null, null),
                    "why",
                    "one",
                    at,
                    at));

            Assert.Throws<InvalidOperationException>(
                () => issues.Add(
                    "A quote over the bound",
                    "What it is.",
                    ["src/A.cs"],
                    new PreExistingProof("read", "3e0b8f5", "src/A.cs", "1-2", new string('x', PreExistingProof.Most + 1), null, null),
                    "why",
                    "one",
                    at,
                    at));

            Assert.Empty(harness.OrchestrationStore.ReadPreExistingIssues(layout)!.Issues);
        }
    }

    /// <summary>The file is written whole and read back strictly: ids out of order, a member no type declares and a duplicate member are refused, never guessed at.</summary>
    [Fact]
    public void TheFileIsReadStrictly_NeverGuessedAt()
    {
        using var temp = new TempDirectory();
        var (harness, layout, issues, records) = Writer(temp.Path);
        var at = DateTimeOffset.UtcNow;

        using (records)
        {
            issues.Add("One", "What it is.", ["src/A.cs"], new PreExistingProof("ran", "c", null, null, null, "cmd", "out"), "why", "one", at, at);

            var file = harness.OrchestrationStore.ReadPreExistingIssues(layout)!;
            var outOfOrder = file with { Issues = [.. file.Issues.Select((issue, at) => issue with { Id = $"pe-{at + 2}" })] };

            Assert.NotNull(outOfOrder.Problem());

            var raised = Assert.Throws<InvalidOperationException>(() => harness.OrchestrationStore.WritePreExistingIssues(layout, outOfOrder));

            Assert.Contains("pe-<n>", raised.Message, StringComparison.Ordinal);
        }
    }
}
