using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The record of what a step's worktree held untracked as the step's run began, which tells the step's own making apart:
/// taken of git's listing on a real repository as the step first starts, kept in its directory, read by every later
/// start, taken only where the step's writes narrow a worktree it writes, and refused where it does not read.
/// </summary>
public sealed class AgentStepStartTests : IDisposable
{
    private readonly HarnessFactory _harness = new();
    private readonly TempDirectory _temp = new();
    private readonly string _tree;
    private readonly string _step;

    public AgentStepStartTests()
    {
        _tree = Directory.CreateDirectory(_temp.Combine("tree")).FullName;
        _step = _temp.Combine("run", "step");
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A record holds each of its paths in any case, and every directory above one - so a delete of a directory that held
    /// an untracked path as the step began, or of the path by another spelling a file system folds to it, is never taken
    /// for the step's own making - and nothing else: no prefix of a name, no path beneath a file, no name met elsewhere.
    /// </summary>
    [Fact]
    public void ARecord_HoldsEachPathInAnyCase_AndEveryDirectoryAboveIt()
    {
        var start = new AgentStepStart(_tree, ["obj/deep/a.o", "b.o"]);

        Assert.Equal(["b.o", "obj/deep/a.o"], start.Untracked);
        Assert.Equal(_tree, start.Worktree);

        foreach (var held in new[] { "b.o", "B.O", "obj/deep/a.o", "OBJ/Deep/A.O", "obj", "obj/deep", "Obj/DEEP" })
        {
            Assert.True(start.Holds(held), held);
        }

        foreach (var other in new[] { "a.o", "ob", "obj/dee", "obj/deep/a", "obj/other", "b.o/x", "deep", "deep/a.o" })
        {
            Assert.False(start.Holds(other), other);
        }
    }

    /// <summary>
    /// The record is taken as the step first starts: every file git lists untracked in the worktree - none it tracks, none
    /// it ignores - written in the step's directory, a head and a path to a line; and every later start of the step reads
    /// that record, so a path its commands made after the first start is never taken for one that was there.
    /// </summary>
    [Fact]
    public async Task TheRecord_IsTakenAsTheStepFirstStarts_AndEveryLaterStartReadsIt()
    {
        await _harness.InitializeGitRepositoryAsync(_tree, Token);
        File.WriteAllText(Path.Combine(_tree, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(_tree, "Makefile"), "all:\n");
        await _harness.CommitAllAsync(_tree, "the tracked files", Token);
        Directory.CreateDirectory(Path.Combine(_tree, "obj"));
        File.WriteAllText(Path.Combine(_tree, "obj", "old.o"), "old");
        File.WriteAllText(Path.Combine(_tree, "before.o"), "before");
        File.WriteAllText(Path.Combine(_tree, "build.log"), "ignored");
        File.WriteAllText(Path.Combine(_tree, "Makefile"), "all: changed\n");

        var first = await TakeAsync(Ground(writes: ["Makefile"]), _tree);

        Assert.Equal(["before.o", "obj/old.o"], first!.Untracked);
        Assert.Equal(_tree, first.Worktree);
        Assert.Equal(["{\"paths\":2}", "\"before.o\"", "\"obj/old.o\""], File.ReadAllLines(Path.Combine(_step, OrchestratorLayout.StepUntrackedFileName)));

        // What the step's commands make after its first start - and what they take away - leaves the record as it was.
        File.WriteAllText(Path.Combine(_tree, "calc.o"), "made");
        File.Delete(Path.Combine(_tree, "before.o"));

        var later = await TakeAsync(Ground(writes: ["Makefile"]), _tree);

        Assert.Equal(["before.o", "obj/old.o"], later!.Untracked);
        Assert.False(later.Holds("calc.o"));
        Assert.Equal(["{\"paths\":2}", "\"before.o\"", "\"obj/old.o\""], File.ReadAllLines(Path.Combine(_step, OrchestratorLayout.StepUntrackedFileName)));
    }

    /// <summary>
    /// No record is taken, and nothing is written, for a step whose writes narrow nothing - none of its deletes is narrowed
    /// - nor for one that writes no worktree, its agent having none or its worktree only read: no delete of its reaches one.
    /// </summary>
    [Fact]
    public async Task NoRecord_IsTaken_WhereTheStepsWritesNarrowNoWorktreeItWrites()
    {
        await _harness.InitializeGitRepositoryAsync(_tree, Token);
        File.WriteAllText(Path.Combine(_tree, "before.o"), "before");

        Assert.Null(await TakeAsync(Ground(writes: null), _tree));
        Assert.Null(await TakeAsync(Ground(writes: ["Makefile"]), worktree: null));
        Assert.Null(await TakeAsync(Ground(writes: ["Makefile"], writable: [_temp.Combine("work")]), _tree));
        Assert.False(Directory.Exists(_step));
    }

    /// <summary>
    /// A record kept that does not read - cut short, counting fewer than no paths, going on past its last - is refused
    /// naming the file and why, never guessed at and never taken again over what the step's commands have made since.
    /// </summary>
    [Fact]
    public async Task ARecordThatDoesNotRead_IsRefused_NeverGuessedAt()
    {
        await _harness.InitializeGitRepositoryAsync(_tree, Token);
        var file = Path.Combine(_step, OrchestratorLayout.StepUntrackedFileName);
        Directory.CreateDirectory(_step);

        foreach (var (written, why) in new[]
        {
            ("{\"paths\":2}\n\"a.o\"\n", "it ends after line 2, where a path was to be"),
            ("{\"paths\":-1}\n", "its head counts fewer than no paths"),
            ("{\"paths\":1}\n\"a.o\"\n\"b.o\"\n", "it goes on past line 2, where it was to end"),
        })
        {
            File.WriteAllText(file, written);

            var refused = await Assert.ThrowsAsync<HarnessException>(() => TakeAsync(Ground(writes: ["Makefile"]), _tree));

            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.Equal(
                $"'{file}' is the step's record of what its worktree held untracked as it began, and does not read as one: {why}. Nothing was changed: a record is never guessed at.",
                refused.Message);
            Assert.Equal(written, File.ReadAllText(file));
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The record of the step's start, as the reason step's runner takes it.</summary>
    private Task<AgentStepStart?> TakeAsync(AgentPolicyGround ground, string? worktree)
        => AgentStepStart.OfAsync(_harness.FileSystem, _harness.GitClient, ground, worktree, _step, _harness.Platform.PathComparison, Token);

    /// <summary>A step's ground over the tree: the tree and a work directory written where nothing else is said.</summary>
    private AgentPolicyGround Ground(IReadOnlyList<string>? writes, IReadOnlyList<string>? writable = null)
        => PolicyGrounds.Of(_tree, [], writable ?? [_tree, _temp.Combine("work")], [], [], [], writes: writes);
}
