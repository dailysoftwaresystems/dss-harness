using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Testing;

namespace RepoHarness.Tests;

/// <summary>
/// What ctest's own description of a leg's tests says: which of the tests it registers start a binary, which of them runs
/// each red case, and what of their entries a sweep that starts the binary itself does not apply.
/// </summary>
public sealed class CtestEntriesTests
{
    private static readonly string Worker = OperatingSystem.IsWindows() ? @"C:\w\fixture_tests.exe" : "/w/fixture_tests";

    private static StringComparer Names => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Start => OperatingSystem.IsWindows() ? @"C:\w" : "/w";

    /// <summary>
    /// A description is read for each test's name, command and properties - a string, a number, a boolean or a list of
    /// strings - and refused where it is none: not JSON, no ctest test description, a major version this does not read,
    /// no tests at all, or a test whose name is no string.
    /// </summary>
    [Fact]
    public void ADescription_IsReadForEachTestsNameCommandAndProperties_AndRefusedWhereItIsNone()
    {
        var read = CtestEntries.Read(
            $$"""
            {
              "kind": "ctestInfo",
              "version": { "major": 1, "minor": 3 },
              "tests": [
                {
                  "name": "lex.CacheTest",
                  "command": ["{{Worker.Replace("\\", "\\\\")}}", "--order", "rand"],
                  "properties": [
                    { "name": "ENVIRONMENT", "value": ["LEX_CACHE_DIR=/tmp/cache"] },
                    { "name": "TIMEOUT", "value": 120 },
                    { "name": "RUN_SERIAL", "value": true },
                    { "name": "DISABLED", "value": false }
                  ]
                },
                { "name": "lex.Plain" }
              ]
            }
            """);

        Assert.Null(read.Why);

        var tests = read.Tests!;

        Assert.Equal(2, tests.Count);
        Assert.Equal("lex.CacheTest", tests[0].Name);
        Assert.Equal([Worker, "--order", "rand"], tests[0].Command);
        Assert.Equal(
            [
                ("ENVIRONMENT", "LEX_CACHE_DIR=/tmp/cache"),
                ("TIMEOUT", "120"),
                ("RUN_SERIAL", "true"),
                ("DISABLED", "false"),
            ],
            tests[0].Properties.Select(property => (property.Name, string.Join("|", property.Value.Shown))));
        Assert.Null(tests[1].Command);
        Assert.Empty(tests[1].Properties);

        foreach (var (json, why) in new (string Json, string Why)[]
        {
            ("{", "no JSON"),
            ("""{"kind": "buildInfo", "tests": []}""", "not ctestInfo"),
            ("""{"kind": "ctestInfo", "version": {"major": 2}, "tests": []}""", "another major version"),
            ("""{"kind": "ctestInfo"}""", "no tests"),
        })
        {
            var refused = CtestEntries.Read(json);

            Assert.Null(refused.Tests);
            Assert.Contains(why switch
            {
                "no JSON" => "it is no JSON",
                "not ctestInfo" => "its kind is not ctestInfo",
                "another major version" => "its version is 2, and this reads major version 1",
                _ => "it names no tests",
            }, refused.Why, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A binary one test starts is that test: the whole entry is its, its cases beside it, and nothing is said of the
    /// other tests of the description, which start another binary.
    /// </summary>
    [Fact]
    public void ABinaryRegisteredOnce_IsThatTest()
    {
        var tests = new[]
        {
            new CtestTest("lex.Other", [Path.Combine(Start, "other_tests")], []),
            new CtestTest("lex_tests", [Worker], []),
        };

        var match = CtestEntries.For(tests, Worker, ["Fixture.Charge"], Start, Names);

        Assert.Equal(CtestMatched.OneTest, match.Matched);
        Assert.Equal("one-test", match.Wire);
        Assert.Equal([("lex_tests", "Fixture.Charge", 0)], match.Tests.Select(test => (test.Test, string.Join(", ", test.Cases), test.NotApplied.Count)));
        Assert.Equal($"ctest starts {Worker} as one test, 'lex_tests', with nothing a sweep does not apply", match.Said);
    }

    /// <summary>
    /// A binary many tests start is matched by each red case: the test whose name is the case's or ends with it, or one
    /// of whose arguments is the case's name or ends with <c>=</c> and it. Each case matched to exactly one test, the
    /// cases are grouped by the test that runs them; no test at all starts the binary, and no way to tell one test,
    /// each said its own way.
    /// </summary>
    [Fact]
    public void ABinaryRegisteredAsManyTests_MatchesEachRedCaseByNameOrByArgument()
    {
        var tests = new[]
        {
            new CtestTest(
                "lex.CacheTest.ReadsWarm",
                [Worker, "--order", "rand"],
                [new CtestProperty("FIXTURES_REQUIRED", CtestValue.Of(["warm_cache"]))]),
            new CtestTest(
                "lex.floor",
                ["/usr/bin/python3", "/w/wrapper.py", Worker, "--gtest_filter=Fixture.Floor"],
                []),
            new CtestTest("Fixture.Floor", [Path.Combine(Start, "other_tests")], []),
            new CtestTest("lex.Other", [Path.Combine(Start, "other_tests")], []),
        };

        var byName = CtestEntries.For(tests, Worker, ["CacheTest.ReadsWarm"], Start, Names);

        Assert.Equal(CtestMatched.ByName, byName.Matched);
        Assert.Equal("by-name", byName.Wire);
        Assert.Equal(
            $"ctest registers 'CacheTest.ReadsWarm' as test 'lex.CacheTest.ReadsWarm', whose FIXTURES_REQUIRED (warm_cache) and arguments (--order, rand) a sweep does not apply",
            byName.Said);

        var byArgument = CtestEntries.For(tests, Worker, ["Fixture.Floor"], Start, Names);

        Assert.Equal(CtestMatched.ByName, byArgument.Matched);
        Assert.Equal(
            $"ctest registers 'Fixture.Floor' as test 'lex.floor', whose arguments (/usr/bin/python3, /w/wrapper.py, --gtest_filter=Fixture.Floor) a sweep does not apply",
            byArgument.Said);

        var grouped = CtestEntries.For(tests, Worker, ["CacheTest.ReadsWarm", "Fixture.Floor"], Start, Names);

        Assert.Equal(
            byName.Said + "; " + byArgument.Said,
            grouped.Said);
        Assert.Equal([("lex.CacheTest.ReadsWarm", "CacheTest.ReadsWarm"), ("lex.floor", "Fixture.Floor")], grouped.Tests.Select(test => (test.Test, string.Join(", ", test.Cases))));

        var none = CtestEntries.For([tests[2]], Worker, ["Fixture.Floor"], Start, Names);

        Assert.Equal(CtestMatched.None, none.Matched);
        Assert.Equal($"ctest, as this leg's tests start it, registers no test that starts {Worker}", none.Said);
    }

    /// <summary>
    /// A red case no one test matches - none, or several - is said uncertain: which of the binary's tests runs it could
    /// not be told by its name, and what of their entries a sweep does not apply is counted over them all.
    /// </summary>
    [Fact]
    public void ACaseNoOneTestMatches_IsSaidUncertain_WithWhatTheBinarysTestsDeclare()
    {
        var tests = new[]
        {
            new CtestTest(
                "lex.CacheTest",
                [Worker, "--order", "rand"],
                [new CtestProperty("FIXTURES_REQUIRED", CtestValue.Of(["warm_cache"]))]),
            new CtestTest(
                "lex.WarmCacheTest",
                [Worker],
                [new CtestProperty("FIXTURES_REQUIRED", CtestValue.Of(["warm_cache"]))]),
            new CtestTest("lex.CacheWarm", [Worker], [new CtestProperty("ENVIRONMENT", CtestValue.Of(["LEX_CACHE_DIR=/tmp/cache"]))]),
            new CtestTest("lex.CacheThird", [Worker], []),
        };

        var several = CtestEntries.For(tests, Worker, ["CacheTest"], Start, Names);

        Assert.Equal(CtestMatched.Uncertain, several.Matched);
        Assert.Equal("uncertain", several.Wire);
        Assert.Equal(
            $"which of the 4 tests ctest registers for {Worker} runs 'CacheTest' could not be told by its name; "
            + "among them 1 declare ENVIRONMENT and 2 FIXTURES_REQUIRED, neither of which a sweep applies",
            several.Said);

        var noCase = CtestEntries.For(tests, Worker, ["Fixture.Nowhere"], Start, Names);

        Assert.Equal(CtestMatched.Uncertain, noCase.Matched);
        Assert.StartsWith($"which of the 4 tests ctest registers for {Worker} runs 'Fixture.Nowhere' could not be told by its name", noCase.Said, StringComparison.Ordinal);
    }

    /// <summary>
    /// A test started through a launcher is still the binary's test: the words of the command before the program - the
    /// launcher and its own arguments - count as the arguments a sweep does not apply, beside those after the program.
    /// </summary>
    [Fact]
    public void ATestStartedThroughALauncher_IsStillThatBinarys()
    {
        var tests = new[] { new CtestTest("lex.Python", ["/usr/bin/python3", "/w/wrapper.py", Worker, "--verbose"], []) };

        var match = CtestEntries.For(tests, Worker, ["Fixture.Charge"], Start, Names);

        Assert.Equal(CtestMatched.OneTest, match.Matched);
        Assert.Equal(
            $"ctest starts {Worker} as one test, 'lex.Python', whose arguments (/usr/bin/python3, /w/wrapper.py, --verbose) a sweep does not apply",
            match.Said);
    }

    /// <summary>
    /// An environment is named by its variables, never their values: what a sweep does not apply of an ENVIRONMENT or an
    /// ENVIRONMENT_MODIFICATION is the names before the equals signs, which is all a line shows of them.
    /// </summary>
    [Fact]
    public void AnEnvironmentIsNamedByItsVariables_NeverTheirValues()
    {
        var test = new CtestTest(
            "lex.CacheTest",
            [Worker],
            [
                new CtestProperty("ENVIRONMENT", CtestValue.Of(["LEX_CACHE_DIR=/tmp/cache", "LEX_MODE=warm"])),
                new CtestProperty("ENVIRONMENT_MODIFICATION", CtestValue.Of(["PATH:path_listprepend=/opt"])),
            ]);

        var notApplied = CtestEntries.NotAppliedOf([test], test, Worker, Start, Names);

        Assert.Equal(
            [
                ("ENVIRONMENT", "LEX_CACHE_DIR|LEX_MODE"),
                ("ENVIRONMENT_MODIFICATION", "PATH"),
            ],
            notApplied.Select(left => (left.Name, string.Join("|", left.Value))));
    }

    /// <summary>
    /// A test's working directory counts only where it is another than the one the sweep starts the binary in, however
    /// each is spelled: the same directory, however written, applies nothing.
    /// </summary>
    [Fact]
    public void AWorkingDirectory_CountsOnlyWhereItIsAnotherThanTheSweeps()
    {
        var another = new CtestTest("lex.CacheTest", [Worker], [new CtestProperty("WORKING_DIRECTORY", CtestValue.Of(Path.Combine(Start, "tests")))]);
        var same = new CtestTest("lex.CacheTest", [Worker], [new CtestProperty("WORKING_DIRECTORY", CtestValue.Of(Start))]);

        var notApplied = CtestEntries.NotAppliedOf([another, same], another, Worker, Start, Names);

        Assert.Equal([("WORKING_DIRECTORY", Path.Combine(Start, "tests"))], notApplied.Select(left => (left.Name, string.Join("|", left.Value))));
        Assert.Empty(CtestEntries.NotAppliedOf([another, same], same, Worker, Start, Names));
    }

    /// <summary>
    /// A test's command and working directory are compared as whole paths, each side made whole however it is spelled:
    /// ctest's description writes forward slashes whatever the system, and the sweep's own strings carry the system's
    /// separator, so a binary whose command is spelled with one separator is still the binary the sweep starts.
    /// </summary>
    [Fact]
    public void ATestCommandSpeltWithEitherSeparator_IsStillThatBinarys()
    {
        var forward = Worker.Replace(Path.DirectorySeparatorChar, '/');
        var tests = new[] { new CtestTest("lex_tests", [forward], []) };

        var match = CtestEntries.For(tests, Worker, ["Fixture.Charge"], Start, Names);

        Assert.Equal(CtestMatched.OneTest, match.Matched);
        Assert.Equal($"ctest starts {Worker} as one test, 'lex_tests', with nothing a sweep does not apply", match.Said);

        var sameOtherSpelt = new CtestTest(
            "lex.Directory",
            [Worker],
            [new CtestProperty("WORKING_DIRECTORY", CtestValue.Of(Start.Replace(Path.DirectorySeparatorChar, '/')))]);

        Assert.Empty(CtestEntries.NotAppliedOf([sameOtherSpelt], sameOtherSpelt, Worker, Start, Names));
    }

    /// <summary>
    /// A fixture a test requires is named by its own name and by the tests that set it up: the FIXTURES_SETUP entries of
    /// the other tests of the description, so the line says who would have made what a sweep does not.
    /// </summary>
    [Fact]
    public void ARequiredFixture_IsNamedWithTheTestsThatSetUpIt()
    {
        var requires = new CtestTest("lex.CacheTest", [Worker], [new CtestProperty("FIXTURES_REQUIRED", CtestValue.Of(["warm_cache"]))]);
        var setUp = new CtestTest(
            "lex.cache_warm",
            [Path.Combine(Start, "cache_warm")],
            [new CtestProperty("FIXTURES_SETUP", CtestValue.Of(["warm_cache"]))]);

        var notApplied = CtestEntries.NotAppliedOf([requires, setUp], requires, Worker, Start, Names);

        Assert.Equal([("FIXTURES_REQUIRED", "warm_cache", "lex.cache_warm")], notApplied.Select(left => (left.Name, string.Join("|", left.Value), string.Join("|", left.SetUpBy ?? []))));

        var match = CtestEntries.For([requires, setUp], Worker, ["CacheTest"], Start, Names);

        Assert.Equal(
            $"ctest starts {Worker} as one test, 'lex.CacheTest', whose FIXTURES_REQUIRED (warm_cache, set up by test 'lex.cache_warm') a sweep does not apply",
            match.Said);
    }

    /// <summary>
    /// Everything of a test's entry a sweep does not apply is named, in one closed list - the seventeen names spelled here
    /// apart, so a name dropped or mistyped in the code's own list changes this verdict - a test declaring every property
    /// of the list has each named back, an environment by its variables and never its values, the command's arguments
    /// last.
    /// </summary>
    [Fact]
    public void EverythingOfATestsEntryASweepDoesNotApply_IsNamed_InOneClosedList()
    {
        string[] closed =
        [
            "ENVIRONMENT",
            "ENVIRONMENT_MODIFICATION",
            "WORKING_DIRECTORY",
            "FIXTURES_REQUIRED",
            "FIXTURES_SETUP",
            "FIXTURES_CLEANUP",
            "DEPENDS",
            "REQUIRED_FILES",
            "RESOURCE_LOCK",
            "RUN_SERIAL",
            "TIMEOUT",
            "WILL_FAIL",
            "PASS_REGULAR_EXPRESSION",
            "FAIL_REGULAR_EXPRESSION",
            "SKIP_REGULAR_EXPRESSION",
            "SKIP_RETURN_CODE",
            "DISABLED",
        ];

        Assert.Equal(closed, CtestEntries.NotAppliedNames);

        var test = new CtestTest(
            "lex.Everything",
            [Worker, "--verbose"],
            [.. closed.Select(name => new CtestProperty(name, name switch
            {
                "ENVIRONMENT" => CtestValue.Of(["LEX_CACHE_DIR=/tmp/cache"]),
                "ENVIRONMENT_MODIFICATION" => CtestValue.Of(["LEX_MODE:reset=1"]),
                "WORKING_DIRECTORY" => CtestValue.Of(Path.Combine(Start, "tests")),
                "FIXTURES_REQUIRED" or "FIXTURES_SETUP" or "FIXTURES_CLEANUP" => CtestValue.Of(["warm_cache"]),
                "TIMEOUT" => CtestValue.Of(120),
                "SKIP_RETURN_CODE" => CtestValue.Of(2),
                "RUN_SERIAL" or "WILL_FAIL" or "DISABLED" => CtestValue.Of(true),
                _ => CtestValue.Of("anything"),
            }))]);

        var notApplied = CtestEntries.NotAppliedOf([test], test, Worker, Start, Names);

        Assert.Equal([.. closed, "arguments"], notApplied.Select(left => left.Name));
        Assert.Equal(["LEX_CACHE_DIR"], notApplied[0].Value);
        Assert.Equal(["LEX_MODE"], notApplied[1].Value);
    }
}

/// <summary>
/// The reader that asks ctest what it registers for a red control's cases: it asks only where the leg's tests run
/// through ctest, with the command the test command of the same settings makes and the description option appended,
/// started where that command starts and bounded by two minutes - and it owns no rule of the match, which
/// <see cref="CtestEntries"/> reads from the answer.
/// </summary>
public sealed class CtestDescriptionTests
{
    private static readonly string Worker = OperatingSystem.IsWindows() ? @"C:\w" : "/w";

    private static readonly string Program = Path.Combine(Worker, "fixture_tests");

    /// <summary>
    /// Nothing is asked - and nothing started - where the subject has no test settings, or their runner is no ctest; a
    /// leg whose tests run through ctest is asked with the command the test command makes, no filter and no label, the
    /// description option appended, started where that command starts, bounded by two minutes.
    /// </summary>
    [Fact]
    public async Task CtestIsAsked_AsTheLegsTestsStartIt_OnlyWhereTheyRunThroughIt()
    {
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new ProcessResult(
                0,
                $$"""
                {"kind": "ctestInfo", "version": {"major": 1, "minor": 0}, "tests": [
                  {"name": "lex_tests", "command": ["{{Program.Replace("\\", "\\\\")}}"], "properties": []}
                ]}
                """,
                string.Empty,
                TimeSpan.Zero,
                TimedOut: false)));
        var answer = await Description(processes).AskAsync(Question(new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", Args = ["--test-dir", "{buildDir}"], WorkingDirectory = "{buildDir}" },
        }), TestContext.Current.CancellationToken);

        Assert.NotNull(answer!.Match);
        Assert.Equal(CtestMatched.OneTest, answer.Match.Matched);

        var request = (ProcessRequest)processes.ReceivedCalls().Single().GetArguments()[0]!;

        Assert.Equal(Path.Combine(Worker, "build", "x64-debug"), request.WorkingDirectory);
        Assert.Equal(["--test-dir", Path.Combine(Worker, "build", "x64-debug"), "--show-only=json-v1"], request.Arguments);
        Assert.Equal(CtestDescription.Bound, request.Timeout);

        foreach (var settings in new TestConfig?[]
        {
            null,
            new TestConfig { All = new TestInvocation { Runner = "fixture_tests" } },
        })
        {
            var silent = Substitute.For<IProcessRunner>();

            Assert.Null(await Description(silent).AskAsync(Question(settings), TestContext.Current.CancellationToken));
            Assert.Empty(silent.ReceivedCalls());
        }
    }

    /// <summary>
    /// What ctest could not say is said as that: ctest that would not start or failed, that ran past the bound, or whose
    /// answer was no description - never a match, and never a rule of the reader's own.
    /// </summary>
    [Theory]
    [InlineData(1, null, false, "ctest exited 1")]
    [InlineData(0, "not json", false, "it is no JSON")]
    [InlineData(0, null, true, "did not answer within 120s")]
    public async Task WhatCtestCouldNotSay_IsSaidAsThat(int exit, string? output, bool timedOut, string why)
    {
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProcessResult(exit, output ?? """{"kind": "ctestInfo", "version": {"major": 1}, "tests": []}""", string.Empty, TimeSpan.Zero, timedOut)));

        var answer = await Description(processes).AskAsync(Question(new TestConfig { All = new TestInvocation { Runner = "ctest" } }), TestContext.Current.CancellationToken);

        Assert.Null(answer!.Match);
        Assert.Contains(why, answer.Unread, StringComparison.Ordinal);
    }

    /// <summary>The asking over the given pieces, as the sweep builds it.</summary>
    private static CtestDescription Description(IProcessRunner processes)
        => new(processes, new HarnessFactory().FileSystem, new HostPlatform());

    private static CtestQuestion Question(TestConfig? settings)
        => new(
            Program,
            ["Fixture.Depth"],
            settings,
            "linux",
            new LegPaths(Worker, Path.Combine(Worker, "build", "x64-debug")),
            [],
            Worker,
            new Dictionary<string, string>(StringComparer.Ordinal),
            [],
            DefaultCores: null);
}
