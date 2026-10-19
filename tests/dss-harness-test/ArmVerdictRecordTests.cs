using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Mutations;
using DssHarness.Core.Repository;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// The record of verdicts: one file to a leg in the main checkout, an arm's entries one to a key and at most eight, the
/// tables they name written with the first and removed with the last, merged under the file's mutex so two sweeps lose
/// nothing, and a record that cannot be read never written.
/// </summary>
public sealed class ArmVerdictRecordTests : IDisposable
{
    private const string Leg = "native";

    private static readonly DateTimeOffset Noon = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();

    public ArmVerdictRecordTests()
    {
        Layout = new HarnessLayout(_temp.Combine("main"), _temp.Combine("main"));
        Record = new ArmVerdictRecord(_harness.FileSystem, Layout);
    }

    private HarnessLayout Layout { get; }

    private ArmVerdictRecord Record { get; }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A record that cannot be written stops the sweep's recording: one warning naming the file and why, once on the
    /// leg's line, and nothing retried - the arm's verdict stands as it was reached, and nothing of the record is the
    /// arm's.
    /// </summary>
    [Fact]
    public void ARecordThatCannotBeWritten_StopsRecording_SaidOnce()
    {
        var sink = new RecordSink(Record, Leg, _harness.Output, MutationService.CommandName);
        var table = Table("fixture_tests", "src/fixture.cpp");
        var file = Record.FileOf(Leg);

        // A file where the record's directory would be: nothing of it can be made.
        Directory.CreateDirectory(Path.GetDirectoryName(Layout.MutationRecordDirectory)!);
        File.WriteAllText(Layout.MutationRecordDirectory, "in the way");

        sink.Judged("charge", Verdict("passed", "k1", Noon, table), [table]);
        sink.Judged("floor", Verdict("passed", "k2", Noon, table), [table]);

        Assert.Single(_harness.StandardError.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith(
            $"check-mutations: WARN - '{file}' could not be written, so this sweep records no verdict of '{Leg}' from arm 'charge' on:",
            _harness.StandardError.ToString().TrimEnd('\r', '\n'),
            StringComparison.Ordinal);
        Assert.StartsWith(
            $"its verdicts from arm 'charge' on were not recorded: '{file}' could not be written:",
            sink.Unrecorded,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A later verdict on a key replaces the arm's entry on that key, whatever either verdict was - a passed followed by
    /// anything else on the same key is no longer a passed - and stands as the newest.
    /// </summary>
    [Fact]
    public void ALaterVerdictOnTheSameKey_ReplacesAPassed()
    {
        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k1", Noon), []));
        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k2", Noon.AddMinutes(1)), []));
        Assert.Null(Record.Merge(Leg, "charge", Verdict("violated", "k1", Noon.AddMinutes(2)), []));

        Assert.Equal([("passed", "k2"), ("violated", "k1")], Entries("charge"));

        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k1", Noon.AddMinutes(3)), []));

        Assert.Equal([("passed", "k2"), ("passed", "k1")], Entries("charge"));
    }

    /// <summary>
    /// A verdict on another key is kept beside the arm's others, up to eight, the oldest by its place in the file going
    /// first - never by when it was reached, which a host whose clock steps may say out of order. A verdict with no key
    /// replaces the arm's entry with no key, as one on a key replaces that key's.
    /// </summary>
    [Fact]
    public void AVerdictOnAnotherKey_IsKeptBesideIt_UpToTheBound_TheOldestGoingFirst()
    {
        for (var index = 1; index <= ArmVerdictRecord.KeptPerArm + 1; index++)
        {
            // Each reached earlier than the one before, by the clock.
            Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", $"k{index}", Noon.AddHours(-index)), []));
        }

        Assert.Equal([.. Enumerable.Range(2, ArmVerdictRecord.KeptPerArm).Select(index => ("passed", $"k{index}"))], Entries("charge"));

        Assert.Null(Record.Merge(Leg, "floor", Verdict("failed", null, Noon), []));
        Assert.Null(Record.Merge(Leg, "floor", Verdict("passed", "k1", Noon), []));
        Assert.Null(Record.Merge(Leg, "floor", Verdict("poisoned", null, Noon), []));

        Assert.Equal([("passed", "k1"), ("poisoned", null)], Entries("floor"));
        Assert.Equal("a file it reads is gone", Record.Read(Leg).Arms["floor"][^1].KeyUnread);
    }

    /// <summary>
    /// An arm is one arm however its id's letters are cased, and its entries are kept under the spelling it was last
    /// recorded by.
    /// </summary>
    [Fact]
    public void AnArmsEntries_AreKeptUnderItsLastSpelling_HoweverItsIdIsCased()
    {
        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k1", Noon), []));
        Assert.Null(Record.Merge(Leg, "Charge", Verdict("passed", "k2", Noon), []));

        var read = Record.Read(Leg);

        Assert.Equal(["Charge"], read.Arms.Keys);
        Assert.Equal(2, read.Arms["charge"].Count);
    }

    /// <summary>
    /// What an entry is written as reads back as it was: its key the digest of what it was judged on, a kind of row the
    /// arm has none of and a developer environment its toolchain names none of left out, and read as nothing.
    /// </summary>
    [Fact]
    public void AnEntry_ReadsBackAsItWasWritten_ItsKeyItsPartsAndItsCauses()
    {
        var table = Table("fixture_tests", "src/fixture.cpp");
        var written = Verdict("passed", null, Noon, table) with { Verified = new RecordedRun("20261012-171455-0c11aa02", Noon.AddHours(5)) };

        written = written with { Key = written.JudgedOn!.Key(), KeyUnread = null };

        Assert.Null(Record.Merge(Leg, "charge", written, [table]));

        var read = Assert.Single(Record.Read(Leg).Arms["charge"]);

        Assert.Equal(written.JudgedOn!.Key(), read.JudgedOn!.Key());
        Assert.Equal(written.Key, read.Key);
        Assert.Equal(JsonSerializer.Serialize(written, JsonStateFile.Options), JsonSerializer.Serialize(read, JsonStateFile.Options));
        Assert.Null(read.JudgedOn.Rows.C);
        Assert.Null(read.JudgedOn.DeveloperEnvironment);
        var index = table.Index();

        Assert.Equal(index, Record.Read(Leg).Tables[table.Digest()] with { Fetched = index.Fetched });
    }

    /// <summary>
    /// Two sweeps recording at once - of one leg, from two trees, each its own process's worth of merges, interleaved - lose
    /// no verdict: each change is one read, merge and write under the file's mutex, entries merged by arm and key.
    /// </summary>
    [Fact]
    public async Task TwoSweepsRecordingAtOnce_LoseNoVerdict()
    {
        var sweeps = new[] { new ArmVerdictRecord(_harness.FileSystem, Layout), new ArmVerdictRecord(_harness.FileSystem, Layout) };
        var table = Table("fixture_tests", "src/fixture.cpp");

        await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(
            () => Assert.Null(sweeps[index % 2].Merge(Leg, $"arm-{index / 2}", Verdict("passed", $"tree-{index % 2}", Noon, table), [table])),
            TestContext.Current.CancellationToken)));

        var read = Record.Read(Leg);

        Assert.Equal(20, read.Arms.Count);
        Assert.All(read.Arms.Values, entries => Assert.Equal(["tree-0", "tree-1"], entries.Select(entry => entry.Key).Order(StringComparer.Ordinal)));
        Assert.True(File.Exists(Record.TableFile(Leg, table.Digest())));
    }

    /// <summary>
    /// A table is written with the first entry that names it - never one no entry names - indexed beside the entries by the
    /// digests of its parts, and removed with the last entry naming it, from the index and from the disk.
    /// </summary>
    [Fact]
    public void ATable_IsWrittenWithTheFirstEntryThatNamesIt_AndRemovedWithTheLast()
    {
        var shared = Table("fixture", "src/fixture.cpp");
        var other = Table("fixture_tests", "src/tests.cpp");
        var unnamed = Table("unnamed", "src/unnamed.cpp");

        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k1", Noon, shared), [shared, unnamed]));

        Assert.Equal(JsonSerializer.Serialize(shared, JsonStateFile.Options), File.ReadAllText(Record.TableFile(Leg, shared.Digest())));
        Assert.False(File.Exists(Record.TableFile(Leg, unnamed.Digest())));
        Assert.Equal([shared.Digest()], Record.Read(Leg).Tables.Keys);

        Assert.Null(Record.Merge(Leg, "floor", Verdict("passed", "k1", Noon, shared), [shared]));
        Assert.Null(Record.Merge(Leg, "charge", Verdict("violated", "k1", Noon, other), [other]));

        // The floor's entry still names it.
        Assert.True(File.Exists(Record.TableFile(Leg, shared.Digest())));

        Assert.Null(Record.Merge(Leg, "floor", Verdict("passed", "k1", Noon, other), [other]));

        Assert.False(File.Exists(Record.TableFile(Leg, shared.Digest())));
        Assert.True(File.Exists(Record.TableFile(Leg, other.Digest())));
        Assert.Equal([other.Digest()], Record.Read(Leg).Tables.Keys);
        var index = other.Index();

        Assert.Equal(index, Record.Read(Leg).Tables[other.Digest()] with { Fetched = index.Fetched });
    }

    /// <summary>
    /// A table is read back by the digest an entry names it by, as it was recorded; one that is gone, cannot be read, or
    /// holds a table of another digest is no table, and says which.
    /// </summary>
    [Fact]
    public void ATable_IsReadBackByItsDigest_AndOneGoneUnreadableOrOfAnotherDigestSaysSo()
    {
        var table = Table("fixture", "src/fixture.cpp");
        var other = Table("fixture_tests", "src/tests.cpp");
        var file = Record.TableFile(Leg, table.Digest());

        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k1", Noon, table), [table]));

        var read = Record.Table(Leg, table.Digest());

        Assert.Null(read.Unread);
        Assert.Equal(table.Digest(), read.Table!.Digest());

        File.WriteAllText(file, JsonSerializer.Serialize(other, JsonStateFile.Options));

        Assert.Equal(
            (null, $"'{file}' holds a table of another digest, {other.Digest()}"),
            (Record.Table(Leg, table.Digest()).Table, Record.Table(Leg, table.Digest()).Unread));

        File.WriteAllText(file, "{ \"target\":");

        Assert.StartsWith($"'{file}' cannot be read: ", Record.Table(Leg, table.Digest()).Unread, StringComparison.Ordinal);

        File.Delete(file);

        Assert.Equal(
            (null, $"'{file}', which an entry names, is gone"),
            (Record.Table(Leg, table.Digest()).Table, Record.Table(Leg, table.Digest()).Unread));
    }

    /// <summary>
    /// A record that cannot be read - one that is not JSON, one holding a member this build does not know - or one a newer
    /// build wrote is never written: a sweep handing it verdicts says so once, as a warning naming the file and what to do,
    /// and once more on the leg's line, and records none.
    /// </summary>
    [Theory]
    [InlineData("{ \"format\": 1, \"leg\": \"native\", \"arms\":", null)]
    [InlineData("{ \"format\": 1, \"leg\": \"native\", \"arms\": {}, \"tables\": {}, \"shards\": [] }", null)]
    [InlineData("{ \"format\": 2, \"leg\": \"native\", \"shards\": [] }", 2)]
    public void ARecordThatCannotBeRead_OrOfANewerFormat_IsNeverWritten_AndSaysSoOnce(string held, int? newer)
    {
        var file = Record.FileOf(Leg);
        var table = Table("fixture_tests", "src/fixture.cpp");

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, held);

        var sink = new RecordSink(Record, Leg, _harness.Output, MutationService.CommandName);

        sink.Judged("charge", Verdict("passed", "k1", Noon, table), [table]);
        sink.Judged("floor", Verdict("passed", "k2", Noon, table), [table]);

        Assert.Equal(held, File.ReadAllText(file));
        Assert.False(Directory.Exists(Record.TablesOf(Leg)));

        var warnings = _harness.StandardError.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var warning = Assert.Single(warnings);

        if (newer is { } format)
        {
            Assert.Equal(format, Record.Read(Leg).Unread?.Format);
            Assert.Equal(
                $"check-mutations: WARN - '{file}' was written by a newer {ToolPackage.Id} (format 2; this one reads 1), so every arm of "
                + $"'native' is pending and this sweep records none. Update the tool (update-tool).",
                warning.TrimEnd('\r'));
            Assert.Equal($"none of its verdicts was recorded: '{file}' was written by a newer {ToolPackage.Id}", sink.Unrecorded);
        }
        else
        {
            var why = Record.Read(Leg).Unread?.Why;

            Assert.Equal($"it holds what no build of {ToolPackage.Id} writes - another build wrote it, or a hand changed it", why);
            Assert.Equal(
                $"check-mutations: WARN - '{file}' cannot be read, so every arm of 'native' is pending and this sweep records none: {why}. "
                + "Delete it to start that leg's record again.",
                warning.TrimEnd('\r'));
            Assert.Equal($"none of its verdicts was recorded: '{file}' cannot be read", sink.Unrecorded);
        }
    }

    /// <summary>
    /// A record a hand damaged - an arm's entries or a table's index turned to null, a null inside an arm's entries, an
    /// entry given a key and nothing it was judged on, a null inside what it was judged on where no null goes - is refused
    /// as unreadable, naming the file and what to do, never reaching a comparison of it as a crash.
    /// </summary>
    [Theory]
    [InlineData("""{ "format": 1, "leg": "native", "arms": { "charge": null }, "tables": {} }""", "it holds no list of entries for arm 'charge'")]
    [InlineData("""{ "format": 1, "leg": "native", "arms": { "charge": [null] }, "tables": {} }""", "it holds what no build of DssHarness writes - another build wrote it, or a hand changed it")]
    [InlineData("""{ "format": 1, "leg": "native", "arms": {}, "tables": { "77a0": null } }""", "it holds no table index for '77a0'")]
    [InlineData("""{ "format": 1, "leg": "native", "arms": { "charge": [ { "verdict": "passed", "run": "r1", "at": "2026-10-12T14:58:11Z", "sweptIn": "the main checkout", "key": "k1" } ] }, "tables": {} }""", "it holds an entry of arm 'charge' with the key 'k1' and nothing it was judged on")]
    [InlineData(DamagedBuilt, "it holds an entry of arm 'charge' whose target 'lib_tests' is named by no digest")]
    [InlineData(DamagedRunRead, "it holds an entry of arm 'charge' whose run-time read 'data/golden.txt' is named by no hash")]
    [InlineData(DamagedCompiler, "it holds what no build of DssHarness writes - another build wrote it, or a hand changed it")]
    [InlineData(DamagedRows, "it holds what no build of DssHarness writes - another build wrote it, or a hand changed it")]
    [InlineData(DamagedHost, "it holds what no build of DssHarness writes - another build wrote it, or a hand changed it")]
    [InlineData(DamagedSettings, "it holds what no build of DssHarness writes - another build wrote it, or a hand changed it")]
    public void ARecordAHandDamaged_IsRefusedAsUnreadable_NamingWhatIsWrong(string held, string why)
    {
        var file = Record.FileOf(Leg);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, held);

        var unread = Record.Read(Leg).Unread;

        Assert.NotNull(unread);
        Assert.Null(unread.Format);
        Assert.Equal(why, unread.Why);
        Assert.StartsWith($"'{file}' cannot be read, so every arm of 'native' is pending", unread.Warning(file, Leg, sweep: false), StringComparison.Ordinal);
        Assert.EndsWith("Delete it to start that leg's record again.", unread.Warning(file, Leg, sweep: false), StringComparison.Ordinal);

        // And it never reaches a comparison: a sweep under --pending weighing the arm is told the record cannot be read.
        var weighed = Record.ArmsOf(Leg, ["charge"]);

        Assert.Equal(why, weighed.Unread?.Why);
        Assert.Empty(weighed.Arms);
    }

    /// <summary>The entry every damaged judgement is written around: a verdict with a key, judged on a whole <c>judgedOn</c>.</summary>
    private const string EntryHead =
        """{ "format": 1, "leg": "native", "arms": { "charge": [ { "verdict": "passed", "run": "r1", "at": "2026-10-12T14:58:11Z", "sweptIn": "the main checkout", "key": "k1", "judgedOn": """;

    /// <summary>The record's tail after the judgement: the entry's list, the arm's, the tables and the document's.</summary>
    private const string EntryTail = """ } ] }, "tables": {} }""";

    /// <summary>An entry whose target's table is named by no digest: <c>"built": {"lib_tests": null}</c>.</summary>
    private const string DamagedBuilt = EntryHead
        + """{ "judge": 2, "rows": { "a": "rows" }, "texts": {}, "sites": {}, "built": { "lib_tests": null }, "compilers": [], "developerEnvironment": null, "settings": { "build": "b", "run": "r", "judge": "j" }, "runReads": {}, "host": "local" }"""
        + EntryTail;

    /// <summary>An entry whose run-time read is named by no hash: <c>"runReads": {"data/golden.txt": null}</c>.</summary>
    private const string DamagedRunRead = EntryHead
        + """{ "judge": 2, "rows": { "a": "rows" }, "texts": {}, "sites": {}, "built": {}, "compilers": [], "developerEnvironment": null, "settings": { "build": "b", "run": "r", "judge": "j" }, "runReads": { "data/golden.txt": null }, "host": "local" }"""
        + EntryTail;

    /// <summary>An entry with a null where one of its members goes: <c>"host": null</c> - refused as what no build writes, its own reader letting no null into a member.</summary>
    private const string DamagedHost = EntryHead
        + """{ "judge": 2, "rows": { "a": "rows" }, "texts": {}, "sites": {}, "built": {}, "compilers": [], "developerEnvironment": null, "settings": { "build": "b", "run": "r", "judge": "j" }, "runReads": {}, "host": null }"""
        + EntryTail;

    /// <summary>An entry with a null where one of its members goes: <c>"settings": null</c> - refused as what no build writes, its own reader letting no null into a member.</summary>
    private const string DamagedSettings = EntryHead
        + """{ "judge": 2, "rows": { "a": "rows" }, "texts": {}, "sites": {}, "built": {}, "compilers": [], "developerEnvironment": null, "settings": null, "runReads": {}, "host": "local" }"""
        + EntryTail;

    /// <summary>An entry with a compiler that is no compiler: <c>"compilers": [null]</c> - refused as what no build writes, its own reader letting no null into a list.</summary>
    private const string DamagedCompiler = EntryHead
        + """{ "judge": 2, "rows": { "a": "rows" }, "texts": {}, "sites": {}, "built": {}, "compilers": [null], "developerEnvironment": null, "settings": { "build": "b", "run": "r", "judge": "j" }, "runReads": {}, "host": "local" }"""
        + EntryTail;

    /// <summary>An entry with a null where one of its members goes: <c>"rows": null</c> - refused as what no build writes, its own reader letting no null into a member.</summary>
    private const string DamagedRows = EntryHead
        + """{ "judge": 2, "rows": null, "texts": {}, "sites": {}, "built": {}, "compilers": [], "developerEnvironment": null, "settings": { "build": "b", "run": "r", "judge": "j" }, "runReads": {}, "host": "local" }"""
        + EntryTail;

    /// <summary>
    /// What the record holds of the arms a sweep under --pending weighs is every entry of each, under any spelling of its
    /// id, and each table the newest of them names - one it cannot read said why - and nothing of an arm it does not
    /// weigh. A record that cannot be read gives nothing but why.
    /// </summary>
    [Fact]
    public void WhatTheRecordHoldsOfALegsArms_IsEveryEntryOfEach_AndTheTablesTheNewestNames()
    {
        var older = Table("fixture_tests", "src/old.cpp");
        var newer = Table("fixture_tests", "src/fixture.cpp");
        var gone = Table("fixture", "src/gone.cpp");

        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k1", Noon, older), [older]));
        Assert.Null(Record.Merge(Leg, "charge", Verdict("violated", "k2", Noon, newer, gone), [newer, gone]));
        Assert.Null(Record.Merge(Leg, "floor", Verdict("passed", "k3", Noon, older), [older]));
        File.Delete(Record.TableFile(Leg, gone.Digest()));

        var recorded = Record.ArmsOf(Leg, ["CHARGE", "depth"]);

        Assert.Null(recorded.Unread);
        Assert.Equal(["CHARGE"], recorded.Arms.Keys);
        Assert.Equal([("passed", "k1"), ("violated", "k2")], recorded.Arms["Charge"].Select(entry => (entry.Verdict, entry.Key)));
        Assert.Equal([newer.Digest()], recorded.Tables.Keys);
        Assert.Equal([gone.Digest()], recorded.TablesUnread.Keys);
        Assert.Equal($"'{Record.TableFile(Leg, gone.Digest())}', which an entry names, is gone", recorded.TablesUnread[gone.Digest()]);

        File.WriteAllText(Record.FileOf(Leg), "{");

        var unread = Record.ArmsOf(Leg, ["charge"]);

        Assert.NotNull(unread.Unread);
        Assert.Empty(unread.Arms);
    }

    /// <summary>
    /// What a dispatch carries of a record that cannot be read - one that cannot be, or one a newer build wrote - reads
    /// back on the host as it was, so every arm of the leg is pending there for it; the words it is said in are made where
    /// they are said, and never cross. A record said unreadable for neither reason, or for both, is no such record: refused.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public void ARecordThatCannotBeRead_CrossesToTheHostAsItIs(int? newer)
    {
        var unread = newer is { } format ? RecordUnread.Newer(format) : RecordUnread.Because("it is not JSON.");
        var carried = RecordedArms.Unreadable(unread).Carried();
        var read = RecordedArms.OfCarried(carried);

        Assert.Equal((unread.Why, unread.Format, unread.Said), (read.Unread?.Why, read.Unread?.Format, read.Unread?.Said));
        Assert.Empty(read.Arms);
        Assert.DoesNotContain("\"said\"", System.Text.Encoding.UTF8.GetString(carried), StringComparison.Ordinal);

        foreach (var neitherOrBoth in new[] { """{"unread":{}}""", """{"unread":{"why":"it is not JSON","format":2}}""" })
        {
            Assert.Throws<ArgumentException>(() => RecordedArms.OfCarried(System.Text.Encoding.UTF8.GetBytes(neitherOrBoth)));
        }
    }

    /// <summary>
    /// What a sweep under --pending found holding is kept in one change of the record: each arm's entry on the key that
    /// held is marked with the run that verified it, every other entry left as it was, an entry no longer there passed
    /// over - and where the record cannot be read nothing is written, said once as a warning and on the leg's line.
    /// </summary>
    [Fact]
    public void WhatHeld_IsMarkedVerifiedByItsRun_InOneChange_AndAnUnreadRecordIsNeverWritten()
    {
        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k1", Noon), []));
        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k2", Noon), []));
        Assert.Null(Record.Merge(Leg, "floor", Verdict("passed", "k3", Noon), []));

        var by = new RecordedRun("20261013-090000-0c11aa02", Noon.AddHours(1));
        var sink = new RecordSink(Record, Leg, _harness.Output, MutationService.CommandName);

        sink.Verified(new Dictionary<string, string>(StringComparer.Ordinal) { ["CHARGE"] = "k1", ["floor"] = "k9", ["depth"] = "k4" }, by);

        var read = Record.Read(Leg);

        Assert.Equal([("k1", by), ("k2", null)], read.Arms["charge"].Select(entry => (entry.Key, entry.Verified)));
        Assert.Null(Assert.Single(read.Arms["floor"]).Verified);
        Assert.Null(sink.Unrecorded);

        var file = Record.FileOf(Leg);

        File.WriteAllText(file, "{");
        sink.Verified(new Dictionary<string, string>(StringComparer.Ordinal) { ["charge"] = "k2" }, by);
        sink.Verified(new Dictionary<string, string>(StringComparer.Ordinal) { ["charge"] = "k2" }, by);

        Assert.Equal("{", File.ReadAllText(file));
        Assert.Single(_harness.StandardError.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal($"none of its verdicts was recorded: '{file}' cannot be read", sink.Unrecorded);
    }

    /// <summary>
    /// The record is the run state of the machine that keeps it: no sync carries it to a copy, nor deletes a copy's own,
    /// and it is no place a misplaced name is looked for.
    /// </summary>
    [Fact]
    public void TheRecord_IsCarriedByNoSync_AndDeletedByNone()
    {
        var kept = $"{HarnessLayout.MutationRecordDirectoryRelative}/{Leg}.json";
        var table = $"{HarnessLayout.MutationRecordDirectoryRelative}/{ArmVerdictRecord.TablesDirectoryName}/{Leg}/abc.json";
        var exclusions = new SyncExclusions(new SyncConfig(), ".worktrees");

        Assert.Equal(Path.Combine(Layout.MainCheckoutRoot, HarnessLayout.DirectoryName, HarnessLayout.MutationRecordDirectoryName), Layout.MutationRecordDirectory);

        foreach (var path in new[] { HarnessLayout.MutationRecordDirectoryRelative, kept, table })
        {
            Assert.True(HarnessDirectorySync.IsRunState(path), path);
            Assert.True(exclusions.IsWithheldFromTransfer(path), path);
            Assert.True(exclusions.IsProtectedFromDeletion(path), path);
        }

        Assert.False(HarnessDirectorySync.IsRunState($"{HarnessLayout.MutationRecordDirectoryRelative}-other/{Leg}.json"));
    }

    /// <summary>The record ignores itself, as the runs directory does, so git never sees it whatever the tree's own rules say.</summary>
    [Fact]
    public void TheRecord_IgnoresItself()
    {
        Assert.Null(Record.Merge(Leg, "charge", Verdict("passed", "k1", Noon), []));

        Assert.Equal(HarnessLayout.SelfIgnoreRule, File.ReadAllText(Layout.MutationRecordIgnoreFile));
        Assert.Equal(Path.Combine(Layout.MutationRecordDirectory, ".gitignore"), Layout.MutationRecordIgnoreFile);
    }

    /// <summary>
    /// A record of one verdict, with the table it names, kept in <paramref name="layout"/>'s main checkout, as
    /// <see cref="Snapshot"/> reads it back.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Recorded(IFileSystem fileSystem, HarnessLayout layout)
    {
        var table = Table("fixture_tests", "src/fixture.cpp");

        Assert.Null(new ArmVerdictRecord(fileSystem, layout).Merge(Leg, "charge", Verdict("passed", "k1", Noon, table), [table]));

        return Snapshot(layout);
    }

    /// <summary>Every file of <paramref name="layout"/>'s record of verdicts, by its path, with what it holds.</summary>
    internal static IReadOnlyDictionary<string, string> Snapshot(HarnessLayout layout)
        => Directory.Exists(layout.MutationRecordDirectory)
            ? Directory.EnumerateFiles(layout.MutationRecordDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToDictionary(path => path, File.ReadAllText)
            : new Dictionary<string, string>();

    /// <summary>Each of the arm's entries, oldest first, as its verdict and its key.</summary>
    private IReadOnlyList<(string Verdict, string? Key)> Entries(string arm)
        => [.. Record.Read(Leg).Arms[arm].Select(entry => (entry.Verdict, entry.Key))];

    /// <summary>A table of <paramref name="target"/>, reading <paramref name="file"/> of the tree and nothing else.</summary>
    private static BuiltFromTable Table(string target, string file) => new()
    {
        Target = target,
        Lines = FileContentHash.Of(System.Text.Encoding.UTF8.GetBytes($"the lines of {target}")),
        Files = new Dictionary<string, string>(StringComparer.Ordinal) { [file] = FileContentHash.Of(System.Text.Encoding.UTF8.GetBytes(file)) },
        Fetched = new Dictionary<string, string>(StringComparer.Ordinal),
        Made = new Dictionary<string, FilesIn>(StringComparer.Ordinal) { ["{build}"] = new FilesIn(1, "made") },
        Outside = new Dictionary<string, FilesIn>(StringComparer.Ordinal),
    };

    /// <summary>
    /// A verdict reached in the main checkout at <paramref name="at"/>, judged on <paramref name="tables"/>, with
    /// <paramref name="key"/> - or with none, and why.
    /// </summary>
    private static RecordedVerdict Verdict(string verdict, string? key, DateTimeOffset at, params BuiltFromTable[] tables) => new()
    {
        Verdict = verdict,
        Detail = $"it was {verdict}",
        Run = "20261012-145059-ba433418",
        At = at,
        SweptIn = RepositoryTree.MainCheckout,
        Key = key,
        KeyUnread = key is null ? "a file it reads is gone" : null,
        JudgedOn = new JudgedOn
        {
            Judge = ArmJudge.Revision,
            Rows = new RowDigests(FileContentHash.Of([1]), null, null, null, null),
            Texts = new Dictionary<string, string?>(StringComparer.Ordinal) { ["texts/charge.before"] = FileContentHash.Of([2]) },
            Sites = new Dictionary<string, string?>(StringComparer.Ordinal) { ["src/fixture.cpp"] = null },
            Built = tables.ToDictionary(table => table.Target, table => table.Digest(), StringComparer.Ordinal),
            Compilers = [new JudgedCompiler("CXX", "GNU", "13.3.0", "/usr/bin/c++", [])],
            Settings = new SweepSettings("build", "run", "judge"),
            RunReads = new Dictionary<string, string>(StringComparer.Ordinal) { ["tests/data/golden.txt"] = FileContentHash.Of([4]) },
            Host = "local",
        },
        Causes = new JudgedCauses(new Dictionary<string, string>(StringComparer.Ordinal) { ["CMakeLists.txt"] = FileContentHash.Of([3]) }, "generation"),
    };
}
