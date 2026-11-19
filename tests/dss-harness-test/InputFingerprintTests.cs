using System.Collections.Concurrent;
using System.Diagnostics;
using NSubstitute;
using DssHarness.Core.Execution;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// Edit a file mid-run and some tests see the old one and some the new, and the report describes a
/// tree that never existed. This was measured: eight failures, all passing seconds later on the
/// unchanged tree. These pin what the fingerprints and the watch notice, and what they refuse to
/// call clean.
/// </summary>
public sealed class InputFingerprintTests
{
    private static readonly string[] Inputs = ["config/c.lang.json", "corpus/sample.txt"];

    [Fact]
    public async Task InputsThatHeldStill_CompareUnchanged()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        var comparison = InputFingerprint.Compare(before, after);

        Assert.Equal(InputChange.Unchanged, comparison.Change);
        Assert.Null(comparison.Verdict());
    }

    [Fact]
    public async Task AnInputThatChanged_IsNamed()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        temp.WriteFile("config/c.lang.json", "{\"changed\": true}");
        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        var comparison = InputFingerprint.Compare(before, after);

        Assert.Equal(InputChange.Moved, comparison.Change);
        Assert.Equal(["config/c.lang.json"], comparison.Changed);
        Assert.Equal(LegVerdict.InputsMoved, comparison.Verdict()!.Verdict);
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(comparison.Verdict()!.Verdict));
    }

    /// <summary>
    /// Many inputs that changed are each named where the detail has room for them - never three and a count - and the
    /// verdict carries every one of them whole, for the leg's line to keep in the ledger and --json, where the detail
    /// says the rest are. Forty here, which three names and "37 more" would have stood for.
    /// </summary>
    [Fact]
    public async Task ManyInputsThatChanged_AreNamedWhereTheyFit_AndTheVerdictCarriesThemAll()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        var inputs = Enumerable.Range(0, 40).Select(index => $"in/{index:00}.txt").ToList();

        foreach (var input in inputs)
        {
            temp.WriteFile(input, "before");
        }

        var before = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);

        foreach (var input in inputs)
        {
            temp.WriteFile(input, "after!");
        }

        var after = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);
        var verdict = InputFingerprint.Compare(before, after).Verdict()!;

        Assert.Equal((LegVerdict.InputsMoved, $"40 inputs changed: {string.Join(", ", inputs)}"), (verdict.Verdict, verdict.Detail));
        Assert.Equal(inputs, verdict.Named);

        // Decided among a leg's verdicts, the list goes with the detail that stands for it.
        var decided = new LegGuardReport(InputFingerprint.Compare(before, after), null).Decide("leg", [ReachedVerdict.Of(LegVerdict.Passed)]);

        Assert.Equal((LegVerdict.InputsMoved, inputs), (decided.Verdict, (IReadOnlyList<string>)decided.Named));
    }

    /// <summary>
    /// Inputs past what a detail's line holds are counted, with where every one of them is: the leg's line, which keeps
    /// the verdict's whole list.
    /// </summary>
    [Fact]
    public async Task InputsPastTheLinesRoom_AreCounted_SayingWhereTheyAllAre()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        var wide = new string('w', 200);
        var inputs = Enumerable.Range(0, 30).Select(index => $"in/{wide}{index:00}.txt").ToList();

        foreach (var input in inputs)
        {
            temp.WriteFile(input, "before");
        }

        var before = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);

        foreach (var input in inputs)
        {
            temp.WriteFile(input, "after!");
        }

        var verdict = InputFingerprint.Compare(before, await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken)).Verdict()!;
        // As many as the line's room holds, ", " between two, the first whatever its width.
        var (fits, width) = (1, inputs[0].Length);

        while (fits < inputs.Count && width + 2 + inputs[fits].Length <= DssHarness.Core.Pages.PageRoom.Line)
        {
            width += 2 + inputs[fits].Length;
            fits++;
        }

        Assert.InRange(fits, 2, inputs.Count - 1);

        Assert.Equal(
            $"30 inputs changed: {string.Join(", ", inputs.Take(fits))} and {inputs.Count - fits} more (all in {InputFingerprint.WholeList})",
            verdict.Detail);
        Assert.Equal(inputs, verdict.Named);
    }

    [Fact]
    public async Task AFileOfTheSameSizeWithOtherContent_IsStillAChange()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        temp.WriteFile("corpus/sample.txt", "aaaa");

        var before = await fingerprint.TakeAsync(temp.Path, ["corpus/sample.txt"], TestContext.Current.CancellationToken);
        temp.WriteFile("corpus/sample.txt", "bbbb");
        var after = await fingerprint.TakeAsync(temp.Path, ["corpus/sample.txt"], TestContext.Current.CancellationToken);

        // Content as well as size: a corpus fixture rewritten in place keeps its length.
        Assert.Equal(InputChange.Moved, InputFingerprint.Compare(before, after).Change);
    }

    [Fact]
    public async Task AnInputThatWasNeverThere_IsNotAChange()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();

        var before = await fingerprint.TakeAsync(temp.Path, ["missing.json"], TestContext.Current.CancellationToken);
        var after = await fingerprint.TakeAsync(temp.Path, ["missing.json"], TestContext.Current.CancellationToken);

        // Absence is a measurement, and it compares equal to absence. Unreadability is what leaves
        // the question open.
        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after).Change);
        Assert.Equal(InputFingerprint.AbsentContent, before.Files.Single().Content);
    }

    [Fact]
    public async Task AnInputThatAppearedWhileTheTestsRan_IsAChange()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();

        var before = await fingerprint.TakeAsync(temp.Path, ["generated.json"], TestContext.Current.CancellationToken);
        temp.WriteFile("generated.json", "{}");
        var after = await fingerprint.TakeAsync(temp.Path, ["generated.json"], TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Moved, InputFingerprint.Compare(before, after).Change);
    }

    [Fact]
    public void ASnapshotThatCouldNotBeTaken_IsNeverReportedAsClean()
    {
        var files = new[] { new FileFingerprint("config/c.lang.json", 4, "abcd") };
        var complete = new InputSnapshot(files, []);
        var incomplete = new InputSnapshot(files, [new UnreadableInput("corpus/sample.txt", "the file is held by another process")]);

        var comparison = InputFingerprint.Compare(complete, incomplete);

        Assert.Equal(InputChange.Unmeasured, comparison.Change);
        Assert.Equal(LegVerdict.Unmeasured, comparison.Verdict()!.Verdict);
        Assert.Contains("corpus/sample.txt", comparison.Detail, StringComparison.Ordinal);
        Assert.False(incomplete.Complete);
    }

    [Fact]
    public async Task AnInputThatCannotBeRead_MakesTheSnapshotIncomplete()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses a read while another program holds the file open.");

        using var temp = new TempDirectory();
        var fingerprint = Create();
        var path = temp.WriteFile("corpus/sample.txt", "held");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var snapshot = await fingerprint.TakeAsync(temp.Path, ["corpus/sample.txt"], TestContext.Current.CancellationToken);

            Assert.False(snapshot.Complete);
            Assert.Equal("corpus/sample.txt", snapshot.Unreadable.Single().Path);
            Assert.Empty(snapshot.Files);
        }
    }

    [Fact]
    public async Task AnEditMadeAndUndoneWhileTheTestsRan_IsStillCaught()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        var original = await File.ReadAllTextAsync(temp.Combine("config/c.lang.json"), TestContext.Current.CancellationToken);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        using var watch = Watching(() => fingerprint.Watch(temp.Path, Inputs, before));

        temp.WriteFile("config/c.lang.json", "{\"rewritten\": true}");
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);

        // Put back exactly as it was, which is what makes the two snapshots agree and the whole
        // rewrite invisible without the watch.
        await File.WriteAllTextAsync(temp.Combine("config/c.lang.json"), original, TestContext.Current.CancellationToken);

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after).Change);

        var withWatch = InputFingerprint.Compare(before, after, watch);
        Assert.Equal(InputChange.Moved, withWatch.Change);
        Assert.Contains("config/c.lang.json", withWatch.Changed);
    }

    [Fact]
    public async Task AWatchThatLostItsEvents_IsUnmeasured()
    {
        using var temp = new TempDirectory();
        var kind = new ScriptedWatchKind();
        var factory = new HarnessFactory();
        var fingerprint = new InputFingerprint(factory.FileSystem, factory.Platform, kind);
        Seed(temp);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        using var watch = fingerprint.Watch(temp.Path, Inputs, before);
        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after, watch).Change);

        // What the machine that runs out of watches mid-run says, or one whose buffer of events
        // overflowed: either way the question can no longer be answered.
        kind.Sink!.Lost("too many changes at once in directory");

        Assert.NotNull(watch.Failure);
        Assert.Equal(InputChange.Unmeasured, InputFingerprint.Compare(before, after, watch).Change);
    }

    /// <summary>
    /// A watch that cannot start is refused, and holds nothing: a tree that is not there, or a machine that will not
    /// watch, is known before the work begins, and the work is not to run under a watch that never was.
    /// </summary>
    [Fact]
    public async Task AWatchThatCannotStart_IsRefused_BeforeAnyWorkRunsUnderIt()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        Seed(temp);

        var refusing = new InputFingerprint(factory.FileSystem, factory.Platform, ScriptedWatchKind.Refusing());
        var before = await refusing.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(
            ScriptedWatchKind.Refused,
            Assert.Throws<InputsUnwatchedException>(() => refusing.Watch(temp.Path, Inputs, before)).Message);

        // With this machine's own watcher: a tree that is not there.
        Assert.Equal(
            $"the inputs could not be watched, so none of this work was run: '{temp.Combine("gone")}' is not a directory",
            Assert.Throws<InputsUnwatchedException>(() => Create().Watch(temp.Combine("gone"), Inputs, before)).Message);
    }

    /// <summary>
    /// A directory of inputs moved aside while the work ran, and put back before it ended, counts every input in it: put
    /// back, each reads as it did at both ends, while the work found none of them in between. Heard through the directory
    /// above it - here the root, which holds no input, and is watched for the names of the directories in it.
    /// </summary>
    [Fact]
    public async Task ADirectoryOfInputsMovedAsideAndPutBack_CountsItsInputs()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        string[] inputs = Inputs;

        var before = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);
        using var watch = Watching(() => fingerprint.Watch(temp.Path, inputs, before));

        // Looked at while it is aside, so a watch that can be told of earlier writes finds the file gone.
        Directory.Move(temp.Combine("config"), temp.Combine("config.aside"));
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);
        Directory.Move(temp.Combine("config.aside"), temp.Combine("config"));

        var after = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after).Change);

        var comparison = InputFingerprint.Compare(before, after, watch);
        Assert.Equal(InputChange.Moved, comparison.Change);
        Assert.Equal(["config/c.lang.json"], comparison.Changed);
    }

    /// <summary>
    /// A file renamed while the work ran counts the input at the name it left and the input at the name it took: the
    /// first is gone, and the second holds what it did not.
    /// </summary>
    [Fact]
    public async Task AFileRenamedWhileTheWorkRan_CountsTheInputAtItsOldName_AndTheOneAtItsNew()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        temp.WriteFile("config/old.json", "{}");
        string[] inputs = ["config/old.json", "config/new.json", "config/other.json"];
        temp.WriteFile("config/other.json", "{}");

        var before = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);
        using var watch = Watching(() => fingerprint.Watch(temp.Path, inputs, before));

        File.Move(temp.Combine("config", "old.json"), temp.Combine("config", "new.json"));
        await WaitForAsync(watch, "config/old.json", TestContext.Current.CancellationToken);
        await WaitForAsync(watch, "config/new.json", TestContext.Current.CancellationToken);

        Assert.Equal(["config/new.json", "config/old.json"], watch.Changed);
    }

    /// <summary>
    /// A watch that can be told of a write made before it began - as one on macOS can - counts a file it
    /// is told of only where the file no longer stands as the work found it: told late of the write the
    /// first reading already holds, it counts nothing. A watch that cannot be told of one counts every
    /// report, as it always did.
    /// </summary>
    [Theory]
    [InlineData(true, InputChange.Unchanged)]
    [InlineData(false, InputChange.Moved)]
    public async Task WordOfAWriteTheWorkFoundDone_CountsOnlyWhereTheWatchCannotBeToldOfEarlierWrites(
        bool reportsEarlierWrites,
        InputChange expected)
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        using var watch = Watching(() => new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, Runtime, reportsEarlierWrites, before.Files));

        // What macOS delivers late: word of the write that made the file, after the first reading held it.
        watch.Record(temp.Combine("config/c.lang.json"));

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(expected, InputFingerprint.Compare(before, after, watch).Change);
    }

    /// <summary>
    /// A file moved aside while the work ran, and put back before it ended, still counts where the watch
    /// can be told of earlier writes: put back, it reads as it did at both ends, while the work read
    /// something else in between. Judged by the two readings alone, it passed as clean.
    /// </summary>
    [Fact]
    public async Task AFileMovedAsideAndPutBack_StillCounts_WhereTheWatchCanBeToldOfEarlierWrites()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        var path = temp.Combine("config/c.lang.json");

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        using var watch = Watching(() => new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, Runtime, reportsEarlierWrites: true, before.Files));

        File.Move(path, path + ".aside");
        await File.WriteAllTextAsync(path, "{\"for this run\": true}", TestContext.Current.CancellationToken);
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);
        File.Move(path + ".aside", path, overwrite: true);

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after).Change);

        var comparison = InputFingerprint.Compare(before, after, watch);
        Assert.Equal(InputChange.Moved, comparison.Change);
        Assert.Contains("config/c.lang.json", comparison.Changed);
    }

    /// <summary>
    /// An edit of the same size, made in place and undone while the work ran, still counts where the
    /// watch can be told of earlier writes: the file is the same size and the same file, and was written
    /// since the work found it.
    /// </summary>
    [Fact]
    public async Task AnEditUndoneInPlaceWhileTheWorkRan_StillCounts_WhereTheWatchCanBeToldOfEarlierWrites()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        var path = temp.Combine("config/c.lang.json");
        var original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        // Dated well back, so the edit's own time differs from it however coarse the clock that
        // stamps files is.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        using var watch = Watching(() => new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, Runtime, reportsEarlierWrites: true, before.Files));

        await File.WriteAllTextAsync(path, "{\"rewrite!\": true}", TestContext.Current.CancellationToken);
        Assert.Equal(original.Length, (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Length);
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after).Change);

        var comparison = InputFingerprint.Compare(before, after, watch);
        Assert.Equal(InputChange.Moved, comparison.Change);
        Assert.Contains("config/c.lang.json", comparison.Changed);
    }

    /// <summary>
    /// A file the work found absent has no times to stand as it was found, so word of it counts even
    /// where the watch can be told of earlier writes, and even once it is gone again: made and removed
    /// while the work ran, it may have been read.
    /// </summary>
    [Fact]
    public async Task AFileMadeAndRemovedWhileTheWorkRan_StillCounts_WhereTheWatchCanBeToldOfEarlierWrites()
    {
        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        File.Delete(temp.Combine("corpus/sample.txt"));

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        using var watch = Watching(() => new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, Runtime, reportsEarlierWrites: true, before.Files));

        // Word of the file, looked at only once it has gone again: absent then and absent now.
        watch.Record(temp.Combine("corpus/sample.txt"));

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        var comparison = InputFingerprint.Compare(before, after, watch);
        Assert.Equal(InputChange.Moved, comparison.Change);
        Assert.Contains("corpus/sample.txt", comparison.Changed);
    }

    /// <summary>
    /// A file replaced while the work ran by one holding the same bytes and dated as it was - as a copy
    /// that keeps times puts one back - still counts where the watch can be told of earlier writes: the
    /// file in its place was created since.
    /// </summary>
    [Fact]
    public async Task AFileReplacedByItsOwnBytesAndTime_StillCounts_WhereTheWatchCanBeToldOfEarlierWrites()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "NTFS gives a file renamed into a name the creation time of the file that name last held.");

        using var temp = new TempDirectory();
        var fingerprint = Create();
        Seed(temp);
        var path = temp.Combine("config/c.lang.json");
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var written = File.GetLastWriteTimeUtc(path);

        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);
        using var watch = Watching(() => new InputWatch(temp.Path, Inputs, StringComparer.Ordinal, Runtime, reportsEarlierWrites: true, before.Files));

        // Made a while after the original, so a coarse clock that stamps files still tells the two
        // apart; then dated as the original and moved over it.
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        var copy = temp.Combine("config/c.lang.json.copy");
        await File.WriteAllBytesAsync(copy, bytes, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(copy, written);

        if (File.GetCreationTimeUtc(copy) <= written)
        {
            Assert.Skip("The runtime reads no time of creation here, and gives the file's earliest other time instead.");
        }

        File.Move(copy, path, overwrite: true);
        await WaitForAsync(watch, "config/c.lang.json", TestContext.Current.CancellationToken);

        var after = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        Assert.Equal(
            before.Files.Single(file => file.Path == "config/c.lang.json").Written,
            after.Files.Single(file => file.Path == "config/c.lang.json").Written);
        Assert.Equal(InputChange.Moved, InputFingerprint.Compare(before, after, watch).Change);
    }

    /// <summary>Only a watch on macOS can be told of a write made before it began.</summary>
    [Theory]
    [InlineData(PlatformId.MacOs, true)]
    [InlineData(PlatformId.Linux, false)]
    [InlineData(PlatformId.Windows, false)]
    public async Task OnlyAWatchOnMacOs_LooksAtWhatItIsToldOfAgainstWhatTheWorkFound(PlatformId platform, bool looks)
    {
        using var temp = new TempDirectory();
        Seed(temp);

        var host = Substitute.For<IHostPlatform>();
        host.Current.Returns(platform);
        host.PathComparison.Returns(new HostPlatform().PathComparison);

        var fingerprint = new InputFingerprint(new HarnessFactory().FileSystem, host, new ScriptedWatchKind());
        var before = await fingerprint.TakeAsync(temp.Path, Inputs, TestContext.Current.CancellationToken);

        using var watch = fingerprint.Watch(temp.Path, Inputs, before);

        Assert.Equal(looks, watch.ReportsEarlierWrites);

        // And looks at it against the readings it was handed: late word of a file standing as it was
        // found counts on macOS for nothing, and elsewhere as every word does.
        watch.Record(temp.Combine("config/c.lang.json"));
        Assert.Equal(looks ? Array.Empty<string>() : ["config/c.lang.json"], watch.Changed);
    }

    /// <summary>
    /// A watch goes as deep as the inputs: a directory holding inputs is watched for its own files only, so a directory
    /// tracked for one placeholder costs one shallow watch - the worktrees root, whose agents' builds overflowed a watch
    /// taken all the way down and voided a main-tree run that only read its own inputs.
    /// </summary>
    [Fact]
    public void AWatch_GoesAsDeepAsTheInputs()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".worktrees/.gitkeep", string.Empty);
        temp.WriteFile(".worktrees/o1/ag/build/obj.o", "an agent's build");
        temp.WriteFile("src/a/x.cpp", "x");
        temp.WriteFile("src/b/y.cpp", "y");
        temp.WriteFile("README.md", "readme");

        using var watch = new InputWatch(temp.Path, [".worktrees/.gitkeep", "src/a/x.cpp", "src/b/y.cpp", "README.md"], StringComparer.Ordinal, new ScriptedWatchKind());

        Assert.Null(watch.Failure);
        Assert.Equal(
            [WatchTarget.Entries(temp.Path), WatchTarget.Entries(temp.Combine(".worktrees")), WatchTarget.NamesOnly(temp.Combine("src")), WatchTarget.Entries(temp.Combine("src", "a")), WatchTarget.Entries(temp.Combine("src", "b"))],
            watch.Watched);
    }

    /// <summary>
    /// A watch never goes into what a tree keeps in its slots: a slot tracked for its placeholder alone is watched for its
    /// own entries, and with a real watch a sweep's worker building there, or a leg's copy written there, is never heard,
    /// so the leg's own work is never voided for a copy of it.
    /// </summary>
    [Fact]
    public async Task AWatch_NeverGoesIntoTheCopiesATreeKeepsInItsSlots()
    {
        using var temp = new TempDirectory();
        string[] inputs = [".m/.gitkeep", ".legs/.gitkeep", "src/a.c"];

        foreach (var input in inputs)
        {
            temp.WriteFile(input, string.Empty);
        }

        Directory.CreateDirectory(temp.Combine(".m", "357e24cw-1", "build"));
        Directory.CreateDirectory(temp.Combine(".legs", "357e24c", "build"));

        using (var scripted = new InputWatch(temp.Path, inputs, StringComparer.Ordinal, new ScriptedWatchKind()))
        {
            Assert.Equal(
                [WatchTarget.NamesOnly(temp.Path), WatchTarget.Entries(temp.Combine(".legs")), WatchTarget.Entries(temp.Combine(".m")), WatchTarget.Entries(temp.Combine("src"))],
                scripted.Watched);
        }

        var fingerprint = Create();
        var before = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);
        var heard = new ConcurrentQueue<string>();

        using var watch = Watching(() => fingerprint.Watch(temp.Path, inputs, before, heard.Enqueue));

        temp.WriteFile(".m/357e24cw-1/build/357e24c/obj.o", "a worker's build");
        temp.WriteFile(".legs/357e24c/build/obj.o", "a leg's copy's build");
        temp.WriteFile("src/a.c", "edited");
        await WaitForAsync(watch, "src/a.c", TestContext.Current.CancellationToken);

        var after = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);

        // Heard in order: word of either write, had the watch been told of it, came before word of the edit after both.
        Assert.DoesNotContain(
            heard,
            path => path.StartsWith(".m/357e24cw-1/", StringComparison.Ordinal) || path.StartsWith(".legs/357e24c/", StringComparison.Ordinal));
        Assert.Equal(["src/a.c"], watch.Changed);
        Assert.Equal(InputChange.Moved, InputFingerprint.Compare(before, after, watch).Change);
    }

    /// <summary>
    /// Where the tree's names fold case, a directory above inputs spelt two ways by them is one directory, watched once:
    /// what lies above an input is told by the comparer the inputs are, never by their spelling alone.
    /// </summary>
    [Fact]
    public void ADirectoryTheInputsSpellTwoWays_WhereNamesFoldCase_IsWatchedOnce()
    {
        using var temp = new TempDirectory();
        string[] inputs = ["Src/x/a.cpp", "src/y/b.cpp"];

        foreach (var input in inputs)
        {
            temp.WriteFile(input, "f");
        }

        using var watch = new InputWatch(temp.Path, inputs, StringComparer.OrdinalIgnoreCase, new ScriptedWatchKind());

        Assert.Single(watch.Watched, target => string.Equals(target.Directory, temp.Combine("src"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, watch.Watched.Count);
    }

    /// <summary>
    /// Through a kind that watches a tree through one watch of its root - .NET's watcher, on Windows and macOS - inputs in
    /// forty thousand directories are watched by that one watch, all the way down, and nothing else: no directory is
    /// left out for a number of watches the tool once allowed itself, past which top-level directories were taken whole.
    /// </summary>
    [Fact]
    public void FortyThousandInputDirectories_AreWatched()
    {
        using var temp = new TempDirectory();
        var inputs = Enumerable.Range(0, 40_000).Select(index => $"t{index % 4}/d{index:00000}/f.cpp").ToList();
        var kind = new ScriptedWatchKind(wholeRoot: true);

        using var watch = new InputWatch(temp.Path, inputs, StringComparer.Ordinal, kind);

        Assert.Null(watch.Failure);
        Assert.Equal([WatchTarget.AllTheWayDown(temp.Path)], watch.Watched);

        // What it hears counts where it names an input, however deep, and nowhere else.
        kind.Sink!.Written(temp.Combine("t3", "d39999", "f.cpp"));
        kind.Sink.Written(temp.Combine("build", "f.o"));
        Assert.Equal(["t3/d39999/f.cpp"], watch.Changed);
    }

    /// <summary>
    /// Events lost to an overflow while the watch goes on have every input looked at again against how the work found it:
    /// one written since, one gone and one arrived count, one that stands as found does not, and the span stays answered
    /// for - a build below the root writing faster than a watch of the root is read once made the work unmeasured.
    /// </summary>
    [Fact]
    public async Task AnOverflow_HasEveryInputLookedAtAgain_CountingThoseThatNoLongerStandAsFound()
    {
        using var temp = new TempDirectory();
        string[] inputs = ["kept.txt", "written.txt", "gone.txt", "arrived.txt"];
        var kind = new ScriptedWatchKind(wholeRoot: true);
        var factory = new HarnessFactory();
        var fingerprint = new InputFingerprint(factory.FileSystem, factory.Platform, kind);

        temp.WriteFile("kept.txt", "kept");
        temp.WriteFile("written.txt", "first");
        temp.WriteFile("gone.txt", "gone");

        var before = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);
        using var watch = fingerprint.Watch(temp.Path, inputs, before);

        temp.WriteFile("written.txt", "written again");
        File.Delete(temp.Combine("gone.txt"));
        temp.WriteFile("arrived.txt", "arrived");

        kind.Sink!.Overflowed("Too many changes at once in directory.");

        Assert.Null(watch.Failure);
        Assert.Equal(["arrived.txt", "gone.txt", "written.txt"], watch.Changed);
    }

    /// <summary>
    /// An overflow that leaves an input with no record of how the work found it - one that could not be read then - leaves
    /// the span unanswered for, saying what overflowed and which input could not be looked at again, never passed over as
    /// standing still.
    /// </summary>
    [Fact]
    public void AnOverflow_WithAnInputThatCannotBeLookedAtAgain_LeavesTheSpanUnansweredFor()
    {
        using var temp = new TempDirectory();
        var kind = new ScriptedWatchKind(wholeRoot: true);

        using var watch = new InputWatch(temp.Path, ["unread.txt"], StringComparer.Ordinal, kind);

        kind.Sink!.Overflowed("Too many changes at once in directory.");

        Assert.Equal("Too many changes at once in directory., and the input 'unread.txt' cannot be looked at again against how the work found it", watch.Failure);
        Assert.Empty(watch.Changed);
    }

    /// <summary>
    /// With a real watch, a write below a directory only a placeholder of it is tracked in is never heard - an agent's build
    /// below the worktrees root - while a write beside the placeholder is, and an edit of the placeholder made and undone is
    /// still caught.
    /// </summary>
    [Fact]
    public async Task AWriteBelowAnUntrackedSubtree_IsNotHeard_WhileAnEditOfTheTrackedFileIsCaught()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(".worktrees/.gitkeep", string.Empty);
        Directory.CreateDirectory(temp.Combine(".worktrees", "o1", "ag", "build"));
        string[] inputs = [".worktrees/.gitkeep"];
        var fingerprint = Create();
        var before = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);

        var heard = new ConcurrentQueue<string>();

        using var watch = Watching(() => fingerprint.Watch(temp.Path, inputs, before, heard.Enqueue));

        temp.WriteFile(".worktrees/o1/ag/build/obj.o", "an agent's build");
        temp.WriteFile(".worktrees/notes.txt", "beside the placeholder");
        temp.WriteFile(".worktrees/.gitkeep", "edited");
        await WaitForAsync(watch, ".worktrees/.gitkeep", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(temp.Combine(".worktrees", ".gitkeep"), string.Empty, TestContext.Current.CancellationToken);

        var after = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);

        // Heard in order: word of either write, had the watch been told of it, came before word of the edit after both.
        Assert.Equal([".worktrees/notes.txt"], heard.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Equal(InputChange.Moved, InputFingerprint.Compare(before, after, watch).Change);
    }

    /// <summary>
    /// With a real watch, inputs that lie only deep in the tree - no input above them - count every one of them where the
    /// directory they are in, or a directory above it, is moved aside and put back while the work runs.
    /// </summary>
    [Theory]
    [InlineData("tests/unit")]
    [InlineData("tests")]
    public async Task ADirectoryAboveDeepInputsMovedAsideAndPutBack_CountsThem_ThoughNoInputLiesAboveThem(string moved)
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() && moved != "tests/unit",
            "Windows refuses to rename a directory while a handle is open below it - the watch's own, on the directory of "
            + "inputs - so there a directory above a watched one cannot move while the work runs.");

        using var temp = new TempDirectory();
        string[] inputs = ["tests/unit/a.cpp", "tests/unit/b.cpp"];

        foreach (var input in inputs)
        {
            temp.WriteFile(input, input);
        }

        var fingerprint = Create();
        var before = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);
        using var watch = Watching(() => fingerprint.Watch(temp.Path, inputs, before));
        var path = temp.Combine(moved.Split('/'));

        Directory.Move(path, path + ".aside");
        await WaitForAsync(watch, "tests/unit/b.cpp", TestContext.Current.CancellationToken);
        Directory.Move(path + ".aside", path);

        var after = await fingerprint.TakeAsync(temp.Path, inputs, TestContext.Current.CancellationToken);

        Assert.Equal(InputChange.Unchanged, InputFingerprint.Compare(before, after).Change);
        Assert.Equal(inputs, InputFingerprint.Compare(before, after, watch).Changed);
    }

    /// <summary>The runtime's own watcher, which the tests of a real watch run under.</summary>
    private static readonly FileSystemWatcherKind Runtime = new();

    private static InputFingerprint Create()
    {
        var factory = new HarnessFactory();
        return new InputFingerprint(factory.FileSystem, factory.Platform, factory.Output);
    }

    /// <summary>
    /// The watch a test of a real one runs under, or the test skipped where this machine cannot watch the tree: a
    /// machine that cannot refuses the watch, and what these pin is what a watch that started sees.
    /// </summary>
    private static InputWatch Watching(Func<InputWatch> start)
    {
        try
        {
            return start();
        }
        catch (InputsUnwatchedException ex)
        {
            Assert.Skip($"This machine cannot watch the tree: {ex.Why}");
            throw;
        }
    }

    private static void Seed(TempDirectory temp)
    {
        temp.WriteFile("config/c.lang.json", "{\"original\": true}");
        temp.WriteFile("corpus/sample.txt", "sample");
    }

    /// <summary>
    /// Waits for the watch to notice <paramref name="path"/>. File system notifications are
    /// delivered by the operating system when it gets to them, so the wait is generous and its
    /// failure is a real one: an edit that was never noticed is the failure this exists to catch.
    /// </summary>
    private static async Task WaitForAsync(InputWatch watch, string path, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();

        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (watch.Changed.Contains(path))
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"The watch never noticed '{path}' changing.");
    }
}
