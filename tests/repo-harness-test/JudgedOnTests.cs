using System.Globalization;
using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// What an arm is judged on, and its key: every part of a row the sweep and the judge read moves it, and nothing else of
/// the registry does; a text or a site that moved moves it and is named; and the judge's revision is held to its rules.
/// </summary>
public sealed class JudgedOnTests
{
    /// <summary>
    /// The registry every case starts from: a TEST-RED arm with two C rows, a G row and an M row in another file and one in
    /// its own, and a BUILD-RED arm with its paired control.
    /// </summary>
    private const string Registry = """
        # the arms
        A | one | src/a.cpp | texts/a.before | texts/a.after | TEST-RED | lib | lib_tests | 3 | texts/a.diag | why one
        C | one | Suite.A | red
        C | one | Suite.B | red
        G | one | Suite.G | green
        M | one | src/b.cpp | texts/b.before | texts/b.after | coupled
        M | one | src/a.cpp | texts/a2.before | texts/a2.after | further

        A | two | src/d.hpp | texts/d.before | texts/d.after | BUILD-RED | lib | - | 0 | PAIRED-CONTROL | why two
        B | two | texts/d.cb | texts/d.ca | another builds
        """;

    /// <summary>
    /// The key moves with every field of every row the sweep and the judge read - one case to each - and with none of
    /// what they do not: a why, a comment, a line's number, the letter case of the arm's id, and an S row.
    /// </summary>
    [Theory]
    [InlineData("A | one | src/a.cpp |", "A | one | src/a9.cpp |", true)]
    [InlineData("| texts/a.before | texts/a.after | TEST-RED", "| texts/a9.before | texts/a.after | TEST-RED", true)]
    [InlineData("| texts/a.before | texts/a.after | TEST-RED", "| texts/a.before | texts/a9.after | TEST-RED", true)]
    [InlineData("| TEST-RED | lib | lib_tests | 3 |", "| TEST-RED | lib9 | lib_tests | 3 |", true)]
    [InlineData("| TEST-RED | lib | lib_tests | 3 |", "| TEST-RED | lib | lib9_tests | 3 |", true)]
    [InlineData("| lib_tests | 3 | texts/a.diag |", "| lib_tests | 4 | texts/a.diag |", true)]
    [InlineData("| 3 | texts/a.diag |", "| 3 | texts/a9.diag |", true)]
    [InlineData("C | one | Suite.B | red", "C | one | Suite.Z | red", true)]
    [InlineData("G | one | Suite.G | green", "G | one | Suite.Z | green", true)]
    [InlineData("M | one | src/b.cpp |", "M | one | src/c.cpp |", true)]
    [InlineData("| texts/b.before | texts/b.after |", "| texts/b9.before | texts/b.after |", true)]
    [InlineData("| texts/b.before | texts/b.after |", "| texts/b.before | texts/b9.after |", true)]
    [InlineData("| texts/a2.before | texts/a2.after |", "| texts/a2.before | texts/a9.after |", true)]
    [InlineData("B | two | texts/d.cb | texts/d.ca |", "B | two | texts/d.c9 | texts/d.ca |", true)]
    [InlineData("B | two | texts/d.cb | texts/d.ca |", "B | two | texts/d.cb | texts/d.c9 |", true)]
    [InlineData("| why one", "| why else", false)]
    [InlineData("C | one | Suite.A | red", "C | one | Suite.A | red, for another reason", false)]
    [InlineData("| coupled", "| coupled, and said so", false)]
    [InlineData("B | two | texts/d.cb | texts/d.ca | another builds", "B | two | texts/d.cb | texts/d.ca | another reason", false)]
    [InlineData("# the arms", "# the arms, commented otherwise\n# and at length\n\n", false)]
    [InlineData("A | one |", "A | ONE |", false)]
    [InlineData("G | one | Suite.G | green", "G | one | Suite.G | green\nS | one | native | where it runs", false)]
    public void TheKey_ChangesWithEachRowTheJudgeReads_AndWithNoWhyCommentLineOrSRow(string row, string edited, bool moves)
    {
        Assert.Contains(row, Registry, StringComparison.Ordinal);

        var before = Arms(Registry);
        var after = Arms(Registry.Replace(row, edited, StringComparison.Ordinal));

        foreach (var id in new[] { "one", "two" })
        {
            var then = Key(before.Find(id)!);
            var now = Key(after.Find(id)!);

            if (moves && Changed(row, id))
            {
                Assert.NotEqual(then, now);
            }
            else
            {
                Assert.Equal(then, now);
            }
        }

        static bool Changed(string row, string id) => row.Contains($"| {id} |", StringComparison.Ordinal)
            || (id == "one" && !row.Contains("| two |", StringComparison.Ordinal) && !row.StartsWith("B |", StringComparison.Ordinal));
    }

    /// <summary>
    /// C and G rows are read as sets, as the judge compares them, so reordering them moves no key; M rows are read in the
    /// order declared, as their texts are found in their sites in that order, so reordering them does.
    /// </summary>
    [Fact]
    public void ReorderingCOrGRows_ChangesNoKey_AndReorderingMRowsDoes()
    {
        const string Rows = """
            A | one | src/a.cpp | texts/a.before | texts/a.after | TEST-RED | lib | lib_tests | 3 | texts/a.diag | why
            C | one | Suite.A | red
            C | one | Suite.B | red
            G | one | Suite.G | green
            G | one | Suite.H | green
            M | one | src/b.cpp | texts/b.before | texts/b.after | coupled
            M | one | src/c.cpp | texts/c.before | texts/c.after | coupled
            """;

        string Swapped(string first, string second)
            => Rows.Replace(first, "\u0001", StringComparison.Ordinal).Replace(second, first, StringComparison.Ordinal).Replace("\u0001", second, StringComparison.Ordinal);

        var key = Key(Arms(Rows).Find("one")!);

        Assert.Equal(key, Key(Arms(Swapped("C | one | Suite.A | red", "C | one | Suite.B | red")).Find("one")!));
        Assert.Equal(key, Key(Arms(Swapped("G | one | Suite.G | green", "G | one | Suite.H | green")).Find("one")!));
        Assert.NotEqual(
            key,
            Key(Arms(Swapped("M | one | src/b.cpp | texts/b.before | texts/b.after | coupled", "M | one | src/c.cpp | texts/c.before | texts/c.after | coupled")).Find("one")!));
    }

    /// <summary>
    /// A text the arm cites, or a site it mutates, that holds something else - or that the reading no longer holds - moves
    /// the key, and the difference names it, in the words a line says it in; every text the arm cites is one, each once,
    /// and its sites are its own and its M rows' files.
    /// </summary>
    [Fact]
    public void ATextOrASiteThatMoved_ChangesTheKey_AndIsNamed()
    {
        var arm = Arms(Registry).Find("one")!;
        var tree = JudgedOn.TextsOf(arm).Concat(arm.Sites.Select(site => site.Site)).ToDictionary(path => path, path => (string?)FileContentHash.Of(Encoding.UTF8.GetBytes(path)), StringComparer.Ordinal);

        Assert.Equal(["texts/a.before", "texts/a.after", "texts/a2.before", "texts/a2.after", "texts/b.before", "texts/b.after", "texts/a.diag"], JudgedOn.TextsOf(arm));
        Assert.Equal(["src/a.cpp", "src/b.cpp"], arm.Sites.Select(site => site.Site));
        Assert.Equal(
            ["texts/d.before", "texts/d.after", "texts/d.cb", "texts/d.ca"],
            JudgedOn.TextsOf(Arms(Registry).Find("two")!));

        var then = KeyOf(arm, tree);
        var text = KeyOf(arm, new Dictionary<string, string?>(tree) { ["texts/a2.after"] = "moved" });
        var gone = KeyOf(arm, new Dictionary<string, string?>(tree) { ["texts/a.diag"] = null });
        var site = KeyOf(arm, new Dictionary<string, string?>(tree) { ["src/b.cpp"] = "moved" });

        Assert.Equal(3, new[] { text.Key, gone.Key, site.Key }.Distinct().Count());
        Assert.DoesNotContain(then.Key, new[] { text.Key, gone.Key, site.Key });
        Assert.Equal([("text-moved", "a text it cites moved: texts/a2.after")], Said(KeyDifference.Between(then, text)));
        Assert.Equal([("text-moved", "a text it cites moved: texts/a.diag")], Said(KeyDifference.Between(then, gone)));
        Assert.Equal([("site-moved", "its site moved: src/b.cpp")], Said(KeyDifference.Between(then, site)));
        Assert.Empty(KeyDifference.Between(then, KeyOf(arm, tree)));
    }

    /// <summary>
    /// Every part beyond the rows, the texts and the sites moves the key where it holds something else - the judge's
    /// revision, each compiler's id, version, program and words, the developer environment, each of the three settings,
    /// the host, and each part of a table - and the difference is named in the words a line says it in, the compiler by
    /// the least that tells the two apart. A part that could not be read leaves no key, and is named as that alone.
    /// </summary>
    [Theory]
    [InlineData("judge", "judge-revised", "the judge's rules changed since (revision {revision}, now {next})")]
    [InlineData("compiler version", "compiler-changed", "its compiler changed: CXX GNU 13.3.0, now GNU 14.1.0")]
    [InlineData("compiler program", "compiler-changed", "its compiler changed: CXX GNU 13.3.0 'C:/mingw/bin/g++.exe', now GNU 13.3.0 'C:/other/bin/g++.exe'")]
    [InlineData("compiler words", "compiler-changed", "its compiler changed: CXX GNU 13.3.0 'C:/mingw/bin/g++.exe', now GNU 13.3.0 'C:/mingw/bin/g++.exe' -m32")]
    [InlineData("compiler added", "compiler-changed", "its compiler changed: C none, now GNU 13.3.0")]
    [InlineData("developer environment", "environment-changed", "its developer environment changed")]
    [InlineData("build settings", "build-environment-changed", "the environment its build is given changed")]
    [InlineData("run settings", "run-settings-changed", "how its run starts changed")]
    [InlineData("judge settings", "judge-settings-changed", "what its run is judged by changed")]
    [InlineData("host", "host-changed", "it was judged on local, and this leg runs on ssh:box")]
    [InlineData("lines", "lines-changed", "how 'lib' is built changed")]
    [InlineData("file moved", "files-moved", "what 'lib' is built from moved: src/a.cpp")]
    [InlineData("run reads", "run-reads-moved", "what its runner reads at run time moved: data/golden.txt")]
    [InlineData("run read joined", "run-reads-moved", "what its runner reads at run time moved: data/silver.txt")]
    [InlineData("file joined", "set-changed", "a file joined or left what 'lib' is built from")]
    [InlineData("made", "made-moved", "a file its build makes moved, in {build}")]
    [InlineData("fetched", "fetched-moved", "the sources of 'plumb' its build fetched moved")]
    [InlineData("outside", "outside-moved", "a file outside the tree its build reads moved, in C:/sys/include")]
    [InlineData("unread", "key-unreadable", "what it would be judged on cannot be read now: 'C:/sys/include/a.h', which the build of 'lib' reads, is gone")]
    public void EveryOtherPart_MovesTheKey_AndIsNamedInTheWordsALineSaysItIn(string part, string code, string said)
    {
        said = said
            .Replace("{revision}", ArmJudge.Revision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{next}", (ArmJudge.Revision + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var then = KeyOf(Arms(Registry).Find("one")!, new Dictionary<string, string?>(StringComparer.Ordinal));
        var compiler = then.JudgedOn.Compilers[0];
        var now = part switch
        {
            "judge" => Again(then, on => on with { Judge = on.Judge + 1 }),
            "compiler version" => Again(then, on => on with { Compilers = [compiler with { Version = "14.1.0" }] }),
            "compiler program" => Again(then, on => on with { Compilers = [compiler with { Program = "C:/other/bin/g++.exe" }] }),
            "compiler words" => Again(then, on => on with { Compilers = [compiler with { Arguments = ["-m32"] }] }),
            "compiler added" => Again(then, on => on with { Compilers = [compiler with { Language = "C" }, compiler] }),
            "developer environment" => Again(then, on => on with { DeveloperEnvironment = new DeveloperEnvironmentFact("vs", "C:/vs", "17.14", "14.44", "amd64") }),
            "build settings" => Again(then, on => on with { Settings = on.Settings with { Build = "other" } }),
            "run settings" => Again(then, on => on with { Settings = on.Settings with { Run = "other" } }),
            "judge settings" => Again(then, on => on with { Settings = on.Settings with { Judge = "other" } }),
            "host" => Again(then, on => on with { Host = "ssh:box" }),
            "lines" => Again(then, table: table => table with { Lines = "other" }),
            "file moved" => Again(then, table: table => table with { Files = new Dictionary<string, string>(table.Files) { ["src/a.cpp"] = "moved" } }),
            "run reads" => Again(then, on => on with { RunReads = new Dictionary<string, string>(on.RunReads) { ["data/golden.txt"] = "moved" } }),
            "run read joined" => Again(then, on => on with { RunReads = new Dictionary<string, string>(on.RunReads) { ["data/silver.txt"] = "silver" } }),
            "file joined" => Again(then, table: table => table with { Files = new Dictionary<string, string>(table.Files) { ["src/b.cpp"] = "b" } }),
            "made" => Again(then, table: table => table with { Made = new Dictionary<string, FilesIn>(table.Made) { [BuiltFromReader.BuildDirectory] = new(1, "moved") } }),
            "fetched" => Again(then, table: table => table with { Fetched = new Dictionary<string, string>(table.Fetched) { ["plumb"] = "moved" } }),
            "outside" => Again(then, table: table => table with { Outside = new Dictionary<string, FilesIn>(table.Outside) { ["C:/sys/include"] = new(2, "moved") } }),
            _ => Again(then, unread: "'C:/sys/include/a.h', which the build of 'lib' reads, is gone"),
        };

        Assert.NotNull(then.Key);
        Assert.NotEqual(then.Key, now.Key);
        Assert.Equal(part == "unread", now.Key is null);
        Assert.Equal([(code, said)], Said(KeyDifference.Between(then, now)));
        Assert.Empty(KeyDifference.Between(then, Again(then)));
    }

    /// <summary>
    /// The judge's revision is held to its rules: a fixed table of observations is judged by the arm's judge and the
    /// unmutated run's, every verdict and detail digested, and the digest is pinned beside the revision - so a rule that
    /// changes what either judge reaches fails here until the revision is raised and the new digest pinned with it.
    /// </summary>
    [Fact]
    public void TheJudgesRevision_IsRaisedWithItsRules()
    {
        const int PinnedRevision = 2;
        const string PinnedDigest = "e4f7762baa5ce64023d246a43751248db964d99508ba8daf6ef737b93c467f5b";

        var digest = FileContentHash.Of(Encoding.UTF8.GetBytes(string.Join("\n", Verdicts())));

        Assert.True(
            (ArmJudge.Revision, digest) == (PinnedRevision, PinnedDigest),
            $"The judges reach {digest} over the fixed table at revision {ArmJudge.Revision}, and {PinnedDigest} is pinned for revision "
            + $"{PinnedRevision}: a rule of theirs changed, so raise ArmJudge.Revision and pin the new digest beside it.");
    }

    /// <summary>
    /// What both judges reach over a fixed table: each refusal of the pre-flight, each way a build ends, each way a run
    /// ends, a BUILD-RED arm's mutation and paired control, and each way an unmutated control ends.
    /// </summary>
    private static IEnumerable<string> Verdicts()
    {
        var registry = Arms(Registry);
        var testRed = registry.Find("one")!;
        var buildRed = registry.Find("two")!;
        const string Object = "CMakeFiles/lib.dir/src/a.cpp.o";
        var ready = new ArmPreflight { Counts = [new TextCount("texts/a.before", "src/a.cpp", 1)], Dependents = [Object] };
        var built = new ArmBuild(ReachedVerdict.Of(LegVerdict.Passed, "built"), [], []);
        var failedAtSite = new ArmBuild(ReachedVerdict.Of(LegVerdict.Failed, "exited 1"), [Object], []);
        var failedElsewhere = new ArmBuild(ReachedVerdict.Of(LegVerdict.Failed, "exited 1"), ["bin/lib_tests"], []);

        ArmPreflight[] preflights =
        [
            ready,
            ready with { MisspeltSites = [new SiteSpelling("src/A.cpp", "src/a.cpp")] },
            ready with { MissingSites = ["src/a.cpp"] },
            ready with { UnreadSites = ["src/a.cpp"] },
            ready with { TextProblems = ["text 'texts/a.after' is not a file in the worker's copy of the tree"] },
            ready with { Counts = [new TextCount("texts/a.before", "src/a.cpp", 2)] },
            ready with { Unchanged = [new UnchangedSite("texts/a.before", "texts/a.after", "src/a.cpp")] },
            ready with { TargetBuilt = false },
            ready with { RunnerProblem = "runner 'lib_tests' builds no program" },
            ready with { Dependents = [] },
        ];

        ArmBuild[] builds =
        [
            built,
            failedAtSite,
            failedElsewhere,
            new(ReachedVerdict.Of(LegVerdict.Stopped, "stopped"), [], []),
            new(ReachedVerdict.Of(LegVerdict.InputsMoved, "an input moved"), [], []),
            new(ReachedVerdict.Of(LegVerdict.Passed, "built"), [], [Object]),
        ];

        ArmRun[] runs =
        [
            ArmRun.StoppedAt(new RunBound(TimeSpan.FromSeconds(100), "10x the unmutated run")),
            ArmRun.Hung(45),
            ArmRun.Exited(3, RunReport.None),
            ArmRun.Exited(1, RunReport.NotRead("held")),
            ArmRun.Exited(1, RunReport.NoReport("no xml")),
            Ran(1, [], ["Suite.A", "Suite.B", "Suite.G"]),
            Ran(1, ["Suite.A"], ["Suite.G"], ["Suite.B"]),
            Ran(0, [], ["Suite.A", "Suite.B", "Suite.G"]),
            Ran(1, ["Suite.A", "Suite.G"], ["Suite.B"]),
            Ran(1, ["Suite.A", "Suite.B"], ["Suite.X"], ["Suite.G"]),
            Ran(1, ["Suite.A", "Suite.B"], ["Suite.G"], said: false),
            Ran(1, ["Suite.A", "Suite.B"], ["Suite.G"]),
        ];

        foreach (var preflight in preflights)
        {
            yield return Said(ArmJudge.Judge(testRed, new ArmObservation(preflight)));
        }

        foreach (var build in builds)
        {
            yield return Said(ArmJudge.Judge(testRed, new ArmObservation(ready) { Build = build }));
            yield return Said(ArmJudge.Judge(buildRed, new ArmObservation(ready) { Build = build }));
            yield return Said(ArmJudge.Judge(buildRed, new ArmObservation(ready) { Build = failedAtSite, Control = build }));
            yield return ArmJudge.Next(testRed, new ArmObservation(ready) { Build = build }).ToString();
        }

        foreach (var run in runs)
        {
            yield return Said(ArmJudge.Judge(testRed, new ArmObservation(ready) { Build = built, Run = run }));

            // An unmutated run has no bound to run past.
            if (run.PastBound is null)
            {
                yield return Outcome(PristineJudge.Judge("lib_tests", built, run, TimeSpan.FromSeconds(4), 10));
            }
        }

        yield return Outcome(PristineJudge.Judge("lib_tests", failedElsewhere, null, TimeSpan.Zero, 10));
        yield return Outcome(PristineJudge.Judge("lib_tests", built, Ran(0, [], ["Suite.A"]), TimeSpan.FromHours(2), 10));
        yield return string.Join(",", ArmJudge.Builds(testRed).Concat(ArmJudge.Builds(buildRed)));

        static string Said(ReachedVerdict? reached) => reached is null ? "none" : $"{reached.Verdict}: {reached.Detail}";

        static string Outcome(PristineOutcome outcome) => $"{Said(outcome.Leg)} / {outcome.Stops} / {outcome.Bound}";
    }

    /// <summary>A run that exited <paramref name="exitCode"/>, its report naming the red, green and unrun cases.</summary>
    private static ArmRun Ran(int exitCode, string[] reds, string[] greens, string[]? notRun = null, bool said = true)
    {
        static string Case(string id, string status, string body)
            => $"<testcase classname=\"{id[..id.IndexOf('.', StringComparison.Ordinal)]}\" name=\"{id[(id.IndexOf('.', StringComparison.Ordinal) + 1)..]}\" status=\"{status}\">{body}</testcase>";

        var xml = "<testsuites><testsuite name=\"Suite\">"
            + string.Concat(reds.Select(id => Case(id, "run", "<failure message=\"red\"/>")))
            + string.Concat(greens.Select(id => Case(id, "run", string.Empty)))
            + string.Concat((notRun ?? []).Select(id => Case(id, "notrun", string.Empty)))
            + "</testsuite></testsuites>";

        return ArmRun.Exited(exitCode, RunReport.Of(JUnitReport.Read(xml)!), said);
    }

    /// <summary>The arms <paramref name="text"/> declares, as a sweep reads them.</summary>
    private static MutationRegistry Arms(string text)
    {
        var reading = MutationRegistryParser.Parse(MutationRegistryParser.Lines(text), texts: null);

        Assert.True(reading.Valid, string.Join("; ", reading.Problems));

        return reading.Registry;
    }

    /// <summary>
    /// A key is made one way: what each target is built from is what its readings say, whatever the parts given hold, so
    /// the key and its tables cannot disagree; the first reading that made no table leaves no key, saying why; and nothing
    /// copies a key with another part under it. A reading, a key and a reason are each made only by their own factories.
    /// </summary>
    [Fact]
    public void AKey_IsMadeOneWay_ItsBuiltFromItsReadings_AndNoneIsCopiedWithAnotherPart()
    {
        var then = KeyOf(Arms(Registry).Find("one")!, new Dictionary<string, string?>(StringComparer.Ordinal));
        var table = then.Tables.Values.Single();
        var claimed = ArmKey.Of(then.JudgedOn with { Built = new Dictionary<string, string>(StringComparer.Ordinal) { ["lib"] = "another" } }, [BuiltFromReading.Read(table)]);

        Assert.Equal(then.Key, claimed.Key);
        Assert.Equal(table.Digest(), claimed.JudgedOn.Built[table.Target]);

        var unread = ArmKey.Of(then.JudgedOn, [BuiltFromReading.Read(table), BuiltFromReading.NotRead("tests", "'a.h' is gone")], "its compilers");

        Assert.Null(unread.Key);
        Assert.Equal("'a.h' is gone", unread.Unread);
        Assert.Equal([table.Target], unread.JudgedOn.Built.Keys);
        Assert.Equal(table.Digest(), BuiltFromReading.Read(table).Digest);

        foreach (var type in new[] { typeof(ArmKey), typeof(BuiltFromReading), typeof(PendingReason) })
        {
            Assert.Null(type.GetMethod("<Clone>$"));
            Assert.All(
                type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                constructor => Assert.True(constructor.IsPrivate, $"{type.Name}: {constructor} is not private"));
        }
    }

    /// <summary>
    /// Under --pending an arm is held by one rule: the key of what it is judged on now is a recorded passed's - whichever
    /// of its entries that is. Every other arm is pending, and says why as a listing would: its record unread or newer,
    /// nothing recorded, the verdict on the key read now no passed, the entry shown swept with no key, a table of it the
    /// record could not give, or what differs between the newest entry and now - what only a sweep reads among it - or
    /// what it is judged on now could not be read.
    /// </summary>
    [Fact]
    public void UnderPending_AnArmIsHeldOnlyByARecordedPassedOfItsKey_AndOtherwiseSaysWhyAsAListingWould()
    {
        var arm = Arms(Registry).Find("one")!;
        var now = KeyOf(arm, new Dictionary<string, string?>(StringComparer.Ordinal));
        var compiled = Again(now, judged => judged with { Compilers = [new JudgedCompiler("CXX", "GNU", "14.1.0", "C:/mingw/bin/g++.exe", [])] });
        var digest = now.JudgedOn.Built.Values.Single();
        var tables = new Dictionary<string, BuiltFromTable>(StringComparer.Ordinal) { [digest] = now.Tables.Values.Single() };

        RecordedVerdict Entry(ArmKey key, string verdict = "passed", string run = "r1")
            => new() { Verdict = verdict, Run = run, At = DateTimeOffset.UnixEpoch, SweptIn = "the main checkout", Key = key.Key, JudgedOn = key.JudgedOn };

        RecordedArms Holding(params RecordedVerdict[] entries) => new()
        {
            Arms = new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase) { ["ONE"] = entries },
            Tables = tables,
        };

        List<(string Code, string Said)> Why(RecordedArms recorded, ArmKey key)
        {
            var standing = ArmHolding.Of(recorded, "one", key);

            Assert.Null(standing.Held);

            return Said(standing.Why);
        }

        Assert.Equal("r2", ArmHolding.Of(Holding(Entry(compiled, run: "r1"), Entry(now, run: "r2"), Entry(compiled, "violated", "r3")), "one", now).Held?.Run);
        Assert.Equal(
            [("record-unreadable", "the record of this leg cannot be read: it holds null")],
            Why(RecordedArms.Unreadable(RecordUnread.Because("it holds null")), now));
        Assert.Equal([("record-newer", $"the record of this leg was written by a newer {ToolPackage.Id}")], Why(RecordedArms.Unreadable(RecordUnread.Newer(2)), now));
        Assert.Equal([("never-swept", "never swept on this leg")], Why(new RecordedArms(), now));
        Assert.Equal([("last-not-passed", "its last verdict is violated, not passed")], Why(Holding(Entry(now, "violated"), Entry(compiled)), now));
        Assert.Equal(
            [("compiler-changed", "its compiler changed: CXX GNU 14.1.0, now GNU 13.3.0")],
            Why(Holding(Entry(compiled)), now));
        Assert.Equal(
            [("not-keyed", "what it was judged on could not be read when it was swept: its compilers"), ("compiler-changed", "its compiler changed: CXX GNU 14.1.0, now GNU 13.3.0")],
            Why(Holding(Entry(compiled) with { Key = null, KeyUnread = "its compilers" }), now));
        Assert.Equal(
            [("record-unreadable", $"the record of this leg cannot be read: '{digest}.json' is gone"), ("compiler-changed", "its compiler changed: CXX GNU 14.1.0, now GNU 13.3.0")],
            Why(Holding(Entry(compiled)) with { Tables = new Dictionary<string, BuiltFromTable>(), TablesUnread = new Dictionary<string, string> { [digest] = $"'{digest}.json' is gone" } }, now));
        Assert.Equal(
            [("key-unreadable", "what it would be judged on cannot be read now: 'a.h' is gone")],
            Why(Holding(Entry(compiled)), Again(now, unread: "'a.h' is gone")));
    }

    /// <summary>
    /// The paths of what a runner reads at run time are compared as the key spells them - as the tree's reading spells
    /// each, whatever the tree's file system folds - as a file of a target's table is: a run-time read the reading spells
    /// otherwise, renamed by case alone, moves the key, and is a move of what the runner reads, naming both spellings.
    /// </summary>
    [Fact]
    public void RunReadPaths_AreComparedAsTheKeySpellsThem()
    {
        var arm = Arms(Registry).Find("one")!;
        var now = KeyOf(arm, new Dictionary<string, string?>(StringComparer.Ordinal));
        var recorded = Again(now, on => on with { RunReads = new Dictionary<string, string>(StringComparer.Ordinal) { ["Data/Golden.txt"] = "golden" } });
        var tables = now.Tables.ToDictionary(table => table.Key, table => table.Value, StringComparer.Ordinal);

        Assert.NotEqual(recorded.Key, now.Key);
        Assert.Equal(
            [("run-reads-moved", "what its runner reads at run time moved: Data/Golden.txt, data/golden.txt")],
            Said(KeyDifference.Between(recorded.JudgedOn, tables, now)));
    }

    /// <summary>
    /// Two keys that differ are never said to be one, nor is an arm pending for nothing: where no part compared names how
    /// they differ, the list closes with the key itself moving - an arm under --pending is pending for that, never ending
    /// its leg, and two workers of a self-test that read an arm otherwise differ for it.
    /// </summary>
    [Fact]
    public void TwoKeysThatDiffer_WithNoPartNamingHow_AreSaidToHaveMoved()
    {
        var arm = Arms(Registry).Find("one")!;
        var now = KeyOf(arm, new Dictionary<string, string?>(StringComparer.Ordinal));
        var table = now.Tables.Values.Single();

        // An entry recorded under a key that is not of what it says it was judged on: nothing it was judged on moved.
        var recorded = new RecordedArms
        {
            Arms = new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase)
            {
                ["one"] = [new RecordedVerdict { Verdict = "passed", Run = "r1", At = DateTimeOffset.UnixEpoch, SweptIn = "the main checkout", Key = "another", JudgedOn = now.JudgedOn }],
            },
            Tables = new Dictionary<string, BuiltFromTable>(StringComparer.Ordinal) { [table.Digest()] = table },
        };

        var standing = ArmHolding.Of(recorded, "one", now);

        Assert.Null(standing.Held);
        Assert.Equal([("key-moved", "its key moved, in no part another reason names")], Said(standing.Why));
        Assert.Equal("whose key moved", standing.Why[0].Counted);

        // A worker that read a target the other did not: no target both read differs, and no other part does.
        var wider = ArmKey.Of(now.JudgedOn, [BuiltFromReading.Read(table), BuiltFromReading.Read(table with { Target = "lib_extra" })]);

        Assert.NotEqual(now.Key, wider.Key);
        Assert.Equal([("key-moved", "its key moved, in no part another reason names")], Said(KeyDifference.Between(now, wider)));
        Assert.Empty(KeyDifference.Between(now, Again(now)));
    }

    /// <summary>
    /// A runReads entry is found however its runner is spelt, as configuration compares names - one rule with the guard
    /// that refuses a runner no A row names: an entry spelled in another case than the row covers the files it names, and
    /// no silence about what a runner reads survives through the case of its name.
    /// </summary>
    [Fact]
    public void ARunReadsEntry_IsFoundHoweverItsRunnerIsSpelt_AsConfigurationComparesNames()
    {
        var arm = Arms(Registry).Find("one")!;
        var settings = new MutationSettings { RunReads = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["LIB_TESTS"] = ["data"] } };
        var tree = new TreeFiles(
            new SyncManifest(
                "tree",
                new Dictionary<string, SyncEntry>(StringComparer.Ordinal) { ["data/golden.txt"] = new("data/golden.txt", 6, "golden") }),
            StringComparer.Ordinal);

        Assert.Equal(["data/golden.txt"], JudgedOn.RunReadsOf(arm, settings, tree).Keys);
        Assert.Empty(new RunReadsOf(settings, tree).Of(arm).Uncovered);
    }

    /// <summary>The key of <paramref name="arm"/> where everything but its rows is held as one value.</summary>
    private static string Key(MutationArm arm) => KeyOf(arm, new Dictionary<string, string?>(StringComparer.Ordinal)).Key!;

    /// <summary>
    /// What <paramref name="arm"/> is judged on where the reading of the tree holds <paramref name="tree"/>, and everything
    /// else is held as one value: one table of a file of each kind, one compiler, no developer environment, and the same
    /// settings and host.
    /// </summary>
    private static ArmKey KeyOf(MutationArm arm, IReadOnlyDictionary<string, string?> tree)
    {
        var table = new BuiltFromTable
        {
            Target = arm.Target,
            Lines = "lines",
            Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["src/a.cpp"] = "a" },
            Fetched = new Dictionary<string, string>(StringComparer.Ordinal) { ["plumb"] = "sources" },
            Made = new Dictionary<string, FilesIn>(StringComparer.Ordinal) { [BuiltFromReader.BuildDirectory] = new(1, "made") },
            Outside = new Dictionary<string, FilesIn>(StringComparer.Ordinal) { ["C:/sys/include"] = new(2, "headers") },
        };

        return ArmKey.Of(
            new JudgedOn
            {
                Judge = ArmJudge.Revision,
                Rows = JudgedOn.RowsOf(arm),
                Texts = JudgedOn.TextsOf(arm).ToDictionary(path => path, path => tree.GetValueOrDefault(path), StringComparer.Ordinal),
                Sites = arm.Sites.ToDictionary(site => site.Site, site => tree.GetValueOrDefault(site.Site), StringComparer.Ordinal),
                Built = new Dictionary<string, string>(StringComparer.Ordinal),
                Compilers = [new JudgedCompiler("CXX", "GNU", "13.3.0", "C:/mingw/bin/g++.exe", [])],
                DeveloperEnvironment = null,
                Settings = new SweepSettings("build", "run", "judge"),
                RunReads = new Dictionary<string, string>(StringComparer.Ordinal) { ["data/golden.txt"] = "golden" },
                Host = "local",
            },
            [BuiltFromReading.Read(table)]);
    }

    /// <summary>
    /// <paramref name="key"/> with what it is judged on as <paramref name="judged"/> makes it and its table as
    /// <paramref name="table"/> makes it, and read or not as <paramref name="unread"/> says.
    /// </summary>
    private static ArmKey Again(ArmKey key, Func<JudgedOn, JudgedOn>? judged = null, Func<BuiltFromTable, BuiltFromTable>? table = null, string? unread = null)
        => ArmKey.Of(
            (judged ?? (on => on))(key.JudgedOn),
            [.. key.Tables.Values.Select(each => BuiltFromReading.Read(table is null ? each : table(each)))],
            unread);

    /// <summary>Each reason by its code and the words a line says it in.</summary>
    private static List<(string Code, string Said)> Said(IReadOnlyList<PendingReason> reasons) => [.. reasons.Select(reason => (reason.Code, reason.Said))];
}
