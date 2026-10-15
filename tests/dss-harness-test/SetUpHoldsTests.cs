using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.SetUps;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// This machine's record of the legs holding a set-up in a tree: the first brings it up, the others join or wait, the
/// last brings it down, and a hold whose process ended is let go of.
/// </summary>
public sealed class SetUpHoldsTests
{
    /// <summary>
    /// Two legs give a set-up the same values only where they give the same inputs, each the same value - names ignoring
    /// case, values exactly: a value for an input the other gives none is another value, whichever gives it.
    /// </summary>
    [Fact]
    public void Values_AreTheSame_OnlyWhereTheSameInputsAreEachGivenTheSameValue()
    {
        var ci = new Dictionary<string, string> { ["profile"] = "ci" };
        var more = new Dictionary<string, string> { ["profile"] = "ci", ["port"] = "5432" };

        Assert.True(SetUpValues.Same(ci, new Dictionary<string, string> { ["PROFILE"] = "ci" }));
        Assert.False(SetUpValues.Same(ci, new Dictionary<string, string> { ["profile"] = "CI" }));
        Assert.False(SetUpValues.Same(ci, more));
        Assert.False(SetUpValues.Same(more, ci));
    }

    private const string Tree = "/work/tree";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The first leg to need a set-up brings it up and every later one joins it; letting go, only the last brings it
    /// down, and once it is gone nothing holds it.
    /// </summary>
    [Fact]
    public async Task TheFirstBringsItUp_TheOthersJoin_AndTheLastBringsItDown()
    {
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var holds = Holds(harness, state);

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token));
        holds.Stood("e2e", Tree, "run-1", "a");
        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("E2E", NoInputs, Tree, "run-1", "b", Never, Token));
        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("e2e", NoInputs, Tree, "run-2", "a", Never, Token));
        Assert.Equal(3, holds.HeldOn("e2e", Tree).Count);

        Assert.False(holds.Leave("e2e", Tree, "run-1", "a"), "two legs still use it");
        Assert.False(holds.Leave("e2e", Tree, "run-2", "a"), "one leg still uses it");
        Assert.True(holds.Leave("e2e", Tree, "run-1", "b"), "the last to let go brings it down");
        Assert.Equal(SetUpPhase.Falling, Assert.Single(holds.HeldOn("e2e", Tree)).Phase);

        holds.Gone("e2e", Tree, "run-1", "b");

        Assert.Empty(holds.HeldOn("e2e", Tree));
    }

    /// <summary>A set-up held in one tree is not held in another, and another set-up in the same tree is its own.</summary>
    [Fact]
    public async Task ASetUpIsHeldPerTree_AndPerName()
    {
        using var state = new TempDirectory();
        var holds = Holds(new HarnessFactory(), state);

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token));
        holds.Stood("e2e", Tree, "run-1", "a");

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree + "-copy", "run-1", "b", Never, Token));
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("db", NoInputs, Tree, "run-1", "c", Never, Token));
    }

    /// <summary>
    /// A set-up the machine shares is held across the repository's trees: a leg in another tree joins it, the first to come
    /// is the one to bring it up and the last to let go, in whichever tree, brings it down. A leg saying so meets a hold
    /// another tree took without saying so, and the other way round.
    /// </summary>
    [Fact]
    public async Task ASetUpTheMachineShares_IsHeldAcrossItsTrees()
    {
        using var state = new TempDirectory();
        var holds = Holds(new HarnessFactory(), state);
        var other = Tree + "-worktree";

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token, Machine));
        holds.Stood("e2e", Tree, "run-1", "a");

        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("e2e", NoInputs, other, "run-2", "b", Never, Token, Machine));
        Assert.Equal(["a", "b"], holds.HeldOn("e2e", other, Machine).Select(hold => hold.Leg));
        Assert.Equal(["a", "b"], holds.HeldOn("e2e", other, ByTree).Select(hold => hold.Leg));
        Assert.False(holds.Leave("e2e", Tree, "run-1", "a"), "a leg in another tree still uses it");

        holds.Gone("e2e", Tree, "run-1", "a");

        Assert.True(holds.Leave("e2e", other, "run-2", "b"), "the last to let go, in its own tree, brings it down");

        holds.Gone("e2e", other, "run-2", "b");

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("db", NoInputs, Tree, "run-3", "c", Never, Token));
        holds.Stood("db", Tree, "run-3", "c");
        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("db", NoInputs, other, "run-4", "d", Never, Token, Machine));
    }

    /// <summary>
    /// A leg whose hold is no longer in the record - removed by hand - lets go as one of its tree's: another leg there still
    /// using the set-up keeps it up.
    /// </summary>
    [Fact]
    public async Task ALegWhoseHoldIsGoneFromTheRecord_LetsGoAsOneOfItsTrees()
    {
        using var state = new TempDirectory();
        var holds = Holds(new HarnessFactory(), state);

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token));
        holds.Stood("e2e", Tree, "run-1", "a");
        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("e2e", NoInputs, Tree, "run-2", "b", Never, Token));
        holds.Gone("e2e", Tree, "run-1", "a");

        Assert.False(holds.Leave("e2e", Tree, "run-1", "a"), "a leg in its tree still uses it");
        Assert.True(holds.Leave("e2e", Tree, "run-2", "b"));
    }

    /// <summary>
    /// A set-up the machine shares is shared by the legs of one repository: another repository's of that name is another
    /// set-up, its lines its own, which a leg of this one neither joins nor waits for, nor counts as using its own.
    /// </summary>
    [Fact]
    public async Task ASetUpTheMachineShares_IsSharedWithinItsRepositoryAlone()
    {
        using var state = new TempDirectory();
        var holds = Holds(new HarnessFactory(), state);
        var elsewhere = new SetUpSharing("/work/another-repository", MachineWide: true);

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, "/work/another-repository", "run-1", "a", Never, Token, elsewhere));
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-2", "b", Never, Token, Machine));

        Assert.Equal(["b"], holds.HeldOn("e2e", Tree + "-worktree", Machine).Select(hold => hold.Leg));
        Assert.True(holds.Leave("e2e", Tree, "run-2", "b"), "no leg of this repository still uses it");
    }

    /// <summary>
    /// The last leg holding a set-up as it is up brings it down, whichever legs it shares with: one a tree shares, joined
    /// through a leg sharing it by the machine, keeps it up for that tree's leg that brought it up and is still there.
    /// </summary>
    [Fact]
    public async Task TheLastLegHoldingASetUpAsItIsUp_BringsItDown_WhicheverWayEachShares()
    {
        using var state = new TempDirectory();
        var holds = Holds(new HarnessFactory(), state);
        var second = Tree + "-second";
        var third = Tree + "-third";

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token, ByTree));
        holds.Stood("e2e", Tree, "run-1", "a");
        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("e2e", NoInputs, third, "run-2", "b", Never, Token, Machine));
        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("e2e", NoInputs, second, "run-3", "d", Never, Token, ByTree));

        Assert.False(holds.Leave("e2e", third, "run-2", "b"), "two legs still hold it");
        holds.Gone("e2e", third, "run-2", "b");
        Assert.False(holds.Leave("e2e", Tree, "run-1", "a"), "the leg in the second tree still holds it, though it shares it with no leg here");
        holds.Gone("e2e", Tree, "run-1", "a");
        Assert.True(holds.Leave("e2e", second, "run-3", "d"));
    }

    /// <summary>
    /// A leg sharing a set-up with legs that each brought it up apart - one tree's and another's, each sharing it by tree -
    /// waits, saying so once, until it meets one alone, then joins that one: joined to one, the other would be brought
    /// down by its own last leg under nothing, while this leg's work assumed the machine holds it once.
    /// </summary>
    [Fact]
    public async Task ALegMeetingASetUpUpApartInTwoTrees_WaitsUntilItMeetsOne_ThenJoinsIt()
    {
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var second = Tree + "-second";
        SetUpHolds? holds = null;
        var waits = 0;

        holds = Holds(harness, state, (_, _) =>
        {
            if (++waits == 2)
            {
                Assert.True(holds!.Leave("e2e", second, "run-2", "d"));
                holds.Gone("e2e", second, "run-2", "d");
            }

            return Task.CompletedTask;
        });

        var said = new List<string>();

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token, ByTree));
        holds.Stood("e2e", Tree, "run-1", "a");
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, second, "run-2", "d", Never, Token, ByTree));
        holds.Stood("e2e", second, "run-2", "d");

        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("e2e", NoInputs, Tree + "-third", "run-3", "b", said.Add, Token, Machine));

        Assert.Equal(2, waits);
        Assert.StartsWith("waiting for set-up 'e2e' to be let go of: it is up apart for leg 'a' (", Assert.Single(said), StringComparison.Ordinal);
        Assert.Contains($") in '{Tree}' and for leg 'd' (", said[0], StringComparison.Ordinal);
        Assert.EndsWith($") in '{second}', and this leg, sharing it with both, can join neither", said[0], StringComparison.Ordinal);
        Assert.False(holds.Leave("e2e", Tree, "run-1", "a"), "the leg that joined still holds it");
    }

    /// <summary>
    /// A leg finding the set-up coming up waits until it is up, saying once whom it waits for, then joins: joined half
    /// up, its work would run against what is not there yet.
    /// </summary>
    [Fact]
    public async Task ALegFindingItComingUp_WaitsUntilItIsUp_ThenJoins()
    {
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        SetUpHolds? holds = null;
        var waits = 0;

        holds = Holds(harness, state, (_, _) =>
        {
            if (++waits == 2)
            {
                holds!.Stood("e2e", Tree, "run-1", "a");
            }

            return Task.CompletedTask;
        });

        var said = new List<string>();

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token));
        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "b", said.Add, Token));

        Assert.Equal(2, waits);
        Assert.StartsWith("waiting for set-up 'e2e' to be up: leg 'a' (", Assert.Single(said), StringComparison.Ordinal);
        Assert.EndsWith(") is bringing it up", said[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg finding the set-up going down waits until it is down, then brings it up again itself: joined as it goes,
    /// its work would run against what is about to be gone.
    /// </summary>
    [Fact]
    public async Task ALegFindingItGoingDown_WaitsUntilItIsDown_ThenBringsItUp()
    {
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        SetUpHolds? holds = null;

        holds = Holds(harness, state, (_, _) =>
        {
            holds!.Gone("e2e", Tree, "run-1", "a");
            return Task.CompletedTask;
        });

        var said = new List<string>();

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token));
        holds.Stood("e2e", Tree, "run-1", "a");
        Assert.True(holds.Leave("e2e", Tree, "run-1", "a"));

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-2", "b", said.Add, Token));
        Assert.EndsWith(") is bringing it down", Assert.Single(said), StringComparison.Ordinal);
        Assert.StartsWith("waiting for set-up 'e2e' to be down", said[0], StringComparison.Ordinal);
    }

    /// <summary>A leg stopped while it waits holds nothing: the wait ends, and the record is as it found it.</summary>
    [Fact]
    public async Task ALegStoppedWhileItWaits_HoldsNothing()
    {
        using var state = new TempDirectory();
        using var stop = new CancellationTokenSource();
        var holds = Holds(new HarnessFactory(), state, (_, _) =>
        {
            stop.Cancel();
            return Task.CompletedTask;
        });

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holds.TakeAsync("e2e", NoInputs, Tree, "run-1", "b", _ => { }, stop.Token));

        Assert.Equal("a", Assert.Single(holds.HeldOn("e2e", Tree)).Leg);
    }

    /// <summary>
    /// A hold whose process has ended is no hold: it is let go of when the record is next read, said so, so a run that
    /// was killed holds no set-up forever - and the next leg brings the set-up up itself, never joining what nobody
    /// stood up.
    /// </summary>
    [Fact]
    public async Task AHoldWhoseProcessEnded_IsLetGo_AndTheNextLegBringsItUp()
    {
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        var ended = Substitute.For<IProcessIdentity>();

        ended.CurrentMachine.Returns(Environment.MachineName);
        ended.CurrentId.Returns(int.MaxValue - 1);
        ended.Current.Returns("a-process-that-has-gone");

        var killed = new SetUpHolds(harness.FileSystem, harness.Output, ended, harness.Platform, () => state.Combine("setups.json"));
        var holds = Holds(harness, state);

        Assert.Equal(SetUpTurn.BringUp, await killed.TakeAsync("e2e", NoInputs, Tree, "run-1", "a", Never, Token));
        killed.Stood("e2e", Tree, "run-1", "a");

        Assert.Empty(holds.HeldOn("e2e", Tree));
        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", NoInputs, Tree, "run-2", "b", Never, Token));
        Assert.Contains("Reclaimed set-up 'e2e' in '", harness.StandardOutput.ToString() + harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Equal("b", Assert.Single(holds.HeldOn("e2e", Tree)).Leg);
    }

    /// <summary>
    /// A leg giving a set-up's inputs other values than the ones it is up with waits until every leg using it lets it go,
    /// saying both sets of values, then brings it up with its own: joined, its work would run against what other values
    /// brought up. A leg giving the same values joins.
    /// </summary>
    [Fact]
    public async Task ALegGivingOtherValues_WaitsUntilItIsLetGo_ThenBringsItUpWithItsOwn()
    {
        using var state = new TempDirectory();
        var harness = new HarnessFactory();
        SetUpHolds? holds = null;
        var ci = new Dictionary<string, string> { ["profile"] = "ci" };
        var dev = new Dictionary<string, string> { ["PROFILE"] = "dev" };

        holds = Holds(harness, state, (_, _) =>
        {
            Assert.True(holds!.Leave("e2e", Tree, "run-1", "a"));
            holds.Gone("e2e", Tree, "run-1", "a");
            return Task.CompletedTask;
        });

        var said = new List<string>();

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", ci, Tree, "run-1", "a", Never, Token));
        holds.Stood("e2e", Tree, "run-1", "a");
        Assert.Equal(SetUpTurn.Joined, await holds.TakeAsync("e2e", new Dictionary<string, string> { ["Profile"] = "ci" }, Tree, "run-1", "same", Never, Token));
        Assert.False(holds.Leave("e2e", Tree, "run-1", "same"));

        Assert.Equal(SetUpTurn.BringUp, await holds.TakeAsync("e2e", dev, Tree, "run-2", "b", said.Add, Token));

        Assert.StartsWith("waiting for set-up 'e2e' to be let go of: it is up (profile=ci) for leg 'a' (", Assert.Single(said), StringComparison.Ordinal);
        Assert.EndsWith("), and this leg gives it (PROFILE=dev)", said[0], StringComparison.Ordinal);
        Assert.Equal("dev", Assert.Single(holds.HeldOn("e2e", Tree)).Values["profile"]);
    }

    private static void Never(string message) => Assert.Fail($"no leg was to wait, and one said: {message}");

    private static IReadOnlyDictionary<string, string> NoInputs { get; } = new Dictionary<string, string>();

    /// <summary>The repository's legs sharing a set-up by the machine: <see cref="Tree"/> is its main checkout.</summary>
    private static SetUpSharing Machine { get; } = new(Tree, MachineWide: true);

    /// <summary>The repository's legs sharing a set-up by tree.</summary>
    private static SetUpSharing ByTree { get; } = new(Tree, MachineWide: false);

    private static SetUpHolds Holds(HarnessFactory harness, TempDirectory state, Func<TimeSpan, CancellationToken, Task>? wait = null)
        => new(harness.FileSystem, harness.Output, harness.Identity, harness.Platform, () => state.Combine("setups.json"), wait: wait);
}
