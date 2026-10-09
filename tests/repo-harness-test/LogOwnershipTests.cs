using System.Globalization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>
/// A run that cannot own its log path cannot keep the evidence for its own verdict. These pin that
/// a live owner is refused as <c>log-held</c>, distinct from a held lock, and that an owner whose
/// process has gone is reclaimed rather than waited for.
/// </summary>
public sealed class LogOwnershipTests
{
    [Fact]
    public async Task AFreeLogPath_IsClaimed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var runId = RunId.New();
        var directory = temp.Combine("runs", runId.Value);

        var claim = await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(claim.Taken);
        Assert.Null(claim.HeldBy);
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);

        // Beside the directory, not inside it: wiping a run directory must not quietly free a path
        // a live run still owns.
        Assert.True(File.Exists(LogOwnership.OwnerFile(directory)));
        Assert.EndsWith(LogOwnership.OwnerSuffix, LogOwnership.OwnerFile(directory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALogPathOwnedByALiveRun_IsHeld()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "shared");

        // Owned by a run of this very process, which is alive by definition.
        Write(directory, Environment.MachineName, Environment.ProcessId, ProcessStart(), "20250101-120000-deadbeef");

        var claim = await ownership.ClaimAsync(directory, RunId.New(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(claim.Taken);
        Assert.Contains("20250101-120000-deadbeef", claim.HeldBy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The only way out of an owner this machine will not reclaim on its own. Without it a stuck
    /// <c>.owner.json</c> can only be deleted by hand, which is the kind of manual step this tool
    /// exists to remove.
    /// </summary>
    [Fact]
    public async Task ALogPathOwnedByALiveRun_CanBeTakenWhenItIsForced()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "stuck");
        var runId = RunId.New();

        Write(directory, Environment.MachineName, Environment.ProcessId, ProcessStart(), "20250101-120000-deadbeef");

        var claim = await ownership.ClaimAsync(directory, runId, force: true, TestContext.Current.CancellationToken);

        Assert.True(claim.Taken);
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);
        Assert.Contains("--force-lock was given", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same upgrade for a log path: an owner file written before the stamp existed can only be
    /// judged by whether anything carries its id, and says so rather than looking like a live run.
    /// </summary>
    [Fact]
    public async Task AnOwnerFromAnOlderBuild_IsHeldWhileItsIdIsCarried_AndSaysWhy()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "from-before");

        WriteLegacy(directory, Environment.MachineName, Environment.ProcessId);

        var claim = await ownership.ClaimAsync(directory, RunId.New(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(claim.Taken);
        Assert.Contains("recorded by an older build", claim.HeldBy, StringComparison.Ordinal);

        // And the way out is the one the message names.
        var forced = await ownership.ClaimAsync(
            directory, RunId.New(), force: true, TestContext.Current.CancellationToken);

        Assert.True(forced.Taken);
    }

    [Fact]
    public async Task ALogPathOwnedByARunThatHasGone_IsReclaimed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "abandoned");
        var runId = RunId.New();

        Write(directory, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone", "20250101-120000-deadbeef");

        var claim = await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(claim.Taken);
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);
        Assert.Contains("Reclaimed", factory.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunGivesUpOnlyItsOwnLogPath()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "owned");
        var mine = RunId.New();
        var other = RunId.New();

        await ownership.ClaimAsync(directory, mine, cancellationToken: TestContext.Current.CancellationToken);

        // Another run's release must leave the owner alone, or two runs end up writing one set of logs.
        await ownership.ReleaseAsync(directory, other, TestContext.Current.CancellationToken);
        Assert.Equal(mine.Value, ownership.Owner(directory)!.RunId);

        await ownership.ReleaseAsync(directory, mine, TestContext.Current.CancellationToken);
        Assert.Null(ownership.Owner(directory));
    }

    /// <summary>
    /// A log path that could not be given up is said, and never stands in for what the run found:
    /// the run is over by then, and the record is reclaimed once it has ended.
    /// </summary>
    [Fact]
    public async Task ALogPathThatCouldNotBeGivenUp_IsSaid_AndNotRaised()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "unreadable");
        var runId = RunId.New();

        await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);

        // The record no longer readable by the time the run gives it up.
        await File.WriteAllTextAsync(LogOwnership.OwnerFile(directory), "not an owner", TestContext.Current.CancellationToken);

        await ownership.ReleaseAsync(directory, runId, TestContext.Current.CancellationToken);

        Assert.Contains("could not be given up", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A log path that cannot be claimed - its directory cannot be made, or its owner file cannot be
    /// written - is a refusal naming the file, never an error that reads as a defect in this tool.
    /// The claim is the first thing a run writes, so this is where a runs directory an earlier run
    /// under sudo left to root is met.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALogPathThatCannotBeClaimed_IsARefusal_NamingTheOwnerFile(bool directoryIsAFile)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "blocked");

        if (directoryIsAFile)
        {
            temp.WriteFile("runs", "not a directory");
        }
        else
        {
            // The owner file replaced by a directory: nothing can be read, nor written, where it goes.
            Directory.CreateDirectory(LogOwnership.OwnerFile(directory));
        }

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => ownership.ClaimAsync(
            directory,
            RunId.New(),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("The log owner file", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(directoryIsAFile ? "could not be written" : "could not be read", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A log path owned by a run on another machine is held - nothing here can ask that machine whether the run still
    /// goes - saying --force-lock is the way out, and taken when forced: each line saying the owner is another machine's,
    /// and never naming that machine, which only the record keeps.
    /// </summary>
    [Fact]
    public async Task ALogPathOwnedOnAnotherMachine_IsHeld_UntilForced()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "shared");

        Write(directory, "another-machine", int.MaxValue - 1, "a-process-elsewhere", "20250101-120000-deadbeef");

        var held = await ownership.ClaimAsync(directory, RunId.New(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(held.Taken);
        Assert.StartsWith($"pid {int.MaxValue - 1} on another machine, run 20250101-120000-deadbeef, since ", held.HeldBy, StringComparison.Ordinal);
        Assert.Contains("--force-lock takes it", held.HeldBy, StringComparison.Ordinal);
        Assert.DoesNotContain("another-machine", held.HeldBy, StringComparison.Ordinal);

        var forced = await ownership.ClaimAsync(directory, RunId.New(), force: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(forced.Taken);
        Assert.Contains($"from pid {int.MaxValue - 1} on another machine, run 20250101-120000-deadbeef", factory.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Contains("because --force-lock was given", factory.StandardError.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("another-machine", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunReclaimsItsOwnLogPath()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "again");
        var runId = RunId.New();

        await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);
        var second = await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(second.Taken);
    }

    /// <summary>
    /// A run that died holding its own log path - killed, or stopped with its machine - wrote no verdict, and nothing
    /// claims its path again, every run having its own. The next run beside it says it was abandoned - its run id, its
    /// process, when it began and where its records are, or that they are gone - and releases its claim.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARunThatDiedHoldingItsLogPath_IsSaidAbandoned_AndReleased_ByTheNextRunBesideIt(bool recordsKept)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var dead = temp.Combine("runs", "20250101-120000-deadbeef");
        var directory = temp.Combine("runs", "20250102-120000-0badf00d");
        var runId = RunId.New();

        Write(dead, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone", "20250101-120000-deadbeef");

        if (recordsKept)
        {
            Directory.CreateDirectory(dead);
        }

        await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);
        var abandoned = ownership.ReleaseAbandoned(directory);

        Assert.Equal(["20250101-120000-deadbeef"], abandoned.Select(owner => owner.RunId));
        Assert.False(File.Exists(LogOwnership.OwnerFile(dead)));
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);

        var said = factory.StandardError.ToString();
        Assert.Contains(
            $"An earlier run was abandoned: pid {int.MaxValue - 1}, run 20250101-120000-deadbeef, since ",
            said,
            StringComparison.Ordinal);
        Assert.Contains(recordsKept ? $"never gave up its records at '{dead}' - most likely it was killed" : $"never gave up '{dead}', which is gone", said, StringComparison.Ordinal);
        Assert.Contains("so its verdict may never have been reported. Its claim is released.", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a claim this machine can judge is released beside a run: a live run's stands, as one recorded on another
    /// machine does, unsaid, and one this build cannot read is left as it is and said, naming why, since nothing else
    /// would ever say a run its machine left unreadable. None stops the run that found it.
    /// </summary>
    [Fact]
    public async Task AClaimBesideARunThatMayStillStand_IsLeftAsItIs()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var live = temp.Combine("runs", "live");
        var elsewhere = temp.Combine("runs", "elsewhere");
        var unreadable = temp.Combine("runs", "unreadable");
        var directory = temp.Combine("runs", "mine");
        var runId = RunId.New();

        Write(live, Environment.MachineName, Environment.ProcessId, ProcessStart(), "20250101-120000-11111111");
        Write(elsewhere, "another-machine", int.MaxValue - 1, "a-process-that-has-gone", "20250101-120000-22222222");
        Write(unreadable, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone", "20250101-120000-33333333");

        var file = LogOwnership.OwnerFile(unreadable);
        await File.WriteAllTextAsync(
            file,
            (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Replace(
                "\"runId\"",
                "\"somethingNobodyDeclared\": 1, \"runId\"",
                StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(ownership.ReleaseAbandoned(directory));
        Assert.All([live, elsewhere, unreadable, directory], path => Assert.True(File.Exists(LogOwnership.OwnerFile(path)), path));

        var said = factory.StandardError.ToString() + factory.StandardOutput.ToString();
        Assert.Contains(
            $"logs: WARN - Whether the run that claimed '{unreadable}' was abandoned could not be judged, so its claim is left as it is: "
            + $"The log owner file '{file}' could not be read: ",
            said,
            StringComparison.Ordinal);
        Assert.DoesNotContain("An earlier run was abandoned", said, StringComparison.Ordinal);
        Assert.DoesNotContain(live, said, StringComparison.Ordinal);
        Assert.DoesNotContain(elsewhere, said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each claim beside a run is judged on its own: one that cannot be read, sorting first, stops nothing after it, and an
    /// abandoned run whose owner file cannot be removed is said all the same - naming the file and why - while the next
    /// is released. This run's own claim stands throughout.
    /// </summary>
    [Fact]
    public async Task OneClaimThatCannotBeJudgedOrRemoved_StopsNoOtherBesideIt()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var first = temp.Combine("runs", "a");
        var stuck = temp.Combine("runs", "b");
        var released = temp.Combine("runs", "c");
        var directory = temp.Combine("runs", "mine");
        var ownership = new LogOwnership(
            new UndeletableFile(factory.FileSystem, LogOwnership.OwnerFile(stuck)),
            factory.Output,
            factory.Identity);
        var runId = RunId.New();

        Write(first, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone", "20250101-120000-aaaaaaaa");
        Write(stuck, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone", "20250101-120000-bbbbbbbb");
        Write(released, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone", "20250101-120000-cccccccc");
        await File.WriteAllTextAsync(LogOwnership.OwnerFile(first), "{ \"runId\": ", TestContext.Current.CancellationToken);

        await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);
        var abandoned = ownership.ReleaseAbandoned(directory);

        Assert.Equal(["20250101-120000-bbbbbbbb", "20250101-120000-cccccccc"], abandoned.Select(owner => owner.RunId));
        Assert.True(File.Exists(LogOwnership.OwnerFile(first)));
        Assert.True(File.Exists(LogOwnership.OwnerFile(stuck)));
        Assert.False(File.Exists(LogOwnership.OwnerFile(released)));
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);

        var said = factory.StandardError.ToString();
        Assert.Contains($"Whether the run that claimed '{first}' was abandoned could not be judged", said, StringComparison.Ordinal);
        Assert.Contains(
            $"run 20250101-120000-bbbbbbbb, since ",
            said,
            StringComparison.Ordinal);
        Assert.Contains($"Its owner file '{LogOwnership.OwnerFile(stuck)}' could not be removed: Access to the path is denied.", said, StringComparison.Ordinal);
        Assert.Contains($"never gave up '{released}', which is gone - most likely it was killed", said, StringComparison.Ordinal);
        Assert.Contains("Its claim is released.", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// A runs directory that cannot be listed - a share that dropped, or one this user may not read - is said, and stops
    /// nothing: no run beside it is said abandoned or released, and this run keeps its own claim.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunsDirectoryThatCannotBeListed_IsSaid_AndReleasesNothing(bool denied)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var runs = temp.Combine("runs");
        var ownership = new LogOwnership(
            new UnlistableDirectory(
                factory.FileSystem,
                runs,
                () => denied ? new UnauthorizedAccessException("Access is denied.") : new IOException("The network path was not found.")),
            factory.Output,
            factory.Identity);
        var dead = Path.Combine(runs, "20250101-120000-deadbeef");
        var directory = Path.Combine(runs, "20250102-120000-0badf00d");
        var runId = RunId.New();

        Write(dead, Environment.MachineName, int.MaxValue - 1, "a-process-that-has-gone", "20250101-120000-deadbeef");
        await ownership.ClaimAsync(directory, runId, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(ownership.ReleaseAbandoned(directory));
        Assert.True(File.Exists(LogOwnership.OwnerFile(dead)));
        Assert.Equal(runId.Value, ownership.Owner(directory)!.RunId);
        Assert.Contains(
            $"logs: WARN - The runs beside '{directory}' could not be listed: {(denied ? "Access is denied" : "The network path was not found")}. "
            + "Any of them that was abandoned is said, and released, by a later run.",
            factory.StandardError.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>This process's own stamp, which is what makes an owner written with it a live one.</summary>
    private static string? ProcessStart() => new ProcessIdentity(new HostPlatform()).Current;

    /// <summary>One owner file exactly as the release before the process stamp wrote it.</summary>
    private static void WriteLegacy(string logDirectory, string machine, int processId)
    {
        var file = LogOwnership.OwnerFile(logDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var stamp = DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture);

        File.WriteAllText(
            file,
            "{ \"machine\": \"" + machine + "\", \"processId\": " + processId.ToString(CultureInfo.InvariantCulture)
            + ", \"processStartedUtc\": \"" + stamp + "\", \"runId\": \"20250101-120000-deadbeef\""
            + ", \"takenUtc\": \"" + stamp + "\" }");
    }

    private static void Write(string logDirectory, string machine, int processId, string? processStamp, string runId)
    {
        var file = LogOwnership.OwnerFile(logDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var taken = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var stampField = processStamp is null ? string.Empty : "\"processStamp\": \"" + processStamp + "\", ";

        File.WriteAllText(
            file,
            "{ \"machine\": \"" + machine + "\", \"processId\": " + processId.ToString(CultureInfo.InvariantCulture)
            + ", " + stampField + "\"runId\": \"" + runId
            + "\", \"takenUtc\": \"" + taken + "\" }");
    }
    /// <summary>
    /// The owner file was hardened alongside the lock file, and needs the same proof. A member this
    /// build does not know means the file was written by one that did, and reading the rest of it
    /// while dropping that member decides who owns a log directory from a partial record.
    /// </summary>
    [Fact]
    public async Task AnOwnerFileWithAMemberThisBuildDoesNotKnow_IsRefused_RatherThanPartlyRead()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var ownership = new LogOwnership(factory.FileSystem, factory.Output, factory.Identity);
        var directory = temp.Combine("runs", "shared");

        Write(directory, Environment.MachineName, Environment.ProcessId, ProcessStart(), "20250101-120000-deadbeef");

        var file = LogOwnership.OwnerFile(directory);

        await File.WriteAllTextAsync(
            file,
            (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).Replace(
                "\"runId\"",
                "\"somethingNobodyDeclared\": 1, \"runId\"",
                StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => ownership.ClaimAsync(
            directory,
            RunId.New(),
            cancellationToken: TestContext.Current.CancellationToken));

        // The parser's reason is joined into the sentence, not closed twice.
        Assert.DoesNotContain("..", refusal.Message, StringComparison.Ordinal);
    }
}
