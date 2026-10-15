using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.SetUps;

/// <summary>Where a leg's hold on a set-up stands.</summary>
public enum SetUpPhase
{
    /// <summary>The leg is bringing the set-up up; every other leg of that set-up and tree waits for it.</summary>
    Rising,

    /// <summary>The set-up is up, and the leg is using it.</summary>
    Standing,

    /// <summary>The leg was the last to use it and is bringing it down; a leg asking for it waits until it is down.</summary>
    Falling,
}

/// <summary>A leg's hold on a set-up in one tree of this machine.</summary>
/// <param name="SetUp">The set-up, as declared.</param>
/// <param name="Tree">The tree its lines run in.</param>
/// <param name="Repository">The repository the tree is of, as <see cref="SetUpSharing.Repository"/> spells it.</param>
/// <param name="Instance">
/// The set-up as it is up: one bringing up gives it a name its own, and every leg joining it holds that name, so the last
/// of them - and only the last - brings it down, wherever each of them works.
/// </param>
/// <param name="ProcessId">The process running the leg, which holds it until it lets go or ends.</param>
/// <param name="RunId">The run the leg is part of.</param>
/// <param name="Leg">The leg.</param>
/// <param name="TakenUtc">When it took the hold, for display.</param>
/// <param name="Phase">Where the hold stands.</param>
/// <param name="ProcessStamp">What tells that process from a later holder of its id; see <see cref="IProcessIdentity"/>.</param>
/// <param name="Inputs">The value each of the set-up's inputs was brought up with; a leg giving others never joins it.</param>
/// <param name="MachineWide">
/// Whether every leg on the machine shares the set-up, whatever tree it works in, rather than the legs in
/// <paramref name="Tree"/> alone (<see cref="SetUpConfig.Shared"/>).
/// </param>
public sealed record SetUpHold(
    string SetUp,
    string Tree,
    string Repository,
    string Instance,
    int ProcessId,
    string RunId,
    string Leg,
    DateTimeOffset TakenUtc,
    SetUpPhase Phase,
    string? ProcessStamp = null,
    IReadOnlyDictionary<string, string>? Inputs = null,
    bool MachineWide = false)
{
    /// <summary>The hold as a line names who holds it: the leg, then the process.</summary>
    public string Describe() => $"leg '{Leg}' ({ProcessHolders.Describe(ProcessId, RunId, TakenUtc)})";

    /// <summary>The values the set-up was brought up with, by input matched ignoring case; none where it has no inputs.</summary>
    public IReadOnlyDictionary<string, string> Values => new Dictionary<string, string>(Inputs ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Which legs share a set-up with a leg: the ones in its tree, or, where the machine shares it, every leg of its repository on the machine.</summary>
/// <param name="Repository">
/// The repository the leg's tree is of, as its main checkout is spelt on this machine (<see cref="IHarnessContextLoader.RepositoryOfAsync"/>):
/// another repository's set-up of that name is another set-up, its lines its own.
/// </param>
/// <param name="MachineWide">Whether every leg of the repository on the machine shares it (<see cref="SetUpConfig.MachineWide"/>).</param>
public sealed record SetUpSharing(string Repository, bool MachineWide);

/// <summary>What taking a hold left the leg to do.</summary>
public enum SetUpTurn
{
    /// <summary>Nothing held it up: the leg brings it up, and marks it standing once it is.</summary>
    BringUp,

    /// <summary>Another leg brought it up and still uses it: the leg joins.</summary>
    Joined,
}

/// <summary>
/// This machine's record of which legs hold which set-up in which tree, so legs sharing one - in one run, in two runs,
/// in two processes - bring it up once and down once: the first to need it brings it up, every other joins, and the
/// last to let go brings it down. Legs share a set-up in one tree, or, where it is shared by the machine
/// (<see cref="SetUpConfig.MachineWide"/>), in any tree of the repository on the machine.
/// </summary>
/// <remarks>
/// <para>
/// One file per machine, kept where this user's own state is kept (<see cref="UserState"/>), and changed under the
/// machine-wide lock every such record is (<see cref="MachineWideFile"/>). A hold is the process's that took it: one
/// whose process has ended is let go of when the record is next read, and said so, so a run that was killed holds no
/// set-up forever - and a set-up it left up is brought up again by the next leg, which is why a set-up's lines must
/// be safe to run twice.
/// </para>
/// <para>
/// A leg that finds the set-up being brought up or down waits for that to end rather than joining: joining a set-up
/// half up runs its work against services that are not there yet, and joining one coming down runs it against
/// services about to go. So does a leg giving its inputs other values than the ones it is up with, until the legs using
/// it let it go: its work would run against what other values brought up. The leg bringing it up or down runs its line
/// outside the lock, so the wait is for that line, or for those legs.
/// </para>
/// <para>
/// Each set-up as it is up is an instance of it (<see cref="SetUpHold.Instance"/>), and letting go counts the legs holding
/// that instance, not the legs one leg shares with: a configuration changed between two runs may have one tree's legs
/// share a set-up by tree and another's by the machine, and a leg meeting two instances up apart - each brought up by a
/// tree for itself - waits until it meets one, rather than joining one and leaving the other to come down under it.
/// </para>
/// </remarks>
public sealed class SetUpHolds
{
    /// <summary>How long a waiting leg waits before asking again.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    private const string Subject = "This machine's record of the set-ups legs hold";
    private const string Consequence = "Until it can be, no leg here can bring a set-up up or down beside another leg's.";
    private const string Unreadable = "Remove it once no leg with a set-up runs on this machine.";

    private readonly IFileSystem _fileSystem;
    private readonly IHarnessOutput _output;
    private readonly IProcessIdentity _identity;
    private readonly IHostPlatform _platform;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly Lazy<MachineWideList<SetUpHold>> _record;

    /// <summary>The record kept at <paramref name="path"/>.</summary>
    /// <param name="fileSystem">Where the record is kept.</param>
    /// <param name="output">Where a hold let go of for a process that ended is said.</param>
    /// <param name="identity">This process, and how the liveness of another is told.</param>
    /// <param name="platform">Compares tree paths as this machine does.</param>
    /// <param name="path">The record's file, named when it is first needed.</param>
    /// <param name="clock">When a hold was taken; the system's, unless a test needs another.</param>
    /// <param name="wait">How a waiting leg waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, unless a test needs another.</param>
    public SetUpHolds(
        IFileSystem fileSystem,
        IHarnessOutput output,
        IProcessIdentity identity,
        IHostPlatform platform,
        Func<string> path,
        TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        _fileSystem = fileSystem;
        _output = output;
        _identity = identity;
        _platform = platform;
        _clock = clock ?? TimeProvider.System;
        _wait = wait ?? Task.Delay;
        _record = new(() =>
        {
            string named;

            try
            {
                named = path();
            }
            catch (DirectoryNotFoundException ex)
            {
                throw new HarnessException(HarnessExit.Refused, $"{Subject} has nowhere to be kept: {ex.Message.TrimEnd('.')}. {Consequence}", ex);
            }

            return new MachineWideList<SetUpHold>(_fileSystem, named, Subject, Consequence, Unreadable);
        });
    }

    /// <summary>What the record's lines are said under, the reclaiming of a hold whose process ended among them.</summary>
    public const string CommandName = "set-up";

    /// <summary>
    /// The record of <paramref name="platform"/>'s machine, for the user running this process. Where the machine can be
    /// told only by its name, that is said, with why.
    /// </summary>
    /// <param name="platform">Tells this machine from every other.</param>
    /// <param name="output">Says the record is named by the machine's name, where it is.</param>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string PathFor(IHostPlatform platform, IHarnessOutput output)
        => UserState.MachineFile(platform, output, CommandName, "setups", "set-ups' holds", "brings up its legs' set-ups beside the ones held under the old");

    /// <summary>The record, as a refusal names it.</summary>
    public string Location => _record.Value.Path;

    /// <summary>
    /// Takes <paramref name="leg"/>'s hold on <paramref name="setUp"/> in <paramref name="tree"/> with
    /// <paramref name="inputs"/>, waiting while another leg brings it up or down, or uses it with other values.
    /// </summary>
    /// <param name="setUp">The set-up, as declared.</param>
    /// <param name="inputs">The value each of its inputs is to be brought up with.</param>
    /// <param name="tree">The tree its lines run in.</param>
    /// <param name="runId">The run the leg is part of.</param>
    /// <param name="leg">The leg.</param>
    /// <param name="waiting">Told once, with who it waits for, when the leg starts waiting.</param>
    /// <param name="cancellationToken">Stops the wait, leaving nothing held.</param>
    /// <param name="sharing">Which legs share it with this one; the legs in <paramref name="tree"/> alone, where none is given.</param>
    /// <returns>Whether the leg brings the set-up up, or joins it.</returns>
    public async Task<SetUpTurn> TakeAsync(
        string setUp,
        IReadOnlyDictionary<string, string> inputs,
        string tree,
        string runId,
        string leg,
        Action<string> waiting,
        CancellationToken cancellationToken = default,
        SetUpSharing? sharing = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(waiting);

        var scope = sharing ?? new SetUpSharing(tree, MachineWide: false);
        var said = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (turn, busy) = _record.Value.Update((standing, afterwards) =>
            {
                var live = Live(standing, afterwards);
                var mine = live.Where(hold => Shares(hold, setUp, tree, scope)).ToList();

                if (Busy(mine, setUp, inputs, tree) is { } why)
                {
                    return ((IReadOnlyList<SetUpHold>?)(live.Count == standing.Count ? null : live), ((SetUpTurn?)null, why));
                }

                var phase = mine.Count == 0 ? SetUpPhase.Rising : SetUpPhase.Standing;
                var instance = mine.Count == 0 ? Guid.NewGuid().ToString("N") : mine[0].Instance;
                var taken = new SetUpHold(
                    setUp, tree, scope.Repository, instance, _identity.CurrentId, runId, leg, _clock.GetUtcNow(), phase, _identity.Current, inputs, scope.MachineWide);

                return ([.. live, taken], (phase == SetUpPhase.Rising ? SetUpTurn.BringUp : SetUpTurn.Joined, (string?)null));
            });

            if (turn is { } taken)
            {
                return taken;
            }

            if (!said)
            {
                said = true;
                waiting(busy!);
            }

            await _wait(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What a leg giving <paramref name="inputs"/> waits for among the holds it shares <paramref name="setUp"/> with, or
    /// <see langword="null"/> where it can bring it up or join it: one bringing it up or down, two instances up apart, or one
    /// up with other values.
    /// </summary>
    private string? Busy(IReadOnlyList<SetUpHold> mine, string setUp, IReadOnlyDictionary<string, string> inputs, string tree)
    {
        if (mine.FirstOrDefault(hold => hold.Phase != SetUpPhase.Standing) is { } moving)
        {
            return moving.Phase == SetUpPhase.Rising
                ? $"waiting for set-up '{setUp}' to be up: {Said(moving, tree)} is bringing it up"
                : $"waiting for set-up '{setUp}' to be down: {Said(moving, tree)} is bringing it down";
        }

        if (mine.DistinctBy(hold => hold.Instance, StringComparer.Ordinal).ToList() is { Count: > 1 } apart)
        {
            return $"waiting for set-up '{setUp}' to be let go of: it is up apart for {Said(apart[0], tree)} and for {Said(apart[1], tree)}, "
                + "and this leg, sharing it with both, can join neither";
        }

        return mine.FirstOrDefault(hold => !SetUpValues.Same(hold.Values, inputs)) is { } other
            ? $"waiting for set-up '{setUp}' to be let go of: it is up{SetUpValues.Said(other.Values)} for {Said(other, tree)}, "
                + $"and this leg gives it{SetUpValues.Said(inputs)}"
            : null;
    }

    /// <summary>Marks <paramref name="leg"/>'s hold standing, once it brought the set-up up.</summary>
    /// <param name="setUp">The set-up.</param>
    /// <param name="tree">Its tree.</param>
    /// <param name="runId">The leg's run.</param>
    /// <param name="leg">The leg.</param>
    public void Stood(string setUp, string tree, string runId, string leg)
        => _record.Value.Change((standing, _) => [.. standing.Select(hold => Mine(hold, setUp, tree, runId, leg) ? hold with { Phase = SetUpPhase.Standing } : hold)]);

    /// <summary>
    /// Lets go of <paramref name="leg"/>'s hold, or, where it is the last live one of the instance it holds, marks it
    /// falling for the leg to bring the set-up down.
    /// </summary>
    /// <param name="setUp">The set-up.</param>
    /// <param name="tree">Its tree.</param>
    /// <param name="runId">The leg's run.</param>
    /// <param name="leg">The leg.</param>
    /// <returns>Whether the leg brings the set-up down: no other live leg holds the instance it holds.</returns>
    /// <remarks>A hold no longer in the record - removed by hand - is let go of as one of its tree's.</remarks>
    public bool Leave(string setUp, string tree, string runId, string leg)
        => _record.Value.Update((standing, afterwards) =>
        {
            var live = Live(standing, afterwards);
            var own = live.FirstOrDefault(hold => Mine(hold, setUp, tree, runId, leg));
            var others = live.Count(hold => !Mine(hold, setUp, tree, runId, leg)
                && (own is null ? Same(hold, setUp, tree) : Same(hold, setUp) && string.Equals(hold.Instance, own.Instance, StringComparison.Ordinal)));

            return others > 0
                ? ((IReadOnlyList<SetUpHold>?)[.. live.Where(hold => !Mine(hold, setUp, tree, runId, leg))], false)
                : ([.. live.Select(hold => Mine(hold, setUp, tree, runId, leg) ? hold with { Phase = SetUpPhase.Falling } : hold)], true);
        });

    /// <summary>
    /// Removes <paramref name="leg"/>'s hold, once it brought the set-up down or gave up bringing it up. Where the record
    /// cannot be changed just then, that is said and nothing is thrown: the hold leaves it with this process's next
    /// change, or with this process, as a dead holder's does.
    /// </summary>
    /// <param name="setUp">The set-up.</param>
    /// <param name="tree">Its tree.</param>
    /// <param name="runId">The leg's run.</param>
    /// <param name="leg">The leg.</param>
    public void Gone(string setUp, string tree, string runId, string leg)
        => _record.Value.GiveBack(
            hold => Mine(hold, setUp, tree, runId, leg),
            ex => _output.Warn(CommandName, $"leg '{leg}' could not let go of set-up '{setUp}' in '{_output.Shown(tree)}': {ex.Message} It is let go of as this process ends."));

    /// <summary>
    /// The live holds on <paramref name="setUp"/> in <paramref name="tree"/> - or, where the machine shares it, in any
    /// tree of the repository on the machine: legs still using it.
    /// </summary>
    /// <param name="setUp">The set-up.</param>
    /// <param name="tree">Its tree.</param>
    /// <param name="sharing">Which legs share it with a leg in <paramref name="tree"/>; the legs in it alone, where none is given.</param>
    public IReadOnlyList<SetUpHold> HeldOn(string setUp, string tree, SetUpSharing? sharing = null)
        => [.. _record.Value.Read().Where(hold => Shares(hold, setUp, tree, sharing ?? new SetUpSharing(tree, MachineWide: false))
            && _identity.IsAlive(hold.ProcessId, hold.ProcessStamp))];

    /// <summary>The hold as a line names who holds it, and in which tree where that is not <paramref name="tree"/>.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="tree">The tree the line is about.</param>
    public string Said(SetUpHold hold, string tree)
    {
        ArgumentNullException.ThrowIfNull(hold);

        return _platform.PathComparer().Equals(hold.Tree, tree) ? hold.Describe() : $"{hold.Describe()} in '{_output.Shown(hold.Tree)}'";
    }

    private List<SetUpHold> Live(IReadOnlyList<SetUpHold> holds, Action<Action> afterwards)
    {
        var live = new List<SetUpHold>();

        foreach (var hold in holds)
        {
            if (_identity.IsAlive(hold.ProcessId, hold.ProcessStamp))
            {
                live.Add(hold);
                continue;
            }

            afterwards(() => _output.Info(CommandName, ProcessHolders.Reclaimed($"set-up '{hold.SetUp}' in '{_output.Shown(hold.Tree)}'", hold.Describe())));
        }

        return live;
    }

    private bool Same(SetUpHold hold, string setUp, string tree) => Same(hold, setUp) && _platform.PathComparer().Equals(hold.Tree, tree);

    private static bool Same(SetUpHold hold, string setUp) => string.Equals(hold.SetUp, setUp, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="hold"/> shares <paramref name="setUp"/> with a leg in <paramref name="tree"/>: in that tree,
    /// or in any of the repository's where either says the machine shares it - so a leg that says so meets every leg of
    /// the repository using it on the machine, whichever way theirs said, and none of them brings it up a second time.
    /// </summary>
    private bool Shares(SetUpHold hold, string setUp, string tree, SetUpSharing sharing)
        => Same(hold, setUp)
            && (_platform.PathComparer().Equals(hold.Tree, tree)
                || ((sharing.MachineWide || hold.MachineWide) && _platform.PathComparer().Equals(hold.Repository, sharing.Repository)));

    private bool Mine(SetUpHold hold, string setUp, string tree, string runId, string leg)
        => Same(hold, setUp, tree)
            && hold.ProcessId == _identity.CurrentId
            && string.Equals(hold.RunId, runId, StringComparison.Ordinal)
            && string.Equals(hold.Leg, leg, StringComparison.Ordinal);
}
