using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Output;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>Which failures an expected exception explains, and on which legs.</summary>
public sealed class ExpectedExceptionMatcherTests
{
    [Fact]
    public void Find_MatchesAMessageByRegularExpression()
    {
        var entry = Entry(messages: [@"device or resource busy \(\d+\)"]);

        Assert.Same(
            entry,
            Matcher().Find(
                Scope("corpus", ["linux-arm64-qemu"], entry),
                "linux-arm64-qemu",
                new RunFailure("IOException", "device or resource busy (16)")));
    }

    [Fact]
    public void Find_MatchesAMessageAsPlainText_WhenItIsNotARegularExpression()
    {
        // 'qemu: uncaught signal 11 (' does not compile as a regular expression, and an author
        // writing plain text should not have to know which characters this engine treats as syntax.
        var entry = Entry(messages: ["qemu: uncaught signal 11 ("]);

        Assert.Same(
            entry,
            Matcher().Find(
                Scope("corpus", ["linux-arm64-qemu"], entry),
                "linux-arm64-qemu",
                new RunFailure("IOException", "qemu: uncaught signal 11 (core dumped)")));
    }

    [Fact]
    public void Find_MatchesAgainstTheFailuresOutput_WhenItCarriedNoMessage()
    {
        var entry = Entry(messages: ["resource busy"]);

        Assert.Same(
            entry,
            Matcher().Find(
                Scope("corpus", ["linux-arm64-qemu"], entry),
                "linux-arm64-qemu",
                new RunFailure("IOException", Output: PhaseOutput.Of("...\nfopen: resource busy\n..."))));
    }

    /// <summary>
    /// A message is matched against each line the failing unit printed, as every pattern read from a phase's output is: one
    /// deep in a long output is found, and one that only matches across a line break is not.
    /// </summary>
    [Fact]
    public void Find_MatchesAMessageAgainstEachLineOfTheOutput()
    {
        var output = PhaseOutput.Of(string.Concat(Enumerable.Range(0, 5000).Select(line => $"case {line} ok\n")) + "fopen: resource busy\n");
        var deep = Entry(messages: ["^fopen: resource busy$"]);
        var across = Entry(messages: ["ok\nfopen"]);

        Assert.Same(deep, Matcher().Find(Scope("corpus", ["linux-arm64-qemu"], deep), "linux-arm64-qemu", new RunFailure("IOException", Output: output)));
        Assert.Null(Matcher().Find(Scope("corpus", ["linux-arm64-qemu"], across), "linux-arm64-qemu", new RunFailure("IOException", Output: output)));
    }

    /// <summary>
    /// The failing unit's output is read once for an entry, however many messages the entry lists: it is read from the
    /// unit's log, and a log can run to gigabytes.
    /// </summary>
    [Fact]
    public void Find_ReadsTheOutputOnceForAnEntry_HoweverManyMessagesItLists()
    {
        var output = new CountedReads("case 1 ok", "fopen: resource busy");
        var entry = Entry(messages: ["no such message", "^fopen: resource busy$"]);

        Assert.Same(entry, Matcher().Find(Scope("corpus", ["linux-arm64-qemu"], entry), "linux-arm64-qemu", new RunFailure("IOException", Output: output)));
        Assert.Equal(1, output.Reads);
    }

    [Fact]
    public void Find_ComparesTheTypeCaseIncluded()
    {
        var entry = Entry(exceptionType: "IOException", messages: ["busy"]);
        var scope = Scope("corpus", ["linux-arm64-qemu"], entry);

        Assert.Null(Matcher().Find(scope, "linux-arm64-qemu", new RunFailure("ioexception", "busy")));
        Assert.Null(Matcher().Find(scope, "linux-arm64-qemu", new RunFailure("TimeoutException", "busy")));

        // A namespace-qualified report is reduced to its class name, because the entry declares one.
        Assert.Same(
            entry,
            Matcher().Find(scope, "linux-arm64-qemu", new RunFailure("System.IO.IOException", "busy")));
    }

    [Fact]
    public void Find_DoesNotExcuseALegTheRunnerDidNotDeclare()
    {
        // An excusal earned under emulation is a fact about emulation. Offered to the native leg it
        // would excuse the regression it was written to explain, on the one leg where nothing does.
        var entry = Entry(messages: ["busy"]);
        var scope = Scope("corpus", ["linux-arm64-qemu"], entry);
        var failure = new RunFailure("IOException", "device or resource busy");

        Assert.Same(entry, Matcher().Find(scope, "linux-arm64-qemu", failure));
        Assert.Null(Matcher().Find(scope, "linux-arm64-native", failure));
    }

    [Fact]
    public void Find_ReturnsNull_WhenTheEntryNamesNoMessages()
    {
        // An entry recognising every failure of its type is an unconditional claim spelled as a
        // scope; it excuses nothing here, as the lint refuses it in configuration.
        var entry = Entry(messages: []);

        Assert.Null(Matcher().Find(
            Scope("corpus", ["linux-arm64-qemu"], entry),
            "linux-arm64-qemu",
            new RunFailure("IOException", "device or resource busy")));
    }

    [Fact]
    public void Find_TakesTheFirstEntryThatRecognisesTheFailure()
    {
        var first = Entry(messages: ["never matches this"], message: "first");
        var second = Entry(messages: ["busy"], message: "second");

        var found = Matcher().Find(
            Scope("corpus", ["linux-arm64-qemu"], first, second),
            "linux-arm64-qemu",
            new RunFailure("IOException", "device or resource busy"));

        Assert.Same(second, found);
    }

    [Fact]
    public void From_NarrowsToTheResolvedLegs_WhenTheRunnerDeclaresNone()
    {
        var entry = Entry(messages: ["busy"]);
        var runner = new RunnerConfig { ExpectedExceptions = [entry] };
        var scope = RunnerScope.From("corpus", runner, ["linux-arm64-qemu"]);

        Assert.True(scope.Covers("linux-arm64-qemu"));
        Assert.False(scope.Covers("linux-arm64-native"));
    }

    [Fact]
    public void RunOutcome_From_CarriesTheEntrysDeclaredOutcome()
    {
        var outcome = RunOutcome.From(Entry(messages: ["busy"], message: "excused: the device was busy"));

        Assert.False(outcome.Success);
        Assert.True(outcome.Warning);
        Assert.Equal(0, outcome.ResultCode);
        Assert.Equal("excused: the device was busy", outcome.Message);
    }

    internal static ExpectedException Entry(
        string exceptionType = "IOException",
        IEnumerable<string>? messages = null,
        string message = "excused",
        IEnumerable<RunCheck>? runChecks = null,
        bool success = false,
        bool warning = true,
        int resultCode = 0)
        => new()
        {
            ExceptionType = exceptionType,
            Messages = [.. messages ?? []],
            Success = success,
            Warning = warning,
            ResultCode = resultCode,
            Message = message,
            EarnedOn = "2026-09-16",
            EarnedAt = "linux-arm64-qemu",
            Mechanism = "the emulated block device reports EBUSY while the host flushes its cache",
            Anchor = "D-QEMU-EBUSY",
            RunChecks = [.. runChecks ?? []],
        };

    internal static RunnerScope Scope(string runner, IReadOnlyList<string> legs, params ExpectedException[] entries)
        => new(runner, legs, entries);

    private static ExpectedExceptionMatcher Matcher()
        => new(new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));

    /// <summary>Output that reads as <paramref name="lines"/>, counting how often it is read.</summary>
    private sealed class CountedReads(params string[] lines) : PhaseOutput
    {
        public int Reads { get; private set; }

        public override IEnumerable<string> Lines()
        {
            Reads++;
            return lines;
        }
    }
}
