using RepoHarness.Core.Execution;
using RepoHarness.Core.Testing;

namespace RepoHarness.Tests;

/// <summary>What ctest's own output says, and what two of its patterns side by side choose, read as ctest 4.3.2 was measured to.</summary>
public sealed class CtestTests
{
    /// <summary>
    /// ctest found no test where a line of its own says so - the one it prints given any option, or the one it prints
    /// given none - read whole, whatever ends the line or pads it, and whichever name or path starts it; not where the
    /// line is only quoted inside another, nor beside the summary of tests that ran, which ctest prints only where it
    /// ran some, nor in another runner's output, nor in none.
    /// </summary>
    [Theory]
    [InlineData("ctest", "No tests were found!!!", true)]
    [InlineData("ctest", "Test project /src/build\nNo tests were found!!!\n", true)]
    [InlineData("ctest", "Test project C:/src/build\r\nNo tests were found!!!\r\n", true)]
    [InlineData("ctest", "  No tests were found!!!   ", true)]
    [InlineData("ctest3", "No test configuration file found!", true)]
    [InlineData("C:/Program Files/CMake/bin/ctest.exe", "No tests were found!!!", true)]
    [InlineData("ctest", "the test printed 'No tests were found!!!' and failed", false)]
    [InlineData("ctest", "No tests were found!!!\n50% tests passed, 1 tests failed out of 2", false)]
    [InlineData("ctest", "", false)]
    [InlineData("dart", "No tests were found!!!", false)]
    public void FoundNone_ReadsCtestsOwnLine(string runner, string output, bool foundNone)
        => Assert.Equal(foundNone, Ctest.FoundNone(runner, PhaseOutput.Of(output)));

    /// <summary>
    /// Output that could not be read back says nothing of ctest having found no test: the phase's own verdict stands,
    /// unexplained, and never ends as a defect of this tool's over a sentence that only explains it.
    /// </summary>
    [Fact]
    public void FoundNone_IsNotSaidOfOutputThatCouldNotBeReadBack()
        => Assert.False(Ctest.FoundNone("ctest", new PhaseOutputTests.Unread()));

    /// <summary>
    /// ctest's summary settles that tests ran, and nothing after it is read: a suite's output can be larger than any text
    /// the harness could hold.
    /// </summary>
    [Fact]
    public void FoundNone_ReadsNoFurtherThanCtestsSummary()
        => Assert.False(
            Ctest.FoundNone(
                "ctest",
                new PhaseOutputTests.ReadUpTo(
                    line => line.Contains("tests passed", StringComparison.Ordinal),
                    "No tests were found!!!",
                    "50% tests passed, 1 tests failed out of 2",
                    "Total Test time (real) =   0.02 sec")));

    /// <summary>
    /// One pattern leaves out every test another chooses, as far as their spelling tells, where the two are spelled the
    /// same, or are plain text and the chosen one holds the other: ctest finds a pattern anywhere in a name or a label,
    /// so -L git-state beside -LE git, and -R parser_unit beside -E parser, choose no test. Not the other way round -
    /// -R parser beside -E parser_unit runs parser - nor in another case, as -L Git-State beside -LE git-state runs a
    /// test labelled Git-State, nor where either reads anything specially, which a guess could get wrong.
    /// </summary>
    [Theory]
    [InlineData("git-state", "git-state", true)]
    [InlineData("^git-state$", "^git-state$", true)]
    [InlineData("git-state", "git", true)]
    [InlineData("parser_unit", "parser", true)]
    [InlineData("parser", "parser_unit", false)]
    [InlineData("Git-State", "git-state", false)]
    [InlineData("^git-state$", "git-state", false)]
    [InlineData("git-state", "^git", false)]
    [InlineData("git.state", "git", false)]
    public void Covers_IsToldFromSpellingAlone(string chosen, string leftOut, bool covers)
        => Assert.Equal(covers, Ctest.Covers(chosen, leftOut));

    /// <summary>
    /// The args choose tests by name where they give -R a value, in either spelling and any form: an empty one ctest
    /// reads as not given, -E chooses nothing, and another runner's -R is its own.
    /// </summary>
    [Theory]
    [InlineData("ctest", new[] { "-R", "parser" }, true)]
    [InlineData("ctest", new[] { "--tests-regex=parser" }, true)]
    [InlineData("ctest3", new[] { "-R", "parser" }, true)]
    [InlineData("ctest", new[] { "-R", "" }, false)]
    [InlineData("ctest", new[] { "-E", "parser" }, false)]
    [InlineData("dart", new[] { "-R", "parser" }, false)]
    public void ChoosesByName_ReadsTheValueGiven(string runner, string[] args, bool chooses)
        => Assert.Equal(chooses, Ctest.ChoosesByName(runner, args));
}
