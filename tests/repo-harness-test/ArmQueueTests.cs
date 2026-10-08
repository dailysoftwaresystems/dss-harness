using RepoHarness.Core.Execution;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// The arms of a sweep dealt to its workers: each once, in order, to whichever worker is free; an arm a retired worker
/// held dealt again first; a worker waiting while an arm that may come back is held; and every arm no worker drove
/// stopped, saying why.
/// </summary>
public sealed class ArmQueueTests
{
    /// <summary>How long a test waits for a worker the queue should have answered, before it fails rather than hangs.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Arms are dealt in their order, each once, to whichever worker asks; once all are finished, none is left for anyone.</summary>
    [Fact]
    public async Task Arms_AreDealtInOrder_EachOnce()
    {
        var queue = new ArmQueue([Arm("a"), Arm("b"), Arm("c")], 2);

        var first = await Take(queue, 1);
        var second = await Take(queue, 2);
        queue.Done(2);
        var third = await Take(queue, 2);
        queue.Done(1);
        queue.Done(2);

        Assert.Equal(["a", "b", "c"], new[] { first, second, third }.Select(arm => arm?.Id));
        Assert.Null(await Take(queue, 1));
        Assert.Null(await Take(queue, 2));
        Assert.Empty(queue.Undriven(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A retired worker takes no arm again; the arm it held goes to the front of the queue where it reached no verdict,
    /// and is finished where it did.
    /// </summary>
    [Fact]
    public async Task ARetiredWorker_TakesNoArm_AndItsArmIsDealtAgainFirst()
    {
        var queue = new ArmQueue([Arm("a"), Arm("b"), Arm("c")], 3);

        await Take(queue, 1);
        await Take(queue, 2);
        queue.Retire(1, "its copy could not be made", requeue: true);
        queue.Retire(2, "'src/fixture.cpp' could not be put back as it was", requeue: false);

        Assert.Null(await Take(queue, 1));
        Assert.Equal("a", (await Take(queue, 3))?.Id);
        queue.Done(3);
        Assert.Equal("c", (await Take(queue, 3))?.Id);
        queue.Done(3);
        Assert.Null(await Take(queue, 3));
        Assert.Empty(queue.Undriven(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A worker finding the queue empty while another holds an arm waits: it takes that arm where its worker is retired
    /// and the arm comes back, and finds nothing left once the arm is finished.
    /// </summary>
    [Fact]
    public async Task AWorkerFindingTheQueueEmpty_WaitsForTheArmAnotherHolds()
    {
        var queue = new ArmQueue([Arm("a"), Arm("b")], 2);

        await Take(queue, 1);
        await Take(queue, 2);
        queue.Done(2);

        var waiting = Take(queue, 2);

        Assert.False(waiting.IsCompleted);

        queue.Retire(1, "its copy could not be made", requeue: true);

        Assert.Equal("a", (await waiting)?.Id);

        var finished = new ArmQueue([Arm("a")], 2);

        await Take(finished, 1);

        var idle = Take(finished, 2);

        Assert.False(idle.IsCompleted);

        finished.Done(1);

        Assert.Null(await idle);
    }

    /// <summary>With every worker retired, each arm left is stopped, saying why each worker was.</summary>
    [Fact]
    public async Task EveryWorkerRetired_StopsTheArmsLeft_SayingWhy()
    {
        var queue = new ArmQueue([Arm("a"), Arm("b"), Arm("c")], 2);

        await Take(queue, 2);
        queue.Retire(2, "'src/fixture.cpp' could not be put back as it was", requeue: false);
        queue.Retire(1, "its copy could not be made", requeue: false);

        var why = ReachedVerdict.Of(
            LegVerdict.Stopped,
            "no worker was left to drive it: worker 1 was retired, its copy could not be made; "
            + "worker 2 was retired, 'src/fixture.cpp' could not be put back as it was");

        Assert.Equal([("b", why), ("c", why)], queue.Undriven(TestContext.Current.CancellationToken).Select(arm => (arm.Arm.Id, arm.Verdict)));
    }

    /// <summary>
    /// A stopped sweep deals no arm, to a worker asking or one waiting, and each arm left is stopped saying the sweep
    /// was; an arm left with nothing to explain it is a defect, never passed over.
    /// </summary>
    [Fact]
    public async Task AStoppedSweep_DealsNothing_AndStopsTheArmsLeft()
    {
        using var stop = new CancellationTokenSource();
        var queue = new ArmQueue([Arm("a"), Arm("b"), Arm("c")], 2);
        var waiting = new ArmQueue([Arm("x")], 2);

        await Take(queue, 1, stop.Token);
        await Take(waiting, 1, stop.Token);

        var idle = Take(waiting, 2, stop.Token);

        await stop.CancelAsync();
        queue.Done(1);

        var stopped = ReachedVerdict.Of(LegVerdict.Stopped, "the sweep was stopped before a worker drove it");

        Assert.Null(await Take(queue, 2, stop.Token));
        Assert.Null(await idle);
        Assert.Equal([("b", stopped), ("c", stopped)], queue.Undriven(stop.Token).Select(arm => (arm.Arm.Id, arm.Verdict)));
        Assert.Equal(
            LegVerdict.Poisoned,
            Assert.Single(new ArmQueue([Arm("a")], 1).Undriven(CancellationToken.None)).Verdict.Verdict);
    }

    /// <summary>
    /// An arm a worker gives back, having taken it and not driven it, goes to the front of the queue and its worker is
    /// not retired: it is dealt again first, to that worker or another, and where the sweep was stopped it is stopped
    /// with the arms no worker drove.
    /// </summary>
    [Fact]
    public async Task AnArmGivenBack_IsDealtAgainFirst_OrStoppedWithTheArmsNoWorkerDrove()
    {
        using var stop = new CancellationTokenSource();
        var queue = new ArmQueue([Arm("a"), Arm("b"), Arm("c")], 2);

        await Take(queue, 1, stop.Token);
        await Take(queue, 2, stop.Token);
        queue.GiveBack(2);
        queue.GiveBack(1);

        Assert.Equal("a", (await Take(queue, 2, stop.Token))?.Id);
        Assert.Equal("b", (await Take(queue, 1, stop.Token))?.Id);
        queue.Done(1);
        queue.GiveBack(2);
        await stop.CancelAsync();

        var stopped = ReachedVerdict.Of(LegVerdict.Stopped, "the sweep was stopped before a worker drove it");

        Assert.Equal([("a", stopped), ("c", stopped)], queue.Undriven(stop.Token).Select(arm => (arm.Arm.Id, arm.Verdict)));

        // A worker waiting for the arm another holds is dealt it once it is given back.
        var one = new ArmQueue([Arm("x")], 2);

        await Take(one, 1);

        var idle = Take(one, 2);

        one.GiveBack(1);

        Assert.Equal("x", (await idle)?.Id);
    }

    /// <summary>
    /// A worker asking while it holds an arm, finishing or giving back one it does not hold, retired twice, or not one of
    /// the queue's, is refused; and the arms left are never asked for while a worker holds one.
    /// </summary>
    [Fact]
    public async Task WhatNoWorkerDoes_IsRefused()
    {
        var queue = new ArmQueue([Arm("a"), Arm("b")], 2);

        await Take(queue, 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Take(queue, 1));
        Assert.Throws<InvalidOperationException>(() => queue.Done(2));
        Assert.Throws<InvalidOperationException>(() => queue.GiveBack(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.GiveBack(3));
        Assert.Throws<InvalidOperationException>(() => queue.Undriven(CancellationToken.None));
        queue.Retire(2, "its copy could not be made", requeue: true);
        Assert.Throws<InvalidOperationException>(() => queue.Retire(2, "again", requeue: true));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Take(queue, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Take(queue, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArmQueue([], 0));
    }

    /// <summary>What <paramref name="worker"/> is dealt, failing rather than hanging where the queue never answers.</summary>
    private static Task<MutationArm?> Take(ArmQueue queue, int worker, CancellationToken? stop = null)
        => queue.TakeAsync(worker, stop ?? TestContext.Current.CancellationToken).WaitAsync(Patience, TestContext.Current.CancellationToken);

    private static MutationArm Arm(string id) => new()
    {
        Id = id,
        Line = 1,
        Own = new MutationSite("src/fixture.cpp", $"texts/{id}.before", $"texts/{id}.after", 1),
        Kind = RedKind.TestRed,
        Target = "fixture",
        Runner = "fixture_tests",
        Cases = 1,
        Diagnostic = $"texts/{id}.diag",
        Why = "a guard",
        Reds = ["Fixture.Case"],
    };
}
