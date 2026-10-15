using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Mutations;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// What <c>check-mutations --list</c> makes of the record and what this machine reads: each arm pending for the first reason
/// that applies, in its own words, or unmoved here - never holding - the verdict shown, and the document every reason is in.
/// </summary>
public sealed class MutationListingTests
{
    /// <summary>A TEST-RED arm citing three texts, and a BUILD-RED arm with its paired control.</summary>
    private const string Registry = """
        A | one | src/a.cpp | texts/a.before | texts/a.after | TEST-RED | lib | lib_tests | 3 | texts/a.diag | why one
        C | one | Suite.A | red
        A | two | src/d.hpp | texts/d.before | texts/d.after | BUILD-RED | lib | - | 0 | PAIRED-CONTROL | why two
        B | two | texts/d.cb | texts/d.ca | another builds
        """;

    private static readonly SweepSettings Settings = new("build", "run", "judge");

    private static readonly MutationArm One = Arms()[0];

    /// <summary>
    /// An arm nothing read here moved for since its passed is unmoved here - its row saying what was read and found as
    /// recorded - and is never said to hold: a listing reads no compiler, no build line and no file a build makes.
    /// </summary>
    [Fact]
    public void AnArmNothingReadHereMovedFor_IsUnmovedHere_NeverHolds()
    {
        var listing = new Listing();
        var leg = MutationListing.Of(listing.Input());
        var arm = Assert.Single(leg.Arms);

        Assert.False(arm.Pending);
        Assert.Empty(arm.Why);
        Assert.Same(listing.Entry, arm.Last);
        Assert.Equal((3, 1, 2), (arm.Texts, arm.Sites, arm.Files));
        Assert.Equal(["lines", "set", "made", "fetched", "outside", "compilers", "developerEnvironment", "host"], arm.NotRead);
        Assert.EndsWith(
            "unmoved here: its rows, 3 text(s), 1 site(s) and 2 file(s) of the tree are as recorded",
            MutationListing.Table([leg], text => text)[2],
            StringComparison.Ordinal);

        using var document = JsonDocument.Parse(MutationListing.Json([leg], "arms.txt", "record", text => text));
        var listed = document.RootElement.GetProperty("legs")[0].GetProperty("arms")[0];

        Assert.Equal("unmoved", listed.GetProperty("state").GetString());
        Assert.False(listed.GetProperty("verified").GetBoolean());
    }

    /// <summary>
    /// An arm whose leg's build is generated from a list file that moved, or with a configuration that changed, is unmoved
    /// here still - what its targets read is what its key holds - and its row says what moved.
    /// </summary>
    [Fact]
    public void AnArmWhoseListFilesMoved_IsUnmovedHere_SayingSo()
    {
        var listing = new Listing();

        listing.Tree["CMakeLists.txt"] = "edited";

        var moved = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.False(moved.Pending);
        Assert.Equal(["CMakeLists.txt"], moved.CausesMoved);
        Assert.EndsWith(
            "unmoved here, though 1 file(s) its build is generated from moved (CMakeLists.txt)",
            MutationListing.Table([MutationListing.Of(listing.Input())], text => text)[2],
            StringComparison.Ordinal);

        var configured = MutationListing.Of(listing.Input() with { Generation = "another" });

        Assert.EndsWith(
            "unmoved here, though 1 file(s) its build is generated from moved (CMakeLists.txt), and what the configuration gives its configure changed",
            MutationListing.Table([configured], text => text)[2],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The files its build is generated from that moved list among <c>causesMoved</c>, the one closed shape of what moved
    /// beside the key: the document carries no member of their own, and the row says both them and what the configuration
    /// gives the configure, where either moved.
    /// </summary>
    [Fact]
    public void TheFilesItsBuildIsGeneratedFrom_ListAmongCausesMoved_WithNoMemberOfTheirOwn()
    {
        var listing = new Listing();

        listing.Tree["CMakeLists.txt"] = "edited";

        var configured = MutationListing.Of(listing.Input() with { Generation = "another" });

        using var document = JsonDocument.Parse(MutationListing.Json([configured], "arms.txt", "the record", text => text));
        var arm = document.RootElement.GetProperty("legs")[0].GetProperty("arms")[0];

        Assert.Equal(["CMakeLists.txt"], arm.GetProperty("causesMoved").EnumerateArray().Select(file => file.GetString()));
        Assert.False(arm.TryGetProperty("generationMoved", out _));

        Assert.EndsWith(
            "unmoved here, though 1 file(s) its build is generated from moved (CMakeLists.txt), and what the configuration gives its configure changed",
            MutationListing.Table([configured], text => text)[2],
            StringComparison.Ordinal);

        var editedOnly = MutationListing.Of(listing.Input());

        Assert.Equal(["CMakeLists.txt"], Assert.Single(editedOnly.Arms).CausesMoved);
        Assert.EndsWith(
            "unmoved here, though 1 file(s) its build is generated from moved (CMakeLists.txt)",
            MutationListing.Table([editedOnly], text => text)[2],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A run-read file that leaves what its runner's entry covers - moved out of the directory it names, so the file is
    /// still in the tree and only what covers it is gone - moves the key as one whose content changed does.
    /// </summary>
    [Fact]
    public void AFileThatLeavesWhatItsRunnerCovers_MovesTheKeyAsOneThatMoved()
    {
        var listing = new Listing();

        Assert.False(MutationListing.Of(listing.Input()).Arms[0].Pending);

        listing.Tree.Remove("data/golden.txt");
        listing.Tree["elsewhere/golden.txt"] = "golden";

        var moved = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.True(moved.Pending);
        Assert.Equal(("run-reads-moved", "what its runner reads at run time moved: data/golden.txt"), (moved.Why[0].Code, moved.Why[0].Said));
    }

    /// <summary>
    /// Each reason a listing gives is said in its own words, and the first that applies is the one an arm's row names:
    /// one case to each code a listing reads, each made to apply over every reason after it in the order.
    /// </summary>
    [Theory]
    [InlineData("record-unreadable", "the record of this leg cannot be read: it is not JSON")]
    [InlineData("record-newer", "the record of this leg was written by a newer dssharness")]
    [InlineData("leg-unsweepable", "this leg cannot be swept: Leg 'native' builds with toolchain 'vs'")]
    [InlineData("never-swept", "never swept on this leg")]
    [InlineData("last-not-passed", "its last verdict is violated, not passed")]
    [InlineData("not-keyed", "what it was judged on could not be read when it was swept: a file it reads is gone")]
    [InlineData("judge-revised", "the judge's rules changed since (revision {before}, now {revision})")]
    [InlineData("rows-changed", "its A rows changed")]
    [InlineData("text-moved", "a text it cites moved: texts/a.before")]
    [InlineData("site-moved", "its site moved: src/a.cpp")]
    [InlineData("files-moved", "what 'lib_tests' is built from moved: src/lib.cpp (gone)")]
    [InlineData("run-reads-moved", "what its runner reads at run time moved: data/golden.txt")]
    [InlineData("build-environment-changed", "the environment its build is given changed")]
    [InlineData("run-settings-changed", "how its run starts changed")]
    [InlineData("judge-settings-changed", "what its run is judged by changed")]
    [InlineData("host-changed", "it was judged on local, and this leg runs on wsl:Ubuntu")]
    public void EachPendingReason_IsSaidInItsOwnWords_TheFirstThatAppliesInOrder(string code, string said)
    {
        said = said
            .Replace("{before}", (ArmJudge.Revision - 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{revision}", ArmJudge.Revision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var listing = new Listing();
        var order = new[]
        {
            "record-unreadable", "record-newer", "leg-unsweepable", "never-swept", "last-not-passed", "not-keyed", "judge-revised",
            "rows-changed", "text-moved", "site-moved", "files-moved", "run-reads-moved", "build-environment-changed", "run-settings-changed",
            "judge-settings-changed", "host-changed",
        };

        // Every reason from this one on applies; those before it do not.
        foreach (var applies in order.SkipWhile(each => each != code).Reverse())
        {
            listing.Make(applies);
        }

        var leg = MutationListing.Of(listing.Input());
        var arm = Assert.Single(leg.Arms);

        Assert.True(arm.Pending);
        Assert.Equal((code, said), (arm.Why[0].Code, arm.Why[0].Said.Replace(ToolPackage.Id, "dssharness", StringComparison.Ordinal)));
        Assert.EndsWith(
            code == "leg-unsweepable" ? "pending: this leg cannot be swept, as said above" : $"pending: {arm.Why[0].Said}",
            MutationListing.Table([leg], text => text)[2],
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdict shown is the newest nothing read here moved for, else the newest; an arm whose verdict shown so is no
    /// passed is pending for it.
    /// </summary>
    [Fact]
    public void TheVerdictShown_IsTheNewestNothingMovedFor_ElseTheNewest()
    {
        var listing = new Listing();
        var older = listing.Entry;
        var newer = older with
        {
            Key = "newer",
            Run = "20261013-090000-00000000",
            JudgedOn = older.JudgedOn! with { Texts = new Dictionary<string, string?>(older.JudgedOn.Texts) { ["texts/a.before"] = "then" } },
        };

        listing.Entries.Add(newer);

        var unmoved = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.Equal((false, older.Run), (unmoved.Pending, unmoved.Last!.Run));

        listing.Entries[0] = older with { Verdict = "violated" };

        var violated = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.True(violated.Pending);
        Assert.Equal(("last-not-passed", older.Run), (violated.Why[0].Code, violated.Last!.Run));

        // Both moved: the newer is shown.
        listing.Entries[0] = older;
        listing.Tree["texts/a.before"] = "moved";

        Assert.Equal(newer.Run, Assert.Single(MutationListing.Of(listing.Input()).Arms).Last!.Run);

        // Both unmoved here - differing only in what a listing never reads - the newer is shown.
        var recompiled = older with
        {
            Key = "recompiled",
            Run = "20261013-100000-00000000",
            JudgedOn = older.JudgedOn with { Compilers = [new JudgedCompiler("CXX", "GNU", "14.1.0", "/usr/bin/c++", [])] },
        };

        listing.Tree["texts/a.before"] = "before";
        listing.Entries.Add(recompiled);

        var newest = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.Equal((false, recompiled.Run), (newest.Pending, newest.Last!.Run));

        listing.Entries.RemoveAt(2);
        listing.Tree["texts/a.before"] = "moved";
        listing.Entries.RemoveAt(0);

        var moved = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.Equal(("text-moved", newer.Run), (moved.Why[0].Code, moved.Last!.Run));
    }

    /// <summary>
    /// A verdict recorded with no key, or one a table of whose the record cannot give, is pending though nothing read here
    /// moved: what it was judged on was never read whole, or cannot be read back.
    /// </summary>
    [Fact]
    public void AVerdictWithNoKey_OrATableTheRecordCannotGive_IsPending_ThoughNothingReadHereMoved()
    {
        var listing = new Listing();

        listing.Entries[0] = listing.Entry with { Key = null, KeyUnread = "a file it reads is gone" };

        var unkeyed = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.True(unkeyed.Pending);
        Assert.Equal(["not-keyed"], unkeyed.Why.Select(reason => reason.Code));

        listing.Entries[0] = listing.Entry;
        listing.Lost = listing.Entry.JudgedOn!.Built["lib"];

        var untabled = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.True(untabled.Pending);
        Assert.Equal(["record-unreadable"], untabled.Why.Select(reason => reason.Code));
        Assert.Equal($"the record of this leg cannot be read: '{listing.Lost}.json' is gone", untabled.Why[0].Said);
    }

    /// <summary>
    /// A file of the tree a table names that still stands but holds something else moves the arm, named as such - "(gone)"
    /// says only a file the reading no longer holds.
    /// </summary>
    [Fact]
    public void AFileATableNames_ThatStillStandsButHoldsSomethingElse_MovesTheArm()
    {
        var listing = new Listing();

        listing.Tree["src/lib.cpp"] = "moved";

        var arm = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.True(arm.Pending);
        Assert.Equal(("files-moved", "what 'lib_tests' is built from moved: src/lib.cpp"), (arm.Why[0].Code, arm.Why[0].Said));
    }

    /// <summary>
    /// The reasons an arm is pending for are in the order they are stated in however they were gathered: a record that
    /// cannot be read and a leg that cannot be swept are both said, the record first, and a table the record cannot give
    /// comes before the leg's own trouble.
    /// </summary>
    [Fact]
    public void TheReasonsAreInTheirOrder_HoweverTheyWereGathered()
    {
        var listing = new Listing { Unsweepable = "Leg 'native' builds with toolchain 'vs'" };

        listing.Unread = RecordUnread.Because("it is not JSON");

        var both = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.Equal(["record-unreadable", "leg-unsweepable"], both.Why.Select(reason => reason.Code));

        listing.Unread = null;
        listing.Lost = listing.Entry.JudgedOn!.Built["lib"];

        var tabled = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.Equal(["record-unreadable", "leg-unsweepable"], tabled.Why.Select(reason => reason.Code));
    }

    /// <summary>
    /// The document's <c>recordUnread</c> names the file that cannot be read, as a listing's warning does, and the warning
    /// itself is written under <c>--json</c> too.
    /// </summary>
    [Fact]
    public void TheDocumentsRecordUnread_NamesTheFile()
    {
        var listing = new Listing();
        listing.Unread = RecordUnread.Because("it is not JSON");

        var leg = MutationListing.Of(listing.Input());

        using var document = JsonDocument.Parse(MutationListing.Json([leg], "arms.txt", "the record", text => text));

        Assert.Equal("native.json: it is not JSON", document.RootElement.GetProperty("legs")[0].GetProperty("recordUnread").GetString());
    }

    /// <summary>
    /// A listing asks after a table of the record for no entry older than the newest nothing moved for: what the newest
    /// unmoved entry names is asked after, and what an older one names never.
    /// </summary>
    [Fact]
    public void AListing_AsksAfterNoTableOfAnEntryOlderThanTheNewestUnmoved()
    {
        var listing = new Listing();
        var older = new BuiltFromTable
        {
            Target = "lib_tests",
            Lines = "lines",
            Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["src/other.cpp"] = "other" },
            Fetched = new Dictionary<string, string>(StringComparer.Ordinal),
            Made = new Dictionary<string, FilesIn>(StringComparer.Ordinal),
            Outside = new Dictionary<string, FilesIn>(StringComparer.Ordinal),
        };
        var entry = listing.Entry with
        {
            Key = "older",
            Run = "20261011-090000-00000000",
            JudgedOn = listing.Entry.JudgedOn! with { Built = new Dictionary<string, string>(listing.Entry.JudgedOn.Built) { ["lib_tests"] = older.Digest() } },
        };

        listing.Entries.Insert(0, entry);

        var arm = Assert.Single(MutationListing.Of(listing.Input()).Arms);

        Assert.False(arm.Pending);
        Assert.DoesNotContain(older.Digest(), listing.Asked, StringComparer.Ordinal);
        Assert.Equal([.. listing.Entry.JudgedOn!.Built.Values.Order(StringComparer.Ordinal)], listing.Asked.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The listing's document carries every reason an arm is pending for, with the target and paths each names, what was
    /// read and what was not, and the verdict shown - its key and where it was swept; and each leg's counts.
    /// </summary>
    [Fact]
    public void AListingsJson_CarriesEveryReason_AndWhatWasNotRead()
    {
        var listing = new Listing { Host = "local" };

        listing.Make("text-moved");
        listing.Make("files-moved");
        listing.Make("run-settings-changed");

        var leg = MutationListing.Of(listing.Input());

        using var document = JsonDocument.Parse(MutationListing.Json([leg], "arms.txt", "the record", text => $"<{text}>"));
        var root = document.RootElement;
        var listed = root.GetProperty("legs")[0];
        var arm = listed.GetProperty("arms")[0];

        Assert.Equal("1 arm(s) on 1 leg(s): 1 pending, 0 unmoved here", root.GetProperty("summary").GetString());
        Assert.Equal(("arms.txt", "<the record>"), (root.GetProperty("registry").GetString(), root.GetProperty("mutationRecord").GetString()));
        Assert.Equal((true, 1, 0), (listed.GetProperty("sweepable").GetBoolean(), listed.GetProperty("pending").GetInt32(), listed.GetProperty("unmoved").GetInt32()));
        Assert.Equal(
            ["text-moved", "files-moved", "run-settings-changed"],
            arm.GetProperty("why").EnumerateArray().Select(reason => reason.GetProperty("code").GetString()));
        Assert.Equal(
            ("lib_tests", "src/lib.cpp"),
            (arm.GetProperty("why")[1].GetProperty("target").GetString(), arm.GetProperty("why")[1].GetProperty("paths")[0].GetString()));
        Assert.Equal(
            ["lines", "set", "made", "fetched", "outside", "compilers", "developerEnvironment"],
            arm.GetProperty("notRead").EnumerateArray().Select(part => part.GetString()));
        Assert.Equal((3, 1, 2), (arm.GetProperty("read").GetProperty("texts").GetInt32(), arm.GetProperty("read").GetProperty("sites").GetInt32(), arm.GetProperty("read").GetProperty("files").GetInt32()));
        Assert.Equal(
            ("passed", listing.Entry.Key, RepositoryTree.MainCheckout, "local", "<it was passed>"),
            (arm.GetProperty("last").GetProperty("verdict").GetString(), arm.GetProperty("last").GetProperty("key").GetString(),
                arm.GetProperty("last").GetProperty("sweptIn").GetString(), arm.GetProperty("last").GetProperty("host").GetString(),
                arm.GetProperty("last").GetProperty("detail").GetString()));
    }

    /// <summary>A leg's line counts its arms pending, grouped by the first reason each is pending for, most first, and those unmoved here.</summary>
    [Fact]
    public void ALegsLine_CountsItsArmsPending_ByTheFirstReason_AndThoseUnmoved()
    {
        var listing = new Listing();
        var arms = Arms();
        var three = arms[0] with { Id = "three" };

        listing.Arms = [arms[0], arms[1], three];

        var leg = MutationListing.Of(listing.Input());

        Assert.Equal("native: 3 arm(s): 2 pending (2 never swept); 1 unmoved here", MutationListing.Table([leg], text => text)[^1]);

        listing.Tree["texts/a.before"] = "moved";
        listing.Now = listing.Now with { Run = "another" };

        Assert.Equal("native: 3 arm(s): 3 pending (2 never swept, 1 whose texts moved)", MutationListing.Table([MutationListing.Of(listing.Input())], text => text)[^1]);
    }

    /// <summary>The arms <see cref="Registry"/> declares.</summary>
    private static IReadOnlyList<MutationArm> Arms()
    {
        var reading = MutationRegistryParser.Parse(MutationRegistryParser.Lines(Registry), texts: null);

        Assert.True(reading.Valid, string.Join("; ", reading.Problems));

        return reading.Registry.Arms;
    }

    /// <summary>
    /// A leg as a listing reads it, made so arm <c>one</c> has one recorded passed nothing read here moved for, until a test
    /// makes a reason apply (<see cref="Make"/>).
    /// </summary>
    private sealed class Listing
    {
        private readonly BuiltFromTable _lib = Table("lib", "src/a.cpp");
        private readonly BuiltFromTable _tests = Table("lib_tests", "src/lib.cpp");

        public Listing()
        {
            Tree = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/a.cpp"] = "a",
                ["src/lib.cpp"] = "lib",
                ["texts/a.before"] = "before",
                ["texts/a.after"] = "after",
                ["texts/a.diag"] = "diag",
                ["CMakeLists.txt"] = "list",
                ["data/golden.txt"] = "golden",
            };

            var on = new JudgedOn
            {
                Judge = ArmJudge.Revision,
                Rows = JudgedOn.RowsOf(One),
                Texts = JudgedOn.TextsOf(One).ToDictionary(path => path, path => (string?)Tree[path], StringComparer.Ordinal),
                Sites = One.Sites.ToDictionary(site => site.Site, site => (string?)Tree[site.Site], StringComparer.Ordinal),
                Built = new Dictionary<string, string>(StringComparer.Ordinal) { ["lib"] = _lib.Digest(), ["lib_tests"] = _tests.Digest() },
                Compilers = [new JudgedCompiler("CXX", "GNU", "13.3.0", "/usr/bin/c++", [])],
                Settings = Settings,
                RunReads = new Dictionary<string, string>(StringComparer.Ordinal) { ["data/golden.txt"] = "golden" },
                Host = "local",
            };

            Entry = new RecordedVerdict
            {
                Verdict = "passed",
                Detail = "it was passed",
                Run = "20261012-145059-ba433418",
                At = new DateTimeOffset(2026, 10, 12, 14, 58, 11, TimeSpan.Zero),
                SweptIn = RepositoryTree.MainCheckout,
                Key = on.Key(),
                JudgedOn = on,
                Causes = new JudgedCauses(new Dictionary<string, string>(StringComparer.Ordinal) { ["CMakeLists.txt"] = "list" }, "generation"),
            };
            Entries = [Entry];
        }

        public Dictionary<string, string> Tree { get; }

        public RecordedVerdict Entry { get; }

        public List<RecordedVerdict> Entries { get; }

        public IReadOnlyList<MutationArm> Arms { get; set; } = [One];

        public RecordUnread? Unread { get; set; }

        public string? Unsweepable { get; set; }

        public SweepSettings Now { get; set; } = Settings;

        public string? Host { get; set; }

        /// <summary>The digest of a table the record cannot give, or <see langword="null"/>.</summary>
        public string? Lost { get; set; }

        /// <summary>Each digest a table of the record was asked after, in the order asked.</summary>
        public List<string> Asked { get; } = [];

        /// <summary>Makes the reason <paramref name="code"/> names apply to arm <c>one</c>.</summary>
        public void Make(string code)
        {
            switch (code)
            {
                case "record-unreadable": Unread = RecordUnread.Because("it is not JSON"); break;
                case "record-newer": Unread = RecordUnread.Newer(2); break;
                case "leg-unsweepable": Unsweepable = "Leg 'native' builds with toolchain 'vs'"; break;
                case "never-swept": Entries.Clear(); break;
                case "last-not-passed": Entries[^1] = Entries[^1] with { Verdict = "violated" }; break;
                case "not-keyed": Entries[^1] = Entries[^1] with { Key = null, KeyUnread = "a file it reads is gone" }; break;
                case "judge-revised": Judged(on => on with { Judge = ArmJudge.Revision - 1 }); break;
                case "rows-changed": Judged(on => on with { Rows = on.Rows with { A = "another" } }); break;
                case "text-moved": Tree["texts/a.before"] = "moved"; break;
                case "site-moved": Tree["src/a.cpp"] = "moved"; break;
                case "files-moved": Tree.Remove("src/lib.cpp"); break;
                case "run-reads-moved": Tree["data/golden.txt"] = "moved"; break;
                case "build-environment-changed": Now = Now with { Build = "another" }; break;
                case "run-settings-changed": Now = Now with { Run = "another" }; break;
                case "judge-settings-changed": Now = Now with { Judge = "another" }; break;
                case "host-changed": Host = "wsl:Ubuntu"; break;
                default: throw new ArgumentOutOfRangeException(nameof(code), code, null);
            }
        }

        /// <summary>What the listing reads of the leg, as the test has made it.</summary>
        public ListingInput Input()
        {
            var tables = new[] { _lib, _tests }.ToDictionary(table => table.Digest(), StringComparer.Ordinal);
            var reading = new SyncManifest(
                "tree",
                Tree.ToDictionary(pair => pair.Key, pair => new SyncEntry(pair.Key, pair.Value.Length, pair.Value), StringComparer.Ordinal));
            var arms = new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase)
            {
                ["one"] = [.. Entries],
            };

            if (Entries.Count == 0)
            {
                arms.Clear();
            }

            return new ListingInput(
                "native",
                Arms,
                Unsweepable,
                Unread is null
                    ? LegVerdictRecord.Read("native.json", arms, new Dictionary<string, TableIndex>(StringComparer.Ordinal))
                    : LegVerdictRecord.Unreadable("native.json", Unread),
                digest => digest != Lost && tables.TryGetValue(digest, out var table) ? AskedTable(digest, table) : AskedLost(digest),
                new TreeFiles(reading, StringComparer.Ordinal),
                Host,
                _ => Now,
                new MutationSettings { RunReads = new() { ["lib_tests"] = ["data"] } },
                "generation");
        }

        private void Judged(Func<JudgedOn, JudgedOn> change) => Entries[^1] = Entries[^1] with { JudgedOn = change(Entries[^1].JudgedOn!) };

        /// <summary>Says the digest was asked after, and gives the table.</summary>
        private RecordedTable AskedTable(string digest, BuiltFromTable table)
        {
            Asked.Add(digest);

            return RecordedTable.Read(table);
        }

        /// <summary>Says the digest was asked after, and gives none.</summary>
        private RecordedTable AskedLost(string digest)
        {
            Asked.Add(digest);

            return RecordedTable.Unreadable($"'{digest}.json' is gone");
        }

        /// <summary>A table of <paramref name="target"/>, reading <paramref name="file"/> of the tree as it holds it now.</summary>
        private static BuiltFromTable Table(string target, string file) => new()
        {
            Target = target,
            Lines = "lines",
            Files = new Dictionary<string, string>(StringComparer.Ordinal) { [file] = file == "src/a.cpp" ? "a" : "lib" },
            Fetched = new Dictionary<string, string>(StringComparer.Ordinal),
            Made = new Dictionary<string, FilesIn>(StringComparer.Ordinal),
            Outside = new Dictionary<string, FilesIn>(StringComparer.Ordinal),
        };
    }
}
