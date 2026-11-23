using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Runners;

namespace DssHarness.Tests;

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

    /// <summary>
    /// What was expected is often the cause under what was thrown: an entry's type matches the record's causes too,
    /// each one however deep it sits, and its messages still match the texts - so an excusal declared against a cause
    /// is not lost the day the same failure arrives wrapped.
    /// </summary>
    [Fact]
    public void Find_MatchesATypeTheRecordCausessAsToo_HoweverDeepItSits()
    {
        var entry = Entry(exceptionType: "ArgumentException", messages: ["the argument was wrong"]);

        var wrapped = new DssHarness.Core.Results.ExceptionRecord(
            "System.InvalidOperationException",
            "The operation is invalid.",
            [],
            [new DssHarness.Core.Results.ExceptionRecord(
                "System.ArgumentException",
                "the argument was wrong",
                [],
                [new DssHarness.Core.Results.ExceptionRecord("System.Exception", "deepest", [], [])])]);

        Assert.Same(
            entry,
            Matcher().Find(
                Scope("corpus", ["linux-arm64-qemu"], entry),
                "linux-arm64-qemu",
                new RunFailure("InvalidOperationException", "The operation is invalid.", Exception: wrapped)));

        Assert.Null(
            Matcher().Find(
                Scope("corpus", ["linux-arm64-qemu"], entry),
                "linux-arm64-qemu",
                new RunFailure("IOException", "device busy", Exception: new DssHarness.Core.Results.ExceptionRecord("IOException", "device busy", [], []))));
    }

    /// <summary>
    /// Both halves of a match are of one exception: a cause's type is never matched with a message the outer exception
    /// says, another cause's, or a line the unit printed - an entry declared against one cause would excuse a failure
    /// whose message no cause of that type ever said. The cause's type with its own message still matches.
    /// </summary>
    [Fact]
    public void ACausesType_IsNeverMatchedWithAnotherExceptionsMessage()
    {
        var entry = Entry(exceptionType: "ArgumentException", messages: ["the device was busy"]);
        var scope = Scope("corpus", ["linux-arm64-qemu"], entry);

        static DssHarness.Core.Results.ExceptionRecord Wrapped(string outer, string argument, string deepest)
            => new(
                "System.InvalidOperationException",
                outer,
                [],
                [new DssHarness.Core.Results.ExceptionRecord("System.ArgumentException", argument, [], [new DssHarness.Core.Results.ExceptionRecord("System.IO.IOException", deepest, [], [])])]);

        // The message is the outer exception's, the run's, a deeper cause's or a printed line: no match.
        Assert.Null(Matcher().Find(scope, "linux-arm64-qemu", new RunFailure("InvalidOperationException", "the device was busy", Exception: Wrapped("the device was busy", "bad argument", "io"))));
        Assert.Null(Matcher().Find(scope, "linux-arm64-qemu", new RunFailure("InvalidOperationException", "it failed", Exception: Wrapped("it failed", "bad argument", "the device was busy"))));
        Assert.Null(Matcher().Find(scope, "linux-arm64-qemu", new RunFailure("InvalidOperationException", "it failed", PhaseOutput.Of("ArgumentException: the device was busy\n"), Exception: Wrapped("it failed", "bad argument", "io"))));

        // The cause's own message: a match.
        Assert.Same(entry, Matcher().Find(scope, "linux-arm64-qemu", new RunFailure("InvalidOperationException", "it failed", Exception: Wrapped("it failed", "the device was busy", "io"))));
    }

    /// <summary>
    /// A line the unit printed is matched as the message of the exception that reached the log only where no stack was
    /// read: under a stack, a line may be a cause's message - the outer type with a cause's printed message is no match -
    /// and with no stack read, the lines are all there is to read it by.
    /// </summary>
    [Fact]
    public void ALinePrinted_IsTheThrownMessage_OnlyWhereNoStackWasRead()
    {
        var entry = Entry(exceptionType: "InvalidOperationException", messages: ["Permission denied"]);
        var scope = Scope("corpus", ["linux-arm64-qemu"], entry);
        var printed = PhaseOutput.Of("System.InvalidOperationException: flush failed\n ---> System.IO.IOException: Permission denied\n");
        var thrown = new DssHarness.Core.Results.ExceptionRecord(
            "System.InvalidOperationException",
            "flush failed",
            [],
            [new DssHarness.Core.Results.ExceptionRecord("System.IO.IOException", "Permission denied", [], [])]);

        Assert.Null(Matcher().Find(scope, "linux-arm64-qemu", new RunFailure("InvalidOperationException", "step failed", printed, thrown)));
        Assert.Same(entry, Matcher().Find(scope, "linux-arm64-qemu", new RunFailure("InvalidOperationException", "step failed", printed)));
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

    /// <summary>
    /// A failure whose output cannot be read back from its log is matched against no entry as one that printed nothing,
    /// and its type is not read as one it named none of: both are said as unread, so the failure is neither excused nor
    /// called genuine on what is left of a log.
    /// </summary>
    [Fact]
    public void Find_SaysOutputThatCouldNotBeReadBack_NeverAFailureThatPrintedNothing()
    {
        var entry = Entry(messages: ["resource busy"]);

        var unread = Assert.Throws<PhaseOutputUnreadException>(
            () => Matcher().Find(Scope("corpus", ["linux-arm64-qemu"], entry), "linux-arm64-qemu", new RunFailure("IOException", Output: new PhaseOutputTests.Unread())));

        Assert.Equal(PhaseOutputTests.Unread.Said, unread.Message);
        Assert.Equal(PhaseOutputTests.Unread.Said, Assert.Throws<PhaseOutputUnreadException>(() => RunnerRunService.FailureTypeIn(new PhaseOutputTests.Unread())).Message);
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
